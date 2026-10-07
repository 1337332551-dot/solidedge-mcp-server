using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SolidEdge.Spy.McpServer.Tools;

namespace SolidEdge.Spy.McpServer;

/// <summary>
/// 权限洋葱外层:挂在 MCP stdin 传输上,按 模式×档位 拦截 tools/call(见 ToolRisk)。
///
/// 用法(Program.cs,包在 JsonRpcTap 外面,响应侧 tap 同时作拒绝响应的汇入点):
/// <code>
/// var stdoutTap = new Telemetry.JsonRpcTap(Console.OpenStandardOutput(), isRequestSide: false, source: "mcp");
/// .WithStreamServerTransport(
///     new PermissionTap(new Telemetry.JsonRpcTap(Console.OpenStandardInput(), true, "mcp"), stdoutTap),
///     stdoutTap)
/// </code>
///
/// 原理:
/// - 逐行扫描请求流;完整解析出 tools/call 的工具名 → ToolRisk.Check。
/// - 放行:原始字节原样下发(不做任何改写,协议零干扰)。
/// - 拒绝:该行**不进 SDK**(工具体完全不执行),直接向响应流伪造一条
///   JSON-RPC result(isError=true, text=拒绝原因+切换指路)。AI 能看到原因。
/// - 拒绝同时落 mcp-audit.log(AuditLog),与工具内拒绝同一份审计。
///
/// 铁律:
/// ① 解析失败/无 id 通知/结构不完整 → 一律透传交 SDK(权限只拦"确认为 tools/call 且判定拒绝"的行,
///    绝不因自己解析不了而吞协议行——那会卡死握手);
/// ② 逐行字节缓冲,换行切分,超长行(>2MB)按字节透传保字节一致;
/// ③ 读方向覆写 Read/ReadAsync(byte[]) 两个重载(Stream 基类默认实现会委托到 byte[] 重载);
/// ④ 任何拦截侧异常不得影响透传主流程。
/// </summary>
internal sealed class PermissionTap : Stream
{
	private readonly Stream _inner;         // stdin(或包在其内的请求侧 tap)
	private readonly Stream _respondSink;   // 响应侧出口(写拒绝结果用)
	private readonly RestorePointHook _restorePointHook; // 自动还原点 hook(可空:单测/未接线场景)
	private readonly List<byte> _line = new List<byte>(8192);
	private readonly Queue<byte[]> _outgoing = new Queue<byte[]>(); // 已放行的完整行,等 SDK 来读
	private int _headOffset;                // _outgoing 队头行的已消费偏移
	private bool _sawEof;
	private bool _oversize;                 // 超长垃圾行标记:直接按字节透传到下一个 '\n'
	private const int MaxLineBytes = 2000000;

	internal PermissionTap(Stream inner, Stream respondSink, RestorePointHook restorePointHook = null)
	{
		_inner = inner ?? throw new ArgumentNullException("inner");
		_respondSink = respondSink ?? throw new ArgumentNullException("respondSink");
		_restorePointHook = restorePointHook;
	}

	// ---------------- 读方向:缓冲 + 逐行过滤 ----------------

