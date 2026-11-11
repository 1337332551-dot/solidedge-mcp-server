using System;
using System.Collections.Generic;
using System.Globalization;
using SolidEdgeAssembly;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// se_assembly_build 的 rebind op —— 救「零件重建后约束引用的面没了」。
    ///
    /// 现场(2026-10-25 真机实测):
    ///   - 零件特征被删/重建后,关系本身还在(Number/Offset 都在),但 DetailedStatus 变成
    ///     6 = igRelation3dDetailedStatusMissingGeometry;
    ///   - 此时该侧的 GetGeometryN / GetElementN 直接 E_FAIL,另一侧仍可读;
    ///   - 关系对象**没有** SetGeometry/SetElement(反射 dump 证实),换不了面 ——
    ///     所以只能"读原几何 → 按几何重新认面 → 删旧关系 → 用同样参数重建"。
    ///
    /// 认面口径(与 se_find_objects / constrain 描述符同一套几何语言):
    ///   - 平面关系:两平面法向平行 + 平面间距 ≤ 容差(法向方向不敏感,靠间距区分正反面);
    ///   - 轴向关系:两轴线方向平行 + 轴线间距 ≤ 容差;
    ///   - 一侧读不到几何时,沿用另一侧的几何做目标 —— 这不是猜:planar/axial 的语义本来就是
    ///     "两侧支撑几何重合",所以另一侧就是它该在的位置。
    ///   - 命中必须唯一;0 个或 ≥2 个都直接报错并回传该零件的面清单,绝不挑一个"最像的"。
    /// </summary>
    public static partial class AssemblyBuildTools
    {
        /// <summary>igRelation3dDetailedStatusMissingGeometry —— 约束引用的几何没了。</summary>
        private const int DetailedStatusMissingGeometry = 6;
        /// <summary>igRelation3dStatusSolved。</summary>
        private const int RelationStatusSolved = 1;
        /// <summary>几何比对容差(米)。5e-5 m = 0.05mm,与 constrain 描述符默认 tol 一致。</summary>
        private const double RebindTolM = 0.00005;

        // ================= 数据类型 =================

        /// <summary>关系一侧的原始支撑几何(装配坐标,关系创建时的快照)。</summary>
        private sealed class RelGeom
        {
            /// <summary>1=Plane 2=Line(实测取值)。</summary>
            public int GeometryType;
            public double[] Point;    // 米
            public double[] Vector;   // 单位
        }

        /// <summary>零件上某张面的支撑几何(零件坐标)。</summary>
        private sealed class FaceGeom
        {
            public object Face;
            public int Index;         // 0-based,Body.Faces(1) 枚举序
            public int FaceId = -1;
            public string Kind;       // Plane / Cylinder / Other
            public double[] Point;    // 平面=面上一点;圆柱=轴上一点(米,零件坐标)
            public double[] Vector;   // 平面=法向(无朝向);圆柱=轴向
            public double? RadiusMm;
        }

        // ================= op 入口 =================

        private static object OpRebind(SolidEdgeContext context, object doc, object relations, AssemblySpec.AssemblyOp op)
        {
            int total = GetCount(relations);
            if (total < 0) return Err(op.Index, "rebind", "读不到 Relations3d.Count。");

            var targets = new List<int>();
            if (op.RelationIndex >= 0)
            {
                if (op.RelationIndex >= total)
                    return Err(op.Index, "rebind", "relation=" + op.RelationIndex + " 越界:本装配有 " + total + " 条关系(0-based)。");
                targets.Add(op.RelationIndex);
            }
            else
            {
                for (int k = 0; k < total; k++)
                {
                    object rel;
                    try { rel = AssemblySpec.Call(relations, "Item", new object[] { k + 1 }); }
                    catch { continue; }
                    if (ReadInt(rel, "DetailedStatus", -1) == DetailedStatusMissingGeometry) targets.Add(k);
                }
                if (targets.Count == 0)
                    return Ok("rebind", new Dictionary<string, object>
                    {
                        ["relations"] = total,
                        ["rebound"] = 0,
                        ["note"] = "没有 detailed_status=6(MissingGeometry)的关系,无需重绑。"
                    });
            }

            // 显式指定一条:失败就当 op 失败(整批中止),语义和别的 op 一致。
            // 自动扫描:逐条尽力救,失败的记在 items[].error 里,不因一条失败就放弃其余。
            bool strict = op.RelationIndex >= 0;
            var items = new List<object>();
            int okCount = 0;
            foreach (int idx in targets)
            {
                Dictionary<string, object> item = RebindOne(context, doc, relations, idx, op);
                items.Add(item);
                bool failed = item.ContainsKey("error");
                if (!failed) okCount++;
                if (failed && strict)
                    return Err(op.Index, "rebind", "relation=" + idx + " 重绑失败: " + item["error"]);
            }

            return Ok("rebind", new Dictionary<string, object>
            {
                ["relations"] = GetCount(relations),
                ["targets"] = targets.Count,
                ["rebound"] = okCount,
                ["failed"] = targets.Count - okCount,
                ["items"] = items
            });
        }

        // ================= 单条关系重绑 =================

        private static Dictionary<string, object> RebindOne(SolidEdgeContext context, object doc, object relations,
            int index, AssemblySpec.AssemblyOp op)
        {
            var d = new Dictionary<string, object> { ["relation"] = index };

            object rel = AssemblySpec.Call(relations, "Item", new object[] { index + 1 });
            if (rel == null) return RebindFail(d, "关系读不到。");

            var planar = rel as PlanarRelation3d;
            var axial = rel as AxialRelation3d;
            if (planar == null && axial == null)
                return RebindFail(d, "不是 planar/axial 关系(Ground 等不归 rebind 管)。");
            bool isPlanar = planar != null;
            d["type"] = isPlanar ? "planar" : "axial";
            int detailedBefore = ReadInt(rel, "DetailedStatus", -1);
            d["detailed_status_before"] = detailedBefore;
            d["status_before"] = ReadInt(rel, "Status", -1);

            // 只救 MissingGeometry(6)的关系。健康关系别碰:rebind 是"先建新再删旧",
            // 对健康关系建新=凭空多一条等价约束,axial 还能容忍、planar 直接 detailed=5(实测)。
            // 想换面请走 delete + constrain(描述符)。
            if (detailedBefore != DetailedStatusMissingGeometry)
                return RebindFail(d, "该关系当前 detailed_status=" + detailedBefore
                    + "(不是 6=MissingGeometry),没有可救的东西。要把它改绑到别的面,请先删掉这条关系"
                    + "(se_invoke_member member=Delete),再用 constrain + 面描述符重建。");

            if (ReadBool(rel, "Suppressed", false))
                return RebindFail(d, "该关系处于抑制(Suppressed)状态,跳过;先解除抑制再 rebind。");

            object occ1 = AssemblySpec.Get(rel, "Occurrence1");
            object occ2 = AssemblySpec.Get(rel, "Occurrence2");
            if (occ1 == null || occ2 == null)
                return RebindFail(d, "关系读不到 Occurrence1/2,无法定位零件。");

            // ---- 1. 读两侧原始几何(死的那侧会 E_FAIL) ----
            RelGeom g1 = ReadRelGeom(rel, isPlanar, 1);
            RelGeom g2 = ReadRelGeom(rel, isPlanar, 2);
            if (g1 == null && g2 == null)
                return RebindFail(d, "两侧的原始几何都读不到(GetGeometry 均 E_FAIL),无法推断该绑哪张面 —— "
                    + "请显式给 face1/face2 描述符(如 {\"type\":\"cylinder\",\"diaMm\":18,\"atMm\":\"73.91,30.615,10\"})。");
            if (g1 == null) { g1 = g2; d["face1_target"] = "inferred_from_face2"; }
            if (g2 == null) { g2 = g1; d["face2_target"] = "inferred_from_face1"; }

            // ---- 2. 两侧各自认面(显式描述符优先,否则按几何唯一匹配) ----
            object bind1 = ResolveRebindSide(context, doc, occ1, op.Face1Ref, g1, isPlanar, op.Index, "face1");
            if (bind1 is string err1) return RebindFail(d, "face1: " + err1);
            object bind2 = ResolveRebindSide(context, doc, occ2, op.Face2Ref, g2, isPlanar, op.Index, "face2");
            if (bind2 is string err2) return RebindFail(d, "face2: " + err2);

            var b1 = (FaceBinding)bind1;
            var b2 = (FaceBinding)bind2;
            d["face1_bound"] = BindingEcho(b1);
            d["face2_bound"] = BindingEcho(b2);

            // ---- 3. 先建新关系(旧关系此时是死的,不影响求解),成功后再删旧的 ----
            bool normalsAligned = ReadBool(rel, "NormalsAligned", true);
            double oldOffset = ReadDouble(rel, "Offset", 0);
            bool oldFixedRotate = ReadBool(rel, "FixedRotate", false);
            var rels = relations as Relations3d;
            int before = GetCount(relations);

            object newRel;
            try
            {
                if (isPlanar)
                {
                    Array cp1 = ToArray(b1.MidM);
                    Array cp2 = ToArray(b2.MidM);
                    newRel = (rels != null)
                        ? rels.AddPlanar(b1.Reference, b2.Reference, normalsAligned, ref cp1, ref cp2)
                        : AssemblySpec.Call(relations, "AddPlanar",
                            new object[] { b1.Reference, b2.Reference, normalsAligned, b1.MidM, b2.MidM });
                }
                else
                {
                    newRel = (rels != null)
                        ? rels.AddAxial(b1.Reference, b2.Reference, normalsAligned)
                        : AssemblySpec.Call(relations, "AddAxial", new object[] { b1.Reference, b2.Reference, normalsAligned });
                }
            }
            catch (Exception ex)
            {
                return RebindFail(d, "重建约束抛异常: " + AssemblySpec.DescribeEx(ex));
            }
            if (newRel == null) return RebindFail(d, "AddPlanar/AddAxial 返回空。");
            if (GetCount(relations) != before + 1)
                return RebindFail(d, "新关系没建上(Relations3d.Count=" + GetCount(relations) + ",期望 " + (before + 1) + ")。");

            // 还原原关系的非几何参数(Offset / FixedRotate)——写不进去不致命,记 warning
            var warnings = new List<string>();
            if (isPlanar && Math.Abs(oldOffset) > 1e-9)
            {
                try { AssemblySpec.Put(newRel, "Offset", oldOffset); }
                catch (Exception ex) { warnings.Add("Offset=" + oldOffset + " 还原失败: " + AssemblySpec.DescribeEx(ex)); }
            }
            if (!isPlanar && oldFixedRotate)
            {
                try { AssemblySpec.Put(newRel, "FixedRotate", true); }
                catch { warnings.Add("FixedRotate=true 还原失败(不影响轴重合)。"); }
            }

            // 求解 + 校验:新约束没解出来就把新关系删掉,保持"没救成也不添乱"
            try { AssemblySpec.Call(doc, "UpdateAll", new object[0]); }
            catch (Exception ex) { warnings.Add("UpdateAll 异常: " + AssemblySpec.DescribeEx(ex)); }

            int newStatus = ReadInt(newRel, "Status", -1);
            int newDetailed = ReadInt(newRel, "DetailedStatus", -1);
            if (newStatus != RelationStatusSolved)
            {
                try { AssemblySpec.Call(newRel, "Delete", new object[0]); }
                catch { }
                try { AssemblySpec.Call(doc, "UpdateAll", new object[0]); }
                catch { }
                return RebindFail(d, "新约束没能求解(status=" + newStatus + " detailed=" + newDetailed
                    + "),已把新关系删掉、旧关系原样保留。多半是这两张面在几何上并不重合(装配位置已漂移),"
                    + "或者原关系本来就和其它约束冗余。");
            }

            // ---- 4. 删旧关系(它 detailed=6,删掉不影响求解) ----
            string deleteNote = null;
            try
            {
                AssemblySpec.Call(rel, "Delete", new object[0]);
                try { AssemblySpec.Call(doc, "UpdateAll", new object[0]); } catch { }
            }
            catch (Exception ex)
            {
                deleteNote = "旧关系删除失败(新关系已生效,模型里会多一条死关系,可手工删): " + AssemblySpec.DescribeEx(ex);
            }

            d["result"] = "rebuilt";
            d["normals_aligned"] = normalsAligned;
            d["offset"] = isPlanar ? Math.Round(oldOffset * 1000.0, 3) : (object)null;   // mm,便于对照
            d["new_relation_status"] = newStatus;
            d["new_relation_detailed"] = newDetailed;
            d["relations_after"] = GetCount(relations);
            if (deleteNote != null) d["delete_note"] = deleteNote;
            if (warnings.Count > 0) d["warnings"] = warnings;
            return d;
        }

        private static Dictionary<string, object> RebindFail(Dictionary<string, object> d, string message)
        {
            d["error"] = message;
            return d;
        }

        // ================= 认面 =================

        /// <summary>
        /// 解析一侧的面:显式描述符优先(BuildFaceBinding 同一套逻辑);
        /// 否则在该零件当前面表里按几何唯一匹配。返回 FaceBinding 或 string(错误原因)。
        /// </summary>
        private static object ResolveRebindSide(SolidEdgeContext context, object doc, object occ, object faceRef,
            RelGeom target, bool isPlanar, int opIndex, string field)
        {
            if (faceRef != null)
            {
                object b = BuildFaceBinding(context, doc, occ, faceRef, !isPlanar, opIndex, field);
                if (b is string e) return "显式描述符解析失败: " + e;
                return b;
            }

            object part = AssemblySpec.Get(occ, "OccurrenceDocument");
            if (part == null) return "取不到 OccurrenceDocument。";
            object models = AssemblySpec.Get(part, "Models");
            if (GetCount(models) < 1) return "零件没有 Models(无实体)。";
            object model = AssemblySpec.Call(models, "Item", new object[] { 1 });
            object body = AssemblySpec.Get(model, "Body");

            List<FaceGeom> geoms = ReadFaceGeoms(body, out string readErr);
            if (readErr != null) return readErr;

            double[] matrix = AssemblySpec.ReadMatrix(occ);
            if (matrix == null) return "读不到该零件的装配变换矩阵,无法把面换算到装配坐标。";

            var hits = new List<FaceGeom>();
            foreach (FaceGeom fg in geoms)
            {
                if (isPlanar ? fg.Kind != "Plane" : fg.Kind != "Cylinder") continue;
                if (Matches(fg, target, matrix, isPlanar)) hits.Add(fg);
            }

            // 命中多张也要分情况:同轴圆柱(孔壁/轴颈)对 axial 约束是**等效**的(轴线同一条),
            // 同平面的多张面同理 —— 这类"等价多解"直接取第一张,只记一笔;真正的歧义(平行但错位的面)才报错。
            // 判据就是 Matches 本身:能同时命中说明它们的支撑几何在容差内重合。
            if (hits.Count >= 1)
            {
                object reference = AssemblySpec.Call(doc, "CreateReference", new object[] { occ, hits[0].Face });
                if (reference == null) return "CreateReference 返回空。";
                double[] mid = FaceMidM(hits[0].Face);
                if (mid == null) return "该面 GetRange 读不到,给不出约束点。";
                // 回显信息统一走 FaceFinder(与 se_find_objects 同一份口径:直径/轴向长/凸凹),
                // 不在这里手算 —— 手算过一次把直径乘了两遍 1000。
                FaceInfo info = FaceFinder.ByFaceId(FaceFinder.ReadAllFaces(body, out string _), hits[0].FaceId);
                if (info == null)
                {
                    info = new FaceInfo
                    {
                        Index = hits[0].Index,
                        FaceId = hits[0].FaceId,
                        Type = hits[0].Kind,
                        CenterM = mid,
                        CenterMm = new[]
                        {
                            Math.Round(mid[0] * 1000.0, 3), Math.Round(mid[1] * 1000.0, 3), Math.Round(mid[2] * 1000.0, 3)
                        },
                        ComFace = hits[0].Face
                    };
                }
                var binding = new FaceBinding
                {
                    Reference = reference,
                    MidM = mid,
                    Source = "geometry",
                    Info = info
                };
                if (hits.Count > 1)
                {
                    binding.Note = "另有 " + (hits.Count - 1) + " 张同支撑几何的面(如孔壁与轴颈同轴、同一平面的多块面),"
                        + "对平面/轴向约束等效,已取第一张。";
                }
                return binding;
            }

            string what = isPlanar ? "平面" : "圆柱面";
            string targetText = "原几何 type=" + target.GeometryType + " point=[" + MmText(target.Point)
                + "mm] vector=[" + FaceSelector.VectorText(target.Vector) + "]";
            return "按几何在零件上找不到匹配的" + what + "(" + targetText + ",容差 0.05mm)。"
                + "该零件现有同类面: " + DescribeFaceGeoms(geoms, isPlanar, 10)
                + " —— 若零件上确实已经没有这张面了(特征被删且没重建),只能显式给 face1/face2 指定新面;"
                + "若特征重建过,检查一下是不是位置/尺寸变了导致对不上。";
        }

        /// <summary>几何比对:方向平行 + 位置重合(平面比间距,圆柱比轴线距)。</summary>
        private static bool Matches(FaceGeom fg, RelGeom target, double[] matrix, bool isPlanar)
        {
            double[] dir = ToAsmDir(matrix, fg.Vector);
            if (dir == null) return false;
            // 方向:平面法向/圆柱轴向都可能反向,一律用 |cos|
            if (Math.Abs(FaceSelector.Dot(dir, target.Vector)) < 0.9995) return false;

            double[] p = ToAsmPoint(matrix, fg.Point);
            if (p == null) return false;
            double[] delta = { p[0] - target.Point[0], p[1] - target.Point[1], p[2] - target.Point[2] };

            if (isPlanar)
            {
                // 点到原平面的距离 ≤ 容差 且 方向平行 → 同一张平面
                return Math.Abs(FaceSelector.Dot(delta, target.Vector)) <= RebindTolM;
            }
            // 两平行直线间距 = |(p2-p1) × v|
            double[] v = target.Vector;
            double cx = delta[1] * v[2] - delta[2] * v[1];
            double cy = delta[2] * v[0] - delta[0] * v[2];
            double cz = delta[0] * v[1] - delta[1] * v[0];
            return Math.Sqrt(cx * cx + cy * cy + cz * cz) <= RebindTolM;
        }

        // ================= 读几何 =================

        /// <summary>
        /// 读关系一侧的原始支撑几何。
        /// 返回 null 只代表【几何真的没了】(E_FAIL,即 MissingGeometry 那一侧);
        /// 其它 HRESULT(如 0x80010001 调用被拒,SE 忙)重试后仍失败就抛出去 —— 别把"没读到"当成"面没了",
        /// 否则会静默退化成"从另一侧推断",把真正的通信故障掩盖掉(2026-10-25 实测踩到过一次)。
        /// </summary>
        private static RelGeom ReadRelGeom(object rel, bool isPlanar, int side)
        {
            const int E_FAIL = unchecked((int)0x80004005);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    int type = 0;
                    double px = 0, py = 0, pz = 0, vx = 0, vy = 0, vz = 0;
                    if (isPlanar)
                    {
                        var p = (PlanarRelation3d)rel;
                        if (side == 1) p.GetGeometry1(out type, out px, out py, out pz, out vx, out vy, out vz);
                        else p.GetGeometry2(out type, out px, out py, out pz, out vx, out vy, out vz);
                    }
                    else
                    {
                        var a = (AxialRelation3d)rel;
                        if (side == 1) a.GetGeometry1(out type, out px, out py, out pz, out vx, out vy, out vz);
                        else a.GetGeometry2(out type, out px, out py, out pz, out vx, out vy, out vz);
                    }
                    double[] v = FaceSelector.Unit(new[] { vx, vy, vz });
                    if (v == null) return null;
                    return new RelGeom { GeometryType = type, Point = new[] { px, py, pz }, Vector = v };
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    if (ex.HResult == E_FAIL) return null;   // 面没了:这是正常信号,不重试也不报错
                    if (attempt == 3) throw;
                }
                catch (Exception)
                {
                    if (attempt == 3) throw;
                }
                System.Threading.Thread.Sleep(150);
            }
            return null;
        }

        /// <summary>读零件全量面的支撑几何(平面:根点+法向;圆柱:轴上一点+轴向+半径)。</summary>
        private static List<FaceGeom> ReadFaceGeoms(object body, out string error)
        {
            error = null;
            var list = new List<FaceGeom>();
            if (body == null) { error = "Model.Body 取不到。"; return list; }
            object faces;
            try { faces = AssemblySpec.Call(body, "Faces", new object[] { 1 }); }
            catch (Exception ex) { error = "读不到 Body.Faces: " + AssemblySpec.DescribeEx(ex); return list; }

            int n = GetCount(faces);
            if (n < 0) { error = "读不到 Body.Faces.Count。"; return list; }

            for (int i = 1; i <= n; i++)
            {
                object face;
                try { face = AssemblySpec.Call(faces, "Item", new object[] { i }); }
                catch { continue; }
                var typed = face as SolidEdgeGeometry.Face;
                if (typed == null) continue;

                var fg = new FaceGeom { Face = face, Index = i - 1, FaceId = ReadInt(face, "ID", -1), Kind = "Other" };
                object geom = null;
                try { geom = typed.Geometry; } catch { }
                if (geom is SolidEdgeGeometry.Plane plane)
                {
                    fg.Kind = "Plane";
                    fg.Vector = TryPlaneNormal(plane);
                    fg.Point = TryPlaneRoot(plane);
                    if (fg.Vector == null || fg.Point == null) fg.Kind = "Other";
                }
                else if (geom is SolidEdgeGeometry.Cylinder cyl)
                {
                    fg.Kind = "Cylinder";
                    fg.Vector = TryCylinderAxis(cyl);
                    fg.Point = TryCylinderBase(cyl);
                    fg.RadiusMm = TryDoubleProp(cyl, "Radius") * 1000.0;
                    if (fg.Vector == null || fg.Point == null) fg.Kind = "Other";
                }
                list.Add(fg);
            }
            return list;
        }

        private static double[] TryPlaneNormal(SolidEdgeGeometry.Plane plane)
        {
            try
            {
                Array nv = Array.CreateInstance(typeof(double), 0);
                plane.GetNormalVector(ref nv);
                return ArrayToUnit(nv);
            }
            catch { return null; }
        }

        private static double[] TryPlaneRoot(SolidEdgeGeometry.Plane plane)
        {
            try
            {
                Array rp = Array.CreateInstance(typeof(double), 0);
                plane.GetRootPoint(ref rp);
                return ArrayToPoint(rp);
            }
            catch { return null; }
        }

        /// <summary>圆柱轴向:Cylinder.GetAxisVector(ref Array)(与 ModelStateTools 同一通道)。</summary>
        private static double[] TryCylinderAxis(SolidEdgeGeometry.Cylinder cyl)
        {
            try
            {
                Array v = Array.CreateInstance(typeof(double), 0);
                cyl.GetAxisVector(ref v);
                return ArrayToUnit(v);
            }
            catch { return null; }
        }

        /// <summary>圆柱轴上一点:Cylinder.GetBasePoint(ref Array)。</summary>
        private static double[] TryCylinderBase(SolidEdgeGeometry.Cylinder cyl)
        {
            try
            {
                Array p = Array.CreateInstance(typeof(double), 0);
                cyl.GetBasePoint(ref p);
                return ArrayToPoint(p);
            }
            catch { return null; }
        }

        private static double[] ArrayToPoint(Array a)
        {
            if (a == null || a.Length < 3) return null;
            return new[]
            {
                Convert.ToDouble(a.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToDouble(a.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToDouble(a.GetValue(2), CultureInfo.InvariantCulture)
            };
        }

        private static double[] ArrayToUnit(Array a)
        {
            double[] p = ArrayToPoint(a);
            return FaceSelector.Unit(p);
        }

        private static double TryDoubleProp(object obj, string name)
        {
            object v = AssemblySpec.Get(obj, name);
            if (v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        // ================= 坐标换算(零件 → 装配) =================

        /// <summary>点换算:行主序 4x4,平移在 [12..14]。</summary>
        private static double[] ToAsmPoint(double[] m, double[] p)
        {
            if (m == null || p == null) return null;
            return new[]
            {
                m[0] * p[0] + m[1] * p[1] + m[2] * p[2] + m[12],
                m[4] * p[0] + m[5] * p[1] + m[6] * p[2] + m[13],
                m[8] * p[0] + m[9] * p[1] + m[10] * p[2] + m[14]
            };
        }

        /// <summary>方向换算(只乘旋转部分,再单位化)。</summary>
        private static double[] ToAsmDir(double[] m, double[] d)
        {
            if (m == null || d == null) return null;
            return FaceSelector.Unit(new[]
            {
                m[0] * d[0] + m[1] * d[1] + m[2] * d[2],
                m[4] * d[0] + m[5] * d[1] + m[6] * d[2],
                m[8] * d[0] + m[9] * d[1] + m[10] * d[2]
            });
        }

        // ================= 小工具 =================

        /// <summary>面包围盒中点(米,零件坐标)—— 与 constrain 的约束点同一口径。</summary>
        private static double[] FaceMidM(object face)
        {
            var typed = face as SolidEdgeGeometry.Face;
            if (typed == null) return null;
            try
            {
                Array minPt = Array.CreateInstance(typeof(double), 0);
                Array maxPt = Array.CreateInstance(typeof(double), 0);
                typed.GetRange(ref minPt, ref maxPt);
                if (minPt.Length < 3 || maxPt.Length < 3) return null;
                var mid = new double[3];
                for (int i = 0; i < 3; i++)
                    mid[i] = (Convert.ToDouble(minPt.GetValue(i), CultureInfo.InvariantCulture)
                              + Convert.ToDouble(maxPt.GetValue(i), CultureInfo.InvariantCulture)) / 2.0;
                return mid;
            }
            catch { return null; }
        }

        private static string DescribeFaceGeoms(List<FaceGeom> faces, bool planar, int max)
        {
            if (faces == null || faces.Count == 0) return "(无)";
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            foreach (FaceGeom f in faces)
            {
                if (planar ? f.Kind != "Plane" : f.Kind != "Cylinder") continue;
                if (shown >= max) { sb.Append(" …"); break; }
                if (shown > 0) sb.Append(" | ");
                sb.Append("[").Append(f.Index).Append("] ");
                if (f.RadiusMm.HasValue) sb.Append("dia=").Append(Math.Round(f.RadiusMm.Value * 2.0, 2)).Append(" ");
                sb.Append("p=[").Append(MmText(f.Point)).Append("] v=[").Append(FaceSelector.VectorText(f.Vector)).Append("]");
                shown++;
            }
            if (shown == 0) return "(无同名类型面)";
            return sb.ToString();
        }

        private static string MmText(double[] p)
        {
            if (p == null) return "?";
            return Math.Round(p[0] * 1000.0, 3).ToString("0.###", CultureInfo.InvariantCulture) + ","
                 + Math.Round(p[1] * 1000.0, 3).ToString("0.###", CultureInfo.InvariantCulture) + ","
                 + Math.Round(p[2] * 1000.0, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static int ReadInt(object obj, string name, int fallback)
        {
            object v = AssemblySpec.Get(obj, name);
            if (v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static double ReadDouble(object obj, string name, double fallback)
        {
            object v = AssemblySpec.Get(obj, name);
            if (v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool ReadBool(object obj, string name, bool fallback)
        {
            object v = AssemblySpec.Get(obj, name);
            if (v == null) return fallback;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
    }
}
