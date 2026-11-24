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
    Distribution = $true; Repository = 'DashyNMS/desktop'; Ref = 'refs/heads/main'
    EventName = 'workflow_dispatch'; WorkflowRef = 'DashyNMS/desktop/.github/workflows/nightly-build.yml@refs/heads/main'
    Commit = $sha; SourceCommit = $sha
}
function Assert-Rejected([hashtable]$Arguments, [string]$Reason) {
    $rejected = $false
    try { & $policy @Arguments } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid signing context: $Reason" }
}
foreach ($allowed in @(
    @{ File = 'edge-build.yml'; EventName = 'push' }, @{ File = 'edge-build.yml'; EventName = 'workflow_dispatch' },
    @{ File = 'nightly-build.yml'; EventName = 'schedule' }, @{ File = 'nightly-build.yml'; EventName = 'workflow_dispatch' },
    @{ File = 'release-build.yml'; EventName = 'workflow_dispatch' }
)) {
    $context = $valid.Clone()
    $context.WorkflowRef = "DashyNMS/desktop/.github/workflows/$($allowed.File)@refs/heads/main"
    $context.EventName = $allowed.EventName
    & $policy @context
}
foreach ($change in @(
    @{ Ref = 'refs/heads/feature' }, @{ EventName = 'pull_request' }, @{ EventName = 'push' }, @{ EventName = 'release' },
    @{ WorkflowRef = 'DashyNMS/desktop/.github/workflows/packages.yml@refs/heads/main' },
    @{ WorkflowRef = 'DashyNMS/desktop/.github/workflows/nightly-build.yml@refs/heads/feature' },
    @{ WorkflowRef = 'DashyNMS/desktop/.github/workflows/release-build.yml@refs/heads/main'; EventName = 'push' },
    @{ Repository = 'example/desktop' }, @{ SourceCommit = ('b' * 40) }
)) {
    $invalid = $valid.Clone()
    foreach ($key in $change.Keys) { $invalid[$key] = $change[$key] }
    Assert-Rejected $invalid ($change | ConvertTo-Json -Compress)
}
& $policy -SourceCommit $sha -Repository example/desktop -Ref refs/heads/feature -EventName pull_request
Assert-Rejected @{ SourceCommit = 'main' } 'moving source ref'
$package = Get-Content (Join-Path $PSScriptRoot '../.github/workflows/packages.yml') -Raw
if (-not $package.Contains("environment: `${{ inputs.sign && startsWith(matrix.runtime, 'osx-') && 'DashyNMS_MacOS' || '' }}")) {
    throw 'Only signed macOS packages may use the signing environment.'
}
$policyStep = $package.IndexOf('Validate the source before using credentials')
$firstSecret = $package.IndexOf('${{ inputs.sign && secrets.')
if ($policyStep -lt 0 -or $firstSecret -lt $policyStep) { throw 'The signing policy must run before any credential is used.' }
$secretNames = @('MACOS_CERTIFICATE_BASE64','MACOS_CERTIFICATE_PASSWORD','MACOS_SIGNING_IDENTITY','APPLE_API_KEY','APPLE_API_KEY_ID','APPLE_API_ISSUER_ID')
foreach ($secret in $secretNames) {
    if (-not $package.Contains('          ' + $secret + ': ${{ inputs.sign && secrets.' + $secret + " || '' }}")) {
        throw "packages.yml gives $secret to a package that isn't signed."
    }
}
foreach ($workflow in @('edge-build.yml','nightly-build.yml','release-build.yml')) {
    $text = Get-Content (Join-Path $PSScriptRoot "../.github/workflows/$workflow") -Raw
    if ($text -notmatch 'sign: true' -or $text -notmatch 'commit: \$\{\{ needs\.version') { throw "$workflow does not pin the packages it signs." }
    if ($text -match 'secrets: inherit') { throw "$workflow must pass only the named distribution credentials." }
    foreach ($secret in $secretNames) {
        if (-not $text.Contains('      ' + $secret + ': ${{ secrets.' + $secret + ' }}')) {
            throw "$workflow does not pass the protected environment secret $secret."
        }
    }
}
if (-not $package.Contains('  MACOS_UPDATE_TEAM_ID: ${{ vars.MACOS_UPDATE_TEAM_ID }}')) {
    throw 'packages.yml does not pass the public update publisher pin to the packager.'
}
Write-Host 'Distribution caller, source and signing checks passed.'
