using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace TbhSigCheck
{
    /// <summary>
    /// 默认模式：读**构建出来的** TbhCombatTracker.dll，把它引用的每一个游戏符号
    /// 拿到当前 interop 的 Assembly-CSharp.dll 里核对——不维护任何清单。
    ///
    ///   1. 类型引用      每个指向 Assembly-CSharp 的 TypeRef（GameSymbols.cs 里的别名都在这）
    ///   2. 成员引用      每个指向 Assembly-CSharp 的 MethodRef / FieldRef（字段访问器的 get_xxx 等）
    ///   3. [Hook] 常量   GameSymbols 里标了 [Hook(typeof(...))] 的方法名，
    ///                    检查它在那些类型上**各自**声明（Harmony 的 DeclaredMethod 不看基类）
    ///
    /// 全部命中返回 0，有缺失返回 2。缺失就说明游戏更新改了混淆名，去改 GameSymbols.cs。
    /// </summary>
    internal static class Program
    {
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

            // --dump <类型全名...>
            // 看 interop 把某个类型生成成了什么形态（值类型变 class、字段变属性……）。
            if (args.Length > 0 && args[0] == "--dump")
                return Dump(args.Skip(1).ToArray());

            // --simulate-update <输入dll> <输出dll>
            // 把对游戏混淆类型（全局命名空间、2~4 个小写字母）的引用改成不存在的名字，
            // 模拟"游戏更新后类型全被重命名"。产物替换 plugins 里的 DLL 后启动游戏，
            // 窗口和更新提示必须仍然出现——发布前必检。见 tools/simulate-update.ps1。
            if (args.Length > 0 && args[0] == "--simulate-update")
                return SimulateUpdate(args.Skip(1).ToArray());

            var modPath = args.FirstOrDefault() ?? DefaultModDll();
            var gamePath = Path.Combine(DefaultInteropDir(), "Assembly-CSharp.dll");

            if (!File.Exists(modPath))
            {
                Console.Error.WriteLine($"找不到 {modPath}\n先构建：dotnet build src/TbhCombatTracker/TbhCombatTracker.csproj -c Release");
                return 1;
            }
            if (!File.Exists(gamePath))
            {
                Console.Error.WriteLine($"找不到 {gamePath}\n先安装 BepInEx 并启动一次游戏，让它生成 interop 程序集。");
                return 1;
            }

            return CheckMod(modPath, gamePath);
        }

        // ------------------------------------------------------------------ 默认模式

        private static int CheckMod(string modPath, string gamePath)
        {
            var game = AssemblyDefinition.ReadAssembly(gamePath, new ReaderParameters { ReadingMode = ReadingMode.Deferred }).MainModule;
            var mod = AssemblyDefinition.ReadAssembly(modPath).MainModule;

            Console.WriteLine($"Mod:  {modPath}\n游戏: {gamePath}   类型数 {game.Types.Count}\n");

            var missing = 0;

            // 1) 类型
            Console.WriteLine("── 引用的游戏类型 ──");
            var typeRefs = mod.GetTypeReferences().Where(IsGame).OrderBy(t => t.FullName).ToList();
            foreach (var tr in typeRefs)
            {
                var t = game.GetType(tr.FullName);
                if (t == null) { Console.WriteLine($"   !! {tr.FullName}   找不到"); missing++; }
                else Console.WriteLine($"   ok {tr.FullName}");
            }

            // 2) 成员
            Console.WriteLine("\n── 引用的游戏成员 ──");
            var memberRefs = mod.GetMemberReferences()
                .Where(m => m.DeclaringType != null && IsGame(m.DeclaringType))
                .OrderBy(m => m.DeclaringType.FullName).ThenBy(m => m.Name)
                .ToList();
            foreach (var mr in memberRefs)
            {
                var owner = mr.DeclaringType.GetElementType().FullName;
                var t = game.GetType(owner);
                if (t == null) continue;   // 类型缺失已在上面计过

                bool found;
                string shown;
                if (mr is MethodReference m)
                {
                    var hit = t.Methods.FirstOrDefault(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count);
                    found = hit != null;
                    shown = hit != null ? Signature(hit) : $"{m.Name}({m.Parameters.Count} 参)";
                }
                else
                {
                    found = t.Fields.Any(f => f.Name == mr.Name);
                    shown = mr.Name;
                }

                if (!found) { Console.WriteLine($"   !! {owner}.{shown}   找不到"); missing++; }
                else Console.WriteLine($"   ok {owner}.{shown}");
            }

            // 3) [Hook] 常量
            Console.WriteLine("\n── [Hook] 标注的方法名 ──");
            var symbols = mod.Types.FirstOrDefault(t => t.Name == "GameSymbols");
            if (symbols == null)
            {
                Console.WriteLine("   !! Mod 里没有 GameSymbols 类");
                missing++;
            }
            else
            {
                foreach (var f in symbols.Fields.Where(f => f.HasConstant))
                {
                    var attr = f.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "HookAttribute");
                    if (attr == null) continue;

                    var name = (string)f.Constant;
                    var types = ((CustomAttributeArgument[])attr.ConstructorArguments[0].Value)
                        .Select(a => (TypeReference)a.Value);

                    foreach (var tr in types)
                    {
                        var t = game.GetType(tr.FullName);
                        if (t == null) { Console.WriteLine($"   !! {f.Name} = \"{name}\"  于 {tr.FullName}：类型不存在"); missing++; continue; }

                        var hits = t.Methods.Where(x => x.Name == name).ToList();
                        if (hits.Count == 0)
                        {
                            // 只在基类上声明的话，AccessTools.DeclaredMethod 会拿不到
                            Console.WriteLine($"   !! {f.Name} = \"{name}\"  未在 {tr.FullName} 上声明");
                            missing++;
                            continue;
                        }
                        foreach (var h in hits)
                        {
                            var kind = h.IsVirtual ? (h.IsNewSlot ? "virtual " : "override ") : "";
                            Console.WriteLine($"   ok {f.Name} = \"{name}\"  {tr.FullName}: {kind}{Signature(h)}");
                        }
                    }
                }
            }

            Console.WriteLine();
            if (missing > 0)
            {
                Console.WriteLine($"!! {missing} 项没找到——混淆名很可能随游戏更新变了。");
                Console.WriteLine("   重跑 pwsh tools/dump-symbols.ps1，按 docs/symbols.md 的识别特征重新定位，只改 GameSymbols.cs。");
                return 2;
            }

            Console.WriteLine($"全部命中：{typeRefs.Count} 个类型、{memberRefs.Count} 个成员引用。");
            return 0;
        }

        private static bool IsGame(TypeReference tr)
        {
            var scope = tr.GetElementType().Scope;
            return scope != null && scope.Name.StartsWith("Assembly-CSharp", StringComparison.Ordinal);
        }

        /// <summary>从 sigcheck 自己的输出目录往上找仓库根，定位构建产物。</summary>
        private static string DefaultModDll()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "TbhCombatTracker", "bin", "Release", "TbhCombatTracker.dll");
                if (File.Exists(Path.Combine(dir.FullName, "src", "TbhCombatTracker", "TbhCombatTracker.csproj")))
                    return candidate;
                dir = dir.Parent;
            }
            return "TbhCombatTracker.dll";
        }

        // ------------------------------------------------------------------ 其它模式

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

        private static int Dump(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("用法: sigcheck --dump <类型全名...>");
                return 1;
            }

            var path = Path.Combine(DefaultInteropDir(), "Assembly-CSharp.dll");
            var mod = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred }).MainModule;

            var missing = 0;
            foreach (var name in args)
            {
                Console.WriteLine($"── {name} ──");
                var t = mod.GetType(name);
                if (t == null)
                {
                    Console.WriteLine("   !! 找不到该类型\n");
                    missing++;
                    continue;
                }

                // Il2CppInterop 把非 blittable 的值类型生成成继承 Il2CppSystem.ValueType 的 class，
                // 字段则一律变成属性。Harmony 补丁的参数类型要按这个来。
                Console.WriteLine($"   isValueType={t.IsValueType}  base={t.BaseType?.FullName}");
                foreach (var f in t.Fields.Where(f => !f.IsStatic).Take(24))
                    Console.WriteLine($"     field  {Short(f.FieldType.FullName)} {f.Name}");
                foreach (var p in t.Properties.Take(24))
                    Console.WriteLine($"     prop   {Short(p.PropertyType.FullName)} {p.Name}");
                Console.WriteLine();
            }
            return missing > 0 ? 2 : 0;
        }

        private static int SimulateUpdate(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("用法: sigcheck --simulate-update <输入dll> <输出dll>");
                return 1;
            }

            var asm = AssemblyDefinition.ReadAssembly(args[0]);
            var obfuscated = new System.Text.RegularExpressions.Regex("^[a-z]{2,4}$");
            var renamed = new List<string>();

            foreach (var tr in asm.MainModule.GetTypeReferences())
            {
                if (tr.Scope == null || !tr.Scope.Name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(tr.Namespace) || tr.DeclaringType != null) continue;
                if (!obfuscated.IsMatch(tr.Name)) continue;
                renamed.Add(tr.Name);
                tr.Name = tr.Name + "_gone";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
            asm.Write(args[1]);
            Console.WriteLine($"已把 {renamed.Count} 处游戏类型引用改成不存在的名字：{string.Join(", ", renamed.Distinct())}");
            Console.WriteLine($"写出 {args[1]}");
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

        // ------------------------------------------------------------------ 小工具

        private static string Signature(MethodDefinition m)
            => $"{Short(m.ReturnType.FullName)} {m.Name}(" +
               string.Join(", ", m.Parameters.Select(p => Short(p.ParameterType.FullName) + " " + p.Name)) + ")";

        private static string DefaultInteropDir()
        {
            // 构建时由 csproj 把 BepInExInteropDir 烧进程序集元数据
            var meta = typeof(Program).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "InteropDir");
            return meta?.Value ?? ".";
        }

        private static string Short(string full)
        {
            if (full == null) return "?";
            full = full.Replace("System.", "").Replace("UnityEngine.", "");
            var i = full.LastIndexOf('/');
            return i >= 0 ? full.Substring(i + 1) : full;
        }
    }
}
