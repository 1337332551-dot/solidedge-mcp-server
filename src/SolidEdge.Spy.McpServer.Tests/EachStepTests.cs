using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// se_invoke_chain 步骤级 each 的占位符替换(纯函数,不碰 COM)。
    /// each 的价值就在于"拼错要看得见"——所以缺键/未闭合/大小写不符都必须**原样保留**占位符,
    /// 绝不能静默变成空串(否则会写出 "Edit 一个叫空字符串的变量" 这种更难查的错)。
    /// </summary>
    public class EachStepTests
    {
        private static JsonElement El(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                return doc.RootElement.Clone();
            }
        }

        [Fact]
        public void 对象行_取同名字段()
        {
            Assert.Equal("Pipe_OD", InvokeTools.ExpandEachToken("{{n}}", El(@"{""n"":""Pipe_OD""}")));
        }

        [Fact]
        public void 一行多个占位符_前后缀保留()
        {
            Assert.Equal("Pipe_ID=81 mm",
                InvokeTools.ExpandEachToken("{{n}}={{v}}", El(@"{""n"":""Pipe_ID"",""v"":""81 mm""}")));
        }

        [Fact]
        public void 同一占位符多次出现_都替换()
        {
            Assert.Equal("a-a", InvokeTools.ExpandEachToken("{{n}}-{{n}}", El(@"{""n"":""a""}")));
        }

        [Fact]
        public void 数字字段_按JSON原样转文本()
        {
            Assert.Equal("89", InvokeTools.ExpandEachToken("{{v}}", El(@"{""v"":89}")));
        }

        [Fact]
        public void 标量行_下划线取值()
        {
            Assert.Equal("89 mm", InvokeTools.ExpandEachToken("{{_}}", El(@"""89 mm""")));
            Assert.Equal("89 mm", InvokeTools.ExpandEachToken("{{value}}", El(@"""89 mm""")));
        }

        [Fact]
        public void 标量行_数字()
        {
            Assert.Equal("3", InvokeTools.ExpandEachToken("{{_}}", El("3")));
        }

        [Fact]
        public void 标量行_用非下划线的键_原样保留()
        {
            Assert.Equal("{{n}}", InvokeTools.ExpandEachToken("{{n}}", El(@"""x""")));
        }

        [Fact]
        public void 缺键_原样保留占位符()
        {
            Assert.Equal("x{{nope}}y", InvokeTools.ExpandEachToken("x{{nope}}y", El(@"{""n"":""a""}")));
        }

        [Fact]
        public void 键名大小写敏感_不符就保留()
        {
            Assert.Equal("{{N}}", InvokeTools.ExpandEachToken("{{N}}", El(@"{""n"":""a""}")));
        }

        [Fact]
        public void 未闭合_原样保留()
        {
            Assert.Equal("x{{n", InvokeTools.ExpandEachToken("x{{n", El(@"{""n"":""a""}")));
        }

        [Fact]
        public void 没有占位符_原样返回()
        {
            Assert.Equal("89 mm", InvokeTools.ExpandEachToken("89 mm", El(@"{""n"":""a""}")));
        }

        [Fact]
        public void 空串与null_原样返回()
        {
            Assert.Equal("", InvokeTools.ExpandEachToken("", El(@"{""n"":""a""}")));
            Assert.Null(InvokeTools.ExpandEachToken(null, El(@"{""n"":""a""}")));
        }

        [Fact]
        public void 占位符里带空格_键名会Trim()
        {
            Assert.Equal("a", InvokeTools.ExpandEachToken("{{ n }}", El(@"{""n"":""a""}")));
        }
    }
}
