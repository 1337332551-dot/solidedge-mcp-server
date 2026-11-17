using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SolidEdgeFramework;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_read_table:工程图表格(PartsList 明细表 / Table2d)结构化读取。
/// 由来(2026-11-13,P4):读一张明细表曾连写 9 个探针脚本才有 120 行有效产出;
/// 本工具把当天跑通的 dump_table.cs / read_partslist.cs 路径固化成一条命令。
///
/// API 路径全部来自当天实机验证(反射晚绑定,与 se_invoke_member 的晚绑定兜底同一思想):
///   table.Rows/Columns.Count、NumberOfHeaderRows、Cell(r,c).value/.PropertyText、
///   Columns.Item(c).Header/.Show/.PropertyText、Pages.Item(i).Sheet(跨页归属)。
///
/// 入口三选一:obj-N 句柄(表格或文档) > 当前选中 > 自动扫描全部 Sheet 定位——
/// 历史教训:旧 se_read_partslist 只认 SelectSet,没选中就抓瞎(2026-09-12 删除的根因之一)。
/// 本工具同时把当年 codebuddy plan 第 10 条"集合类返回要限流与摘要"一并落实(maxCells 硬上限+显式截断)。
/// 只读。
/// </summary>
[McpServerToolType]
public static class TableTools
{
	[McpServerTool]
	[Description("读工程图表格(PartsList 明细表/Table2d)。detail=summary(默认):扫描活动文档全部 Sheet 的" +
		"PartsLists+Tables2d,一次出表清单+每张表的行列数/表头行数/跨页归属/列头;detail=cells:输出第 table 张表的" +
		"全量单元格(值+PropertyText,换行压平为~)。\n" +
		"入口优先级:objectId=obj-N(表格或文档句柄) > 当前选中 > 自动扫描——没选中也能定位(旧 read_partslist 只认 SelectSet 的教训)。\n" +
		"cells 有硬上限 maxCells(默认 2000),超出显式 truncated=true+rowsIncluded 标注,不会静默截半。\n" +
		"典型用途:明细表内容核对、装配 BOM 导出前置、表格跨页归属确认。只读,不改任何东西。")]
	public static string se_read_table(
		SolidEdgeContext context,
		[Description("可选:obj-N 句柄——表格对象(直接读)或文档(在其中扫描);省略=活动文档")] string objectId = null,
		[Description("summary(默认)=表清单+元数据+列头;cells=第 table 张表的全量单元格")] string detail = "summary",
		[Description("cells 模式:找到多张表时读第几张(1-based,默认 1)")] int table = 1,
		[Description("cells 模式行数上限,0=全部(仍受 maxCells 约束)")] int maxRows = 0,
		[Description("cells 模式单元格硬上限(默认 2000),超出截断并显式标注")] int maxCells = 2000)
	{
		if (maxCells <= 0)
		{
			maxCells = 2000;
		}
		string det = string.IsNullOrWhiteSpace(detail) ? "summary" : detail.Trim().ToLowerInvariant();
		if (det != "summary" && det != "cells")
		{
			return Error("detail 只支持 summary|cells,收到: " + detail);
		}
		try
		{
			return context.Invoke(delegate
			{
				// ---- 定位目标:句柄 > 选中 > 扫描 ----
				object docObj = null;
				object directTable = null;
				if (!string.IsNullOrWhiteSpace(objectId))
				{
					var handle = context.GetHandle(objectId.Trim());
					if (handle == null)
					{
						return Error("句柄不存在或已失效(文档已变更?): " + objectId + "。请重新 se_get_selection。");
					}
					if (HasTableShape(handle.ComObject))
					{
						directTable = handle.ComObject;
					}
					else
					{
						docObj = handle.ComObject;
					}
				}
				if (docObj == null && directTable == null)
				{
					Application application = context.GetApplication();
					try { docObj = application.ActiveDocument; } catch { docObj = null; }
					if (docObj == null)
					{
						return Error("当前没有活动文档。请先打开工程图(.dft)再读表格。");
					}
				}

				var tables = new List<TableRef>();
				if (directTable != null)
				{
					tables.Add(new TableRef { Com = directTable, SheetName = null, Kind = "obj-N(直接句柄)" });
				}
				else
				{
					// 选中的表格优先(用户明确指着的东西最可信),其次全扫描
					foreach (object sel in EnumerateSelection(docObj))
					{
						if (HasTableShape(sel))
						{
							tables.Add(new TableRef { Com = sel, SheetName = null, Kind = "选中对象" });
						}
					}
					if (tables.Count == 0)
					{
						ScanDocument(docObj, tables);
					}
					if (tables.Count == 0)
					{
						return Error("文档里没有定位到表格(PartsList/Table2d)。排查:① 当前文档是否工程图(.dft)?类型=" +
							SafeTypeName(docObj) + ";② 表格是否在模型视图内的零件文件里(那是 Part/Asm 的东西,本工具不覆盖);" +
							"③ 可先用 se_get_selection 选中表格再调,或传 objectId。");
					}
				}

				// ---- summary:全清单 ----
				bool cellsMode = (det == "cells");
				var tableSummaries = new List<object>();
				int n = 0;
				foreach (TableRef tr in tables)
				{
					n++;
					tableSummaries.Add(Summarize(tr, n, !cellsMode));
				}

				var result = new Dictionary<string, object>
				{
					["status"] = "ok",
					["doc"] = DocInfo(docObj),
					["tableCount"] = tables.Count,
					["detail"] = det,
					["tables"] = tableSummaries
				};

				if (cellsMode)
				{
					if (table < 1 || table > tables.Count)
					{
						return Error("table=第几张 越界:1..'" + tables.Count + "' 收到 " + table + "(summary 模式可见全部表的清单)。");
					}
					result["cells"] = DumpCells(tables[table - 1], maxRows, maxCells);
				}
				return JsonSerializer.Serialize(result);
			});
		}
		catch (Exception ex)
		{
			return Error("读表格失败: " + EvidenceContract.DescribeException(ex));
		}
	}

