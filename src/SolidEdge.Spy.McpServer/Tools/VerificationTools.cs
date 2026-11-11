using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 模型尺寸自检:把"我以为建成了 Ø220/Ø116/8 孔"变成"从实体上量出来是 Ø220/Ø116/8 孔"。
///
/// 动机(2026-10-25 画法兰):建完只能自己拿 π(R²−r²)t 手算对体积,或者人肉读
/// se_read_model_state 的圆柱清单去数直径。体积对得上也可能不是你以为的那种几何
/// (例如孔数少一个、孔位半径错),所以需要一条"按设计意图逐项量"的通道。
///
/// 口径(写进工具描述,避免误判):
///   od      = 直径最大的凸圆柱(外圆)
///   bore    = 直径最小的凹圆柱(贯穿孔/内孔)
///   rfOD    = 排除 od 之后直径最大的凸圆柱(突面/凸台)
///   rfH     = 该突面圆柱的轴向长度
///   thickness = 沿 Z 的总厚度(±Z 平面最大间距,含凸台)
///   pcd     = 孔心到 Z 轴距离×2 的众数(螺栓分布圆)
///   holeDia = 孔直径的众数
///   holeCount = 按(直径+圆心)去重后的孔数
/// 测量不出的项一律标 undetermined 并说明原因,不猜值。
/// </summary>
[McpServerToolType]
public static class VerificationTools
{
	// 2026-11-07:MCP 工具面已并入 se_read_model_state(verify=expects),旧工具名
	// se_verify_model 由 ToolAliases 改写路由过去;本方法保留给 CLI --verify 与该转调,行为零漂移。
	[Description("模型尺寸自检:传入期望值,从实体上逐项量出实际值并判定通过/偏差。" +
		"expects 支持 od(外径)/bore(内孔)/thickness(沿Z总厚度,含凸台)/pcd(螺栓分布圆)/holeCount(孔数)/holeDia(孔径)/" +
		"rfOD(突面直径)/rfH(突面高度),单位 mm(holeCount 为整数);只校验给了的项。" +
		"口径:od=直径最大的凸圆柱;bore=直径最小的凹圆柱;rfOD=排除 od 后直径最大的凸圆柱;pcd=孔心到 Z 轴距离×2 的众数;" +
		"holeCount=按(直径+圆心)去重;thickness=±Z 平面最大间距。量不出的标 undetermined 并给原因,不猜值。" +
		"返回 checks[](expect/actual/deviation/pass)、extra(体积/包围盒/圆柱条数)与 verdict(ok/fail/partial)。" +
		"典型用法:建模后立刻自检,避免'体积对得上但孔数少一个'。")]
	public static string se_verify_model(
		SolidEdgeContext context,
		[Description("期望尺寸(JSON 对象),如 {\"od\":220,\"bore\":116,\"thickness\":25,\"pcd\":182,\"holeCount\":8,\"holeDia\":18}。只校验给出的项,单位 mm。")] JsonElement expects,
		[Description("允许偏差(毫米),默认 0.05")] double toleranceMm = 0.05,
		[Description("模型序号(默认 1)")] int modelIndex = 1)
	{
		try
		{
			if (expects.ValueKind != JsonValueKind.Object)
			{
				return Error("expects 必须是 JSON 对象,如 {\"od\":220,\"bore\":116,\"holeCount\":8}。");
			}

			return context.Invoke(delegate
			{
				object doc = null;
				try { doc = context.GetApplication().ActiveDocument; }
				catch { doc = null; }
				if (doc == null)
				{
					return Error("当前没有活动文档。");
				}

				object model = ModelMetrics.FreshModel(doc, modelIndex);
				if (model == null)
				{
					return Error("当前文档没有实体 Model(空零件或只有曲面体),无尺寸可量。");
				}

				ModelStateTools.VerifyMeasurement m = ModelStateTools.MeasureForVerify(model, modelIndex);
				if (m.Error != null)
				{
					return Error("测量失败: " + m.Error);
				}

				double tol = toleranceMm > 0 ? toleranceMm : 0.05;

				// ---------------- 从圆柱/平面提炼实际值 ----------------
				List<ModelStateTools.VerifyCylinder> boss = new List<ModelStateTools.VerifyCylinder>();
				List<ModelStateTools.VerifyCylinder> holes = new List<ModelStateTools.VerifyCylinder>();
				foreach (ModelStateTools.VerifyCylinder c in m.Cylinders)
				{
					if (c.Boss == true) boss.Add(c);
					else if (c.Boss == false) holes.Add(c);
				}

				ModelStateTools.VerifyCylinder odCyl = Biggest(boss);
				if (odCyl == null) odCyl = Biggest(m.Cylinders);

				ModelStateTools.VerifyCylinder rfCyl = null;
				foreach (ModelStateTools.VerifyCylinder c in boss)
				{
					if (odCyl != null && Math.Abs(c.DiameterMm - odCyl.DiameterMm) < 1e-6) continue;
					if (rfCyl == null || c.DiameterMm > rfCyl.DiameterMm) rfCyl = c;
				}

				// 凹圆柱要分两类,否则"最小凹圆柱"会量到螺栓孔、孔数会把内孔也算进去:
				//   同轴(圆心在 Z 轴上) = 内孔/贯通孔 → bore
				//   偏心(圆心在分布圆上) = 螺栓孔 → pcd/holeCount/holeDia
				// 2026-10-25 首测就是在这里把 Ø116 内孔算成了第 9 个孔、又把 bore 量成 Ø18。
				List<ModelStateTools.VerifyCylinder> coaxialHoles = new List<ModelStateTools.VerifyCylinder>();
				List<ModelStateTools.VerifyCylinder> offAxisHoles = new List<ModelStateTools.VerifyCylinder>();
				foreach (ModelStateTools.VerifyCylinder c in holes)
				{
					if (IsCoaxial(c)) coaxialHoles.Add(c);
					else offAxisHoles.Add(c);
				}

				ModelStateTools.VerifyCylinder boreCyl = Biggest(coaxialHoles);
				if (boreCyl == null) boreCyl = Smallest(holes);
				if (boreCyl == null) boreCyl = Smallest(m.Cylinders);

				double? thickness = null;
				double? topZ = null, bottomZ = null;
				List<double> zLevels = new List<double>();
				foreach (ModelStateTools.VerifyPlane p in m.Planes)
				{
					if (p.Normal == "+Z") { if (!topZ.HasValue || p.PositionMm > topZ.Value) topZ = p.PositionMm; zLevels.Add(p.PositionMm); }
					else if (p.Normal == "-Z") { if (!bottomZ.HasValue || p.PositionMm < bottomZ.Value) bottomZ = p.PositionMm; zLevels.Add(p.PositionMm); }
				}
				if (topZ.HasValue && bottomZ.HasValue) thickness = Math.Round(topZ.Value - bottomZ.Value, 3);

				// 孔位/孔径:按(直径+圆心)去重,再取 PCD / 孔径众数
				List<double> holeDias = new List<double>();
				List<double> pcds = new List<double>();
				HashSet<string> seenHoles = new HashSet<string>(StringComparer.Ordinal);
				foreach (ModelStateTools.VerifyCylinder c in offAxisHoles)
				{
					if (c.Axis != "Z" || c.CenterMm == null) continue;
					string key = c.DiameterMm.ToString("F2", CultureInfo.InvariantCulture) + "|"
								 + Math.Round(c.CenterMm[0], 2).ToString("F2", CultureInfo.InvariantCulture) + "|"
								 + Math.Round(c.CenterMm[1], 2).ToString("F2", CultureInfo.InvariantCulture);
					if (!seenHoles.Add(key)) continue;
					holeDias.Add(c.DiameterMm);
					pcds.Add(Math.Round(Math.Sqrt(c.CenterMm[0] * c.CenterMm[0] + c.CenterMm[1] * c.CenterMm[1]) * 2.0, 3));
				}
				int? holeCount = seenHoles.Count > 0 ? (int?)seenHoles.Count : null;
				double? holeDia = Mode(holeDias);
				double? pcd = Mode(pcds);

				// ---------------- 逐项判定 ----------------
				List<object> checks = new List<object>();
				double? expectOd, expectBore, expectThickness, expectPcd, expectHoleDia, expectRfOd, expectRfH;
				int? expectHoleCount;
				bool any = false;

				if (TryNum(expects, "od", out double v1)) { any = true; expectOd = v1; AddCheck(checks, "od", v1, odCyl != null ? (double?)odCyl.DiameterMm : null, tol, "直径最大的凸圆柱"); } else expectOd = null;
				if (TryNum(expects, "bore", out double v2)) { any = true; expectBore = v2; AddCheck(checks, "bore", v2, boreCyl != null ? (double?)boreCyl.DiameterMm : null, tol, "直径最小的凹圆柱"); } else expectBore = null;
				if (TryNum(expects, "thickness", out double v3)) { any = true; expectThickness = v3; AddCheck(checks, "thickness", v3, thickness, tol, "±Z 平面最大间距(总厚度,含凸台)"); } else expectThickness = null;
				if (TryNum(expects, "pcd", out double v4)) { any = true; expectPcd = v4; AddCheck(checks, "pcd", v4, pcd, tol, "孔心到 Z 轴距离×2 的众数"); } else expectPcd = null;
				if (TryNum(expects, "holeDia", out double v5)) { any = true; expectHoleDia = v5; AddCheck(checks, "holeDia", v5, holeDia, tol, "孔直径众数"); } else expectHoleDia = null;
				if (TryNum(expects, "rfOD", out double v6)) { any = true; expectRfOd = v6; AddCheck(checks, "rfOD", v6, rfCyl != null ? (double?)rfCyl.DiameterMm : null, tol, "排除外圆后直径最大的凸圆柱"); } else expectRfOd = null;
				if (TryNum(expects, "rfH", out double v7)) { any = true; expectRfH = v7; AddCheck(checks, "rfH", v7, rfCyl != null ? (double?)rfCyl.LengthMm : null, tol, "突面圆柱的轴向长度"); } else expectRfH = null;
				if (TryInt(expects, "holeCount", out int v8)) { any = true; expectHoleCount = v8; AddCheckInt(checks, "holeCount", v8, holeCount, "按(直径+圆心)去重后的孔数"); } else expectHoleCount = null;

				if (!any)
				{
					return Error("expects 里没有任何可校验的项。可用键:od / bore / thickness / pcd / holeCount / holeDia / rfOD / rfH(单位 mm)。");
				}

				int failCount = 0, undetermined = 0, passCount = 0;
				foreach (object c in checks)
				{
					string p = ReadProp(c, "pass");
					if (p == "False") failCount++;
					else if (p == "-") undetermined++;
					else passCount++;
				}
				string verdict = failCount > 0 ? "fail" : (undetermined > 0 ? "partial" : "ok");

				string conclusion;
				if (verdict == "ok") conclusion = "全部 " + passCount + " 项通过(偏差 ≤ " + tol + " mm)。";
				else if (verdict == "fail") conclusion = failCount + " 项不符,见 checks 里 pass=false 的行;若 actual 为 null 说明该尺寸在这件实体上量不到(口径见工具描述)。";
				else conclusion = passCount + " 项通过," + undetermined + " 项量不出(undetermined,不猜值);如需确认请用 se_read_model_state detail=summary 看原始圆柱/平面清单。";

				return JsonSerializer.Serialize(new
				{
					status = "ok",
					verdict,
					toleranceMm = tol,
					passCount,
					failCount,
					undeterminedCount = undetermined,
					checks,
					measured = new
					{
						odMm = odCyl != null ? (double?)odCyl.DiameterMm : null,
						boreMm = boreCyl != null ? (double?)boreCyl.DiameterMm : null,
						rfOdMm = rfCyl != null ? (double?)rfCyl.DiameterMm : null,
						rfHMm = rfCyl != null ? (double?)rfCyl.LengthMm : null,
						thicknessMm = thickness,
						pcdMm = pcd,
						holeDiaMm = holeDia,
						holeCount,
						zLevelsMm = zLevels
					},
					extra = new
					{
						volumeMm3 = m.VolumeMm3,
						sizeMm = m.SizeMm,
						cylinderFaces = m.Cylinders.Count,
						planeFaces = m.Planes.Count,
						warnings = m.Warnings.Count > 0 ? m.Warnings : null
					},
					conclusion
				});
			});
		}
		catch (Exception ex)
		{
			return Error("尺寸自检失败: " + ModelMetrics.DescribeException(ex));
		}
	}

