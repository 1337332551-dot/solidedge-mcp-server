using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 特征参数改写(非标注参数)。
///
/// 为什么需要独立工具(2026-10-25 真机实战):
///   改"孔数"时 <c>Pattern.NumberOfOccurrences</c> 在互操作元数据里就是<b>只读</b>——
///   csc 直接报 CS0200,连编译都过不去;真正的开关在阵列<b>载体轮廓</b>的
///   <c>CircularPattern2d.Count</c> 上(8→12→8 实测,体积 586117.52 → 563724.24 → 586117.52 精确往返)。
///   这个"参数不在变量表、属性只读、但底层载体可写"的模式在 SE 里很常见,
///   过去只能手写脚本遍历 ProfileSets 去碰,现在收敛成一条工具:
///   按 <b>强类型 → IDispatch → 载体子对象</b> 三级选路,命中即停并回报是哪条路生效。
/// </summary>
[McpServerToolType]
public static class FeatureParameterTools
{
	/// <summary>
	/// 已知只读成员名(interop 元数据里没有 setter,写它编译期就失败,别浪费一次 COM 往返)。
	/// 这些参数只能走载体通道。
	/// </summary>
	private static readonly HashSet<string> KnownReadOnlyMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"NumberOfOccurrences"
	};

	[McpServerTool]
	[Description("改写特征参数(不是草图标注变量——标注变量请用 se_get_variables 的 action=set)。典型场景:改圆周阵列的【孔数】、矩形阵列的【行列数】。" +
		"Pattern.NumberOfOccurrences 在 interop 里只读(编译期 CS0200),真正的开关在阵列载体轮廓上:" +
		"圆周=CircularPattern2d.Count,矩形=RectangularPattern2d.XCount/YCount(2026-11-08 实测可写)。" +
		"本工具按【强类型 → IDispatch → 载体子对象】三级选路,命中即停并在 channel 字段回报哪条路生效。" +
		"feature 支持 SE 特征名(Pattern_1/Hole_1)或 obj-N;parameter 支持逻辑名 count/xcount/ycount/angle/radius/depth/suppress/visible 或成员原名。" +
		"写后自动 Recompute 并回传体积/包围盒前后变化;verify=true 时回读,没生效标 silentFailure。" +
		"失败时 attempts 列出各通道报错,便于判断是只读/成员名不对/值类型不匹配。")]
	public static string se_set_feature_parameter(
		SolidEdgeContext context,
		[Description("特征:SE 特征名(如 Pattern_1 / Hole_1 / RevolvedProtrusion_1,大小写不敏感,可带 @ 前缀)或 obj-N 句柄")] string feature,
		[Description("参数逻辑名:count(圆周实例数)/ xcount|ycount(矩形阵列行列数)/ angle / radius / depth / suppress / visible;也可直接写成员原名(如 AngularSpacing)")] string parameter,
		[Description("新值,按字面量解析:12 → 整数,1.5 → 小数,true/false → 布尔,其它按字符串原样传")] string value,
		[Description("写后是否回读校验(默认 true)。写入无报错但没生效时返回 silentFailure=true。")] bool verify = true,
		[Description("写后是否 Recompute(默认 true)。false 只写不重算,几何不会跟随。")] bool recompute = true)
	{
		try
		{
			return context.Invoke(delegate
			{
				object doc = null;
				try { doc = context.GetApplication().ActiveDocument; }
				catch { doc = null; }
				if (doc == null)
				{
					return Error("当前没有活动文档。", null);
				}
				string low = NormalizeParameter(parameter);
				if (low == null)
				{
					return Error("parameter 不能为空。可用:count / xcount / ycount / angle / radius / depth / suppress / visible,或成员原名。", null);
				}

				string featureName;
				string resolveError;
				object feat = ResolveFeature(context, doc, feature, out featureName, out resolveError);
				if (feat == null)
				{
					return Error(resolveError, null);
				}

				string valueError;
				object parsed = ParseValue(value, out valueError);
				if (valueError != null)
				{
					return Error(valueError, null);
				}

				string[] candidates = MemberCandidates(low);
				List<object> attempts = new List<object>();
				string channel = null;
				string memberUsed = null;

				ModelMetrics.Snapshot before = ModelMetrics.Capture(doc);

				// ---------- L1:强类型通道 ----------
				// 动态特征类型无法统一 cast,这里只做两件确定的事:
				//   ① 已知只读成员直接跳过(避免拿一次无意义的 COM 失败);
				//   ② 如实记录"为什么没走强类型",不让调用方误以为试过。
				bool knownReadOnly = false;
				foreach (string m in candidates)
				{
					if (KnownReadOnlyMembers.Contains(m)) { knownReadOnly = true; break; }
				}
				attempts.Add(new
				{
					channel = "pia",
					ok = false,
					skipped = true,
					detail = knownReadOnly
						? "该参数在 interop 元数据里只读(如 Pattern.NumberOfOccurrences,编译期 CS0200),跳过强类型写,直接试载体通道。"
						: "特征运行期类型不固定,没有对该参数的统一强类型写法;直接走 IDispatch。"
				});

				// ---------- L2:IDispatch 写属性 ----------
				foreach (string member in candidates)
				{
					if (KnownReadOnlyMembers.Contains(member))
					{
						attempts.Add(new { channel = "idispatch:" + member, ok = false, skipped = true, detail = "已知只读成员(interop 无 setter),跳过。" });
						continue;
					}
					try
					{
						feat.GetType().InvokeMember(member, System.Reflection.BindingFlags.SetProperty, null,
							feat, new object[] { CoerceFor(member, parsed) });
						channel = "idispatch:" + member;
						memberUsed = member;
						attempts.Add(new { channel = channel, ok = true, detail = "写入成功。" });
						break;
					}
					catch (Exception ex)
					{
						attempts.Add(new { channel = "idispatch:" + member, ok = false, detail = ModelMetrics.DescribeException(ex) });
					}
				}

				// ---------- L3:载体子对象(圆周阵列孔数就在这儿) ----------
				if (channel == null && IsCountLike(low))
				{
					object carrier = FindCircularPattern2d(doc);
					if (carrier == null)
					{
						attempts.Add(new
						{
							channel = "carrier:CircularPattern2d.Count",
							ok = false,
							detail = "没有在任何草图轮廓上找到 CircularPattern2d(该特征可能不是圆周阵列,或载体草图已被清理)。"
						});
					}
					else
					{
						try
						{
							int n = Convert.ToInt32(parsed, CultureInfo.InvariantCulture);
							carrier.GetType().InvokeMember("Count", System.Reflection.BindingFlags.SetProperty,
								null, carrier, new object[] { n });
							channel = "carrier:CircularPattern2d.Count";
							memberUsed = "Count";
							attempts.Add(new { channel, ok = true, detail = "写入载体轮廓的圆周阵列定义成功(这是孔数真正的开关)。" });
						}
						catch (Exception ex)
						{
							attempts.Add(new { channel = "carrier:CircularPattern2d.Count", ok = false, detail = ModelMetrics.DescribeException(ex) });
						}
					}
				}

				// ---------- L3b:矩形阵列载体(2026-11-08):行列数的开关是 RectangularPattern2d.XCount/YCount ----------
				//   (2026-11-08 实测可写:xc 3→4 → Recompute → occ 9→12 精确;XSpace/YSpace 不可写,改间距须 replaceOf 重建)
				//   ★ targeting 用 Pattern.Profile 直取它自己的载体轮廓——全文档扫描在多阵列文档会打错目标
				//     (b13 实测踩过:ARRAY33 的 xcount 写到了 ROW1 上,readBack"5"验证的还是错对象)。
				if (channel == null && (low == "xcount" || low == "ycount"))
				{
					string rectMember = low == "xcount" ? "XCount" : "YCount";
					string rectChannel = "carrier:RectangularPattern2d." + rectMember;
					object carrierRect = FindCarrierPattern2dForFeature(feat, doc, "RectangularPatterns2d");
					if (carrierRect == null)
					{
						attempts.Add(new
						{
							channel = rectChannel,
							ok = false,
							detail = "该特征的载体轮廓上没有 RectangularPattern2d(该特征可能不是矩形阵列,或载体草图已被清理)。"
						});
					}
					else
					{
						try
						{
							int n = Convert.ToInt32(parsed, CultureInfo.InvariantCulture);
							carrierRect.GetType().InvokeMember(rectMember, System.Reflection.BindingFlags.SetProperty,
								null, carrierRect, new object[] { n });
							channel = rectChannel;
							memberUsed = rectMember;
							attempts.Add(new { channel, ok = true, detail = "写入该特征自己的载体矩形阵列定义成功(这是行列数真正的开关)。" });
						}
						catch (Exception ex)
						{
							attempts.Add(new { channel = rectChannel, ok = false, detail = ModelMetrics.DescribeException(ex) });
						}
					}
				}

				if (channel == null)
				{
					return JsonSerializer.Serialize(new
					{
						status = "error",
						code = "E607",
						feature = featureName,
						parameter,
						value,
						attempts,
						message = "所有写入通道都失败,参数未改动。",
						fix = new
						{
							action = "inspect",
							next = new[]
							{
								"se_describe_object(该特征句柄) 看成员名与 writable 标记",
								"se_read_model_state 确认特征名对不对(用 Features 里的 Name)",
								"若是孔数,确认该特征是 pattern 且载体轮廓还在(se_get_variables list 里能看到 Bolt_R 说明种子孔在)"
							}
						}
					});
				}

				// ---------- 重算 + 采数 ----------
				string recomputeNote = null;
				if (recompute)
				{
					try { ModelMetrics.Call(doc, "Recompute"); }
					catch (Exception ex) { recomputeNote = "Recompute 失败: " + ModelMetrics.DescribeException(ex); }
				}
				ModelMetrics.Snapshot after = ModelMetrics.Capture(doc);

				// ---------- 回读 ----------
				object readBackRaw = null;
				bool readBackOk = false;
				string readBackNote = null;
				if (verify)
				{
					foreach (string m in candidates)
					{
						try
						{
							readBackRaw = ModelMetrics.Get(feat, m);
							readBackOk = true;
							break;
						}
						catch { }
					}
					if (!readBackOk && IsCountLike(low))
					{
						// 走载体通道时,特征本身的 NumberOfOccurrences 是只读但可读,用它做交叉核对
						try
						{
							readBackRaw = ModelMetrics.Get(feat, "NumberOfOccurrences");
							readBackOk = true;
							readBackNote = "特征属性回读(只读),实际写入走的是载体通道。";
						}
						catch { }
					}
					if (!readBackOk && (low == "xcount" || low == "ycount") && channel != null)
					{
						// 矩形阵列载体通道:特征本身没有 XCount/YCount。Recompute 后旧 RCW 可能失效——
						// 按名重取特征(新 Model)再精确回它自己的载体 2D 阵列定义读(不做全文档扫描,防多阵列文档错目标)。
						try
						{
							string rn; string rerr;
							object featFresh = ResolveFeature(context, doc, featureName, out rn, out rerr);
							if (featFresh != null)
							{
								object carrierFresh = FindCarrierPattern2dForFeature(featFresh, doc, "RectangularPatterns2d");
								if (carrierFresh != null)
								{
									readBackRaw = ModelMetrics.Get(carrierFresh, low == "xcount" ? "XCount" : "YCount");
									readBackOk = true;
									readBackNote = "载体 2D 矩形阵列定义回读(按名重取特征后精确回它自己的载体),实际写入走的是载体通道。";
								}
							}
						}
						catch { }
					}
				}

				bool? verified = null;
				if (verify && readBackOk)
				{
					verified = ValuesEqual(readBackRaw, parsed);
				}

				bool geometryMoved = false;
				if (before != null && after != null && before.VolumeMm3.HasValue && after.VolumeMm3.HasValue)
				{
					geometryMoved = Math.Abs(after.VolumeMm3.Value - before.VolumeMm3.Value) > 1e-6;
				}

				AuditLog.Write("se_set_feature_parameter", featureName, memberUsed, true, Convert.ToString(parsed),
					InvocationRisk.ModelChanging, true, channel);

				string conclusion;
				if (verified.HasValue && !verified.Value)
				{
					conclusion = "写入接口返回成功但回读值不符(silentFailure):SE 可能做了规范化(如把角度按度/弧度换算),或该属性只是提示性字段。"
								 + "以 delta 为准——几何动了才是真改到模型。";
				}
				else if (!recompute)
				{
					conclusion = "已写入但未重算(recompute=false),模型尚未跟随。";
				}
				else if (!geometryMoved)
				{
					conclusion = "参数已写入(" + channel + "),但体积/包围盒没变化。可能该参数不驱动几何(如显示类参数),或新值与旧值等价。";
				}
				else
				{
					conclusion = "参数已生效(" + channel + "),几何跟着变了:见 delta.volumeMm3 / delta.sizeMm。";
				}

				return JsonSerializer.Serialize(new
				{
					status = "ok",
					feature = featureName,
					parameter,
					value,
					parsedValue = Convert.ToString(parsed, CultureInfo.InvariantCulture),
					channel,
					memberUsed,
					recompute,
					attempts,
					before = ModelMetrics.ToPayload(before),
					after = ModelMetrics.ToPayload(after),
					delta = ModelMetrics.BuildDelta(before, after),
					readBack = readBackOk ? Convert.ToString(readBackRaw, CultureInfo.InvariantCulture) : null,
					readBackNote,
					verified,
					silentFailure = verified.HasValue && !verified.Value,
					geometryMoved,
					recomputeNote,
					conclusion
				});
			});
		}
		catch (Exception ex)
		{
			return Error("改写特征参数失败: " + ModelMetrics.DescribeException(ex), null);
		}
	}

	// ==================== 特征解析 ====================

	private static object ResolveFeature(SolidEdgeContext context, object doc, string feature,
		out string featureName, out string error)
	{
		featureName = null;
		error = null;
		string text = (feature ?? "").Trim();
		if (text.Length == 0)
		{
			error = "feature 不能为空(传 SE 特征名或 obj-N 句柄)。";
			return null;
		}

		if (text.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
		{
			ObjectHandle handle = null;
			try { handle = context.GetHandle(text); }
			catch { handle = null; }
			if (handle == null || handle.ComObject == null)
			{
				error = "句柄 " + text + " 不存在或已失效。句柄表随 MCP 进程存亡,请重新 se_get_selection 获取。";
				return null;
			}
			featureName = SafeStr(ModelMetrics.TryGet(handle.ComObject, "Name"));
			return handle.ComObject;
		}

		object model = ModelMetrics.FreshModel(doc);
		if (model == null)
		{
			error = "当前文档没有实体 Model(空零件或只有曲面体),无特征可改。";
			return null;
		}
		object features = ModelMetrics.TryGet(model, "Features");
		if (features == null)
		{
			error = "读不到模型的 Features 集合。";
			return null;
		}
		int count = 0;
		try { count = Convert.ToInt32(ModelMetrics.Get(features, "Count")); }
		catch { count = 0; }

		string wanted = text.TrimStart('@');
		List<string> names = new List<string>();
		object fuzzy = null;
		for (int i = 1; i <= count; i++)
		{
			object f = null;
			try { f = ModelMetrics.InvokeAny(features, "Item", i); }
			catch { continue; }
			if (f == null) continue;
			string nm = SafeStr(ModelMetrics.TryGet(f, "Name"));
			if (string.IsNullOrEmpty(nm)) continue;
			names.Add(nm);
			if (string.Equals(nm, wanted, StringComparison.OrdinalIgnoreCase))
			{
				featureName = nm;
				return f;
			}
			if (fuzzy == null && nm.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				fuzzy = f;
				featureName = nm;
			}
		}
		if (fuzzy != null) return fuzzy;

		error = "找不到特征 \"" + text + "\"。当前特征: " + (names.Count > 0 ? string.Join(", ", names) : "(读不到)");
		return null;
	}

	// ==================== 参数名 → 成员候选 ====================

	private static string NormalizeParameter(string parameter)
	{
		if (string.IsNullOrWhiteSpace(parameter)) return null;
		return parameter.Trim().ToLowerInvariant();
	}

	private static bool IsCountLike(string low)
	{
		return low == "count" || low == "occurrences" || low == "number" || low == "instances";
	}

	private static string[] MemberCandidates(string low)
	{
		switch (low)
		{
			case "count":
			case "occurrences":
			case "number":
			case "instances":
				return new[] { "NumberOfOccurrences", "OccurrenceCount", "Count" };
			case "xcount":
				return new[] { "XCount" };
			case "ycount":
				return new[] { "YCount" };
			case "angle":
				return new[] { "AngularSpacing", "Angle", "Spacing" };
			case "radius":
				return new[] { "Radius", "Value" };
			case "depth":
				return new[] { "Depth", "Distance" };
			case "suppress":
			case "suppressed":
				return new[] { "Suppress", "Suppressed" };
			case "visible":
				return new[] { "Visible" };
			default:
				return new[] { low };
		}
	}

	// ==================== 值解析/类型适配 ====================

	/// <summary>
	/// 字面量解析:整数尽量给 int(SE 的 Count/实例数类属性收 int,给 double 会 E_INVALIDARG),
	/// 这点在 2026-10-25 改孔数时实打实踩过。
	/// </summary>
	private static object ParseValue(string raw, out string error)
	{
		error = null;
		string s = (raw ?? "").Trim();
		if (s.Length == 0)
		{
			error = "value 不能为空。";
			return null;
		}
		if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
		if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

		double d;
		if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
		{
			if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e9)
			{
				return (int)Math.Round(d);
			}
			return d;
		}
		return s;
	}

	/// <summary>按成员名把值调成该属性期望的类型;调不动就原样传(让 COM 自己报错,错误信息更准)。</summary>
	private static object CoerceFor(string member, object value)
	{
		string m = (member ?? "").ToLowerInvariant();
		try
		{
			if (m.Contains("count") || m.Contains("occurrence") || m.Contains("index"))
			{
				return Convert.ToInt32(value, CultureInfo.InvariantCulture);
			}
			if (m.Contains("visible") || m.Contains("suppress") || m.Contains("clockwise") || m.Contains("enable"))
			{
				return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
			}
		}
		catch
		{
			// 类型调不动 → 原样传
		}
		return value;
	}

	private static bool ValuesEqual(object readBack, object written)
	{
		if (readBack == null) return false;
		if (written is int)
		{
			try { return Convert.ToInt64(readBack, CultureInfo.InvariantCulture) == (int)written; }
			catch { return false; }
		}
		if (written is bool)
		{
			try { return Convert.ToBoolean(readBack, CultureInfo.InvariantCulture) == (bool)written; }
			catch { return false; }
		}
		if (written is double)
		{
			try { return Math.Abs(Convert.ToDouble(readBack, CultureInfo.InvariantCulture) - (double)written) <= 1e-9; }
			catch { return false; }
		}
		return string.Equals(SafeStr(readBack), SafeStr(written), StringComparison.Ordinal);
	}

	// ==================== 载体轮廓查找 ====================

	/// <summary>
	/// 在所有草图轮廓里找 CircularPattern2d(Count&gt;0 的那个)。这就是圆周阵列的"孔数开关"。
	/// 2026-10-25 实测:profile.CircularPatterns2d.Item(1).Count 可写,写 12 后重算体积精确变化。
	/// </summary>
	private static object FindCircularPattern2d(object doc)
	{
		return FindPattern2d(doc, "CircularPatterns2d");
	}

	/// <summary>
	/// 在所有草图轮廓里找 RectangularPattern2d(2026-11-08):矩形阵列的"行列数开关"
	/// (XCount/YCount 可写,实测 xc 3→4 → occ 9→12 精确;XSpace/YSpace 不可写,改间距须 replaceOf 重建)。
	/// </summary>
	private static object FindRectangularPattern2d(object doc)
	{
		return FindPattern2d(doc, "RectangularPatterns2d");
	}

	/// <summary>
	/// 精确 targeting(2026-11-08):阵列特征自带 Profile 属性——它的 2D 阵列定义就挂在自己的载体轮廓上,
	/// 直取它,不落入全文档扫描(多阵列文档下全扫描会把 A 阵列的参数写到 B 阵列上,b13 实测踩过)。
	/// Profile 直取失败(旧特征/载体被清)再兜底全文档扫描(单阵列文档下等价)。
	/// </summary>
	private static object FindCarrierPattern2dForFeature(object feat, object doc, string collectionName)
	{
		try
		{
			object prof = ModelMetrics.TryGet(feat, "Profile");
			if (prof != null)
			{
				object col = ModelMetrics.TryGet(prof, collectionName);
				if (col != null)
				{
					int cn = 0;
					try { cn = Convert.ToInt32(ModelMetrics.Get(col, "Count")); }
					catch { }
					if (cn > 0) return ModelMetrics.InvokeAny(col, "Item", 1);
				}
			}
		}
		catch { }
		return FindPattern2d(doc, collectionName);
	}

	/// <summary>按 2D 阵列集合名在所有草图轮廓里找第一个非空阵列定义(CircularPatterns2d / RectangularPatterns2d 共用)。</summary>
	private static object FindPattern2d(object doc, string collectionName)
	{
		object psets = ModelMetrics.TryGet(doc, "ProfileSets");
		if (psets == null) return null;
		int psCount = 0;
		try { psCount = Convert.ToInt32(ModelMetrics.Get(psets, "Count")); }
		catch { return null; }

		for (int i = 1; i <= psCount; i++)
		{
			try
			{
				object ps = ModelMetrics.InvokeAny(psets, "Item", i);
				object profiles = ModelMetrics.TryGet(ps, "Profiles");
				if (profiles == null) continue;
				int pn = 0;
				try { pn = Convert.ToInt32(ModelMetrics.Get(profiles, "Count")); }
				catch { continue; }
				for (int j = 1; j <= pn; j++)
				{
					try
					{
						object prof = ModelMetrics.InvokeAny(profiles, "Item", j);
						object cps = ModelMetrics.TryGet(prof, collectionName);
						if (cps == null) continue;
						int cn = 0;
						try { cn = Convert.ToInt32(ModelMetrics.Get(cps, "Count")); }
						catch { continue; }
						if (cn <= 0) continue;
						return ModelMetrics.InvokeAny(cps, "Item", 1);
					}
					catch { }
				}
			}
			catch { }
		}
		return null;
	}

	private static string SafeStr(object o)
	{
		try { return o == null ? "" : Convert.ToString(o); }
		catch { return ""; }
	}

	private static string Error(string message, object detail)
	{
		return JsonSerializer.Serialize(new
		{
			status = "error",
			message,
			detail
		});
	}
}
