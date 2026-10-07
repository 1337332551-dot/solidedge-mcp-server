# Solid Edge MCP Server

**定位：AI 与 Solid Edge 之间的「受控网关」（不是 SolidEdge 二次开发框架）。**

本 server 不重写任何 SolidEdge 业务，只把已有的 COM 能力翻译成 LLM 能调用的工具，并套一层安全壳：
让 AI 安全地**看**（读对象树/属性/路径）、**做**（执行 COM 成员、受护栏约束）、**感知**（事件，见配套的 EventMcp）。
领域逻辑只此一份——通过链接编译复用 `SolidEdge.Spy` 的 `InteropServices`，MCP 只是薄薄的翻译层。

与配套的 `EventMcp` 组成**双进程闭环**：本执行 MCP（眼+手）负责「做」，EventMcp（耳）负责「感知 SE 状态变化」，
典型闭环为 `驱动(执行) → 等事件(耳) → 确认(读回)`。详见下文「双进程协同」。

## 工作原理

```
┌──────────────┐     MCP协议(stdio)     ┌──────────────────────┐    COM    ┌──────────┐
│ AI客户端     │ ◄─────────────────────►│ SolidEdge.Spy.McpServer│ ◄───────►│Solid Edge│
│(CodeBuddy)   │   AI自动发起工具调用     │ (独立进程,net8.0)    │           │ (运行中)  │
└──────────────┘                        └──────────────────────┘           └──────────┘
                                                 │ 链接编译复用(零修改)
                                                 ▼
                                        SolidEdge.Shared\InteropServices\
                                        (ComPtr、ComTypeInfo、
                                         IDispatchExtensions、
                                         OleMessageFilter...)
```

- MCP server 由 AI 客户端**按需自动拉起**，通过 stdio 通信
- 直连运行中的 SE（COM 多客户端机制，Windows ROT 提供，不经过 Spy 界面程序）
- 复用 `../SolidEdge.Shared/InteropServices` 全套 COM 互操作源码（链接编译，零修改）
- 事件感知另见 `../SolidEdge.Spy.EventMcp/README.md`（独立进程，耳朵）

## 双进程协同（执行 MCP + 事件 MCP）

SE 是 STA COM 服务器，事件回调需要消息泵；执行 MCP 的 STA 线程用 `BlockingCollection` 阻塞等出站调用、没有消息循环，收不到事件回调。
因此事件监听拆成**独立进程** EventMcp（自带消息泵），二者风险隔离、互不阻塞。

协同闭环：

```
AI ──① 调执行MCP 改 SE──► Solid Edge 变化
 │                                     │
 │◄──③ 调执行MCP 读回确认 ──           ▼
 │                             ② 事件Mcp 捕获(环形缓冲) ──► AI 读 se_get_events
```

- ① 执行 MCP 驱动 SE（建特征/改参数/打开文档…）
- ② EventMcp 订阅 SE 事件接口，写入 200 条环形缓冲，AI 用 `se_get_events` 增量拉取（含 `AfterRecompute` 等关键确认信号）
- ③ AI 结合事件确认「刚才的改动真的生效了 / 没崩」，再决定下一步

host 同时挂载两个 server（`solidedge` 执行 + `solidedge-event` 事件），配置见各自 README。

## 工具集（两轨混合，非纯堆工具）

设计走**两轨**：通用通道兜底任意操作 + 高级封装提质常见操作。每工具标注安全等级：
🟢 只读（无副作用）· 🟡 写（改变模型/文档，受护栏或独立保护）· 🔴 破坏性（删除/移除，必须显式确认）。

**轨 A · 通用通道（兜底，AI 遇未封装能力也能干）**

| 工具 | 等级 | 作用 |
|---|---|---|
| `se_invoke_member` | 🟡（按成员名分级） | 在选中对象上执行任意成员（方法/带参属性）；`out` 参数自动回填，返回 COM 对象登记新句柄供下钻。**按成员名走 `Guardrail` 分级**，`Delete/Cut/Remove*` 等需 `confirm=true` |
| `se_invoke_chain` | 🟡（按成员名分级） | 链式调用多个成员，建模配方主力；同样过 `Guardrail` |
| `se_walk_object` | 🟢 | 按路径字符串取对象树任意位置概要，AI 自由探索 |

**轨 B · 高级封装（提质，高频操作专用，带护栏/保护）**

