using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;
using SolidEdgeAssembly;
using SolidEdgeFrameworkSupport;
using SolidEdgePart;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_find_objects —— 补上"遍历集合找目标对象"这个能力空缺。
///
/// 为什么需要它（2026-10-25 用户复盘时定的 P0-1）：
/// - 建模/改标注时最耗往返的动作是"找哪条线是外母线、哪个面是端面、哪个标注叫 Flange_OD"；
/// - 以前 MCP 只能逐个 describe 去猜，实际都退化成写 C# 脚本循环读坐标
///   （本次 spool 里为找母线写了 3 个脚本）；
/// - 有了按条件查对象，脚本就能省掉。
///
/// 设计要点：
/// - faces 复用 ModelStateTools.ReadFaces（唯一的拓扑读取链，**不另写一套**），
///   因此返回的 index 与 Body.Faces 的 0-based 枚举序一致 —— 可直接喂给
///   se_assembly_build 的 face1/face2（装配约束按这个序号绑面）。
/// - lines / dimensions 按 ProfileSets → Profiles → Lines2d/Dimensions 遍历；
/// - 过滤条件之间是 AND；匹配不上返回空数组 + 扫了多少个，不猜。
/// - 所有长度值 API 内部是米，对外统一 mm 且 Round(3)（与 se_read_model_state 同规范，
///   避免同一模型两次调用末位漂移导致对比失效）。
/// </summary>
[McpServerToolType]
public static class FindObjectTools
{
	private const double MmPerMeter = 1000.0;

	[McpServerTool]
	[Description("按条件在模型里查对象,返回句柄(obj-N)与 0-based 索引,替代'写脚本循环遍历找目标对象'。" +
		"collection=faces(默认)可按 type(plane/cylinder/cone/torus)/diaMm/normal(平面=外法向,圆柱=轴向,平行即命中)/" +
		"atMm(包围盒中心)/lenMm(轴向长度)过滤;返回的 index 与 Body.Faces 枚举序一致,可直接当 se_assembly_build 的 face1/face2。" +
		"collection=lines 按 type=h/v、lenMm、atMm、normal、nameContains;collection=dimensions 按 nameContains/atMm,回传 valueMm;" +
		"collection=occurrences 按 nameContains,回传 positionMm。多条件 AND;无匹配时 matches=[] 并回传 scanned。长度单位 mm,容差默认 0.05mm。\n" +
			"分工:本工具=条件过滤+拿索引;要全局面概览/孔统计/特征树用 se_read_model_state。")] 
	public static string se_find_objects(
		SolidEdgeContext context,
		[Description("集合类型: faces(默认) | lines | dimensions | occurrences")] string collection = "faces",
		[Description("起始对象句柄 obj-N(文档)。省略=当前活动文档")] string objectId = null,
		[Description("名字包含(不区分大小写): dimensions 用 DisplayName、occurrences 用实例名")] string nameContains = null,
		[Description("类型过滤: faces 用 plane/cylinder/cone/torus/other; lines 用 h(水平)/v(竖直)")] string type = null,
		[Description("圆直径(mm),只对 faces 的圆柱面生效;配合 tolMm")] double diaMm = 0,
		[Description("位置核对(mm),格式 \"x,y,z\";faces 比范围中心、lines 比中点、occurrences 比原点")] string atMm = null,
		[Description("方向/外法向: \"+Z\"/\"-Z\"/\"+X\" 或 \"0,0,1\";faces 的平面面用外法向、圆柱面用轴向(平行即命中,不分正负)、lines 用线方向")] string normal = null,
		[Description("长度核对(mm): lines=线长、faces=圆柱轴向长度(按真实轴向投影算,薄法兰不会被直径带偏)")] double lenMm = 0,
		[Description("直径/位置/长度的容差(mm),默认 0.05")] double tolMm = 0.05,
		[Description("最多返回条数,默认 20")] int limit = 20)
	{
		try
		{
			return context.Invoke(delegate
			{
				string err;
				object doc = ResolveDocument(context, objectId, out err);
				if (doc == null)
				{
					return Error(err);
				}

				string col = (collection ?? "faces").Trim().ToLowerInvariant();
				double[] wantAt = ParseVector(atMm, false);
				double[] wantNormal = ParseVector(normal, true);
				if (atMm != null && wantAt == null)
				{
					return Error("atMm 格式应为 \"x,y,z\"(mm),收到: " + atMm);
				}
				if (normal != null && wantNormal == null)
				{
					return Error("normal 格式应为 \"+Z\"/\"-X\"/\"0,0,1\",收到: " + normal);
				}
				if (tolMm <= 0) tolMm = 0.05;
				if (limit <= 0) limit = 20;

				var matches = new List<object>();
				var warnings = new List<string>();
				int scanned = 0;
				bool truncated = false;

				if (col == "faces")
				{
					var faceSel = new FaceSelector
					{
						Type = type,
						DiaMm = diaMm,
						AtMm = wantAt,
						Normal = wantNormal,
						LenMm = lenMm,
						TolMm = tolMm
					};
					MatchFaces(context, doc, faceSel, limit, matches, warnings, ref scanned, ref truncated);
				}
				else if (col == "lines")
				{
					MatchLines(context, doc, nameContains, type, wantAt, wantNormal, lenMm, tolMm, limit,
						matches, warnings, ref scanned, ref truncated);
				}
				else if (col == "dimensions")
				{
					MatchDimensions(context, doc, nameContains, wantAt, tolMm, limit,
						matches, warnings, ref scanned, ref truncated);
				}
				else if (col == "occurrences")
				{
					MatchOccurrences(context, doc, nameContains, limit, matches, warnings, ref scanned, ref truncated);
				}
				else
				{
					return Error("collection 只支持 faces / lines / dimensions / occurrences,收到: " + collection);
				}

				return JsonSerializer.Serialize(new
				{
					status = "ok",
					collection = col,
					scanned = scanned,
					matched = matches.Count,
					truncated = truncated,
					matches = matches,
					warnings = (warnings.Count > 0) ? warnings : null,
					note = BuildNote(col, matches.Count, scanned)
				});
			});
		}
		catch (Exception ex)
		{
			return Error("查找失败: " + DescribeException(ex));
		}
	}

