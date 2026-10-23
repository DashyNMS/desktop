<# Select the next preview and require reviewed release notes. Does not create tags or releases. #>
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string]$OutputDirectory = 'artifacts/preview',
    [string]$GitHubOutput = $env:GITHUB_OUTPUT
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $RepositoryRoot).Path
Push-Location $root
try {
    [xml]$props = Get-Content Directory.Build.props
    $baseVersion = [string]$props.Project.PropertyGroup.Version
    if ($baseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The base version must be numeric, such as 2.0.0.' }
    $pattern = '^v' + [regex]::Escape($baseVersion) + '-preview\.(\d+)$'
    $tags = @(git tag -l "v$baseVersion-preview.*")
    if ($LASTEXITCODE -ne 0) { throw 'Could not read preview tags.' }
    $numbers = @($tags | ForEach-Object { if ($_ -match $pattern) { [int]$matches[1] } })
    $next = if ($numbers.Count) { ($numbers | Measure-Object -Maximum).Maximum + 1 } else { 1 }
    $version = "$baseVersion-preview.$next"
    $notes = "docs/releases/$version.md"
    if (-not (Test-Path $notes -PathType Leaf) -or [string]::IsNullOrWhiteSpace((Get-Content $notes -Raw))) {
        throw "Write and review $notes before starting this preview."
    }
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the preview source commit.' }
    New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
    Copy-Item $notes (Join-Path $OutputDirectory 'release-notes.md')
    if ($GitHubOutput) {
        @("version=$version", "tag=v$version", "commit=$commit") | Add-Content $GitHubOutput -Encoding utf8NoBOM
    }
    [pscustomobject]@{ Version = $version; Tag = "v$version"; Commit = $commit; Notes = $notes }
} finally { Pop-Location }
