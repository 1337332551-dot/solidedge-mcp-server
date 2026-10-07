using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 自动还原点 hook(洋葱中层:挂在 PermissionTap 权限判定之后、工具体执行之前)。
///
/// 目的(2026-10-06 用户拍板):把"改坏可回退"从协议纪律升级为机制保证——
/// 任何 Model/Escape 档工具(写模型)执行前,自动对目标文档所在目录打一个 git 还原点,
/// 不依赖 AI 记得跑 model_rp.ps1。恢复(restore)仍走 model_rp.ps1(需关文档+pre-restore 存档+字节级校验,
/// 交互性强不自动化);本 hook 只保证"改前一定有点可回"。
///
/// 行为:
/// - 仓库位置:%LOCALAPPDATA%\SolidEdgeSpy\model-vcs\&lt;目录指纹&gt;.git(零污染模式,
///   镜像 model_rp.ps1 init -GitDir:core.worktree 指向模型目录,工作区不出现 .git)。
/// - 无仓自动建仓;清单驱动(只管 .par/.asm/.psm/.dft/.cfg);无变化快速跳过;
/// - tag=rp-&lt;yyyyMMdd-HHmmssfff&gt;-auto;滚动保留最近 SE_RP_KEEP(默认 5)个 auto 点,手动 tag 不碰。
/// - best-effort:任何失败(含 git.exe 缺失)只记审计,绝不阻断工具调用(PermissionTap 铁律④同源)。
/// - 开关:SE_RP_MODE=off 关闭(默认 on);保留数:SE_RP_KEEP(默认 5)。
///
/// 线程模型:BeforeWrite 运行在 tap 读循环线程;COM 查询经 SolidEdgeContext.Invoke 封送到
/// 专用 STA 队列线程(STA 线程只消费队列、不碰 stdin,无死锁路径);git 子进程加全局锁串行。
/// </summary>
internal sealed class RestorePointHook
{
	private readonly SolidEdgeContext _context;
	private static readonly object GitSync = new object();

	/// <summary>受保护的模型文件扩展名(与 model_rp.ps1 清单一致)。</summary>
	private static readonly string[] ModelExt = { ".par", ".asm", ".psm", ".dft", ".cfg" };

	public RestorePointHook(SolidEdgeContext context)
	{
		_context = context ?? throw new ArgumentNullException("context");
	}

	// ---------------- 开关/参数(懒读 env,测试可覆写) ----------------

	private static bool? _enabled;

