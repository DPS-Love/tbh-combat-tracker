# Task Bar Hero — 逆向符号表

> 来源：Il2CppDumper v6.7.46 对 `GameAssembly.dll` + `global-metadata.dat` 的 dump
> 游戏版本：**1.2.2**（`Version.txt`）／Unity **6000.0.72f1** ／ IL2CPP ／ metadata v31
>
> 1.01.05 → 1.2.0 混淆名**全部变了**，重定位过程见第 11 节；
> 1.2.0 → 1.2.2 只有**字段名整体平移**（方法名没动），见第 12 节。
> 下文正文里的名字是 1.2.2 的。

## 0. 混淆规律（重要）

游戏用了 **GUPS.Obfuscator**，但只混淆了一部分：

| 保留原名 | 被混淆 |
|---|---|
| 枚举类型名 + 枚举成员名 | 绝大多数方法名（`een`、`gsd`、`gpz`…） |
| `[SerializeField]` 标记的字段（Unity 序列化不能改名） | 非序列化的私有/保护字段（`bdfa`、`bdhe`…） |
| `public` struct 的字段（`DamageInfo.Attacker` 等） | 大部分内部类名（`pj`、`ph`、`bfc`…） |
| 顶层单位类 `Unit` / `Hero` / `Monster` | 属性名（`bsjm`、`bssi`…） |

另外，`Assets\02_Script\11.Combat\` 下的绝大多数类名**完全消失**——
`AreaOfEffect`、`TickDamageArea`、`Projectile_Base`、`Turret` 在 dump.cs 里一个都搜不到，
只有源文件路径还留在 metadata 里。侥幸存活的原名只有这几类：

- 单位基类 `Unit` / `Hero` / `Monster`
- `public` struct：`DamageInfo`、`FakeDieCreator`
- 全部枚举（类型名 + 成员名）
- 编译器生成的状态机名（`<HealthRegenAsync>d__20`、`<ApplyTickDamageAsync>d__8`）——
  混淆器不动这些，反而成了定位宿主类最可靠的锚点

**结论：游戏每次更新，混淆名很可能全部变化。** 这不是假设——1.01.05 → 1.2.0 时，
本文档记录的每一个混淆名都变了（`pj`→`pp`、`gsi`→`gvz`、`grd`→`gun`……），
连前缀风格都从 `g*`/`m*` 换成了 `e*`/`n*`。

本文件就是为了让重新对齐时有据可查——更新后重跑 `tools/dump-symbols.ps1`，
再按下面的"识别特征"重新定位即可。第 11 节是上一次重定位的完整过程，可以照着走。

## 1. 类型映射

| 混淆名 | 真实身份 | 命名空间 | 识别特征 |
|---|---|---|---|
| `bgg` | `IDamageable` | *(全局)* | 唯一被 `Unit` 实现的接口，有 `DamageableType` 属性 |
| `pp` | `UnitHealth` | *(全局)* | `MonoBehaviour`，含 `SpriteSlider HpBar` + `Action<float> OnHpChange`；<br>还有编译器生成的状态机 `pp.<HealthRegenAsync>d__20`——**这个名字没被混淆，是最硬的指纹**；<br>另外 `Unit.UnitHealthController` 属性名也没混淆，顺着它的类型也能找到 |
| `pl` | `HeroHealth` | *(全局)* | `: pp`，私有字段 `Hero bdvt`；**覆写了 `gvz`** |
| `pn` | `MonsterHealth` | *(全局)* | `: pp`，私有字段类型为 `Monster`；**不**覆写 `gvz` |
| `wg` | `HeroCache`（英雄运行时数据） | *(全局)* | 含 `HeroInfoData` 字段 + `Hero` 反向引用；`Hero.cache` 的类型 |
| `wl` | `SkillCache`（技能运行时数据） | *(全局)* | 含 `SkillInfoData` 字段；`ActiveSkill.skillCache` 的类型 |
| `ot` | 窗口控制 | *(全局)* | 调 `os.SetWindowLong` / `GetWindowLong` 的那个类 |
| `os` | Win32 P/Invoke 集合 | *(全局)* | 一堆 `extern`：`SetWindowLong` / `SetWindowPos` / `DwmExtendFrameIntoClientArea` |
| `nz` | 本地化包装 | *(全局)* | 五个 `[Extension] static string`，见第 10 节 |
| `bgn` | 治疗场（圣域生成的持续治疗区域） | *(全局)* | `PriestSanctuary` 调它的 `nxp(Unit, float, float, Vector3, UniqueModInfoData)` |
| `Unit` | — 未混淆 | `TaskbarHero` | `abstract class Unit : MonoBehaviour, bgg` |
| `Hero` | — 未混淆 | `TaskbarHero` | `class Hero : Unit`，`public wg cache` |
| `Monster` | — 未混淆 | `TaskbarHero` | `class Monster : Unit`，`public EMonsterType MonsterType` |
| `DamageInfo` | — 未混淆 | `TaskbarHero` | `struct`，字段全部保留原名 |

## 2. 伤害链路（已用运行时诊断实测确认）

```
Monster.gun(DamageInfo, bool)                 = TakeDamage
   │  内部完成暴击判定 / 抗性减免 / 吸收护盾结算
   └─> UnitHealthController.gvz(float delta, Unit source)   [pp slot 8] = ChangeHp
          delta < 0 → 伤害      delta > 0 → 治疗
