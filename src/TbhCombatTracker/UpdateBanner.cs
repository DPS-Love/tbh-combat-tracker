using System;
using System.Diagnostics;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 面板顶部的更新/警告横幅。只在主线程拼字符串——<see cref="UpdateChecker"/> 的
    /// 后台线程只给事实，语言选择（会碰 Il2Cpp）在这里做。
    ///
    /// 严重情况（本版本被标记为会出问题、或有标为重要的更新）在面板隐藏时也会单独画一条，
    /// 否则按 F9 收起面板的玩家永远看不到。
    /// </summary>
    internal static class UpdateBanner
    {
        public const float Height = 40f;

        private static bool _dismissed;
        private static bool _stylesReady;
        private static GUIStyle _title, _sub, _btn;
        private static Texture2D _white;

        private static bool Urgent =>
            UpdateChecker.CurrentBroken
            || (UpdateChecker.Current == UpdateChecker.State.UpdateAvailable && UpdateChecker.LatestCritical);

        private static bool HasNotice =>
            UpdateChecker.CurrentBroken
            || Plugin.Disabled
            || UpdateChecker.Installed
            || UpdateChecker.InstallError != null
            || UpdateChecker.Current == UpdateChecker.State.UpdateAvailable
            || UpdateChecker.GameNewerThanBuild;

        public static bool Visible => !_dismissed && HasNotice;

        /// <summary>画在主面板里，r 是横幅在窗口内的矩形。</summary>
        public static void Draw(Rect r)
        {
            EnsureStyles();
            DrawInner(r);
        }

        /// <summary>面板隐藏时，严重情况单独画一条在屏幕左上角。</summary>
        public static void DrawStandalone()
        {
            if (_dismissed || !Urgent) return;
            try
            {
                EnsureStyles();
                var r = new Rect(20f, 20f, 480f, Height - 4f);
                DrawInner(r);
            }
            catch
            {
                // 横幅画不出来不值得让游戏承担任何后果
            }
        }

        private static void DrawInner(Rect r)
        {
            var (bg, title, sub) = Compose();

            GUI.color = bg;
            GUI.DrawTexture(r, _white);
            GUI.color = Color.white;

            var textW = r.width - 6f - ButtonsWidth();
            GUI.Label(new Rect(r.x + 6f, r.y + 2f, textW, 16f), title, _title);
            GUI.Label(new Rect(r.x + 6f, r.y + 18f, textW, 16f), sub, _sub);

            var x = r.xMax - 4f;
            const float bw = 54f, bh = 20f;
            var by = r.y + (r.height - bh) / 2f;

            // 右起：关闭、停用（仅被点名时）、更新、下载页——每一个都只响应玩家的点击
            x -= bw * 0.55f;
            if (GUI.Button(new Rect(x, by, bw * 0.55f, bh), "×", _btn))
                _dismissed = true;

            if (UpdateChecker.CurrentBroken && !Plugin.Disabled)
            {
                x -= bw + 4f;
                if (GUI.Button(new Rect(x, by, bw, bh), BuiltinText.Pick("停用", "Disable"), _btn))
                    Plugin.DisableForThisSession();
            }

            if (UpdateChecker.CanInstall && !UpdateChecker.Installed)
            {
                x -= bw + 4f;
                var label = UpdateChecker.Installing
                    ? BuiltinText.Pick("更新中…", "Updating…")
                    : BuiltinText.Pick("更新", "Update");
                GUI.enabled = !UpdateChecker.Installing;
                if (GUI.Button(new Rect(x, by, bw, bh), label, _btn))
                    UpdateChecker.RequestInstall();
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(UpdateChecker.ReleaseUrl))
            {
                x -= bw + 4f;
                if (GUI.Button(new Rect(x, by, bw, bh), BuiltinText.Pick("下载页", "Release"), _btn))
                    OpenUrl(UpdateChecker.ReleaseUrl);
            }
        }

        private static float ButtonsWidth()
        {
            var w = 54f * 0.55f + 4f;
            if (UpdateChecker.CurrentBroken && !Plugin.Disabled) w += 58f;
            if (UpdateChecker.CanInstall && !UpdateChecker.Installed) w += 58f;
            if (!string.IsNullOrEmpty(UpdateChecker.ReleaseUrl)) w += 58f;
            return w;
        }

        /// <summary>按优先级挑一条最要紧的说。颜色：红 = 出问题了，琥珀 = 有事要做，绿 = 做完了。</summary>
        private static (Color bg, string title, string sub) Compose()
        {
            var red = new Color(0.70f, 0.12f, 0.12f, 0.92f);
            var amber = new Color(0.72f, 0.48f, 0.05f, 0.92f);
            var green = new Color(0.10f, 0.50f, 0.22f, 0.92f);

            if (UpdateChecker.Installed)
                return (green,
                    BuiltinText.Pick($"已更新到 v{UpdateChecker.Latest}，重启游戏生效",
                                     $"Updated to v{UpdateChecker.Latest} — restart the game"),
                    BuiltinText.Pick("如新版本加载失败，把 plugins 里的 .old 改回 .dll 即可回滚",
                                     "If it fails to load, rename the .old file in plugins back to .dll"));

            if (Plugin.Disabled)
                return (new Color(0.30f, 0.30f, 0.34f, 0.92f),
                    BuiltinText.Pick("已停用本次会话的统计，重启游戏恢复",
                                     "Stats disabled for this session — restart the game to re-enable"),
                    UpdateChecker.CanInstall
                        ? BuiltinText.Pick("可以先点「更新」，重启后就是新版本", "Click Update now; the new version loads on restart")
                        : BuiltinText.Pick("请到下载页获取新版本", "Get the new version from the release page"));

            if (UpdateChecker.CurrentBroken)
                return (red,
                    BuiltinText.Pick($"v{UpdateChecker.Mine} 在游戏 {UpdateChecker.Game} 上会出问题",
                                     $"v{UpdateChecker.Mine} is known to misbehave on game {UpdateChecker.Game}"),
                    Pick(UpdateChecker.BrokenReasonZh, UpdateChecker.BrokenReasonEn)
                        ?? BuiltinText.Pick("可以停用本次统计，或更新到新版本", "You can disable stats for now, or update"));

            if (UpdateChecker.InstallError != null)
                return (amber,
                    BuiltinText.Pick("自动更新失败，请手动下载", "Auto-update failed — download manually"),
                    UpdateChecker.InstallError);

            if (UpdateChecker.Current == UpdateChecker.State.UpdateAvailable)
                return (UpdateChecker.LatestCritical ? red : amber,
                    BuiltinText.Pick($"有新版本 v{UpdateChecker.Latest}" + (UpdateChecker.LatestCritical ? "（重要）" : ""),
                                     $"Update available: v{UpdateChecker.Latest}" + (UpdateChecker.LatestCritical ? " (important)" : "")),
                    Pick(UpdateChecker.LatestNotesZh, UpdateChecker.LatestNotesEn) ?? "");

            // 只剩"游戏比构建时新"这一种情况
            return (amber,
                BuiltinText.Pick($"游戏已更新到 {UpdateChecker.Game}，本版 Mod 是为 {UpdateChecker.BuiltFor} 构建的",
                                 $"Game updated to {UpdateChecker.Game}; this build targets {UpdateChecker.BuiltFor}"),
                BuiltinText.Pick("统计可能缺失或不准，不影响游戏本身。留意新版本",
                                 "Stats may be missing or off; the game itself is unaffected. Watch for an update"));
        }

        private static string Pick(string zh, string en)
        {
            var s = BuiltinText.Chinese ? (zh ?? en) : (en ?? zh);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static void OpenUrl(string url)
        {
            try { Application.OpenURL(url); return; }
            catch { /* 被裁剪或不可用时走下面 */ }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Mod.Log.Warning($"打不开浏览器：{e.Message}  地址：{url}"); }
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;
            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), wordWrap = false,
            };
            _title.normal.textColor = Color.white;
            _sub = new GUIStyle(_title) { fontSize = 9, fontStyle = FontStyle.Normal };
            _sub.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 10, padding = new RectOffset(2, 2, 0, 0) };
            _stylesReady = true;
        }
    }
}
