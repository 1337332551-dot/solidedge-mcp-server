using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_model_state_snapshot：拓扑快照 + 差异对比（P2）。
///
/// 解决的是"改完到底变了什么"这个问题：
/// - 截图只能看"现在长什么样"，看不出"比刚才多/少了哪几个面"；
/// - se_snapshot_diff 是通用对象属性快照，配对靠 Name，而面根本没有 Name；
/// - 面级对比的唯一稳定键是 Face.ID（L2 铁律：跨重开/重启/改尺寸不变，只有"被完全覆盖"才换新 ID，
///   此时表现为 removed + added —— 这正是正确的语义）。
///
/// 快照落盘 %LOCALAPPDATA%\SolidEdgeSpy\topology\&lt;name&gt;.json，SE 崩溃/进程重启后依然可 diff。
/// </summary>
[McpServerToolType]
public static class ModelStateSnapshotTools
{
	// 2026-11-07:MCP 工具面已并入 se_snapshot_diff(scope="faces"),旧工具名
	// se_model_state_snapshot 由 ToolAliases 改写路由过去;本方法保留给 CLI --modelsnap
	// 与 SnapshotTools 的 faces 分支转调,行为零漂移。
	[Description("模型拓扑快照与差异对比(面级,按 Face.ID 配对)。action=save 存成命名快照" +
			"(落盘 %LOCALAPPDATA%\\SolidEdgeSpy\\topology,重启后仍在);action=diff 输出 added/removed/changed(面积或类型变化),每组带 ID/类型/位置;" +
			"action=list/drop。\n" +
			"identical=true 表示面集合与特征状态完全一致(幂等判据;体积变化但面积变化都低于容差时也为 true,体积看 volumeDeltaMm3)。" +
			"comparable=false 表示快照与当前文档不是同一零件(按 FullName/文档名判定),跨文档对比无意义,先在目标文档重新 save。" +
			"areaTolerancePct 默认 1%。配对:Face.ID 不变=同一个面;面被完全覆盖会变成 removed+added。只读。")]
	public static string se_model_state_snapshot(
		SolidEdgeContext context,
		[Description("动作：save=保存快照 / diff=与已存快照对比 / list=列出快照 / drop=删除快照")] string action = "save",
		[Description("快照名（如 before_hole / after_hole / expected_ok）；save/diff/drop 必填，只允许字母数字下划线短横线点，1~64 字符")] string name = null,
		[Description("diff 可选：面积变化超过该百分比才算 changed，默认 1.0")] double areaTolerancePct = 1.0,
		[Description("diff 可选：added/removed/changed 列表每项最多输出多少条，默认 50；超出部分只给计数")] int maxItems = 50,
		[Description("可选文档句柄 obj-N；省略=活动文档")] string objectId = null)
	{
		try
		{
			string act = string.IsNullOrWhiteSpace(action) ? "save" : action.Trim().ToLowerInvariant();
			if (act == "list") return ListSnapshots();
			if (string.IsNullOrWhiteSpace(name)) return Error("name 不能为空（save/diff/drop 都要快照名）。");
			string snapName = name.Trim();
			if (!IsSafeName(snapName))
				return Error("name 非法：只允许字母/数字/下划线/短横线/点，长度 1~64。收到: \"" + name + "\"");
			if (act == "drop") return DropSnapshot(snapName);
			if (act == "save") return context.Invoke(() => SaveCore(context, snapName, objectId));
			if (act == "diff") return context.Invoke(() => DiffCore(context, snapName, objectId, areaTolerancePct, maxItems));
			return Error("未知 action \"" + action + "\"。可用：save / diff / list / drop。");
		}
		catch (Exception ex)
		{
			return Error("拓扑快照操作失败: " + EvidenceContract.DescribeException(ex));
		}
	}

	// ---------------- save ----------------

	private static string SaveCore(SolidEdgeContext context, string name, string objectId)
	{
		var warnings = new List<string>();
		string error;
		TopoSnap snap = Capture(context, objectId, warnings, out error);
		if (snap == null) return Error(error);

		snap.name = name;
		snap.time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

		string file = FileOf(name);
		try
		{
			string dir = Path.GetDirectoryName(file);
			if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
			File.WriteAllText(file, JsonSerializer.Serialize(snap, JsonOpts()), new UTF8Encoding(false));
		}
		catch (Exception ex)
		{
			return Error("写快照文件失败: " + ex.Message);
		}

		int faceCount = 0;
		foreach (TopoModelSnap m in snap.models) faceCount += m.faceCount;

		return JsonSerializer.Serialize(new
		{
			status = "ok",
			action = "save",
			name,
			document = snap.document,
			modelCount = snap.models.Count,
			faceCount,
			featureCount = snap.features.Count,
			file,
			warnings = warnings.Count > 0 ? warnings : null,
			hint = "改完模型后再调 action=diff（同一个 name）：identical=true 即没有意外改动。"
		});
	}

