using System.Collections.Generic;
using System.Linq;

namespace TbhCombatTracker
{
    /// <summary>拆分维度。输出 / 承伤看前三个，治疗只有恢复来源。</summary>
    internal enum Dimension { Skill, DamageType, Attribute, HealKind }

    /// <summary>拆分表的一行。只有技能维度有次数、暴击、最高（<see cref="Detailed"/>）。</summary>
    internal struct BreakdownRow
    {
        public string Label;
        public double Value;
        public long Hits;
        public long Crits;
        public float Max;
        public bool Detailed;
    }

    /// <summary>把一个来源的统计按维度拆开，明细窗口和主面板共用。</summary>
    internal static class Breakdown
    {
        public static List<BreakdownRow> Of(SourceStats s, Dimension dim)
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
            }

            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        /// <summary>饼图的切片，颜色按排名取，和拆分表的色块一一对应。</summary>
        public static List<Slice> Slices(List<BreakdownRow> rows)
            => rows.Select((r, i) => new Slice { Label = r.Label, Value = r.Value, Color = PieChart.ColorAt(i) }).ToList();
    }
}
