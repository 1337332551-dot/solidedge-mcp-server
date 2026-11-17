using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 晚绑定兜底的实参转换(纯函数,不碰 COM)。
    /// 背景:工程图明细表(Table/PartsList)拿不到类型信息时,se_invoke_member 曾直接报
    /// "无法读取对象(__ComObject)的类型信息" 死路;现在退回 IDispatch 晚绑定,参数只能靠字面量猜类型。
    /// 猜错了调用会 TYPEMISMATCH,所以这里把"怎么猜"钉死:整数→int、小数→double、真假→bool、其余原样。
    /// </summary>
    public class LateBoundArgsTests
    {
        [Fact]
        public void 整数按int传()
        {
            object v = InvokeTools.ConvertLateBoundValue("12");
            Assert.IsType<int>(v);
            Assert.Equal(12, v);
        }

        [Fact]
        public void 小数按double传()
        {
            object v = InvokeTools.ConvertLateBoundValue("0.005");
            Assert.IsType<double>(v);
            Assert.Equal(0.005, (double)v, 12);
        }

        [Fact]
        public void 真假按bool传()
        {
            Assert.Equal(true, InvokeTools.ConvertLateBoundValue("True"));
            Assert.Equal(false, InvokeTools.ConvertLateBoundValue("false"));
        }

        [Fact]
        public void 一和零不当作bool()
        {
            // "1"/"0" 保持整数:SE 里大量索引/枚举参数是数字,当 bool 传反而会 TYPEMISMATCH
            Assert.IsType<int>(InvokeTools.ConvertLateBoundValue("1"));
            Assert.IsType<int>(InvokeTools.ConvertLateBoundValue("0"));
        }

        [Fact]
        public void 零件号这类文本原样保留()
        {
            Assert.Equal("1PEZ24304701-025", InvokeTools.ConvertLateBoundValue("1PEZ24304701-025"));
        }

        [Fact]
        public void 前后空格不影响数字判定()
        {
            object v = InvokeTools.ConvertLateBoundValue("  7 ");
            Assert.IsType<int>(v);
            Assert.Equal(7, v);
        }

        [Fact]
        public void 空串原样传()
        {
            Assert.Equal("", InvokeTools.ConvertLateBoundValue(""));
        }
    }
}