```

这条链路是 1.01.05 用诊断模式实测出来的（当时叫 `Monster.grd` → `pj.gsi`）：

```
[diag] Monster.grd(DamageInfo, false)
[diag] pj.gsi(-735.586,  Unit("Hero_301(Clone)"))
[diag] Monster.grd(DamageInfo, false)
[diag] pj.gsi(-1284.333, Unit("Hero_301(Clone)"))
[diag] pj.gxq(1.5, false, false) → pf.gsi(1.5, null)      ← 治疗走同一入口，正数
```

1.2.0 只是换了名字，结构一模一样（对照见第 11 节）。

- **`gun` 提供分类信息**（暴击、伤害类型、伤害属性），但只有 `OriginDamage`（减免前）
- **`gvz` 提供最终数值**（负数）和来源（`Unit source`）
- 二者配对：`gun` 前后设置 / 清除"当前伤害上下文"，`gvz` 里读取

覆写关系决定了要挂哪个类：

| 承伤方 | 走哪个实现 | 要挂的 hook |
|---|---|---|
| 怪物 | `pn` **没有**覆写 `gvz` → 走基类 `pp.gvz` | `pp.gvz`，且 `__instance` 是 `pn` |
| 英雄 | `pl` **覆写了** `gvz` → 走 `pl.gvz` | `pl.gvz` |

> ⚠️ **`gvu(Unit, Vector3, float)` 不是伤害入口。** 签名看着像
> `ApplyDamage(attacker, hitPos, damage)`，实际是血条初始化：单位生成时调一次，
> 第一个参数是**单位自己**，float 恒为 `-0.875`。这里踩过坑，别再押它。
> （1.01.05 里它叫 `gsd`。注意 slot 号也变了：`gsd` 原本 slot 6、`gsi` slot 9，
> 现在 `gvz` 是 slot 8、`gvu` 是 slot 9——**别拿 slot 号当锚点，只认签名**。）
>
> 同理 `eha` 也不是——`Hero.eha` 和 `Monster.eha` 编译结果完全相同（共用 `0xCABFD0`），
> 说明它只是个转发器，`gun` 才是各自的真实现。而且共用机器码的方法不能安全 hook，见第 7 节。
> （1.01.05 里这对叫 `een` / `grd`，共用地址是 `0xC94760`。）

## 3. 关键签名

```csharp
// 全局命名空间 → Il2CppInterop 生成后为 Il2Cpp.*
public class pp : MonoBehaviour {
    public SpriteSlider HpBar;              // 0x20
    public Action<float> OnHpChange;        // 0x28
    protected Unit  bdyt;                   // 0x30  拥有者 Unit
    protected float bdyu;                   // 0x38  当前 HP
    protected float bdyv;                   // 0x3C  最大 HP
    public virtual void gvz(float a, Unit b);              // slot 8 = ChangeHp ← 【伤害入口】
    public virtual void gvu(Unit a, Vector3 b, float c);   // slot 9 = 血条初始化，**不是伤害**
    public void hbs(float a, bool b, bool c);              // 所有生命恢复的总入口
    public void hbr(Unit a, float b, bool c, bool d);      // 带来源的恢复，内部转 hbs
}
// pn 不覆写 gvz → 怪物承伤走基类 pp.gvz
public class pn : pp { private Monster bdwh; public override void gvu(Unit a, Vector3 b, float c); }
// pl 覆写了 gvz → 英雄承伤必须单独挂 pl.gvz
public class pl : pp { private Hero    bdvt; public override void gvz(float a, Unit b); }

