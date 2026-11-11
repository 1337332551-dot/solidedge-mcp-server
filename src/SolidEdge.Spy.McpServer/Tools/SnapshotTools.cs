using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol.Server;
using SolidEdge.Spy.InteropServices;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_snapshot_diff:命名快照 + 差异对比。解决实测中反复出现的三类活:
///   ① "改参数/重建对象前后逐项对照"(以前每次都要手写两遍探针脚本再肉眼比)
///   ② "SE 崩溃后盘点残留对象"(崩溃丢对象后不知道还剩什么)
///   ③ "幂等脚本跑完校验"(改前 save、改后 diff,结果为空即幂等成立)
///
/// 与 obj-N / Name / Key 无关:集合成员按名字配对,配不上时用几何指纹兜底 —— SE 每次重建对象连 ID 都会变。
/// 快照落盘到 %LOCALAPPDATA%\SolidEdgeSpy\snapshots\&lt;name&gt;.json,进程重启甚至 SE 崩溃后依然可 diff。
/// </summary>
[McpServerToolType]
public static class SnapshotTools
{
	private static readonly object Sync = new object();
	private static readonly Dictionary<string, SnapshotDoc> Cache = new Dictionary<string, SnapshotDoc>(StringComparer.OrdinalIgnoreCase);
	private static int _typeLibsLoaded;

	[McpServerTool]
	[Description("对象/拓扑快照与差异对比。scope=objects(默认):把 obj-N 指向的集合(如 Sheet 的 Dimensions/Lines2d)或单个对象的可读属性存成命名快照," +
		"配对按集合成员 Name/单对象属性名(值比较失败回退几何指纹),参数用 target;" +
		"scope=faces:模型拓扑面级快照,按 Face.ID 配对(面被完全覆盖会变成 removed+added),参数用 objectId(文档句柄,省略=活动文档)与 areaTolerancePct(默认 1%)。\n" +
		"action=save/diff/list/drop;diff 输出 added/removed/changed(含旧值→新值),faces 档 identical=true=面集合与特征状态全一致(幂等判据)。\n" +
		"快照落盘 %LOCALAPPDATA%\\SolidEdgeSpy\\{snapshots|topology},重启后仍在;comparable=false=快照与当前文档不是同一零件。\n" +
		"⚠️ scope 决定 target(objects 用)与 objectId(faces 用)的取义。典型用途:改参数/重建前后对照、拓扑级\"这次特征新增了哪些面\"、幂等校验。只读。")]
	public static string se_snapshot_diff(
		SolidEdgeContext context,
		[Description("动作:save=保存快照 / diff=与已存快照对比 / list=列出快照 / drop=删除快照")] string action = "save",
		[Description("快照名(如 before_fix / after_rerun / crash_residue);save/diff/drop 必填")] string name = null,
		[Description("快照域:objects(默认)=对象属性快照(用 target) | faces=拓扑面级快照(用 objectId,配 Face.ID,即原 se_model_state_snapshot)")] string scope = "objects",
		[Description("scope=objects 的 save 必填:目标 obj-N(集合如 Sheet.Dimensions、Lines2d,或单个对象);diff 可省略(默认按快照记录重新解析,解析不到再要求传)")] string target = null,
		[Description("scope=faces 可选:文档句柄 obj-N;省略=活动文档")] string objectId = null,
		[Description("scope=faces 可选:面积变化超过该百分比才算 changed,默认 1.0")] double areaTolerancePct = 1.0,
		[Description("可选,列表输出上限:objects 档默认 500(枚举集合成员),faces 档默认 50(diff 列表每项条数);0=按 scope 取默认")] int maxItems = 0,
		[Description("scope=objects 可选,只保留 Name 含该子串的成员(如 \"Dimension\"),省略=全部")] string nameFilter = null)
	{
		try
		{
			string act = string.IsNullOrWhiteSpace(action) ? "save" : action.Trim().ToLowerInvariant();
			string sc = string.IsNullOrWhiteSpace(scope) ? "objects" : scope.Trim().ToLowerInvariant();
			if (sc == "faces")
			{
				// 拓扑面级快照(原 se_model_state_snapshot,2026-11-07 并入):整条链路转调原实现,行为零漂移
				if (maxItems <= 0) maxItems = 50;
				return ModelStateSnapshotTools.se_model_state_snapshot(context, act, name, areaTolerancePct, maxItems, objectId);
			}
			if (sc != "objects")
			{
				return Error("未知 scope \"" + scope + "\"。可用:objects(默认) / faces。");
			}
			if (act == "list") return ListSnapshots();
			if (string.IsNullOrWhiteSpace(name)) return Error("name 不能为空(save/diff/drop 都要快照名)。");
			string snapName = name.Trim();
			if (!IsSafeName(snapName)) return Error("name 非法:只允许字母/数字/下划线/短横线/点,长度 1~64。收到: \"" + name + "\"");
			if (maxItems <= 0) maxItems = 500;
			if (act == "drop") return DropSnapshot(snapName);
			if (act == "save") return context.Invoke(() => SaveCore(context, snapName, target, maxItems, nameFilter));
			if (act == "diff") return context.Invoke(() => DiffCore(context, snapName, target, maxItems, nameFilter));
			return Error("未知 action \"" + action + "\"。可用:save / diff / list / drop。");
		}
		catch (Exception ex)
		{
			if (SolidEdgeContext.IsDisconnected(ex))
			{
				return Error("COM 对象已断连(文档可能已被关闭,或对象已删除/句柄失效)。请重新调用 se_get_selection 获取新句柄。原始错误: " + DescribeException(ex));
			}
			return Error("快照操作失败: " + DescribeException(ex));
		}
	}

