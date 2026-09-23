using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using TbhCombatTracker;

namespace LogCheck
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0)
            {
                Console.WriteLine("用法：logcheck <日志文件> [--idle]    打印会话概要和每段统计");
                Console.WriteLine("      logcheck --test                  跑解析器的合成测试");
                return 2;
            }

            if (args[0] == "--test") return Tests.Run();

            var opt = new CombatParser.Options { SegmentByStage = !args.Contains("--idle") };
            return Summary(args[0], opt);
        }

        private static int Summary(string path, CombatParser.Options opt)
        {
            Session s;
            try { s = EventLogReader.Read(path, opt); }
            catch (Exception e)
            {
                Console.WriteLine($"读取失败：{e.GetType().Name}: {e.Message}");
                return 1;
            }

            Console.WriteLine($"文件：{path}  （{new FileInfo(path).Length / 1024.0:0.0} KB）");
            Console.WriteLine($"格式 v{s.FormatVersion}  " + string.Join("  ", s.Meta.Select(kv => $"{kv.Key}={kv.Value}")));
            Console.WriteLine($"事件 {s.EventCount}（不认识 {s.UnknownLines}，坏行 {s.BadLines}）  " +
                              (s.Truncated ? "末尾不完整（游戏没正常退出，或还在写）" : "完整"));
            var heroes = s.Units.Values.Count(u => u.Kind == 'H');
            var monsters = s.Units.Values.Count(u => u.Kind == 'M');
            Console.WriteLine($"单位 {s.Units.Count}（英雄 {heroes}，怪物 {monsters}，其它 {s.Units.Count - heroes - monsters}），技能 {s.Abilities.Count}");

            if (s.Encounters.Count > 0)
            {
                var first = s.Encounters[0].StartTime;
                var last = s.Encounters[s.Encounters.Count - 1].LastActivityTime;
                var hours = Math.Max((last - first) / 3600d, 1e-6);
                Console.WriteLine($"事件密度 ≈ {s.EventCount / Math.Max(last, 1):0.0} 条/秒，" +
                                  $"文件 ≈ {new FileInfo(path).Length / 1024d / 1024d / hours:0.00} MB/小时");
            }

            Console.WriteLine($"段 {s.Encounters.Count}：");
            foreach (var e in s.Encounters)
            {
                var at = s.StartedAt?.AddSeconds(e.StartTime).ToLocalTime().ToString("HH:mm:ss") ?? $"+{e.StartTime:0}s";
                var top = string.Join("，", e.Outgoing.Values.OrderByDescending(x => x.Total).Take(4)
                                             .Select(x => $"{x.Name ?? (x.InstanceId == 0 ? "未知" : "?")} {Short(x.Total)}"));
                Console.WriteLine($"  #{e.Index,-3} {at}  {Title(e),-14} {e.DurationSeconds,7:0.0}s  " +
                                  $"输出 {Short(e.OutgoingTotal),-8} 承伤 {Short(e.IncomingTotal),-8} 治疗 {Short(e.HealingTotal),-8} [{top}]");
            }
            return 0;
        }

        internal static string Title(Encounter e)
        {
            if (!string.IsNullOrEmpty(e.StageName)) return e.Run > 1 ? $"{e.StageName} #{e.Run}" : e.StageName;
            if (e.StageNo > 0) return $"关卡 #{e.StageNo}";
            return $"#{e.Index}";
        }

        internal static string Short(double v)
        {
            var a = Math.Abs(v);
            if (a >= 1e9) return (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (a >= 1e6) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (a >= 1e3) return (v / 1e3).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>解析器的合成测试。场景都是照着真实日志里见过的信号顺序编的。</summary>
    internal static class Tests
    {
        private static int _pass, _fail;

        private static void Check(bool ok, string what)
        {
            if (ok) { _pass++; return; }
            _fail++;
            Console.WriteLine("  FAIL " + what);
        }

        public static int Run()
        {
            Run("关卡分段：启动、自动重复、先翻标志后改名、按名字换关、去抖、模板名", StageSegmentation);
            Run("空闲分段", IdleSegmentation);
            Run("手动重置与保留上限", ResetAndCap);
            Run("归因：技能、暴击 / 类型 / 元素、治疗来源", Attribution);
            Run("写入再读回：与直接解析逐项一致", RoundTrip);
            Run("截断的日志：读到哪算哪，不抛异常", Truncated);
            Run("向前兼容：不认识的事件和多出来的字段", ForwardCompat);
            Run("文本转义", Escaping);

            Console.WriteLine(_fail == 0 ? $"全部通过（{_pass} 项检查）" : $"{_fail} 项失败，{_pass} 项通过");
            return _fail == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            Console.WriteLine("· " + name);
            try { test(); }
            catch (Exception e) { _fail++; Console.WriteLine("  FAIL 抛异常：" + e); }
        }

        // ------------------------------------------------------------------ 事件构造

        private const int Ranger = -51234, Priest = -51300, Mage = -51402, Mob = -60012;

        private static CombatEvent U(double t, int id, char kind, int cls, string key, string name)
            => new CombatEvent { T = t, Kind = EventKind.Unit, A = id, UnitKind = kind, ClassType = cls, Key = key, Text = name };

        private static CombatEvent Ab(double t, int aid, string key, string name)
            => new CombatEvent { T = t, Kind = EventKind.Ability, A = aid, Key = key, Text = name };

        private static CombatEvent D(double t, int src, float amount, int crit = -1, int type = -1, int attr = -1,
                                     int aid = 0, int tgt = Mob)
            => new CombatEvent
            {
                T = t, Kind = EventKind.Damage, A = src, B = tgt, Amount = amount,
                Origin = crit >= 0 ? amount * 1.25f : float.NaN,
                Crit = (sbyte)crit, DamageType = type, Attribute = attr, Ability = aid,
            };

        private static CombatEvent Tk(double t, int victim, float amount, int src = Mob)
            => new CombatEvent
            {
                T = t, Kind = EventKind.Taken, A = victim, B = src, Amount = amount,
                Origin = float.NaN, Crit = -1, DamageType = -1, Attribute = -1,
            };

        private static CombatEvent H(double t, int target, int caster, float amount, int kind,
                                     sbyte b = -1, sbyte c = -1, int bracket = 0)
            => new CombatEvent
            {
                T = t, Kind = EventKind.Heal, A = target, B = caster, Amount = amount,
                HealKind = kind, FlagB = b, FlagC = c, Bracket = bracket,
            };

        private static CombatEvent Name(double t, string n) => new CombatEvent { T = t, Kind = EventKind.StageName, Text = n };
        private static CombatEvent Start(double t, bool v) => new CombatEvent { T = t, Kind = EventKind.StageStart, Flag = v };
        private static CombatEvent Wave(double t, string s) => new CombatEvent { T = t, Kind = EventKind.Wave, Text = s };
        private static CombatEvent Reset(double t) => new CombatEvent { T = t, Kind = EventKind.Reset };

        private static Session Parse(IEnumerable<CombatEvent> events, CombatParser.Options opt, bool finish = true)
        {
            var s = new Session();
            var p = new CombatParser(s, opt);
            foreach (var e in events) p.Apply(e);
            if (finish) p.Finish();
            return s;
        }

        /// <summary>照真实日志编的一局：启动 → 3-2 打两遍 → 换到 3-3（先翻标志后改名）→ 按名字换到 3-4。</summary>
        private static List<CombatEvent> Scenario()
        {
            var ev = new List<CombatEvent>
            {
                U(0.1, Ranger, 'H', 2, "Hero_201", "游侠"),
                U(0.1, Priest, 'H', 4, "Hero_401", "牧师"),
                U(0.1, Mob, 'M', 0, "Monster_30043", "Monster_30043"),
                Ab(0.1, 3, "SkillName_20101", "快速射击"),
                Name(0.2, "关卡 3-2"),          // 第一次读到名字：不切段
                Start(0.3, false),              // 第一次读到标志：不切段
                Start(1.0, true),               // false → true：切段，用已知的名字
                Wave(1.0, "MONSTERSPAWN"),
            };
            for (var i = 0; i < 100; i++) ev.Add(D(2 + i * 2.5, Ranger, 1000 + i, crit: i % 3 == 0 ? 1 : 0, type: 2, attr: 1, aid: 3));
            ev.Add(Tk(50, Priest, 300));
            ev.Add(H(51, Priest, 0, 120, 1, 1, 0, 1));

            ev.Add(Start(272, false));
            ev.Add(Start(273, true));           // 自动重复同一关 → 关卡 3-2 #2
            for (var i = 0; i < 50; i++) ev.Add(D(274 + i * 5, Ranger, 2000));

            ev.Add(Start(541, false));
            ev.Add(Start(542, true));           // 先翻标志……
            ev.Add(Name(543.5, "关卡 3-3"));    // ……1.5 秒后才刷新关卡名：改名而不是再切一段
            for (var i = 0; i < 10; i++) ev.Add(D(544 + i, Ranger, 500));

            ev.Add(Name(600, "关卡 3-4"));      // 按名字换关
            ev.Add(Start(600.5, false));
            ev.Add(Start(601, true));           // 1 秒后又翻标志：在去抖期内，不再切
            ev.Add(D(605, Ranger, 777));
            ev.Add(Name(610, "Stage {0}-{1}")); // 本地化没填充时的模板，不算换关
            ev.Add(D(611, Ranger, 1));
            return ev;
        }

        // ------------------------------------------------------------------ 测试

        private static void StageSegmentation()
        {
            var s = Parse(Scenario(), new CombatParser.Options());
            var titles = s.Encounters.Select(Program.Title).ToArray();
            Check(titles.SequenceEqual(new[] { "关卡 3-2", "关卡 3-2 #2", "关卡 3-3", "关卡 3-4" }),
                  "段标题应为 关卡 3-2 / 关卡 3-2 #2 / 关卡 3-3 / 关卡 3-4，实际 " + string.Join(" / ", titles));

            var e1 = s.Encounters[0];
            Check(Math.Abs(e1.StartTime - 1.0) < 1e-9, $"第一段从标志翻转时（1.0s）开始，实际 {e1.StartTime}");
            Check(e1.Outgoing[Ranger].Hits == 100, $"第一段游侠 100 次命中，实际 {e1.Outgoing[Ranger].Hits}");
            Check(s.Encounters[1].Outgoing[Ranger].Total == 50 * 2000d, "第二段游侠总伤害 100000");
            Check(s.Encounters[2].Outgoing[Ranger].Total == 5000d, "3-3 那段游侠总伤害 5000");
            Check(s.Encounters[3].Outgoing[Ranger].Total == 778d, "3-4 那段游侠总伤害 778（模板名没切段）");
        }

        private static void IdleSegmentation()
        {
            var ev = new List<CombatEvent>
            {
                U(0, Ranger, 'H', 2, "Hero_201", "游侠"),
                D(1, Ranger, 10), D(2, Ranger, 10), D(3, Ranger, 10),
                Name(5, "关卡 3-2"), Start(6, true),       // 空闲分段模式下关卡信号不切段
                D(20, Ranger, 5),                          // 距上次 17 秒 ≥ 8：新的一段
                D(25, Ranger, 5),
            };
            var s = Parse(ev, new CombatParser.Options { SegmentByStage = false, IdleSeconds = 8f });
            Check(s.Encounters.Count == 2, $"应切成 2 段，实际 {s.Encounters.Count}");
            Check(s.Encounters[0].StartTime == 1 && s.Encounters[0].LastActivityTime == 3, "第一段 1s–3s");
            Check(s.Encounters[1].StartTime == 20 && s.Encounters[1].Outgoing[Ranger].Total == 10, "第二段从 20s 起，总量 10");
        }

        private static void ResetAndCap()
        {
            var ev = new List<CombatEvent>
            {
                D(1, Ranger, 1), Reset(2), D(3, Ranger, 2), Reset(4), Reset(4.5),   // 空段重置不留记录
                D(5, Ranger, 3), Reset(6), D(7, Ranger, 4),
            };
            var s = Parse(ev, new CombatParser.Options { MaxEncounters = 2 }, finish: false);
            Check(s.Encounters.Count == 2 && s.DroppedEncounters == 1,
                  $"上限 2：保留 2 段、丢 1 段，实际保留 {s.Encounters.Count}、丢 {s.DroppedEncounters}");
            Check(s.Encounters[0].Outgoing[Ranger].Total == 2 && s.Encounters[1].Outgoing[Ranger].Total == 3,
                  "丢的是最旧的那段");
            Check(s.Current.Outgoing[Ranger].Total == 4, "进行中的那段不受影响");
        }

        private static void Attribution()
        {
            var ev = new List<CombatEvent>
            {
                U(0, Ranger, 'H', 2, "Hero_201", "游侠"),
                U(0, Priest, 'H', 4, "Hero_401", "牧师"),
                Ab(0, 3, "SkillName_20101", "快速射击"),
                D(1, Ranger, 100, crit: 1, type: 2, attr: 1, aid: 3),
                D(2, Ranger, 50, crit: 0, type: 6, attr: 2, aid: 3),     // 类型带两个位：两个桶都记
                D(3, Ranger, 30),                                        // 没有分类上下文：不算暴击/类型/元素，不记技能
                D(4, Ranger, 20, aid: 99),                               // 没定义过的技能编号
                D(5, 0, 7),                                              // 未知来源
                H(6, Priest, 0, 40, 1, 1, 0, 1),                         // 自愈：归牧师自己
                H(7, Ranger, Priest, 60, 3, 1, 1, 3),                    // 技能治疗：归施法者牧师
                H(8, Ranger, 0, 5, 0),                                   // 自然回复：归游侠自己
                D(9, Ranger, 0f), D(9, Ranger, float.NaN),               // 0 和 NaN 丢弃
            };
            var s = Parse(ev, new CombatParser.Options());
            var r = s.Encounters[0].Outgoing[Ranger];
            Check(r.Total == 200 && r.Hits == 4, $"游侠 4 次共 200，实际 {r.Hits} 次 {r.Total}");
            Check(r.Crits == 1 && Math.Abs(r.CritRate - 0.25) < 1e-9, $"暴击 1 次（分母是全部命中），实际 {r.Crits}");
            Check(r.ByType[2] == 150 && r.ByType[3] == 50, $"投射物 150、AOE 50，实际 {r.ByType[2]} / {r.ByType[3]}");
            Check(r.ByAttribute[1] == 100 && r.ByAttribute[2] == 50, "火 100、冰 50");
            Check(r.BySkill.TryGetValue("快速射击", out var sk) && sk.Total == 150 && sk.Hits == 2 && sk.Crits == 1 && sk.Max == 100,
                  "快速射击 2 次 150，暴击 1 次，最高 100");
            Check(r.BySkill.ContainsKey("#99") && r.BySkill.Count == 2, "没定义过的技能显示成 #99，无技能的那次不进技能表");
            Check(s.Encounters[0].Outgoing[0].Total == 7 && s.Encounters[0].Outgoing[0].Name == null, "未知来源记在 id 0 下");
            var heal = s.Encounters[0].Healing;
            Check(heal[Priest].Total == 100 && heal[Priest].ByHealKind[3] == 60 && heal[Priest].ByHealKind[1] == 40,
                  "牧师：自愈 40（战斗回复）+ 给游侠的治愈 60");
            Check(heal[Ranger].Total == 5 && heal[Ranger].TopHealKind == 0, "游侠：自然回复 5");
            Check(r.Name == "游侠" && r.ClassType == 2, "名字和职业来自单位定义");
        }

        private static void RoundTrip()
        {
            // 记录端会先把金额和时间规整成日志里的写法，这里照做，然后要求逐项一致
            var events = Scenario().Select(Canon).ToList();
            events.Insert(0, U(0.05, Mage, 'H', 3, "Hero_301", "法|师\\测试\n名"));
            events.Add(H(612, Priest, 0, 33.337f, 1, 1, 0, 1));
            events.Add(Reset(613));
            events.Add(D(614, Mage, 12345.678f, 1, 4, 3));
            events = events.Select(Canon).ToList();

            var direct = Parse(events, new CombatParser.Options());

            var bytes = WriteLog(events, gzip: true, end: true, flushAt: -1);
            var read = new Session();
            using (var ms = new MemoryStream(bytes)) EventLogReader.Read(ms, read, new CombatParser.Options());

            Check(!read.Truncated && read.BadLines == 0 && read.UnknownLines == 0, "完整读回，没有坏行");
            Check(read.EventCount == direct.EventCount, $"事件数 {read.EventCount} vs {direct.EventCount}");
            Check(read.Meta.TryGetValue("mod", out var mod) && mod == "0.3.0" && read.StartedAt.HasValue, "meta 读回");
            var diff = Diff(direct, read);
            Check(diff == null, "读回与直接解析不一致：" + diff);
        }

        private static void Truncated()
        {
            var events = Scenario().Select(Canon).ToList();
            var half = events.Count / 2;

            // gzip：前一半写完做一次同步刷新（写日志的线程每 2 秒做一次），后一半没刷新就"断电"
            var bytes = WriteLog(events, gzip: true, end: false, flushAt: half);
            var s = new Session();
            using (var ms = new MemoryStream(bytes)) EventLogReader.Read(ms, s, new CombatParser.Options());
            Check(s.Truncated, "没有 #END：标记为不完整");
            Check(s.EventCount >= half && s.EventCount <= events.Count, $"至少读到刷新前的 {half} 条，实际 {s.EventCount}");

            // 纯文本：最后一行被截在半截
            var text = Encoding.UTF8.GetString(WriteLog(events, gzip: false, end: false, flushAt: -1));
            var cut = text.Substring(0, text.Length - 9);
            var s2 = new Session();
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(cut))) EventLogReader.Read(ms, s2, new CombatParser.Options());
            Check(s2.Truncated && s2.EventCount == events.Count - 1 && s2.BadLines == 1,
                  $"半截的末行记为坏行、不参与统计：读到 {s2.EventCount}/{events.Count - 1}，坏行 {s2.BadLines}");
        }

        private static void ForwardCompat()
        {
            var log = string.Join("\n", new[]
            {
                "#TBHLOG|3",
                "#META|mod=9.9.9|future=yes",
                "# 注释行",
                "0.100|U|-51234|H|2|Hero_201|游侠|将来加的字段",
                "1.000|Z|这是以后才有的事件",
                "1.500|S|weather|rain",
                "2.000|D|-51234|-60012|500|625|1|2|1||extra|more",
                "#END|events=4",
            });
            var s = new Session();
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(log))) EventLogReader.Read(ms, s, new CombatParser.Options());
            Check(s.FormatVersion == 3 && s.UnknownLines == 2 && s.BadLines == 0 && !s.Truncated,
                  $"未来版本：跳过 2 行不认识的，实际跳过 {s.UnknownLines}、坏行 {s.BadLines}");
            Check(s.Encounters.Count == 1 && s.Encounters[0].Outgoing[-51234].Total == 500 &&
                  s.Encounters[0].Outgoing[-51234].Crits == 1, "多出来的字段忽略，事件照常解析");

            var threw = false;
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("hello\nworld\n")))
                    EventLogReader.Read(ms, new Session(), new CombatParser.Options());
            }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "不是战斗日志的文件：明确报错");
        }

        private static void Escaping()
        {
            foreach (var raw in new[] { "a|b", "back\\slash", "line\nbreak", "\\p 不是转义", "", "普通文本" })
            {
                var back = EventLogFormat.Unescape(EventLogFormat.Escape(raw));
                Check(back == raw, $"转义往返：'{raw}' → '{back}'");
            }
        }

        // ------------------------------------------------------------------ 工具

        private static CombatEvent Canon(CombatEvent e)
        {
            e.T = EventLogFormat.CanonicalTime(e.T);
            e.Amount = EventLogFormat.CanonicalAmount(e.Amount);
            if (!float.IsNaN(e.Origin)) e.Origin = EventLogFormat.CanonicalAmount(e.Origin);
            return e;
        }

        private static byte[] WriteLog(List<CombatEvent> events, bool gzip, bool end, int flushAt)
        {
            var ms = new MemoryStream();
            Stream sink = gzip ? new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true) : (Stream)ms;
            var w = new StreamWriter(sink, new UTF8Encoding(false)) { NewLine = "\n" };
            w.WriteLine(EventLogFormat.HeaderLine());
            w.WriteLine(EventLogFormat.MetaLine(new Dictionary<string, string>
            {
                ["mod"] = "0.3.0", ["game"] = "1.2.8", ["start"] = "2026-09-23T11:22:08.123+08:00",
            }));
            var sb = new StringBuilder();
            for (var i = 0; i < events.Count; i++)
            {
                sb.Clear();
                EventLogFormat.Append(sb, events[i]);
                w.WriteLine(sb.ToString());
                if (i == flushAt)
                {
                    w.Flush();
                    sink.Flush();
                    // 模拟进程被杀：刷新点之后的数据还在压缩器里，文件里只有到刷新点为止的内容
                    return ms.ToArray();
                }
            }
            if (end) w.WriteLine(EventLogFormat.EndLine(events.Count));
            w.Flush();
            if (gzip) sink.Dispose();
            return ms.ToArray();
        }

        private static string Diff(Session a, Session b)
        {
            if (a.Encounters.Count != b.Encounters.Count) return $"段数 {a.Encounters.Count} vs {b.Encounters.Count}";
            for (var i = 0; i < a.Encounters.Count; i++)
            {
                var x = a.Encounters[i]; var y = b.Encounters[i];
                if (x.Index != y.Index || x.StageName != y.StageName || x.Run != y.Run || x.StageNo != y.StageNo ||
                    x.StartTime != y.StartTime || x.LastActivityTime != y.LastActivityTime)
                    return $"第 {i} 段头部不同：{Program.Title(x)} {x.StartTime}-{x.LastActivityTime} vs {Program.Title(y)} {y.StartTime}-{y.LastActivityTime}";
                foreach (TrackerView v in Enum.GetValues(typeof(TrackerView)))
                {
                    var bx = x.Bucket(v); var by = y.Bucket(v);
                    if (bx.Count != by.Count) return $"第 {i} 段 {v} 来源数 {bx.Count} vs {by.Count}";
                    foreach (var kv in bx)
                    {
                        if (!by.TryGetValue(kv.Key, out var sy)) return $"第 {i} 段 {v} 缺来源 {kv.Key}";
                        var sx = kv.Value;
                        if (sx.Total != sy.Total || sx.Hits != sy.Hits || sx.Crits != sy.Crits || sx.MaxHit != sy.MaxHit ||
                            sx.Name != sy.Name || sx.ClassType != sy.ClassType ||
                            sx.FirstHitTime != sy.FirstHitTime || sx.LastHitTime != sy.LastHitTime)
                            return $"第 {i} 段 {v} 来源 {kv.Key} 汇总不同：{sx.Total}/{sx.Hits} vs {sy.Total}/{sy.Hits}";
                        if (!sx.ByAttribute.SequenceEqual(sy.ByAttribute) || !sx.ByType.SequenceEqual(sy.ByType) ||
                            !sx.ByHealKind.SequenceEqual(sy.ByHealKind) || !sx.PerSecond.SequenceEqual(sy.PerSecond))
                            return $"第 {i} 段 {v} 来源 {kv.Key} 分桶不同";
                        if (sx.BySkill.Count != sy.BySkill.Count ||
                            sx.BySkill.Any(k => !sy.BySkill.TryGetValue(k.Key, out var o) || !o.Equals(k.Value)))
                            return $"第 {i} 段 {v} 来源 {kv.Key} 技能表不同";
                    }
                }
            }
            if (a.Units.Count != b.Units.Count || a.Units.Any(k => !b.Units.TryGetValue(k.Key, out var u) ||
                                                                    u.Name != k.Value.Name || u.Key != k.Value.Key))
                return "单位表不同";
            if (a.Abilities.Count != b.Abilities.Count) return "技能表不同";
            return null;
        }
    }
}
