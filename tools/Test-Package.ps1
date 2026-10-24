param(
    [Parameter(Mandatory)][string]$Runtime,
    [Parameter(Mandatory)][string]$Version,
    [switch]$MacOSDistribution
)
$ErrorActionPreference='Stop'
if ($MacOSDistribution -and -not $IsMacOS) { throw 'Developer ID verification requires macOS.' }
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output=Join-Path $root "artifacts/packages/$Version/$Runtime"
$verify=Join-Path $root "artifacts/package-verification/$Version/$Runtime"
if (Test-Path $verify) { throw 'Package verification requires a fresh folder.' }
New-Item $verify -ItemType Directory -Force | Out-Null
$mounted=$false
Push-Location $root
try {
    if ($IsWindows) {
        if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Installer smoke tests run only on disposable GitHub Actions runners.' }
        $installer=Join-Path $output "DashyNMS-Setup-$Version.exe"
        $install=Join-Path $verify 'installed'
        $process=Start-Process $installer -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/TASKS=',"/DIR=`"$install`"" -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
        $info=[Diagnostics.FileVersionInfo]::GetVersionInfo("$install/DashyNMS.exe")
        if ($info.FileDescription -ne 'DashyNMS' -or -not $info.ProductVersion.StartsWith($Version)) { throw 'The installer did not deploy the expected Avalonia app.' }
        $executable=Join-Path $install 'mcp/dashynms-mcp.exe'
    }
    elseif ($IsMacOS) {
        $mount=Join-Path $verify 'mounted'; New-Item $mount -ItemType Directory | Out-Null
        hdiutil attach "$output/DashyNMS-$Version-$Runtime.dmg" -readonly -nobrowse -mountpoint $mount
        if ($LASTEXITCODE -ne 0) { throw 'Could not mount the package.' }; $mounted=$true
        $app=Join-Path $mount 'DashyNMS.app'
        codesign --verify --deep --strict $app
        if ($LASTEXITCODE -ne 0) { throw 'The packaged app signature is invalid.' }
        if ($MacOSDistribution) {
            . "$PSScriptRoot/macos/Distribution-Common.ps1"
            $diskImage = "$output/DashyNMS-$Version-$Runtime.dmg"
            Invoke-MacTool xcrun @('stapler','validate',$diskImage) | Out-Null
            Invoke-MacTool xcrun @('stapler','validate',$app) | Out-Null
            Invoke-MacTool spctl @('--assess','--type','execute','--verbose=2',$app) | Out-Null
            Invoke-MacTool spctl @('--assess','--type','open','--context','context:primary-signature','--verbose=2',$diskImage) | Out-Null
        }
        foreach ($file in @('DashyNMS','dashynms-login','libdashynms-notifications.dylib')) {
            if (-not (Test-Path "$app/Contents/MacOS/$file" -PathType Leaf)) { throw "Missing packaged file: $file" }
        }
        foreach ($file in @('LICENSE','BUILD.txt','mcp/LICENSE','mcp/BUILD.txt')) {
            if (-not (Test-Path "$app/Contents/Resources/$file" -PathType Leaf)) { throw "Missing packaged resource: $file" }
        }
        $screenshots = Join-Path $verify 'demo-screenshots'
        $start = [Diagnostics.ProcessStartInfo]::new("$app/Contents/MacOS/DashyNMS")
        $start.UseShellExecute = $false
        foreach ($argument in @('--demo','--screenshots',$screenshots)) { $start.ArgumentList.Add($argument) }
        $demo = [Diagnostics.Process]::Start($start)
        try {
            if (-not $demo.WaitForExit(180000)) { throw 'The packaged Mac app demo timed out.' }
            if ($demo.ExitCode -ne 0) { throw "The packaged Mac app failed with exit code $($demo.ExitCode)." }
            foreach ($screen in @('dashboard','devices','alerts','health','device-overview')) {
                if (-not (Test-Path (Join-Path $screenshots "$screen.png") -PathType Leaf)) {
                    throw "The packaged Mac app did not render $screen."
                }
            }
        } finally {
            if (-not $demo.HasExited) { $demo.Kill(); $demo.WaitForExit() }
            $demo.Dispose()
        }
        $executable=Join-Path $app 'Contents/MacOS/mcp/dashynms-mcp'
    }
    else {
        Push-Location $verify
        try {
            & "$output/DashyNMS-$Version-$Runtime.AppImage" --appimage-extract | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not extract the AppImage.' }
        } finally { Pop-Location }
        $executable=Join-Path $verify 'squashfs-root/usr/bin/mcp/dashynms-mcp'
        desktop-file-validate "$verify/squashfs-root/dashynms.desktop"
        if ($LASTEXITCODE -ne 0) { throw 'The desktop launcher is invalid.' }
    }
    $env:DASHYNMS_TEST_MCP=(Resolve-Path $executable).Path
    dotnet test tests/DesktopNMS.Presentation.Tests -c Release --filter FullyQualifiedName~McpBundleTests
    if ($LASTEXITCODE -ne 0) { throw 'The network tools failed from inside the finished package.' }
} finally {
    if ($mounted) { hdiutil detach $mount | Out-Null }
    Remove-Item Env:DASHYNMS_TEST_MCP -ErrorAction SilentlyContinue
    Pop-Location
}
