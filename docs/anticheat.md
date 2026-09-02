# Task Bar Hero 的反作弊：实际做了什么

> 针对游戏 **1.01.05** 的分析。目的是判断一个**只读**的伤害统计 Mod 会不会让玩家被误判。
> 方法：`tools/xref-scan.py` 对 `GameAssembly.dll` 做调用点扫描 + 字符串表检索 + 带 BepInEx 实跑对照。

## TL;DR

| 问题 | 结论 |
|---|---|
| 游戏会不会检测 Mod 加载器 / 注入？ | **不会。** ACTk 的 `InjectionDetector` 打包了但**从未被启动**，字符串表里也没有任何加载器/作弊工具黑名单 |
| 触发检测后会怎样？ | 向开发者的一个 Google Apps Script webhook 发一条 JSON 报告（遥测），客户端里**没有任何封禁逻辑** |
| 本 Mod 会触发吗？ | 只读、不写内存、不改时间、不碰存档 —— 三个在跑的检测器一个都碰不到 |

## 1. 打包了哪些检测器，哪些真的在跑

游戏引入了 Code Stage 的 Anti-Cheat Toolkit (ACTk)。**类存在 ≠ 会运行**——ACTk 的检测器不调
`StartDetection()` 就是死代码。扫描 `GameAssembly.dll` 里 267 万个调用点的结果：

| 检测器 | 启动 API 的调用点 | 状态 |
|---|---|---|
| `SpeedHackDetector` | `StartDetection(Action)` @ `0x180742D10` ← 3 处 | **运行中** |
| `ObscuredCheatingDetector` | `StartDetection(Action)` @ `0x18073D0F0` ← 3 处 | **运行中** |
| `TimeCheatingDetector` | `StartDetection(handler)` @ `0x180744730` ← 3 处 | **运行中** |
| `InjectionDetector` | 全部静态方法 **0 处** | ★ 死代码 |
| `WallHackDetector` | 全部静态方法 **0 处** | ★ 死代码 |

三个调用点都来自同一个类 `or`（全局命名空间的静态类，游戏自研的反作弊入口，
对应源码 `Assets\02_Script\02.Manager\AnomalyDetector.cs`）的
`gof()` / `hrj()` / `kej()` —— 三条启动/重启路径。

`InjectionDetector` 唯一的外部引用来自 `dtr`，那是 ACTk 自带 Examples 场景的演示组件，
只读了个属性，同样是随资源包一起打进来的死代码。

> 顺带说：ACTk 的 `InjectionDetector` 在 IL2CPP 下本来就基本失效——它校验的是托管程序集清单，
> 而 IL2CPP 构建里根本没有那种形态的程序集。就算启动了也抓不到 BepInEx。

## 2. 触发后会发生什么

`or` 的检测回调最终走到：

```
ov.goq(ov.EReportType a, string[] b)      // 全局静态类 ov = 遥测上报
   └─ HTTPS POST application/json
      https://script.google.com/macros/s/AKfycbw...(略).../exec
```

**是一个 Google Apps Script webhook**（通常后面接一张 Google 表格），不是游戏后端
（TheBackend），也不是 Steam。客户端里没有任何"封号""踢下线""锁存档"的逻辑——
它就是把异常事件记一行给开发者看。另外会调 `bba.mbz(EPopupType)` 弹个提示框。

`EReportType` 一共 25 种，只有前 5 种和 ACTk 有关，其余全是**经济系统异常遥测**：

```
ACTk_Speed=0  ACTk_Time=1  ACTk_Memory=2  ACTk_Inject=3  ACTk_Disabled=4
Box_Normal_Rate=5 … Box_StageBoss_Volume=8      掉落率/掉落量异常
Trade_Arcana=9 … Trade_Cosmic=13               交易异常
Grade_Arcana=14 … Grade_Cosmic=18              装备品质异常
ItemLevel_50=19  ItemLevel_80=20
Level_50=21  Level_80=22
Gold_Massive=23                                 金币暴涨
Save_Tampered=24                                存档被篡改
```

