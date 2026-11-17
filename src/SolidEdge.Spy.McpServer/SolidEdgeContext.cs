using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SolidEdgeFramework;
using SolidEdge.Spy.InteropServices;

namespace SolidEdge.Spy.McpServer
{
    /// <summary>
    /// Solid Edge 连接的公共服务。
    /// - 单例,整个 MCP server 共享一个 SE 连接。
    /// - 处理 SE 关闭/重启时的自动重连。
    /// - 维护"选中对象句柄表"(se_get_selection 取到的对象按编号缓存,
    ///   供后续 se_find_paths / se_describe_object 复用,避免用户改了选择后失配)。
    /// - 所有 Solid Edge COM 调用都被封送到同一个专用 STA 线程上执行。
    ///   因为 Solid Edge 是 STA(单线程单元)COM 对象,从 MTA(线程池)线程直接
    ///   访问会失败/不稳定。MCP 工具由 AI 客户端发请求触发,运行在 MTA 线程,
    ///   必须经由 Invoke() 转到 STA 线程后才能安全调用 SE 对象。
    /// </summary>
    public sealed class SolidEdgeContext : IDisposable
    {
        private Application _application;
        private readonly object _connectLock = new object();

        // 专用 STA 线程:承载所有 SE COM 调用。
        // 用 BlockingCollection 作为工作队列,在该线程上顺序消费。
        private Thread _staThread;
        private readonly BlockingCollection<Action> _staQueue = new BlockingCollection<Action>();
        private int _staStarted = 0;

        // 句柄表:obj-1 / obj-2 ... → 用户上次选中的对象
        private readonly ConcurrentDictionary<string, ObjectHandle> _handles = new ConcurrentDictionary<string, ObjectHandle>();
        private int _handleCounter = 0;

        private readonly ILogger<SolidEdgeContext> _logger;

        public SolidEdgeContext(ILogger<SolidEdgeContext> logger = null)
        {
            _logger = logger;
            StartStaThread();
        }

