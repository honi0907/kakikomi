# Download ffmpeg into third_party/ffmpeg/ffmpeg.exe when missing.
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$destDir = Join-Path $Root "third_party\ffmpeg"
$destExe = Join-Path $destDir "ffmpeg.exe"

if (Test-Path $destExe) {
    Write-Host "ffmpeg already present: $destExe"
    exit 0
}

New-Item -ItemType Directory -Force -Path $destDir | Out-Null

# GitHub releases are more reliable than gyan.dev direct links
$candidates = @(
    "https://github.com/GyanD/codexffmpeg/releases/download/8.0.1/ffmpeg-8.0.1-essentials_build.zip",
    "https://github.com/GyanD/codexffmpeg/releases/download/7.1.1/ffmpeg-7.1.1-essentials_build.zip",
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip"
)

$zipPath = Join-Path $env:TEMP ("kakikomi-ffmpeg-{0}.zip" -f [guid]::NewGuid().ToString("N"))
$extractDir = Join-Path $env:TEMP ("kakikomi-ffmpeg-{0}" -f [guid]::NewGuid().ToString("N"))
$downloaded = $false

try {
    foreach ($zipUrl in $candidates) {
        try {
            Write-Host "Downloading ffmpeg..."
            Write-Host "  $zipUrl"
            & curl.exe -L --fail --retry 3 --connect-timeout 30 --max-time 600 -o $zipPath $zipUrl
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $zipPath) -or (Get-Item $zipPath).Length -lt 1MB) {
                throw "download failed (exit=$LASTEXITCODE)"
            }
            $downloaded = $true
            break
        }
        catch {
            Write-Host "  failed: $($_.Exception.Message)"
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not $downloaded) {
        throw "All ffmpeg download mirrors failed"
    }

    Write-Host "Extracting..."
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

    $found = Get-ChildItem -Path $extractDir -Recurse -Filter "ffmpeg.exe" |
        Where-Object { $_.DirectoryName -match '[\\/]bin$' } |
        Select-Object -First 1

    if (-not $found) {
        $found = Get-ChildItem -Path $extractDir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    }
    if (-not $found) {
        throw "ffmpeg.exe not found in downloaded archive"
    }

    Copy-Item -Path $found.FullName -Destination $destExe -Force
    Write-Host "OK: $destExe ($([math]::Round((Get-Item $destExe).Length / 1MB, 1)) MB)"
}
finally {
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
}
