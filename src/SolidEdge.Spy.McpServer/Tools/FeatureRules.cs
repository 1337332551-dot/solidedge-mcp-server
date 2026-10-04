using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 规则注册表。新增一条规则 = 加一个类 + 在这里 new 一行,不动其它规则。
    ///
    /// 编号体系:
    ///   E1xx/W1xx 结构   E2xx/W2xx 引用   E3xx/W3xx 几何   E4xx/W4xx 语义
    /// E000 = 规则自身异常(兜底)。
    ///
    /// 分级原则:**宁 warning 不 error**——只有"必然失败"的才 error,
    /// 否则误报会打断 LLM 的流程,比漏报更烦人。
    /// </summary>
    public static class RuleRegistry
    {
        public static List<IFeatureRule> Build()
        {
            return new List<IFeatureRule>
            {
                // ---- 结构层 ----
                new OpRule(),                 // E101 未知 op
                new RequiredShapeRule(),      // E102 缺草图形状
                new FieldTypeRule(),          // E103 字段类型错误 / E305 circle 半径非法
                new NonFiniteRule(),          // E104 NaN / Infinity
                new DimensionRule(),          // W101 量纲可疑
                new UnknownFieldRule(),       // W102 未知字段

                // ---- 引用层 ----
                new PlaneRefRule(),           // E201/E202/W202/E203/W204

                // ---- 几何层 ----
                new LoopGeometryRule(),       // E301/E302/E303/E304/W301

                // ---- 语义层 ----
                new CutBeforeExtrudeRule(),   // E401
                new ExtrudeDepthRule(),       // E402
                new RevolveAxisRule(),        // E403 revolve 缺轴 / 轴退化 / 角度越界
                new CutModeRule(),            // E404 finite 缺 depth / W405 未指定 mode(默认会切穿)
                new SideDirectionRule(),      // E105/W402
                new DirVolumeFieldRule(),     // W409 dir 解析失败 / E418 expectvolumedelta 解析失败
                new ConsecutiveCutRule(),     // W401 同平面多孔未合并(僵尸头号杀手)
                new CutOutsideStockRule(),    // W403 除料落在毛坯外
                new LoopOverlapRule(),        // W404 环重叠
                new ConstraintBindingRule(),  // W406 声明与形状不匹配 / W407 dims element 越界

                // ---- 扩 op(2026-09-22):fillet / chamfer / rib / pattern ----
                new EdgeRefRule(),            // E205 边引用缺失/结构错/项解析失败
                new FilletRule(),             // E405 fillet 缺 radius / <=0
                new ChamferRule(),            // E406 chamfer 缺 distance / <=0
                new RibRule(),                // E407 rib 缺 thickness / 轮廓不是开放链形状
                new PatternRule(),            // E408 pattern 缺 of/counts/spacing 非法 / W408 of 本批内找不到

                // ---- 扩 op(2026-09-23 P1):hole ----
                new HoleRule(),               // E409 hole 形状只接受圆 / mode 非法 / finite 缺 depth

                // ---- 扩 op(2026-09-23 P2):loft / sweep / helix ----
                new LoftRule(),               // E410 loft 多轮廓声明(profiles 数量/形状/plane)
                new SweepRule(),              // E411 sweep path+截面声明
                new HelixRule(),              // E412 helix 参数(pitch/height/revolutions 二给一)
                // 2026-09-23 P3 面引用机制:draft / split / web_network / thicken / delete_face
                new FeatureRefRule(),         // E204 faceOf 引用未定义/前向引用/解析失败
                new DraftRule(),              // E413 draft angle/side(只认 4/5)
                new SplitRule(),              // E414 split target(@别名/obj-K)
                new WebNetworkRule(),         // E415 web_network thickness/depth > 0
                new ThickenRule(),            // E416 thicken thickness > 0
                new DeleteFaceRule()           // E417 delete_face confirm 必须 true
            };
        }
    }

    // ==================== 结构层 ====================

    /// <summary>E101:op 必须是 plane / extrude / cut / revolve / fillet / chamfer / rib / pattern / hole / loft / sweep / helix。</summary>
    public sealed class OpRule : IFeatureRule
    {
        public string Code { get { return "E101"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (s.OpLower == "plane" || s.OpLower == "extrude" || s.OpLower == "cut" || s.OpLower == "revolve" ||
                s.OpLower == "fillet" || s.OpLower == "chamfer" || s.OpLower == "rib" || s.OpLower == "pattern" ||
                s.OpLower == "hole" || s.OpLower == "loft" || s.OpLower == "sweep" || s.OpLower == "helix" ||
                s.OpLower == "draft" || s.OpLower == "split" || s.OpLower == "web_network" ||
                s.OpLower == "extrude_surface" || s.OpLower == "thicken" || s.OpLower == "delete_face")
                yield break;

            yield return ctx.Error("E101", "op",
                "未知 op \"" + s.Op + "\",仅支持 plane / extrude / cut / revolve / fillet / chamfer / rib / pattern / hole / loft / sweep / helix / draft / split / web_network / extrude_surface / thicken / delete_face。",
                new { action = "set", field = "op", allowed = new[] { "plane", "extrude", "cut", "revolve", "fillet", "chamfer", "rib", "pattern", "hole", "loft", "sweep", "helix", "draft", "split", "web_network", "extrude_surface", "thicken", "delete_face" } });
        }
    }

    /// <summary>E102:extrude / cut / revolve / helix 必须有可解析的草图形状(circle / circles / slot / rect / polygon / loops 六选一)。</summary>
    public sealed class RequiredShapeRule : IFeatureRule
    {
        public string Code { get { return "E102"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut", "revolve", "helix", "extrude_surface", "web_network" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (s.HasCircle || s.HasCircles || s.HasSlot) yield break;
            if (s.ShapeError == null) yield break;

            // 2026-09-30 v2.1:去掉与 ShapeError 重复的六形状罗列,改为给可复制的正确写法
            //(形状键平铺在特征对象顶层;嵌套 shape 是 se_extrude_on_face 的入参写法,勿串味)。
            // field 名保持 "shape" 不改——E409/E412 等 5 处一致使用,单改此处会制造跨规则不一致。
            yield return ctx.Error("E102", "shape", s.ShapeError +
                " 形状键平铺在特征对象顶层(无 shape 包装),如 \"rect\":[[0,0],[0.1,0.1]];嵌套 shape:{...} 是 se_extrude_on_face 的入参写法。",
                new { action = "provide", field = "shape", oneOf = new[] { "circle", "circles", "slot", "rect", "polygon", "loops" } });
        }
    }

    /// <summary>
    /// E403:revolve / helix 必须有旋转轴 axis,且轴不能退化;revolve 的角度必须合法。
    ///
    /// 旋转类是唯一"多一个自由度"的 op 族:轮廓之外还得有轴,少了轴整个特征无从定义,
    /// 所以是 error 而不是 warning。角度超过一整圈 AddFinite 也接受不了(helix 无角度语义,跳过)。
    /// </summary>
    public sealed class RevolveAxisRule : IFeatureRule
    {
        public string Code { get { return "E403"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "revolve", "helix" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (!s.HasAxis)
            {
                bool wrote = s.Raw.ValueKind == JsonValueKind.Object && s.Raw.TryGetProperty("axis", out _);

                yield return ctx.Error("E403", "axis",
                    wrote
                        ? "旋转轴两点重合(退化成点),无法确定旋转方向。"
                        : s.OpLower + " 缺少 axis(旋转轴两点)。",
                    new
                    {
                        action = wrote ? "fix" : "provide",
                        field = "axis",
                        format = "[[u1,v1],[u2,v2]] 或 [u1,v1,u2,v2]",
                        hint = "轴是草图平面内的直线,如沿局部 v 轴:[[0,0],[0,0.05]]"
                    });
                yield break;
            }

            if (s.OpLower != "revolve") yield break;   // helix 无角度语义

            double angle = s.Degrees.HasValue
                ? s.Degrees.Value * Math.PI / 180.0
                : (s.Angle.HasValue ? s.Angle.Value : 2.0 * Math.PI);

            if (!(angle > 0))
            {
                yield return ctx.Error("E403", "angle",
                    "旋转角必须 > 0(当前 " + GeoUtil.Fmt(angle) + " 弧度)。",
                    new { action = "set", field = "angle", value = 6.283185307179586 });
            }
            else if (angle > 2.0 * Math.PI + 1e-9)
            {
                yield return ctx.Error("E403", "angle",
                    "旋转角超过一整圈(2π 弧度 / 360 度)。",
                    new { action = "clamp", field = "angle", max = 6.283185307179586 });
            }
        }
    }

    /// <summary>
    /// E404 / W405:cut 的除料模式,两条都是"静默默认值"陷阱。
    ///
    ///   W405(警告)——没写 mode:默认走 AddThroughNext(切到下一个面)。在单块实体上
    ///     这等于【切穿】,而多数人开门窗要的是定深凹槽。不阻拦,但必须提醒。
    ///   E404(错误)——mode="finite" 却没给 depth:构建器会静默套用 0.2 米默认值,
    ///     几乎肯定不是用户想要的尺寸,直接拦下要求写明。
    /// </summary>
    public sealed class CutModeRule : IFeatureRule
    {
        public string Code { get { return "E404"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            string mode = (s.Mode ?? "").Trim().ToLowerInvariant();

            if (mode == "finite")
            {
                if (!s.Depth.HasValue)
                {
                    yield return ctx.Error("E404", "depth",
                        "mode=\"finite\" 必须给 depth(米);否则会静默套用 0.2 米的默认值。",
                        new { action = "provide", field = "depth", hint = "如 \"depth\":0.02 = 切 20mm 深的盲槽" });
                }
                yield break;
            }

            if (mode == "" || mode == "next")
            {
                yield return ctx.Warn("W405", "mode",
                    "cut 未指定 mode,默认走 AddThroughNext(切到下一个面)——在单块实体上就是切穿。" +
                    "若要定深凹槽/盲孔,请用 \"mode\":\"finite\" 并给出 depth。",
                    new { action = "set", field = "mode", value = "finite", also = "depth" });
            }
        }
    }

    /// <summary>
    /// E103:字段类型错误(给了但解析器读不出来,会被静默按默认值处理)。
    /// 这类错误最阴——表面"跑通了",实际参数根本没生效。
    /// </summary>
    public sealed class FieldTypeRule : IFeatureRule
    {
        public string Code { get { return "E103"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        private static readonly string[] StringFields = { "op", "name", "plane", "base", "mode", "endmode", "of" };
        private static readonly string[] NumberFields = { "depth", "distance", "angle", "degrees", "radius", "thickness", "xspacing", "yspacing" };
        private static readonly string[] IntFields = { "side", "profileside", "xcount", "ycount" };

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.Raw.ValueKind != JsonValueKind.Undefined && s.Raw.ValueKind != JsonValueKind.Object)
            {
                yield return ctx.Error("E103", null,
                    "特征必须是 JSON 对象,实际是 " + s.Raw.ValueKind + "。",
                    new { action = "wrap", field = (string)null });
                yield break;
            }

            foreach (var f in StringFields)
                if (BadString(s, f))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是字符串。",
                        new { action = "set_type", field = f, type = "string" });

            foreach (var f in NumberFields)
                if (BadNumber(s, f))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是数字(或数字字符串)。",
                        new { action = "set_type", field = f, type = "number" });

            foreach (var f in IntFields)
                if (BadInt(s, f))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是整数(或整数字符串)。",
                        new { action = "set_type", field = f, type = "int" });

            if (BadBool(s, "visible"))
                yield return ctx.Error("E103", "visible", "字段 \"visible\" 必须是布尔值。",
                    new { action = "set_type", field = "visible", type = "bool" });

            // 约束/标注声明(2026-09-13 新增字段):类型读不出来同样会被静默按默认值处理
            foreach (var f in new[] { "autoconstraint", "fixorigin" })
                if (BadBool(s, f))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是布尔值。",
                        new { action = "set_type", field = f, type = "bool" });

            foreach (var iss in CheckDims(ctx, s))
                yield return iss;

            if (BadShapeKind(s, "circle", true))
                yield return ctx.Error("E103", "circle",
                    "字段 \"circle\" 必须是对象 {\"center\":[x,y],\"radius\":r} 或数组 [x,y,r]。", null);
            if (BadShapeKind(s, "circles", false))
                yield return ctx.Error("E103", "circles",
                    "字段 \"circles\" 必须是圆数组 [[x,y,r], ...]。", null);
            foreach (var f in new[] { "rect", "polygon", "loops" })
                if (BadShapeKind(s, f, false))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是点列数组。", null);
        }

        private static bool TryRaw(FeatureSpec s, string name, out JsonElement el)
        {
            el = default(JsonElement);
            if (s.Raw.ValueKind != JsonValueKind.Object) return false;
            return s.Raw.TryGetProperty(name, out el);
        }

        private static bool BadString(FeatureSpec s, string f)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            return el.ValueKind != JsonValueKind.String && el.ValueKind != JsonValueKind.Null;
        }

        private static bool BadNumber(FeatureSpec s, string f)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            if (el.ValueKind == JsonValueKind.Number) return false;
            if (el.ValueKind == JsonValueKind.String)
                return !double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            return el.ValueKind != JsonValueKind.Null;
        }

        private static bool BadInt(FeatureSpec s, string f)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            if (el.ValueKind == JsonValueKind.Number) return !el.TryGetInt32(out _);
            if (el.ValueKind == JsonValueKind.String)
                return !int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            return el.ValueKind != JsonValueKind.Null;
        }

        private static bool BadBool(FeatureSpec s, string f)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            if (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String) return !bool.TryParse(el.GetString(), out _);
            return el.ValueKind != JsonValueKind.Null;
        }

        /// <summary>
        /// dims 的结构类型(dims 必是数组 / 每项必是对象 / element 必是整数 / name·value·formula 必是字符串)。
        /// 类型读不出来就是"静默不生效"——正是 E103 要拦的那一类。
        /// 注意 element 若写成 1.5 这类小数,解析器的 GetInt32 会直接抛异常,所以这里必须提前拦。
        /// </summary>
        private static IEnumerable<Issue> CheckDims(ValidationContext ctx, FeatureSpec s)
        {
            JsonElement dimsEl;
            if (!TryRaw(s, "dims", out dimsEl) || dimsEl.ValueKind == JsonValueKind.Null) yield break;

            if (dimsEl.ValueKind != JsonValueKind.Array)
            {
                yield return ctx.Error("E103", "dims",
                    "字段 \"dims\" 必须是数组(每项 {\"element\":n,\"name\":\"...\",\"value\"|\"formula\":\"...\"})。",
                    new { action = "set_type", field = "dims", type = "array" });
                yield break;
            }

            int k = 0;
            foreach (var item in dimsEl.EnumerateArray())
            {
                string fld = "dims[" + k + "]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    yield return ctx.Error("E103", fld,
                        "dims 第 " + (k + 1) + " 项必须是对象。",
                        new { action = "set_type", field = fld, type = "object" });
                }
                else
                {
                    JsonElement el;
                    if (item.TryGetProperty("element", out el) && el.ValueKind != JsonValueKind.Null)
                    {
                        bool ok = el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out _);
                        if (!ok)
                            yield return ctx.Error("E103", fld + ".element",
                                "dims 第 " + (k + 1) + " 项的 element 必须是整数(0-based 线索引)。",
                                new { action = "set_type", field = fld + ".element", type = "int" });
                    }

                    foreach (var f in new[] { "name", "value", "formula" })
                        if (BadStrEl(item, f))
                            yield return ctx.Error("E103", fld + "." + f,
                                "dims 第 " + (k + 1) + " 项的 \"" + f + "\" 必须是字符串。",
                                new { action = "set_type", field = fld + "." + f, type = "string" });
                }
                k++;
            }
        }

        private static bool BadStrEl(JsonElement obj, string f)
        {
            if (obj.ValueKind != JsonValueKind.Object) return false;
            JsonElement el;
            if (!obj.TryGetProperty(f, out el)) return false;
            return el.ValueKind != JsonValueKind.String && el.ValueKind != JsonValueKind.Null;
        }

        private static bool BadShapeKind(FeatureSpec s, string f, bool allowObject)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            if (el.ValueKind == JsonValueKind.Array) return false;
            if (allowObject && el.ValueKind == JsonValueKind.Object) return false;
            return el.ValueKind != JsonValueKind.Null;
        }
    }

    /// <summary>E104:NaN / Infinity 会直接把 COM 调用带沟里,必须拦在前面。</summary>
    public sealed class NonFiniteRule : IFeatureRule
    {
        public string Code { get { return "E104"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.Depth.HasValue && !IsFinite(s.Depth.Value))
                yield return ctx.Error("E104", "depth", "depth 不是有限数值。", null);
            if (s.HasDistance && !IsFinite(s.Distance))
                yield return ctx.Error("E104", "distance", "distance 不是有限数值。", null);
            if (s.HasCircle && (!IsFinite(s.CircleR) || !IsFinite(s.CircleX) || !IsFinite(s.CircleY)))
                yield return ctx.Error("E104", "circle", "circle 的圆心/半径不是有限数值。", null);

            for (int i = 0; i < s.Loops.Count; i++)
            {
                foreach (var p in s.Loops[i])
                {
                    if (!IsFinite(p[0]) || !IsFinite(p[1]))
                    {
                        yield return ctx.Error("E104", "loops[" + i + "]",
                            "第 " + (i + 1) + " 个环含非有限坐标。", null);
                        break;
                    }
                }
            }
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }

    /// <summary>
    /// W101:量纲哨兵。单位是【米】,零件尺度下坐标 &gt;100m 或深度 &gt;10m 基本都是把 mm 当 m 写了。
    /// 这是 LLM 最高频的单位错误,值得单独一条。
    /// </summary>
    public sealed class DimensionRule : IFeatureRule
    {
        public string Code { get { return "W101"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            const double coordMax = FeatureValidator.SuspiciousCoordAbs;
            const double depthMax = FeatureValidator.SuspiciousDepthAbs;

            if (s.HasCircle)
            {
                if (Math.Abs(s.CircleX) > coordMax || Math.Abs(s.CircleY) > coordMax || Math.Abs(s.CircleR) > coordMax)
                    yield return ctx.Warn("W101", "circle",
                        "圆心/半径达到 " + GeoUtil.Fmt(Math.Max(Math.Abs(s.CircleX), Math.Abs(s.CircleR))) +
                        " 米,疑似单位用错(应为米,不是毫米)。",
                        new { action = "rescale", field = "circle", from = "mm", to = "m", factor = 0.001 });
            }

            if (s.HasSlot && (Math.Abs(s.SlotX) > coordMax || Math.Abs(s.SlotY) > coordMax ||
                              Math.Abs(s.SlotLength) > coordMax || Math.Abs(s.SlotWidth) > coordMax))
                yield return ctx.Warn("W101", "slot",
                    "slot 中心/尺寸达到 " + GeoUtil.Fmt(Math.Max(Math.Max(Math.Abs(s.SlotX), Math.Abs(s.SlotY)),
                        Math.Max(Math.Abs(s.SlotLength), Math.Abs(s.SlotWidth)))) + " 米,疑似单位用错(应为米,不是毫米)。",
                    new { action = "rescale", field = "slot", from = "mm", to = "m", factor = 0.001 });

            for (int i = 0; i < s.Loops.Count; i++)
            {
                var bb = GeoUtil.BBox(s.Loops[i]);
                double m = Math.Max(Math.Max(Math.Abs(bb[0]), Math.Abs(bb[1])), Math.Max(Math.Abs(bb[2]), Math.Abs(bb[3])));
                if (m > coordMax)
                    yield return ctx.Warn("W101", "loops[" + i + "]",
                        "第 " + (i + 1) + " 个环坐标达到 " + GeoUtil.Fmt(m) + " 米,疑似单位用错(应为米,不是毫米)。",
                        new { action = "rescale", field = "loops[" + i + "]", from = "mm", to = "m", factor = 0.001 });
            }

            if (s.Depth.HasValue && Math.Abs(s.Depth.Value) > depthMax)
                yield return ctx.Warn("W101", "depth",
                    "depth = " + GeoUtil.Fmt(s.Depth.Value) + " 米,疑似单位用错(应为米,不是毫米)。",
                    new { action = "rescale", field = "depth", from = "mm", to = "m", factor = 0.001 });

            if (s.HasDistance && Math.Abs(s.Distance) > depthMax)
                yield return ctx.Warn("W101", "distance",
                    "distance = " + GeoUtil.Fmt(s.Distance) + " 米,疑似单位用错。",
                    new { action = "rescale", field = "distance", from = "mm", to = "m", factor = 0.001 });
        }
    }

    /// <summary>W102:白名单外的字段。多半是拼错(如 "depht"),或用了尚未支持的字段。</summary>
    public sealed class UnknownFieldRule : IFeatureRule
    {
        public string Code { get { return "W102"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            foreach (var f in ctx.Current.UnknownFields)
            {
                // 2026-09-30 v2.1:高频误用键 shape 给 did-you-mean(踩坑那一刻教学,平时零常驻成本)。
                string hint = string.Equals(f, "shape", StringComparison.OrdinalIgnoreCase)
                    ? "本工具形状键平铺在特征对象顶层(无 shape 包装),如 \"rect\":[[0,0],[0.1,0.1]];嵌套 shape:{...} 是 se_extrude_on_face 的入参写法。"
                    : "";
                yield return ctx.Warn("W102", f,
                    "未知字段 \"" + f + "\",将被忽略(可能是拼写错误)。" + hint,
                    new { action = "remove_or_rename", field = f });
            }
        }
    }

    // ==================== 引用层 ====================

    /// <summary>
    /// E201 缺 plane/base · E202 RefPlane_N 索引非法 · W202 索引超出默认面个数 ·
    /// E203 @别名未定义/前向引用 · W204 别名重复定义。
    ///
    /// obj-K 句柄引用【不判】——句柄表在 server 进程里,静态校验看不到,强行判只会误报。
    /// </summary>
    public sealed class PlaneRefRule : IFeatureRule
    {
        public string Code { get { return "E201"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            bool isPlaneOp = s.OpLower == "plane";

            string ref_ = isPlaneOp ? s.BaseRef : s.PlaneRef;
            string field = isPlaneOp ? "base" : "plane";

            if (!isPlaneOp && s.OpLower != "extrude" && s.OpLower != "cut" &&
                s.OpLower != "rib" && s.OpLower != "pattern" && s.OpLower != "helix" &&
                s.OpLower != "draft" && s.OpLower != "web_network" && s.OpLower != "extrude_surface" &&
                s.OpLower != "hole") yield break;

            // 2026-10-04:coords 字段(face 平面坐标模式)。非法值 / 与非 face 平面共存在静态层拦下,
            // 别留到执行层才炸(执行层错误无法 dryRun 预演)。
            if (s.Coords != null && s.Coords != "global" && s.Coords != "local")
            {
                yield return ctx.Error("E203", "coords",
                    "coords 只能是 \"global\"(默认,全局世界坐标投影)或 \"local\"(面局部 u/v),收到 \"" + s.Coords + "\"。",
                    new { action = "set", field = "coords", example = "global / local" });
            }
            if (s.CoordsLocal &&
                !(ref_ != null && ref_.StartsWith("face:", StringComparison.OrdinalIgnoreCase)))
            {
                yield return ctx.Error("E203", "coords",
                    "coords:\"local\" 只在 plane 为 face:<ID>/face:±轴 时有意义(当前 " + field + "=\"" + ref_ +
                    "\")。普通参考平面本来就是面局部 u/v,无需 coords。",
                    new { action = "set", field = "coords" });
            }

            if (string.IsNullOrWhiteSpace(ref_))
            {
                yield return ctx.Error("E201", field,
                    "缺少 \"" + field + "\" 平面引用(支持 RefPlane_1/2/3、@别名、obj-K、face:<ID>/face:±Z)。",
                    new { action = "provide", field = field });
                yield break;
            }

            if (ref_.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
                yield break;   // 句柄:静态校验不判

            // 2026-10-03 面锚定(仅非 plane op):只查格式,查不了存在性——
            // 面在正在构建的模型里,静态校验看不到 ⇒ 与 obj-K 同等对待。
            if (!isPlaneOp && ref_.StartsWith("face:", StringComparison.OrdinalIgnoreCase))
            {
                var fp = s.FacePlaneRef;
                if (fp != null && fp.ParseError != null)
                {
                    yield return ctx.Error("E203", field, fp.ParseError,
                        new { action = "set", field = field, example = "face:26 / face:+Z" });
                    yield break;
                }
                // 面锚定目前只有 extrude/cut/hole 三个 op 实现了世界坐标→面局部投影,
                // 其余 op(rib/draft/helix/pattern/...)静态拦下,别留到执行层才炸。
                if (s.OpLower != "extrude" && s.OpLower != "cut" && s.OpLower != "hole")
                {
                    yield return ctx.Error("E203", field,
                        "face:<ID>/face:±Z 面锚定目前只支持 extrude / cut / hole(收到 op=\"" + s.Op + "\")。",
                        new { action = "set", field = field, example = "RefPlane_1" });
                }
                yield break;
            }

            if (ref_.StartsWith("@", StringComparison.Ordinal))
            {
                string key = ref_.Substring(1);
                int defIdx;
                if (!ctx.DefinedPlanes.TryGetValue(key, out defIdx))
                {
                    yield return ctx.Error("E203", field,
                        "未找到本批内命名平面 \"@" + key + "\"(需先用 op=plane 且带 name 创建)。",
                        new { action = "define_plane", name = key, beforeFeature = ctx.Index });
                }
                else if (defIdx >= ctx.Index)
                {
                    yield return ctx.Error("E203", field,
                        "\"@" + key + "\" 是在第 " + defIdx + " 个特征定义的,不能在当前第 " + ctx.Index +
                        " 个特征(前向引用)使用——必须把 op=plane 移到前面。",
                        new { action = "reorder", moveFeature = defIdx, beforeFeature = ctx.Index });
                }
                yield break;
            }

            if (ref_.StartsWith("RefPlane", StringComparison.OrdinalIgnoreCase))
            {
                string idxStr = ref_.Substring("RefPlane".Length).TrimStart('_', ' ', '-');
                int idx;
                if (!int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                {
                    yield return ctx.Error("E202", field,
                        "\"" + ref_ + "\" 解析不出平面索引,应为 RefPlane_1 / RefPlane_2 / RefPlane_3。",
                        new { action = "set", field = field, example = "RefPlane_1" });
                    yield break;
                }
                if (idx < 1)
                {
                    yield return ctx.Error("E202", field,
                        "RefPlane_" + idx + " 索引必须 >= 1(Item 是 1-based)。",
                        new { action = "set", field = field, min = 1 });
                    yield break;
                }
                if (idx > FeatureValidator.DefaultRefPlaneCount)
                {
                    yield return ctx.Warn("W202", field,
                        "RefPlane_" + idx + " 超出默认参考面个数(" + FeatureValidator.DefaultRefPlaneCount +
                        ")。若该面确实是文档里的局部/自建面(如 UI 在实体表面画图自动生成的)可忽略,否则会报\"找不到参考面\"。",
                        new { action = "verify", field = field, defaultPlaneCount = FeatureValidator.DefaultRefPlaneCount });
                }
                yield break;
            }

            // 其它:构建器会按 DisplayName 匹配。中文版是「参考平面_N」,英文名基本匹配不上。
            yield return ctx.Warn("W202", field,
                "\"" + ref_ + "\" 不是 RefPlane_N / @别名 / obj-K / face:。构建器会退化为按 DisplayName 匹配," +
                "中文版 SE 的显示名是「参考平面_N」,通常匹配不上。",
                new { action = "set", field = field, example = "RefPlane_1" });
        }
    }

    // ==================== 几何层 ====================

    /// <summary>
    /// E301 点数不足 · E302 相邻点重合 · E303 自交(报第几条边 × 第几条边) ·
    /// E304 退化面积 · W301 同一特征内环绕向不一致。
    ///
    /// 全部在【平面局部 u/v 二维坐标】下判定——草图坐标不是全局 XYZ,别投影。
    /// </summary>
    public sealed class LoopGeometryRule : IFeatureRule
    {
        public string Code { get { return "E301"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            // 圆轮廓:只需校验半径
            if (s.HasCircle)
            {
                if (s.CircleR <= 0)
                    yield return ctx.Error("E305", "circle.radius",
                        "circle 半径必须 > 0(当前 " + GeoUtil.Fmt(s.CircleR) + ");半径 <= 0 会被当成\"没有圆\"而回退走直线环。",
                        new { action = "set", field = "circle.radius", must = "> 0" });
                yield break;
            }

            // 腰孔:length 是含两端半圆的总长,所以必须 >= width(width 就是端头半圆的直径)
            if (s.HasSlot)
            {
                if (s.SlotWidth <= 0)
                    yield return ctx.Error("E305", "slot.width",
                        "slot 的 width 必须 > 0(当前 " + GeoUtil.Fmt(s.SlotWidth) + ")。",
                        new { action = "set", field = "slot.width", must = "> 0" });

                if (s.SlotLength <= 0)
                    yield return ctx.Error("E305", "slot.length",
                        "slot 的 length 必须 > 0(当前 " + GeoUtil.Fmt(s.SlotLength) + ")。",
                        new { action = "set", field = "slot.length", must = "> 0" });
                else if (s.SlotLength < s.SlotWidth)
                    yield return ctx.Error("E306", "slot.length",
                        "slot 的 length(" + GeoUtil.Fmt(s.SlotLength) + ") 必须 >= width(" +
                        GeoUtil.Fmt(s.SlotWidth) + ")——length 是含两端半圆的总长,width 是端头半圆的直径。",
                        new { action = "set", field = "slot.length", must = ">= slot.width" });
                yield break;
            }

            // circle 字段存在但没解析成圆(半径 <= 0)→ 上面已覆盖;这里补一条"给了 circle 却走直线环"的提示
            if (s.Raw.ValueKind == JsonValueKind.Object && s.Raw.TryGetProperty("circle", out _) && !s.HasCircle)
                yield break;

            // --- 原始点列的数量校验(解析器会【静默丢弃】不足 3 点的环,必须在这里补报)---
            foreach (var iss in CheckRawPointCounts(ctx, s))
                yield return iss;

            for (int i = 0; i < s.Loops.Count; i++)
            {
                var pts = s.Loops[i];
                string fld = "loops[" + i + "]";

                if (pts.Length < 3)
                {
                    yield return ctx.Error("E301", fld,
                        "第 " + (i + 1) + " 个环只有 " + pts.Length + " 个点,闭合环至少需要 3 个点。", null);
                    continue;
                }

                // E302 相邻点重合
                for (int k = 0; k < pts.Length; k++)
                {
                    var a = pts[k];
                    var b = pts[(k + 1) % pts.Length];
                    if (GeoUtil.Dist(a, b) < FeatureValidator.CoincidentEpsilon)
                    {
                        yield return ctx.Error("E302", fld,
                            "第 " + (i + 1) + " 个环的第 " + (k + 1) + " 个点与第 " +
                            (((k + 1) % pts.Length) + 1) + " 个点重合(零长度边)。",
                            new { action = "remove_duplicate_point", field = fld, point = k });
                        break;
                    }
                }

                // E303 自交:非相邻边两两求交(相邻边共享端点,不算自交)
                int n = pts.Length;
                for (int a = 0; a < n; a++)
                {
                    for (int b = a + 1; b < n; b++)
                    {
                        bool adjacent = (b == a + 1) || (a == 0 && b == n - 1);
                        if (adjacent) continue;

                        double ix, iy;
                        if (GeoUtil.SegmentIntersect(pts[a], pts[(a + 1) % n], pts[b], pts[(b + 1) % n], out ix, out iy))
                        {
                            yield return ctx.Error("E303", fld,
                                "第 " + (i + 1) + " 个环自交:第 " + (a + 1) + " 条边 × 第 " + (b + 1) +
                                " 条边,交点 (" + GeoUtil.Fmt(ix) + ", " + GeoUtil.Fmt(iy) + ")。",
                                new { action = "fix_self_intersection", field = fld, edgeA = a + 1, edgeB = b + 1 });
                        }
                    }
                }

                // E304 退化面积
                double area = Math.Abs(GeoUtil.SignedArea(pts));
                if (area < FeatureValidator.DegenerateArea)
                {
                    yield return ctx.Error("E304", fld,
                        "第 " + (i + 1) + " 个环面积约 " + area.ToString("E2", CultureInfo.InvariantCulture) +
                        " m²,接近零——所有点共线或轮廓退化。",
                        new { action = "fix_degenerate", field = fld });
                }
            }

            // W301 绕向不一致:同一特征的多个环一会儿顺一会儿逆
            if (s.Loops.Count >= 2)
            {
                bool hasPos = false, hasNeg = false;
                foreach (var pts in s.Loops)
                {
                    if (pts.Length < 3) continue;
                    double sa = GeoUtil.SignedArea(pts);
                    if (sa > 0) hasPos = true;
                    else if (sa < 0) hasNeg = true;
                }
                if (hasPos && hasNeg)
                    yield return ctx.Warn("W301", "loops",
                        "同一特征内的环绕向不一致(既有逆时针也有顺时针)。多孔轮廓建议统一绕向," +
                        "否则 ProfileSide 的语义在各环上不一致。",
                        new { action = "unify_winding", field = "loops" });
            }
        }

        private static IEnumerable<Issue> CheckRawPointCounts(ValidationContext ctx, FeatureSpec s)
        {
            if (s.Raw.ValueKind != JsonValueKind.Object) yield break;

            JsonElement loopsEl;
            if (s.Raw.TryGetProperty("loops", out loopsEl) && loopsEl.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var loopEl in loopsEl.EnumerateArray())
                {
                    var pts = FeatureSpecParser.ParsePointArray(loopEl);
                    if (pts == null || pts.Length < 3)
                    {
                        yield return ctx.Error("E301", "loops[" + i + "]",
                            "loops 第 " + (i + 1) + " 个环只有 " + (pts == null ? 0 : pts.Length) +
                            " 个有效点(需 >= 3),会被解析器静默丢弃——多孔会少切一个孔。", null);
                    }
                    i++;
                }
                yield break;   // 有 loops 时以 loops 为准
            }

            JsonElement el;
            if (s.Raw.TryGetProperty("polygon", out el) && el.ValueKind == JsonValueKind.Array && el.GetArrayLength() < 3)
            {
                yield return ctx.Error("E301", "polygon",
                    "polygon 只有 " + el.GetArrayLength() + " 个点,至少需要 3 个点。", null);
            }

            if (s.Raw.TryGetProperty("rect", out el) && el.ValueKind == JsonValueKind.Array && el.GetArrayLength() < 2)
            {
                yield return ctx.Error("E301", "rect",
                    "rect 需要两个角点 [[x1,y1],[x2,y2]],当前只有 " + el.GetArrayLength() + " 个。", null);
            }
        }
    }

    // ==================== 语义层 ====================

    /// <summary>
    /// E401:除料类特征之前必须已有增材特征(除料得有料可除)。
    ///
    /// 2026-09-23 P2 扩展:除料语义从 cut/hole 扩到全部 op 的 mode:"cut" 变体
    /// (revolve/loft/sweep/helix 切割);增材判定同样扩到 rib 与四者的凸台形态。
    /// </summary>
    public sealed class CutBeforeExtrudeRule : IFeatureRule
    {
        public string Code { get { return "E401"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "cut", "hole", "revolve", "loft", "sweep", "helix" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            // 除料语义:cut/hole 天然是;revolve/loft/sweep/helix 要 mode=="cut" 才算
            bool cutLike = s.OpLower == "cut" || s.OpLower == "hole" ||
                string.Equals(s.Mode, "cut", StringComparison.OrdinalIgnoreCase);
            if (!cutLike) yield break;

            for (int i = 0; i < ctx.Index; i++)
            {
                if (CreatesMaterial(ctx.Specs[i])) yield break;
            }

            yield return ctx.Error("E401", "op",
                "除料前必须先建实体——当前特征之前没有任何增材特征(extrude/rib 或 revolve/loft/sweep/helix 凸台)。",
                new { action = "insert_extrude_before", beforeFeature = ctx.Index });
        }

        /// <summary>该特征是否给模型加料:extrude/rib 恒是;四类旋转/多轮廓 op 取决于 mode 是否 "cut"。</summary>
        private static bool CreatesMaterial(FeatureSpec p)
        {
            switch (p.OpLower)
            {
                case "extrude":
                case "rib":
                    return true;
                case "revolve":
                case "loft":
                case "sweep":
                case "helix":
                    return !string.Equals(p.Mode, "cut", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }
    }

    /// <summary>E402:extrude 必须给 depth,且必须 &gt; 0。</summary>
    public sealed class ExtrudeDepthRule : IFeatureRule
    {
        public string Code { get { return "E402"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "extrude" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (!s.Depth.HasValue)
            {
                yield return ctx.Error("E402", "depth",
                    "extrude 必须提供 depth(米)。",
                    new { action = "provide", field = "depth", unit = "m" });
                yield break;
            }
            if (s.Depth.Value <= 0)
            {
                yield return ctx.Error("E402", "depth",
                    "depth 必须 > 0(当前 " + GeoUtil.Fmt(s.Depth.Value) + ")。",
                    new { action = "set", field = "depth", must = "> 0", unit = "m" });
            }
        }
    }

    /// <summary>
    /// E105 side 取值非法(SE 只认 1/2/3) · W402 方向/深度组合可疑。
    /// </summary>
    public sealed class SideDirectionRule : IFeatureRule
    {
        public string Code { get { return "W402"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut", "hole" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            int side = s.Side ?? ((s.OpLower == "cut" || s.OpLower == "hole") ? 1 : 2);

            if (side < 1 || side > 3)
            {
                yield return ctx.Error("E105", "side",
                    "side = " + side + " 非法。ProfilePlaneSide 只支持 1(igLeft) / 2(igRight) / 3(igBoth)。",
                    new { action = "set", field = "side", allowed = new[] { 1, 2, 3 } });
                yield break;
            }

            if (side == 3 && s.Depth.HasValue && s.Depth.Value > 0)
            {
                yield return ctx.Warn("W402", "side",
                    "side=3 是双向拉伸,实际总长约为 depth 的两倍(" + GeoUtil.Fmt(s.Depth.Value * 2) +
                    " 米)。若本意是单侧这个深度,改用 side=1 或 2。",
                    new { action = "set", field = "side", suggested = 2 });
            }

            // finite 除料缺 depth 已由 CutModeRule 以 E404(error) 覆盖——静默套 0.2m 是
            // 实打实的坑,该拦下而不是提醒,这里不再重复报 W402。
        }
    }

    /// <summary>
    /// W409 dir 解析失败(构建器会回退默认方向) · E418 expectvolumedelta 解析失败(无法核对设计意图)。
    /// 2026-09-29 新增的两个语义字段:进 KnownFields 只免掉 W102 未知字段,格式错仍须在这里拦——
    /// 否则"静态校验 status=ok"与实际构建行为不符,违背"校验通过=构建器会这么解释"的承诺。
    /// 分级对齐运行时:dir 错 → 回退默认 + warning(不致命);expectvolumedelta 错 → 无法核对 + error。
    /// </summary>
    public sealed class DirVolumeFieldRule : IFeatureRule
    {
        public string Code { get { return "W409"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut", "hole" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.DirParseError != null)
            {
                yield return ctx.Warn("W409", "dir",
                    "dir 无法解析:" + s.DirParseError + "。构建器会回退到默认方向(side 由方向自愈决定)。",
                    new { action = "fix", field = "dir", example = new[] { 0, 0, 1 } });
            }

            // 2026-10-03:dir→side 换算依赖"已知平面法向";face 平面的法向静态换算不出来 ⇒ dir 会被忽略。
            if (s.FacePlaneRef != null && s.HasDir)
            {
                yield return ctx.Warn("W409", "dir",
                    "plane 是 face 引用时无法静态换算 dir→side(dir 需要已知平面法向),dir 将被忽略," +
                    "请改用显式 side(1/2)。",
                    new { action = "set", field = "side", allowed = new[] { 1, 2 } });
            }

            if (s.ExpectVolParseError != null)
            {
                yield return ctx.Error("E418", "expectvolumedelta",
                    "expectvolumedelta 无法解析:" + s.ExpectVolParseError + "。声明了却解析不出来=无法核对设计意图,建前拦下。",
                    new { action = "fix", field = "expectvolumedelta", example = -1250 });
            }
        }
    }

    /// <summary>
    /// W401:同一平面上出现两次及以上 cut/hole 却没有合并成一个轮廓——**僵尸特征头号杀手**。
    /// 实测:同一模型第 2 个及以后的 AddThroughNext 必然 Status=1216476311(几何未生成),
    /// 与 mode=next/all/finite 无关。多孔必须画进同一个 Profile 的多个圆环(circles),一次切完。
    /// hole 与 cut 共用除料管线,僵尸规律相同(2026-09-23 hole op 纳入)。
    /// </summary>
    public sealed class ConsecutiveCutRule : IFeatureRule
    {
        public string Code { get { return "W401"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "cut", "hole" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (string.IsNullOrWhiteSpace(s.PlaneRef)) yield break;

            for (int i = 0; i < ctx.Index; i++)
            {
                var prev = ctx.Specs[i];
                if (prev.OpLower != "cut" && prev.OpLower != "hole") continue;
                if (!string.Equals(prev.PlaneRef, s.PlaneRef, StringComparison.OrdinalIgnoreCase)) continue;

                yield return ctx.Warn("W401", "plane",
                    "第 " + i + " 个特征已经在同一平面 \"" + s.PlaneRef + "\" 上除过料/打过孔了。" +
                    "同一平面上的第 2 次除料必然生成僵尸特征(Status=1216476311)," +
                    "请把这些孔合并进【同一个】特征的 circles/loops 里一次切完。",
                    new
                    {
                        action = "merge_into_previous",
                        target = i,
                        field = "circles",
                        hint = "把本特征的圆追加到目标特征的 circles 数组,再删掉本特征"
                    });
                yield break;
            }
        }
    }

    /// <summary>
    /// W403:除料轮廓落在毛坯之外(第五大坑:圆心/半径必须落在实心墙内,否则 6311)。
    /// 只在 plane 能解析成默认面(或基于默认面偏移的 @别名)时才判——
    /// obj-K 与无法解析的面没有确定的 u/v→全局映射,一律跳过(out of scope,不误报)。
    /// </summary>
    public sealed class CutOutsideStockRule : IFeatureRule
    {
        public string Code { get { return "W403"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "cut", "hole" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var stock = StockEstimator.Estimate(ctx.Specs);
            if (stock == null) yield break;   // 毛坯无法估算(没有可解析的 extrude)→ 不判

            var s = ctx.Current;
            int planeIdx;
            double offset;
            if (!PlaneResolver.TryResolve(ctx.Specs, ctx.Index, s.PlaneRef, out planeIdx, out offset))
                yield break;

            double depth = s.Depth ?? 0;
            bool depthKnown = s.Depth.HasValue;

            var bb = StockEstimator.FeatureBBox(ctx.Specs, ctx.Index, stock);
            if (bb == null) yield break;

            if (!StockEstimator.Intersects(bb, stock))
            {
                yield return ctx.Warn("W403", "plane",
                    "除料轮廓落在毛坯之外(毛坯包围盒 [" + StockEstimator.FmtBox(stock) +
                    "],除料 [" + StockEstimator.FmtBox(bb) + "])。除不到任何材料 → 必然 6311 僵尸。" +
                    "注意草图坐标是【所在平面的局部 u/v】,不是全局 XYZ。",
                    new { action = "move_inside_stock", field = "loops", stockBox = stock, cutBox = bb });
            }

            // finite 且深度已知:深度方向也要够得着
            if (depthKnown && string.Equals(s.Mode, "finite", StringComparison.OrdinalIgnoreCase) && depth <= 0)
            {
                yield return ctx.Warn("W403", "depth",
                    "mode=finite 的 depth = " + GeoUtil.Fmt(depth) + ",切不到任何材料。",
                    new { action = "set", field = "depth", must = "> 0" });
            }
        }
    }

    /// <summary>
    /// E409:hole 的形状与模式校验(2026-09-23 P1)。
    /// 形状只接受圆(circle/circles/center+diameter)——孔是圆的,异形孔诚实分流回 cut,不做万能入口;
    /// mode 允许 through_all(默认,与 cut 的 next 默认不同)/all/next/finite,finite 必须给 depth。
    /// </summary>
    public sealed class HoleRule : IFeatureRule
    {
        public string Code { get { return "E409"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "hole" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.Diameter.HasValue && s.Diameter.Value <= 0)
            {
                yield return ctx.Error("E409", "diameter",
                    "diameter 必须 > 0(当前 " + GeoUtil.Fmt(s.Diameter.Value) + ")。",
                    new { action = "set", field = "diameter", must = "> 0", unit = "m" });
            }

            bool round = s.HasCircle || s.HasCircles;
            if (!round)
            {
                yield return ctx.Error("E409", "shape",
                    string.IsNullOrEmpty(s.ShapeError)
                        ? "hole 需要 circle/circles 或 center+diameter(孔是圆的);非圆异形孔请用 cut。"
                        : s.ShapeError + "(hole 只接受圆孔;异形孔请用 cut)",
                    new { action = "provide", field = "shape",
                          oneOf = new[] { "circle", "circles", "center+diameter" } });
                yield break;
            }

            // 模式:through_all(默认)/all/next/finite
            string mode = (s.Mode ?? "").Trim().ToLowerInvariant();
            if (mode == "through_all" || mode == "through") mode = "all";
            if (string.IsNullOrEmpty(mode) || mode == "all" || mode == "next") yield break;   // 贯穿类,无额外要求

            if (mode == "finite")
            {
                if (!s.Depth.HasValue)
                    yield return ctx.Error("E409", "depth",
                        "mode=\"finite\" 必须给 depth(米),否则会静默套用 0.2 米的默认值——盲孔深度别靠猜。",
                        new { action = "provide", field = "depth", hint = "如 \"depth\":0.02 = 盲孔深 20mm" });
            }
            else
            {
                yield return ctx.Error("E409", "mode",
                    "hole 的 mode \"" + s.Mode + "\" 不认识。允许 through_all(默认)/all/next/finite。",
                    new { action = "set", field = "mode", allowed = new[] { "through_all", "all", "next", "finite" } });
            }
        }
    }

    // ==================== P2 多轮廓(2026-09-23):loft / sweep / helix ====================

    /// <summary>
    /// E410:loft 的多轮廓声明校验。
    ///
    /// loft 打破"单特征单轮廓"惯例:形状全部声明在 profiles 数组里(顶层不收形状)。
    /// 核对:profiles 是数组且 ≥2 项;每项有 plane 且形状是【单闭合轮廓】——
    /// circles 多环/开放链不能当放样截面(截面间的对应关系无定义)。
    /// </summary>
    public sealed class LoftRule : IFeatureRule
    {
        public string Code { get { return "E410"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "loft" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.ProfilesError != null)
            {
                yield return ctx.Error("E410", "profiles", s.ProfilesError,
                    new { action = "fix", field = "profiles", format = "[{\"plane\":...,形状字段,origin?},...]" });
                yield break;
            }

            if (!s.HasProfiles)
            {
                yield return ctx.Error("E410", "profiles",
                    "loft 的形状必须声明在 profiles 数组里(顶层不收形状字段)。例:profiles:[{\"plane\":\"RefPlane_1\",\"circle\":[0,0,0.02]},{\"plane\":\"@P1\",\"circle\":[0,0,0.01]}]。",
                    new { action = "provide", field = "profiles", minItems = 2 });
                yield break;
            }

            if (s.Profiles.Count < 2)
            {
                yield return ctx.Error("E410", "profiles",
                    "loft 至少 2 个截面(当前 " + s.Profiles.Count + " 个)——一个截面放不出体。",
                    new { action = "add_item", field = "profiles", minItems = 2 });
                yield break;
            }

            foreach (var e in SectionIssues(ctx, s, 0, "E410"))
                yield return e;
        }

        /// <summary>
        /// 逐项核对截面:plane 必填、形状必须单闭合轮廓。loft 检查全部项,sweep 从第 2 项起检查
        /// (首项是路径,规则不同)。放在 LoftRule 里只是"就近",sweep 也复用。
        /// </summary>
        internal static IEnumerable<Issue> SectionIssues(ValidationContext ctx, FeatureSpec s, int startIndex, string code)
        {
            for (int i = startIndex; i < s.Profiles.Count; i++)
            {
                var p = s.Profiles[i];
                string at = "profiles[" + i + "]";

                if (string.IsNullOrWhiteSpace(p.PlaneRef))
                    yield return ctx.Error(code, at + ".plane",
                        at + " 缺少 plane(每个截面各自声明所在平面,可用 @别名 / RefPlane_N / obj-K)。",
                        new { action = "provide", field = at + ".plane" });

                if (p.HasCircles)
                    yield return ctx.Error(code, at + ".shape",
                        at + " 用了 circles(多真圆)——放样/扫掠截面必须是【单个】闭合轮廓,多环没有对应关系。",
                        new { action = "set", field = at + ".shape", oneOf = new[] { "circle", "slot", "rect", "polygon", "loops" } });
                else if (p.ShapeError != null)
                    yield return ctx.Error(code, at + ".shape",
                        at + " " + p.ShapeError,
                        new { action = "fix", field = at + ".shape" });
                else if (p.OpenChain != null)
                    yield return ctx.Error(code, at + ".shape",
                        at + " 是开放链(polygon 只有 sweep 首项按开放路径解释)——截面必须闭合。",
                        new { action = "fix", field = at + ".shape" });
                else if (p.Loops.Count > 1)
                    yield return ctx.Error(code, at + ".shape",
                        at + " 有 " + p.Loops.Count + " 个环——截面必须是单闭合轮廓。",
                        new { action = "fix", field = at + ".shape" });
            }
        }
    }

    /// <summary>
    /// E411:sweep 的 path + 截面声明。
    ///
    /// 首项 = 路径:polygon 按【开放链】解释(≥2 点,不自动闭合,如折线/直线),
    /// circle/rect/loops/slot 则是闭合路径(扫一整圈)。其余项 = 截面,须单闭合轮廓(同 loft)。
    /// </summary>
    public sealed class SweepRule : IFeatureRule
    {
        public string Code { get { return "E411"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "sweep" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.ProfilesError != null)
            {
                yield return ctx.Error("E411", "profiles", s.ProfilesError,
                    new { action = "fix", field = "profiles", format = "[{\"plane\":...,形状字段,origin?},...]" });
                yield break;
            }

            if (!s.HasProfiles)
            {
                yield return ctx.Error("E411", "profiles",
                    "sweep 的形状必须声明在 profiles 数组里:首项是路径(path),其余是截面。例:profiles:[{\"plane\":\"RefPlane_3\",\"polygon\":[[0,0],[0.05,0.05]]},{\"plane\":\"RefPlane_1\",\"circle\":[0,0,0.005]}]。",
                    new { action = "provide", field = "profiles", minItems = 2 });
                yield break;
            }

            if (s.Profiles.Count < 2)
            {
                yield return ctx.Error("E411", "profiles",
                    "sweep 至少 2 项:1 条路径 + 1 个截面(当前 " + s.Profiles.Count + " 项)。",
                    new { action = "add_item", field = "profiles", minItems = 2 });
                yield break;
            }

            // 首项:路径(trace 线弧混排 / polygon 开放链 / 单个闭合轮廓均可,但不能多环)
            var path = s.Profiles[0];
            if (string.IsNullOrWhiteSpace(path.PlaneRef))
                yield return ctx.Error("E411", "profiles[0].plane",
                    "profiles[0](路径)缺少 plane。",
                    new { action = "provide", field = "profiles[0].plane" });
            else if (path.TraceError != null)
                yield return ctx.Error("E411", "profiles[0].trace",
                    "profiles[0](路径) trace 无法解析:" + path.TraceError +
                    "。格式 [{\"line\":[[x,y],[x,y]]},{\"arc\":{\"center\":[x,y],\"start\":[x,y],\"end\":[x,y]}}]。",
                    new { action = "fix", field = "profiles[0].trace" });
            else if (path.HasCircles)
                yield return ctx.Error("E411", "profiles[0].shape",
                    "profiles[0](路径)用了 circles——路径必须是单条链(trace 线弧混排 / polygon 开放链)或单个闭合轮廓。",
                    new { action = "set", field = "profiles[0].shape", oneOf = new[] { "trace", "polygon", "circle", "slot", "rect", "loops" } });
            else if (path.ShapeError != null && path.OpenChain == null && path.Trace == null)
                yield return ctx.Error("E411", "profiles[0].shape",
                    "profiles[0](路径)" + path.ShapeError + " 路径可用 trace(线段/真圆弧)或 polygon(开放链,≥2 点)或 circle/rect/loops(闭合)。",
                    new { action = "fix", field = "profiles[0].shape" });

            // trace 段间首尾必须相接:构建器对相邻段加 AddKeypoint 强制端点重合,不接会让 SE 报错或轮廓扭曲。
            if (path.Trace != null)
            {
                for (int i = 0; i + 1 < path.Trace.Count; i++)
                {
                    double[] e = path.Trace[i].P1, n = path.Trace[i + 1].P0;
                    if (Math.Abs(e[0] - n[0]) > 1e-9 || Math.Abs(e[1] - n[1]) > 1e-9)
                    {
                        yield return ctx.Error("E411", "profiles[0].trace",
                            "profiles[0](路径)第 " + (i + 1) + " 段终点(" + e[0] + "," + e[1] +
                            ")与第 " + (i + 2) + " 段起点(" + n[0] + "," + n[1] + ")不重合——路径必须首尾相连。",
                            new { action = "fix", field = "profiles[0].trace" });
                        break;
                    }
                }
            }

            // 弧必须与相邻段【切向连续】——端点重合还不够:接点处不相切扫出来的就是折角(90° 斜接)而不是弯头。
            // 2026-10-04 事故教训:圆心错放到拐角点上时端点仍重合、静态层全绿,直到肉眼才发现。
            // 判据纯三角运算(零 COM 往返):接点两侧的单位切向夹角 > TangentTolDeg 即拒。
            // 直线-直线折角是合法的斜接(两段直管对接),不在此列;只查"任一侧是圆弧"的接点。
            if (path.Trace != null)
            {
                for (int i = 0; i + 1 < path.Trace.Count; i++)
                {
                    var segA = path.Trace[i];
                    var segB = path.Trace[i + 1];
                    if (segA.Kind != "arc" && segB.Kind != "arc") continue;
                    double[] ta, tb;
                    if (!TryTangentAt(segA, true, out ta) || !TryTangentAt(segB, false, out tb)) continue;
                    double dot = ta[0] * tb[0] + ta[1] * tb[1];
                    if (dot > 1.0) dot = 1.0; else if (dot < -1.0) dot = -1.0;
                    double deg = Math.Acos(dot) * 180.0 / Math.PI;
                    if (deg > TangentTolDeg)
                        yield return ctx.Error("E411", "profiles[0].trace",
                            "profiles[0](路径)第 " + (i + 1) + " 段与第 " + (i + 2) + " 段在接点处【不相切】(切向夹角 " +
                            deg.ToString("F1", CultureInfo.InvariantCulture) + "°)——圆弧必须与相邻段相切。" +
                            "相切弯头的圆心在【拐角的对角点】:R=0.02 从 (0.02,0.03) 转到 (0.04,0.05) 时 center=[0.04,0.03]。" +
                            "更省事的写法是顶点链自动倒圆角:{\"plane\":...,\"polygon\":[[u,v],...],\"fillet\":0.02}(圆心/切点由几何算出,必然相切)。",
                            new { action = "fix", field = "profiles[0].trace" });
                }
            }

            // 其余项:截面(单闭合轮廓),复用 loft 的逐项核对
            foreach (var e in LoftRule.SectionIssues(ctx, s, 1, "E411"))
                yield return e;
        }

        /// <summary>接点处允许的切向夹角容差(度)。只在"任一侧是圆弧"的接点判;直-直折角是合法斜接。</summary>
        private const double TangentTolDeg = 2.0;

        /// <summary>
        /// 段沿【声明方向】(P0→P1)在起点/终点处的单位切向(相切检查用)。
        /// 圆弧按【劣弧】定方向(与构建层 CreateProfileTrace 的交换规则一致):切向 = sign(sweep)·(−sin a, cos a)。
        /// 退化段(零长/无圆心/整圆)返回 false,交给别的检查处理。
        /// </summary>
        private static bool TryTangentAt(PathSegment seg, bool atEnd, out double[] t)
        {
            t = null;
            if (seg.Kind == "line")
            {
                double dx = seg.P1[0] - seg.P0[0], dy = seg.P1[1] - seg.P0[1];
                double l = Math.Sqrt(dx * dx + dy * dy);
                if (l < 1e-12) return false;
                t = new[] { dx / l, dy / l };
                return true;
            }
            if (seg.Kind != "arc" || seg.Center == null || seg.P0 == null || seg.P1 == null) return false;
            double a0 = Math.Atan2(seg.P0[1] - seg.Center[1], seg.P0[0] - seg.Center[0]);
            double a1 = Math.Atan2(seg.P1[1] - seg.Center[1], seg.P1[0] - seg.Center[0]);
            double sweep = a1 - a0;
            while (sweep <= -Math.PI) sweep += 2.0 * Math.PI;
            while (sweep > Math.PI) sweep -= 2.0 * Math.PI;     // 归一到 (−π,π] = 劣弧
            if (Math.Abs(sweep) < 1e-12) return false;
            double a = atEnd ? a1 : a0;
            double sgn = sweep > 0 ? 1.0 : -1.0;
            t = new[] { -Math.Sin(a) * sgn, Math.Cos(a) * sgn };
            return true;
        }
    }

    /// <summary>
    /// E412:helix 的参数校验(pitch / height / revolutions 二给一)。
    ///
    /// helix 与 revolve 同构:顶层 plane + 单闭合截面 + axis(旋转轴,由 E403 核对),
    /// 另需螺旋三要素中【至少两个】&gt; 0(第三个由 SE 推导)。circles 多环不能当螺旋截面。
    /// </summary>
    public sealed class HelixRule : IFeatureRule
    {
        public string Code { get { return "E412"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "helix" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.HasCircles)
                yield return ctx.Error("E412", "circles",
                    "helix 截面必须单闭合轮廓(circle/rect/polygon/loops),circles 多环不支持。",
                    new { action = "set", field = "shape", oneOf = new[] { "circle", "slot", "rect", "polygon", "loops" } });

            if (s.Loops.Count > 1)
                yield return ctx.Error("E412", "loops",
                    "helix 截面有 " + s.Loops.Count + " 个环,必须单环。",
                    new { action = "fix", field = "shape" });

            if (s.Pitch.HasValue && !(s.Pitch.Value > 0))
                yield return ctx.Error("E412", "pitch",
                    "pitch(螺距)必须 > 0(当前 " + GeoUtil.Fmt(s.Pitch.Value) + ")。",
                    new { action = "set", field = "pitch", must = "> 0", unit = "m" });

            if (s.Height.HasValue && !(s.Height.Value > 0))
                yield return ctx.Error("E412", "height",
                    "height(螺旋高度)必须 > 0(当前 " + GeoUtil.Fmt(s.Height.Value) + ")。",
                    new { action = "set", field = "height", must = "> 0", unit = "m" });

            if (s.Revolutions.HasValue && !(s.Revolutions.Value > 0))
                yield return ctx.Error("E412", "revolutions",
                    "revolutions(圈数)必须 > 0(当前 " + GeoUtil.Fmt(s.Revolutions.Value) + ")。",
                    new { action = "set", field = "revolutions", must = "> 0" });

            int given = (s.Pitch.HasValue ? 1 : 0) + (s.Height.HasValue ? 1 : 0) + (s.Revolutions.HasValue ? 1 : 0);
            if (given < 2)
                yield return ctx.Error("E412", "pitch|height|revolutions",
                    "helix 需给出 pitch(螺距,m)/ height(高度,m)/ revolutions(圈数)中【至少 2 个】,第三个由 SE 推导(当前只给了 " + given + " 个)。",
                    new { action = "provide", fields = new[] { "pitch", "height", "revolutions" }, give = "至少 2/3" });

            // 2026-09-23 真机实测:helix 螺距 vs 线径(2r)——重叠≥50%(pitch=0.006,r=0.006)必得僵尸
            // Status=1216476311(SE 官方博客:"cross section diameter cannot be the same or larger than
            // the minimum pitch"是对设计者的保守指引);但重叠~8%(pitch=0.011,r=0.006)SE 实际能成体。
            // 阈值在 (r, 2r] 之间未定,故只对实证边界 pitch<r 给 W 警告,轻度重叠靠真机兜底。
            if (s.Pitch.HasValue && s.HasCircle && s.CircleR > 0 && s.Pitch.Value < s.CircleR)
                yield return ctx.Warn("W412", "pitch",
                    "螺距(" + GeoUtil.Fmt(s.Pitch.Value) + "m) < 线半径(" + GeoUtil.Fmt(s.CircleR) +
                    "m):相邻圈重叠超半线径,SE 大概率拒绝成体(僵尸 1216476311)。建议 pitch > 2×circle 半径最稳。",
                    new { action = "set", field = "pitch", must = "> 2r", unit = "m" });
        }
    }

    // ==================== P3 面引用机制(2026-09-23) ====================

    /// <summary>
    /// E204:faceOf 引用未定义 / 前向引用 / 解析失败。
    /// 对照 PlaneRefRule(E203) 模板:faceOf 必须是 @别名(本批前面特征的 name) 或 obj-K 句柄。
    /// obj-K 句柄不判(句柄表在 server 进程里,静态校验看不到)。
    /// </summary>
    public sealed class FeatureRefRule : IFeatureRule
    {
        public string Code { get { return "E204"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            // 仅面引用类 op 才判:draft / thicken / delete_face
            if (s.OpLower != "draft" && s.OpLower != "thicken" && s.OpLower != "delete_face")
                yield break;

            if (!s.HasFaceRef)
            {
                yield return ctx.Error("E204", "faceOf",
                    "缺少 faceOf 面引用(需 {\"faceOf\":\"@别名\",\"faceNormal\":[0,0,1]} 或 {\"faceOf\":\"@别名\",\"faceIndex\":0})。",
                    new { action = "provide", field = "faceOf" });
                yield break;
            }

            // 解析失败的优先报
            if (s.FaceRef != null && s.FaceRef.ParseError != null)
            {
                yield return ctx.Error("E204", "faceOf",
                    "面引用解析失败:" + s.FaceRef.ParseError,
                    new { action = "fix", field = "faceOf|faceNormal|faceIndex" });
                yield break;
            }

            // faceOf 必填且为 @别名 或 obj-K
            string fof = s.FaceRef != null ? s.FaceRef.FeatureName : null;
            if (string.IsNullOrWhiteSpace(fof))
            {
                yield return ctx.Error("E204", "faceOf",
                    "faceOf 必须是非空字符串(@别名 或 obj-K 句柄)。",
                    new { action = "set", field = "faceOf" });
                yield break;
            }

            if (fof.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
                yield break;   // 句柄:静态校验不判

            if (fof.StartsWith("@", StringComparison.Ordinal))
            {
                string key = fof.Substring(1);
                int defIdx;
                if (!ctx.DefinedFeatures.TryGetValue(key, out defIdx))
                {
                    yield return ctx.Error("E204", "faceOf",
                        "未找到本批内命名特征 \"@" + key + "\"(需先给前面的特征带 name 创建)。",
                        new { action = "define_feature", name = key, beforeFeature = ctx.Index });
                }
                else if (defIdx >= ctx.Index)
                {
                    yield return ctx.Error("E204", "faceOf",
                        "\"@" + key + "\" 是在第 " + defIdx + " 个特征定义的,不能在当前第 " + ctx.Index +
                        " 个特征(前向引用)使用——必须把那个特征移到前面。",
                        new { action = "reorder", moveFeature = defIdx, beforeFeature = ctx.Index });
                }
                yield break;
            }

            // 其它格式(纯名字无 @ 前缀)按非法处理
            yield return ctx.Error("E204", "faceOf",
                "faceOf 必须以 \"@\" 开头(本批别名)或 \"obj-\" 开头(跨批句柄),当前 \"" + fof + "\"。",
                new { action = "set", field = "faceOf", example = "@base" });
        }
    }

    /// <summary>
    /// E413:draft(拔模)参数校验。
    /// side(DraftSide)只认 igInside=4 / igOutside=5(对照表 §一 真机验证:传 1/2/3 全 E_FAIL)。
    /// angle 弧度 ∈ [0, π/2);plane 必给(draft 需拔模基准面)。
    /// </summary>
    public sealed class DraftRule : IFeatureRule
    {
        public string Code { get { return "E413"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "draft" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            // angle 必给且 ∈ [0, π/2)
            if (!s.Angle.HasValue && !s.Degrees.HasValue)
            {
                yield return ctx.Error("E413", "angle",
                    "draft 必须给 angle(弧度)或 degrees(度)。",
                    new { action = "provide", field = "angle|degrees" });
            }
            else
            {
                double a = s.Degrees.HasValue ? s.Degrees.Value * System.Math.PI / 180.0 : s.Angle.Value;
                if (!(a >= 0) || !(a < System.Math.PI / 2))
                    yield return ctx.Error("E413", "angle",
                        "angle 必须 ∈ [0, π/2)(当前 " + GeoUtil.Fmt(a) + " 弧度 = " + (a * 180 / System.Math.PI) + "°)。",
                        new { action = "set", field = "angle", min = 0, maxOpen = "π/2" });
            }

            // side(DraftSide)只认 4(igInside) 或 5(igOutside)
            if (s.Side.HasValue && s.Side.Value != 4 && s.Side.Value != 5)
            {
                yield return ctx.Error("E413", "side",
                    "draft 的 side 只能是 4(igInside,向内拔)或 5(igOutside,向外拔),传 1/2/3 全 E_FAIL(当前 " + s.Side.Value + ")。",
                    new { action = "set", field = "side", allowed = new[] { 4, 5 } });
            }
        }
    }

    /// <summary>
    /// E414:split(分割)参数校验。plane 必给(分割基准面);target 缺省用 Models.Item(1)。
    /// </summary>
    public sealed class SplitRule : IFeatureRule
    {
        public string Code { get { return "E414"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "split" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            // plane 必给(走 PlaneRefRule 的 E201 也判,这里不重复)

            // target 可选:给了必须是 @别名 或 obj-K
            if (!string.IsNullOrEmpty(s.Target) &&
                !s.Target.StartsWith("@", StringComparison.Ordinal) &&
                !s.Target.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                yield return ctx.Error("E414", "target",
                    "target 必须是 @别名 或 obj-K 句柄(当前 \"" + s.Target + "\")。",
                    new { action = "set", field = "target", example = "@base" });
                yield break;
            }

            // 2026-09-23 真机批次发现:@别名 只查了前缀没查存在性,@nope 静默通过(负例 n6 漏报)。
            // 对齐 faceOf 的 E204 逻辑:本批名字表查存在性 + 前向引用。
            if (!string.IsNullOrEmpty(s.Target) && s.Target.StartsWith("@", StringComparison.Ordinal))
            {
                string key = s.Target.Substring(1);
                int defIdx;
                if (!ctx.DefinedFeatures.TryGetValue(key, out defIdx))
                {
                    yield return ctx.Error("E414", "target",
                        "未找到本批内命名特征 \"" + s.Target + "\"(需先给前面的特征带 name 创建)。",
                        new { action = "define_feature", name = key, beforeFeature = ctx.Index });
                }
                else if (defIdx >= ctx.Index)
                {
                    yield return ctx.Error("E414", "target",
                        "\"" + s.Target + "\" 是在第 " + defIdx + " 个特征定义的,不能在当前第 " + ctx.Index +
                        " 个特征(前向引用)使用——必须把那个特征移到前面。",
                        new { action = "reorder", moveFeature = defIdx, beforeFeature = ctx.Index });
                }
            }
        }
    }

    /// <summary>
    /// E415:web_network(腹板网)参数校验。thickness/depth > 0;polygon 闭合环(参考 rib 实测结论)。
    /// </summary>
    public sealed class WebNetworkRule : IFeatureRule
    {
        public string Code { get { return "E415"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "web_network" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (!s.Thickness.HasValue || !(s.Thickness.Value > 0))
                yield return ctx.Error("E415", "thickness",
                    "web_network 的 thickness 必须 > 0(米)。",
                    new { action = "set", field = "thickness", must = "> 0", unit = "m" });

            if (!s.Depth.HasValue || !(s.Depth.Value > 0))
                yield return ctx.Error("E415", "depth",
                    "web_network 的 depth 必须 > 0(米)。",
                    new { action = "set", field = "depth", must = "> 0", unit = "m" });
        }
    }

    /// <summary>
    /// E416:thicken(曲面加厚)参数校验。thickness > 0;faceOf 必填(由 FeatureRefRule E204 判);
    /// 坑:surface 自身 .Faces 抛异常,必须取自 Constructions.Item(n).Body.Faces(igQueryAll)。
    /// </summary>
    public sealed class ThickenRule : IFeatureRule
    {
        public string Code { get { return "E416"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "thicken" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (!s.Thickness.HasValue || !(s.Thickness.Value > 0))
                yield return ctx.Error("E416", "thickness",
                    "thicken 的 thickness 必须 > 0(米)。",
                    new { action = "set", field = "thickness", must = "> 0", unit = "m" });
        }
    }

    /// <summary>
    /// E417:delete_face(删面)参数校验。confirm 必须 true(破坏性操作,防误删);
    /// faceOf 必填(由 FeatureRefRule E204 判);目标须是 blend 面(运行期判,静态只做结构校验)。
    /// </summary>
    public sealed class DeleteFaceRule : IFeatureRule
    {
        public string Code { get { return "E417"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "delete_face" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.Confirm != true)
                yield return ctx.Error("E417", "confirm",
                    "delete_face 是破坏性操作,必须显式传 \"confirm\":true 才会执行(防误删 blend 面)。",
                    new { action = "set", field = "confirm", must = "true" });
        }
    }

    /// <summary>W404:同一特征内的环相互重叠。重叠轮廓会让 Profile 闭合关系含糊,极易出僵尸。</summary>
    public sealed class LoopOverlapRule : IFeatureRule
    {
        public string Code { get { return "W404"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var loops = ctx.Current.Loops;
            for (int i = 0; i < loops.Count; i++)
            {
                for (int j = i + 1; j < loops.Count; j++)
                {
                    var bi = GeoUtil.BBox(loops[i]);
                    var bj = GeoUtil.BBox(loops[j]);
                    if (!GeoUtil.BBoxOverlap(bi, bj)) continue;

                    bool hit = false;
                    foreach (var p in loops[i])
                        if (GeoUtil.PointInPolygon(p, loops[j])) { hit = true; break; }
                    if (!hit)
                        foreach (var p in loops[j])
                            if (GeoUtil.PointInPolygon(p, loops[i])) { hit = true; break; }

                    if (hit)
                    {
                        yield return ctx.Warn("W404", "loops[" + i + "]",
                            "第 " + (i + 1) + " 个环与第 " + (j + 1) + " 个环重叠。",
                            new { action = "separate_loops", loopA = i, loopB = j });
                    }
                }
            }
        }
    }

    /// <summary>
    /// W406 声明了约束/标注但形状走不到应用路径 · W407 dims 的 element 索引越界 / 声明不完整。
    ///
    /// 背景(2026-09-13 构建器实测):autoConstraint / fixOrigin / dims 只在
    /// CreateProfileMulti / CreateProfileRevolve 的【直线环】路径上应用(必须在 Profile.End 之前),
    /// circle / circles / slot 轮廓直接 End —— 声明被【静默忽略】,连错都不报。
    /// 几何照样建成,但"参数化意图"必然落空:变量不存在,后续特征引用它会失败。
    ///
    /// 级别定 warning 而不是 error:geometrically 能建成,不属于"必然失败"
    /// (se_model_build 遇 error 会拒绝执行整批,不该为这个拦住建模)。
    ///
    /// element 越界的可算性:解析器已把 rect 归一化成 4 点环、polygon/loops 直接给点列,
    /// 所以"跨环扁平线索引"的总数 = Σ 各环点数(旋转轴是独立构造线,不占位)。
    /// </summary>
    public sealed class ConstraintBindingRule : IFeatureRule
    {
        public string Code { get { return "W406"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            bool hasDims = s.Dims != null && s.Dims.Count > 0;
            bool hasAuto = s.AutoConstraint == true;
            bool hasFix = s.FixOrigin == true;
            if (!hasDims && !hasAuto && !hasFix) yield break;

            var names = new List<string>();
            if (hasAuto) names.Add("autoConstraint");
            if (hasFix) names.Add("fixOrigin");
            if (hasDims) names.Add("dims(" + s.Dims.Count + " 条)");

            // ---- W406:形状不支持(声明必然不生效) ----
            string why = null;
            if (s.OpLower == "plane")
                why = "op=\"plane\" 不建草图形状";
            else if (s.HasCircle)
                why = "circle(圆)轮廓没有直线可约束/标注";
            else if (s.HasCircles)
                why = "circles(多真圆)轮廓没有直线可约束/标注";
            else if (s.HasSlot)
                why = "slot(腰孔)轮廓未接入约束/标注应用路径";

            if (why != null)
            {
                yield return ctx.Warn("W406", hasDims ? "dims" : (hasAuto ? "autoconstraint" : "fixorigin"),
                    "声明了 " + string.Join("、", names) + ",但 " + why +
                    "——这些声明会被【静默忽略】:不报错,也不生效(尺寸不会被驱动、变量不会进变量表)。" +
                    "如需尺寸驱动,请把轮廓改成 rect / polygon / loops 直线环。",
                    new
                    {
                        action = "remove_or_switch_shape",
                        fields = names,
                        unsupportedShape = s.ShapeSource == "" ? s.OpLower : s.ShapeSource,
                        supportedShapes = new[] { "rect", "polygon", "loops" }
                    });
                yield break;   // 形状不支持时再报 element 越界没有意义(重复报噪)
            }

            if (!hasDims) yield break;

            // ---- W407:声明本身不成立 ----
            int lineCount = 0;
            foreach (var loop in s.Loops) lineCount += loop.Length;

            for (int k = 0; k < s.Dims.Count; k++)
            {
                var ds = s.Dims[k];
                string tag = string.IsNullOrEmpty(ds.Name) ? ("#" + k) : ds.Name;

                if (ds.ParseError != null)
                {
                    yield return ctx.Warn("W407", "dims[" + k + "]",
                        "第 " + (k + 1) + " 条标注声明不完整(" + ds.ParseError + "),该标注不会建立。",
                        new { action = "fix", field = "dims[" + k + "]", required = new[] { "element", "name", "value|formula" } });
                    continue;
                }

                if (ds.Element < 0 || ds.Element >= lineCount)
                {
                    yield return ctx.Warn("W407", "dims[" + k + "]",
                        "dims \"" + tag + "\" 的 element=" + ds.Element + " 越界——本特征共 " + lineCount +
                        " 条直线(跨环扁平 0-based,合法范围 0.." + Math.Max(lineCount - 1, 0) +
                        "),运行时该标注会被跳过,变量 \"" + tag + "\" 不会建立。",
                        new
                        {
                            action = "set",
                            field = "dims[" + k + "].element",
                            lineCount = lineCount,
                            range = new[] { 0, Math.Max(lineCount - 1, 0) }
                        });
                }
            }
        }
    }

    // ==================== 扩 op 语义层(2026-09-22) ====================

    /// <summary>
    /// E205:fillet/chamfer 必须有 edges,且每项是可解析的 {"face":"face:ID","edge":0-based}。
    /// 静态层只判结构;Face.ID 是否存在、edge 是否越界是运行期的事(几何校验留到运行时)。
    /// </summary>
    public sealed class EdgeRefRule : IFeatureRule
    {
        public string Code { get { return "E205"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "fillet", "chamfer" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.Raw.ValueKind == JsonValueKind.Object && !s.Raw.TryGetProperty("edges", out _))
            {
                yield return ctx.Error("E205", "edges",
                    s.OpLower + " 缺少 edges(边引用数组)。每项 {\"face\":\"face:<Face.ID>\",\"edge\":0-based}。",
                    new { action = "provide", field = "edges",
                          format = "[{\"face\":\"face:71\",\"edge\":0}]" });
                yield break;
            }

            foreach (var er in s.Edges)
            {
                if (er.ParseError != null)
                    yield return ctx.Error("E205", "edges",
                        "边引用不合法:" + er.ParseError + "。",
                        new { action = "fix", field = "edges", format = "{\"face\":\"face:<Face.ID>\",\"edge\":0}" });
            }
        }
    }

    /// <summary>E405:fillet 必须给 radius 且 &gt; 0。</summary>
    public sealed class FilletRule : IFeatureRule
    {
        public string Code { get { return "E405"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "fillet" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (!s.Radius.HasValue)
                yield return ctx.Error("E405", "radius", "fillet 必须提供 radius(米)。",
                    new { action = "provide", field = "radius", unit = "m" });
            else if (s.Radius.Value <= 0)
                yield return ctx.Error("E405", "radius", "radius 必须 > 0(当前 " + GeoUtil.Fmt(s.Radius.Value) + ")。",
                    new { action = "set", field = "radius", must = "> 0", unit = "m" });
        }
    }

    /// <summary>E406:chamfer 必须给 distance 且 &gt; 0。</summary>
    public sealed class ChamferRule : IFeatureRule
    {
        public string Code { get { return "E406"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "chamfer" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (!s.HasDistance)
                yield return ctx.Error("E406", "distance", "chamfer 必须提供 distance(米)。",
                    new { action = "provide", field = "distance", unit = "m" });
            else if (s.Distance <= 0)
                yield return ctx.Error("E406", "distance", "distance 必须 > 0(当前 " + GeoUtil.Fmt(s.Distance) + ")。",
                    new { action = "set", field = "distance", must = "> 0", unit = "m" });
        }
    }

    /// <summary>
    /// E407:rib 必须给 thickness &gt; 0,且轮廓是【闭合】环。
    /// SE 2022 实测:开放链经 Ribs.Add 不出几何,闭合轮廓(画在实体表面贴合面上)才出。
    /// </summary>
    public sealed class RibRule : IFeatureRule
    {
        public string Code { get { return "E407"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "rib" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (!s.Thickness.HasValue)
                yield return ctx.Error("E407", "thickness", "rib 必须提供 thickness(米)。",
                    new { action = "provide", field = "thickness", unit = "m" });
            else if (s.Thickness.Value <= 0)
                yield return ctx.Error("E407", "thickness", "thickness 必须 > 0(当前 " + GeoUtil.Fmt(s.Thickness.Value) + ")。",
                    new { action = "set", field = "thickness", must = "> 0", unit = "m" });

            if (s.ShapeError != null)
            {
                yield return ctx.Error("E407", "polygon",
                    "rib 需要【闭合】轮廓(rect/polygon/loops/circle)——SE 2022 的 Ribs.Add 通道不支持开放链画法。",
                    new { action = "provide", field = "polygon",
                          format = "[[u,v],...]",
                          hint = "轮廓画在与实体表面贴合的平面上,如板底面用 RefPlane_1" });
            }
        }
    }

    /// <summary>
    /// E408 / W408:pattern 的 of / counts / spacing。
    /// E408 = 必然失败(缺 of、counts 非法、count&gt;1 却没给 spacing);
    /// W408 = of 在本批之前找不到同名特征——可能引用的是更早调用或外部特征,不一定是错,只提醒。
    /// </summary>
    public sealed class PatternRule : IFeatureRule
    {
        public string Code { get { return "E408"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "pattern" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (string.IsNullOrWhiteSpace(s.Of))
            {
                yield return ctx.Error("E408", "of",
                    "pattern 必须提供 of(被阵列特征:本批前面特征的 name、SE 特征名或 obj-K 句柄)。",
                    new { action = "provide", field = "of" });
            }
            else if (!s.Of.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                // 本批之前找同名 name:找不到只提醒(可能是上一次调用/外部已建的特征)
                bool foundInBatch = false;
                for (int i = 0; i < ctx.Index; i++)
                {
                    if (string.Equals(ctx.Specs[i].Name, s.Of, StringComparison.OrdinalIgnoreCase))
                    {
                        foundInBatch = true;
                        break;
                    }
                }
                if (!foundInBatch)
                    yield return ctx.Warn("W408", "of",
                        "of=\"" + s.Of + "\" 在本批之前的特征里没有同名 name。若它不是 SE 已有特征名(见前面返回的 feature 字段),运行时会报\"未找到被阵列特征\"。",
                        new { action = "verify", field = "of" });
            }

            int xc = s.XCount ?? 1, yc = s.YCount ?? 1;
            if (s.XCount.HasValue && s.XCount.Value < 1)
                yield return ctx.Error("E408", "xcount", "xcount 必须 >= 1(当前 " + s.XCount.Value + ")。",
                    new { action = "set", field = "xcount", min = 1 });
            if (s.YCount.HasValue && s.YCount.Value < 1)
                yield return ctx.Error("E408", "ycount", "ycount 必须 >= 1(当前 " + s.YCount.Value + ")。",
                    new { action = "set", field = "ycount", min = 1 });
            if (xc == 1 && yc == 1)
                yield return ctx.Error("E408", "xcount",
                    "xcount 与 ycount 不能同时为 1——至少一个方向要阵列,否则没有可阵列的东西。",
                    new { action = "set", field = "xcount", hint = "如 xcount=3 + xspacing=0.02" });

            if (xc > 1 && !s.XSpacing.HasValue)
                yield return ctx.Error("E408", "xspacing",
                    "xcount=" + xc + " 时必须提供 xspacing(米,> 0)。",
                    new { action = "provide", field = "xspacing", unit = "m" });
            if (yc > 1 && !s.YSpacing.HasValue)
                yield return ctx.Error("E408", "yspacing",
                    "ycount=" + yc + " 时必须提供 yspacing(米,> 0)。",
                    new { action = "provide", field = "yspacing", unit = "m" });
        }
    }

    // ==================== 毛坯估算 / 平面解析(供 W403 用) ====================

    /// <summary>
    /// 默认参考面的 u/v → 全局轴映射(2026-09-13/15 Convert3DCoordinate 坐标级探针拍板;
    /// 2026-08-22 目检实测已作废——当时把 RefPlane_2/3 记反并在此固化过,订正时须同步排查代码引用):
    ///   RefPlane_1 = XY 面:u→X, v→Y, 法向轴 Z
    ///   RefPlane_2 = YZ 面:u→Y, v→Z, 法向轴 X
    ///   RefPlane_3 = XZ 面:u→X, v→Z, 法向轴 Y
    /// 只在默认面上做估算;@别名若最终能追到默认面,则按 base + distance 叠加偏移。
    /// </summary>
    public static class PlaneResolver
    {
        /// <summary>解析出"底层默认面索引(1..3)"与沿其法向的偏移(米)。</summary>
        public static bool TryResolve(List<FeatureSpec> specs, int beforeIndex, string planeRef,
            out int planeIdx, out double offset)
        {
            planeIdx = 0;
            offset = 0;
            if (string.IsNullOrWhiteSpace(planeRef)) return false;
            return Resolve(specs, beforeIndex, planeRef, 0, out planeIdx, out offset);
        }

        private static bool Resolve(List<FeatureSpec> specs, int beforeIndex, string planeRef, int depth,
            out int planeIdx, out double offset)
        {
            planeIdx = 0;
            offset = 0;
            if (depth > 8) return false;   // 防循环引用

            if (planeRef.StartsWith("RefPlane", StringComparison.OrdinalIgnoreCase))
            {
                string idxStr = planeRef.Substring("RefPlane".Length).TrimStart('_', ' ', '-');
                int idx;
                if (!int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) return false;
                if (idx < 1 || idx > FeatureValidator.DefaultRefPlaneCount) return false;
                planeIdx = idx;
                offset = 0;
                return true;
            }

            if (planeRef.StartsWith("@", StringComparison.Ordinal))
            {
                string key = planeRef.Substring(1);
                for (int i = beforeIndex - 1; i >= 0; i--)
                {
                    var s = specs[i];
                    if (s.OpLower != "plane" || string.IsNullOrEmpty(s.Name)) continue;
                    if (!string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase)) continue;

                    int baseIdx;
                    double baseOffset;
                    if (!Resolve(specs, i, s.BaseRef, depth + 1, out baseIdx, out baseOffset)) return false;
                    planeIdx = baseIdx;
                    offset = baseOffset + s.Distance;
                    return true;
                }
                return false;
            }

            return false;   // obj-K 或其它:没有确定的全局映射,不判
        }

        /// <summary>取平面的 [u轴, v轴, 法向轴](0=X, 1=Y, 2=Z)。</summary>
        public static int[] Axes(int planeIdx)
        {
            switch (planeIdx)
            {
                case 1: return new[] { 0, 1, 2 };   // XY,法向 Z
                case 2: return new[] { 1, 2, 0 };   // YZ,法向 X(2026-09-13 探针拍板,旧记 XZ 已作废)
                case 3: return new[] { 0, 2, 1 };   // XZ,法向 Y(2026-09-13 探针拍板,旧记 YZ 已作废)
                default: return null;
            }
        }
    }

    /// <summary>毛坯包围盒估算:把所有可解析的 extrude 的"草图包围盒 × 深度"并起来。</summary>
    public static class StockEstimator
    {
        /// <summary>返回 [xmin,ymin,zmin,xmax,ymax,zmax],无法估算时返回 null。</summary>
        public static double[] Estimate(List<FeatureSpec> specs)
        {
            double[] stock = null;
            for (int i = 0; i < specs.Count; i++)
            {
                var s = specs[i];
                if (s.OpLower != "extrude") continue;
                if (!s.Depth.HasValue || s.Depth.Value <= 0) continue;

                var bb = FeatureBBox(specs, i, null);
                if (bb == null) continue;
                stock = stock == null ? bb : Union(stock, bb);
            }
            return stock;
        }

        /// <summary>
        /// 单个特征的世界包围盒。
        /// stock 传 null 表示"正在算毛坯自身";非 null 时,若深度未知(cut 的 next/all)
        /// 则沿法向轴借用毛坯的厚度。
        /// </summary>
        public static double[] FeatureBBox(List<FeatureSpec> specs, int index, double[] stock)
        {
            var s = specs[index];
            int planeIdx;
            double offset;
            if (!PlaneResolver.TryResolve(specs, index, s.PlaneRef, out planeIdx, out offset)) return null;

            var axes = PlaneResolver.Axes(planeIdx);
            if (axes == null) return null;
            int ua = axes[0], va = axes[1], na = axes[2];

            // 草图在 u/v 上的范围
            double[] local;
            if (s.HasCircle)
                local = new[] { s.CircleX - s.CircleR, s.CircleY - s.CircleR, s.CircleX + s.CircleR, s.CircleY + s.CircleR };
            else if (s.HasSlot)
            {
                // 旋转矩形的轴对齐包围盒:长轴半长与短轴半长各自在 u/v 上的投影之和
                double dx = Math.Cos(s.SlotAngle), dy = Math.Sin(s.SlotAngle);
                double eu = Math.Abs(dx) * s.SlotLength / 2.0 + Math.Abs(-dy) * s.SlotWidth / 2.0;
                double ev = Math.Abs(dy) * s.SlotLength / 2.0 + Math.Abs(dx) * s.SlotWidth / 2.0;
                local = new[] { s.SlotX - eu, s.SlotY - ev, s.SlotX + eu, s.SlotY + ev };
            }
            else if (s.Loops.Count > 0)
            {
                local = GeoUtil.BBox(s.Loops[0]);
                for (int k = 1; k < s.Loops.Count; k++)
                {
                    var b = GeoUtil.BBox(s.Loops[k]);
                    local[0] = Math.Min(local[0], b[0]);
                    local[1] = Math.Min(local[1], b[1]);
                    local[2] = Math.Max(local[2], b[2]);
                    local[3] = Math.Max(local[3], b[3]);
                }
            }
            else return null;

            var box = new double[6];
            box[ua] = local[0]; box[ua + 3] = local[2];
            box[va] = local[1]; box[va + 3] = local[3];

            int side = s.Side ?? (s.OpLower == "cut" ? 1 : 2);
            double d = s.Depth ?? 0;

            if (s.Depth.HasValue)
            {
                if (side == 2) { box[na] = offset; box[na + 3] = offset + d; }
                else if (side == 1) { box[na] = offset - d; box[na + 3] = offset; }
                else { box[na] = offset - d; box[na + 3] = offset + d; }
            }
            else if (stock != null)
            {
                // next / all:沿法向穿过去,借用毛坯在该轴上的范围
                box[na] = stock[na];
                box[na + 3] = stock[na + 3];
            }
            else
            {
                return null;
            }

            return box;
        }

        public static double[] Union(double[] a, double[] b)
        {
            return new[]
            {
                Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Min(a[2], b[2]),
                Math.Max(a[3], b[3]), Math.Max(a[4], b[4]), Math.Max(a[5], b[5])
            };
        }

        public static bool Intersects(double[] a, double[] b)
        {
            for (int i = 0; i < 3; i++)
            {
                if (a[i + 3] < b[i] || b[i + 3] < a[i]) return false;
            }
            return true;
        }

        public static string FmtBox(double[] b)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(GeoUtil.Fmt(b[i]));
            }
            return sb.ToString();
        }
    }
}