	// ---------------- save ----------------

	private static string SaveCore(SolidEdgeContext context, string name, string target, int maxItems, string nameFilter)
	{
		if (string.IsNullOrWhiteSpace(target))
		{
			return Error("save 需要 target(obj-N):集合对象(如 Sheet 的 Dimensions / Lines2d)或单个对象。先用 se_get_selection 或 se_invoke_member 拿到句柄。");
		}
		string targetId = target.Trim();
		ObjectHandle handle = context.GetHandle(targetId);
		if (handle == null || handle.ComObject == null)
		{
			return Error("找不到对象编号 " + targetId + "。句柄只由 se_get_selection 与 se_invoke_member 的返回值产生。");
		}

		var doc = new SnapshotDoc
		{
			Name = name,
			Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
			TargetId = targetId,
			TargetType = handle.TypeName,
			TargetDisplay = handle.DisplayName,
			Filter = nameFilter
		};
		CaptureCurrent(handle.ComObject, doc, maxItems, nameFilter);

		string file = FileOf(name);
		try
		{
			string dir = Path.GetDirectoryName(file);
			if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
			File.WriteAllText(file, JsonSerializer.Serialize(doc, JsonOpts()), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch (Exception ex)
		{
			return Error("写快照文件失败: " + ex.Message);
		}
		lock (Sync) { Cache[name] = doc; }

		return JsonSerializer.Serialize(new
		{
			status = "ok",
			action = "save",
			name = name,
			kind = doc.Kind,
			targetType = doc.TargetType,
			targetDisplay = doc.TargetDisplay,
			itemCount = doc.Items?.Count ?? 0,
			propertyCount = doc.Props?.Count ?? 0,
			unreadableCount = doc.Unreadable?.Count ?? 0,
			file = file,
			hint = "改完/重跑完再调 action=diff(同一个 name)看 added/removed/changed。"
		});
	}

	// ---------------- diff ----------------

	private static string DiffCore(SolidEdgeContext context, string name, string target, int maxItems, string nameFilter)
	{
		SnapshotDoc old = LoadSnapshot(name);
		if (old == null) return Error("找不到快照 \"" + name + "\"。先 action=save 建一个,或用 action=list 看已有的。");

		object com = null;
		string resolvedFrom = null;
		if (!string.IsNullOrWhiteSpace(target))
		{
			ObjectHandle h = context.GetHandle(target.Trim());
			if (h == null || h.ComObject == null) return Error("找不到对象编号 " + target.Trim() + "(diff 的 target 指定了但句柄不存在)。");
			com = h.ComObject;
			resolvedFrom = "参数 target=" + target.Trim();
		}
		else if (!string.IsNullOrWhiteSpace(old.TargetId))
		{
			ObjectHandle h2 = context.GetHandle(old.TargetId);
			if (h2 != null && h2.ComObject != null)
			{
				com = h2.ComObject;
				resolvedFrom = "快照记录的 " + old.TargetId;
			}
		}
		if (com == null)
		{
			return Error("无法定位对比目标:快照记录的是 " + (string.IsNullOrWhiteSpace(old.TargetId) ? "(无)" : old.TargetId) + ",而当前句柄表里没有它(句柄不跨会话/进程)。请重新取得句柄后用参数 target=obj-N 指定同一对象再 diff。");
		}

		var cur = new SnapshotDoc
		{
			Name = name,
			Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
			TargetId = target,
			TargetType = ObjectFingerprint.TypeName(com),
			TargetDisplay = ObjectFingerprint.DisplayName(com),
			Filter = nameFilter
		};
		CaptureCurrent(com, cur, maxItems, nameFilter);

		if (old.Kind != cur.Kind)
		{
			return Error("快照类型与当前对象不一致(快照是 " + old.Kind + ",当前是 " + cur.Kind + ")。请用同一个对象重新 save。");
		}

		var added = new List<object>();
		var removed = new List<object>();
		var changed = new List<object>();

		if (cur.Kind == "collection")
		{
			Dictionary<string, SnapItem> oldMap = Index(old.Items);
			Dictionary<string, SnapItem> newMap = Index(cur.Items);
			foreach (var kv in newMap)
			{
				SnapItem o;
				if (!oldMap.TryGetValue(kv.Key, out o))
				{
					// 名字配不上:再退一步按几何指纹找(对象被重建、Name 变了但位置没变)
					SnapItem byGeo = FindByGeometric(old.Items, kv.Value.Geometric);
					if (byGeo != null)
					{
						changed.Add(new { key = kv.Key, match = "geometric", from = byGeo, to = kv.Value });
					}
					else
					{
						added.Add(kv.Value);
					}
					continue;
				}
				if (!SameItem(o, kv.Value))
				{
					changed.Add(new { key = kv.Key, match = "name", from = o, to = kv.Value });
				}
			}
			foreach (var kv in oldMap)
			{
				if (!newMap.ContainsKey(kv.Key) && FindByGeometric(cur.Items, kv.Value.Geometric) == null)
				{
					removed.Add(kv.Value);
				}
			}
		}
		else
		{
			foreach (var kv in cur.Props)
			{
				string ov;
				if (!old.Props.TryGetValue(kv.Key, out ov)) added.Add(new { property = kv.Key, value = kv.Value });
				else if (!string.Equals(ov, kv.Value, StringComparison.Ordinal)) changed.Add(new { property = kv.Key, from = ov, to = kv.Value });
			}
			foreach (var kv in old.Props)
			{
				if (!cur.Props.ContainsKey(kv.Key)) removed.Add(new { property = kv.Key, value = kv.Value });
			}
		}

		return JsonSerializer.Serialize(new
		{
			status = "ok",
			action = "diff",
			name = name,
			kind = cur.Kind,
			oldTime = old.Time,
			newTime = cur.Time,
			oldCount = old.Kind == "collection" ? (old.Items?.Count ?? 0) : (old.Props?.Count ?? 0),
			newCount = cur.Kind == "collection" ? (cur.Items?.Count ?? 0) : (cur.Props?.Count ?? 0),
			addedCount = added.Count,
			removedCount = removed.Count,
			changedCount = changed.Count,
			identical = (added.Count == 0 && removed.Count == 0 && changed.Count == 0),
			added = added,
			removed = removed,
			changed = changed,
			unreadableNow = cur.Unreadable,
			hint = "identical=true 表示两次快照完全一致(幂等校验通过)。changed 的 match=geometric 表示按名字没配上、靠几何指纹认出的同一个对象(SE 重建对象后 Name/ID 会变,属正常)。"
		});
	}

	// ---------------- list / drop ----------------

	private static string ListSnapshots()
	{
		var list = new List<object>();
		string dir = SnapDir();
		if (Directory.Exists(dir))
		{
			foreach (string f in Directory.GetFiles(dir, "*.json"))
			{
				try
				{
					SnapshotDoc d = JsonSerializer.Deserialize<SnapshotDoc>(File.ReadAllText(f));
					if (d == null) continue;
					list.Add(new
					{
						name = d.Name,
						time = d.Time,
						kind = d.Kind,
						targetType = d.TargetType,
						targetDisplay = d.TargetDisplay,
						count = d.Kind == "collection" ? (d.Items?.Count ?? 0) : (d.Props?.Count ?? 0),
						file = f
					});
				}
				catch
				{
				}
			}
		}
		return JsonSerializer.Serialize(new { status = "ok", action = "list", count = list.Count, snapshots = list, dir = dir });
	}

	private static string DropSnapshot(string name)
	{
		string file = FileOf(name);
		bool deleted = false;
		try
		{
			if (File.Exists(file)) { File.Delete(file); deleted = true; }
		}
		catch (Exception ex)
		{
			return Error("删除快照文件失败: " + ex.Message);
		}
		lock (Sync) { Cache.Remove(name); }
		return JsonSerializer.Serialize(new { status = "ok", action = "drop", name = name, deleted = deleted, file = file });
	}

	// ---------------- 采集 ----------------

	private static void CaptureCurrent(object com, SnapshotDoc doc, int maxItems, string nameFilter)
	{
		int? count = ObjectFingerprint.TryCount(com);
		if (count.HasValue)
		{
			doc.Kind = "collection";
			doc.Items = new List<SnapItem>();
			int limit = Math.Min(Math.Max(count.Value, 0), Math.Max(maxItems, 1));
			for (int i = 1; i <= limit; i++)
			{
				object item = ObjectFingerprint.TryItem(com, i);
				if (item == null) continue;
				string nm = ObjectFingerprint.DisplayName(item);
				if (!string.IsNullOrWhiteSpace(nameFilter))
				{
					string typeName = ObjectFingerprint.TypeName(item) ?? "";
					if ((nm ?? "").IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0
						&& typeName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}
				}
				doc.Items.Add(new SnapItem
				{
					Index = i,
					Name = nm,
					Type = ObjectFingerprint.TypeName(item),
					Identity = ObjectFingerprint.Identity(item),
					Geometric = ObjectFingerprint.GeometricKey(item)
				});
			}
		}
		else
		{
			doc.Kind = "object";
			doc.Props = new Dictionary<string, string>(StringComparer.Ordinal);
			doc.Unreadable = new List<string>();
			DumpProperties(com, doc.Props, doc.Unreadable, Math.Max(maxItems, 1));
		}
	}

	private static void DumpProperties(object target, Dictionary<string, string> props, List<string> unreadable, int maxProps)
	{
		EnsureTypeLibrariesLoaded();
		ComPtr ptr = null;
		ComTypeInfo ti = null;
		try
		{
			ptr = ComPtr.FromRCW(target);
			ti = ptr.TryGetComTypeInfo();
		}
		catch
		{
		}
		if (ti == null)
		{
			unreadable.Add("(无法获取 COM 类型信息,未能枚举属性;请改用集合目标或 se_describe_object)");
			try { ptr?.Dispose(); } catch { }
			return;
		}
		try
		{
			ComPropertyInfo[] ps = ti.GetProperties(includeInherited: true);
			foreach (ComPropertyInfo p in ps)
			{
				string n = p.Name;
				if (BlockedMembers.IsBlocked(n)) continue;
				if (props.Count >= maxProps) break;
				try
				{
					object v = target.GetType().InvokeMember(n, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, null);
					props[n] = FmtValue(v);
				}
				catch
				{
					unreadable.Add(n);
				}
			}
		}
		catch (Exception ex)
		{
			unreadable.Add("(枚举属性失败: " + ex.GetType().Name + ")");
		}
		finally
		{
			try { ptr?.Dispose(); } catch { }
		}
	}

	private static string FmtValue(object v)
	{
		if (v == null) return "(null)";
		if (Marshal.IsComObject(v))
		{
			string t = ObjectFingerprint.TypeName(v);
			string n = ObjectFingerprint.DisplayName(v);
			return "(" + (t ?? "COM对象") + (string.IsNullOrWhiteSpace(n) ? "" : " " + n) + ")";
		}
		return ObjectFingerprint.Str(v) ?? "";
	}

	// ---------------- 比对辅助 ----------------

	private static Dictionary<string, SnapItem> Index(List<SnapItem> items)
	{
		var map = new Dictionary<string, SnapItem>(StringComparer.Ordinal);
		if (items == null) return map;
		foreach (SnapItem it in items)
		{
			string key = string.IsNullOrWhiteSpace(it.Name) ? ("#" + it.Index) : it.Name;
			if (map.ContainsKey(key)) key = key + "#" + it.Index;
			map[key] = it;
		}
		return map;
	}

	private static bool SameItem(SnapItem a, SnapItem b)
	{
		if (a == null || b == null) return false;
		if (!string.Equals(a.Type, b.Type, StringComparison.Ordinal)) return false;
		if (!string.Equals(a.Geometric, b.Geometric, StringComparison.Ordinal)) return false;
		return true;
	}

	private static SnapItem FindByGeometric(List<SnapItem> items, string geometric)
	{
		if (string.IsNullOrWhiteSpace(geometric) || items == null) return null;
		foreach (SnapItem it in items)
		{
			if (string.Equals(it.Geometric, geometric, StringComparison.Ordinal)) return it;
		}
		return null;
	}

	// ---------------- 存储 ----------------

	private static string SnapDir()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidEdgeSpy", "snapshots");
	}

	private static string FileOf(string name)
	{
		return Path.Combine(SnapDir(), name + ".json");
	}

	private static SnapshotDoc LoadSnapshot(string name)
	{
		lock (Sync)
		{
			SnapshotDoc d;
			if (Cache.TryGetValue(name, out d)) return d;
		}
		string file = FileOf(name);
		if (!File.Exists(file)) return null;
		try
		{
			SnapshotDoc d2 = JsonSerializer.Deserialize<SnapshotDoc>(File.ReadAllText(file));
			if (d2 != null) lock (Sync) { Cache[name] = d2; }
			return d2;
		}
		catch
		{
			return null;
		}
	}

	private static bool IsSafeName(string s)
	{
		if (s.Length == 0 || s.Length > 64) return false;
		foreach (char c in s)
		{
			if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.') return false;
		}
		return true;
	}

	private static JsonSerializerOptions JsonOpts()
	{
		return new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
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
		return JsonSerializer.Serialize(new { status = "error", message = message });
	}

	private static string DescribeException(Exception ex)
	{
		// P2-1:统一走 EvidenceContract(本文件旧版没接 P1-1 码表,收编后自动补齐)
		return EvidenceContract.DescribeException(ex);
	}

	// ---------------- 数据模型 ----------------

	public sealed class SnapshotDoc
	{
		public string Name { get; set; }
		public string Time { get; set; }
		public string Kind { get; set; }
		public string TargetId { get; set; }
		public string TargetType { get; set; }
		public string TargetDisplay { get; set; }
		public string Filter { get; set; }
		public List<SnapItem> Items { get; set; }
		public Dictionary<string, string> Props { get; set; }
		public List<string> Unreadable { get; set; }
	}

	public sealed class SnapItem
	{
		public int Index { get; set; }
		public string Name { get; set; }
		public string Type { get; set; }
		public string Identity { get; set; }
		public string Geometric { get; set; }
	}
}
