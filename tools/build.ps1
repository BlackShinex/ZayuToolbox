# 构建脚本（无需安装 .NET SDK，使用 Windows 自带的 .NET Framework 4.8 + MSBuild）
#
# 用法：
#   .\tools\build.ps1              # Debug 构建
#   .\tools\build.ps1 -Release     # Release 构建
#   .\tools\build.ps1 -Release -Package  # Release 构建并生成单文件 exe 到 dist\
param(
    [switch]$Release,
    [switch]$Package,
    [switch]$Clean,
    [switch]$Tests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
$config = if ($Release) { "Release" } else { "Debug" }
$version = "1.8"

if (-not (Test-Path $msbuild)) {
    throw "找不到 MSBuild：$msbuild"
}

$projects = @(
    (Join-Path $root "src\Zayu.App\Zayu.App.csproj")
)
if ($Tests) {
    $projects += (Join-Path $root "tests\Zayu.CoreTests\Zayu.CoreTests.csproj")
}

$commonArgs = @(
    "/nologo",
    "/v:minimal",
    "/p:Configuration=$config",
    "/p:Platform=AnyCPU"
)
if ($Clean) { $commonArgs += "/t:Rebuild" }

foreach ($project in $projects) {
    Write-Host ""
    Write-Host ">>> 构建 $project" -ForegroundColor Cyan
    & $msbuild $project @commonArgs
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败：$project（退出码 $LASTEXITCODE）"
    }
}

$appDir = Join-Path $root "src\Zayu.App\bin\$config"
Write-Host ""
Write-Host "构建完成：$appDir" -ForegroundColor Green

if ($Package) {
    $dist = Join-Path $root "dist"
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $exePath = Join-Path $appDir "杂鱼串流工具箱.exe"
    Copy-Item $exePath $dist
    Write-Host "已生成单文件：$(Join-Path $dist '杂鱼串流工具箱.exe')" -ForegroundColor Green
}