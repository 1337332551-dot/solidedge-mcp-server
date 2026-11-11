using System;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// FaceSelector(纯函数,不碰 COM):se_find_objects 的过滤字段 与 se_assembly_build 的
    /// face1/face2 描述符共用这一份解析与匹配,所以两个工具的面语义靠这组用例锁住。
    /// </summary>
    public class FaceSelectorTests
    {
        private static FaceSelector FromString(string text)
        {
            string err;
            FaceSelector sel = FaceSelector.FromString(text, out err);
            Assert.Null(err);
            Assert.NotNull(sel);
            return sel;
        }

        private static string ErrorOf(string text)
        {
            string err;
            FaceSelector sel = FaceSelector.FromString(text, out err);
            Assert.Null(sel);
            return err;
        }

        private static FaceSelector FromJson(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                string err;
                FaceSelector sel = FaceSelector.FromJson(doc.RootElement, out err);
                Assert.Null(err);
                Assert.NotNull(sel);
                return sel;
            }
        }

        // ---------- 人工构造 FaceRow,把 TryMatch 的行为钉死 ----------

        /// <summary>圆柱面:dia 200、轴向 Z、长 20mm、中心 (0,0,10)。</summary>
        private static ModelStateTools.FaceRow Cylinder(double diaMm, double[] lo, double[] hi, double[] axis)
        {
            return new ModelStateTools.FaceRow
            {
                Id = 53,
                Type = "Cylinder",
                RadiusM = diaMm / 2000.0,
                HasRange = true,
                Lo = lo,
                Hi = hi,
                AxisVector = axis,
                ComFace = new object()
            };
        }

        private static ModelStateTools.FaceRow Plane(double[] outwardNormal, double[] lo, double[] hi)
        {
            return new ModelStateTools.FaceRow
            {
                Id = 40,
                Type = "Plane",
                HasRange = true,
                Lo = lo,
                Hi = hi,
                Normal = outwardNormal,
                OutwardNormal = outwardNormal,
                ComFace = new object()
            };
        }

        // ---------- 字符串描述符 ----------

        [Fact]
        public void FromString_TypeAndDia()
        {
            FaceSelector s = FromString("cylinder:dia=89");
            Assert.Equal("cylinder", s.Type);
            Assert.Equal(89, s.DiaMm, 9);
            Assert.False(s.IsEmpty);
        }

        [Fact]
        public void FromString_BareNumberMeansDia()
        {
            Assert.Equal(89, FromString("cylinder:89").DiaMm, 9);
        }

        [Fact]
        public void FromString_BareAxisMeansNormal()
        {
            FaceSelector s = FromString("plane:-Z");
            Assert.Equal(new[] { 0.0, 0.0, -1.0 }, s.Normal);
        }

        [Fact]
        public void FromString_SemicolonFields()
        {
            FaceSelector s = FromString("cylinder:dia=89;at=0,0,10;len=20;tol=0.1");
            Assert.Equal(89, s.DiaMm, 9);
            Assert.Equal(new[] { 0.0, 0.0, 10.0 }, s.AtMm);
            Assert.Equal(20, s.LenMm, 9);
            Assert.Equal(0.1, s.TolMm, 9);
        }

        [Fact]
        public void FromString_UnknownTypeIsError()
        {
            Assert.Contains("面类型", ErrorOf("cube:dia=10"));
        }

        [Fact]
        public void FromString_UnknownFieldIsError()
        {
            Assert.Contains("未知字段", ErrorOf("cylinder:89;bogus=1"));
        }

        [Fact]
        public void FromString_BadDirectionIsError()
        {
            Assert.Contains("normal", ErrorOf("plane:normal=up"));
        }

        [Fact]
        public void FromString_EmptyIsError()
        {
            Assert.NotNull(ErrorOf("   "));
        }

        // ---------- JSON 描述符 ----------

        [Fact]
        public void FromJson_AllFields()
        {
            FaceSelector s = FromJson(@"{""type"":""cylinder"",""diaMm"":89,""atMm"":""0,0,10"",""normal"":""-Z"",""lenMm"":20,""tolMm"":0.2}");
            Assert.Equal("cylinder", s.Type);
            Assert.Equal(89, s.DiaMm, 9);
            Assert.Equal(new[] { 0.0, 0.0, 10.0 }, s.AtMm);
            Assert.Equal(new[] { 0.0, 0.0, -1.0 }, s.Normal);
            Assert.Equal(20, s.LenMm, 9);
            Assert.Equal(0.2, s.TolMm, 9);
        }

        [Fact]
        public void FromJson_ArrayForms()
        {
            FaceSelector s = FromJson(@"{""atMm"":[1,2,3],""normal"":[0,0,2]}");
            Assert.Equal(new[] { 1.0, 2.0, 3.0 }, s.AtMm);
            Assert.Equal(new[] { 0.0, 0.0, 1.0 }, s.Normal);   // 数组方向会被单位化
        }

        [Fact]
        public void FromJson_EmptyObjectIsNotOkForConstrain()
        {
            Assert.True(FromJson("{}").IsEmpty);
        }

        // ---------- 匹配 ----------

        [Fact]
        public void Match_CylinderByDia()
        {
            var row = Cylinder(89, new[] { -0.0445, -0.0445, 0.0 }, new[] { 0.0445, 0.0445, 0.02 }, new[] { 0.0, 0.0, 1.0 });
            FaceInfo info;
            Assert.True(FromString("cylinder:dia=89").TryMatch(row, out info));
            Assert.Equal(89, info.DiaMm.Value, 3);
            Assert.Equal(new[] { 0.0, 0.0, 10.0 }, info.CenterMm);
            Assert.False(FromString("cylinder:dia=200").TryMatch(row, out info));
        }

        [Fact]
        public void Match_CylinderNormalIsAxisAndSignInsensitive()
        {
            var row = Cylinder(89, new[] { -0.0445, -0.0445, 0.0 }, new[] { 0.0445, 0.0445, 0.02 }, new[] { 0.0, 0.0, 1.0 });
            FaceInfo info;
            Assert.True(FromString("cylinder:normal=+Z").TryMatch(row, out info));
            Assert.True(info.NormalIsAxis);
            Assert.True(FromString("cylinder:normal=-Z").TryMatch(row, out info));   // 轴向平行即命中
            Assert.False(FromString("cylinder:normal=+X").TryMatch(row, out info));
        }

        [Fact]
        public void Match_PlaneNormalIsSigned()
        {
            var row = Plane(new[] { 0.0, 0.0, -1.0 }, new[] { -0.1, -0.1, 0.0 }, new[] { 0.1, 0.1, 0.0 });
            FaceInfo info;
            Assert.True(FromString("plane:normal=-Z").TryMatch(row, out info));
            Assert.False(info.NormalIsAxis);
            Assert.False(FromString("plane:normal=+Z").TryMatch(row, out info));   // 平面法向有朝向,反向不命中
        }

        [Fact]
        public void Match_PlaneAtCenter()
        {
            var row = Plane(new[] { 0.0, 0.0, 1.0 }, new[] { -0.1, -0.1, 0.02 }, new[] { 0.1, 0.1, 0.02 });
            FaceInfo info;
            Assert.True(FromString("plane:at=0,0,20").TryMatch(row, out info));
            Assert.False(FromString("plane:at=0,0,21").TryMatch(row, out info));
            Assert.True(FromString("plane:at=0,0,21;tol=2").TryMatch(row, out info));   // 容差放宽后命中
        }

        [Fact]
        public void Match_LenUsesAxisProjectionNotBoundingBox()
        {
            // 薄法兰:直径 200、轴向 20。包围盒最长边是 200,轴向投影才是 20。
            var row = Cylinder(200, new[] { -0.1, -0.1, 0.0 }, new[] { 0.1, 0.1, 0.02 }, new[] { 0.0, 0.0, 1.0 });
            FaceInfo info;
            Assert.True(FromString("cylinder:dia=200;len=20").TryMatch(row, out info));
            Assert.Equal(20, info.AxialLengthMm, 3);
            Assert.False(FromString("cylinder:dia=200;len=200").TryMatch(row, out info));
        }

        [Fact]
        public void Match_NoRangeIsSkipped()
        {
            var row = Cylinder(89, null, null, new[] { 0.0, 0.0, 1.0 });
            row.HasRange = false;
            FaceInfo info;
            Assert.False(FromString("cylinder:dia=89").TryMatch(row, out info));
        }
    }
}
