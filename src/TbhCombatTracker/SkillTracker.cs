using System;
using System.Collections.Generic;
using UnityEngine;

using GActiveSkill = TaskbarHero.Combat.ActiveSkill;

namespace TbhCombatTracker
{
    /// <summary>
    /// 记住"这一次伤害是哪个技能打出来的"。
    ///
    /// 关键发现：游戏里 **53 个技能类全部保留了原名**（`KnightBaseAtk`、`WizardMeteorStrike`、
    /// `PriestHeal`、`PriestSanctuary`…），混淆器没动它们。所以拿到技能实例就等于拿到技能名。
    ///
    /// 连接点是 <c>ActiveSkill.AttackDamage()</c>——同样没被混淆的方法，每个技能生成自己
    /// <c>DamageInfo</c> 的工厂。交叉引用显示它只有 1 个直接调用点（<c>HunterExplosiveBolt</c>
    /// 调 base），其余全是虚分发/委托调用——也就是说它是**命中时才求值的惰性工厂**，
    /// 弹道和 AOE 的延迟伤害也会走到它。所以在这里记下技能，伤害落地时就能归因。
    ///
    /// 只有 7 个类覆写了 <c>AttackDamage</c>，其中只有 <c>HunterExplosiveBolt</c> 是技能，
    /// 所以挂基类 + 它这一个覆写就够覆盖全部技能。
    /// </summary>
    internal static class SkillTracker
    {
        /// <summary>备忘的有效期。正常情况下工厂求值和伤害落地就隔几行代码，给足冗余即可。</summary>
        private const float MemoSeconds = 0.5f;

        private static string _skill;
        private static int _ownerId;
        private static float _at;

        /// <summary>技能生成 DamageInfo 时记一笔。</summary>
        public static void Remember(GActiveSkill skill)
        {
            if (skill == null) return;

            try
            {
                _skill = NameOf(skill);
                var owner = skill.bilo;
                _ownerId = owner != null ? owner.GetInstanceID() : 0;
                _at = Time.realtimeSinceStartup;
            }
            catch
            {
                _skill = null;
            }
        }

        /// <summary>
        /// 取这次伤害对应的技能名。
        /// 会校验攻击者是否对得上——技能工厂和伤害落地之间理论上不会插进别人，
        /// 但真插进来了宁可标成未知，也不要张冠李戴。
        /// </summary>
        public static string For(int attackerId)
        {
            if (_skill == null) return null;
            if (_ownerId != 0 && attackerId != 0 && _ownerId != attackerId) return null;
            if (Time.realtimeSinceStartup - _at > MemoSeconds) return null;
            return _skill;
        }

        /// <summary>类名 -> 显示名的缓存。译文查询和字符串处理都不便宜，一个技能只做一次。</summary>
        private static readonly Dictionary<string, string> NameCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 技能显示名。优先走游戏的本地化——<c>ActiveSkill.skillCache</c>(wl)
        /// 里挂着 <c>SkillInfoData</c>，它的 <c>SkillNameKey</c> 就是权威的本地化键
        /// （和英雄的 <c>HeroNameKey</c> 同一套路，字段名都没被混淆）。
        /// 查不到才退回类名整理出来的英文名。
        /// </summary>
        private static string NameOf(GActiveSkill skill)
        {
            var typeName = skill.GetIl2CppType().Name;

            lock (NameCache)
            {
                if (NameCache.TryGetValue(typeName, out var hit)) return hit;
            }

            string name = null;
            try
            {
                var key = skill.skillCache?.bgjn?.SkillNameKey;
                name = Localize.TryGet(key);
            }
            catch { /* 没有技能数据的（怪物普攻之类）走兜底 */ }

            if (string.IsNullOrWhiteSpace(name)) name = PrettyName(typeName);

            lock (NameCache) NameCache[typeName] = name;
            return name;
        }

        /// <summary>
        /// 显示时剥掉的类名前缀。
        /// **注意不含 "Monster"**：普通怪物的攻击统一走 <c>MonsterActive</c> 这一个类，
        /// 剥掉前缀会剩下毫无意义的 "Active"——承伤面板上一整列都显示 Active 就是这么来的。
        /// </summary>
        private static readonly string[] JobPrefixes =
        {
            "Knight", "Archer", "Wizard", "Priest", "Hunter", "Slayer", "ActBoss",
        };

        /// <summary>类名直接对应固定叫法的，不走前缀剥离。</summary>
        private static string ExplicitName(string typeName)
        {
            switch (typeName)
            {
                case "MonsterActive": return BuiltinText.MonsterAttack;
                case "ActiveSkill": return BuiltinText.UnknownSkill;
                default: return null;
            }
        }

        public static string PrettyName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            var fixedName = ExplicitName(typeName);
            if (fixedName != null) return fixedName;

            var name = typeName;
            foreach (var p in JobPrefixes)
            {
                if (name.Length > p.Length && name.StartsWith(p, StringComparison.Ordinal))
                {
                    var rest = name.Substring(p.Length);
                    // 剥完只剩个把字母就没意义了，宁可保留全名
                    if (rest.Length >= 3) name = rest;
                    break;
                }
            }

            // KnightBaseAtk / ArcherBaseAtk / … 都是普通攻击，统一叫法
            if (name == "BaseAtk") return BuiltinText.NormalAttack;

            return name;
        }
    }
}
