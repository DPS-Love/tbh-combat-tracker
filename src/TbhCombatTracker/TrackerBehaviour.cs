using System;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 注入进 IL2CPP 域的 MonoBehaviour，负责每帧的热键轮询和驱动界面。
    /// BepInEx 的 BasePlugin 不是 MonoBehaviour，拿不到 Unity 生命周期回调，
    /// 所以这些必须放在一个真正被注册进游戏运行时的组件里。
    /// </summary>
    public class TrackerBehaviour : MonoBehaviour
    {
        /// <summary>Il2CppInterop 要求注入类型提供这个构造函数，缺了会在 AddComponent 时崩。</summary>
        public TrackerBehaviour(IntPtr ptr) : base(ptr) { }

        private void OnDestroy()
        {
            Ui.UiRoot.Destroy();
        }

        /// <summary>游戏正常退出：把战斗日志剩下的写完、写结束标记、关文件。</summary>
        private void OnApplicationQuit()
        {
            try { EventLogWriter.Stop(); } catch { /* 退出阶段不吵闹 */ }
        }

        private static bool _stageWatcherBroken;

        private void Update()
        {
            // StageWatcher 的静态字段是游戏类型，游戏更新改了名的话，类本身都加载不了，
            // 异常会在这个调用点抛（Tick 里的 try/catch 根本跑不到）。接住并停用，
            // 热键和面板绘制必须继续。
            if (!_stageWatcherBroken)
            {
                try { StageWatcher.Tick(); }
                catch (Exception e)
                {
                    _stageWatcherBroken = true;
                    Mod.Log.Warning($"关卡分段不可用（游戏类型可能已变）：{e.GetType().Name}");
                }
            }

            // 界面：鼠标、刷新、窗口。它自己兜着异常，坏了会退回一行 IMGUI 提示
            Ui.UiRoot.Update();

            // 设置界面正在录新热键时，别让同一次按键再触发旧功能
            if (Ui.UiRoot.CapturingKey) return;

            if (Hotkeys.Pressed(Mod.Config.ToggleKey.Value))
                Ui.UiRoot.ToggleOverlay();

            if (Hotkeys.Pressed(Mod.Config.ResetKey.Value))
            {
                DamageTracker.ResetCurrent();
                Mod.Log.Msg("统计已手动重置。");
            }

            if (Hotkeys.Pressed(Mod.Config.MainPanelKey.Value))
                Ui.UiRoot.ToggleMain();

            if (Hotkeys.Pressed(Mod.Config.ExportKey.Value))
            {
                var path = DamageTracker.ExportCsv(DamageTracker.Current);
                Mod.Log.Msg(path != null ? $"已导出：{path}" : "导出失败，见上方日志。");
            }
        }

        /// <summary>界面是 uGUI 画的；这里只在它建不起来时画一行 IMGUI 提示（和更新横幅的文字）。</summary>
        private void OnGUI()
        {
            Ui.UiRoot.OnGUIFallback();
        }
    }

    internal static class Hotkeys
    {
        private static bool _legacyBroken;

        public static bool Pressed(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName) || _legacyBroken)
                return false;

            if (!Enum.TryParse<KeyCode>(keyName, true, out var key))
                return false;

            try
            {
                return Input.GetKeyDown(key);
            }
            catch (Exception e)
            {
                // 游戏若把 Active Input Handling 设成了 "Input System (New)"，
                // 旧版 Input 会直接抛异常。记录一次就永久降级，不要每帧刷屏。
                _legacyBroken = true;
                Mod.Log.Warning(
                    $"旧版 Input 不可用（{e.GetType().Name}），热键已禁用。" +
                    "请改用配置文件里的开关，或改接 Unity.InputSystem。");
                return false;
            }
        }
    }
}
