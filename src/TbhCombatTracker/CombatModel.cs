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

        /// <summary>每秒一个桶，下标 = 距本段开始的秒数。主面板的曲线用它。</summary>
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

        internal void AddSkill(string skill, double amount, bool crit)
        {
            BySkill.TryGetValue(skill, out var st);
            st.Total += amount;
            st.Hits++;
            if (crit) st.Crits++;
            if (amount > st.Max) st.Max = (float)amount;
            BySkill[skill] = st;
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

        /// <summary>已结束的段，最旧在前。</summary>
        public readonly List<Encounter> Encounters = new List<Encounter>();
        /// <summary>进行中的段。导入的日志读完后它也收进 <see cref="Encounters"/>，这里变成 null。</summary>
        public Encounter Current;
        /// <summary>超出保留上限被丢掉的段数（只有实时会话会丢，日志里仍然都在）。</summary>
        public int DroppedEncounters;

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
