using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>一个码的说明:官方符号名 + 中文人话。</summary>
internal sealed class CodeInfo
{
    internal string Name;

    internal string Meaning;
}

/// <summary>
/// 错误码目录(P1-1):把 AI 看不懂的裸码翻译成「码 + 官方名 + 人话」。
///
/// 覆盖两类码:
///   ① COM/Win32 HRESULT(0x80004005 一类)—— 项目里最高频、AI 最抓瞎的;
///   ② Solid Edge 状态/错误枚举(igFeatureWarned 一类)。
///
/// 数据是【嵌入资源】(se_code_catalog.json,由 scripts/gen_code_catalog.py 生成),
/// 编译进 dll —— 刻意不学 recipes 走外部文件:错误码表是只读参考、不需要现场改,而 MCP
/// 有多个 live 部署目录(bin\Release / obj\deploy_out_mcp),外部文件极易出现
/// "改了 A 目录没生效,实际跑的是 B 目录"那类老坑(项目 2026-10-03 踩过)。
///
/// 查不到时的态度:HRESULT 落到系统消息(Marshal.GetExceptionForHR)兜底;仍然拿不到就
/// 返回 null,由调用方原样输出裸码 —— 不猜、不编。
/// </summary>
internal static class CodeCatalog
{
    private const string ResourceName = "se_code_catalog.json";

    private static readonly object Sync = new object();

    private static bool _loaded;

    /// <summary>key = "0x80004005"(8 位大写十六进制,与生成脚本一致)。</summary>
    private static Dictionary<string, CodeInfo> _hresults;

    /// <summary>枚举名 → (值(十进制字符串) → 说明)。</summary>
    private static Dictionary<string, Dictionary<string, CodeInfo>> _seEnums;

