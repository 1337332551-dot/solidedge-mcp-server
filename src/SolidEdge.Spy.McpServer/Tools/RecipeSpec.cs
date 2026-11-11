using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 配方的"语法单元级 IR":<c>&lt;配方名&gt;.json</c> 解析后的中间表示。
///
/// 解析器(RecipeSpecParser)与校验器(RecipeValidator)【共用这一份解析】——照 FeatureSpec.cs 的既定原则:
/// "若校验器另写一套解析,二者行为必然漂移,校验就退化成看起来对但实际不管用"。
///
/// 字段语义与单位:
///   坐标/长度一律【米】(UI 显示 mm,×1000);角度为弧度。
///   Steps 与 se_invoke_chain 的 steps 完全同构(member/args/on),执行器零翻译。
///   额外允许的占位符是 {{inputName}}(与 chain 的 $$/$N/obj-K/@arr: 并存,不冲突)。
/// </summary>
public sealed class RecipeSpec
{
	/// <summary>配方名(= 文件名去掉 .json)。</summary>
	public string Name;

	public string Title;

	public int Version = 1;

	/// <summary>draft(研发中,AI 可写) | verified(已固化,需用户拍板)。</summary>
	public string Status = "draft";

	/// <summary>
	/// step(默认:声明式 steps,走 se_invoke_chain) | script(壳式:整段脚本 + 参数,steps 为空)。
	///
	/// script 形态由 CLI `--recipe-save` 从已经跑通的 se_script_run 脚本生成,目的是把"一次性草稿"
	/// 转成"有名字、有参数、能被 --recipes 发现"的资产。执行时转交脚本通道,不做步骤级护栏,
	/// 所以 risk 只能靠声明值 —— 这也是它 status 默认 draft 的原因。
	/// </summary>
	public string Kind = "step";

	/// <summary>kind=script 时:沙箱里的脚本名(se_script_run 的 name)。</summary>
	public string ScriptName;

	/// <summary>kind=script 时:固化时的脚本文件路径(留作溯源,执行时按 ScriptName 去沙箱找)。</summary>
	public string ScriptPath;

	/// <summary>kind=script 时:固化时的源码 SHA256。脚本被改过就提示"配方可能已失配"。</summary>
	public string ScriptSha256;

	/// <summary>normal | modelChanging | destructive。**只是提示**,执行时以 Guardrail 重算为准。</summary>
	public string Risk;

	public string AnchorSe;

	public string AnchorSourceDoc;

	public List<InputSpec> Inputs = new List<InputSpec>();

	public List<PreconditionSpec> Preconditions = new List<PreconditionSpec>();

	/// <summary>与 se_invoke_chain 的 steps 同构。</summary>
	public List<RecipeStep> Steps = new List<RecipeStep>();

	public string VerifyKind;

	public string VerifyName;

	public string VerifyExpect;

	/// <summary>反向指针(md 权威,JSON 派生)。</summary>
	public string DerivedFrom;

	public string Notes;

	/// <summary>配方文件路径(同名冲突时用于标注来源)。</summary>
	public string SourcePath;

	/// <summary>原始 JSON(Clone 过,可安全跨作用域持有)。</summary>
	public JsonElement Raw;
}

/// <summary>配方入参声明。Type 只枚举 6 种,不做通用类型系统。</summary>
public sealed class InputSpec
{
	public string Name;

	/// <summary>object | objectArray | double | int | string | bool</summary>
	public string Type = "string";

	public bool Required;

	/// <summary>是否有 default 字段(区分"没写"与"写了空串")。</summary>
	public bool HasDefault;

	/// <summary>字面量;"selection" 是特殊值,表示取当前选中对象的首个句柄。</summary>
	public string Default;

	/// <summary>单位提示(如 "m"),仅用于报错文案,不做换算。</summary>
	public string Unit;

	public string Desc;
}

/// <summary>前置条件。Kind 只枚举 4 种,绝不做表达式求值器。</summary>
public sealed class PreconditionSpec
{
	/// <summary>docKind | handleExists | handleType | selectionNonEmpty</summary>
	public string Kind;

