using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 自动还原点 hook 测试(2026-10-06)。只测纯 git 核心(EnsurePoint/指纹/清单/清理),
    /// 用临时目录真跑 git;不测 BeforeWrite 的 COM 路径(那需要 SE,归真机验证)。
    /// 合同:
    /// ① 目录指纹稳定、不同目录不碰撞、无非法字符;
    /// ② 首次自动建仓+打点,tag 以 -auto 结尾;
    /// ③ 无变化不重复打点;④ 内容变化打新点;
    /// ⑤ 清单只含模型扩展名(.txt 等不进);
    /// ⑥ 滚动清理只删 rp-*-auto,手动 rp-* 标签不受影响。
    /// </summary>
    public class RestorePointHookTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dir;
        private readonly string _repoDir;

        public RestorePointHookTests()
        {
            RestorePointHook.ResetForTests();
            RestorePointHook.Enabled = true;
            RestorePointHook.Keep = 5;
            _root = Path.Combine(Path.GetTempPath(), "rp-tests-" + Guid.NewGuid().ToString("N"));
            _dir = Path.Combine(_root, "model");
            Directory.CreateDirectory(_dir);
            _repoDir = Path.Combine(_root, "repo.git");
        }

        public void Dispose()
        {
            RestorePointHook.ResetForTests();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---------- 工具 ----------

        private static void WriteFile(string path, string content)
        {
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        private static void RunGit(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(60000);
                Assert.Equal(0, p.ExitCode);
            }
        }

        private string[] GitTags()
        {
            return RestorePointHook.AutoTags(_dir, _repoDir);
        }

        private bool TagExists(string tag)
        {
            // 不能用 AutoTags(只列 *-auto);手动标签用 git tag -l 精确查询
            var psi = new ProcessStartInfo
            {
                FileName = "git.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in new[] { "-C", _dir, "--git-dir=" + _repoDir, "--work-tree=" + _dir, "tag", "-l", tag })
            {
                psi.ArgumentList.Add(a);
            }
            using (var p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                p.WaitForExit(60000);
                return so.Trim() == tag;
            }
        }

        // ---------- 指纹 ----------

        [Fact]
        public void 指纹_稳定且不同目录不同()
        {
            string f1 = RestorePointHook.RepoFingerprint(_dir);
            string f2 = RestorePointHook.RepoFingerprint(_dir + Path.DirectorySeparatorChar);
            string other = RestorePointHook.RepoFingerprint(Path.Combine(_root, "其他项目"));

            Assert.Equal(f1, f2);                       // 尾部分隔符不影响
            Assert.NotEqual(f1, other);                 // 不同目录不碰撞
            Assert.Matches(@"^[\w.\-]+-[0-9a-f]{8}$", f1); // 清洗体 + 尾部 8 位 hex 指纹
            Assert.True(f1.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'),
                "指纹含非法字符: " + f1);
        }

        // ---------- 建仓/打点 ----------

        [Fact]
        public void 首次调用自动建仓并打点()
        {
            WriteFile(Path.Combine(_dir, "a.par"), "v1");
            WriteFile(Path.Combine(_dir, "b.asm"), "v1");

            var r = RestorePointHook.EnsurePoint(_dir, _repoDir, "se_model_build",
                RestorePointHook.ModelFiles(_dir));

            Assert.True(r.Committed);
            Assert.NotNull(r.Tag);
            Assert.EndsWith("-auto", r.Tag);
            Assert.StartsWith("rp-", r.Tag);
            Assert.True(Directory.Exists(_repoDir), "仓库目录未创建");
            Assert.Equal(1, GitTags().Length);
        }

        [Fact]
        public void 无变化不重复打点()
        {
            WriteFile(Path.Combine(_dir, "a.par"), "v1");
            var r1 = RestorePointHook.EnsurePoint(_dir, _repoDir, "se_model_build", RestorePointHook.ModelFiles(_dir));
            var r2 = RestorePointHook.EnsurePoint(_dir, _repoDir, "se_invoke_member", RestorePointHook.ModelFiles(_dir));

            Assert.True(r1.Committed);
            Assert.False(r2.Committed, "无变化不应重复打点");
            Assert.Equal(1, GitTags().Length);
        }

        [Fact]
        public void 内容变化打新点()
        {
            WriteFile(Path.Combine(_dir, "a.par"), "v1");
            RestorePointHook.EnsurePoint(_dir, _repoDir, "se_model_build", RestorePointHook.ModelFiles(_dir));
            WriteFile(Path.Combine(_dir, "a.par"), "v2-改坏了");
            var r2 = RestorePointHook.EnsurePoint(_dir, _repoDir, "se_invoke_member", RestorePointHook.ModelFiles(_dir));

            Assert.True(r2.Committed);
            Assert.NotEqual(1, GitTags().Length);
            Assert.Equal(2, GitTags().Length);
        }

        [Fact]
        public void 清单只含模型扩展名()
        {
            WriteFile(Path.Combine(_dir, "a.par"), "v1");
            string[] files = RestorePointHook.ModelFiles(_dir);
            Assert.Equal(new[] { "a.par" }, files);

            // 首点(全新仓库 a.par 未跟踪,必提交)
            var r0 = RestorePointHook.EnsurePoint(_dir, _repoDir, "se_model_build", files);
            Assert.True(r0.Committed);

            // 此后只新增 txt(不在模型清单) → 对清单而言无变化,不打点
            WriteFile(Path.Combine(_dir, "note.txt"), "不该进清单");
            var r1 = RestorePointHook.EnsurePoint(_dir, _repoDir, "se_model_build", files);
            Assert.False(r1.Committed, "txt 不应触发打点");
            Assert.Equal(1, GitTags().Length);
        }

        // ---------- 滚动清理 ----------

        [Fact]
        public void 滚动清理只删auto且保留手动标签()
        {
            WriteFile(Path.Combine(_dir, "a.par"), "v0");
            var r0 = RestorePointHook.EnsurePoint(_dir, _repoDir, "se_model_build", RestorePointHook.ModelFiles(_dir));
            // 手动风格标签(非 -auto 结尾),挂在首点上
            RunGit("-C", _dir, "--git-dir=" + _repoDir, "--work-tree=" + _dir,
                "tag", "rp-20260101-000000-manual", r0.Tag);

            RestorePointHook.Keep = 2;
            for (int i = 1; i <= 4; i++)
            {
                WriteFile(Path.Combine(_dir, "a.par"), "v" + i);
                RestorePointHook.EnsurePoint(_dir, _repoDir, "se_invoke_member", RestorePointHook.ModelFiles(_dir));
            }

            string[] auto = GitTags();
            Assert.True(auto.Length <= 2, "auto 点应滚动保留 2 个,实际 " + auto.Length + ": " + string.Join(",", auto));
            Assert.True(TagExists("rp-20260101-000000-manual"), "手动标签被误删");
        }

        // ---------- 开关 ----------

        [Fact]
        public void 开关与保留数默认值()
        {
            RestorePointHook.ResetForTests();
            Assert.True(RestorePointHook.Enabled, "默认应开启(机制保证)");
            Assert.Equal(5, RestorePointHook.Keep);
        }

        [Fact]
        public void Read档工具BeforeWrite直接跳过不碰COM()
        {
            // Read 档:BeforeWrite 应在档位过滤处直接返回,即使 context 从未连过 SE 也不抛异常
            var hook = new RestorePointHook(new SolidEdgeContext());
            var ex = Record.Exception(() =>
                hook.BeforeWrite("se_get_document", default(System.Text.Json.JsonElement)));
            Assert.Null(ex);
        }
    }
}