	// ==================== faces ====================

	/// <summary>
	/// 面匹配走 FaceSelector / FaceFinder —— 与 se_assembly_build 的 face1/face2 描述符是**同一份代码**,
	/// 所以"diaMm/normal/atMm/lenMm"在两个工具里含义绝不会漂移(2026-10-25 P1 抽出)。
	/// </summary>
	private static void MatchFaces(SolidEdgeContext context, object doc, FaceSelector sel, int limit,
		List<object> matches, List<string> warnings, ref int scanned, ref bool truncated)
	{
		object models = ModelStateTools.Get(doc, "Models");
		int modelCount = ModelStateTools.Count(models);
		if (modelCount == 0)
		{
			warnings.Add("文档里没有实体 Model(空文档或纯曲面体),faces 读不到。");
			return;
		}
		if (modelCount > 1)
		{
			warnings.Add("文档里有 " + modelCount + " 个 Model,index 只在单个 Model 时与 Body.Faces 序号一一对应。");
		}

		for (int mi = 1; mi <= modelCount; mi++)
		{
			object model = ModelStateTools.Item(models, mi);
			object body = ModelStateTools.Get(model, "Body");
			if (body == null) continue;

			string err;
			List<ModelStateTools.FaceRow> rows = FaceFinder.ReadAllFaces(body, out err);
			if (err != null)
			{
				warnings.Add("model[" + mi + "] " + err);
				continue;
			}

			for (int k = 0; k < rows.Count; k++)
			{
				scanned++;
				ModelStateTools.FaceRow r = rows[k];
				FaceInfo fi;
				if (!sel.TryMatch(r, out fi)) continue;

				if (matches.Count >= limit) { truncated = true; break; }

				double[] n = fi.Normal;
				matches.Add(new
				{
					handle = context.AddHandle(r.ComFace, "Face", "Face " + r.Id),
					index = k,                 // 0-based,直接对应 Body.Faces 枚举序
					faceId = r.Id,
					modelIndex = mi,
					type = r.Type,
					diaMm = fi.DiaMm,
					centerMm = fi.CenterMm,
					normalMm = n == null ? null : new[] { Math.Round(n[0], 4), Math.Round(n[1], 4), Math.Round(n[2], 4) },
					boss = r.IsBoss,
					axialLengthMm = (sel.LenMm > 0) ? (double?)fi.AxialLengthMm : null,
					edges = (r.Edges >= 0) ? (int?)r.Edges : null,
					areaMm2 = r.AreaM2.HasValue ? (double?)Math.Round(r.AreaM2.Value * 1e6, 3) : null
				});
			}
			if (truncated) break;
		}
	}

