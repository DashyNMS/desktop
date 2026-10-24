<# Publishes a native package on its matching host. Build MCP for the runtime first. #>
param(
    [Parameter(Mandatory)][ValidateSet('win-x64','osx-x64','osx-arm64','linux-x64','linux-arm64')][string]$Runtime,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$InnoCompiler,
    [switch]$MacOSDistribution
)
$ErrorActionPreference = 'Stop'
if ($MacOSDistribution) {
    . "$PSScriptRoot/macos/Distribution-Common.ps1"
    Assert-MacOSDistribution
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$hostRuntime = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
if ($hostRuntime -ne $Runtime) { throw "Build $Runtime on its native host; this host is $hostRuntime." }
$stage = Join-Path $root "artifacts/package-staging/$Version/$Runtime"
$output = Join-Path $root "artifacts/packages/$Version/$Runtime"
if ((Test-Path $stage) -or (Test-Path $output)) { throw 'This package version already has output. Use a new version or a fresh checkout.' }
$publish = Join-Path $stage 'publish'
New-Item $output -ItemType Directory -Force | Out-Null
$mcpName = if ($IsWindows) { 'dashynms-mcp.exe' } else { 'dashynms-mcp' }
foreach ($file in @($mcpName,'LICENSE','BUILD.txt')) {
    if (-not (Test-Path "$root/artifacts/mcp/$Runtime/$file" -PathType Leaf)) { throw "Build the $Runtime MCP bundle first; missing $file." }
}
function Check-Native([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE." } }
function Download-Verified([string]$url, [string]$path, [string]$sha256) {
    Invoke-WebRequest $url -OutFile $path
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $sha256) { throw "Checksum mismatch for $url" }
}
Push-Location $root
try {
    [string[]]$bundleOptions = if ($IsWindows) {
        @('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true')
    } elseif ($IsMacOS) {
        # Keep managed assemblies inside the app host, with native libraries
        # beside it so each Mach-O file can be signed before sealing the app.
        @('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=false','-p:EnableCompressionInSingleFile=true')
    } else { @('-p:PublishSingleFile=false') }
    dotnet publish src/DesktopNMS.Avalonia -c Release -r $Runtime --self-contained true -m:1 `
        "-p:Version=$Version" @bundleOptions -p:DebugType=none -o $publish --nologo
    Check-Native 'Publish'
    foreach ($file in @($mcpName,'LICENSE','BUILD.txt')) {
        if (-not (Test-Path "$publish/mcp/$file" -PathType Leaf)) { throw "The app is missing mcp/$file." }
    }
    Copy-Item LICENSE "$publish/LICENSE"
    $commit = (git rev-parse HEAD).Trim(); Check-Native 'Read source revision'
    @("DashyNMS $Version ($Runtime)", "Desktop commit: $commit", (Get-Content "$publish/mcp/BUILD.txt" -Raw).Trim()) |
        Set-Content "$publish/BUILD.txt" -Encoding utf8NoBOM

    # Reuse the largest PNG image already embedded in the branded Windows icon.
    $ico = [IO.File]::ReadAllBytes("$root/src/DesktopNMS.Avalonia/Assets/app.ico")
    $bestLength = 0; $bestOffset = 0
    for ($i=0; $i -lt [BitConverter]::ToUInt16($ico,4); $i++) {
        $entry=6+16*$i; $length=[BitConverter]::ToInt32($ico,$entry+8); $offset=[BitConverter]::ToInt32($ico,$entry+12)
        if ($length -gt $bestLength -and $ico[$offset] -eq 137 -and $ico[$offset+1] -eq 80) { $bestLength=$length; $bestOffset=$offset }
    }
    if ($bestLength -eq 0) { throw 'The app icon contains no PNG image.' }
    $png=Join-Path $stage 'dashynms.png'
    [IO.File]::WriteAllBytes($png,[byte[]]$ico[$bestOffset..($bestOffset+$bestLength-1)])

    if ($IsWindows) {
        if (-not $InnoCompiler) { $InnoCompiler=(Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
        if (-not $InnoCompiler) { throw 'Pass -InnoCompiler with the path to Inno Setup 6 ISCC.exe.' }
        & $InnoCompiler "/DAppVersion=$Version" "/DAppSourceDir=$publish" "/O$output" installer/DashyNMS.iss
        Check-Native 'Build Windows installer'
    }
    elseif ($IsMacOS) {
        $image=Join-Path $stage 'disk-image'; $app=Join-Path $image 'DashyNMS.app'; $contents=Join-Path $app 'Contents'
        $mac=Join-Path $contents 'MacOS'; $resources=Join-Path $contents 'Resources'
        New-Item $mac,$resources -ItemType Directory -Force | Out-Null
        # MacOS is a code directory: keep notices in Resources so codesign
        # does not treat the MCP licence and revision files as unsigned code.
        Get-ChildItem $publish | Where-Object Name -NotIn @('mcp','LICENSE','BUILD.txt') |
            ForEach-Object { Copy-Item $_.FullName $mac -Recurse }
        New-Item "$mac/mcp","$resources/mcp" -ItemType Directory -Force | Out-Null
        Get-ChildItem "$publish/mcp" | Where-Object Name -NotIn @('LICENSE','BUILD.txt') |
            ForEach-Object { Copy-Item $_.FullName "$mac/mcp" -Recurse }
        Copy-Item "$publish/mcp/LICENSE","$publish/mcp/BUILD.txt" "$resources/mcp"
        Copy-Item "$publish/LICENSE","$publish/BUILD.txt" $resources
        $architecture=if ($Runtime -eq 'osx-arm64') { 'arm64' } else { 'x86_64' }
        swiftc -swift-version 5 -target "$architecture-apple-macos13" -emit-library tools/macos/Notifications.swift -o "$mac/libdashynms-notifications.dylib"
        Check-Native 'Compile notifications'
        swiftc -target "$architecture-apple-macos13" tools/macos/LoginItem.swift -o "$mac/dashynms-login"
        Check-Native 'Compile login helper'
        $iconset=Join-Path $stage 'DashyNMS.iconset'; New-Item $iconset -ItemType Directory | Out-Null
        foreach ($size in @(16,32,128,256,512)) {
            sips -z $size $size $png --out "$iconset/icon_${size}x${size}.png" | Out-Null; Check-Native 'Build app icon'
            $double=$size*2
            sips -z $double $double $png --out "$iconset/icon_${size}x${size}@2x.png" | Out-Null; Check-Native 'Build retina app icon'
        }
        iconutil -c icns $iconset -o "$resources/DashyNMS.icns"; Check-Native 'Compile app icon'
        $numeric=($Version -split '-')[0]
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>net.dashynms.desktop</string>
<key>CFBundleName</key><string>DashyNMS</string>
<key>CFBundleDisplayName</key><string>DashyNMS</string>
<key>CFBundleExecutable</key><string>DashyNMS</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>DashyNMS.icns</string>
<key>CFBundleShortVersionString</key><string>$numeric</string>
<key>CFBundleVersion</key><string>$numeric</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@ | Set-Content "$contents/Info.plist" -Encoding utf8NoBOM
        chmod +x "$mac/DashyNMS" "$mac/dashynms-login" "$mac/mcp/$mcpName"; Check-Native 'Mark bundle executables'
        New-Item (Join-Path $image 'Applications') -ItemType SymbolicLink -Target /Applications | Out-Null
        $diskImage = "$output/DashyNMS-$Version-$Runtime.dmg"
        if ($MacOSDistribution) {
            & "$PSScriptRoot/macos/Publish-Distribution.ps1" -App $app -ImageDirectory $image -DiskImage $diskImage
        } else {
            foreach ($file in Get-ChildItem $mac -Recurse -File | Where-Object { $_.Extension -eq '.dylib' -or $_.Name -in @('dashynms-login',$mcpName) }) {
                codesign --force --sign - $file.FullName; Check-Native 'Sign native code'
            }
            codesign --force --sign - $app; Check-Native 'Sign app bundle'
            codesign --verify --deep --strict $app; Check-Native 'Verify app signature'
            hdiutil create -volname DashyNMS -srcfolder $image -format UDZO $diskImage; Check-Native 'Create disk image'
        }
    }
    else {
        $app=Join-Path $stage 'DashyNMS.AppDir'; $bin=Join-Path $app 'usr/bin'
        New-Item $bin -ItemType Directory -Force | Out-Null; Copy-Item "$publish/*" $bin -Recurse
        Copy-Item $png "$app/dashynms.png"
        @'
#!/bin/sh
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec "$HERE/usr/bin/DashyNMS" "$@"
'@ | Set-Content "$app/AppRun" -Encoding utf8NoBOM
        @'
[Desktop Entry]
Type=Application
Name=DashyNMS
Exec=DashyNMS
Icon=dashynms
Categories=Network;System;
Terminal=false
StartupWMClass=DashyNMS
'@ | Set-Content "$app/dashynms.desktop" -Encoding utf8NoBOM
        chmod +x "$app/AppRun" "$bin/DashyNMS" "$bin/mcp/$mcpName"; Check-Native 'Mark AppImage executables'
        $architecture=if ($Runtime -eq 'linux-arm64') { 'aarch64' } else { 'x86_64' }
        $toolHash=if ($architecture -eq 'aarch64') { 'f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158' } else { 'ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0' }
        $runtimeHash=if ($architecture -eq 'aarch64') { '00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444' } else { '2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d' }
        Download-Verified "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$architecture.AppImage" "$stage/appimagetool" $toolHash
        Download-Verified "https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-$architecture" "$stage/runtime" $runtimeHash
        chmod +x "$stage/appimagetool"; Check-Native 'Mark package builder executable'
        Push-Location $stage
        try {
            & ./appimagetool --appimage-extract | Out-Null; Check-Native 'Extract package builder'
            $env:ARCH=$architecture
            & ./squashfs-root/AppRun --runtime-file "$stage/runtime" $app "$output/DashyNMS-$Version-$Runtime.AppImage"
            Check-Native 'Create AppImage'
        } finally { Pop-Location }
    }
    foreach ($file in Get-ChildItem $output -File) {
        $hash=(Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($file.Name)" | Set-Content "$($file.FullName).sha256" -Encoding utf8NoBOM
    }
    Copy-Item "$publish/BUILD.txt" "$output/BUILD-$Runtime.txt"
    Write-Host "Native package: $output"
} finally { Pop-Location }