// TaskbarHero → Il2CppTaskbarHero.*
public struct DamageInfo {
    public Unit  Attacker;                  // 0x00
    public float OriginDamage;              // 0x08   ← 减免前
    public bool  IsCritical;                // 0x0C
    public bool  FloatingDamageText;        // 0x0D
    public EDamageAttribute DamageAttribute;// 0x10
    public EDamageType      DamageType;     // 0x14
    public bool  PlayHitFeedBack;           // 0x18
    public bool  PlayHitSound;              // 0x19
    public List<BuffEffectData> HitEffects; // 0x20
}

public abstract class Unit : MonoBehaviour, bgg {
    [SerializeField] protected bool b_isHero;      // ← 阵营判定，最可靠
    [SerializeField] protected ObscuredBool b_isLive;
    public pp UnitHealthController;                // ← 属性名没被混淆，找 pp 的第二条路
    public virtual void eha(DamageInfo a, bool b);         // IDamageable.TakeDamage
                                                           //   ⚠ 只是转发器，机器码与 Hero/Monster 共用，不可 hook
    public virtual void gun(DamageInfo a, bool b);         // 真正的 TakeDamage ← 【分类入口】
    public virtual bool gvg(Unit a);                       // 击杀相关，内部会走一次 pp.hbs（处决回复）
    public virtual DamageableType egs();
    public virtual string gti();                           // 疑似 Name
    public virtual string gtj();                           // 疑似 DisplayName / Id
}
```

## 4. 枚举（未混淆，可直接用）

```csharp
// TaskbarHero.Data
enum EDamageAttribute { Physical=0, Fire=1, Cold=2, Lightning=3, Chaos=4, AllElement=5, None=6 }
enum EDamageType      { None=0, Melee=1, Projectile=2, AOE=4, Summon=8, DOT=16, Trap=32 }  // [Flags] 语义

// TaskbarHero.Combat
enum DamageableType   { Hero=1, Monster=2, Structure=6 }
```

## 5. 代码里怎么引用这些类型

BepInEx 的 Il2CppInterop **保留原始程序集名和命名空间**，所以：

```csharp
using GUnit   = TaskbarHero.Unit;    // 有命名空间的照写
using GHero   = TaskbarHero.Hero;
using GUnitHealth = global::pp;      // 无命名空间的留在全局，起别名要加 global::
```

引用的程序集是 `BepInEx\interop\Assembly-CSharp.dll`。

> 对比：MelonLoader 会给所有东西加 `Il2Cpp` 前缀——`Il2CppTaskbarHero.Unit`、`Il2Cpp.pp`、
> `Il2CppAssembly-CSharp.dll`。以后要是换回 MelonLoader，[Patches.cs](../src/TbhCombatTracker/Patches.cs)
> 顶部那几行 using 要全部加前缀。

## 6. 运行时确认结果

已确认：

- ✅ `DamageInfo` 被 Il2CppInterop 生成为 **class**（继承 `Il2CppSystem.ValueType`），字段变成属性
- ✅ 形参名保留（`gvz(float a, Unit b)`、`gun(DamageInfo a, bool b)`），Harmony 按名注入可用
- ✅ 伤害入口是 ChangeHp 不是血条初始化（见第 2 节）
- ✅ 单位 GameObject 名形如 `Hero_301(Clone)` / `Monster_30043(Clone)`，去掉 `(Clone)` 即可当显示名

仍待确认：

- `Unit.gti()` / `gtj()` 的实际返回内容（能否拿到职业名而不只是 `Hero_301`）——开 `ProbeMode` 看
- 持续伤害（DOT）/ 陷阱是否绕过 `gun` 直接调 `gvz`（若绕过，数值仍准，但分类落到"未分类"）
- **1.2.0 的 hook 点只做了静态验证**（签名 + 调用点 + 机器码唯一性），
  还没有像 1.01.05 那样跑一轮实测。恢复来源分类尤其值得用 `HealingDebug` 复核一次。

## 7. IL2CPP 方法体去重（打补丁前必读）

IL2CPP 编译时会把**方法体完全相同**的函数合并成同一段机器码。本游戏里的实例：

| 地址 | 全局共用它的方法数 | 本项目相关的 |
|---|---|---|
| `0x6BAA90` | **1884** | `Monster.gua` / `Monster.guc`（空方法桩，全二进制通用）|
| `0xA50B40` | 157 | `Hero.egs` |
| `0xA78910` | 17 | `pp.gfs` / `pp.eag` / `pp.ont` |
| `0xCD4100` | 5 | `pp.kky` / `pp.hbq` / `pp.lsb` / `pp.ghu` / `pp.oaj` |
| `0xCD2D30` | 5 | `pl.gwa` / `pl.gvx` / `pl.hth` / `pl.iek` / `pl.gsk` |
| `0xCD48A0` | 2 | `pp.nhm` / `pp.hbr`（都是 `(Unit, float, bool, bool)`）|
| `0xCABFD0` | 2 | `Hero.eha` / `Monster.eha` ← **转发器，不可 hook** |

（1.01.05 的对应例子是 `0x6B1620` 被 1872 个方法共用、`0xC94760` 被 `Hero.een`/`Monster.een`
共用——数字变了，规律没变。）

两个后果：

1. **看起来的"多个候选方法"可能只是一个函数。** `pp` 表面上有 43 个方法，实际只有 34 段不同的机器码。
2. **对同一地址挂两次 Harmony detour 会无限递归栈溢出**，表现为游戏启动闪退。
   批量打补丁前必须按原生函数指针去重——见 `Il2CppUtil.NativePointer`。
   `Patches.TryPatch` 和 `Diagnostics.Apply` 都做了这个守卫。

查某个类里哪些方法共用地址：`python tools/safe-hooks.py --types pp pl pn Monster Hero --show-unsafe`，
或者直接在 dump.cs 里按 `// RVA:` 分组。

