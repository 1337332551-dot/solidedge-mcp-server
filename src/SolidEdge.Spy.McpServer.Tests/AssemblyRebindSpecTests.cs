using System.Collections.Generic;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// rebind 的静态校验层(纯函数,不碰 COM)。跑不了真机的部分靠这组用例锁住:
    /// relation 的三种写法、非法值报 E192、"自动模式下只给 face1"的告警。
    /// </summary>
    public class AssemblyRebindSpecTests
    {
        private static AssemblySpec.AssemblyOp Parse(string opJson)
        {
            using (var doc = JsonDocument.Parse(opJson))
            {
                var report = new AssemblySpec.AssemblyReport { opCount = 1 };
                return AssemblySpec.ParseAndValidate(doc.RootElement, 0, report);
            }
        }

        private static AssemblySpec.AssemblyReport ParseWithReport(string opJson)
        {
            using (var doc = JsonDocument.Parse(opJson))
            {
                var report = new AssemblySpec.AssemblyReport { opCount = 1 };
                AssemblySpec.ParseAndValidate(doc.RootElement, 0, report);
                return report;
            }
        }

        private static bool HasCode(AssemblySpec.AssemblyReport report, string code)
        {
            foreach (AssemblySpec.AssemblyIssue issue in report.issues)
                if (issue.code == code) return true;
            return false;
        }

        [Fact]
        public void Rebind_在支持列表里()
        {
            Assert.Contains("rebind", AssemblySpec.SupportedOps);
        }

        [Fact]
        public void Rebind_不给relation_等于自动()
        {
            AssemblySpec.AssemblyOp op = Parse(@"{""op"":""rebind""}");
            Assert.Equal(-1, op.RelationIndex);
            Assert.Null(op.Face1Ref);
            Assert.Null(op.Face2Ref);
            Assert.False(ParseWithReport(@"{""op"":""rebind""}").HasError);
        }

        [Fact]
        public void Rebind_relation序号()
        {
            Assert.Equal(2, Parse(@"{""op"":""rebind"",""relation"":2}").RelationIndex);
        }

        [Fact]
        public void Rebind_relation写auto()
        {
            Assert.Equal(-1, Parse(@"{""op"":""rebind"",""relation"":""auto""}").RelationIndex);
        }

        [Fact]
        public void Rebind_relation负数报E192()
        {
            AssemblySpec.AssemblyReport r = ParseWithReport(@"{""op"":""rebind"",""relation"":-1}");
            Assert.True(r.HasError);
            Assert.True(HasCode(r, "E192"));
        }

        [Fact]
        public void Rebind_relation乱写报E192()
        {
            AssemblySpec.AssemblyReport r = ParseWithReport(@"{""op"":""rebind"",""relation"":""随便""}");
            Assert.True(r.HasError);
            Assert.True(HasCode(r, "E192"));
        }

        [Fact]
        public void Rebind_可以带面描述符()
        {
            AssemblySpec.AssemblyOp op = Parse(
                @"{""op"":""rebind"",""relation"":0,""face1"":{""type"":""cylinder"",""diaMm"":18},""face2"":""cylinder:18""}");
            Assert.IsType<FaceSelector>(op.Face1Ref);
            Assert.IsType<FaceSelector>(op.Face2Ref);
            Assert.False(ParseWithReport(
                @"{""op"":""rebind"",""relation"":0,""face1"":{""type"":""cylinder"",""diaMm"":18}}").HasError);
        }

        [Fact]
        public void Rebind_自动模式只给face1要告警()
        {
            AssemblySpec.AssemblyReport r = ParseWithReport(
                @"{""op"":""rebind"",""face1"":{""type"":""cylinder"",""diaMm"":18}}");
            Assert.False(r.HasError);
            Assert.True(HasCode(r, "W193"));
        }

        [Fact]
        public void Rebind_面描述符写错报E191()
        {
            AssemblySpec.AssemblyReport r = ParseWithReport(
                @"{""op"":""rebind"",""relation"":0,""face1"":""cube:dia=1""}");
            Assert.True(r.HasError);
            Assert.True(HasCode(r, "E191"));
        }
    }
}
