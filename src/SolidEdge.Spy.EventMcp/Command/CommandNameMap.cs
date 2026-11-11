using System;
using System.Collections.Generic;

namespace SolidEdge.Spy.EventMcp.Command
{
    /// <summary>
    /// 命令名翻译:SE 常量枚举成员(如 PartFeatureProtrusion) → 中文界面叫法(拉伸)。
    ///
    /// SE 没有公开"命令 ID → UI 中文名"的接口,枚举成员是开发者命名;
    /// 本表按"剥掉环境前缀后的后缀"查表(同一后缀跨环境复用,如 FeatureProtrusion
    /// 在零件/钣金环境同名同义),一张表覆盖全部环境。
    ///
    /// 查不到的驼峰拆词兜底(FeatureVent → "Feature Vent"),比原始连写可读。
    /// 台账里存原始枚举名,显示时才翻译 —— 换表不用改历史数据。
    ///
    /// 扩展方式:直接往 Map 里加一行;术语以 Solid Edge 中文界面为准。
    /// </summary>
    internal static class CommandNameMap
    {
        /// <summary>环境前缀(成员名开头),按长度降序剥离,避免短前缀误吃长名。</summary>
        private static readonly string[] EnvPrefixes = new[]
        {
            "DrawingViewEdit", "CuttingPlaneLine", "LayoutInPart", "SheetMetal", "ProfileHole",
            "ProfilePattern", "ProfileRevolved", "Assembly", "Profile", "Detail", "Explode",
            "Simplify", "Weldment", "Tubing", "Motion", "Studio", "Layout", "Part", "SolidEdge",
        };

        /// <summary>后缀 → 中文。按"特征 / 草图 / 装配 / 工程图 / 通用"分组。</summary>
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ---- 实体特征(零件/钣金共用) ----
            { "FeatureProtrusion",         "拉伸" },
            { "FeatureCutout",             "拉伸除料" },
            { "FeatureRevolvedProtrusion", "旋转拉伸" },
            { "FeatureRevolvedCutout",     "旋转除料" },
            { "FeatureSweptProtrusion",    "扫掠拉伸" },
            { "FeatureSweptCutout",        "扫掠除料" },
            { "FeatureLoftedProtrusion",   "放样拉伸" },
            { "FeatureLoftedCutout",       "放样除料" },
            { "FeatureHelicalProtrusion",  "螺旋拉伸" },
            { "FeatureHelicalCutout",      "螺旋除料" },
            { "FeatureNormalProtrusion",   "法向凸缘" },
            { "FeatureNormalCutout",       "法向除料" },
            { "FeatureHole",               "打孔" },
            { "FeatureThread",             "螺纹" },
            { "FeatureRound",              "倒圆" },
            { "FeatureChamfer",            "倒角" },
            { "FeatureRib",                "肋板" },
            { "FeatureWebNetwork",         "网格筋" },
            { "FeatureThinWall",           "薄壁" },
            { "FeatureThinRegion",         "局部薄壁" },
            { "FeatureAddDraft",           "拔模" },
            { "FeatureThicken",            "加厚" },
            { "FeatureDividePart",         "分割零件" },
            { "FeatureReplaceFace",        "替换面" },

            // ---- 阵列/镜像 ----
            { "PatternPattern",            "阵列" },
            { "EditOpenObject",            "打开对象" },
            { "EnvironmentsExit",          "退出当前环境" },
            { "PatternMirrorCopyFeature",  "镜像特征" },
            { "PatternMirrorCopyPart",     "镜像零件" },
            { "PatternRectangularPattern", "矩形阵列" },
            { "PatternCircularPattern",    "环形阵列" },

