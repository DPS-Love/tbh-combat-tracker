using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 职业表。索引就是游戏的 <c>TaskbarHero.Data.EEquipClassType</c>：
    /// <code>All=0, Knight=1, Ranger=2, Sorcerer=3, Priest=4, Hunter=5, Slayer=6</code>
    ///
    /// 英雄 GameObject 名形如 <c>Hero_301(Clone)</c>，首位数字正好等于 EEquipClassType——
    /// 但代码里**不依赖这个规律**，而是运行时从 <c>Hero.cache.bflj</c>（HeroInfoData）
    /// 直接读 <c>ClassType</c>。HeroInfoData 的字段没被混淆，是权威来源。
    ///
    /// 配色取各职业立绘的主色，而不是 Horizoverlay 原版的职能三色
    /// （坦克蓝 / 治疗绿 / 输出红）——这游戏只有六个固定职业，用形象色辨识度更高。
    /// 六个色相拉开：红 / 绿 / 紫 / 金 / 青 / 锈橙。
    /// 想自己调的话不用改代码，配置文件 [Colors] 段里就是这些十六进制值。
    /// </summary>
    internal static class JobTable
    {
        /// <summary>职业名取自游戏内的角色卡（不是枚举名直译）。</summary>
        private static readonly string[] Labels =
        {
            null,      // 0 All —— 不是真职业
            "骑士",    // 1 Knight   猩红披风与盾徽
            "游侠",    // 2 Ranger   森林绿劲装
            "法师",    // 3 Sorcerer 紫罗兰法杖
            "牧师",    // 4 Priest   圣白法袍配金饰
            "猎人",    // 5 Hunter   青碧斗篷
            "杀手",    // 6 Slayer   赭褐皮甲双斧
        };

        /// <summary>默认配色，对应各职业立绘的主色。</summary>
        public static readonly string[] DefaultColors =
        {
            "#9E9E9E", // 0 未知/怪物/环境伤害
            "#C0392B", // 1 骑士 猩红
            "#5FB04A", // 2 游侠 森林绿
            "#8E5BD0", // 3 法师 紫罗兰
            "#F0D98C", // 4 牧师 淡金
            "#2AA8A0", // 5 猎人 青碧
            "#E07B39", // 6 杀手 赭橙
        };

        public static int Count => Labels.Length;

        public static bool IsKnown(int classType)
            => classType >= 1 && classType < Labels.Length;

        public static string Label(int classType)
            => IsKnown(classType) ? Labels[classType] : null;

        /// <summary>给配置项用的英文键名，避免中文键名在 cfg 里出编码问题。</summary>
        public static string Key(int classType)
        {
            switch (classType)
            {
                case 1: return "Knight";
                case 2: return "Ranger";
                case 3: return "Sorcerer";
                case 4: return "Priest";
                case 5: return "Hunter";
                case 6: return "Slayer";
                default: return "Unknown";
            }
        }

        public static Color ColorOf(int classType)
        {
            var idx = classType >= 0 && classType < DefaultColors.Length ? classType : 0;

            string hex = null;
            try { hex = Mod.Config?.JobColors?[idx]?.Value; } catch { /* 配置还没就绪 */ }
            if (string.IsNullOrWhiteSpace(hex)) hex = DefaultColors[idx];

            return Parse(hex, DefaultColors[idx]);
        }

        public static Color ColorOf(int classType, float alpha)
        {
            var c = ColorOf(classType);
            c.a = alpha;
            return c;
        }

        // ------------------------------------------------------------------

        private static readonly Dictionary<string, Color> ParseCache = new Dictionary<string, Color>();

        /// <summary>
        /// 解析 #RGB / #RRGGBB / #RRGGBBAA。
        /// 自己写而不是用 ColorUtility.TryParseHtmlString：那是纯托管方法，
        /// 在这个 IL2CPP 构建里有被裁剪掉的风险（参见 README「被裁剪的 Unity API」），
        /// 而这点解析逻辑十几行就能写完，没必要冒险。
        /// </summary>
        private static Color Parse(string hex, string fallback)
        {
            if (hex == null) hex = fallback;

            lock (ParseCache)
            {
                if (ParseCache.TryGetValue(hex, out var cached)) return cached;
            }

            var c = ParseCore(hex) ?? ParseCore(fallback) ?? Color.gray;

            lock (ParseCache)
            {
                // 用户可能在配置里反复试色，缓存别无限涨
                if (ParseCache.Count > 64) ParseCache.Clear();
                ParseCache[hex] = c;
            }
            return c;
        }

        private static Color? ParseCore(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;

            var s = hex.Trim();
            if (s.Length > 0 && s[0] == '#') s = s.Substring(1);

            // #RGB 简写展开成 #RRGGBB
            if (s.Length == 3)
                s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });

            if (s.Length != 6 && s.Length != 8) return null;

            if (!TryByte(s, 0, out var r)) return null;
            if (!TryByte(s, 2, out var g)) return null;
            if (!TryByte(s, 4, out var b)) return null;

            var a = 255;
            if (s.Length == 8 && !TryByte(s, 6, out a)) return null;

            return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        }

        private static bool TryByte(string s, int at, out int value)
        {
            value = 0;
            for (var i = 0; i < 2; i++)
            {
                var d = HexDigit(s[at + i]);
                if (d < 0) return false;
                value = value * 16 + d;
            }
            return true;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