	// ---------- 表格定位 ----------

	private sealed class TableRef
	{
		internal object Com;
		internal string SheetName;
		internal string Kind;
	}

	/// <summary>表格判别:有 Rows 且有 Columns(对 PartsList/Table2d 都成立,不猜具体类型)。</summary>
	private static bool HasTableShape(object o)
	{
		if (o == null || !Marshal.IsComObject(o))
		{
			return false;
		}
		return Call(o, "Rows") != null && Call(o, "Columns") != null;
	}

	private static IEnumerable<object> EnumerateSelection(object doc)
	{
		object ss = Call(doc, "SelectSet");
		if (ss == null)
		{
			yield break;
		}
		int count = ToInt(Call(ss, "Count"));
		for (int i = 1; i <= count; i++)
		{
			object item = Call(ss, "Item", i);
			if (item != null)
			{
				yield return item;
			}
		}
	}

	private static void ScanDocument(object doc, List<TableRef> tables)
	{
		// ★ 实机勘误(2026-11-14):本机 SE2022 的 PartsLists/Tables/HoleTables/... 全部挂在
		//   DraftDocument 级,Sheet 上【没有】这些集合(interop 反射证实;旧工具只走 SelectSet
		//   从没暴露过)。跨页归属用 Pages.Item(i).Sheet 还原所在图页(dump_table.cs 实测路径)。
		foreach (string collection in new[] { "PartsLists", "Tables", "HoleTables", "BlockTables", "ConnectorTables", "DraftBendTables" })
		{
			object coll = Call(doc, collection);
			int cnt = ToInt(Call(coll, "Count"));
			for (int i = 1; i <= cnt; i++)
			{
				object t = Call(coll, "Item", i);
				if (t != null)
				{
					tables.Add(new TableRef { Com = t, SheetName = SheetOf(t), Kind = collection });
				}
			}
		}
		// 兜底:某些版本若把集合放 Sheet 级,也扫一遍(本机不会命中,无害)
		object sheets = Call(doc, "Sheets");
		if (sheets != null)
		{
			int sheetCount = ToInt(Call(sheets, "Count"));
			for (int s = 1; s <= sheetCount; s++)
			{
				object sheet = Call(sheets, "Item", s);
				if (sheet == null)
				{
					continue;
				}
				foreach (string collection in new[] { "PartsLists", "Tables2d" })
				{
					object coll = Call(sheet, collection);
					int cnt = ToInt(Call(coll, "Count"));
					for (int i = 1; i <= cnt; i++)
					{
						object t = Call(coll, "Item", i);
						if (t != null)
						{
							tables.Add(new TableRef { Com = t, SheetName = S(Call(sheet, "Name")), Kind = collection + "(sheet级)" });
						}
					}
				}
			}
		}
	}

