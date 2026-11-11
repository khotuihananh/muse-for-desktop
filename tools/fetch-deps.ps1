# Fetches the two third-party payloads that are not stored in this repo:
#   1. tools\ffmpeg.exe                  (gdigrab + libx264 build, ffmpeg-static b6.1.1)
#   2. MicrosoftEdgeWebView2Setup.exe    (WebView2 runtime bootstrapper, Microsoft fwlink)
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\fetch-deps.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$ffmpegGz = Join-Path $env:TEMP "ffmpeg-win32-x64.gz"
$ffmpegOut = Join-Path $root "tools\ffmpeg.exe"
if (-not (Test-Path $ffmpegOut)) {
    Write-Host "Downloading ffmpeg (ffmpeg-static b6.1.1)..."
    Invoke-WebRequest "https://github.com/eugeneware/ffmpeg-static/releases/download/b6.1.1/ffmpeg-win32-x64.gz" -OutFile $ffmpegGz
    Write-Host "Decompressing..."
    $in = [System.IO.File]::OpenRead($ffmpegGz)
    $gz = New-Object System.IO.Compression.GzipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
    $out = [System.IO.File]::Create($ffmpegOut)
    $gz.CopyTo($out)
    $out.Close(); $gz.Close(); $in.Close()
    Remove-Item $ffmpegGz
}
Write-Host "ffmpeg.exe: $ffmpegOut"

$wv2Out = Join-Path $root "MicrosoftEdgeWebView2Setup.exe"
if (-not (Test-Path $wv2Out)) {
    Write-Host "Downloading WebView2 bootstrapper..."
    Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $wv2Out
}
Write-Host "WebView2 bootstrapper: $wv2Out"
Write-Host "Done. You can now publish + stage + run makensis (see README)."
