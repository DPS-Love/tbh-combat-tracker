<#
.SYNOPSIS
    把 BepInEx 6 (IL2CPP, x64) 安装到 Task Bar Hero 目录。

.DESCRIPTION
    BepInEx 6 至今没有正式版，IL2CPP 支持只在 bleeding-edge 构建里。
    GitHub Release 上最新带 tag 的 IL2CPP 包是 6.0.0-pre.2（2024-08），
    比 Unity 6 正式发布还早，肯定啃不动本游戏的 metadata v31。
    所以这里用 builds.bepinex.dev 的 BE 构建。

    参考实现 ElPinguinoXD/TBH_ModMenu 就是用 BepInEx 6 IL2CPP 跑通这个游戏的。

.NOTES
    BepInEx IL2CPP 走 winhttp.dll 代理注入（MelonLoader 用的是 version.dll）。
    Steam"验证游戏文件完整性"会把 winhttp.dll 判为多余文件删掉，删了重跑本脚本即可。

.EXAMPLE
    pwsh tools/install-bepinex.ps1
    pwsh tools/install-bepinex.ps1 -Zip build\downloads\bepinex.zip
    pwsh tools/install-bepinex.ps1 -Uninstall
#>
param(
    [string]$GameDir = "D:\Steam\steamapps\common\TaskbarHero",
    [string]$Zip,
    [string]$Build = "785",
    [string]$Proxy = "http://127.0.0.1:7897",
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $GameDir 'TaskBarHero.exe'))) {
    Write-Error "找不到游戏：$GameDir"
}

# 注入型加载器不能在游戏运行时装/卸——文件被占用，而且会把游戏搞崩。
$proc = Get-Process -Name 'TaskBarHero' -ErrorAction SilentlyContinue
if ($proc) {
    Write-Error "游戏正在运行（PID $($proc.Id -join ', ')），请先退出游戏再执行。"
}

# BepInEx 解压后铺在游戏根目录的东西
$targets = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'dotnet', 'BepInEx', 'changelog.txt')

if ($Uninstall) {
    foreach ($n in $targets) {
        $p = Join-Path $GameDir $n
        if (Test-Path $p) {
            # BepInEx\plugins 和 config 是你自己的东西，别一起删了
            if ($n -eq 'BepInEx') {
                Get-ChildItem $p -Force | Where-Object { $_.Name -notin @('plugins', 'config') } |
                    Remove-Item -Recurse -Force
                Write-Host "已清空 BepInEx\（保留 plugins\ 和 config\）" -ForegroundColor Yellow
            }
            else {
                Remove-Item $p -Recurse -Force
                Write-Host "已删除 $n" -ForegroundColor Yellow
            }
        }
    }
    Write-Host "`nBepInEx 已卸载，游戏恢复原样。" -ForegroundColor Green
    Write-Host "要更彻底可以在 Steam 里'验证游戏文件完整性'。" -ForegroundColor DarkGray
    return
}

if (-not $Zip) {
    $Zip = Join-Path $PSScriptRoot "..\build\downloads\BepInEx-Unity.IL2CPP-win-x64-be.$Build.zip"
}

if (-not (Test-Path $Zip)) {
    Write-Host "本地没有安装包，尝试下载 BE 构建 #$Build …" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path (Split-Path $Zip) | Out-Null

    # BE 构建的文件名带 commit hash，先抓列表页解析出准确的链接
    $listUrl = "https://builds.bepinex.dev/projects/bepinex_be"
    try {
        $html = Invoke-WebRequest -Uri $listUrl -Proxy $Proxy -UseBasicParsing -TimeoutSec 30
    }
    catch {
        Write-Error @"
拿不到 BepInEx 构建列表：$($_.Exception.Message)
本机走公司网络时需要 Clash 代理（默认 $Proxy）。
只试一次就放弃是故意的——手动重试一下通常就好了：
    pwsh tools/install-bepinex.ps1 -Proxy $Proxy
或者自己去 $listUrl 下载 IL2CPP-win-x64 包，再用 -Zip 指过来。
"@
    }

    $href = ([regex]::Matches($html.Content, 'href="([^"]*bepinex_be/' + $Build + '/[^"]*IL2CPP-win-x64[^"]*\.zip)"') |
        Select-Object -First 1).Groups[1].Value
    if (-not $href) { Write-Error "在构建列表里找不到 #$Build 的 IL2CPP-win-x64 包。" }

    $url = "https://builds.bepinex.dev$href"
    Write-Host "  $url" -ForegroundColor DarkGray
    Invoke-WebRequest -Uri $url -OutFile $Zip -Proxy $Proxy -UseBasicParsing -TimeoutSec 300
}

Write-Host "安装包: $Zip ($([math]::Round((Get-Item $Zip).Length / 1MB, 1)) MB)" -ForegroundColor Cyan

# 已存在就先备份，方便出问题时回滚（plugins / config 不动）
$existing = $targets | Where-Object { $_ -ne 'BepInEx' -and (Test-Path (Join-Path $GameDir $_)) }
if ($existing) {
    $backup = Join-Path $GameDir ("BepInEx.backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    foreach ($n in $existing) { Move-Item (Join-Path $GameDir $n) (Join-Path $backup $n) -Force }
    Write-Host "已把旧版本移到 $backup" -ForegroundColor Yellow
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("bie-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    Expand-Archive -Path $Zip -DestinationPath $staging -Force
    Get-ChildItem $staging | ForEach-Object {
        Copy-Item $_.FullName -Destination $GameDir -Recurse -Force
        Write-Host "已安装 $($_.Name)" -ForegroundColor Green
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir 'BepInEx\plugins') | Out-Null
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "`n安装完成。" -ForegroundColor Green
Write-Host @"

下一步：启动游戏。首次启动 BepInEx 会用 Cpp2IL 反编译 GameAssembly.dll，
再用 Il2CppInterop 生成 C# 可引用的代理程序集，要 1-3 分钟，
期间游戏窗口可能看着像卡住，别急着关。

完成的标志：
    $GameDir\BepInEx\interop\Assembly-CSharp.dll

日志在 $GameDir\BepInEx\LogOutput.log
"@ -ForegroundColor DarkGray
