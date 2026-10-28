$ErrorActionPreference = 'Stop'
foreach ($script in Get-ChildItem $PSScriptRoot -Recurse -Filter '*.ps1' -File) {
    $syntax = [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$null)
    $calls = $syntax.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-MacTool'
    }, $true)
    foreach ($call in $calls) {
        $operation = @($call.CommandElements | Where-Object {
            $_ -is [Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -eq 'Operation'
        })
        if ($operation.Count -eq 0) { throw "Missing macOS diagnostic operation in $($script.Name):$($call.Extent.StartLineNumber)." }
    }
}
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
    $packageCaller = ($text -split '(?m)^  packages:\r?$')[1] -split '(?m)^  publish:\r?$' | Select-Object -First 1
    if ($packageCaller -match 'secrets: inherit') { throw "$workflow must pass only the named distribution credentials." }
    foreach ($secret in @('MACOS_CERTIFICATE_BASE64','MACOS_CERTIFICATE_PASSWORD','MACOS_SIGNING_IDENTITY',
        'APPLE_API_KEY','APPLE_API_KEY_ID','APPLE_API_ISSUER_ID')) {
        $mapping = '      ' + $secret + ': ${{ secrets.' + $secret + ' }}'
        if (-not $packageCaller.Contains($mapping)) { throw "$workflow does not pass the protected environment secret $secret." }
    }
    $assetLines = @($text -split '\r?\n' | Where-Object { $_ -match '^\s+dist/DashyNMS-' })
    if ($assetLines.Count -ne 5 -or $assetLines -match '\.sha256|BUILD') { throw "$workflow must publish only the five installers." }
}
Write-Host 'Distribution caller, source and installer publication checks passed.'

# Development builds invoke the packager directly, outside package-build.yml.
# Both paths must embed the public pin for an ad-hoc dev-to-preview upgrade.
foreach ($workflow in @('dev-build.yml','package-build.yml')) {
    $text = Get-Content (Join-Path $PSScriptRoot "../.github/workflows/$workflow") -Raw
    if (-not $text.Contains('  MACOS_UPDATE_TEAM_ID: ${{ vars.MACOS_UPDATE_TEAM_ID }}')) {
        throw "$workflow does not pass the public update publisher pin to the packager."
    }
}
Write-Host 'Development and native package builds share the public update publisher pin.'
