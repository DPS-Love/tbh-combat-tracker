using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TbhCombatTracker
{
    /// <summary>
    /// 读日志文件，交给 <see cref="CombatParser"/> 重新解析。纯 .NET，可以在后台线程跑。
    ///
    /// 容错：
    ///   · gzip 和纯文本都认（看开头两个字节）
    ///   · 游戏没正常退出时文件末尾不完整——读到哪算哪，<see cref="Session.Truncated"/> 标记出来
    ///   · 正在写的日志也能读（FileShare.ReadWrite），读到最近一次落盘为止
    ///   · 不认识的事件类型跳过，缺字段按"不知道"处理
    /// </summary>
    public static class EventLogReader
    {
        public static Session Read(string path, CombatParser.Options options)
        {
            var session = new Session { SourcePath = path };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete))
            {
                Read(fs, session, options);
            }
            return session;
        }

        public static void Read(Stream raw, Session session, CombatParser.Options options)
        {
            var b0 = raw.ReadByte();
            var b1 = raw.ReadByte();
            raw.Position = 0;

            var gz = b0 == 0x1F && b1 == 0x8B;
            var input = gz ? new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true) : raw;
            try
            {
                using (var reader = new StreamReader(input, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true))
                {
                    var parser = new CombatParser(session, options);
                    ReadLines(reader, session, parser);
                    parser.Finish();
                }
            }
            finally
            {
                if (gz) input.Dispose();
            }
        }

        public static void ReadLines(TextReader reader, Session session, CombatParser parser)
        {
            var sawHeader = false;
            session.Truncated = true;   // 读到 #END 才算完整

            while (true)
            {
                string line;
                try
                {
                    line = reader.ReadLine();
                }
                catch (InvalidDataException) { break; }   // 压缩流被截断
                catch (EndOfStreamException) { break; }
                catch (IOException) { break; }

                if (line == null) break;

                switch (EventLogFormat.Parse(line, out var e))
                {
                    case EventLogFormat.LineKind.Event:
                        if (!sawHeader) throw new InvalidDataException("不是 TBH 战斗日志（缺少 #TBHLOG 文件头）");
                        parser.Apply(e);
                        break;

                    case EventLogFormat.LineKind.Header:
                        if (EventLogFormat.TryParseHeader(line, out var version))
                        {
                            sawHeader = true;
                            session.FormatVersion = version;
                        }
                        break;

                    case EventLogFormat.LineKind.Meta:
                        EventLogFormat.ParseMeta(line, session.Meta);
                        if (session.Meta.TryGetValue("start", out var start) &&
                            DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture,
                                                    DateTimeStyles.RoundtripKind, out var at))
                            session.StartedAt = at;
                        break;

                    case EventLogFormat.LineKind.End:
                        session.Truncated = false;
                        break;

                    case EventLogFormat.LineKind.Unknown:
                        session.UnknownLines++;
                        break;

                    case EventLogFormat.LineKind.Bad:
                        session.BadLines++;
                        break;
                }
            }

            if (!sawHeader) throw new InvalidDataException("不是 TBH 战斗日志（缺少 #TBHLOG 文件头）");
        }
    }
}
