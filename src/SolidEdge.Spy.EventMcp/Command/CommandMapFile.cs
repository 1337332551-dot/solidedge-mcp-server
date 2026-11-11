using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace SolidEdge.Spy.EventMcp.Command
{
    /// <summary>
    /// SE 官方 UI 命令表(SECmdMap.xml)的加载与查询。
    ///
    /// 数据来源:SE 安装目录 \Program\ResDLLs\&lt;语言LCID&gt;\SECmdMap.xml ——
    /// 这是 SE "命令查找器"(CmdFinder)的数据文件:命令 ID → 界面显示名(中文版 0804
    /// 直接给出 UI 中文名)→ 适用环境列表,共 1000+ 条。
    ///
    /// 为什么需要它:Constants 类型库只暴露 *CommandConstants 枚举里的成员,大量常用
    /// 命令(孔 10307、拉伸 10295、矩形中心 10622、SmartDimension 10091、装配 39002 …)
    /// 不在枚举里,类型库链路永远反解不到 → 本表补上这块缺口。已知边角:个别条目与
    /// AfterCommandRun 的 ID 语义对不上(如 10231 表里是"3D 草图-封闭",实际是
    /// EnvironmentsExit)——所以反解优先级仍是类型库在前,本表只兜底。
    ///
    /// 定位顺序:注册表卸载信息 → 运行中的 Edge.exe 进程路径 → Program Files 默认路径;
    /// 语言目录:当前 UI 文化 → 0804(简中) → 0009(英文) → 任一存在者。
    /// 找不到/解析失败不致命:Lookup 返回 false,行为退回纯类型库链路(显示 #id)。
    ///
    /// 线程模型:EnsureLoaded 只在泵线程首次 Resolve/Diagnose 时触发(锁内一次),
    /// 之后查表是纯字典读,任意线程安全。
    /// </summary>
    internal static class CommandMapFile
    {
        private sealed class Entry
        {
            public string Name;     // UI 显示名(0804 = 中文)
            public string[] Envs;   // 适用环境名(SECmdMap 原文,如 Part/Assembly/Profile)
        }

        private static readonly object _lock = new object();
        private static bool _attempted;          // 只尝试加载一次(失败也缓存,避免每次查表扫盘)
        private static Dictionary<int, List<Entry>> _byId;
        private static string _filePath = "";
        private static string _loadError = "";

        /// <summary>是否成功加载(诊断/浮窗状态用)。</summary>
        public static bool IsLoaded
        {
            get { lock (_lock) { return _byId != null && _byId.Count > 0; } }
        }

        /// <summary>已收录的命令 ID 数(诊断用)。</summary>
        public static int CommandCount
        {
            get { lock (_lock) { return _byId == null ? 0 : _byId.Count; } }
        }

        /// <summary>实际加载的文件完整路径(诊断用)。</summary>
        public static string FilePath
        {
            get { lock (_lock) { return _filePath; } }
        }

        /// <summary>加载失败原因(诊断用;成功时为空)。</summary>
        public static string LoadError
        {
            get { lock (_lock) { return _loadError; } }
        }

        /// <summary>
        /// 查命令 ID 的 UI 显示名。envName 是事件快照的环境名:多条同名 ID(如 孔 在
        /// 零件/装配叫法不同)优先取适用环境匹配的那条,匹配不上取文档序第一条(Rank 序)。
        /// </summary>
        public static bool TryLookup(int commandId, string envName, out string name)
        {
            name = null;
            if (commandId <= 0) return false;

            EnsureLoaded();
            lock (_lock)
            {
                List<Entry> list;
                if (_byId == null || !_byId.TryGetValue(commandId, out list) || list.Count == 0) return false;

                Entry best = null;
                if (!string.IsNullOrEmpty(envName))
                {
                    foreach (Entry e in list)
                    {
                        if (MatchesEnv(e, envName)) { best = e; break; }
                    }
                }
                // 无环境匹配:优先适用环境更多的条目(DM 专属变体通常只有 1-2 个环境,
                // 通用条目挂 Part/Weldment/Assembly 一串,如 10295 "拉伸"对"拉伸 (DM)")
                if (best == null)
                {
                    foreach (Entry e in list)
                    {
                        if (best == null || e.Envs.Length > best.Envs.Length) best = e;
                    }
                }
                name = best.Name;
                return !string.IsNullOrEmpty(name);
            }
        }

        private static bool MatchesEnv(Entry e, string envName)
        {
            foreach (string env in e.Envs)
            {
                if (string.Equals(env, envName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------------- 加载 ----------------

        /// <summary>强制尝试加载一次(幂等;诊断先调它,避免"未加载"误报)。返回是否成功。</summary>
        public static bool EnsureLoaded()
        {
            if (_attempted) return IsLoaded;
            lock (_lock)
            {
                if (_attempted) return IsLoaded;
                _attempted = true;
                try
                {
                    string path = FindCmdMapFile();
                    if (path == null)
                    {
                        _loadError = "未定位到 SECMDMap.xml(SE 安装目录)";
                        return false;
                    }
                    Dictionary<int, List<Entry>> byId = Parse(path, out string error);
                    if (byId == null)
                    {
                        _loadError = error;
                        return false;
                    }
                    _filePath = path;
                    _byId = byId;
                }
                catch (Exception ex)
                {
                    _loadError = ex.Message;
                }
            }
            return IsLoaded;
        }

        /// <summary>解析 SECMDMap.xml:只取 Type="Command" 的 ID(EdgebarTab 等不是命令 ID)。</summary>
        private static Dictionary<int, List<Entry>> Parse(string path, out string error)
        {
            error = null;
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);

                var byId = new Dictionary<int, List<Entry>>();
                foreach (XmlNode cmd in doc.DocumentElement.SelectNodes("Command"))
                {
                    XmlNode idNode = cmd.SelectSingleNode("ID");
                    if (idNode == null) continue;
                    string type = idNode.Attributes?["Type"]?.Value ?? "";
                    if (!string.Equals(type, "Command", StringComparison.OrdinalIgnoreCase)) continue;

                    int id;
                    if (!int.TryParse((idNode.InnerText ?? "").Trim(), out id) || id <= 0) continue;

                    string name = (cmd.SelectSingleNode("Name")?.InnerText ?? "").Trim();
                    if (name.Length == 0) continue;

                    var envs = new List<string>();
                    foreach (XmlNode env in cmd.SelectNodes("Environments/Environment"))
                    {
                        string en = env.Attributes?["Name"]?.Value ?? "";
                        if (en.Length > 0) envs.Add(en);
                    }

                    List<Entry> list;
                    if (!byId.TryGetValue(id, out list))
                    {
                        list = new List<Entry>();
                        byId[id] = list;
                    }
                    list.Add(new Entry { Name = name, Envs = envs.ToArray() });
                }
                return byId.Count > 0 ? byId : null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // ---------------- 安装目录定位 ----------------

        private static string FindCmdMapFile()
        {
            foreach (string root in CandidateRoots())
            {
                string resDir = Path.Combine(root, "Program", "ResDLLs");
                if (!Directory.Exists(resDir)) continue;
                string file = PickByLanguage(resDir);
                if (file != null) return file;
            }
            return null;
        }

        /// <summary>语言目录优先级:当前 UI 文化 → 0804(简中) → 0009(英文) → 任一存在者。</summary>
        private static string PickByLanguage(string resDir)
        {
            string ui = null;
            try { ui = CultureInfo.CurrentUICulture.LCID.ToString("X4"); } catch { }

            foreach (string lcid in new[] { ui, "0804", "0009" })
            {
                if (string.IsNullOrEmpty(lcid)) continue;
                string file = Path.Combine(resDir, lcid, "SECmdMap.xml");
                if (File.Exists(file)) return file;
            }
            foreach (string dir in Directory.GetDirectories(resDir))
            {
                string file = Path.Combine(dir, "SECmdMap.xml");
                if (File.Exists(file)) return file;
            }
            return null;
        }

        /// <summary>SE 安装根目录候选(依次:注册表卸载信息 / 运行中进程 / 默认路径)。</summary>
        private static IEnumerable<string> CandidateRoots()
        {
            var roots = new List<string>();

            // 1) 注册表卸载信息(DisplayName 含 "Solid Edge";两种视图都扫,兼容 32 位注册)
            foreach (Microsoft.Win32.RegistryView view in new[]
                { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                        Microsoft.Win32.RegistryHive.LocalMachine, view))
                    using (var unKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (unKey == null) continue;
                        foreach (string sub in unKey.GetSubKeyNames())
                        {
                            using (var k = unKey.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string display = k.GetValue("DisplayName") as string;
                                if (display == null ||
                                    display.IndexOf("Solid Edge", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                string loc = (k.GetValue("InstallLocation") as string ?? "").Trim();
                                if (loc.Length > 0 && Directory.Exists(loc)) roots.Add(loc);
                            }
                        }
                    }
                }
                catch
                {
                    // 注册表读不到(权限等):走后续候选
                }
            }

            // 2) 运行中的 SE 进程:<root>\Program\Edge.exe → 上跳两级
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Edge"))
                {
                    try
                    {
                        string exe = null;
                        if (p.MainModule != null) exe = p.MainModule.FileName;
                        if (!string.IsNullOrEmpty(exe))
                        {
                            string programDir = Path.GetDirectoryName(exe);
                            if (!string.IsNullOrEmpty(programDir))
                            {
                                string root = Path.GetFullPath(Path.Combine(programDir, "..", ".."));
                                if (Directory.Exists(root)) roots.Add(root);
                            }
                        }
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
                // 进程枚举失败(权限等):走后续候选
            }

            // 3) 默认安装路径:%ProgramFiles%\Solid Edge * 与 %ProgramFiles%\Siemens\Solid Edge *
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            try
            {
                if (Directory.Exists(pf))
                {
                    roots.AddRange(Directory.GetDirectories(pf, "Solid Edge *", SearchOption.TopDirectoryOnly));
                    string siemens = Path.Combine(pf, "Siemens");
                    if (Directory.Exists(siemens))
                        roots.AddRange(Directory.GetDirectories(siemens, "Solid Edge *", SearchOption.TopDirectoryOnly));
                }
            }
            catch
            {
                // 目录枚举失败:就这么多候选
            }
            return roots;
        }
    }
}
