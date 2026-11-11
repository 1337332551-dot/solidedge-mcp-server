using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using System.ComponentModel;
using SolidEdgeGeometry;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_read_model_state：模型状态读取（拓扑 + 特征树），目标是替代"截图看模型建得对不对"。
///
/// 为什么需要它：
/// - se_read_geometry 只给包围盒/面数这些整体量，看不出"结构长什么样"；
/// - 截图能看结构，但要跳前台 2 秒、且只能靠肉眼判断"几个孔/在哪/圆角有没有出来"。
///
/// 本工具给三类证据：
/// 1. 整体量：壳数、面数、体积、表面积、包围盒；
/// 2. 面级结构：按面类型计数 + 轴对齐平面按(法向, 位置)聚合 + 圆柱面按(轴向, 直径)聚合（孔/轴颈清单）；
/// 3. 特征树：每个特征的名称/类型/Status/健康档。
///
/// 真机实测依据（2026-10-23，1PEZ24304701-606 吸入锥管，116 面）：
/// - Face 可用成员：ID / Area / Edges / Loops / Vertices / IsParamReversed / Continuity / GetRange / Geometry(QI)；
/// - Face.FacetCount 与 Body.Area **读不到**（DISP_E_UNKNOWNNAME），不要用；
/// - Face.GeometryForm 在 SE 2022 强类型通道返回 9（不可判类型）→ 一律 QI；
/// - Cylinder 只有 Radius（无 AxisVector/CenterPoint）→ 轴向靠 GetRange 的直径反推；
/// - Plane 只有 Type 属性，法向走 Plane.GetNormalVector(ref Array) 方法；
/// - Body.Faces(1).Count 与 Body.Shells 各 Faces 累加完全一致（实测 116=116）；
/// - Face.Area 实测 0~4ms/面（旧的 54ms/面 结论来自另一条通道，本通道不成立）。
/// </summary>
[McpServerToolType]
public static class ModelStateTools
{
	private const double MmPerMeter = 1000.0;

	[McpServerTool]
	[Description("读模型状态(拓扑 + 特征树),替代'截图看模型建得对不对'。detail=summary(默认):壳数/面数/体积/表面积/包围盒 + 面按类型计数 + " +
		"轴对齐平面按(外法向,位置)聚合 + 完整圆柱按(轴向,直径)聚合(孔与轴颈清单,含孔心/长度/面积;boss=true 凸面、false 孔)+ " +
		"非完整圆柱 partialCylinders(圆角/过渡,给半径/轴向/coverage)+ 特征树。" +
		"detail=faces 另附逐面明细(Face.ID/类型/面积/法向或直径/位置/边数/环数)。单位 mm、mm²、mm³;normal 是【外法向】。" +
		"读不到的字段省略并在 warnings 说明,不猜值。\n" +
		"cylinderSummary 给孔统计:holeCount 是判为孔的【面数】(一孔拆多面时偏大);'一共几个孔'看 distinctHoleCenters" +
		"(按圆心去重,沉头孔算一个;partial 壁不计入,是下限);bossUnknownCount>0 时两值都是下限。" +
		"已知限制:面→所属特征无 API;曲面形态与工程图版面仍需截图。\n" +
		"传 verify=expects(mm 对象)即做尺寸自检(原 se_verify_model):od/bore/thickness/pcd/holeCount/holeDia/rfOD/rfH," +
		"量不出标 undetermined;此时只返回 checks[]/verdict。\n" +
			"分工:本工具=全局概览/聚合统计/特征树/尺寸自检;要按条件找某个面并拿建模索引用 se_find_objects。")]
	public static string se_read_model_state(
		SolidEdgeContext context,
		[Description("summary=概览(默认,体量小)；faces=概览+逐面明细")] string detail = "summary",
		[Description("是否读面积(默认 true；实测 0~4ms/面，关掉可更快)")] bool area = true,
		[Description("是否附特征树状态(默认 true)")] bool includeFeatures = true,
		[Description("可选文档句柄 obj-N；省略=活动文档")] string objectId = null,
		[Description("detail=faces 时逐面明细的最大面数(默认 300；超出只给概览并提示)")] int maxFaces = 300,
		[Description("detail=faces 时逐面附边明细:每条边给 index(0-based,=fillet/chamfer 的 edge 参数)/kind(Line/Circle/BSplineCurve)/" +
		"startMm/endMm/midMm/lengthMm/sharedBy(1=开放边,2=两邻面共用)。选边建模前先看它。" +
		"边不给半径/圆心(Circle 边无此属性,用 midMm 到面圆心的距离判内外)。默认 false(输出量大)")] bool edgeInfo = false,
		[Description("尺寸自检 expects(JSON,mm):{\"od\":100,\"bore\":30,\"holeCount\":4};给了就不读拓扑(原 se_verify_model)")] JsonElement? verify = null,
		[Description("verify 的允许偏差(毫米),默认 0.05")] double toleranceMm = 0.05,
		[Description("verify 的模型序号(默认 1)")] int modelIndex = 1)
	{
		try
		{
			if (verify.HasValue)
			{
				JsonElement v = verify.Value;
				// 防御性解包(2026-11-10):ZCode 等客户端对 object 参数疑似双重编码——对象被
				// 序列化成"字符串包 JSON"到达(工具面精简终验报告§5:MCP 通道 3/3 失败,CLI/stdio 正常)。
				// 有界解包(最多 3 层):字符串能解析出 JSON 对象就解包使用;解析失败走下方原有报错,
				// 不改变正确客户端的行为。
				for (int i = 0; i < 3 && v.ValueKind == JsonValueKind.String; i++)
				{
					try
					{
						using (JsonDocument unpacked = JsonDocument.Parse(v.GetString()))
						{
							if (unpacked.RootElement.ValueKind == JsonValueKind.Object ||
								unpacked.RootElement.ValueKind == JsonValueKind.String)
							{
								v = unpacked.RootElement.Clone();
							}
							else
							{
								break;
							}
						}
					}
					catch { break; /* 非 JSON 字符串,维持原语义 */ }
				}
				if (v.ValueKind == JsonValueKind.Object)
				{
					// 尺寸自检(原 se_verify_model,2026-11-07 并入):整链转调,返回与旧工具同构
					return VerificationTools.se_verify_model(context, v, toleranceMm, modelIndex);
				}
				if (v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined)
				{
					return Error("verify 必须是 expects JSON 对象,如 {\"od\":100,\"bore\":30,\"holeCount\":4};只想读拓扑就不要传 verify。");
				}
			}
			return context.Invoke(() => ReadCore(context, detail, area, includeFeatures, objectId, maxFaces, edgeInfo));
		}
		catch (Exception ex)
		{
			return Error("se_read_model_state 失败: " + EvidenceContract.DescribeException(ex));
		}
	}

