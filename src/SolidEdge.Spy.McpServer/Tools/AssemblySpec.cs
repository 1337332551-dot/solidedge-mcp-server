using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 装配批量操作(AssemblyOp)的 schema 定义 + 静态校验器 + 诚实拒绝清单。
    ///
    /// 移植自 SolidEdge-MCP(Python)装配侧的实测结论,签名已按 SE2022 离线 SDK 文档逐一核实:
    ///   Occurrences.AddByFilename / AddWithMatrix(String, ByRef Double())/AddWithTransform /
    ///   AddFamilyByFilename / ReorderOccurrence(OccurrenceToReorder, TargetOccurrence, AfterTarget)
    ///   Occurrence.GetMatrix(ByRef Double())/PutMatrix(ByRef Double(), Replace)/PutTransform/PutOrigin/
    ///              Move(DeltaX,DeltaY,DeltaZ)/Rotate(轴两点, Angle 弧度)/Visible/Delete/Replace/FaceStyle
    ///   AssemblyDocument.CreateReference(Occurrence, Entity) —— 约束必须用它包裹零件面
    ///   Relations3d.AddPlanar(Plane1,Plane2,NormalsAligned,CP1(),CP2()) —— True=mated 贴合 / False=aligned
    ///   Relations3d.AddAxial(Axis1,Axis2,NormalsAligned) —— 须圆柱面
    ///   Relations3d.AddGround(Occurrence);GroundRelation3d.Occurrence + Delete
    ///
    /// 实测坑(来源项目 SE2025/2026 + 本仓库合页 v2→v6 四版实测):
    ///   - AddFamilyByFilename 恒落原点,要后补 PutTransform
    ///   - AddAsAdjustablePart 对普通零件报 E_INVALIDARG(须 UI 先做 adjustable)
    ///   - AddByFilename 装入的零件默认带 GroundRelation3d,加约束前先 unground,否则求解器固死
    ///   - NormalsAligned=true(贴合) 可能翻转零件 180°,与轴对齐组合时用 mate=false(共面同向)
    /// </summary>
    internal static class AssemblySpec
    {
        /// <summary>全部可执行的 op 名(小写)。</summary>
        internal static readonly string[] SupportedOps = new[]
        {
            "place", "move", "rotate", "transform", "origin", "visible",
            "ground", "delete", "replace", "reorder", "color", "constrain", "rebind"
        };

        /// <summary>
        /// 诚实拒绝的 op:实测 COM 自动化不可达或本工具未实现的 op,
        /// 校验期即拦截并给出替代方案,不让 AI 对着 COM 瞎试。
        /// mirror/suppress/unsuppress:COM 通道实测不可达(来源项目 SE2025/2026;suppress 三条晚绑定
        /// 封送通道 2026-11-01 在 SE222.00.00.132 本机复现全灭)。
        /// pattern:COM 通道实测可达(AssemblyPatterns.CreateDuplicate,2026-11-01 SE222 真机成功,
        /// 新副本落位=master+(to−from) 语义正确)——只是本工具未实现该 op,拒绝文案给替代方案,
        /// 不得再表述为"不可达"。来源项目 E_ACCESSDENIED 的是 AssemblyFeaturesPatterns(装配【加工
        /// 特征】阵列,挂在 AssemblyFeatures 下),与 occurrence 阵列不是一条通道,勿再混为一谈。
        /// </summary>
        private static readonly Dictionary<string, string> RefusedOps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pattern"] = "本工具暂未实现装配 pattern op,但 COM 通道本身可达(2026-11-01 SE222 真机实测):"
                + "装配级 occurrence 阵列走 AssemblyDocument.AssemblyPatterns(Create/CreateAlongCurve/CreateDuplicate);"
                + "来源项目实测 E_ACCESSDENIED 的是 AssemblyFeaturesPatterns(装配【加工特征】阵列,挂在 AssemblyFeatures 下),两者不是一条通道。"
                + "替代:①用 place op 按矩阵放多份副本(阵列=多个 place);"
                + "②需真阵列时用 se_script_run 走编译期强类型调用 AssemblyPatterns.CreateDuplicate"
                + "(PatternName, NumberOfMasters, ref Array MasterOccurrences, FromOccurrence, NumberOfToOccurrences, ref Array ToOccurrences)"
                + "——晚绑定 InvokeMember 解析不到该属性,必须强类型 interop;③简单阵列做在零件文档里(零件级 pattern op 已开放)。",
            ["mirror"] = "装配级镜像不可用:来源项目实测 AssemblyFeaturesMirrors.Add 返回 E_ACCESSDENIED(0x80070005)。"
                + "替代:用 place op 放副本并给 transform op 设镜像矩阵(旋转 180°+平移),或在零件文档里镜像特征。",
            ["suppress"] = "压缩零件在本环境不可达:官方签名是 SetSuppressComponent(OccurrenceToSuppress, " +
                "ByRef SuppressComponent),第二个是接口出参——SE2022(222.00.10.03)实测经裸 IDispatch、" +
                "InvokeMember+ParameterModifier、dynamic 三条通道均封送失败(PARAMNOTFOUND/TYPEMISMATCH);" +
                "来源项目在 SE2026 可用(pywin32 自动补 out)。请在 SE UI 里压缩,或等升级 SE 后重试。",
            ["unsuppress"] = "解除压缩不可达:AssemblyDocument.SetSuppressComponent 返回的 SuppressComponent 对象"
                + "拥有 UnSuppress,但 SE 不提供把已压缩零件的这个对象再查出来的通道(来源项目实测)。请在 SE UI 里解除压缩。",
            ["angular"] = "角度约束(constrain type=angular)不可用:Relations3d.AddAngular 需要 4 个面/边参数"
                + "(两个受约束元素 + 两个测量基准),COM 自动化无法选几何(来源项目实测)。请在 SE UI 里加,或改用 transform op 直接摆角度。",
            ["tangent"] = "相切约束(constrain type=tangent)不可用:Relations3d.AddTangent 需要两个面 + 各一个约束点,"
                + "约束点必须落在面上,COM 自动化选不了面(来源项目实测)。请在 SE UI 里加。",
            ["point"] = "点约束(constrain type=point)不可用:Relations3d.AddPoint 需要几何元素 + 关键点组合"
                + "(Relation3dGeometryConstants),COM 自动化选不了(来源项目实测)。请在 SE UI 里加。",
            ["gear"] = "齿轮约束(constrain type=gear)不可用:Relations3d.AddGear 的 Element1/2 是被耦合的旋转面/轴,不是零件,"
                + "COM 自动化选不了(来源项目实测)。请在 SE UI 里加。",
        };

        // ---------------- 校验报告结构(仿 FeatureValidator) ----------------

        internal class AssemblyIssue
        {
            public string code;          // E### / W###
            public string level;         // error / warning
            public int op;               // 0-based op 序号
            public string opName;
            public string field;
            public string message;       // 中文
            public object fix;           // 结构化修正建议,可为 null
        }

        internal class AssemblyReport
        {
            public string status = "ok";
            public string version = "1.0";
            public int opCount;
            public int errorCount;
            public int warningCount;
            public List<AssemblyIssue> issues = new List<AssemblyIssue>();

            public bool HasError { get { return errorCount > 0; } }

            /// <summary>报告全是公有字段,System.Text.Json 默认只认属性——必须 IncludeFields 才能完整落 JSON。</summary>
            internal static readonly JsonSerializerOptions JsonOpts =
                new JsonSerializerOptions(FeatureValidator.JsonOpts) { IncludeFields = true };

            public string ToJson()
            {
                return JsonSerializer.Serialize(this, JsonOpts);
            }
        }

        // ---------------- 解析后的 op ----------------

        internal class AssemblyOp
        {
            public int Index;
            public string Op;
            public object CompRef;        // int(0-based)/string(名字或 obj-N)
            public object Comp2Ref;
            public object TargetRef;
            /// <summary>
            /// 约束面引用:int(0-based 面序号) / string(面句柄 "obj-N" 或几何描述符 "cylinder:dia=89") /
            /// FaceSelector(JSON 描述符对象)。null=没给(校验期会报错)。
            /// </summary>
            public object Face1Ref;
            public object Face2Ref;
            public string File;
            public string FamilyMember;
            public double[] Matrix;       // 16
            public double[] Position;     // 3
            public double[] Delta;        // 3
            public double[] Angles;       // 3(度)
            public double[] Axis1;        // 3
            public double[] Axis2;        // 3
            public double Degrees;
            public int[] Rgb;             // 3
            public bool BoolValue = true;
            public bool HasBool;
            public bool After = true;
            public bool Mate = true;      // constrain planar: true=贴合(mated) false=对齐(aligned)
            public bool KeepGround;       // constrain: true=保留 component1 的 Ground(默认自动解固)
            public bool PlaceGround = true; // place: false=放置后立即解固(删默认 GroundRelation3d)
            public bool All;              // replace ReplaceAll
            public string ConstrainType;
            /// <summary>rebind: 要重绑的关系 0-based 序号;-1 = 自动处理全部 detailed_status=6 的关系。</summary>
            public int RelationIndex = -1;
            public bool Destructive;      // delete / replace / unground
        }

        // ---------------- 校验入口 ----------------

        internal static AssemblyReport Validate(JsonElement[] ops)
        {
            var report = new AssemblyReport { opCount = ops == null ? 0 : ops.Length };
            if (ops == null || ops.Length == 0)
            {
                AddError(report, 0, null, "ops", "E101", "ops 为空:至少要给一个装配操作。");
                return report;
            }
            for (int i = 0; i < ops.Length; i++)
            {
                ParseAndValidate(ops[i], i, report);
            }
            return report;
        }

        /// <summary>解析单个 op;问题全部记进 report(有 error 时执行侧不会真正跑它)。</summary>
        internal static AssemblyOp ParseAndValidate(JsonElement e, int index, AssemblyReport report)
        {
            var op = new AssemblyOp { Index = index };
            if (e.ValueKind != JsonValueKind.Object)
            {
                AddError(report, index, null, "op", "E102", "每个操作必须是 JSON 对象 {\"op\":...}。");
                return op;
            }

            string opName = GetString(e, "op");
            if (string.IsNullOrEmpty(opName))
            {
                AddError(report, index, null, "op", "E103", "缺少 \"op\" 字段(可用: " + string.Join("/", SupportedOps) + ")。");
                return op;
            }
            op.Op = opName.ToLowerInvariant();

            // 诚实拒绝清单:校验期即拦截,附原因与替代方案。
            if (RefusedOps.TryGetValue(op.Op, out string refused))
            {
                AddError(report, index, op.Op, "op", "E190", refused);
                return op;
            }
            if (Array.IndexOf(SupportedOps, op.Op) < 0)
            {
                AddError(report, index, op.Op, "op", "E104",
                    "未知 op \"" + opName + "\"。可用: " + string.Join("/", SupportedOps)
                    + ";另有一批已实测不可用的 op(angular/tangent/point/gear/pattern/mirror/unsuppress)会被直接拒绝并给替代方案。");
                return op;
            }

            switch (op.Op)
            {
                case "place": ValidatePlace(e, index, op, report); break;
                case "move": ValidateMove(e, index, op, report); break;
                case "rotate": ValidateRotate(e, index, op, report); break;
                case "transform": ValidateTransform(e, index, op, report); break;
                case "origin": ValidateOrigin(e, index, op, report); break;
                case "visible": ValidateVisible(e, index, op, report); break;
                case "ground": ValidateGround(e, index, op, report); break;
                case "suppress": ValidateComponentOnly(e, index, op, report, "suppress"); break;
                case "delete": ValidateDelete(e, index, op, report); break;
                case "replace": ValidateReplace(e, index, op, report); break;
                case "reorder": ValidateReorder(e, index, op, report); break;
                case "color": ValidateColor(e, index, op, report); break;
                case "constrain": ValidateConstrain(e, index, op, report); break;
                case "rebind": ValidateRebind(e, index, op, report); break;
            }
            return op;
        }

        // ---------------- 各 op 的字段校验 ----------------

        private static void ValidatePlace(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.File = GetString(e, "file");
            if (string.IsNullOrEmpty(op.File))
            {
                AddError(r, i, "place", "file", "E110", "place 缺少 \"file\"(.par/.asm/.psm 全路径)。");
                return;
            }
            if (!File.Exists(op.File))
                AddError(r, i, "place", "file", "W111", "文件不存在: " + op.File + "(执行时 SE 仍会尝试,通常报错)。");
            op.FamilyMember = GetString(e, "familyMember");
            // ground:false = 放置后立即解固(AddByFilename 默认带 GroundRelation3d,摆位后不打算约束也得防"以后约束失效")
            if (TryBool(e, "ground", out bool pg) && !pg) op.PlaceGround = false;
            op.Matrix = GetDoubleArray(e, "matrix");
            if (op.Matrix != null && op.Matrix.Length != 16)
                AddError(r, i, "place", "matrix", "E112", "matrix 必须是 16 个数(行主序 4x4,平移在下排 [12..14],[15]=1),实际 " + op.Matrix.Length + " 个。");
            op.Position = GetDoubleArray(e, "origin");
            if (op.Position != null && op.Position.Length != 3)
                AddError(r, i, "place", "origin", "E113", "origin 必须是 3 个数 [x,y,z](米)。");
            op.Angles = GetDoubleArray(e, "angles");
            if (op.Angles != null && op.Angles.Length != 3)
                AddError(r, i, "place", "angles", "E114", "angles 必须是 3 个数 [rx,ry,rz](度)。");
        }

        private static void ValidateMove(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "move"); return; }
            op.Delta = GetDoubleArray(e, "delta");
            if (op.Delta == null)
            {
                bool ok = TryNumbers(e, new[] { "dx", "dy", "dz" }, out double[] d);
                op.Delta = d;
                if (!ok)
                {
                    AddError(r, i, "move", "delta", "E120", "move 需要 delta:[dx,dy,dz](米)或 dx/dy/dz 三个数。");
                    return;
                }
            }
            if (op.Delta.Length != 3)
                AddError(r, i, "move", "delta", "E121", "delta 必须是 3 个数 [dx,dy,dz](米)。");
        }

        private static void ValidateRotate(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "rotate"); return; }
            op.Axis1 = GetDoubleArray(e, "axis1");
            op.Axis2 = GetDoubleArray(e, "axis2");
            if (op.Axis1 == null || op.Axis1.Length != 3 || op.Axis2 == null || op.Axis2.Length != 3)
            {
                AddError(r, i, "rotate", "axis1/axis2", "E130", "rotate 需要 axis1:[x,y,z] 与 axis2:[x,y,z] 两点定义旋转轴(米)。");
                return;
            }
            if (!TryNumber(e, "degrees", out op.Degrees))
            {
                AddError(r, i, "rotate", "degrees", "E131", "rotate 缺少 \"degrees\"(旋转角,度,可负)。");
                return;
            }
        }

        private static void ValidateTransform(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "transform"); return; }
            op.Position = GetDoubleArray(e, "origin");

            // 矩阵模式(斜置姿态):给 16 元素行主序矩阵就够,不需要 origin/angles。
            op.Matrix = GetDoubleArray(e, "matrix");
            if (op.Matrix != null)
            {
                if (op.Matrix.Length != 16)
                {
                    AddError(r, i, "transform", "matrix", "E143",
                        "matrix 必须是 16 个数(行主序 4x4,平移在索引 12/13/14,单位米)。");
                    return;
                }
                if (op.Position != null)
                    AddWarning(r, i, "transform", "origin", "W144",
                        "matrix 与 origin 同时给了:矩阵是绝对姿态,origin 被忽略。");
                op.Angles = null;
                return;
            }

            if (op.Position == null || op.Position.Length != 3)
            {
                AddError(r, i, "transform", "origin", "E140", "transform 需要 origin:[x,y,z](米),或 matrix:[16 个数]。");
                return;
            }
            op.Angles = GetDoubleArray(e, "angles");
            if (op.Angles != null && op.Angles.Length != 3)
            {
                AddError(r, i, "transform", "angles", "E141", "angles 必须是 3 个数 [rx,ry,rz](度)。");
                return;
            }
            if (op.Angles == null)
                AddWarning(r, i, "transform", "angles", "W142",
                    "transform 未给 angles:PutTransform 六参全量生效,旋转会被归零。只挪位置请用 origin op(PutOrigin 保旋转)。");
        }

        private static void ValidateOrigin(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "origin"); return; }
            op.Position = GetDoubleArray(e, "position");
            if (op.Position == null)
            {
                bool ok = TryNumbers(e, new[] { "x", "y", "z" }, out double[] p);
                op.Position = p;
                if (!ok)
                {
                    AddError(r, i, "origin", "position", "E150", "origin 需要 position:[x,y,z](米)或 x/y/z 三个数。");
                    return;
                }
            }
            if (op.Position.Length != 3)
                AddError(r, i, "origin", "position", "E151", "position 必须是 3 个数 [x,y,z](米)。");
        }

        private static void ValidateVisible(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "visible"); return; }
            if (TryBool(e, "value", out bool v)) { op.BoolValue = v; op.HasBool = true; }
            else AddError(r, i, "visible", "value", "E160", "visible 缺少 \"value\"(true 显示 / false 隐藏)。");
        }

        private static void ValidateGround(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "ground"); return; }
            if (TryBool(e, "value", out bool v)) { op.BoolValue = v; op.HasBool = true; }
            if (op.HasBool && !op.BoolValue)
            {
                // unground 要删 GroundRelation3d,属破坏性,执行侧要求 confirm
                op.Destructive = true;
            }
        }

        private static void ValidateComponentOnly(JsonElement e, int i, AssemblyOp op, AssemblyReport r, string name)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) RequireComponent(r, i, name);
        }

        private static void ValidateDelete(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.Destructive = true;
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) RequireComponent(r, i, "delete");
        }

        private static void ValidateReplace(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.Destructive = true;
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "replace"); return; }
            op.File = GetString(e, "file");
            if (string.IsNullOrEmpty(op.File))
            {
                AddError(r, i, "replace", "file", "E170", "replace 缺少 \"file\"(新零件全路径)。");
                return;
            }
            if (!File.Exists(op.File))
                AddError(r, i, "replace", "file", "W171", "新文件不存在: " + op.File + "(执行时 SE 会报错)。");
            if (TryBool(e, "all", out bool a)) { op.All = a; }
        }

        private static void ValidateReorder(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "reorder"); return; }
            op.TargetRef = GetComponentField(e, "target");
            if (op.TargetRef == null)
            {
                AddError(r, i, "reorder", "target", "E180", "reorder 缺少 \"target\"(目标零件: 0-based 序号、名称或 obj-N)。");
                return;
            }
            if (TryBool(e, "after", out bool a)) op.After = a;
        }

        private static void ValidateColor(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.CompRef = GetComponent(e);
            if (op.CompRef == null) { RequireComponent(r, i, "color"); return; }
            op.Rgb = GetIntArray(e, "rgb");
            if (op.Rgb == null || op.Rgb.Length != 3)
            {
                AddError(r, i, "color", "rgb", "E182", "color 需要 rgb:[r,g,b](0~255)。");
                return;
            }
            foreach (int c in op.Rgb)
            {
                if (c < 0 || c > 255)
                {
                    AddError(r, i, "color", "rgb", "E183", "rgb 分量须在 0~255,实际 [" + string.Join(",", op.Rgb) + "]。");
                    return;
                }
            }
        }

        private static void ValidateConstrain(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.ConstrainType = GetString(e, "type");
            if (string.IsNullOrEmpty(op.ConstrainType))
            {
                AddError(r, i, "constrain", "type", "E184", "constrain 缺少 \"type\"(planar 面贴合/对齐 或 axial 轴对齐)。");
                return;
            }
            op.ConstrainType = op.ConstrainType.ToLowerInvariant();
            if (RefusedOps.TryGetValue(op.ConstrainType, out string refused))
            {
                AddError(r, i, "constrain", "type", "E190", refused);
                return;
            }
            if (op.ConstrainType != "planar" && op.ConstrainType != "axial")
            {
                AddError(r, i, "constrain", "type", "E185",
                    "constrain type 只支持 planar / axial(其余类型已实测不可用,见拒绝清单)。");
                return;
            }
            op.CompRef = GetComponentField(e, "component1");
            op.Comp2Ref = GetComponentField(e, "component2");
            if (op.CompRef == null || op.Comp2Ref == null)
            {
                AddError(r, i, "constrain", "component1/component2", "E186",
                    "constrain 需要 component1 与 component2(0-based 序号、名称或 obj-N)。");
                return;
            }
            string ferr;
            op.Face1Ref = ParseFaceRef(e, "face1", out ferr);
            if (ferr != null) { AddError(r, i, "constrain", "face1", "E191", ferr); return; }
            op.Face2Ref = ParseFaceRef(e, "face2", out ferr);
            if (ferr != null) { AddError(r, i, "constrain", "face2", "E191", ferr); return; }
            if (op.Face1Ref == null || op.Face2Ref == null)
            {
                // 以前这里默认 0 —— 等于"闷声绑到第 0 张面",是最坏的一种绑定。缺参数就报错。
                AddError(r, i, "constrain", "face1/face2", "E187",
                    "constrain 必须给 face1 与 face2。三种写法(推荐描述符):\n"
                    + "  ① 描述符对象 {\"type\":\"cylinder\",\"diaMm\":89} / {\"type\":\"plane\",\"normal\":\"-Z\"} —— 按几何认面,"
                    + "零件重建后序号漂移也不影响;\n"
                    + "  ② 描述符字符串 \"cylinder:dia=89\" / \"plane:normal=-Z;at=0,0,0\";\n"
                    + "  ③ 0-based 面序号 1(★加/删特征后序号会整体漂移,重跑配方会绑到别的面上);\n"
                    + "  ④ 面句柄 \"obj-N\"(se_find_objects 返回的 handle)。");
                return;
            }
            if (TryBool(e, "mate", out bool m)) op.Mate = m;
            // keepGround=true = 显式保留 component1 的 Ground(跳过自动解固)。
            // 固定件上加约束,求解器不会挪零件——校验期就把后果说清(运行期还会再看实际 Ground 状态)。
            if (TryBool(e, "keepGround", out bool kg) && kg)
            {
                op.KeepGround = true;
                AddWarning(r, i, "constrain", "keepGround", "W189",
                    "keepGround=true:将保留 component1 的固定约束。固定件上的约束不会让零件移动,"
                    + "约束只对另一方生效或根本无法求解;除非 component1 就是你故意固定的基准件,否则别加这个参数。");
            }
            if (op.ConstrainType == "axial" && op.Mate)
            {
                // axial 的第三参语义与 planar 相同:True 法向同向。轴向对齐通常两个都成立,不拦只提示。
                AddWarning(r, i, "constrain", "mate", "W188",
                    "axial 约束的 mate 参数一般不敏感(共轴两种取值等效),省略即可。");
            }
        }

        // ---------------- rebind 校验 ----------------

        /// <summary>
        /// rebind:救「零件重建后约束引用的面没了」(detailed_status=6)。
        /// 只需要 relation(可省略=自动全扫);face1/face2 可选,给了就覆盖自动认面。
        /// </summary>
        private static void ValidateRebind(JsonElement e, int i, AssemblyOp op, AssemblyReport r)
        {
            op.RelationIndex = -1;
            if (e.TryGetProperty("relation", out JsonElement rv) && rv.ValueKind != JsonValueKind.Null)
            {
                if (rv.ValueKind == JsonValueKind.Number && rv.TryGetInt32(out int idx))
                {
                    if (idx < 0)
                    {
                        AddError(r, i, "rebind", "relation", "E192", "relation 是 0-based 关系序号,不能为负。");
                        return;
                    }
                    op.RelationIndex = idx;
                }
                else if (rv.ValueKind == JsonValueKind.String)
                {
                    string s = (rv.GetString() ?? "").Trim().ToLowerInvariant();
                    if (s != "auto" && s.Length > 0)
                    {
                        AddError(r, i, "rebind", "relation", "E192",
                            "relation 要 0-based 序号(数字)或 \"auto\";省略 = auto(处理全部 detailed_status=6 的关系)。");
                        return;
                    }
                    op.RelationIndex = -1;
                }
                else
                {
                    AddError(r, i, "rebind", "relation", "E192", "relation 要数字序号或 \"auto\"。");
                    return;
                }
            }

            string ferr;
            op.Face1Ref = ParseFaceRef(e, "face1", out ferr);
            if (ferr != null) { AddError(r, i, "rebind", "face1", "E191", ferr); return; }
            op.Face2Ref = ParseFaceRef(e, "face2", out ferr);
            if (ferr != null) { AddError(r, i, "rebind", "face2", "E191", ferr); return; }

            if (op.RelationIndex < 0 && op.Face1Ref != null && op.Face2Ref == null)
            {
                AddWarning(r, i, "rebind", "face1", "W193",
                    "自动模式(relation 省略)下给了 face1 但没给 face2:命中多条关系时 face1 会套到每条关系上,"
                    + "通常不是你想要的 —— 建议先 se_assembly_query mode=relations 看清哪条坏了,再指定 relation。");
            }
        }

        // ---------------- 约束面引用解析 ----------------

        /// <summary>
        /// 解析 face1 / face2。支持四种写法,返回 int / string / FaceSelector,统一交给运行期 BuildFaceBinding 解析。
        /// 解析不出就返回 null 并给 error(调用方报错,不猜)。
        /// </summary>
        private static object ParseFaceRef(JsonElement e, string name, out string error)
        {
            error = null;
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null
                || v.ValueKind == JsonValueKind.Undefined)
                return null;

            if (v.ValueKind == JsonValueKind.Number)
            {
                int idx;
                if (!v.TryGetInt32(out idx))
                {
                    error = name + " 要整数面序号。";
                    return null;
                }
                if (idx < 0)
                {
                    error = name + "=" + idx + " 是 0-based 面序号,不能为负。";
                    return null;
                }
                return idx;
            }

            if (v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    error = name + " 是空字符串。";
                    return null;
                }
                s = s.Trim();
                if (s.StartsWith("obj-", StringComparison.OrdinalIgnoreCase)) return s;   // 句柄,运行期取
                FaceSelector sel = FaceSelector.FromString(s, out error);
                if (sel == null)
                {
                    error = name + " 描述符解析失败: " + error;
                    return null;
                }
                return sel;
            }

            if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("handle", out JsonElement h) && h.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(h.GetString()))
                    return h.GetString().Trim();
                if (v.TryGetProperty("index", out JsonElement ix) && ix.ValueKind == JsonValueKind.Number
                    && ix.TryGetInt32(out int i2))
                {
                    if (i2 < 0)
                    {
                        error = name + ".index=" + i2 + " 不能为负。";
                        return null;
                    }
                    return i2;
                }
                FaceSelector sel = FaceSelector.FromJson(v, out error);
                if (sel == null)
                {
                    error = name + " 描述符无效: " + error;
                    return null;
                }
                if (sel.IsEmpty)
                {
                    error = name + " 是空描述符:至少给 type/diaMm/atMm/normal/lenMm 之一。";
                    return null;
                }
                return sel;
            }

            error = name + " 写法不支持:要 0-based 序号(数字) / 描述符字符串 / 描述符对象 / {\"handle\":\"obj-N\"}。";
            return null;
        }

        // ---------------- 组件引用解析辅助 ----------------

        /// <summary>读 "component" 字段:整数(0-based)或字符串(名称 / obj-N)。</summary>
        private static object GetComponent(JsonElement e)
        {
            return GetComponentField(e, "component");
        }

        private static object GetComponentField(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out JsonElement v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int idx)) return idx;
            if (v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return null;
        }

        private static void RequireComponent(AssemblyReport r, int i, string opName)
        {
            AddError(r, i, opName, "component", "E105",
                opName + " 缺少 \"component\"(0-based 序号,或零件名如 \"Par1.par:1\",或句柄 obj-N)。");
        }

        // ---------------- JSON 取值辅助 ----------------

        private static string GetString(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        private static bool TryNumber(JsonElement e, string name, out double value)
        {
            value = 0;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                && v.ValueKind == JsonValueKind.Number)
            {
                return v.TryGetDouble(out value);
            }
            return false;
        }

        private static bool TryInt(JsonElement e, string name, out int value)
        {
            value = 0;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                && v.ValueKind == JsonValueKind.Number)
            {
                return v.TryGetInt32(out value);
            }
            return false;
        }

        private static bool TryBool(JsonElement e, string name, out bool value)
        {
            value = false;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                && v.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v2)
                && v2.ValueKind == JsonValueKind.False) { value = false; return true; }
            return false;
        }

        private static double[] GetDoubleArray(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v)
                || v.ValueKind != JsonValueKind.Array) return null;
            var list = new List<double>();
            foreach (JsonElement item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double d)) return null;
                list.Add(d);
            }
            return list.ToArray();
        }

        private static int[] GetIntArray(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v)
                || v.ValueKind != JsonValueKind.Array) return null;
            var list = new List<int>();
            foreach (JsonElement item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int d)) return null;
                list.Add(d);
            }
            return list.ToArray();
        }

        private static bool TryNumbers(JsonElement e, string[] names, out double[] values)
        {
            values = new double[names.Length];
            for (int k = 0; k < names.Length; k++)
            {
                if (!TryNumber(e, names[k], out values[k])) return false;
            }
            return true;
        }

        // ---------------- issue 记录 ----------------

        private static void AddError(AssemblyReport r, int opIndex, string opName, string field, string code, string message)
        {
            r.issues.Add(new AssemblyIssue
            {
                code = code, level = "error", op = opIndex, opName = opName,
                field = field, message = message, fix = null
            });
            r.errorCount++;
            r.status = "error";
        }

        private static void AddWarning(AssemblyReport r, int opIndex, string opName, string field, string code, string message)
        {
            r.issues.Add(new AssemblyIssue
            {
                code = code, level = "warning", op = opIndex, opName = opName,
                field = field, message = message, fix = null
            });
            r.warningCount++;
        }

        // ---------------- 共享 COM 辅助(Build/Query 两个工具文件都用) ----------------

        /// <summary>读属性(IDispatch PROPERTYGET)。读不到返回 null,不抛。</summary>
        internal static object Get(object target, string name)
        {
            try
            {
                if (ManualInvoke.TryInvoke(target, name, null, out object result, out _)) return result;
            }
            catch { }
            return null;
        }

        /// <summary>读属性,失败给默认值。</summary>
        internal static object Get(object target, string name, object fallback)
        {
            object v = Get(target, name);
            return v ?? fallback;
        }

        /// <summary>调方法(IDispatch)。失败抛异常。</summary>
        internal static object Call(object target, string name, object[] args)
        {
            if (!ManualInvoke.TryInvoke(target, name, args, out object result, out Exception err))
                throw new InvalidOperationException("COM 调用 " + name + " 失败: " + (err != null ? DescribeEx(err) : "未知错误"), err);
            return result;
        }

        /// <summary>写属性(IDispatch PROPERTYPUT)。失败抛异常。</summary>
        internal static void Put(object target, string name, object value)
        {
            if (!ManualInvoke.TryInvokeSet(target, name, value, out Exception err))
                throw new InvalidOperationException("写属性 " + name + " 失败: " + (err != null ? DescribeEx(err) : "未知错误"), err);
        }

        /// <summary>异常描述:展开 InnerException 链 + HRESULT,不让 TargetInvocationException 吞掉真实原因。(P2-1:逻辑已收编至 EvidenceContract,保留入口兼容既有调用)</summary>
        internal static string DescribeEx(Exception ex)
        {
            return EvidenceContract.DescribeException(ex);
        }

        /// <summary>
        /// 读 Occurrence 的 16 元素变换矩阵(行主序 4x4,平移在 [12..14],[15]=1)。
        /// 官方 C# 示例写法(SolidEdgeAssembly~Occurrence~GetMatrix.html):强类型 cast + Array.CreateInstance + ref。
        /// 读不到返回 null(不猜)。
        /// </summary>
        internal static double[] ReadMatrix(object occurrence)
        {
            try
            {
                var occ = (SolidEdgeAssembly.Occurrence)occurrence;
                Array m = Array.CreateInstance(typeof(double), 0);
                occ.GetMatrix(ref m);
                if (m != null && m.Length >= 16)
                {
                    var r = new double[16];
                    for (int k = 0; k < 16; k++) r[k] = Convert.ToDouble(m.GetValue(k), CultureInfo.InvariantCulture);
                    return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>装配文档判定:Document.Type == 3(igAssemblyDocument,离线枚举表核实)。支持焊接件装配(同值族)时另行放行。</summary>
        internal static bool IsAssemblyDocument(object doc)
        {
            try
            {
                object t = Get(doc, "Type");
                return t != null && Convert.ToInt32(t, CultureInfo.InvariantCulture) == 3;
            }
            catch { return false; }
        }

        /// <summary>SE2022 实测(2026-09-22):GroundRelation3d.Type = 1959028688(大常量),不是来源项目文档化映射的 0。</summary>
        internal const long GroundRelationTypeValue = 1959028688L;

        /// <summary>
        /// 判断某零件是否带 Ground 固定关系(按归属匹配,只读不删)。
        /// 判定用强类型 cast(RCW as GroundRelation3d),Type 数值只作辅助——别信数值映射表。
        /// </summary>
        internal static bool HasGround(object relations, string occName)
        {
            string deleted;
            return FindGround(relations, occName, false, out deleted);
        }

        /// <summary>
        /// 解除某零件的 Ground 固定关系(按归属匹配后删除)。
        /// 没有 Ground 时幂等返回 false(不报错,"先解固再约束"工作流可重跑)。
        /// </summary>
        internal static bool TryUnground(object relations, string occName, out string deletedName)
        {
            return FindGround(relations, occName, true, out deletedName);
        }

        private static bool FindGround(object relations, string occName, bool delete, out string matchedName)
        {
            matchedName = null;
            if (relations == null || string.IsNullOrEmpty(occName)) return false;
            object c = Get(relations, "Count");
            if (c == null) return false;
            int count;
            if (!int.TryParse(Convert.ToString(c, CultureInfo.InvariantCulture), out count)) return false;
            for (int i = count; i >= 1; i--)
            {
                object rel;
                try { rel = Call(relations, "Item", new object[] { i }); } catch { continue; }
                var gr = rel as SolidEdgeAssembly.GroundRelation3d;
                if (gr == null)
                {
                    object t = Get(rel, "Type");
                    long tv;
                    if (t == null || !long.TryParse(Convert.ToString(t, CultureInfo.InvariantCulture), out tv)
                        || tv != GroundRelationTypeValue) continue;
                }
                object relOcc = Get(rel, "Occurrence");    // GroundRelation3d.Occurrence(离线文档核实)
                string relOccName = relOcc == null ? null : Convert.ToString(Get(relOcc, "Name"), CultureInfo.InvariantCulture);
                if (!string.Equals(relOccName, occName, StringComparison.OrdinalIgnoreCase)) continue;
                matchedName = relOccName;
                if (delete) gr.Delete();
                return true;
            }
            return false;
        }
    }
}
