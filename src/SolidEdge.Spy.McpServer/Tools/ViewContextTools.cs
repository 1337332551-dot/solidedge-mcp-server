using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SolidEdgeFramework;
using SolidEdgeDraft;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_view_context:工程图"视图 ↔ 编辑上下文"映射判定 + 视图/图纸坐标换算。
///
/// 为什么需要它(实测返工最贵的一块):
/// - 视图编辑上下文(sheet 集合里那些"数字名"sheet,如 30987)与自己所属的 DrawingView(如 30861)
///   之间没有任何显式关联属性,必须靠试探判定;
/// - ★曾经用"上下文 Relations2d 里的 point-on 约束 obj1(孔圆)Key → 遍历各视图 DVCircles2d 反查"
///   来判定归属,结果把两遍中心线装反时 REL_FAIL 仍为 0(约束允许跨上下文建立)——判据是错的且静默出错;
/// - 权威判据只有一条:DrawingView.GetReferenceToGraphicMember(上下文里的对象, out Reference) 成功
///   ⇔ 该对象属于这个视图的编辑上下文(不属于时抛 0x80040225)。故这里做 2×2 交叉测试。
///
/// 坐标换算同理:标注建在图页坐标上,而视图里的几何是视图坐标,建标注前必须 ViewToSheet 换算,
/// 否则标注会"飘到图外"(实测把视图坐标当图纸坐标用,Range 直接落到 (10,788)mm)。
/// </summary>
[McpServerToolType]
public static class ViewContextTools
{
	[McpServerTool]
	[Description("工程图'视图 ↔ 编辑上下文'映射判定与坐标换算。action=map(默认):列出图页上每个 DrawingView 与每个'数字名 sheet'" +
		"(视图编辑上下文)的配对结果——判据是 DrawingView.GetReferenceToGraphicMember 成功(不属于时抛 0x80040225),这是唯一权威判据;" +
		"2D 约束(AddKeypoint/AddPointOn)可跨上下文建立,绝不能拿来判归属。action=convert:在视图坐标与图纸坐标之间换算" +
		"(ViewToSheet/SheetToView),建标注前必须先换算,否则标注会飘到图外。只读。")]
	public static string se_view_context(
		SolidEdgeContext context,
		[Description("动作:map=列出视图↔上下文映射(默认);convert=坐标换算")] string action = "map",
		[Description("map 可选:指定图页的 sheet 名(非数字名,如 \"Sheet1\";不支持 obj-N 句柄);省略=自动取第一个含 Dimensions 的非数字名 sheet")] string sheet = null,
		[Description("convert 必填:视图的 obj-N 或视图名(如 \"30861\")")] string view = null,
		[Description("convert 可选:换算方向 view=视图坐标→图纸坐标(默认,建标注用);sheet=图纸坐标→视图坐标")] string from = "view",
		[Description("convert 必填:x 坐标(内部单位米;UI 毫米 ÷1000)")] double? x = null,
		[Description("convert 必填:y 坐标(内部单位米;UI 毫米 ÷1000)")] double? y = null)
	{
		try
		{
			string act = string.IsNullOrWhiteSpace(action) ? "map" : action.Trim().ToLowerInvariant();
			if (act == "convert")
			{
				return context.Invoke(() => ConvertCore(context, view, from, x, y));
			}
			return context.Invoke(() => MapCore(context, sheet));
		}
		catch (Exception ex)
		{
			if (SolidEdgeContext.IsDisconnected(ex))
			{
				return Error("COM 对象已断连(文档可能已被关闭)。请重新调用 se_get_document / se_get_selection。原始错误: " + DescribeException(ex));
			}
			return Error("se_view_context 失败: " + DescribeException(ex));
		}
	}

	// ---------------- map ----------------

