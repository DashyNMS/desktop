<#
.SYNOPSIS
    Builds the DashyNMS MCP server for bundling with the desktop app.

.DESCRIPTION
    Publishes DashyNMS/mcp's server as one self-contained Windows exe
    (no .NET install needed) into artifacts\mcp, where DesktopNMS.csproj
    picks it up and copies it beside the app as mcp\dashynms-mcp.exe.
    Needs the .NET 10 SDK.

.EXAMPLE
    ./tools/Publish-Mcp.ps1 -Source ..\mcp
    ./tools/Publish-Mcp.ps1 -Source ..\mcp -Ref v1.1.0
#>
param(
    # A checkout of https://github.com/DashyNMS/mcp.
    [Parameter(Mandatory = $true)]
    [string]$Source,

    # A tag or commit to build, so the bundled server is pinned; the checkout's current commit if left out.
    [string]$Ref,

    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$source = Resolve-Path $Source
$output = Join-Path $root 'artifacts\mcp'

if ($Ref) {
    git -C $source checkout --quiet $Ref
    if ($LASTEXITCODE -ne 0) { throw "Couldn't check out $Ref." }
}

git -C $source submodule update --init --quiet
if ($LASTEXITCODE -ne 0) { throw "Couldn't fetch the MCP repo's submodules." }

$commit = (git -C $source rev-parse --short HEAD).Trim()
Write-Host "Building DashyNMS MCP at $commit for $Runtime ..."

dotnet publish (Join-Path $source 'src\DashyNMS.Mcp\DashyNMS.Mcp.csproj') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $output --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Publishing the MCP server failed." }

Set-Content -Path (Join-Path $output 'BUILD.txt') -Value "DashyNMS MCP $commit ($Runtime)" -Encoding utf8
Get-Item (Join-Path $output 'dashynms-mcp.exe') | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
