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
        public ConfigEntry<float> IdleResetSeconds;
        public ConfigEntry<bool> TrackIncoming;
        public ConfigEntry<bool> TrackHealing;
        public ConfigEntry<bool> TrackSkills;
        public ConfigEntry<bool> SegmentByStage;
        public ConfigEntry<bool> ProbeMode;
        public ConfigEntry<bool> DiagnosticMode;
        public ConfigEntry<bool> HealingDebug;
        public ConfigEntry<bool> LocalizationDebug;
        public ConfigEntry<float> UiScale;
        public ConfigEntry<float> SkewDegrees;
        public ConfigEntry<bool> FixClickThrough;

        /// <summary>按 EEquipClassType 索引的职业配色，0 是未知/怪物。</summary>
        public ConfigEntry<string>[] JobColors;

        public static ModConfig Bind(ConfigFile c) => new ModConfig
        {
            ToggleKey = c.Bind("Hotkeys", "ToggleKey", "F9", "显示/隐藏面板"),
            ResetKey = c.Bind("Hotkeys", "ResetKey", "F10", "重置当前战斗统计"),
            ExportKey = c.Bind("Hotkeys", "ExportKey", "F11", "导出 CSV"),

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
            ProbeMode = c.Bind("Tracking", "ProbeMode", false,
                "调试模式：把首次遇到的每个攻击者的各种名字字段打到日志，用来确认怎么给英雄取名"),
            HealingDebug = c.Bind("Tracking", "HealingDebug", false,
                "把每一次生命恢复连同判定出的来源打到日志，用来核对恢复分类是否准确。"
                + "和 DiagnosticMode 不同，这个开关不会自动关闭——它只记日志，不打补丁，不会搞崩游戏"),
            LocalizationDebug = c.Bind("Tracking", "LocalizationDebug", false,
                "把本地化查询的尝试结果打到日志。伤害类型/元素属性的译文键名只能靠运行时试，"
                + "开一轮就能看出游戏用的是哪种命名"),
            DiagnosticMode = c.Bind("Tracking", "DiagnosticMode", false,
                "诊断模式：给血量控制器和 Monster 的所有方法挂钩子，记录前几次调用和实参，"
                + "用来定位伤害到底走哪条路。日志量大，查完记得改回 false"),

            UiScale = c.Bind("UI", "UiScale", 1.0f, "面板缩放"),
            SkewDegrees = c.Bind("UI", "SkewDegrees", -30f,
                "卡片色块的斜切角度，Horizoverlay 用的是 -30。设成 0 就是普通矩形"),
            FixClickThrough = c.Bind("UI", "FixClickThrough", true,
                "光标移到面板上时临时关掉游戏窗口的点击穿透，让按钮可点、窗口可拖。"
                + "关掉的话面板就是纯展示，点击会穿透到下面的程序"),

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