	private static string MapCore(SolidEdgeContext context, string sheetArg)
	{
		object doc = null;
		object app = context.GetApplication();
		if (app == null) return Error("拿不到 Solid Edge Application(SE 未启动?)。");
		if (!ManualInvoke.TryInvoke(app, "ActiveDocument", null, out doc, out _) || doc == null)
		{
			return Error("没有活动文档。工程图映射只适用于 .dft(请先打开图纸)。");
		}
		// 用强类型转换判定"是不是工程图":比读类型名字符串可靠(GetType().Name 对 RCW 只能拿到 __ComObject)
		object sheets = null;
		try
		{
			var draftDoc = (DraftDocument)doc;
			sheets = draftDoc.Sheets;
		}
		catch (Exception ex)
		{
			return Error("当前活动文档不是工程图(或取 Sheets 失败):类型 " + ObjectFingerprint.TypeName(doc) + ",错误 " + DescribeException(ex) + "。se_view_context 只适用于 .dft(DraftDocument)。");
		}
		if (sheets == null)
		{
			return Error("取 Sheets 返回 null,无法继续。");
		}
		int sheetCount = ObjectFingerprint.TryCount(sheets) ?? 0;
		if (sheetCount == 0) return Error("文档没有 sheet。");

		var contexts = new List<object>();      // 数字名 sheet = 视图编辑上下文
		var contextNames = new List<string>();
		var pages = new List<object>();
		var pageNames = new List<string>();
		for (int i = 1; i <= sheetCount; i++)
		{
			object sh = ObjectFingerprint.TryItem(sheets, i);
			if (sh == null) continue;
			string nm = ObjectFingerprint.DisplayName(sh);
			long tmp;
			if (long.TryParse(nm, out tmp)) { contexts.Add(sh); contextNames.Add(nm); }
			else { pages.Add(sh); pageNames.Add(nm); }
		}

		// 选图页
		object page = null;
		string pageName = null;
		if (!string.IsNullOrWhiteSpace(sheetArg))
		{
			string want = sheetArg.Trim();
			for (int i = 0; i < pages.Count; i++)
			{
				if (string.Equals(pageNames[i], want, StringComparison.OrdinalIgnoreCase)) { page = pages[i]; pageName = pageNames[i]; break; }
			}
			if (page == null) return Error("找不到图页 \"" + sheetArg + "\"。当前非数字名 sheet: " + string.Join(", ", pageNames.ToArray()));
		}
		else
		{
			for (int i = 0; i < pages.Count; i++)
			{
				object dims = null;
				ManualInvoke.TryInvoke(pages[i], "Dimensions", null, out dims, out _);
				if (dims != null && (ObjectFingerprint.TryCount(dims) ?? 0) > 0) { page = pages[i]; pageName = pageNames[i]; break; }
			}
			if (page == null && pages.Count > 0) { page = pages[0]; pageName = pageNames[0]; }
		}
		if (page == null) return Error("找不到可用图页(没有非数字名 sheet)。");

		object views = null;
		ManualInvoke.TryInvoke(page, "DrawingViews", null, out views, out _);
		int viewCount = views == null ? 0 : (ObjectFingerprint.TryCount(views) ?? 0);

		var viewRows = new List<object>();
		var mapped = new Dictionary<int, string>();     // 上下文索引 → 视图名
		if (viewCount == 0)
		{
			viewRows.Add(new { note = "图页 " + pageName + " 上没有 DrawingView(或 DrawingViews 取不到)。" });
		}
		for (int v = 1; v <= viewCount; v++)
		{
			object vw = ObjectFingerprint.TryItem(views, v);
			if (vw == null) continue;
			string vName = ObjectFingerprint.DisplayName(vw);
			string layer = null;
			object lv = ObjectFingerprint.TryGet(vw, "Layer");
			if (lv != null) layer = lv.ToString();

			string sampleDesc = null;
			string pairedCtx = null;
			string pairError = null;
			string originSheet = null;
			for (int c = 0; c < contexts.Count; c++)
			{
				if (mapped.ContainsKey(c) && mapped[c] != vName) continue;   // 已被别的视图占用
				string err;
				string sd;
				if (IsPaired(vw, contexts[c], out sd, out err))
				{
					pairedCtx = contextNames[c];
					sampleDesc = sd;
					mapped[c] = vName;
					break;
				}
				if (pairError == null && err != null) pairError = err;
			}
			try
			{
				var dv = (DrawingView)vw;
				double sx, sy;
				dv.ViewToSheet(0.0, 0.0, out sx, out sy);
				originSheet = "(" + MM(sx) + "," + MM(sy) + ")mm";
			}
			catch
			{
			}

			viewRows.Add(new
			{
				view = vName,
				layer = layer,
				mappedContext = pairedCtx,
				verifiedBy = pairedCtx == null ? null : "GetReferenceToGraphicMember",
				sampleUsed = sampleDesc,
				viewOriginInSheet = originSheet,
				lastProbeError = pairedCtx == null ? pairError : null
			});
		}

		var ctxRows = new List<object>();
		for (int c = 0; c < contexts.Count; c++)
		{
			object sh = contexts[c];
			int circles = CountOf(sh, "Circles2d");
			int lines = CountOf(sh, "Lines2d");
			int dims = CountOf(sh, "Dimensions");
			string mappedView = null;
			mapped.TryGetValue(c, out mappedView);
			ctxRows.Add(new
			{
				sheet = contextNames[c],
				circles2d = circles,
				lines2d = lines,
				dimensions = dims,
				mappedView = mappedView,
				note = mappedView == null
					? ((circles + lines == 0) ? "空上下文(无几何),无法判定归属" : "未与任何视图配对成功(可能是不属于图页 " + pageName + " 的上下文)")
					: null
			});
		}

		return JsonSerializer.Serialize(new
		{
			status = "ok",
			action = "map",
			page = pageName,
			viewCount = viewCount,
			contextCount = contexts.Count,
			views = viewRows,
			contexts = ctxRows,
			otherSheets = pageNames,
			hint = "配对成功的含义:该数字名 sheet 是那个视图的编辑上下文(要在里面画几何/建约束就进它)。判定用 2×2 交叉试探,不属于时 GetReferenceToGraphicMember 抛 0x80040225;不要用 2D 约束反查归属(约束可跨上下文建立)。建图页标注前先用 action=convert 把视图坐标换算成图纸坐标。"
		});
	}

