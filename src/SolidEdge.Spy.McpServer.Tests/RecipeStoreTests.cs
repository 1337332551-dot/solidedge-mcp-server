using System;
using System.IO;
using System.Linq;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// RecipeStore 定位器(纯文件逻辑,不碰 COM)。
    ///
    /// 两个坑都要堵(2026-09-16 评审):
    /// ① SE_MCP_RECIPES_DIR 是进程级全局 → 同 Collection 串行,用例前后都清;
    /// ② SearchDirs() 的 _dirs 静态缓存首次调用后永不过期 → 每个用例前必须
    ///    RecipeStore.ResetForTests(),否则切环境变量的用例拿到旧目录(假失败)。
    ///
    /// 注意:exe 向上找的 adjacent recipes 目录(本仓库根/recipes)与兜底
    /// %LOCALAPPDATA%\SolidEdgeSpy\recipes 也在搜索路径里——用例一律用
    /// zz_ 前缀的独特名,避免与仓库真实配方同名混淆。
    /// </summary>
    [Collection("RecipeStoreSerial")]
    public class RecipeStoreTests : IDisposable
    {
        private readonly string _dirA;
        private readonly string _dirB;

        public RecipeStoreTests()
        {
            _dirA = Path.Combine(Path.GetTempPath(), "sespy_ut_A_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _dirB = Path.Combine(Path.GetTempPath(), "sespy_ut_B_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dirA);
            Directory.CreateDirectory(_dirB);
            Reset();
        }

        public void Dispose()
        {
            Reset();
            try { Directory.Delete(_dirA, true); } catch { }
            try { Directory.Delete(_dirB, true); } catch { }
        }

        private static void Reset()
        {
            Environment.SetEnvironmentVariable("SE_MCP_RECIPES_DIR", null);
            RecipeStore.ResetForTests();
        }

        private static void WriteRecipe(string dir, string file, string name)
        {
            File.WriteAllText(Path.Combine(dir, file),
                @"{""name"":""" + name + @""",""steps"":[{""member"":""X""}]}");
        }

        [Fact]
        public void List_SkipsUnderscoreFiles()
        {
            WriteRecipe(_dirA, "_draft.json", "_draft");       // _ 前缀 = 草稿,不登记
            WriteRecipe(_dirA, "zz_ut_alpha.json", "zz_ut_alpha");
            Environment.SetEnvironmentVariable("SE_MCP_RECIPES_DIR", _dirA);

            var names = RecipeStore.List().Select(r => r.Name).ToList();
            Assert.Contains("zz_ut_alpha", names);
            Assert.DoesNotContain("_draft", names);
        }

        [Fact]
        public void SearchDirs_EnvDirComesFirst()
        {
            Environment.SetEnvironmentVariable("SE_MCP_RECIPES_DIR", _dirA);
            var dirs = RecipeStore.SearchDirs();
            Assert.NotEmpty(dirs);
            Assert.Equal(Path.GetFullPath(_dirA), Path.GetFullPath(dirs[0]));   // 环境变量目录最优先
            // adjacent(仓库 recipes)与 DefaultDir 也应在列表里,且去重
            Assert.Equal(dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count(), dirs.Length);
        }

        [Fact]
        public void SameName_EnvDirWinsOverLaterDirs()
        {
            // 同名配方按目录顺序先命中优先
            WriteRecipe(_dirA, "zz_ut_dup.json", "zz_ut_dup");
            Environment.SetEnvironmentVariable("SE_MCP_RECIPES_DIR", _dirA);
            var hit = RecipeStore.List().First(r => r.Name == "zz_ut_dup");
            Assert.Equal(Path.GetFullPath(_dirA), Path.GetFullPath(hit.Directory));
        }

        [Fact]
        public void ResetForTests_EnvChangeTakesEffectImmediately()
        {
            // ★ 评审点②的核心验证:换环境变量后必须 ResetForTests,
            // 否则 _dirs 缓存让第二次 SearchDirs 拿到旧目录(假失败)
            WriteRecipe(_dirA, "zz_ut_cache.json", "zz_ut_cache");
            WriteRecipe(_dirB, "zz_ut_cache.json", "zz_ut_cache");

            Environment.SetEnvironmentVariable("SE_MCP_RECIPES_DIR", _dirA);
            var first = RecipeStore.List().First(r => r.Name == "zz_ut_cache");
            Assert.Equal(Path.GetFullPath(_dirA), Path.GetFullPath(first.Directory));

            Environment.SetEnvironmentVariable("SE_MCP_RECIPES_DIR", _dirB);
            RecipeStore.ResetForTests();                        // 不调这个,断言必失败(缓存)
            var second = RecipeStore.List().First(r => r.Name == "zz_ut_cache");
            Assert.Equal(Path.GetFullPath(_dirB), Path.GetFullPath(second.Directory));
        }

        [Fact]
        public void Find_ByShortName_IgnoresCaseAndJsonSuffix()
        {
            WriteRecipe(_dirA, "zz_ut_find.json", "zz_ut_find");
            Environment.SetEnvironmentVariable("SE_MCP_RECIPES_DIR", _dirA);

            var byName = RecipeStore.Find("zz_ut_find");
            Assert.NotNull(byName);
            Assert.Equal("zz_ut_find", byName.Name);

            var byJson = RecipeStore.Find("zz_ut_find.json");   // 带 .json 后缀也接受
            Assert.NotNull(byJson);
            Assert.Equal("zz_ut_find", byJson.Name);

            var byCase = RecipeStore.Find("ZZ_UT_FIND");        // 大小写不敏感
            Assert.NotNull(byCase);
        }

        [Fact]
        public void Find_ByAbsolutePath_ReturnsFileRef()
        {
            WriteRecipe(_dirA, "zz_ut_path.json", "zz_ut_path");
            // 不设环境变量(List 扫不到它),只按路径找
            string full = Path.Combine(_dirA, "zz_ut_path.json");
            var r = RecipeStore.Find(full);
            Assert.NotNull(r);
            Assert.Equal("zz_ut_path", r.Name);
            Assert.Equal(full, r.Path);
        }

        [Fact]
        public void Find_Unknown_ReturnsNull()
        {
            Assert.Null(RecipeStore.Find("zz_ut_nonexistent_42"));
            Assert.Null(RecipeStore.Find(null));
            Assert.Null(RecipeStore.Find("  "));
        }
    }
}
