using System;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace TbhCombatTracker
{
    /// <summary>
    /// 加载器门面。统计和 UI 代码只认这里的 Log / Config，不直接依赖 BepInEx 类型，
    /// 这样万一以后换回 MelonLoader，只要重写这一个文件加 Plugin.cs 就行。
    /// </summary>
    internal static class Mod
    {
        public static ILog Log { get; private set; } = new NullLog();
        public static ModConfig Config { get; private set; }

        public static void Init(ManualLogSource log, ConfigFile config)
        {
            Log = new BepInExLog(log);
            Config = ModConfig.Bind(config);
        }

        public interface ILog
        {
            void Msg(string message);
            void Warning(string message);
            void Error(string message);
        }

        private sealed class BepInExLog : ILog
        {
            private readonly ManualLogSource _log;
            public BepInExLog(ManualLogSource log) => _log = log;
            public void Msg(string message) => _log.LogInfo(message);
            public void Warning(string message) => _log.LogWarning(message);
            public void Error(string message) => _log.LogError(message);
        }

        /// <summary>Load() 之前就被调用到时的兜底，避免空引用。</summary>
        private sealed class NullLog : ILog
        {
            public void Msg(string message) { }
            public void Warning(string message) { }
            public void Error(string message) { }
        }
    }

    /// <summary>配置项落在 BepInEx\config\dpslove.tbh.combattracker.cfg。</summary>
    internal class ModConfig
    {
        public ConfigEntry<string> ToggleKey;
        public ConfigEntry<string> ResetKey;
        public ConfigEntry<string> ExportKey;
        public ConfigEntry<string> MainPanelKey;
        public ConfigEntry<float> IdleResetSeconds;
        public ConfigEntry<bool> TrackIncoming;
        public ConfigEntry<bool> TrackHealing;
        public ConfigEntry<bool> TrackSkills;
        public ConfigEntry<bool> SegmentByStage;
        public ConfigEntry<int> HistorySize;
        public ConfigEntry<bool> ProbeMode;
        public ConfigEntry<bool> DiagnosticMode;
        public ConfigEntry<bool> HealingDebug;
        public ConfigEntry<bool> LocalizationDebug;
        public ConfigEntry<float> UiScale;
        public ConfigEntry<float> SkewDegrees;
        public ConfigEntry<bool> FixClickThrough;
        public ConfigEntry<bool> CheckUpdates;
        public ConfigEntry<bool> LogEvents;
        public ConfigEntry<int> LogRetentionDays;
        public ConfigEntry<bool> AutoInstall;
        public ConfigEntry<string> ManifestUrl;

        /// <summary>按 EEquipClassType 索引的职业配色，0 是未知/怪物。</summary>
        public ConfigEntry<string>[] JobColors;

        public static ModConfig Bind(ConfigFile c) => new ModConfig
        {
            ToggleKey = c.Bind("Hotkeys", "ToggleKey", "F9", "显示/隐藏面板"),
            ResetKey = c.Bind("Hotkeys", "ResetKey", "F10", "重置当前战斗统计"),
            ExportKey = c.Bind("Hotkeys", "ExportKey", "F11", "导出 CSV"),
            MainPanelKey = c.Bind("Hotkeys", "MainPanelKey", "F8", "打开 / 关闭战斗记录主面板（浮窗标题栏上的「记录」也行）"),

            IdleResetSeconds = c.Bind("Tracking", "IdleResetSeconds", 8f,
                "多少秒没有任何伤害就自动开启新一场战斗统计，0 = 从不自动重置"),
            TrackIncoming = c.Bind("Tracking", "TrackIncoming", true,
                "同时统计英雄承受的伤害"),
            TrackHealing = c.Bind("Tracking", "TrackHealing", true,
                "统计英雄获得的治疗。牧师主动治疗按施法者归因；自然回血没有来源，"
                + "会归到\"自动回复\"一档"),
            TrackSkills = c.Bind("Tracking", "TrackSkills", true,
                "按技能拆分伤害（普通攻击 / 陨石 / 圣剑…）。游戏的技能类名没被混淆，"
                + "所以拆出来是真名而不是编号"),
            SegmentByStage = c.Bind("Tracking", "SegmentByStage", true,
                "按关卡自动分段（读游戏的 StageManager 状态机）。"
                + "关掉的话退回按 IdleResetSeconds 的空闲时间分段"),
            HistorySize = c.Bind("Tracking", "HistorySize", 200,
                "主面板里本局保留多少段已结束的战斗，0 = 不限，最多 1000。"
                + "更早的仍在战斗日志里，导入就能看"),
            ProbeMode = c.Bind("Tracking", "ProbeMode", false,
                "调试模式：把首次遇到的每个攻击者的各种名字字段打到日志，用来确认怎么给英雄取名"),
            HealingDebug = c.Bind("Tracking", "HealingDebug", false,
                "把每一次生命恢复连同判定出的来源打到日志，用来核对恢复分类是否准确。"
                + "和 DiagnosticMode 不同，这个开关不会自动关闭——它只记日志，不打补丁，不会搞崩游戏"),
            LocalizationDebug = c.Bind("Tracking", "LocalizationDebug", false,
                "把每次本地化查询的结果打到日志（每个键只打一次）。"
                + "游戏更新后如果面板上出现原文或空白，开一轮就能看出是哪个键失效了"),
            DiagnosticMode = c.Bind("Tracking", "DiagnosticMode", false,
                "诊断模式：给血量控制器和 Monster 的所有方法挂钩子，记录前几次调用和实参，"
                + "用来定位伤害到底走哪条路。日志量大，查完记得改回 false"),

            UiScale = c.Bind("UI", "UiScale", 1.0f, "面板缩放"),
            SkewDegrees = c.Bind("UI", "SkewDegrees", -30f,
                "卡片色块的斜切角度，Horizoverlay 用的是 -30。设成 0 就是普通矩形"),
            FixClickThrough = c.Bind("UI", "FixClickThrough", true,
                "光标移到面板上时临时关掉游戏窗口的点击穿透，让按钮可点、窗口可拖。"
                + "关掉的话面板就是纯展示，点击会穿透到下面的程序"),

            LogEvents = c.Bind("Log", "LogEvents", true,
                "把战斗事件写进日志文件（BepInEx\\TbhCombatTracker\\logs，每局一个）。"
                + "主面板可以导入任意一份，按当前版本重新解析——以后加了新的统计维度，旧日志也能算出来。"
                + "关掉的话主面板只有本局的数据"),
            LogRetentionDays = c.Bind("Log", "LogRetentionDays", 30,
                "战斗日志保留天数，超过的在启动时删除；0 = 永久保留"),

            CheckUpdates = c.Bind("Update", "CheckUpdates", true,
                "启动时联网检查一次更新：只 GET 仓库里的 manifest.json，不上传任何数据。"
                + "有新版本、或本版本被标记为在当前游戏版本上会出问题时，在面板上提示"),
            AutoInstall = c.Bind("Update", "AutoInstall", false,
                "发现新版本后自动下载更新（校验 SHA-256 后替换 DLL，下次启动生效）。"
                + "关着的话只提示，点面板横幅上的「更新」才下载并替换"),
            ManifestUrl = c.Bind("Update", "ManifestUrl", "",
                "更新清单地址。留空用官方地址（jsDelivr，不通再试 GitHub）；填 URL 可走镜像；"
                + "填本地文件路径可用来测试横幅（tools/test-manifest.ps1 会生成一份），"
                + "此时清单里的 download 也可以是本地 zip 路径"),

            JobColors = BindJobColors(c),
        };

        /// <summary>
        /// 六个职业的卡片配色，默认取各自立绘的主色。
        /// 单独开一段是为了让人能直接改十六进制值调色，不用重新编译。
        /// 支持 #RGB / #RRGGBB / #RRGGBBAA。
        /// </summary>
        private static ConfigEntry<string>[] BindJobColors(ConfigFile c)
        {
            var arr = new ConfigEntry<string>[JobTable.Count];
            for (var i = 0; i < arr.Length; i++)
            {
                var label = JobTable.Label(i) ?? "未知来源 / 怪物 / 环境伤害";
                arr[i] = c.Bind("Colors", JobTable.Key(i), JobTable.DefaultColors[i],
                                $"{label} 的卡片颜色（#RGB / #RRGGBB / #RRGGBBAA）");
            }
            return arr;
        }
    }
}
