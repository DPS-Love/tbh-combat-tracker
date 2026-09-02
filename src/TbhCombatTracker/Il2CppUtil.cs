using System;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.Runtime;

namespace TbhCombatTracker
{
    internal static class Il2CppUtil
    {
        /// <summary>
        /// 取 Il2CppInterop 代理方法背后的原生函数入口地址，取不到返回 IntPtr.Zero。
        ///
        /// 为什么需要它：IL2CPP 编译时会把**方法体完全相同**的函数去重成同一段机器码。
        /// 本游戏里 Monster.gqq / Monster.gqs 都在 0x6B1620，pj 的 cjr/gxp/ode/hm 都在 0xCAB440。
        /// 对同一个原生地址挂两次 Harmony detour，其中一个的 trampoline 会重新进入 detour，
        /// 无限递归直接栈溢出——表现为游戏启动闪退，而且日志里只有一坨重复调用栈。
        /// 所以凡是批量打补丁，都必须先按这个地址去重。
        ///
        /// 实现依据：Il2CppInterop 给每个代理方法生成一个静态字段
        /// NativeMethodInfoPtr_&lt;方法名&gt;_&lt;可见性&gt;_&lt;签名&gt;_&lt;序号&gt;，存着 Il2CppMethodInfo*。
        /// </summary>
        public static unsafe IntPtr NativePointer(MethodBase m)
        {
            try
            {
                if (m?.DeclaringType == null) return IntPtr.Zero;

                var prefix = "NativeMethodInfoPtr_" + m.Name + "_";
                var fields = m.DeclaringType
                    .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(f => f.FieldType == typeof(IntPtr) &&
                                f.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList();

                // 有重载时同名前缀会命中多个字段，无法确定对应关系——保守起见放弃
                if (fields.Count != 1) return IntPtr.Zero;

                var infoPtr = (IntPtr)fields[0].GetValue(null);
                if (infoPtr == IntPtr.Zero) return IntPtr.Zero;

                return UnityVersionHandler.Wrap((Il2CppMethodInfo*)infoPtr).MethodPointer;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
