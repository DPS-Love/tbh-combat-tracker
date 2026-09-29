namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 图标：Segoe Fluent Icons（Win11）/ Segoe MDL2 Assets（Win10）的码位，两套字体里这些码位是同一个图形。
    /// 系统里两个都没有（比如在 Wine 下跑）时，<see cref="Of"/> 退回一个文字符号，按钮照样能认。
    /// </summary>
    internal static class Glyphs
    {
        public const char ChevronUp = (char)0xE70E;
        public const char ChevronDown = (char)0xE70D;
        public const char ChevronLeft = (char)0xE76B;
        public const char ChevronRight = (char)0xE76C;
        public const char Close = (char)0xE8BB;
        public const char Cancel = (char)0xE711;
        public const char Settings = (char)0xE713;
        public const char FolderOpen = (char)0xE838;
        public const char OpenFile = (char)0xE8E5;
        public const char Export = (char)0xEDE1;
        public const char Download = (char)0xE896;
        public const char Save = (char)0xE74E;
        public const char Refresh = (char)0xE72C;
        public const char HardDrive = (char)0xEDA2;
        /// <summary>"存在硬盘上"：软盘比硬盘字形在小字号下好认得多。</summary>
        public const char OnDisk = (char)0xE74E;
        public const char History = (char)0xE81C;
        public const char List = (char)0xE8FD;
        public const char Info = (char)0xE946;
        public const char CheckMark = (char)0xE73E;
        public const char Keyboard = (char)0xE765;
        public const char Color = (char)0xE790;
        public const char AreaChart = (char)0xE9D2;
        public const char Warning = (char)0xE7BA;
        public const char Error = (char)0xE783;
        public const char Completed = (char)0xE930;
        public const char Sync = (char)0xE895;
        public const char OpenInNew = (char)0xE8A7;
        public const char Stopwatch = (char)0xE916;
        public const char Shield = (char)0xEA18;
        public const char Heart = (char)0xEB51;
        public const char Sliders = (char)0xE9E9;
        public const char Speed = (char)0xEC4A;
        public const char Document = (char)0xE9F9;

        /// <summary>图标字体可用时原样返回，否则给一个意思相近的文字符号。</summary>
        public static string Of(char glyph)
        {
            if (UiKit.HasIcons) return glyph.ToString();
            switch (glyph)
            {
                case ChevronUp: return "▲";
                case ChevronDown: return "▼";
                case ChevronLeft: return "◀";
                case ChevronRight: return "▶";
                case Close:
                case Cancel: return "×";
                case Settings: return "⚙";
                case FolderOpen: return "▤";
                case OpenFile: return "⇪";
                case Export:
                case Download:
                case Save: return "⇩";
                case Refresh:
                case Sync: return "↻";
                case HardDrive: return "◌";
                case History:
                case List: return "≡";
                case CheckMark: return "✓";
                case Warning:
                case Error: return "!";
                case Completed: return "✓";
                case OpenInNew: return "↗";
                default: return "•";
            }
        }
    }
}
