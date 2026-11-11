using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using SolidEdgeFramework;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 受控 C# 一次性脚本通道(se_script_run)。
/// 设计要点:
/// - 编译器用 Windows 自带的框架 csc(v4.0.30319),零安装;
///   脚本里 Marshal.GetActiveObject / [STAThread] 在 .NET Framework 下原样可用(net8 已删 GetActiveObject)。
/// - 脚本作为独立子进程运行,卡死/崩溃只死子进程,MCP server 毫发无损;
///   超时强杀整个进程树(Kill(entireProcessTree: true))。
/// - 互操作引用复用 server 部署目录里的合并版 Interop.SolidEdge.dll(net40 纯类型定义),
///   工具启动时自动拷进沙箱并加 /r:,杜绝手动拷 DLL。
/// - csc 不加 /noconfig,利用默认 csc.rsp 免费引用 System.dll/System.Core.dll 等基础程序集。
/// - 清理策略:exe/pdb/Interop.dll 跑完即删,.cs 源码保留(草稿纸,%TEMP% 由系统自动清理)。
/// </summary>
[McpServerToolType]
public static class ScriptTools
{
	/// <summary>脚本名称白名单:字母/数字/下划线/短横线,1~64 位,拒绝任何路径分隔符(防穿越)。</summary>
	private static readonly Regex NamePattern = new Regex("^[A-Za-z0-9_\\-]{1,64}$", RegexOptions.Compiled);

	/// <summary>输出上限(字符)。超出的部分截断并在 truncated=true 里如实报告。</summary>
	private const int MaxOutputChars = 64 * 1024;

	/// <summary>编译超时(秒)。</summary>
	private const int CompileTimeoutSeconds = 30;

	/// <summary>运行超时上限(秒)。</summary>
	private const int MaxRunTimeoutSeconds = 600;

