<#
.SYNOPSIS
    Renders the README and release screenshots from demo mode.

.DESCRIPTION
    Builds DashyNMS, then runs it with --demo --screenshots: the real app
    against its built-in example network (example.net names, documentation
    addresses), in demo mode's own data folder. It tours the main screens,
    saves each as a 1920x1080 PNG and exits. Nothing from a real LibreNMS, and
    nothing of your own settings, is used or changed - so it is safe to run
    with DashyNMS already open.

.EXAMPLE
    ./tools/Render-Screenshots.ps1
    ./tools/Render-Screenshots.ps1 -OutputPath C:\temp\shots
#>
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\docs\screenshots'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\DesktopNMS.Avalonia\DesktopNMS.Avalonia.csproj'
$buildOutput = Join-Path $root 'artifacts/screenshots-app'

dotnet build $project -c $Configuration -o $buildOutput --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$executable = Join-Path $buildOutput $(if ($IsWindows) { 'DashyNMS.exe' } else { 'DashyNMS' })
if (-not (Test-Path $executable -PathType Leaf)) { throw "DashyNMS executable not found after the build." }

New-Item -ItemType Directory -Force $OutputPath | Out-Null
$output = (Resolve-Path $OutputPath).Path

Write-Host "Rendering screenshots into $output ..."
$start = @{ FilePath = $executable; ArgumentList = @('--demo', '--screenshots', "`"$output`""); PassThru = $true }
if ($IsWindows) { $start.WindowStyle = 'Hidden' }
$process = Start-Process @start
if (-not $process.WaitForExit(300000)) {
    $process.Kill()
    throw "Timed out after 5 minutes."
}
if ($process.ExitCode -ne 0) { throw "The screenshot tour failed with exit code $($process.ExitCode)." }

foreach ($name in @('dashboard', 'devices', 'alerts', 'alert-rules', 'health', 'neighbours', 'logs', 'network-map',
    'device-overview', 'device-ports', 'device-graphs', 'device-graphs-temperature', 'device-graphs-legend', 'device-neighbours')) {
    if (-not (Test-Path (Join-Path $output "$name.png") -PathType Leaf)) { throw "Screenshot missing: $name.png" }
}

Get-ChildItem $output -Filter '*.png' | Sort-Object Name | ForEach-Object { Write-Host ("  {0}  {1:N0} KB" -f $_.Name, ($_.Length / 1KB)) }
