using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 让 IMGUI 面板能接收鼠标。
    ///
    /// Task Bar Hero 是任务栏挂件：它的窗口铺满屏幕但默认带 <c>WS_EX_TRANSPARENT</c>（点击穿透），
    /// 好让你照常操作桌面。游戏每帧在 <c>WindowManager.Update()</c> 里调 <c>on.glu(bool)</c>，
    /// 依据"光标是否落在它自己的 uGUI 上"来开关这个样式。
    ///
    /// 我们的面板是 IMGUI，不参与 uGUI 射线检测，所以永远不会被判定为"在 UI 上"——
    /// 窗口保持穿透，按钮点不到、窗口也拖不动，点击直接落到下面的程序上。
    ///
    /// 解法：在 <c>on.glu</c> 上加 Prefix，当光标落在面板矩形内时把参数改成"可交互"那一档。
    /// 参数极性不靠猜——<see cref="Observe"/> 在游戏自己调用之后读一次真实的
    /// <c>GWL_EXSTYLE</c>，看 <c>WS_EX_TRANSPARENT</c> 位是否被置上，一次就能标定出来。
    /// </summary>
    internal static class ClickThrough
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private static IntPtr _hwnd = IntPtr.Zero;
        private static bool _hwndTried;

        /// <summary>光标是否落在面板上——由 <see cref="Tick"/> 每帧刷新。</summary>
        public static bool PointerOverPanel { get; private set; }

        /// <summary>光标在 GUI 坐标系（左上原点、y 向下）里的位置，与 IMGUI 一致。</summary>
        public static Vector2 CursorGui { get; private set; }

        // 极性标定结果：传哪个值会让窗口变成可交互（非穿透）
        private static bool _polarityKnown;
        private static bool _interactiveArg;

        /// <summary>本帧我们是否改写过参数——改写过就不能拿来标定极性。</summary>
        private static bool _forcedThisCall;

        public static bool PolarityKnown => _polarityKnown;

        private static IntPtr Hwnd
        {
            get
            {
                if (_hwnd != IntPtr.Zero || _hwndTried) return _hwnd;
                _hwndTried = true;
                try
                {
                    _hwnd = Process.GetCurrentProcess().MainWindowHandle;
                    if (_hwnd == IntPtr.Zero)
                        Mod.Log.Warning("拿不到游戏主窗口句柄，鼠标穿透修正无法工作。");
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"获取窗口句柄失败，鼠标穿透修正无法工作：{e.GetType().Name}");
                }
                return _hwnd;
            }
        }

        /// <summary>每帧刷新光标位置和命中状态。</summary>
        public static void Tick()
        {
            if (!Mod.Config.FixClickThrough.Value || !Overlay.Visible)
            {
                PointerOverPanel = false;
                return;
            }

            var hwnd = Hwnd;
            if (hwnd == IntPtr.Zero) { PointerOverPanel = false; return; }

            try
            {
                if (!GetCursorPos(out var p)) { PointerOverPanel = false; return; }

                // 窗口是穿透的，Unity 收不到鼠标消息，Input.mousePosition 不可信，
                // 所以走 Win32 拿全局坐标再换算到客户区——客户区坐标系和 GUI 一致（左上原点）。
                ScreenToClient(hwnd, ref p);
                CursorGui = new Vector2(p.X, p.Y);
                PointerOverPanel = Overlay.HitTest(CursorGui);
            }
            catch (Exception e)
            {
                PointerOverPanel = false;
                Mod.Log.Warning($"光标命中检测失败，已停用穿透修正：{e.GetType().Name}");
                Mod.Config.FixClickThrough.Value = false;
            }
        }

        /// <summary>
        /// Prefix：光标在面板上时，把游戏传给 <c>on.glu</c> 的参数改成"可交互"。
        /// 极性还没标定出来时不动它——先让游戏正常跑一帧好完成标定。
        /// </summary>
        public static void OverrideArg(ref bool arg)
        {
            _forcedThisCall = false;

            if (!Mod.Config.FixClickThrough.Value) return;
            if (RaycastAnchor.Active) return;   // uGUI 射线靶已接管，不碰窗口样式
            if (!PointerOverPanel || !_polarityKnown) return;
            if (arg == _interactiveArg) return;

            arg = _interactiveArg;
            _forcedThisCall = true;
        }

        /// <summary>
        /// Postfix：读一次真实的窗口样式，标定"哪个参数值 = 可交互"。
        /// 只需要观察到一次；我们自己改写过的那次不能用来标定。
        /// </summary>
        public static void Observe(bool arg)
        {
            if (_polarityKnown || _forcedThisCall) return;

            var hwnd = Hwnd;
            if (hwnd == IntPtr.Zero) return;

            try
            {
                var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                if (ex == 0) return; // 调用失败，等下一帧

                var transparent = (ex & WS_EX_TRANSPARENT) != 0;
                _interactiveArg = transparent ? !arg : arg;
                _polarityKnown = true;

                Mod.Log.Msg($"穿透极性已标定：on.glu({arg}) 后 WS_EX_TRANSPARENT={transparent}，" +
                            $"因此可交互档 = {_interactiveArg}。光标移到面板上即可点击和拖动。");
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"读取窗口样式失败，穿透修正停用：{e.GetType().Name}");
                Mod.Config.FixClickThrough.Value = false;
            }
        }
    }
}