	private static bool IsPaired(object viewObj, object ctxObj, out string sampleDesc, out string error)
	{
		sampleDesc = null;
		error = null;
		object sample = PickSample(ctxObj, out sampleDesc);
		if (sample == null)
		{
			error = "上下文里没有可用样本对象(既无 Lines2d 也无 Circles2d)";
			return false;
		}
		try
		{
			var dv = (DrawingView)viewObj;
			Reference reference;
			dv.GetReferenceToGraphicMember(sample, out reference);
			if (reference != null) return true;
			error = "返回 Reference=null";
			return false;
		}
		catch (Exception ex)
		{
			error = DescribeException(ex);
			return false;
		}
	}

	/// <summary>取上下文里最长的线当样本;没有线就取第一个圆。返回 null 表示该上下文没有可用几何。</summary>
	private static object PickSample(object ctx, out string desc)
	{
		desc = null;
		object lines = null;
		ManualInvoke.TryInvoke(ctx, "Lines2d", null, out lines, out _);
		int lc = lines == null ? 0 : (ObjectFingerprint.TryCount(lines) ?? 0);
		object best = null;
		double bestLen = -1;
		for (int i = 1; i <= lc; i++)
		{
			object ln = ObjectFingerprint.TryItem(lines, i);
			if (ln == null) continue;
			double[] a = ObjectFingerprint.TryPoint(ln, "GetStartPoint");
			double[] b = ObjectFingerprint.TryPoint(ln, "GetEndPoint");
			if (a == null || b == null) continue;
			double dx = b[0] - a[0];
			double dy = b[1] - a[1];
			double len = Math.Sqrt(dx * dx + dy * dy);
			if (len > bestLen) { bestLen = len; best = ln; }
		}
		if (best != null)
		{
			desc = "最长线 " + (ObjectFingerprint.DisplayName(best) ?? "?") + " len=" + MM(bestLen) + "mm";
			return best;
		}
		object circles = null;
		ManualInvoke.TryInvoke(ctx, "Circles2d", null, out circles, out _);
		int cc = circles == null ? 0 : (ObjectFingerprint.TryCount(circles) ?? 0);
		for (int i = 1; i <= cc; i++)
		{
			object ci = ObjectFingerprint.TryItem(circles, i);
			if (ci != null)
			{
				desc = "圆 " + (ObjectFingerprint.DisplayName(ci) ?? "?");
				return ci;
			}
		}
		return null;
	}

