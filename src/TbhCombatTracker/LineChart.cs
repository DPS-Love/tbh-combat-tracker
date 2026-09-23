using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 一组折线画成一张纹理：主面板里"每秒数值随时间变化"的那张图。
    ///
    /// 和 <see cref="PieChart"/> 一个思路：逐像素写纹理，不用 GL（在 OnGUI 里管 GL 的矩阵和材质状态，
    /// 出错是整个界面花屏）。数据没变就不重画；实时那段的数据每帧都在变，最多每秒重画一次。
    /// </summary>
    internal sealed class LineChart
    {
        public struct Series
        {
            /// <summary>每个点一个值，所有折线点数相同，横轴均匀铺满。</summary>
            public float[] Values;
            public Color Color;
            /// <summary>加粗画在最上面：主面板里选中的那个角色。</summary>
            public bool Bold;
        }

        private Texture2D _tex;
        private int _w, _h;
        private string _signature;
        private float _renderedAt = -999f;

        /// <summary>要不要重画。实时数据（throttle）即使变了也最多每秒画一次。</summary>
        public bool Stale(string signature, int w, int h, bool throttle)
        {
            if (_tex == null || _w != w || _h != h) return true;
            if (signature == _signature) return false;
            return !throttle || Time.unscaledTime - _renderedAt >= 1f;
        }

        public Texture2D Texture => _tex;

        public Texture2D Render(IList<Series> series, int w, int h, double yMax, string signature)
        {
            w = Math.Max(8, w);
            h = Math.Max(8, h);
            if (_tex == null || _w != w || _h != h)
            {
                if (_tex != null) UnityEngine.Object.Destroy(_tex);
                _tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _w = w;
                _h = h;
            }

            var px = new Color32[w * h];   // 默认全透明，底色由调用方画

            // 参考线：25% / 50% / 75%
            var grid = new Color32(255, 255, 255, 26);
            for (var k = 1; k <= 3; k++)
            {
                var y = (int)Math.Round((h - 3) * k / 4.0) + 1;
                for (var x = 0; x < w; x++) px[y * w + x] = grid;
            }

            if (yMax <= 0d) yMax = 1d;

            // 先画普通的，加粗的最后画，压在最上面
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var s in series)
                {
                    if (s.Bold != (pass == 1) || s.Values == null || s.Values.Length == 0) continue;
                    Plot(px, w, h, s, yMax);
                }
            }

            _tex.SetPixels32(px);
            _tex.Apply(false, false);
            _signature = signature;
            _renderedAt = Time.unscaledTime;
            return _tex;
        }

        private static void Plot(Color32[] px, int w, int h, Series s, double yMax)
        {
            var c = (Color32)s.Color;
            c.a = s.Bold ? (byte)255 : (byte)200;
            var v = s.Values;
            var n = v.Length;
            var prev = -1;

            for (var x = 0; x < w; x++)
            {
                // 按列采样，相邻两点之间线性插值
                var pos = n == 1 ? 0d : x * (n - 1) / (double)(w - 1);
                var i0 = (int)pos;
                var i1 = Math.Min(i0 + 1, n - 1);
                var val = v[i0] + (v[i1] - v[i0]) * (pos - i0);

                var y = (int)Math.Round(val / yMax * (h - 3)) + 1;   // 纹理 y 轴朝上，正好是图的方向
                if (y < 0) y = 0; else if (y > h - 1) y = h - 1;

                // 和上一列之间连成竖线，陡坡才不会断开
                var lo = prev < 0 ? y : Math.Min(prev, y);
                var hi = prev < 0 ? y : Math.Max(prev, y);
                if (s.Bold) { lo--; hi++; }
                for (var yy = Math.Max(0, lo); yy <= Math.Min(h - 1, hi); yy++) px[yy * w + x] = c;
                prev = y;
            }
        }
    }
}
