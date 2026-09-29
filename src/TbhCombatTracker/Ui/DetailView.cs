using System.Collections.Generic;
using UnityEngine;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 点浮窗卡片弹出的拆分窗口：一个来源在当前段里的构成，环形图 + 图例。
    /// 输出 / 承伤按技能、伤害类型、元素拆；治疗只有恢复来源一个维度。
    /// 跟着实时的当前段走：刚切段、这个角色还没出手时显示空状态，不自动关——几秒后数据就回来了。
    /// </summary>
    internal sealed class DetailView : Window
    {
        private const float W = 372f, H = 214f, Pad = 12f, Donut = 118f, LegendRow = 16f;
        private const int LegendRows = 7;

        public int SourceId { get; private set; }
        public TrackerView View { get; private set; }
        private string _name;
        private Dimension _dim = Dimension.Skill;

        private readonly Box _dot;
        private readonly Label _title, _segment, _empty, _more;
        private readonly Button _close;
        private readonly Segmented _tabs, _healTabs;
        private static readonly Dimension[] DamageDims = { Dimension.Skill, Dimension.DamageType, Dimension.Attribute, Dimension.Target };
        private readonly DonutChart _donut;
        private readonly Legend[] _legend = new Legend[LegendRows];

        private sealed class Legend
        {
            public Node Root;
            public Box Hover, Dot;
            public Label Name, Value, Pct;
            public Hit Hit;
        }

        /// <summary>光标指着的图例行（-1 = 没有）；和环形图上指着的那段互相高亮。</summary>
        private int _legendHover = -1;

        public DetailView(Transform canvas) : base("detail", canvas, W, H, Theme.WindowBg)
        {
            RefreshInterval = 0.25f;
            DragHandle(Body, new Rect(0f, 0f, W, TitleH));

            // 内容区垫一层半透明的黑：窗口背景调得很透明时，环形图和图例还有衬底
            var contentBg = new Box(Body, Theme.Panel, 6f, "ContentBg");
            contentBg.Place(6f, TitleH, W - 12f, H - TitleH - 6f);

            _dot = new Box(Body, Color.white, 4f, "Dot");
            _title = new Label(Body, Theme.FontBody, Theme.Text, bold: true, name: "Title");
            _close = new Button(this, Body, Glyphs.Cancel, null, () => Show(false), tip: () => Strings.TipClose);
            _segment = new Label(Body, Theme.FontSmall, Theme.TextDim, name: "Segment");
            _tabs = new Segmented(this, Body, DamageDims.Length, i =>
            {
                _dim = DamageDims[i];
                Dirty = true;
            });
            _healTabs = new Segmented(this, Body, 2, i =>
            {
                _dim = i == 0 ? Dimension.HealKind : Dimension.Target;
                Dirty = true;
            });
            _donut = new DonutChart(this, Body);
            _donut.HoverChanged = () => Dirty = true;
            _empty = new Label(Body, Theme.FontSmall, Theme.TextDim, name: "Empty");
            _more = new Label(Body, Theme.FontTiny, Theme.TextFaint, name: "More");

            for (var i = 0; i < LegendRows; i++)
            {
                var slot = i;
                var l = new Legend { Root = new Node("Legend" + i, Body) };
                l.Hover = new Box(l.Root, Theme.Hover, 4f, "Hover");
                l.Hover.Active = false;
                l.Dot = new Box(l.Root, Theme.PaletteAt(i), 3.5f, "Dot");
                l.Name = new Label(l.Root, Theme.FontSmall, Theme.Text, name: "Name");
                l.Value = new Label(l.Root, Theme.FontSmall, Theme.Text, TextAnchor.MiddleRight, name: "Value").NoWrap();
                l.Pct = new Label(l.Root, Theme.FontTiny, Theme.TextDim, TextAnchor.MiddleRight, name: "Pct").NoWrap();
                l.Hit = AddHit(l.Root);
                l.Hit.State = st =>
                {
                    var hot = st != HitState.Normal ? slot : _legendHover == slot ? -1 : _legendHover;
                    if (hot == _legendHover) return;
                    _legendHover = hot;
                    Dirty = true;
                };
                _legend[i] = l;
            }
        }

        /// <summary>打开某个来源；再点一次同一张卡片就关掉，符合直觉。</summary>
        public void Toggle(int sourceId, TrackerView view, string name)
        {
            if (Visible && SourceId == sourceId && View == view)
            {
                Show(false);
                return;
            }
            SourceId = sourceId;
            View = view;
            _name = name;
            _dim = view == TrackerView.Healing ? Dimension.HealKind : Dimension.Skill;
            _tabs.Select(0);
            _healTabs.Select(0);
            if (!Visible && !Placed)
            {
                // 没存过位置：放在浮窗正下方；玩家拖过之后就一直在他拖到的地方
                var o = UiRoot.Overlay.Rect;
                MoveTo(o.x, o.yMax + 8f);
            }
            Show(true);
            UiRoot.ToFront(this);
            Dirty = true;
            UiRoot.Overlay.Dirty = true;   // 浮窗上被选中的名字变色
        }

        public override void Show(bool on)
        {
            var was = Visible;
            base.Show(on);
            if (was && !on && UiRoot.Overlay != null) UiRoot.Overlay.Dirty = true;
        }

        public override void Tick(float dt)
        {
            FollowOpacity(Theme.WindowBg);
            _donut.Tick();
        }

        public override void Refresh()
        {
            var enc = DamageTracker.Current;
            SourceStats stats = null;
            enc.Bucket(View).TryGetValue(SourceId, out stats);
            if (stats != null) _name = Strings.SourceName(stats);

            _dot.Place(Pad, TitleH * 0.5f - 4f, 8f, 8f);
            _dot.Color = Theme.SourceColor(stats);
            _title.Text = Strings.DetailTitle(_name ?? "?", Strings.ViewLabel(View));
            _title.Place(Pad + 14f, 0f, W - Pad - 14f - 40f, TitleH);
            _close.Place(W - 34f, 4f, 26f, 24f);

            // 第二行：当前段 + 维度
            var y = TitleH + 2f;
            _segment.Text = Strings.EncounterTitle(enc);
            var heal = View == TrackerView.Healing;
            _tabs.Active = !heal;
            _healTabs.Active = heal;
            if (heal)
            {
                _healTabs.SetLabels(Strings.TabHealSources, Strings.TabTargets(View));
                var tw = _healTabs.Place(W - Pad - 2f * 64f - 4f, y, 64f, 24f);
                _segment.Place(Pad, y, W - Pad * 2f - tw - 6f, 24f);
            }
            else
            {
                _tabs.SetLabels(Strings.TabSkills, Strings.TabTypes, Strings.TabElements, Strings.TabTargets(View));
                var tw = _tabs.Place(W - Pad - 4f * 50f - 4f, y, 50f, 24f);
                _segment.Place(Pad, y, W - Pad * 2f - tw - 6f, 24f);
            }

            var top = y + 32f;
            _empty.Active = stats == null;
            _donut.Active = stats != null;
            if (stats == null)
            {
                _empty.Text = Strings.NoDataInSegment;
                _empty.Place(Pad, top, W - Pad * 2f, 20f);
                foreach (var l in _legend) l.Root.Active = false;
                _more.Active = false;
                return;
            }

            var rows = Breakdown.Of(stats, _dim, View);
            double sum = 0;
            foreach (var r in rows) sum += r.Value;

            // 图例行和环上的一段互相高亮：指着哪边都行
            var highlight = _legendHover >= 0 && _legendHover < rows.Count ? _legendHover : _donut.Hovered;
            _donut.Place(Pad, top, Donut);
            _donut.Set(rows, Fmt.Short(stats.Total), Fmt.Short(stats.Dps) + "/s", UiRoot.Scale, highlight);

            var lx = Pad + Donut + 14f;
            var lw = W - lx - Pad;
            if (rows.Count == 0)
            {
                _empty.Active = true;
                _empty.Text = Strings.NoBreakdown;
                _empty.Place(lx, top, lw, 20f);
            }
            for (var i = 0; i < LegendRows; i++)
            {
                var l = _legend[i];
                if (i >= rows.Count)
                {
                    l.Root.Active = false;
                    continue;
                }
                var r = rows[i];
                l.Root.Active = true;
                l.Root.Place(lx, top + 2f + i * LegendRow, lw, LegendRow);
                l.Hit.Rect = l.Root.InWindow;
                l.Hover.Active = i == highlight;
                l.Hover.Place(-4f, 0f, lw + 6f, LegendRow);
                l.Dot.Place(0f, LegendRow * 0.5f - 3.5f, 7f, 7f);
                l.Dot.Color = Theme.PaletteAt(i);
                l.Name.Text = r.Label;
                l.Name.Place(12f, 0f, lw - 12f - 96f, LegendRow);
                l.Value.Text = Fmt.Short(r.Value);
                l.Value.Place(lw - 96f, 0f, 50f, LegendRow);
                l.Pct.Text = Fmt.Pct(sum > 0 ? r.Value / sum : 0d);
                l.Pct.Place(lw - 44f, 0f, 44f, LegendRow);
            }
            _more.Active = rows.Count > LegendRows;
            if (rows.Count > LegendRows)
            {
                _more.Text = Strings.MoreItems(rows.Count - LegendRows);
                _more.Place(lx + 12f, top + 2f + LegendRows * LegendRow, lw - 12f, LegendRow);
            }
        }
    }
}