	// ==================== 判定小工具 ====================

	private static void AddCheck(List<object> checks, string item, double expect, double? actual, double tol, string basis)
	{
		double? dev = actual.HasValue ? (double?)Math.Round(actual.Value - expect, 3) : null;
		bool? pass = actual.HasValue && Math.Abs(actual.Value - expect) <= tol ? (bool?)true : (actual.HasValue ? (bool?)false : null);
		checks.Add(new
		{
			item,
			expectMm = Math.Round(expect, 3),
			actualMm = actual.HasValue ? (double?)Math.Round(actual.Value, 3) : null,
			deviationMm = dev,
			pass,
			basis = actual.HasValue ? basis : basis + " —— 本件实体上量不到该尺寸(undetermined,不猜值)"
		});
	}

	private static void AddCheckInt(List<object> checks, string item, int expect, int? actual, string basis)
	{
		bool? pass = actual.HasValue ? (bool?)(actual.Value == expect) : null;
		checks.Add(new
		{
			item,
			expectMm = (double?)null,
			expect = (object)expect,
			actual = (object)actual,
			deviationMm = actual.HasValue ? (double?)null : null,
			deviation = actual.HasValue ? (int?)(actual.Value - expect) : null,
			pass,
			basis = actual.HasValue ? basis : basis + " —— 量不到孔(undetermined)"
		});
	}

