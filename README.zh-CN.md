# solidedge-mcp

[![CI](https://github.com/1337332551-dot/solidedge-mcp-server/actions/workflows/ci.yml/badge.svg)](https://github.com/1337332551-dot/solidedge-mcp-server/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**让 AI 客户端直接操作 Siemens Solid Edge 的 MCP server**——通过自然对话查询模型、构建参数化特征、自动化出图、监听文档事件。

国际客户端（Claude Desktop、Cursor、Cline）和国内 AI 编程工具都可用：**Trae、CodeBuddy（腾讯）、ZCode**——只要支持 MCP 就能接。

Solid Edge 始终是唯一事实源：AI 不替代你的 CAD 工作流，而是操作它——像一双手，搭在你正在运行的 Solid Edge 实例上。

- [English](README.md) | [中文文档](README.zh-CN.md)

![用 MCP 在 Solid Edge 里建模](assets/demo.gif)

_一次建模过程：AI 通过 MCP 驱动 Solid Edge 一步步把模型建出来。_

## 为什么做这个

机械工程师每天在重复的 CAD 操作上消耗大量时间：填参数化模型、改特征名、核对 BOM 一致性、导出图纸。本项目把 Solid Edge 的 COM API 暴露给任何支持 MCP 的 AI 客户端，让这些步骤变成一段对话，而不是一个你必须自己写、自己维护的宏。

当前已验证支持的工作流：

- **参数化建模**——用 JSON 描述特征序列，AI 负责填参数（`se_model_build` 先走静态 dry-run 校验，通过才真正动模型）
- **模型探查**——遍历对象树、读几何、读变量、读选中状态
- **出图自动化**——视图裁剪、图幅排版、中心线/中心标记、驱动尺寸
- **装配操作**——读取装配结构（零件表/约束/BOM）、干涉检查与重量汇总，一次声明式调用完成建装配（`se_assembly_query` / `se_assembly_build`）
- **事件监听**——第二个 MCP server 把 Solid Edge 文档事件流式推给 AI

## 两个 MCP server

| Server | 可执行文件 | 用途 |
|---|---|---|
| 执行 | `solidedge-mcp` | 22 个工具：查询、文档、建模、装配、脚本 |
| 事件 | `solidedge-event-mcp` | 4 个工具：订阅/等待/查询 Solid Edge 事件 |

## 设计意图：配合 skill 使用

工具面是刻意做小的——**22 个，不是 200 个**。工作流知识放在上面一层：**skill**——可版本化、可编辑的知识包（企业制图标准、特征命名规则、典型零件建模 SOP；可以自己写，也可以让 AI 从一次会话里总结生成），负责告诉 AI"做什么、按什么顺序"。本 server 只提供底下那层安全、受控的执行原语：读模型、建特征、探弹窗、落审计。

这样分工，领域知识就不需要烧进工具代码：

- 扩展功能靠改 markdown，不用发新版 server
- 团队规范跟模型文件一起进 git 仓库，而不是编译进二进制
- `se_recipe_run` 是这套契约的执行半环——skill（或 AI）产出 JSON 特征规格，server 先 dry-run 校验，通过才建模

如果你更想要把整个 COM API 1:1 映射成几百个工具的"胖工具"方案，别的项目是那个路线；本项目押注的是 skill + 原语。

## 工具总览（执行 server）

| 分类 | 工具 |
|---|---|
| 查询/只读 | `se_get_document` `se_get_selection` `se_find_paths` `se_describe_object` `se_walk_object` `se_batch_read` `se_read_geometry` `se_get_variables` `se_view_context` `se_capture_viewport` `se_snapshot_diff` `se_validate_features` `se_assembly_query` |
| 文档会话 | `se_open_document` `se_new_document` `se_close_document` |
| 改动模型 | `se_model_build` `se_invoke_member` `se_invoke_chain` `se_recipe_run` `se_assembly_build` |
| 逃生通道 | `se_script_run`（对 COM API 跑一段 C# 脚本） |

事件 server（`solidedge-event-mcp`）：

| 工具 | 用途 |
|---|---|
| `se_get_events` | 增量读取环形缓冲中累积的事件 |
| `se_wait_event` | 阻塞等待匹配的事件到达（轮询助手） |
| `se_set_event_filter` | 开关事件源降噪（如等重算完成时静默命令类事件） |
| `se_event_status` | 诊断：SE 连接状态、各事件接口订阅结果、缓冲统计——事件不触发时先查它 |

事件中心用 200 条的环形缓冲存事件，读取基于游标（`afterSeq`）增量进行，多次读取之间不会漏事件。默认只关掉高频噪声源 `SelectSetChanged` 过滤器。事件二进制也有个小 CLI 用于冒烟测试：`--listen [秒]` 和 `--cleanup`。

## 权限模式

在 MCP 配置的 server 节点上设置 `SE_MCP_MODE` 环境变量：

| 值 | 行为 |
|---|---|
| `full`（默认） | 22 个工具全放行 |
| `engineer`（机械工程师） | 工具全放行；但自由调用通道（`se_invoke_member`/`se_invoke_chain`）只放行只读成员——一份显式白名单的属性式读取（`Models`/`Item`/`Body`/`Name` 等）加 `get` 前缀成员（`GetXxx`/`get_xxx`），建模走 `se_model_build`/`se_recipe_run`/`se_assembly_build` |
| `readonly` | 只放行 13 个查询工具；建模/会话/脚本类调用在传输层直接拒绝，并提示如何切回 |
| 其他任意值 | fail-closed，按 `readonly` 处理 |

旧的 `SE_MCP_READONLY=1` 仍然兼容，等价 `readonly`。改模式后需要重启 AI 会话（客户端重载 MCP server 才生效）。

门禁背后的工具风险档位：**Read**（13 个查询工具）/ **Session**（open/new/close 文档）/ **Model**（6 个改模型工具）/ **Escape**（`se_script_run`）。未登记工具 fail-closed，按最高危处理。

其他环境变量：

| 变量 | 用途 |
|---|---|
| `SE_MCP_TIMEOUT_SECONDS` | 单次 COM 调用超时秒数（默认 120，最小 5）。大装配场景建议调大。 |
| `SE_MCP_RECIPES_DIR` | 配方 JSON 的额外搜索路径（分号分隔多个）。不设时先在 exe 所在目录向上最多 8 层找 `recipes` 目录，最后兜底 `%LOCALAPPDATA%\SolidEdgeSpy\recipes`。`_` 开头的文件视为草稿，不能按名执行。 |

另外还有第二层洋葱：成员级护栏（`Guardrail`），并且每次工具调用都会写入审计日志 `%LOCALAPPDATA%\SolidEdgeSpy\mcp-audit.log`。单次调用的耗时和成败记录在同目录的 `tool-usage.jsonl`（CLI 用 `--usage` 可出报表），`se_snapshot_diff` 的快照落盘在 `%LOCALAPPDATA%\SolidEdgeSpy\snapshots`。

## 环境要求

- Windows + 已安装并可运行的 **Siemens Solid Edge**（互操作包对应 SE2022 / 类型库 v108；其他版本见下文说明）
- **.NET 8 SDK**（自己编译），或直接下载 [Release 二进制](https://github.com/1337332551-dot/solidedge-mcp-server/releases/latest)——自包含，连 .NET 运行时都不用装
- 一个支持 MCP 的 AI 客户端

## 构建

从源码构建：

```powershell
git clone https://github.com/1337332551-dot/solidedge-mcp-server.git
cd solidedge-mcp
dotnet build src/SolidEdge.Spy.McpServer -c Release
dotnet build src/SolidEdge.Spy.EventMcp  -c Release
dotnet test solidedge-mcp.sln            # 558 个单元测试，不需要装 Solid Edge
```

不需要你提前准备任何 Siemens 文件：COM 互操作程序集来自社区发布的 [`Interop.SolidEdge`](https://www.nuget.org/packages/Interop.SolidEdge) NuGet 包（纯类型定义，本仓库不分发任何 Siemens 专有代码）。

> 国内用户如果 nuget.org 慢或连不上，restore 前先加个镜像源，或在 `.sln` 旁边放一个 `nuget.config`：
>
> ```xml
> <?xml version="1.0" encoding="utf-8"?>
> <configuration>
>   <packageSources>
>     <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
>     <add key="azure-cn" value="https://nuget.cdn.azure.cn/v3/index.json" />
>   </packageSources>
> </configuration>
> ```

如果你想基于本机安装的 Solid Edge 自己生成互操作程序集（例如 SE 版本不同），用 `scripts/gen_interop.ps1`（依赖 .NET Framework 的 `TlbImp.exe`），生成后改为引用产物 DLL。

## 配置 AI 客户端

把 server 指向编译好的二进制。示例：

**Claude Desktop / Cursor / Cline**（`claude_desktop_config.json` / `mcp.json`）：

```json
{
  "mcpServers": {
    "solidedge": {
      "command": "D:/path/to/solidedge-mcp/src/SolidEdge.Spy.McpServer/bin/Release/net8.0-windows/solidedge-mcp.exe",
      "env": { "SE_MCP_MODE": "readonly" }
    },
    "solidedge-events": {
      "command": "D:/path/to/solidedge-mcp/src/SolidEdge.Spy.EventMcp/bin/Release/net8.0-windows/solidedge-event-mcp.exe"
    }
  }
}
```

**Trae / CodeBuddy / ZCode**——国内 AI 编程工具同样走 MCP 配置，JSON 格式相同（Trae 读工作区 `.trae/mcp.json`，其余在各自的 MCP 设置面板里配）。

先启动 Solid Edge，再在客户端开新会话——server 会自动连接正在运行的实例。

## CLI 模式

执行二进制可以直接当一次性命令行工具用，不需要 AI 客户端，便于调试：

```powershell
solidedge-mcp.exe get_document
solidedge-mcp.exe invoke_member --objectId <id> --member Name
```

常用开关：`-d` 文档 / `-s` 选中集 / `--vars` 变量 / `-w` 遍历 / `-desc` 描述 / `-p` 找路径 / `--geometry` 几何 / `--viewctx` 视图 / `--batchread` 批读 / `--snap` 快照对比 / `--probe` / `--preview`。写类开关（`--newpart`、`--newclose`、`--model`、`--set`、`--openclose`、`--cs`、`--recipe-run`）走同一张权限门禁表：readonly 模式下在触碰 Solid Edge 之前就被拒绝。多数短开关有长名别名（`--doc`、`--selection`、`--walk`、`--describe`、`--paths`）；`--preview` 接受 `-o/--out <文件>` 和视角名（`iso`/`top`/`front`/…/`current`）。完整清单见 `solidedge-mcp.exe --help`。

实用工具类开关（大多不需要连接 Solid Edge）：

| 开关 | 用途 |
|---|---|
| `--version` / `-v` | 打印构建时间戳后退出 |
| `--usage` / `-u [天数]` | 从 `tool-usage.jsonl` 出调用统计报表（`--all`、`--by day`、`--sort calls\|ms\|last\|fail`） |
| `--recipes` | 列出找到的全部配方（名称/状态/参数/来源目录） |
| `--recipe-validate <名字\|路径>` | 静态校验配方，不碰 COM |
| `--dialogs` | 弹窗探针：SE 卡死也能枚举模态框（`--close <hwnd> --confirm` 可受控关闭某个框） |

## 架构

```
AI 客户端 (Claude/Cursor/Trae)
   │  stdio JSON-RPC
   ▼
PermissionTap ── 模式 × 工具风险档位 门禁（拒绝在进 SDK 前伪造完成）
   ▼
JsonRpcTap ── 调用计量 / 审计
   ▼
MCP SDK 工具处理器 ── Guardrail 成员级检查
   ▼
COM 互操作 (IDispatch + PIA) ── 正在运行的 Solid Edge 实例
```

```
src/
├── SolidEdge.Spy.McpServer/        # 执行 MCP server（23 工具）
├── SolidEdge.Spy.EventMcp/         # 事件 MCP server（4 工具）
├── SolidEdge.Shared/               # COM 互操作基础设施（编译期共享，单一数据源）
├── SolidEdge.Spy.McpServer.Tests/  # 单元测试第 2 套（纯逻辑，不需要装 SE）
tests/SolidEdge.Spy.McpServer.Tests/ # 单元测试第 1 套（纯逻辑，不需要装 SE）
recipes/                            # 示例配方 JSON（见 recipes/README.md）
scripts/                            # 辅助脚本（互操作程序集生成）
```

两套 xUnit 工程合计 558 个单元测试（`dotnet test solidedge-mcp.sln`）。

## 开发

```powershell
dotnet test solidedge-mcp.sln
```

测试是纯 .NET 的（不依赖 Solid Edge），覆盖解析、校验规则、权限档位表、传输层 tap。

## FAQ

**server 连不上 Solid Edge？**
先启动 Solid Edge。server 会自动连接运行中的实例，连不上也不阻塞启动，下次工具调用时重试。

**工具调用被拒绝，提示"已拒绝 ... SE_MCP_MODE"？**
当前处于受限模式。`readonly` 模式下写档位工具整体被拦；`engineer` 模式下自由调用只放 `get` 前缀成员。在 MCP 配置里把 `SE_MCP_MODE` 改为 `full`（或删掉该变量）后重启会话。

**改了配置但没生效？**
MCP server 由 AI 客户端在会话启动时拉起，任何 `mcp.json` 改动都需要重启会话——旧进程缓存的工具会一直服务到那时。

**支持哪些 Solid Edge 版本？**
互操作包版本号对应 SE 类型库版本：`108.0.0` = SE2022。其他 SE 版本把 `Interop.SolidEdge` 的 PackageReference 换成对应版本号（NuGet 上 105–220 都有），或用 `scripts/gen_interop.ps1` 从本机安装的 SE 生成。

**让 AI 操作我的 CAD 安全吗？**
纵深防御：传输层模式门禁（readonly/full）、写调用的成员级护栏、文档会话追踪（`close` 只关本会话自己打开的文档，绝不误关用户文档）、每次工具调用的审计日志（`%LOCALAPPDATA%\SolidEdgeSpy\mcp-audit.log`）。真正的建模改动之前会先跑 `dry-run` 静态校验；`se_invoke_member` 写属性后默认自动回读比对（`verify=true`），防止"写入没报错但实际没生效"。

**Solid Edge 弹了模态框，AI 会卡死吗？**
不会：弹窗探针检测到模态框时直接上报框标题和按钮，而不是傻等到超时。

## 项目状态

`se_model_build` 消费的 features JSON——建模中间表示（IR）——还处在**毛坯阶段**：op 覆盖面、默认值、字段名都可能随版本调整。如果你的工作流要依赖它，建议固定 commit 使用，并预期格式变动。本仓库自带一组**示例配方**（拉伸、旋转、除料、只读探查，以及一条护栏自检配方），放在 [`recipes/`](recipes/)——自己的配方可以放在它们旁边，或用 `SE_MCP_RECIPES_DIR` 指向别的目录。来自真实参数化建模用例的反馈尤其有价值——欢迎开 issue 告诉你想建什么、卡在哪。

## Roadmap

- [ ] 稳定 features IR（v1）
- [x] 发布 Release 二进制（不用装 SDK 也能试用）
- [x] GitHub Actions CI（push 时自动 build + test）
- [ ] 更多配方示例（出图自动化、BOM 提取）
- [ ] 工具文档站

欢迎贡献——提 issue 或 PR。

## 致谢与许可

- COM 互操作基础设施（`InteropServices/*`、扩展方法）衍生自 [Jason Newell 的 SolidEdgeSpy](https://github.com/JWSingleton/SolidEdgeSpy)——本项目最初就是在那份代码基础上长出来的。
- 互操作程序集由 [Solid Edge Community](https://github.com/SolidEdgeCommunity) 发布。

MIT——见 [LICENSE](LICENSE)。

> 本项目与 Siemens 无关，也未经 Siemens 认可。Solid Edge 是 Siemens Digital Industries Software 的商标。
