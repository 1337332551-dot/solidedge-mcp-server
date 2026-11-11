using System;
using System.IO;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// P0 featuresFile 入口(纯函数,不碰 COM)。
    /// 重点锁两件事:①两种根节点格式都能读 ②返回的 JsonElement 在 JsonDocument 释放后仍然可用
    /// (Clone 语义,否则下游 FeatureSpecParser 会读到失效元素)。
    /// </summary>
    public class FeatureFileInputTests
    {
        private static string WriteTemp(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "se_features_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void Load_ArrayRoot_ReturnsElements()
        {
            string path = WriteTemp(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}]");
            try
            {
                string error;
                var features = ModelingTools.LoadFeaturesFile(path, out error);

                Assert.Null(error);
                Assert.NotNull(features);
                Assert.Single(features);
                Assert.Equal(JsonValueKind.Object, features[0].ValueKind);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_WrappedObject_ReturnsInnerArray()
        {
            string path = WriteTemp(@"{""features"":[{""op"":""plane"",""name"":""p1""},{""op"":""extrude""}]}");
            try
            {
                string error;
                var features = ModelingTools.LoadFeaturesFile(path, out error);

                Assert.Null(error);
                Assert.Equal(2, features.Length);
                Assert.Equal("plane", features[0].GetProperty("op").GetString());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_ElementsUsableAfterDocumentDisposed()
        {
            // 回归点:如果实现里直接返回 JsonDocument.RootElement 的子元素(未 Clone),
            // 文档释放后 GetProperty 会抛 InvalidOperationException。
            string path = WriteTemp(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}]");
            try
            {
                string error;
                var features = ModelingTools.LoadFeaturesFile(path, out error);
                Assert.Null(error);

                var specs = FeatureSpecParser.ParseAll(features);

                Assert.Single(specs);
                Assert.Equal("extrude", specs[0].Op);
                Assert.Equal(0.05, specs[0].Depth.Value, 12);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_MissingFile_ReturnsErrorWithoutThrowing()
        {
            string path = Path.Combine(Path.GetTempPath(), "se_features_" + Guid.NewGuid().ToString("N") + ".json");
            string error;
            var features = ModelingTools.LoadFeaturesFile(path, out error);

            Assert.Null(features);
            Assert.Contains("找不到", error);
        }

        [Fact]
        public void Load_InvalidJson_ReturnsError()
        {
            string path = WriteTemp("[{op:extrude");
            try
            {
                string error;
                var features = ModelingTools.LoadFeaturesFile(path, out error);

                Assert.Null(features);
                Assert.Contains("不是合法 JSON", error);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_RootNotArray_ReturnsError()
        {
            string path = WriteTemp(@"{""name"":""hello""}");
            try
            {
                string error;
                var features = ModelingTools.LoadFeaturesFile(path, out error);

                Assert.Null(features);
                Assert.Contains("必须是 JSON 数组", error);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_EmptyPath_ReturnsError()
        {
            string error;
            var features = ModelingTools.LoadFeaturesFile("   ", out error);

            Assert.Null(features);
            Assert.Contains("空字符串", error);
        }

        [Fact]
        public void Resolve_BothInlineAndFile_Rejected()
        {
            using (var doc = JsonDocument.Parse(@"[{""op"":""extrude""}]"))
            {
                string error;
                var features = ModelingTools.ResolveFeatures(new[] { doc.RootElement }, @"C:\some\features.json", out error);

                Assert.Null(features);
                Assert.Contains("只能二选一", error);
            }
        }

        [Fact]
        public void Resolve_InlineOnly_PassesThrough()
        {
            using (var doc = JsonDocument.Parse(@"[{""op"":""extrude""}]"))
            {
                string error;
                var features = ModelingTools.ResolveFeatures(new[] { doc.RootElement }, null, out error);

                Assert.Null(error);
                Assert.Single(features);
            }
        }

        [Fact]
        public void Resolve_NothingProvided_ReturnsNullWithoutError()
        {
            // 保持旧行为:既没内联也没文件时不在这里报错,交给后续"features 不能为空"的既有判定。
            string error;
            var features = ModelingTools.ResolveFeatures(null, null, out error);

            Assert.Null(features);
            Assert.Null(error);
        }
    }
}