	// ---------------- diff ----------------

	private static string DiffCore(SolidEdgeContext context, string name, string objectId,
		double tolerancePct, int maxItems)
	{
		string loadError;
		TopoSnap before = LoadSnapshot(name, out loadError);
		if (before == null)
			return Error(loadError + " 用 action=list 看已有快照。");

		var warnings = new List<string>();
		string error;
		TopoSnap after = Capture(context, objectId, warnings, out error);
		if (after == null) return Error(error);

		double tol = tolerancePct < 0 ? 0 : tolerancePct;
		int cap = Math.Max(1, maxItems);

		// P0-2 跨文档拦截：快照和当前文档不是同一个零件时，diff 数字全部失真
		//（实测锥管快照拿到 6 面新件上 diff 静默报 removed=116，像"模型被删了"，其实只是换文档）。
		bool comparable = IsSameDocument(before, after);
		if (!comparable)
		{
			warnings.Add("快照来自另一个文档（快照: " + DescribeDoc(before) + "，当前: " + DescribeDoc(after) +
				"），下面的 added/removed 数字是跨文档对比，没有意义。");
		}

		TopoDiffResult d = Compare(before, after, tol, cap);

		var result = new Dictionary<string, object>
		{
			["status"] = "ok",
			["action"] = "diff",
			["name"] = name,
			["savedAt"] = before.time,
			["document"] = new { before = before.document, after = after.document },
			["comparable"] = comparable,
			["identical"] = d.identical,
			["summary"] = new
			{
				facesBefore = d.facesBefore,
				facesAfter = d.facesAfter,
				facesAdded = d.totalAdded,
				facesRemoved = d.totalRemoved,
				facesChanged = d.totalChanged,
				featuresAdded = d.featuresAdded.Count,
				featuresRemoved = d.featuresRemoved.Count,
				featuresChanged = d.featuresChanged.Count
			},
			["models"] = d.modelDiffs,
			["features"] = new { added = d.featuresAdded, removed = d.featuresRemoved, changed = d.featuresChanged },
			["notes"] = new[]
			{
				"面按 Face.ID 配对：ID 不变=同一个面；面被完全覆盖会表现为 removed+added（真实几何变化，不是 bug）。",
				"changed 只报超过面积阈值(" + tol + "%)的面积变化，或类型/位置变化。",
				"identical=true = 面集合、面积、特征状态全部一致。"
			},
			["warnings"] = warnings.Count > 0 ? warnings : null
		};
		if (!comparable)
		{
			result["hint"] = "先在当前文档上 action=save 存新快照再 diff；或确认你要对比的就是 " + DescribeDoc(before) + " 并重新打开它。";
		}
		else if (!d.identical && d.totalAdded == 0 && d.totalRemoved == 0 && d.totalChanged == 0
			&& (d.featuresAdded.Count + d.featuresRemoved.Count + d.featuresChanged.Count) > 0)
		{
			result["hint"] = "面没变但特征变了——通常是参数调整（如深度/角度），几何量未跨过阈值。可调小 areaTolerancePct 再看。";
		}
		return JsonSerializer.Serialize(result);
	}

	/// <summary>
	/// 快照与当前文档是否同一个零件：优先比 FullName（未保存文档 FullName 为空时退回文档名）。
	/// 名字相同且都未保存（如同会话的"零件1"）视为同一文档——已是能拿到的最强证据。
	/// </summary>
	internal static bool IsSameDocument(TopoSnap a, TopoSnap b)
	{
		if (!string.IsNullOrEmpty(a.documentPath) && !string.IsNullOrEmpty(b.documentPath))
			return string.Equals(a.documentPath, b.documentPath, StringComparison.OrdinalIgnoreCase);
		return string.Equals(a.document ?? "", b.document ?? "", StringComparison.OrdinalIgnoreCase);
	}

