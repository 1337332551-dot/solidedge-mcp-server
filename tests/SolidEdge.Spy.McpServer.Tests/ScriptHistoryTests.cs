using System;
using System.IO;
using System.Text;
using SolidEdge.Spy.McpServer.Telemetry;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests;

/// <summary>
/// ScriptSig 指纹与 ScriptHistory 留档测试(跨会话重复检测的数据源,指纹失真 = 沉淀候选误报/漏报)。
/// 通过 LogPath internal 覆盖重定向到临时目录,不碰真实 %LOCALAPPDATA%。
/// </summary>
public sealed class ScriptHistoryTests : IDisposable
{
	private readonly string _tempDir;

	public ScriptHistoryTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "sespy-scripthistory-tests", Guid.NewGuid().ToString("N"));
		ScriptHistory.SetLogPathOverride(Path.Combine(_tempDir, "script-history.jsonl"));
	}

	public void Dispose()
	{
		ScriptHistory.SetLogPathOverride(null);
		try { Directory.Delete(_tempDir, true); } catch { /* 尽力清理 */ }
	}

	// ---------- ScriptSig:归一化 ----------

	[Fact]
	public void Sig_同一逻辑不同变量名注释与字符串_指纹相同()
	{
		string a = (@"
using System;
// 遍历所有面
class P { static void Main() {
  var app = (SolidEdgeFramework.Application)System.Runtime.InteropServices.Marshal.GetActiveObject(""SolidEdge.Application"");
  var doc = app.ActiveDocument;
  Console.WriteLine(""hello"");
} }");
		string b = (@"
using System;
/* 另一个会话里重写的版本 */
class Q { static void Main() {
  var se = (SolidEdgeFramework.Application)System.Runtime.InteropServices.Marshal.GetActiveObject(""SolidEdge.OTHER"");
  var d = se.ActiveDocument;
  Console.WriteLine(""world 别的字符串"");
} }");
		ScriptSigResult ra = ScriptSig.Compute(a);
		ScriptSigResult rb = ScriptSig.Compute(b);
		Assert.Equal(ra.Hash, rb.Hash);
		Assert.Equal(ra.Members, rb.Members);
	}

	[Fact]
	public void Sig_成员集合不同_指纹不同()
	{
		string a = ("var x = doc.Faces; var y = x.Item(1);");
		string b = ("var x = doc.Models; var y = x.Item(1);");
		Assert.NotEqual(ScriptSig.Compute(a).Hash, ScriptSig.Compute(b).Hash);
	}

	[Fact]
	public void Sig_噪声成员_不进签名()
	{
		string code = ("Console.WriteLine(a.ToString().Length); Console.WriteLine(b.Count); doc.Faces;");
		ScriptSigResult r = ScriptSig.Compute(code);
		Assert.Equal("Faces", r.Members);
	}

	[Fact]
	public void Sig_注释与字符串里的成员名_不进签名()
	{
		string code = ("// 注释里有 .Faces 和 .Models\nvar s = \".Edges 在字符串里\"; doc.Colors;");
		ScriptSigResult r = ScriptSig.Compute(code);
		Assert.Equal("Colors", r.Members);
	}

	[Fact]
	public void Sig_空代码_空指纹不抛异常()
	{
		ScriptSigResult r = ScriptSig.Compute(null);
		Assert.Null(r.Hash);
		Assert.Null(r.Members);
		r = ScriptSig.Compute("");
		Assert.Null(r.Hash);
		Assert.Null(r.Members);
	}

	[Fact]
	public void Sig_注释里有双斜杠网址_不算行注释把后续成员吞掉()
	{
		// 网址在字符串里应被剥成空格;真正的行注释后的成员不应进签名
		string code = ("var u = \"http://x/y\"; doc.Faces; // doc.Models");
		Assert.Equal("Faces", ScriptSig.Compute(code).Members);
	}

	[Fact]
	public void Sig_逐字字符串双引号转义_不提前终止剥离()
	{
		string code = ("var p = @\"a\"\"b\"; doc.Faces;");
		Assert.Equal("Faces", ScriptSig.Compute(code).Members);
	}

	// ---------- Record:写入格式 ----------

	[Fact]
	public void Record_写出全部字段且全文代码可完整读回()
	{
		string code = ("class P { static void Main() {\n  doc.Faces; // 中文注释\n  var s = \"含\"\"引号\";\n} }");
		ScriptHistory.Record("probe_faces", "inline", code, "ok", 0, 123);

		string line = AssertSingleLine();
		using var doc = System.Text.Json.JsonDocument.Parse(line);
		var root = doc.RootElement;

		Assert.Equal("probe_faces", root.GetProperty("name").GetString());
		Assert.Equal("inline", root.GetProperty("src").GetString());
		Assert.Equal("ok", root.GetProperty("status").GetString());
		Assert.Equal(0, root.GetProperty("exit").GetInt32());
		Assert.Equal(123, root.GetProperty("ms").GetInt64());
		Assert.True(root.GetProperty("sig").GetString().Length == 8, "指纹应为 8 位十六进制");
		Assert.True(root.TryGetProperty("members", out _));
		// code 全文含换行/引号,转义后必须能无损读回(这是与 ToolUsage.Escape 的关键差异)
		Assert.Equal(code, root.GetProperty("code").GetString());
	}

	[Fact]
	public void Record_失败状态_不带exit字段()
	{
		ScriptHistory.Record("p8", "filePath:C:\\x\\y.cs", "doc.Faces;", "compile_error", null, 5);

		string line = AssertSingleLine();
		using var doc = System.Text.Json.JsonDocument.Parse(line);
		Assert.False(doc.RootElement.TryGetProperty("exit", out _), "compile_error 没有 exitCode,不应写 exit 字段");
		Assert.Equal("filePath:C:\\x\\y.cs", doc.RootElement.GetProperty("src").GetString());
	}

	[Fact]
	public void Record_目录不存在_自动创建()
	{
		Assert.False(Directory.Exists(_tempDir));
		ScriptHistory.Record("a", "inline", "doc.Faces;", "ok", 0, 1);
		Assert.True(File.Exists(ScriptHistory.LogPath));
	}

	[Fact]
	public void Record_代码为null_不抛异常且留空code()
	{
		ScriptHistory.Record("a", "inline", null, "error", null, 1);
		string line = AssertSingleLine();
		using var doc = System.Text.Json.JsonDocument.Parse(line);
		Assert.Equal("", doc.RootElement.GetProperty("code").GetString());
	}

	// ---------- Report:指纹聚合 ----------

	[Fact]
	public void Report_同指纹三次_标沉淀候选且计数正确()
	{
		string codeA1 = ("var a = doc.Faces; a.Item(1);");
		string codeA2 = ("var b = doc.Faces; b.Item(2);");   // 同指纹
		string codeB = ("var c = doc.Models; c.Item(1);");    // 不同指纹
		ScriptHistory.Record("probe", "inline", codeA1, "ok", 0, 10);
		ScriptHistory.Record("probe", "inline", codeA2, "ok", 0, 10);
		ScriptHistory.Record("probe", "inline", codeA1, "exit_nonzero", 1, 10);
		ScriptHistory.Record("other", "inline", codeB, "ok", 0, 10);

		string report = ScriptHistory.Report(0);
		Assert.Contains("记录: 4 条", report);
		Assert.Contains("沉淀候选", report);
		Assert.Contains("ok=3", report);
		Assert.Contains("exit_nonzero=1", report);
		// 两种形态,按次数排序,3 次的在前
		int idxTop = report.IndexOf("Faces");
		int idxOther = report.IndexOf("Models");
		Assert.True(idxTop >= 0 && idxOther >= 0 && idxTop < idxOther, "按次数降序:3 次的形态应排在 1 次的前面");
	}

	[Fact]
	public void Report_无记录_给出提示()
	{
		string report = ScriptHistory.Report(30);
		Assert.Contains("没有任何脚本执行记录", report);
	}

	[Fact]
	public void Report_坏行跳过不炸()
	{
		Directory.CreateDirectory(_tempDir);
		File.AppendAllText(ScriptHistory.LogPath, "{broken json\n", new UTF8Encoding(false));
		ScriptHistory.Record("ok_one", "inline", "doc.Faces;", "ok", 0, 1);

		string report = ScriptHistory.Report(0);
		Assert.Contains("ok_one", report);
	}

	private string AssertSingleLine()
	{
		string[] lines = File.ReadAllLines(ScriptHistory.LogPath, Encoding.UTF8);
		int nonEmpty = 0;
		string last = null;
		foreach (string l in lines)
		{
			if (!string.IsNullOrWhiteSpace(l))
			{
				nonEmpty++;
				last = l;
			}
		}
		Assert.Equal(1, nonEmpty);
		return last;
	}
}
