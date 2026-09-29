using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 鼠标状态，每帧轮询一次。
    ///
    /// 光标位置和左键走 Win32（GetCursorPos / GetAsyncKeyState），不走 Unity 的 Input：
    /// 游戏窗口平时是点击穿透的，光标还没移到面板上时窗口收不到鼠标消息，Input.mousePosition 是旧值，
    /// 悬停效果就会慢半拍。Win32 读的是真实状态，和窗口穿不穿透无关。
    /// 滚轮只有 Unity 那边有：光标在面板上时游戏已经解除了穿透，窗口能收到滚轮消息。
    ///
    /// 坐标换算成界面单位：左上原点、y 向下、除以界面缩放——和各窗口布局用的坐标系一致。
    /// </summary>
    internal static class UiInput
    {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

        private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, SM_SWAPBUTTON = 23;

        private static IntPtr _hwnd;
        private static int _hwndRetry;
        private static bool _win32Broken, _wheelBroken;

        /// <summary>光标位置（界面单位，左上原点）。</summary>
        public static Vector2 Pos { get; private set; }
        /// <summary>这一帧拿到了可信的光标位置。</summary>
        public static bool Valid { get; private set; }
        public static bool Held { get; private set; }
        public static bool Pressed { get; private set; }
        public static bool Released { get; private set; }
        /// <summary>滚轮，向上为正，单位"格"。</summary>
        public static float Wheel { get; private set; }

        public static void Poll(float scale)
        {
            Pressed = Released = false;
            Wheel = 0f;
            if (scale <= 0f) scale = 1f;

            var down = false;
            Valid = false;
            if (!_win32Broken)
            {
                try
                {
                    var hwnd = Hwnd();
                    if (hwnd != IntPtr.Zero && GetCursorPos(out var p) && ScreenToClient(hwnd, ref p))
                    {
                        // 客户区和 Unity 的屏幕尺寸不一致（DPI 虚拟化）时按比例换算
                        float sx = 1f, sy = 1f;
                        if (GetClientRect(hwnd, out var rc) && rc.R > 0 && rc.B > 0)
                        {
                            sx = Screen.width / (float)rc.R;
                            sy = Screen.height / (float)rc.B;
                        }
                        Pos = new Vector2(p.X * sx / scale, p.Y * sy / scale);
                        Valid = true;
                    }

                    var vk = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
                    down = (GetAsyncKeyState(vk) & 0x8000) != 0;
                }
                catch (Exception e)
                {
                    _win32Broken = true;
                    Mod.Log.Warning($"读不到系统光标（{e.GetType().Name}），改用 Unity 的鼠标输入。");
                }
            }

            if (_win32Broken)
            {
                try
                {
                    var m = Input.mousePosition;
                    Pos = new Vector2(m.x / scale, (Screen.height - m.y) / scale);
                    Valid = true;
                    down = Input.GetMouseButton(0);
                }
                catch { Valid = false; }
            }

            // 一帧之内按下又抬起（帧率很低时会有）：Win32 轮询看不到，Unity 的消息队列能看到
            var quickClick = false;
            if (!_wheelBroken)
            {
                try
                {
                    Wheel = Input.mouseScrollDelta.y;
                    quickClick = !down && !Held && Input.GetMouseButtonDown(0);
                }
                catch
                {
                    _wheelBroken = true;
                }
            }

            if (_pendingRelease)
            {
                // 上一帧是"同一帧里按下又抬起"：当时只报了按下，这一帧补上抬起
                _pendingRelease = false;
                if (Held && !down)
                {
                    Held = false;
                    Released = true;
                }
            }
            else if (!Held && (down || quickClick))
            {
                Held = true;
                Pressed = true;
                if (!down) _pendingRelease = true;
            }
            else if (Held && !down)
            {
                Held = false;
                Released = true;
            }
        }

        private static bool _pendingRelease;

        private static IntPtr Hwnd()
        {
            if (_hwnd != IntPtr.Zero) return _hwnd;
            if (_hwndRetry-- > 0) return IntPtr.Zero;
            _hwndRetry = 120;   // 拿不到就过两秒再试，别每帧枚举窗口
            try { _hwnd = Process.GetCurrentProcess().MainWindowHandle; }
            catch { _hwnd = IntPtr.Zero; }
            return _hwnd;
        }
    }
}
