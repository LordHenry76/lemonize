# Downloads the programs bundled with Lemonize into third_party\ (they are too large for git):
#   ffmpeg\ffmpeg.exe, ffmpeg\ffprobe.exe   FFmpeg essentials build (gyan.dev)
#   ghostscript\bin\gswin64c.exe + gsdll64.dll   Ghostscript (Artifex, official GitHub release)
#   pandoc\pandoc.exe                       Pandoc (official GitHub release)
# Needs 7-Zip to unpack the Ghostscript installer without running it.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$GhostscriptTag = 'gs10080'          # Ghostscript 10.08.0
$PandocVersion = '3.12.1'

$root = Split-Path $PSScriptRoot -Parent
$third = Join-Path $root 'third_party'
$work = Join-Path ([IO.Path]::GetTempPath()) ("lemonize-third-party-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

try {
    # FFmpeg
    Write-Host 'FFmpeg…'
    $zip = Join-Path $work 'ffmpeg.zip'
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zip
    Expand-Archive $zip (Join-Path $work 'ffmpeg') -Force
    $bin = Get-ChildItem (Join-Path $work 'ffmpeg') -Recurse -Directory -Filter bin | Select-Object -First 1
    $dest = New-Item -ItemType Directory -Force (Join-Path $third 'ffmpeg')
    Copy-Item (Join-Path $bin.FullName 'ffmpeg.exe'), (Join-Path $bin.FullName 'ffprobe.exe') $dest -Force
    $license = Get-ChildItem (Join-Path $work 'ffmpeg') -Recurse -File -Filter 'LICENSE*' | Select-Object -First 1
    if ($license) { Copy-Item $license.FullName (Join-Path $dest 'LICENSE.txt') -Force }

    # Ghostscript: the official installer is NSIS; 7-Zip extracts it without installing.
    Write-Host 'Ghostscript…'
    $sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $sevenZip) { throw '7-Zip not found (winget install 7zip.7zip)' }
    $gsExe = Join-Path $work 'gs.exe'
    Invoke-WebRequest "https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/$GhostscriptTag/${GhostscriptTag}w64.exe" -OutFile $gsExe
    & $sevenZip x -y "-o$(Join-Path $work 'gs')" $gsExe | Out-Null
    $dest = New-Item -ItemType Directory -Force (Join-Path $third 'ghostscript\bin')
    Copy-Item (Join-Path $work 'gs\bin\gswin64c.exe'), (Join-Path $work 'gs\bin\gsdll64.dll') $dest -Force
    Copy-Item (Join-Path $work 'gs\doc\COPYING') (Join-Path $third 'ghostscript\COPYING') -Force

    # Pandoc
    Write-Host 'Pandoc…'
    $zip = Join-Path $work 'pandoc.zip'
    Invoke-WebRequest "https://github.com/jgm/pandoc/releases/download/$PandocVersion/pandoc-$PandocVersion-windows-x86_64.zip" -OutFile $zip
    Expand-Archive $zip (Join-Path $work 'pandoc') -Force
    $dest = New-Item -ItemType Directory -Force (Join-Path $third 'pandoc')
    $folder = Join-Path $work "pandoc\pandoc-$PandocVersion"
    Copy-Item (Join-Path $folder 'pandoc.exe'), (Join-Path $folder 'COPYRIGHT.txt') $dest -Force

    Write-Host 'Done.'
    Get-ChildItem $third -Recurse -File | Select-Object @{ n = 'File'; e = { $_.FullName.Substring($third.Length + 1) } }, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
