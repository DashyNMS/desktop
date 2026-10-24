param(
    [switch]$Distribution,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Ref = $env:GITHUB_REF,
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$WorkflowRef = $env:GITHUB_WORKFLOW_REF,
    [string]$Commit = $env:GITHUB_SHA,
    [string]$SourceCommit = $env:GITHUB_SHA
)
$ErrorActionPreference = 'Stop'
if ($SourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'Package source must be a full commit SHA.' }
if ($Distribution) {
    $callers = @(
        'DashyNMS/desktop/.github/workflows/preview-build.yml@refs/heads/master',
        'DashyNMS/desktop/.github/workflows/release-build.yml@refs/heads/master'
    )
    if ($Repository -cne 'DashyNMS/desktop' -or $Ref -cne 'refs/heads/master' -or
        $EventName -cne 'workflow_dispatch' -or $WorkflowRef -cnotin $callers -or
        $Commit -cne $SourceCommit) {
        throw 'Distribution signing requires a preview or release dispatched on master at its exact source commit.'
    }
}
'Package source policy passed.' | Write-Host
