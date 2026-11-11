using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// skipIfZero（步骤级"返回值为 0 则跳过本步"）的两层测试，均不碰 COM：
    /// ① 校验器层 RecipeValidator —— 写法合法性 + $N 越界检查；
    /// ② 运行时辅助层 InvokeTools.ParseSkipIfZero / IsStepValueZero / FirstSkippedDependency。
    /// 契约见 recipes/README.md §二 steps。
    /// </summary>
    public class SkipIfZeroTests
    {
        private static RecipeSpec Parse(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                return RecipeSpecParser.Parse(doc.RootElement, "test.json");
            }
        }

        private static JsonElement El(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                return doc.RootElement.Clone();
            }
        }

        // ---------------- ① 校验器层 ----------------

        [Fact]
        public void Validate_合法skipIfZero_无报错()
        {
            // 第 2 步以第 1 步的返回值为零判断依据：$1 是前序步骤，合法
            var s = Parse(@"{""name"":""r1"",""steps"":[
                {""member"":""Count"",""on"":""$$""},
                {""member"":""Item"",""on"":""$$"",""args"":[""1""],""skipIfZero"":""$1""}]}");
            Assert.DoesNotContain(RecipeValidator.Validate(s), i => i.Severity == "error");
        }

        [Fact]
        public void Validate_skipIfZero引用自身_报越界错误()
        {
            // $2 在第 2 步引用第 2 步 —— 只能引用前序步骤
            var s = Parse(@"{""name"":""r1"",""steps"":[
                {""member"":""Count"",""on"":""$$""},
                {""member"":""Item"",""on"":""$$"",""skipIfZero"":""$2""}]}");
            var issues = RecipeValidator.Validate(s);
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "steps[1].skipIfZero");
        }

        [Fact]
        public void Validate_skipIfZero引用后序步_报越界错误()
        {
            var s = Parse(@"{""name"":""r1"",""steps"":[
                {""member"":""Item"",""on"":""$$"",""skipIfZero"":""$3""},
                {""member"":""Count"",""on"":""$$""},
                {""member"":""Count"",""on"":""$$""}]}");
            var issues = RecipeValidator.Validate(s);
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "steps[0].skipIfZero");
        }

        [Theory]
        [InlineData("\"$1\"")]      // 合法
        [InlineData("\" $1 \"")]    // 允许前后空白
        public void Validate_skipIfZero写法合法_该字段无错误(string raw)
        {
            string json = @"{""name"":""r1"",""steps"":[{""member"":""Count"",""on"":""$$""},{""member"":""Item"",""on"":""$$"",""skipIfZero"":" + raw + @"}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.DoesNotContain(issues, i => i.Where == "steps[1].skipIfZero" && i.Severity == "error");
        }

        [Theory]
        [InlineData("\"0\"")]          // 不是 $N
        [InlineData("\"$1,$2\"")]      // 多个引用
        [InlineData("\"abc\"")]
        [InlineData("\"$$\"")]         // 起始对象不是步骤
        public void Validate_skipIfZero写法非法_报错(string raw)
        {
            string json = @"{""name"":""r1"",""steps"":[{""member"":""Count"",""on"":""$$""},{""member"":""Item"",""on"":""$$"",""skipIfZero"":" + raw + @"}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Where == "steps[1].skipIfZero" && i.Severity == "error");
        }

        [Fact]
        public void Parse_skipIfZero字段被读进RecipeStep()
        {
            var s = Parse(@"{""name"":""r1"",""steps"":[{""member"":""Count"",""on"":""$$""},{""member"":""Item"",""on"":""$$"",""skipIfZero"":""$1""}]}");
            Assert.Equal("$1", s.Steps[1].SkipIfZero);
            Assert.Null(s.Steps[0].SkipIfZero);
        }

        // ---------------- ② 运行时辅助层 ----------------

        [Theory]
        [InlineData("\"$9\"", 9)]
        [InlineData("9", 9)]
        [InlineData("\"$1\"", 1)]
        public void ParseSkipIfZero_解析引用步号(string raw, int expected)
        {
            JsonElement step = El(@"{""member"":""Item"",""skipIfZero"":" + raw + "}");
            Assert.Equal(expected, InvokeTools.ParseSkipIfZero(step));
        }

        [Theory]
        [InlineData("{}")]                              // 未声明
        [InlineData(@"{""skipIfZero"":""abc""}")]         // 非数字
        [InlineData(@"{""skipIfZero"":""0""}")]           // 0 步不存在
        public void ParseSkipIfZero_无声明或非法_返回0(string json)
        {
            Assert.Equal(0, InvokeTools.ParseSkipIfZero(El(json)));
        }

        /// <summary>构造一条"某步已完成"的结果 JSON（与 se_invoke_chain 的 steps 条目同构）。</summary>
        private static List<object> ResultsWith(params string[] values)
        {
            var list = new List<object>();
            foreach (string v in values)
            {
                string json = @"{""status"":""ok"",""member"":""Count"",""returnValue"":{""name"":""return"",""type"":""Int32"",""value"":" + (v == null ? "null" : "\"" + v + "\"") + "}}";
                list.Add(El(json));
            }
            return list;
        }

        [Theory]
        [InlineData("0", true)]
        [InlineData("0.0", true)]
        [InlineData("1", false)]
        [InlineData("2", false)]
        [InlineData("null", false)]   // 读不到值
        [InlineData("abc", false)]    // 非数值
        public void IsStepValueZero_判定(string value, bool expected)
        {
            var results = ResultsWith(value);
            Assert.Equal(expected, InvokeTools.IsStepValueZero(results, 1));
        }

        [Fact]
        public void IsStepValueZero_步号越界或非JsonElement_返回false()
        {
            var results = ResultsWith("0");
            Assert.False(InvokeTools.IsStepValueZero(results, 0));
            Assert.False(InvokeTools.IsStepValueZero(results, 2));
            Assert.False(InvokeTools.IsStepValueZero(new List<object> { null }, 1));
        }

        [Fact]
        public void IsStepValueZero_跳过步的占位null_返回false()
        {
            // 跳过的步在 stepResults 里是 null，不应被误判成"值为 0"
            var results = new List<object> { null, El(@"{""status"":""ok"",""returnValue"":{""value"":""5""}}") };
            Assert.False(InvokeTools.IsStepValueZero(results, 1));
        }

        [Fact]
        public void FirstSkippedDependency_on引用被跳步_命中()
        {
            var skipped = new HashSet<int> { 5 };
            JsonElement step = El(@"{""member"":""Name"",""on"":""$5""}");
            Assert.Equal(5, InvokeTools.FirstSkippedDependency(step, null, skipped));
        }

        [Fact]
        public void FirstSkippedDependency_args引用被跳步_命中()
        {
            var skipped = new HashSet<int> { 6 };
            JsonElement step = El(@"{""member"":""Add"",""on"":""$$"",""args"":[""1"",""$6""]}");
            Assert.Equal(6, InvokeTools.FirstSkippedDependency(step, new[] { "1", "$6" }, skipped));
        }

        [Fact]
        public void FirstSkippedDependency_args数组引用被跳步_命中()
        {
            var skipped = new HashSet<int> { 8 };
            JsonElement step = El(@"{""member"":""Add"",""on"":""$$""}");
            Assert.Equal(8, InvokeTools.FirstSkippedDependency(step, new[] { "@arr:$8" }, skipped));
        }

        [Fact]
        public void FirstSkippedDependency_无被跳步依赖_返回0()
        {
            var skipped = new HashSet<int> { 5 };
            JsonElement step = El(@"{""member"":""Status"",""on"":""$3""}");
            Assert.Equal(0, InvokeTools.FirstSkippedDependency(step, new[] { "$3" }, skipped));
        }

        [Fact]
        public void FirstSkippedDependency_没有跳过的步_返回0()
        {
            JsonElement step = El(@"{""member"":""Status"",""on"":""$1""}");
            Assert.Equal(0, InvokeTools.FirstSkippedDependency(step, null, new HashSet<int>()));
            Assert.Equal(0, InvokeTools.FirstSkippedDependency(step, null, null));
        }

        [Fact]
        public void FirstSkippedDependency_引用不存在的步号_不误判()
        {
            // args 里出现字面量 "$99"（越界），若它不在 skipped 集合里就不该命中
            var skipped = new HashSet<int> { 5 };
            JsonElement step = El(@"{""member"":""Add"",""on"":""$$""}");
            Assert.Equal(0, InvokeTools.FirstSkippedDependency(step, new[] { "$99" }, skipped));
        }

        [Fact]
        public void HasOwnTarget_判定是否显式给了on()
        {
            // 显式 on（字符串）→ true
            Assert.True(InvokeTools.HasOwnTarget(El(@"{""member"":""Name"",""on"":""$5""}")));
            // 省略 on → false（会走"默认接上一步返回值"分支）
            Assert.False(InvokeTools.HasOwnTarget(El(@"{""member"":""Name""}")));
            // on 非字符串 → 同样走默认分支
            Assert.False(InvokeTools.HasOwnTarget(El(@"{""member"":""Name"",""on"":3}")));
        }
    }
}
