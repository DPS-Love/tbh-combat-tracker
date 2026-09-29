using System;
using System.Collections.Generic;

// ============================================================================
//  统计模型 —— 纯 C#
//
//  这个文件和 CombatEvent / EventLogFormat / EventLogReader / CombatParser 一起构成"解析器"：
//  不引用 UnityEngine、BepInEx、游戏类型，也不碰 Mod.Config / Strings。
//  tools/logcheck 会把这几个文件单独编译成一个控制台程序——谁在这里引用了 Unity，那边就编译不过。
//
//  显示用的文字（段标题、未知来源、技能兜底名）都在界面层用 Strings 拼，这里只存原始事实。
// ============================================================================

namespace TbhCombatTracker
{
    /// <summary>面板的三个视图，也是统计的三个桶。</summary>
    public enum TrackerView
    {
        /// <summary>我方打出的伤害（按攻击者归因）。</summary>
        Outgoing,
        /// <summary>英雄承受的伤害（按承伤的英雄归因）。</summary>
        Incoming,
        /// <summary>英雄获得的治疗（按治疗来源归因）。</summary>
        Healing,
    }

    /// <summary>单个技能的小计。</summary>
    public struct SkillStats
    {
        public double Total;
        public long Hits;
        public long Crits;
        public float Max;
    }

    public class SourceStats
    {
        /// <summary>恢复来源的档数，和 HealKind 枚举一一对应。</summary>
        public const int HealKindCount = 6;

        /// <summary>显示名；null 表示日志里没有这个单位的定义（id 0 = 未知来源）。</summary>
        public string Name;
        public int InstanceId;
        /// <summary>EEquipClassType，0 = 未知（怪物 / 环境伤害）。决定面板配色。</summary>
        public int ClassType;

        public double Total;
        public long Hits;
        public long Crits;
        public float MaxHit;

        /// <summary>按 EDamageAttribute 分桶：Physical / Fire / Cold / Lightning / Chaos …</summary>
        public readonly double[] ByAttribute = new double[8];
        /// <summary>按 EDamageType 分桶，下标是位序 + 1（0 = None）：Melee / Projectile / AOE / Summon / DOT / Trap。</summary>
        public readonly double[] ByType = new double[8];
        /// <summary>治疗视图专用：按恢复来源分桶。</summary>
        public readonly double[] ByHealKind = new double[HealKindCount];

        /// <summary>按技能分桶，键是技能显示名。</summary>
        public readonly Dictionary<string, SkillStats> BySkill = new Dictionary<string, SkillStats>(StringComparer.Ordinal);

        /// <summary>
        /// 按对手分桶：输出是被打的怪物、承伤是打人的怪物、治疗是被治疗的英雄。
        /// 键见 <see cref="CombatParser.CounterpartKey"/>——怪物按种类归（同一种怪刷几百只也是一行）。
        /// </summary>
        public readonly Dictionary<string, SkillStats> ByTarget = new Dictionary<string, SkillStats>(StringComparer.Ordinal);

        /// <summary>每秒一个桶，下标 = 距本段开始的秒数。战斗记录里的曲线用它。</summary>
        public readonly List<float> PerSecond = new List<float>();

        public double FirstHitTime = -1d;
        public double LastHitTime;

        public double CritRate => Hits > 0 ? (double)Crits / Hits : 0d;

        public double ActiveSeconds
        {
            get
            {
                if (FirstHitTime < 0d) return 0d;
                var span = LastHitTime - FirstHitTime;
                return span > 0.05d ? span : 0.05d;
            }
        }

        public double Dps => ActiveSeconds > 0d ? Total / ActiveSeconds : 0d;

        /// <summary>占比最大的恢复来源（下标），没有治疗数据时为 -1。</summary>
        public int TopHealKind
        {
            get
            {
                var best = -1;
                for (var i = 0; i < ByHealKind.Length; i++)
                    if (ByHealKind[i] > 0d && (best < 0 || ByHealKind[i] > ByHealKind[best])) best = i;
                return best;
            }
        }

        internal void AddSkill(string skill, double amount, bool crit) => Tally(BySkill, skill, amount, crit);

        internal void AddTarget(string key, double amount, bool crit) => Tally(ByTarget, key, amount, crit);

        private static void Tally(Dictionary<string, SkillStats> into, string key, double amount, bool crit)
        {
            into.TryGetValue(key, out var st);
            st.Total += amount;
            st.Hits++;
            if (crit) st.Crits++;
            if (amount > st.Max) st.Max = (float)amount;
            into[key] = st;
        }
    }

    /// <summary>一段战斗（按关卡或按空闲时间切出来的）。</summary>
    public class Encounter
    {
        /// <summary>会话内的流水号，从 1 起。</summary>
        public int Index;
        /// <summary>会话内秒数。</summary>
        public double StartTime;
        public double LastActivityTime;

        /// <summary>关卡名（游戏原文）；null 表示不是按关卡名切出来的。</summary>
        public string StageName;
        /// <summary>同一关连续第几次（自动重复挑战），从 1 起。</summary>
        public int Run;
        /// <summary>连关卡名都读不到时的兜底序号（显示成「关卡 #n」）；0 = 不用。</summary>
        public int StageNo;

        public readonly Dictionary<int, SourceStats> Outgoing = new Dictionary<int, SourceStats>();
        public readonly Dictionary<int, SourceStats> Incoming = new Dictionary<int, SourceStats>();
        public readonly Dictionary<int, SourceStats> Healing = new Dictionary<int, SourceStats>();

