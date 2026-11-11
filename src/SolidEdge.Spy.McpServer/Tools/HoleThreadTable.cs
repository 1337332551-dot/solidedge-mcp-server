using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 一行螺纹规格 = Solid Edge 孔数据库「螺纹孔」工作表的一行。
    /// 数值单位全是【毫米】，传给 SE 之前要 ÷1000。
    /// </summary>
    public sealed class ThreadRow
    {
        /// <summary>孔标准（数据库文件名，如 "GB Metric"）。</summary>
        public string Standard;

        /// <summary>螺纹类型：1=标准螺纹、2=直管螺纹、3=锥管螺纹（数据库首列的原值）。</summary>
        public int Kind;

        /// <summary>子类型（= HoleData.SubType），如 "标准螺纹" / "直管螺纹" / "锥管螺纹"。</summary>
        public string Subtype;

        /// <summary>螺纹族，如 "M"、"Mx1.25"、"G"、"Rp"。</summary>
        public string Family;

        /// <summary>规格原始写法（= HoleData.Size），如 "M10"、"M10 x 1.25"、"Rp 1/2-14"。</summary>
        public string Size;

        public double NominalMm;      // 标称直径
        public double TapDrillMm;     // 丝锥直径（底孔）
        public double MinorMm;        // 内螺纹小径
        public double ExternalMm;     // 外螺纹小径
        public double PitchMm;        // 螺距

        /// <summary>
        /// 传给 HoleData.ThreadSetting：
        /// 164=igRegularThread、165=igStraightPipeThread、166=igTaperedPipeThread。
        /// </summary>
        public int ThreadSetting
        {
            get { return Kind == 2 ? 165 : (Kind == 3 ? 166 : 164); }
        }

        /// <summary>底孔直径：表里有丝锥直径就用它，否则退内螺纹小径，最后退标称直径。</summary>
        public double DrillMm
        {
            get { return TapDrillMm > 0 ? TapDrillMm : (MinorMm > 0 ? MinorMm : NominalMm); }
        }

        /// <summary>UI 上的"螺纹选项"中文名，仅用于回传给人看。</summary>
        public static string OptionName(string option)
        {
            string o = (option ?? "").Trim().ToLowerInvariant();
            if (o == "tapdrill" || o == "tap_drill") return "丝锥直径";
            if (o == "nominal") return "标称直径";
            return "内螺纹小径";
        }

        /// <summary>
        /// 建模用的孔直径（毫米）。SE 是按"螺纹选项"决定实际孔径的：
        ///   minor（默认）= 内螺纹小径、tapdrill = 丝锥直径、nominal = 标称直径。
        /// 实测（M10，option=minor）→ 模型里量到 Ø8.376，与内螺纹小径完全一致。
        /// </summary>
        public double ModelDiameterMm(string option)
        {
            string o = (option ?? "").Trim().ToLowerInvariant();
            if (o == "tapdrill" || o == "tap_drill") return DrillMm;
            if (o == "nominal") return NominalMm;
            return MinorMm > 0 ? MinorMm : DrillMm;
        }

        /// <summary>"M10（GB Metric 标准螺纹，底孔Ø8.5，小径Ø8.376，螺距1.5）" 这类回显。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Size).Append("（").Append(Standard).Append(' ').Append(Subtype);
            sb.Append("，标称Ø").Append(F(NominalMm)).Append("，底孔Ø").Append(F(DrillMm));
            if (MinorMm > 0) sb.Append("，内螺纹小径Ø").Append(F(MinorMm));
            if (PitchMm > 0) sb.Append("，螺距").Append(F(PitchMm));
            sb.Append("）");
            return sb.ToString();
        }

        private static string F(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// SE 孔数据库「螺纹孔」表的只读副本 + 规格查找。
    ///
    /// 为什么要内置一份数据：Solid Edge 的 HoleDataCollection.AddEx 会自己查库，但只对
    /// 简单孔/沉头孔/埋头孔有效——螺纹孔的 HoleType=igTappedHole(37) 会被静默夹成 36，
    /// 查询直接失效（实测）。所以螺纹孔这几个直径必须由我们自己按库填进 HoleData。
    ///
    /// 数据来源：SE 安装目录下的 Preferences\Holes\&lt;标准&gt;.xlsx（GB Metric 已内置，
    /// 生成脚本见 HoleThreadTable.Data.cs 文件头）。现在只内置 GB Metric；要加其它标准，
    /// 生成同样格式的数组并在 <see cref="BuildMaps"/> 里登记即可。
    ///
    /// 用法：HoleThreadTable.TryFind("GB Metric", "M10", ...) / "Rp1/2" 都能查到。
    /// </summary>
    public static partial class HoleThreadTable
    {
        /// <summary>standard 省略时的默认值。</summary>
        public const string DefaultStandard = "GB Metric";

        private static readonly object Gate = new object();

        /// <summary>标准名 → (归一化规格 → 行)。惰性构建，只建一次。</summary>
        private static Dictionary<string, Dictionary<string, ThreadRow>> _maps;

        /// <summary>当前内置的标准名清单（用于报错提示）。</summary>
        public static readonly string[] SupportedStandards = { "GB Metric" };

        /// <summary>
        /// 按标准 + 规格找一行。找不到时 error 里带相近规格建议。
        /// 匹配顺序：完全匹配 → 补 0 变体（M0.25→M.25）→ Rc→R → 前缀（缺螺距/牙数）。
        /// </summary>
        public static bool TryFind(string standard, string size, out ThreadRow row, out string error)
        {
            row = null;
            error = null;

            if (string.IsNullOrWhiteSpace(size))
            {
                error = "thread 不能为空。写法如 thread:\"M10\"（粗牙）、thread:\"M10x1.25\"（细牙）、thread:\"Rp1/2\"（管螺纹）。";
                return false;
            }

            string std = string.IsNullOrWhiteSpace(standard) ? DefaultStandard : standard.Trim();
            var map = GetMap(std);
            if (map == null)
            {
                error = "标准 \"" + std + "\" 没有内置螺纹表，目前只内置 " + string.Join(" / ", SupportedStandards) +
                        "。其它标准（ISO/DIN/ANSI…）可在 HoleThreadTable.Data.cs 里按同样格式补一份。";
                return false;
            }

            string key = Norm(size);
            if (map.TryGetValue(key, out row)) return true;

            // SE 库把 1mm 以下的公制写成 "M.25"，用户通常写 "M0.25"。
            if (key.StartsWith("M0.", StringComparison.Ordinal) && key.Length > 3)
            {
                if (map.TryGetValue("M" + key.Substring(2), out row)) return true;
            }

            // 锥管螺纹表里的规格是 R 1/2-14，UI 上叫 Rc 1/2；用户很可能写 Rc1/2。
            if (key.StartsWith("RC", StringComparison.Ordinal) && key.Length > 3)
            {
                if (map.TryGetValue("R" + key.Substring(2), out row)) return true;
            }

            // 前缀：省略螺距/牙数，如 "Rp1/2" → "Rp 1/2-14"、"M10x1.25" 已在上面完全匹配。
            var hits = new List<ThreadRow>();
            foreach (var kv in map)
            {
                if (kv.Key.StartsWith(key + "-", StringComparison.Ordinal) ||
                    kv.Key.StartsWith(key + "X", StringComparison.Ordinal))
                    hits.Add(kv.Value);
            }
            if (hits.Count == 1)
            {
                row = hits[0];
                return true;
            }
            if (hits.Count > 1)
            {
                error = "螺纹规格 \"" + size + "\" 在 " + std + " 里有多种牙型，请写全：" +
                        string.Join("、", hits.ConvertAll(h => h.Size).ToArray()) + "。";
                return false;
            }

            string[] near = Suggest(std, size, 8);
            error = "螺纹规格 \"" + size + "\" 在 " + std + " 里找不到。" +
                    (near.Length > 0 ? "相近的有：" + string.Join("、", near) + "。" : "") +
                    "写法示例：M10（粗牙）/ M10x1.25（细牙）/ Rp1/2（直管）/ R1/2（锥管）。";
            return false;
        }

        /// <summary>按前缀给出相近规格（给报错提示用）。</summary>
        public static string[] Suggest(string standard, string size, int max)
        {
            var map = GetMap(string.IsNullOrWhiteSpace(standard) ? DefaultStandard : standard.Trim());
            if (map == null) return Array.Empty<string>();

            string key = Norm(size);
            if (key.Length > 0 && key[key.Length - 1] == 'X') key = key.Substring(0, key.Length - 1);

            var list = new List<string>();
            // 先按"同族前缀"捞（M10 → M10 / M10x1.25…），再按"首字母"捞（Rp → Rp…）
            foreach (var kv in map)
                if (key.Length > 0 && kv.Key.StartsWith(key, StringComparison.Ordinal) && list.Count < max)
                    list.Add(kv.Value.Size);

            if (list.Count == 0)
            {
                string head = key.Length > 0 ? key.Substring(0, 1) : "";
                foreach (var kv in map)
                    if (kv.Key.StartsWith(head, StringComparison.Ordinal) && list.Count < max)
                        list.Add(kv.Value.Size);
            }
            return list.ToArray();
        }

        /// <summary>标准名是否已内置。</summary>
        public static bool IsSupported(string standard)
        {
            return GetMap(string.IsNullOrWhiteSpace(standard) ? DefaultStandard : standard.Trim()) != null;
        }

        private static Dictionary<string, ThreadRow> GetMap(string standard)
        {
            BuildMaps();
            var maps = _maps;
            Dictionary<string, ThreadRow> map;
            return maps.TryGetValue(standard, out map) ? map : null;
        }

        private static void BuildMaps()
        {
            if (_maps != null) return;
            lock (Gate)
            {
                if (_maps != null) return;
                var maps = new Dictionary<string, Dictionary<string, ThreadRow>>(StringComparer.OrdinalIgnoreCase);
                maps[DefaultStandard] = BuildMap(DefaultStandard, GbMetricData);
                _maps = maps;
            }
        }

        private static Dictionary<string, ThreadRow> BuildMap(string standard, string[] data)
        {
            var map = new Dictionary<string, ThreadRow>(data.Length, StringComparer.Ordinal);
            foreach (string line in data)
            {
                string[] f = line.Split('|');
                if (f.Length < 9) continue;

                var row = new ThreadRow
                {
                    Standard = standard,
                    Kind = (int)Num(f[0]),
                    Subtype = f[1],
                    Family = f[2],
                    Size = f[3],
                    NominalMm = Num(f[4]),
                    TapDrillMm = Num(f[5]),
                    MinorMm = Num(f[6]),
                    ExternalMm = Num(f[7]),
                    PitchMm = Num(f[8])
                };

                string key = Norm(row.Size);
                if (key.Length == 0) continue;

                // 同一规格在表里可能出现在多个螺纹类型里（如 "M10 x 1" 标准螺纹 + 锥管螺纹），
                // 默认取"标准螺纹"那一行——机械上人们说 M10x1 指的都是普通细牙螺纹。
                ThreadRow exist;
                if (map.TryGetValue(key, out exist) && exist.Kind <= row.Kind) continue;
                map[key] = row;
            }
            return map;
        }

        private static double Num(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
        }

        /// <summary>
        /// 规格归一化：去空白、转大写、"×"/"*" 统一成 "X"。
        /// 例："Rp 1/2-14" → "RP1/2-14"、"m10 x 1.25" → "M10X1.25"。
        /// </summary>
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                char u = char.ToUpperInvariant(c);
                if (u == '×' || u == '*' || u == '＊') u = 'X';
                sb.Append(u);
            }
            return sb.ToString();
        }
    }
}
