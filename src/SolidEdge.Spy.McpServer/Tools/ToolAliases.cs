using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 工具名别名层(2026-11-07 工具面精简)。
///
/// 背景:三个工具并入别的工具(se_validate_features→se_model_build dryRun、
/// se_model_state_snapshot→se_snapshot_diff scope=faces、se_verify_model→se_read_model_state verify),
/// 旧名不再进 tools/list,但调用仍须成功——老会话缓存的工具清单、技能文档与历史经验里的
/// 旧引用、人的肌肉记忆都不应断。本层在 PermissionTap(请求方向)把旧名透明改写为新名,
/// 审计留痕(AuditLog "alias old→new")。
///
/// 铁律:
/// ① 未命中别名表的工具名一律原样放行,本层零开销、零改写;
/// ② 改写只动 params.name 与 params.arguments(映射的参数变换),其余字段经 JsonNode 往返不动语义;
/// ③ 权限档位检查、还原点 hook、遥测都以【新名】为准——旧名不进 ToolRisk 登记表(fail-closed 不误伤);
/// ④ 改写失败宁可拒绝也不把坏行交给 SDK(与 PermissionTap 铁律①同源);
/// ⑤ 目标工具必须在 ToolRisk 登记表(ValidateMappings 单测不变式把关)。
/// </summary>
internal static class ToolAliases
{
	/// <summary>一条别名映射:旧名 → 新名(必须在 ToolRisk 登记),可选参数变换。</summary>
	internal sealed class AliasMapping
	{
		public string Target;
		public Action<JsonObject> TransformArguments; // 可空 = arguments 原样透传
		public string Note;                           // 审计用一句话
	}

	private static readonly Dictionary<string, AliasMapping> Mappings =
		new Dictionary<string, AliasMapping>(StringComparer.Ordinal);

	static ToolAliases()
	{
		RegisterProductionMappings();
	}

	/// <summary>
	/// 生产映射登记(幂等,随各合并步加一条;删工具 = 在这里加一行别名,
	/// 见 docs\工具面精简-实施计划-20261107.md)。ResetForTests 后由本方法恢复生产态。
	/// </summary>
	internal static void RegisterProductionMappings()
	{
		// Step 1(2026-11-07):静态校验并入 se_model_build——dryRun=true 即校验,不启动 COM。
		// features/featuresFile/sets/assumeStock 两工具同名,原样透传。
		Add("se_validate_features", "se_model_build",
			args => args["dryRun"] = true,
			"静态校验已并入 se_model_build:dryRun=true 即校验,不启动 COM");

		// Step 2(2026-11-07):拓扑面级快照并入 se_snapshot_diff——scope=faces。
		// action/name/areaTolerancePct/maxItems/objectId 同名透传。
		Add("se_model_state_snapshot", "se_snapshot_diff",
			args => args["scope"] = "faces",
			"拓扑快照已并入 se_snapshot_diff:scope=faces,配对按 Face.ID");

		// Step 3(2026-11-07):尺寸自检并入 se_read_model_state——expects 改名 verify。
		// toleranceMm/modelIndex 同名透传。
		Add("se_verify_model", "se_read_model_state",
			args =>
			{
				if (args["expects"] != null)
				{
					args["verify"] = args["expects"].DeepClone();
					args.Remove("expects");
				}
			},
			"尺寸自检已并入 se_read_model_state:verify=expects,返回 checks[]/verdict");
	}

	/// <summary>解析别名;命中返回 true。未命中是绝对多数路径,哈希一次查询。</summary>
	internal static bool TryResolve(string tool, out AliasMapping mapping)
	{
		if (tool == null)
		{
			mapping = null;
			return false;
		}
		return Mappings.TryGetValue(tool, out mapping);
	}

	/// <summary>
	/// 不变式校验:每条映射的目标都必须在 ToolRisk 登记表里,否则改写后的调用会被
	/// fail-closed 裸拒(比旧名直调更糟)。返回违规清单(空=通过),供单测断言。
	/// </summary>
	internal static List<string> ValidateMappings()
	{
		var violations = new List<string>();
		foreach (KeyValuePair<string, AliasMapping> kv in Mappings)
		{
			if (kv.Value == null || kv.Value.Target == null || !ToolRisk.TierOf(kv.Value.Target).HasValue)
			{
				violations.Add(kv.Key + " → " + (kv.Value == null ? "(null)" : kv.Value.Target ?? "(null)"));
			}
		}
		return violations;
	}

	/// <summary>生产映射登记(各合并步在静态构造里调用)。私有:生产映射不允许运行期改动。</summary>
	private static void Add(string oldName, string target, Action<JsonObject> transform, string note)
	{
		Mappings[oldName] = new AliasMapping { Target = target, TransformArguments = transform, Note = note };
	}

	internal static void ResetForTests()
	{
		Mappings.Clear();
		RegisterProductionMappings(); // 生产映射恢复(测试注入的临时映射不保留)
	}

	/// <summary>仅测试用:注入临时映射,绝不进生产路径。</summary>
	internal static void AddMappingForTests(string oldName, string target, Action<JsonObject> transform, string note)
	{
		Add(oldName, target, transform, note);
	}
}
