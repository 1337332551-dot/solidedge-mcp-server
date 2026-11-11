using System.Linq;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// assumeStock 回归测试（2026-10-23 新增）。
    ///
    /// 背景：E401「除料前必须先建实体」只扫【本次 features 数组】，看不见文档里已有的实体。
    /// 于是在"已有底板上单独打一个孔"这种合法增量场景会被误拒（真机实测过）。
    /// 校验器是纯函数，无法自己查 COM，只能由调用方传 assumeStock 声明"目标文档已有实体"。
    ///
    /// 这组用例锁三件事：
    /// 1) 不传 assumeStock 时旧行为完全不变（E401 照拦）——防止"顺手放宽"埋掉原有保护；
    /// 2) 传 assumeStock=true 时只放行 E401，其他规则照旧；
    /// 3) 同一批里有增材时，两种取值都不该报 E401。
    /// </summary>
    public class AssumeStockTests
    {
        private const string CutOnly =
            "[{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"rect\":[[0,0],[0.04,0.04]],\"depth\":0.005}]";

        private const string ExtrudeThenCut =
            "[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"rect\":[[-0.05,-0.05],[0.05,0.05]],\"depth\":0.02}," +
            "{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"rect\":[[0,0],[0.01,0.01]],\"depth\":0.02}]";

        private const string PlaneAliasThenCut =
            "[{\"op\":\"plane\",\"name\":\"@top\",\"base\":\"RefPlane_1\",\"distance\":0.02}," +
            "{\"op\":\"cut\",\"plane\":\"@top\",\"rect\":[[0,0],[0.01,0.01]],\"depth\":0.005}]";

        private static bool Has(ValidationReport r, string code) => r.issues.Any(i => i.code == code);

        [Fact]
        public void 仅除料且未声明已有实体_E401拦截()
        {
            var r = FeatureValidator.ValidateJson(CutOnly);
            Assert.True(Has(r, "E401"), "未传 assumeStock 时 E401 必须照拦（旧行为不能变）");
            Assert.Equal("error", r.status);
        }

        [Fact]
        public void 仅除料且声明已有实体_E401放行()
        {
            var r = FeatureValidator.ValidateJson(CutOnly, assumeStock: true);
            Assert.False(Has(r, "E401"), "assumeStock=true 时不应再报 E401");
        }

        [Fact]
        public void 同批有增材时_两种取值都不报E401()
        {
            var r1 = FeatureValidator.ValidateJson(ExtrudeThenCut);
            var r2 = FeatureValidator.ValidateJson(ExtrudeThenCut, assumeStock: true);
            Assert.False(Has(r1, "E401"));
            Assert.False(Has(r2, "E401"));
        }

        [Fact]
        public void 平面别名后除料_带assumeStock只少E401一条error()
        {
            // 用"前后对比"而不是硬编码 errorCount：本用例只想证明
            // assumeStock 的唯一作用是少掉 E401 这一条，别的规则结论必须一模一样。
            var withoutStock = FeatureValidator.ValidateJson(PlaneAliasThenCut, assumeStock: false);
            var withStock = FeatureValidator.ValidateJson(PlaneAliasThenCut, assumeStock: true);

            Assert.True(Has(withoutStock, "E401"), "前置：不声明已有实体时该批必须被 E401 拦");
            Assert.False(Has(withStock, "E401"), "声明已有实体后不应再报 E401");
            Assert.Equal(withoutStock.errorCount - 1, withStock.errorCount);
        }

        [Fact]
        public void assumeStock不影响其他错误码()
        {
            // 缺 plane 的 cut：无论如何都该有引用类错误，assumeStock 不该把它一起放过
            const string missingPlane = "[{\"op\":\"cut\",\"rect\":[[0,0],[0.01,0.01]],\"depth\":0.005}]";
            var r = FeatureValidator.ValidateJson(missingPlane, assumeStock: true);
            Assert.True(r.errorCount > 0, "缺 plane 必须仍报错，assumeStock 不应放宽其他规则");
        }

        [Fact]
        public void ValidateJson与Validate数组重载结果一致()
        {
            using var doc = JsonDocument.Parse(CutOnly);
            var arr = doc.RootElement.EnumerateArray().ToArray();

            var byJson = FeatureValidator.ValidateJson(CutOnly, assumeStock: true);
            var byArray = FeatureValidator.Validate(arr, assumeStock: true);

            Assert.Equal(byJson.status, byArray.status);
            Assert.Equal(byJson.errorCount, byArray.errorCount);
            Assert.Equal(byJson.warningCount, byArray.warningCount);
        }

        [Fact]
        public void 默认参数保持向后兼容()
        {
            // 不传 assumeStock 等于 false：既有调用点（含旧二进制行为）语义不变
            var r1 = FeatureValidator.ValidateJson(CutOnly);
            var r2 = FeatureValidator.ValidateJson(CutOnly, assumeStock: false);
            Assert.Equal(r1.status, r2.status);
            Assert.Equal(r1.errorCount, r2.errorCount);
        }

        [Fact]
        public void 规则版本号已升到1点5()
        {
            // 规则行为变更必须升版本号；这条防止"改了规则忘了改版本"
            Assert.Equal("1.5", FeatureValidator.Version);
        }
    }
}
