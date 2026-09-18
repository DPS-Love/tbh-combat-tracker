using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;

namespace TbhCombatTracker
{
    /// <summary>
    /// 更新检查与一键更新。清单只负责通知，任何动作都由玩家在横幅上点按钮触发。
    ///
    /// 为什么要有这个：v0.2.1 在游戏 1.2.2 上会让所有生命恢复失效，而装了它的玩家
    /// 没有任何渠道知道这件事——除非自己去翻 GitHub。这类"必须立刻知道"的问题需要
    /// 一条从维护者到玩家的通路。
    ///
    /// 通路就是仓库 main 分支上的 <c>manifest.json</c>：
    /// <code>
    /// latest  —— 最新版本、构建时的游戏版本、下载地址、SHA-256、是否重要、说明
    /// broken  —— 已知会出问题的（Mod 版本区间 × 游戏版本区间）
    /// </code>
    /// 启动时用 HTTP GET 读一次，只读、不上传任何东西。国内先走 jsDelivr（可达且有 CDN），
    /// 不通再试 GitHub 原始文件；都不通就静默放弃，日志里留一行。
    ///
    /// 【线程】检查和下载都在后台线程。后台线程**绝不能碰任何 Il2Cpp 对象**（包括
    /// <c>Strings.Chinese</c> 这种会读 LocalizationSettings 的东西），所以这里只存
    /// 结构化事实（版本号、两种语言的文案），由 <see cref="UpdateBanner"/> 在主线程上拼字符串。
    ///
    /// 【清单只能通知，不能动作】哪怕清单把当前版本标记为 broken，这里也只是把事实
    /// 摆到横幅上——是继续用、点「停用」、还是点「更新」，全由玩家自己决定。
    /// 曾经做过"被点名就自动卸 hook"的远程安全模式，后来去掉了：一个本地统计工具
    /// 不应该有任何"别人能远程改变它行为"的性质，哪怕只是往关的方向。
    ///
    /// 【自更新】下载 zip → 校验 SHA-256（清单里没有哈希就拒绝更新）→ 抠出 DLL →
    /// 把正在运行的 DLL 改名为 .old（Windows 允许改名已加载的文件）→ 写入新文件。
    /// 下次启动生效；启动时顺手删掉 .old。新版本若加载失败，把 .old 改回去即可回滚。
    /// </summary>
    internal static class UpdateChecker
    {
        private static readonly string[] ManifestUrls =
        {
            "https://cdn.jsdelivr.net/gh/DPS-Love/tbh-combat-tracker@main/manifest.json",
            "https://raw.githubusercontent.com/DPS-Love/tbh-combat-tracker/main/manifest.json",
        };

        private const string DllName = "TbhCombatTracker.dll";

        public enum State { Idle, Checking, UpToDate, UpdateAvailable, Failed }

        // ---- 供主线程读的事实。后台线程只写这些简单字段，不做任何 UI/Il2Cpp 操作 ----
        public static volatile State Current = State.Idle;
        public static volatile Version Mine;
        public static volatile Version Game;            // 实际游戏版本（Version.txt），读不到为 null
        public static volatile Version BuiltFor;        // 本 DLL 构建时的游戏版本，"?" 时为 null
        public static volatile Version Latest;
        public static volatile string LatestNotesZh;
        public static volatile string LatestNotesEn;
        public static volatile bool LatestCritical;
        public static volatile string ReleaseUrl;
        public static volatile bool CanInstall;         // 有下载地址且有 SHA-256
        public static volatile bool CurrentBroken;
        public static volatile string BrokenReasonZh;
        public static volatile string BrokenReasonEn;
        public static volatile bool Installing;
        public static volatile bool Installed;          // 已换好文件，下次启动生效
        public static volatile string InstallError;
        /// <summary>主 hook 没挂上，统计不可用——最常见的原因是游戏更新改了混淆名。
        /// 这条不依赖网络也不依赖 Version.txt，是"Mod 和游戏对不上"最直接的信号。</summary>
        public static volatile bool CoreHookFailed;

        /// <summary>游戏比本 DLL 构建时新——本地就能判断，不需要网络。</summary>
        public static bool GameNewerThanBuild =>
            Game != null && BuiltFor != null && Game > BuiltFor;

        private static LatestInfo _latest;

        // ------------------------------------------------------------------ 入口

        public static void Start()
        {
            try
            {
                CleanupOld();
                Mine = ParseVersion(BuildInfo.Version);
                BuiltFor = ParseVersion(BuildInfo.GameVersion);
                Game = ReadGameVersion();

                if (GameNewerThanBuild)
                    Mod.Log.Warning($"游戏已更新到 {Game}，本版 Mod 是为 {BuiltFor} 构建的，" +
                                    "混淆名可能已变化；hook 失配只会让统计缺失，不会影响游戏。");
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"读取版本信息失败：{e.GetType().Name}: {e.Message}");
            }

