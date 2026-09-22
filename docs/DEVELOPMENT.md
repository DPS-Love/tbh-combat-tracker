# 开发文档

面向想改代码、或在游戏更新后重新对齐符号的人。玩家看仓库根目录的 README 即可。

当前对齐：游戏 **1.2.6** / BepInEx **6.0.0-be.785** / .NET SDK 8。

---

## 开发环境

### 1. .NET SDK 8

`dotnet --list-sdks` 有输出即可。

### 2. BepInEx 6 IL2CPP

```powershell
pwsh tools/install-bepinex.ps1                    # 默认 be.785，本地没有就自动下载
pwsh tools/install-bepinex.ps1 -Build 790         # 换个构建号
pwsh tools/install-bepinex.ps1 -Uninstall         # 卸载（保留 plugins\ 和 config\）
```

只能用 `builds.bepinex.dev` 的 BE 构建：GitHub Release 上最新的 IL2CPP 包 `6.0.0-pre.2`
比 Unity 6 还早，啃不动本游戏的 metadata v31。

BepInEx IL2CPP 走 `winhttp.dll` 代理注入，Steam"验证游戏文件完整性"会把它删掉，重跑脚本即可。

### 3. 启动一次游戏

BepInEx 首次运行用 Cpp2IL 反编译 `GameAssembly.dll`，再用 Il2CppInterop 生成可被 C# 引用的
代理程序集，耗时 1–3 分钟。产物 `BepInEx\interop\Assembly-CSharp.dll` 出现后才能编译本项目。

## 构建

```powershell
dotnet build src/TbhCombatTracker/TbhCombatTracker.csproj -c Release
```

成功后自动部署到 `<游戏目录>\BepInEx\plugins\`。游戏路径在 [Directory.Build.props](../Directory.Build.props)
的 `GameDir`，也可命令行覆盖 `-p:GameDir="E:\..."`。

构建前会跑两道检查，不合格直接失败：

| 检查 | 做什么 |
|---|---|
| `CheckGameRefs` | 游戏和 interop 程序集在不在，给出人话报错 |
| `CheckPatchGuards` | 每个 Harmony 补丁方法是否被 `try/catch` 包住（见下文「补丁边界」） |

版本号和构建时的游戏版本由 `GenerateBuildInfo` 生成进 `BuildInfo.g.cs`，
`[BepInPlugin]` 和更新检查都从那里读，不要手写。

---

## 工作原理

```
Monster.<TakeDamage>(DamageInfo, bool)      ← Prefix/Finalizer: 记下暴击 / 伤害类型 / 元素属性
  └─ UnitHealth.<ChangeHp>(float, Unit)     ← Postfix: 负数 = 最终伤害，第二参 = 攻击者
