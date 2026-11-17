using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 错误码目录(CodeCatalog)回归测试(2026-10-15 新增,P1-1)。
    ///
    /// 数据是【嵌入资源】se_code_catalog.json(生成脚本 scripts/gen_code_catalog.py),
    /// 所以这组测试同时是一道哨兵:一旦构建漏了 EmbeddedResource,这里立刻红——
    /// 而不是等上线后 AI 又拿到一堆裸码,才发现码表压根没进去。
    ///
    /// 两类码各测:COM/Win32 HRESULT(E_FAIL 一类)+ Solid Edge 状态枚举(特征五档)。
    /// 查不到时的契约是【返回 null,不猜不编】,这里一并钉死。
    /// </summary>
    public class CodeCatalogTests
    {
        private const int EFail = unchecked((int)0x80004005);
        private const int RpcDisconnected = unchecked((int)0x80010108);
        private const int AccessDenied = unchecked((int)0x80070005);

        [Fact]
        public void FindHResult_E_FAIL_返回官方名与中文()
        {
            CodeInfo info = CodeCatalog.FindHResult(EFail);
            Assert.NotNull(info);
            Assert.Equal("E_FAIL", info.Name);
            Assert.False(string.IsNullOrEmpty(info.Meaning));
            Assert.Contains("未指定", info.Meaning);
        }

        [Fact]
        public void FindHResult_多个常见码_均能查到()
        {
            Assert.Equal("RPC_E_DISCONNECTED", CodeCatalog.FindHResult(RpcDisconnected).Name);
            Assert.Equal("E_ACCESSDENIED", CodeCatalog.FindHResult(AccessDenied).Name);
            Assert.Equal("DISP_E_UNKNOWNNAME", CodeCatalog.FindHResult(unchecked((int)0x80020006)).Name);
            Assert.Equal("CO_E_OBJNOTCONNECTED", CodeCatalog.FindHResult(unchecked((int)0x800401FD)).Name);
            Assert.Equal("RPC_S_SERVER_UNAVAILABLE", CodeCatalog.FindHResult(unchecked((int)0x800706BA)).Name);
        }

        [Fact]
        public void DescribeHResult_含码_名_人话()
        {
            string s = CodeCatalog.DescribeHResult(RpcDisconnected);
            Assert.NotNull(s);
            Assert.Contains("0x80010108", s);
            Assert.Contains("RPC_E_DISCONNECTED", s);
        }

        [Fact]
        public void DescribeHResult_表外码_不抛异常()
        {
            // 0x00001234 不是常见 HRESULT;契约是"能查到就查,查不到退系统消息或 null",绝不抛。
            string s = CodeCatalog.DescribeHResult(unchecked((int)0x00001234));
            Assert.True(s == null || s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void FindHResult_表外码_返回null()
        {
            Assert.Null(CodeCatalog.FindHResult(unchecked((int)0x00001234)));
        }

        [Theory]
        [InlineData(1216476310, "igFeatureOK")]
        [InlineData(1216476311, "igFeatureFailed")]
        [InlineData(1216476312, "igFeatureWarned")]
        [InlineData(1216476313, "igFeatureSuppressed")]
        [InlineData(1216476314, "igFeatureRolledBack")]
        public void DescribeSeEnum_特征五档_齐全(long value, string expectedName)
        {
            string s = CodeCatalog.DescribeSeEnum("FeatureStatusConstants", value);
            Assert.NotNull(s);
            Assert.Contains(expectedName, s);
        }

        [Fact]
        public void DescribeSeEnum_未知枚举或成员_返回null()
        {
            Assert.Null(CodeCatalog.DescribeSeEnum("NoSuchEnumConstants", 1));
            Assert.Null(CodeCatalog.DescribeSeEnum("FeatureStatusConstants", 999999));
            Assert.Null(CodeCatalog.DescribeSeEnum(null, 1));
        }

        [Fact]
        public void SeEnumMeaning_特征状态_有中文说明()
        {
            string m = CodeCatalog.SeEnumMeaning("FeatureStatusConstants");
            Assert.False(string.IsNullOrEmpty(m));
        }

        /// <summary>
        /// 接入点验证:项目的异常描述函数(AssemblySpec.DescribeEx)喂进带 HRESULT 的 COM 异常后,
        /// 输出里必须出现"官方名"而不只是裸码 —— 这才是 P1-1 真正让 AI 受益的地方。
        /// </summary>
        [Fact]
        public void AssemblySpec_DescribeEx_HRESULT带官方名()
        {
            var ex = new System.Runtime.InteropServices.COMException("测试用异常", unchecked((int)0x80004005));
            string s = AssemblySpec.DescribeEx(ex);
            Assert.Contains("0x80004005", s);
            Assert.Contains("E_FAIL", s);
        }

        [Fact]
        public void AssemblySpec_DescribeEx_内层异常也带人话()
        {
            var inner = new System.Runtime.InteropServices.COMException("内层", unchecked((int)0x80010108));
            var outer = new System.InvalidOperationException("外层", inner);
            string s = AssemblySpec.DescribeEx(outer);
            Assert.Contains("RPC_E_DISCONNECTED", s);
        }

        // ---------- P2-2: action 字段(下一步动作,2026-11-13) ----------

        [Fact]
        public void DescribeHResultAction_E_FAIL_有下一步动作()
        {
            string a = CodeCatalog.DescribeHResultAction(EFail);
            Assert.False(string.IsNullOrEmpty(a));
            Assert.Contains("重新取对象", a);
        }

        [Fact]
        public void DescribeHResultAction_连接与占用类码_各带专属指引()
        {
            Assert.Contains("完整性级别", CodeCatalog.DescribeHResultAction(unchecked((int)0x800401E3)));
            Assert.Contains("模态框", CodeCatalog.DescribeHResultAction(unchecked((int)0x80010001)));
            Assert.Contains("重新", CodeCatalog.DescribeHResultAction(unchecked((int)0x800401FD)));
        }

        [Fact]
        public void DescribeHResultAction_无动作码_返回null不编()
        {
            Assert.Null(CodeCatalog.DescribeHResultAction(unchecked((int)0x00001234)));
            Assert.Null(CodeCatalog.DescribeHResultAction(AccessDenied));   // 高频但未配 action
        }

        [Fact]
        public void FindHResult_SE专用码0x80040225_已收录()
        {
            CodeInfo info = CodeCatalog.FindHResult(unchecked((int)0x80040225));
            Assert.NotNull(info);
            Assert.False(string.IsNullOrEmpty(info.Meaning));
        }

        [Fact]
        public void DescribeException_E_FAIL_末尾带怎么办()
        {
            var ex = new System.Runtime.InteropServices.COMException("测试", EFail);
            string s = EvidenceContract.DescribeException(ex);
            Assert.Contains("怎么办", s);
            Assert.Contains("重新取对象", s);
        }

        [Fact]
        public void DescribeException_内层码的动作_也能带出()
        {
            var inner = new System.Runtime.InteropServices.COMException("内层", unchecked((int)0x800401FD));
            var outer = new System.Reflection.TargetInvocationException("外层", inner);
            string s = EvidenceContract.DescribeException(outer);
            Assert.Contains("怎么办", s);
        }

        [Fact]
        public void DescribeException_无动作异常_不带怎么办段()
        {
            var ex = new System.Runtime.InteropServices.COMException("测试", AccessDenied);
            string s = EvidenceContract.DescribeException(ex);
            Assert.DoesNotContain("怎么办", s);
        }
    }
}
