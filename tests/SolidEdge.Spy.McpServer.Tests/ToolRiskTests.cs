using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidEdge.Spy.McpServer;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 权限洋葱外层(模式×档位)测试。
    /// 核心合同:
    /// ① 全部 [McpServerTool] 方法都登记在 ToolRisk 表里(反射对账,防新工具漏登记被 fail-closed 裸拒);
    /// ② readonly 模式只放 Read 档,其余档拒绝且必须带切换指路(SE_MCP_MODE);
    /// ③ 未登记工具 fail-closed 拒绝;
    /// ④ PermissionTap 在传输层拦截:被拒请求不进 SDK,伪造 isError 结果;放行请求字节原样透传。
    /// </summary>
    public class ToolRiskTests : IDisposable
    {
        public void Dispose()
        {
            // 恢复默认,避免污染其它测试
            ToolRisk.Configure(null, null);
        }

        // ---------- 登记表完整性(反射对账) ----------

        [Fact]
        public void 全部McpServerTool方法都已登记()
        {
            var asm = typeof(Guardrail).Assembly;
            var toolMethods = asm.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                .Where(m => m.GetCustomAttributesData().Any(a => a.AttributeType.Name == "McpServerToolAttribute"))
                .Select(m => m.Name)
                .Distinct()
                .ToList();
            Assert.True(toolMethods.Count >= 21, "工具数量异常减少: " + toolMethods.Count);
            foreach (var name in toolMethods)
            {
                Assert.True(ToolRisk.TierOf(name).HasValue, "[McpServerTool] 未在 ToolRisk 登记表: " + name);
            }
        }

        [Fact]
        public void 登记表恰好22个工具()
        {
            // 21(合流前) + se_assembly_build + se_assembly_query(远端装配域,2026-09-22)
            // - se_extrude_on_face(2026-10-05 用户拍板删除,能力由 se_model_build 的 face 锚定覆盖)
            Assert.Equal(22, ToolRisk.RegisteredTools.Count());
        }

        [Theory]
        [InlineData("se_get_document", "Read")]
        [InlineData("se_get_selection", "Read")]
        [InlineData("se_find_paths", "Read")]
        [InlineData("se_describe_object", "Read")]
        [InlineData("se_walk_object", "Read")]
        [InlineData("se_batch_read", "Read")]
        [InlineData("se_read_geometry", "Read")]
        [InlineData("se_get_variables", "Read")]
        [InlineData("se_view_context", "Read")]
        [InlineData("se_capture_viewport", "Read")]
        [InlineData("se_snapshot_diff", "Read")]
        [InlineData("se_validate_features", "Read")]
        [InlineData("se_open_document", "Session")]
        [InlineData("se_new_document", "Session")]
        [InlineData("se_close_document", "Session")]
        [InlineData("se_model_build", "Model")]
        [InlineData("se_invoke_member", "Model")]
        [InlineData("se_invoke_chain", "Model")]
        [InlineData("se_recipe_run", "Model")]
        [InlineData("se_script_run", "Escape")]
        public void 档位表内容(string tool, string tierName)
        {
            Assert.Equal(Enum.Parse(typeof(RiskTier), tierName), ToolRisk.TierOf(tool));
        }

        // ---------- 模式解析(Configure) ----------

        [Fact]
        public void 默认无环境变量为full模式()
        {
            ToolRisk.Configure(null, null);
            Assert.Equal(McpMode.Full, ToolRisk.Mode);
        }

        [Fact]
        public void 旧SE_MCP_READONLY兼容映射readonly()
        {
            ToolRisk.Configure(null, "1");
            Assert.Equal(McpMode.ReadOnly, ToolRisk.Mode);
            ToolRisk.Configure(null, "true");
            Assert.Equal(McpMode.ReadOnly, ToolRisk.Mode);
        }

        [Fact]
        public void SE_MCP_MODE优先于旧变量()
        {
            ToolRisk.Configure("full", "1");
            Assert.Equal(McpMode.Full, ToolRisk.Mode);
            ToolRisk.Configure("readonly", null);
            Assert.Equal(McpMode.ReadOnly, ToolRisk.Mode);
        }

        [Fact]
        public void SE_MCP_MODE未识别值_fail_closed按只读()
        {
            ToolRisk.Configure("FullExtra", null);
            Assert.Equal(McpMode.ReadOnly, ToolRisk.Mode);
            Assert.NotNull(ToolRisk.Check("se_model_build"));
        }

        // ---------- 门禁(Check) ----------

        [Fact]
        public void Full模式_全部档位放行()
        {
            ToolRisk.Configure("full", null);
            foreach (var tool in ToolRisk.RegisteredTools)
            {
                Assert.Null(ToolRisk.Check(tool));
            }
            // 未登记工具在 full 模式也放行?——不,full 模式直接放行所有(与历史行为一致,权限层只在 readonly 起作用)
            Assert.Null(ToolRisk.Check("se_unknown_tool"));
        }

        [Fact]
        public void ReadOnly模式_只放Read档_其余拒绝且带指路()
        {
            ToolRisk.Configure(null, "1");
            foreach (var tool in ToolRisk.RegisteredTools.Where(t => ToolRisk.TierOf(t) == RiskTier.Read))
            {
                Assert.Null(ToolRisk.Check(tool));
            }
            foreach (var tool in ToolRisk.RegisteredTools.Where(t => ToolRisk.TierOf(t) != RiskTier.Read))
            {
                var err = ToolRisk.Check(tool);
                Assert.NotNull(err);
                Assert.Contains("SE_MCP_MODE", err);   // 必须带切换指路
                Assert.Contains(tool, err);            // 拒绝信息指名工具
            }
        }

        [Fact]
        public void ReadOnly模式_未登记工具fail_closed拒绝()
        {
            ToolRisk.Configure(null, "1");
            var err = ToolRisk.Check("se_unknown_tool");
            Assert.NotNull(err);
            Assert.Contains("未在权限档位表登记", err);
        }

        // ---------- engineer(机械工程师)模式 ----------

        [Fact]
        public void Engineer模式_解析与环境变量()
        {
            ToolRisk.Configure("engineer", null);
            Assert.Equal(McpMode.Engineer, ToolRisk.Mode);
            ToolRisk.Configure("Engineer", null);   // 大小写不敏感
            Assert.Equal(McpMode.Engineer, ToolRisk.Mode);
            ToolRisk.Configure("engineerX", null);  // 未识别仍 fail-closed 按只读
            Assert.Equal(McpMode.ReadOnly, ToolRisk.Mode);
        }

        [Fact]
        public void Engineer模式_工具级门禁_全部登记工具放行()
        {
            ToolRisk.Configure("engineer", null);
            foreach (var tool in ToolRisk.RegisteredTools)
            {
                Assert.Null(ToolRisk.Check(tool));
            }
        }

        [Fact]
        public void Engineer模式_成员过滤_显式读取白名单放行()
        {
            ToolRisk.Configure("engineer", null);
            // 属性式读取:旧规则只认 get 前缀,把它们全误拦(engineer 下配方不可用,2026-10-05 整改)
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "Models"));
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "Count"));
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "Item"));
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "Body"));
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "Name"));
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "Status"));
            // 方法式读取:get 前缀兜底
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "get_variables"));   // 小写 get_ 前缀
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "GetVariables"));    // 大写 Get 前缀
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "GetRelated"));      // 无下划线的 SE 读取 API
            // 写操作仍一律拒绝
            Assert.NotNull(ToolRisk.CheckMember("se_invoke_member", "AddFiniteExtrudedProtrusion"));
            Assert.NotNull(ToolRisk.CheckMember("se_invoke_member", "TargetGetInfo"));  // 中缀含 get 不放行,只认前缀
            Assert.NotNull(ToolRisk.CheckMember("se_invoke_chain", "SetValue"));
            var err = ToolRisk.CheckMember("se_invoke_member", "Delete");
            Assert.Contains("get", err);   // 拒绝信息带白名规则指路
        }

        [Fact]
        public void Engineer模式_成员过滤_仅作用于自由调用通道()
        {
            ToolRisk.Configure("engineer", null);
            Assert.Null(ToolRisk.CheckMember("se_recipe_run", "Add"));     // 配方走工具级门禁,不做成员过滤
            Assert.Null(ToolRisk.CheckMember("se_model_build", "Add"));
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", null));   // 空成员交由 Guardrail 处理
        }

        [Fact]
        public void Engineer模式_成员过滤_其他模式不生效()
        {
            ToolRisk.Configure("full", null);
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "Add"));
            ToolRisk.Configure(null, "1");
            Assert.Null(ToolRisk.CheckMember("se_invoke_member", "Add"));   // readonly 下整工具已在工具级拦,成员级不重复生效
        }

        // ---------- PermissionTap(传输层拦截) ----------

        [Fact]
        public void PermissionTap_只读模式拒绝写工具并伪造结果_放行查询()
        {
            ToolRisk.Configure(null, "1");
            string denied = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"se_model_build\",\"arguments\":{}}}";
            string allowed = "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"se_get_document\",\"arguments\":{}}}";
            var input = new MemoryStream(Encoding.UTF8.GetBytes(denied + "\n" + allowed + "\n"));
            var sink = new MemoryStream();
            var tap = new PermissionTap(input, sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));

            Assert.DoesNotContain("se_model_build", outText);   // 被拒请求不进 SDK
            Assert.Contains(allowed, outText);                  // 放行请求字节原样透传
            string sinkText = Encoding.UTF8.GetString(sink.ToArray());
            Assert.Contains("\"id\":7", sinkText);
            Assert.Contains("\"isError\":true", sinkText);
            Assert.Contains("SE_MCP_MODE", sinkText);           // 拒绝信息带切换指路
        }

        [Fact]
        public void PermissionTap_只读模式未登记工具被拦()
        {
            ToolRisk.Configure(null, "1");
            string denied = "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"se_unknown_tool\",\"arguments\":{}}}";
            var input = new MemoryStream(Encoding.UTF8.GetBytes(denied + "\n"));
            var sink = new MemoryStream();
            var tap = new PermissionTap(input, sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));

            Assert.DoesNotContain("se_unknown_tool", outText);
            string sinkText = Encoding.UTF8.GetString(sink.ToArray());
            Assert.Contains("未在权限档位表登记", sinkText);
        }

        [Fact]
        public void PermissionTap_full模式全部透传_响应流无伪造()
        {
            ToolRisk.Configure("full", null);
            string request = "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"se_model_build\",\"arguments\":{}}}";
            var input = new MemoryStream(Encoding.UTF8.GetBytes(request + "\n"));
            var sink = new MemoryStream();
            var tap = new PermissionTap(input, sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));

            Assert.Contains(request, outText);
            Assert.Equal(0, sink.Length);
        }

        [Fact]
        public void PermissionTap_非tools_call行原样透传()
        {
            ToolRisk.Configure(null, "1");
            string init = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"0\"}}}";
            string notify = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";
            var input = new MemoryStream(Encoding.UTF8.GetBytes(init + "\n" + notify + "\n"));
            var sink = new MemoryStream();
            var tap = new PermissionTap(input, sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));

            Assert.Contains(init, outText);
            Assert.Contains(notify, outText);
            Assert.Equal(0, sink.Length);
        }

        [Fact]
        public void PermissionTap_跨块到达的行同样被拦()
        {
            ToolRisk.Configure(null, "1");
            string denied = "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"se_script_run\",\"arguments\":{}}}";
            var bytes = Encoding.UTF8.GetBytes(denied + "\n");
            // 把请求行拆成三段喂给内层流:逐段读取时 tap 必须等整行到齐才判定
            int cut1 = Math.Min(40, bytes.Length);
            int cut2 = Math.Min(cut1 + 60, bytes.Length);
            var inner = new MemoryStream();
            inner.Write(bytes, 0, cut1); inner.Position = 0;
            var rest = new MemoryStream();
            rest.Write(bytes, cut1, cut2 - cut1);
            rest.Write(bytes, cut2, bytes.Length - cut2);
            rest.Position = 0;
            var multi = new ConcatStream(inner, rest);
            var sink = new MemoryStream();
            var tap = new PermissionTap(multi, sink);

            string outText = Encoding.UTF8.GetString(ReadAll(tap));

            Assert.DoesNotContain("se_script_run", outText);
            Assert.Contains("\"id\":21", Encoding.UTF8.GetString(sink.ToArray()));
        }

        private static byte[] ReadAll(Stream s)
        {
            var buf = new byte[8192];
            using (var ms = new MemoryStream())
            {
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        /// <summary>顺序拼接两个流,第一个读完接第二个(测跨块切分)。</summary>
        private sealed class ConcatStream : Stream
        {
            private readonly Stream _first;
            private readonly Stream _second;
            private bool _firstDone;

            public ConcatStream(Stream first, Stream second)
            {
                _first = first;
                _second = second;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!_firstDone)
                {
                    int n = _first.Read(buffer, offset, count);
                    if (n > 0)
                    {
                        return n;
                    }
                    _firstDone = true;
                }
                return _second.Read(buffer, offset, count);
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
