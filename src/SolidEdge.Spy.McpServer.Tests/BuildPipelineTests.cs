using System.Collections.Generic;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// P1:sets 参数表 + 表达式占位符(纯函数,不碰 COM)。
    /// 锁的核心不变量:替换发生在静态校验之前,校验器与构建器看到同一份"写死数字"的 JSON。
    /// </summary>
    public class BuildPipelineTests
    {
        private static JsonElement? Sets(string json)
        {
            return JsonDocument.Parse(json).RootElement;
        }

        private static JsonElement[] Features(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var list = new List<JsonElement>();
                foreach (var it in doc.RootElement.EnumerateArray())
                    list.Add(it.Clone());
                return list.ToArray();
            }
        }

        [Fact]
        public void ResolveSets_NumbersOnly()
        {
            Dictionary<string, double> values;
            string error;
            bool ok = BuildPipeline.ResolveSets(Sets(@"{""l1"":0.04,""w"":0.02}"), out values, out error);

            Assert.True(ok, error);
            Assert.Equal(0.04, values["l1"], 12);
            Assert.Equal(0.02, values["w"], 12);
        }

        [Fact]
        public void ResolveSets_ChainInAnyOrder()
        {
            // d 写在 l1 前面:靠迭代求解,不要求调用方自己排序。
            Dictionary<string, double> values;
            string error;
            bool ok = BuildPipeline.ResolveSets(
                Sets(@"{""d"":""0.5 * {{l1}} + 2"",""l1"":4}"), out values, out error);

            Assert.True(ok, error);
            Assert.Equal(4.0, values["l1"], 12);
            Assert.Equal(4.0, values["d"], 12);   // 0.5*4+2
        }

        [Fact]
        public void ResolveSets_Cycle_ReportsError()
        {
            Dictionary<string, double> values;
            string error;
            bool ok = BuildPipeline.ResolveSets(
                Sets(@"{""a"":""{{b}} + 1"",""b"":""{{a}} + 1""}"), out values, out error);

            Assert.False(ok);
            Assert.Contains("成环", error);
        }

        [Fact]
        public void ResolveSets_UndefinedRef_ReportsError()
        {
            Dictionary<string, double> values;
            string error;
            bool ok = BuildPipeline.ResolveSets(Sets(@"{""a"":""{{nope}} * 2""}"), out values, out error);

            Assert.False(ok);
            Assert.Contains("未定义", error);
        }

        [Fact]
        public void ResolveSets_BadName_ReportsError()
        {
            Dictionary<string, double> values;
            string error;
            bool ok = BuildPipeline.ResolveSets(Sets(@"{""1abc"":1}"), out values, out error);

            Assert.False(ok);
            Assert.Contains("名字不合法", error);
        }

        [Fact]
        public void ResolveSets_NotObject_ReportsError()
        {
            Dictionary<string, double> values;
            string error;
            bool ok = BuildPipeline.ResolveSets(Sets(@"[1,2,3]"), out values, out error);

            Assert.False(ok);
            Assert.Contains("必须是对象", error);
        }

        [Fact]
        public void ResolveSets_BadExpression_ReportsError()
        {
            Dictionary<string, double> values;
            string error;
            bool ok = BuildPipeline.ResolveSets(Sets(@"{""a"":""1 +* 2""}"), out values, out error);

            Assert.False(ok);
            Assert.Contains("求值失败", error);
        }

        [Fact]
        public void Substitute_ReplacesNumberAndStaysParsable()
        {
            var features = Features(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.05,-0.03],[0.05,0.03]],""depth"":""{{d}}""}]");

            var values = new Dictionary<string, double> { ["d"] = 0.0125 };
            JsonElement[] resolved;
            string error;
            bool ok = BuildPipeline.Substitute(features, values, out resolved, out error);

            Assert.True(ok, error);
            Assert.Single(resolved);
            Assert.Equal(0.0125, resolved[0].GetProperty("depth").GetDouble(), 12);

            // 替换结果必须能被构建器那套解析器正常读走
            var specs = FeatureSpecParser.ParseAll(resolved);
            Assert.Equal(0.0125, specs[0].Depth.Value, 12);
        }

        [Fact]
        public void Substitute_UnknownPlaceholder_WithoutSets_ReportsError()
        {
            var features = Features(@"[{""op"":""extrude"",""depth"":""{{d}}""}]");

            JsonElement[] resolved;
            string error;
            bool ok = BuildPipeline.Substitute(features, null, out resolved, out error);

            Assert.False(ok);
            Assert.Contains("{{d}}", error);
        }

        [Fact]
        public void Substitute_PlaceholderInsideString_ReplacesTextKeepsQuotes()
        {
            // 表驱动的典型用法:文件名按行编号("save":"E:\out\{{idx}}.par")。
            var features = Features(@"[{""op"":""doc"",""save"":""E:\\out\\{{idx}}.par""}]");
            var values = new Dictionary<string, double> { ["idx"] = 7 };

            JsonElement[] resolved;
            string error;
            bool ok = BuildPipeline.Substitute(features, values, out resolved, out error);

            Assert.True(ok, error);
            Assert.Equal(@"E:\out\7.par", resolved[0].GetProperty("save").GetString());
        }

        [Fact]
        public void Substitute_NoPlaceholder_ReturnsInputAsIs()
        {
            var features = Features(@"[{""op"":""extrude"",""depth"":0.05}]");
            var values = new Dictionary<string, double> { ["d"] = 1 };

            JsonElement[] resolved;
            string error;
            bool ok = BuildPipeline.Substitute(features, values, out resolved, out error);

            Assert.True(ok, error);
            Assert.Same(features, resolved);
        }

        [Fact]
        public void ApplySets_EndToEnd_ValidationSeesResolvedNumbers()
        {
            // 占位符在 JSON 里只能以字符串形式出现(负数用表达式写进 sets)。
            var features = Features(
                @"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[""{{nx}}"",""{{ny}}""],[""{{hw}}"",""{{hh}}""]],""depth"":""{{t}}""}]");
            var sets = Sets(@"{""hw"":0.05,""hh"":0.03,""nx"":""-{{hw}}"",""ny"":""-{{hh}}"",""t"":""2 * 0.005""}");

            JsonElement[] resolved;
            string error;
            bool ok = ModelingTools.ApplySets(features, sets, out resolved, out error);

            Assert.True(ok, error);
            var report = FeatureValidator.Validate(resolved, false);
            Assert.False(report.HasError);
            Assert.Equal(1, report.featureCount);
        }

        // ---------------- P2:doc op 分段 ----------------

        [Fact]
        public void Split_NoDocOp_KeepsSingleSegment()
        {
            var features = Features(@"[{""op"":""extrude""},{""op"":""cut""}]");

            List<BuildSegment> segments;
            string error;
            bool ok = BuildPipeline.SplitSegments(features, out segments, out error);

            Assert.True(ok, error);
            Assert.Single(segments);
            Assert.Null(segments[0].Doc);
            Assert.Equal(2, segments[0].Features.Count);
        }

        [Fact]
        public void Split_DocOpStartsNewSegment()
        {
            var features = Features(
                @"[{""op"":""extrude""},{""op"":""doc"",""type"":""part"",""name"":""p2""},{""op"":""extrude""},{""op"":""doc""},{""op"":""cut""}]");

            List<BuildSegment> segments;
            string error;
            bool ok = BuildPipeline.SplitSegments(features, out segments, out error);

            Assert.True(ok, error);
            Assert.Equal(3, segments.Count);
            Assert.Null(segments[0].Doc);                 // 起手段沿用调用方文档
            Assert.Equal(1, segments[0].Features.Count);
            Assert.Equal("p2", segments[1].Name);
            Assert.Equal(1, segments[1].Features.Count);
            Assert.Equal(1, segments[2].Features.Count);
            Assert.Equal(2, segments[2].Index);
        }

        [Fact]
        public void Split_LeadingDocOp_NoEmptySegment()
        {
            var features = Features(@"[{""op"":""doc"",""type"":""part""},{""op"":""extrude""}]");

            List<BuildSegment> segments;
            string error;
            bool ok = BuildPipeline.SplitSegments(features, out segments, out error);

            Assert.True(ok, error);
            Assert.Single(segments);
            Assert.True(segments[0].Doc.HasValue);
            Assert.Single(segments[0].Features);
        }

        [Fact]
        public void Split_DocOpWithoutFeatures_KeptAsEmptySegment()
        {
            var features = Features(@"[{""op"":""doc"",""save"":""E:\\a.par""}]");

            List<BuildSegment> segments;
            string error;
            bool ok = BuildPipeline.SplitSegments(features, out segments, out error);

            Assert.True(ok, error);
            Assert.Single(segments);
            Assert.Empty(segments[0].Features);
        }

        [Fact]
        public void Split_BadDocFieldType_ReportsError()
        {
            var features = Features(@"[{""op"":""doc"",""type"":123}]");

            List<BuildSegment> segments;
            string error;
            bool ok = BuildPipeline.SplitSegments(features, out segments, out error);

            Assert.False(ok);
            Assert.Contains("type 必须是字符串", error);
        }

        [Fact]
        public void ApplySets_TableRows_ExpandToSegments()
        {
            var template = Features(
                @"[{""op"":""doc"",""type"":""part"",""save"":""E:\\out\\{{idx}}.par""}," +
                @"{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[""{{n}}"",""{{n}}""],[""{{w}}"",""{{h}}""]],""depth"":""{{t}}""}]");
            var rows = Sets(@"[{""idx"":1,""n"":0,""w"":0.1,""h"":0.05,""t"":0.01},{""idx"":2,""n"":0,""w"":0.2,""h"":0.05,""t"":0.02}]");

            JsonElement[] resolved;
            string error;
            bool ok = ModelingTools.ApplySets(template, rows, out resolved, out error);

            Assert.True(ok, error);
            Assert.Equal(4, resolved.Length);        // 每行 = doc + extrude

            List<BuildSegment> segments;
            string splitError;
            Assert.True(BuildPipeline.SplitSegments(resolved, out segments, out splitError), splitError);
            Assert.Equal(2, segments.Count);
            Assert.Equal(@"E:\out\1.par", BuildPipeline.ReadString(segments[0].Doc.Value, "save"));
            Assert.Equal(@"E:\out\2.par", BuildPipeline.ReadString(segments[1].Doc.Value, "save"));
        }

        [Fact]
        public void ApplySets_TableRowsWithoutDocOp_ReportsError()
        {
            var template = Features(@"[{""op"":""extrude"",""depth"":""{{t}}""}]");
            var rows = Sets(@"[{""t"":0.01},{""t"":0.02}]");

            JsonElement[] resolved;
            string error;
            bool ok = ModelingTools.ApplySets(template, rows, out resolved, out error);

            Assert.False(ok);
            Assert.Contains("没有 doc op", error);
        }

        [Fact]
        public void ApplySets_TableRowError_ReportsRowNumber()
        {
            var template = Features(@"[{""op"":""doc""},{""op"":""extrude"",""depth"":""{{t}}""}]");
            var rows = Sets(@"[{""t"":0.01},{""t"":""{{nope}}""}]");

            JsonElement[] resolved;
            string error;
            bool ok = ModelingTools.ApplySets(template, rows, out resolved, out error);

            Assert.False(ok);
            Assert.Contains("第 2 行", error);
        }

        // ---------------- P3:measure / compute ----------------

        [Fact]
        public void Split_MeasureAndCompute_AreOwnSegments()
        {
            var features = Features(
                @"[{""op"":""extrude""},{""op"":""measure"",""property"":""volume"",""as"":""v1""}," +
                @"{""op"":""compute"",""expr"":""{{v1.mm3}}/2"",""as"":""half""},{""op"":""extrude"",""depth"":""{{half}}""}]");

            List<BuildSegment> segments;
            string error;
            bool ok = BuildPipeline.SplitSegments(features, out segments, out error);

            Assert.True(ok, error);
            Assert.Equal(4, segments.Count);
            Assert.Null(segments[0].ActionOp);
            Assert.Equal("measure", segments[1].ActionOp);
            Assert.Equal("v1", segments[1].Name);
            Assert.Equal("compute", segments[2].ActionOp);
            Assert.Single(segments[3].Features);
        }

        [Fact]
        public void Evaluate_ReferencesValuesIncludingDottedNames()
        {
            var env = new Dictionary<string, double> { ["v1.mm3"] = 60000, ["t"] = 0.01 };

            double value;
            string error;
            bool ok = BuildPipeline.Evaluate("{{v1.mm3}} / 2 + {{t}}", env, out value, out error);

            Assert.True(ok, error);
            Assert.Equal(30000.01, value, 6);
        }

        [Fact]
        public void Evaluate_UndefinedName_ReportsError()
        {
            var env = new Dictionary<string, double> { ["t"] = 1 };

            double value;
            string error;
            bool ok = BuildPipeline.Evaluate("{{nope}} + 1", env, out value, out error);

            Assert.False(ok);
            Assert.Contains("未定义", error);
        }

        [Fact]
        public void Substitute_DottedPlaceholder_ReplacedInNumericPosition()
        {
            var features = Features(@"[{""op"":""extrude"",""depth"":""{{v1.mm3}}""}]");
            var values = new Dictionary<string, double> { ["v1.mm3"] = 12.5 };

            JsonElement[] resolved;
            string error;
            bool ok = BuildPipeline.Substitute(features, values, out resolved, out error);

            Assert.True(ok, error);
            Assert.Equal(12.5, resolved[0].GetProperty("depth").GetDouble(), 9);
        }

        [Fact]
        public void ApplySets_NoSetsNoPlaceholder_PassesThrough()
        {
            var features = Features(@"[{""op"":""extrude"",""depth"":0.05}]");

            JsonElement[] resolved;
            string error;
            bool ok = ModelingTools.ApplySets(features, null, out resolved, out error);

            Assert.True(ok, error);
            Assert.Same(features, resolved);
        }
    }
}
