using System;
using System.Collections.Generic;

namespace TbhCombatTracker
{
    /// <summary>
    /// 从战斗事件算出统计：分段、归因、分桶全在这里。
    ///
    /// **实时统计和导入日志走的是同一个解析器**：游戏里 hook 产出的事件一边写进日志文件、
    /// 一边喂给这里；导入时从文件读出同样的事件再喂一遍。所以两边的数字必然一致，
    /// 以后给解析器加了新维度，旧日志导入时也能算出来——这是按事件记日志、而不是按结果记日志的全部意义。
    ///
    /// 纯 C#：不碰 Unity 和游戏类型，时间只用事件自带的时间戳。
    /// </summary>
    public sealed class CombatParser
    {
        public sealed class Options
        {
            /// <summary>按关卡信号分段；关掉则按 <see cref="IdleSeconds"/> 的空闲时间分段。</summary>
            public bool SegmentByStage = true;
            public float IdleSeconds = 8f;
            /// <summary>最多保留几段已结束的战斗，0 = 不限。只给实时会话用，导入的日志全部保留。</summary>
            public int MaxEncounters;
        }

        /// <summary>切段后的静默期，防止两个关卡信号同时触发切出一个空段。</summary>
        public const double DebounceSeconds = 1.5;

        /// <summary>
        /// b_StageStart 切段之后这么久之内到达的关卡名变化，视为同一次换关：给刚切的段改名，
        /// 而不是再切一段。游戏是先翻标志、后刷新关卡名文本还是相反，两种顺序都见过，不能押一边。
        /// </summary>
        public const double RelabelWindowSeconds = 4.0;

        /// <summary>每秒一个桶，一段最多记这么久，防止一段永远不结束时无限增长。</summary>
        private const int MaxBucketSeconds = 6 * 3600;

        public Session Session { get; }
        public Options Opt { get; }

        /// <summary>新的一段开始（已经知道标题）。实时会话用它打日志。</summary>
        public Action<Encounter> Started;
        /// <summary>刚开始的那段被更正了标题。</summary>
        public Action<Encounter> Relabeled;
        /// <summary>一段结束、收进 <see cref="Session.Encounters"/>。</summary>
        public Action<Encounter> Closed;

        private int _encounterCounter = 1;

        // ---- 关卡分段的状态 ----
        private string _lastStageName;
        private bool _hasStageStart;
        private bool _lastStageStart;
        private double _lastSegmentAt = double.NegativeInfinity;
        private bool _lastCutByFlag;
        private string _segName;
        private int _segRun;
        private int _stageCount;

        public CombatParser(Session session, Options options)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Opt = options ?? new Options();
            if (Session.Current == null)
                Session.Current = new Encounter { Index = _encounterCounter };
        }

        public void Apply(in CombatEvent e)
        {
            Session.EventCount++;
            switch (e.Kind)
            {
                case EventKind.Unit:
                    DefineUnit(e);
                    break;

                case EventKind.Ability:
                    Session.Abilities[e.A] = new AbilityInfo { Id = e.A, Key = e.Key, Name = e.Text };
                    break;

                case EventKind.Damage:
                    Add(TrackerView.Outgoing, e.A, e.T, e.Amount, e.Crit, e.DamageType, e.Attribute,
                        SkillName(e.Ability), -1);
                    break;

                case EventKind.Taken:
                    // 承伤按挨打的英雄归因（A），攻击者（B）目前只进日志
                    Add(TrackerView.Incoming, e.A, e.T, e.Amount, e.Crit, e.DamageType, e.Attribute,
                        SkillName(e.Ability), -1);
                    break;

                case EventKind.Heal:
                    // 技能治疗归施法者（B），自愈类（B = 0）归被恢复的英雄自己（A）
                    Add(TrackerView.Healing, e.B != 0 ? e.B : e.A, e.T, e.Amount, -1, -1, -1, null, e.HealKind);
                    break;

                case EventKind.StageName:
                    OnStageName(e.T, e.Text);
                    break;

                case EventKind.StageStart:
                    OnStageStart(e.T, e.Flag);
                    break;

                case EventKind.Wave:
                    break;   // 波次状态只进日志，目前不参与统计

                case EventKind.Reset:
                    Roll(e.T);
                    break;
            }
        }

