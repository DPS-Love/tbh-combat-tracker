using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 横向伤害面板，形制参考 FFXIV ACT 的 Horizoverlay：
    /// 每个来源一张窄卡片横向并排，卡片主体是 <c>skew(-30deg)</c> 的平行四边形，
    /// 按职业立绘主色配色，底部一条 2px 占比条。
    /// </summary>
    internal static class Overlay
    {
        public static bool Visible = true;

        private const int WindowId = 0x7B4D; // 随便挑一个不太可能撞车的 id

        // 版式常量，数值对齐 Horizoverlay 的 CSS（.row max-width:140px; margin:0 6px）
        private const float CardW = 140f;
        private const float CardGap = 6f;
        private const float Pad = 10f;
        private const float HeaderH = 20f;
        private const float NameH = 15f;
        private const float BlockH = 21f;
        private const float PctBarH = 3f;
        private const float PctTextH = 11f;
        private const float DetailH = 12f;

        private static Rect _rect = new Rect(20f, 20f, 460f, 116f);
        private static TrackerView _view = TrackerView.Outgoing;
        private static bool _stylesReady;

        private static GUIStyle _name, _dpsLeft, _dpsRight, _pct, _detail, _header, _headerRight;
        private static Texture2D _white;

        /// <summary>
        /// UI 出问题时的熔断。OnGUI 每帧调用，一个每帧都抛的异常会把游戏彻底刷死
        /// （曾经就因为 GUILayout.FlexibleSpace 在这个 IL2CPP 构建里被裁剪而卡死启动），
        /// 所以宁可关掉面板也不能让它拖垮游戏。
        /// </summary>
        private static int _failures;
        private static bool _disabled;

        public static void Draw()
        {
            if (!Visible || _disabled) return;

            var oldMatrix = GUI.matrix;
            try
            {
                EnsureStyles();

                var scale = Mathf.Clamp(Mod.Config.UiScale.Value, 0.5f, 3f);
                if (Math.Abs(scale - 1f) > 0.001f)
                    GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

                // 窗口宽度跟着卡片数量走，没人的时候也留出提示位
                var n = Mathf.Max(1, RowCount());
                _rect.width = Pad * 2f + n * CardW + (n - 1) * CardGap;
                _rect.height = HeaderH + NameH + BlockH + PctBarH + PctTextH + DetailH + Pad * 2f;

                _rect = GUI.Window(WindowId, _rect, (GUI.WindowFunction)DrawWindow, "TBH Combat Tracker");
                DetailWindow.Draw();
                _failures = 0;
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                GUI.matrix = oldMatrix;
            }
        }

        /// <summary>
        /// 光标（客户区坐标，左上原点）是否落在面板上。
        /// 面板是带 UiScale 缩放绘制的，所以要先把坐标除回去再和 _rect 比。
        /// </summary>
        /// <summary>面板当前的 GUI 矩形（未乘缩放），给射线靶同步用。</summary>
        public static Rect CurrentRect => _rect;

        /// <summary>当前视图，明细窗口要跟着一起切。</summary>
        public static TrackerView CurrentView => _view;

        public static float CurrentScale
        {
            get
            {
                try { return Mathf.Clamp(Mod.Config.UiScale.Value, 0.5f, 3f); }
                catch { return 1f; }
            }
        }

        public static bool HitTest(Vector2 clientPoint)
        {
            if (!Visible || _disabled) return false;
            try
            {
                var scale = Mathf.Clamp(Mod.Config.UiScale.Value, 0.5f, 3f);
                return _rect.Contains(clientPoint / scale);
            }
            catch
            {
                return false;
            }
        }

        private static int RowCount() => DamageTracker.Current.Bucket(_view).Count;

        private static string ViewLabel(TrackerView v)
        {
            switch (v)
            {
                case TrackerView.Incoming: return "承伤";
                case TrackerView.Healing: return "治疗";
                default: return "输出";
            }
        }

        private static string EmptyHint(TrackerView v)
        {
            switch (v)
            {
                case TrackerView.Incoming: return "尚未承受伤害…";
                case TrackerView.Healing: return "尚未产生治疗…";
                default: return "等待伤害数据…";
            }
        }

        private static void DrawWindow(int id)
        {
            // GUI.Window 的回调是跨 IL2CPP 边界调过来的，异常在这里就得截住，
            // 否则会变成 trampoline 异常，每帧刷屏。
            try
            {
                DrawWindowBody();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void Fail(Exception e)
        {
            if (++_failures < 3) return;
            _disabled = true;
            Mod.Log.Error($"面板连续 {_failures} 帧绘制失败，已永久关闭以免拖垮游戏：{e}");
        }

        private static void DrawWindowBody()
        {
            var enc = DamageTracker.Current;
            var rows = DamageTracker.Snapshot(_view);
            var total = enc.TotalOf(_view);
            var duration = enc.DurationSeconds;

            var top = Pad + 6f;

            // ---- 头部：关卡/战斗标题 + 时长 + 总量 + 团队每秒 + 按钮 ----
            var headRect = new Rect(Pad, top, _rect.width - Pad * 2f, HeaderH);
            var title = string.IsNullOrEmpty(enc.Label) ? $"#{enc.Index}" : enc.Label;
            Shadowed(new Rect(headRect.x, headRect.y, headRect.width * 0.62f, headRect.height),
                     $"{title}   {duration:0.0}s   {Short(total)}   " +
                     $"{Short(duration > 0d ? total / duration : 0d)}/s", _header);

            var btnW = 46f;
            if (GUI.Button(new Rect(headRect.xMax - btnW * 2f - 4f, headRect.y, btnW, 17f),
                           ViewLabel(_view)))
            {
                // 输出 → 承伤 → 治疗 → 输出
                _view = _view == TrackerView.Outgoing ? TrackerView.Incoming
                      : _view == TrackerView.Incoming ? TrackerView.Healing
                      : TrackerView.Outgoing;
            }
            if (GUI.Button(new Rect(headRect.xMax - btnW, headRect.y, btnW, 17f), "重置"))
                DamageTracker.ResetCurrent();

            top += HeaderH;

            if (rows.Count == 0)
            {
                Shadowed(new Rect(Pad, top + 12f, _rect.width - Pad * 2f, 20f),
                         EmptyHint(_view), _name);
                GUI.DragWindow();
                return;
            }

            // ---- 卡片区 ----
            var max = rows[0].Total;
            var cardH = NameH + BlockH + PctBarH + PctTextH + DetailH + 2f;
            for (int i = 0; i < rows.Count; i++)
            {
                var at = new Rect(Pad + i * (CardW + CardGap), top, CardW, cardH);
                DrawCard(rows[i], at, total, max);
                HandleCardClick(at, rows[i].InstanceId);
            }

            // 整个窗口都能拖：按钮和其它控件会先消费掉自己的点击，不冲突
            GUI.DragWindow();
        }

        // 点击卡片展开明细。要和拖窗口共存：MouseDown 不消费（留给 GUI.DragWindow 起拖），
        // 只在 MouseUp 时判断"按下到抬起几乎没移动"才算点击。
        private static Vector2 _pressPos;
        private static int _pressedId;
        private static bool _pressing;

        private static void HandleCardClick(Rect r, int id)
        {
            var e = Event.current;
            if (e == null || e.button != 0) return;

            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                _pressPos = e.mousePosition;
                _pressedId = id;
                _pressing = true;
            }
            else if (e.type == EventType.MouseUp && _pressing && _pressedId == id)
            {
                _pressing = false;
                // 位移超过几像素就当成拖窗口，不是点击。
                // 这里不调 e.Use()——消费掉会让 DragWindow 收不到抬起事件，窗口可能卡在拖拽态。
                if (r.Contains(e.mousePosition) && (e.mousePosition - _pressPos).sqrMagnitude <= 25f)
                    DetailWindow.Open(id, _view);
            }
        }

        private static void DrawCard(SourceStats s, Rect at, double total, double max)
        {
            var share = total > 0d ? s.Total / total : 0d;
            var jobColor = JobTable.ColorOf(s.ClassType);

            var y = at.y;

            // 名字
            Shadowed(new Rect(at.x, y, CardW, NameH), s.Name ?? "?", _name);
            y += NameH;

            // 主体平行四边形：整块按职能上色（对应 .data-items:before 的 rgba(...,.5)）
            var block = new Rect(at.x, y, CardW, BlockH);
            Skewed(block, new Color(0f, 0f, 0f, 0.30f));
            Skewed(block, With(jobColor, 0.50f));

            // 块内左 DPS、右总量（对应 .dps 和 .dps:last-child）
            Shadowed(new Rect(block.x + 8f, block.y + 2f, CardW * 0.55f, BlockH - 4f),
                     $"{Short(s.Dps)}/s", _dpsLeft);
            Shadowed(new Rect(block.x + CardW * 0.45f - 8f, block.y + 2f, CardW * 0.55f, BlockH - 4f),
                     Short(s.Total), _dpsRight);
            y += BlockH + 1f;

            // 2px 占比条（对应 .damage-percent-bg / -fg，同样斜切）
            var barBg = new Rect(at.x + 2f, y, CardW - 4f, PctBarH);
            Skewed(barBg, new Color(0f, 0f, 0f, 0.30f));
            Skewed(new Rect(barBg.x, barBg.y, barBg.width * (float)share, PctBarH), With(jobColor, 0.70f));
            y += PctBarH + 1f;

            // 占比文字，右对齐
            Shadowed(new Rect(at.x, y, CardW - 6f, PctTextH), $"{share * 100d:0.0}%", _pct);
            y += PctTextH;

            // ACT 风格的补充信息。治疗没有暴击这一说，改显示次数。
            if (_view == TrackerView.Healing)
            {
                // 治疗没有暴击这一说，换成"主要来源 + 次数"更有信息量
                var top = s.TopHealKind;
                var lead = top ?? $"{s.Hits} 次";
                Shadowed(new Rect(at.x, y, CardW - 6f, DetailH),
                         $"{lead}   {s.Hits} 次", _detail);
            }
            else
            {
                var crit = s.Hits > 0 ? $"暴 {s.CritRate * 100d:0}%" : "暴 —";
                Shadowed(new Rect(at.x, y, CardW - 6f, DetailH),
                         $"{crit}   最大 {Short(s.MaxHit)}", _detail);
            }
        }

        // ------------------------------------------------------------------
        // 绘制辅助
        // ------------------------------------------------------------------

        /// <summary>
        /// 画一个 skew(-30deg) 的平行四边形——Horizoverlay 的标志性造型。
        ///
        /// 用横向切片手工拼，而不是 GUI.matrix 做剪切变换：GUI.matrix 工作在**屏幕空间**，
        /// 而窗口回调里传的是**窗口内局部坐标**，两者差一个窗口位移。结果是窗口越往下拖，
        /// 剪切基准偏得越多，内容横向飞出边界——这个 bug 真出现过。
        /// 切片法不依赖任何坐标空间假设，代价是每块多几次 DrawTexture，可以忽略。
        /// </summary>
        private static void Skewed(Rect r, Color color)
        {
            if (r.width <= 0f || r.height <= 0f) return;

            var prev = GUI.color;
            GUI.color = color;

            float deg;
            try { deg = Mathf.Clamp(Mod.Config.SkewDegrees.Value, -60f, 60f); }
            catch { deg = 0f; }

            if (Mathf.Abs(deg) < 0.01f)
            {
                GUI.DrawTexture(r, _white);
            }
            else
            {
                var k = -Mathf.Tan(deg * Mathf.Deg2Rad);
                var cy = r.y + r.height * 0.5f;
                const float step = 1f;

                for (var y = r.y; y < r.yMax; y += step)
                {
                    var h = Mathf.Min(step, r.yMax - y);
                    var dx = k * (y + h * 0.5f - cy);
                    GUI.DrawTexture(new Rect(r.x + dx, y, r.width, h), _white);
                }
            }

            GUI.color = prev;
        }

        /// <summary>带描边的文字。游戏画面在底下一直在动，纯白字很容易糊掉。</summary>
        private static void Shadowed(Rect r, string text, GUIStyle style)
        {
            var prev = GUI.color;

            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 1f, r.y, r.width, r.height), text, style);
            GUI.Label(new Rect(r.x - 1f, r.y, r.width, r.height), text, style);
            GUI.Label(new Rect(r.x, r.y + 1f, r.width, r.height), text, style);
            GUI.Label(new Rect(r.x, r.y - 1f, r.width, r.height), text, style);

            GUI.color = prev;
            GUI.Label(r, text, style);
        }

        private static Color With(Color c, float alpha)
        {
            c.a = alpha;
            return c;
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;

            _white = SolidTexture(Color.white);

            var basis = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                wordWrap = false,
            };

            _name = new GUIStyle(basis) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _dpsLeft = new GUIStyle(basis) { fontSize = 12 };
            _dpsRight = new GUIStyle(basis) { fontSize = 12, alignment = TextAnchor.MiddleRight };
            _pct = new GUIStyle(basis) { fontSize = 9, alignment = TextAnchor.MiddleRight };
            _detail = new GUIStyle(basis) { fontSize = 9, alignment = TextAnchor.MiddleRight };
            _header = new GUIStyle(basis) { fontSize = 11, fontStyle = FontStyle.Bold };
            _headerRight = new GUIStyle(_header) { alignment = TextAnchor.MiddleRight };

            _stylesReady = true;
        }

        private static Texture2D SolidTexture(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        /// <summary>大数字缩写：1234567 -> 1.23M。战斗后期伤害动辄七八位，不缩写根本看不了。</summary>
        public static string Short(double v)
        {
            var abs = Math.Abs(v);
            if (abs >= 1e12) return (v / 1e12).ToString("0.##", CultureInfo.InvariantCulture) + "T";
            if (abs >= 1e9) return (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (abs >= 1e6) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (abs >= 1e3) return (v / 1e3).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
