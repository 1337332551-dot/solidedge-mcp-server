using System;
using System.Collections.Generic;
using System.Linq;
using SolidEdge.Spy;               // CommandHelper(链接编译)
using SolidEdge.Spy.Extensions;    // GetActiveEnvironment / GetGuid
using SolidEdgeFramework;

namespace SolidEdge.Spy.EventMcp.Command
{
    /// <summary>
    /// (环境, CommandID) → 可读命令名。
    ///
    /// Resolve 只能在泵线程调(内部走 COM:GetActiveEnvironment + Constants 类型库反射);
    /// 结果按环境缓存,同一 (环境, ID) 只反解一次,之后纯字典查询。
    ///
    /// 已知局限(容忍,不致命):反解按"调用时的活动环境"查(CommandHelper 既有行为),
    /// 若事件与处理之间恰好切了环境,可能反解不到 → 显示 "#id",不影响统计与一键执行。
    /// </summary>
    internal sealed class CommandCatalog
    {
        private readonly object _lock = new object();
        // 环境 → (CommandID → 短名)
        private readonly Dictionary<string, Dictionary<int, string>> _cache =
            new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        // 成功反解入缓存时递增(仅新增条目才加):浮窗刷新签名用它感知"有新名字可显示了",
        // 否则历史 #id 条目反解成功后签名不变、永不重绘,#id 钉死在屏幕上
        private int _cacheVersion;

        /// <summary>
        /// 反解命令名。envName 是事件发生时快照的环境名(仅作缓存键)。
        /// 返回短名(如 "seExtrudedProtrusion")或 "#57603"(反解不到)。
        /// </summary>
        public string Resolve(Application app, int commandId, string envName)
        {
            string key = string.IsNullOrEmpty(envName) ? "?" : envName;

            lock (_lock)
            {
                Dictionary<int, string> byId;
                if (_cache.TryGetValue(key, out byId))
                {
                    string hit;
                    if (byId.TryGetValue(commandId, out hit)) return hit;
                }
            }

            string resolved = ShortName(app, commandId, key);

            // 只缓存成功反解:失败("#id")进缓存会永久锁死重试机会
            // (如事件与反解之间切了环境导致的暂时性失败)
            if (!resolved.StartsWith("#"))
            {
                lock (_lock)
                {
                    Dictionary<int, string> byId2;
                    if (!_cache.TryGetValue(key, out byId2))
                    {
                        byId2 = new Dictionary<int, string>();
                        _cache[key] = byId2;
                    }
                    if (!byId2.ContainsKey(commandId)) _cacheVersion++;
                    byId2[commandId] = resolved;
                }
            }
            return resolved;
        }

        /// <summary>反解缓存版本号:每次新增成功反解 +1(浮窗刷新签名用)。</summary>
        public int CacheVersion
        {
            get { lock (_lock) { return _cacheVersion; } }
        }

        /// <summary>已反解的 (环境,ID) 缓存条数。2026-10-25 审计:双消费者(浮窗/CLI)grep 均无调用点(浮窗用 ResolveVersion 做刷新签名),判死待删。</summary>
        public int CachedCount
        {
            get
            {
                lock (_lock)
                {
                    int n = 0;
                    foreach (var kv in _cache) n += kv.Value.Count;
                    return n;
                }
            }
        }

        /// <summary>
        /// 只查缓存不碰 COM(浮窗 UI 线程可用):历史台账条目存的是 "#id",
        /// 渲染时用已缓存的真名兜底替换;miss 由调用方决定是否投泵线程反解。
        /// </summary>
        public string CachedResolve(int commandId, string envName)
        {
            string key = string.IsNullOrEmpty(envName) ? "?" : envName;
            lock (_lock)
            {
                Dictionary<int, string> byId;
                if (_cache.TryGetValue(key, out byId))
                {
                    string hit;
                    if (byId.TryGetValue(commandId, out hit) && !hit.StartsWith("#")) return hit;
                }
            }
            return null;
        }

        /// <summary>
        /// 反解链路:类型库环境枚举 → 类型库全库 → SECmdMap 官方 UI 命令表 → #id。
        /// 类型库在前:它才是 AfterCommandRun 的 ID 语义(SECmdMap 个别条目对不上,
        /// 如 10231);SECMDMap 兜底覆盖类型库不暴露的隐藏命令(孔/拉伸/矩形中心等)。
        /// </summary>
        private static string ShortName(Application app, int commandId, string envName)
        {
            try
            {
                string full = CommandHelper.ResolveCommandId(app, commandId);
                string name = PeelShortName(full);
                if (name != null) return name;
            }
            catch
            {
                // 反解失败不致命:SE 未连接/类型库加载失败等,走全库兜底
            }

            string global = GlobalSearch(commandId);
            if (global != null) return global;

            string uiName;
            if (CommandMapFile.TryLookup(commandId, envName, out uiName)) return uiName;

            return Unknown(commandId);
        }

        /// <summary>"57603 [XxxConstants.seYYY]" → "seYYY";Undefined/解析不出 → null。</summary>
        private static string PeelShortName(string full)
        {
            if (string.IsNullOrEmpty(full)) return null;

            int open = full.IndexOf('[');
            int close = full.IndexOf(']');
            if (open >= 0 && close > open)
            {
                string inner = full.Substring(open + 1, close - open - 1);
                if (inner.Equals("Undefined", StringComparison.OrdinalIgnoreCase)) return null;
                int dot = inner.LastIndexOf('.');
                string name = dot >= 0 ? inner.Substring(dot + 1) : inner;
                if (name.Length > 0) return name;
            }
            return null;
        }

