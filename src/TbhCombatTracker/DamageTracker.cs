using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;

namespace TbhCombatTracker
{
    /// <summary>一次伤害事件的分类信息，由 TakeDamage 的 hook 填充，供 ChangeHp 的 hook 消费。</summary>
    internal struct DamageContext
    {
        public bool Valid;
        public bool IsCritical;
        public int DamageType;      // EDamageType
        public int DamageAttribute; // EDamageAttribute
        public float OriginDamage;
    }

    /// <summary>事件里的一个单位：id + 定义它需要的全部信息（第一次出现时写进日志）。</summary>
    internal struct UnitRef
    {
        /// <summary>0 = 没有具体单位（环境伤害、自愈…）。</summary>
        public int Id;
        /// <summary>'H' 英雄 / 'M' 怪物 / 'O' 其它。</summary>
        public char Kind;
        public int ClassType;
        public string Key;
        public string Name;
    }

    /// <summary>技能：稳定键 + 显示名。会话内编号在第一次写进日志时分配。</summary>
    internal sealed class SkillRef
    {
        public string Key;
        public string Name;
        public int Aid;
    }

    /// <summary>
    /// 实时统计的入口：hook 在这里产出战斗事件。
    ///
    /// 每个事件同时交给两处——本局的实时解析器（面板上的数字）和日志写入器（本局的日志文件）。
    /// 解析器和导入日志时用的是同一个 <see cref="CombatParser"/>，
    /// 所以"现在看到的"和"以后导入这份日志看到的"一定一致。
    /// 金额和时间先规整成日志里的写法再分发，连小数点后的尾数都对得上。
    /// </summary>
    internal static class DamageTracker
    {
        private static readonly object Gate = new object();

        /// <summary>会话时钟：从插件加载起算。用 Stopwatch 而不是 Time.realtimeSinceStartup——每个事件都要取时间，不必跨 IL2CPP 边界。</summary>
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        [ThreadStatic] private static DamageContext _pending;

        private static readonly Session LiveSession = new Session();
        private static readonly CombatParser Parser = new CombatParser(LiveSession, new CombatParser.Options());
        private static bool _logEnabled;

        /// <summary>写进日志的单位定义，用来判断是否要（重新）写一条 U。怪物一波一波地刷，别无限涨。</summary>
        private static readonly Dictionary<int, (char Kind, int Class, string Name)> SentUnits =
            new Dictionary<int, (char, int, string)>();
        private static int _nextAbility = 1;

        /// <summary>本局的实时会话。只在主线程上读写。</summary>
        public static Session Live => LiveSession;

        /// <summary>实时的当前段——浮窗显示的就是它。</summary>
        public static Encounter Current { get { lock (Gate) return LiveSession.Current; } }

        /// <summary>会话内秒数。</summary>
        internal static double Now => Clock.Elapsed.TotalSeconds;

        /// <summary>插件加载时调一次：按配置设好解析器，开始写日志。</summary>
        public static void Init()
        {
            lock (Gate)
            {
                var opt = Parser.Opt;
                opt.SegmentByStage = Mod.Config.SegmentByStage.Value;
                opt.IdleSeconds = Mod.Config.IdleResetSeconds.Value;
                opt.MaxEncounters = Math.Clamp(Mod.Config.HistorySize.Value, 0, 1000);
                Parser.Started = e => Mod.Log.Msg($"[stage] === 开始统计 {Strings.EncounterTitle(e)} ===");
                Parser.Relabeled = e => Mod.Log.Msg($"[stage] 当前段更正为 {Strings.EncounterTitle(e)}");
                Parser.Closed = e => Mod.Log.Msg(
                    $"[stage] 本段结束：{Strings.EncounterTitle(e)}  {e.DurationSeconds:0.0}s  " +
                    $"输出 {Overlay.Short(e.OutgoingTotal)}  承伤 {Overlay.Short(e.IncomingTotal)}  " +
                    $"治疗 {Overlay.Short(e.HealingTotal)}");
                LiveSession.StartedAt = DateTimeOffset.Now - Clock.Elapsed;
            }

            if (!Mod.Config.LogEvents.Value)
            {
                Mod.Log.Msg("战斗日志已在配置里关闭，主面板只有本局的数据。");
                return;
            }

            var meta = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("mod", BuildInfo.Version),
                new KeyValuePair<string, string>("game", ReadGameVersion() ?? "?"),
                new KeyValuePair<string, string>("start", LiveSession.StartedAt.Value.ToString("o", CultureInfo.InvariantCulture)),
            };
            EventLogWriter.Start(LogDirectory, meta, Mod.Config.LogRetentionDays.Value);
            _logEnabled = EventLogWriter.Running;
        }

        public static string LogDirectory => Path.Combine(Paths.BepInExRootPath, "TbhCombatTracker", "logs");

        /// <summary>导入日志时用的解析选项：分段方式跟当前配置一致，段数不设上限。</summary>
        public static CombatParser.Options ImportOptions() => new CombatParser.Options
        {
            SegmentByStage = Mod.Config.SegmentByStage.Value,
            IdleSeconds = Mod.Config.IdleResetSeconds.Value,
            MaxEncounters = 0,
        };

