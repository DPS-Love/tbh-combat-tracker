using System;

namespace TbhCombatTracker
{
    /// <summary>
    /// 生命恢复的来源分类。
    ///
    /// 游戏数据里的恢复来源（<c>TaskbarHero.Data.StatType</c>，枚举名未混淆）：
    /// <code>
    /// HpLeech       = 21   生命偷取
    /// HpRegenPerSec = 23   每秒自然回复
    /// AddHpPerHit   = 33   每次攻击回复
    /// AddHpPerKill  = 58   处决回复
    /// </code>
    /// 外加牧师的两个技能：治愈（<c>PriestHeal</c>）和圣域（带 HealingAsync 的治疗场）。
    ///
    /// 全部六条路径最终都汇到 <c>pj.gxq(float, bool, bool)</c>——交叉引用确认它有 12 个
    /// 调用点，其中 <c>Unit.grd</c>（伤害结算）2 处、<c>Unit.grt</c>（击杀）1 处，
    /// 其余是各类包装。所以不必逐条 hook，只要在 <c>gxq</c> 这个漏斗上判断
    /// **当前处在哪个上游括号里**就能分类。
    /// </summary>
    internal enum HealKind
    {
        /// <summary>每秒自然回复。gxq 标志 (false, false)。</summary>
        Regen,
        /// <summary>伤害结算内触发：生命偷取 / 每次攻击回复。gxq 标志 (true, false)。</summary>
        InCombat,
        /// <summary>击杀时触发：处决回复。只有 Unit.grt 括号能确认。</summary>
        OnKill,
        /// <summary>牧师技能「治愈」——由 PriestHeal.mti 括号确认。</summary>
        PriestHeal,
        /// <summary>牧师技能「圣域」——由 PriestSanctuary.mti 括号确认。</summary>
        Sanctuary,
        /// <summary>技能治疗但分不清是「治愈」还是「圣域」。gxq 标志 (true, true)。</summary>
        Skill,
    }

    /// <summary>
    /// 治疗归因的上下文。用 Harmony 的 <c>__state</c> 保存/恢复，天然支持嵌套
    /// （比如治愈过程中又触发了别的恢复）。
    /// </summary>
    internal static class Healing
    {
        public const int KindCount = 6;

        private static readonly string[] KindNames =
        {
            "自然回复",   // Regen
            "战斗回复",   // InCombat —— 生命偷取和每次攻击回复混在一起，如实标注
            "处决回复",   // OnKill
            "治愈",       // PriestHeal
            "圣域",       // Sanctuary
            "技能治疗",   // Skill —— 延迟落地、括号没抓到时的兜底
        };

        /// <summary>
        /// 按 <c>gxq(float, bool b, bool c)</c> 的两个标志判定来源。
        ///
        /// 这两个 bool 的含义是实测相关性推出来的，不是从代码读出来的。229 次采样：
        /// <code>
        /// (false, false)  192 次  全部落在"无括号"    → 每秒自然回复
        /// (true,  false)   25 次  全部落在 Unit.grd   → 生命偷取 / 每次攻击回复
        /// (true,  true)    10 次  散布在全部英雄身上  → 技能治疗（治愈群体回复 3 次 x 3 人）
        /// </code>
        /// 前两组与上游括号 100% 吻合，互为印证。第三组括号只抓到 2/10——治疗是延迟落地的，
        /// Prefix/Finalizer 跨不过去，所以标志位才是覆盖率 100% 的那个依据。
        /// </summary>
        public static HealKind ClassifyByFlags(bool b, bool c)
        {
            if (b && c) return HealKind.Skill;
            if (b) return HealKind.InCombat;
            return HealKind.Regen;
        }

        /// <summary>
        /// 恢复来源名。这几档是我们自己划分的概念，游戏里没有对应文本，
        /// 所以走内置双语表而不是硬编码中文——Mod 是公开发布的。
        /// </summary>
        public static string KindName(int kind) => BuiltinText.HealKind(kind);

        // 当前括号状态。IL2CPP 的游戏逻辑都在主线程，但标成 ThreadStatic 更保险。
        [ThreadStatic] private static HealKind _kind;
        [ThreadStatic] private static SourceIdentity _caster;
        [ThreadStatic] private static bool _hasCaster;

        /// <summary>是否正处在 gxq 内——用来发现绕过漏斗直接调 gsi 的回血路径。</summary>
        [ThreadStatic] private static bool _inFunnel;
        [ThreadStatic] private static bool _flagB;
        [ThreadStatic] private static bool _flagC;

        /// <summary>
        /// 最近一次施放治疗技能的英雄。技能治疗是**延迟落地**的，Prefix/Finalizer 的
        /// 作用域跨不过去（实测只覆盖 2/10），所以记住施法者，等治疗真正落地时再归因。
        /// 这游戏只有牧师有治疗技能（治愈 / 圣域），所以这个归因是安全的。
        /// </summary>
        private static SourceIdentity _lastCaster;
        private static bool _hasLastCaster;

        public static void RememberCaster(SourceIdentity caster)
        {
            _lastCaster = caster;
            _hasLastCaster = true;
        }

        public static bool TryGetLastCaster(out SourceIdentity caster)
        {
            caster = _lastCaster;
            return _hasLastCaster;
        }

        public static HealKind CurrentKind => _kind;
        public static bool InFunnel => _inFunnel;

        /// <summary>进入一个上游括号，返回之前的状态供 Finalizer 恢复。</summary>
        public static (HealKind, SourceIdentity, bool) Enter(HealKind kind)
        {
            var prev = (_kind, _caster, _hasCaster);
            _kind = kind;
            _hasCaster = false;
            _caster = default;
            return prev;
        }

        public static (HealKind, SourceIdentity, bool) Enter(HealKind kind, SourceIdentity caster)
        {
            var prev = (_kind, _caster, _hasCaster);
            _kind = kind;
            _caster = caster;
            _hasCaster = true;
            return prev;
        }

        public static void Restore((HealKind, SourceIdentity, bool) prev)
        {
            _kind = prev.Item1;
            _caster = prev.Item2;
            _hasCaster = prev.Item3;
        }

        public static (bool, bool, bool) EnterFunnel(bool b, bool c)
        {
            var prev = (_inFunnel, _flagB, _flagC);
            _inFunnel = true;
            _flagB = b;
            _flagC = c;
            return prev;
        }

        public static void RestoreFunnel((bool, bool, bool) prev)
        {
            _inFunnel = prev.Item1;
            _flagB = prev.Item2;
            _flagC = prev.Item3;
        }

        /// <summary>
        /// 这次恢复的最终来源判定。
        /// 括号能确认的（处决回复、治愈）优先——那是确凿的；
        /// 其余靠 gxq 的标志位，因为它覆盖率 100% 而括号只有 20%。
        /// </summary>
        public static HealKind Resolve()
        {
            if (_kind == HealKind.OnKill || _kind == HealKind.PriestHeal ||
                _kind == HealKind.Sanctuary)
                return _kind;
            return _inFunnel ? ClassifyByFlags(_flagB, _flagC) : _kind;
        }

        public static bool FlagB => _flagB;
        public static bool FlagC => _flagC;

        /// <summary>
        /// 这次恢复该记到谁头上。
        /// 技能治疗归施法者（和伤害归攻击者一致）；自愈类（偷取/攻击回复/处决/自然回复）
        /// 归被恢复的单位自己。
        /// </summary>
        public static bool TryGetCaster(out SourceIdentity caster)
        {
            caster = _caster;
            return _hasCaster;
        }
    }
}
