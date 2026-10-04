using System;
using System.Linq;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// RecipeSpecParser + RecipeValidator(均纯函数,不碰 COM)。
    /// 断言依据 RecipeSpec.cs 源码与 recipes/README.md 契约。
    /// </summary>
    public class RecipeSpecTests
    {
        private static RecipeSpec Parse(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                return RecipeSpecParser.Parse(doc.RootElement, "test.json");
            }
        }

        private static RecipeSpec Valid()
        {
            return Parse(@"{""name"":""r1"",""title"":""t"",""steps"":[{""member"":""Models"",""on"":""$$""}]}");
        }

        // ---------- Parser ----------

        [Fact]
        public void Parse_RootNotObject_Throws()
        {
            Assert.Throws<ArgumentException>(() => Parse(@"[1,2]"));
        }

        [Fact]
        public void Parse_EmptyObject_AllDefaults()
        {
            var s = Parse(@"{}");
            Assert.Null(s.Name);
            Assert.Equal("draft", s.Status);
            Assert.Equal(1, s.Version);
            Assert.Empty(s.Steps);
            Assert.Empty(s.Inputs);
            Assert.Empty(s.Preconditions);
        }

        [Fact]
        public void Parse_FullRecipe_AllFields()
        {
            // recipes/README.md 第二节的标准示例
            var s = Parse(@"{
                ""name"": ""demo"",
                ""title"": ""一句话"",
                ""version"": 2,
                ""status"": ""verified"",
                ""risk"": ""modelChanging"",
                ""anchoredTo"": { ""se"": ""2022"", ""sourceDoc"": ""part"" },
                ""inputs"": [
                    { ""name"": ""target"", ""type"": ""object"", ""required"": true, ""desc"": ""起始对象"" },
                    { ""name"": ""depth"",  ""type"": ""double"", ""unit"": ""m"", ""default"": ""0.02"" }
                ],
                ""preconditions"": [
                    { ""kind"": ""docKind"", ""value"": ""part"", ""hint"": ""仅零件文档"" }
                ],
                ""steps"": [
                    { ""member"": ""Models"", ""on"": ""{{target}}"" },
                    { ""member"": ""AddFiniteExtrudedProtrusion"", ""on"": ""$1"", ""args"": [""1"", ""@arr:$2"", ""2"", ""{{depth}}""] }
                ],
                ""verification"": { ""kind"": ""snapshotDiff"", ""name"": ""demo"", ""expect"": ""identical=true"" },
                ""derivedFrom"": ""README"",
                ""notes"": ""n""
            }");
            Assert.Equal("demo", s.Name);
            Assert.Equal("verified", s.Status);
            Assert.Equal(2, s.Version);
            Assert.Equal("modelChanging", s.Risk);
            Assert.Equal("2022", s.AnchorSe);
            Assert.Equal("part", s.AnchorSourceDoc);
            Assert.Equal(2, s.Inputs.Count);
            Assert.Equal("target", s.Inputs[0].Name);
            Assert.Equal("object", s.Inputs[0].Type);
            Assert.True(s.Inputs[0].Required);
            Assert.Equal("double", s.Inputs[1].Type);
            Assert.True(s.Inputs[1].HasDefault);
            Assert.Equal("0.02", s.Inputs[1].Default);
            Assert.Single(s.Preconditions);
            Assert.Equal("docKind", s.Preconditions[0].Kind);
            Assert.Equal(2, s.Steps.Count);
            Assert.Equal("Models", s.Steps[0].Member);
            Assert.Equal("{{target}}", s.Steps[0].On);
            Assert.Equal(4, s.Steps[1].Args.Length);
            Assert.Equal("@arr:$2", s.Steps[1].Args[1]);
            Assert.Equal("snapshotDiff", s.VerifyKind);
            Assert.Equal("identical=true", s.VerifyExpect);
            Assert.Equal("README", s.DerivedFrom);
        }

        [Fact]
        public void Parse_StatusIsTrimmed()
        {
            var s = Parse(@"{""name"":""r1"",""status"":"" verified "",""steps"":[{""member"":""X""}]}");
            Assert.Equal("verified", s.Status);
        }

        [Fact]
        public void Parse_StepsArgsNonStringCoerced()
        {
            var s = Parse(@"{""name"":""r1"",""steps"":[{""member"":""X"",""args"":[1,""a"",true]}]}");
            Assert.Equal(3, s.Steps[0].Args.Length);
            Assert.Equal("1", s.Steps[0].Args[0]);     // 数字 → ToString
            Assert.Equal("True", s.Steps[0].Args[2]);  // 布尔 → ToString
        }

        [Fact]
        public void Parse_VersionFromString()
        {
            var s = Parse(@"{""name"":""r1"",""version"":""3"",""steps"":[{""member"":""X""}]}");
            Assert.Equal(3, s.Version);
        }

        // ---------- Validator ----------

        [Fact]
        public void Validate_MinimalValid_NoIssues()
        {
            Assert.Empty(RecipeValidator.Validate(Valid()));
        }

        [Fact]
        public void Validate_ReadmeExample_SelfRefStepIsError()
        {
            // ★ 定性(2026-09-16 用户仲裁,经 InvokeTools.cs:156/171 核实):执行器对 @arr: 前缀
            // 内的 $N 本来就走 ResolveStepToken 展开为跨步引用——校验器报"$2 越界"与执行语义
            // 【完全一致】,不是校验器误报。错的是 recipes/README.md 标准示例本身:第 2 步 args
            // 引用 $2 = 引用自己,不合法(示例是完整 chain 的缩略展示,$N 编号没跟着改)。
            // 本用例把"自引用报 error"固化为正确行为;README 示例待修。
            var s = Parse(@"{
                ""name"": ""demo"",
                ""steps"": [
                    { ""member"": ""Models"", ""on"": ""{{target}}"" },
                    { ""member"": ""AddFiniteExtrudedProtrusion"", ""on"": ""$1"", ""args"": [""1"", ""@arr:$2"", ""2"", ""{{depth}}""] }
                ],
                ""inputs"": [
                    { ""name"": ""target"", ""type"": ""object"" },
                    { ""name"": ""depth"", ""type"": ""double"" }
                ]
            }");
            var issues = RecipeValidator.Validate(s);
            Assert.Contains(issues, i => i.Severity == "error" && i.Message.Contains("越界"));
            Assert.Single(issues);
        }

        [Fact]
        public void Validate_NameMissing_Error()
        {
            var s = Parse(@"{""steps"":[{""member"":""X""}]}");
            var issues = RecipeValidator.Validate(s);
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "name");
        }

        [Theory]
        [InlineData("a b")]
        [InlineData("a/b")]
        [InlineData("配方 名")]
        public void Validate_UnsafeName_Error(string name)
        {
            var s = Parse(@"{""name"":""" + name + @""",""steps"":[{""member"":""X""}]}");
            Assert.Contains(RecipeValidator.Validate(s), i => i.Severity == "error" && i.Where == "name");
        }

        [Theory]
        [InlineData("a")]
        [InlineData("A-b_2.c")]
        [InlineData("revolve_circle_profile")]
        [InlineData("名字")]   // IsLetterOrDigit 是 Unicode 语义,中文算"字母" → 被接受
                               // (recipes/README.md 写"只允许字母/数字"指 ASCII,与实现有出入——记录现状)
        public void IsSafeName_Accepts(string name)
        {
            Assert.True(RecipeValidator.IsSafeName(name));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("a b")]
        public void IsSafeName_Rejects(string name)
        {
            Assert.False(RecipeValidator.IsSafeName(name));
        }

        [Fact]
        public void IsSafeName_LengthBound()
        {
            string s64 = new string('a', 64);
            string s65 = new string('a', 65);
            Assert.True(RecipeValidator.IsSafeName(s64));
            Assert.False(RecipeValidator.IsSafeName(s65));
        }

        [Fact]
        public void Validate_StatusIllegal_Warn()
        {
            var s = Parse(@"{""name"":""r1"",""status"":""frozen"",""steps"":[{""member"":""X""}]}");
            Assert.Contains(RecipeValidator.Validate(s), i => i.Severity == "warn" && i.Where == "status");
        }

        [Fact]
        public void Validate_VerifiedWithoutVerification_Error()
        {
            var s = Parse(@"{""name"":""r1"",""status"":""verified"",""steps"":[{""member"":""X""}]}");
            Assert.Contains(RecipeValidator.Validate(s), i => i.Severity == "error" && i.Where == "verification");
        }

        [Fact]
        public void Validate_VerifiedWithVerification_Passes()
        {
            var s = Parse(@"{""name"":""r1"",""status"":""verified"",""steps"":[{""member"":""X""}],""verification"":{""kind"":""snapshotDiff"",""name"":""r1"",""expect"":""identical=true""}}");
            Assert.Empty(RecipeValidator.Validate(s));
        }

        [Fact]
        public void Validate_EmptySteps_Error()
        {
            var s = Parse(@"{""name"":""r1"",""steps"":[]}");
            Assert.Contains(RecipeValidator.Validate(s), i => i.Severity == "error" && i.Where == "steps");
        }

        [Fact]
        public void Validate_StepsOverThreshold_Warn()
        {
            var steps = string.Join(",", Enumerable.Range(0, 21)
                .Select(i => @"{""member"":""M" + i + @"""}"));
            var s = Parse(@"{""name"":""r1"",""steps"":[" + steps + "]}");
            Assert.Contains(RecipeValidator.Validate(s), i => i.Severity == "warn" && i.Where == "steps");
        }

        [Fact]
        public void Validate_StepsAtThreshold_NoWarn()
        {
            var steps = string.Join(",", Enumerable.Range(0, RecipeValidator.MaxRecommendedSteps)
                .Select(i => @"{""member"":""M" + i + @"""}"));
            var s = Parse(@"{""name"":""r1"",""steps"":[" + steps + "]}");
            Assert.DoesNotContain(RecipeValidator.Validate(s), i => i.Where == "steps");
        }

        [Fact]
        public void Validate_UndeclaredInput_Error()
        {
            var s = Parse(@"{""name"":""r1"",""steps"":[{""member"":""X"",""on"":""{{target}}""}]}");
            Assert.Contains(RecipeValidator.Validate(s),
                i => i.Severity == "error" && i.Message.Contains("未声明的 input"));
        }

        [Fact]
        public void Validate_StepRefForward_Error()
        {
            // 第 1 步就引用 $1(自己) → 越界
            var s = Parse(@"{""name"":""r1"",""steps"":[{""member"":""X"",""on"":""$1""}]}");
            Assert.Contains(RecipeValidator.Validate(s), i => i.Severity == "error" && i.Message.Contains("越界"));
        }

        [Fact]
        public void Validate_StepRefBackward_Ok()
        {
            // 第 2 步引用 $1 → 合法
            var s = Parse(@"{""name"":""r1"",""steps"":[{""member"":""X""},{""member"":""Y"",""on"":""$1""}]}");
            Assert.DoesNotContain(RecipeValidator.Validate(s), i => i.Message.Contains("越界"));
        }

        [Fact]
        public void Validate_InputTypeIllegal_Error()
        {
            var s = Parse(@"{""name"":""r1"",""inputs"":[{""name"":""a"",""type"":""float""}],""steps"":[{""member"":""X""}]}");
            Assert.Contains(RecipeValidator.Validate(s),
                i => i.Severity == "error" && i.Where == "inputs.a" && i.Message.Contains("type"));
        }

        [Fact]
        public void Validate_InputDuplicateName_Error()
        {
            var s = Parse(@"{""name"":""r1"",""inputs"":[{""name"":""a""},{""name"":""a""}],""steps"":[{""member"":""X""}]}");
            Assert.Contains(RecipeValidator.Validate(s),
                i => i.Severity == "error" && i.Message.Contains("重复"));
        }

        [Fact]
        public void Validate_PreconditionKindIllegal_Error()
        {
            var s = Parse(@"{""name"":""r1"",""preconditions"":[{""kind"":""magic"",""value"":""x""}],""steps"":[{""member"":""X""}]}");
            Assert.Contains(RecipeValidator.Validate(s),
                i => i.Severity == "error" && i.Where == "preconditions");
        }

        [Fact]
        public void Validate_PreconditionNeedsValue_Error()
        {
            // docKind 需要 value,漏了 → error
            var s = Parse(@"{""name"":""r1"",""preconditions"":[{""kind"":""docKind""}],""steps"":[{""member"":""X""}]}");
            Assert.Contains(RecipeValidator.Validate(s),
                i => i.Severity == "error" && i.Where == "preconditions.docKind");
        }

        [Fact]
        public void Validate_PreconditionNoValueKinds_Ok()
        {
            // selectionNonEmpty / handleExists 是无参检查,不需要 value
            var s = Parse(@"{""name"":""r1"",""preconditions"":[{""kind"":""selectionNonEmpty""},{""kind"":""handleExists""}],""steps"":[{""member"":""X""}]}");
            Assert.DoesNotContain(RecipeValidator.Validate(s), i => i.Severity == "error");
        }

        [Fact]
        public void Validate_InputUnused_Warn()
        {
            var s = Parse(@"{""name"":""r1"",""inputs"":[{""name"":""a"",""type"":""string""}],""steps"":[{""member"":""X""}]}");
            Assert.Contains(RecipeValidator.Validate(s),
                i => i.Severity == "warn" && i.Message.Contains("没有引用"));
        }

        [Fact]
        public void Validate_RequiredWithDefault_Warn()
        {
            var s = Parse(@"{""name"":""r1"",""inputs"":[{""name"":""a"",""type"":""string"",""required"":true,""default"":""x""}],""steps"":[{""member"":""X"",""on"":""{{a}}""}]}");
            Assert.Contains(RecipeValidator.Validate(s),
                i => i.Severity == "warn" && i.Message.Contains("required"));
        }

        [Fact]
        public void Validate_StepMissingMember_Error()
        {
            var s = Parse(@"{""name"":""r1"",""steps"":[{""on"":""$$""}]}");
            Assert.Contains(RecipeValidator.Validate(s),
                i => i.Severity == "error" && i.Where == "steps[0]" && i.Message.Contains("member"));
        }
    }
}
