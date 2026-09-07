using System;
using UnityEngine;
using UnityEngine.UI;

namespace TbhCombatTracker
{
    /// <summary>
    /// 一个隐形的 uGUI 射线靶，尺寸和位置跟着 IMGUI 面板走。
    ///
    /// 为什么这样就够了：游戏的 <c>WindowManager.Update()</c> 每帧做的是
    /// <c>EventSystem.current.RaycastAll(...)</c>——**通用射线**，会遍历场景里所有
    /// 已注册的 BaseRaycaster，包括我们自己新建的 Canvas。所以只要光标下有我们的
    /// raycastTarget，游戏就会自己把窗口的 <c>WS_EX_TRANSPARENT</c> 摘掉，
    /// IMGUI 于是能正常收到鼠标，按钮可点、窗口可拖。
    ///
    /// 比自己改宿主窗口样式（<see cref="ClickThrough"/>）干净得多：不碰 Win32、
    /// 不需要标定参数极性、光标移开后由游戏自己恢复穿透。
    /// </summary>
    internal static class RaycastAnchor
    {
        private const string RootName = "TbhCombatTracker_RaycastAnchor";

        private static GameObject _root;
        private static RectTransform _area;
        private static RectTransform _area2;   // 明细窗口
        private static bool _tried;

        /// <summary>创建成功且仍然存活时为 true；此时 Win32 那条路会自动让位。</summary>
        public static bool Active => _root != null && _area != null;

        /// <summary>只尝试一次；失败就让调用方回退到 Win32 方案。</summary>
        public static bool TryCreate()
        {
            if (_tried) return Active;
            _tried = true;

            try
            {
                _root = new GameObject(RootName);
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _root.hideFlags = HideFlags.HideAndDontSave;

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                // 压在游戏自己的 UI 下面：RaycastAll 会返回所有命中，所以游戏的
                // "光标在 UI 上吗"判断照常成立；但真要派发点击事件时，
                // 重叠区域仍然是游戏的 UI 优先，不会被我们这块透明图挡掉。
                canvas.sortingOrder = -1000;

                _root.AddComponent<GraphicRaycaster>();

                var areaGo = new GameObject("Area");
                areaGo.transform.SetParent(_root.transform, false);

                var img = areaGo.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0f); // 完全透明
                // Image 默认 alphaHitTestMinimumThreshold = 0，全透明也照样算命中
                img.raycastTarget = true;

                _area = SetupArea(areaGo);

                // 第二块给明细窗口。用两块独立矩形而不是并集包围盒——
                // 并集会把两窗之间的空白也变成不可穿透，挡住桌面操作。
                var area2Go = new GameObject("Area2");
                area2Go.transform.SetParent(_root.transform, false);
                var img2 = area2Go.AddComponent<Image>();
                img2.color = new Color(0f, 0f, 0f, 0f);
                img2.raycastTarget = true;
                _area2 = SetupArea(area2Go);

                Mod.Log.Msg("已创建 uGUI 射线靶：光标移到面板上时，游戏会自动解除点击穿透。");
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"创建 uGUI 射线靶失败，回退到 Win32 方案：{e}");
                Destroy();
                return false;
            }
        }

        private static RectTransform SetupArea(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);   // 锚到左上，和 GUI 坐标系对齐
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = Vector2.zero;
            return rt;
        }

        /// <summary>把射线靶同步到面板当前的屏幕矩形。</summary>
        public static void Sync(Rect guiRect, float scale)
        {
            if (!Active) return;

            try
            {
                Place(_area, guiRect, scale);

                // 明细窗口开着时才占位
                if (DetailWindow.IsOpen) Place(_area2, DetailWindow.CurrentRect, scale);
                else if (_area2 != null) _area2.sizeDelta = Vector2.zero;
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"同步射线靶失败，已停用：{e.GetType().Name}");
                Destroy();
            }
        }

        private static void Place(RectTransform rt, Rect guiRect, float scale)
        {
            if (rt == null) return;
            // GUI 坐标（左上原点、y 向下）* 缩放 = 屏幕像素；
            // 锚点和轴心都在左上，所以 anchoredPosition 直接用 (x, -y)。
            rt.anchoredPosition = new Vector2(guiRect.x * scale, -guiRect.y * scale);
            rt.sizeDelta = new Vector2(guiRect.width * scale, guiRect.height * scale);
        }

        /// <summary>面板隐藏时把靶子缩到 0，免得看不见的区域还在抢鼠标。</summary>
        public static void Hide()
        {
            if (!Active) return;
            try
            {
                _area.sizeDelta = Vector2.zero;
                if (_area2 != null) _area2.sizeDelta = Vector2.zero;
            }
            catch { /* 无所谓 */ }
        }

        public static void Destroy()
        {
            try
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
            }
            catch { /* 退出阶段不吵闹 */ }
            _root = null;
            _area = null;
            _area2 = null;
        }
    }
}
