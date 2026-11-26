<# Exercise the release asset gate without publishing. #>
$ErrorActionPreference = 'Stop'
$verify = Join-Path $PSScriptRoot 'Test-ReleaseArtifacts.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('dashynms-release-' + [guid]::NewGuid().ToString('N'))
New-Item $fixture -ItemType Directory -Force | Out-Null
Set-Content "$fixture/Directory.Build.props" '<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>'
function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid release: $Reason" }
}
Push-Location $fixture
try {
    git init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture init failed.' }
    git add Directory.Build.props
    git -c user.name=ReleaseTests -c user.email=release-tests@example.invalid commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed.' }
    $result = [pscustomobject]@{ Version = '2.0.0-nightly.5'; Commit = (git rev-parse HEAD).Trim() }
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
    Write-Host 'Release asset gate checks passed.'
} finally { Pop-Location }