            // ---- 曲面/构造几何 ----
            { "ConstructionExtrudedSurface",   "拉伸曲面" },
            { "ConstructionRevolvedSurface",   "旋转曲面" },
            { "ConstructionSweptSurface",      "扫掠曲面" },
            { "ConstructionLoftedSurface",     "放样曲面" },
            { "ConstructionOffsetSurface",     "偏置曲面" },
            { "ConstructionTrimSurface",       "修剪曲面" },
            { "ConstructionExtendSurface",     "延伸曲面" },
            { "ConstructionStitchSurface",     "缝合曲面" },
            { "ConstructionBoundedSurface",    "边界曲面" },
            { "ConstructionCopySurface",       "复制曲面" },
            { "ConstructionProjectCurve",      "投影曲线" },
            { "ConstructionIntersectionCurve", "相交曲线" },
            { "ConstructionIntersectionPoint", "交点" },
            { "ConstructionKeypointCurve",     "关键点曲线" },
            { "ConstructionSplitCurve",        "分割曲线" },
            { "ConstructionDerivedCurve",      "派生曲线" },
            { "ConstructionContourCurve",      "轮廓曲线" },
            { "ConstructionCurvebyTable",      "表格曲线" },
            { "ConstructionMirrorCopyGeometry","镜像几何体" },
            { "ConstructionBooleanFeature",    "布尔特征" },
            { "ConstructionPartCopy",          "零件副本" },
            { "ConstructionInterPartCopy",     "跨零件副本" },
            { "ConstructionMakeBaseFeature",   "设为基础特征" },

            // ---- 草图 ----
            { "SketchSketch",              "草图" },
            { "SketchShowAllSketches",     "显示所有草图" },
            { "SketchHideAllSketches",     "隐藏所有草图" },

            // ---- 草图绘制(Profile 环境 Drawing*) ----
            { "DrawingLine",               "直线" },
            { "DrawingLineArcContinuous",  "直线圆弧连续" },
            { "DrawingTangentArc",         "相切弧" },
            { "DrawingArcby3Points",       "三点弧" },
            { "DrawingArcbyCenter",        "圆心弧" },
            { "DrawingArcbyCenterPoint",   "圆心弧" },
            { "DrawingTangentCircle",      "相切圆" },
            { "DrawingCircleby3Points",    "三点圆" },
            { "DrawingCirclebyCenter",     "圆心圆" },
            { "DrawingCirclebyCenterPoint","圆心圆" },
            { "DrawingEllipseby3Points",   "三点椭圆" },
            { "DrawingEllipsebyCenter",    "中心椭圆" },
            { "DrawingEllipsebyCenterPoint","中心椭圆" },
            { "DrawingRectangle",          "矩形" },
            { "DrawingCurve",              "曲线" },
            { "DrawingPoint",              "点" },
            { "DrawingFreeForm",           "自由曲线" },
            { "DrawingFreeSketch",         "自由草图" },
            { "DrawingHoleCircle",         "孔圆" },
            { "DrawingSymmetricOffset",    "对称偏置" },
            { "DrawingInclude",            "包含" },
            { "DrawingOffset",             "偏置" },
            { "DrawingAxisofRevolution",   "旋转轴" },

            // ---- 草图/工程图绘制(Detail 环境 Draw*) ----
            { "DrawLine",                  "直线" },
            { "DrawTangentArc",            "相切弧" },
            { "DrawArcby3Points",          "三点弧" },
            { "DrawArcbyCenterPoint",      "圆心弧" },
            { "DrawTangentCircle",         "相切圆" },
            { "DrawCircleby3Points",       "三点圆" },
            { "DrawCirclebyCenterPoint",   "圆心圆" },
            { "DrawEllipseby3Points",      "三点椭圆" },
            { "DrawEllipsebyCenterPoint",  "中心椭圆" },
            { "DrawRectangle",             "矩形" },
            { "DrawCurve",                 "曲线" },
            { "DrawPoint",                 "点" },
            { "DrawOffset",                "偏置" },
            { "DrawSymmetricOffset",       "对称偏置" },
            { "DrawShowEdges",             "显示边" },
            { "DrawHideEdges",             "隐藏边" },
            { "DrawEdgePainter",           "边画家" },

            // ---- 草图编辑(Profile 环境 Trim*) ----
            { "TrimFillet",                "圆角" },
            { "TrimChamfer",               "倒角" },
            { "TrimRotate",                "旋转" },
            { "TrimMove",                  "移动" },
            { "TrimMirror",                "镜像" },
            { "TrimScale",                 "比例缩放" },
            { "TrimTrim",                  "修剪" },
            { "TrimTrimCorner",            "修剪拐角" },
            { "TrimExtendtoNext",          "延伸" },