	private static string DescribeDoc(TopoSnap s)
	{
		return string.IsNullOrEmpty(s.documentPath)
			? (s.document ?? "(无名)")
			: (s.document + " [" + s.documentPath + "]");
	}

	/// <summary>Compare 的结果（纯数据，供 diff 工具与 se_model_build 的 topologyDiff 共用）。</summary>
	internal sealed class TopoDiffResult
	{
		public int facesBefore;
		public int facesAfter;
		public int totalAdded;
		public int totalRemoved;
		public int totalChanged;
		public List<object> modelDiffs = new List<object>();
		public List<object> featuresAdded = new List<object>();
		public List<object> featuresRemoved = new List<object>();
		public List<object> featuresChanged = new List<object>();
		/// <summary>配对 model 的体积增量合计（毫米³）；建前为空文档时=建后总体积。</summary>
		public double? volumeDeltaMm3;
		public bool identical;
	}

	/// <summary>
	/// 拓扑对比【纯函数】（P1 从 DiffCore 抽出）：不碰 COM、不碰文件，se_model_build 的
	/// topologyDiff 建前/建后对比也走这里，保证两处 diff 语义永远一致。
	/// </summary>
	internal static TopoDiffResult Compare(TopoSnap before, TopoSnap after, double tol, int cap)
	{
		var d = new TopoDiffResult();

		// 按 model index 配对（零件通常 1 个 Model；焊接件可能多个）
		var beforeByIdx = new Dictionary<int, TopoModelSnap>();
		foreach (TopoModelSnap m in before.models) beforeByIdx[m.index] = m;
		var afterByIdx = new Dictionary<int, TopoModelSnap>();
		foreach (TopoModelSnap m in after.models) afterByIdx[m.index] = m;

		var allIdx = new SortedSet<int>();
		foreach (int k in beforeByIdx.Keys) allIdx.Add(k);
		foreach (int k in afterByIdx.Keys) allIdx.Add(k);

		double volSum = 0;
		bool volKnown = false;

		foreach (int idx in allIdx)
		{
			TopoModelSnap mb, ma;
			beforeByIdx.TryGetValue(idx, out mb);
			afterByIdx.TryGetValue(idx, out ma);

			if (mb == null || ma == null)
			{
				// 建前空文档 → 建后有实体：全部面记 added，体积增量=建后体积（首次建模最常见的路径）
				int addedC = (ma != null) ? ma.faceCount : 0;
				int removedC = (mb != null) ? mb.faceCount : 0;
				d.totalAdded += addedC;
				d.totalRemoved += removedC;
				if (ma != null && ma.volumeMm3.HasValue) { volSum += ma.volumeMm3.Value; volKnown = true; }
				if (mb != null && mb.volumeMm3.HasValue) { volSum -= mb.volumeMm3.Value; volKnown = true; }
				var noteRow = new Dictionary<string, object>
				{
					["index"] = idx,
					["note"] = mb == null ? "新增的 Model（快照里没有）" : "Model 消失（快照里有、现在没有）"
				};
				if (addedC > 0) noteRow["addedCount"] = addedC;
				if (removedC > 0) noteRow["removedCount"] = removedC;
				d.modelDiffs.Add(noteRow);
				continue;
			}

			var bFaces = new Dictionary<int, TopoFaceSnap>();
			foreach (TopoFaceSnap f in mb.faces) bFaces[f.id] = f;
			var aFaces = new Dictionary<int, TopoFaceSnap>();
			foreach (TopoFaceSnap f in ma.faces) aFaces[f.id] = f;

			var added = new List<object>();
			var removed = new List<object>();
			var changed = new List<object>();
			int addedCount = 0, removedCount = 0, changedCount = 0;

			foreach (TopoFaceSnap f in ma.faces)
			{
				TopoFaceSnap old;
				if (!bFaces.TryGetValue(f.id, out old))
				{
					addedCount++;
					if (added.Count < cap) added.Add(FaceJson(f));
					continue;
				}
				string field = null;
				double? deltaPct = null;
				object oldVal = null, newVal = null;

				if (old.type != f.type)
				{
					field = "type"; oldVal = old.type; newVal = f.type;
				}
				else if (old.areaMm2.HasValue && f.areaMm2.HasValue)
				{
					double baseVal = Math.Abs(old.areaMm2.Value);
					if (baseVal > 1e-9)
					{
						double pct = (f.areaMm2.Value - old.areaMm2.Value) / baseVal * 100.0;
						if (Math.Abs(pct) > tol)
						{
							field = "areaMm2"; oldVal = old.areaMm2; newVal = f.areaMm2; deltaPct = Math.Round(pct, 3);
						}
					}
				}
				if (field == null && old.where != f.where)
				{
					field = "where"; oldVal = old.where; newVal = f.where;
				}

				if (field != null)
				{
					changedCount++;
					if (changed.Count < cap)
						changed.Add(new
						{
							id = f.id,
							type = f.type,
							field,
							before = oldVal,
							after = newVal,
							deltaPct
						});
				}
			}

			foreach (TopoFaceSnap f in mb.faces)
			{
				if (!aFaces.ContainsKey(f.id))
				{
					removedCount++;
					if (removed.Count < cap) removed.Add(FaceJson(f));
				}
			}
			d.totalAdded += addedCount;
			d.totalRemoved += removedCount;
			d.totalChanged += changedCount;

			double? volDelta = null;
			if (mb.volumeMm3.HasValue && ma.volumeMm3.HasValue)
			{
				volDelta = Math.Round(ma.volumeMm3.Value - mb.volumeMm3.Value, 3);
				volSum += volDelta.Value;
				volKnown = true;
			}

			var row = new Dictionary<string, object>
			{
				["index"] = idx,
				["faceCount"] = new { before = mb.faceCount, after = ma.faceCount, delta = ma.faceCount - mb.faceCount },
				["volumeMm3"] = new { before = mb.volumeMm3, after = ma.volumeMm3, delta = volDelta },
				["sizeMm"] = new { before = mb.sizeMm, after = ma.sizeMm },
				["addedCount"] = addedCount,
				["removedCount"] = removedCount,
				["changedCount"] = changedCount,
				["unchangedCount"] = aFaces.Count - addedCount - changedCount
			};
			if (added.Count > 0) row["added"] = added;
			if (removed.Count > 0) row["removed"] = removed;
			if (changed.Count > 0) row["changed"] = changed;
			if (added.Count < addedCount) row["addedTruncated"] = "只列前 " + cap + " 条（共 " + addedCount + " 条）";
			if (removed.Count < removedCount) row["removedTruncated"] = "只列前 " + cap + " 条（共 " + removedCount + " 条）";
			if (changed.Count < changedCount) row["changedTruncated"] = "只列前 " + cap + " 条（共 " + changedCount + " 条）";
			d.modelDiffs.Add(row);
		}

		// ---- 特征树对比（按 (model, name) 配对）----
		var fBefore = new Dictionary<string, TopoFeatureSnap>(StringComparer.Ordinal);
		foreach (TopoFeatureSnap f in before.features) fBefore[f.model + "|" + f.name] = f;
		var fAfter = new Dictionary<string, TopoFeatureSnap>(StringComparer.Ordinal);
		foreach (TopoFeatureSnap f in after.features) fAfter[f.model + "|" + f.name] = f;

		foreach (KeyValuePair<string, TopoFeatureSnap> kv in fAfter)
		{
			TopoFeatureSnap old;
			if (!fBefore.TryGetValue(kv.Key, out old))
			{
				if (d.featuresAdded.Count < cap) d.featuresAdded.Add(new { model = kv.Value.model, name = kv.Value.name, status = kv.Value.status });
			}
			else if (old.status != kv.Value.status)
			{
				if (d.featuresChanged.Count < cap)
					d.featuresChanged.Add(new { model = kv.Value.model, name = kv.Value.name, statusBefore = old.status, statusAfter = kv.Value.status });
			}
		}
		foreach (KeyValuePair<string, TopoFeatureSnap> kv in fBefore)
		{
			if (!fAfter.ContainsKey(kv.Key) && d.featuresRemoved.Count < cap)
				d.featuresRemoved.Add(new { model = kv.Value.model, name = kv.Value.name, status = kv.Value.status });
		}

		d.facesBefore = before.models.Sum(x => x.faceCount);
		d.facesAfter = after.models.Sum(x => x.faceCount);
		if (volKnown) d.volumeDeltaMm3 = Math.Round(volSum, 3);
		d.identical = d.totalAdded == 0 && d.totalRemoved == 0 && d.totalChanged == 0
			&& d.featuresAdded.Count == 0 && d.featuresRemoved.Count == 0 && d.featuresChanged.Count == 0;
		return d;
	}

