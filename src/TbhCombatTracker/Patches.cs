using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// 游戏类型别名。混淆名的含义见 docs/symbols.md。
// 游戏更新后如果编译不过，绝大概率只需要改这几行。
//
// BepInEx 的 Il2CppInterop 保留原始命名空间，所以是 TaskbarHero.Unit；
// 无命名空间的类型（pj / ph）留在全局命名空间，用 global:: 才能起别名。
// （换成 MelonLoader 的话这里要全部加 Il2Cpp 前缀：Il2CppTaskbarHero.Unit / Il2Cpp.pj）
using GUnit = TaskbarHero.Unit;
using GHero = TaskbarHero.Hero;
using GMonster = TaskbarHero.Monster;
using GDamageInfo = TaskbarHero.DamageInfo;
using GUnitHealth = global::pj;      // UnitHealth
using GMonsterHealth = global::ph;   // MonsterHealth
using GHeroHealth = global::pf;      // HeroHealth
using GWindowNative = global::on;    // Win32 窗口样式控制（点击穿透开关）
using GPriestHeal = TaskbarHero.Combat.PriestHeal;
using GPriestSanctuary = TaskbarHero.Combat.PriestSanctuary;
using GActiveSkill = TaskbarHero.Combat.ActiveSkill;
using GExplosiveBolt = TaskbarHero.Combat.Projectile.HunterExplosiveBolt;

namespace TbhCombatTracker
{
    internal static class Patches
    {
        // UnitHealth.ChangeHp(float delta, Unit source)：**负数 = 伤害，正数 = 治疗**。
        // 这是实测出来的（诊断模式），不是从签名猜的。
        //   Monster.grd(DamageInfo, false)
        //   pj.gsi(-735.586, Unit("Hero_301(Clone)"))     ← 怪物承伤，第二参就是攻击者
        //   pj.gxq(1.5, false, false) → pf.gsi(1.5, null) ← 英雄回血，同一个入口
        //
        // 曾经押错过 gsd(Unit, Vector3, float)：那是血条初始化，生成时调一次，
        // float 恒为 -0.875，第一参是单位自己。签名看着像伤害，其实完全不是。
        private const string HpDeltaMethod = "gsi";

        // 分类 hook 用 grd 而不是 een，原因是 IL2CPP 的全局方法体去重：
        // Hero.een 和 Monster.een 编译结果完全相同，被合并到同一段机器码 (0xC94760)。
        // detour 它会同时劫持英雄的那条路径，Harmony wrapper 把 Hero 往 Monster 上转型 →
        // NullReferenceException 刷屏。而 Monster.grd 的机器码全局唯一，安全。
        // （een 在两个类上代码相同这件事本身也说明它只是个转发器，grd 才是各自的真实现。）
        // 哪些方法全局唯一：python tools/safe-hooks.py
        // grd = TakeDamage。Monster / Hero / Unit 各有各的覆写且机器码全局唯一：
        //   Monster.grd —— 怪物承伤 = 我方输出的分类来源
        //   Hero.grd    —— 英雄承伤 = 承伤面板的分类来源
        //   Unit.grd    —— 基类，另外兼作"战斗回复"的判定括号
        private const string TakeDamageMethod = "grd";

        // on.glu(bool) —— 每帧由 WindowManager.Update() 调用，开关窗口的 WS_EX_TRANSPARENT。
        // 交叉引用确认过：on 里三个 (bool) 方法只有 glu 有调用点，另两个是死代码；
        // 且只有 glu 的机器码里含 GWL_EXSTYLE(-20) 和 WS_EX_LAYERED。
        private const string ClickThroughMethod = "glu";

        // PriestHeal.mti() —— 牧师主动治疗的执行入口，机器码全局唯一。
        // 治疗和伤害共用 UnitHealth.ChangeHp，但治疗那条路径的 Unit source 恒为 null
        // （实测 pf.gsi(1.5, null)），在血量入口无法归因。所以改从技能侧夹上下文：
        // mti() 前后记下 / 清除"当前治疗者"，中间落到 gsi 的正数增量就归给它。
        // 和伤害那套 Monster.grd → pj.gsi 的配对是同一个套路。
        private const string PriestHealMethod = "mti";

