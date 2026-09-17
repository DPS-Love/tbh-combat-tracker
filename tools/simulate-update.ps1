<#
.SYNOPSIS
    模拟"游戏更新把混淆类型全改了名"，验证 Mod 在这种情况下窗口和更新提示仍会出现。

.DESCRIPTION
    构建 → 用 sigcheck --simulate-update 把 DLL 里对游戏混淆类型（pq / pm / oa …）的引用
    改成不存在的名字 → 覆盖 plugins 里的 DLL。然后启动游戏，应当看到：
      - 面板照常出现（空的），顶部横幅「本版 Mod 与当前游戏不匹配，统计不可用」
      - 日志里各 hook 报"类型不存在"，插件本身是"就绪"状态
    以前这种情况会让插件整个加载失败（TypeLoadException 在 JIT 阶段抛出），窗口都没有。

    看完用 -Restore 重新构建，把真 DLL 换回去。

.EXAMPLE
    pwsh tools/simulate-update.ps1
    pwsh tools/simulate-update.ps1 -Restore
#>
param(
    [switch]$Restore,
    [string]$GameDir = 'D:\Steam\steamapps\common\TaskbarHero'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\TbhCombatTracker\TbhCombatTracker.csproj'
$plugins = Join-Path $GameDir 'BepInEx\plugins'
$deployed = Join-Path $plugins 'TbhCombatTracker.dll'

function Resolve-Dotnet {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $onPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ($onPath) { $candidates += $onPath }
    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        if (& $c --list-sdks 2>$null) { return $c }
    }
    Write-Error '找不到带 SDK 的 dotnet。'
}

$dotnet = Resolve-Dotnet

if ($Restore) {
    Write-Host '重新构建，换回真 DLL …' -ForegroundColor Cyan
    & $dotnet build $proj -c Release --nologo | Where-Object { $_ -match 'error|已部署|Build succeeded|已成功' }
    if ($LASTEXITCODE -ne 0) { Write-Error '构建失败。' }
    Write-Host '已恢复。' -ForegroundColor Green
    return
}

Write-Host '构建 …' -ForegroundColor Cyan
& $dotnet build $proj -c Release --nologo | Where-Object { $_ -match 'error|Build succeeded|已成功' }
if ($LASTEXITCODE -ne 0) { Write-Error '构建失败，不模拟。' }

$real = Join-Path $root 'src\TbhCombatTracker\bin\Release\TbhCombatTracker.dll'
$mutated = Join-Path $root 'build\simulate\TbhCombatTracker.dll'

Write-Host '改写游戏类型引用 …' -ForegroundColor Cyan
& $dotnet run --project (Join-Path $root 'tools\sigcheck') -c Release -- --simulate-update $real $mutated
if ($LASTEXITCODE -ne 0) { Write-Error '改写失败。' }

Copy-Item $mutated $deployed -Force
Write-Host "`n已把模拟版 DLL 部署到 $deployed" -ForegroundColor Green
Write-Host @"

现在启动游戏，应当看到：
  1. 面板照常出现（没有数据）
  2. 顶部横幅：「本版 Mod 与当前游戏不匹配，统计不可用」
  3. BepInEx\LogOutput.log 里各 hook 报"类型不存在"，但最后一行仍是"就绪。"

看完换回真 DLL：  pwsh tools/simulate-update.ps1 -Restore
"@ -ForegroundColor DarkGray