	// ---------------- 读取当前拓扑 ----------------

	private static TopoSnap Capture(SolidEdgeContext context, string objectId, List<string> warnings, out string error)
	{
		error = null;
		object doc;
		if (!string.IsNullOrWhiteSpace(objectId))
		{
			ObjectHandle h = context.GetHandle(objectId);
			if (h == null || h.ComObject == null)
			{
				error = "找不到对象编号 " + objectId + "。请先 se_get_selection，或省略 objectId 用活动文档。";
				return null;
			}
			doc = h.ComObject;
		}
		else
		{
			doc = Get(context.GetApplication(), "ActiveDocument");
			if (doc == null) { error = "没有活动文档。"; return null; }
		}
		return CaptureDoc(doc, warnings, out error);
	}

	/// <summary>
	/// 从文档 COM 对象读当前拓扑（P1 抽出：se_model_build 的 topologyDiff 建前/建后捕获也走这里，
	/// 与快照工具同一套读取语义）。只读，不落盘。
	/// </summary>
	internal static TopoSnap CaptureDoc(object doc, List<string> warnings, out string error)
	{
		error = null;
		object models = Get(doc, "Models");
		if (models == null) { error = "当前文档不是零件（取不到 Models）。只支持 .par。"; return null; }
		int modelCount = Count(models);
		if (modelCount == 0)
		{
			error = "零件里没有实体 Model：可能是空文档，也可能只含曲面/构造体（曲面体不进 Models）。";
			return null;
		}

		var snap = new TopoSnap
		{
			document = Convert.ToString(Get(doc, "Name"), CultureInfo.InvariantCulture),
			documentPath = SafeString(Get(doc, "FullName"))
		};

		for (int mi = 1; mi <= modelCount; mi++)
		{
			object model = Item(models, mi);
			if (model == null) continue;
			object body = Get(model, "Body");
			if (body == null)
			{
				warnings.Add("model[" + mi + "] 没有 Body（空模型/曲面体），已跳过。");
				continue;
			}

			object faces = null;
			string src;
			if (!ModelStateTools.TryGetAllFaces(body, out faces, out src))
				warnings.Add("model[" + mi + "] 面集合读不到。");

			List<ModelStateTools.FaceRow> rows = ModelStateTools.ReadFaces(faces, true, warnings, mi);

			var ms = new TopoModelSnap
			{
				index = mi,
				shellCount = TryShellCount(body),
				faceCount = rows.Count
			};
			double? vol = TryDouble(body, "Volume");
			if (vol.HasValue) ms.volumeMm3 = Math.Round(vol.Value * 1e9, 3);
			double[] box = ModelStateTools.TryBodyRange(body);
			if (box != null)
			{
				ms.sizeMm = new[]
				{
					Math.Round((box[3] - box[0]) * 1000.0, 3),
					Math.Round((box[4] - box[1]) * 1000.0, 3),
					Math.Round((box[5] - box[2]) * 1000.0, 3)
				};
				// P0-4：与 se_read_model_state 字段统一——两个工具都能同时拿到 rangeBoxMm(绝对定位)与 sizeMm(跨度)。
				ms.rangeBoxMm = new[]
				{
					Math.Round(box[0] * 1000.0, 3), Math.Round(box[1] * 1000.0, 3), Math.Round(box[2] * 1000.0, 3),
					Math.Round(box[3] * 1000.0, 3), Math.Round(box[4] * 1000.0, 3), Math.Round(box[5] * 1000.0, 3)
				};
			}

			foreach (ModelStateTools.FaceRow r in rows)
			{
				var fs = new TopoFaceSnap
				{
					id = r.Id,
					type = r.Type,
					edges = r.Edges,
					loops = r.Loops
				};
				if (r.AreaM2.HasValue) fs.areaMm2 = Math.Round(r.AreaM2.Value * 1e6, 3);
				fs.where = DescribeWhere(r);
				ms.faces.Add(fs);
			}
			snap.models.Add(ms);

			// 特征树
			object feats = Get(model, "Features");
			if (feats != null)
			{
				int fn = Count(feats);
				for (int i = 1; i <= fn; i++)
				{
					object ft = Item(feats, i);
					if (ft == null) continue;
					snap.features.Add(new TopoFeatureSnap
					{
						model = mi,
						name = Convert.ToString(Get(ft, "Name"), CultureInfo.InvariantCulture),
						status = SafeNullableLong(Get(ft, "Status"))
					});
				}
			}
		}
		return snap;
	}

