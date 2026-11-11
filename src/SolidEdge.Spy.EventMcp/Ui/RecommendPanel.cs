using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SolidEdge.Spy.EventMcp.Command;

namespace SolidEdge.Spy.EventMcp.Ui
{
    /// <summary>
    /// 命令助手浮窗:推荐区(双击执行) + 最近操作区 + 状态栏。
    ///
    /// 数据源全部走 EventHub 的纯托管读(GetRecommendations/RecentLedger),线程安全;
    /// 执行走 hub.ExecuteCommand(投递到泵线程)。1 秒定时刷新,数据没变就不重绘(防闪烁)。
    /// 手写布局无 Designer 文件:控件少,直接 Dock 排列,易读易改。
    /// </summary>
    internal sealed class RecommendPanel : Form
    {
        /// <summary>刷新间隔(节流:用户连点命令时避免疯狂重绘)。</summary>
        private const int RefreshIntervalMs = 1000;
        private const int RecentCount = 10;

        private readonly EventHub _hub;
        private readonly Timer _refreshTimer;

        private readonly Label _statusLabel;
        private readonly Label _recoHeader;
        private readonly ListView _recoList;
        private readonly Label _recentHeader;
        private readonly ListView _recentList;
        private string _lastSignature = "";   // 上次刷新的数据签名,变了才重绘

        public RecommendPanel(EventHub hub)
        {
            _hub = hub;

            // 窗体:置顶小工具窗,不进任务栏,默认落屏幕右下角
            Text = "SolidEdge 命令助手";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(400, 520);
            Font = new Font("Microsoft YaHei UI", 9F);

            // 应用图标:入口程序集嵌入资源 icon.png(solidedge-command-assistant 嵌入;
            // 其他宿主如 EventMcp 无此资源则保持默认图标)。走 Bitmap→GetHicon 路径,
            // 因 GDI+ Icon(Stream) 不支持 PNG 帧的 ico。
            try
            {
                using (var stream = System.Reflection.Assembly.GetEntryAssembly()
                    ?.GetManifestResourceStream("icon.png"))
                {
                    if (stream != null)
                    {
                        using (var bmp = new Bitmap(stream))
                        {
                            Icon = Icon.FromHandle(bmp.GetHicon());
                        }
                    }
                }
            }
            catch
            {
                // 图标加载失败不致命:保持系统默认图标
            }

            // ---- 推荐区 ----
            _recoList = new ListView
            {
                Dock = DockStyle.Top,
                Height = 200,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            _recoList.Columns.Add("命令", 120);
            _recoList.Columns.Add("置信", 55);
            _recoList.Columns.Add("为什么推它", 200);
            _recoList.DoubleClick += OnRecommendExecute;

            _recoHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "推荐",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 4, 0, 0),
                ForeColor = SystemColors.ControlDarkDark,
            };

            // ---- 最近操作区 ----
            _recentList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            _recentList.Columns.Add("时间", 68);
            _recentList.Columns.Add("操作", 305);

