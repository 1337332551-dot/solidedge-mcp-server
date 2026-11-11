using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_recipe_run:按名执行一条已登记的配方。
///
/// 设计要点(与 <see cref="RecipeSpec"/> 头注释配套):
///   - **执行核零复制**:渲染占位符后直接调 <see cref="InvokeTools.se_invoke_chain"/>,
///     护栏判定/占位符语义/$N 解析/句柄登记全部沿用同一份实现,不存在"两套解析漂移"。
///   - **只读**:本工具永不写配方文件。落盘由 AI/人直接写 JSON(配方是纯数据)。
///   - **护栏不可绕过**:配方里的 risk 字段只是提示;这里按成员名重算一次(并可列出具体的危险步骤),
///     chain 内部还会再判一次。含 Destructive 步骤时仍要求 confirm=true。
///   - **能力边界**(必须让调用方知道):① steps 建议 ≤9(>9 静态校验会警告);② $N 只能引用前序步骤的
///     返回值,拿不到 out 参数里的对象;③ **本通道不支持写属性**(propertySet),需要写属性的流程请用
///     se_invoke_member(propertySet=true) 分步执行。
/// </summary>
[McpServerToolType]
public static class RecipeTools
{
	[McpServerTool]
	[Description("按名执行一条已登记的配方(JSON 文件,含 steps/inputs/preconditions/verification)。只执行,不创建也不修改配方文件。" +
		"args 形如 [\"setback=0.001\",\"target=obj-3\"],缺省取 inputs 的 default。" +
		"起始对象:配方里第一个 type=\"object\" 的 input(通常叫 target),调用前先用 se_get_selection 取 obj-N(注意它会清空句柄表)。" +
		"执行前做静态校验(引用完整性/步数/状态一致性)、前置条件校验(docKind/handleExists/handleType/selectionNonEmpty)与 {{inputName}} 占位符注入。" +
		"护栏:含破坏性成员(Delete/Cut/Drop/Erase/Purge/Remove*)时先拒绝并列出具体步骤,确认追加 confirm=true;risk 字段只是提示,以重算为准。" +
		"边界:①建议 ≤9 步(>10 步易超时) ②\"$N\" 只能引用前序步骤返回值,拿不到 out 参数 ③本通道不支持写属性,用 se_invoke_member(propertySet=true)。" +
		"验收:verification 判据不会自动执行,需随后自行调 se_snapshot_diff 核对。")]
	public static string se_recipe_run(
		SolidEdgeContext context,
		[Description("配方名(= 配方文件名去掉 .json);搜索路径见环境变量 SE_MCP_RECIPES_DIR 与 %LOCALAPPDATA%\\SolidEdgeSpy\\recipes,可用 CLI --recipes 列出全部")] string name,
		[Description("可选,形如 [\"setback=0.001\",\"target=obj-3\"] 的键值对列表;未提供的 input 取配方里的 default,无 default 且 required 则报错")] string[] args = null,
		[Description("可选,配方含破坏性成员(Delete/Cut/Drop/Erase/Purge/Remove* 等)时需显式置 true,否则整条配方在执行前被拒绝(报错会列出具体的危险步骤位置)。默认 false")] bool confirm = false)
	{
		try
		{
			return context.Invoke(delegate
			{
				RecipeRef recipeRef = RecipeStore.Find(name);
				if (recipeRef == null)
				{
					return Error("找不到配方 \"" + (name ?? "(空)") + "\"。已搜索目录: " + string.Join(" ; ", RecipeStore.SearchDirs()) + "。可用 CLI `solidedge-mcp --recipes` 列出全部配方。");
				}

				string text;
				try
				{
					text = File.ReadAllText(recipeRef.Path);
				}
				catch (Exception ex)
				{
					return Error("读取配方文件失败(" + recipeRef.Path + "): " + ex.Message);
				}

				RecipeSpec spec;
				try
				{
					using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions
					{
						CommentHandling = JsonCommentHandling.Skip,
						AllowTrailingCommas = true
					});
					spec = RecipeSpecParser.Parse(doc.RootElement, recipeRef.Path);
				}
				catch (JsonException ex2)
				{
					return Error("配方 JSON 语法错误(" + recipeRef.Path + "): " + ex2.Message);
				}
				catch (ArgumentException ex3)
				{
					return Error("配方结构错误(" + recipeRef.Path + "): " + ex3.Message);
				}

				List<RecipeIssue> issues = RecipeValidator.Validate(spec);
				List<object> errors = new List<object>();
				List<object> warnings = new List<object>();
				foreach (RecipeIssue it in issues)
				{
					if (it.Severity == "error")
					{
						errors.Add(new { where = it.Where, message = it.Message });
					}
					else
					{
						warnings.Add(new { where = it.Where, message = it.Message });
					}
				}
				if (errors.Count > 0)
				{
					return JsonSerializer.Serialize(new
					{
						status = "error",
						recipe = spec.Name,
						sourcePath = recipeRef.Path,
						message = "配方未通过静态校验,已拒绝执行(未触碰 Solid Edge)。",
						errors = errors,
						warnings = warnings,
						hint = "可用 CLI `solidedge-mcp --recipe-validate " + (spec.Name ?? name) + "` 复现同样的校验结果。"
					}, JsonOpts());
				}

				Dictionary<string, string> provided;
				string argError = ParseArgs(args, out provided);
				if (argError != null)
				{
					return Error(argError);
				}

				Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
				List<object> missing = new List<object>();
				List<string> unknown = new List<string>();
				HashSet<string> declaredNames = new HashSet<string>(StringComparer.Ordinal);
				foreach (InputSpec inp in spec.Inputs)
				{
					if (string.IsNullOrWhiteSpace(inp.Name))
					{
						continue;
					}
					declaredNames.Add(inp.Name);
					string val;
					if (provided.TryGetValue(inp.Name, out val))
					{
						values[inp.Name] = val;
					}
					else if (inp.HasDefault)
					{
						values[inp.Name] = inp.Default;
					}
					else if (inp.Required)
					{
						missing.Add(new
						{
							name = inp.Name,
							type = inp.Type,
							unit = inp.Unit,
							desc = inp.Desc
						});
					}
				}
				foreach (KeyValuePair<string, string> kv in provided)
				{
					if (!declaredNames.Contains(kv.Key))
					{
						unknown.Add(kv.Key);
					}
				}
				if (missing.Count > 0)
				{
					return JsonSerializer.Serialize(new
					{
						status = "error",
						recipe = spec.Name,
						message = "缺少必填参数,已拒绝执行。",
						missing = missing,
						hint = "按 args 格式补充,如 [\"target=obj-1\",\"setback=0.001\"];也可在配方里给该 input 加 default。"
					}, JsonOpts());
				}

				// 壳式配方(kind=script):没有声明式步骤,整段转交脚本通道。
				if (RecipeValidator.IsScriptKind(spec))
				{
					return RunScriptShell(context, spec, values, recipeRef, warnings, confirm);
				}

				// 起始对象:优先 args 里的 target,否则取第一个 object 类型的 input
				string startId = null;
				string startInputName = null;
				string targetVal;
				if (values.TryGetValue("target", out targetVal) && !string.IsNullOrWhiteSpace(targetVal))
				{
					startId = targetVal.Trim();
					startInputName = "target";
				}
				else
				{
					foreach (InputSpec inp2 in spec.Inputs)
					{
						string v2;
						if (string.Equals(inp2.Type, "object", StringComparison.OrdinalIgnoreCase)
							&& !string.IsNullOrWhiteSpace(inp2.Name)
							&& values.TryGetValue(inp2.Name, out v2)
							&& !string.IsNullOrWhiteSpace(v2))
						{
							startId = v2.Trim();
							startInputName = inp2.Name;
							break;
						}
					}
				}
				if (string.IsNullOrWhiteSpace(startId))
				{
					return Error("无法确定起始对象:配方需要声明一个 type=\"object\" 的 input(通常名为 target),并在 args 里传入 obj-N。传递前请先调用 se_get_selection 取得句柄(注意它会清空旧句柄表)。");
				}

				string preError = CheckPreconditions(context, spec, startId);
				if (preError != null)
				{
					return Error(preError);
				}

				JsonElement[] steps;
				string renderError;
				if (!RenderSteps(spec, values, out steps, out renderError))
				{
					return Error(renderError);
				}

				string riskError = CheckRecipeRisk(spec, steps, startId, confirm);
				if (riskError != null)
				{
					return riskError;
				}

				string chainJson = InvokeTools.se_invoke_chain(context, startId, steps, confirm);
				return Decorate(chainJson, spec, recipeRef, warnings, startInputName, startId);
			});
		}
		catch (Exception ex)
		{
			if (SolidEdgeContext.IsDisconnected(ex))
			{
				return Error("COM 对象已断连(文档可能已被关闭,或对象已被删除/句柄失效)。请重新调用 se_get_selection 获取新句柄,不要继续用旧 obj-N。原始错误: " + DescribeException(ex));
			}
			return Error("执行配方失败: " + DescribeException(ex));
		}
	}

	// ---------------- 壳式配方(kind=script) ----------------

	/// <summary>
	/// 执行壳式配方:把 inputs 渲染成脚本的 Main(string[] args) 参数,转交 se_script_run。
	///
	/// 两个必须的处理:
	///  1. type="object" 的 input 是 MCP 进程内存里的 obj-N 句柄,**脚本是独立子进程,拿不到那张表**。
	///     所以先把句柄换成文档全路径,作为 doc=&lt;路径&gt; 传给脚本,由脚本自己 Documents.Open。
	///     换不出来(句柄失效 / 不是文档对象)就跳过并在 recipeWarnings 里说明 —— 不能把 obj-N 硬塞给脚本,
	///     那只会让脚本拿着一串它根本解析不了的字符串去报一堆看不懂的错。
	///  2. 其余 input 原样转成 key=value,与 se_script_run 的 scriptArgs 约定一致。
	///
	/// 护栏:壳式配方没有步骤级分析,危险成员识别不出来,所以只看配方声明的 risk;
	/// 声明 destructive 时必须 confirm=true。
	/// </summary>
	private static string RunScriptShell(SolidEdgeContext context, RecipeSpec spec, Dictionary<string, string> values,
		RecipeRef recipeRef, List<object> warnings, bool confirm)
	{
		if (string.Equals(spec.Risk, "destructive", StringComparison.OrdinalIgnoreCase) && !confirm)
		{
			return JsonSerializer.Serialize(new
			{
				status = "error",
				recipe = spec.Name,
				message = "该配方声明了 risk=destructive,而整段脚本无法逐步判定危险操作,需 confirm=true 才执行。",
				hint = "先看脚本内容再决定:源码在沙箱 " + Path.Combine(Path.GetTempPath(), "sespy", spec.ScriptName ?? "", (spec.ScriptName ?? "") + ".cs") + "。"
			}, JsonOpts());
		}

		List<string> scriptArgs = new List<string>();
		string startId = null;
		foreach (InputSpec inp in spec.Inputs)
		{
			if (string.IsNullOrWhiteSpace(inp.Name))
			{
				continue;
			}
			string v;
			if (!values.TryGetValue(inp.Name, out v) || string.IsNullOrWhiteSpace(v))
			{
				continue;
			}
			if (string.Equals(inp.Type, "object", StringComparison.OrdinalIgnoreCase))
			{
				// 只处理第一个 object input(通常就是 target),多余的句柄对脚本没意义。
				if (startId == null)
				{
					startId = v.Trim();
					string docPath = TryResolveDocumentPath(context, startId, warnings);
					if (docPath != null)
					{
						scriptArgs.Add("doc=" + docPath);
					}
				}
				continue;
			}
			scriptArgs.Add(inp.Name + "=" + v);
		}

		// 2026-10-25 审计:兑现"脚本改动后 sha256 会提示失配"的承诺(此前 ScriptSha256 只在
		// --recipe-save 时写入,运行时从不比对)。口径与 --recipe-save 一致:沙箱 .cs 的字节流 SHA256。
		if (!string.IsNullOrEmpty(spec.ScriptSha256) && !string.IsNullOrEmpty(spec.ScriptName))
		{
			try
			{
				string sandboxCs = Path.Combine(Path.GetTempPath(), "sespy", spec.ScriptName, spec.ScriptName + ".cs");
				if (File.Exists(sandboxCs))
				{
					string currentSha;
					using (FileStream fs = File.OpenRead(sandboxCs))
					using (var sha256 = System.Security.Cryptography.SHA256.Create())
					{
						currentSha = BitConverter.ToString(sha256.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
					}
					if (!string.Equals(currentSha, spec.ScriptSha256, StringComparison.OrdinalIgnoreCase))
					{
						warnings.Add(new {
							code = "script_sha_mismatch",
							message = "脚本源码与固化时(--recipe-save)的 SHA256 不一致,配方可能已失配:沙箱脚本在固化后被改动过。如属有意更新,请重新 --recipe-save 固化;否则请核对脚本内容再继续。"
						});
					}
				}
				else
				{
					warnings.Add(new {
						code = "script_sha_unverifiable",
						message = "沙箱里没有脚本源码(" + sandboxCs + "),无法比对 SHA256(脚本将在本次运行时重新生成)。"
					});
				}
			}
			catch
			{
				// 指纹核验失败不阻塞执行,与 --recipe-save 侧"算不出就跳过"同一取向
			}
		}

		string scriptJson = ScriptTools.se_script_run(context, spec.ScriptName, null, null, 300, scriptArgs.ToArray());
		return Decorate(scriptJson, spec, recipeRef, warnings, null, startId);
	}

	/// <summary>obj-N 句柄 → 文档全路径(脚本子进程唯一能用的文档定位方式)。读不到返回 null 并记 warning。</summary>
	private static string TryResolveDocumentPath(SolidEdgeContext context, string handleId, List<object> warnings)
	{
		try
		{
			ObjectHandle h = context.GetHandle(handleId);
			if (h == null || h.ComObject == null)
			{
				warnings.Add(new { where = "target", message = "句柄 " + handleId + " 不在句柄表里,未向脚本传 doc=,脚本需自己定位活动文档。" });
				return null;
			}
			object val;
			Exception err;
			if (ManualInvoke.TryInvoke(h.ComObject, "FullName", new object[0], out val, out err))
			{
				string path = val as string;
				if (!string.IsNullOrWhiteSpace(path))
				{
					return path;
				}
			}
			warnings.Add(new { where = "target", message = "读不到 " + handleId + " 的 FullName(可能不是文档对象),未向脚本传 doc=。" });
			return null;
		}
		catch (Exception ex)
		{
			warnings.Add(new { where = "target", message = "解析文档路径失败: " + ex.Message });
			return null;
		}
	}

	// ---------------- 参数 ----------------

	private static string ParseArgs(string[] args, out Dictionary<string, string> map)
	{
		map = new Dictionary<string, string>(StringComparer.Ordinal);
		if (args == null || args.Length == 0)
		{
			return null;
		}
		foreach (string raw in args)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}
			int idx = raw.IndexOf('=');
			if (idx <= 0)
			{
				return "参数格式错误: \"" + raw + "\"。应形如 \"名字=值\",例如 \"target=obj-3\" / \"setback=0.001\"。";
			}
			string key = raw.Substring(0, idx).Trim();
			string val = raw.Substring(idx + 1);
			if (key.Length == 0)
			{
				return "参数格式错误: \"" + raw + "\"(等号左边没有名字)。";
			}
			map[key] = val;
		}
		return null;
	}

	private static JsonSerializerOptions JsonOpts()
	{
		return new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
	}

	// ---------------- 渲染 ----------------

	/// <summary>把 {{inputName}} 占位符替换成实参(做 JSON 转义),产出与 se_invoke_chain 完全同构的 steps。</summary>
	private static bool RenderSteps(RecipeSpec spec, Dictionary<string, string> values, out JsonElement[] steps, out string error)
	{
		steps = null;
		error = null;
		JsonElement rawSteps;
		if (!spec.Raw.TryGetProperty("steps", out rawSteps) || rawSteps.ValueKind != JsonValueKind.Array)
		{
			error = "配方没有 steps 数组。";
			return false;
		}
		string text = rawSteps.GetRawText();
		// 值是"数据",必须按 JSON 字符串转义后再落到文本里,否则含引号/反斜杠的实参会破坏 JSON 结构。
		text = RecipeValidator.PlaceholderPattern.Replace(text, delegate (Match m)
		{
			string nm = m.Groups[1].Value;
			string v;
			if (values.TryGetValue(nm, out v))
			{
				return JsonEncodedText.Encode(v ?? "", JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
			}
			return m.Value;
		});
		Match leftover = RecipeValidator.PlaceholderPattern.Match(text);
		if (leftover.Success)
		{
			error = "步骤里仍有未解析的占位符 " + leftover.Value + "(该 input 没有值,也没有 default)。";
			return false;
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(text);
			List<JsonElement> list = new List<JsonElement>();
			foreach (JsonElement e in doc.RootElement.EnumerateArray())
			{
				list.Add(e.Clone());
			}
			steps = list.ToArray();
			return true;
		}
		catch (JsonException ex)
		{
			error = "占位符替换后的 steps 不是合法 JSON(通常是实参里含特殊字符): " + ex.Message;
			return false;
		}
	}

	// ---------------- 前置条件 ----------------

	private static string CheckPreconditions(SolidEdgeContext context, RecipeSpec spec, string startId)
	{
		foreach (PreconditionSpec p in spec.Preconditions)
		{
			string kind = (p.Kind ?? "").Trim();
			if (kind.Length == 0)
			{
				continue;
			}
			string hint = string.IsNullOrWhiteSpace(p.Hint) ? "" : ("(" + p.Hint + ")");
			if (kind.Equals("docKind", StringComparison.OrdinalIgnoreCase))
			{
				string actual = TryGetDocTypeShort(context);
				if (actual == null)
				{
					return "前置条件校验失败: 读不到活动文档类型,无法校验 docKind=" + p.Value + hint + "。";
				}
				if ((p.Value ?? "").Length > 0 && actual.IndexOf(p.Value, StringComparison.OrdinalIgnoreCase) < 0)
				{
					return "前置条件不满足: 配方要求 " + p.Value + " 文档,当前活动文档是 " + actual + "。" + hint;
				}
			}
			else if (kind.Equals("handleExists", StringComparison.OrdinalIgnoreCase))
			{
				if (string.IsNullOrWhiteSpace(startId) || context.GetHandle(startId) == null)
				{
					return "前置条件不满足: 起始对象句柄 " + (startId ?? "(未提供)") + " 不在句柄表里" + hint + "。请先 se_get_selection(会清空并重建句柄表)再重跑配方。";
				}
			}
			else if (kind.Equals("handleType", StringComparison.OrdinalIgnoreCase))
			{
				ObjectHandle handle = string.IsNullOrWhiteSpace(startId) ? null : context.GetHandle(startId);
				if (handle == null)
				{
					return "前置条件不满足: 起始对象句柄 " + (startId ?? "(未提供)") + " 不存在,无法校验 handleType=" + p.Value + "。";
				}
				if ((p.Value ?? "").Length > 0 && (handle.TypeName ?? "").IndexOf(p.Value, StringComparison.OrdinalIgnoreCase) < 0)
				{
					return "前置条件不满足: 起始对象类型是 " + handle.TypeName + ",配方要求类型名含 \"" + p.Value + "\"" + hint + "。";
				}
			}
			else if (kind.Equals("selectionNonEmpty", StringComparison.OrdinalIgnoreCase))
			{
				if (TryGetSelectionCount(context) <= 0)
				{
					return "前置条件不满足: 需要先在 Solid Edge 中选中对象" + hint + "。";
				}
			}
		}
		return null;
	}

	/// <summary>读活动文档的 COM 类型短名(如 PartDocument / DraftDocument)。走 se_get_document,失败返回 null。</summary>
	private static string TryGetDocTypeShort(SolidEdgeContext context)
	{
		try
		{
			string json = DocumentTools.se_get_document(context);
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement docEl;
			if (doc.RootElement.TryGetProperty("document", out docEl) && docEl.ValueKind == JsonValueKind.Object)
			{
				JsonElement t;
				if (docEl.TryGetProperty("typeShort", out t) && t.ValueKind == JsonValueKind.String)
				{
					return t.GetString();
				}
				if (docEl.TryGetProperty("type", out t) && t.ValueKind == JsonValueKind.String)
				{
					return t.GetString();
				}
			}
		}
		catch
		{
		}
		return null;
	}

	/// <summary>读 ActiveSelectSet.Count。刻意【不】调 se_get_selection —— 那会清空句柄表,把这次配方要用的 obj-N 一起毁掉。</summary>
	private static int TryGetSelectionCount(SolidEdgeContext context)
	{
		try
		{
			object application = context.GetApplication();
			object selectSet = application.GetType().InvokeMember("ActiveSelectSet", BindingFlags.GetProperty, null, application, null);
			if (selectSet == null)
			{
				return 0;
			}
			object count = selectSet.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, selectSet, null);
			return (count is int) ? ((int)count) : 0;
		}
		catch
		{
			return 0;
		}
	}

	// ---------------- 配方级护栏 ----------------

	/// <summary>
	/// 按成员名重算整条配方的最高危级别。含 Destructive 且未 confirm 时返回错误(并列出具体步骤位置)。
	/// 配方把步骤藏在文件里,调用方的"确认"必须针对列出来的具体步骤,而不是配方名。
	/// </summary>
	private static string CheckRecipeRisk(RecipeSpec spec, JsonElement[] steps, string startId, bool confirm)
	{
		InvocationRisk worst = InvocationRisk.Normal;
		string worstMember = null;
		List<object> destructiveSpots = new List<object>();
		for (int i = 0; i < steps.Length; i++)
		{
			JsonElement memberEl;
			if (!steps[i].TryGetProperty("member", out memberEl) || memberEl.ValueKind != JsonValueKind.String)
			{
				continue;
			}
			string member = memberEl.GetString();
			InvocationRisk risk = Guardrail.Classify(member, false);
			if (risk > worst)
			{
				worst = risk;
				worstMember = member;
			}
			if (risk == InvocationRisk.Destructive)
			{
				string on = null;
				JsonElement onEl;
				if (steps[i].TryGetProperty("on", out onEl) && onEl.ValueKind == JsonValueKind.String)
				{
					on = onEl.GetString();
				}
				destructiveSpots.Add(new { step = i + 1, member = member, on = on });
			}
		}

		if (worst == InvocationRisk.Destructive && !confirm)
		{
			return JsonSerializer.Serialize(new
			{
				status = "error",
				recipe = spec.Name,
				message = "配方含破坏性步骤,已在执行前拒绝。确认请追加 confirm=true 再调用一次。",
				destructiveSteps = destructiveSpots,
				hint = "这些步骤来自配方文件而非本次对话,请先核对上面列出的具体步骤(成员名/作用对象)再决定是否确认。"
			}, JsonOpts());
		}

		// 2026-09-14 台账改造：原先只在 worst != Normal 时写审计 → 纯 Normal 配方（全读类）在
		// 通道 A 里完全不可见，台账只能靠 B 手记（方案 §5.6 的"纯 Normal 全空"缺口）。
		// 现在总是写，并把 verification 声明写进 note。
		// ⚠️ note 只说明"判据是否声明"，**不代表判据结果**——配方不声明快照 target，
		// 判据无法在此自动执行，仍需按 recipes/README §五 的 save→run→diff 流程验收。
		string verifyNote = string.IsNullOrWhiteSpace(spec.VerifyName)
			? "verification=none"
			: "verification=declared:" + spec.VerifyKind + "/" + spec.VerifyName + "/" + spec.VerifyExpect;
		// 2026-10-25 审计修正:此处执行必然 allowed=true(破坏性未确认已在上面拦截),
		// 原代码把 confirm 传给 allowed,导致实际执行的纯 Normal/ModelChanging 配方在台账记成 DENY。
		AuditLog.Write("se_recipe_run", startId, "(recipe:" + (spec.Name ?? "?") + ")", false,
			steps.Length + " steps, worst=" + worstMember, worst, true, verifyNote);
		return null;
	}

	// ---------------- 输出 ----------------

	/// <summary>在 se_invoke_chain 的结果上补配方元信息(不改动它的任何字段)。</summary>
	private static string Decorate(string chainJson, RecipeSpec spec, RecipeRef recipeRef, List<object> warnings, string startInputName, string startId)
	{
		try
		{
			JsonNode node = JsonNode.Parse(chainJson);
			JsonObject obj = node as JsonObject;
			if (obj == null)
			{
				return chainJson;
			}
			obj["recipe"] = spec.Name;
			obj["recipeSource"] = recipeRef.Path;
			obj["recipeKind"] = spec.Kind;
			obj["recipeStatus"] = spec.Status;
			obj["startObject"] = startId;
			if (startInputName != null && startInputName != "target")
			{
				obj["startInput"] = startInputName;
			}
			if (warnings != null && warnings.Count > 0)
			{
				obj["recipeWarnings"] = JsonSerializer.SerializeToNode(warnings, JsonOpts());
			}
			if (!string.IsNullOrWhiteSpace(spec.VerifyName))
			{
				obj["verification"] = new JsonObject
				{
					["kind"] = spec.VerifyKind,
					["name"] = spec.VerifyName,
					["expect"] = spec.VerifyExpect,
					["hint"] = "配方声明的验收判据,不会自动执行。请自行调用 se_snapshot_diff 核对(expect=" + (spec.VerifyExpect ?? "?") + " 即通过)。"
				};
			}
			return obj.ToJsonString(JsonOpts());
		}
		catch
		{
			// 装饰失败不影响结果本身
			return chainJson;
		}
	}

	// ---------------- CLI 入口(不是 MCP 工具:不碰 COM、不依赖常驻会话,故走 CLI 省工具额度) ----------------

	/// <summary>CLI `--recipes`:列出全部配方(名称/状态/锚定版本/参数/来源目录)并顺带做一次静态校验。纯本地。</summary>
	internal static string FormatRecipeList()
	{
		List<RecipeRef> refs = RecipeStore.List();
		StringBuilder sb = new StringBuilder();
		sb.Append("=== 配方清单 (").Append(refs.Count).Append(") ===").Append(Environment.NewLine);
		sb.Append("搜索路径: ").Append(string.Join(" ; ", RecipeStore.SearchDirs())).Append(Environment.NewLine);
		if (refs.Count == 0)
		{
			sb.Append("(没有找到配方文件。把 <配方名>.json 放进上面的任一目录即可;以 _ 开头的文件不登记。)").Append(Environment.NewLine);
			return sb.ToString();
		}
		sb.Append(Environment.NewLine);
		foreach (RecipeRef r in refs)
		{
			string status = "?";
			string title = null;
			string anchor = null;
			string inputs = null;
			int steps = 0;
			string kind = null;
			string scriptName = null;
			List<string> errs = new List<string>();
			try
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(r.Path), new JsonDocumentOptions
				{
					CommentHandling = JsonCommentHandling.Skip,
					AllowTrailingCommas = true
				});
				RecipeSpec spec = RecipeSpecParser.Parse(doc.RootElement, r.Path);
				status = spec.Status;
				title = spec.Title;
				anchor = spec.AnchorSe;
				steps = spec.Steps.Count;
				kind = spec.Kind;
				scriptName = spec.ScriptName;
				List<string> ps = new List<string>();
				foreach (InputSpec i in spec.Inputs)
				{
					if (string.IsNullOrWhiteSpace(i.Name))
					{
						continue;
					}
					string mark = (i.HasDefault ? ("=" + i.Default) : (i.Required ? "(必填)" : ""));
					ps.Add(i.Name + mark);
				}
				inputs = string.Join(", ", ps);
				foreach (RecipeIssue it in RecipeValidator.Validate(spec))
				{
					if (it.Severity == "error")
					{
						errs.Add(it.Where + ": " + it.Message);
					}
				}
			}
			catch (Exception ex)
			{
				errs.Add("解析失败: " + ex.Message);
			}
			sb.Append("- ").Append(r.Name).Append("  [").Append(status).Append(']');
			if (title != null)
			{
				sb.Append("  ").Append(title);
			}
			sb.Append(Environment.NewLine);
			if (string.Equals(kind, "script", StringComparison.OrdinalIgnoreCase))
			{
				// 壳式配方没有声明式步骤,显示"步骤数=0"会让人以为坏掉了
				sb.Append("    脚本=").Append(scriptName ?? "(未声明)").Append("  (kind=script,走脚本通道)");
			}
			else
			{
				sb.Append("    步骤数=").Append(steps);
			}
			if (anchor != null)
			{
				sb.Append("  锚定 SE=").Append(anchor);
			}
			sb.Append(Environment.NewLine);
			if (!string.IsNullOrEmpty(inputs))
			{
				sb.Append("    参数: ").Append(inputs).Append(Environment.NewLine);
			}
			sb.Append("    文件: ").Append(r.Path).Append(Environment.NewLine);
			if (errs.Count > 0)
			{
				sb.Append("    [校验错误] ").Append(string.Join(" | ", errs)).Append(Environment.NewLine);
			}
		}
		return sb.ToString();
	}

	/// <summary>CLI `--recipe-validate &lt;名字|路径&gt;`:静态校验(不碰 COM,SE 没开也能跑)。</summary>
	internal static string ValidateForCli(string nameOrPath)
	{
		if (string.IsNullOrWhiteSpace(nameOrPath))
		{
			return "用法: solidedge-mcp --recipe-validate <配方名|json 路径>";
		}
		RecipeRef r = RecipeStore.Find(nameOrPath);
		if (r == null)
		{
			return "找不到配方 \"" + nameOrPath + "\"。搜索路径: " + string.Join(" ; ", RecipeStore.SearchDirs()) + "。可用 --recipes 查看全部。";
		}
		string text;
		try
		{
			text = File.ReadAllText(r.Path);
		}
		catch (Exception ex)
		{
			return "读取失败(" + r.Path + "): " + ex.Message;
		}
		RecipeSpec spec;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions
			{
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true
			});
			spec = RecipeSpecParser.Parse(doc.RootElement, r.Path);
		}
		catch (JsonException jex)
		{
			return "JSON 语法错误: " + jex.Message;
		}
		catch (ArgumentException aex)
		{
			return "结构错误: " + aex.Message;
		}

		List<RecipeIssue> issues = RecipeValidator.Validate(spec);
		int errN = 0;
		int warnN = 0;
		StringBuilder sb = new StringBuilder();
		sb.Append("=== 配方校验: ").Append(spec.Name ?? r.Name).Append(" ===").Append(Environment.NewLine);
		sb.Append("文件: ").Append(r.Path).Append(Environment.NewLine);
		sb.Append("状态: ").Append(spec.Status).Append("  步骤数: ").Append(spec.Steps.Count).Append(Environment.NewLine);
		foreach (RecipeIssue it in issues)
		{
			if (it.Severity == "error")
			{
				errN++;
			}
			else
			{
				warnN++;
			}
			sb.Append('[').Append(it.Severity).Append("] ").Append(it.Where).Append(": ").Append(it.Message).Append(Environment.NewLine);
		}
		sb.Append(new string('-', 60)).Append(Environment.NewLine);
		sb.Append((errN == 0) ? "通过(errors=0" : ("不通过(errors=" + errN));
		sb.Append(", warnings=").Append(warnN).Append(")。");
		if (errN > 0)
		{
			sb.Append(" -> 该配方会被 se_recipe_run 拒绝执行。");
		}
		return sb.ToString();
	}

	// ---------------- CLI:把跑通的脚本固化成配方 ----------------

	/// <summary>
	/// CLI `--recipe-save &lt;脚本名&gt; [选项]`:把沙箱里已经跑通的 se_script_run 脚本固化成一条 kind=script 配方。
	///
	/// 为什么走 CLI 而不是新增 MCP 工具:
	///  - 纯本地文件操作(读 .cs、写 .json),不碰 COM,不需要常驻会话的句柄表;
	///  - 与 --recipes / --recipe-validate / --usage 同惯例,不占 MCP 工具额度(工具描述是常驻上下文的);
	///  - 固化是低频、需要有意识的动作,放 CLI 里天然挡住"随手堆一堆垃圾配方"。
	/// </summary>
	internal static string SaveFromScriptForCli(string[] args)
	{
		if (args == null || args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--"))
		{
			return SaveUsage();
		}
		string scriptName = args[1].Trim();

		string recipeName = scriptName;
		string title = null;
		string risk = "modelChanging";
		string verify = null;
		string sourceDoc = "part";
		string seVersion = "2022";
		bool project = false;
		bool force = false;
		bool dry = false;
		bool allowOnce = false;
		List<string> inputSpecs = new List<string>();

		for (int i = 2; i < args.Length; i++)
		{
			string a = args[i];
			switch (a)
			{
				case "--name": if (i + 1 < args.Length) { recipeName = args[++i]; } break;
				case "--title": if (i + 1 < args.Length) { title = args[++i]; } break;
				case "--input": if (i + 1 < args.Length) { inputSpecs.Add(args[++i]); } break;
				case "--risk": if (i + 1 < args.Length) { risk = args[++i]; } break;
				case "--verify": if (i + 1 < args.Length) { verify = args[++i]; } break;
				case "--doc": if (i + 1 < args.Length) { sourceDoc = args[++i]; } break;
				case "--se": if (i + 1 < args.Length) { seVersion = args[++i]; } break;
				case "--project": project = true; break;
				case "--force": force = true; break;
				case "--dry": dry = true; break;
				case "--allow-once": allowOnce = true; break;
				default:
					return "不认识的选项: " + a + Environment.NewLine + SaveUsage();
			}
		}

		if (!RecipeValidator.IsSafeName(scriptName))
		{
			return "脚本名非法(只允许字母/数字/下划线/短横线,1~64 位): \"" + scriptName + "\"";
		}
		if (!RecipeValidator.IsSafeName(recipeName))
		{
			return "配方名非法(只允许字母/数字/下划线/短横线/点,1~64 位): \"" + recipeName + "\"";
		}

		string sandbox = Path.Combine(Path.GetTempPath(), "sespy", scriptName);
		string csPath = Path.Combine(sandbox, scriptName + ".cs");
		if (!File.Exists(csPath))
		{
			return "沙箱里没有脚本 \"" + scriptName + "\" 的源码(" + csPath + ")。" + Environment.NewLine +
				"先用 se_script_run 跑通一次,脚本源码会自动留在沙箱里,再来固化。";
		}

		// 源码指纹:脚本被改过时,se_recipe_run 能据此提示"配方可能已失配"。
		string sha = null;
		try
		{
			using (FileStream fs = File.OpenRead(csPath))
			{
				using (var sha256 = System.Security.Cryptography.SHA256.Create())
				{
					sha = BitConverter.ToString(sha256.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
				}
			}
		}
		catch
		{
			// 算不出来就别写这个字段,不影响固化
		}

		// ---- 入库准入:挡掉"只跑过一次""最近一次是失败""没写用途"的脚本 ----
		// 配方库的价值在于"库里每一条都能跑",放进来一条跑不起来的,以后就没人敢用这个库了。
		ScriptTools.ScriptRunStats stats = ScriptTools.ReadRunStats(scriptName);
		string gateError = CheckSaveGate(scriptName, title, stats, allowOnce);
		if (gateError != null)
		{
			return gateError;
		}
		int runs = stats.Total;

		List<JsonObject> inputNodes = new List<JsonObject>();
		foreach (string raw in inputSpecs)
		{
			// 分隔符统一用 ':'。default 常是 Windows 路径(C:\temp\x.csv),会被盘符的冒号切碎,
			// 所以第 3 段只有一个字母(盘符)时,把它和第 4 段拼回完整路径。
			string[] parts = raw.Split(new char[] { ':' });
			string nm = (parts.Length > 0) ? parts[0].Trim() : null;
			if (string.IsNullOrWhiteSpace(nm))
			{
				return "--input 缺少参数名: \"" + raw + "\"。格式: name:type[:default][:desc]";
			}
			if (parts.Length < 2 || parts[1].Trim().Length == 0)
			{
				return "--input 缺少 type: \"" + raw + "\"。格式: name:type[:default][:desc],type 取 object/objectArray/double/int/string/bool";
			}
			string tp = parts[1].Trim();
			string dflt = null;
			string desc = null;
			if (parts.Length > 2)
			{
				int idx = 2;
				string head = parts[idx].Trim();
				if (head.Length == 1 && char.IsLetter(head[0]) && parts.Length > 3
					&& (parts[3].StartsWith("\\", StringComparison.Ordinal) || parts[3].StartsWith("/", StringComparison.Ordinal)))
				{
					// 盘符特例:"C" + ":" + "\temp\x.csv"。必须同时满足"单个字母 + 下一段是路径",
					// 否则 seed:int:7:随机种子 这种会被误并成 "7:随机种子"。
					dflt = head + ":" + parts[3];
					idx = 4;
				}
				else
				{
					dflt = parts[idx];
					idx = 3;
				}
				if (dflt != null && dflt.Length == 0)
				{
					dflt = null;    // 空段 = 没有 default
				}
				if (idx < parts.Length)
				{
					// desc 允许再出现冒号(中文冒号或英文冒号都原样保留)
					desc = string.Join(":", parts, idx, parts.Length - idx);
				}
			}
			JsonObject io = new JsonObject();
			io["name"] = nm;
			io["type"] = tp;
			// 约定:第三段为空串 = 没有 default(与"default 是空串"不做区分,壳式配方用不到空串默认值)。
			if (!string.IsNullOrEmpty(dflt))
			{
				io["default"] = dflt;
			}
			else
			{
				io["required"] = true;
			}
			if (!string.IsNullOrEmpty(desc))
			{
				io["desc"] = desc;
			}
			inputNodes.Add(io);
		}

		JsonObject scriptNode = new JsonObject();
		scriptNode["name"] = scriptName;
		scriptNode["path"] = csPath;
		if (sha != null)
		{
			scriptNode["sha256"] = sha;
		}

		JsonObject root = new JsonObject();
		root["name"] = recipeName;
		root["title"] = string.IsNullOrWhiteSpace(title) ? (scriptName + "(脚本固化)") : title;
		root["version"] = 1;
		root["status"] = "draft";
		root["kind"] = "script";
		root["risk"] = risk;
		root["anchoredTo"] = new JsonObject { ["se"] = seVersion, ["sourceDoc"] = sourceDoc };
		root["script"] = scriptNode;
		root["inputs"] = new JsonArray(inputNodes.ToArray());
		root["steps"] = new JsonArray();
		root["verification"] = new JsonObject
		{
			["kind"] = string.IsNullOrWhiteSpace(verify) ? "none" : "declared",
			["expect"] = string.IsNullOrWhiteSpace(verify) ? "整段脚本执行,无快照级验收;判据看脚本自身输出" : verify
		};
		root["derivedFrom"] = "se_script_run 固化(--recipe-save)";
		root["notes"] = DateTime.Now.ToString("yyyy-MM-dd") + " 由 --recipe-save 从沙箱脚本 \"" + scriptName + "\" 固化(固化前运行 "
			+ runs + " 次,其中成功 " + stats.Ok + " 次)"
			+ (allowOnce ? ",【准入门槛已用 --allow-once 豁免】" : "")
			+ "。执行走脚本通道(steps 为空);risk 仅为声明值,不做步骤级危险识别;"
			+ "object 类型的 input 会被换成文档全路径以 doc=<路径> 传给脚本(子进程拿不到句柄表)。";

		// 落盘/预览都用缩进格式:配方是给人看的纯数据,单行 JSON 改起来太痛苦。
		string json = root.ToJsonString(new JsonSerializerOptions
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			WriteIndented = true
		});

		// 落盘前先按正式流程解析 + 静态校验:存进去却跑不了的配方,比没有配方更糟。
		RecipeSpec parsed;
		try
		{
			using (JsonDocument doc = JsonDocument.Parse(json))
			{
				parsed = RecipeSpecParser.Parse(doc.RootElement, "(生成中)");
			}
		}
		catch (Exception ex)
		{
			return "生成的配方解析失败(内部错误): " + ex.Message + Environment.NewLine + json;
		}

		List<string> errs = new List<string>();
		List<string> warns = new List<string>();
		foreach (RecipeIssue it in RecipeValidator.Validate(parsed))
		{
			if (it.Severity == "error")
			{
				errs.Add(it.Where + ": " + it.Message);
			}
			else
			{
				warns.Add(it.Where + ": " + it.Message);
			}
		}
		if (errs.Count > 0)
		{
			return "生成的配方未通过静态校验,已拒绝落盘:" + Environment.NewLine +
				"  " + string.Join(Environment.NewLine + "  ", errs) + Environment.NewLine + Environment.NewLine + json;
		}

		if (dry)
		{
			StringBuilder dryOut = new StringBuilder();
			dryOut.Append("=== --recipe-save 预演(未落盘) ===").Append(Environment.NewLine);
			dryOut.Append("脚本: ").Append(scriptName)
				.Append("  (台账 ").Append(runs).Append(" 次,成功 ").Append(stats.Ok).Append(" 次 / ").Append(stats.OkDays).Append(" 天)")
				.Append(Environment.NewLine);
			dryOut.Append("目标: ").Append(Path.Combine(RecipeStore.SaveDir(project), recipeName + ".json")).Append(Environment.NewLine);
			if (warns.Count > 0)
			{
				dryOut.Append("提示: ").Append(string.Join(" | ", warns)).Append(Environment.NewLine);
			}
			dryOut.Append(Environment.NewLine).Append(json).Append(Environment.NewLine);
			dryOut.Append(Environment.NewLine).Append("确认无误后去掉 --dry 重跑即可落盘。").Append(Environment.NewLine);
			return dryOut.ToString();
		}

		string dir = RecipeStore.SaveDir(project);
		string targetPath = Path.Combine(dir, recipeName + ".json");

		RecipeRef existingSameName = RecipeStore.Find(recipeName);
		if (existingSameName != null && !force)
		{
			return "已存在同名配方: " + existingSameName.Path + Environment.NewLine +
				"覆盖请加 --force(注意搜索顺序:仓库目录优先于用户目录,写在用户目录的同名配方可能被仓库里的遮蔽)。" + Environment.NewLine + Environment.NewLine + json;
		}
		if (File.Exists(targetPath) && !force)
		{
			return "目标文件已存在: " + targetPath + "。覆盖请加 --force。";
		}

		try
		{
			Directory.CreateDirectory(dir);
			File.WriteAllText(targetPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch (Exception ex)
		{
			return "写配方文件失败(" + targetPath + "): " + ex.Message;
		}

		StringBuilder sb = new StringBuilder();
		sb.Append("已固化: ").Append(targetPath).Append(Environment.NewLine);
		sb.Append("脚本: ").Append(scriptName)
			.Append("  (台账 ").Append(runs).Append(" 次,成功 ").Append(stats.Ok).Append(" 次 / ").Append(stats.OkDays).Append(" 天)")
			.Append(Environment.NewLine);
		if (warns.Count > 0)
		{
			sb.Append("提示: ").Append(string.Join(" | ", warns)).Append(Environment.NewLine);
		}
		sb.Append(Environment.NewLine).Append("下一步:").Append(Environment.NewLine);
		sb.Append("  1) 校验: solidedge-mcp --recipe-validate ").Append(recipeName).Append(Environment.NewLine);
		sb.Append("  2) 试跑: se_recipe_run ").Append(recipeName);
		if (inputNodes.Count > 0)
		{
			sb.Append(" args=[\"target=obj-1\"]");
		}
		sb.Append(Environment.NewLine);
		sb.Append("注意:状态是 draft。脚本通道不做步骤级危险识别,risk 只是声明值;脚本 sha256 会在运行时与固化值比对,失配将给出 script_sha_mismatch 警告(不阻塞执行)。").Append(Environment.NewLine);
		return sb.ToString();
	}

	/// <summary>
	/// --recipe-save 的入库准入。默认严格,五条全过才让进:
	///   ① 必须给 --title(没用途说明的配方,过几天没人敢删也没人敢用);
	///   ② 脚本名不能是探针类(probe/dump/test/tmp/temp)—— 这类只对某一次的现场有意义;
	///   ③ 成功运行过 ≥2 次(只跑一次的多半还在探索期,固化=垃圾);
	///   ④ 成功的日期 ≥2 天(同一天连跑两遍说明不了什么,隔天还能跑通才算稳);
	///   ⑤ 最近一次运行是成功的(别把坏状态封进配方)。
	///
	/// ②~⑤ 可用 --allow-once 显式豁免,但会在配方 notes 里留"门禁已豁免"的痕迹 ——
	/// 豁免可以有,不能悄悄有。
	/// </summary>
	/// <summary>探针类脚本名前缀(去掉前导下划线/短横线后判断,能命中 _probe_tune 这种)。</summary>
	private static readonly string[] ProbePrefixes = { "probe", "dump", "test", "tmp", "temp" };

	private static bool LooksLikeProbe(string scriptName)
	{
		string n = (scriptName ?? "").Trim().TrimStart('_', '-').ToLowerInvariant();
		if (n.Length == 0)
		{
			return false;
		}
		foreach (string p in ProbePrefixes)
		{
			if (n.StartsWith(p, StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private static string CheckSaveGate(string scriptName, string title, ScriptTools.ScriptRunStats stats, bool allowOnce)
	{
		// ① 用途说明:不可豁免
		if (string.IsNullOrWhiteSpace(title))
		{
			return "缺少 --title:配方必须有能看懂的用途说明,否则过几天没人知道这条配方是干什么的、敢不敢删。" + Environment.NewLine +
				"例: --title \"装配体按种子随机上色\"";
		}
		if (allowOnce)
		{
			return null;
		}

		// ② 探针类命名:天然一次性探索,固化要多一道确认
		if (LooksLikeProbe(scriptName))
		{
			return "\"" + scriptName + "\" 看起来是探针/诊断类脚本(probe*/dump*/test*/tmp*),这类脚本通常只对某一次的现场有意义," +
				"固化后很容易变成配方库里的垃圾条目 —— 库里每多一条跑不起来或没人用的配方,这个库的可信度就掉一截。" + Environment.NewLine +
				"确认要固化请加 --allow-once;如果是长期任务,建议先把脚本改成有明确语义的名字再跑一次。";
		}

		if (!stats.HasAny)
		{
			return "沙箱台账里没有 \"" + scriptName + "\" 的运行记录,无法确认它真的跑通过(注意:沙箱里的 .cs 是编译前就写盘的," +
				"编译失败也会留下文件,所以只看文件存在不够)。" + Environment.NewLine +
				"先 se_script_run 成功跑一次再来固化;确实要登记未验证的东西,加 --allow-once。";
		}
		if (stats.Ok < 2)
		{
			return "该脚本只成功运行过 " + stats.Ok + " 次(台账共 " + stats.Total + " 次),默认要求至少 2 次成功 —— " +
				"只跑一次的多半还在探索期,固化下来大概率是垃圾配方。" + Environment.NewLine +
				"确认要固化请加 --allow-once(会在配方 notes 里留下豁免痕迹)。";
		}

		// ③ 跨天验证:两天各跑通一次,比同一天连跑两遍强得多
		if (stats.OkDays < 2)
		{
			return "该脚本的成功记录只覆盖 " + stats.OkDays + " 天(成功 " + stats.Ok + " 次),要求跨 2 天 —— " +
				"同一天连跑两遍说明不了什么,隔天还能跑通才说明它不依赖当时的临时状态(文档还开着、选中还在、变量还是那个值)。" + Environment.NewLine +
				"明天再跑通一次即可自动通过;确认现在就要固化请加 --allow-once。";
		}

		if (!stats.LastOk)
		{
			return "该脚本最近一次运行没有成功(状态 " + (stats.LastStatus ?? "unknown") + "),现在固化等于把坏状态封进配方。" + Environment.NewLine +
				"先修好再跑通一次;确认要固化请加 --allow-once。";
		}
		return null;
	}

	private static string SaveUsage()
	{
		return "用法: solidedge-mcp --recipe-save <脚本名> [选项]" + Environment.NewLine +
			"  把沙箱(%TEMP%\\sespy\\<脚本名>\\)里已经跑通的 se_script_run 脚本固化成一条 kind=script 配方。" + Environment.NewLine +
			"  纯本地操作,不碰 Solid Edge。" + Environment.NewLine +
			Environment.NewLine +
			"选项:" + Environment.NewLine +
			"  --name <名>        配方名,默认=脚本名" + Environment.NewLine +
			"  --title \"...\"      一句话说明用途" + Environment.NewLine +
			"  --input <规格>     可重复。规格 = name:type[:default][:desc]" + Environment.NewLine +
			"                     type 取 object/objectArray/double/int/string/bool;第三段留空=必填" + Environment.NewLine +
			"                     default 是 Windows 路径时照常写,如 out:string:C:\\a\\b.csv:导出路径(盘符冒号会自动识别)" + Environment.NewLine +
			"  --risk <档>        normal|modelChanging|destructive,默认 modelChanging(声明值)" + Environment.NewLine +
			"  --verify \"...\"     期望结果。draft 可不给;给了会写进 verification.expect" + Environment.NewLine +
			"  --doc <类型>       part|assembly|draft,写进 anchoredTo.sourceDoc,默认 part" + Environment.NewLine +
			"  --se <版本>        锚定 SE 版本,默认 2022" + Environment.NewLine +
			"  --project          写进仓库 recipes 目录(默认写 %LOCALAPPDATA%\\SolidEdgeSpy\\recipes)" + Environment.NewLine +
			"  --force            覆盖同名配方" + Environment.NewLine +
			"  --dry              只打印生成的 JSON,不落盘" + Environment.NewLine +
			"  --allow-once       豁免准入门槛(见下),会在配方 notes 里留下豁免痕迹" + Environment.NewLine +
			Environment.NewLine +
			"准入门槛(默认全过才入库):" + Environment.NewLine +
			"  1) 必须给 --title(用途说明)" + Environment.NewLine +
			"  2) 脚本名不能是探针类(probe*/dump*/test*/tmp*/temp*)" + Environment.NewLine +
			"  3) 该脚本成功运行过 ≥2 次(看沙箱 .runs 台账,编译失败不算)" + Environment.NewLine +
			"  4) 成功记录覆盖 ≥2 个不同日期(同一天连跑两遍不算)" + Environment.NewLine +
			"  5) 最近一次运行是成功的" + Environment.NewLine +
			"  2~5 可用 --allow-once 跳过(会在 notes 留痕);1 不可跳过。" + Environment.NewLine +
			Environment.NewLine +
			"示例:" + Environment.NewLine +
			"  solidedge-mcp --recipe-save assembly-random-colors --name asm_random_colors " + Environment.NewLine +
			"    --title \"装配体随机上色\" --input \"target:object::装配文档句柄\" --input \"seed:int:42:随机种子\" --dry";
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new
		{
			status = "error",
			message = message
		}, JsonOpts());
	}

	private static string DescribeException(Exception ex)
	{
		// P2-1:统一走 EvidenceContract(过去私有副本行为漂移,已收编)
		return EvidenceContract.DescribeException(ex);
	}
}
