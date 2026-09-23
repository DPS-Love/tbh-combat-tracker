using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 点击角色卡片后弹出的明细窗口：一张环形图 + 图例，按维度切换。
    ///
    /// 维度随主面板当前视图变化：
    ///   输出 / 承伤 → 技能、伤害类型、元素属性
    ///   治疗        → 恢复来源（技能维度对治疗没意义，直接不显示）
    /// </summary>
    internal static class DetailWindow
    {
        private const int WindowId = 0x7B4E;   // 和主面板错开

        private const float PieSize = 116f;
        private const float Pad = 10f;
        private const float RowH = 15f;
        private const float TabH = 18f;

        /// <summary>当前展开的来源实例 id；0 表示没开。</summary>
        private static int _targetId;
        /// <summary>来源的名字。翻到它没数据的那一段时窗口标题还要用。</summary>
        private static string _targetName;
        private static TrackerView _targetView;
        private static Dimension _dim = Dimension.Skill;
        private static Rect _rect = new Rect(40f, 160f, 330f, 210f);

        private static readonly PieChart Pie = new PieChart();

        private static GUIStyle _title, _tab, _legend, _legendRight, _center, _sub;
        private static Texture2D _white;
        private static bool _stylesReady;

        public static bool IsOpen => _targetId != 0;

        /// <summary>明细窗口的 GUI 矩形，给射线靶同步用——不然点击会穿透过去。</summary>
        public static Rect CurrentRect => _rect;

        public static void Open(int sourceId, TrackerView view, string name)
        {
            // 再点一次同一张卡片就关掉，符合直觉
            if (_targetId == sourceId && _targetView == view) { Close(); return; }

            _targetId = sourceId;
            _targetView = view;
            _targetName = name;
            _dim = view == TrackerView.Healing ? Dimension.HealKind : Dimension.Skill;
        }

        public static void Close() => _targetId = 0;

        public static void Draw()
        {
            if (_targetId == 0) return;

            EnsureStyles();

            // 跟着实时的当前段走。刚切段、这个角色在新的一段里还没出手时显示空状态，
            // 不自动关窗——几秒后数据就回来了；已经结束的段在主面板里看
            var stats = Find();
            if (stats != null) _targetName = Strings.SourceName(stats);

            _rect = GUI.Window(WindowId, _rect, (GUI.WindowFunction)DrawBody,
                               Strings.DetailTitle(_targetName ?? "?", Strings.ViewLabel(_targetView),
                                                   Strings.EncounterTitle(DamageTracker.Current)));
        }

        private static SourceStats Find()
        {
            var bucket = DamageTracker.Current.Bucket(_targetView);
            return bucket.TryGetValue(_targetId, out var s) ? s : null;
        }

        private static void DrawBody(int id)
        {
            try
            {
                var stats = Find();

                var top = Pad + 6f;

                // ---- 维度切换 ----
                if (_targetView == TrackerView.Healing)
                {
                    Shadowed(new Rect(Pad, top, 120f, TabH), Strings.TabHealSources, _tab);
                }
                else
                {
                    DrawTab(new Rect(Pad, top, 56f, TabH), Strings.TabSkills, Dimension.Skill);
                    DrawTab(new Rect(Pad + 60f, top, 56f, TabH), Strings.TabTypes, Dimension.DamageType);
                    DrawTab(new Rect(Pad + 120f, top, 56f, TabH), Strings.TabElements, Dimension.Attribute);
                }

                if (GUI.Button(new Rect(_rect.width - Pad - 22f, top, 22f, TabH), "×"))
                {
                    Close();
                    return;
                }

                top += TabH + 6f;

                if (stats == null)
                {
                    Shadowed(new Rect(Pad, top, _rect.width - Pad * 2f, RowH), Strings.NoDataInSegment, _legend);
                    GUI.DragWindow();
                    return;
                }

                // ---- 数据 ----
                var slices = Breakdown.Slices(Breakdown.Of(stats, _dim));
                double total = 0;
                foreach (var s in slices) total += s.Value;

                // ---- 环形图 ----
                var pieRect = new Rect(Pad, top, PieSize, PieSize);
                var tex = Pie.Get(slices, (int)PieSize);
                GUI.DrawTexture(pieRect, tex);

                // 环心放总计
                Shadowed(new Rect(pieRect.x, pieRect.y + PieSize * 0.5f - 16f, PieSize, 16f),
                         Overlay.Short(stats.Total), _center);
                Shadowed(new Rect(pieRect.x, pieRect.y + PieSize * 0.5f, PieSize, 14f),
                         $"{Overlay.Short(stats.Dps)}/s", _sub);

                // ---- 图例 ----
                var lx = Pad + PieSize + 10f;
                var lw = _rect.width - lx - Pad;
                var ly = top;

                if (slices.Count == 0)
                {
                    Shadowed(new Rect(lx, ly, lw, RowH), Strings.NoBreakdown, _legend);
                }
                else
                {
                    var shown = Mathf.Min(slices.Count, 7);
                    for (var i = 0; i < shown; i++)
                    {
                        var s = slices[i];
                        var share = total > 0d ? s.Value / total : 0d;

                        var sw = new Rect(lx, ly + 3f, 8f, 8f);
                        var prev = GUI.color;
                        GUI.color = s.Color;
                        GUI.DrawTexture(sw, _white);
                        GUI.color = prev;

                        Shadowed(new Rect(lx + 12f, ly, lw * 0.52f, RowH), s.Label, _legend);
                        Shadowed(new Rect(lx, ly, lw, RowH),
                                 $"{Overlay.Short(s.Value)}  {share * 100d:0.0}%", _legendRight);
                        ly += RowH;
                    }

                    if (slices.Count > shown)
                        Shadowed(new Rect(lx + 12f, ly, lw, RowH),
                                 Strings.MoreItems(slices.Count - shown), _legend);
                }

                GUI.DragWindow();
            }
            catch (Exception e)
            {
                Mod.Log.Error($"明细窗口绘制失败，已关闭：{e}");
                Close();
            }
        }

        private static void DrawTab(Rect r, string label, Dimension dim)
        {
            var on = _dim == dim;
            var prev = GUI.color;
            if (on)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.22f);
                GUI.DrawTexture(r, _white);
                GUI.color = prev;
            }
            if (GUI.Button(r, label, _tab)) _dim = dim;
        }

        private static void Shadowed(Rect r, string text, GUIStyle style)
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 1f, r.y, r.width, r.height), text, style);
            GUI.Label(new Rect(r.x - 1f, r.y, r.width, r.height), text, style);
            GUI.Label(new Rect(r.x, r.y + 1f, r.width, r.height), text, style);
            GUI.color = prev;
            GUI.Label(r, text, style);
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;

            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _white.hideFlags = HideFlags.HideAndDontSave;

            var basis = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
                wordWrap = false,
            };

            _title = new GUIStyle(basis) { fontStyle = FontStyle.Bold };
            _tab = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
            };
            _legend = new GUIStyle(basis) { fontSize = 10 };
            _legendRight = new GUIStyle(basis) { fontSize = 10, alignment = TextAnchor.MiddleRight };
            _center = new GUIStyle(basis) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _sub = new GUIStyle(basis) { fontSize = 10, alignment = TextAnchor.MiddleCenter };

            _stylesReady = true;
        }
    }
}