            if (!Mod.Config.CheckUpdates.Value)
            {
                Mod.Log.Msg("更新检查已在配置里关闭。");
                return;
            }

            Current = State.Checking;
            Task.Run(CheckAsync);
        }

        /// <summary>面板上的「更新」按钮：此时才下载、校验、替换文件。</summary>
        public static void RequestInstall()
        {
            if (Installing || Installed || !CanInstall) return;
            Installing = true;
            InstallError = null;
            Task.Run(InstallAsync);
        }

        // ------------------------------------------------------------------ 检查

        private static async Task CheckAsync()
        {
            try
            {
                using var http = NewHttp(TimeSpan.FromSeconds(8));
                Manifest manifest = null;
                foreach (var url in ManifestSources())
                {
                    try
                    {
                        var json = await FetchText(http, url).ConfigureAwait(false);
                        manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
                        if (manifest?.latest != null)
                        {
                            Mod.Log.Msg($"更新清单来源：{url}");
                            break;
                        }
                        manifest = null;
                    }
                    catch (Exception e)
                    {
                        Mod.Log.Msg($"更新清单 {Host(url)} 不可达：{e.GetType().Name}");
                    }
                }

                if (manifest == null)
                {
                    Current = State.Failed;
                    Mod.Log.Msg("更新检查失败（网络不通或清单格式不对），本次跳过。");
                    return;
                }

                Evaluate(manifest);
            }
            catch (Exception e)
            {
                Current = State.Failed;
                Mod.Log.Warning($"更新检查出错：{e}");
            }
        }

        private static void Evaluate(Manifest m)
        {
            _latest = m.latest;
            Latest = ParseVersion(m.latest.version);
            LatestNotesZh = m.latest.notes?.zh;
            LatestNotesEn = m.latest.notes?.en;
            LatestCritical = m.latest.critical;
            ReleaseUrl = m.latest.url;
            CanInstall = !string.IsNullOrWhiteSpace(m.latest.download)
                      && !string.IsNullOrWhiteSpace(m.latest.sha256);

            // 先看自己是不是被点名了——这比"有没有新版本"重要得多
            if (m.broken != null && Mine != null)
            {
                foreach (var b in m.broken)
                {
                    if (b == null || !b.Matches(Mine, Game)) continue;
                    CurrentBroken = true;
                    BrokenReasonZh = b.reason?.zh;
                    BrokenReasonEn = b.reason?.en;
                    Mod.Log.Error($"更新清单标记：v{Mine} 在游戏 {Game?.ToString() ?? "?"} 上会出问题——" +
                                  $"{b.reason?.zh ?? b.reason?.en ?? "（无说明）"}。" +
                                  "面板横幅上可以选择停用本次统计或更新；不做任何自动处理。");
                    break;
                }
            }

            if (Latest != null && Mine != null && Latest > Mine)
            {
                Current = State.UpdateAvailable;
                Mod.Log.Warning($"有新版本 v{Latest}{(LatestCritical ? "（重要）" : "")}：" +
                                $"{LatestNotesZh ?? LatestNotesEn ?? ""}  {ReleaseUrl}");

                if (Mod.Config.AutoInstall.Value && CanInstall)
                {
                    Installing = true;
                    _ = InstallAsync();
                }
            }
            else
            {
                Current = State.UpToDate;
                Mod.Log.Msg($"已是最新版本（v{Mine}）。");
            }
        }

        // ------------------------------------------------------------------ 更新（下载 + 校验 + 替换）

