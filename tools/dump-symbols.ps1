<#
.SYNOPSIS
    重新 dump 游戏符号。游戏更新后跑这个，然后对照 docs/symbols.md 校正混淆名。

.EXAMPLE
    pwsh tools/dump-symbols.ps1
    pwsh tools/dump-symbols.ps1 -GameDir "E:\Steam\steamapps\common\TaskbarHero"
#>
param(
    [string]$GameDir = "D:\Steam\steamapps\common\TaskbarHero",
    [string]$OutDir = "$PSScriptRoot\..\build\dump"
)

$ErrorActionPreference = 'Stop'

$dumper = Join-Path $PSScriptRoot 'Il2CppDumper\Il2CppDumper.exe'
if (-not (Test-Path $dumper)) {
    Write-Error @"
找不到 $dumper
去 https://github.com/Perfare/Il2CppDumper/releases 下载 Il2CppDumper-win-<版本>.zip
（不带 -netX 的那个是自包含版，不需要装 .NET 运行时），解压到 tools\Il2CppDumper\。
本机走公司网络时记得挂 Clash 代理：`$env:HTTPS_PROXY='http://127.0.0.1:7897'
"@
}

$asm = Join-Path $GameDir 'GameAssembly.dll'
$meta = Join-Path $GameDir 'TaskBarHero_Data\il2cpp_data\Metadata\global-metadata.dat'
foreach ($f in @($asm, $meta)) {
    if (-not (Test-Path $f)) { Write-Error "找不到 $f —— 检查 -GameDir 是否正确。" }
}

$version = Get-Content (Join-Path $GameDir 'Version.txt') -Raw -ErrorAction SilentlyContinue
Write-Host "游戏版本: $($version.Trim())" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Il2CppDumper 结束时会 ReadKey，在非交互终端里必然抛异常。
# 这是它的已知行为，产物在那之前就已经全部写完了，所以只看文件不看退出码。
& $dumper $asm $meta $OutDir 2>&1 | ForEach-Object { Write-Host "  $_" }

$dumpCs = Join-Path $OutDir 'dump.cs'
if (-not (Test-Path $dumpCs)) { Write-Error "dump 失败，没有生成 dump.cs。" }

Write-Host "`ndump 完成 -> $OutDir" -ForegroundColor Green
Get-ChildItem $OutDir | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize

Write-Host "核心类型现状：" -ForegroundColor Cyan
python (Join-Path $PSScriptRoot 'extract-types.py') $dumpCs --names-only Unit Hero Monster DamageInfo pj ph pf bfc

Write-Host @"

下一步：
  1. 上面若有类型缺失，说明混淆名变了。用识别特征重新定位：
       python tools/extract-types.py "$dumpCs" --grep Damage --names-only
       Select-String -Path "$dumpCs" -Pattern 'class \w+ : Unit'
  2. 校正 docs/symbols.md
  3. 改 src/TbhCombatTracker/Patches.cs 顶部的 using 别名和方法名常量
"@ -ForegroundColor DarkGray
