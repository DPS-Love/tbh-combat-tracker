using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 在托管侧拼一张网格，再一次性交给 <see cref="Shape"/>（CanvasRenderer.SetMesh）。
    /// 曲线、环形图、斜切色块都用它画：几何直接进 Canvas 的合批，GPU 光栅化，
    /// 不再逐像素写纹理——旧版的锯齿和"切维度要等一秒"都来自那张纹理。
    ///
    /// 抗锯齿靠"羽化边"：每条边外侧再铺一圈 alpha 从 1 降到 0 的窄带，宽度正好一个屏幕像素（<see cref="Px"/>）。
    /// 坐标系和界面布局一致：左上原点、y 向下、单位是缩放前的界面单位。
    /// </summary>
    internal sealed class MeshBuilder
    {
        /// <summary>UI 用 16 位索引，一张网格最多 65535 个顶点；留点余量。</summary>
        public const int MaxVertices = 60000;

        private readonly List<Vector3> _v = new List<Vector3>(1024);
        private readonly List<Color32> _c = new List<Color32>(1024);
        private readonly List<int> _i = new List<int>(3072);

        // 交给 Mesh 的 il2cpp 数组常驻复用，只在不够大时重新分配
        private Il2CppStructArray<Vector3> _va;
        private Il2CppStructArray<Color32> _ca;
        private Il2CppStructArray<Vector2> _uva;
        private Il2CppStructArray<int> _ia;

        /// <summary>一个屏幕像素折合多少界面单位（= 1 / 缩放），羽化带就这么宽。</summary>
        public float Px = 1f;

        public int VertexCount => _v.Count;

        public void Clear()
        {
            _v.Clear();
            _c.Clear();
            _i.Clear();
        }

        private bool Room(int verts) => _v.Count + verts <= MaxVertices;

        private int V(float x, float y, Color32 c)
        {
            _v.Add(new Vector3(x, -y, 0f));
            _c.Add(c);
            return _v.Count - 1;
        }

        private void Tri(int a, int b, int c)
        {
            _i.Add(a);
            _i.Add(b);
            _i.Add(c);
        }

        private void Quad(int a, int b, int c, int d)
        {
            Tri(a, b, c);
            Tri(a, c, d);
        }

        private static Color32 Alpha(Color32 c, byte a)
        {
            c.a = a;
            return c;
        }

        // ================================================================== 基本形状

        /// <summary>实心矩形，不羽化（像素对齐的条、块用它最清楚）。</summary>
        public void Rect(float x, float y, float w, float h, Color color)
        {
            if (w <= 0f || h <= 0f || !Room(4)) return;
            Color32 c = color;
            var a = V(x, y, c);
            var b = V(x + w, y, c);
            var d = V(x + w, y + h, c);
            var e = V(x, y + h, c);
            Quad(a, b, d, e);
        }

        /// <summary>
        /// 斜切的平行四边形（浮窗卡片的色块）：第 y 行水平偏移 <c>-tan(deg) × (y - 中线)</c>，
        /// 和旧版逐行切片的造型一致。四条边都羽化。
        /// <paramref name="skewCenterY"/> 是斜切的中线（不给就是自己的中线）：卡片的色块和下面的占比条
        /// 用同一条中线，斜边才在一条直线上。
        /// </summary>
        public void Parallelogram(float x, float y, float w, float h, float deg, Color color, float skewCenterY = float.NaN)
        {
            if (w <= 0f || h <= 0f || !Room(16)) return;

            var k = Slope(deg);
            var cy = float.IsNaN(skewCenterY) ? y + h * 0.5f : skewCenterY;
            var f = Px;
            // 斜边的垂直羽化宽度是 f，换成水平方向要除以 cos
            var fx = f * Mathf.Sqrt(1f + k * k);

            Color32 on = color;
            var off = Alpha(on, 0);

            var ys = new[] { y - f, y, y + h, y + h + f };
            var idx = new int[16];
            for (var r = 0; r < 4; r++)
            {
                var dx = k * (ys[r] - cy);
                var edgeRow = r == 0 || r == 3;
                var xs0 = x + dx;
                var xs1 = x + w + dx;
                idx[r * 4 + 0] = V(xs0 - fx, ys[r], off);
                idx[r * 4 + 1] = V(xs0, ys[r], edgeRow ? off : on);
                idx[r * 4 + 2] = V(xs1, ys[r], edgeRow ? off : on);
                idx[r * 4 + 3] = V(xs1 + fx, ys[r], off);
            }
            for (var r = 0; r < 3; r++)
                for (var col = 0; col < 3; col++)
                    Quad(idx[r * 4 + col], idx[r * 4 + col + 1], idx[(r + 1) * 4 + col + 1], idx[(r + 1) * 4 + col]);
        }

        /// <summary>斜切角度对应的水平偏移率：第 y 行比中线右移 <c>Slope × (y - 中线)</c>。</summary>
        public static float Slope(float deg) => -Mathf.Tan(Mathf.Clamp(deg, -75f, 75f) * Mathf.Deg2Rad);

        // ================================================================== 折线与面积

        /// <summary>
        /// 抗锯齿折线：每个点沿法线铺 4 个顶点（外羽化 / 线身 / 线身 / 外羽化），相邻点之间连 3 条带。
        /// 拐角用斜接（miter），夹角很尖时限制长度，免得尖刺冲出去。
        /// </summary>
        public void Polyline(IList<Vector2> pts, int count, float width, Color color)
        {
            if (count < 2 || !Room(count * 4)) return;

            Color32 on = color;
            var off = Alpha(on, 0);
            var hw = Mathf.Max(width * 0.5f, Px * 0.5f);
            var f = Px;

            var first = _v.Count;
            for (var i = 0; i < count; i++)
            {
                var p = pts[i];
                Vector2 dirIn = Vector2.zero, dirOut = Vector2.zero;
                if (i > 0) dirIn = Norm(p - pts[i - 1]);
                if (i < count - 1) dirOut = Norm(pts[i + 1] - p);
                var t = Norm(dirIn + dirOut);
                if (t == Vector2.zero) t = dirIn != Vector2.zero ? dirIn : dirOut != Vector2.zero ? dirOut : Vector2.right;

                var n = new Vector2(-t.y, t.x);
                // 斜接长度 = 半宽 / cos(半夹角)，夹角太尖时封顶
                var seg = dirIn != Vector2.zero ? dirIn : dirOut;
                var cos = Mathf.Abs(Vector2.Dot(n, new Vector2(-seg.y, seg.x)));
                var m = hw / Mathf.Max(cos, 0.4f);

                V(p.x + n.x * (m + f), p.y + n.y * (m + f), off);
                V(p.x + n.x * m, p.y + n.y * m, on);
                V(p.x - n.x * m, p.y - n.y * m, on);
                V(p.x - n.x * (m + f), p.y - n.y * (m + f), off);
            }
            for (var i = 0; i < count - 1; i++)
            {
                var a = first + i * 4;
                var b = a + 4;
                Quad(a, b, b + 1, a + 1);
                Quad(a + 1, b + 1, b + 2, a + 2);
                Quad(a + 2, b + 2, b + 3, a + 3);
            }
        }

        /// <summary>折线下方到 <paramref name="baseY"/> 的面积，上下渐变。上沿不羽化——折线会盖住它。</summary>
        public void Area(IList<Vector2> pts, int count, float baseY, Color top, Color bottom)
        {
            if (count < 2 || !Room(count * 2)) return;
            Color32 ct = top, cb = bottom;
            var first = _v.Count;
            for (var i = 0; i < count; i++)
            {
                V(pts[i].x, pts[i].y, ct);
                V(pts[i].x, baseY, cb);
            }
            for (var i = 0; i < count - 1; i++)
            {
                var a = first + i * 2;
                Quad(a, a + 2, a + 3, a + 1);
            }
        }

        // ================================================================== 环形图

        /// <summary>
        /// 环形的一段：角度从 12 点方向起、顺时针，单位弧度。内外圆和两端都羽化；
        /// <paramref name="gap"/> 是两段之间的缝（界面单位，沿切线方向平移，内外圈缝宽一致）。
        /// </summary>
        public void Ring(float cx, float cy, float rInner, float rOuter, float a0, float a1, Color color, float gap = 0f)
        {
            var sweep = a1 - a0;
            if (sweep <= 0f || rOuter <= rInner) return;

            // 太窄的一段（内圈弧长连缝都不够）画不出来，图例里照样列着
            if (sweep * rInner <= gap + Px && sweep < Mathf.PI * 2f - 0.001f) return;

            var full = sweep >= Mathf.PI * 2f - 0.001f;
            if (full) gap = 0f;

            var steps = Mathf.Max(2, Mathf.CeilToInt(sweep / (Mathf.PI * 2f) * 160f));
            var cols = steps + 1 + (full ? 0 : 2);
            if (!Room(cols * 4)) return;

            Color32 on = color;
            var off = Alpha(on, 0);
            var f = Px;
            var radii = new[] { rOuter + f, rOuter, rInner, rInner - f };

            var first = _v.Count;
            void Column(float a, float shift, bool fade)
            {
                var sx = Mathf.Sin(a);
                var sy = -Mathf.Cos(a);
                var tx = Mathf.Cos(a);    // 顺时针方向的切线
                var ty = Mathf.Sin(a);
                for (var k = 0; k < 4; k++)
                {
                    var r = Mathf.Max(0f, radii[k]);
                    var edge = fade || k == 0 || k == 3;
                    V(cx + sx * r + tx * shift, cy + sy * r + ty * shift, edge ? off : on);
                }
            }

            var half = gap * 0.5f;
            if (!full) Column(a0, half - f, true);
            for (var s = 0; s <= steps; s++)
            {
                var a = a0 + sweep * s / steps;
                var shift = full ? 0f : s == 0 ? half : s == steps ? -half : 0f;
                Column(a, shift, false);
            }
            if (!full) Column(a1, -half + f, true);

            // 整圈时最后一列落在第一列上，不用回绕
            for (var s = 0; s < cols - 1; s++)
            {
                var a = first + s * 4;
                var b = a + 4;
                for (var k = 0; k < 3; k++) Quad(a + k, b + k, b + k + 1, a + k + 1);
            }
        }

        private static Vector2 Norm(Vector2 v)
        {
            var len = Mathf.Sqrt(v.x * v.x + v.y * v.y);
            return len > 1e-6f ? v / len : Vector2.zero;
        }

        // ================================================================== 提交

        /// <summary>把拼好的几何写进 <paramref name="mesh"/>。il2cpp 侧的数组复用，不够才扩容。</summary>
        public void ApplyTo(Mesh mesh)
        {
            mesh.Clear();
            var n = _v.Count;
            var m = _i.Count;
            if (n == 0 || m == 0) return;

            if (_va == null || _va.Length < n)
            {
                var cap = Math.Max(n, (_va?.Length ?? 256) * 2);
                _va = new Il2CppStructArray<Vector3>(cap);
                _ca = new Il2CppStructArray<Color32>(cap);
                _uva = new Il2CppStructArray<Vector2>(cap);   // 全零：白纹理上任何位置都一样
            }
            if (_ia == null || _ia.Length < m)
                _ia = new Il2CppStructArray<int>(Math.Max(m, (_ia?.Length ?? 512) * 2));

            for (var i = 0; i < n; i++)
            {
                _va[i] = _v[i];
                _ca[i] = _c[i];
            }
            for (var i = 0; i < m; i++) _ia[i] = _i[i];

            mesh.SetVertices(_va, 0, n);
            mesh.SetColors(_ca, 0, n);
            mesh.SetUVs(0, _uva, 0, n);
            mesh.SetTriangles(_ia, 0, m, 0, true, 0);
        }
    }
}