        /// <summary>
        /// 把一段需要访问 Solid Edge COM 的代码封送到专用 STA 线程执行,同步等待结果。
        /// 工具方法应把 COM 相关逻辑整体包进这里。
        ///
        /// 等待策略(2026-09-11 增强,专治"SE 弹报警框 → COM 被堵 → AI 干等到超时卡死"):
        /// 不再一次性 Wait(整个超时),而是分片等待,等待期间用**进程外**窗口探针
        /// (WindowProbe,纯 Win32 枚举,不碰 COM)盯住 Solid Edge:
        ///   ① 调用开始前 SE 就挂着模态对话框 → 等待上限压到 DialogBlockedTimeoutSeconds,
        ///      到期直接报"被对话框阻塞"并给出框的标题/按钮(不再陪跑满超时);
        ///   ② 等待期间**新冒出**模态对话框 → 宽限 DialogGraceMs 后立即中止等待并报告;
        ///   ③ 全程没有对话框 → 到超时仍按原来的 TimeoutException 报(更可能是重算/加载)。
        /// 注意:控制权在 SE 那边的 COM 调用上,这里只能"放弃等待"并如实说明状态未知。
        ///
        /// 设计裁定(2026-09-11,用户):**这是默认机制,不另设"弹窗检测"工具**——
        /// 任何工具的调用被堵都自动拿到同样的框信息(带标题/按钮/正文/处置建议);
        /// AI 想主动探一眼时,调任意现有只读工具即可兼任探针(被堵→得到框信息,
        /// 没堵→得到数据,不浪费调用),无需模型记得去找某个专用工具。
        /// 只有"关掉某个框"这种显式动作才走 CLI:`solidedge-mcp --dialogs [--close <hwnd> --confirm]`。
        /// </summary>
        public T Invoke<T>(Func<T> action)
        {
            if (Thread.CurrentThread == _staThread)
            {
                // 已经在 STA 线程上,直接执行(CLI 模式/STA 入口可能已是 STA)
                return action();
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _staQueue.Add(() =>
            {
                try
                {
                    tcs.SetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return WaitForResult(tcs);
        }

        /// <summary>分片等待 + 弹窗探针:对话框一出现就尽早返回,不让调用方干等满超时。</summary>
        private T WaitForResult<T>(TaskCompletionSource<T> tcs)
        {
            int timeoutMs = Math.Max(5, InvokeTimeoutSeconds) * 1000;

            WindowSnapshot baseline = null;
            if (DialogProbeEnabled)
            {
                try { baseline = WindowProbe.Snapshot(); }
                catch { baseline = null; }
            }

            DialogWindow blockerAtStart = (baseline != null && baseline.HasModalDialog)
                ? baseline.Dialogs.First(d => d.Modal)
                : null;
            int effectiveTimeoutMs = (blockerAtStart != null)
                ? Math.Min(timeoutMs, Math.Max(1000, DialogBlockedTimeoutSeconds * 1000))
                : timeoutMs;

            var watch = Stopwatch.StartNew();
            while (true)
            {
                int remainMs = effectiveTimeoutMs - (int)watch.ElapsedMilliseconds;
                if (remainMs <= 0)
                {
                    break;
                }
                if (tcs.Task.Wait(Math.Min(DialogPollMs, remainMs)))
                {
                    _connectionSuspicious = false;
                    return tcs.Task.GetAwaiter().GetResult();
                }

                // 还没返回:看看是不是被新冒出来的对话框堵住了
                if (baseline == null || watch.ElapsedMilliseconds < DialogGraceMs)
                {
                    continue;
                }
                WindowSnapshot now = null;
                try { now = WindowProbe.Snapshot(); }
                catch { continue; }

                DialogWindow fresh = FirstNewModal(baseline, now);
                if (fresh != null)
                {
                    _connectionSuspicious = true;
                    throw new SolidEdgeDialogBlockedException(
                        DialogBlockedMessage(fresh, now, watch.ElapsedMilliseconds));
                }
            }

            _connectionSuspicious = true;
            if (blockerAtStart != null)
            {
                throw new SolidEdgeDialogBlockedException(
                    AlreadyBlockedMessage(blockerAtStart, baseline, watch.ElapsedMilliseconds));
            }
            throw new TimeoutException(TimeoutMessage(baseline, watch.ElapsedMilliseconds, timeoutMs));
        }

        /// <summary>对比两次快照,找出"新出现的模态对话框"。</summary>
        private static DialogWindow FirstNewModal(WindowSnapshot baseline, WindowSnapshot now)
        {
            if (baseline == null || now == null)
            {
                return null;
            }
            HashSet<string> known = baseline.KeySet();
            foreach (DialogWindow d in now.Dialogs)
            {
                if (d.Modal && !known.Contains(d.Key))
                {
                    return d;
                }
            }
            return null;
        }

        private static string DialogBlockedMessage(DialogWindow dlg, WindowSnapshot snap, long elapsedMs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("检测到 Solid Edge 弹出了对话框,已中止等待(").Append(elapsedMs).Append(" 毫秒):").Append(System.Environment.NewLine);
            sb.Append("  ").Append(dlg.Describe()).Append(System.Environment.NewLine);
            if (dlg.Texts != null && dlg.Texts.Count > 0)
            {
                sb.Append("  框内文字: ").Append(Truncate(string.Join(" / ", dlg.Texts.ToArray()), 400)).Append(System.Environment.NewLine);
            }
            string probe = ObjectExplorer.LastProbe;
            if (!string.IsNullOrEmpty(probe))
            {
                sb.Append("  堵住时正在访问的 COM 成员(定位副作用用): ").Append(probe).Append(System.Environment.NewLine);
            }
            if (snap != null && snap.Dialogs.Count > 1)
            {
                sb.Append("  (SE 下共 ").Append(snap.Dialogs.Count).Append(" 个对话框,完整清单可跑 CLI solidedge-mcp --dialogs 看)").Append(System.Environment.NewLine);
            }
            sb.Append("结论:这次 COM 调用是被这个对话框堵住的,不是在重算。").Append(System.Environment.NewLine);
            sb.Append("怎么办:①推荐人工到 Solid Edge 界面按上面的按钮/文字处理掉它,处理完直接重试本次调用即可,SE 会自动恢复;")
              .Append("②确需代为关闭:在命令行跑「solidedge-mcp --dialogs --close ").Append(dlg.Hwnd).Append(" --confirm」(探针不碰 COM,SE 卡着也能跑;关闭前先看上面按钮的语义);")
              .Append("③框没关掉之前,任何 se_* 工具调用都会卡在同一处,不要连续重试。").Append(System.Environment.NewLine);
            sb.Append("注意:本次调用没有被取消,可能仍在后台等待——处理完对话框后它会自行返回(结果不会回传给本条消息)。");
            return sb.ToString();
        }

        private static string AlreadyBlockedMessage(DialogWindow blocker, WindowSnapshot snap, long elapsedMs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Solid Edge 当前挂着模态对话框,调用等了 ").Append(elapsedMs).Append(" 毫秒仍未返回,已提前放弃等待(而非等满整个超时)。").Append(System.Environment.NewLine);
            sb.Append("  阻塞的框: ").Append(blocker.Describe()).Append(System.Environment.NewLine);
            if (snap != null && snap.Dialogs.Count > 1)
            {
                sb.Append("  (SE 下共 ").Append(snap.Dialogs.Count).Append(" 个对话框,完整清单可跑 CLI solidedge-mcp --dialogs 看)").Append(System.Environment.NewLine);
            }
            sb.Append("处理:人工到 SE 界面点掉它(或命令行跑 solidedge-mcp --dialogs --close ").Append(blocker.Hwnd).Append(" --confirm 代为关闭);")
              .Append("处理完直接重试即可。框还在时不要连续调用其它 se_* 工具,每次都会这样快速失败。").Append(System.Environment.NewLine);
            sb.Append("注意:本次调用没有被取消,可能仍在后台等待。");
            return sb.ToString();
        }

        private static string TimeoutMessage(WindowSnapshot snap, long elapsedMs, int timeoutMs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Solid Edge 调用超过 ").Append(timeoutMs / 1000).Append(" 秒未返回(实际 ").Append(elapsedMs).Append(" 毫秒)。");
            if (snap != null)
            {
                sb.Append("已做进程外窗口探测:未发现模态对话框 → 更可能是在重算/加载大装配,而不是弹窗。");
            }
            else
            {
                sb.Append("窗口探测未启用,无法区分'弹窗'与'重算'。");
            }
            sb.Append("重要:该调用并未被取消,可能仍在后台执行——不要立刻盲目重试(每次重试都会再等满一遍超时)。")
              .Append("建议:先用 solidedge-event 的 se_wait_event 等 AfterRecompute / se_event_status 确认状态,")
              .Append("或用 CLI `solidedge-mcp --dialogs` 复查有无对话框(不碰 COM,SE 卡着也能跑),再到 SE 界面确认后再继续。");
            return sb.ToString();
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
            {
                return text;
            }
            return text.Substring(0, max) + "...";
        }

        private static bool ReadBoolEnv(string name, bool fallback)
        {
            string raw = System.Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }
            return !(raw == "0"
                || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("off", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>单次 COM 调用的超时秒数。可用环境变量 SE_MCP_TIMEOUT_SECONDS 覆盖(最小 5)。</summary>
        public static int InvokeTimeoutSeconds { get; set; } = 120;

        /// <summary>弹窗探针开关(默认开)。可用环境变量 SE_MCP_DIALOG_PROBE=0 关掉。</summary>
        internal static bool DialogProbeEnabled { get; set; } = ReadBoolEnv("SE_MCP_DIALOG_PROBE", true);

        /// <summary>等待期间的探针轮询间隔(毫秒)。</summary>
        private static readonly int DialogPollMs = 500;

        /// <summary>起调后的宽限时间(毫秒):这么快就返回的调用不可能被弹窗堵住,不做弹窗判定以免误报。</summary>
        private static readonly int DialogGraceMs = 2000;

        /// <summary>调用前 SE 就已挂着模态对话框时,最多再等这么多秒就报"被对话框阻塞"。</summary>
        private static readonly int DialogBlockedTimeoutSeconds = 8;

        // 上次 STA 调用超时后置位:SE 可能仍卡在上一个调用里,状态未知,下次调用前先做健康检查。
        private volatile bool _connectionSuspicious;

        /// <summary>封送一个无返回值的操作到 STA 线程。</summary>
        public void Invoke(Action action)
        {
            Invoke<object>(() => { action(); return null; });
        }

        private void StartStaThread()
        {
            if (Interlocked.CompareExchange(ref _staStarted, 1, 0) != 0) return;

            _staThread = new Thread(() =>
            {
                // STA 线程上注册 OLE 消息过滤器(处理 SE 忙时的重入)
                try { OleMessageFilter.Register(); } catch { }

                // 顺序消费队列里的工作项
                while (!_staQueue.IsAddingCompleted)
                {
                    try
                    {
                        Action work = _staQueue.Take();
                        try { work(); }
                        catch (Exception) { /* 异常已由 TaskCompletionSource 捕获 */ }
                    }
                    catch (InvalidOperationException)
                    {
                        break; // 队列已完成添加且已空
                    }
                }

                try { OleMessageFilter.Unregister(); } catch { }
            });
            _staThread.IsBackground = true;
            _staThread.Name = "SolidEdge-STA";
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
        }

        /// <summary>尝试连接到运行中的 Solid Edge。连不上返回 false(不抛异常)。</summary>
        public bool TryConnect()
        {
            return Invoke(TryConnectCore);
        }

        private bool TryConnectCore()
        {
            lock (_connectLock)
            {
                if (_application != null)
                {
                    // 已连接,做一次活性检查
                    try
                    {
                        // 调一个无害属性验证 SE 还活着
                        object doc = _application.ActiveDocument;
                        if (doc != null)
                        {
                            _ = doc.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, doc, null);
                        }
                        return true;
                    }
                    catch
                    {
                        // SE 关了或异常,重置
                        TryReleaseApplication();
                    }
                }

                try
                {
                    // 从 ROT 取运行中的 SE(本方法已在 STA 线程上执行)
                    ComPtr pApp = IntPtr.Zero;
                    if (MarshalEx.Succeeded(MarshalEx.GetActiveObject("SolidEdge.Application", out pApp)))
                    {
                        _application = pApp.TryGetUniqueRCW<Application>();
                        _logger?.LogInformation("已连接到 Solid Edge。");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "连接 Solid Edge 失败。请确认 SE 正在运行且打开了文档。");
                }

                return false;
            }
        }

        /// <summary>获取 Application,如果失效会尝试重连。连不上抛友好异常。</summary>
        public Application GetApplication()
        {
            return Invoke(GetApplicationCore);
        }

        private Application GetApplicationCore()
        {
            if (_connectionSuspicious && _application != null)
            {
                // 上次调用超时,SE 状态未知:先强制做一次活性检查,失败就地重连。
                _logger?.LogWarning("上次 COM 调用超时,先做连接健康检查。");
                if (!IsAlive() && !TryConnectCore())
                {
                    throw new InvalidOperationException(
                        "上次调用超时后连接已失效,且重连失败。请确认 Solid Edge 正在运行。");
                }
                _connectionSuspicious = false;
            }
            if (_application == null || !IsAlive())
            {
                if (!TryConnectCore())
                {
                    throw new InvalidOperationException(
                        "无法连接到 Solid Edge。请确认 Solid Edge 正在运行,且至少打开了一个文档。");
                }
            }
            return _application;
        }

        /// <summary>检查当前连接是否还有效。</summary>
        private bool IsAlive()
        {
            try
            {
                // 触摸一下应用对象,确认 SE 进程还在
                _ = _application.Visible;
                return true;
            }
            catch
            {
                TryReleaseApplication();
                return false;
            }
        }

        private void TryReleaseApplication()
        {
            if (_application != null)
            {
                try { Marshal.FinalReleaseComObject(_application); } catch { }
                _application = null;
            }
        }

        /// <summary>
        /// 判断异常链里是否是"COM 对象已断连"类错误。
        /// 典型场景:用户中途关闭了文档/切换了活动文档,句柄表里的 RCW 已指向死对象。
        /// 与"该属性/方法不适用"的 TargetInvocationException 区分开——后者对象还活着。
        /// </summary>
        internal static bool IsDisconnected(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is COMException comException)
                {
                    int hr = comException.HResult;
                    if (hr == unchecked((int)0x80010108)    // RPC_E_DISCONNECTED
                        || hr == unchecked((int)0x800706BA) // RPC_S_SERVER_UNAVAILABLE
                        || hr == unchecked((int)0x800706BE) // RPC_S_CALL_FAILED
                        || hr == unchecked((int)0x800706BF) // RPC_S_CALL_FAILED_DNE
                        || hr == unchecked((int)0x80010001))// RPC_E_CALL_CANCELLED
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>清空句柄表(新一次选择获取时应先清空)。</summary>
        public void ClearHandles()
        {
            Invoke(ClearHandlesCore);
        }

        private void ClearHandlesCore()
        {
            foreach (var kv in _handles)
            {
                kv.Value.Release();
            }
            _handles.Clear();
            _handleCounter = 0;
        }

        /// <summary>把一个选中对象存入句柄表,返回编号(如 "obj-1")。</summary>
        public string AddHandle(object comObject, string typeName, string displayName)
        {
            return Invoke(() =>
            {
                int id = Interlocked.Increment(ref _handleCounter);
                string key = "obj-" + id;
                var handle = new ObjectHandle(comObject, typeName, displayName, GetCurrentDocPath());
                _handles[key] = handle;
                return key;
            });
        }

        /// <summary>按编号取出缓存的选中对象。找不到返回 null。</summary>
        public ObjectHandle GetHandle(string id)
        {
            if (_handles.TryGetValue(id, out var handle))
            {
                return handle;
            }
            return null;
        }

        /// <summary>
        /// P1(2026-11-14):用前统一句柄解析——四入口(invoke_member/invoke_chain/read_geometry/read_model_state)专用。
        /// ① 文档类句柄先 TTL 探活(2 秒内不重复;读 FullName,便宜且无副作用);
        /// ② 探活失败按错误码分流:断连家族判死,忙/参数类放过(防误杀);
        /// ③ 判死返回 staleError,报错带注册文档路径,直接指路重新取句柄。
        /// 几何/特征句柄没有通用探针,不放这里——它们的失效(E_FAIL)由码表 action 的两态文案兜。
        /// </summary>
        public (ObjectHandle Handle, string StaleError) ResolveHandleForUse(string id)
        {
            ObjectHandle handle = GetHandle(id);
            if (handle == null)
            {
                return (null, null);   // 不存在——由调用方按原文案报"找不到编号"
            }
            if (handle.ComObject != null && Marshal.IsComObject(handle.ComObject)
                && !string.IsNullOrEmpty(handle.TypeName) && handle.TypeName.EndsWith("Document", StringComparison.Ordinal))
            {
                var now = DateTime.UtcNow;
                if ((now - handle.LastProbeUtc).TotalSeconds >= 2.0)
                {
                    handle.LastProbeUtc = now;
                    try
                    {
                        handle.ComObject.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, handle.ComObject, null);
                        handle.DeadConfirmed = false;
                    }
                    catch (COMException ce) when (HandleLiveness.HrMeansDead(ce.ErrorCode))
                    {
                        handle.DeadConfirmed = true;
                    }
                    catch
                    {
                        // 其它异常(忙/参数不识别/E_FAIL)一律不判死——防误杀,放行让真实调用去报真实错误
                    }
                }
            }
            if (handle.DeadConfirmed)
            {
                return (null, HandleLiveness.StaleMessage(id, handle));
            }
            return (handle, null);
        }

        /// <summary>当前活动文档 FullName,500ms 缓存(注册热循环里不该每次都付一次跨进程 COM 读)。</summary>
        internal string GetCurrentDocPath()
        {
            DateTime now = DateTime.UtcNow;
            if (_docPathCachedAtUtc != default && (now - _docPathCachedAtUtc).TotalMilliseconds < 500)
            {
                return _docPathCache;
            }
            _docPathCachedAtUtc = now;
            try
            {
                object doc = null;
                try { doc = GetApplicationCore().ActiveDocument; } catch { doc = null; }
                _docPathCache = (doc == null) ? null : Convert.ToString(doc.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, doc, null));
            }
            catch
            {
                _docPathCache = null;
            }
            return _docPathCache;
        }

        private string _docPathCache;
        private DateTime _docPathCachedAtUtc;

        public void Dispose()
        {
            try
            {
                Invoke(() =>
                {
                    ClearHandlesCore();
                    TryReleaseApplication();
                    try { OleMessageFilter.Unregister(); } catch { }
                });
            }
            catch { /* 忽略关闭时的异常 */ }

            // 停止 STA 线程
            try
            {
                _staQueue.CompleteAdding();
            }
            catch { }
        }
    }

    /// <summary>
    /// "这次 COM 调用被 Solid Edge 的模态对话框堵住了" 专用异常,
    /// 消息里带对话框标题/按钮/处理建议。继承 TimeoutException,
    /// 这样已有 catch(TimeoutException) 的调用点无需改动。
    /// </summary>
    public sealed class SolidEdgeDialogBlockedException : TimeoutException
    {
        public SolidEdgeDialogBlockedException(string message)
            : base(message)
        {
        }
    }

    /// <summary>句柄表里保存的选中对象条目。</summary>
    public sealed class ObjectHandle
    {
        public object ComObject { get; }
        public string TypeName { get; }
        public string DisplayName { get; }
        public IntPtr IUnknownPtr { get; }

        /// <summary>P1(2026-11-14):注册时的活动文档 FullName(取不到为 null)——失效报错与身份比对的原料。</summary>
        public string DocPath { get; }

        /// <summary>上次 TTL 探活时间(UTC);ResolveHandleForUse 用。</summary>
        internal DateTime LastProbeUtc { get; set; }

        /// <summary>探活确认已断连(只按断连家族 hr 判,忙/E_FAIL 不判)。</summary>
        internal bool DeadConfirmed { get; set; }

        public ObjectHandle(object comObject, string typeName, string displayName)
        {
            ComObject = comObject;
            TypeName = typeName;
            DisplayName = displayName;
            IUnknownPtr = comObject != null
                ? Marshal.GetIUnknownForObject(comObject)
                : IntPtr.Zero;
        }

        public ObjectHandle(object comObject, string typeName, string displayName, string docPath)
            : this(comObject, typeName, displayName)
        {
            DocPath = docPath;
        }

        /// <summary>比对另一个对象是否与本条目指向同一个 COM 对象(IUnknown 指针相等)。</summary>
        public bool IsSameObject(object other)
        {
            if (ComObject == null || other == null) return false;
            IntPtr otherPtr = Marshal.GetIUnknownForObject(other);
            try
            {
                return IUnknownPtr == otherPtr;
            }
            finally
            {
                if (otherPtr != IntPtr.Zero) Marshal.Release(otherPtr);
            }
        }

        internal void Release()
        {
            if (IUnknownPtr != IntPtr.Zero)
            {
                try { Marshal.Release(IUnknownPtr); } catch { }
            }
        }
    }

    /// <summary>
    /// P1(2026-11-14):句柄探活的错误码分流与失效文案——纯静态,单测钉死,不许漂移。
    /// 分流原则:只有"对象确实不在了"的码才判死;忙(RPC_E_CALL_REJECTED/RETRYLATER)、
    /// 参数类(DISP_E_*)、语义含混(E_FAIL)一律放过,防误杀正常句柄。
    /// </summary>
    internal static class HandleLiveness
    {
        private static readonly int[] DeadHrs =
        {
            unchecked((int)0x800401FD),   // CO_E_OBJNOTCONNECTED:对象未连接(文档关闭/RCW 断开)
            unchecked((int)0x80010108),   // RPC_E_DISCONNECTED:对象已与客户端断开
            unchecked((int)0x800706BA),   // RPC_S_SERVER_UNAVAILABLE:服务器进程不存在
            unchecked((int)0x800706BE),   // RPC_S_CALL_FAILED:RPC 调用失败
        };

        /// <summary>该 HRESULT 是否"确认对象已不在"。忙/参数/E_FAIL 一律 false。</summary>
        internal static bool HrMeansDead(int hr)
        {
            foreach (int dead in DeadHrs)
            {
                if (hr == dead)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>失效报错:带句柄号/类型/注册文档,直接给恢复动作,不留"API 不支持"的错觉。</summary>
        internal static string StaleMessage(string id, ObjectHandle handle)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("句柄已失效(").Append(id);
            if (handle != null && !string.IsNullOrEmpty(handle.TypeName))
            {
                sb.Append(",类型 ").Append(handle.TypeName);
            }
            if (handle != null && !string.IsNullOrEmpty(handle.DocPath))
            {
                sb.Append(",注册自文档 ").Append(handle.DocPath);
            }
            sb.Append("):文档已关闭或对象已断连(0x800401FD/RPC_E_DISCONNECTED 家族)。");
            sb.Append("请重新 se_get_selection 获取句柄(或用 se_find_objects 重新取面/特征),不要继续用旧 obj-N 重试。");
            return sb.ToString();
        }
    }
}