	public string Value;

	public string Hint;
}

/// <summary>一步成员调用(与 se_invoke_chain 的 step 同构)。</summary>
public sealed class RecipeStep
{
	public string Member;

	/// <summary>调用目标:$$ / $N / obj-K / {{input}};省略则用上一步返回值。</summary>
	public string On;

	public string[] Args;

	/// <summary>
	/// 可选。形如 "$N":第 N 步的 returnValue.value 为数值 0 时跳过本步(而不是硬调用)。
	/// 用于"集合为空就不要取 Item(1)"这类场景。被跳过的步仍占步序编号,引用了它的后续步骤自动级联跳过。
	/// </summary>
	public string SkipIfZero;

	public JsonElement Raw;
}

/// <summary>一条校验问题。Severity = error(拦截) | warn(提示,不拦截)。</summary>
public sealed class RecipeIssue
{
	public string Severity;

	public string Where;

	public string Message;

	public override string ToString()
	{
		return "[" + Severity + "] " + Where + ": " + Message;
	}
}

/// <summary>
/// 配方 JSON → RecipeSpec 的解析器。纯函数、不碰 COM,SE 没启动也能跑。
/// 结构致命错误抛 ArgumentException(由调用方包装成 error JSON);字段级问题留给 RecipeValidator 报。
/// </summary>
public static class RecipeSpecParser
{
	public static RecipeSpec Parse(JsonElement root, string sourcePath)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw new ArgumentException("配方根节点必须是 JSON 对象。");
		}
		RecipeSpec s = new RecipeSpec();
		s.SourcePath = sourcePath;
		s.Raw = root.Clone();
		s.Name = GetStr(root, "name");
		s.Title = GetStr(root, "title");
		s.Risk = GetStr(root, "risk");
		s.DerivedFrom = GetStr(root, "derivedFrom");
		s.Notes = GetStr(root, "notes");
		string status = GetStr(root, "status");
		if (!string.IsNullOrWhiteSpace(status))
		{
			s.Status = status.Trim();
		}
		string kind = GetStr(root, "kind");
		if (!string.IsNullOrWhiteSpace(kind))
		{
			s.Kind = kind.Trim().ToLowerInvariant();
		}
		JsonElement script;
		if (root.TryGetProperty("script", out script) && script.ValueKind == JsonValueKind.Object)
		{
			s.ScriptName = GetStr(script, "name");
			s.ScriptPath = GetStr(script, "path");
			s.ScriptSha256 = GetStr(script, "sha256");
		}
		int version;
		if (TryGetInt(root, "version", out version))
		{
			s.Version = version;
		}

		JsonElement anchor;
		if (root.TryGetProperty("anchoredTo", out anchor) && anchor.ValueKind == JsonValueKind.Object)
		{
			s.AnchorSe = GetStr(anchor, "se");
			s.AnchorSourceDoc = GetStr(anchor, "sourceDoc");
		}

		JsonElement inputs;
		if (root.TryGetProperty("inputs", out inputs) && inputs.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement el in inputs.EnumerateArray())
			{
				if (el.ValueKind != JsonValueKind.Object)
				{
					s.Inputs.Add(new InputSpec());
					continue;
				}
				InputSpec inp = new InputSpec();
				inp.Name = GetStr(el, "name");
				string type = GetStr(el, "type");
				if (!string.IsNullOrWhiteSpace(type))
				{
					inp.Type = type.Trim();
				}
				inp.Unit = GetStr(el, "unit");
				inp.Desc = GetStr(el, "desc");
				bool req;
				if (TryGetBool(el, "required", out req))
				{
					inp.Required = req;
				}
				JsonElement dflt;
				if (el.TryGetProperty("default", out dflt))
				{
					inp.HasDefault = true;
					inp.Default = ((dflt.ValueKind == JsonValueKind.String) ? dflt.GetString() : dflt.ToString());
				}
				s.Inputs.Add(inp);
			}
		}

		JsonElement pres;
		if (root.TryGetProperty("preconditions", out pres) && pres.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement el2 in pres.EnumerateArray())
			{
				PreconditionSpec p = new PreconditionSpec();
				if (el2.ValueKind == JsonValueKind.Object)
				{
					p.Kind = GetStr(el2, "kind");
					p.Value = GetStr(el2, "value");
					p.Hint = GetStr(el2, "hint");
				}
				s.Preconditions.Add(p);
			}
		}

		JsonElement steps;
		if (root.TryGetProperty("steps", out steps) && steps.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement el3 in steps.EnumerateArray())
			{
				RecipeStep st = new RecipeStep();
				if (el3.ValueKind == JsonValueKind.Object)
				{
					st.Raw = el3.Clone();
					st.Member = GetStr(el3, "member");
					st.On = GetStr(el3, "on");
					st.SkipIfZero = GetStr(el3, "skipIfZero");
					JsonElement a;
					if (el3.TryGetProperty("args", out a) && a.ValueKind == JsonValueKind.Array)
					{
						List<string> list = new List<string>();
						foreach (JsonElement item in a.EnumerateArray())
						{
							list.Add((item.ValueKind == JsonValueKind.String) ? item.GetString() : item.ToString());
						}
						st.Args = list.ToArray();
					}
				}
				s.Steps.Add(st);
			}
		}

		JsonElement ver;
		if (root.TryGetProperty("verification", out ver) && ver.ValueKind == JsonValueKind.Object)
		{
			s.VerifyKind = GetStr(ver, "kind");
			s.VerifyName = GetStr(ver, "name");
			s.VerifyExpect = GetStr(ver, "expect");
		}

		return s;
	}

	private static string GetStr(JsonElement e, string key)
	{
		JsonElement v;
		if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out v) && v.ValueKind == JsonValueKind.String)
		{
			return v.GetString();
		}
		return null;
	}

	private static bool TryGetInt(JsonElement e, string key, out int val)
	{
		val = 0;
		if (e.ValueKind != JsonValueKind.Object)
		{
			return false;
		}
		JsonElement v;
		if (!e.TryGetProperty(key, out v))
		{
			return false;
		}
		if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out val))
		{
			return true;
		}
		return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out val);
	}

	private static bool TryGetBool(JsonElement e, string key, out bool val)
	{
		val = false;
		if (e.ValueKind != JsonValueKind.Object)
		{
			return false;
		}
		JsonElement v;
		if (!e.TryGetProperty(key, out v))
		{
			return false;
		}
		if (v.ValueKind == JsonValueKind.True)
		{
			val = true;
			return true;
		}
		if (v.ValueKind == JsonValueKind.False)
		{
			val = false;
			return true;
		}
		return v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out val);
	}
}