            // ---- 几何变换/编辑(Detail/草图 Relationships*) ----
            { "RelationshipsFillet",       "圆角" },
            { "RelationshipsChamfer",      "倒角" },
            { "RelationshipsTrim",         "修剪" },
            { "RelationshipsTrimCorner",   "修剪拐角" },
            { "RelationshipsExtendtoNext", "延伸" },
            { "RelationshipsStretch",      "拖拽编辑" },
            { "RelationshipsRotate",       "旋转" },
            { "RelationshipsMove",         "移动" },
            { "RelationshipsMirror",       "镜像" },
            { "RelationshipsScale",        "比例缩放" },
            { "RelationshipsDelete",       "删除" },
            { "RelationshipsLock",         "锁定" },
            { "RelationshipsBringtoFront", "前置" },
            { "RelationshipsSendtoBack",   "后置" },
            { "RelationshipsPullUp",       "上移一层" },
            { "RelationshipsPushDown",     "下移一层" },
            { "RelationshipsAlignViews",   "对齐视图" },
            { "RelationshipsUnalignViews", "取消对齐视图" },

            // ---- 草图约束(Profile 环境 Relationships*) ----
            { "RelationshipsParallelRelationship",    "平行约束" },
            { "RelationshipsConnect",                 "连接约束" },
            { "RelationshipsHorizontalVertical",      "水平/竖直约束" },
            { "RelationshipsPerpendicular",           "垂直约束" },
            { "RelationshipsTangent",                 "相切约束" },
            { "RelationshipsEqual",                   "相等约束" },
            { "RelationshipsSymmetricRelationship",   "对称约束" },
            { "RelationshipsCollinear",               "共线约束" },
            { "RelationshipsConcentric",              "同心约束" },
            { "RelationshipsSetSymmetryAxis",         "设置对称轴" },

            // ---- 尺寸标注 ----
            { "DimensionSmartDimension",       "智能尺寸" },
            { "DimensionDistanceBetween",      "间距尺寸" },
            { "DimensionAngleBetween",         "角度尺寸" },
            { "DimensionChamfer",              "倒角尺寸" },
            { "DimensionSymmetricDiameter",    "对称直径" },
            { "DimensionCoordinateDimension",  "坐标尺寸" },
            { "DimensionAngularCoordinate",    "角坐标尺寸" },
            { "DimensionAlignDimension",       "对齐尺寸" },
            { "DimensionAttachDimension",      "附加尺寸" },
            { "DimensionRetrieveDimensions",   "检索尺寸" },
            { "DimensionCenterLine",           "中心线" },
            { "DimensionCenterMark",           "中心标记" },
            { "DimensionFeatureControlFrame",  "形位公差" },
            { "DimensionDatumFrame",           "基准框" },
            { "DimensionDatumTarget",          "基准目标" },
            { "DimensionHoleTable",            "孔表" },
            { "DimensionPrefixCopier",         "尺寸前缀复制" },
            { "DimensionDimensionAxis",        "尺寸轴" },
            { "DimensionFill",                 "填充" },
            { "DimensionRelationshipAssistant","关系助手" },
            { "DimensionUpdatePropertyText",   "更新属性文本" },

            // ---- 工程图文本/符号 ----
            { "TextText",                  "文本" },
            { "TextCallout",               "引出标注" },
            { "TextLeader",                "引线" },
            { "TextBalloon",               "零件序号" },
            { "TextSurfaceTextureSymbol",  "表面粗糙度符号" },
            { "TextWeldSymbol",            "焊接符号" },
            { "TextCharacterMap",          "字符映射表" },
            { "TextEdgeCondition",         "边条件符号" },

            // ---- 工程图视图 ----
            { "DrawingViewsViewofPart",    "零件视图(视图向导)" },
            { "DrawingViewsPartsList",     "零件明细表" },
            { "DrawingViewsDetailView",    "局部放大图" },
            { "DrawingViewsPrincipalView", "主视图" },
            { "DrawingViewsSectionView",   "剖视图" },
            { "DrawingViewsCuttingPlane",  "剖切平面" },
            { "DrawingViewsAuxiliaryView", "辅助视图" },
            { "DrawingViewsSnapshotView",  "快照视图" },
            { "DrawingViewsUpdateViews",   "更新视图" },
            { "DrawingViewsEdgeDisplay",   "边显示" },
            { "DrawingViewsTracker",       "视图跟踪器" },
            { "DrawingViewsDraftView",     "图纸视图" },
            { "FileSheetSetup",            "图纸设置" },
            { "ViewBackgroundSheet",       "背景图页" },
            { "ViewWorkingsheet",          "工作图页" },
            { "ViewInsertSheet",           "插入图页" },
            { "ViewDeleteSheet",           "删除图页" },
            { "EditGoToSheet",             "转到图页" },
            { "EditSelectAllInView",       "在视图中全选" },

