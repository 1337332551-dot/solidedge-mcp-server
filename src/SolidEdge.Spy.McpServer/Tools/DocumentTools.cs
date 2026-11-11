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
	}

	private static readonly ConcurrentDictionary<string, TrackedDocument> TrackedDocs =
		new ConcurrentDictionary<string, TrackedDocument>(StringComparer.OrdinalIgnoreCase);

	/// <summary>该 COM 对象是否为本会话追踪的文档(open/new 登记)。internal 供 InvokeTools 拦截通用通道 Close(2026-11-06 自 dot/master 同步)。</summary>
	internal static bool IsTrackedDocument(object doc)
	{
		if (doc == null || !Marshal.IsComObject(doc)) return false;
		foreach (var entry in TrackedDocs.Values)
		{
			if (ReferenceEquals(entry.Document, doc)) return true;
		}
		return false;
	}

	[McpServerTool]
	[Description("获取当前活动文档的基本信息:COM 类型名、文件名、全路径、所在环境,以及内容探针" +
		"(modelsCount/occurrencesCount/sheetsCount,不适用为 null)。" +
		"返回含 build=构建时间戳(部署滞后自检:与源码改动时间对不上=跑的是旧二进制)。" +
		"调用其它工具前先调它了解上下文;建模前确认文档身份与是否空模板也用它。")]
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
						sheetsCount = SafeProbeCount(activeDocument, "Sheets"),
						// 版本自报(P0-1,2026-11-10):csproj 注入 AssemblyMetadata(BuildTime),每次构建刷新。
						build = SolidEdge.Spy.McpServer.Telemetry.BuildInfo.Stamp
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
	[Description("打开一个 Solid Edge 文档(.par/.asm/.dft/.psm/.pwd 等)并登记句柄。只有本工具打开的文档进追踪表," +
		"se_close_document 只能关追踪表里的,不会误关用户手开的文档。" +
		"Documents.Open 没有 readonly 参数,结果里如实报告实际只读状态;要强制只读请设 SE_MCP_READONLY=1。" +
		"Open 前临时 DisplayAlerts=false 防对话框卡死,返回前用 Count 探针确认可读。" +
		"每次打开都切成 ActiveDocument;同文件重复打开返回现有文档并刷新追踪表。")]
	public static string se_open_document(SolidEdgeContext context, [Description("要打开的文件完整路径")] string path, [Description("期望只读。SE API 不支持只读打开,本参数仅用于比对结果里的实际状态")] bool readOnly = false)
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
						FullName = fullName
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
	[Description("关闭一个由 se_open_document 打开的文档。只能关本会话追踪表里的文档,用户手开的永远关不掉。" +
		"target 传 obj-N 或全路径。关闭前检查 Document.Dirty:有未保存修改(或读不出)时默认拒绝,必须显式 confirm=true;" +
		"本工具永不保存,confirm 后按丢弃修改关闭。" +
		"MCP 进程重启后追踪表清空,重启前打开的文档会因'无主'被拒绝关闭(请人工处理)。")]
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
				// 用 RemoveTracked 而不是按 FullName 删:SaveAs 后追踪表里可能存着旧 key 别名,
				// 只删一个 key 会留下指向已关闭文档的悬空条目。
				RemoveTracked(tracked);
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
					remainingTracked = TrackedDocs.Values.Distinct().Count(),
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
	[Description("把当前文档落盘(SaveAs)。①占用检测:目标路径正被本会话另一个文档占用时默认自动关闭它" +
		"(Dirty=true 时拒绝并指路 se_close_document),避免 EBUSY/覆盖弹窗;" +
		"②覆盖策略:目标已存在默认拒绝(overwrite=false),显式 true 才先删再存;" +
		"③成功判定靠回读 Document.FullName 与目标路径比对——不看'没抛异常'。" +
		"保存后追踪表跟随改名(旧 key 保留为别名)。只能保存追踪表内的文档;缺扩展名按文档类型自动补。")]
	public static string se_save_document(
		SolidEdgeContext context,
		[Description("目标文件完整路径,如 E:\\out\\Flange.par。缺扩展名时按当前文档类型自动补。")] string path,
		[Description("要保存的文档:obj-N 句柄或文档全路径;缺省=当前活动文档。必须在追踪表内。")] string target = null,
		[Description("目标文件已存在时是否覆盖(默认 false=拒绝,防误删)。true 会先删除已存在文件再保存。")] bool overwrite = false,
		[Description("目标路径被本会话另一个文档占用时是否自动关闭它(默认 true);它 Dirty 时仍拒绝。")] bool closeConflict = true)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return Error("path 不能为空(要保存到的完整路径,如 E:\\out\\Flange.par)。");
			}
			return context.Invoke(delegate
			{
				Application app = context.GetApplication();

				// ---------- 1) 定位文档(必须在追踪表内) ----------
				TrackedDocument tracked;
				if (string.IsNullOrWhiteSpace(target))
				{
					object active = null;
					try { active = app.ActiveDocument; }
					catch { active = null; }
					if (active == null)
					{
						return Error("当前没有活动文档,也没有给 target。请先打开或新建文档。");
					}
					tracked = FindTrackedByComObject(active);
					if (tracked == null)
					{
						return Error("拒绝保存:当前活动文档不在本会话追踪表里。se_save_document 只操作由 se_open_document / se_new_document 产生(或打开)的文档;"
									 + "用户手动打开的文档请自行保存,或先用 se_open_document 打开它使其进入追踪表。当前追踪表: " + ListTracked() + "。");
					}
				}
				else
				{
					tracked = FindTracked(context, target);
					if (tracked == null)
					{
						return Error("拒绝保存:\"" + target + "\" 不在本会话追踪表里。当前追踪表: " + ListTracked() + "。");
					}
				}

				// ---------- 2) 路径规范化 + 补扩展名 ----------
				string targetPath;
				try
				{
					targetPath = Path.GetFullPath(path.Trim().Trim('"'));
				}
				catch (Exception ex)
				{
					return Error("路径非法: " + ex.Message);
				}
				if (string.IsNullOrEmpty(Path.GetExtension(targetPath)))
				{
					string ext = GuessDocumentExtension(tracked.Document);
					if (!string.IsNullOrEmpty(ext))
					{
						targetPath = targetPath + ext;
					}
				}

				string previousFullName = TryGetString(tracked.Document, "FullName");
				bool wasUntitled = string.IsNullOrEmpty(previousFullName) || !IsAbsolutePath(previousFullName);

				// ---------- 3) 占用检测:目标路径是否已被另一个已打开文档占用 ----------
				List<object> occupied = new List<object>();
				List<string> occupiedDesc = new List<string>();
				try
				{
					object docs = app.Documents;
					int docCount = Convert.ToInt32(docs.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, docs, null));
					for (int i = 1; i <= docCount; i++)
					{
						object d = null;
						try { d = docs.GetType().InvokeMember("Item", BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, docs, new object[] { i }); }
						catch { continue; }
						if (d == null || IsSameComObject(d, tracked.Document)) continue;
						string fn = TryGetString(d, "FullName");
						if (string.IsNullOrEmpty(fn) || !IsAbsolutePath(fn)) continue;
						string normalized;
						try { normalized = Path.GetFullPath(fn); }
						catch { continue; }
						if (!string.Equals(normalized, targetPath, StringComparison.OrdinalIgnoreCase)) continue;
						occupied.Add(d);
						bool? dirty = TryGetBool(d, "Dirty");
						occupiedDesc.Add(normalized + "(dirty=" + (dirty.HasValue ? dirty.Value.ToString() : "读不出") + ")");
					}
				}
				catch (Exception ex)
				{
					// 探针失败不阻断:继续走后面的 File.Exists/Delete 分支,由它们给真实原因
					occupiedDesc.Add("占用探测失败: " + ex.Message);
				}

				List<string> closedConflicts = new List<string>();
				if (occupied.Count > 0)
				{
					if (!closeConflict)
					{
						return Error("拒绝保存:目标路径 " + targetPath + " 正被本会话另一个文档占用 [" + string.Join("; ", occupiedDesc)
									 + "]。可加 closeConflict=true 让它自动关闭(仅在该文档无未保存修改时),或改用另一个文件名。");
					}
					foreach (object d in occupied)
					{
						bool? dirty = TryGetBool(d, "Dirty");
						if (!dirty.HasValue || dirty.Value)
						{
							string fn = TryGetString(d, "FullName");
							return Error("拒绝保存:占用目标路径的文档 " + fn + " 有未保存修改(dirty=" + (dirty.HasValue ? "true" : "读不出")
										 + "),自动关闭会丢修改。请先用 se_close_document 决定丢弃或另存,再重试。");
						}
						try
						{
							d.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, d,
								new object[3] { false, Type.Missing, Type.Missing });
							TrackedDocument stale = FindTrackedByComObject(d);
							if (stale != null) RemoveTracked(stale);
							closedConflicts.Add(TryGetString(d, "FullName"));
						}
						catch (Exception ex)
						{
							return Error("占用文档关闭失败(" + ex.Message + "),请手动关闭后重试。目标路径: " + targetPath);
						}
					}
				}

				// ---------- 4) 覆盖策略 ----------
				if (File.Exists(targetPath))
				{
					if (!overwrite)
					{
						return Error("拒绝保存:目标文件已存在 " + targetPath + "。确认覆盖请传 overwrite=true(会先删除该文件;"
									 + "若它正被 Solid Edge 或其它程序占用,删除会失败并如实报错)。");
					}
					try
					{
						File.Delete(targetPath);
					}
					catch (Exception ex)
					{
						return Error("覆盖失败:无法删除已存在的 " + targetPath + " —— " + ex.Message
									 + "。常见原因:该文件仍被某个 Solid Edge 文档占用(先用 se_close_document 关掉)或被杀软/资源管理器锁定。");
					}
				}

				// ---------- 5) SaveAs(DisplayAlerts=false 防覆盖确认框) ----------
				Exception saveError = null;
				bool previousAlerts = true;
				bool alertsChanged = false;
				try
				{
					try { previousAlerts = app.DisplayAlerts; app.DisplayAlerts = false; alertsChanged = true; }
					catch { }
					try
					{
						tracked.Document.GetType().InvokeMember("SaveAs", BindingFlags.InvokeMethod, null,
							tracked.Document, new object[] { targetPath });
					}
					catch (Exception ex)
					{
						saveError = ex;
					}
				}
				finally
				{
					if (alertsChanged)
					{
						try { app.DisplayAlerts = previousAlerts; } catch { }
					}
				}

				// ---------- 6) 回读校验(不看"没抛异常") ----------
				string nowFullName = TryGetString(tracked.Document, "FullName");
				bool landed = false;
				try
				{
					landed = !string.IsNullOrEmpty(nowFullName) && IsAbsolutePath(nowFullName)
							 && string.Equals(Path.GetFullPath(nowFullName), targetPath, StringComparison.OrdinalIgnoreCase);
				}
				catch { landed = false; }

				if (saveError != null || !landed)
				{
					string why = saveError != null
						? "SaveAs 抛异常: " + DescribeError(saveError)
						: "SaveAs 没报错但回读 Document.FullName=\"" + (nowFullName ?? "(空)") + "\" 与目标路径不符(文件可能没落地)";
					return JsonSerializer.Serialize(new
					{
						status = "error",
						message = "保存失败: " + why,
						targetPath,
						documentFullNameNow = nowFullName,
						fileExists = File.Exists(targetPath),
						closedConflicts,
						hint = "兜底通道:se_invoke_member(文档句柄,'SaveAs',['" + targetPath.Replace("\\", "\\\\") + "'])。"
							   + "仍失败时优先怀疑:目标被占用(先 se_close_document)、目录无写权限、或路径含 SE 不接受的字符。"
					});
				}

				// ---------- 7) 追踪表跟随改名(旧 key 保留为别名) ----------
				string oldKey = tracked.Key;
				string newName = TryGetString(tracked.Document, "Name");
				List<string> removedKeys = RemoveTracked(tracked);
				tracked.FullName = targetPath;
				tracked.Key = targetPath;
				TrackedDocs[targetPath] = tracked;
				List<string> trackedKeys = new List<string> { targetPath };
				if (!string.IsNullOrEmpty(newName) && !trackedKeys.Contains(newName))
				{
					TrackedDocs[newName] = tracked;
					trackedKeys.Add(newName);
				}
				bool aliasKept = false;
				if (!string.IsNullOrEmpty(oldKey) && !string.Equals(oldKey, targetPath, StringComparison.OrdinalIgnoreCase))
				{
					TrackedDocs[oldKey] = tracked;
					trackedKeys.Add(oldKey);
					aliasKept = true;
				}

				AuditLog.Write("se_save_document", targetPath, "SaveAs", false, targetPath, InvocationRisk.ModelChanging, true, null);

				return JsonSerializer.Serialize(new
				{
					status = "ok",
					path = targetPath,
					previousFullName = string.IsNullOrEmpty(previousFullName) ? null : previousFullName,
					wasUntitled,
					overwrite,
					closedConflicts,
					readBack = new
					{
						fullName = nowFullName,
						name = newName,
						dirty = TryGetBool(tracked.Document, "Dirty"),
						fileExists = File.Exists(targetPath)
					},
					tracking = new
					{
						keys = trackedKeys,
						oldKeyKeptAsAlias = aliasKept,
						removedKeys,
						trackedDocumentCount = TrackedDocs.Values.Distinct().Count()
					},
					hint = "已落盘并回读确认。追踪表已跟随改名:se_close_document 用新路径、文件名或原 key 都能命中。"
				});
			});
		}
		catch (Exception ex)
		{
			return Error("保存文档失败: " + DescribeError(ex));
		}
	}

	[McpServerTool]
	[Description("新建一个空的 Solid Edge 文档(未保存)并登记句柄、进入追踪表(origin=created)。只有本工具创建的文档进追踪表。" +
		"类型支持 part/assembly/draft/sheetmetal/weldment,也可传 ProgID。" +
		"新建文档从未保存,SE 视为已修改(Dirty),关闭时需 confirm=true(无内容,丢弃无损失)。" +
		"要放进装配 place 的零件必须先落盘。")]
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
						FullName = name
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
						hint = "未命名文档没有 FullName,追踪表以文档名为 key;落盘请用 se_save_document(会回读确认,并让追踪表跟随改名,旧 key 保留为别名)。该文档已切换为 ActiveDocument;关闭需 confirm=true(新文档在 SE 里视为已修改)。要装配 place 的零件必须先落盘。"
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

	/// <summary>文档类型 → ProgID。internal:se_model_build 的 doc op 复用同一张表(不另写一份)。</summary>
	internal static string ResolveDocumentProgId(string docType)
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

	/// <summary>按 COM 对象在追踪表里找文档(缺省 target=ActiveDocument 时用)。</summary>
	private static TrackedDocument FindTrackedByComObject(object comObject)
	{
		if (comObject == null) return null;
		foreach (KeyValuePair<string, TrackedDocument> item in TrackedDocs)
		{
			if (IsSameComObject(item.Value.Document, comObject))
			{
				return item.Value;
			}
		}
		return null;
	}

	/// <summary>
	/// 把一个 tracked 文档从表里彻底摘掉——包括 SaveAs 后保留的旧 key 别名(别名指向同一对象,
	/// 只按 FullName 删会留下悬空条目,之后 FindTracked 会命中已关闭的文档)。
	/// </summary>
	private static List<string> RemoveTracked(TrackedDocument tracked)
	{
		List<string> removed = new List<string>();
		if (tracked == null) return removed;
		foreach (KeyValuePair<string, TrackedDocument> item in TrackedDocs)
		{
			if (ReferenceEquals(item.Value, tracked))
			{
				((IDictionary<string, TrackedDocument>)TrackedDocs).Remove(item.Key);
				removed.Add(item.Key);
			}
		}
		return removed;
	}

	/// <summary>读字符串属性;读不到返回 null(不像 SafeGetProperty 那样把 null 变成 "(空)",便于做路径比较)。</summary>
	private static string TryGetString(object obj, string propertyName)
	{
		try
		{
			object v = obj.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, obj, null);
			return v == null ? null : Convert.ToString(v);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>判断是否绝对路径——新建未保存文档的 FullName 往往等于文档名(如"零件9"),不能当路径用。</summary>
	private static bool IsAbsolutePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return false;
		try { return Path.IsPathRooted(path); }
		catch { return false; }
	}

	/// <summary>按当前文档类型猜扩展名:优先沿用现有 FullName 的扩展名,否则按 COM 类型名映射。</summary>
	private static string GuessDocumentExtension(object doc)
	{
		string full = TryGetString(doc, "FullName");
		if (!string.IsNullOrEmpty(full))
		{
			try
			{
				string ext = Path.GetExtension(full);
				if (!string.IsNullOrEmpty(ext)) return ext;
			}
			catch { }
		}
		string typeName = TryResolveComTypeName(doc);
		if (string.IsNullOrEmpty(typeName)) return null;
		switch (typeName.ToLowerInvariant())
		{
			case "solidedgepart.partdocument": return ".par";
			case "solidedgeassembly.assemblydocument": return ".asm";
			case "solidedgedraft.draftdocument": return ".dft";
			case "solidedgesheetmetal.sheetmetaldocument": return ".psm";
			case "solidedgeweldment.weldmentdocument": return ".pwd";
			default: return null;
		}
	}

	private static string ListTracked()
	{
		if (TrackedDocs.IsEmpty)
		{
			return "(空)";
		}
		// Distinct:SaveAs 后会保留旧 key 作别名,同一个文档可能出现多行
		return string.Join("; ", TrackedDocs.Values.Distinct().Select((TrackedDocument t) => t.FullName));
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
