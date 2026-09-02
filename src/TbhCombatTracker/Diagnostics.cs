using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

using GMonster = TaskbarHero.Monster;
using GUnitHealth = global::pj;
using GMonsterHealth = global::ph;
using GHeroHealth = global::pf;

namespace TbhCombatTracker
{
    /// <summary>
    /// 诊断模式：把血量控制器和 Monster 上的方法挂钩，记录前几次调用和实参，
    /// 用来搞清楚伤害到底走哪条路——混淆过的代码光看签名会猜错，让游戏自己说。
    ///
    /// 【必须按原生函数指针去重】IL2CPP 会把方法体相同的函数去重成同一段机器码
    /// （例如 Monster.gqq 和 Monster.gqs 都在 0x6B1620，pj 的 cjr/gxp/ode/hm 都在 0xCAB440）。
    /// 对同一个原生地址挂两次 Harmony detour，其中一个的 trampoline 会重新进入 detour，
    /// 直接无限递归栈溢出——这个坑真的把游戏搞成了启动闪退。
    /// </summary>
    internal static class Diagnostics
    {
        /// <summary>每个方法最多记这么多次，避免每帧方法刷屏。</summary>
        private const int PerMethodLimit = 3;
        /// <summary>总量上限，防止日志失控。</summary>
        private const int TotalLimit = 400;

        /// <summary>Unity 生命周期回调每帧都跑，记了也没信息量。</summary>
        private static readonly HashSet<string> Skip = new HashSet<string>
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnEnable", "OnDisable",
            "OnDestroy", "Awake", "Start", "OnValidate", "Finalize", "ToString",
            "GetHashCode", "Equals", "MemberwiseClone",
        };

        /// <summary>
        /// 白名单：机器码在全二进制里**全局唯一**的方法，只有这些能安全 detour。
        ///
        /// 光在目标类型内部去重是不够的——IL2CPP 的共享是全局的。本游戏里
        /// `0x6B1620`（一条 ret）被 1872 个方法共用，`0xCE8880`（有完整序言的真函数）
        /// 被 457 个方法共用。对这种地址挂 detour 会劫持全游戏所有共用它的方法，
        /// 它们的 this 是各种不相干类型，进 wrapper 一转型就 NullReferenceException，
        /// 每帧刷屏导致游戏进不去。
        ///
        /// 这份列表和游戏版本绑定，更新后用 `python tools/safe-hooks.py --csharp` 重新生成。
        /// </summary>
        private static readonly HashSet<string> SafeToPatch = new HashSet<string>
        {
            "pj.hv", "pj.opn", "pj.gxk", "pj.gxr", "pj.gsd", "pj.gsh",
            "pj.esh", "pj.gse", "pj.gxi", "pj.gxn", "pj.mma", "pj.ibj",
            "pj.cpg", "pj.fva", "pj.gxl", "pj.liv", "pj.err", "pj.cds",
            "pj.gxj", "pj.gxq", "pj.ibb", "pj.gsi",
            "ph.gsd", "ph.gsh",
            "pf.gsh", "pf.obo", "pf.gse", "pf.iyf", "pf.gsi", "pf.gsf",
            "pf.jih", "pf.gsd",
            "Monster.gpz", "Monster.gqa", "Monster.gqt", "Monster.gqu", "Monster.gsq", "Monster.gsr",
            "Monster.gss", "Monster.gst", "Monster.gsu", "Monster.grd", "Monster.grt", "Monster.gsv",
            "Monster.gsw", "Monster.gsx", "Monster.gsy",
        };

        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        private static int _total;
        private static bool _capReported;

