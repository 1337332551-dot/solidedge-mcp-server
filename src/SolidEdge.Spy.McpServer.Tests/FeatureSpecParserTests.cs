using System;
using System.Linq;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// FeatureSpecParser(纯函数,不碰 COM)。
    /// 断言全部依据 FeatureSpec.cs 源码逐条核实的行为,不猜。
    /// </summary>
    public class FeatureSpecParserTests
    {
        private static FeatureSpec Parse(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                return FeatureSpecParser.Parse(doc.RootElement, 0);
            }
        }

        [Fact]
        public void Parse_MinimalExtrude_DefaultsAndRectExpanded()
        {
            var s = Parse(@"{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}");
            Assert.Equal("extrude", s.Op);
            Assert.Equal("RefPlane_1", s.PlaneRef);
            Assert.Equal(0.05, s.Depth.Value, 12);
            Assert.Null(s.Side);                 // 未给 → null,由 op 套默认值
            Assert.Equal("rect", s.ShapeSource);
            Assert.Null(s.ShapeError);
            Assert.Empty(s.UnknownFields);
            Assert.Single(s.Loops);
            Assert.Equal(4, s.Loops[0].Length);   // rect 两角点 → 归一化 4 角点
            Assert.Equal(-0.1, s.Loops[0][0][0], 12);
            Assert.Equal(-0.1, s.Loops[0][0][1], 12);
            Assert.Equal(0.1, s.Loops[0][2][0], 12);
            Assert.Equal(0.1, s.Loops[0][2][1], 12);
        }

        [Fact]
        public void Parse_RectAnyCornerOrder_Normalized()
        {
            // 两角点乱序给出(右下、左上)也归一化成同一 4 角点
            var s = Parse(@"{""rect"":[[0.1,0.1],[-0.1,-0.1]]}");
            Assert.Equal(-0.1, s.Loops[0][0][0], 12);
            Assert.Equal(-0.1, s.Loops[0][0][1], 12);
            Assert.Equal(0.1, s.Loops[0][2][0], 12);
            Assert.Equal(0.1, s.Loops[0][2][1], 12);
        }

        [Fact]
        public void Parse_OpMissing_FallsBackToExtrude()
        {
            var s = Parse(@"{""rect"":[[-0.1,-0.1],[0.1,0.1]]}");
            Assert.Equal("extrude", s.Op);
            Assert.Equal("extrude", s.OpLower);
        }

        [Fact]
        public void Parse_OpNonString_FallsBackToExtrude()
        {
            var s = Parse(@"{""op"":5,""rect"":[[-0.1,-0.1],[0.1,0.1]]}");
            Assert.Equal("extrude", s.Op);
        }

        [Fact]
        public void Parse_CircleObjectForm()
        {
            // 合流后行为:本地线已修 object 形式 center(TryGetCenter 支持 [x,y]),
            // 远端旧断言"静默回退原点"已被取代(见本地测试 center嵌套写法_仍被接受)。
            var s = Parse(@"{""circle"":{""center"":[0.01,0.02],""radius"":0.005}}");
            Assert.True(s.HasCircle);
            Assert.Equal("circle", s.ShapeSource);
            Assert.Equal(0.01, s.CircleX, 12);
            Assert.Equal(0.02, s.CircleY, 12);
            Assert.Equal(0.005, s.CircleR, 12);
            Assert.Empty(s.Loops);
        }

        [Fact]
        public void Parse_CircleObjectCenter_NeedsTwoPoints()
        {
            // 合流后行为:嵌套 [[x,y]] 单点即被 TryGetCenter 接受,不再要求 ≥2 点。
            var one = Parse(@"{""circle"":{""center"":[[0.01,0.02]],""radius"":0.005}}");
            Assert.True(one.HasCircle);
            Assert.Equal(0.01, one.CircleX, 12);
            Assert.Equal(0.02, one.CircleY, 12);
            Assert.Equal(0.005, one.CircleR, 12);
        }

        [Fact]
        public void Parse_CircleArrayForm()
        {
            var s = Parse(@"{""circle"":[0,0.01,0.02]}");
            Assert.True(s.HasCircle);
            Assert.Equal(0.02, s.CircleR, 12);
        }

        [Fact]
        public void Parse_CircleNonPositiveRadius_FallsBackToLoops()
        {
            // radius <= 0 视为"没有圆",回退走直线环(历史行为,FeatureSpec.cs 注释明说)
            var s = Parse(@"{""circle"":[0,0,0],""rect"":[[-0.01,-0.01],[0.01,0.01]]}");
            Assert.False(s.HasCircle);
            Assert.Equal("rect", s.ShapeSource);
            Assert.Single(s.Loops);
        }

        [Fact]
        public void Parse_NoShapeAtAll_RecordsShapeError()
        {
            var s = Parse(@"{""op"":""extrude""}");
            Assert.False(s.HasCircle);
            Assert.NotNull(s.ShapeError);
            Assert.Equal("", s.ShapeSource);
            Assert.Empty(s.Loops);
        }

        [Fact]
        public void Parse_CirclesArray_KeepsOnlyPositiveRadius()
        {
            var s = Parse(@"{""circles"":[[0,0,0.01],[0.05,0,0.02],[0.1,0,0]]}");
            Assert.True(s.HasCircles);
            Assert.Equal("circles", s.ShapeSource);
            Assert.Equal(2, s.Circles.Count);     // r=0 的第三项被丢弃
        }

        [Fact]
        public void Parse_SlotObjectAndArrayForms()
        {
            var a = Parse(@"{""slot"":{""center"":[0,0],""length"":0.1,""width"":0.03,""angle"":0.5}}");
            Assert.True(a.HasSlot);
            Assert.Equal(0.1, a.SlotLength, 12);
            Assert.Equal(0.03, a.SlotWidth, 12);
            Assert.Equal(0.5, a.SlotAngle, 12);

            var b = Parse(@"{""slot"":[0,0,0.1,0.03,0.25]}");
            Assert.True(b.HasSlot);
            Assert.Equal(0.25, b.SlotAngle, 12);
        }

        [Fact]
        public void Parse_SlotObjectCenter_SameGetPointsPitfall()
        {
            // 合流后行为:slot 对象写法 center 同样走 TryGetCenter,[x,y] 直接生效,
            // 远端旧断言"静默归零/塞 2 点才取到"已被本地修复取代。
            var one = Parse(@"{""slot"":{""center"":[0.01,0.02],""length"":0.1,""width"":0.03}}");
            Assert.True(one.HasSlot);
            Assert.Equal(0.01, one.SlotX, 12);
            Assert.Equal(0.02, one.SlotY, 12);
            Assert.Equal(0.1, one.SlotLength, 12);
            Assert.Equal(0.03, one.SlotWidth, 12);
        }

        [Fact]
        public void Parse_AxisNestedAndFlatForms()
        {
            var nested = Parse(@"{""axis"":[[0,0],[0,0.05]]}");
            Assert.True(nested.HasAxis);
            Assert.Equal(0.05, nested.AxisP2[1], 12);

            var flat = Parse(@"{""axis"":[0,0,0,0.05]}");
            Assert.True(flat.HasAxis);
            Assert.Equal(0.05, flat.AxisP2[1], 12);
        }

        [Fact]
        public void Parse_AxisCoincidentPoints_Rejected()
        {
            // 两点重合(退化成点)→ 不算有效轴
            var s = Parse(@"{""axis"":[[0,0],[0,0]]}");
            Assert.False(s.HasAxis);
        }

        [Fact]
        public void Parse_DegreesAndAngle_BothRead()
        {
            var s = Parse(@"{""angle"":1.5,""degrees"":90}");
            Assert.Equal(1.5, s.Angle.Value, 12);
            Assert.Equal(90, s.Degrees.Value, 12);
        }

        [Fact]
        public void Parse_DimsElementNonInteger_RecordsParseError()
        {
            // element 写成 1.5:解析器降级为 ParseError(2026-09-14 修复,不再抛异常带崩校验)
            var s = Parse(@"{""rect"":[[-0.1,-0.1],[0.1,0.1]],""dims"":[{""element"":1.5,""name"":""w"",""value"":""40 mm""}]}");
            Assert.Single(s.Dims);
            Assert.NotNull(s.Dims[0].ParseError);
            Assert.Contains("整数", s.Dims[0].ParseError);
        }

        [Fact]
        public void Parse_DimsMissingName_RecordsParseError()
        {
            var s = Parse(@"{""rect"":[[-0.1,-0.1],[0.1,0.1]],""dims"":[{""element"":0,""value"":""40 mm""}]}");
            Assert.Contains("name", s.Dims[0].ParseError);
        }

        [Fact]
        public void Parse_DimsMissingValueAndFormula_RecordsParseError()
        {
            var s = Parse(@"{""rect"":[[-0.1,-0.1],[0.1,0.1]],""dims"":[{""element"":0,""name"":""w""}]}");
            Assert.Contains("value/formula", s.Dims[0].ParseError);
        }

        [Fact]
        public void Parse_DimsValid_NoParseError()
        {
            var s = Parse(@"{""rect"":[[-0.1,-0.1],[0.1,0.1]],""dims"":[{""element"":0,""name"":""w"",""value"":""40 mm""}]}");
            Assert.Null(s.Dims[0].ParseError);
            Assert.Equal("w", s.Dims[0].Name);
        }

        [Fact]
        public void Parse_UnknownField_Collected()
        {
            var s = Parse(@"{""op"":""extrude"",""depht"":0.05,""rect"":[[-0.1,-0.1],[0.1,0.1]]}");
            Assert.Contains("depht", s.UnknownFields);   // 拼错字段进白名单外名单
        }

        [Fact]
        public void Parse_BoolFields_CaseInsensitiveAndStringForms()
        {
            // TryGetBoolCI:驼峰键也能读;"true" 字符串可解析
            var s = Parse(@"{""AutoConstraint"":""true"",""FixOrigin"":true}");
            Assert.True(s.AutoConstraint.Value);
            Assert.True(s.FixOrigin.Value);
        }

        [Fact]
        public void Parse_NumericFields_AcceptStringNumbers()
        {
            var s = Parse(@"{""depth"":""0.05"",""side"":""2""}");
            Assert.Equal(0.05, s.Depth.Value, 12);
            Assert.Equal(2, s.Side.Value);
        }

        [Fact]
        public void Parse_LoopsTooFewPoints_DroppedThenFallback()
        {
            // 2 点环被静默丢弃 → 无 loops → 回退 rect/polygon → 都没有则记 ShapeError
            var s = Parse(@"{""loops"":[[[0,0],[1,1]]]}");
            Assert.NotNull(s.ShapeError);
            Assert.Empty(s.Loops);
        }

        [Fact]
        public void Parse_LoopsMultiple_Valid()
        {
            var s = Parse(@"{""loops"":[[[0,0],[1,0],[0,1]],[[2,0],[3,0],[2,1]]]}");
            Assert.Equal(2, s.Loops.Count);
            Assert.Equal("loops", s.ShapeSource);
        }

        [Fact]
        public void Parse_PolygonThreePoints_Valid()
        {
            var s = Parse(@"{""polygon"":[[-0.15,0.2],[0.15,0.2],[0,0.3]]}");
            Assert.Single(s.Loops);
            Assert.Equal(3, s.Loops[0].Length);
            Assert.Equal("polygon", s.ShapeSource);
        }

        // ---------- hole 便捷写法(2026-09-23 P1) ----------

        [Fact]
        public void Parse_HoleCenterDiameter_SynthesizesCircle()
        {
            // center+diameter(米) → 合成单圆轮廓,r = diameter/2
            var s = Parse(@"{""op"":""hole"",""center"":[0.02,0.02],""diameter"":0.012}");
            Assert.True(s.HasCircle);
            Assert.Equal("circle", s.ShapeSource);
            Assert.Equal(0.02, s.CircleX, 12);
            Assert.Equal(0.02, s.CircleY, 12);
            Assert.Equal(0.006, s.CircleR, 12);   // 直径 12mm → 半径 6mm
            Assert.Empty(s.Loops);
        }

        [Fact]
        public void Parse_HoleDiameterOnly_CenterDefaultsOrigin()
        {
            var s = Parse(@"{""op"":""hole"",""diameter"":0.02}");
            Assert.True(s.HasCircle);
            Assert.Equal(0, s.CircleX, 12);
            Assert.Equal(0, s.CircleY, 12);
            Assert.Equal(0.01, s.CircleR, 12);
        }

        [Fact]
        public void Parse_HoleCenterInvalid_FallsBackOrigin()
        {
            // center 非法(TryGetPoint2 返回 null)→ 圆心静默取 (0,0),不抛异常
            var s = Parse(@"{""op"":""hole"",""center"":""oops"",""diameter"":0.01}");
            Assert.Null(s.Center);
            Assert.True(s.HasCircle);
            Assert.Equal(0, s.CircleX, 12);
            Assert.Equal(0, s.CircleY, 12);
            Assert.Equal(0.005, s.CircleR, 12);
        }

        [Fact]
        public void Parse_HoleCircleTakesPrecedenceOverDiameter()
        {
            // 合成链:circle → circles → slot → hole(diameter)。显式 circle 在前,diameter 被忽略
            var s = Parse(@"{""op"":""hole"",""circle"":[0.05,0,0.008],""diameter"":0.012}");
            Assert.True(s.HasCircle);
            Assert.Equal(0.008, s.CircleR, 12);   // circle 的半径,不是 diameter/2
            Assert.Equal(0.05, s.CircleX, 12);
        }

        [Fact]
        public void Parse_HoleMethodAliasForMode()
        {
            // method 是 mode 的别名
            var s = Parse(@"{""op"":""hole"",""circle"":[0,0,0.005],""method"":""finite""}");
            Assert.Equal("finite", s.Mode);
        }

        [Fact]
        public void Parse_HoleModeOverridesMethod()
        {
            // 同时给 mode 与 method:mode 优先(??" 链顺序)
            var s = Parse(@"{""op"":""hole"",""circle"":[0,0,0.005],""mode"":""next"",""method"":""finite""}");
            Assert.Equal("next", s.Mode);
        }

        [Fact]
        public void Parse_HoleNewFields_AreKnown()
        {
            // diameter/center/method 进入字段白名单,不得当未知字段报警(W102)
            var s = Parse(@"{""op"":""hole"",""center"":[0.01,0.01],""diameter"":0.01,""method"":""all""}");
            Assert.DoesNotContain("diameter", s.UnknownFields);
            Assert.DoesNotContain("center", s.UnknownFields);
            Assert.DoesNotContain("method", s.UnknownFields);
        }

        [Fact]
        public void Parse_HoleNegativeDiameter_NoSynthesis()
        {
            // diameter<=0 不合成圆(由校验器 E409 报错,解析器不替它做决定)
            var s = Parse(@"{""op"":""hole"",""diameter"":-0.01}");
            Assert.False(s.HasCircle);
            Assert.NotNull(s.ShapeError);
        }

        // ---------- P2 多轮廓(2026-09-23):profiles / origin / helix 三要素 / sweep 开放链 ----------

        [Fact]
        public void Parse_Profiles_EachItemRecursive()
        {
            // profiles 每项是"迷你特征":plane/形状字段全走同一套解析
            var s = Parse(@"{""op"":""loft"",""profiles"":[" +
                         @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}," +
                         @"{""plane"":""@top"",""rect"":[[-0.01,-0.01],[0.01,0.01]]}]}");
            Assert.True(s.HasProfiles);
            Assert.Null(s.ProfilesError);
            Assert.Equal(2, s.Profiles.Count);
            Assert.Equal("RefPlane_1", s.Profiles[0].PlaneRef);
            Assert.True(s.Profiles[0].HasCircle);
            Assert.Equal(0.03, s.Profiles[0].CircleR, 12);
            Assert.Equal("@top", s.Profiles[1].PlaneRef);
            Assert.Equal("rect", s.Profiles[1].ShapeSource);
            Assert.Single(s.Profiles[1].Loops);          // rect → 4 角点闭合环
            Assert.Equal(4, s.Profiles[1].Loops[0].Length);
        }

        [Theory]
        [InlineData(@"{""op"":""loft"",""profiles"":[]}")]
        [InlineData(@"{""op"":""loft"",""profiles"":{}}")]
        [InlineData(@"{""op"":""sweep"",""profiles"":""RefPlane_1""}")]
        public void Parse_ProfilesBadForm_RecordsError(string json)
        {
            // 空数组 / 非数组 → ProfilesError,HasProfiles=false(合法性裁决定格在校验器 E410/E411)
            var s = Parse(json);
            Assert.False(s.HasProfiles);
            Assert.NotNull(s.ProfilesError);
        }

        [Fact]
        public void Parse_ProfilesMissing_AllClear()
        {
            var s = Parse(@"{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}");
            Assert.False(s.HasProfiles);
            Assert.Null(s.ProfilesError);
            Assert.Empty(s.Profiles);
        }

        [Fact]
        public void Parse_SweepFirstPolygon_BecomesOpenChain()
        {
            // sweep 首项 polygon → 开放链:Loops 清空、ShapeError 清空、ShapeSource 改标
            var s = Parse(@"{""op"":""sweep"",""profiles"":[" +
                         @"{""plane"":""RefPlane_3"",""polygon"":[[0,0],[0.02,0],[0.05,0.05]]}," +
                         @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.005]}]}");
            var path = s.Profiles[0];
            Assert.NotNull(path.OpenChain);
            Assert.Equal(3, path.OpenChain.Length);
            Assert.Empty(path.Loops);
            Assert.Null(path.ShapeError);
            Assert.Equal("polygon(开放链)", path.ShapeSource);
            Assert.Equal(0.05, path.OpenChain[2][0], 12);
        }

        [Fact]
        public void Parse_SweepFirstPolygon_TwoPoints_MinimalOpenChain()
        {
            // 2 点折线(直线)也是合法路径
            var s = Parse(@"{""op"":""sweep"",""profiles"":[" +
                         @"{""plane"":""RefPlane_3"",""polygon"":[[0,0],[0.05,0.05]]}]}");
            var path = s.Profiles[0];
            Assert.NotNull(path.OpenChain);
            Assert.Equal(2, path.OpenChain.Length);
        }

        [Fact]
        public void Parse_SweepFirstPolygon_OnePoint_StaysError()
        {
            // 1 点不成链:保持闭合解析的 ShapeError,交 E411 报
            var s = Parse(@"{""op"":""sweep"",""profiles"":[" +
                         @"{""plane"":""RefPlane_3"",""polygon"":[[0,0]]}]}");
            var path = s.Profiles[0];
            Assert.Null(path.OpenChain);
            Assert.NotNull(path.ShapeError);
        }

        [Fact]
        public void Parse_NonSweepPolygon_StillClosed()
        {
            // 非 sweep(含 loft 截面项,内层 op 缺省 extrude)polygon 恒闭合
            var s = Parse(@"{""op"":""loft"",""profiles"":[" +
                         @"{""plane"":""RefPlane_3"",""polygon"":[[0,0],[0.02,0],[0.05,0.05]]}]}");
            var p = s.Profiles[0];
            Assert.Null(p.OpenChain);
            Assert.Single(p.Loops);
            Assert.Equal("polygon", p.ShapeSource);
        }

        [Fact]
        public void Parse_SweepFirstCircle_NotOpenChain()
        {
            // circle 首项 = 闭合路径(扫一整圈),不转开放链
            var s = Parse(@"{""op"":""sweep"",""profiles"":[" +
                         @"{""plane"":""RefPlane_3"",""circle"":[0,0,0.03]}]}");
            var path = s.Profiles[0];
            Assert.True(path.HasCircle);
            Assert.Null(path.OpenChain);
        }

        [Fact]
        public void Parse_HelixParams_PitchHeightRevolutions()
        {
            var s = Parse(@"{""op"":""helix"",""pitch"":0.008,""height"":0.04,""revolutions"":5}");
            Assert.Equal(0.008, s.Pitch.Value, 12);
            Assert.Equal(0.04, s.Height.Value, 12);
            Assert.Equal(5, s.Revolutions.Value, 12);
        }

        [Fact]
        public void Parse_HelixParams_AcceptStringNumbers()
        {
            var s = Parse(@"{""op"":""helix"",""pitch"":""0.008"",""revolutions"":""5""}");
            Assert.Equal(0.008, s.Pitch.Value, 12);
            Assert.Equal(5, s.Revolutions.Value, 12);
        }

        [Fact]
        public void Parse_Origin_Explicit()
        {
            var s = Parse(@"{""op"":""loft"",""origin"":[0.01,0.02]}");
            Assert.NotNull(s.Origin);
            Assert.Equal(0.01, s.Origin[0], 12);
            Assert.Equal(0.02, s.Origin[1], 12);
        }

        [Fact]
        public void Parse_OriginInvalid_Null()
        {
            var s = Parse(@"{""op"":""loft"",""origin"":""oops""}");
            Assert.Null(s.Origin);
        }

        [Fact]
        public void Parse_P2Fields_AreKnown()
        {
            // profiles/origin/pitch/height/revolutions 进入字段白名单,不当未知字段
            var s = Parse(@"{""op"":""helix"",""origin"":[0,0],""pitch"":0.008,""height"":0.04,""revolutions"":5}");
            Assert.DoesNotContain("origin", s.UnknownFields);
            Assert.DoesNotContain("pitch", s.UnknownFields);
            Assert.DoesNotContain("height", s.UnknownFields);
            Assert.DoesNotContain("revolutions", s.UnknownFields);
            var s2 = Parse(@"{""op"":""loft"",""profiles"":[{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}]," +
                          @"""origin"":[0,0]}");
            Assert.DoesNotContain("profiles", s2.UnknownFields);
            Assert.DoesNotContain("origin", s2.UnknownFields);
        }

        // ---------- P3 面引用机制(2026-09-23):FaceRefSpec 解析(faceOf/faceNormal/faceIndex/confirm/target) ----------

        [Fact]
        public void Parse_NoFaceRef_NullAndNoHasFaceRef()
        {
            // 非 face 引用 op:draft 不给 faceOf → FaceRef=null,HasFaceRef=false
            var s = Parse(@"{""op"":""draft"",""plane"":""RefPlane_1"",""angle"":0.1,""side"":4}");
            Assert.Null(s.FaceRef);
            Assert.False(s.HasFaceRef);
        }

        [Fact]
        public void Parse_FaceOfAlias_HasFaceRef()
        {
            // 给了 faceOf(即使是 obj- 句柄)→ HasFaceRef=true
            var s = Parse(@"{""op"":""draft"",""plane"":""RefPlane_1"",""faceOf"":""@base"",""faceNormal"":[0,0,1],""angle"":0.1,""side"":4}");
            Assert.True(s.HasFaceRef);
            Assert.NotNull(s.FaceRef);
            Assert.Equal("@base", s.FaceRef.FeatureName);
        }

        [Fact]
        public void Parse_FaceNormalParsed()
        {
            var s = Parse(@"{""op"":""draft"",""plane"":""RefPlane_1"",""faceOf"":""@base"",""faceNormal"":[0,0,1],""angle"":0.1,""side"":4}");
            Assert.NotNull(s.FaceRef.Normal);
            Assert.Equal(3, s.FaceRef.Normal.Length);
            Assert.Equal(1.0, s.FaceRef.Normal[2], 12);
            Assert.Null(s.FaceRef.ParseError);
        }

        [Fact]
        public void Parse_FaceIndexParsed()
        {
            var s = Parse(@"{""op"":""delete_face"",""faceOf"":""@base"",""faceIndex"":3,""confirm"":true}");
            Assert.True(s.FaceRef.Index.HasValue);
            Assert.Equal(3, s.FaceRef.Index.Value);
        }

        [Fact]
        public void Parse_FaceNormalNonArray_ParseError()
        {
            // faceNormal 非 3 元数组 → ParseError
            var s = Parse(@"{""op"":""draft"",""plane"":""RefPlane_1"",""faceOf"":""@base"",""faceNormal"":[0,0],""angle"":0.1,""side"":4}");
            Assert.NotNull(s.FaceRef.ParseError);
            Assert.Contains("3 元数组", s.FaceRef.ParseError);
        }

        [Fact]
        public void Parse_FaceNormalZeroVector_ParseError()
        {
            var s = Parse(@"{""op"":""draft"",""plane"":""RefPlane_1"",""faceOf"":""@base"",""faceNormal"":[0,0,0],""angle"":0.1,""side"":4}");
            Assert.Contains("零向量", s.FaceRef.ParseError);
        }

        [Fact]
        public void Parse_FaceNormalNonNumber_ParseError()
        {
            var s = Parse(@"{""op"":""draft"",""plane"":""RefPlane_1"",""faceOf"":""@base"",""faceNormal"":[0,0,""a""],""angle"":0.1,""side"":4}");
            Assert.Contains("数字", s.FaceRef.ParseError);
        }

        [Fact]
        public void Parse_FaceIndexNegative_ParseError()
        {
            var s = Parse(@"{""op"":""delete_face"",""faceOf"":""@base"",""faceIndex"":-1,""confirm"":true}");
            Assert.Contains(">= 0", s.FaceRef.ParseError);
        }

        [Fact]
        public void Parse_FaceIndexFloat_ParseError()
        {
            // faceIndex 必须整数,小数 → ParseError
            var s = Parse(@"{""op"":""delete_face"",""faceOf"":""@base"",""faceIndex"":1.5,""confirm"":true}");
            Assert.Contains("整数", s.FaceRef.ParseError);
        }

        [Fact]
        public void Parse_FaceOfNotString_ParseError()
        {
            var s = Parse(@"{""op"":""draft"",""plane"":""RefPlane_1"",""faceOf"":123,""angle"":0.1,""side"":4}");
            Assert.Contains("非空字符串", s.FaceRef.ParseError);
        }

        [Fact]
        public void Parse_FaceOfEmpty_ParseError()
        {
            var s = Parse(@"{""op"":""draft"",""plane"":""RefPlane_1"",""faceOf"":"""",""angle"":0.1,""side"":4}");
            Assert.Contains("非空字符串", s.FaceRef.ParseError);
        }

        // ---------- plane 面锚定(2026-10-03):face:<ID> / face:±X/±Y/±Z ----------

        [Fact]
        public void Parse_PlaneFaceId_KindId()
        {
            var s = Parse(@"{""op"":""extrude"",""plane"":""face:26"",""rect"":[[-0.02,-0.02],[0.02,0.02]],""depth"":0.005}");
            Assert.NotNull(s.FacePlaneRef);
            Assert.Equal("Id", s.FacePlaneRef.Kind);
            Assert.Equal(26, s.FacePlaneRef.FaceId);
            Assert.Null(s.FacePlaneRef.ParseError);
        }

        [Theory]
        [InlineData("face:+Z", "+Z")]
        [InlineData("face:-X", "-X")]
        [InlineData("face:+y", "+Y")]     // 小写轴符号归一成大写
        public void Parse_PlaneFaceAxis_KindAxis(string plane, string axis)
        {
            var s = Parse(@"{""op"":""extrude"",""plane"":""" + plane + @""",""rect"":[[-0.02,-0.02],[0.02,0.02]],""depth"":0.005}");
            Assert.NotNull(s.FacePlaneRef);
            Assert.Equal("Axis", s.FacePlaneRef.Kind);
            Assert.Equal(axis, s.FacePlaneRef.Axis);
            Assert.Null(s.FacePlaneRef.ParseError);
        }

        [Theory]
        [InlineData("face:abc", "±X/±Y/±Z")]
        [InlineData("face:", "缺少内容")]
        [InlineData("face:-1", ">= 0")]
        [InlineData("face:+W", "±X/±Y/±Z")]
        public void Parse_PlaneFaceBad_ParseError(string plane, string fragment)
        {
            var s = Parse(@"{""op"":""extrude"",""plane"":""" + plane + @""",""rect"":[[-0.02,-0.02],[0.02,0.02]],""depth"":0.005}");
            Assert.NotNull(s.FacePlaneRef);
            Assert.NotNull(s.FacePlaneRef.ParseError);
            Assert.Contains(fragment, s.FacePlaneRef.ParseError);
        }

        [Fact]
        public void Parse_PlaneNonFace_FacePlaneRefNull()
        {
            var s = Parse(@"{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}");
            Assert.Null(s.FacePlaneRef);
        }

        [Fact]
        public void Parse_ConfirmParsed()
        {
            var s = Parse(@"{""op"":""delete_face"",""faceOf"":""@base"",""faceIndex"":2,""confirm"":true}");
            Assert.True(s.Confirm.HasValue);
            Assert.True(s.Confirm.Value);
        }

        [Fact]
        public void Parse_ConfirmDefaultNull()
        {
            // confirm 默认 null(op 用 !=true 拒绝)
            var s = Parse(@"{""op"":""delete_face"",""faceOf"":""@base"",""faceIndex"":2}");
            Assert.False(s.Confirm.HasValue);
        }

        [Fact]
        public void Parse_TargetParsed()
        {
            var s = Parse(@"{""op"":""split"",""plane"":""RefPlane_2"",""target"":""@base""}");
            Assert.Equal("@base", s.Target);
        }

        [Fact]
        public void Parse_FaceRefFields_KnownNotUnknown()
        {
            // faceOf/faceNormal/faceIndex/confirm/target 进字段白名单 → 不当未知字段
            var s = Parse(@"{""op"":""delete_face"",""faceOf"":""@base"",""faceNormal"":[0,0,1],""faceIndex"":1,""confirm"":true,""target"":""@b""}");
            Assert.DoesNotContain("faceOf", s.UnknownFields);
            Assert.DoesNotContain("faceNormal", s.UnknownFields);
            Assert.DoesNotContain("faceIndex", s.UnknownFields);
            Assert.DoesNotContain("confirm", s.UnknownFields);
            Assert.DoesNotContain("target", s.UnknownFields);
        }
    }
}