        /// <summary>导入的日志读完了：进行中的那段也收进列表。</summary>
        public void Finish()
        {
            var c = Session.Current;
            if (c != null && !c.IsEmpty)
            {
                Session.Encounters.Add(c);
                Closed?.Invoke(c);
            }
            Session.Current = null;
        }

        // ------------------------------------------------------------------ 统计

        private string SkillName(int aid)
        {
            if (aid <= 0) return null;
            if (Session.Abilities.TryGetValue(aid, out var a))
                return !string.IsNullOrEmpty(a.Name) ? a.Name : (a.Key ?? "");
            return "#" + aid;
        }

        private void Add(TrackerView view, int id, double t, float amount, int crit, int type, int attr,
                         string skill, int healKind)
        {
            // 0 和 NaN 一律丢弃——它们会把 DPS 和暴击率算歪
            if (float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;

            TouchIdle(t);

            var enc = Session.Current;
            var bucket = enc.Bucket(view);
            if (!bucket.TryGetValue(id, out var s))
            {
                s = new SourceStats { InstanceId = id };
                if (Session.Units.TryGetValue(id, out var u))
                {
                    s.Name = u.Name;
                    s.ClassType = u.ClassType;
                }
                bucket[id] = s;
            }

            s.Total += amount;
            s.Hits++;
            if (amount > s.MaxHit) s.MaxHit = amount;
            if (s.FirstHitTime < 0d) s.FirstHitTime = t;
            s.LastHitTime = t;

            var sec = (int)Math.Floor(t - enc.StartTime);
            if (sec < 0) sec = 0;
            else if (sec > MaxBucketSeconds) sec = MaxBucketSeconds;
            while (s.PerSecond.Count <= sec) s.PerSecond.Add(0f);
            s.PerSecond[sec] += amount;

            if (skill != null) s.AddSkill(skill, amount, crit == 1);

            if (view == TrackerView.Healing)
            {
                if (healKind >= 0 && healKind < s.ByHealKind.Length) s.ByHealKind[healKind] += amount;
            }
            else if (crit >= 0)
            {
                // 有分类上下文（暴击 / 元素 / 伤害类型）才统计这几项
                if (crit == 1) s.Crits++;

                if (attr >= 0 && attr < s.ByAttribute.Length) s.ByAttribute[attr] += amount;

                // EDamageType 是位标志（Melee=1, Projectile=2, AOE=4, Summon=8, DOT=16, Trap=32），
                // 一次伤害理论上只带一个位，但按位拆开更保险
                if (type == 0)
                {
                    s.ByType[0] += amount;
                }
                else if (type > 0)
                {
                    for (var bit = 0; bit < 7; bit++)
                        if ((type & (1 << bit)) != 0) s.ByType[bit + 1] += amount;
                }
            }

            enc.LastActivityTime = t;
        }

        private void DefineUnit(in CombatEvent e)
        {
            var u = new UnitInfo
            {
                Id = e.A,
                Kind = e.UnitKind,
                ClassType = e.ClassType,
                Key = e.Key,
                Name = !string.IsNullOrEmpty(e.Text) ? e.Text : e.Key,
            };
            Session.Units[u.Id] = u;

            // 首次记录时名字 / 职业可能还没解析出来，后面补上
            var c = Session.Current;
            if (c == null) return;
            Patch(c.Outgoing, u);
            Patch(c.Incoming, u);
            Patch(c.Healing, u);
        }

        private static void Patch(Dictionary<int, SourceStats> bucket, UnitInfo u)
        {
            if (!bucket.TryGetValue(u.Id, out var s)) return;
            if (s.Name == null) s.Name = u.Name;
            if (s.ClassType == 0) s.ClassType = u.ClassType;
        }

        // ------------------------------------------------------------------ 分段

        /// <summary>
        /// 空闲分段（关卡分段关掉时）：一段有数据后，隔 <see cref="Options.IdleSeconds"/> 秒没有新数据就另起一段。
        /// 按关卡分段时不用它，否则关卡内的间歇会被误切。
        /// </summary>
        private void TouchIdle(double t)
        {
            if (Opt.SegmentByStage) return;
            var idle = Opt.IdleSeconds;
            if (idle <= 0f) return;

            var c = Session.Current;
            if (c.IsEmpty)
            {
                c.StartTime = t;
                return;
            }

            if (t - c.LastActivityTime >= idle) Roll(t);
        }

        /// <summary>
        /// 关卡名变了就是换关卡。
        ///
        /// 【踩过的坑】最早用波次状态 <c>EStageState</c> 进入 MONSTERSPAWN 当分段点，结果每波刷怪都切一次——
        /// 那个状态机描述的是关卡内的波次循环，不是关卡边界。现在只认两个关卡级信号：
        /// 关卡名变化，和 <c>b_StageStart</c> 由 false 变 true。
        /// </summary>
        private void OnStageName(double t, string name)
        {
            if (string.IsNullOrEmpty(name) || name == _lastStageName) return;

            // 本地化还没填充时读到的是模板（实测见过 "Stage {0}-{1}"），那不是真的换关卡
            if (name.IndexOf("{0}", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("{1}", StringComparison.Ordinal) >= 0)
                return;

            var prev = _lastStageName;
            _lastStageName = name;

            // 第一次读到名字时不切段——那只是刚拿到引用，不代表换关了
            if (prev == null || !Opt.SegmentByStage) return;
            Segment(t, name);
        }

        private void OnStageStart(double t, bool flag)
        {
            var first = !_hasStageStart;
            var prev = _hasStageStart && _lastStageStart;
            _lastStageStart = flag;
            _hasStageStart = true;

            if (first || prev || !flag || !Opt.SegmentByStage) return;
            Segment(t, null);
        }

        /// <summary>
        /// 切一段。<paramref name="changedName"/> 是关卡名变化触发时的新名字；由 b_StageStart 触发时为 null，
        /// 此时用最近读到的关卡名——自动重复挑战同一关时名字不会变，但名字是已知的，
        /// 不该退回「关卡 #n」这种计数，而是给同名的段编序号（关卡 3-2、关卡 3-2 #2 …）。
        /// </summary>
        private void Segment(double t, string changedName)
        {
            var name = changedName ?? _lastStageName;
            var sinceCut = t - _lastSegmentAt;

            // 标志先切了段、关卡名随后才刷新：这是同一次换关，给刚切的段改名
            if (changedName != null && _lastCutByFlag && sinceCut < RelabelWindowSeconds)
            {
                if (name != _segName) Relabel(name);
                return;
            }
            if (sinceCut < DebounceSeconds) return;

            _lastSegmentAt = t;
            _lastCutByFlag = changedName == null;

            if (string.IsNullOrEmpty(name))
            {
                _stageCount++;
                _segName = null;
                _segRun = 0;
                BeginStage(t, null, 0, _stageCount);
            }
            else
            {
                if (name == _segName) _segRun++;
                else { _segName = name; _segRun = 1; }
                BeginStage(t, _segName, _segRun, 0);
            }
        }

        /// <summary>刚切的那段被当成了上一关的重复，其实是新关：改名，序号从 1 起。</summary>
        private void Relabel(string name)
        {
            _segName = name;
            _segRun = 1;
            var c = Session.Current;
            c.StageName = name;
            c.Run = 1;
            c.StageNo = 0;
            Relabeled?.Invoke(c);
        }

        private void BeginStage(double t, string stageName, int run, int stageNo)
        {
            var c = Session.Current;
            if (c.IsEmpty)
            {
                // 上一段是空的就直接改标签，别平白多出一堆空战斗
                c.StartTime = t;
                c.LastActivityTime = t;
            }
            else
            {
                Roll(t);
                c = Session.Current;
            }

            c.StageName = stageName;
            c.Run = run;
            c.StageNo = stageNo;
            Started?.Invoke(c);
        }

        /// <summary>结束当前段、另起一段。旧段非空就收进列表，超出上限丢最旧的。</summary>
        private void Roll(double t)
        {
            var old = Session.Current;
            if (old != null && !old.IsEmpty)
            {
                Session.Encounters.Add(old);
                if (Opt.MaxEncounters > 0)
                {
                    while (Session.Encounters.Count > Opt.MaxEncounters)
                    {
                        Session.Encounters.RemoveAt(0);
                        Session.DroppedEncounters++;
                    }
                }
                Closed?.Invoke(old);
            }

            _encounterCounter++;
            Session.Current = new Encounter { Index = _encounterCounter, StartTime = t, LastActivityTime = t };
        }
    }
}
