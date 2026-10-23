<# Exercise preview numbering, required notes and release asset gates without publishing. #>
$ErrorActionPreference = 'Stop'
$prepare = Join-Path $PSScriptRoot 'Prepare-Preview.ps1'
$verify = Join-Path $PSScriptRoot 'Test-PreviewArtifacts.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('dashynms-preview-' + [guid]::NewGuid().ToString('N'))
New-Item "$fixture/docs/releases" -ItemType Directory -Force | Out-Null
Set-Content "$fixture/Directory.Build.props" '<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>'
function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid preview: $Reason" }
}
Push-Location $fixture
try {
    git init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture init failed.' }
    git add Directory.Build.props
    git -c user.name=PreviewTests -c user.email=preview-tests@example.invalid commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed.' }
    foreach ($tag in @('v2.0.0-preview.2','v2.0.0-preview.4','v2.0.0-preview.ci.999.1','v3.0.0-preview.99')) {
        git tag $tag
        if ($LASTEXITCODE -ne 0) { throw 'Fixture tag failed.' }
    }
    Assert-Rejected { & $prepare -RepositoryRoot $fixture -GitHubOutput '' } 'missing notes'
    Set-Content docs/releases/2.0.0-preview.5.md ' '
    Assert-Rejected { & $prepare -RepositoryRoot $fixture -GitHubOutput '' } 'blank notes'
    Set-Content docs/releases/2.0.0-preview.5.md 'Reviewed preview notes'
    $result = & $prepare -RepositoryRoot $fixture -GitHubOutput "$fixture/outputs.txt"
    if ($result.Version -ne '2.0.0-preview.5') { throw 'Incorrect preview number.' }
    if ((Get-Content artifacts/preview/release-notes.md -Raw).Trim() -ne 'Reviewed preview notes') { throw 'Notes changed.' }
    if ((Get-Content outputs.txt) -notcontains 'tag=v2.0.0-preview.5') { throw 'Missing workflow output.' }
    if (@(git tag -l v2.0.0-preview.5).Count) { throw 'Preparation created a tag.' }
    New-Item assets -ItemType Directory | Out-Null
    foreach ($runtime in @('win-x64','osx-x64','osx-arm64','linux-x64','linux-arm64')) {
        $name = if ($runtime -eq 'win-x64') { "DashyNMS-Setup-$($result.Version).exe" }
            elseif ($runtime.StartsWith('osx-')) { "DashyNMS-$($result.Version)-$runtime.dmg" }
            else { "DashyNMS-$($result.Version)-$runtime.AppImage" }
        Set-Content "assets/$name" "fixture $runtime"
        $hash = (Get-FileHash "assets/$name" -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content "assets/$name.sha256" "$hash  $name"
        Set-Content "assets/BUILD-$runtime.txt" "DashyNMS $($result.Version) ($runtime)`nDesktop commit: $($result.Commit)"
    }
    & $verify -Directory assets -Version $result.Version -Commit $result.Commit
    $asset = "assets/DashyNMS-Setup-$($result.Version).exe"
    $original = Get-Content $asset -Raw
    Set-Content $asset 'tampered'
    Assert-Rejected { & $verify -Directory assets -Version $result.Version -Commit $result.Commit } 'tampered package'
    [IO.File]::WriteAllText((Join-Path $fixture $asset), $original)
    Assert-Rejected { & $verify -Directory assets -Version $result.Version -Commit ('0' * 40) } 'wrong source commit'
    Set-Content assets/extra.txt 'unexpected'
    Assert-Rejected { & $verify -Directory assets -Version $result.Version -Commit $result.Commit } 'unexpected asset'
    Remove-Item -LiteralPath assets/extra.txt
    Remove-Item -LiteralPath assets/BUILD-linux-arm64.txt
    Assert-Rejected { & $verify -Directory assets -Version $result.Version -Commit $result.Commit } 'missing target'
    Write-Host 'Preview numbering, notes and asset gate checks passed.'
} finally { Pop-Location }
