# TBH Combat Tracker

**简体中文** | [English](README.en.md)

给 **TBH: Task Bar Hero** 用的战斗统计面板。横向悬浮窗，按英雄拆分输出 / 承伤 / 治疗，
点开任一角色可看技能与伤害类型的环形图明细，按关卡自动分段，可导出 CSV。
另有一个仿 ACT 的战斗记录窗口：本局每一段战斗都能回看，每局的战斗事件自动存成日志，可以导入重新解析。
设置都能在游戏里改。界面文本跟随游戏语言。

**只读统计，不修改任何游戏数值。**

当前适配：游戏 **1.2.8**，BepInEx **6.0.0-be.785**。

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
| `F9` | 显示 / 隐藏浮窗 |
| `F8` | 打开 / 关闭战斗记录 |
| `F10` | 重置当前统计（旧的一段收进战斗记录） |
| `F11` | 把当前这一段导出成 CSV 到 `BepInEx\TbhCombatTracker\` |

- 浮窗平时只有文字和卡片浮在游戏上（字带黑色描边）；鼠标移上去，半透明的背景和标题栏按钮才出现（背景浓淡在设置里调）
- 浮窗标题栏：最左的记录图标打开战斗记录；右边依次是视图切换（**输出 / 承伤 / 治疗**）、重置、设置
- **点击角色卡片**弹出明细：普通攻击与各技能占比、伤害类型与元素占比
- 默认按关卡自动分段，标题显示当前关卡名；自动重复挑战同一关时带序号，如 `关卡 3-2 #2`
- 窗口按住就能拖，位置会记住；图标按钮悬停一会儿有文字提示

游戏窗口平时是点击穿透的。光标移到面板上时会自动解除穿透让你操作，移开即恢复，不影响正常用电脑。

### 战斗记录

浮窗标题栏最左的记录图标（或 `F8`）打开，形制参考 ACT 的主窗口：

- **左边**是这一局的全部分段，最新的在上面，进行中的那段标「实时」。Boss 战这类很快打完的战斗，结束后点它就能细看。
  底部显示当前看到的是第几到第几段、一共多少段
- 只有最近 20 段（可调）的详细数据留在内存里，更早的在列表里照常列出，字是灰的、带一个软盘图标，
  点开时从本局的战斗日志重新载入——列表始终是本局完整的记录，内存不会越涨越多
- **右边**是选中那段的统计：输出 / 承伤 / 治疗的战斗员表格（总量、占比、每秒、暴击、次数、最高），
  每个人每秒数值随时间变化的曲线（鼠标移上去看每一秒的数值），以及拆分表和环形图：
  按技能 / 伤害类型 / 元素 / **目标**（打了哪种怪；承伤是被哪种怪打，治疗是治疗了谁）。
  点表格里的一行只看这个人，再点一次或点空白处取消、回到全队；拆分表的行和环形图互相高亮，
  超过 8 项时用滚轮翻看。环上第 8 项之后的、占比太小画不出来的并成灰色的「其他」（表里是灰点）
- 右上角可以把下半截换成**逐条事件**：这一段的每一次伤害 / 承伤 / 治疗按时间排开（来源、目标、技能、数值、暴击、类型），
  同样跟着视图和选中的人筛选。实时那段跟着最新的往下滚，已经结束的段和导入的日志从日志文件里读
- 顶部的「导出 CSV」导出选中的那段；「日志目录」打开存日志的文件夹

### 设置

浮窗或战斗记录标题栏上的齿轮打开：界面缩放、卡片斜切角度、分段方式、内存里保留几段、统计开关、战斗日志、
更新、热键、职业颜色都能直接改，改完自动存进配置文件。标着「重启后生效」的几项下次启动游戏才生效。

### 战斗日志

每局游戏都会把战斗**事件**（每一次伤害、治疗、关卡变化……）写进一份日志：
`BepInEx\TbhCombatTracker\logs\tbh-日期-时间.tbhlog.gz`，实测一小时约 0.2 MB，默认保留 30 天。

战斗记录的「导入」可以打开任意一份日志，**按当前版本重新解析**——以后更新加了新的统计维度，旧日志导入也能看到。
别人的日志放进这个文件夹也能导入。格式说明见 [战斗日志格式](https://github.com/DPS-Love/tbh-combat-tracker/blob/main/docs/eventlog.md)。

## 配置

`BepInEx\config\dpslove.tbh.combattracker.cfg`，**游戏启动一次后才会生成**。
下面这些在游戏里的设置界面都能改；直接改文件的话要重启游戏。

| 配置项 | 默认 | 说明 |
|---|---|---|
| `SegmentByStage` | true | 按关卡自动分段；关掉则按 `IdleResetSeconds` 空闲秒数分段 |
| `KeepInMemory` | 20 | 最近多少段留着详细数据，更早的点开时从本局日志重新载入；`0` 为全部留着（关掉 `LogEvents` 时也全部留着） |
| `TrackIncoming` / `TrackHealing` / `TrackSkills` | true | 承伤 / 治疗 / 技能拆分 |
| `UiScale` | 1.0 | 界面缩放 |
| `SkewDegrees` | -30 | 卡片斜切角度，`0` 为普通矩形 |
| `BackgroundOpacity` | 0.7 | 浮窗、角色拆分和战斗记录窗口背景的不透明度（0–1）；浮窗只在鼠标移上去时显示背景 |
| `LogEvents` | true | 写战斗日志；关掉的话战斗记录只有本局的数据，也不能导入 |
| `LogRetentionDays` | 30 | 战斗日志保留天数，`0` 为永久保留 |
| `CheckUpdates` | true | 启动时检查更新（见下） |
| `AutoInstall` | false | 发现新版本后自动下载替换，重启生效 |

`[Colors]` 段是六个职业的卡片颜色（设置界面里有一组预设色可选），也可以直接改十六进制值（`#RGB` / `#RRGGBB` / `#RRGGBBAA`）。

## 更新提示

启动时会读一次仓库里的 `manifest.json`（**只读取，不上传任何数据**）：

- **有新版本** → 浮窗顶部横幅。点「下载页」自己下，或点「更新」由它下载、校验 SHA-256 并替换文件，重启游戏生效
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

**左上角提示"界面加载失败"** — 统计和热键照常工作，只是界面没建起来。把 `BepInEx\LogOutput.log` 发到 Issues。

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
