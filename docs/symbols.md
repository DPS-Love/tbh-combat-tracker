# Task Bar Hero — 逆向符号表

> 来源：Il2CppDumper v6.7.46 对 `GameAssembly.dll` + `global-metadata.dat` 的 dump
> 游戏版本：**1.01.05**（`Version.txt`）／Unity **6000.0.72f1** ／ IL2CPP ／ metadata v31

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

**结论：游戏每次更新，混淆名很可能全部变化。** 本文件就是为了让重新对齐时有据可查——
更新后重跑 `tools/dump-symbols.ps1`，再按下面的"识别特征"重新定位即可。

## 1. 类型映射

| 混淆名 | 真实身份 | 命名空间 | 识别特征 |
|---|---|---|---|
| `bfc` | `IDamageable` | *(全局)* | 唯一被 `Unit` 实现的接口，有 `DamageableType` 属性 |
| `pj` | `UnitHealth` | *(全局)* | `MonoBehaviour`，含 `SpriteSlider HpBar` + `Action<float> OnHpChange`；<br>还有编译器生成的状态机 `pj.<HealthRegenAsync>d__20`——**这个名字没被混淆，是最硬的指纹** |
| `pf` | `HeroHealth` | *(全局)* | `: pj`，私有字段类型为 `Hero` |
| `ph` | `MonsterHealth` | *(全局)* | `: pj`，私有字段类型为 `Monster` |
| `vo` | `HeroCache`（英雄运行时数据） | *(全局)* | 含 `HeroInfoData` 字段 + `Hero` 反向引用 |
| `Unit` | — 未混淆 | `TaskbarHero` | `abstract class Unit : MonoBehaviour, bfc` |
| `Hero` | — 未混淆 | `TaskbarHero` | `class Hero : Unit`，`public vo cache` |
| `Monster` | — 未混淆 | `TaskbarHero` | `class Monster : Unit`，`public EMonsterType MonsterType` |
| `DamageInfo` | — 未混淆 | `TaskbarHero` | `struct`，字段全部保留原名 |

## 2. 伤害链路（已用运行时诊断实测确认）

```
Monster.grd(DamageInfo, bool)                 [Monster slot 46] = TakeDamage
   │  内部完成暴击判定 / 抗性减免 / 吸收护盾结算
   └─> UnitHealthController.gsi(float delta, Unit source)   [pj slot 9] = ChangeHp
          delta < 0 → 伤害      delta > 0 → 治疗
```

实测日志（诊断模式）：

```
[diag] Monster.grd(DamageInfo, false)
[diag] pj.gsi(-735.586,  Unit("Hero_301(Clone)"))
[diag] Monster.grd(DamageInfo, false)
[diag] pj.gsi(-1284.333, Unit("Hero_301(Clone)"))
[diag] pj.gxq(1.5, false, false) → pf.gsi(1.5, null)      ← 治疗走同一入口，正数
```

- **`grd` 提供分类信息**（暴击、伤害类型、伤害属性），但只有 `OriginDamage`（减免前）
- **`gsi` 提供最终数值**（负数）和来源（`Unit source`）
- 二者配对：`grd` 前后设置 / 清除"当前伤害上下文"，`gsi` 里读取

覆写关系决定了要挂哪个类：

| 承伤方 | 走哪个实现 | 要挂的 hook |
|---|---|---|
| 怪物 | `ph` **没有**覆写 `gsi` → 走基类 `pj.gsi` | `pj.gsi`，且 `__instance` 是 `ph` |
| 英雄 | `pf` **覆写了** `gsi` → 走 `pf.gsi` | `pf.gsi` |

> ⚠️ **`gsd(Unit, Vector3, float)` 不是伤害入口。** 签名看着像
> `ApplyDamage(attacker, hitPos, damage)`，实际是血条初始化：单位生成时调一次，
> 第一个参数是**单位自己**，float 恒为 `-0.875`。这里踩过坑，别再押它。
>
> 同理 `een` 也不是——`Hero.een` 和 `Monster.een` 编译结果完全相同（共用 `0xC94760`），
> 说明它只是个转发器，`grd` 才是各自的真实现。而且共用机器码的方法不能安全 hook，见第 7 节。

## 3. 关键签名