```

伤害结算分两步：`TakeDamage` 拿到带分类信息的 `DamageInfo`，算完暴击和抗性减免后把**最终增量**
交给 `ChangeHp`——负数是伤害，正数是治疗，共用一个入口。两边各缺一半信息，所以用
Prefix/Finalizer 夹住外层，在内层把信息拼起来。攻击者归因靠 `ChangeHp` 的 `Unit source` 参数。

挂哪个类由覆写关系决定：怪物的血量类没覆写 `ChangeHp`，走基类；英雄的覆写了，要单独挂。

治疗走同一个入口，但 `source` 恒为 null，所以从技能侧夹上下文（牧师技能的执行方法），
再用恢复总入口的两个 bool 标志区分自然回复 / 战斗回复 / 技能治疗。
技能级归因挂在 `ActiveSkill.AttackDamage()`（惰性工厂，命中时才求值，弹道和 AOE 也能归对）。

这些方法在游戏里都是三字母混淆名，每次更新都会变。当前值和识别方法见 [symbols.md](symbols.md)。

### 代码分工

| 文件 | 职责 |
|---|---|
| `GameSymbols.cs` | **所有混淆名**：类型别名、Harmony 方法名、字段访问器。游戏更新后只改这里 |
| `Strings.cs` | **所有由我们提供的文案**（中 / 英），界面模块不写字面量 |
| `Patches.cs` | hook 逻辑：挂哪些方法、Prefix/Postfix 各拿什么。不含混淆名 |
| `DamageTracker.cs` | 聚合统计、战斗切分、CSV 导出 |
| `Healing.cs` / `SkillTracker.cs` | 恢复来源分类、技能级归因的上下文 |
| `StageWatcher.cs` | 关卡分段信号 |
| `Localize.cs` | 读游戏本地化表（英雄名、技能名、元素属性） |
| `Overlay.cs` / `DetailWindow.cs` / `PieChart.cs` | IMGUI 面板、角色明细、饼图 |
| `UpdateChecker.cs` / `UpdateBanner.cs` | 更新检查、一键更新、横幅 |
| `RaycastAnchor.cs` / `ClickThrough.cs` | 解除窗口点击穿透（射线靶为主，Win32 兜底） |
| `Plugin.cs` / `Mod.cs` / `TrackerBehaviour.cs` | 入口、加载器门面、注入 IL2CPP 域的 MonoBehaviour |
| `Diagnostics.cs` / `Il2CppUtil.cs` | 诊断模式、原生指针去重 |

---

## 四条硬规则

### 补丁边界：异常一个都不许漏

**Harmony 的 Prefix 抛异常，原方法就不执行。** 一个只读统计 Mod 因此能把游戏功能整个搞没
（真发生过，见 [symbols.md 第 12 节](symbols.md)）。游戏每次更新都改混淆名，`MissingMethodException`
迟早还会出现，所以目标不是"不出异常"，而是**出了异常也只影响统计**。

每个补丁方法的第一条语句必须是 `try`，`catch` 里调 `LogOnceInternal`。
`python tools/check-guards.py --list` 检查，构建前自动跑。

### 只挂机器码全局唯一的方法

IL2CPP 把**方法体完全相同**的函数合并成同一段机器码，且是全局合并（本游戏一条 `ret` 被 1800+ 个方法共用）。
对共享地址挂 detour：挂两次 → 递归栈溢出、启动闪退；挂一次 → 无关方法的 `this` 类型对不上，NRE 刷屏。

```powershell
python tools/safe-hooks.py --types pq pm po Monster Hero --show-unsafe
```

判据是"这段机器码在全二进制里是否只属于一个方法"，不是"代码是否简单"。
`Patches.TryPatch` 按原生指针做了去重守卫。

### 新 UI 代码先查 API 有没有被裁剪

IL2CPP 裁剪了游戏自身不用的托管方法，Il2CppInterop 还原失败的会生成一个直接 `throw` 的桩——
编译期毫无提示。`GUILayout.FlexibleSpace()` 就是这样，曾导致启动卡死。

```powershell
dotnet run --project tools/sigcheck/sigcheck.csproj -- --stripped UnityEngine.IMGUIModule GUILayout GUI
```

`Overlay` 另有熔断：连续 3 帧绘制失败就永久关闭面板。

### 生存路径不能引用游戏类型

.NET 的 JIT 在编译一个方法时会加载它**引用**的所有类型。游戏更新把 `pq` 改名之后，
`typeof(pq)` 所在的整个方法在 JIT 阶段就抛 `TypeLoadException`——连第一行都跑不到。
要是这发生在 `Plugin.Load` 的调用链上，插件整个加载失败：没有窗口，也就没有更新提示，
而那正是最需要提示的时刻。

规则：`Plugin.Load` → `TrackerBehaviour` → `Overlay` / `UpdateBanner` / `UpdateChecker` 这条路上，
任何方法的**签名和方法体**都不能出现游戏类型。需要碰游戏类型的代码放进单独的方法，
在调用处 `try/catch`（`Patches.TryPatch` 的 `Func<Type>`、`TrackerBehaviour` 里对 `StageWatcher.Tick` 的包裹、
`BuiltinText.ReadLocaleCode` 都是这个形状）。

同一条规则的另一面：**生存路径上不能触发游戏系统的初始化。**
`LocalizationSettings.SelectedLocale` 在本地化未初始化时会同步跑完整个 Addressables 初始化；
我们的第一帧 OnGUI 早于游戏自己的启动流程，替它把初始化跑掉，游戏的场景就起不来（画面全黑、UI 布局狂刷警告）。
所以 `Strings.ReadLocaleCode` 只读已缓存的异步句柄，没完成就按英文走。凡是"读一下就会顺手初始化"的
游戏 / 引擎 API，第一帧都不能碰。

发布前验证：

```powershell
pwsh tools/simulate-update.ps1            # 把 DLL 里的游戏类型引用改成不存在的名字后部署
# 启动游戏：窗口必须出现，横幅显示「本版 Mod 与当前游戏不匹配」，日志里各 hook 报"类型不存在"
pwsh tools/simulate-update.ps1 -Restore   # 重新构建，换回真 DLL
```

---

## 游戏更新之后

游戏用了 GUPS.Obfuscator，方法名、字段名、内部类名每次更新都可能变；
`[SerializeField]` 字段、枚举、`public` struct 字段、编译器生成的状态机名、少数顶层类名不变。

```powershell
# 1. 哪些 hook 点还在（几秒）
dotnet run --project tools/sigcheck/sigcheck.csproj

