using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 配色和尺寸。深色主题：面板压在游戏画面和桌面上，底下什么颜色都有，
    /// 所以底色要够暗、够不透明，边上再描一圈极淡的亮边把轮廓勾出来。
    /// </summary>
    internal static class Theme
    {
        public static readonly Color WindowBg = new Color32(0x14, 0x16, 0x1B, 0xFA);
        /// <summary>浮窗底色；透明度来自配置 BackgroundOpacity，鼠标不在浮窗上时整个隐藏。</summary>
        public static readonly Color OverlayBg = new Color32(0x10, 0x12, 0x17, 0xFF);
        public static readonly Color Border = new Color(1f, 1f, 1f, 0.09f);
        public static readonly Color Pane = new Color32(0x1B, 0x1E, 0x25, 0xFF);
        /// <summary>
        /// 窗口里的分区底色（列表、右侧数据区）：再压一层半透明的黑。窗口背景调得很透明时，
        /// 数据区还有一层衬底，字不至于直接糊在后面的桌面上；背景不透明时就是略深一点的内嵌分区。
        /// </summary>
        public static readonly Color Panel = new Color(0f, 0f, 0f, 0.24f);
        /// <summary>分区里再分块（曲线）：叠一层淡白。</summary>
        public static readonly Color Inset = new Color(1f, 1f, 1f, 0.035f);
        /// <summary>浮窗上的次要文字：浮窗平时没有背景，灰字压在杂乱的桌面上看不清，得比窗口里的次要文字亮。</summary>
        public static readonly Color OverlayDim = new Color32(0xD3, 0xD7, 0xDD, 0xFF);
        public static readonly Color Hover = new Color(1f, 1f, 1f, 0.07f);
        public static readonly Color Pressed = new Color(1f, 1f, 1f, 0.12f);
        public static readonly Color Divider = new Color(1f, 1f, 1f, 0.06f);

        public static readonly Color Text = new Color32(0xE6, 0xE8, 0xEB, 0xFF);
        public static readonly Color TextDim = new Color32(0xA0, 0xA6, 0xB0, 0xFF);
        public static readonly Color TextFaint = new Color32(0x6C, 0x72, 0x7D, 0xFF);

        public static readonly Color Accent = new Color32(0x4C, 0x8D, 0xF6, 0xFF);
        public static readonly Color AccentSoft = new Color32(0x4C, 0x8D, 0xF6, 0x47);
        public static readonly Color Live = new Color32(0x3D, 0xDC, 0x84, 0xFF);
        public static readonly Color Danger = new Color32(0xD9, 0x4F, 0x4F, 0xFF);
        public static readonly Color Warning = new Color32(0xC8, 0x8A, 0x12, 0xFF);
        public static readonly Color Success = new Color32(0x2E, 0x9E, 0x57, 0xFF);

        public const int FontTitle = 13;
        public const int FontBody = 12;
        public const int FontSmall = 11;
        public const int FontTiny = 10;

        public const float RadiusWindow = 8f;
        public const float RadiusControl = 5f;

        /// <summary>拆分表 / 环形图的配色：色相拉开，和职业色也错开，不至于混淆。</summary>
        private static readonly Color[] Palette =
        {
            new Color32(0x4F, 0xA3, 0xE3, 0xFF), // 蓝
            new Color32(0xE8, 0x7D, 0x3E, 0xFF), // 橙
            new Color32(0x6C, 0xC2, 0x4A, 0xFF), // 绿
            new Color32(0xD1, 0x54, 0x8C, 0xFF), // 品红
            new Color32(0xF2, 0xC9, 0x4C, 0xFF), // 黄
            new Color32(0x9B, 0x6B, 0xD6, 0xFF), // 紫
            new Color32(0x3F, 0xC1, 0xB0, 0xFF), // 青
            new Color32(0xC4, 0x5B, 0x4A, 0xFF), // 砖红
            new Color32(0x8A, 0x9B, 0xA8, 0xFF), // 灰蓝
        };

        public static Color PaletteAt(int i) => Palette[((i % Palette.Length) + Palette.Length) % Palette.Length];

        /// <summary>来源的颜色：英雄按职业，未知来源 / 怪物用灰。</summary>
        public static Color SourceColor(SourceStats s)
            => s == null || s.InstanceId == 0 || s.ClassType == 0 ? new Color(0.62f, 0.62f, 0.62f) : JobTable.ColorOf(s.ClassType);

        public static Color With(Color c, float a)
        {
            c.a = a;
            return c;
        }
    }

    /// <summary>
    /// 界面的公共资源：字体、圆角贴图、网格材质。全部在第一次建界面时创建，之后常驻。
    ///
    /// 字体用系统字体动态生成（<c>Font.CreateDynamicFontFromOSFont</c>）：游戏自带的 TMP 字库是静态图集，
    /// 动态加字的接口在这个构建里被裁掉了，显示不了任意中文；老式 Text + 系统字体没有这个限制。
    /// 图标用 Windows 自带的图标字体（Win11 的 Segoe Fluent Icons，Win10 的 Segoe MDL2 Assets），
    /// 两个都没有就退回文字（见 <see cref="Glyphs"/>）。
    /// </summary>
    internal static class UiKit
    {
        public static Font TextFont { get; private set; }
        public static Font IconFont { get; private set; }
        public static bool HasIcons => IconFont != null;

        /// <summary>九宫格圆角：半径 <see cref="RoundRadiusPx"/> 像素，靠 pixelsPerUnitMultiplier 缩到想要的半径。</summary>
        public static Sprite Round { get; private set; }
        /// <summary>九宫格柔和阴影，窗口底下垫一层。</summary>
        public static Sprite Shadow { get; private set; }
        public static Material ShapeMaterial { get; private set; }

        public const float RoundRadiusPx = 16f;
        public const float ShadowSpreadPx = 10f;

        private static readonly string[] TextFonts =
        {
            "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Yu Gothic UI", "Malgun Gothic",
            "Meiryo UI", "SimSun", "Arial Unicode MS", "Arial",
        };
        private static readonly string[] TextFontFiles = { "msyh.ttc", "segoeui.ttf", "simsun.ttc", "arial.ttf" };

        private static readonly string[] IconFonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };
        private static readonly string[] IconFontFiles = { "SegoeIcons.ttf", "segmdl2.ttf" };

        /// <summary>字体是怎么拿到的，写进日志方便排查。</summary>
        public static string FontSource { get; private set; }

        public static void Init()
        {
            Round = MakeRound();
            Shadow = MakeShadow();
            ShapeMaterial = Canvas.GetDefaultCanvasMaterial();

            var installed = InstalledFonts();
            TextFont = OsFont(Available(TextFonts, installed, true), TextFontFiles, (char)0x9A91, out var how)
                       ?? Font.GetDefault();
            FontSource = how ?? "default";

            var icons = Available(IconFonts, installed, false);
            if (icons.Length > 0 || installed == null)
                IconFont = OsFont(icons.Length > 0 ? icons : IconFonts, IconFontFiles, Glyphs.ChevronUp, out _);

            Mod.Log.Msg($"界面字体：{FontSource}；图标字体：{(IconFont != null ? IconFont.name : "无（用文字代替）")}");
        }

        private static string[] InstalledFonts()
        {
            try
            {
                var names = Font.GetOSInstalledFontNames();
                var list = new string[names.Length];
                for (var i = 0; i < names.Length; i++) list[i] = names[i];
                return list;
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"读不到系统字体列表：{e.GetType().Name}");
                return null;
            }
        }

        /// <summary>
        /// 用系统字体建一个动态字体。<c>Font.CreateDynamicFontFromOSFont</c> 在这个构建里用不了——
        /// 它内部 new 的那个私有构造函数被裁掉了（MissingMethodException）。所以照着 Unity 的实现手工做一遍：
        /// 先要一个没跑构造函数的 Font 对象，再调它背后那个原生接口。这条路不通就按文件路径加载字体文件。
        /// <paramref name="probe"/> 是一个这套字体必须有的字，用来确认拿到的字体真能用。
        /// </summary>
        private static Font OsFont(string[] names, string[] files, char probe, out string how)
        {
            how = null;
            try
            {
                var font = new Font(IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<Font>.NativeClassPtr));
                Font.Internal_CreateDynamicFont(font, names, 16);
                if (font.HasCharacter(probe))
                {
                    font.hideFlags = HideFlags.HideAndDontSave;
                    how = "OS:" + names[0];
                    return font;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"按名字创建系统字体失败（{names[0]}）：{e.GetType().Name}: {e.Message}");
            }

            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var file in files)
            {
                try
                {
                    var path = System.IO.Path.Combine(dir, file);
                    if (!System.IO.File.Exists(path)) continue;
                    var font = new Font(path);
                    if (!font.HasCharacter(probe)) continue;
                    font.hideFlags = HideFlags.HideAndDontSave;
                    how = "file:" + file;
                    return font;
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"按文件加载字体失败（{file}）：{e.GetType().Name}: {e.Message}");
                }
            }
            return null;
        }

        /// <summary>按优先级挑系统里装了的字体；列表读不到时原样返回（交给 Unity 自己回退）。</summary>
        private static string[] Available(string[] wanted, string[] installed, bool keepAll)
        {
            if (installed == null) return wanted;
            var set = new System.Collections.Generic.HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
            var list = new System.Collections.Generic.List<string>();
            foreach (var w in wanted)
                if (set.Contains(w)) list.Add(w);
            if (list.Count == 0 && keepAll) list.Add("Arial");
            return list.ToArray();
        }

        private static Sprite MakeRound()
        {
            const int size = 48;
            var r = RoundRadiusPx;
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // 到最近那个圆角圆心的距离；不在角上就是实心
                    var cx = Mathf.Clamp(x + 0.5f, r, size - r);
                    var cy = Mathf.Clamp(y + 0.5f, r, size - r);
                    var dx = x + 0.5f - cx;
                    var dy = y + 0.5f - cy;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = Mathf.Clamp01(r - d + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f + 0.5f));
                }
            }
            return MakeSprite(px, size, r + 1f);
        }

        private static Sprite MakeShadow()
        {
            // 圆角矩形往外 ShadowSpreadPx 的衰减：贴边处也只有三成浓，往外按平方很快淡掉
            const int size = 64;
            var spread = ShadowSpreadPx;
            var r = RoundRadiusPx * 0.5f + spread;
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var cx = Mathf.Clamp(x + 0.5f, r, size - r);
                    var cy = Mathf.Clamp(y + 0.5f, r, size - r);
                    var dx = x + 0.5f - cx;
                    var dy = y + 0.5f - cy;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var t = Mathf.Clamp01((r - d) / spread);   // 0 在最外圈，1 在窗口边
                    var a = t * t * 0.32f;
                    px[y * size + x] = new Color32(0, 0, 0, (byte)(a * 255f + 0.5f));
                }
            }
            return MakeSprite(px, size, r + 1f);
        }

        private static Sprite MakeSprite(Color32[] px, int size, float border)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            var sprite = Sprite.Create(tex, new UnityEngine.Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0u,
                                       SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }

    // ====================================================================== 节点

    /// <summary>
    /// 一个 RectTransform。布局全部用绝对坐标：左上原点、y 向下、父节点内的本地坐标，
    /// 和 IMGUI 的写法一样直白。改值之前先和缓存比，没变就不跨 IL2CPP 边界。
    /// </summary>
    internal class Node
    {
        public readonly GameObject Go;
        public readonly RectTransform Rt;
        public readonly Node Parent;

        private float _x = float.NaN, _y, _w, _h;
        private bool _active = true;

        public Node(string name, Node parent) : this(name, parent?.Rt) => Parent = parent;

        public Node(string name, Transform parent)
        {
            Go = new GameObject(name) { layer = 5 };   // 5 = UI 层
            Rt = Go.AddComponent<RectTransform>();
            if (parent != null) Rt.SetParent(parent, false);
            Rt.anchorMin = new Vector2(0f, 1f);
            Rt.anchorMax = new Vector2(0f, 1f);
            Rt.pivot = new Vector2(0f, 1f);
        }

        public float X => _x;
        public float Y => _y;
        public float W => _w;
        public float H => _h;
        public Rect Local => new Rect(float.IsNaN(_x) ? 0f : _x, _y, _w, _h);

        /// <summary>在所属窗口里的矩形（逐级加上父节点的偏移），命中检测用。</summary>
        public Rect InWindow
        {
            get
            {
                var r = Local;
                for (var p = Parent; p != null && !p.IsWindowRoot; p = p.Parent)
                {
                    r.x += p.Local.x;
                    r.y += p.Local.y;
                }
                return r;
            }
        }

        public bool IsWindowRoot;

        /// <summary>自己和所有祖先都没隐藏（纯托管判断，不问 Unity）。</summary>
        public bool Shown
        {
            get
            {
                for (var n = this; n != null; n = n.Parent)
                    if (!n.Active) return false;
                return true;
            }
        }

        public void Place(float x, float y, float w, float h)
        {
            if (w < 0f) w = 0f;
            if (h < 0f) h = 0f;
            if (x == _x && y == _y && w == _w && h == _h) return;
            if (x != _x || y != _y) Rt.anchoredPosition = new Vector2(x, -y);
            if (w != _w || h != _h) Rt.sizeDelta = new Vector2(w, h);
            _x = x;
            _y = y;
            _w = w;
            _h = h;
        }

        public void Place(Rect r) => Place(r.x, r.y, r.width, r.height);

        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                Go.SetActive(value);
            }
        }

        public void ToFront() => Rt.SetAsLastSibling();
    }

    /// <summary>一段文字。单行：宽度不够时在字 / 词边界截断，高度要留够一行（约 1.4 倍字号）。</summary>
    internal sealed class Label : Node
    {
        public readonly UnityEngine.UI.Text T;
        private string _text;
        private Color _color;
        private int _size;
        private TextAnchor _align;

        public Label(Node parent, int size, Color color, TextAnchor align = TextAnchor.MiddleLeft,
                     bool bold = false, Font font = null, string name = "Label")
            : base(name, parent)
        {
            T = Go.AddComponent<UnityEngine.UI.Text>();
            T.font = font ?? UiKit.TextFont;
            T.fontSize = size;
            T.alignment = align;
            T.color = color;
            T.horizontalOverflow = HorizontalWrapMode.Wrap;
            T.verticalOverflow = VerticalWrapMode.Truncate;
            T.supportRichText = false;
            T.raycastTarget = false;
            if (bold) T.fontStyle = FontStyle.Bold;
            _color = color;
            _size = size;
            _align = align;
            _text = "";
        }

        public string Text
        {
            get => _text;
            set
            {
                value = value ?? "";
                if (value == _text) return;
                _text = value;
                T.text = value;
            }
        }

        public Color Color
        {
            get => _color;
            set
            {
                if (value == _color) return;
                _color = value;
                T.color = value;
            }
        }

        public int Size
        {
            set
            {
                if (value == _size) return;
                _size = value;
                T.fontSize = value;
            }
        }

        public TextAnchor Align
        {
            set
            {
                if (value == _align) return;
                _align = value;
                T.alignment = value;
            }
        }

        /// <summary>不换行、不截断（数字这类肯定放得下的）。</summary>
        public Label NoWrap()
        {
            T.horizontalOverflow = HorizontalWrapMode.Overflow;
            T.verticalOverflow = VerticalWrapMode.Overflow;
            return this;
        }

        /// <summary>允许 &lt;b&gt; / &lt;color&gt; 这类富文本（标题里粗体和灰色混排）。</summary>
        public Label Rich()
        {
            T.supportRichText = true;
            return this;
        }

        /// <summary>
        /// 四面描边：浮窗平时没有背景，字直接压在桌面上，底下什么颜色都有，只有一圈黑边才在哪都看得清。
        /// Outline 组件（四个方向各复制一份）万一在以后的游戏版本里用不了，退回单向的 Shadow；再不行就不描。
        /// </summary>
        public Label Outline(float alpha = 0.92f)
        {
            if (_outline == 0)
            {
                try
                {
                    var o = Go.AddComponent<UnityEngine.UI.Outline>();
                    o.effectColor = new Color(0f, 0f, 0f, alpha);
                    o.effectDistance = new Vector2(1f, -1f);
                    return this;
                }
                catch (Exception e)
                {
                    _outline = 1;
                    Mod.Log.Warning($"文字四面描边不可用，改用单向阴影：{e.GetType().Name}");
                }
            }
            if (_outline == 1)
            {
                try
                {
                    var sh = Go.AddComponent<UnityEngine.UI.Shadow>();
                    sh.effectColor = new Color(0f, 0f, 0f, alpha);
                    sh.effectDistance = new Vector2(1f, -1f);
                }
                catch (Exception e)
                {
                    _outline = 2;
                    Mod.Log.Warning($"文字描边不可用：{e.GetType().Name}");
                }
            }
            return this;
        }

        /// <summary>0 = 用 Outline，1 = 退回 Shadow，2 = 都不可用。</summary>
        private static int _outline;

        public float PreferredWidth => T.preferredWidth;
    }

    /// <summary>纯色块或圆角块（Image）。<paramref name="radius"/> = 0 是直角。</summary>
    internal sealed class Box : Node
    {
        public readonly Image I;
        private Color _color;

        public Box(Node parent, Color color, float radius = 0f, string name = "Box") : base(name, parent)
        {
            I = Go.AddComponent<Image>();
            I.raycastTarget = false;
            I.color = color;
            _color = color;
            if (radius > 0f) Radius(radius);
        }

        public Box Radius(float radius)
        {
            I.sprite = UiKit.Round;
            I.type = Image.Type.Sliced;
            I.pixelsPerUnitMultiplier = UiKit.RoundRadiusPx / Mathf.Max(0.5f, radius);
            return this;
        }

        public Color Color
        {
            get => _color;
            set
            {
                if (value == _color) return;
                _color = value;
                I.color = value;
            }
        }
    }

    /// <summary>
    /// 自己拼网格的图形：只挂 CanvasRenderer，几何由 <see cref="MeshBuilder"/> 给。
    /// 不派生 Graphic（那要往 IL2CPP 里注入子类），直接 SetMesh，效果一样，还少一层。
    /// </summary>
    internal sealed class Shape : Node
    {
        private readonly CanvasRenderer _cr;
        private readonly Mesh _mesh;
        public readonly MeshBuilder B = new MeshBuilder();

        public Shape(Node parent, string name = "Shape") : base(name, parent)
        {
            _cr = Go.AddComponent<CanvasRenderer>();
            _mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _mesh.MarkDynamic();
            _cr.materialCount = 1;
            _cr.SetMaterial(UiKit.ShapeMaterial, 0);
            _cr.SetTexture(Texture2D.whiteTexture);
        }

        /// <summary>开始重画：清空几何，羽化宽度按当前缩放取一个屏幕像素。</summary>
        public MeshBuilder Begin(float scale)
        {
            B.Clear();
            B.Px = 1f / Mathf.Max(0.25f, scale);
            return B;
        }

        public void Commit()
        {
            B.ApplyTo(_mesh);
            _cr.SetMesh(_mesh);
        }
    }
}
