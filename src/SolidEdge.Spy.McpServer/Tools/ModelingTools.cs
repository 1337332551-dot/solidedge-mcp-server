using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 高级声明式建模工具:把"内嵌草图 + 端点闭合约束 + End + 拉伸/除料"整套已验证配方
    /// 封装成服务器端代码,AI 只需给特征描述,不再逐条拼 15~17 步 COM 原语 chain。
    ///
    /// 支持的操作(op):
    ///   plane   创建局部参考面(平行偏移),返回句柄并可命名复用
    ///   extrude 拉伸凸台(第一个特征走 Models.AddFiniteExtrudedProtrusion,后续走
    ///           Model.ExtrudedProtrusions.AddFinite,自动并入同一实体)
    ///   cut     除料挖孔 / 铣槽(Model.ExtrudedCutouts.AddThroughNext/AddFinite 等)
    ///           ★ 方向由两个参数共同决定(2026-09-10 实测,踩过坑):
    ///             profileside = ProfileSide,决定"切除轮廓【内侧还是外侧】的料",默认 1(=官方示例 igLeft);
    ///             side        = ProfilePlaneSide,决定"相对草图平面往哪一侧延伸"。
    ///             两者都只有 1/2 两值;调用方【没显式给】的维度,op 会自动按组合重试到几何正常为止。
    ///   revolve 旋转凸台(截面 + 独立旋转轴 → Model.RevolvedProtrusions.AddFinite;
    ///           轴是草图平面内的一条线,不参与截面闭环;建完自动隐藏草图);
    ///           mode:"cut" 走旋转切割 RevolvedCutouts.AddFinite(在已有实体上切除)
    ///   fillet  圆角(Rounds.Add):edges 引用已有实体边,face 用 Face.ID(唯一稳定索引)
    ///   chamfer 等距倒角(Chamfers.AddEqualSetback):同 fillet 的边引用
    ///   rib     筋板/薄台(Ribs.Add):★闭合轮廓+plane 贴实体表面才出几何(SE 2022 实测,开放链不支持)
    ///   pattern 矩形阵列:SE 2022 COM 不可达(AddByRectangular 全种子 E_FAIL),诚实拒绝并给替代方案
    ///
    /// P3 面引用机制(P2 之后):前序特征带 name 登记为 @别名,供 faceOf 引用其产出面;
    ///   面选择:faceOf(@别名/obj-K,仅做存在性校验) + faceNormal([nx,ny,nz] 法向命中) 或 faceIndex(全量面列表 0-based 序号)。
    ///   draft      拔模(Drafts.Add):faceOf 目标面 + angle(弧度,默认 π/36) + side(只认 4=igInside/5=igOutside);
    ///             refPlane 必须与目标面相交(枢轴语义),否则 6311
    ///   split      分割(Splits.Add):target 须 @别名/obj-K;用平面分割实体
    ///   extrude_surface 曲面拉伸(Constructions.ExtrudedSurfaces.AddFinite):轮廓拉成【曲面】非实体;产物面缓存供 thicken
    ///   thicken    曲面加厚(Thickens.Add):faceOf 指向 extrude_surface;★ SE 2022 该 COM 通道不可达(全组合 E_INVALIDARG),诚实拒绝
    ///   web_network 腹板网(WebNetworks.Add):闭合轮廓+thickness+depth;ExtentType/ProfileExtensionType 用专属枚举
    ///             (seWebExtendFinite+seWebProfileNoExtend,勿混用通用 FeaturePropertyConstants)
    ///   delete_face 删面(DeleteBlends.Add 删 blend/round + DeleteFaces.Add 兜底):需 confirm:true;删圆角面用 faceNormal 命中
    ///
    /// 草图形状:circle 圆({center,radius} 或 [cx,cy,r];圆轮廓拉伸即真圆柱,无需旋转)/
    ///           circles 多真圆([[x,y,r],...],一个轮廓多环,一次切/拉多个真圆孔)/
    ///           rect 两角点矩形 / polygon 显式多边形点列(自动闭合)/ loops 多环。
    /// 所有内嵌轮廓自动加端点重合约束(Profile.Form==2)并设 Visible=False(创建即隐藏,
    /// 无需再跑 Python 隐藏脚本)。
    /// </summary>
    [McpServerToolType]
    public static class ModelingTools
    {
        /// <summary>特征 Status 正常值。不等于它 = 几何没生成或状态异常。</summary>
        private const long StatusOk = 1216476310;

        /// <summary>Status=1216476311:COM 调用"成功"但几何没生成——僵尸特征。</summary>
        private const long StatusZombie = 1216476311;

        /// <summary>
        /// 批量声明式建模:一次 MCP 调用完成整栋房子/整套模型的特征创建。
        /// 每个特征是一个 JSON 对象:
        ///   {"op":"plane","name":"front","base":"RefPlane_3","distance":-0.1}      // 建局部参考面
        ///   {"op":"extrude","name":"wall","plane":"RefPlane_1",
        ///    "rect":[[-0.15,-0.1],[0.15,0.1]],"side":2,"depth":0.2}               // 矩形拉伸
        ///   {"op":"extrude","name":"roof","plane":"RefPlane_3",
        ///    "polygon":[[-0.15,0.2],[0.15,0.2],[0,0.3]],"side":3,"depth":0.2}      // 多边形拉伸
        ///   {"op":"cut","name":"door","plane":"@front",
        ///    "rect":[[-0.03,0],[0.03,0.09]]}                                       // 矩形除料
        ///
        /// 字段说明:
        ///   plane:  "RefPlane_1/2/3"(按名称找默认面) / "@别名"(本批前面 plane 建的面) / "obj-K"(句柄表对象)
        ///           / "face:<Face.ID>" 或 "face:±X/±Y/±Z"(2026-10-03 面锚定:在已有实体面上加 extrude/cut/hole;
        ///             坐标模式由 coords 决定:缺省/"global"=全局世界坐标投影(仅轴向面);
        ///             "local"=该面贴面参考平面的局部 u/v(支持斜面),不投影)
        ///   coords:  可选,"global"(默认)|"local"(仅 plane 为 face: 时有意义,普通参考平面本来就是局部 u/v)
        ///   side:     ProfilePlaneSide(延伸方向)。extrude 默认 2;cut 默认 1
        ///   profileside: ProfileSide(切除轮廓【内/外】侧)。extrude 默认 1;cut 默认 1(=官方示例 igLeft)
        ///   ★ cut 的"切哪一侧" = side + profileside 共同决定;调用方【没显式给】的维度,
        ///     op 会自动按组合重试直到几何正常(只能纠正"切到实体外"的僵尸;合法但镜像的判不出来)
        ///   visible:  可选 bool,false=自动隐藏草图(默认),true=保留显示
        ///
        /// 参数化(可选,只对 rect/polygon/loops 直线环生效;circle/circles/slot 会被忽略并回传 warning):
        ///   autoconstraint: true 时按坐标自动补水平/垂直约束(|dy|<eps→水平,|dx|<eps→垂直)
        ///   fixorigin:      true 时固定首环首线起点(AddKeypointFix),消掉整体平移自由度
        ///   dims:           [{"element":0,"name":"Len1","value":"40 mm"} 或 {"formula":"Rad1 - 5 mm"}]
        ///                   element 是【跨环扁平的 0-based 线索引】(第 0 环的线排最前;轴不占位),
        ///                   name 会进变量表(标注即变量),formula 可引用本批已建变量做关联式。
        ///                   必须在 Profile.End 之前应用(构建器已内置此时序)。
        /// 返回每个特征的名称/Status(1216476310=正常,1216476311=几何未生成)/面数/句柄。
        /// 坐标单位为米。
        /// </summary>
        [McpServerTool, Description("高级声明式建模:一次调用创建多个特征(拉伸/除料/打孔/旋转/放样/扫掠/螺旋/局部参考面/圆角/倒角/筋板)。" +
            "扩 op:fillet 圆角 {op,radius,edges:[{face:'face:ID',edge:0-based}]};" +
            "chamfer 等距倒角 {op,distance,edges};rib 筋板 {op,plane,闭合轮廓,thickness}(轮廓须闭合且 plane 贴实体表面);" +
            "hole 圆孔 {op,plane,circle|circles 或 center+diameter, mode?:through_all(默认贯穿)/finite(盲孔需depth)/next};" +
            "pattern 阵列在 SE 2022 COM 不可达(诚实拒绝,多孔阵列改用一个 cut+circles 或 hole+circles)。" +
            "P2 扩 op(多轮廓,均需先有基体特征,首特征通道未开放):" +
            "loft 放样 {op:'loft', profiles:[{plane,形状},...]≥2 项单闭合轮廓, origin?:[u,v] 截面锚点(缺省按形状推导), mode?:'cut'(默认凸台)};" +
            "sweep 扫掠 {op:'sweep', profiles:[首项=路径,其余=截面]}——路径用 trace 时按【线段+真圆弧混排】解释,如 trace:[{line:[[x,y],[x,y]]},{arc:{center:[x,y],start:[x,y],end:[x,y]}}](arc 取 start→end 的【劣弧】≤180°,方向由几何自动判定,段间端点须重合;要相切弯头就把圆心放在拐角的对角点,如 R20 从 (0.02,0.03) 转到 (0.04,0.05) 时 center=[0.04,0.03]),polygon 加 fillet:R 时按【顶点链自动倒圆角】展开(圆心/切点由几何算出,必然与直段相切——弯头首选,如 polygon:[[0,0],[0,0.05],[0.06,0.05]],fillet:0.02),否则按【开放链】折线解释(≥2 点不闭合),circle/rect/loops 是闭合路径(扫一整圈);" +
            "helix 螺旋 {op:'helix', plane, 单闭合截面, axis(同 revolve 的两点轴), pitch/height/revolutions 三给二(螺距m/高度m/圈数,第三个由SE推导), mode?:'cut'}。" +
            "plane 局部参考面 {op:'plane', name:'别名', base:'RefPlane_1/2/3'或'@别名', distance:偏移米}——base+distance 建平行偏置面(省 distance=与 base 重合),后续特征用 plane:'@别名' 引用;" +
            "features 每项 {op, name?, plane|base, 形状, side, profileside, depth, axis?, angle?|degrees?, visible?}。" +
            "op=revolve 旋转凸台(默认)或旋转切割(mode:\"cut\"走 RevolvedCutout):截面用 rect/polygon/loops,必须给 axis 旋转轴两点(草图平面局部 u/v," +
            "如 \"axis\":[[0,0],[0,0.05]] 沿局部 v 轴);angle 弧度(默认 2π 整圈)或 degrees 度。" +
            "轴是独立构造线、不参与截面闭环;建完自动隐藏草图(COM 建特征不会自动隐藏)。" +
            "★ 每个特征建完【自动校验】Status:不等于正常值即判失败并自动删除该特征回滚," +
            "所以返回结果里 status=ok 就一定是健康的,你无需再检查 1216476310/1216476311 这类状态码;" +
            "任一特征失败即停止后续(避免连环僵尸),按该条 diagnosis 修正后重跑。" +
            "★ 防误建守卫(推荐每次都传):expectDocument=期望的文档名,COM 调用内先核对当前文档 Name,不符立即拒绝且一个特征都不建——" +
            "防患于未然,杜绝建到错误的活动文档里。" +
            "形状六选一:circle 圆(写 {\"center\":[x,y],\"radius\":r} 或简写 [x,y,r];圆轮廓拉伸=真圆柱,面数 3)、" +
            "circles 多真圆(写 [[x,y,r],[x,y,r],...],一个轮廓多环、一次切出多个真圆孔,如法兰螺栓孔阵列)、" +
            "slot 腰孔/长圆孔({\"center\":[x,y],\"length\":总长,\"width\":宽,\"angle\":弧度?} 或简写 [x,y,长,宽];" +
            "【真圆弧】构造,两端是半圆不是折线)、" +
            "rect 两角点矩形(如 [[0,0],[0.1,0.1]])、polygon 多边形点列、loops 多环。" +
            "★ 形状键平铺在特征对象顶层(无 shape 包装——嵌套 shape:{...} 不是本工具的写法,勿串味)。" +
            "plane 支持 RefPlane_1/2/3、@别名(前面 plane op 建的)、obj-K、face:<Face.ID>(如 \"face:26\")或轴向选择器 face:±X/±Y/±Z。" +
            "★ face 锚定(2026-10-03):在已有实体面上加 extrude/cut/hole 时,plane 直接写该面的 Face.ID(或 face:+Z 这类选择器);" +
            "坐标模式由可选字段 coords 决定(2026-10-04):缺省/\"global\"=轮廓按【全局世界坐标】解释——外法向轴 Z→(X,Y)、X→(Y,Z)、Y→(X,Z)," +
            "工具内部建一张贴面的隐藏参考平面并投影到它的局部 u/v(仅支持轴向面,斜面报错指路 coords:\"local\");" +
            "\"local\"=轮廓按【该面贴面参考平面的局部 u/v】直接画,不投影,支持任意平面面(含斜面);" +
            "local 的原点/轴向由 SE 决定(常见为面中心),建议先 se_read_geometry 读该面边界坐标再写轮廓;" +
            "local 模式下 slot 也可用(global 模式 slot 的 angle 语义未定,仍拒绝)。" +
            "face:±Z 按外法向朝向选面(同向有多个面时报错并列出候选 Face.ID,请改用 face:<ID>)。" +
            "★ 参数化(可选,仅 rect/polygon/loops 直线环生效——圆/腰孔轮廓会忽略并回传 warnings):" +
            "autoconstraint=true 按坐标自动补水平/垂直约束;fixorigin=true 固定首环首线起点(消平移自由度);" +
            "dims=[{\"element\":0,\"name\":\"Len1\",\"value\":\"40 mm\"}] 给直线加长度标注并把它变成变量" +
            "(element 为跨环扁平 0-based 线索引,轴不占位;也可用 \"formula\":\"Rad1 - 5 mm\" 建关联式)。" +
            "内部自动完成 画轮廓→端点闭合约束(仅直线需要/圆不需要)→End(0)→可见性隐藏→AddThroughNext/AddFinite 全链路," +
            "cut 方向由 side(ProfilePlaneSide=延伸方向) 与 profileside(ProfileSide=切轮廓内/外侧,默认 1) 共同决定:" +
            "调用方没显式给的维度,op 会自动按组合重试到几何正常;替代手工拼 se_invoke_chain 长链。" +
            "★ 执行前会先跑静态校验(见 se_validate_features):有 error 时直接拒绝执行并返回 issues;" +
            "dryRun=true 则只返回校验报告、不建任何特征。建议先跑一次校验再建。" +
            "★ 显式方向(2026-09-29,extrude/cut/hole 可选):dir=[dx,dy,dz] 世界坐标方向向量——" +
            "给了 dir 且未给 side 时,按平面带符号法向 dot 直接换算 side(RefPlane_1=+Z、RefPlane_2=+X、RefPlane_3=−Y," +
            "plane op 派生面继承基准面),结果回传 warnings 里带 dot 值可核对;dir 换算出的 side 不再被方向自愈改写。" +
            "★ 体积对账(2026-09-29,extrude/cut/hole 可选):expectvolumedelta=声明本特征应造成的模型体积增量(毫米³," +
            "数字或 [下限,上限],extrude 为正、cut/hole 为负),建完即读实际增量核对,越界自动回滚该特征并返回实际 vs 预期——" +
            "这是 mode 被截断(如 through_next 只切到一半)、depth 给错这类\"Status 正常但几何不对\"问题的当轮拦截手段;" +
            "结果 resolved 里附 volumeDeltaMm3 供自行核对。")]
        public static string se_model_build(
            SolidEdgeContext context,
            [Description("特征列表(JSON 数组),每项见工具描述")] JsonElement[] features,
            [Description("起始对象句柄(零件文档),可省略;省略时用当前活动文档")] string objectId = null,
            [Description("期望的文档名(可选守卫):与当前目标文档 Name 不符时立即拒绝、一个特征都不建,防误建到别的文档。强烈建议每次都传")] string expectDocument = null,
            [Description("true=只做静态校验并返回报告,不建任何特征(不启动 COM);默认 false")] bool dryRun = false,
            [Description("true=全部特征建完后对模型执行一次 Recompute(统一重算);默认 false(SE 建特征时已自动重算,一般不需要)")] bool recomputeAfter = false)
        {
            try
            {
                // 灌进 Solid Edge 之前先静态校验:纯函数、毫秒级、不碰 COM。
                // 僵尸特征(调用成功但几何未生成)事后才发现的成本,远高于事前拦下。
                var report = FeatureValidator.Validate(features);

                if (dryRun)
                    return JsonSerializer.Serialize(new { status = "dry-run", validate = report },
                        FeatureValidator.JsonOpts);

                if (report.HasError)
                    return JsonSerializer.Serialize(new
                    {
                        status = "error",
                        message = "静态校验未通过(" + report.errorCount + " 个 error),已拒绝执行," +
                                  "以免生成僵尸特征。请按 issues 修正后重试;只想看报告可用 se_validate_features。",
                        validate = report
                    }, FeatureValidator.JsonOpts);

                return context.Invoke(() =>
                {
                    object doc;
                    if (!string.IsNullOrEmpty(objectId))
                    {
                        var h = context.GetHandle(objectId);
                        if (h == null || h.ComObject == null)
                            return Error("找不到起始对象编号 " + objectId + "。请先调用 se_get_selection,或省略 objectId 使用活动文档。");
                        doc = h.ComObject;
                    }
                    else
                    {
                        var app = context.GetApplication();
                        doc = app.ActiveDocument;
                        if (doc == null)
                            return Error("没有活动文档。请打开一个零件文档,或提供 objectId。");
                    }

                    // 防误建守卫:目标文档名与期望不符时立即拒绝,一个特征都不建。
                    // 污染事故(2026-09-22)的根因就是"以为的活动文档"≠"实际的活动文档"——
                    // 把核对放进写操作的同一个 COM 调用内,才真正闭合 TOCTOU 时间窗口。
                    if (!string.IsNullOrEmpty(expectDocument))
                    {
                        string actualName = TryGetDocName(doc);
                        if (!string.Equals(actualName, expectDocument, StringComparison.OrdinalIgnoreCase))
                            return Error("活动文档守卫:当前文档是 \"" + (actualName ?? "(无法读取)") +
                                         "\",与 expectDocument \"" + expectDocument + "\" 不符,已拒绝建模(未建任何特征、零僵尸)。" +
                                         "请先切换活动文档、或修正 expectDocument 后重试。");
                    }

                    if (features == null || features.Length == 0)
                        return Error("features 不能为空。");

                    // 本批内命名局部参考面:name → RefPlane COM 对象
                    var namedPlanes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    // 2026-09-29:平面带符号法向登记(平面引用名 → 单位法向 [nx,ny,nz],世界坐标)。
                    // 来源与符号约定(2026-09-28 挤出探针实测):ProfilePlaneSide=2 沿"平面自身坐标系的 +w",
                    // 三基准面实测 = RefPlane_1→+Z、RefPlane_2→+X、RefPlane_3→−Y;
                    // plane op 派生面(CreatePlaneOp)继承基准面法向。dir 字段用它 dot 求符号换算 side。
                    var planeNormals = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["RefPlane_1"] = new[] { 0.0, 0.0, 1.0 },
                        ["RefPlane_2"] = new[] { 1.0, 0.0, 0.0 },
                        ["RefPlane_3"] = new[] { 0.0, 0.0, -1.0 }
                    };
                    // P3:本批内命名特征:name → 特征 COM 对象(供 faceOf @别名 引用产出面)
                    var namedFeatures = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    // P3.4:extrude_surface 产物面缓存:name → 曲面体面列表(AddFinite 返回对象与 Constructions.Item 不同 RCW,按名缓存最稳)
                    var surfaceFaces = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
                    // 2026-10-03:face 平面缓存(Face.ID → 贴面隐藏 RefPlane)。同一张面被多个特征引用时只建一张,
                    // 避免模型树堆一串无名 RefPlane。特征失败回滚时,只清掉"本次特征新建"的那些。
                    var facePlanes = new Dictionary<int, object>();
                    // 2026-10-03:face 平面相关的非致命提示(临时对象清理失败 / 孤儿平面删除失败),不阻断建模。
                    var batchWarnings = new List<string>();
                    var results = new List<object>();

                    // 统一走 FeatureSpec 解析:构建器与静态校验器(FeatureValidator)共用同一份取数语义,
                    // 避免"校验说没问题、构建却是另一套解释"。
                    var specs = FeatureSpecParser.ParseAll(features);

                    bool aborted = false;

                    for (int i = 0; i < specs.Count; i++)
                    {
                        var spec = specs[i];
                        string op = spec.Op;
                        string name = spec.Name;

                        object result;
                        // 2026-10-03:face 平面回滚基线——本次特征执行前已缓存的 Face.ID。
                        var faceKeysBefore = new List<int>(facePlanes.Keys);
                        try
                        {
                            switch (spec.OpLower)
                            {
                                case "plane":
                                    result = CreatePlaneOp(context, doc, spec, name, namedPlanes, planeNormals);
                                    break;
                                case "extrude":
                                    result = ExtrudeOp(context, doc, spec, name, namedPlanes, planeNormals, facePlanes);
                                    break;
                                case "cut":
                                    result = CutOp(context, doc, spec, name, namedPlanes, planeNormals, facePlanes);
                                    break;
                                case "hole":
                                    result = HoleOp(context, doc, spec, name, namedPlanes, planeNormals, facePlanes);
                                    break;
                                case "revolve":
                                    result = RevolveOp(context, doc, spec, name, namedPlanes);
                                    break;
                                case "fillet":
                                    result = FilletOp(context, doc, spec, name);
                                    break;
                                case "chamfer":
                                    result = ChamferOp(context, doc, spec, name);
                                    break;
                                case "rib":
                                    result = RibOp(context, doc, spec, name, namedPlanes);
                                    break;
                                case "pattern":
                                    result = PatternOp(context, doc, spec, name, namedPlanes);
                                    break;
                                case "loft":
                                    result = LoftOp(context, doc, spec, name, namedPlanes);
                                    break;
                                case "sweep":
                                    result = SweepOp(context, doc, spec, name, namedPlanes);
                                    break;
                                case "helix":
                                    result = HelixOp(context, doc, spec, name, namedPlanes);
                                    break;
                                // P3 面引用机制 op(namedFeatures 供面引用解析;登记由主循环统一做)
                                case "draft":
                                    result = DraftOp(context, doc, spec, name, namedPlanes, namedFeatures);
                                    break;
                                case "split":
                                    result = SplitOp(context, doc, spec, name, namedPlanes, namedFeatures);
                                    break;
                                case "web_network":
                                    result = WebNetworkOp(context, doc, spec, name, namedPlanes, namedFeatures);
                                    break;
                                case "extrude_surface":
                                    result = ExtrudeSurfaceOp(context, doc, spec, name, namedPlanes, namedFeatures, surfaceFaces);
                                    break;
                                case "thicken":
                                    result = ThickenOp(context, doc, spec, name, namedFeatures, surfaceFaces);
                                    break;
                                case "delete_face":
                                    result = DeleteFaceOp(context, doc, spec, name, namedFeatures);
                                    break;
                                default:
                                    result = new { op = op, name = name, status = "error", message = "未知 op(仅支持 plane/extrude/cut/hole/revolve/fillet/chamfer/rib/pattern/loft/sweep/helix/draft/split/web_network/extrude_surface/thicken/delete_face)。" };
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            result = new { op = op, name = name, status = "error", message = DescribeException(ex) };
                        }

                        results.Add(result);

                        // 2026-10-03:face 平面回滚——特征没建成时,清掉本次为它新建的贴面参考平面
                        // (基线里已有=缓存复用,别的特征还要用,不删)。删除失败只记提示,不阻断。
                        if (!IsOk(result) && facePlanes.Count > 0)
                        {
                            foreach (int fid in new List<int>(facePlanes.Keys))
                            {
                                if (faceKeysBefore.Contains(fid)) continue;
                                string delErr = TryDeleteFacePlane(facePlanes, fid);
                                if (delErr != null) batchWarnings.Add(delErr);
                            }
                        }

                        // P3:成功的特征带 name 时登记到 namedFeatures,供后续 faceOf @别名 引用产出面。
                        // 不改 FeatureResult / 现有 op 签名——通过 result.handle 反查 context 拿 featObj COM 对象。
                        if (IsOk(result) && !string.IsNullOrEmpty(name))
                        {
                            try
                            {
                                var hProp = result.GetType().GetProperty("handle");
                                string hid = hProp?.GetValue(result) as string;
                                if (!string.IsNullOrEmpty(hid))
                                {
                                    var h = context.GetHandle(hid);
                                    if (h != null && h.ComObject != null)
                                        namedFeatures[name] = h.ComObject;
                                }
                            }
                            catch { /* 登记失败不影响主流程,最坏是后续 faceOf 引用报"未找到" */ }
                        }

                        // 只要有一个特征没建成(参数错 / 建出来是僵尸且已回滚)就停:
                        // 后面的特征多半依赖前面的实体,硬着头皮继续只会连环失败、留一树僵尸。
                        if (!IsOk(result))
                        {
                            aborted = true;
                            break;
                        }
                    }

                    // recomputeAfter(2026-09-23 P1):全部特征建完后统一重算。
                    // 有特征失败时不重算——僵尸已回滚,半成品模型重算意义不大,先让调用方修参数。
                    string recomputeNote = null;
                    if (recomputeAfter && !aborted)
                    {
                        try
                        {
                            object models = Get(doc, "Models");
                            if (Count(models) > 0)
                            {
                                Call(Get(models, "Item", 1), "Recompute", new object[0]);
                                recomputeNote = "已对模型执行 Recompute。";
                            }
                            else
                            {
                                recomputeNote = "模型为空,跳过 Recompute。";
                            }
                        }
                        catch (Exception ex)
                        {
                            recomputeNote = "Recompute 失败(特征已建成): " + DescribeException(ex);
                        }
                    }

                    return JsonSerializer.Serialize(new
                    {
                        status = aborted ? "error" : "ok",
                        featureCount = results.Count,
                        aborted = aborted,
                        recomputeAfter = recomputeAfter,
                        recompute = recomputeNote,
                        message = aborted
                            ? "第 " + results.Count + " 个特征未建成(失败特征已自动回滚删除),后续特征已停止执行," +
                              "以免连环失败。请按该条的 diagnosis 修正后重跑。"
                            : null,
                        warnings = batchWarnings.Count > 0 ? batchWarnings : null,
                        results = results
                    });
                });
            }
            catch (Exception ex)
            {
                return Error("se_model_build 失败: " + DescribeException(ex));
            }
        }

        /// <summary>
        /// features JSON 的静态校验:【纯函数,不启动 COM、不碰文档】,SE 没启动也能跑。
        /// 与构建器共用 FeatureSpecParser 的解析结果,所以"校验通过"就等于"构建器会这么解释它"。
        /// 目的:把僵尸特征(调用成功但 Status=1216476311、几何没生成)的发现时点从事后提到事前。
        /// </summary>
        [McpServerTool, Description("声明式建模的静态校验:不建任何特征、不碰 Solid Edge,毫秒级检查 features JSON。" +
            "拦五类问题——①结构:未知 op / 缺必填 / 字段类型错(含 dims 结构)/ NaN / 单位疑似把 mm 当 m;" +
            "②引用:plane 缺失 / RefPlane_N 越界 / @别名未定义或前向引用 / 别名重复;" +
            "③几何:环点数不足 / 相邻点重合 / 自交(报第几条边×第几条边)/ 退化面积 / 绕向不一致;" +
            "④语义:除料前没有拉伸 / extrude 缺 depth / 同一平面多次 cut 未合并(必然僵尸)/ 除料落在毛坯外 / 环重叠;" +
            "⑤声明:autoConstraint/fixOrigin/dims 声明了但形状走不到应用路径(circle/circles/slot/plane 会被静默忽略)/ " +
            "dims.element 越界或声明不完整(该标注与变量必然不建立)。" +
            "返回 status=ok|warning|error 与 issues 列表,每条带 code / level / feature 序号 / field / 中文 message / 结构化 fix。" +
            "有 error 时 se_model_build 会直接拒绝执行。SE 未启动也能调用,适合在动手前先跑一遍。")]
        public static string se_validate_features(
            SolidEdgeContext context,
            [Description("特征列表(JSON 数组),与 se_model_build 的 features 同格式")] JsonElement[] features)
        {
            try
            {
                return FeatureValidator.Validate(features).ToJson();
            }
            catch (Exception ex)
            {
                return Error("se_validate_features 失败: " + DescribeException(ex));
            }
        }

        // ---------------- op 实现 ----------------

        /// <summary>
        /// 2026-09-29:按 dir(世界坐标方向向量)解析 ProfilePlaneSide。
        /// 规则:side = dot(dir, 平面带符号法向) > 0 ? 2 : 1(ProfilePlaneSide=2 沿平面自身 +w,
        /// 三基准面/派生面的带符号法向登记在 planeNormals,来源见主循环注释)。
        /// 优先级:显式 side &gt; dir &gt; op 默认;显式 side 与 dir 同时给且矛盾时报 warning(听 side 的)。
        /// planeNormals 查不到该面(如 obj-K 引用的外部面)时回退 opDefault 并带 warning。
        /// </summary>
        private static int ResolveSideByDir(FeatureSpec spec, Dictionary<string, double[]> planeNormals,
            int opDefault, List<string> warnings, string opLabel)
        {
            if (spec.Side.HasValue)
            {
                if (spec.HasDir)
                    warnings.Add(opLabel + " 同时给了 side=" + spec.Side.Value + " 和 dir,已按显式 side 执行(dir 仅记录)——两者应只给其一。");
                return spec.Side.Value;
            }
            if (!spec.HasDir)
                return opDefault;

            if (spec.DirParseError != null)
            {
                warnings.Add(opLabel + " dir 未生效:" + spec.DirParseError + "。已回退默认 side=" + opDefault + "。");
                return opDefault;
            }

            if (spec.PlaneRef != null && planeNormals.TryGetValue(spec.PlaneRef, out double[] n) && n != null)
            {
                double dot = spec.Dir[0] * n[0] + spec.Dir[1] * n[1] + spec.Dir[2] * n[2];
                if (Math.Abs(dot) < 1e-9)
                {
                    // dir 落在平面内(与法向垂直):"朝平面哪一侧"根本未定义,给 side=1 只是假确定性。
                    // 诚实做法 = 视同 dir 不生效,回退默认并说明。
                    warnings.Add(opLabel + " dir 未生效:dir 与平面 \"" + spec.PlaneRef + "\" 平行(dot=0),方向未定义。已回退默认 side=" + opDefault + "。");
                    return opDefault;
                }
                int side = dot > 0 ? 2 : 1;
                warnings.Add("dir=[" + spec.Dir[0].ToString("0.##") + "," + spec.Dir[1].ToString("0.##") + ","
                    + spec.Dir[2].ToString("0.##") + "] × " + spec.PlaneRef + " 法向=["
                    + n[0].ToString("0.##") + "," + n[1].ToString("0.##") + "," + n[2].ToString("0.##")
                    + "] → dot=" + dot.ToString("0.###") + " → side=" + side + "(显式方向,未经自动重试)。");
                return side;
            }

            warnings.Add(opLabel + " dir 未生效:平面 \"" + spec.PlaneRef + "\" 的带符号法向未知(仅 RefPlane_1/2/3 与本批 plane op 派生面可解析)。已回退默认 side=" + opDefault + "。");
            return opDefault;
        }

        /// <summary>
        /// 2026-09-29:读 Model 实体体积(米³)。走 IDispatch 读 Body.Volume(与 inspect_faces 脚本同通道,实测可靠)。
        /// 无模型时返回 0(首特征前体积为零);读不到返回 null,调用方跳过体积对账。
        /// </summary>
        private static double? TryModelVolume(object doc)
        {
            try
            {
                object models = Get(doc, "Models");
                if (Count(models) == 0) return 0.0;
                object bodyObj = Get(Get(models, "Item", 1), "Body");
                if (bodyObj == null) return null;
                object v = Get(bodyObj, "Volume");
                return Convert.ToDouble(v);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 2026-09-29:体积对账。caller 通过 expectvolumedelta(毫米³,数字或 [下限,上限])声明本特征
        /// 应造成的模型体积增量区间;不符 → 删特征+草图并返回 error 结果(实际 vs 预期)。
        /// 返回 null = 对账通过(或无法校验,带 warning);非 null = 已回滚的 error 结果。
        /// </summary>
        private static object CheckVolumeExpectation(object doc, FeatureSpec spec, object profile, object featObj,
            double? v0, double? v1, string opLabel, string name, object resolved, List<string> warnings)
        {
            double deltaMm3 = (v0.HasValue && v1.HasValue) ? (v1.Value - v0.Value) * 1e9 : double.NaN;
            if (!spec.HasExpectVol)
                return null;
            if (spec.ExpectVolParseError != null)
            {
                // 声明了却解析不出来 = 无法核对设计意图:按 error 处理,且必须【一并回滚】——
                // 只报 error 不回滚会把特征留在模型里,调用方按 error 语义重试就出重复特征。
                // (正常路径下 FeatureRules 的 E418 已在建前拦住,这里是纵深防御。)
                DiscardCutCandidate(profile, featObj);
                return new { op = opLabel, name, status = "error", resolved, message = opLabel + " expectvolumedelta 未生效:" + spec.ExpectVolParseError + ",特征已回滚。" };
            }
            if (double.IsNaN(deltaMm3))
            {
                // 体积读不到,无法对账:诚实放行但明确说明(宁可漏判不误删调用方合法特征)
                if (warnings != null)
                    warnings.Add(opLabel + " 已声明 expectvolumedelta,但读不到模型体积,本条【跳过体积对账】(无法核对,不误删特征)。");
                return null;
            }
            bool ok = deltaMm3 >= spec.ExpectVolMin.Value && deltaMm3 <= spec.ExpectVolMax.Value;
            if (ok)
                return null;

            DiscardCutCandidate(profile, featObj);
            return new
            {
                op = opLabel,
                name,
                status = "error",
                resolved,
                message = opLabel + " 体积对账失败:实际体积增量 " + deltaMm3.ToString("F1") + " mm³ 不在声明区间 ["
                    + spec.ExpectVolMin.Value.ToString("F1") + ", " + spec.ExpectVolMax.Value.ToString("F1")
                    + "] 内,特征已回滚。典型原因:cut 的 mode/方向与预期不符(如 through_next 被截断)、depth 给错。",
                diagnosis = "声明 expectvolumedelta 后构建器会在回滚前用实际体积核对设计意图——本条拦截说明几何结果与声明不符,请核对 mode/side/depth。",
                fix = new { action = "fix_and_retry", check = new[] { "mode", "side/dir", "depth" } }
            };
        }

        private static object CreatePlaneOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, double[]> planeNormals)
        {
            object basePlane = ResolvePlane(context, doc, spec.BaseRef, namedPlanes);
            double distance = spec.Distance;

            object refPlanes = Get(doc, "RefPlanes");
            object plane = Call(refPlanes, "AddParallelByDistance",
                new object[] { basePlane, distance, 2, 0, 1, false, false });

            SetVisible(plane, spec.Visible ?? false);

            string handleId = context.AddHandle(plane, "RefPlane", SafeString(Get(plane, "DisplayName")) ?? "(局部面)");
            if (!string.IsNullOrEmpty(name))
            {
                namedPlanes[name] = plane;
                // 派生面法向继承基准面(AddParallelByDistance 沿基准面法向偏移,法向方向不变);
                // 基准面法向未知时登记 null 占位,后续 dir 查询走"法向未知"回退而不是误用别的面。
                planeNormals[name] = (spec.BaseRef != null && planeNormals.TryGetValue(spec.BaseRef, out double[] bn)) ? bn : null;
            }

            return new
            {
                op = "plane",
                name = name,
                status = "ok",
                displayName = SafeString(Get(plane, "DisplayName")),
                distance = distance,
                handle = handleId,
                hint = "后续特征可用 \"@name\" 引用此面。"
            };
        }

        private static object ExtrudeOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, double[]> planeNormals,
            Dictionary<int, object> facePlanes = null)
        {
            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes, facePlanes, spec.CoordsLocal);
            bool visible = spec.Visible ?? false;

            var specWarnings = new List<string>();
            object profile = CreateProfileForFeature(context, doc, plane, spec, visible, specWarnings);
            int side = ResolveSideByDir(spec, planeNormals, 2, specWarnings, "extrude");   // ProfilePlaneSide
            int profileSide = spec.ProfileSide ?? 1;             // ProfileSide
            double depth = spec.Depth ?? double.NaN;
            if (double.IsNaN(depth))
                return new { op = "extrude", name = name, status = "error", message = "extrude 必须提供 depth(米)。" };

            object models = Get(doc, "Models");
            int modelCount = Count(models);
            double? v0 = TryModelVolume(doc);

            object featObj;
            if (modelCount == 0)
            {
                // 第一个特征:新建实体;AddFiniteExtrudedProtrusion 的返回值就是新 Model
                // (2026-09-13 实测:此处立刻回读 models.Item(1) 可能返回 null → NRE,必须用返回值)
                // 通道必须走 Call(ManualInvoke):裸 InvokeMember(binder 直传数组)对
                // SAFEARRAY(DISPATCH) 的 ProfileArray 必报 TYPEMISMATCH(0x80020005,
                // 2026-09-17 三通道对照实测;两线对该结论一致;extrude_rect_profile
                // 配方走 chain 同通道 ~90 次验证成功)。本方法枚举参数
                // (planeSide) 用 int 传不触发 VT_USERDEFINED 静默 null——那是 Revolve 的
                // RefAxis 才有的问题,所以这里不能照搬 RevolveOp 的 PIA 方案。
                // ⚠️ 两线实测矛盾记录:远端 2026-09-14 曾测 PIA 强类型成功,本地 2026-09-17
                // 同 API PIA 直调实测返回僵尸 6311(更新的测试)。裁决走 Call 通道,
                // PIA 通道留待同环境复验(见 plan.md 待人工确认)。
                object model = Call(models, "AddFiniteExtrudedProtrusion",
                    new object[] { 1, new object[] { profile }, side, depth });
                object extrudes = Get(model, "ExtrudedProtrusions");
                featObj = Get(extrudes, "Item", 1);
            }
            else
            {
                object model = Get(models, "Item", 1);
                object extrudes = Get(model, "ExtrudedProtrusions");
                featObj = Call(extrudes, "AddFinite", new object[] { profile, profileSide, side, depth });
            }

            double? v1 = TryModelVolume(doc);
            double deltaMm3 = (v0.HasValue && v1.HasValue) ? (v1.Value - v0.Value) * 1e9 : double.NaN;
            var volFail = CheckVolumeExpectation(doc, spec, profile, featObj, v0, v1, "extrude", name, null, specWarnings);
            if (volFail != null)
                return volFail;

            return FeatureResult("extrude", name, featObj, context, profile, null, specWarnings,
                new
                {
                    plane = spec.PlaneRef,
                    side = side,
                    profileside = profileSide,
                    depth = depth,
                    dir = spec.HasDir ? spec.Dir : null,
                    volumeDeltaMm3 = deltaMm3.Equals(double.NaN) ? (double?)null : deltaMm3
                });
        }

        // opLabel:返回结果里的 op 名(hole 复用本管线时传 "hole",其余用默认 "cut")。
        private static object CutOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, double[]> planeNormals,
            Dictionary<int, object> facePlanes = null, string opLabel = "cut")
        {
            // 中文称呼:报错文案随入口语义变化(除料 / 打孔)
            string kindCn = opLabel == "hole" ? "打孔" : "除料";

            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes, facePlanes, spec.CoordsLocal);
            bool visible = spec.Visible ?? false;

            // 多孔(门+多个窗)必须画进【同一个轮廓】的多个闭合环,一次 AddThroughNext 全切;
            // 分开多次 AddThroughNext 时,第二个及以后的除料会 6311 僵尸(实测)。
            int profileSide = spec.ProfileSide ?? 1;             // ProfileSide:1=切除轮廓【内侧】的料(官方示例 igLeft)
            var specWarnings = new List<string>();
            // 2026-09-29:dir 给出时按带符号法向换算 planeSide(等效显式 side,不再参与自动翻转重试);
            // 未给 dir 且未给 side 时维持旧默认 1 + 方向自愈。
            int planeSide = ResolveSideByDir(spec, planeNormals, 1, specWarnings, opLabel);
            string mode = spec.Mode ?? "next";

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = opLabel, name = name, status = "error", message = kindCn + "前必须先建实体(extrude)。" };

            object model = Get(models, "Item", 1);
            object cutouts = Get(model, "ExtrudedCutouts");

            // ★ 方向自愈(2026-09-10/09-14 实测): cut "切哪一侧" 由 ProfileSide 和 ProfilePlaneSide 共同决定,
            //   任一个取反都可能出问题。两层判定,对【调用方没显式给】的维度做组合尝试:
            //     ① 僵尸(Status=6311)      = 料切到实体外,几何没生成;
            //     ② 包围盒骤缩(见 IsCutSuspicious) = 几何生成了,但把轮廓【外侧】的料整块切掉,
            //        实体只剩一根轮廓柱 —— 即"挖反"。这类失败 Status 完全正常,只看状态码判不出来。
            //   两层都过 = 定案。两个方向都显式给了 = 完全听调用方的,不再猜。
            //   2026-09-29:dir 换算出的 side 视同显式(ppsFree=false)——方向是算出来的,不该再被自愈改写。
            //   ⚠️ 仍判不了的:两个方向都"切到料且都不缩小包围盒"的合法镜像(如沿轮廓切掉板的一半),
            //      这种情况没有机器可辨的唯一解,只能靠调用方显式给方向或人工目检。
            bool psFree = !spec.ProfileSide.HasValue;
            bool ppsFree = !spec.Side.HasValue && !spec.HasDir;
            int[] psList = psFree ? new[] { profileSide, profileSide == 1 ? 2 : 1 } : new[] { profileSide };
            int[] ppsList = ppsFree ? new[] { planeSide, planeSide == 1 ? 2 : 1 } : new[] { planeSide };

            // 除料前基准:实体包围盒。用于识别"挖反"——把轮廓外侧的料切掉后实体只剩轮廓柱,
            // 包围盒会骤缩(实测口径:60×40 板切 Ø12 贯通孔,正常盒≈板、挖反盒≈柱,体积差一个量级以上)。
            // 判据为什么不用面数:L2 给的 `Body.Faces(FaceType=1).Count` 在圆孔场景不稳定
            // (本构建下"板 + 贯通圆孔"实测 6 面,与纯板相同,面数不增),包围盒则必然变化。
            double[] baseBox = TryModelRangeBox(doc);
            double? v0 = TryModelVolume(doc);

            object bestProfile = null;
            object bestFeat = null;
            bool bestSuspicious = false;
            bool healed = false;
            int bestPs = profileSide;        // 实际生效的 ProfileSide,随候选一起记录(供 resolved 回传)
            int bestPps = planeSide;         // 实际生效的 ProfilePlaneSide

            foreach (int ps in psList)
            {
                foreach (int pps in ppsList)
                {
                    object profile = CreateProfileForFeature(context, doc, plane, spec, visible, specWarnings);
                    object featObj = AddCutout(cutouts, profile, ps, pps, mode, spec);

                    long? st = SafeLong(Get(featObj, "Status"));
                    if (st.HasValue && st.Value == StatusZombie)
                    {
                        // ① 这一组方向切到实体外了(僵尸):删掉特征和它的草图,换下一组重试。
                        DiscardCutCandidate(profile, featObj);
                        continue;
                    }

                    // ② 几何生成了,再看是不是把料切没了(挖反)。
                    bool suspicious = IsCutSuspicious(doc, baseBox);

                    if (bestFeat == null)
                    {
                        bestProfile = profile;
                        bestFeat = featObj;
                        bestSuspicious = suspicious;
                        bestPs = ps;
                        bestPps = pps;
                    }
                    else if (!suspicious && bestSuspicious)
                    {
                        // 当前候选不可疑、已有候选可疑 → 换成这个(前面的挖反候选整个回滚)
                        healed = true;
                        DiscardCutCandidate(bestProfile, bestFeat);
                        bestProfile = profile;
                        bestFeat = featObj;
                        bestSuspicious = false;
                        bestPs = ps;
                        bestPps = pps;
                    }
                    else
                    {
                        // 已有候选更可信(或两个都可疑,先来者优先)→ 丢弃这个
                        DiscardCutCandidate(profile, featObj);
                    }

                    // 定案条件:找到一个不可疑的候选。可疑的才需要继续试其它方向组合。
                    if (!bestSuspicious) break;
                }
                if (bestFeat != null && !bestSuspicious) break;
            }

            // resolved:本次【实际生效】的方向。自愈会替调用方改方向,不回传的话调用方
            // 根本不知道最终用了哪一组——"默认值"也就无从核对。
            // 2026-09-29:附体积对账数据(volumeDeltaMm3 = 实际模型体积增量)。
            double? v1 = TryModelVolume(doc);
            double deltaMm3 = (v0.HasValue && v1.HasValue) ? (v1.Value - v0.Value) * 1e9 : double.NaN;
            object cutResolved = new
            {
                plane = spec.PlaneRef,
                side = bestPps,
                profileside = bestPs,
                mode = mode,
                depth = string.Equals(mode, "finite", StringComparison.OrdinalIgnoreCase) ? (spec.Depth ?? 0.2) : (double?)null,
                dir = spec.HasDir ? spec.Dir : null,
                volumeDeltaMm3 = deltaMm3.Equals(double.NaN) ? (double?)null : deltaMm3
            };

            if (bestFeat == null)
            {
                // ★ 所有方向组合都出僵尸。这里【绝不能】像旧实现那样把已删除的特征交给
                //   FeatureResult:删除后的对象读 Status 会抛异常 → SafeLong 返回 null →
                //   被当成"读不到状态但调用没报错"而判成功,于是返回一个已删对象的句柄 + status=ok。
                return new
                {
                    op = opLabel,
                    name = name,
                    status = "error",
                    resolved = cutResolved,
                    message = kindCn + "在所有方向组合下都未生成几何(Status=" + StatusZombie + " 僵尸),已逐一回滚。" +
                              "常见原因:草图画在默认 RefPlane 而非实体外表面的局部 RefPlane;或草图落在毛坯范围之外。",
                    diagnosis = kindCn + "没切到实体:检查 plane 是否选对、草图是否落在毛坯范围内。",
                    fix = new { action = "fix_and_retry", check = new[] { "plane", "形状位置" } }
                };
            }

            // 2026-09-29:体积对账——caller 声明 expectvolumedelta 时,实际增量越界即回滚报错。
            // ⚠️ 必须排在 bestFeat == null 之后:所有方向组合都出僵尸时模型体积根本没变(delta≈0),
            //    先跑对账会把"几何压根没生成"误报成"体积对账失败",还跳过上面更准的僵尸诊断。
            var volFail = CheckVolumeExpectation(doc, spec, bestProfile, bestFeat, v0, v1, opLabel, name, cutResolved, specWarnings);
            if (volFail != null)
                return volFail;

            if (healed)
                specWarnings.Add("首次尝试把轮廓【外侧】的料整块切掉(实体被切得只剩轮廓柱、包围盒骤缩)," +
                                 "已自动换方向重试并成功。若要固定方向,请在 features 里显式指定 side / profileside。");
            else if (bestSuspicious)
                specWarnings.Add("除料后实体包围盒显著缩小(疑似切反了方向),但已无其它方向组合可试,已按首次成功的结果保留。" +
                                 "请目检确认;必要时显式指定 side / profileside。");

            return FeatureResult(opLabel, name, bestFeat, context, bestProfile, null, specWarnings, cutResolved);
        }

        /// <summary>
        /// 孔(hole):圆孔专用入口,复用除料管线(cut 的方向自愈/僵尸回滚/包围盒判挖反全套照用)。
        /// ★ 设计依据 docs/se_model_build-特征补全计划.md P1:首选复用 cut 体系(圆 profile +
        ///   ExtrudedCutouts.AddThroughAll 等),而【不是】 Holes.Add* —— 对照表实测记录:
        ///   Holes.Add* "记录特征但可能不切材料"(孔表/螺纹需求出现前,不引真 hole 特征)。
        /// 限定:形状必须 circle/circles(解析器已支持 center+diameter 合成单圆),异形孔诚实分流回 cut;
        /// 默认贯穿(through_all → mode="all"),与 cut 的默认 next 不同——孔的心智默认是打穿。
        /// </summary>
        private static object HoleOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, double[]> planeNormals,
            Dictionary<int, object> facePlanes = null)
        {
            if (!spec.HasCircle && !spec.HasCircles)
                return new
                {
                    op = "hole",
                    name = name,
                    status = "error",
                    message = "hole 需要 circle/circles(圆孔轮廓)或 center+diameter(米);非圆异形孔请用 cut。"
                };

            // mode 归一:through_all/through → all(贯穿)。合法值已由静态校验 E409 把关,
            // 这里兜底再收敛一次,防止绕过校验的调用路径把脏值带进 cut 管线。
            string mode = (spec.Mode ?? "all").Trim().ToLowerInvariant();
            if (mode == "through_all" || mode == "through") mode = "all";
            if (mode != "all" && mode != "next" && mode != "finite") mode = "all";
            spec.Mode = mode;

            return CutOp(context, doc, spec, name, namedPlanes, planeNormals, facePlanes, "hole");
        }

        /// <summary>按 mode 调对应的除料 API(切穿所有 / 定深 / 切到下一面),供 cut 首次尝试与方向翻转重试共用。</summary>
        private static object AddCutout(object cutouts, object profile, int profileSide, int planeSide, string mode, FeatureSpec spec)
        {
            switch (mode.ToLowerInvariant())
            {
                case "all":
                    return Call(cutouts, "AddThroughAll", new object[] { profile, profileSide, planeSide });
                case "finite":
                    double depth = spec.Depth ?? 0.2;
                    return Call(cutouts, "AddFinite", new object[] { profile, profileSide, planeSide, depth });
                default:
                    return Call(cutouts, "AddThroughNext", new object[] { profile, profileSide, planeSide });
            }
        }

        /// <summary>
        /// 丢弃一个除料候选:删特征 + 删它所属的草图 ProfileSet。
        /// 顺序必须是"先取 ProfileSet 再删特征"——特征一删,profile 常取不到 Parent(实测踩过,草图会漏删)。
        /// 失败不抛:回滚失败别盖掉真正的原因。
        /// </summary>
        private static void DiscardCutCandidate(object profile, object featObj)
        {
            object profileSet = profile != null ? TryGetProfileSet(profile) : null;
            if (featObj != null) TryDelete(featObj);
            if (profileSet != null) TryDelete(profileSet);
        }

        /// <summary>
        /// 读 Model 实体的包围盒(米)。走裸 IDispatch 读 "RangeBox",与 se_read_geometry 'model'
        /// 是同一条通道(已实测);读不到返回 null,绝不抛。
        ///
        /// 为什么不复用本文件的 TryRangeBox:那个函数优先用 dynamic 绑定,是为"特征对象"调的;
        /// 对 Model 走 IDispatch 更直接,也与验证过的 se_read_geometry 行为保持一致。
        /// </summary>
        private static double[] TryModelRangeBox(object doc)
        {
            try
            {
                object models = Get(doc, "Models");
                if (Count(models) == 0) return null;
                return TryBodyRangeBox(Get(models, "Item", 1));
            }
            catch
            {
            }
            return null;
        }

        /// <summary>包围盒体积(米³)。读不到、或退化(任一边长为 0) → -1。</summary>
        private static double BoxVolume(double[] box)
        {
            if (box == null || box.Length < 6) return -1;
            return Math.Abs(box[3] - box[0]) * Math.Abs(box[4] - box[1]) * Math.Abs(box[5] - box[2]);
        }

        /// <summary>
        /// 判断这次除料是不是"挖反":把轮廓【外侧】的料整块切掉 → 实体只剩一根轮廓柱 → 包围盒骤缩。
        /// 这补上了只看 Status 判据的盲区——挖反的特征 Status 完全正常,旧实现会当成功返回。
        ///
        /// 阈值比 0.5 有实测余量(是数量级差距,不是踩线判定,不敏感):
        ///   60×40×10 板中心切 Ø12 贯通孔 —— 正常:盒≈板(比值≈0.95);挖反:盒≈柱(比值≈0.06)。
        ///
        /// ★ 读不到除料前基准、或读不到当前盒时【一律不判】(返回 false) → 退化为"第一个非僵尸即用"
        ///   的既有行为。宁可漏判也不误判:误判会把调用方合法的"切掉一大块"结果改掉。
        /// </summary>
        private static bool IsCutSuspicious(object doc, double[] baseBox)
        {
            double bv = BoxVolume(baseBox);
            if (bv <= 0) return false;
            double nv = BoxVolume(TryModelRangeBox(doc));
            if (nv < 0) return false;
            return nv < bv * 0.5;
        }

        /// <summary>
        /// 旋转凸台(revolve):截面(rect / polygon / loops)+ 独立旋转轴 → RevolvedProtrusions.AddFinite。
        ///
        /// 旋转轴是【独立于截面的构造线】(交互里的"中心线"角色),不参与截面闭环约束;
        /// 截面本身照常逐线 + 端点重合约束闭合成环。
        ///
        /// ★ 两个实测坑(2026-09-09):
        ///   1) SetAxisOfRevolution 的返回值 RefAxis 必须原样传给 AddFinite。丢了/传错就会
        ///      在 AddFinite 上抛"找不到成员"——看起来像方法名错,其实是参数对象不对。
        ///   2) COM 建特征【不会】像交互那样自动隐藏草图,必须显式 SetVisible(profile,false),
        ///      否则截面和轴线会一直挂在图形区。
        /// </summary>
        private static object RevolveOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);
            bool visible = spec.Visible ?? false;

            // 旋转截面走直线环:circle/slot 是闭合曲线,直接拉伸就是圆柱/腰孔,不必旋转。
            if (spec.HasCircle || spec.HasSlot)
                return new
                {
                    op = "revolve", name = name, status = "error",
                    message = "revolve 截面请用 rect/polygon/loops 点列;circle(圆)/slot(腰孔)直接拉伸即可,无需旋转。"
                };

            if (!spec.HasAxis)
                return new
                {
                    op = "revolve", name = name, status = "error",
                    message = "revolve 必须提供 axis(旋转轴两点),如 \"axis\":[[u1,v1],[u2,v2]]。"
                };

            if (spec.ShapeError != null)
                return new { op = "revolve", name = name, status = "error", message = spec.ShapeError };

            var specWarnings = new List<string>();
            object[] pair = CreateProfileRevolve(context, doc, plane, spec, visible, specWarnings);
            object profile = pair[0];
            object refAxis = pair[1];

            bool isRevolveCut = string.Equals(spec.Mode, "cut", StringComparison.OrdinalIgnoreCase);
            int profileSide = spec.ProfileSide ?? 1;      // ProfileSide
            // ★ 2026-09-22 本机实测(零件3 全矩阵):ProfilePlaneSide 默认值必须按 op 类型分——
            //   凸台 side=1 三基准面(RP1/RP2/RP3)全通,side=2 在 RP2 稳定僵尸 6311(4/4);
            //   除料 side=2 才切到实体(faces=12),side=1 反向切空稳定僵尸(诊断"除料没切到实体")。
            //   显式 spec.Side 仍可覆盖。
            int planeSide = spec.Side ?? (isRevolveCut ? 2 : 1);   // ProfilePlaneSide
            double angle = RevolveAngle(spec);            // 默认 2π(360°)

            object models = Get(doc, "Models");
            bool firstFeature = Count(models) == 0;   // 首特征旋转:走 Models 级 API(尚无 Model 可挂 RevolvedProtrusions)
            // ★ 首特征时 Models 集合为空,Item(1) 会拿到 null(model=null → 下一行 NRE,2026-09-13 实测),
            //   所以 model/revolves 的获取必须整个条件化。
            object model = null;
            object revolves = null;
            // "旋转切割" = RevolvedCutout;其余(含缺省)走旋转凸台 RevolvedProtrusion(isRevolveCut 在上方 side 默认值处判定)
            if (!firstFeature)
            {
                model = Get(models, "Item", 1);
                revolves = isRevolveCut
                    ? Get(model, "RevolvedCutouts")
                    : Get(model, "RevolvedProtrusions");
            }

            // 签名:AddFinite(Profile, RefAxis, ProfileSide, ProfilePlaneSide, AngleofRevolution)
            object featObj;
            if (firstFeature)
            {
                // Models.AddFiniteRevolvedProtrusion 的返回值就是新 Model
                // (2026-09-13 实测:建完立刻回读 models.Item(1) 返回 null → NRE,必须用返回值)。
                // 调用必须走 PIA 强类型:标准 binder 对 SAFEARRAY(DISPATCH) 参数报 TYPEMISMATCH,
                // ManualInvoke 对 VT_USERDEFINED 参数(RefAxis/枚举)会静默返回 null。
                var modelsTyped = (SolidEdgePart.Models)models;
                Array profArr = new object[] { profile };
                var refAxisTyped = (SolidEdgePart.RefAxis)refAxis;
                object m = modelsTyped.AddFiniteRevolvedProtrusion(1, ref profArr, refAxisTyped,
                    (SolidEdgePart.FeaturePropertyConstants)planeSide, angle);
                featObj = Get(Get(m, "RevolvedProtrusions"), "Item", 1);
            }
            else
            {
                // ★ 2026-09-18 修复:后续旋转特征原走 late binding(Call),对 VT_USERDEFINED(RefAxis)
                //   静默 null / SAFEARRAY(DISPATCH) 报 TYPEMISMATCH——与首特征分支同款坑。
                //   改为 PIA 强类型;集合级签名 AddFinite(Profile, RefAxis, ProfileSide,
                //   ProfilePlaneSide, Angle)(编译器实证,与 Models 级 count+数组 风格不同)。
                var profileTyped = (SolidEdgePart.Profile)profile;
                var refAxisTyped = (SolidEdgePart.RefAxis)refAxis;
                var sideProfile = (SolidEdgePart.FeaturePropertyConstants)profileSide;
                var sidePlane = (SolidEdgePart.FeaturePropertyConstants)planeSide;
                if (isRevolveCut)
                {
                    var cutoutsTyped = (SolidEdgePart.RevolvedCutouts)revolves;
                    featObj = cutoutsTyped.AddFinite(profileTyped, refAxisTyped, sideProfile, sidePlane, angle);
                }
                else
                {
                    var revsTyped = (SolidEdgePart.RevolvedProtrusions)revolves;
                    featObj = revsTyped.AddFinite(profileTyped, refAxisTyped, sideProfile, sidePlane, angle);
                }
            }

            string kindOverride = isRevolveCut ? "RevolvedCutout" : "RevolvedProtrusion";
            return FeatureResult("revolve", name, featObj, context, profile, kindOverride, specWarnings,
                new { plane = spec.PlaneRef, side = planeSide, profileside = profileSide, angle = angle, mode = isRevolveCut ? "cut" : "protrusion" });
        }

        /// <summary>旋转角:degrees(度)优先并换算成弧度,其次 angle(弧度),都没有则 2π(整圈)。</summary>
        private static double RevolveAngle(FeatureSpec spec)
        {
            if (spec.Degrees.HasValue) return spec.Degrees.Value * Math.PI / 180.0;
            if (spec.Angle.HasValue) return spec.Angle.Value;
            return 2.0 * Math.PI;
        }

        // ---------------- 扩 op(2026-09-22):fillet / chamfer / rib / pattern ----------------
        // 依据:docs/对比分析/SolidEdge-MCP-验证API对照表.md(对方 SE2026 真机验证 + SE2022 官方文档核实)
        // 边引用:face:<Face.ID>(唯一稳定索引,L2 modeling-recipes §七)+ 面内 0-based 边索引。
        // 校验双保险:Status 之外,再加 Rounds/Chamfers/Patterns 集合 Count 增长判据
        // (移植对方 verifies_collection_growth——圆角/倒角不改面数,只看 Status 有盲区)。

        /// <summary>圆角:Model.Rounds.Add(n, edgeArray, radiusArray),每条边独立 set。</summary>
        private static object FilletOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name)
        {
            if (!spec.Radius.HasValue || spec.Radius.Value <= 0)
                return new { op = "fillet", name = name, status = "error",
                    message = "fillet 必须提供 radius(米,且 > 0)。" };

            object edgeErr = null;
            var edgeObjs = ResolveEdgeRefs(doc, spec, out edgeErr);
            if (edgeObjs == null)
                return new { op = "fillet", name = name, status = "error", message = edgeErr };

            object models = Get(doc, "Models");
            object rounds = Get(Get(models, "Item", 1), "Rounds");
            int before = Count(rounds);

            Array edgeArr = edgeObjs.ToArray();
            var radiusArr = new double[edgeObjs.Count];
            for (int i = 0; i < radiusArr.Length; i++) radiusArr[i] = spec.Radius.Value;

            // PIA 强类型调用(官方示例 SolidEdgePart~Rounds~Add.html 的 C# 标签页同款写法)
            object feat = ((SolidEdgePart.Rounds)rounds).Add(edgeArr.Length, edgeArr, radiusArr);

            return FinishEdgeFeature("fillet", name, context, feat, rounds, before,
                new { radius = spec.Radius.Value, edges = spec.Edges });
        }

        /// <summary>倒角(首版只做等距):Model.Chamfers.AddEqualSetback(n, edgeArray, distance)。</summary>
        private static object ChamferOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name)
        {
            if (!spec.HasDistance || spec.Distance <= 0)
                return new { op = "chamfer", name = name, status = "error",
                    message = "chamfer 必须提供 distance(米,且 > 0)。非 45° 角度/不等距版本暂未开放。" };

            object edgeErr = null;
            var edgeObjs = ResolveEdgeRefs(doc, spec, out edgeErr);
            if (edgeObjs == null)
                return new { op = "chamfer", name = name, status = "error", message = edgeErr };

            object models = Get(doc, "Models");
            object chamfers = Get(Get(models, "Item", 1), "Chamfers");
            int before = Count(chamfers);

            Array edgeArr = edgeObjs.ToArray();

            // PIA 强类型;距离单位米。注意:若后续开放角度版,Angle 的单位是【度】(L2 §五实测),别套弧度。
            object feat = ((SolidEdgePart.Chamfers)chamfers).AddEqualSetback(edgeArr.Length, edgeArr, spec.Distance);

            return FinishEdgeFeature("chamfer", name, context, feat, chamfers, before,
                new { distance = spec.Distance, edges = spec.Edges });
        }

        /// <summary>
        /// 筋板:Model.Ribs.Add(profile, igExtend, igThkNormalToProfilePlane, MaterialSide, igSymmetric, thickness)。
        /// ★ SE 2022 实测(2026-09-22 沙盒,36 组合):【闭合轮廓】才能出几何(画在体表面所在的
        ///   平面上,如板底面 RefPlane_1,MaterialSide=igRight 朝材料侧);经典 UI 的【开放链】
        ///   画法经此 COM 通道一律 6311 僵尸(30+ 组合含 Recompute 后复读全灭)。
        ///   效果 = 以轮廓为界长出的薄台(厚度方向 = 轮廓面法向,igSymmetric 对称)。
        /// MaterialSide 沿用 cut 的方向自愈思路:没显式给时僵尸就换侧重试。
        /// </summary>
        private static object RibOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            if (!spec.Thickness.HasValue || spec.Thickness.Value <= 0)
                return new { op = "rib", name = name, status = "error",
                    message = "rib 必须提供 thickness(米,且 > 0)。" };
            if (spec.HasCircle || spec.HasCircles || spec.HasSlot || spec.Loops.Count > 0)
            {
                // 形状可用,继续
            }
            else
            {
                return new { op = "rib", name = name, status = "error",
                    message = string.IsNullOrEmpty(spec.ShapeError)
                        ? "rib 需要【闭合】轮廓(rect/polygon/loops/circle)。SE 2022 的 Ribs.Add 通道不支持开放链。"
                        : spec.ShapeError };
            }

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "rib", name = name, status = "error", message = "rib 前必须先有实体(extrude)——筋板要长在已有几何上。" };

            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);
            bool visible = spec.Visible ?? false;

            var specWarnings = new List<string>();
            object ribs = Get(Get(models, "Item", 1), "Ribs");

            // 方向自愈:MaterialSide(igRight=2 默认)没显式给时,僵尸就换 igLeft=1 重试
            int ms = spec.Side ?? 2;
            bool msFree = !spec.Side.HasValue;
            object feat = null;
            object profile = null;
            int usedMs = ms;
            int attempts = msFree ? 2 : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                int candidate = attempt == 0 ? ms : (ms == 2 ? 1 : 2);
                var specWarningsTry = new List<string>();
                profile = CreateProfileForFeature(context, doc, plane, spec, visible, specWarningsTry);
                feat = ((SolidEdgePart.Ribs)ribs).Add((SolidEdgePart.Profile)profile,
                    SolidEdgePart.FeaturePropertyConstants.igExtend,
                    SolidEdgePart.FeaturePropertyConstants.igThkNormalToProfilePlane,
                    (SolidEdgePart.FeaturePropertyConstants)candidate,
                    SolidEdgePart.FeaturePropertyConstants.igSymmetric,
                    spec.Thickness.Value);
                long? st = SafeLong(Get(feat, "Status"));
                if (st.HasValue && st.Value != StatusZombie) { usedMs = candidate; break; }
                if (attempt < attempts - 1)
                {
                    // 换侧重试:丢弃当前候选(特征+草图),下一轮重建轮廓
                    DiscardCutCandidate(profile, feat);
                    feat = null;
                }
            }

            var resolved = new { plane = spec.PlaneRef, thickness = spec.Thickness.Value, materialSide = usedMs };
            if (feat == null)
            {
                return new
                {
                    op = "rib",
                    name = name,
                    status = "error",
                    resolved = resolved,
                    message = "rib 在两个材料侧都未生成几何(6311 僵尸),已逐一回滚。" +
                              "SE 2022 实测:轮廓必须是【闭合】的,且应画在【实体表面所在/贴合】的平面上" +
                              "(如板底面用 RefPlane_1),材料侧要有料。",
                    diagnosis = "检查轮廓是否闭合、plane 是否贴合实体表面、材料侧(igRight)是否朝向实体。",
                    fix = new { action = "fix_and_retry", check = new[] { "plane", "轮廓闭合", "side" } }
                };
            }

            return FeatureResult("rib", name, feat, context, profile, "Rib", specWarnings, resolved);
        }

        /// <summary>
        /// 矩形阵列:SE 2022 的 Patterns.AddByRectangular 经实测【系统性 E_FAIL 不可达】
        /// (2026-09-22 沙盒,除料/圆角/倒角/筋板 4 类种子 × 2 个 PatternMethod × 2 个 ReferenceIndex
        ///  共 16+ 组合,强类型 PIA 通道全灭;晚绑定通道则报 TYPEMISMATCH——对方项目也从未
        /// 验证过非 Ex 版,其 2026 的 OK 证据来自 AddByRectangularEx,而 SE 2022 没有 Ex 版)。
        /// 所以本 op 不碰 COM,直接诚实拒绝(照对方装配级 pattern 的 unsupported 模板)。
        /// 替代方案:多孔阵列用【一个 cut 的 circles 多真圆】一次切完;多凸台重复给 extrude 特征。
        /// </summary>
        private static object PatternOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            return new
            {
                op = "pattern",
                name = name,
                status = "error",
                unsupported = true,
                message = "pattern 在 SE 2022 的 COM 通道不可用:Patterns.AddByRectangular 对所有种子特征" +
                          "(除料/凸台/圆角/倒角/筋板)一律 E_FAIL(16+ 组合实测,2026-09-22 沙盒)," +
                          "而 2025/2026 可用的 AddByRectangularEx 在 SE 2022 不存在。本 op 已诚实拒绝,不碰 COM。",
                alternatives = new[]
                {
                    "多孔阵列:一个 cut + circles 多真圆([[x,y,r],...])一次切完(如法兰螺栓孔)",
                    "多凸台:在 features 里逐个给出 extrude 特征(坐标错开)",
                    "圆形阵列:同理用 circles 按极坐标给圆心"
                },
                fix = new { action = "use_alternative", see = "alternatives" }
            };
        }

        /// <summary>
        /// fillet/chamfer/pattern 的统一收尾:Status 校验(FeatureResult)之外,再验证
        /// 对应特征集合 Count 确实增长——"Status OK 但集合没长大"= 没作用到任何几何,判失败回滚。
        /// </summary>
        private static object FinishEdgeFeature(string op, string name, SolidEdgeContext context, object feat,
            object collection, int countBefore, object resolved)
        {
            int countAfter = Count(collection);
            if (countAfter <= countBefore)
            {
                TryDelete(feat);
                return new
                {
                    op = op,
                    name = name,
                    status = "error",
                    feature = SafeString(Get(feat, "Name")),
                    rolledBack = true,
                    resolved = resolved,
                    message = op + " 调用返回了特征,但 " + (op == "pattern" ? "Patterns" : op == "fillet" ? "Rounds" : "Chamfers") +
                              " 集合数量未增长(before=" + countBefore + ", after=" + countAfter + ")——没有作用到任何几何,已回滚。" +
                              "常见原因:引用的边不合适(相切中性边不能倒圆)或参数超界。",
                    diagnosis = op == "pattern"
                        ? "阵列特征未生成:检查被阵列特征是否可阵列、plane 与间距是否合理。"
                        : "圆角/倒角未生成:检查边引用是否选对(face:ID 是否还在——几何变更后 Face.ID 可能变化)、半径/距离是否超过相邻面大小。",
                    fix = new { action = "fix_and_retry", check = new[] { "edges", op == "fillet" ? "radius" : op == "chamfer" ? "distance" : "of/plane/spacing" } }
                };
            }
            return FeatureResult(op, name, feat, context, null,
                op == "fillet" ? "Round" : op == "chamfer" ? "Chamfer" : "Pattern", null, resolved);
        }

        /// <summary>
        /// 解析 edges 声明为 COM 边对象列表。任一条失败即整体失败(返回 null + error 消息)——
        /// 部分成功会让用户搞不清哪条边生效了。错误消息带可用面 ID 列表,学对方的"诚实报错"。
        /// </summary>
        private static List<object> ResolveEdgeRefs(object doc, FeatureSpec spec, out object error)
        {
            error = null;
            if (!spec.HasEdges || spec.Edges.Count == 0)
            {
                error = "缺少 edges(边引用数组,每项 {\"face\":\"face:<Face.ID>\",\"edge\":0-based})。";
                return null;
            }

            object models = Get(doc, "Models");
            if (Count(models) == 0) { error = "没有模型实体——圆角/倒角要作用在已有实体上(先 extrude)。"; return null; }
            object model = Get(models, "Item", 1);

            var result = new List<object>();
            var badRefs = new List<string>();
            int faceCountTotal = -1;
            var availableIds = new List<int>();

            foreach (var er in spec.Edges)
            {
                if (er.ParseError != null)
                {
                    badRefs.Add("edge 引用解析失败:" + er.ParseError);
                    continue;
                }

                object face = FindFaceById(model, er.FaceId, out faceCountTotal, availableIds);
                if (face == null)
                {
                    badRefs.Add("未找到 Face.ID=" + er.FaceId + " 的面(共 " + faceCountTotal + " 个面)。");
                    continue;
                }

                object edges;
                try { edges = Get(face, "Edges"); }
                catch { edges = ((SolidEdgeGeometry.Face)face).Edges; }
                int edgeCount = Count(edges);
                if (er.EdgeIndex >= edgeCount)
                {
                    badRefs.Add("Face.ID=" + er.FaceId + " 只有 " + edgeCount + " 条边,edge=" + er.EdgeIndex + " 越界(0-based)。");
                    continue;
                }

                result.Add(Get(edges, "Item", er.EdgeIndex + 1));
            }

            if (badRefs.Count > 0)
            {
                error = string.Join(";", badRefs) +
                        " 提示:face 用 se_read_geometry 或 se_describe_object 查 Face.ID;几何变更后 Face.ID 一般不变,但被完全覆盖的面会换新 ID。";
                return null;
            }
            return result;
        }

        /// <summary>在 Model.Body 的所有 Shell 里按 Face.ID 找面(面锚定 face:&lt;ID&gt; 的判据)。</summary>
        private static object FindFaceById(object model, int faceId, out int faceCountTotal, List<int> availableIds)
        {
            faceCountTotal = 0;
            object body = Get(model, "Body");
            object shells = Get(body, "Shells");
            int shellCount = Count(shells);
            for (int s = 1; s <= shellCount; s++)
            {
                object faces = Get(Get(shells, "Item", s), "Faces");
                int n = Count(faces);
                faceCountTotal += n;
                for (int i = 1; i <= n; i++)
                {
                    object f = Get(faces, "Item", i);
                    int id = SafeInt(Get(f, "ID"));
                    if (availableIds != null && availableIds.Count < 20) availableIds.Add(id);
                    if (id == faceId) return f;
                }
            }
            return null;
        }

        // ==================== P3 面引用机制运行时(2026-09-23) ====================

        /// <summary>
        /// 解析面引用:从 namedFeatures 查 @别名 对应的特征 COM 对象(存在性确认),
        /// 然后从 Model.Body.Shells.Faces 按 faceNormal 或 faceIndex 选择目标面。
        ///
        /// 设计简化:faceOf @别名 仅做"特征存在"校验(静态校验已判未定义/前向引用);
        /// 实际面选择走 faceNormal/faceIndex 从当前 Model 全量扫描——因为 SE 特征对象
        /// 没有直接的"产出面集合"属性(thicken 需走 Constructions.Body.Faces,各类型路径不同,
        /// 统一走 Model.Body 全量 + 法向/序号过滤更稳)。
        ///
        /// 返回:目标 Face COM 对象;失败抛 ArgumentException(由调用方 catch 包装成 error result)。
        /// </summary>
        private static object ResolveFaceRef(SolidEdgeContext context, object doc, FaceRefSpec faceRef,
            Dictionary<string, object> namedFeatures, out List<string> warnings, bool blendMode = false)
        {
            warnings = new List<string>();
            if (faceRef == null || !string.IsNullOrEmpty(faceRef.ParseError))
                throw new ArgumentException("面引用解析失败:" + (faceRef?.ParseError ?? "faceOf 未声明"));

            string fof = faceRef.FeatureName;
            if (string.IsNullOrWhiteSpace(fof))
                throw new ArgumentException("faceOf 必须是非空字符串(@别名 或 obj-K 句柄)");

            // 1) 确认特征存在(静态校验已判 @别名;这里只做运行时反查)
            object featObj = null;
            if (fof.StartsWith("@", StringComparison.Ordinal))
            {
                string key = fof.Substring(1);
                if (!namedFeatures.TryGetValue(key, out featObj))
                    throw new ArgumentException("未找到本批内命名特征 \"@" + key + "\"(需先给前面的特征带 name 创建)");
            }
            else if (fof.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                var h = context.GetHandle(fof);
                if (h == null || h.ComObject == null)
                    throw new ArgumentException("句柄表里找不到特征对象 " + fof);
                featObj = h.ComObject;
            }
            else
                throw new ArgumentException("faceOf 必须以 @ 或 obj- 开头(当前 \"" + fof + "\")");

            // 2) 从 Model.Body.Shells.Faces 全量收集平面面(igPlane)
            object models = Get(doc, "Models");
            if (Count(models) == 0)
                throw new ArgumentException("没有模型实体——面引用类 op 需先有基体特征");
            object model = Get(models, "Item", 1);
            var planeFaces = FindPlaneFaces(model);

            if (planeFaces.Count == 0)
                throw new ArgumentException("当前模型没有面——面引用类 op 需先有基体特征产出面");

            // 3) 按 faceNormal 或 faceIndex 选择
            object targetFace = null;
            if (faceRef.Normal != null)
            {
                // 法向选择:点积 > 1-ε(同向)
                double nx = faceRef.Normal[0], ny = faceRef.Normal[1], nz = faceRef.Normal[2];
                double mag = System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= mag; ny /= mag; nz /= mag;   // 归一化

                var hits = new List<object>();
                foreach (var f in planeFaces)
                {
                    double[] nrm = TryGetFaceNormal(f);
                    if (nrm == null) continue;
                    double dot = nrm[0] * nx + nrm[1] * ny + nrm[2] * nz;
                    if (dot > 1 - 1e-6) hits.Add(f);
                }

                if (hits.Count == 0)
                {
                    var ids = new List<int>();
                    foreach (var f in planeFaces)
                    {
                        int id = SafeInt(Get(f, "ID"));
                        if (id > 0) ids.Add(id);
                    }
                    throw new ArgumentException("没有平面面的法向与 [" + faceRef.Normal[0] + "," + faceRef.Normal[1] + "," + faceRef.Normal[2] +
                        "] 同向。可用平面面 Face.ID:" + string.Join(",", ids));
                }

                if (hits.Count > 1)
                    warnings.Add("法向匹配到 " + hits.Count + " 个平面面,取第一个(可用 faceIndex 精确指定)。");

                targetFace = hits[0];

                // 同时给 faceIndex 则从 hits 里按序号取
                if (faceRef.Index.HasValue)
                {
                    if (faceRef.Index.Value >= hits.Count)
                        throw new ArgumentException("faceIndex=" + faceRef.Index.Value + " 越界(法向过滤后仅 " + hits.Count + " 个面)");
                    targetFace = hits[faceRef.Index.Value];
                }
            }
            else if (faceRef.Index.HasValue)
            {
                // 纯序号选择:从平面面列表里 0-based 取
                if (faceRef.Index.Value >= planeFaces.Count)
                    throw new ArgumentException("faceIndex=" + faceRef.Index.Value + " 越界(共 " + planeFaces.Count + " 个平面面)");
                targetFace = planeFaces[faceRef.Index.Value];
            }
            else
            {
                if (blendMode)
                {
                    // 2026-09-23 真机:delete_face 语义是删 blend/round 面——PartFeature.Faces COM 侧读不出
                    // (TargetParameterCountException),无法按特征产出面定位。改为:默认取第一个非平面面
                    // (TryGetFaceNormal=null 即 blend/圆柱面,DeleteBlends 通道目标)。draft/thicken 不受影响。
                    foreach (var f in planeFaces)
                    {
                        if (TryGetFaceNormal(f) == null) { targetFace = f; break; }
                    }
                    if (targetFace == null)
                        throw new ArgumentException("模型里没有非平面面(圆角/圆柱面)可删——请确认已建 fillet,或用 faceNormal/faceIndex 显式指定。");
                    warnings.Add("未给 faceNormal/faceIndex,默认取第一个非平面面(Face.ID=" + SafeInt(Get(targetFace, "ID")) + ",blend 删除候选)。");
                }
                else
                {
                    // 都没给:取第一个平面面(draft 默认场景)
                    targetFace = planeFaces[0];
                    warnings.Add("未给 faceNormal 或 faceIndex,取第一个平面面(Face.ID=" + SafeInt(Get(targetFace, "ID")) + ")。");
                }
            }

            return targetFace;
        }

        /// <summary>
        /// 收集 Model.Body 所有 Shell 里的面(用于面引用类 op 选面)。
        /// ★ 不按 GeometryForm 过滤:SE 2022 强类型 interop 下 Face.GeometryForm 返回 9(非对照表所称的
        /// GNTTypePropertyConstants.igPlane=-1909484335——那是 SE 2026 pywin32 晚绑定的结论,通道不同)。
        /// 改为收集全部面,平面面的判定与选择交给 TryGetFaceNormal(边叉积):平面面给出稳定法向,
        /// 非平面面(圆柱/圆锥)的边是曲线,GetEndPoints 给弦方向,法向匹配时自然落选。
        /// </summary>
        private static List<object> FindPlaneFaces(object model)
        {
            var result = new List<object>();
            object body = Get(model, "Body");
            object shells = Get(body, "Shells");
            int shellCount = Count(shells);
            for (int s = 1; s <= shellCount; s++)
            {
                object faces = Get(Get(shells, "Item", s), "Faces");
                int n = Count(faces);
                for (int i = 1; i <= n; i++)
                {
                    result.Add(Get(faces, "Item", i));
                }
            }
            return result;
        }

        /// <summary>
        /// 纯平面性判定(2026-10-04):Geometry 强类型 QI 为 Plane 即平面面。
        /// 与 TryGetFaceNormal 的区别:不看边(带内孔/全曲线边的平面面也算平面),
        /// 不给方向(coords:"local" 只需要"能不能贴一张平面",不需要法向)。
        /// </summary>
        private static bool IsPlanarFace(object face)
        {
            try { return ((SolidEdgeGeometry.Face)face).Geometry is SolidEdgeGeometry.Plane; }
            catch { return false; }
        }

        /// <summary>
        /// 读平面面的法向(世界系 XYZ)。Vertex 没有直接 x/y/z 属性(SE SDK 实测),
        /// 改走强类型 Edge.GetEndPoints(out StartPoint, out EndPoint)——返回两个 Double[3],
        /// 取前两条不共线边的方向向量叉积得平面法向。失败返回 null(调用方跳过该面)。
        /// </summary>
        private static double[] TryGetFaceNormal(object face)
        {
            try
            {
                var f = (SolidEdgeGeometry.Face)face;
                // 2026-09-23 真机:圆柱/blend 面的圆弧边 GetEndPoints 给弦向量,两条弦不共线照样叉积出
                // "假法向"——blend 面被误判平面。先强类型 QI:Geometry 不是 Plane 即非平面面,直接 null。
                if (!(f.Geometry is SolidEdgeGeometry.Plane)) return null;
                var edges = (SolidEdgeGeometry.Edges)f.Edges;
                int ec = edges.Count;
                if (ec < 2) return null;

                // 2026-10-04:dir1 不能只取 Item(1)——带内孔的面(如打了凸台/孔的平面)的边序里
                // 整圆边可能排第一,退化边返回 null 会把整块判定拖死。跳过退化边找第一条有效方向。
                double[] dir1 = null;
                for (int i = 1; i <= ec; i++)
                {
                    var d = EdgeDirection(edges.Item(i));
                    if (d != null) { dir1 = d; break; }
                }
                if (dir1 == null) return null;

                // 找第二条不与 dir1 共线的边方向(共线叉积为 0,推不出法向)
                double[] dir2 = null;
                for (int i = 2; i <= ec; i++)
                {
                    var d = EdgeDirection(edges.Item(i));
                    if (d != null && !IsParallel(dir1, d)) { dir2 = d; break; }
                }
                if (dir2 == null) return null;

                double nx = dir1[1] * dir2[2] - dir1[2] * dir2[1];
                double ny = dir1[2] * dir2[0] - dir1[0] * dir2[2];
                double nz = dir1[0] * dir2[1] - dir1[1] * dir2[0];
                double mag = System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (mag < 1e-12) return null;
                return new[] { nx / mag, ny / mag, nz / mag };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>取边的方向向量(终点 - 起点),走 Edge.GetEndPoints 返回的两个 Double[3]。</summary>
        private static double[] EdgeDirection(object edgeObj)
        {
            try
            {
                var edge = (SolidEdgeGeometry.Edge)edgeObj;
                Array startPt = Array.CreateInstance(typeof(double), 0);
                Array endPt = Array.CreateInstance(typeof(double), 0);
                edge.GetEndPoints(ref startPt, ref endPt);
                if (startPt == null || endPt == null || startPt.Length < 3 || endPt.Length < 3) return null;
                double dx = Convert.ToDouble(endPt.GetValue(0), CultureInfo.InvariantCulture) - Convert.ToDouble(startPt.GetValue(0), CultureInfo.InvariantCulture);
                double dy = Convert.ToDouble(endPt.GetValue(1), CultureInfo.InvariantCulture) - Convert.ToDouble(startPt.GetValue(1), CultureInfo.InvariantCulture);
                double dz = Convert.ToDouble(endPt.GetValue(2), CultureInfo.InvariantCulture) - Convert.ToDouble(startPt.GetValue(2), CultureInfo.InvariantCulture);
                // 2026-10-04 真机:整圆边(内孔/圆角)的 start==end,弦向量是零向量;零向量进叉积会把
                // "平面性/轴向"判定整体污染成 null(零向量与任何边都"共线")。退化边直接按 null 处理。
                if (dx * dx + dy * dy + dz * dz < 1e-24) return null;
                return new[] { dx, dy, dz };
            }
            catch { return null; }
        }

        private static bool IsParallel(double[] a, double[] b, double eps = 1e-6)
        {
            double cx = a[1] * b[2] - a[2] * b[1];
            double cy = a[2] * b[0] - a[0] * b[2];
            double cz = a[0] * b[1] - a[1] * b[0];
            return System.Math.Sqrt(cx * cx + cy * cy + cz * cz) < eps;
        }

        // ==================== P3 新 op 实现(2026-09-23) ====================
        // 签名依据 Interop.SolidEdge.dll 反射核实(非对照表的 pywin32 晚绑定形态):
        //   Drafts.Add(plane, n, FaceSetArray[Object], DraftAngleArray[Object], DraftSide)
        //   Splits.Add(nTargets, ref TargetArray, nTools, ref ToolsArray, DesignBodyOpt, ConstrBodyOpt)
        //   WebNetworks.Add(nProfiles, ref Profile, thickness, WebDir, [ExtentType],[ProfileExtType],[FiniteDepth])
        //   ExtrudedSurfaces.AddFinite(nProfiles, ref ProfileArray, ProfilePlaneSide, Depth, [WantEndCaps])  ← 在 Constructions 上
        //   Thickens.Add(Side, offsetDistance, nFaces, ref Faces)  ← 在 Model 上
        //   DeleteBlends.Add(BlendsToDelete[Object]) / DeleteFaces.Add(FaceSetToDelete[Object])

        /// <summary>
        /// draft 拔模:对一个或多个面施加拔模角,绕 refPlane 与面的交线旋转。
        /// DraftSide 只认 igInside=4 / igOutside=5(传 1/2/3 全 E_FAIL,对照表真机验证)。
        /// </summary>
        private static object DraftOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, object> namedFeatures)
        {
            if (!spec.Angle.HasValue || spec.Angle.Value <= 0)
                return new { op = "draft", name = name, status = "error",
                    message = "draft 必须提供 angle(弧度,且 > 0,通常 0.01~0.1)。" };
            int side = spec.Side ?? 0;
            if (side != 4 && side != 5)
                return new { op = "draft", name = name, status = "error",
                    message = "draft 的 side 必须是 4(igInside=向内拔模)或 5(igOutside=向外拔模);" +
                              "SE 真机验证:传 1/2/3 一律 E_FAIL。" };

            if (!spec.HasFaceRef)
                return new { op = "draft", name = name, status = "error",
                    message = "draft 需通过 faceOf 指定要拔模的面(如 \"faceOf\":\"@base\",\"faceNormal\":[0,0,1])。" };

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "draft", name = name, status = "error", message = "draft 前必须先有实体(extrude)。" };
            object model = Get(models, "Item", 1);

            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);

            List<string> warnings;
            object targetFace;
            try { targetFace = ResolveFaceRef(context, doc, spec.FaceRef, namedFeatures, out warnings); }
            catch (Exception ex)
            {
                return new { op = "draft", name = name, status = "error", message = "面引用解析失败:" + ex.Message };
            }

            try
            {
                object[] faceArr = new object[] { targetFace };
                double[] angleArr = new double[] { spec.Angle.Value };
                object feat = ((SolidEdgePart.Model)model).Drafts.Add(
                    plane, 1, faceArr, angleArr,
                    (SolidEdgePart.FeaturePropertyConstants)side);
                return FeatureResult("draft", name, feat, context, null, "Draft", warnings,
                    new { plane = spec.PlaneRef, faceOf = spec.FaceRef.FeatureName, angle = spec.Angle.Value, side = side });
            }
            catch (Exception ex)
            {
                return new { op = "draft", name = name, status = "error",
                    message = "Drafts.Add 失败:" + DescribeException(ex) +
                              "(常见原因:面不是可拔模面、拔模角过大自交、draft 枢轴面与目标面不相交)。" };
            }
        }

        /// <summary>
        /// split 分割:用 ref-plane 把目标实体切成两半。走 Splits.Add(nTargets, ref bodies, nTools, ref tools, ...)。
        /// 对照表称对方"ref-plane 路径实测 OK"但那是 pywin32 晚绑定;本机强类型 interop 失败即入拒绝清单。
        /// </summary>
        private static object SplitOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, object> namedFeatures)
        {
            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "split", name = name, status = "error", message = "split 前必须先有实体。" };
            object model = Get(models, "Item", 1);

            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);

            // target 缺省用当前 Models.Item(1) 的 Body;给了 @别名/obj-K 则做存在性校验
            if (!string.IsNullOrEmpty(spec.Target))
            {
                // target 仅做存在性校验(@别名/obj-K 已定义即通过);Body 一律取 Model.Body——
                // 特征对象(ExtrudedProtrusion 等)没有 Body 属性(实测 GetIDsOfNames 失败 0x80020006),
                // Body 是 Model 的成员,单实体场景目标体就是 Models.Item(1).Body。
                try
                {
                    ResolveFeatureObject(context, spec.Target, namedFeatures);
                }
                catch (Exception ex)
                {
                    return new { op = "split", name = name, status = "error", message = "target 解析失败:" + ex.Message };
                }
            }
            object targetBody = Get(model, "Body");

            try
            {
                Array targetArr = new object[] { targetBody };
                Array toolArr = new object[] { plane };
                object feat = ((SolidEdgePart.Model)model).Splits.Add(
                    1, ref targetArr, 1, ref toolArr,
                    SolidEdgePart.SETargetDesignBodyOption.igCreateMultipleDesignBodiesOnNonManifoldOption,
                    SolidEdgePart.SETargetConstructionBodyOption.igCreateSingleConstructionGeneralBodyOnNonManifoldOption);
                return FeatureResult("split", name, feat, context, null, "Split", null,
                    new { plane = spec.PlaneRef, target = spec.Target ?? "Models.Item(1)" });
            }
            catch (Exception ex)
            {
                return new
                {
                    op = "split", name = name, status = "error", unsupported = true,
                    message = "Splits.Add 在 SE 强类型 interop 通道失败:" + DescribeException(ex) +
                              "(对方项目称 ref-plane 路径 OK,但那是 pywin32 晚绑定,通道不同。)",
                    alternatives = new[]
                    {
                        "用 cut 沿分割面切一刀(切开成两个体需后续处理)",
                        "用 se_invoke_member 走晚绑定试 Splits.Add 的另一形态"
                    },
                    fix = new { action = "use_alternative", see = "alternatives" }
                };
            }
        }

        /// <summary>
        /// web_network 腹板网:与 rib 并列的薄板特征,闭合轮廓 + 厚度 + 深度 + 方向。
        /// WebNetworks.Add(nProfiles, ref Profile, thickness, WebDirection, [ExtentType],[ProfileExtType],[FiniteDepth])。
        /// 方向 seWebNormal=1 / seWebReverseNormal=2(与 rib 的 MaterialSide=igRight=2 / igLeft=1 同向自愈语义)。
        /// </summary>
        private static object WebNetworkOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, object> namedFeatures)
        {
            if (!spec.Thickness.HasValue || spec.Thickness.Value <= 0)
                return new { op = "web_network", name = name, status = "error",
                    message = "web_network 必须提供 thickness(米,且 > 0)。" };
            if (spec.HasCircle || spec.HasCircles || spec.HasSlot || spec.Loops.Count > 0) { }
            else
                return new { op = "web_network", name = name, status = "error",
                    message = string.IsNullOrEmpty(spec.ShapeError)
                        ? "web_network 需闭合轮廓(rect/polygon/loops/circle)。"
                        : spec.ShapeError };

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "web_network", name = name, status = "error", message = "web_network 前必须先有实体。" };
            object model = Get(models, "Item", 1);
            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);
            bool visible = spec.Visible ?? false;

            var specWarnings = new List<string>();
            // 方向自愈:缺省 seWebNormal=1,僵尸则换 seWebReverseNormal=2(同 rib 思路)
            int wd = spec.Side ?? 1;
            bool wdFree = !spec.Side.HasValue;
            object feat = null;
            object profile = null;
            int usedWd = wd;
            int attempts = wdFree ? 2 : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                int candidate = attempt == 0 ? wd : (wd == 1 ? 2 : 1);
                profile = CreateProfileForFeature(context, doc, plane, spec, visible, specWarnings);
                try
                {
                    Array profArr = new object[] { profile };
                    double depth = spec.Depth ?? 0;
                    // ExtentType/ProfileExtensionType 用 WebNetwork 专属枚举(反射核实),勿混用通用 FeaturePropertyConstants
                    // (实测 igFinite/igExtend 混传 → E_INVALIDARG):seWebExtendFinite+FiniteDepth、seWebProfileNoExtend(轮廓即边界)
                    feat = ((SolidEdgePart.Model)model).WebNetworks.Add(
                        1, ref profArr, spec.Thickness.Value,
                        (SolidEdgePart.WebNetworkFeatureConstants)candidate,
                        SolidEdgePart.WebNetworkFeatureConstants.seWebExtendFinite,
                        SolidEdgePart.WebNetworkFeatureConstants.seWebProfileNoExtend,
                        depth);
                    long? st = SafeLong(Get(feat, "Status"));
                    if (st.HasValue && st.Value != StatusZombie) { usedWd = candidate; break; }
                    if (attempt < attempts - 1) { DiscardCutCandidate(profile, feat); feat = null; }
                }
                catch (Exception)
                {
                    if (attempt < attempts - 1) { DiscardCutCandidate(profile, null); }
                    else throw;
                }
            }

            var resolved = new { plane = spec.PlaneRef, thickness = spec.Thickness.Value, depth = spec.Depth, webDirection = usedWd };
            if (feat == null)
                return new { op = "web_network", name = name, status = "error", resolved = resolved,
                    message = "web_network 在两个方向都未生成几何(6311 僵尸)。",
                    diagnosis = "检查轮廓是否闭合、plane 是否贴合实体、方向是否朝实体。" };

            return FeatureResult("web_network", name, feat, context, profile, "WebNetwork", specWarnings, resolved);
        }

        /// <summary>
        /// extrude_surface 曲面拉伸:把轮廓拉成【曲面】(非实体),产物落在 doc.Constructions.ExtrudedSurfaces。
        /// 随后 thicken 把曲面加厚成实体。AddFinite(nProfiles, ref ProfileArray, ProfilePlaneSide, Depth)。
        /// </summary>
        private static object ExtrudeSurfaceOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, object> namedFeatures,
            Dictionary<string, List<object>> surfaceFaces)
        {
            if (!spec.Depth.HasValue || spec.Depth.Value <= 0)
                return new { op = "extrude_surface", name = name, status = "error",
                    message = "extrude_surface 必须提供 depth(米,且 > 0)。" };
            if (spec.HasCircle || spec.HasCircles || spec.HasSlot || spec.Loops.Count > 0) { }
            else
                return new { op = "extrude_surface", name = name, status = "error",
                    message = string.IsNullOrEmpty(spec.ShapeError) ? "extrude_surface 需轮廓(rect/polygon/loops/circle)。" : spec.ShapeError };

            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);
            bool visible = spec.Visible ?? false;
            var specWarnings = new List<string>();
            object profile = CreateProfileForFeature(context, doc, plane, spec, visible, specWarnings);

            // 曲面拉伸方向:缺省 igRight=2(同 extrude),允许自愈到 igLeft=1
            int side = spec.Side ?? 2;

            object constructions = Get(doc, "Constructions");
            object exSurfs = Get(constructions, "ExtrudedSurfaces");
            try
            {
                Array profArr = new object[] { profile };
                object feat = ((SolidEdgePart.ExtrudedSurfaces)exSurfs).AddFinite(
                    1, ref profArr,
                    (SolidEdgePart.FeaturePropertyConstants)side,
                    spec.Depth.Value);

                // P3.4 缓存曲面体面:AddFinite 刚加的构造体即 Constructions 最后一个成员(实测 Constructions.Item(n).Name 为空、
                // 与 AddFinite 返回对象 ReferenceEquals=False——不同 RCW,按名/引用匹配不可靠,创建时缓存最稳)。
                // 缓存后 ThickenOp 的 faceOf "@别名" 直接命中,不再走 GetSurfaceFaces 匹配。
                if (!string.IsNullOrEmpty(name))
                {
                    try
                    {
                        int cCount = Count(constructions);
                        if (cCount >= 1)
                        {
                            object cons = Get(constructions, "Item", cCount);
                            object body = Get(cons, "Body");
                            if (body != null)
                            {
                                object faces = Get(body, "Faces", 1);   // igQueryAll=1
                                int n = Count(faces);
                                var list = new List<object>();
                                for (int f = 1; f <= n; f++) list.Add(Get(faces, "Item", f));
                                if (list.Count > 0) surfaceFaces[name] = list;
                            }
                        }
                    }
                    catch { /* 缓存失败不阻断:ThickenOp 走 GetSurfaceFaces 兜底 */ }
                }

                // 产物 kind 用 ExtrudedSurface,登记到 namedFeatures 后供 thicken 的 faceOf 引用
                return FeatureResult("extrude_surface", name, feat, context, profile, "ExtrudedSurface", specWarnings,
                    new { plane = spec.PlaneRef, depth = spec.Depth.Value, side = side,
                          faceCount = surfaceFaces.TryGetValue(name ?? "", out var fl) ? fl.Count : 0 });
            }
            catch (Exception ex)
            {
                DiscardCutCandidate(profile, null);
                return new { op = "extrude_surface", name = name, status = "error",
                    message = "ExtrudedSurfaces.AddFinite 失败:" + DescribeException(ex) };
            }
        }

        /// <summary>
        /// thicken 曲面加厚:把 extrude_surface 产出的曲面加厚成实体。
        /// faces 必须取自 Constructions.Item(n).Body.Faces(igQueryAll)——surface 自身 .Faces 抛异常(对照表坑)。
        /// Thickens.Add(Side, offsetDistance, nFaces, ref Faces);Side: igOutside=5(向外加厚,默认) / igInside=4。
        /// </summary>
        private static object ThickenOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedFeatures, Dictionary<string, List<object>> surfaceFaces)
        {
            if (!spec.Thickness.HasValue || spec.Thickness.Value <= 0)
                return new { op = "thicken", name = name, status = "error",
                    message = "thicken 必须提供 thickness(米,且 > 0)。" };
            if (!spec.HasFaceRef)
                return new { op = "thicken", name = name, status = "error",
                    message = "thicken 需通过 faceOf 指向 extrude_surface 产出的曲面特征(如 \"faceOf\":\"@s1\")。" };

            object featObj;
            try { featObj = ResolveFeatureObject(context, spec.FaceRef.FeatureName, namedFeatures); }
            catch (Exception ex)
            {
                return new { op = "thicken", name = name, status = "error", message = "面引用解析失败:" + ex.Message };
            }

            // 取曲面体的所有面:优先用 ExtrudeSurfaceOp 创建时缓存的体面(按 @别名 名精确命中);
            // 兜底走 GetSurfaceFaces 扫描 Constructions(surface 自身 .Faces 会抛,须走 Body.Faces(igQueryAll=1))
            List<object> surfFaces = null;
            if (!string.IsNullOrEmpty(spec.FaceRef.FeatureName)
                && surfaceFaces.TryGetValue(spec.FaceRef.FeatureName, out var cached)
                && cached != null && cached.Count > 0)
            {
                surfFaces = cached;
            }
            else
            {
                try { surfFaces = GetSurfaceFaces(featObj, doc); }
                catch (Exception ex)
                {
                    return new { op = "thicken", name = name, status = "error", unsupported = true,
                        message = "读取曲面体面失败:" + ex.Message +
                                  "(对照表坑:surface 自身 .Faces 抛异常,须走 Constructions.Item.Body.Faces;本通道也失败则入拒绝清单)",
                        alternatives = new[] { "客户端用 se_read_geometry 读出曲面 Face.ID 后,改用其它 op 或 se_invoke_member" },
                        fix = new { action = "use_alternative" } };
                }
            }
            if (surfFaces == null || surfFaces.Count == 0)
                return new { op = "thicken", name = name, status = "error",
                    message = "曲面特征没有可加厚的面(faceOf 指向的可能不是 extrude_surface 产物)。" };

            // faceNormal 可选:在曲面面里按法向过滤(非平面面 TryGetFaceNormal 返回 null,跳过)
            if (spec.FaceRef.Normal != null)
            {
                double nx = spec.FaceRef.Normal[0], ny = spec.FaceRef.Normal[1], nz = spec.FaceRef.Normal[2];
                double mag = System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (mag > 1e-12) { nx /= mag; ny /= mag; nz /= mag; }
                var filtered = new List<object>();
                foreach (var f in surfFaces)
                {
                    double[] nrm = TryGetFaceNormal(f);
                    if (nrm == null) continue;
                    if (nrm[0] * nx + nrm[1] * ny + nrm[2] * nz > 1 - 1e-6) filtered.Add(f);
                }
                if (filtered.Count > 0) surfFaces = filtered;
            }

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "thicken", name = name, status = "error", message = "thicken 需在已有 Model 上执行(文档应有 Models)。" };
            object model = Get(models, "Item", 1);

            int side = spec.Side ?? 5;   // 默认 igOutside=5(向外加厚)
            try
            {
                Array facesArr = surfFaces.ToArray();
                object feat = ((SolidEdgePart.Model)model).Thickens.Add(
                    (SolidEdgePart.FeaturePropertyConstants)side,
                    spec.Thickness.Value, surfFaces.Count, ref facesArr);
                return FeatureResult("thicken", name, feat, context, null, "Thicken", null,
                    new { faceOf = spec.FaceRef.FeatureName, thickness = spec.Thickness.Value, side = side, faceCount = surfFaces.Count });
            }
            catch (Exception ex)
            {
                // 2026-09-23 P3 批2 真机:in-process Thickens.Add 全组合 E_INVALIDARG(0-based/1-based 数组、Side 4/5/97/98/160、面数 1/4/8)
                // 按 pattern 惯例诚实拒绝:SE 2022 强类型 interop 下 Thickens.Add 不可达,勿再烧调用方重试
                return new { op = "thicken", name = name, status = "error", unsupported = true,
                    message = "Thickens.Add 失败:" + DescribeException(ex) +
                              "(SE 2022 实测全组合 E_INVALIDARG:数组 0-based/1-based、Side igInside=4/igOutside=5/97/98/160、面数 1/4/8 均失败;in-process 与脚本子进程结论一致,判定该 COM 通道不可达)",
                    alternatives = new[] { "用 extrude_surface + 后续 extrude/cut 手工造壁(薄板另走 rib/web_network)", "用 se_invoke_member 走晚绑定再探(低概率)", "改用同步建模加厚(见 P4 face_edit)" },
                    fix = new { action = "use_alternative" } };
            }
        }

        /// <summary>
        /// delete_face 删面:删除 blend(圆角)面并愈合。需 confirm=true(破坏性操作)。
        /// 走 DeleteBlends.Add(BlendsToDelete)——专门删 blend/round 面;若目标非 blend 面则失败入拒绝清单。
        /// </summary>
        private static object DeleteFaceOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedFeatures)
        {
            if (spec.Confirm != true)
                return new { op = "delete_face", name = name, status = "error",
                    message = "delete_face 是破坏性操作,必须显式传 \"confirm\":true 才会执行。" };
            if (!spec.HasFaceRef)
                return new { op = "delete_face", name = name, status = "error",
                    message = "delete_face 需通过 faceOf/faceIndex 指定要删除的面。" };

            List<string> warnings;
            object targetFace;
            try { targetFace = ResolveFaceRef(context, doc, spec.FaceRef, namedFeatures, out warnings, blendMode: true); }
            catch (Exception ex)
            {
                return new { op = "delete_face", name = name, status = "error", message = "面引用解析失败:" + ex.Message };
            }

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "delete_face", name = name, status = "error", message = "delete_face 前必须先有实体。" };
            object model = Get(models, "Item", 1);

            // 先试 DeleteBlends(删 blend/round 面,自动愈合);失败再试 DeleteFaces(删任意面)
            try
            {
                object feat = ((SolidEdgePart.Model)model).DeleteBlends.Add(targetFace);
                long st = 0;
                try { st = Convert.ToInt64(Get(feat, "Status")); } catch { }
                if (st == 1216476310)
                {
                    return FeatureResult("delete_face", name, feat, context, null, "DeleteBlend", warnings,
                        new { faceOf = spec.FaceRef.FeatureName, faceIndex = spec.FaceRef.Index, kind = "blend" });
                }
                // 2026-09-23 真机:对非 blend 面(如孔圆柱面),DeleteBlends 不抛异常而是产出 Status=6311
                // 僵尸特征——按异常切通道的旧逻辑永远走不到 DeleteFaces。此处删掉僵尸再抛给兜底通道。
                try { Get(feat, "Delete"); } catch { }
                throw new InvalidOperationException("DeleteBlends 未成体(Status=" + st + "),目标疑似非 blend 面");
            }
            catch (Exception blendEx)
            {
                // 非 blend 面:试 DeleteFaces.Add(删任意面,带愈合)
                try
                {
                    object feat = ((SolidEdgePart.Model)model).DeleteFaces.Add(targetFace);
                    return FeatureResult("delete_face", name, feat, context, null, "DeleteFace", warnings,
                        new { faceOf = spec.FaceRef.FeatureName, faceIndex = spec.FaceRef.Index, kind = "general",
                              note = "目标不是 blend 面,改走 DeleteFaces 通用删面通道(DeleteBlends 失败:" + blendEx.Message + ")" });
                }
                catch (Exception ex)
                {
                    return new { op = "delete_face", name = name, status = "error", unsupported = true,
                        message = "DeleteBlends 与 DeleteFaces 都失败:" + DescribeException(ex),
                        alternatives = new[] { "用 cut 切掉目标面所在区域", "用 se_invoke_member 走晚绑定试其它删除形态" },
                        fix = new { action = "use_alternative" } };
                }
            }
        }

        /// <summary>
        /// 解析面引用里的特征对象(仅 @别名/obj-K 存在性反查,不做面选择)。
        /// 供 thicken(自己从曲面体取面)与 split(target) 复用。
        /// </summary>
        private static object ResolveFeatureObject(SolidEdgeContext context, string fof,
            Dictionary<string, object> namedFeatures)
        {
            if (string.IsNullOrWhiteSpace(fof))
                throw new ArgumentException("faceOf/target 必须是非空字符串(@别名 或 obj-K 句柄)");
            if (fof.StartsWith("@", StringComparison.Ordinal))
            {
                string key = fof.Substring(1);
                if (!namedFeatures.TryGetValue(key, out var featObj))
                    throw new ArgumentException("未找到本批内命名特征 \"@" + key + "\"(需先给前面的特征带 name 创建)");
                return featObj;
            }
            if (fof.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                var h = context.GetHandle(fof);
                if (h == null || h.ComObject == null)
                    throw new ArgumentException("句柄表里找不到特征对象 " + fof);
                return h.ComObject;
            }
            throw new ArgumentException("faceOf/target 必须以 @ 或 obj- 开头(当前 \"" + fof + "\")");
        }

        /// <summary>
        /// 取曲面特征的体所有面。★ 不走 featObj.Body——特征对象(ExtrudedSurface 等)没有 Body 属性
        /// (实测 GetIDsOfNames 失败 0x80020006,与 ExtrudedProtrusion 同款);且 surface 自身 .Faces 抛异常。
        /// 唯一可靠路径:扫 doc.Constructions 取 Constructions.Item(n).Body.Faces(igQueryAll=1)。
        /// ★ 匹配策略:Constructions.Item(n).Name 实测为空、且与 AddFinite 返回对象 ReferenceEquals=False(不同 RCW),
        /// 无法按名/引用匹配 → 兜底启发式:从最新成员倒序取第一个有 Body 面的构造体(批内单曲面场景即命中)。
        /// 首选通道是 ExtrudeSurfaceOp 创建时缓存(ThickenOp 按 @别名 精确取),本方法仅兜底 obj-K/未缓存场景。
        /// </summary>
        private static List<object> GetSurfaceFaces(object featObj, object doc)
        {
            var result = new List<object>();
            object constructions = Get(doc, "Constructions");
            int cCount = Count(constructions);
            for (int i = cCount; i >= 1; i--)
            {
                object item = Get(constructions, "Item", i);
                try
                {
                    object body = Get(item, "Body");
                    if (body == null) continue;
                    object faces = Get(body, "Faces", 1);   // igQueryAll=1
                    int n = Count(faces);
                    if (n == 0) continue;
                    for (int f = 1; f <= n; f++) result.Add(Get(faces, "Item", f));
                    return result;
                }
                catch { }
            }
            return result;
        }

        /// <summary>按名字找特征:先查 obj-K 句柄,再扫常用特征集合的 Name(学对方 DesignEdgebarFeatures 遍历思路)。</summary>
        private static object FindFeatureByName(SolidEdgeContext context, object doc, string of, out object model)
        {
            model = null;
            if (string.IsNullOrEmpty(of)) return null;

            if (of.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                var h = context.GetHandle(of);
                if (h != null && h.ComObject != null)
                {
                    try { model = Get(h.ComObject, "Parent"); } catch { }
                    return h.ComObject;
                }
                return null;
            }

            object models = Get(doc, "Models");
            if (Count(models) == 0) return null;
            model = Get(models, "Item", 1);

            foreach (var collName in new[] { "ExtrudedProtrusions", "ExtrudedCutouts", "RevolvedProtrusions", "RevolvedCutouts", "Rounds", "Chamfers", "Ribs", "Holes" })
            {
                object coll;
                try { coll = Get(model, collName); } catch { continue; }
                int n = Count(coll);
                for (int i = 1; i <= n; i++)
                {
                    object item = Get(coll, "Item", i);
                    string nm = SafeString(Get(item, "Name"));
                    if (string.Equals(nm, of, StringComparison.OrdinalIgnoreCase)) return item;
                }
            }
            return null;
        }

        /// <summary>
        /// 建旋转轮廓:ProfileSets.Add → Profiles.Add(plane) → 截面闭环(逐线 + 端点重合约束)
        // ============================ P2(2026-09-23):多轮廓 loft / sweep / helix ============================
        //
        // 三个 op 的共用约定(依据 SE2022 SDK 离线文档 + 对照表情报):
        //  - 均要求模型里已有基体特征。首特征通道(Models.AddLoftedProtrusion 18 参 /
        //    Models.AddSweptProtrusion / Models.AddFiniteBaseHelix 后者每 Part 仅许一次)P2 统一不开放,
        //    提示调用方先 extrude;
        //  - mode:"cut" 走对应 Cutouts 集合(与 revolve 的双通道同构);
        //  - 截面锚点 Origins:显式 origin > 周期截面(圆)传 0(SDK 文档明示)> 轮廓首点。
        //    非周期截面的锚点必须是轮廓上真实一点,硬编码 (0,0) 会静默无几何;
        //  - CrossSectionTypes 恒 igProfileBasedCrossSection(48):我们的截面全是草图 Profile,不是实体边。

        /// <summary>loft:放样凸台(默认)/ 放样除料(mode:"cut")。profiles ≥2 个截面,各自建草图后 AddSimple。</summary>
        private static object LoftOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            bool isCut = string.Equals(spec.Mode, "cut", StringComparison.OrdinalIgnoreCase);

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new
                {
                    op = "loft", name = name, status = "error",
                    message = "loft 需先有基体特征(首特征放样走 Models.AddLoftedProtrusion 18 参通道,P2 未开放)——本批或前一批先 extrude。",
                    diagnosis = "放样截面叠在已有实体上才有料可长/可切。"
                };

            object model = Get(models, "Item", 1);
            var specWarnings = new List<string>();
            var profiles = new List<object>();

            try
            {
                foreach (var entry in spec.Profiles)
                {
                    object plane = ResolvePlane(context, doc, entry.PlaneRef, namedPlanes);
                    profiles.Add(CreateProfileForFeature(context, doc, plane, entry, spec.Visible ?? false, specWarnings));
                }

                int n = profiles.Count;
                var sections = new object[n];
                var types = new object[n];
                var origins = new object[n];
                for (int i = 0; i < n; i++)
                {
                    sections[i] = profiles[i];
                    types[i] = 48;   // igProfileBasedCrossSection
                    origins[i] = SectionOrigin(spec.Profiles[i]);
                }

                object coll = Get(model, isCut ? "LoftedCutouts" : "LoftedProtrusions");
                // ★ AddSimple 含 SAFEARRAY 参数(CrossSections/Origins):本机实测 IDispatch 手工封送通道
                //   直接崩 SE(0x800706BE RPC 失败,2026-09-23,SE 2022)——与 sweep/helix 同因,必须走 PIA 强类型。
                //   强类型 9 参 = 7 必选 + NumGuideCurves/GuideCurves(无导线传 0/null),Interop.dll 反射核实。
                //   MaterialSide=igLeft(1)、Start/EndTangentType=igNone(44):SDK VB 示例取值。
                Array sectionArr = sections, typeArr = types, originArr = origins;
                object featObj = isCut
                    ? ((SolidEdgePart.LoftedCutouts)coll).AddSimple(n, sectionArr, typeArr, originArr,
                        SolidEdgePart.FeaturePropertyConstants.igLeft,
                        SolidEdgePart.FeaturePropertyConstants.igNone,
                        SolidEdgePart.FeaturePropertyConstants.igNone, 0, null)
                    : ((SolidEdgePart.LoftedProtrusions)coll).AddSimple(n, sectionArr, typeArr, originArr,
                        SolidEdgePart.FeaturePropertyConstants.igLeft,
                        SolidEdgePart.FeaturePropertyConstants.igNone,
                        SolidEdgePart.FeaturePropertyConstants.igNone, 0, null);

                return FeatureResult("loft", name, featObj, context, profiles.Count > 0 ? profiles[0] : null,
                    isCut ? "LoftedCutout" : "LoftedProtrusion", specWarnings,
                    new { mode = isCut ? "cut" : "protrusion", sections = n },
                    profiles.Count > 1 ? profiles.GetRange(1, profiles.Count - 1) : null);
            }
            catch
            {
                DiscardProfiles(profiles);   // AddSimple 半路抛:草图一定留着,全部清理再让上层报错
                throw;
            }
        }

        /// <summary>
        /// sweep:扫掠凸台(默认)/ 扫掠除料(mode:"cut")。profiles 首项=路径(trace 线弧混排 / polygon 开放链),
        /// 其余=截面。SDK 的 15 参 Add:路径在 TraceCurves、截面在 CrossSections【分开传】,
        /// SegmentMaps=0、两端 Extent=igNone(44)/0/null、MaterialSide=igLeft(1)。
        /// </summary>
        private static object SweepOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            bool isCut = string.Equals(spec.Mode, "cut", StringComparison.OrdinalIgnoreCase);

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new
                {
                    op = "sweep", name = name, status = "error",
                    message = "sweep 需先有基体特征(首特征扫掠走 Models.AddSweptProtrusion 通道,P2 未开放)——本批或前一批先 extrude。",
                    diagnosis = "扫掠截面沿路径叠在已有实体上才有料可长/可切。"
                };

            object model = Get(models, "Item", 1);
            var specWarnings = new List<string>();
            var profiles = new List<object>();   // [0]=路径,其余=截面

            try
            {
                bool visible = spec.Visible ?? false;
                var pathSpec = spec.Profiles[0];
                object pathPlane = ResolvePlane(context, doc, pathSpec.PlaneRef, namedPlanes);
                if (pathSpec.Trace != null)
                    profiles.Add(CreateProfileTrace(doc, pathPlane, pathSpec.Trace, visible));
                else if (pathSpec.OpenChain != null)
                    profiles.Add(CreateProfileOpenChain(doc, pathPlane, pathSpec.OpenChain, visible));
                else
                    profiles.Add(CreateProfileForFeature(context, doc, pathPlane, pathSpec, visible, specWarnings));

                for (int i = 1; i < spec.Profiles.Count; i++)
                {
                    var entry = spec.Profiles[i];
                    object plane = ResolvePlane(context, doc, entry.PlaneRef, namedPlanes);
                    profiles.Add(CreateProfileForFeature(context, doc, plane, entry, visible, specWarnings));
                }

                int nSec = profiles.Count - 1;
                var trace = new object[] { profiles[0] };
                var traceTypes = new object[] { 48 };
                var sections = new object[nSec];
                var types = new object[nSec];
                var origins = new object[nSec];
                for (int i = 0; i < nSec; i++)
                {
                    sections[i] = profiles[i + 1];
                    types[i] = 48;
                    origins[i] = SectionOrigin(spec.Profiles[i + 1]);
                }

                // ★ Add 含 ByRef SAFEARRAY 参数(CrossSections 等):IDispatch 晚绑定会错位
                //   (DISP_E 0x8002000F,puArgErr=3 指向第 4 参)——必须走强类型。签名(15 参):
                //   Add(NumTraceCurves, TraceCurves, TraceCurveTypes, NumSections, CrossSections,
                //       CrossSectionTypes, SectionOrigins, SegmentMaps, MaterialSide(FPC),
                //       StartExtentType(FPC), StartExtentValue, StartExtentRef,
                //       EndExtentType(FPC), EndExtentValue, EndExtentRef)
                var m = (SolidEdgePart.Model)model;
                Array traceArr = new object[] { profiles[0] };
                Array traceTypeArr = new object[] { 48 };
                Array sectionArr = (Array)sections;   // object[] → Array,ByRef SAFEARRAY 编组
                Array sectionTypeArr = (Array)types;
                Array originArr = (Array)origins;
                object featObj = isCut
                    ? m.SweptCutouts.Add(1, traceArr, traceTypeArr, nSec, sectionArr, sectionTypeArr, originArr, 0,
                            SolidEdgePart.FeaturePropertyConstants.igLeft,
                            SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null,
                            SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null)
                    : m.SweptProtrusions.Add(1, traceArr, traceTypeArr, nSec, sectionArr, sectionTypeArr, originArr, 0,
                            SolidEdgePart.FeaturePropertyConstants.igLeft,
                            SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null,
                            SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null);

                return FeatureResult("sweep", name, featObj, context, profiles.Count > 0 ? profiles[0] : null,
                    isCut ? "SweptCutout" : "SweptProtrusion", specWarnings,
                    new { mode = isCut ? "cut" : "protrusion", path = "profiles[0]", sections = nSec },
                    profiles.Count > 1 ? profiles.GetRange(1, profiles.Count - 1) : null);
            }
            catch
            {
                DiscardProfiles(profiles);
                throw;
            }
        }

        /// <summary>
        /// helix:螺旋凸台(默认)/ 螺旋除料(mode:"cut")。与 revolve 同构(plane + 单闭合截面 + axis),
        /// 另加 pitch/height/revolutions 三给二——这里把第三个补全(height = pitch × turns)后三参一致同传,
        /// 规避 SDK"三种范围定义"取哪两个的歧义。
        /// ★ AddFinite 的 CrossSectionArray 是 ByRef SAFEARRAY:与首特征 extrude 同类的 PIA 陷阱
        ///   (IDispatch 晚绑定在冷启动/新文档下 DISP_E_TYPEMISMATCH),必须走强类型调用。
        /// </summary>
        private static object HelixOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            bool isCut = string.Equals(spec.Mode, "cut", StringComparison.OrdinalIgnoreCase);

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new
                {
                    op = "helix", name = name, status = "error",
                    message = "helix 需先有基体特征(首特征螺旋走 Models.AddFiniteBaseHelix 且每 Part 仅许一次,P2 未开放)——本批或前一批先 extrude。",
                    diagnosis = "先 extrude 一个基体,螺旋特征叠在其上。"
                };

            object model = Get(models, "Item", 1);
            var specWarnings = new List<string>();

            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);
            // circle 截面是自然闭合的真圆曲线:CreateProfileRevolve 只画直线环(Lines2d),
            // 对 circle 会生成空剖面 → AddFinite E_FAIL。圆走专用"圆+旋转轴"路径(同 revolve 的轴语义)。
            object[] pair = spec.HasCircle
                ? CreateProfileRevolveCircle(doc, plane, spec.CircleX, spec.CircleY, spec.CircleR, spec, spec.Visible ?? false)
                : CreateProfileRevolve(context, doc, plane, spec, spec.Visible ?? false, specWarnings);
            object profile = pair[0];

            // 三给二 → 补全第三个(三者保持一致,SE 取任意两个都无歧义)
            double pitch = spec.Pitch ?? 0, height = spec.Height ?? 0, turns = spec.Revolutions ?? 0;
            if (!spec.Pitch.HasValue) pitch = height / turns;
            else if (!spec.Height.HasValue) height = pitch * turns;
            else if (!spec.Revolutions.HasValue) turns = height / pitch;

            try
            {
                Array csArr = new object[] { profile };
                object featObj;
                if (isCut)
                {
                    featObj = ((SolidEdgePart.Model)model).HelixCutouts.AddFinite(
                        (SolidEdgePart.RefAxis)pair[1],
                        SolidEdgePart.FeaturePropertyConstants.igStart, 1, ref csArr,
                        SolidEdgePart.FeaturePropertyConstants.igRight,
                        height, pitch, turns,
                        SolidEdgePart.FeaturePropertyConstants.igRight);
                }
                else
                {
                    featObj = ((SolidEdgePart.Model)model).HelixProtrusions.AddFinite(
                        (SolidEdgePart.RefAxis)pair[1],
                        SolidEdgePart.FeaturePropertyConstants.igStart, 1, ref csArr,
                        SolidEdgePart.FeaturePropertyConstants.igRight,
                        height, pitch, turns,
                        SolidEdgePart.FeaturePropertyConstants.igRight);
                }

                return FeatureResult("helix", name, featObj, context, profile, null, specWarnings,
                    new { mode = isCut ? "cut" : "protrusion", pitch = pitch, height = height, revolutions = turns });
            }
            catch
            {
                DiscardProfiles(new List<object> { profile });
                throw;
            }
        }

        /// <summary>
        /// 截面锚点(Origins 数组单项):显式 origin &gt; 周期截面(圆)传 0(SDK 文档明示)&gt; 轮廓首点。
        /// 非周期截面的锚点必须是轮廓上真实一点——硬编码 (0,0) 会静默无几何。
        /// </summary>
        private static object SectionOrigin(FeatureSpec entry)
        {
            if (entry.Origin != null && entry.Origin.Length >= 2)
                return new double[] { entry.Origin[0], entry.Origin[1] };

            if (entry.HasCircle) return 0;   // 周期截面:SDK 明示可传 0

            if (entry.HasSlot)
            {
                // 腰孔左端点(轮廓上真实一点):中心 - 长轴方向 × 半长
                double half = entry.SlotLength / 2.0;
                return new double[] { entry.SlotX - Math.Cos(entry.SlotAngle) * half,
                                      entry.SlotY - Math.Sin(entry.SlotAngle) * half };
            }

            if (entry.Loops.Count > 0 && entry.Loops[0].Length > 0)
                return new double[] { entry.Loops[0][0][0], entry.Loops[0][0][1] };

            return 0;
        }

        /// <summary>
        /// 建开放链轮廓(sweep 路径专用):n 点画 n-1 条线,相邻线端点重合约束;
        /// 【不】闭合、不加末点-首点约束——路径是"线",不是"面"(Line2d 索引:0=起点,1=终点)。
        /// </summary>
        private static object CreateProfileOpenChain(object doc, object plane, double[][] pts, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object lines = Get(profile, "Lines2d");
            object relations = Get(profile, "Relations2d");

            var lineObjs = new object[pts.Length - 1];
            for (int i = 0; i + 1 < pts.Length; i++)
                lineObjs[i] = Call(lines, "AddBy2Points",
                    new object[] { pts[i][0], pts[i][1], pts[i + 1][0], pts[i + 1][1] });

            for (int i = 0; i + 1 < lineObjs.Length; i++)
                Call(relations, "AddKeypoint", new object[] { lineObjs[i], 1, lineObjs[i + 1], 0 });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建【线段 + 真圆弧】混排的开放路径草图(sweep 首项 trace 专用,2026-10-04)。
        /// 与 CreateProfileOpenChain(纯折线)并列:直线走 Lines2d.AddBy2Points,圆弧走
        /// Arcs2d.AddByCenterStartEnd,段间用 Relations2d.AddKeypoint 端点首尾相接。
        /// ★ 端点索引按元素类型不同(见 CreateProfileSlot 注释):Line2d 0=起点/1=终点;Arc2d 0=圆心/1=起点/2=终点。
        /// ★ AddByCenterStartEnd 只能【逆时针】,而弧段语义是"start→end 的劣弧(≤180°)"——
        ///   若 start→end 的逆时针扫掠角 > 180°,说明要的是顺时针劣弧,此时【交换起终点】调用
        ///   (同一条圆弧、反向遍历),否则会画出法向相反的大圆弧(2026-10-04 用户实测:右转弯头不相切)。
        /// </summary>
        private static object CreateProfileTrace(object doc, object plane, List<PathSegment> segs, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object lines = Get(profile, "Lines2d");
            object arcs = Get(profile, "Arcs2d");
            object relations = Get(profile, "Relations2d");

            var objs = new List<object>();
            var startIdx = new List<int>();   // 该段【沿路径流向】起点的关键点索引
            var endIdx = new List<int>();     // 该段【沿路径流向】终点的关键点索引
            foreach (var seg in segs)
            {
                if (seg.Kind == "arc")
                {
                    double a0 = Math.Atan2(seg.P0[1] - seg.Center[1], seg.P0[0] - seg.Center[0]);
                    double a1 = Math.Atan2(seg.P1[1] - seg.Center[1], seg.P1[0] - seg.Center[0]);
                    double ccw = a1 - a0;
                    while (ccw <= 1e-12) ccw += 2.0 * Math.PI;    // 归一化到 (0, 2π]
                    if (ccw > Math.PI + 1e-9)                     // 逆时针是劣弧 → 交换,走顺时针劣弧
                    {
                        objs.Add(Call(arcs, "AddByCenterStartEnd", new object[]
                            { seg.Center[0], seg.Center[1], seg.P1[0], seg.P1[1], seg.P0[0], seg.P0[1] }));
                        startIdx.Add(2); endIdx.Add(1);           // 弧自身起点=路径终点,终点=路径起点
                    }
                    else
                    {
                        objs.Add(Call(arcs, "AddByCenterStartEnd", new object[]
                            { seg.Center[0], seg.Center[1], seg.P0[0], seg.P0[1], seg.P1[0], seg.P1[1] }));
                        startIdx.Add(1); endIdx.Add(2);
                    }
                }
                else
                {
                    objs.Add(Call(lines, "AddBy2Points",
                        new object[] { seg.P0[0], seg.P0[1], seg.P1[0], seg.P1[1] }));
                    startIdx.Add(0); endIdx.Add(1);
                }
            }

            // 端点首尾相接:上一段(沿流向)终点 ↔ 下一段(沿流向)起点
            for (int i = 0; i + 1 < objs.Count; i++)
                Call(relations, "AddKeypoint",
                    new object[] { objs[i], endIdx[i], objs[i + 1], startIdx[i + 1] });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>批量清理草图(多轮廓 op 失败回滚用):删每个 profile 所属的 ProfileSet。</summary>
        private static void DiscardProfiles(List<object> profiles)
        {
            if (profiles == null) return;
            foreach (var p in profiles)
            {
                object ps = TryGetProfileSet(p);
                if (ps != null) TryDelete(ps);
            }
        }

        /// → 单独画旋转轴(不进闭环)→ SetAxisOfRevolution(必须在 End 之前)→ End(0)→ 隐藏草图。
        /// 返回 [Profile, RefAxis]:RefAxis 要原样交给 AddFinite。
        /// </summary>
        private static object[] CreateProfileRevolve(SolidEdgeContext context, object doc, object plane, FeatureSpec spec, bool visible, List<string> specWarnings)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object lines = Get(profile, "Lines2d");
            object relations = Get(profile, "Relations2d");

            // 1) 截面:每个环逐线首尾相连 + 端点重合约束(与 CreateProfileMulti 同逻辑)
            var loopLines = new List<(object line, double[] p0, double[] p1)>();
            foreach (var pts in spec.Loops)
            {
                int n = pts.Length;
                var lineObjs = new object[n];
                for (int i = 0; i < n; i++)
                {
                    var p0 = pts[i];
                    var p1 = pts[(i + 1) % n];
                    lineObjs[i] = Call(lines, "AddBy2Points", new object[] { p0[0], p0[1], p1[0], p1[1] });
                    loopLines.Add((lineObjs[i], p0, p1));
                }
                for (int i = 0; i < n; i++)
                {
                    Call(relations, "AddKeypoint", new object[] { lineObjs[i], 1, lineObjs[(i + 1) % n], 0 });
                }
            }

            // 1.5) 声明式约束/标注(必须在 End 之前的开放上下文里;轴独立于截面,不占 dims 的 element 索引)
            ApplySpecConstraints(context, doc, profile, spec, loopLines, specWarnings);

            // 2) 旋转轴:独立构造线,不参与上面的闭环约束
            object axisLine = Call(lines, "AddBy2Points",
                new object[] { spec.AxisP1[0], spec.AxisP1[1], spec.AxisP2[0], spec.AxisP2[1] });

            // 2.5) AutoConstraint 时把轴线整体固定,否则轴自身 4 个自由度悬空(DOF 会计不为零)
            if (spec.AutoConstraint == true)
            {
                try { Call(relations, "AddFix", new object[] { axisLine }); }
                catch (Exception ex) { specWarnings.Add("axisFix: " + ex.Message); }
            }

            // 3) 指定旋转轴(End 之前),返回值 RefAxis 交给 AddFinite
            object refAxis = Call(profile, "SetAxisOfRevolution", new object[] { axisLine });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);

            return new object[] { profile, refAxis };
        }

        /// <summary>
        /// 读包围盒 [xmin,ymin,zmin,xmax,ymax,zmax](米),用于诊断坐标。
        ///
        /// ★ 2026-09-14 修正:特征对象**本身没有 RangeBox 成员** —— 查 SDK
        ///   `SolidEdgePart~ExtrudedProtrusion_members.html` 对 RangeBox 是 **0 命中**,
        ///   所以原实现(先 dynamic 绑定、再裸 IDispatch 读,两条路都读特征自己)必然失败、
        ///   恒返 null:不是"读崩了",而是这个属性在特征上根本不存在。
        ///   改为读**它的宿主**(Parent,即所属 Model)的 RangeBox —— 那才是有诊断价值的范围。
        ///   仍读不到就返回 null(不猜、不误报)。
        ///   ⚠️ `Parent` 具体返回 Model 还是 Features 集合**尚未实机标定**;若不是 Model,
        ///      这里会安静地退回 null(与修正前行为一致),不会有副作用。
        /// </summary>
        private static double[] TryRangeBox(object featObj)
        {
            object host = null;
            try { host = Get(featObj, "Parent"); } catch { }
            return TryBodyRangeBox(host != null ? host : featObj);
        }

        /// <summary>
        /// 读宿主(Model)实体的包围盒,走**官方 `Body.GetRange`**(2026-09-14 修正的核心)。
        ///
        /// ★ 为什么不能用 `RangeBox`:全 SDK 查不到零件侧的 `RangeBox` 成员
        ///   (`ExtrudedProtrusion_members.html` / `Model_members.html` 均 **0 命中**;
        ///    `RangeBox` 只出现在钣金 `ShowRangeBox`(显示开关)与装配
        ///    `Occurrence.GetRangeBox()`(方法)里)→ 原实现读 `RangeBox` **必然恒返 null**,
        ///    不是"读崩了"而是根本没有这个属性。
        /// ★ 官方写法(`SolidEdgeGeometry~Body~GetRange.html` 的 C# 示例):
        ///     var body = (SolidEdgeGeometry.Body)model.Body;
        ///     var minPt = Array.CreateInstance(typeof(double), 0);
        ///     var maxPt = Array.CreateInstance(typeof(double), 0);
        ///     body.GetRange(ref minPt, ref maxPt);   // 两个 ByRef Double() out 参数
        ///   Remarks:返回的盒边平行于全局坐标系。实测 `Body.Vertices.Count=8`、
        ///   `Body.Faces(1).Count=6`,Body 对象可正常取到。
        /// </summary>
        private static double[] TryBodyRangeBox(object host)
        {
            if (host == null) return null;
            try
            {
                object bodyObj = Get(host, "Body");
                if (bodyObj == null) return null;
                var body = (SolidEdgeGeometry.Body)bodyObj;
                Array minPt = Array.CreateInstance(typeof(double), 0);
                Array maxPt = Array.CreateInstance(typeof(double), 0);
                body.GetRange(ref minPt, ref maxPt);
                if (minPt != null && maxPt != null && minPt.Length >= 3 && maxPt.Length >= 3)
                {
                    return new[]
                    {
                        Convert.ToDouble(minPt.GetValue(0)), Convert.ToDouble(minPt.GetValue(1)), Convert.ToDouble(minPt.GetValue(2)),
                        Convert.ToDouble(maxPt.GetValue(0)), Convert.ToDouble(maxPt.GetValue(1)), Convert.ToDouble(maxPt.GetValue(2))
                    };
                }
            }
            catch { }
            return null;
        }

        // ---------------- 公共封装 ----------------

        /// <summary>
        /// 读内嵌轮廓"是否欠约束"。true=欠约束 / false=完全约束 / null=读不到(不猜)。
        ///
        /// 判据来源(2026-09-14 实测):`IsUnderDefined` 名义上挂在 Sketch 上,而本工具走的是
        /// `ProfileSets.Add().Profiles.Add(plane)` 内嵌轮廓 —— 它**不产生 Sketch 节点**
        /// (实测该文档 `Sketches.Count` 恒为 0),看路径像是读不到。但实测 `Profile`(49 个属性)
        /// 虽无此成员,它的**宿主 `ProfileSet` 上就有 `IsUnderDefined`** —— 而本类建轮廓时手里
        /// 正持有 ProfileSet。所以走 `profile.Parent` 即可,不必改用独立 Sketch(那会多出
        /// Sketch 特征节点,与既有配方冲突)。
        ///
        /// ⚠️ 判据敏感性已有实测佐证:`sandbox_reset` 那块板(4 条线 + 端点/水平垂直约束、
        ///    **没加尺寸**)读出来是 True(欠约束)——与"还剩 2 个自由度"的预期一致。
        /// ⚠️ 反面尚未标定:声明完整约束后是否真能变 False(此前被 ExtrudeOp 首特征通道 bug
        ///    挡住,建不出特征就无从读),待后续实测。
        /// </summary>
        private static bool? TryUnderDefined(object profile)
        {
            if (profile == null) return null;
            try
            {
                object ps = Get(profile, "Parent");
                if (ps == null) return null;
                return Convert.ToBoolean(Get(ps, "IsUnderDefined"));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>安全读集合 Count:读不到返回 -1(用 -1 区分"0 个"与"读不到")。</summary>
        private static int SafeCount(object obj, string member)
        {
            try { return Convert.ToInt32(Get(Get(obj, member), "Count")); }
            catch { return -1; }
        }

        /// <summary>
        /// 所有建模 op 的统一出口——【内置强制校验】:
        /// 特征建完立刻读 Status,不等于 StatusOk 就判失败,并【自动删除该特征】回滚,
        /// 不让僵尸特征留在模型树上(留着会污染模型树、误导后续特征)。
        ///
        /// 于是调用方看到的 status 只有 "ok" / "error",既不用认识 Solid Edge 的状态码,
        /// 也不用记得自己去检查——验证固化在 op 里,对调用方无感。
        /// </summary>
        private static object FeatureResult(string op, string name, object featObj, SolidEdgeContext context,
            object profile = null, string kindOverride = null, List<string> specWarnings = null,
            object resolved = null, List<object> extraProfiles = null)
        {
            string featureName = SafeString(Get(featObj, "Name"));
            long? status = SafeLong(Get(featObj, "Status"));

            if (status.HasValue && status.Value != StatusOk)
            {
                // 先把草图容器抓在手里:特征一删,profile 常常就取不到 Parent 了
                // (实测:先删特征再回头取 Parent 会失败,草图就漏删了)。
                object profileSet = profile != null ? TryGetProfileSet(profile) : null;

                bool rolledBack = TryDelete(featObj);

                // 特征删完再删草图:ProfileSet 不在 Features 集合里,不清理就会
                // 在文档里留下一堆草图垃圾(还会显示在图形区)。
                bool sketchCleaned = profileSet != null && TryDelete(profileSet);

                // P2 多轮廓(loft/sweep):其余截面的草图一并清理,回滚不留垃圾。
                if (extraProfiles != null)
                {
                    foreach (var ep in extraProfiles)
                    {
                        object eps = TryGetProfileSet(ep);
                        if (eps != null) TryDelete(eps);
                    }
                }

                string reason = status.Value == StatusZombie ? "几何未生成(僵尸特征)" : "特征状态异常";
                string diagnosis = (op == "cut" || (kindOverride != null && kindOverride.Contains("Cutout")))
                    ? "除料没切到实体:检查 plane 是否选对、side 方向是否朝实体内部、草图是否落在毛坯范围内。"
                    : "草图没长出实体:检查轮廓是否闭合、是否落在已有实体上、side/depth 方向是否正确。";

                return new
                {
                    op = op,
                    name = name,
                    status = "error",
                    feature = featureName,
                    featureStatus = status.Value,
                    rolledBack = rolledBack,
                    sketchCleaned = sketchCleaned,
                    resolved = resolved,
                    message = reason + ":Status=" + status.Value + "(正常应为 " + StatusOk + ");" +
                              (rolledBack ? "已自动删除该特征" : "自动删除失败,请手动删除该僵尸特征") +
                              (profile == null ? "。" : (sketchCleaned ? ",并清理了它的草图。" : ",但草图清理失败。")),
                    diagnosis = diagnosis,
                    fix = new { action = "fix_and_retry", check = new[] { "plane", "side", "形状位置", "depth" } }
                };
            }

            string kind = kindOverride
                ?? (op == "cut" ? "ExtrudedCutout"
                : (op == "revolve" ? "RevolvedProtrusion" : "ExtrudedProtrusion"));
            string handleId = context.AddHandle(featObj, kind, featureName);

            // ★ 轮廓约束状态(2026-09-14 新增):把"草图到底锁没锁住"从隐式变成显式。
            //   欠约束不算错误(几何已按给定坐标成形),但它意味着 SE 语义上没锁死 ——
            //   改一个尺寸、或局部面索引漂移时,几何可能整片走位,而调用方从返回值看不出任何征兆。
            bool? underDefined = TryUnderDefined(profile);
            List<string> warningsOut = specWarnings;
            if (underDefined == true)
            {
                warningsOut = (specWarnings == null) ? new List<string>() : new List<string>(specWarnings);
                warningsOut.Add("轮廓仍欠约束(ProfileSet.IsUnderDefined=True):几何已按坐标成形,但 SE 语义上未完全锁死," +
                                "后续改尺寸或局部面索引漂移时可能整片移位。要完全约束请用 autoconstraint+fixorigin,或补 dims/约束。");
            }

            return new
            {
                op = op,
                name = name,
                status = "ok",
                feature = featureName,
                featureStatus = status,
                faces = FacesCount(featObj),
                rangebox = TryRangeBox(featObj),
                resolved = resolved,
                constraint = (profile == null) ? null : new
                {
                    underDefined = underDefined,   // true=欠约束 / false=完全约束 / null=读不到(不猜)
                    relations = SafeCount(profile, "Relations2d"),
                    dimensions = SafeCount(profile, "Dimensions")
                },
                handle = handleId,
                verified = true,
                warnings = (warningsOut != null && warningsOut.Count > 0) ? warningsOut : null,
                hint = "已内置校验(仅 Status=" + StatusOk + " 才返回 ok)。resolved = 本次【实际生效】的方向/参数" +
                       "(省略的参数由工具自愈选定);要固定行为请显式传 side / profileside。" +
                       "constraint.underDefined = 轮廓是否完全约束(为 true 时见 warnings)。"
            };
        }

        /// <summary>删除刚建出来的特征(回滚用)。失败只返回 false 不抛——别让回滚失败盖掉真正的错误。</summary>
        private static bool TryDelete(object comObj)
        {
            try
            {
                Call(comObj, "Delete", null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 删掉草图所属的 ProfileSet(Profile → Profiles → ProfileSet)。
        ///
        /// 为什么必须单独做这件事:ProfileSet 【不在】 Features 集合里,所以删特征
        /// (不管是手动删还是回滚删)都不会连带删草图。不清理,文档里就会越堆越多
        /// 草图垃圾——它们还会显示在图形区,看起来就像"草图没隐藏"。
        /// </summary>
        private static object TryGetProfileSet(object profile)
        {
            try
            {
                // 实测(2026-09-09):Profile.Parent 直接就是 ProfileSet,不是 Profiles 集合。
                // 早先按"Profile→Profiles→ProfileSet"取了两层,第二层其实是文档级的
                // ProfileSets 集合——删它会失败(真删成功就会把所有草图一锅端)。
                object parent = Get(profile, "Parent");
                if (parent == null) return null;

                // 保险:若拿到的确实不是 ProfileSet(没有 Profiles 属性),再上溯一层。
                if (!HasMember(parent, "Profiles"))
                {
                    object upper = Get(parent, "Parent");
                    if (upper == null) return null;
                    parent = upper;
                }

                return parent;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>判断对象上是否有某个可读成员(用来区分 ProfileSet 与 Profiles 集合)。</summary>
        private static bool HasMember(object comObj, string name)
        {
            try
            {
                return Get(comObj, name) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读结果对象里的 status 字段。结果都是匿名对象,这里反射取一下即可——
        /// 省得为了判成败把每个 op 的返回类型都改一遍。
        /// </summary>
        private static bool IsOk(object result)
        {
            try
            {
                var p = result.GetType().GetProperty("status");
                return Equals(p?.GetValue(result) as string, "ok");
            }
            catch
            {
                return true;    // 取不到就不中止,保守放行
            }
        }

        /// <summary>
        /// 按特征描述建轮廓:给了 circle 就走圆轮廓(真圆柱/真圆孔),
        /// 否则走直线环(rect / polygon / loops)。extrude 与 cut 共用此入口。
        /// </summary>
        private static object CreateProfileForFeature(SolidEdgeContext context, object doc, object plane, FeatureSpec spec, bool visible, List<string> specWarnings)
        {
            // 2026-10-03:face 锚定 + coords 缺省/"global" 时,轮廓声明的是【全局世界坐标】,
            // 先投影成贴面平面的局部 (u,v),再走原有建轮廓路径。
            // 2026-10-04:coords:"local" 时轮廓本来就是贴面平面的局部 u/v,不走投影分支,
            // 直接落下面的 circle/circles/slot/Multi 分派(与普通 RefPlane 草图同构,支持斜面)。
            if (spec.FacePlaneRef != null && !spec.CoordsLocal)
                return CreateProfileForFace(context, doc, plane, spec, visible, specWarnings);

            if (spec.HasCircle)
            {
                WarnIfDeclarationsIgnored(spec, specWarnings, "circle");
                return CreateProfileCircle(doc, plane, spec.CircleX, spec.CircleY, spec.CircleR, visible);
            }

            if (spec.HasCircles)
            {
                WarnIfDeclarationsIgnored(spec, specWarnings, "circles");
                return CreateProfileCircles(doc, plane, spec.Circles, visible);
            }

            if (spec.HasSlot)
            {
                WarnIfDeclarationsIgnored(spec, specWarnings, "slot");
                return CreateProfileSlot(doc, plane, spec, visible);
            }

            // 形状解析失败时在这里抛(与改造前 ParseLoops 抛异常的时机一致),
            // 由 se_model_build 的 per-feature try/catch 转成 status=error。
            if (spec.ShapeError != null)
                throw new ArgumentException(spec.ShapeError);

            return CreateProfileMulti(context, doc, plane, spec, spec.Loops, visible, specWarnings);
        }

        /// <summary>
        /// face 平面的轮廓(2026-10-03 IR 面锚定):声明的坐标是【全局世界坐标】,先投影成贴面参考平面的
        /// 局部 (u,v),再复用原建轮廓路径。
        /// 世界 2D 语义:外法向轴 Z→(X,Y)、X→(Y,Z)、Y→(X,Z);第三坐标取 0。
        /// slot 不支持(其 angle 在全局坐标下绕哪个轴未定,coords:"local" 时由 CreateProfileForFeature 分流走普通路径);
        /// 非轴向面(斜面)由 ResolveFacePlane 建平面前拒绝并指路 coords:"local"。
        ///
        /// ★ 2026-10-03 实测修正:投影基必须在本特征【自己的 profile】上求。早先版本为了"不污染"而
        ///   临时建一个 ProfileSet 求完基再删掉,结果那张贴面 RefPlane 的 RCW 会变 CO_E_OBJNOTCONNECTED,
        ///   随后的 Profiles.Add(plane) 报 0x800401FD、回滚删平面也一起失败。现改为一次建好 profile,
        ///   在同一对象上完成"求基 → 画轮廓 → End"。
        /// </summary>
        private static object CreateProfileForFace(SolidEdgeContext context, object doc, object plane, FeatureSpec spec, bool visible, List<string> specWarnings)
        {
            if (spec.OpLower != "extrude" && spec.OpLower != "cut" && spec.OpLower != "hole")
                throw new ArgumentException("face 平面本轮只支持 extrude / cut / hole(当前 op=" + spec.OpLower + ")。");

            if (spec.ShapeError != null)
                throw new ArgumentException(spec.ShapeError);

            if (spec.HasSlot)
                throw new ArgumentException("face 平面 global 模式不支持 slot(腰孔):其 angle 在全局坐标下绕哪个轴没有定义。" +
                                            "请改用 \"coords\":\"local\"(坐标为该面局部 u/v,slot 可用)或 polygon/loops。");

            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profile = Call(Get(profileSet, "Profiles"), "Add", new object[] { plane });

            var map = GeometryTools.FacePlaneMap.BuildFromProfile(profile);
            if (specWarnings != null)
            {
                specWarnings.Add("face 平面:外法向轴 = " + map.NormalAxis + ",轮廓 2D 坐标按全局 (" +
                                 (map.NormalAxis == "Z" ? "X,Y" : (map.NormalAxis == "X" ? "Y,Z" : "X,Z")) + ") 解释。");
            }

            if (spec.HasCircle)
            {
                WarnIfDeclarationsIgnored(spec, specWarnings, "circle");
                double[] c = map.Map(spec.CircleX, spec.CircleY);
                Call(Get(profile, "Circles2d"), "AddByCenterRadius", new object[] { c[0], c[1], spec.CircleR });
            }
            else if (spec.HasCircles)
            {
                WarnIfDeclarationsIgnored(spec, specWarnings, "circles");
                object circles2d = Get(profile, "Circles2d");
                foreach (var cc in spec.Circles)
                {
                    double[] c = map.Map(cc[0], cc[1]);
                    Call(circles2d, "AddByCenterRadius", new object[] { c[0], c[1], cc[2] });
                }
            }
            else
            {
                // 直线环:逐点投影 + 绕向归一(面局部坐标基可能是镜像,不归一会出现反绕向轮廓)。
                // 不改 spec.Loops 本体 —— cut 的方向自愈会对同一 spec 反复建轮廓,改本体等于重复投影。
                var loops = new List<double[][]>(spec.Loops.Count);
                foreach (var pts in spec.Loops)
                {
                    var mappedPts = new double[pts.Length][];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        double[] uv = map.Map(pts[i][0], pts[i][1]);
                        mappedPts[i] = new[] { uv[0], uv[1] };
                    }
                    if (GeoUtil.SignedArea(mappedPts) < 0) Array.Reverse(mappedPts);
                    loops.Add(mappedPts);
                }
                FillProfileWithLines(context, doc, profile, spec, loops, specWarnings);
            }

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 往【已建好的 profile】里画多个闭合直线环(逐线首尾相连 + 端点重合约束)并应用声明式约束/标注。
        /// 由 <see cref="CreateProfileMulti"/> 与 face 轮廓共用,避免两份实现漂移。
        /// </summary>
        private static void FillProfileWithLines(SolidEdgeContext context, object doc, object profile, FeatureSpec spec,
            List<double[][]> loops, List<string> specWarnings)
        {
            object lines = Get(profile, "Lines2d");
            object relations = Get(profile, "Relations2d");

            var loopLines = new List<(object line, double[] p0, double[] p1)>();
            foreach (var pts in loops)
            {
                int n = pts.Length;
                var lineObjs = new object[n];
                for (int i = 0; i < n; i++)
                {
                    var p0 = pts[i];
                    var p1 = pts[(i + 1) % n];
                    lineObjs[i] = Call(lines, "AddBy2Points", new object[] { p0[0], p0[1], p1[0], p1[1] });
                    loopLines.Add((lineObjs[i], p0, p1));
                }
                for (int i = 0; i < n; i++)
                {
                    Call(relations, "AddKeypoint", new object[] { lineObjs[i], 1, lineObjs[(i + 1) % n], 0 });
                }
            }

            ApplySpecConstraints(context, doc, profile, spec, loopLines, specWarnings);
        }

        /// <summary>
        /// 圆 / circles / slot 轮廓走不到 <see cref="ApplySpecConstraints"/>(那是直线环专属路径),
        /// 声明的 autoConstraint / fixOrigin / dims 会被静默忽略——这里补一条 warning。
        /// 与静态校验的 W406 对应:那条报在灌进 SE 之前,这条报在真建的时候(兜底,防止绕过校验)。
        /// </summary>
        private static void WarnIfDeclarationsIgnored(FeatureSpec spec, List<string> warnings, string shape)
        {
            if (warnings == null || spec == null) return;

            var names = new List<string>();
            if (spec.AutoConstraint == true) names.Add("autoConstraint");
            if (spec.FixOrigin == true) names.Add("fixOrigin");
            if (spec.Dims != null && spec.Dims.Count > 0) names.Add("dims(" + spec.Dims.Count + " 条)");
            if (names.Count == 0) return;

            warnings.Add("shape=" + shape + " 走不到约束/标注应用路径(仅 rect/polygon/loops 直线环支持)," +
                "已忽略 " + string.Join("、", names) + "——该轮廓不会被尺寸驱动,变量也不会进变量表。");
        }

        /// <summary>
        /// 建【真圆】截面 + 独立旋转轴(revolve/helix 专用),返回 [Profile, RefAxis]。
        /// 与 CreateProfileRevolve 同构,唯一区别是截面用 Circles2d.AddByCenterRadius(真圆)
        /// 而非 Lines2d 直线环——CreateProfileRevolve 只画直线环,对 circle 声明生成的截面为空
        /// (Loops 空),AddFinite 直接 E_FAIL。圆是天然闭合曲线,无需端点重合约束。
        /// 轴与 CreateProfileRevolve 相同:独立构造线,End 之前 SetAxisOfRevolution,RefAxis 原样交给 AddFinite。
        /// </summary>
        private static object[] CreateProfileRevolveCircle(object doc, object plane, double cx, double cy, double r, FeatureSpec spec, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            // 1) 截面:真圆(单闭合环,天然闭合无约束需求)
            object circles = Get(profile, "Circles2d");
            Call(circles, "AddByCenterRadius", new object[] { cx, cy, r });

            // 2) 旋转轴:独立构造线,不参与截面闭环约束(同 CreateProfileRevolve)
            object lines = Get(profile, "Lines2d");
            object axisLine = Call(lines, "AddBy2Points",
                new object[] { spec.AxisP1[0], spec.AxisP1[1], spec.AxisP2[0], spec.AxisP2[1] });
            object refAxis = Call(profile, "SetAxisOfRevolution", new object[] { axisLine });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);

            return new object[] { profile, refAxis };
        }

        /// <summary>
        /// 建圆形内嵌轮廓:ProfileSets.Add → Profiles.Add(plane) → Circles2d.AddByCenterRadius → End(0) → 隐藏。
        /// 圆是天然闭合曲线,【不需要】端点重合约束(直线轮廓才必须加)。
        /// 圆轮廓直接拉伸 = 真圆柱(1 侧面 + 顶 + 底 = 3 面),不必走旋转。
        /// </summary>
        private static object CreateProfileCircle(object doc, object plane, double cx, double cy, double r, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object circles = Get(profile, "Circles2d");
            Call(circles, "AddByCenterRadius", new object[] { cx, cy, r });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建【多个真圆】内嵌轮廓(同一 profile 的多个闭合环):Circles2d.AddByCenterRadius ×N → End(0) → 隐藏。
        /// 用于一次切/拉多个真圆孔(如法兰螺栓孔阵列),比 loops 多边形近似更准(孔壁是真圆柱面)。
        /// 实测(2026-09-10):一个 profile 放 3 个圆,AddThroughNext 一次切出全部孔(实体面数 = 2 + 外圆 + N)。
        /// </summary>
        private static object CreateProfileCircles(object doc, object plane, List<double[]> circles, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object circles2d = Get(profile, "Circles2d");
            foreach (var c in circles)
                Call(circles2d, "AddByCenterRadius", new object[] { c[0], c[1], c[2] });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建【真圆弧】腰孔(长圆孔)轮廓:两条直线边 + 两端半圆弧 + 4 个端点重合约束 → End(0) → 隐藏。
        ///
        /// 为什么不用 polygon 近似:折线逼近会在两端半圆留下可见的棱(8 段/端就有 12° 的折角),
        /// 而且面数会随段数暴涨(18 段 = 18 个侧面)。真圆弧只有 4 个侧壁面(2 平面 + 2 柱面)。
        ///
        /// ★★ 端点重合约束的索引【按元素类型不同而不同】(2026-09-08 实测,别再凭猜):
        ///   Line2d: 0 = 起点, 1 = 终点
        ///   Arc2d : 0 = 圆心, 1 = 起点, 2 = 终点
        /// 实测方法:两段半圆弧拼整圆,只有索引取对时 Profile.Form 才 = 2(闭合),取错则 = 1。
        /// 圆弧方向:AddByCenterStartEnd 一律【逆时针】从起点走到终点。
        /// </summary>
        private static object CreateProfileSlot(object doc, object plane, FeatureSpec spec, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            double r = spec.SlotWidth / 2.0;
            // 直边半长;length == width 时退化为 0(两端半圆直接相接 = 圆)
            double half = Math.Max((spec.SlotLength - spec.SlotWidth) / 2.0, 0.0);

            double dx = Math.Cos(spec.SlotAngle), dy = Math.Sin(spec.SlotAngle);   // 长轴方向
            double nx = -dy, ny = dx;                                              // 面内法向

            double c1x = spec.SlotX - dx * half, c1y = spec.SlotY - dy * half;     // 左端圆心
            double c2x = spec.SlotX + dx * half, c2y = spec.SlotY + dy * half;     // 右端圆心

            double ptrX = c2x + nx * r, ptrY = c2y + ny * r;   // 右上
            double ptlX = c1x + nx * r, ptlY = c1y + ny * r;   // 左上
            double pblX = c1x - nx * r, pblY = c1y - ny * r;   // 左下
            double pbrX = c2x - nx * r, pbrY = c2y - ny * r;   // 右下

            object lines = Get(profile, "Lines2d");
            object arcs = Get(profile, "Arcs2d");
            object relations = Get(profile, "Relations2d");

            // 逆时针一圈:上直边 → 左半圆 → 下直边 → 右半圆
            object lineTop = Call(lines, "AddBy2Points", new object[] { ptrX, ptrY, ptlX, ptlY });
            object arcLeft = Call(arcs, "AddByCenterStartEnd", new object[] { c1x, c1y, ptlX, ptlY, pblX, pblY });
            object lineBottom = Call(lines, "AddBy2Points", new object[] { pblX, pblY, pbrX, pbrY });
            object arcRight = Call(arcs, "AddByCenterStartEnd", new object[] { c2x, c2y, pbrX, pbrY, ptrX, ptrY });

            // 端点首尾相接:上一个的终点 ↔ 下一个的起点(索引见上面注释)
            Call(relations, "AddKeypoint", new object[] { lineTop, 1, arcLeft, 1 });      // 左上
            Call(relations, "AddKeypoint", new object[] { arcLeft, 2, lineBottom, 0 });   // 左下
            Call(relations, "AddKeypoint", new object[] { lineBottom, 1, arcRight, 1 });  // 右下
            Call(relations, "AddKeypoint", new object[] { arcRight, 2, lineTop, 0 });     // 右上

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建内嵌轮廓(支持多环):ProfileSets.Add → Profiles.Add(plane) → 每环逐线 + 端点闭合 → End(0) → 隐藏。
        /// 多孔(门+窗)必须放同一轮廓的多个闭合环里一次切,否则第 2+ 个 AddThroughNext 会 6311 僵尸。
        /// </summary>
        private static object CreateProfileMulti(SolidEdgeContext context, object doc, object plane, FeatureSpec spec, List<double[][]> loops, bool visible, List<string> specWarnings)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            FillProfileWithLines(context, doc, profile, spec, loops, specWarnings);

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 草图解析已上移到 <see cref="FeatureSpecParser"/>(构建器与校验器共用),
        /// 本文件不再保留私有副本——两份解析必然漂移,校验就失去意义。
        /// </summary>

        /// <summary>
        /// 2026-10-03 IR 面锚定:把 "face:&lt;Face.ID&gt;" / "face:±X/±Y/±Z" 解析成一张【与实体面重合的隐藏参考平面】。
        /// SE2022 没有"直接在实体面上建草图"的 API(Sketches.AddByPlanarFace 抛 0x80004021),唯一通路是
        /// RefPlanes.AddParallelByDistance(face, 0, 1) 建贴面平面再在其上画。
        /// 同一张面被多个特征引用时只建一张(facePlanes 缓存,避免模型树堆一串无名 RefPlane)。
        /// localCoords(2026-10-04):coords:"local" 时轮廓按贴面平面局部 u/v 画——只要求【平面面】
        /// (斜面也行);global 模式则要求【轴向面】(全局 2D 坐标在斜面无定义)。
        /// </summary>
        private static object ResolveFacePlane(object doc, string planeRef, Dictionary<int, object> facePlanes, bool localCoords = false)
        {
            var fr = FeatureSpecParser.ParseFacePlaneRef(planeRef);
            if (fr == null || fr.ParseError != null)
                throw new ArgumentException("plane \"" + planeRef + "\" 无法解析:" + (fr == null ? "格式错" : fr.ParseError) +
                                            "(应为 face:<Face.ID> 或 face:±X/±Y/±Z)。");

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                throw new ArgumentException("face 锚定需要已有实体:当前模型还没有实体,没有面可引用。" +
                                            "第一个特征请用 RefPlane_1/2/3,建出实体后再用 face: 在面上加特征。");
            object model = Get(models, "Item", 1);

            object face;
            int faceId;
            if (fr.Kind == "Id")
            {
                int total;
                var ids = new List<int>();
                face = FindFaceById(model, fr.FaceId, out total, ids);
                if (face == null)
                    throw new ArgumentException("找不到 Face.ID=" + fr.FaceId + " 的面(当前实体共 " + total + " 个面:" +
                                                string.Join(",", ids) + ")。" +
                                                "几何变更后 Face.ID 会变,先用 se_read_geometry 或 se_describe_object 重新确认。");
                faceId = fr.FaceId;
            }
            else
            {
                face = SelectFaceByAxis(doc, model, fr.Axis, out faceId);
            }

            // 2026-10-03 实测:斜面若拖到建平面之后才拒绝,回滚删平面会失败(RefPlane.Delete 抛 E_INVALIDARG),
            // 模型树残留一张无名隐藏平面。故在【建平面之前】提前拒绝,按模式分:
            //   local:只要求【平面面】(Geometry is Plane,斜面/带内孔平面都放行;与法向无关);
            //   global:要求【轴向面】(法向不沿全局 X/Y/Z 拒绝)。
            // (轴向选择器分支已在 SelectFaceByAxis 内筛过轴向面,这里对它恒为通过;真正拦的是 face:<ID>。)
            if (localCoords)
            {
                if (!IsPlanarFace(face))
                    throw new ArgumentException(
                        "该实体面不是平面面(圆柱/blend 等曲面无法建贴面草图平面),coords:\"local\" 只支持平面面。" +
                        "可先用 se_read_geometry 确认面类型。");
            }
            else
            {
                double[] faceNormal = TryGetFaceNormal(face);
                double normalMax = (faceNormal == null) ? 0.0
                    : Math.Max(Math.Abs(faceNormal[0]), Math.Max(Math.Abs(faceNormal[1]), Math.Abs(faceNormal[2])));
                if (normalMax < 1 - 1e-6)
                    throw new ArgumentException(
                        "该实体面不是轴向面(外法向不沿全局 X/Y/Z),全局 2D 轮廓坐标在斜面上无定义。" +
                        "请改用 \"coords\":\"local\"(坐标为该面局部 u/v,支持斜面)或脚本。");
            }

            object cached;
            if (facePlanes != null && facePlanes.TryGetValue(faceId, out cached) && cached != null) return cached;

            object refPlanes = Get(doc, "RefPlanes");
            object newPlane = Call(refPlanes, "AddParallelByDistance", new object[] { face, 0.0, 1 });
            SetVisible(newPlane, false);
            if (facePlanes != null) facePlanes[faceId] = newPlane;
            return newPlane;
        }

        /// <summary>
        /// 轴向选择器:在"外法向沿全局 X/Y/Z"的平面面里,按朝 +A / −A 选面。
        /// 法向【轴】由 TryGetFaceNormal(边叉积)定;法向【朝向】由面的锚点相对模型包围盒定
        /// (面在 +A 端 ⇒ 外法向朝 +A)。选不出 / 多解都抛明确错误并列出候选 Face.ID,要求改用 face:&lt;ID&gt;。
        /// </summary>
        private static object SelectFaceByAxis(object doc, object model, string axis, out int faceId)
        {
            faceId = -1;
            char letter = axis[1];
            int sign = axis[0] == '-' ? -1 : 1;
            int ai = letter == 'X' ? 0 : (letter == 'Y' ? 1 : 2);

            double[] box = TryModelRangeBox(doc);
            if (box == null || box.Length < 6)
                throw new ArgumentException("读不到模型包围盒,无法用轴向选择器(" + axis + ")定位面;请改用 face:<Face.ID>。");

            var candidates = new List<object>();
            var candidateIds = new List<int>();
            var others = new List<string>();   // 报错用:模型里全部轴向平面面
            foreach (var f in FindPlaneFaces(model))
            {
                double[] n = TryGetFaceNormal(f);
                if (n == null) continue;

                double ax = Math.Abs(n[0]), ay = Math.Abs(n[1]), az = Math.Abs(n[2]);
                double max = Math.Max(ax, Math.Max(ay, az));
                if (max < 1 - 1e-6) continue;   // 斜面:法向不沿任何全局轴
                int fi = ax == max ? 0 : (ay == max ? 1 : 2);

                double[] anchor = TryGetFaceAnchor(f);
                if (anchor == null) continue;

                double mid = (box[fi] + box[fi + 3]) / 2.0;
                int fs = anchor[fi] >= mid ? 1 : -1;
                int id = SafeInt(Get(f, "ID"));
                others.Add("face:" + id + "(" + "XYZ"[fi] + (fs > 0 ? "+" : "-") + ")");
                if (fi == ai && fs == sign)
                {
                    candidates.Add(f);
                    candidateIds.Add(id);
                }
            }

            if (candidates.Count == 0)
                throw new ArgumentException("模型里没有外法向朝 " + axis + " 的轴向平面面。当前轴向平面面:" +
                                            (others.Count > 0 ? string.Join(",", others) : "(无)") +
                                            "。请改用 face:<Face.ID> 精确指定。");
            if (candidates.Count > 1)
                throw new ArgumentException("外法向朝 " + axis + " 的轴向平面面有 " + candidates.Count +
                                            " 个(Face.ID:" + string.Join(",", candidateIds) + "),选择器无法区分。" +
                                            "请改用 face:<Face.ID> 精确指定。");

            faceId = candidateIds[0];
            return candidates[0];
        }

        /// <summary>取面上的一个锚点(首条边的起点,世界坐标);读不到返回 null。</summary>
        private static double[] TryGetFaceAnchor(object face)
        {
            try
            {
                var f = (SolidEdgeGeometry.Face)face;
                var edges = (SolidEdgeGeometry.Edges)f.Edges;
                if (edges.Count < 1) return null;
                var e = (SolidEdgeGeometry.Edge)edges.Item(1);
                Array sp = Array.CreateInstance(typeof(double), 0);
                Array ep = Array.CreateInstance(typeof(double), 0);
                e.GetEndPoints(ref sp, ref ep);
                if (sp == null || sp.Length < 3) return null;
                return new[]
                {
                    Convert.ToDouble(sp.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToDouble(sp.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToDouble(sp.GetValue(2), CultureInfo.InvariantCulture)
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>2026-10-03:清掉某张 face 平面的缓存条目(特征失败回滚用)。null=成功;否则为提示文案。</summary>
        private static string TryDeleteFacePlane(Dictionary<int, object> facePlanes, int faceId)
        {
            object plane;
            if (facePlanes == null || !facePlanes.TryGetValue(faceId, out plane)) return null;
            facePlanes.Remove(faceId);
            if (plane == null) return null;
            try
            {
                Exception err;
                if (ManualInvoke.TryInvoke(plane, "Delete", Array.Empty<object>(), out _, out err)) return null;
                return "回滚 face 平面 face:" + faceId + " 失败:" + (err == null ? "未知错误" : err.Message) +
                       "(模型树可能残留一张无名隐藏参考平面,可手动删除,不影响后续建模)。";
            }
            catch (Exception ex)
            {
                return "回滚 face 平面 face:" + faceId + " 异常:" + ex.Message + "(可能残留一张无名隐藏参考平面)。";
            }
        }

        private static object ResolvePlane(SolidEdgeContext context, object doc, string planeRef,
            Dictionary<string, object> namedPlanes, Dictionary<int, object> facePlanes = null, bool localCoords = false)
        {
            if (string.IsNullOrWhiteSpace(planeRef))
                throw new ArgumentException("特征缺少 plane(或 base)平面引用。");

            // 2026-10-03:face 锚定 → 返回一张贴面的隐藏 RefPlane
            // (global 模式的坐标投影在 CreateProfileForFeature 里做;local 模式直接按面局部 u/v 画)
            if (planeRef.StartsWith("face:", StringComparison.OrdinalIgnoreCase))
                return ResolveFacePlane(doc, planeRef, facePlanes, localCoords);

            if (planeRef.StartsWith("@", StringComparison.Ordinal))
            {
                string key = planeRef.Substring(1);
                if (namedPlanes.TryGetValue(key, out var np)) return np;
                throw new ArgumentException("未找到本批内命名平面 \"@" + key + "\"(需先用 op=plane 创建)。");
            }

            if (planeRef.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                var h = context.GetHandle(planeRef);
                if (h == null || h.ComObject == null)
                    throw new ArgumentException("句柄表里找不到平面对象 " + planeRef + "。");
                return h.ComObject;
            }

            // 默认面:按名称解析
            object refPlanes = Get(doc, "RefPlanes");
            int count = Count(refPlanes);

            // "RefPlane_N" / "RefPlaneN" → 按索引 Item(N)(本机中文版 DisplayName 是"参考平面_N",不能用名称匹配)
            if (planeRef.StartsWith("RefPlane", StringComparison.OrdinalIgnoreCase))
            {
                string idxStr = planeRef.Substring("RefPlane".Length).TrimStart('_', ' ', '-');
                if (int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                    && idx >= 1 && idx <= count)
                {
                    return Get(refPlanes, "Item", idx);
                }
            }

            // 其它:按 DisplayName 匹配(含本地化名"参考平面_N")
            for (int i = 1; i <= count; i++)
            {
                object p = Get(refPlanes, "Item", i);
                string dn = SafeString(Get(p, "DisplayName"));
                if (string.Equals(dn, planeRef, StringComparison.OrdinalIgnoreCase))
                    return p;
            }

            throw new ArgumentException("找不到参考面 \"" + planeRef + "\"(支持 RefPlane_1/2/3、@name、obj-K、face:<ID>/face:±Z)。");
        }

        // ---------------- COM 调用(裸 IDispatch,兼容强/弱类型 RCW) ----------------
        // 不用 Type.InvokeMember:app.ActiveDocument 等返回的强类型 RCW(System.Type 被
        // 固定为接口类型如 SolidEdgeFramework.Document)只暴露该接口成员,找不到零件文档
        // 才有的 RefPlanes/Models 等;裸 IDispatch 调用不挑 RCW 类型,与 se_invoke_chain 同路。

        /// <summary>无参属性读取。</summary>
        private static object Get(object obj, string name)
        {
            return InvokeRaw(obj, name, null);
        }

        /// <summary>带参属性(索引器)读取,如 Item(1)。</summary>
        private static object Get(object obj, string name, object arg)
        {
            return InvokeRaw(obj, name, new[] { arg });
        }

        /// <summary>方法调用(含 SAFEARRAY 数组参数,ManualInvoke 按元素类型构造数组)。</summary>
        private static object Call(object obj, string name, object[] args)
        {
            return InvokeRaw(obj, name, args);
        }

        private static object InvokeRaw(object obj, string name, object[] args)
        {
            if (ManualInvoke.TryInvoke(obj, name, args ?? Array.Empty<object>(), out var r, out var err))
                return r;
            throw err ?? new Exception("IDispatch 调用失败: " + name);
        }

        private static int Count(object coll)
        {
            try { return Convert.ToInt32(Get(coll, "Count")); }
            catch { return 0; }
        }

        /// <summary>
        /// 写属性(属性名 + 值)。建模 IR 的 autoConstraint/dims 需要(Constraint 等)。
        ///
        /// ★ 2026-09-14 修正:原实现走 `obj.GetType().InvokeMember(..., SetProperty, ...)` 反射。
        ///   对 COM RCW,`GetType()` 拿到的是 `__ComObject` 而不是真实接口类型,反射写属性
        ///   不可靠(本文件其它调用早已统一走裸 IDispatch,只有这里漏了)。改走
        ///   `ManualInvoke.TryInvokeSet`(DISPATCH_PROPERTYPUT),与 `Get`/`Call` 同一条路。
        /// </summary>
        private static void Put(object obj, string name, object value)
        {
            if (ManualInvoke.TryInvokeSet(obj, name, value, out var err)) return;
            throw err ?? new Exception("IDispatch 写属性失败: " + name);
        }

        /// <summary>
        /// 轮廓 End 之前应用声明式约束/标注(必须在开放的轮廓编辑上下文内,这是绑定生效的唯一窗口):
        ///   ①autoConstraint:按坐标推断 H/V(|dy|&lt;eps → 水平,|dx|&lt;eps → 垂直);
        ///   ②fixOrigin:首环首线起点 AddKeypointFix(消除整体平移自由度);
        ///   ③dims:AddLength(线)→ Constraint=true(驱动化,缺了 Edit 静默无效,2026-09-13 实测)
        ///     → PutName 改名进变量表 → Edit 值/公式。
        /// 单项失败只记 warning 不中断(几何已成型,绑定失败可事后补);硬 COM 异常向上抛。
        /// </summary>
        private static void ApplySpecConstraints(SolidEdgeContext context, object doc, object profile,
            FeatureSpec spec, List<(object line, double[] p0, double[] p1)> loopLines, List<string> warnings)
        {
            object relations = Get(profile, "Relations2d");

            if (spec.AutoConstraint == true)
            {
                for (int i = 0; i < loopLines.Count; i++)
                {
                    var (line, p0, p1) = loopLines[i];
                    try
                    {
                        if (Math.Abs(p0[1] - p1[1]) <= 1e-9)
                            Call(relations, "AddHorizontal", new object[] { line });
                        else if (Math.Abs(p0[0] - p1[0]) <= 1e-9)
                            Call(relations, "AddVertical", new object[] { line });
                    }
                    catch (Exception ex) { warnings.Add("autoConstraint[" + i + "]: " + ex.Message); }
                }
            }

            if (spec.FixOrigin == true && loopLines.Count > 0)
            {
                try { Call(relations, "AddKeypointFix", new object[] { loopLines[0].line, 0 }); }
                catch (Exception ex) { warnings.Add("fixOrigin: " + ex.Message); }
            }

            if (spec.Dims == null || spec.Dims.Count == 0) return;

            object dimsCol = Get(profile, "Dimensions");
            object varsObj = Get(doc, "Variables");
            foreach (var ds in spec.Dims)
            {
                if (ds.ParseError != null) { warnings.Add("dims[" + (ds.Name ?? "?") + "]: " + ds.ParseError); continue; }
                if (ds.Element < 0 || ds.Element >= loopLines.Count)
                {
                    warnings.Add("dims[" + ds.Name + "]: element=" + ds.Element + " 越界(共 " + loopLines.Count + " 条线)");
                    continue;
                }
                try
                {
                    object dim = Call(dimsCol, "AddLength", new object[] { loopLines[ds.Element].line });
                    Put(dim, "Constraint", true);

                    // ★ 2026-09-14:PutName / Edit 改走 PIA 强类型(官方示例同款)。
                    //   原实现走裸 IDispatch,实测报 DISP_E_EXCEPTION(hr=0x80020009, puArgErr=0
                    //   = 第 1 个参数) —— 尺寸名与 value 都写不进去(驱动化本身不受影响)。
                    //   官方 WorkingWithDimensions.html 的示例是这个顺序:
                    //     objVariables.PutName(objDimension1, "Dimension1")   ' 给尺寸命名
                    //     sName = objVariables.GetName(objDimension2)         ' 取【实际名】
                    //     objVariables.Edit(sName, "Dimension1/2.0")          ' 用实际名改公式
                    //   → 这里照样办:Edit 一律用 GetName 回读到的名字,而不是自己传进去的名字。
                    //     这样即使 PutName 失败(拿到的是系统名)也照样能写值,两个动作解耦,
                    //     不会"命名失败"连带把"写值"一起拖坏。
                    var varsTyped = (SolidEdgeFramework.Variables)varsObj;

                    try { varsTyped.PutName(dim, ds.Name); }
                    catch (Exception ex) { warnings.Add("dims[" + ds.Name + "].PutName: " + ex.Message); }

                    string varName = ds.Name;
                    try { varName = varsTyped.GetName(dim); } catch { /* 读不到就退回传入名 */ }

                    string formula = ds.Value ?? ds.Formula;
                    if (!string.IsNullOrEmpty(formula))
                        varsTyped.Edit(varName, formula);
                }
                catch (Exception ex) { warnings.Add("dims[" + ds.Name + "]: " + ex.Message); }
            }
        }

        private static void SetVisible(object comObj, bool value)
        {
            try
            {
                ((dynamic)comObj).Visible = value;
            }
            catch
            {
                try
                {
                    comObj.GetType().InvokeMember("Visible", BindingFlags.SetProperty, null, comObj,
                        new object[] { value }, null, CultureInfo.InvariantCulture, null);
                }
                catch { /* 忽略:隐藏失败只影响显示,不影响几何 */ }
            }
        }

        private static int FacesCount(object feat)
        {
            try
            {
                object faces = Get(feat, "Faces", 1);
                if (faces == null) return -1;
                return Convert.ToInt32(Get(faces, "Count"));
            }
            catch { return -1; }
        }

        // ---------------- JSON 解析 ----------------
        // 全部上移到 FeatureSpecParser:构建器与校验器必须共用同一份取数语义,
        // 本文件不再保留私有副本。

        private static string SafeString(object v)
        {
            try { return v?.ToString(); } catch { return null; }
        }

        private static long? SafeLong(object v)
        {
            try
            {
                if (v == null) return null;
                return Convert.ToInt64(v);
            }
            catch { return null; }
        }

        private static int SafeInt(object v)
        {
            try { return Convert.ToInt32(v); }
            catch { return -1; }
        }

        private static string DescribeException(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            Exception e = ex;
            int depth = 0;
            while (e != null && depth < 3)
            {
                if (depth > 0) sb.Append(" <- 内部: ");
                sb.Append(e.GetType().Name);
                if (!string.IsNullOrEmpty(e.Message)) sb.Append(": ").Append(e.Message);
                if (e.HResult != 0) sb.Append(" (HRESULT=0x").Append(e.HResult.ToString("X8")).Append(')');
                e = e.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        private static string Error(string message)
        {
            return JsonSerializer.Serialize(new { status = "error", message = message });
        }

        /// <summary>反射读文档 Name;读不动(半加载/异常)返回 null,守卫按"不符"处理,宁可拒绝不误建。</summary>
        private static string TryGetDocName(object doc)
        {
            try
            {
                return doc?.GetType().InvokeMember("Name",
                    System.Reflection.BindingFlags.GetProperty, null, doc, null) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
