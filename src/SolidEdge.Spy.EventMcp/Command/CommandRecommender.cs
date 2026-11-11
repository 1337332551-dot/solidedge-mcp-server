using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidEdge.Spy.EventMcp.Command
{
    /// <summary>推荐结果里的一条。</summary>
    public sealed class Recommendation
    {
        public int CommandId;
        public string CommandName;
        public double Score;      // 0.6*T' + 0.4*F,相对强弱值,不是概率
        public string Reason;     // 人话理由,如 "做完「草图」后 10 次里 7 次接着它"
        public int SampleCount;   // 支撑样本数(转移次数或该命令出现次数)
    }

    /// <summary>推荐结果。LearnMode=true 表示样本不足,不硬推(浮窗显示"学习中")。</summary>
    public sealed class RecommendResult
    {
        public bool LearnMode;
        public int TotalSamples;
        public Recommendation[] Items = new Recommendation[0];
    }

    /// <summary>
    /// 命令推荐器:一阶马尔可夫(转移) + 环境内频次,加权求和取 Top-5。
    ///
    ///   转移分 T(c) = 加权次数(上一个命令→c) / 加权次数(上一个命令→任意)
    ///   频次分 F(c) = 加权次数(c) / 加权次数(任意)      (同一环境内)
    ///   总分 = 0.6*T'(c) + 0.4*F(c)
    ///
    /// 修正项:
    ///   时间衰减 —— 每次历史贡献 0.5^(距今天数/34),半衰期约 34 天,旧习惯自动淡化
    ///   小样本平滑 —— T'(c) = T(c) * n/(n+3),n=转移原始次数,防"一次偶然推到第一"
    ///   会话切分 —— 相邻命令间隔>30分钟 / 中间切环境 / 切文档 → 不算转移
    ///   自转移忽略 —— 连续点同一命令不计转移;推荐时也不推"刚点过的"
    ///   冷启动 —— 命令总数<20 条时 LearnMode,不硬推
    ///
    /// 统计键 = (环境, CommandID):命令 ID 是 per-environment 整数
    /// (零件环境的 57603 与工程图环境的 57603 是两个命令),只用 ID 会跨环境撞车。
    ///
    /// 线程模型:Observe(泵线程)/Rebuild(启动线程)/Recommend(UI 线程) → 全部走 _lock。
    /// </summary>
    internal sealed class CommandRecommender
    {
        // ---- 可调参数(实机观察效果后再定) ----
        private const double WeightTransition = 0.6;
        private const double WeightFrequency = 0.4;
        private const double HalfLifeDays = 34;
        private const int SmoothingK = 3;
        private static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);
        private const int MinSamples = 20;
        private const int TopK = 5;

        /// <summary>视图/导航类命令关键词:只污染转移序列不参与推荐(按翻译后的名字尽力过滤)。</summary>
        private static readonly string[] IgnoreNameKeywords = new[]
        {
            "View", "视图", "Zoom", "缩放", "满屏", "Pan", "平移", "着色", "透视",
            "Rotate", "旋转视图", "Fit", "Window", "窗口",
        };

        private sealed class FreqStat { public double Weight; public int Count; public string Name; }
        private sealed class TransStat { public double Weight; public int Count; }

        private readonly object _lock = new object();

        // 环境内频次:env → id → 统计
        private readonly Dictionary<string, Dictionary<int, FreqStat>> _freq =
            new Dictionary<string, Dictionary<int, FreqStat>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _freqTotal =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // 一阶转移:"env|prevId" → nextId → 统计
        private readonly Dictionary<string, Dictionary<int, TransStat>> _trans =
            new Dictionary<string, Dictionary<int, TransStat>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _transTotal =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // 会话跟踪:"上一个命令"(转移的起点)
        private DateTime _lastCommandAt = DateTime.MinValue;
        private int _lastCommandId;
        private string _lastCommandName = "";
        private string _lastCommandEnv = "";
        private int _totalCommands;

        /// <summary>启动时用台账全量重建(内存计数器,无持久模型文件)。</summary>
        public void Rebuild(LedgerEntry[] entries)
        {
            lock (_lock)
            {
                _freq.Clear();
                _freqTotal.Clear();
                _trans.Clear();
                _transTotal.Clear();
                _totalCommands = 0;
                _lastCommandAt = DateTime.MinValue;
                _lastCommandId = 0;
                _lastCommandName = "";
                _lastCommandEnv = "";

                if (entries == null) return;
                foreach (LedgerEntry e in entries)
                {
                    ObserveCore(e);
                }
            }
        }

        /// <summary>增量观察一条台账(泵线程在 Append 后调)。</summary>
        public void Observe(LedgerEntry entry)
        {
            if (entry == null) return;
            lock (_lock)
            {
                ObserveCore(entry);
            }
        }

        private void ObserveCore(LedgerEntry e)
        {
            // 非命令里程碑:只有切环境/切文档影响会话边界("上一个命令"失效)
            if (e.Kind == "EnvChange" || e.Kind == "DocChange")
            {
                _lastCommandId = 0;
                _lastCommandName = "";
                return;
            }
            // 建特征/保存/装配增改不参与统计:它们由对应命令触发,算了会双计
            if (e.Kind != "Command") return;
            // 历史 #0 脏数据(伴随环境/文档切换的误触,新台账已挡):不进统计不进推荐
            if (e.CommandId <= 0) return;

            string env = string.IsNullOrEmpty(e.Environment) ? "?" : e.Environment;
            double w = Decay(e.Time);

            // 频次
            Dictionary<int, FreqStat> byId = GetOrCreate(_freq, env);
            FreqStat fs;
            if (!byId.TryGetValue(e.CommandId, out fs))
            {
                fs = new FreqStat();
                byId[e.CommandId] = fs;
            }
            fs.Weight += w;
            fs.Count++;
            if (string.IsNullOrEmpty(fs.Name) || fs.Name.StartsWith("#"))
            {
                // 优先记住反解出的可读名(后续 "#57603" 被真名覆盖);翻译成中文界面叫法
                fs.Name = string.IsNullOrEmpty(e.CommandName)
                    ? ("#" + e.CommandId)
                    : CommandNameMap.Translate(e.CommandName);
            }
            double total;
            _freqTotal.TryGetValue(env, out total);
            _freqTotal[env] = total + w;

            // 转移(会话有效且非自转移才计)
            bool sessionValid =
                _lastCommandId != 0 &&
                string.Equals(_lastCommandEnv, env, StringComparison.OrdinalIgnoreCase) &&
                (e.Time - _lastCommandAt) <= SessionGap;
            if (sessionValid && _lastCommandId != e.CommandId)
            {
                string tkey = env + "|" + _lastCommandId;
                Dictionary<int, TransStat> next = GetOrCreate(_trans, tkey);
                TransStat ts;
                if (!next.TryGetValue(e.CommandId, out ts))
                {
                    ts = new TransStat();
                    next[e.CommandId] = ts;
                }
                ts.Weight += w;
                ts.Count++;

                double ttotal;
                _transTotal.TryGetValue(tkey, out ttotal);
                _transTotal[tkey] = ttotal + w;
            }

            // 更新"上一个命令"(重复点同一命令也刷新时间,会话延续)
            _lastCommandAt = e.Time;
            _lastCommandId = e.CommandId;
            _lastCommandName = string.IsNullOrEmpty(e.CommandName)
                ? ("#" + e.CommandId)
                : CommandNameMap.Translate(e.CommandName);
            _lastCommandEnv = env;
            _totalCommands++;
        }

        /// <summary>推荐 currentEnv 下接下来可能用的命令(UI 线程调,1 秒节流在浮窗侧)。</summary>
        public RecommendResult Recommend(string currentEnv)
        {
            lock (_lock)
            {
                var result = new RecommendResult();
                result.TotalSamples = _totalCommands;

                // 冷启动:样本不足,不硬推
                if (_totalCommands < MinSamples)
                {
                    result.LearnMode = true;
                    return result;
                }

                string env = string.IsNullOrEmpty(currentEnv) ? "?" : currentEnv;

                // "上一个命令"是否仍在会话内(决定用不用转移分;新会话/刚切环境只用频次)
                bool useTransition =
                    _lastCommandId != 0 &&
                    string.Equals(_lastCommandEnv, env, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.Now - _lastCommandAt) <= SessionGap;

                Dictionary<int, FreqStat> freqById;
                if (!_freq.TryGetValue(env, out freqById) || freqById.Count == 0)
                {
                    return result;   // 该环境还没有数据(样本都在别的环境)
                }
                double freqTotal;
                _freqTotal.TryGetValue(env, out freqTotal);
                if (freqTotal <= 0) return result;

                string transKey = env + "|" + _lastCommandId;
                Dictionary<int, TransStat> transById = null;
                double transTotal = 0;
                int prevOutRaw = 0;
                bool hasTrans = false;
                if (useTransition)
                {
                    if (_trans.TryGetValue(transKey, out transById) && transById.Count > 0)
                    {
                        hasTrans = true;
                        _transTotal.TryGetValue(transKey, out transTotal);
                        foreach (TransStat ts in transById.Values) prevOutRaw += ts.Count;
                    }
                }

                var scored = new List<Recommendation>();
                foreach (var kv in freqById)
                {
                    int id = kv.Key;
                    FreqStat fs = kv.Value;
                    string name = string.IsNullOrEmpty(fs.Name) ? ("#" + id) : fs.Name;

                    if (IsIgnored(name)) continue;
                    if (useTransition && id == _lastCommandId) continue;   // 不推刚点过的

                    double f = fs.Weight / freqTotal;
                    double score = WeightFrequency * f;
                    string reason = "「" + env + "」环境常用（约 " + fs.Count + " 次）";
                    int samples = fs.Count;

                    if (hasTrans)
                    {
                        TransStat ts;
                        if (transById.TryGetValue(id, out ts))
                        {
                            double tRaw = transTotal > 0 ? ts.Weight / transTotal : 0;
                            double tSmooth = tRaw * ts.Count / (ts.Count + SmoothingK);
                            score += WeightTransition * tSmooth;
                            reason = "做完「" + _lastCommandName + "」后 " +
                                     prevOutRaw + " 次里 " + ts.Count + " 次接着它";
                            samples = ts.Count;
                        }
                    }

                    var rec = new Recommendation();
                    rec.CommandId = id;
                    rec.CommandName = name;
                    rec.Score = score;
                    rec.Reason = reason;
                    rec.SampleCount = samples;
                    scored.Add(rec);
                }

                scored.Sort(delegate(Recommendation a, Recommendation b) { return b.Score.CompareTo(a.Score); });
                result.Items = scored.Take(TopK).ToArray();
                return result;
            }
        }

        /// <summary>时间衰减权重:0.5^(距今天数/半衰期)。</summary>
        private static double Decay(DateTime time)
        {
            double days = Math.Max(0, (DateTime.Now - time).TotalDays);
            return Math.Pow(0.5, days / HalfLifeDays);
        }

        private static bool IsIgnored(string name)
        {
            foreach (string keyword in IgnoreNameKeywords)
            {
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static Dictionary<int, TValue> GetOrCreate<TValue>(
            Dictionary<string, Dictionary<int, TValue>> dict, string key)
        {
            Dictionary<int, TValue> inner;
            if (!dict.TryGetValue(key, out inner))
            {
                inner = new Dictionary<int, TValue>();
                dict[key] = inner;
            }
            return inner;
        }
    }
}