	private static int CountOf(object obj, string collectionName)
	{
		object col = null;
		ManualInvoke.TryInvoke(obj, collectionName, null, out col, out _);
		return col == null ? 0 : (ObjectFingerprint.TryCount(col) ?? 0);
	}

	// ---------------- convert ----------------

	private static string ConvertCore(SolidEdgeContext context, string viewArg, string from, double? x, double? y)
	{
		if (string.IsNullOrWhiteSpace(viewArg)) return Error("convert 需要 view(视图 obj-N 或视图名,如 \"30861\")。");
		if (!x.HasValue || !y.HasValue) return Error("convert 需要 x 与 y(内部单位米;UI 毫米 ÷1000)。");
		object app = context.GetApplication();
		object doc = null;
		if (!ManualInvoke.TryInvoke(app, "ActiveDocument", null, out doc, out _) || doc == null) return Error("没有活动文档。");
		object sheets = null;
		ManualInvoke.TryInvoke(doc, "Sheets", null, out sheets, out _);
		if (sheets == null) return Error("取 Sheets 失败。");

		object vw = null;
		string vName = null;
		string want = viewArg.Trim();
		if (want.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
		{
			ObjectHandle h = context.GetHandle(want);
			if (h == null || h.ComObject == null) return Error("找不到对象编号 " + want + "。");
			vw = h.ComObject;
			vName = h.DisplayName;
		}
		else
		{
			int sc = ObjectFingerprint.TryCount(sheets) ?? 0;
			for (int i = 1; i <= sc && vw == null; i++)
			{
				object sh = ObjectFingerprint.TryItem(sheets, i);
				object vs = null;
				ManualInvoke.TryInvoke(sh, "DrawingViews", null, out vs, out _);
				int vc = vs == null ? 0 : (ObjectFingerprint.TryCount(vs) ?? 0);
				for (int v = 1; v <= vc; v++)
				{
					object cand = ObjectFingerprint.TryItem(vs, v);
					if (cand == null) continue;
					string cn = ObjectFingerprint.DisplayName(cand) ?? "";
					if (string.Equals(cn, want, StringComparison.OrdinalIgnoreCase) || cn.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						vw = cand;
						vName = cn;
						break;
					}
				}
			}
			if (vw == null) return Error("找不到视图 \"" + viewArg + "\"。可先用 action=map 列出图页上的视图名。");
		}

		string dir = string.IsNullOrWhiteSpace(from) ? "view" : from.Trim().ToLowerInvariant();
		double ix = x.Value;
		double iy = y.Value;
		double ox, oy;
		string note = null;
		try
		{
			var dv = (DrawingView)vw;
			if (dir == "sheet")
			{
				dv.SheetToView(ix, iy, out ox, out oy);
				note = "图纸坐标 → 视图坐标(SheetToView)";
			}
			else if (dir == "view")
			{
				dv.ViewToSheet(ix, iy, out ox, out oy);
				note = "视图坐标 → 图纸坐标(ViewToSheet);建图页标注(AddAngleBetweenObjects 等的 locate)要用换算后的图纸坐标";
			}
			else
			{
				return Error("from 只能是 view 或 sheet。收到: \"" + from + "\"");
			}
		}
		catch (Exception ex)
		{
			return Error("坐标换算失败: " + DescribeException(ex) + "。请确认 view 指向的是 DrawingView(不是 sheet/视图编辑上下文)。");
		}

		return JsonSerializer.Serialize(new
		{
			status = "ok",
			action = "convert",
			view = vName,
			direction = note,
			input = new { x = ix, y = iy, xmm = MM(ix), ymm = MM(iy) },
			output = new { x = ox, y = oy, xmm = MM(ox), ymm = MM(oy) },
			hint = "Solid Edge API 坐标内部单位为米(m),UI 显示毫米(mm):mm = 米 × 1000。"
		});
	}

	// ---------------- 杂项 ----------------

	private static string MM(double meters)
	{
		return (meters * 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
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
}
