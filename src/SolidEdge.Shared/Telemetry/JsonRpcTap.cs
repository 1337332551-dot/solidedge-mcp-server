using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SolidEdge.Spy.McpServer.Telemetry;

/// <summary>
/// stdio 传输层的 JSON-RPC 旁路记录器:边透传边统计工具调用,不侵入任何工具代码。
///
/// 用法(替换 WithStdioServerTransport):
/// <code>
/// .WithStreamServerTransport(
///     new JsonRpcTap(Console.OpenStandardInput(),  isRequestSide: true,  source: "mcp"),
///     new JsonRpcTap(Console.OpenStandardOutput(), isRequestSide: false, source: "mcp"))
/// </code>
///
/// 原理:MCP stdio 是"换行分隔的 JSON-RPC"。
/// - 读方向(请求):抓 method=="tools/call" → 记 {tool, argsN, 起始时刻} 进内存表(按 id);
/// - 写方向(响应):用 id 配回内存表 → 算耗时/成功失败 → 追加一行到 tool-usage.jsonl。
///
/// 铁律:
/// ① 同步与异步读写都要覆写(Stream 的 <c>ReadAsync(Memory&lt;byte&gt;)</c> / <c>WriteAsync(ReadOnlyMemory&lt;byte&gt;)</c>
///    默认实现会委托给 byte[] 重载,故覆写 4 个即可覆盖全部路径);
/// ② 按 '\n' 切行并保留跨块残余,UTF-8 用 Decoder 增量解码(多字节字符可能跨块);
/// ③ stdout 是协议通道——本类**绝不**向流里打印任何日志,统计只落文件;
/// ④ 任何解析/记录异常一律吞掉,计量失败不影响调用。
/// </summary>
internal sealed class JsonRpcTap : Stream
{
	private sealed class PendingCall
	{
		internal string Tool;
		internal int ArgsCount;
		internal long StartTicks;
	}

	private readonly Stream _inner;
	private readonly bool _isRequestSide;
	private readonly string _source;
	private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
	private readonly char[] _charBuf = new char[8192];
	private readonly StringBuilder _pending = new StringBuilder();
	// ⚠️ 请求方向(stdin)与响应方向(stdout)是**两个不同的实例**,
	// 待配对表必须静态共享(同一进程内),否则请求记在 A 实例、响应在 B 实例里查,永远配不上(实测踩过)。
	private static readonly Dictionary<string, PendingCall> Calls = new Dictionary<string, PendingCall>(StringComparer.Ordinal);
	private static readonly object CallsLock = new object();
	private const int MaxPendingChars = 2000000;

	// L02（2026-09-21）：包装型工具（se_script_run / se_recipe_run 等）把业务失败包成
	// 正常 result，失败语义藏在 "status":"<X>" 文本里。命中任一标记 → 计量为失败。
	private static readonly string[] FailureStatusMarkers =
	{
		"\"status\":\"error\"",
		"\"status\":\"compile_error\"",
		"\"status\":\"exit_nonzero\"",
		"\"status\":\"timeout\"",
		// se_assembly_build 部分失败时外层是 {"status":"partial",...},失败明细在内层
		// results[].status:"error"。之前只靠内层 error 侥幸命中——哪天 partial 里没有
		// error 子项就会静默漏判,所以这里显式登记。
		"\"status\":\"partial\"",
	};

	// 排障开关:环境变量 SE_MCP_TAP_DEBUG=1 时,把旁路观察到的行写 tap-debug.log(最多 500 行)。
	// 用于定位"统计没落盘"这类问题:能看出流是否被读到、行内容是否符合预期。
	private static readonly bool DebugTrace = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SE_MCP_TAP_DEBUG"));
	private static readonly object TraceSync = new object();
	private static int _traceCount;

