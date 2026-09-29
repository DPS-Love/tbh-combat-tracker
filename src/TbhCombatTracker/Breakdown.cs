using System.Collections.Generic;

namespace TbhCombatTracker
{
    /// <summary>
    /// 拆分维度。输出 / 承伤：技能、类型、元素、对手（输出是打了哪种怪，承伤是被哪种怪打）；
    /// 治疗：恢复来源、对手（治疗了谁）。
    /// </summary>
    internal enum Dimension { Skill, DamageType, Attribute, HealKind, Target }

    /// <summary>拆分表的一行。技能和对手维度有次数、暴击、最高（<see cref="Detailed"/>）。</summary>
    internal struct BreakdownRow
    {
        public string Label;
        public double Value;
        public long Hits;
        public long Crits;
        public float Max;
        public bool Detailed;
    }

    /// <summary>把一个来源的统计按维度拆开，明细窗口和战斗记录共用。颜色按排名取 <c>Theme.PaletteAt</c>。</summary>
    internal static class Breakdown
    {
        public static List<BreakdownRow> Of(SourceStats s, Dimension dim, TrackerView view = TrackerView.Outgoing)
        {
            var list = new List<BreakdownRow>();
            if (s == null) return list;

            switch (dim)
            {
                case Dimension.Skill:
                    foreach (var kv in s.BySkill)
                        list.Add(new BreakdownRow
                        {
                            Label = Strings.SkillName(kv.Key), Value = kv.Value.Total,
                            Hits = kv.Value.Hits, Crits = kv.Value.Crits, Max = kv.Value.Max, Detailed = true,
                        });
                    break;

                case Dimension.DamageType:
                    for (var i = 0; i < s.ByType.Length; i++)
                        if (s.ByType[i] > 0d)
                            list.Add(new BreakdownRow { Label = DamageTracker.TypeName(i), Value = s.ByType[i] });
                    break;

                case Dimension.Attribute:
                    for (var i = 0; i < s.ByAttribute.Length; i++)
                        if (s.ByAttribute[i] > 0d)
                            list.Add(new BreakdownRow { Label = DamageTracker.AttributeName(i), Value = s.ByAttribute[i] });
                    break;

                case Dimension.HealKind:
                    for (var i = 0; i < s.ByHealKind.Length; i++)
                        if (s.ByHealKind[i] > 0d)
                            list.Add(new BreakdownRow { Label = Healing.KindName(i), Value = s.ByHealKind[i] });
                    break;

                case Dimension.Target:
                    // 不同的键可能译成同一个名字（比如精英版的同种怪），按显示名合并
                    var byLabel = new Dictionary<string, int>();
                    foreach (var kv in s.ByTarget)
                    {
                        var label = CounterpartLabel(kv.Key, view);
                        if (byLabel.TryGetValue(label, out var at))
                        {
                            var row = list[at];
                            row.Value += kv.Value.Total;
                            row.Hits += kv.Value.Hits;
                            row.Crits += kv.Value.Crits;
                            if (kv.Value.Max > row.Max) row.Max = kv.Value.Max;
                            list[at] = row;
                            continue;
                        }
                        byLabel[label] = list.Count;
                        list.Add(new BreakdownRow
                        {
                            Label = label, Value = kv.Value.Total,
                            Hits = kv.Value.Hits, Crits = kv.Value.Crits, Max = kv.Value.Max, Detailed = true,
                        });
                    }
                    break;
            }

            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        /// <summary>对手分组键（见 CombatParser.CounterpartKey）→ 显示名。怪物查游戏的怪物译名。</summary>
        public static string CounterpartLabel(string key, TrackerView view)
        {
            if (string.IsNullOrEmpty(key)) return view == TrackerView.Outgoing ? Strings.UnknownTarget : Strings.UnknownSource;
            if (key.StartsWith(CombatParser.CounterpartMonster, System.StringComparison.Ordinal))
                return MonsterName(key.Substring(CombatParser.CounterpartMonster.Length));
            if (key.StartsWith(CombatParser.CounterpartUnit, System.StringComparison.Ordinal))
                return key.Substring(CombatParser.CounterpartUnit.Length);
            return key;
        }

        private static readonly Dictionary<string, string> MonsterNames = new Dictionary<string, string>();

        /// <summary>
        /// 怪物的显示名。日志里记的是 GameObject 名（<c>Monster_30043</c>），游戏的译名表里对应的键是
        /// <c>MonsterName_30043</c>（"雪山卫兵"）——编号一一对应，实测过几十种怪都对得上。查不到就原样显示。
        /// </summary>
        public static string MonsterName(string unitKey)
        {
            if (string.IsNullOrEmpty(unitKey)) return Strings.UnknownTarget;
            if (MonsterNames.TryGetValue(unitKey, out var name)) return name;

            name = unitKey;
            var us = unitKey.LastIndexOf('_');
            if (us > 0 && us < unitKey.Length - 1)
                name = Localize.TryGet("MonsterName_" + unitKey.Substring(us + 1)) ?? unitKey;

            if (MonsterNames.Count > 2000) MonsterNames.Clear();
            MonsterNames[unitKey] = name;
            return name;
        }
    }
}
