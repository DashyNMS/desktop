param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string]$GitHubOutput = $env:GITHUB_OUTPUT
)
$ErrorActionPreference = 'Stop'
if ($Tag -cnotmatch '^v(\d+\.\d+\.\d+)$') { throw 'Stable releases require a vX.Y.Z tag.' }
$version = $Matches[1]
Push-Location (Resolve-Path $RepositoryRoot).Path
try {
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the checked-out source commit.' }
    $tagCommit = (git rev-parse --verify "refs/tags/$Tag^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $commit) { throw 'The release tag must point to this master workflow commit.' }
    [xml]$props = Get-Content Directory.Build.props
    if ([string]$props.Project.PropertyGroup.Version -cne $version) { throw 'The stable tag must match Directory.Build.props.' }
    if ($GitHubOutput) { @("version=$version", "tag=$Tag", "commit=$commit") | Add-Content $GitHubOutput -Encoding utf8NoBOM }
    [pscustomobject]@{ Version = $version; Tag = $Tag; Commit = $commit }
} finally { Pop-Location }
