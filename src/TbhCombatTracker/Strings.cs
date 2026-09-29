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
        public static string UnknownTarget => Pick("未知目标", "Unknown target");
        public static string UnknownSkill => Pick("未知技能", "Unknown Skill");

        /// <summary>来源的显示名。解析器只存原始事实，id 0（没有具体单位）的名字在这里给。</summary>
        public static string SourceName(SourceStats s)
            => s == null ? "?" : s.Name ?? (s.InstanceId == 0 ? UnknownSource : "?");

        /// <summary>技能的显示名；日志里技能名为空时（极少见）显示"未知技能"。</summary>
        public static string SkillName(string name) => string.IsNullOrEmpty(name) ? UnknownSkill : name;

        /// <summary>
        /// 一段的标题：关卡名（自动重复挑战时带序号，如「关卡 3-2 #2」）；
        /// 读不到关卡名时是「关卡 #n」；按空闲时间分段时是战斗序号 #n。
        /// </summary>
        public static string EncounterTitle(Encounter e)
            => e == null ? "—" : EncounterTitle(e.StageName, e.Run, e.StageNo, e.Index);

        public static string EncounterTitle(string stageName, int run, int stageNo, int index)
        {
            if (!string.IsNullOrEmpty(stageName)) return run > 1 ? StageRepeat(stageName, run) : stageName;
            if (stageNo > 0) return StageLabel(stageNo);
            return "#" + index;
        }

        // ================================================================ 浮窗

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

        public static string TipLog(string key) => Pick("战斗记录", "Combat log") + KeyTag(key);
        public static string TipView => Pick("切换：输出 / 承伤 / 治疗", "Switch: damage / taken / healing");
        public static string TipReset(string key) => Pick("重置当前统计", "Reset the current encounter") + KeyTag(key);
        public static string TipSettings => Pick("设置", "Settings");
        public static string TipClose => Pick("关闭", "Close");

        private static string KeyTag(string key) => string.IsNullOrWhiteSpace(key) ? "" : $"（{key}）";
        public static string HitsCount(long n) => Pick($"{n} 次", $"{n} hits");
        public static string CritRate(double rate) => Pick($"暴 {rate * 100d:0}%", $"Crit {rate * 100d:0}%");
        public static string CritNone => Pick("暴 —", "Crit —");
        public static string MaxHit(string shortValue) => Pick($"最大 {shortValue}", $"Max {shortValue}");
        public static string StageLabel(int n) => Pick($"关卡 #{n}", $"Stage #{n}");
        /// <summary>同一关连续第 n 次：关卡名来自游戏、已是玩家语言，只补一个序号，如 "关卡 3-2 #2"。</summary>
        public static string StageRepeat(string name, int n) => $"{name} #{n}";

        // ================================================================ 战斗记录

        public static string MainTitle => Pick("战斗记录", "Combat Log");
        public static string TipImportedChip(string file) => Pick($"正在看导入的日志 {file}；点 × 回到本局", $"Showing the imported log {file}; click × to go back");
        public static string TipBackToLive => Pick("回到本局", "Back to this session");
        public static string TipImport => Pick("导入一份战斗日志，按当前版本重新解析", "Import a combat log and re-parse it with this version");
        public static string TipLogFolder => Pick("打开战斗日志所在的文件夹", "Open the combat log folder");
        public static string TipExport => Pick("把选中的这一段导出成 CSV", "Export the selected encounter as CSV");
        public static string TipPageUp => Pick("上一页", "Page up");
        public static string TipPageDown => Pick("下一页", "Page down");
        public static string TipStored => Pick("已从内存卸载，点开时从本局日志重新载入", "Unloaded from memory; reloads from this session's log when opened");
        public static string Loading(string title) => Pick($"正在从日志载入 {title} …", $"Loading {title} from the log …");
        public static string LoadNotFound => Pick("日志里找不到这一段（日志可能不完整）", "This encounter is not in the log (the log may be incomplete)");
        public static string LoadNoLog => Pick("战斗日志没有开，已卸载的段载入不了", "Combat logging is off, so unloaded encounters cannot be reloaded");
        public static string LoadFailed(string why) => Pick($"载入失败：{why}", $"Loading failed: {why}");
        public static string BtnRetry => Pick("重试", "Retry");
        public static string UiFailed => Pick("TBH Combat Tracker：界面加载失败，统计照常记录、热键可用（详见 BepInEx/LogOutput.log）",
                                              "TBH Combat Tracker: the UI failed to load; stats and hotkeys still work (see BepInEx/LogOutput.log)");
        public static string BtnImport => Pick("导入", "Import");
        public static string BtnLogFolder => Pick("日志目录", "Log folder");
        public static string BtnExportCsv => Pick("导出 CSV", "Export CSV");
        public static string BtnCancel => Pick("取消", "Cancel");
        public static string NoEncounters => Pick("还没有战斗记录", "No encounters yet");
        public static string LiveTag => Pick("实时", "Live");

        public static string SummaryStats(string duration, string outgoing, string dps, string incoming, string healing)
            => Pick($"{duration}  ·  输出 {outgoing}（{dps}/s）  ·  承伤 {incoming}  ·  治疗 {healing}",
                    $"{duration}  ·  Damage {outgoing} ({dps}/s)  ·  Taken {incoming}  ·  Healing {healing}");

        public static string ColName => Pick("名字", "Name");
        public static string ColItem => Pick("项目", "Item");
        public static string ColTotal => Pick("总量", "Total");
        public static string ColShare => Pick("占比", "Share");
        public static string ColPerSec => Pick("每秒", "Per sec");
        public static string ColCrit => Pick("暴击", "Crit");
        public static string ColHits => Pick("次数", "Hits");
        public static string ColMax => Pick("最高", "Max");
        public static string ColTopSource => Pick("主要来源", "Top source");

        public static string Peak(string v) => Pick($"峰值 {v}/s", $"Peak {v}/s");
        public static string AllSources => Pick("全部", "Everyone");

        public static string TipModeChart => Pick("曲线与拆分", "Chart and breakdown");
        public static string TipModeEvents => Pick("逐条事件", "Event log");
        public static string EventsHeader(string who, string view, int n)
            => Pick($"{who} · {view} · {n:N0} 条", $"{who} · {view} · {n:N0} events");
        public static string EventsLoading => Pick("正在从日志读取这一段的事件…", "Reading this encounter's events from the log…");
        public static string EventsNoLog => Pick("战斗日志没有开，只能看实时这一段的事件", "Combat logging is off; only the live encounter has an event log");
        public static string EventsNotFound => Pick("日志里对不上这一段（日志可能不完整）", "This encounter does not match the log (the log may be incomplete)");
        public static string EventsEmpty => Pick("这一段没有这类事件", "No events of this kind in this encounter");
        public static string EventsCapped(int n) => Pick($"只保留了前 {n:N0} 条", $"Only the first {n:N0} are kept");
        public static string ColTime => Pick("时间", "Time");
        public static string ColSource => Pick("来源", "Source");
        public static string ColTarget => Pick("目标", "Target");
        public static string ColVictim => Pick("承受者", "Victim");
        public static string ColSkill => Pick("技能", "Skill");
        public static string ColAmount => Pick("数值", "Amount");
        public static string ColKind => Pick("类型", "Type");
        public static string ColCaster => Pick("施法者", "Caster");
        public static string ColRecipient => Pick("对象", "Recipient");
        public static string CritMark => Pick("暴", "Crit");

        public static string PickLog => Pick("选择要导入的日志：读入后按当前版本重新解析", "Choose a log: it is re-parsed with this version");
        public static string NoLogs => Pick("日志目录里还没有日志", "No logs in the log folder yet");
        public static string CurrentFileTag => Pick("（本局，正在写）", "  (this session, still writing)");
        public static string Parsing(string file) => Pick($"正在解析 {file} …", $"Parsing {file} …");
        public static string ImportDone(int n, long events)
            => Pick($"已导入 {n} 段（{events} 条事件）", $"Imported {n} encounters ({events} events)");
        public static string ImportTruncated => Pick("，末尾不完整（游戏没正常退出）", "; the log ends abruptly (the game did not exit cleanly)");
        public static string ImportCurrentFile => Pick("，本局日志读到最近一次落盘", "; this session's log, up to its last flush");
        public static string ImportFailed(string why) => Pick($"导入失败：{why}", $"Import failed: {why}");
        public static string Exported(string file) => Pick($"已导出 {file}", $"Exported {file}");
        public static string ExportFailed => Pick("导出失败，详见日志", "Export failed, see the log");

        // ================================================================ 明细窗口

        public static string DetailTitle(string name, string view) => $"{name}  ·  {view}";
        public static string TabSkills => Pick("技能", "Skills");
        public static string TabTypes => Pick("类型", "Types");
        public static string TabElements => Pick("元素", "Elements");
        public static string TabHealSources => Pick("恢复来源", "Sources");

        /// <summary>对手维度的页签：输出看打了谁，承伤看被谁打，治疗看治疗了谁。</summary>
        public static string TabTargets(TrackerView v)
        {
            switch (v)
            {
                case TrackerView.Incoming: return Pick("来源", "Attackers");
                case TrackerView.Healing: return Pick("对象", "Recipients");
                default: return Pick("目标", "Targets");
            }
        }
        public static string NoBreakdown => Pick("暂无细分数据", "No breakdown yet");
        public static string NoDataInSegment => Pick("这一段没有它的数据", "No data in this segment");
        public static string MoreItems(int n) => Pick($"…另有 {n} 项", $"…{n} more");
        /// <summary>环形图上并起来的那一段（一屏之外的、占比太小的项）。</summary>
        public static string Other => Pick("其他", "Other");

        // ================================================================ 设置

        public static string SettingsTitle => Pick("设置", "Settings");
        public static string TabDisplay => Pick("界面", "Display");
        public static string TabTracking => Pick("统计", "Tracking");
        public static string TabLogUpdate => Pick("日志与更新", "Logs & updates");
        public static string TabHotkeys => Pick("热键", "Hotkeys");
        public static string TabColors => Pick("颜色", "Colours");
        public static string RestartTag => Pick("重启后生效", "Needs restart");
        public static string SettingsFooter => Pick("改动自动保存到 BepInEx/config/dpslove.tbh.combattracker.cfg",
                                                    "Saved automatically to BepInEx/config/dpslove.tbh.combattracker.cfg");

        public static string SetUiScale => Pick("界面缩放", "UI scale");
        public static string SetUiScaleDesc => Pick("所有窗口一起缩放，松手后生效", "Scales every window; applies when you let go");
        public static string SetOverlayBg => Pick("窗口背景", "Window background");
        public static string SetOverlayBgDesc => Pick("浮窗、角色拆分和战斗记录窗口背景的浓淡。浮窗只在鼠标移上去时显示背景和按钮",
                                                      "Background opacity of the overlay, breakdown and combat log windows. The overlay shows its background and buttons only while the cursor is on it");
        public static string SetSkew => Pick("卡片斜切", "Card slant");
        public static string SetSkewDesc => Pick("浮窗色块的斜切角度，0 为直角", "Slant of the overlay's colour blocks; 0 is square");
        public static string SetByStage => Pick("按关卡分段", "Split by stage");
        public static string SetByStageDesc => Pick("关掉则按空闲时间分段", "When off, encounters split after idle time");
        public static string SetIdle => Pick("空闲切段", "Idle split");
        public static string SetIdleDesc => Pick("多少秒没有伤害就另起一段（不按关卡分段时）", "Seconds without damage before a new encounter (when not splitting by stage)");
        public static string SetKeep => Pick("内存中保留", "Keep in memory");
        public static string SetKeepDesc => Pick("最近多少段留着详细数据，更早的点开时从日志载入", "Recent encounters kept in full; older ones reload from the log");
        public static string SetIncoming => Pick("统计承伤", "Track damage taken");
        public static string SetIncomingDesc => Pick("英雄承受的伤害", "Damage your heroes take");
        public static string SetHealing => Pick("统计治疗", "Track healing");
        public static string SetHealingDesc => Pick("英雄获得的生命恢复，按来源拆分", "Healing your heroes receive, split by source");
        public static string SetSkills => Pick("按技能拆分", "Split by skill");
        public static string SetSkillsDesc => Pick("伤害按技能分项统计", "Break damage down by skill");
        public static string SetLogEvents => Pick("记录战斗日志", "Write combat logs");
        public static string SetLogEventsDesc => Pick("每局一份，可导入重新解析；关掉后旧段不再卸载", "One per session, importable; when off, nothing is unloaded");
        public static string SetRetention => Pick("日志保留", "Keep logs for");
        public static string SetRetentionDesc => Pick("超过的在游戏启动时删除", "Older logs are deleted when the game starts");
        public static string SetCheckUpdates => Pick("检查更新", "Check for updates");
        public static string SetCheckUpdatesDesc => Pick("启动时读一次仓库里的版本清单，不上传任何数据", "Reads the version manifest once at startup; sends nothing");
        public static string SetAutoInstall => Pick("自动更新", "Install updates automatically");
        public static string SetAutoInstallDesc => Pick("发现新版本就下载替换，下次启动生效", "Downloads and replaces the DLL; takes effect next launch");
        public static string BtnOpenLogFolder => Pick("打开日志目录", "Open log folder");

        public static string KeyToggle => Pick("显示 / 隐藏浮窗", "Show / hide the overlay");
        public static string KeyReset => Pick("重置当前统计", "Reset the current encounter");
        public static string KeyExport => Pick("导出当前段的 CSV", "Export the current encounter as CSV");
        public static string KeyMain => Pick("打开战斗记录", "Open the combat log");
        public static string KeyPress => Pick("按下新按键…", "Press a key…");
        public static string KeyNone => Pick("未设置", "None");
        public static string KeyHint => Pick("点按钮，再按下新的按键。Esc 取消，Delete 清除", "Click a button, then press a key. Esc cancels, Delete clears");
        public static string ColorsHint => Pick("浮窗卡片和表格里各职业的颜色", "Class colours on the overlay cards and in the tables");
        public static string TipResetColor => Pick("恢复默认", "Reset to default");

        public static string Seconds(float v) => v <= 0f ? Pick("从不", "Never") : Pick($"{v:0} 秒", $"{v:0} s");
        public static string Segments(float v) => v <= 0f ? Pick("全部", "All") : Pick($"{v:0} 段", $"{v:0}");
        public static string Days(float v) => v <= 0f ? Pick("永久", "Forever") : Pick($"{v:0} 天", $"{v:0} days");

        /// <summary>职业名：中文取游戏角色卡上的叫法，英文用枚举名。</summary>
        public static string JobName(int classType)
            => JobTable.IsKnown(classType) ? Pick(JobTable.Label(classType), JobTable.Key(classType)) : Pick("未知 / 怪物", "Unknown / monsters");

        // ================================================================ 更新横幅

        public static string BtnDisable => Pick("停用", "Disable");
        public static string BtnUpdate => Pick("更新", "Update");
        public static string BtnUpdating => Pick("更新中…", "Updating…");
        public static string BtnRelease => Pick("下载页", "Release");
        public static string TipDismiss => Pick("先不提示", "Dismiss");

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
