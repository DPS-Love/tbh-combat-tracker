using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 折线图：每个来源一条"每秒数值"曲线，选中的那条加粗。不铺面积色——半透明的面积会盖住别的线，看不清。
    /// 网格走 <see cref="MeshBuilder"/>，线是抗锯齿的；数据点比像素多时按宽度重采样，长战斗也不会拖慢。
    /// 光标移上去有一条竖线和读数：有选中的看选中那条，没有就看这一秒最高的那条。
    /// </summary>
    internal sealed class LineChart
    {
        public struct Series
        {
            public float[] Values;
            public Color Color;
            public bool Bold;
            public string Name;
        }

        private const float PadL = 8f, PadR = 8f, PadT = 22f, PadB = 18f;

        private readonly Box _bg;
        private readonly Shape _shape;
        private readonly Label _peak, _t0, _t1, _readout;
        private readonly Box _cross, _dot;
        private readonly Hit _hit;
        private readonly Window _window;
        private readonly List<Vector2> _pts = new List<Vector2>(512);
        private readonly List<Series> _series = new List<Series>();

        private bool _hover;
        private int _sel = -1;
        private float _yMax;

        public LineChart(Window w, Node parent)
        {
            _window = w;
            _bg = new Box(parent, Theme.Inset, Theme.RadiusControl, "Chart");
            _shape = new Shape(_bg, "Lines");
            _peak = new Label(_bg, Theme.FontTiny, Theme.TextDim, name: "Peak").NoWrap();
            _t0 = new Label(_bg, Theme.FontTiny, Theme.TextFaint, name: "T0").NoWrap();
            _t1 = new Label(_bg, Theme.FontTiny, Theme.TextFaint, TextAnchor.MiddleRight, name: "T1").NoWrap();
            _cross = new Box(_bg, new Color(1f, 1f, 1f, 0.28f), 0f, "Cross");
            _dot = new Box(_bg, Color.white, 4f, "Dot");
            _readout = new Label(_bg, Theme.FontTiny, Theme.Text, TextAnchor.MiddleRight, name: "Readout").NoWrap();
            _cross.Active = _dot.Active = _readout.Active = false;
            _hit = w.AddHit(_bg);
            _hit.State = s =>
            {
                _hover = s != HitState.Normal;
                if (!_hover) _cross.Active = _dot.Active = _readout.Active = false;
            };
        }

        public bool Active
        {
            get => _bg.Active;
            set => _bg.Active = value;
        }

        public void Place(float x, float y, float w, float h)
        {
            _bg.Place(x, y, w, h);
            _shape.Place(0f, 0f, w, h);
            _peak.Place(PadL, 3f, w * 0.5f, 16f);
            _t0.Place(PadL, h - PadB + 1f, 80f, 16f);
            _t1.Place(w - PadR - 80f, h - PadB + 1f, 80f, 16f);
            _hit.Rect = _bg.InWindow;
        }

        /// <summary>重画。<paramref name="yMax"/> 是纵轴上限（通常是峰值 × 1.12）。</summary>
        public void Set(IList<Series> series, float yMax, string peak, double duration, float scale)
        {
            _peak.Text = peak;
            _t0.Text = "0:00";
            _t1.Text = Fmt.Dur(duration);
            _yMax = yMax > 0f ? yMax : 1f;
            _series.Clear();
            _series.AddRange(series);
            _sel = _series.FindIndex(s => s.Bold);

            var w = _bg.W;
            var h = _bg.H;
            var plotW = Mathf.Max(8f, w - PadL - PadR);
            var plotH = Mathf.Max(8f, h - PadT - PadB);
            var b = _shape.Begin(scale);

            // 网格：25% / 50% / 75%，外加一条零线
            for (var k = 1; k <= 3; k++) b.Rect(PadL, PadT + plotH * k / 4f, plotW, 1f, Theme.Divider);
            b.Rect(PadL, PadT + plotH, plotW, 1f, new Color(1f, 1f, 1f, 0.10f));

            // 先画普通的，选中的最后画、压在最上面
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var s in _series)
                {
                    if (s.Bold != (pass == 1) || s.Values == null || s.Values.Length == 0) continue;
                    Sample(s.Values, plotW, plotH);
                    if (s.Bold) b.Polyline(_pts, _pts.Count, 2.25f, s.Color);
                    else b.Polyline(_pts, _pts.Count, 1.25f, Theme.With(s.Color, _sel >= 0 ? 0.55f : 0.85f));
                }
            }
            _shape.Commit();
        }

        /// <summary>把数据点换成图上的坐标；点比像素多就按宽度重采样（每两个像素一个点）。</summary>
        private void Sample(float[] v, float plotW, float plotH)
        {
            _pts.Clear();
            var n = v.Length;
            var m = Mathf.Clamp(n, 2, Mathf.Max(2, (int)(plotW / 2f)));
            for (var j = 0; j < m; j++)
            {
                var pos = n == 1 ? 0f : j * (n - 1) / (float)(m - 1);
                var i0 = Mathf.Min((int)pos, n - 1);
                var i1 = Mathf.Min(i0 + 1, n - 1);
                var val = v[i0] + (v[i1] - v[i0]) * (pos - i0);
                var x = PadL + plotW * j / (m - 1);
                var y = PadT + plotH * (1f - Mathf.Clamp01(val / _yMax));
                _pts.Add(new Vector2(x, y));
            }
        }

        /// <summary>每帧：光标在图上时移动竖线、刷新读数。</summary>
        public void Tick()
        {
            if (!_hover || _series.Count == 0 || !_bg.Shown)
            {
                if (_cross.Active) _cross.Active = _dot.Active = _readout.Active = false;
                return;
            }

            var w = _bg.W;
            var h = _bg.H;
            var plotW = Mathf.Max(8f, w - PadL - PadR);
            var plotH = Mathf.Max(8f, h - PadT - PadB);
            var local = UiInput.Pos - _window.Rect.position - _bg.InWindow.position;
            var t = Mathf.Clamp01((local.x - PadL) / plotW);
            var n = 0;
            foreach (var s in _series) n = Math.Max(n, s.Values?.Length ?? 0);
            if (n == 0) return;
            var idx = Mathf.Clamp(Mathf.RoundToInt(t * (n - 1)), 0, n - 1);

            // 有选中的就读选中那条；没有就读这一秒最高的那条
            var pick = _sel;
            if (pick < 0)
            {
                var best = float.MinValue;
                for (var i = 0; i < _series.Count; i++)
                {
                    var v = _series[i].Values;
                    if (v == null || idx >= v.Length || v[idx] <= best) continue;
                    best = v[idx];
                    pick = i;
                }
            }
            if (pick < 0) return;

            var s0 = _series[pick];
            var val = idx < s0.Values.Length ? s0.Values[idx] : 0f;
            var x = PadL + plotW * (n > 1 ? idx / (float)(n - 1) : 0f);
            var y = PadT + plotH * (1f - Mathf.Clamp01(val / _yMax));

            _cross.Active = _dot.Active = _readout.Active = true;
            _cross.Place(Mathf.Round(x), PadT, 1f, plotH);
            _dot.Place(x - 4f, y - 4f, 8f, 8f);
            _dot.Color = s0.Color;
            // 每秒一个点：下标就是距本段开始的秒数
            _readout.Text = $"{Fmt.Dur(idx)}  {s0.Name}  {Fmt.Short(val)}/s";
            _readout.Place(w * 0.3f, 3f, w * 0.7f - PadR, 16f);
        }
    }

    /// <summary>
    /// 环形图：各段按占比、段间留缝，环心写总量和每秒。和旁边的拆分表 / 图例联动：
    /// 光标停在某一段上，<see cref="Hovered"/> 报出是哪一行（表里对应的行跟着高亮）；
    /// 表里某一行被指着时，调用方把 <c>highlight</c> 传进 <see cref="Set"/>，这一段往外凸出、别的变淡，环心改写这一项。
    /// 行是从大到小排的：图例一屏之外的、窄到画不出来的都在尾巴上，并成最后一段灰色的「其他」；
    /// 「其他」自己也画不出来时，这点角度并给最后一段——跳过不画的话，这些角度会在 12 点方向攒成一道时宽时窄的缝。
    /// 形状没变（按 0.1% 取整的各段和高亮项）就不重建网格。
    /// </summary>
    internal sealed class DonutChart
    {
        /// <summary>「其他」那一段的编号，用在 <see cref="Hovered"/> 和 <see cref="Set"/> 的 highlight 里。</summary>
        public const int OtherSlice = -2;

        private const float Gap = 2f;
        /// <summary>单独成段至少要在内圈留出的实色宽度（屏幕像素），不够就并进「其他」。</summary>
        private const float MinSolidPx = 1.5f;

        private readonly Window _window;
        private readonly Shape _shape;
        private readonly Label _big, _small;
        private readonly Hit _hit;
        private readonly int _maxSlices;
        // 画着的各段：终点（占整圈的比例）和对应的行号（「其他」是 OtherSlice）
        private readonly List<float> _ends = new List<float>();
        private readonly List<int> _ids = new List<int>();
        private string _signature;
        private float _size;
        private bool _hovering;

        /// <summary>光标停在哪一段上：行号，「其他」那段是 <see cref="OtherSlice"/>（-1 = 没有）。</summary>
        public int Hovered { get; private set; } = -1;

        /// <summary>光标换了一段：调用方据此重画对应的行。</summary>
        public Action HoverChanged;

        /// <summary>单独成段的行数：行号不小于它的行都算在「其他」里，图例上用灰点。</summary>
        public int Folded { get; private set; }

        /// <param name="maxSlices">最多几行单独成段：和拆分表 / 图例一屏的行数一致，往后的（要滚动才看得到）都进「其他」。</param>
        public DonutChart(Window w, Node parent, int maxSlices)
        {
            _window = w;
            _maxSlices = maxSlices;
            _shape = new Shape(parent, "Donut");
            _big = new Label(parent, 14, Theme.Text, TextAnchor.MiddleCenter, bold: true, name: "Total").NoWrap();
            _small = new Label(parent, Theme.FontTiny, Theme.TextDim, TextAnchor.MiddleCenter, name: "PerSec").NoWrap();
            _hit = w.AddHit(_shape);
            _hit.State = s =>
            {
                _hovering = s != HitState.Normal;
                if (!_hovering) SetHovered(-1);
            };
        }

        public bool Active
        {
            set
            {
                _shape.Active = value;
                _big.Active = value;
                _small.Active = value;
                if (!value) SetHovered(-1);
            }
        }

        public void Place(float x, float y, float size)
        {
            if (!Mathf.Approximately(size, _size)) _signature = null;
            _size = size;
            _shape.Place(x, y, size, size);
            _big.Place(x, y + size * 0.5f - 17f, size, 20f);
            _small.Place(x, y + size * 0.5f + 3f, size, 14f);
            _hit.Rect = _shape.InWindow;
        }

        /// <param name="highlight">要突出的行号或 <see cref="OtherSlice"/>（-1 = 不突出）：环心改写这一项的数值和占比。
        /// 并进「其他」的行突出的是「其他」那段。</param>
        public void Set(IList<BreakdownRow> rows, string big, string small, float scale, int highlight = -1)
        {
            double total = 0;
            foreach (var r in rows) total += r.Value;
            if (highlight >= rows.Count) highlight = -1;

            var c = _size * 0.5f;
            var outer = c - 4f;           // 留出凸出的余地
            var inner = outer * 0.64f;

            // 哪些行单独成段：图例列得下，而且突出时（内圈缩 1）扣掉缝还剩 MinSolidPx 的实色。
            // 行从大到小排，头一个不够的往后全都不够
            var px = 1f / Mathf.Max(0.25f, scale);   // 一个屏幕像素，同 Shape.Begin
            var minShare = (Gap + MinSolidPx * px) / Mathf.Max(1f, inner - 1f) / (Mathf.PI * 2f);
            var fold = 0;
            if (total > 0)
                while (fold < rows.Count && fold < _maxSlices && rows[fold].Value / total >= minShare) fold++;
            double rest = 0;
            for (var i = fold; i < rows.Count; i++) rest += rows[i].Value;
            var otherShown = rest > 0 && rest / total >= minShare;
            Folded = fold;
            if (highlight == OtherSlice && rest <= 0) highlight = -1;

            if (highlight == OtherSlice)
            {
                _big.Text = Fmt.Short(rest);
                _small.Text = Strings.Other + " " + Fmt.Pct(rest / total);
            }
            else if (highlight >= 0 && total > 0)
            {
                _big.Text = Fmt.Short(rows[highlight].Value);
                _small.Text = Fmt.Pct(rows[highlight].Value / total);
            }
            else
            {
                _big.Text = big;
                _small.Text = small;
            }

            _ends.Clear();
            _ids.Clear();
            double acc = 0;
            for (var i = 0; i < fold; i++)
            {
                acc += rows[i].Value;
                // 「其他」画不出来：最后一段直接接到 12 点，段间的缝都一样宽
                _ends.Add(i == fold - 1 && !otherShown ? 1f : (float)(acc / total));
                _ids.Add(i);
            }
            if (otherShown)
            {
                _ends.Add(1f);
                _ids.Add(OtherSlice);
            }

            // 突出哪一段：并进「其他」的行突出「其他」；「其他」没画出来就只是别的都变淡
            var hot = highlight >= fold ? OtherSlice : highlight;

            var sig = new System.Text.StringBuilder();
            sig.Append(scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(hot).Append('|');
            for (var k = 0; k < _ends.Count; k++)
                sig.Append(_ids[k]).Append(':').Append(Math.Round(_ends[k], 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            var s = sig.ToString();
            if (s == _signature) return;
            _signature = s;

            var b = _shape.Begin(scale);
            if (_ends.Count == 0)
            {
                b.Ring(c, c, inner, outer, 0f, Mathf.PI * 2f, new Color(1f, 1f, 1f, 0.08f));
            }
            else
            {
                var a0 = 0f;
                for (var k = 0; k < _ends.Count; k++)
                {
                    var id = _ids[k];
                    var a1 = _ends[k] * Mathf.PI * 2f;
                    var color = id == OtherSlice ? Theme.OtherSlice : Theme.PaletteAt(id);
                    if (hot != -1 && id != hot) color = Theme.With(color, 0.35f);
                    var on = id == hot;
                    b.Ring(c, c, on ? inner - 1f : inner, on ? outer + 3.5f : outer, a0, a1, color, Gap);
                    a0 = a1;
                }
            }
            _shape.Commit();
        }

        /// <summary>每帧：光标在环上时算出指着的是哪一段（按角度，12 点方向起顺时针）。</summary>
        public void Tick()
        {
            var idx = -1;
            if (_hovering && _shape.Shown && _ends.Count > 0)
            {
                var local = UiInput.Pos - _window.Rect.position - _shape.InWindow.position;
                var c = _size * 0.5f;
                var dx = local.x - c;
                var dy = local.y - c;
                var r = Mathf.Sqrt(dx * dx + dy * dy);
                var outer = c - 4f;
                var inner = outer * 0.64f;
                if (r >= inner - 3f && r <= outer + 6f)
                {
                    var a = Mathf.Atan2(dx, -dy);   // y 向下：12 点方向是 0，顺时针增大
                    if (a < 0f) a += Mathf.PI * 2f;
                    var t = a / (Mathf.PI * 2f);
                    for (var i = 0; i < _ends.Count; i++)
                    {
                        if (t > _ends[i]) continue;
                        idx = _ids[i];
                        break;
                    }
                }
            }
            SetHovered(idx);
        }

        private void SetHovered(int idx)
        {
            if (idx == Hovered) return;
            Hovered = idx;
            HoverChanged?.Invoke();
        }
    }
}
