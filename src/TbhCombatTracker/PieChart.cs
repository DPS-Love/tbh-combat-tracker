using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>饼图的一块。</summary>
    internal struct Slice
    {
        public string Label;
        public double Value;
        public Color Color;
    }

    /// <summary>
    /// 把一组占比画成一张纹理。
    ///
    /// 没用 <c>GL</c> 画三角扇形：GL 在 OnGUI 里要自己管矩阵和材质状态，一旦出错是
    /// 整个界面花屏，排查代价高。逐像素写纹理是纯数据操作，画错了最多是图不好看。
    /// 代价是每次重绘要跑 size² 个像素——所以按数据指纹缓存，数据没变就不重画。
    ///
    /// 纹理按显示尺寸的 2 倍渲染再缩小显示，靠双线性过滤白捡一层抗锯齿。
    /// </summary>
    internal static class PieChart
    {
        private const int Supersample = 2;

        /// <summary>切片配色。色相拉开，且避开面板已用的职业色系不至于混淆。</summary>
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

        public static Color ColorAt(int index)
            => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

        private static Texture2D _tex;
        private static string _signature;
        private static int _texSize;

        /// <summary>
        /// 取一张画好的饼图。<paramref name="displaySize"/> 是显示尺寸，内部按 2 倍渲染。
        /// 数据没变就直接返回上次那张。
        /// </summary>
        public static Texture2D Get(IList<Slice> slices, int displaySize)
        {
            var sig = Signature(slices, displaySize);
            if (_tex != null && sig == _signature) return _tex;

            var size = Mathf.Max(16, displaySize * Supersample);
            if (_tex == null || _texSize != size)
            {
                if (_tex != null) UnityEngine.Object.Destroy(_tex);
                _tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _texSize = size;
            }

            Render(_tex, size, slices);
            _signature = sig;
            return _tex;
        }

        private static void Render(Texture2D tex, int size, IList<Slice> slices)
        {
            double total = 0;
            for (var i = 0; i < slices.Count; i++) total += slices[i].Value;

            var px = new Color32[size * size];
            var transparent = new Color32(0, 0, 0, 0);
            var empty = new Color32(0x30, 0x30, 0x36, 0xC0); // 没数据时的底盘

            var c = size * 0.5f;
            var outer = c - 1f;
            var inner = c * 0.42f;          // 挖空成环，中间留给总计文字
            var outerSq = outer * outer;
            var innerSq = inner * inner;

            // 预累计角度边界，免得每个像素都遍历切片
            var bounds = new float[slices.Count];
            if (total > 0)
            {
                double acc = 0;
                for (var i = 0; i < slices.Count; i++)
                {
                    acc += slices[i].Value;
                    bounds[i] = (float)(acc / total);
                }
            }

            for (var y = 0; y < size; y++)
            {
                var dy = y - c;
                for (var x = 0; x < size; x++)
                {
                    var dx = x - c;
                    var d2 = dx * dx + dy * dy;
                    var idx = y * size + x;

                    if (d2 > outerSq || d2 < innerSq)
                    {
                        px[idx] = transparent;
                        continue;
                    }

                    if (total <= 0)
                    {
                        px[idx] = empty;
                        continue;
                    }

                    // 从 12 点方向顺时针，和常见饼图习惯一致
                    var ang = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;   // 12点=0，顺时针increasing
                    if (ang < 0f) ang += 360f;
                    var t = ang / 360f;

                    var slice = slices.Count - 1;
                    for (var i = 0; i < bounds.Length; i++)
                    {
                        if (t <= bounds[i]) { slice = i; break; }
                    }

                    px[idx] = slices[slice].Color;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
        }

        private static string Signature(IList<Slice> slices, int displaySize)
        {
            // 数值取到 4 位有效数字就够——面板上肉眼分辨不出更细的变化，
            // 但能避免每帧因为末位抖动而重画整张纹理。
            var sb = new System.Text.StringBuilder();
            sb.Append(displaySize).Append('|');
            for (var i = 0; i < slices.Count; i++)
            {
                sb.Append(slices[i].Label).Append(':')
                  .Append(slices[i].Value.ToString("G4")).Append(';');
            }
            return sb.ToString();
        }
    }
}
