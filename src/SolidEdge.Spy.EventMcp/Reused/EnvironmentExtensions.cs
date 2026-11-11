using System;

namespace SolidEdge.Spy.Extensions
{
    /// <summary>
    /// 从 SolidEdgeExtensions.cs 提取的两个纯 interop 扩展(链接进来的 CommandHelper 需要)。
    ///
    /// 为什么不直接链接整个 SolidEdgeExtensions.cs:其 GetCommandType() 依赖
    /// SolidEdgeSDK.EnvironmentCategories(来自 SolidEdge.Community NuGet 包,EventMcp 未引用)。
    /// 而 CommandHelper.ResolveCommandId 只用到 GetActiveEnvironment() 与 GetGuid() 两个方法。
    ///
    /// 命名空间必须保持 SolidEdge.Spy.Extensions,这样链接的 CommandHelper.cs 零改动即可编译。
    /// </summary>
    internal static class EnvironmentExtensions
    {
        /// <summary>取应用当前活动环境对象(原 SolidEdgeExtensions.cs:16,纯 interop)。</summary>
        public static SolidEdgeFramework.Environment GetActiveEnvironment(this SolidEdgeFramework.Application application)
        {
            SolidEdgeFramework.Environments environments = application.Environments;
            return environments.Item(application.ActiveEnvironment);
        }

        /// <summary>取环境 CATID(原 SolidEdgeExtensions.cs:133,纯 interop)。</summary>
        public static Guid GetGuid(this SolidEdgeFramework.Environment environment)
        {
            return new Guid(environment.CATID);
        }
    }
}
