using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using SolidEdgeFramework;

namespace SolidEdge.Spy.McpServer.Tools;

[McpServerToolType]
public static class GeometryTools
{
	private sealed class CaptureMeta
	{
		public bool Ok;

		public string ImagePath;

		public int Width;

		public int Height;

		public string OrientationLabel;

		public bool? OrientationApplied;

		public bool? CameraRestored;

		public string RestoreMethod;

		public string Note;

		public string ErrorMessage;

		public string ToJson()
		{
			if (!Ok)
			{
				return JsonSerializer.Serialize(new
				{
					status = "error",
					message = ErrorMessage
				});
			}
			return JsonSerializer.Serialize(new
			{
				status = "ok",
				image = ImagePath,
				width = Width,
				height = Height,
				orientation = OrientationLabel,
				orientationApplied = OrientationApplied,
				cameraRestored = CameraRestored,
				restoreMethod = RestoreMethod,
				note = Note
			});
		}
	}

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	/// <summary>
	/// 2026-10-05 整改:View 对象没有 Orientation 属性(IDispatch 与 PIA 均无,报 DISP_E_UNKNOWNNAME 0x80020006),
	/// 旧代码 TryInvokeSet(view,"Orientation",…) 必然失败、视角从未真正切换。改用文档命名视图(真机实测存在
	/// top/front/right/iso/dimetric/trimetric)+ RotateCamera 180° 派生反向视图。参见 SE2022 SDK
	/// SolidEdgeFramework~View~ApplyNamedView.html / ~View~RotateCamera.html / ~NamedViews~Names.html。
	/// </summary>
	private sealed class ViewSpec
	{
		/// <summary>文档命名视图名(null = 不切换视角,仅 current)。</summary>
		public string NamedView;

		/// <summary>非空则在 ApplyNamedView 后再绕该世界轴旋 180°(用于派生 back/left/bottom)。</summary>
		public double[] RotateAxis;

		/// <summary>展示/文件用标签。</summary>
		public string Label;
	}

	private static readonly Dictionary<string, ViewSpec> StandardViewMap = new Dictionary<string, ViewSpec>(StringComparer.OrdinalIgnoreCase)
	{
		["current"] = new ViewSpec { Label = "cur" },
		["iso"] = new ViewSpec { NamedView = "iso", Label = "iso" },
		["top"] = new ViewSpec { NamedView = "top", Label = "top" },
		["right"] = new ViewSpec { NamedView = "right", Label = "right" },
		["front"] = new ViewSpec { NamedView = "front", Label = "front" },
		["back"] = new ViewSpec { NamedView = "front", RotateAxis = new double[3] { 0.0, 0.0, 1.0 }, Label = "back" },
		["left"] = new ViewSpec { NamedView = "right", RotateAxis = new double[3] { 0.0, 0.0, 1.0 }, Label = "left" },
		["bottom"] = new ViewSpec { NamedView = "top", RotateAxis = new double[3] { 0.0, 1.0, 0.0 }, Label = "bottom" }
	};

	private const string CameraBackupViewName = "__se_mcp_bak";

	private static int CamType;

	private static double[] CamArgs;

	private const int SW_RESTORE = 9;

	[McpServerTool]
	[Description("读取模型中对象的定位信息，解决'坐标系心智负担'。返回包围盒/形心/尺寸(mm)/面法向与位置。target 传 obj-N 句柄（特征/Model/RefPlane），或 'refplanes'（扫全部参考面并反推精确法向）/'model'（整个模型）。平面法向用通用梯度法（Profile.Convert3DCoordinate 把世界三轴投影到面上，投影长度≈0 的轴即法向），支持斜平面。坐标单位=米。")]
	public static string se_read_geometry(SolidEdgeContext context, [Description("要读定位的对象：obj-N 句柄 / 'refplanes' / 'model'")] string target)
	{
		try
		{
			return context.Invoke(delegate
			{
				if (string.Equals(target, "refplanes", StringComparison.OrdinalIgnoreCase))
				{
					return ReadAllRefPlanes(context);
				}
				if (string.Equals(target, "model", StringComparison.OrdinalIgnoreCase))
				{
					return ReadModel(context);
				}
				ObjectHandle handle = context.GetHandle(target);
				return (handle == null || handle.ComObject == null) ? Error("找不到对象 " + target + "（支持 obj-N 句柄 / 'refplanes' / 'model'）。") : ReadObject(context, handle.ComObject, handle.TypeName);
			});
		}
		catch (Exception ex)
		{
			return Error("se_read_geometry 失败: " + DescribeException(ex));
		}
	}

	private static string ReadAllRefPlanes(SolidEdgeContext context)
	{
		object obj = Get(context.GetApplication(), "ActiveDocument");
		if (obj == null)
		{
			return Error("没有活动文档。");
		}
		object obj2 = Get(obj, "RefPlanes");
		int num = Count(obj2);
		List<object> list = new List<object>();
		List<string> cleanupErrors = new List<string>();
		for (int i = 1; i <= num; i++)
		{
			object obj3 = Get(obj2, "Item", i);
			string name = SafeString(Get(obj3, "DisplayName")) ?? "(无名称)";
			var tuple = ProbePlaneNormal(context, obj3);
			if (tuple.Item5 != null)
			{
				cleanupErrors.Add("#" + i + " " + name + ": " + tuple.Item5);
			}
			list.Add(new
			{
				index = i,
				name = name,
				normalAxis = tuple.Item1,
				axisAligned = tuple.Item2,
				origin2d = tuple.Item3,
				projectedLength = tuple.Item4,
				hint = ((tuple.Item1 != null) ? (tuple.Item2 ? ("法向沿 " + tuple.Item1 + " 轴（轴对齐平面）") : ("非严格轴对齐（斜平面），法向近似沿 " + tuple.Item1 + " 轴")) : "法向反推失败")
			});
		}
		return JsonSerializer.Serialize(new
		{
			status = "ok",
			target = "refplanes",
			count = num,
			refPlanes = list,
			cleanupError = ((cleanupErrors.Count > 0) ? string.Join("; ", cleanupErrors) : null)
		});
	}

