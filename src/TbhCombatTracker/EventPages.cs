using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TbhCombatTracker
{
    /// <summary>
    /// "逐条事件"视图的数据：某一段的原始事件，从日志文件里重新读出来。
    ///
    /// 本局实时的那段不走这里（<see cref="DamageTracker.CopyCurrentEvents"/> 在内存里留着）；
    /// 已经结束的段和导入的日志，原始事件只在日志里——后台线程从头解析到那一段结束为止，
    /// 把属于它的伤害 / 承伤 / 治疗事件挑出来，连同当时的单位表、技能表一起交给界面。
    /// 只留最近读的一页：一段战斗的事件最多几万条，看完别的段就换掉。
    ///
    /// 纯托管、不碰 Unity：后台线程只读文件和解析，结果在主线程的 <see cref="Poll"/> 里取走。
    /// </summary>
    internal static class EventPages
    {
        /// <summary>一段读出来的事件，最多留这么多条（一局里罕见的超长段，别把内存撑爆）。</summary>
        public const int MaxEvents = 200000;

        internal sealed class Page
        {
            public string Path;
            public int Index;
            public double Start;
            public readonly List<CombatEvent> Events = new List<CombatEvent>();
            public Dictionary<int, UnitInfo> Units = new Dictionary<int, UnitInfo>();
            public Dictionary<int, AbilityInfo> Abilities = new Dictionary<int, AbilityInfo>();
            /// <summary>失败原因：notFound（日志里对不上这一段）或异常说明；null = 成功。</summary>
            public string Error;
            public bool Capped;
        }

        private static readonly object Sync = new object();
        private static Page _page, _done;
        private static string _loadingPath;
        private static int _loadingIndex = -1;

        public static Page Get(string path, int index, double start)
        {
            var p = _page;
            return p != null && p.Index == index && Math.Abs(p.Start - start) < 0.0005d &&
                   string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)
                ? p
                : null;
        }

        public static bool IsLoading(string path, int index)
            => _loadingIndex == index && string.Equals(_loadingPath, path, StringComparison.OrdinalIgnoreCase);

        /// <summary>开始读 <paramref name="path"/> 里第 <paramref name="index"/> 段的事件。已经在读别的就等那次完了再说。</summary>
        public static void Request(string path, int index, double start)
        {
            if (path == null || _loadingIndex >= 0 || Get(path, index, start) != null) return;

            // 默认值会被日志里的 C 事件盖掉；只留 1 段详细数据，长日志不会把整局都载进内存
            var options = DamageTracker.ImportOptions();
            options.MaxEncounters = 1;

            _loadingPath = path;
            _loadingIndex = index;
            if (string.Equals(path, EventLogWriter.CurrentPath, StringComparison.OrdinalIgnoreCase)) EventLogWriter.FlushSoon();

            Task.Run(() =>
            {
                var page = new Page { Path = path, Index = index, Start = start };
                var matched = false;
                try
                {
                    var session = EventLogReader.Read(path, options, e =>
                    {
                        if (e.Index == index) matched = Math.Abs(e.StartTime - start) < 0.0005d;
                        return e.Index < index;   // 那一段结束就不用往下读了
                    }, (e, s) =>
                    {
                        if (e.Kind != EventKind.Damage && e.Kind != EventKind.Taken && e.Kind != EventKind.Heal) return;
                        if (s.Current == null || s.Current.Index != index) return;
                        if (page.Events.Count < MaxEvents) page.Events.Add(e);
                        else page.Capped = true;
                    });
                    page.Units = new Dictionary<int, UnitInfo>(session.Units);
                    page.Abilities = new Dictionary<int, AbilityInfo>(session.Abilities);
                    if (!matched) page.Error = "notFound";
                }
                catch (Exception e)
                {
                    page.Error = e.GetType().Name + ": " + e.Message;
                }

                lock (Sync) _done = page;
            });
        }

        /// <summary>主线程每帧调一次：把后台读好的那页换上。换了返回 true。</summary>
        public static bool Poll()
        {
            Page done;
            lock (Sync)
            {
                if (_done == null) return false;
                done = _done;
                _done = null;
            }
            _page = done;
            _loadingIndex = -1;
            _loadingPath = null;
            if (done.Error != null) Mod.Log.Warning($"从日志读第 {done.Index} 段的事件失败：{done.Error}");
            return true;
        }
    }
}