    /// <summary>枚举名 → 该枚举的中文说明(可能为 null)。</summary>
    private static Dictionary<string, string> _seEnumMeaning;

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }
            _hresults = new Dictionary<string, CodeInfo>(StringComparer.OrdinalIgnoreCase);
            _seEnums = new Dictionary<string, Dictionary<string, CodeInfo>>(StringComparer.OrdinalIgnoreCase);
            _seEnumMeaning = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Assembly asm = typeof(CodeCatalog).Assembly;
                using (Stream s = asm.GetManifestResourceStream(ResourceName))
                {
                    if (s != null)
                    {
                        using (JsonDocument doc = JsonDocument.Parse(s))
                        {
                            JsonElement root = doc.RootElement;
                            if (root.TryGetProperty("hresults", out JsonElement hr) && hr.ValueKind == JsonValueKind.Object)
                            {
                                foreach (JsonProperty p in hr.EnumerateObject())
                                {
                                    _hresults[p.Name] = ReadInfo(p.Value);
                                }
                            }
                            if (root.TryGetProperty("seEnums", out JsonElement se) && se.ValueKind == JsonValueKind.Object)
                            {
                                foreach (JsonProperty p in se.EnumerateObject())
                                {
                                    Dictionary<string, CodeInfo> members = new Dictionary<string, CodeInfo>(StringComparer.Ordinal);
                                    if (p.Value.TryGetProperty("meaning", out JsonElement em) && em.ValueKind == JsonValueKind.String)
                                    {
                                        _seEnumMeaning[p.Name] = em.GetString();
                                    }
                                    if (p.Value.TryGetProperty("members", out JsonElement mem) && mem.ValueKind == JsonValueKind.Object)
                                    {
                                        foreach (JsonProperty m in mem.EnumerateObject())
                                        {
                                            members[m.Name] = ReadInfo(m.Value);
                                        }
                                    }
                                    _seEnums[p.Name] = members;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // 资源缺失/损坏都静默降级:查询返回 null,调用方原样输出裸码,绝不因码表问题把正常流程搞挂。
            }
            _loaded = true;
        }
    }

    private static CodeInfo ReadInfo(JsonElement el)
    {
        CodeInfo info = new CodeInfo();
        if (el.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String)
        {
            info.Name = n.GetString();
        }
        if (el.TryGetProperty("meaning", out JsonElement m) && m.ValueKind == JsonValueKind.String)
        {
            info.Meaning = m.GetString();
        }
        return info;
    }

    /// <summary>HRESULT 的规范键("0x" + 8 位大写十六进制)。</summary>
    private static string HrKey(int hr)
    {
        return "0x" + (hr & 0xFFFFFFFF).ToString("X8");
    }

    /// <summary>按 HRESULT 查目录(含 SE 常用码如 E_FAIL/E_ACCESSDENIED)。查不到返回 null。</summary>
    internal static CodeInfo FindHResult(int hr)
    {
        EnsureLoaded();
        CodeInfo info;
        return (_hresults != null && _hresults.TryGetValue(HrKey(hr), out info)) ? info : null;
    }

    /// <summary>
    /// 把 HRESULT 说成人话,形如:
    ///   "0x80004005 (E_FAIL):未指定的错误(通用失败)"
    /// 目录里没有时退到系统消息(如 "拒绝访问。"),仍拿不到返回 null。
    /// </summary>
    internal static string DescribeHResult(int hr)
    {
        string key = HrKey(hr);
        CodeInfo info = FindHResult(hr);
        if (info != null && !string.IsNullOrEmpty(info.Name))
        {
            string tail = string.IsNullOrEmpty(info.Meaning) ? "" : (":" + info.Meaning);
            return key + " (" + info.Name + ")" + tail;
        }
        if (info != null && !string.IsNullOrEmpty(info.Meaning))
        {
            return key + ":" + info.Meaning;
        }
        string sys = SystemMessage(hr);
        return (sys == null) ? null : (key + ":" + sys);
    }

    /// <summary>取系统本地化消息(Marshal.GetExceptionForHR)。失败返回 null。</summary>
    private static string SystemMessage(int hr)
    {
        try
        {
            Exception ex = Marshal.GetExceptionForHR(hr);
            if (ex == null || string.IsNullOrWhiteSpace(ex.Message))
            {
                return null;
            }
            string m = ex.Message;
            // .NET 会在尾部拼 "(Exception from HRESULT: 0x...)",去掉,只留人话主体。
            int i = m.IndexOf("(Exception from HRESULT", StringComparison.OrdinalIgnoreCase);
            if (i > 0)
            {
                m = m.Substring(0, i);
            }
            m = m.Trim();
            return m.Length == 0 ? null : m;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按 SE 枚举名 + 值查成员(如 FeatureStatusConstants / 1216476312)。查不到返回 null。</summary>
    internal static CodeInfo FindSeEnumMember(string enumName, long value)
    {
        if (string.IsNullOrEmpty(enumName))
        {
            return null;
        }
        EnsureLoaded();
        Dictionary<string, CodeInfo> members;
        if (_seEnums == null || !_seEnums.TryGetValue(enumName, out members) || members == null)
        {
            return null;
        }
        CodeInfo info;
        return members.TryGetValue(value.ToString(), out info) ? info : null;
    }

    /// <summary>该 SE 枚举的中文说明(可能为 null)。</summary>
    internal static string SeEnumMeaning(string enumName)
    {
        if (string.IsNullOrEmpty(enumName))
        {
            return null;
        }
        EnsureLoaded();
        string meaning;
        return (_seEnumMeaning != null && _seEnumMeaning.TryGetValue(enumName, out meaning)) ? meaning : null;
    }

    /// <summary>
    /// 把 SE 状态码说成人话,形如:"igFeatureWarned(1216476312):特征已生成但有警告(保留,几何可用)"。
    /// 查不到返回 null。
    /// </summary>
    internal static string DescribeSeEnum(string enumName, long value)
    {
        CodeInfo info = FindSeEnumMember(enumName, value);
        if (info == null || string.IsNullOrEmpty(info.Name))
        {
            return null;
        }
        string tail = string.IsNullOrEmpty(info.Meaning) ? "" : (":" + info.Meaning);
        return info.Name + "(" + value + ")" + tail;
    }

    /// <summary>测试专用:清空缓存,使下一次查询重新从资源加载。</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _loaded = false;
            _hresults = null;
            _seEnums = null;
            _seEnumMeaning = null;
        }
    }
}