	private static string ReadModel(SolidEdgeContext context)
	{
		object obj = Get(context.GetApplication(), "ActiveDocument");
		if (obj == null)
		{
			return Error("没有活动文档。");
		}
		object obj2 = Get(obj, "Models");
		int num = Count(obj2);
		List<object> list = new List<object>();
		for (int i = 1; i <= num; i++)
		{
			double[] array = TryRangeBox(Get(obj2, "Item", i));
			list.Add(new
			{
				index = i,
				rangeBox = array,
				sizeMm = ((array != null) ? Mm(array) : null),
				centroid = ((array != null) ? Centroid(array) : null)
			});
		}
		return JsonSerializer.Serialize(new
		{
			status = "ok",
			target = "model",
			modelCount = num,
			models = list
		});
	}

	private static string ReadObject(SolidEdgeContext context, object obj, string typeName)
	{
		double[] array = TryRangeBox(obj);
		int faceCount = -1;
		try
		{
			object obj2 = Get(obj, "Faces", 1);
			if (obj2 != null)
			{
				faceCount = Count(obj2);
			}
		}
		catch
		{
		}
		return JsonSerializer.Serialize(new
		{
			status = "ok",
			target = typeName,
			rangeBox = array,
			sizeMm = ((array != null) ? Mm(array) : null),
			centroid = ((array != null) ? Centroid(array) : null),
			faceCount = faceCount,
			note = "坐标单位=米;sizeMm 换算成 UI 显示的毫米。"
		});
	}

	private static (string normalAxis, bool axisAligned, double[] origin2d, double[] projectedLength, string cleanupError) ProbePlaneNormal(SolidEdgeContext context, object plane)
	{
		object tempProfileSet = null;
		object profile = null;
		try
		{
			profile = CreateTempProfile(context, plane, out tempProfileSet);
			if (profile == null)
			{
				return (normalAxis: null, axisAligned: false, origin2d: null, projectedLength: null, cleanupError: null);
			}
			double[] array = Convert3D(profile, 0.0, 0.0, 0.0);
			double[] a = Convert3D(profile, 1.0, 0.0, 0.0);
			double[] a2 = Convert3D(profile, 0.0, 1.0, 0.0);
			double[] a3 = Convert3D(profile, 0.0, 0.0, 1.0);
			double num = Dist2D(a, array);
			double num2 = Dist2D(a2, array);
			double num3 = Dist2D(a3, array);
			double[] item = new double[3] { num, num2, num3 };
			double num4 = Math.Min(num, Math.Min(num2, num3));
			string item2 = ((num4 == num) ? "X" : ((num4 == num2) ? "Y" : "Z"));
			bool item3 = num4 < 1E-06;
			TryCloseProfile(profile);
			return (normalAxis: item2, axisAligned: item3, origin2d: array, projectedLength: item, cleanupError: TryDeleteTempProfileSet(tempProfileSet));
		}
		catch
		{
			TryCloseProfile(profile);
			return (normalAxis: null, axisAligned: false, origin2d: null, projectedLength: null, cleanupError: TryDeleteTempProfileSet(tempProfileSet));
		}
	}

	private static object CreateTempProfile(SolidEdgeContext context, object plane, out object tempProfileSet)
	{
		tempProfileSet = null;
		object obj = Get(context.GetApplication(), "ActiveDocument");
		if (obj == null)
		{
			return null;
		}
		object obj2 = Call(Get(obj, "ProfileSets"), "Add", null);
		tempProfileSet = obj2;
		return Call(Get(obj2, "Profiles"), "Add", new object[1] { plane });
	}

	/// <summary>
	/// 2026-09-16 体检 P0-2 整改:只读工具不得写模型。探查用临时 ProfileSet 用后必删
	/// (官方成员 ProfileSet.Delete()),删除失败时返回人话说明由 ReadAllRefPlanes
	/// 聚合回传,不许静默残留。本删除只针对本工具自己创建的临时对象,不碰用户数据。
	/// </summary>
	private static string TryDeleteTempProfileSet(object tempProfileSet)
	{
		if (tempProfileSet == null)
		{
			return null;
		}
		try
		{
			if (ManualInvoke.TryInvoke(tempProfileSet, "Delete", Array.Empty<object>(), out var _, out var error))
			{
				return null;
			}
			return "临时 ProfileSet 删除失败:" + (error?.Message ?? "未知错误") + "(文档已置脏,残留需手动清理)";
		}
		catch (Exception ex)
		{
			return "临时 ProfileSet 删除异常:" + ex.Message + "(残留需手动清理)";
		}
	}

	private static void TryCloseProfile(object profile)
	{
		try
		{
			Call(profile, "End", new object[1] { 0 });
		}
		catch
		{
		}
	}

	private static double[] Convert3D(object profile, double x, double y, double z)
	{
		object[] array = new object[5] { x, y, z, 0.0, 0.0 };
		ParameterModifier parameterModifier = new ParameterModifier(5);
		parameterModifier[3] = true;
		parameterModifier[4] = true;
		profile.GetType().InvokeMember("Convert3DCoordinate", BindingFlags.InvokeMethod, null, profile, array, new ParameterModifier[1] { parameterModifier }, CultureInfo.InvariantCulture, null);
		return new double[2]
		{
			(double)array[3],
			(double)array[4]
		};
	}