        /// <summary>关卡信号读不出来了：本局退回按空闲时间分段。</summary>
        public static void DisableStageSegmentation()
        {
            lock (Gate) Parser.Opt.SegmentByStage = false;
        }

        public static List<SourceStats> Snapshot(TrackerView view)
        {
            lock (Gate) return LiveSession.Current.Bucket(view).Values.OrderByDescending(v => v.Total).ToList();
        }

        // ------------------------------------------------------------------ TakeDamage 的 hook 用这两个方法夹住一次伤害结算

        /// <summary>
        /// 进入一次伤害结算，返回之前的上下文供 Finalizer 恢复。
        ///
        /// 必须是保存/恢复而不是简单的 push/clear：TakeDamage 可能调 base 的实现，
        /// 两层都会进这里；内层若直接清空，回到外层再扣血时上下文就没了，
        /// 暴击率和元素拆分会莫名其妙丢一部分。
        /// </summary>
        internal static DamageContext PushContext(bool crit, int damageType, int damageAttribute, float originDamage)
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

        // ------------------------------------------------------------------ ChangeHp 的 hook 记录最终数值

        /// <summary>对怪物造成的伤害。攻击者 Id = 0 表示未知来源（陷阱、召唤物主人已死…）。</summary>
        internal static void RecordOutgoing(in UnitRef attacker, in UnitRef target, float amount, SkillRef skill)
        {
            if (!Valid(amount)) return;
            var ctx = _pending;
            lock (Gate)
            {
                var t = EventLogFormat.CanonicalTime(Now);
                EnsureUnit(t, attacker);
                EnsureUnit(t, target);
                Emit(Hit(EventKind.Damage, t, attacker.Id, target.Id, amount, ctx, EnsureAbility(t, skill)));
            }
        }

        /// <summary>英雄承受的伤害。</summary>
        internal static void RecordIncoming(in UnitRef victim, in UnitRef attacker, float amount, SkillRef skill)
        {
            if (!Valid(amount)) return;
            var ctx = _pending;
            lock (Gate)
            {
                var t = EventLogFormat.CanonicalTime(Now);
                EnsureUnit(t, victim);
                EnsureUnit(t, attacker);
                Emit(Hit(EventKind.Taken, t, victim.Id, attacker.Id, amount, ctx, EnsureAbility(t, skill)));
            }
        }

        /// <summary>
        /// 英雄获得的生命恢复。<paramref name="caster"/> 是技能治疗归因到的施法者，Id = 0 表示自愈。
        /// 除了记录时的判定 <paramref name="kind"/>，还带上判定依据的原始事实（恢复总入口的两个标志、
        /// 所在的上游括号），以后改进分类规则时旧日志也能重新分类。
        /// </summary>
        internal static void RecordHealing(in UnitRef target, in UnitRef caster, float amount, HealKind kind,
                                           sbyte flagB, sbyte flagC, int bracket)
        {
            if (!Valid(amount)) return;
            lock (Gate)
            {
                var t = EventLogFormat.CanonicalTime(Now);
                EnsureUnit(t, target);
                EnsureUnit(t, caster);
                Emit(new CombatEvent
                {
                    T = t, Kind = EventKind.Heal, A = target.Id, B = caster.Id,
                    Amount = EventLogFormat.CanonicalAmount(amount),
                    HealKind = (int)kind, FlagB = flagB, FlagC = flagC, Bracket = bracket,
                });
            }
        }

        // ------------------------------------------------------------------ 关卡信号与手动重置

        internal static void StageName(string name) => Signal(new CombatEvent { Kind = EventKind.StageName, Text = name });
        internal static void StageStart(bool value) => Signal(new CombatEvent { Kind = EventKind.StageStart, Flag = value });
        internal static void StageWave(string state) => Signal(new CombatEvent { Kind = EventKind.Wave, Text = state });

        /// <summary>F10 / 浮窗上的「重置」：当前段收进记录，另起一段。</summary>
        public static void ResetCurrent() => Signal(new CombatEvent { Kind = EventKind.Reset });

        private static void Signal(CombatEvent e)
        {
            lock (Gate)
            {
                e.T = EventLogFormat.CanonicalTime(Now);
                Emit(e);
            }
        }

        // ------------------------------------------------------------------ 内部

        private static bool Valid(float amount)
            => !(float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f);

        private static CombatEvent Hit(EventKind kind, double t, int a, int b, float amount, in DamageContext ctx, int aid)
            => new CombatEvent
            {
                T = t, Kind = kind, A = a, B = b,
                Amount = EventLogFormat.CanonicalAmount(amount),
                Origin = ctx.Valid ? EventLogFormat.CanonicalAmount(ctx.OriginDamage) : float.NaN,
                Crit = ctx.Valid ? (sbyte)(ctx.IsCritical ? 1 : 0) : (sbyte)-1,
                DamageType = ctx.Valid ? ctx.DamageType : -1,
                Attribute = ctx.Valid ? ctx.DamageAttribute : -1,
                Ability = aid,
            };

