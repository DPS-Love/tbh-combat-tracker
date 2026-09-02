# TBH Combat Tracker

给 **Task Bar Hero**（Unity 6 / IL2CPP）做的伤害统计 Mod：按英雄拆分输出、DPS、暴击率、
元素与伤害类型占比，IMGUI 悬浮面板实时显示，可导出 CSV。

只读统计，不修改任何游戏数值。

---

> **只想装来用？** 看 [docs/INSTALL.md](docs/INSTALL.md)，本 README 是给开发者的。
> 发布包从 [Releases](../../releases) 下载。

已在**游戏 1.01.05** + **BepInEx 6.0.0-be.785** 上完整验证：伤害/承伤统计、职业识别、
关卡自动分段、面板交互。反作弊风险评估见 [docs/anticheat.md](docs/anticheat.md)。

---

## 开发环境

### 1. .NET SDK 8

装完 `dotnet --list-sdks` 有输出即可。不想动系统 PATH 的话可以用
[dotnet-install 脚本](https://dot.net/v1/dotnet-install.ps1) 装到用户目录。

### 2. BepInEx 6 IL2CPP

用 bleeding-edge 构建 **6.0.0-be.785**（2026-06-28）验证过。安装 / 换构建 / 卸载：

```powershell
pwsh tools/install-bepinex.ps1                    # 默认 be.785，本地没有就自动下载
pwsh tools/install-bepinex.ps1 -Build 790         # 换个构建号
pwsh tools/install-bepinex.ps1 -Uninstall         # 卸载（保留 plugins\ 和 config\）
```

> **为什么不用带 tag 的正式版：** BepInEx 6 至今没发过正式版，GitHub Release 上最新的
> IL2CPP 包是 `6.0.0-pre.2`（2024-08），比 Unity 6 正式发布还早，啃不动本游戏的 metadata v31。
> IL2CPP 的活跃开发都在 `builds.bepinex.dev` 的 BE 构建里。

> **注意：** BepInEx IL2CPP 走 `winhttp.dll` 代理注入。Steam"验证游戏文件完整性"
> 会把它判为多余文件删掉，删了重跑脚本即可。

> 公司网络下载失败的话挂 Clash 代理（脚本默认已带 `-Proxy http://127.0.0.1:7897`）。
> 失败就手动重试一次，别循环重试。

### 3. 启动一次游戏

BepInEx 首次运行会用 Cpp2IL 反编译 `GameAssembly.dll`，再用 Il2CppInterop
生成可以被 C# 引用的代理程序集，耗时 1–3 分钟。产物在：

```
TaskbarHero\BepInEx\interop\Assembly-CSharp.dll
```

这个文件出现了，才能编译本项目。日志看 `TaskbarHero\BepInEx\LogOutput.log`。

---

## 构建

```powershell
dotnet build src/TbhCombatTracker/TbhCombatTracker.csproj -c Release
```

构建成功会自动把 `TbhCombatTracker.dll` 部署到 `<游戏目录>\BepInEx\plugins\`。

游戏路径写死在 [Directory.Build.props](Directory.Build.props) 的 `GameDir`，
不在 D 盘就改那里，或者命令行覆盖：

```powershell
dotnet build src/TbhCombatTracker/TbhCombatTracker.csproj -p:GameDir="E:\Steam\steamapps\common\TaskbarHero"
```

---

## 使用

| 按键 | 作用 |
|---|---|
| `F9` | 显示 / 隐藏面板 |
| `F10` | 重置当前战斗统计 |
| `F11` | 导出 CSV 到 `<游戏目录>\BepInEx\TbhCombatTracker\` |

面板标题栏可拖动。默认 8 秒没有任何伤害就自动开启新一场统计。

标题栏的按钮在**输出 / 承伤**两个视图间切换。整个窗口都能拖动。

> 治疗统计做过但撤掉了：游戏的回血和伤害共用 `UnitHealth.ChangeHp`，但回血这条路径的
> `Unit source` 恒为 null（实测 `pf.gsi(1.5, null)`），归因不到治疗者，全部只能落到
> "自动回复"一档，没有统计价值。要做的话得从技能侧另找 hook 点。

面板形制参考 FFXIV ACT 的 [Horizoverlay](https://github.com/bsides/horizoverlay)：每个来源一张
140px 窄卡片横向并排，主体是 `skew(-30deg)` 平行四边形，按**职业立绘主色**配色，底部一条 2px 占比条，
再带一行暴击率和单次最大伤害。职业不是猜的——运行时从 `HeroInfoData.ClassType` 读，
见 [docs/symbols.md](docs/symbols.md) 第 8 节。

配置在 `<游戏目录>\BepInEx\config\dpslove.tbh.combattracker.cfg`：

| 配置项 | 默认 | 说明 |
|---|---|---|
| `SegmentByStage` | true | 按关卡自动分段（关卡名变化 / `b_StageStart`）。关掉才用下面的空闲阈值 |
| `IdleResetSeconds` | 8 | 空闲多少秒切一段，仅在 `SegmentByStage=false` 时生效，0 = 从不自动切 |
| `TrackIncoming` | true | 是否同时统计英雄承受的伤害 |
| `ProbeMode` | false | 调试：把攻击者的各种名字字段打到日志，用来确定英雄显示名怎么取 |
| `UiScale` | 1.0 | 面板缩放 |
| `SkewDegrees` | -30 | 卡片色块斜切角度（Horizoverlay 用 -30），设 0 就是普通矩形 |
| `FixClickThrough` | true | 光标移到面板上时解除窗口点击穿透，让按钮可点、窗口可拖 |
| `DiagnosticMode` | false | 一次性诊断：定位某段逻辑走哪条代码路径，见下方"诊断模式" |

配置文件里还有独立的 `[Colors]` 段，六个职业的卡片颜色可以直接改十六进制值，
不用重新编译（支持 `#RGB` / `#RRGGBB` / `#RRGGBBAA`）：

| 职业 | 默认色 | 取自 |
|---|---|---|
| 骑士 Knight | `#C0392B` | 猩红披风与盾徽 |
| 游侠 Ranger | `#5FB04A` | 森林绿劲装 |
| 法师 Sorcerer | `#8E5BD0` | 紫罗兰法杖 |
| 牧师 Priest | `#F0D98C` | 圣白法袍配金饰 |
| 猎人 Hunter | `#2AA8A0` | 青碧斗篷 |
| 杀手 Slayer | `#E07B39` | 赭褐皮甲双斧 |
| 未知/怪物/环境 | `#9E9E9E` | — |

> 没有沿用 Horizoverlay 的职能三色（坦克蓝/治疗绿/输出红）：这游戏只有六个固定职业，
> 用形象色辨识度更高。

---

## 工作原理

```
Monster.grd(DamageInfo, bool)          ← Prefix/Finalizer: 记下暴击/伤害类型/元素属性
  └─ UnitHealth.gsi(float, Unit)       ← Postfix: 负数=最终伤害，第二参=攻击者
```

游戏的伤害结算分两步：`grd`（= `TakeDamage`）拿到带分类信息的 `DamageInfo`，
内部算完暴击和抗性减免后，把**最终增量**交给 `gsi`（= `ChangeHp`）改血量——
负数是伤害，正数是治疗，两者共用同一个入口。

单独 hook 任何一个都不够：`grd` 只有减免前的 `OriginDamage`，`gsi` 只有一个裸 float。
所以用 Prefix/Finalizer 把 `grd` 夹住，在中间的 `gsi` 里把两边的信息拼起来。

攻击者归因靠 `gsi` 的第二个参数 `Unit source`，不用猜。

挂哪个类由覆写关系决定：怪物的 `ph` 没覆写 `gsi`，走基类 `pj.gsi`；英雄的 `pf` 覆写了，
必须单独挂 `pf.gsi`。

> 这条链路是**运行时诊断实测**出来的，不是从签名推的。中间押错过 `gsd(Unit, Vector3, float)`
> ——那个签名看着完全像伤害，实际是血条初始化。教训见 [docs/symbols.md](docs/symbols.md) 第 2 节。

代码分工：

| 文件 | 职责 |
|---|---|
| [Patches.cs](src/TbhCombatTracker/Patches.cs) | 所有和游戏类型耦合的代码。游戏更新后基本只需要改这一个文件 |
| [DamageTracker.cs](src/TbhCombatTracker/DamageTracker.cs) | 聚合统计、战斗切分、CSV 导出 |
| [Overlay.cs](src/TbhCombatTracker/Overlay.cs) | IMGUI 面板 |
| [Plugin.cs](src/TbhCombatTracker/Plugin.cs) | BepInEx `BasePlugin` 入口 |
| [Mod.cs](src/TbhCombatTracker/Mod.cs) | 加载器门面（日志 + 配置）。换加载器只需重写这个文件和 Plugin.cs |
| [TrackerBehaviour.cs](src/TbhCombatTracker/TrackerBehaviour.cs) | 注入 IL2CPP 域的 MonoBehaviour，负责 `Update` / `OnGUI` |

---

## 签名验证

编译通过 ≠ Harmony 能绑上。形参名、方法是否在派生类上声明、值类型被生成成 class 还是 struct，
这些编译器都不检查，但 Harmony 全都依赖。所以有个专门的检查器：

```powershell
dotnet run --project tools/sigcheck/sigcheck.csproj
```

它直接读 `BepInEx\interop\Assembly-CSharp.dll`，打印我们 hook 的方法的真实签名。
全部命中返回 0，有缺失返回 2。

对着 be.785 + 游戏 1.01.05 的实测结果，三个原本的风险点都已确认：

| 风险点 | 实测结果 |
|---|---|
| 形参名是否被重命名 | ✅ 保留：`gsi(Single a, Unit b)`、`grd(DamageInfo a, Boolean b)` |
| 方法是否在预期类型上声明 | ✅ `pf.gsi` 是 `override`、`pj.gsi` 是 `virtual`，`DeclaredMethod` 都拿得到 |
| `DamageInfo` 生成形态 | ✅ class（继承 `Il2CppSystem.ValueType`），字段一律变成属性，现有代码兼容 |

还有两个**只靠静态分析发现不了**的坑，都是实跑才暴露的，见下面两节。

### 被裁剪的 Unity API（IL2CPP 特有的坑）

游戏是 IL2CPP 构建且自身不用 IMGUI，所以 Unity 的纯托管方法有一部分被裁剪掉了。
Il2CppInterop 会尝试还原 IL（本游戏：`11210 successful, 1494 failed`），还原失败的会生成一个
直接 `throw new NotSupportedException("Method unstripping failed")` 的桩——**编译期毫无提示，
一调用就抛**。如果这发生在 `OnGUI` 里，就是每帧抛异常，游戏直接卡死。

```powershell
dotnet run --project tools/sigcheck/sigcheck.csproj -- --stripped UnityEngine.IMGUIModule GUILayout GUI
```

写任何新 UI 代码前先跑一遍。本构建的实测结果：

| 类型 | 可用 / 失效 | 踩过的坑 |
|---|---|---|
| `GUILayout` | 217 / **1** | **`FlexibleSpace()` 失效** —— 曾导致启动卡死 |
| `GUI` | 239 / 8 | `BeginScrollView` / `EndScrollView` / `Slider` / `Scroller` 失效 |
| `GUILayoutUtility` | 70 / 0 | — |
| `GUIStyle` | 205 / 0 | — |
| `Mathf` | 81 / 3 | 只有 `Max`/`Min` 的数组重载失效 |
| `Texture2D` | 176 / 1 | 只有 `GenerateAtlasImpl` 失效 |
| `Input`（Legacy） | 85 / 2 | 只有 `compass` / `location` 失效 |
| `Time`、`Object`、`Matrix4x4` | 全部可用 | — |

[Overlay.cs](src/TbhCombatTracker/Overlay.cs) 里另外加了熔断：连续 3 帧绘制失败就永久关闭面板并记一条日志。
UI 的 bug 不该有能力拖垮游戏。

### IL2CPP 方法体去重（会把游戏搞崩的坑）

IL2CPP 把**方法体完全相同**的函数合并成同一段机器码，而且是**全局**合并：本游戏里
`0x6B1620`（一条 `ret`）被 **1872** 个方法共用，`0xCE8880`（一个有完整序言的真函数）
被 **457** 个方法共用。

对这种共享地址挂 Harmony detour 有两种死法，都踩过：

- 同一地址挂**两次** → 其中一个的 trampoline 重新进入 detour → 无限递归栈溢出 → **启动闪退**
- 挂**一次**但地址被无关方法共用 → 那些方法的 `this` 是各种类型，进 wrapper 一转型就
  `NullReferenceException` → **每帧刷屏，游戏进不去**

判据不是"代码是否简单"（`0xCE8880` 就是真函数），而是"这段机器码在全二进制里是否只属于一个方法"：

```powershell
python tools/safe-hooks.py                 # 列出可安全 hook 的方法
python tools/safe-hooks.py --csharp        # 直接出 C# 白名单字面量
python tools/safe-hooks.py --show-unsafe   # 看被排除的和它们的共用数
```

`Patches.TryPatch` 和 `Diagnostics.Apply` 都按原生函数指针做了去重守卫
（[Il2CppUtil.cs](src/TbhCombatTracker/Il2CppUtil.cs)）。

## 诊断模式

搞不清某个逻辑走哪条代码路径时，把配置里的 `DiagnosticMode` 打开。它会给白名单内的
血量控制器/Monster 方法挂钩，记录前几次调用和实参：

```
[diag] Monster.grd(DamageInfo, false)
[diag] pj.gsi(-735.586, Unit("Hero_301(Clone)"))
[diag] pj.gxq(1.5, false, false)
[diag] pf.gsi(1.5, null)
```

伤害入口就是这么定位出来的——**别再靠签名猜**。这个模式是**一次性**的：启用后立刻把配置
写回 `false`，万一挂载又把游戏搞崩，下次启动自动是关闭状态，不会陷在崩溃循环里。

## 剩余待实测项

1. **DOT / 陷阱伤害是否绕过 `grd` 直接调 `gsi`。** 若绕过，数值仍会被统计，
   但暴击率和类型拆分会落到"未分类"（`ByType[0]`）。
2. **英雄显示名。** 目前用 GameObject 名去掉 `(Clone)`，即 `Hero_301` 这种。
   把 `ProbeMode` 打开看 `gpz()` / `gqa()` 返回什么，也许能拿到职业名。

---

## 游戏更新之后

游戏用了 GUPS.Obfuscator，**方法名和内部类名每次更新都可能全变**（`gsd` → 别的三字母）。
重新对齐流程：

```powershell
# 1. 先看 hook 点还在不在（快，几秒）
dotnet run --project tools/sigcheck/sigcheck.csproj

# 2. 有缺失才需要重新 dump 全量符号（慢，53MB dump.cs）
pwsh tools/dump-symbols.ps1
```

`sigcheck` 返回 2 就说明混淆名变了。按 [docs/symbols.md](docs/symbols.md) 里记录的
"识别特征"在新 dump 里重新定位，然后改两个地方：

- `tools/sigcheck/Program.cs` 顶部的 `Targets`
- `src/TbhCombatTracker/Patches.cs` 顶部的 using 别名和方法名常量

注意游戏更新后 BepInEx 的 `interop\` 缓存也要清掉重新生成，否则拿到的还是旧签名。

---

## 分享给别人

面向使用者的安装文档是 [docs/INSTALL.md](docs/INSTALL.md)（本 README 是给开发者看的）。

打发布包：

```powershell
pwsh tools/package-release.ps1
```

产出 `build/release/TbhCombatTracker-v<版本>.zip`，结构对齐游戏根目录，
对方装好 BepInEx 之后解压覆盖即可：

```
BepInEx/plugins/TbhCombatTracker.dll
安装说明.md
反作弊说明.md
LICENSE
```

**不打包 BepInEx 本体**——它是 LGPL 的独立项目、版本更新频繁，让用户自己去
官方构建站拿更稳妥，也免得本项目变成它的分发方。

发布前自查：

1. 在**干净的游戏目录**上按 `安装说明.md` 从零走一遍
2. 写清楚测试过的游戏版本（当前 1.01.05）和 BepInEx 构建号（当前 be.785）
3. **不要**把 `build/dump/` 的符号 dump（52MB，游戏反编译产物）或
   `build/downloads/` 的 BepInEx 安装包一起发出去——`.gitignore` 已经挡住了，
   但手动打包时容易误带

## 反作弊风险

已经把游戏的反作弊完整逆向过一遍，结论和证据在 **[docs/anticheat.md](docs/anticheat.md)**。

摘要：

- 游戏用了 Anti-Cheat Toolkit，但**只启动了 3 个检测器**：`SpeedHackDetector`、
  `ObscuredCheatingDetector`、`TimeCheatingDetector`。
- **`InjectionDetector` 从未被启动**（启动 API 零调用点），字符串表里也**没有**
  BepInEx / MelonLoader / Cheat Engine 之类的黑名单。游戏不检测 Mod 加载器。
- 触发检测只会向开发者的一个 Google Apps Script webhook 发一条遥测，
  客户端里**没有封禁逻辑**。
- 带 BepInEx 实跑一轮关卡，游戏日志的反作弊相关命中数和未安装时**完全一致**。

本 Mod 只读、不写内存、不改时间、不碰存档，三个在跑的检测器一个都碰不到。

**给贡献者的红线**（做了就会真的上报）：给 `ObscuredInt/Float` 字段赋值、改 `Time.timeScale`、
禁用检测器组件、改 `.es3` 存档、任何拉高掉落/金币/品质的改动。详见
[docs/anticheat.md](docs/anticheat.md) 第 5 节。

即便如此仍建议玩家先备份存档：`%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskBarHero`