# 2. 有缺失才重新 dump（慢，~50MB dump.cs）
pwsh tools/dump-symbols.ps1
```

然后按 [symbols.md](symbols.md) 第 11 节的方法论重新定位，可靠性从高到低：

1. **没被混淆的名字**——`UnitHealthController` / `Hero.cache` / `ActiveSkill.skillCache` / `<HealthRegenAsync>d__20`
2. **类内唯一的签名**——`python tools/extract-types.py build/dump/dump.cs <类> --methods`
3. **RVA 关系**——同签名的一组里，子类 RVA 互异的是真实现，共用的是转发器
4. **调用点**——`python tools/xref-scan.py --targets <类>.<方法>`；死代码没有调用点
5. slot 号——❌ 别用，会变

改 **`src/TbhCombatTracker/GameSymbols.cs`**——所有混淆名只在这一个文件：类型别名（`global using`）、
Harmony 按名字找的方法（`[Hook]` 标注的常量）、字段访问器。改完构建，再跑 `sigcheck`：
它直接读构建出的 DLL，核对其中引用的每一个游戏类型和成员、以及 `[Hook]` 标注的方法名是否仍在，
不用维护清单。`safe-hooks.py` 应全部唯一。

> **名字还在 ≠ 含义没变。** 混淆名会被复用给别的成员（`bilm` 曾从 `Unit` 变成 `int`，`hbs` 曾从三参变两参）。
> 核对类型和签名，不要只看名字。挂载日志带参数类型就是为此。

静态验证只能保证挂得上、不崩；数值和分类是否正确要进游戏对账，恢复来源分类用 `HealingDebug`。

## 诊断模式

搞不清某段逻辑走哪条路径时，把 `DiagnosticMode` 打开：给白名单内的血量控制器 / Monster 方法挂钩，
记录前几次调用和实参。**一次性**——启用后立刻把配置写回 `false`，万一挂载把游戏搞崩也不会陷入崩溃循环。

---

## 更新检查与一键更新

启动时 GET 仓库 `main` 上的 [manifest.json](../manifest.json)（先 jsDelivr，再 GitHub 原始文件；
都不通就静默放弃）。只读，不上传任何东西。

```json
{
  "latest": { "version": "0.2.3", "gameVersion": "1.2.4", "url": "…", "download": "…zip", "sha256": "…",
              "critical": false, "notes": { "zh": "…", "en": "…" } },
  "broken": [ { "modMax": "0.2.1", "gameMin": "1.2.2", "reason": { "zh": "…", "en": "…" } } ]
}
```

| 情况 | 面板 | 自动做的事 |
|---|---|---|
| `latest` 比本机新 | 琥珀色横幅（`critical` 则红）+「更新」「下载页」 | 仅 `AutoInstall=true` 时下载替换 |
| 本版本命中 `broken` | 红色横幅 +「停用」「更新」「下载页」，面板收起时也画 | **无**——继续用、停用、更新都由玩家点按钮 |
| 游戏比构建时新（本地判断） | 琥珀色提示 | 无 |

**清单只能通知，不能动作。** 任何改变 Mod 行为的操作都只由玩家的点击触发；`broken` 条目的全部效果是一条横幅。

一键更新：下载 zip → 校验 SHA-256（清单没有哈希就拒绝）→ 抠出 DLL → 把运行中的 DLL 改名为 `.old`
（Windows 允许改名已加载的文件）→ 写入新文件。下次启动生效，启动时自动清理 `.old`。

后台线程只写结构化事实，**不碰任何 Il2Cpp 对象**；语言选择和字符串拼接在主线程的 `UpdateBanner` 里做。

### 发布前看一眼横幅

真清单对刚构建的版本什么都不会显示（没有更新、也没被点名）。用本地测试清单：

```powershell
pwsh tools/package-release.ps1                          # 构建 + 打 zip
pwsh tools/test-manifest.ps1 -Scenario install -Apply   # 生成 build/test-manifest.json 并写进 cfg
```

`-Scenario` 可选 `update`（琥珀）/ `critical`（红）/ `install`（可一键更新，download 指向本机刚打的 zip）/
`broken`（当前版本被点名，红 + 「停用」）。启动游戏、按 F9 即可看到。
`install` 场景点「更新」会真的走一遍替换：换上的是同一个版本，`plugins` 里会多出 `.old`，横幅变绿。

看完把 cfg 里的 `ManifestUrl` 清空，恢复官方地址。

---

## 发布

```powershell
pwsh tools/package-release.ps1                 # 只打包：build/release/TbhCombatTracker-vX.Y.Z.zip
```

包内：`BepInEx/plugins/TbhCombatTracker.dll`、`安装说明.md`（即 README）、`反作弊说明.md`、`LICENSE`。
**不打包 BepInEx 本体**（LGPL 独立项目，让用户自己去官方构建站拿）。

发新版本：

```powershell
# 1. 改 csproj 的 <Version>，提交
# 2. 打标签；CI 核对版本号并建草稿 Release
git tag v0.2.3 && git push origin main v0.2.3
# 3. 本机补产物并发布；顺带把 manifest.json 更新成这个版本、推送、清 jsDelivr 缓存
pwsh tools/package-release.ps1 -Upload -NotesZh "…" -NotesEn "…" [-Critical]
```

要让某个旧版本的玩家看到红色警告，手动把它加进 `manifest.json` 的 `broken` 列表再提交。

CI（[release.yml](../.github/workflows/release.yml)）默认只建草稿：编译要引用从游戏本体生成的
interop 程序集，那是游戏代码的派生物，不能进公开仓库，runner 上也生成不了。
若建一个**私有**仓库放 `BepInEx/core/*.dll` 与 `BepInEx/interop/*.dll`，并配
`GAME_REFS_REPO` / `GAME_REFS_TOKEN` 两个 secret，工作流会自动切换成 CI 编译并直接发布。

发布前：在干净的游戏目录按 README 从零装一遍；跑一次 `tools/simulate-update.ps1` 确认类型全丢时窗口和横幅仍在；
`build/dump/`（游戏反编译产物）和 `build/downloads/` 绝不能进包。

---

## 反作弊

完整分析在 [anticheat.md](anticheat.md)。给贡献者的红线（做了就会真的上报）：
给 `ObscuredInt/Float` 字段赋值、改 `Time.timeScale`、禁用检测器组件、改 `.es3` 存档、
任何拉高掉落 / 金币 / 品质的改动。本项目的 csproj 不引用 ACTk，也不需要。

## 工具一览

| 工具 | 用途 |
|---|---|
| `tools/install-bepinex.ps1` | 安装 / 切换 / 卸载 BepInEx BE 构建 |
| `tools/dump-symbols.ps1` | Il2CppDumper 重新 dump 符号到 `build/dump/` |
| `tools/sigcheck/` | 读构建出的 DLL，核对它引用的每个游戏类型 / 成员 / `[Hook]` 方法名是否还在；`--stripped` 查被裁剪的 API；`--members` 列 interop 成员；`--dump` 看类型生成形态 |
| `tools/extract-types.py` | 从 dump.cs 抠类型；`--methods` 列方法 + RVA |
| `tools/xref-scan.py` | 机器码调用点扫描：谁调用了它 / 它调用了谁 |
| `tools/safe-hooks.py` | 机器码全局唯一性，决定哪些方法能挂 |
| `tools/check-guards.py` | 补丁方法异常防护检查（构建前自动跑） |
| `tools/test-manifest.ps1` | 生成本地测试清单并写进 cfg，发布前看横幅效果 |
| `tools/simulate-update.ps1` | 模拟游戏更新改名类型，验证窗口和横幅仍会出现 |
| `tools/dump-localization.py` | 离线解包 Addressables 里的本地化字符串表 |
| `tools/package-release.ps1` | 打包 / 上传 Release / 更新 manifest |
