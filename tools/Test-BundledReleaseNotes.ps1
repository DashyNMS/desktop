# Verify an actual release build embeds the published source file, and refuses missing notes.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
$previousExpected = $env:DASHYNMS_EXPECTED_RELEASE_NOTES
try {
    [xml]$props = Get-Content Directory.Build.props
    $baseVersion = [string]$props.Project.PropertyGroup.Version
    $pattern = '^' + [regex]::Escape($baseVersion) + '-preview\.(\d+)\.md$'
    $previews = @(Get-ChildItem docs/releases -File | Where-Object { $_.Name -match $pattern } |
        Sort-Object { if ($_.Name -match $pattern) { [int]$Matches[1] } } -Descending)
    $version = if ($previews.Count) { $previews[0].BaseName } else { $baseVersion }
    $env:DASHYNMS_EXPECTED_RELEASE_NOTES = Join-Path $root "docs/releases/$version.md"
    dotnet test tests/DesktopNMS.Presentation.Tests -c Release "-p:Version=$version" -p:RequireReleaseNotes=true --filter FullyQualifiedName~BundledWhatsNewTests
    if ($LASTEXITCODE -ne 0) { throw 'The release build did not embed its exact published notes.' }
    $missing = '0.0.0-preview.999999'
    if (Test-Path "docs/releases/$missing.md") { throw 'The negative test requires a missing release notes file.' }
    $output = dotnet build src/DesktopNMS.Presentation -c Release --no-restore "-p:Version=$missing" -p:RequireReleaseNotes=true 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'Missing exact release notes') {
        throw 'A published package without exact release notes did not fail for the expected reason.'
    }
    $global:LASTEXITCODE = 0
    Write-Host "Exact embedded release notes and missing-notes build refusal passed ($version)."
} finally {
    $env:DASHYNMS_EXPECTED_RELEASE_NOTES = $previousExpected
    Pop-Location
}
