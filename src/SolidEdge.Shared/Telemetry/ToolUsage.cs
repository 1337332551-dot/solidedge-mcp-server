using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SolidEdge.Spy.McpServer.Telemetry;

/// <summary>
/// MCP 工具调用计量(轻量 JSONL,只追加)。
///
/// 分工:
/// - mcp-audit.log   = 写操作合规留痕(Guardrail/AuditLog,只记写);
/// - tool-usage.jsonl = 全量调用计量(含只读),供"哪个工具常用/哪个从没用过/失败率"决策。
///
/// 写入方是传输层的 <see cref="JsonRpcTap"/>,因此不改任何工具代码。
/// 路径:%LOCALAPPDATA%\SolidEdgeSpy\tool-usage.jsonl
/// </summary>
internal static class ToolUsage
{
	private static readonly object WriteSync = new object();

	// 测试覆盖用:置为非空时 LogPath 返回它(与 Guardrail.ReadOnlyEnabled 同款可写静态模式)
	private static string _logPathOverride;

	internal static void SetLogPathOverride(string path) { _logPathOverride = path; }

	internal static string LogPath
	{
		get
		{
			if (!string.IsNullOrEmpty(_logPathOverride))
			{
				return _logPathOverride;
			}
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidEdgeSpy", "tool-usage.jsonl");
		}
	}

