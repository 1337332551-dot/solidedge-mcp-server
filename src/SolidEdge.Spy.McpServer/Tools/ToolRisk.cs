using System;
using System.Collections.Generic;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>权限模式:readonly=只放 Read 档;engineer=工具档全放但自由调用通道限 get 前缀成员;full=全放。</summary>
internal enum McpMode
{
	ReadOnly = 0,
	Full = 1,
	Engineer = 2
}

/// <summary>工具风险档位(洋葱外层,按整个工具粒度;成员级分级在内层 Guardrail)。</summary>
internal enum RiskTier
{
	/// <summary>只读查询/静态校验/瞬态视图:不改文档不改模型。</summary>
	Read = 0,

	/// <summary>会话操作:打开/新建/关闭文档。</summary>
	Session = 1,

	/// <summary>建模/写模型:声明式建模、成员调用、配方。</summary>
	Model = 2,

	/// <summary>逃逸通道:任意代码执行(se_script_run)。</summary>
	Escape = 3
}

/// <summary>
/// 权限洋葱外层:工具名 → 风险档位 的静态登记表 + 模式门禁。
/// - 三种模式:readonly(只放 Read 档)/ engineer(机械工程师:工具档全放,自由调用通道限 get 前缀成员)/ full(全放)。
///   模式由环境变量决定,进程启动时 Configure 一次:
///   SE_MCP_MODE=readonly|engineer|full;旧 SE_MCP_READONLY=1 兼容映射 readonly;SE_MCP_MODE 给了未识别值则 fail-closed 按只读。
/// - fail-closed:未登记的工具名一律拒绝(防新工具漏登记直接裸奔)。
/// - readonly 模式下非 Read 档工具在注册期即被滤除、不进 tools/list(Program.DiscoverVisibleTools),
///   工具描述不暴露给 AI;engineer/full 模式工具全量注册,限制只发生在 tools/call,
///   拒绝信息带切换指路(改环境变量+重启会话)。
/// - 与内层 Guardrail 的分工:这里管"整个工具能不能调",Guardrail 管"成员级风险分级+confirm"。
/// </summary>
internal static class ToolRisk
{
	// ⚠️ 新增 [McpServerTool] 工具时必须同步登记,否则只读模式下被 fail-closed 拒绝。
	//    有单测"全部McpServerTool方法都已登记"反射对账兜底。
	private static readonly Dictionary<string, RiskTier> Table = new Dictionary<string, RiskTier>(24, StringComparer.Ordinal)
	{
		// ---- Read 档(12):只读查询/静态校验/瞬态视图,不产生模型副作用 ----
		["se_get_document"] = RiskTier.Read,
		["se_get_selection"] = RiskTier.Read,
		["se_find_paths"] = RiskTier.Read,
		["se_describe_object"] = RiskTier.Read,
		["se_walk_object"] = RiskTier.Read,
		["se_batch_read"] = RiskTier.Read,
		["se_read_geometry"] = RiskTier.Read,
		["se_get_variables"] = RiskTier.Read,
		["se_view_context"] = RiskTier.Read,
		["se_capture_viewport"] = RiskTier.Read,
		["se_snapshot_diff"] = RiskTier.Read,      // 本地快照 store,不碰 COM
		["se_validate_features"] = RiskTier.Read,  // 纯静态校验,不碰 COM
		["se_assembly_query"] = RiskTier.Read,     // 装配只读查询(occurrences/relations/bom),只读模式可用

		// ---- Session 档(3):文档会话操作 ----
		["se_open_document"] = RiskTier.Session,
		["se_new_document"] = RiskTier.Session,
		["se_close_document"] = RiskTier.Session,

		// ---- Model 档(6):写模型/装配 ----
		["se_model_build"] = RiskTier.Model,
		["se_extrude_on_face"] = RiskTier.Model,
		["se_invoke_member"] = RiskTier.Model,
		["se_invoke_chain"] = RiskTier.Model,
		["se_recipe_run"] = RiskTier.Model,
		["se_assembly_build"] = RiskTier.Model,    // 装配声明式操作(place/constrain/...),破坏性 op 另需 confirm

		// ---- Escape 档(1):任意代码执行,只读模式整体禁用 ----
		["se_script_run"] = RiskTier.Escape
	};

	/// <summary>当前权限模式。默认 full(与历史行为一致);Program/CliRunner 启动时 Configure。</summary>
	internal static McpMode Mode { get; private set; } = McpMode.Full;

	/// <summary>已登记的工具名(单测用:反射对账,防新工具漏登记)。</summary>
	internal static IEnumerable<string> RegisteredTools
	{
		get { return Table.Keys; }
	}