	private static string ReadCore(SolidEdgeContext context, string detail, bool area, bool includeFeatures,
		string objectId, int maxFaces, bool edgeInfo)
	{
		bool full = string.Equals(detail, "faces", StringComparison.OrdinalIgnoreCase);

		object doc;
		if (!string.IsNullOrWhiteSpace(objectId))
		{
			ObjectHandle h = context.GetHandle(objectId);
			if (h == null || h.ComObject == null)
				return Error("找不到对象编号 " + objectId + "。请先 se_get_selection，或省略 objectId 用活动文档。");
			doc = h.ComObject;
		}
		else
		{
			doc = Get(context.GetApplication(), "ActiveDocument");
			if (doc == null) return Error("没有活动文档。");
		}

		object models = Get(doc, "Models");
		if (models == null) return Error("当前文档不是零件（取不到 Models）。本工具只支持 .par 零件。");
		int modelCount = Count(models);
		// 文案区分两种"没有实体"：真空文档 vs 只有曲面/构造体（曲面体不进 Models，实测 extrude_surface 件 modelsCount=0）
		if (modelCount == 0)
			return Error("零件里没有实体 Model：可能是空文档，也可能只含曲面/构造体（曲面体不进 Models，读不到拓扑）。" +
				"若特征树里有曲面特征，这不是错误，只是本工具只读实体拓扑。");

		var modelRows = new List<object>();
		var warnings = new List<string>();
		var faceRowsAll = new List<object>();
		int totalFaces = 0;
		if (edgeInfo && !full)
			warnings.Add("edgeInfo 只在 detail=faces 下输出边明细，当前是 summary，已忽略（不是错误）。");
		bool readEdges = edgeInfo && full;

		for (int mi = 1; mi <= modelCount; mi++)
		{
			object model = Item(models, mi);
			if (model == null) continue;

			object body = Get(model, "Body");
			if (body == null)
			{
				warnings.Add("model[" + mi + "] 没有 Body（可能是空模型或曲面体），已跳过。");
				modelRows.Add(new
				{
					index = mi,
					shellCount = (int?)null,
					faceCount = (int?)null,
					note = "无 Body（空模型/曲面体）"
				});
				continue;
			}

			object faces = null;
			string facesSource = null;
			if (!TryGetAllFaces(body, out faces, out facesSource))
			{
				warnings.Add("model[" + mi + "] 面集合读不到（Body.Faces(1) 与 Shells.Faces 都失败）。");
			}
			int faceCount = (faces == null) ? -1 : Count(faces);
			if (faceCount < 0) faceCount = 0;
			totalFaces += faceCount;

			List<FaceRow> rows = ReadFaces(faces, area, warnings, mi, readEdges);

			double? volumeM3 = TryDouble(body, "Volume");
			double? faceAreaSumMm2 = null;
			double sum = 0;
			int areaKnown = 0;
			foreach (FaceRow r in rows)
			{
				if (r.AreaM2.HasValue) { sum += r.AreaM2.Value; areaKnown++; }
			}
			// 数值必须圆整到输出精度：double 原值直接序列化会出现 17~18 位有效数字，
			// 同一模型两次调用末位就会漂（实测 volumeMm3 一次 30862414.328905255、另一次少一位）
			// → 跨调用/跨会话对比全部失效。所有对外数值统一 Round(3)。
			if (areaKnown > 0) faceAreaSumMm2 = Math.Round(sum * 1e6, 3);

			double[] box = TryBodyRange(body);
			int shellCount = TryShellCount(body);

			// ---- 面类型计数 ----
			var typeCount = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (FaceRow r in rows)
			{
				int c;
				typeCount.TryGetValue(r.Type, out c);
				typeCount[r.Type] = c + 1;
			}
			var faceTypeCount = new List<object>();
			foreach (KeyValuePair<string, int> kv in typeCount)
				faceTypeCount.Add(new { type = kv.Key, count = kv.Value });

			// 输出字段按"有值才出现"组装：null 字段(读不到的量、常量说明)不再占位。
			var row = new Dictionary<string, object>
			{
				["index"] = mi,
				["shellCount"] = shellCount,
				["faceCount"] = faceCount,
				["facesSource"] = facesSource,
				["faceTypeCount"] = faceTypeCount,
				["planarGroups"] = BuildPlanarGroups(rows),
				["faceEdgeSum"] = CountFaceEdgeSum(rows)
			};
			// rangeBoxMm 保留绝对定位(镜头/装配要对坐标)；sizeMm(X/Y/Z 跨度)与 se_model_state_snapshot 统一输出，
			// 调用方不用自己减（2026-10-23 P0-4：此前两工具一边只有 range 一边只有 size，对账要心算）。
			if (box != null)
			{
				row["rangeBoxMm"] = ToMm(box);
				row["sizeMm"] = new[]
				{
					R3((box[3] - box[0]) * MmPerMeter),
					R3((box[4] - box[1]) * MmPerMeter),
					R3((box[5] - box[2]) * MmPerMeter)
				};
			}
			else warnings.Add("model[" + mi + "] 包围盒读不到（Body.GetRange 失败）。");
			if (volumeM3.HasValue) row["volumeMm3"] = Math.Round(volumeM3.Value * 1e9, 3);
			else warnings.Add("model[" + mi + "] 体积读不到（Body.Volume 失败）。");
			if (faceAreaSumMm2.HasValue) row["faceAreaSumMm2"] = faceAreaSumMm2.Value;

			List<object> fullCyl, partialCyl;
			object cylSummary;
			SplitCylinderGroups(rows, warnings, out fullCyl, out partialCyl, out cylSummary);
			row["cylinderSummary"] = cylSummary;
			if (fullCyl.Count > 0) row["cylinders"] = fullCyl;
			if (partialCyl.Count > 0) row["partialCylinders"] = partialCyl;
			List<object> cones = BuildConeGroups(rows);
			if (cones.Count > 0) row["cones"] = cones;
			List<object> tori = BuildTorusGroups(rows);
			if (tori.Count > 0) row["tori"] = tori;
			List<object> oblique = BuildObliquePlanarFaces(rows);
			if (oblique.Count > 0) row["obliquePlanarFaces"] = oblique;

			modelRows.Add(row);

			// ---- 逐面明细（装满为止） ----
			if (full)
			{
				foreach (FaceRow r in rows)
				{
					if (faceRowsAll.Count >= Math.Max(1, maxFaces)) break;
					faceRowsAll.Add(r.ToJson(mi));
				}
			}
		}

		object features = null;
		if (includeFeatures) features = ReadFeatures(models, modelCount, warnings);

		if (full && totalFaces > Math.Max(1, maxFaces))
			warnings.Add("逐面明细只输出前 " + maxFaces + " 个面（实际 " + totalFaces + " 个）；要看其余请调大 maxFaces。");

		var result = new Dictionary<string, object>
		{
			["status"] = "ok",
			["document"] = Convert.ToString(Get(doc, "Name"), CultureInfo.InvariantCulture),
			["detail"] = full ? "faces" : "summary",
			["modelCount"] = modelCount,
			["totalFaceCount"] = totalFaces,
			["models"] = modelRows,
			["warnings"] = warnings
		};
		if (full) result["faces"] = faceRowsAll;
		if (features != null) result["features"] = features;

		return JsonSerializer.Serialize(result);
	}

	// ---------------- 面读取 ----------------

	// ==================== se_verify_model 的测量底座(2026-10-25) ====================

	/// <summary>一个圆柱面(按直径聚合后的单条,供尺寸自检用)。</summary>
	internal sealed class VerifyCylinder
	{
		public string Axis;          // "X"/"Y"/"Z";判不出为 null
		public double DiameterMm;
		public bool? Boss;           // true=凸(轴颈/外圆),false=凹(孔),null=判不出
		public double[] CenterMm;    // 轴向取中点、横向取中点(完整圆柱=真圆心)
		public double LengthMm;      // 轴向长度
	}

	/// <summary>一个轴对齐平面层(法向+位置+面积),供"板厚/端面位置"判定。</summary>
	internal sealed class VerifyPlane
	{
		public string Normal;        // "+Z"/"-Z"/"+X"...;斜平面为 null
		public double PositionMm;
		public double AreaMm2;
	}

	/// <summary>测量结果容器。所有方法只读、不抛。</summary>
	internal sealed class VerifyMeasurement
	{
		public List<VerifyCylinder> Cylinders = new List<VerifyCylinder>();
		public List<VerifyPlane> Planes = new List<VerifyPlane>();
		public List<string> Warnings = new List<string>();
		public double? VolumeMm3;
		public double[] SizeMm;
		public string Error;
	}

	/// <summary>
	/// 给 se_verify_model 量尺寸:复用本文件既有的 <see cref="ReadFaces"/> 读取链(唯一数据源),
	/// 再把 FaceRow 提炼成"圆柱条 + 平面层"两串强类型结果。
	///
	/// 为什么不复用 BuildPlanarGroups / SplitCylinderGroups:那两个方法输出的是**给 AI 看的 JSON 结构**
	/// (匿名对象里套 List&lt;object&gt;),自检要的是"直径/位置/凸凹"这几个标量,反解 JSON 比直接读
	/// FaceRow 更绕、更容易错。所以这里保持"读取链复用、提炼各写一遍"的边界。
	/// </summary>
	internal static VerifyMeasurement MeasureForVerify(object model, int modelIndex = 1)
	{
		var m = new VerifyMeasurement();
		if (model == null)
		{
			m.Error = "没有实体 Model(空零件或只有曲面体)";
			return m;
		}
		object body = Get(model, "Body");
		if (body == null)
		{
			m.Error = "Model.Body 为空(空模型/曲面体)";
			return m;
		}

		object faces = null;
		string facesSource = null;
		if (!TryGetAllFaces(body, out faces, out facesSource))
		{
			m.Error = "面集合读不到(Body.Faces(1) 与 Shells.Faces 都失败)";
			return m;
		}

		List<FaceRow> rows = ReadFaces(faces, true, m.Warnings, modelIndex, false);

		double? volumeM3 = TryDouble(body, "Volume");
		if (volumeM3.HasValue) m.VolumeMm3 = Math.Round(volumeM3.Value * 1e9, 3);

		double[] box = TryBodyRange(body);
		if (box != null && box.Length >= 6)
		{
			m.SizeMm = new[]
			{
				Math.Round((box[3] - box[0]) * 1000.0, 3),
				Math.Round((box[4] - box[1]) * 1000.0, 3),
				Math.Round((box[5] - box[2]) * 1000.0, 3)
			};
		}

		foreach (FaceRow r in rows)
		{
			if (!r.HasRange) continue;

			if (string.Equals(r.Type, "Cylinder", StringComparison.OrdinalIgnoreCase) && r.RadiusM.HasValue)
			{
				int axis = VerifyAxisOf(r.AxisVector, r);
				var c = new VerifyCylinder
				{
					Axis = axis >= 0 ? "XYZ"[axis].ToString() : null,
					DiameterMm = Math.Round(r.RadiusM.Value * 2.0 * MmPerMeter, 3),
					Boss = r.IsBoss
				};
				if (axis >= 0)
				{
					c.LengthMm = Math.Round(r.AxialLengthMm(axis), 3);
					c.CenterMm = new[]
					{
						Math.Round((r.Lo[0] + r.Hi[0]) / 2.0 * MmPerMeter, 3),
						Math.Round((r.Lo[1] + r.Hi[1]) / 2.0 * MmPerMeter, 3),
						Math.Round((r.Lo[2] + r.Hi[2]) / 2.0 * MmPerMeter, 3)
					};
				}
				m.Cylinders.Add(c);
				continue;
			}

			if (string.Equals(r.Type, "Plane", StringComparison.OrdinalIgnoreCase))
			{
				double[] n = r.OutwardNormal ?? r.Normal;
				int axis = -1;
				int sign = 0;
				if (n != null && n.Length >= 3)
				{
					int best = 0;
					for (int k = 1; k < 3; k++) if (Math.Abs(n[k]) > Math.Abs(n[best])) best = k;
					if (Math.Abs(Math.Abs(n[best]) - 1.0) <= 1e-4)
					{
						axis = best;
						sign = n[best] >= 0 ? 1 : -1;
					}
				}
				if (axis >= 0)
				{
					m.Planes.Add(new VerifyPlane
					{
						Normal = (sign > 0 ? "+" : "-") + "XYZ"[axis],
						PositionMm = Math.Round((r.Lo[axis] + r.Hi[axis]) / 2.0 * MmPerMeter, 3),
						AreaMm2 = r.AreaM2.HasValue ? Math.Round(r.AreaM2.Value * 1e6, 3) : 0
					});
				}
			}
		}

		return m;
	}

