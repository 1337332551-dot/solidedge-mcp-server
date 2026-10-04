using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// Guardrail 分级与门禁(纯函数,不碰 COM)。
    /// 注意:SE_MCP_READONLY 的真实链路(启动时读一次 → ReadOnlyEnabled)归 L3,
    /// xUnit 进程里只能直接设静态属性测拒绝分支——这里不测环境变量(会写出假 bug)。
    /// </summary>
    public class GuardrailTests
    {
        [Theory]
        [InlineData("Delete")]
        [InlineData("delete")]      // 大小写不敏感
        [InlineData("Cut")]
        [InlineData("Drop")]
        [InlineData("Erase")]
        [InlineData("Purge")]
        [InlineData("RemoveFaces")] // Remove 前缀
        [InlineData("  Delete  ")]  // 两端空白被 Trim
        public void Classify_Destructive(string member)
        {
            Assert.Equal(InvocationRisk.Destructive, Guardrail.Classify(member, false));
        }

        [Theory]
        [InlineData("Move")]
        [InlineData("Rotate")]
        [InlineData("Scale")]
        [InlineData("Mirror")]
        [InlineData("Copy")]
        [InlineData("Duplicate")]
        [InlineData("Insert")]
        [InlineData("SetOrigin")]
        [InlineData("AddFiniteExtrudedProtrusion")]
        [InlineData("ReplaceLine")]
        [InlineData("ConvertUnits")]
        [InlineData("ApplyStyle")]
        [InlineData("ClearList")]
        [InlineData("UpdateDraft")]
        public void Classify_ModelChanging(string member)
        {
            Assert.Equal(InvocationRisk.ModelChanging, Guardrail.Classify(member, false));
        }

        [Theory]
        [InlineData("Models")]
        [InlineData("Item")]
        [InlineData("GetRange")]
        [InlineData("Count")]
        [InlineData("Describe")]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public void Classify_Normal(string member)
        {
            Assert.Equal(InvocationRisk.Normal, Guardrail.Classify(member, false));
        }

        [Fact]
        public void Classify_PropertySet_IsAlwaysModelChanging()
        {
            // 写属性(哪怕名字无害)一律视为写操作
            Assert.Equal(InvocationRisk.ModelChanging, Guardrail.Classify("Name", true));
            Assert.Equal(InvocationRisk.ModelChanging, Guardrail.Classify("Visible", true));
        }

        [Fact]
        public void Classify_DestructiveBeatsPropertySet()
        {
            // Delete 即使 propertySet=true 也先判破坏性(名单检查在 propertySet 分支之前)
            Assert.Equal(InvocationRisk.Destructive, Guardrail.Classify("Delete", true));
        }

        [Fact]
        public void Check_Normal_Passes()
        {
            InvocationRisk risk;
            Assert.Null(Guardrail.Check("se_invoke_member", "GetRange", false, false, out risk));
            Assert.Equal(InvocationRisk.Normal, risk);
        }

        [Fact]
        public void Check_ModelChanging_PassesWithoutConfirm()
        {
            InvocationRisk risk;
            Assert.Null(Guardrail.Check("se_invoke_member", "SetOrigin", false, false, out risk));
            Assert.Equal(InvocationRisk.ModelChanging, risk);
        }

        [Fact]
        public void Check_Destructive_RequiresConfirm()
        {
            InvocationRisk risk;
            string err = Guardrail.Check("se_invoke_member", "Delete", false, false, out risk);
            Assert.NotNull(err);
            Assert.Contains("confirm", err);   // 拒绝信息指引 confirm=true

            InvocationRisk risk2;
            Assert.Null(Guardrail.Check("se_invoke_member", "Delete", false, true, out risk2));  // 补 confirm 放行
        }

        [Fact]
        public void Check_ReadOnly_RejectsAllWrites()
        {
            InvocationRisk risk;
            try
            {
                Guardrail.ReadOnlyEnabled = true;

                // ModelChanging 无 confirm → 拒
                string err = Guardrail.Check("se_invoke_member", "SetOrigin", false, false, out risk);
                Assert.NotNull(err);
                Assert.Contains("SE_MCP_READONLY", err);

                // 只读开关优先于 confirm:Destructive + confirm 也拒
                string err2 = Guardrail.Check("se_invoke_member", "Delete", false, true, out risk);
                Assert.NotNull(err2);
                Assert.Contains("只读", err2);

                // 只读不影响 Normal
                Assert.Null(Guardrail.Check("se_invoke_member", "GetRange", false, false, out risk));
            }
            finally
            {
                Guardrail.ReadOnlyEnabled = false;   // 复位,不污染其它测试
            }
        }

        [Fact]
        public void Describe_CoversThreeLevels()
        {
            Assert.Contains("只读", Guardrail.Describe(InvocationRisk.Normal));
            Assert.Contains("写操作", Guardrail.Describe(InvocationRisk.ModelChanging));
            Assert.Contains("破坏性", Guardrail.Describe(InvocationRisk.Destructive));
        }
    }
}
