<#
.SYNOPSIS
    找游戏目录并输出。tools\ 下其它脚本的 -GameDir 都经过这里；单独运行可以看看会用哪个目录。

.DESCRIPTION
    和 Directory.Build.props 同一套规则，按顺序：
      1. -GameDir
      2. 环境变量 TBH_GAME_DIR
      3. Steam 默认库：注册表里的 SteamPath\steamapps\common\TaskbarHero
    游戏装在别的 Steam 库时，设置 TBH_GAME_DIR。
    都找不到就报错；加 -Optional 则什么都不输出。

.EXAMPLE
    pwsh tools/find-game.ps1
#>
param(
    [string]$GameDir,
    [switch]$Optional
)

$ErrorActionPreference = 'Stop'

if (-not $GameDir) { $GameDir = $env:TBH_GAME_DIR }
if (-not $GameDir) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($steam) {
        # SteamPath 是 d:/steam 这种正斜杠写法，规范一下
        $dir = [System.IO.Path]::GetFullPath((Join-Path $steam 'steamapps\common\TaskbarHero'))
        if (Test-Path (Join-Path $dir 'TaskBarHero.exe')) { $GameDir = $dir }
    }
}

if ($GameDir) { return $GameDir }
if (-not $Optional) {
    Write-Error '找不到游戏目录。游戏不在 Steam 默认库时，设置环境变量 TBH_GAME_DIR，或用 -GameDir 指定。'
}
