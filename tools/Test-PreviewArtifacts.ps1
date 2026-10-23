<# Verify the complete preview asset set before creating its public tag. #>
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+-preview\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $Directory).Path
$expected = [System.Collections.Generic.List[string]]::new()
foreach ($runtime in @('win-x64','osx-x64','osx-arm64','linux-x64','linux-arm64')) {
    $name = if ($runtime -eq 'win-x64') { "DashyNMS-Setup-$Version.exe" }
        elseif ($runtime.StartsWith('osx-')) { "DashyNMS-$Version-$runtime.dmg" }
        else { "DashyNMS-$Version-$runtime.AppImage" }
    foreach ($file in @($name, "$name.sha256", "BUILD-$runtime.txt")) {
        if (-not (Test-Path (Join-Path $root $file) -PathType Leaf)) { throw "Missing preview asset: $file" }
        $expected.Add($file)
    }
    $checksum = (Get-Content (Join-Path $root "$name.sha256") -Raw).Trim()
    if ($checksum -notmatch ('^([0-9a-fA-F]{64})\s+' + [regex]::Escape($name) + '$')) { throw "Invalid checksum record for $name" }
    $hash = $matches[1]
    if ((Get-FileHash (Join-Path $root $name) -Algorithm SHA256).Hash -ne $hash) { throw "Checksum mismatch for $name" }
    $manifest = Get-Content (Join-Path $root "BUILD-$runtime.txt") -Raw
    if ($manifest -notmatch ('(?m)^Desktop commit: ' + [regex]::Escape($Commit) + '\r?$') -or
        $manifest -notmatch ('(?m)^DashyNMS ' + [regex]::Escape($Version) + ' \(' + [regex]::Escape($runtime) + '\)\r?$')) {
        throw "The $runtime package was built from a different version or commit."
    }
}
foreach ($file in Get-ChildItem $root -Force) {
    if ($file.PSIsContainer -or -not $expected.Contains($file.Name)) { throw "Unexpected preview asset: $($file.Name)" }
}
Write-Host 'All five preview packages, checksums and source manifests match.'
