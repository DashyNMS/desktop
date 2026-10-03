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
$project = Join-Path $root 'src\DesktopNMS\DesktopNMS.csproj'

dotnet build $project -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$exe = Get-ChildItem (Join-Path $root "src\DesktopNMS\bin\$Configuration") -Recurse -Filter 'DashyNMS.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $exe) { throw "DashyNMS.exe not found after the build." }

New-Item -ItemType Directory -Force $OutputPath | Out-Null
$output = (Resolve-Path $OutputPath).Path

Write-Host "Rendering screenshots into $output ..."
$process = Start-Process $exe.FullName -ArgumentList '--demo', '--screenshots', "`"$output`"" -PassThru
if (-not $process.WaitForExit(300000)) {
    $process.Kill()
    throw "Timed out after 5 minutes."
}

Get-ChildItem $output -Filter '*.png' | Sort-Object Name | ForEach-Object { Write-Host ("  {0}  {1:N0} KB" -f $_.Name, ($_.Length / 1KB)) }