        private static async Task InstallAsync()
        {
            try
            {
                var latest = _latest;
                if (latest == null) throw new InvalidOperationException("没有清单");

                using var http = NewHttp(TimeSpan.FromSeconds(60));
                var zip = await FetchBytes(http, latest.download).ConfigureAwait(false);

                // 没有哈希就不装：宁可让玩家手动下载，也不执行没法校验的代码
                var actual = Sha256Hex(zip);
                if (!string.Equals(actual, latest.sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 不匹配：清单 {latest.sha256}，实际 {actual}");

                var dllBytes = ExtractDll(zip);
                var dllPath = CurrentDllPath();
                var oldPath = dllPath + ".old";

                // Windows 允许给正在被加载的文件改名，但不允许覆盖写。
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(dllPath, oldPath);
                try
                {
                    File.WriteAllBytes(dllPath, dllBytes);
                }
                catch
                {
                    // 写入失败就把原来的改回去，别留下一个没有 DLL 的 plugins 目录
                    try { if (!File.Exists(dllPath)) File.Move(oldPath, dllPath); } catch { }
                    throw;
                }

                Installed = true;
                Mod.Log.Warning($"已更新到 v{latest.version}（{dllPath}），下次启动游戏生效。" +
                                $"如新版本加载失败，把 {Path.GetFileName(oldPath)} 改回 {DllName} 即可回滚。");
            }
            catch (Exception e)
            {
                InstallError = $"{e.GetType().Name}: {e.Message}";
                Mod.Log.Error($"自动更新失败：{e}");
            }
            finally
            {
                Installing = false;
            }
        }

        private static byte[] ExtractDll(byte[] zipBytes)
        {
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (!string.Equals(entry.Name, DllName, StringComparison.OrdinalIgnoreCase)) continue;
                using var es = entry.Open();
                using var buf = new MemoryStream();
                es.CopyTo(buf);
                return buf.ToArray();
            }
            throw new FileNotFoundException($"发布包里没有 {DllName}");
        }

        private static string CurrentDllPath()
        {
            try
            {
                var loc = typeof(Plugin).Assembly.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc)) return loc;
            }
            catch { /* 有的加载方式下 Location 是空的 */ }
            return Path.Combine(Paths.PluginPath, DllName);
        }

        /// <summary>上次自更新留下的 .old，启动时清掉。</summary>
        private static void CleanupOld()
        {
            try
            {
                var old = CurrentDllPath() + ".old";
                if (File.Exists(old))
                {
                    File.Delete(old);
                    Mod.Log.Msg("已清理上次更新留下的旧版本文件。");
                }
            }
            catch (Exception e)
            {
                Mod.Log.Msg($"清理 .old 失败（不影响运行）：{e.GetType().Name}");
            }
        }

        // ------------------------------------------------------------------ 小工具

        /// <summary>配置里填了地址就只用它（镜像或本地测试清单），否则官方列表。</summary>
        private static string[] ManifestSources()
        {
            string custom = null;
            try { custom = Mod.Config.ManifestUrl.Value?.Trim(); } catch { }
            return string.IsNullOrEmpty(custom) ? ManifestUrls : new[] { custom };
        }

        /// <summary>
        /// 本地路径也当作来源：发布前用 tools/test-manifest.ps1 生成的测试清单，
        /// 或内网镜像。HttpClient 不认 file:，所以自己分流。
        /// </summary>
        private static bool IsLocal(string src)
            => src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || (!src.Contains("://") && Path.IsPathRooted(src));

        private static string LocalPath(string src)
            => src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(src).LocalPath : src;

        private static async Task<string> FetchText(HttpClient http, string src)
            => IsLocal(src)
                ? await File.ReadAllTextAsync(LocalPath(src)).ConfigureAwait(false)
                : await http.GetStringAsync(src).ConfigureAwait(false);

        private static async Task<byte[]> FetchBytes(HttpClient http, string src)
            => IsLocal(src)
                ? await File.ReadAllBytesAsync(LocalPath(src)).ConfigureAwait(false)
                : await http.GetByteArrayAsync(src).ConfigureAwait(false);

        private static HttpClient NewHttp(TimeSpan timeout)
        {
            // 走系统代理（HttpClient 默认行为）——国内玩家开着 Clash 之类的就能通
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"TbhCombatTracker/{BuildInfo.Version}");
            return http;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static Version ReadGameVersion()
        {
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "Version.txt");
                return File.Exists(path) ? ParseVersion(File.ReadAllText(path)) : null;
            }
            catch { return null; }
        }

        /// <summary>"1.01.05" / "1.2.4" / "0.2.3" 都能解；解不出返回 null，绝不抛。</summary>
        private static Version ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            // Version 至少要两段
            if (s.IndexOf('.') < 0) s += ".0";
            return Version.TryParse(s, out var v) ? v : null;
        }

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string Host(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }

        // ------------------------------------------------------------------ 清单结构

        private sealed class Manifest
        {
            public int schema { get; set; }
            public LatestInfo latest { get; set; }
            public Broken[] broken { get; set; }
        }

        private sealed class LatestInfo
        {
            public string version { get; set; }
            public string gameVersion { get; set; }
            public string url { get; set; }
            public string download { get; set; }
            public string sha256 { get; set; }
            public bool critical { get; set; }
            public Notes notes { get; set; }
        }

        private sealed class Notes
        {
            public string zh { get; set; }
            public string en { get; set; }
        }

        /// <summary>Mod 版本区间 × 游戏版本区间，边界都是闭区间，留空表示不限。</summary>
        private sealed class Broken
        {
            public string modMin { get; set; }
            public string modMax { get; set; }
            public string gameMin { get; set; }
            public string gameMax { get; set; }
            public Notes reason { get; set; }

            public bool Matches(Version mod, Version game)
            {
                var lo = ParseVersion(modMin); var hi = ParseVersion(modMax);
                if (lo != null && mod < lo) return false;
                if (hi != null && mod > hi) return false;

                var glo = ParseVersion(gameMin); var ghi = ParseVersion(gameMax);
                if (glo != null || ghi != null)
                {
                    if (game == null) return false;   // 读不到游戏版本就不武断
                    if (glo != null && game < glo) return false;
                    if (ghi != null && game > ghi) return false;
                }
                return true;
            }
        }
    }
}