## 8. 英雄职业

两个相关枚举（都没被混淆）：

```csharp
// TaskbarHero.Data
enum EHeroType       { Knight=0, Archer=1, Wizard=2, Priest=3, Hunter=4, Barbarian=5 }
enum EEquipClassType { All=0, Knight=1, Ranger=2, Sorcerer=3, Priest=4, Hunter=5, Slayer=6 }
```

权威数据结构 `TaskbarHero.Data.HeroInfoData`，**字段全部保留原名**：

```csharp
public int    HeroKey;      // 101 / 201 / 301 / 401 ...
public string HeroNameKey;  // 本地化键
public EEquipClassType ClassType;
public string IconPath;     // 职业图标（做图标化 UI 时可用）
public bool   IsMeleeHero;
public int    AttackDamage, CriticalChance, CriticalDamage, MaxHp, Armor, ...;
```

运行时取法（`Patches.ReadClassType`）：

```
Hero.cache          → wg（HeroCache）
     .bghy          → HeroInfoData
     .ClassType     → EEquipClassType
```

GameObject 名 `Hero_301(Clone)` 的**首位数字恰好等于 EEquipClassType**：

| GameObject | HeroKey | EEquipClassType | 中文 |
|---|---|---|---|
| `Hero_101` | 101 | 1 Knight | 骑士 |
| `Hero_201` | 201 | 2 Ranger | 游侠 |
| `Hero_301` | 301 | 3 Sorcerer | 法师 |
| `Hero_401` | 401 | 4 Priest | 牧师 |
| `Hero_501` | 501 | 5 Hunter | 猎人 |
| `Hero_601` | 601 | 6 Slayer | **杀手** |

> 六个中文名都取自游戏内的角色卡，不是枚举名直译——`Slayer` 在游戏里叫**杀手**
> （描述写的是"用斧头砍断一切的狂战士"），最早按枚举直译成"狂战"是错的。

> ⚠️ **代码里不依赖这个规律**——首位数字只是恰好对上，`Patches.ReadClassType` 走的是
> `HeroInfoData.ClassType`。每识别一个新英雄会打一条日志
> （`识别英雄 Hero_301: HeroKey=301 ClassType=Sorcerer(3) -> 法师`），
> 映射对不对看日志即可，不用猜。

## 9. 关卡分段与窗口交互

### 关卡状态机

`TaskbarHero.StageManager`（单例，继承 `nu<StageManager>`）：

