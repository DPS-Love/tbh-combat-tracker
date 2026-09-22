<#
.SYNOPSIS
    打一个可以直接发给别人的发布包。

.DESCRIPTION
    产出 build/release/TbhCombatTracker-vX.Y.Z.zip，结构是：

        BepInEx/plugins/TbhCombatTracker.dll
        安装说明.md
        Install Guide.md
        LICENSE

    收包的人装好 BepInEx 之后，把这个 zip 解压覆盖到游戏根目录就完事——
    目录层级是对齐的，不需要他们理解 plugins 该放哪。

    **不打包 BepInEx 本体**：它是 LGPL 的独立项目，版本更新频繁，
    让用户自己去官方构建站拿更稳妥，也免得我们变成它的分发方。

    -Upload 会把包传到同名标签的 GitHub Release 上并发布它。
    Release 标题固定为标签名 vX.Y.Z，说明取自标签注释（轻量标签则取提交信息）。
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
    [switch]$Upload,
    # 下面几个只在 -Upload 时用：写进 manifest.json，装了旧版的玩家会在面板上看到
    [switch]$Critical,
    [string]$NotesZh = '',
    [string]$NotesEn = '',
    [string]$GameDir = 'D:\Steam\steamapps\common\TaskbarHero'
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

# README 就是面向玩家的安装说明；包里用中文名，收包的人一眼知道先看哪个。英文版一并带上
Copy-Item (Join-Path $root 'README.md') (Join-Path $stage '安装说明.md') -Force
Copy-Item (Join-Path $root 'README.en.md') (Join-Path $stage 'Install Guide.md') -Force
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
        # 标题就是标签名，说明取自标签注释（轻量标签则取提交信息）——和 CI 的 release.yml 一致
        gh release create $tag --draft --title $tag --notes-from-tag
        if ($LASTEXITCODE -ne 0) { Write-Error "建 Release $tag 失败。标签推上去了吗？" }
    }

    gh release upload $tag $zip --clobber
    if ($LASTEXITCODE -ne 0) { Write-Error '上传失败。' }

    gh release edit $tag --draft=false
    if ($LASTEXITCODE -ne 0) { Write-Error '产物传上去了，但取消草稿状态失败，去网页上点一下发布。' }

    Write-Host "已发布：https://github.com/DPS-Love/tbh-combat-tracker/releases/tag/$tag" -ForegroundColor Green

    # ---- 更新 manifest.json：这是装了旧版的玩家收到通知的唯一渠道 ----
    # broken 列表是手工维护的（很少改、要慎重），这里只覆盖 latest。
    Write-Host "`n写 manifest.json …" -ForegroundColor Cyan
    $manifestPath = Join-Path $root 'manifest.json'
    $existing = if (Test-Path $manifestPath) { Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $broken = @()
    if ($existing -and $existing.broken) { $broken = @($existing.broken) }

    $gameVer = ''
    $verFile = Join-Path $GameDir 'Version.txt'
    if (Test-Path $verFile) { $gameVer = (Get-Content $verFile -Raw).Trim() }
    else { Write-Warning "读不到 $verFile，manifest 里的 gameVersion 会是空的。" }

    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        schema = 1
        latest = [ordered]@{
            version     = $version
            gameVersion = $gameVer
            url         = "https://github.com/DPS-Love/tbh-combat-tracker/releases/tag/$tag"
            download    = "https://github.com/DPS-Love/tbh-combat-tracker/releases/download/$tag/$(Split-Path $zip -Leaf)"
            sha256      = $sha
            critical    = [bool]$Critical
            notes       = [ordered]@{ zh = $NotesZh; en = $NotesEn }
        }
        broken = $broken
    }
    # ConvertTo-Json 会把空数组写成 null 以外的东西吗？不会，@() -> []；但要给够 Depth
    ($manifest | ConvertTo-Json -Depth 8) + "`n" | Set-Content $manifestPath -Encoding UTF8 -NoNewline

    git -C $root add manifest.json
    git -C $root commit -q -m "manifest: v$version" 2>$null
    git -C $root -c http.proxy=http://127.0.0.1:7897 -c https.proxy=http://127.0.0.1:7897 push origin main
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "manifest.json 已提交但推送失败。手动重试：git push origin main"
    }
    else {
        # jsDelivr 对分支引用缓存 12 小时，主动清一下，玩家能立刻看到
        try {
            Invoke-WebRequest -UseBasicParsing -TimeoutSec 15 `
                'https://purge.jsdelivr.net/gh/DPS-Love/tbh-combat-tracker@main/manifest.json' | Out-Null
            Write-Host 'manifest 已推送，jsDelivr 缓存已清。' -ForegroundColor Green
        }
        catch { Write-Warning "jsDelivr 缓存清理失败（最多 12 小时后自动刷新）：$($_.Exception.Message)" }
    }
    return
}
Write-Host @"

包内结构：
  BepInEx/plugins/TbhCombatTracker.dll
  安装说明.md
  Install Guide.md
  反作弊说明.md
  LICENSE

发布前自查：
  1. 在**干净的游戏目录**上按 安装说明.md 走一遍，确认从零能装上
  2. 说清楚测试过的游戏版本和 BepInEx 构建号
  3. 不要把 build/dump/ 的符号 dump 或 build/downloads/ 的 BepInEx 包一起发出去
"@ -ForegroundColor DarkGray
