using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace TbhCombatTracker
{
    /// <summary>
    /// 把战斗事件写进本局的日志文件：<c>BepInEx\TbhCombatTracker\logs\tbh-yyyyMMdd-HHmmss.tbhlog.gz</c>。
    ///
    /// 主线程只往队列里塞一个结构体；格式化、压缩、写盘都在后台线程。
    /// 每 2 秒做一次 gzip 同步刷新——游戏被强杀时最多丢 2 秒，文件也仍然能读（读到最后一次刷新为止）。
    /// 正常退出时写结束标记 <c>#END</c> 并关闭 gzip。
    ///
    /// 任何 IO 错误都只停掉日志、打一行警告，实时统计照常工作——日志坏了不能连累面板，更不能连累游戏。
    /// </summary>
    internal static class EventLogWriter
    {
        /// <summary>后台线程卡住（比如磁盘满）时队列不能无限涨；超过就丢事件，日志末尾会标记。</summary>
        private const int MaxQueued = 200_000;
        private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

        private static readonly ConcurrentQueue<CombatEvent> Queue = new ConcurrentQueue<CombatEvent>();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);

        private static Thread _thread;
        private static volatile bool _running;
        private static volatile bool _flushRequested;
        private static StreamWriter _writer;
        private static Stream _gzip;
        private static long _written;
        private static long _dropped;

        public static string LogDirectory { get; private set; }
        public static string CurrentPath { get; private set; }
        public static bool Running => _running;

        public static void Start(string directory, IEnumerable<KeyValuePair<string, string>> meta, int retentionDays)
        {
            if (_running) return;
            try
            {
                LogDirectory = directory;
                System.IO.Directory.CreateDirectory(directory);
                CleanupOld(directory, retentionDays);

                var path = Path.Combine(directory, $"tbh-{DateTime.Now:yyyyMMdd-HHmmss}.tbhlog.gz");
                // 同一秒内重启过（极少见）就加个后缀，别覆盖上一局
                for (var i = 2; File.Exists(path); i++)
                    path = Path.Combine(directory, $"tbh-{DateTime.Now:yyyyMMdd-HHmmss}-{i}.tbhlog.gz");

                // 允许别人同时读：主面板导入"本局"的日志时就是边写边读
                var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                _gzip = new GZipStream(fs, CompressionLevel.Optimal);
                _writer = new StreamWriter(_gzip, new UTF8Encoding(false), 64 * 1024) { NewLine = "\n" };
                _writer.WriteLine(EventLogFormat.HeaderLine());
                _writer.WriteLine(EventLogFormat.MetaLine(meta));
                _writer.Flush();
                _gzip.Flush();

                CurrentPath = path;
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "TbhCombatTracker.EventLog" };
                _thread.Start();
                Mod.Log.Msg($"战斗日志：{path}");
            }
            catch (Exception e)
            {
                _running = false;
                CloseQuietly();
                Mod.Log.Warning($"战斗日志无法写入，本次只做实时统计：{e.GetType().Name}: {e.Message}");
            }
        }

        public static void Enqueue(in CombatEvent e)
        {
            if (!_running) return;
            if (Queue.Count >= MaxQueued)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            Queue.Enqueue(e);
        }

        /// <summary>尽快落盘（导入本局日志前调用，好读到最新的数据）。不等待。</summary>
        public static void FlushSoon()
        {
            if (!_running) return;
            _flushRequested = true;
            Signal.Set();
        }

        /// <summary>游戏退出：写完剩下的、写结束标记、关文件。最多等 3 秒。</summary>
        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            Signal.Set();
            try { _thread?.Join(3000); } catch { /* 退出阶段不吵闹 */ }
        }

        private static void Loop()
        {
            // 后台线程上的未处理异常会直接带走整个游戏进程，所以这里必须全包住
            try
            {
                var sb = new StringBuilder(256);
                var sinceFlush = Stopwatch.StartNew();

                while (_running)
                {
                    Signal.WaitOne(500);
                    Drain(sb);
                    if (_flushRequested || sinceFlush.Elapsed >= FlushEvery)
                    {
                        _flushRequested = false;
                        FlushToDisk();
                        sinceFlush.Restart();
                    }
                }

                Drain(sb);
                if (_dropped > 0) _writer.WriteLine($"# 日志写入跟不上，丢弃了 {_dropped} 条事件");
                _writer.WriteLine(EventLogFormat.EndLine(_written));
                _writer.Flush();
                CloseQuietly();   // 关闭 gzip 时写入文件尾
            }
            catch (Exception e)
            {
                _running = false;
                try { Mod.Log.Warning($"战斗日志写入出错，已停止写日志（实时统计不受影响）：{e.GetType().Name}: {e.Message}"); }
                catch { /* 连日志都打不出来就算了 */ }
                CloseQuietly();
            }
        }

        private static void Drain(StringBuilder sb)
        {
            while (Queue.TryDequeue(out var e))
            {
                sb.Clear();
                EventLogFormat.Append(sb, e);
                _writer.WriteLine(sb.ToString());
                _written++;
            }
        }

        private static void FlushToDisk()
        {
            _writer.Flush();
            _gzip.Flush();   // .NET 的 DeflateStream.Flush 是同步刷新：到这里为止的数据都能解出来
        }

        private static void CloseQuietly()
        {
            try { _writer?.Dispose(); } catch { }
            try { _gzip?.Dispose(); } catch { }
            _writer = null;
            _gzip = null;
        }

        /// <summary>删掉超过保留天数的旧日志。只动我们自己命名的文件。</summary>
        private static void CleanupOld(string directory, int retentionDays)
        {
            if (retentionDays <= 0) return;
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            var removed = 0;
            foreach (var f in new DirectoryInfo(directory).GetFiles("tbh-*.tbhlog*"))
            {
                try
                {
                    if (f.LastWriteTime >= cutoff) continue;
                    f.Delete();
                    removed++;
                }
                catch { /* 被占用之类，下次再说 */ }
            }
            if (removed > 0) Mod.Log.Msg($"已删除 {removed} 个超过 {retentionDays} 天的旧战斗日志。");
        }
    }
}
