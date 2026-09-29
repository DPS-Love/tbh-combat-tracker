using System;
using System.Globalization;

namespace TbhCombatTracker
{
    /// <summary>界面和日志里的数字格式。都用不变区域性，免得系统区域设置把小数点变成逗号。</summary>
    internal static class Fmt
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>大数字缩写：1234567 → 1.23M。战斗后期伤害动辄七八位，不缩写根本看不了。</summary>
        public static string Short(double v)
        {
            var abs = Math.Abs(v);
            if (abs >= 1e12) return (v / 1e12).ToString("0.##", Inv) + "T";
            if (abs >= 1e9) return (v / 1e9).ToString("0.##", Inv) + "B";
            if (abs >= 1e6) return (v / 1e6).ToString("0.##", Inv) + "M";
            if (abs >= 1e3) return (v / 1e3).ToString("0.##", Inv) + "K";
            return v.ToString("0.#", Inv);
        }

        /// <summary>时长：2:31、1:02:03。</summary>
        public static string Dur(double seconds)
        {
            var s = (int)Math.Round(Math.Max(0d, seconds));
            return s >= 3600
                ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}"
                : $"{s / 60}:{s % 60:00}";
        }

        public static string Pct(double v) => (v * 100d).ToString("0.0", Inv) + "%";

        public static string Count(long n) => n.ToString("N0", Inv);
    }
}