/// <summary>
/// 配方静态校验器(不碰 COM,不需要 SE 在运行)。
///
/// v1 只做"单元级"校验:结构合法性、占位符引用完整性、$N 越界、步数阈值、状态与验收判据的一致性。
/// **不做**成员存在性校验 —— 那需要模拟整条类型链(第 N 步返回类型 → 决定第 N+1 步 on 的类型),
/// typelib 有信息可做,但工作量大,留 v1.1。
/// </summary>
public static class RecipeValidator
{
	/// <summary>
	/// 步数提示阈值。
	/// 实测依据:文档里"拉伸体完整 chain"共 **19 步**真机跑通,所以不能按传闻的"8~9 步最稳"设阈值
	/// (那会把正常的建模配方全部误报,让警告失去意义)。取 20 —— 略高于已知可跑的 19,
	/// 只对异常拉长的配方提示拆分。
	/// </summary>
	public const int MaxRecommendedSteps = 20;

	private static readonly string[] InputTypes = { "object", "objectArray", "double", "int", "string", "bool" };

	private static readonly string[] PreconditionKinds = { "docKind", "handleExists", "handleType", "selectionNonEmpty" };

	internal static readonly Regex PlaceholderPattern = new Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

	// $$ 是起始对象,不是 $N;这里只抓 "$数字"。
	private static readonly Regex StepRefPattern = new Regex(@"\$(\d+)", RegexOptions.Compiled);