	/// <summary>圆柱轴向:优先用 GetAxisVector 的主分量;取不到就用 range 最长轴反推。</summary>
	private static int VerifyAxisOf(double[] axisVector, FaceRow r)
	{
		if (axisVector != null && axisVector.Length >= 3)
		{
			int best = 0;
			for (int k = 1; k < 3; k++) if (Math.Abs(axisVector[k]) > Math.Abs(axisVector[best])) best = k;
			if (Math.Abs(axisVector[best]) > 0.5) return best;
		}
		int longest = 0;
		double len = -1;
		for (int k = 0; k < 3; k++)
		{
			double d = Math.Abs(r.Hi[k] - r.Lo[k]);
			if (d > len) { len = d; longest = k; }
		}
		return longest;
	}

	internal sealed class FaceRow
	{
		public int ModelIndex;
		public int Id = -1;
		public string Type = "Unknown";
		public double? AreaM2;
		public bool HasRange;
		public double[] Lo = new double[3];
		public double[] Hi = new double[3];
		public int Edges = -1;
		public int Loops = -1;
		public int Vertices = -1;
		public bool? ParamReversed;
		public int? Continuity;
		public double[] Normal;        // 平面面法向（Plane.GetNormalVector；不带朝向）
		public bool NormalSignFromPlane; // true=符号来自 Plane.GetNormalVector(未校验)；false=叉积/退化轴
		/// <summary>平面面【外法向】(Face.GetNormal 实测)：朝实体外侧。取不到时回退 Normal。</summary>
		public double[] OutwardNormal;
		/// <summary>圆柱轴向单位向量(Cylinder.GetAxisVector 实测)；取不到为 null，由 range 反推。</summary>
		public double[] AxisVector;
		/// <summary>圆柱面凸凹：true=凸(轴颈/凸台)，false=凹(孔)，null=判不出。</summary>
		public bool? IsBoss;
		/// <summary>凸凹判定失败的原因（汇总进 warnings，不逐面输出）。</summary>
		public string Diag;
		public double? RadiusM;        // 圆柱/圆锥半径
		public double? HalfAngleRad;   // 圆锥半角
		public double? MajorRadiusM;
		public double? MinorRadiusM;
		/// <summary>面的 COM 对象（仅本次调用内有效，供 partial 圆柱凸凹兜底判定用；不序列化）。</summary>
		public object ComFace;
		/// <summary>边明细（edgeInfo=true 时填，0-based 序号=fillet/chamfer 的 edge 参数）。</summary>
		public List<EdgeRow> EdgeList;

		public double AxialLengthMm(int axis)
		{
			return (Hi[axis] - Lo[axis]) * MmPerMeter;
		}

		public object ToJson(int modelIndex)
		{
			var d = new Dictionary<string, object>
			{
				["model"] = modelIndex,
				["id"] = Id,
				["type"] = Type,
				["areaMm2"] = AreaM2.HasValue ? (double?)Math.Round(AreaM2.Value * 1e6, 3) : null,
				["rangeMm"] = HasRange ? ToMm(new[] { Lo[0], Lo[1], Lo[2], Hi[0], Hi[1], Hi[2] }) : null,
				["normal"] = Normal,
				["radiusMm"] = RadiusM.HasValue ? (double?)Math.Round(RadiusM.Value * MmPerMeter, 3) : null,
				["halfAngleDeg"] = HalfAngleRad.HasValue ? (double?)Math.Round(HalfAngleRad.Value * 180.0 / Math.PI, 3) : null,
				["majorRadiusMm"] = MajorRadiusM.HasValue ? (double?)Math.Round(MajorRadiusM.Value * MmPerMeter, 3) : null,
				["minorRadiusMm"] = MinorRadiusM.HasValue ? (double?)Math.Round(MinorRadiusM.Value * MmPerMeter, 3) : null,
				["edges"] = Edges >= 0 ? (int?)Edges : null,
				["loops"] = Loops >= 0 ? (int?)Loops : null,
				["vertices"] = Vertices >= 0 ? (int?)Vertices : null,
				["paramReversed"] = ParamReversed,
				["continuity"] = Continuity
			};
			if (EdgeList != null)
			{
				var el = new List<object>();
				foreach (EdgeRow e in EdgeList) el.Add(e.ToJson());
				d["edgeList"] = el;
			}
			return d;
		}
	}

	/// <summary>
	/// 边明细行（edgeInfo=true）。定位是"选边不再盲试"：fillet/chamfer 的 edge 参数是 0-based 边序号，
	/// 建模前先看每条边是什么、中点在哪，直接判断哪条是外轮廓/孔口。
	/// ★ 不给半径/圆心：SolidEdgeGeometry.Circle 只有 Type（CenterPoint/NormalVector 属性不存在，2026-10-23 实测），
	/// 判"外圆边还是孔口边"用 midMm 到面圆心的距离（≈R外=外圆边、≈R孔=孔口边）。
	/// </summary>
	internal sealed class EdgeRow
	{
		public int Index;             // 0-based，与 fillet/chamfer 的 edge 参数一致
		public string Kind = "Other"; // Line / Circle / BSplineCurve / Other / Unknown
		public double[] StartMm;
		public double[] EndMm;
		public double[] MidMm;
		public double? LengthMm;      // GetLengthAtParam(真弧长)；读不到时 Line 边用端点距兜底
		public int? SharedBy;         // 相邻面数：1=开放边，2=两邻面共用
		public string Note;           // 读取异常备注

		public object ToJson()
		{
			var d = new Dictionary<string, object>
			{
				["index"] = Index,
				["kind"] = Kind
			};
			if (StartMm != null) d["startMm"] = StartMm;
			if (EndMm != null) d["endMm"] = EndMm;
			if (MidMm != null) d["midMm"] = MidMm;
			if (LengthMm.HasValue) d["lengthMm"] = LengthMm.Value;
			if (SharedBy.HasValue) d["sharedBy"] = SharedBy.Value;
			if (Note != null) d["note"] = Note;
			return d;
		}
	}

	/// <summary>遍历面集合，逐面读几何属性。任何一项读不到都记 null + 原因，不猜值。</summary>
	internal static List<FaceRow> ReadFaces(object faces, bool readArea, List<string> warnings, int modelIndex, bool readEdges = false)
	{
		var rows = new List<FaceRow>();
		if (faces == null) return rows;
		int n = Count(faces);
		int areaFail = 0;
		for (int i = 1; i <= n; i++)
		{
			object f = Item(faces, i);
			if (f == null) continue;
			var r = new FaceRow { ModelIndex = modelIndex, ComFace = f };
			r.Id = SafeInt(Get(f, "ID"));
			r.Edges = SafeCount(f, "Edges");
			r.Loops = SafeCount(f, "Loops");
			r.Vertices = SafeCount(f, "Vertices");
			r.ParamReversed = SafeBool(Get(f, "IsParamReversed"));
			r.Continuity = SafeNullableInt(Get(f, "Continuity"));
			if (readArea)
			{
				double? a = TryDouble(f, "Area");
				if (a.HasValue) r.AreaM2 = a; else areaFail++;
			}
			double[] lo; double[] hi;
			if (TryGetFaceRange(f, out lo, out hi))
			{
				r.HasRange = true; r.Lo = lo; r.Hi = hi;
			}

			// 几何类型 + 参数（QI，绝不用 GeometryForm——SE2022 返回 9 不可信）
			try
			{
				var face = (SolidEdgeGeometry.Face)f;
				object g = face.Geometry;
				if (g is SolidEdgeGeometry.Plane plane)
				{
					r.Type = "Plane";
					r.Normal = TryPlaneNormal(plane);
					r.NormalSignFromPlane = (r.Normal != null);
					// Plane.GetNormalVector 只给"平面法向",不带朝向:同一张平面的正反两面拿到同一个向量
					// (实测底 z=0 的面向下却报 +Z,归组后会混进 +Z 组)。外法向必须走 Face.GetNormal(u,v)。
					r.OutwardNormal = TryFaceOutwardNormal(face, r.Normal);
				}
				else if (g is SolidEdgeGeometry.Cylinder cyl)
				{
					r.Type = "Cylinder";
					r.RadiusM = TryTypedDouble(cyl, "Radius");
					// Cylinder 没有 AxisVector 属性,但有 GetAxisVector 方法(实测可用);
					// 之前靠"range 哪两轴接近直径"反推,圆角面直接判不出来被静默丢弃。
					r.AxisVector = TryCylinderAxis(cyl);
					r.IsBoss = TryCylinderIsBoss(face, r, r.AxisVector);
				}
				else if (g is SolidEdgeGeometry.Cone cone)
				{
					r.Type = "Cone";
					r.RadiusM = TryTypedDouble(cone, "Radius");
					r.HalfAngleRad = TryTypedDouble(cone, "HalfAngle");
				}
				else if (g is SolidEdgeGeometry.Torus torus)
				{
					r.Type = "Torus";
					r.MajorRadiusM = TryTypedDouble(torus, "MajorRadius");
					r.MinorRadiusM = TryTypedDouble(torus, "MinorRadius");
				}
				else if (g is SolidEdgeGeometry.Sphere)
				{
					r.Type = "Sphere";
				}
				else if (g is SolidEdgeGeometry.BSplineSurface)
				{
					r.Type = "BSplineSurface";
				}
				else
				{
					r.Type = (g == null) ? "Unknown" : "Other";
				}
			}
			catch
			{
				r.Type = "Unknown";
			}
			if (readEdges) r.EdgeList = ReadEdgeRows(f);
			rows.Add(r);
		}
		if (areaFail > 0)
			warnings.Add("model[" + modelIndex + "] 有 " + areaFail + " 个面的面积读不到（其余正常）。");
		return rows;
	}

