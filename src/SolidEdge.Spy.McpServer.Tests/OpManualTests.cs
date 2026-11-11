using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// op 手册(OpManual)与注入注册表(OpDocRegistry)。
    /// 这两件东西的存在理由是"把 Description 的常驻开销换成按需注入",
    /// 所以测试重点不在文案,而在三条契约:
    ///   1. 手册收录的 op 必须与 E101 白名单完全一致(否则模型查到的手册和校验器对不上);
    ///   2. 同一个 op 在一次进程里只注入一次(否则历史里会堆多份副本,每轮重发);
    ///   3. se_model_build 的 Description 必须留在预算内(否则瘦身白做)。
    /// </summary>
    public class OpManualTests
    {
        /// <summary>se_model_build 的 Description 字符预算。加内容前先想清楚是否该进 OpManual。</summary>
        private const int ModelBuildDescBudget = 800;

        private static JsonElement[] F(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var list = new JsonElement[root.GetArrayLength()];
                int i = 0;
                foreach (var e in root.EnumerateArray()) list[i++] = e.Clone();
                return list;
            }
        }

        // ---------- 一致性 ----------

        [Fact]
        public void ManualCoversExactlyTheAllowedOps()
        {
            var manual = new HashSet<string>(OpManual.Names, StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(OpRule.Allowed, StringComparer.OrdinalIgnoreCase);

            var missing = allowed.Except(manual, StringComparer.OrdinalIgnoreCase).ToList();
            var extra = manual.Except(allowed, StringComparer.OrdinalIgnoreCase).ToList();

            Assert.True(missing.Count == 0, "E101 允许但手册缺条目: " + string.Join(",", missing));
            Assert.True(extra.Count == 0, "手册有但 E101 不认: " + string.Join(",", extra));
            Assert.Equal(allowed.Count, manual.Count);
        }

        [Fact]
        public void ManualHasNoEmptyEntry()
        {
            foreach (var op in OpManual.Names)
            {
                string text;
                Assert.True(OpManual.Entries.TryGetValue(op, out text), "缺条目: " + op);
                Assert.False(string.IsNullOrWhiteSpace(text), "空条目: " + op);
            }
        }

        [Fact]
        public void IndexListsEveryOp()
        {
            var index = OpManual.Index();
            foreach (var op in OpManual.Names)
            {
                Assert.Contains(op, index);
            }
        }

        [Fact]
        public void IndexTellsHowToQuery()
        {
            var index = OpManual.Index();
            Assert.Contains("\"op\":\"?\"", index);
            Assert.Contains("topic", index);
        }

        [Fact]
        public void LookupCommonReturnsSharedRules()
        {
            var common = OpManual.Lookup("common");
            Assert.Contains("单位", common);
            Assert.Contains("assumeStock", common);
        }

        [Fact]
        public void LookupUnknownExplainsAndFallsBackToIndex()
        {
            var text = OpManual.Lookup("no_such_op");
            Assert.Contains("no_such_op", text);
            Assert.Contains("plane", text);          // 回落到索引
        }

        [Fact]
        public void LookupIsCaseInsensitive()
        {
            Assert.Equal(OpManual.Lookup("boltcircle"), OpManual.Lookup("BoltCircle"));
        }

        // ---------- {"op":"?"} 查询入口 ----------

        [Fact]
        public void Query_NoName_ReturnsIndex()
        {
            string text;
            Assert.True(OpManual.TryQuery(F("[{\"op\":\"?\"}]"), out text));
            Assert.Contains("op 索引", text);
        }

        [Fact]
        public void Query_WithName_ReturnsSingleEntry()
        {
            string text;
            Assert.True(OpManual.TryQuery(F("[{\"op\":\"?\",\"name\":\"sweep\"}]"), out text));
            Assert.Contains("trace", text);
            Assert.DoesNotContain("op 索引", text);
        }

        [Fact]
        public void Query_TopicCommon_ReturnsCommonRules()
        {
            string text;
            Assert.True(OpManual.TryQuery(F("[{\"op\":\"?\",\"topic\":\"common\"}]"), out text));
            Assert.Contains("通用规则", text);
        }

        [Fact]
        public void Query_HelpAliasWorks()
        {
            string text;
            Assert.True(OpManual.TryQuery(F("[{\"op\":\"help\"}]"), out text));
            Assert.Contains("op 索引", text);
        }

        [Fact]
        public void Query_NormalFeatureIsNotAQuery()
        {
            string text;
            Assert.False(OpManual.TryQuery(F("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\"}]"), out text));
            Assert.Null(text);
        }

        // ---------- 注入注册表 ----------

        [Fact]
        public void Registry_InjectsEachOpOnlyOnce()
        {
            OpDocRegistry.Reset();
            try
            {
                var first = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, new[] { "hole", "cut" });
                Assert.Equal(2, first.Count);
                Assert.True(first.ContainsKey("hole"));
                Assert.True(first.ContainsKey("cut"));

                var second = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, new[] { "hole", "cut" });
                Assert.Empty(second);

                // 新 op 只补新的那个,已发过的不重复
                var third = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, new[] { "hole", "pattern" });
                Assert.Single(third);
                Assert.True(third.ContainsKey("pattern"));
            }
            finally
            {
                OpDocRegistry.Reset();
            }
        }

        [Fact]
        public void Registry_SkipsUnknownOpInsteadOfReturningEmptyEntry()
        {
            OpDocRegistry.Reset();
            try
            {
                var got = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, new[] { "__bad_op__", "hole" });
                Assert.Single(got);
                Assert.True(got.ContainsKey("hole"));
                Assert.False(got.ContainsKey("__bad_op__"));
            }
            finally
            {
                OpDocRegistry.Reset();
            }
        }

        [Fact]
        public void Registry_DomainsDoNotCollide()
        {
            OpDocRegistry.Reset();
            try
            {
                var model = OpDocRegistry.TakeUnsent("model", OpManual.Entries, new[] { "hole" });
                var other = OpDocRegistry.TakeUnsent("assembly", OpManual.Entries, new[] { "hole" });
                Assert.Single(model);
                Assert.Single(other);
            }
            finally
            {
                OpDocRegistry.Reset();
            }
        }

        [Fact]
        public void Note_PointsAtHowToLookUpAgain()
        {
            var fresh = OpDocRegistry.Note(new[] { "hole" }, new[] { "hole" });
            Assert.Contains("本次注入", fresh);
            Assert.Contains("\"op\":\"?\"", fresh);

            var repeated = OpDocRegistry.Note(new[] { "hole" }, new string[0]);
            Assert.Contains("不重复", repeated);
        }

        // ---------- 描述预算 ----------

        [Fact]
        public void ModelBuildDescriptionStaysWithinBudget()
        {
            var m = typeof(ModelingTools).GetMethod("se_model_build", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(m);
            var attr = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(attr);
            Assert.True(attr.Description.Length <= ModelBuildDescBudget,
                "se_model_build 描述 " + attr.Description.Length + " 字符,超出预算 " + ModelBuildDescBudget +
                "。新增的字段说明请写进 OpManual,不要塞回 Description。");
        }

        [Fact]
        public void ValidateFeaturesDescriptionStaysWithinBudget()
        {
            var m = typeof(ModelingTools).GetMethod("se_validate_features", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(m);
            var attr = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(attr);
            Assert.True(attr.Description.Length <= 600,
                "se_validate_features 描述 " + attr.Description.Length + " 字符,超出预算 600。");
        }

        // ---------- 轴框架约定(2026-11-05 用户规矩:每张 2D 草图默认带 X/Y 重合构造线框架) ----------

        [Fact]
        public void CommonManualDocumentsAxisFrame()
        {
            // AI 必须知道框架存在并知道怎么用:是什么(X/Y 重合全约束构造线)、怎么用(定位引用框架线)、
            // 例外(hole op 6311 僵尸史不带构造几何)——缺一条,模型就会退回自画临时辅助线的老毛病
            Assert.Contains("14. 轴框架", OpManual.Common);
            Assert.Contains("X/Y 轴重合", OpManual.Common);
            Assert.Contains("引用框架线", OpManual.Common);
            Assert.Contains("hole op 孔轮廓不带任何构造几何", OpManual.Common);
        }

        [Fact]
        public void CommonManualSaysFrameDoesNotShiftDimsIndices()
        {
            // dims 的 element 索引只数声明的环几何——框架线不占位,这句话丢了模型会算错索引
            Assert.Contains("不占 dims 的 element 索引", OpManual.Common);
        }

        [Fact]
        public void PolarHoleManualReferencesFrameX()
        {
            string text;
            Assert.True(OpManual.Entries.TryGetValue("polarhole", out text));
            Assert.Contains("轴框架", text);
            Assert.Contains("角度引用框架 X 线", text);
        }

        [Fact]
        public void PatternManualAlignsAuxLineLengthWithFrame()
        {
            string text;
            Assert.True(OpManual.Entries.TryGetValue("pattern", out text));
            Assert.Contains("与轴框架同常量", text);
        }

        [Fact]
        public void SketchFrameLenIsTheSingleSourceOfLength()
        {
            // 20mm 全项目唯一常量:反射读 internal 常量,别让魔数 0.02/0.05 再散落回各处
            var f = typeof(ModelingTools).GetField("SketchFrameLen",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(f);
            Assert.Equal(0.02, f.GetValue(null));
        }
    }
}