| 工具 | 等级 | 作用 |
|---|---|---|
| `se_get_document` | 🟢 | 当前活动文档信息（类型/文件名/环境） |
| `se_get_variables` | 🟢 | 读取当前零件变量表全部变量（显示名/名称/系统名/公式/类型） |
| `se_get_selection` | 🟢 | 取 SE 中选中的所有对象并分配编号（obj-1, obj-2…） |
| `se_find_paths` | 🟢 | 反查选中对象在对象模型中的访问路径，如 `Application.ActiveDocument.Sketches.Item("草图 1").Lines2d.Item(12)` |
| `se_describe_object` | 🟢 | 详细描述选中对象：实现接口/类、全部属性（含当前值）、全部方法（含签名） |
| `se_read_geometry` | 🟢 | 读包围盒/形心/尺寸(mm)/参考面法向等定位信息（解决坐标系心智负担） |
| `se_capture_viewport` | 🟢 | 视口截图并直接以图片返回给多模态 AI（含裁剪/视角还原），用于建模后目检 |
| `se_view_context` | 🟢 | 当前视图/选择上下文概要 |
| `se_batch_read` | 🟢 | 批量读取多个对象属性，减少 round-trip |
| `se_snapshot_diff` | 🟢 | 快照比对（改动前后差异） |
| `se_open_document` | 🟡 | 打开文档并登记句柄；仅本工具打开的进追踪表（见安全模型）；`DisplayAlerts=false` 防卡死 + 轻量读取探针防半加载 |
| `se_new_document` | 🟡 | 新建空文档并登记句柄（part/asm/dft/psm/weld） |
| `se_close_document` | 🔴（潜在） | 关闭**仅本会话追踪表内**的文档；`Dirty` 未保存时须 `confirm=true`，**永不保存** |
| `se_model_build` | 🟡 | 建模：按描述构建特征（独立保护逻辑，建议统一收口到 `Guardrail`，见下） |
| `se_validate_features` | 🟢 | 校验特征（只读检查） |
| `se_recipe_run` | 🟡 | 运行配方（预检 + 过 `Guardrail`） |
| `se_script_run` | 🟡 | 受控 C# 一次性脚本通道（沙箱子进程 + 全局只读开关 + 审计） |

> 注：工具名以源码 `[McpServerTool]` 标注为准；新增工具只需在 `Tools/` 加 `.cs`，SDK 自动扫描注册。

## 安全模型（受控网关的核心）

**权限模式（权限洋葱外层，传输层 `tools/call` 门禁）**：MCP 配置的 server 节点设环境变量 `SE_MCP_MODE=readonly|engineer|full`（默认 full）：

| 模式 | 工具面 | 自由调用通道（se_invoke_member / se_invoke_chain） |
|---|---|---|
| `readonly` | 只放 13 个 Read 档查询工具，其余档传输层直接拒绝 | 不可用（写工具整体被拦） |
| `engineer`（机械工程师） | 22 个工具全放 | **只放行显式读取白名单 + `get` 前缀成员**（`Models`/`Item`/`Body`/`Name` 等属性式读取，及 `GetXxx` / `get_xxx` 读取类）；建模走 se_model_build / se_recipe_run |
| `full` | 22 个工具全放 | 任意成员（Guardrail 分级 + confirm 仍在） |

旧 `SE_MCP_READONLY=1` 兼容映射 readonly；未识别值 fail-closed 按 readonly。改模式需重启 AI 会话（客户端重载 server 才生效）。

护栏分**两套并存机制**，这是当前真实架构，也是已知待办：

1. **通用通道走 `Guardrail` 按成员名静态分级**（不解析参数，invoke 前即可拦截）：
   - `Normal`：只读查询，放行
   - `ModelChanging`：`Add`/`Set`/`Move`/`Replace`/`Clear`/`Update` 等写操作，放行但记审计、提示会触发重算
   - `Destructive`：`Delete`/`Cut`/`Drop`/`Erase`/`Purge`/`Remove*` 等不可逆操作，**默认拒绝，必须显式 `confirm=true`**
   - 全局只读开关：`readonly` 模式下自动开启（旧 `SE_MCP_READONLY=1` 仍兼容），一切写操作（含脚本通道）整体拒绝
2. **高级写工具各自实现等价保护**（尚未统一收口到 `Guardrail`）：
   - `se_open/close/new_document`：文档追踪表 + `Dirty` 检查 + `confirm` + `AuditLog`
   - `se_close_document`：**只关本会话 `se_open_document` 打开的文档，绝不误关你手动打开的**
   - `se_script_run`：名称白名单（防路径穿越）+ 独立子进程沙箱 + 超时强杀进程树 + 全局只读开关禁用 + 每次运行写审计
   - `se_model_build`：独立保护逻辑（当前未调 `Guardrail`，**建议后续统一收口**）

**审计**：所有写操作（含被拒）写入 `AuditLog`（`%LOCALAPPDATA%\SolidEdgeSpy\mcp-audit.log`），可事后追溯。

