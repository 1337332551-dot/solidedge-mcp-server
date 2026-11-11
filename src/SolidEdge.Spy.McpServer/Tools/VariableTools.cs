using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 变量表(Variables)读写。
///
/// 2026-10-25 真机事实(决定了本文件的实现方式):
///   ① <c>Variables.Item(i)</c> 只枚举"表内变量"(如 Hole_1_Diameter 这类系统变量),
///      <b>PutName 出来的命名标注变量不在枚举里</b>——本轮画法兰时 7 个 Flange_* / Bolt_* 变量
///      一个都没出现在 list 结果里,只能靠 Item("名") 逐个试。要"看得到",必须反查草图轮廓的
///      Dimensions 集合、用 GetName(dim) 换名字,这条路径实测可用。
///   ② <c>GetFormula/GetSystemName/GetDisplayName</c> 的参数是<b>对象</b>,
///      而 <c>GetFormula</c> 的参数是<b>字符串变量名</b>(源码旧注释 2026-10-16 已记录,别一起改)。
///   ③ <b>Edit 的公式必须带单位</b>。写成 "0.1" 会被 SE 按 0.1mm 解释:孔位塌到圆心、
///      孔特征整片失效,而 Edit 本身"不报错"。所以写值前必须校验单位,写完必须回读。
///   ④ <c>doc.Recompute()</c> 后旧 Model/Body RCW 失效(读体积报 E_FAIL),
///      所以改完要用 <see cref="ModelMetrics"/> 重新取模型再采数。
/// </summary>
[McpServerToolType]
public static class VariableTools
{
	private const int NamedScanMaxErrors = 20;

	[McpServerTool]
	[Description("变量表(Variables)读写。action=list(默认):列出全部变量——含枚举表内的系统变量," +
		"并补全枚举表里看不到的【命名标注变量】(PutName 出来的不在 Item(i) 枚举里,必须反查草图尺寸)。" +
		"action=get 按名查(name),不存在给模糊候选;action=set 按名改值/公式(name+formula),写后自动 Recompute 并回传体积/包围盒变化," +
		"verify=true 时回读校验;action=add 新增用户变量(name+formula+unitType)。\n" +
		"★ formula 必须带单位(\"240 mm\"/\"0.24 m\"/\"45 deg\")或引用已有变量名(\"Flange_T / 2\");裸数字会被拒绝——" +
		"SE 对无单位数值按【毫米】解释,\"0.1\" 会让几何塌掉。★ 改完看 delta.volumeMm3 / delta.sizeMm,不要只看 status=ok。" +
		"只读模式下 list/get 可用,set/add 拒绝。")]
	public static string se_get_variables(
		SolidEdgeContext context,
		[Description("动作:list(默认,列出全部变量)/ get(按名查)/ set(按名改值)/ add(新增用户变量)")] string action = "list",
		[Description("变量名(用户起的名字或系统名 V123)。get/set 必填。")] string name = null,
		[Description("新值或公式。set/add 必填。必须带单位(\"240 mm\"/\"0.24 m\"/\"45 deg\")或引用已有变量名(\"Flange_T / 2\");裸数字会被拒绝。")] string formula = null,
		[Description("list 时是否补全枚举表里看不到的命名标注变量(默认 true)。关掉可只枚举表内变量。")] bool includeNamed = true,
		[Description("add 的单位类型(SE Variables.Add 第三参,默认 1=长度)。角度变量可试 4。")] int unitType = 1,
		[Description("set 后是否 Recompute(默认 true)。false 只写不重算,模型不会跟随,一般不建议。")] bool recompute = true,
		[Description("set 后是否回读校验(默认 true)。写入无报错但没生效时返回 silentFailure=true。")] bool verify = true)
	{
		string act = (action ?? "list").Trim().ToLowerInvariant();
		if (act != "list" && act != "get" && act != "set" && act != "add")
		{
			return Json(new
			{
				status = "error",
				code = "E605",
				message = "action 只能是 list / get / set / add,收到 \"" + action + "\"。",
				fix = new { action = "set", field = "action", allowed = new[] { "list", "get", "set", "add" } }
			});
		}

		bool isWrite = act == "set" || act == "add";
		if (isWrite && ToolRisk.Mode == McpMode.ReadOnly)
		{
			return Json(new
			{
				status = "error",
				code = "E604",
				message = "已拒绝写操作:当前处于只读模式(SE_MCP_MODE=readonly),se_get_variables 只放行 list/get。" +
						  "要改变量请把 MCP 配置里的 SE_MCP_MODE 改为 full(或删除该变量)后重启会话。",
				fix = new { action = "switch_mode", env = "SE_MCP_MODE", value = "full", then = "重启 MCP server" }
			});
		}

		try
		{
			return context.Invoke(delegate
			{
				object doc = ActiveDocument(context);
				if (doc == null)
				{
					return Error("E600", "当前没有活动文档。", null);
				}
				object vars = ModelMetrics.TryGet(doc, "Variables");
				if (vars == null)
				{
					return Error("E600", "当前文档不支持变量表(Variables),或无法访问。可能不是 Part 文档。", null);
				}

				if (act == "list")
				{
					return DoList(doc, vars, includeNamed);
				}
				if (act == "get")
				{
					return DoGet(doc, vars, name);
				}
				if (act == "set")
				{
					return DoSet(doc, vars, name, formula, recompute, verify);
				}
				return DoAdd(doc, vars, name, formula, unitType);
			});
		}
		catch (Exception ex)
		{
			return Error("E600", "变量表操作失败: " + ModelMetrics.DescribeException(ex), null);
		}
	}