	/// <summary>skipIfZero 的合法写法:恰好一个 "$N" 引用(允许前后空白)。</summary>
	private static readonly Regex SkipIfZeroPattern = new Regex(@"^\s*\$\d+\s*$", RegexOptions.Compiled);

	public static List<RecipeIssue> Validate(RecipeSpec r)
	{
		List<RecipeIssue> issues = new List<RecipeIssue>();
		if (r == null)
		{
			Add(issues, "error", "(root)", "配方为空。");
			return issues;
		}

		if (string.IsNullOrWhiteSpace(r.Name))
		{
			Add(issues, "error", "name", "缺少 name(建议与文件名一致)。");
		}
		else if (!IsSafeName(r.Name))
		{
			Add(issues, "error", "name", "name 非法:只允许字母/数字/下划线/短横线/点,长度 1~64。收到: \"" + r.Name + "\"");
		}

		if (r.Status != "draft" && r.Status != "verified")
		{
			Add(issues, "warn", "status", "status 建议只取 draft 或 verified,收到: \"" + r.Status + "\"。");
		}

		HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
		HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
		foreach (InputSpec inp in r.Inputs)
		{
			if (string.IsNullOrWhiteSpace(inp.Name))
			{
				Add(issues, "error", "inputs", "存在没有 name 的 input。");
				continue;
			}
			if (!declared.Add(inp.Name))
			{
				Add(issues, "error", "inputs." + inp.Name, "input 名重复。");
			}
			if (!Contains(InputTypes, inp.Type))
			{
				Add(issues, "error", "inputs." + inp.Name, "type 非法: \"" + inp.Type + "\"(可用: " + string.Join(" / ", InputTypes) + ")。");
			}
			if (inp.Required && inp.HasDefault)
			{
				Add(issues, "warn", "inputs." + inp.Name, "同时声明了 required 与 default:调用方不传时会用 default,required 形同虚设。");
			}
		}

		foreach (PreconditionSpec p in r.Preconditions)
		{
			if (!Contains(PreconditionKinds, p.Kind))
			{
				Add(issues, "error", "preconditions", "kind 非法: \"" + (p.Kind ?? "(空)") + "\"(可用: " + string.Join(" / ", PreconditionKinds) + ")。");
				continue;
			}
			// selectionNonEmpty 与 handleExists 是"无参检查",不需要 value
			bool needsValue = !p.Kind.Equals("selectionNonEmpty", StringComparison.OrdinalIgnoreCase)
				&& !p.Kind.Equals("handleExists", StringComparison.OrdinalIgnoreCase);
			if (needsValue && string.IsNullOrWhiteSpace(p.Value))
			{
				Add(issues, "error", "preconditions." + p.Kind, "该 kind 需要 value。");
			}
		}

		if (r.Steps.Count == 0)
		{
			if (IsScriptKind(r))
			{
				// 壳式配方本来就没有声明式步骤:整段执行交给脚本通道,这里只要求脚本名存在。
				if (string.IsNullOrWhiteSpace(r.ScriptName))
				{
					Add(issues, "error", "script.name", "kind=script 的配方必须声明 script.name(沙箱里的脚本名)。");
				}
				else if (!IsSafeName(r.ScriptName))
				{
					Add(issues, "error", "script.name", "script.name 非法:只允许字母/数字/下划线/短横线,长度 1~64。收到: \"" + r.ScriptName + "\"");
				}
			}
			else
			{
				Add(issues, "error", "steps", "配方没有任何步骤。");
			}
		}
		else if (IsScriptKind(r))
		{
			Add(issues, "warn", "steps", "kind=script 的配方不应声明 steps(声明了也不会执行,执行走脚本通道)。");
		}
		if (r.Steps.Count > MaxRecommendedSteps)
		{
			Add(issues, "warn", "steps", "共 " + r.Steps.Count + " 步,超过提示阈值 " + MaxRecommendedSteps + " 步。实测 19 步的建模链可跑通,但步数越多单次调用越慢、超时风险越高,建议拆成多条配方。");
		}

		for (int i = 0; i < r.Steps.Count; i++)
		{
			RecipeStep st = r.Steps[i];
			string where = "steps[" + i + "]";
			if (string.IsNullOrWhiteSpace(st.Member))
			{
				Add(issues, "error", where, "缺少 member。");
			}
			CheckToken(issues, where + ".on", st.On, i + 1, declared, used);
			CheckToken(issues, where + ".skipIfZero", st.SkipIfZero, i + 1, declared, used);
			if (!string.IsNullOrWhiteSpace(st.SkipIfZero) && !SkipIfZeroPattern.IsMatch(st.SkipIfZero))
			{
				Add(issues, "error", where + ".skipIfZero", "格式应为 \"$N\"(引用一个前序步骤的返回值),当前是 \"" + st.SkipIfZero + "\"。");
			}
			if (st.Args != null)
			{
				for (int j = 0; j < st.Args.Length; j++)
				{
					CheckToken(issues, where + ".args[" + j + "]", st.Args[j], i + 1, declared, used);
				}
			}
		}

		if (r.Status == "verified" && string.IsNullOrWhiteSpace(r.VerifyName))
		{
			Add(issues, "error", "verification", "status=verified 必须提供 verification(建议 kind=snapshotDiff + name + expect=identical=true),否则\"已固化\"没有客观判据。");
		}

		// 壳式配方的 input 是给脚本 Main(args) 用的,不会出现在 steps 里,所以不能按"未引用"报 warn。
		if (!IsScriptKind(r))
		{
			foreach (InputSpec inp2 in r.Inputs)
			{
				if (!string.IsNullOrWhiteSpace(inp2.Name) && !used.Contains(inp2.Name))
				{
					Add(issues, "warn", "inputs." + inp2.Name, "声明了但配方正文没有引用。");
				}
			}
		}

		return issues;
	}

