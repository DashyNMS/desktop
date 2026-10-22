<#
.SYNOPSIS
    Tests and publishes a versioned Windows Avalonia development build.
.EXAMPLE
    ./tools/Publish-Mcp.ps1 -Source artifacts/mcp-dev-source
    ./tools/Publish-Dev.ps1
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the desktop revision.' }
    if (-not $Version) {
        [xml]$props = Get-Content Directory.Build.props
        $Version = "$($props.Project.PropertyGroup.Version)-dev.$(Get-Date -Format yyyyMMddHHmmss).g$($commit.Substring(0, 8))"
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+-dev\.[0-9A-Za-z.-]+$') {
        throw 'Use a development version such as 2.0.0-dev.42.'
    }
    foreach ($file in @('dashynms-mcp.exe', 'LICENSE', 'BUILD.txt')) {
        if (-not (Test-Path "artifacts/mcp/$file" -PathType Leaf)) {
            throw "Missing artifacts/mcp/$file. Run tools/Publish-Mcp.ps1 first."
        }
    }
    $output = Join-Path $root "artifacts/dev/$Version/win-x64"
    if (Test-Path $output) { throw "A build already exists at $output." }

    dotnet test tests/DesktopNMS.Presentation.Tests -c Release -m:1 --nologo -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw 'Presentation tests failed.' }
    dotnet publish src/DesktopNMS.Avalonia/DesktopNMS.Avalonia.csproj -c Release -r win-x64 --self-contained true `
        -m:1 -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the development build failed.' }
    foreach ($file in @('DashyNMS.exe', 'mcp/dashynms-mcp.exe', 'mcp/LICENSE', 'mcp/BUILD.txt')) {
        if (-not (Test-Path (Join-Path $output $file) -PathType Leaf)) { throw "Build is missing $file." }
    }
    $dirty = [bool](git status --porcelain --untracked-files=no)
    @(
        "DashyNMS $Version (Windows x64, self-contained)"
        "Desktop commit: $commit"
        "Tracked working-tree changes: $dirty"
        "Built UTC: $([DateTime]::UtcNow.ToString('O'))"
        'Validation: Presentation Release tests passed.'
        (Get-Content artifacts/mcp/BUILD.txt -Raw).Trim()
    ) | Set-Content (Join-Path $output 'BUILD.txt') -Encoding utf8
    Copy-Item LICENSE (Join-Path $output 'LICENSE')
    Write-Host "Development build: $output"
} finally { Pop-Location }
