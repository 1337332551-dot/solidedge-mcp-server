using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools;

[McpServerToolType]
public static class VariableTools
{
	[McpServerTool]
	[Description("读取当前零件(Part)文档变量表(Variables)的全部变量:显示名、名称、系统名、公式(值)、数据类型。用于确认模型参数化基础——是否有可驱动筋板厚度/数量等尺寸的变量。")]
	public static string se_get_variables(SolidEdgeContext context)
	{
		try
		{
			return context.Invoke(delegate
			{
				// SE 在没有活动文档时,ActiveDocument 抛 COM 异常(0x80004005 E_FAIL),不返回 null。
				object activeDocument;
				try { activeDocument = context.GetApplication().ActiveDocument; }
				catch { activeDocument = null; }
				if (activeDocument == null)
				{
					return Error("当前没有活动文档。");
				}
				object obj = SafeGetProperty(activeDocument, "Variables");
				if (obj == null)
				{
					return Error("当前文档不支持变量表(Variables),或无法访问。可能不是 Part 文档。");
				}
				dynamic val = obj;
				int num = 0;
				try
				{
					num = (int)val.Count;
				}
				catch
				{
				}
				List<object> list = new List<object>();
				for (int i = 1; i <= num; i++)
				{
					object obj3 = null;
					string displayName = "";
					string name = "";
					string systemName = "";
					string formula = "";
					try
					{
						obj3 = val.Item(i);
						if (obj3 != null)
						{
							displayName = (val.GetDisplayName(obj3) as string) ?? "";
							name = (val.GetName(obj3) as string) ?? "";
							systemName = (val.GetSystemName(obj3) as string) ?? "";
							formula = (val.GetFormula(obj3) as string) ?? "";
							goto IL_0304;
						}
					}
					catch
					{
						goto IL_0304;
					}
					continue;
					IL_0304:
					list.Add(new
					{
						index = i,
						displayName = displayName,
						name = name,
						systemName = systemName,
						formula = formula
					});
				}
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					variableCount = num,
					variables = list,
					conclusion = ((num == 0) ? "变量表为空。若要让筋板参数化驱动,需先在 Solid Edge 中为筋板厚度/数量定义变量(或用变量关联尺寸)。" : ("变量表含 " + num + " 个变量。若有控制筋板厚度/数量的变量,即可用于参数化自动重建。"))
				});
			});
		}
		catch (Exception ex)
		{
			return Error("读取变量表失败: " + ex.Message);
		}
	}

	private static object SafeGetProperty(object obj, string propertyName)
	{
		try
		{
			return obj.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, obj, null);
		}
		catch
		{
			return null;
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
