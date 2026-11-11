using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SolidEdge.Spy.InteropServices;
using SolidEdge.Spy.McpServer;   // 链接编译的 OleMessageFilter

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// STA 消息泵线程 —— 监视 server 的心脏。
    ///
    /// SE 是 STA COM 服务器:事件回调封送到订阅线程的 STA 单元,必须靠
    /// GetMessage/DispatchMessage 消息循环分发进 sink(调研记录 §5.1)。
    /// 现有执行 MCP 的 STA 线程用 BlockingCollection.Take 等工作项,没有
    /// 消息循环,收不到回调 —— 这正是监视功能必须独立成进程的原因。
    ///
    /// 三类消息驱动全部工作:
    ///   WM_APP+1  排空工作队列(同步 COM 操作,Invoke 提交)
    ///   WM_APP+2  重扫文档级订阅(sink 回调里 PostMessage 延迟触发 ——
    ///             回调铁律:零 COM 调用零阻塞)
    ///   WM_TIMER  5 秒一拍:活性检查 + SE 重启自动重连重订
    ///
    /// 全部 COM 调用只发生在此线程的消息处理中,天然串行,无锁竞争。
    /// </summary>
    internal sealed class StaPump
    {
        private const uint WM_INVOKE = 0x8001;      // WM_APP+1
        private const uint WM_RESCAN = 0x8002;      // WM_APP+2
        private const uint WM_TIMER_MSG = 0x0113;
        private const uint WM_QUIT = 0x0012;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private const string ClassName = "SolidEdgeEventPumpWnd";
        private const uint TimerIntervalMs = 5000;
        private static readonly IntPtr TimerId = new IntPtr(1);

        /// <summary>WM_TIMER 周期到达(泵线程,可做 COM 调用)。</summary>
        internal event Action Tick;

        /// <summary>重扫文档级订阅请求(泵线程;来源是 sink 回调的 PostMessage)。</summary>
        internal event Action Rescan;

        private sealed class WorkItem
        {
            public Func<object> Fn;
            public TaskCompletionSource<object> Tcs;
        }

        private static StaPump s_current;                  // WndProc 静态回调 → 单实例
        private readonly WndProc _proc = StaticWndProc;    // 字段持有委托,防 GC
        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

        private Thread _thread;
        private IntPtr _hwnd;
        private uint _threadId;
        private int _started;
        private int _stopping; // 2026-10-25 审计:write-only(Stop 写入,泵循环靠 WM_QUIT 退出,无读点),判死待删
        private int _startFailed;

        /// <summary>启动泵线程并等待 message-only 窗口就绪。</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            s_current = this;
            _thread = new Thread(Run)
            {
                IsBackground = false,   // 不做后台线程:保证 Shutdown 有机会执行 Unadvise(僵尸订阅教训 §5.8)
                Name = "SolidEdge-EventPump",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(5000);
            if (_startFailed != 0)
            {
                throw new InvalidOperationException("消息泵窗口创建失败,Win32 错误码 " + _startFailed);
            }
        }

        /// <summary>
        /// 把一段需要访问 Solid Edge COM 的代码封送到泵线程执行,同步等待结果。
        /// 只应由宿主生命周期代码调用(连接/订阅/关闭);MCP 工具走纯托管读路径,不经此。
        /// </summary>
        public T Invoke<T>(Func<T> fn)
        {
            EnsureReady();
            if (ReferenceEquals(Thread.CurrentThread, _thread))
            {
                return fn();
            }

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(new WorkItem { Fn = () => (object)fn(), Tcs = tcs });
            PostMessage(_hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
            return (T)tcs.Task.GetAwaiter().GetResult();
        }

        /// <summary>封送一个无返回值操作到泵线程。</summary>
        public void Invoke(Action action)
        {
            Invoke<object>(() => { action(); return null; });
        }

        /// <summary>尽力而为的封送(带超时)。用于 ProcessExit / Ctrl+C 等退出兜底。</summary>
        public bool TryInvoke(Action action, TimeSpan timeout)
        {
            if (_thread == null || !_thread.IsAlive) return false;
            if (ReferenceEquals(Thread.CurrentThread, _thread)) { action(); return true; }

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(new WorkItem { Fn = () => { action(); return null; }, Tcs = tcs });
            PostMessage(_hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
            return tcs.Task.Wait(timeout);
        }

        /// <summary>投递"重扫文档级订阅"消息(sink 回调里调用,立即返回)。</summary>
        public void PostRescan()
        {
            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, WM_RESCAN, IntPtr.Zero, IntPtr.Zero);
            }
        }

        /// <summary>
        /// 投递一段 COM 操作到泵线程,立即返回、不等结果(投递即忘)。
        /// 与 Invoke 的区别:不创建 TaskCompletionSource,不阻塞调用方,不回传结果/异常。
        /// 用途:sink 回调里延迟做 COM(回调铁律:零 COM 零阻塞),如命令名反解+台账落盘。
        /// 顺序保证:PostMessage FIFO,多次 Post 按提交顺序在泵线程执行。
        /// </summary>
        public void Post(Action action)
        {
            if (action == null || _hwnd == IntPtr.Zero) return;
            _queue.Enqueue(new WorkItem { Fn = () => { action(); return null; }, Tcs = null });
            PostMessage(_hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>停止泵线程(投递 WM_QUIT 并等待退出)。</summary>
        public void Stop(TimeSpan joinTimeout)
        {
            Interlocked.Exchange(ref _stopping, 1);
            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            var t = _thread;
            if (t != null && t.IsAlive)
            {
                try { t.Join(joinTimeout); } catch { }
            }
        }

        // ---------------- 泵线程主体 ----------------

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            try { OleMessageFilter.Register(); } catch { }

            var wc = new WNDCLASS
            {
                lpfnWndProc = _proc,
                lpszClassName = ClassName,
                hInstance = GetModuleHandle(null),
            };
            if (RegisterClass(ref wc) == 0)
            {
                _startFailed = Marshal.GetLastWin32Error();
                _ready.Set();
                return;
            }

            _hwnd = CreateWindowEx(0, ClassName, null, 0, 0, 0, 0, 0, HWND_MESSAGE,
                IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _startFailed = Marshal.GetLastWin32Error();
                _ready.Set();
                return;
            }

            SetTimer(_hwnd, TimerId, TimerIntervalMs, IntPtr.Zero);
            _ready.Set();

            // GetMessage:0=WM_QUIT,-1=错误;其余进 Translate/Dispatch 分发
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            KillTimer(_hwnd, TimerId);
            DestroyWindow(_hwnd);
            try { OleMessageFilter.Unregister(); } catch { }
        }

        private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            StaPump p = s_current;
            return p != null ? p.WndProcImpl(hWnd, msg, wParam, lParam) : DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_INVOKE:
                    DrainQueue();
                    return IntPtr.Zero;

                case WM_RESCAN:
                {
                    Action h = Rescan;
                    if (h != null) { try { h(); } catch { } }
                    return IntPtr.Zero;
                }

                case WM_TIMER_MSG:
                {
                    if (wParam == TimerId)
                    {
                        Action h = Tick;
                        if (h != null) { try { h(); } catch { } }
                    }
                    return IntPtr.Zero;
                }

                default:
                    return DefWindowProc(hWnd, msg, wParam, lParam);
            }
        }

        private void DrainQueue()
        {
            WorkItem item;
            while (_queue.TryDequeue(out item))
            {
                try
                {
                    object result = item.Fn();
                    if (item.Tcs != null) item.Tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    // Invoke 的项要回传异常;Post(投递即忘)的项没有等待者,吞掉避免拖垮泵线程
                    if (item.Tcs != null) item.Tcs.SetException(ex);
                }
            }
        }

        private void EnsureReady()
        {
            if (_started == 0 || _hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("消息泵未启动。请先调用 Start()。");
            }
        }

        // ---------------- Win32 ----------------

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint ms, IntPtr proc);

        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hWnd, IntPtr id);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
