using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace TbhCombatTracker
{
    [BepInPlugin(PluginGuid, PluginName, BuildInfo.Version)]
    [BepInProcess("TaskBarHero.exe")]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "dpslove.tbh.combattracker";
        public const string PluginName = "TBH Combat Tracker";

        // 版本号来自构建时生成的 BuildInfo（csproj 的 <Version>），不再手写——
        // v0.2.2 的启动日志还打着 "v0.1.0"，就是手写常量忘了同步。
        public const string PluginVersion = BuildInfo.Version;

        private static Harmony _harmony;
        private static bool _disabled;

        public override void Load()
        {
            Mod.Init(Log, Config);
            Mod.Log.Msg($"{PluginName} v{BuildInfo.Version}（构建时游戏 {BuildInfo.GameVersion}）启动中…");

            // 手动打补丁而不是用 [HarmonyPatch] 特性 + PatchAll：
            // 某个 hook 因为游戏更新失配时，其余 hook 仍然能工作，而不是整个插件崩掉。
            _harmony = new Harmony(PluginGuid);
            var coreOk = false;
            try
            {
                coreOk = Patches.ApplyAll(_harmony);
            }
            catch (Exception e)
            {
                // 兜底：TryPatch 已经按 hook 隔离了类型加载失败，这里接的是 Patches 类本身
                // 加载不了之类的情况。无论如何，下面的窗口和更新检查都必须继续。
                Mod.Log.Error($"挂载 hook 时出错，统计不可用（游戏很可能已更新）：{e.GetType().Name}: {e.Message}");
            }
            UpdateChecker.CoreHookFailed = !coreOk;

            // BasePlugin 本身不是 MonoBehaviour，拿不到 Update / OnGUI。
            // AddComponent 会把这个类注册进 IL2CPP 域并挂到一个常驻 GameObject 上。
            AddComponent<TrackerBehaviour>();

            // 放在最后：它只读文件和发一个 GET，失败也不影响上面任何东西
            UpdateChecker.Start();

            Mod.Log.Msg("就绪。");
        }

        /// <summary>本次会话是否已由玩家停用统计。</summary>
        internal static bool Disabled => _disabled;

        /// <summary>
        /// 玩家在横幅上点了「停用」：卸下全部 hook，本次会话不再统计，游戏不受影响。
        /// **只有玩家的点击能走到这里**——清单、网络、任何远端都不能触发它。
        /// 重启游戏会恢复正常加载。
        /// </summary>
        internal static void DisableForThisSession()
        {
            if (_disabled) return;
            _disabled = true;
            try
            {
                _harmony?.UnpatchSelf();
                Mod.Log.Warning("玩家选择停用本次会话的统计，全部 hook 已卸下。重启游戏恢复。");
            }
            catch (Exception e)
            {
                Mod.Log.Error($"卸下 hook 失败：{e}");
            }
        }

        public override bool Unload()
        {
            try { DamageTracker.ExportCsv("session-final"); } catch { /* 退出阶段不吵闹 */ }
            _harmony?.UnpatchSelf();
            return true;
        }
    }
}
