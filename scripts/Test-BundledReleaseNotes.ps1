# Verify an actual release build embeds the version's notes, and refuses missing notes.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
$previousExpected = $env:DASHYNMS_EXPECTED_RELEASE_NOTES
try {
    [xml]$props = Get-Content Directory.Build.props
    $baseVersion = [string]$props.Project.PropertyGroup.Version
    $version = "$baseVersion-nightly.999999"
    $notes = Join-Path $root "docs/releases/$baseVersion.md"
    $env:DASHYNMS_EXPECTED_RELEASE_NOTES = $notes
    dotnet test tests/DesktopNMS.Presentation.Tests -c Release "-p:Version=$version" -p:RequireReleaseNotes=true --filter FullyQualifiedName~BundledWhatsNewTests
    if ($LASTEXITCODE -ne 0) { throw 'The release build did not embed the version's notes.' }
    $missing = '0.0.0-nightly.999999'
    if (Test-Path 'docs/releases/0.0.0.md') { throw 'The negative test requires a missing release notes file.' }
    $output = dotnet build src/DesktopNMS.Presentation -c Release --no-restore "-p:Version=$missing" -p:RequireReleaseNotes=true 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'Missing release notes') {
        throw 'A published package without release notes did not fail for the expected reason.'
    }
    $global:LASTEXITCODE = 0
    Write-Host "Embedded release notes and missing-notes build refusal passed ($version)."
} finally {
    $env:DASHYNMS_EXPECTED_RELEASE_NOTES = $previousExpected
    Pop-Location
}
