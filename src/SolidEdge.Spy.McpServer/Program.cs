using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer
{
    /// <summary>
    /// Solid Edge MCP Server 入口。
    /// 通过 stdio 与 AI 客户端(CodeBuddy/Cursor/Claude)通信,
    /// 内部独立连接运行中的 Solid Edge 实例。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        internal static async Task Main(string[] args)
        {
            // 必须是 STA 线程(Solid Edge COM 是单线程单元模型)。
            // 整个 server 在这个 STA 线程上跑,所有 COM 调用都在同一线程。

            // 命令行模式:有参数时作为一次性 CLI 工具运行,输出 JSON 后退出。
            // 这样不依赖 MCP 客户端也能直接查询 Solid Edge,便于调试和复用。
            // 注意:async Task Main 的入口线程不保证是 STA,而 Solid Edge COM 需要 STA,
            // 所以 CLI 逻辑必须在显式创建的 STA 线程上执行。
            // --version:打印构建时间戳后退出(P0-1:防"改了代码但跑的仍是旧二进制"的假部署)
            if (args != null && Array.Exists(args, a => a == "--version" || a == "-v"))
            {
                Console.WriteLine(SolidEdge.Spy.McpServer.Telemetry.BuildInfo.Describe("solidedge-mcp"));
                return;
            }

            if (args != null && args.Length > 0)
            {
                int code = 1;
                var t = new System.Threading.Thread(() => { code = CliRunner.Run(args); });
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
                t.Join();
                Environment.ExitCode = code;
                return;
            }

            // 无参数:进入 MCP server(stdio 传输)模式
            await RunServerAsync(args);
        }

        private static async Task RunServerAsync(string[] args)
        {
            // ⚠️ 关键:改用 WithStreamServerTransport 后,SDK 不再替我们把 Console 输出改道,
            // 必须手动把 Console.Out 指向 stderr,否则宿主日志会混进 stdout 的 MCP 协议通道。
            // (tap 持有 Console.OpenStandardOutput() 的原始流句柄,不受 SetOut 影响,协议照常写 stdout。)
            Console.SetOut(Console.Error);
            Console.Error.WriteLine(SolidEdge.Spy.McpServer.Telemetry.BuildInfo.Describe("solidedge-mcp"));

            // 权限洋葱外层:模式 × 工具档位。SE_MCP_MODE=readonly|engineer|full(readonly 只放 Read 档工具,
            // 其余档在 tools/call 层拒绝并带切换指路;engineer 工具档全放但自由调用通道限 get 前缀成员,见 ToolRisk.CheckMember);
            // 旧 SE_MCP_READONLY=1 兼容映射 readonly;
            // SE_MCP_MODE 给了未识别值则 fail-closed 按只读。默认 full(与历史行为一致)。
            // 内层成员级护栏(Guardrail)的只读开关随模式联动:readonly 模式下整工具已在外层拦,这里是双保险。
            // ⚠️ 必须 BEFORE builder.Build():SDK 传输层构造后立刻开始读 stdin,请求可能在
            //    Configure 之前到达(真机冒烟实锤:传输层 15ms 内就处理了 tools/call,而 Configure 还没跑)。
            string modeNote = SolidEdge.Spy.McpServer.Tools.ToolRisk.Configure(
                Environment.GetEnvironmentVariable("SE_MCP_MODE"),
                Environment.GetEnvironmentVariable("SE_MCP_READONLY"));
            SolidEdge.Spy.McpServer.Tools.Guardrail.ReadOnlyEnabled = SolidEdge.Spy.McpServer.Tools.ToolRisk.Mode == SolidEdge.Spy.McpServer.Tools.McpMode.ReadOnly;
            Console.Error.WriteLine("Permission mode: " + modeNote);

            var builder = Host.CreateApplicationBuilder(args);

            // 配置日志(写到 stderr,不干扰 stdio 上的 MCP 协议)
            builder.Logging.AddDebug();
            builder.Logging.SetMinimumLevel(LogLevel.Information);

            // 注册 MCP server。传输用"自定义流"而非 WithStdioServerTransport:
            // 请求侧挂两层——最外层 PermissionTap(权限洋葱:tools/call 进 SDK 前按 模式×档位 门禁,
            // 被拒调用不执行工具体,直接伪造 isError 结果回给 AI),内层 JsonRpcTap(计量旁路);
            // 响应侧 stdoutTap 既是协议出口,也作权限层写"拒绝响应"的汇入点。
            // 工具代码零改动(见 PermissionTap.cs / Telemetry/JsonRpcTap.cs;绝不向 stdout 打日志)。
            var stdoutTap = new SolidEdge.Spy.McpServer.Telemetry.JsonRpcTap(Console.OpenStandardOutput(), isRequestSide: false, source: "mcp");
            var mcpBuilder = builder.Services
                .AddMcpServer()
                .WithStreamServerTransport(
                    new SolidEdge.Spy.McpServer.PermissionTap(
                        new SolidEdge.Spy.McpServer.Telemetry.JsonRpcTap(Console.OpenStandardInput(), isRequestSide: true, source: "mcp"),
                        stdoutTap),
                    stdoutTap);

            // 只读模式:非 Read 档工具在注册期即被滤除,不进 tools/list——工具描述(含
            // 参数 schema)完全不暴露给 AI,而不是"列表可见、调用被拒"。被滤工具即使
            // 通过客户端缓存的旧列表硬调,PermissionTap/ToolRisk.Check 仍在 tools/call
            // 层拒绝(fail-closed),纵深防御不变。其余模式仍走 WithToolsFromAssembly。
            //
            // 实现要点:工具必须**延迟创建**(AddSingleton<McpServerTool> 工厂 lambda,
            // 首次解析时才 Create),并把宿主 IServiceProvider 传进 McpServerToolCreateOptions
            // .Services——否则 SolidEdgeContext 这类注入参数会被当成必填 JSON 参数,
            // 调用时抛 "missing a value for the required parameter 'context'"(2026-09-28 实测)。
            // WithToolsFromAssembly 内部即等价于此做法。
            if (SolidEdge.Spy.McpServer.Tools.ToolRisk.Mode == SolidEdge.Spy.McpServer.Tools.McpMode.ReadOnly)
            {
                int exposed = 0;
                foreach (System.Reflection.MethodInfo toolMethod in EnumerateVisibleToolMethods())
                {
                    System.Reflection.MethodInfo captured = toolMethod;
                    mcpBuilder.Services.AddSingleton<ModelContextProtocol.Server.McpServerTool>(serviceProvider =>
                        ModelContextProtocol.Server.McpServerTool.Create(captured, (object)null,
                            new ModelContextProtocol.Server.McpServerToolCreateOptions { Services = serviceProvider }));
                    exposed++;
                }
                Console.Error.WriteLine("Tool registration: " + exposed
                    + " tools exposed (readonly mode: non-Read tiers excluded from tools/list).");
            }
            else
            {
                mcpBuilder.WithToolsFromAssembly();
            }

            // 注册我们的 SE 连接服务(单例,所有工具共享一个连接)
            builder.Services.AddSingleton<SolidEdgeContext>();

            var host = builder.Build();

            var logger = host.Services.GetRequiredService<ILogger<SolidEdgeContext>>();
            logger.LogInformation("Solid Edge MCP Server 启动中...");
            logger.LogInformation("Permission mode: " + modeNote);

            // 单次 COM 调用超时(秒)。默认 120,可用 SE_MCP_TIMEOUT_SECONDS 覆盖(最小 5)。
            string timeoutEnv = Environment.GetEnvironmentVariable("SE_MCP_TIMEOUT_SECONDS");
            if (int.TryParse(timeoutEnv, out int timeoutSecs) && timeoutSecs >= 5)
            {
            	SolidEdgeContext.InvokeTimeoutSeconds = timeoutSecs;
            }
            logger.LogInformation("Invoke timeout: " + SolidEdgeContext.InvokeTimeoutSeconds + "s");

            // 启动时尝试连接 SE(连不上不报错,AI 调用时再重试)
            var context = host.Services.GetRequiredService<SolidEdgeContext>();
            _ = context.TryConnect(); // 不 await,异步尝试,失败也不阻塞 server 启动

            logger.LogInformation("MCP Server 已就绪,等待 AI 客户端调用工具。");
            await host.RunAsync();
        }

        /// <summary>
        /// 反射扫描程序集里全部 [McpServerToolType]/[McpServerTool] 方法(与
        /// WithToolsFromAssembly 同源),按 ToolRisk 档位只保留 Read 档。
        /// 仅只读模式调用(见 RunServerAsync 里的分支);工具实例由调用方延迟创建。
        /// </summary>
        private static System.Collections.Generic.List<System.Reflection.MethodInfo> EnumerateVisibleToolMethods()
        {
            var visible = new System.Collections.Generic.List<System.Reflection.MethodInfo>();
            foreach (Type type in typeof(Program).Assembly.GetTypes())
            {
                if (!type.IsDefined(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), inherit: false))
                {
                    continue;
                }
                foreach (System.Reflection.MethodInfo method in type.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                {
                    if (!method.IsDefined(typeof(ModelContextProtocol.Server.McpServerToolAttribute), inherit: false))
                    {
                        continue;
                    }
                    // 工具名与运行时一致:方法名即工具名(本项目方法名已是 se_* 蛇形)。
                    if (SolidEdge.Spy.McpServer.Tools.ToolRisk.TierOf(method.Name)
                        == SolidEdge.Spy.McpServer.Tools.RiskTier.Read)
                    {
                        visible.Add(method);
                    }
                }
            }
            return visible;
        }
    }
}
