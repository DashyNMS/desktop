param(
    [Parameter(Mandatory)][ValidateSet('1.0.0','1.1.0')][string]$FromVersion,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PackageDirectory
)
$ErrorActionPreference='Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true') { throw 'This test installs apps only on disposable Windows GitHub Actions runners.' }
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work=Join-Path $root "artifacts/windows-upgrade/$FromVersion"
if(Test-Path $work) { throw 'Upgrade checks require a fresh folder.' }
New-Item $work -ItemType Directory -Force | Out-Null
$install=Join-Path $work 'DashyNMS'
$assetName="DashyNMS-Setup-$FromVersion.exe"
$release=gh api "repos/DashyNMS/desktop/releases/tags/v$FromVersion" | ConvertFrom-Json
if($LASTEXITCODE -ne 0) { throw 'Could not find the shipped installer.' }
$asset=$release.assets | Where-Object name -eq $assetName
if(-not $asset -or $asset.digest -notmatch '^sha256:([0-9a-f]{64})$') { throw 'The shipped installer has no trusted SHA-256 digest.' }
$sha=$matches[1]
$old=Join-Path $work $assetName
Invoke-WebRequest $asset.browser_download_url -OutFile $old
if((Get-FileHash $old -Algorithm SHA256).Hash -ne $sha) { throw 'The shipped installer checksum did not match.' }
$process=Start-Process $old -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/TASKS=startmenuicon',"/DIR=`"$install`"" -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0) { throw 'Installing the shipped app failed.' }
$env:DASHYNMS_UPGRADE_FROM=$FromVersion
$env:DASHYNMS_UPGRADE_VERSION=$Version
$env:DASHYNMS_UPGRADE_EXE=Join-Path $install 'DashyNMS.exe'
$env:DASHYNMS_UPGRADE_PHASE='seed'
Push-Location $root
try {
    dotnet test tests/DesktopNMS.Presentation.Tests -c Release --filter FullyQualifiedName~WindowsInstallerUpgradeTests
    if($LASTEXITCODE -ne 0) { throw 'Could not seed the legacy profile.' }
    $profile=Join-Path $env:APPDATA 'DashyNMS'
    $hashes=@{}; Get-ChildItem $profile -File | ForEach-Object { $hashes[$_.FullName]=(Get-FileHash $_.FullName).Hash }
    $runKey='HKCU:/Software/Microsoft/Windows/CurrentVersion/Run'
    New-Item $runKey -Force | Out-Null
    $startup="`"$install\DashyNMS.exe`" --minimised"
    Set-ItemProperty $runKey -Name DashyNMS -Value $startup
    $shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'DashyNMS.lnk'
    $shell=New-Object -ComObject WScript.Shell
    $oldTarget=$shell.CreateShortcut($shortcut).TargetPath
    if($oldTarget -ne $env:DASHYNMS_UPGRADE_EXE) { throw 'The shipped shortcut did not target the installed app.' }
    $candidate=Join-Path (Resolve-Path $PackageDirectory) "DashyNMS-Setup-$Version.exe"
    # No /DIR: the unchanged AppId must discover and reuse the 1.x install.
    $process=Start-Process $candidate -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER' -WindowStyle Hidden -Wait -PassThru
    if($process.ExitCode -ne 0) { throw 'Upgrading the shipped app failed.' }
    foreach($file in $hashes.Keys) { if((Get-FileHash $file).Hash -ne $hashes[$file]) { throw "The installer changed profile file $file" } }
    if((Get-ItemPropertyValue $runKey -Name DashyNMS) -ne $startup) { throw 'Startup registration was lost.' }
    if($shell.CreateShortcut($shortcut).TargetPath -ne $oldTarget) { throw 'The installed shortcut identity changed.' }
    $entries=@(Get-ChildItem 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall' | Get-ItemProperty | Where-Object DisplayName -eq 'DashyNMS')
    if($entries.Count -ne 1) { throw 'The upgrade created a side-by-side install.' }
    if(-not (Test-Path "$install/mcp/dashynms-mcp.exe")) { throw 'The upgraded install is missing its network tools.' }
    $env:DASHYNMS_UPGRADE_PHASE='verify'
    dotnet test tests/DesktopNMS.Presentation.Tests -c Release --no-build --filter FullyQualifiedName~WindowsInstallerUpgradeTests
    if($LASTEXITCODE -ne 0) { throw 'The upgraded app cannot read the legacy profile.' }
} finally {
    Get-ChildItem Env:DASHYNMS_UPGRADE_* | Remove-Item
    Pop-Location
}
