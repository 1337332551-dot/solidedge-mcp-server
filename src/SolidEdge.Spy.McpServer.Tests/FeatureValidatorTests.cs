using System;
using System.Linq;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// FeatureValidator(纯函数,不碰 COM)。断言基于 FeatureRules.cs 的真实规则编号:
    /// E101 未知op / E102 缺形状 / E103 字段类型 / E104 非有限数 / E105 side 非法 /
    /// E201 缺平面 / E202 索引非法 / E203 别名未定义或前向 / E301~E306 几何 /
    /// E401 cut 前无 extrude / E402 depth / E403 revolve 轴 / E404 finite 缺 depth /
    /// W101 量纲 / W102 未知字段 / W202 / W301 绕向 / W401 同面多刀 / W402~W407。
    /// W403(毛坯外除料)依赖 PlaneResolver 的默认面映射——该映射与 2026-09-13 实测
    /// (RP2=YZ/RP3=XZ)相反的嫌疑待仲裁,刻意不测,避免把可疑行为固化成断言。
    /// </summary>
    public class FeatureValidatorTests
    {
        private static ValidationReport V(string json)
        {
            return FeatureValidator.ValidateJson(json);
        }

        private static bool HasCode(ValidationReport r, string code)
        {
            return r.issues.Any(i => i.code == code);
        }

        private const string Wall = @"{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}";

        // ---------- 基线与契约 ----------

        [Fact]
        public void MinimalExtrude_Ok()
        {
            var r = V("[" + Wall + "]");
            Assert.Equal("ok", r.status);
            Assert.Equal(0, r.errorCount);
            Assert.Equal(0, r.warningCount);
            Assert.Equal(1, r.featureCount);
            Assert.Equal(FeatureValidator.Version, r.version);
        }

        [Fact]
        public void EmptyArray_Ok()
        {
            var r = V("[]");
            Assert.Equal("ok", r.status);
            Assert.Equal(0, r.featureCount);
        }

        [Fact]
        public void ValidateJson_ObjectWrapperForm_Equivalent()
        {
            var asArray = V("[" + Wall + "]");
            var asObject = V(@"{""features"":[" + Wall + "]}");
            Assert.Equal(asArray.status, asObject.status);
            Assert.Equal(asArray.errorCount, asObject.errorCount);
            Assert.Equal(asArray.warningCount, asObject.warningCount);
        }

        [Fact]
        public void Validate_Deterministic()
        {
            var bad = @"[{""op"":""foo"",""rect"":[[-0.1,-0.1],[0.1,0.1]]}]";
            Assert.Equal(V(bad).ToJson(), V(bad).ToJson());   // 纯函数:两次结果逐字相等
        }

        [Fact]
        public void ToJson_ChineseNotEscaped()
        {
            var r = V(@"[{""op"":""foo""}]");
            string json = r.ToJson();
            Assert.Contains("未知 op", json);      // UnsafeRelaxedJsonEscaping 契约
            Assert.DoesNotContain("\\u", json);    // 中文不得被转成 \uXXXX
        }

        // ---------- 结构层 ----------

        [Fact]
        public void UnknownOp_E101()
        {
            var r = V(@"[{""op"":""unknown_op"",""rect"":[[-0.1,-0.1],[0.1,0.1]]}]");
            Assert.Equal("error", r.status);
            Assert.True(HasCode(r, "E101"));
        }

        [Fact]
        public void MissingShape_E102()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""depth"":0.05}]");
            Assert.True(HasCode(r, "E102"));
        }

        [Theory]
        [InlineData(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":""abc""}]")]
        [InlineData(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05,""side"":1.5}]")]
        [InlineData(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05,""visible"":""yes""}]")]
        public void BadFieldType_E103(string json)
        {
            Assert.True(HasCode(V(json), "E103"));
        }

        [Fact]
        public void DepthNaN_E104()
        {
            // JSON 无法表达 NaN,但字符串 "NaN" 能被 double.TryParse 解析 → E104 拦截
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":""NaN""}]");
            Assert.True(HasCode(r, "E104"));
        }

        [Fact]
        public void SuspiciousUnits_W101()
        {
            // 坐标 > 100 米 = 疑似 mm 当 m 写
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-200,-200],[200,200]],""depth"":0.05}]");
            Assert.True(HasCode(r, "W101"));
        }

        [Fact]
        public void UnknownField_W102()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05,""depht"":0.05}]");
            Assert.True(HasCode(r, "W102"));
        }

        // ---------- 引用层 ----------

        [Fact]
        public void MissingPlane_E201()
        {
            var r = V(@"[{""op"":""extrude"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}]");
            Assert.True(HasCode(r, "E201"));
        }

        [Fact]
        public void RefPlaneIndexZero_E202()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_0"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}]");
            Assert.True(HasCode(r, "E202"));
        }

        [Fact]
        public void RefPlaneIndexHigh_W202()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_9"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}]");
            Assert.True(HasCode(r, "W202"));
        }

        [Fact]
        public void AliasUndefined_E203()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""@noface"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}]");
            Assert.True(HasCode(r, "E203"));
        }

        [Fact]
        public void AliasForwardReference_E203()
        {
            // 第 0 个特征引用第 1 个特征才定义的 @p → 前向引用
            var r = V(@"[{""op"":""extrude"",""plane"":""@p"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05}," +
                      @"{""op"":""plane"",""name"":""p"",""base"":""RefPlane_1"",""distance"":0.1}]");
            Assert.True(HasCode(r, "E203"));
        }

        // ---------- plane 面锚定(2026-10-03):face:<ID> / face:±X/±Y/±Z ----------

        [Theory]
        [InlineData("extrude")]
        [InlineData("cut")]
        [InlineData("hole")]
        public void PlaneFace_SupportedOps_NoE203(string op)
        {
            // 面锚定本轮只对 extrude/cut/hole 放行(其它 op 由 E203 拦下)
            var r = V(@"[{""op"":""" + op + @""",""plane"":""face:+Z"",""circle"":[0,0,0.01],""depth"":0.005}]");
            Assert.False(HasCode(r, "E203"));
        }

        [Fact]
        public void PlaneFace_IdForm_NoE203()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""face:26"",""rect"":[[-0.02,-0.02],[0.02,0.02]],""depth"":0.005}]");
            Assert.False(HasCode(r, "E203"));
        }

        [Fact]
        public void PlaneFace_RibOp_E203()
        {
            var r = V(@"[{""op"":""rib"",""plane"":""face:+Z"",""polygon"":[[0,0],[0.01,0],[0.01,0.01],[0,0.01]],""thickness"":0.002}]");
            Assert.True(HasCode(r, "E203"));
        }

        [Fact]
        public void PlaneFace_BadSelector_E203()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""face:abc"",""circle"":[0,0,0.01],""depth"":0.005}]");
            Assert.True(HasCode(r, "E203"));
        }

        [Fact]
        public void PlaneFace_WithDir_W409()
        {
            // face 平面法向静态算不出 ⇒ dir 会被忽略,必须记账(warning)
            var r = V(@"[{""op"":""extrude"",""plane"":""face:+Z"",""circle"":[0,0,0.01],""depth"":0.005,""dir"":[0,0,1]}]");
            Assert.True(HasCode(r, "W409"));
        }

        // ---------- 几何层 ----------

        [Fact]
        public void PolygonTooFewPoints_E301()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""polygon"":[[0,0],[1,0]],""depth"":0.05}]");
            Assert.True(HasCode(r, "E301"));
        }

        [Fact]
        public void LoopsTooFewPoints_E301_SilentlyDroppedReported()
        {
            // 2 点环会被解析器静默丢弃(多孔会少切一个孔),校验器必须补报
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""loops"":[[[0,0],[1,1]]],""depth"":0.05}]");
            Assert.True(HasCode(r, "E301"));
        }

        [Fact]
        public void CoincidentPoints_E302()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""polygon"":[[0,0],[0,0],[1,0.1]],""depth"":0.05}]");
            Assert.True(HasCode(r, "E302"));
        }

        [Fact]
        public void SelfIntersection_E303()
        {
            // 8 字形:边(0,0)-(2,2) 与边(0,2)-(2,0) 相交于 (1,1)
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""polygon"":[[0,0],[2,2],[0,2],[2,0]],""depth"":0.05}]");
            Assert.True(HasCode(r, "E303"));
        }

        [Fact]
        public void DegenerateCollinear_E304()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""polygon"":[[0,0],[1,0],[2,0]],""depth"":0.05}]");
            Assert.True(HasCode(r, "E304"));
        }

        [Fact]
        public void WindingMismatch_W301()
        {
            // 环 1 逆时针(面积 +0.5)、环 2 顺时针(面积 -0.5) → W301;两环不相交 → 无 W404
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""loops"":[[[0,0],[1,0],[0,1]],[[2,0],[2,1],[3,1]]],""depth"":0.05}]");
            Assert.True(HasCode(r, "W301"));
        }

        [Fact]
        public void CircleNonPositiveRadius_FallsBack_E102()
        {
            // 真实行为(2026-09-16 单测发现并固化):r<=0 的 circle 被解析器当"没有圆"回退直线环;
            // LoopGeometryRule 见"有 circle 字段但没解析成圆"直接 yield break →
            // E305 在该路径不可达,实际报的是 E102(缺形状)。
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""circle"":[0,0,0],""depth"":0.05}]");
            Assert.True(HasCode(r, "E102"));
            Assert.False(HasCode(r, "E305"));   // 规则存在但此路径不可达
        }

        [Fact]
        public void SlotLengthLessThanWidth_E306()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""slot"":[0,0,0.02,0.03],""depth"":0.05}]");
            Assert.True(HasCode(r, "E306"));
        }

        // ---------- 语义层 ----------

        [Fact]
        public void CutBeforeExtrude_E401()
        {
            var r = V(@"[{""op"":""cut"",""plane"":""RefPlane_1"",""circle"":[0,0,0.01]}]");
            Assert.True(HasCode(r, "E401"));
        }

        [Theory]
        [InlineData(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]]}]", "E402")]
        [InlineData(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0}]", "E402")]
        [InlineData(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":-0.01}]", "E402")]
        public void ExtrudeDepth_E402(string json, string code)
        {
            Assert.True(HasCode(V(json), code));
        }

        [Theory]
        [InlineData(@"[{""op"":""revolve"",""polygon"":[[0,0],[0.02,0],[0.02,0.05]]}]", "E403")]
        [InlineData(@"[{""op"":""revolve"",""axis"":[0,0,0,0],""polygon"":[[0,0],[0.02,0],[0.02,0.05]]}]", "E403")]
        [InlineData(@"[{""op"":""revolve"",""axis"":[[0,0],[0,0.05]],""degrees"":400,""polygon"":[[0,0],[0.02,0],[0.02,0.05]]}]", "E403")]
        public void RevolveAxisAndAngle_E403(string json, string code)
        {
            Assert.True(HasCode(V(json), code));
        }

        [Fact]
        public void CutFiniteNoDepth_E404()
        {
            var r = V("[" + Wall + @",{""op"":""cut"",""plane"":""RefPlane_1"",""mode"":""finite"",""circle"":[0,0,0.01]}]");
            Assert.True(HasCode(r, "E404"));
        }

        [Fact]
        public void CutNoMode_W405()
        {
            var r = V("[" + Wall + @",{""op"":""cut"",""plane"":""RefPlane_1"",""circle"":[0,0,0.01]}]");
            Assert.True(HasCode(r, "W405"));
        }

        [Fact]
        public void SideOutOfRange_E105()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05,""side"":4}]");
            Assert.True(HasCode(r, "E105"));
        }

        [Fact]
        public void SideBothWithDepth_W402()
        {
            // side=3 双向:实际总长 = depth × 2,提示写错
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05,""side"":3}]");
            Assert.True(HasCode(r, "W402"));
        }

        [Fact]
        public void SamePlaneDoubleCut_W401()
        {
            // 僵尸特征头号杀手:同平面第二次 AddThroughNext
            var r = V("[" + Wall +
                      @",{""op"":""cut"",""plane"":""RefPlane_1"",""circle"":[0,0,0.01]}" +
                      @",{""op"":""cut"",""plane"":""RefPlane_1"",""circle"":[0.05,0,0.01]}]");
            Assert.True(HasCode(r, "W401"));
        }

        [Fact]
        public void OverlapLoops_W404()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""loops"":[[[0,0],[1,0],[0,1]],[[0,0],[1,0],[0,1]]],""depth"":0.05}]");
            Assert.True(HasCode(r, "W404"));
        }

        [Fact]
        public void ConstraintOnCircle_W406()
        {
            // circle 轮廓没有直线可约束 → autoConstraint 被静默忽略
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""circle"":[0,0,0.01],""depth"":0.05,""autoconstraint"":true}]");
            Assert.True(HasCode(r, "W406"));
        }

        [Fact]
        public void DimsElementOutOfRange_W407()
        {
            // rect 4 点 = 4 条线,element=99 越界
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05,""dims"":[{""element"":99,""name"":""w"",""value"":""40 mm""}]}]");
            Assert.True(HasCode(r, "W407"));
        }

        [Fact]
        public void DimsIncomplete_W407()
        {
            var r = V(@"[{""op"":""extrude"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.05,""dims"":[{""element"":0,""value"":""40 mm""}]}]");
            Assert.True(HasCode(r, "W407"));
        }

        // ---------- 回归:skill 文档中的小房子示例 ----------

        [Fact]
        public void HouseExample_NoErrors()
        {
            // SKILL.md §2.1 / modeling-recipes-project.md 的小房子 features(se_model_build 示例)
            // 只断言 errorCount==0(允许 W405 cut 无 mode / W402 side=3 提示)
            var r = V(@"[{""op"":""plane"",""name"":""front"",""base"":""RefPlane_3"",""distance"":-0.1}," +
                      @"{""op"":""extrude"",""name"":""wall"",""plane"":""RefPlane_1"",""rect"":[[-0.15,-0.1],[0.15,0.1]],""side"":2,""depth"":0.2}," +
                      @"{""op"":""cut"",""name"":""holes"",""plane"":""@front"",""loops"":[[[-0.03,0],[0.03,0],[0.03,0.09],[-0.03,0.09]]]}," +
                      @"{""op"":""extrude"",""name"":""roof"",""plane"":""RefPlane_3"",""polygon"":[[-0.15,0.2],[0.15,0.2],[0,0.3]],""side"":3,""depth"":0.2}]");
            Assert.Equal(0, r.errorCount);
            Assert.Equal(4, r.featureCount);
        }

        // ---------- 扩 op(2026-09-22):fillet / chamfer / rib / pattern ----------

        private const string Plate = @"{""op"":""extrude"",""name"":""plate"",""plane"":""RefPlane_1"",""rect"":[[-0.1,-0.1],[0.1,0.1]],""depth"":0.01}";

        [Fact]
        public void Fillet_Valid_Ok()
        {
            var r = V("[" + Plate + @",{""op"":""fillet"",""radius"":0.002,""edges"":[{""face"":""face:71"",""edge"":0}]}]");
            Assert.Equal("ok", r.status);
        }

        [Fact]
        public void Fillet_MissingRadius_E405()
        {
            var r = V("[" + Plate + @",{""op"":""fillet"",""edges"":[{""face"":""face:71"",""edge"":0}]}]");
            Assert.True(HasCode(r, "E405"));
        }

        [Fact]
        public void Fillet_MissingEdges_E205()
        {
            var r = V("[" + Plate + @",{""op"":""fillet"",""radius"":0.002}]");
            Assert.True(HasCode(r, "E205"));
        }

        [Fact]
        public void Fillet_BadEdgeRef_E205()
        {
            // face 缺失 / edge 负数 / 整项不是对象 → 都归 E205
            Assert.True(HasCode(V("[" + Plate + @",{""op"":""fillet"",""radius"":0.002,""edges"":[{""edge"":0}]}]"), "E205"));
            Assert.True(HasCode(V("[" + Plate + @",{""op"":""fillet"",""radius"":0.002,""edges"":[{""face"":""face:71"",""edge"":-1}]}]"), "E205"));
            Assert.True(HasCode(V("[" + Plate + @",{""op"":""fillet"",""radius"":0.002,""edges"":[""oops""]}]"), "E205"));
        }

        [Fact]
        public void Fillet_ZeroRadius_E405()
        {
            var r = V("[" + Plate + @",{""op"":""fillet"",""radius"":0,""edges"":[{""face"":""face:71"",""edge"":0}]}]");
            Assert.True(HasCode(r, "E405"));
        }

        [Fact]
        public void Chamfer_Valid_Ok()
        {
            var r = V("[" + Plate + @",{""op"":""chamfer"",""distance"":0.002,""edges"":[{""face"":""face:71"",""edge"":0}]}]");
            Assert.Equal("ok", r.status);
        }

        [Fact]
        public void Chamfer_MissingDistance_E406()
        {
            var r = V("[" + Plate + @",{""op"":""chamfer"",""edges"":[{""face"":""face:71"",""edge"":0}]}]");
            Assert.True(HasCode(r, "E406"));
        }

        [Fact]
        public void Rib_Valid_Ok()
        {
            // rib:【闭合】轮廓(rect/polygon/loops/circle)+ thickness + plane(SE 2022 实测约束)
            var r = V("[" + Plate + @",{""op"":""rib"",""plane"":""RefPlane_1"",""polygon"":[[0,0],[0.01,0],[0.01,0.01],[0,0.01]],""thickness"":0.003}]");
            Assert.Equal("ok", r.status);
        }

        [Fact]
        public void Rib_MissingThickness_E407()
        {
            var r = V("[" + Plate + @",{""op"":""rib"",""plane"":""RefPlane_1"",""polygon"":[[0,0],[0.01,0],[0.01,0.01],[0,0.01]]}]");
            Assert.True(HasCode(r, "E407"));
        }

        [Fact]
        public void Rib_MissingShape_E407()
        {
            var r = V("[" + Plate + @",{""op"":""rib"",""plane"":""RefPlane_3"",""thickness"":0.003}]");
            Assert.True(HasCode(r, "E407"));
        }

        [Fact]
        public void Pattern_Valid_Ok()
        {
            var r = V("[" + Plate + @",{""op"":""pattern"",""of"":""plate"",""plane"":""RefPlane_1"",""xcount"":3,""xspacing"":0.05}]");
            Assert.Equal("ok", r.status);
        }

        [Fact]
        public void Pattern_MissingOf_E408()
        {
            var r = V("[" + Plate + @",{""op"":""pattern"",""plane"":""RefPlane_1"",""xcount"":3,""xspacing"":0.05}]");
            Assert.True(HasCode(r, "E408"));
        }

        [Fact]
        public void Pattern_BothCountsOne_E408()
        {
            var r = V("[" + Plate + @",{""op"":""pattern"",""of"":""plate"",""plane"":""RefPlane_1"",""xcount"":1,""ycount"":1,""xspacing"":0.05,""yspacing"":0.05}]");
            Assert.True(HasCode(r, "E408"));
        }

        [Fact]
        public void Pattern_CountWithoutSpacing_E408()
        {
            var r = V("[" + Plate + @",{""op"":""pattern"",""of"":""plate"",""plane"":""RefPlane_1"",""xcount"":3}]");
            Assert.True(HasCode(r, "E408"));
        }

        [Fact]
        public void Pattern_UnknownOfRef_W408()
        {
            // of 不是 obj- 且本批之前无同名 name → W408 提醒(不拦 error)
            var r = V("[" + Plate + @",{""op"":""pattern"",""of"":""NoName"",""plane"":""RefPlane_1"",""xcount"":3,""xspacing"":0.05}]");
            Assert.Equal(0, r.errorCount);
            Assert.True(HasCode(r, "W408"));
        }

        [Fact]
        public void Pattern_KnownOfRef_NoW408()
        {
            var r = V("[" + Plate + @",{""op"":""pattern"",""of"":""plate"",""plane"":""RefPlane_1"",""xcount"":3,""xspacing"":0.05}]");
            Assert.False(HasCode(r, "W408"));
        }

        [Fact]
        public void NewOps_AreInWhitelist_NoE101()
        {
            var r = V("[" + Plate + @",{""op"":""fillet"",""radius"":0.002,""edges"":[{""face"":""face:71"",""edge"":0}]}," +
                     @"{""op"":""chamfer"",""distance"":0.002,""edges"":[{""face"":""face:71"",""edge"":1}]}," +
                     @"{""op"":""rib"",""plane"":""RefPlane_1"",""polygon"":[[0,0],[0.01,0],[0.01,0.01],[0,0.01]],""thickness"":0.003}," +
                     @"{""op"":""pattern"",""of"":""plate"",""plane"":""RefPlane_1"",""xcount"":3,""xspacing"":0.05}]");
            Assert.False(HasCode(r, "E101"));
            Assert.Equal(5, r.featureCount);
        }

        [Fact]
        public void Fillet_DoesNotNeedPlane()
        {
            // fillet/chamfer 不需要 plane 引用(直接引用已有实体边),缺 plane 不应报 E201
            var r = V("[" + Plate + @",{""op"":""fillet"",""radius"":0.002,""edges"":[{""face"":""face:71"",""edge"":0}]}]");
            Assert.False(HasCode(r, "E201"));
        }

        // ---------- 扩 op(2026-09-23 P1):hole ----------

        [Fact]
        public void Hole_CenterDiameter_Valid_NoErrors()
        {
            // hole 便捷写法:center+diameter(米);单孔无 W401
            var r = V("[" + Plate + @",{""op"":""hole"",""plane"":""RefPlane_1"",""center"":[0.02,0.02],""diameter"":0.012}]");
            Assert.Equal(0, r.errorCount);
            Assert.Equal(2, r.featureCount);
            Assert.False(HasCode(r, "E409"));
            Assert.False(HasCode(r, "E401"));
            Assert.False(HasCode(r, "E101"));
        }

        [Fact]
        public void Hole_CircleFiniteDepth_Valid_NoErrors()
        {
            // hole 显式圆 + 盲孔(finite 必须带 depth,E409 深度分支)
            var r = V("[" + Plate + @",{""op"":""hole"",""plane"":""RefPlane_1"",""circle"":[0.05,0,0.006],""mode"":""finite"",""depth"":0.004}]");
            Assert.Equal(0, r.errorCount);
            Assert.False(HasCode(r, "E409"));
        }

        [Theory]
        [InlineData("through_all")]
        [InlineData("through")]
        [InlineData("all")]
        [InlineData("next")]
        public void Hole_ThroughModes_Accepted_NoE409(string mode)
        {
            // through_all/through 归一为 all;四种贯穿写法都合法且无 W405(W405 只管 cut)
            var r = V("[" + Plate + @",{""op"":""hole"",""plane"":""RefPlane_1"",""circle"":[0.02,0,0.006],""mode"":""" + mode + @"""}]");
            Assert.Equal(0, r.errorCount);
            Assert.False(HasCode(r, "E409"));
            Assert.False(HasCode(r, "W405"));
        }

        [Theory]
        [InlineData(@"{""op"":""hole"",""plane"":""RefPlane_1"",""rect"":[[0.01,0.01],[0.03,0.03]]}")]
        [InlineData(@"{""op"":""hole"",""plane"":""RefPlane_1"",""polygon"":[[0,0],[0.01,0],[0.01,0.01]]}")]
        public void Hole_NonRoundShape_E409(string feat)
        {
            // 孔是圆的:rect/polygon 异形孔诚实分流回 cut(E409 shape 分支;E102 白名单不含 hole)
            var r = V("[" + Plate + "," + feat + "]");
            Assert.True(HasCode(r, "E409"));
            Assert.False(HasCode(r, "E102"));
        }

        [Theory]
        [InlineData(@"{""op"":""hole"",""plane"":""RefPlane_1"",""diameter"":0}")]
        [InlineData(@"{""op"":""hole"",""plane"":""RefPlane_1"",""diameter"":-0.005}")]
        public void Hole_NonPositiveDiameter_E409(string feat)
        {
            // diameter<=0:先报 diameter 分支;合成不生效 → 无圆 → 连带 shape 分支(都是 E409)
            var r = V("[" + Plate + "," + feat + "]");
            Assert.True(HasCode(r, "E409"));
        }

        [Fact]
        public void Hole_UnknownMode_E409()
        {
            var r = V("[" + Plate + @",{""op"":""hole"",""plane"":""RefPlane_1"",""circle"":[0.02,0,0.006],""mode"":""through_hole""}]");
            Assert.True(HasCode(r, "E409"));
        }

        [Fact]
        public void Hole_FiniteNoDepth_E409_OnlyOnce()
        {
            // finite 缺 depth:E409 报,且不与 cut 的 E404 双报(E404 只管 cut)
            var r = V("[" + Plate + @",{""op"":""hole"",""plane"":""RefPlane_1"",""circle"":[0.02,0,0.006],""mode"":""finite""}]");
            Assert.True(HasCode(r, "E409"));
            Assert.False(HasCode(r, "E404"));
        }

        [Fact]
        public void Hole_BeforeExtrude_E401()
        {
            // 打孔得有料可除:hole 与 cut 共用 E401
            var r = V(@"[{""op"":""hole"",""plane"":""RefPlane_1"",""circle"":[0,0,0.01]}]");
            Assert.True(HasCode(r, "E401"));
        }

        [Fact]
        public void Hole_SamePlaneDoubleHole_W401()
        {
            // 僵尸头号杀手同样适用于 hole:同平面两个孔 → W401(错误但可继续,警告级)
            var r = V("[" + Plate +
                      @",{""op"":""hole"",""plane"":""RefPlane_1"",""center"":[0.02,0.02],""diameter"":0.012}" +
                      @",{""op"":""hole"",""plane"":""RefPlane_1"",""circle"":[0.05,0,0.006]}]");
            Assert.True(HasCode(r, "W401"));
            Assert.Equal(0, r.errorCount);
        }

        [Fact]
        public void Hole_MixedCutAndHole_W401()
        {
            // cut 与 hole 共用除料管线:同面先 cut 后 hole 也算连续刀
            var r = V("[" + Plate +
                      @",{""op"":""cut"",""plane"":""RefPlane_1"",""circle"":[0,0,0.01]}" +
                      @",{""op"":""hole"",""plane"":""RefPlane_1"",""circle"":[0.05,0,0.006]}]");
            Assert.True(HasCode(r, "W401"));
        }

        // ---------- 扩 op(2026-09-23 P2):loft / sweep / helix ----------

        // 放样双圆截面:底面大圆 + 顶面小圆(@别名平面,批内先 plane 声明)
        private const string LoftPair =
            @"{""op"":""plane"",""name"":""top"",""base"":""RefPlane_1"",""distance"":0.05}," +
            @"{""op"":""loft"",""profiles"":[" +
            @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}," +
            @"{""plane"":""@top"",""circle"":[0,0,0.01]}]}";

        [Fact]
        public void Loft_TwoCircleSections_Ok()
        {
            var r = V("[" + Plate + "," + LoftPair + "]");
            Assert.Equal("ok", r.status);
            Assert.Equal(3, r.featureCount);
            Assert.False(HasCode(r, "E410"));
            Assert.False(HasCode(r, "E401"));
        }

        [Fact]
        public void Loft_TopLevelShape_NoProfiles_E410()
        {
            // loft 的形状只认 profiles 数组,顶层 rect 不算数(也不报 E102——形状在 profiles 里)
            var r = V("[" + Plate + @",{""op"":""loft"",""rect"":[[-0.01,-0.01],[0.01,0.01]]}]");
            Assert.True(HasCode(r, "E410"));
            Assert.False(HasCode(r, "E102"));
        }

        [Theory]
        [InlineData(@"{""op"":""loft""}")]
        [InlineData(@"{""op"":""loft"",""profiles"":[]}")]
        [InlineData(@"{""op"":""loft"",""profiles"":{}}")]
        [InlineData(@"{""op"":""loft"",""profiles"":[{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}]}")]
        public void Loft_BadProfiles_E410(string feat)
        {
            // 缺 profiles / 空数组 / 非数组 / 只有 1 个截面 → E410
            var r = V("[" + Plate + "," + feat + "]");
            Assert.True(HasCode(r, "E410"));
            Assert.Equal("error", r.status);
        }

        [Theory]
        [InlineData(@"{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}," +
                    @"{""circle"":[0,0,0.01]}", "profiles[1].plane")]
        [InlineData(@"{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}," +
                    @"{""plane"":""@top"",""circles"":[[0,0,0.01],[0.02,0,0.005]]}", "circles")]
        [InlineData(@"{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}," +
                    @"{""plane"":""@top"",""loops"":[[[0,0],[0.01,0],[0,0.01]],[[0.02,0],[0.03,0],[0.02,0.01]]]}", "个环")]
        public void Loft_BadSection_E410(string sections, string expectText)
        {
            // 截面缺 plane / circles 多真圆 / loops 多环 → E410
            var r = V(@"[{""op"":""plane"",""name"":""top"",""base"":""RefPlane_1"",""distance"":0.05}," +
                      @"{""op"":""loft"",""profiles"":[" + sections + "]}]");
            Assert.True(HasCode(r, "E410"));
            Assert.Contains(expectText, r.ToJson());
        }

        [Fact]
        public void Loft_ModeCut_BeforeExtrude_E401()
        {
            // mode:"cut" 的放样是除料:前面没料 → E401
            var r = V(@"[{""op"":""plane"",""name"":""top"",""base"":""RefPlane_1"",""distance"":0.05}," +
                      @"{""op"":""loft"",""mode"":""cut"",""profiles"":[" +
                      @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}," +
                      @"{""plane"":""@top"",""circle"":[0,0,0.01]}]}]");
            Assert.True(HasCode(r, "E401"));
        }

        [Fact]
        public void Loft_Protrusion_NoE401_EvenWithoutBase()
        {
            // 凸台模式不触发 E401(静态校验放行;首特征限制由运行时 Models.Count 门卫拦,给出明确 error)
            var r = V(@"[{""op"":""plane"",""name"":""top"",""base"":""RefPlane_1"",""distance"":0.05}," +
                      @"{""op"":""loft"",""profiles"":[" +
                      @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}," +
                      @"{""plane"":""@top"",""circle"":[0,0,0.01]}]}]");
            Assert.False(HasCode(r, "E401"));
        }

        // 扫掠:路径(开放链)+ 圆截面
        private const string SweepPair =
            @"{""op"":""sweep"",""profiles"":[" +
            @"{""plane"":""RefPlane_3"",""polygon"":[[0,0],[0.05,0.05]]}," +
            @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.005]}]}";

        [Fact]
        public void Sweep_PathAndSection_Ok()
        {
            var r = V("[" + Plate + "," + SweepPair + "]");
            Assert.Equal("ok", r.status);
            Assert.Equal(2, r.featureCount);
            Assert.False(HasCode(r, "E411"));
        }

        [Theory]
        [InlineData(@"{""op"":""sweep""}")]
        [InlineData(@"{""op"":""sweep"",""profiles"":[]}")]
        [InlineData(@"{""op"":""sweep"",""profiles"":[{""plane"":""RefPlane_3"",""polygon"":[[0,0],[0.05,0.05]]}]}")]
        public void Sweep_BadProfiles_E411(string feat)
        {
            // 缺 profiles / 空数组 / 只有路径没截面 → E411
            var r = V("[" + Plate + "," + feat + "]");
            Assert.True(HasCode(r, "E411"));
        }

        [Theory]
        [InlineData(@"{""polygon"":[[0,0],[0.05,0.05]]}," +           // 路径缺 plane
                    @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.005]}")]
        [InlineData(@"{""plane"":""RefPlane_3"",""circles"":[[0,0,0.005],[0.01,0,0.003]]}," +  // 路径 circles
                    @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.005]}")]
        [InlineData(@"{""plane"":""RefPlane_3"",""polygon"":[[0,0],[0.05,0.05]]}," +
                    @"{""circle"":[0,0,0.005]}")]                     // 截面缺 plane
        public void Sweep_BadPathOrSection_E411(string sections)
        {
            var r = V(@"[{""op"":""sweep"",""profiles"":[" + sections + "]}]");
            Assert.True(HasCode(r, "E411"));
        }

        [Fact]
        public void Sweep_ModeCut_BeforeExtrude_E401()
        {
            var r = V(@"[{""op"":""sweep"",""mode"":""cut"",""profiles"":[" +
                      @"{""plane"":""RefPlane_3"",""polygon"":[[0,0],[0.05,0.05]]}," +
                      @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.005]}]}]");
            Assert.True(HasCode(r, "E401"));
        }

        // 螺旋除料:circle 截面 + axis + pitch/height 三给二(pitch 必须大于线径 2r=0.010,否则触发 W412)
        private const string HelixFeat =
            @"{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.005]," +
            @"""axis"": [[0,0],[0,0.05]],""pitch"":0.015,""height"":0.04}";

        [Fact]
        public void Helix_PitchHeight_Ok()
        {
            var r = V("[" + Plate + "," + HelixFeat + "]");
            Assert.Equal("ok", r.status);
            Assert.Equal(2, r.featureCount);
            Assert.False(HasCode(r, "E412"));
        }

        [Theory]
        [InlineData(@"{""op"":""helix"",""plane"":""RefPlane_1"",""circles"":[[0.03,0,0.005],[0.05,0,0.003]],""axis"":[[0,0],[0,0.05]],""pitch"":0.008,""height"":0.04}", "circles")]
        [InlineData(@"{""op"":""helix"",""plane"":""RefPlane_1"",""loops"":[[[0,0],[0.01,0],[0,0.01]],[[0.02,0],[0.03,0],[0.02,0.01]]],""axis"":[[0,0],[0,0.05]],""pitch"":0.008,""height"":0.04}", "个环")]
        [InlineData(@"{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.005],""axis"":[[0,0],[0,0.05]],""pitch"":0,""height"":0.04}", "pitch")]
        [InlineData(@"{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.005],""axis"":[[0,0],[0,0.05]],""height"":-0.04,""revolutions"":5}", "height")]
        [InlineData(@"{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.005],""axis"":[[0,0],[0,0.05]],""revolutions"":0}", "revolutions")]
        [InlineData(@"{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.005],""axis"":[[0,0],[0,0.05]],""pitch"":0.008}", "至少 2")]
        public void Helix_BadParams_E412(string feat, string expectText)
        {
            // circles 多环 / loops 多环 / 非正数参数 / 三要素只给 1 个 → E412
            var r = V("[" + Plate + "," + feat + "]");
            Assert.True(HasCode(r, "E412"));
            Assert.Contains(expectText, r.ToJson());
        }

        [Fact]
        public void Helix_MissingShape_E102()
        {
            // helix 纳入 E102 必填形状(与 revolve 同构:顶层单闭合截面)
            var r = V("[" + Plate + @",{""op"":""helix"",""plane"":""RefPlane_1"",""axis"":[[0,0],[0,0.05]],""pitch"":0.008,""height"":0.04}]");
            Assert.True(HasCode(r, "E102"));
        }

        [Fact]
        public void Helix_MissingPlane_E201()
        {
            var r = V("[" + Plate + @",{""op"":""helix"",""circle"":[0.03,0,0.005],""axis"":[[0,0],[0,0.05]],""pitch"":0.008,""height"":0.04}]");
            Assert.True(HasCode(r, "E201"));
        }

        [Fact]
        public void Helix_MissingAxis_E403()
        {
            var r = V("[" + Plate + @",{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.005],""pitch"":0.008,""height"":0.04}]");
            Assert.True(HasCode(r, "E403"));
        }

        [Fact]
        public void Helix_AngleIgnored_NoE403AngleError()
        {
            // E403 的角度分支只管 revolve;helix 带 angle 不报"超过一整圈"
            var r = V("[" + Plate + @",{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.005],""axis"":[[0,0],[0,0.05]],""angle"":7.0,""pitch"":0.008,""height"":0.04}]");
            Assert.False(HasCode(r, "E403"));
        }

        [Fact]
        public void Helix_BeforeExtrude_E401()
        {
            // helix 默认凸台,但 mode:"cut" 就是除料 → 前面没料时 E401
            var r = V(@"[{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.005],""axis"":[[0,0],[0,0.05]],""mode"":""cut"",""pitch"":0.008,""height"":0.04}]");
            Assert.True(HasCode(r, "E401"));
        }

        [Fact]
        public void Helix_ProtrusionAfterExtrude_NoE401()
        {
            var r = V("[" + Plate + "," + HelixFeat + "]");
            Assert.False(HasCode(r, "E401"));
        }

        [Fact]
        public void Helix_SelfIntersect_W412()
        {
            // 2026-09-23 真机:重叠≥50%(pitch=0.005 < r=0.006)必得僵尸 6311 → W412
            var r = V("[" + Plate + @",{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.006],""axis"":[[0,0],[0,0.05]],""pitch"":0.005,""revolutions"":3}]");
            Assert.True(HasCode(r, "W412"));
        }

        [Fact]
        public void Helix_LightOverlap_NoW412()
        {
            // 真机:重叠~8%(pitch=0.011,r=0.006)SE 实际能成体 → 不警告(阈值在 (r,2r] 未定,靠真机兜底)
            var r = V("[" + Plate + @",{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.006],""axis"":[[0,0],[0,0.05]],""pitch"":0.011,""revolutions"":3}]");
            Assert.False(HasCode(r, "W412"));
        }

        [Fact]
        public void Helix_PitchOverWire_NoW412()
        {
            // pitch(0.015) > 2r(0.012) → 无 W412
            var r = V("[" + Plate + @",{""op"":""helix"",""plane"":""RefPlane_1"",""circle"":[0.03,0,0.006],""axis"":[[0,0],[0,0.05]],""pitch"":0.015,""revolutions"":3}]");
            Assert.False(HasCode(r, "W412"));
        }

        [Fact]
        public void P2Ops_AreInWhitelist_NoE101()
        {
            var r = V("[" + Plate + "," + LoftPair + "," + SweepPair + "," + HelixFeat + "]");
            Assert.False(HasCode(r, "E101"));
            Assert.Equal(5, r.featureCount);
        }

        [Fact]
        public void P2Ops_UnknownFields_W102()
        {
            // profiles 项内的形状字段在项白名单内;顶层凑出未知字段仍要 W102
            var r = V("[" + Plate + @",{""op"":""loft"",""sectoins"":[1],""profiles"":[" +
                    @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.03]}," +
                    @"{""plane"":""RefPlane_1"",""circle"":[0,0,0.01]}]}]");
            Assert.True(HasCode(r, "W102"));
        }

        // ---------- 扩 op(2026-09-23 P3):draft/split/web_network/extrude_surface/thicken/delete_face ----------
        // 基础设施:E204(FeatureRefRule,@别名面引用)/E413~E417(6 op 各自参数)。
        // Plate 常量带 name:"plate",故 @plate 是已定义的命名特征——可直接当面引用目标。

        // ===== E204 FeatureRefRule(仅 draft/thicken/delete_face 触发)=====

        [Fact]
        public void Draft_FaceRefAliasOk_NoE204()
        {
            // @plate 由 Plate 的 name 定义在前 → 不报 E204
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""@plate"",""faceNormal"":[0,0,1],""angle"":0.0873,""side"":4}]");
            Assert.False(HasCode(r, "E204"));
        }

        [Fact]
        public void Draft_NoFaceRef_E204()
        {
            // draft 是面引用类 op,缺 faceOf → E204
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1"",""angle"":0.0873,""side"":4}]");
            Assert.True(HasCode(r, "E204"));
        }

        [Fact]
        public void Thicken_FaceRefForwardRef_E204()
        {
            // @s1 定义在 thicken 之后(前向引用)→ E204
            var r = V(@"[{""op"":""thicken"",""faceOf"":""@s1"",""thickness"":0.002}," +
                    @"{""op"":""extrude_surface"",""name"":""s1"",""plane"":""RefPlane_1"",""rect"":[[-0.01,-0.01],[0.01,0.01]],""depth"":0.01}]");
            Assert.True(HasCode(r, "E204"));
        }

        [Fact]
        public void DeleteFace_FaceRefUndefined_E204()
        {
            // @missing 从未定义 → E204
            var r = V("[" + Plate + @",{""op"":""delete_face"",""faceOf"":""@missing"",""faceIndex"":2,""confirm"":true}]");
            Assert.True(HasCode(r, "E204"));
        }

        [Fact]
        public void Draft_FaceRefObjHandle_NoE204()
        {
            // obj-K 句柄静态不判(句柄表在 server 进程)→ 不报 E204
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""obj-1"",""faceNormal"":[0,0,1],""angle"":0.0873,""side"":4}]");
            Assert.False(HasCode(r, "E204"));
        }

        [Fact]
        public void Draft_FaceRefBareName_E204()
        {
            // 纯名字无 @ 前缀 → E204(必须以 @ 或 obj- 开头)
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""plate"",""faceNormal"":[0,0,1],""angle"":0.0873,""side"":4}]");
            Assert.True(HasCode(r, "E204"));
        }

        [Fact]
        public void Thicken_NoFaceRef_E204()
        {
            // thicken 也是面引用类 op,缺 faceOf → E204
            var r = V("[" + Plate + @",{""op"":""thicken"",""thickness"":0.002}]");
            Assert.True(HasCode(r, "E204"));
        }

        [Theory]
        [InlineData(@"""faceOf"":123", "faceOf 必须是非空字符串")]
        [InlineData(@"""faceOf"":""""", "faceOf 必须是非空字符串")]
        [InlineData(@"""faceOf"":""@plate"",""faceNormal"":[0,0]", "faceNormal 必须是 3 元数组")]
        [InlineData(@"""faceOf"":""@plate"",""faceNormal"":[0,0,0]", "faceNormal 不能是零向量")]
        [InlineData(@"""faceOf"":""@plate"",""faceIndex"":-1", "faceIndex 必须 >= 0")]
        public void Draft_FaceRefParseError_E204(string faceFields, string expectText)
        {
            // 解析失败优先报 E204 并带原文
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," + faceFields + @",""angle"":0.0873,""side"":4}]");
            Assert.True(HasCode(r, "E204"));
            Assert.Contains(expectText, r.ToJson());
        }

        // ===== E413 DraftRule(side 只认 4/5;angle ∈ [0,π/2))=====

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(6)]
        public void Draft_SideIllegal_E413(int side)
        {
            // side 只认 4(igInside)/5(igOutside),传 1/2/3/6 全 E_FAIL → E413
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""@plate"",""faceNormal"":[0,0,1],""angle"":0.0873,""side"":" + side + "}]");
            Assert.True(HasCode(r, "E413"));
        }

        [Theory]
        [InlineData(2.0)]        // > π/2 ≈ 1.5708
        [InlineData(-0.1)]      // < 0
        [InlineData(1.5708)]    // = π/2(开区间,等于也越界)
        public void Draft_AngleOutOfRange_E413(double ang)
        {
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""@plate"",""faceNormal"":[0,0,1],""angle"":" + ang.ToString(System.Globalization.CultureInfo.InvariantCulture) + @",""side"":4}]");
            Assert.True(HasCode(r, "E413"));
        }

        [Fact]
        public void Draft_AngleZero_NoE413()
        {
            // 0 ∈ [0,π/2) → 合法(不报 E413)
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""@plate"",""faceNormal"":[0,0,1],""angle"":0,""side"":4}]");
            Assert.False(HasCode(r, "E413"));
        }

        [Fact]
        public void Draft_DegreesInsteadOfAngle_NoE413()
        {
            // degrees(度)也能给,5° → 0.0873 弧度,合法
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""@plate"",""faceNormal"":[0,0,1],""degrees"":5,""side"":5}]");
            Assert.False(HasCode(r, "E413"));
        }

        [Fact]
        public void Draft_NoAngle_E413()
        {
            // 缺 angle 与 degrees → E413
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""@plate"",""faceNormal"":[0,0,1],""side"":4}]");
            Assert.True(HasCode(r, "E413"));
        }

        // ===== E414 SplitRule(target 给了必须是 @别名/obj-K)=====

        [Fact]
        public void Split_BareTarget_E414()
        {
            // target 纯名字无前缀 → E414
            var r = V("[" + Plate + @",{""op"":""split"",""plane"":""RefPlane_1"",""target"":""plate""}]");
            Assert.True(HasCode(r, "E414"));
        }

        [Fact]
        public void Split_AliasTarget_NoE414()
        {
            var r = V("[" + Plate + @",{""op"":""split"",""plane"":""RefPlane_1"",""target"":""@plate""}]");
            Assert.False(HasCode(r, "E414"));
        }

        [Fact]
        public void Split_ObjTarget_NoE414()
        {
            var r = V("[" + Plate + @",{""op"":""split"",""plane"":""RefPlane_1"",""target"":""obj-1""}]");
            Assert.False(HasCode(r, "E414"));
        }

        [Fact]
        public void Split_UnknownAliasTarget_E414()
        {
            // 2026-09-23 真机负例 n6:@nope 引用不存在的名字 → E414(此前只查前缀静默通过)
            var r = V("[" + Plate + @",{""op"":""split"",""plane"":""RefPlane_1"",""target"":""@nope""}]");
            Assert.True(HasCode(r, "E414"));
        }

        [Fact]
        public void Split_ForwardAliasTarget_E414()
        {
            // @引用本批后面才定义的特征(前向引用)→ E414
            var r = V("[" + Plate + @",{""op"":""split"",""plane"":""RefPlane_1"",""target"":""@later""}," +
                    @"{""op"":""extrude"",""name"":""later"",""plane"":""RefPlane_1"",""rect"":[[0.2,0.2],[0.3,0.3]],""depth"":0.005}]");
            Assert.True(HasCode(r, "E414"));
        }

        [Fact]
        public void Split_BackwardAliasTarget_NoE414()
        {
            // @引用前面已定义特征(合法)→ 无 E414
            var r = V("[" + Plate + @",{""op"":""extrude"",""name"":""boss"",""plane"":""RefPlane_1"",""rect"":[[0.2,0.2],[0.3,0.3]],""depth"":0.005}," +
                    @"{""op"":""split"",""plane"":""RefPlane_1"",""target"":""@boss""}]");
            Assert.False(HasCode(r, "E414"));
        }

        [Fact]
        public void Split_NoTarget_NoE414()
        {
            // target 缺省(用 Models.Item(1))→ 不报 E414
            var r = V("[" + Plate + @",{""op"":""split"",""plane"":""RefPlane_1""}]");
            Assert.False(HasCode(r, "E414"));
        }

        // ===== E415 WebNetworkRule(thickness/depth 必须 > 0)=====

        [Theory]
        [InlineData("\"thickness\":-0.001,\"depth\":0.005")]
        [InlineData("\"thickness\":0,\"depth\":0.005")]
        [InlineData("\"depth\":0.005")]                         // 缺 thickness
        public void Web_BadThickness_E415(string fields)
        {
            var r = V("[" + Plate + @",{""op"":""web_network"",""plane"":""RefPlane_1""," +
                    @"""polygon"":[[-0.01,-0.01],[0.01,-0.01],[0.01,0.01],[-0.01,0.01]]," + fields + "}]");
            Assert.True(HasCode(r, "E415"));
        }

        [Theory]
        [InlineData("\"thickness\":0.003,\"depth\":-0.001")]
        [InlineData("\"thickness\":0.003,\"depth\":0")]
        [InlineData("\"thickness\":0.003")]                     // 缺 depth
        public void Web_BadDepth_E415(string fields)
        {
            var r = V("[" + Plate + @",{""op"":""web_network"",""plane"":""RefPlane_1""," +
                    @"""polygon"":[[-0.01,-0.01],[0.01,-0.01],[0.01,0.01],[-0.01,0.01]]," + fields + "}]");
            Assert.True(HasCode(r, "E415"));
        }

        [Fact]
        public void Web_BothPositive_NoE415()
        {
            var r = V("[" + Plate + @",{""op"":""web_network"",""plane"":""RefPlane_1""," +
                    @"""polygon"":[[-0.01,-0.01],[0.01,-0.01],[0.01,0.01],[-0.01,0.01]],""thickness"":0.003,""depth"":0.005}]");
            Assert.False(HasCode(r, "E415"));
        }

        // ===== E416 ThickenRule(thickness 必须 > 0;faceOf 由 E204 判)=====

        [Theory]
        [InlineData("\"thickness\":-0.001")]
        [InlineData("\"thickness\":0")]
        public void Thicken_BadThickness_E416(string fields)
        {
            var r = V("[" + Plate + @",{""op"":""thicken"",""faceOf"":""@plate""," + fields + "}]");
            Assert.True(HasCode(r, "E416"));
        }

        [Fact]
        public void Thicken_NoThickness_E416()
        {
            var r = V("[" + Plate + @",{""op"":""thicken"",""faceOf"":""@plate""}]");
            Assert.True(HasCode(r, "E416"));
        }

        [Fact]
        public void Thicken_PositiveThickness_NoE416()
        {
            var r = V("[" + Plate + @",{""op"":""thicken"",""faceOf"":""@plate"",""thickness"":0.002}]");
            Assert.False(HasCode(r, "E416"));
        }

        // ===== E417 DeleteFaceRule(confirm 必须 true)=====

        [Fact]
        public void DeleteFace_NoConfirm_E417()
        {
            var r = V("[" + Plate + @",{""op"":""delete_face"",""faceOf"":""@plate"",""faceIndex"":2}]");
            Assert.True(HasCode(r, "E417"));
        }

        [Fact]
        public void DeleteFace_ConfirmFalse_E417()
        {
            var r = V("[" + Plate + @",{""op"":""delete_face"",""faceOf"":""@plate"",""faceIndex"":2,""confirm"":false}]");
            Assert.True(HasCode(r, "E417"));
        }

        [Fact]
        public void DeleteFace_ConfirmTrue_NoE417()
        {
            var r = V("[" + Plate + @",{""op"":""delete_face"",""faceOf"":""@plate"",""faceIndex"":2,""confirm"":true}]");
            Assert.False(HasCode(r, "E417"));
        }

        // ===== 集成:整批 ok + 新 op 进白名单(不报 E101)=====

        [Fact]
        public void Draft_FullBatch_Ok()
        {
            // Plate(name=plate)→ draft(@plate, faceNormal, angle, side=4)→ 整批 ok
            var r = V("[" + Plate + @",{""op"":""draft"",""plane"":""RefPlane_1""," +
                    @"""faceOf"":""@plate"",""faceNormal"":[0,0,1],""angle"":0.0873,""side"":4}]");
            Assert.Equal("ok", r.status);
            Assert.Equal(2, r.featureCount);
            Assert.False(HasCode(r, "E101"));
        }

        [Fact]
        public void ExtrudeSurface_NotE101()
        {
            // extrude_surface 进 op 白名单 → 不报 E101
            var r = V(@"[{""op"":""extrude_surface"",""plane"":""RefPlane_1""," +
                    @"""rect"":[[-0.01,-0.01],[0.01,0.01]],""depth"":0.01}]");
            Assert.False(HasCode(r, "E101"));
        }

        [Fact]
        public void ExtrudeSurface_NoShape_E102()
        {
            // extrude_surface 缺形状(无 rect/circle/polygon/loops)→ E102(已扩进 RequiredShapeRule)
            var r = V(@"[{""op"":""extrude_surface"",""plane"":""RefPlane_1"",""depth"":0.01}]");
            Assert.True(HasCode(r, "E102"));
        }
    }
}
