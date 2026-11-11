using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 记录"某个 op 的手册已经发给 AI 了",保证同一次 server 进程内只注入一次。
    ///
    /// 状态放在 server 层而不是工具里:stdio 单连接,进程内状态 ≈ 本次会话。
    /// 但它是【近似】——server 看不见 AI 的上下文,所以:
    ///   - IDE 新开对话但进程没重启 → 不会重新注入;
    ///   - 上下文被压缩裁掉早期 opDocs → 内容没了。
    /// 这两种情况都由返回值里的 opDocsNote 指针兜住(模型看到指针会主动查),
    /// 所以这里记错最多多一轮查询,不会让模型凭记忆瞎写。
    ///
    /// 若将来改成 HTTP 多客户端部署,本类要按 session 分桶,否则先到的会话会替后者"消耗"注入机会。
    /// </summary>
    public static class OpDocRegistry
    {
        private static readonly HashSet<string> _sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _gate = new object();

        /// <summary>
        /// 取本次应当注入的条目:只含首次出现、且在该域手册里存在的 op。
        /// 白名单外的名字(如写错的 op 触发 E101)必须跳过,否则会带出空条目。
        /// 返回空字典 = 本批没有新内容可注入,调用方据此不写 opDocs 字段。
        /// </summary>
        public static Dictionary<string, string> TakeUnsent(string domain,
            IReadOnlyDictionary<string, string> entries, IEnumerable<string> ops)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entries == null || ops == null) return result;

            lock (_gate)
            {
                foreach (var op in ops)
                {
                    if (string.IsNullOrEmpty(op)) continue;

                    string text;
                    if (!entries.TryGetValue(op, out text)) continue;

                    var key = domain + ":" + op;
                    if (!_sent.Add(key)) continue;      // 发过就跳过

                    result[op] = text;
                }
            }
            return result;
        }

        /// <summary>
        /// 一行指针,附在每次调用返回值里(约 40 字符)。
        /// 它是"注入记录只是加速器"的兜底:模型即便忘了内容,看到这里也能自己再查一次。
        /// ops 为空返回 null,调用方不写该字段。
        /// </summary>
        public static string Note(IEnumerable<string> ops, IEnumerable<string> injected, string argName = "features")
        {
            if (ops == null) return null;

            var names = ops.Where(o => !string.IsNullOrEmpty(o))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();
            if (names.Count == 0) return null;

            var fresh = injected == null
                ? new List<string>()
                : injected.Where(o => !string.IsNullOrEmpty(o))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

            var sb = new System.Text.StringBuilder();
            if (fresh.Count > 0)
                sb.Append("本次注入 ").Append(string.Join("/", fresh)).Append(" 的手册;");
            else
                sb.Append(string.Join("/", names)).Append(" 手册已注入(不重复);");

            sb.Append("需要重看传 ").Append(argName).Append(":[{\"op\":\"?\",\"name\":\"")
              .Append(fresh.Count > 0 ? fresh[0] : names[0])
              .Append("\"}],通用规则用 {\"op\":\"?\",\"topic\":\"common\"}");

            return sb.ToString();
        }

        /// <summary>清空注入记录。单测用;生产路径不调用(进程重启自然清空)。</summary>
        public static void Reset()
        {
            lock (_gate)
            {
                _sent.Clear();
            }
        }
    }
}