	/// <summary>是不是壳式(整段脚本)配方。</summary>
	internal static bool IsScriptKind(RecipeSpec r)
	{
		return r != null && string.Equals(r.Kind, "script", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>校验一个 token 里的 {{input}} 引用与 $N 引用。stepNo 是本步的 1-based 序号。</summary>
	private static void CheckToken(List<RecipeIssue> issues, string where, string token, int stepNo, HashSet<string> declared, HashSet<string> used)
	{
		if (string.IsNullOrEmpty(token))
		{
			return;
		}
		foreach (Match m in PlaceholderPattern.Matches(token))
		{
			string nm = m.Groups[1].Value;
			if (!declared.Contains(nm))
			{
				Add(issues, "error", where, "引用了未声明的 input: {{" + nm + "}}(需要在 inputs 里声明)。");
			}
			else
			{
				used.Add(nm);
			}
		}
		foreach (Match m2 in StepRefPattern.Matches(token))
		{
			int n;
			if (!int.TryParse(m2.Groups[1].Value, out n))
			{
				continue;
			}
			if (n >= stepNo)
			{
				Add(issues, "error", where, "$" + n + " 越界:$N 只能引用前序步骤的返回值,而本步是第 " + stepNo + " 步。");
			}
		}
	}

	private static void Add(List<RecipeIssue> list, string severity, string where, string message)
	{
		list.Add(new RecipeIssue { Severity = severity, Where = where, Message = message });
	}

	private static bool Contains(string[] arr, string value)
	{
		if (value == null)
		{
			return false;
		}
		for (int i = 0; i < arr.Length; i++)
		{
			if (string.Equals(arr[i], value, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	internal static bool IsSafeName(string s)
	{
		if (string.IsNullOrEmpty(s) || s.Length > 64)
		{
			return false;
		}
		foreach (char c in s)
		{
			if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.')
			{
				return false;
			}
		}
		return true;
	}
}