```csharp
[SerializeField] private EStageState stageState;   // 字段名未混淆，interop 里直接是属性
enum EStageState { NONE=0, MONSTERSPAWN=1, BATTLE=2, REORGANIZATION=3 }

public Action OnFirstClearStage;   // 这几个 Action 的名字也没混淆
public Action<int> OnGetBox;
public Action<Hero> OnDeadInfoChange;
[SerializeField] private bool b_StageStart;
```

> ⚠️ **`EStageState` 是波次状态，不是关卡边界。** 一关之内会反复
> `MONSTERSPAWN → BATTLE → MONSTERSPAWN → …`，拿它切段的结果是每波都重置。
> 佐证：`UI_Stage` 里有 `StageWaveIconSliderController`，波次确实是关卡的子单位。

真正的关卡级信号有两个，`StageWatcher` 任一触发即切段（带 1.5s 去抖）：

| 信号 | 来源 | 备注 |
|---|---|---|
| 关卡名变化 | `UI_Stage.text_StageName`（TMP） | 顺带当面板标题 |
| `false → true` | `StageManager.b_StageStart` | `[SerializeField] bool` |

`stageState` 仍然每次跃迁记一条日志，只是不再拿来切段。
**不订阅 Action**——Il2Cpp 委托跨边界麻烦，轮询一个属性的代价可以忽略，而且不改游戏任何东西。

> 读 `stageState`（`[SerializeField]`）而不是混淆过的 getter `bswc`/`iih`：
> 序列化字段名混淆器动不了，游戏更新后更可能还在。同理 `pf.bdeg`、`Hero.cache`。

### 窗口点击穿透

游戏是任务栏挂件，窗口默认带 `WS_EX_TRANSPARENT`。`WindowManager.Update()` 每帧：

```
EventSystem.current.RaycastAll(pointerEventData, list)   ← 通用射线，遍历所有 BaseRaycaster
   → on.glu(bool)                                        ← 开关 WS_EX_TRANSPARENT
```

因为是**通用射线**，我们自建的 Canvas + GraphicRaycaster 也会被命中——所以只要挂一个
透明的 `Image (raycastTarget=true)` 跟着面板走，游戏就会自己解除穿透
（`RaycastAnchor.cs`）。不需要碰 Win32。

`ClickThrough.cs` 是兜底：射线靶创建失败时才改窗口样式，并且参数极性靠读回
`GWL_EXSTYLE` 自动标定，不硬编码。

## 10. 本地化文本

游戏用 Unity Localization 包，封装在全局类 `nz` 上（一组 string 扩展方法）：

```csharp
nz.giz(key)          // ← 走本地化表，返回玩家当前语言
nz.gix(key)          // ← 返回英文源文本，只能当兜底
nz.giy(key, table)   nz.gja(key, table)   // 指定表名
nz.gjb(key, args)                          // 带格式化参数
```

> 哪个对哪张表，静态分析看不出来。1.01.05 是靠一条日志定的：
> 中文界面下 `gft("HeroName_401")` 返回 `"Priest"`，`gfv` 才返回 `"牧师"`。
> 1.2.0（`nz`）里五个方法在 dump 中的先后顺序和当年的
> `gft`/`gfu`/`gfv`/`gfw`/`gfx` 一一对应，所以 `giz` 应当就是原来的 `gfv`。
> **这只是顺序推断**——`Localize.Lookup` 两个都查、谁先返回有效译文用谁，
> 顺序推断错了也不会显示成英文。

两张表：`StringTable`（通用）和 `ItemTable`（道具）。语言 16 种。
当前语言从 `LocalizationSettings.SelectedLocale`（**静态**属性；同名的
`GetSelectedLocale()` 是实例方法）→ `Locale.Identifier` → `LocaleIdentifier.Code`。
`LocaleIdentifier` 是结构体，不能对 `.Identifier` 用 `?.`。

### 键从哪来

| 东西 | 键 | 出处 |
|---|---|---|
| 英雄名 | `HeroName_401` | `HeroInfoData.HeroNameKey`，字段名没被混淆 |
| 技能名 | — | `ActiveSkill.skillCache`(`wl`) → `.bgjn` → `SkillInfoData.SkillNameKey` |
| 元素属性 | `Fire` / `Cold` / `Physical` … | **裸枚举名**就是键 |
| 伤害类型 | 没有 | 见下 |

