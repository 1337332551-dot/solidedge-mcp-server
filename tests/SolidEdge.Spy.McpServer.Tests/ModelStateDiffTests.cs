using System.Collections.Generic;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
	/// <summary>
	/// 拓扑 diff 纯函数回归测试（2026-10-23 P0-2/P1 新增）。
	///
	/// Compare 是 se_model_state_snapshot(diff) 与 se_model_build(topologyDiff) 共用的对比内核，
	/// 抽成纯函数后必须用用例锁住语义，否则两处数字会悄悄分叉。
	/// 锁的行为：
	/// 1) 面集合/面积/特征全一致 → identical=true（幂等判据，不能被浮点噪声误伤——面积已在写入侧 Round(3)）；
	/// 2) 加凸台场景：added=5、changed=1（顶面面积变化超阈值）、volumeDelta 精确；
	/// 3) 建前空文档（首次建模路径）：added=全部面、volumeDeltaMm3=建后总体积；
	/// 4) 面积变化低于容差不算 changed；
	/// 5) 特征状态变化进 featuresChanged，identical=false；
	/// 6) IsSameDocument：路径优先、路径空时退回文档名——跨文档 diff 拦截（comparable）的判据。
	/// </summary>
	public class ModelStateDiffTests
	{
		private static ModelStateSnapshotTools.TopoFaceSnap Face(int id, double? areaMm2 = null, string type = "Plane", string where = null)
		{
			return new ModelStateSnapshotTools.TopoFaceSnap { id = id, areaMm2 = areaMm2, type = type, where = where ?? ("plane +Z@10mm") };
		}

		private static ModelStateSnapshotTools.TopoModelSnap Model(int index, int faceCount, double? volumeMm3, params ModelStateSnapshotTools.TopoFaceSnap[] faces)
		{
			var m = new ModelStateSnapshotTools.TopoModelSnap { index = index, faceCount = faceCount, volumeMm3 = volumeMm3 };
			m.faces.AddRange(faces);
			return m;
		}

		private static ModelStateSnapshotTools.TopoSnap Snap(params ModelStateSnapshotTools.TopoModelSnap[] models)
		{
			var s = new ModelStateSnapshotTools.TopoSnap { document = "a.par", documentPath = @"C:\a.par" };
			s.models.AddRange(models);
			return s;
		}

		[Fact]
		public void Compare_IdenticalSnaps_IdenticalTrue()
		{
			ModelStateSnapshotTools.TopoSnap a = Snap(Model(1, 6, 60000, Face(1, 100), Face(2, 200)));
			ModelStateSnapshotTools.TopoSnap b = Snap(Model(1, 6, 60000, Face(1, 100), Face(2, 200)));

			var d = ModelStateSnapshotTools.Compare(a, b, 1.0, 50);

			Assert.True(d.identical);
			Assert.Equal(0, d.totalAdded);
			Assert.Equal(0, d.totalRemoved);
			Assert.Equal(0, d.totalChanged);
			Assert.Equal(0, d.volumeDeltaMm3);
		}

		[Fact]
		public void Compare_AddBossFace_Added5Changed1()
		{
			// 交接文档 §2.2 实测场景：方块 6 面 → 加凸台，added=5 / removed=0 / changed=1 / volume +16000
			ModelStateSnapshotTools.TopoSnap before = Snap(Model(1, 6, 60000, Face(1, 10000), Face(2, 10000)));
			ModelStateSnapshotTools.TopoSnap after = Snap(Model(1, 11, 76000, Face(1, 8400), Face(2, 10000),
				Face(10, 1600), Face(11, 400), Face(12, 400), Face(13, 400), Face(14, 400)));

			var d = ModelStateSnapshotTools.Compare(before, after, 1.0, 50);

			Assert.False(d.identical);
			Assert.Equal(5, d.totalAdded);
			Assert.Equal(0, d.totalRemoved);
			Assert.Equal(1, d.totalChanged);   // 面 1: 10000 → 8400（-16%，超 1% 阈值）
			Assert.Equal(16000, d.volumeDeltaMm3);
			Assert.Equal(6, d.facesBefore);
			Assert.Equal(11, d.facesAfter);
		}

		[Fact]
		public void Compare_AreaChangeBelowTolerance_NotChanged()
		{
			ModelStateSnapshotTools.TopoSnap a = Snap(Model(1, 1, 100, Face(1, 10000)));
			ModelStateSnapshotTools.TopoSnap b = Snap(Model(1, 1, 100, Face(1, 10050)));   // +0.5% < 1% 容差

			var d = ModelStateSnapshotTools.Compare(a, b, 1.0, 50);

			Assert.True(d.identical);
			Assert.Equal(0, d.totalChanged);
		}

		[Fact]
		public void Compare_EmptyBefore_AllFacesAdded_VolumeIsTotal()
		{
			// 首次建模路径：建前空文档（Models 为空），建后 8 面
			ModelStateSnapshotTools.TopoSnap before = new ModelStateSnapshotTools.TopoSnap { document = "零件1.par", documentPath = null };
			ModelStateSnapshotTools.TopoSnap after = Snap(Model(1, 8, 50000,
				Face(1), Face(2), Face(3), Face(4), Face(5), Face(6), Face(7), Face(8)));

			var d = ModelStateSnapshotTools.Compare(before, after, 1.0, 50);

			Assert.False(d.identical);
			Assert.Equal(8, d.totalAdded);
			Assert.Equal(0, d.totalRemoved);
			Assert.Equal(50000, d.volumeDeltaMm3);   // 建前为空 → 增量=总体积
		}

		[Fact]
		public void Compare_ModelDisappeared_AllFacesRemoved()
		{
			ModelStateSnapshotTools.TopoSnap before = Snap(Model(1, 6, 60000, Face(1), Face(2)));
			ModelStateSnapshotTools.TopoSnap after = new ModelStateSnapshotTools.TopoSnap { document = "a.par", documentPath = @"C:\a.par" };

			var d = ModelStateSnapshotTools.Compare(before, after, 1.0, 50);

			Assert.Equal(0, d.totalAdded);
			Assert.Equal(6, d.totalRemoved);
			Assert.Equal(-60000, d.volumeDeltaMm3);
		}

		[Fact]
		public void Compare_FeatureStatusChanged_FeaturesChanged()
		{
			ModelStateSnapshotTools.TopoSnap a = Snap(Model(1, 1, 100, Face(1, 100)));
			ModelStateSnapshotTools.TopoSnap b = Snap(Model(1, 1, 100, Face(1, 100)));
			a.features.Add(new ModelStateSnapshotTools.TopoFeatureSnap { model = 1, name = "拉伸 1", status = 1216476310 });
			b.features.Add(new ModelStateSnapshotTools.TopoFeatureSnap { model = 1, name = "拉伸 1", status = 1216476312 });

			var d = ModelStateSnapshotTools.Compare(a, b, 1.0, 50);

			Assert.False(d.identical);
			Assert.Equal(0, d.totalChanged);
			Assert.Equal(1, d.featuresChanged.Count);
		}

		[Fact]
		public void Compare_FaceTypeChanged_CountsAsChanged()
		{
			ModelStateSnapshotTools.TopoSnap a = Snap(Model(1, 1, 100, Face(1, 100, "Plane")));
			ModelStateSnapshotTools.TopoSnap b = Snap(Model(1, 1, 100, Face(1, 100, "Cylinder", "cyl Z Ø20mm hole")));

			var d = ModelStateSnapshotTools.Compare(a, b, 1.0, 50);

			Assert.Equal(1, d.totalChanged);
			Assert.False(d.identical);
		}

		[Fact]
		public void IsSameDocument_SamePath_True()
		{
			ModelStateSnapshotTools.TopoSnap a = new ModelStateSnapshotTools.TopoSnap { document = "a.par", documentPath = @"C:\DIR\a.par" };
			ModelStateSnapshotTools.TopoSnap b = new ModelStateSnapshotTools.TopoSnap { document = "a.par", documentPath = @"c:\dir\A.PAR" };
			Assert.True(ModelStateSnapshotTools.IsSameDocument(a, b));
		}

		[Fact]
		public void IsSameDocument_DifferentPath_False()
		{
			// 交接文档 §5.2 实测事故：锥管快照拿到新件上 diff，静默 removed=116
			ModelStateSnapshotTools.TopoSnap a = new ModelStateSnapshotTools.TopoSnap { document = "锥管.par", documentPath = @"C:\锥管.par" };
			ModelStateSnapshotTools.TopoSnap b = new ModelStateSnapshotTools.TopoSnap { document = "零件9.par", documentPath = null };
			Assert.False(ModelStateSnapshotTools.IsSameDocument(a, b));
		}

		[Fact]
		public void IsSameDocument_BothUnsaved_FallBackToName()
		{
			ModelStateSnapshotTools.TopoSnap a = new ModelStateSnapshotTools.TopoSnap { document = "零件1.par", documentPath = null };
			ModelStateSnapshotTools.TopoSnap b = new ModelStateSnapshotTools.TopoSnap { document = "零件1.par", documentPath = null };
			Assert.True(ModelStateSnapshotTools.IsSameDocument(a, b));

			ModelStateSnapshotTools.TopoSnap c = new ModelStateSnapshotTools.TopoSnap { document = "零件2.par", documentPath = null };
			Assert.False(ModelStateSnapshotTools.IsSameDocument(a, c));
		}
	}
}
