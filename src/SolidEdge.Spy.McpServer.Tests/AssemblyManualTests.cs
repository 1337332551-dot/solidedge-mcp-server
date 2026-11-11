using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using SolidEdge.Spy.McpServer.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 装配域手册 + 全 server 描述预算。
    /// 装配的 op 白名单在 AssemblySpec.SupportedOps(internal),用反射读,不为了测试改生产可见性。
    /// </summary>
    public class AssemblyManualTests
    {
        private static string[] SupportedOps()
        {
            var t = typeof(AssemblyManual).Assembly.GetType("SolidEdge.Spy.McpServer.Tools.AssemblySpec");
            Assert.NotNull(t);
            var f = t.GetField("SupportedOps", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(f);
            return (string[])f.GetValue(null);
        }

        [Fact]
        public void ManualCoversExactlyTheSupportedOps()
        {
            var manual = new HashSet<string>(AssemblyManual.Names, StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(SupportedOps(), StringComparer.OrdinalIgnoreCase);

            Assert.Empty(allowed.Except(manual, StringComparer.OrdinalIgnoreCase));
            Assert.Empty(manual.Except(allowed, StringComparer.OrdinalIgnoreCase));
            Assert.Equal(allowed.Count, manual.Count);
        }

        [Fact]
        public void IndexListsEveryOp()
        {
            var index = AssemblyManual.Index();
            foreach (var op in AssemblyManual.Names)
                Assert.Contains(op, index);
        }

        [Fact]
        public void CommonMentionsComponentForms()
        {
            var common = AssemblyManual.Lookup("common");
            Assert.Contains("obj-N", common);
            Assert.Contains("confirm", common);
        }

        [Fact]
        public void RegistryIsSharedButDomainsSeparate()
        {
            OpDocRegistry.Reset();
            try
            {
                var m = OpDocRegistry.TakeUnsent(OpManual.Domain, OpManual.Entries, new[] { "hole" });
                var a = OpDocRegistry.TakeUnsent(AssemblyManual.Domain, AssemblyManual.Entries, new[] { "constrain" });
                Assert.Single(m);
                Assert.Single(a);
            }
            finally
            {
                OpDocRegistry.Reset();
            }
        }

        [Fact]
        public void AssemblyBuildDescriptionStaysWithinBudget()
        {
            var m = typeof(AssemblyBuildTools).GetMethod("se_assembly_build", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(m);
            var d = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(d);
            Assert.True(d.Description.Length <= 800,
                "se_assembly_build 描述 " + d.Description.Length + " 字符,超出 800;字段说明请写进 AssemblyManual。");
        }

        [Fact]
        public void RecipeRunDescriptionStaysWithinBudget()
        {
            var m = typeof(RecipeTools).GetMethod("se_recipe_run", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(m);
            var d = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(d);
            Assert.True(d.Description.Length <= 700,
                "se_recipe_run 描述 " + d.Description.Length + " 字符,超出 700。");
        }

        // ---------- 全 server 描述预算 ----------

        /// <summary>
        /// 全 server 的 [Description] 总量(工具级 + 参数级)。
        /// 这是 tools/list 常驻成本的唯一度量——它每条 API 请求都会被重发,不像 tool result 能被压缩掉。
        /// 基线:瘦身前 29,224,2026-10-25 瘦身后实测 16,995。超顶了就说明又要开始膨胀。
        /// </summary>
        private const int ServerDescriptionBudget = 17000;

        /// <summary>单工具上限,防止某一个工具重新膨胀(瘦身前 se_model_build 是 6,420)。</summary>
        private const int SingleToolBudget = 1200;

        private static List<KeyValuePair<string, int>> MeasureDescriptions()
        {
            var asm = typeof(ModelingTools).Assembly;
            var rows = new List<KeyValuePair<string, int>>();

            foreach (var type in asm.GetTypes())
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.GetCustomAttribute<McpServerToolAttribute>() == null) continue;

                    var d = m.GetCustomAttribute<DescriptionAttribute>();
                    int n = d == null ? 0 : d.Description.Length;

                    foreach (var p in m.GetParameters())
                    {
                        var pd = p.GetCustomAttribute<DescriptionAttribute>();
                        if (pd != null) n += pd.Description.Length;
                    }
                    rows.Add(new KeyValuePair<string, int>(m.Name, n));
                }
            }
            return rows;
        }

        [Fact]
        public void ServerDescriptionTotalStaysWithinBudget()
        {
            var rows = MeasureDescriptions();
            int total = rows.Sum(r => r.Value);
            var top = rows.OrderByDescending(r => r.Value).Take(5).Select(r => r.Key + "=" + r.Value);

            Assert.True(total <= ServerDescriptionBudget,
                "全 server Description 合计 " + total + " 字符,超出预算 " + ServerDescriptionBudget +
                "(瘦身前 29,224)。Top5: " + string.Join(", ", top) +
                "。新增字段说明请写进 OpManual/AssemblyManual(按需注入);确需扩充常驻描述,先从上面这几个里删等量内容。");
        }

        [Fact]
        public void NoSingleToolExceedsBudget()
        {
            var over = MeasureDescriptions()
                .Where(r => r.Value > SingleToolBudget)
                .Select(r => r.Key + "=" + r.Value)
                .ToList();

            Assert.True(over.Count == 0,
                "以下工具描述超出单工具上限 " + SingleToolBudget + " 字符: " + string.Join(", ", over) +
                "。字段说明请写进 OpManual/AssemblyManual,或用注入按需下发。");
        }
    }
}
