using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_batch_read:一次读取多个对象的同组成员,把"N 个对象 × M 个属性"压缩成 1 次 MCP 调用。
///
/// 存在的理由(来自实测痛点):工程图排错时反复出现"要一次列出 5~10 个标注的 Value / 投影角 / 包围盒",
/// 每次都只能写一段一次性 se_script_run C# 脚本 —— 脚本通道适合控制流,但纯读取属于通用原语,值得做成工具。
///
/// 通道:只读,走 ManualInvoke.TryInvoke(IDispatch),不写任何东西、不需要 confirm、不违反护栏;
/// 单个成员读失败只记进 errors,不影响同一行的其它成员(与 se_describe_object 的"读不到就如实报"一致)。
/// </summary>
[McpServerToolType]
public static class BatchReadTools
{
	[McpServerTool]
	[Description("批量读取多个对象(obj-N)的同一组成员的只读值,把 N×M 次查询压成 1 次调用。" +
		"targets 传对象编号数组(如 [\"obj-1\",\"obj-2\"]),members 传成员名数组(属性或无参方法,如 [\"Value\",\"ProjectionLineAngle1\",\"Range\"]);" +
		"members 省略时只返回身份与几何指纹。每行带 identity(类型|Name|Key)、geometric(与 ID 无关的几何指纹,重建后仍一致,适合幂等判据)" +
		"与 values(成员名→值/类型/单位提示)。纯只读;读不到的成员进 errors。单位:API 长度=米、角度=弧度(unitNote=true 时附 mm/度换算)。\n" +
			"分工:单对象也走本工具(targets=[\"obj-1\"]);全接口/方法清单发现用 se_script_run 探查。")]
	public static string se_batch_read(
		SolidEdgeContext context,
		[Description("对象编号列表,如 [\"obj-1\",\"obj-2\"];单个对象也请用数组")] string[] targets,
		[Description("要读取的成员名列表(属性或无参方法,如 [\"Value\",\"ProjectionLineAngle1\"]);留空=只返回身份/几何指纹")] string[] members = null,
		[Description("是否附身份(identity)与几何(geometric)指纹,默认 true")] bool includeFingerprint = true,
		[Description("可选,数值是否附单位换算提示(米→mm、弧度→度),默认 true")] bool unitNote = true)
	{
		try
		{
			if (targets == null || targets.Length == 0)
			{
				return Error("targets 不能为空,请传对象编号列表,如 [\"obj-1\",\"obj-2\"]。句柄来自 se_get_selection 或 se_invoke_member 的返回值。");
			}
			return context.Invoke(() => ReadCore(context, targets, members, includeFingerprint, unitNote));
		}
		catch (Exception ex)
		{
			if (SolidEdgeContext.IsDisconnected(ex))
			{
				return Error("COM 对象已断连(文档可能已被关闭,或对象已删除/句柄失效)。请重新调用 se_get_selection 获取新句柄,不要继续用旧 obj-N。原始错误: " + DescribeException(ex));
			}
			return Error("批量读取失败: " + DescribeException(ex));
		}
	}

