param(
    [switch]$DryRun,
    [switch]$SkipGitHub
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== Build portable =="
& (Join-Path $root "scripts\Build-Portable.ps1")
if ($LASTEXITCODE -ne 0) { throw "Build-Portable failed" }

Write-Host "== Build installer =="
& (Join-Path $root "installer\build-installer.ps1")
if ($LASTEXITCODE -ne 0) { throw "build-installer failed" }

$csproj = Join-Path $root "Kakikomi.csproj"
[xml]$xml = Get-Content $csproj
$version = (@($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1).Trim()
$tag = "v$version"
$dist = Join-Path $root "dist"
$setup = Join-Path $dist "Kakikomi-$version-x64-Setup.exe"
$portable = Join-Path $dist "Kakikomi-$version-x64-portable.zip"

if (-not (Test-Path $setup)) { throw "Missing setup: $setup" }
if (-not (Test-Path $portable)) { throw "Missing portable: $portable" }

Write-Host ""
Write-Host "Artifacts v$version"
Write-Host "  $setup"
Write-Host "  $portable"

if ($DryRun -or $SkipGitHub) {
    Write-Host "DryRun/SkipGitHub: GitHub release not executed."
    exit 0
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "gh CLI not found"
}

$remote = git remote get-url origin 2>$null
if (-not $remote) {
    throw "git remote 'origin' is not set"
}

Write-Host "== GitHub Release $tag =="
$existing = $null
try {
    $existing = gh release view $tag 2>$null
} catch {
    $existing = $null
}
if ($LASTEXITCODE -eq 0 -and $existing) {
    throw "Release $tag already exists. Bump version or delete the release first."
}

gh release create $tag `
    $setup `
    $portable `
    --title "Kakikomi v$version" `
    --notes @"
## Kakikomi v$version

- Setup インストーラー（オンライン更新用）
- ポータブル ZIP

インストール後、設定の「オンライン更新を確認」から更新できます。
"@

Write-Host "Release created: $tag"
exit 0
