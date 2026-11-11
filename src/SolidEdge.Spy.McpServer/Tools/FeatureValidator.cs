using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>规则默认级别。三档是为将来 YAML 规则配置预留的接口,本次不实现配置本身。</summary>
    public enum Severity
    {
        /// <summary>关闭该规则。</summary>
        Off = 0,

        Warning = 1,
        Error = 2
    }

    /// <summary>
    /// 一条校验问题。给 LLM 消费的,所以 <see cref="fix"/> 比 message 更重要——
    /// 光说"这里错了"没用,得说"改成什么"。
    /// </summary>
    public sealed class Issue
    {
        /// <summary>规则编号,如 E401 / W401。</summary>
        public string code { get; set; }

        /// <summary>"error" / "warning"。</summary>
        public string level { get; set; }

        /// <summary>features 数组中的序号(0-based)。</summary>
        public int feature { get; set; }

        public string featureName { get; set; }

        public string op { get; set; }

        /// <summary>出问题的字段名,如 "plane" / "depth" / "loops[0]"。</summary>
        public string field { get; set; }

        /// <summary>中文描述。</summary>
        public string message { get; set; }

        /// <summary>结构化修复建议(可为 null)。例:{"action":"merge_into_previous","target":2,"field":"loops"}</summary>
        public object fix { get; set; }
    }

    /// <summary>校验报告。</summary>
    public sealed class ValidationReport
    {
        /// <summary>"ok" / "warning" / "error"。</summary>
        public string status { get; set; }

        /// <summary>规则集版本。规则库演进时靠它区分行为。</summary>
        public string version { get; set; }

        public int featureCount { get; set; }
        public int errorCount { get; set; }
        public int warningCount { get; set; }

        public List<Issue> issues { get; set; } = new List<Issue>();

        public bool HasError
        {
            get { return errorCount > 0; }
        }

        /// <summary>
        /// 序列化。刻意用 UnsafeRelaxedJsonEscaping:报告是给 LLM 直接读的,
        /// 默认的 \uXXXX 转义会把中文 message 变成一串看不懂的转义,白白浪费可读性。
        /// (项目其它工具仍用默认编码器,这里只为报告破例。)
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(new
            {
                status = status,
                version = version,
                featureCount = featureCount,
                errorCount = errorCount,
                warningCount = warningCount,
                hasError = HasError,
                issues = issues
            }, FeatureValidator.JsonOpts);
        }
    }

    /// <summary>
    /// 校验上下文:规则拿到的全部输入。
    /// 规则是纯函数——只看 ctx,不碰 COM,所以 SE 没启动也能跑。
    /// </summary>
    public sealed class ValidationContext
    {
        public List<FeatureSpec> Specs;
        public FeatureSpec Current;
        public int Index;

        /// <summary>本批 plane op 定义的别名 → 定义所在序号。用于 @name 的未定义/前向引用判定。</summary>
        public Dictionary<string, int> DefinedPlanes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// true = 目标文档里已经有实体(增量建模)。此时"本批第一个特征就是除料"是合法流程,
        /// E401 不该拦(2026-10-23 真实建模任务实测:在已有底板单独打一个孔被误拒)。
        /// 校验器本身仍是纯函数,这个事实只能由调用方告知。
        /// </summary>
        public bool AssumeStock;

        /// <summary>
        /// 本批【所有 op 带 name】定义的特征别名 → 定义所在序号(P3 新增)。
        /// 用于 faceOf 的 @别名 引用未定义/前向引用判定(E204)。
        /// 与 DefinedPlanes 并列:plane op 同时进两表;非 plane op 只进本表。
        /// </summary>
        public Dictionary<string, int> DefinedFeatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Issue Error(string code, string field, string message, object fix = null)
        {
            return Make(code, "error", field, message, fix);
        }

        public Issue Warn(string code, string field, string message, object fix = null)
        {
            return Make(code, "warning", field, message, fix);
        }

        private Issue Make(string code, string level, string field, string message, object fix)
        {
            return new Issue
            {
                code = code,
                level = level,
                feature = Index,
                featureName = Current != null ? Current.Name : null,
                op = Current != null ? Current.Op : null,
                field = field,
                message = message,
                fix = fix
            };
        }
    }

    /// <summary>
    /// 一条校验规则。新增规则 = 加一个类,不动已有代码。
    ///
    /// 关键:<see cref="AppliesTo"/> 挂的是 op(如 {"extrude","cut"} 或 {"*"}),
    /// 但规则要尽量写成"语法单元级"的(引用可解析 / 几何合法 / 量纲合理),
    /// 这样将来加 revolve 之类新 op 时,通用规则自动生效,只需补 op 专属的少数几条。
    /// </summary>
    public interface IFeatureRule
    {
        /// <summary>规则编号(注册用)。规则内部可对同一语义按级别发不同编号,如 E202/W202。</summary>
        string Code { get; }

        Severity DefaultLevel { get; }

        /// <summary>{"*"} 表示所有 op。</summary>
        string[] AppliesTo { get; }

        IEnumerable<Issue> Check(ValidationContext ctx);
    }

    /// <summary>
    /// features JSON 的静态校验器。
    ///
    /// 目的:把"僵尸特征"(API 调用成功、Status=1216476311、几何却没生成)的发现时点
    /// 从【事后看 Status】提前到【灌进 Solid Edge 之前】。
    ///
    /// 不碰 COM、不改文档,SE 未启动也能跑。
    /// 与构建器共用 <see cref="FeatureSpecParser"/> 的解析结果,杜绝"校验通过但构建行为不同"。
    /// </summary>
    public static class FeatureValidator
    {
        /// <summary>
        /// 报告专用序列化选项:不转义中文(报告是给 LLM 直接读的,\uXXXX 会毁掉可读性)。
        /// `se_model_build` 回传报告时也要用这一份,别用默认编码器。
        /// </summary>
        public static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>规则集版本。规则增删/改级时 +1。</summary>
        public const string Version = "1.5";   // 1.5:assumeStock(已有实体上增量除料不再被 E401 误拦);1.4:P3 面引用机制(E204 FeatureRefRule;DefinedFeatures 字典;draft/split/web_network/thicken/delete_face 五 op)

        /// <summary>零件文档默认参考面个数。中文版 DisplayName 是「参考平面_N」,只能按索引取。</summary>
        public const int DefaultRefPlaneCount = 3;

        /// <summary>米制下"大到可疑"的坐标阈值(米)。100 米对零件而言基本就是单位搞错了。</summary>
        public const double SuspiciousCoordAbs = 100.0;

        /// <summary>米制下"大到可疑"的深度阈值(米)。</summary>
        public const double SuspiciousDepthAbs = 10.0;

        /// <summary>小于此面积(平方米)视为退化轮廓。1e-9 m² = 0.001 mm² 。</summary>
        public const double DegenerateArea = 1e-9;

        /// <summary>相邻点距离小于此值(米)视为重合。1e-9 m = 1 nm。</summary>
        public const double CoincidentEpsilon = 1e-9;

        private static List<IFeatureRule> _rules;

        public static List<IFeatureRule> Rules
        {
            get { return _rules ?? (_rules = RuleRegistry.Build()); }
        }

        public static ValidationReport Validate(JsonElement[] features, bool assumeStock = false)
        {
            return Validate(FeatureSpecParser.ParseAll(features), assumeStock);
        }

        public static ValidationReport Validate(List<FeatureSpec> specs, bool assumeStock = false)
        {
            var report = new ValidationReport
            {
                version = Version,
                featureCount = specs.Count
            };

            // 别名表先全量建好,规则自己按序号判断"是不是前向引用"。
            var defined = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var definedFeatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < specs.Count; i++)
            {
                var s = specs[i];
                if (s.OpLower == "plane" && !string.IsNullOrEmpty(s.Name))
                {
                    if (!defined.ContainsKey(s.Name))
                        defined[s.Name] = i;
                }
                // P3:所有 op 带 name 都进 DefinedFeatures(供 faceOf @别名 引用判定)
                if (!string.IsNullOrEmpty(s.Name))
                {
                    if (!definedFeatures.ContainsKey(s.Name))
                        definedFeatures[s.Name] = i;
                }
            }

            for (int i = 0; i < specs.Count; i++)
            {
                var ctx = new ValidationContext
                {
                    Specs = specs,
                    Current = specs[i],
                    Index = i,
                    DefinedPlanes = defined,
                    DefinedFeatures = definedFeatures,
                    AssumeStock = assumeStock
                };

                foreach (var rule in Rules)
                {
                    if (rule.DefaultLevel == Severity.Off) continue;
                    if (!Applies(rule, specs[i].OpLower)) continue;

                    try
                    {
                        foreach (var issue in rule.Check(ctx))
                        {
                            if (issue != null) report.issues.Add(issue);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 规则自身抛异常不能带走整次校验——降级成一条 error,保证调用方拿得到结果。
                        report.issues.Add(ctx.Error("E000", null,
                            "规则 " + rule.Code + " 执行异常: " + ex.Message, null));
                    }
                }
            }

            foreach (var it in report.issues)
            {
                if (it.level == "error") report.errorCount++;
                else report.warningCount++;
            }

            report.status = report.errorCount > 0 ? "error"
                : (report.warningCount > 0 ? "warning" : "ok");

            return report;
        }

        /// <summary>从 JSON 文本校验。接受 [ ... ] 或 {"features":[...]}。</summary>
        public static ValidationReport ValidateJson(string json, bool assumeStock = false)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                JsonElement featsEl = root;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("features", out var fEl))
                    featsEl = fEl;

                if (featsEl.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("JSON 必须是数组或 {\"features\":[...]}。");

                var list = new List<JsonElement>();
                foreach (var e in featsEl.EnumerateArray())
                    list.Add(e);

                return Validate(list.ToArray(), assumeStock);
            }
        }

        private static bool Applies(IFeatureRule rule, string opLower)
        {
            var a = rule.AppliesTo;
            if (a == null || a.Length == 0) return true;
            for (int i = 0; i < a.Length; i++)
                if (a[i] == "*" || string.Equals(a[i], opLower, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// 手写二维几何工具。不引第三方几何库——
    /// 需要的只是"点距 / 线段相交 / 鞋带面积"三件事,共几十行,而自交必须能报出
    /// 【第几条边 × 第几条边】,现成库反而要绕一圈才能拿到。
    ///
    /// 全部在【平面局部 u/v 二维坐标】下工作,不做全局投影(见建模配方:草图坐标不是全局 XYZ)。
    /// </summary>
    public static class GeoUtil
    {
        public static double Dist(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// 线段 p1p2 与 p3p4 是否真相交(不含仅端点接触、共线重叠按相交处理)。
        /// 相交时回写交点,便于报错时给出位置。
        /// </summary>
        public static bool SegmentIntersect(double[] p1, double[] p2, double[] p3, double[] p4,
            out double ix, out double iy)
        {
            ix = iy = 0;
            double d1x = p2[0] - p1[0], d1y = p2[1] - p1[1];
            double d2x = p4[0] - p3[0], d2y = p4[1] - p3[1];
            double denom = d1x * d2y - d1y * d2x;

            if (Math.Abs(denom) < 1e-12)
                return false;   // 平行/共线:交由"退化面积/重合点"规则处理,这里不算自交

            double t = ((p3[0] - p1[0]) * d2y - (p3[1] - p1[1]) * d2x) / denom;
            double u = ((p3[0] - p1[0]) * d1y - (p3[1] - p1[1]) * d1x) / denom;

            // 用开区间 + 容差:仅端点接触(相邻边)不算自交
            const double eps = 1e-9;
            if (t <= eps || t >= 1 - eps || u <= eps || u >= 1 - eps)
                return false;

            ix = p1[0] + t * d1x;
            iy = p1[1] + t * d1y;
            return true;
        }

        /// <summary>鞋带公式:有符号面积。正 = 逆时针,负 = 顺时针。</summary>
        public static double SignedArea(double[][] pts)
        {
            double s = 0;
            int n = pts.Length;
            for (int i = 0; i < n; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % n];
                s += a[0] * b[1] - b[0] * a[1];
            }
            return s / 2.0;
        }

        public static double[] BBox(double[][] pts)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p[0] < minX) minX = p[0];
                if (p[0] > maxX) maxX = p[0];
                if (p[1] < minY) minY = p[1];
                if (p[1] > maxY) maxY = p[1];
            }
            return new[] { minX, minY, maxX, maxY };
        }

        /// <summary>点是否在多边形内(射线法,边界算内)。</summary>
        public static bool PointInPolygon(double[] pt, double[][] poly)
        {
            bool inside = false;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i][0], yi = poly[i][1];
                double xj = poly[j][0], yj = poly[j][1];
                if (((yi > pt[1]) != (yj > pt[1])) &&
                    (pt[0] < (xj - xi) * (pt[1] - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }

        public static bool BBoxOverlap(double[] a, double[] b)
        {
            return !(a[2] < b[0] || b[2] < a[0] || a[3] < b[1] || b[3] < a[1]);
        }

        public static string Fmt(double v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
