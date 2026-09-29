using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TbhCombatTracker.Ui
{
    internal enum HitState { Normal, Hover, Pressed }

    /// <summary>
    /// 一块能响应鼠标的区域。输入不走 Unity 的 EventSystem：<see cref="UiRoot"/> 每帧拿光标位置
    /// 对各窗口登记的 Hit 做命中检测，按下、抬起、拖动、滚轮都在托管代码里分发——
    /// 不用往 IL2CPP 里注入实现事件接口的组件，行为也完全自己说了算。
    ///
    /// 游戏那边只需要知道"光标在 UI 上"：每个窗口的底板是 raycastTarget，游戏自己的
    /// EventSystem.RaycastAll 打到它就会解除窗口的点击穿透。
    /// </summary>
    internal sealed class Hit
    {
        /// <summary>所在节点：它或它的祖先隐藏时，这块区域不参与命中。</summary>
        public Node Owner;
        /// <summary>窗口坐标（左上原点）。</summary>
        public Rect Rect;
        public bool Enabled = true;

        public Action Click;
        /// <summary>按下的那一刻，参数是窗口坐标（滑杆点哪跳哪）。</summary>
        public Action<Vector2> Press;
        /// <summary>拖动中，参数是（窗口坐标，本帧位移）。</summary>
        public Action<Vector2, Vector2> Drag;
        public Action DragEnd;
        /// <summary>按下就算开始拖（标题栏、滑杆）；否则移动超过几个像素才算拖，没超过的抬起算点击。</summary>
        public bool DragAtOnce;
        public Action<float> Wheel;
        public Action<HitState> State;
        /// <summary>悬停提示；返回 null 不显示。</summary>
        public Func<string> Tip;

        public bool Live => Owner == null || Owner.Shown;
    }

    /// <summary>
    /// 窗口：圆角底板 + 淡边 + 阴影，自带一个子 Canvas（这个窗口里的变化不会让别的窗口重新合批）。
    /// 布局用绝对坐标，<see cref="Refresh"/> 里整窗重排——节点有缓存，没变的值不跨 IL2CPP 边界。
    /// </summary>
    internal abstract class Window
    {
        public const float TitleH = 32f;

        public readonly Node Root;
        protected readonly Node Body;
        private readonly Box _shadow, _border, _bg;
        private Color _bgColor;
        private float _frameAlpha = 1f;
        private float _shadowScale = 1f;

        public Rect Rect;
        public readonly List<Hit> Hits = new List<Hit>();

        /// <summary>要不要在下一帧重画。</summary>
        public bool Dirty = true;
        /// <summary>数据在变的窗口（实时统计）每隔这么久重画一次，0 = 只在 Dirty 时。</summary>
        public float RefreshInterval;
        public float NextRefresh;

        /// <summary>窗口位置存盘用的名字。</summary>
        public readonly string Key;

        /// <summary>位置是玩家定的（存过、或者拖过）：程序不再自作主张挪它。</summary>
        public bool Placed;

        protected Window(string key, Transform canvas, float w, float h, Color bg)
        {
            Key = key;
            Root = new Node(key, canvas) { IsWindowRoot = true };
            Root.Go.AddComponent<Canvas>();
            Root.Go.AddComponent<GraphicRaycaster>();

            _shadow = new Box(Root, Color.white, 0f, "Shadow");
            _shadow.I.sprite = UiKit.Shadow;
            _shadow.I.type = Image.Type.Sliced;
            _border = new Box(Root, Theme.Border, Theme.RadiusWindow, "Border");
            _bg = new Box(Root, bg, Theme.RadiusWindow - 1f, "Bg");
            _bg.I.raycastTarget = true;   // 游戏的射线打到它 → 解除点击穿透
            // 底板淡成全透明时也要能被射线打到（浮窗平时就是透明的），别让它因为透明被剔掉
            try { _bg.I.canvasRenderer.cullTransparentMesh = false; }
            catch { /* 老版本没有这个开关，默认就不剔 */ }
            _bgColor = bg;
            Body = new Node("Body", Root);

            Rect = new Rect(0f, 0f, w, h);
            Layout();
        }

        public bool Visible => Root.Active;

        public virtual void Show(bool on)
        {
            if (on == Root.Active) return;
            Root.Active = on;
            if (on) Dirty = true;
        }

        public void Resize(float w, float h)
        {
            if (Mathf.Approximately(w, Rect.width) && Mathf.Approximately(h, Rect.height)) return;
            Rect.width = w;
            Rect.height = h;
            Layout();
        }

        /// <summary>挪到 (x, y)，并保证至少露出一截标题栏，别拖到够不着的地方。</summary>
        public void MoveTo(float x, float y)
        {
            var sw = UiRoot.ScreenW;
            var sh = UiRoot.ScreenH;
            Rect.x = Mathf.Clamp(x, 60f - Rect.width, Mathf.Max(0f, sw - 60f));
            Rect.y = Mathf.Clamp(y, 0f, Mathf.Max(0f, sh - 28f));
            Root.Place(Rect);
        }

        public void Clamp() => MoveTo(Rect.x, Rect.y);

        private void Layout()
        {
            Root.Place(Rect);
            var s = UiKit.ShadowSpreadPx;
            _shadow.Place(-s, -s + 2f, Rect.width + s * 2f, Rect.height + s * 2f);
            _border.Place(0f, 0f, Rect.width, Rect.height);
            _bg.Place(1f, 1f, Rect.width - 2f, Rect.height - 2f);
            Body.Place(0f, 0f, Rect.width, Rect.height);
        }

        protected Color Background
        {
            set
            {
                _bgColor = value;
                PaintFrame();
            }
        }

        /// <summary>阴影、描边、底板一起的不透明度（0 = 只剩内容）。浮窗用它做"鼠标移开就隐藏背景"。</summary>
        public float FrameAlpha
        {
            get => _frameAlpha;
            set
            {
                value = Mathf.Clamp01(value);
                if (Mathf.Approximately(value, _frameAlpha)) return;
                _frameAlpha = value;
                PaintFrame();
            }
        }

        /// <summary>背景按全局不透明度（设置里的"窗口背景"）画；阴影也跟着淡，不然一圈阴影围着一块几乎透明的底，很怪。</summary>
        protected void FollowOpacity(Color baseColor)
        {
            var opacity = UiRoot.Opacity;
            Background = Theme.With(baseColor, opacity);
            ShadowScale = Mathf.Clamp01(opacity * 1.6f);
        }

        /// <summary>阴影另乘的浓淡：背景调得很淡时阴影也该跟着淡。</summary>
        public float ShadowScale
        {
            get => _shadowScale;
            set
            {
                value = Mathf.Clamp01(value);
                if (Mathf.Approximately(value, _shadowScale)) return;
                _shadowScale = value;
                PaintFrame();
            }
        }

        private void PaintFrame()
        {
            // 阴影贴图本身是黑的，白色乘上去只改浓淡
            _shadow.Color = new Color(1f, 1f, 1f, _frameAlpha * _shadowScale);
            _border.Color = Theme.With(Theme.Border, Theme.Border.a * _frameAlpha);
            _bg.Color = Theme.With(_bgColor, _bgColor.a * _frameAlpha);
        }

        /// <summary>窗口坐标 → 最上面那块能响应的区域。禁用的区域也挡住下面的（只是不响应）。</summary>
        public Hit HitAt(Vector2 p)
        {
            for (var i = Hits.Count - 1; i >= 0; i--)
            {
                var h = Hits[i];
                if (h.Live && h.Rect.Contains(p)) return h;
            }
            return null;
        }

        /// <summary>滚轮给光标下最上面那块会滚的区域（列表里的行自己不滚，交给列表）。</summary>
        public Hit WheelAt(Vector2 p)
        {
            for (var i = Hits.Count - 1; i >= 0; i--)
            {
                var h = Hits[i];
                if (h.Wheel != null && h.Enabled && h.Live && h.Rect.Contains(p)) return h;
            }
            return null;
        }

        public Hit AddHit(Node owner, Rect rect = default)
        {
            var h = new Hit { Owner = owner, Rect = rect };
            Hits.Add(h);
            return h;
        }

        /// <summary>整块标题栏可拖，拖完记下位置。</summary>
        protected Hit DragHandle(Node owner, Rect rect)
        {
            var h = AddHit(owner, rect);
            h.DragAtOnce = true;
            h.Drag = (_, d) => MoveTo(Rect.x + d.x, Rect.y + d.y);
            h.DragEnd = EndDrag;
            return h;
        }

        /// <summary>拖完：记下"位置是玩家定的"，存盘。</summary>
        protected void EndDrag()
        {
            Placed = true;
            UiRoot.SavePositions();
        }

        /// <summary>第一次打开（没有存过位置）时放哪。</summary>
        public virtual Vector2 DefaultPosition() => new Vector2(40f, 120f);

        /// <summary>每帧调一次（动画）；重画之外的轻活放这里。</summary>
        public virtual void Tick(float dt)
        {
        }

        /// <summary>把数据画出来。<see cref="Dirty"/> 时、或者到了 <see cref="RefreshInterval"/> 时调用。</summary>
        public abstract void Refresh();
    }
}