	// ==================== action: list ====================

	private static string DoList(object doc, object vars, bool includeNamed)
	{
		List<object> items = new List<object>();
		List<object> readErrors = new List<object>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		int count = -1;
		try { count = Convert.ToInt32(ModelMetrics.Get(vars, "Count")); }
		catch { /* Count 读不到 → count 保持 -1,结论里如实说明 */ }

		for (int i = 1; i <= count; i++)
		{
			try
			{
				object v = ModelMetrics.InvokeAny(vars, "Item", i);
				if (v == null) throw new InvalidOperationException("Item(" + i + ") 返回 null");

				string displayName = SafeStr(ModelMetrics.InvokeAny(vars, "GetDisplayName", v));
				string varName = SafeStr(ModelMetrics.InvokeAny(vars, "GetName", v));
				string systemName = SafeStr(ModelMetrics.InvokeAny(vars, "GetSystemName", v));
				string formulaText = "";
				if (!string.IsNullOrEmpty(varName))
				{
					// GetFormula 的参数是【字符串变量名】,不是 Variable 对象(2026-10-16 实测)
					formulaText = SafeStr(ModelMetrics.TryGetFormula(vars, varName));
					seen.Add(varName);
				}
				items.Add(new
				{
					index = i,
					source = "table",
					displayName = displayName,
					name = varName,
					systemName = systemName,
					formula = formulaText,
					valueMeter = ReadValue(vars, varName),
					readOnly = ReadReadOnly(vars, varName)
				});
			}
			catch (Exception ex)
			{
				readErrors.Add(new { index = i, error = ModelMetrics.DescribeException(ex) });
			}
		}

		// 补全"命名标注变量":它们不在 Item(i) 枚举里,只能反查草图 Profiles→Dimensions 再用 GetName(dim) 换名
		int namedAdded = 0;
		object namedScan = null;
		if (includeNamed)
		{
			int profileSets = 0, profiles = 0, dimsScanned = 0;
			namedAdded = CollectNamedDimensionVariables(doc, vars, seen, items, readErrors,
				out profileSets, out profiles, out dimsScanned);
			namedScan = new
			{
				profileSets,
				profiles,
				dimensionsScanned = dimsScanned,
				namedFound = namedAdded,
				note = "命名标注变量不在 Variables.Item(i) 枚举里,这里是把草图 Dimensions 逐个 GetName 换出来的。"
			};
		}

		string conclusion;
		if (count < 0)
		{
			conclusion = "变量个数(Count)读不到,无法确认变量表状态。";
		}
		else if (items.Count == 0)
		{
			conclusion = "变量表为空(枚举 " + count + " 个" + (includeNamed ? ",命名标注变量也没扫到" : "") + ")。";
		}
		else if (namedAdded > 0)
		{
			conclusion = "共 " + items.Count + " 个可驱动变量:枚举表内 " + Math.Max(0, items.Count - namedAdded)
						 + " 个 + 命名标注变量 " + namedAdded + " 个(后者靠反查草图尺寸补全)。改值用 action=set。";
		}
		else
		{
			conclusion = "变量表含 " + items.Count + " 个变量。若有控制几何尺寸的变量,可用 action=set 驱动。";
		}
		if (readErrors.Count > 0)
		{
			conclusion += "(其中 " + readErrors.Count + " 项读取失败,见 readErrors,不要当成空公式)";
		}

		return Json(new
		{
			status = "ok",
			action = "list",
			variableCount = count >= 0 ? (int?)count : null,
			totalListed = items.Count,
			namedDimensionCount = namedAdded,
			variables = items,
			namedScan,
			readErrors = readErrors.Count > 0 ? readErrors : null,
			conclusion
		});
	}

