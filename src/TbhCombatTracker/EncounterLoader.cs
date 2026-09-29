using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TbhCombatTracker
{
    /// <summary>
    /// 把已卸载的段从本局日志里捞回来。
    ///
    /// 后台线程从头重新解析本局的日志文件——日志记的是事件，重新解析出来的就是当时那一段
    /// （切段设置也在日志里，见 C 事件）。捞到要的那几段就停，不读完整个文件；
    /// 顺手把前后相邻的几段也捞回来，在列表里挨着点不用每次都重读。捞回来的放进一个小缓存，
    /// 不塞回实时会话，免得又把内存涨回去。
    ///
    /// 纯托管、不碰 Unity：后台线程只读文件和解析，结果在主线程的 <see cref="Poll"/> 里取走。
    /// </summary>
    internal static class EncounterLoader
    {
        private const int CacheSize = 12;
        private const int Neighbors = 3;

        /// <summary>最近用过的在前。</summary>
        private static readonly List<Encounter> Cache = new List<Encounter>();
        private static readonly object Sync = new object();

        // 后台线程的结果，主线程 Poll 时取走
        private static List<Encounter> _found;
        private static string _failure;
        private static bool _finished;

        private static int _loading = -1;
        private static int _errorIndex = -1;

        /// <summary>最近一次失败的原因码：notFound / noLog / 异常说明。界面按它选文案。</summary>
        public static string Error { get; private set; }

        /// <summary>这一段现在能拿到的详细数据：内存里的，或者缓存里捞回来的；都没有返回 null。</summary>
        public static Encounter Get(EncounterRecord r)
        {
            if (r == null) return null;
            if (r.Detail != null) return r.Detail;
            for (var i = 0; i < Cache.Count; i++)
            {
                if (!r.Matches(Cache[i])) continue;
                var e = Cache[i];
                if (i > 0)
                {
                    Cache.RemoveAt(i);
                    Cache.Insert(0, e);
                }
                return e;
            }
            return null;
        }

        /// <summary>缓存里有没有（不动缓存的先后，列表绑定时用）。</summary>
        public static bool Has(EncounterRecord r)
        {
            if (r == null) return false;
            if (r.Detail != null) return true;
            foreach (var e in Cache)
                if (r.Matches(e)) return true;
            return false;
        }

        public static bool IsLoading(EncounterRecord r) => r != null && _loading == r.Index;

        public static bool Failed(EncounterRecord r) => r != null && _errorIndex == r.Index;

        /// <summary>开始捞 <paramref name="target"/>（和它前后几段）。已经在捞别的就等那次完了再说。</summary>
        public static void Request(Session live, EncounterRecord target)
        {
            // 失败过的不自动重试（不然每次刷新都重读一遍日志），等玩家点「重试」
            if (target == null || _loading >= 0 || Failed(target) || Get(target) != null) return;

            var path = EventLogWriter.CurrentPath;
            if (!DamageTracker.CanReload || path == null)
            {
                Error = "noLog";
                _errorIndex = target.Index;
                return;
            }

            var at = live.Records.IndexOf(target);
            var wanted = new List<(int Index, double Start)>();
            for (var k = Math.Max(0, at - Neighbors); k <= Math.Min(live.Records.Count - 1, at + Neighbors); k++)
            {
                var r = live.Records[k];
                if (r == target || (r.Detail == null && Get(r) == null)) wanted.Add((r.Index, r.StartTime));
            }
            var last = 0;
            foreach (var w in wanted) last = Math.Max(last, w.Index);

            // 默认值会被日志里的 C 事件盖掉；只留 1 段详细数据，不然长日志会把整局都载进内存
            var options = DamageTracker.ImportOptions();
            options.MaxEncounters = 1;

            _loading = target.Index;
            _errorIndex = -1;
            EventLogWriter.FlushSoon();

            var targetIndex = target.Index;
            Task.Run(() =>
            {
                var found = new List<Encounter>();
                string failure = null;
                try
                {
                    EventLogReader.Read(path, options, e =>
                    {
                        foreach (var w in wanted)
                        {
                            if (w.Index == e.Index && Math.Abs(w.Start - e.StartTime) < 0.0005d)
                            {
                                found.Add(e);
                                break;
                            }
                        }
                        return e.Index < last;
                    });
                    if (!found.Exists(e => e.Index == targetIndex)) failure = "notFound";
                }
                catch (Exception e)
                {
                    failure = e.GetType().Name + ": " + e.Message;
                }

                lock (Sync)
                {
                    _found = found;
                    _failure = failure;
                    _finished = true;
                }
            });
        }

        /// <summary>主线程每帧调一次：把后台捞到的放进缓存。</summary>
        public static void Poll()
        {
            List<Encounter> found;
            string failure;
            lock (Sync)
            {
                if (!_finished) return;
                _finished = false;
                found = _found;
                failure = _failure;
                _found = null;
            }

            var index = _loading;
            _loading = -1;
            foreach (var e in found)
            {
                Cache.Insert(0, e);
                if (Cache.Count > CacheSize) Cache.RemoveAt(Cache.Count - 1);
            }
            if (failure != null)
            {
                Error = failure;
                _errorIndex = index;
                Mod.Log.Warning($"从本局日志载入第 {index} 段失败：{failure}");
            }
        }

        /// <summary>「重试」：忘掉上次的失败，下次刷新时重新捞。缓存只装本局的段，按流水号和开始时间认，不用清。</summary>
        public static void ClearError() => _errorIndex = -1;
    }
}
