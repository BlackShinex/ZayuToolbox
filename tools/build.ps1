# 构建脚本（无需安装 .NET SDK，使用 Windows 自带的 .NET Framework 4.8 + MSBuild）
#
# 用法：
#   .\tools\build.ps1              # Debug 构建
#   .\tools\build.ps1 -Release     # Release 构建
#   .\tools\build.ps1 -Release -Package  # Release 构建并打包到 dist\
#   .\tools\build.ps1 -Release -Package -Zip  # 再额外生成 dist\ZayuToolbox-1.7-win-x64.zip
param(
    [switch]$Release,
    [switch]$Package,
    [switch]$Zip,
    [switch]$Clean,
    [switch]$Tests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
$config = if ($Release) { "Release" } else { "Debug" }
$version = "1.7"

if (-not (Test-Path $msbuild)) {
    throw "找不到 MSBuild：$msbuild"
}

$projects = @(
    (Join-Path $root "src\IpInfo.App\IpInfo.App.csproj")
)
if ($Tests) {
    $projects += (Join-Path $root "tests\IpInfo.CoreTests\IpInfo.CoreTests.csproj")
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

$appDir = Join-Path $root "src\IpInfo.App\bin\$config"
Write-Host ""
Write-Host "构建完成：$appDir" -ForegroundColor Green

if ($Package) {
    $dist = Join-Path $root "dist"
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    Copy-Item (Join-Path $appDir "杂鱼串流工具箱.exe") $dist
    Copy-Item (Join-Path $appDir "杂鱼串流工具箱.exe.config") $dist -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "README.md") $dist -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "LICENSE") $dist -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "CHANGELOG.md") $dist -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "GITHUB_PUBLISH.md") $dist -ErrorAction SilentlyContinue
    $docs = Join-Path $root "docs"
    if (Test-Path $docs) {
        Copy-Item $docs (Join-Path $dist "docs") -Recurse
    }
    Write-Host "已打包到：$dist" -ForegroundColor Green

    if ($Zip) {
        $staging = Join-Path $env:TEMP ("ZayuToolbox-zip-" + [Guid]::NewGuid().ToString("N"))
        $packageRoot = Join-Path $staging ("杂鱼串流工具箱-" + $version)
        try {
            New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
            Copy-Item (Join-Path $dist "*") $packageRoot -Recurse
            $zipPath = Join-Path $dist ("ZayuToolbox-" + $version + "-win-x64.zip")
            if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
            Compress-Archive -Path $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
            Write-Host "已生成压缩包：$zipPath" -ForegroundColor Green
        }
        finally {
            if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        }
    }
}