	/// <summary>表格所在图页:Pages.Item(1).Sheet.Name(dump_table.cs 实测路径);取不到给 null。</summary>
	private static string SheetOf(object table)
	{
		try
		{
			object pages = Call(table, "Pages");
			int n = ToInt(Call(pages, "Count"));
			for (int i = 1; i <= n && i <= 3; i++)
			{
				object page = Call(pages, "Item", i);
				object sheet = Call(page, "Sheet");
				if (sheet != null)
				{
					string name = S(Call(sheet, "Name"));
					if (name.Length > 0)
					{
						return name;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	// ---------- 概要与单元格 ----------

	private static object Summarize(TableRef tr, int ordinal, bool withColumns)
	{
		object t = tr.Com;
		int rowCount = ToInt(Call(Call(t, "Rows"), "Count"));
		int colCount = ToInt(Call(Call(t, "Columns"), "Count"));
		var pages = new List<string>();
		object pagesColl = Call(t, "Pages");
		int pageCount = ToInt(Call(pagesColl, "Count"));
		for (int p = 1; p <= pageCount && p <= 12; p++)
		{
			object page = Call(pagesColl, "Item", p);
			object sheet = Call(page, "Sheet");
			pages.Add(sheet != null ? S(Call(sheet, "Name")) : ("page" + p));
		}

		var columns = new List<object>();
		object colsColl = Call(t, "Columns");
		int colLimit = Math.Min(colCount, 30);
		for (int c = 1; c <= colLimit; c++)
		{
			object col = Call(colsColl, "Item", c);
			columns.Add(new
			{
				col = c,
				header = S(Call(col, "Header")),
				show = Call(col, "Show") is bool b ? (bool?)b : null,
				propertyText = NullIfEmpty(S(Call(col, "PropertyText")))
			});
		}

		return new
		{
			ordinal,
			kind = tr.Kind,
			sheet = tr.SheetName,
			name = NullIfEmpty(S(Call(t, "Name"))),
			rows = rowCount,
			columns = colCount,
			numberOfHeaderRows = ToInt(Call(t, "NumberOfHeaderRows")),
			pages = pages,
			isUpToDate = Call(t, "IsUpToDate") is bool up ? (bool?)up : null,
			assemblyFileName = NullIfEmpty(S(Call(t, "AssemblyFileName"))),
			columnHeaders = withColumns ? columns : null
		};
	}

	private static object DumpCells(TableRef tr, int maxRows, int maxCells)
	{
		object t = tr.Com;
		int rowCount = ToInt(Call(Call(t, "Rows"), "Count"));
		int colCount = ToInt(Call(Call(t, "Columns"), "Count"));
		int rowLimit = (maxRows > 0) ? Math.Min(rowCount, maxRows) : rowCount;

		var grid = new List<string[]>();
		int budget = maxCells;
		int rowsIncluded = 0;
		bool truncated = false;
		for (int r = 1; r <= rowLimit; r++)
		{
			if (budget < colCount)
			{
				truncated = true;
				break;
			}
			var rowCells = new string[colCount];
			for (int c = 1; c <= colCount; c++)
			{
				string v;
				try
				{
					object cell = Call(t, "Cell", r, c);
					v = Flatten(S(Call(cell, "value")));
					string pt = Flatten(S(Call(cell, "PropertyText")));
					if (pt.Length > 0 && pt != v)
					{
						v = v + "{PT:" + pt + "}";
					}
				}
				catch (Exception e)
				{
					v = "<err:" + FirstLine(e.Message, 60) + ">";
				}
				rowCells[c - 1] = v;
				budget--;
			}
			grid.Add(rowCells);
			rowsIncluded = r;
		}

		return new
		{
			sheet = tr.SheetName,
			kind = tr.Kind,
			rowCount,
			colCount,
			maxRows,
			maxCells,
			rowsIncluded,
			truncated,
			truncationNote = truncated
				? ("单元格超出 maxCells=" + maxCells + " 上限,只输出前 " + rowsIncluded + "/" + rowCount + " 行。要更多请调大 maxCells 或用 maxRows 分段读。")
				: null,
			grid
		};
	}

	// ---------- 小工具(反射晚绑定,与 dump_table.cs 实测路径一致) ----------

	/// <summary>属性优先、方法兜底的统一调用(与脚本验证过的 Call 同款)。</summary>
	private static object Call(object o, string name, params object[] args)
	{
		if (o == null)
		{
			return null;
		}
		try
		{
			return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args ?? Array.Empty<object>());
		}
		catch
		{
		}
		try
		{
			return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args ?? Array.Empty<object>());
		}
		catch
		{
			return null;
		}
	}

	private static object DocInfo(object doc)
	{
		return new
		{
			name = S(Call(doc, "Name")),
			fullName = NullIfEmpty(S(Call(doc, "FullName"))),
			type = SafeTypeName(doc)
		};
	}

	private static string SafeTypeName(object o)
	{
		if (o == null)
		{
			return "(null)";
		}
		try
		{
			// __ComObject 没信息量,走 TypeInfo 解析真实类型名(与 se_get_document 同思路)
			string t = ObjectFingerprint.TypeName(o);
			return string.IsNullOrEmpty(t) ? o.GetType().Name : t;
		}
		catch
		{
			return o.GetType().Name;
		}
	}

	private static int ToInt(object o)
	{
		try
		{
			return o == null ? 0 : Convert.ToInt32(o);
		}
		catch
		{
			return 0;
		}
	}

	private static string S(object o)
	{
		return o == null ? "" : Convert.ToString(o);
	}

	private static string Flatten(string s)
	{
		return (s ?? "").Replace("\r", " ").Replace("\n", "~").Trim();
	}

	private static string FirstLine(string s, int max)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		int i = s.IndexOfAny(new[] { '\r', '\n' });
		string line = (i >= 0) ? s.Substring(0, i) : s;
		return line.Length > max ? line.Substring(0, max) : line;
	}

	private static string NullIfEmpty(string s)
	{
		return string.IsNullOrEmpty(s) ? null : s;
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new { status = "error", message });
	}
}
