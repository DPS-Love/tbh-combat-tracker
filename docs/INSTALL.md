# TBH Combat Tracker — 安装说明

给 **TBH: Task Bar Hero** 用的伤害统计面板。横向悬浮窗，按英雄拆分 DPS、伤害占比、
暴击率和单次最大伤害，按关卡自动分段，可导出 CSV。

**只读统计，不修改任何游戏数值。**

---

## 环境要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows |
| 游戏版本 | 在 **1.2.0** 上开发和测试。别的版本大概率也能跑，但游戏更新后可能失效（见文末） |
| 前置 | **BepInEx 6 (IL2CPP, x64)** —— 必装，本 Mod 只是它的一个插件 |

---

## 安装

### 第一步：装 BepInEx 6 IL2CPP

到 <https://builds.bepinex.dev/projects/bepinex_be> 下载
**`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.XXX.zip`**（本 Mod 在 **be.785** 上测试通过），
解压到游戏根目录，也就是和 `TaskBarHero.exe` 同一层：

```
TaskbarHero\
├── TaskBarHero.exe
├── winhttp.dll          ← BepInEx 解压出来的
├── doorstop_config.ini
├── dotnet\
└── BepInEx\
```

> **为什么不是 GitHub Release 上那个正式版：** BepInEx 6 至今没发过正式版，
> Release 页最新的 IL2CPP 包是 `6.0.0-pre.2`（2024-08），比 Unity 6 发布还早，
> 认不出这游戏的元数据格式。IL2CPP 的活跃开发都在上面那个 BE 构建站。

### 第二步：先启动一次游戏

BepInEx 首次运行要反编译游戏生成中间程序集，**要 1–3 分钟**，
期间游戏窗口可能看着像卡住，别急着关。

完成的标志是这个文件出现了：

```
TaskbarHero\BepInEx\interop\Assembly-CSharp.dll
```

### 第三步：装本 Mod

把发布包里的 `BepInEx` 文件夹解压覆盖到游戏根目录，最终是：

```
TaskbarHero\BepInEx\plugins\TbhCombatTracker.dll
```

启动游戏即可。

---

## 使用

| 按键 | 作用 |
|---|---|
| `F9` | 显示 / 隐藏面板 |
| `F10` | 重置当前统计 |
| `F11` | 导出 CSV 到 `BepInEx\TbhCombatTracker\` |

- **整个面板都能拖动。**
- 标题栏右侧两个按钮：**输出 / 承伤**切换、**重置**。
- 默认按关卡自动分段，标题显示当前关卡名。

游戏窗口平时是点击穿透的（这样你能照常操作桌面）。光标移到面板上时会自动解除穿透
让你点按钮和拖窗口，移开后自动恢复——不影响你正常用电脑。

---

## 配置

配置文件在 `BepInEx\config\dpslove.tbh.combattracker.cfg`，**游戏启动一次后才会生成**。
改完需要重启游戏。

常用项：

| 配置项 | 默认 | 说明 |
|---|---|---|
| `SegmentByStage` | true | 按关卡自动分段。关掉则改用空闲时间分段 |
| `TrackIncoming` | true | 是否统计英雄承受的伤害 |
| `UiScale` | 1.0 | 面板缩放 |
| `SkewDegrees` | -30 | 卡片斜切角度，设 `0` 就是普通矩形 |
| `FixClickThrough` | true | 关掉的话面板变成纯展示，点击会穿透过去 |

`[Colors]` 段是六个职业的卡片颜色，可以直接改十六进制值
（支持 `#RGB` / `#RRGGBB` / `#RRGGBBAA`）：

```ini
[Colors]
Knight = #C0392B     # 骑士
Ranger = #5FB04A     # 游侠
Sorcerer = #8E5BD0   # 法师
Priest = #F0D98C     # 牧师
Hunter = #2AA8A0     # 猎人
Slayer = #E07B39     # 杀手
```

---

## 关于封号风险

**结论：本 Mod 只读不写，不触碰游戏在跑的任何一个作弊检测。** 完整的逆向分析和证据见
[anticheat.md](anticheat.md)，摘要：

- 游戏用了 Anti-Cheat Toolkit，但只启动了 3 个检测器：加速、内存篡改、系统时间。
  本 Mod 一个都不碰——不写内存、不改 `Time.timeScale`、不改系统时钟、不动存档。
- **注入检测器（`InjectionDetector`）从未被启动**，游戏也没有任何 Mod 加载器黑名单
  （字符串表里检索 BepInEx / MelonLoader / Cheat Engine 全部零命中）。
- 检测触发后只会向开发者的一个 Google Apps Script 端点发一条遥测，
  客户端里**没有任何封禁逻辑**。

尽管如此，仍然建议先备份存档：

```
%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskBarHero
```

无法承诺的部分也说清楚：服务端拿到遥测后做什么，从客户端看不出来。本 Mod 不产生任何遥测，
但**用不用、风险自负**。

---

## 卸载

删掉 `BepInEx\plugins\TbhCombatTracker.dll` 即可。

要连 BepInEx 一起卸，删掉游戏根目录的 `winhttp.dll`、`doorstop_config.ini`、
`.doorstop_version`、`dotnet\`、`BepInEx\`，或者直接在 Steam 里"验证游戏文件完整性"。

---

## 常见问题

**面板没出现**
看 `BepInEx\LogOutput.log` 里有没有 `Loading [TBH Combat Tracker]`。没有的话多半是
DLL 放错位置，确认是 `BepInEx\plugins\` 下面。按一下 `F9` 也确认下不是被隐藏了。

**按钮点不动 / 窗口拖不动**
确认配置里 `FixClickThrough = true`。仍然不行的话把日志发出来。

**游戏更新后失效了**
很可能。游戏代码用了混淆器，方法名每次更新都可能变。日志里会明确写出来是哪个
hook 挂载失败：

```
主 hook pj.gsi 挂载失败，伤害统计不会工作。
```

这种情况需要重新定位符号并重新编译，见开发文档 [symbols.md](symbols.md)。

**Steam 验证文件后 BepInEx 没了**
正常——Steam 会把 `winhttp.dll` 当多余文件删掉。重装 BepInEx 即可，插件和配置不受影响。
