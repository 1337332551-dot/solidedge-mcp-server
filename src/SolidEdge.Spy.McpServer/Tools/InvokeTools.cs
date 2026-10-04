using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol.Server;
using SolidEdge.Spy.InteropServices;

namespace SolidEdge.Spy.McpServer.Tools;

[McpServerToolType]
public static class InvokeTools
{
	private sealed class ArgSlot
	{
		public ComParameterInfo Param;

		public object Value;

		public bool ByRef;

		public bool Collect;
	}

	private enum UdKind
	{
		Unknown,
		Enum,
		Interface,
		Other
	}

	private static int _typeLibsLoaded;

	[McpServerTool]
	[Description("批量执行多步成员调用(一次 MCP 调用跑完整条依赖链)。每步 {member, args[], on?};on 指定本步调用目标(默认=上一步返回值,首步=起始对象),支持 \"$$\"(起始对象)/\"$N\"(第N步返回值)/\"obj-K\"(已登记句柄);args 支持占位符: \"$$\"=起始对象, \"$N\"=第 N 步(1-based)返回的 COM 对象。所有步骤在同一 STA 调用内串行执行,中间句柄不回传即可串联,是大批量建模(如椅子/桌子的多特征)的推荐方式。返回值/out 中的 COM 对象自动登记句柄表。任一步失败立即中断并返回已完成步骤与错误。注意:$N 引用的是第 N 步的返回值,编号从 1 开始。护栏:整条链在执行前按最高危步骤判定,含破坏性成员(Delete/Cut/Drop/Remove* 等)时需 confirm=true 才执行;改变模型的步骤会记入审计日志。")]
	public static string se_invoke_chain(SolidEdgeContext context, [Description("起始对象句柄,如 \"obj-1\"。后续步骤可用 \"$$\" 引用它。")] string objectId, [Description("步骤数组,每步 {\"member\":\"方法/属性\", \"args\":[...]}。args 可用 \"$$\" / \"$N\" 引用前序对象。")] JsonElement[] steps, [Description("可选,链中含破坏性成员(Delete/Cut/Drop/Remove* 等)时需显式置 true,否则整条链在执行前被拒绝。默认 false")] bool confirm = false)
	{
		try
		{
			return context.Invoke(delegate
			{
				ObjectHandle handle = context.GetHandle(objectId);
				if (handle == null)
				{
					return Error("找不到起始对象编号 " + objectId + "。请先调用 se_get_selection 获取。");
				}
				object comObject = handle.ComObject;
				if (comObject == null)
				{
					return Error("起始对象已失效,请重新调用 se_get_selection。");
				}
				// 整条链按最高危步骤判定:只要有一处是破坏性操作,就在执行前拦下来,避免跑到一半才失败。
				InvocationRisk chainRisk = InvocationRisk.Normal;
				string riskiestMember = null;
				if (steps != null)
				{
					foreach (JsonElement stepElem in steps)
					{
						if (stepElem.TryGetProperty("member", out var memberElem) && memberElem.ValueKind == JsonValueKind.String)
						{
							string memberFilter = ToolRisk.CheckMember("se_invoke_chain", memberElem.GetString());
							if (memberFilter != null)
							{
								AuditLog.Write("se_invoke_chain", objectId, memberElem.GetString(), false, null, InvocationRisk.Normal, false, "blocked by engineer member filter");
								return Error(memberFilter);
							}
							InvocationRisk stepRisk = Guardrail.Classify(memberElem.GetString(), false);
							if (stepRisk > chainRisk)
							{
								chainRisk = stepRisk;
								riskiestMember = memberElem.GetString();
							}
						}
					}
				}
				string chainGuard = Guardrail.Check("se_invoke_chain", riskiestMember ?? "(chain)", false, confirm, out _);
				string chainArgs = (steps == null) ? null : (steps.Length + " steps");
				if (chainGuard != null)
				{
					AuditLog.Write("se_invoke_chain", objectId, riskiestMember ?? "(chain)", false, chainArgs, chainRisk, false, "blocked by guardrail");
					return Error(chainGuard);
				}
				if (chainRisk != InvocationRisk.Normal)
				{
					AuditLog.Write("se_invoke_chain", objectId, riskiestMember ?? "(chain)", false, chainArgs, chainRisk, true, null);
				}
				List<object> stepResults = new List<object>();
				Dictionary<int, string> stepHandles = new Dictionary<int, string>();
				List<object> list = new List<object>();
				for (int i = 0; i < steps.Length; i++)
				{
					if (!steps[i].TryGetProperty("member", out var value) || value.ValueKind != JsonValueKind.String)
					{
						return Error("第 " + (i + 1) + " 步缺少 \"member\" 字符串字段。");
					}
					string text = value.GetString();
					string[] array = null;
					if (steps[i].TryGetProperty("args", out var value2) && value2.ValueKind == JsonValueKind.Array)
					{
						List<string> list2 = new List<string>();
						foreach (JsonElement item2 in value2.EnumerateArray())
						{
							list2.Add((item2.ValueKind == JsonValueKind.String) ? item2.GetString() : item2.ToString());
						}
						array = list2.ToArray();
					}
					string typeNameHint = null;
					object targetObj;
					if (steps[i].TryGetProperty("on", out var value3) && value3.ValueKind == JsonValueKind.String)
					{
						string text2 = value3.GetString();
						int result;
						if (text2 == "$$")
						{
							targetObj = comObject;
							typeNameHint = handle.TypeName;
						}
						else if (text2.StartsWith("$") && int.TryParse(text2.Substring(1), out result) && result >= 1 && result <= stepResults.Count)
						{
							targetObj = stepResults[result - 1];
						}
						else
						{
							if (!text2.StartsWith("obj-"))
							{
								return Error("第 " + (i + 1) + " 步的 \"on\" 无效: " + text2 + "(支持 $$ / $N / obj-K)。");
							}
							ObjectHandle handle2 = context.GetHandle(text2);
							if (handle2 == null)
							{
								return Error("第 " + (i + 1) + " 步的 \"on\" 句柄不存在: " + text2);
							}
							targetObj = handle2.ComObject;
							typeNameHint = handle2.TypeName;
						}
					}
					else
					{
						object obj = ((stepResults.Count > 0) ? stepResults[stepResults.Count - 1] : comObject);
						if (obj == null || !Marshal.IsComObject(obj))
						{
							targetObj = comObject;
							typeNameHint = handle.TypeName;
						}
						else
						{
							targetObj = obj;
						}
					}
					if (array != null)
					{
						for (int j = 0; j < array.Length; j++)
						{
							string text3 = array[j];
							if (text3.StartsWith("@arr:", StringComparison.OrdinalIgnoreCase))
							{
								string[] array2 = text3.Substring(5).Split(',');
								bool flag = false;
								for (int k = 0; k < array2.Length; k++)
								{
									string text4 = ResolveStepToken(array2[k].Trim());
									if (text4 != null)
									{
										array2[k] = text4;
										flag = true;
									}
								}
								if (flag)
								{
									array[j] = "@arr:" + string.Join(",", array2);
								}
							}
							else
							{
								string text5 = ResolveStepToken(text3);
								if (text5 != null)
								{
									array[j] = text5;
								}
							}
						}
					}
					var (json, item, text6) = RunStep(context, targetObj, typeNameHint, objectId, text, array);
					if (text6 != null)
					{
						return JsonSerializer.Serialize(new
						{
							status = "error",
							objectId = objectId,
							failedStep = i + 1,
							failedMember = text,
							message = text6,
							completedSteps = list
						});
					}
					stepResults.Add(item);
					JsonElement jsonElement = JsonSerializer.Deserialize<JsonElement>(json);
					if (jsonElement.TryGetProperty("returnValue", out var value4) && value4.ValueKind == JsonValueKind.Object && value4.TryGetProperty("handle", out var value5) && value5.ValueKind == JsonValueKind.String)
					{
						stepHandles[i + 1] = value5.GetString();
					}
					list.Add(jsonElement);
				}
				string ResolveStepToken(string t)
				{
					if (t == "$$")
					{
						return objectId;
					}
					if (t.StartsWith("$") && int.TryParse(t.Substring(1), out var result) && result >= 1 && result <= stepResults.Count)
					{
						if (stepHandles.TryGetValue(result, out var value) && !string.IsNullOrEmpty(value))
						{
							return value;
						}
						return context.AddHandle(stepResults[result - 1], stepResults[result - 1]?.GetType()?.Name ?? "object", "chain-alias");
					}
					return null;
				}
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					objectId = objectId,
					stepCount = steps.Length,
					steps = list
				});
			});
		}
		catch (Exception ex)
		{
			return Error("批量调用失败: " + DescribeException(ex));
		}
	}

	private static (string statusJson, object comResult, string error) RunStep(SolidEdgeContext context, object targetObj, string typeNameHint, string objectId, string member, string[] args)
	{
		if (string.IsNullOrWhiteSpace(member))
		{
			return (statusJson: null, comResult: null, error: "成员名不能为空。请先用 se_describe_object 查看该对象的可用成员。");
		}
		if (BlockedMembers.IsBlocked(member.Trim()))
		{
			return (statusJson: null, comResult: null, error: "拒绝调用 " + member.Trim() + ":它属于邮件/会签类副作用成员,读取或调用会激活 MAPI 子系统,可能弹出模态对话框并堵死 COM 通道(共享黑名单见 BlockedMembers)。");
		}
		if (targetObj == null)
		{
			return (statusJson: null, comResult: null, error: "对象已失效。请重新调 se_get_selection 获取选中对象。");
		}
		if (!Marshal.IsComObject(targetObj))
		{
			return (statusJson: null, comResult: null, error: "调用目标不是 COM 对象(" + (targetObj?.GetType().Name ?? "null") + ")。若上一步返回的是标量(如计数),请在下一步用 on:\"$$\" 指定回到起始对象,或用 on:\"$N\" 引用对象步。");
		}
		EnsureTypeLibrariesLoaded();
		ComTypeInfo comTypeInfo = null;
		ComPtr comPtr = null;
		try
		{
			comPtr = ComPtr.FromRCW(targetObj);
			comTypeInfo = comPtr.TryGetComTypeInfo();
		}
		catch
		{
		}
		if (comTypeInfo == null)
		{
			try
			{
				comPtr?.Dispose();
			}
			catch
			{
			}
			return (statusJson: null, comResult: null, error: "无法读取对象(" + targetObj.GetType().Name + ")的类型信息。");
		}
		ComFunctionInfo comFunctionInfo = FindMember(comTypeInfo, member.Trim());
		if (comFunctionInfo == null)
		{
			try
			{
				comPtr?.Dispose();
			}
			catch
			{
			}
			return (statusJson: null, comResult: null, error: "对象(" + targetObj.GetType().Name + ")上找不到成员 \"" + member + "\"。");
		}
		string text = comFunctionInfo.ToString(includeParameters: true);
		ComParameterInfo[] parameters = comFunctionInfo.Parameters;
		string unsupportedOutArray = DetectUnsupportedOutArray(comFunctionInfo, member.Trim());
		if (unsupportedOutArray != null)
		{
			try
			{
				comPtr?.Dispose();
			}
			catch
			{
			}
			return (statusJson: null, comResult: null, error: unsupportedOutArray);
		}
		if (args == null)
		{
			args = Array.Empty<string>();
		}
		string text2 = PrepareArguments(context, parameters, args, text, out var slots);
		if (text2 != null)
		{
			try
			{
				comPtr?.Dispose();
			}
			catch
			{
			}
			return (statusJson: null, comResult: null, error: text2);
		}
		object result;
		Exception error;
		if (slots.Any((ArgSlot s) => s.Value is object[]) && !slots.Any((ArgSlot s) => s.ByRef))
		{
			if (!ManualInvoke.TryInvoke(targetObj, member.Trim(), slots.Select((ArgSlot s) => s.Value).ToArray(), out result, out error))
			{
				try
				{
					comPtr?.Dispose();
				}
				catch
				{
				}
				return (statusJson: null, comResult: null, error: "调用成员失败(数组参数走手工 IDispatch::Invoke 路径): " + DescribeException(error) + "。成员签名: " + text + "。");
			}
		}
		else if (!InvokeWithSlots(targetObj, member.Trim(), slots, out result, out error))
		{
			List<ArgSlot> list = CloneSlots(slots);
			bool flag = false;
			foreach (ArgSlot item in list)
			{
				if (item.ByRef && IsObjectOutSlot(item.Param) && item.Value == null)
				{
					item.Value = targetObj;
					flag = true;
				}
			}
			if (flag && InvokeWithSlots(targetObj, member.Trim(), list, out result, out error))
			{
				slots = list;
			}
			else
			{
				List<ArgSlot> list2 = CloneSlots(slots);
				bool flag2 = false;
				foreach (ArgSlot item2 in list2)
				{
					if (item2.ByRef && ResolveVt(item2.Param) == VarEnum.VT_USERDEFINED && ResolveUserDefinedKind(item2.Param) == UdKind.Unknown)
					{
						item2.Value = ((item2.Value == null) ? ((object)0) : null);
						flag2 = true;
					}
				}
				if (!flag2 || !InvokeWithSlots(targetObj, member.Trim(), list2, out result, out error))
				{
					try
					{
						comPtr?.Dispose();
					}
					catch
					{
					}
					return (statusJson: null, comResult: null, error: "调用成员失败: " + DescribeException(error) + "。成员签名: " + text + "。");
				}
				slots = list2;
			}
		}
		List<object> list3 = new List<object>();
		object returnValue = DescribeValue("return", result, context);
		foreach (ArgSlot item3 in slots)
		{
			if (item3.Collect && item3.Param != null)
			{
				list3.Add(DescribeValue(item3.Param.Name, item3.Value, context));
			}
		}
		var parameters2 = parameters.Select((ComParameterInfo p) => new
		{
			name = p.Name,
			type = ResolveVt(p).ToString(),
			userType = ResolveUserDefinedName(p),
			isIn = p.IsIn,
			isOut = p.IsOut,
			isRetval = p.IsRetval
		}).ToArray();
		try
		{
			comPtr?.Dispose();
		}
		catch
		{
		}
		bool flag3 = (typeNameHint ?? targetObj.GetType().Name).IndexOf("Dimension", StringComparison.OrdinalIgnoreCase) >= 0;
		return (statusJson: JsonSerializer.Serialize(new
		{
			status = "ok",
			objectId = objectId,
			member = member.Trim(),
			signature = text,
			parameters = parameters2,
			returnValue = returnValue,
			outputs = list3,
			unitsNote = (flag3 ? "Solid Edge API 返回的长度/距离/坐标值为内部单位米(m);UI 通常显示毫米(mm),换算 ×1000;角度值为弧度(rad)。" : null)
		}), comResult: result, error: null);
	}

	[McpServerTool]
	[Description("在选中对象(obj-N)上执行一个成员(方法或带参属性),如标注关联查询的 GetRelatedCount / GetRelated。参数 args 按成员签名的入参顺序提供(int/double/string,也可传 obj-N 引用已有句柄对象);out 参数自动识别并填充,无需提供。返回值及 out 参数中的 COM 对象自动登记为新句柄(obj-N),可用 se_describe_object 继续下钻。propertySet=true 时把 member 当【属性写入】(如 Depth / Visible),args[0] 为属性新值(支持数字/bool/字符串/obj-N 对象)——写属性会触发模型重算。护栏:Delete/Cut/Drop/Erase/Purge/Remove* 等破坏性成员一律先拒绝,确认执行需追加 confirm=true;Add*/Set*/Move/写属性等会改变模型的操作允许执行,但会写入审计日志(%LOCALAPPDATA%\\SolidEdgeSpy\\mcp-audit.log)且通常触发重算;设环境变量 SE_MCP_READONLY=1 可全局禁写(默认关闭,否则建模配方会失效)。注意:查询链路中不要再调 se_get_selection,它会清空句柄表导致先前句柄失效。单位约定:Solid Edge API 返回的长度/坐标为内部单位米(m),UI 显示毫米(mm),换算 ×1000;角度为弧度。")]
	public static string se_invoke_member(SolidEdgeContext context, [Description("对象编号,如 obj-1(se_get_selection 返回)")] string objectId, [Description("成员名(方法或带参属性),如 GetRelatedCount / GetRelated;propertySet=true 时为要写的属性名")] string member, [Description("可选,按成员入参顺序的参数值列表(int/double/string,或 obj-N 对象引用);propertySet=true 时 args[0] 为属性新值")] string[] args = null, [Description("可选,设为 true 表示写属性(PROPERTYPUT),args[0] 为属性新值。默认 false=只读调用")] bool propertySet = false, [Description("可选,破坏性成员(Delete/Cut/Drop/Remove* 等)需显式置 true 才会执行,不确认则直接拒绝。默认 false")] bool confirm = false, [Description("可选(仅对 propertySet=true 生效):写完后自动回读同名属性并比对,默认 true。返回 verified=true/false 与 readBack——'写入无报错但实际没生效'是实测踩过的坑(被 SE 规范化/联动/静默忽略),故默认开启校验;确实不可读的只写属性会在 verifyNote 里说明")] bool verify = true)
	{
		try
		{
			return context.Invoke(delegate
			{
				ObjectHandle handle = context.GetHandle(objectId);
				if (handle == null)
				{
					return Error("找不到对象编号 " + objectId + "。请先在本会话内调用 se_get_selection 获取选中对象编号。注意:句柄表存在 MCP server 进程内存中,se_get_selection 与后续工具必须在同一 MCP 会话进程内连续调用;且 se_get_selection 会清空旧句柄表,查询链路中不要重复调用。");
				}
				if (string.IsNullOrWhiteSpace(member))
				{
					return Error("member 不能为空。");
				}
				InvocationRisk risk;
				string guard = Guardrail.Check("se_invoke_member", member, propertySet, confirm, out risk);
				if (guard != null)
				{
					AuditLog.Write("se_invoke_member", objectId, member, propertySet, AuditLog.SummarizeArgs(args), risk, false, "blocked by guardrail");
					return Error(guard);
				}
				string memberFilter = ToolRisk.CheckMember("se_invoke_member", member);
				if (memberFilter != null)
				{
					AuditLog.Write("se_invoke_member", objectId, member, propertySet, AuditLog.SummarizeArgs(args), InvocationRisk.Normal, false, "blocked by engineer member filter");
					return Error(memberFilter);
				}
				if (risk != InvocationRisk.Normal)
				{
					AuditLog.Write("se_invoke_member", objectId, member, propertySet, AuditLog.SummarizeArgs(args), risk, true, null);
				}
				if (propertySet)
				{
					if (args == null || args.Length == 0)
					{
						return Error("写属性需要提供新值:args[0](如 [\"0.04\"] / [\"False\"] / [\"obj-3\"])。");
					}
					object value = ResolveSetValue(context, args[0]);
					if (ManualInvoke.TryInvokeSet(handle.ComObject, member.Trim(), value, out var error))
					{
						bool? verified = null;
						string readBack = null;
						string verifyNote = null;
						if (verify)
						{
							object readValue;
							Exception readError;
							if (ManualInvoke.TryInvoke(handle.ComObject, member.Trim(), null, out readValue, out readError))
							{
								readBack = (readValue == null) ? "(null)" : readValue.ToString();
								verified = LooksSame(context, args[0], value, readValue);
								verifyNote = verified.Value
									? "回读一致。"
									: "⚠回读值与写入值不一致:写入 " + args[0] + ",回读 " + readBack + "。可能是 SE 对该属性做了规范化/联动,或该写入实际未生效——请用 se_describe_object 复核该属性的当前值。";
							}
							else
							{
								verifyNote = "回读失败(该成员可能只写不可读): " + DescribeException(readError);
							}
						}
						return JsonSerializer.Serialize(new
						{
							status = "ok",
							objectId = objectId,
							property = member.Trim(),
							value = args[0],
							verified = verified,
							readBack = readBack,
							verifyNote = verifyNote,
							note = "属性已写入。写属性通常触发模型重算,可用 solidedge-event 的 AfterRecompute 确认。"
						});
					}
					return Error("写属性 " + member.Trim() + " 失败: " + DescribeException(error) + "。请先用 se_describe_object 确认该属性可写、且值类型匹配。");
				}
				var (text, _, text2) = RunStep(context, handle.ComObject, handle.TypeName, objectId, member, args);
				return (text2 != null) ? Error(text2) : text;
			});
		}
		catch (Exception ex)
		{
			if (SolidEdgeContext.IsDisconnected(ex))
			{
				return Error("COM 对象已断连(文档可能已被关闭,或对象已被删除/句柄失效)。请重新调用 se_get_selection 获取新句柄,不要继续用旧 obj-N。原始错误: " + DescribeException(ex));
			}
			return Error("调用成员失败: " + DescribeException(ex) + "。可先用 se_describe_object 核对该成员的签名与参数个数。");
		}
	}

	/// <summary>
	/// 写后回读校验:比较"写入值"与"回读值"。
	/// 数值走容差比较(避免 double 往返精度噪声);obj-N 写对象走 COM 同一性判定(同一接口指针);
	/// 其余走字符串比较。校验只报结论,不抛异常。
	/// </summary>
	private static bool LooksSame(SolidEdgeContext context, string rawArg, object written, object readBack)
	{
		if (readBack == null) return written == null;
		try
		{
			if (rawArg != null && rawArg.Trim().StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
			{
				ObjectHandle handle = context.GetHandle(rawArg.Trim());
				if (handle != null) return handle.IsSameObject(readBack);
			}
			if (written is double)
			{
				try { return Math.Abs(Convert.ToDouble(written) - Convert.ToDouble(readBack)) < 1e-9; } catch { }
			}
			if (written is bool)
			{
				try { return Convert.ToBoolean(written) == Convert.ToBoolean(readBack); } catch { }
			}
			return string.Equals(Convert.ToString(written), Convert.ToString(readBack), StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private static object ResolveSetValue(SolidEdgeContext context, string raw)
	{
		if (raw == null)
		{
			return null;
		}
		string text = raw.Trim();
		if (text.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
		{
			ObjectHandle handle = context.GetHandle(text);
			if (handle != null)
			{
				return handle.ComObject;
			}
		}
		if (bool.TryParse(text, out var result))
		{
			return result;
		}
		if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result2))
		{
			return result2;
		}
		return text;
	}

	private static ComFunctionInfo FindMember(ComTypeInfo typeInfo, string member)
	{
		ComFunctionInfo[] methods = typeInfo.GetMethods(includeInherited: true);
		foreach (ComFunctionInfo comFunctionInfo in methods)
		{
			if (string.Equals(comFunctionInfo.Name, member, StringComparison.OrdinalIgnoreCase))
			{
				return comFunctionInfo;
			}
		}
		ComPropertyInfo[] properties = typeInfo.GetProperties(includeInherited: true);
		foreach (ComPropertyInfo comPropertyInfo in properties)
		{
			if (string.Equals(comPropertyInfo.Name, member, StringComparison.OrdinalIgnoreCase))
			{
				ComFunctionInfo getFunction = comPropertyInfo.GetFunction;
				if (getFunction != null)
				{
					return getFunction;
				}
			}
		}
		return null;
	}

	private static string PrepareArguments(SolidEdgeContext context, ComParameterInfo[] parameters, string[] args, string signature, out List<ArgSlot> slots)
	{
		slots = new List<ArgSlot>();
		int num = 0;
		ComParameterInfo[] array = parameters;
		foreach (ComParameterInfo comParameterInfo in array)
		{
			if (!comParameterInfo.IsRetval && !comParameterInfo.IsLcid && (!comParameterInfo.IsOut || comParameterInfo.IsIn))
			{
				num++;
			}
		}
		int num2 = 0;
		array = parameters;
		foreach (ComParameterInfo comParameterInfo2 in array)
		{
			if (comParameterInfo2.IsRetval || comParameterInfo2.IsLcid)
			{
				continue;
			}
			bool num3 = comParameterInfo2.IsOut && !comParameterInfo2.IsIn;
			bool flag = comParameterInfo2.IsOut && comParameterInfo2.IsIn;
			bool flag2 = !comParameterInfo2.IsIn && !comParameterInfo2.IsOut;
			bool flag3 = comParameterInfo2.VariantType == VarEnum.VT_PTR;
			if (num3 || ((flag2 & flag3) && num2 >= args.Length))
			{
				slots.Add(new ArgSlot
				{
					Param = comParameterInfo2,
					Value = TypedDefault(comParameterInfo2),
					ByRef = true,
					Collect = true
				});
				continue;
			}
			if (num2 >= args.Length)
			{
				if (comParameterInfo2.IsOptional)
				{
					slots.Add(new ArgSlot
					{
						Param = comParameterInfo2,
						Value = Type.Missing
					});
					continue;
				}
				return "参数不足:成员签名 " + signature + " 需要至少 " + num + " 个入参,实际提供 " + args.Length + " 个(纯 out/retval/可选参数无需提供)。请按签名顺序提供。";
			}
			object value = ConvertArg(context, args[num2], comParameterInfo2, out var error);
			if (error != null)
			{
				return "第 " + (num2 + 1) + " 个参数(" + comParameterInfo2.Name + ")转换失败: " + error + "。成员签名: " + signature;
			}
			slots.Add(new ArgSlot
			{
				Param = comParameterInfo2,
				Value = value,
				ByRef = (flag || (flag2 & flag3)),
				Collect = (flag || (flag2 & flag3))
			});
			num2++;
		}
		if (num2 < args.Length)
		{
			return "参数过多:成员签名 " + signature + " 只需要 " + num2 + " 个入参,实际提供 " + args.Length + " 个(注意 out/retval 参数无需提供)。";
		}
		return null;
	}

	private const int VT_ARRAY_BIT = 0x2000;

	/// <summary>
	/// 通用调度通道构不出 out 数组出参(签名含 (ref|out) double[] 的成员,如 GetRange/GetPointData/GetParamRange),
	/// 与其发到 COM 再吃 0x80020005 TYPEMISMATCH,不如按签名在调用前 fail-fast 并指路(零 COM 往返)。
	/// 判据 IsOut &amp;&amp; !IsRetval &amp;&amp; 数组型:retval 走 pVarResult,通道本就正常,必须排除;
	/// 只看数组类型不判 IsOut 会误伤 in 方向 double[](loft/sweep 的 Origins)。
	/// </summary>
	private static string DetectUnsupportedOutArray(ComFunctionInfo info, string member)
	{
		if (info == null)
		{
			return null;
		}
		foreach (ComParameterInfo p in info.Parameters)
		{
			if (!p.IsOut || p.IsRetval || !IsArrayLikeType(p))
			{
				continue;
			}
			return "成员 \"" + member + "\" 含 out 数组出参,通用通道封送不了(已知盲区)。绕行:se_script_run(真 C# + PIA 强类型)或 se_read_geometry 等专用工具。";
		}
		return null;
	}

	private static bool IsArrayLikeType(ComParameterInfo p)
	{
		// EffectiveVarType 已解一层 VT_PTR(typelib 里 SAFEARRAY(double)* 写作 VT_PTR → lptdesc → VT_ARRAY|VT_R8)。
		// ⚠️ 判据故意【不限于 VT_ARRAY|VT_R8】:真机只实证过 double 数组,但根因是通用通道【整体】
		//    构不出 byref SAFEARRAY(ManualInvoke 只按值封送、无 VT_BYREF,与元素类型无关),
		//    所以按"结构性不支持"宽拦所有 out 数组;若将来发现某元素类型(out BSTR[]/I4[])真能通,
		//    再按实证收窄到白名单——宽拦的代价只是一句指路文案,漏拦的代价是 TYPEMISMATCH 撞墙。
		VarEnum vt = p.EffectiveVarType;
		if (vt == VarEnum.VT_SAFEARRAY)
		{
			return true;
		}
		return ((int)vt & VT_ARRAY_BIT) != 0;
	}

	private static VarEnum ResolveVt(ComParameterInfo p)
	{
		try
		{
			if (p == null || p.ELEMDESC.tdesc.lpValue == IntPtr.Zero)
			{
				return p?.VariantType ?? VarEnum.VT_EMPTY;
			}
			TYPEDESC tYPEDESC = p.ELEMDESC.tdesc;
			VarEnum vt = (VarEnum)tYPEDESC.vt;
			int num = 0;
			while (vt == VarEnum.VT_PTR && tYPEDESC.lpValue != IntPtr.Zero && num < 2)
			{
				tYPEDESC = (TYPEDESC)Marshal.PtrToStructure(tYPEDESC.lpValue, typeof(TYPEDESC));
				vt = (VarEnum)tYPEDESC.vt;
				num++;
			}
			return vt;
		}
		catch
		{
			return p?.VariantType ?? VarEnum.VT_EMPTY;
		}
	}

	private static object TypedDefault(ComParameterInfo p)
	{
		switch (ResolveVt(p))
		{
		case VarEnum.VT_R4:
			return 0f;
		case VarEnum.VT_R8:
			return 0.0;
		case VarEnum.VT_I1:
			return (sbyte)0;
		case VarEnum.VT_I2:
			return (short)0;
		case VarEnum.VT_I4:
		case VarEnum.VT_INT:
			return 0;
		case VarEnum.VT_I8:
			return 0L;
		case VarEnum.VT_UI1:
			return (byte)0;
		case VarEnum.VT_UI2:
			return (ushort)0;
		case VarEnum.VT_UI4:
		case VarEnum.VT_UINT:
			return 0u;
		case VarEnum.VT_UI8:
			return 0uL;
		case VarEnum.VT_BOOL:
			return false;
		case VarEnum.VT_BSTR:
			return "";
		case VarEnum.VT_USERDEFINED:
			if (ResolveUserDefinedKind(p) != UdKind.Enum)
			{
				return null;
			}
			return 0;
		default:
			return null;
		}
	}

	// 实现已提取到 ComEnumHelper:se_describe_object 的枚举解码与 invoke 的参数占位共用同一份逻辑。
	private static TYPEDESC? InnermostTypeDesc(ComParameterInfo p)
	{
		return ComEnumHelper.InnermostTypeDesc(p);
	}

	private static UdKind ResolveUserDefinedKind(ComParameterInfo p)
	{
		try
		{
			TYPEDESC? tYPEDESC = InnermostTypeDesc(p);
			if (!tYPEDESC.HasValue)
			{
				return UdKind.Unknown;
			}
			TYPEDESC value = tYPEDESC.Value;
			if (value.vt != 29 || value.lpValue == IntPtr.Zero)
			{
				return UdKind.Unknown;
			}
			p.ComFunctionInfo.ComTypeInfo.GetITypeInfo().GetRefTypeInfo(((IntPtr)value.lpValue).ToInt32(), out ITypeInfo ppTI);
			if (ppTI == null)
			{
				return UdKind.Unknown;
			}
			ppTI.GetTypeAttr(out var ppTypeAttr);
			try
			{
				switch (((TYPEATTR)Marshal.PtrToStructure(ppTypeAttr, typeof(TYPEATTR))).typekind)
				{
				case TYPEKIND.TKIND_ENUM:
					return UdKind.Enum;
				case TYPEKIND.TKIND_INTERFACE:
				case TYPEKIND.TKIND_DISPATCH:
				case TYPEKIND.TKIND_COCLASS:
					return UdKind.Interface;
				default:
					return UdKind.Other;
				}
			}
			finally
			{
				ppTI.ReleaseTypeAttr(ppTypeAttr);
			}
		}
		catch
		{
			return UdKind.Unknown;
		}
	}

	private static string ResolveUserDefinedName(ComParameterInfo p)
	{
		try
		{
			TYPEDESC? tYPEDESC = InnermostTypeDesc(p);
			if (!tYPEDESC.HasValue)
			{
				return null;
			}
			TYPEDESC value = tYPEDESC.Value;
			if (value.vt != 29 || value.lpValue == IntPtr.Zero)
			{
				return null;
			}
			p.ComFunctionInfo.ComTypeInfo.GetITypeInfo().GetRefTypeInfo(((IntPtr)value.lpValue).ToInt32(), out ITypeInfo ppTI);
			if (ppTI == null)
			{
				return null;
			}
			ppTI.GetDocumentation(-1, out string strName, out string _, out int _, out string _);
			return strName;
		}
		catch
		{
			return null;
		}
	}

	private static bool IsObjectOutSlot(ComParameterInfo p)
	{
		if (p == null)
		{
			return false;
		}
		switch (ResolveVt(p))
		{
		case VarEnum.VT_DISPATCH:
		case VarEnum.VT_VARIANT:
		case VarEnum.VT_UNKNOWN:
		case VarEnum.VT_PTR:
			return true;
		case VarEnum.VT_USERDEFINED:
		{
			UdKind udKind = ResolveUserDefinedKind(p);
			if (udKind != UdKind.Interface)
			{
				return udKind == UdKind.Other;
			}
			return true;
		}
		default:
			return false;
		}
	}

	private static List<ArgSlot> CloneSlots(List<ArgSlot> slots)
	{
		return slots.Select((ArgSlot s) => new ArgSlot
		{
			Param = s.Param,
			Value = s.Value,
			ByRef = s.ByRef,
			Collect = s.Collect
		}).ToList();
	}

	private static string DescribeParams(ComParameterInfo[] parameters)
	{
		return string.Join(", ", parameters.Select((ComParameterInfo p) => p.Name + ":" + ResolveVt(p).ToString() + ((ResolveUserDefinedName(p) != null) ? ("(" + ResolveUserDefinedName(p) + ")") : "") + (p.IsIn ? "[in]" : "") + (p.IsOut ? "[out]" : "") + (p.IsRetval ? "[retval]" : "")));
	}

	private static bool InvokeWithSlots(object target, string member, List<ArgSlot> slots, out object result, out Exception error)
	{
		result = null;
		error = null;
		try
		{
			object[] array = slots.Select((ArgSlot s) => s.Value).ToArray();
			if (slots.Any((ArgSlot s) => s.ByRef))
			{
				ParameterModifier parameterModifier = new ParameterModifier(slots.Count);
				for (int num = 0; num < slots.Count; num++)
				{
					parameterModifier[num] = slots[num].ByRef;
				}
				result = target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, array, new ParameterModifier[1] { parameterModifier }, CultureInfo.InvariantCulture, null);
			}
			else
			{
				result = target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, array, null, CultureInfo.InvariantCulture, null);
			}
			for (int num2 = 0; num2 < slots.Count; num2++)
			{
				slots[num2].Value = array[num2];
			}
			return true;
		}
		catch (Exception ex)
		{
			error = ex;
			return false;
		}
	}

	private static object ConvertArg(SolidEdgeContext context, string raw, ComParameterInfo p, out string error)
	{
		error = null;
		if (raw == null)
		{
			return Type.Missing;
		}
		string text = raw.Trim();
		if (text.StartsWith("obj-", StringComparison.OrdinalIgnoreCase) && IsObjectParam(p))
		{
			ObjectHandle handle = context.GetHandle(text);
			if (handle == null)
			{
				error = "对象编号 " + text + " 不存在于句柄表(注意 se_get_selection 会清空句柄表)。";
				return null;
			}
			return handle.ComObject;
		}
		switch (ResolveVt(p))
		{
		case VarEnum.VT_I2:
		case VarEnum.VT_I4:
		case VarEnum.VT_I1:
		case VarEnum.VT_UI1:
		case VarEnum.VT_UI2:
		case VarEnum.VT_UI4:
		case VarEnum.VT_I8:
		case VarEnum.VT_UI8:
		case VarEnum.VT_INT:
		case VarEnum.VT_UINT:
		{
			if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result5))
			{
				return result5;
			}
			if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result6))
			{
				return result6;
			}
			error = "应为整数,实际 \"" + text + "\"。";
			return null;
		}
		case VarEnum.VT_R4:
		{
			if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result4))
			{
				return result4;
			}
			error = "应为数值,实际 \"" + text + "\"。";
			return null;
		}
		case VarEnum.VT_R8:
		{
			if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result3))
			{
				return result3;
			}
			error = "应为数值,实际 \"" + text + "\"。";
			return null;
		}
		case VarEnum.VT_BOOL:
		{
			if (bool.TryParse(text, out var result7))
			{
				return result7;
			}
			if (text == "1")
			{
				return true;
			}
			if (text == "0")
			{
				return false;
			}
			error = "应为布尔(true/false/1/0),实际 \"" + text + "\"。";
			return null;
		}
		case VarEnum.VT_BSTR:
			return text;
		default:
		{
			if (text.StartsWith("@arr:", StringComparison.OrdinalIgnoreCase))
			{
				string[] array = text.Substring(5).Split(',');
				object[] array2 = new object[array.Length];
				for (int i = 0; i < array.Length; i++)
				{
					string text2 = array[i].Trim();
					double result;
					if (text2.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
					{
						ObjectHandle handle2 = context.GetHandle(text2);
						if (handle2 == null)
						{
							error = "数组元素 " + text2 + " 不存在于句柄表。";
							return null;
						}
						array2[i] = handle2.ComObject;
					}
					else if (double.TryParse(text2, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
					{
						array2[i] = result;
					}
					else
					{
						array2[i] = text2;
					}
				}
				return array2;
			}
			if (ResolveUserDefinedKind(p) == UdKind.Enum)
			{
				int? num = ResolveEnumConstantValue(p, text);
				if (num.HasValue)
				{
					return num.Value;
				}
			}
			if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2))
			{
				return result2;
			}
			return text;
		}
		}
	}

	// 实现已提取到 ComEnumHelper.ResolveEnumConstantValue(多了一层 TKIND_ENUM 校验,行为等价)。
	private static int? ResolveEnumConstantValue(ComParameterInfo p, string name)
	{
		return ComEnumHelper.ResolveEnumConstantValue(p, name);
	}

	private static bool IsObjectParam(ComParameterInfo p)
	{
		if (p.VariantType == VarEnum.VT_PTR)
		{
			return true;
		}
		VarEnum variantType = p.VariantType;
		if (variantType == VarEnum.VT_DISPATCH || (uint)(variantType - 12) <= 1u)
		{
			return true;
		}
		return false;
	}

	private static object DescribeValue(string name, object value, SolidEdgeContext context)
	{
		if (value == null)
		{
			return new
			{
				name = name,
				type = "(null)",
				value = (string)null
			};
		}
		if (Marshal.IsComObject(value))
		{
			string text = "(未知)";
			ComPtr comPtr = null;
			try
			{
				comPtr = ComPtr.FromRCW(value);
				ComTypeInfo comTypeInfo = comPtr.TryGetComTypeInfo();
				if (comTypeInfo != null)
				{
					text = comTypeInfo.FullName;
				}
			}
			catch
			{
			}
			string text2 = null;
			try
			{
				text2 = value.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, value, null) as string;
			}
			catch
			{
			}
			try
			{
				comPtr?.Dispose();
			}
			catch
			{
			}
			string text3 = context.AddHandle(value, text, text2 ?? "(无名称)");
			return new
			{
				name = name,
				type = text,
				displayName = text2,
				handle = text3,
				hint = "已登记句柄,可用 se_describe_object(" + text3 + ") 下钻查看详情。"
			};
		}
		return new
		{
			name = name,
			type = value.GetType().Name,
			value = value.ToString()
		};
	}

	private static string DescribeException(Exception ex)
	{
		if (ex == null)
		{
			return "(未知错误)";
		}
		StringBuilder stringBuilder = new StringBuilder();
		Exception ex2 = ex;
		int num = 0;
		while (ex2 != null && num < 3)
		{
			if (num > 0)
			{
				stringBuilder.Append(" <- 内部: ");
			}
			stringBuilder.Append(ex2.GetType().Name);
			if (!string.IsNullOrEmpty(ex2.Message))
			{
				stringBuilder.Append(": ").Append(ex2.Message);
			}
			try
			{
				if (ex2.HResult != 0 && ex2.HResult != -2146232828)
				{
					stringBuilder.Append(" (HRESULT=0x").Append(ex2.HResult.ToString("X8")).Append(')');
				}
			}
			catch
			{
			}
			ex2 = ex2.InnerException;
			num++;
		}
		return stringBuilder.ToString();
	}

	private static void EnsureTypeLibrariesLoaded()
	{
		if (Interlocked.CompareExchange(ref _typeLibsLoaded, 1, 0) == 0)
		{
			try
			{
				Version version = new Version(1, 0);
				ComTypeManager instance = ComTypeManager.Instance;
				instance.LoadRegTypeLib(TypeLibGuid.RevisionManager, version);
				instance.LoadRegTypeLib(TypeLibGuid.SEInstallDataLib, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeAssembly, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeConstants, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeDraft, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeFileProperties, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeFramework, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeFrameworkSupport, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeGeometry, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgePart, version);
				instance.LoadRegTypeLib(TypeLibGuid.StructureEditor, version);
			}
			catch
			{
			}
		}
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new
		{
			status = "error",
			message = message
		});
	}
}
