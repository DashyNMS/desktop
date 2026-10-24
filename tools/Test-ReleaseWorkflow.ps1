$ErrorActionPreference = 'Stop'
$prepare = Join-Path $PSScriptRoot 'Prepare-ReleaseWorkflow.ps1'
$verify = Join-Path $PSScriptRoot 'Test-PreviewArtifacts.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('dashynms-release-' + [guid]::NewGuid().ToString('N'))
New-Item $fixture -ItemType Directory | Out-Null
function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid release: $Reason" }
}
Push-Location $fixture
try {
    git init --quiet
    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>'
    git add Directory.Build.props
    git -c user.name=ReleaseTests -c user.email=release-tests@example.invalid commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed.' }
    git tag v2.0.0
    git tag v3.0.0
    $result = & $prepare -RepositoryRoot $fixture -Tag v2.0.0 -GitHubOutput ''
    if ($result.Version -cne '2.0.0') { throw 'Stable version changed.' }
    Assert-Rejected { & $prepare -RepositoryRoot $fixture -Tag v3.0.0 -GitHubOutput '' } 'version differs from props'
    Assert-Rejected { & $prepare -RepositoryRoot $fixture -Tag v2.0.0-preview.8 -GitHubOutput '' } 'preview tag'
    New-Item assets -ItemType Directory | Out-Null
    foreach ($runtime in @('win-x64','osx-x64','osx-arm64','linux-x64','linux-arm64')) {
        $name = if ($runtime -eq 'win-x64') { 'DashyNMS-Setup-2.0.0.exe' }
            elseif ($runtime.StartsWith('osx-')) { "DashyNMS-2.0.0-$runtime.dmg" }
            else { "DashyNMS-2.0.0-$runtime.AppImage" }
        Set-Content "assets/$name" "fixture $runtime"
        $hash = (Get-FileHash "assets/$name" -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content "assets/$name.sha256" "$hash  $name"
        Set-Content "assets/BUILD-$runtime.txt" "DashyNMS 2.0.0 ($runtime)`nDesktop commit: $($result.Commit)"
    }
    & $verify -Directory assets -Version $result.Version -Commit $result.Commit
    Set-Content another.txt changed
    git add another.txt
    git -c user.name=ReleaseTests -c user.email=release-tests@example.invalid commit --quiet -m changed
    if ($LASTEXITCODE -ne 0) { throw 'Fixture follow-up commit failed.' }
    Assert-Rejected { & $prepare -RepositoryRoot $fixture -Tag v2.0.0 -GitHubOutput '' } 'tag points to an earlier commit'
    Write-Host 'Stable tag, version, source and asset checks passed.'
} finally { Pop-Location }
