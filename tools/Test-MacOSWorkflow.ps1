$ErrorActionPreference = 'Stop'
$policy = Join-Path $PSScriptRoot 'Test-MacOSWorkflowPolicy.ps1'
$sha = 'a' * 40
$valid = @{
    Distribution = $true; Repository = 'DashyNMS/desktop'; Ref = 'refs/heads/master'
    EventName = 'workflow_dispatch'; WorkflowRef = 'DashyNMS/desktop/.github/workflows/preview-build.yml@refs/heads/master'
    Commit = $sha; SourceCommit = $sha
}
function Assert-Rejected([hashtable]$Arguments, [string]$Reason) {
    $rejected = $false
    try { & $policy @Arguments } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid signing context: $Reason" }
}
& $policy @valid
$release = $valid.Clone()
$release.WorkflowRef = 'DashyNMS/desktop/.github/workflows/release-build.yml@refs/heads/master'
& $policy @release
foreach ($change in @(
    @{ Ref = 'refs/heads/feature' }, @{ EventName = 'pull_request' }, @{ EventName = 'push' },
    @{ WorkflowRef = 'DashyNMS/desktop/.github/workflows/package-build.yml@refs/heads/master' },
    @{ WorkflowRef = 'DashyNMS/desktop/.github/workflows/preview-build.yml@refs/heads/feature' },
    @{ Repository = 'example/desktop' }, @{ SourceCommit = ('b' * 40) }
)) {
    $invalid = $valid.Clone()
    foreach ($key in $change.Keys) { $invalid[$key] = $change[$key] }
    Assert-Rejected $invalid ($change | ConvertTo-Json -Compress)
}
& $policy -SourceCommit $sha -Repository example/desktop -Ref refs/heads/feature -EventName pull_request
Assert-Rejected @{ SourceCommit = 'master' } 'moving source ref'
$package = Get-Content (Join-Path $PSScriptRoot '../.github/workflows/package-build.yml') -Raw
$routine = ($package -split '(?m)^  package:\r?$')[1] -split '(?m)^  macos-distribution:\r?$' | Select-Object -First 1
if ($routine -match 'environment:|MACOS_CERTIFICATE|APPLE_API') { throw 'Routine packages reference signing credentials.' }
if ($package -notmatch 'environment: DashyNMS_MacOS' -or $package -notmatch 'needs.version.result') { throw 'Protected signing job lacks its source-policy dependency.' }
foreach ($workflow in @('preview-build.yml','release-build.yml')) {
    $text = Get-Content (Join-Path $PSScriptRoot "../.github/workflows/$workflow") -Raw
    if ($text -notmatch 'distribution: true' -or $text -notmatch 'source_commit:') { throw "$workflow does not pin distribution packages." }
    $assetLines = @($text -split '\r?\n' | Where-Object { $_ -match '^\s+dist/DashyNMS-' })
    if ($assetLines.Count -ne 5 -or $assetLines -match '\.sha256|BUILD') { throw "$workflow must publish only the five installers." }
}
Write-Host 'Distribution caller, source and installer publication checks passed.'
