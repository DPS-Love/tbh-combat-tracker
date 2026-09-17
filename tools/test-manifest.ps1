<#
.SYNOPSIS
    生成一份本地测试清单，用来在游戏里看更新横幅的实际效果。

.DESCRIPTION
    真清单对刚构建的版本什么都不会显示——没有更新、也没被点名。这个脚本写一份
    build\test-manifest.json，并（-Apply 时）把 cfg 的 [Update] ManifestUrl 指到它。
    版本号从 csproj 读；"新版本" = patch +1。

    -Scenario
      update    有新版本，琥珀色，只有「下载页」按钮（没给 sha256，所以不出现「更新」）
      critical  有重要新版本，红色
      install   有新版本且可一键更新：download 指向 build\release 里最新的 zip，sha256 现算。
                点「更新」会真的走一遍替换——换上的是同一个版本，plugins 里多出 .old，横幅变绿
      broken    当前版本被点名：红色 +「停用」按钮（不限游戏版本）

.EXAMPLE
    pwsh tools/test-manifest.ps1 -Scenario install -Apply
    pwsh tools/test-manifest.ps1 -Reset          # 清空 cfg 里的 ManifestUrl，恢复官方地址
#>
param(
    [ValidateSet('update', 'critical', 'install', 'broken')]
    [string]$Scenario = 'update',
    [switch]$Apply,
    [switch]$Reset,
    [string]$GameDir = 'D:\Steam\steamapps\common\TaskbarHero'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cfg = Join-Path $GameDir 'BepInEx\config\dpslove.tbh.combattracker.cfg'

function Set-ManifestUrl([string]$value) {
    if (-not (Test-Path $cfg)) {
        Write-Error "找不到 $cfg —— 先带新版 DLL 启动一次游戏让配置生成。"
    }
    $lines = @(Get-Content $cfg -Encoding UTF8)
    $key = ($lines | Select-String -SimpleMatch 'ManifestUrl = ' | Select-Object -First 1)
    if ($key) {
        $lines[$key.LineNumber - 1] = "ManifestUrl = $value"
    }
    else {
        # cfg 是旧版 DLL 生成的，还没有这个键：有 [Update] 段就插进去，没有就整段追加
        $section = ($lines | Select-String -SimpleMatch '[Update]' | Select-Object -First 1)
        if ($section) {
            $i = $section.LineNumber
            $lines = $lines[0..($i - 1)] + "ManifestUrl = $value" + $lines[$i..($lines.Count - 1)]
        }
        else {
            $lines += '', '[Update]', "ManifestUrl = $value"
        }
    }
    Set-Content $cfg $lines -Encoding UTF8
}

if ($Reset) {
    Set-ManifestUrl ''
    Write-Host '已清空 ManifestUrl，恢复官方地址。' -ForegroundColor Green
    return
}

$csproj = Get-Content (Join-Path $root 'src\TbhCombatTracker\TbhCombatTracker.csproj') -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { Write-Error 'csproj 里没有 <Version>' }
$cur = [version]$Matches[1]
$next = '{0}.{1}.{2}' -f $cur.Major, $cur.Minor, ($cur.Build + 1)

$latest = [ordered]@{
    version     = $next
    gameVersion = ''
    url         = 'https://github.com/DPS-Love/tbh-combat-tracker/releases'
    download    = ''
    sha256      = ''
    critical    = ($Scenario -eq 'critical')
    notes       = [ordered]@{
        zh = "测试清单（$Scenario）——这不是真的更新"
        en = "Test manifest ($Scenario) - not a real update"
    }
}
$broken = @()

switch ($Scenario) {
    'install' {
        $zip = Get-ChildItem (Join-Path $root 'build\release') -Filter 'TbhCombatTracker-v*.zip' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $zip) { Write-Error '没有 zip：先跑 pwsh tools/package-release.ps1' }
        $latest.download = $zip.FullName
        $latest.sha256 = (Get-FileHash $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    'broken' {
        $latest.version = $cur.ToString()   # 没有新版本，只有点名
        $broken = @([ordered]@{
            modMax = $cur.ToString()
            reason = [ordered]@{
                zh = '测试：当前版本被清单点名——这不是真的'
                en = 'Test: this version is flagged by the manifest - not real'
            }
        })
    }
}

$manifest = [ordered]@{ schema = 1; latest = $latest; broken = $broken }
$out = Join-Path $root 'build\test-manifest.json'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
($manifest | ConvertTo-Json -Depth 8) + "`n" | Set-Content $out -Encoding UTF8 -NoNewline

Write-Host "已生成 $out（场景：$Scenario）" -ForegroundColor Green
if ($Apply) {
    Set-ManifestUrl $out
    Write-Host "已写入 cfg：ManifestUrl = $out" -ForegroundColor Green
}
else {
    Write-Host "把下面这行写进 $cfg 的 [Update] 段（或加 -Apply 让脚本写）："
    Write-Host "  ManifestUrl = $out"
}
Write-Host '启动游戏、按 F9 看横幅；日志里会有一行「更新清单来源：…」。看完 pwsh tools/test-manifest.ps1 -Reset 恢复。' -ForegroundColor DarkGray
