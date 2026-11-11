using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol.Server;
using SolidEdge.Spy.InteropServices;
using SolidEdgeFramework;

namespace SolidEdge.Spy.McpServer.Tools;

[McpServerToolType]
public static class WalkTools
{
	private static int _typeLibsLoaded;

	[McpServerTool]
	[Description("按路径字符串取对象树任意位置的概要。路径格式如 'Application.ActiveDocument.Sketches.Item(1)'。" +
		"返回该位置对象的类型、名称、子属性列表、子集合元素列表——children 只含 null/集合/COM 对象三类分支," +
		"标量属性值不进列表也不报错(要看属性值请用 se_describe_object)。集合额外返回 items(前 maxItems 个元素名)," +
		"用于全集枚举发现异常成员(如视图编辑上下文的数字名 sheet)。本工具只枚举无参属性;FEA 仿真对象(Study/Load/Constraint)" +
		"的 Name 与集合入口是 IDispatch 派发成员/方法,走到 Study 层会'断头'。" +
		"若路径解析失败或成员为空:不要写脚本绕过!应终止本轮自动尝试,请用户在 SE 中选中目标对象," +
		"改用 se_get_selection + se_describe_object(走 IDispatch 通道,能读到派发属性与全部方法)。")]
	public static string se_walk_object(SolidEdgeContext context, [Description("对象路径,如 'Application.ActiveDocument.Sketches.Item(1)'")] string path, [Description("每个集合列出元素名单的最大条数,默认 25,上限 200;0=不列名单")] int maxItems = 25)
	{
		try
		{
			return context.Invoke(delegate
			{
				Application application = context.GetApplication();
				EnsureTypeLibrariesLoaded();
				object obj = ResolvePath(application, path);
				if (obj == null)
				{
					return Error("无法解析路径: " + path + "。请检查路径中的属性名和索引是否正确。重要提示:不要写脚本或独立程序绕过!请终止本轮自动尝试,让用户在 Solid Edge 中手动选中目标对象,然后改用 se_get_selection + se_describe_object 重新查找——它们走 IDispatch 通道,能读到派发属性(如 FEA 载荷的 Name)和全部方法,比本工具(只枚举无参属性)覆盖面更广。");
				}
				string typeShort = "(未知)";
				string type = "(未知)";
				ComPtr comPtr = null;
				ComTypeInfo comTypeInfo = null;
				try
				{
					comPtr = ComPtr.FromRCW(obj);
					comTypeInfo = comPtr.TryGetComTypeInfo();
					if (comTypeInfo != null)
					{
						typeShort = comTypeInfo.Name;
						type = comTypeInfo.FullName;
					}
				}
				catch
				{
				}
				string name = (SafeGetProperty(obj, "Name") as string) ?? "(无名称)";
				List<object> list = new List<object>();
				List<object> unreadableProperties = new List<object>();   // P3-2b:属性读不到不再静默消失
				// 目标本身是集合时,列出自身元素(否则集合成员不可见,如 Sheets 里的数字名 sheet)
				object selfItems = null;
				int selfCount = 0;
				if (IsCollection(obj) && maxItems > 0)
				{
					selfCount = SafeGetCount(obj);
					if (selfCount > 0)
					{
						selfItems = SafeListNames(obj, Math.Min(maxItems, 200), selfCount);
					}
				}
				if (comTypeInfo != null)
				{
					ComPropertyInfo[] properties = comTypeInfo.Properties;
					foreach (ComPropertyInfo comPropertyInfo in properties)
					{
						if (comPropertyInfo.GetFunction != null && !comPropertyInfo.GetFunctionHasParameters)
						{
							string name2 = comPropertyInfo.Name;
							if (!BlockedMembers.IsBlocked(name2))
							{
								object obj3 = null;
								try
								{
									obj3 = obj.GetType().InvokeMember(name2, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, obj, null);
								}
								catch (Exception ex)
								{
									// P3-2b:读不到的属性单独列出(带原因),不再从 children 里静默消失。
									// 2026-10-19 验证发现:ex.Message 只有 TargetInvocationException 套话,
									// 真实内因(成员不存在/需要参数/权限)全被吞——改用统一展开(内含 HRESULT 人话化)。
									unreadableProperties.Add(new { name = name2, error = EvidenceContract.DescribeException(ex) });
									continue;
								}
								if (obj3 == null)
								{
									list.Add(new
									{
										name = name2,
										type = "(null)",
										isCollection = false
									});
								}
								else if (IsCollection(obj3))
								{
									int count = SafeGetCount(obj3);
									object items = null;
									if (maxItems > 0 && count > 0)
									{
										items = SafeListNames(obj3, Math.Min(maxItems, 200), count);
									}
									list.Add(new
									{
										name = name2,
										type = "Collection",
										isCollection = true,
										count = count,
										items = items
									});
								}
								else if (Marshal.IsComObject(obj3))
								{
									string type2 = "(未知:COM类型信息读不到)";
									try
									{
										ComTypeInfo comTypeInfo2 = ComPtr.FromRCW(obj3).TryGetComTypeInfo();
										if (comTypeInfo2 != null)
										{
											type2 = comTypeInfo2.Name;
										}
									}
									catch
									{
										// P3-2b:类型读不到时回退值本身说明原因(不掩盖)
									}
									list.Add(new
									{
										name = name2,
										type = type2,
										isCollection = false
									});
								}
							}
						}
					}
				}
				try
				{
					comPtr?.Dispose();
				}
				catch
				{
				}
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					path = path,
					type = type,
					typeShort = typeShort,
					name = name,
					selfCount = (selfItems != null ? (int?)selfCount : null),
					selfItems = selfItems,
					children = list,
					unreadableProperties = ((unreadableProperties.Count > 0) ? unreadableProperties : null)
				});
			});
		}
		catch (Exception ex)
		{
			return Error("探索对象失败: " + ex.Message);
		}
	}

	private static object ResolvePath(object root, string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}
		path = path.Trim();
		object obj = root;
		List<string> list = SplitPath(path);
		if (list.Count == 0 || list[0] != "Application")
		{
			return null;
		}
		for (int i = 1; i < list.Count; i++)
		{
			obj = ResolveSegment(obj, list[i]);
			if (obj == null)
			{
				return null;
			}
		}
		return obj;
	}

	private static List<string> SplitPath(string path)
	{
		List<string> list = new List<string>();
		int num = 0;
		int num2 = 0;
		for (int i = 0; i < path.Length; i++)
		{
			switch (path[i])
			{
			case '(':
				num++;
				break;
			case ')':
				num--;
				break;
			case '.':
				if (num == 0)
				{
					list.Add(path.Substring(num2, i - num2));
					num2 = i + 1;
				}
				break;
			}
		}
		list.Add(path.Substring(num2));
		return list;
	}

	private static object ResolveSegment(object parent, string segment)
	{
		if (parent == null)
		{
			return null;
		}
		segment = segment.Trim();
		if (segment.StartsWith("Item(", StringComparison.Ordinal))
		{
			int num = segment.LastIndexOf(')');
			if (num < 0)
			{
				return null;
			}
			string text = segment.Substring(5, num - 5).Trim();
			object obj = ((text.StartsWith("\"") || text.StartsWith("'")) ? text.Trim('"', '\'') : ((!int.TryParse(text, out var result)) ? text : ((object)result)));
			try
			{
				return parent.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, parent, new object[1] { obj });
			}
			catch
			{
				return null;
			}
		}
		try
		{
			return parent.GetType().InvokeMember(segment, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, parent, null);
		}
		catch
		{
			return null;
		}
	}

	private static object SafeGetProperty(object obj, string name)
	{
		try
		{
			return obj.GetType().InvokeMember(name, BindingFlags.GetProperty, null, obj, null);
		}
		catch
		{
			return null;
		}
	}

	private static bool IsCollection(object obj)
	{
		try
		{
			obj.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, obj, null);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static int SafeGetCount(object collection)
	{
		try
		{
			object obj = collection.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, collection, null);
			return (obj is int) ? ((int)obj) : 0;
		}
		catch
		{
			return 0;
		}
	}

	// 列出集合前 max 个元素的名字(Name 优先,其次 Key,都没有则 "#索引")
	// 用于全集枚举:发现异常成员(如视图编辑上下文的数字名 sheet)而不必写脚本
	private static List<string> SafeListNames(object collection, int max, int count)
	{
		var names = new List<string>();
		int scan = Math.Min(max, count);
		for (int i = 1; i <= scan; i++)
		{
			object item = null;
			try
			{
				item = collection.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, collection, new object[] { i });
			}
			catch
			{
				break;
			}
			if (item == null)
			{
				names.Add("#" + i + "=null");
				continue;
			}
			string n = null;
			try
			{
				n = item.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, item, null) as string;
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(n))
			{
				try
				{
					n = item.GetType().InvokeMember("Key", BindingFlags.GetProperty, null, item, null) as string;
				}
				catch
				{
				}
			}
			names.Add(string.IsNullOrEmpty(n) ? ("#" + i) : n);
		}
		if (count > scan)
		{
			names.Add("...(共" + count + "个,仅列前" + scan + "个)");
		}
		return names;
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