        // 恢复来源的上游括号。交叉引用确认这三处都会走到 pj.gxq：
        //   Unit.grd(DamageInfo, bool)  2 处 —— 伤害结算内的生命偷取 / 每次攻击回复
        //   Unit.grt(Unit killer)       1 处 —— 击杀时的处决回复
        //   PriestHeal.mti()                —— 牧师主动治疗
        // 三个方法的机器码都全局唯一（python tools/safe-hooks.py 确认）。
        private const string UnitTakeDamageMethod = "grd";
        private const string UnitOnKilledMethod = "grt";
        private const string HealFunnelMethod = "gxq";   // pj.gxq —— 所有恢复的总入口

        // ActiveSkill.AttackDamage() —— 每个技能生成自己 DamageInfo 的工厂，方法名未混淆。
        // 它是**命中时才求值的惰性工厂**（基类只有 1 个直接调用点，其余全是虚分发/委托），
        // 所以弹道和 AOE 的延迟伤害也会走到，是做技能级归因最准的位置。
        // 53 个技能类里只有 HunterExplosiveBolt 覆写了它，所以挂基类 + 它就够全覆盖。
        private const string SkillDamageFactoryMethod = "AttackDamage";
        private const string SkillExecuteMethod = "mti";

        public static void ApplyAll(Harmony harmony)
        {
            // 主 hook：怪物承伤 = 我方输出。这条失败的话 mod 就没意义了。
            // 注意挂的是基类 pj —— ph(MonsterHealth) 没有覆写 gsi，怪物走的就是基类实现。
            var ok = TryPatch(harmony, typeof(GUnitHealth), HpDeltaMethod,
                postfix: nameof(UnitHealth_HpDelta_Post));

            if (!ok)
            {
                Mod.Log.Error(
                    $"主 hook {typeof(GUnitHealth).FullName}.{HpDeltaMethod} 挂载失败，" +
                    "伤害统计不会工作。游戏可能已更新导致混淆名变化，请重跑 tools/dump-symbols.ps1 并对照 docs/symbols.md。");
            }

            // 辅助 hook：拿暴击 / 伤害类型 / 伤害属性。失败只是丢失分类，数值仍然准确。
            if (!TryPatch(harmony, typeof(GMonster), TakeDamageMethod,
                    prefix: nameof(TakeDamage_Pre),
                    finalizer: nameof(TakeDamage_Fin)))
            {
                Mod.Log.Warning(
                    "伤害分类 hook 挂载失败：总伤害/DPS 仍然准确，但暴击率和元素/类型拆分会全部为空。");
            }

            // 英雄承伤的分类。之前只挂了 Monster.grd，所以承伤面板点开没有类型/元素两栏。
            // Hero.grd 是独立覆写、机器码全局唯一，可以安全另挂一份。
            if (!TryPatch(harmony, typeof(GHero), TakeDamageMethod,
                    prefix: nameof(TakeDamage_Pre),
                    finalizer: nameof(TakeDamage_Fin)))
            {
                Mod.Log.Warning("英雄承伤分类 hook 挂载失败：承伤面板将没有类型/元素拆分。");
            }

            // 可选 hook：英雄承伤。pf(HeroHealth) 覆写了 gsi，所以必须单独挂它，
            // 挂基类 pj 抓不到英雄——虚函数分发会走覆写。
            if (Mod.Config.TrackIncoming.Value)
            {
                if (!TryPatch(harmony, typeof(GHeroHealth), HpDeltaMethod,
                        postfix: nameof(HeroHealth_HpDelta_Post)))
                {
                    Mod.Log.Warning("承伤统计 hook 挂载失败，只统计输出。");
                }
            }

            // 点击穿透修正：让面板能接收鼠标。失败只是按钮点不了，不影响统计。
            if (Mod.Config.FixClickThrough.Value)
            {
                if (!TryPatch(harmony, typeof(GWindowNative), ClickThroughMethod,
                        prefix: nameof(WindowStyle_Pre),
                        postfix: nameof(WindowStyle_Post)))
                {
                    Mod.Log.Warning("穿透修正 hook 挂载失败：面板只能看，按钮点不了也拖不动。");
                }
            }

            // 治疗归因：在三个上游入口夹上下文，好让血量入口知道这次恢复是哪来的。
            // 任一失败只是该来源被并进"自然回复"，不影响总量。
            if (Mod.Config.TrackHealing.Value)
            {
                TryPatch(harmony, typeof(GPriestHeal), PriestHealMethod,
                    prefix: nameof(PriestHeal_Pre), finalizer: nameof(PriestHeal_Fin));

                TryPatch(harmony, typeof(GUnit), UnitTakeDamageMethod,
                    prefix: nameof(UnitTakeDamage_Pre), finalizer: nameof(UnitTakeDamage_Fin));

                TryPatch(harmony, typeof(GUnit), UnitOnKilledMethod,
                    prefix: nameof(UnitOnKilled_Pre), finalizer: nameof(UnitOnKilled_Fin));

                TryPatch(harmony, typeof(GUnitHealth), HealFunnelMethod,
                    prefix: nameof(HealFunnel_Pre), finalizer: nameof(HealFunnel_Fin));

                // 圣域和治愈都是 (true,true) 标志，分不开；各挂各的执行入口才能区分。
                TryPatch(harmony, typeof(GPriestSanctuary), SkillExecuteMethod,
                    prefix: nameof(Sanctuary_Pre), finalizer: nameof(Sanctuary_Fin));
            }

            // 技能级归因：伤害饼图的数据来源。失败只是技能维度缺失，总量不受影响。
            if (Mod.Config.TrackSkills.Value)
            {
                if (!TryPatch(harmony, typeof(GActiveSkill), SkillDamageFactoryMethod,
                        postfix: nameof(SkillDamage_Post)))
                {
                    Mod.Log.Warning("技能归因 hook 挂载失败：伤害仍会统计，但拆不出技能维度。");
                }

                TryPatch(harmony, typeof(GExplosiveBolt), SkillDamageFactoryMethod,
                    postfix: nameof(SkillDamage_Post));
            }

            if (Mod.Config.DiagnosticMode.Value)
                Diagnostics.Apply(harmony);
        }

