using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SolidEdge.Spy.EventMcp.Command;
using SolidEdge.Spy.Extensions;
using SolidEdge.Spy.InteropServices;
using SolidEdgeFramework;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// 监视 server 的核心单例:连接 SE、管理订阅生命周期、维护过滤器与
    /// 当前文档名快照、把 sink 回调写入环形缓冲。
    ///
    /// 线程模型:
    /// - 全部 COM 调用只发生在泵线程(StaPump)的消息处理中:连接、订阅、
    ///   文档级重订、活性检查 —— 天然串行,无锁竞争;
    /// - MCP 工具(线程池 MTA)只碰纯托管状态(环形缓冲/过滤器/快照字段),
    ///   微秒级返回,永不卡在 COM 上;
    /// - sink 回调(泵线程)只调 Record/RequestDocumentRescan,零 COM 零阻塞。
    ///
    /// 自愈能力(WM_TIMER 5 秒一拍):
    /// - SE 关闭 → 探测失败 → 释放状态、合成 Disconnected 事件 → 周期重试
    /// - SE 重启 → GetActiveObject 成功 → 全量重订 → 合成 Reconnected 事件
    /// - 文档切换漏收 AfterActiveDocumentChange → 文档名快照对不上 → 兜底重订
    /// </summary>
    public sealed class EventHub : IDisposable
    {
        private readonly StaPump _pump = new StaPump();
        private readonly EventRing _ring;
        private readonly SubscriptionManager _subs;
        private readonly DateTime _startedAt = DateTime.Now;
        private readonly object _filterLock = new object();
        private readonly Dictionary<string, bool> _filters = EventCatalog.BuildDefaultFilters();

        // 命令助手三件套:台账(记录习惯) + 反解目录(ID→名) + 推荐器(算法)
        private readonly CommandLedger _ledger = new CommandLedger();
        private readonly CommandCatalog _catalog = new CommandCatalog();
        private readonly CommandRecommender _recommender = new CommandRecommender();

        // 已投泵线程预反解的 (环境,ID):历史 "#id" 条目渐进自愈的去重标记
        private readonly HashSet<string> _primedResolves = new HashSet<string>();

        // 以下字段:带 volatile 的由泵线程写、工具线程读;_app 仅泵线程访问
        private Application _app;
        private volatile bool _connected;
        private volatile string _docName = "";
        private volatile string _envName = "";     // 活动环境名快照(台账/推荐按环境分组的关键字段)
        private volatile string _subscribedDoc = "";
        private int _rescanPending;
        private int _disposed;

        /// <summary>事件进入环形缓冲时触发(泵线程;CLI 模式用于实时打印)。</summary>
        public event Action<EventRecord> EventRecorded;

        /// <summary>
        /// 台账写入开关:双进程场景(独立命令助手应用 + 本 MCP 进程)都 Advise 同一批
        /// SE 事件接口,连接点多播下事件双投递无害,但台账若两边都写会双份记录、
        /// 推荐统计翻倍 —— 台账写入权归独立应用(solidedge-command-assistant)独占,
        /// MCP 进程传 false 只保留事件环形缓冲(工具面不受影响)。
        /// </summary>
        private readonly bool _enableLedger;

        public EventHub(int ringCapacity = 200, bool enableLedger = true)
        {
            _enableLedger = enableLedger;
            _ring = new EventRing(ringCapacity);
            _subs = new SubscriptionManager(this);

            _pump.Tick += OnTimerTick;     // 泵线程:活性检查/重连
            _pump.Rescan += OnRescan;      // 泵线程:文档级重订

            // 干净退出兜底(僵尸订阅教训 §5.8):正常路径是 Main 的 finally 调
            // Shutdown();这里是 ProcessExit / Ctrl+C 的最后防线。
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
            Console.CancelKeyPress += (s, e) => Shutdown();
        }

        // ---------------- 对外状态(工具线程读) ----------------

        public bool IsConnected { get { return _connected; } }

        public string CurrentDocument { get { return _docName; } }

        /// <summary>活动环境名快照(如 "Part"/"Assembly"/"Draft";浮窗状态栏与推荐分组用)。</summary>
        public string CurrentEnvironment { get { return _envName; } }

        /// <summary>台账文件完整路径(浮窗"打开台账目录"用)。</summary>
        public string LedgerFilePath { get { return _ledger.FilePath; } }

        public int UptimeSeconds { get { return (int)(DateTime.Now - _startedAt).TotalSeconds; } }

        public SubscriptionManager.SubscriptionInfo[] SubscriptionStatus { get { return _subs.GetStatus(); } }

        public RingStats GetRingStats() { return _ring.GetStats(); }

        public RingReadResult GetEvents(long afterSeq, int limit) { return _ring.Read(afterSeq, limit); }

        public List<string> AllEventKeys() { return EventCatalog.AllKeys(); }

        public string[] DisabledEvents
        {
            get
            {
                lock (_filterLock)
                {
                    var list = new List<string>();
                    foreach (var kv in _filters)
                    {
                        if (!kv.Value) list.Add(kv.Key);
                    }
                    return list.ToArray();
                }
            }
        }

        /// <summary>设置事件过滤器。event 名可为短名(须唯一)或全名。</summary>
        public bool TrySetFilter(string eventName, bool enabled, out string resolvedKey, out string error)
        {
            resolvedKey = null;
            error = null;
            lock (_filterLock)
            {
                string key = EventCatalog.ResolveKey(eventName);
                if (key == null)
                {
                    error = "无法解析事件名 '" + eventName + "'。短名必须在全部事件中唯一(如 BeforeRecompute 同时存在于零件级/装配级,须用全名 ISEModelRecomputeEvents.BeforeRecompute)";
                    return false;
                }
                _filters[key] = enabled;
                resolvedKey = key;
                return true;
            }
        }

        // ---------------- 生命周期 ----------------

        /// <summary>启动泵线程并尝试连接订阅(SE 未运行也不失败,周期重试)。</summary>
        public void Start()
        {
            // 台账先加载、推荐器先重建(纯托管文件读),再起泵 —— 之后进来的事件做增量
            _ledger.Load();
            _recommender.Rebuild(_ledger.SnapshotAll());

            _pump.Start();
            try
            {
                _pump.Invoke(ConnectAndSubscribe);
            }
            catch
            {
                // 初始连接失败:进入未连接状态,WM_TIMER 周期重试
            }
        }

        /// <summary>干净退出:Unadvise 全部订阅并停止泵(幂等,多路径调用安全)。</summary>
        public void Shutdown()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                _pump.TryInvoke(_subs.UnsubscribeAll, TimeSpan.FromSeconds(3));
            }
            catch { }

            _pump.Stop(TimeSpan.FromSeconds(3));
            _ledger.Dispose();   // 关台账文件句柄(幂等)
        }

        public void Dispose()
        {
            Shutdown();
        }

        // ---------------- sink 回调入口(泵线程,零 COM) ----------------

        /// <summary>sink 事件入口:过过滤器 → 入环形缓冲 → 通知订阅者。</summary>
        public void Record(string source, string evt, string detail)
        {
            lock (_filterLock)
            {
                bool on;
                if (_filters.TryGetValue(source + "." + evt, out on) && !on) return;
            }

            EventRecord rec = _ring.Add(source, evt, detail, _docName);
            RaiseRecorded(rec);
        }

        /// <summary>合成运维事件(连接/断开/重订),不受过滤器控制。</summary>
        public void RecordSynthetic(string evt, string detail)
        {
            EventRecord rec = _ring.Add(EventCatalog.SyntheticSource, evt, detail, _docName);
            RaiseRecorded(rec);
        }

        /// <summary>请求重扫文档级订阅(sink 回调/兜底调用;仅 PostMessage,立即返回)。</summary>
        public void RequestDocumentRescan()
        {
            if (Interlocked.Exchange(ref _rescanPending, 1) == 0)
            {
                _pump.PostRescan();
            }
        }

        private void RaiseRecorded(EventRecord rec)
        {
            Action<EventRecord> h = EventRecorded;
            if (h != null)
            {
                try { h(rec); }
                catch { }
            }
        }

        // ---------------- 台账(sink 回调入口零 COM;反解与落盘 Post 到泵线程) ----------------
        // 台账独立于 se_set_event_filter:过滤器是给 AI 减噪的,台账记"人的习惯"应完整。

        /// <summary>
        /// 记录一条用户命令(AfterCommandRun 的 sink 回调调,零 COM 零阻塞)。
        /// 命令名反解需要 COM,Post 到泵线程延迟做 —— 同 PostRescan 的套路。
        /// </summary>
        public void NoteCommand(int commandId)
        {
            if (!_enableLedger) return;   // MCP 进程:台账写入权归独立命令助手应用
            if (commandId <= 0) return;   // 伴随环境/文档切换触发的无效命令,不进台账不进统计

            // 回调里只取托管快照字段,进闭包
            string doc = _docName;
            string env = _envName;
            DateTime time = DateTime.Now;

            _pump.Post(() =>
            {
                string name = _catalog.Resolve(_app, commandId, env);
                LedgerEntry entry = new LedgerEntry
                {
                    Time = time,
                    Kind = "Command",
                    CommandId = commandId,
                    CommandName = name,
                    Environment = env,
                    Doc = doc,
                };
                _ledger.Append(entry);
                _recommender.Observe(entry);
            });
        }

        /// <summary>
        /// 记录一条关键建模事件(建特征/改特征/保存/切文档/切环境;sink 回调调,零 COM 零阻塞)。
        /// 不做反解不碰 COM,只为习惯画像更完整;推荐统计只吃 Command,这些当会话边界用。
        /// </summary>
        public void NoteMilestone(string kind, string detail)
        {
            if (!_enableLedger) return;   // MCP 进程:台账写入权归独立命令助手应用

            string doc = _docName;
            string env = _envName;
            DateTime time = DateTime.Now;

            _pump.Post(() =>
            {
                LedgerEntry entry = new LedgerEntry
                {
                    Time = time,
                    Kind = kind,
                    Environment = env,
                    Doc = doc,
                    Detail = detail ?? "",
                };
                _ledger.Append(entry);
                _recommender.Observe(entry);
            });
        }

        // ---------------- 命令执行(浮窗一键执行用) ----------------

        /// <summary>
        /// 查 (环境,ID) 的已缓存反解名(UI 线程安全,纯内存):浮窗渲染历史 "#id"
        /// 条目时兜底替换;miss 由浮窗再调 PrimeResolve 渐进自愈。
        /// </summary>
        public string ResolveCached(int commandId, string envName)
        {
            return _catalog.CachedResolve(commandId, envName);
        }

        /// <summary>反解缓存版本号(浮窗刷新签名用):新增成功反解时递增,驱动 #id 条目重绘。</summary>
        public int ResolveVersion { get { return _catalog.CacheVersion; } }

        /// <summary>
        /// 投泵线程预反解一次(去重):历史 "#id" 条目反解成功即进缓存,
        /// 浮窗下一拍刷新就带真名。仅当 ID 在类型库暴露范围内有效。
        /// </summary>
        public void PrimeResolve(int commandId, string envName)
        {
            if (commandId <= 0) return;   // 无效 ID(历史脏数据 #0 等)永远反解不到,不做无用功
            string key = (envName ?? "?") + "|" + commandId;
            lock (_primedResolves)
            {
                if (!_primedResolves.Add(key)) return;
            }
            string env = envName;
            _pump.Post(() =>
            {
                if (_app == null)
                {
                    // 首拍刷新可能早于 SE 连接(5 秒重试周期):退回去重标记,
                    // 连接后的下一拍重新投递,否则自愈永久失效
                    lock (_primedResolves) { _primedResolves.Remove(key); }
                    return;
                }
                _catalog.Resolve(_app, commandId, env);
            });
        }

        /// <summary>
        /// 执行一条命令(参考 CommandBrowser:application.StartCommand)。投递即忘:
        /// 不阻塞浮窗 UI 线程,COM 只在泵线程。StartCommand 进入交互模式后立即返回。
        /// 已知风险:若某命令同步阻塞,会占住泵线程(事件暂停)直到退出 —— 实测遇到再改
        /// UI 线程直调(COM 封送)的 fallback。
        /// </summary>
        public bool ExecuteCommand(int commandId)
        {
            if (!_connected) return false;
            _pump.Post(() =>
            {
                try
                {
                    _app.StartCommand((SolidEdgeCommandConstants)commandId);
                }
                catch
                {
                    // 环境不支持该 ID 等:静默失败(浮窗按提交成功提示,SE 无反应即失败)
                }
            });
            return true;
        }

        // ---------------- 浮窗数据源(纯托管读,任意线程) ----------------

        /// <summary>当前推荐(内部按活动环境+会话状态算;冷启动返回 LearnMode)。</summary>
        public RecommendResult GetRecommendations()
        {
            return _recommender.Recommend(_envName);
        }

        /// <summary>台账最近 count 条(旧的在前;浮窗"最近操作"区用)。</summary>
        public LedgerEntry[] RecentLedger(int count)
        {
            return _ledger.Snapshot(count);
        }

        /// <summary>台账总条数。2026-10-25 审计:原注释称浮窗"学习中·已记录 N 条"用,但 RecommendPanel 全文无引用(浮窗用 RecentLedger),双消费者 grep 均无调用点,判死待删。</summary>
        public int LedgerCount
        {
            get { return _ledger.Count; }
        }

        // ---------------- 泵线程逻辑(可做 COM) ----------------

        private void ConnectAndSubscribe()
        {
            if (TryConnectCore())
            {
                _subs.SubscribeAll(_app);
                UpdateDocSnapshot();
                UpdateEnvSnapshot();
                _subscribedDoc = _docName;
                RecordSynthetic("Connected", "已连接 Solid Edge,文档=" + _docName);
            }
        }

        private bool TryConnectCore()
        {
            try
            {
                ComPtr pApp = IntPtr.Zero;
                if (MarshalEx.Succeeded(MarshalEx.GetActiveObject("SolidEdge.Application", out pApp)))
                {
                    _app = pApp.TryGetUniqueRCW<Application>();
                    _connected = true;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private void OnTimerTick()
        {
            if (!_connected)
            {
                if (TryConnectCore())
                {
                    _subs.SubscribeAll(_app);
                    UpdateDocSnapshot();
                    _subscribedDoc = _docName;
                    RecordSynthetic("Reconnected", "Solid Edge 已恢复,重新订阅,文档=" + _docName);
                }
                return;
            }

            try
            {
                _ = _app.Visible;   // 活性检查:SE 进程死了这里抛异常
                UpdateDocSnapshot();
                UpdateEnvSnapshot();

                // 文档切换兜底:正常靠 AfterActiveDocumentChange 触发重订,
                // 万一事件漏收,这里用文档名快照对比兜底。
                if (_docName != _subscribedDoc)
                {
                    RequestDocumentRescan();
                }
            }
            catch
            {
                HandleDisconnected();
            }
        }

        private void OnRescan()
        {
            _rescanPending = 0;
            if (!_connected)
            {
                _subscribedDoc = "";
                return;
            }

            _subs.RescanDocument(_app);
            UpdateDocSnapshot();
            UpdateEnvSnapshot();
            _subscribedDoc = _docName;
            RecordSynthetic("DocumentResubscribed", "文档级事件源已跟随切换,文档=" + _docName);
        }

        private void HandleDisconnected()
        {
            _connected = false;
            _app = null;
            _docName = "";
            _envName = "";
            _subscribedDoc = "";
            _subs.ClearForReconnect();
            RecordSynthetic("Disconnected", "Solid Edge 已关闭或连接断开,每 5 秒自动重试连接");
        }

        private void UpdateDocSnapshot()
        {
            try
            {
                object doc = _app.ActiveDocument;
                _docName = doc == null ? "" : SafeDocName(doc);
            }
            catch
            {
                _docName = "";
            }
        }

        /// <summary>
        /// 活动环境名快照(泵线程,COM)。挂在 5s Tick/连接/重订上,与 UpdateDocSnapshot 同构:
        /// AfterCommandRun 只给 CommandID 不给环境,而命令 ID 是 per-environment 的,
        /// 台账/推荐必须按环境分组 —— 环境就从这里来。
        /// </summary>
        private void UpdateEnvSnapshot()
        {
            try
            {
                SolidEdgeFramework.Environment env = _app.GetActiveEnvironment();
                _envName = env == null ? "" : (env.Name ?? "");
            }
            catch
            {
                _envName = "";
            }
        }

        private static string SafeDocName(object doc)
        {
            try
            {
                object name = doc.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, doc, null);
                return name == null ? "" : name.ToString();
            }
            catch
            {
                return "";
            }
        }
    }
}