            // ---- 分组 ----
            { "GroupGroup",                "分组" },
            { "GroupUngroup",              "取消分组" },
            { "GroupRectangularPattern",   "矩形阵列" },
            { "GroupCircularPattern",      "环形阵列" },

            // ---- 装配关系 ----
            { "AssemblyRelationshipsMate",        "匹配" },
            { "AssemblyRelationshipsPlanarAlign", "面对齐" },
            { "AssemblyRelationshipsAxialAlign",  "轴对齐" },
            { "AssemblyRelationshipsAngle",       "角度" },
            { "AssemblyRelationshipsInsert",      "插入" },
            { "AssemblyRelationshipsConnect",     "连接" },
            { "AssemblyRelationshipsGround",      "固定" },
            { "AssemblyRelationshipsMovePart",    "移动零件" },
            { "AssemblyRelationshipsReplacePart", "替换零件" },
            { "AssemblyToolsPatternParts",        "阵列零件" },
            { "AssemblyToolsCheckInterference",   "干涉检查" },
            { "AssemblyToolsMultiPartCutout",     "多零件除料" },
            { "AssemblyToolsCutawayView",         "剖切视图" },
            { "AssemblyToolsDisplayConfigurations","显示配置" },
            { "AssemblyToolsActivate",            "激活" },
            { "AssemblyToolsInactivate",          "停用" },
            { "AssemblyToolsShow",                "显示" },
            { "AssemblyToolsHide",                "隐藏" },
            { "AssemblyToolsShowAll",             "全部显示" },
            { "AssemblyToolsHideAll",             "全部隐藏" },
            { "AssemblyToolsShowOnly",            "仅显示" },
            { "AssemblyToolsAllPartsSameColor",   "所有零件同色" },
            { "AssemblyToolsUnloadHiddenParts",   "卸载隐藏零件" },
            { "AssemblyToolsSketch",              "草图" },
            { "AssemblyEnvironmentsExplodedView", "爆炸图" },
            { "AssemblyEnvironmentsVirtualStudio","Virtual Studio 渲染" },
            { "AssemblyEnvironmentsMotion",       "运动仿真" },
            { "AssemblyEnvironmentsXpresRoute",   "管道设计" },
            { "ExplodedViewMovePart",             "爆炸移动零件" },

            // ---- 参考面/坐标系 ----
            { "ReferencePlaneCoincidentPlane",        "重合平面" },
            { "ReferencePlaneCoincidentPlanebyAxis",  "按轴重合平面" },
            { "ReferencePlaneParallelPlane",          "平行平面" },
            { "ReferencePlaneAngledPlane",            "成角度平面" },
            { "ReferencePlanePerpendicularPlane",     "垂直平面" },
            { "ReferencePlanePlaneby3Points",         "三点平面" },
            { "ReferencePlanePlaneNormaltoCurve",     "垂直于曲线的平面" },
            { "ReferencePlaneCoordinateSystem",       "坐标系" },
            { "ReferencePlaneShowAllReferencePlanes", "显示所有参考面" },
            { "ReferencePlaneHideAllReferencePlanes", "隐藏所有参考面" },
            { "ReferencePlaneShowAllReferenceAxes",   "显示所有参考轴" },
            { "ReferencePlaneHideAllReferenceAxes",   "隐藏所有参考轴" },

            // ---- 工具/测量 ----
            { "ToolsVariables",             "变量表" },
            { "ToolsPhysicalProperties",    "物理属性" },
            { "ToolsPropertyTables",        "属性表" },
            { "ToolsAttachProperties",      "附加属性" },
            { "ToolsMeasureDistance",       "测量距离" },
            { "ToolsMeasureAngle",          "测量角度" },
            { "ToolsMeasureMinimumDistance","测量最小距离" },
            { "ToolsMeasureNormalDistance", "测量法向距离" },
            { "ToolsMeasureArea",           "测量面积" },
            { "ToolsMeasureTotalLength",    "测量总长度" },
            { "ToolsInquireElement",        "查询图素" },
            { "ToolsSelectTool",            "选择工具" },
            { "ToolsInterPartManager",      "跨零件管理器" },
            { "ToolsAutomaticUpdate",       "自动更新" },
            { "ToolsUpdateRelationships",   "更新关系" },
            { "ToolsUpdateAllLinks",        "更新所有链接" },
            { "ToolsReports",               "报告" },
            { "ToolsToDoList",              "待办事项" },

