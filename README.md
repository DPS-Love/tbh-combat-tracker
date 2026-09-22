# TBH Combat Tracker

**简体中文** | [English](README.en.md)

给 **TBH: Task Bar Hero** 用的战斗统计面板。横向悬浮窗，按英雄拆分输出 / 承伤 / 治疗，
点开任一角色可看技能与伤害类型的饼图明细，按关卡自动分段，可导出 CSV。
面板文本跟随游戏语言。

**只读统计，不修改任何游戏数值。**

当前适配：游戏 **1.2.6**，BepInEx **6.0.0-be.785**。

---

## 下载

从 [Releases](https://github.com/DPS-Love/tbh-combat-tracker/releases/latest) 下载最新的
`TbhCombatTracker-vX.Y.Z.zip`。

## 安装

### 1. 装 BepInEx 6 IL2CPP

到 <https://builds.bepinex.dev/projects/bepinex_be> 下载
**`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785.zip`**，解压到游戏根目录（和 `TaskBarHero.exe` 同一层）：

```
TaskbarHero\
├── TaskBarHero.exe
├── winhttp.dll          ← BepInEx 解压出来的
├── doorstop_config.ini
├── dotnet\
└── BepInEx\
```

> 要用构建站上的 BE 版本，不要用 GitHub Release 上的 `6.0.0-pre.2`——后者比 Unity 6 还早，认不出这个游戏。

### 2. 先启动一次游戏

BepInEx 首次运行要生成中间程序集，**需要 1–3 分钟**，期间窗口可能像卡住，别关。
出现这个文件就好了：`TaskbarHero\BepInEx\interop\Assembly-CSharp.dll`

### 3. 装本 Mod

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

- 标题栏按钮在 **输出 / 承伤 / 治疗** 三个视图间切换；整个面板可拖动
- **点击角色卡片**弹出明细：普通攻击与各技能占比、伤害类型与元素占比
- 默认按关卡自动分段，标题显示当前关卡名

游戏窗口平时是点击穿透的。光标移到面板上时会自动解除穿透让你操作，移开即恢复，不影响正常用电脑。

## 配置

`BepInEx\config\dpslove.tbh.combattracker.cfg`，**游戏启动一次后才会生成**，改完重启游戏。

| 配置项 | 默认 | 说明 |
|---|---|---|
| `SegmentByStage` | true | 按关卡自动分段；关掉则按 `IdleResetSeconds` 空闲秒数分段 |
| `TrackIncoming` / `TrackHealing` / `TrackSkills` | true | 承伤 / 治疗 / 技能拆分 |
| `UiScale` | 1.0 | 面板缩放 |
| `SkewDegrees` | -30 | 卡片斜切角度，`0` 为普通矩形 |
| `FixClickThrough` | true | 关掉的话面板只能看，点击会穿透过去 |
| `CheckUpdates` | true | 启动时检查更新（见下） |
| `AutoInstall` | false | 发现新版本后自动下载替换，重启生效 |

`[Colors]` 段是六个职业的卡片颜色，直接改十六进制值即可（`#RGB` / `#RRGGBB` / `#RRGGBBAA`）。

## 更新提示

启动时会读一次仓库里的 `manifest.json`（**只读取，不上传任何数据**）：

- **有新版本** → 面板顶部横幅。点「下载页」自己下，或点「更新」由它下载、校验 SHA-256 并替换文件，重启游戏生效
- **当前版本被标记为在你的游戏版本上会出问题** → 红色横幅说明原因。**不会自动做任何事**——
  可以点「停用」让统计在本次游戏里停下（重启恢复），或更新，或什么都不做
- **游戏更新了而 Mod 还没跟上** → 琥珀色提示，统计可能缺失，游戏本身不受影响

不想联网就把 `CheckUpdates` 改成 `false`。自动更新后若新版本加载失败，把
`BepInEx\plugins\TbhCombatTracker.dll.old` 改回 `TbhCombatTracker.dll` 即可回滚。

## 关于封号风险

游戏用了 Anti-Cheat Toolkit，但只启动了加速、内存篡改、系统时间三个检测器，本 Mod 一个都不碰——
不写内存、不改时间、不动存档；**注入检测器从未被启动**，也没有 Mod 加载器黑名单；检测触发后
客户端也**没有封禁逻辑**。完整分析与证据见
[反作弊说明](https://github.com/DPS-Love/tbh-combat-tracker/blob/main/docs/anticheat.md)。

尽管如此，服务端拿到遥测后做什么从客户端看不出来。建议先备份存档
`%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskBarHero`，**用不用、风险自负**。

## 卸载

删掉 `BepInEx\plugins\TbhCombatTracker.dll`。要连 BepInEx 一起卸，删掉游戏根目录的
`winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`dotnet\`、`BepInEx\`，或在 Steam 里"验证游戏文件完整性"。

## 常见问题

**面板没出现** — 看 `BepInEx\LogOutput.log` 有没有 `Loading [TBH Combat Tracker]`；没有多半是 DLL 放错位置。按 `F9` 确认不是被隐藏了。

**按钮点不动 / 拖不动** — 确认配置里 `FixClickThrough = true`。

**游戏更新后失效** — 正常现象：游戏代码用了混淆器，每次更新方法名都可能变。日志会写明哪个 hook 挂载失败，面板也会提示；等新版本即可，游戏本身不受影响。

**Steam 验证文件后 BepInEx 没了** — Steam 会把 `winhttp.dll` 当多余文件删掉，重装 BepInEx 即可，插件和配置不受影响。

---

## 开发

构建、逆向工具、游戏更新后的重新对齐、发布流程见
[docs/DEVELOPMENT.md](https://github.com/DPS-Love/tbh-combat-tracker/blob/main/docs/DEVELOPMENT.md)。

## 许可与声明

[MIT 协议](LICENSE)。

本项目是 TBH: Task Bar Hero 的**非官方粉丝作品**，与开发商 TesseractStudio 无关，未获其背书。
游戏本身及其资产的权利归各自权利人所有；本项目只读取游戏运行时的可观测状态供玩家自用，
**不包含也不分发任何游戏资产**。