	/// <summary>
	/// face 平面的"全局 2D → 平面局部 (u,v)"仿射映射(2026-10-03 IR 面锚定)。
	/// 世界 2D 语义:外法向轴 Z→(X,Y)、X→(Y,Z)、Y→(X,Z);第三坐标取 0
	/// (投影沿法向,第三坐标不影响结果)。
	/// 与 <see cref="ProbePlaneNormal"/> 同机制:把世界原点与三个单位轴投到平面局部坐标,
	/// 投影长度最小的轴即法向轴;非法向面(斜面)直接拒绝。
	/// </summary>
	internal sealed class FacePlaneMap
	{
		/// <summary>外法向轴字母(X/Y/Z)。</summary>
		internal string NormalAxis;

		private readonly double[] _o;
		private readonly double[] _e1;
		private readonly double[] _e2;

		private FacePlaneMap(string axis, double[] o, double[] e1, double[] e2)
		{
			NormalAxis = axis;
			_o = o;
			_e1 = e1;
			_e2 = e2;
		}

		/// <summary>把全局 2D 坐标 (a,b) 映射成贴面参考平面的局部 (u,v)。</summary>
		internal double[] Map(double a, double b)
		{
			return new double[2]
			{
				_o[0] + a * _e1[0] + b * _e2[0],
				_o[1] + a * _e1[1] + b * _e2[1]
			};
		}

		/// <summary>
		/// 在【调用方已建好的 Profile】上求仿射基(原点 + 两个面内轴向量),不创建/删除任何临时对象。
		/// 非轴向面(斜面)抛异常。
		///
		/// ★ 2026-10-03 实测(真机 E2E):早先的实现会临时建一个 ProfileSet(``Profiles.Add(plane)``)、
		///   投影完再 ``ProfileSet.Delete()`` 掉;结果把那张贴面参考平面的 RCW 弄成
		///   CO_E_OBJNOTCONNECTED(Invoke 报 0x800401FD,puArgErr=0),随后同一张平面的
		///   ``Profiles.Add(plane)`` 直接失败、连回滚删平面也一并挂掉。故改为复用调用方自己的 profile。
		/// </summary>
		internal static FacePlaneMap BuildFromProfile(object profile)
		{
			double[] o = Convert3D(profile, 0.0, 0.0, 0.0);
			double[] ex = Convert3D(profile, 1.0, 0.0, 0.0);
			double[] ey = Convert3D(profile, 0.0, 1.0, 0.0);
			double[] ez = Convert3D(profile, 0.0, 0.0, 1.0);

			double lx = Dist2D(ex, o);
			double ly = Dist2D(ey, o);
			double lz = Dist2D(ez, o);
			double min = Math.Min(lx, Math.Min(ly, lz));
			if (min > 1E-06)
			{
				throw new ArgumentException(
					"该实体面不是轴向面(外法向不沿全局 X/Y/Z),全局 2D 轮廓坐标在斜面上无定义。" +
					"请改用 \"coords\":\"local\"(坐标为该面局部 u/v,支持斜面)或脚本。");
			}

			if (min == lx) return new FacePlaneMap("X", o, Sub(ey, o), Sub(ez, o));
			if (min == ly) return new FacePlaneMap("Y", o, Sub(ex, o), Sub(ez, o));
			return new FacePlaneMap("Z", o, Sub(ex, o), Sub(ey, o));
		}

		private static double[] Sub(double[] p, double[] q)
		{
			return new double[2] { p[0] - q[0], p[1] - q[1] };
		}
	}

	internal static bool IsOrientationName(string s)
	{
		if (!string.IsNullOrWhiteSpace(s))
		{
			return StandardViewMap.ContainsKey(s.Trim());
		}
		return false;
	}

	[McpServerTool]
	[Description("把 Solid Edge 当前活动视口截图并【以图片内容直接返回】给多模态 AI 查看(无需 read_file)。orientation 可选 current(默认,不动视角)/iso/top/front/back/left/right/bottom;默认 Fit 满幅,动了视角/缩放后自动还原原相机。支持 3D 文档与工程图 DFT(DFT 走 SheetWindow.Fit+SaveAsImage 路线,orientation/zoom 被忽略,不还原视角)。图片同时落盘到自管理目录(%TEMP%\\se_mcp_captures,LRU 只保留最近50张/100MB,与上一张画面相同则不重复落盘),元数据里给出路径。注意:截图时 SE 主窗口会短暂跳到前台约2秒(不改变最大化状态)。典型用法:建模/改参数后调用本工具目检结果。")]
	public static List<AIContent> se_capture_viewport(SolidEdgeContext context, [Description("视角:current(默认)/iso/top/front/back/left/right/bottom")] string orientation = "current", [Description("截图前是否 Fit 满幅(默认 true)")] bool fit = true, [Description("Fit 后再 ZoomCamera 的倍率(可选,1 或不传=不缩放)")] double? zoom = null, [Description("落盘原图宽(像素,默认1600)")] int width = 1600, [Description("落盘原图高(像素,默认1200)")] int height = 1200, 	[Description("截图后是否还原原视角(默认 true)")] bool restoreCamera = true, [Description("可选:裁剪区域 \"x,y,w,h\"(0~1 比例,相对整幅;如 \"0.3,0.3,0.4,0.4\" 取中央 40%)。用于放大看局部(某个视图/某个孔位/某段文字),裁剪后就地覆盖落盘图并改写元数据里的宽高")] string region = null)
	{
		try
		{
			return context.Invoke(() => CaptureViewport(context, orientation, fit, zoom, width, height, restoreCamera, inlineImage: true, null, region));
		}
		catch (Exception ex)
		{
			return ErrorContent("se_capture_viewport 失败: " + DescribeException(ex));
		}
	}