	private static string ReadCore(SolidEdgeContext context, string[] targets, string[] members, bool includeFingerprint, bool unitNote)
	{
		var rows = new List<object>();
		int okCount = 0;
		int rowFails = 0;   // P2-0:行级成员读取失败总数(信封自报,供 tap 计量)
		foreach (string raw in targets)
		{
			string targetId = (raw ?? "").Trim();
			ObjectHandle handle = context.GetHandle(targetId);
			if (handle == null || handle.ComObject == null)
			{
				rows.Add(new
				{
					target = targetId,
					status = "error",
					message = "找不到对象编号 " + targetId + "(句柄表已清空或从未登记)。句柄只由 se_get_selection 与 se_invoke_member 的返回值产生,且 se_get_selection 会清空旧句柄表。"
				});
				continue;
			}

			object com = handle.ComObject;
			var values = new Dictionary<string, object>();
			var errors = new Dictionary<string, string>();
			if (members != null)
			{
				foreach (string m in members)
				{
					string name = (m ?? "").Trim();
					if (name.Length == 0) continue;
					object val;
					Exception readErr;
					if (!ManualInvoke.TryInvoke(com, name, null, out val, out readErr))
					{
						// P2-2: 旧 hint 是错误归因(把"句柄失效"误诊成"成员不存在"),改为有序排查
						errors[name] = "读取失败: " + DescribeException(readErr)
							+ "。排查顺序:① 若近期改过拓扑/参数(重算/重建),旧几何对象句柄可能已失效——重新取对象(se_find_objects/se_get_selection)后再读;"
							+ "② 核对该成员是否需要参数(需要参数的方法/只写属性会报同类错误,成员签名用 se_script_run 探查);"
							+ "③ 消息里的内部 HRESULT 可按码表判断具体原因。";
						continue;
					}
					values[name] = DescribeValue(context, val, name, unitNote);
				}
			}

			rows.Add(new
			{
				target = targetId,
				status = (errors.Count > 0) ? "partial" : "ok",   // P2-0:有行级失败就如实标,不再顶着 ok
				type = handle.TypeName,
				displayName = handle.DisplayName,
				identity = includeFingerprint ? ObjectFingerprint.Identity(com) : null,
				geometric = includeFingerprint ? ObjectFingerprint.GeometricKey(com) : null,
				values = (values.Count > 0) ? values : null,
				errors = (errors.Count > 0) ? errors : null
			});
			okCount++;
			rowFails += errors.Count;
		}

		return JsonSerializer.Serialize(new
		{
			status = "ok",
			count = rows.Count,
			ok = okCount,
			rowFails = rowFails,   // P2-0:行级失败计数——JsonRpcTap 据此把该调用记为失败,不再"假成功"
			rows = rows,
			unitNote = "Solid Edge API 返回的长度/距离/坐标值为内部单位米(m),UI 显示毫米(mm) 换算 ×1000;角度值为弧度(rad)。",
			hint = "幂等/回归对比请用 geometric 字段(与对象 ID 无关,重建后依然一致);要改属性用 se_invoke_member(propertySet=true,可加 verify=true 自动回读校验);要存/比整批状态用 se_snapshot_diff。"
		});
	}

	private static object DescribeValue(SolidEdgeContext context, object value, string name, bool unitNote)
	{
		if (value == null)
		{
			// P3-2b:成员真实返回 null(读取失败走 errors,不会到这)——说明语义,防止 AI 当成读失败或漏看
			return new { value = (string)null, type = "(null)", unitNote = (string)null, note = "成员真实返回 null(对象模型的空引用,非读取失败)" };
		}
		if (Marshal.IsComObject(value))
		{
			string typeName = ObjectFingerprint.TypeName(value);
			string display = ObjectFingerprint.DisplayName(value);
			string handle = null;
			string hint = null;
			try
			{
				handle = context.AddHandle(value, typeName ?? "(未知)", display ?? "(无名称)");
				hint = "已登记句柄 " + handle + ",可再传给本工具的 targets 继续读成员值。";
			}
			catch (Exception ex)
			{
				// P3-2b:句柄登记失败不再静默吞,值照常返回但说明下钻不可用及原因
				hint = "句柄登记失败(本条拿到的是值快照,后续无法再用句柄下钻): " + ex.Message;
			}
			return new { value = display, type = typeName, handle = handle, hint = hint };
		}
		return new
		{
			value = ObjectFingerprint.Str(value),
			type = value.GetType().Name,
			unitNote = unitNote ? UnitNoteOf(name, value) : null
		};
	}

	/// <summary>按成员名启发式给数值附单位提示(与 se_describe_object 同一思路:宁可不标,不可误标)。</summary>
	private static string UnitNoteOf(string name, object value)
	{
		if (!(value is double d) || double.IsNaN(d) || double.IsInfinity(d))
		{
			return null;
		}
		string n = name ?? "";
		if (n.IndexOf("Angle", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			// 超出 2π 的多半是哨兵值(如 TrackAngle=-99),换算成度没有意义
			if (Math.Abs(d) <= 6.283185307179586)
			{
				return "角度:弧度 = " + (d * 180.0 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture) + " 度";
			}
			return null;
		}
		if (n.EndsWith("Distance", StringComparison.OrdinalIgnoreCase)
			|| n.EndsWith("Length", StringComparison.OrdinalIgnoreCase)
			|| n.EndsWith("Radius", StringComparison.OrdinalIgnoreCase)
			|| n.StartsWith("Track", StringComparison.OrdinalIgnoreCase)
			|| n.StartsWith("Break", StringComparison.OrdinalIgnoreCase)
			|| n.StartsWith("Leader", StringComparison.OrdinalIgnoreCase)
			|| n.StartsWith("Radial", StringComparison.OrdinalIgnoreCase))
		{
			return "长度:内部单位米 = " + (d * 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " mm";
		}
		return null;
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new { status = "error", message = message });
	}

	private static string DescribeException(Exception ex)
	{
		// P2-1:统一走 EvidenceContract(过去 7 份私有副本行为漂移,已收编)
		return EvidenceContract.DescribeException(ex);
	}
}