	private static void Trace(string message)
	{
		if (!DebugTrace || _traceCount > 500)
		{
			return;
		}
		try
		{
			lock (TraceSync)
			{
				_traceCount++;
				string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidEdgeSpy", "tap-debug.log");
				string dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				{
					Directory.CreateDirectory(dir);
				}
				File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine, new UTF8Encoding(false));
			}
		}
		catch
		{
		}
	}

	internal JsonRpcTap(Stream inner, bool isRequestSide, string source)
	{
		_inner = inner ?? throw new ArgumentNullException("inner");
		_isRequestSide = isRequestSide;
		_source = string.IsNullOrEmpty(source) ? "mcp" : source;
	}

	// ---------------- 透传读写(带扫描) ----------------

	public override int Read(byte[] buffer, int offset, int count)
	{
		int n = _inner.Read(buffer, offset, count);
		if (n > 0)
		{
			Scan(buffer, offset, n);
		}
		return n;
	}

	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		int n = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
		if (n > 0)
		{
			Scan(buffer, offset, n);
		}
		return n;
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		if (count > 0)
		{
			Scan(buffer, offset, count);
		}
		_inner.Write(buffer, offset, count);
	}

	public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		if (count > 0)
		{
			Scan(buffer, offset, count);
		}
		await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
	}

	public override void Flush()
	{
		_inner.Flush();
	}

	public override Task FlushAsync(CancellationToken cancellationToken)
	{
		return _inner.FlushAsync(cancellationToken);
	}

	public override bool CanRead
	{
		get { return _inner.CanRead; }
	}

	public override bool CanWrite
	{
		get { return _inner.CanWrite; }
	}

	public override bool CanSeek
	{
		get { return false; }
	}

	public override long Length
	{
		get { throw new NotSupportedException(); }
	}

	public override long Position
	{
		get { throw new NotSupportedException(); }
		set { throw new NotSupportedException(); }
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		throw new NotSupportedException();
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	protected override void Dispose(bool disposing)
	{
		// 不 dispose 内层 stdio 流(Console 标准流由进程持有),避免影响协议通道;
		// 退出前把残余未配对的调用补记一次(标记为未完成),防止统计漏掉长调用。
		if (disposing)
		{
			FlushPendingAsUnknown();
		}
		base.Dispose(disposing);
	}

	// ---------------- 扫描与解析 ----------------

	private void Scan(byte[] buffer, int offset, int count)
	{
		try
		{
			int chars = _decoder.GetChars(buffer, offset, count, _charBuf, 0);
			for (int i = 0; i < chars; i++)
			{
				char c = _charBuf[i];
				if (c == '\n')
				{
					string line = _pending.ToString();
					_pending.Length = 0;
					HandleLine(line);
				}
				else if (c != '\r')
				{
					if (_pending.Length < MaxPendingChars)
					{
						_pending.Append(c);
					}
				}
			}
		}
		catch
		{
		}
	}

	private void HandleLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}
		Trace((_isRequestSide ? "IN  " : "OUT ") + ((line.Length > 200) ? line.Substring(0, 200) : line));
		try
		{
			if (_isRequestSide)
			{
				HandleRequest(line);
			}
			else
			{
				HandleResponse(line);
			}
		}
		catch
		{
		}
	}

	private void HandleRequest(string line)
	{
		if (line.IndexOf("\"tools/call\"", StringComparison.Ordinal) < 0)
		{
			return;
		}
		using JsonDocument doc = JsonDocument.Parse(line);
		JsonElement root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		JsonElement method;
		if (!root.TryGetProperty("method", out method) || method.ValueKind != JsonValueKind.String || method.GetString() != "tools/call")
		{
			return;
		}
		JsonElement idEl;
		if (!root.TryGetProperty("id", out idEl))
		{
			return;
		}
		string id = IdKey(idEl);
		if (id == null)
		{
			return;
		}
		string tool = "?";
		int argsN = 0;
		JsonElement prms;
		if (root.TryGetProperty("params", out prms) && prms.ValueKind == JsonValueKind.Object)
		{
			JsonElement nameEl;
			if (prms.TryGetProperty("name", out nameEl) && nameEl.ValueKind == JsonValueKind.String)
			{
				tool = nameEl.GetString();
			}
			JsonElement args;
			if (prms.TryGetProperty("arguments", out args) && args.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty _ in args.EnumerateObject())
				{
					argsN++;
				}
			}
		}
		PendingCall call = new PendingCall();
		call.Tool = tool;
		call.ArgsCount = argsN;
		call.StartTicks = Stopwatch.GetTimestamp();
		lock (CallsLock)
		{
			Calls[id] = call;
		}
	}

	private void HandleResponse(string line)
	{
		if (line.IndexOf("\"id\"", StringComparison.Ordinal) < 0)
		{
			return;
		}
		using JsonDocument doc = JsonDocument.Parse(line);
		JsonElement root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		JsonElement idEl;
		if (!root.TryGetProperty("id", out idEl))
		{
			return;
		}
		string id = IdKey(idEl);
		if (id == null)
		{
			return;
		}
		PendingCall call = null;
		lock (CallsLock)
		{
			if (Calls.TryGetValue(id, out call))
			{
				Calls.Remove(id);
			}
		}
		if (call == null)
		{
			// 不是工具调用的响应(initialize / tools/list / ping 等),不计量
			return;
		}
		bool ok = true;
		string err = null;
		string errText = null;    // L14（2026-09-21）：失败原文，截 300 落进 tool-usage
		JsonElement errorEl;
		if (root.TryGetProperty("error", out errorEl) && errorEl.ValueKind == JsonValueKind.Object)
		{
			ok = false;
			JsonElement codeEl;
			if (errorEl.TryGetProperty("code", out codeEl))
			{
				err = (codeEl.ValueKind == JsonValueKind.Number) ? codeEl.ToString() : null;
			}
			JsonElement protoMsgEl;
			if (errorEl.TryGetProperty("message", out protoMsgEl) && protoMsgEl.ValueKind == JsonValueKind.String)
			{
				err = (err == null ? "protocol" : err) + ":" + TruncateMessage(protoMsgEl.GetString());
			}
			if (err == null)
			{
				err = "protocol";
			}
			JsonElement msgEl;
			if (errorEl.TryGetProperty("message", out msgEl) && msgEl.ValueKind == JsonValueKind.String)
			{
				errText = msgEl.GetString();
			}
		}
		else
		{
			JsonElement resultEl;
			if (root.TryGetProperty("result", out resultEl) && resultEl.ValueKind == JsonValueKind.Object)
			{
				JsonElement isErrEl;
				if (resultEl.TryGetProperty("isError", out isErrEl) && isErrEl.ValueKind == JsonValueKind.True)
				{
					ok = false;
					err = "toolError:" + TruncateMessage(FirstContentText(resultEl));
					errText = FirstContentText(resultEl);
				}
				else
				{
					// 本仓库的工具把业务错误包装成正常 result(文本里是 {"status":"error",...}),
					// 不标 isError。故补一条启发式:content[0].text 命中失败 status → 记为失败。
					// L02（2026-09-21）：失败 status 集合从 {"error"} 扩到
					//   {error, compile_error, exit_nonzero, timeout} ——
					//   se_script_run/se_recipe_run 把编译失败/退出码非0/超时都包成正常
					//   result，导致 78 条假成功（audit 93 实败 vs tool-usage 全记 ok:true）。
					string text = FirstContentText(resultEl);
					if (text != null)
					{
						foreach (string badStatus in FailureStatusMarkers)
						{
							if (text.IndexOf(badStatus, StringComparison.Ordinal) >= 0)
							{
								ok = false;
								err = "toolStatusError:" + TruncateMessage(ExtractErrorMessage(text));
								errText = text;
								break;
							}
						}
					}
				}
			}
		}
		long ms = (Stopwatch.GetTimestamp() - call.StartTicks) * 1000L / Stopwatch.Frequency;
		ToolUsage.Record(_source, call.Tool, ms, ok, err, call.ArgsCount, errText);
	}

	/// <summary>进程/传输结束前,把仍未配对的调用记一次(ok=false, err=noResponse),避免超时/取消的调用不留痕。</summary>
	private void FlushPendingAsUnknown()
	{
		try
		{
			List<PendingCall> leftovers;
			lock (CallsLock)
			{
				leftovers = new List<PendingCall>(Calls.Values);
				Calls.Clear();
			}
			foreach (PendingCall call in leftovers)
			{
				long ms = (Stopwatch.GetTimestamp() - call.StartTicks) * 1000L / Stopwatch.Frequency;
				ToolUsage.Record(_source, call.Tool, ms, false, "noResponse", call.ArgsCount);
			}
		}
		catch
		{
		}
	}

	/// <summary>截断错误消息到 100 字符并压平换行(保持 JSONL 单行)。</summary>
	private static string TruncateMessage(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		s = s.Replace('\r', ' ').Replace('\n', ' ');
		return s.Length > 100 ? s.Substring(0, 100) : s;
	}

	/// <summary>从业务错误文本({"status":"error","message":"..."})提取 message;非 JSON 时返回原文。</summary>
	private static string ExtractErrorMessage(string text)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(text);
			if (doc.RootElement.ValueKind == JsonValueKind.Object &&
				doc.RootElement.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String)
			{
				return m.GetString();
			}
		}
		catch
		{
		}
		return text;
	}

	/// <summary>取 result.content 里第一个 text 块的内容(用于识别被包装成正常结果的业务错误)。</summary>
	private static string FirstContentText(JsonElement resultEl)
	{
		JsonElement contentEl;
		if (!resultEl.TryGetProperty("content", out contentEl) || contentEl.ValueKind != JsonValueKind.Array)
		{
			return null;
		}
		foreach (JsonElement item in contentEl.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object)
			{
				continue;
			}
			JsonElement textEl;
			if (item.TryGetProperty("text", out textEl) && textEl.ValueKind == JsonValueKind.String)
			{
				return textEl.GetString();
			}
		}
		return null;
	}

	private static string IdKey(JsonElement idEl)
	{
		switch (idEl.ValueKind)
		{
		case JsonValueKind.Number:
			return "#" + idEl.GetRawText();
		case JsonValueKind.String:
			return "s" + idEl.GetString();
		default:
			return null;
		}
	}
}
