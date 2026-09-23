namespace TbhCombatTracker
{
    /// <summary>
    /// 事件类型。日志里每种事件一个字母，见 <see cref="EventLogFormat"/> 和 docs/eventlog.md。
    /// **只加不改**：已发布的字母和字段含义永远不变，新信息加新类型或在行尾追加字段。
    /// </summary>
    public enum EventKind : byte
    {
        /// <summary>U：单位定义（第一次出现、或名字/职业变了时写一次）。</summary>
        Unit,
        /// <summary>A：技能定义，给技能分配一个会话内编号，后面的伤害行只写编号。</summary>
        Ability,
        /// <summary>D：对怪物造成的伤害。</summary>
        Damage,
        /// <summary>T：英雄承受的伤害。</summary>
        Taken,
        /// <summary>H：英雄获得的生命恢复。</summary>
        Heal,
        /// <summary>S name：关卡名文本变了。</summary>
        StageName,
        /// <summary>S start：StageManager.b_StageStart 变了（第一次读到也写）。</summary>
        StageStart,
        /// <summary>S wave：关卡内的波次状态变了（MONSTERSPAWN / BATTLE / …）。</summary>
        Wave,
        /// <summary>R：玩家手动重置（F10 / 面板上的「重置」）。</summary>
        Reset,
    }

    /// <summary>
    /// 一条战斗事件。只装记录那一刻能拿到的**原始事实**，不装结论——
    /// 分段、归因、分类都由 <see cref="CombatParser"/> 从事件算出来，
    /// 所以解析器改进之后，旧日志重新导入也能得到新的统计。
    ///
    /// 各字段按 <see cref="Kind"/> 解释：
    /// <code>
    /// Damage  A = 攻击者 id（0 = 未知来源）  B = 目标怪物血条 id
    /// Taken   A = 挨打的英雄 id              B = 攻击者 id（0 = 未知）
    /// Heal    A = 被恢复的英雄 id            B = 归因的施法者 id（0 = 自愈，算 A 自己的）
    /// Unit    A = 单位 id                    Key / Text / UnitKind / ClassType
    /// Ability A = 技能编号                   Key / Text
    /// </code>
    /// </summary>
    public struct CombatEvent
    {
        /// <summary>会话内秒数（从插件加载起算）。</summary>
        public double T;
        public EventKind Kind;

        public int A;
        public int B;

        public float Amount;
        /// <summary>
        /// 结算前 DamageInfo.OriginDamage 的原值；NaN = 这次伤害没有分类上下文。
        /// 实测最终伤害常是它的若干倍，不是"减免前的伤害"，原样记下留给以后分析。
        /// </summary>
        public float Origin;
        /// <summary>1 暴击 / 0 非暴击 / -1 不知道（没有分类上下文）。</summary>
        public sbyte Crit;
        /// <summary>EDamageType 位标志；-1 = 不知道。</summary>
        public int DamageType;
        /// <summary>EDamageAttribute；-1 = 不知道。</summary>
        public int Attribute;
        /// <summary>技能编号（见 Ability 事件）；0 = 不知道是哪个技能。</summary>
        public int Ability;

        /// <summary>Heal：记录时判定的恢复来源（HealKind 数值）。</summary>
        public int HealKind;
        /// <summary>Heal：恢复总入口的两个 bool 参数；-1 = 这次恢复没经过总入口。</summary>
        public sbyte FlagB, FlagC;
        /// <summary>Heal：所在的上游括号（HealKind 数值，0 = 不在任何括号里）。</summary>
        public int Bracket;

        /// <summary>Unit：'H' 英雄 / 'M' 怪物 / 'O' 其它。</summary>
        public char UnitKind;
        /// <summary>Unit：EEquipClassType。</summary>
        public int ClassType;
        /// <summary>StageStart 的值。</summary>
        public bool Flag;

        /// <summary>Unit / Ability：稳定键。</summary>
        public string Key;
        /// <summary>Unit / Ability：显示名；StageName / Wave：文本。</summary>
        public string Text;
    }
}
