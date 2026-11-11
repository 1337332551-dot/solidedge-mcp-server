using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
    ///   pattern 圆周阵列(2026-10-23 开放;CircularPatterns2d.AddByCircle + Patterns.Add,真机复刻验证):
    ///           of=种子特征 / plane=载体平面(法向=旋转轴) / count=实例总数(含种子) /
    ///           center=圆心 / spacing="fit"(默认,整圈均布)|spacingDeg(固定角间距) / clockwise / patterntype
    ///   pattern kind:"rect" 矩形阵列(2026-11-08 开放;RectangularPatterns2d.Add(…,Type.Missing×4) + Patterns.Add,
    ///           规格=reports/op-design_rect-pattern_20261108.md,链路三件套真机实测):
    ///           of=种子特征 / plane=载体平面(保守用种子所在平面) / xcount,ycount(>=1,=1 即单行/列) /
    ///           xspacing,yspacing(米) / center=[u,v] 阵列角点=种子位 / suppress=[occ 序号] 单项抑制(列主序)
    ///
    /// P3 面引用机制(P2 之后):前序特征带 name 登记为 @别名,供 faceOf 引用其产出面;
    ///   面选择:faceOf(@别名/obj-K,仅做存在性校验) + faceNormal([nx,ny,nz] 法向命中) 或 faceIndex(全量面列表 0-based 序号)。
    ///   draft      拔模(Drafts.Add):faceOf 目标面 + angle(弧度,默认 π/36) + side(只认 4=igInside/5=igOutside);
    ///             refPlane 必须与目标面相交(枢轴语义),否则 6311
    ///   split      分割(Splits.Add):target 须 @别名/obj-K;用平面分割实体
    ///   extrude_surface 曲面拉伸(Constructions.ExtrudedSurfaces.AddFinite):轮廓拉成【曲面】非实体;产物面缓存供 thicken
    ///   loft_surface 放样曲面(Constructions.LoftedSurfaces.Add,WantEndCaps=false):曲面放样,同样缓存产物面供 thicken
    ///   thicken    曲面加厚(Model.Thickens.Add;空文档首个设计体走 Models.AddThickenFeature):Side 用 igLeft/igRight/igSymmetric(1/2/3)
    ///   web_network 腹板网(WebNetworks.Add):闭合轮廓+thickness+depth;ExtentType/ProfileExtensionType 用专属枚举
    ///             (seWebExtendFinite+seWebProfileNoExtend,勿混用通用 FeaturePropertyConstants)
    ///   delete_face 删面(DeleteBlends.Add 删 blend/round + DeleteFaces.Add 兜底):需 confirm:true;删圆角面用 faceNormal 命中
    ///   spline3d 3D 样条 / arc3d 3D 圆弧(2026-11-02):doc.Sketches3D 里加曲线,不进特征树;
    ///             spline3d 走 BSplineCurves3D.AddByPoints(ref double[] → VT_BYREF|VT_ARRAY),arc3d 走
    ///             Arcs3D.AddByStartEndCenterNormal(13 个 double,半径按几何算)
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
        /// <summary>特征 Status 正常值(igFeatureOK)。官方枚举共 5 档,勿再按"不等于它就算坏"处理。</summary>
        private const long StatusOk = 1216476310;

        /// <summary>Status=1216476311(igFeatureFailed):COM 调用"成功"但几何没生成——僵尸特征。唯一该删除的一档。</summary>
        private const long StatusZombie = 1216476311;

        /// <summary>Status=1216476312(igFeatureWarned):特征【已生成】,只是带警告——保留,勿删。</summary>
        private const long StatusWarned = 1216476312;

        /// <summary>Status=1216476313(igFeatureSuppressed):特征被用户主动压缩(Suppress)——保留,禁止删除。</summary>
        private const long StatusSuppressed = 1216476313;

        /// <summary>Status=1216476314(igFeatureRolledBack):特征因位于失败特征之后被回滚——保留。</summary>
        private const long StatusRolledBack = 1216476314;

        /// <summary>
        /// 特征健康度。取值对应 Solid Edge 官方 FeatureStatusConstants 五档 + Unknown。
        /// 事实来源:Solid Edge 2022 自带 Program\interop.SolidEdgeConstantLib.dll 反编译实测
        /// (ilspycmd,文件 SolidEdge.Constant.Interop\FeatureStatusConstants.cs,2026-10-14 核实)。
        /// 历史教训:旧判定按"status != StatusOk 即判失败并自动删除",把 Warned/Suppressed/RolledBack
        /// 一并误杀——带良性警告的好特征被删后重试,可能永远建不成。现全部判定改走 ClassifyFeatureStatus。
        /// </summary>
        internal enum FeatureHealth { Ok, Zombie, Warned, Suppressed, RolledBack, Unknown }

        /// <summary>
        /// Status → 健康档。null(读不到)归 Unknown:【不删、不猜】,如实上报。
        /// 只有 Zombie 允许触发删除回滚。
        /// </summary>
        internal static FeatureHealth ClassifyFeatureStatus(long? status)
        {
            switch (status.GetValueOrDefault(long.MinValue))
            {
                case StatusOk: return FeatureHealth.Ok;
                case StatusZombie: return FeatureHealth.Zombie;
                case StatusWarned: return FeatureHealth.Warned;
                case StatusSuppressed: return FeatureHealth.Suppressed;
                case StatusRolledBack: return FeatureHealth.RolledBack;
                default: return FeatureHealth.Unknown;
            }
        }

        /// <summary>官方枚举名——报给调用方,便于对源码/文档自查(如 igFeatureWarned)。</summary>
        internal static string HealthEnumName(FeatureHealth h)
        {
            switch (h)
            {
                case FeatureHealth.Ok: return "igFeatureOK";
                case FeatureHealth.Zombie: return "igFeatureFailed";
                case FeatureHealth.Warned: return "igFeatureWarned";
                case FeatureHealth.Suppressed: return "igFeatureSuppressed";
                case FeatureHealth.RolledBack: return "igFeatureRolledBack";
                default: return "unknown";
            }
        }

        /// <summary>健康档的人话解释。</summary>
        internal static string HealthMeaning(FeatureHealth h)
        {
            switch (h)
            {
                case FeatureHealth.Ok: return "特征正常";
                case FeatureHealth.Zombie: return "几何未生成(僵尸特征)";
                case FeatureHealth.Warned: return "特征已生成但有警告,几何可用";
                case FeatureHealth.Suppressed: return "特征被主动压缩(Suppress),当前不参与建模";
                case FeatureHealth.RolledBack: return "特征位于失败特征之后被回滚,尚未生效";
                default: return "未知状态码,含义未核实";
            }
        }

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
        ///   fixorigin:      true 时以辅助线锚定首环顶点消平移自由度(2026-10-25 起:轮廓线禁固定,
        ///                   固定只落在原点→顶点的构造辅助线上,与用户 UI 画法同规矩)
        ///   dims:           [{"element":0,"name":"Len1","value":"40 mm"} 或 {"formula":"Rad1 - 5 mm"}]
        ///                   element 是【跨环扁平的 0-based 线索引】(第 0 环的线排最前;轴不占位),
        ///                   name 会进变量表(标注即变量),formula 可引用本批已建变量做关联式。
        ///                   必须在 Profile.End 之前应用(构建器已内置此时序)。
        /// 返回每个特征的名称/Status(1216476310=正常,1216476311=几何未生成,1216476312=带警告)/featureHealth/面数/句柄。
        /// 坐标单位为米。
        /// </summary>
        [McpServerTool, Description("声明式建模:一次建多个特征。\n" +
            "op:plane/extrude/cut/revolve/hole/polarhole/boltcircle/pattern/loft/sweep/helix/fillet/chamfer/" +
            "rib/draft/split/web_network/extrude_surface/loft_surface/thicken/delete_face/spline3d/arc3d/bluesurf。\n" +
            "硬规则:米;形状键平铺在顶层(circle|circles|slot|rect|polygon|loops);" +
            "已有实体上只除料/打孔要 assumeStock=true;有 error 直接拒绝;" +
            "status=ok 只是特征在,健康度看 featureHealth。\n" +
            "静态校验=dryRun:true(不启动 COM,SE 未启动也能跑;旧名 se_validate_features 自动路由到这里)。\n" +
            "★ op 字段表首调时注入 opDocs;也可主动取 {\"op\":\"?\"}(索引)/" +
            "{\"op\":\"?\",\"name\":\"sweep\"}(单条)/{\"op\":\"?\",\"topic\":\"common\"}(通用规则)。")]
        public static string se_model_build(
            SolidEdgeContext context,
            [Description("特征列表(JSON 数组),每项见工具描述。与 featuresFile 二选一")] JsonElement[] features = null,
            [Description("起始对象句柄(零件文档),可省略;省略时用当前活动文档")] string objectId = null,
            [Description("期望的文档名(可选守卫):不符则立即拒绝、一个特征都不建,防误建。强烈建议每次都传")] string expectDocument = null,
            [Description("true=已有实体上增量建模(只除料/打孔时必须传,否则 E401 拦下)")] bool assumeStock = false,
            [Description("true=只静态校验并返回报告,不建特征(不启动 COM)")] bool dryRun = false,
            [Description("true=全部建完后统一 Recompute;默认 false(建特征时已自动重算)")] bool recomputeAfter = false,
            [Description("true=建前/建后各捕获一次拓扑并返回 topologyDiff{facesBefore,facesAfter,added,removed,changed,volumeDeltaMm3,identical};" +
			"默认 false(两次全量读面,大模型加秒级耗时)")] bool topologyDiff = false,
            [Description("features JSON 文件路径(数组根或 {\"features\":[...]} 包装,建议绝对路径)。与 features 二选一")] string featuresFile = null,
            [Description("参数表:{名字:数字|表达式},features 里用 {{名字}} 引用(可互相引用,自动定序);也可以是行数组(表驱动,每行建一段)")] JsonElement? sets = null,
            [Description("true=只回段摘要/体积,不含逐特征明细(省 token)")] bool slim = false,
            [Description("本次最多建几段(配合 cursor 续批,防 60 秒超时)")] int limit = 0,
            [Description("从第几段开始(上次的 nextCursor)")] int cursor = 0)
        {
            try
            {
                // P0 文件入口(2026-10-27):features 与 featuresFile 二选一,先合流成同一个数组,
                // 之后的每个出口(手册查询/校验/dry-run/构建)只认 features 一个来源。
                string fileError;
                features = ResolveFeatures(features, featuresFile, out fileError);
                if (fileError != null) return Error(fileError);

                // 手册查询:{"op":"?"} / {"op":"help"}。纯字符串返回,不碰 COM,
                // 必须放在校验之前拦——否则 "?" 会被 OpRule 当成未知 op 报 E101。
                // 也放在 sets 求值之前:查手册不该被参数表里的表达式错误挡住。
                string manualText;
                if (OpManual.TryQuery(features, out manualText))
                    return manualText;

                // P2:先按分段符(doc/measure/compute)切段。段 = 一份文档或一个动作,批内字典不跨段。
                List<BuildSegment> segments;
                string splitError;
                if (!BuildPipeline.SplitSegments(features, out segments, out splitError))
                    return Error(splitError);
                if (segments.Count == 0)
                    return Error("features 不能为空。");

                bool hasDynamicOps = false;
                foreach (var s in segments)
                {
                    if (s.ActionOp != null) { hasDynamicOps = true; break; }
                }

                // P1/P3:参数表求值。
                //   全是静态段时做法与 P1 相同——一次性替换,校验器与构建器看到同一份写死数字。
                //   有 measure/compute 时改为**逐段惰性替换**(段开始前才算),因为后面的段要引用前面的实测值。
                var runtimeValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (!hasDynamicOps)
                {
                    string setsError;
                    if (!ApplySets(features, sets, out features, out setsError))
                        return Error(setsError);
                    // 切段发生在替换之前的 features 上,替换后要重新切一次(占位符可能出现在 doc 字段里)。
                    if (!BuildPipeline.SplitSegments(features, out segments, out splitError))
                        return Error(splitError);
                }
                else
                {
                    if (sets.HasValue && sets.Value.ValueKind == JsonValueKind.Array)
                        return Error("表驱动(sets 行数组)与 measure/compute 不能同时用:每行都有自己的上下文,"
                            + "动作段没有行概念。请拆成两次调用。");

                    string setsError;
                    if (!BuildPipeline.ResolveSets(sets, out runtimeValues, out setsError))
                        return Error(setsError);
                }

                // 逐段静态校验:纯函数、毫秒级、不碰 COM。
                // 段内校验 = 与"这段自己单独建"完全同构(护栏粒度不变);doc op 本身不参与特征校验。
                var segValidations = new List<Dictionary<string, object>>();
                bool anyValidateError = false;
                int validateErrorCount = 0;
                foreach (var seg in segments)
                {
                    if (seg.Action.HasValue)
                    {
                        segValidations.Add(new Dictionary<string, object>
                        {
                            ["segment"] = seg.Name,
                            ["action"] = seg.ActionOp,
                            ["validate"] = null
                        });
                        continue;
                    }

                    var segFeaturesForCheck = seg.Features.ToArray();
                    if (hasDynamicOps)
                    {
                        JsonElement[] substituted;
                        string subError;
                        if (!BuildPipeline.Substitute(segFeaturesForCheck, runtimeValues, out substituted, out subError))
                        {
                            // 诚实跳过:这段依赖前面 measure/compute 的运行时值,静态阶段算不出来。
                            segValidations.Add(new Dictionary<string, object>
                            {
                                ["segment"] = seg.Name,
                                ["validate"] = null,
                                ["note"] = "依赖前段运行时值,静态校验跳过(构建时逐段再校验): " + subError
                            });
                            continue;
                        }
                        segFeaturesForCheck = substituted;
                    }

                    var rep = FeatureValidator.Validate(segFeaturesForCheck, assumeStock);
                    if (rep.HasError) anyValidateError = true;
                    validateErrorCount += rep.errorCount;
                    segValidations.Add(new Dictionary<string, object>
                    {
                        ["segment"] = seg.Name,
                        ["validate"] = rep
                    });
                }

                // 本批涉及、且手册里有的 op(去重)。三个出口都用它做按需注入:
                // 每个 op 的手册在整个 server 进程里只发一次,之后只留一行 opDocsNote 指针。
                var docOps = OpsOf(features);

                if (dryRun)
                {
                    var dryInj = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, docOps);
                    return JsonSerializer.Serialize(new
                    {
                        status = "dry-run",
                        segmentCount = segments.Count,
                        validate = segValidations.Count == 1 ? segValidations[0]["validate"] : null,
                        segments = segValidations.Count > 1 ? segValidations : null,
                        opDocs = dryInj.Count > 0 ? dryInj : null,
                        opDocsNote = OpDocRegistry.Note(docOps, dryInj.Keys)
                    }, FeatureValidator.JsonOpts);
                }

                if (anyValidateError)
                {
                    var errInj = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, docOps);
                    return JsonSerializer.Serialize(new
                    {
                        status = "error",
                        message = "静态校验未通过(" + validateErrorCount + " 个 error),已拒绝执行," +
                                  "以免生成僵尸特征。请按 issues 修正后重试;只想看报告可用 dryRun=true 重新调用。",
                        validate = segValidations.Count == 1 ? segValidations[0]["validate"] : null,
                        segments = segValidations.Count > 1 ? segValidations : null,
                        opDocs = errInj.Count > 0 ? errInj : null,
                        opDocsNote = OpDocRegistry.Note(docOps, errInj.Keys)
                    }, FeatureValidator.JsonOpts);
                }

                // P2 分批:只在段边界切,不切段内部。limit=0 表示不限制(一次建完)。
                // cursor 是无状态下标——续批时把同一份 features/featuresFile 再传一遍即可,
                // 不依赖服务进程内存里的队列(那种设计在 COM/句柄生命周期上很脆)。
                int startSeg = Math.Max(0, cursor);
                int endSeg = limit > 0 ? Math.Min(segments.Count, startSeg + limit) : segments.Count;

                return context.Invoke(() =>
                {
                    // 起手段(没有 doc op 的那段)才用调用方的文档:解析一次、守卫一次。
                    object startDoc = null;
                    bool startDocResolved = false;

                    // P3 求值环境:sets 的值 + 前面 measure/compute 攒出来的值,后面的段都能引用。
                    var env = new Dictionary<string, double>(runtimeValues, StringComparer.OrdinalIgnoreCase);

                    var allResults = new List<object>();
                    var segSummaries = new List<Dictionary<string, object>>();
                    var allWarnings = new List<string>();
                    int builtSegments = 0;
                    bool anyAborted = false;
                    string abortMessage = null;

                    for (int si = startSeg; si < endSeg; si++)
                    {
                        var segment = segments[si];
                        object doc;
                        string savePath = null;
                        bool overwrite = false;

                        if (segment.Doc.HasValue)
                        {
                            // doc op:新建一份文档,本段特征全部建在它里面。
                            string createError;
                            doc = CreateSegmentDocument(context, segment.Doc.Value, out savePath, out overwrite, out createError);
                            if (createError != null)
                            {
                                anyAborted = true;
                                abortMessage = "段 \"" + segment.Name + "\" 建文档失败: " + createError;
                                break;
                            }
                        }
                        else
                        {
                            if (!startDocResolved)
                            {
                                if (!string.IsNullOrEmpty(objectId))
                                {
                                    var h = context.GetHandle(objectId);
                                    if (h == null || h.ComObject == null)
                                        return Error("找不到起始对象编号 " + objectId + "。请先调用 se_get_selection,或省略 objectId 使用活动文档。");
                                    startDoc = h.ComObject;
                                }
                                else
                                {
                                    var app = context.GetApplication();
                                    startDoc = app.ActiveDocument;
                                    if (startDoc == null)
                                        return Error("没有活动文档。请打开一个零件文档,或提供 objectId。");
                                }

                                // 防误建守卫:目标文档名与期望不符时立即拒绝,一个特征都不建。
                                // 污染事故(2026-09-22)的根因就是"以为的活动文档"≠"实际的活动文档"——
                                // 把核对放进写操作的同一个 COM 调用内,才真正闭合 TOCTOU 时间窗口。
                                if (!string.IsNullOrEmpty(expectDocument))
                                {
                                    string actualName = TryGetDocName(startDoc);
                                    if (!string.Equals(actualName, expectDocument, StringComparison.OrdinalIgnoreCase))
                                        return Error("活动文档守卫:当前文档是 \"" + (actualName ?? "(无法读取)") +
                                                     "\",与 expectDocument \"" + expectDocument + "\" 不符,已拒绝建模(未建任何特征、零僵尸)。" +
                                                     "请先切换活动文档、或修正 expectDocument 后重试。");
                                }
                                startDocResolved = true;
                            }
                            doc = startDoc;
                        }

                        // P3 动作段:measure/compute 只往求值环境里写值,不建任何特征。
                        if (segment.Action.HasValue)
                        {
                            string actionError;
                            ExecuteActionSegment(segment, doc, env, out actionError);
                            builtSegments++;
                            var actionSummary = SegmentSummary(segment, 0, actionError != null, null, null, actionError);
                            actionSummary["action"] = segment.ActionOp;
                            segSummaries.Add(actionSummary);
                            if (actionError != null)
                            {
                                anyAborted = true;
                                abortMessage = "段 \"" + segment.Name + "\": " + actionError;
                                break;
                            }
                            continue;
                        }

                        // 空段:只建文档(可选落盘),不建任何特征。
                        if (segment.Features.Count == 0)
                        {
                            string emptySaveError;
                            SaveSegmentDocument(context, doc, savePath, overwrite, out emptySaveError);
                            builtSegments++;
                            segSummaries.Add(SegmentSummary(segment, 0, false, TryModelVolume(doc), savePath, emptySaveError));
                            if (emptySaveError != null)
                            {
                                anyAborted = true;
                                abortMessage = emptySaveError;
                                break;
                            }
                            continue;
                        }

                        var segmentFeatures = segment.Features.ToArray();

                        // 有 measure/compute 时占位符不能提前替换(后面的段要用前面的实测值),这里逐段求值。
                        if (hasDynamicOps)
                        {
                            JsonElement[] resolvedSegment;
                            string subError;
                            if (!BuildPipeline.Substitute(segmentFeatures, env, out resolvedSegment, out subError))
                            {
                                anyAborted = true;
                                abortMessage = "段 \"" + segment.Name + "\" 占位符求值失败: " + subError;
                                break;
                            }
                            segmentFeatures = resolvedSegment;
                        }

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
                    // face 平面缓存(Face.ID → 贴面隐藏 RefPlane)。同一张面被多个特征引用时只建一张,
                    // 避免模型树堆一串无名 RefPlane。
                    // 2026-10-10:特征失败时【不】回滚删除这些平面(原因见主循环内那处说明)。
                    var facePlanes = new Dictionary<int, object>();
                    // face 平面相关的非致命提示,不阻断建模。
                    // 2026-10-10:旧的唯一写入点("孤儿平面删除失败")已随回滚移除,此通道保留备用。
                    var batchWarnings = new List<string>();
                    var results = new List<object>();

                    // P1 topologyDiff：建前拓扑捕获（内存快照，不落盘）。空文档(首次建模)捕获会失败，
                    // 属正常路径——按"空拓扑"处理，建后 added=全部面、volumeDelta=建后总体积。
                    ModelStateSnapshotTools.TopoSnap topoBefore = null;
                    bool topoBeforeEmptyDoc = false;
                    if (topologyDiff)
                    {
                        var tw = new List<string>();
                        string terr;
                        topoBefore = ModelStateSnapshotTools.CaptureDoc(doc, tw, out terr);
                        if (topoBefore == null)
                        {
                            topoBeforeEmptyDoc = true;   // 空文档/曲面体：不算错误，Compare 按 0 面起点处理
                            batchWarnings.Add("topologyDiff 建前捕获: " + terr + "（按空拓扑处理）");
                        }
                        batchWarnings.AddRange(tw);
                    }

                    // 统一走 FeatureSpec 解析:构建器与静态校验器(FeatureValidator)共用同一份取数语义,
                    // 避免"校验说没问题、构建却是另一套解释"。P2:只解析**本段**特征(doc op 不进来)。
                    var specs = FeatureSpecParser.ParseAll(segmentFeatures);

                    bool aborted = false;

                    for (int i = 0; i < specs.Count; i++)
                    {
                        var spec = specs[i];
                        string op = spec.Op;
                        string name = spec.Name;

                        object result;
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
                                case "polarhole":
                                    result = PolarHoleOp(context, doc, spec, name, namedPlanes, planeNormals, facePlanes);
                                    break;
                                case "boltcircle":
                                    result = BoltCircleOp(context, doc, spec, name, namedPlanes, planeNormals, facePlanes, namedFeatures);
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
                                    result = PatternOp(context, doc, spec, name, namedPlanes, namedFeatures);
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
                                case "loft_surface":
                                    result = LoftSurfaceOp(context, doc, spec, name, namedPlanes, namedFeatures, surfaceFaces);
                                    break;
                                case "thicken":
                                    result = ThickenOp(context, doc, spec, name, namedFeatures, surfaceFaces);
                                    break;
                                case "delete_face":
                                    result = DeleteFaceOp(context, doc, spec, name, namedFeatures);
                                    break;
                                case "bluesurf":
                                    result = BluesurfOp(context, doc, spec, name, namedFeatures, surfaceFaces);
                                    break;
                                // 2026-11-02 3D 曲线:只往 3D 草图里加曲线,不产生实体特征
                                case "spline3d":
                                    result = Spline3DOp(context, doc, spec, name);
                                    break;
                                case "arc3d":
                                    result = Arc3DOp(context, doc, spec, name);
                                    break;
                                default:
                                    result = new { op = op, name = name, status = "error", message = "未知 op(仅支持 plane/extrude/cut/hole/polarhole/boltcircle/revolve/fillet/chamfer/rib/pattern/loft/loft_surface/sweep/helix/draft/split/web_network/extrude_surface/thicken/delete_face/spline3d/arc3d/bluesurf)。" };
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            result = new { op = op, name = name, status = "error", message = DescribeException(ex) };
                        }

                        results.Add(result);

                        // 2026-10-10 修正:特征失败时【不再回滚】本次新建的贴面参考平面。
                        //
                        // 旧实现(见 git 历史)在 !IsOk(result) 时对"本次新建"的 Face.ID 调 TryDeleteFacePlane,
                        // 意图是不留垃圾。但实测(2026-10-10 真机复现)这条路是有害的:
                        //   特征失败时,建在该贴面平面上的轮廓/ProfileSet 通常已被先行删除
                        //   (DiscardCutCandidate 等),而"删掉以某张贴面平面为基础建出的 ProfileSet"
                        //   会把那张贴面 RefPlane 的 RCW 变成 CO_E_OBJNOTCONNECTED ——
                        //     · 紧随其后的 RefPlane.Delete 必然失败(旧实现只记 warning hr=0x80010108,静默降级);
                        //     · 更严重:该 Face.ID 被污染——此后对同一面再锚定,
                        //       RefPlanes.AddParallelByDistance / Profiles.Add(plane) 在【建轮廓阶段】
                        //       直接报 COMException 0x800401FD,而同文档内其它 Face.ID 完全正常(污染按面隔离)。
                        //   这与 §十二 已修过的"求基阶段临时建/删 ProfileSet"是同一类缺陷,只是发生在回滚分支。
                        //
                        // 贴面平面本身没坏(轮廓已成功建在其上,只是特征参数不符),保留在 facePlanes 缓存里
                        // 供后续特征复用:既不新建堆积、也不删除破坏 RCW。与既有拍板一致——
                        // §十二 已明确"贴面平面当前无害、暂不自动清理(仅记录)"。

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

                    // P1 topologyDiff：建后再捕获一次并对比（aborted 也执行——失败回滚后 identical=true
                    // 就是"确实没动模型"的硬证据）。对比语义与 se_model_state_snapshot 的 diff 完全同源
                    // （同一个 Compare 纯函数），两处数字永远一致。
                    object topoDiffObj = null;
                    if (topologyDiff)
                    {
                        var tw2 = new List<string>();
                        string terr2;
                        var topoAfter = ModelStateSnapshotTools.CaptureDoc(doc, tw2, out terr2);
                        if (topoAfter == null)
                        {
                            batchWarnings.Add("topologyDiff 建后捕获失败: " + terr2);
                        }
                        else
                        {
                            var b = topoBefore ?? new ModelStateSnapshotTools.TopoSnap();
                            var d = ModelStateSnapshotTools.Compare(b, topoAfter, 1.0, 20);
                            var t = new Dictionary<string, object>
                            {
                                ["facesBefore"] = d.facesBefore,
                                ["facesAfter"] = d.facesAfter,
                                ["added"] = d.totalAdded,
                                ["removed"] = d.totalRemoved,
                                ["changed"] = d.totalChanged,
                                ["identical"] = d.identical
                            };
                            if (d.volumeDeltaMm3.HasValue) t["volumeDeltaMm3"] = d.volumeDeltaMm3.Value;
                            if (d.featuresAdded.Count > 0) t["featuresAdded"] = d.featuresAdded.Count;
                            if (topoBeforeEmptyDoc) t["note"] = "建前为空文档:added=全部现有面,volumeDeltaMm3=总体积。";
                            topoDiffObj = t;
                        }
                        batchWarnings.AddRange(tw2);
                    }

                    // ---- 段收尾:落盘(若 doc op 给了 save)+ 汇总 ----
                    string saveError;
                    string savedPath = SaveSegmentDocument(context, doc, savePath, overwrite, out saveError);

                    builtSegments++;
                    allResults.AddRange(results);
                    allWarnings.AddRange(batchWarnings);
                    var summary = SegmentSummary(segment, results.Count, aborted, TryModelVolume(doc), savedPath, saveError);
                    if (topoDiffObj != null) summary["topologyDiff"] = topoDiffObj;
                    if (recomputeNote != null) summary["recompute"] = recomputeNote;
                    segSummaries.Add(summary);

                    if (saveError != null)
                    {
                        anyAborted = true;
                        abortMessage = saveError;
                        break;
                    }
                    if (aborted)
                    {
                        anyAborted = true;
                        abortMessage = "段 \"" + segment.Name + "\" 第 " + results.Count + " 个特征未建成" +
                                       "(失败特征已自动回滚删除),后续段已停止执行,以免连环失败。请按该条的 diagnosis 修正后重跑。";
                        break;
                    }
                }   // 段循环结束

                    var payload = new Dictionary<string, object>
                    {
                        ["status"] = anyAborted ? "error" : "ok",
                        ["segmentCount"] = segments.Count,
                        ["builtSegments"] = builtSegments,
                        ["fromSegment"] = startSeg,
                        ["nextCursor"] = endSeg < segments.Count ? (object)endSeg : null,
                        ["done"] = endSeg >= segments.Count,
                        ["featureCount"] = allResults.Count,
                        ["aborted"] = anyAborted,
                        ["message"] = abortMessage,
                        ["recomputeAfter"] = recomputeAfter,
                        ["warnings"] = allWarnings.Count > 0 ? allWarnings : null,
                        ["segments"] = segSummaries
                    };
                    if (!slim) payload["results"] = allResults;

                    // 按需注入:本批新出现的 op 才发手册(每个 op 一次),之后只留一行指针。
                    // 放在最后统一处理,成功与 aborted 两条路径都覆盖。
                    var doneInj = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, docOps);
                    if (doneInj.Count > 0) payload["opDocs"] = doneInj;
                    var doneNote = OpDocRegistry.Note(docOps, doneInj.Keys);
                    if (doneNote != null) payload["opDocsNote"] = doneNote;

                    return JsonSerializer.Serialize(payload);
                });
            }
            catch (Exception ex)
            {
                return Error("se_model_build 失败: " + DescribeException(ex));
            }
        }

        /// <summary>
        /// 取本批涉及、且手册里收录的 op(去重、保持出现顺序)。
        /// 复用 FeatureSpecParser 的解析(与校验器同一份),op 缺失/非字符串时和构建器一样回退 extrude。
        /// 必须先过滤手册里没有的名字:写错的 op 会走到 E101,不过滤就会往 opDocs 里塞空条目。
        /// </summary>
        private static List<string> OpsOf(JsonElement[] features)
        {
            var list = new List<string>();
            if (features == null || features.Length == 0) return list;

            foreach (var spec in FeatureSpecParser.ParseAll(features))
            {
                if (spec == null || string.IsNullOrEmpty(spec.OpLower)) continue;
                if (!OpManual.Has(spec.OpLower)) continue;
                if (list.Contains(spec.OpLower, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(spec.OpLower);
            }
            return list;
        }

        /// <summary>
        /// P0 文件入口:读 features JSON 文件。接受两种根节点——数组,或 {"features":[...]} 包装
        /// (与 CLI --model 同格式)。返回的元素一律 Clone:JsonDocument 释放后其原生元素失效,
        /// 克隆件可以安全带出本方法。
        /// </summary>
        internal static JsonElement[] LoadFeaturesFile(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "featuresFile 是空字符串。要么给一个 JSON 文件路径,要么不传它、改用内联 features。";
                return null;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                error = "featuresFile 路径非法: " + path + "(" + ex.Message + ")";
                return null;
            }
            if (!File.Exists(full))
            {
                error = "找不到 features 文件: " + full + "(相对路径按服务进程当前目录解析,建议直接给绝对路径)";
                return null;
            }

            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (Exception ex)
            {
                error = "读 features 文件失败: " + full + "(" + ex.Message + ")";
                return null;
            }

            try
            {
                using (var docJson = JsonDocument.Parse(text))
                {
                    var root = docJson.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("features", out var inner))
                        root = inner;
                    if (root.ValueKind != JsonValueKind.Array)
                    {
                        error = "features 文件必须是 JSON 数组,或 {\"features\":[...]} 包装: " + full;
                        return null;
                    }

                    var list = new List<JsonElement>();
                    foreach (var item in root.EnumerateArray())
                        list.Add(item.Clone());
                    return list.ToArray();
                }
            }
            catch (JsonException ex)
            {
                error = "features 文件不是合法 JSON: " + full + "(" + ex.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// P1:sets → 数值 → 替换占位符。失败时 error 非空且 resolved 为 null,调用方直接整批拒绝
        /// (不做"部分替换"——半成品 JSON 会被校验器当成合法输入,是最坏的结果)。
        /// </summary>
        internal static bool ApplySets(JsonElement[] features, JsonElement? sets,
            out JsonElement[] resolved, out string error)
        {
            resolved = null;
            error = null;

            // 表驱动:sets 是数组 = 每行一套参数,模板按行展开一遍。
            // 每行的开头必须是 doc op(否则多行会全挤进同一份文档,那不是调用方想要的)。
            if (sets.HasValue && sets.Value.ValueKind == JsonValueKind.Array)
            {
                bool hasDocOp = false;
                if (features != null)
                {
                    foreach (var f in features)
                    {
                        if (BuildPipeline.IsDocOp(f)) { hasDocOp = true; break; }
                    }
                }
                if (!hasDocOp)
                {
                    error = "sets 用了行数组(表驱动),但 features 模板里没有 doc op:每行要建一份文档,"
                        + "否则所有行会挤进同一份文档。请在模板开头加 {\"op\":\"doc\",\"save\":\"...\"}。";
                    return false;
                }

                var all = new List<JsonElement>();
                int rowIndex = 0;
                foreach (var row in sets.Value.EnumerateArray())
                {
                    rowIndex++;
                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        error = "sets 第 " + rowIndex + " 行必须是对象:{ \"名字\": 值 }。";
                        return false;
                    }

                    Dictionary<string, double> rowValues;
                    string rowError;
                    if (!BuildPipeline.ResolveSets(row, out rowValues, out rowError))
                    {
                        error = "sets 第 " + rowIndex + " 行: " + rowError;
                        return false;
                    }

                    JsonElement[] expanded;
                    if (!BuildPipeline.Substitute(features, rowValues, out expanded, out rowError))
                    {
                        error = "sets 第 " + rowIndex + " 行: " + rowError;
                        return false;
                    }
                    all.AddRange(expanded);
                }

                if (all.Count == 0)
                {
                    error = "sets 行数组是空的,没有可建的内容。";
                    return false;
                }

                resolved = all.ToArray();
                return true;
            }

            Dictionary<string, double> values;
            if (!BuildPipeline.ResolveSets(sets, out values, out error))
                return false;
            return BuildPipeline.Substitute(features, values, out resolved, out error);
        }

        /// <summary>
        /// P3:执行 measure / compute 动作段——只往求值环境写值,**不碰模型**(改模型必须走后续声明式 op,
        /// 体积对账/E401/僵尸检测才管得住)。
        ///   measure:{"op":"measure","property":"volume","as":"v1"} → env["v1"]=m³、env["v1.mm3"]=mm³
        ///   compute:{"op":"compute","expr":"{{v1.mm3}} / 2","as":"half"} → env["half"]
        /// </summary>
        private static void ExecuteActionSegment(BuildSegment segment, object doc,
            Dictionary<string, double> env, out string error)
        {
            error = null;
            var op = segment.Action.Value;
            string asName = BuildPipeline.ReadString(op, "as");
            if (string.IsNullOrWhiteSpace(asName)) asName = segment.ActionOp + "_" + segment.Name;

            if (string.Equals(segment.ActionOp, "measure", StringComparison.OrdinalIgnoreCase))
            {
                string property = BuildPipeline.ReadString(op, "property") ?? "volume";
                if (!string.Equals(property, "volume", StringComparison.OrdinalIgnoreCase))
                {
                    error = "measure 的 property 目前只支持 \"volume\";要读面/边/包围盒请用 se_read_geometry / se_read_model_state。";
                    return;
                }
                double? volume = TryModelVolume(doc);
                if (!volume.HasValue)
                {
                    error = "measure 读不到体积(文档里可能还没有实体)。";
                    return;
                }
                env[asName] = volume.Value;
                env[asName + ".mm3"] = volume.Value * 1e9;
                return;
            }

            // compute:纯表达式求值(可引用 sets 与前面 measure 出来的值)。
            string expr = BuildPipeline.ReadString(op, "expr");
            if (string.IsNullOrWhiteSpace(expr))
            {
                error = "compute 需要 expr 字符串(可引用已有值,如 \"{{v1.mm3}} / 2\")。";
                return;
            }
            double value;
            string evalError;
            if (!BuildPipeline.Evaluate(expr, env, out value, out evalError))
            {
                error = "compute 求值失败: " + evalError;
                return;
            }
            env[asName] = value;
        }

        /// <summary>
        /// P2:按 doc op 新建一份文档(不自动保存,保存由段收尾统一做)。
        /// 与 se_new_document 的区别:这里在已有的 COM 调用内直接建,不再嵌套 context.Invoke
        /// (嵌套 Invoke 从 STA 线程进去会自锁),所以只用同一张 ProgID 表、不走 handle 追踪。
        /// </summary>
        private static object CreateSegmentDocument(SolidEdgeContext context, JsonElement docOp,
            out string savePath, out bool overwrite, out string error)
        {
            savePath = BuildPipeline.ReadString(docOp, "save");
            overwrite = false;
            error = null;

            JsonElement owFlag;
            if (docOp.TryGetProperty("overwrite", out owFlag) && owFlag.ValueKind == JsonValueKind.True)
                overwrite = true;

            // 落盘可行性提前判:目标已存在且没给 overwrite 就在这里拒绝。
            // 不许"先建一堆特征、到段尾才说存不了"——那会留下一个建好却没落盘的文档(真机实测踩到过)。
            if (!string.IsNullOrWhiteSpace(savePath) && !overwrite)
            {
                string earlyTarget;
                try
                {
                    earlyTarget = Path.GetFullPath(savePath);
                }
                catch (Exception ex)
                {
                    error = "save 路径非法: " + savePath + "(" + ex.Message + ")";
                    return null;
                }
                if (File.Exists(earlyTarget))
                {
                    error = "doc op 的 save 目标已存在,已拒绝覆盖: " + earlyTarget
                        + "(确需覆盖请在该 doc op 里写 overwrite:true)。本次未建任何特征。";
                    return null;
                }
            }

            string docType = BuildPipeline.ReadString(docOp, "type");
            string progId = DocumentTools.ResolveDocumentProgId(docType);
            if (progId == null)
            {
                error = "type 不支持: \"" + docType + "\"(part/assembly/draft/sheetmetal/weldment,或直接给 ProgID)";
                return null;
            }

            try
            {
                var app = context.GetApplication();
                bool prevAlerts = true;
                bool alertsChanged = false;
                try
                {
                    prevAlerts = app.DisplayAlerts;
                    app.DisplayAlerts = false;
                    alertsChanged = true;
                }
                catch { }

                try
                {
                    object documents = app.Documents;
                    object created = documents.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null,
                        documents, new object[] { progId });
                    if (created == null)
                    {
                        error = "Documents.Add 返回空(" + progId + ")";
                        return null;
                    }
                    return created;
                }
                finally
                {
                    if (alertsChanged)
                    {
                        try { app.DisplayAlerts = prevAlerts; } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                error = DescribeException(ex);
                return null;
            }
        }

        /// <summary>
        /// P2:段收尾落盘。save 为空 = 不落盘(返回 null)。
        /// 覆盖策略与 se_save_document 一致:默认拒绝覆盖已存在文件,overwrite=true 才先删再存。
        /// </summary>
        private static string SaveSegmentDocument(SolidEdgeContext context, object doc, string savePath,
            bool overwrite, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(savePath) || doc == null) return null;

            string target;
            try
            {
                target = Path.GetFullPath(savePath);
            }
            catch (Exception ex)
            {
                error = "save 路径非法: " + savePath + "(" + ex.Message + ")";
                return null;
            }

            try
            {
                if (File.Exists(target))
                {
                    if (!overwrite)
                    {
                        error = "doc op 的 save 目标已存在,已拒绝覆盖: " + target + "(确需覆盖请在该 doc op 里写 overwrite:true)";
                        return null;
                    }
                    File.Delete(target);
                }
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var app = context.GetApplication();
                bool prevAlerts = true;
                bool alertsChanged = false;
                try
                {
                    try { prevAlerts = app.DisplayAlerts; app.DisplayAlerts = false; alertsChanged = true; } catch { }
                    doc.GetType().InvokeMember("SaveAs", BindingFlags.InvokeMethod, null, doc, new object[] { target });
                }
                finally
                {
                    if (alertsChanged)
                    {
                        try { app.DisplayAlerts = prevAlerts; } catch { }
                    }
                }
                return target;
            }
            catch (Exception ex)
            {
                error = "落盘失败: " + target + "(" + DescribeException(ex) + ")";
                return null;
            }
        }

        /// <summary>段摘要(slim 模式下的主角:不逐个吐特征,只说这段建了几个、多大体积、存哪了)。</summary>
        private static Dictionary<string, object> SegmentSummary(BuildSegment segment, int featureCount, bool aborted,
            double? volumeM3, string savedPath, string error)
        {
            var d = new Dictionary<string, object>
            {
                ["segment"] = segment.Name,
                ["createdDoc"] = segment.Doc.HasValue,
                ["featureCount"] = featureCount,
                ["aborted"] = aborted
            };
            if (volumeM3.HasValue) d["volumeMm3"] = Math.Round(volumeM3.Value * 1e9, 3);
            if (savedPath != null) d["saved"] = savedPath;
            if (error != null) d["error"] = error;
            return d;
        }

        /// <summary>
        /// features 与 featuresFile 二选一(同时传会歧义,直接拒绝)。文件优先走 LoadFeaturesFile。
        /// </summary>
        internal static JsonElement[] ResolveFeatures(JsonElement[] features, string featuresFile, out string error)
        {
            error = null;
            bool hasInline = features != null && features.Length > 0;
            bool hasFile = !string.IsNullOrWhiteSpace(featuresFile);

            if (hasInline && hasFile)
            {
                error = "features 与 featuresFile 只能二选一,不要同时传:内联特征就别给 featuresFile,文件输入就把 features 留空。";
                return null;
            }
            if (hasFile)
                return LoadFeaturesFile(featuresFile, out error);
            return features;
        }

        /// <summary>
        /// features JSON 的静态校验:【纯函数,不启动 COM、不碰文档】,SE 没启动也能跑。
        /// 与构建器共用 FeatureSpecParser 的解析结果,所以"校验通过"就等于"构建器会这么解释它"。
        /// 目的:把僵尸特征(调用成功但 Status=1216476311、几何没生成)的发现时点从事后提到事前。
        /// 2026-11-07:MCP 工具面已并入 se_model_build(dryRun=true),本方法保留给 CLI --validate;
        /// 旧工具名 se_validate_features 由 ToolAliases 改写路由到 se_model_build{dryRun:true}。
        /// </summary>
        [Description("声明式建模的静态校验:不建特征、不碰 Solid Edge,毫秒级检查 features JSON。" +
            "拦五类问题——结构(未知 op/缺必填/类型错/NaN/疑似把 mm 当 m)、" +
            "引用(plane 缺失/RefPlane_N 越界/@别名未定义或前向引用/别名重复)、" +
            "几何(环点数不足/相邻点重合/自交/退化面积/绕向不一致)、" +
            "语义(除料前没有拉伸/extrude 缺 depth/同平面多次 cut 未合并/除料落在毛坯外/环重叠)、" +
            "声明(autoConstraint/fixOrigin/dims 声明了但形状走不到应用路径/dims.element 越界)。" +
            "返回 status=ok|warning|error 与 issues(每条带 code/level/feature/field/message/fix)。" +
            "有 error 时 se_model_build 会直接拒绝执行。SE 未启动也能调用,适合动手前先跑一遍。")]
        public static string se_validate_features(
            SolidEdgeContext context,
            [Description("特征列表(JSON 数组),同 se_model_build。与 featuresFile 二选一")] JsonElement[] features = null,
            [Description("true=目标文档里已有实体(增量建模),放行\"本批第一个特征就是除料\"。与 se_model_build 同义")] bool assumeStock = false,
            [Description("features JSON 文件路径。与 features 二选一")] string featuresFile = null,
            [Description("参数表(与 se_model_build 同义),用于在 SE 未启动时预检表达式")] JsonElement? sets = null)
        {
            try
            {
                string fileError;
                features = ResolveFeatures(features, featuresFile, out fileError);
                if (fileError != null) return Error(fileError);

                string manualText;
                if (OpManual.TryQuery(features, out manualText))
                    return manualText;

                // P2/P3:先切段再决定 sets 怎么求——有 measure/compute 时段间有依赖,只能逐段惰性求值。
                List<BuildSegment> segments;
                string splitError;
                if (!BuildPipeline.SplitSegments(features, out segments, out splitError))
                    return Error(splitError);

                bool hasDynamicOps = false;
                foreach (var s in segments)
                {
                    if (s.ActionOp != null) { hasDynamicOps = true; break; }
                }

                var runtimeValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (hasDynamicOps)
                {
                    if (sets.HasValue && sets.Value.ValueKind == JsonValueKind.Array)
                        return Error("表驱动(sets 行数组)与 measure/compute 不能同时用,请拆成两次调用。");
                    string dynSetsError;
                    if (!BuildPipeline.ResolveSets(sets, out runtimeValues, out dynSetsError))
                        return Error(dynSetsError);
                }
                else
                {
                    string setsError;
                    if (!ApplySets(features, sets, out features, out setsError))
                        return Error(setsError);

                    // 替换后要重新切段:占位符可能出现在 doc 字段(如表驱动的 save 路径)。
                    if (!BuildPipeline.SplitSegments(features, out segments, out splitError))
                        return Error(splitError);
                }

                var docOps = OpsOf(features);
                var inj = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, docOps);
                var note = OpDocRegistry.Note(docOps, inj.Keys);

                if (segments.Count > 1)
                {
                    var list = new List<object>();
                    bool anyErr = false;
                    foreach (var seg in segments)
                    {
                        if (seg.Action.HasValue)
                        {
                            list.Add(new { segment = seg.Name, action = seg.ActionOp, validate = (object)null });
                            continue;
                        }

                        var segFeats = seg.Features.ToArray();
                        if (hasDynamicOps)
                        {
                            JsonElement[] substituted;
                            string subError;
                            if (!BuildPipeline.Substitute(segFeats, runtimeValues, out substituted, out subError))
                            {
                                list.Add(new
                                {
                                    segment = seg.Name,
                                    validate = (object)null,
                                    note = "依赖前段运行时值,静态校验跳过(构建时逐段再校验): " + subError
                                });
                                continue;
                            }
                            segFeats = substituted;
                        }

                        var r = FeatureValidator.Validate(segFeats, assumeStock);
                        if (r.HasError) anyErr = true;
                        list.Add(new { segment = seg.Name, validate = r });
                    }
                    return JsonSerializer.Serialize(new
                    {
                        status = anyErr ? "error" : "ok",
                        segmentCount = segments.Count,
                        hasError = anyErr,
                        segments = list,
                        opDocs = inj.Count > 0 ? inj : null,
                        opDocsNote = note
                    }, FeatureValidator.JsonOpts);
                }

                var singleFeatures = segments.Count == 1 ? segments[0].Features.ToArray() : features;
                if (hasDynamicOps)
                {
                    JsonElement[] substituted;
                    string subError;
                    if (!BuildPipeline.Substitute(singleFeatures, runtimeValues, out substituted, out subError))
                    {
                        return JsonSerializer.Serialize(new
                        {
                            status = "deferred",
                            message = "这段依赖 measure/compute 的运行时值,静态阶段算不出来——只在 se_model_build 执行时逐段校验。",
                            detail = subError,
                            opDocs = inj.Count > 0 ? inj : null,
                            opDocsNote = note
                        }, FeatureValidator.JsonOpts);
                    }
                    singleFeatures = substituted;
                }

                var report = FeatureValidator.Validate(singleFeatures, assumeStock);

                // 无 op 可注入时沿用原来的紧凑结构,避免每份校验报告都多两个空字段。
                if (inj.Count == 0 && note == null)
                    return report.ToJson();

                return JsonSerializer.Serialize(new
                {
                    status = report.status,
                    version = report.version,
                    featureCount = report.featureCount,
                    errorCount = report.errorCount,
                    warningCount = report.warningCount,
                    hasError = report.HasError,
                    issues = report.issues,
                    opDocs = inj.Count > 0 ? inj : null,
                    opDocsNote = note
                }, FeatureValidator.JsonOpts);
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
                DiscardCutCandidate(profile, featObj, IsFaceAnchored(spec));
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

            DiscardCutCandidate(profile, featObj, IsFaceAnchored(spec));
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
            // 2026-11-06 修复:extrude 此前不处理 replaceOf(静默忽略)——重建语义补齐(通用规则 12)
            var replaceInfo = ReplaceNamedFeatures(doc, spec.ReplaceOf, specWarnings);
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
                    replaced = replaceInfo,
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
            // 2026-11-06 修复:cut 此前不处理 replaceOf(静默忽略)——重建语义补齐(通用规则 12)
            var replaceInfo = ReplaceNamedFeatures(doc, spec.ReplaceOf, specWarnings);
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
                    if (ClassifyFeatureStatus(st) == FeatureHealth.Zombie)
                    {
                        // ① 这一组方向切到实体外了(僵尸):删掉特征和它的草图,换下一组重试。
                        DiscardCutCandidate(profile, featObj, IsFaceAnchored(spec));
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
                        DiscardCutCandidate(bestProfile, bestFeat, IsFaceAnchored(spec));
                        bestProfile = profile;
                        bestFeat = featObj;
                        bestSuspicious = false;
                        bestPs = ps;
                        bestPps = pps;
                    }
                    else
                    {
                        // 已有候选更可信(或两个都可疑,先来者优先)→ 丢弃这个
                        DiscardCutCandidate(profile, featObj, IsFaceAnchored(spec));
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
                replaced = replaceInfo,
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
        /// 孔(hole):建真正的 Hole 特征(Model.Holes.Add*)。
        ///
        /// ★ 2026-10-23 重写。旧实现把孔当成"圆轮廓 + ExtrudedCutout"的假孔:特征树里是除料、
        ///   没有孔数据(HoleData),螺纹孔根本做不出来。真机实测推翻旧注释(Holes.Add* 确实切材料),
        ///   而且带 HoleData 的只有它——这是螺纹孔的唯一通路。
        ///
        /// 两种用法:
        ///   ① 光孔:plane + circle/circles(或 center+diameter)+ mode/depth;
        ///   ② 螺纹孔:plane + thread:"M10"(可选 standard / threadoption),孔径按 SE 孔数据库
        ///      自动带出(底孔/内螺纹小径/螺距),此时再写 diameter 不生效(以螺纹为准,回 warning)。
        ///
        /// 轮廓里画的是 Hole2d(孔位点)而不是 Circles2d:直径只由 HoleData 决定,画真圆没用。
        /// 方向自愈/僵尸回滚沿用 cut 那套(方向错 → 6311 僵尸 → 换一侧重试)。
        /// </summary>
        private static object HoleOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, double[]> planeNormals,
            Dictionary<int, object> facePlanes = null)
        {
            var specWarnings = new List<string>();

            // ---------- 1) 螺纹规格 / 光孔直径 ----------
            ThreadRow thread = null;
            if (!string.IsNullOrWhiteSpace(spec.Thread))
            {
                ThreadRow row;
                string lookupError;
                if (!HoleThreadTable.TryFind(spec.Standard, spec.Thread, out row, out lookupError))
                    return new
                    {
                        op = "hole",
                        name = name,
                        status = "error",
                        message = lookupError,
                        fix = new { action = "set", field = "thread", examples = new[] { "M10", "M10x1.25", "Rp1/2", "R1/2" } }
                    };
                thread = row;

                string opt = (spec.ThreadOption ?? "").Trim().ToLowerInvariant();
                if (opt.Length > 0 && opt != "minor" && opt != "tapdrill" && opt != "tap_drill" && opt != "nominal")
                    return new
                    {
                        op = "hole",
                        name = name,
                        status = "error",
                        message = "threadoption 只能是 minor(默认,内螺纹小径)/ tapdrill(丝锥直径)/ nominal(标称直径),收到 \"" + spec.ThreadOption + "\"。"
                    };

                if (spec.HasCircle || (spec.HasCircles && spec.Circles.Count > 0 && spec.Circles[0][2] > 0))
                    specWarnings.Add("螺纹孔:circle/circles 里的半径被忽略,实际孔径由螺纹规格决定 —— " + thread.Describe() + "。");
            }
            else if (!spec.HasCircle && !spec.HasCircles && !(spec.BoltDia > 0))
            {
                return new
                {
                    op = "hole",
                    name = name,
                    status = "error",
                    message = "hole 需要 circle/circles(孔位)、center+diameter,或标准定位 boltdia+angleDeg(分布圆Ø+角度)。"
                };
            }

            // 2026-10-24 标准定位:boltdia 模式下孔径必须由 diameter/thread 给(无 circle 半径可用)
            if (spec.BoltDia > 0 && thread == null && !(spec.Diameter > 0))
                return new { op = "hole", name = name, status = "error",
                    message = "boltdia(标准定位)模式需要 diameter(孔径)或 thread(螺纹规格)。" };

            double diameterM = thread != null
                ? thread.ModelDiameterMm(spec.ThreadOption) / 1000.0
                : (spec.BoltDia > 0 ? spec.Diameter.Value
                   : (spec.HasCircle ? spec.CircleR * 2.0 : spec.Circles[0][2] * 2.0));

            if (!(diameterM > 0))
                return new { op = "hole", name = name, status = "error", message = "孔径 <= 0,无法建孔(光孔请给 circle 半径或 diameter)。" };

            // ---------- 2) 平面 / 模型 / 方向 ----------
            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes, facePlanes, spec.CoordsLocal);
            bool visible = spec.Visible ?? false;

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "hole", name = name, status = "error", message = "打孔前必须先建实体(extrude)。" };

            object model = Get(models, "Item", 1);
            // ★ 2026-10-24 修复:Holes 集合改强类型获取(与建轮廓/建孔同轮;全强类型链实测成功)。
            object holesColl = ((SolidEdgePart.Model)model).Holes;

            string mode = NormalizeHoleMode(spec.Mode);
            // ★ 2026-10-24 修复:默认方向 1→2。孔是除料语义(同 cut:side=2 才切到实体),
            //   原默认 1 在顶面草图上朝空气打 → 必僵尸 → 进换侧重试 → 踩中"删 Hole 特征
            //   会断连其 HoleData RCW"的坑。默认值改对,首次即中,重试只剩显式给错 side/dir 的场景。
            int side = ResolveSideByDir(spec, planeNormals, 2, specWarnings, "hole");
            double depth = spec.Depth ?? 0.2;

            // ---------- 3)+4) 逐孔位建孔 ----------
            // ★ 2026-10-24 第二轮修复:弃"一 profile 多 Hole2d"——干净文档实测该结构不可靠:
            //   End(igProfileClosed) 把多 Hole2d 清剩 1 个(8→1,终审实锤),幸存点能切;
            //   不 End 则点保留但 AddThroughAll 直接 6311 僵尸(0 切料)。
            //   改为【每个孔位独立 profile + 独立 Hole 特征】(单点路径已被 A/D/法兰充分验证):
            //   每孔独立 HoleData,工程图可逐孔识别;单点 = 1 次迭代的退化情形,统一代码路径。
            //   任一点失败 → 整体回滚(先删各点 HoleData 再删特征,避免断连残留),报失败点序号。
            //   旧"换侧重试"取消:默认 side=2 已是除料正方向,僵尸=孔位/板问题,重试无意义。
            double? v0 = TryModelVolume(doc);

            var holePts = new List<double[]>();
            if (spec.BoltDia > 0)
            {
                // ★ 2026-10-24 标准定位:孔位 = 分布圆Ø + 起始角(几何唯一,草图可全约束)
                double ang = (spec.StartAngleDeg ?? 0) * Math.PI / 180.0;
                double rr = spec.BoltDia.Value / 2.0;
                holePts.Add(new[] { rr * Math.Cos(ang), rr * Math.Sin(ang) });
            }
            else if (spec.HasCircle) holePts.Add(new[] { spec.CircleX, spec.CircleY });
            else foreach (var c in spec.Circles) holePts.Add(new[] { c[0], c[1] });

            // 2026-10-24 替换语义:重建前按名删旧特征(改 boltdia/angleDeg 重跑时用)
            var replaceInfo = ReplaceNamedFeatures(doc, spec.ReplaceOf, specWarnings);

            var built = new List<object[]>();   // 每项 {feat, holeData, profile}
            object firstProfile = null, firstFeat = null;
            string firstFeatName = null;
            var pointRows = new List<object>();

            for (int pi = 0; pi < holePts.Count; pi++)
            {
                double px = holePts[pi][0], py = holePts[pi][1];
                string where = "第 " + (pi + 1) + "/" + holePts.Count + " 个孔位(" +
                               px.ToString("0.####", CultureInfo.InvariantCulture) + "," +
                               py.ToString("0.####", CultureInfo.InvariantCulture) + ")";

                object profile;
                try
                {
                    profile = CreateHoleProfileForFeature(context, doc, plane, spec, px, py, visible,
                        pi == 0 ? specWarnings : null);
                }
                catch (Exception ex)
                {
                    RollbackBuiltHoles(built);
                    throw new Exception("[hole] " + where + " 建轮廓失败: " + ex.Message, ex);
                }

                // ★ HoleData 每点新建(删 Hole 特征会断连其 HoleData,复用必挂——第一轮修复)
                object holeData;
                try
                {
                    holeData = CreateHoleData(doc, thread, spec.ThreadOption, diameterM, specWarnings);
                }
                catch (Exception ex)
                {
                    TryDelete(TryGetProfileSet(profile));
                    RollbackBuiltHoles(built);
                    throw new Exception("[hole] " + where + " 建孔数据失败: " + ex.Message, ex);
                }

                object feat;
                try
                {
                    feat = AddHole(holesColl, profile, side, mode, depth, holeData);
                }
                catch (Exception ex)
                {
                    TryDelete(holeData);
                    TryDelete(TryGetProfileSet(profile));
                    RollbackBuiltHoles(built);
                    throw new Exception("[hole] " + where + " 建孔失败(Holes.Add" +
                        (mode == "finite" ? "Finite" : mode == "next" ? "ThroughNext" : "ThroughAll") + "): " + ex.Message, ex);
                }

                long? st = SafeLong(Get(feat, "Status"));
                if (ClassifyFeatureStatus(st) == FeatureHealth.Zombie)
                {
                    // 该孔位没切到料:先删本点 holeData(RCW 活着)再删特征+草图,然后整体回滚
                    TryDelete(holeData);
                    DiscardCutCandidate(profile, feat, IsFaceAnchored(spec));
                    RollbackBuiltHoles(built);
                    return new
                    {
                        op = "hole",
                        name = name,
                        status = "error",
                        resolved = new
                        {
                            plane = spec.PlaneRef,
                            mode = mode,
                            side,
                            depth = mode == "finite" ? depth : (double?)null,
                            point = new[] { px, py },
                            pointIndex = pi + 1,
                            totalPoints = holePts.Count
                        },
                        message = where + "打孔未生成几何(6311 僵尸),已整体回滚(含前 " + pi + " 个已成功孔位);" +
                                  "常见原因:孔位落在毛坯之外、与已有孔完全重合、或 plane 没贴到实体上。",
                        diagnosis = "孔没切到实体:检查孔位坐标是否落在实体范围内(单位米)、plane 是否选对。",
                        fix = new { action = "fix_and_retry", check = new[] { "孔位", "plane", "depth" } }
                    };
                }

                string fname = SafeString(Get(feat, "Name"));
                string hid = null;
                if (pi == 0)
                {
                    firstProfile = profile;
                    firstFeat = feat;
                    firstFeatName = fname;
                }
                else
                {
                    hid = context.AddHandle(feat, "Hole", fname);
                }
                pointRows.Add(new { feature = fname, handle = hid, x = px, y = py });
                built.Add(new[] { feat, holeData, profile });
            }

            double? v1 = TryModelVolume(doc);
            double deltaMm3 = (v0.HasValue && v1.HasValue) ? (v1.Value - v0.Value) * 1e9 : double.NaN;

            object resolvedInfo = new
            {
                plane = spec.PlaneRef,
                mode = mode,
                side,
                depth = mode == "finite" ? depth : (double?)null,
                holeDiameterMm = Math.Round(diameterM * 1000.0, 4),
                holeCount = holePts.Count,
                points = pointRows,
                replaced = replaceInfo,
                thread = thread == null ? null : new
                {
                    standard = thread.Standard,
                    subtype = thread.Subtype,
                    size = thread.Size,
                    option = ThreadRow.OptionName(spec.ThreadOption),
                    nominalMm = thread.NominalMm,
                    tapDrillMm = thread.DrillMm,
                    minorMm = thread.MinorMm,
                    pitchMm = thread.PitchMm
                },
                volumeDeltaMm3 = double.IsNaN(deltaMm3) ? (double?)null : deltaMm3,
                note = holePts.Count > 1
                    ? "多点孔位=每个孔位一个独立 Hole 特征(COM 通道一 profile 多 Hole2d 不可靠:End 清点/不 End 僵尸,2026-10-24 终审);每孔独立 HoleData 便于工程图逐孔识别"
                    : null
            };

            var volFail = CheckVolumeExpectation(doc, spec, firstProfile, firstFeat, v0, v1, "hole", name, resolvedInfo, specWarnings);
            if (volFail != null)
            {
                // 对账失败:CheckVolumeExpectation 内部已删首个特征,其余点特征与 HoleData 一并回滚
                RollbackBuiltHoles(built, skipFirst: true);
                specWarnings.Add("体积对账失败回滚后,HoleData 记录可能残留(特征删除会断连它,已尽力删除);孔建议别配 expectvolumedelta。");
                return volFail;
            }

            if (mode == "next")
                specWarnings.Add("mode=next(切到下一面),孔的深度由几何决定;要精确深度请用 mode:finite + depth。");

            return FeatureResult("hole", name, firstFeat, context, firstProfile, null, specWarnings, resolvedInfo);
        }

        /// <summary>
        /// hole 多点整体回滚:先删各点 HoleData(RCW 还活着),再删特征与草图——
        /// 顺序反了会断连 HoleData 留残留。skipFirst:体积对账路径里首个特征已被
        /// CheckVolumeExpectation 删掉,只清其余点。
        /// </summary>
        private static void RollbackBuiltHoles(List<object[]> built, bool skipFirst = false)
        {
            int start = skipFirst ? 1 : 0;
            for (int i = start; i < built.Count; i++)
                TryDelete(built[i][1]);   // holeData
            for (int i = start; i < built.Count; i++)
            {
                TryDelete(built[i][0]);   // feat
                TryDelete(TryGetProfileSet(built[i][2]));   // profileSet
            }
        }

        /// <summary>hole 的 mode 归一:空/through_all/through → all(贯穿);next / finite 原样。</summary>
        private static string NormalizeHoleMode(string mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            if (m.Length == 0 || m == "through_all" || m == "through" || m == "all") return "all";
            if (m == "next" || m == "finite") return m;
            return "all";
        }

        /// <summary>
        /// 极坐标定位孔(polarhole,2026-10-25):圆形法兰定位孔专用 op。
        /// 孔位 = 分布圆 boltdia/2 · (cosθ, sinθ)(平面局部 u/v,θ 逆时针为正,每角度一个独立 Hole 特征);
        /// 定位草图【全约束】(用户手画样板 Hole_20 解码 + 真机复刻 Hole_26 验证的配方):
        /// 定位构造线(原点→孔心,ToggleConstruction)+ 辅助水平线(角度基准,替代 COM 摸不到的隐式 H 轴)
        /// + AddKeypointFix×2 + AddKeypoint×2 + AddLength/AddAngleBetweenObjects 双驱动尺寸(自动入变量表)。
        /// ★ 隔离矩阵实证(2026-10-24/25):孔轮廓带约束/构造线不是僵尸根因——根因是 side 方向
        /// (side=2=沿平面法向,顶面平面朝空中打→0 切料→6311)。故缺省 side=1 + 僵尸自愈换侧重试一次
        /// (平面法向朝材料哪侧静态不可知:RefPlane_1 材料在 +侧、平行顶面平面材料在 -侧)。
        /// </summary>
        private static object PolarHoleOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, double[]> planeNormals,
            Dictionary<int, object> facePlanes = null)
        {
            var specWarnings = new List<string>();

            // ---------- 1) 孔径:thread 或 diameter(与 hole op 同一套语义) ----------
            ThreadRow thread = null;
            if (!string.IsNullOrWhiteSpace(spec.Thread))
            {
                ThreadRow row;
                string lookupError;
                if (!HoleThreadTable.TryFind(spec.Standard, spec.Thread, out row, out lookupError))
                    return new
                    {
                        op = "polarhole",
                        name = name,
                        status = "error",
                        message = lookupError,
                        fix = new { action = "set", field = "thread", examples = new[] { "M10", "M10x1.25", "Rp1/2" } }
                    };
                thread = row;
                string opt = (spec.ThreadOption ?? "").Trim().ToLowerInvariant();
                if (opt.Length > 0 && opt != "minor" && opt != "tapdrill" && opt != "tap_drill" && opt != "nominal")
                    return new
                    {
                        op = "polarhole",
                        name = name,
                        status = "error",
                        message = "threadoption 只能是 minor(默认,内螺纹小径)/ tapdrill(丝锥直径)/ nominal(标称直径),收到 \"" + spec.ThreadOption + "\"。"
                    };
                if (spec.Diameter.HasValue)
                    specWarnings.Add("螺纹孔:diameter 被忽略,实际孔径按螺纹规格(" + thread.Describe() + ")。");
            }
            else if (!(spec.Diameter > 0))
                return new
                {
                    op = "polarhole",
                    name = name,
                    status = "error",
                    message = "polarhole 需要 diameter(孔径,米)或 thread(螺纹规格)。"
                };

            double diameterM = thread != null ? thread.ModelDiameterMm(spec.ThreadOption) / 1000.0 : spec.Diameter.Value;

            // ---------- 2) 定位:boltdia + 角度列表 ----------
            if (spec.AnglesParseError != null)
                return new
                {
                    op = "polarhole",
                    name = name,
                    status = "error",
                    message = "angles 未生效:" + spec.AnglesParseError
                };
            if (!(spec.BoltDia > 0))
                return new
                {
                    op = "polarhole",
                    name = name,
                    status = "error",
                    message = "polarhole 需要 boltdia(分布圆直径,米)。孔心=分布圆上的角度位置。"
                };
            if (spec.BoltDia.Value > 5)
                return new
                {
                    op = "polarhole",
                    name = name,
                    status = "error",
                    message = "boltdia 疑似把 mm 当米(>5m):boltdia 是【直径】且单位米。"
                };

            List<double> angles = (spec.AnglesDeg != null && spec.AnglesDeg.Count > 0)
                ? spec.AnglesDeg
                : (spec.StartAngleDeg.HasValue ? new List<double> { spec.StartAngleDeg.Value } : null);
            if (angles == null)
                return new
                {
                    op = "polarhole",
                    name = name,
                    status = "error",
                    message = "polarhole 需要角度:angles(数组,多孔各一特征)或 angleDeg(单孔)。"
                };
            foreach (double a in angles)
            {
                double norm = ((a % 360) + 360) % 360;
                if (Math.Abs(norm) < 1e-9 || Math.Abs(norm - 180) < 1e-9)
                    return new
                    {
                        op = "polarhole",
                        name = name,
                        status = "error",
                        message = "角度 " + a.ToString("0.###") + "° 与 0/180 共线:定位线与角度基准线重合,角度标注退化。请错开至少 0.1°。"
                    };
            }
            double radiusM = spec.BoltDia.Value / 2.0;

            // ---------- 3) 平面 / 模型 / 方向 ----------
            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes, facePlanes, spec.CoordsLocal);
            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "polarhole", name = name, status = "error", message = "打孔前必须先建实体(extrude)。" };
            object model = Get(models, "Item", 1);
            object holesColl = ((SolidEdgePart.Model)model).Holes;
            string mode = NormalizeHoleMode(spec.Mode);
            double depth = spec.Depth ?? 0.2;

            // 方向:显式 side/dir 尊重(ResolveSideByDir);缺省 side=1(逆法向=顶面平面往下打)。
            // side/dir 都没给时僵尸自愈:首试 6311 → 清理后换侧重试一次。
            int side = ResolveSideByDir(spec, planeNormals, spec.Side ?? 1, specWarnings, "polarhole");
            bool canHealSide = !spec.Side.HasValue && !spec.HasDir;

            double? v0 = TryModelVolume(doc);
            var replaceInfo = ReplaceNamedFeatures(doc, spec.ReplaceOf, specWarnings);

            var built = new List<object[]>();   // 每项 {feat, holeData, profile}
            object firstProfile = null, firstFeat = null;
            string firstFeatName = null;
            var pointRows = new List<object>();

            for (int pi = 0; pi < angles.Count; pi++)
            {
                double aDeg = angles[pi];
                double rad = aDeg * Math.PI / 180.0;
                double px = radiusM * Math.Cos(rad);
                double py = radiusM * Math.Sin(rad);
                string where = "第 " + (pi + 1) + "/" + angles.Count + " 孔(R=" +
                               (radiusM * 1000).ToString("0.###", CultureInfo.InvariantCulture) + "mm, " +
                               aDeg.ToString("0.###", CultureInfo.InvariantCulture) + "°)";

                object profile = null, holeData = null, feat = null;
                for (int attempt = 0; ; attempt++)
                {
                    // 自愈重试前清理上次残骸:hd 趁 RCW 活着先删,再特征,再草图(顺序不能反——
                    // 删 Hole 特征会断连其 HoleData;删特征后轮廓内容不可再碰,只盲删 ProfileSet)
                    if (profile != null)
                    {
                        TryDelete(holeData);
                        TryDelete(feat);
                        TryDelete(TryGetProfileSet(profile));
                        profile = holeData = feat = null;
                    }
                    try
                    {
                        profile = CreatePolarHoleProfile(doc, plane, spec, px, py, pi, name, angles.Count, specWarnings);
                        holeData = CreateHoleData(doc, thread, spec.ThreadOption, diameterM, specWarnings);
                        feat = AddHole(holesColl, profile, side, mode, depth, holeData);
                    }
                    catch (Exception ex)
                    {
                        TryDelete(holeData);
                        if (profile != null)
                        {
                            if (feat != null) TryDelete(feat);
                            TryDelete(TryGetProfileSet(profile));
                        }
                        RollbackBuiltHoles(built);
                        throw new Exception("[polarhole] " + where + " 建孔失败: " + ex.Message, ex);
                    }

                    long? st = SafeLong(Get(feat, "Status"));
                    if (ClassifyFeatureStatus(st) == FeatureHealth.Zombie && canHealSide && attempt == 0)
                    {
                        side = side == 1 ? 2 : 1;
                        specWarnings.Add(where + " 首试 6311 僵尸(0 切料),方向自愈:side 换成 " + side + " 重试。");
                        continue;
                    }
                    if (ClassifyFeatureStatus(st) == FeatureHealth.Zombie)
                    {
                        TryDelete(holeData);
                        TryDelete(feat);
                        TryDelete(TryGetProfileSet(profile));
                        RollbackBuiltHoles(built);
                        return new
                        {
                            op = "polarhole",
                            name = name,
                            status = "error",
                            resolved = new
                            {
                                plane = spec.PlaneRef,
                                mode = mode,
                                side = side,
                                boltdiaMm = Math.Round(spec.BoltDia.Value * 1000.0, 4),
                                angles = angles
                            },
                            message = where + "打孔未生成几何(6311 僵尸),已整体回滚(含前 " + pi + " 个已成功孔位);" +
                                      "常见原因:孔位落在毛坯之外、与已有孔完全重合、plane 没贴到实体上、side 方向不对(可显式给 side=1|2)。",
                            diagnosis = "孔没切到实体:核对孔位是否在实体范围内、plane 是否选对。",
                            fix = new { action = "fix_and_retry", check = new[] { "boltdia", "angles", "plane", "side" } }
                        };
                    }
                    break;   // 成功
                }

                // 2026-11-06:多孔位时第 2+ 个孔也落名 {name}_{序号}(首孔由 FeatureResult 落 op 名)。
                // 此前只有首孔进 FeatureResult,其余保持 Hole_N 默认名——replaceOf 按名重建时删不全。
                if (pi > 0 && !string.IsNullOrWhiteSpace(name) && angles.Count > 1)
                {
                    try { Put(feat, "Name", name.Trim() + "_" + (pi + 1).ToString(CultureInfo.InvariantCulture)); }
                    catch (Exception exN) { specWarnings.Add("孔位命名失败(" + exN.Message + "),保留系统名。"); }
                }
                string fname = SafeString(Get(feat, "Name"));
                string hid = null;
                if (pi == 0)
                {
                    firstProfile = profile;
                    firstFeat = feat;
                    firstFeatName = fname;
                }
                else
                {
                    hid = context.AddHandle(feat, "Hole", fname);
                }
                pointRows.Add(new { feature = fname, handle = hid, angleDeg = aDeg, x = px, y = py, r = radiusM });
                built.Add(new[] { feat, holeData, profile });
            }

            double? v1 = TryModelVolume(doc);
            double deltaMm3 = (v0.HasValue && v1.HasValue) ? (v1.Value - v0.Value) * 1e9 : double.NaN;

            object resolvedInfo = new
            {
                plane = spec.PlaneRef,
                mode = mode,
                side = side,
                depth = mode == "finite" ? depth : (double?)null,
                boltdiaMm = Math.Round(spec.BoltDia.Value * 1000.0, 4),
                holeDiameterMm = Math.Round(diameterM * 1000.0, 4),
                holeCount = angles.Count,
                points = pointRows,
                positioning = "全约束极坐标定位(轴框架 X/Y 重合构造线+定位线 KeyFix+连孔心,R/角度双驱动尺寸引用框架 X 线,IsUnderDefined=False);" +
                              "定位尺寸在变量表 " + (string.IsNullOrWhiteSpace(name) ? "polarhole" : name.Trim()) +
                              "_R/_Angle(多孔位加序号);角度尺寸是幅值,方向随首建几何侧",
                replaced = replaceInfo,
                thread = thread == null ? null : new
                {
                    standard = thread.Standard,
                    subtype = thread.Subtype,
                    size = thread.Size,
                    option = ThreadRow.OptionName(spec.ThreadOption),
                    nominalMm = thread.NominalMm,
                    tapDrillMm = thread.DrillMm,
                    minorMm = thread.MinorMm,
                    pitchMm = thread.PitchMm
                },
                volumeDeltaMm3 = double.IsNaN(deltaMm3) ? (double?)null : deltaMm3,
                note = angles.Count > 1
                    ? "每个角度=独立 Hole 特征+独立全约束定位草图+独立 HoleData(工程图可逐孔识别)"
                    : null
            };

            var volFail = CheckVolumeExpectation(doc, spec, firstProfile, firstFeat, v0, v1, "polarhole", name, resolvedInfo, specWarnings);
            if (volFail != null)
            {
                RollbackBuiltHoles(built, skipFirst: true);
                specWarnings.Add("体积对账失败回滚后,HoleData 记录可能残留(特征删除会断连它,已尽力删除);孔建议别配 expectvolumedelta。");
                return volFail;
            }

            if (mode == "next")
                specWarnings.Add("mode=next(切到下一面),孔的深度由几何决定;要精确深度请用 mode:finite + depth。");

            return FeatureResult("polarhole", name, firstFeat, context, firstProfile, null, specWarnings, resolvedInfo);
        }

        /// <summary>
        /// polarhole 单孔位轮廓(全约束):轴框架(2 条 X/Y 重合构造线) + 定位线 + 8 关系 + 2 驱动尺寸,全部在 End 前完成。
        /// 参照用户手画样板 Hole_20 的 UI 解码(2026-10-24)与真机复刻 Hole_26 配方(2026-10-25)。
        /// 定位尺寸自动入变量表 {featName}_R / {featName}_Angle(多孔位加 1 起序号),失败仅 warning。
        /// </summary>
        private static object CreatePolarHoleProfile(object doc, object plane, FeatureSpec spec,
            double px, double py, int pointIndex, string featName, int angleCount, List<string> warnings)
        {
            var docT = (SolidEdgePart.PartDocument)doc;
            var planeT = (SolidEdgePart.RefPlane)plane;
            SolidEdgePart.Profile profile = docT.ProfileSets.Add().Profiles.Add(planeT);

            if (spec.FacePlaneRef != null && !spec.CoordsLocal)
            {
                var map = GeometryTools.FacePlaneMap.BuildFromProfile(profile);
                double[] mapped = map.Map(px, py);
                px = mapped[0];
                py = mapped[1];
                if (warnings != null)
                    warnings.Add("face 平面:外法向轴 = " + map.NormalAxis + ",孔位按全局 (" +
                                 (map.NormalAxis == "Z" ? "X,Y" : (map.NormalAxis == "X" ? "Y,Z" : "X,Z")) + ") 解释。");
            }

            var lines = (SolidEdgeFrameworkSupport.Lines2d)profile.Lines2d;

            // 统一轴框架(2026-11-05 规矩):先画 X/Y 重合构造线(全约束),角度基准 = frameX——
            // 替代 COM 摸不到的隐式 H 轴,也是全项目唯一定位基准。框架失败(仅 warning)时退回自画基线老配方。
            var (frameX, frameY) = DrawSketchAxisFrame(profile, warnings);
            object angleBase = frameX;                       // 角度尺寸引用的基准线(X 轴重合框架线)
            if (angleBase == null)
            {
                var l2fb = lines.AddBy2Points(0.0, 0.0, SketchFrameLen, 0.0);
                profile.ToggleConstruction(l2fb);
                var relFb = (SolidEdgeFrameworkSupport.Relations2d)profile.Relations2d;
                relFb.AddKeypointFix(l2fb, 0, Type.Missing);
                relFb.AddHorizontal(l2fb, Type.Missing);
                relFb.AddKeypointFix(l2fb, 1, Type.Missing);
                angleBase = l2fb;
            }

            var l1 = lines.AddBy2Points(0.0, 0.0, px, py);          // 定位线:原点→孔心
            profile.Holes2d.Add(px, py);
            var hs = (SolidEdgePart.Holes2d)profile.Holes2d;
            var h2 = (SolidEdgePart.Hole2d)hs.Item(hs.Count);
            // 定位线必须 ToggleConstruction——普通线会被当成轮廓边界,孔特征消费轮廓时 6311 僵尸(2026-10-24 实测)
            profile.ToggleConstruction(l1);

            var rel = (SolidEdgeFrameworkSupport.Relations2d)profile.Relations2d;
            rel.AddKeypointFix(l1, 0, Type.Missing);        // 定位线起点锚定原点(等效 UI 的隐式轴线连接)
            rel.AddKeypoint(l1, 1, h2, 0, Type.Missing);    // 定位线尾端 = 孔圆心
            // (原角度基准线的起点共点/水平/尾端固定三条约束,已随轴框架进 DrawSketchAxisFrame)

            var dims = (SolidEdgeFrameworkSupport.Dimensions)profile.Dimensions;
            var dLen = dims.AddLength(l1);
            dLen.Constraint = true;                          // 驱动化(缺了 Edit/重算静默无效,2026-09-13 实测)
            var dAng = dims.AddAngleBetweenObjects(l1, px, py, 0.0, true, angleBase, SketchFrameLen, 0.0, 0.0, true);
            // SE 按「ele1 逆时针→ele2」取角:孔在 +X 下方量出劣角(如 45°)、上方量出优角(如 345°)。
            // 统一取 ≤180° 劣角:超了就删掉换元素顺序重标(两方向之和为 360°,必有一个 ≤180°)。
            if (dAng.Value > Math.PI + 1e-9)
            {
                dAng.Delete();
                dAng = dims.AddAngleBetweenObjects(angleBase, SketchFrameLen, 0.0, 0.0, true, l1, px, py, 0.0, true);
            }
            dAng.Constraint = true;

            // 定位尺寸入变量表(参数化入口;改 R/角度变量重算孔随动——真机实测 2026-10-25)
            try
            {
                var varsT = (SolidEdgeFramework.Variables)((SolidEdgePart.PartDocument)doc).Variables;
                string baseName = string.IsNullOrWhiteSpace(featName) ? "polarhole" : featName.Trim();
                string suffix = angleCount > 1 ? (pointIndex + 1).ToString(CultureInfo.InvariantCulture) : "";
                try { varsT.PutName(dLen, baseName + "_R" + suffix); }
                catch (Exception ex) { if (warnings != null) warnings.Add("定位半径尺寸命名失败(" + ex.Message + "),保留系统名。"); }
                try { varsT.PutName(dAng, baseName + "_Angle" + suffix); }
                catch (Exception ex) { if (warnings != null) warnings.Add("定位角度尺寸命名失败(" + ex.Message + "),保留系统名。"); }
            }
            catch (Exception ex) { if (warnings != null) warnings.Add("变量表不可用(" + ex.Message + "),定位尺寸未命名。"); }

            // 全约束自检:欠约束说明关系/尺寸没挂上——孔位虽准,但违背"完全约束"承诺,诚实上报
            try
            {
                object psObj = TryGetProfileSet(profile);
                bool under = (bool)Get(psObj, "IsUnderDefined");
                if (under && warnings != null)
                    warnings.Add("极坐标定位约束未收敛(IsUnderDefined=true):孔位按坐标放置,但草图有剩余自由度,请检查。");
            }
            catch { /* 读不到就不判,别挡建模 */ }

            profile.End((SolidEdgePart.ProfileValidationType)1);
            SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建 HoleData(孔数据)。光孔与螺纹孔同一个入口:
        ///   光孔  = 规则孔 + 显式孔径(TreatmentType=igNone);
        ///   螺纹孔= 规则孔几何 + 螺纹数据(TreatmentType=igTappedHole=37)。
        ///
        /// ★ 螺纹孔的表达方式是从 UI 建模的 M24 孔反查出来的:HoleType 仍是 33(不是 37!37 会被
        ///   静默夹成 36 沉头钻),靠 TreatmentType=37 + ThreadSetting(164/165/166)+ 螺纹四径 标识。
        ///   实际孔径由 ThreadDiameterOption 决定(实测 option=minor 时模型量到 Ø8.376 = M10 小径)。
        /// </summary>
        private static object CreateHoleData(object doc, ThreadRow thread, string threadOption,
            double diameterM, List<string> warnings)
        {
            // ★ 2026-10-24 修复:全强类型(与建轮廓同轮修复;晚绑定派生对象虽然可强转,
            //   但统一走 PIA 链最稳——V3a 实证强转后 Add 可行,全强类型 V1 实证一次成功)。
            var hdcTyped = (SolidEdgePart.HoleDataCollection)((SolidEdgePart.PartDocument)doc).HoleDataCollection;

            double holeDiaM = thread != null ? thread.NominalMm / 1000.0 : diameterM;
            double minorM = (thread != null && thread.MinorMm > 0) ? thread.MinorMm / 1000.0 : 0.0;
            double externalM = (thread != null && thread.ExternalMm > 0) ? thread.ExternalMm / 1000.0 : 0.0;

            // ★ 光孔与螺纹孔分开给参:命名实参的语义是"不需要就省略"(=标准缺省),
            //   传 0.0 凑位会被拒(E_INVALIDARG,真机 D 例实证)——螺纹径/描述只有螺纹孔才给。
            // ★ VBottomDimType=igVBottomDimToFlat(145) 是钻尖生效的【必要参数】:不给它
            //   BottomAngle=118 被静默忽略(盲孔变平底,2026-10-24 实测差 46.2mm³);
            //   给了之后"深度标到 V 平":圆柱 depth + 尖高 r/tan59°(贯穿时尖在板外无几何,体积不变)。
            //   实测闭环:Ø10×12 盲孔 = 942.48+78.54 = 1021.02(实测 1021.13);
            //   M10 小径盲孔 = 661.16+46.22 = 707.38(= handoff §1.4 判据 707.4)。
            SolidEdgePart.HoleData hd;
            if (thread == null)
            {
                hd = hdcTyped.Add(
                    (SolidEdgePart.FeaturePropertyConstants)33,   // igRegularHole
                    holeDiaM,
                    BottomAngle: 118.0,                            // 118° 标准钻尖
                    TreatmentType: (SolidEdgePart.FeaturePropertyConstants)44,  // igNone
                    VBottomDimType: (SolidEdgePart.FeaturePropertyConstants)145,  // igVBottomDimToFlat
                    IgnoreSavedDefaultValues: true);               // 别吃上次保存的默认值
                // 光孔标志 = TreatmentType=44(igNone)。★ ThreadSetting=0 【写不进去】——
                // 强类型 PROPERTYPUT 报 E_INVALIDARG(2026-10-24 实测:0/1/2/3 全拒、164 可写),
                // 但 Setting 保持默认 164 不影响几何:沙盒完整链路 Ø10 贯穿 = -1178.10 mm³ 精确命中。
                // (handoff §1.3"必须显式 0"源自晚绑定 TryPut 静默失败,该结论按实测修正。)
                try { hd.ThreadSetting = (SolidEdgePart.FeaturePropertyConstants)0; }
                catch (Exception ex)
                {
                    if (warnings != null)
                    {
                        // 剥掉异常文本里的符号名(如 "(E_INVALIDARG)"):只留十六进制码。op 文案/警告
                        // 不含 E 码字面量是 2026-11-08 定的纪律(run-op-tests 判据按 E 模式扫全输出,
                        // 本警告随每个光孔出现,会把 expect=ok 用例整批误杀——b1 实测归因 2026-11-10)。
                        string detail = System.Text.RegularExpressions.Regex.Replace(ex.Message, @"\s*\((E_[A-Z_]+)\)", "");
                        warnings.Add("光孔 ThreadSetting 无法写 0(" + detail + ");实测与几何无关" +
                                     "(TreatmentType=44 即光孔标志,默认 164 仅是标签,贯穿体积已精确对账)。");
                    }
                }
                return hd;
            }

            hd = hdcTyped.Add(
                (SolidEdgePart.FeaturePropertyConstants)33,   // igRegularHole(37 会被静默夹成 36,handoff §1.2)
                holeDiaM,
                BottomAngle: 118.0,                            // 118° 标准钻尖
                TreatmentType: (SolidEdgePart.FeaturePropertyConstants)37,  // igTappedHole
                VBottomDimType: (SolidEdgePart.FeaturePropertyConstants)145,  // igVBottomDimToFlat(钻尖生效必要,见上)
                ThreadMinorDiameter: minorM,
                ThreadExternalDiameter: externalM,
                ThreadDescription: thread.Size,
                IgnoreSavedDefaultValues: true);

            // 螺纹标注属性:强类型写;失败降级为 warning(几何由 Add 参数保证,标注缺了不该毁孔)。
            // ★ 写 Standard/SubType/Size 会触发 SE 按库重载默认值、把 BottomAngle 重置成 0
            //   (2026-10-24 Y1 实测:BA 118→0,钻尖消失 707.4→661.2)——所以属性写完【最后补写
            //   BottomAngle】,顺序不能反。
            try
            {
                hd.Standard = thread.Standard;
                hd.SubType = thread.Subtype;
                hd.Size = thread.Size;
                hd.ThreadNominalDiameter = thread.NominalMm / 1000.0;
                hd.ThreadTapDrillDiameter = thread.DrillMm / 1000.0;
                hd.ThreadSetting = (SolidEdgePart.FeaturePropertyConstants)thread.ThreadSetting;
                hd.ThreadDiameterOption = (SolidEdgePart.ThreadDiameterOptionConstants)ThreadOptionCode(threadOption);
                hd.BottomAngle = 118.0;   // ★ 库重载会清它,必须最后补(实测恢复后钻尖 707.44)
            }
            catch (Exception ex)
            {
                if (warnings != null)
                    warnings.Add("HoleData 螺纹标注属性部分写入失败(" + ex.Message + ");几何已按 Add 参数建成," +
                                 "但工程图螺纹标注回读可能不完整。");
            }

            if (thread.Kind == 3 && warnings != null)
                warnings.Add("锥管螺纹(" + thread.Size + "):SE 的锥孔要 AddSync 专用通道,本 op 建的是圆柱底孔 Ø" +
                             thread.DrillMm.ToString("0.###") + "。要锥孔请在 UI 里用「锥孔」类型。");

            return hd;
        }

        /// <summary>螺纹选项 → ThreadDiameterOptionConstants：0=丝锥直径、1=内螺纹小径(默认)、2=标称直径。</summary>
        private static int ThreadOptionCode(string option)
        {
            string o = (option ?? "").Trim().ToLowerInvariant();
            if (o == "tapdrill" || o == "tap_drill") return 0;
            if (o == "nominal") return 2;
            return 1;
        }

        /// <summary>按 mode 调 Hole 集合对应方法。贯穿两种都在 Holes 集合上(AddThroughAll/AddThroughNext)。</summary>
        private static object AddHole(object holesColl, object profile, int side, string mode, double depth, object holeData)
        {
            // ★ 2026-10-24 修复:改 PIA 强类型调用(双断点之二)。原实现晚绑定
            // Call(holesColl,"AddThroughAll",{profile, side, holeData}):profile/holeData 的对象引用
            // 封送报 CO_E_OBJNOTCONNECTED(真机 2026-10-24,2/2 稳定复现);同流程脚本通道强类型是通的。
            // 与 RevolveOp"晚绑定对 VT_USERDEFINED/SAFEARRAY 必挂"同族教训——本仓库 Add* 全走强类型。
            var holesTyped = (SolidEdgePart.Holes)holesColl;
            var profileTyped = (SolidEdgePart.Profile)profile;
            var dataTyped = (SolidEdgePart.HoleData)holeData;
            var sideConst = (SolidEdgePart.FeaturePropertyConstants)side;
            switch (mode)
            {
                case "finite":
                    return holesTyped.AddFinite(profileTyped, sideConst, depth, dataTyped);
                case "next":
                    return holesTyped.AddThroughNext(profileTyped, sideConst, dataTyped);
                default:
                    return holesTyped.AddThroughAll(profileTyped, sideConst, dataTyped);
            }
        }

        /// <summary>
        /// 孔用的轮廓:一个 profile 里画多个 Hole2d(孔位点)→ End(igProfileClosed=1)→ 隐藏。
        /// 注意孔轮廓画的是 Holes2d 不是 Circles2d——孔的直径由 HoleData 给,画真圆没有意义。
        /// face 锚定 + coords 缺省时,孔位按全局世界坐标投影到贴面平面的局部 u/v
        /// (与 CreateProfileForFace 同一条路)。
        /// </summary>
        private static object CreateHoleProfileForFeature(SolidEdgeContext context, object doc, object plane,
            FeatureSpec spec, double px, double py, bool visible, List<string> warnings)
        {
            // ★ 2026-10-24 第二轮修复:单点 profile——"一 profile 多 Hole2d"已实测不可靠
            //   (End 清点 8→1 / 不 End 僵尸,干净文档终审),多点由 HoleOp 逐点循环本函数。

            // 全强类型链建轮廓(与 se_script_run 实测成功的路径完全一致)。
            var docT = (SolidEdgePart.PartDocument)doc;
            var planeT = (SolidEdgePart.RefPlane)plane;
            SolidEdgePart.Profile profile = docT.ProfileSets.Add().Profiles.Add(planeT);

            if (spec.FacePlaneRef != null && !spec.CoordsLocal)
            {
                var map = GeometryTools.FacePlaneMap.BuildFromProfile(profile);
                double[] mapped = map.Map(px, py);
                px = mapped[0]; py = mapped[1];

                if (warnings != null)
                    warnings.Add("face 平面:外法向轴 = " + map.NormalAxis + ",孔位按全局 (" +
                                 (map.NormalAxis == "Z" ? "X,Y" : (map.NormalAxis == "X" ? "Y,Z" : "X,Z")) + ") 解释。");
            }

            // ★ 2026-10-24 boltdia 标准定位:孔位由 分布圆Ø+角度 计算坐标。
            //   【2026-10-25 订正】当年 R3~R13,11 轮的 6311 僵尸根因是 side 方向(side=2 沿平面法向
            //   朝空中打→0 切料),不是约束——全约束定位配方已实测可行并产品化为 polarhole op
            //   (构造线+Connect+KeyFix+驱动尺寸,AddThroughAll 一次 igFeatureOK)。本 op 维持
            //   【只落 Hole2d 坐标】的轻量路径,孔位由 boltdia/angleDeg 参数唯一确定;
            //   需要全约束定位草图(定位尺寸进变量表)请用 polarhole。
            profile.Holes2d.Add(px, py);
            if (spec.BoltDia > 0 && warnings != null)
                warnings.Add("标准定位(boltdia=" + spec.BoltDia.Value.ToString("0.####") + "m, angleDeg=" +
                             (spec.StartAngleDeg ?? 0).ToString("0.#") + "°)已换算为坐标 " +
                             px.ToString("0.####") + "," + py.ToString("0.####") +
                             "。孔位由坐标精确定义(草图无约束标注);要全约束定位(定位尺寸入变量表)请用 polarhole op。改孔位请重跑本 op。");

            profile.End((SolidEdgePart.ProfileValidationType)1);
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>写属性,失败不抛(次要参数写不进去不该毁掉整个建模)。</summary>
        private static bool TryPut(object obj, string name, object value)
        {
            try { Put(obj, name, value); return true; }
            catch { return false; }
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
        private static void DiscardCutCandidate(object profile, object featObj, bool keepSketch = false)
        {
            object profileSet = profile != null ? TryGetProfileSet(profile) : null;
            if (featObj != null) TryDelete(featObj);
            // 2026-10-10 修正: face 锚定场景【必须保留草图】(调用方传 keepSketch=true)。
            //
            // 实测根因(真机复现): 删掉"建在贴面 RefPlane 上的 ProfileSet"会把那张平面的 RCW
            // 弄成 CO_E_OBJNOTCONNECTED —— 而 SE 对该面后续再调 RefPlanes.AddParallelByDistance
            // 时会【复用同一张平面对象】返回,于是 Profiles.Add(plane) 直接报
            // IDispatch::Invoke 失败 [成员=Add] hr=0x800401FD(puArgErr=0, 即第一个参数 plane),
            // 该 Face.ID 从此废掉(同文档内其它面完全正常,污染按面隔离)。
            // 且贴面平面【删不掉】(RefPlane.Delete 实测抛 0x80070057),残留无法自行清除。
            // 所以唯一出路就是别把它的 RCW 弄断: 草图保持隐藏留存即可
            // (CreateProfileForFace 末尾已 SetVisible(profile,false),不会浮在图形区;
            //  与 §十二 "贴面平面当前无害、暂不自动清理" 的既有拍板一致)。
            if (profileSet != null && !keepSketch) TryDelete(profileSet);
        }

        /// <summary>
        /// 本特征是否走 face 锚定(plane:"face:…")。用于决定失败回滚时是否保留草图 ——
        /// 见 DiscardCutCandidate 的说明: face 锚定的草图一删就会弄断贴面平面的 RCW。
        /// </summary>
        private static bool IsFaceAnchored(FeatureSpec spec)
        {
            return spec != null && spec.PlaneRef != null &&
                   spec.PlaneRef.StartsWith("face:", StringComparison.OrdinalIgnoreCase);
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
            // 2026-11-06 修复:revolve 此前不处理 replaceOf(静默忽略)——法兰重建实测踩坑,重建语义补齐(通用规则 12)
            var replaceInfo = ReplaceNamedFeatures(doc, spec.ReplaceOf, specWarnings);
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
                new { plane = spec.PlaneRef, side = planeSide, profileside = profileSide, angle = angle, mode = isRevolveCut ? "cut" : "protrusion", replaced = replaceInfo });
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
                // 判定走统一分类函数:只要不是僵尸(几何未生成)就算这个方向成了——
                // Warned(6312)/Suppressed(6313)/RolledBack(6314) 都意味着特征存在,不该换侧重试。
                long? st = SafeLong(Get(feat, "Status"));
                if (ClassifyFeatureStatus(st) != FeatureHealth.Zombie) { usedMs = candidate; break; }
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
        /// 圆周阵列(2026-10-23 开放;矩形阵列仍未开放,见 PatternRule 的 xcount 报错)。
        ///
        /// 链路(2026-10-23 真机复刻验证,官方 Patterns.Add 示例同构):
        ///   ① doc.ProfileSets.Add().Profiles.Add(plane)          —— 载体轮廓(不画任何 2D 几何)
        ///   ② profile.CircularPatterns2d.AddByCircle(cx, cy,
        ///        startX, startY, 逆/顺时针, Fit|Fixed, count, angularSpacing)
        ///   ③ model.Patterns.Add(1, ref {种子特征}, profile, seSmart|seFastPattern)
        ///   ④ profile.Visible = false
        ///
        /// ★ 几何语义(2026-10-23 真机实测,零件9 复刻实验):
        ///   · 实例位置 = 种子特征绕"过 center 的平面法向轴"旋转 k×spacing;起始角由种子自身位置决定。
        ///   · 阵列圆弧(Arc2d)的半径与起始角【纯视觉】,不参与实例定位——原件弧 R=35.4517 而实例全在
        ///     种子所在的 R=35 上即为此故。radius 字段只控制草图里那个圆的显示大小。
        ///   · plane 的法向 = 旋转轴(RefPlane_1 → Z 轴);count 含种子自身(8 个实例 = 种子 + 7 个新实例)。
        ///   · "fit"(默认)= 整圈均布,AngularSpacing 自动 360/count;spacingDeg = 固定角间距(Fixed)。
        ///   · ★ 阵列圆心(2026-10-25 五次修订):CP2d.GetGeometry() 可直接拿内部弧对象(旧"内部弧
        ///     COM 不可约束"结论已推翻),圆心约束三级自动选型——①文档里有可借的"平面级隐式轴线"
        ///     (只在 UI 草图编辑过的平面存在,见 FindDonorAxis)→ 直连 Connect(与 UI 画法同型,零附加
        ///     几何,仅 center=(0,0) 可用);②辅助构造线+Connect(无轴线文档默认档,连接语义,任意
        ///     center,与 polarhole 定位草图同款;用户 2026-10-25 拍板:宁要连接+辅助线,不要固定);
        ///     ③KeyFix 弧心原地固定兜底(零附加几何,Recompute 存活已验证)。centerConstraint 回显
        ///     实际档位;改圆心仍建议 replaceOf 重建。种子定位精度由种子特征自身保证(推荐 polarhole)。
        /// </summary>
        /// <summary>
        /// 圆周螺栓孔群(2026-10-25 新增 op):一条声明 = 【1 个种子孔 + 1 个圆周阵列】。
        ///
        /// 为什么要有这个 op:
        ///   用 polarhole 的 angles[] 一次给 8 个角度,建出来的是 8 个<b>独立 Hole 特征</b>——
        ///   孔数从此改不了(只能删了重建),还会撞 W401"同平面多次除料必僵尸"的告警。
        ///   正确结构是"1 个种子孔 + 1 个圆周阵列",改孔数只需改阵列实例数
        ///   (se_set_feature_parameter parameter=count)。本 op 就是把这个结构做成一条声明,
        ///   从工具层保证"孔数天生可改",不会退化成 N 个独立孔。
        ///
        /// 内部<b>完全复用</b> PolarHoleOp / PatternOp,不新增任何 COM 调用路径,
        /// 所以崩溃面、方向自愈、僵尸回滚等既有行为与手写两段完全一致。
        /// </summary>
        private static object BoltCircleOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, double[]> planeNormals,
            Dictionary<int, object> facePlanes, Dictionary<string, object> namedFeatures)
        {
            int count = spec.PatCount ?? 0;
            if (count < 2)
            {
                return new
                {
                    op = "boltcircle", name, status = "error",
                    message = "boltcircle 需要 count(孔数,含种子孔,必须 >= 2),收到 "
                              + (spec.PatCount.HasValue ? spec.PatCount.Value.ToString() : "(未给)") + "。",
                    fix = new { action = "set", field = "count", example = 8 }
                };
            }
            if (!(spec.BoltDia > 0))
            {
                return new
                {
                    op = "boltcircle", name, status = "error",
                    message = "boltcircle 需要 boltdia(螺栓分布圆直径,米),如 0.182 = Ø182 分布圆。",
                    fix = new { action = "set", field = "boltdia", example = 0.182 }
                };
            }
            if (!(spec.Diameter > 0) && string.IsNullOrWhiteSpace(spec.Thread))
            {
                return new
                {
                    op = "boltcircle", name, status = "error",
                    message = "boltcircle 需要 diameter(孔径,米)或 thread(螺纹规格,如 M10)。",
                    fix = new { action = "provide", field = "diameter | thread" }
                };
            }

            // 起始角缺省 = 半个齿距(如 8 孔 → 22.5°):
            //   ① 0°/180° 与角度基准线共线,定位尺寸退化,polarhole 会直接拒;
            //   ② 半个齿距让孔跨中心线对称分布(标准件画法)。
            double startDeg = spec.StartAngleDeg ?? (360.0 / count / 2.0);
            string baseName = string.IsNullOrWhiteSpace(name) ? "Bolts" : name.Trim();

            // 2026-11-06 修复:boltcircle 此前不处理顶层 replaceOf(静默忽略;内部种子/阵列的
            // ReplaceOf 一律置 null 防连挂)——重建语义补齐(通用规则 12)。删旧件在种子孔之前。
            var bcWarnings = new List<string>();
            var replaceInfo = ReplaceNamedFeatures(doc, spec.ReplaceOf, bcWarnings);

            // 种子孔:直接把外层 spec 的必要字段搬过去,角度走单孔路径(angles 必须清空,
            // 否则 PolarHoleOp 会按 angles[] 建出 N 个独立孔——正是本 op 要消灭的形态)。
            var seedSpec = new FeatureSpec
            {
                Index = spec.Index,
                Op = "polarhole",
                OpLower = "polarhole",
                Name = baseName,
                PlaneRef = spec.PlaneRef,
                FacePlaneRef = spec.FacePlaneRef,
                Coords = spec.Coords,
                CoordsLocal = spec.CoordsLocal,
                BoltDia = spec.BoltDia,
                StartAngleDeg = startDeg,
                AnglesDeg = null,
                Diameter = spec.Diameter,
                Thread = spec.Thread,
                Standard = spec.Standard,
                ThreadOption = spec.ThreadOption,
                Mode = spec.Mode,
                Side = spec.Side,
                Dir = spec.Dir,
                HasDir = spec.HasDir,
                ReplaceOf = null
            };

            var patSpec = new FeatureSpec
            {
                Index = spec.Index,
                Op = "pattern",
                OpLower = "pattern",
                Name = baseName + "_Pattern",
                PlaneRef = spec.PlaneRef,
                FacePlaneRef = spec.FacePlaneRef,
                Coords = spec.Coords,
                CoordsLocal = spec.CoordsLocal,
                PatCount = count,
                Center = spec.Center,
                Spacing = "fit",
                SpacingFit = true,
                SpacingDeg = null,
                Clockwise = spec.Clockwise,
                PatternType = string.IsNullOrWhiteSpace(spec.PatternType) ? "smart" : spec.PatternType,
                Radius = spec.Radius,
                ReplaceOf = null
            };

            double? v0 = TryModelVolume(doc);

            object seedResult = PolarHoleOp(context, doc, seedSpec, seedSpec.Name, namedPlanes, planeNormals, facePlanes);
            if (!IsOk(seedResult))
            {
                // 种子孔失败:原样透出它的诊断(方向自愈/孔位相切等原因都在里面)
                return seedResult;
            }

            string seedFeature = ResultString(seedResult, "feature");
            if (string.IsNullOrEmpty(seedFeature))
            {
                return new
                {
                    op = "boltcircle", name, status = "error", seed = seedResult,
                    message = "种子孔已建,但拿不到它的 SE 特征名,无法挂圆周阵列(种子孔已保留,请人工核对)。"
                };
            }

            patSpec.Of = seedFeature;
            object patResult = PatternOp(context, doc, patSpec, patSpec.Name, namedPlanes, namedFeatures);

            if (!IsOk(patResult))
            {
                // 阵列失败 → 回滚种子孔,不留半成品(否则调用方重试会出现重复孔)
                var rollbackLog = new List<string>();
                ReplaceNamedFeatures(doc, seedFeature, rollbackLog);
                return new
                {
                    op = "boltcircle", name, status = "error",
                    seed = new { feature = seedFeature },
                    pattern = patResult,
                    rollback = rollbackLog,
                    message = "种子孔已建,但圆周阵列失败,已回滚种子孔(不留半成品)。失败原因见 pattern 字段。",
                    fix = new { action = "fix_and_retry", check = new[] { "boltdia", "count", "plane" } }
                };
            }

            string patFeature = ResultString(patResult, "feature");
            double? v1 = TryModelVolume(doc);

            return new
            {
                op = "boltcircle",
                name,
                status = "ok",
                seed = new
                {
                    feature = seedFeature,
                    handle = ResultString(seedResult, "handle"),
                    angleDeg = Math.Round(startDeg, 4)
                },
                pattern = new
                {
                    feature = patFeature,
                    handle = ResultString(patResult, "handle"),
                    count
                },
                resolved = new
                {
                    plane = spec.PlaneRef,
                    boltdiaMm = Math.Round(spec.BoltDia.Value * 1000.0, 4),
                    count,
                    pitchDeg = Math.Round(360.0 / count, 4),
                    holeDiameterMm = spec.Diameter.HasValue ? (double?)Math.Round(spec.Diameter.Value * 1000.0, 4) : null,
                    thread = spec.Thread,
                    mode = spec.Mode,
                    side = spec.Side,
                    startAngleDeg = Math.Round(startDeg, 4),
                    replaced = replaceInfo
                },
                warnings = bcWarnings.Count > 0 ? bcWarnings : null,
                volumeDeltaMm3 = (v0.HasValue && v1.HasValue) ? (double?)Math.Round((v1.Value - v0.Value) * 1e9, 3) : null,
                handle = ResultString(patResult, "handle"),
                hint = "孔数可改:se_set_feature_parameter(feature='" + patFeature + "', parameter='count', value=N),"
                       + "整圈均布会自动重排(spacing=fit)。改分布圆/起始角请改种子孔的 Bolt_R / Bolt_Angle 变量。"
            };
        }

        /// <summary>读结果匿名对象上的字符串字段(feature/handle 等)。取不到返回 null,不抛。</summary>
        private static string ResultString(object result, string member)
        {
            try
            {
                if (result == null) return null;
                var p = result.GetType().GetProperty(member);
                return p == null ? null : p.GetValue(result) as string;
            }
            catch
            {
                return null;
            }
        }

        private static object PatternOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, object> namedFeatures)
        {
            // 2026-11-08:kind:"rect" 走矩形阵列分支(独立方法,圆周链路零改动)
            if (string.Equals(spec.Kind, "rect", StringComparison.OrdinalIgnoreCase))
                return RectPatternOp(context, doc, spec, name, namedPlanes, namedFeatures);

            int count = spec.PatCount ?? 0;
            if (count < 2)
                return new { op = "pattern", name = name, status = "error",
                    message = "圆周阵列必须提供 count(实例总数,含输入特征,>= 2)。" };
            if (string.IsNullOrWhiteSpace(spec.Of))
                return new { op = "pattern", name = name, status = "error",
                    message = "pattern 必须提供 of(被阵列特征:本批前面特征的 name、SE 特征名或 obj-K 句柄)。" };

            // 1) 种子特征解析:obj-K 句柄 → 本批 name → 模型特征集合按 SE 名匹配
            //    2026-10-24 替换语义:先删 replaceOf 名单里的旧特征(改参数重跑场景)
            // 2026-11-06 修复:此前传 warnings=null,"未找到/删除失败"全被吞(replaceOf 静默空删帮凶)。
            var specWarningsP = new List<string>();
            var replaceInfo2 = ReplaceNamedFeatures(doc, spec.ReplaceOf, specWarningsP);
            string seedErr;
            object seedFeat = ResolvePatternSeed(context, doc, spec.Of, namedFeatures, out seedErr);
            if (seedFeat == null)
                return new { op = "pattern", name = name, status = "error", message = seedErr };

            // 2) 载体平面(法向 = 旋转轴)
            object plane;
            try { plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes); }
            catch (Exception ex) { return new { op = "pattern", name = name, status = "error", message = ex.Message }; }

            // 3) 模型与 Patterns 集合
            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "pattern", name = name, status = "error",
                    message = "没有模型实体——pattern 要阵列已有特征,先 extrude/cut 建出被阵列的特征。" };
            object model = Get(models, "Item", 1);
            object patterns = Get(model, "Patterns");
            int countBefore = Count(patterns);

            // 4) 参数
            double cx = 0, cy = 0;
            if (spec.Center != null) { cx = spec.Center[0]; cy = spec.Center[1]; }
            double visRadius = spec.Radius ?? 0.05;                 // 阵列圆显示半径(纯视觉,默认 50mm)
            bool clockwise = spec.Clockwise == true;
            bool fit = spec.SpacingFit && !spec.SpacingDeg.HasValue;
            double spacingRad = fit
                ? 2.0 * Math.PI / count                              // fit:360/count
                : spec.SpacingDeg.Value * Math.PI / 180.0;           // fixed:spacingDeg 度→弧度
            bool fast = string.Equals(spec.PatternType, "fast", StringComparison.OrdinalIgnoreCase);

            // 5) 载体轮廓 + 2D 圆周阵列定义(PIA 强类型:枚举参数晚绑定会静默 null,同 RevolveOp 教训)
            object profileSet = Call(Get(doc, "ProfileSets"), "Add", null);
            object profile = Call(Get(profileSet, "Profiles"), "Add", new object[] { plane });
            var profileTyped = (SolidEdgePart.Profile)profile;

            // specWarningsP 里已带 replaceOf 删除警告(2026-11-06):两段共用同一列表,一起回显
            var specWarnings = specWarningsP;
            DrawSketchAxisFrame(profile, specWarnings);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

            var orient = clockwise
                ? SolidEdgeFrameworkSupport.Geom2dOrientationConstants.igGeom2dOrientClockwise
                : SolidEdgeFrameworkSupport.Geom2dOrientationConstants.igGeom2dOrientCounterClockwise;
            var offsetType = fit
                ? SolidEdgeFrameworkSupport.PatternOffsetTypeConstants.sePatternFitOffset
                : SolidEdgeFrameworkSupport.PatternOffsetTypeConstants.sePatternFixedOffset;
            // 起始点取 (cx+R, cy)(0°)——起始角纯视觉,实例从种子所在角开始
            SolidEdgeFrameworkSupport.CircularPattern2d cp2d = profileTyped.CircularPatterns2d.AddByCircle(
                cx, cy, cx + visRadius, cy, orient, offsetType, count, spacingRad);

            // 6) Patterns.Add(PIA 强类型:ref Array + 枚举;官方签名 Add(N, ref FeatureArray, Profile, PatternType))
            Array featArr = new object[] { seedFeat };
            var patTyped = ((SolidEdgePart.Patterns)patterns).Add(1, ref featArr, profileTyped,
                fast ? SolidEdgePart.PatternTypeConstants.seFastPattern
                     : SolidEdgePart.PatternTypeConstants.seSmartPattern);

            SetVisible(profile, false);

            // 7) 回读验证:实例数 / 角间距 / 阵列圆心 / 集合增长(FinishEdgeFeature)
            int? occurrences = null; double? spacingBack = null; int? offsetBack = null;
            double[] centerBack = null;
            try
            {
                occurrences = Convert.ToInt32(Get(patTyped, "NumberOfOccurrences"));
                spacingBack = Convert.ToDouble(Get(cp2d, "AngularSpacing"));
                offsetBack = Convert.ToInt32(Get(cp2d, "OffsetType"));
                // ★ 阵列圆心回读:CP2d.GetGeometry() 拿内部弧(2026-10-25 实测可达),
                //   回读确认圆心没跑;圆心 Connect 约束的附加见第 8 步。
                var arcT = (SolidEdgeFrameworkSupport.Arc2d)Call(cp2d, "GetGeometry", null);
                double ccx, ccy; arcT.GetCenterPoint(out ccx, out ccy);
                centerBack = new[] { ccx, ccy };
            }
            catch { /* 回读失败不拦截,由集合增长 + Status 校验兜底 */ }

            // 8) ★ 圆心约束三级自动选型(2026-10-25 五次修订,用户拍板:无轴线文档默认连接语义):
            //    ① donor 轴线直连:全库扫描可借的"平面级隐式轴线"(FindDonorAxis;该对象只在 UI 草图
            //       编辑过的平面存在,COM 无法凭空创建),AddKeypoint(弧心 kp0 ↔ 轴线 kp0) —— 与用户
            //       UI 手工挂的 Connect(768508992)同型,零附加几何。仅当 center=(0,0)(=轴线 kp0 位置)
            //       时可用,否则会把圆心拽到原点;连完回读关系计数+圆心,异常删关系自愈降级 ②。
            //    ② 辅助线 Connect(无轴线文档的默认档):构造线+KeyFix×2+Horizontal+
            //       Connect(弧心↔线起点,768508992)——连接语义,与 polarhole 定位草图同款脚手架,
            //       任意 center 可用(用户 2026-10-25 拍板:宁要连接+辅助线,不要零几何的固定)。
            //    ③ KeyFix 弧心兜底:AddKeypointFix(弧,kp0) 原地固定(固定语义,零附加几何);
            //       整档 Recompute 存活(2026-10-25 真机验证)。仅 ② 失败时使用。
            string centerConstraint = null;
            try
            {
                var arcC = (SolidEdgeFrameworkSupport.Arc2d)Call(cp2d, "GetGeometry", null);
                var relsT = (SolidEdgeFrameworkSupport.Relations2d)profileTyped.Relations2d;
                int relsBefore = relsT.Count;

                // ① donor 轴线直连(仅 center=原点时;轴线 kp0 就是原点)
                if (Math.Abs(cx) < 1e-9 && Math.Abs(cy) < 1e-9)
                {
                    string axisKey;
                    object axis = FindDonorAxis(doc, out axisKey);
                    if (axis != null)
                    {
                        try
                        {
                            var conn = relsT.AddKeypoint(arcC, 0, axis, 0);
                            double dxx, dyy; arcC.GetCenterPoint(out dxx, out dyy);
                            if (relsT.Count == relsBefore + 1 &&
                                Math.Abs(dxx - cx) < 1e-6 && Math.Abs(dyy - cy) < 1e-6)
                            {
                                centerConstraint = "direct(Connect type=" + Convert.ToInt64(Get(conn, "Type")) +
                                    ": 弧心 kp0 ↔ 隐式轴线 kp0[Key=" + axisKey + "],与 UI 画法同型,零附加几何)";
                            }
                            else
                            {
                                // 借到的是异面轴线/求解异常:删掉自愈,降级 ②
                                TryDelete(conn);
                                double rx, ry; arcC.GetCenterPoint(out rx, out ry);
                                if (Math.Abs(rx - cx) > 1e-6 || Math.Abs(ry - cy) > 1e-6)
                                    specWarnings.Add("donor 轴线直连后圆心漂移且删除未复位(" +
                                        rx.ToString("0.####") + "," + ry.ToString("0.####") + "),请目检阵列圆心。");
                            }
                        }
                        catch { /* 借线失败降级 ② */ }
                    }
                }

                // ② 辅助线 Connect(默认档:连接语义,同 polarhole 定位草图;长度对齐统一框架常量 SketchFrameLen)
                if (centerConstraint == null)
                {
                    var aux = profileTyped.Lines2d.AddBy2Points(cx, cy, cx + SketchFrameLen, cy);
                    profileTyped.ToggleConstruction(aux);
                    relsT.AddKeypointFix(aux, 0, Type.Missing);
                    relsT.AddKeypointFix(aux, 1, Type.Missing);
                    relsT.AddHorizontal(aux, Type.Missing);
                    var conn = relsT.AddKeypoint(arcC, 0, aux, 0);
                    centerConstraint = "attached(Connect type=" + Convert.ToInt64(Get(conn, "Type")) +
                                       ": 弧心 kp0 ↔ 辅助线原点端 kp0,同用户 UI 画法)";
                }

                // ③ KeyFix 弧心兜底(零附加几何,固定语义;仅 ② 失败时)
                if (centerConstraint == null)
                {
                    try
                    {
                        var kf = relsT.AddKeypointFix(arcC, 0, Type.Missing);
                        double kx, ky; arcC.GetCenterPoint(out kx, out ky);
                        if (relsT.Count == relsBefore + 1 &&
                            Math.Abs(kx - cx) < 1e-6 && Math.Abs(ky - cy) < 1e-6)
                            centerConstraint = "keyfix(弧心原地固定,零附加几何;兜底档,Recompute 存活 2026-10-25 真机验证)";
                        else
                            TryDelete(kf);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                centerConstraint = "failed(" + ex.Message + ")";
                specWarnings.Add("阵列圆心约束附加失败(" + ex.Message +
                                 ")——圆心仅为数值定位,建议在 UI 编辑定义里手工补一条圆心 Connect。");
            }

            if (occurrences.HasValue && occurrences.Value != count)
                specWarnings.Add("回读 NumberOfOccurrences=" + occurrences.Value + " 与声明 count=" + count +
                                 " 不一致,请目检(se_read_model_state / 截图)确认实例数。");
            if (centerBack != null &&
                (Math.Abs(centerBack[0] - cx) > 1e-6 || Math.Abs(centerBack[1] - cy) > 1e-6))
                specWarnings.Add("阵列圆心回读=(" + centerBack[0].ToString("0.####") + "," + centerBack[1].ToString("0.####") +
                                 ") 与声明 center=(" + cx.ToString("0.####") + "," + cy.ToString("0.####") + ") 不一致——圆心可能漂移;" +
                                 "第 8 步的圆心 Connect 约束按声明 center 挂设,请目检确认。");

            var resolved = new
            {
                @of = spec.Of,
                plane = spec.PlaneRef,
                center = new[] { cx, cy },
                count = count,
                spacing = fit ? "fit(360/count=" + Math.Round(360.0 / count, 4) + "°)" : "fixed",
                spacingRad = spacingRad,
                clockwise = clockwise,
                visualRadius = visRadius,
                patternType = fast ? "fast" : "smart",
                replaced = replaceInfo2,
                readback = new { occurrences = occurrences, angularSpacingRad = spacingBack, offsetType = offsetBack },
                centerConstraint = centerConstraint,
                note = "实例位置=种子绕 center 法向轴旋转 k×spacing;起始角由种子位置决定,阵列圆弧半径纯视觉(2026-10-23 实测);" +
                       "圆心约束三级自动选型(2026-10-25:donor 轴线直连→辅助线 Connect→KeyFix 兜底)"
            };
            return FinishEdgeFeature("pattern", name, context, patTyped, patterns, countBefore, resolved,
                profile, specWarnings);
        }

        /// <summary>
        /// pattern 矩形阵列分支(2026-11-08 开放,规格=reports/op-design_rect-pattern_20261108.md)。
        /// 链路(真机攻坚三件套实测:建/驱动/单项抑制全通):
        ///   载体轮廓画种子位圆 → RectangularPatterns2d.Add(…,Type.Missing×4) → Patterns.Add(seSmartPattern)
        ///   → 真「阵列」特征(NumberOfInputFeatures=1,种子保留在树中)。
        /// ★ 生死线:Add 尾部 4 个 Object 可选槽(StaggerType/StaggerOffset/StaggerOffsetHalf/StaggerIncludeLast)
        ///   必须传 Type.Missing——传强类型枚举/bool 一律 E_FAIL(2026-11-08 探查批 2 四组失败全栽这里)。
        /// 死路勿试:Patterns.AddByRectangular(E_FAIL,PRTAUTO.dll 无 Ex 变体)/ AddByCurve(锚点封送损坏)/
        ///   RecognizeAndCreatePatterns(多特征矩阵 E_FAIL)。证据:api-probe-report §2/§2.5/§2.6。
        /// 语义(实测钉死):origin=阵列角点(occ1=种子位),实例向 +u/+v 展开;occurrence 序号 1-based 列主序
        ///   (X 先递增 Y,col=(k-1)/ycount 整除,row=(k-1)%ycount);抑制不改 NumberOfOccurrences(定义数)。
        /// 驱动:改 xcount/ycount → se_set_feature_parameter(载体 RectangularPattern2d.XCount/YCount 可写);
        ///   改间距/角度/种子位 → replaceOf 重建(XSpace/YSpace 在 2D 对象上不可写)。
        /// </summary>
        private static object RectPatternOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, object> namedFeatures)
        {
            int xc = spec.XCount ?? 0;
            int yc = spec.YCount ?? 0;
            if (xc < 1 || yc < 1)
                return new { op = "pattern", name = name, status = "error",
                    message = "矩形阵列必须提供 xcount/ycount(X/Y 方向实例数,>=1;=1 即单行/单列阵列),如 \"xcount\":3,\"ycount\":3。" };
            if (!spec.XSpacing.HasValue || !spec.YSpacing.HasValue ||
                spec.XSpacing.Value <= 0 || spec.YSpacing.Value <= 0)
                return new { op = "pattern", name = name, status = "error",
                    message = "矩形阵列必须提供 xspacing/yspacing(X/Y 实例间距,米,>0),如 \"xspacing\":0.015,\"yspacing\":0.015。" };
            if (string.IsNullOrWhiteSpace(spec.Of))
                return new { op = "pattern", name = name, status = "error",
                    message = "pattern 必须提供 of(被阵列特征:本批前面特征的 name、SE 特征名或 obj-K 句柄)。" };

            // 1) 种子特征解析 + replaceOf(与圆周共用同一套语义;Pattern 必须先于其种子给出)
            var specWarnings = new List<string>();
            var replaceInfo = ReplaceNamedFeatures(doc, spec.ReplaceOf, specWarnings);
            string seedErr;
            object seedFeat = ResolvePatternSeed(context, doc, spec.Of, namedFeatures, out seedErr);
            if (seedFeat == null)
                return new { op = "pattern", name = name, status = "error", message = seedErr };

            // 2) 载体平面(保守语义:与种子同一张平面——2026-11-08 探查只实测了同面载体,异面行为未定)
            object plane;
            try { plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes); }
            catch (Exception ex) { return new { op = "pattern", name = name, status = "error", message = ex.Message }; }

            // 3) 模型与 Patterns 集合
            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "pattern", name = name, status = "error",
                    message = "没有模型实体——pattern 要阵列已有特征,先 extrude/cut 建出被阵列的特征。" };
            object model = Get(models, "Item", 1);
            object patterns = Get(model, "Patterns");
            int countBefore = Count(patterns);
            double? v0 = TryModelVolume(doc);

            // 4) 参数:center=阵列角点 origin(=occ1=种子位,载体平面局部 u/v,米),缺省 [0,0]
            double ox = 0, oy = 0;
            if (spec.Center != null)
            {
                if (spec.Center.Length != 2)
                    return new { op = "pattern", name = name, status = "error",
                        message = "center 必须是 2 个数字的数组 [u,v](阵列角点=种子位置,载体平面局部坐标,米)。" };
                ox = spec.Center[0]; oy = spec.Center[1];
            }
            // 角度:degrees(度)优先,angle(弧度)兜底;缺省 0。★ 只放行 0:angle≠0 实例方向语义未破解
            // (2026-11-08 真机单点实测 angle=15° → +X 实例落在 229.4°,约定应为 15°)——运行时双保险,
            // 正常流程校验层 E408 已拦。
            double angleRad = 0;
            if (spec.Degrees.HasValue) angleRad = spec.Degrees.Value * Math.PI / 180.0;
            else if (spec.Angle.HasValue) angleRad = spec.Angle.Value;
            if (Math.Abs(angleRad) > 1e-9)
                return new { op = "pattern", name = name, status = "error",
                    message = "矩形阵列 angle 只支持 0:angle≠0 的实例方向语义未破解(2026-11-08 真机实测 angle=15° 时" +
                              "+X 实例落在 229.4° 而非约定 15°,且有实例伸出毛坯边界)。倾斜网格请改用其它方式。" };
            bool fast = string.Equals(spec.PatternType, "fast", StringComparison.OrdinalIgnoreCase);
            double visRadius = spec.Radius ?? 0.05;   // 载体种子位圆显示半径(纯视觉;b13 探针已复核与种子异径亦可)

            // 5) 载体轮廓 + 种子位圆(实测形态:圆心=种子位置;轴框架按 2026-11-05 草图规矩补)
            object profileSet = Call(Get(doc, "ProfileSets"), "Add", null);
            object profile = Call(Get(profileSet, "Profiles"), "Add", new object[] { plane });
            var profileTyped = (SolidEdgePart.Profile)profile;
            DrawSketchAxisFrame(profile, specWarnings);
            profileTyped.Circles2d.AddByCenterRadius(ox, oy, visRadius);

            // 6) ★ 2D 矩形阵列定义——尾部 4 个 Object 槽必须 Type.Missing(生死线,见方法注释)
            SolidEdgeFrameworkSupport.RectangularPattern2d rp2d = profileTyped.RectangularPatterns2d.Add(
                ox, oy,
                xc * spec.XSpacing.Value,           // W/H:Fixed 模式宽松(实测 0.02~0.07 混用皆成),给紧凑跨度
                yc * spec.YSpacing.Value,
                angleRad,
                SolidEdgeFrameworkSupport.PatternOffsetTypeConstants.sePatternFixedOffset,
                xc, yc,
                spec.XSpacing.Value, spec.YSpacing.Value,
                Type.Missing, Type.Missing, Type.Missing, Type.Missing);

            // 7) 3D 阵列特征(官方签名 Add(N, ref FeatureArray, Profile, PatternType))
            Array featArr = new object[] { seedFeat };
            var patTyped = ((SolidEdgePart.Patterns)patterns).Add(1, ref featArr, profileTyped,
                fast ? SolidEdgePart.PatternTypeConstants.seFastPattern
                     : SolidEdgePart.PatternTypeConstants.seSmartPattern);
            SetVisible(profile, false);
            string patName = SafeString(Get(patTyped, "Name"));

            // 8) 单项抑制(1-based 列主序,见方法注释)。PIA 索引属性 Suppressed(Occurrence) 须 InvokeMember
            //    直调(pat.Suppressed[5] 编译不过);置位后 Recompute 才落几何;Recompute 后 Model/Body RCW
            //    失效——第 9 步统一重新取对象再交给收尾校验。
            var suppressed = new List<int>();
            var suppressErrors = new List<string>();
            if (spec.Suppress != null && spec.Suppress.Count > 0)
            {
                foreach (int k in spec.Suppress)
                {
                    try
                    {
                        patTyped.GetType().InvokeMember("Suppressed", System.Reflection.BindingFlags.SetProperty,
                            null, patTyped, new object[] { k, true });
                        suppressed.Add(k);
                    }
                    catch (Exception ex)
                    {
                        suppressErrors.Add("occ" + k + ": " + DescribeException(ex));
                    }
                }
                if (suppressed.Count > 0)
                    Call(model, "Recompute", new object[0]);
            }

            // 9) Recompute 后重取对象(无抑制时对象都是刚建的,直接用)
            object patternsAfter = patterns;
            object patReadback = patTyped;
            if (suppressed.Count > 0)
            {
                object models2 = Get(doc, "Models");
                object model2 = Count(models2) > 0 ? Get(models2, "Item", 1) : null;
                if (model2 != null)
                {
                    patternsAfter = Get(model2, "Patterns");
                    int patTotal = Count(patternsAfter);
                    for (int i = 1; i <= patTotal; i++)
                    {
                        object p = Get(patternsAfter, "Item", i);
                        if (string.Equals(SafeString(Get(p, "Name")), patName, StringComparison.Ordinal))
                        {
                            patReadback = p;
                            break;
                        }
                    }
                }
            }

            // 10) 回读:实例数 / 2D 图案个数(载体 pattern.Profile → RectangularPatterns2d)/ 逐项抑制位图
            int? occurrences = null; int? xcBack = null; int? ycBack = null; string flagsBack = null;
            try { occurrences = Convert.ToInt32(Get(patReadback, "NumberOfOccurrences")); } catch { }
            try
            {
                var rpBack = (SolidEdgeFrameworkSupport.RectangularPattern2d)
                    Get(Get(((SolidEdgePart.Pattern)patReadback).Profile, "RectangularPatterns2d"), "Item", 1);
                xcBack = rpBack.XCount;
                ycBack = rpBack.YCount;
            }
            catch { /* 回读失败不拦截,由集合增长 + Status 校验兜底 */ }
            try
            {
                int occTotal = occurrences ?? xc * yc;
                var sbFlags = new System.Text.StringBuilder();
                for (int i = 1; i <= occTotal; i++)
                    sbFlags.Append((bool)patReadback.GetType().InvokeMember("Suppressed",
                        System.Reflection.BindingFlags.GetProperty, null, patReadback, new object[] { i }) ? "1" : "0");
                flagsBack = sbFlags.ToString();
            }
            catch { }

            if (occurrences.HasValue && occurrences.Value != xc * yc)
                specWarnings.Add("回读 NumberOfOccurrences=" + occurrences.Value + " 与声明 xcount*ycount=" + (xc * yc) +
                                 " 不一致,请目检(se_read_model_state / 截图)确认实例数。");
            if (xcBack.HasValue && (xcBack.Value != xc || (ycBack.HasValue && ycBack.Value != yc)))
                specWarnings.Add("回读载体 2D 图案 XCount/YCount=(" + xcBack.Value + "," + (ycBack.HasValue ? ycBack.Value.ToString() : "?") +
                                 ") 与声明 (" + xc + "," + yc + ") 不一致,请目检。");
            if (suppressErrors.Count > 0)
                specWarnings.Add("部分 occurrence 抑制失败(定义数不受影响,失败的实例仍在): " + string.Join("; ", suppressErrors));

            double? v1 = TryModelVolume(doc);
            var resolved = new
            {
                @of = spec.Of,
                kind = "rect",
                plane = spec.PlaneRef,
                origin = new[] { ox, oy },
                xcount = xc,
                ycount = yc,
                xspacingM = spec.XSpacing.Value,
                yspacingM = spec.YSpacing.Value,
                angleRad = angleRad,
                offsetType = "fixed",
                patterntype = fast ? "fast" : "smart",
                carrierRadiusM = visRadius,
                replaced = replaceInfo,
                readback = new { occurrences = occurrences, xcount = xcBack, ycount = ycBack, suppressedFlags = flagsBack },
                suppressed = suppressed.Count > 0 ? suppressed.ToArray() : null,
                suppressErrors = suppressErrors.Count > 0 ? suppressErrors.ToArray() : null,
                volumeDeltaMm3 = (v0.HasValue && v1.HasValue) ? (double?)Math.Round((v1.Value - v0.Value) * 1e9, 3) : null,
                note = "实例位置=origin + col·xspacing·û + row·yspacing·v̂(米);occurrence 序号 1-based 列主序" +
                       "(X 先递增 Y):col=(k-1)/ycount 整除,row=(k-1)%ycount;抑制不改 NumberOfOccurrences(定义数);" +
                       "改个数:se_set_feature_parameter(feature=阵列名, parameter='xcount'|'ycount', value=N);" +
                       "改间距/角度/种子位:replaceOf 重建;center=阵列角点=种子位置(载体平面局部 u/v)," +
                       "center 不在种子正下方时实例整体错位——务必与种子坐标一致"
            };
            return FinishEdgeFeature("pattern", name, context, patReadback, patternsAfter, countBefore, resolved,
                profile, specWarnings);
        }

        /// <summary>
        /// 全库扫描"平面级隐式轴线"donor(2026-10-25)。
        /// 该对象只在 UI 草图编辑过的平面存在(SE 给平面自动建),COM 无法凭空创建,
        /// 只能从既有关系的成员里借出(GetRelatedObject)。签名(2026-10-25 真机标定):
        ///   Line2d + Index 不可读(不在任何集合) + KeyPointCount=1 + 起点=(0,0)(=草图原点)。
        /// GetRelatedObject 索引 1 基(index 0 抛 DISP_E_BADINDEX)。
        /// 返回轴线对象(找不到为 null),axisKey 带出其 Key 供 resolved 回显。
        /// </summary>
        private static object FindDonorAxis(object doc, out string axisKey)
        {
            axisKey = null;
            try
            {
                object psets = Get(doc, "ProfileSets");
                for (int i = 1; i <= Count(psets); i++)
                {
                    object pros = Get(Get(psets, "Item", i), "Profiles");
                    for (int j = 1; j <= Count(pros); j++)
                    {
                        object rels = Get(Get(pros, "Item", j), "Relations2d");
                        for (int r = 1; r <= Count(rels); r++)
                        {
                            var relT = Get(rels, "Item", r) as SolidEdgeFrameworkSupport.Relation2d;
                            if (relT == null) continue;
                            int n = 0;
                            try { relT.GetRelatedObjectCount(out n); } catch { continue; }
                            for (int m = 1; m <= n; m++)
                            {
                                object gfx; int kp;
                                try { relT.GetRelatedObject(m, out gfx, out kp); }
                                catch { continue; }
                                var ln = gfx as SolidEdgeFrameworkSupport.Line2d;
                                if (ln == null) continue;
                                try { Get(gfx, "Index"); continue; }   // 在集合里=普通画出来的线,不是轴线
                                catch { /* Index 不可读=不在集合,轴线签名之一 */ }
                                int kpc = 0;
                                try { kpc = Convert.ToInt32(Get(gfx, "KeyPointCount")); } catch { }
                                if (kpc != 1) continue;
                                double sx, sy;
                                try { ln.GetStartPoint(out sx, out sy); } catch { continue; }
                                if (Math.Abs(sx) > 1e-6 || Math.Abs(sy) > 1e-6) continue;
                                try { axisKey = Convert.ToString(Get(gfx, "Key")); } catch { }
                                return gfx;
                            }
                        }
                    }
                }
            }
            catch { /* 扫描失败=无 donor,调用方走 ② */ }
            return null;
        }

        /// <summary>
        /// 替换语义(2026-10-24):按名删除旧特征,供 hole/pattern 改参数重跑时调用。
        /// replaceOf 逗号分隔多个名字;**Pattern 必须先于其种子特征给出**(否则种子先删会连挂阵列)。
        /// Hole 特征删除前先删其 HoleData(防断连残留);找不到的名字跳过并记 warning。
        /// 返回删除清单(供 resolved.replaced 回显)。
        /// </summary>
        private static List<string> ReplaceNamedFeatures(object doc, string replaceOf, List<string> warnings)
        {
            var deleted = new List<string>();
            if (string.IsNullOrWhiteSpace(replaceOf)) return deleted;
            var models = Get(doc, "Models");
            if (Count(models) == 0) return deleted;
            object model = Get(models, "Item", 1);

            foreach (var raw in replaceOf.Split(','))
            {
                string nm = raw.Trim();
                if (nm.Length == 0) continue;
                object feat = FindFeatureByName(model, nm, out string kind);
                if (feat == null)
                {
                    if (warnings != null) warnings.Add("replaceOf: 未找到 \"" + nm + "\"(可能已删除),跳过。");
                    continue;
                }
                // Hole 特征:先删 HoleData(防特征删除断连它留残留)
                if (kind == "Hole")
                {
                    try
                    {
                        object hd = Get(feat, "HoleData");
                        if (hd != null) TryDelete(hd);
                    }
                    catch { }
                }
                bool ok = TryDelete(feat);
                if (ok) deleted.Add(nm + "(" + kind + ")");
                else if (warnings != null) warnings.Add("replaceOf: \"" + nm + "\" 删除失败,请手动检查。");
            }
            return deleted;
        }

        /// <summary>按 SE 名在各特征集合里找特征(Holes/Patterns/各类拉伸除料/圆角倒角);kind=命中的集合名(replaced 回显用,RCW GetType() 只给 __ComObject 拿不到真名)。</summary>
        private static object FindFeatureByName(object model, string featureName, out string kind)
        {
            kind = null;
            string[] colls = { "Patterns", "Holes", "ExtrudedProtrusions", "ExtrudedCutouts",
                               "RevolvedProtrusions", "RevolvedCutouts", "Rounds", "Chamfers" };
            foreach (string coll in colls)
            {
                object c = null;
                try { c = Get(model, coll); } catch { continue; }
                int n = Count(c);
                for (int i = 1; i <= n; i++)
                {
                    object item = null;
                    try { item = Get(c, "Item", i); } catch { continue; }
                    string nm = SafeString(Get(item, "Name"));
                    if (string.Equals(nm, featureName, StringComparison.OrdinalIgnoreCase))
                    {
                        kind = coll;
                        return item;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// pattern 的种子特征解析:① obj-K 句柄;② 本批 name(namedFeatures);③ 模型各特征集合按 SE 名匹配。
        /// 找不到时返回 null + 列出模型现有特征名的错误消息。
        /// </summary>
        private static object ResolvePatternSeed(SolidEdgeContext context, object doc, string of,
            Dictionary<string, object> namedFeatures, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(of)) { error = "of 为空。"; return null; }

            if (of.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                var h = context.GetHandle(of);
                if (h == null || h.ComObject == null) { error = "句柄表里找不到 " + of + "。"; return null; }
                return h.ComObject;
            }

            if (namedFeatures != null && namedFeatures.TryGetValue(of, out var nf) && nf != null)
                return nf;

            // 模型特征集合按 SE 名(SystemName,如 "ExtrudedCutout_2")匹配
            object models = Get(doc, "Models");
            if (Count(models) > 0)
            {
                object model = Get(models, "Item", 1);
                string[] colls = { "ExtrudedProtrusions", "ExtrudedCutouts", "RevolvedProtrusions", "RevolvedCutouts",
                                   "Holes", "Rounds", "Chamfers", "Patterns" };
                foreach (string coll in colls)
                {
                    object c = null;
                    try { c = Get(model, coll); } catch { continue; }
                    int n = Count(c);
                    for (int i = 1; i <= n; i++)
                    {
                        object item = null;
                        try { item = Get(c, "Item", i); } catch { continue; }
                        string nm = SafeString(Get(item, "Name"));
                        if (string.Equals(nm, of, StringComparison.OrdinalIgnoreCase)) return item;
                    }
                }
            }

            error = "未找到被阵列特征 \"" + of + "\"。支持三种写法:本批前面特征的 name、SE 特征名" +
                    "(如 \"ExtrudedCutout_2\",即前面结果里的 feature 字段)、obj-K 句柄。";
            return null;
        }

        /// <summary>
        /// fillet/chamfer/pattern 的统一收尾:Status 校验(FeatureResult)之外,再验证
        /// 对应特征集合 Count 确实增长——"Status OK 但集合没长大"= 没作用到任何几何,判失败回滚。
        /// 2026-10-23:pattern 加入后多两个可选参(profile=载体轮廓,僵尸回滚时连草图一起清;
        /// specWarnings=回读告警透传)。fillet/chamfer 调用点不受影响(都是可缺省参)。
        /// </summary>
        private static object FinishEdgeFeature(string op, string name, SolidEdgeContext context, object feat,
            object collection, int countBefore, object resolved,
            object profile = null, List<string> specWarnings = null)
        {
            int countAfter = Count(collection);
            if (countAfter <= countBefore)
            {
                TryDelete(feat);
                // 载体轮廓一并清理(pattern 专用;fillet/chamfer 无草图)
                if (profile != null)
                {
                    object profileSet = TryGetProfileSet(profile);
                    if (profileSet != null) TryDelete(profileSet);
                }
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
            // pattern 的载体轮廓里只有 2D 阵列定义、无草图几何,IsUnderDefined 对它无意义,
            // 传 ignoreUnderDefined=true 抑制"轮廓仍欠约束"误报。
            return FeatureResult(op, name, feat, context, profile,
                op == "fillet" ? "Round" : op == "chamfer" ? "Chamfer" : "Pattern", specWarnings, resolved,
                null, op == "pattern");
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
                    // 2026-10-23:faceNormal 匹配依赖法向【符号】(点积同向)。只认边叉积那条路径——
                    // 几何体自带法向(盘面兜底)符号未校验,混进来会让老配方选到反向的面,故跳过。
                    double[] nrm = TryGetFaceNormal(f, out bool signOk);
                    if (nrm == null || !signOk) continue;
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
        /// 读平面面的法向(世界系 XYZ)。
        /// ★ 2026-10-23 改造:**边叉积优先,取不到再问几何体**(两段式)。
        ///   ① 叉积路径(原有,符号与历史完全一致):Vertex 没有直接 x/y/z 属性(SE SDK 实测),
        ///      走强类型 Edge.GetEndPoints 拿两个 Double[3],取前两条不共线边方向叉积。
        ///   ② 兜底路径(新增):盘面(圆柱/圆凸台顶面这类**只有一条整圆边**的平面面)拿不到两条不共线边,
        ///      旧实现在 ec&lt;2 / dir2==null 时直接返回 null → 该面进不了任何轴向候选
        ///      (实测症状:圆凸台顶面用 face:+Z 选不出来)。现改为直接问几何体:
        ///        Plane.GetNormalVector(ref Array)   // typelib 实测:Void GetNormalVector(ref Array)
        ///      ⚠️ 该法向只保证"沿哪根轴",**符号不保证**与叉积约定一致(它是几何体自带方向)。
        ///      所以 signFromEdges 必须回传给调用方:依赖符号的 faceNormal 匹配只认 true,
        ///      否则老配方会因为符号翻转而选错面。轴向选择器不依赖符号(朝向靠面位置判),可用 false。
        /// 失败返回 null(调用方跳过该面)。
        /// </summary>
        private static double[] TryGetFaceNormal(object face)
        {
            return TryGetFaceNormal(face, out _);
        }

        /// <summary>
        /// <paramref name="signFromEdges"/>=true 表示法向符号来自边叉积(与历史行为一致);
        /// false 表示来自 Plane.GetNormalVector(只保证轴向正确,符号未校验)。null 时该值无意义。
        /// </summary>
        private static double[] TryGetFaceNormal(object face, out bool signFromEdges)
        {
            signFromEdges = false;
            try
            {
                var f = (SolidEdgeGeometry.Face)face;
                // 2026-09-23 真机:圆柱/blend 面的圆弧边 GetEndPoints 给弦向量,两条弦不共线照样叉积出
                // "假法向"——blend 面被误判平面。先强类型 QI:Geometry 不是 Plane 即非平面面,直接 null。
                if (!(f.Geometry is SolidEdgeGeometry.Plane plane)) return null;

                double[] byEdges = TryFaceNormalByEdges(f);
                if (byEdges != null)
                {
                    signFromEdges = true;
                    return byEdges;
                }

                return TryFaceNormalByPlane(plane);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>叉积路径:需要两条不共线的直边。拿不到(盘面/退化边)返回 null,由调用方回落。</summary>
        private static double[] TryFaceNormalByEdges(SolidEdgeGeometry.Face f)
        {
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

        /// <summary>
        /// 平面几何体自带的法向路径(2026-10-23):不碰边,单条整圆边的盘面也能给法向。
        /// 官方签名(typelib 实测):<c>Void GetNormalVector(ref Array NormalVector)</c>——C# 侧写 ref,不是 out。
        /// </summary>
        private static double[] TryFaceNormalByPlane(SolidEdgeGeometry.Plane plane)
        {
            if (plane == null) return null;
            Array nv = Array.CreateInstance(typeof(double), 0);
            plane.GetNormalVector(ref nv);
            if (nv == null || nv.Length < 3) return null;

            double nx = Convert.ToDouble(nv.GetValue(0), CultureInfo.InvariantCulture);
            double ny = Convert.ToDouble(nv.GetValue(1), CultureInfo.InvariantCulture);
            double nz = Convert.ToDouble(nv.GetValue(2), CultureInfo.InvariantCulture);
            double mag = System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (mag < 1e-12) return null;
            return new[] { nx / mag, ny / mag, nz / mag };
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
                    // 同 rib:非僵尸即算该方向成了(见上条注释)。
                    long? st = SafeLong(Get(feat, "Status"));
                    if (ClassifyFeatureStatus(st) != FeatureHealth.Zombie) { usedWd = candidate; break; }
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
        /// loft_surface 放样曲面:Constructions.LoftedSurfaces.Add(..., WantEndCaps=false)。
        /// ★ 必须不封端:封端后特征 Faces[igQueryAll].Count=0(已是闭壳/实体),再 thicken 必 E_INVALIDARG
        ///   ——2026-10-31 真机复核证实,这就是"曲面加厚不可用"那份报告的真实根因。
        /// CrossSectionTypes 必须是 int[](传 object[] 直接 E_INVALIDARG);Origins 每截面一个 double[2]{u,v}。
        /// </summary>
        private static object LoftSurfaceOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes, Dictionary<string, object> namedFeatures,
            Dictionary<string, List<object>> surfaceFaces)
        {
            if (!spec.HasProfiles || spec.Profiles.Count < 2)
                return new { op = "loft_surface", name = name, status = "error",
                    message = "loft_surface 需要 profiles 里至少 2 个截面(每项 {plane,形状}),当前 " +
                              (spec.HasProfiles ? spec.Profiles.Count : 0) + " 个。",
                    fix = new { action = "provide", field = "profiles", minItems = 2 } };

            // 开口截面(2026-11-02):默认只收闭合轮廓;要开放壳必须显式 openProfile:true。
            // 静态校验(E410)已拦,这里再兜一层——直接走 CLI 或绕过校验的入口也应拿到明确报错,
            // 而不是建到一半变僵尸再回滚。
            if (!spec.OpenProfile)
            {
                for (int i = 0; i < spec.Profiles.Count; i++)
                {
                    var p = spec.Profiles[i];
                    if (p.HasSpline && !p.SplineClosed)
                        return new { op = "loft_surface", name = name, status = "error",
                            message = "截面 profiles[" + i + "] 是开口样条(closed:false)。开口截面出的是【开放壳】," +
                                      "必须显式 \"openProfile\":true 才放行;要闭合体就给 \"closed\":true。",
                            fix = new { action = "set", field = "openProfile", value = true } };
                }
            }

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
                int[] types = new int[n];     // 必须 int[]:object[] 会 E_INVALIDARG
                var origins = new object[n];  // 每元素 double[2]{u,v}:空数组/扁平数组/二维数组都失败
                for (int i = 0; i < n; i++)
                {
                    sections[i] = profiles[i];
                    types[i] = 48;   // igProfileBasedCrossSection
                    origins[i] = SectionOrigin(spec.Profiles[i]);
                }

                // 导线(2026-10-31 真机验证过):GuideCurves 吃的是【开放样条 Profile】。
                // 用 3D 草图折线边当导线会 faces=0(几何不生成),所以这里统一按"平面样条轮廓"建。
                if (spec.GuidesError != null)
                    throw new ArgumentException(spec.GuidesError);
                var guideObjs = new List<object>();
                if (spec.HasGuides)
                {
                    foreach (var g in spec.Guides)
                    {
                        if (!g.HasSpline)
                            throw new ArgumentException("导线必须是 spline(开放样条)轮廓,当前写法不是:" +
                                (g.ShapeError ?? ("形状=" + g.ShapeSource)));
                        if (g.SplineClosed)
                            throw new ArgumentException("导线必须是【开放】样条(closed 不能是 true)——闭合导线没有首尾对应关系。");
                        object gplane = ResolvePlane(context, doc, g.PlaneRef, namedPlanes);
                        guideObjs.Add(CreateProfileSpline(doc, gplane, g, false));
                    }
                }

                object constructions = Get(doc, "Constructions");
                object lofts = Get(constructions, "LoftedSurfaces");
                object feat = ((SolidEdgePart.LoftedSurfaces)lofts).Add(
                    n, sections, types, origins,
                    SolidEdgePart.FeaturePropertyConstants.igNone,
                    SolidEdgePart.FeaturePropertyConstants.igNone,
                    SolidEdgePart.FeaturePropertyConstants.igNone, 0.0,
                    SolidEdgePart.FeaturePropertyConstants.igNone, 0.0,
                    guideObjs.Count, guideObjs.ToArray(),
                    false);   // WantEndCaps=false:封端后曲面就没有面可加厚

                // 与 extrude_surface 同一套缓存:Add 刚加的构造体是 Constructions 最后一个成员,
                // 从它的 Body 取面缓存给 thicken 的 faceOf 直接命中(按名/引用匹配 RCW 不可靠)。
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
                                int fn = Count(faces);
                                var list = new List<object>();
                                for (int f = 1; f <= fn; f++) list.Add(Get(faces, "Item", f));
                                if (list.Count > 0) surfaceFaces[name] = list;
                            }
                        }
                    }
                    catch { /* 缓存失败不阻断:ThickenOp 走 GetSurfaceFaces 兜底 */ }
                }

                return FeatureResult("loft_surface", name, feat, context, profiles[0], "LoftedSurface", specWarnings,
                    new { sections = n, guides = guideObjs.Count, endCaps = false, openProfile = spec.OpenProfile,
                          faceCount = surfaceFaces.TryGetValue(name ?? "", out var fl) ? fl.Count : 0 },
                    profiles.Count > 1 ? profiles.GetRange(1, profiles.Count - 1) : null);
            }
            catch
            {
                DiscardProfiles(profiles);
                throw;
            }
        }

        /// <summary>
        /// thicken 曲面加厚:把 extrude_surface / loft_surface 产出的曲面加厚成实体。
        /// faces 取自曲面体(创建时缓存的 Constructions.Item(n).Body.Faces(igQueryAll),失败再扫)。
        /// Thickens.Add(Side, offsetDistance, nFaces, ref Faces);Side: igLeft=1(默认,沿曲面法向一侧)/
        /// igRight=2 / igSymmetric=3(两侧各一半);空文档的首个设计体走 Models.AddThickenFeature。
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
                    // 2026-10-23:同上,faceNormal 依赖符号 → 只认边叉积路径。
                    double[] nrm = TryGetFaceNormal(f, out bool signOk);
                    if (nrm == null || !signOk) continue;
                    if (nrm[0] * nx + nrm[1] * ny + nrm[2] * nz > 1 - 1e-6) filtered.Add(f);
                }
                if (filtered.Count > 0) surfFaces = filtered;
            }

            object models = Get(doc, "Models");

            // Side 的类型是 FeaturePropertyConstants:igLeft=1 / igRight=2 / igSymmetric=3(2026-10-31 真机三个都成)。
            // 旧代码默认 5、并且只试过 4/5/97/98/160 —— 那是 DraftSideConstants 的 igInside/igOutside,
            // 传进来必 E_INVALIDARG,才导致"该通道不可达"的误判。
            int side = spec.Side ?? 1;
            if (side < 1 || side > 3)
                return new { op = "thicken", name = name, status = "error",
                    message = "thicken 的 side 只支持 1(igLeft)/2(igRight)/3(igSymmetric),当前 " + side + "。",
                    fix = new { action = "set", field = "side", allowed = new[] { 1, 2, 3 } } };

            try
            {
                Array facesArr = surfFaces.ToArray();
                bool firstBody = Count(models) == 0;
                object feat;
                if (firstBody)
                {
                    // 空文档里只有【曲面构造体】没有 Model,而 Thickens 挂在 Model 上 —— 首特征必须走
                    // Models.AddThickenFeature(与 AddFiniteExtrudedProtrusion 同级的首特征通道)。
                    // 返回的是新建的 Model,必须回取特征对象,否则 Status 读到的是设计体(同 loft 首特征)。
                    SolidEdgePart.Model created = ((SolidEdgePart.Models)models).AddThickenFeature(
                        (SolidEdgePart.FeaturePropertyConstants)side,
                        spec.Thickness.Value, surfFaces.Count, ref facesArr);
                    feat = Count(Get(created, "Thickens")) >= 1 ? Get(Get(created, "Thickens"), "Item", 1) : (object)created;
                }
                else
                {
                    object model = Get(models, "Item", 1);
                    feat = ((SolidEdgePart.Model)model).Thickens.Add(
                        (SolidEdgePart.FeaturePropertyConstants)side,
                        spec.Thickness.Value, surfFaces.Count, ref facesArr);
                }
                return FeatureResult("thicken", name, feat, context, null, "Thicken", null,
                    new { faceOf = spec.FaceRef.FeatureName, thickness = spec.Thickness.Value, side = side,
                          faceCount = surfFaces.Count, firstBody = firstBody });
            }
            catch (Exception ex)
            {
                // 真机实测的两个常见失败面:①取面路径拿到的面数=0(2026-11-01 复测订正:封端曲面
                //   WantEndCaps=true 本身 Faces.Count=4 且可成功加厚,"封端必失败"旧表述作废——
                //   faces=0 是取面路径问题,先回读面数再判因);②thickness 单位不是米。别再把整个通道判死。
                return new { op = "thicken", name = name, status = "error",
                    message = "Thickens.Add 失败:" + DescribeException(ex),
                    diagnosis = "先回读上游曲面 Faces.Count:faces=0 是取面路径问题(本 op 链路内 loft_surface/extrude_surface 恒不封端);" +
                        "faces>0 时核对 thickness 单位(米)。2026-11-01 实测封端曲面也有面且可加厚,\"必须未封端\"的旧表述已作废。",
                    alternatives = new[] { "faces=0:改用本 op 链路的 loft_surface/extrude_surface 产面再 faceOf 引用", "thickness 用米,如 0.0015 = 1.5 mm" },
                    fix = new { action = "use_alternative" } };
            }
        }

        /// <summary>
        /// bluesurf 蓝面补面(2026-11-09 真机打通,探针 probes_bluesurf2/p5~p9):
        /// 截面 = 实体 Model.Body.Edges 的两条共端点边(1-based 序号)。
        /// 配方:BlueSurfs.Add(2, 边×2, origins×2, igNatural,0, igNatural,0, 0, [], igNatural,0, igNatural,0, false,false)。
        /// ★ origins 必须是【两条边在共享端点处的 Vertex 对象】——传边自身只在"边锚点恰好重合"时成功
        ///   (盒 12 边仅原点 3 边互组,p8);Vertex origins 解锁全部共端点对(p9 实测 10/10)。
        /// ★ 2D 轮廓截面(CurveBody/Curve/KeyPointCurve)恒产 igFeatureFailed 空壳(2026-11-01/11-08 双裁决),
        ///   本 op 只吃实体边,不接受轮廓。
        /// ★ Constructions 层僵尸不自动回滚(11-08 T6 实测):僵尸必须 bs.Delete() 清掉再换序重试一次。
        /// 产物 faces=1,入 surfaceFaces 缓存供 thicken faceOf——蓝面对象【无 Body 属性】,thicken 的
        /// GetSurfaceFaces 兜底扫 Constructions 时拿不到它,创建时缓存是唯一通路(同 extrude_surface)。
        /// </summary>
        private static object BluesurfOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedFeatures, Dictionary<string, List<object>> surfaceFaces)
        {
            // 静态层 E421 已拦;这里兜底
            if (spec.EdgeIndices == null || spec.EdgeIndices.Count != 2)
                return new { op = "bluesurf", name = name, status = "error",
                    message = "bluesurf 需要 edges:[i,j] 恰好 2 条边(1-based Model.Body.Edges 序)。",
                    fix = new { action = "set", field = "edges", example = new[] { 1, 2 } } };
            int i1 = spec.EdgeIndices[0], i2 = spec.EdgeIndices[1];
            try
            {
                object featObj = ResolveFeatureObject(context, spec.EdgeOf, namedFeatures);
                object model = Get(featObj, "Parent");
                if (!(model is SolidEdgePart.Model))
                    model = Get(Get(doc, "Models"), "Item", 1);
                object body = Get(model, "Body");
                object edgesCol = Get(body, "Edges", 1);   // igQueryAll=1
                int ne = Count(edgesCol);
                if (i1 > ne || i2 > ne)
                    return new { op = "bluesurf", name = name, status = "error",
                        message = "边序号越界:Model.Body.Edges 共 " + ne + " 条(1-based),收到 [" + i1 + "," + i2 +
                            "]。边序号会随特征增删漂移,建议与 edgeOf 特征同批使用。",
                        fix = new { action = "set", field = "edges" } };
                object ea = Get(edgesCol, "Item", i1), eb = Get(edgesCol, "Item", i2);

                // 共享端点 → Vertex origins(p9:解锁全部共端点对);找不到共享点则退回边自身(p8 锚点恰合时仍可成)
                var eaP = EdgeEndpoints(ea);
                var ebP = EdgeEndpoints(eb);
                object va = null, vb = null;
                double[] anchor = null;
                bool viaVertex = false;
                if (eaP != null && ebP != null)
                {
                    for (int a = 0; a < 2 && !viaVertex; a++)
                    {
                        for (int b = 0; b < 2 && !viaVertex; b++)
                        {
                            if (eaP[a] == null || ebP[b] == null) continue;
                            if (Dist3(eaP[a], ebP[b]) < 1e-6)
                            {
                                object vaT = VertexOf(ea, eaP[a]), vbT = VertexOf(eb, ebP[b]);
                                if (vaT != null && vbT != null)
                                {
                                    va = vaT; vb = vbT; anchor = eaP[a]; viaVertex = true;
                                }
                                else if (anchor == null)
                                {
                                    anchor = eaP[a];   // 有共享点但顶点对象取不到:记录坐标,走边自身 origins
                                }
                            }
                        }
                    }
                }
                string anchorMode = viaVertex ? "shared-vertex"
                    : (anchor != null ? "shared-point-edge-origins" : "edge-origins(未检出共享端点,成功率低)");

                var bss = (SolidEdgePart.BlueSurfs)Get(Get(doc, "Constructions"), "BlueSurfs");
                var attempts = new List<string>();
                object feat = TryBlueAdd(bss, ea, eb, viaVertex ? va : null, viaVertex ? vb : null);
                long? st1 = SafeLong(Get(feat, "Status"));
                attempts.Add("尝试1(origins=" + (viaVertex ? "共享端Vertex" : "边自身") + "): " +
                    HealthMeaning(ClassifyFeatureStatus(st1)));
                if (ClassifyFeatureStatus(st1) == FeatureHealth.Zombie)
                {
                    TryDelete(feat);   // Constructions 层僵尸不自动回滚,必须手动清
                    feat = TryBlueAdd(bss, eb, ea, viaVertex ? vb : null, viaVertex ? va : null);
                    long? st2 = SafeLong(Get(feat, "Status"));
                    attempts.Add("尝试2(换序): " + HealthMeaning(ClassifyFeatureStatus(st2)));
                    if (ClassifyFeatureStatus(st2) == FeatureHealth.Zombie)
                    {
                        TryDelete(feat);
                        return new { op = "bluesurf", name = name, status = "error",
                            message = "BlueSurfs.Add 正序/换序两次尝试均为 igFeatureFailed 空壳,僵尸已清理。",
                            diagnosis = "蓝面补面条件:两条【实体】边共享一个端点且端点可解析为顶点对象;2D 轮廓截面恒产僵尸(2026-11-01/11-08 裁决),本 op 不接受轮廓。",
                            attempts = attempts,
                            alternatives = new[] {
                                "选两条共享端点的边(同一顶点引出的两条 Body 边)",
                                "edgeOf 指向产出这两条边的实体特征(边序号按 Model.Body.Edges 全局序)",
                                "后续 thicken 报方向错时,换 edges:[j,i] 重建蓝面(边序决定面法向)" },
                            fix = new { action = "use_alternative" } };
                    }
                }

                // 成功:缓存蓝面面供 thicken 的 faceOf @别名(蓝面对象无 Body,兜底扫不到,缓存是唯一通路)
                var faceList = new List<object>();
                try
                {
                    object faces = Get(feat, "Faces", 1);   // igQueryAll=1
                    int n = Count(faces);
                    for (int f = 1; f <= n; f++) faceList.Add(Get(faces, "Item", f));
                }
                catch { /* 缓存失败不阻断 */ }
                if (!string.IsNullOrEmpty(name) && faceList.Count > 0)
                    surfaceFaces[name] = faceList;

                return FeatureResult("bluesurf", name, feat, context, null, "BlueSurf", null,
                    new
                    {
                        edgeOf = spec.EdgeOf,
                        edges = new[] { i1, i2 },
                        anchorMm = anchor != null
                            ? new[] { anchor[0] * 1000.0, anchor[1] * 1000.0, anchor[2] * 1000.0 }
                            : (double[])null,
                        anchorMode = anchorMode,
                        faceCount = faceList.Count
                    });
            }
            catch (Exception ex)
            {
                return new { op = "bluesurf", name = name, status = "error",
                    message = "BlueSurfs.Add 失败:" + DescribeException(ex),
                    diagnosis = "edges 必须是 Model.Body.Edges 的 1-based 序号(两条边共享端点);edgeOf 指向实体特征。",
                    fix = new { action = "use_alternative" } };
            }
        }

        private static object TryBlueAdd(SolidEdgePart.BlueSurfs bss, object ea, object eb, object oa, object ob)
        {
            Array sA = new object[] { ea, eb };
            Array oA = (oa != null && ob != null) ? new object[] { oa, ob } : new object[] { ea, eb };
            Array gA = new object[0];
            return bss.Add(2, ref sA, ref oA,
                SolidEdgePart.FeaturePropertyConstants.igNatural, 0.0,
                SolidEdgePart.FeaturePropertyConstants.igNatural, 0.0,
                0, ref gA,
                SolidEdgePart.FeaturePropertyConstants.igNatural, 0.0,
                SolidEdgePart.FeaturePropertyConstants.igNatural, 0.0,
                false, false);
        }

        // 实体边端点(SEG.Edge 强类型 Start/EndVertex,p7 实测 QI 正常;失败回退 IDispatch 属性)
        private static double[][] EdgeEndpoints(object edge)
        {
            try
            {
                var se = (SolidEdgeGeometry.Edge)edge;
                return new[] { VertexPos3(se.StartVertex), VertexPos3(se.EndVertex) };
            }
            catch { }
            try
            {
                return new[] { VertexPos3(Get(edge, "StartVertex")), VertexPos3(Get(edge, "EndVertex")) };
            }
            catch { }
            return null;
        }

        private static double[] VertexPos3(object v)
        {
            if (v == null) return null;
            try
            {
                var sv = (SolidEdgeGeometry.Vertex)v;
                Array a = new double[0];
                sv.GetPointData(ref a);
                if (a != null && a.Length >= 3)
                    return new[]
                    {
                        Convert.ToDouble(a.GetValue(0), CultureInfo.InvariantCulture),
                        Convert.ToDouble(a.GetValue(1), CultureInfo.InvariantCulture),
                        Convert.ToDouble(a.GetValue(2), CultureInfo.InvariantCulture)
                    };
            }
            catch { }
            return null;
        }

        private static object VertexOf(object edge, double[] pt)
        {
            try
            {
                var se = (SolidEdgeGeometry.Edge)edge;
                if (VertexMatches(se.StartVertex, pt)) return se.StartVertex;
                if (VertexMatches(se.EndVertex, pt)) return se.EndVertex;
            }
            catch { }
            return null;
        }

        private static bool VertexMatches(object v, double[] pt)
        {
            var p = VertexPos3(v);
            return p != null && Dist3(p, pt) < 1e-6;
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
                long? st = null;
                try { st = SafeLong(Get(feat, "Status")); } catch { }
                // 只有"确实生效"的两档才算这条通道成功:OK(正常)与 Warned(已生成但有警告,几何可用)。
                // Suppressed/RolledBack 说明这个删面特征并未真正生效,不能算成功——继续交给 DeleteFaces 兜底。
                FeatureHealth blendHealth = ClassifyFeatureStatus(st);
                if (blendHealth == FeatureHealth.Ok || blendHealth == FeatureHealth.Warned)
                {
                    return FeatureResult("delete_face", name, feat, context, null, "DeleteBlend", warnings,
                        new { faceOf = spec.FaceRef.FeatureName, faceIndex = spec.FaceRef.Index, kind = "blend" });
                }
                // 2026-09-23 真机:对非 blend 面(如孔圆柱面),DeleteBlends 不抛异常而是产出 Status=6311
                // 僵尸特征——按异常切通道的旧逻辑永远走不到 DeleteFaces。此处删掉僵尸再抛给兜底通道。
                try { Get(feat, "Delete"); } catch { }
                throw new InvalidOperationException("DeleteBlends 未成体(Status=" + (st.HasValue ? st.Value.ToString() : "读不到") +
                                                    " / " + HealthEnumName(blendHealth) + "),目标疑似非 blend 面");
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
        /// spline3d 3D 样条(2026-11-02):doc.Sketches3D.Add() → BSplineCurves3D.AddByPoints(点数, ref 扁平点数组, 闭合)。
        ///
        /// ★ 只往 3D 草图里加曲线,不是实体特征(没有 Status/体积),所以不走 FeatureResult(那个会判僵尸/体积)。
        /// ★ 走 IDispatch 而不是强类型:本仓合并版 interop(108)里 BSplineCurves3D 没有 AddByPoints(反射核实),
        ///   只能晚绑定;ref double[] 必须按 VT_BYREF|VT_ARRAY 封送(见 ManualInvoke.TryInvokeByRefDoubleArray)。
        /// ★ ArraySize 传【点数】,不是坐标个数(传 3N 直接报错)。
        /// 用途:loft_surface 的 3D 样条导线(3D 草图折线当导线会建出 faces=0 的空特征,样条才光滑)。
        /// </summary>
        private static object Spline3DOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name)
        {
            if (spec.Points3DError != null)
                return new { op = "spline3d", name = name, status = "error", message = spec.Points3DError,
                    fix = new { action = "fix", field = "points", format = "[[x,y,z],...](世界坐标,米)" } };

            int n = spec.Points3D.Count;
            var flat = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                flat[i * 3] = spec.Points3D[i][0];
                flat[i * 3 + 1] = spec.Points3D[i][1];
                flat[i * 3 + 2] = spec.Points3D[i][2];
            }

            object sketch = null;
            try
            {
                sketch = CreateSketch3D(doc);
                object curves = Get(sketch, "BSplineCurves3D");
                if (curves == null)
                    throw new InvalidOperationException("3D 草图没有 BSplineCurves3D 集合(SE 版本不支持?)");

                Exception callErr;
                if (!ManualInvoke.TryInvokeByRefDoubleArray(curves, "AddByPoints",
                        new object[] { n, null, spec.Spline3DClosed }, 1, flat, out _, out callErr))
                {
                    throw new InvalidOperationException("BSplineCurves3D.AddByPoints 失败:" + DescribeException(callErr), callErr);
                }

                return Curve3DResult(context, sketch, spec, "spline3d", name, "BSplineCurve3D", "BSplineCurves3D",
                    new { points = n, closed = spec.Spline3DClosed });
            }
            catch (Exception ex)
            {
                if (sketch != null) TryDelete(sketch);
                return new { op = "spline3d", name = name, status = "error", message = DescribeException(ex),
                    diagnosis = "spline3d 只做 3D 样条曲线:points 是世界坐标(米),至少 2 点;不需要 plane。",
                    alternatives = new[] { "se_invoke_member 直接调 BSplineCurves3D.AddByPoints 排查签名" } };
            }
        }

        /// <summary>
        /// arc3d 3D 圆弧(2026-11-02):doc.Sketches3D.Add() → Arcs3D.AddByStartEndCenterNormal(13 个 double)。
        ///
        /// ★ 13 参顺序:起点 3 + 终点 3 + 圆心 3 + 法向 3 + 半径 1。
        /// ★ 半径必须等于 |起点-圆心|(= |终点-圆心|),给错直接 E_FAIL ——这是该接口唯一的失败点,
        ///   所以这里永远按几何算,调用方给的 radius 只在不到 1e-6 的范围内被采纳(不一致时静态校验已报 W420)。
        /// </summary>
        private static object Arc3DOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name)
        {
            if (spec.Arc3DError != null)
                return new { op = "arc3d", name = name, status = "error", message = spec.Arc3DError,
                    fix = new { action = "provide", fields = new[] { "start", "end", "center" } } };

            double[] st = spec.Start3D, en = spec.End3D, ce = spec.Center3D;
            double rStart = Dist3(st, ce);
            double rEnd = Dist3(en, ce);
            if (rStart < 1e-9 || rEnd < 1e-9)
                return new { op = "arc3d", name = name, status = "error",
                    message = "arc3d 的起点/终点与圆心重合(半径≈0),画不出圆弧。",
                    fix = new { action = "fix", field = "start/end/center" } };
            if (Math.Abs(rStart - rEnd) > 1e-6)
                return new { op = "arc3d", name = name, status = "error",
                    message = "arc3d 几何不自洽:起点到圆心 " + rStart.ToString("F6") + " m,终点到圆心 " +
                              rEnd.ToString("F6") + " m。该接口要求两端半径相等,end 必须落在圆心为该点的圆上。",
                    fix = new { action = "fix", field = "end" } };

            double[] nrm = spec.Normal3D ?? NormalOf3(st, en, ce);
            if (nrm == null)
                return new { op = "arc3d", name = name, status = "error",
                    message = "arc3d 的 start / end / center 三点共线,定不出圆弧平面;请给 normal 或修正三点。",
                    fix = new { action = "provide", field = "normal" } };
            nrm = Normalize3(nrm);

            object sketch = null;
            try
            {
                sketch = CreateSketch3D(doc);
                object arcs = Get(sketch, "Arcs3D");
                if (arcs == null)
                    throw new InvalidOperationException("3D 草图没有 Arcs3D 集合(SE 版本不支持?)");

                Call(arcs, "AddByStartEndCenterNormal", new object[]
                {
                    st[0], st[1], st[2],
                    en[0], en[1], en[2],
                    ce[0], ce[1], ce[2],
                    nrm[0], nrm[1], nrm[2],
                    rStart
                });

                return Curve3DResult(context, sketch, spec, "arc3d", name, "Arc3D", "Arcs3D",
                    new { radius = rStart, center = ce, normal = nrm });
            }
            catch (Exception ex)
            {
                if (sketch != null) TryDelete(sketch);
                return new { op = "arc3d", name = name, status = "error", message = DescribeException(ex),
                    diagnosis = "AddByStartEndCenterNormal 只有 13 个 double:起点/终点/圆心/法向各 3 个 + 半径;" +
                                "半径必须与 |start-center| 相等(本 op 已按几何算)。",
                    alternatives = new[] { "se_invoke_member 直接调 Arcs3D.AddByStartEndCenterNormal 排查" } };
            }
        }

        /// <summary>建一个新的 3D 草图并设为活动(曲线必须画在活动 3D 草图里)。</summary>
        private static object CreateSketch3D(object doc)
        {
            object sketches = Get(doc, "Sketches3D");
            if (sketches == null)
                throw new InvalidOperationException("当前文档取不到 Sketches3D —— spline3d/arc3d 只能用于零件文档。");

            object sketch = Call(sketches, "Add", null);
            try { Put(sketch, "Active", true); } catch { /* 个别版本 Active 只读,失败不阻断 */ }
            return sketch;
        }

        /// <summary>
        /// 3D 曲线 op 的统一出口:数曲线、登记句柄、隐藏草图(曲线不进特征树,不判 Status/体积)。
        /// resolved 是各 op 自己的参数回显(嵌套一层,免得为两种曲线各写一个匿名类型)。
        /// </summary>
        private static object Curve3DResult(SolidEdgeContext context, object sketch, FeatureSpec spec, string op, string name,
            string kind, string collectionName, object resolved)
        {
            int curveCount = -1;
            try { curveCount = Count(Get(sketch, collectionName)); } catch { }
            if (curveCount <= 0)
                curveCount = -1;   // 读不到/0 都不算证据,回显 -1 让调用方知道没核对上

            try { Put(sketch, "Active", false); } catch { }
            // 默认隐藏(与其它 op 一致:草图不挡视线);要看曲线就显式给 "visible":true
            bool keepVisible = spec.Visible ?? false;
            if (!keepVisible) try { SetVisible(sketch, false); } catch { }

            string handleId = context.AddHandle(sketch, kind, string.IsNullOrEmpty(name) ? "(3D 曲线)" : name);
            string displayName = null;
            try { displayName = SafeString(Get(sketch, "DisplayName")); } catch { }

            return new
            {
                op,
                name,
                status = "ok",
                displayName,
                handle = handleId,
                curveType = kind,
                curveCount,
                resolved,
                hint = "3D 曲线不进特征树;后续 op 可用 @name / obj-K 引用这个 3D 草图。"
            };
        }

        private static double Dist3(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>三点定平面法向:((start-center) × (end-center)),归一化;三点共线返回 null。</summary>
        private static double[] NormalOf3(double[] start, double[] end, double[] center)
        {
            double[] u = { start[0] - center[0], start[1] - center[1], start[2] - center[2] };
            double[] v = { end[0] - center[0], end[1] - center[1], end[2] - center[2] };
            double[] n = { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
            double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (len < 1e-12) return null;
            return new[] { n[0] / len, n[1] / len, n[2] / len };
        }

        private static double[] Normalize3(double[] n)
        {
            double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (len < 1e-12) return n;
            return new[] { n[0] / len, n[1] / len, n[2] / len };
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
        // 三个 op 的共用约定:
        //  - 【凸台】在空零件上也能建:走首特征通道 Models.AddLoftedProtrusion(18 参)/
        //    Models.AddSweptProtrusion(15 参)/ Models.AddFiniteBaseHelix(17 参)。
        //    这三个方法与对应集合版(AddSimple / Add / AddFinite)的前若干个参数同序同义,
        //    差别只在多出的段映射 / 起止面 / 相切 / 锥度等可选位(一律 igNone / 0.0 / null),
        //    以及★返回值是【新建的 Model(设计体)】而不是特征对象——必须回取特征,
        //    否则 FeatureResult 的 Status 校验与失败回滚都会落空。
        //    (2026-10-08 修正:此处原写"首特征通道 P2 统一不开放",实测不成立——
        //     那是未经验证的假设,曾迫使 sweep/loft/helix 全都要求先建占位基体。)
        //  - 【除料】必须有材料可切,空模型上仍拒绝,提示先 extrude;
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
            // 空零件也允许放样凸台:首特征走 Models.AddLoftedProtrusion(见下方实建分支)。
            // 除料则必须有材料可切,空模型上无意义,维持拒绝。
            bool isFirstFeature = Count(models) == 0;
            if (isFirstFeature && isCut)
                return new
                {
                    op = "loft", name = name, status = "error",
                    message = "放样除料需要已有实体(无材料可切)——先 extrude,或改用放样凸台。",
                    diagnosis = "除料是把已有实体切掉一块,空模型没有料。"
                };

            object model = isFirstFeature ? null : Get(models, "Item", 1);
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

                // ★ AddSimple 含 SAFEARRAY 参数(CrossSections/Origins):本机实测 IDispatch 手工封送通道
                //   直接崩 SE(0x800706BE RPC 失败,2026-09-23,SE 2022)——与 sweep/helix 同因,必须走 PIA 强类型。
                //   强类型 9 参 = 7 必选 + NumGuideCurves/GuideCurves(无导线传 0/null),Interop.dll 反射核实。
                //   MaterialSide=igLeft(1)、Start/EndTangentType=igNone(44):SDK VB 示例取值。
                Array sectionArr = sections, typeArr = types, originArr = origins;
                object featObj;
                if (isFirstFeature)
                {
                    // 首特征放样:Models.AddLoftedProtrusion(18 参)。比集合版 AddSimple 多出
                    // SegmentMaps(第 5 参)与两端的 Extent / Tangent 组;本工具不做起止面与相切控制,
                    // 这些位置一律 igNone / 0.0 / null,语义与 AddSimple 的默认取值一致。
                    // ★ 返回的是【新建的 Model(设计体)】而不是特征对象,必须回取特征:
                    //   否则 FeatureResult 读 Status 会读到设计体,僵尸特征也回滚不掉。
                    SolidEdgePart.Model created = ((SolidEdgePart.Models)models).AddLoftedProtrusion(
                        n, sectionArr, typeArr, originArr, 0,
                        SolidEdgePart.FeaturePropertyConstants.igLeft,
                        SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null,
                        SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null,
                        SolidEdgePart.FeaturePropertyConstants.igNone, 0.0,
                        SolidEdgePart.FeaturePropertyConstants.igNone, 0.0,
                        0, null);
                    featObj = created.LoftedProtrusions.Item(1);
                }
                else
                {
                    object coll = Get(model, isCut ? "LoftedCutouts" : "LoftedProtrusions");
                    featObj = isCut
                        ? ((SolidEdgePart.LoftedCutouts)coll).AddSimple(n, sectionArr, typeArr, originArr,
                            SolidEdgePart.FeaturePropertyConstants.igLeft,
                            SolidEdgePart.FeaturePropertyConstants.igNone,
                            SolidEdgePart.FeaturePropertyConstants.igNone, 0, null)
                        : ((SolidEdgePart.LoftedProtrusions)coll).AddSimple(n, sectionArr, typeArr, originArr,
                            SolidEdgePart.FeaturePropertyConstants.igLeft,
                            SolidEdgePart.FeaturePropertyConstants.igNone,
                            SolidEdgePart.FeaturePropertyConstants.igNone, 0, null);
                }

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
            // 空零件也允许扫掠凸台:首特征走 Models.AddSweptProtrusion(见下方实建分支)。
            // 除料则必须有材料可切,空模型上无意义,维持拒绝。
            bool isFirstFeature = Count(models) == 0;
            if (isFirstFeature && isCut)
                return new
                {
                    op = "sweep", name = name, status = "error",
                    message = "扫掠除料需要已有实体(无材料可切)——先 extrude,或改用扫掠凸台。",
                    diagnosis = "除料是把已有实体切掉一块,空模型没有料。"
                };

            object model = isFirstFeature ? null : Get(models, "Item", 1);
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
                Array traceArr = new object[] { profiles[0] };
                Array traceTypeArr = new object[] { 48 };
                Array sectionArr = (Array)sections;   // object[] → Array,ByRef SAFEARRAY 编组
                Array sectionTypeArr = (Array)types;
                Array originArr = (Array)origins;
                object featObj;
                if (isFirstFeature)
                {
                    // 首特征扫掠:Models.AddSweptProtrusion——15 参,与集合版 SweptProtrusions.Add 完全同签名。
                    // ★ 返回值是【新建的 Model(设计体)】而不是特征对象,必须回取特征:
                    //   否则 FeatureResult 读 Status 会读到设计体,僵尸特征也回滚不掉。
                    SolidEdgePart.Model created = ((SolidEdgePart.Models)models).AddSweptProtrusion(
                        1, traceArr, traceTypeArr, nSec, sectionArr, sectionTypeArr, originArr, 0,
                        SolidEdgePart.FeaturePropertyConstants.igLeft,
                        SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null,
                        SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null);
                    featObj = created.SweptProtrusions.Item(1);
                }
                else
                {
                    var m = (SolidEdgePart.Model)model;
                    featObj = isCut
                        ? m.SweptCutouts.Add(1, traceArr, traceTypeArr, nSec, sectionArr, sectionTypeArr, originArr, 0,
                                SolidEdgePart.FeaturePropertyConstants.igLeft,
                                SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null,
                                SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null)
                        : m.SweptProtrusions.Add(1, traceArr, traceTypeArr, nSec, sectionArr, sectionTypeArr, originArr, 0,
                                SolidEdgePart.FeaturePropertyConstants.igLeft,
                                SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null,
                                SolidEdgePart.FeaturePropertyConstants.igNone, 0.0, null);
                }

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
            // 空零件也允许螺旋凸台:首特征走 Models.AddFiniteBaseHelix(见下方实建分支)。
            // 除料同上,必须有材料可切。
            bool isFirstFeature = Count(models) == 0;
            if (isFirstFeature && isCut)
                return new
                {
                    op = "helix", name = name, status = "error",
                    message = "螺旋除料需要已有实体(无材料可切)——先 extrude,或改用螺旋凸台。",
                    diagnosis = "除料是把已有实体切掉一块,空模型没有料。"
                };

            object model = isFirstFeature ? null : Get(models, "Item", 1);
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
                if (isFirstFeature)
                {
                    // 首特征螺旋:Models.AddFiniteBaseHelix(17 参)。前 9 参与集合版 AddFinite 同序同义,
                    // 其后 8 参是锥角/端部螺距/起止面等可选控制,本工具不涉及,一律 null。
                    // ★ 返回的是【新建的 Model(设计体)】而不是特征对象,必须回取特征:
                    //   否则 FeatureResult 读 Status 会读到设计体,僵尸特征也回滚不掉。
                    SolidEdgePart.Model created = ((SolidEdgePart.Models)models).AddFiniteBaseHelix(
                        (SolidEdgePart.RefAxis)pair[1],
                        SolidEdgePart.FeaturePropertyConstants.igStart, 1, ref csArr,
                        SolidEdgePart.FeaturePropertyConstants.igRight,
                        height, pitch, turns,
                        SolidEdgePart.FeaturePropertyConstants.igRight,
                        null, null, null, null, null, null, null, null);
                    featObj = created.HelixProtrusions.Item(1);
                }
                else if (isCut)
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

            // 开口样条(2026-11-02):非周期截面,锚点必须是轮廓上【真实一点】,传 0 会静默无几何
            // (开口截面放样建出 faceCount=0 僵尸的真凶就是这里)。闭合样条仍传 0(已验证可用,别动)。
            if (entry.HasSpline && !entry.SplineClosed && entry.SplinePoints != null && entry.SplinePoints.Length > 0)
                return new double[] { entry.SplinePoints[0][0], entry.SplinePoints[0][1] };

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

            DrawSketchAxisFrame(profile, null);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

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

            DrawSketchAxisFrame(profile, null);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

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

            DrawSketchAxisFrame(profile, specWarnings);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

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

            // 2) 旋转轴:独立构造线,不参与上面的闭环约束。
            //    ★ 2026-10-24 标准画法:轴先建(供 symdia 尺寸锚定),autoconstraint 时加
            //    方向约束 + Fix + 【HA 锚定】——把截面最低顶点与轴线低端点水平对齐,
            //    锁死截面的 v 平移(标准件解剖:用户草图正是靠"轴+对齐+对称直径"全约束的)。
            object axisLine = Call(lines, "AddBy2Points",
                new object[] { spec.AxisP1[0], spec.AxisP1[1], spec.AxisP2[0], spec.AxisP2[1] });

            if (spec.AutoConstraint == true)
            {
                try
                {
                    if (Math.Abs(spec.AxisP2[0] - spec.AxisP1[0]) <= 1e-9)
                        Call(relations, "AddVertical", new object[] { axisLine });
                    else if (Math.Abs(spec.AxisP2[1] - spec.AxisP1[1]) <= 1e-9)
                        Call(relations, "AddHorizontal", new object[] { axisLine });
                }
                catch (Exception ex) { specWarnings.Add("axisDir: " + ex.Message); }
                try { Call(relations, "AddFix", new object[] { axisLine }); }
                catch (Exception ex) { specWarnings.Add("axisFix: " + ex.Message); }

                // HA 锚定:截面最低顶点(tie 取 u 最小) ↔ 轴线低端点
                try
                {
                    int aIdx = spec.AxisP1[1] <= spec.AxisP2[1] ? 0 : 1;
                    int bestLine = -1, bestEnd = 0;
                    double bestY = double.MaxValue, bestX = double.MaxValue;
                    for (int i = 0; i < loopLines.Count; i++)
                    {
                        var (ln, p0, p1) = loopLines[i];
                        // 端点0=p0
                        if (p0[1] < bestY - 1e-12 || (Math.Abs(p0[1] - bestY) <= 1e-12 && p0[0] < bestX - 1e-12))
                        { bestY = p0[1]; bestX = p0[0]; bestLine = i; bestEnd = 0; }
                        if (p1[1] < bestY - 1e-12 || (Math.Abs(p1[1] - bestY) <= 1e-12 && p1[0] < bestX - 1e-12))
                        { bestY = p1[1]; bestX = p1[0]; bestLine = i; bestEnd = 1; }
                    }
                    if (bestLine >= 0)
                        Call(relations, "AddHorizontalAlign",
                            new object[] { axisLine, aIdx, loopLines[bestLine].line, bestEnd });
                }
                catch (Exception ex) { specWarnings.Add("axisAnchor(HA): " + ex.Message); }
            }

            // 1.5) 声明式约束/标注(必须在 End 之前的开放上下文里;轴不占 dims 的 element 索引;
            //      symdia 尺寸以轴线为锚)
            ApplySpecConstraints(context, doc, profile, spec, loopLines, specWarnings,
                axisLine, spec.AxisP1, spec.AxisP2);

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
        /// 特征建完立刻读 Status,按官方 5 档分类处置(ClassifyFeatureStatus):
        ///   · 仅 igFeatureFailed(1216476311,几何未生成)= 僵尸 → 判失败并【自动删除特征+草图】回滚;
        ///   · igFeatureWarned(1216476312,已生成但有警告)、igFeatureSuppressed(1216476313,用户主动压缩)、
        ///     igFeatureRolledBack(1216476314,被回滚) → 【一律保留】,status 仍为 "ok"(特征确实在模型树上,
        ///     后续特征可继续引用),但返回体里带 featureHealth + 官方枚举名 + warnings,如实标注不健康原因;
        ///   · 未知状态码(含 Status 读不到) → 【保留】并上报,交人工确认——宁可漏删不可误删。
        ///
        /// 2026-10-14 修正:旧版 `status != StatusOk` 即判失败并删除,把 Warned/Suppressed/RolledBack 全误杀,
        /// 触发"建了删、删了建"死循环(带良性警告的好特征可能永远建不成)。现所有判定统一走 ClassifyFeatureStatus。
        /// </summary>
        private static object FeatureResult(string op, string name, object featObj, SolidEdgeContext context,
            object profile = null, string kindOverride = null, List<string> specWarnings = null,
            object resolved = null, List<object> extraProfiles = null, bool ignoreUnderDefined = false)
        {
            string featureName = SafeString(Get(featObj, "Name"));
            long? status = SafeLong(Get(featObj, "Status"));
            FeatureHealth health = ClassifyFeatureStatus(status);

            if (health == FeatureHealth.Zombie)
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

                // 走到这里只有一种可能:health == Zombie(igFeatureFailed,1216476311)。
                string reason = HealthMeaning(FeatureHealth.Zombie);
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
                    featureStatusMeaning = CodeCatalog.DescribeSeEnum("FeatureStatusConstants", status.Value),
                    featureHealth = HealthEnumName(health),
                    limitations = new[] { "几何未生成(igFeatureFailed),已自动删除回滚" },
                    source = "COM:Feature.Status",
                    rolledBack = rolledBack,
                    sketchCleaned = sketchCleaned,
                    resolved = resolved,
                    message = reason + ":Status=" + status.Value + " / " + HealthEnumName(health) + "(正常应为 " + StatusOk + ");" +
                              (rolledBack ? "已自动删除该特征" : "自动删除失败,请手动删除该僵尸特征") +
                              (profile == null ? "。" : (sketchCleaned ? ",并清理了它的草图。" : ",但草图清理失败。")),
                    diagnosis = diagnosis,
                    fix = new { action = "fix_and_retry", check = new[] { "plane", "side", "形状位置", "depth" } }
                };
            }

            string kind = kindOverride
                ?? (op == "cut" ? "ExtrudedCutout"
                : (op == "revolve" ? "RevolvedProtrusion" : "ExtrudedProtrusion"));
            List<string> warningsOut = specWarnings;

            // ★ 2026-11-06 修复:op 的 name 落到 SE 特征名(PutName,可写已真机实测)。
            //   此前特征名始终是 SE 默认名(RevolvedProtrusion_1 等),而 replaceOf / pattern of 的
            //   按名匹配(FindFeatureByName)只认 SE 名——调用方按通用规则 12 传 op 名时永远匹配不到,
            //   replaceOf 静默空删(2026-11-05 法兰重建实测:旧特征没删,新旧并集成杂合体)。
            //   命名失败仅 warning 不阻断(特征保留 SE 名,replaceOf 仍可用 SE 名匹配);
            //   RPC_E_CANTCALLOUT_INEXTERNALCALL(消息筛选器)实测偶发,重试两次。
            if (!string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    if (!string.Equals(featureName, name, StringComparison.Ordinal))
                    {
                        bool renamed = false;
                        Exception lastErr = null;
                        for (int attempt = 0; attempt < 3 && !renamed; attempt++)
                        {
                            try
                            {
                                Put(featObj, "Name", name);
                                renamed = string.Equals(SafeString(Get(featObj, "Name")), name, StringComparison.Ordinal);
                            }
                            catch (Exception ex)
                            {
                                lastErr = ex;
                                if (attempt < 2) System.Threading.Thread.Sleep(60);
                            }
                        }
                        if (!renamed)
                        {
                            warningsOut = warningsOut ?? specWarnings ?? new List<string>();
                            warningsOut.Add("特征命名失败(保留 SE 名 " + featureName + ",replaceOf/pattern of 请用 SE 名匹配): " +
                                            (lastErr == null ? "回读不一致" : lastErr.Message));
                        }
                        else
                        {
                            featureName = name;
                        }
                    }
                }
                catch (Exception ex)
                {
                    warningsOut = warningsOut ?? specWarnings ?? new List<string>();
                    warningsOut.Add("特征命名失败(保留 SE 名 " + featureName + "): " + ex.Message);
                }
            }
            string handleId = context.AddHandle(featObj, kind, featureName);

            // ★ 轮廓约束状态(2026-09-14 新增):把"草图到底锁没锁住"从隐式变成显式。
            //   欠约束不算错误(几何已按给定坐标成形),但它意味着 SE 语义上没锁死 ——
            //   改一个尺寸、或局部面索引漂移时,几何可能整片走位,而调用方从返回值看不出任何征兆。
            //   ignoreUnderDefined=true(pattern 载体轮廓):里面只有 2D 阵列定义、无草图几何,欠约束语义不适用。
            bool? underDefined = ignoreUnderDefined ? (bool?)null : TryUnderDefined(profile);
            if (underDefined == true)
            {
                warningsOut = (specWarnings == null) ? new List<string>() : new List<string>(specWarnings);
                warningsOut.Add("轮廓仍欠约束(ProfileSet.IsUnderDefined=True):几何已按坐标成形,但 SE 语义上未完全锁死," +
                                "后续改尺寸或局部面索引漂移时可能整片移位。要完全约束请用 autoconstraint+fixorigin,或补 dims/约束。");
            }

            // ★ 非 OK、非僵尸的健康档(2026-10-14 新增):特征真实存在,【保留不删】,
            //   但必须让调用方看见"它不是 igFeatureOK"——否则这个修复就只剩"不删"而已。
            //   status 仍为 "ok"(特征在模型树上,批量循环可继续),不健康的事实由 featureHealth + warnings 承载。
            if (health != FeatureHealth.Ok)
            {
                warningsOut = (warningsOut == null) ? new List<string>() : new List<string>(warningsOut);
                string head = status.HasValue
                    ? "特征 Status=" + status.Value + "(" + HealthEnumName(health) + "):" + HealthMeaning(health) + "。已【保留】该特征未删除"
                    : "未能读到特征 Status(COM 读值失败):无法确认几何是否生成,已【保留】该特征未删除";
                if (health == FeatureHealth.Unknown)
                    head += "。该码不在已知五档枚举(igFeatureOK/Failed/Warned/Suppressed/RolledBack)内,请人工确认后再处置";
                else if (health == FeatureHealth.Suppressed || health == FeatureHealth.RolledBack)
                    head += "。它当前不参与建模,后续特征若依赖它可能失败";
                warningsOut.Add(head + "。");
            }

            // P1-2 证据契约最小集:标注本次结果的"可信度来源"与"已知局限",
            // 让调用方一眼看出哪些是 COM 直读、哪些是推导、有哪些坑要留意。
            List<string> limitations = null;
            if (health == FeatureHealth.Warned)
                limitations = new List<string> { "特征已生成但带警告(igFeatureWarned),几何可用但建议复核" };
            else if (health == FeatureHealth.Suppressed)
                limitations = new List<string> { "特征被用户压缩(igFeatureSuppressed),不参与建模" };
            else if (health == FeatureHealth.RolledBack)
                limitations = new List<string> { "特征被回滚(igFeatureRolledBack),后续特征若依赖它可能失败" };
            else if (health == FeatureHealth.Unknown)
                limitations = new List<string> { "特征状态码未识别或读取失败,已保留未删除,需人工确认" };
            if (underDefined == true)
            {
                limitations = limitations ?? new List<string>();
                limitations.Add("轮廓欠约束,几何可能因尺寸/索引漂移而走位");
            }

            // P3-1:提升为局部变量,以便 rangeboxConfidence 按实测值标注
            double[] rangebox = TryRangeBox(featObj);
            return new
            {
                op = op,
                name = name,
                status = "ok",
                feature = featureName,
                featureStatus = status,
                featureStatusMeaning = status.HasValue ? CodeCatalog.DescribeSeEnum("FeatureStatusConstants", status.Value) : null,
                featureHealth = HealthEnumName(health),
                // P3-1:摘除整体 confidence——本结构全是 COM 直读值,恒 observed 无信息量(P1-2 验收结论);
                // 状态语义由 featureHealth/limitations 承载。可信度改标在会读不到的字段旁(见 rangeboxConfidence)。
                limitations = limitations,
                source = "COM:Feature.Status",
                faces = FacesCount(featObj),
                rangebox = rangebox,
                rangeboxConfidence = rangebox != null ? EvidenceContract.Observed : EvidenceContract.Inferred,
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
                hint = "已内置校验:status=ok 表示特征确实存在(仅几何未生成的僵尸 " + StatusZombie +
                       " / igFeatureFailed 会被判失败并自动删除回滚);featureHealth 给出确切健康档" +
                       "(igFeatureOK=全健康 / igFeatureWarned=已生成但有警告 / Suppressed / RolledBack),非 igFeatureOK 时见 warnings。" +
                       "resolved = 本次【实际生效】的方向/参数" +
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

        // ---------------- 统一轴框架(2026-11-05 用户规矩) ----------------

        /// <summary>框架线长 20mm——全项目唯一长度常量(pattern tier② 辅助线同源引用,别再散落 0.05 之类的魔数)。</summary>
        internal const double SketchFrameLen = 0.02;

        /// <summary>
        /// 每张 2D 草图默认先画两条与草图 X/Y 轴重合的构造线(过原点,各自全约束),作为唯一的标准定位基准。
        /// 为什么需要它:SE 的隐式 H/V 轴 COM 摸不到(2026-10-25 实测),框架线就是它们的可达替身——
        /// 替代此前 polarhole 20mm 基线 / pattern 50mm 辅助线 / 各处临时辅助线各自为政的写法。
        /// 怎么用:定位 Connect / 尺寸一律引用 frameX/frameY(polarhole 的 R/角度尺寸即引用 frameX);
        /// 框架线是构造几何,不参与轮廓边界,也不占 dims 的 element 索引(索引只数声明的环几何)。
        /// 例外:hole op 孔轮廓不带任何构造几何(6311 僵尸,2026-10-24 十一轮 UI 实验),3D 草图无 2D 轴。
        /// 失败仅 warning 不阻断建模(框架是脚手架,缺了特征照建,几何零影响);返回 (frameX, frameY),失败为 (null, null)。
        /// </summary>
        private static (object frameX, object frameY) DrawSketchAxisFrame(object profile, List<string> warnings)
        {
            try
            {
                object lines = Get(profile, "Lines2d");
                object relations = Get(profile, "Relations2d");

                // 先画后切构造(同 polarhole/fixorigin 配方):普通线会被消费成轮廓边界,必须 ToggleConstruction
                object frameX = Call(lines, "AddBy2Points", new object[] { 0.0, 0.0, SketchFrameLen, 0.0 });
                object frameY = Call(lines, "AddBy2Points", new object[] { 0.0, 0.0, 0.0, SketchFrameLen });
                Call(profile, "ToggleConstruction", new object[] { frameX });
                Call(profile, "ToggleConstruction", new object[] { frameY });

                // 全约束(与 polarhole 真机收敛配方同族;顺序敏感:起点固定 → 方向 → 终点固定,
                // H/V 放中间,终点 KeyFix 才只带 1 个冗余分量——与 2026-10-25 Hole_26 配方一致)
                Call(relations, "AddKeypointFix", new object[] { frameX, 0 });
                Call(relations, "AddHorizontal", new object[] { frameX });
                Call(relations, "AddKeypointFix", new object[] { frameX, 1 });
                Call(relations, "AddKeypointFix", new object[] { frameY, 0 });
                Call(relations, "AddVertical", new object[] { frameY });
                Call(relations, "AddKeypointFix", new object[] { frameY, 1 });

                return (frameX, frameY);
            }
            catch (Exception ex)
            {
                if (warnings != null) warnings.Add("axisFrame: " + ex.Message + "(框架线缺失,特征照建;依赖框架的定位引用会落空)");
                return (null, null);
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

            if (spec.HasSpline)
            {
                WarnIfDeclarationsIgnored(spec, specWarnings, "spline");
                return CreateProfileSpline(doc, plane, spec, visible);
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

            // 2026-10-10: face 锚定的贴面平面存在一个 SE 侧硬限制(真机逐项复现确认) ——
            //   ① face 锚定的特征失败回滚时会删特征, 这次模型重建会让该面上那张贴面 RefPlane
            //      在 SE 内部失效;
            //   ② 而 SE 对同一个面【复用同一张】平面(RefPlanes.AddParallelByDistance 不新建),
            //      于是此后对同一 Face.ID 的任何面锚定都必然失败;
            //   ③ 该平面又【删不掉】(RefPlane.Delete 实测抛 0x80070057), 所以无法删掉重建。
            //   症状就是下面这行 Profiles.Add(plane) 抛
            //     IDispatch::Invoke 失败 [成员=Add] hr=0x800401FD, puArgErr=0
            //   且污染按 Face.ID 隔离(同文档内其它面完全正常)。
            // 工具侧无法根治(SE 行为), 故把裸 HRESULT 转成可操作的指引。
            // 注: 曾试过「回滚时不删特征」「保留草图」「释放 RCW 后从集合重取」三种规避,
            //     前两者只能减轻不能消除, 第三种无效(坏的是 SE 端对象而非 .NET 包装)。
            object profile;
            try
            {
                // 2026-10-10 修复尝试: 改走【强类型接口】而不是 ManualInvoke。
                // 实测对照(同一文档 / 同一张经历过回滚的 face:26 / 同一平面参数):
                //   脚本里用 dynamic(.NET COM binder) 调 Profiles.Add(plane) —— 成功;
                //   工具里用 ManualInvoke(手工 IDispatch::Invoke, COM 对象封送成 VT_DISPATCH) —— 失败 0x800401FD。
                // 说明问题出在封送通道而非 SE 侧, 故优先用强类型接口。
                object profilesColl = Get(profileSet, "Profiles");
                SolidEdgePart.Profiles profilesTyped = profilesColl as SolidEdgePart.Profiles;
                SolidEdgePart.RefPlane planeTyped = plane as SolidEdgePart.RefPlane;
                if (profilesTyped != null && planeTyped != null)
                {
                    profile = profilesTyped.Add(planeTyped);
                }
                else
                {
                    profile = Call(profilesColl, "Add", new object[] { plane });
                }
            }
            catch (Exception ex)
            {
                if (DescribeException(ex).IndexOf("0x800401FD", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException(
                        "face 锚定失败(0x800401FD): 面 " + spec.PlaneRef + " 上原先建立的贴面参考平面已在 SE 内部失效。" +
                        "SE 会对同一张实体面复用同一张贴面平面, 且这种平面无法删除/重建, 因此该面已不可再用。" +
                        "常见诱因: 此前有一次 face 锚定的特征失败回滚(例如 expectvolumedelta 体积对账不符)。" +
                        "解决办法: 换一个面 —— 用 face:<另一个 Face.ID>(可用一次 face:±X/±Y/±Z 轴选择器触发多解报错" +
                        "来读出候选 ID 列表), 或关闭并重新打开本文档后重试。", ex);
                }
                throw;
            }

            DrawSketchAxisFrame(profile, specWarnings);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

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

            DrawSketchAxisFrame(profile, null);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

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

            DrawSketchAxisFrame(profile, null);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

            object circles = Get(profile, "Circles2d");
            Call(circles, "AddByCenterRadius", new object[] { cx, cy, r });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建【真样条】截面(2026-10-31):BSplineCurves2d.AddByPointsWithCloseOption(order, 点数, ref 点数组, 是否闭合)。
        /// ★ 第 2 个参数是【点数】不是坐标个数,传 2N 会 DISP_E_BADINDEX(真机验证过)。
        /// ★ 点数组是扁平 double[2N](x/y 交替),必须声明成 System.Array 才能传给 ref Array 形参。
        /// 闭合时末点不必重复首点(SE 自动接回);开口样条可直接做 sweep 路径。
        /// 为什么需要它:折线截面会在曲面上留一圈折棱,而且折线壳 thicken 会偏置超量;
        /// 真样条出来的曲面光滑,加厚才稳。
        /// </summary>
        private static object CreateProfileSpline(object doc, object plane, FeatureSpec spec, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            DrawSketchAxisFrame(profile, null);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

            double[][] pts = spec.SplinePoints;
            Array flat = new double[pts.Length * 2];
            for (int i = 0; i < pts.Length; i++)
            {
                flat.SetValue(pts[i][0], i * 2);
                flat.SetValue(pts[i][1], i * 2 + 1);
            }

            object bsplinesObj = Get(profile, "BSplineCurves2d");
            var coll = (SolidEdgeFrameworkSupport.BSplineCurves2d)bsplinesObj;
            coll.AddByPointsWithCloseOption(spec.SplineOrder, pts.Length, ref flat, spec.SplineClosed);

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

            DrawSketchAxisFrame(profile, null);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

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

            DrawSketchAxisFrame(profile, null);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

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

            DrawSketchAxisFrame(profile, specWarnings);   // 统一轴框架:先画 X/Y 重合构造线(定位基准,2026-11-05 规矩)

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
        /// 2026-10-03 IR 面锚定:把 "face:&lt;Face.ID&gt;" / "face:±X/±Y/±Z[:outer|:inner]" / "face:±A:@x,y,z"
        /// 解析成一张【与实体面重合的隐藏参考平面】。
        /// SE2022 没有"直接在实体面上建草图"的 API(Sketches.AddByPlanarFace 抛 0x80004021),唯一通路是
        /// RefPlanes.AddParallelByDistance(face, 0, 1) 建贴面平面再在其上画。
        /// 同一张面被多个特征引用时只建一张(facePlanes 缓存,避免模型树堆一串无名 RefPlane)——
        /// 三种写法最终都归约成具体 Face 再取 Face.ID 走同一份缓存。
        /// localCoords(2026-10-04):coords:"local" 时轮廓按贴面平面局部 u/v 画——只要求【平面面】
        /// (斜面也行);global 模式则要求【轴向面】(全局 2D 坐标在斜面无定义)。
        /// </summary>
        private static object ResolveFacePlane(object doc, string planeRef, Dictionary<int, object> facePlanes, bool localCoords = false)
        {
            var fr = FeatureSpecParser.ParseFacePlaneRef(planeRef);
            if (fr == null || fr.ParseError != null)
                throw new ArgumentException("plane \"" + planeRef + "\" 无法解析:" + (fr == null ? "格式错" : fr.ParseError) +
                                            "(应为 face:<Face.ID> / face:±X±Y±Z / face:±A:outer|inner / face:±A:@x,y,z)。");

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
            else if (fr.Kind == "Point")
            {
                face = SelectFaceByPoint(doc, model, fr.Axis, fr.Point, out faceId);
            }
            else
            {
                face = SelectFaceByAxis(doc, model, fr.Axis, fr.Side, out faceId);
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
            // 注: 第 3 参数按 int 1 传, 对应 SolidEdgePart.ReferenceElementConstants.igReverseNormalSide。
            // 这个值沿用自最初实装, 已被大量真机面锚定成功用例验证; 实测 0(igRefEleInit)/1/2(igNormalSide)/-1
            // 在本场景行为一致, 故不做"看起来更对"的替换, 避免引入无谓的几何回归风险。
            object newPlane = Call(refPlanes, "AddParallelByDistance", new object[] { face, 0.0, 1 });
            SetVisible(newPlane, false);
            if (facePlanes != null) facePlanes[faceId] = newPlane;
            return newPlane;
        }

        /// <summary>
        /// 一个"法向沿全局轴"的平面面候选(2026-10-23)。**位置是唯一判据**:
        ///   Pos = Sign × 该面沿该轴的坐标(米),即"带符号法向投影" center·n̂;
        ///   因此任何轴向都统一成 outer = max(Pos) / inner = min(Pos),不需要为负轴写反向逻辑。
        /// 轴向平面面在该轴上的 GetRange 是退化的(min == max == 平面偏移),取平均即可。
        /// </summary>
        private sealed class AxisPlanarFace
        {
            public object Face;
            public int Id;
            public int AxisIndex;    // 0=X 1=Y 2=Z
            public int Sign;         // +1:外法向沿 +轴; -1:沿 -轴
            public double Pos;       // 米,= Sign × 坐标
            public double[] Min;     // 包围盒 min(米),点定位择近用
            public double[] Max;     // 包围盒 max(米)
        }

        /// <summary>
        /// 收集模型里全部"外法向沿全局 X/Y/Z"的平面面(斜面/曲面直接跳过)。
        /// 法向【轴】来自 TryGetFaceNormal;**朝向不看法向符号**(盘面路径的符号未校验,不可信),
        /// 只看面位置落在模型该轴范围的哪一半——与 :outer/:inner 用的是同一个位置量。
        /// 读不到包围盒/位置的面直接跳过(不猜)。
        /// </summary>
        private static List<AxisPlanarFace> CollectAxisPlanarFaces(object model, double[] modelBox)
        {
            var list = new List<AxisPlanarFace>();
            foreach (var f in FindPlaneFaces(model))
            {
                double[] n = TryGetFaceNormal(f);
                if (n == null) continue;

                double ax = Math.Abs(n[0]), ay = Math.Abs(n[1]), az = Math.Abs(n[2]);
                double max = Math.Max(ax, Math.Max(ay, az));
                if (max < 1 - 1e-6) continue;   // 斜面:法向不沿任何全局轴
                int fi = ax == max ? 0 : (ay == max ? 1 : 2);

                if (!TryGetFaceRange(f, out double[] minPt, out double[] maxPt)) continue;

                double coord = (minPt[fi] + maxPt[fi]) / 2.0;
                double mid = (modelBox[fi] + modelBox[fi + 3]) / 2.0;
                int fs = coord >= mid ? 1 : -1;

                list.Add(new AxisPlanarFace
                {
                    Face = f,
                    Id = SafeInt(Get(f, "ID")),
                    AxisIndex = fi,
                    Sign = fs,
                    Pos = fs * coord,
                    Min = minPt,
                    Max = maxPt
                });
            }
            return list;
        }

        /// <summary>读面的包围盒 [min(3), max(3)](米),走官方 Face.GetRange(ref Array, ref Array)。读不到返回 false。</summary>
        private static bool TryGetFaceRange(object face, out double[] minPt, out double[] maxPt)
        {
            minPt = null;
            maxPt = null;
            try
            {
                var f = (SolidEdgeGeometry.Face)face;
                Array lo = Array.CreateInstance(typeof(double), 0);
                Array hi = Array.CreateInstance(typeof(double), 0);
                f.GetRange(ref lo, ref hi);
                if (lo == null || hi == null || lo.Length < 3 || hi.Length < 3) return false;
                minPt = new double[3];
                maxPt = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    minPt[i] = Convert.ToDouble(lo.GetValue(i), CultureInfo.InvariantCulture);
                    maxPt[i] = Convert.ToDouble(hi.GetValue(i), CultureInfo.InvariantCulture);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>米 → 毫米文本(两位小数),报错文案统一用它。</summary>
        private static string MmText(double meters)
        {
            return (meters * 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>报错用:把轴向平面面清单连位置一起摊平(选不到面时给出全部可用面)。</summary>
        private static string DescribeAxisFaces(List<AxisPlanarFace> all)
        {
            if (all == null || all.Count == 0) return "(无)";
            var sb = new System.Text.StringBuilder();
            foreach (var c in all.OrderBy(c => c.AxisIndex).ThenByDescending(c => c.Pos))
            {
                sb.Append("\n  face:").Append(c.Id)
                  .Append(" (外法向 ").Append("XYZ"[c.AxisIndex]).Append(c.Sign > 0 ? "+" : "-")
                  .Append(", 位置 ").Append(MmText(c.Pos)).Append("mm)");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 轴向选择器(2026-10-23 重构):在"外法向沿全局 X/Y/Z"的平面面里,按朝 +A / −A 选面。
        ///   side = null   → 旧行为:唯一匹配才通过,多解报错(只改文案,不改语义)
        ///   side = outer  → 该方向 Pos 最大者(零件外皮);side = inner → 最小者(内腔侧)
        /// 朝向与极值共用同一个位置量 Pos(见 AxisPlanarFace),所以报错里印的位置
        /// 与 :outer/:inner 选中的面必然一致。
        /// </summary>
        private static object SelectFaceByAxis(object doc, object model, string axis, string side, out int faceId)
        {
            faceId = -1;
            int sign = axis[0] == '-' ? -1 : 1;
            int ai = axis[1] == 'X' ? 0 : (axis[1] == 'Y' ? 1 : 2);

            double[] box = TryModelRangeBox(doc);
            if (box == null || box.Length < 6)
                throw new ArgumentException("读不到模型包围盒,无法用轴向选择器(" + axis + ")定位面;请改用 face:<Face.ID>。");

            var all = CollectAxisPlanarFaces(model, box);
            var facing = all.Where(c => c.AxisIndex == ai && c.Sign == sign)
                            .OrderByDescending(c => c.Pos).ToList();

            if (facing.Count == 0)
                throw new ArgumentException("模型里没有外法向朝 " + axis + " 的轴向平面面。当前轴向平面面:" +
                                            DescribeAxisFaces(all) +
                                            "\n请改用 face:<Face.ID> 精确指定。");

            if (!string.IsNullOrEmpty(side))
            {
                var pick = side == "outer" ? facing[0] : facing[facing.Count - 1];
                faceId = pick.Id;
                return pick.Face;
            }

            if (facing.Count == 1)
            {
                faceId = facing[0].Id;
                return facing[0].Face;
            }

            // 多解:规格 §4——每个候选都要报位置,并标出 :outer/:inner 会选中谁。
            var sb = new System.Text.StringBuilder();
            sb.Append("外法向朝 ").Append(axis).Append(" 的轴向平面面有 ").Append(facing.Count).Append(" 个:");
            if (sign < 0)
                sb.Append("(位置 = 沿 ").Append(axis).Append(" 的带符号投影,数值越大越靠外)");
            for (int i = 0; i < facing.Count; i++)
            {
                var c = facing[i];
                sb.Append("\n  face:").Append(c.Id)
                  .Append(" (沿 ").Append(axis).Append(" 位置 ").Append(MmText(c.Pos)).Append("mm");
                if (facing.Count > 1 && i == 0) sb.Append(", 最外");
                if (facing.Count > 1 && i == facing.Count - 1) sb.Append(", 最内");
                sb.Append(")");
            }
            sb.Append("\n选择器无法区分。请改用 face:").Append(axis).Append(":outer / face:").Append(axis)
              .Append(":inner / face:").Append(axis).Append(":@x,y,z / face:<ID>。");
            throw new ArgumentException(sb.ToString());
        }

        /// <summary>
        /// 点定位选择器(2026-10-23):选"外法向朝 axis、且通过该点"的平面面。
        /// 算法:候选集与轴向选择器一致 → 按位置同层筛选(容差 0.01mm) → 同层多解按距离择优。
        /// ★ 距离是**近似**(SE2022 没有 Face.GetClosestPointOn,已反射核实):
        ///   用面包围盒到点的距离,凹面/带缺口的面会把"点在缺口里"误判为命中。
        ///   因此本函数只保证"层对了 + 挑了个最近的",不得对外声称精确。
        /// </summary>
        private static object SelectFaceByPoint(object doc, object model, string axis, double[] pointMeters, out int faceId)
        {
            faceId = -1;
            const double OnFaceToleranceMeters = 1e-5;   // 0.01mm,与规格一致

            int sign = axis[0] == '-' ? -1 : 1;
            int ai = axis[1] == 'X' ? 0 : (axis[1] == 'Y' ? 1 : 2);

            double[] box = TryModelRangeBox(doc);
            if (box == null || box.Length < 6)
                throw new ArgumentException("读不到模型包围盒,无法用点定位选择器(" + axis + ")定位面;请改用 face:<Face.ID>。");

            var all = CollectAxisPlanarFaces(model, box);
            var facing = all.Where(c => c.AxisIndex == ai && c.Sign == sign)
                            .OrderByDescending(c => c.Pos).ToList();

            if (facing.Count == 0)
                throw new ArgumentException("模型里没有外法向朝 " + axis + " 的轴向平面面。当前轴向平面面:" +
                                            DescribeAxisFaces(all) +
                                            "\n请改用 face:<Face.ID> 精确指定。");

            double target = sign * pointMeters[ai];
            var at = facing.Where(c => Math.Abs(c.Pos - target) <= OnFaceToleranceMeters).ToList();

            if (at.Count == 0)
            {
                var levels = facing.Select(c => Math.Round(c.Pos, 6)).Distinct()
                                   .OrderByDescending(v => v)
                                   .Select(v => MmText(v) + "mm");
                throw new ArgumentException("没有沿 " + axis + " 位置 " + MmText(target) +
                                            "mm 的轴向平面面(容差 0.01mm)。该方向可用层高(mm):" +
                                            string.Join(", ", levels) +
                                            "\n请核对点坐标(单位【米】,不是毫米)或改用 face:<Face.ID>。");
            }

            if (at.Count == 1)
            {
                faceId = at[0].Id;
                return at[0].Face;
            }

            // 同层多解(如顶面被一条槽分成两块):按"点到面"距离择优——近似,见方法注释。
            var best = at.OrderBy(c => PointToBoxDistance(c, pointMeters)).First();
            faceId = best.Id;
            return best.Face;
        }

        /// <summary>点到面包围盒的距离(米);点在盒内=0。近似量,不是点到曲面的真实距离。</summary>
        private static double PointToBoxDistance(AxisPlanarFace c, double[] p)
        {
            double d2 = 0;
            for (int i = 0; i < 3; i++)
            {
                double v = p[i];
                if (v < c.Min[i]) { double t = c.Min[i] - v; d2 += t * t; }
                else if (v > c.Max[i]) { double t = v - c.Max[i]; d2 += t * t; }
            }
            return System.Math.Sqrt(d2);
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
        ///   ②fixOrigin:辅助线锚定(2026-10-25 用户规矩:轮廓禁固定,固定只落辅助线)——
        ///     原点→首环顶点构造线,两端 AddKeypointFix + 连接顶点(顶点在原点时改锚最远顶点);
        ///   ③dims:AddLength(线)→ Constraint=true(驱动化,缺了 Edit 静默无效,2026-09-13 实测)
        ///     → PutName 改名进变量表 → Edit 值/公式。
        /// 单项失败只记 warning 不中断(几何已成型,绑定失败可事后补);硬 COM 异常向上抛。
        /// </summary>
        private static void ApplySpecConstraints(SolidEdgeContext context, object doc, object profile,
            FeatureSpec spec, List<(object line, double[] p0, double[] p1)> loopLines, List<string> warnings,
            object axisLine = null, double[] axisP1 = null, double[] axisP2 = null)
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
                // ★ 2026-10-25 用户规矩:轮廓(实体线)禁止固定约束,固定只允许出现在辅助线上。
                //   原 AddKeypointFix(首线起点,0) 直接固定轮廓关键点,违规。改为【辅助线锚定】:
                //   从原点画一条构造线到首环顶点 → 构造线两端 AddKeypointFix(固定的是辅助线,合规)
                //   → AddKeypoint 连到该顶点关键点。锚定坐标与原方案完全一致(几何零变化),
                //   同样消掉 2 个平移自由度;不依赖轴线是否被固定,revolve/extrude 通用。
                //   首顶点在原点时(如 rect [[0,0],[0.1,0.1]])辅助线退化为零长,自动改锚
                //   距原点最远的环顶点(有效闭合环必有非原点顶点,静态校验已拦退化面积)。
                try
                {
                    double[] target = loopLines[0].p0;
                    int tLine = 0;
                    if (Math.Abs(target[0]) < 1e-12 && Math.Abs(target[1]) < 1e-12)
                    {
                        double best = -1.0;
                        for (int i = 0; i < loopLines.Count; i++)
                        {
                            var q = loopLines[i].p0;
                            double d = q[0] * q[0] + q[1] * q[1];
                            if (d > best) { best = d; tLine = i; target = q; }
                        }
                        if (best < 1e-24) throw new Exception("首环顶点全部与原点重合,辅助线锚定退化");
                    }
                    object lines2d = Get(profile, "Lines2d");
                    object anchorAux = Call(lines2d, "AddBy2Points",
                        new object[] { 0.0, 0.0, target[0], target[1] });
                    Call(profile, "ToggleConstruction", new object[] { anchorAux });
                    Call(relations, "AddKeypointFix", new object[] { anchorAux, 0 });
                    Call(relations, "AddKeypointFix", new object[] { anchorAux, 1 });
                    Call(relations, "AddKeypoint", new object[] { anchorAux, 1, loopLines[tLine].line, 0 });
                }
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
                    // ★ 2026-10-24 标准画法:dims 支持 type:"symdia"(对称直径,旋转截面专用)。
                    //   ★ 2026-10-25 纠正参数顺序(用户看图指出"中心线不对"后实测):
                    //     签名 = AddSymmetricalDiameter(Object1,x1,y1,z1,kp1, Object2,x2,y2,z2,kp2),
                    //     **第一个对象必须是对称轴(中心线),第二个才是被标注的母线**。
                    //     写反(母线在前)时标注虽然 Value 也是跨轴直径、也能驱动,但 SE 把中心线画到母线那一侧。
                    //     调换后 GetRelated(0)=轴线、GetRelated(1)=母线,与 UI 手标一致(用户确认"这才是对的")。
                    //   keyPoint 必须 true(否则量出 0);Value=跨轴直径(米),如 Ø220 → 0.22。
                    object dim;
                    bool isSymDia = string.Equals(ds.Type, "symdia", StringComparison.OrdinalIgnoreCase);
                    if (isSymDia)
                    {
                        if (axisLine == null)
                        {
                            warnings.Add("dims[" + ds.Name + "]: symdia 只在 revolve(带 axis)下可用");
                            continue;
                        }
                        double mx = (loopLines[ds.Element].p0[0] + loopLines[ds.Element].p1[0]) / 2.0;
                        double my = (loopLines[ds.Element].p0[1] + loopLines[ds.Element].p1[1]) / 2.0;
                        double ax = (axisP1[0] + axisP2[0]) / 2.0;
                        double ay = (axisP1[1] + axisP2[1]) / 2.0;
                        dim = Call(dimsCol, "AddSymmetricalDiameter",
                            // 轴在前、母线在后(2026-10-25 修正,见上方注释)
                            new object[] { axisLine, ax, ay, 0.0, true, loopLines[ds.Element].line, mx, my, 0.0, true });
                    }
                    else
                    {
                        dim = Call(dimsCol, "AddLength", new object[] { loopLines[ds.Element].line });
                    }
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

                    // ★ 2026-10-24:symdia 跳过 Edit——对称直径值由截面坐标决定(标注即几何),
                    //   Edit 公式 "0.22" 会被 SE 按【mm】解释(=0.00022m)把直径改塌
                    //   (真机实测:revolve 塌成只剩内孔)。要改直径请改 polygon 坐标。
                    string varName = ds.Name;
                    try { varName = varsTyped.GetName(dim); } catch { /* 读不到就退回传入名 */ }

                    string formula = ds.Value ?? ds.Formula;
                    if (!string.IsNullOrEmpty(formula) && !isSymDia)
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
            // P2-1:统一走 EvidenceContract(过去私有副本行为漂移,已收编)
            return EvidenceContract.DescribeException(ex);
        }

        private static string Error(string message)
        {
            // 用报告专用选项:错误文案是给 LLM 直接读的,默认编码器会把中文转成 \uXXXX(可读性差、还更费 token)。
            return JsonSerializer.Serialize(new { status = "error", message = message }, FeatureValidator.JsonOpts);
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