```csharp
// 全局命名空间 → Il2CppInterop 生成后为 Il2Cpp.*
public class pj : MonoBehaviour {
    public SpriteSlider HpBar;              // 0x20
    public Action<float> OnHpChange;        // 0x28
    protected Unit  bdhd;                   // 0x30  拥有者 Unit
    protected float bdhe;                   // 0x38  当前 HP
    protected float bdhf;                   // 0x3C  最大 HP
    public virtual void gsd(Unit a, Vector3 b, float c);   // slot 6 = 血条初始化，**不是伤害**
    public virtual void gsh();                             // slot 7
    public virtual void gse();                             // slot 8
    public virtual void gsi(float a, Unit b);              // slot 9 = ChangeHp ← 【伤害入口】
}
// ph 只覆写 gsd/gsh，**没有**覆写 gsi → 怪物承伤走基类 pj.gsi
public class ph : pj { private Monster bdew; public override void gsd(Unit a, Vector3 b, float c); }
// pf 覆写了 gsi → 英雄承伤必须单独挂 pf.gsi
public class pf : pj { private Hero    bdeg; public override void gsi(float a, Unit b); }

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

public abstract class Unit : MonoBehaviour, bfc {
    [SerializeField] protected bool b_isHero;      // 0x100  ← 阵营判定，最可靠
    [SerializeField] protected ObscuredBool b_isLive;
    public pj UnitHealthController;                // 0xB0
    public abstract void een(DamageInfo a, bool b);        // slot 29 = IDamageable.TakeDamage
                                                           //   ⚠ 只是转发器，且机器码与 Hero.een 共用，不可 hook
    public abstract DamageableType eef();                  // slot 28
    public abstract string gpz();                          // slot 16  疑似 Name
    public abstract string gqa();                          // slot 17  疑似 DisplayName / Id
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
using GUnitHealth = global::pj;      // 无命名空间的留在全局，起别名要加 global::
```

引用的程序集是 `BepInEx\interop\Assembly-CSharp.dll`。

> 对比：MelonLoader 会给所有东西加 `Il2Cpp` 前缀——`Il2CppTaskbarHero.Unit`、`Il2Cpp.pj`、
> `Il2CppAssembly-CSharp.dll`。以后要是换回 MelonLoader，[Patches.cs](../src/TbhCombatTracker/Patches.cs)
> 顶部那几行 using 要全部加前缀。

## 6. 运行时确认结果

已确认：

- ✅ `DamageInfo` 被 Il2CppInterop 生成为 **class**（继承 `Il2CppSystem.ValueType`），字段变成属性
- ✅ 形参名保留（`gsi(float a, Unit b)`、`grd(DamageInfo a, bool b)`），Harmony 按名注入可用
- ✅ 伤害入口是 `gsi` 不是 `gsd`（见第 2 节）
- ✅ 单位 GameObject 名形如 `Hero_301(Clone)` / `Monster_30043(Clone)`，去掉 `(Clone)` 即可当显示名

仍待确认：

- `Unit.gpz()` / `gqa()` 的实际返回内容（能否拿到职业名而不只是 `Hero_301`）——开 `ProbeMode` 看
- 持续伤害（DOT）/ 陷阱是否绕过 `grd` 直接调 `gsi`（若绕过，数值仍准，但分类落到"未分类"）

## 7. IL2CPP 方法体去重（打补丁前必读）

IL2CPP 编译时会把**方法体完全相同**的函数合并成同一段机器码。本游戏里的实例：

| 地址 | 共用它的方法 |
|---|---|
| `0x6B1620` | `Monster.gqq` / `Monster.gqs` |
| `0xCAB440` | `pj.cjr` / `pj.gxp` / `pj.ode` / `pj.hm`（都是 `(Unit, float, bool, bool)`）|
| `0xCAB320` | `pj.cdw` / `pj.lwx` / `pj.ftd` / `pj.gxm` / `pj.brl` |
| `0xCABEC0` | `pj.nyo` / `pj.gxo` / `pj.nxa` |
| `0xCAB370` | `pj.bvq` / `pj.gxt` |
| `0x829540` | `pj.ehp` / `pj.OnDisable` |
| `0xCAA120` | `pf.gsj` / `pf.dvq` / `pf.ioa` / `pf.gsg` |
| `0xC94760` | `Hero.een` / `Monster.een` |

两个后果：

1. **看起来的"多个候选方法"可能只是一个函数。** `pj` 表面上有 57 个方法，实际只有 31 段不同的机器码。
2. **对同一地址挂两次 Harmony detour 会无限递归栈溢出**，表现为游戏启动闪退。
   批量打补丁前必须按原生函数指针去重——见 `Il2CppUtil.NativePointer`。
   `Patches.TryPatch` 和 `Diagnostics.Apply` 都做了这个守卫。

查某个类里哪些方法共用地址，直接在 dump.cs 里按 `// RVA:` 分组即可。

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
Hero.cache          → vo（HeroCache）
     .bflj          → HeroInfoData
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
