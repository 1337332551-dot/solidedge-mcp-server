using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>面匹配结果(一张命中的面 + 给它打上的摘要)。</summary>
    internal sealed class FaceInfo
    {
        /// <summary>0-based 序号,与 Body.Faces(1) 枚举序一致。</summary>
        public int Index = -1;
        public int FaceId = -1;
        public string Type;
        public double? DiaMm;
        /// <summary>包围盒中心【米】—— 直接当装配约束点(CP1/CP2)用,别再乘除一次。</summary>
        public double[] CenterM;
        /// <summary>包围盒中心【mm】—— 只给人看。</summary>
        public double[] CenterMm;
        public double[] Normal;
        /// <summary>true=Normal 其实是圆柱轴向(圆柱没有外法向,用轴向顶替)。</summary>
        public bool NormalIsAxis;
        public bool? Boss;
        /// <summary>轴向长度(mm):圆柱按轴向投影,其余按包围盒最长边。</summary>
        public double AxialLengthMm;
        public object ComFace;

        public Dictionary<string, object> ToEcho()
        {
            var d = new Dictionary<string, object>
            {
                ["index"] = Index,
                ["faceId"] = FaceId,
                ["type"] = Type,
                ["diaMm"] = DiaMm,
                ["centerMm"] = CenterMm,
                ["normal"] = Normal,
                ["axialLengthMm"] = AxialLengthMm,
                ["boss"] = Boss
            };
            if (NormalIsAxis) d["normalIs"] = "axis";
            return d;
        }
    }

    /// <summary>
    /// 面选择条件 —— se_find_objects 的过滤字段 与 se_assembly_build 的 face1/face2 描述符 **共用这一份语义**。
    ///
    /// 为什么要有它(2026-10-25 P1):
    /// 装配约束原先只能按 Body.Faces 的 0-based 序号绑面。零件重建(加/删特征)后序号整体漂移,
    /// 同一条装配配方再跑一次就会**静默绑到另一张面上** —— 比报错危险得多。
    /// 按几何条件(类型/直径/法向/位置)描述面,是人在图纸上认面的方式,不随序号漂移。
    ///
    /// 匹配字段之间是 AND 关系;没给条件就不限。单位对外一律 mm(SE 内部是米)。
    /// </summary>
    internal sealed class FaceSelector
    {
        internal const double MmPerMeter = 1000.0;

        /// <summary>plane / cylinder / cone / torus / sphere / other;null=不限。</summary>
        public string Type;
        /// <summary>&gt;0 才生效(仅圆柱/圆锥有意义)。</summary>
        public double DiaMm;
        /// <summary>包围盒中心(mm);null=不限。</summary>
        public double[] AtMm;
        /// <summary>单位方向(平面=外法向、圆柱=轴向由 AxisVector 决定这里仍是外法向/法向);null=不限。</summary>
        public double[] Normal;
        /// <summary>&gt;0 才生效。</summary>
        public double LenMm;
        public double TolMm = 0.05;

        public bool IsEmpty
        {
            get
            {
                return string.IsNullOrWhiteSpace(Type) && DiaMm <= 0 && AtMm == null && Normal == null && LenMm <= 0;
            }
        }

        // ==================== 匹配 ====================

        public bool TryMatch(ModelStateTools.FaceRow row, out FaceInfo info)
        {
            info = null;
            if (row == null) return false;
            if (!TypeMatches(row.Type)) return false;

            double? dia = row.RadiusM.HasValue
                ? Math.Round(row.RadiusM.Value * 2.0 * MmPerMeter, 3)
                : (double?)null;
            if (DiaMm > 0 && (!dia.HasValue || Math.Abs(dia.Value - DiaMm) > TolMm)) return false;

            // 没有包围盒的面给不出位置,按"条件不足"跳过(与 se_find_objects 原行为一致)
            if (!row.HasRange) return false;

            double[] centerM = new double[3];
            double[] centerMm = new double[3];
            for (int a = 0; a < 3; a++)
            {
                centerM[a] = (row.Lo[a] + row.Hi[a]) / 2.0;
                centerMm[a] = Math.Round(centerM[a] * MmPerMeter, 3);
            }
            if (AtMm != null && !Near(AtMm, centerMm, TolMm)) return false;

            // 平面 → 外法向;圆柱没有"外法向",用【轴向】顶替(轴只有方向没有朝向,匹配时不分正负)
            double[] n = Unit(row.OutwardNormal) ?? Unit(row.Normal);
            bool isAxis = false;
            if (n == null)
            {
                double[] ax = Unit(row.AxisVector);
                if (ax != null)
                {
                    n = ax;
                    isAxis = true;
                }
            }
            if (Normal != null)
            {
                if (n == null) return false;
                double dot = Dot(n, Normal);
                if (isAxis ? Math.Abs(dot) <= 0.999 : dot <= 0.999) return false;
            }

            double axial = AxialLengthMm(row);
            if (LenMm > 0 && Math.Abs(axial - LenMm) > TolMm) return false;

            info = new FaceInfo
            {
                FaceId = row.Id,
                Type = row.Type,
                DiaMm = dia,
                CenterM = centerM,
                CenterMm = centerMm,
                Normal = n,
                NormalIsAxis = isAxis,
                Boss = row.IsBoss,
                AxialLengthMm = axial,
                ComFace = row.ComFace
            };
            return true;
        }

        private bool TypeMatches(string faceType)
        {
            if (string.IsNullOrWhiteSpace(Type)) return true;
            string w = Type.Trim().ToLowerInvariant();
            string t = (faceType ?? "").ToLowerInvariant();
            switch (w)
            {
                case "plane": return t == "plane";
                case "cylinder": return t == "cylinder";
                case "cone": return t == "cone";
                case "torus": return t == "torus";
                case "sphere": return t == "sphere";
                case "other": return t != "plane" && t != "cylinder" && t != "cone" && t != "torus" && t != "sphere";
                default: return t == w;   // 也允许直接传真实类型名(BSplineSurface 等)
            }
        }

        /// <summary>
        /// 轴向长度:圆柱有 AxisVector 时按轴向投影(薄法兰的轴向 20mm 会淹没在 200mm 直径的包围盒里,
        /// 取最长边会算错);其余面按包围盒最长边。
        /// </summary>
        private static double AxialLengthMm(ModelStateTools.FaceRow row)
        {
            double[] axis = Unit(row.AxisVector);
            if (axis != null)
            {
                double sum = 0;
                for (int i = 0; i < 3; i++) sum += Math.Abs((row.Hi[i] - row.Lo[i]) * axis[i]);
                if (sum > 1e-9) return Math.Round(sum * MmPerMeter, 3);
            }
            double max = Math.Max(Math.Max(row.Hi[0] - row.Lo[0], row.Hi[1] - row.Lo[1]), row.Hi[2] - row.Lo[2]);
            return Math.Round(max * MmPerMeter, 3);
        }

        /// <summary>一句话回显条件,用在"没命中/命中多个"的报错里。</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Type)) parts.Add("type=" + Type);
            if (DiaMm > 0) parts.Add("dia=" + Fmt(DiaMm) + "mm");
            if (AtMm != null) parts.Add("at=" + VectorText(AtMm) + "mm");
            if (Normal != null) parts.Add("normal=" + VectorText(Normal));
            if (LenMm > 0) parts.Add("len=" + Fmt(LenMm) + "mm");
            if (parts.Count == 0) parts.Add("(无条件)");
            if (TolMm > 0 && (DiaMm > 0 || AtMm != null || LenMm > 0)) parts.Add("tol=" + Fmt(TolMm) + "mm");
            return string.Join(" ", parts);
        }

        // ==================== 解析:字符串描述符 ====================

        /// <summary>
        /// 字符串描述符:
        ///   "plane"                    仅类型
        ///   "cylinder:89"              裸数字 = dia(mm)
        ///   "cylinder:dia=89"
        ///   "plane:normal=-Z"
        ///   "cylinder:dia=89;at=0,0,10;len=20;tol=0.1"
        /// 字段名大小写不敏感。
        /// </summary>
        public static FaceSelector FromString(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "描述符为空。";
                return null;
            }
            string s = text.Trim();
            int colon = s.IndexOf(':');
            string head = (colon < 0) ? s : s.Substring(0, colon);
            string body = (colon < 0) ? null : s.Substring(colon + 1);

            var sel = new FaceSelector { Type = head.Trim().ToLowerInvariant() };
            if (sel.Type.Length == 0)
            {
                error = "描述符缺少面类型(如 cylinder:dia=89 / plane:normal=-Z)。";
                return null;
            }
            if (!KnownTypeWord(sel.Type))
            {
                error = "面类型只能是 plane / cylinder / cone / torus / sphere / other,收到 \"" + head.Trim() + "\"。";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(body))
            {
                foreach (string raw in body.Split(';'))
                {
                    string part = raw.Trim();
                    if (part.Length == 0) continue;
                    int eq = part.IndexOf('=');
                    if (eq < 0)
                    {
                        // 简写:裸数字=dia("cylinder:89");裸方向=normal("plane:-Z" / "plane:0,0,1")
                        double bare;
                        if (TryParseD(part, out bare))
                        {
                            if (bare <= 0)
                            {
                                error = "描述符 \"" + part + "\" 当直径用时必须为正数(mm)。";
                                return null;
                            }
                            sel.DiaMm = bare;
                            continue;
                        }
                        double[] dir = ParseDirection(part);
                        if (dir != null)
                        {
                            sel.Normal = dir;
                            continue;
                        }
                        error = "描述符片段 \"" + part + "\" 既不是数字、也不是方向(+Z/-X/0,0,1)、也不是 key=value。";
                        return null;
                    }
                    string k = part.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = part.Substring(eq + 1).Trim();
                    if (!sel.SetField(k, v, ref error)) return null;
                }
            }
            return sel;
        }

        private static bool KnownTypeWord(string t)
        {
            switch (t)
            {
                case "plane":
                case "cylinder":
                case "cone":
                case "torus":
                case "sphere":
                case "other":
                    return true;
                default:
                    return false;
            }
        }

        private bool SetField(string key, string value, ref string error)
        {
            if (value == null) value = "";
            switch (key)
            {
                case "type":
                    string t = value.Trim().ToLowerInvariant();
                    if (!KnownTypeWord(t))
                    {
                        error = "type 只能是 plane/cylinder/cone/torus/sphere/other,收到 \"" + value + "\"。";
                        return false;
                    }
                    Type = t;
                    return true;
                case "dia":
                case "diamm":
                case "diameter":
                {
                    double d;
                    if (!TryParseD(value, out d) || d <= 0)
                    {
                        error = "dia 需要正数(mm),收到 \"" + value + "\"。";
                        return false;
                    }
                    DiaMm = d;
                    return true;
                }
                case "at":
                case "atmm":
                case "center":
                {
                    double[] p = ParsePointMm(value);
                    if (p == null)
                    {
                        error = "at 需要 \"x,y,z\"(mm,逗号分隔),收到 \"" + value + "\"。";
                        return false;
                    }
                    AtMm = p;
                    return true;
                }
                case "normal":
                case "dir":
                case "axis":
                {
                    double[] n = ParseDirection(value);
                    if (n == null)
                    {
                        error = "normal 需要 \"+Z\"/\"-X\" 或 \"0,0,1\",收到 \"" + value + "\"。";
                        return false;
                    }
                    Normal = n;
                    return true;
                }
                case "len":
                case "lenmm":
                case "length":
                {
                    double d;
                    if (!TryParseD(value, out d) || d <= 0)
                    {
                        error = "len 需要正数(mm),收到 \"" + value + "\"。";
                        return false;
                    }
                    LenMm = d;
                    return true;
                }
                case "tol":
                case "tolmm":
                {
                    double d;
                    if (!TryParseD(value, out d) || d <= 0)
                    {
                        error = "tol 需要正数(mm),收到 \"" + value + "\"。";
                        return false;
                    }
                    TolMm = d;
                    return true;
                }
                default:
                    error = "未知字段 \"" + key + "\"(可用: type/dia/at/normal/len/tol)。";
                    return false;
            }
        }

        // ==================== 解析:JSON 对象 ====================

        public static FaceSelector FromJson(JsonElement e, out string error)
        {
            error = null;
            var sel = new FaceSelector();
            foreach (JsonProperty p in e.EnumerateObject())
            {
                string k = p.Name.ToLowerInvariant();
                JsonElement v = p.Value;
                if (v.ValueKind == JsonValueKind.Array)
                {
                    double[] arr = ToNumberArray(v);
                    if (arr == null)
                    {
                        error = p.Name + " 需要数字数组。";
                        return null;
                    }
                    if (k == "at" || k == "atmm" || k == "center")
                    {
                        if (arr.Length != 3) { error = p.Name + " 需要 3 个数字(x,y,z mm)。"; return null; }
                        sel.AtMm = arr;
                    }
                    else if (k == "normal" || k == "dir" || k == "axis")
                    {
                        double[] n = Unit(arr);
                        if (n == null) { error = p.Name + " 不能是零向量。"; return null; }
                        sel.Normal = n;
                    }
                    else
                    {
                        error = "未知字段 \"" + p.Name + "\"。";
                        return null;
                    }
                    continue;
                }
                if (v.ValueKind == JsonValueKind.Number)
                {
                    double num;
                    if (!v.TryGetDouble(out num)) { error = p.Name + " 不是数字。"; return null; }
                    switch (k)
                    {
                        case "dia":
                        case "diamm":
                            if (num <= 0) { error = p.Name + " 需要正数(mm)。"; return null; }
                            sel.DiaMm = num;
                            break;
                        case "len":
                        case "lenmm":
                            if (num <= 0) { error = p.Name + " 需要正数(mm)。"; return null; }
                            sel.LenMm = num;
                            break;
                        case "tol":
                        case "tolmm":
                            if (num <= 0) { error = p.Name + " 需要正数(mm)。"; return null; }
                            sel.TolMm = num;
                            break;
                        default:
                            error = "未知字段 \"" + p.Name + "\"。";
                            return null;
                    }
                    continue;
                }
                if (v.ValueKind == JsonValueKind.String)
                {
                    if (!sel.SetField(k, v.GetString(), ref error)) return null;
                    continue;
                }
                error = p.Name + " 的类型不支持(要字符串/数字/数字数组)。";
                return null;
            }
            return sel;
        }

        // ==================== 向量小工具(与 se_find_objects 共用) ====================

        /// <summary>解析 "x,y,z"(mm,不做单位换算)。</summary>
        public static double[] ParsePointMm(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] parts = text.Split(',');
            if (parts.Length != 3) return null;
            double[] r = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (!TryParseD(parts[i].Trim(), out r[i])) return null;
            }
            return r;
        }

        /// <summary>解析 "+Z"/"-X"/"0,0,1" → 单位向量。</summary>
        public static double[] ParseDirection(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.Trim();
            if (s.Length == 2 && (s[0] == '+' || s[0] == '-'))
            {
                char axis = char.ToUpperInvariant(s[1]);
                int idx = axis == 'X' ? 0 : (axis == 'Y' ? 1 : (axis == 'Z' ? 2 : -1));
                if (idx < 0) return null;
                double[] v = new double[3];
                v[idx] = (s[0] == '-') ? -1.0 : 1.0;
                return v;
            }
            return Unit(ParsePointMm(s));
        }

        public static double[] Unit(double[] v)
        {
            if (v == null || v.Length < 3) return null;
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (len < 1e-12) return null;
            return new[] { v[0] / len, v[1] / len, v[2] / len };
        }

        public static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        public static bool Near(double[] a, double[] b, double tol)
        {
            if (a == null || b == null || a.Length < 3 || b.Length < 3) return false;
            return Math.Abs(a[0] - b[0]) <= tol && Math.Abs(a[1] - b[1]) <= tol && Math.Abs(a[2] - b[2]) <= tol;
        }

        /// <summary>Rounded 3 位的 "x,y,z",给报错文案用(避免 0.6999999999 这类噪声)。</summary>
        public static string VectorText(double[] v)
        {
            if (v == null) return "null";
            return Fmt(v[0]) + "," + Fmt(v[1]) + "," + Fmt(v[2]);
        }

        private static string Fmt(double d)
        {
            return Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>按不变文化解析数字(COM/JSON 场景都别让当前区域设置插一脚)。</summary>
        private static bool TryParseD(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static double[] ToNumberArray(JsonElement arr)
        {
            var list = new List<double>();
            foreach (JsonElement item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double d)) return null;
                list.Add(d);
            }
            return list.ToArray();
        }
    }

    /// <summary>在零件 Body 上按条件找面(装配约束绑面 与 se_find_objects 共用同一条遍历链)。</summary>
    internal static class FaceFinder
    {
        /// <summary>
        /// 遍历 Body 全量面,返回所有命中(歧义由调用方判断——装配约束要求唯一命中)。
        /// scanned=扫了多少张面;error 非空表示面集合读不到。
        /// </summary>
        /// <summary>
        /// 读零件 Body 的全量面表。这是面相关的唯一遍历链(se_find_objects / 装配约束绑面 / 面清单预览都走它),
        /// 所以"序号"在三处含义一致。error 非空表示面集合读不到。
        /// </summary>
        internal static List<ModelStateTools.FaceRow> ReadAllFaces(object body, out string error)
        {
            error = null;
            if (body == null)
            {
                error = "Model.Body 取不到。";
                return new List<ModelStateTools.FaceRow>();
            }
            object faces;
            string source;
            if (!ModelStateTools.TryGetAllFaces(body, out faces, out source))
            {
                error = "读不到 Body 的面集合(零件可能没有实体几何)。";
                return new List<ModelStateTools.FaceRow>();
            }
            var warnings = new List<string>();
            return ModelStateTools.ReadFaces(faces, false, warnings, 1);
        }

        /// <summary>在已读出的面表上按条件过滤(给只扫一次面表、要同时做别的事的调用方)。</summary>
        internal static List<FaceInfo> Filter(List<ModelStateTools.FaceRow> rows, FaceSelector selector, out int scanned)
        {
            var hits = new List<FaceInfo>();
            scanned = rows == null ? 0 : rows.Count;
            if (rows == null) return hits;
            if (selector == null) selector = new FaceSelector();
            for (int i = 0; i < rows.Count; i++)
            {
                FaceInfo info;
                if (selector.TryMatch(rows[i], out info))
                {
                    info.Index = i;   // 与 Body.Faces(1) 枚举序一致
                    hits.Add(info);
                }
            }
            return hits;
        }

        /// <summary>按 0-based 序号取一张面(越界返回 null)。</summary>
        internal static FaceInfo ByIndex(List<ModelStateTools.FaceRow> rows, int index)
        {
            if (rows == null || index < 0 || index >= rows.Count) return null;
            FaceInfo info;
            if (!new FaceSelector().TryMatch(rows[index], out info)) return null;
            info.Index = index;
            return info;
        }

        /// <summary>按 Face.ID 取面(Face.ID 在单个 Body 内唯一,比 COM 引用比对可靠)。</summary>
        internal static FaceInfo ByFaceId(List<ModelStateTools.FaceRow> rows, int faceId)
        {
            if (rows == null || faceId < 0) return null;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Id != faceId) continue;
                FaceInfo info;
                if (!new FaceSelector().TryMatch(rows[i], out info)) return null;
                info.Index = i;
                return info;
            }
            return null;
        }

        /// <summary>按 COM 对象引用反查面(句柄路径用:确认这个句柄的面确实属于这个零件 Body)。</summary>
        internal static FaceInfo ByComFace(List<ModelStateTools.FaceRow> rows, object comFace)
        {
            if (rows == null || comFace == null) return null;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!ReferenceEquals(rows[i].ComFace, comFace)) continue;
                FaceInfo info;
                if (!new FaceSelector().TryMatch(rows[i], out info)) return null;
                info.Index = i;
                return info;
            }
            return null;
        }

    }
}
