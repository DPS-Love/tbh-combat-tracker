using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhCombatTracker.Ui
{
    internal enum ButtonStyle
    {
        /// <summary>平时透明，悬停才显出底色（工具栏、标题栏上的图标按钮）。</summary>
        Ghost,
        /// <summary>一直有一层淡底色（设置里的按钮、翻页）。</summary>
        Subtle,
        /// <summary>页签 / 分段：选中的那个是强调色底。</summary>
        Tab,
        /// <summary>强调色实心（横幅上的「更新」）。</summary>
        Accent,
    }

    /// <summary>按钮：图标、文字或两者都有。</summary>
    internal sealed class Button
    {
        public readonly Box Bg;
        public readonly Label Icon;
        public readonly Label Text;
        public readonly Hit Hit;

        private readonly ButtonStyle _style;
        private HitState _state;
        private bool _on;
        private bool _enabled = true;

        public Button(Window w, Node parent, char glyph, string text, Action click,
                      ButtonStyle style = ButtonStyle.Ghost, Func<string> tip = null)
        {
            _style = style;
            Bg = new Box(parent, Color.clear, Theme.RadiusControl, "Button");
            if (glyph != 0)
            {
                Icon = new Label(Bg, 13, Theme.TextDim, TextAnchor.MiddleCenter, font: UiKit.IconFont ?? UiKit.TextFont, name: "Icon").NoWrap();
                Icon.Text = Glyphs.Of(glyph);
            }
            if (text != null)
            {
                Text = new Label(Bg, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleCenter, name: "Text").NoWrap();
                Text.Text = text;
            }
            Hit = w.AddHit(Bg);
            Hit.Click = click;
            Hit.Tip = tip;
            Hit.State = s =>
            {
                _state = s;
                Paint();
            };
            Paint();
        }

        public bool On
        {
            get => _on;
            set
            {
                if (_on == value) return;
                _on = value;
                Paint();
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Hit.Enabled = value;
                Paint();
            }
        }

        public bool Active
        {
            get => Bg.Active;
            set => Bg.Active = value;
        }

        public void SetText(string s)
        {
            if (Text != null) Text.Text = s;
        }

        public void SetGlyph(char glyph)
        {
            if (Icon != null) Icon.Text = Glyphs.Of(glyph);
        }

        /// <summary>按内容算宽度：左右各留 <paramref name="pad"/>。</summary>
        public float Measure(float pad = 9f)
        {
            var w = pad * 2f;
            if (Icon != null) w += 16f;
            if (Text != null) w += Mathf.Ceil(Text.PreferredWidth) + (Icon != null ? 5f : 0f);
            return w;
        }

        public void Place(float x, float y, float w, float h)
        {
            Bg.Place(x, y, w, h);
            Hit.Rect = Bg.InWindow;
            if (Icon != null && Text != null)
            {
                Icon.Place(8f, 0f, 16f, h);
                Text.Align = TextAnchor.MiddleLeft;
                Text.Place(29f, 0f, Mathf.Max(0f, w - 33f), h);
            }
            else
            {
                Icon?.Place(0f, 0f, w, h);
                Text?.Place(0f, 0f, w, h);
            }
        }

        private void Paint()
        {
            var hover = _enabled && _state == HitState.Hover;
            var pressed = _enabled && _state == HitState.Pressed;
            Color bg, fg;
            switch (_style)
            {
                case ButtonStyle.Subtle:
                    bg = new Color(1f, 1f, 1f, pressed ? 0.14f : hover ? 0.10f : 0.055f);
                    fg = Theme.Text;
                    break;
                case ButtonStyle.Tab:
                    bg = _on ? Theme.AccentSoft : pressed ? Theme.Pressed : hover ? Theme.Hover : Color.clear;
                    fg = _on || hover ? Theme.Text : Theme.TextDim;
                    break;
                case ButtonStyle.Accent:
                    bg = pressed ? Theme.With(Theme.Accent, 0.75f) : hover ? Theme.With(Theme.Accent, 0.9f) : Theme.Accent;
                    fg = Color.white;
                    break;
                default:
                    bg = _on ? Theme.AccentSoft : pressed ? Theme.Pressed : hover ? Theme.Hover : Color.clear;
                    fg = _on || hover || pressed ? Theme.Text : Theme.TextDim;
                    break;
            }
            if (!_enabled)
            {
                bg = _style == ButtonStyle.Accent ? Theme.With(Theme.Accent, 0.35f) : _style == ButtonStyle.Subtle ? new Color(1f, 1f, 1f, 0.04f) : Color.clear;
                fg = Theme.TextFaint;
            }
            Bg.Color = bg;
            if (Icon != null) Icon.Color = fg;
            if (Text != null) Text.Color = fg;
        }
    }

    /// <summary>分段选择（输出 / 承伤 / 治疗、技能 / 类型 / 元素）。</summary>
    internal sealed class Segmented
    {
        private readonly Box _bg;
        private readonly List<Button> _items = new List<Button>();
        private readonly Action<int> _changed;
        private int _selected;

        public Segmented(Window w, Node parent, int count, Action<int> changed)
        {
            _changed = changed;
            _bg = new Box(parent, new Color(1f, 1f, 1f, 0.05f), Theme.RadiusControl, "Segmented");
            for (var i = 0; i < count; i++)
            {
                var index = i;
                _items.Add(new Button(w, _bg, (char)0, "", () => Select(index, true), ButtonStyle.Tab));
            }
            _items[0].On = true;
        }

        public bool Active
        {
            get => _bg.Active;
            set => _bg.Active = value;
        }

        public void SetLabels(params string[] labels)
        {
            for (var i = 0; i < _items.Count && i < labels.Length; i++) _items[i].SetText(labels[i]);
        }

        public void Select(int index, bool notify = false)
        {
            if (index < 0 || index >= _items.Count) return;
            var changed = index != _selected;
            _selected = index;
            for (var i = 0; i < _items.Count; i++) _items[i].On = i == index;
            if (notify && changed) _changed?.Invoke(index);
        }

        /// <summary>每段等宽；返回总宽度。</summary>
        public float Place(float x, float y, float segW, float h)
        {
            var n = _items.Count;
            _bg.Place(x, y, segW * n + 4f, h);
            for (var i = 0; i < n; i++) _items[i].Place(2f + i * segW, 2f, segW, h - 4f);
            return segW * n + 4f;
        }
    }

    /// <summary>开关。</summary>
    internal sealed class Toggle
    {
        public const float W = 34f, H = 18f;

        private readonly Box _track, _knob;
        private readonly Hit _hit;
        private readonly Action<bool> _changed;
        private bool _on;
        private HitState _state;

        public Toggle(Window w, Node parent, Action<bool> changed)
        {
            _changed = changed;
            _track = new Box(parent, Color.gray, H * 0.5f, "Toggle");
            _knob = new Box(_track, Color.white, 7f, "Knob");
            _hit = w.AddHit(_track);
            _hit.Click = () =>
            {
                On = !_on;
                _changed?.Invoke(_on);
            };
            _hit.State = s =>
            {
                _state = s;
                Paint();
            };
            Paint();
        }

        public bool On
        {
            get => _on;
            set
            {
                if (_on == value) return;
                _on = value;
                Paint();
            }
        }

        public void Place(float x, float y)
        {
            _track.Place(x, y, W, H);
            // 命中区比开关本身大一圈，好点
            var r = _track.InWindow;
            _hit.Rect = new Rect(r.x - 4f, r.y - 4f, r.width + 8f, r.height + 8f);
            Paint();
        }

        private void Paint()
        {
            var hover = _state != HitState.Normal;
            _track.Color = _on ? (hover ? Theme.With(Theme.Accent, 0.88f) : Theme.Accent)
                               : new Color(1f, 1f, 1f, hover ? 0.24f : 0.18f);
            _knob.Place(_on ? W - 16f : 2f, 2f, 14f, 14f);
        }
    }

    /// <summary>
    /// 滑杆。拖动时只预览（数值跟着变），松手才提交——界面缩放要是边拖边生效，
    /// 滑杆会从光标底下跑掉；配置也不必每一帧都写一遍文件。
    /// </summary>
    internal sealed class Slider
    {
        private readonly Box _track, _fill, _thumb;
        private readonly Label _value;
        private readonly Hit _hit;
        private readonly float _min, _max, _step;
        private readonly Func<float, string> _format;
        private readonly Action<float> _commit;
        private float _v;
        private bool _dragging;
        private HitState _state;
        private float _x, _w;
        private float _thumbD;

        /// <summary>
        /// 悬停 / 拖动时的实时值，移开、松手后给 null。设置里的"浮窗背景"用它让浮窗边拖边预览——
        /// 值只在松手时提交，但玩家得边拖边看到效果。
        /// </summary>
        public Action<float?> Preview;

        public Slider(Window w, Node parent, float min, float max, float step, Func<float, string> format, Action<float> commit)
        {
            _min = min;
            _max = max;
            _step = step;
            _format = format;
            _commit = commit;
            _track = new Box(parent, new Color(1f, 1f, 1f, 0.14f), 2f, "Slider");
            _fill = new Box(_track, Theme.Accent, 2f, "Fill");
            _thumb = new Box(parent, Color.white, 7f, "Thumb");
            _value = new Label(parent, Theme.FontSmall, Theme.Text, TextAnchor.MiddleRight, name: "Value").NoWrap();

            _hit = w.AddHit(_track);
            _hit.DragAtOnce = true;
            _hit.Press = p => SetFromX(p.x);
            _hit.Drag = (p, _) => SetFromX(p.x);
            _hit.DragEnd = Commit;
            _hit.Click = Commit;
            _hit.State = s =>
            {
                _state = s;
                Paint();
                if (s != HitState.Normal) Preview?.Invoke(_v);
                else if (!_dragging) Preview?.Invoke(null);
            };
        }

        public float Value
        {
            get => _v;
            set
            {
                if (_dragging) return;   // 拖着的时候不让外面的刷新把它拽回去
                _v = Snap(value);
                Paint();
            }
        }

        private float Snap(float v)
        {
            v = Mathf.Clamp(v, _min, _max);
            if (_step > 0f) v = _min + Mathf.Round((v - _min) / _step) * _step;
            return Mathf.Clamp(v, _min, _max);
        }

        private void SetFromX(float windowX)
        {
            _dragging = true;
            var t = _w > 0f ? Mathf.Clamp01((windowX - _x) / _w) : 0f;
            _v = Snap(_min + (_max - _min) * t);
            Paint();
            Preview?.Invoke(_v);
        }

        private void Commit()
        {
            if (!_dragging) return;
            _dragging = false;
            _commit?.Invoke(_v);
            if (_state == HitState.Normal) Preview?.Invoke(null);
        }

        /// <summary>滑杆轨道 (x, y) 起、宽 w；数值标签放在轨道右边 56 宽的地方。</summary>
        public void Place(float x, float y, float w)
        {
            _track.Place(x, y + 7f, w, 4f);
            _value.Place(x + w + 8f, y, 56f, 18f);
            var r = _track.InWindow;
            _x = r.x;
            _w = r.width;
            _hit.Rect = new Rect(r.x - 8f, r.y - 8f, r.width + 16f, 20f);
            Paint();
        }

        private void Paint()
        {
            var t = _max > _min ? (_v - _min) / (_max - _min) : 0f;
            var w = _track.W;
            _fill.Place(0f, 0f, Mathf.Max(4f, w * t), 4f);
            var big = _dragging || _state != HitState.Normal;
            var d = big ? 16f : 14f;
            _thumb.Place(_track.X + w * t - d * 0.5f, _track.Y + 2f - d * 0.5f, d, d);
            if (d != _thumbD)
            {
                _thumbD = d;
                _thumb.Radius(d * 0.5f);
            }
            _value.Text = _format(_v);
        }
    }

    /// <summary>颜色块：点一下选这个颜色；<see cref="Selected"/> 时外面描一圈。</summary>
    internal sealed class Swatch
    {
        private readonly Box _ring, _fill;
        private readonly Hit _hit;
        private bool _selected;
        private HitState _state;

        public Swatch(Window w, Node parent, Action click)
        {
            _ring = new Box(parent, Color.clear, 5f, "Swatch");
            _fill = new Box(_ring, Color.white, 3.5f, "Fill");
            _hit = w.AddHit(_ring);
            _hit.Click = click;
            _hit.State = s =>
            {
                _state = s;
                Paint();
            };
        }

        public Color Color
        {
            set => _fill.Color = value;
        }

        public bool Selected
        {
            set
            {
                if (_selected == value) return;
                _selected = value;
                Paint();
            }
        }

        public Func<string> Tip
        {
            set => _hit.Tip = value;
        }

        public void Place(float x, float y, float size)
        {
            _ring.Place(x, y, size, size);
            _fill.Place(2f, 2f, size - 4f, size - 4f);
            _hit.Rect = _ring.InWindow;
            Paint();
        }

        private void Paint()
        {
            _ring.Color = _selected ? Color.white : _state != HitState.Normal ? new Color(1f, 1f, 1f, 0.35f) : Color.clear;
        }
    }

    /// <summary>
    /// 虚拟化的纵向列表：行对象只建看得见的那么多，滚动时换绑定。滚动是平滑的（滚轮给目标位置，
    /// 每帧往目标靠），被 RectMask2D 裁掉的半行照样画一半。右边一条细滚动条，可拖。
    /// </summary>
    internal sealed class ListView
    {
        public readonly Node Viewport;
        private readonly Box _thumb;
        private readonly Hit _wheel, _thumbHit;
        private HitState _thumbState;

        public float RowH;
        private int _count;
        private float _offset, _target;

        public ListView(Window w, Node parent, float rowH)
        {
            RowH = rowH;
            Viewport = new Node("Viewport", parent);
            Viewport.Go.AddComponent<UnityEngine.UI.RectMask2D>();
            _wheel = w.AddHit(Viewport);
            _wheel.Wheel = d => ScrollBy(-d * 3f * RowH);
            _thumb = new Box(parent, new Color(1f, 1f, 1f, 0.16f), 2f, "Scrollbar");
            _thumbHit = w.AddHit(_thumb);
            _thumbHit.DragAtOnce = true;
            _thumbHit.Drag = (_, d) => DragThumb(d.y);
            _thumbHit.State = s =>
            {
                _thumbState = s;
                PaintThumb();
            };
        }

        public int Count
        {
            get => _count;
            set
            {
                _count = Mathf.Max(0, value);
                _target = Mathf.Clamp(_target, 0f, MaxOffset);
                _offset = Mathf.Clamp(_offset, 0f, MaxOffset);
            }
        }

        public float Offset => _offset;
        public float Height => Viewport.H;
        public float MaxOffset => Mathf.Max(0f, _count * RowH - Viewport.H);

        /// <summary>第一行（可能只露出一半）的下标。</summary>
        public int First => RowH > 0f ? Mathf.FloorToInt(_offset / RowH) : 0;

        /// <summary>至少要这么多个行对象才铺得满。</summary>
        public int PoolSize => Mathf.CeilToInt(Viewport.H / RowH) + 1;

        /// <summary>完整露出来的第一行和最后一行（从 1 数），底栏的 "a–b / N" 用。</summary>
        public void VisibleRange(out int from, out int to)
        {
            if (_count == 0)
            {
                from = to = 0;
                return;
            }
            from = Mathf.Clamp(Mathf.CeilToInt(_offset / RowH - 0.35f), 0, _count - 1) + 1;
            to = Mathf.Clamp(Mathf.FloorToInt((_offset + Viewport.H) / RowH + 0.35f), from, _count);
        }

        public void ScrollBy(float px) => _target = Mathf.Clamp(_target + px, 0f, MaxOffset);

        public void Page(int dir) => ScrollBy(dir * Mathf.Max(RowH, Mathf.Floor(Viewport.H / RowH) * RowH));

        public void ScrollToTop()
        {
            _target = 0f;
            _offset = 0f;
        }

        /// <summary>滚到最底（逐条事件跟着最新的走）。</summary>
        public void ScrollToEnd()
        {
            _target = MaxOffset;
            _offset = _target;
        }

        /// <summary>已经在最底（容差半行）：新内容进来时要不要跟着往下滚。</summary>
        public bool AtEnd => _offset >= MaxOffset - RowH * 0.5f;

        private void DragThumb(float dy)
        {
            var track = Viewport.H;
            var content = _count * RowH;
            if (content <= track) return;
            var thumbH = Mathf.Max(24f, track * track / content);
            var px = dy * (content - track) / Mathf.Max(1f, track - thumbH);
            _target = Mathf.Clamp(_target + px, 0f, MaxOffset);
            _offset = _target;
        }

        /// <summary>每帧调：往目标位置靠。位置变了返回 true（调用方要重排行）。</summary>
        public bool Tick(float dt)
        {
            if (Mathf.Abs(_target - _offset) < 0.25f)
            {
                if (_offset == _target) return false;
                _offset = _target;
                return true;
            }
            _offset = Mathf.Lerp(_offset, _target, 1f - Mathf.Exp(-dt * 18f));
            return true;
        }

        public void Place(float x, float y, float w, float h)
        {
            Viewport.Place(x, y, w, h);
            _wheel.Rect = Viewport.InWindow;
            _target = Mathf.Clamp(_target, 0f, MaxOffset);
            _offset = Mathf.Clamp(_offset, 0f, MaxOffset);
            PaintThumb();
        }

        /// <summary>第 <paramref name="slot"/> 个行对象此刻该放的 y（视口内坐标）和对应的数据下标。</summary>
        public int ItemAt(int slot, out float y)
        {
            var first = First;
            y = (first + slot) * RowH - _offset;
            return first + slot;
        }

        public void PaintThumb()
        {
            var track = Viewport.H;
            var content = _count * RowH;
            var show = content > track + 0.5f;
            _thumb.Active = show;
            if (!show) return;
            var thumbH = Mathf.Max(24f, track * track / content);
            var t = MaxOffset > 0f ? _offset / MaxOffset : 0f;
            var wide = _thumbState != HitState.Normal;
            var w = wide ? 6f : 4f;
            _thumb.Place(Viewport.X + Viewport.W - w - 1f, Viewport.Y + (track - thumbH) * t, w, thumbH);
            _thumb.Color = new Color(1f, 1f, 1f, wide ? 0.32f : 0.16f);
            var r = _thumb.InWindow;
            _thumbHit.Rect = new Rect(r.x - 4f, r.y, r.width + 6f, r.height);
        }
    }
}
