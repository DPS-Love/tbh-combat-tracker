// ============================================================================
//  游戏符号表 —— 游戏更新后【只改这一个文件】
//
//  游戏用 GUPS.Obfuscator，方法名 / 字段名 / 内部类名每次更新都可能变。
//  所有混淆名只允许出现在这里：
//    1. 类型别名（global using，对整个项目生效）
//    2. Harmony 按名字找的方法（[Hook] 标注的常量）
//    3. 字段 / 属性访问器（混淆的成员名藏在方法体里）
//
//  每一条后面写着"怎么重新定位"的判据；完整方法论见 docs/symbols.md 第 11 节。
//  改完构建，跑 `dotnet run --project tools/sigcheck`：它直接读构建出的 DLL，
//  核对这里引用的每一个类型和成员在当前 interop 里是否还在——不需要再维护第二份清单。
//
//  【生存路径】Plugin.Load / TrackerBehaviour / Overlay / UpdateBanner 这条路上的代码
//  不引用这里的任何别名——类型不存在时它们照样要跑，见 docs/DEVELOPMENT.md「四条硬规则」。
// ============================================================================

// ---------------------------------------------------------------- 类型
// 未混淆（顶层单位类、public struct、数据类）——历次更新都没变过
global using GUnit = TaskbarHero.Unit;
global using GHero = TaskbarHero.Hero;
global using GMonster = TaskbarHero.Monster;
global using GDamageInfo = TaskbarHero.DamageInfo;
global using GPriestHeal = TaskbarHero.Combat.PriestHeal;
global using GPriestSanctuary = TaskbarHero.Combat.PriestSanctuary;
global using GActiveSkill = TaskbarHero.Combat.ActiveSkill;
global using GHeroActiveSkill = TaskbarHero.Combat.HeroActiveSkill;
global using GExplosiveBolt = TaskbarHero.Combat.Projectile.HunterExplosiveBolt;
global using GStageManager = TaskbarHero.StageManager;
global using GStageState = TaskbarHero.EStageState;
global using GUiStage = TaskbarHero.UI_Stage;
global using GHeroInfoData = TaskbarHero.Data.HeroInfoData;
global using GSkillInfoData = TaskbarHero.Data.SkillInfoData;

// 混淆的（全局命名空间，两三个小写字母）——每次更新都要重新定位
global using GUnitHealth = global::pp;      // UnitHealth     ← 编译器生成的 <HealthRegenAsync>d__20 状态机的宿主类；Unit.UnitHealthController 的类型
global using GHeroHealth = global::pl;      // HeroHealth     ← : GUnitHealth，唯一的 private Hero 字段，且覆写了 ChangeHp
global using GMonsterHealth = global::pn;   // MonsterHealth  ← : GUnitHealth，唯一的 private Monster 字段，不覆写 ChangeHp
global using GWindowNative = global::ou;    // 窗口控制       ← 调 SetWindowLong / GetWindowLong 的那个类（不是放 extern 的那个）
global using GLoc = global::oa;             // 本地化包装     ← 五个 [Extension] static string 方法的静态类
global using GHealField = global::bhh;      // 治疗场         ← PriestSanctuary 偏移 0x80 的字段类型

using System;

namespace TbhCombatTracker
{
    /// <summary>
    /// 标在方法名常量上：Harmony 会在这些类型上按名字找它。
    /// sigcheck 从编译产物里读这个特性，据此核对方法是否仍在这些类型上声明。
    /// 只是元数据，运行时没人反射它，类型不存在也不会出事。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class HookAttribute : Attribute
    {
        public Type[] Types { get; }
        public HookAttribute(params Type[] types) => Types = types;
    }

    internal static class GameSymbols
    {
        // ------------------------------------------------------------ Harmony 按名字找的方法

        /// <summary>UnitHealth.ChangeHp(float delta, Unit source)：负 = 伤害，正 = 治疗。
        /// 判据：(float, Unit) 这个签名在 UnitHealth 上唯一。HeroHealth 覆写了它，要单独挂。</summary>
        [Hook(typeof(GUnitHealth), typeof(GHeroHealth))]
        public const string ChangeHp = "gxf";