	/// <summary>记录一次已完成的工具调用。任何异常都吞掉——计量绝不能影响主流程。</summary>
	/// <remarks>
	/// L14（2026-09-21）：<paramref name="errText"/> = 失败时的错误原文（截 300 字落盘）。
	/// 之前只记 err 代号（toolStatusError/toolError），112 条失败无法做文本细分。
	/// 可选参数，旧调用点不破坏。
	/// </remarks>
	internal static void Record(string source, string tool, long elapsedMs, bool ok, string errorCode, int argsCount, string errText = null)
	{
		try
		{
			StringBuilder sb = new StringBuilder(160);
			sb.Append('{');
			sb.Append("\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\",");
			sb.Append("\"src\":\"").Append(Escape(source)).Append("\",");
			sb.Append("\"tool\":\"").Append(Escape(tool)).Append("\",");
			sb.Append("\"ms\":").Append(elapsedMs.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append("\"ok\":").Append(ok ? "true" : "false").Append(',');
			sb.Append("\"argsN\":").Append(argsCount.ToString(CultureInfo.InvariantCulture));
			if (!string.IsNullOrEmpty(errorCode))
			{
				sb.Append(",\"err\":\"").Append(Escape(errorCode)).Append('"');
			}
			if (!string.IsNullOrEmpty(errText))
			{
				sb.Append(",\"errText\":\"").Append(Escape(TruncateHeadTail(errText, 120, 260))).Append('"');
			}
			sb.Append('}');
			string line = sb.ToString();

			lock (WriteSync)
			{
				string dir = Path.GetDirectoryName(LogPath);
				if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				{
					Directory.CreateDirectory(dir);
				}
				File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
			}
		}
		catch
		{
			// 计量失败绝不能影响工具调用本身
		}
	}

	/// <summary>
	/// 生成统计报告(给 CLI --usage 用)。
	/// </summary>
	/// <param name="days">只统计最近 N 天;小于等于 0 表示全部。</param>
	/// <param name="sortBy">calls / ms / last / fail</param>
	/// <param name="byDay">true=按天汇总;false=按工具汇总(默认)</param>
	internal static string Report(int days, string sortBy, bool byDay)
	{
		List<Row> rows = ReadRows();
		DateTime cutoff = (days > 0) ? DateTime.Now.AddDays(-days) : DateTime.MinValue;

		StringBuilder sb = new StringBuilder();
		sb.Append("=== MCP 工具调用统计");
		sb.Append(days > 0 ? ("（最近 " + days + " 天）") : "（全部）");
		sb.Append(" ===").Append(Environment.NewLine);
		sb.Append("文件: ").Append(LogPath).Append(Environment.NewLine);
		sb.Append("原始记录: ").Append(rows.Count).Append(" 行").Append(Environment.NewLine).Append(Environment.NewLine);

		List<Row> window = new List<Row>();
		foreach (Row r in rows)
		{
			if (r.Ts >= cutoff)
			{
				window.Add(r);
			}
		}

		if (window.Count == 0)
		{
			sb.Append("（窗口内没有任何调用记录。若刚从 MCP 客户端调用过工具，请确认 server 进程已换成带计量的新构建。）").Append(Environment.NewLine);
			sb.Append(Environment.NewLine).Append(NeverCalledSection(new List<string>()));
			return sb.ToString();
		}

		if (byDay)
		{
			AppendByDay(sb, window);
		}
		else
		{
			AppendByTool(sb, window, sortBy);
		}

		AppendFailureReasons(sb, window);

		List<string> used = new List<string>();
		foreach (Row r in window)
		{
			if (!used.Contains(r.Tool))
			{
				used.Add(r.Tool);
			}
		}
		sb.Append(Environment.NewLine).Append(NeverCalledSection(used));
		return sb.ToString();
	}

	private sealed class Row
	{
		internal DateTime Ts;
		internal string Src;
		internal string Tool;
		internal long Ms;
		internal bool Ok;
		internal int ArgsN;
		// P2-0(2026-11-13):失败原因维度——落盘一直有,读取端从来没读过,Report 拿它做 Top N
		internal string Err;
		internal string ErrText;
	}

	private sealed class Agg
	{
		internal long Calls;
		internal long Ok;
		internal long Fail;
		internal long TotalMs;
		internal long MaxMs;
		internal DateTime LastTs;
		internal string Src;
	}

	private static List<Row> ReadRows()
	{
		List<Row> rows = new List<Row>();
		string path = LogPath;
		if (!File.Exists(path))
		{
			return rows;
		}
		string[] lines;
		try
		{
			lines = File.ReadAllLines(path, Encoding.UTF8);
		}
		catch
		{
			return rows;
		}
		foreach (string line in lines)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}
			try
			{
				using JsonDocument doc = JsonDocument.Parse(line);
				JsonElement root = doc.RootElement;
				Row row = new Row();
				row.Ts = ParseTs(GetString(root, "ts"));
				row.Src = GetString(root, "src") ?? "?";
				row.Tool = GetString(root, "tool") ?? "?";
				row.Ms = GetLong(root, "ms");
				row.Ok = GetBool(root, "ok");
				row.ArgsN = (int)GetLong(root, "argsN");
				row.Err = GetString(root, "err");
				row.ErrText = GetString(root, "errText");
				rows.Add(row);
			}
			catch
			{
				// 坏行跳过
			}
		}
		return rows;
	}

	private static DateTime ParseTs(string s)
	{
		DateTime ts;
		if (!string.IsNullOrEmpty(s) && DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out ts))
		{
			return ts;
		}
		return DateTime.MinValue;
	}

	private static string GetString(JsonElement e, string name)
	{
		JsonElement v;
		if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String)
		{
			return v.GetString();
		}
		return null;
	}

	private static long GetLong(JsonElement e, string name)
	{
		JsonElement v;
		long n;
		if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out n))
		{
			return n;
		}
		return 0L;
	}

	private static bool GetBool(JsonElement e, string name)
	{
		JsonElement v;
		if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v))
		{
			return v.ValueKind == JsonValueKind.True;
		}
		return false;
	}

	private static void AppendByTool(StringBuilder sb, List<Row> window, string sortBy)
	{
		Dictionary<string, Agg> map = new Dictionary<string, Agg>(StringComparer.Ordinal);
		foreach (Row r in window)
		{
			Agg a;
			if (!map.TryGetValue(r.Tool, out a))
			{
				a = new Agg();
				a.Src = r.Src;
				map[r.Tool] = a;
			}
			a.Calls++;
			if (r.Ok)
			{
				a.Ok++;
			}
			else
			{
				a.Fail++;
			}
			a.TotalMs += r.Ms;
			if (r.Ms > a.MaxMs)
			{
				a.MaxMs = r.Ms;
			}
			if (r.Ts > a.LastTs)
			{
				a.LastTs = r.Ts;
			}
		}

		List<KeyValuePair<string, Agg>> list = new List<KeyValuePair<string, Agg>>(map);
		string mode = (sortBy ?? "calls").ToLowerInvariant();
		list.Sort(delegate (KeyValuePair<string, Agg> x, KeyValuePair<string, Agg> y)
		{
			switch (mode)
			{
			case "ms":
				return y.Value.TotalMs.CompareTo(x.Value.TotalMs);
			case "last":
				return y.Value.LastTs.CompareTo(x.Value.LastTs);
			case "fail":
				return y.Value.Fail.CompareTo(x.Value.Fail);
			default:
				return y.Value.Calls.CompareTo(x.Value.Calls);
			}
		});

		sb.Append("工具".PadRight(24)).Append("来源".PadRight(7)).Append("次数".PadLeft(6))
			.Append("成功".PadLeft(6)).Append("失败".PadLeft(6)).Append("平均ms".PadLeft(8))
			.Append("最大ms".PadLeft(8)).Append("  最后调用").Append(Environment.NewLine);
		sb.Append(new string('-', 78)).Append(Environment.NewLine);
		foreach (KeyValuePair<string, Agg> kv in list)
		{
			Agg a = kv.Value;
			long avg = (a.Calls > 0) ? (a.TotalMs / a.Calls) : 0;
			sb.Append(kv.Key.PadRight(24));
			sb.Append((a.Src ?? "?").PadRight(7));
			sb.Append(a.Calls.ToString(CultureInfo.InvariantCulture).PadLeft(6));
			sb.Append(a.Ok.ToString(CultureInfo.InvariantCulture).PadLeft(6));
			sb.Append(a.Fail.ToString(CultureInfo.InvariantCulture).PadLeft(6));
			sb.Append(avg.ToString(CultureInfo.InvariantCulture).PadLeft(8));
			sb.Append(a.MaxMs.ToString(CultureInfo.InvariantCulture).PadLeft(8));
			sb.Append("  ").Append(a.LastTs.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
			sb.Append(Environment.NewLine);
		}
	}

	private static void AppendByDay(StringBuilder sb, List<Row> window)
	{
		Dictionary<string, long> map = new Dictionary<string, long>(StringComparer.Ordinal);
		foreach (Row r in window)
		{
			string key = r.Ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			long n;
			map.TryGetValue(key, out n);
			map[key] = n + 1;
		}
		List<string> keys = new List<string>(map.Keys);
		keys.Sort(StringComparer.Ordinal);
		sb.Append("日期".PadRight(14)).Append("调用次数".PadLeft(10)).Append(Environment.NewLine);
		sb.Append(new string('-', 26)).Append(Environment.NewLine);
		foreach (string k in keys)
		{
			sb.Append(k.PadRight(14)).Append(map[k].ToString(CultureInfo.InvariantCulture).PadLeft(10)).Append(Environment.NewLine);
		}
	}

	/// <summary>列出本程序集里注册了 [McpServerTool] 但统计窗口内从没被调用过的工具。</summary>
	private static string NeverCalledSection(List<string> used)
	{
		List<string> all = DiscoverToolNames();
		if (all.Count == 0)
		{
			return "";
		}
		List<string> never = new List<string>();
		foreach (string name in all)
		{
			if (!used.Contains(name))
			{
				never.Add(name);
			}
		}
		StringBuilder sb = new StringBuilder();
		if (never.Count == 0)
		{
			sb.Append("本次窗口内所有工具都被调用过（本程序集共 ").Append(all.Count).Append(" 个）。");
			return sb.ToString();
		}
		sb.Append("窗口内从未调用的工具（").Append(never.Count).Append('/').Append(all.Count).Append("）：").Append(Environment.NewLine);
		foreach (string n in never)
		{
			sb.Append("  - ").Append(n).Append(Environment.NewLine);
		}
		return sb.ToString();
	}

	private static List<string> DiscoverToolNames()
	{
		List<string> names = new List<string>();
		try
		{
			Assembly asm = typeof(ToolUsage).Assembly;
			Type attrType = Type.GetType("ModelContextProtocol.Server.McpServerToolAttribute, ModelContextProtocol");
			foreach (Type t in asm.GetTypes())
			{
				MethodInfo[] methods;
				try
				{
					methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
				}
				catch
				{
					continue;
				}
				foreach (MethodInfo m in methods)
				{
					if (!m.Name.StartsWith("se_", StringComparison.Ordinal))
					{
						continue;
					}
					bool isTool = (attrType == null) || (m.GetCustomAttributes(attrType, false).Length > 0);
					if (isTool)
					{
						names.Add(m.Name);
					}
				}
			}
		}
		catch
		{
		}
		names.Sort(StringComparer.Ordinal);
		return names;
	}

	/// <summary>
	/// P2-0(2026-11-13):失败原因 Top N——把 err/errText 归成可读的桶,按条数排序。
	/// 归类优先级:无响应 > toolError > 结构化 status(compile_error/exit_nonzero/timeout/partial)
	/// > status:error 的消息首段 > HRESULT > err 原文首段。查不出就落"(无 err 明细)",不猜。
	/// </summary>
	private static void AppendFailureReasons(StringBuilder sb, List<Row> window)
	{
		int totalFail = 0;
		Dictionary<string, int> buckets = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (Row r in window)
		{
			if (r.Ok)
			{
				continue;
			}
			totalFail++;
			string label = ReasonLabel(r);
			int n;
			buckets.TryGetValue(label, out n);
			buckets[label] = n + 1;
		}
		if (totalFail == 0)
		{
			sb.Append(Environment.NewLine).Append("失败原因 Top N:窗口内没有失败记录。");
			return;
		}

		List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(buckets);
		list.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y)
		{
			int c = y.Value.CompareTo(x.Value);
			return (c != 0) ? c : string.CompareOrdinal(x.Key, y.Key);
		});

		sb.Append(Environment.NewLine);
		sb.Append("=== 失败原因 Top N（窗口内失败 ").Append(totalFail).Append(" 条） ===").Append(Environment.NewLine);
		const int TopN = 12;
		for (int i = 0; i < list.Count && i < TopN; i++)
		{
			long pct = (list[i].Value * 100L) / totalFail;
			sb.Append("  ").Append(list[i].Key.PadRight(46)).Append(list[i].Value.ToString(CultureInfo.InvariantCulture).PadLeft(6))
				.Append('(').Append(pct).Append("%)").Append(Environment.NewLine);
		}
		if (list.Count > TopN)
		{
			int rest = 0;
			for (int i = TopN; i < list.Count; i++)
			{
				rest += list[i].Value;
			}
			sb.Append("  ……其余 ").Append(list.Count - TopN).Append(" 类").Append(rest).Append(" 条").Append(Environment.NewLine);
		}
	}

	/// <summary>单条失败归到哪个桶。只认能从记录里客观提取的标记,不猜。</summary>
	private static string ReasonLabel(Row r)
	{
		string err = r.Err ?? "";
		if (err == "noResponse")
		{
			return "无响应(超时/取消,没收到结果)";
		}
		if (err.StartsWith("rowFailures:", StringComparison.Ordinal))
		{
			return "rowFailures(信封行级失败,如 batch_read 部分成员读不出)";
		}
		if (err.StartsWith("toolError", StringComparison.Ordinal))
		{
			return "toolError(isError 协议错误)";
		}
		string t = r.ErrText ?? "";
		foreach (string s in StructuredStatuses)
		{
			if (t.IndexOf("\"status\":\"" + s + "\"", StringComparison.Ordinal) >= 0)
			{
				if (s == "compile_error")
				{
					Match cs = Regex.Match(t, "error (CS\\d+)");
					return cs.Success ? ("compile_error(首次=" + cs.Groups[1].Value + ")") : "compile_error";
				}
				return s;
			}
		}
		if (t.IndexOf("\"status\":\"error\"", StringComparison.Ordinal) >= 0)
		{
			string head = err.StartsWith("toolStatusError:", StringComparison.Ordinal) ? err.Substring("toolStatusError:".Length) : err;
			if (head.Length == 0)
			{
				head = "(无明细)";
			}
			return "error:" + head.Substring(0, Math.Min(28, head.Length));
		}
		Match hrx = Regex.Match(err.Length > 12 ? err : t, "0x[0-9A-Fa-f]{8}");
		if (hrx.Success)
		{
			return "HRESULT " + hrx.Value.ToUpperInvariant();
		}
		if (err.Length > 0)
		{
			return err.Substring(0, Math.Min(28, err.Length));
		}
		return "(无 err 明细)";
	}

	private static readonly string[] StructuredStatuses = { "compile_error", "exit_nonzero", "timeout", "partial" };

	private static string Escape(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}

	/// <summary>L14：错误原文截断(留尾不留头会丢前缀语义,故从头截)。</summary>
	private static string Truncate(string s, int max)
	{
		if (string.IsNullOrEmpty(s) || s.Length <= max)
		{
			return s;
		}
		return s.Substring(0, max);
	}

	/// <summary>P2-0：errText 头 120+尾 260——头有 status/name,尾有编译器报错尾部/汇总,中段标注省略字数。</summary>
	private static string TruncateHeadTail(string s, int head, int tail)
	{
		if (string.IsNullOrEmpty(s) || s.Length <= head + tail)
		{
			return s;
		}
		return s.Substring(0, head) + "…[中略" + (s.Length - head - tail).ToString(CultureInfo.InvariantCulture) + "字]…" + s.Substring(s.Length - tail);
	}
}