	/// <summary>查档位;未登记返回 null。</summary>
	internal static RiskTier? TierOf(string tool)
	{
		RiskTier tier;
		return (tool != null && Table.TryGetValue(tool, out tier)) ? tier : (RiskTier?)null;
	}

	/// <summary>门禁判定。返回 null=放行;否则返回应回给 AI 的拒绝说明(含切换指路)。</summary>
	internal static string Check(string tool)
	{
		if (Mode == McpMode.Full)
		{
			return null;
		}
		RiskTier? tier = TierOf(tool);
		if (tier == null)
		{
			return "已拒绝:工具 '" + (tool ?? "(null)") + "' 未在权限档位表登记,按最高危处理(fail-closed)。请检查工具名拼写;若确为新工具,请在 ToolRisk 登记表补登记。";
		}
		if (tier == RiskTier.Read)
		{
			return null;
		}
		if (Mode == McpMode.Engineer)
		{
			return null;
		}
		return "已拒绝:当前处于只读模式(SE_MCP_MODE=readonly),工具 " + tool + " 属于" + DescribeTier(tier.Value)
			+ "。只读模式只放行查询类工具。如需放开,请把 MCP 配置里的环境变量 SE_MCP_MODE 改为 full(或删除该变量)后重启会话重载 MCP server。";
	}

	/// <summary>
	/// engineer 模式的成员级白名单:仅对自由调用通道(se_invoke_member/se_invoke_chain)生效,
	/// 成员名须以 "get" 开头(忽略大小写,覆盖 GetXxx / get_xxx 两类 SE 读取 API);
	/// 其余模式/其余工具一律放行(返回 null)。
	/// 配方(se_recipe_run)内部步骤不在此过滤——配方是预审过的打包件,走工具级门禁。
	/// </summary>
	internal static string CheckMember(string tool, string member)
	{
		if (Mode != McpMode.Engineer || member == null)
		{
			return null;
		}
		if (tool != "se_invoke_member" && tool != "se_invoke_chain")
		{
			return null;
		}
		if (member.TrimStart().StartsWith("get", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return "已拒绝:机械工程师模式下," + tool + " 只允许调用名字以 \"get\" 开头的读取类成员,'" + member.Trim() + "' 不符合。建模请走 se_model_build / se_extrude_on_face / se_recipe_run,探索请走 se_walk_object / se_describe_object;如需任意成员调用,请把 MCP 配置里 SE_MCP_MODE 改为 full 后重启会话重载。";
	}

	internal static string DescribeTier(RiskTier tier)
	{
		switch (tier)
		{
		case RiskTier.Session:
			return "会话档(打开/新建/关闭文档)";
		case RiskTier.Model:
			return "建模档(创建特征/改模型)";
		case RiskTier.Escape:
			return "逃逸档(任意代码执行)";
		default:
			return "只读档";
		}
	}

	/// <summary>
	/// 启动时解析模式。返回给人看的解析说明(进日志)。
	/// 优先级:SE_MCP_MODE &gt; 旧 SE_MCP_READONLY &gt; 默认 full。
	/// </summary>
	internal static string Configure(string modeEnv, string legacyReadOnlyEnv)
	{
		if (!string.IsNullOrWhiteSpace(modeEnv))
		{
			string m = modeEnv.Trim();
			if (m.Equals("readonly", StringComparison.OrdinalIgnoreCase))
			{
				Mode = McpMode.ReadOnly;
				return "readonly (env SE_MCP_MODE=readonly)";
			}
			if (m.Equals("engineer", StringComparison.OrdinalIgnoreCase))
			{
				Mode = McpMode.Engineer;
				return "engineer (env SE_MCP_MODE=engineer)";
			}
			if (m.Equals("full", StringComparison.OrdinalIgnoreCase))
			{
				Mode = McpMode.Full;
				return "full (env SE_MCP_MODE=full)";
			}
			Mode = McpMode.ReadOnly;
			return "readonly (fail-closed: SE_MCP_MODE='" + m + "' 未识别,只认 readonly|engineer|full)";
		}
		bool legacy = !string.IsNullOrWhiteSpace(legacyReadOnlyEnv)
			&& (legacyReadOnlyEnv == "1" || legacyReadOnlyEnv.Equals("true", StringComparison.OrdinalIgnoreCase));
		Mode = legacy ? McpMode.ReadOnly : McpMode.Full;
		return legacy ? "readonly (legacy SE_MCP_READONLY=1)" : "full (默认)";
	}
}
