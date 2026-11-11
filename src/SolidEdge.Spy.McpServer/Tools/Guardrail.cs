using System;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>一次成员调用的风险级别。</summary>
internal enum InvocationRisk
{
	/// <summary>只读查询:取值/遍历/计数等,无副作用。</summary>
	Normal = 0,

	/// <summary>会改变模型或文档:写属性、Add*/Set*/Move 等。允许执行,但需审计并提示会触发重算。</summary>
	ModelChanging = 1,

	/// <summary>破坏性:Delete/Cut/Drop/Remove* 等,默认拒绝,必须显式 confirm=true。</summary>
	Destructive = 2
}

/// <summary>
/// 写能力护栏。设计取舍:
/// - 只按成员名做静态分级,不解析参数,因此在 invoke 之前就能拦截。
/// - 黑名单刻意收窄:只把"不可逆"的操作定为 Destructive。
///   建模主力成员(AddFiniteExtrudedProtrusion / Profiles.Add / End 等)属于 ModelChanging,
///   默认放行——否则现有建模配方(se_invoke_chain 建特征)会被整体瘫痪。
/// - 全局只读开关默认关闭,需要时设环境变量 SE_MCP_READONLY=1。
/// </summary>
internal static class Guardrail
{
	private static readonly string[] DestructiveExact =
	{
		"Delete",
		"Cut",
		"Drop",
		"Erase",
		"Purge"
	};

	private static readonly string[] DestructivePrefix =
	{
		"Remove"
	};

	private static readonly string[] ChangingExact =
	{
		"Move",
		"Rotate",
		"Scale",
		"Mirror",
		"Copy",
		"Duplicate",
		"Insert"
	};

	private static readonly string[] ChangingPrefix =
	{
		"Set",
		"Add",
		"Replace",
		"Convert",
		"Apply",
		"Clear",
		"Update"
	};

	/// <summary>全局只读开关(由 Program 启动时从 SE_MCP_READONLY 读取)。开启后一切写操作全部拒绝。</summary>
	internal static bool ReadOnlyEnabled { get; set; }

	internal static InvocationRisk Classify(string member, bool propertySet)
	{
		if (string.IsNullOrWhiteSpace(member))
		{
			return InvocationRisk.Normal;
		}
		string text = member.Trim();
		string[] destructiveExact = DestructiveExact;
		foreach (string value in destructiveExact)
		{
			if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
			{
				return InvocationRisk.Destructive;
			}
		}
		string[] destructivePrefix = DestructivePrefix;
		foreach (string value2 in destructivePrefix)
		{
			if (text.StartsWith(value2, StringComparison.OrdinalIgnoreCase))
			{
				return InvocationRisk.Destructive;
			}
		}
		if (propertySet)
		{
			return InvocationRisk.ModelChanging;
		}
		string[] changingExact = ChangingExact;
		foreach (string value3 in changingExact)
		{
			if (string.Equals(text, value3, StringComparison.OrdinalIgnoreCase))
			{
				return InvocationRisk.ModelChanging;
			}
		}
		string[] changingPrefix = ChangingPrefix;
		foreach (string value4 in changingPrefix)
		{
			if (text.StartsWith(value4, StringComparison.OrdinalIgnoreCase))
			{
				return InvocationRisk.ModelChanging;
			}
		}
		return InvocationRisk.Normal;
	}

	/// <summary>门禁判定。返回 null 表示放行,否则返回应回给调用方的错误说明。</summary>
	internal static string Check(string tool, string member, bool propertySet, bool confirm, out InvocationRisk risk)
	{
		risk = Classify(member, propertySet);
		if (risk == InvocationRisk.Normal)
		{
			return null;
		}
		if (ReadOnlyEnabled)
		{
			return "已拒绝写操作:全局只读开关处于开启状态(SE_MCP_READONLY=1)。成员 " + member.Trim() + " 属于" + Describe(risk)
				+ "。若确实要写入,请在 MCP 配置里把环境变量 SE_MCP_READONLY 设为 0(或删除该变量)后重启 MCP server。";
		}
		if (risk == InvocationRisk.Destructive && !confirm)
		{
			// P3-3 反误导闸门:拒绝消息里带上证据核对规则,让 confirm 不只是"再说一次"
			return "已拒绝高危操作:成员 " + member.Trim() + " 属于" + Describe(risk)
				+ "。确认要执行请追加 confirm=true 再调用一次;不确认则不会执行。"
				+ "【闸门】删除前必须核对目标证据:仅 featureHealth=igFeatureFailed(几何未生成的僵尸,Status=1216476311)"
				+ "可删;igFeatureWarned/Suppressed/RolledBack 一律保留,未知状态码原样上报交人工确认;"
				+ "证据须为 COM 直读(confidence=observed),inferred/读失败的不作为删除依据。";
		}
		return null;
	}

	internal static string Describe(InvocationRisk risk)
	{
		switch (risk)
		{
		case InvocationRisk.ModelChanging:
			return "写操作(会改变模型/文档,通常触发重算)";
		case InvocationRisk.Destructive:
			return "破坏性操作(删除/移除,不可逆)";
		default:
			return "只读查询";
		}
	}
}
