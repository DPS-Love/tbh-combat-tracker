<#
.SYNOPSIS
    打一个可以直接发给别人的发布包。

.DESCRIPTION
    产出 build/release/TbhCombatTracker-vX.Y.Z.zip，结构是：

        BepInEx/plugins/TbhCombatTracker.dll
        安装说明.md
        LICENSE

    收包的人装好 BepInEx 之后，把这个 zip 解压覆盖到游戏根目录就完事——
    目录层级是对齐的，不需要他们理解 plugins 该放哪。

    **不打包 BepInEx 本体**：它是 LGPL 的独立项目，版本更新频繁，
    让用户自己去官方构建站拿更稳妥，也免得我们变成它的分发方。

    -Upload 会把包传到同名标签的 GitHub Release 上并发布它。
    CI 编译不了这个项目（要引用从游戏本体生成的 Il2CppInterop 程序集），
    所以默认流程是：打标签 -> CI 建草稿 Release -> 在装了游戏的机器上跑这个脚本补产物。

.EXAMPLE
    pwsh tools/package-release.ps1
    pwsh tools/package-release.ps1 -Configuration Debug
    pwsh tools/package-release.ps1 -Upload
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\TbhCombatTracker\TbhCombatTracker.csproj'
$dll = Join-Path $root "src\TbhCombatTracker\bin\$Configuration\TbhCombatTracker.dll"

# 机器上可能同时装着"只有运行时的 dotnet"和"带 SDK 的 dotnet"，
# 而 PATH 上先出现的未必是带 SDK 的那个。优先用 DOTNET_ROOT。
function Resolve-Dotnet {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $onPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ($onPath) { $candidates += $onPath }

    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        $sdks = & $c --list-sdks 2>$null
        if ($sdks) { return $c }
    }
    Write-Error '找不到带 SDK 的 dotnet。装一个 .NET SDK，或把 DOTNET_ROOT 指过去。'
}

if (-not $SkipBuild) {
    $dotnet = Resolve-Dotnet
    Write-Host "构建中…（$dotnet）" -ForegroundColor Cyan
    # 部署到游戏目录那一步失败无所谓（游戏可能开着），打包只要 bin 里的产物
    & $dotnet build $proj -c $Configuration --nologo | Where-Object { $_ -match 'error|已成功|Build succeeded' }
    if ($LASTEXITCODE -ne 0) { Write-Error '构建失败，包没打。' }
}

if (-not (Test-Path $dll)) { Write-Error "找不到产物 $dll，先构建。" }

# 版本号以程序集为准，避免 csproj 改了忘记同步
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
if (-not $version) { $version = '0.0.0' }
$version = ($version -split '\+')[0]
# FileVersion 是四段的，去掉尾巴那个 .0，文件名好看些
if ($version -match '^(\d+\.\d+\.\d+)\.0$') { $version = $Matches[1] }

$stage = Join-Path $root "build\release\stage"
$outDir = Join-Path $root 'build\release'
$zip = Join-Path $outDir "TbhCombatTracker-v$version.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'BepInEx\plugins') | Out-Null
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Copy-Item $dll (Join-Path $stage 'BepInEx\plugins\') -Force

# 安装说明用中文名，收包的人一眼知道先看哪个
Copy-Item (Join-Path $root 'docs\INSTALL.md') (Join-Path $stage '安装说明.md') -Force
Copy-Item (Join-Path $root 'docs\anticheat.md') (Join-Path $stage '反作弊说明.md') -Force

$license = Join-Path $root 'LICENSE'
if (Test-Path $license) { Copy-Item $license $stage -Force }
else { Write-Warning '仓库里没有 LICENSE，发布包里也就没有。公开分享前建议补一个。' }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host "`n发布包已生成：" -ForegroundColor Green
Write-Host "  $zip  ($size KB)"

if ($Upload) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Error '要 -Upload 得先装 GitHub CLI：https://cli.github.com/'
    }
    $tag = "v$version"
    Write-Host "`n上传到 Release $tag …" -ForegroundColor Cyan

    # 标签还没有对应的 Release 就现建一个（正常流程里 CI 已经建好草稿了）
    gh release view $tag *> $null
    if ($LASTEXITCODE -ne 0) {
        gh release create $tag --draft --generate-notes --title "TBH Combat Tracker $tag"
        if ($LASTEXITCODE -ne 0) { Write-Error "建 Release $tag 失败。标签推上去了吗？" }
    }

    gh release upload $tag $zip --clobber
    if ($LASTEXITCODE -ne 0) { Write-Error '上传失败。' }

    gh release edit $tag --draft=false
    if ($LASTEXITCODE -ne 0) { Write-Error '产物传上去了，但取消草稿状态失败，去网页上点一下发布。' }

    Write-Host "已发布：https://github.com/DPS-Love/tbh-combat-tracker/releases/tag/$tag" -ForegroundColor Green
    return
}
Write-Host @"

包内结构：
  BepInEx/plugins/TbhCombatTracker.dll
  安装说明.md
  反作弊说明.md
  LICENSE

发布前自查：
  1. 在**干净的游戏目录**上按 安装说明.md 走一遍，确认从零能装上
  2. 说清楚测试过的游戏版本和 BepInEx 构建号
  3. 不要把 build/dump/ 的符号 dump 或 build/downloads/ 的 BepInEx 包一起发出去
"@ -ForegroundColor DarkGray