	/// <summary>
	/// 面的"位置描述"：diff 里要能一眼看懂"新增的是不是一个 Ø28 的孔"。
	/// 平面→外法向+位置；圆柱→轴向+直径+孔/轴颈；锥/环→类型+半径。
	/// </summary>
	private static string DescribeWhere(ModelStateTools.FaceRow r)
	{
		if (!r.HasRange) return r.Type;
		if (r.Type == "Plane" && r.OutwardNormal != null)
		{
			double[] a = { Math.Abs(r.OutwardNormal[0]), Math.Abs(r.OutwardNormal[1]), Math.Abs(r.OutwardNormal[2]) };
			int axis = 0;
			for (int k = 1; k < 3; k++) if (a[k] > a[axis]) axis = k;
			if (a[axis] >= 0.9999)
			{
				string sign = r.OutwardNormal[axis] >= 0 ? "+" : "-";
				double pos = (r.Lo[axis] + r.Hi[axis]) / 2.0 * 1000.0;
				return "plane " + sign + "XYZ"[axis] + "@" + pos.ToString("0.###", CultureInfo.InvariantCulture) + "mm";
			}
			return "plane oblique";
		}
		if (r.Type == "Cylinder" && r.RadiusM.HasValue)
		{
			string ax = "?";
			if (r.AxisVector != null)
			{
				double[] a = { Math.Abs(r.AxisVector[0]), Math.Abs(r.AxisVector[1]), Math.Abs(r.AxisVector[2]) };
				int k = 0;
				for (int i = 1; i < 3; i++) if (a[i] > a[k]) k = i;
				if (a[k] >= 0.9999) ax = Convert.ToString("XYZ"[k]);
			}
			string kind = !r.IsBoss.HasValue ? "unknown" : (r.IsBoss.Value ? "boss" : "hole");
			return "cyl " + ax + " Ø" + (r.RadiusM.Value * 2000.0).ToString("0.###", CultureInfo.InvariantCulture) + "mm " + kind;
		}
		if ((r.Type == "Cone" || r.Type == "Torus") && r.RadiusM.HasValue)
		{
			double d = (r.Type == "Torus" && r.MajorRadiusM.HasValue ? r.MajorRadiusM.Value : r.RadiusM.Value) * 1000.0;
			return r.Type.ToLowerInvariant() + " R" + d.ToString("0.###", CultureInfo.InvariantCulture) + "mm";
		}
		return r.Type;
	}

