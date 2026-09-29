using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 设置窗口：配置文件里给玩家用的那些项都能在这里改，改完 BepInEx 自动写回 cfg。
    /// 能立刻生效的立刻生效（缩放、斜切、分段、内存上限、热键、颜色）；
    /// 挂钩子和开日志只在启动时做，那几项标着「重启后生效」。调试用的开关不放进来。
    /// </summary>
    internal sealed class SettingsView : Window
    {
        private const float W = 600f, H = 456f;
        private const float RailX = 10f, RailW = 138f, TabH = 32f;
        private const float CX = RailX + RailW + 14f, CW = W - CX - 16f;
        /// <summary>一行：标题（右边放开关 / 滑杆）+ 最多两行的说明，说明占满整行宽度。</summary>
        private const float RowH = 56f;

        private static readonly string[] Presets =
        {
            "#C0392B", "#E74C3C", "#E07B39", "#F0D98C", "#F2C94C", "#5FB04A",
            "#2AA8A0", "#4FA3E3", "#3F6FD8", "#8E5BD0", "#D1548C", "#9E9E9E",
        };

        private int _page;
        private readonly Label _icon, _title, _footer;
        private readonly Button _close;
        private readonly Button[] _tabs = new Button[5];
        private readonly Node[] _pages = new Node[5];
        private readonly Box _railLine;

        // 行：标题、说明、「重启后生效」
        private sealed class Row
        {
            public Label Title, Desc, Tag;
            public Box TagBg;
        }

        private readonly List<(Row Row, int Page, int Index, Func<string> Title, Func<string> Desc, bool Restart)> _rows =
            new List<(Row, int, int, Func<string>, Func<string>, bool)>();

        // 控件
        private readonly Slider _scale, _skew, _opacity, _idle, _keep, _retention;
        private readonly Toggle _byStage, _incoming, _healing, _skills, _logEvents, _checkUpdates, _autoInstall;
        private readonly Button _openLogs;
        private readonly (ConfigEntry<string> Entry, Func<string> Title, Button Button, Label Label)[] _keys;
        private readonly Label _keyHint, _colorHint;
        private readonly (Label Name, Box Current, Swatch[] Swatches, Button Reset)[] _colors;

        public SettingsView(Transform canvas) : base("settings", canvas, W, H, Theme.WindowBg)
        {
            DragHandle(Body, new Rect(0f, 0f, W, TitleH));
            _icon = new Label(Body, 15, Theme.Accent, TextAnchor.MiddleCenter, font: UiKit.IconFont ?? UiKit.TextFont, name: "Icon").NoWrap();
            _icon.Text = Glyphs.Of(Glyphs.Settings);
            _title = new Label(Body, Theme.FontTitle, Theme.Text, bold: true, name: "Title");
            _close = new Button(this, Body, Glyphs.Cancel, null, () => Show(false), tip: () => Strings.TipClose);
            _footer = new Label(Body, Theme.FontTiny, Theme.TextFaint, name: "Footer");
            _railLine = new Box(Body, Theme.Divider, 0f, "RailLine");

            var glyphs = new[] { Glyphs.Sliders, Glyphs.AreaChart, Glyphs.Document, Glyphs.Keyboard, Glyphs.Color };
            for (var i = 0; i < _tabs.Length; i++)
            {
                var page = i;
                _tabs[i] = new Button(this, Body, glyphs[i], "", () =>
                {
                    _page = page;
                    Dirty = true;
                }, ButtonStyle.Tab);
                _pages[i] = new Node("Page" + i, Body);
                _pages[i].Place(0f, 0f, W, H);
            }

            var cfg = Mod.Config;

            // ---- 界面 ----
            AddRow(0, 0, () => Strings.SetUiScale, () => Strings.SetUiScaleDesc);
            _scale = new Slider(this, _pages[0], 0.5f, 2.5f, 0.05f, v => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "×",
                                v => Set(cfg.UiScale, v));
            AddRow(0, 1, () => Strings.SetSkew, () => Strings.SetSkewDesc);
            _skew = new Slider(this, _pages[0], -45f, 45f, 1f, v => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "°",
                               v => Set(cfg.SkewDegrees, v));
            AddRow(0, 2, () => Strings.SetOverlayBg, () => Strings.SetOverlayBgDesc);
            _opacity = new Slider(this, _pages[0], 0f, 100f, 5f, v => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%",
                                  v => Set(cfg.BackgroundOpacity, v / 100f));
            // 拖着 / 指着这个滑杆时，各窗口（浮窗也亮出背景）按滑杆上的值画，松手才写进配置
            _opacity.Preview = v => UiRoot.PreviewOpacity = v.HasValue ? v.Value / 100f : (float?)null;

            // ---- 统计 ----
            AddRow(1, 0, () => Strings.SetByStage, () => Strings.SetByStageDesc);
            _byStage = new Toggle(this, _pages[1], on => Set(cfg.SegmentByStage, on));
            AddRow(1, 1, () => Strings.SetIdle, () => Strings.SetIdleDesc);
            _idle = new Slider(this, _pages[1], 0f, 60f, 1f, Strings.Seconds, v => Set(cfg.IdleResetSeconds, v));
            AddRow(1, 2, () => Strings.SetKeep, () => Strings.SetKeepDesc);
            _keep = new Slider(this, _pages[1], 0f, 300f, 5f, Strings.Segments, v => Set(cfg.KeepInMemory, Mathf.RoundToInt(v)));
            AddRow(1, 3, () => Strings.SetIncoming, () => Strings.SetIncomingDesc, restart: true);
            _incoming = new Toggle(this, _pages[1], on => Set(cfg.TrackIncoming, on));
            AddRow(1, 4, () => Strings.SetHealing, () => Strings.SetHealingDesc, restart: true);
            _healing = new Toggle(this, _pages[1], on => Set(cfg.TrackHealing, on));
            AddRow(1, 5, () => Strings.SetSkills, () => Strings.SetSkillsDesc, restart: true);
            _skills = new Toggle(this, _pages[1], on => Set(cfg.TrackSkills, on));

            // ---- 日志与更新 ----
            AddRow(2, 0, () => Strings.SetLogEvents, () => Strings.SetLogEventsDesc, restart: true);
            _logEvents = new Toggle(this, _pages[2], on => Set(cfg.LogEvents, on));
            AddRow(2, 1, () => Strings.SetRetention, () => Strings.SetRetentionDesc, restart: true);
            _retention = new Slider(this, _pages[2], 0f, 180f, 5f, Strings.Days, v => Set(cfg.LogRetentionDays, Mathf.RoundToInt(v)));
            AddRow(2, 2, () => Strings.SetCheckUpdates, () => Strings.SetCheckUpdatesDesc, restart: true);
            _checkUpdates = new Toggle(this, _pages[2], on => Set(cfg.CheckUpdates, on));
            AddRow(2, 3, () => Strings.SetAutoInstall, () => Strings.SetAutoInstallDesc, restart: true);
            _autoInstall = new Toggle(this, _pages[2], on => Set(cfg.AutoInstall, on));
            _openLogs = new Button(this, _pages[2], Glyphs.FolderOpen, "", () => UiRoot.Main.OpenLogFolder(), ButtonStyle.Subtle);

            // ---- 热键 ----
            var keys = new (ConfigEntry<string>, Func<string>)[]
            {
                (cfg.ToggleKey, () => Strings.KeyToggle),
                (cfg.ResetKey, () => Strings.KeyReset),
                (cfg.ExportKey, () => Strings.KeyExport),
                (cfg.MainPanelKey, () => Strings.KeyMain),
            };
            _keys = new (ConfigEntry<string>, Func<string>, Button, Label)[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                var entry = keys[i].Item1;
                var label = new Label(_pages[3], Theme.FontBody, Theme.Text, name: "KeyName");
                var button = new Button(this, _pages[3], Glyphs.Keyboard, "", () =>
                {
                    if (UiRoot.CaptureTarget == entry) UiRoot.CancelCapture();
                    else UiRoot.BeginCapture(entry, () => Dirty = true);
                    Dirty = true;
                }, ButtonStyle.Subtle);
                _keys[i] = (entry, keys[i].Item2, button, label);
            }
            _keyHint = new Label(_pages[3], Theme.FontTiny, Theme.TextDim, name: "Hint");

            // ---- 颜色 ----
            _colorHint = new Label(_pages[4], Theme.FontTiny, Theme.TextDim, name: "Hint");
            _colors = new (Label, Box, Swatch[], Button)[JobTable.Count];
            for (var j = 0; j < JobTable.Count; j++)
            {
                var job = ColorOrder(j);
                var name = new Label(_pages[4], Theme.FontBody, Theme.Text, name: "Job");
                var current = new Box(_pages[4], Color.white, 4f, "Current");
                var swatches = new Swatch[Presets.Length];
                for (var k = 0; k < Presets.Length; k++)
                {
                    var hex = Presets[k];
                    swatches[k] = new Swatch(this, _pages[4], () => Set(cfg.JobColors[job], hex)) { Tip = () => hex };
                }
                var reset = new Button(this, _pages[4], Glyphs.Refresh, null, () => Set(cfg.JobColors[job], JobTable.DefaultColors[job]),
                                       tip: () => Strings.TipResetColor);
                _colors[j] = (name, current, swatches, reset);
            }
        }

        public override Vector2 DefaultPosition() => new Vector2(120f, 120f);

        public override void Show(bool on)
        {
            base.Show(on);
            if (!on && UiRoot.CapturingKey) UiRoot.CancelCapture();
            if (!on) UiRoot.PreviewOpacity = null;
        }

        /// <summary>职业按 1..6 排，未知 / 怪物（0）放最后。</summary>
        private static int ColorOrder(int row) => row < JobTable.Count - 1 ? row + 1 : 0;

        private void AddRow(int page, int index, Func<string> title, Func<string> desc, bool restart = false)
        {
            var p = _pages[page];
            var row = new Row
            {
                Title = new Label(p, Theme.FontBody, Theme.Text, name: "Title"),
                Desc = new Label(p, Theme.FontTiny, Theme.TextDim, name: "Desc"),
            };
            if (restart)
            {
                row.TagBg = new Box(p, Theme.With(Theme.Warning, 0.18f), 7f, "Tag");
                row.Tag = new Label(row.TagBg, 9, new Color32(0xE8, 0xB3, 0x4B, 0xFF), TextAnchor.MiddleCenter, name: "TagText").NoWrap();
            }
            _rows.Add((row, page, index, title, desc, restart));
        }

        private void Set<T>(ConfigEntry<T> entry, T value)
        {
            try
            {
                entry.Value = value;   // BepInEx 默认改了就写回 cfg
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"设置 {entry.Definition.Key} 失败：{e.GetType().Name}: {e.Message}");
            }
            Dirty = true;
            UiRoot.MarkAllDirty();
        }

        public override void Refresh()
        {
            var cfg = Mod.Config;
            _icon.Place(10f, 0f, 22f, TitleH);
            _title.Text = Strings.SettingsTitle;
            _title.Place(36f, 0f, 300f, TitleH);
            _close.Place(W - 36f, 4f, 28f, 24f);
            _footer.Text = Strings.SettingsFooter;
            _footer.Place(CX, H - 26f, CW, 18f);
            _railLine.Place(RailX + RailW + 6f, TitleH + 8f, 1f, H - TitleH - 20f);

            var tabNames = new[] { Strings.TabDisplay, Strings.TabTracking, Strings.TabLogUpdate, Strings.TabHotkeys, Strings.TabColors };
            for (var i = 0; i < _tabs.Length; i++)
            {
                _tabs[i].SetText(tabNames[i]);
                _tabs[i].On = i == _page;
                _tabs[i].Place(RailX, TitleH + 8f + i * (TabH + 4f), RailW, TabH);
                _pages[i].Active = i == _page;
            }

            // ---- 行标题 ----
            foreach (var (row, page, index, title, desc, restart) in _rows)
            {
                if (page != _page) continue;
                var y = Top(index);
                row.Title.Text = title();
                row.Title.Place(CX, y, 240f, 22f);
                // 控件都放在标题那一行的右边，说明占满整行，放不下就折成两行
                row.Desc.Text = desc();
                row.Desc.Place(CX, y + 22f, CW, 30f);
                if (restart)
                {
                    row.Tag.Text = Strings.RestartTag;
                    var tw = Mathf.Ceil(row.Tag.PreferredWidth) + 12f;
                    var titleW = Mathf.Min(200f, Mathf.Ceil(row.Title.PreferredWidth));
                    row.TagBg.Place(CX + titleW + 8f, y + 4f, tw, 15f);
                    row.Tag.Place(0f, 0f, tw, 15f);
                }
            }

            var sliderX = CX + CW - 196f;
            switch (_page)
            {
                case 0:
                    _scale.Value = cfg.UiScale.Value;
                    _scale.Place(sliderX, Top(0) + 2f, 130f);
                    _skew.Value = cfg.SkewDegrees.Value;
                    _skew.Place(sliderX, Top(1) + 2f, 130f);
                    _opacity.Value = Mathf.Clamp01(cfg.BackgroundOpacity.Value) * 100f;
                    _opacity.Place(sliderX, Top(2) + 2f, 130f);
                    break;

                case 1:
                    _byStage.On = cfg.SegmentByStage.Value;
                    _byStage.Place(CX + CW - Toggle.W, Top(0) + 2f);
                    _idle.Value = cfg.IdleResetSeconds.Value;
                    _idle.Place(sliderX, Top(1) + 2f, 130f);
                    _keep.Value = cfg.KeepInMemory.Value;
                    _keep.Place(sliderX, Top(2) + 2f, 130f);
                    _incoming.On = cfg.TrackIncoming.Value;
                    _incoming.Place(CX + CW - Toggle.W, Top(3) + 2f);
                    _healing.On = cfg.TrackHealing.Value;
                    _healing.Place(CX + CW - Toggle.W, Top(4) + 2f);
                    _skills.On = cfg.TrackSkills.Value;
                    _skills.Place(CX + CW - Toggle.W, Top(5) + 2f);
                    // 按关卡分段时空闲秒数不起作用，标题灰掉提示一下
                    foreach (var r in _rows)
                        if (r.Page == 1 && r.Index == 1) r.Row.Title.Color = cfg.SegmentByStage.Value ? Theme.TextDim : Theme.Text;
                    break;

                case 2:
                    _logEvents.On = cfg.LogEvents.Value;
                    _logEvents.Place(CX + CW - Toggle.W, Top(0) + 2f);
                    _retention.Value = cfg.LogRetentionDays.Value;
                    _retention.Place(sliderX, Top(1) + 2f, 130f);
                    _checkUpdates.On = cfg.CheckUpdates.Value;
                    _checkUpdates.Place(CX + CW - Toggle.W, Top(2) + 2f);
                    _autoInstall.On = cfg.AutoInstall.Value;
                    _autoInstall.Place(CX + CW - Toggle.W, Top(3) + 2f);
                    _openLogs.SetText(Strings.BtnOpenLogFolder);
                    var bw = _openLogs.Measure(12f);
                    _openLogs.Place(CX, Top(4) + 8f, bw, 28f);
                    break;

                case 3:
                    for (var i = 0; i < _keys.Length; i++)
                    {
                        var k = _keys[i];
                        var y = Top(i);
                        k.Label.Text = k.Title();
                        k.Label.Place(CX, y, CW - 150f, 34f);
                        var capturing = UiRoot.CaptureTarget == k.Entry;
                        k.Button.SetText(capturing ? Strings.KeyPress : string.IsNullOrWhiteSpace(k.Entry.Value) ? Strings.KeyNone : k.Entry.Value);
                        k.Button.On = capturing;
                        k.Button.Place(CX + CW - 140f, y + 3f, 140f, 28f);
                    }
                    _keyHint.Text = Strings.KeyHint;
                    _keyHint.Place(CX, Top(_keys.Length) + 4f, CW, 18f);
                    break;

                case 4:
                    _colorHint.Text = Strings.ColorsHint;
                    _colorHint.Place(CX, TitleH + 10f, CW, 18f);
                    for (var j = 0; j < _colors.Length; j++)
                    {
                        var job = ColorOrder(j);
                        var c = _colors[j];
                        var y = TitleH + 34f + j * 36f;
                        c.Name.Text = Strings.JobName(job);
                        c.Name.Place(CX, y, 96f, 26f);
                        var current = JobTable.ColorOf(job);
                        c.Current.Color = current;
                        c.Current.Place(CX + 98f, y + 3f, 20f, 20f);
                        var hex = Normalize(cfg.JobColors[job].Value);
                        for (var k = 0; k < c.Swatches.Length; k++)
                        {
                            var s = c.Swatches[k];
                            s.Color = Hex(Presets[k]);
                            s.Selected = hex == Presets[k];
                            s.Place(CX + 128f + k * 22f, y + 3f, 20f);
                        }
                        c.Reset.Place(CX + CW - 26f, y + 1f, 26f, 24f);
                    }
                    break;
            }
        }

        private static float Top(int index) => TitleH + 12f + index * RowH;

        private static string Normalize(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "";
            var s = hex.Trim().ToUpperInvariant();
            if (!s.StartsWith("#")) s = "#" + s;
            if (s.Length == 9) s = s.Substring(0, 7);   // 忽略透明度
            return s;
        }

        private static Color Hex(string hex)
        {
            var s = hex.TrimStart('#');
            var r = Convert.ToInt32(s.Substring(0, 2), 16);
            var g = Convert.ToInt32(s.Substring(2, 2), 16);
            var b = Convert.ToInt32(s.Substring(4, 2), 16);
            return new Color32((byte)r, (byte)g, (byte)b, 0xFF);
        }
    }
}
