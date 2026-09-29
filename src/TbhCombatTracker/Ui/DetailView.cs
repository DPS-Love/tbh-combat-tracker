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
        private readonly Label _title, _segment, _empty, _range;
        private readonly Button _close;
        private readonly Segmented _tabs, _healTabs;
        private static readonly Dimension[] DamageDims = { Dimension.Skill, Dimension.DamageType, Dimension.Attribute, Dimension.Target };
        private readonly DonutChart _donut;
        /// <summary>图例：一屏 LegendRows 行，多的用滚轮翻。行对象是个池，跟着滚动位置换绑。</summary>
        private readonly ListView _legendList;
        private readonly Legend[] _legend = new Legend[LegendRows + 1];

        private sealed class Legend
        {
            public Node Root;
            public Box Hover, Dot;
            public Label Name, Value, Pct;
            public Hit Hit;
            public int Item = -1;
        }

        /// <summary>光标指着的图例行对象（-1 = 没有）；它此刻绑的那一行和环形图上指着的那段互相高亮。</summary>
        private int _legendHoverSlot = -1;
        // 滚动时换绑行要用的：这次的行和合计、图例宽、环上单独成段的行数、高亮项，以及滚动位置归零的依据
        private List<BreakdownRow> _legendData = new List<BreakdownRow>();
        private double _legendSum;
        private float _legendW;
        private int _legendFolded, _legendHighlight = -1, _legendHoverRow = -1;
        private string _legendKey;

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
            _donut = new DonutChart(this, Body, LegendRows);
            _donut.HoverChanged = () => Dirty = true;
            _empty = new Label(Body, Theme.FontSmall, Theme.TextDim, name: "Empty");
            _range = new Label(Body, Theme.FontTiny, Theme.TextFaint, name: "Range").NoWrap();

            _legendList = new ListView(this, Body, LegendRow);
            for (var i = 0; i < _legend.Length; i++)
            {
                var slot = i;
                var l = new Legend { Root = new Node("Legend" + i, _legendList.Viewport) };
                l.Hover = new Box(l.Root, Theme.Hover, 4f, "Hover");
                l.Hover.Active = false;
                l.Dot = new Box(l.Root, Theme.PaletteAt(i), 3.5f, "Dot");
                l.Name = new Label(l.Root, Theme.FontSmall, Theme.Text, name: "Name");
                l.Value = new Label(l.Root, Theme.FontSmall, Theme.Text, TextAnchor.MiddleRight, name: "Value").NoWrap();
                l.Pct = new Label(l.Root, Theme.FontTiny, Theme.TextDim, TextAnchor.MiddleRight, name: "Pct").NoWrap();
                l.Hit = AddHit(l.Root);
                l.Hit.State = st =>
                {
                    var hot = st != HitState.Normal ? slot : _legendHoverSlot == slot ? -1 : _legendHoverSlot;
                    if (hot == _legendHoverSlot) return;
                    _legendHoverSlot = hot;
                    Dirty = true;
                };
                l.Root.Active = false;
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
            if (_legendList.Tick(dt)) BindLegend();
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
            // 图例右边留 8 给滚动条（在内容底板里面）
            var lx = Pad + Donut + 14f;
            var lw = W - lx - Pad - 8f;
            _empty.Active = stats == null;
            _donut.Active = stats != null;
            var rows = stats != null ? Breakdown.Of(stats, _dim, View) : new List<BreakdownRow>();
            if (stats == null)
            {
                _empty.Text = Strings.NoDataInSegment;
                _empty.Place(Pad, top, W - Pad * 2f, 20f);
                _legendHoverRow = _legendHighlight = -1;
            }
            else
            {
                // 图例行和环上的一段互相高亮：指着哪边都行。先定环，图例的色点跟着它分单独成段 / 「其他」
                _legendHoverRow = HoveredLegendRow();
                var highlight = _legendHoverRow >= 0 && _legendHoverRow < rows.Count ? _legendHoverRow : _donut.Hovered;
                _donut.Place(Pad, top, Donut);
                _donut.Set(rows, Fmt.Short(stats.Total), Fmt.Short(stats.Dps) + "/s", UiRoot.Scale, highlight);
                _legendFolded = _donut.Folded;
                _legendHighlight = highlight;
                if (rows.Count == 0)
                {
                    _empty.Active = true;
                    _empty.Text = Strings.NoBreakdown;
                    _empty.Place(lx, top, lw, 20f);
                }
            }

            double sum = 0;
            foreach (var r in rows) sum += r.Value;
            _legendData = rows;
            _legendSum = sum;
            _legendW = lw;

            // 一屏 LegendRows 行，多的用滚轮翻；换了段 / 人 / 视图 / 维度就回到顶上
            var key = $"{enc.Index}|{enc.StartTime}|{SourceId}|{(int)View}|{(int)_dim}";
            _legendList.Place(lx - 4f, top + 2f, lw + 14f, Mathf.Min(rows.Count, LegendRows) * LegendRow);
            _legendList.Count = rows.Count;
            if (key != _legendKey)
            {
                _legendKey = key;
                _legendList.ScrollToTop();
            }
            _range.Active = rows.Count > LegendRows;
            if (_range.Active) _range.Place(lx + 12f, top + 2f + LegendRows * LegendRow, lw - 12f, LegendRow);
            BindLegend();
        }

        private int HoveredLegendRow()
            => _legendHoverSlot >= 0 && _legendHoverSlot < _legend.Length && _legend[_legendHoverSlot].Root.Active
                ? _legend[_legendHoverSlot].Item
                : -1;

        /// <summary>把图例行摆到当前滚动位置、填上内容。滚动动画每帧都会调它。</summary>
        private void BindLegend()
        {
            var rows = _legendData;
            var lw = _legendW;
            var vr = _legendList.Viewport.InWindow;
            var otherHot = _legendHighlight == DonutChart.OtherSlice;
            for (var slot = 0; slot < _legend.Length; slot++)
            {
                var l = _legend[slot];
                var i = _legendList.ItemAt(slot, out var y);
                if (slot >= _legendList.PoolSize || i >= rows.Count)
                {
                    l.Root.Active = false;
                    l.Item = -1;
                    continue;
                }
                var r = rows[i];
                l.Item = i;
                l.Root.Active = true;
                l.Root.Place(4f, y, lw, LegendRow);
                // 被视口裁掉的那部分不该还能指
                var rr = l.Root.InWindow;
                var top = Mathf.Max(rr.y, vr.y);
                var bottom = Mathf.Min(rr.yMax, vr.yMax);
                l.Hit.Rect = new Rect(rr.x, top, rr.width, Mathf.Max(0f, bottom - top));
                l.Hover.Active = i == _legendHighlight || (otherHot && i >= _legendFolded);
                l.Hover.Place(-4f, 0f, lw + 6f, LegendRow);
                l.Dot.Place(0f, LegendRow * 0.5f - 3.5f, 7f, 7f);
                l.Dot.Color = i < _legendFolded ? Theme.PaletteAt(i) : Theme.OtherSlice;
                l.Name.Text = r.Label;
                l.Name.Place(12f, 0f, lw - 12f - 96f, LegendRow);
                l.Value.Text = Fmt.Short(r.Value);
                l.Value.Place(lw - 96f, 0f, 50f, LegendRow);
                l.Pct.Text = Fmt.Pct(_legendSum > 0 ? r.Value / _legendSum : 0d);
                l.Pct.Place(lw - 44f, 0f, 44f, LegendRow);
            }
            _legendList.PaintThumb();
            if (_range.Active)
            {
                _legendList.VisibleRange(out var from, out var to);
                _range.Text = $"{from}–{to} / {rows.Count}";
            }
            // 滚动时光标下换了一行：重排一次，环上跟着突出它
            if (HoveredLegendRow() != _legendHoverRow) Dirty = true;
        }
    }
}