        /// <summary>
        /// 已挂载的原生函数地址。IL2CPP 会把方法体相同的函数去重成同一段机器码，
        /// 对同一地址挂两次 detour 会无限递归栈溢出（详见 <see cref="Il2CppUtil.NativePointer"/>）。
        /// 目前三个目标各自独立，但游戏更新后可能变成共用，所以这里守一道。
        /// </summary>
        private static readonly Dictionary<IntPtr, string> PatchedNative = new Dictionary<IntPtr, string>();

        private static bool TryPatch(Harmony harmony, Type target, string method,
                                     string prefix = null, string postfix = null, string finalizer = null)
        {
            try
            {
                var original = AccessTools.DeclaredMethod(target, method);
                if (original == null)
                {
                    Mod.Log.Error($"找不到方法 {target.FullName}.{method}");
                    return false;
                }

                var native = Il2CppUtil.NativePointer(original);
                if (native != IntPtr.Zero)
                {
                    if (PatchedNative.TryGetValue(native, out var owner))
                    {
                        Mod.Log.Error(
                            $"{target.FullName}.{method} 与 {owner} 被 IL2CPP 去重到同一段机器码 " +
                            $"(0x{native.ToInt64():X})，跳过以免双重 detour 导致栈溢出。" +
                            "游戏更新后方法体可能变得相同，需要重新挑 hook 点。");
                        return false;
                    }
                    PatchedNative[native] = $"{target.FullName}.{method}";
                }

                harmony.Patch(original,
                    prefix: Hook(prefix),
                    postfix: Hook(postfix),
                    finalizer: Hook(finalizer));

                Mod.Log.Msg($"已挂载 {target.FullName}.{method}");
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Error($"挂载 {target.FullName}.{method} 失败：{e}");
                return false;
            }
        }