	// ---------------- 边明细（edgeInfo） ----------------

	/// <summary>
	/// 读一张面的全部边。可用 API（Interop XML + 2026-10-23 真机）：
	/// GetEndPoints(ref,ref) 端点；GetParamExtents(ref double,ref double) 标量参数域；
	/// GetPointAtParam(1,ref Array,ref Array) 参数处取点；GetLengthAtParam(p0,p1,ref len) 真弧长；
	/// GetFaces(out int,out Array) 相邻面。类型 QI（Line/Circle/BSplineCurve），绝不用 GeometryForm。
	/// </summary>
	internal static List<EdgeRow> ReadEdgeRows(object faceObj)
	{
		var rows = new List<EdgeRow>();
		object edges = Get(faceObj, "Edges");
		int n = Count(edges);
		for (int i = 1; i <= n; i++)
		{
			object eo = Item(edges, i);
			if (eo == null) continue;
			var er = new EdgeRow { Index = i - 1 };   // 0-based，与 fillet/chamfer 的 edge 参数对齐
			try
			{
				var e = (SolidEdgeGeometry.Edge)eo;

				// 类型：QI 判（与面类型同一套纪律）
				try
				{
					object g = e.Geometry;
					if (g is SolidEdgeGeometry.Line) er.Kind = "Line";
					else if (g is SolidEdgeGeometry.Circle) er.Kind = "Circle";
					else if (g is SolidEdgeGeometry.BSplineCurve) er.Kind = "BSplineCurve";
					else er.Kind = (g == null) ? "Unknown" : "Other";
				}
				catch { er.Kind = "Unknown"; }

				double[] sp = null, tp = null;
				try
				{
					Array s = Array.CreateInstance(typeof(double), 0);
					Array t = Array.CreateInstance(typeof(double), 0);
					e.GetEndPoints(ref s, ref t);
					sp = Read3(s);
					tp = Read3(t);
				}
				catch { }
				if (sp != null) er.StartMm = ToMm(sp);
				if (tp != null) er.EndMm = ToMm(tp);

				// 参数域中点取 mid（整圆边 start==end，中点是圆上另一点——判内外轮廓就靠它）
				double pmin = 0, pmax = 0;
				bool hasParam = false;
				try { e.GetParamExtents(out pmin, out pmax); hasParam = true; }
				catch { }
				if (hasParam)
				{
					try
					{
						Array prm = new double[] { (pmin + pmax) / 2.0 };
						Array pt = Array.CreateInstance(typeof(double), 0);
						e.GetPointAtParam(1, ref prm, ref pt);
						double[] m = Read3(pt);
						if (m != null) er.MidMm = ToMm(m);
					}
					catch { }
					try
					{
						double len = 0;
						e.GetLengthAtParam(pmin, pmax, out len);
						if (len > 0) er.LengthMm = R3(len * MmPerMeter);
					}
					catch { }
				}
				// 弧长读不到时，Line 边用端点距兜底（曲线不行——弦长≠弧长，宁缺毋滥）
				if (!er.LengthMm.HasValue && er.Kind == "Line" && sp != null && tp != null)
				{
					double dx = tp[0] - sp[0], dy = tp[1] - sp[1], dz = tp[2] - sp[2];
					er.LengthMm = R3(Math.Sqrt(dx * dx + dy * dy + dz * dz) * MmPerMeter);
				}

				try
				{
					int cnt = 0;
					// ref 参数必须预初始化非 null（真机实测传 null 直接 NullReferenceException 0x80004003）
					Array fs = Array.CreateInstance(typeof(object), 0);
					e.GetFaces(out cnt, ref fs);
					er.SharedBy = cnt;
				}
				catch { }
			}
			catch (Exception ex)
			{
				er.Note = "读取失败: " + ex.GetType().Name;
			}
			rows.Add(er);
		}
		return rows;
	}

	/// <summary>Array → double[3]（不足 3 个返回 null）。</summary>
	private static double[] Read3(Array a)
	{
		if (a == null || a.Length < 3) return null;
		return new[] { Dbl(a.GetValue(0)), Dbl(a.GetValue(1)), Dbl(a.GetValue(2)) };
	}

