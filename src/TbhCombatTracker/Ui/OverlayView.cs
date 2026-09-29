using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 实时浮窗，形制参考 FFXIV ACT 的 Horizoverlay：每个来源一张窄卡片横向并排，
    /// 卡片主体是斜切的平行四边形（职业色），底下一条细的占比条。
    /// 标题栏：战斗记录、当前段、视图切换、重置、设置。更新横幅也在这里。
    ///
    /// 平时只有文字和卡片浮在游戏上；鼠标移上来，半透明的背景和标题栏按钮才淡入（不透明度可在设置里调）。
    /// 整个窗口按住就能拖；点卡片（没拖动）打开那个来源的拆分窗口。
    /// </summary>
    internal sealed class OverlayView : Window
    {
        private const float CardW = 140f, CardGap = 6f, Pad = 10f;
        private const float HeaderH = 30f, BannerH = 46f;
        private const float NameH = 16f, BlockH = 22f, BarH = 3f, LineH = 13f;
        private const float CardH = NameH + 1f + BlockH + 3f + BarH + 2f + LineH * 2f;
        private const float MinW = 330f, BannerMinW = 470f;
        private const int MaxCards = 12;
        /// <summary>背景和按钮淡入 / 淡出用多久。</summary>
        private const float FadeSeconds = 0.15f;

        private TrackerView _view = TrackerView.Outgoing;

        /// <summary>玩家想不想看浮窗（F9）。收起时如果有严重的更新提示，只显示横幅。</summary>
        public bool Wanted = true;
        private bool _bannerOnly;

        private readonly Node _header, _buttons;
        private readonly CanvasGroup _buttonsGroup;
        private readonly Button _log, _viewBtn, _reset, _settings;
        private readonly Label _title, _empty;
        private readonly Shape _shapes;
        private readonly Card[] _cards = new Card[MaxCards];
        private readonly Banner _banner;
        private readonly Hit _drag;
        private List<SourceStats> _rows = new List<SourceStats>();

        /// <summary>0 = 背景和按钮全隐藏，1 = 全显示。</summary>
        private float _fade;
        private float _groupAlpha = -1f;
        private int _hoverCard = -1;

        private sealed class Card
        {
            public Node Root;
            public Label Name, Dps, Total, Crit, Pct, Max;
            public Hit Hit;
            public int SourceId;
            public string SourceName;
        }

        public OverlayView(Transform canvas) : base("overlay", canvas, MinW, 100f, Theme.OverlayBg)
        {
            RefreshInterval = 0.2f;

            // 整窗可拖：最先登记，按钮和卡片压在它上面
            _drag = AddHit(Body);
            _drag.DragAtOnce = true;
            _drag.Drag = (_, d) => MoveTo(Rect.x + d.x, Rect.y + d.y);
            _drag.DragEnd = EndDrag;

            _header = new Node("Header", Body);
            _title = new Label(_header, Theme.FontBody, Theme.Text, name: "Title").Rich().Outline();

            // 标题栏按钮放一组，整组跟着背景淡入淡出
            _buttons = new Node("Buttons", _header);
            try { _buttonsGroup = _buttons.Go.AddComponent<CanvasGroup>(); }
            catch (Exception e) { Mod.Log.Warning($"CanvasGroup 不可用，按钮改为直接显隐：{e.GetType().Name}"); }
            _log = new Button(this, _buttons, Glyphs.History, null, () => UiRoot.ToggleMain(),
                              tip: () => Strings.TipLog(Mod.Config.MainPanelKey.Value));
            _viewBtn = new Button(this, _buttons, (char)0, "", CycleView, ButtonStyle.Subtle, () => Strings.TipView);
            _reset = new Button(this, _buttons, Glyphs.Refresh, null, DamageTracker.ResetCurrent,
                                tip: () => Strings.TipReset(Mod.Config.ResetKey.Value));
            _settings = new Button(this, _buttons, Glyphs.Settings, null, UiRoot.OpenSettings, tip: () => Strings.TipSettings);

            _banner = new Banner(this, Body);

            _shapes = new Shape(Body, "Blocks");
            _empty = new Label(Body, Theme.FontBody, Theme.OverlayDim, TextAnchor.MiddleCenter, name: "Empty").Outline();
            for (var i = 0; i < MaxCards; i++) _cards[i] = MakeCard(i);

            ApplyFade();
        }

        public override Vector2 DefaultPosition() => new Vector2(20f, 20f);

        private Card MakeCard(int i)
        {
            var c = new Card { Root = new Node("Card" + i, Body) };
            c.Name = new Label(c.Root, Theme.FontSmall, Theme.Text, TextAnchor.MiddleCenter, bold: true, name: "Name").Outline();
            c.Dps = new Label(c.Root, Theme.FontBody, Color.white, bold: true, name: "Dps").NoWrap().Outline();
            c.Total = new Label(c.Root, Theme.FontBody, Color.white, TextAnchor.MiddleRight, bold: true, name: "Total").NoWrap().Outline();
            c.Crit = new Label(c.Root, Theme.FontTiny, Theme.OverlayDim, name: "Crit").NoWrap().Outline();
            c.Pct = new Label(c.Root, Theme.FontTiny, Color.white, TextAnchor.MiddleRight, name: "Pct").NoWrap().Outline();
            c.Max = new Label(c.Root, Theme.FontTiny, Theme.OverlayDim, TextAnchor.MiddleCenter, name: "Max").NoWrap().Outline();
            c.Hit = AddHit(c.Root);
            c.Hit.Click = () => UiRoot.Detail.Toggle(c.SourceId, _view, c.SourceName);
            c.Hit.Drag = (_, d) => MoveTo(Rect.x + d.x, Rect.y + d.y);   // 拖卡片也是拖窗口
            c.Hit.DragEnd = EndDrag;
            // 悬停时给斜切色块本身提亮（不另画一个矩形框，框和斜边对不上）
            c.Hit.State = s =>
            {
                var hot = s != HitState.Normal ? i : _hoverCard == i ? -1 : _hoverCard;
                if (hot == _hoverCard) return;
                _hoverCard = hot;
                Dirty = true;
            };
            c.Root.Active = false;
            return c;
        }

        private void CycleView()
        {
            // 输出 → 承伤 → 治疗 → 输出
            _view = _view == TrackerView.Outgoing ? TrackerView.Incoming
                  : _view == TrackerView.Incoming ? TrackerView.Healing
                  : TrackerView.Outgoing;
            Dirty = true;
        }

        /// <summary>每帧：玩家收起浮窗时，严重的更新提示仍要单独露出来。</summary>
        public void Sync()
        {
            var urgent = UpdateNotice.Visible && UpdateNotice.Urgent;
            var bannerOnly = !Wanted && urgent;
            if (bannerOnly != _bannerOnly)
            {
                _bannerOnly = bannerOnly;
                Dirty = true;
            }
            Show(Wanted || bannerOnly);
        }

        /// <summary>鼠标在浮窗上（或正拖着它、或在设置里预览）时淡入背景和按钮，否则淡出。</summary>
        public override void Tick(float dt)
        {
            // 设置里拖"窗口背景"滑杆时也亮出来，边拖边看效果
            var shown = UiRoot.HoveredWindow == this || UiRoot.PressedWindow == this || UiRoot.PreviewOpacity.HasValue;
            _fade = Mathf.MoveTowards(_fade, shown ? 1f : 0f, dt / FadeSeconds);
            ApplyFade();
        }

        private void ApplyFade()
        {
            FollowOpacity(Theme.OverlayBg);
            FrameAlpha = _fade;

            if (_buttonsGroup != null)
            {
                if (!Mathf.Approximately(_fade, _groupAlpha))
                {
                    _groupAlpha = _fade;
                    _buttonsGroup.alpha = _fade;
                }
            }
            else
            {
                _buttons.Active = _fade > 0.5f;
            }
        }

        public override void Refresh()
        {
            var bannerOn = UpdateNotice.Visible;
            _header.Active = !_bannerOnly;

            var enc = DamageTracker.Current;
            _rows = _bannerOnly ? new List<SourceStats>() : DamageTracker.Snapshot(_view);
            var n = Mathf.Min(_rows.Count, MaxCards);

            float skew;
            try { skew = Mathf.Clamp(Mod.Config.SkewDegrees.Value, -60f, 60f); }
            catch { skew = 0f; }
            var slope = MeshBuilder.Slope(skew);

            // 占比条和下面两行跟着斜度往一边错开：往哪边斜，哪边就多留这么宽，免得最后一张卡的字出了窗口
            var lean = Mathf.Ceil(Mathf.Abs(slope) * (BlockH * 0.5f + 3f + BarH * 0.5f));
            var padL = Pad + (slope < 0f ? lean : 0f);
            var padR = Pad + (slope > 0f ? lean : 0f);

            var width = _bannerOnly
                ? BannerMinW
                : Mathf.Max(padL + padR + Mathf.Max(1, n) * CardW + (Mathf.Max(1, n) - 1) * CardGap, bannerOn ? BannerMinW : MinW);
            var top = _bannerOnly ? 6f : HeaderH;
            var bannerTop = top;
            if (bannerOn) top += BannerH + 4f;
            var height = _bannerOnly ? BannerH + 12f : top + 4f + CardH + 10f;
            Resize(width, height);
            _drag.Rect = new Rect(0f, 0f, width, height);

            // ---- 横幅 ----
            _banner.Active = bannerOn;
            if (bannerOn) _banner.Refresh(Pad - 2f, bannerTop, width - Pad * 2f + 4f, BannerH);

            if (_bannerOnly)
            {
                _empty.Active = false;
                for (var i = 0; i < MaxCards; i++) _cards[i].Root.Active = false;
                _shapes.Begin(UiRoot.Scale);
                _shapes.Commit();
                return;
            }

            // ---- 标题栏 ----
            _header.Place(0f, 0f, width, HeaderH);
            _buttons.Place(0f, 0f, width, HeaderH);
            _log.Place(6f, 4f, 26f, 22f);
            var right = width - 6f;
            _settings.Place(right - 26f, 4f, 26f, 22f);
            right -= 28f;
            _reset.Place(right - 26f, 4f, 26f, 22f);
            right -= 30f;
            _viewBtn.SetText(Strings.ViewLabel(_view));
            var vw = Mathf.Max(44f, _viewBtn.Measure(8f));
            _viewBtn.Place(right - vw, 5f, vw, 20f);
            right -= vw + 6f;

            var total = enc.TotalOf(_view);
            var d = enc.DurationSeconds;
            _title.Text = $"<b>{Esc(Strings.EncounterTitle(enc))}</b>  <color=#D3D7DD>{Fmt.Dur(d)} · {Fmt.Short(total)} · " +
                          $"{Fmt.Short(d > 0d ? total / d : 0d)}/s</color>";
            _title.Place(36f, 0f, Mathf.Max(0f, right - 36f), HeaderH);

            // ---- 卡片 ----
            var b = _shapes.Begin(UiRoot.Scale);
            _shapes.Place(0f, 0f, width, height);
            var cardTop = top + 4f;

            _empty.Active = n == 0;
            if (n == 0)
            {
                _empty.Text = Strings.EmptyHint(_view);
                _empty.Place(Pad, cardTop, width - Pad * 2f, CardH);
            }

            var openId = UiRoot.Detail.Visible ? UiRoot.Detail.SourceId : int.MinValue;
            for (var i = 0; i < MaxCards; i++)
            {
                var c = _cards[i];
                if (i >= n)
                {
                    c.Root.Active = false;
                    continue;
                }
                var s = _rows[i];
                var x = padL + i * (CardW + CardGap);
                c.Root.Active = true;
                c.Root.Place(x, cardTop, CardW, CardH);
                c.Hit.Rect = new Rect(x - 3f, cardTop - 2f, CardW + 6f, CardH + 4f);
                c.SourceId = s.InstanceId;
                c.SourceName = Strings.SourceName(s);

                var share = total > 0d ? s.Total / total : 0d;
                var color = Theme.SourceColor(s);
                var hot = i == _hoverCard;

                c.Name.Text = c.SourceName;
                c.Name.Color = s.InstanceId == openId && UiRoot.Detail.View == _view ? Theme.Accent : Theme.Text;
                c.Name.Place(0f, 0f, CardW, NameH);

                // 色块和占比条用同一条斜切中线（色块的中线），两条斜边才在一条直线上
                var by = NameH + 1f;
                var blockY = cardTop + by;
                var center = blockY + BlockH * 0.5f;
                var fill = hot ? Theme.With(Color.Lerp(color, Color.white, 0.18f), 0.85f) : Theme.With(color, 0.55f);
                b.Parallelogram(x, blockY, CardW, BlockH, skew, new Color(0f, 0f, 0f, 0.30f), center);
                b.Parallelogram(x, blockY, CardW, BlockH, skew, fill, center);
                c.Dps.Text = Fmt.Short(s.Dps) + "/s";
                c.Dps.Place(9f, by, CardW * 0.55f, BlockH);
                c.Total.Text = Fmt.Short(s.Total);
                c.Total.Place(CardW * 0.45f - 9f, by, CardW * 0.55f, BlockH);

                var barY = by + BlockH + 3f;
                b.Parallelogram(x, cardTop + barY, CardW, BarH, skew, new Color(0f, 0f, 0f, 0.35f), center);
                b.Parallelogram(x, cardTop + barY, CardW * (float)share, BarH, skew, Theme.With(color, 0.85f), center);

                // 下面两行跟着占比条的斜度往右错开，和条的两端对齐：
                // 第一行左边暴击率（治疗是主要来源）、右边占比；第二行居中最大一击（治疗是次数）
                var dx = slope * (cardTop + barY + BarH * 0.5f - center);
                var lineY = barY + BarH + 2f;
                var heal = _view == TrackerView.Healing;
                var topKind = heal ? s.TopHealKind : -1;
                c.Crit.Text = heal ? (topKind >= 0 ? Healing.KindName(topKind) : "")
                                   : s.Hits > 0 ? Strings.CritRate(s.CritRate) : Strings.CritNone;
                c.Crit.Place(dx + 1f, lineY, CardW * 0.6f, LineH);
                c.Pct.Text = Fmt.Pct(share);
                c.Pct.Place(dx, lineY, CardW - 1f, LineH);
                c.Max.Text = heal ? Strings.HitsCount(s.Hits) : Strings.MaxHit(Fmt.Short(s.MaxHit));
                c.Max.Place(dx, lineY + LineH, CardW, LineH);
            }
            _shapes.Commit();
        }

        /// <summary>富文本里的 &lt; 会被当成标签，关卡名里万一有就换掉。</summary>
        private static string Esc(string s) => s?.Replace('<', '‹').Replace('>', '›');

        // ================================================================== 更新横幅

        private sealed class Banner
        {
            private readonly Box _bg, _bar;
            private readonly Label _icon, _title, _sub;
            private readonly Button _release, _update, _disable, _close;
            private readonly Node _root;

            public Banner(Window w, Node parent)
            {
                _root = new Node("Banner", parent);
                _bg = new Box(_root, Theme.Pane, Theme.RadiusControl, "Bg");
                _bar = new Box(_root, Theme.Warning, 1.5f, "Bar");
                _icon = new Label(_root, 15, Theme.Warning, TextAnchor.MiddleCenter, font: UiKit.IconFont ?? UiKit.TextFont, name: "Icon").NoWrap();
                _title = new Label(_root, Theme.FontSmall, Theme.Text, bold: true, name: "Title");
                _sub = new Label(_root, Theme.FontTiny, Theme.TextDim, name: "Sub");
                _release = new Button(w, _root, Glyphs.OpenInNew, null, UpdateNotice.OpenRelease, tip: () => Strings.BtnRelease);
                _update = new Button(w, _root, (char)0, "", UpdateNotice.Install, ButtonStyle.Accent);
                _disable = new Button(w, _root, (char)0, "", UpdateNotice.Disable, ButtonStyle.Subtle);
                _close = new Button(w, _root, Glyphs.Cancel, null, UpdateNotice.Dismiss, tip: () => Strings.TipDismiss);
            }

            public bool Active
            {
                set => _root.Active = value;
            }

            public void Refresh(float x, float y, float w, float h)
            {
                var c = UpdateNotice.Compose();
                var color = c.Level == UpdateNotice.Level.Error ? Theme.Danger
                          : c.Level == UpdateNotice.Level.Success ? Theme.Success
                          : c.Level == UpdateNotice.Level.Neutral ? Theme.TextDim
                          : Theme.Warning;
                _root.Place(x, y, w, h);
                _bg.Place(0f, 0f, w, h);
                _bg.Color = Color.Lerp(Theme.Pane, color, 0.16f);
                _bar.Place(0f, 6f, 3f, h - 12f);
                _bar.Color = color;
                _icon.Text = Glyphs.Of(c.Level == UpdateNotice.Level.Success ? Glyphs.Completed
                                     : c.Level == UpdateNotice.Level.Error ? Glyphs.Error : Glyphs.Warning);
                _icon.Color = color;
                _icon.Place(8f, 0f, 22f, h);

                // 按钮从右往左排：关闭、停用（被点名时）、更新、下载页
                var right = w - 6f;
                _close.Place(right - 24f, (h - 24f) * 0.5f, 24f, 24f);
                right -= 28f;

                _disable.Active = UpdateNotice.ShowDisable;
                if (UpdateNotice.ShowDisable)
                {
                    _disable.SetText(Strings.BtnDisable);
                    var bw = _disable.Measure(10f);
                    _disable.Place(right - bw, (h - 24f) * 0.5f, bw, 24f);
                    right -= bw + 6f;
                }

                _update.Active = UpdateNotice.ShowUpdate;
                if (UpdateNotice.ShowUpdate)
                {
                    _update.SetText(UpdateNotice.Installing ? Strings.BtnUpdating : Strings.BtnUpdate);
                    _update.Enabled = !UpdateNotice.Installing;
                    var bw = Mathf.Max(56f, _update.Measure(12f));
                    _update.Place(right - bw, (h - 24f) * 0.5f, bw, 24f);
                    right -= bw + 6f;
                }

                _release.Active = UpdateNotice.ShowRelease;
                if (UpdateNotice.ShowRelease)
                {
                    _release.Place(right - 24f, (h - 24f) * 0.5f, 24f, 24f);
                    right -= 30f;
                }

                _title.Text = c.Title;
                _title.Place(34f, 5f, Mathf.Max(0f, right - 34f), 18f);
                _sub.Text = c.Sub;
                _sub.Place(34f, 23f, Mathf.Max(0f, right - 34f), 16f);
            }
        }
    }
}
