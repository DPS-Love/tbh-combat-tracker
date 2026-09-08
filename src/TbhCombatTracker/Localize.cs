using System;
using System.Collections.Generic;

using GLoc = global::nz;

namespace TbhCombatTracker
{
    /// <summary>
    /// 取游戏当前语言的译文。
    ///
    /// 游戏用的是 Unity Localization 包（interop 里有 <c>Unity.Localization.dll</c>），
    /// 封装在全局类 <c>nz</c> 上（1.01.05 时叫 <c>nt</c>）——一组 string 扩展方法：
    /// <code>
    /// nz.gix(key)          nz.giz(key)           两个单参版本，对应两张不同的表
    /// nz.giy(key, table)   nz.gja(key, table)    双参版本
    /// nz.gjb(key, args)                          带格式化参数
    /// </code>
    /// 这五个在 dump 里的先后顺序和 1.01.05 的 gft/gfu/gfv/gfw/gfx 一一对应，
    /// 所以 giz 应该就是原来的 gfv。但这只是顺序推断，下面两个都查，不押单边。
    ///
    /// 哪个单参版本对应哪张表，静态看不出来——所以运行时两个都试，谁先返回有效译文就用谁，
    /// 结果按键缓存。查不到就退回调用方给的兜底文本，**绝不显示原始键**。
    /// </summary>
    internal static class Localize
    {
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        /// <summary>Unity Localization 查不到时的典型返回，别把这种字符串当译文用。</summary>
        private static bool LooksUnresolved(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            if (value == key) return true;
            if (value.IndexOf("No translation", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (value.IndexOf("translation found", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>查译文；查不到返回 null。</summary>
        public static string TryGet(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var hit)) return hit;
            }

            var value = Lookup(key);

            lock (Cache)
            {
                // 查不到也缓存（存 null），免得每帧重复问一遍
                Cache[key] = value;
            }
            return value;
        }

        /// <summary>
        /// 先 giz 后 gix。
        ///
        /// 1.01.05 实测：中文界面下 <c>gft("HeroName_401")</c> 返回 "Priest"——英文源文本，
        /// 不是玩家当前语言，<c>gfv</c> 才是走本地化表的那个。1.2.0 里按 dump 顺序对应到
        /// giz / gix。顺序万一推断反了也不会错：两个都查，谁先返回有效译文就用谁，
        /// 打开 LocalizationDebug 能看到每个键在两边分别返回了什么。
        /// </summary>
        private static string Lookup(string key)
        {
            string first = null, second = null;

            try { first = GLoc.giz(key); } catch { /* 表不存在会抛 */ }
            try { second = GLoc.gix(key); } catch { }

            if (Mod.Config.LocalizationDebug.Value)
                Mod.Log.Msg($"[i18n] '{key}'  giz='{first ?? "null"}'  gix='{second ?? "null"}'");

            if (!LooksUnresolved(key, first)) return first;
            if (!LooksUnresolved(key, second)) return second;
            return null;
        }

        /// <summary>查译文，查不到用兜底文本。</summary>
        public static string Get(string key, string fallback)
            => TryGet(key) ?? fallback;

        /// <summary>剥完前后缀之后还要去掉的边角料。</summary>
        private static readonly char[] Edge = { ' ', ' ', ':', '：', '-', '·' };

        /// <summary>
        /// 从一组"结构相同、只有一个词不同"的译文里，把那个词抠出来。
        ///
        /// 用途：伤害类型（近战 / 投射物 / 范围 / 召唤）在游戏里没有单独的词条，
        /// 但属性面板上有 <c>StatName_IncreaseMeleeDamage</c> = "增加近战伤害" 这样的整句。
        /// 把这几句共有的前缀和后缀都剥掉，剩下的正好就是类型词本身。
        ///
        /// 关键是这个做法**与语言无关**——不需要知道"增加"在哪门语言里怎么写、摆在前面还是后面。
        /// 已把游戏发行的全部 16 种语言的字符串表离线解包验证过（见 docs/symbols.md 第 10 节），
        /// 每一种都能抠出四个互不相同的词：
        /// <code>
        /// zh-Hans  近战 / 投射物 / 范围 / 召唤物        en-US  Melee / Projectile / Area Of Effect / Summon
        /// ko-KR    근접 공격 / 투사체 공격 / 범위 공격 / 소환물
        /// ru-RU    ближнего боя / снарядов / по области / призванных
        /// </code>
        ///
        /// 任何一条查不到、抠完是空的、或者抠出重复词，就整体返回 null 让调用方退回内置文本。
        /// 宁可四个都用内置文本，也不要一半游戏译文一半内置文本的拼盘。
        /// </summary>
        public static string[] LiftDistinctParts(params string[] keys)
        {
            if (keys == null || keys.Length < 2) return null;

            var vals = new string[keys.Length];
            var min = int.MaxValue;
            for (var i = 0; i < keys.Length; i++)
            {
                vals[i] = TryGet(keys[i]);
                if (string.IsNullOrWhiteSpace(vals[i])) return null;
                if (vals[i].Length < min) min = vals[i].Length;
            }

            var head = 0;
            while (head < min && SameAt(vals, v => v[head])) head++;

            var tail = 0;
            while (head + tail < min && SameAt(vals, v => v[v.Length - 1 - tail])) tail++;

            var parts = new string[vals.Length];
            for (var i = 0; i < vals.Length; i++)
            {
                parts[i] = vals[i].Substring(head, vals[i].Length - tail - head).Trim(Edge);
                if (parts[i].Length == 0) return null;
            }

            // 抠出重复词说明这几句的结构不是我们以为的那样，别拿去显示
            for (var i = 0; i < parts.Length; i++)
                for (var j = i + 1; j < parts.Length; j++)
                    if (string.Equals(parts[i], parts[j], StringComparison.Ordinal))
                        return null;

            return parts;
        }

        private static bool SameAt(string[] vals, Func<string, char> pick)
        {
            var c = pick(vals[0]);
            for (var i = 1; i < vals.Length; i++)
                if (pick(vals[i]) != c) return false;
            return true;
        }

        /// <summary>
        /// 探针：把一批候选键的查询结果打到日志。
        /// 伤害类型/元素属性这些枚举的本地化键叫什么，静态分析看不出来
        /// （键在 Addressables 里的 StringTable 资产中，不在 metadata）。
        /// 与其瞎猜，不如打一轮日志看哪种命名成立。
        /// </summary>
        /// <summary>已经探过的标签，探针只打一次——否则明细窗口每帧都会重打，
        /// 上一版实测刷了 5 万多行日志。</summary>
        private static readonly HashSet<string> Probed = new HashSet<string>(StringComparer.Ordinal);

        public static void Probe(string label, params string[] candidates)
        {
            if (!Mod.Config.LocalizationDebug.Value) return;

            lock (Probed)
            {
                if (!Probed.Add(label)) return;
            }

            foreach (var k in candidates)
            {
                var v = TryGet(k);
                Mod.Log.Msg($"[i18n] {label}  键 '{k}' -> {(v == null ? "（查不到）" : "'" + v + "'")}");
            }
        }
    }
}
