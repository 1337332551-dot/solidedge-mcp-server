using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NCalc;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 声明式建模的预处理流水线(P1:sets 参数表 + 算术表达式)。
    ///
    /// 只做"数字"这一件事:
    ///   ① ResolveSets   把 sets 里的每条声明求成一个具体数值(允许互相引用,自动按依赖顺序解)
    ///   ② Substitute    把 features JSON 里的 {{名字}} 换成数字文本,再解析回 JsonElement[]
    ///
    /// 关键不变量(改造前就存在、必须保住):替换发生在 FeatureSpecParser / FeatureValidator **之前**,
    /// 所以校验器与构建器看到的仍是同一份"全部写死数字"的 JSON。求值出错就整批拒绝,绝不半途替换。
    ///
    /// 设计红线:这里不是 DSL——只有数值、四则/幂/括号,名字只能是标识符。
    /// 需要循环/条件/读几何 → 后续的 compute op(脚本)或 measure op,不要往这里加语法。
    ///
    /// 纯函数,不碰 COM,可直接单测。
    /// </summary>
    /// <summary>
    /// 一个建模段:要么沿用调用方的文档(Doc=null),要么由 doc op 新建一份文档。
    /// 段与段之间只共享求值环境(sets),批内字典(@别名/贴面缓存)一律每段重建。
    /// </summary>
    internal sealed class BuildSegment
    {
        /// <summary>doc op 的原始 JSON;null = 沿用当前文档。</summary>
        public JsonElement? Doc;

        /// <summary>P3 动作段的原始 JSON(measure/compute);null = 普通建模段。</summary>
        public JsonElement? Action;

        /// <summary>动作段类型:"measure" / "compute";普通段为 null。</summary>
        public string ActionOp;

        /// <summary>本段的特征(不含 doc op 本身)。</summary>
        public List<JsonElement> Features = new List<JsonElement>();

        /// <summary>段序号,从 0 开始(报错定位用)。</summary>
        public int Index;

        /// <summary>段名:doc.name 优先,否则 "segment-N"。</summary>
        public string Name;
    }

    internal static class BuildPipeline
    {
        /// <summary>占位符语法与配方通道保持一致:{{名字}}(名字 = 字母/数字/下划线,不以数字开头)。</summary>
        internal static readonly Regex PlaceholderPattern =
            new Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}", RegexOptions.Compiled);

        /// <summary>
        /// 带引号的占位符(JSON 里唯一能合法出现的形态):"{{名字}}" → 连引号一起换成数字字面量。
        /// </summary>
        private static readonly Regex QuotedPlaceholderPattern =
            new Regex("\"\\s*\\{\\{\\s*([A-Za-z_][A-Za-z0-9_.]*)\\s*\\}\\}\\s*\"", RegexOptions.Compiled);

        /// <summary>sets 里的名字只允许标识符,避免与 JSON 语法/占位符语法打架。</summary>
        private static readonly Regex NamePattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        /// <summary>
        /// 求 sets。输入形如 {"l1": 0.04, "d": "0.5*{{l1}} + 2"}:
        /// 数字字面量直接取值;字符串按表达式求值,里面可以引用其它已定义的名字。
        /// 允许任意书写顺序(靠迭代求解,不要求调用方自己排序);引用了不存在的名字或成环 → 报错。
        /// </summary>
        internal static bool ResolveSets(JsonElement? sets, out Dictionary<string, double> values, out string error)
        {
            // out 参数不能进 lambda(CS1628),所以全程用局部 acc,出口再赋给 values。
            var acc = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            values = acc;
            error = null;

            if (!sets.HasValue || sets.Value.ValueKind == JsonValueKind.Null || sets.Value.ValueKind == JsonValueKind.Undefined)
                return true;
            if (sets.Value.ValueKind != JsonValueKind.Object)
            {
                error = "sets 必须是对象:{ \"名字\": 数字或表达式字符串 }。";
                return false;
            }

            // 先收成待求值列表(保持声明顺序,方便报错时指出是哪个名字)。
            var pending = new List<KeyValuePair<string, string>>();
            foreach (var prop in sets.Value.EnumerateObject())
            {
                string name = prop.Name;
                if (!NamePattern.IsMatch(name))
                {
                    error = "sets 里的名字不合法: \"" + name + "\"(只允许字母/数字/下划线,且不能以数字开头)。";
                    return false;
                }
                if (pending.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)))
                {
                    error = "sets 里的名字重复: \"" + name + "\"(名字不区分大小写)。";
                    return false;
                }

                string expr;
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.Number:
                        expr = prop.Value.GetRawText();
                        break;
                    case JsonValueKind.String:
                        expr = prop.Value.GetString();
                        break;
                    default:
                        error = "sets[\"" + name + "\"] 的值必须是数字或表达式字符串,不能是 " + prop.Value.ValueKind + "。";
                        return false;
                }
                pending.Add(new KeyValuePair<string, string>(name, expr));
            }

            // 迭代求解:一轮里能算的都算掉,算不了(依赖还没出来)的留到下一轮。
            // 一旦某轮没有任何进展,剩下的必然是"引用了没定义的名字"或"互相成环"。
            while (pending.Count > 0)
            {
                var next = new List<KeyValuePair<string, string>>();
                bool progressed = false;

                foreach (var kv in pending)
                {
                    string missingName = null;
                    string prepared = PlaceholderPattern.Replace(kv.Value ?? string.Empty, m =>
                    {
                        string refName = m.Groups[1].Value;
                        if (acc.TryGetValue(refName, out double refValue))
                            return "(" + FormatNumber(refValue) + ")";
                        if (missingName == null) missingName = refName;
                        return m.Value;
                    });

                    if (missingName != null)
                    {
                        next.Add(kv);   // 依赖还没解出来,下一轮再试
                        continue;
                    }

                    double v;
                    string evalError;
                    if (!TryEvaluate(prepared, out v, out evalError))
                    {
                        error = "sets[\"" + kv.Key + "\"] 求值失败: " + evalError + "(表达式: " + kv.Value + ")";
                        return false;
                    }
                    acc[kv.Key] = v;
                    progressed = true;
                }

                if (!progressed)
                {
                    error = "sets 求值无法推进,以下名字引用了未定义的名字或互相成环: "
                        + string.Join(", ", next.Select(kv => "\"" + kv.Key + "\"")) + "。";
                    return false;
                }
                pending = next;
            }

            return true;
        }

        /// <summary>
        /// P2:按 doc op 把 features 切成若干建模段。
        /// 约定:doc op 只要出现就**起一个新段**,它自己不参与特征校验/构建。
        /// 第一个 doc op 之前若还有特征,那些特征构成段 0(沿用调用方的文档,老行为不变)。
        /// </summary>
        internal static bool SplitSegments(JsonElement[] features, out List<BuildSegment> segments, out string error)
        {
            segments = new List<BuildSegment>();
            error = null;
            if (features == null || features.Length == 0) return true;

            var cur = new BuildSegment { Doc = null, Name = "segment-0" };

            foreach (var f in features)
            {
                if (f.ValueKind != JsonValueKind.Object)
                {
                    error = "features 里的每一项都必须是对象,第 " + (cur.Index + 1) + " 段遇到 " + f.ValueKind + "。";
                    return false;
                }

                string kind = SegmentOpKind(f);
                if (kind != null)
                {
                    // 分段符:doc / measure / compute 各自独立成段。
                    // 上一段有内容就收尾(开头直接是分段符时,那个空段丢弃)。
                    if (cur.Doc.HasValue || cur.Action.HasValue || cur.Features.Count > 0)
                        segments.Add(cur);

                    if (kind == "doc")
                    {
                        string docError;
                        if (!CheckDocOp(f, out docError))
                        {
                            error = docError;
                            return false;
                        }
                        string segName = ReadString(f, "name");
                        cur = new BuildSegment
                        {
                            Doc = f,
                            Name = string.IsNullOrWhiteSpace(segName) ? ("segment-" + segments.Count) : segName
                        };
                    }
                    else
                    {
                        string asName = ReadString(f, "as");
                        cur = new BuildSegment
                        {
                            Action = f,
                            ActionOp = kind,
                            Name = string.IsNullOrWhiteSpace(asName) ? (kind + "-" + segments.Count) : asName
                        };
                    }
                    continue;
                }

                // 动作段(measure/compute)只装自己,后面来的特征必须另起一段,
                // 且这股新段沿用当前文档(不新建)。
                if (cur.Action.HasValue)
                {
                    segments.Add(cur);
                    cur = new BuildSegment { Name = "segment-" + segments.Count };
                }

                cur.Features.Add(f);
            }

            if (cur.Doc.HasValue || cur.Action.HasValue || cur.Features.Count > 0)
                segments.Add(cur);

            for (int i = 0; i < segments.Count; i++)
                segments[i].Index = i;

            return true;
        }

        /// <summary>分段符类型:"doc"/"measure"/"compute";普通特征返回 null。</summary>
        internal static string SegmentOpKind(JsonElement element)
        {
            string op = ReadString(element, "op");
            if (op == null) return null;
            if (string.Equals(op, "doc", StringComparison.OrdinalIgnoreCase)) return "doc";
            if (string.Equals(op, "measure", StringComparison.OrdinalIgnoreCase)) return "measure";
            if (string.Equals(op, "compute", StringComparison.OrdinalIgnoreCase)) return "compute";
            return null;
        }

        /// <summary>是不是文档分段符:{"op":"doc", ...}。</summary>
        internal static bool IsDocOp(JsonElement element)
        {
            return SegmentKindIs(element, "doc");
        }

        private static bool SegmentKindIs(JsonElement element, string kind)
        {
            if (element.ValueKind != JsonValueKind.Object) return false;
            return string.Equals(SegmentOpKind(element), kind, StringComparison.Ordinal);
        }

        /// <summary>
        /// P3:在运行时环境上求一个表达式(compute op 的实现)。可引用 sets 与前面 measure 出来的值。
        /// 仍然是"只算数、不动模型"——要改模型必须写后续声明式 op,护栏才管得住。
        /// </summary>
        internal static bool Evaluate(string expression, IReadOnlyDictionary<string, double> values,
            out double value, out string error)
        {
            value = 0;
            error = null;
            if (string.IsNullOrWhiteSpace(expression))
            {
                error = "表达式为空";
                return false;
            }

            string prepared = expression;
            if (expression.Contains("{{"))
            {
                string missing = null;
                prepared = PlaceholderPattern.Replace(expression, m =>
                {
                    string name = m.Groups[1].Value;
                    double v;
                    if (values != null && values.TryGetValue(name, out v))
                        return "(" + FormatNumber(v) + ")";
                    if (missing == null) missing = name;
                    return m.Value;
                });
                if (missing != null)
                {
                    error = "表达式引用了未定义的值 {{" + missing + "}}"
                        + (values != null && values.Count > 0
                            ? "(当前可用: " + string.Join(", ", values.Keys) + ")"
                            : "(还没有 sets/measure 产生的值)");
                    return false;
                }
            }

            return TryEvaluate(prepared, out value, out error);
        }

        /// <summary>doc op 的结构校验(纯结构:类型/字段类型;能不能建出来交给 COM 那步报)。</summary>
        internal static bool CheckDocOp(JsonElement doc, out string error)
        {
            error = null;
            if (!string.IsNullOrEmpty(ReadString(doc, "save")))
            {
                string save = ReadString(doc, "save");
                if (string.IsNullOrWhiteSpace(save))
                {
                    error = "doc op 的 save 是空字符串;要么给一个文件路径,要么不写 save。";
                    return false;
                }
            }
            JsonElement tmp;
            if (doc.TryGetProperty("type", out tmp) && tmp.ValueKind != JsonValueKind.String)
            {
                error = "doc op 的 type 必须是字符串(part/assembly/draft/sheetmetal/weldment)。";
                return false;
            }
            if (doc.TryGetProperty("overwrite", out tmp) && tmp.ValueKind != JsonValueKind.True && tmp.ValueKind != JsonValueKind.False)
            {
                error = "doc op 的 overwrite 必须是 true/false。";
                return false;
            }
            if (doc.TryGetProperty("name", out tmp) && tmp.ValueKind != JsonValueKind.String)
            {
                error = "doc op 的 name 必须是字符串(仅用于段名/报错定位,不是文档标题)。";
                return false;
            }
            return true;
        }

        internal static string ReadString(JsonElement element, string property)
        {
            JsonElement v;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out v))
                return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        /// <summary>
        /// 把占位符换成数字文本。两条规则(与 JSON 语法强相关,必须记住):
        ///   ① 整段构成字符串值 → 连引号一起吃成数字字面量:"depth":"{{t}}" → "depth":0.01
        ///      (数组元素同理:"rect":[["{{x}}","{{y}}"]] 也可以)
        ///   ② 嵌在字符串里 → 只换文本、引号保留:"save":"E:\\out\\{{idx}}.par" → "E:\\out\\1.par"
        /// 数值字段想写算式,请把它写进 sets(如 "t":"2*0.005"),不要在字符串里拼("0.05*{{x}}"
        /// 只会得到字符串,下游会按类型错误报出来)。
        /// 名字没定义一律当场报错,绝不半替换。
        /// </summary>
        internal static bool Substitute(JsonElement[] features, IReadOnlyDictionary<string, double> values,
            out JsonElement[] resolved, out string error)
        {
            resolved = features;
            error = null;
            if (features == null || features.Length == 0) return true;

            string text = JsonSerializer.Serialize(features);
            if (!text.Contains("{{"))
                return true;    // 没有占位符,原样返回(保持 JsonElement 身份,零额外分配)

            string missingName = null;
            Func<Match, string> resolve = m =>
            {
                string name = m.Groups[1].Value;
                double v;
                if (values != null && values.TryGetValue(name, out v))
                    return FormatNumber(v);
                if (missingName == null) missingName = name;
                return m.Value;
            };

            // 第一步:整段构成的字符串值 → 连引号吃掉,变成数字字面量("depth":"{{t}}" → "depth":0.01)。
            var evaluator = new MatchEvaluator(resolve);
            string replaced = QuotedPlaceholderPattern.Replace(text, evaluator);
            // 第二步:剩下的(嵌在字符串里的,如 "save":"E:\\out\\{{idx}}.par")按文本替换,引号保留。
            replaced = PlaceholderPattern.Replace(replaced, evaluator);

            if (missingName != null)
            {
                error = "features 里用了占位符 {{" + missingName + "}},但 sets 里没有定义这个名字"
                    + (values == null || values.Count == 0 ? "(本次没传 sets)。" : "。当前已定义: "
                        + string.Join(", ", values.Keys.Select(k => "{{" + k + "}}")) + "。");
                return false;
            }

            try
            {
                using (var doc = JsonDocument.Parse(replaced))
                {
                    var list = new List<JsonElement>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                        list.Add(item.Clone());
                    resolved = list.ToArray();
                    return true;
                }
            }
            catch (JsonException ex)
            {
                error = "占位符替换后的 features 不是合法 JSON(通常是把占位符写在了字符串字面量里,"
                    + "或表达式结果撑破了结构): " + ex.Message;
                return false;
            }
        }

        /// <summary>NCalc 求值。只收有限实数——NaN/Infinity 一律当错误,不往下游传。</summary>
        private static bool TryEvaluate(string expr, out double value, out string error)
        {
            value = 0;
            error = null;
            if (string.IsNullOrWhiteSpace(expr))
            {
                error = "表达式为空";
                return false;
            }
            try
            {
                var e = new Expression(expr);
                if (e.HasErrors())
                {
                    error = e.Error;
                    return false;
                }
                object r = e.Evaluate();
                if (r == null)
                {
                    error = "求值结果为空";
                    return false;
                }
                value = Convert.ToDouble(r, CultureInfo.InvariantCulture);
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    error = "求值结果不是有限实数";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 数字 → JSON 数字文本。用 "R"(可精确回读的最短表示)+ 不变文化,
        /// 避免区域设置把小数点写成逗号、把 JSON 弄坏。
        /// </summary>
        internal static string FormatNumber(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