        public static void Apply(Harmony harmony)
        {
            // 一次性保险：立刻把开关写回 false。诊断挂载如果再把游戏搞崩，
            // 下次启动就是关闭状态，不会让人陷在崩溃循环里出不来。
            Mod.Config.DiagnosticMode.Value = false;
            Mod.Log.Warning("[diag] 诊断模式为一次性：配置已自动写回 false，下次启动不再启用。");

            var types = new (string Label, Type Type)[]
            {
                ("pj (UnitHealth)", typeof(GUnitHealth)),
                ("ph (MonsterHealth)", typeof(GMonsterHealth)),
                ("pf (HeroHealth)", typeof(GHeroHealth)),
                ("Monster", typeof(GMonster)),
            };

            var prefix = new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(Diagnostics), nameof(Trace)));

            // 原生指针 -> 已经挂过的那个方法名，用来去重并把共用关系打出来
            var seen = new Dictionary<IntPtr, string>();
            int patched = 0, skippedShared = 0, skippedUnresolved = 0, skippedUnsafe = 0, failed = 0;

            foreach (var (label, type) in types)
            {
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                  BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethod) continue;
                    if (Skip.Contains(m.Name)) continue;
                    if (m.Name.StartsWith("get_", StringComparison.Ordinal)) continue;
                    if (m.Name.StartsWith("set_", StringComparison.Ordinal)) continue;
                    if (m.Name.StartsWith("add_", StringComparison.Ordinal)) continue;
                    if (m.Name.StartsWith("remove_", StringComparison.Ordinal)) continue;

                    // 白名单是第一道也是最关键的一道闸：只有机器码全局唯一的方法才能挂
                    if (!SafeToPatch.Contains($"{type.Name}.{m.Name}"))
                    {
                        skippedUnsafe++;
                        continue;
                    }

                    var native = Il2CppUtil.NativePointer(m);
                    if (native == IntPtr.Zero)
                    {
                        // 拿不到指针就不挂——宁可少一条诊断信息，也不冒双重 detour 的险
                        skippedUnresolved++;
                        continue;
                    }

                    if (seen.TryGetValue(native, out var owner))
                    {
                        Mod.Log.Msg($"[diag] {type.Name}.{m.Name} 与 {owner} 共用同一段机器码 " +
                                    $"(0x{native.ToInt64():X})，跳过以免双重 detour");
                        skippedShared++;
                        continue;
                    }

                    try
                    {
                        harmony.Patch(m, prefix: prefix);
                        seen[native] = $"{type.Name}.{m.Name}";
                        patched++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Mod.Log.Warning($"[diag] 挂载 {type.Name}.{m.Name} 失败：{e.GetType().Name}");
                    }
                }
            }

            Mod.Log.Msg($"[diag] 挂载完成：成功 {patched}，不在全局唯一白名单跳过 {skippedUnsafe}，" +
                        $"因共用机器码跳过 {skippedShared}，因取不到原生指针跳过 {skippedUnresolved}，失败 {failed}。");
            Mod.Log.Msg($"[diag] 每个方法最多记 {PerMethodLimit} 次，总上限 {TotalLimit} 条。" +
                        "进游戏打十几秒，然后退出并把 LogOutput.log 发出来。");
        }

        private static void Trace(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                if (_total >= TotalLimit)
                {
                    if (!_capReported)
                    {
                        _capReported = true;
                        Mod.Log.Msg($"[diag] 已达总量上限 {TotalLimit} 条，后续静默。");
                    }
                    return;
                }

                var key = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
                lock (Counts)
                {
                    Counts.TryGetValue(key, out var n);
                    if (n >= PerMethodLimit) return;
                    Counts[key] = n + 1;
                    _total++;
                }

                Mod.Log.Msg($"[diag] {key}({Format(__args)})");
            }
            catch
            {
                // 诊断代码自己绝不能把游戏搞崩
            }
        }

        private static string Format(object[] args)
        {
            if (args == null || args.Length == 0) return "";

            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Describe(args[i]));
            }
            return sb.ToString();
        }

        private static string Describe(object o)
        {
            if (o == null) return "null";
            try
            {
                switch (o)
                {
                    case float f: return f.ToString("0.###");
                    case double d: return d.ToString("0.###");
                    case bool b: return b ? "true" : "false";
                    case string s: return $"\"{s}\"";
                }

                // Il2Cpp 对象打类型名 + Unity 物体名，比 ToString 有用
                var t = o.GetType();
                var nameProp = t.GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
                if (nameProp != null && nameProp.PropertyType == typeof(string))
                    return $"{t.Name}(\"{nameProp.GetValue(o)}\")";

                return $"{t.Name}[{o}]";
            }
            catch (Exception e)
            {
                return $"<{e.GetType().Name}>";
            }
        }
    }
}