            _recentHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "最近操作",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 4, 0, 0),
                ForeColor = SystemColors.ControlDarkDark,
            };

            // ---- 状态栏 / 按钮栏 ----
            _statusLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = "未连接 Solid Edge",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 5, 0, 0),
            };

            var buttonBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4, 4, 4, 0),
            };
            buttonBar.Controls.Add(MakeButton("隐藏", delegate { Close(); }));
            buttonBar.Controls.Add(MakeButton("打开台账目录", delegate { OpenLedgerFolder(); }));
            buttonBar.Controls.Add(MakeButton("刷新", delegate { RefreshData(force: true); }));

            // Dock 自上而下的堆叠顺序与添加顺序相反:先加的沉底
            Controls.Add(_recentList);
            Controls.Add(_recentHeader);
            Controls.Add(_recoList);
            Controls.Add(_recoHeader);
            Controls.Add(_statusLabel);
            Controls.Add(buttonBar);

            _refreshTimer = new Timer { Interval = RefreshIntervalMs };
            _refreshTimer.Tick += delegate { RefreshData(force: false); };

            Shown += delegate
            {
                RefreshData(force: true);
                _refreshTimer.Start();
            };
            FormClosed += delegate { _refreshTimer.Stop(); };

            PlaceBottomRight();
        }

        // ---------------- 刷新 ----------------

        private void RefreshData(bool force)
        {
            try
            {
                string env = _hub.CurrentEnvironment;
                string doc = _hub.CurrentDocument;
                bool connected = _hub.IsConnected;
                _currentEnv = env;
                RecommendResult reco = _hub.GetRecommendations();
                LedgerEntry[] recent = _hub.RecentLedger(RecentCount);

                // 数据签名:没变就不重绘(防闪烁)。
                // ResolveVersion 是反解缓存版本:历史 #id 条目被泵线程反解成功后版本递增,
                // 签名变化驱动下一拍重绘,渐进自愈才看得见(否则 #id 钉死在屏幕上)。
                string signature = connected + "|" + env + "|" + doc + "|" + reco.TotalSamples + "|" +
                                   reco.Items.Length + "|" + (reco.LearnMode ? "L" : "R") + "|" +
                                   _hub.ResolveVersion;
                foreach (Recommendation r in reco.Items)
                {
                    signature += "|" + r.CommandId + ":" + r.Score.ToString("F4");
                }
                if (recent.Length > 0)
                {
                    LedgerEntry newest = recent[recent.Length - 1];
                    signature += "|" + newest.Time.Ticks + "|" + newest.Kind;
                }
                if (!force && signature == _lastSignature) return;
                _lastSignature = signature;

                _statusLabel.Text = connected
                    ? (env + (doc.Length > 0 ? " · " + doc : "") + " · 已连接")
                    : "未连接 Solid Edge（启动后自动接入）";

                if (reco.LearnMode)
                {
                    _recoHeader.Text = "推荐（学习中 · 已记录 " + reco.TotalSamples + " 条操作，满 20 条开始推荐）";
                    _recoList.BeginUpdate();
                    _recoList.Items.Clear();
                    _recoList.EndUpdate();
                }
                else
                {
                    _recoHeader.Text = "推荐（基于你的习惯，共 " + reco.TotalSamples + " 条样本；双击执行）";
                    FillRecommendations(reco.Items);
                }

                FillRecent(recent);
            }
            catch
            {
                // 刷新失败(等价于数据还没好):下一拍再来,不打断 UI
            }
        }

        private void FillRecommendations(Recommendation[] items)
        {
            _recoList.BeginUpdate();
            _recoList.Items.Clear();

            // 置信列 = 相对最高分的百分比(相对强弱,不是概率)
            double top = 0;
            foreach (Recommendation r in items)
            {
                if (r.Score > top) top = r.Score;
            }

            foreach (Recommendation r in items)
            {
                int percent = top > 0 ? (int)Math.Round(100.0 * r.Score / top) : 0;
                var item = new ListViewItem(DisplayName(r.CommandName, r.CommandId, _currentEnv));
                item.SubItems.Add(percent + "%");
                item.SubItems.Add(r.Reason);
                item.Tag = r.CommandId;
                item.ToolTipText = r.Reason + "（样本 " + r.SampleCount + " 条，原始名 " + r.CommandName + "）";
                _recoList.Items.Add(item);
            }
            _recoList.EndUpdate();
        }

        private void FillRecent(LedgerEntry[] entries)
        {
            _recentList.BeginUpdate();
            _recentList.Items.Clear();

            // 新的在上
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                LedgerEntry e = entries[i];
                if (e.Kind == "Command" && e.CommandId <= 0) continue;   // 历史脏数据(#0):切换误触,无显示价值
                var item = new ListViewItem(e.Time.ToString("HH:mm:ss"));
                item.SubItems.Add(DescribeEntry(e));
                _recentList.Items.Add(item);
            }
            _recentList.EndUpdate();
        }

        /// <summary>当前刷新用的活动环境名(推荐缓存查询键;台账条目另有 e.Environment)。</summary>
        private string _currentEnv = "";

        /// <summary>
        /// 显示名兜底:历史台账条目存 "#id"(当时反解失败),渲染时先查反解缓存
        /// (--resolve/运行期反解成功过的都有),miss 则投泵线程预反解一次(去重),
        /// 下一拍刷新自然带真名 —— 历史 #id 条目渐进自愈,不在类型库的保持 #id。
        /// </summary>
        private string DisplayName(string name, int commandId, string envKey)
        {
            if (string.IsNullOrEmpty(name)) name = "#" + commandId;
            if (!name.StartsWith("#")) return CommandNameMap.Translate(name);

            string cached = _hub.ResolveCached(commandId, envKey);
            if (cached != null) return CommandNameMap.Translate(cached);
            _hub.PrimeResolve(commandId, envKey);
            return name;
        }

        private string DescribeEntry(LedgerEntry e)
        {
            switch (e.Kind)
            {
                case "Command":
                    return "命令 " + DisplayName(e.CommandName, e.CommandId, e.Environment);
                case "FeatureAdded": return "新建特征";
                case "FeatureModified": return "修改特征";
                case "AsmAdd": return "装配加零件";
                case "AsmModify": return "装配改零件";
                case "Save": return "保存";
                case "DocChange": return "切换文档";
                case "EnvChange": return "切换环境";
                default: return e.Kind;
            }
        }

        // ---------------- 动作 ----------------

        private void OnRecommendExecute(object sender, EventArgs e)
        {
            if (_recoList.SelectedItems.Count == 0) return;
            int commandId;
            if (!int.TryParse(_recoList.SelectedItems[0].Tag.ToString(), out commandId)) return;

            if (_hub.ExecuteCommand(commandId))
            {
                _statusLabel.Text = "已发送命令 " + _recoList.SelectedItems[0].Text + "（若 SE 无反应则该命令在当前环境不可用）";
            }
            else
            {
                _statusLabel.Text = "未连接 Solid Edge,无法执行";
            }
        }

        private void OpenLedgerFolder()
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _hub.LedgerFilePath + "\"");
            }
            catch
            {
                // 打开失败静默(资源管理器异常等)
            }
        }

        private void PlaceBottomRight()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(work.Right - Width - 16, work.Bottom - Height - 16);
        }

        private static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += onClick;
            return b;
        }
    }
}