        private static HarmonyMethod Hook(string name)
            => name == null ? null : new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Patches), name));

        // ------------------------------------------------------------------
        // 参数名必须和游戏里的形参名逐字一致（a / b / c），Harmony 是按名字注入的。
        // ------------------------------------------------------------------

        /// <summary>
        /// UnitHealth.ChangeHp(float delta, Unit source) —— 我方输出。
        /// 只统计怪物身上的扣血；英雄和建筑各走各的覆写/分支。
        /// </summary>
        private static void UnitHealth_HpDelta_Post(GUnitHealth __instance, float a, GUnit b)
        {
            try
            {
                if (__instance == null) return;

                // 只要怪物承的伤。pf(英雄) 若调了 base.gsi 也会走到这里，必须挡掉，否则重复计数。
                if (__instance.TryCast<GMonsterHealth>() == null) return;

                if (a >= 0f) return; // 正数是治疗
                var amount = -a;

                // 攻击者可能为 null（陷阱、环境伤害、召唤物主人已死等）。
                // 不能直接丢弃，否则总伤害对不上——归到"未知来源"。
                if (b == null)
                {
                    DamageTracker.RecordOutgoing(0, SourceIdentity.Plain(BuiltinText.UnknownSource),
                                                 amount, SkillTracker.For(0));
                    return;
                }

                var attackerId = b.GetInstanceID();
                DamageTracker.RecordOutgoing(attackerId, Naming.For(b), amount,
                                             SkillTracker.For(attackerId));
            }
            catch (Exception e)
            {
                LogOnceInternal("UnitHealth_HpDelta_Post", e);
            }
        }

        /// <summary>
        /// HeroHealth.ChangeHp(float delta, Unit source) —— 英雄承伤（负）与回血（正）。
        /// 两者共用同一个入口，只是符号相反。
        /// </summary>
        private static void HeroHealth_HpDelta_Post(GHeroHealth __instance, float a, GUnit b)
        {
            try
            {
                if (__instance == null || a == 0f) return;

                if (a < 0f)
                {
                    // 承伤按挨打的那个英雄归因
                    // 承伤也带上技能维度——明细窗口里能看出"被什么技能打的"。
                    // 同样按英雄 id 归档，和治疗/输出保持同一套键。
                    DamageTracker.RecordIncoming(HeroIdOf(__instance),
                                                 Naming.ForHealth(__instance), -a,
                                                 SkillTracker.For(0));
                    return;
                }

                if (!Mod.Config.TrackHealing.Value) return;

                // 这里拿到的是**实际生效**的恢复量（gxq 传下来的值可能因满血被削），
                // 分类则来自上游括号。
                RecordHeal(__instance, a);
            }
            catch (Exception e)
            {
                LogOnceInternal("HeroHealth_HpDelta_Post", e);
            }
        }

        /// <summary>
        /// 把一次实际生效的恢复计入统计。
        /// 技能治疗归施法者（和伤害归攻击者一致），自愈类归被恢复的单位自己。
        /// </summary>
        private static void RecordHeal(GHeroHealth health, float amount)
        {
            var kind = Healing.Resolve();

            // 技能治疗归施法者（和伤害归攻击者一致）。括号内能直接拿到；
            // 延迟落地的那部分靠记住的"最近一次施法者"补上。
            // Sanctuary 也必须算进来——漏了它，圣域的治疗会被记到被治疗者头上。
            var isSkill = kind == HealKind.PriestHeal
                       || kind == HealKind.Sanctuary
                       || kind == HealKind.Skill;

            if (isSkill && Healing.TryGetCaster(out var caster))
            {
                DamageTracker.RecordHealing(caster.InstanceId, caster, amount, kind);
            }
            else if (isSkill && Healing.TryGetLastCaster(out var last))
            {
                DamageTracker.RecordHealing(last.InstanceId, last, amount, kind);
            }
            else
            {
                // 自愈类（自然回复 / 战斗回复 / 处决回复）归被恢复的单位自己。
                //
                // 【必须用英雄的实例 id，不能用血量组件的】
                // 技能治疗那两条分支用的是英雄 id（caster.InstanceId），
                // 这里若用 health.GetInstanceID() 就成了两个不同的键，
                // 同一个牧师会在面板上裂成两张卡片——自愈一张、技能治疗一张。
                var who = Naming.ForHealth(health);
                DamageTracker.RecordHealing(HeroIdOf(health), who, amount, kind);
            }

            if (Mod.Config.HealingDebug.Value)
            {
                // 排查用：绕过 gxq 直接改血的恢复路径会被归成"自然回复"，
                // 记一条好知道有没有漏。
                if (!Healing.InFunnel)
                    LogOnceInternal("回血未经过 gxq 漏斗",
                        new Exception($"amount={amount:0.##} kind={Healing.KindName((int)kind)}"));

                // 括号和标志位不一致时记一条——分类依据出问题时能第一时间发现
                var bracket = Healing.CurrentKind;
                if (Healing.InFunnel && bracket != kind &&
                    bracket != HealKind.Regen)
                    Mod.Log.Msg($"[heal] 判定分歧: 括号={Healing.KindName((int)bracket)} " +
                                $"标志=({Healing.FlagB},{Healing.FlagC})->{Healing.KindName((int)kind)}");
            }
        }

        /// <summary>
        /// 取血量组件背后那个英雄的实例 id。统计的键必须始终是**英雄**，
        /// 不能有时用英雄、有时用血量组件，否则同一个人会被拆成两条记录。
        /// </summary>
        private static int HeroIdOf(GHeroHealth health)
        {
            try
            {
                var hero = health?.bdeg;
                if (hero != null) return hero.GetInstanceID();
            }
            catch { /* 退回组件 id 总比崩了强 */ }

            return health != null ? health.GetInstanceID() : 0;
        }

        // ---- 三个上游括号。用 __state 保存/恢复，天然支持嵌套 ----

        /// <summary>PriestHeal.mti() —— 牧师「治愈」。</summary>
        private static void PriestHeal_Pre(GPriestHeal __instance,
                                           out (HealKind, SourceIdentity, bool) __state)
        {
            __state = default;
            try
            {
                var hero = __instance?.bhfz;
                if (hero != null)
                {
                    // 治疗延迟落地，括号跨不过去，所以额外记住施法者
                    Healing.RememberCaster(Naming.For(hero));
                    __state = Healing.Enter(HealKind.PriestHeal, Naming.For(hero));
                }
                else
                {
                    __state = Healing.Enter(HealKind.PriestHeal);
                }

                if (Mod.Config.HealingDebug.Value)
                    Mod.Log.Msg($"[heal] PriestHeal.mti() 施法者={(hero != null ? Naming.For(hero).Name : "?")}");
            }
            catch (Exception e)
            {
                LogOnceInternal("PriestHeal_Pre", e);
            }
        }

        /// <summary>用 Finalizer 而不是 Postfix：原方法抛异常时也要把括号恢复掉。</summary>
        private static Exception PriestHeal_Fin((HealKind, SourceIdentity, bool) __state,
                                                Exception __exception)
        {
            Healing.Restore(__state);
            return __exception;
        }

        /// <summary>
        /// ActiveSkill.AttackDamage() 的 Postfix —— 技能刚生成 DamageInfo，
        /// 记下是哪个技能，等伤害落地时归因。
        /// </summary>
        private static void SkillDamage_Post(GActiveSkill __instance)
        {
            try { SkillTracker.Remember(__instance); }
            catch (Exception e) { LogOnceInternal("SkillDamage_Post", e); }
        }

        /// <summary>PriestSanctuary.mti() —— 「圣域」，和「治愈」区分开。</summary>
        private static void Sanctuary_Pre(GPriestSanctuary __instance,
                                          out (HealKind, SourceIdentity, bool) __state)
        {
            __state = default;
            try
            {
                var hero = __instance?.bhnf?.TryCast<GHero>();
                if (hero != null)
                {
                    Healing.RememberCaster(Naming.For(hero));
                    __state = Healing.Enter(HealKind.Sanctuary, Naming.For(hero));
                }
                else
                {
                    __state = Healing.Enter(HealKind.Sanctuary);
                }

                if (Mod.Config.HealingDebug.Value)
                    Mod.Log.Msg($"[heal] PriestSanctuary.mti() 施法者={(hero != null ? Naming.For(hero).Name : "?")}");
            }
            catch (Exception e)
            {
                LogOnceInternal("Sanctuary_Pre", e);
            }
        }

        private static Exception Sanctuary_Fin((HealKind, SourceIdentity, bool) __state,
                                               Exception __exception)
        {
            Healing.Restore(__state);
            return __exception;
        }

        /// <summary>Unit.grd —— 伤害结算内触发的恢复 = 生命偷取 / 每次攻击回复。</summary>
        private static void UnitTakeDamage_Pre(out (HealKind, SourceIdentity, bool) __state)
            => __state = Healing.Enter(HealKind.InCombat);

        private static Exception UnitTakeDamage_Fin((HealKind, SourceIdentity, bool) __state,
                                                    Exception __exception)
        {
            Healing.Restore(__state);
            return __exception;
        }

        /// <summary>Unit.grt(Unit killer) —— 击杀时触发的恢复 = 处决回复。</summary>
        private static void UnitOnKilled_Pre(out (HealKind, SourceIdentity, bool) __state)
            => __state = Healing.Enter(HealKind.OnKill);

        private static Exception UnitOnKilled_Fin((HealKind, SourceIdentity, bool) __state,
                                                  Exception __exception)
        {
            Healing.Restore(__state);
            return __exception;
        }

        /// <summary>pj.gxq —— 所有恢复的总入口，只用来标记"确实走了漏斗"。</summary>
        private static void HealFunnel_Pre(GUnitHealth __instance, float a, bool b, bool c,
                                           out (bool, bool, bool) __state)
        {
            __state = Healing.EnterFunnel(b, c);

            if (Mod.Config.HealingDebug.Value)
            {
                var target = Naming.ForHealth(__instance).Name;
                Mod.Log.Msg($"[heal] gxq({a:0.##}, {b}, {c}) 目标={target} " +
                            $"来源={Healing.KindName((int)Healing.Resolve())}");
            }
        }

        private static Exception HealFunnel_Fin((bool, bool, bool) __state, Exception __exception)
        {
            Healing.RestoreFunnel(__state);
            return __exception;
        }

        /// <summary>Monster.grd(DamageInfo info, bool _) = TakeDamage —— 在结算前后夹住分类上下文。</summary>
        private static void TakeDamage_Pre(GDamageInfo a, bool b, out DamageContext __state)
        {
            __state = default;
            try
            {
                __state = DamageTracker.PushContext(
                    crit: a.IsCritical,
                    damageType: (int)a.DamageType,
                    damageAttribute: (int)a.DamageAttribute,
                    originDamage: a.OriginDamage);
            }
            catch (Exception e)
            {
                LogOnceInternal("TakeDamage_Pre", e);
            }
        }

        /// <summary>光标在面板上时，把游戏的"点击穿透"参数改成可交互档。</summary>
        private static void WindowStyle_Pre(ref bool a)
        {
            try { ClickThrough.OverrideArg(ref a); }
            catch (Exception e) { LogOnceInternal("WindowStyle_Pre", e); }
        }

        /// <summary>标定参数极性用：读一次调用后的真实窗口样式。</summary>
        private static void WindowStyle_Post(bool a)
        {
            try { ClickThrough.Observe(a); }
            catch (Exception e) { LogOnceInternal("WindowStyle_Post", e); }
        }

        /// <summary>Finalizer 即使原方法抛异常也会执行，保证上下文不会泄漏到下一次伤害。</summary>
        private static Exception TakeDamage_Fin(DamageContext __state, Exception __exception)
        {
            DamageTracker.RestoreContext(__state);
            return __exception; // 原样抛回去，不吞游戏的异常
        }

        private static readonly System.Collections.Generic.HashSet<string> Logged =
            new System.Collections.Generic.HashSet<string>();

        internal static void LogOnceInternal(string where, Exception e)
        {
            // hook 在伤害热路径上，一帧可能几十次，绝不能刷屏。
            lock (Logged)
            {
                if (!Logged.Add(where)) return;
            }
            Mod.Log.Error($"{where} 抛异常（后续同类异常将被静默）：{e}");
        }
    }

    /// <summary>一个伤害来源的身份：显示名 + 职业（EEquipClassType，0 表示未知）。</summary>
    internal struct SourceIdentity
    {
        public string Name;
        public int ClassType;
        /// <summary>来源单位的实例 id，0 表示没有具体单位（环境伤害、自然回复等）。</summary>
        public int InstanceId;

        public static SourceIdentity Plain(string name) => new SourceIdentity { Name = name };
    }

    /// <summary>给伤害来源取一个人能看懂的名字和职业，并缓存，避免每次命中都做反射。</summary>
    internal static class Naming
    {
        private static readonly System.Collections.Generic.Dictionary<int, SourceIdentity> Cache =
            new System.Collections.Generic.Dictionary<int, SourceIdentity>();

        public static SourceIdentity For(GUnit unit)
        {
            var id = unit.GetInstanceID();
            lock (Cache)
            {
                if (Cache.TryGetValue(id, out var cached)) return cached;
            }

            var identity = Resolve(unit);
            identity.InstanceId = id;

            lock (Cache) Cache[id] = identity;
            return identity;
        }

        public static SourceIdentity ForHealth(GUnitHealth health)
        {
            var id = health.GetInstanceID();
            lock (Cache)
            {
                if (Cache.TryGetValue(id, out var cached)) return cached;
            }

            // pf(HeroHealth) 持有它服务的那个 Hero（字段 bdeg），顺着它去解析职业，
            // 否则承伤面板只会显示 Hero_401 这种 GameObject 名。
            // 注意复用 For(hero)：它按**英雄**的实例 id 缓存，所以同一个英雄在
            // 输出面板和承伤面板拿到的是同一份身份，不会被重名后缀判成两个人。
            SourceIdentity identity;
            var hero = SafeHeroOf(health);
            identity = hero != null
                ? For(hero)
                : SourceIdentity.Plain(Clean(SafeGameObjectName(health)) ?? $"Unit#{id}");

            lock (Cache) Cache[id] = identity;
            return identity;
        }

        /// <summary>本地化到的职业名，索引是 EEquipClassType；没查到的位置为 null。</summary>
        internal static readonly string[] LocalizedJobName = new string[JobTable.Count];

        /// <summary>同名单位出现第几个，用来给重复名字加 #2 / #3 后缀。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, int> NameSeq =
            new System.Collections.Generic.Dictionary<string, int>();

        private static SourceIdentity Resolve(GUnit unit)
        {
            var hero = unit.TryCast<GHero>();

            // 实测 GameObject 名形如 Hero_301(Clone) / Monster_30043(Clone)。
            var baseName = Clean(SafeGameObjectName(unit)) ?? (hero != null ? "Hero" : "Unit");
            var classType = hero != null ? ReadClassType(hero, baseName) : 0;

            // 优先用游戏本地化的职业名，其次是内置中文表，最后才是 GameObject 名
            var label = (classType > 0 && classType < LocalizedJobName.Length
                            ? LocalizedJobName[classType] : null)
                        ?? JobTable.Label(classType)
                        ?? baseName;

            if (Mod.Config.ProbeMode.Value)
                Probe(unit, baseName);

            // 只有真撞名了才加后缀，避免平时一堆没用的 #A3F
            lock (NameSeq)
            {
                NameSeq.TryGetValue(label, out var n);
                NameSeq[label] = n + 1;
                if (n > 0) label = $"{label}#{n + 1}";
            }

            return new SourceIdentity { Name = label, ClassType = classType };
        }

        /// <summary>
        /// 从 Hero.cache(vo).bflj(HeroInfoData).ClassType 读职业。
        /// HeroInfoData 的字段没被混淆（HeroKey / ClassType / HeroNameKey / IconPath），
        /// 是权威来源——不要去猜 GameObject 名里的数字。
        /// </summary>
        private static int ReadClassType(GHero hero, string baseName)
        {
            try
            {
                var info = hero.cache?.bflj;
                if (info == null) return 0;

                var classType = (int)info.ClassType;

                // 用游戏自己的本地化名，而不是我们硬编码的中文——跟随玩家的语言设置。
                // HeroInfoData.HeroNameKey 是权威的本地化键（实测形如 "HeroName_401"），
                // 查不到才退回硬编码表。
                var localized = Localize.TryGet(info.HeroNameKey);
                if (!string.IsNullOrWhiteSpace(localized))
                    LocalizedJobName[classType] = localized;

                // 每个英雄只会走到这里一次（外层有缓存），打一条方便核对映射关系
                Mod.Log.Msg($"识别英雄 {baseName}: HeroKey={info.HeroKey} " +
                            $"ClassType={info.ClassType}({classType}) " +
                            $"NameKey='{info.HeroNameKey}' -> " +
                            $"{localized ?? JobTable.Label(classType) ?? "?"}" +
                            (localized == null ? "（本地化查不到，用内置名）" : ""));

                return classType;
            }
            catch (Exception e)
            {
                LogOnce("ReadClassType", e);
                return 0;
            }
        }

        /// <summary>
        /// 调试用：把这个 Unit 上几个疑似"名字"的成员全部打出来，
        /// 好确定到底该用 gpz() 还是 gqa() 还是 GameObject 名。见 docs/symbols.md 第 6 节。
        /// </summary>
        private static void LogOnce(string where, Exception e) => Patches.LogOnceInternal(where, e);

        private static void Probe(GUnit unit, string baseName)
        {
            var log = Mod.Log;
            log.Msg($"[probe] instanceId={unit.GetInstanceID()} gameObject='{baseName}' " +
                    $"type={unit.GetIl2CppType().FullName}");

            foreach (var m in new[] { "gpz", "gqa" })
            {
                try
                {
                    var mi = AccessTools.DeclaredMethod(unit.GetType(), m)
                             ?? AccessTools.Method(unit.GetType(), m);
                    if (mi == null || mi.GetParameters().Length != 0) continue;
                    log.Msg($"[probe]   {m}() = '{mi.Invoke(unit, null)}'");
                }
                catch (Exception e)
                {
                    log.Msg($"[probe]   {m}() 调用失败：{e.GetType().Name}");
                }
            }
        }

        /// <summary>从 HeroHealth 取它服务的 Hero；不是英雄血条就返回 null。</summary>
        private static GUnit SafeHeroOf(GUnitHealth health)
        {
            try
            {
                return health.TryCast<GHeroHealth>()?.bdeg;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGameObjectName(UnityEngine.Object o)
        {
            try { return o.name; } catch { return null; }
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            // Unity 实例化出来的对象名普遍带 "(Clone)" 后缀，看着碍眼
            const string clone = "(Clone)";
            while (s.EndsWith(clone, StringComparison.Ordinal))
                s = s.Substring(0, s.Length - clone.Length).TrimEnd();
            return s.Length == 0 ? null : s;
        }
    }
}