        private static void Emit(in CombatEvent e)
        {
            Parser.Apply(e);
            if (_logEnabled) EventLogWriter.Enqueue(e);
        }

        /// <summary>单位第一次出现、或名字 / 职业变了，就写一条定义。</summary>
        private static void EnsureUnit(double t, in UnitRef u)
        {
            if (u.Id == 0) return;
            var kind = u.Kind == '\0' ? 'O' : u.Kind;
            if (SentUnits.TryGetValue(u.Id, out var sent) &&
                sent.Kind == kind && sent.Class == u.ClassType && sent.Name == u.Name)
                return;

            // 重新写一遍定义是无害的，所以满了直接清空，不做精细淘汰
            if (SentUnits.Count > 20000) SentUnits.Clear();
            SentUnits[u.Id] = (kind, u.ClassType, u.Name);

            Emit(new CombatEvent
            {
                T = t, Kind = EventKind.Unit, A = u.Id, UnitKind = kind,
                ClassType = u.ClassType, Key = u.Key ?? u.Name, Text = u.Name,
            });
        }

        private static int EnsureAbility(double t, SkillRef s)
        {
            if (s == null) return 0;
            if (s.Aid != 0) return s.Aid;
            s.Aid = _nextAbility++;
            Emit(new CombatEvent { T = t, Kind = EventKind.Ability, A = s.Aid, Key = s.Key ?? s.Name, Text = s.Name });
            return s.Aid;
        }

        private static string ReadGameVersion()
        {
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "Version.txt");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ 伤害类型 / 元素的显示名

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
            return Strings.DamageType(raw);
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

        // ------------------------------------------------------------------ CSV 导出

        /// <summary>把一段导出成 CSV。浮窗的 F11 导出实时的当前段，主面板导出选中的那段。</summary>
        public static string ExportCsv(Encounter enc, string tag = null)
        {
            try
            {
                if (enc == null) return null;

                var dir = Path.Combine(Paths.BepInExRootPath, "TbhCombatTracker");
                Directory.CreateDirectory(dir);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var file = Path.Combine(dir, $"damage-{stamp}{(tag != null ? "-" + tag : "")}.csv");

                var sb = new StringBuilder();
                // 第一行说明这份数据属于哪一段：关卡分段模式下是关卡名（重复挑战带序号），否则是战斗序号
                sb.AppendLine($"# segment: {Strings.EncounterTitle(enc)}, duration_seconds: {F(enc.DurationSeconds)}");
                sb.AppendLine("direction,source,job,class_type,instance_id,total,dps,hits,crits,crit_rate,max_hit,active_seconds," +
                              string.Join(",", AttributeNames.Take(7).Select(n => "attr_" + n)) + "," +
                              string.Join(",", TypeNames.Take(7).Select(n => "type_" + n)));

                void Dump(string direction, IEnumerable<SourceStats> rows)
                {
                    foreach (var s in rows.OrderByDescending(r => r.Total))
                    {
                        sb.Append(direction).Append(',')
                          .Append(Csv(Strings.SourceName(s))).Append(',')
                          .Append(Csv(JobTable.Label(s.ClassType) ?? "")).Append(',')
                          .Append(s.ClassType.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.InstanceId.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(F(s.Total)).Append(',')
                          .Append(F(s.Dps)).Append(',')
                          .Append(s.Hits.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.Crits.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(F(s.CritRate)).Append(',')
                          .Append(F(s.MaxHit)).Append(',')
                          .Append(F(s.ActiveSeconds));
                        for (var i = 0; i < 7; i++) sb.Append(',').Append(F(s.ByAttribute[i]));
                        for (var i = 0; i < 7; i++) sb.Append(',').Append(F(s.ByType[i]));
                        sb.AppendLine();
                    }
                }

                lock (Gate)
                {
                    Dump("outgoing", enc.Outgoing.Values);
                    Dump("incoming", enc.Incoming.Values);
                    Dump("healing", enc.Healing.Values);

                    // 技能拆分单开一段：每个来源的技能数不固定，做成列会很难看
                    sb.AppendLine();
                    sb.AppendLine("# 技能拆分");
                    sb.AppendLine("direction,source,skill,total,hits,crits,max_hit,share_of_source");
                    foreach (var s in enc.Outgoing.Values.OrderByDescending(v => v.Total))
                    {
                        foreach (var kv in s.BySkill.OrderByDescending(k => k.Value.Total))
                        {
                            sb.Append("outgoing,").Append(Csv(Strings.SourceName(s))).Append(',')
                              .Append(Csv(Strings.SkillName(kv.Key))).Append(',')
                              .Append(F(kv.Value.Total)).Append(',')
                              .Append(kv.Value.Hits.ToString(CultureInfo.InvariantCulture)).Append(',')
                              .Append(kv.Value.Crits.ToString(CultureInfo.InvariantCulture)).Append(',')
                              .Append(F(kv.Value.Max)).Append(',')
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