	/// <summary>
	/// 反查草图尺寸,把 PutName 出来的命名标注变量补进列表。
	/// 单个 Profile/ProfileSet 读不到只记 readErrors,不影响整表(被特征消费后的草图可能拒绝访问)。
	/// </summary>
	private static int CollectNamedDimensionVariables(object doc, object vars, HashSet<string> seen,
		List<object> items, List<object> readErrors, out int profileSetCount, out int profileCount, out int dimCount)
	{
		profileSetCount = 0;
		profileCount = 0;
		dimCount = 0;
		int added = 0;

		object psets = ModelMetrics.TryGet(doc, "ProfileSets");
		if (psets == null) return 0;

		int psTotal = 0;
		try { psTotal = Convert.ToInt32(ModelMetrics.Get(psets, "Count")); }
		catch { return 0; }
		profileSetCount = psTotal;

		for (int i = 1; i <= psTotal; i++)
		{
			try
			{
				object ps = ModelMetrics.InvokeAny(psets, "Item", i);
				object profs = ModelMetrics.TryGet(ps, "Profiles");
				if (profs == null) continue;
				int pn = 0;
				try { pn = Convert.ToInt32(ModelMetrics.Get(profs, "Count")); }
				catch { continue; }
				profileCount += pn;

				for (int j = 1; j <= pn; j++)
				{
					try
					{
						object prof = ModelMetrics.InvokeAny(profs, "Item", j);
						object dims = ModelMetrics.TryGet(prof, "Dimensions");
						if (dims == null) continue;
						int dn = 0;
						try { dn = Convert.ToInt32(ModelMetrics.Get(dims, "Count")); }
						catch { continue; }
						dimCount += dn;

						for (int k = 1; k <= dn; k++)
						{
							try
							{
								object dim = ModelMetrics.InvokeAny(dims, "Item", k);
								if (dim == null) continue;
								string varName = SafeStr(ModelMetrics.InvokeAny(vars, "GetName", dim));
								if (string.IsNullOrEmpty(varName) || !seen.Add(varName)) continue;

								string systemName = SafeStr(ModelMetrics.InvokeAny(vars, "GetSystemName", dim));
								string displayName = SafeStr(ModelMetrics.InvokeAny(vars, "GetDisplayName", dim));
								string formulaText = SafeStr(ModelMetrics.TryGetFormula(vars, varName));
								items.Add(new
								{
									index = (int?)null,
									source = "dimension",
									profileSet = i,
									profile = j,
									displayName,
									name = varName,
									systemName,
									formula = formulaText,
									valueMeter = ReadValue(vars, varName),
									readOnly = ReadReadOnly(vars, varName)
								});
								added++;
							}
							catch (Exception ex)
							{
								if (readErrors.Count < NamedScanMaxErrors)
									readErrors.Add(new { where = "dimension", profileSet = i, profile = j, dim = k, error = ModelMetrics.DescribeException(ex) });
							}
						}
					}
					catch (Exception ex)
					{
						if (readErrors.Count < NamedScanMaxErrors)
							readErrors.Add(new { where = "profile", profileSet = i, profile = j, error = ModelMetrics.DescribeException(ex) });
					}
				}
			}
			catch (Exception ex)
			{
				if (readErrors.Count < NamedScanMaxErrors)
					readErrors.Add(new { where = "profileSet", index = i, error = ModelMetrics.DescribeException(ex) });
			}
		}
		return added;
	}

