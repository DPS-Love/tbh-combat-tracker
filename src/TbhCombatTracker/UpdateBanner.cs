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
            || UpdateChecker.CoreHookFailed
            || UpdateChecker.GameNewerThanBuild;

        public static bool Visible => !_dismissed && HasNotice;

        /// <summary>画在主面板里，r 是横幅在窗口内的矩形。</summary>
        public static void Draw(Rect r)
        {
            EnsureStyles();
            DrawInner(r);
        }

        /// <summary>
        /// 面板不在时单独画一条在屏幕左上角。
        /// 玩家主动收起面板（F9）只画严重情况；面板因为连续绘制失败被熔断（panelGone）时，
        /// 这里是唯一还能说话的地方，有什么都画。
        /// </summary>
        public static void DrawStandalone(bool panelGone)
        {
            if (_dismissed) return;
            if (!(Urgent || (panelGone && HasNotice))) return;
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
                if (GUI.Button(new Rect(x, by, bw, bh), Strings.BtnDisable, _btn))
                    Plugin.DisableForThisSession();
            }

            if (UpdateChecker.CanInstall && !UpdateChecker.Installed)
            {
                x -= bw + 4f;
                var label = UpdateChecker.Installing
                    ? Strings.BtnUpdating
                    : Strings.BtnUpdate;
                GUI.enabled = !UpdateChecker.Installing;
                if (GUI.Button(new Rect(x, by, bw, bh), label, _btn))
                    UpdateChecker.RequestInstall();
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(UpdateChecker.ReleaseUrl))
            {
                x -= bw + 4f;
                if (GUI.Button(new Rect(x, by, bw, bh), Strings.BtnRelease, _btn))
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
                    Strings.Updated(UpdateChecker.Latest),
                    Strings.UpdatedHint);

            if (Plugin.Disabled)
                return (new Color(0.30f, 0.30f, 0.34f, 0.92f),
                    Strings.DisabledTitle,
                    UpdateChecker.CanInstall ? Strings.DisabledHintUpdate : Strings.DisabledHintRelease);

            if (UpdateChecker.CurrentBroken)
                return (red,
                    Strings.BrokenTitle(UpdateChecker.Mine, UpdateChecker.Game),
                    Pick(UpdateChecker.BrokenReasonZh, UpdateChecker.BrokenReasonEn) ?? Strings.BrokenHint);

            if (UpdateChecker.InstallError != null)
                return (amber,
                    Strings.UpdateFailed,
                    UpdateChecker.InstallError);

            if (UpdateChecker.Current == UpdateChecker.State.UpdateAvailable)
                return (UpdateChecker.LatestCritical ? red : amber,
                    Strings.UpdateAvailable(UpdateChecker.Latest, UpdateChecker.LatestCritical),
                    Pick(UpdateChecker.LatestNotesZh, UpdateChecker.LatestNotesEn) ?? "");

            if (UpdateChecker.CoreHookFailed)
                return (amber,
                    Strings.MismatchTitle,
                    Strings.MismatchHint(UpdateChecker.Game, UpdateChecker.BuiltFor));

            // 只剩"游戏比构建时新"这一种情况
            return (amber,
                Strings.GameNewerTitle(UpdateChecker.Game, UpdateChecker.BuiltFor),
                Strings.GameNewerHint);
        }

        private static string Pick(string zh, string en)
        {
            var s = Strings.Chinese ? (zh ?? en) : (en ?? zh);
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
