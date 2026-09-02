using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace TbhSigCheck
{
    internal static class Program
    {
        /// <summary>
        /// 要检查的目标。左边是 interop 里的类型全名，右边是我们打补丁的方法名。
        /// 游戏更新后混淆名会变，改这里 + Patches.cs 顶部的常量。
        /// </summary>
        private static readonly (string Type, string[] Methods)[] Targets =
        {
            ("pj", new[] { "gsi" }),                     // UnitHealth.ChangeHp        ← 主 hook（怪物承伤）
            ("pf", new[] { "gsi", "get_bdeg" }),         // HeroHealth.ChangeHp + 它持有的 Hero
            ("TaskbarHero.Monster", new[] { "grd" }),    // Monster.TakeDamage         ← 分类 hook
            ("on", new[] { "glu" }),                     // 点击穿透开关（Win32 兜底方案用）
            ("TaskbarHero.StageManager", new[] { "get_stageState", "get_b_StageStart" }), // 关卡分段信号
            ("TaskbarHero.UI_Stage", new[] { "get_text_StageName" }),  // 关卡名 ← 分段依据 + 面板标题
            ("TaskbarHero.Combat.PriestHeal", new[] { "mti", "get_bhfz" }), // 治疗归因 ← 施法者上下文
            ("TaskbarHero.Unit", new[] { "gpz", "gqa" }),
        };

        private static readonly string[] TypeDumps =
        {
            "TaskbarHero.DamageInfo",
            "TaskbarHero.Hero",
        };

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // --stripped <程序集名> <类型名...>
            // 列出哪些方法的 IL 还原失败了。Il2CppInterop 对被裁剪的纯托管方法会生成
            // 一个直接 throw NotSupportedException("Method unstripping failed") 的桩，
            // 调用它就会每帧抛异常。用这个模式提前知道哪些 Unity API 不能用。
            if (args.Length > 0 && args[0] == "--stripped")
                return Stripped(args.Skip(1).ToArray());

            // --members <dll路径或interop里的程序集名> [类型名子串...]
            // 用来确认某个库到底暴露了哪些 API，免得靠记忆猜方法名。
            if (args.Length > 0 && args[0] == "--members")
                return Members(args.Skip(1).ToArray());

            var interopDir = args.FirstOrDefault() ?? DefaultInteropDir();
            var path = Directory.Exists(interopDir)
                ? Path.Combine(interopDir, "Assembly-CSharp.dll")
                : interopDir; // 也允许直接传一个 dll 路径

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"找不到 {path}");
                Console.Error.WriteLine("先安装 BepInEx 并启动一次游戏，让它生成 interop 程序集。");
                return 1;
            }

            var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
            var mod = asm.MainModule;
            Console.WriteLine($"{path}\n程序集: {asm.Name.Name}   类型数: {mod.Types.Count}\n");

            var missing = 0;
            foreach (var (type, methods) in Targets)
                missing += DumpMethods(mod, type, methods);
            foreach (var t in TypeDumps)
                missing += DumpType(mod, t);

            if (missing > 0)
            {
                Console.WriteLine($"!! {missing} 项没找到——混淆名很可能随游戏更新变了。");
                Console.WriteLine("   重跑 pwsh tools/dump-symbols.ps1，按 docs/symbols.md 的识别特征重新定位。");
                return 2;
            }

            Console.WriteLine("全部命中。");
            return 0;
        }

        private static int Members(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("用法: sigcheck --members <dll路径|程序集名> [类型名子串...]");
                return 1;
            }

            var path = File.Exists(args[0])
                ? args[0]
                : Path.Combine(DefaultInteropDir(), args[0].EndsWith(".dll") ? args[0] : args[0] + ".dll");

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"找不到 {path}");
                return 1;
            }

            var filters = args.Skip(1).ToArray();
            var asm = AssemblyDefinition.ReadAssembly(path);

            foreach (var t in asm.MainModule.Types.OrderBy(t => t.FullName))
            {
                if (filters.Length > 0 &&
                    !filters.Any(f => t.FullName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                Console.WriteLine($"── {t.FullName} ──");
                foreach (var m in t.Methods.OrderBy(m => m.Name))
                    Console.WriteLine($"   {(m.IsStatic ? "static " : "")}{Signature(m)}");
                foreach (var f in t.Fields)
                    Console.WriteLine($"   field {(f.IsStatic ? "static " : "")}{Short(f.FieldType.FullName)} {f.Name}");
                Console.WriteLine();
            }

            return 0;
        }

        private const string UnstripMarker = "Method unstripping failed";

        private static int Stripped(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("用法: sigcheck --stripped <程序集名，如 UnityEngine.IMGUIModule> [类型名...]");
                return 1;
            }

            var dir = DefaultInteropDir();
            var asmName = args[0].EndsWith(".dll") ? args[0] : args[0] + ".dll";
            var path = Path.Combine(dir, asmName);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"找不到 {path}");
                return 1;
            }

            var wanted = args.Skip(1).ToHashSet(StringComparer.Ordinal);
            var asm = AssemblyDefinition.ReadAssembly(path);

            foreach (var t in asm.MainModule.Types.OrderBy(t => t.FullName))
            {
                if (wanted.Count > 0 && !wanted.Contains(t.Name) && !wanted.Contains(t.FullName))
                    continue;

                var bad = new List<string>();
                var ok = new List<string>();
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var broken = m.Body.Instructions.Any(i =>
                        i.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr &&
                        (i.Operand as string) == UnstripMarker);
                    (broken ? bad : ok).Add(Signature(m));
                }

                if (bad.Count == 0 && ok.Count == 0) continue;

                Console.WriteLine($"── {t.FullName} ──  可用 {ok.Count} / 失效 {bad.Count}");
                foreach (var s in bad.OrderBy(s => s))
                    Console.WriteLine($"   ✗ {s}");
                Console.WriteLine();
            }

            return 0;
        }

        private static string Signature(MethodDefinition m)
            => $"{Short(m.ReturnType.FullName)} {m.Name}(" +
               string.Join(", ", m.Parameters.Select(p => Short(p.ParameterType.FullName))) + ")";

        private static string DefaultInteropDir()
        {
            // 构建时由 csproj 把 BepInExInteropDir 烧进程序集元数据
            var meta = typeof(Program).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "InteropDir");
            return meta?.Value ?? ".";
        }

        private static TypeDefinition Find(ModuleDefinition mod, string fullName)
            => mod.Types.FirstOrDefault(t => t.FullName == fullName);

        private static int DumpMethods(ModuleDefinition mod, string typeName, string[] methods)
        {
            Console.WriteLine($"── {typeName} ──");

            var t = Find(mod, typeName);
            if (t == null)
            {
                Console.WriteLine("   !! 找不到该类型\n");
                return 1;
            }

            Console.WriteLine($"   base={t.BaseType?.FullName}");

            var missing = 0;
            foreach (var name in methods)
            {
                var found = t.Methods.Where(m => m.Name == name).ToList();
                if (found.Count == 0)
                {
                    // 只在基类上声明的话，AccessTools.DeclaredMethod 会拿不到
                    Console.WriteLine($"   !! {name} 未在此类型上声明");
                    missing++;
                    continue;
                }

                foreach (var m in found)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p =>
                        $"{Short(p.ParameterType.FullName)} {p.Name}" +
                        (p.HasDefault ? $" = {p.Constant ?? "null"}" : "")));
                    var kind = m.IsVirtual ? (m.IsNewSlot ? "virtual " : "override ") : "";
                    Console.WriteLine($"   {Vis(m)} {kind}{Short(m.ReturnType.FullName)} {m.Name}({ps})");
                }
            }

            Console.WriteLine();
            return missing;
        }

        private static int DumpType(ModuleDefinition mod, string typeName)
        {
            Console.WriteLine($"── {typeName} ──");

            var t = Find(mod, typeName);
            if (t == null)
            {
                Console.WriteLine("   !! 找不到该类型\n");
                return 1;
            }

            // Il2CppInterop 把非 blittable 的值类型生成成继承 Il2CppSystem.ValueType 的 class，
            // 字段则一律变成属性。Harmony 补丁的参数类型要按这个来。
            Console.WriteLine($"   isValueType={t.IsValueType}  base={t.BaseType?.FullName}");
            foreach (var f in t.Fields.Where(f => !f.IsStatic).Take(24))
                Console.WriteLine($"     field  {Short(f.FieldType.FullName)} {f.Name}");
            foreach (var p in t.Properties.Take(24))
                Console.WriteLine($"     prop   {Short(p.PropertyType.FullName)} {p.Name}");

            Console.WriteLine();
            return 0;
        }

        private static string Vis(MethodDefinition m)
            => m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : "private";

        private static string Short(string full)
        {
            if (full == null) return "?";
            full = full.Replace("System.", "").Replace("UnityEngine.", "");
            var i = full.LastIndexOf('/');
            return i >= 0 ? full.Substring(i + 1) : full;
        }
    }
}
