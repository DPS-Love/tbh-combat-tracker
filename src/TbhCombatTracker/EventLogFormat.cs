using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TbhCombatTracker
{
    /// <summary>
    /// 战斗日志的文本格式（v1），完整说明见 docs/eventlog.md。
    ///
    /// 一行一个事件，字段用 <c>|</c> 分隔，第一个字段是会话内秒数，第二个是事件字母：
    /// <code>
    /// #TBHLOG|1
    /// #META|mod=0.3.0|game=1.2.8|start=2026-09-23T11:22:08.123+08:00
    /// 0.512|U|-51234|H|2|Hero_201|游侠
    /// 0.512|A|3|SkillName_20101|快速射击
    /// 0.512|D|-51234|-60012|8123.46|10234|1|2|1|3
    /// …
    /// #END|events=123456
    /// </code>
    /// 规则：未知的事件字母跳过（新版本写的日志旧版本也能读个大概）；
    /// 字段只在行尾追加，旧版本读新日志时多出来的字段直接忽略；缺的字段按"不知道"处理。
    /// </summary>
    public static class EventLogFormat
    {
        public const int Version = 1;
        public const string Magic = "#TBHLOG";
        public const string MetaTag = "#META";
        public const string EndTag = "#END";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public enum LineKind { Event, Header, Meta, End, Blank, Unknown, Bad }

        // ------------------------------------------------------------------ 写

        public static string HeaderLine() => Magic + "|" + Version.ToString(Inv);

        public static string MetaLine(IEnumerable<KeyValuePair<string, string>> meta)
        {
            var sb = new StringBuilder(MetaTag);
            foreach (var kv in meta)
                sb.Append('|').Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value));
            return sb.ToString();
        }

        public static string EndLine(long events) => EndTag + "|events=" + events.ToString(Inv);

        /// <summary>把一个事件写成一行（不含换行）。</summary>
        public static void Append(StringBuilder sb, in CombatEvent e)
        {
            sb.Append(e.T.ToString("0.000", Inv)).Append('|');
            switch (e.Kind)
            {
                case EventKind.Unit:
                    sb.Append("U|").Append(Int(e.A)).Append('|')
                      .Append(e.UnitKind == '\0' ? 'O' : e.UnitKind).Append('|')
                      .Append(Int(e.ClassType)).Append('|')
                      .Append(Escape(e.Key)).Append('|').Append(Escape(e.Text));
                    break;

                case EventKind.Ability:
                    sb.Append("A|").Append(Int(e.A)).Append('|')
                      .Append(Escape(e.Key)).Append('|').Append(Escape(e.Text));
                    break;

                case EventKind.Damage:
                case EventKind.Taken:
                    sb.Append(e.Kind == EventKind.Damage ? "D|" : "T|")
                      .Append(Int(e.A)).Append('|').Append(Int(e.B)).Append('|')
                      .Append(Num(e.Amount)).Append('|')
                      .Append(float.IsNaN(e.Origin) ? "" : Num(e.Origin)).Append('|')
                      .Append(e.Crit < 0 ? "" : Int(e.Crit)).Append('|')
                      .Append(e.DamageType < 0 ? "" : Int(e.DamageType)).Append('|')
                      .Append(e.Attribute < 0 ? "" : Int(e.Attribute)).Append('|')
                      .Append(e.Ability <= 0 ? "" : Int(e.Ability));
                    break;

                case EventKind.Heal:
                    sb.Append("H|").Append(Int(e.A)).Append('|').Append(Int(e.B)).Append('|')
                      .Append(Num(e.Amount)).Append('|')
                      .Append(Int(e.HealKind)).Append('|')
                      .Append(e.FlagB < 0 ? "" : Int(e.FlagB)).Append('|')
                      .Append(e.FlagC < 0 ? "" : Int(e.FlagC)).Append('|')
                      .Append(Int(e.Bracket));
                    break;

                case EventKind.StageName:
                    sb.Append("S|name|").Append(Escape(e.Text));
                    break;

                case EventKind.StageStart:
                    sb.Append("S|start|").Append(e.Flag ? '1' : '0');
                    break;

                case EventKind.Wave:
                    sb.Append("S|wave|").Append(Escape(e.Text));
                    break;

                case EventKind.Reset:
                    sb.Append('R');
                    break;
            }
        }

        // ------------------------------------------------------------------ 读

        public static LineKind Parse(string line, out CombatEvent e)
        {
            e = default;
            if (string.IsNullOrEmpty(line)) return LineKind.Blank;

            if (line[0] == '#')
            {
                if (line.StartsWith(Magic + "|", StringComparison.Ordinal)) return LineKind.Header;
                if (line.StartsWith(MetaTag, StringComparison.Ordinal)) return LineKind.Meta;
                if (line.StartsWith(EndTag, StringComparison.Ordinal)) return LineKind.End;
                return LineKind.Blank;                       // 注释
            }

            var f = line.Split('|');
            if (f.Length < 2 || !TryDouble(f[0], out var t)) return LineKind.Bad;
            e.T = t;

            switch (f[1])
            {
                case "U":
                    if (f.Length < 7 || !TryInt(f[2], out e.A) || f[3].Length == 0) return LineKind.Bad;
                    e.Kind = EventKind.Unit;
                    e.UnitKind = f[3][0];
                    TryInt(f[4], out e.ClassType);
                    e.Key = Unescape(f[5]);
                    e.Text = Unescape(f[6]);
                    return LineKind.Event;

                case "A":
                    if (f.Length < 5 || !TryInt(f[2], out e.A)) return LineKind.Bad;
                    e.Kind = EventKind.Ability;
                    e.Key = Unescape(f[3]);
                    e.Text = Unescape(f[4]);
                    return LineKind.Event;

                case "D":
                case "T":
                    // 截断的末行少字段，宁可丢掉也不要读出一个错的数
                    if (f.Length < 10 || !TryInt(f[2], out e.A) || !TryInt(f[3], out e.B) ||
                        !TryFloat(f[4], out e.Amount))
                        return LineKind.Bad;
                    e.Kind = f[1] == "D" ? EventKind.Damage : EventKind.Taken;
                    e.Origin = TryFloat(f[5], out var o) ? o : float.NaN;
                    e.Crit = TryInt(f[6], out var crit) ? (sbyte)(crit != 0 ? 1 : 0) : (sbyte)-1;
                    e.DamageType = TryInt(f[7], out var type) ? type : -1;
                    e.Attribute = TryInt(f[8], out var attr) ? attr : -1;
                    e.Ability = TryInt(f[9], out var aid) ? aid : 0;
                    return LineKind.Event;

                case "H":
                    if (f.Length < 9 || !TryInt(f[2], out e.A) || !TryInt(f[3], out e.B) ||
                        !TryFloat(f[4], out e.Amount) || !TryInt(f[5], out e.HealKind))
                        return LineKind.Bad;
                    e.Kind = EventKind.Heal;
                    e.FlagB = TryInt(f[6], out var fb) ? (sbyte)(fb != 0 ? 1 : 0) : (sbyte)-1;
                    e.FlagC = TryInt(f[7], out var fc) ? (sbyte)(fc != 0 ? 1 : 0) : (sbyte)-1;
                    TryInt(f[8], out e.Bracket);
                    return LineKind.Event;

                case "S":
                    if (f.Length < 4) return LineKind.Bad;
                    switch (f[2])
                    {
                        case "name":
                            e.Kind = EventKind.StageName;
                            e.Text = Unescape(f[3]);
                            return LineKind.Event;
                        case "start":
                            e.Kind = EventKind.StageStart;
                            e.Flag = f[3] == "1";
                            return LineKind.Event;
                        case "wave":
                            e.Kind = EventKind.Wave;
                            e.Text = Unescape(f[3]);
                            return LineKind.Event;
                        default:
                            return LineKind.Unknown;
                    }

                case "R":
                    e.Kind = EventKind.Reset;
                    return LineKind.Event;

                default:
                    return LineKind.Unknown;
            }
        }

        public static bool TryParseHeader(string line, out int version)
        {
            version = 0;
            var f = line.Split('|');
            return f.Length >= 2 && f[0] == Magic && TryInt(f[1], out version);
        }

        public static void ParseMeta(string line, IDictionary<string, string> into)
        {
            var f = line.Split('|');
            for (var i = 1; i < f.Length; i++)
            {
                var eq = f[i].IndexOf('=');
                if (eq <= 0) continue;
                into[Unescape(f[i].Substring(0, eq))] = Unescape(f[i].Substring(eq + 1));
            }
        }

        /// <summary>
        /// 规整成日志里的写法：记录端先过一遍再同时喂给实时解析器和日志，
        /// 保证"实时看到的数字"和"导入同一份日志算出的数字"逐位一致。
        /// </summary>
        public static float CanonicalAmount(float v)
            => float.TryParse(Num(v), NumberStyles.Float, Inv, out var r) ? r : v;

        public static double CanonicalTime(double t)
            => double.TryParse(t.ToString("0.000", Inv), NumberStyles.Float, Inv, out var r) ? r : t;

        // ------------------------------------------------------------------ 小工具

        private static string Int(int v) => v.ToString(Inv);
        private static string Num(float v) => v.ToString("0.##", Inv);

        private static bool TryInt(string s, out int v)
            => int.TryParse(s, NumberStyles.Integer, Inv, out v);

        private static bool TryFloat(string s, out float v)
            => float.TryParse(s, NumberStyles.Float, Inv, out v) && !float.IsNaN(v) && !float.IsInfinity(v);

        private static bool TryDouble(string s, out double v)
            => double.TryParse(s, NumberStyles.Float, Inv, out v) && !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>文本字段里的 | 和换行要转义，否则会被当成分隔符。</summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(Specials) < 0) return s;

            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '|': sb.Append("\\p"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s ?? "";

            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                var n = s[++i];
                switch (n)
                {
                    case 'p': sb.Append('|'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    default: sb.Append(n); break;   // \\ 以及不认识的转义都按字面
                }
            }
            return sb.ToString();
        }

        private static readonly char[] Specials = { '\\', '|', '\n', '\r' };
    }
}