	private static double Dist3(double[] a, double[] b)
	{
		double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
		return Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	/// <summary>Face 在 (u,v) 处的面上点（米）；失败返回 null。</summary>
	private static double[] FacePointAt(SolidEdgeGeometry.Face face, double u, double v)
	{
		try
		{
			Array prm = new double[] { u, v };
			Array pt = Array.CreateInstance(typeof(double), 0);
			face.GetPointAtParam(1, ref prm, ref pt);
			return Read3(pt);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// partial 圆柱面（圆角/被切碎的孔壁）的凸凹兜底判定（P0-3）：弦中点内测法（交接文档 §4.6 方法 B 的变体）。
	/// 在参数域取同 v 的两个 u 点（如 25%/75%），弦中点 M 与弧上中点 M' 比较：
	/// - 沿【角度】方向：M 在弧的凹侧（偏离表面），IsPointInside(M)=true → 材料在凹侧 → 凸（轴颈/外圆角）；false → 凹（孔壁）。
	/// - 沿【直母线】方向（圆柱轴向）：弦就在表面上（M≈M'），该方向不投票——实测必须剔除，否则与角度方向矛盾。
	/// 判不出（弦太短/取点失败/两方向矛盾）留 null，不猜。
	/// </summary>
	private static void TryPartialCylinderIsBoss(FaceRow r)
	{
		try
		{
			var face = r.ComFace as SolidEdgeGeometry.Face;
			if (face == null) { r.Diag = "无 COM 面"; return; }
			object shellObj = Get(face, "Shell");
			var shell = shellObj as SolidEdgeGeometry.Shell;
			if (shell == null) { r.Diag = "无 Shell"; return; }

			Array pmin = Array.CreateInstance(typeof(double), 0);
			Array pmax = Array.CreateInstance(typeof(double), 0);
			face.GetParamRange(ref pmin, ref pmax);
			if (pmin == null || pmax == null || pmin.Length < 2 || pmax.Length < 2) { r.Diag = "无参数域"; return; }
			double u0 = Dbl(pmin.GetValue(0)), u1 = Dbl(pmax.GetValue(0));
			double v0 = Dbl(pmin.GetValue(1)), v1 = Dbl(pmax.GetValue(1));
			double um = (u0 + u1) / 2.0, vm = (v0 + v1) / 2.0;

			bool? voteU = ChordVote(shell, face, u0 + 0.25 * (u1 - u0), vm, u0 + 0.75 * (u1 - u0), vm);
			bool? voteV = ChordVote(shell, face, um, v0 + 0.25 * (v1 - v0), um, v0 + 0.75 * (v1 - v0));
			if (voteU.HasValue && voteV.HasValue && voteU.Value != voteV.Value) { r.Diag = "弦测两方向矛盾"; return; }
			bool? vote = voteU.HasValue ? voteU : voteV;
			if (!vote.HasValue) { r.Diag = "弦测取点失败"; return; }
			r.IsBoss = vote.Value;
			r.Diag = null;   // 兜底成功：从 bossUnknown 统计里摘掉
		}
		catch (Exception ex)
		{
			r.Diag = ex.GetType().Name;
		}
	}

	/// <summary>
	/// 一条弦的投票：取 (u1,v1)/(u2,v2) 两面上点，弦中点 M；弧中参数处的表面点 M' 与 M 距离 &gt; 0.01mm
	/// 才算"弦真的切进了凹侧"（直母线方向 M≈M'，不投票）。M 在实体内=true(凸)/外=false(凹)。
	/// </summary>
	private static bool? ChordVote(SolidEdgeGeometry.Shell shell, SolidEdgeGeometry.Face face,
		double u1, double v1, double u2, double v2)
	{
		double[] a = FacePointAt(face, u1, v1);
		double[] b = FacePointAt(face, u2, v2);
		if (a == null || b == null) return null;
		if (Dist3(a, b) < 1e-4) return null;   // 弦 < 0.1mm，中点离面太近，IsPointInside 不可信

		double[] m = { (a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0, (a[2] + b[2]) / 2.0 };
		double[] surf = FacePointAt(face, (u1 + u2) / 2.0, (v1 + v2) / 2.0);
		if (surf == null) return null;
		if (Dist3(m, surf) < 1e-5) return null;   // 弦中点仍在表面上=直母线方向，不投票

		try
		{
			Array pt = new double[] { m[0], m[1], m[2] };
			// Interop 里 IsPointInside 是参数化属性（同 Body.Faces(1)），C# 侧用索引语法取值
			bool inside = shell.IsPointInside[ref pt];
			return inside;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>取全部面：优先 Body.Faces(1)（实测与 Shells 累加一致），失败退回 Shells 遍历。</summary>
	internal static bool TryGetAllFaces(object body, out object faces, out string source)
	{
		faces = null;
		source = null;
		// 参数化属性 Body.Faces(FaceType) 只能用反射带参拿
		try
		{
			object v = body.GetType().InvokeMember("Faces", BindingFlags.GetProperty, null, body, new object[] { 1 });
			if (v != null) { faces = v; source = "Body.Faces(1)"; return true; }
		}
		catch
		{
		}
		try
		{
			object shells = Get(body, "Shells");
			int sc = Count(shells);
			if (sc == 0) return false;
			// 多壳时合并成一个可枚举集合代价高（要建中间 List），这里直接返回第一壳的 Faces 并把壳数交给调用方比对
			// —— 实测 116 面零件只有 1 个壳；多壳场景由 shellCount 字段暴露，避免静默只读半个模型。
			object first = Get(Item(shells, 1), "Faces");
			if (first == null) return false;
			faces = first;
			source = (sc == 1) ? "Shells.Item(1).Faces" : ("Shells.Item(1).Faces(仅第1壳/" + sc + "壳)");
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static int TryShellCount(object body)
	{
		try
		{
			object shells = Get(body, "Shells");
			return shells == null ? 0 : Count(shells);
		}
		catch
		{
			return 0;
		}
	}

	private static double[] TryPlaneNormal(SolidEdgeGeometry.Plane plane)
	{
		try
		{
			Array nv = Array.CreateInstance(typeof(double), 0);
			plane.GetNormalVector(ref nv);
			if (nv == null || nv.Length < 3) return null;
			double x = Convert.ToDouble(nv.GetValue(0), CultureInfo.InvariantCulture);
			double y = Convert.ToDouble(nv.GetValue(1), CultureInfo.InvariantCulture);
			double z = Convert.ToDouble(nv.GetValue(2), CultureInfo.InvariantCulture);
			double mag = Math.Sqrt(x * x + y * y + z * z);
			if (mag < 1e-12) return null;
			// 圆整到 1e-6：法向只需表达方向，原值会带 17 位有效数字导致跨调用输出不可比
			return new[]
			{
				Math.Round(x / mag, 6),
				Math.Round(y / mag, 6),
				Math.Round(z / mag, 6)
			};
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 平面面【外法向】:Face.GetNormal(1, [u,v], out [nx,ny,nz]) 在参数域中点取值。
	/// 为什么不用 Plane.GetNormalVector:它给的是"平面法向",同一张平面的正反两面拿到同一个向量,
	/// 于是 z=0 的底面会报 +Z、x=0 的侧面会报 +X,按法向分组后朝向判断全错(2026-10-23 实测)。
	/// 取不到/与平面法向不平行时回退 planeNormal(朝向不可信,但不至于没有值)。
	/// </summary>
	private static double[] TryFaceOutwardNormal(SolidEdgeGeometry.Face face, double[] planeNormal)
	{
		try
		{
			Array prm = MidParams(face);
			if (prm == null) return planeNormal;
			Array nrm = Array.CreateInstance(typeof(double), 0);
			face.GetNormal(1, ref prm, ref nrm);
			if (nrm == null || nrm.Length < 3) return planeNormal;

			double x = Dbl(nrm.GetValue(0)), y = Dbl(nrm.GetValue(1)), z = Dbl(nrm.GetValue(2));
			double mag = Math.Sqrt(x * x + y * y + z * z);
			if (mag < 1e-12) return planeNormal;
			double[] n = { R6(x / mag), R6(y / mag), R6(z / mag) };

			// 采信前先与平面法向对齐:必须平行(同向或反向)。不平行说明拿到的不是这张面的法向。
			if (planeNormal != null)
			{
				double dot = n[0] * planeNormal[0] + n[1] * planeNormal[1] + n[2] * planeNormal[2];
				if (Math.Abs(dot) < 0.99) return planeNormal;
			}
			return n;
		}
		catch
		{
			return planeNormal;
		}
	}

	/// <summary>参数域中点 [u,v](GetParamRange)；取不到返回 null。</summary>
	private static Array MidParams(SolidEdgeGeometry.Face face)
	{
		try
		{
			Array pmin = Array.CreateInstance(typeof(double), 0);
			Array pmax = Array.CreateInstance(typeof(double), 0);
			face.GetParamRange(ref pmin, ref pmax);
			if (pmin == null || pmax == null || pmin.Length < 2 || pmax.Length < 2) return null;
			double u = (Dbl(pmin.GetValue(0)) + Dbl(pmax.GetValue(0))) / 2.0;
			double v = (Dbl(pmin.GetValue(1)) + Dbl(pmax.GetValue(1))) / 2.0;
			return new double[] { u, v };
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 圆柱面是"孔(凹)"还是"轴颈/凸台(凸)"：
	/// 取参数域中点处的面上点 P 与外法向 N，径向 r = (P - 圆柱中心) 去掉轴向分量，
	/// N·r &gt; 0 = 法向朝外 = 凸；&lt; 0 = 朝轴心 = 孔。
	/// 中心用 range 中心近似（对完整圆柱就是真圆心；局部圆柱会偏，故只对完整圆柱采信）。
	/// </summary>
	private static bool? TryCylinderIsBoss(SolidEdgeGeometry.Face face, FaceRow r, double[] axis)
	{
		if (axis == null) { r.Diag = "无轴向"; return null; }
		if (!r.HasRange) { r.Diag = "无 range"; return null; }
		try
		{
			Array prm = MidParams(face);
			if (prm == null) { r.Diag = "无参数域"; return null; }
			Array pt = Array.CreateInstance(typeof(double), 0);
			face.GetPointAtParam(1, ref prm, ref pt);
			Array nrm = Array.CreateInstance(typeof(double), 0);
			face.GetNormal(1, ref prm, ref nrm);
			if (pt == null || nrm == null) { r.Diag = "取点/法向返回空"; return null; }
			if (pt.Length < 3 || nrm.Length < 3) { r.Diag = "取点长度=" + pt.Length + " 法向长度=" + nrm.Length; return null; }

			double[] c = { (r.Lo[0] + r.Hi[0]) / 2.0, (r.Lo[1] + r.Hi[1]) / 2.0, (r.Lo[2] + r.Hi[2]) / 2.0 };
			double[] rad =
			{
				Dbl(pt.GetValue(0)) - c[0],
				Dbl(pt.GetValue(1)) - c[1],
				Dbl(pt.GetValue(2)) - c[2]
			};
			double axial = rad[0] * axis[0] + rad[1] * axis[1] + rad[2] * axis[2];
			for (int k = 0; k < 3; k++) rad[k] -= axial * axis[k];
			double rl = Math.Sqrt(rad[0] * rad[0] + rad[1] * rad[1] + rad[2] * rad[2]);
			if (rl < 1e-9) { r.Diag = "取点落在轴上"; return null; }   // 退化，不猜

			double dot = (Dbl(nrm.GetValue(0)) * rad[0] + Dbl(nrm.GetValue(1)) * rad[1] + Dbl(nrm.GetValue(2)) * rad[2]) / rl;
			if (Math.Abs(dot) < 0.5) { r.Diag = "dot=" + Math.Round(dot, 3); return null; }   // 既不像朝外也不像朝内（坏参数），不猜
			return dot > 0;
		}
		catch (Exception ex)
		{
			r.Diag = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8");
			return null;
		}
	}

	/// <summary>
	/// 圆柱轴向单位向量:Cylinder.GetAxisVector(ref Array)。
	/// 之前只能靠"range 哪两轴长度接近直径"反推,圆角(90° 局部圆柱)三轴长度都不等于直径,
	/// 直接判不出轴向 → 面被静默丢弃。有真轴向就能把所有圆柱面都归类。
	/// </summary>
	private static double[] TryCylinderAxis(SolidEdgeGeometry.Cylinder cyl)
	{
		try
		{
			Array v = Array.CreateInstance(typeof(double), 0);
			cyl.GetAxisVector(ref v);
			if (v == null || v.Length < 3) return null;

			// 实测返回 3 个 double(方向)。若给的是 6 个(原点+方向),取模长更接近 1 的那一半。
			double[] best = null;
			double bestErr = double.MaxValue;
			for (int off = 0; off + 3 <= v.Length; off += 3)
			{
				double x = Dbl(v.GetValue(off)), y = Dbl(v.GetValue(off + 1)), z = Dbl(v.GetValue(off + 2));
				double mag = Math.Sqrt(x * x + y * y + z * z);
				if (mag < 1e-12) continue;
				double[] d = { x / mag, y / mag, z / mag };
				double err = Math.Abs(mag - 1.0);
				if (err < bestErr) { bestErr = err; best = d; }
			}
			if (best == null) return null;
			return new[] { R6(best[0]), R6(best[1]), R6(best[2]) };
		}
		catch
		{
			return null;
		}
	}

	/// <summary>圆整到 1e-6,并把 -0 归一成 0(否则 JSON 里出现 -0,跨调用文本比对会不一致)。</summary>
	private static double R6(double v)
	{
		double r = Math.Round(v, 6);
		return (r == 0) ? 0.0 : r;
	}

	private static double Dbl(object v)
	{
		return Convert.ToDouble(v, CultureInfo.InvariantCulture);
	}

	// ---------------- 聚合 ----------------

	/// <summary>
	/// 平面面按(外法向轴+符号, 位置)聚合。位置取 range 中心在法向轴上的分量。
	/// 轴归属由【外法向】判定（|n轴分量|≥0.9999），不靠 range 退化——实测同一个轴对齐面的
	/// range 在该轴上仍可能有 0.001mm 级差值，用退化判据会把 +Y 面误判成斜平面（2026-10-23 首测踩到）。
	/// 外法向拿不到时才退回平面法向，再不行退回 range 退化判据。
	/// </summary>
	private static List<object> BuildPlanarGroups(List<FaceRow> rows)
	{
		var groups = new Dictionary<string, List<FaceRow>>(StringComparer.Ordinal);
		var order = new List<string>();

		foreach (FaceRow r in rows)
		{
			if (r.Type != "Plane" || !r.HasRange) continue;
			int axis = AxisOfPlanarFace(r);
			if (axis < 0) continue;   // 真斜平面

			string sign;
			double[] n = r.OutwardNormal ?? r.Normal;
			if (n != null)
			{
				sign = n[axis] >= 0 ? "+" : "-";
			}
			else
			{
				// 法向未知：按"面相对模型原点在哪一侧"定朝向（对实心件成立，空腔可能反）
				sign = (r.Lo[axis] >= 0) ? "+" : "-";
			}
			string key = sign + "XYZ"[axis];
			if (!groups.ContainsKey(key)) { groups[key] = new List<FaceRow>(); order.Add(key); }
			groups[key].Add(r);
		}

		order.Sort(StringComparer.Ordinal);
		var list = new List<object>();
		foreach (string key in order)
		{
			// 同一法向组内再按位置（mm，0.001mm 容差）细分
			var byPos = new Dictionary<long, List<FaceRow>>();
			var posKeys = new List<long>();
			foreach (FaceRow r in groups[key])
			{
				int axis = "XYZ".IndexOf(key[1]);
				long p = (long)Math.Round(((r.Lo[axis] + r.Hi[axis]) / 2.0) * MmPerMeter * 1000.0);
				if (!byPos.ContainsKey(p)) { byPos[p] = new List<FaceRow>(); posKeys.Add(p); }
				byPos[p].Add(r);
			}
			posKeys.Sort();
			var positions = new List<object>();
			foreach (long p in posKeys)
			{
				var members = byPos[p];
				int axis = "XYZ".IndexOf(key[1]);
				double posMm = ((members[0].Lo[axis] + members[0].Hi[axis]) / 2.0) * MmPerMeter;
				double? areaMm2 = null;
				double sum = 0; int known = 0;
				foreach (FaceRow m in members)
				{
					if (m.AreaM2.HasValue) { sum += m.AreaM2.Value; known++; }
				}
				if (known > 0) areaMm2 = sum * 1e6;
				var ids = new List<int>();
				foreach (FaceRow m in members) ids.Add(m.Id);
				positions.Add(new
				{
					posMm = R3(posMm),
					count = members.Count,
					areaMm2 = areaMm2.HasValue ? (double?)Math.Round(areaMm2.Value, 3) : null,
					faces = ids
				});
			}
			list.Add(new { normal = key, positionCount = positions.Count, positions });
		}
		return list;
	}

	/// <summary>斜平面（法向不沿任何全局轴）：给出法向与位置，不硬按轴分组。</summary>
	private static List<object> BuildObliquePlanarFaces(List<FaceRow> rows)
	{
		var list = new List<object>();
		foreach (FaceRow r in rows)
		{
			if (r.Type != "Plane" || !r.HasRange) continue;
			if (AxisOfPlanarFace(r) >= 0) continue;
			list.Add(new
			{
				id = r.Id,
				normal = r.OutwardNormal ?? r.Normal,
				areaMm2 = r.AreaM2.HasValue ? (double?)Math.Round(r.AreaM2.Value * 1e6, 3) : null,
				centerMm = Center(r),
				edges = r.Edges >= 0 ? (int?)r.Edges : null
			});
		}
		return list;
	}

	/// <summary>
	/// 平面面的轴归属：法向某分量绝对值 ≥ 0.9999 即视为沿该轴（约 0.81° 容差）。
	/// 返回 -1 = 斜平面，或法向/range 都判不出来。
	/// </summary>
	private static int AxisOfPlanarFace(FaceRow r)
	{
		double[] n = r.OutwardNormal ?? r.Normal;
		if (n != null)
		{
			double[] a = { Math.Abs(n[0]), Math.Abs(n[1]), Math.Abs(n[2]) };
			int max = 0;
			for (int k = 1; k < 3; k++) if (a[k] > a[max]) max = k;
			if (a[max] >= 0.9999) return max;
			return -1;
		}
		if (!r.HasRange) return -1;
		// 法向读不到：退回 range 退化判定（容差 0.002mm 或最长边的 1e-5，二者取大）
		double[] len = { r.AxialLengthMm(0), r.AxialLengthMm(1), r.AxialLengthMm(2) };
		double maxLen = Math.Max(len[0], Math.Max(len[1], len[2]));
		double tol = Math.Max(2e-3, maxLen * 1e-5);
		int found = -1; int count = 0;
		for (int k = 0; k < 3; k++)
		{
			if (len[k] <= tol) { if (count == 0) found = k; count++; }
		}
		return count == 1 ? found : -1;
	}

	/// <summary>
	/// 圆柱面分两类输出：
	///   cylinders        = 完整圆柱面（孔壁 / 轴颈），按(轴向, 直径)聚合；
	///   partialCylinders = 非完整圆柱面（圆角 / 过渡 / 槽端），逐面给半径/轴向/长度/coverage。
	/// 为什么必须分（2026-10-23 实测）：90° 圆角面的 range 三轴长度都不等于直径，旧的"两轴接近直径"判据
	/// 判不出轴向 → 面被静默丢弃，验收时"圆角做没做、R 多少"零证据。
	/// 轴向优先用 Cylinder.GetAxisVector（真轴向），取不到才退回 range 反推。
	/// P0-3：同时产出 cylinderSummary——holeCount 直接数孔（完整 boss=false + partial 弦测判孔），
	/// 调用方不用再拿 cylinders+partialCylinders 心算（100 孔件曾要 81+19 手算）。
	/// </summary>
	private static void SplitCylinderGroups(List<FaceRow> rows, List<string> warnings,
		out List<object> full, out List<object> partial, out object cylinderSummary)
	{
		full = new List<object>();
		partial = new List<object>();
		var groups = new Dictionary<string, List<FaceRow>>(StringComparer.Ordinal);
		var meta = new Dictionary<string, int>(StringComparer.Ordinal);   // key -> axisIndex
		var order = new List<string>();
		var holeCenters = new HashSet<string>(StringComparer.Ordinal);   // 完整孔壁的圆心去重键
		int noAxis = 0;
		int fullCount = 0, partialCount = 0, holeCount = 0, bossCount = 0, bossUnknownCount = 0;

		foreach (FaceRow r in rows)
		{
			if (r.Type != "Cylinder" || !r.HasRange || !r.RadiusM.HasValue) continue;
			// 相切处会留下面积=0 的退化"圆柱"薄片(实测 R318 圆角旁边各有一条,x 跨度 0)。
			// 它们既不是孔也不是圆角,列进清单只会主次不分(还可能被当成孔读)。
			if (r.AreaM2.HasValue && r.AreaM2.Value <= 1e-12) continue;
			double rMm = r.RadiusM.Value * MmPerMeter;
			double dMm = rMm * 2.0;

			int axis = AxisIndexOf(r.AxisVector);
			if (axis < 0)
			{
				int guess;
				if (TryGuessCylinderAxis(r, dMm, out guess)) axis = guess;
			}

			double? coverage = Coverage(r, rMm, axis);

			bool isPartial = axis < 0 || !IsFullCylinder(r, rMm, axis, coverage);
			if (isPartial)
			{
				if (axis < 0) noAxis++;
				// 凸凹兜底（P0-3）：方法 A 对 partial 面不稳定（range 中心不是真圆心），
				// 用弦中点内测法再试一次；判不出就留空并计入 bossUnknownCount，不猜。
				if (!r.IsBoss.HasValue) TryPartialCylinderIsBoss(r);
				partialCount++;
				partial.Add(PartialCylinder(r, rMm, axis, coverage));
			}
			else
			{
				fullCount++;
				string key = "XYZ"[axis] + "|" + Math.Round(dMm, 2).ToString("F2", CultureInfo.InvariantCulture);
				if (!groups.ContainsKey(key)) { groups[key] = new List<FaceRow>(); meta[key] = axis; order.Add(key); }
				groups[key].Add(r);
				// 孔位去重：同轴同横心（0.01mm 容差）的孔壁算一个孔（沉头孔两层壁=一个孔位）
				if (r.IsBoss == false)
				{
					double[] c = CenterOnPlane(r, axis);
					int t1 = (axis + 1) % 3, t2 = (axis + 2) % 3;
					holeCenters.Add(axis + "|" + Math.Round(c[t1] * 100.0) + "|" + Math.Round(c[t2] * 100.0));
				}
			}
			if (r.IsBoss.HasValue) { if (r.IsBoss.Value) bossCount++; else holeCount++; }
			else bossUnknownCount++;
		}

		foreach (string key in order)
		{
			int axis = meta[key];
			var members = groups[key];
			double d = members[0].RadiusM.Value * 2.0 * MmPerMeter;
			var entries = new List<object>();
			bool? groupBoss = null;
			bool mixed = false;
			foreach (FaceRow m in members)
			{
				var e = new Dictionary<string, object>
				{
					["id"] = m.Id,
					["centerMm"] = CenterOnPlane(m, axis),
					["lengthMm"] = Math.Round(m.AxialLengthMm(axis), 3)
				};
				if (m.AreaM2.HasValue) e["areaMm2"] = Math.Round(m.AreaM2.Value * 1e6, 3);
				if (m.IsBoss.HasValue)
				{
					e["boss"] = m.IsBoss.Value;   // true=轴颈/凸台，false=孔
					if (!groupBoss.HasValue) groupBoss = m.IsBoss.Value;
					else if (groupBoss.Value != m.IsBoss.Value) mixed = true;
				}
				entries.Add(e);
			}
			var grp = new Dictionary<string, object>
			{
				["axis"] = Convert.ToString("XYZ"[axis]),
				["diameterMm"] = Math.Round(d, 3),
				["count"] = members.Count,
				["faces"] = entries
			};
			// 整组同性质才在组级给结论（省得每条重复读）；混装就不给，避免以偏概全。
			if (groupBoss.HasValue && !mixed) grp["boss"] = groupBoss.Value;
			full.Add(grp);
		}

		if (noAxis > 0)
			warnings.Add("有 " + noAxis + " 个圆柱面的轴向无法判定（既没有 GetAxisVector 值、range 也反推不出），" +
				"已放进 partialCylinders（半径/范围仍可读，但它的轴向和长度不可信）。");

		// 凸凹(孔/轴颈)判不出来的面：不静默省略，给一行汇总原因(半径/位置/长度不受影响)
		var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
		int bossUnknown = 0;
		foreach (FaceRow r in rows)
		{
			if (r.Diag == null) continue;
			bossUnknown++;
			int c; reasons.TryGetValue(r.Diag, out c); reasons[r.Diag] = c + 1;
		}
		if (bossUnknown > 0 && warnings != null)
		{
			var parts = new List<string>();
			foreach (KeyValuePair<string, int> kv in reasons) parts.Add(kv.Key + "×" + kv.Value);
			warnings.Add("有 " + bossUnknown + " 个圆柱面判不出凸凹（" + string.Join("，", parts) +
				"），对应条目的 boss 字段已省略；半径/位置/长度不受影响。");
		}

		cylinderSummary = new
		{
			fullCount,          // 完整圆柱面数（cylinders 里的面）
			partialCount,       // 非完整圆柱面数（partialCylinders 里的面）
			holeCount,          // 判定为孔的【面数】（完整 boss=false + partial 弦测判孔）——一孔拆多面时会大于孔数
			bossCount,          // 判定为凸的面数（轴颈/外圆/外圆角）
			bossUnknownCount,   // 判不出凸凹的面数
			// 按圆心去重的孔位数（0.01mm 容差，沉头孔两层壁算一个）——"一共几个孔"的直接答案；
			// 只统计完整圆柱孔壁（partial 壁圆心不可靠），是下限
			distinctHoleCenters = holeCenters.Count
		};
		if (bossUnknownCount > 0 && warnings != null)
			warnings.Add("cylinderSummary.holeCount=" + holeCount + " 是【下限】：另有 " + bossUnknownCount +
				" 个圆柱面判不出凸凹，未计入。");
	}

	/// <summary>完整圆柱面判定：横截面两向 extent 都要≈直径，且（若读了面积）面积≈2πRL。</summary>
	private static bool IsFullCylinder(FaceRow r, double radiusMm, int axis, double? coverage)
	{
		double d = radiusMm * 2.0;
		for (int k = 0; k < 3; k++)
		{
			if (k == axis) continue;
			if (r.AxialLengthMm(k) < d * 0.95) return false;
		}
		return !coverage.HasValue || coverage.Value >= 0.9;
	}

	/// <summary>面积占"同半径同长度完整圆柱(2πRL)"的比例：1.0=整圈，0.25≈90° 圆角，0.5≈半圆槽端。</summary>
	private static double? Coverage(FaceRow r, double radiusMm, int axis)
	{
		if (axis < 0 || !r.AreaM2.HasValue) return null;
		double len = r.AxialLengthMm(axis);
		if (len <= 0) return null;
		double fullArea = 2.0 * Math.PI * radiusMm * len;
		if (fullArea <= 0) return null;
		return Math.Round(r.AreaM2.Value * 1e6 / fullArea, 3);
	}

	private static object PartialCylinder(FaceRow r, double radiusMm, int axis, double? coverage)
	{
		var e = new Dictionary<string, object>
		{
			["id"] = r.Id,
			["radiusMm"] = Math.Round(radiusMm, 3),
			["rangeMm"] = ToMm(new[] { r.Lo[0], r.Lo[1], r.Lo[2], r.Hi[0], r.Hi[1], r.Hi[2] })
		};
		if (r.IsBoss.HasValue) e["boss"] = r.IsBoss.Value;   // 弦中点内测法判出时给出（P0-3）
		if (axis >= 0)
		{
			e["axis"] = Convert.ToString("XYZ"[axis]);
			e["lengthMm"] = Math.Round(r.AxialLengthMm(axis), 3);
		}
		else if (r.AxisVector != null)
		{
			e["axisVector"] = r.AxisVector;
			e["note"] = "轴向非轴对齐";
		}
		else
		{
			e["note"] = "轴向无法判定";
		}
		if (r.AreaM2.HasValue) e["areaMm2"] = Math.Round(r.AreaM2.Value * 1e6, 3);
		if (coverage.HasValue) e["coverage"] = coverage.Value;
		return e;
	}

	/// <summary>轴对齐方向向量 → 轴序号（|分量|≥0.9999）；非轴对齐返回 -1。</summary>
	private static int AxisIndexOf(double[] v)
	{
		if (v == null) return -1;
		double[] a = { Math.Abs(v[0]), Math.Abs(v[1]), Math.Abs(v[2]) };
		int max = 0;
		for (int k = 1; k < 3; k++) if (a[k] > a[max]) max = k;
		return a[max] >= 0.9999 ? max : -1;
	}

	/// <summary>
	/// 圆柱轴向反推：找与直径最接近的两个轴（容差 = 直径的 15% 或 1mm 取大），剩下的就是轴向。
	/// 达不到两轴匹配时退回"最长轴=轴向"，并标记 inferred（调用方据此提示不确定性）。
	/// </summary>
	private static bool TryGuessCylinderAxis(FaceRow r, double diameterMm, out int axis)
	{
		axis = -1;
		double[] len = { r.AxialLengthMm(0), r.AxialLengthMm(1), r.AxialLengthMm(2) };
		double tol = Math.Max(1.0, diameterMm * 0.15);
		int near = 0;
		int nearA = -1, nearB = -1;
		for (int k = 0; k < 3; k++)
		{
			if (Math.Abs(len[k] - diameterMm) <= tol)
			{
				if (near == 0) nearA = k;
				else if (near == 1) nearB = k;
				near++;
			}
		}
		if (near == 2)
		{
			axis = 3 - nearA - nearB;
			return true;
		}
		// 退回最长轴
		int longest = 0;
		for (int k = 1; k < 3; k++) if (len[k] > len[longest]) longest = k;
		axis = longest;
		return near > 0;
	}

	private static List<object> BuildConeGroups(List<FaceRow> rows)
	{
		var list = new List<object>();
		foreach (FaceRow r in rows)
		{
			if (r.Type != "Cone" || !r.HasRange) continue;
			int axis = LongestAxis(r);
			list.Add(new
			{
				id = r.Id,
				axis = Convert.ToString("XYZ"[axis]),
				radiusMm = r.RadiusM.HasValue ? (double?)Math.Round(r.RadiusM.Value * MmPerMeter, 3) : null,
				halfAngleDeg = r.HalfAngleRad.HasValue ? (double?)Math.Round(r.HalfAngleRad.Value * 180.0 / Math.PI, 3) : null,
				lengthMm = Math.Round(r.AxialLengthMm(axis), 3),
				areaMm2 = r.AreaM2.HasValue ? (double?)Math.Round(r.AreaM2.Value * 1e6, 3) : null
			});
		}
		return list;
	}

	private static List<object> BuildTorusGroups(List<FaceRow> rows)
	{
		var list = new List<object>();
		foreach (FaceRow r in rows)
		{
			if (r.Type != "Torus") continue;
			list.Add(new
			{
				id = r.Id,
				majorRadiusMm = r.MajorRadiusM.HasValue ? (double?)Math.Round(r.MajorRadiusM.Value * MmPerMeter, 3) : null,
				minorRadiusMm = r.MinorRadiusM.HasValue ? (double?)Math.Round(r.MinorRadiusM.Value * MmPerMeter, 3) : null,
				areaMm2 = r.AreaM2.HasValue ? (double?)Math.Round(r.AreaM2.Value * 1e6, 3) : null,
				centerMm = r.HasRange ? Center(r) : null
			});
		}
		return list;
	}

	/// <summary>
	/// 各面边数之和。⚠️ 这不是"开放边/破面"——闭合实体每个面都数了一遍共享边(立方体报 24,实际 12 条边)。
	/// 旧名 openEdges 会让人以为模型有 24 条开放边(破面),已改名(2026-10-23)。
	/// </summary>
	private static int? CountFaceEdgeSum(List<FaceRow> rows)
	{
		int sum = 0; bool any = false;
		foreach (FaceRow r in rows)
		{
			if (r.Edges > 0) { sum += r.Edges; any = true; }
		}
		return any ? (int?)sum : null;
	}

	// ---------------- 特征树 ----------------

	private static List<object> ReadFeatures(object models, int modelCount, List<string> warnings)
	{
		var list = new List<object>();
		for (int mi = 1; mi <= modelCount; mi++)
		{
			object model = Item(models, mi);
			object feats = Get(model, "Features");
			if (feats == null) continue;
			int n = Count(feats);
			for (int i = 1; i <= n; i++)
			{
				object ft = Item(feats, i);
				if (ft == null) continue;
				long? status = SafeNullableLong(Get(ft, "Status"));
				string health = FeatureHealthName(status);
				list.Add(new
				{
					model = mi,
					index = i,
					name = Convert.ToString(Get(ft, "Name"), CultureInfo.InvariantCulture),
					status,
					featureHealth = health
				});
			}
		}
		if (list.Count == 0) warnings.Add("特征树读不到（Models[i].Features 为空或不可访问）。");
		return list;
	}

	/// <summary>与 ModelingTools.ClassifyFeatureStatus 同一套枚举值（官方 FeatureStatusConstants 五档）。</summary>
	private static string FeatureHealthName(long? status)
	{
		if (!status.HasValue) return "unknown";
		switch (status.Value)
		{
			case 1216476310: return "ok";          // igFeatureOK
			case 1216476311: return "zombie";      // igFeatureFailed（几何未生成）
			case 1216476312: return "warned";      // igFeatureWarned
			case 1216476313: return "suppressed";  // igFeatureSuppressed
			case 1216476314: return "rolledBack";  // igFeatureRolledBack
			default: return "unknown";
		}
	}

	// ---------------- 几何小工具 ----------------

	/// <summary>
	/// 模型包围盒：走 <c>SolidEdgeGeometry.Body.GetRange(ref Array, ref Array)</c>。
	/// 实测 Model 上没有 RangeBox 属性（IDispatch 取不到），必须从 Body 走（2026-10-23 首测返回 null 踩到）。
	/// </summary>
	internal static double[] TryBodyRange(object body)
	{
		try
		{
			var b = (SolidEdgeGeometry.Body)body;
			Array lo = Array.CreateInstance(typeof(double), 0);
			Array hi = Array.CreateInstance(typeof(double), 0);
			b.GetRange(ref lo, ref hi);
			if (lo == null || hi == null || lo.Length < 3 || hi.Length < 3) return null;
			var d = new double[6];
			for (int i = 0; i < 3; i++)
			{
				d[i] = Convert.ToDouble(lo.GetValue(i), CultureInfo.InvariantCulture);
				d[i + 3] = Convert.ToDouble(hi.GetValue(i), CultureInfo.InvariantCulture);
			}
			return d;
		}
		catch
		{
			return null;
		}
	}

	private static bool TryGetFaceRange(object face, out double[] lo, out double[] hi)
	{
		lo = null; hi = null;
		try
		{
			var f = (SolidEdgeGeometry.Face)face;
			Array a = Array.CreateInstance(typeof(double), 0);
			Array b = Array.CreateInstance(typeof(double), 0);
			f.GetRange(ref a, ref b);
			if (a == null || b == null || a.Length < 3 || b.Length < 3) return false;
			lo = new double[3]; hi = new double[3];
			for (int i = 0; i < 3; i++)
			{
				lo[i] = Convert.ToDouble(a.GetValue(i), CultureInfo.InvariantCulture);
				hi[i] = Convert.ToDouble(b.GetValue(i), CultureInfo.InvariantCulture);
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static int LongestAxis(FaceRow r)
	{
		int a = 0;
		for (int k = 1; k < 3; k++) if (r.AxialLengthMm(k) > r.AxialLengthMm(a)) a = k;
		return a;
	}

	private static double[] Center(FaceRow r)
	{
		return new[]
		{
			R3((r.Lo[0] + r.Hi[0]) / 2.0 * MmPerMeter),
			R3((r.Lo[1] + r.Hi[1]) / 2.0 * MmPerMeter),
			R3((r.Lo[2] + r.Hi[2]) / 2.0 * MmPerMeter)
		};
	}

	/// <summary>横截面圆心：轴向那个坐标取范围中点，横向两轴也取中点（完整圆柱=真实圆心；被切圆柱=近似）。</summary>
	private static double[] CenterOnPlane(FaceRow r, int axis)
	{
		var c = Center(r);
		c[axis] = R3((r.Lo[axis] + r.Hi[axis]) / 2.0 * MmPerMeter);
		return c;
	}

	private static double[] ToMm(double[] box)
	{
		if (box == null) return null;
		var d = new double[box.Length];
		for (int i = 0; i < box.Length; i++) d[i] = R3(box[i] * MmPerMeter);
		return d;
	}

	/// <summary>圆整到 0.001mm，并把 -0 归一成 0（JSON 里的 "-0" 会让人以为是负零/特殊值）。</summary>
	private static double R3(double v)
	{
		double r = Math.Round(v, 3);
		return (r == 0) ? 0.0 : r;
	}

	// ---------------- COM 读取底层（全部失败即 null，不抛） ----------------

	/// <summary>
	/// 统一读取入口：优先走 ManualInvoke 的 IDispatch 通道（本项目既有的唯一可靠通道），
	/// 失败再用反射兜底。
	/// ⚠️ 反射的 InvokeMember("Item", GetProperty, args) 对 COM 参数化属性拿不到值
	///    ——实测集合 Item(i) 全部返回 null（2026-10-23 首轮自测踩到），所以 ManualInvoke 必须优先。
	/// </summary>
	internal static object Get(object obj, string member, params object[] args)
	{
		if (obj == null) return null;
		object v;
		Exception err;
		if (ManualInvoke.TryInvoke(obj, member, (args == null || args.Length == 0) ? null : args, out v, out err))
		{
			return v;
		}
		try
		{
			return obj.GetType().InvokeMember(member, BindingFlags.GetProperty, null, obj, args);
		}
		catch
		{
			return null;
		}
	}

	internal static object Item(object collection, int index1Based)
	{
		if (collection == null) return null;
		return Get(collection, "Item", index1Based);
	}

	internal static int Count(object collection)
	{
		if (collection == null) return 0;
		object c = Get(collection, "Count");
		if (c == null) return 0;
		try { return Convert.ToInt32(c, CultureInfo.InvariantCulture); }
		catch { return 0; }
	}

	private static int SafeCount(object obj, string member)
	{
		object col = Get(obj, member);
		if (col == null) return -1;
		object c = Get(col, "Count");
		if (c == null) return -1;
		try { return Convert.ToInt32(c, CultureInfo.InvariantCulture); }
		catch { return -1; }
	}

	private static int SafeInt(object v)
	{
		if (v == null) return -1;
		try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
		catch { return -1; }
	}

	private static int? SafeNullableInt(object v)
	{
		if (v == null) return null;
		try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
		catch { return null; }
	}

	private static long? SafeNullableLong(object v)
	{
		if (v == null) return null;
		try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
		catch { return null; }
	}

	private static bool? SafeBool(object v)
	{
		if (v == null) return null;
		try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
		catch { return null; }
	}

	private static double? TryDouble(object obj, string member)
	{
		object v = Get(obj, member);
		if (v == null) return null;
		try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
		catch { return null; }
	}

	/// <summary>几何对象上读 double 属性（Cylinder.Radius / Cone.HalfAngle / Torus.MajorRadius 等），读不到返回 null。</summary>
	private static double? TryTypedDouble(object typedObj, string prop)
	{
		if (typedObj == null) return null;
		object v = Get(typedObj, prop);
		if (v == null) return null;
		try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
		catch { return null; }
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new { status = "error", message });
	}
}
