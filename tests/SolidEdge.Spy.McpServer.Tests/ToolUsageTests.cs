using System;
using System.IO;
using System.Text;
using SolidEdge.Spy.McpServer.Telemetry;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests;

/// <summary>
/// ToolUsage 计量测试(打脸台账/机会数的数据源,统计失真 = 知识沉淀失真)。
/// 通过 LogPath internal 覆盖重定向到临时目录,不碰真实 %LOCALAPPDATA%。
/// </summary>
public sealed class ToolUsageTests : IDisposable
{
	private readonly string _tempDir;

	public ToolUsageTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "sespy-usage-tests", Guid.NewGuid().ToString("N"));
		ToolUsage.SetLogPathOverride(Path.Combine(_tempDir, "tool-usage.jsonl"));
	}

	public void Dispose()
	{
		ToolUsage.SetLogPathOverride(null);
		try { Directory.Delete(_tempDir, true); } catch { /* 尽力清理 */ }
	}

	// ---------- Record:写入格式 ----------

	[Fact]
	public void Record_成功调用_写出全部字段且无err()
	{
		ToolUsage.Record("mcp", "se_get_document", 123, true, null, 2);

		string line = AssertSingleLine();
		using var doc = System.Text.Json.JsonDocument.Parse(line);
		var root = doc.RootElement;

		Assert.Equal("mcp", root.GetProperty("src").GetString());
		Assert.Equal("se_get_document", root.GetProperty("tool").GetString());
		Assert.Equal(123, root.GetProperty("ms").GetInt64());
		Assert.True(root.GetProperty("ok").GetBoolean());
		Assert.Equal(2, root.GetProperty("argsN").GetInt32());
		// ts 必须是可解析的本地时间格式
		Assert.True(DateTime.TryParseExact(
			root.GetProperty("ts").GetString(), "yyyy-MM-dd HH:mm:ss",
			null, System.Globalization.DateTimeStyles.None, out _));
		Assert.False(root.TryGetProperty("err", out _), "成功调用不应写 err 字段");
	}

	[Fact]
	public void Record_失败调用_带原始错误码()
	{
		ToolUsage.Record("cli", "se_invoke_chain", 50, false, "E404", 3);

		string line = AssertSingleLine();
		using var doc = System.Text.Json.JsonDocument.Parse(line);
		var root = doc.RootElement;

		Assert.False(root.GetProperty("ok").GetBoolean());
		Assert.Equal("E404", root.GetProperty("err").GetString());
	}

	[Fact]
	public void Record_特殊字符_转义后可完整读回()
	{
		ToolUsage.Record("mcp", "tool\"with\\quote", 1, true, "err\"x\\y", 0);

		string line = AssertSingleLine();
		using var doc = System.Text.Json.JsonDocument.Parse(line);
		Assert.Equal("tool\"with\\quote", doc.RootElement.GetProperty("tool").GetString());
		Assert.Equal("err\"x\\y", doc.RootElement.GetProperty("err").GetString());
	}

	[Fact]
	public void Record_目录不存在_自动创建()
	{
		Assert.False(Directory.Exists(_tempDir));
		ToolUsage.Record("mcp", "se_get_selection", 5, true, null, 0);
		Assert.True(File.Exists(ToolUsage.LogPath));
	}

	[Fact]
	public void Record_写入失败_吞异常不抛出()
	{
		// 把目录本身当文件路径写 → 必炸,但 Record 必须吞掉
		Directory.CreateDirectory(_tempDir);
		ToolUsage.SetLogPathOverride(_tempDir); // 指向目录

		ToolUsage.Record("mcp", "se_get_document", 1, true, null, 0); // 不应抛
	}

	[Fact]
	public void Record_多次调用_逐行追加()
	{
		ToolUsage.Record("mcp", "a", 1, true, null, 0);
		ToolUsage.Record("mcp", "b", 2, false, "E1", 1);

		string[] lines = File.ReadAllLines(ToolUsage.LogPath);
		Assert.Equal(2, lines.Length);
	}

	// ---------- Report:读取与聚合 ----------

	[Fact]
	public void Report_日志缺失_提示空窗口并列出未调用工具()
	{
		string report = ToolUsage.Report(7, "calls", byDay: false);

		Assert.Contains("没有任何调用记录", report);
		Assert.Contains("从未调用的工具", report);
		// 未调用清单来自程序集反射,应包含真实注册的工具名
		Assert.Contains("se_get_document", report);
	}

	[Fact]
	public void Report_按工具聚合_次数成功失败均值最大值()
	{
		// toolA: 2 次全成功, ms 2000+3000 → 总 5000 平均 2500 最大 3000
		// toolB: 4 次 3 失败, ms 100×4 → 总 400 平均 100
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":2000,\"ok\":true,\"argsN\":1}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":3000,\"ok\":true,\"argsN\":1}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_b\",\"ms\":100,\"ok\":false,\"argsN\":2,\"err\":\"E1\"}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_b\",\"ms\":100,\"ok\":false,\"argsN\":2,\"err\":\"E1\"}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_b\",\"ms\":100,\"ok\":false,\"argsN\":2,\"err\":\"E1\"}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_b\",\"ms\":100,\"ok\":true,\"argsN\":2}");

		string report = ToolUsage.Report(0, "calls", byDay: false);

		int idxA = report.IndexOf("se_test_a", StringComparison.Ordinal);
		int idxB = report.IndexOf("se_test_b", StringComparison.Ordinal);
		Assert.True(idxA >= 0 && idxB >= 0);
		Assert.True(idxB < idxA, "按次数降序:4 次的 toolB 应排在 2 次的 toolA 之前");
		Assert.Contains("2500", report); // toolA 平均
		Assert.Contains("3000", report); // toolA 最大
	}

	[Fact]
	public void Report_排序模式_ms与fail改变顺序()
	{
		// toolA: 2 次成功但总耗时 5000;toolB: 4 次失败但总耗时 400
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":2500,\"ok\":true,\"argsN\":1}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":2500,\"ok\":true,\"argsN\":1}");
		for (int i = 0; i < 4; i++)
		{
			AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_b\",\"ms\":100,\"ok\":false,\"argsN\":1,\"err\":\"E1\"}");
		}

		// ms 排序:toolA 总耗时 5000 > toolB 400 → A 在前
		string byMs = ToolUsage.Report(0, "ms", byDay: false);
		Assert.True(byMs.IndexOf("se_test_a", StringComparison.Ordinal) < byMs.IndexOf("se_test_b", StringComparison.Ordinal),
			"ms 排序下 toolA 应在前");

		// calls 排序:toolB 4 次 > toolA 2 次 → B 在前
		string byCalls = ToolUsage.Report(0, "calls", byDay: false);
		Assert.True(byCalls.IndexOf("se_test_b", StringComparison.Ordinal) < byCalls.IndexOf("se_test_a", StringComparison.Ordinal),
			"calls 排序下 toolB 应在前");

		// fail 排序:toolB 4 败 > toolA 0 败 → B 在前
		string byFail = ToolUsage.Report(0, "fail", byDay: false);
		Assert.True(byFail.IndexOf("se_test_b", StringComparison.Ordinal) < byFail.IndexOf("se_test_a", StringComparison.Ordinal),
			"fail 排序下 toolB 应在前");
	}

	[Fact]
	public void Report_按天汇总_按日期分组计数()
	{
		string today = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
		string yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");
		AppendLine("{\"ts\":\"" + today + "\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":1,\"ok\":true,\"argsN\":0}");
		AppendLine("{\"ts\":\"" + today + "\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":1,\"ok\":true,\"argsN\":0}");
		AppendLine("{\"ts\":\"" + yesterday + "\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":1,\"ok\":true,\"argsN\":0}");

		string report = ToolUsage.Report(0, "calls", byDay: true);

		string todayKey = DateTime.Now.ToString("yyyy-MM-dd");
		string yesterdayKey = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
		int idxToday = report.IndexOf(todayKey, StringComparison.Ordinal);
		int idxYesterday = report.IndexOf(yesterdayKey, StringComparison.Ordinal);
		Assert.True(idxToday >= 0 && idxYesterday >= 0, "两个日期都应出现");
		Assert.True(idxYesterday < idxToday, "按天排序:昨日应早于今日");
	}

	[Fact]
	public void Report_坏行跳过_不影响好行统计()
	{
		AppendLine("这不是 JSON");
		AppendLine("");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":1,\"ok\":true,\"argsN\":0}");

		string report = ToolUsage.Report(0, "calls", byDay: false);

		Assert.Contains("原始记录: 1 行", report);
		Assert.Contains("se_test_a", report);
	}

	[Fact]
	public void Report_时间窗口_旧记录与新解析失败的ts被过滤()
	{
		AppendLine("{\"ts\":\"2000-01-01 00:00:00\",\"src\":\"mcp\",\"tool\":\"se_test_old\",\"ms\":1,\"ok\":true,\"argsN\":0}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_new\",\"ms\":1,\"ok\":true,\"argsN\":0}");
		AppendLine("{\"ts\":\"不是时间\",\"src\":\"mcp\",\"tool\":\"se_test_badt\",\"ms\":1,\"ok\":true,\"argsN\":0}");

		// 最近 7 天:只有 se_test_new(旧时间与解析失败→MinValue 均被 cutoff 排除)
		string recent = ToolUsage.Report(7, "calls", byDay: false);
		Assert.Contains("原始记录: 3 行", recent);          // 原始行数仍全量
		Assert.DoesNotContain("se_test_old", recent);
		Assert.DoesNotContain("se_test_badt", recent);
		Assert.Contains("se_test_new", recent);

		// 全部窗口(days<=0):三条都进窗口
		string all = ToolUsage.Report(0, "calls", byDay: false);
		Assert.Contains("se_test_old", all);
		Assert.Contains("se_test_badt", all);
	}

	[Fact]
	public void Report_未调用清单_排除已用工具()
	{
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_get_document\",\"ms\":1,\"ok\":true,\"argsN\":0}");

		string report = ToolUsage.Report(7, "calls", byDay: false);

		// 已调用的 se_get_document 不出现在"从未调用"清单;其它工具仍列出
		int neverIdx = report.IndexOf("从未调用的工具", StringComparison.Ordinal);
		Assert.True(neverIdx >= 0);
		string neverSection = report.Substring(neverIdx);
		Assert.DoesNotContain("se_get_document", neverSection);
		Assert.Contains("se_get_selection", neverSection);
	}

	// ---------- P2-0:失败原因 Top N 与 errText 头尾截断(2026-11-13) ----------

	[Fact]
	public void Report_失败原因TopN_按结构化status归类排序()
	{
		// 3 条 status:error(不同消息头)、2 条 compile_error、1 条无响应
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_a\",\"ms\":1,\"ok\":false,\"argsN\":1,\"err\":\"toolStatusError:第 1 个特征未建成\",\"errText\":\"{\\\"status\\\":\\\"error\\\",\\\"message\\\":\\\"x\\\"}\"}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_a\",\"ms\":1,\"ok\":false,\"argsN\":1,\"err\":\"toolStatusError:第 1 个特征未建成\",\"errText\":\"{\\\"status\\\":\\\"error\\\",\\\"message\\\":\\\"x\\\"}\"}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_a\",\"ms\":1,\"ok\":false,\"argsN\":1,\"err\":\"toolStatusError:第 2 个特征未建成\",\"errText\":\"{\\\"status\\\":\\\"error\\\",\\\"message\\\":\\\"x\\\"}\"}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_b\",\"ms\":1,\"ok\":false,\"argsN\":1,\"err\":\"toolStatusError:compile_error\",\"errText\":\"{\\\"status\\\":\\\"compile_error\\\",\\\"compileErrors\\\":[{\\\"code\\\":\\\"CS1510\\\",\\\"message\\\":\\\"ref 或 out 参数必须是可赋值\\\"}],\\\"error CS1510 占位\\\":1}\"}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_b\",\"ms\":1,\"ok\":false,\"argsN\":1,\"err\":\"toolStatusError:compile_error\",\"errText\":\"{\\\"status\\\":\\\"compile_error\\\",\\\"compileErrors\\\":[{\\\"code\\\":\\\"CS1002\\\",\\\"message\\\":\\\"应输入;\\\"}]\"}");
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_c\",\"ms\":1,\"ok\":false,\"argsN\":0,\"err\":\"noResponse\"}");

		string report = ToolUsage.Report(0, "calls", byDay: false);

		Assert.Contains("失败原因 Top N", report);
		Assert.Contains("窗口内失败 6 条", report);
		// error 桶(3 条)应排最前,且消息首段进入标签
		int idxErr = report.IndexOf("error:第 1 个特征未建成", StringComparison.Ordinal);
		int idxCs = report.IndexOf("CS1510", StringComparison.Ordinal);
		int idxNoResp = report.IndexOf("无响应", StringComparison.Ordinal);
		Assert.True(idxErr >= 0, "status:error 应按消息首段归类");
		Assert.True(idxCs > 0, "compile_error 应带首个 CS 码");
		Assert.True(idxNoResp > 0, "noResponse 应单独成桶");
		Assert.True(idxErr < idxCs && idxCs < idxNoResp, "按条数降序:3 > 2 > 1");
	}

	[Fact]
	public void Report_无失败_不输出TopN段()
	{
		AppendLine("{\"ts\":\"{NOW}\",\"src\":\"mcp\",\"tool\":\"se_test_a\",\"ms\":1,\"ok\":true,\"argsN\":0}");
		string report = ToolUsage.Report(0, "calls", byDay: false);
		Assert.Contains("窗口内没有失败记录", report);
	}

	[Fact]
	public void Record_errText超长_头尾保留中段标注()
	{
		string head = new string('H', 120);
		string mid = new string('M', 500);
		string tail = new string('T', 260);
		ToolUsage.Record("mcp", "se_x", 1, false, "toolStatusError", 0, head + mid + tail);

		string line = AssertSingleLine();
		using var doc = System.Text.Json.JsonDocument.Parse(line);
		string errText = doc.RootElement.GetProperty("errText").GetString();

		Assert.StartsWith(head, errText);
		Assert.EndsWith(tail, errText);
		Assert.Contains("中略500字", errText);
		Assert.DoesNotContain(new string('M', 10), errText);   // 中段确实被省略
		Assert.True(errText.Length < 120 + 260 + 30, "总长应约为头+尾+标注");
	}

	// ---------- helpers ----------

	private void AppendLine(string line)
	{
		Directory.CreateDirectory(_tempDir);
		File.AppendAllText(ToolUsage.LogPath, line.Replace("{NOW}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + Environment.NewLine, new UTF8Encoding(false));
	}

	private string AssertSingleLine()
	{
		string[] lines = File.ReadAllLines(ToolUsage.LogPath);
		Assert.Single(lines);
		return lines[0];
	}
}