	internal static string CliCaptureViewport(SolidEdgeContext context, string orientation, bool fit, double? zoom, int width, int height, bool restoreCamera, string explicitPath)
	{
		try
		{
			// CLI 只要 JSON,不需要图片文件,保持原有去重行为
			return context.Invoke(() => CaptureCore(context, orientation, fit, zoom, width, height, restoreCamera, explicitPath, keepFile: false).ToJson());
		}
		catch (Exception ex)
		{
			return Error("CLI 截图失败: " + DescribeException(ex));
		}
	}

	private static List<AIContent> CaptureViewport(SolidEdgeContext context, string orientation, bool fit, double? zoom, int width, int height, bool restoreCamera, bool inlineImage, string explicitPath, string region)
	{
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Expected O, but got Unknown
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Expected O, but got Unknown
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Expected O, but got Unknown
		// MCP 工具路线:一定要内嵌图片;若给了 region 还要裁剪 —— 所以文件必须真实存在,不能被去重删掉
		CaptureMeta captureMeta = CaptureCore(context, orientation, fit, zoom, width, height, restoreCamera, explicitPath, keepFile: true);
		// region 裁剪:先整幅截、再裁 —— 3D 与 DFT 两条路线都适用,不动相机/窗口矩形逻辑
		if (captureMeta.Ok && !string.IsNullOrWhiteSpace(region) && captureMeta.ImagePath != null)
		{
			int croppedWidth;
			int croppedHeight;
			string cropError;
			if (CropToRegion(captureMeta.ImagePath, region, out croppedWidth, out croppedHeight, out cropError))
			{
				captureMeta.Width = croppedWidth;
				captureMeta.Height = croppedHeight;
				captureMeta.Note = ((captureMeta.Note == null) ? "" : captureMeta.Note + "; ") + "已按 region=" + region + " 裁剪为 " + croppedWidth + "x" + croppedHeight;
			}
			else
			{
				captureMeta.Note = ((captureMeta.Note == null) ? "" : captureMeta.Note + "; ") + "region 裁剪失败(已返回原图): " + cropError;
			}
		}
		List<AIContent> list = new List<AIContent> { (AIContent)new TextContent(captureMeta.ToJson()) };
		if (captureMeta.Ok & inlineImage)
		{
			try
			{
				byte[] array = CaptureStore.DownscaleToPngBytes(captureMeta.ImagePath, 1500);
				list.Add((AIContent)new DataContent((ReadOnlyMemory<byte>)array, "image/png"));
			}
			catch (Exception ex)
			{
				list.Add((AIContent)new TextContent("(内嵌图片生成失败:" + ex.Message + ";可用 read_file 读 " + captureMeta.ImagePath + ")"));
			}
		}
		return list;
	}

	private static CaptureMeta FailMeta(string msg)
	{
		return new CaptureMeta
		{
			Ok = false,
			ErrorMessage = msg
		};
	}

