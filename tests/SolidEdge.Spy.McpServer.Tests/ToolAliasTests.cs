using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using SolidEdge.Spy.McpServer;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 工具别名层(2026-11-07 工具面精简)测试。
    /// 合同:
    /// ① 别名目标必须在 ToolRisk 登记表(不变式,防改写后被 fail-closed 裸拒);
    /// ② 未命中别名表 → 请求字节原样透传,零开销;
    /// ③ 命中 → params.name 改写为新名 + arguments 按映射变换,SDK 只见新名;
    /// ④ readonly:别名目标为 Model 档时,dryRun=true 放行(离线校验保底)、非 dryRun 拒绝;
    /// ⑤ 还原点 hook 对 dryRun 静态校验跳过。
    /// </summary>
    [Collection("ToolRiskStaticMode")]
    public class ToolAliasTests : IDisposable
    {
        public ToolAliasTests()
        {
            ToolAliases.ResetForTests();
            ToolRisk.Configure("full", null);
        }

        public void Dispose()
        {
            ToolAliases.ResetForTests();
            ToolRisk.Configure(null, null);
        }

        // ---------- 登记表不变式 ----------

        [Fact]
        public void 全部别名目标都在权限档位表登记()
        {
            Assert.Empty(ToolAliases.ValidateMappings());
        }

        [Fact]
        public void 生产映射_se_validate_features_路由到se_model_build并注入dryRun()
        {
            ToolAliases.AliasMapping m;
            Assert.True(ToolAliases.TryResolve("se_validate_features", out m));
            Assert.Equal("se_model_build", m.Target);
            var args = JsonNode.Parse("{\"features\":[]}").AsObject();
            m.TransformArguments(args);
            Assert.True((bool)args["dryRun"]);
        }

        [Fact]
        public void se_validate_features已不在工具注册面_内部保留给CLI()
        {
            var m = typeof(SolidEdge.Spy.McpServer.Tools.ModelingTools).GetMethod("se_validate_features");
            Assert.NotNull(m); // 内部方法保留(CLI --validate 走这里)
            // 不直接引用 MCP SDK(与 ToolRiskTests 同口径,用特性名字符串判)
            Assert.False(m.GetCustomAttributesData().Any(a => a.AttributeType.Name == "McpServerToolAttribute"),
                "se_validate_features 不应再出现在 MCP 工具注册面(别名层已接管)");
        }

        [Fact]
        public void 生产映射_se_model_state_snapshot_路由到se_snapshot_diff并注入scope()
        {
            ToolAliases.AliasMapping m;
            Assert.True(ToolAliases.TryResolve("se_model_state_snapshot", out m));
            Assert.Equal("se_snapshot_diff", m.Target);
            var args = JsonNode.Parse("{\"action\":\"diff\",\"name\":\"before_hole\"}").AsObject();
            m.TransformArguments(args);
            Assert.Equal("faces", (string)args["scope"]);
            Assert.Equal("diff", (string)args["action"]);   // 同名参数原样保留
        }

        [Fact]
        public void se_model_state_snapshot已不在工具注册面_内部保留给CLI与转调()
        {
            var m = typeof(SolidEdge.Spy.McpServer.Tools.ModelStateSnapshotTools).GetMethod("se_model_state_snapshot");
            Assert.NotNull(m); // 内部保留(CLI --modelsnap 与 faces 转调走这里)
            Assert.False(m.GetCustomAttributesData().Any(a => a.AttributeType.Name == "McpServerToolAttribute"),
                "se_model_state_snapshot 不应再出现在 MCP 工具注册面(别名层已接管)");
        }

        [Fact]
        public void 生产映射_se_verify_model_路由到se_read_model_state并改名expects()
        {
            ToolAliases.AliasMapping m;
            Assert.True(ToolAliases.TryResolve("se_verify_model", out m));
            Assert.Equal("se_read_model_state", m.Target);
            var args = JsonNode.Parse("{\"expects\":{\"od\":100},\"toleranceMm\":0.1,\"modelIndex\":2}").AsObject();
            m.TransformArguments(args);
            Assert.Null(args["expects"]);
            Assert.Equal(100, (int)args["verify"]["od"]);   // expects → verify 改名
            Assert.Equal(0.1, (double)args["toleranceMm"]); // 同名透传
            Assert.Equal(2, (int)args["modelIndex"]);
        }

        [Fact]
        public void se_verify_model已不在工具注册面_内部保留给CLI与转调()
        {
            var m = typeof(SolidEdge.Spy.McpServer.Tools.VerificationTools).GetMethod("se_verify_model");
            Assert.NotNull(m); // 内部保留(CLI --verify 与 verify 转调走这里)
            Assert.False(m.GetCustomAttributesData().Any(a => a.AttributeType.Name == "McpServerToolAttribute"),
                "se_verify_model 不应再出现在 MCP 工具注册面(别名层已接管)");
        }

        [Fact]
        public void 未命中别名表_原样解析()
        {
            ToolAliases.AliasMapping m;
            Assert.False(ToolAliases.TryResolve("se_model_build", out m));
            Assert.False(ToolAliases.TryResolve("se_unknown_tool", out m));
            Assert.False(ToolAliases.TryResolve(null, out m));
        }

        [Fact]
        public void 命中别名表_返回映射()
        {
            ToolAliases.AddMappingForTests("se_old_tool", "se_get_document",
                args => args["dryRun"] = true, "测试映射");
            ToolAliases.AliasMapping m;
            Assert.True(ToolAliases.TryResolve("se_old_tool", out m));
            Assert.Equal("se_get_document", m.Target);
            Assert.NotNull(m.TransformArguments);
        }

        // ---------- 参数变换(纯逻辑) ----------

        [Fact]
        public void 参数变换_注入与改名()
        {
            JsonObject args = JsonNode.Parse("{\"expects\":{\"od\":100},\"toleranceMm\":0.1}").AsObject();
            ToolAliases.AddMappingForTests("se_old", "se_new", a =>
            {
                if (a["expects"] != null)
                {
                    a["verify"] = a["expects"].DeepClone();
                    a.Remove("expects");
                }
                a["dryRun"] = true;
            }, "测试");
            ToolAliases.AliasMapping m;
            Assert.True(ToolAliases.TryResolve("se_old", out m));
            m.TransformArguments(args);
            Assert.Null(args["expects"]);
            Assert.Equal(100, (int)args["verify"]["od"]);
            Assert.Equal(0.1, (double)args["toleranceMm"]);
            Assert.True((bool)args["dryRun"]);
        }

        // ---------- PermissionTap 集成 ----------

        [Fact]
        public void PermissionTap_未命中别名_字节原样透传()
        {
            ToolRisk.Configure("full", null);
            string request = "{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"tools/call\",\"params\":{\"name\":\"se_get_document\",\"arguments\":{}}}";
            var tap = BuildTap(request + "\n", out MemoryStream sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));
            Assert.Contains(request, outText);
            Assert.Equal(0, sink.Length);
        }

        [Fact]
        public void PermissionTap_命中别名_改写为新名并注入参数()
        {
            ToolRisk.Configure("full", null);
            ToolAliases.AddMappingForTests("se_old_tool", "se_get_document",
                args => args["dryRun"] = true, "测试映射");
            string request = "{\"jsonrpc\":\"2.0\",\"id\":32,\"method\":\"tools/call\",\"params\":{\"name\":\"se_old_tool\",\"arguments\":{\"x\":1}}}";
            var tap = BuildTap(request + "\n", out MemoryStream sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));
            Assert.DoesNotContain("se_old_tool", outText);              // 旧名不下发
            Assert.Contains("\"name\":\"se_get_document\"", outText);    // 新名进 SDK
            Assert.Contains("\"dryRun\":true", outText);                 // 参数变换生效
            Assert.Contains("\"x\":1", outText);                         // 原有参数保留
            Assert.Equal(0, sink.Length);                                // 不是拒绝
        }

        [Fact]
        public void PermissionTap_只读模式别名到模型档_非dryRun被拒()
        {
            ToolRisk.Configure(null, "1");
            ToolAliases.AddMappingForTests("se_old_tool", "se_model_build", null, "测试映射");
            string request = "{\"jsonrpc\":\"2.0\",\"id\":33,\"method\":\"tools/call\",\"params\":{\"name\":\"se_old_tool\",\"arguments\":{}}}";
            var tap = BuildTap(request + "\n", out MemoryStream sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));
            Assert.DoesNotContain("se_old_tool", outText);
            string sinkText = Encoding.UTF8.GetString(sink.ToArray());
            Assert.Contains("\"id\":33", sinkText);
            Assert.Contains("\"isError\":true", sinkText);
            Assert.Contains("SE_MCP_MODE", sinkText);
        }

        [Fact]
        public void PermissionTap_只读模式别名到模型档_dryRun放行()
        {
            ToolRisk.Configure(null, "1");
            ToolAliases.AddMappingForTests("se_old_tool", "se_model_build",
                args => args["dryRun"] = true, "测试映射");
            string request = "{\"jsonrpc\":\"2.0\",\"id\":34,\"method\":\"tools/call\",\"params\":{\"name\":\"se_old_tool\",\"arguments\":{}}}";
            var tap = BuildTap(request + "\n", out MemoryStream sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));
            Assert.DoesNotContain("se_old_tool", outText);
            Assert.Contains("\"name\":\"se_model_build\"", outText);
            Assert.Contains("\"dryRun\":true", outText);
            Assert.Equal(0, sink.Length);
        }

        [Fact]
        public void PermissionTap_只读模式直调模型档带dryRun_放行()
        {
            ToolRisk.Configure(null, "1");
            string request = "{\"jsonrpc\":\"2.0\",\"id\":35,\"method\":\"tools/call\",\"params\":{\"name\":\"se_model_build\",\"arguments\":{\"dryRun\":true}}}";
            var tap = BuildTap(request + "\n", out MemoryStream sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));
            Assert.Contains(request, outText);   // 原样透传
            Assert.Equal(0, sink.Length);
        }

        [Fact]
        public void PermissionTap_只读模式直调模型档无dryRun_拒绝()
        {
            ToolRisk.Configure(null, "1");
            string request = "{\"jsonrpc\":\"2.0\",\"id\":36,\"method\":\"tools/call\",\"params\":{\"name\":\"se_model_build\",\"arguments\":{}}}";
            var tap = BuildTap(request + "\n", out MemoryStream sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));
            Assert.DoesNotContain("se_model_build", outText);
            Assert.Contains("\"isError\":true", Encoding.UTF8.GetString(sink.ToArray()));
        }

        // ---------- ToolRisk.Check 重载 ----------

        [Fact]
        public void Check_只读模式_se_model_build_dryRun特例()
        {
            ToolRisk.Configure(null, "1");
            Assert.Null(ToolRisk.Check("se_model_build", dryRun: true));
            Assert.NotNull(ToolRisk.Check("se_model_build", dryRun: false));
            Assert.NotNull(ToolRisk.Check("se_script_run", dryRun: true));   // Escape 档不吃特例
            Assert.Null(ToolRisk.Check("se_get_document", dryRun: false));

            ToolRisk.Configure("full", null);
            Assert.Null(ToolRisk.Check("se_model_build", dryRun: false));
        }

        // ---------- RestorePointHook dryRun 识别 ----------

        [Fact]
        public void RestorePointHook_识别dryRun标记()
        {
            Assert.True(RestorePointHook.IsDryRunToolCall(
                System.Text.Json.JsonDocument.Parse("{\"name\":\"se_model_build\",\"arguments\":{\"dryRun\":true}}").RootElement));
            Assert.False(RestorePointHook.IsDryRunToolCall(
                System.Text.Json.JsonDocument.Parse("{\"name\":\"se_model_build\",\"arguments\":{}}").RootElement));
            Assert.False(RestorePointHook.IsDryRunToolCall(
                System.Text.Json.JsonDocument.Parse("{\"name\":\"se_model_build\",\"dryRun\":\"yes\"}").RootElement));
        }

        // ---------- 基建 ----------

        private static PermissionTap BuildTap(string inputText, out MemoryStream sink)
        {
            var input = new MemoryStream(Encoding.UTF8.GetBytes(inputText));
            sink = new MemoryStream();
            return new PermissionTap(input, sink);
        }

        private static byte[] ReadAll(Stream tap)
        {
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int n;
                while ((n = tap.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, n);
                }
                return output.ToArray();
            }
        }
    }
}