> ⚠️ 关键认知：`Guardrail` 防「删库」，防不了「建垃圾」——它按成员名分级，`Add` 一个特征带错参数会建出废料甚至崩 SE，护栏放行。写工具的质量取决于每个作者自觉。后续应把写入口统一收口到一个带护栏的写网关。

## 编译

```powershell
cd "D:\path\to\solidedge-mcp\src\SolidEdge.Spy.McpServer"
dotnet build -c Release
```

输出：`bin\Release\net8.0-windows\solidedge-mcp.dll`

## 命令行模式（不依赖 MCP 客户端）

`solidedge-mcp.exe` 除做 MCP server 外，也支持命令行查询（调试/脚本复用/任意进程调用）：

```powershell
solidedge-mcp.exe --doc              # 当前活动文档信息
solidedge-mcp.exe --selection        # 选中对象列表(分配 obj-N)
solidedge-mcp.exe --walk "Application.ActiveDocument.ProfileSets.Item(1).Profiles.Item(1)"  # 按路径取对象概要
solidedge-mcp.exe --help
```

> CLI 与 MCP 共用同一套逻辑，入口 `CliRunner.cs` 由 `Program.Main` 按有无参数分流：有参数走 CLI，无参数走 MCP stdio。

## 在 CodeBuddy 中配置

```json
{
  "mcpServers": {
    "solidedge": {
      "command": "dotnet",
      "args": ["D:\\path\\to\\solidedge-mcp\\src\\SolidEdge.Spy.McpServer\\bin\\Release\\net8.0-windows\\solidedge-mcp.dll"]
    }
  }
}
```

或改用 exe（更稳）：`"command": "D:\\path\\to\\solidedge-mcp\\src\\SolidEdge.Spy.McpServer\\bin\\Release\\net8.0-windows\\solidedge-mcp.exe"`。

## 使用流程

1. 打开 Solid Edge 并打开一个文档（零件/装配/图纸）
2. 在 SE 里用鼠标选中一个对象（如一条线、一个特征）
3. 在 CodeBuddy 里对 AI 说："我选了一个对象，帮我查它的路径和属性"
4. AI 自动调用：`se_get_selection` → `se_find_paths` → `se_describe_object` → `se_invoke_member`（返回对象自动获新句柄 obj-2，继续下钻）
5. AI 拿到信息后可直接写出精确代码：

```csharp
var app = (SolidEdgeFramework.Application)Marshal.GetActiveObject("SolidEdge.Application");
var line = app.ActiveDocument.Sketches.Item("草图 1").Lines2d.Item(12);
// 直接操作 line...
```

> 注意：`se_invoke_member` 产生的句柄（obj-2、obj-3…）存活到下次 `se_get_selection` 为止——查询链路中不要重复调 `se_get_selection`，否则句柄表清空、链路断。默认仅用只读查询；`Delete`/`Cut`/`Move` 等仅在用户明确要求且 `confirm=true` 时执行。

## 常见踩坑（迁移到新电脑时必读）

### 1. 必须走 MCP 工具调用，不要用命令行 exe 查询选中对象

`obj-N` 句柄表存在 **MCP server 进程内存**里。`se_get_selection` 和 `se_find_paths`/`se_describe_object` 必须在**同一个 MCP 会话进程**内连续调用。
命令行模式（`solidedge-mcp.exe --selection` 后 `--paths obj-1`）每次都是独立新进程，句柄不共享，第二个命令必报"找不到对象编号"。CLI 只适合 `--doc`/`--walk` 这类无状态查询。

**判别**：若"之前查得通、现在报找不到对象编号"，先查是不是把 MCP 调用换成了 CLI 调用。

### 2. 单位陷阱

SE API 长度/距离/坐标内部单位是**米**，UI 显示毫米（×1000）；角度是**弧度**。`Dimension.Value = 0.65` 即 650 mm，别当 0.65 mm。

### 3. 编译前先停掉运行中的 server

MCP server 是常驻进程，`dotnet build` 复制 exe 时会被文件锁卡住（MSB3027/MSB3021）。编译前先结束 `solidedge-mcp` 进程，编完由 MCP 客户端按需重拉起。

### 4. 强类型 vs dynamic

写操作 SE 对象的代码时，用强类型转换（如 `(SolidEdgeFrameworkSupport.Dimensions)sheet.Dimensions`）走 interop vtable；用 `dynamic` 走 IDispatch 可能取不到非默认派发接口的成员。

### 5. 编辑模型前先确认可回滚

建模/改参数是 🟡 写操作，建议先在副本或新建文档试，配合 `se_snapshot_diff`/`se_capture_viewport` 目检。批量探索阶段可用 `SE_MCP_MODE=readonly` 整体禁写（只放查询工具）。

## 关键技术点

