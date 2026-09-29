using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 界面的根：一个 ScreenSpaceOverlay 的 Canvas（排序压在游戏 UI 之上），下面挂浮窗、明细、战斗记录、设置四个窗口。
    ///
    /// 每帧：读鼠标（<see cref="UiInput"/>）→ 命中检测和分发 → 该重画的窗口重画 → 悬停提示。
    /// 数据在变的窗口按固定间隔重画（浮窗 5 次/秒），用户操作则当帧重画——旧版切维度要等一秒的毛病就是这么没的。
    ///
    /// 【生存路径】从 TrackerBehaviour.Update 直接调过来，这里和各窗口都不能引用游戏类型。
    /// 界面建不起来或者连续出错，就拆掉整个 Canvas，退回一行 IMGUI 提示；统计照常。
    /// </summary>
    internal static class UiRoot
    {
        private const int SortingOrder = 30000;

        private static GameObject _root;
        private static Canvas _canvas;
        private static bool _built, _failed;
        private static int _errors;
        private static int _screenW, _screenH;

        private static readonly List<Window> Windows = new List<Window>();
        private static readonly Dictionary<Window, int> WindowErrors = new Dictionary<Window, int>();

        public static OverlayView Overlay { get; private set; }
        public static DetailView Detail { get; private set; }
        public static MainView Main { get; private set; }
        public static SettingsView Settings { get; private set; }

        public static float Scale { get; private set; } = 1f;
        public static float ScreenW => Screen.width / Mathf.Max(0.25f, Scale);
        public static float ScreenH => Screen.height / Mathf.Max(0.25f, Scale);

        // ---- 鼠标 ----
        private static Hit _hover, _pressed;
        private static Window _pressedWindow;
        private static Vector2 _pressAt, _last;
        private static bool _dragging;
        private static float _hoverSince;

        /// <summary>设置里拖"窗口背景"滑杆时的预览值：拖着的时候各窗口按它画，松手才写进配置。</summary>
        public static float? PreviewOpacity;

        /// <summary>浮窗 / 拆分 / 战斗记录窗口背景的不透明度。</summary>
        public static float Opacity
        {
            get
            {
                if (PreviewOpacity.HasValue) return PreviewOpacity.Value;
                try { return Mathf.Clamp01(Mod.Config.BackgroundOpacity.Value); }
                catch { return 0.7f; }
            }
        }

        /// <summary>光标下最上面那个窗口（没有就是 null）。</summary>
        public static Window HoveredWindow { get; private set; }

        /// <summary>正按着（拖着）的那个窗口：拖出窗口外也算它的。</summary>
        public static Window PressedWindow => _pressed != null ? _pressedWindow : null;

        // ---- 悬停提示 ----
        private static Node _tip;
        private static Box _tipBg;
        private static Label _tipText;
        private static string _tipShown;

        // ---- 热键录制 ----
        private static ConfigEntry<string> _capture;
        private static Action _captureDone;
        public static bool CapturingKey => _capture != null;
        public static ConfigEntry<string> CaptureTarget => _capture;

        // ================================================================== 入口

        public static void Update()
        {
            if (_failed) return;
            if (!_built)
            {
                try
                {
                    Build();
                    _built = true;
                }
                catch (Exception e)
                {
                    Fail(e);
                }
                return;
            }
            try
            {
                Frame();
                _errors = 0;
            }
            catch (Exception e)
            {
                if (++_errors >= 3) Fail(e);
            }
        }

        /// <summary>界面挂了时的兜底：左上角一行字，有更新提示就把提示也写上。</summary>
        public static void OnGUIFallback()
        {
            if (!_failed) return;
            try
            {
                var notice = UpdateNotice.Visible ? UpdateNotice.Compose() : default;
                var h = notice.Title != null ? 66f : 28f;
                GUI.Box(new Rect(16f, 16f, 560f, h), "");
                GUI.Label(new Rect(24f, 20f, 548f, 22f), Strings.UiFailed);
                if (notice.Title != null)
                {
                    GUI.Label(new Rect(24f, 40f, 548f, 20f), notice.Title);
                    GUI.Label(new Rect(24f, 58f, 548f, 20f), notice.Sub ?? "");
                }
            }
            catch
            {
                // 连这一行都画不出来就算了
            }
        }

        public static void Destroy()
        {
            try
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
            }
            catch { /* 退出阶段不吵闹 */ }
            _root = null;
        }

        // ================================================================== 建界面

        private static void Build()
        {
            UiKit.Init();

            _root = new GameObject("TbhCombatTracker_UI");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;
            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = SortingOrder;
            Scale = CurrentScale();
            _canvas.scaleFactor = Scale;
            _root.AddComponent<GraphicRaycaster>();

            var t = _root.transform;
            Overlay = Add(new OverlayView(t));
            Detail = Add(new DetailView(t));
            Main = Add(new MainView(t));
            Settings = Add(new SettingsView(t));
            BuildTip(t);

            LoadPositions();
            Overlay.Sync();
            Mod.Log.Msg($"界面已就绪（uGUI，缩放 {Scale:0.##}，字体 {UiKit.FontSource}）。");
        }

        private static T Add<T>(T w) where T : Window
        {
            Windows.Add(w);
            w.Show(false);
            return w;
        }

        private static void Fail(Exception e)
        {
            _failed = true;
            Mod.Log.Error($"界面出错，已停用（统计照常记录，热键照常可用）：{e}");
            Destroy();
        }

        private static float CurrentScale()
        {
            try { return Mathf.Clamp(Mod.Config.UiScale.Value, 0.5f, 3f); }
            catch { return 1f; }
        }

        // ================================================================== 每帧

        private static void Frame()
        {
            // 缩放变了（设置里改的）或者游戏窗口尺寸变了：重画，并把窗口拉回屏幕里
            var scale = CurrentScale();
            if (!Mathf.Approximately(scale, Scale) || Screen.width != _screenW || Screen.height != _screenH)
            {
                Scale = scale;
                _screenW = Screen.width;
                _screenH = Screen.height;
                _canvas.scaleFactor = scale;
                foreach (var w in Windows)
                {
                    w.Dirty = true;
                    w.Clamp();
                }
            }

            UiInput.Poll(Scale);
            Pointer();
            Capture();
            EncounterLoader.Poll();
            Overlay.Sync();

            var now = Time.unscaledTime;
            var dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            foreach (var w in Windows)
            {
                if (!w.Visible) continue;
                try
                {
                    w.Tick(dt);
                    if (w.Dirty || (w.RefreshInterval > 0f && now >= w.NextRefresh))
                    {
                        w.Dirty = false;
                        w.NextRefresh = now + w.RefreshInterval;
                        w.Refresh();
                    }
                    WindowErrors[w] = 0;
                }
                catch (Exception e)
                {
                    // 一个窗口坏了不连累别的：连续出错就把它关掉
                    WindowErrors.TryGetValue(w, out var n);
                    WindowErrors[w] = ++n;
                    if (n == 1) Mod.Log.Warning($"{w.Key} 刷新出错：{e}");
                    if (n >= 3)
                    {
                        w.Show(false);
                        WindowErrors[w] = 0;
                        Mod.Log.Error($"{w.Key} 连续刷新失败，已关闭。");
                    }
                }
            }

            UpdateTip(now);
        }

        // ================================================================== 鼠标

        private static void Pointer()
        {
            var p = UiInput.Pos;
            Window win = null;
            Hit hit = null;
            if (UiInput.Valid)
            {
                for (var i = Windows.Count - 1; i >= 0; i--)
                {
                    var w = Windows[i];
                    if (!w.Visible || !w.Rect.Contains(p)) continue;
                    win = w;
                    hit = w.HitAt(p - w.Rect.position);
                    break;
                }
            }
            HoveredWindow = win;

            if (_pressed != null)
            {
                // 按住期间锁定在按下的那块：拖出窗口也照样跟手
                if (UiInput.Held)
                {
                    var local = p - _pressedWindow.Rect.position;
                    var delta = p - _last;
                    if (!_dragging && _pressed.Drag != null &&
                        (_pressed.DragAtOnce || (p - _pressAt).sqrMagnitude > 16f))
                        _dragging = true;
                    if (_dragging && delta != Vector2.zero)
                    {
                        var drag = _pressed.Drag;
                        Safe("拖动", () => drag(local, delta));
                    }
                }
                else
                {
                    var target = _pressed;
                    var wasDragging = _dragging;
                    _pressed = null;
                    _dragging = false;
                    SetState(target, target == hit ? HitState.Hover : HitState.Normal);
                    if (wasDragging) Safe("拖动", target.DragEnd);
                    else if (target == hit && target.Enabled) Safe("点击", target.Click);
                    _hover = hit;
                }
            }
            else
            {
                if (hit != _hover)
                {
                    SetState(_hover, HitState.Normal);
                    SetState(hit, HitState.Hover);
                    _hover = hit;
                    _hoverSince = Time.unscaledTime;
                }
                if (UiInput.Pressed && win != null)
                {
                    ToFront(win);
                    HideTip();
                    if (hit != null && hit.Enabled)
                    {
                        _pressed = hit;
                        _pressedWindow = win;
                        _pressAt = p;
                        _dragging = false;
                        SetState(hit, HitState.Pressed);
                        var press = hit.Press;
                        var at = p - win.Rect.position;
                        if (press != null) Safe("按下", () => press(at));
                    }
                }
            }

            if (UiInput.Wheel != 0f && win != null)
            {
                var wheel = win.WheelAt(p - win.Rect.position)?.Wheel;
                var amount = UiInput.Wheel;
                if (wheel != null) Safe("滚轮", () => wheel(amount));
            }

            _last = p;
        }

        /// <summary>按钮背后的操作出错只记一笔：一个坏按钮不该连累整个界面被熔断。</summary>
        private static void Safe(string what, Action a)
        {
            if (a == null) return;
            try { a(); }
            catch (Exception e) { Mod.Log.Warning($"界面{what}处理出错：{e}"); }
        }

        private static void SetState(Hit h, HitState s)
        {
            if (h == null) return;
            try { h.State?.Invoke(s); }
            catch (Exception e) { Mod.Log.Warning($"按钮状态刷新出错：{e.GetType().Name}: {e.Message}"); }
        }

        public static void ToFront(Window w)
        {
            var i = Windows.IndexOf(w);
            if (i < 0 || i == Windows.Count - 1) return;
            Windows.RemoveAt(i);
            Windows.Add(w);
            w.Root.ToFront();
            _tip?.ToFront();
        }

        // ================================================================== 悬停提示

        private static void BuildTip(Transform canvas)
        {
            _tip = new Node("Tooltip", canvas) { IsWindowRoot = true };
            var c = _tip.Go.AddComponent<Canvas>();
            c.overrideSorting = true;
            c.sortingOrder = SortingOrder + 10;
            _tipBg = new Box(_tip, new Color32(0x26, 0x29, 0x31, 0xF6), 4f, "Bg");
            _tipText = new Label(_tip, Theme.FontSmall, Theme.Text, name: "Text").NoWrap();
            _tip.Active = false;
        }

        private static void UpdateTip(float now)
        {
            var text = _pressed == null && _hover != null && _hover.Tip != null && now - _hoverSince > 0.45f
                ? _hover.Tip()
                : null;
            if (string.IsNullOrEmpty(text))
            {
                HideTip();
                return;
            }
            if (text != _tipShown)
            {
                _tipShown = text;
                _tipText.Text = text;
            }
            var w = Mathf.Ceil(_tipText.PreferredWidth) + 16f;
            const float h = 22f;
            var x = Mathf.Clamp(UiInput.Pos.x + 12f, 0f, Mathf.Max(0f, ScreenW - w));
            var y = UiInput.Pos.y + 20f;
            if (y + h > ScreenH) y = UiInput.Pos.y - h - 6f;
            _tip.Place(x, y, w, h);
            _tipBg.Place(0f, 0f, w, h);
            _tipText.Place(8f, 0f, w - 16f, h);
            _tip.Active = true;
        }

        private static void HideTip()
        {
            if (_tip != null) _tip.Active = false;
            _tipShown = null;
        }

        // ================================================================== 热键录制

        private static readonly KeyCode[] CaptureKeys = BuildCaptureKeys();

        private static KeyCode[] BuildCaptureKeys()
        {
            var keys = new List<KeyCode>();
            for (var k = KeyCode.F1; k <= KeyCode.F15; k++) keys.Add(k);
            for (var k = KeyCode.A; k <= KeyCode.Z; k++) keys.Add(k);
            for (var k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++) keys.Add(k);
            for (var k = KeyCode.Keypad0; k <= KeyCode.KeypadEquals; k++) keys.Add(k);
            keys.AddRange(new[]
            {
                KeyCode.Insert, KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown, KeyCode.Pause,
                KeyCode.ScrollLock, KeyCode.BackQuote, KeyCode.Minus, KeyCode.Equals, KeyCode.LeftBracket,
                KeyCode.RightBracket, KeyCode.Backslash, KeyCode.Semicolon, KeyCode.Quote, KeyCode.Comma,
                KeyCode.Period, KeyCode.Slash, KeyCode.Tab, KeyCode.Space, KeyCode.Return,
                KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
            });
            return keys.ToArray();
        }

        /// <summary>开始给 <paramref name="entry"/> 录一个新热键：Esc 取消，Delete / Backspace 清空（不用热键）。</summary>
        public static void BeginCapture(ConfigEntry<string> entry, Action done)
        {
            _capture = entry;
            _captureDone = done;
        }

        public static void CancelCapture()
        {
            var done = _captureDone;
            _capture = null;
            _captureDone = null;
            done?.Invoke();
        }

        private static void Capture()
        {
            if (_capture == null) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    CancelCapture();
                    return;
                }
                if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))
                {
                    _capture.Value = "";
                    CancelCapture();
                    return;
                }
                foreach (var k in CaptureKeys)
                {
                    if (!Input.GetKeyDown(k)) continue;
                    _capture.Value = k.ToString();
                    CancelCapture();
                    return;
                }
            }
            catch
            {
                // 旧版 Input 不可用：热键本来也用不了
                CancelCapture();
            }
        }

        // ================================================================== 开关窗口

        public static void ToggleOverlay()
        {
            if (!_built || _failed) return;
            Overlay.Wanted = !Overlay.Wanted;
            Overlay.Sync();
            if (!Overlay.Wanted) Detail.Show(false);
        }

        public static void ToggleMain()
        {
            if (!_built || _failed) return;
            Main.Show(!Main.Visible);
            if (Main.Visible) ToFront(Main);
        }

        /// <summary>设置改了（颜色、斜切…）：所有窗口下一帧重画。</summary>
        public static void MarkAllDirty()
        {
            foreach (var w in Windows) w.Dirty = true;
        }

        public static void OpenSettings()
        {
            if (!_built || _failed) return;
            Settings.Show(true);
            ToFront(Settings);
        }

        // ================================================================== 窗口位置

        private static string StatePath => Path.Combine(Paths.BepInExRootPath, "TbhCombatTracker", "ui-state.txt");

        private static void LoadPositions()
        {
            var saved = new Dictionary<string, Vector2>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(StatePath))
                {
                    foreach (var line in File.ReadAllLines(StatePath))
                    {
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var xy = line.Substring(eq + 1).Split(',');
                        if (xy.Length == 2 &&
                            float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                            float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                            saved[line.Substring(0, eq).Trim()] = new Vector2(x, y);
                    }
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"读不到窗口位置：{e.GetType().Name}");
            }

            foreach (var w in Windows)
            {
                w.Placed = saved.TryGetValue(w.Key, out var v);
                var at = w.Placed ? v : w.DefaultPosition();
                w.MoveTo(at.x, at.y);
            }
        }

        public static void SavePositions()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                // 只存玩家定过位置的窗口：没动过的下次还按默认规则摆（比如拆分窗口跟在浮窗下面）
                var lines = new List<string>();
                foreach (var w in Windows)
                {
                    if (!w.Placed) continue;
                    lines.Add(w.Key + "=" + w.Rect.x.ToString("0", CultureInfo.InvariantCulture) + "," +
                              w.Rect.y.ToString("0", CultureInfo.InvariantCulture));
                }
                File.WriteAllLines(StatePath, lines);
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"窗口位置保存失败：{e.GetType().Name}");
            }
        }
    }
}
