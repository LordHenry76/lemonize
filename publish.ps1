# Builds the Lemonize installer: dist\Lemonize-Setup-<version>.exe
#   1. dotnet publish (self-contained, no .NET install needed) into publish\app,
#      together with the bundled ffmpeg, ghostscript and pandoc folders;
#   2. Inno Setup compiles installer\Lemonize.iss.
# Requires Inno Setup 6 (winget install JRSoftware.InnoSetup --scope user).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'publish\app'

[xml]$project = Get-Content (Join-Path $root 'src\Lemonize\Lemonize.csproj')
$version = ($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
# Not single-file: an installed app starts faster and doesn't unpack native libraries to %TEMP% on each run.
dotnet publish (Join-Path $root 'src\Lemonize') -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found: winget install JRSoftware.InnoSetup --scope user" }

& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\Lemonize.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Get-ChildItem (Join-Path $root 'dist') -Filter "Lemonize-Setup-$version.exe" |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
