using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 声明式建模的"语法单元级 IR":features JSON 解析后的中间表示。
    ///
    /// 构建器(ModelingTools)与静态校验器(FeatureValidator)【共用这一份解析】——
    /// 这是本层存在的唯一理由:若校验器另写一套解析,二者行为必然漂移,
    /// 校验就会退化成"看起来对但实际不管用"。
    ///
    /// 字段语义与单位:
    ///   坐标/长度一律【米】(UI 显示 mm,×1000);角度为弧度。
    ///   Loops 里的点是【目标平面的局部 u/v】,不是全局 XYZ。
    ///
    /// 2026-09-08 抽出。取数语义与改造前逐条对齐,刻意保留了几个历史行为:
    ///   - op 不是字符串(缺失/数字)时回退 "extrude";
    ///   - circle 半径 <= 0 视为"没有圆",回退走直线环;
    ///   - side/profileside/depth 缺字段或类型不可解析 → 记 null,由各 op 套自己的默认值
    ///     (extrude side 默认 2、cut side 默认 1,两边不一样,所以默认值留在 op 里)。
    ///
    /// 架构思想参考 SolidPilot(见 SolidPilot建模思路学习总结.md「报错带诊断」一条),
    /// 但本文件是面向 Solid Edge API 的独立实现,不含其代码或 schema 文本。
    /// </summary>
    public sealed class FeatureSpec
    {
        /// <summary>在 features 数组中的序号(0-based),用于报错定位。</summary>
        public int Index;

        /// <summary>原始 op 字符串(未改大小写,用于回显);缺失/非字符串时为 "extrude"。</summary>
        public string Op = "extrude";

        /// <summary>op 的小写形式,switch 用它。</summary>
        public string OpLower = "extrude";

        public string Name;

        /// <summary>"plane" 字段(草图所在面)。</summary>
        public string PlaneRef;

        /// <summary>
        /// plane 的面锚定声明(2026-10-03):plane 写 "face:&lt;Face.ID&gt;" 或 "face:±X/±Y/±Z" 时非 null。
        /// 语义由 Coords 决定(2026-10-04 扩展):
        ///   coords 缺省/"global" → 轮廓坐标为【全局世界坐标】,由执行层投影到该面所在平面
        ///     (世界 2D 语义:法向 Z→(X,Y)、法向 X→(Y,Z)、法向 Y→(X,Z));仅支持轴向面。
        ///   coords "local" → 轮廓坐标为【该面贴面参考平面的局部 u/v】(与普通 RefPlane 草图同构),
        ///     不投影、不绕向归一;支持任意平面面(含斜面),原点/轴向由 SE 决定。
        /// null = 不是 face 来源(走 RefPlane_N / @别名 / obj-K)。
        /// </summary>
        public FacePlaneRef FacePlaneRef;

        /// <summary>coords 字段原始值(小写);null = 未给(= global)。</summary>
        public string Coords;

        /// <summary>coords:"local" → true(轮廓按面局部 u/v 解释)。仅对 face 平面有意义,由校验层把关。</summary>
        public bool CoordsLocal;

        /// <summary>"base" 字段(plane op 的基准面)。</summary>
        public string BaseRef;

        /// <summary>plane op 的偏移距离(米);缺字段时为 0。</summary>
        public double Distance;

        public bool HasDistance;

        /// <summary>extrude 必须、cut(mode=finite)可选;缺字段或类型不可解析时为 null。</summary>
        public double? Depth;

        /// <summary>ProfilePlaneSide。null = 未给,由 op 套默认值。</summary>
        public int? Side;

        /// <summary>ProfileSide。null = 未给,由 op 套默认值。</summary>
        public int? ProfileSide;

        /// <summary>
        /// 挤出/除料方向(世界坐标,单位向量,2026-09-29 新增):给了 dir 且未显式给 side 时,
        /// 构建器用平面带符号法向 dot(dir, n) 直接算 side(>0→2,<0→1),不再依赖默认值/自动重试猜方向。
        /// 带符号法向的已知来源:RefPlane_1=+Z、RefPlane_2=+X、RefPlane_3=−Y(2026-09-28 挤出探针实测);
        /// plane op 派生面继承基准面法向。其它来源(obj-K 引用的外部面)解析不到 → 回退默认 side 并带 warning。
        /// </summary>
        public double[] Dir;

        /// <summary>是否提供了有效的 dir(3 个数字)。</summary>
        public bool HasDir;

        /// <summary>dir 解析失败原因;null = 可用(未给 dir 也为 null)。</summary>
        public string DirParseError;

        // ---- 体积对账(2026-09-29 新增,extrude/cut/hole 通用) ----

        /// <summary>调用方声明的模型体积增量区间(毫米³)。extrude>0、cut/hole<0;null = 未声明不校验。</summary>
        public double? ExpectVolMin;

        public double? ExpectVolMax;

        /// <summary>是否声明了体积增量期望(给了 expectvolumedelta)。</summary>
        public bool HasExpectVol;

        /// <summary>expectvolumedelta 解析失败原因;null = 可用。</summary>
        public string ExpectVolParseError;

        /// <summary>cut 的除料方式:next / all / finite;缺失时为 null(op 套 "next")。</summary>
        public string Mode;

        /// <summary>
        /// 旋转轴(revolve 专用)两点:[[u1,v1],[u2,v2]] 或 [u1,v1,u2,v2]。
        /// 局部 u/v 坐标(米)。轴是【独立于截面】的构造线,不参与截面闭环约束。
        /// </summary>
        public double[] AxisP1, AxisP2;

        /// <summary>
        /// 圆角半径(fillet 专用,米)。null = 未给,由校验/构建报错。
        /// </summary>
        public double? Radius;

        /// <summary>
        /// 边引用数组(fillet/chamfer 专用):被倒圆/倒角的已有实体边。
        /// </summary>
        public List<EdgeRefSpec> Edges = new List<EdgeRefSpec>();

        /// <summary>是否提供了有效的 edges 数组(至少 1 条可解析)。</summary>
        public bool HasEdges;

        /// <summary>筋板厚度(rib 专用,米)。null = 未给。</summary>
        public double? Thickness;

        /// <summary>被阵列的特征名(pattern 专用):本批之前特征的 name、SE 特征名或 obj-K 句柄。</summary>
        public string Of;

        /// <summary>X/Y 方向阵列个数(pattern 专用)。null = 未给。</summary>
        public int? XCount, YCount;

        /// <summary>X/Y 方向阵列间距(pattern 专用,米)。null = 未给。</summary>
        public double? XSpacing, YSpacing;

        /// <summary>是否提供了有效旋转轴(写了且两点不退化)。</summary>
        public bool HasAxis;

        // ---- hole 专用(2026-09-23 P1) ----

        /// <summary>
        /// 孔径(hole 便捷写法,米)。给了且无 circle/circles 时合成单圆轮廓(radius = diameter/2)。
        /// </summary>
        public double? Diameter;

        /// <summary>孔中心(hole 便捷写法,目标平面局部 u/v,米)。与 diameter 配套,缺省 [0,0]。</summary>
        public double[] Center;

        /// <summary>旋转角(弧度);null = 未给,由 op 套默认 2π(360°)。</summary>
        public double? Angle;

        /// <summary>旋转角(度);给了则换算成弧度覆盖 Angle,方便人写。 </summary>
        public double? Degrees;

        // ---- P2 多轮廓(2026-09-23):loft / sweep / helix ----

        /// <summary>
        /// profiles 数组(loft/sweep/helix 专用):每项是 {plane, 形状字段, origin?} 的"迷你特征",
        /// 复用同一份 Parse 解析(形状语义与顶层完全一致);项的 Index 记数组内序号,报错定位用。
        /// </summary>
        public List<FeatureSpec> Profiles = new List<FeatureSpec>();

        /// <summary>是否提供了可枚举的 profiles 数组(≥1 项;项内容合法性由校验器 E410/E411/E412 判)。</summary>
        public bool HasProfiles;

        /// <summary>profiles 字段存在但不是数组/为空时的错误信息;null=无问题。</summary>
        public string ProfilesError;

        /// <summary>
        /// 显式截面锚点(局部 u/v,米):loft/sweep 的 Origins 用。
        /// null=按形状自动推导:circle→圆心,rect/polygon/loops→首点。
        /// SDK 文档:周期截面(圆/椭圆)可传 0,非周期截面必须是轮廓上真实一点,否则静默无几何。
        /// </summary>
        public double[] Origin;

        /// <summary>螺距(helix 专用,米)。</summary>
        public double? Pitch;

        /// <summary>螺旋总高度(helix 专用,米)。</summary>
        public double? Height;

        /// <summary>圈数(helix 专用,可为小数如 2.5 圈)。</summary>
        public double? Revolutions;

        /// <summary>
        /// 开放链点列(sweep 首项专用):sweep 的路径用 polygon 声明时按【开放链】解释
        /// (≥2 点,不自动闭合);null=不是开放链。局部 u/v,米。
        /// </summary>
        public double[][] OpenChain;

        /// <summary>
        /// 显式路径段列(sweep 首项专用,trace 字段,2026-10-04):线段 + 真圆弧混排;
        /// null = 未声明 trace(回退 polygon 开放链或闭合轮廓)。与 OpenChain/形状字段互斥。
        /// </summary>
        public List<PathSegment> Trace;

        /// <summary>trace 字段存在但解析失败时的原因;null = 无问题(含"未给 trace")。</summary>
        public string TraceError;

        // ---- P3 面引用机制(2026-09-23):draft / thicken / delete_face ----

        /// <summary>
        /// 面引用声明(JSON 对象):{"faceOf":"@base","faceNormal":[0,0,1]} 或 {"faceOf":"@base","faceIndex":0}。
        /// faceOf 必填(@别名 或 obj-K 句柄);faceNormal 与 faceIndex 二选一,都给则先 normal 过滤再按 index 选。
        /// null=未声明(非面引用类 op 不需要)。
        /// </summary>
        public FaceRefSpec FaceRef;

        /// <summary>是否提供了可解析的面引用(即使有 ParseError 也算"声明过")。</summary>
        public bool HasFaceRef;

        /// <summary>split 的目标特征/模型引用(@别名 或 obj-K);缺省用 Models.Item(1)。</summary>
        public string Target;

        /// <summary>破坏性操作的二次确认(delete_face 专用);默认 false,op 在 confirm!=true 时拒绝。</summary>
        public bool? Confirm;

        /// <summary>草图可见性。null = 未给,默认 false(创建即隐藏)。</summary>
        public bool? Visible;

        /// <summary>自动补全几何约束:按坐标推断 H/V(|dy|&lt;eps → 水平,|dx|&lt;eps → 垂直)。null = 未给,默认 false。</summary>
        public bool? AutoConstraint;

        /// <summary>固定首环首线起点(AddKeypointFix,消除整体平移自由度)。null = 未给,默认 false。</summary>
        public bool? FixOrigin;

        /// <summary>直线长度标注声明(轮廓 End 之前的开放上下文里应用,这是绑定生效的唯一窗口)。</summary>
        public List<DimSpec> Dims = new List<DimSpec>();

        /// <summary>是否为有效圆轮廓(radius &gt; 0 才算)。</summary>
        public bool HasCircle;

        public double CircleX, CircleY, CircleR;

        /// <summary>
        /// 多真圆(circles 数组):每项 [x,y,r]。用于一次切/拉多个真圆(如法兰螺栓孔阵列),
        /// 比 loops 多边形近似更准(孔壁是真圆柱面)。
        /// </summary>
        public List<double[]> Circles = new List<double[]>();

        /// <summary>是否提供了有效的 circles 数组(至少 1 个 r&gt;0)。</summary>
        public bool HasCircles;

        /// <summary>
        /// 是否为有效腰孔(长圆孔)轮廓。用【真圆弧】构造:两条直线边 + 两端半圆弧,
        /// 不像 polygon 那样用折线近似——折线近似会在两端留下可见的棱。
        /// </summary>
        public bool HasSlot;

        /// <summary>腰孔中心(局部 u/v,米)。</summary>
        public double SlotX, SlotY;

        /// <summary>腰孔总长(含两端半圆)与宽度(即两端半圆的直径),米。</summary>
        public double SlotLength, SlotWidth;

        /// <summary>腰孔长轴相对局部 u 轴的转角(弧度),默认 0。</summary>
        public double SlotAngle;

        /// <summary>形状来源:circle / loops / rect / polygon / ""(都没有)。</summary>
        public string ShapeSource = "";

        /// <summary>归一化后的闭合环点列(局部 u/v,米)。未走圆轮廓时才有值。</summary>
        public List<double[][]> Loops = new List<double[][]>();

        /// <summary>无 circle 且解析不出任何环时的错误信息;为 null 表示形状可用。</summary>
        public string ShapeError;

        /// <summary>已知字段白名单之外的字段名,供 W102 提示(可能拼错/使用了不支持的字段)。</summary>
        public List<string> UnknownFields = new List<string>();

        /// <summary>预留:Profile.End 模式。当前构建器硬编码 End(0),尚未启用本字段。</summary>
        public string EndMode;

        /// <summary>原始 JSON(Clone 过,可安全跨作用域持有),供校验器做类型/未知字段等细查。</summary>
        public JsonElement Raw;

        /// <summary>该特征是否带草图形状(plane/fillet/chamfer/pattern 不需要;rib 需要开放链,单独放宽)。</summary>
        public bool NeedsShape
        {
            get
            {
                return OpLower == "extrude" || OpLower == "cut" || OpLower == "revolve" || OpLower == "rib";
            }
        }
    }

    /// <summary>
    /// 边引用声明(fillet/chamfer 专用)。
    /// face 写 "face:&lt;Face.ID&gt;"(Face.ID 是唯一稳定索引,见 L2 modeling-recipes §七)或纯整数;
    /// edge 是该面内 0-based 边索引(运行期转 COM 1-based)。
    /// </summary>
    public sealed class EdgeRefSpec
    {
        /// <summary>目标面的 Face.ID。</summary>
        public int FaceId;

        /// <summary>该面内 0-based 边索引。</summary>
        public int EdgeIndex;

        /// <summary>解析失败原因;null = 可用。</summary>
        public string ParseError;
    }

    /// <summary>
    /// 面引用声明(P3: draft / thicken / delete_face 专用)。
    /// faceOf 必填:@别名(本批前面特征的 name) 或 obj-K 句柄(跨批);
    /// faceNormal(3 元数组,世界系 XYZ)与 faceIndex(非负整数)二选一——
    ///   给 faceNormal:从特征产出的平面面集合中,按法向点积 &gt; 1-ε 命中(多面命中取首并 warning);
    ///   给 faceIndex:按 0-based 序号直接取(运行期转 COM 1-based)。
    /// 都给则先 normal 过滤再按 index 选;都没给由 op 决定默认(如 draft 取首平面面)。
    /// </summary>
    public sealed class FaceRefSpec
    {
        /// <summary>特征别名 "@name" 或句柄 "obj-K";必填。</summary>
        public string FeatureName;

        /// <summary>世界系法向(3 元数组);null=未给。</summary>
        public double[] Normal;

        /// <summary>0-based 面序号;null=未给。</summary>
        public int? Index;

        /// <summary>解析失败原因;null = 可用。</summary>
        public string ParseError;
    }

    /// <summary>
    /// plane 字段的"面锚定"声明(2026-10-03 IR 面平面:extrude / cut / hole)。
    /// 两种写法:
    ///   "face:26"  → Kind=Id,   FaceId=26      (按 Face.ID 直接定位)
    ///   "face:+Z"  → Kind=Axis, Axis="+Z"      (±X/±Y/±Z 轴向选择器)
    /// 坐标语义由顶层 coords 字段决定(2026-10-04):
    ///   缺省/"global" → 全局世界坐标,执行层投影(仅轴向面);
    ///   "local"       → 该面贴面参考平面的局部 u/v,不投影(支持斜面)。
    /// Kind=Axis 时正负号【不参与匹配】(投影法只能定出法向轴、定不出朝向):
    /// 在法向轴为 Z 的面里取面积最大者;若同时存在两个同向面则报错,要求改用 face:&lt;ID&gt;。
    /// </summary>
    public sealed class FacePlaneRef
    {
        /// <summary>引用种类:"Id" 或 "Axis"。</summary>
        public string Kind;

        /// <summary>Kind=Id 时的 Face.ID。</summary>
        public int FaceId;

        /// <summary>Kind=Axis 时的规范轴向符号:"+X"/"-X"/"+Y"/"-Y"/"+Z"/"-Z"。</summary>
        public string Axis;

        /// <summary>解析失败原因;null = 可用。</summary>
        public string ParseError;
    }

    /// <summary>
    /// 直线长度标注声明。"element" 是跨环扁平的 0-based 线索引(第 0 环的线排最前,轴/构造线不占位)。
    /// "name" → PutName 进变量表(标注即变量);"value" → 直接值(如 "40 mm");"formula" → 公式(如 "Rad1 - 5 mm")。
    /// </summary>
    public sealed class DimSpec
    {
        public int Element;

        public string Name;

        public string Value;

        public string Formula;

        /// <summary>解析失败原因;null = 可用。</summary>
        public string ParseError;
    }

    /// <summary>
    /// sweep 路径的一段(trace 数组元素,2026-10-04):直线或【真圆弧】。
    /// 局部 u/v,米。与 polygon 折线不同,圆弧走 Arcs2d.AddByCenterStartEnd(真弧,不留折棱)。
    /// </summary>
    public sealed class PathSegment
    {
        /// <summary>"line" 或 "arc"。</summary>
        public string Kind;

        /// <summary>line 起点 / arc 起点(局部 u/v,米)。</summary>
        public double[] P0;

        /// <summary>line 终点 / arc 终点(局部 u/v,米)。</summary>
        public double[] P1;

        /// <summary>arc 圆心(仅 arc;局部 u/v,米)。</summary>
        public double[] Center;
    }

    /// <summary>
    /// features JSON → FeatureSpec 的解析器。
    ///
    /// 纯函数、不碰 COM,SE 没启动也能跑——这是校验层能"事前拦截"的前提。
    /// </summary>
    public static class FeatureSpecParser
    {
        /// <summary>顶层已知字段白名单(大小写不敏感)。center/radius 是 circle 的子字段,不在顶层。</summary>
        private static readonly HashSet<string> KnownFields =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "op", "name", "plane", "base", "distance", "depth",
                "side", "profileside", "mode", "visible",
                "coords",   // 2026-10-04:face 平面坐标模式 global(默认)|local(面局部 u/v,支持斜面)
                "dir", "expectvolumedelta",   // 2026-09-29:显式方向 + 体积对账(extrude/cut/hole)
                "circle", "circles", "slot", "rect", "polygon", "loops",
                "axis", "angle", "degrees",   // revolve 专用
                "autoconstraint", "fixorigin", "dims",   // 2026-09-13:完全约束+变量绑定(End 前应用)
                "endmode",   // 预留
                // 2026-09-22 扩 op:fillet / chamfer / rib / pattern
                "radius", "edges",           // fillet/chamfer 专用
                "thickness",                 // rib 专用
                "of", "xcount", "ycount", "xspacing", "yspacing",   // pattern 专用
                // 2026-09-23 扩 op:hole(method 为 mode 的别名;center+diameter 为便捷写法)
                "diameter", "center", "method",
                // 2026-09-23 P2 多轮廓:loft / sweep / helix
                "profiles", "origin", "pitch", "height", "revolutions",
                "trace",   // 2026-10-04:sweep 路径的线段+真圆弧混排声明(profiles[0] 专用)
                "fillet",  // 2026-10-04:sweep 路径的圆角半径(配 polygon 顶点链,自动求切弧)
                // 2026-09-23 P3 面引用机制:draft / thicken / delete_face / split / web_network
                "faceOf", "faceNormal", "faceIndex", "confirm", "target"
            };

        public static List<FeatureSpec> ParseAll(JsonElement[] features)
        {
            var list = new List<FeatureSpec>();
            if (features == null) return list;
            for (int i = 0; i < features.Length; i++)
                list.Add(Parse(features[i], i));
            return list;
        }

        public static FeatureSpec Parse(JsonElement feat, int index)
        {
            var s = new FeatureSpec { Index = index };

            if (feat.ValueKind == JsonValueKind.Object)
                s.Raw = feat.Clone();

            s.Op = GetStr(feat, "op") ?? "extrude";
            s.OpLower = s.Op.ToLowerInvariant();
            s.Name = GetStr(feat, "name");
            s.PlaneRef = GetStr(feat, "plane");
            s.FacePlaneRef = ParseFacePlaneRef(s.PlaneRef);   // 2026-10-03:plane 的面锚定("face:26" / "face:+Z")
            // 2026-10-04:coords(global 默认|local 面局部 u/v)。原始值(小写)存 s.Coords 供校验层报非法值;
            // 这里只认 "local",其它字符串不在此报错。
            string coordsRaw = GetStr(feat, "coords");
            if (coordsRaw != null)
            {
                s.Coords = coordsRaw.ToLowerInvariant();
                s.CoordsLocal = s.Coords == "local";
            }
            s.BaseRef = GetStr(feat, "base");
            s.Mode = GetStr(feat, "mode") ?? GetStr(feat, "method");   // hole 可用 method 作 mode 别名
            s.EndMode = GetStr(feat, "endmode");

            // hole 便捷写法:diameter(米) + center(局部 u/v,缺省 [0,0])
            if (TryGetDbl(feat, "diameter", out double holeDia)) s.Diameter = holeDia;
            s.Center = TryGetPoint2(feat, "center");

            // P2 多轮廓(2026-09-23):显式截面锚点 + helix 参数(pitch/height/revolutions)
            s.Origin = TryGetPoint2(feat, "origin");
            if (TryGetDbl(feat, "pitch", out double pitch)) s.Pitch = pitch;
            if (TryGetDbl(feat, "height", out double hgt)) s.Height = hgt;
            if (TryGetDbl(feat, "revolutions", out double revs)) s.Revolutions = revs;

            // P3 面引用机制(2026-09-23):draft / thicken / delete_face / split / web_network
            s.FaceRef = ParseFaceRef(feat);
            s.HasFaceRef = feat.TryGetProperty("faceOf", out _);
            s.Target = GetStr(feat, "target");
            if (TryGetBool(feat, "confirm", out bool cf)) s.Confirm = cf;

            // revolve 专用:旋转轴与角度
            if (TryGetAxis(feat, out double[] ap1, out double[] ap2))
            {
                s.HasAxis = true;
                s.AxisP1 = ap1;
                s.AxisP2 = ap2;
            }
            if (TryGetDbl(feat, "angle", out double ang)) s.Angle = ang;
            if (TryGetDbl(feat, "degrees", out double deg)) s.Degrees = deg;

            if (TryGetDbl(feat, "distance", out double dist)) { s.Distance = dist; s.HasDistance = true; }
            if (TryGetDbl(feat, "depth", out double d)) s.Depth = d;
            if (TryGetDbl(feat, "radius", out double rad)) s.Radius = rad;
            if (TryGetDbl(feat, "thickness", out double thk)) s.Thickness = thk;
            if (TryGetInt(feat, "side", out int sd)) s.Side = sd;
            if (TryGetInt(feat, "profileside", out int ps)) s.ProfileSide = ps;

            // 2026-09-29:dir(世界坐标 3 分量方向向量)+ expectvolumedelta(模型体积增量区间,毫米³)
            if (feat.TryGetProperty("dir", out var dirEl))
            {
                if (dirEl.ValueKind == JsonValueKind.Array && dirEl.GetArrayLength() == 3
                    && TryGetNum(dirEl[0], out double dx) && TryGetNum(dirEl[1], out double dy) && TryGetNum(dirEl[2], out double dz))
                {
                    double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (len > 1e-12)
                    {
                        s.Dir = new[] { dx / len, dy / len, dz / len };   // 归一化,后续 dot 只看符号
                        s.HasDir = true;
                    }
                    else
                        s.DirParseError = "dir 是零向量";
                }
                else
                    s.DirParseError = "dir 必须是 3 个数字的数组,如 \"dir\":[0,0,1]";
            }
            if (feat.TryGetProperty("expectvolumedelta", out var evdEl))
            {
                if (evdEl.ValueKind == JsonValueKind.Number && evdEl.TryGetDouble(out double ev1))
                {
                    s.ExpectVolMin = ev1;
                    s.ExpectVolMax = ev1;
                    s.HasExpectVol = true;
                }
                else if (evdEl.ValueKind == JsonValueKind.Array && evdEl.GetArrayLength() == 2
                    && TryGetNum(evdEl[0], out double evMin) && TryGetNum(evdEl[1], out double evMax))
                {
                    s.ExpectVolMin = Math.Min(evMin, evMax);
                    s.ExpectVolMax = Math.Max(evMin, evMax);
                    s.HasExpectVol = true;
                }
                else
                    s.ExpectVolParseError = "expectvolumedelta 必须是数字或 [下限,上限](毫米³),如 \"expectvolumedelta\":-1250 或 [-1300,-1200]";
            }
            if (TryGetInt(feat, "xcount", out int xc)) s.XCount = xc;
            if (TryGetInt(feat, "ycount", out int yc)) s.YCount = yc;
            if (TryGetDbl(feat, "xspacing", out double xs)) s.XSpacing = xs;
            if (TryGetDbl(feat, "yspacing", out double ys)) s.YSpacing = ys;
            s.Of = GetStr(feat, "of");
            if (TryGetBool(feat, "visible", out bool vb)) s.Visible = vb;
            if (TryGetBoolCI(feat, "autoconstraint", out bool ac)) s.AutoConstraint = ac;
            if (TryGetBoolCI(feat, "fixorigin", out bool fo)) s.FixOrigin = fo;
            if (feat.TryGetProperty("dims", out var dimsEl) && dimsEl.ValueKind == JsonValueKind.Array)
            {
                int dimIdx = 0;
                foreach (var item in dimsEl.EnumerateArray())
                {
                    var ds = new DimSpec();
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        ds.ParseError = "第 " + dimIdx + " 项不是对象";
                    }
                    else
                    {
                        if (item.TryGetProperty("element", out var elEl) && elEl.ValueKind == JsonValueKind.Number)
                        {
                            // 必须用 TryGetInt32:写成 1.5 这类小数时 GetInt32 会抛 InvalidOperationException,
                            // 一路把校验/构建带崩(2026-09-14),降级成 ParseError 由校验器报出。
                            if (elEl.TryGetInt32(out int elIdx)) ds.Element = elIdx;
                            else ds.ParseError = "element 必须是整数(当前 " + elEl.GetRawText() + ")";
                        }
                        else
                            ds.ParseError = "缺 element(0-based 线索引)";
                        ds.Name = GetStr(item, "name");
                        ds.Value = GetStr(item, "value");
                        ds.Formula = GetStr(item, "formula");
                        if (ds.Name == null)
                            ds.ParseError = (ds.ParseError != null ? ds.ParseError + ";" : "") + "缺 name";
                        else if (ds.Value == null && ds.Formula == null)
                            ds.ParseError = (ds.ParseError != null ? ds.ParseError + ";" : "") + "value/formula 至少给一个";
                    }
                    s.Dims.Add(ds);
                    dimIdx++;
                }
            }

            // 边引用(fillet/chamfer 专用):解析失败也逐条记录,由校验器/构建器报出
            if (feat.TryGetProperty("edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in edgesEl.EnumerateArray())
                    s.Edges.Add(ParseEdgeRef(item));
                s.HasEdges = s.Edges.Exists(e => e.ParseError == null);
            }
            else if (feat.TryGetProperty("edges", out _))
            {
                s.Edges.Add(new EdgeRefSpec { ParseError = "edges 必须是数组" });
            }

            // P2(2026-09-23):profiles 数组——loft/sweep/helix 的多轮廓声明。
            // 每项是"迷你特征"对象,复用本 Parse(形状字段语义与顶层完全一致;op/name 对项无意义)。
            // 递归深度受 JSON 嵌套天然限制;项里再写 profiles 属无意义输入,由校验器按"内层无形状"报出。
            if (feat.TryGetProperty("profiles", out var profEl))
            {
                if (profEl.ValueKind == JsonValueKind.Array)
                {
                    int pi = 0;
                    foreach (var item in profEl.EnumerateArray())
                        s.Profiles.Add(Parse(item, pi++));
                    s.HasProfiles = s.Profiles.Count > 0;
                    if (!s.HasProfiles)
                        s.ProfilesError = "profiles 是空数组——loft 至少 2 个截面,sweep 至少 1 条 path + 1 个截面。";
                }
                else
                {
                    s.ProfilesError = "profiles 必须是数组,每项 {\"plane\":...,形状字段,origin?}。";
                }
            }

            // sweep 的首项是路径(path):trace 走【线段+真圆弧混排】,polygon 按【开放链】解释(≥2 点,不闭合);
            // circle/rect/loops/slot 仍按闭合轮廓(闭合路径 = 扫一整圈)。其余 op 的 polygon 恒为闭合。
            if (s.OpLower == "sweep" && s.HasProfiles && s.Profiles.Count > 0)
            {
                var pathSpec = s.Profiles[0];
                bool hasFillet = pathSpec.Raw.ValueKind == JsonValueKind.Object &&
                    pathSpec.Raw.TryGetProperty("fillet", out _);
                if (pathSpec.Raw.ValueKind == JsonValueKind.Object &&
                    pathSpec.Raw.TryGetProperty("trace", out var traceEl))
                {
                    var segs = ParseTraceSegments(traceEl, out string traceErr);
                    if (segs == null)
                        pathSpec.TraceError = traceErr;
                    else if (HasAnyShapeKey(pathSpec.Raw))
                        pathSpec.TraceError = "trace 与 circle/circles/slot/rect/polygon/loops/fillet 互斥,路径只能选一种声明方式";
                    else
                    {
                        pathSpec.Trace = segs;
                        pathSpec.ShapeError = null;
                        pathSpec.Loops.Clear();
                        pathSpec.ShapeSource = "trace(线段/圆弧)";
                    }
                }
                else if (pathSpec.Raw.ValueKind == JsonValueKind.Object &&
                    pathSpec.Raw.TryGetProperty("polygon", out var polyEl) && polyEl.ValueKind == JsonValueKind.Array)
                {
                    double[][] pts = ParsePointArray(polyEl);
                    if (pts != null && pts.Length >= 2)
                    {
                        double fr = 0;
                        if (hasFillet && !TryGetDbl(pathSpec.Raw, "fillet", out fr))
                        {
                            pathSpec.ShapeError = "fillet 必须是数字(圆角半径,米)。";
                        }
                        else if (hasFillet && fr > 0)
                        {
                            // 顶点链 + 圆角:R 放不下/回折/零长段 → ShapeError,由校验层报出(零 COM 往返)
                            var filletSegs = BuildFilletTrace(pts, fr, out string ferr);
                            if (filletSegs == null)
                            {
                                pathSpec.ShapeError = ferr;
                            }
                            else
                            {
                                pathSpec.Trace = filletSegs;
                                pathSpec.ShapeError = null;
                                pathSpec.Loops.Clear();
                                pathSpec.ShapeSource = "polygon+fillet(自动切弧)";
                            }
                        }
                        else
                        {
                            pathSpec.OpenChain = pts;
                            pathSpec.ShapeError = null;
                            pathSpec.Loops.Clear();
                            pathSpec.ShapeSource = "polygon(开放链)";
                        }
                    }
                }
                else if (hasFillet)
                {
                    pathSpec.ShapeError = "fillet 只能配 polygon 路径(顶点链 + 圆角半径,圆心由几何算出)。";
                }
            }

            // 形状:优先 circle(与构建器取形状的先后顺序完全一致)
            // rib 例外:筋板轮廓是【开放链】(2 点即合法),走放宽解析,不走闭合环逻辑。
            if (TryGetCircle(feat, out double cx, out double cy, out double r))
            {
                s.HasCircle = true;
                s.CircleX = cx; s.CircleY = cy; s.CircleR = r;
                s.ShapeSource = "circle";
            }
            else if (TryGetCircles(feat, out var circs))
            {
                s.Circles = circs;
                s.HasCircles = true;
                s.ShapeSource = "circles";
            }
            else if (TryGetSlot(feat, out double sx, out double sy, out double slen, out double swid, out double sang))
            {
                s.HasSlot = true;
                s.SlotX = sx; s.SlotY = sy; s.SlotLength = slen; s.SlotWidth = swid; s.SlotAngle = sang;
                s.ShapeSource = "slot";
            }
            else if (s.OpLower == "hole" && s.Diameter.HasValue && s.Diameter.Value > 0)
            {
                // hole 便捷写法:center + diameter → 合成单圆轮廓(与 circle 等价,后续管线共用)
                s.HasCircle = true;
                s.CircleX = s.Center != null && s.Center.Length >= 2 ? s.Center[0] : 0;
                s.CircleY = s.Center != null && s.Center.Length >= 2 ? s.Center[1] : 0;
                s.CircleR = s.Diameter.Value / 2.0;
                s.ShapeSource = "circle";
            }
            else
            {
                // 构建器在这里会抛异常;解析器改为记录错误,由构建器在用时抛、校验器当 issue 报。
                // rib 也走闭合环(SE 2022 实测:开放链经 Ribs.Add 不出几何,闭合轮廓才出——见对照表 §rib)。
                try
                {
                    s.Loops = ParseLoops(feat);
                    s.ShapeSource = DetectShapeSource(feat);
                }
                catch (ArgumentException ex)
                {
                    s.ShapeError = ex.Message;
                    s.ShapeSource = "";
                }
            }

            if (feat.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in feat.EnumerateObject())
                {
                    if (!KnownFields.Contains(p.Name))
                        s.UnknownFields.Add(p.Name);
                }
            }

            return s;
        }

        /// <summary>闭合轮廓形状字段名(trace 互斥判定用:只要声明过其中之一就不允许再给 trace)。</summary>
        private static readonly string[] ShapeKeys = { "circle", "circles", "slot", "rect", "polygon", "loops", "fillet" };

        /// <summary>feat 上是否声明过任一闭合轮廓形状字段(不看能否解析成功——声明过即算,避免无效多边形漏检)。</summary>
        private static bool HasAnyShapeKey(JsonElement feat)
        {
            if (feat.ValueKind != JsonValueKind.Object) return false;
            foreach (var k in ShapeKeys)
                if (feat.TryGetProperty(k, out _)) return true;
            return false;
        }

        private static string DetectShapeSource(JsonElement feat)
        {
            if (HasNonEmptyArray(feat, "loops")) return "loops";
            if (HasNonEmptyArray(feat, "polygon")) return "polygon";
            if (HasNonEmptyArray(feat, "rect")) return "rect";
            return "";
        }

        private static bool HasNonEmptyArray(JsonElement e, string key)
        {
            return e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;
        }

        // ---------------- 形状解析(与改造前 ModelingTools 的实现逐条对齐) ----------------

        /// <summary>
        /// 解析圆形草图。两种写法:
        ///   "circle": {"center":[x,y],"radius":0.005}
        ///   "circle": [x, y, 0.005]
        /// 半径 &gt; 0 才算有效圆(与改造前一致:否则回退走直线环)。
        /// </summary>
        private static bool TryGetCircle(JsonElement feat, out double cx, out double cy, out double r)
        {
            cx = cy = r = 0;
            if (!feat.TryGetProperty("circle", out var v)) return false;

            if (v.ValueKind == JsonValueKind.Array)
            {
                var arr = new List<double>();
                foreach (var item in v.EnumerateArray())
                    arr.Add(item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0);
                if (arr.Count < 3) return false;
                cx = arr[0]; cy = arr[1]; r = arr[2];
                return r > 0;
            }

            if (v.ValueKind == JsonValueKind.Object)
            {
                TryGetCenter(v, out cx, out cy);
                r = GetDbl(v, "radius", 0);
                return r > 0;
            }

            return false;
        }

        /// <summary>
        /// 解析多真圆(circles)。写法:
        ///   "circles": [[x1,y1,r1],[x2,y2,r2], ...]
        /// 只保留 r&gt;0 的项;至少 1 个有效才算成功。用于一次切/拉多个真圆孔(如法兰螺栓孔阵列)。
        /// </summary>
        private static bool TryGetCircles(JsonElement feat, out List<double[]> circles)
        {
            circles = new List<double[]>();
            if (!feat.TryGetProperty("circles", out var v)) return false;
            if (v.ValueKind != JsonValueKind.Array) return false;

            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var arr = new List<double>();
                foreach (var x in item.EnumerateArray())
                    arr.Add(x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0);
                if (arr.Count < 3) continue;
                if (arr[2] > 0) circles.Add(new[] { arr[0], arr[1], arr[2] });
            }
            return circles.Count > 0;
        }

        /// <summary>
        /// 解析腰孔(长圆孔)草图。两种写法:
        ///   "slot": {"center":[x,y],"length":0.1,"width":0.03,"angle":0}
        ///   "slot": [x, y, 0.1, 0.03]          (简写:中心 + 总长 + 宽,angle 可加第 5 位)
        /// length 是【含两端半圆的总长】,width 是宽度(也是端头半圆的直径)。
        /// 用真圆弧构造,不是折线近似。
        /// </summary>
        private static bool TryGetSlot(JsonElement feat, out double cx, out double cy,
            out double len, out double wid, out double ang)
        {
            cx = cy = len = wid = ang = 0;
            if (!feat.TryGetProperty("slot", out var v)) return false;

            if (v.ValueKind == JsonValueKind.Array)
            {
                var arr = new List<double>();
                foreach (var item in v.EnumerateArray())
                    arr.Add(item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0);
                if (arr.Count < 4) return false;
                cx = arr[0]; cy = arr[1]; len = arr[2]; wid = arr[3];
                if (arr.Count >= 5) ang = arr[4];
                return wid > 0 && len > 0;
            }

            if (v.ValueKind == JsonValueKind.Object)
            {
                TryGetCenter(v, out cx, out cy);
                len = GetDbl(v, "length", 0);
                wid = GetDbl(v, "width", 0);
                ang = GetDbl(v, "angle", 0);
                return wid > 0 && len > 0;
            }

            return false;
        }

        /// <summary>
        /// 解析 circle / slot 的 "center" 字段。接受两种写法:扁平 [x,y](文档语法)、
        /// 嵌套 [[x,y]](按点列习惯写的)。
        /// 历史坑(2026-09-15 由单元测试抓出并修复):center 为 [x,y] 时曾走 GetPoints,
        /// 它要求"每个元素是点数组且至少 2 个点",扁平 [x,y] 被判无效 → 圆心/腰孔中心
        /// 静默变成 (0,0),特征画在原点。
        /// </summary>
        private static bool TryGetCenter(JsonElement obj, out double cx, out double cy)
        {
            cx = cy = 0;
            if (!obj.TryGetProperty("center", out var c) || c.ValueKind != JsonValueKind.Array) return false;

            if (c.GetArrayLength() >= 2 && c[0].ValueKind == JsonValueKind.Number)
            {
                cx = c[0].GetDouble();
                cy = c[1].ValueKind == JsonValueKind.Number ? c[1].GetDouble() : 0;
                return true;
            }
            if (c.GetArrayLength() >= 1 && c[0].ValueKind == JsonValueKind.Array)
            {
                foreach (var pair in c.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
                    var it = pair.EnumerateArray();
                    it.MoveNext();
                    cx = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                    it.MoveNext();
                    cy = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 解析旋转轴(revolve 专用)。两种写法:
        ///   "axis": [[u1,v1],[u2,v2]]
        ///   "axis": [u1, v1, u2, v2]
        /// 两点【不重合】才算有效轴——退化成点的轴会让 AddFinite 失败,所以这里直接判掉。
        /// </summary>
        private static bool TryGetAxis(JsonElement feat, out double[] p1, out double[] p2)
        {
            p1 = p2 = null;
            if (feat.ValueKind != JsonValueKind.Object) return false;
            if (!feat.TryGetProperty("axis", out var v)) return false;
            if (v.ValueKind != JsonValueKind.Array) return false;

            // 写法 1:[[u1,v1],[u2,v2]]
            double[][] pts = ParsePointArray(v);
            if (pts != null && pts.Length >= 2)
            {
                p1 = pts[0];
                p2 = pts[1];
                return !NearlySame(p1, p2);
            }

            // 写法 2:[u1,v1,u2,v2]
            var flat = new List<double>();
            foreach (var item in v.EnumerateArray())
                flat.Add(item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0);
            if (flat.Count < 4) return false;

            p1 = new[] { flat[0], flat[1] };
            p2 = new[] { flat[2], flat[3] };
            return !NearlySame(p1, p2);
        }

        private static bool NearlySame(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length < 2 || b.Length < 2) return false;
            return Math.Abs(a[0] - b[0]) < 1e-12 && Math.Abs(a[1] - b[1]) < 1e-12;
        }

        /// <summary>解析特征草图:优先 loops(多个闭合环),回退 rect / polygon(单个环)。</summary>
        public static List<double[][]> ParseLoops(JsonElement feat)
        {
            var loops = new List<double[][]>();

            if (feat.TryGetProperty("loops", out var loopsEl) && loopsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var loopEl in loopsEl.EnumerateArray())
                {
                    double[][] pts = ParsePointArray(loopEl);
                    if (pts != null && pts.Length >= 3) loops.Add(pts);
                }
                if (loops.Count > 0) return loops;
            }

            double[][] single = ResolveProfilePoints(feat);
            if (single != null) loops.Add(single);
            if (loops.Count == 0)
                throw new ArgumentException("特征缺少 rect(两角点)/polygon(>=3 点)/loops(多环)草图。");
            return loops;
        }

        private static double[][] ResolveProfilePoints(JsonElement feat)
        {
            double[][] pts = GetPoints(feat, "polygon");
            if (pts != null && pts.Length >= 3) return pts;

            pts = GetPoints(feat, "rect");
            if (pts != null && pts.Length >= 2)
            {
                double x1 = pts[0][0], y1 = pts[0][1], x2 = pts[1][0], y2 = pts[1][1];
                double minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2);
                double minY = Math.Min(y1, y2), maxY = Math.Max(y1, y2);
                return new[]
                {
                    new[] { minX, minY }, new[] { maxX, minY },
                    new[] { maxX, maxY }, new[] { minX, maxY }
                };
            }

            throw new ArgumentException("特征缺少 rect(两角点)或 polygon(>=3 点)草图。");
        }

        /// <summary>
        /// 解析 plane 字段的 "face:" 前缀(2026-10-03 IR 面锚定):
        ///   "face:26"  → Kind=Id,   FaceId=26
        ///   "face:+Z"  → Kind=Axis, Axis="+Z"(±X/±Y/±Z,大小写不敏感;正负号必须写)
        /// 非 "face:" 开头返回 null(照旧走 RefPlane_N / @别名 / obj-K);
        /// 格式非法时返回带 ParseError 的对象,由校验层 E203 报出。
        /// </summary>
        public static FacePlaneRef ParseFacePlaneRef(string planeRef)
        {
            if (string.IsNullOrEmpty(planeRef)) return null;

            const string prefix = "face:";
            if (!planeRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            var fr = new FacePlaneRef();
            string body = planeRef.Substring(prefix.Length).Trim();
            if (body.Length == 0)
            {
                fr.ParseError = "face: 后缺少内容(应为 Face.ID 整数或 ±X/±Y/±Z)";
                return fr;
            }

            if (int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fid))
            {
                if (fid < 0) fr.ParseError = "face:<ID> 的 ID 必须 >= 0";
                else { fr.Kind = "Id"; fr.FaceId = fid; }
                return fr;
            }

            string axis = body.ToUpperInvariant();
            if (axis.Length == 2 && (axis[0] == '+' || axis[0] == '-') &&
                (axis[1] == 'X' || axis[1] == 'Y' || axis[1] == 'Z'))
            {
                fr.Kind = "Axis";
                fr.Axis = axis;
                return fr;
            }

            fr.ParseError = "face: 后应为 Face.ID 整数或 ±X/±Y/±Z(收到 \"" + body + "\")";
            return fr;
        }

        /// <summary>
        /// 解析单条边引用。两种写法:
        ///   {"face":"face:71","edge":0}   (推荐,与 se_read_geometry / 对方项目的 0-based 约定一致)
        ///   {"face":71,"edge":0}          (整数简写)
        /// </summary>
        private static EdgeRefSpec ParseEdgeRef(JsonElement item)
        {
            var er = new EdgeRefSpec();
            if (item.ValueKind != JsonValueKind.Object)
            {
                er.ParseError = "必须是对象 {\"face\":\"face:71\",\"edge\":0}";
                return er;
            }

            if (item.TryGetProperty("face", out var fEl))
            {
                if (fEl.ValueKind == JsonValueKind.Number && fEl.TryGetInt32(out int fid))
                    er.FaceId = fid;
                else if (fEl.ValueKind == JsonValueKind.String)
                {
                    string f = fEl.GetString();
                    if (f != null && f.StartsWith("face:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(f.Substring(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fid2))
                        er.FaceId = fid2;
                    else
                        er.ParseError = "face 应为 \"face:<Face.ID>\" 或整数";
                }
                else
                    er.ParseError = "face 应为 \"face:<Face.ID>\" 或整数";
            }
            else
            {
                er.ParseError = (er.ParseError != null ? er.ParseError + ";" : "") + "缺 face";
            }

            if (item.TryGetProperty("edge", out var eEl))
            {
                if (eEl.ValueKind == JsonValueKind.Number && eEl.TryGetInt32(out int ei))
                {
                    if (ei >= 0) er.EdgeIndex = ei;
                    else er.ParseError = (er.ParseError != null ? er.ParseError + ";" : "") + "edge 必须 >= 0(0-based)";
                }
                else
                    er.ParseError = (er.ParseError != null ? er.ParseError + ";" : "") + "edge 必须是整数";
            }
            else
            {
                er.ParseError = (er.ParseError != null ? er.ParseError + ";" : "") + "缺 edge(0-based 面内边索引)";
            }

            return er;
        }

        /// <summary>
        /// 解析面引用(P3):从 features 项里读 faceOf/faceNormal/faceIndex 三字段。
        /// faceOf 缺省返回 null(非面引用 op 不需要);给了就构造 FaceRefSpec 并按规则校验。
        /// faceNormal 必须是 3 元数组(世界系 XYZ);faceIndex 必须非负整数。
        /// </summary>
        private static FaceRefSpec ParseFaceRef(JsonElement feat)
        {
            if (!feat.TryGetProperty("faceOf", out var fofEl)) return null;

            var fr = new FaceRefSpec();

            // faceOf 必须是字符串(@别名 或 obj-K)
            if (fofEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fofEl.GetString()))
            {
                fr.ParseError = "faceOf 必须是非空字符串(@别名 或 obj-K 句柄)";
                return fr;
            }
            fr.FeatureName = fofEl.GetString();

            // faceNormal:3 元数组(世界系 XYZ)
            if (feat.TryGetProperty("faceNormal", out var fnEl))
            {
                if (fnEl.ValueKind != JsonValueKind.Array || fnEl.GetArrayLength() != 3)
                {
                    fr.ParseError = "faceNormal 必须是 3 元数组 [x,y,z](世界系法向)";
                }
                else
                {
                    var arr = new double[3];
                    int i = 0;
                    bool ok = true;
                    foreach (var v in fnEl.EnumerateArray())
                    {
                        if (v.ValueKind != JsonValueKind.Number) { ok = false; break; }
                        arr[i++] = v.GetDouble();
                    }
                    if (!ok)
                        fr.ParseError = "faceNormal 数组元素必须是数字";
                    else
                    {
                        // 零向量无意义
                        double mag = System.Math.Sqrt(arr[0] * arr[0] + arr[1] * arr[1] + arr[2] * arr[2]);
                        if (mag < 1e-9)
                            fr.ParseError = "faceNormal 不能是零向量";
                        else
                            fr.Normal = arr;
                    }
                }
            }

            // faceIndex:非负整数(0-based)
            if (feat.TryGetProperty("faceIndex", out var fiEl))
            {
                if (fiEl.ValueKind != JsonValueKind.Number || !fiEl.TryGetInt32(out int idx))
                    fr.ParseError = (fr.ParseError != null ? fr.ParseError + ";" : "") + "faceIndex 必须是整数";
                else if (idx < 0)
                    fr.ParseError = (fr.ParseError != null ? fr.ParseError + ";" : "") + "faceIndex 必须 >= 0(0-based)";
                else
                    fr.Index = idx;
            }

            return fr;
        }

        /// <summary>解析 [[x,y],...] 点列;不足 2 点返回 null。</summary>
        public static double[][] ParsePointArray(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Array) return null;
            var pts = new List<double[]>();
            foreach (var pt in el.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                var it = pt.EnumerateArray();
                it.MoveNext();
                double x = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                it.MoveNext();
                double y = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                pts.Add(new[] { x, y });
            }
            return pts.Count >= 2 ? pts.ToArray() : null;
        }

        /// <summary>
        /// 解析 trace 段数组(sweep 路径专用):[{"line":[[x,y],[x,y]]}, {"arc":{"center":[x,y],"start":[x,y],"end":[x,y]}}]。
        /// 段数 == 0 或任一段格式错 → 返回 null 并经 out err 给原因(段号 1-based)。
        /// arc 方向恒为【逆时针】从 start 走到 end(与 Arcs2d.AddByCenterStartEnd 语义一致,不支持顺时针)。
        /// </summary>
        public static List<PathSegment> ParseTraceSegments(JsonElement el, out string err)
        {
            err = null;
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() == 0)
            {
                err = "trace 必须是非空数组";
                return null;
            }

            var segs = new List<PathSegment>();
            int i = 0;
            foreach (var item in el.EnumerateArray())
            {
                i++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    err = "第 " + i + " 段不是对象(应为 {\"line\":...} 或 {\"arc\":...})";
                    return null;
                }
                if (item.TryGetProperty("line", out var lineEl))
                {
                    var pts = ParsePointArray(lineEl);
                    if (pts == null || pts.Length != 2)
                    {
                        err = "第 " + i + " 段 line 需要恰好 2 个点 [[x,y],[x,y]]";
                        return null;
                    }
                    segs.Add(new PathSegment { Kind = "line", P0 = pts[0], P1 = pts[1] });
                }
                else if (item.TryGetProperty("arc", out var arcEl))
                {
                    if (arcEl.ValueKind != JsonValueKind.Object)
                    {
                        err = "第 " + i + " 段 arc 必须是对象 {center,start,end}";
                        return null;
                    }
                    double[] c = TryGetPoint2(arcEl, "center");
                    double[] a = TryGetPoint2(arcEl, "start");
                    double[] b = TryGetPoint2(arcEl, "end");
                    if (c == null || a == null || b == null)
                    {
                        err = "第 " + i + " 段 arc 需要 center/start/end 三个 [x,y] 点";
                        return null;
                    }
                    segs.Add(new PathSegment { Kind = "arc", Center = c, P0 = a, P1 = b });
                }
                else
                {
                    err = "第 " + i + " 段既没有 line 也没有 arc";
                    return null;
                }
            }
            return segs;
        }

        /// <summary>
        /// 把【折线顶点链 + 圆角半径】展开成"线段 + 真圆弧"段列(sweep 路径专用,2026-10-04)。
        /// 圆心与切点全部由几何算出 ⇒ 展开出的圆弧与相邻直段【必然相切】。
        /// 要做这条通道的原因:让调用方手写 arc 的 center 极易把圆心放到拐角点本身
        /// (2026-10-04 "弯头不相切"事故的根因),这里把圆心从输入里彻底拿掉。
        /// R 放不下 / 顶点重合 / 180° 回折 → 返回 null 并给 err(由校验层报出,零 COM 往返)。
        /// </summary>
        public static List<PathSegment> BuildFilletTrace(double[][] pts, double r, out string err)
        {
            err = null;
            var segs = new List<PathSegment>();
            double[] cur = pts[0];
            double prevT = 0;                       // 上一拐角在本段(入段)上吃掉的长度——防两个圆角重叠
            for (int i = 1; i < pts.Length - 1; i++)
            {
                double[] p0 = pts[i - 1], p1 = pts[i], p2 = pts[i + 1];
                double ux = p1[0] - p0[0], uy = p1[1] - p0[1];
                double vx = p2[0] - p1[0], vy = p2[1] - p1[1];
                double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                if (lu < 1e-12 || lv < 1e-12)
                {
                    err = "第 " + (i + 1) + " 个顶点与前/后顶点重合(零长段无法倒圆角)。";
                    return null;
                }
                ux /= lu; uy /= lu; vx /= lv; vy /= lv;
                double cross = ux * vy - uy * vx, dot = ux * vx + uy * vy;
                if (Math.Abs(cross) < 1e-12 && dot > 0)          // 共线直行:没有拐角
                {
                    segs.Add(new PathSegment { Kind = "line", P0 = cur, P1 = p1 });
                    cur = p1; prevT = 0;
                    continue;
                }
                if (Math.Abs(cross) < 1e-12)                      // dot<0 → 180° 回折
                {
                    err = "第 " + (i + 1) + " 个顶点处是 180° 回折,无法倒圆角。";
                    return null;
                }
                double delta = Math.Atan2(cross, dot);            // 转向角,右转为负
                double t = r * Math.Tan(Math.Abs(delta) / 2.0);   // 切点到顶点的距离
                if (prevT + t > lu - 1e-12 || t > lv - 1e-12)
                {
                    err = "第 " + (i + 1) + " 个顶点放不下 R=" + r.ToString("G6", CultureInfo.InvariantCulture) +
                          " 的圆角(切点距顶点 " + t.ToString("G6", CultureInfo.InvariantCulture) +
                          ",相邻段长 " + lu.ToString("G6", CultureInfo.InvariantCulture) +
                          "/" + lv.ToString("G6", CultureInfo.InvariantCulture) + ")。";
                    return null;
                }
                double[] a = { p1[0] - ux * t, p1[1] - uy * t };  // 入切点
                double[] b = { p1[0] + vx * t, p1[1] + vy * t };  // 出切点
                double[] n = delta < 0 ? new[] { uy, -ux } : new[] { -uy, ux };   // 右转取右侧法向
                double[] c = { a[0] + n[0] * r, a[1] + n[1] * r };                // 圆心
                segs.Add(new PathSegment { Kind = "line", P0 = cur, P1 = a });
                segs.Add(new PathSegment { Kind = "arc", P0 = a, P1 = b, Center = c });
                cur = b; prevT = t;
            }
            segs.Add(new PathSegment { Kind = "line", P0 = cur, P1 = pts[pts.Length - 1] });
            return segs;
        }

        // ---------------- 取值原语(nullable 版:区分"没给"与"给了 0") ----------------

        private static string GetStr(JsonElement e, string key)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        private static bool TryGetDbl(JsonElement e, string key, out double val)
        {
            val = 0;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) { val = d; return true; }
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2))
            { val = d2; return true; }
            return false;
        }

        /// <summary>
        /// 数组元素取数。⚠️ JsonElement.TryGetDouble 在元素【不是 Number 时会抛 InvalidOperationException】,
        /// 不是返回 false——直接对 dir[i]/expectvolumedelta[i] 调它,会让"格式写错"从"报 W409/E418"变成
        /// "静态校验整个崩掉"。必须先判 ValueKind。语义与 TryGetDbl 一致(容忍数字字符串)。
        /// </summary>
        private static bool TryGetNum(JsonElement e, out double val)
        {
            val = 0;
            if (e.ValueKind == JsonValueKind.Number) return e.TryGetDouble(out val);
            if (e.ValueKind == JsonValueKind.String &&
                double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2))
            { val = d2; return true; }
            return false;
        }

        private static bool TryGetInt(JsonElement e, string key, out int val)
        {
            val = 0;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) { val = i; return true; }
            if (v.ValueKind == JsonValueKind.String &&
                int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i2))
            { val = i2; return true; }
            return false;
        }

        /// <summary>解析顶层 [x,y] 点(hole 的 center);非法/缺元素返回 null。</summary>
        private static double[] TryGetPoint2(JsonElement e, string key)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2) return null;
            var it = v.EnumerateArray();
            it.MoveNext();
            if (it.Current.ValueKind != JsonValueKind.Number) return null;
            double x = it.Current.GetDouble();
            it.MoveNext();
            if (it.Current.ValueKind != JsonValueKind.Number) return null;
            double y = it.Current.GetDouble();
            return new[] { x, y };
        }

        /// <summary>大小写不敏感的布尔读取(Utf8JsonReader.TryGetProperty 默认大小写敏感,驼峰键会漏读)。</summary>
        private static bool TryGetBoolCI(JsonElement e, string key, out bool val)
        {
            val = false;
            if (e.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in e.EnumerateObject())
            {
                if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.True) { val = true; return true; }
                if (p.Value.ValueKind == JsonValueKind.False) { val = false; return true; }
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    if (bool.TryParse(p.Value.GetString(), out val)) return true;
                }
            }
            return false;
        }

        private static bool TryGetBool(JsonElement e, string key, out bool val)
        {
            val = false;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind == JsonValueKind.True) { val = true; return true; }
            if (v.ValueKind == JsonValueKind.False) { val = false; return true; }
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) { val = b; return true; }
            return false;
        }

        private static double GetDbl(JsonElement e, string key, double def)
        {
            return TryGetDbl(e, key, out var d) ? d : def;
        }

        /// <summary>解析 [[x,y],...] 二维点数组;不足 2 点返回 null。</summary>
        private static double[][] GetPoints(JsonElement e, string key)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;

            var pts = new List<double[]>();
            foreach (var pt in v.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                var it = pt.EnumerateArray();
                it.MoveNext();
                double x = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                it.MoveNext();
                double y = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                pts.Add(new[] { x, y });
            }
            return pts.Count >= 2 ? pts.ToArray() : null;
        }
    }
}