> ⚠️ **界面文本的键大多不在 `global-metadata.dat` 里。** 它们由预制体上的
> `LocalizeStringEvent` 组件持有，存在 Addressables 资产中。翻 `dump.cs` 或扫描
> metadata 字面量都找不到，只能解包 bundle：`tools/dump-localization.py`。

### 伤害类型：游戏没有独立词条，从整句里反推

`EDamageType` 的 Melee / Projectile / AOE / Summon 在**任何**语言的字符串表里都没有
单独的词条（裸枚举名、`EDamageType` 前缀、下划线变体都试过）——界面上不显示这批枚举。

但属性面板上有整句：

```
StatName_IncreaseMeleeDamage        zh 增加近战伤害      en Increase Melee Damage
StatName_IncreaseProjectileDamage   zh 增加投射物伤害    en Increase Projectile Damage
StatName_IncreaseAreaOfEffectDamage zh 增加范围伤害      en Increase Area Of Effect Damage
StatName_IncreaseSummonDamage       zh 增加召唤物伤害    en Increase Summon Damage
```

把这四句**共有的前缀和后缀**都剥掉，剩下的正好是类型词。这个做法与语言无关——
不需要知道"增加"在哪门语言里怎么写、摆在词的前面还是后面。
实现在 `Localize.LiftDistinctParts`，已把全部 16 种语言离线跑过，每种都能抠出
四个互不相同的词：

```
zh-Hans  近战 / 投射物 / 范围 / 召唤物          zh-Hant  近戰 / 投射物 / 範圍 / 召喚物
en-US    Melee / Projectile / Area Of Effect / Summon
ja-JP    近接 / 投射物 / 範囲 / 召喚            ko-KR    근접 공격 / 투사체 공격 / 범위 공격 / 소환물
ru-RU    ближнего боя / снарядов / по области / призванных
de-DE    Nahkampf / Geschoss / Flächen / Beschwörungs
```

> 用 `StatName_*` 而不是 `Stat_Increase*Damage_ADDITIVE`：后者在 de / fr / th / tr / vi
> 里带缩写和占位符残留（`Nahkampfscha.`、`%{0} Artan Yakın Dövüş`），`StatName_*` 干净。

**DOT 和 Trap 在任何语言里都没有出处**，和 `None` 一起继续用 `BuiltinText` 的内置双语表。
内置文本按 `LocalizationSettings.SelectedLocale` 选中/英，不硬编码中文——Mod 是公开发布的。

## 11. 1.01.05 → 1.2.0 重定位记录

游戏 2026-09-08 更新到 1.2.0，**本文档记录的混淆名全部失效**——`sigcheck` 12 项没找到。
下面是完整的对照表和当时用的判据，下次更新照着走即可。

### 类型

| 1.01.05 | 1.2.0 | 判据 |
|---|---|---|
| `pj` UnitHealth | `pp` | 编译器生成的状态机 `<HealthRegenAsync>d__20` 名字没变；字段布局 `HpBar`/`OnHpChange`/`Unit`/`float`/`float` 完全一致 |
| `pf` HeroHealth | `pl` | `: pp` + `private Hero` 字段 + 覆写 ChangeHp |
| `ph` MonsterHealth | `pn` | `: pp` + `private Monster` 字段 + 不覆写 ChangeHp |
| `bfc` IDamageable | `bgg` | `Unit : MonoBehaviour, bgg` |
| `vo` HeroCache | `wg` | `Hero.cache` 的类型（`cache` 没被混淆）|
| `vt` SkillCache | `wl` | `ActiveSkill.skillCache` 的类型（`skillCache` 没被混淆）|
| `on` 窗口控制 | `ot` | 唯一调 `SetWindowLong`/`GetWindowLong` 的类 |
| `nt` 本地化 | `nz` | 五个 `[Extension] static string`，签名组合唯一 |
| `bfj` 治疗场 | `bgn` | `PriestSanctuary` 调它的 `nxp(Unit, float, float, Vector3, UniqueModInfoData)` |

### 方法与字段