注意 `ACTk_Inject=3` 这个类型虽然定义了，但产生它的 `InjectionDetector` 从没启动，所以永远不会被发出。

## 3. 没有黑名单

21039 条字符串字面量里检索
`BepInEx` / `MelonLoader` / `doorstop` / `winhttp` / `Harmony` / `Cheat Engine` / `x64dbg` /
`ArtMoney` / `WeMod` / `injector` / `trainer` —— **零命中**。也没有任何进程或模块枚举相关的字符串。
游戏不按名字找 Mod 工具。

## 4. 实跑对照

带 BepInEx 6 (be.785 + Doorstop + 独立 .NET 运行时注入) 完整打了一轮关卡：

- `BepInEx\LogOutput.log`：干净加载，0 错误
- `Player.log`：反作弊相关关键词命中 **1** 行，且那行是 Odin 序列化器的平台探测（`Odin Serializer detected whitelisted runtime platform…`），与反作弊无关
- 安装 BepInEx **之前**的 `Player-prev.log` 命中数同样是 **1** —— 完全无差异

## 5. 对本 Mod 的结论与红线

三个在跑的检测器分别看什么：

| 检测器 | 触发条件 | 本 Mod |
|---|---|---|
| `ObscuredCheatingDetector` | `ObscuredInt/Float` 的密文和明文影子对不上，即**有人改了内存** | 只读，从不写任何字段 ✅ |
| `SpeedHackDetector` | `Time.timeScale` 被动、系统计时被加速 | 从不碰 ✅ |
| `TimeCheatingDetector` | 系统时钟被改（对表在线时间） | 从不碰 ✅ |

**给后续贡献者的红线**——做下面任何一件事都会真的上报：

1. 给任何 `ObscuredInt` / `ObscuredFloat` 字段赋值 → `ACTk_Memory`
2. 改 `Time.timeScale`（比如加个"加速"功能）→ `ACTk_Speed`
3. 禁用或销毁那几个检测器组件 → `ACTk_Disabled`
4. 改 `.es3` 存档 → `Save_Tampered`
5. 任何拉高掉落率 / 金币 / 装备品质的改动 → `Box_*` / `Gold_Massive` / `Grade_*`

> 参考实现 `ElPinguinoXD/TBH_ModMenu` 的 csproj 引用了 `ACTk.Runtime.dll`——因为它要**修改**
> 游戏数值，而那些字段的类型就是 `ObscuredInt/ObscuredFloat`，必须引用 ACTk 才能构造赋值。
> 那正好是 `ObscuredCheatingDetector` 盯着的操作。本项目的 csproj **不引用 ACTk**，也不需要。

## 6. 这份分析的边界

诚实说明可信度：

- **可靠**：`InjectionDetector` / `WallHackDetector` 的启动 API 零调用点。IL2CPP 的方法体去重
  只会让某地址的调用计数**变多**不会变少，所以"零"是硬结论。
- **不可靠**：非零调用数的归因。IL2CPP 会把方法体相同的函数去重到同一地址（`ph` 那 3 个方法里
  `.ctor` 就和几百个类共用），所以"某方法被调用 N 次"不能单独归因。工具里已加警告。
- **无法排除**：检测器组件若是直接摆在场景里、靠 Unity 序列化实例化并 autoStart，就不会有直接调用点。
  但 ACTk 的 `InjectionDetector` 需要显式 `StartDetection`，加上第 4 节的实跑日志是干净的，
  这个残余可能性很低。
- **根本无法知道**：服务端拿到遥测后做什么。客户端没有封禁逻辑，但开发者事后人工处理是另一回事。
  本 Mod 不产生任何遥测，所以这一层风险不适用——**前提是严格守住第 5 节的红线**。