	private static List<AIContent> ErrorContent(string message)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Expected O, but got Unknown
		return new List<AIContent> { (AIContent)new TextContent(JsonSerializer.Serialize(new
		{
			status = "error",
			message = message
		})) };
	}

	/// <summary>把 orientation 参数解析为标准视角键(StandardViewMap 的键);空/缺省按 "current";非法抛异常。</summary>
	private static string ParseOrientationName(string orientation)
	{
		if (string.IsNullOrWhiteSpace(orientation))
		{
			return "current";
		}
		string text = orientation.Trim();
		if (!StandardViewMap.ContainsKey(text))
		{
			throw new ArgumentException("未知视角 \"" + orientation + "\"。可用: current/iso/top/front/back/left/right/bottom");
		}
		return text;
	}

	/// <summary>
	/// 应用标准视角:命名视图(SE 文档自带 top/front/right/iso)+ 需要时绕世界轴旋 180° 派生反向视图。
	/// 成功 true;失败 false 并回填 applyError(供 note 如实说明,不再谎报已切换)。
	/// </summary>
	private static bool ApplyStandardView(object view, string orientKey, out string applyError)
	{
		applyError = null;
		if (!StandardViewMap.TryGetValue(orientKey, out var spec) || spec.NamedView == null)
		{
			return true;
		}
		if (!ManualInvoke.TryInvoke(view, "ApplyNamedView", new object[1] { spec.NamedView }, out var _, out var error))
		{
			applyError = "ApplyNamedView(\"" + spec.NamedView + "\") 失败: " + (error?.Message ?? "未知错误");
			return false;
		}
		if (spec.RotateAxis != null && !ManualInvoke.TryInvoke(view, "RotateCamera", new object[7]
		{
			180.0, 0.0, 0.0, 0.0, spec.RotateAxis[0], spec.RotateAxis[1], spec.RotateAxis[2]
		}, out var _, out var error2))
		{
			applyError = "RotateCamera 失败(派生 " + spec.Label + "): " + (error2?.Message ?? "未知错误");
			return false;
		}
		return true;
	}

	// keepFile: 调用方是否还需要这个图片文件(内嵌给 AI / 按 region 裁剪)。
	// true 时即使画面与上一张相同也保留文件,不删除 —— 否则返回的 ImagePath 指向已删文件,
	// 下游 region 裁剪、内嵌图片、read_file 会连锁失败(2026-09-16 修复)。
	private static CaptureMeta CaptureCore(SolidEdgeContext context, string orientation, bool fit, double? zoom, int width, int height, bool restoreCamera, string explicitPath, bool keepFile)
	{
		//IL_013a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0370: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_030e: Unknown result type (might be due to invalid IL or missing references)
		if (width <= 0 || height <= 0)
		{
			return FailMeta("width/height 必须为正数。");
		}
		string orientName;
		try
		{
			orientName = ParseOrientationName(orientation);
		}
		catch (ArgumentException ex)
		{
			return FailMeta(ex.Message);
		}
		ViewSpec orientSpec = StandardViewMap[orientName];
		bool orientChanges = orientSpec.NamedView != null;
		object application = context.GetApplication();
		if (!ManualInvoke.TryInvoke(application, "ActiveWindow", null, out var result, out var error))
		{
			return FailMeta("取 ActiveWindow 失败: " + error?.Message);
		}
		if (!ManualInvoke.TryInvoke(result, "View", null, out var result2, out var error2))
		{
			if (ManualInvoke.TryInvoke(result, "ActiveSheet", null, out var _, out var _))
			{
				return CaptureSheetWindow(application, result, orientation, fit, zoom, width, height, explicitPath, keepFile);
			}
			return FailMeta("取 View 失败: " + error2?.Message);
		}
		CaptureStore.StartupSweep();
		bool flag = (orientChanges | fit) || (zoom.HasValue && Math.Abs(zoom.Value - 1.0) > 1E-09);
		bool flag2 = false;
		bool flag3 = false;
		object result3;
		Exception error3;
		if (restoreCamera & flag)
		{
			try
			{
				((View)result2).SaveCurrentView((object)"__se_mcp_bak");
				flag2 = true;
			}
			catch
			{
			}
			if (!flag2)
			{
				flag2 = ManualInvoke.TryInvoke(result2, "SaveCurrentView", new object[1] { "__se_mcp_bak" }, out result3, out error3);
			}
			if (!flag2)
			{
				try
				{
					int camType = default(int);
					double num = default(double);
					double num2 = default(double);
					double num3 = default(double);
					double num4 = default(double);
					double num5 = default(double);
					double num6 = default(double);
					double num7 = default(double);
					double num8 = default(double);
					double num9 = default(double);
					double num10 = default(double);
					double num11 = default(double);
					double num12 = default(double);
					double num13 = default(double);
					double num14 = default(double);
					double num15 = default(double);
					double num16 = default(double);
					((View)result2).GetCameraEx(out camType, out num, out num2, out num3, out num4, out num5, out num6, out num7, out num8, out num9, out num10, out num11, out num12, out num13, out num14, out num15, out num16);
					CamType = camType;
					CamArgs = new double[16]
					{
						num, num2, num3, num4, num5, num6, num7, num8, num9, num10,
						num11, num12, num13, num14, num15, num16
					};
					flag3 = true;
				}
				catch
				{
				}
			}
		}
		bool flag4 = true;
		string orientApplyError = null;
		if (orientChanges)
		{
			flag4 = ApplyStandardView(result2, orientName, out orientApplyError);
		}
		if (fit)
		{
			ManualInvoke.TryInvoke(result2, "Fit", null, out result3, out error3);
		}
		if (zoom.HasValue && Math.Abs(zoom.Value - 1.0) > 1E-09)
		{
			ManualInvoke.TryInvoke(result2, "ZoomCamera", new object[1] { zoom.Value }, out result3, out error3);
		}
		Thread.Sleep(600);
		string text = orientSpec.Label;
		string text2 = explicitPath;
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = CaptureStore.NewFilePath(GetDocNameForFile(application), text);
		}
		bool flag5 = TryCaptureWindow(text2, TryGetFrameWindowHandle(result), out var width2, out var height2, out var error4);
		bool? flag6 = null;
		string restoreMethod = null;
		string backupNote = null;
		if (restoreCamera & flag)
		{
			if (flag2)
			{
				bool flag7 = false;
				try
				{
					((View)result2).ApplyNamedView((object)"__se_mcp_bak");
					flag7 = true;
				}
				catch
				{
					flag7 = false;
				}
				if (!flag7)
				{
					flag7 = ManualInvoke.TryInvoke(result2, "ApplyNamedView", new object[1] { "__se_mcp_bak" }, out result3, out error3);
				}
				if (flag7)
				{
					flag6 = true;
					restoreMethod = "named-view";
				}
			}
			if ((flag6 != true) & flag3)
			{
				try
				{
					View val = (View)result2;
					double[] camArgs = CamArgs;
					val.SetCameraEx(CamType, camArgs[0], camArgs[1], camArgs[2], camArgs[3], camArgs[4], camArgs[5], camArgs[6], camArgs[7], camArgs[8], camArgs[9], camArgs[10], camArgs[11], camArgs[12], camArgs[13], camArgs[14], camArgs[15]);
					flag6 = true;
					restoreMethod = "camera-ex";
				}
				catch
				{
				}
			}
			if (flag6 != true)
			{
				flag6 = false;
			}
			// 2026-09-16 体检 P1-6 整改:相机还原成功后立即删除备份命名视图,不再残留污染文档;
			// 还原失败时保留备份供手动恢复,并在 note 里诚实说明(SDK 通道:NamedViews.Remove(Name))。
			if (flag2)
			{
				if (flag6 == true && TryRemoveBackupNamedView(application))
				{
					backupNote = null;
				}
				else if (flag6 == true)
				{
					backupNote = "备份命名视图 " + CameraBackupViewName + " 删除失败,已残留(可手动删除)";
				}
				else
				{
					backupNote = "相机还原失败,备份命名视图 " + CameraBackupViewName + " 已保留供手动恢复";
				}
			}
		}
		if (!flag5)
		{
			return FailMeta("截图失败: " + error4);
		}
		List<string> list = new List<string>();
		if (!flag4)
		{
			list.Add("视角切换失败(" + (orientApplyError ?? "未知错误") + "),截的是切换前视图");
		}
		if (backupNote != null)
		{
			list.Add(backupNote);
		}
		if (flag6 == false)
		{
			list.Add("相机还原失败,named-view 与 camera-ex 均不可用");
		}
		if (explicitPath == null)
		{
			bool isDup = CaptureStore.IsDuplicate(text2);
			if (isDup && !keepFile)
			{
				try
				{
					File.Delete(text2);
				}
				catch
				{
				}
				list.Add("与上一张画面相同,未重复落盘");
			}
			else if (isDup)
			{
				list.Add("与上一张画面相同,保留文件(内嵌/裁剪需要)");
			}
			CaptureStore.EnforceRetention();
		}
		return new CaptureMeta
		{
			Ok = true,
			ImagePath = text2,
			Width = width2,
			Height = height2,
			OrientationLabel = orientation,
			OrientationApplied = (orientChanges ? (bool?)flag4 : null),
			CameraRestored = flag6,
			RestoreMethod = restoreMethod,
			Note = ((list.Count > 0) ? string.Join("; ", list) : null)
		};
	}

	/// <summary>
	/// 2026-09-16 体检 P1-6 整改:删除相机备份命名视图(官方成员 NamedViews.Remove(Name),
	/// 见 SE2022 SDK SolidEdgeFramework~NamedViews~Remove.html)。只删本工具自己创建的
	/// __se_mcp_bak,不碰用户命名视图;失败返回 false 由 note 诚实回传。
	/// </summary>
	private static bool TryRemoveBackupNamedView(object application)
	{
		try
		{
			if (!ManualInvoke.TryInvoke(application, "ActiveDocument", null, out var doc, out var _) || doc == null)
			{
				return false;
			}
			if (!ManualInvoke.TryInvoke(doc, "NamedViews", null, out var views, out var _) || views == null)
			{
				return false;
			}
			return ManualInvoke.TryInvoke(views, "Remove", new object[1] { CameraBackupViewName }, out var _, out var _);
		}
		catch
		{
			return false;
		}
	}

	private static string GetDocNameForFile(object app)
	{
		try
		{
			if (!ManualInvoke.TryInvoke(app, "ActiveDocument", null, out var result, out var error) || result == null)
			{
				return "SE";
			}
			if (!ManualInvoke.TryInvoke(result, "Name", null, out var result2, out error) || result2 == null)
			{
				return "SE";
			}
			return result2.ToString();
		}
		catch
		{
			return "SE";
		}
	}

	private static CaptureMeta CaptureSheetWindow(object application, object sheetWindow, string orientation, bool fit, double? zoom, int width, int height, string explicitPath, bool keepFile)
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrWhiteSpace(orientation) && !string.Equals(orientation.Trim(), "current", StringComparison.OrdinalIgnoreCase))
		{
			list.Add("DFT 为二维视图,orientation \"" + orientation.Trim() + "\" 不适用,已忽略");
		}
		if (zoom.HasValue && Math.Abs(zoom.Value - 1.0) > 1E-09)
		{
			list.Add("DFT 为二维视图,zoom 不适用,已忽略");
		}
		if (fit)
		{
			ManualInvoke.TryInvoke(sheetWindow, "Fit", null, out var _, out var _);
		}
		Thread.Sleep(600);
		string text = (string.IsNullOrWhiteSpace(orientation) ? "cur" : orientation.Trim().ToLowerInvariant());
		if (text == "current")
		{
			text = "cur";
		}
		string text2 = explicitPath;
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = CaptureStore.NewFilePath(GetDocNameForFile(application), text);
		}
		string text3 = Path.ChangeExtension(text2, ".bmp");
		bool flag = string.Equals(Path.GetFullPath(text3), Path.GetFullPath(text2), StringComparison.OrdinalIgnoreCase);
		if (!ManualInvoke.TryInvoke(sheetWindow, "SaveAsImage", new object[3] { text3, width, height }, out var _, out var error))
		{
			return FailMeta("DFT 截图失败(SheetWindow.SaveAsImage): " + error?.Message);
		}
		int num = width;
		int num2 = height;
		if (!flag)
		{
			try
			{
				using (Bitmap bitmap = new Bitmap(text3))
				{
					num = bitmap.Width;
					num2 = bitmap.Height;
					bitmap.Save(text2, ImageFormat.Png);
				}
			}
			catch (Exception ex)
			{
				return FailMeta("DFT 图片转存 PNG 失败: " + ex.Message);
			}
			finally
			{
				try
				{
					File.Delete(text3);
				}
				catch
				{
				}
			}
		}
		else
		{
			try
			{
				using Bitmap bitmap2 = new Bitmap(text2);
				num = bitmap2.Width;
				num2 = bitmap2.Height;
			}
			catch
			{
			}
		}
		if (explicitPath == null)
		{
			bool isDup = CaptureStore.IsDuplicate(text2);
			if (isDup && !keepFile)
			{
				try
				{
					File.Delete(text2);
				}
				catch
				{
				}
				list.Add("与上一张画面相同,未重复落盘");
			}
			else if (isDup)
			{
				list.Add("与上一张画面相同,保留文件(内嵌/裁剪需要)");
			}
			CaptureStore.EnforceRetention();
		}
		list.Add("DFT 路线: SheetWindow.Fit + SaveAsImage,无相机概念,不还原视角");
		return new CaptureMeta
		{
			Ok = true,
			ImagePath = text2,
			Width = num,
			Height = num2,
			OrientationLabel = orientation,
			CameraRestored = null,
			RestoreMethod = null,
			Note = ((list.Count > 0) ? string.Join("; ", list) : null)
		};
	}

	/// <summary>
	/// 按比例裁剪已落盘的截图(region = "x,y,w,h",0~1,相对整幅)。
	/// 用途:排查"标注飘到哪/某个视图长什么样"时放大局部 —— 比全屏缩略图可靠得多。
	/// 就地覆盖原图:先写临时文件再替换(Bitmap 仍持有原文件句柄,直接覆盖会失败)。
	/// </summary>
	private static bool CropToRegion(string path, string region, out int newWidth, out int newHeight, out string error)
	{
		newWidth = 0;
		newHeight = 0;
		error = null;
		string[] parts = region.Split(',');
		if (parts.Length != 4)
		{
			error = "region 需要 4 个数(x,y,w,h),如 \"0.25,0.25,0.5,0.5\"";
			return false;
		}
		double rx, ry, rw, rh;
		if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out rx)
			|| !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ry)
			|| !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out rw)
			|| !double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out rh))
		{
			error = "region 里有非数字: \"" + region + "\"";
			return false;
		}
		if (rw <= 0 || rh <= 0)
		{
			error = "region 的 w/h 必须大于 0";
			return false;
		}
		if (!File.Exists(path))
		{
			error = "截图文件不存在: " + path;
			return false;
		}
		string tempPath = path + ".region.tmp.png";
		try
		{
			int x, y, cw, ch;
			using (Bitmap source = new Bitmap(path))
			{
				x = (int)Math.Round(rx * source.Width);
				y = (int)Math.Round(ry * source.Height);
				cw = (int)Math.Round(rw * source.Width);
				ch = (int)Math.Round(rh * source.Height);
				if (x < 0) x = 0;
				if (y < 0) y = 0;
				if (cw < 1) cw = 1;
				if (ch < 1) ch = 1;
				if (x + cw > source.Width) cw = source.Width - x;
				if (y + ch > source.Height) ch = source.Height - y;
				if (cw < 1 || ch < 1)
				{
					error = "裁剪区域落在图外(算得 " + cw + "x" + ch + ")";
					return false;
				}
				using (Bitmap cropped = source.Clone(new Rectangle(x, y, cw, ch), source.PixelFormat))
				{
					cropped.Save(tempPath, ImageFormat.Png);
				}
				newWidth = cw;
				newHeight = ch;
			}
			File.Copy(tempPath, path, overwrite: true);
		}
		catch (Exception ex)
		{
			error = ex.GetType().Name + ": " + ex.Message;
			return false;
		}
		finally
		{
			try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
		}
		return true;
	}

	private static bool TryCaptureWindow(string path, nint frameHwnd, out int width, out int height, out string error)
	{
		width = 0;
		height = 0;
		error = null;
		// 2026-10-05 整改:旧实现用 Process.MainWindowTitle 找 SE 窗口,窗口最小化/隐藏时
		// Process.MainWindowHandle 会退化为 0,直接报"找不到 Solid Edge 主窗口"(真机复现)。
		// 改为优先用 SE COM 的 ActiveWindow.hWnd → GetAncestor(GA_ROOT) 拿主框架窗口句柄
		// (真机实测:WinLayer 子窗 → EngineFrame 主框架,标题 "Solid Edge 2022 - ..."),
		// 句柄不随可见性变化;COM 路线不可用时退回按窗口类名 "EngineFrame" 直接查找。
		nint hwnd = frameHwnd;
		if (hwnd == IntPtr.Zero)
		{
			hwnd = FindWindow("EngineFrame", null);
		}
		if (hwnd == IntPtr.Zero)
		{
			error = "找不到 Solid Edge 主窗口（进程/窗口标题）";
			return false;
		}
		try
		{
			if (IsIconic(hwnd))
			{
				ShowWindow(hwnd, 9);
			}
			SetForegroundWindow(hwnd);
			Thread.Sleep(1200);
			if (!GetWindowRect(hwnd, out var rect))
			{
				error = "GetWindowRect 失败";
				return false;
			}
			// 与窗口所在屏幕的「工作区」求交。
			// 最大化窗口的 rect 常比工作区四周各大 ~11px（DPI 虚拟化 / 被 DWM 裁掉的边框）：
			//   · 溢出到屏幕外的一侧 → CopyFromScreen 截出纯黑边
			//   · 跨屏时溢出到相邻屏幕 → 把邻屏内容截进图里（比黑边更糟）
			MONITORINFO monitorINFO = default(MONITORINFO);
			monitorINFO.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
			nint num3 = MonitorFromWindow(hwnd, 2u);
			if (num3 != IntPtr.Zero && GetMonitorInfo(num3, ref monitorINFO))
			{
				rect.Left = Math.Max(rect.Left, monitorINFO.rcWork.Left);
				rect.Top = Math.Max(rect.Top, monitorINFO.rcWork.Top);
				rect.Right = Math.Min(rect.Right, monitorINFO.rcWork.Right);
				rect.Bottom = Math.Min(rect.Bottom, monitorINFO.rcWork.Bottom);
			}
			int num = rect.Right - rect.Left;
			int num2 = rect.Bottom - rect.Top;
			if (num <= 0 || num2 <= 0)
			{
				error = "窗口尺寸无效: " + num + "x" + num2;
				return false;
			}
			using (Bitmap bitmap = new Bitmap(num, num2))
			{
				using Graphics graphics = Graphics.FromImage(bitmap);
				graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(num, num2));
				bitmap.Save(path, ImageFormat.Png);
			}
			width = num;
			height = num2;
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	/// <summary>
	/// 从 SE 的 Window(通常是 Application.ActiveWindow)读 hWnd,再上溯到顶层主框架窗口。
	/// 真机实测:ActiveWindow.hWnd 是 MDI 客户区子窗(WinLayer),其 GA_ROOT 才是主框架
	/// (EngineFrame)。句柄与窗口可见性无关,最小化时同样有效。失败返回 IntPtr.Zero。
	/// </summary>
	private static nint TryGetFrameWindowHandle(object window)
	{
		if (window == null)
		{
			return IntPtr.Zero;
		}
		try
		{
			if (!ManualInvoke.TryInvoke(window, "hWnd", null, out var hWndObj, out var _) || hWndObj == null)
			{
				return IntPtr.Zero;
			}
			nint num = new IntPtr(Convert.ToInt64(hWndObj));
			if (num == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}
			nint num2 = GetAncestor(num, 2u);
			return (num2 != IntPtr.Zero) ? num2 : num;
		}
		catch
		{
			return IntPtr.Zero;
		}
	}

	[DllImport("user32.dll")]
	private static extern nint GetAncestor(nint hWnd, uint gaFlags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindow(string lpClassName, string lpWindowName);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

	private struct MONITORINFO
	{
		public int cbSize;

		public RECT rcMonitor;

		public RECT rcWork;

		public uint dwFlags;
	}

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hWnd, out RECT rect);

	private static object Get(object o, string n)
	{
		return InvokeRaw(o, n, null);
	}

	private static object Get(object o, string n, object a)
	{
		return InvokeRaw(o, n, new object[1] { a });
	}

	private static object Call(object o, string n, object[] a)
	{
		return InvokeRaw(o, n, a);
	}

	private static object InvokeRaw(object o, string n, object[] a)
	{
		if (ManualInvoke.TryInvoke(o, n, a ?? Array.Empty<object>(), out var result, out var error))
		{
			return result;
		}
		throw error ?? new Exception("IDispatch 调用失败: " + n);
	}

	private static int Count(object c)
	{
		try
		{
			return Convert.ToInt32(Get(c, "Count"));
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>
	/// 读包围盒 [xmin,ymin,zmin,xmax,ymax,zmax](米)。
	///
	/// 2026-09-14 修正:原实现读 `RangeBox` 属性 —— 零件侧全 SDK 查无此成员
	/// (`Model_members.html` / `ExtrudedProtrusion_members.html` 均 0 命中;
	///  `RangeBox` 只在钣金 `ShowRangeBox` 与装配 `Occurrence.GetRangeBox()` 里出现),
	/// 所以它**恒返 null** —— 这正是 `se_read_geometry target=model` 长期
	/// `rangeBox=null` 的原因(refplanes 那条路走的是草图点换算,不受影响)。
	/// 改用官方 `Body.GetRange`(SolidEdgeGeometry.Body,两个 ByRef Double() out 参数):
	///   var body = (SolidEdgeGeometry.Body)host.Body; body.GetRange(ref min, ref max);
	/// 入参可能是 Model / 特征 / 其它拓扑对象 → 先试自身(自身有 Body),再试 Parent。
	/// </summary>
	private static double[] TryRangeBox(object obj)
	{
		double[] num = TryBodyRange(obj);
		if (num != null)
		{
			return num;
		}
		try
		{
			return TryBodyRange(Get(obj, "Parent"));
		}
		catch
		{
			return null;
		}
	}

	/// <summary>宿主对象的实体包围盒,走官方 Body.GetRange;读不到返回 null。</summary>
	private static double[] TryBodyRange(object host)
	{
		if (host == null)
		{
			return null;
		}
		try
		{
			object obj = Get(host, "Body");
			if (obj == null)
			{
				return null;
			}
			SolidEdgeGeometry.Body body = (SolidEdgeGeometry.Body)obj;
			Array min = Array.CreateInstance(typeof(double), 0);
			Array max = Array.CreateInstance(typeof(double), 0);
			body.GetRange(ref min, ref max);
			if (min != null && max != null && min.Length >= 3 && max.Length >= 3)
			{
				return new double[6]
				{
					Convert.ToDouble(min.GetValue(0)),
					Convert.ToDouble(min.GetValue(1)),
					Convert.ToDouble(min.GetValue(2)),
					Convert.ToDouble(max.GetValue(0)),
					Convert.ToDouble(max.GetValue(1)),
					Convert.ToDouble(max.GetValue(2))
				};
			}
		}
		catch
		{
		}
		return null;
	}

	private static double[] Centroid(double[] rb)
	{
		return new double[3]
		{
			(rb[0] + rb[3]) / 2.0,
			(rb[1] + rb[4]) / 2.0,
			(rb[2] + rb[5]) / 2.0
		};
	}

	private static double[] Mm(double[] rb)
	{
		return new double[3]
		{
			(rb[3] - rb[0]) * 1000.0,
			(rb[4] - rb[1]) * 1000.0,
			(rb[5] - rb[2]) * 1000.0
		};
	}

	private static double Dist2D(double[] a, double[] b)
	{
		return Math.Sqrt(Math.Pow(a[0] - b[0], 2.0) + Math.Pow(a[1] - b[1], 2.0));
	}

	private static string SafeString(object v)
	{
		try
		{
			return v?.ToString();
		}
		catch
		{
			return null;
		}
	}

	private static string DescribeException(Exception ex)
	{
		StringBuilder stringBuilder = new StringBuilder();
		Exception ex2 = ex;
		int num = 0;
		while (ex2 != null && num < 3)
		{
			if (num > 0)
			{
				stringBuilder.Append(" <- 内部: ");
			}
			stringBuilder.Append(ex2.GetType().Name);
			if (!string.IsNullOrEmpty(ex2.Message))
			{
				stringBuilder.Append(": ").Append(ex2.Message);
			}
			ex2 = ex2.InnerException;
			num++;
		}
		return stringBuilder.ToString();
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new
		{
			status = "error",
			message = message
		});
	}
}