	public override int Read(byte[] buffer, int offset, int count)
	{
		return ReadCore(buffer, offset, count, async: false, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
	}

	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		return await ReadCore(buffer, offset, count, async: true, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private async Task<int> ReadCore(byte[] buffer, int offset, int count, bool async, CancellationToken cancellationToken)
	{
		if (count > 0)
		{
			while (_outgoing.Count == 0 && !_sawEof)
			{
				byte[] chunk = new byte[16384];
				int n = async
					? await _inner.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)
					: _inner.Read(chunk, 0, chunk.Length);
				if (n <= 0)
				{
					_sawEof = true;
					if (_line.Count > 0)
					{
						byte[] tail = _line.ToArray();
						_line.Clear();
						ProcessLine(tail);
					}
					break;
				}
				Feed(chunk, n);
			}
		}
		return Drain(buffer, offset, count);
	}

	private void Feed(byte[] chunk, int count)
	{
		for (int i = 0; i < count; i++)
		{
			byte b = chunk[i];
			if (_oversize)
			{
				// 超长垃圾行:逐字节透传,保字节一致(罕见路径,正确性优先)
				_outgoing.Enqueue(new byte[] { b });
				if (b == (byte)'\n')
				{
					_oversize = false;
				}
				continue;
			}
			if (b == (byte)'\n')
			{
				_line.Add(b); // 保留换行,透传时字节与原始流完全一致
				byte[] rawLine = _line.ToArray();
				_line.Clear();
				ProcessLine(rawLine);
				continue;
			}
			_line.Add(b);
			if (_line.Count > MaxLineBytes)
			{
				FlushOversize();
			}
		}
	}

	private void FlushOversize()
	{
		_outgoing.Enqueue(_line.ToArray());
		_line.Clear();
		_oversize = true;
	}

	private void ProcessLine(byte[] rawLine)
	{
		try
		{
			string text = Encoding.UTF8.GetString(rawLine);
			if (text.IndexOf("\"tools/call\"", StringComparison.Ordinal) >= 0 && TryIntercept(text))
			{
				return; // 已拒绝并写了伪造响应,原始行不再下发
			}
		}
		catch
		{
			// 解析/拦截侧异常:按透传处理,绝不吞协议行
		}
		_outgoing.Enqueue(rawLine);
	}

	/// <summary>true=已拦截(拒绝并写了响应);false=放行(调用方透传原始行)。</summary>
	private bool TryIntercept(string line)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(line);
		}
		catch
		{
			return false;
		}
		using (doc)
		{
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return false;
			}
			JsonElement methodEl;
			if (!root.TryGetProperty("method", out methodEl) || methodEl.ValueKind != JsonValueKind.String
				|| methodEl.GetString() != "tools/call")
			{
				return false;
			}
			JsonElement idEl;
			if (!root.TryGetProperty("id", out idEl))
			{
				return false; // 无 id 的通知:交 SDK 按协议处理
			}
			string idJson = idEl.GetRawText();
			JsonElement prms;
			if (!root.TryGetProperty("params", out prms) || prms.ValueKind != JsonValueKind.Object)
			{
				return false;
			}
			JsonElement nameEl;
			if (!prms.TryGetProperty("name", out nameEl) || nameEl.ValueKind != JsonValueKind.String)
			{
				return false;
			}
			string tool = nameEl.GetString();
			if (string.IsNullOrEmpty(tool))
			{
				return false;
			}
			string deny = ToolRisk.Check(tool);
			if (deny == null)
			{
				// 自动还原点 hook:权限放行后、工具体执行前(2026-10-06)。
				// best-effort:hook 自身不抛异常,这里再兜一层——还原点任何问题绝不阻断工具调用。
				if (_restorePointHook != null)
				{
					try { _restorePointHook.BeforeWrite(tool, prms); }
					catch { }
				}
				return false;
			}
			WriteDeny(idJson, tool, deny);
			AuditLog.Write(tool, "-", "(tools/call)", false, "-", InvocationRisk.ModelChanging, false,
				"denied by permission layer, mode=" + ToolRisk.Mode);
			return true;
		}
	}

	// 拒绝文案含中文,用宽松编码器避免 \uXXXX 转义(生成的是 JSON 字符串字面量,无 HTML 注入面)
	private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	/// <summary>向响应流伪造一条 tools/call 的失败 result(isError=true),AI 可读到拒绝原因与指路。</summary>
	private void WriteDeny(string idJson, string tool, string reason)
	{
		string payload = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":"
			+ JsonSerializer.Serialize(reason + " [tool=" + tool + "]", JsonOpts) + "}]}}\n";
		byte[] bytes = Encoding.UTF8.GetBytes(payload);
		_respondSink.Write(bytes, 0, bytes.Length);
		_respondSink.Flush();
	}

	/// <summary>把放行队列的内容拷进调用方缓冲。</summary>
	private int Drain(byte[] buffer, int offset, int count)
	{
		int total = 0;
		while (total < count && _outgoing.Count > 0)
		{
			byte[] head = _outgoing.Peek();
			int take = Math.Min(head.Length - _headOffset, count - total);
			Buffer.BlockCopy(head, _headOffset, buffer, offset + total, take);
			_headOffset += take;
			total += take;
			if (_headOffset >= head.Length)
			{
				_outgoing.Dequeue();
				_headOffset = 0;
			}
		}
		return total;
	}

	// ---------------- 其余 Stream 成员:只读流 ----------------

	public override void Flush()
	{
	}

	public override Task FlushAsync(CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}

	public override bool CanRead
	{
		get { return true; }
	}

	public override bool CanSeek
	{
		get { return false; }
	}

	public override bool CanWrite
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

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException("PermissionTap 只包请求方向(读),响应走独立的 stdout tap。");
	}

	protected override void Dispose(bool disposing)
	{
		// 不 dispose 内层流(Console 标准流由进程持有)
		base.Dispose(disposing);
	}
}
