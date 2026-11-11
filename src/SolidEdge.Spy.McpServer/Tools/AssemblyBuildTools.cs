using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 装配批量声明式操作:se_assembly_build —— 一次 MCP 调用完成"放零件 + 摆位 + 加约束 + 改属性"整套装配。
    ///
    /// 移植自 SolidEdge-MCP(Python)装配侧全部实测结论,签名按 SE2022 离线 SDK 文档核实:
    ///   放置 AddByFilename/AddWithMatrix(16 元素行主序,平移[12..14])/AddWithTransform(角度弧度)/
    ///        AddFamilyByFilename(恒落原点,后补 PutTransform)
    ///   变换 Move/Rotate(轴两点+弧度)/PutTransform/PutOrigin/PutMatrix(Matrix,Replace)/GetMatrix
    ///   约束 CreateReference(occ,face) 包裹 → AddPlanar(贴合/对齐)/AddAxial(圆柱面);先 unground 再约束
    ///   属性 Visible/Delete/Replace(file,all)/SetSuppressComponent/FaceStyle(颜色)
    ///
    /// ★ 每个 op 建完自动验证(集合 Count 增长 / 矩阵回读比对 / 关系 Status 回读),失败立即中止后续;
    ///   但**不自动删除**已产生的对象(回滚交给调用方判断后显式下 delete op,与仓库"脚本不做隐式删除"纪律一致)。
    /// ★ 诚实拒绝:角度/相切/点/齿轮约束、装配级阵列/镜像、解除压缩 —— 校验期即拦截并给替代方案(见 AssemblySpec)。
    /// </summary>
    [McpServerToolType]
    public static partial class AssemblyBuildTools
    {
        /// <summary>关系求解正常 Status 值(L2 实测:全 1 且 Offset=0 即求解成功)。</summary>
        private const long RelationStatusOk = 1;

        [McpServerTool, Description("装配批量声明式操作:一次调用完成放置零件、摆位、加约束、改属性。\n" +
            "op:place 放零件/move 平移/rotate 旋转/transform 全量变换/origin 挪位置/visible 显隐/" +
            "ground 固定与解固/delete 删除/replace 替换/reorder 调序/color 上色/" +
            "constrain 加约束(planar|axial)/rebind 修复失效面引用。\n" +
            "硬规则:component 写 0-based 序号、零件名(如 \"Par1.par:1\")或句柄 obj-N;" +
            "长度单位米,角度单位度;delete|replace|ground value=false 必须 confirm=true" +
            "(本批按最高危 op 统一预检,有未确认的就整批拒绝);" +
            "每个 op 建完自动验证、失败即中止后续,但不自动删除已产生的对象。" +
            "constrain 默认先解固 component1(固定件上加约束不生效)、component2 视为基准件;" +
            "face 推荐用描述符对象按几何认面,面序号会随特征增删漂移。\n" +
            "★ 各 op 的完整字段表与示例:首次用到时自动附在返回值的 opDocs 里(同一个 op 不重复注入);" +
            "随时可主动取 ops:[{\"op\":\"?\"}](索引)/[{\"op\":\"?\",\"name\":\"constrain\"}](单条)/" +
            "[{\"op\":\"?\",\"topic\":\"common\"}](通用规则)。")]
        public static string se_assembly_build(
            SolidEdgeContext context,
            [Description("操作列表(JSON 数组),每项见工具描述")] JsonElement[] ops,
            [Description("目标装配文档句柄,可省略;省略时用当前活动文档")] string objectId = null,
            [Description("true=只做静态校验并返回报告,不启动 COM;默认 false")] bool dryRun = false,
            [Description("破坏性操作(delete/replace/ground value=false)的确认开关,默认 false")] bool confirm = false)
        {
            // 手册查询:{"op":"?"}。纯字符串返回、不碰 COM,必须放在校验之前拦(否则 "?" 会被当成未知 op)。
            string manualText;
            if (AssemblyManual.TryQuery(ops, out manualText))
                return manualText;

            // 本批涉及、且手册收录的 op(去重)。四个出口都用它做按需注入。
            var docOps = OpsOf(ops);

            // ---- 1. 静态校验(纯函数,毫秒级,不碰 COM) ----
            var report = new AssemblySpec.AssemblyReport { opCount = ops == null ? 0 : ops.Length };
            var parsed = new List<AssemblySpec.AssemblyOp>();
            if (ops == null || ops.Length == 0)
            {
                AssemblySpec.ParseAndValidate(default, 0, report);
            }
            else
            {
                for (int i = 0; i < ops.Length; i++)
                    parsed.Add(AssemblySpec.ParseAndValidate(ops[i], i, report));
            }

            if (dryRun)
            {
                Dictionary<string, string> dryDocs;
                string dryNote;
                TakeDocs(docOps, out dryDocs, out dryNote);
                return JsonSerializer.Serialize(new
                {
                    status = "dry-run",
                    validate = report,
                    opDocs = dryDocs,
                    opDocsNote = dryNote
                }, AssemblySpec.AssemblyReport.JsonOpts);
            }

            if (report.HasError)
            {
                Dictionary<string, string> errDocs;
                string errNote;
                TakeDocs(docOps, out errDocs, out errNote);
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "静态校验未通过(" + report.errorCount + " 个 error),未执行任何操作。按 issues[].message/fix 修正后重跑;" +
                              "标 E190 的是已实测不可用的操作(诚实拒绝),见其中的替代方案。",
                    validate = report,
                    opDocs = errDocs,
                    opDocsNote = errNote
                }, AssemblySpec.AssemblyReport.JsonOpts);
            }

            // ---- 2. 只读护栏:装配批量操作全是写,只读模式整体拒绝 ----
            if (Guardrail.ReadOnlyEnabled)
            {
                AuditLog.Write("se_assembly_build", objectId ?? "(active)", "ops", false,
                    "n=" + parsed.Count, InvocationRisk.ModelChanging, false, "blocked by guardrail");
                Dictionary<string, string> roDocs;
                string roNote;
                TakeDocs(docOps, out roDocs, out roNote);
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "当前处于只读模式(SE_MCP_READONLY=1),装配批量操作被拒绝。",
                    opDocs = roDocs,
                    opDocsNote = roNote
                });
            }

            // ---- 3. 执行(整体 STA 封送) ----
            var results = new List<object>();
            bool aborted = false;
            string abortReason = null;

            context.Invoke(() =>
            {
                object doc = ResolveDocument(context, objectId);
                if (doc == null)
                {
                    results.Add(Err(0, "(doc)", "没有可用的装配文档:活动文档为空或句柄无效。"));
                    aborted = true;
                    return;
                }
                if (!AssemblySpec.IsAssemblyDocument(doc))
                {
                    results.Add(Err(0, "(doc)", "活动文档不是装配文档(Document.Type != 3=igAssemblyDocument)。"));
                    aborted = true;
                    return;
                }

                object occurrences = AssemblySpec.Get(doc, "Occurrences");
                object relations = AssemblySpec.Get(doc, "Relations3d");

                // 2026-10-25 审计修正:破坏性 confirm 改为执行前全量预检(原为循环内逐 op 检查,
                // 会导致排在破坏性 op 之前的非破坏 op 先真实执行、产生"部分已执行才被拒"窗口,
                // 与 se_invoke_chain 的"整链预扫描、一个不跑"语义不一致)。
                // 执行前全量 confirm 预检:任一破坏性 op 未确认 → 整批拒绝,一个都不执行
                for (int i = 0; i < parsed.Count; i++)
                {
                    var op = parsed[i];
                    if (op.Destructive && !confirm)
                    {
                        results.Add(Err(i, op.Op, "破坏性操作(" + op.Op + ",第" + (i + 1) + "个 op)需要 confirm=true 才会执行;本批已整体拒绝,未执行任何 op。"));
                        aborted = true;
                        abortReason = "confirm";
                        return;
                    }
                }

                for (int i = 0; i < parsed.Count; i++)
                {
                    var op = parsed[i];
                    try
                    {
                        AuditLog.Write("se_assembly_build", objectId ?? "(active)", op.Op, false,
                            Summarize(op),
                            op.Destructive ? InvocationRisk.Destructive : InvocationRisk.ModelChanging,
                            true, null);

                        object r = ExecOp(context, doc, occurrences, relations, op);
                        results.Add(r);
                        if (!IsOk(r))
                        {
                            aborted = true;
                            abortReason = op.Op;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(Err(i, op.Op, "执行异常: " + AssemblySpec.DescribeEx(ex)));
                        aborted = true;
                        abortReason = op.Op;
                        return;
                    }
                }
            });

            int okCount = 0;
            foreach (var r in results)
                if (IsOk(r)) okCount++;

            if (aborted && results.Count > 0 && IsErr(results[0]) && abortReason == null)
            {
                // 文档级失败(非装配文档/无文档),直接返回单条错误
                return JsonSerializer.Serialize(results[0], AssemblySpec.AssemblyReport.JsonOpts);
            }

            Dictionary<string, string> doneDocs;
            string doneNote;
            TakeDocs(docOps, out doneDocs, out doneNote);

            return JsonSerializer.Serialize(new
            {
                status = aborted ? "partial" : "ok",
                executed = results.Count,
                total = parsed.Count,
                ok = okCount,
                aborted = aborted,
                abortedAtOp = abortReason,
                note = aborted
                    ? "在 " + abortReason + " 处失败,后续 op 未执行;已成功的 op 不回滚(需要撤销请显式下 delete/ground 等 op)。"
                    : null,
                results = results,
                opDocs = doneDocs,
                opDocsNote = doneNote
            }, AssemblySpec.AssemblyReport.JsonOpts);
        }

        /// <summary>
        /// 取本批涉及、且手册收录的 op(去重、保持出现顺序)。
        /// 写错或已被拒绝的 op 不在手册里,直接跳过,避免往 opDocs 里塞空条目。
        /// </summary>
        private static List<string> OpsOf(JsonElement[] ops)
        {
            var list = new List<string>();
            if (ops == null) return list;

            foreach (var o in ops)
            {
                if (o.ValueKind != JsonValueKind.Object) continue;

                JsonElement e;
                if (!o.TryGetProperty("op", out e) || e.ValueKind != JsonValueKind.String) continue;

                var op = (e.GetString() ?? "").Trim().ToLowerInvariant();
                if (!AssemblyManual.Has(op)) continue;
                if (list.Contains(op)) continue;
                list.Add(op);
            }
            return list;
        }

        /// <summary>
        /// 按需注入:本批新出现的 op 才发手册(每个 op 一次),之后只留一行指针。
        /// 没有新内容时 opDocs 置 null,序列化为 "opDocs":null,不占实质空间。
        /// </summary>
        private static void TakeDocs(List<string> docOps,
            out Dictionary<string, string> opDocs, out string opDocsNote)
        {
            opDocs = OpDocRegistry.TakeUnsent(AssemblyManual.Domain, AssemblyManual.Entries, docOps);
            opDocsNote = OpDocRegistry.Note(docOps, opDocs.Keys, AssemblyManual.ArgName);
            if (opDocs.Count == 0) opDocs = null;
        }

        // ================= 各 op 执行 =================

        private static object ExecOp(SolidEdgeContext context, object doc,
            object occurrences, object relations, AssemblySpec.AssemblyOp op)
        {
            switch (op.Op)
            {
                case "place": return OpPlace(context, doc, occurrences, op);
                case "move": return OpMove(context, occurrences, op);
                case "rotate": return OpRotate(context, occurrences, op);
                case "transform": return OpTransform(context, occurrences, op);
                case "origin": return OpOrigin(context, occurrences, op);
                case "visible": return OpVisible(context, occurrences, op);
                case "ground": return OpGround(context, doc, relations, op);
                case "delete": return OpDelete(context, occurrences, op);
                case "replace": return OpReplace(context, occurrences, op);
                case "reorder": return OpReorder(context, occurrences, op);
                case "color": return OpColor(context, doc, occurrences, op);
                case "constrain": return OpConstrain(context, doc, relations, op);
                case "rebind": return OpRebind(context, doc, relations, op);
                default: return Err(op.Index, op.Op, "内部错误:op " + op.Op + " 未分发。");
            }
        }

        // ---- place ----

        private static object OpPlace(SolidEdgeContext context, object doc, object occurrences, AssemblySpec.AssemblyOp op)
        {
            int before = GetCount(occurrences);
            if (before < 0) return Err(op.Index, "place", "读不到 Occurrences.Count。");

            object occ;
            var occs = occurrences as SolidEdgeAssembly.Occurrences;
            if (!string.IsNullOrEmpty(op.FamilyMember))
            {
                // 零部件族成员:恒落原点(来源项目实测),有位置就后补 PutTransform
                occ = occs != null
                    ? occs.AddFamilyByFilename(op.File, op.FamilyMember)
                    : AssemblySpec.Call(occurrences, "AddFamilyByFilename", new object[] { op.File, op.FamilyMember });
                if (op.Position != null)
                    TransformOcc(occ, op.Position, op.Angles);
            }
            else if (op.Matrix != null && op.Matrix.Length == 16)
            {
                // 官方写法:强类型 + Array.CreateInstance + ref(AddWithMatrix.html C# 示例)
                Array m = Array.CreateInstance(typeof(double), 16);
                for (int k = 0; k < 16; k++) m.SetValue(op.Matrix[k], k);
                occ = occs != null
                    ? occs.AddWithMatrix(op.File, ref m)
                    : AssemblySpec.Call(occurrences, "AddWithMatrix", new object[] { op.File, op.Matrix });
            }
            else if (op.Position != null)
            {
                // AddWithTransform 角度参数为弧度(来源项目实测;文档未明示单位,以实测为准)
                double rx = op.Angles != null ? DegToRad(op.Angles[0]) : 0;
                double ry = op.Angles != null ? DegToRad(op.Angles[1]) : 0;
                double rz = op.Angles != null ? DegToRad(op.Angles[2]) : 0;
                occ = occs != null
                    ? occs.AddWithTransform(op.File, op.Position[0], op.Position[1], op.Position[2], rx, ry, rz)
                    : AssemblySpec.Call(occurrences, "AddWithTransform",
                        new object[] { op.File, op.Position[0], op.Position[1], op.Position[2], rx, ry, rz });
            }
            else
            {
                occ = occs != null
                    ? occs.AddByFilename(op.File)
                    : AssemblySpec.Call(occurrences, "AddByFilename", new object[] { op.File });
            }

            // 验证:Count 增长 1
            int after = GetCount(occurrences);
            if (after != before + 1)
                return Err(op.Index, "place", "放置后 Occurrences.Count=" + after + "(期望 " + (before + 1) + "),放置未生效。");

            string name = Convert.ToString(AssemblySpec.Get(occ, "Name", "(未命名)"), CultureInfo.InvariantCulture);
            string handle = context.AddHandle(occ, "Occurrence", name);
            double[] matrix = AssemblySpec.ReadMatrix(occ);
            var result = new Dictionary<string, object>
            {
                ["op"] = "place", ["status"] = "ok",
                ["index"] = after - 1, ["name"] = name, ["objectId"] = handle,
                ["matrix"] = matrix
            };

            // ground:false = 放置后立即解固。AddByFilename 默认带 GroundRelation3d(固定),
            // 留着它以后加约束必失效(2026-09-22 用户实测反馈)——要"只摆位不固定"就传 ground:false。
            if (!op.PlaceGround)
            {
                string deletedName;
                bool removed = AssemblySpec.TryUnground(AssemblySpec.Get(doc, "Relations3d"), name, out deletedName);
                result["ground"] = false;
                result["auto_ungrounded"] = removed;
                if (!removed)
                    result["note"] = "ground=false 但没找到默认 Ground(可能 SE 版本行为差异),零件本就未被固定。";
            }
            else
            {
                result["note"] = "★ 零件默认带 GroundRelation3d(固定):要加约束请直接下 constrain op"
                    + "(会自动解固本零件);只想摆位不想固定可传 ground:false。";
            }
            return result;
        }

        // ---- move / rotate / transform / origin ----

        private static object OpMove(SolidEdgeContext context, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "move");
            if (occ is string err) return Err(op.Index, "move", err);

            double[] before = AssemblySpec.ReadMatrix(occ);
            // ★ 2026-09-22 本机实测(装配5,SE2022):Occurrence.Move(dx,dy,dz) 稳定 E_FAIL(0x80004005,
            //   5 次交叉验证:自由件/带关系件/保存前后/不同参数全灭),与远端线实测成功矛盾(同 extrude PIA
            //   两线矛盾同款)。改走 PutOrigin 通道(origin op 已实测 rotation_preserved,矩阵回读精确)。
            var o = (SolidEdgeAssembly.Occurrence)occ;
            if (before == null)
                return Err(op.Index, "move", "移动前矩阵回读失败,无法计算目标位置(拒绝盲移)。");
            o.PutOrigin(before[12] + op.Delta[0], before[13] + op.Delta[1], before[14] + op.Delta[2]);

            double[] after = AssemblySpec.ReadMatrix(occ);
            if (after == null)
                return Warn(op.Index, "move", "矩阵回读失败,无法核对平移结果(调用本身未报错)。",
                    new Dictionary<string, object> { ["op"] = "move", ["delta"] = op.Delta, ["verify"] = "not-verified" });
            bool moved = Math.Abs((after[12] - before[12]) - op.Delta[0]) < 1e-9
                      && Math.Abs((after[13] - before[13]) - op.Delta[1]) < 1e-9
                      && Math.Abs((after[14] - before[14]) - op.Delta[2]) < 1e-9;
            if (!moved)
                return Err(op.Index, "move", "Move 后矩阵平移分量与 delta 不符(零件可能是固定件——固定件不接受移动,先 ground value=false)。");
            bool rotKept = SameMatrix(Slice(before, 0, 12), Slice(after, 0, 12));
            return Ok("move", new Dictionary<string, object> { ["delta"] = op.Delta, ["position"] = Slice(after, 12, 3), ["rotation_preserved"] = rotKept });
        }

        private static object OpRotate(SolidEdgeContext context, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "rotate");
            if (occ is string err) return Err(op.Index, "rotate", err);

            double[] before = AssemblySpec.ReadMatrix(occ);
            // 官方:Angle 单位弧度(SolidEdgeAssembly~Occurrence~Rotate.html Parameters)
            var o = (SolidEdgeAssembly.Occurrence)occ;
            // ★ 2026-09-22 本机实测(装配5,SE2022):Occurrence.Rotate(7参) 稳定 E_FAIL(0x80004005),
            //   与 move 同款(move 已改走 PutOrigin 绕开)。这里保留先试 Rotate——远端线环境实测成功;
            //   失败时明确报"本机通道不可用"并指路 transform,不再掉进通用 catch 变成一条
            //   没有指路意义的 "执行异常: COMException hr=0x80004005"(日志里 4 条失败全由此而来)。
            try
            {
                o.Rotate(op.Axis1[0], op.Axis1[1], op.Axis1[2],
                         op.Axis2[0], op.Axis2[1], op.Axis2[2], DegToRad(op.Degrees));
            }
            catch (COMException)
            {
                return Err(op.Index, "rotate",
                    "Occurrence.Rotate 在本机 SE2022 通道不可用(E_FAIL)。请改用 transform op(2026-09-24 冒烟实测可用):"
                    + " {\"op\":\"transform\",\"component\":N,\"origin\":[x,y,z],\"angles\":[rx,ry,rz]},"
                    + "angles 单位度、是绝对姿态不是增量;先 se_assembly_query 读当前矩阵算出目标姿态。");
            }

            double[] after = AssemblySpec.ReadMatrix(occ);
            if (before == null || after == null)
                return Warn(op.Index, "rotate", "矩阵回读失败,无法核对旋转结果。",
                    new Dictionary<string, object> { ["op"] = "rotate", ["degrees"] = op.Degrees, ["verify"] = "not-verified" });
            bool changed = !SameMatrix(before, after);
            if (!changed)
                return Err(op.Index, "rotate", "Rotate 后矩阵未变化——零件可能是固定件或轴两点重合。");
            return Ok("rotate", new Dictionary<string, object>
            {
                ["degrees"] = op.Degrees,
                ["position"] = Slice(after, 12, 3)
            });
        }

        private static object OpTransform(SolidEdgeContext context, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "transform");
            if (occ is string err) return Err(op.Index, "transform", err);

            // 矩阵入口:origin+angles 的 Euler 组合表达不了斜置姿态(本机 Rotate 还 E_FAIL),
            // 而矩阵是 SE 原生姿态表示——16 元素行主序,平移在 [12..14](米),直接 PutMatrix。
            if (op.Matrix != null && op.Matrix.Length == 16)
            {
                var occTyped = (SolidEdgeAssembly.Occurrence)occ;
                Array mat = Array.CreateInstance(typeof(double), 16);
                for (int k = 0; k < 16; k++) mat.SetValue(op.Matrix[k], k);
                occTyped.PutMatrix(ref mat, true);      // Replace=true:给的是绝对姿态

                double[] afterM = AssemblySpec.ReadMatrix(occ);
                if (afterM == null)
                    return Warn(op.Index, "transform", "矩阵回读失败,无法核对结果。",
                        new Dictionary<string, object> { ["op"] = "transform", ["verify"] = "not-verified" });
                // 容差放 1e-9(千分之一微米):PutMatrix 回读会有末位归一化误差,1e-12 会误报。
                bool sameEnough = true;
                for (int k = 0; k < 16; k++)
                {
                    if (Math.Abs(op.Matrix[k] - afterM[k]) > 1e-9) { sameEnough = false; break; }
                }
                if (!sameEnough)
                {
                    // 回读值原样吐出来,调用方一眼能看出是"没动"还是"被归一化/转了置"。
                    var parts = new string[16];
                    for (int k = 0; k < 16; k++)
                        parts[k] = Math.Round(afterM[k], 6).ToString(CultureInfo.InvariantCulture);
                    return Err(op.Index, "transform",
                        "PutMatrix 后回读矩阵与给定矩阵不符(零件可能是固定件;或矩阵不是行主序)。回读值: "
                        + string.Join(",", parts));
                }
                return Ok("transform", new Dictionary<string, object>
                {
                    ["mode"] = "matrix",
                    ["position"] = Slice(afterM, 12, 3),
                    ["verified"] = true
                });
            }

            TransformOcc(occ, op.Position, op.Angles);
            double[] after = AssemblySpec.ReadMatrix(occ);
            if (after == null)
                return Warn(op.Index, "transform", "矩阵回读失败,无法核对结果。",
                    new Dictionary<string, object> { ["op"] = "transform", ["verify"] = "not-verified" });
            bool posOk = Math.Abs(after[12] - op.Position[0]) < 1e-9
                      && Math.Abs(after[13] - op.Position[1]) < 1e-9
                      && Math.Abs(after[14] - op.Position[2]) < 1e-9;
            if (!posOk)
                return Err(op.Index, "transform", "PutTransform 后矩阵平移分量与 origin 不符(零件可能是固定件)。");
            return Ok("transform", new Dictionary<string, object>
            {
                ["origin"] = op.Position,
                ["angles_degrees"] = op.Angles ?? new double[] { 0, 0, 0 },
                ["position"] = Slice(after, 12, 3)
            });
        }

        private static object OpOrigin(SolidEdgeContext context, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "origin");
            if (occ is string err) return Err(op.Index, "origin", err);

            double[] before = AssemblySpec.ReadMatrix(occ);
            var o = (SolidEdgeAssembly.Occurrence)occ;
            o.PutOrigin(op.Position[0], op.Position[1], op.Position[2]);

            double[] after = AssemblySpec.ReadMatrix(occ);
            if (after == null)
                return Warn(op.Index, "origin", "矩阵回读失败,无法核对结果。",
                    new Dictionary<string, object> { ["op"] = "origin", ["verify"] = "not-verified" });
            bool posOk = Math.Abs(after[12] - op.Position[0]) < 1e-9
                      && Math.Abs(after[13] - op.Position[1]) < 1e-9
                      && Math.Abs(after[14] - op.Position[2]) < 1e-9;
            if (!posOk)
                return Err(op.Index, "origin", "PutOrigin 后矩阵平移分量与 position 不符(零件可能是固定件)。");
            bool rotKept = before == null || (SameMatrix(Slice(before, 0, 12), Slice(after, 0, 12)));
            return Ok("origin", new Dictionary<string, object>
            {
                ["position"] = op.Position,
                ["rotation_preserved"] = rotKept,
                ["position_readback"] = Slice(after, 12, 3)
            });
        }

        private static void TransformOcc(object occ, double[] origin, double[] anglesDeg)
        {
            double rx = anglesDeg != null ? DegToRad(anglesDeg[0]) : 0;
            double ry = anglesDeg != null ? DegToRad(anglesDeg[1]) : 0;
            double rz = anglesDeg != null ? DegToRad(anglesDeg[2]) : 0;
            var o = (SolidEdgeAssembly.Occurrence)occ;
            o.PutTransform(origin[0], origin[1], origin[2], rx, ry, rz);
        }

        // ---- visible / suppress / color ----

        private static object OpVisible(SolidEdgeContext context, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "visible");
            if (occ is string err) return Err(op.Index, "visible", err);
            AssemblySpec.Put(occ, "Visible", op.BoolValue);
            object rb = AssemblySpec.Get(occ, "Visible");
            bool ok = rb != null && Convert.ToBoolean(rb, CultureInfo.InvariantCulture) == op.BoolValue;
            if (!ok) return Err(op.Index, "visible", "Visible 写入后回读不一致。");
            return Ok("visible", new Dictionary<string, object> { ["value"] = op.BoolValue });
        }

        // suppress op 已移入诚实拒绝清单(SE2022 三条封送通道实测全灭,见 AssemblySpec.RefusedOps)

        private static object OpColor(SolidEdgeContext context, object doc, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "color");
            if (occ is string err) return Err(op.Index, "color", err);

            object styles = AssemblySpec.Get(doc, "FaceStyles");
            if (styles == null) return Err(op.Index, "color", "装配文档没有 FaceStyles 集合,无法上色。");

            // 来源项目实测:Occurrence 无 Color/SetColor,上色=给它一个 diffuse 颜色匹配的 FaceStyle
            string name = "MCP " + op.Rgb[0].ToString("X2") + op.Rgb[1].ToString("X2") + op.Rgb[2].ToString("X2");
            object style = null;
            try { style = AssemblySpec.Call(styles, "Item", new object[] { name }); } catch { }
            if (style == null)
                style = AssemblySpec.Call(styles, "Add", new object[] { name, "" });   // Parent 传空串可用(逐面着色实测)
            AssemblySpec.Call(style, "SetDiffuse", new object[]
            {
                op.Rgb[0] / 255.0, op.Rgb[1] / 255.0, op.Rgb[2] / 255.0
            });
            AssemblySpec.Put(occ, "FaceStyle", style);

            object rb = AssemblySpec.Get(occ, "FaceStyle");
            string rbName = rb == null ? null : Convert.ToString(AssemblySpec.Get(rb, "Name"), CultureInfo.InvariantCulture);
            // 实测(2026-09-22):FaceStyle 写入成功但 SE 回读 FaceStyle.Name 常给空串——回读空串不算失败,
            // 只标 not-verified;名字对得上才算 ok。
            string verify = rbName == name ? "ok"
                : string.IsNullOrEmpty(rbName) ? "not-verified(SE 回吐的 FaceStyle.Name 为空,颜色可能已生效,可在 UI 目检)"
                : "style 回读名不一致(" + rbName + ")";
            var colorResult = Ok("color", new Dictionary<string, object>
            {
                ["rgb"] = op.Rgb, ["style"] = name, ["verify"] = verify
            });
            if (verify == "style 回读名不一致(" + rbName + ")")
                colorResult["status"] = "error";
            return colorResult;
        }

        // ---- ground / delete / replace / reorder ----

        private static object OpGround(SolidEdgeContext context, object doc, object relations, AssemblySpec.AssemblyOp op)
        {
            object occurrences = AssemblySpec.Get(doc, "Occurrences");
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "ground");
            if (occ is string err) return Err(op.Index, "ground", err);
            bool ground = !op.HasBool || op.BoolValue;
            string occName = Convert.ToString(AssemblySpec.Get(occ, "Name", ""), CultureInfo.InvariantCulture);

            var rels = relations as SolidEdgeAssembly.Relations3d;
            int before = GetCount(relations);

            if (ground)
            {
                if (rels != null) rels.AddGround((SolidEdgeAssembly.Occurrence)occ);
                else AssemblySpec.Call(relations, "AddGround", new object[] { occ });
                int after = GetCount(relations);
                if (after != before + 1)
                    return Err(op.Index, "ground", "AddGround 后 Relations3d.Count=" + after + "(期望 " + (before + 1) + ")。");
                return Ok("ground", new Dictionary<string, object> { ["component"] = occName, ["grounded"] = true });
            }

            // unground:遍历找 Ground 关系且 Occurrence 匹配的,删除(公共辅助,constrain 自动解固同款)
            string deleted;
            AssemblySpec.TryUnground(relations, occName, out deleted);
            if (deleted == null)
                return Ok("ground", new Dictionary<string, object>
                {
                    ["component"] = occName, ["grounded"] = false,
                    ["note"] = "没找到该零件的 Ground 关系——可能已解除过(幂等跳过,继续后续 op)。"
                });
            int afterUnground = GetCount(relations);
            return Ok("ground", new Dictionary<string, object>
            {
                ["component"] = occName, ["grounded"] = false,
                ["deleted_relation"] = deleted,
                ["relations"] = afterUnground
            });
        }

        private static object OpDelete(SolidEdgeContext context, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "delete");
            if (occ is string err) return Err(op.Index, "delete", err);
            string name = Convert.ToString(AssemblySpec.Get(occ, "Name", ""), CultureInfo.InvariantCulture);
            int before = GetCount(occurrences);

            if (occ is SolidEdgeAssembly.Occurrence oo) oo.Delete();
            else AssemblySpec.Call(occ, "Delete", null);

            int after = GetCount(occurrences);
            if (after != before - 1)
                return Err(op.Index, "delete", "Delete 后 Occurrences.Count=" + after + "(期望 " + (before - 1) + "),删除未生效。");
            return Ok("delete", new Dictionary<string, object> { ["name"] = name, ["occurrences"] = after });
        }

        private static object OpReplace(SolidEdgeContext context, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "replace");
            if (occ is string err) return Err(op.Index, "replace", err);
            string oldFile = Convert.ToString(AssemblySpec.Get(occ, "OccurrenceFileName", ""), CultureInfo.InvariantCulture);
            int newBefore = CountByFile(occurrences, op.File);
            int oldBefore = CountByFile(occurrences, oldFile);

            // 官方签名:Replace(NewOccurrenceFileName As String, ReplaceAll As Boolean, [NewFamilyMemberName])
            // 必须传两参(来源项目实测:只传一参会抛)
            AssemblySpec.Call(occ, "Replace", new object[] { op.File, op.All });

            // ★ 2026-09-22 本机实测(装配5):Replace 后旧 Occurrence RCW 即失效,对旧对象回读
            //   OccurrenceFileName 永远是旧值——旧验证逻辑因此误报"不匹配"而实际已替换成功。
            //   正确核对:重新枚举集合,新文件实例数 +1 且旧文件实例数 -1。
            int newAfter = CountByFile(occurrences, op.File);
            int oldAfter = CountByFile(occurrences, oldFile);
            bool ok = newAfter == newBefore + 1 && (oldAfter == oldBefore - 1 || oldFile.Equals(op.File, StringComparison.OrdinalIgnoreCase));
            if (!ok)
                return Err(op.Index, "replace", "Replace 后集合核对不符:新文件 \"" + op.File + "\" 实例 " + newBefore + "→" + newAfter
                    + ",旧文件实例 " + oldBefore + "→" + oldAfter + "(期望新 +1 旧 -1)。");
            return Ok("replace", new Dictionary<string, object>
            {
                ["old_file"] = oldFile, ["new_file"] = op.File, ["all"] = op.All,
                ["new_count"] = newAfter
            });
        }

        /// <summary>统计 occurrences 里 OccurrenceFileName 等于给定文件(全路径不敏感比较)的实例数。</summary>
        private static int CountByFile(object occurrences, string file)
        {
            int count = GetCount(occurrences);
            int hit = 0;
            for (int i = 1; i <= count; i++)
            {
                object occ = AssemblySpec.Call(occurrences, "Item", new object[] { i });
                string f = Convert.ToString(AssemblySpec.Get(occ, "OccurrenceFileName", ""), CultureInfo.InvariantCulture);
                if (string.Equals(f, file, StringComparison.OrdinalIgnoreCase))
                    hit++;
            }
            return hit;
        }

        private static object OpReorder(SolidEdgeContext context, object occurrences, AssemblySpec.AssemblyOp op)
        {
            object occ = Resolve(context, occurrences, op.CompRef, op.Index, "reorder");
            if (occ is string err) return Err(op.Index, "reorder", err);
            object target = Resolve(context, occurrences, op.TargetRef, op.Index, "reorder", "target");
            if (target is string err2) return Err(op.Index, "reorder", err2);

            string[] before = ReadNames(occurrences);
            var occs = occurrences as SolidEdgeAssembly.Occurrences;
            // 官方:第二参是目标 occurrence 对象,不是索引(离线文档核实)
            if (occs != null)
                occs.ReorderOccurrence(occ, target, op.After);
            else
                AssemblySpec.Call(occurrences, "ReorderOccurrence", new object[] { occ, target, op.After });

            string[] after = ReadNames(occurrences);
            bool changed = !SameSeq(before, after);
            return Ok("reorder", new Dictionary<string, object>
            {
                ["after"] = op.After,
                ["order_changed"] = changed,
                ["order"] = after,
                ["verify"] = changed ? "ok" : "顺序未变化——可能已在目标位(target=自身或相邻同位)"
            });
        }

        // ---- constrain(planar / axial) ----

        private static object OpConstrain(SolidEdgeContext context, object doc, object relations, AssemblySpec.AssemblyOp op)
        {
            object occurrences = AssemblySpec.Get(doc, "Occurrences");
            object occ1 = Resolve(context, occurrences, op.CompRef, op.Index, "constrain", "component1");
            if (occ1 is string err1) return Err(op.Index, "constrain", err1);
            object occ2 = Resolve(context, occurrences, op.Comp2Ref, op.Index, "constrain", "component2");
            if (occ2 is string err2) return Err(op.Index, "constrain", err2);

            // ★ 自动解固 component1(2026-09-22 用户反馈:带固定约束的零件再加约束不生效)。
            // 求解时 SE 挪的是 component1(实测语义),component2 视为基准件、其 Ground 不碰。
            // keepGround=true 显式跳过——此时若 component1 确实带固定,结果里给 warning 说清后果。
            string occ1Name = Convert.ToString(AssemblySpec.Get(occ1, "Name", ""), CultureInfo.InvariantCulture);
            bool comp1Grounded = AssemblySpec.HasGround(relations, occ1Name);
            string autoUngrounded = null;
            if (!op.KeepGround && comp1Grounded)
            {
                string deletedName;
                if (AssemblySpec.TryUnground(relations, occ1Name, out deletedName))
                    autoUngrounded = deletedName;
            }

            int before = GetCount(relations);

            // 约束必须用 CreateReference 包裹零件面(直传零件文档的面报 0x80040225,来源项目实测)
            object bind1 = BuildFaceBinding(context, doc, occ1, op.Face1Ref, op.ConstrainType == "axial", op.Index, "face1");
            if (bind1 is string e1) return Err(op.Index, "constrain", e1);
            object bind2 = BuildFaceBinding(context, doc, occ2, op.Face2Ref, op.ConstrainType == "axial", op.Index, "face2");
            if (bind2 is string e2) return Err(op.Index, "constrain", e2);
            var r1 = (FaceBinding)bind1;
            var r2 = (FaceBinding)bind2;

            var rels = relations as SolidEdgeAssembly.Relations3d;
            object relation;
            if (op.ConstrainType == "planar")
            {
                // AddPlanar(Plane1, Plane2, NormalsAligned, CP1(), CP2())
                // NormalsAligned=true=mated 贴合 / false=aligned 共面同向(离线文档 Parameters + 合页实测)
                if (rels != null)
                {
                    Array cp1 = ToArray(r1.MidM);
                    Array cp2 = ToArray(r2.MidM);
                    relation = rels.AddPlanar(r1.Reference, r2.Reference, op.Mate, ref cp1, ref cp2);
                }
                else
                {
                    relation = AssemblySpec.Call(relations, "AddPlanar",
                        new object[] { r1.Reference, r2.Reference, op.Mate, r1.MidM, r2.MidM });
                }
            }
            else
            {
                // AddAxial(Axis1, Axis2, NormalsAligned)——两面须圆柱面(构建 Reference 时已校验)
                if (rels != null)
                    relation = rels.AddAxial(r1.Reference, r2.Reference, op.Mate);
                else
                    relation = AssemblySpec.Call(relations, "AddAxial", new object[] { r1.Reference, r2.Reference, op.Mate });
            }

            int after = GetCount(relations);
            if (after != before + 1)
                return Err(op.Index, "constrain", op.ConstrainType + " 约束后 Relations3d.Count=" + after + "(期望 " + (before + 1) + "),约束未建成。");

            // Status 回读(L2 实测:全 1 即求解成功;0/5=冲突固死——多半是没删 Ground 或 mate 翻转)。
            // ★ SE2022 实测(2026-09-22):建完约束立刻读 Status=1 但零件还没挪——求解滞后,
            //   必须先 doc.UpdateAll() 再读 Status/矩阵,否则"已求解"是假的。
            try { AssemblySpec.Call(doc, "UpdateAll", null); } catch { }
            object statusObj = AssemblySpec.Get(relation, "Status");
            long status = -1;
            if (statusObj != null) long.TryParse(Convert.ToString(statusObj, CultureInfo.InvariantCulture), out status);
            var extra = new Dictionary<string, object>
            {
                ["type"] = op.ConstrainType,
                ["mate"] = op.Mate,
                ["relations"] = after,
                // 实际绑到哪张面:序号会随特征增删漂移,回显是为了"绑错了至少看得见"
                ["face1_bound"] = BindingEcho(r1),
                ["face2_bound"] = BindingEcho(r2)
            };
            if (autoUngrounded != null)
                extra["auto_ungrounded"] = autoUngrounded;   // 已自动删掉 component1 的固定,求解才会真挪零件
            if (op.KeepGround && comp1Grounded)
                extra["warning"] = "component1(" + occ1Name + ")带固定约束且 keepGround=true:求解器不会挪这个零件,"
                    + "约束只可能挪 component2 或无法求解;确认这是你要的,否则去掉 keepGround 重跑。";
            if (statusObj != null)
            {
                // 键名用 relation_status:不能叫 status——会覆盖结果里 status="ok" 的约定键
                extra["relation_status"] = status;
                if (status != RelationStatusOk)
                {
                    extra["diagnosis"] = "relation_status=" + status + "(正常=" + RelationStatusOk + ")。常见原因:①零件还带 Ground 固定关系"
                        + "(先 ground value=false);②planar 的 mate=true 触发 180° 翻转与已有轴向约束冲突(改 mate=false);③两面不满足几何条件。";
                    // 求解失败时也把"实际绑到哪张面"带出来 —— 否则分不清是几何冲突还是绑错面
                    var errDict = Err(op.Index, "constrain", "约束已创建但未求解: " + extra["diagnosis"]);
                    errDict["face1_bound"] = extra["face1_bound"];
                    errDict["face2_bound"] = extra["face2_bound"];
                    return errDict;
                }
            }
            object offset = AssemblySpec.Get(relation, "Offset");
            if (offset != null) extra["offset"] = offset;
            // 回传 component1 约束后的矩阵:求解成功时 SE 会挪零件,矩阵位移是"真求解"的回读级证据
            double[] occMatrix = AssemblySpec.ReadMatrix(occ1);
            if (occMatrix != null) extra["component1_position"] = new[] { occMatrix[12], occMatrix[13], occMatrix[14] };
            return Ok("constrain", extra);
        }

        /// <summary>
        /// 一次约束绑面的结果:Reference + 约束点 + 这张面的"身份证"(回显给调用方核对)。
        /// </summary>
        private sealed class FaceBinding
        {
            public object Reference;
            public double[] MidM;          // 约束点(米)
            public string Source;          // index / selector / handle / geometry(rebind 按几何认面)
            public FaceInfo Info;          // 绑到的面(序号/ID/类型/直径/位置)
            public string Note;            // 需要告诉调用方的补充说明(如"多张等价面,已取第一张")
        }

        /// <summary>
        /// 取零件面并包成装配 Reference。
        /// 链路:occ → OccurrenceDocument → Models.Item(1) → Body 全量面表(FaceFinder,与 se_find_objects 同一条链)
        /// → 按序号/描述符/句柄定位 → doc.CreateReference(occ, face);约束点 = 面包围盒中点(米)。
        ///
        /// 为什么不再只用序号(2026-10-25 P1):零件加/删特征后 Body.Faces 序号整体漂移,
        /// 同一条装配配方重跑会**静默绑到另一张面**上。描述符按几何认面,不随序号漂移;
        /// 不管走哪条路,返回值都回显"实际绑到了哪张面",让漂移至少可见。
        /// </summary>
        private static object BuildFaceBinding(SolidEdgeContext context, object doc, object occ, object faceRef,
            bool needCylindrical, int opIndex, string field)
        {
            object part = AssemblySpec.Get(occ, "OccurrenceDocument");
            if (part == null) return "取不到 OccurrenceDocument。";
            object models = AssemblySpec.Get(part, "Models");
            int modelCount = GetCount(models);
            if (modelCount < 1) return "零件文档没有 Models(无实体),取不了面。";
            object model = AssemblySpec.Call(models, "Item", new object[] { 1 });
            object body = AssemblySpec.Get(model, "Body");

            string err;
            List<ModelStateTools.FaceRow> rows = FaceFinder.ReadAllFaces(body, out err);
            if (err != null) return err;

            var binding = new FaceBinding();
            FaceInfo hit = null;

            if (faceRef is int index)
            {
                if (index < 0 || index >= rows.Count)
                    return field + "=" + index + " 越界:该零件 Body 有 " + rows.Count + " 个面(0-based)。"
                        + "（序号会随特征增删漂移,建议改用描述符,如 " + field + ":{\"type\":\"cylinder\",\"diaMm\":89}）";
                hit = FaceFinder.ByIndex(rows, index);
                if (hit == null) return field + "=" + index + " 这张面的包围盒读不到,给不出约束点。";
                binding.Source = "index";
            }
            else if (faceRef is string handle && handle.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                var h = context.GetHandle(handle);
                if (h == null || h.ComObject == null)
                    return "面句柄 " + handle + " 无效或已失效(句柄表会随文档切换/选择刷新),请重新用 se_find_objects 取。";
                hit = FaceFinder.ByComFace(rows, h.ComObject);
                if (hit == null)
                    return "面句柄 " + handle + " 指向的面不在这个零件的 Body 里(可能来自别的文档或已过期),"
                        + "请用 se_find_objects 重新取,或改用描述符。";
                binding.Source = "handle";
            }
            else if (faceRef is FaceSelector sel)
            {
                int scanned;
                List<FaceInfo> hits = FaceFinder.Filter(rows, sel, out scanned);
                if (hits.Count == 0)
                    return field + " 描述符没命中任何面(" + sel.Describe() + ")。扫了 " + scanned + " 张面,"
                        + "这个零件的面清单: " + DescribeCandidates(FaceFinder.Filter(rows, new FaceSelector(), out scanned), 12)
                        + " → 按清单改 diaMm/atMm/normal,或放宽 tolMm(当前 " + sel.TolMm + "mm)。";
                if (hits.Count > 1)
                    return field + " 描述符命中了 " + hits.Count + " 张面(" + sel.Describe() + "),装配约束必须唯一命中:"
                        + DescribeCandidates(hits, 8)
                        + " → 加 atMm(包围盒中心)/lenMm(轴向长度)缩小范围,或用 se_find_objects 拿 handle 后传 \"obj-N\"。";
                hit = hits[0];
                binding.Source = "selector";
            }
            else
            {
                return field + " 引用无效(要 0-based 序号 / 描述符 / 面句柄)。";
            }

            object face = hit.ComFace;
            if (needCylindrical && !string.Equals(hit.Type, "Cylinder", StringComparison.OrdinalIgnoreCase))
                return field + " 绑到的面是 " + hit.Type + "(面 " + hit.Index + " / FaceID " + hit.FaceId + "),"
                    + "axial 约束两面都必须是圆柱面(来源项目与合页实测一致)。";

            // 约束点:面包围盒中点。官方写法:强类型 Face.GetRange(ref min, ref max)
            double[] mid = hit.CenterM;
            if (mid == null)
            {
                try
                {
                    var f = (SolidEdgeGeometry.Face)face;
                    Array minPt = Array.CreateInstance(typeof(double), 0);
                    Array maxPt = Array.CreateInstance(typeof(double), 0);
                    f.GetRange(ref minPt, ref maxPt);
                    if (minPt.Length >= 3 && maxPt.Length >= 3)
                    {
                        mid = new[]
                        {
                            (Convert.ToDouble(minPt.GetValue(0), CultureInfo.InvariantCulture) + Convert.ToDouble(maxPt.GetValue(0), CultureInfo.InvariantCulture)) / 2,
                            (Convert.ToDouble(minPt.GetValue(1), CultureInfo.InvariantCulture) + Convert.ToDouble(maxPt.GetValue(1), CultureInfo.InvariantCulture)) / 2,
                            (Convert.ToDouble(minPt.GetValue(2), CultureInfo.InvariantCulture) + Convert.ToDouble(maxPt.GetValue(2), CultureInfo.InvariantCulture)) / 2
                        };
                    }
                }
                catch { }
            }
            if (mid == null) return "面包围盒(GetRange)读不到,给不出约束点。";

            object reference = AssemblySpec.Call(doc, "CreateReference", new object[] { occ, face });
            if (reference == null) return "CreateReference 返回空。";
            binding.Reference = reference;
            binding.MidM = mid;
            binding.Info = hit;
            return binding;
        }

        /// <summary>绑面回显:实际绑的是哪张面 + 怎么定位的 + 约束点(mm)。</summary>
        private static Dictionary<string, object> BindingEcho(FaceBinding b)
        {
            var d = (b.Info != null) ? b.Info.ToEcho() : new Dictionary<string, object>();
            d["source"] = b.Source;
            if (!string.IsNullOrEmpty(b.Note)) d["note"] = b.Note;
            if (b.MidM != null)
            {
                d["pointMm"] = new[]
                {
                    Math.Round(b.MidM[0] * 1000.0, 3),
                    Math.Round(b.MidM[1] * 1000.0, 3),
                    Math.Round(b.MidM[2] * 1000.0, 3)
                };
            }
            return d;
        }

        /// <summary>"face1 命中了 3 张面: [0] cylinder dia=89 at=0,0,10 …" —— 让调用方一眼看出该加什么条件。</summary>
        private static string DescribeCandidates(List<FaceInfo> faces, int max)
        {
            if (faces == null || faces.Count == 0) return "(空)";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < faces.Count && i < max; i++)
            {
                FaceInfo f = faces[i];
                sb.Append(i == 0 ? "" : " | ");
                sb.Append("[").Append(f.Index).Append("] ").Append(f.Type);
                if (f.DiaMm.HasValue) sb.Append(" dia=").Append(Math.Round(f.DiaMm.Value, 2));
                if (f.CenterMm != null)
                    sb.Append(" at=").Append(FaceSelector.VectorText(f.CenterMm));
                if (f.Normal != null)
                    sb.Append(" n=").Append(FaceSelector.VectorText(f.Normal));
            }
            if (faces.Count > max) sb.Append(" …(共 ").Append(faces.Count).Append(" 张)");
            return sb.ToString();
        }

        // ================= 通用辅助 =================

        private static object ResolveDocument(SolidEdgeContext context, string objectId)
        {
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                var handle = context.GetHandle(objectId);
                return handle?.ComObject;
            }
            return context.GetApplication().ActiveDocument;
        }

        /// <summary>解析组件引用:int=0-based 序号 / "obj-N" 句柄 / 字符串=Name 或文件名匹配。失败返回 string 错误。</summary>
        private static object Resolve(SolidEdgeContext context, object occurrences, object compRef, int opIndex, string opName, string field = "component")
        {
            int count = GetCount(occurrences);
            if (count < 0) return "读不到 Occurrences.Count。";
            if (compRef is int idx)
            {
                if (idx < 0 || idx >= count) return field + "=" + idx + " 越界:共 " + count + " 个零件(0-based)。";
                return AssemblySpec.Call(occurrences, "Item", new object[] { idx + 1 });
            }
            var s = compRef as string;
            if (string.IsNullOrWhiteSpace(s)) return field + " 引用无效。";
            if (s.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                var handle = context.GetHandle(s);
                if (handle == null || handle.ComObject == null)
                    return "句柄 " + s + " 无效或已失效(句柄表随 se_get_selection 刷新)。";
                return handle.ComObject;
            }
            for (int i = 1; i <= count; i++)
            {
                object occ = AssemblySpec.Call(occurrences, "Item", new object[] { i });
                string name = Convert.ToString(AssemblySpec.Get(occ, "Name", ""), CultureInfo.InvariantCulture);
                string file = Convert.ToString(AssemblySpec.Get(occ, "OccurrenceFileName", ""), CultureInfo.InvariantCulture);
                if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return occ;
                if (!string.IsNullOrEmpty(file) &&
                    (string.Equals(file, s, StringComparison.OrdinalIgnoreCase) ||
                     file.EndsWith(s, StringComparison.OrdinalIgnoreCase) ||
                     name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                    return occ;
            }
            return "找不到零件 \"" + s + "\"(按 Name/OccurrenceFileName 匹配了 " + count + " 个零件)。";
        }

        private static double[] Slice(double[] m, int start, int len)
        {
            if (m == null || start + len > m.Length) return null;
            var r = new double[len];
            Array.Copy(m, start, r, 0, len);
            return r;
        }

        private static bool SameMatrix(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 1e-12) return false;
            return true;
        }

        private static bool SameSeq(string[] a, string[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static string[] ReadNames(object occurrences)
        {
            int count = GetCount(occurrences);
            if (count < 0) return null;
            var names = new string[count];
            for (int i = 1; i <= count; i++)
            {
                object occ = AssemblySpec.Call(occurrences, "Item", new object[] { i });
                names[i - 1] = Convert.ToString(AssemblySpec.Get(occ, "Name", "?" + i), CultureInfo.InvariantCulture);
            }
            return names;
        }

        private static int GetCount(object collection)
        {
            object c = AssemblySpec.Get(collection, "Count");
            if (c == null) return -1;
            try { return Convert.ToInt32(c, CultureInfo.InvariantCulture); }
            catch { return -1; }
        }

        private static Array ToArray(double[] values)
        {
            Array a = Array.CreateInstance(typeof(double), values.Length);
            for (int i = 0; i < values.Length; i++) a.SetValue(values[i], i);
            return a;
        }

        private static double DegToRad(double deg) { return deg * Math.PI / 180.0; }

        private static Dictionary<string, object> Ok(string op, Dictionary<string, object> extra)
        {
            var d = new Dictionary<string, object> { ["op"] = op, ["status"] = "ok" };
            if (extra != null)
                foreach (var kv in extra) d[kv.Key] = kv.Value;
            return d;
        }

        private static Dictionary<string, object> Warn(int index, string op, string message, Dictionary<string, object> extra)
        {
            var d = Ok(op, extra);
            d["index"] = index;
            d["warning"] = message;
            return d;
        }

        private static Dictionary<string, object> Err(int index, string op, string message)
        {
            return new Dictionary<string, object>
            {
                ["op"] = op, ["index"] = index, ["status"] = "error", ["message"] = message
            };
        }

        private static bool IsOk(object result)
        {
            var d = result as Dictionary<string, object>;
            return d != null && string.Equals(d["status"] as string, "ok", StringComparison.Ordinal);
        }

        private static bool IsErr(object result)
        {
            var d = result as Dictionary<string, object>;
            return d != null && string.Equals(d["status"] as string, "error", StringComparison.Ordinal);
        }

        private static string Summarize(AssemblySpec.AssemblyOp op)
        {
            string s = op.Op;
            if (!string.IsNullOrEmpty(op.File)) s += " file=" + op.File;
            if (op.CompRef != null) s += " comp=" + op.CompRef;
            if (op.Comp2Ref != null) s += " comp2=" + op.Comp2Ref;
            if (s.Length > 80) s = s.Substring(0, 80);
            return s;
        }
    }
}
