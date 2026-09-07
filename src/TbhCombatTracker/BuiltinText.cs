using System;

using GLocSettings = UnityEngine.Localization.Settings.LocalizationSettings;

namespace TbhCombatTracker
{
    /// <summary>
    /// 我们自己提供的文本。
    ///
    /// 只用于**游戏本身没有译文**的东西。实测（见 docs/symbols.md 第 10 节）：
    /// 元素属性用裸枚举名就能查到译文（<c>'Fire'</c> → 火焰），
    /// 但伤害类型（Melee / Projectile / AOE / Summon / DOT / Trap）三种命名全部落空——
    /// 游戏界面根本不显示这批枚举，也就没做翻译。
    ///
    /// 这种缺口只能自己补，但不能硬编码中文：这个 Mod 已经公开发布，
    /// 英文玩家看到一半中文一半英文会很怪。所以按游戏当前语言选。
    /// </summary>
    internal static class BuiltinText
    {
        private static bool _localeChecked;
        private static bool _chinese;

        /// <summary>当前语言是否中文。读游戏的 Locale，不是读系统语言。</summary>
        public static bool Chinese
        {
            get
            {
                if (_localeChecked) return _chinese;

                try
                {
                    // SelectedLocale 是**静态**属性；同名的 GetSelectedLocale() 是实例方法，
                    // 要先拿 Instance 才能调，绕一圈没必要。
                    var locale = GLocSettings.SelectedLocale;
                    // LocaleIdentifier 是结构体，不能对它用 ?.，所以先判 locale 本身。
                    var code = locale != null ? locale.Identifier.Code : null;

                    // 还没初始化完就先别记住结论——否则第一次调用赶在本地化启动之前，
                    // 整局都会被钉死在英文。查不到就这次按英文走，下次再问一遍。
                    if (code == null) return false;

                    _localeChecked = true;
                    _chinese = code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                    Mod.Log.Msg($"游戏语言 = '{code}'，内置文本使用{(_chinese ? "中文" : "英文")}。");
                }
                catch (Exception e)
                {
                    _localeChecked = true;
                    _chinese = false;
                    Mod.Log.Warning($"读取游戏语言失败，内置文本回退英文：{e.GetType().Name}");
                }

                return _chinese;
            }
        }

        public static string Pick(string zh, string en) => Chinese ? zh : en;

        /// <summary>伤害类型。游戏没有译文，全靠这张表。</summary>
        public static string DamageType(string raw)
        {
            switch (raw)
            {
                case "None": return Pick("无", "None");
                case "Melee": return Pick("近战", "Melee");
                case "Projectile": return Pick("弹道", "Projectile");
                case "AOE": return Pick("范围", "AOE");
                case "Summon": return Pick("召唤", "Summon");
                case "DOT": return Pick("持续伤害", "DoT");
                case "Trap": return Pick("陷阱", "Trap");
                default: return raw;
            }
        }

        /// <summary>生命恢复来源。这几档是我们自己划分的，游戏里没有对应概念，只能自己命名。</summary>
        public static string HealKind(int kind)
        {
            switch (kind)
            {
                case 0: return Pick("自然回复", "Regen");
                case 1: return Pick("战斗回复", "In Combat");
                case 2: return Pick("处决回复", "On Kill");
                case 3: return Pick("治愈", "Heal");
                case 4: return Pick("圣域", "Sanctuary");
                case 5: return Pick("技能治疗", "Skill Heal");
                default: return Pick("未分类", "Unknown");
            }
        }

        public static string NormalAttack => Pick("普通攻击", "Basic Attack");
        public static string MonsterAttack => Pick("怪物攻击", "Monster Attack");
        public static string UnknownSource => Pick("未知来源", "Unknown");
        public static string UnknownSkill => Pick("未知技能", "Unknown Skill");
    }
}
