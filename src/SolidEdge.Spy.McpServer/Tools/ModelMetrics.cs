using System;
using System.Reflection;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 模型度量快照(体积/包围盒)与"重算后重新取 Model/Body"的公共设施。
///
/// 为什么单独抽出来(2026-10-25 真机实测):
///   ① <c>doc.Recompute()</c> 之后,先前拿到的 Model/Body RCW 会失效——继续读体积直接报
///      E_FAIL(0x80004005),不是几何坏了。凡是"改参数→重算→读模型"的链路都必须重新取一次,
///      这个坑在变量驱动、特征参数改写、保存后校验里会各踩一遍,所以统一收在这里。
///   ② 体积(精确到 1e-9 m³)与包围盒是"改参数到底动没动模型"最便宜也最硬的证据:
///      Status=正常 只证明特征活着,不证明几何对(既有结论)。
///
/// 约定:所有方法只读、绝不抛;读不到就留 null 并把原因写进 Snapshot.Error。
/// </summary>
internal static class ModelMetrics
{
	/// <summary>一次度量结果(单位已换算成 mm / mm³)。</summary>
	internal sealed class Snapshot
	{
		public double? VolumeMm3;
		/// <summary>[xmin,ymin,zmin,xmax,ymax,zmax](mm)</summary>
		public double[] RangeBoxMm;
		/// <summary>[dx,dy,dz](mm)</summary>
		public double[] SizeMm;
		public string Error;
	}

	/// <summary>
	/// 重新取 Model 对象。凡是 Recompute 之后的读模型操作都从这里拿,别复用旧引用。
	/// 返回 null 表示没有实体(空零件/纯曲面体/文档不可读)。
	/// </summary>
	public static object FreshModel(object doc, int modelIndex = 1)
	{
		if (doc == null) return null;
		if (modelIndex < 1) modelIndex = 1;
		try
		{
			object models = Get(doc, "Models");
			if (models == null) return null;
			int count = Convert.ToInt32(Get(models, "Count"));
			if (count < modelIndex) return null;
			return InvokeAny(models, "Item", modelIndex);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>采集体积 + 包围盒(mm)。任何一项读不到就留 null,并累计 Error。</summary>
	public static Snapshot Capture(object doc, int modelIndex = 1)
	{
		Snapshot s = new Snapshot();
		object model = FreshModel(doc, modelIndex);
		if (model == null)
		{
			s.Error = "没有实体 Model(空零件、只有曲面体,或模型索引越界)";
			return s;
		}

		object body = null;
		try { body = Get(model, "Body"); }
		catch (Exception ex) { s.Error = "Model.Body 读不到: " + ex.Message; }
		if (body == null)
		{
			if (s.Error == null) s.Error = "Model.Body 为 null(空模型)";
			return s;
		}

		try
		{
			double m3 = ((SolidEdgeGeometry.Body)body).Volume;
			s.VolumeMm3 = m3 * 1e9;
		}
		catch (Exception ex)
		{
			s.Error = Append(s.Error, "体积读不到: " + ex.Message);
		}

		try
		{
			Array minPt = Array.CreateInstance(typeof(double), 0);
			Array maxPt = Array.CreateInstance(typeof(double), 0);
			((SolidEdgeGeometry.Body)body).GetRange(ref minPt, ref maxPt);
			if (minPt != null && maxPt != null && minPt.Length >= 3 && maxPt.Length >= 3)
			{
				double[] rb = new double[6];
				double[] size = new double[3];
				for (int i = 0; i < 3; i++)
				{
					double lo = Convert.ToDouble(minPt.GetValue(i)) * 1000.0;
					double hi = Convert.ToDouble(maxPt.GetValue(i)) * 1000.0;
					rb[i] = Round(lo);
					rb[i + 3] = Round(hi);
					size[i] = Round(hi - lo);
				}
				s.RangeBoxMm = rb;
				s.SizeMm = size;
			}
			else
			{
				s.Error = Append(s.Error, "包围盒 GetRange 返回空数组");
			}
		}
		catch (Exception ex)
		{
			s.Error = Append(s.Error, "包围盒读不到: " + ex.Message);
		}

		return s;
	}

	/// <summary>JSON 载荷:体积/包围盒/尺寸,读不到的字段为 null 并附 error。</summary>
	public static object ToPayload(Snapshot s)
	{
		if (s == null) return null;
		return new
		{
			volumeMm3 = s.VolumeMm3.HasValue ? (double?)Round(s.VolumeMm3.Value) : null,
			sizeMm = s.SizeMm,
			rangeBoxMm = s.RangeBoxMm,
			error = s.Error
		};
	}

	/// <summary>前后差值:体积增量(mm³)与三向尺寸增量(mm)。任一端缺失则对应字段为 null。</summary>
	public static object BuildDelta(Snapshot before, Snapshot after)
	{
		double? dv = null;
		if (before != null && after != null && before.VolumeMm3.HasValue && after.VolumeMm3.HasValue)
		{
			dv = Round(after.VolumeMm3.Value - before.VolumeMm3.Value);
		}
		double[] dsize = null;
		if (before != null && after != null && before.SizeMm != null && after.SizeMm != null)
		{
			dsize = new double[3];
			for (int i = 0; i < 3; i++) dsize[i] = Round(after.SizeMm[i] - before.SizeMm[i]);
		}
		return new { volumeMm3 = dv, sizeMm = dsize };
	}

	// ---------------- COM 晚绑定小工具(与方法同名语义,刻意不依赖 dynamic) ----------------

	/// <summary>读属性。</summary>
	public static object Get(object o, string member)
	{
		return o.GetType().InvokeMember(member, BindingFlags.GetProperty, null, o, null);
	}

	/// <summary>调方法(无参)。</summary>
	public static object Call(object o, string member)
	{
		return o.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, o, null);
	}

