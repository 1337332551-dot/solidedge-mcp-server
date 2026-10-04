using System;
using System.Linq;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// FeatureSpec 解析层的行为合同。
    /// 建模器与校验器共用这一份解析——这里钉死的每一条都是"漂移 = 校验失效"的关键行为,
    /// 用例名即文档,与 FeatureSpec.cs 头部注释里"刻意保留的历史行为"逐条对应。
    /// </summary>
    public class FeatureSpecParseTests
    {
        private static FeatureSpec ParseOne(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return FeatureSpecParser.Parse(doc.RootElement, 0);
        }

        // ---------- op 字段 ----------

        [Fact]
        public void op缺失_回退extrude()
        {
            var s = ParseOne("{\"rect\":[[0,0],[0.05,0.05]]}");
            Assert.Equal("extrude", s.Op);
            Assert.Equal("extrude", s.OpLower);
        }

        [Fact]
        public void op是非字符串_回退extrude不抛异常()
        {
            var s = ParseOne("{\"op\":123}");
            Assert.Equal("extrude", s.Op);
        }

        [Fact]
        public void op大小写混合_OpLower归一小写_Op保留原文()
        {
            var s = ParseOne("{\"op\":\"CUT\"}");
            Assert.Equal("CUT", s.Op);
            Assert.Equal("cut", s.OpLower);
        }

        // ---------- circle / circles / slot ----------

        [Fact]
        public void circle对象写法_解析出圆心半径()
        {
            var s = ParseOne("{\"circle\":{\"center\":[0.01,0.02],\"radius\":0.005}}");
            Assert.True(s.HasCircle);
            Assert.Equal(0.01, s.CircleX, 12);
            Assert.Equal(0.02, s.CircleY, 12);
            Assert.Equal(0.005, s.CircleR, 12);
            Assert.Equal("circle", s.ShapeSource);
        }

        [Fact]
        public void circle数组简写_解析出圆心半径()
        {
            var s = ParseOne("{\"circle\":[0.01,0.02,0.005]}");
            Assert.True(s.HasCircle);
            Assert.Equal(0.005, s.CircleR, 12);
        }

        [Fact]
        public void circle半径小于等于0_视同没有圆_回退直线环()
        {
            // 历史行为:半径 <= 0 会"回退走直线环",这里给 rect 保证回退后有形可依
            var s = ParseOne("{\"circle\":{\"center\":[0,0],\"radius\":0},\"rect\":[[0,0],[0.05,0.05]]}");
            Assert.False(s.HasCircle);
            Assert.Equal("rect", s.ShapeSource);
            Assert.Single(s.Loops);
        }

        [Fact]
        public void circles数组_半径非正的项被丢弃()
        {
            var s = ParseOne("{\"circles\":[[0,0,0.005],[0.1,0,-0.005],[0.2,0,0]]}");
            Assert.True(s.HasCircles);
            Assert.Single(s.Circles);                 // 只剩 r>0 的那一项
            Assert.Equal("circles", s.ShapeSource);
        }

        [Fact]
        public void slot数组简写_带可选第5位角度()
        {
            var s = ParseOne("{\"slot\":[0.01,0.02,0.1,0.03,1.57]}");
            Assert.True(s.HasSlot);
            Assert.Equal(0.1, s.SlotLength, 12);
            Assert.Equal(0.03, s.SlotWidth, 12);
            Assert.Equal(1.57, s.SlotAngle, 12);
            Assert.Equal("slot", s.ShapeSource);
        }

        [Fact]
        public void slot对象写法_中心坐标被正确读取()
        {
            // 回归用例:修复前 slot 对象写法的 center 走 GetPoints 被判无效,
            // 中心静默变 (0,0)——与 circle 对象写法同一个坑
            var s = ParseOne("{\"slot\":{\"center\":[0.01,0.02],\"length\":0.1,\"width\":0.03}}");
            Assert.True(s.HasSlot);
            Assert.Equal(0.01, s.SlotX, 12);
            Assert.Equal(0.02, s.SlotY, 12);
            Assert.Equal(0.1, s.SlotLength, 12);
        }

        [Fact]
        public void center嵌套写法_仍被接受()
        {
            // TryGetCenter 兼容按点列习惯写的 [[x,y]]
            var s = ParseOne("{\"circle\":{\"center\":[[0.01,0.02]],\"radius\":0.005}}");
            Assert.True(s.HasCircle);
            Assert.Equal(0.01, s.CircleX, 12);
            Assert.Equal(0.02, s.CircleY, 12);
        }

        // ---------- sweep 路径 trace(线段+真圆弧,2026-10-04) ----------

        [Fact]
        public void sweep路径trace_解析出线段与圆弧段()
        {
            var s = ParseOne("{\"op\":\"sweep\",\"profiles\":[" +
                "{\"plane\":\"RefPlane_2\",\"trace\":[" +
                "{\"line\":[[0,0],[0,0.03]]}," +
                "{\"arc\":{\"center\":[0.02,0.03],\"start\":[0,0.03],\"end\":[0.02,0.05]}}," +
                "{\"line\":[[0.02,0.05],[0.06,0.05]]}]}," +
                "{\"plane\":\"RefPlane_1\",\"circle\":[0,0,0.006]}]}");
            var path = s.Profiles[0];
            Assert.NotNull(path.Trace);
            Assert.Null(path.TraceError);
            Assert.Equal(3, path.Trace.Count);
            Assert.Equal("line", path.Trace[0].Kind);
            Assert.Equal("arc", path.Trace[1].Kind);
            Assert.Equal(0.02, path.Trace[1].Center[0], 12);
            Assert.Equal(0.03, path.Trace[1].Center[1], 12);
            Assert.Null(path.ShapeError);                  // trace 生效后不应残留"缺形状"错误
            Assert.Equal("trace(线段/圆弧)", path.ShapeSource);
        }

        [Fact]
        public void sweep路径polygon加fillet_展开出相切圆弧段()
        {
            // 顶点链 + R → 圆心/切点由几何算出:R=0.02 的 90° 拐角,圆心落在对角点 [0.02,0.03]
            var s = ParseOne("{\"op\":\"sweep\",\"profiles\":[" +
                "{\"plane\":\"RefPlane_2\",\"polygon\":[[0,0],[0,0.05],[0.06,0.05]],\"fillet\":0.02}," +
                "{\"plane\":\"RefPlane_1\",\"circle\":[0,0,0.006]}]}");
            var path = s.Profiles[0];
            Assert.Null(path.ShapeError);
            Assert.NotNull(path.Trace);
            Assert.Equal(3, path.Trace.Count);
            Assert.Equal("line", path.Trace[0].Kind);
            Assert.Equal("arc", path.Trace[1].Kind);
            Assert.Equal("line", path.Trace[2].Kind);
            Assert.Equal(0.0, path.Trace[1].P0[0], 12);    // 入切点 = 顶点前 0.02
            Assert.Equal(0.03, path.Trace[1].P0[1], 12);
            Assert.Equal(0.02, path.Trace[1].P1[0], 12);   // 出切点 = 顶点后 0.02
            Assert.Equal(0.05, path.Trace[1].P1[1], 12);
            Assert.Equal(0.02, path.Trace[1].Center[0], 12);   // 圆心 = 拐角对角点
            Assert.Equal(0.03, path.Trace[1].Center[1], 12);
            Assert.Equal("polygon+fillet(自动切弧)", path.ShapeSource);
            Assert.Empty(path.Loops);
        }

        [Fact]
        public void sweep路径trace圆弧缺center_记TraceError不抛异常()
        {
            var s = ParseOne("{\"op\":\"sweep\",\"profiles\":[" +
                "{\"plane\":\"RefPlane_2\",\"trace\":[{\"arc\":{\"start\":[0,0],\"end\":[0.02,0]}}]}," +
                "{\"plane\":\"RefPlane_1\",\"circle\":[0,0,0.006]}]}");
            Assert.Null(s.Profiles[0].Trace);
            Assert.Contains("arc", s.Profiles[0].TraceError);
        }

        [Fact]
        public void sweep路径trace与polygon并存_记TraceError()
        {
            var s = ParseOne("{\"op\":\"sweep\",\"profiles\":[" +
                "{\"plane\":\"RefPlane_2\",\"polygon\":[[0,0],[0,0.03]],\"trace\":[{\"line\":[[0,0],[0,0.03]]}]}," +
                "{\"plane\":\"RefPlane_1\",\"circle\":[0,0,0.006]}]}");
            Assert.Null(s.Profiles[0].Trace);
            Assert.Contains("互斥", s.Profiles[0].TraceError);
        }

        // ---------- revolve 专用 ----------

        [Fact]
        public void axis嵌套写法_解析出两点()
        {
            var s = ParseOne("{\"axis\":[[0,0],[0,0.05]]}");
            Assert.True(s.HasAxis);
            Assert.Equal(0, s.AxisP1[0], 12);
            Assert.Equal(0.05, s.AxisP2[1], 12);
        }

        [Fact]
        public void axis扁平写法_与嵌套写法等价()
        {
            var s = ParseOne("{\"axis\":[0,0,0,0.05]}");
            Assert.True(s.HasAxis);
            Assert.Equal(0.05, s.AxisP2[1], 12);
        }

        [Fact]
        public void axis两点重合_视为无轴()
        {
            var s = ParseOne("{\"axis\":[[0,0],[0,0]]}");
            Assert.False(s.HasAxis);
        }

        [Fact]
        public void degrees与angle_分别存放_degrees供op层换算()
        {
            var s = ParseOne("{\"op\":\"revolve\",\"angle\":3.14,\"degrees\":360}");
            Assert.Equal(3.14, s.Angle.Value, 12);
            Assert.Equal(360, s.Degrees.Value, 12);
        }

        // ---------- 数值字段容错 ----------

        [Fact]
        public void depth给数字字符串_解析成功()
        {
            var s = ParseOne("{\"depth\":\"0.05\"}");
            Assert.Equal(0.05, s.Depth.Value, 12);
        }

        [Fact]
        public void depth类型不可解析_记null不抛异常()
        {
            var s = ParseOne("{\"depth\":\"abc\"}");
            Assert.Null(s.Depth);   // 默认值由各 op 自己套(extrude side=2 / cut side=1),解析层不猜
        }

        [Fact]
        public void side给整数字符串_解析成功()
        {
            var s = ParseOne("{\"side\":\"2\"}");
            Assert.Equal(2, s.Side.Value);
        }

        [Fact]
        public void side未给_保持null()
        {
            var s = ParseOne("{}");
            Assert.Null(s.Side);
            Assert.Null(s.ProfileSide);
        }

        // ---------- 布尔字段 ----------

        [Fact]
        public void visible缺省为null_true可解析()
        {
            Assert.Null(ParseOne("{}").Visible);
            Assert.True(ParseOne("{\"visible\":true}").Visible.Value);
        }

        [Fact]
        public void autoconstraint键大小写不敏感()
        {
            // TryGetBoolCI 存在的理由:Utf8JsonReader 默认大小写敏感,驼峰键会漏读
            var s = ParseOne("{\"AutoConstraint\":true}");
            Assert.True(s.AutoConstraint.Value);
        }

        // ---------- loops / rect / polygon ----------

        [Fact]
        public void rect两角点_归一化为四点闭合环()
        {
            // 角点顺序反过来给也一样:解析层做 min/max 归一化
            var s = ParseOne("{\"rect\":[[0.05,0.03],[0,0]]}");
            Assert.Single(s.Loops);
            var pts = s.Loops[0];
            Assert.Equal(4, pts.Length);
            Assert.Equal(0, pts[0][0], 12);
            Assert.Equal(0, pts[0][1], 12);
            Assert.Equal(0.05, pts[2][0], 12);
            Assert.Equal(0.03, pts[2][1], 12);
        }

        [Fact]
        public void loops中不足3点的环_被静默丢弃()
        {
            // 已知坑:静默丢弃会导致多孔少切一个孔——校验层 E301 会补报,这里钉解析行为本身
            var s = ParseOne("{\"loops\":[[[0,0],[0.05,0],[0.05,0.05],[0,0.05]],[[1,1],[1.1,1]]]}");
            Assert.Single(s.Loops);
            Assert.Equal("loops", s.ShapeSource);
        }

        [Fact]
        public void 完全没有形状_ShapeError被记录而非抛异常()
        {
            // 解析器不抛(校验器当 issue 报、构建器在用时抛)
            var s = ParseOne("{\"op\":\"extrude\"}");
            Assert.False(s.HasCircle);
            Assert.NotNull(s.ShapeError);
            Assert.Equal("", s.ShapeSource);
        }

        // ---------- 未知字段 / 索引 ----------

        [Fact]
        public void 白名单外字段_进UnknownFields供W102()
        {
            var s = ParseOne("{\"depht\":0.05,\"plane\":\"RefPlane_1\"}");
            Assert.Contains("depht", s.UnknownFields);
            Assert.DoesNotContain("plane", s.UnknownFields);
        }

        // ---------- coords(face 平面坐标模式,2026-10-04) ----------

        [Fact]
        public void coords缺失_为null_走global()
        {
            var s = ParseOne("{\"plane\":\"face:26\",\"rect\":[[0,0],[0.04,0.04]]}");
            Assert.Null(s.Coords);
            Assert.False(s.CoordsLocal);
            Assert.NotNull(s.FacePlaneRef);
        }

        [Fact]
        public void coordslocal_大小写不敏感_原始值小写保存()
        {
            var s = ParseOne("{\"plane\":\"face:26\",\"coords\":\"LOCAL\",\"rect\":[[0,0],[0.04,0.04]]}");
            Assert.Equal("local", s.Coords);
            Assert.True(s.CoordsLocal);
        }

        [Fact]
        public void coordsglobal_CoordsLocal为假()
        {
            var s = ParseOne("{\"plane\":\"face:26\",\"coords\":\"global\",\"rect\":[[0,0],[0.04,0.04]]}");
            Assert.Equal("global", s.Coords);
            Assert.False(s.CoordsLocal);
        }

        [Fact]
        public void coords非法值_原样小写保存_不在此报错()
        {
            // 解析层只认 "local",非法值交给校验层报 E203
            var s = ParseOne("{\"plane\":\"face:26\",\"coords\":\"Bogus\",\"rect\":[[0,0],[0.04,0.04]]}");
            Assert.Equal("bogus", s.Coords);
            Assert.False(s.CoordsLocal);
        }

        [Fact]
        public void coords在白名单内_不进UnknownFields()
        {
            var s = ParseOne("{\"plane\":\"face:26\",\"coords\":\"local\",\"rect\":[[0,0],[0.04,0.04]]}");
            Assert.DoesNotContain("coords", s.UnknownFields);
        }

        [Fact]
        public void ParseAll_为每个特征赋0based索引()
        {
            using var doc = JsonDocument.Parse("[{\"op\":\"plane\"},{\"op\":\"extrude\"},{\"op\":\"cut\"}]");
            var arr = doc.RootElement.EnumerateArray().ToArray();
            var list = FeatureSpecParser.ParseAll(arr);
            Assert.Equal(3, list.Count);
            Assert.Equal(0, list[0].Index);
            Assert.Equal(1, list[1].Index);
            Assert.Equal(2, list[2].Index);
        }

        // ---------- dims(变量绑定声明) ----------

        [Fact]
        public void dims合法项_解析通过_非法项带ParseError()
        {
            var s = ParseOne("{\"circle\":[0,0,0.005],\"dims\":[" +
                "{\"element\":0,\"name\":\"d1\",\"value\":\"10 mm\"}," +   // 合法
                "{\"element\":1,\"name\":\"d2\"}," +                      // 缺 value/formula
                "{\"element\":\"x\",\"name\":\"d3\",\"formula\":\"d1 - 1 mm\"}]}"); // element 非数字
            Assert.Equal(3, s.Dims.Count);
            Assert.Null(s.Dims[0].ParseError);
            Assert.NotNull(s.Dims[1].ParseError);
            Assert.Contains("value/formula", s.Dims[1].ParseError);
            Assert.NotNull(s.Dims[2].ParseError);
            Assert.Contains("element", s.Dims[2].ParseError);
        }

        // ---------- NeedsShape ----------

        [Theory]
        [InlineData("plane", false)]
        [InlineData("extrude", true)]
        [InlineData("cut", true)]
        [InlineData("revolve", true)]
        public void NeedsShape_只有plane不需要草图形状(string op, bool expected)
        {
            var s = ParseOne("{\"op\":\"" + op + "\"}");
            Assert.Equal(expected, s.NeedsShape);
        }
    }
}