	// ==================== lines ====================

	private static void MatchLines(SolidEdgeContext context, object doc, string nameContains, string type,
		double[] wantAt, double[] wantNormal, double lenMm, double tol, int limit,
		List<object> matches, List<string> warnings, ref int scanned, ref bool truncated)
	{
		object pss = ModelStateTools.Get(doc, "ProfileSets");
		int psCount = ModelStateTools.Count(pss);
		if (psCount == 0)
		{
			warnings.Add("文档里没有 ProfileSets(草图集合为空)。");
			return;
		}

		string wantKind = null;
		if (!string.IsNullOrWhiteSpace(type))
		{
			string t = type.Trim().ToLowerInvariant();
			if (t == "h" || t == "horizontal" || t == "水平") wantKind = "h";
			else if (t == "v" || t == "vertical" || t == "竖直") wantKind = "v";
			else warnings.Add("lines 的 type 只支持 h/v,已忽略: " + type);
		}

		for (int s = 1; s <= psCount; s++)
		{
			object ps = ModelStateTools.Item(pss, s);
			object profs = ModelStateTools.Get(ps, "Profiles");
			int pCount = ModelStateTools.Count(profs);
			for (int p = 1; p <= pCount; p++)
			{
				object prof = ModelStateTools.Item(profs, p);
				object lines = ModelStateTools.Get(prof, "Lines2d");
				int lCount = ModelStateTools.Count(lines);
				for (int i = 1; i <= lCount; i++)
				{
					object ln = ModelStateTools.Item(lines, i);
					if (ln == null) continue;
					scanned++;

					double x1, y1, x2, y2;
					if (!TryLinePoints(ln, out x1, out y1, out x2, out y2))
					{
						if (warnings.Count < 6) warnings.Add("profileSet[" + s + "]/profile[" + p + "] 第 " + i + " 条线读不到端点,已跳过。");
						continue;
					}

					double dx = x2 - x1, dy = y2 - y1;
					double len = Math.Sqrt(dx * dx + dy * dy) * MmPerMeter;
					bool isH = Math.Abs(dy) < 1e-9;
					bool isV = Math.Abs(dx) < 1e-9;
					if (wantKind == "h" && !isH) continue;
					if (wantKind == "v" && !isV) continue;
					if (lenMm > 0 && Math.Abs(len - lenMm) > tol) continue;

					double[] mid = new double[]
					{
						Math.Round((x1 + x2) / 2.0 * MmPerMeter, 3),
						Math.Round((y1 + y2) / 2.0 * MmPerMeter, 3),
						0
					};
					if (wantAt != null && !Near(wantAt, mid, tol)) continue;

					double[] dir = new double[] { dx, dy, 0 };
					double dl = Math.Sqrt(dx * dx + dy * dy);
					if (dl > 1e-12) { dir[0] /= dl; dir[1] /= dl; }
					if (wantNormal != null && !SameDirection(dir, wantNormal) && !SameDirection(dir, Negate(wantNormal))) continue;

					string name = null;
					try { name = ln is Line2d ? ((Line2d)ln).Name : null; } catch { }
					if (nameContains != null && (name == null || name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)) continue;

					if (matches.Count >= limit) { truncated = true; break; }

					matches.Add(new
					{
						handle = context.AddHandle(ln, "Line2d", name),
						index = i - 1,             // 0-based 在 Lines2d 集合里的序号
						profileSet = s,
						profile = p,
						name = name,
						lengthMm = Math.Round(len, 3),
						kind = isH ? "h" : (isV ? "v" : "oblique"),
						startMm = new[] { R(x1), R(y1), 0d },
						endMm = new[] { R(x2), R(y2), 0d },
						midMm = mid
					});
				}
				if (truncated) break;
			}
			if (truncated) break;
		}
	}