	// ==================== action: get ====================

	private static string DoGet(object doc, object vars, string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return Error("E602", "get 需要 name(变量名)。可先用 action=list 看有哪些变量。",
				new { action = "call", tool = "se_get_variables", args = new { action = "list" } });
		}
		string wanted = name.Trim();
		object v = null;
		try { v = ModelMetrics.InvokeAny(vars, "Item", wanted); }
		catch (Exception ex)
		{
			return NotFound(vars, wanted, "读取变量 \"" + wanted + "\" 失败: " + ModelMetrics.DescribeException(ex));
		}
		if (v == null)
		{
			return NotFound(vars, wanted, "变量 \"" + wanted + "\" 不存在(Item 返回 null)。");
		}

		string displayName = SafeStr(ModelMetrics.InvokeAny(vars, "GetDisplayName", v));
		string realName = SafeStr(ModelMetrics.InvokeAny(vars, "GetName", v));
		string systemName = SafeStr(ModelMetrics.InvokeAny(vars, "GetSystemName", v));
		string formulaText = SafeStr(ModelMetrics.TryGetFormula(vars, wanted));
		double? value = ReadValue(vars, wanted);
		bool? readOnly = ReadReadOnly(vars, wanted);

		return Json(new
		{
			status = "ok",
			action = "get",
			name = wanted,
			displayName,
			realName,
			systemName,
			formula = formulaText,
			valueMeter = value,
			readOnly,
			note = value.HasValue
				? "valueMeter 是 SE 内部单位(米);UI 显示毫米请 ×1000。改值请用 action=set 且公式带单位。"
				: "值读不到(可能是只读系统变量或接口不可用),不代表变量不存在。"
		});
	}

	private static string NotFound(object vars, string wanted, string reason)
	{
		return Json(new
		{
			status = "error",
			code = "E602",
			message = reason,
			candidates = KnownNames(vars, wanted),
			fix = new { action = "call", tool = "se_get_variables", args = new { action = "list" } }
		});
	}

	/// <summary>按包含关系给出候选变量名(拼错/记不住名字时省一次试错)。</summary>
	private static List<string> KnownNames(object vars, string wanted)
	{
		List<string> names = new List<string>();
		int count = 0;
		try { count = Convert.ToInt32(ModelMetrics.Get(vars, "Count")); } catch { return names; }
		for (int i = 1; i <= count; i++)
		{
			try
			{
				object v = ModelMetrics.InvokeAny(vars, "Item", i);
				string nm = SafeStr(ModelMetrics.InvokeAny(vars, "GetName", v));
				if (string.IsNullOrEmpty(nm)) continue;
				string w = (wanted ?? "").Trim();
				if (w.Length == 0 || nm.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0
					|| w.IndexOf(nm, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					names.Add(nm);
				}
			}
			catch { }
			if (names.Count >= 10) break;
		}
		return names;
	}

	// ==================== action: set ====================

	private static string DoSet(object doc, object vars, string name, string formula, bool recompute, bool verify)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return Error("E602", "set 需要 name(变量名)。", null);
		}
		string unitError = ValidateFormulaUnits(formula);
		if (unitError != null)
		{
			return Error("E601", unitError, new
			{
				action = "fix",
				field = "formula",
				examples = new[] { "240 mm", "0.24 m", "45 deg", "Flange_T / 2" }
			});
		}

		string wanted = name.Trim();
		string expr = formula.Trim();

		object varObj = null;
		try { varObj = ModelMetrics.InvokeAny(vars, "Item", wanted); }
		catch { varObj = null; }
		if (varObj == null)
		{
			return NotFound(vars, wanted, "变量 \"" + wanted + "\" 不存在,无法改值。");
		}

		ModelMetrics.Snapshot before = ModelMetrics.Capture(doc);

		try
		{
			ModelMetrics.InvokeAny(vars, "Edit", wanted, expr);
		}
		catch (Exception ex)
		{
			return Error("E606", "写入变量 \"" + wanted + "\" = \"" + expr + "\" 失败: " + ModelMetrics.DescribeException(ex),
				new { action = "fix", field = "formula", examples = new[] { "240 mm", "0.24 m", "45 deg" } });
		}

		string recomputeNote = null;
		if (recompute)
		{
			try { ModelMetrics.Call(doc, "Recompute"); }
			catch (Exception ex) { recomputeNote = "Recompute 失败: " + ModelMetrics.DescribeException(ex); }
		}

		// Recompute 之后旧 Model/Body RCW 必失效 → 由 ModelMetrics 重新取
		ModelMetrics.Snapshot after = ModelMetrics.Capture(doc);

		double? readBackValue = ReadValue(vars, wanted);
		string readBackFormula = SafeStr(ModelMetrics.TryGetFormula(vars, wanted));

		double? expectMeter, expectRad;
		ParseUnitValue(expr, out expectMeter, out expectRad);

		bool? verified = null;
		string verifyNote = null;
		if (verify && readBackValue.HasValue)
		{
			if (expectMeter.HasValue)
			{
				verified = Math.Abs(readBackValue.Value - expectMeter.Value) <= 1e-9;
				if (!verified.Value)
					verifyNote = "回读 " + (readBackValue.Value * 1000.0).ToString("0.####", CultureInfo.InvariantCulture)
								 + " mm 与期望 " + (expectMeter.Value * 1000.0).ToString("0.####", CultureInfo.InvariantCulture) + " mm 不符";
			}
			else if (expectRad.HasValue)
			{
				verified = Math.Abs(readBackValue.Value - expectRad.Value) <= 1e-9;
				if (!verified.Value)
					verifyNote = "回读 " + (readBackValue.Value * 180.0 / Math.PI).ToString("0.####", CultureInfo.InvariantCulture)
								 + "° 与期望 " + (expectRad.Value * 180.0 / Math.PI).ToString("0.####", CultureInfo.InvariantCulture) + "° 不符";
			}
			else
			{
				verifyNote = "公式是变量引用/表达式,无法静态换算期望值,只回报回读结果(verified=null 不代表失败)。";
			}
		}
		else if (verify && !readBackValue.HasValue)
		{
			verifyNote = "回读不到数值(可能该变量只写或接口不可读),请用 delta 判断模型是否跟随。";
		}

		bool silentFailure = verified.HasValue && !verified.Value;
		bool geometryMoved = false;
		if (before != null && after != null && before.VolumeMm3.HasValue && after.VolumeMm3.HasValue)
		{
			geometryMoved = Math.Abs(after.VolumeMm3.Value - before.VolumeMm3.Value) > 1e-6;
		}

		string conclusion;
		if (silentFailure)
		{
			conclusion = "写入无报错但回读不符(silentFailure):SE 可能把公式按别的单位/规则规范化了,或该变量在封闭轮廓上只读。"
						 + "请确认单位,或改用带单位的等价写法。";
		}
		else if (!recompute)
		{
			conclusion = "已写入但未重算(recompute=false),模型尚未跟随;需要时再调一次 action=set 并 recompute=true。";
		}
		else if (!geometryMoved)
		{
			conclusion = "变量值已改为 \"" + expr + "\",但体积/包围盒没变化——可能该变量不驱动几何(如纯注释变量或系统变量),或改的数值恰好等价。";
		}
		else
		{
			conclusion = "变量已生效且模型跟随(delta.volumeMm3 / delta.sizeMm 可见几何变化)。";
		}

		return Json(new
		{
			status = "ok",
			action = "set",
			name = wanted,
			formula = expr,
			recompute,
			before = ModelMetrics.ToPayload(before),
			after = ModelMetrics.ToPayload(after),
			delta = ModelMetrics.BuildDelta(before, after),
			readBack = new { valueMeter = readBackValue, formula = readBackFormula },
			expected = new
			{
				meter = expectMeter,
				degree = expectRad.HasValue ? (double?)Math.Round(expectRad.Value * 180.0 / Math.PI, 6) : null
			},
			verified = verified,
			silentFailure,
			geometryMoved,
			verifyNote,
			recomputeNote,
			conclusion
		});
	}

	// ==================== action: add ====================

	private static string DoAdd(object doc, object vars, string name, string formula, int unitType)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return Error("E603", "add 需要 name(新变量名)。", null);
		}
		string unitError = ValidateFormulaUnits(formula);
		if (unitError != null)
		{
			return Error("E601", unitError, new
			{
				action = "fix",
				field = "formula",
				examples = new[] { "240 mm", "0.24 m", "45 deg", "Flange_T / 2" }
			});
		}

		string wanted = name.Trim();
		string expr = formula.Trim();

		object existing = null;
		try { existing = ModelMetrics.InvokeAny(vars, "Item", wanted); }
		catch { existing = null; }
		if (existing != null)
		{
			return Error("E603", "变量 \"" + wanted + "\" 已存在,add 会重名失败。要改值请用 action=set。",
				new { action = "call", tool = "se_get_variables", args = new { action = "set", name = wanted, formula = expr } });
		}

		// Variables.Add 的第三参在 PIA 里是枚举,晚绑定传 int 有可能类型不符 → 三级尝试,记录哪条路成功
		string channel = null;
		Exception last = null;
		object[][] attempts = new object[][]
		{
			new object[] { wanted, expr, unitType },
			new object[] { wanted, expr },
			new object[] { wanted, expr, Type.Missing }
		};
		string[] attemptNames = new string[] { "Add(name, formula, unitType)", "Add(name, formula)", "Add(name, formula, Missing)" };

		for (int i = 0; i < attempts.Length; i++)
		{
			try
			{
				ModelMetrics.InvokeAny(vars, "Add", attempts[i]);
				channel = attemptNames[i];
				last = null;
				break;
			}
			catch (Exception ex)
			{
				last = ex;
			}
		}
		if (channel == null)
		{
			return Error("E606", "新增变量失败(三种调用签名都试过): " + ModelMetrics.DescribeException(last), null);
		}

		try { ModelMetrics.Call(doc, "Recompute"); } catch { }

		return Json(new
		{
			status = "ok",
			action = "add",
			name = wanted,
			formula = expr,
			unitType,
			channel,
			readBack = new
			{
				valueMeter = ReadValue(vars, wanted),
				formula = SafeStr(ModelMetrics.TryGetFormula(vars, wanted))
			},
			conclusion = "已新增用户变量 \"" + wanted + "\"(通道:" + channel + ")。用它做驱动的前提是:草图标注的公式引用它,而不是反过来。"
		});
	}

	// ==================== 公式单位校验/解析 ====================

	private static readonly Regex PureNumberRx = new Regex(
		@"^[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?$", RegexOptions.Compiled);

	private static readonly Regex UnitValueRx = new Regex(
		@"^\s*([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*([A-Za-z\u00B0\u4E00-\u9FA5/]*)\s*$",
		RegexOptions.Compiled);

	/// <summary>
	/// 公式单位校验:拦"裸数字"和"纯数字算式"。
	/// 依据 2026-10-25 实测事故:Edit("Bolt_R","0.1") 被按 0.1mm 解释,孔位塌到圆心、孔特征失效,
	/// 而 Edit 本身不报错——所以必须在入口拦,而不是靠事后回读。
	/// </summary>
	private static string ValidateFormulaUnits(string formula)
	{
		string f = (formula ?? "").Trim();
		if (f.Length == 0)
		{
			return "公式为空。set/add 都需要给出新值,例如 \"240 mm\"。";
		}
		if (PureNumberRx.IsMatch(f))
		{
			return "公式 \"" + f + "\" 是裸数值、没有单位。Solid Edge 对无单位数值按【毫米】解释"
				 + "(实测 \"0.1\" = 0.1mm,会把几何改塌)。请写明单位:米写 \"0.1 m\",毫米写 \"100 mm\",角度写 \"45 deg\"。";
		}
		if (!Regex.IsMatch(f, "[A-Za-z\u00B0\u4E00-\u9FA5]"))
		{
			return "公式 \"" + f + "\" 里既没有单位也没有变量名,等同于裸数字运算,Solid Edge 会按毫米解释(会把几何改塌)。"
				 + "请给数值加单位,或直接引用变量名(如 \"Flange_T / 2\")。";
		}
		return null;
	}

	/// <summary>解析"数值+单位"形式的公式;解析不出就两个 out 都是 null(不猜)。</summary>
	private static void ParseUnitValue(string formula, out double? meters, out double? radians)
	{
		meters = null;
		radians = null;
		Match m = UnitValueRx.Match((formula ?? "").Trim());
		if (!m.Success) return;

		double num;
		if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out num)) return;

		string u = m.Groups[2].Value.Trim().ToLowerInvariant();
		switch (u)
		{
			case "mm":
			case "\u6BEB\u7C73":
			case "\u516C\u5398":
				meters = num / 1000.0;
				break;
			case "cm":
			case "\u5398\u7C73":
				meters = num / 100.0;
				break;
			case "m":
			case "\u7C73":
				meters = num;
				break;
			case "in":
			case "\u82F1\u5BF8":
				meters = num * 0.0254;
				break;
			case "deg":
			case "\u5EA6":
			case "\u00B0":
				radians = num * Math.PI / 180.0;
				break;
			case "rad":
			case "\u5F27\u5EA6":
				radians = num;
				break;
		}
	}

	// ==================== 读值小工具 ====================

	private static object ActiveDocument(SolidEdgeContext context)
	{
		try
		{
			object doc = context.GetApplication().ActiveDocument;
			return doc;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>读 Variable.Value(米/弧度)。读不到返回 null(只写变量是常态,不当错误)。</summary>
	private static double? ReadValue(object vars, string name)
	{
		if (string.IsNullOrEmpty(name)) return null;
		try
		{
			object v = ModelMetrics.InvokeAny(vars, "Item", name);
			if (v == null) return null;
			object raw = ModelMetrics.Get(v, "Value");
			if (raw == null) return null;
			return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>读 Variable.IsReadOnly。老版本没有该属性时返回 null,不猜。</summary>
	private static bool? ReadReadOnly(object vars, string name)
	{
		if (string.IsNullOrEmpty(name)) return null;
		try
		{
			object v = ModelMetrics.InvokeAny(vars, "Item", name);
			if (v == null) return null;
			object raw = ModelMetrics.TryGet(v, "IsReadOnly");
			if (raw == null) return null;
			return Convert.ToBoolean(raw);
		}
		catch
		{
			return null;
		}
	}

	private static string SafeStr(object o)
	{
		try { return o == null ? "" : Convert.ToString(o); }
		catch { return ""; }
	}

	private static string Json(object payload)
	{
		return JsonSerializer.Serialize(payload, payload.GetType());
	}

	private static string Error(string code, string message, object fix)
	{
		return Json(new
		{
			status = "error",
			code,
			message,
			fix
		});
	}
}
