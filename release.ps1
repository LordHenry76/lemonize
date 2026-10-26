# Publishes a GitHub release: tag v<version> + dist\Lemonize-Setup-<version>.exe.
#   .\release.ps1                 version from src\Lemonize\Lemonize.csproj
#   .\release.ps1 -Version 1.0.5  an earlier version (its installer must still be in dist\)
# The release goes on the commit whose subject ends with "(<version>)", which must already be on origin/main.
# Its message body (without Co-Authored-By) becomes the release notes. Requires the GitHub CLI (gh).
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'src\Lemonize\Lemonize.csproj')
    $Version = ($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version
}
$tag = "v$Version"

gh release view $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { Write-Host "La release $tag esiste già."; exit 0 }

git -C $root fetch origin --quiet
$commit = git -C $root log origin/main --format=%H --fixed-strings --grep="($Version)" -n 1
if (-not $commit) { throw "Nessun commit '($Version)' su origin/main: fai prima commit e push." }

$installer = Join-Path $root "dist\Lemonize-Setup-$Version.exe"
if (-not (Test-Path $installer)) { throw "Manca ${installer}: lancia prima .\publish.ps1" }

$subject = git -C $root log -1 --format=%s $commit
$body = (git -C $root log -1 --format=%b $commit) | Where-Object { $_ -notmatch '^Co-Authored-By:' }
$notes = @(
    "**Novità:** $($subject -replace '\s*\([\d.]+\)$', '')"
    ''
    ($body -join "`n").Trim()
    ''
    "**Aggiornamento:** scarica ``Lemonize-Setup-$Version.exe`` e installalo sopra la versione precedente: le impostazioni restano."
    ''
    '🤖 Generated with [Claude Code](https://claude.com/claude-code)'
) -join "`n"
$notesFile = New-TemporaryFile
Set-Content $notesFile $notes -Encoding utf8

try {
    # gh creates the tag on GitHub at that commit; fetch brings it back locally.
    gh release create $tag $installer --target $commit --title "Lemonize $Version" --notes-file $notesFile
    if ($LASTEXITCODE -ne 0) { throw "gh release create non riuscito" }
    git -C $root fetch origin --tags --quiet
}
finally {
    Remove-Item $notesFile
}
