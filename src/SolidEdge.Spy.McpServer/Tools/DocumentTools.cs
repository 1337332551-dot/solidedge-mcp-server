using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol.Server;
using SolidEdge.Spy.InteropServices;
using SolidEdgeFramework;

namespace SolidEdge.Spy.McpServer.Tools;

[McpServerToolType]
public static class DocumentTools
{
	/// <summary>
	/// 本会话由 se_open_document 打开的文档追踪表:key = 文档全路径(忽略大小写)。
	/// 设计约定:se_close_document 只允许关闭这张表里的文档——AI 永远不可能误关
	/// 用户手动打开的文档。表随 MCP 进程存亡,进程重启即清空,之前打开的文档变
	/// "无主",se_close_document 会拒绝关闭(安全默认)。
	/// </summary>
	private sealed class TrackedDocument
	{
		public object Document;
		public string Key;
		public string FullName;
		public string LastHandleId;
		public bool RequestedReadOnly;
		public string Origin;
		public DateTime OpenedAt;
	}

	private static readonly ConcurrentDictionary<string, TrackedDocument> TrackedDocs =
		new ConcurrentDictionary<string, TrackedDocument>(StringComparer.OrdinalIgnoreCase);

	[McpServerTool]
	[Description("获取当前活动文档的基本信息:文档类型(COM 真实类型名,如 SolidEdgePart.PartDocument)、文件名、全路径、所在环境,以及内容探针(modelsCount=零件模型数,occurrencesCount=装配件数,sheetsCount=图纸页数,不适用为 null)。AI 应在调用其它工具前先调此工具了解上下文;建模前确认文档身份和是否空模板也先用它,不用再跑一次性脚本。")]
	public static string se_get_document(SolidEdgeContext context)
	{
		try
		{
			return context.Invoke(delegate
			{
				Application application = context.GetApplication();
				// SE 在没有活动文档时,ActiveDocument 是抛 COM 异常(0x80004005 E_FAIL),
				// 不是返回 null。不 try 的话,"没开文档"会被误报成"获取文档信息失败: E_FAIL"
				// (日志里 5 条此类失败全由此而来)。
				object activeDocument;
				try { activeDocument = application.ActiveDocument; }
				catch { activeDocument = null; }
				if (activeDocument == null)
				{
					return Error("当前没有活动文档。请在 Solid Edge 中打开一个文档(零件/装配/图纸)后再试。");
				}
				Type type = activeDocument.GetType();
				string typeFullName = type.FullName;
				string typeShortName = type.Name;
				// .NET 的 GetType() 对弱类型 COM 对象只会给出 System.__ComObject,
				// 必须走 COM TypeInfo 才能拿到真实类型名(如 SolidEdgeDraft.DraftDocument)。
				string resolvedType = TryResolveComTypeName(activeDocument);
				if (resolvedType != null)
				{
					typeFullName = resolvedType;
					int num = resolvedType.LastIndexOf('.');
					typeShortName = ((num >= 0) ? resolvedType.Substring(num + 1) : resolvedType);
				}
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					document = new
					{
						type = typeFullName,
						typeShort = typeShortName,
						name = SafeGetProperty(activeDocument, "Name"),
						fullName = SafeGetProperty(activeDocument, "FullName"),
						environment = SafeGetActiveEnvironmentName(application),
						// 轻量内容探针:零件/钣金=Models,装配=Occurrences,图纸=Sheets。
						// 建模前确认"是空模板"不用再跑一次性脚本(se_open_document 同款探针思路)。
						modelsCount = SafeProbeCount(activeDocument, "Models"),
						occurrencesCount = SafeProbeCount(activeDocument, "Occurrences"),
						sheetsCount = SafeProbeCount(activeDocument, "Sheets")
					}
				});
			});
		}
		catch (Exception ex)
		{
			return Error("获取文档信息失败: " + ex.Message);
		}
	}

	[McpServerTool]
	[Description("打开一个 Solid Edge 文档(.par/.asm/.dft/.psm/.pwd 等)并登记句柄。只有本工具打开的文档会进入追踪表;配套的 se_close_document 只允许关闭追踪表里的文档,绝不会误关用户手动打开的文档。注意:SE 的 Documents.Open API 没有 readonly 参数,本工具会在结果里如实报告文档实际的只读状态;要强制只读请在批处理前设环境变量 SE_MCP_READONLY=1(全局禁写)。Open 前会临时把 DisplayAlerts 置 false 防止外链接丢失等对话框把调用卡死,结束后恢复;返回前做一次轻量读取探针(Sheets/Models/Occurrences 的 Count)确认文档可读(防半加载)。每次打开都会把该文档切换为 ActiveDocument;同一文件重复打开会返回现有文档并刷新追踪表。")]
	public static string se_open_document(SolidEdgeContext context, [Description("要打开的文件完整路径")] string path, [Description("期望以只读方式使用。注意 SE API 不支持只读打开,本参数仅用于在结果里提示实际状态与期望是否一致")] bool readOnly = false)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return Error("path 不能为空。");
			}
			string fullPath;
			try
			{
				fullPath = Path.GetFullPath(path.Trim('"'));
			}
			catch (Exception inner)
			{
				return Error("path 不是合法路径: " + inner.Message);
			}
			if (!File.Exists(fullPath))
			{
				return Error("文件不存在: " + fullPath);
			}
			return context.Invoke(delegate
			{
				Application app = context.GetApplication();
				bool previousAlerts = true;
				bool alertsChanged = false;
				try
				{
					try
					{
						previousAlerts = app.DisplayAlerts;
						app.DisplayAlerts = false;
						alertsChanged = true;
					}
					catch
					{
						// DisplayAlerts 不可用时照常打开
					}
					object documents = app.Documents;
					object doc = documents.GetType().InvokeMember("Open", BindingFlags.InvokeMethod, null, documents,
						new object[6]
						{
							fullPath,
							Type.Missing,
							Type.Missing,
							Type.Missing,
							Type.Missing,
							Type.Missing
						});
					if (doc == null)
					{
						return Error("Documents.Open 返回空,打开失败: " + fullPath);
					}

					// 轻量读取探针:读得动才算加载完成(防半加载)。图纸=Sheets,零件/钣金=Models,装配=Occurrences
					int? probeCount = SafeProbeCount(doc, "Sheets") ?? SafeProbeCount(doc, "Models") ?? SafeProbeCount(doc, "Occurrences");
					string probeCollection = ProbeName(doc);
					string fullName = SafeGetProperty(doc, "FullName");
					string resolvedType = TryResolveComTypeName(doc);
					string typeShort = ((resolvedType != null) ? resolvedType.Substring(resolvedType.LastIndexOf('.') + 1) : doc.GetType().Name);
					bool? actualReadOnly = TryGetBool(doc, "ReadOnly");
					bool? dirty = TryGetBool(doc, "Dirty");
					string handleId = context.AddHandle(doc, typeShort, fullName);
					bool alreadyTracked = TrackedDocs.ContainsKey(fullPath);
					TrackedDocs[fullPath] = new TrackedDocument
					{
						Document = doc,
						Key = fullPath,
						FullName = fullName,
						LastHandleId = handleId,
						RequestedReadOnly = readOnly,
						Origin = "opened",
						OpenedAt = DateTime.Now
					};
					AuditLog.Write("se_open_document", handleId, "Open", false, fullPath, InvocationRisk.ModelChanging, true, null);
					string readOnlyNote = null;
					if (readOnly && actualReadOnly == false)
					{
						readOnlyNote = "注意:请求了只读,但文档实际以可写方式打开(SE API 无只读打开参数)。批处理请先设环境变量 SE_MCP_READONLY=1 全局禁写。";
					}
					return JsonSerializer.Serialize(new
					{
						status = "ok",
						handle = handleId,
						fullName,
						type = resolvedType ?? "(未知)",
						typeShort,
						readOnly = actualReadOnly,
						requestedReadOnly = readOnly,
						readOnlyNote,
						dirty,
						alreadyOpen = alreadyTracked,
						probe = new
						{
							collection = probeCollection,
							count = probeCount,
							loaded = probeCount.HasValue
						},
						openedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
						tracked = true,
						hint = "已加入追踪表;关闭用 se_close_document(传本句柄或文档全路径)。该文档已切换为 ActiveDocument。"
					});
				}
				finally
				{
					if (alertsChanged)
					{
						try { app.DisplayAlerts = previousAlerts; } catch { }
					}
				}
			});
		}
		catch (Exception ex)
		{
			return Error("打开文档失败: " + DescribeError(ex));
		}
	}

	[McpServerTool]
	[Description("关闭一个由 se_open_document 打开的文档。安全规则:只能关闭本会话追踪表里的文档(即由 se_open_document 打开的),用户手动打开的文档永远无法通过本工具关闭。target 传 obj-N 句柄或文档全路径均可。关闭前检查 Document.Dirty:有未保存修改(或修改状态读不出)时默认拒绝,必须显式 confirm=true;本工具永不保存——保存与否由用户在 Solid Edge 里决定,confirm 后按丢弃修改关闭。close 全程包在 DisplayAlerts=false 里防保存对话框卡死。注意 MCP 进程重启后追踪表清空,重启前打开的文档会因'无主'被拒绝关闭(安全默认,请人工处理)。")]
	public static string se_close_document(SolidEdgeContext context, [Description("要关闭的文档:obj-N 句柄或文档全路径(必须是本会话 se_open_document 打开过的)")] string target, [Description("文档有未保存修改(Dirty=true)或修改状态读不出时,必须显式置 true 才会丢弃修改并关闭;本工具永不保存")] bool confirm = false)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(target))
			{
				return Error("target 不能为空(传 obj-N 句柄或文档全路径)。");
			}
			return context.Invoke(delegate
			{
				Application app = context.GetApplication();
				TrackedDocument tracked = FindTracked(context, target);
				if (tracked == null)
				{
					return Error("拒绝关闭:\"" + target + "\" 不在本会话追踪表里(se_close_document 只能关闭由 se_open_document 打开的文档)。当前追踪表: " + ListTracked() + "。用户手动打开的文档请由用户自行关闭。");
				}
				bool? dirty = TryGetBool(tracked.Document, "Dirty");
				if ((!dirty.HasValue || dirty.Value) && !confirm)
				{
					return JsonSerializer.Serialize(new
					{
						status = "error",
						message = (!dirty.HasValue)
							? "拒绝关闭:文档修改状态(Dirty)读不出,按保守策略需要确认。确认丢弃修改并关闭请追加 confirm=true(本工具永不保存)。"
							: "拒绝关闭:文档有未保存修改(Dirty=true)。确认丢弃修改并关闭请追加 confirm=true(本工具永不保存)。",
						fullName = tracked.FullName,
						dirty,
						requiresConfirm = true
					});
				}
				bool previousAlerts = true;
				bool alertsChanged = false;
				try
				{
					try
					{
						previousAlerts = app.DisplayAlerts;
						app.DisplayAlerts = false;
						alertsChanged = true;
					}
					catch
					{
					}
					bool discard = !dirty.HasValue || dirty.Value;
					object[] closeArgs = discard
						? new object[3] { false, Type.Missing, Type.Missing }
						: new object[3] { Type.Missing, Type.Missing, Type.Missing };
					tracked.Document.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, tracked.Document, closeArgs);
				}
				finally
				{
					if (alertsChanged)
					{
						try { app.DisplayAlerts = previousAlerts; } catch { }
					}
				}
				TrackedDocs.TryRemove(tracked.FullName, out var _);
				bool discarded = dirty.HasValue && dirty.Value;
				AuditLog.Write("se_close_document", tracked.FullName, "Close", false, tracked.FullName, discarded ? InvocationRisk.Destructive : InvocationRisk.ModelChanging, true, discarded ? "丢弃未保存修改" : null);
				string newActive = null;
				try
				{
					object activeDocument = app.ActiveDocument;
					if (activeDocument != null)
					{
						newActive = SafeGetProperty(activeDocument, "FullName");
					}
				}
				catch
				{
				}
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					closed = tracked.FullName,
					dirtyWas = dirty,
					discardedChanges = discarded,
					remainingTracked = TrackedDocs.Count,
					newActiveDocument = newActive ?? "(无)",
					hint = "文档已关闭并移出追踪表。句柄表中指向该文档的其他 obj-N 句柄已失效,继续使用请重新获取。"
				});
			});
		}
		catch (Exception ex)
		{
			return Error("关闭文档失败: " + DescribeError(ex));
		}
	}

	[McpServerTool]
	[Description("新建一个空的 Solid Edge 文档(未保存)并登记句柄、进入追踪表(origin=created)。只有本工具创建的文档会进追踪表;se_close_document 只能关闭追踪表里的文档。类型支持 part/assembly/draft/sheetmetal/weldment,也可直接传 ProgID(如 SolidEdge.PartDocument)。注意:新建文档从未保存,SE 视其为已修改(Dirty),因此关闭时需要 confirm=true——新建文档无内容,丢弃无损失。工具组没有独立保存工具,落盘走 se_invoke_member(句柄,'SaveAs',['全路径'])或脚本;要放进装配(se_assembly_build place)的零件必须先 SaveAs 落盘——place 的 file 参数需要文件路径。测试纪律:先盘点复用(se_invoke_chain 查 Documents.Count),确实要新建才用本工具,收尾逐个 se_close_document,别让测试文档堆积。")]
	public static string se_new_document(SolidEdgeContext context, [Description("文档类型:part(默认)/assembly/draft/sheetmetal/weldment,或直接传 ProgID")] string docType = "part")
	{
		try
		{
			string progId = ResolveDocumentProgId(docType);
			if (progId == null)
			{
				return Error("不支持的文档类型 \"" + docType + "\"。支持: part / assembly / draft / sheetmetal / weldment,或直接传 ProgID(如 SolidEdge.PartDocument)。");
			}
			return context.Invoke(delegate
			{
				Application app = context.GetApplication();
				bool previousAlerts = true;
				bool alertsChanged = false;
				try
				{
					try
					{
						previousAlerts = app.DisplayAlerts;
						app.DisplayAlerts = false;
						alertsChanged = true;
					}
					catch
					{
					}
					object documents = app.Documents;
					object doc = documents.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, documents, new object[1] { progId });
					if (doc == null)
					{
						return Error("Documents.Add 返回空,新建失败: " + progId);
					}
					string name = SafeGetProperty(doc, "Name");
					string resolvedType = TryResolveComTypeName(doc);
					string typeShort = ((resolvedType != null) ? resolvedType.Substring(resolvedType.LastIndexOf('.') + 1) : doc.GetType().Name);
					bool? dirty = TryGetBool(doc, "Dirty");
					string handleId = context.AddHandle(doc, typeShort, name);
					TrackedDocs[name] = new TrackedDocument
					{
						Document = doc,
						Key = name,
						FullName = name,
						LastHandleId = handleId,
						RequestedReadOnly = false,
						Origin = "created",
						OpenedAt = DateTime.Now
					};
					AuditLog.Write("se_new_document", handleId, "Add", false, progId, InvocationRisk.ModelChanging, true, null);
					return JsonSerializer.Serialize(new
					{
						status = "ok",
						handle = handleId,
						name,
						key = name,
						type = resolvedType ?? "(未知)",
						typeShort,
						dirty,
						origin = "created",
						createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
						tracked = true,
						hint = "未命名文档没有 FullName,追踪表以文档名为 key;SaveAs 后请继续用句柄关闭(key 不随改名更新)。该文档已切换为 ActiveDocument;关闭需 confirm=true(新文档在 SE 里视为已修改)。落盘:se_invoke_member(本句柄,'SaveAs',['全路径']) 或脚本;要装配 place 的零件必须先落盘(place.file 需文件路径)。"
					});
				}
				finally
				{
					if (alertsChanged)
					{
						try { app.DisplayAlerts = previousAlerts; } catch { }
					}
				}
			});
		}
		catch (Exception ex)
		{
			return Error("新建文档失败: " + DescribeError(ex));
		}
	}

	private static string ResolveDocumentProgId(string docType)
	{
		if (string.IsNullOrWhiteSpace(docType))
		{
			return "SolidEdge.PartDocument";
		}
		string text = docType.Trim();
		if (text.IndexOf('.') >= 0)
		{
			return text;
		}
		switch (text.ToLowerInvariant())
		{
		case "part":
			return "SolidEdge.PartDocument";
		case "assembly":
			return "SolidEdge.AssemblyDocument";
		case "draft":
			return "SolidEdge.DraftDocument";
		case "sheetmetal":
			return "SolidEdge.SheetMetalDocument";
		case "weldment":
			return "SolidEdge.WeldmentDocument";
		default:
			return null;
		}
	}

	/// <summary>按 obj-N 句柄或全路径在追踪表里找文档;找不到返回 null。</summary>
	private static TrackedDocument FindTracked(SolidEdgeContext context, string target)
	{
		string text = target.Trim('"');
		if (text.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
		{
			ObjectHandle handle = context.GetHandle(text);
			if (handle == null || handle.ComObject == null)
			{
				return null;
			}
			foreach (KeyValuePair<string, TrackedDocument> item in TrackedDocs)
			{
				if (IsSameComObject(item.Value.Document, handle.ComObject))
				{
					return item.Value;
				}
			}
			return null;
		}
		try
		{
			string fullPath = Path.GetFullPath(text);
			if (TrackedDocs.TryGetValue(fullPath, out var byPath))
			{
				return byPath;
			}
		}
		catch
		{
		}
		// 三级回退:key 本身(新建未命名文档的 key 是"零件N"这类文档名)/全路径文件名/文档当前 Name。
		// 新建未命名文档没有 FullName,必须靠 Key 或实时 Name 才能命中。
		string fileName = Path.GetFileName(text);
		return TrackedDocs.Values.FirstOrDefault((TrackedDocument t) =>
			string.Equals(t.Key, text, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(Path.GetFileName(t.FullName ?? ""), fileName, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(SafeGetProperty(t.Document, "Name"), text, StringComparison.OrdinalIgnoreCase));
	}

	private static string ListTracked()
	{
		if (TrackedDocs.IsEmpty)
		{
			return "(空)";
		}
		return string.Join("; ", TrackedDocs.Values.Select((TrackedDocument t) => t.FullName));
	}

	private static bool IsSameComObject(object a, object b)
	{
		if (a == null || b == null)
		{
			return false;
		}
		if (!Marshal.IsComObject(a) || !Marshal.IsComObject(b))
		{
			return ReferenceEquals(a, b);
		}
		IntPtr ptrA = Marshal.GetIUnknownForObject(a);
		try
		{
			IntPtr ptrB = Marshal.GetIUnknownForObject(b);
			try
			{
				return ptrA == ptrB;
			}
			finally
			{
				if (ptrB != IntPtr.Zero)
				{
					Marshal.Release(ptrB);
				}
			}
		}
		finally
		{
			if (ptrA != IntPtr.Zero)
			{
				Marshal.Release(ptrA);
			}
		}
	}

	private static int? SafeProbeCount(object doc, string collectionName)
	{
		try
		{
			object collection = doc.GetType().InvokeMember(collectionName, BindingFlags.GetProperty, null, doc, null);
			if (collection == null)
			{
				return null;
			}
			object count = collection.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, collection, null);
			return Convert.ToInt32(count);
		}
		catch
		{
			return null;
		}
	}

	private static string ProbeName(object doc)
	{
		if (SafeProbeCount(doc, "Sheets").HasValue)
		{
			return "Sheets";
		}
		if (SafeProbeCount(doc, "Models").HasValue)
		{
			return "Models";
		}
		if (SafeProbeCount(doc, "Occurrences").HasValue)
		{
			return "Occurrences";
		}
		return "(无)";
	}

	private static bool? TryGetBool(object obj, string propertyName)
	{
		try
		{
			object value = obj.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, obj, null);
			if (value == null)
			{
				return null;
			}
			return Convert.ToBoolean(value);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>取 COM 真实类型名;取不到返回 null(由调用方回退到 .NET 类型名)。</summary>
	private static string TryResolveComTypeName(object comObject)
	{
		try
		{
			ComPtr comPtr = ComPtr.FromRCW(comObject);
			ComTypeInfo comTypeInfo = comPtr.TryGetComTypeInfo();
			if (comTypeInfo != null && !string.IsNullOrEmpty(comTypeInfo.FullName))
			{
				return comTypeInfo.FullName;
			}
		}
		catch
		{
		}
		return null;
	}

	private static string SafeGetProperty(object obj, string propertyName)
	{
		try
		{
			return obj.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, obj, null)?.ToString() ?? "(空)";
		}
		catch
		{
			return "(未知)";
		}
	}

	private static string SafeGetActiveEnvironmentName(Application app)
	{
		try
		{
			return app.ActiveEnvironment?.ToString() ?? "(未知)";
		}
		catch
		{
			return "(未知)";
		}
	}

	private static string DescribeError(Exception ex)
	{
		string message = ex.Message;
		for (Exception e = ex.InnerException; e != null; e = e.InnerException)
		{
			message += " <- " + e.Message;
		}
		return message;
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
