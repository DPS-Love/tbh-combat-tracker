using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace TbhCombatTracker
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("TaskBarHero.exe")]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "dpslove.tbh.combattracker";
        public const string PluginName = "TBH Combat Tracker";
        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;

        public override void Load()
        {
            Mod.Init(Log, Config);
            Mod.Log.Msg($"{PluginName} v{PluginVersion} 启动中…");

            // 手动打补丁而不是用 [HarmonyPatch] 特性 + PatchAll：
            // 某个 hook 因为游戏更新失配时，其余 hook 仍然能工作，而不是整个插件崩掉。
            _harmony = new Harmony(PluginGuid);
            Patches.ApplyAll(_harmony);

            // BasePlugin 本身不是 MonoBehaviour，拿不到 Update / OnGUI。
            // AddComponent 会把这个类注册进 IL2CPP 域并挂到一个常驻 GameObject 上。
            AddComponent<TrackerBehaviour>();

            Mod.Log.Msg("就绪。");
        }

        public override bool Unload()
        {
            try { DamageTracker.ExportCsv("session-final"); } catch { /* 退出阶段不吵闹 */ }
            _harmony?.UnpatchSelf();
            return true;
        }
    }
}