	/// <summary>hook 开关。SE_RP_MODE=off(或 0/false)时关闭;默认开。</summary>
	internal static bool Enabled
	{
		get
		{
			if (!_enabled.HasValue)
			{
				string v = Environment.GetEnvironmentVariable("SE_RP_MODE");
				_enabled = !(v != null && (v.Equals("off", StringComparison.OrdinalIgnoreCase)
					|| v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0"));
			}
			return _enabled.Value;
		}
		set { _enabled = value; }
	}

	private static int? _keep;

	/// <summary>auto 还原点滚动保留数。SE_RP_KEEP 覆盖(最小 1),默认 5。</summary>
	internal static int Keep
	{
		get
		{
			if (!_keep.HasValue)
			{
				int n;
				string v = Environment.GetEnvironmentVariable("SE_RP_KEEP");
				_keep = (int.TryParse(v, out n) && n >= 1) ? n : 5;
			}
			return _keep.Value;
		}
		set { _keep = value; }
	}

	/// <summary>测试后重置懒读缓存。</summary>
	internal static void ResetForTests()
	{
		_enabled = null;
		_keep = null;
	}

	// ---------------- 入口 ----------------

	/// <summary>
	/// 写类工具调用前打还原点。铁律:本方法自身绝不抛异常、绝不阻断调用方
	/// (PermissionTap 侧还有一层 try/catch 兜底)。
	/// </summary>
	public void BeforeWrite(string tool, JsonElement toolParams)
	{
		try
		{
			if (!Enabled || tool == null)
			{
				return;
			}
			RiskTier? tier = ToolRisk.TierOf(tool);
			if (tier != RiskTier.Model && tier != RiskTier.Escape)
			{
				return; // Read/Session/未登记档不改模型内容
			}

			string docName;
			string docPath;
			try
			{
				docPath = ResolveTargetPath(toolParams, out docName);
			}
			catch (Exception ex)
			{
				Audit(tool, null, "-", "rp skip: 定位目标文档失败(不阻断): " + ex.Message);
				return;
			}
			if (string.IsNullOrEmpty(docPath))
			{
				Audit(tool, docName, "-", "rp skip: 无活动文档(新建文件本无基线可保护)");
				return;
			}
			// 既有实测:未保存文档的 FullName 只有文件名、无路径
			string dir = Path.GetDirectoryName(docPath);
			if (string.IsNullOrEmpty(dir))
			{
				Audit(tool, docName, "-", "rp skip: 文档未保存过,磁盘无基线可打(请先在 SE 里 Save)");
				return;
			}

			string[] files = ModelFiles(dir);
			if (files.Length == 0)
			{
				Audit(tool, docName, "-", "rp skip: 目录无模型文件(" + dir + ")");
				return;
			}

			string repoDir = RepoDirFor(dir);
			RpResult r = EnsurePoint(dir, repoDir, tool, files);
			if (r.Committed)
			{
				Audit(tool, docName, r.Tag, "rp auto 点已打 repo=" + Path.GetFileName(repoDir)
					+ " files=" + files.Length + " autoTags=" + r.AutoTagCount);
			}
			else
			{
				Audit(tool, docName, "-", "rp skip: 自上一点以来无文件变化");
			}
		}
		catch (Exception ex)
		{
			// 双保险:BeforeWrite 内部异常也不允许冒到 tap
			try { Audit(tool, null, "-", "rp FAILED(不阻断): " + ex.Message); } catch { }
		}
	}

	// ---------------- 目标文档定位(COM 封送) ----------------

	/// <summary>解析目标文档完整路径:objectId 句柄优先,回落活动文档。取不到返回 null。</summary>
	private string ResolveTargetPath(JsonElement toolParams, out string docName)
	{
		string objectId = TryGetString(toolParams, "objectId");
		string name = null; // 匿名方法不能写 out 参数,经捕获局部变量带出(CS1628)
		string path = _context.Invoke(delegate
		{
			if (!string.IsNullOrEmpty(objectId))
			{
				ObjectHandle h = _context.GetHandle(objectId);
				string p = TryGetFullName(h == null ? null : h.ComObject);
				if (p != null)
				{
					name = Path.GetFileName(p);
					return p;
				}
				// 句柄不是文档/已失效 → 回落活动文档
			}
			try
			{
				object app = _context.GetApplication(); // 已在 STA 线程内,Invoke 直接执行
				object doc = app.GetType().InvokeMember("ActiveDocument", BindingFlags.GetProperty, null, app, null);
				string p2 = TryGetFullName(doc);
				if (p2 != null)
				{
					name = Path.GetFileName(p2);
				}
				return p2;
			}
			catch
			{
				return null;
			}
		});
		docName = name;
		return path;
	}

	private static string TryGetFullName(object doc)
	{
		if (doc == null)
		{
			return null;
		}
		try
		{
			return doc.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, doc, null) as string;
		}
		catch
		{
			return null;
		}
	}

	private static string TryGetString(JsonElement obj, string name)
	{
		if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement el)
			&& el.ValueKind == JsonValueKind.String)
		{
			return el.GetString();
		}
		return null;
	}

	// ---------------- 目录指纹与仓库路径 ----------------

	/// <summary>仓库根:%LOCALAPPDATA%\SolidEdgeSpy\model-vcs(与审计日志/snapshots 同根,持久目录)。</summary>
	internal static string RepoRoot
	{
		get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidEdgeSpy", "model-vcs"); }
	}

	/// <summary>目录指纹:路径清洗(非 ASCII 字母数字/._- → '_')+ 全路径 SHA256 前 8 位,稳定且防碰撞。</summary>
	internal static string RepoFingerprint(string dir)
	{
		string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
		using (SHA256 sha = SHA256.Create())
		{
			byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
			StringBuilder hex = new StringBuilder(8);
			for (int i = 0; i < 4; i++)
			{
				hex.Append(hash[i].ToString("x2"));
			}
			StringBuilder sb = new StringBuilder(full.Length);
			foreach (char c in full)
			{
				bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
					|| c == '.' || c == '-' || c == '_';
				sb.Append(ok ? c : '_');
			}
			string s = sb.ToString();
			if (s.Length > 80)
			{
				s = s.Substring(s.Length - 80); // 保尾部(叶子目录名)更可读
			}
			return s + "-" + hex.ToString();
		}
	}

	internal static string RepoDirFor(string dir)
	{
		return Path.Combine(RepoRoot, RepoFingerprint(dir) + ".git");
	}

	// ---------------- 清单与还原点核心 ----------------

	/// <summary>目录顶层模型文件清单(相对文件名,排序稳定;与 model_rp.ps1 DefaultFiles 同口径)。</summary>
	internal static string[] ModelFiles(string dir)
	{
		return Directory.EnumerateFiles(dir)
			.Where(f => ModelExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
			.Select(Path.GetFileName)
			.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	internal sealed class RpResult
	{
		public bool Committed;
		public string Tag;
		public int AutoTagCount;
	}

	/// <summary>
	/// 确保 dir 当前磁盘状态有对应还原点:无仓自动建仓→status→有变化才 add/commit/tag→滚动清理。
	/// git 全流程同步执行(典型 <100ms,首次建仓稍慢);异常向上抛,由 BeforeWrite 统一兜底。
	/// </summary>
	internal static RpResult EnsurePoint(string dir, string repoDir, string tool, string[] files)
	{
		lock (GitSync)
		{
			if (!Directory.Exists(repoDir))
			{
				string parent = Path.GetDirectoryName(repoDir);
				if (!string.IsNullOrEmpty(parent))
				{
					Directory.CreateDirectory(parent);
				}
				// 关键:init 必须带 --git-dir 前缀跑(与 model_rp.ps1 同构)——GIT_DIR 已设时 git init
				// 把仓库平铺建在指定目录;裸 init 会嵌套出 <repo>\.git,后续 --git-dir 全部失效
				// (2026-10-06 实测 "fatal: not in a git directory")。
				RunGit(dir, repoDir, "init", repoDir);
				// 零污染模式配置(镜像 model_rp.ps1 init):worktree 指向模型目录,中性身份,纯二进制仓
				RunGit(dir, repoDir, "config", "core.bare", "false");
				RunGit(dir, repoDir, "config", "core.worktree", dir);
				RunGit(dir, repoDir, "config", "user.name", "se-rp");
				RunGit(dir, repoDir, "config", "user.email", "se-rp@local");
				RunGit(dir, repoDir, "config", "core.autocrlf", "false");
			}

			string[] pathspec = new string[files.Length + 1];
			pathspec[0] = "--";
			Array.Copy(files, 0, pathspec, 1, files.Length);
			string[] status = RunGit(dir, repoDir, BuildArgs("status", "--porcelain", pathspec));
			RpResult r = new RpResult();
			r.AutoTagCount = AutoTags(dir, repoDir).Length;
			if (!status.Any(l => !string.IsNullOrWhiteSpace(l)))
			{
				return r; // 无变化:不重复打点(快速路径)
			}

			RunGit(dir, repoDir, BuildArgs("add", "--", files));
			RunGit(dir, repoDir, "-c", "core.createObject=link", "commit",   // 绕开 AV/IDE 监视导致的松散对象写入失败(既有坑)
				"-m", "rp-auto: " + tool,
				"-m", "清单(还原范围): " + string.Join(", ", files));
			r.Tag = "rp-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-auto";
			RunGit(dir, repoDir, "tag", r.Tag);
			r.Committed = true;

			// 滚动清理:只动 rp-*-auto,手动 tag(rp-* 非 -auto 结尾)不受影响
			string[] tags = AutoTags(dir, repoDir);
			r.AutoTagCount = tags.Length;
			int excess = tags.Length - Keep;
			for (int i = 0; i < excess; i++)
			{
				RunGit(dir, repoDir, "tag", "-d", tags[i]);
			}
			if (excess > 0)
			{
				RunGit(dir, repoDir, "-c", "gc.reflogExpire=now", "-c", "gc.reflogExpireUnreachable=now",
					"reflog", "expire", "--expire=now", "--all");
				RunGit(dir, repoDir, "gc", "-q", "--prune=now");
				r.AutoTagCount = tags.Length - excess;
			}
			return r;
		}
	}

	/// <summary>auto 还原点标签(旧→新)。tag 内嵌定宽时间戳,字典序=时间序,不依赖 git 排序语义。</summary>
	internal static string[] AutoTags(string dir, string repoDir)
	{
		return RunGit(dir, repoDir, "for-each-ref", "--format=%(refname:short)", "refs/tags/rp-*-auto")
			.Select(l => (l ?? "").Trim())
			.Where(l => l.Length > 0)
			.OrderBy(l => l.StartsWith("rp-") ? l.Substring(3) : l, StringComparer.Ordinal)
			.ToArray();
	}

	// ---------------- git 进程封装 ----------------

	private static string[] BuildArgs(string head, string tail, string[] rest)
	{
		string[] all = new string[2 + rest.Length];
		all[0] = head;
		all[1] = tail;
		Array.Copy(rest, 0, all, 2, rest.Length);
		return all;
	}

	private static string[] BuildArgs(string head, string[] rest)
	{
		string[] all = new string[1 + rest.Length];
		all[0] = head;
		Array.Copy(rest, 0, all, 1, rest.Length);
		return all;
	}

	/// <summary>零污染模式的统一前缀:-C dir --git-dir=repo --work-tree=dir(与 model_rp.ps1 GitBase 同构)。</summary>
	private static string[] RunGit(string dir, string repoDir, params string[] args)
	{
		// git 允许 -c 出现在子命令之前任意位置,调用方把 -c 放在 args 首即可
		string[] all = new string[args.Length + 4];
		all[0] = "-C";
		all[1] = dir;
		all[2] = "--git-dir=" + repoDir;
		all[3] = "--work-tree=" + dir;
		for (int i = 0; i < args.Length; i++)
		{
			all[4 + i] = args[i];
		}
		return ExecGit(all);
	}

	/// <summary>执行 git.exe:不走 shell、参数数组、60s 超时 kill;非零退出抛 IOException(带 stderr)。</summary>
	private static string[] ExecGit(string[] args)
	{
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName = "git.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		foreach (string a in args)
		{
			psi.ArgumentList.Add(a);
		}
		using (Process p = Process.Start(psi))
		{
			string stdout = p.StandardOutput.ReadToEnd();
			string stderr = p.StandardError.ReadToEnd();
			if (!p.WaitForExit(60000))
			{
				try { p.Kill(); } catch { }
				throw new IOException("git 超时(60s): git " + string.Join(" ", args));
			}
			if (p.ExitCode != 0)
			{
				string detail = ((stderr ?? "") + (stdout ?? "")).Trim();
				string cmd = string.Join(" ", args);
				if (cmd.Length > 120)
				{
					cmd = cmd.Substring(0, 120) + "...";
				}
				throw new IOException("git 失败(exit=" + p.ExitCode + ")[" + cmd + "]: " + detail);
			}
			return string.IsNullOrEmpty(stdout)
				? new string[0]
				: stdout.Split('\n');
		}
	}

	// ---------------- 审计 ----------------

	private static void Audit(string tool, string docName, string member, string note)
	{
		AuditLog.Write(tool, docName ?? "-", member ?? "-", false, "-",
			InvocationRisk.ModelChanging, true, note);
	}
}
