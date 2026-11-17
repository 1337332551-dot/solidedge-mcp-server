using SolidEdge.Spy.McpServer;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests;

/// <summary>
/// P1(2026-11-14) 句柄探活分流测试:钉死"哪些 hr 判死、哪些放过"——
/// 误杀正常句柄比漏报失效更糟,这张表是唯一裁决标准。
/// </summary>
public sealed class HandleLivenessTests
{
    [Theory]
    [InlineData(unchecked((int)0x800401FD))]   // CO_E_OBJNOTCONNECTED
    [InlineData(unchecked((int)0x80010108))]   // RPC_E_DISCONNECTED
    [InlineData(unchecked((int)0x800706BA))]   // RPC_S_SERVER_UNAVAILABLE
    [InlineData(unchecked((int)0x800706BE))]   // RPC_S_CALL_FAILED
    public void HrMeansDead_断连家族_判死(int hr)
    {
        Assert.True(HandleLiveness.HrMeansDead(hr));
    }

    [Theory]
    [InlineData(unchecked((int)0x80010001))]   // RPC_E_CALL_REJECTED:忙,不是死
    [InlineData(unchecked((int)0x8001010A))]   // RPC_E_SERVERCALL_RETRYLATER:忙
    [InlineData(unchecked((int)0x80004005))]   // E_FAIL:语义含混(可能是对象重建),不判
    [InlineData(unchecked((int)0x80020006))]   // DISP_E_UNKNOWNNAME:参数类
    [InlineData(unchecked((int)0x80020003))]   // DISP_E_MEMBERNOTFOUND:参数类
    [InlineData(unchecked((int)0x80020009))]   // DISP_E_EXCEPTION:包装
    public void HrMeansDead_忙与参数与含混码_放过(int hr)
    {
        Assert.False(HandleLiveness.HrMeansDead(hr));
    }

    [Fact]
    public void StaleMessage_带句柄号类型注册文档与恢复动作()
    {
        var h = new ObjectHandle(null, "PartDocument", "零件1", @"E:\x\y.par");
        string msg = HandleLiveness.StaleMessage("obj-7", h);
        Assert.Contains("obj-7", msg);
        Assert.Contains("PartDocument", msg);
        Assert.Contains(@"E:\x\y.par", msg);
        Assert.Contains("se_get_selection", msg);
        Assert.Contains("不要继续用旧 obj-N", msg);
    }

    [Fact]
    public void StaleMessage_句柄元数据缺失_也能成句()
    {
        string msg = HandleLiveness.StaleMessage("obj-2", null);
        Assert.Contains("obj-2", msg);
        Assert.Contains("se_get_selection", msg);
    }
}