        /// <summary>TakeDamage(DamageInfo, bool)。判据：Hero / Monster 各自覆写且 RVA 互异。
        /// 同签名还有一个 Hero / Monster 共用同一段机器码的——那是 IDamageable 的转发器，绝不能挂。</summary>
        [Hook(typeof(GMonster), typeof(GHero), typeof(GUnit))]
        public const string TakeDamage = "gvu";

        /// <summary>Unit 的击杀方法 bool (Unit)。判据：唯一既收 Unit 参数、又调用恢复总入口的方法。</summary>
        [Hook(typeof(GUnit))]
        public const string OnKilled = "gwm";

        /// <summary>所有生命恢复的总入口 (float, bool, bool)。判据：同签名的几个候选里唯一有调用点的，
        /// 且其中 2 处来自 TakeDamage、1 处来自 OnKilled——历次更新这个分布都没变。</summary>
        [Hook(typeof(GUnitHealth))]
        public const string HealFunnel = "hcy";

        /// <summary>窗口点击穿透开关 (bool)。判据：GWindowNative 的几个 (bool) 方法里唯一被
        /// WindowManager.Update() 调用的那个。</summary>
        [Hook(typeof(GWindowNative))]
        public const string ClickThrough = "gql";

        /// <summary>技能执行入口 override void ()。判据：PriestHeal / PriestSanctuary 各自覆写、RVA 互异。</summary>
        [Hook(typeof(GPriestHeal), typeof(GPriestSanctuary))]
        public const string SkillExecute = "nrx";

        /// <summary>每个技能生成自己 DamageInfo 的惰性工厂。名字没被混淆。</summary>
        [Hook(typeof(GActiveSkill), typeof(GExplosiveBolt))]
        public const string SkillDamageFactory = "AttackDamage";

        // ------------------------------------------------------------ 字段 / 属性访问器
        // 全部允许传 null，返回 null。混淆的成员名只出现在这几行的右边。

        /// <summary>HeroHealth 服务的那个 Hero。判据：HeroHealth 上唯一的 Hero 字段（偏移 0x58）。</summary>
        public static GHero HeroOf(GHeroHealth health) => health?.befe;

        /// <summary>技能的施法者。判据：ActiveSkill 偏移 0x38 的 Unit 字段（HeroActiveSkill 0x78 还有一份 Hero，同一个人）。</summary>
        public static GUnit OwnerOf(GActiveSkill skill) => skill?.bizb;

        /// <summary>「治愈」这次的目标。判据：PriestHeal 偏移 0x80 的 Hero 字段——施法者在基类里已有两份，
        /// 子类这份只能是目标；PriestSanctuary 同一偏移放的是治疗场对象。**不是施法者，别拿去归因。**</summary>
        public static GHero HealTargetOf(GPriestHeal skill) => skill?.birj;

        /// <summary>英雄的静态数据（职业、名字键）。判据：Hero.cache（未混淆）的类型上唯一的 HeroInfoData 字段（0x30）。</summary>
        public static GHeroInfoData HeroInfoOf(GHero hero) => hero?.cache?.bgtb;

        /// <summary>技能的静态数据（名字键）。判据：ActiveSkill.skillCache（未混淆）的类型上唯一的 SkillInfoData 字段（0x10）。</summary>
        public static GSkillInfoData SkillInfoOf(GActiveSkill skill) => skill?.skillCache?.bgur;

        /// <summary>查本地化表，返回玩家当前语言。判据：GLoc 五个方法在 dump 里的顺序
        /// 对应 1.01.05 的 gft/gfu/gfv/gfw/gfx，第三个是走本地化表的那个。</summary>
        public static string Localized(string key) => GLoc.gkf(key);

        /// <summary>英文源文本，只作兜底。判据：五个方法里的第一个。</summary>
        public static string SourceText(string key) => GLoc.gkd(key);
    }
}
