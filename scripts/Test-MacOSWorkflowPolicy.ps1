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
    # What may sign, and what starts it. Everything else is refused.
    $callers = @{
        'edge-build.yml' = @('push','workflow_dispatch')
        'nightly-build.yml' = @('schedule','workflow_dispatch')
        'release-build.yml' = @('workflow_dispatch')
    }
    $caller = if ($WorkflowRef -cmatch '^DashyNMS/desktop/\.github/workflows/([a-z-]+\.yml)@refs/heads/main$') { $Matches[1] }
    if ($Repository -cne 'DashyNMS/desktop' -or $Ref -cne 'refs/heads/main' -or -not $caller -or
        -not $callers.ContainsKey($caller) -or $EventName -cnotin $callers[$caller] -or $Commit -cne $SourceCommit) {
        throw 'Distribution signing requires an edge, nightly or release build of main at its exact source commit.'
    }
}
'Package source policy passed.' | Write-Host
