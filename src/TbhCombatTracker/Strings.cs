using System;


namespace TbhCombatTracker
{
    /// <summary>
    /// 所有由我们自己提供的文案，按游戏当前语言选中 / 英。
    ///
    /// 两类东西都在这里：
    ///   1. 游戏本身没有译文的数据标签（伤害类型、恢复来源、"普通攻击"…）
    ///   2. 面板、明细窗口、更新横幅上的界面文字
    /// 界面模块只引用这里的成员，不再各自写字面量。日志消息不在此列——那是给开发者看的。
    ///
    /// 不硬编码中文：这个 Mod 是公开发布的，英文玩家看到一半中文一半英文会很怪。
    /// 游戏有译文的东西（英雄名、技能名、元素属性）走 <see cref="Localize"/>，不在这里。
    /// </summary>
    internal static class Strings
    {
        // ================================================================ 语言

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
                    var code = ReadLocaleCode();

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

        /// <summary>
        /// 只读地取当前语言：**绝不能**用 <c>LocalizationSettings.SelectedLocale</c>。
        /// 那个 getter 在本地化还没初始化时会**同步跑完整个 Addressables 初始化**——
        /// 我们的第一帧 OnGUI 早于游戏自己的启动流程，在那时替游戏把初始化跑掉，
        /// 游戏的场景和 UI 就起不来了（实测：主线程活着、UI 布局狂刷警告、画面全黑）。
        ///
        /// 这里只读已经缓存好的异步句柄：还没完成就返回 null（调用方按英文走，不锁定结论），
        /// 完成了才拿 Result。全程不触发任何初始化。引用 Unity 类型的代码只在这一个方法里，
        /// 类型加载失败时异常落在调用方的 try 里。
        /// </summary>
        private static string ReadLocaleCode()
        {
            // 不创建默认实例，没有就是没有
            var settings = UnityEngine.Localization.Settings.LocalizationSettings.GetInstanceDontCreateDefault();
            if (settings == null) return null;

            var handle = settings.m_SelectedLocaleAsync;
            if (handle == null || !handle.IsValid() || !handle.IsDone) return null;
            if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) return null;

            var locale = handle.Result;
            // LocaleIdentifier 是结构体，不能对它用 ?.，所以先判 locale 本身
            return locale != null ? locale.Identifier.Code : null;
        }

        public static string Pick(string zh, string en) => Chinese ? zh : en;

        // ================================================================ 游戏没有译文的数据标签

        /// <summary>伤害类型。游戏里没有独立词条（Melee / Projectile / AOE / Summon 从属性面板整句里反推，
        /// 见 DamageTracker），DOT / Trap / None 任何语言都没有出处，只能靠这里。</summary>
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

        /// <summary>生命恢复来源。这几档是我们自己划分的，游戏里没有对应概念。</summary>
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

        // ================================================================ 主面板

        public static string ViewLabel(TrackerView v)
        {
            switch (v)
            {
                case TrackerView.Incoming: return Pick("承伤", "Taken");
                case TrackerView.Healing: return Pick("治疗", "Healing");
                default: return Pick("输出", "Damage");
            }
        }

        public static string EmptyHint(TrackerView v)
        {
            switch (v)
            {
                case TrackerView.Incoming: return Pick("尚未承受伤害…", "No damage taken yet…");
                case TrackerView.Healing: return Pick("尚未产生治疗…", "No healing yet…");
                default: return Pick("等待伤害数据…", "Waiting for damage…");
            }
        }

        public static string Reset => Pick("重置", "Reset");
        public static string HitsCount(long n) => Pick($"{n} 次", $"{n} hits");
        public static string CritRate(double rate) => Pick($"暴 {rate * 100d:0}%", $"Crit {rate * 100d:0}%");
        public static string CritNone => Pick("暴 —", "Crit —");
        public static string MaxHit(string shortValue) => Pick($"最大 {shortValue}", $"Max {shortValue}");
        public static string StageLabel(int n) => Pick($"关卡 #{n}", $"Stage #{n}");

        // ================================================================ 明细窗口

        public static string DetailTitle(string name, string view) => $"{name} — {view}";
        public static string TabSkills => Pick("技能", "Skills");
        public static string TabTypes => Pick("类型", "Types");
        public static string TabElements => Pick("元素", "Elements");
        public static string TabHealSources => Pick("恢复来源", "Sources");
        public static string NoBreakdown => Pick("暂无细分数据", "No breakdown yet");
        public static string MoreItems(int n) => Pick($"…另有 {n} 项", $"…{n} more");

        // ================================================================ 更新横幅

        public static string BtnDisable => Pick("停用", "Disable");
        public static string BtnUpdate => Pick("更新", "Update");
        public static string BtnUpdating => Pick("更新中…", "Updating…");
        public static string BtnRelease => Pick("下载页", "Release");

        public static string Updated(Version v) =>
            Pick($"已更新到 v{v}，重启游戏生效", $"Updated to v{v} — restart the game");
        public static string UpdatedHint =>
            Pick("如新版本加载失败，把 plugins 里的 .old 改回 .dll 即可回滚",
                 "If it fails to load, rename the .old file in plugins back to .dll");

        public static string DisabledTitle =>
            Pick("已停用本次会话的统计，重启游戏恢复",
                 "Stats disabled for this session — restart the game to re-enable");
        public static string DisabledHintUpdate =>
            Pick("可以先点「更新」，重启后就是新版本", "Click Update now; the new version loads on restart");
        public static string DisabledHintRelease =>
            Pick("请到下载页获取新版本", "Get the new version from the release page");

        public static string BrokenTitle(Version mine, Version game) =>
            Pick($"v{mine} 在游戏 {game} 上会出问题", $"v{mine} is known to misbehave on game {game}");
        public static string BrokenHint =>
            Pick("可以停用本次统计，或更新到新版本", "You can disable stats for now, or update");

        public static string UpdateFailed =>
            Pick("自动更新失败，请手动下载", "Auto-update failed — download manually");

        public static string UpdateAvailable(Version latest, bool critical) =>
            Pick($"有新版本 v{latest}" + (critical ? "（重要）" : ""),
                 $"Update available: v{latest}" + (critical ? " (important)" : ""));

        public static string MismatchTitle =>
            Pick("本版 Mod 与当前游戏不匹配，统计不可用",
                 "This build does not match the current game; stats unavailable");
        public static string MismatchHint(Version game, Version builtFor) =>
            Pick($"游戏 {game?.ToString() ?? "?"}，本版为 {builtFor?.ToString() ?? "?"} 构建。游戏本身不受影响，留意新版本",
                 $"Game {game?.ToString() ?? "?"}, this build targets {builtFor?.ToString() ?? "?"}. The game is unaffected; watch for an update");

        public static string GameNewerTitle(Version game, Version builtFor) =>
            Pick($"游戏已更新到 {game}，本版 Mod 是为 {builtFor} 构建的",
                 $"Game updated to {game}; this build targets {builtFor}");
        public static string GameNewerHint =>
            Pick("统计可能缺失或不准，不影响游戏本身。留意新版本",
                 "Stats may be missing or off; the game itself is unaffected. Watch for an update");
    }
}