| 1.01.05 | 1.2.0 | 判据 |
|---|---|---|
| `pj.gsi` ChangeHp | `pp.gvz` | `(float, Unit)` 在 `pp` 上唯一 |
| `pj.gsd` 血条初始化 | `pp.gvu` | `(Unit, Vector3, float)` 唯一 |
| `pj.gxq` 恢复总入口 | `pp.hbs` | 五个 `(float,bool,bool)` 候选里唯一有调用点的，且其中 2 处来自 `Unit` 的 TakeDamage |
| `Monster.grd` TakeDamage | `Monster.gun` | `(DamageInfo,bool)` 且 Hero/Monster 的 RVA **互异** |
| `Hero/Monster.een` 转发器 | `.eha` | 同签名但 Hero/Monster **共用同一 RVA** —— 反过来正好用来认转发器 |
| `Unit.grt` 处决回复 | `Unit.gvg` | 唯一一个既接受 `Unit` 参数、又调用恢复总入口的方法 |
| `on.glu` 点击穿透 | `ot.gpb` | `ot` 六个 `(bool)` 方法里唯一被 `WindowManager.Update()` 调用的 |
| `PriestHeal.mti` | `.niu` | `HeroActiveSkill` 的执行槽；PriestHeal / Sanctuary 各自覆写且 RVA 互异 |
| `Unit.gpz`/`gqa` | `.gti`/`.gtj` | `abstract string`，Hero/Monster 都覆写 |
| `pf.bdeg` → Hero | `pl.bdvr` | `pl` 上唯一的 `Hero` 字段 |
| `PriestHeal.bhfz` | `.bidv` | `PriestHeal` 上唯一的 `Hero` 字段。⚠ 它是治疗**目标**不是施法者，见第 2 节末尾 |
| `ActiveSkill.bhnf` 拥有者 | `.bilm` | `ActiveSkill` 上唯一的 `Unit` 字段 |
| `vt.bfmw` | `wl.bgjl` | `wl` 上唯一的 `SkillInfoData` 字段 |
| `vo.bflj` | `wg.bghw` | `wg` 上唯一的 `HeroInfoData` 字段 |
| `nt.gfv`/`gft` | `nz.giz`/`gix` | 按 dump 里的先后顺序对应（见第 10 节的告诫）|
| `ActiveSkill.AttackDamage` | 不变 | 名字本来就没被混淆 |
| `StageManager.stageState` / `b_StageStart` / `UI_Stage.text_StageName` | 不变 | `[SerializeField]`，混淆器动不了 |

### 方法论

按可靠性从高到低，**优先用排在前面的**：

1. **没被混淆的名字**——`[SerializeField]` 字段、`public` struct 字段、枚举、编译器生成的
   状态机名、`Unit.UnitHealthController` / `Hero.cache` / `ActiveSkill.skillCache` 这类属性。
   一次都没变过。
2. **签名在类内唯一**——`(float, Unit)` 认 ChangeHp、`(Unit, Vector3, float)` 认血条初始化。
   `python tools/extract-types.py build/dump/dump.cs pp --methods`
3. **RVA 关系**——同签名的一组方法里，各子类 RVA 互异的是真实现，共用同一 RVA 的是转发器。
   `python tools/safe-hooks.py --types ... --show-unsafe`
4. **调用点**——死代码没有调用点；"谁调用了它"和"它调用了谁"都能定身份。
   `python tools/xref-scan.py --targets pp.hbs`
5. **slot 号**——❌ **别用**。`gsd` 原本 slot 6、`gsi` slot 9，1.2.0 里对应的 `gvz` 是 slot 8、
   `gvu` 是 slot 9，顺序整个变了。

改完这两处，再跑 `sigcheck` 应当"全部命中"：

- `tools/sigcheck/Program.cs` 顶部的 `Targets`
- `src/TbhCombatTracker/Patches.cs` 顶部的 using 别名和方法名常量
  （另外 `Localize.cs` / `SkillTracker.cs` / `Diagnostics.cs` 里也各有几个）

> ⚠️ 静态验证到此为止只能保证"挂得上、不崩"。**数值和分类是否正确仍要进游戏实测**——
> 尤其是恢复来源分类（`HealingDebug`）和伤害归因（面板对账）。

---

### 附：技能的「施法者」取哪个字段

治疗归因踩过一次坑，记在这里。技能类上和单位有关的字段有三层：

| 字段 | 偏移 | 声明于 | 是什么 |
|---|---|---|---|
| `bilo` | 0x38 | `ActiveSkill` | **施法者**（Unit）。所有技能都有，归因就用它 |
| `bicc` | 0x78 | `HeroActiveSkill` | **施法者**（Hero）。英雄技能专有，和上面同一个人 |
| `bidx` | 0x80 | `PriestHeal` | 这次治疗的**目标**，❌ 不是施法者 |
| `bidz` | 0x80 | `PriestSanctuary` | 展开的治疗场对象（`bgn`）|

