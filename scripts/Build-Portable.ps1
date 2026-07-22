# Kakikomi portable build (double-click Kakikomi.exe)

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Stop-Process -Name Kakikomi -Force -ErrorAction SilentlyContinue

$csproj = Join-Path $root "Kakikomi.csproj"
[xml]$xml = Get-Content $csproj
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }

$dist = Join-Path $root "dist"
$publish = Join-Path $dist "Kakikomi"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Get-ChildItem $dist -Filter "Kakikomi-*-portable.zip" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $dist -Filter "Kakikomi-*-x64-Setup.exe" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $dist -Filter "Kakikomi-Setup-*" -ErrorAction SilentlyContinue | Remove-Item -Force

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publish | Out-Null

$msbuild = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($msbuild) {
    Write-Host "Using MSBuild: $msbuild"
    & $msbuild $csproj /t:Restore,Publish /p:Configuration=$Configuration /p:Platform=x64 /p:RuntimeIdentifier=$Runtime /p:PublishSingleFile=false /p:WindowsAppSDKSelfContained=true /p:WindowsPackageType=None /p:PublishDir="$publish\\" /v:m
} else {
    dotnet publish $csproj `
        -c $Configuration `
        -r $Runtime `
        -p:Platform=x64 `
        -p:PublishSingleFile=false `
        -p:WindowsAppSDKSelfContained=true `
        -p:WindowsPackageType=None `
        -o $publish
}

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $publish "Kakikomi.exe"
if (-not (Test-Path $exe)) { throw "Missing exe: $exe" }

# .mov → mp4 変換用 ffmpeg（元 mov は残す）
& (Join-Path $root "scripts\Ensure-Ffmpeg.ps1") -Root $root
$ffmpegSrc = Join-Path $root "third_party\ffmpeg\ffmpeg.exe"
if (-not (Test-Path $ffmpegSrc)) { throw "Missing ffmpeg: $ffmpegSrc" }
$ffmpegOut = Join-Path $publish "ffmpeg"
New-Item -ItemType Directory -Force -Path $ffmpegOut | Out-Null
Copy-Item -Path $ffmpegSrc -Destination (Join-Path $ffmpegOut "ffmpeg.exe") -Force

# 書き込み PNG 用フォルダ（設定からも参照）
New-Item -ItemType Directory -Force -Path (Join-Path $publish "save") | Out-Null

$zipName = "Kakikomi-$version-x64-portable.zip"
$zipPath = Join-Path $dist $zipName
if (Test-Path $zipPath) {
    try { Remove-Item $zipPath -Force -ErrorAction Stop }
    catch {
        $zipPath = Join-Path $dist ("Kakikomi-$version-x64-portable-{0:yyyyMMdd-HHmmss}.zip" -f (Get-Date))
    }
}

Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "OK: double-click to run"
Write-Host "  $exe"
Write-Host "ZIP: $zipPath"