	/// <summary>线端点:优先 PIA 强类型 GetStartPoint/GetEndPoint(ref double, ref double),失败返回 false。</summary>
	private static bool TryLinePoints(object line, out double x1, out double y1, out double x2, out double y2)
	{
		x1 = y1 = x2 = y2 = 0;
		Line2d l2 = line as Line2d;
		if (l2 == null) return false;
		try
		{
			l2.GetStartPoint(out x1, out y1);
			l2.GetEndPoint(out x2, out y2);
			return true;
		}
		catch
		{
			return false;
		}
	}

	// ==================== dimensions ====================

	private static void MatchDimensions(SolidEdgeContext context, object doc, string nameContains,
		double[] wantAt, double tol, int limit,
		List<object> matches, List<string> warnings, ref int scanned, ref bool truncated)
	{
		object pss = ModelStateTools.Get(doc, "ProfileSets");
		int psCount = ModelStateTools.Count(pss);
		if (psCount == 0)
		{
			warnings.Add("文档里没有 ProfileSets(草图集合为空)。");
			return;
		}

		for (int s = 1; s <= psCount; s++)
		{
			object ps = ModelStateTools.Item(pss, s);
			object profs = ModelStateTools.Get(ps, "Profiles");
			int pCount = ModelStateTools.Count(profs);
			for (int p = 1; p <= pCount; p++)
			{
				object prof = ModelStateTools.Item(profs, p);
				object dims = ModelStateTools.Get(prof, "Dimensions");
				int dCount = ModelStateTools.Count(dims);
				for (int i = 1; i <= dCount; i++)
				{
					object dm = ModelStateTools.Item(dims, i);
					if (dm == null) continue;
					scanned++;

					Dimension d = dm as Dimension;
					if (d == null)
					{
						if (warnings.Count < 6) warnings.Add("profileSet[" + s + "]/profile[" + p + "] 第 " + i + " 个标注不是 Dimension,已跳过。");
						continue;
					}

					string display = null;
					double valueM = 0;
					int dimType = -1;
					try { display = d.DisplayName; } catch { }
					try { valueM = d.Value; } catch { }
					try { dimType = (int)d.DimensionType; } catch { }

					if (nameContains != null && (display == null || display.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)) continue;

					// 标注位置:Dimension 没有稳定的"位置"成员(坐标散在投影线/关键点里,取法因类型而异),
					// 读不准就不猜 —— 有 atMm 时给出警告并忽略该条件,而不是把所有结果过滤掉。
					double[] posMm = null;
					if (wantAt != null && warnings.Count < 6)
					{
						warnings.Add("dimensions 暂不支持 atMm 过滤(标注位置读取不稳定),该条件已忽略。");
					}

					if (matches.Count >= limit) { truncated = true; break; }

					matches.Add(new
					{
						handle = context.AddHandle(dm, "Dimension", display),
						index = i - 1,
						profileSet = s,
						profile = p,
						displayName = display,
						valueMm = Math.Round(valueM * MmPerMeter, 3),
						dimensionType = dimType,
						positionMm = posMm
					});
				}
				if (truncated) break;
			}
			if (truncated) break;
		}
	}

	// 标注位置没有通用读法:见 MatchDimensions 里的说明(atMm 对 dimensions 直接忽略并警告)。

	// ==================== occurrences ====================