判据很直接：施法者在基类里已经存了两份，子类没理由再存第三份；而 `PriestHeal.bidx` 和
`PriestSanctuary.bidz` 在**同一个偏移 0x80** 上——那个位置放的是「这个技能作用在什么东西上」，
单体治疗放目标，领域技能放领域对象。

拿这个字段当施法者的后果：牧师给友军放「治愈」，治疗量记到**被治疗的友军**头上，
面板上看起来像友军自己治疗了自己，牧师反而没数据。
`PriestSanctuary` 那条路径一直取的是施法者字段，所以只有「治愈」错、「圣域」是对的——
这个不对称本身就是定位线索。

> 这个错误从 1.01.05 就在（当时字段叫 `bhfz`），1.2.0 改名时被原样搬了过来。
> 开 `HealingDebug` 会打 `施法者=X 目标=Y`，两个名字应当不同；相同就是又取错了。

## 12. 1.2.0 → 1.2.2 重定位记录

2026-09-09 的修复更新。这次范围小得多：**方法名一个没动，字段名整体向后平移了两位。**

| 1.2.0 | 1.2.2 | 偏移 | 是什么 |
|---|---|---|---|
| `pl.bdvr` | `pl.bdvt` | 0x58 | HeroHealth → Hero |
| `wg.bghw` | `wg.bghy` | 0x30 | HeroCache → HeroInfoData |
| `wl.bgjl` | `wl.bgjn` | 0x10 | SkillCache → SkillInfoData |
| `ActiveSkill.bilm` | `.bilo` | 0x38 | 施法者（Unit）|
| `HeroActiveSkill.bica` | `.bicc` | 0x78 | 施法者（Hero）|
| `PriestHeal.bidv` | `.bidx` | 0x80 | 治疗目标 |
| `PriestSanctuary.bidx` | `.bidz` | 0x80 | 治疗场对象 |
| `PriestHeal/Sanctuary.niu` | `.niw` | — | 技能执行入口（唯一变了的方法名）|

**偏移量一个没变**，这是确认「同一个字段只是改了名」最省事的判据。

`gvz` / `gun` / `gvg` / `hbs` / `gpb` / `eha` 全部原样保留，调用点分布也一致
（`hbs` 仍是 2 处来自 `Unit.gun`、1 处来自 `Unit.gvg`；`gpb` 仍只被 `WindowManager.Update()` 调用），
`Hero.eha` / `Monster.eha` 仍共用同一段机器码。

> ⚠️ **「方法名还在」不等于「含义没变」。** 这次 `ActiveSkill.bilm` 就从 `Unit` 变成了 `int`
> ——名字被复用给了别的字段。所以每次更新都要重新核对**类型和签名**，不能只看名字在不在。
> 挂载日志现在会连参数类型一起打（`已挂载 pp.gvz(Single, Unit)`），错位一眼可见。

### 这次更新暴露的真正问题

1.2.2 刚更新时，装着 v0.2.1 的玩家**游戏里所有生命恢复都失效了**——现象是「牧师的治愈不回血」。

原因链：

```
pl.bdvr 改名 → HealFunnel_Pre 里一行调试日志读它 → MissingMethodException
            → 那个方法没有 try/catch → 异常漏进 Harmony 的 Prefix
            → Prefix 抛异常 = 原方法不执行 → 挂在恢复总入口 pp.hbs 上 → 所有回血失效
```

一次会话里刷了 234 次。**一个只读的统计 Mod 把游戏功能玩坏了**，这比统计不准严重得多。

教训不是「别写错字段名」——游戏每次更新都会改名，这类异常迟早还会出现。
正确的目标是**出了异常也只影响统计，绝不影响游戏**：

> **补丁方法是我们和游戏代码之间的边界，边界上一个异常都不许漏过去。**

现在 `tools/check-guards.py` 会检查每个补丁方法的第一条语句是不是 `try`，
并挂在构建流程前面（见 `TbhCombatTracker.csproj` 的 `CheckPatchGuards`），不合格直接编译失败。
要求「第一条就是 try」而不是「方法体里有 catch」，是因为后者会放过只包了一半的写法——
而这次出事的恰恰就是没被包住的那半边。