            // ---- 简化 ----
            { "SimplifyFeaturesDeleteFaces","删除面(简化)" },
            { "ModelSimplify",              "简化" },

            // ---- 文件/编辑/视图(各环境通用) ----
            { "FileNew",          "新建" },
            { "FileOpen",         "打开" },
            { "FileClose",        "关闭" },
            { "FileSave",         "保存" },
            { "FileSaveAs",       "另存为" },
            { "FileSaveAll",      "全部保存" },
            { "FileSaveCopy",     "保存副本" },
            { "FileSaveAsImage",  "另存为图像" },
            { "FilePrint",        "打印" },
            { "FileSendMail",     "邮件发送" },
            { "FileRevert",       "还原" },
            { "FileExit",         "退出" },
            { "FileProperties",   "文件属性" },
            { "FileFileProperties","文件属性" },
            { "EditDelete",       "删除" },
            { "EditCopy",         "复制" },
            { "EditCut",          "剪切" },
            { "EditPaste",        "粘贴" },
            { "EditPasteSpecial", "选择性粘贴" },
            { "EditUndo",         "撤销" },
            { "EditRedo",         "重做" },
            { "EditBinder",       "绑定" },
            { "EditEditLinks",    "编辑链接" },
            { "ViewFit",          "满屏显示" },
            { "ViewZoom",         "缩放" },
            { "ViewZoomIn",       "放大" },
            { "ViewZoomOut",      "缩小" },
            { "ViewZoomArea",     "区域缩放" },
            { "ViewZoomTool",     "缩放工具" },
            { "ViewPan",          "平移" },
            { "ViewRotate",       "旋转视图" },
            { "ViewSpinAbout",    "绕轴旋转" },
            { "ViewShade",        "着色" },
            { "ViewPerspective",  "透视" },
            { "ViewISOView",      "等轴测视图" },
            { "ViewFrontView",    "前视图" },
            { "ViewTopView",      "俯视图" },
            { "ViewRightView",    "右视图" },
            { "ViewLeftView",     "左视图" },
            { "ViewBottomView",   "仰视图" },
            { "ViewNamedViews",   "命名视图" },
            { "ViewCommonViews",  "常用视图" },
            { "ViewLookatFace",   "正视于面" },
            { "ViewAlignEdge",    "对齐边" },
            { "ViewPreviousView", "上一视图" },
            { "ViewNextView",     "下一视图" },
            { "ViewRefreshWindow","刷新窗口" },
            { "WindowArrange",    "排列窗口" },
            { "WindowNewWindow",  "新建窗口" },
            { "WindowArrangeAllWindows", "层叠全部窗口" },
        };

        /// <summary>
        /// 翻译命令名。"#"开头的未反解 ID 原样返回;查不到的后缀驼峰拆词兜底。
        /// </summary>
        public static string Translate(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.StartsWith("#")) return name;   // 未反解的原始 ID

            string hit;
            if (Map.TryGetValue(name, out hit)) return hit;

            string suffix = StripEnvPrefix(name);
            if (Map.TryGetValue(suffix, out hit)) return hit;

            return CamelSplit(suffix);
        }

        /// <summary>剥离环境前缀(PartFeatureProtrusion → FeatureProtrusion)。</summary>
        private static string StripEnvPrefix(string name)
        {
            foreach (string prefix in EnvPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return name.Substring(prefix.Length);
                }
            }
            return name;
        }

        /// <summary>
        /// 驼峰拆词:FeatureVent → "Feature Vent"(兜底可读性)。
        /// 数字后的大写不拆,除非后面跟小写——"3D"保持连写,"Arcby3Points"拆成 "Arcby3 Points"。
        /// </summary>
        private static string CamelSplit(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder(name.Length + 8);
            sb.Append(name[0]);
            for (int i = 1; i < name.Length; i++)
            {
                char prev = name[i - 1];
                char cur = name[i];
                bool boundary =
                    (char.IsUpper(cur) && char.IsLower(prev)) ||
                    (char.IsUpper(cur) && char.IsDigit(prev) &&
                     i + 1 < name.Length && char.IsLower(name[i + 1]));
                if (boundary) sb.Append(' ');
                sb.Append(cur);
            }
            return sb.ToString();
        }
    }
}