	private static void MatchOccurrences(SolidEdgeContext context, object doc, string nameContains, int limit,
		List<object> matches, List<string> warnings, ref int scanned, ref bool truncated)
	{
		object occs = ModelStateTools.Get(doc, "Occurrences");
		int n = ModelStateTools.Count(occs);
		if (n == 0)
		{
			warnings.Add("当前文档不是装配,或装配里没有零件实例。");
			return;
		}

		for (int i = 1; i <= n; i++)
		{
			object occ = ModelStateTools.Item(occs, i);
			if (occ == null) continue;
			scanned++;

			string name = null;
			try { name = Convert.ToString(ModelStateTools.Get(occ, "Name"), CultureInfo.InvariantCulture); } catch { }
			if (nameContains != null && (name == null || name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)) continue;

			if (matches.Count >= limit) { truncated = true; break; }

			matches.Add(new
			{
				handle = context.AddHandle(occ, "Occurrence", name),
				index = i - 1,
				name = name,
				positionMm = TryOccurrencePositionMm(occ)
			});
		}
	}

	private static double[] TryOccurrencePositionMm(object occ)
	{
		try
		{
			Occurrence o = occ as Occurrence;
			if (o == null) return null;
			Array m = Array.CreateInstance(typeof(double), 0);
			o.GetMatrix(ref m);
			if (m == null || m.Length < 16) return null;
			return new[]
			{
				Math.Round(Convert.ToDouble(m.GetValue(12), CultureInfo.InvariantCulture) * MmPerMeter, 3),
				Math.Round(Convert.ToDouble(m.GetValue(13), CultureInfo.InvariantCulture) * MmPerMeter, 3),
				Math.Round(Convert.ToDouble(m.GetValue(14), CultureInfo.InvariantCulture) * MmPerMeter, 3)
			};
		}
		catch
		{
			return null;
		}
	}

	// ==================== 公共小工具 ====================

	private static object ResolveDocument(SolidEdgeContext context, string objectId, out string err)
	{
		err = null;
		if (!string.IsNullOrWhiteSpace(objectId))
		{
			ObjectHandle h = context.GetHandle(objectId);
			if (h == null || h.ComObject == null)
			{
				err = "找不到句柄 " + objectId + ",请先用 se_get_document / se_get_selection 取到文档句柄。";
				return null;
			}
			return h.ComObject;
		}
		try
		{
			object doc = context.GetApplication().ActiveDocument;
			if (doc == null) err = "没有活动文档,先打开一个零件或装配。";
			return doc;
		}
		catch (Exception ex)
		{
			err = "取活动文档失败: " + ex.Message;
			return null;
		}
	}

	/// <summary>解析 "x,y,z" 或 "+Z"/"-X"/"0,0,1" —— 直接复用 FaceSelector 的解析(同一套写法,不留第二份)。</summary>
	private static double[] ParseVector(string text, bool allowDirection)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;
		return allowDirection ? FaceSelector.ParseDirection(text) : FaceSelector.ParsePointMm(text);
	}

	private static bool Near(double[] a, double[] b, double tol)
	{
		return FaceSelector.Near(a, b, tol);
	}

	private static bool SameDirection(double[] a, double[] b)
	{
		if (a == null || b == null || a.Length < 3 || b.Length < 3) return false;
		return FaceSelector.Dot(a, b) > 0.999;
	}

	private static double[] Negate(double[] v)
	{
		return new[] { -v[0], -v[1], -v[2] };
	}

	private static double R(double m)
	{
		return Math.Round(m * MmPerMeter, 3);
	}

	private static string BuildNote(string col, int matched, int scanned)
	{
		if (matched == 0)
		{
			return "扫了 " + scanned + " 个 " + col + ",没有符合条件的。放宽 type/diaMm/atMm/normal/lenMm 或加大 tolMm 再试。";
		}
		if (col == "faces")
		{
			return "index 是 Body.Faces 的 0-based 序号,可直接当 se_assembly_build 的 face1/face2。";
		}
		if (col == "dimensions")
		{
			return "拿到 handle 后可 se_invoke_member(member=\"Value\") 读值,或 se_get_variables 改名。";
		}
		return null;
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new { status = "error", message = message });
	}

	private static string DescribeException(Exception ex)
	{
		Exception e = ex;
		string s = null;
		while (e != null)
		{
			if (s != null) s += " <- ";
			s += e.Message;
			e = e.InnerException;
		}
		return s;
	}
}