	/// <summary>圆心是否落在 Z 轴上(0.5mm 容差)。同轴凹圆柱=内孔,偏心的=螺栓孔。</summary>
	private static bool IsCoaxial(ModelStateTools.VerifyCylinder c)
	{
		if (c == null || c.CenterMm == null) return true;
		double r = Math.Sqrt(c.CenterMm[0] * c.CenterMm[0] + c.CenterMm[1] * c.CenterMm[1]);
		return r <= 0.5;
	}

	private static ModelStateTools.VerifyCylinder Biggest(List<ModelStateTools.VerifyCylinder> list)
	{
		ModelStateTools.VerifyCylinder best = null;
		foreach (ModelStateTools.VerifyCylinder c in list)
		{
			if (best == null || c.DiameterMm > best.DiameterMm) best = c;
		}
		return best;
	}

	private static ModelStateTools.VerifyCylinder Smallest(List<ModelStateTools.VerifyCylinder> list)
	{
		ModelStateTools.VerifyCylinder best = null;
		foreach (ModelStateTools.VerifyCylinder c in list)
		{
			if (best == null || c.DiameterMm < best.DiameterMm) best = c;
		}
		return best;
	}

	/// <summary>众数(并列取较小值);空列表返回 null。</summary>
	private static double? Mode(List<double> values)
	{
		if (values == null || values.Count == 0) return null;
		var counts = new Dictionary<double, int>();
		foreach (double v in values)
		{
			double key = Math.Round(v, 2);
			int c;
			counts.TryGetValue(key, out c);
			counts[key] = c + 1;
		}
		double best = 0;
		int bestCount = -1;
		foreach (KeyValuePair<double, int> kv in counts)
		{
			if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key < best))
			{
				best = kv.Key;
				bestCount = kv.Value;
			}
		}
		return best;
	}

	private static bool TryNum(JsonElement el, string name, out double value)
	{
		value = 0;
		JsonElement found;
		if (!TryProp(el, name, out found)) return false;
		if (found.ValueKind != JsonValueKind.Number) return false;
		return found.TryGetDouble(out value);
	}

	private static bool TryInt(JsonElement el, string name, out int value)
	{
		value = 0;
		JsonElement found;
		if (!TryProp(el, name, out found)) return false;
		if (found.ValueKind != JsonValueKind.Number) return false;
		double d;
		if (!found.TryGetDouble(out d)) return false;
		value = (int)Math.Round(d);
		return true;
	}

	private static bool TryProp(JsonElement el, string name, out JsonElement value)
	{
		value = default(JsonElement);
		if (el.ValueKind != JsonValueKind.Object) return false;
		foreach (JsonProperty p in el.EnumerateObject())
		{
			if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				value = p.Value;
				return true;
			}
		}
		return false;
	}

	private static string ReadProp(object o, string name)
	{
		try
		{
			var p = o.GetType().GetProperty(name);
			if (p == null) return "?";
			object v = p.GetValue(o);
			return v == null ? "-" : Convert.ToString(v);
		}
		catch
		{
			return "?";
		}
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new { status = "error", message });
	}
}