	[McpServerTool]
	[Description("编译并运行一段一次性 C# 脚本(受控沙箱),用于控制流/批量遍历/复杂循环等链式调用不适合的任务" +
		"(把 240 次 round-trip 收敛成 1 次调用)。机制:源码写入 %TEMP%\\sespy\\<name>\\name.cs → 框架自带 csc" +
		"(.NET Framework v4.0.30319)编译 → 独立子进程运行 → 回传 stdout/stderr;崩溃/卡死只影响子进程," +
		"超时(timeoutSec,默认 60,上限 600)强杀整个进程树。\n" +
		"源码来源三选一(优先级):filePath(推荐,免 JSON 转义) > code(内联) > 都不给则复用沙箱里上次的 <name>.cs。\n" +
		"环境:①必须 C# 5 语法(不能用 $\"\" 插值/?.);②已自动引用 Interop.SolidEdge.dll 与默认程序集," +
		"可直接 using SolidEdgeFramework/SolidEdgeGeometry/SolidEdgePart/SolidEdgeDraft;" +
		"③连 SE 用 Marshal.GetActiveObject(\"SolidEdge.Application\"),Main 必须 [STAThread];" +
		"④建议设 Console.OutputEncoding=UTF8;⑤运行期间临时 DisplayAlerts=false,结束恢复;" +
		"⑥自动把 Main 改名 UserMain 并套 try/catch,未处理异常把完整堆栈打到 stderr 并以退出码 1 结束(结果见 crashGuard),用户逻辑不改。" +
		"输出:stdout/stderr 上限 64KB(超出 truncated=true),完整输出落盘到 <name>.log;编译产物跑完即删,.cs/.log 保留可复查。" +
		"SE_MCP_READONLY=1 时整体禁用。")]
	public static string se_script_run(
		SolidEdgeContext context,
		[Description("脚本名称,仅限字母/数字/下划线/短横线(1~64位)。用作沙箱子目录与编译产物名,同名会覆盖")] string name,
		[Description("C# 脚本完整源码(含 using 与 Main 入口,C# 5 语法)。与 filePath 二选一;两者都不提供时,复用沙箱里上次保存的 <name>.cs 原样重跑")] string code = null,
		[Description("可选:C# 源码文件全路径(长脚本强烈推荐:免 JSON 转义,改文件后重调本工具即重跑)。优先于 code。默认允许任意本地可读 .cs 绝对路径;设环境变量 SE_MCP_SCRIPT_ALLOW_FILE=0 则收紧为只允许沙箱内的 <name>.cs")] string filePath = null,
		[Description("运行超时秒数,默认 60,上限 600。超时强杀进程树并在结果里如实报告")] int timeoutSec = 60,
		[Description("可选:传给脚本 Main(string[] args) 的参数,如 [\"same=1\",\"out=C:\\\\x.csv\"]")] string[] scriptArgs = null)
	{
		// ---- 护栏:名称校验 ----
		if (string.IsNullOrWhiteSpace(name))
		{
			return Error("name 不能为空。");
		}
		name = name.Trim();
		if (!NamePattern.IsMatch(name))
		{
			return Error("name 非法:只允许字母/数字/下划线/短横线,长度 1~64,不含路径分隔符。收到: \"" + name + "\"");
		}
		if (timeoutSec <= 0)
		{
			timeoutSec = 60;
		}
		if (timeoutSec > MaxRunTimeoutSeconds)
		{
			timeoutSec = MaxRunTimeoutSeconds;
		}

		// ---- 沙箱准备(%TEMP%\sespy\<name>\,纯 ASCII 避开 GBK 坑) ----
		string sandbox;
		try
		{
			sandbox = Path.Combine(Path.GetTempPath(), "sespy", name);
			Directory.CreateDirectory(sandbox);
		}
		catch (Exception ex)
		{
			return Error("创建沙箱目录失败: " + ex.Message);
		}

		string csPath = Path.Combine(sandbox, name + ".cs");
		string exePath = Path.Combine(sandbox, name + ".exe");
		string logPath = Path.Combine(sandbox, name + ".log");
		// 运行次数台账:每真跑一次追加一行时间戳。用来在"第 2 次运行"时提醒固化成配方。
		string runsPath = Path.Combine(sandbox, name + ".runs");
		string interopSrc = Path.Combine(AppContext.BaseDirectory, "Interop.SolidEdge.dll");
		string interopDst = Path.Combine(sandbox, "Interop.SolidEdge.dll");

		// ---- 源码来源优先级:filePath > code > 复用沙箱里上次的 <name>.cs ----
		string codeSource;
		if (!string.IsNullOrWhiteSpace(filePath))
		{
			string resolved = ResolveSourceFile(filePath, csPath, out string fileErr);
			if (resolved == null)
			{
				AuditLog.Write("se_script_run", name, "SourceFile", false, filePath, InvocationRisk.ModelChanging, false, "blocked: " + fileErr);
				return Error("filePath 不可用: " + fileErr);
			}
			filePath = resolved;
			try
			{
				code = File.ReadAllText(filePath, DetectEncoding(filePath));
			}
			catch (Exception ex)
			{
				return Error("读取源码文件失败: " + ex.Message);
			}
			codeSource = "filePath:" + filePath;
		}
		else if (string.IsNullOrWhiteSpace(code))
		{
			// 两者都不给:复用上次落在沙箱里的源码(只想原样重跑时用)
			if (!File.Exists(csPath))
			{
				return Error("code 与 filePath 都为空,且沙箱里没有上次保存的源码(" + csPath + ")。请提供 code 或 filePath 之一。");
			}
			try
			{
				code = File.ReadAllText(csPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			}
			catch (Exception ex)
			{
				return Error("复用沙箱源码失败: " + ex.Message);
			}
			codeSource = "reuse:" + csPath;
		}
		else
		{
			codeSource = "inline";
		}

		// ---- 护栏:全局只读开关 ----
		string codeSummary = SummarizeCode(code);
		if (Guardrail.ReadOnlyEnabled)
		{
			AuditLog.Write("se_script_run", name, "Run", false, codeSummary, InvocationRisk.ModelChanging, false, "blocked by guardrail");
			return Error("已拒绝:全局只读开关处于开启状态(SE_MCP_READONLY=1),脚本通道整体禁用。若确实要运行,请把环境变量 SE_MCP_READONLY 设为 0(或删除)后重启 MCP server。");
		}

		// ---- 定位 csc ----
		string cscPath = ResolveCscPath();
		if (cscPath == null)
		{
			return Error("找不到框架 csc.exe。默认路径 C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe 不存在,且未设置 SE_MCP_CSC 环境变量。请确认系统自带 .NET Framework,或用 SE_MCP_CSC 指定 csc 全路径。");
		}

		string crashGuardNote = null;
		try
		{
			// ---- 崩溃栈注入(2026-10-25) ----
			// .NET Framework 进程里未处理异常会直接崩,连 "Unhandled Exception" 文本都不打,
			// 工具侧只拿到 exitCode=-532462766(0xE0434352)与 stderr"由于 Exception.ToString() 失败"。
			// 这里在写入沙箱前把用户的 Main 改名并套一层 try/catch,把 ex.ToString() 打到 stderr。
			code = WrapMainWithCrashGuard(code, out crashGuardNote);

			// 写源码(覆盖同名草稿,这本来就是我方托管区的草稿纸)
			// 带 BOM 写:csc 对无 BOM 文件按系统 ANSI 读,中文注释/字面量会乱码
			File.WriteAllText(csPath, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

			// 拷互操作程序集(3.3MB,常规开销可忽略)
			if (!File.Exists(interopSrc))
			{
				return Error("server 部署目录里找不到 Interop.SolidEdge.dll(期望: " + interopSrc + ")。请重新部署 mcp_build。");
			}
			File.Copy(interopSrc, interopDst, overwrite: true);
		}
		catch (Exception ex)
		{
			return Error("写沙箱失败: " + ex.Message);
		}

		// 本次运行之前已经跑过几次(编译失败那次不计,所以在这里读一次就够了)
		int previousRuns = CountRuns(runsPath);

		// ---- 编译(纯文件操作,不占 STA 线程;失败不进入运行阶段) ----
		var sw = Stopwatch.StartNew();
		string compilerOutput;
		bool compileOk;
		try
		{
			// 注意:不加 /noconfig——让默认 csc.rsp 免费引用 System.dll/System.Core.dll 等基础程序集
			// /debug:pdbonly:额外生成 pdb,让脚本运行期异常的 stderr 带源码行号(定位"脚本在哪一行挂的")。
			// pdb 只在运行期需要,跑完由 CleanupArtifacts 一并删除。
			// /utf8output:csc 默认按控制台代码页(中文机器是 GBK)输出诊断,管道按 UTF-8 解码会乱码
			string args = "/nologo /target:exe /platform:anycpu /debug:pdbonly /utf8output /r:\"" + interopDst + "\" /out:\"" + exePath + "\" \"" + csPath + "\"";
			compilerOutput = RunCapture(cscPath, args, TimeSpan.FromSeconds(CompileTimeoutSeconds), out int cscExit);
			compileOk = cscExit == 0 && File.Exists(exePath);
		}
		catch (Exception ex)
		{
			return Error("调用 csc 失败: " + ex.Message);
		}

		if (!compileOk)
		{
			// 编译失败:不运行,产物清理后把 CS 错误结构化回传
			CleanupArtifacts(exePath, interopDst);
			// 2026-10-25 审计修正:编译失败记 ALLOW 会让台账无法区分"跑了什么"与"什么都没跑",改记 DENY
			AuditLog.Write("se_script_run", name, "Compile", false, codeSummary, InvocationRisk.ModelChanging, false, "compile error(未运行)");
			return JsonSerializer.Serialize(new
			{
				status = "compile_error",
				name = name,
				compiler = cscPath,
				compilerOutput = Truncate(compilerOutput, out _),
				compileErrors = ParseCompileErrors(compilerOutput),
				csPath = csPath,
				runCount = previousRuns,
				hint = "编译失败,脚本未运行。直接看 compileErrors(已把 csc 输出解析成 file/line/column/code/message);脚本必须用 C# 5 语法(框架 csc 不认 $\"\" 插值/?. 等新语法)。"
			});
		}

		// ---- 运行(包在 context.Invoke 内:STA 串行 + DisplayAlerts 包裹防对话框卡死) ----
		string stdout = null;
		string stderr = null;
		string status;
		int? exitCode = null;
		bool truncated = false;
		string note = null;
		// 落盘用的全量输出(回传会被 Truncate 砍到 64KB,沙箱日志保留全量便于复查)
		string fullStdout = "";
		string fullStderr = "";
		try
		{
			var runResult = context.Invoke(() => RunScriptExe(context, exePath, TimeSpan.FromSeconds(timeoutSec), scriptArgs));
			stdout = runResult.Stdout;
			stderr = runResult.Stderr;
			status = runResult.TimedOut ? "timeout" : "ok";
			if (runResult.TimedOut)
			{
				note = "运行超过 " + timeoutSec + "s,已强杀整个进程树;超时前已产生的 stdout/stderr 已尽量回传(输出是边跑边异步读的,不会因强杀整段丢失)";
				stderr = ((stderr ?? "") + "\n[se_script_run] " + note).TrimStart();
			}
			else
			{
				exitCode = runResult.ExitCode;
				if (exitCode != 0)
				{
					status = "exit_nonzero";
					note = "脚本退出码非 0";
				}
			}
			fullStdout = stdout ?? "";
			fullStderr = stderr ?? "";
			stdout = Truncate(stdout, out bool t1);
			stderr = Truncate(stderr, out bool t2);
			truncated = t1 || t2;
		}
		catch (Exception ex)
		{
			status = "error";
			stderr = "[se_script_run] 运行阶段异常: " + ex.Message;
			fullStderr = stderr;
		}
		sw.Stop();

		// ---- 输出落盘:完整 stdout/stderr 写沙箱 <name>.log(突破 64KB 回传截断) ----
		try
		{
			File.WriteAllText(logPath,
				"=== se_script_run " + name + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\n" +
				"source   : " + codeSource + "\n" +
				"status   : " + status + "\n" +
				"exitCode : " + exitCode + "\n" +
				"duration : " + sw.ElapsedMilliseconds + "ms\n" +
				"truncated: " + truncated + "\n\n" +
				"--- stdout ---\n" + fullStdout + "\n\n--- stderr ---\n" + fullStderr + "\n",
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch
		{
			logPath = null;
		}

		// ---- 记账:脚本确实跑起来了才 +1(编译失败属手误,不当作"该固化"的信号) ----
		int runCount = AppendRun(runsPath, previousRuns, status, exitCode);

		// ---- 清理:exe/dll 跑完即删,.cs 与 .log 保留 ----
		CleanupArtifacts(exePath, interopDst);

		// ---- 审计 ----
		AuditLog.Write("se_script_run", name, "Run", false, codeSummary, InvocationRisk.ModelChanging, true, note ?? ("exitCode=" + exitCode));

		return JsonSerializer.Serialize(new
		{
			status = status,
			name = name,
			exitCode = exitCode,
			durationMs = sw.ElapsedMilliseconds,
			stdout = stdout,
			stderr = stderr,
			truncated = truncated,
			diagnostics = BuildDiagnostics(fullStderr, csPath),
			artifactPath = csPath,
			logPath = logPath,
			crashGuard = crashGuardNote,
			source = codeSource,
			runCount = runCount,
			hint = BuildHint(name, runCount)
		});
	}

	/// <summary>读沙箱台账里已有的运行次数(文件不存在算 0;读失败也按 0,绝不因此中断脚本)。</summary>
	private static int CountRuns(string runsPath)
	{
		try
		{
			if (!File.Exists(runsPath))
			{
				return 0;
			}
			int n = 0;
			foreach (string line in File.ReadAllLines(runsPath))
			{
				if (!string.IsNullOrWhiteSpace(line))
				{
					n++;
				}
			}
			return n;
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>
	/// 追加一行运行台账,返回本次是第几次。写失败时按 previousRuns+1 报,不影响主流程。
	///
	/// 格式:`yyyy-MM-dd HH:mm:ss ok 0`(时间 状态 退出码)。
	/// 只记时间戳不够 —— 沙箱里的 .cs 是【编译前】就写盘的,编译失败的脚本同样留下文件,
	/// 光看"文件存在"会把从没跑通的脚本当成可固化对象。带上状态才能让 --recipe-save 判断准入。
	/// </summary>
	private static int AppendRun(string runsPath, int previousRuns, string status, int? exitCode)
	{
		int current = previousRuns + 1;
		try
		{
			string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " +
				(status ?? "unknown") + " " +
				(exitCode.HasValue ? exitCode.Value.ToString() : "-") +
				System.Environment.NewLine;
			File.AppendAllText(runsPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch
		{
			// 台账只是提示/准入用的,写不进去就算了
		}
		return current;
	}

	/// <summary>沙箱里某个脚本的台账汇总(供 CLI `--recipe-save` 判断能不能入库)。</summary>
	internal struct ScriptRunStats
	{
		internal int Total;
		internal int Ok;

		/// <summary>成功运行过的【不同日期】数。同一天连跑 10 次也只算 1 天 —— 用来挡"一次调试里连跑两遍就入库"。</summary>
		internal int OkDays;

		internal bool LastOk;
		internal string LastStatus;

		internal bool HasAny { get { return Total > 0; } }
	}

	/// <summary>
	/// 读沙箱里某个脚本的运行台账。文件不存在 / 读失败都返回空统计,由调用方决定怎么处理。
	/// 旧格式(只有时间戳、没有状态)的行按"未知"计,不计入 Ok —— 宁可高估风险,不可误放行。
	/// </summary>
	internal static ScriptRunStats ReadRunStats(string scriptName)
	{
		ScriptRunStats st = new ScriptRunStats();
		if (string.IsNullOrWhiteSpace(scriptName))
		{
			return st;
		}
		try
		{
			string runsPath = Path.Combine(Path.GetTempPath(), "sespy", scriptName, scriptName + ".runs");
			if (!File.Exists(runsPath))
			{
				return st;
			}
			HashSet<string> okDates = new HashSet<string>(StringComparer.Ordinal);
			foreach (string line in File.ReadAllLines(runsPath))
			{
				if (string.IsNullOrWhiteSpace(line))
				{
					continue;
				}
				st.Total++;
				string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				string day = (parts.Length >= 1 && parts[0].Length == 10) ? parts[0] : null;
				string stt = (parts.Length >= 3) ? parts[2] : null;
				string code = (parts.Length >= 4) ? parts[3] : null;
				st.LastStatus = stt ?? "unknown";
				if (string.Equals(stt, "ok", StringComparison.OrdinalIgnoreCase) && code == "0")
				{
					st.Ok++;
					st.LastOk = true;
					if (day != null)
					{
						okDates.Add(day);
					}
				}
				else
				{
					st.LastOk = false;
				}
			}
			st.OkDays = okDates.Count;
		}
		catch
		{
		}
		return st;
	}

	/// <summary>
	/// 返回体的 hint。第 2 次及以后在开头加一句"该固化了"——判断依据是
	/// 同一脚本名被反复真跑,说明这类任务不是一次性的,应该沉淀成配方(se_recipe_run)。
	/// </summary>
	private static string BuildHint(string name, int runCount)
	{
		StringBuilder sb = new StringBuilder();
		if (runCount >= 2)
		{
			sb.Append("【该脚本已运行 ").Append(runCount).Append(" 次,建议固化成配方,别下次再重写一遍】")
				.Append("用 CLI 一条命令即可(纯本地操作,不碰 Solid Edge): ")
				.Append("solidedge-mcp --recipe-save ").Append(name).Append(" --title \"用途说明\"")
				.Append(" --input \"target:object::起始文档句柄\" --dry")
				.Append("(--dry 只打印生成的 JSON,确认没问题再去掉落盘;之后可用 se_recipe_run ").Append(name).Append(" 调用)。");
		}
		sb.Append("控制流/批量遍历用脚本;确定性建模仍优先 se_invoke_chain 或 se_model_build;")
			.Append("脚本里验证过的可复用结论请沉淀进配方。长脚本优先用 filePath 传源码文件:免 JSON 转义,改完文件重调即重跑;")
			.Append("只想原样重跑时连 filePath/code 都不用传。输出被截断时读 logPath 看全量。");
		return sb.ToString();
	}

	/// <summary>
	/// 校验并解析 filePath:必须存在且为 .cs 绝对路径(展开环境变量、去包裹引号)。
	/// 设 SE_MCP_SCRIPT_ALLOW_FILE=0 时收紧为只允许沙箱内的 &lt;name&gt;.cs(供只读/受控场景)。
	/// 失败返回 null 并通过 error 给出原因。
	/// </summary>
	private static string ResolveSourceFile(string filePath, string sandboxCsPath, out string error)
	{
		error = null;
		try
		{
			filePath = System.Environment.ExpandEnvironmentVariables(filePath.Trim().Trim('"'));
			if (!Path.IsPathRooted(filePath))
			{
				error = "必须是绝对路径。收到: \"" + filePath + "\"";
				return null;
			}
			string full = Path.GetFullPath(filePath);
			if (!File.Exists(full))
			{
				error = "文件不存在: " + full;
				return null;
			}
			if (!full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			{
				error = "只接受 .cs 源文件。收到: " + full;
				return null;
			}
			string restrict = System.Environment.GetEnvironmentVariable("SE_MCP_SCRIPT_ALLOW_FILE");
			if (restrict == "0")
			{
				string allowed = Path.GetFullPath(sandboxCsPath);
				if (!string.Equals(full, allowed, StringComparison.OrdinalIgnoreCase))
				{
					error = "SE_MCP_SCRIPT_ALLOW_FILE=0,filePath 只允许沙箱内的 " + allowed;
					return null;
				}
			}
			return full;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return null;
		}
	}

	/// <summary>
	/// 猜源码文件编码:BOM 优先(UTF8/UTF16);无 BOM 时先按 UTF8 严格解码,
	/// 非法字节序列(典型的 GBK 中文)则退回系统 ANSI,避免中文注释/字面量变乱码。
	/// </summary>
	private static Encoding DetectEncoding(string path)
	{
		try
		{
			byte[] bytes = File.ReadAllBytes(path);
			if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			{
				return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
			}
			if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
			{
				return Encoding.Unicode;
			}
			try
			{
				new UTF8Encoding(false, true).GetCharCount(bytes);
				return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
			}
			catch
			{
				return Encoding.Default;
			}
		}
		catch
		{
			return Encoding.Default;
		}
	}

	/// <summary>解析 csc 路径:SE_MCP_CSC 优先,其次 Framework64,最后 Framework。都没有返回 null。</summary>
	private static string ResolveCscPath()
	{
		string envCsc = System.Environment.GetEnvironmentVariable("SE_MCP_CSC");
		if (!string.IsNullOrWhiteSpace(envCsc) && File.Exists(envCsc))
		{
			return envCsc;
		}
		string winDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
		string framework64 = Path.Combine(winDir, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
		if (File.Exists(framework64))
		{
			return framework64;
		}
		string framework = Path.Combine(winDir, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
		if (File.Exists(framework))
		{
			return framework;
		}
		return null;
	}

	/// <summary>运行脚本 exe:异步读输出防管道死锁,超时强杀整个进程树。须在 STA 线程上调用(context.Invoke 内)。</summary>
	private static ScriptRunResult RunScriptExe(SolidEdgeContext context, string exePath, TimeSpan timeout, string[] scriptArgs = null)
	{
		var result = new ScriptRunResult();
		var psi = new ProcessStartInfo
		{
			FileName = exePath,
			// 脚本参数按 Windows 规则逐个加引号,支持带空格的路径/值
			Arguments = scriptArgs == null || scriptArgs.Length == 0 ? "" : JoinScriptArgs(scriptArgs),
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = false,
			CreateNoWindow = true,
			WorkingDirectory = Path.GetDirectoryName(exePath),
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

		Application app = null;
		bool previousAlerts = true;
		bool alertsChanged = false;
		Process proc = null;
		try
		{
			// 父进程侧 DisplayAlerts=false 包裹:防脚本触发的 COM 对话框把 SE 挂起
			try
			{
				app = context.GetApplication();
				previousAlerts = app.DisplayAlerts;
				app.DisplayAlerts = false;
				alertsChanged = true;
			}
			catch
			{
				// SE 不在/取不到也不影响脚本本身跑
			}

			proc = Process.Start(psi);

			// 异步读完输出再等退出:标准输出/错误管道缓冲区满会死锁,必须边跑边读
			var stdoutTask = proc.StandardOutput.ReadToEndAsync();
			var stderrTask = proc.StandardError.ReadToEndAsync();

			if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
			{
				result.TimedOut = true;
				try { proc.Kill(entireProcessTree: true); } catch { }
				proc.WaitForExit(5000);
			}
			else
			{
				result.ExitCode = proc.ExitCode;
			}

			try { result.Stdout = stdoutTask.GetAwaiter().GetResult() ?? ""; } catch { result.Stdout = ""; }
			try { result.Stderr = stderrTask.GetAwaiter().GetResult() ?? ""; } catch { result.Stderr = ""; }
		}
		finally
		{
			if (proc != null)
			{
				try { proc.Dispose(); } catch { }
			}
			if (alertsChanged && app != null)
			{
				try { app.DisplayAlerts = previousAlerts; } catch { }
			}
		}
		return result;
	}

	/// <summary>拼接脚本参数,按 Windows 命令行规则给含空格/制表/引号的参数加引号(反斜杠转义)。</summary>
	private static string JoinScriptArgs(string[] args)
	{
		var sb = new StringBuilder();
		foreach (var a in args)
		{
			if (sb.Length > 0) sb.Append(' ');
			if (a.Length == 0 || a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
			{
				sb.Append(a);
				continue;
			}
			sb.Append('"');
			for (int i = 0; i < a.Length; i++)
			{
				int backslashes = 0;
				while (i < a.Length && a[i] == '\\') { backslashes++; i++; }
				if (i == a.Length) { sb.Append('\\', backslashes * 2); break; }
				if (a[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
				else sb.Append('\\', backslashes).Append(a[i]);
			}
			sb.Append('"');
		}
		return sb.ToString();
	}

	/// <summary>同步跑一个外部命令并捕获输出(用于 csc 编译)。返回合并后的输出。</summary>
	private static string RunCapture(string fileName, string arguments, TimeSpan timeout, out int exitCode)
	{
		var psi = new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			// csc 用 /utf8output 输出 UTF-8,这里必须显式按 UTF-8 解码:
			// 不指定时 .NET 按"父进程控制台编码"解码,而 MCP server(无控制台)与 CLI 进程行为不一致
			// —— 实测同一份编译错误在 CLI 通道中文正常、在 MCP 通道乱码,就是这里没对齐。
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		using var proc = Process.Start(psi);
		var stdoutTask = proc.StandardOutput.ReadToEndAsync();
		var stderrTask = proc.StandardError.ReadToEndAsync();
		if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
		{
			try { proc.Kill(entireProcessTree: true); } catch { }
			proc.WaitForExit(5000);
			exitCode = -1;
			return "csc 编译超时(" + (int)timeout.TotalSeconds + "s),已强杀。";
		}
		exitCode = proc.ExitCode;
		string stdout = "";
		string stderr = "";
		try { stdout = stdoutTask.GetAwaiter().GetResult() ?? ""; } catch { }
		try { stderr = stderrTask.GetAwaiter().GetResult() ?? ""; } catch { }
		return ((stdout + "\n" + stderr).Trim() + "\n[csc 退出码 " + exitCode + "]").Trim();
	}

	private static void CleanupArtifacts(params string[] files)
	{
		foreach (string file in files)
		{
			if (string.IsNullOrEmpty(file))
			{
				continue;
			}
			try { if (File.Exists(file)) File.Delete(file); } catch { }
			// .pdb 兜底(csc 默认不生成,防万一)
			try { string pdb = Path.ChangeExtension(file, ".pdb"); if (File.Exists(pdb)) File.Delete(pdb); } catch { }
		}
	}

	/// <summary>
	/// 把 csc 输出解析成结构化的编译错误列表。
	/// csc 的行格式(中英文一致):`C:\path\x.cs(12,5): error CS0103: 当前上下文中不存在名称"xxx"`
	/// —— 以前只能把整段文本丢给 AI 自己找行号,这里直接给出 file/line/column/code/message。
	/// </summary>
	private static List<object> ParseCompileErrors(string compilerOutput)
	{
		var list = new List<object>();
		if (string.IsNullOrEmpty(compilerOutput))
		{
			return list;
		}
		MatchCollection matches = Regex.Matches(compilerOutput, @"(?<f>[^\r\n(]+)\((?<line>\d+),(?<col>\d+)\)\s*:\s*(?<sev>error|warning)\s+(?<code>CS\d+)\s*:\s*(?<msg>[^\r\n]*)");
		foreach (Match m in matches)
		{
			int line;
			int col;
			int.TryParse(m.Groups["line"].Value, out line);
			int.TryParse(m.Groups["col"].Value, out col);
			list.Add(new
			{
				file = m.Groups["f"].Value.Trim(),
				line = line,
				column = col,
				severity = m.Groups["sev"].Value,
				code = m.Groups["code"].Value,
				message = m.Groups["msg"].Value.Trim()
			});
		}
		return list;
	}

	/// <summary>
	/// 运行期诊断:从 stderr 里抽出"未处理的异常"的类型/消息、源码行号与栈顶若干帧。
	/// 依赖 csc 的 /debug:pdbonly(否则 .NET Framework 不打行号)。scriptLine 只在栈帧指向本脚本时给出。
	/// </summary>
	private static object BuildDiagnostics(string stderr, string csPath)
	{
		if (string.IsNullOrEmpty(stderr))
		{
			return null;
		}
		string exceptionType = null;
		string exceptionMessage = null;
		string atFile = null;
		int? atLine = null;
		var frames = new List<string>();
		string[] lines = stderr.Replace("\r\n", "\n").Split('\n');
		for (int i = 0; i < lines.Length; i++)
		{
			string trimmed = lines[i].Trim();
			if (trimmed.Length == 0)
			{
				continue;
			}
			if (exceptionType == null && (trimmed.StartsWith("Unhandled Exception", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("未处理的异常", StringComparison.Ordinal)))
			{
				int colon = trimmed.IndexOf(':');
				if (colon >= 0)
				{
					string rest = trimmed.Substring(colon + 1).Trim();
					int second = rest.IndexOf(':');
					if (second > 0)
					{
						exceptionType = rest.Substring(0, second).Trim();
						exceptionMessage = rest.Substring(second + 1).Trim();
					}
					else
					{
						exceptionType = rest;
					}
				}
				continue;
			}
			if (frames.Count < 8 && (trimmed.StartsWith("在 ", StringComparison.Ordinal) || trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase)))
			{
				frames.Add(trimmed);
			}
			if (!atLine.HasValue)
			{
				Match m = Regex.Match(trimmed, @"位置\s*(?<f>.+?):行号\s*(?<l>\d+)");
				if (!m.Success)
				{
					m = Regex.Match(trimmed, @"\bin\s+(?<f>.+?):line\s+(?<l>\d+)");
				}
				if (m.Success)
				{
					atFile = m.Groups["f"].Value.Trim();
					int parsed;
					if (int.TryParse(m.Groups["l"].Value, out parsed))
					{
						atLine = parsed;
					}
				}
			}
		}
		bool inScript = false;
		if (atFile != null && !string.IsNullOrEmpty(csPath))
		{
			try { inScript = string.Equals(Path.GetFullPath(atFile), Path.GetFullPath(csPath), StringComparison.OrdinalIgnoreCase); }
			catch { }
		}
		// ---- ASCII 锚点兜底 ----
		// 子进程(.NET Framework exe)的 stderr 编码取决于脚本有没有设 Console.OutputEncoding=UTF8:
		// 没设时按系统 ANSI(中文=GBK)输出 → 被按 UTF-8 解码成乱码,本地化标记("未处理的异常"/"位置"/"行号")全丢。
		// 但"异常类型名"与"文件全路径"是 ASCII,乱码里依然可辨 —— 用它们兜底提取。
		if (exceptionType == null)
		{
			Match em = Regex.Match(stderr, @"(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*(?:Exception|Error)\b");
			if (em.Success) exceptionType = em.Value;
		}
		if (!atLine.HasValue)
		{
			Match fm = Regex.Match(stderr, "(?<f>[A-Za-z]:\\\\[^\\s:\"]+\\.cs)[^\\d\\r\\n]{0,8}(?<l>\\d{1,6})");
			if (fm.Success)
			{
				atFile = fm.Groups["f"].Value;
				int asciiLine;
				if (int.TryParse(fm.Groups["l"].Value, out asciiLine)) atLine = asciiLine;
			}
		}
		if (frames.Count == 0)
		{
			string[] asciiLines = stderr.Replace("\r\n", "\n").Split('\n');
			for (int j = 0; j < asciiLines.Length && frames.Count < 8; j++)
			{
				string t2 = asciiLines[j].Trim();
				if (t2.IndexOf(".cs:", StringComparison.OrdinalIgnoreCase) >= 0) frames.Add(t2);
			}
		}

		// ASCII 兜底可能刚刚才补上 atFile/atLine,而 inScript 是在它之前算的 → 这里补算一次
		if (!inScript && atFile != null && !string.IsNullOrEmpty(csPath))
		{
			try { inScript = string.Equals(Path.GetFullPath(atFile), Path.GetFullPath(csPath), StringComparison.OrdinalIgnoreCase); }
			catch { }
		}

		// 本机 .NET Framework 打印未处理异常时可能自身崩溃(实测退出码 0xC0000005,stderr 只有一句
		// "由于 Exception.ToString() 失败,因此无法打印异常字符串"),此时拿不到栈与行号 —— 如实标注并给绕法
		bool clrPrintFailure = stderr.IndexOf("Exception.ToString()", StringComparison.Ordinal) >= 0
			|| stderr.IndexOf("无法打印异常字符串", StringComparison.Ordinal) >= 0;
		if (exceptionType == null && atLine == null && !clrPrintFailure)
		{
			return null;
		}
		return new
		{
			exceptionType = exceptionType,
			exceptionMessage = exceptionMessage,
			atFile = atFile,
			atLine = atLine,
			scriptLine = (inScript ? atLine : null),
			stackTop = ((frames.Count > 0) ? frames : null),
			clrFailure = clrPrintFailure,
			hint = (clrPrintFailure
				? "本机 .NET Framework 打印未处理异常时会直接崩溃(没有 'Unhandled Exception' 文本,退出码常见 0xC0000005),因此拿不到异常类型与行号。绕法:脚本 Main 里用 try/catch 包住主体,手动 Console.Error.WriteLine(ex.ToString()) 再 Environment.Exit(1) —— 工具就能回传完整栈与行号。"
				: "脚本运行期异常。scriptLine 是脚本源码行号(已加 /debug:pdbonly);未捕获异常会截断其后的所有 Console 输出,修好该行再重跑。")
		};
	}

	private static string Truncate(string text, out bool truncated)
	{
		truncated = false;
		if (string.IsNullOrEmpty(text) || text.Length <= MaxOutputChars)
		{
			return text ?? "";
		}
		truncated = true;
		return text.Substring(0, MaxOutputChars) + "\n[se_script_run] 输出超过 64KB,已截断。完整输出请让脚本写到文件再读。";
	}

	private static string SummarizeCode(string code)
	{
		string first = (code ?? "").TrimStart();
		int idx = first.IndexOf('\n');
		if (idx > 0)
		{
			first = first.Substring(0, idx);
		}
		first = first.Trim();
		if (first.Length > 80)
		{
			first = first.Substring(0, 80) + "...";
		}
		return first + " (共 " + ((code ?? "").Length) + " 字符)";
	}

	/// <summary>
	/// 给脚本注入崩溃兜底壳(2026-10-25)。
	///
	/// 背景:框架(.NET Framework)进程里未处理异常会直接崩,连 "Unhandled Exception" 文本都不打,
	/// 工具侧只拿到 exitCode=-532462766(0xE0434352)+ stderr"由于 Exception.ToString() 失败",
	/// 堆栈全丢——本轮的 SaveAs 崩溃就是这样,查不出任何原因。
	///
	/// 做法:把用户的 <c>static Main</c> 改名 <c>UserMain</c>(并提升为 internal static,
	/// 否则壳类访问不到),再追加 <c>__SeMpcCrashGuard.Main</c> 包 try/catch,
	/// 异常时把完整 <c>ex.ToString()</c> 打到 stderr 并以退出码 1 结束。用户逻辑一行不改。
	///
	/// 幂等:源码里已含壳(沙箱 reuse 场景)或找不到 Main 时原样返回。
	/// </summary>
	private static string WrapMainWithCrashGuard(string code, out string note)
	{
		note = null;
		if (string.IsNullOrEmpty(code))
		{
			note = "源码为空,未注入";
			return code;
		}
		if (code.IndexOf("__SeMpcCrashGuard", StringComparison.Ordinal) >= 0)
		{
			note = "已含崩溃兜底壳,跳过(幂等)";
			return code;
		}

		var rx = new Regex(@"(?:(?:public|private|internal|protected)\s+)?static\s+(void|int)\s+Main\s*\(([^)]*)\)");
		Match m = rx.Match(code);
		if (!m.Success)
		{
			note = "没找到 static void/int Main(...),未注入(入口不是标准 Main)";
			return code;
		}

		// 壳类在另一个类里,必须以【类名.UserMain】调用,裸调用会 CS0103(2026-10-25 首跑踩到)。
		var classRx = new Regex(@"\bclass\s+([A-Za-z_]\w*)");
		Match cm = classRx.Match(code);
		if (!cm.Success)
		{
			note = "找不到 class 声明,未注入(无法确定 Main 的宿主类名)";
			return code;
		}
		string className = cm.Groups[1].Value;

		string retType = m.Groups[1].Value;
		string paramsText = (m.Groups[2].Value ?? "").Trim();
		string renamed = "internal static " + retType + " UserMain(" + paramsText + ")";
		string instrumented = code.Substring(0, m.Index) + renamed + code.Substring(m.Index + m.Length);

		bool hasArgs = paramsText.Length > 0;
		string argName = "args";
		if (hasArgs)
		{
			string first = paramsText.Split(',')[0].Trim();
			int sp = first.LastIndexOf(' ');
			if (sp >= 0 && sp + 1 < first.Length) argName = first.Substring(sp + 1).Trim();
		}
		string callTarget = className + ".UserMain(" + (hasArgs ? argName : "") + ");";
		// Environment 必须全限定:脚本常 using SolidEdgeFramework,那里也有个 Environment 类型(CS0104)
		string call = retType == "int"
			? "System.Environment.ExitCode = " + callTarget
			: callTarget;

		string guard =
			"\n\n// ===== 由 se_script_run 自动注入的崩溃兜底壳(不改你的逻辑) =====\n" +
			"internal static class __SeMpcCrashGuard\n" +
			"{\n" +
			"    [STAThread]\n" +
			"    static void Main(string[] args)\n" +
			"    {\n" +
			"        // ★ 由壳统一设 UTF-8:shell 侧(se_script_run)按 UTF-8 解码子进程输出,\n" +
			"        //   脚本自己没设 OutputEncoding 时会按系统 ANSI(GBK)输出 -> 中文全乱码\n" +
			"        //   (2026-10-25 实测:崩溃栈的 ASCII 类型名/行号可辨,中文异常消息变成问号堆)。\n" +
			"        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }\n" +
			"        try\n" +
			"        {\n" +
			"            " + call + "\n" +
			"        }\n" +
			"        catch (Exception __seEx)\n" +
			"        {\n" +
			"            try { Console.Error.WriteLine(\"=== 脚本未处理异常(se_script_run 崩溃兜底) ===\"); } catch { }\n" +
			"            try { Console.Error.WriteLine(__seEx.ToString()); } catch { }\n" +
			"            try { System.Environment.Exit(1); } catch { }\n" +
			"        }\n" +
			"    }\n" +
			"}\n";

		note = "已注入崩溃兜底:Main → UserMain(" + (hasArgs ? paramsText : "无参") + "),未处理异常会把完整堆栈打到 stderr";
		return instrumented + guard;
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new
		{
			status = "error",
			message = message
		});
	}

	/// <summary>脚本运行结果。</summary>
	private sealed class ScriptRunResult
	{
		public string Stdout;
		public string Stderr;
		public int? ExitCode;
		public bool TimedOut;
	}
}
