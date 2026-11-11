using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// se_assembly_build 的 op 手册。与 <see cref="OpManual"/> 同一套机制:
    /// 首次用到某 op 时随返回值注入一次,之后只留指针,随时可用 {"op":"?"} 主动查。
    ///
    /// 新增 op 时:先加 AssemblySpec 的解析与校验,再在这里补条目,
    /// AssemblyManualTests 会盯住"手册 op == 白名单 op"这条契约。
    /// </summary>
    public static class AssemblyManual
    {
        public const string Domain = "assembly";

        /// <summary>参数名。装配工具的数组参数叫 ops(建模工具叫 features),指路文案要跟着变。</summary>
        public const string ArgName = "ops";

        private static readonly string[] Order =
        {
            "place", "move", "rotate", "transform", "origin", "visible", "ground",
            "delete", "replace", "reorder", "color", "constrain", "rebind"
        };

        public static readonly string Common =
            "通用规则(所有装配 op 共用)\n" +
            "1. component 三种写法:0-based 序号 / 零件名(如 \"Par1.par:1\") / 句柄 obj-N。\n" +
            "2. 长度单位米,角度单位度。\n" +
            "3. 破坏性 op(delete / replace / ground value=false)必须显式 confirm=true;" +
            "本批所有 op 在执行前按【最高危】op 统一预检,存在未确认的破坏性 op 时整批拒绝、一个都不执行。\n" +
            "4. confirm=true 只解除拦截,护栏【不】核验删除证据;先调 se_assembly_query 拿 occurrences 核对目标序号/身份,再传 confirm。\n" +
            "5. 每个 op 建完自动验证(集合 Count 增长 / 矩阵回读比对 / 关系 Status 回读),失败立即中止后续;" +
            "但【不自动删除】已产生的对象——回滚由调用方判断后显式下 delete op。\n" +
            "6. 诚实拒绝(校验期即拦截并给替代方案):constrain type=angular/tangent/point/gear、" +
            "op=mirror/suppress/unsuppress —— SE2022 COM 自动化不可达;" +
            "op=pattern 属\"COM 可达但本工具未实现\"(AssemblyPatterns.CreateDuplicate 2026-11-01 真机实测可达)," +
            "拒绝文案指替代方案,勿当不可达。\n" +
            "7. dryRun=true 只做静态校验,不碰 COM。";

        private static readonly Dictionary<string, string> _entries =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["place"] =
                "place 放置零件(AddByFilename / AddWithMatrix / AddWithTransform;familyMember 走 AddFamilyByFilename)。\n" +
                "{op:\"place\", file:路径, familyMember?:族成员名, matrix?:[16 元素行主序,平移在 12..14], " +
                "origin?:[x,y,z] + angles?:[度], ground?:}\n" +
                "- 默认零件自带固定约束;ground=false 放置后立即解固\n" +
                "- familyMember 恒落原点,需要再补 transform/origin",

            ["move"] =
                "move 平移。\n" +
                "{op:\"move\", component, delta:[dx,dy,dz]}\n" +
                "- delta 单位米,是增量不是绝对位置(绝对位置用 origin)",

            ["rotate"] =
                "rotate 旋转。\n" +
                "{op:\"rotate\", component, axis1:[x,y,z], axis2:[x,y,z], degrees:度}\n" +
                "- 轴用两点定义(轴1→轴2),degrees 单位度",

            ["transform"] =
                "transform 全量变换(同时给位置和姿态)。\n" +
                "{op:\"transform\", component, origin:[x,y,z], angles:[度]}\n" +
                "{op:\"transform\", component, matrix:[16 元素行主序,平移在 12..14,米]}\n" +
                "- angles 省略会把旋转归零;只想挪位置请用 origin op\n" +
                "- 斜置姿态用 matrix(直接 PutMatrix,回读矩阵逐元素核对);Euler 组合表达不了时优先它",

            ["origin"] =
                "origin 仅挪位置(不动姿态)。\n" +
                "{op:\"origin\", component, position:[x,y,z]}\n" +
                "- 绝对位置,单位米",

            ["visible"] =
                "visible 显示/隐藏。\n" +
                "{op:\"visible\", component, value:true|false}",

            ["ground"] =
                "ground 固定/解除固定。\n" +
                "{op:\"ground\", component, value:true|false, confirm?:}\n" +
                "- value=false 走删 GroundRelation3d,必须 confirm=true",

            ["delete"] =
                "delete 删除零件。\n" +
                "{op:\"delete\", component, confirm:true}\n" +
                "- 必须 confirm=true;先核对目标再删,本工具不会替你验证删的是不是对的",

            ["replace"] =
                "replace 替换零件文件。\n" +
                "{op:\"replace\", component, file:新文件路径, all?:bool, confirm:true}\n" +
                "- 必须 confirm=true;all=true 用于同名多实例场景",

            ["reorder"] =
                "reorder 调整装配树顺序。\n" +
                "{op:\"reorder\", component, target, after?:bool}\n" +
                "- after 省略=插到 target 之前",

            ["color"] =
                "color 上色。\n" +
                "{op:\"color\", component, rgb:[r,g,b]}\n" +
                "- rgb 0~1 或 0~255 由 FaceStyle 通道决定(实测按 0~1)",

            ["constrain"] =
                "constrain 加装配约束(planar 贴合/共面、axial 同轴)。\n" +
                "{op:\"constrain\", type:\"planar\"|\"axial\", component1, face1, component2, face2, " +
                "mate?:bool, keepGround?:bool}\n" +
                "- 默认自动解固 component1 的固定约束再求解——固定件上加约束不生效;component2 视为基准件、其固定不动\n" +
                "- keepGround=true 显式保留 component1 的固定(仅基准件场景,此时回传 warning)\n" +
                "- planar 的 mate=true=贴合(可能把零件翻转 180°);与轴向约束同用时给 false=共面同向\n" +
                "- face1/face2 四种写法(axial 两面必须是圆柱面):\n" +
                "  ①描述符对象 {\"type\":\"cylinder\",\"diaMm\":89} / {\"type\":\"plane\",\"normal\":\"-Z\"}——推荐," +
                "按几何认面,零件重建后序号漂移也不受影响;字段与 se_find_objects 完全一致(type/diaMm/atMm/normal/lenMm/tolMm;" +
                "平面面 normal=外法向,圆柱面 normal=轴向,平行即命中)\n" +
                "  ②描述符字符串 \"cylinder:dia=89\" / \"plane:-Z\" / \"cylinder:89;at=0,0,10;len=20\"" +
                "(冒号后 key=value、分号分隔,裸数字=直径、裸方向=法向)\n" +
                "  ③0-based 面序号(上界见 se_assembly_query 的 occurrences[].faceCount)——加删特征后序号整体漂移,重跑配方会静默绑错面\n" +
                "  ④面句柄 \"obj-N\"(se_find_objects 返回的 handle,会校验该面确实属于这个零件)\n" +
                "- 返回 face1_bound / face2_bound 回显实际绑到的面(index/FaceID/type/diaMm/centerMm)与约束点;" +
                "描述符命不中或命中多张时会回传该零件的面清单,照着改条件即可\n" +
                "- relation_status 是工具自动 UpdateAll 后回读的值——直接读 Status 会因求解滞后得到假成功," +
                "=1 才是真求解,0/5=冲突固死;同时返回 component1_position 作为回读级证据",

            ["rebind"] =
                "rebind 修复「零件重建后约束引用的面没了」。\n" +
                "{op:\"rebind\", relation?:序号|\"auto\", face1?, face2?}\n" +
                "- 症状:se_assembly_query mode=relations 里 detailed_status=6(MissingGeometry)、solved=false,UpdateAll 救不回\n" +
                "- 做法:读该关系记录的原始几何(平面=法向+一点 / 轴向=轴线;失效那侧读不到就沿用另一侧)," +
                "在该零件当前面表里按几何唯一匹配(容差 0.05mm;同轴孔壁/轴颈这类等效多解取第一张并记 note)," +
                "然后【先建新关系】、求解成功后才删旧关系;新约束解不出来就删掉新的、旧的原样保留;Offset/FixedRotate 一并还原\n" +
                "- relation 省略 = 自动处理全部 detailed_status=6;给了 face1/face2 则跳过几何推断直接用描述符\n" +
                "- 只认 detailed_status=6(健康关系会被拒绝:duplicate planar 会把求解打成 detailed=5;" +
                "想改绑别的面请删掉重下 constrain)\n" +
                "- 返回 items[] 逐条给 face1_bound/face2_bound/new_relation_status/detailed_status_before;" +
                "失败项带 error 与候选面清单"
        };

        public static IReadOnlyDictionary<string, string> Entries
        {
            get { return _entries; }
        }

        public static IEnumerable<string> Names
        {
            get { return Order; }
        }

        public static bool Has(string op)
        {
            return !string.IsNullOrEmpty(op) && _entries.ContainsKey(op);
        }

        public static string Index()
        {
            var sb = new StringBuilder();
            sb.Append("se_assembly_build op 索引。取单个完整手册:{\"op\":\"?\",\"name\":\"<op>\"};")
              .Append("取通用规则:{\"op\":\"?\",\"topic\":\"common\"}\n");
            foreach (var op in Order)
            {
                string text;
                if (!_entries.TryGetValue(op, out text)) continue;
                int nl = text.IndexOf('\n');
                sb.Append(nl >= 0 ? text.Substring(0, nl) : text).Append('\n');
            }
            return sb.ToString();
        }

        public static string Lookup(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "?" || name == "*")
                return Index();

            if (string.Equals(name, "common", StringComparison.OrdinalIgnoreCase))
                return Common;

            string text;
            if (_entries.TryGetValue(name.Trim(), out text))
                return text;

            return "手册里没有 op \"" + name + "\"。可用 op 见下:\n" + Index();
        }

        public static bool TryQuery(JsonElement[] ops, out string text)
        {
            text = null;
            if (ops == null || ops.Length == 0) return false;

            var first = ops[0];
            if (first.ValueKind != JsonValueKind.Object) return false;

            string op = ReadString(first, "op");
            if (op == null) return false;
            if (op != "?" && !string.Equals(op, "help", StringComparison.OrdinalIgnoreCase)) return false;

            string topic = ReadString(first, "topic");
            if (string.Equals(topic, "common", StringComparison.OrdinalIgnoreCase))
            {
                text = Common;
                return true;
            }

            text = Lookup(ReadString(first, "name"));
            return true;
        }

        private static string ReadString(JsonElement obj, string prop)
        {
            JsonElement e;
            if (!obj.TryGetProperty(prop, out e)) return null;
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
    }
}