        /// <summary>
        /// 全库兜底:环境枚举没命中时,扫全部 *CommandConstants 枚举找该 ID。
        /// 覆盖"事件与反解之间切了环境"的场景(反解走的是当前活动环境的枚举)。
        /// Constants tlb 几千个成员,毫秒级,只在环境内失败时才跑。
        /// </summary>
        private static string GlobalSearch(int commandId)
        {
            try
            {
                var tlb = InteropServices.ComTypeManager.Instance.ComTypeLibraries
                    .Where(x => x.Guid.Equals(new Guid("{C467A6F5-27ED-11D2-BE30-080036B4D502}")))
                    .FirstOrDefault();
                if (tlb == null) return null;

                foreach (var enumInfo in tlb.Enums)
                {
                    if (enumInfo.Name == null || !enumInfo.Name.Contains("CommandConstants")) continue;
                    foreach (var v in enumInfo.Variables)
                    {
                        if (v.ConstantValue != null && v.ConstantValue.Equals(commandId))
                        {
                            string name = v.Name;
                            if (!string.IsNullOrEmpty(name) && !name.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
                            {
                                return name;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 兜底失败:保持 #id
            }
            return null;
        }

        private static string Unknown(int commandId)
        {
            return "#" + commandId;
        }

        /// <summary>
        /// 反解链路逐步诊断(--resolve CLI 用):app 连接 → 活动环境 → 预热反解 →
        /// Constants 类型库 → 全库搜该 ID 的枚举成员。只读不写缓存,定位"为什么全是 #id"。
        /// </summary>
        public static System.Collections.Generic.List<string> Diagnose(Application app, int commandId)
        {
            var steps = new System.Collections.Generic.List<string>();
            steps.Add("[1] app: " + (app == null ? "null(未连接)" : "OK"));

            try
            {
                SolidEdgeFramework.Environment env = app.GetActiveEnvironment();
                steps.Add("[2] 活动环境: " + env.Name + " CATID=" + env.CATID);
            }
            catch (Exception ex)
            {
                steps.Add("[2] 活动环境: 获取失败 " + ex.Message);
            }

            // 预热:触发 CommandHelper.Initialize → LoadRegTypeLib(全部惰性,不预热扫不到)
            string probe;
            try
            {
                probe = CommandHelper.ResolveCommandId(app, commandId);
                steps.Add("[3] 预热反解(环境内): " + probe);
            }
            catch (Exception ex)
            {
                probe = null;
                steps.Add("[3] 预热反解异常: " + ex.Message);
            }

            InteropServices.ComTypeLibrary tlb = null;
            try
            {
                var libs = InteropServices.ComTypeManager.Instance.ComTypeLibraries;
                steps.Add("[4] 已加载类型库: " + libs.Count() + " 个");
                tlb = libs.Where(x => x.Guid.Equals(new Guid("{C467A6F5-27ED-11D2-BE30-080036B4D502}"))).FirstOrDefault();
                steps.Add("[5] Constants 类型库({C467A6F5-...}): " + (tlb == null ? "未找到!" : tlb.Description + " <" + tlb.Filename + ">"));
            }
            catch (Exception ex)
            {
                steps.Add("[4/5] 类型库扫描异常: " + ex.Message);
            }

            try
            {
                int hits = 0;
                if (tlb != null)
                {
                    foreach (var enumInfo in tlb.Enums)
                    {
                        if (enumInfo.Name == null || !enumInfo.Name.Contains("CommandConstants")) continue;
                        foreach (var v in enumInfo.Variables)
                        {
                            if (v.ConstantValue != null && v.ConstantValue.Equals(commandId))
                            {
                                steps.Add("[6] 全库命中: " + enumInfo.Name + "." + v.Name + " = " + commandId);
                                hits++;
                            }
                        }
                    }
                    if (hits == 0)
                    {
                        steps.Add("[6] 全部 *CommandConstants 枚举中无值 " + commandId + "(该命令 ID 不在类型库暴露范围)");
                    }
                }
                string fallback = GlobalSearch(commandId);
                steps.Add("[7] 类型库链路(环境内+全库) → 短名: " + (fallback ?? "反解不到,继续查 SECMDMap"));
            }
            catch (Exception ex)
            {
                steps.Add("[6/7] 枚举搜索异常: " + ex.Message);
            }

            try
            {
                CommandMapFile.EnsureLoaded();   // 强制加载:类型库命中的 ID 不会自然触发,诊断要看到全貌
                steps.Add("[8] SECMDMap(UI 命令表): " + (CommandMapFile.IsLoaded
                    ? CommandMapFile.CommandCount + " 条, " + CommandMapFile.FilePath
                    : "未加载: " + CommandMapFile.LoadError));
                string uiName;
                if (CommandMapFile.IsLoaded && CommandMapFile.TryLookup(commandId, "", out uiName))
                    steps.Add("[9] SECMDMap 命中: " + uiName);
                else if (CommandMapFile.IsLoaded)
                    steps.Add("[9] SECMDMap 无此 ID");
            }
            catch (Exception ex)
            {
                steps.Add("[8/9] SECMDMap 异常: " + ex.Message);
            }

            steps.Add("[10] 最终结果: " + ShortName(app, commandId, ""));
            return steps;
        }
    }
}