        public Dictionary<int, SourceStats> Bucket(TrackerView v)
        {
            switch (v)
            {
                case TrackerView.Incoming: return Incoming;
                case TrackerView.Healing: return Healing;
                default: return Outgoing;
            }
        }

        public double TotalOf(TrackerView v)
        {
            double s = 0;
            foreach (var x in Bucket(v).Values) s += x.Total;
            return s;
        }

        public double OutgoingTotal => TotalOf(TrackerView.Outgoing);
        public double IncomingTotal => TotalOf(TrackerView.Incoming);
        public double HealingTotal => TotalOf(TrackerView.Healing);

        public bool IsEmpty => Outgoing.Count == 0 && Incoming.Count == 0 && Healing.Count == 0;

        public double DurationSeconds
        {
            get
            {
                var d = LastActivityTime - StartTime;
                return d > 0.05d ? d : 0.05d;
            }
        }
    }

    /// <summary>
    /// 一段已结束战斗的摘要，列表里显示的就是它。本局每一段都有一条（几十字节，常驻内存）；
    /// 详细数据（各来源、每秒桶、技能表）只有最近若干段留在内存里，更早的卸载掉，
    /// 要看时从日志文件重新解析——日志记的是事件，重新解析出来的就是同一段。
    /// </summary>
    public sealed class EncounterRecord
    {
        public int Index;
        public string StageName;
        public int Run;
        public int StageNo;
        public double StartTime;
        public double LastActivityTime;
        public double OutgoingTotal;
        public double IncomingTotal;
        public double HealingTotal;

        /// <summary>详细数据；null = 已从内存卸载，还在日志文件里。</summary>
        public Encounter Detail;

        public bool Loaded => Detail != null;

        public double DurationSeconds
        {
            get
            {
                var d = LastActivityTime - StartTime;
                return d > 0.05d ? d : 0.05d;
            }
        }

        public double TotalOf(TrackerView v)
            => v == TrackerView.Incoming ? IncomingTotal : v == TrackerView.Healing ? HealingTotal : OutgoingTotal;

        public static EncounterRecord Of(Encounter e) => new EncounterRecord
        {
            Index = e.Index,
            StageName = e.StageName,
            Run = e.Run,
            StageNo = e.StageNo,
            StartTime = e.StartTime,
            LastActivityTime = e.LastActivityTime,
            OutgoingTotal = e.OutgoingTotal,
            IncomingTotal = e.IncomingTotal,
            HealingTotal = e.HealingTotal,
            Detail = e,
        };

        /// <summary>重新解析出来的某一段是不是这一条：流水号和开始时间都对得上。</summary>
        public bool Matches(Encounter e)
            => e != null && e.Index == Index && Math.Abs(e.StartTime - StartTime) < 0.0005d;
    }

    /// <summary>日志里定义过的单位（英雄、怪物、召唤物…）。</summary>
    public sealed class UnitInfo
    {
        public int Id;
        /// <summary>'H' 英雄 / 'M' 怪物 / 'O' 其它。</summary>
        public char Kind;
        public int ClassType;
        /// <summary>稳定键：GameObject 名去掉 (Clone)，如 Hero_201 / Monster_30043。</summary>
        public string Key;
        /// <summary>记录时的显示名（玩家当时的语言）。</summary>
        public string Name;
    }

    /// <summary>日志里定义过的技能。</summary>
    public sealed class AbilityInfo
    {
        public int Id;
        /// <summary>稳定键：SkillNameKey（如 SkillName_20101），没有就是技能类名。</summary>
        public string Key;
        /// <summary>记录时的显示名。</summary>
        public string Name;
    }

    /// <summary>一局游戏的全部统计：实时会话，或者从日志文件解析出来的。</summary>
    public sealed class Session
    {
        /// <summary>日志文件路径；实时会话为 null。</summary>
        public string SourcePath;
        /// <summary>会话开始的墙钟时间（日志头里的 start），读不到为 null。</summary>
        public DateTimeOffset? StartedAt;
        public readonly Dictionary<string, string> Meta = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>本局所有已结束的段（摘要），最旧在前。详细数据可能已卸载，见 <see cref="EncounterRecord.Detail"/>。</summary>
        public readonly List<EncounterRecord> Records = new List<EncounterRecord>();
        /// <summary>进行中的段。导入的日志读完后它也收进 <see cref="Records"/>，这里变成 null。</summary>
        public Encounter Current;

        /// <summary>详细数据还在内存里的已结束段，最旧在前。导入的日志全部都在。</summary>
        public List<Encounter> Encounters
        {
            get
            {
                var list = new List<Encounter>(Records.Count);
                foreach (var r in Records)
                    if (r.Detail != null) list.Add(r.Detail);
                return list;
            }
        }

        /// <summary>按流水号找一条记录；找不到返回 null。</summary>
        public EncounterRecord RecordOf(int index)
        {
            // 流水号递增，从后往前二分也行，但段数不多，线性找足够
            for (var i = Records.Count - 1; i >= 0; i--)
                if (Records[i].Index == index) return Records[i];
            return null;
        }

        public readonly Dictionary<int, UnitInfo> Units = new Dictionary<int, UnitInfo>();
        public readonly Dictionary<int, AbilityInfo> Abilities = new Dictionary<int, AbilityInfo>();

        public int FormatVersion;
        public long EventCount;
        /// <summary>不认识的事件类型（更新版本写的日志）——跳过，不算错。</summary>
        public long UnknownLines;
        /// <summary>认识但解析失败的行，通常是文件末尾被截断的那一行。</summary>
        public long BadLines;
        /// <summary>没有读到结束标记：游戏没正常退出，或者还在写。</summary>
        public bool Truncated;
    }
}