	private static object FaceJson(TopoFaceSnap f)
	{
		var d = new Dictionary<string, object>
		{
			["id"] = f.id,
			["type"] = f.type,
			["where"] = f.where
		};
		if (f.areaMm2.HasValue) d["areaMm2"] = f.areaMm2.Value;
		if (f.edges >= 0) d["edges"] = f.edges;
		if (f.loops >= 0) d["loops"] = f.loops;
		return d;
	}

	// ---------------- list / drop ----------------

	private static string ListSnapshots()
	{
		string dir = SnapDir();
		var list = new List<object>();
		if (Directory.Exists(dir))
		{
			foreach (string f in Directory.GetFiles(dir, "*.json"))
			{
				try
				{
					TopoSnap s = JsonSerializer.Deserialize<TopoSnap>(File.ReadAllText(f));
					if (s == null) continue;
					int faces = 0;
					foreach (TopoModelSnap m in s.models) faces += m.faceCount;
					list.Add(new
					{
						name = s.name,
						time = s.time,
						document = s.document,
						modelCount = s.models.Count,
						faceCount = faces,
						featureCount = s.features.Count
					});
				}
				catch
				{
				}
			}
		}
		list.Sort((a, b) => string.CompareOrdinal(
			JsonSerializer.Serialize(a), JsonSerializer.Serialize(b)));
		return JsonSerializer.Serialize(new
		{
			status = "ok",
			action = "list",
			dir,
			count = list.Count,
			snapshots = list
		});
	}

	private static string DropSnapshot(string name)
	{
		string file = FileOf(name);
		if (!File.Exists(file))
			return Error("快照 \"" + name + "\" 不存在（文件: " + file + "）。用 action=list 看已有快照。");
		try
		{
			File.Delete(file);
		}
		catch (Exception ex)
		{
			return Error("删快照失败: " + ex.Message);
		}
		return JsonSerializer.Serialize(new { status = "ok", action = "drop", name, file, deleted = true });
	}

