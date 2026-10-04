# solidedge-mcp

[![CI](https://github.com/1337332551-dot/solidedge-mcp-server/actions/workflows/ci.yml/badge.svg)](https://github.com/1337332551-dot/solidedge-mcp-server/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**MCP servers that let AI clients drive Siemens Solid Edge** — query models, build parametric features, automate drawings, and listen to document events, all through natural conversation.

Works with Claude Desktop, Cursor, Cline, and Chinese AI coding tools alike: **Trae, CodeBuddy (Tencent), ZCode** — anything that speaks MCP.

Solid Edge remains the single source of truth: the AI never replaces your CAD workflow, it operates it — like a pair of hands on your running Solid Edge instance.

- [English](README.md) | [中文文档](README.zh-CN.md)

![Building a model in Solid Edge through MCP](assets/demo.gif)

_A model being built step by step — the AI drives Solid Edge through MCP._

## Why

Mechanical engineers lose hours on repetitive CAD operations: filling in parametric models, renaming features, checking BOM consistency, exporting drawings. This project exposes Solid Edge's COM API to any MCP-capable AI client, so those steps become a conversation instead of a macro you have to write and maintain.

Real workflows it supports today:

- **Parametric model building** — describe features in a JSON spec, AI fills the parameters (`se_model_build` with static dry-run validation before anything touches your model)
- **Model interrogation** — walk the object tree, read geometry, variables, selection state
- **Drawing automation** — view crops, drawing frames, center marks, driven dimensions
- **Assembly operations** — read the structure (parts / constraints / BOM), run interference detection and weight rollups, and build assemblies with one declarative call (`se_assembly_query` / `se_assembly_build`)
- **Event monitoring** — a second MCP server streams Solid Edge document events to the AI

## Two MCP servers

| Server | Binary | Purpose |
|---|---|---|
| Execution | `solidedge-mcp` | 23 tools: query, document, modeling, assembly, scripting |
| Events | `solidedge-event-mcp` | 4 tools: subscribe/wait/query Solid Edge events |

## Designed to pair with skills

The tool surface is deliberately small — **23 tools, not 200**. The workflow knowledge is meant to live one layer up, in **skills**: versioned, editable knowledge packages (company drawing standards, feature naming rules, typical-part modeling SOPs — written by you or distilled by the AI itself from a session) that tell the AI *what to build and in what order*. This server provides the safe, verified primitives underneath: read, build, probe, audit.

That division keeps domain knowledge out of tool code:

- Extend coverage by editing markdown, not by shipping a new server version
- Team standards live in a git repo alongside your models, not baked into a binary
- `se_recipe_run` is the executable half of the contract — a skill (or the AI) emits a JSON feature spec, the server dry-run validates it, then builds

If you prefer a fat-tool server that maps the whole COM API 1:1, other projects do that; this one bets on skills + primitives.

## Tool overview (execution server)

| Category | Tools |
|---|---|
| Query / read | `se_get_document` `se_get_selection` `se_find_paths` `se_describe_object` `se_walk_object` `se_batch_read` `se_read_geometry` `se_get_variables` `se_view_context` `se_capture_viewport` `se_snapshot_diff` `se_validate_features` `se_assembly_query` |
| Document session | `se_open_document` `se_new_document` `se_close_document` |
| Model changing | `se_model_build` `se_extrude_on_face` `se_invoke_member` `se_invoke_chain` `se_recipe_run` `se_assembly_build` |
| Escape hatch | `se_script_run` (run a C# script against the COM API) |

Events server (`solidedge-event-mcp`):

| Tool | Purpose |
|---|---|
| `se_get_events` | Read events accumulated in the ring buffer (incremental) |
| `se_wait_event` | Block until a matching event arrives (poll loop helper) |
| `se_set_event_filter` | Enable/disable event sources to cut noise (e.g. silence command events while waiting for recompute) |
| `se_event_status` | Diagnostics: SE connection, per-interface subscription state, buffer stats — start here when events don't fire |

The event hub keeps a ring buffer of 200 events and reads are cursor-based (`afterSeq`), so nothing is lost between incremental reads. By default only the high-frequency `SelectSetChanged` filter is off to cut noise. The event binary also has a small CLI for smoke-testing: `--listen [seconds]` and `--cleanup`.

## Permission model

Set the `SE_MCP_MODE` environment variable on the server entry in your MCP config:

| Value | Behavior |
|---|---|
| `full` *(default)* | All 23 tools allowed |
| `engineer` | All 23 tools allowed, but the free-form invoke channel (`se_invoke_member`/`se_invoke_chain`) only accepts read-only members whose name starts with `get` (e.g. `GetVariables`); model via the guarded tools (`se_model_build`/`se_extrude_on_face`/`se_recipe_run`/`se_assembly_build`) |
| `readonly` | Only the 13 query tools; model-changing/session/script calls are rejected at the transport layer with a hint on how to switch back |
| anything else | Fail-closed: treated as `readonly` |

Legacy `SE_MCP_READONLY=1` is still honored and maps to `readonly`. Changing the mode requires restarting the MCP session (the AI client reloads the server).

Tool risk tiers behind the gate: **Read** (13 query tools) / **Session** (open/new/close document) / **Model** (6 model-changing tools) / **Escape** (`se_script_run`). Unregistered tools are fail-closed (treated as the highest tier).

Other environment variables:

| Variable | Purpose |
|---|---|
| `SE_MCP_TIMEOUT_SECONDS` | Per-COM-call timeout in seconds (default 120, minimum 5). Raise it when working with huge assemblies. |
| `SE_MCP_RECIPES_DIR` | Semicolon-separated extra search paths for recipe JSON files. Without it, recipes are looked up next to the exe (walking up 8 directory levels), then in `%LOCALAPPDATA%\SolidEdgeSpy\recipes`. Files starting with `_` are drafts and cannot be invoked by name. |

There is also a member-level guardrail (`Guardrail`) as a second onion layer, and every tool call is written to an audit log at `%LOCALAPPDATA%\SolidEdgeSpy\mcp-audit.log`. Per-call timing and success/failure go to `tool-usage.jsonl` in the same folder (inspect it with `--usage`), and `se_snapshot_diff` snapshots persist in `%LOCALAPPDATA%\SolidEdgeSpy\snapshots`.

## Requirements

- Windows + a running **Siemens Solid Edge** install (interop package targets SE2022 / type library v108; other versions may work — see below)
- **.NET 8 SDK** to build, or use a published binary
- An MCP-capable AI client

## Build

Or build from source:

```powershell
git clone https://github.com/1337332551-dot/solidedge-mcp-server.git
cd solidedge-mcp
dotnet build src/SolidEdge.Spy.McpServer -c Release
dotnet build src/SolidEdge.Spy.EventMcp  -c Release
dotnet test solidedge-mcp.sln            # 551 unit tests, no Solid Edge required
```

No Siemens files are needed from you up front: the COM interop assembly comes from the community-published [`Interop.SolidEdge`](https://www.nuget.org/packages/Interop.SolidEdge) NuGet package (pure type definitions, no Siemens proprietary code is distributed in this repo).

> Mainland China users: if nuget.org is slow or unreachable, add a mirror before restoring, e.g. `dotnet nuget add source azure-cn -n azure-cn -s https://nuget.cdn.azure.cn/v3/index.json`, or drop this `nuget.config` next to the `.sln`:
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

If you prefer building the interop assembly from your own installed Solid Edge (e.g. for a different SE version), use `scripts/gen_interop.ps1` with the .NET Framework `TlbImp.exe` tool and reference the produced DLLs instead.

## Configure your AI client

Point the server at the built binary. Examples:

**Claude Desktop / Cursor / Cline** (`claude_desktop_config.json` / `mcp.json`):

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

**Trae / CodeBuddy / ZCode** — Chinese AI coding IDEs all support MCP with the same JSON shape (Trae reads `.trae/mcp.json` in your workspace; others use their own MCP settings panel).

Start Solid Edge first, then start a new conversation in your client — the server attaches to the running instance automatically.

## CLI mode

The execution binary doubles as a one-shot CLI for debugging without any AI client:

```powershell
solidedge-mcp.exe get_document
solidedge-mcp.exe invoke_member --objectId <id> --member Name
```

Common switches: `-d` document / `-s` selection / `--vars` variables / `-w` walk / `-desc` describe / `-p` find paths / `--geometry` / `--viewctx` / `--batchread` / `--snap` / `--probe` / `--preview`. Write-style switches (`--newpart`, `--newclose`, `--model`, `--set`, `--openclose`, `--cs`, `--recipe-run`) go through the same permission gate: in `readonly` mode they are rejected before touching Solid Edge. Most short switches have long aliases (`--doc`, `--selection`, `--walk`, `--describe`, `--paths`); `--preview` accepts `-o/--out <file>` and a view name (`iso`/`top`/`front`/.../`current`). Run `solidedge-mcp.exe --help` for the full list.

Utility switches (no Solid Edge required for most):

| Switch | Purpose |
|---|---|
| `--version` / `-v` | Print build timestamp and exit |
| `--usage` / `-u [days]` | Call statistics report from `tool-usage.jsonl` (`--all`, `--by day`, `--sort calls\|ms\|last\|fail`) |
| `--recipes` | List all recipes found (name / status / params / source directory) |
| `--recipe-validate <name\|path>` | Static recipe validation, never touches COM |
| `--dialogs` | Popup probe: enumerate modal dialogs even if Solid Edge is stuck (`--close <hwnd> --confirm` to dismiss one) |

## Architecture

```
AI client (Claude/Cursor/Trae)
   │  stdio JSON-RPC
   ▼
PermissionTap ── mode × tool-risk-tier gate (deny fabricated before SDK)
   ▼
JsonRpcTap ── usage metering / audit
   ▼
MCP SDK tool handlers ── Guardrail member-level checks
   ▼
COM interop (IDispatch + PIA) ── running Solid Edge instance
```

```
src/
├── SolidEdge.Spy.McpServer/        # execution MCP server (23 tools)
├── SolidEdge.Spy.EventMcp/         # events MCP server (4 tools)
├── SolidEdge.Shared/               # COM interop infrastructure shared at compile time
├── SolidEdge.Spy.McpServer.Tests/  # xUnit tests, part 2 (pure logic, no SE needed)
tests/SolidEdge.Spy.McpServer.Tests/ # xUnit tests, part 1 (pure logic, no SE needed)
recipes/                            # example recipe JSON files (see recipes/README.md)
scripts/                            # helper scripts (interop generation)
```

551 unit tests across the two xUnit projects (`dotnet test solidedge-mcp.sln`).

## Development

```powershell
dotnet test solidedge-mcp.sln
```

Tests are pure .NET (no Solid Edge required) and cover parsing, validation rules, the permission tier table, and the transport tap.

## FAQ

**The server says it cannot connect to Solid Edge.**
Start Solid Edge first. The server attaches to the running instance automatically and retries on the next tool call — startup never blocks on it.

**A tool call was rejected with "已拒绝 ... SE_MCP_MODE".**
You are in a restricted mode: `readonly` blocks write-tier tools entirely; `engineer` only allows `get`-prefixed members on the free-form invoke channel. Set `SE_MCP_MODE=full` (or remove the variable) in your MCP config and restart the session.

**I changed the config but nothing happened.**
MCP servers are spawned by the AI client when the session starts. Restart the conversation after any `mcp.json` change — tools cached from the old process keep serving until then.

**Which Solid Edge versions are supported?**
The interop package version matches the SE type-library version: `108.0.0` = SE2022. For other SE versions, bump the `Interop.SolidEdge` PackageReference to the matching version (105–220 exist on NuGet), or generate interop assemblies from your own install with `scripts/gen_interop.ps1`.

**Is it safe to let an AI operate my CAD?**
Defense in depth: transport-layer mode gate (readonly/full), member-level guardrail on write calls, document-session tracking (`close` only closes documents the session itself opened), and an audit log of every tool call at `%LOCALAPPDATA%\SolidEdgeSpy\mcp-audit.log`. `dry-run` validation runs before any real modeling change, and `se_invoke_member` re-reads a property after writing it (`verify=true` by default) to catch silent no-ops.

**The AI hangs when Solid Edge shows a dialog.**
It doesn't: a popup probe detects modal dialogs and reports their title and buttons instead of blocking until timeout.

## Project status

The feature-spec JSON — the intermediate representation (IR) that `se_model_build` consumes — is still **rough and evolving**: op coverage, defaults and field names may change between versions. If you build workflows on it, pin a commit and expect churn. A small set of **example recipes** (extrude, revolve, cut, read-only probes, and a guardrail self-test) ships in [`recipes/`](recipes/) — drop your own next to them, or point `SE_MCP_RECIPES_DIR` at another folder. Feedback from real parametric-modeling use cases is especially valuable — open an issue and tell us what you tried to model.

## Roadmap

- [ ] Stabilize the feature-spec IR (v1)
- [ ] Published Release binaries (no SDK needed to try)
- [ ] GitHub Actions CI (build + test on push)
- [ ] More recipe examples (drawing automation, BOM extraction)
- [ ] Tool documentation site

Contributions welcome — open an issue or PR.

## Credits & license

- COM interop infrastructure (`InteropServices/*`, extensions) derives from [Jason Newell's SolidEdgeSpy](https://github.com/JWSingleton/SolidEdgeSpy) — this project started as a fork of that codebase.
- Interop assemblies published by the [Solid Edge Community](https://github.com/SolidEdgeCommunity).

MIT — see [LICENSE](LICENSE).

> This project is not affiliated with or endorsed by Siemens. Solid Edge is a trademark of Siemens Digital Industries Software.
