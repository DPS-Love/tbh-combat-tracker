using System;
using System.Diagnostics;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 更新 / 警告横幅该说什么：只算事实和文案，画在哪由界面决定（浮窗顶上；浮窗收起时单独一条）。
    /// 文案只在主线程拼——<see cref="UpdateChecker"/> 的后台线程只给事实，语言选择会碰 Il2Cpp。
    /// </summary>
    internal static class UpdateNotice
    {
        public enum Level { Error, Warning, Success, Neutral }

        public struct Content
        {
            public Level Level;
            public string Title;
            public string Sub;
        }

        private static bool _dismissed;

        /// <summary>清单里的版本比本机新。「更新」「下载页」只在这时（或被点名时）才有意义。
        /// v0.2.5 在游戏 1.2.8 上踩过坑：横幅因"游戏比构建时新"而出现，按钮却只看清单有没有
        /// 下载地址，于是把和本机一模一样的 v0.2.5 又下载替换了一遍。</summary>
        private static bool UpdateAvailable =>
            UpdateChecker.Current == UpdateChecker.State.UpdateAvailable;

        public static bool ShowUpdate =>
            UpdateAvailable && UpdateChecker.CanInstall && !UpdateChecker.Installed;

        public static bool Installing => UpdateChecker.Installing;

        public static bool ShowRelease =>
            (UpdateAvailable || UpdateChecker.CurrentBroken)
            && !string.IsNullOrEmpty(UpdateChecker.ReleaseUrl);

        /// <summary>本版本被点名在当前游戏上会出问题：给玩家一个「停用」按钮。</summary>
        public static bool ShowDisable => UpdateChecker.CurrentBroken && !Plugin.Disabled;

        /// <summary>严重情况：浮窗收起时也要单独显示出来。</summary>
        public static bool Urgent =>
            UpdateChecker.CurrentBroken
            || (UpdateChecker.Current == UpdateChecker.State.UpdateAvailable && UpdateChecker.LatestCritical);

        private static bool HasNotice =>
            UpdateChecker.CurrentBroken
            || Plugin.Disabled
            || UpdateChecker.Installed
            || UpdateChecker.InstallError != null
            || UpdateChecker.Current == UpdateChecker.State.UpdateAvailable
            || UpdateChecker.CoreHookFailed
            || UpdateChecker.GameNewerThanBuild;

        public static bool Visible => !_dismissed && HasNotice;

        public static void Dismiss() => _dismissed = true;

        // 每一个按钮都只响应玩家的点击：清单、网络、任何远端都不能触发它们
        public static void Install() => UpdateChecker.RequestInstall();
        public static void Disable() => Plugin.DisableForThisSession();
        public static void OpenRelease() => OpenUrl(UpdateChecker.ReleaseUrl);

        /// <summary>按优先级挑一条最要紧的说。红 = 出问题了，琥珀 = 有事要做，绿 = 做完了。</summary>
        public static Content Compose()
        {
            if (UpdateChecker.Installed)
                return C(Level.Success, Strings.Updated(UpdateChecker.Latest), Strings.UpdatedHint);

            if (Plugin.Disabled)
                return C(Level.Neutral, Strings.DisabledTitle,
                         ShowUpdate ? Strings.DisabledHintUpdate : UpdateAvailable ? Strings.DisabledHintRelease : "");

            if (UpdateChecker.CurrentBroken)
                return C(Level.Error, Strings.BrokenTitle(UpdateChecker.Mine, UpdateChecker.Game),
                         Pick(UpdateChecker.BrokenReasonZh, UpdateChecker.BrokenReasonEn) ?? Strings.BrokenHint);

            if (UpdateChecker.InstallError != null)
                return C(Level.Warning, Strings.UpdateFailed, UpdateChecker.InstallError);

            if (UpdateChecker.Current == UpdateChecker.State.UpdateAvailable)
                return C(UpdateChecker.LatestCritical ? Level.Error : Level.Warning,
                         Strings.UpdateAvailable(UpdateChecker.Latest, UpdateChecker.LatestCritical),
                         Pick(UpdateChecker.LatestNotesZh, UpdateChecker.LatestNotesEn) ?? "");

            if (UpdateChecker.CoreHookFailed)
                return C(Level.Warning, Strings.MismatchTitle, Strings.MismatchHint(UpdateChecker.Game, UpdateChecker.BuiltFor));

            // 只剩"游戏比构建时新"这一种情况
            return C(Level.Warning, Strings.GameNewerTitle(UpdateChecker.Game, UpdateChecker.BuiltFor), Strings.GameNewerHint);
        }

        private static Content C(Level level, string title, string sub) => new Content { Level = level, Title = title, Sub = sub };

        private static string Pick(string zh, string en)
        {
            var s = Strings.Chinese ? (zh ?? en) : (en ?? zh);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        public static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Application.OpenURL(url); return; }
            catch { /* 被裁剪或不可用时走下面 */ }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Mod.Log.Warning($"打不开浏览器：{e.Message}  地址：{url}"); }
        }
    }
}
