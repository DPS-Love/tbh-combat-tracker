using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>一次伤害事件的分类信息，由 Monster.een 的 hook 填充，供 ph.gsd 的 hook 消费。</summary>
    internal struct DamageContext
    {
        public bool Valid;
        public bool IsCritical;
        public int DamageType;      // EDamageType
        public int DamageAttribute; // EDamageAttribute
        public float OriginDamage;
    }

    public class SourceStats
    {
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
        /// <summary>按 EDamageType 分桶，索引是位序：Melee / Projectile / AOE / Summon / DOT / Trap。</summary>
        public readonly double[] ByType = new double[8];
        /// <summary>治疗视图专用：按 <see cref="HealKind"/> 分桶。</summary>
        public readonly double[] ByHealKind = new double[Healing.KindCount];

        /// <summary>
        /// 按技能分桶。键是技能名（普通攻击 / MeteorStrike / SacredBlade …）。
        /// 饼图面板要的就是这份数据。
        /// </summary>
        public readonly Dictionary<string, SkillStats> BySkill = new Dictionary<string, SkillStats>();

        internal void AddSkill(string skill, double amount)
        {
            if (string.IsNullOrEmpty(skill)) skill = BuiltinText.UnknownSkill;
            if (!BySkill.TryGetValue(skill, out var st)) st = new SkillStats();
            st.Total += amount;
            st.Hits++;
            BySkill[skill] = st;
        }

        /// <summary>占比最大的恢复来源名，治疗卡片上显示用。</summary>
        public string TopHealKind
        {
            get
            {
                var best = -1;
                for (var i = 0; i < ByHealKind.Length; i++)
                    if (best < 0 || ByHealKind[i] > ByHealKind[best]) best = i;
                return best >= 0 && ByHealKind[best] > 0d ? Healing.KindName(best) : null;
            }
        }

        public float FirstHitTime = -1f;
        public float LastHitTime;

        public double CritRate => Hits > 0 ? (double)Crits / Hits : 0d;

        public double ActiveSeconds
        {
            get
            {
                if (FirstHitTime < 0f) return 0d;
                var span = LastHitTime - FirstHitTime;
                return span > 0.05f ? span : 0.05d;
            }
        }

        public double Dps => ActiveSeconds > 0d ? Total / ActiveSeconds : 0d;
    }

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
    }

    public class Encounter
    {
        public int Index;
        public float StartTime;
        public float LastActivityTime;
        /// <summary>关卡分段模式下的标题，例如 "关卡 #3"；空则用战斗序号。</summary>
        public string Label;

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

        public double DurationSeconds
        {
            get
            {
                var d = LastActivityTime - StartTime;
                return d > 0.05f ? d : 0.05d;
            }
        }
    }

    public static class DamageTracker
    {
        private static readonly object Gate = new object();

        [ThreadStatic] private static DamageContext _pending;

        private static Encounter _current = NewEncounter(1);
        private static int _encounterCounter = 1;

        public static Encounter Current { get { lock (Gate) return _current; } }

        // ---- Monster.een 的 hook 用这两个方法夹住一次伤害结算 ----

        /// <summary>
        /// 进入一次伤害结算，返回之前的上下文供 Finalizer 恢复。
        ///
        /// 必须是保存/恢复而不是简单的 push/clear：Monster.grd 可能调 base.grd，
        /// 两层都会进这里；内层若直接清空，回到外层再扣血时上下文就没了，
        /// 暴击率和元素拆分会莫名其妙丢一部分。
        /// </summary>
        internal static DamageContext PushContext(bool crit, int damageType, int damageAttribute,
                                                  float originDamage)
        {
            var prev = _pending;
            _pending = new DamageContext
            {
                Valid = true,
                IsCritical = crit,
                DamageType = damageType,
                DamageAttribute = damageAttribute,
                OriginDamage = originDamage,
            };
            return prev;
        }

        internal static void RestoreContext(DamageContext prev) => _pending = prev;

        // ---- ph.gsd / pj.gsd 的 hook 记录最终数值 ----

        internal static void RecordOutgoing(int attackerId, SourceIdentity attacker, float amount,
                                            string skill = null)
        {
            _pendingSkill = skill;
            try { Record(TrackerView.Outgoing, attackerId, attacker, amount); }
            finally { _pendingSkill = null; }
        }

        [ThreadStatic] private static string _pendingSkill;

        internal static void RecordIncoming(int victimId, SourceIdentity victim, float amount,
                                            string skill = null)
        {
            _pendingSkill = skill;
            try { Record(TrackerView.Incoming, victimId, victim, amount); }
            finally { _pendingSkill = null; }
        }

        internal static void RecordHealing(int sourceId, SourceIdentity source, float amount, HealKind kind)
        {
            _pendingHealKind = (int)kind;
            try { Record(TrackerView.Healing, sourceId, source, amount); }
            finally { _pendingHealKind = -1; }
        }

        [ThreadStatic] private static int _pendingHealKind;

        private static void Record(TrackerView view, int id, SourceIdentity who, float amount)
        {
            // 0 和 NaN 一律丢弃——它们会把 DPS 和暴击率算歪。
            if (float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f)
                return;

            var now = Time.realtimeSinceStartup;
            var ctx = _pending;

            lock (Gate)
            {
                MaybeRollEncounter(now);

                var bucket = _current.Bucket(view);
                if (!bucket.TryGetValue(id, out var s))
                {
                    s = new SourceStats { Name = who.Name, InstanceId = id, ClassType = who.ClassType };
                    bucket[id] = s;
                }
                else
                {
                    // 首次记录时职业可能还没解析出来，后面补上
                    if (s.Name == null && who.Name != null) s.Name = who.Name;
                    if (s.ClassType == 0 && who.ClassType != 0) s.ClassType = who.ClassType;
                }

                s.Total += amount;
                s.Hits++;
                if (amount > s.MaxHit) s.MaxHit = amount;
                if (s.FirstHitTime < 0f) s.FirstHitTime = now;
                s.LastHitTime = now;

                if (_pendingSkill != null) s.AddSkill(_pendingSkill, amount);

                if (view == TrackerView.Healing)
                {
                    var k = _pendingHealKind;
                    if (k >= 0 && k < s.ByHealKind.Length) s.ByHealKind[k] += amount;
                }

                // 分类上下文（暴击/元素/伤害类型）只对伤害有意义，治疗不该沾
                if (ctx.Valid && view != TrackerView.Healing)
                {
                    if (ctx.IsCritical) s.Crits++;

                    var attr = ctx.DamageAttribute;
                    if (attr >= 0 && attr < s.ByAttribute.Length)
                        s.ByAttribute[attr] += amount;

                    // EDamageType 是位标志（Melee=1, Projectile=2, AOE=4, Summon=8, DOT=16, Trap=32），
                    // 一次伤害理论上只带一个位，但按位拆开更保险。
                    var t = ctx.DamageType;
                    if (t == 0)
                    {
                        s.ByType[0] += amount;
                    }
                    else
                    {
                        for (int bit = 0; bit < 7 && t != 0; bit++)
                        {
                            if ((t & (1 << bit)) != 0)
                                s.ByType[bit + 1] += amount;
                        }
                    }
                }

                _current.LastActivityTime = now;
            }
        }

        /// <summary>
        /// 关卡开始时切一段新的。由 <see cref="StageWatcher"/> 在检测到
        /// StageManager 进入新关卡时调用。
        /// </summary>
        public static void BeginStage(string label)
        {
            lock (Gate)
            {
                // 上一段是空的就直接改标签，别平白多出一堆空战斗
                if (_current.Outgoing.Count == 0 && _current.Incoming.Count == 0 &&
                    _current.Healing.Count == 0)
                {
                    _current.Label = label;
                    _current.StartTime = Time.realtimeSinceStartup;
                    _current.LastActivityTime = _current.StartTime;
                    return;
                }

                _encounterCounter++;
                _current = NewEncounter(_encounterCounter);
                _current.Label = label;
            }
        }

        private static void MaybeRollEncounter(float now)
        {
            // 按关卡分段时不再用空闲时间切，否则关卡内的间歇会被误切
            if (Mod.Config?.SegmentByStage?.Value == true) return;

            var idle = Mod.Config?.IdleResetSeconds?.Value ?? 8f;
            if (idle <= 0f) return;

            if (_current.Outgoing.Count == 0 && _current.Incoming.Count == 0 &&
                _current.Healing.Count == 0)
            {
                _current.StartTime = now;
                return;
            }

            if (now - _current.LastActivityTime >= idle)
            {
                _encounterCounter++;
                _current = NewEncounter(_encounterCounter);
                _current.StartTime = now;
            }
        }

        public static void ResetCurrent()
        {
            lock (Gate)
            {
                _encounterCounter++;
                _current = NewEncounter(_encounterCounter);
                _current.StartTime = Time.realtimeSinceStartup;
            }
        }

        private static Encounter NewEncounter(int index)
        {
            var t = 0f;
            try { t = Time.realtimeSinceStartup; } catch { /* 初始化早于 Unity 时钟时会抛 */ }
            return new Encounter { Index = index, StartTime = t, LastActivityTime = t };
        }

        public static List<SourceStats> Snapshot(TrackerView view)
        {
            lock (Gate) return _current.Bucket(view).Values.OrderByDescending(v => v.Total).ToList();
        }

        private static readonly string[] AttributeNames =
            { "Physical", "Fire", "Cold", "Lightning", "Chaos", "AllElement", "None", "?" };
        private static readonly string[] TypeNames =
            { "None", "Melee", "Projectile", "AOE", "Summon", "DOT", "Trap", "?" };

        /// <summary>
        /// 元素属性：游戏有译文，**键就是裸枚举名**（实测 'Fire' → 火焰、'Cold' → 冰冷）。
        /// 之前试的 EDamageAttributeFire / EDamageAttribute_Fire 都是错的。
        /// </summary>
        public static string AttributeName(int i)
        {
            var raw = i >= 0 && i < AttributeNames.Length ? AttributeNames[i] : "?";
            return Localize.TryGet(raw) ?? raw;
        }

        /// <summary>
        /// 伤害类型：游戏**没有**单条的 "近战" / "投射物" 词条（裸枚举名、EDamageType 前缀都试过，
        /// 全部落空），但属性面板上有 "增加近战伤害" 这样的整句。把四句共有的部分剥掉，
        /// 剩下的就是类型词——见 <see cref="Localize.LiftDistinctParts"/>。
        ///
        /// DOT 和 Trap 在任何语言的字符串表里都查不到出处，只能继续用内置文本。
        /// </summary>
        public static string TypeName(int i)
        {
            var raw = i >= 0 && i < TypeNames.Length ? TypeNames[i] : "?";
            if (GameTypeNames.TryGetValue(raw, out var v)) return v;
            return BuiltinText.DamageType(raw);
        }

        private static Dictionary<string, string> _gameTypeNames;

        /// <summary>
        /// 从游戏文本还原出来的伤害类型名，还原不出来就是空表（调用方自动退回内置文本）。
        /// 只做一次：查译文和字符串处理都不便宜，而面板每帧都要用。
        /// </summary>
        private static Dictionary<string, string> GameTypeNames
        {
            get
            {
                if (_gameTypeNames != null) return _gameTypeNames;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                var lifted = Localize.LiftDistinctParts(
                    "StatName_IncreaseMeleeDamage",
                    "StatName_IncreaseProjectileDamage",
                    "StatName_IncreaseAreaOfEffectDamage",
                    "StatName_IncreaseSummonDamage");

                if (lifted != null)
                {
                    map["Melee"] = lifted[0];
                    map["Projectile"] = lifted[1];
                    map["AOE"] = lifted[2];
                    map["Summon"] = lifted[3];
                    Mod.Log.Msg("伤害类型译名取自游戏属性面板：" + string.Join(" / ", lifted));
                }
                else
                {
                    Mod.Log.Warning("没能从游戏文本还原伤害类型译名，改用内置文本。");
                }

                _gameTypeNames = map;
                return map;
            }
        }

        public static string ExportCsv(string tag = null)
        {
            try
            {
                var dir = Path.Combine(Paths.BepInExRootPath, "TbhCombatTracker");
                Directory.CreateDirectory(dir);

                Encounter enc;
                lock (Gate) enc = _current;

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var file = Path.Combine(dir, $"damage-{stamp}{(tag != null ? "-" + tag : "")}.csv");

                var sb = new StringBuilder();
                sb.AppendLine("direction,source,job,class_type,instance_id,total,dps,hits,crits,crit_rate,max_hit,active_seconds," +
                              string.Join(",", AttributeNames.Take(7).Select(n => "attr_" + n)) + "," +
                              string.Join(",", TypeNames.Take(7).Select(n => "type_" + n)));

                void Dump(string dir2, IEnumerable<SourceStats> rows)
                {
                    foreach (var s in rows.OrderByDescending(r => r.Total))
                    {
                        sb.Append(dir2).Append(',')
                          .Append(Csv(s.Name)).Append(',')
                          .Append(Csv(JobTable.Label(s.ClassType) ?? "")).Append(',')
                          .Append(s.ClassType).Append(',')
                          .Append(s.InstanceId).Append(',')
                          .Append(F(s.Total)).Append(',')
                          .Append(F(s.Dps)).Append(',')
                          .Append(s.Hits).Append(',')
                          .Append(s.Crits).Append(',')
                          .Append(F(s.CritRate)).Append(',')
                          .Append(F(s.MaxHit)).Append(',')
                          .Append(F(s.ActiveSeconds));
                        for (int i = 0; i < 7; i++) sb.Append(',').Append(F(s.ByAttribute[i]));
                        for (int i = 0; i < 7; i++) sb.Append(',').Append(F(s.ByType[i]));
                        sb.AppendLine();
                    }
                }

                lock (Gate)
                {
                    Dump("outgoing", enc.Outgoing.Values);
                    Dump("incoming", enc.Incoming.Values);
                    Dump("healing", enc.Healing.Values);
                }

                // 技能拆分单开一段：每个来源的技能数不固定，做成列会很难看
                sb.AppendLine();
                sb.AppendLine("# 技能拆分");
                sb.AppendLine("direction,source,skill,total,hits,share_of_source");
                lock (Gate)
                {
                    foreach (var s in enc.Outgoing.Values.OrderByDescending(v => v.Total))
                    {
                        foreach (var kv in s.BySkill.OrderByDescending(k => k.Value.Total))
                        {
                            sb.Append("outgoing,").Append(Csv(s.Name)).Append(',')
                              .Append(Csv(kv.Key)).Append(',')
                              .Append(F(kv.Value.Total)).Append(',')
                              .Append(kv.Value.Hits).Append(',')
                              .Append(F(s.Total > 0d ? kv.Value.Total / s.Total : 0d))
                              .AppendLine();
                        }
                    }
                }

                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
                return file;
            }
            catch (Exception e)
            {
                Mod.Log?.Error($"导出 CSV 失败：{e}");
                return null;
            }
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }
    }
}
