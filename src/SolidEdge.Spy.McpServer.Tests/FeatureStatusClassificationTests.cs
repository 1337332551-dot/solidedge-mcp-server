using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 特征状态分类回归测试(2026-10-14 新增)。
    ///
    /// 背景:Solid Edge 官方 <c>FeatureStatusConstants</c> 共【五档】——
    /// 事实来源为 SE 2022 自带 Program\interop.SolidEdgeConstantLib.dll 反编译核实
    /// (ilspycmd,SolidEdge.Constant.Interop\FeatureStatusConstants.cs)。
    /// 旧代码按 "status != 1216476310 即判失败并自动删除特征",
    /// 把 igFeatureWarned(6312,建成但有警告)/igFeatureSuppressed(6313,用户主动压缩)/
    /// igFeatureRolledBack(6314,被回滚)一并误杀,会触发"建了删、删了建"死循环。
    ///
    /// 本组测试把契约钉死:【只有 igFeatureFailed(1216476311)算失败、才允许删除回滚】,
    /// 其余档位与未知码一律保留。
    ///
    /// 断言用「官方枚举名」字符串而不直接比 FeatureHealth(该枚举是 internal 且不进公共签名),
    /// 走的是生产代码同一对函数:ClassifyFeatureStatus + HealthEnumName。纯函数,不碰 COM。
    /// </summary>
    public class FeatureStatusClassificationTests
    {
        // 官方五档的数值(与 interop.SolidEdgeConstantLib.dll 反编译结果逐一对齐)
        private const long IgFeatureOk = 1216476310;
        private const long IgFeatureFailed = 1216476311;
        private const long IgFeatureWarned = 1216476312;
        private const long IgFeatureSuppressed = 1216476313;
        private const long IgFeatureRolledBack = 1216476314;

        private const string NameOk = "igFeatureOK";
        private const string NameFailed = "igFeatureFailed";
        private const string NameWarned = "igFeatureWarned";
        private const string NameSuppressed = "igFeatureSuppressed";
        private const string NameRolledBack = "igFeatureRolledBack";
        private const string NameUnknown = "unknown";

        /// <summary>Status → 官方枚举名(生产代码同款两步:ClassifyFeatureStatus → HealthEnumName)。</summary>
        private static string Health(long? status)
            => ModelingTools.HealthEnumName(ModelingTools.ClassifyFeatureStatus(status));

        [Theory]
        [InlineData(IgFeatureOk, NameOk)]
        [InlineData(IgFeatureFailed, NameFailed)]
        [InlineData(IgFeatureWarned, NameWarned)]
        [InlineData(IgFeatureSuppressed, NameSuppressed)]
        [InlineData(IgFeatureRolledBack, NameRolledBack)]
        public void Classify_OfficialFiveCodes_MapToExpectedEnumName(long status, string expectedName)
        {
            Assert.Equal(expectedName, Health(status));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1216476309)]
        [InlineData(1216476315)]
        [InlineData(999999999)]
        public void Classify_UnknownCode_FallsBackToUnknown(long status)
        {
            // 未知码既不猜成正常、也不猜成僵尸——归 unknown/Unknown,由调用方上报人工确认。
            Assert.Equal(NameUnknown, Health(status));
        }

        [Fact]
        public void Classify_StatusUnreadable_IsUnknown_NotFailure()
        {
            // Status 读不到(COM 读值失败)时必须归 unknown:
            // 旧逻辑在 status.HasValue==false 时直接静默走成功分支,新逻辑要显式上报"读不到"。
            Assert.Equal(NameUnknown, Health(null));
        }

        [Fact]
        public void OnlyZombie_IsAllowedToTriggerRollbackDelete()
        {
            // 本次修复的核心契约:除 6311 外,任何档位/未知码都不得被判为僵尸(即不得触发删除回滚)。
            var mustNotBeZombie = new long?[] { IgFeatureOk, IgFeatureWarned, IgFeatureSuppressed, IgFeatureRolledBack, 0, null };
            foreach (var s in mustNotBeZombie)
            {
                Assert.NotEqual(NameFailed, Health(s));
            }
        }

        [Fact]
        public void Warned_Suppressed_RolledBack_AreKeptButNotFullyHealthy()
        {
            // 这三档正是旧逻辑的误杀对象:必须"保留"(非 igFeatureFailed)且"非完全健康"(非 igFeatureOK)。
            var keptButImperfect = new[] { IgFeatureWarned, IgFeatureSuppressed, IgFeatureRolledBack };
            foreach (var s in keptButImperfect)
            {
                string h = Health(s);
                Assert.NotEqual(NameFailed, h);
                Assert.NotEqual(NameOk, h);
            }
        }

        [Fact]
        public void HealthMeaning_EveryBucket_HasHumanReadableText()
        {
            var all = new long?[]
            {
                IgFeatureOk, IgFeatureFailed, IgFeatureWarned, IgFeatureSuppressed, IgFeatureRolledBack, 0, null
            };
            foreach (var s in all)
            {
                var bucket = ModelingTools.ClassifyFeatureStatus(s);
                Assert.False(string.IsNullOrWhiteSpace(ModelingTools.HealthEnumName(bucket)));
                Assert.False(string.IsNullOrWhiteSpace(ModelingTools.HealthMeaning(bucket)));
            }
        }
    }
}