	/// <summary>
	/// 载入快照。失败原因分两种（P0-5：此前解析失败也报"找不到快照"，文件明明存在，误导排查方向）：
	/// 文件不存在 → "不存在"；文件在但 JSON 坏了 → "损坏，建议 drop 重存"。
	/// </summary>
	private static TopoSnap LoadSnapshot(string name, out string error)
	{
		string file = FileOf(name);
		error = null;
		if (!File.Exists(file))
		{
			error = "快照 \"" + name + "\" 不存在（文件: " + file + "）。先调 action=save 存一个。";
			return null;
		}
		try
		{
			TopoSnap s = JsonSerializer.Deserialize<TopoSnap>(File.ReadAllText(file));
			if (s == null)
			{
				error = "快照文件 \"" + file + "\" 解析结果为空（内容异常）。建议 action=drop 删掉后重新 save。";
				return null;
			}
			return s;
		}
		catch (Exception ex)
		{
			error = "快照文件 \"" + file + "\" 损坏（解析失败: " + ex.GetType().Name +
				"）。建议 action=drop 删掉后重新 save。";
			return null;
		}
	}

	// ---------------- 数据结构 ----------------

	internal sealed class TopoSnap
	{
		public string name { get; set; }
		public string time { get; set; }
		public string document { get; set; }
		public string documentPath { get; set; }
		public List<TopoModelSnap> models { get; set; } = new List<TopoModelSnap>();
		public List<TopoFeatureSnap> features { get; set; } = new List<TopoFeatureSnap>();
	}

	internal sealed class TopoModelSnap
	{
		public int index { get; set; }
		public int shellCount { get; set; }
		public int faceCount { get; set; }
		public double? volumeMm3 { get; set; }
		public double[] sizeMm { get; set; }
		/// <summary>绝对包围盒 [x0,y0,z0,x1,y1,z1] mm（P0-4 补，与 se_read_model_state 统一）。</summary>
		public double[] rangeBoxMm { get; set; }
		public List<TopoFaceSnap> faces { get; set; } = new List<TopoFaceSnap>();
	}

	internal sealed class TopoFaceSnap
	{
		public int id { get; set; }
		public string type { get; set; }
		public double? areaMm2 { get; set; }
		public string where { get; set; }
		public int edges { get; set; } = -1;
		public int loops { get; set; } = -1;
	}

	internal sealed class TopoFeatureSnap
	{
		public int model { get; set; }
		public string name { get; set; }
		public long? status { get; set; }
	}

	// ---------------- 杂项 ----------------

	private static string SnapDir()
	{
		string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		return Path.Combine(root, "SolidEdgeSpy", "topology");
	}

	private static string FileOf(string name)
	{
		return Path.Combine(SnapDir(), name + ".json");
	}

	private static bool IsSafeName(string s)
	{
		if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
		foreach (char c in s)
		{
			if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.') return false;
		}
		return true;
	}

	private static JsonSerializerOptions JsonOpts()
	{
		return new JsonSerializerOptions
		{
			WriteIndented = false,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};
	}

	private static object Get(object obj, string member, params object[] args)
	{
		if (obj == null) return null;
		object v;
		Exception err;
		if (ManualInvoke.TryInvoke(obj, member, (args == null || args.Length == 0) ? null : args, out v, out err))
			return v;
		try
		{
			return obj.GetType().InvokeMember(member, BindingFlags.GetProperty, null, obj, args);
		}
		catch
		{
			return null;
		}
	}

	private static object Item(object collection, int index1Based)
	{
		return Get(collection, "Item", index1Based);
	}

	private static int Count(object collection)
	{
		object c = Get(collection, "Count");
		if (c == null) return 0;
		try { return Convert.ToInt32(c, CultureInfo.InvariantCulture); }
		catch { return 0; }
	}

	private static int TryShellCount(object body)
	{
		object shells = Get(body, "Shells");
		return shells == null ? 0 : Count(shells);
	}

	private static double? TryDouble(object obj, string member)
	{
		object v = Get(obj, member);
		if (v == null) return null;
		try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
		catch { return null; }
	}

	private static long? SafeNullableLong(object v)
	{
		if (v == null) return null;
		try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
		catch { return null; }
	}

	private static string SafeString(object v)
	{
		try { return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture); }
		catch { return null; }
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new { status = "error", message });
	}
}
