using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SolidEdge.Spy.McpServer.Telemetry;

/// <summary>
/// 脚本内容指纹:把一段脚本归一成"调了哪些成员"的签名(跨会话重复检测的数据基础)。
///
/// 设计要点:
/// - 不用原始哈希——跨会话重写的代码变量名/结构都不同,裸哈希永远对不上;
///   SE 脚本里真正承载知识的是 COM 成员调用序列(PutOrigin+occurrences=装配移动坑的签名),
///   变量名/注释/字符串字面量都是噪声,剥掉后抽成员名集合,同任务重写三遍签名大概率重合。
/// - BCL 管线成员(WriteLine/ToString/Length…)几乎每个脚本都有,不区分任务,过滤掉让聚类更锐。
/// - 哈希用 SHA256 前 4 字节(16 进制 8 位)。string.GetHashCode 在 .NET 里按进程随机化,不能落盘。
/// </summary>
internal static class ScriptSig
{
	/// <summary>BCL/控制台管线成员:出现在几乎所有脚本里,对"这是什么任务"零区分度。</summary>
	private static readonly HashSet<string> NoiseMembers = new HashSet<string>(StringComparer.Ordinal)
	{
		"Write", "WriteLine", "WriteAllText", "WriteLineAsync",
		"Read", "ReadLine", "ReadKey", "ReadToEnd", "ReadAllText", "ReadAllLines",
		"ToString", "GetType", "Equals", "GetHashCode", "Format", "Join", "Split", "Trim", "TrimStart", "TrimEnd",
		"Sleep", "Exit", "Now", "Today", "Length", "Count", "Copy", "Combine", "Exists", "CreateDirectory"
	};

	private static readonly Regex MemberPattern = new Regex(@"\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

	/// <summary>计算指纹与成员签名。空/null 代码返回空结果,不抛异常。</summary>
	internal static ScriptSigResult Compute(string code)
	{
		var result = new ScriptSigResult();
		if (string.IsNullOrEmpty(code))
		{
			return result;
		}
		string stripped = StripCommentsAndStrings(code);
		var members = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match m in MemberPattern.Matches(stripped))
		{
			string name = m.Groups[1].Value;
			if (!NoiseMembers.Contains(name))
			{
				members.Add(name);
			}
		}
		var list = new List<string>(members);
		list.Sort(StringComparer.Ordinal);
		result.Members = string.Join(";", list.ToArray());
		if (list.Count > 0)
		{
			using SHA256 sha = SHA256.Create();
			byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(result.Members));
			result.Hash = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
		}
		return result;
	}

	/// <summary>
	/// 剥掉注释与字符串字面量(小状态机,非正则替换:注释里有引号/字符串里有 // 都不能弄坏)。
	/// 剥掉的位置补一个空格,保证 .member 不因删除而粘连。
	/// </summary>
	internal static string StripCommentsAndStrings(string code)
	{
		if (string.IsNullOrEmpty(code))
		{
			return "";
		}
		var sb = new StringBuilder(code.Length);
		int i = 0;
		int n = code.Length;
		while (i < n)
		{
			char c = code[i];
			// 行注释 //... 到行尾
			if (c == '/' && i + 1 < n && code[i + 1] == '/')
			{
				while (i < n && code[i] != '\n')
				{
					i++;
				}
				continue;
			}
			// 块注释 /*...*/
			if (c == '/' && i + 1 < n && code[i + 1] == '*')
			{
				i += 2;
				while (i + 1 < n && !(code[i] == '*' && code[i + 1] == '/'))
				{
					i++;
				}
				i = Math.Min(i + 2, n);
				sb.Append(' ');
				continue;
			}
			// 逐字字符串 @"..."("" 转义)
			if (c == '@' && i + 1 < n && code[i + 1] == '"')
			{
				i += 2;
				while (i < n)
				{
					if (code[i] == '"')
					{
						if (i + 1 < n && code[i + 1] == '"')
						{
							i += 2;
							continue;
						}
						i++;
						break;
					}
					i++;
				}
				sb.Append(' ');
				continue;
			}
			// 普通字符串 "..."(反斜杠转义)
			if (c == '"')
			{
				i++;
				while (i < n && code[i] != '"')
				{
					if (code[i] == '\\')
					{
						i++;
					}
					i++;
				}
				i = Math.Min(i + 1, n);
				sb.Append(' ');
				continue;
			}
			// 字符字面量 '...'(反斜杠转义)
			if (c == '\'')
			{
				i++;
				while (i < n && code[i] != '\'')
				{
					if (code[i] == '\\')
					{
						i++;
					}
					i++;
				}
				i = Math.Min(i + 1, n);
				sb.Append(' ');
				continue;
			}
			sb.Append(c);
			i++;
		}
		return sb.ToString();
	}
}

