# Publishes a GitHub release: tag v<version> + dist\Lemonize-Setup-<version>.exe.
#   .\release.ps1                 version from src\Lemonize\Lemonize.csproj
#   .\release.ps1 -Version 1.0.5  an earlier version (its installer must still be in dist\)
# The release goes on the commit whose subject ends with "(<version>)", which must already be on origin/main.
# Its message body (without Co-Authored-By) becomes the release notes: English, a "---" line, then the
# Italian title and text give bilingual notes; a message without "---" is taken as Italian only.
# Requires the GitHub CLI (gh).
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

function Title([string]$line) { $line -replace '\s*\([\d.]+\)$', '' }

# Bilingual commits: English subject and body, a "---" line, then the Italian title and body.
# Older commits are Italian only.
$subject = git -C $root log -1 --format=%s $commit
$body = @((git -C $root log -1 --format=%b $commit) | Where-Object { $_ -notmatch '^Co-Authored-By:' })
$split = [array]::FindIndex([string[]]$body, [Predicate[string]] { param($l) $l -match '^-{3,}\s*$' })
if ($split -ge 0) {
    $after = @($body | Select-Object -Skip ($split + 1))
    $titleIndex = [array]::FindIndex([string[]]$after, [Predicate[string]] { param($l) $l.Trim() -ne '' })
    $italianTitle = $after[$titleIndex]
    $italianBody = ($after | Select-Object -Skip ($titleIndex + 1)) -join "`n"
    $englishBody = ($body | Select-Object -First $split) -join "`n"
}
else {
    $italianTitle = $subject
    $italianBody = $body -join "`n"
}
$italian = @(
    "**Novità:** $(Title $italianTitle)"
    ''
    $italianBody.Trim()
    ''
    "**Aggiornamento:** scarica ``Lemonize-Setup-$Version.exe`` e installalo sopra la versione precedente: le impostazioni restano."
) -join "`n"
$notes = if ($split -ge 0) {
    $english = @(
        "**What's new:** $(Title $subject)"
        ''
        $englishBody.Trim()
        ''
        "**Update:** download ``Lemonize-Setup-$Version.exe`` and install it over the previous version: your settings are kept."
    ) -join "`n"
    "### English`n`n$english`n`n---`n`n### Italiano`n`n$italian"
} else { $italian }
$notes += "`n`n🤖 Generated with [Claude Code](https://claude.com/claude-code)"
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
