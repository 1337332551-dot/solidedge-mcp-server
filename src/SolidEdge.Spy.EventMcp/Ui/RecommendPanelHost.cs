using System;
using System.Threading;
using System.Windows.Forms;

namespace SolidEdge.Spy.EventMcp.Ui
{
    /// <summary>
    /// 浮窗的 UI 线程宿主:独立 STA 线程 + Application.Run 消息循环。
    ///
    /// 为什么不占泵线程:泵线程是全进程唯一 COM 线程(订阅/重订/活性检查),
    /// 浮窗若挤上去,一个模态弹窗就能卡死事件订阅。独立 STA 线程互不干扰;
    /// 浮窗触发的 COM(一键执行)经 hub.ExecuteCommand 投递回泵线程。
    ///
    /// 生命周期:Start 起线程;窗体被用户关掉 → Run 返回 → 线程自然结束;
    /// MCP 退出路径调 Close 收尾(BeginInvoke Close + Join 兜底)。
    /// </summary>
    internal static class RecommendPanelHost
    {
        private static readonly object _lock = new object();
        private static Thread _thread;
        private static RecommendPanel _panel;

        /// <summary>起 UI 线程并显示浮窗(幂等;已在跑则不动)。</summary>
        public static void Start(EventHub hub)
        {
            lock (_lock)
            {
                if (_thread != null && _thread.IsAlive) return;

                _thread = new Thread(() =>
                {
                    // WinForms 标准初始化必须在创建任何控件之前、且在本线程上
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    var panel = new RecommendPanel(hub);
                    _panel = panel;
                    Application.Run(panel);   // 窗体关闭 → Run 返回 → 线程结束
                });
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.IsBackground = true;   // 兜底:退出路径没来得及关它也不拖住进程
                _thread.Name = "SolidEdge-RecommendPanel";
                _thread.Start();
            }
        }

        /// <summary>浮窗是否仍在显示( --panel CLI 判断退出用)。</summary>
        public static bool IsRunning
        {
            get
            {
                var p = _panel;
                return p != null && p.IsHandleCreated && !p.IsDisposed && p.Visible;
            }
        }

        /// <summary>请求关闭浮窗并等 UI 线程退出(MCP 退出路径调;幂等安全)。</summary>
        public static void Close()
        {
            var p = _panel;
            if (p != null)
            {
                try
                {
                    if (p.IsHandleCreated && !p.IsDisposed)
                    {
                        p.BeginInvoke(new Action(p.Close));
                    }
                }
                catch
                {
                    // 线程已死/句柄失效:无需处理
                }
            }

            var t = _thread;
            if (t != null && t.IsAlive)
            {
                try { t.Join(3000); } catch { }
            }
        }
    }
}
