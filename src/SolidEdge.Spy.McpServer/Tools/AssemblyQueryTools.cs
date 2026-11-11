using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 装配只读查询:se_assembly_query —— 零件清单(含 16 元素矩阵)/ 约束清单 / BOM 统计。
    /// 纯只读,不走 Guardrail;配 se_assembly_build 使用:先 query 拿到零件序号与面数,再下 build op。
    /// </summary>
    [McpServerToolType]
    public static class AssemblyQueryTools
    {
        /// <summary>Relation3d.Type 枚举映射(来源项目 get_relation_info + SE 文档)。</summary>
        private static readonly Dictionary<int, string> RelationTypeNames = new Dictionary<int, string>
        {
            { 0, "Ground" }, { 1, "Axial" }, { 2, "Planar" }, { 3, "Connect" },
            { 4, "Angle" }, { 5, "Tangent" }, { 6, "Cam" }, { 7, "Gear" },
            { 8, "ParallelAxis" }, { 9, "Center" }
        };

        [McpServerTool, Description("装配只读查询(不改任何东西)。mode=\"occurrences\" 返回零件清单(0-based 序号、名称、源文件、显示状态、" +
		"16 元素变换矩阵行主序、平移在 [12..14],米);mode=\"relations\" 返回约束清单(类型、Status(1=求解成功)、Offset、法向对齐状态);" +
		"mode=\"bom\" 返回 BOM(按源文件聚合数量,含子装配递归,跳过 IncludeInBom=false,标注 IsPatternItem);mode=\"all\" 全返回。" +
		"典型流程:先查 occurrences 拿 0-based 序号与各零件 faceCount(constrain face1/face2 的序号上界),再下 build op。" +
		"只读模式下也可用。faceCount 三态:实测值 / 子装配与空零件=0 / 读失败=null(带 faceCountError,勿当 0 用)。\n" +
			"分工:本工具=全景清单/BOM/约束关系;按名称过滤找零件拿序号用 se_find_objects(collection=occurrences)。")]
        public static string se_assembly_query(
            SolidEdgeContext context,
            [Description("查询模式:occurrences / relations / bom / all,默认 occurrences")] string mode = "occurrences",
            [Description("目标装配文档句柄,可省略;省略时用当前活动文档")] string objectId = null)
        {
            string m = string.IsNullOrWhiteSpace(mode) ? "occurrences" : mode.Trim().ToLowerInvariant();
            if (m != "occurrences" && m != "relations" && m != "bom" && m != "all")
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "mode 只支持 occurrences / relations / bom / all,实际: " + mode
                });

            return context.Invoke(() =>
            {
                object doc = ResolveDocument(context, objectId);
                if (doc == null)
                    return JsonSerializer.Serialize(new { status = "error", message = "没有可用的文档:活动文档为空或句柄无效。" });
                if (!AssemblySpec.IsAssemblyDocument(doc))
                    return JsonSerializer.Serialize(new
                    {
                        status = "error",
                        message = "目标文档不是装配文档(Document.Type != 3=igAssemblyDocument),se_assembly_query 只查装配。"
                    });

                var result = new Dictionary<string, object> { ["status"] = "ok", ["mode"] = m };

                if (m == "occurrences" || m == "all")
                {
                    object occurrences = AssemblySpec.Get(doc, "Occurrences");
                    result["occurrences"] = ListOccurrences(occurrences);
                }
                if (m == "relations" || m == "all")
                {
                    object relations = AssemblySpec.Get(doc, "Relations3d");
                    result["relations"] = ListRelations(relations);
                }
                if (m == "bom" || m == "all")
                {
                    object occurrences = AssemblySpec.Get(doc, "Occurrences");
                    var bomReadErrors = new List<object>();
                    result["bom"] = BomStatistics(occurrences, bomReadErrors);
                    // P3-2a:BOM 枚举读不到的项不再静默丢,单独列出原因(AI 需要知道 BOM 可能少件)
                    if (bomReadErrors.Count > 0) result["bomReadErrors"] = bomReadErrors;
                }
                return JsonSerializer.Serialize(result, FeatureValidator.JsonOpts);
            });
        }

        // ================= occurrences =================

        private static List<object> ListOccurrences(object occurrences)
        {
            var list = new List<object>();
            int count = GetCount(occurrences);
            if (count < 0) return list;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    object occ = AssemblySpec.Call(occurrences, "Item", new object[] { i });
                    var item = new Dictionary<string, object>
                    {
                        ["index"] = i - 1,
                        ["name"] = AssemblySpec.Get(occ, "Name", "?"),
                        ["file"] = AssemblySpec.Get(occ, "OccurrenceFileName", "")
                    };
                    object visible = AssemblySpec.Get(occ, "Visible");
                    if (visible != null) item["visible"] = Convert.ToBoolean(visible, CultureInfo.InvariantCulture);
                    double[] matrix = AssemblySpec.ReadMatrix(occ);
                    if (matrix != null)
                    {
                        item["matrix"] = matrix;
                        item["position"] = new[] { matrix[12], matrix[13], matrix[14] };
                    }
                    object subs = AssemblySpec.Get(occ, "SubOccurrences");
                    int subCount = GetCount(subs);
                    item["subOccurrenceCount"] = subCount >= 0 ? subCount : 0;
                    // faceCount:该零件 Body 全量面数 = se_assembly_build constrain face1/face2 可填的 0-based 面序号上界。
                    // P3-2a:读不到时不再填假 0(0 会和"子装配合法 0"撞车,还会误导 constrain 面序号上界)。
                    // 三态:实测值 / 合法 0(子装配、空零件) / 读失败(faceCount=null + faceCountError 带原因)。
                    ReadFaceCount(occ, item);
                    list.Add(item);
                }
                catch (Exception ex)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["index"] = i - 1, ["status"] = "error", ["message"] = AssemblySpec.DescribeEx(ex)
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// 读单个 Occurrence 的 faceCount(P3-2a 三态):
        /// 实测值 → faceCount=N;
        /// 子装配(无实体 Body)/空零件(还没建特征) → 合法 0;
        /// 读失败 → faceCount=null + faceCountError 说明原因(不再填假 0)。
        /// </summary>
        private static void ReadFaceCount(object occ, Dictionary<string, object> item)
        {
            object pdoc = AssemblySpec.Get(occ, "OccurrenceDocument");
            if (pdoc == null)
            {
                item["faceCount"] = null;
                item["faceCountError"] = "读不到 OccurrenceDocument";
                return;
            }
            if (AssemblySpec.IsAssemblyDocument(pdoc))
            {
                item["faceCount"] = 0;   // 子装配无实体 Body,constrain 本来也不可用,合法 0
                return;
            }
            try
            {
                object models = AssemblySpec.Get(pdoc, "Models");
                // 零件还没建任何特征时 Models.Item(1) 抛 DISP_E_BADINDEX(0x8002000B) → 空零件,合法 0
                object model = models == null ? null : AssemblySpec.Call(models, "Item", new object[] { 1 });
                object body = model == null ? null : AssemblySpec.Get(model, "Body");
                object faces = body == null ? null : AssemblySpec.Call(body, "Faces", new object[] { 1 });
                int fc = faces == null ? 0 : GetCount(faces);
                if (fc >= 0)
                    item["faceCount"] = fc;
                else
                {
                    item["faceCount"] = null;
                    item["faceCountError"] = "Faces.Count 读不到";
                }
            }
            catch (Exception ex)
            {
                if (ex.GetBaseException().HResult == unchecked((int)0x8002000B))   // DISP_E_BADINDEX = 无几何
                {
                    item["faceCount"] = 0;
                }
                else
                {
                    item["faceCount"] = null;
                    item["faceCountError"] = AssemblySpec.DescribeEx(ex);
                }
            }
        }

        // ================= relations =================

        private static List<object> ListRelations(object relations)
        {
            var list = new List<object>();
            int count = GetCount(relations);
            if (count < 0) return list;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    object rel = AssemblySpec.Call(relations, "Item", new object[] { i });
                    var item = new Dictionary<string, object> { ["index"] = i - 1 };

                    object type = AssemblySpec.Get(rel, "Type");
                    // SE2022 实测:GroundRelation3d.Type = 1959028688(大常量,非来源项目的 0..9 小枚举)
                    // → 类型名优先按强类型 cast 判定,数值映射只作兜底
                    string typeName = null;
                    if (rel is SolidEdgeAssembly.GroundRelation3d) typeName = "Ground";
                    else if (rel is SolidEdgeAssembly.AxialRelation3d) typeName = "Axial";
                    else if (rel is SolidEdgeAssembly.PlanarRelation3d) typeName = "Planar";
                    if (type != null)
                    {
                        long t = Convert.ToInt64(type, CultureInfo.InvariantCulture);
                        item["type"] = t;
                        if (typeName == null)
                            typeName = RelationTypeNames.TryGetValue((int)t, out string tn) ? tn : null;
                        item["type_name"] = typeName ?? "Unknown(" + t + ")";
                    }
                    else if (typeName != null)
                    {
                        item["type_name"] = typeName;
                    }
                    object status = AssemblySpec.Get(rel, "Status");
                    if (status != null)
                    {
                        item["status"] = status;
                        item["solved"] = Convert.ToString(status, CultureInfo.InvariantCulture) == "1";
                    }
                    object detailed = AssemblySpec.Get(rel, "DetailedStatus");
                    if (detailed != null) item["detailed_status"] = detailed;
                    object name = AssemblySpec.Get(rel, "Name");
                    if (name != null) item["name"] = name;
                    object offset = AssemblySpec.Get(rel, "Offset");
                    if (offset != null) item["offset"] = offset;
                    object aligned = AssemblySpec.Get(rel, "NormalsAligned");
                    if (aligned != null) item["normals_aligned"] = Convert.ToBoolean(aligned, CultureInfo.InvariantCulture);
                    object suppressed = AssemblySpec.Get(rel, "Suppressed");
                    if (suppressed != null) item["suppressed"] = Convert.ToBoolean(suppressed, CultureInfo.InvariantCulture);

                    list.Add(item);
                }
                catch (Exception ex)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["index"] = i - 1, ["status"] = "error", ["message"] = AssemblySpec.DescribeEx(ex)
                    });
                }
            }
            return list;
        }

        // ================= bom =================

        private class BomEntry
        {
            public string File;
            public int Qty;
            public bool HasPatternItem;
            public List<string> Instances = new List<string>();
        }

        private static List<object> BomStatistics(object occurrences, List<object> readErrors)
        {
            var byFile = new Dictionary<string, BomEntry>(StringComparer.OrdinalIgnoreCase);
            WalkBom(occurrences, byFile, readErrors);

            var list = new List<object>();
            foreach (var kv in byFile)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["file"] = kv.Key,
                    ["qty"] = kv.Value.Qty,
                    ["instances"] = kv.Value.Instances,
                    ["is_pattern_item"] = kv.Value.HasPatternItem
                });
            }
            return list;
        }

        /// <summary>
        /// 递归走 Occurrences / SubOccurrences。跳过 IncludeInBom=false;
        /// IsPatternItem=true 的成员照样计数但打标(阵列成员,数量上要做乘法判断)。
        /// SubOccurrence 的文件名在 SubOccurrenceFileName 上(不在 OccurrenceFileName,离线文档核实)。
        /// P3-2a:枚举读不到的项不再静默跳过,记入 readErrors(避免 BOM 少一件 AI 不知情)。
        /// </summary>
        private static void WalkBom(object occurrences, Dictionary<string, BomEntry> byFile, List<object> readErrors)
        {
            int count = GetCount(occurrences);
            if (count < 0)
            {
                readErrors.Add(new { scope = "occurrences", message = "Count 读不到,该层枚举中止(此层 BOM 可能少件)" });
                return;
            }
            for (int i = 1; i <= count; i++)
            {
                object occ;
                try { occ = AssemblySpec.Call(occurrences, "Item", new object[] { i }); }
                catch (Exception ex)
                {
                    readErrors.Add(new { index = i - 1, message = AssemblySpec.DescribeEx(ex) });
                    continue;
                }

                object includeBom = AssemblySpec.Get(occ, "IncludeInBom", null);
                if (includeBom != null && !Convert.ToBoolean(includeBom, CultureInfo.InvariantCulture))
                    continue;   // 明确排除出 BOM 的件

                // SubOccurrence 用 SubOccurrenceFileName;顶层 Occurrence 两者都试
                string file = Convert.ToString(AssemblySpec.Get(occ, "SubOccurrenceFileName", null), CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(file))
                    file = Convert.ToString(AssemblySpec.Get(occ, "OccurrenceFileName", "(未知)"), CultureInfo.InvariantCulture);
                string name = Convert.ToString(AssemblySpec.Get(occ, "Name", "?" + i), CultureInfo.InvariantCulture);
                bool patternItem = false;
                object pi = AssemblySpec.Get(occ, "IsPatternItem", null);
                if (pi != null) patternItem = Convert.ToBoolean(pi, CultureInfo.InvariantCulture);

                if (!byFile.TryGetValue(file, out BomEntry entry))
                {
                    entry = new BomEntry { File = file };
                    byFile[file] = entry;
                }
                entry.Qty++;
                if (patternItem) entry.HasPatternItem = true;
                entry.Instances.Add(name);

                object subs = AssemblySpec.Get(occ, "SubOccurrences");
                if (subs != null)
                {
                    int subCount = GetCount(subs);
                    if (subCount > 0)
                        WalkBom(subs, byFile, readErrors);
                    else if (subCount < 0)
                        readErrors.Add(new { scope = name, message = "SubOccurrences.Count 读不到,该子装配未递归(其内部 BOM 可能少件)" });
                }
            }
        }

        // ================= 通用 =================

        private static object ResolveDocument(SolidEdgeContext context, string objectId)
        {
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                var handle = context.GetHandle(objectId);
                return handle?.ComObject;
            }
            return context.GetApplication().ActiveDocument;
        }

        private static int GetCount(object collection)
        {
            object c = AssemblySpec.Get(collection, "Count");
            if (c == null) return -1;
            try { return Convert.ToInt32(c, CultureInfo.InvariantCulture); }
            catch { return -1; }
        }
    }
}
