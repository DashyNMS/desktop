<#
.SYNOPSIS
    Builds the DashyNMS MCP server for bundling with the desktop app.

.DESCRIPTION
    Publishes DashyNMS/mcp's server as a self-contained native executable
    into artifacts/mcp/<runtime>, where DesktopNMS.Avalonia.csproj picks
    the matching runtime up and copies it beside the app in mcp/.
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

    [ValidateSet('win-x64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$source = Resolve-Path $Source
$output = Join-Path $root "artifacts/mcp/$Runtime"
if (Test-Path $output) { throw "A network-tools build already exists at $output. Use a fresh checkout or archive that runtime folder first." }

if ($Ref) {
    git -C $source checkout --quiet $Ref
    if ($LASTEXITCODE -ne 0) { throw "Couldn't check out $Ref." }
}

git -C $source submodule update --init --quiet
if ($LASTEXITCODE -ne 0) { throw "Couldn't fetch the MCP repo's submodules." }

$commit = (git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Couldn't identify the MCP commit." }
Write-Host "Building DashyNMS MCP at $commit for $Runtime ..."

Push-Location $source
try {
    $nativeLibraries = if ($Runtime.StartsWith('osx-')) {
        '-p:IncludeNativeLibrariesForSelfExtract=false'
    } else { '-p:IncludeNativeLibrariesForSelfExtract=true' }
    # Resolve the MCP repo's .NET 10 global.json rather than the desktop SDK.
    dotnet publish (Join-Path $source 'src\DashyNMS.Mcp\DashyNMS.Mcp.csproj') `
        -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true $nativeLibraries -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none "-p:SourceRevisionId=$commit" `
        -o $output --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publishing the MCP server failed." }
    $version = dotnet msbuild (Join-Path $source 'src/DashyNMS.Mcp/DashyNMS.Mcp.csproj') `
        -nologo -verbosity:quiet -target:GenerateAssemblyInfo -getProperty:InformationalVersion `
        -property:Configuration=Release "-property:SourceRevisionId=$commit"
    if ($LASTEXITCODE -ne 0 -or -not $version -or ($version -join '').Trim() -notmatch '^[0-9A-Za-z][0-9A-Za-z.+_-]{0,199}$') {
        throw 'Could not read the MCP informational version.'
    }
    $version = ($version -join '').Trim()
} finally {
    Pop-Location
}

Copy-Item (Join-Path $source 'LICENSE') (Join-Path $output 'LICENSE') -Force
Set-Content -Path (Join-Path $output 'BUILD.txt') -Value "DashyNMS MCP $commit ($Runtime)" -Encoding utf8
$name = if ($Runtime.StartsWith('win-')) { 'dashynms-mcp.exe' } else { 'dashynms-mcp' }
if (-not $IsWindows -and -not $Runtime.StartsWith('win-')) {
    chmod +x (Join-Path $output $name)
    if ($LASTEXITCODE -ne 0) { throw 'Could not mark the network tools executable.' }
}
Get-Item (Join-Path $output $name) | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

@{ SchemaVersion = 1; Version = $version; Commit = $commit; Runtime = $Runtime;
   Sha256 = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'version.json') -Encoding utf8NoBOM