- **目标框架**：`net8.0-windows`（MCP SDK 2.2.0 要求）
- **MCP SDK**：官方 `ModelContextProtocol` 2.2.0
- **SE 互操作**：`Interop.SolidEdge` NuGet（net40 编译，可被 net8 加载）+ 链接编译复用 `../SolidEdge.Shared/InteropServices` 源码
- **STA 线程**：入口 `[STAThread]`，所有 COM 调用在同一 STA 线程，避免跨线程崩溃
- **OLE 消息过滤器**：自建 `OleMessageFilter.cs`，处理 SE 忙时 COM 调用重入
- **弹窗探针**：SE 弹模态框时不等超时，直接报框标题与按钮，避免卡死
- **对象身份比对**：`Marshal.GetIUnknownForObject` 拿 IUnknown 指针比较，确保找到的就是用户选中的那一个
- **路径搜索算法**：BFS 从 `Application.ActiveDocument` 出发遍历对象树，类型剪枝，节点上限 50000 防大文档拖死
- **句柄表**：`obj-N` 在进程内存中映射 COM 对象，跨调用复用，下次 `se_get_selection` 清空

## 项目结构

```
src/SolidEdge.Spy.McpServer/
├── SolidEdge.Spy.McpServer.csproj  ← 链接编译复用原项目源码
├── Program.cs                       ← 入口：无参数走 MCP stdio，有参数走 CLI
├── CliRunner.cs                     ← 命令行模式(--doc / --selection / --walk)
├── SolidEdgeContext.cs              ← SE 连接/重连/句柄表/STA 封送/弹窗探测
├── OleMessageFilter.cs              ← OLE 消息过滤器
├── ObjectExplorer.cs                ← 对象树 BFS 遍历+身份比对+路径生成
├── Guardrail.cs                     ← 写能力护栏(三级分级 + 全局只读)
├── AuditLog.cs                      ← 写操作审计日志
├── Telemetry/ToolUsage.cs          ← 工具调用统计(可反推哪些能力该固化为封装)
└── Tools/
    ├── SelectionTools.cs            ← se_get_selection / se_find_paths / se_describe_object
    ├── DocumentTools.cs             ← se_get_document / se_open/close/new_document(追踪表+Dirty)
    ├── VariableTools.cs             ← se_get_variables
    ├── GeometryTools.cs             ← se_read_geometry / se_capture_viewport(读+截图)
    ├── ViewContextTools.cs          ← se_view_context
    ├── BatchReadTools.cs            ← se_batch_read
    ├── SnapshotTools.cs             ← se_snapshot_diff
    ├── WalkTools.cs                 ← se_walk_object
    ├── InvokeTools.cs               ← se_invoke_member / se_invoke_chain(通用通道,过 Guardrail)
    ├── ModelingTools.cs             ← se_model_build / se_validate_features(建模写)
    ├── RecipeTools.cs               ← se_recipe_run(配方,预检+Guardrail)
    └── ScriptTools.cs               ← se_script_run(受控脚本沙箱)
```

## 扩展指南

加新功能 = 在 `Tools/` 加一个 `.cs` 文件，SDK 自动扫描 `[McpServerTool]` 注册。例：

```csharp
[McpServerToolType]
public static class MyNewTools
{
    [McpServerTool("se_count_features")]
    [Description("统计当前文档的特征数量")]
    public static string CountFeatures(SolidEdgeContext context)
    {
        var app = context.GetApplication();
        // ... 业务逻辑 ...
        return JsonSerializer.Serialize(new { count = ... });
    }
}
```

保存编译即可，不改注册代码、不改配置。

**铁律（写工具必须守）**：
1. 🟡 写操作必须实现护栏等价保护——优先调 `Guardrail.Check`；若自行保护，须写 `AuditLog` 且破坏性操作要 `confirm`
2. 禁止裸调 COM 绕过护栏；所有写调用走 `context.Invoke`（STA 封送 + 弹窗探测）
3. 文档开关类必须进追踪表，`close` 只关本会话打开的、绝不误关用户文档
4. 命名统一 `se_` 前缀，描述写清「做了什么 + 什么情况要 confirm + 是否触发重算」

## 致谢与渊源

- COM 互操作基础设施（`InteropServices/*`）衍生自 Jason Newell 的开源项目 [SolidEdgeSpy](https://github.com/JWSingleton/SolidEdgeSpy)（MIT）
- 链接编译复用 `../SolidEdge.Shared/` 共享源码：源文件不复制、不修改，本执行 MCP 与 EventMcp 共享同一份
- Spy 对象树浏览的思路保留了"人工浏览对象模型"的场景；MCP server 是给 AI 用的独立工具（眼+手）；EventMcp 是耳朵
