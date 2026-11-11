using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// se_model_build 的 op 手册:原本挤在 [Description] 里、每轮请求都要重发的 6,420 字符,
    /// 现在按 op 拆开存这里,改为"首次用到某 op 时注入一次 + 随时可主动查"。
    ///
    /// 为什么不放 Description:那是 tools 参数,每条 API 请求都重发;放进返回值只在本会话出现一份。
    ///
    /// 维护规矩:
    ///   1. 新增 op 必须同时在 FeatureRules.cs 的 OpRule(E101 白名单)与这里加条目,
    ///      FeatureValidatorTests 里的一致性单测会盯住两边是否对齐。
    ///   2. 条目第一行是索引行(Index() 直接取它),格式固定为"op 名 + 一句话 + 签名"。
    ///   3. 只写调用约定,不写日期、修订次数、设计动机、COM 内部对象 ID —— 那些留给 /// 注释。
    /// </summary>
    public static class OpManual
    {
        /// <summary>域标识。注入状态按 域:op 记账,装配域/配方域各用自己的前缀。</summary>
        public const string Domain = "model";

        /// <summary>索引顺序:与 FeatureRules.cs 的 E101 白名单保持同一顺序,便于肉眼核对。</summary>
        private static readonly string[] Order =
        {
            "plane", "extrude", "cut", "revolve", "fillet", "chamfer", "rib", "pattern",
            "hole", "polarhole", "boltcircle", "loft", "sweep", "helix",
            "draft", "split", "web_network", "extrude_surface", "loft_surface", "thicken", "delete_face",
            "spline3d", "arc3d", "bluesurf"
        };

        /// <summary>通用规则。与具体 op 无关,首次调用时随第一批 op 一起注入一次。</summary>
        public static readonly string Common =
            "通用规则(所有 op 共用)\n" +
            "1. 单位:坐标/长度一律【米】;angle 弧度、degrees 度、angleDeg 度。\n" +
            "2. 形状七选一,键平铺在特征对象顶层(没有 shape 包装):\n" +
            "   circle {\"center\":[x,y],\"radius\":r} 或简写 [x,y,r](圆轮廓拉伸=真圆柱)\n" +
            "   circles [[x,y,r],...] 一个轮廓多环,一次切/拉多个真圆孔\n" +
            "   slot {\"center\":[x,y],\"length\":总长,\"width\":宽,\"angle\":弧度?} 或简写 [x,y,长,宽](两端是半圆)\n" +
            "   spline [[u,v],...] 或 {\"points\":[[u,v],...],\"order\":3,\"closed\":true} 真 B 样条(曲面光滑,薄壁加厚首选)\n" +
            "   rect [[x0,y0],[x1,y1]] / polygon [[x,y],...] / loops 多环\n" +
            "3. plane 支持:RefPlane_1/2/3、@别名(前面 plane op 建的)、obj-K、face:<Face.ID>、" +
            "轴向选择器 face:±X/±Y/±Z、轴向极值 face:±A:outer|inner(该方向最外/最内一张,薄壁件首选)、" +
            "点定位 face:±A:@x,y,z(通过该点且外法向朝 ±A 的平面面,坐标米,缺轴报错)。\n" +
            "4. face 锚定:在已有实体面上加 extrude/cut/hole 时,plane 直接写该 Face.ID 或选择器。" +
            "coords 缺省/\"global\" = 轮廓按【全局世界坐标】解释(外法向 Z→(X,Y)、X→(Y,Z)、Y→(X,Z)),仅支持轴向面;" +
            "coords:\"local\" = 按该面贴面参考平面的局部 u/v 直接画,支持斜面,原点/轴向由 SE 决定" +
            "(建议先 se_read_geometry 读该面边界),local 下 slot 也可用。\n" +
            "5. face:±Z 同向多面时报错并列出候选:用 face:±Z:outer / face:±Z:inner 消歧(位置最大者=外皮、最小者=内腔)," +
            "或 face:±Z:@x,y,z 按层高定位。\n" +
            "6. 参数化(仅 rect/polygon/loops 直线环生效,圆/腰孔会忽略并回传 warning):" +
            "autoconstraint=true 按坐标自动补水平/垂直约束;" +
            "fixorigin=true 以辅助线锚定顶点(轮廓线禁固定,固定只落在原点→顶点的构造辅助线上);" +
            "dims=[{\"element\":0,\"name\":\"Len1\",\"value\":\"40 mm\"}] 给直线加长度标注并变成变量" +
            "(element 为跨环扁平 0-based 线索引,轴不占位;也可用 \"formula\":\"Rad1 - 5 mm\" 建关联式)。\n" +
            "7. 方向:dir=[dx,dy,dz] 世界方向向量,给了 dir 且未给 side 时按平面带符号法向 dot 换算 side" +
            "(RefPlane_1=+Z、RefPlane_2=+X、RefPlane_3=−Y),结果在 warnings 里带 dot 值可核对;" +
            "dir 换算出的 side 不再被方向自愈改写。\n" +
            "8. 体积对账:expectvolumedelta = 本特征应造成的体积增量(毫米³,数字或 [下限,上限]," +
            "extrude 为正、cut/hole 为负),建完即读实际增量核对,越界自动回滚并返回实际 vs 预期;" +
            "results 里附 volumeDeltaMm3。\n" +
            "9. 防误建:expectDocument = 期望的文档名,不符立即拒绝且一个特征都不建;" +
            "目标文档已有实体、本批只做除料/打孔时必须传 assumeStock=true,否则建前校验拒绝。\n" +
            "10. 校验:执行前先静态校验,有 error 直接拒绝并返回 issues(每条带 code/field/message/fix);" +
            "dryRun=true 只返回报告、不建任何特征。\n" +
            "11. 状态:只有几何未生成的僵尸特征(Status=1216476311)判失败并自动删除回滚;" +
            "Warned(1216476312)、Suppressed(1216476313)、RolledBack(1216476314) 一律保留不删。" +
            "status=ok 表示特征确实存在,是否完全健康看 featureHealth(igFeatureOK 才算全健康),非全健康时见 warnings。" +
            "任一特征失败即停止后续(避免连环僵尸),按该条 diagnosis 修正后重跑。\n" +
            "12. 重建语义:改参数重跑同一特征时,用 replaceOf 先删旧特征(逗号分隔多个名字,按序执行);" +
            "op 的 name 会直接落到 SE 特征名上(2026-11-06 修复),replaceOf / pattern 的 of 都可以直接写 op 名或 SE 名;" +
            "replaceOf 支持的 op:extrude/cut/revolve/hole/polarhole/boltcircle/pattern,其它 op 传了会被忽略并报 W433;" +
            "Pattern 必须排在它的种子特征【之前】——种子先删会连挂阵列;Hole 特征删除前会先删 HoleData;" +
            "找不到的名字跳过并记 warning,删除清单在 resolved.replaced 回显。\n" +
            "13. 脚本侧 interop 必须与 SE 版本对齐:SE 2022 运行时是 222,而合并版 Interop.SolidEdge 常见是 108," +
            "版本错配会让合法成员报 DISP_E_UNKNOWNNAME(看起来像\"接口不可用\",极易得出错误结论)。" +
            "先打印 typeof(Application).Assembly.FullName 与 app.Version 核对(手册里说的\"实测可用\"都指 op 内部路径,不保证脚本直调)。" +
            "不等时用 TlbImp 从 SE 自带类型库现生成:framewrk.tlb→SolidEdgeFramework、fwksupp.tlb→SolidEdgeFrameworkSupport、" +
            "geometry.tlb→SolidEdgeGeometry、constant.tlb→SolidEdgeConstants、Part.tlb→SolidEdgePart(/reference: 依次指向先生成的);" +
            "旧版签名是 ref Array,222 版是 ref object[] / ref double[]。\n" +
            "14. 轴框架(默认,2026-11-05 规矩):每张 2D 草图自动先画两条与草图 X/Y 轴重合的全约束构造线" +
            "(过原点,20mm)——COM 摸不到隐式 H/V 轴,框架线就是它们的可达替身,也是唯一的定位基准。" +
            "怎么用:给草图补定位 Connect/尺寸时一律引用框架线(polarhole 的 R/角度尺寸即引用框架 X 线)," +
            "不要再自画临时辅助线;构造线不参与轮廓边界、不占 dims 的 element 索引。" +
            "例外:hole op 孔轮廓不带任何构造几何(6311 僵尸,2026-10-24 十一轮 UI 实验),3D 草图无 2D 轴框架;";

        private static readonly Dictionary<string, string> _entries =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["plane"] =
                "plane 局部参考面(平行偏移),后续特征用 plane:\"@别名\" 引用。\n" +
                "{op:\"plane\", name:\"front\", base:\"RefPlane_1|RefPlane_2|RefPlane_3|@别名\", distance:偏移米}\n" +
                "- distance 省略 = 与 base 重合\n" +
                "- 不产生实体,只把一张面登记成别名;返回值带 handle 可直接给 objectId",

            ["doc"] =
                "doc 文档分段符:从这里开始的新特征建在**新建的文档**里(一次调用建多零件)。\n" +
                "{op:\"doc\", type?:part|assembly|draft|sheetmetal|weldment, save?:路径, overwrite?:false, name?:段名}\n" +
                "- 段内 @别名/贴面缓存不跨段;每段自己校验、自己体积对账\n" +
                "- save 给了就在该段建完后落盘(默认拒绝覆盖已存在文件)\n" +
                "- 与 sets 行数组(表驱动)配合:模板每行建一段,文件名可用 {{idx}} 等占位符",

            ["measure"] =
                "measure 读模型实测值写进求值环境,后面的段用 {{as}} 引用(不碰模型)。\n" +
                "{op:\"measure\", property:\"volume\", as:\"v1\"}  →  env: v1(m³)、v1.mm3(mm³)\n" +
                "- 目前只支持 volume;面/边/包围盒请用 se_read_geometry / se_read_model_state\n" +
                "- 是分段符:前后特征是不同段,作用在当时的活动/新建文档上",

            ["compute"] =
                "compute 在运行时环境上算表达式(可引用 sets 与前面 measure 的值),结果写回 {{as}}。\n" +
                "{op:\"compute\", expr:\"{{v1.mm3}} / 2\", as:\"half\"}\n" +
                "- 只算数、不改模型(改模型必须写后续声明式 op,护栏才管得住)\n" +
                "- 需要循环/条件/读几何请用 se_script_run,别在这里堆语法",

            ["extrude"] =
                "extrude 拉伸凸台(首个特征走 AddFiniteExtrudedProtrusion,后续走 ExtrudedProtrusions.AddFinite,自动并入同一实体)。\n" +
                "{op:\"extrude\", plane, 形状, side?:2, profileside?:1, depth?:米}\n" +
                "- side = ProfilePlaneSide(相对草图平面往哪一侧延伸),默认 2;取值 1/2\n" +
                "- circle 形状拉出来就是真圆柱(3 个面),不必用 revolve\n" +
                "- 空零件首个特征也能直接建",

            ["cut"] =
                "cut 除料(ExtrudedCutouts)。目标文档必须已有实体;若本批第一个特征就是 cut,必须传 assumeStock=true。\n" +
                "{op:\"cut\", plane, 形状, mode?:\"through_all\"(默认)|\"next\"|\"finite\"(需 depth), depth?, side?:1, profileside?:1}\n" +
                "- 切哪一侧 = side(延伸方向) + profileside(切轮廓内侧/外侧) 共同决定,两者都只有 1/2\n" +
                "- 调用方没显式给的维度,op 会自动按组合重试到几何正常(只纠正\"切到实体外\"的僵尸,合法但镜像的判不出来)\n" +
                "- 同平面多个孔用 circles 合并成一个轮廓一次切出;逐条 cut 是僵尸头号来源(W401)",

            ["revolve"] =
                "revolve 旋转(mode 缺省=凸台,mode:\"cut\"=旋转切割,需已有实体)。\n" +
                "{op:\"revolve\", plane, 形状, axis:[[u0,v0],[u1,v1]], angle?:弧度(默认 2π)|degrees?:度, mode?:\"cut\"}\n" +
                "- axis 是草图平面内的一条线(局部 u/v),不参与截面闭环,必须给;两点重合会被建前校验拒绝\n" +
                "- 截面用 rect/polygon/loops,不要用 circle(圆直接拉伸即可)\n" +
                "- 建完自动隐藏草图(COM 建特征不会自动隐藏)",

            ["fillet"] =
                "fillet 圆角(Rounds.Add)。\n" +
                "{op:\"fillet\", radius:米, edges:[{face:\"face:26\", edge:0}, ...]}\n" +
                "- edges 必须是已有实体的边;face 用 Face.ID(唯一稳定索引),edge 是该面内边的 0-based 序号\n" +
                "- 缺 radius 或 <=0 会被建前校验拒绝",

            ["chamfer"] =
                "chamfer 等距倒角(Chamfers.AddEqualSetback)。\n" +
                "{op:\"chamfer\", distance:米, edges:[{face:\"face:26\", edge:0}]}\n" +
                "- 边引用写法同 fillet;缺 distance 或 <=0 会被建前校验拒绝",

            ["rib"] =
                "rib 筋板/薄台(Ribs.Add)。\n" +
                "{op:\"rib\", plane:贴实体表面的平面, 闭合轮廓, thickness:米}\n" +
                "- 轮廓必须闭合,且 plane 必须贴实体表面,否则不出几何(SE 2022 实测,开放链不支持)\n" +
                "- 缺 thickness 或轮廓形状不对会被建前校验拒绝",

            ["pattern"] =
                "pattern 阵列,两种:圆周(缺省)与矩形(kind:\"rect\",2026-11-08 开放)。\n" +
                "【圆周】{op:\"pattern\", of:种子特征, plane:载体平面(法向=旋转轴), count:实例总数(含种子,>=2), " +
                "center?:[u,v](圆心,平面局部米,缺省[0,0]), spacing?:\"fit\"(默认,整圈均布 360/count), " +
                "spacingDeg?:固定角间距(度,与 fit 互斥), clockwise?:false, " +
                "radius?:阵列圆草图显示半径(米,默认0.05,纯视觉不参与定位), patterntype?:\"smart\"(默认)|\"fast\"}\n" +
                "- of 写本批前面 op 的 name / SE 特征名(如 ExtrudedCutout_2) / obj-K;op 的 name 会落到 SE 特征名,跨调用重建也能按名找到(2026-11-06 修复)\n" +
                "- 实例位置 = 种子绕过 center 的平面法向轴旋转 k×spacing,起始角由种子位置决定\n" +
                "- 建完自动挂圆心约束,三级选型:①直连文档里已有的平面级隐式轴线(仅 UI 编辑过草图的平面才有," +
                "要求 center=(0,0))②辅助构造线(20mm,与轴框架同常量)+Connect(无轴线文档的默认档,任意 center 可用)" +
                "③KeyFix 弧心原地固定兜底;centerConstraint 回显实际档位(direct/attached/keyfix)\n" +
                "- 改圆心请用 replaceOf 重建\n" +
                "【矩形 kind:\"rect】{op:\"pattern\", kind:\"rect\", of:种子特征, plane:载体平面(保守用种子所在平面), " +
                "xcount,ycount(>=1,=1 即单行/单列), xspacing,yspacing(米,>0), " +
                "center?:[u,v](阵列角点=种子位置,缺省[0,0]), radius?(载体种子位圆显示半径,米,默认0.05), " +
                "suppress?:[序号](1-based occurrence 单项抑制), patterntype?}\n" +
                "- ★ angle 只支持 0(缺省):angle≠0 的实例方向语义未破解(2026-11-08 真机实测 angle=15° 时 +X 实例落在 229.4°," +
                "且实例可伸出毛坯边界),非零会被静态校验拒绝\n" +
                "- 实例位置 = origin + col·xspacing·û + row·yspacing·v̂;occurrence 序号 1-based 列主序(X 先递增 Y):" +
                "col=(k-1)/ycount 整除,row=(k-1)%ycount(2026-11-08 实测)\n" +
                "- suppress 置位后 NumberOfOccurrences 不变(定义数),Recompute 后该实例几何消失;可逆(面 ID 复原)\n" +
                "- 改行列数:se_set_feature_parameter(parameter=\"xcount\"|\"ycount\")(载体 2D 定义可写);" +
                "改间距/角度/种子位:replaceOf 重建(XSpace/YSpace 在 2D 对象上不可写)\n" +
                "- 间距护栏:实例间距必须 > 相邻特征尺寸之和,相切接触 → 布尔退化 → 僵尸(2026-11-08 实测)",

            ["hole"] =
                "hole 真孔特征(Model.Holes,带 HoleData)。\n" +
                "{op:\"hole\", plane, circle:{center:[x,y],radius:r}|circles:[[x,y,r],...]|center:[x,y], " +
                "mode?:\"through_all\"(默认)|\"next\"|\"finite\"(需 depth), thread?:\"M10\", diameter?:光孔直径(米), " +
                "threadoption?, standard?, side?:1|2}\n" +
                "- 只接受圆类形状(circle/circles/center),其他形状会被建前校验拒绝\n" +
                "- thread:\"M10\" 时底孔/内螺纹小径/螺距按 SE 孔数据库自动带出(内置 GB Metric 全表);" +
                "可写 M10(粗牙)/M10x1.25(细牙)/Rp1/2(直管,可省牙数)/R1/2(锥管)\n" +
                "- 给了 thread 时 diameter 被忽略,不必再给直径\n" +
                "- threadoption: minor(默认,内螺纹小径)/tapdrill(丝锥直径)/nominal(标称直径) 决定实际孔径;" +
                "standard 目前只认 \"GB Metric\"\n" +
                "- 锥管螺纹建出来是圆柱底孔,要锥孔请用 UI\n" +
                "- 同平面多孔用 circles 一次切出",

            ["polarhole"] =
                "polarhole 极坐标定位孔(圆形法兰定位孔,全约束配方)。每个角度一条独立 Hole。\n" +
                "{op:\"polarhole\", plane, boltdia:分布圆直径(米), angleDeg:单孔角度(度)|angles:[度,...]多孔各一独立特征, " +
                "diameter:孔径(米)|thread:\"M10\", mode?:\"all\"(默认贯穿)|\"next\"|\"finite\"(+depth), " +
                "side?:1|2(缺省1,僵尸自愈换侧), name?}\n" +
                "- 孔位 = (boltdia/2)·(cosθ,sinθ),平面局部 u/v,逆时针为正\n" +
                "- 定位草图自动全约束:轴框架(2 条 X/Y 重合构造线,通用规则 14)+定位线(原点→孔心)KeyFix+连孔心+" +
                "长度/角度双驱动尺寸,角度引用框架 X 线(IsUnderDefined=False)\n" +
                "- 给 name 则定位尺寸入变量表 {name}_R / {name}_Angle,改变量重算孔随动\n" +
                "- 角度 0/180 拒收(与基准线共线退化)\n" +
                "- 要\"孔数可改\"的螺栓孔群用 boltcircle,不要用 angles[]",

            ["boltcircle"] =
                "boltcircle 螺栓孔群 = 1 个种子孔 + 1 个圆周阵列(内部复用 polarhole + pattern,不新增 COM 调用路径)。\n" +
                "{op:\"boltcircle\", plane, boltdia:分布圆直径(米), count:孔数(含种子,>=2), diameter:孔径(米)|thread:\"M10\", " +
                "angleDeg?:起始角(度,缺省 360/count/2), center?:[u,v], " +
                "mode?:\"all\"(默认贯穿)|\"next\"|\"finite\"(+depth), side?:1|2, patterntype?:\"smart\"|\"fast\", name?}\n" +
                "- 改孔数:se_set_feature_parameter(feature=阵列名, parameter=count, value=N),整圈均布自动重排\n" +
                "- 与 polarhole 的 angles[] 区别:那是 N 个独立 Hole,孔数从此改不了\n" +
                "- angles[] 会被忽略并报 W431;分布圆/起始角由种子孔的 {name}_R / {name}_Angle 变量驱动\n" +
                "- 圆形法兰螺栓孔首选本 op",

            ["loft"] =
                "loft 放样(>=2 个截面)。\n" +
                "{op:\"loft\", profiles:[{plane,形状}, ...], origin?:[u,v] 截面锚点(缺省按形状推导), mode?:\"cut\"(默认凸台)}\n" +
                "- profiles 至少 2 项,每项一个单闭合轮廓\n" +
                "- 空零件上可直接建(首特征通道已开放);mode:\"cut\" 需已有实体",

            ["sweep"] =
                "sweep 扫掠。\n" +
                "{op:\"sweep\", profiles:[首项=路径, 其余=截面]}\n" +
                "- 路径用 trace 时按\"线段+真圆弧混排\"解释:trace:[{line:[[x,y],[x,y]]}, " +
                "{arc:{center:[x,y],start:[x,y],end:[x,y]}}];arc 取 start→end 的劣弧(<=180°),方向由几何判定,段间端点须重合\n" +
                "- 相切弯头:把圆心放在拐角的对角点,如 R20 从 (0.02,0.03) 转到 (0.04,0.05) 时 center=[0.04,0.03]\n" +
                "- polygon 加 fillet:R 时按顶点链自动倒圆角(圆心/切点由几何算出,必然与直段相切)——弯头首选," +
                "如 polygon:[[0,0],[0,0.05],[0.06,0.05]], fillet:0.02\n" +
                "- 不带 fillet 的 polygon 按开放链折线解释(>=2 点不闭合);circle/rect/loops 是闭合路径(扫一整圈)",

            ["helix"] =
                "helix 螺旋。\n" +
                "{op:\"helix\", plane, 单闭合截面, axis:[[u0,v0],[u1,v1]](同 revolve 的两点轴), " +
                "pitch?:螺距(米), height?:高度(米), revolutions?:圈数, mode?:\"cut\"}\n" +
                "- pitch/height/revolutions 三给二,第三个由 SE 推导;给不齐会被建前校验拒绝",

            ["draft"] =
                "draft 拔模(Drafts.Add)。\n" +
                "{op:\"draft\", faceOf:目标面(@别名/obj-K), refPlane:与之相交的参考面, " +
                "angle?:弧度(默认 π/36), side?:4(igInside)|5(igOutside)}\n" +
                "- refPlane 必须与目标面相交(枢轴语义),否则 Status=6311\n" +
                "- side 只认 4/5,其他值会被建前校验拒绝",

            ["split"] =
                "split 分割(Splits.Add)。\n" +
                "{op:\"split\", target:\"@别名|obj-K\", plane:分割平面}\n" +
                "- target 必须是本批前序 op 的 @别名或 obj-K;缺失/前向引用会被建前校验拒绝",

            ["web_network"] =
                "web_network 腹板网(WebNetworks.Add)。\n" +
                "{op:\"web_network\", plane, 闭合轮廓, thickness:米, depth:米}\n" +
                "- 内部用专属枚举 seWebExtendFinite + seWebProfileNoExtend,勿混用通用 FeaturePropertyConstants\n" +
                "- thickness/depth 必须 >0,否则建前校验拒绝",

            ["extrude_surface"] =
                "extrude_surface 曲面拉伸(Constructions.ExtrudedSurfaces.AddFinite)。\n" +
                "{op:\"extrude_surface\", plane, 形状, depth:米, side?}\n" +
                "- 轮廓拉成【曲面】非实体;产物面会被缓存供 thicken 引用\n" +
                "- 不产生体积,别用 expectvolumedelta 对账",

            ["loft_surface"] =
                "loft_surface 放样曲面(Constructions.LoftedSurfaces.Add,WantEndCaps=false)。\n" +
                "{op:\"loft_surface\", profiles:[{plane,形状}, ...], openProfile?:false, guides?:[...]}\n" +
                "- 与 loft 的区别:loft 是【实体】凸台,本 op 只出【曲面】,产物面缓存给 thicken 的 faceOf\n" +
                "- 永远不封端:封端件 Faces.Count=0(已是闭壳),再 thicken 必 E_INVALIDARG\n" +
                "- 截面 >=2,各自单闭合轮廓(circle/spline/rect/polygon/loops;slot 的切线分段轮廓放样会僵尸,别用;也不要 circles)\n" +
                "- 开口截面(拱形/半开放的样条,closed:false)默认【不放行】,会出 faceCount=0 的僵尸并自动回滚;" +
                "要做开口壳(鼠标壳、外壳件)必须显式给 \"openProfile\":true —— 底层 Add 本来就支持开口截面出开放壳," +
                "只是 op 默认只放行闭合轮廓(静态校验会提前拦,不用等建完回滚)\n" +
                "- 薄壁件(曲面→thicken)优先用 spline 截面:折线多边形会在曲面上留折棱,加厚还会偏置超量\n" +
                "- guides 导线(可选):[{plane, \"spline\":[[u,v],...]}] —— 必须是【开放样条】轮廓,穿过的点要落在各截面上;" +
                "用 3D 草图折线边当导线会建出 faces=0 的空特征(3D 空间里的导线请先用 spline3d 建曲线,本 op 的 guides 只吃平面样条轮廓)",

            ["thicken"] =
                "thicken 曲面加厚(Model.Thickens.Add;空文档首个设计体走 Models.AddThickenFeature)。\n" +
                "{op:\"thicken\", faceOf:指向 extrude_surface|loft_surface, thickness:米, side?:1}\n" +
                "- side 是 FeaturePropertyConstants:1 igLeft(默认,向曲面内侧加厚)/2 igRight(向外)/3 igSymmetric(两侧各半);" +
                "4/5 是拔模用的,传了必 E_INVALIDARG\n" +
                "- thickness 必须 >0,单位米\n" +
                "- 加厚失败先回读上游曲面 Faces.Count 再判因(2026-11-01 复测:封端曲面 WantEndCaps=true 本身 " +
                "Faces.Count=4 且 Model.Thickens.Add 可成功,\"面必须来自未封端曲面\"的旧表述作废,勿外推为 COM 通用规律)\n" +
                "- 加厚请用本 op,别在脚本里直调 Model.Thickens.Add:faces 数组在 .NET 互操作层难以传入" +
                "(强类型 / dynamic / PowerShell 晚绑定都不行,实测 SE 2022),而 op 内部路径已验证可用",

            ["delete_face"] =
                "delete_face 删面(DeleteBlends.Add 删 blend/round + DeleteFaces.Add 兜底)。\n" +
                "{op:\"delete_face\", faceOf?:\"@别名|obj-K\", faceNormal?:[nx,ny,nz], confirm:true}\n" +
                "- 必须显式 confirm:true,否则建前校验拒绝\n" +
                "- 删圆角面优先用 faceNormal 命中",

            ["spline3d"] =
                "spline3d 3D 样条(doc.Sketches3D → BSplineCurves3D.AddByPoints)。\n" +
                "{op:\"spline3d\", name:\"guide1\", points:[[x,y,z],...], closed?:false, visible?:false}\n" +
                "- points 是【世界坐标,米】,至少 2 点;不需要 plane(与 2D 轮廓的 spline 不是一回事)\n" +
                "- closed:true 时末点不必重复首点(SE 自动接回)\n" +
                "- 默认隐藏新建的 3D 草图;要看曲线给 visible:true\n" +
                "- 不产生特征:返回 handle + curveCount;几何核对不上时 curveCount=-1\n" +
                "- 典型用途:loft_surface 的空间导线(3D 折线当导线会出 faces=0 空特征,样条才光滑)",

            ["arc3d"] =
                "arc3d 3D 圆弧(doc.Sketches3D → Arcs3D.AddByStartEndCenterNormal)。\n" +
                "{op:\"arc3d\", name:\"arc1\", start:[x,y,z], end:[x,y,z], center:[x,y,z], normal?:[nx,ny,nz], visible?:false}\n" +
                "- 三点都是世界坐标(米);normal 省略时按 start/end/center 的平面法向算\n" +
                "- 半径【不用给】:必须等于 |start-center|(= |end-center|),op 内部按几何算;" +
                "两端半径不等必 E_FAIL,静态校验会提前拦下\n" +
                "- 不给 normal 且三点共线时报错(定不出圆弧平面)",

            ["bluesurf"] =
                "bluesurf 蓝面补面(Constructions.BlueSurfs.Add,截面=实体边)。\n" +
                "{op:\"bluesurf\", name:\"bs1\", edgeOf:\"@box\", edges:[1,2]}\n" +
                "- edges 是 Model.Body.Edges 的 1-based 序号,恰好 2 条且必须共享一个端点" +
                "(同顶点引出的两条边);序号会随特征增删漂移,推荐与 edgeOf 特征同批使用\n" +
                "- 成功 = 补出 1 张面,产物入 surfaceFaces 缓存供 thicken 的 faceOf\n" +
                "- 边序决定面法向:thicken 报方向错时换 edges:[j,i] 重建\n" +
                "- 不接受 2D 轮廓截面(恒产僵尸,2026-11-08 裁决);僵尸自动清理并换序重试一次\n" +
                "- 返回 resolved.anchorMm(共享端点坐标)与 anchorMode,便于核对选边是否正确"
        };

        /// <summary>op → 完整条目。调用方应先 Has(op) 过滤,否则可能拿到手册里没有的名字。</summary>
        public static IReadOnlyDictionary<string, string> Entries
        {
            get { return _entries; }
        }

        /// <summary>手册收录的 op 名(按白名单顺序)。一致性单测拿它和 E101 白名单比对。</summary>
        public static IEnumerable<string> Names
        {
            get { return Order; }
        }

        public static bool Has(string op)
        {
            return !string.IsNullOrEmpty(op) && _entries.ContainsKey(op);
        }

        /// <summary>op 索引:每行一个 op(取条目第一行),约 1 千字符。</summary>
        public static string Index()
        {
            var sb = new StringBuilder();
            sb.Append("se_model_build op 索引。取单个完整手册:{\"op\":\"?\",\"name\":\"<op>\"};")
              .Append("取通用规则(单位/形状/plane 选择器/参数化/dir/体积对账/状态语义):{\"op\":\"?\",\"topic\":\"common\"}\n");
            foreach (var op in Order)
            {
                string text;
                if (!_entries.TryGetValue(op, out text)) continue;
                int nl = text.IndexOf('\n');
                sb.Append(nl >= 0 ? text.Substring(0, nl) : text).Append('\n');
            }
            // 不可用接口在这里点一句(只出现在索引,不占单条手册):免得模型反复试。
            sb.Append("注:BlueSurfs(蓝面)的 2D 轮廓截面恒产僵尸空壳(2026-11-08 全矩阵复核);实体边截面已打通为 bluesurf op,请勿绕过 op 直接试轮廓截面。\n");
            return sb.ToString();
        }

        /// <summary>查手册。name 为空/"?"/"*" 返回索引;common 返回通用规则;查不到返回提示 + 索引。</summary>
        public static string Lookup(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "?" || name == "*")
                return Index();

            if (string.Equals(name, "common", StringComparison.OrdinalIgnoreCase))
                return Common;

            string text;
            if (_entries.TryGetValue(name.Trim(), out text))
                return text;

            return "手册里没有 op \"" + name + "\"。可用 op 见下:\n" + Index();
        }

        /// <summary>
        /// 识别 {"op":"?"} / {"op":"help"} 查询。命中时 text 为手册内容,调用方直接返回、不碰 COM。
        /// 只认 features 第一项,其余项忽略(查询本来就是单条语义)。
        /// </summary>
        public static bool TryQuery(JsonElement[] features, out string text)
        {
            text = null;
            if (features == null || features.Length == 0) return false;

            var first = features[0];
            if (first.ValueKind != JsonValueKind.Object) return false;

            string op = ReadString(first, "op");
            if (op == null) return false;
            if (op != "?" && !string.Equals(op, "help", StringComparison.OrdinalIgnoreCase)) return false;

            string topic = ReadString(first, "topic");
            if (string.Equals(topic, "common", StringComparison.OrdinalIgnoreCase))
            {
                text = Common;
                return true;
            }

            text = Lookup(ReadString(first, "name"));
            return true;
        }

        private static string ReadString(JsonElement obj, string prop)
        {
            JsonElement e;
            if (!obj.TryGetProperty(prop, out e)) return null;
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
    }
}