	/// <summary>
	/// 调"带参属性或方法"(如 Variables.Item("名")、ProfileSets.Item(1))。
	/// 对 COM 对象 GetProperty|InvokeMethod 一起给最稳——SE 的 Item 在 IDispatch 里既像属性又像方法。
	/// </summary>
	public static object InvokeAny(object o, string member, params object[] args)
	{
		return o.GetType().InvokeMember(member,
			BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, o, args);
	}

	/// <summary>读属性,失败返回 null(用于"读不到也不该炸"的探针式读取)。</summary>
	public static object TryGet(object o, string member)
	{
		try { return Get(o, member); } catch { return null; }
	}

	/// <summary>
	/// 读变量公式。★ <c>Variables.GetFormula</c> 的参数是【字符串变量名】而不是 Variable 对象
	/// (源码旧注释 2026-10-16 实测:传对象必失败,曾被"空公式入库"掩盖),所以这里单独封一个,
	/// 防止后续又有人把 dim 对象传进来。
	/// </summary>
	public static object TryGetFormula(object vars, string name)
	{
		if (string.IsNullOrEmpty(name)) return null;
		try { return InvokeAny(vars, "GetFormula", name); } catch { return null; }
	}

	/// <summary>把异常压成一行可读文本(避免把整段 stack 塞进 JSON)。</summary>
	public static string DescribeException(Exception ex)
	{
		if (ex == null) return null;
		string msg = ex.Message;
		try
		{
			if (ex.InnerException != null) msg += " <- " + ex.InnerException.Message;
			if (ex is System.Runtime.InteropServices.COMException com)
			{
				msg += " (hr=0x" + com.ErrorCode.ToString("X8") + ")";
			}
		}
		catch { }
		return msg;
	}

	private static string Append(string existing, string add)
	{
		return string.IsNullOrEmpty(existing) ? add : existing + "; " + add;
	}

	private static double Round(double v)
	{
		return Math.Round(v, 6);
	}
}
