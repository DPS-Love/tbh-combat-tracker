using System;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 注入进 IL2CPP 域的 MonoBehaviour，负责每帧的热键轮询和 IMGUI 绘制。
    /// BepInEx 的 BasePlugin 不是 MonoBehaviour，拿不到 Unity 生命周期回调，
    /// 所以这些必须放在一个真正被注册进游戏运行时的组件里。
    /// </summary>
    public class TrackerBehaviour : MonoBehaviour
    {
        /// <summary>Il2CppInterop 要求注入类型提供这个构造函数，缺了会在 AddComponent 时崩。</summary>
        public TrackerBehaviour(IntPtr ptr) : base(ptr) { }

        private void Start()
        {
            // 优先走 uGUI 射线靶：让游戏现成的 EventSystem.RaycastAll 帮我们解除穿透。
            // 创建失败才回退到自己改窗口样式的 Win32 方案。
            if (Mod.Config.FixClickThrough.Value && !RaycastAnchor.TryCreate())
                Mod.Log.Warning("将使用 Win32 方案兜底解除点击穿透。");
        }

        private void OnDestroy()
        {
            RaycastAnchor.Destroy();
        }

        private void Update()
        {
            StageWatcher.Tick();

            if (RaycastAnchor.Active)
            {
                // 靶子跟着面板走；面板隐藏时缩到 0，别在看不见的地方抢鼠标
                if (Overlay.Visible)
                    RaycastAnchor.Sync(Overlay.CurrentRect, Overlay.CurrentScale);
                else
                    RaycastAnchor.Hide();
            }
            else
            {
                ClickThrough.Tick();
            }

            if (Hotkeys.Pressed(Mod.Config.ToggleKey.Value))
                Overlay.Visible = !Overlay.Visible;

            if (Hotkeys.Pressed(Mod.Config.ResetKey.Value))
            {
                DamageTracker.ResetCurrent();
                Mod.Log.Msg("统计已手动重置。");
            }

            if (Hotkeys.Pressed(Mod.Config.ExportKey.Value))
            {
                var path = DamageTracker.ExportCsv();
                Mod.Log.Msg(path != null ? $"已导出：{path}" : "导出失败，见上方日志。");
            }
        }

        private void OnGUI()
        {
            Overlay.Draw();
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
