<#
.SYNOPSIS
    Builds the DashyNMS MCP server for bundling with the desktop app.

.DESCRIPTION
    Publishes DashyNMS/mcp's server as a self-contained native executable
    into artifacts/mcp/<runtime>, where DesktopNMS.Avalonia.csproj picks
    the matching runtime up and copies it beside the app in mcp/.
    Needs the .NET 10 SDK.

.EXAMPLE
    ./scripts/Publish-Mcp.ps1 -Source ..\mcp
    ./scripts/Publish-Mcp.ps1 -Source ..\mcp -Ref v1.1.0
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
    # Uncompressed on macOS, as for the app (Publish-Package.ps1): compressed
    # single-file apps on Apple silicon can crash when starting a child process
    # with redirected output (dotnet/runtime#123324).
    $compression = if ($Runtime.StartsWith('osx-')) { 'false' } else { 'true' }
    # Without the HTTP transport: the app only uses stdio, so ASP.NET Core would
    # be installer weight, and a way to open a port that nothing here needs.
    # Resolve the MCP repo's .NET 10 global.json rather than the desktop SDK.
    dotnet publish (Join-Path $source 'src\DashyNMS.Mcp\DashyNMS.Mcp.csproj') `
        -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true $nativeLibraries "-p:EnableCompressionInSingleFile=$compression" `
        -p:McpHttpTransport=false -p:DebugType=none "-p:SourceRevisionId=$commit" `
        -o $output --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publishing the MCP server failed." }
    $version = dotnet msbuild (Join-Path $source 'src/DashyNMS.Mcp/DashyNMS.Mcp.csproj') `
        -nologo -verbosity:quiet -target:GenerateAssemblyInfo -getProperty:InformationalVersion `
        -property:Configuration=Release -property:McpHttpTransport=false "-property:SourceRevisionId=$commit"
    if ($LASTEXITCODE -ne 0 -or -not $version -or ($version -join '').Trim() -notmatch '^[0-9A-Za-z][0-9A-Za-z.+_-]{0,199}$') {
        throw 'Could not read the MCP informational version.'
    }
    $version = ($version -join '').Trim()
} finally {
    Pop-Location
}

Copy-Item (Join-Path $source 'LICENSE') (Join-Path $output 'LICENSE') -Force
Copy-Item (Join-Path $source 'NOTICES.md') (Join-Path $output 'NOTICES.md') -Force

# Legal files for what the bundle redistributes, in third-party/: every
# restored package that puts runtime files in the executable, checked by the
# MCP repo's own collector, and the self-contained .NET runtime's notices.
# Build-only packages (the trimming analyzers) ship nothing, so they're left out.
$python = @('python3', 'python') | Where-Object {
    (Get-Command $_ -ErrorAction SilentlyContinue) -and ((& $_ --version 2>&1) -match '^Python 3\.')
} | Select-Object -First 1
if (-not $python) { throw 'Python 3 is needed to collect the network tools'' dependency notices.' }
$legal = Join-Path ([IO.Path]::GetTempPath()) "dashynms-mcp-legal-$([guid]::NewGuid().ToString('N'))"
New-Item $legal -ItemType Directory | Out-Null
try {
    # From git rather than the working tree: a CRLF checkout fails the supplements' hashes.
    $archive = Join-Path $legal 'legal.zip'
    git -C $source -c core.autocrlf=false archive --format=zip -o $archive HEAD scripts/collect-dependency-notices.py docs/dependency-licences
    if ($LASTEXITCODE -ne 0) { throw "Couldn't read the MCP repo's licence evidence." }
    Expand-Archive -LiteralPath $archive -DestinationPath $legal

    $assets = Get-Content -Raw (Join-Path $source 'src/DashyNMS.Mcp/obj/project.assets.json') | ConvertFrom-Json -AsHashtable
    $target = $assets.targets.Keys | Where-Object { $_.EndsWith("/$Runtime") } | Select-Object -First 1
    if (-not $target) { throw "The MCP restore has no $Runtime target." }
    foreach ($library in @($assets.libraries.Keys)) {
        $entry = $assets.targets[$target][$library]
        $shipped = foreach ($kind in 'runtime', 'native', 'resource', 'runtimeTargets') {
            if ($entry -and $entry[$kind]) { $entry[$kind].Keys | Where-Object { -not $_.EndsWith('/_._') } }
        }
        if (-not $shipped) { $assets.libraries.Remove($library) }
    }
    $shippedAssets = Join-Path $legal 'project.assets.json'
    $assets | ConvertTo-Json -Depth 64 -Compress | Set-Content -LiteralPath $shippedAssets -Encoding utf8NoBOM
    # UTF-8 mode: -I ignores PYTHONUTF8, and a NuGet cache under a non-ASCII user name must still resolve.
    & $python -I -X utf8 (Join-Path $legal 'scripts/collect-dependency-notices.py') $shippedAssets (Join-Path $output 'third-party') (Join-Path $legal 'docs/dependency-licences')
    if ($LASTEXITCODE -ne 0) { throw 'The network tools'' dependency notices are incomplete.' }

    $framework = $assets.project.frameworks.Values | Select-Object -First 1
    foreach ($reference in $framework.frameworkReferences.Keys) {
        $pack = "$reference.Runtime.$Runtime"
        $download = $framework.downloadDependencies | Where-Object { $_.name -eq $pack } | Select-Object -First 1
        if (-not $download -or $download.version -notmatch '^\[([^,\]]+)') { throw "The MCP restore doesn't record the $pack version." }
        $packFolder = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ "$($pack.ToLowerInvariant())/$($Matches[1])" } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $packFolder) { throw "The $pack $($Matches[1]) runtime pack isn't in the NuGet cache." }
        $runtimeNotices = New-Item (Join-Path $output "third-party/$($pack.ToLowerInvariant())") -ItemType Directory -Force
        foreach ($file in 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
            Copy-Item (Join-Path $packFolder $file) $runtimeNotices
        }
    }
} finally {
    Remove-Item $legal -Recurse -Force -ErrorAction SilentlyContinue
}

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