/// <summary>指纹结果:Hash=8 位十六进制(落盘聚类键),Members=排序去重后的成员签名(人读)。</summary>
internal struct ScriptSigResult
{
	internal string Hash;
	internal string Members;
}

/// <summary>
/// se_script_run 执行历史(轻量 JSONL,只追加)。
///
/// 分工(与 tool-usage.jsonl 互补):
/// - tool-usage.jsonl 记"调了几次"但不见内容(argsN 只是参数个数);
/// - script-history.jsonl 记"每次跑的是什么"——ts/名称/来源/指纹/成员签名/状态/耗时/全文。
///   跨会话重复检测(沉淀候选探测)与未来的脚本库 action=stats 都吃这个文件。
///
/// 记录时机:源码已解析成功(codeSource 确定)之后的 blocked / compile_error / 最终执行结果;
/// 名称非法、源码文件读不到这类"没有代码可记"的分支不记。
/// 任何异常都吞掉——历史留档绝不能影响脚本执行本身。
/// 路径:%LOCALAPPDATA%\SolidEdgeSpy\script-history.jsonl
/// </summary>
internal static class ScriptHistory
{
	private static readonly object WriteSync = new object();

	// 测试覆盖用:置为非空时 LogPath 返回它(与 ToolUsage 同款模式)
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
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidEdgeSpy", "script-history.jsonl");
		}
	}

	/// <summary>
	/// 记录一次脚本执行(或编译失败/被拦截)。status ∈ { blocked, compile_error, ok, exit_nonzero, timeout, error }。
	/// code 必须传用户原始源码(注入崩溃护栏前的),指纹才可比。
	/// </summary>
	internal static void Record(string name, string source, string code, string status, int? exitCode, long elapsedMs)
	{
		try
		{
			ScriptSigResult sig = ScriptSig.Compute(code);
			// members 截断:全文已在 code 字段,这里只做聚类展示;超长说明成员巨多,截断不损语义
			string members = sig.Members;
			if (members != null && members.Length > 256)
			{
				members = members.Substring(0, 256) + "…";
			}
			StringBuilder sb = new StringBuilder(512);
			sb.Append('{');
			sb.Append("\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\",");
			sb.Append("\"name\":\"").Append(Escape(name)).Append("\",");
			sb.Append("\"src\":\"").Append(Escape(Truncate(source, 120))).Append("\",");
			sb.Append("\"sig\":\"").Append(sig.Hash ?? "").Append("\",");
			sb.Append("\"members\":\"").Append(Escape(members ?? "")).Append("\",");
			sb.Append("\"status\":\"").Append(Escape(status)).Append("\",");
			if (exitCode.HasValue)
			{
				sb.Append("\"exit\":").Append(exitCode.Value.ToString(CultureInfo.InvariantCulture)).Append(',');
			}
			sb.Append("\"ms\":").Append(elapsedMs.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append("\"chars\":").Append((code ?? "").Length.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append("\"code\":\"").Append(Escape(code)).Append('"');
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
			// 留档失败绝不能影响脚本执行本身
		}
	}

	/// <summary>
	/// 重复形态报表(给 CLI --scripts 用):按指纹分组,同形态出现 ≥3 次标"沉淀候选"。
	/// </summary>
	/// <param name="days">只统计最近 N 天;小于等于 0 表示全部。</param>
	internal static string Report(int days)
	{
		List<Row> rows = ReadRows();
		DateTime cutoff = (days > 0) ? DateTime.Now.AddDays(-days) : DateTime.MinValue;

		StringBuilder sb = new StringBuilder();
		sb.Append("=== 脚本执行历史");
		sb.Append(days > 0 ? ("（最近 " + days + " 天）") : "（全部）");
		sb.Append(" ===").Append(Environment.NewLine);
		sb.Append("文件: ").Append(LogPath).Append(Environment.NewLine);

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
			sb.Append("窗口内没有任何脚本执行记录。(留档自 2026-11-17 起生效,之前的执行没有历史可查。)");
			return sb.ToString();
		}

		// 状态分布总览
		Dictionary<string, int> statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (Row r in window)
		{
			int n;
			statusCounts.TryGetValue(r.Status ?? "?", out n);
			statusCounts[r.Status ?? "?"] = n + 1;
		}
		sb.Append("记录: ").Append(window.Count).Append(" 条 | 状态: ");
		bool firstStatus = true;
		foreach (KeyValuePair<string, int> kv in statusCounts)
		{
			if (!firstStatus)
			{
				sb.Append(", ");
			}
			firstStatus = false;
			sb.Append(kv.Key).Append('=').Append(kv.Value.ToString(CultureInfo.InvariantCulture));
		}
		sb.Append(Environment.NewLine).Append(Environment.NewLine);

		// 按指纹分组(空指纹=剥完噪声没有成员可认,单列)
		var groups = new Dictionary<string, Agg>(StringComparer.Ordinal);
		int noSig = 0;
		foreach (Row r in window)
		{
			if (string.IsNullOrEmpty(r.Sig))
			{
				noSig++;
				continue;
			}
			Agg a;
			if (!groups.TryGetValue(r.Sig, out a))
			{
				a = new Agg();
				a.Members = r.Members;
				groups[r.Sig] = a;
			}
			a.Count++;
			if (r.Status == "ok")
			{
				a.Ok++;
			}
			if (r.Ts > a.LastTs)
			{
				a.LastTs = r.Ts;
			}
			if (!a.Names.Contains(r.Name))
			{
				a.Names.Add(r.Name);
			}
		}

		List<KeyValuePair<string, Agg>> list = new List<KeyValuePair<string, Agg>>(groups);
		list.Sort(delegate (KeyValuePair<string, Agg> x, KeyValuePair<string, Agg> y)
		{
			int c = y.Value.Count.CompareTo(x.Value.Count);
			return (c != 0) ? c : string.CompareOrdinal(x.Key, y.Key);
		});

		sb.Append("=== 重复形态 Top 12（按成员指纹分组:变量名/注释/字符串已归一） ===").Append(Environment.NewLine);
		const int TopN = 12;
		int shown = 0;
		foreach (KeyValuePair<string, Agg> kv in list)
		{
			if (shown >= TopN)
			{
				sb.Append("……其余 ").Append(list.Count - TopN).Append(" 种形态").Append(Environment.NewLine);
				break;
			}
			shown++;
			Agg a = kv.Value;
			string names = string.Join(",", a.Names.ToArray());
			if (names.Length > 40)
			{
				names = names.Substring(0, 40) + "…";
			}
			string members = a.Members ?? "";
			if (members.Length > 90)
			{
				members = members.Substring(0, 90) + "…";
			}
			sb.Append(kv.Key).Append(' ')
				.Append(a.Count.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("次 ")
				.Append(a.Ok.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("成 ")
				.Append(a.LastTs.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(' ')
				.Append('[').Append(names).Append("] ")
				.Append(members);
			if (a.Count >= 3)
			{
				sb.Append("  ← 沉淀候选(≥3 次)");
			}
			sb.Append(Environment.NewLine);
		}
		if (shown == 0)
		{
			sb.Append("（窗口内没有可聚类的记录。）").Append(Environment.NewLine);
		}
		if (noSig > 0)
		{
			sb.Append("另有 ").Append(noSig.ToString(CultureInfo.InvariantCulture)).Append(" 条无成员签名的记录(剥掉噪声后无可识别成员调用)。").Append(Environment.NewLine);
		}
		return sb.ToString();
	}

	private sealed class Row
	{
		internal DateTime Ts;
		internal string Name;
		internal string Src;
		internal string Sig;
		internal string Members;
		internal string Status;
	}

	private sealed class Agg
	{
		internal int Count;
		internal int Ok;
		internal DateTime LastTs;
		internal string Members;
		internal readonly List<string> Names = new List<string>();
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
				row.Name = GetString(root, "name") ?? "?";
				row.Src = GetString(root, "src");
				row.Sig = GetString(root, "sig");
				row.Members = GetString(root, "members");
				row.Status = GetString(root, "status") ?? "?";
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

	/// <summary>JSON 转义(比 ToolUsage 版多处理换行/制表/控制字符:code 字段是全文,必然含 \n)。</summary>
	private static string Escape(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		var sb = new StringBuilder(s.Length + 16);
		foreach (char c in s)
		{
			switch (c)
			{
			case '\\': sb.Append("\\\\"); break;
			case '"': sb.Append("\\\""); break;
			case '\n': sb.Append("\\n"); break;
			case '\r': sb.Append("\\r"); break;
			case '\t': sb.Append("\\t"); break;
			default:
				if (c < ' ')
				{
					sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
				}
				else
				{
					sb.Append(c);
				}
				break;
			}
		}
		return sb.ToString();
	}

	private static string Truncate(string s, int max)
	{
		if (string.IsNullOrEmpty(s) || s.Length <= max)
		{
			return s;
		}
		return s.Substring(0, max);
	}
}
