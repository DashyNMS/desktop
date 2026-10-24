param(
    [Parameter(Mandatory)][string]$App,
    [Parameter(Mandatory)][string]$ImageDirectory,
    [Parameter(Mandatory)][string]$DiskImage
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Distribution-Common.ps1"
Assert-MacOSDistribution
$temporaryRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
$temporary = Join-Path $temporaryRoot "dashynms-signing-$([Guid]::NewGuid().ToString('N'))"
$keychain = Join-Path $temporary 'signing.keychain-db'
$keychainCreated = $false
$keychainPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$profile = 'dashynms-notarisation'
function Submit-Notarisation([string]$Path) {
    $response = & xcrun notarytool submit $Path --keychain-profile $profile `
        --keychain $keychain --wait --timeout 20m --output-format json 2>&1
    $notaryExitCode = $LASTEXITCODE
    try { $submission = ($response -join "`n") | ConvertFrom-Json } catch {
        throw "Apple notarisation returned no usable result. Exit code: $notaryExitCode."
    }
    if ($notaryExitCode -ne 0 -or $submission.status -ne 'Accepted') {
        throw "Apple notarisation failed. Submission: $($submission.id). Status: $($submission.status). Exit code: $notaryExitCode."
    }
}
try {
    New-Item $temporary -ItemType Directory | Out-Null
    Invoke-MacTool chmod @('700',$temporary) | Out-Null
    $certificate = Join-Path $temporary 'certificate.p12'
    $apiKey = Join-Path $temporary 'AuthKey.p8'
    [IO.File]::WriteAllBytes($certificate,[Convert]::FromBase64String($env:MACOS_CERTIFICATE_BASE64))
    [IO.File]::WriteAllText($apiKey,$env:APPLE_API_KEY,[Text.UTF8Encoding]::new($false))
    Invoke-MacTool chmod @('600',$certificate,$apiKey) | Out-Null
    Invoke-MacTool security @('create-keychain','-p',$keychainPassword,$keychain) | Out-Null
    $keychainCreated = $true
    Invoke-MacTool security @('set-keychain-settings','-lut','21600',$keychain) | Out-Null
    Invoke-MacTool security @('unlock-keychain','-p',$keychainPassword,$keychain) | Out-Null
    # security requires these passwords as process arguments. Never echo its arguments or output.
    Invoke-MacTool security @('import',$certificate,'-k',$keychain,'-P',$env:MACOS_CERTIFICATE_PASSWORD,
        '-T','/usr/bin/codesign','-T','/usr/bin/security') | Out-Null
    Invoke-MacTool security @('set-key-partition-list','-S','apple-tool:,apple:,codesign:',
        '-s','-k',$keychainPassword,$keychain) | Out-Null
    Invoke-MacTool xcrun @('notarytool','store-credentials',$profile,'--key',$apiKey,
        '--key-id',$env:APPLE_API_KEY_ID,'--issuer',$env:APPLE_API_ISSUER_ID,'--keychain',$keychain) | Out-Null
    Remove-Item -LiteralPath $certificate,$apiKey

    $appHost = [IO.Path]::GetFullPath((Join-Path $App 'Contents/MacOS/DashyNMS'))
    $mcpHost = [IO.Path]::GetFullPath((Join-Path $App 'Contents/MacOS/mcp/dashynms-mcp'))
    $nativeFiles = @(Get-ChildItem $App -Recurse -File | Where-Object { -not $_.LinkType -and (Test-MachO $_.FullName) } |
        Sort-Object { ($_.FullName -split '[/\\]').Count } -Descending)
    if ($nativeFiles.Count -eq 0) { throw 'The app contains no Mach-O code.' }
    foreach ($file in $nativeFiles) {
        $sign = @('--force','--sign',$env:MACOS_SIGNING_IDENTITY,'--keychain',$keychain,'--options','runtime','--timestamp')
        if ($file.FullName -in @($appHost,$mcpHost)) { $sign += @('--entitlements',"$PSScriptRoot/App.entitlements.plist") }
        Invoke-MacTool codesign ($sign + $file.FullName) | Out-Null
    }
    Invoke-MacTool codesign @('--force','--sign',$env:MACOS_SIGNING_IDENTITY,'--keychain',$keychain,
        '--options','runtime','--timestamp','--entitlements',"$PSScriptRoot/App.entitlements.plist",$App) | Out-Null
    Invoke-MacTool codesign @('--verify','--deep','--strict',$App) | Out-Null

    $archive = Join-Path $temporary 'DashyNMS.zip'
    Invoke-MacTool ditto @('-c','-k','--sequesterRsrc','--keepParent',$App,$archive) | Out-Null
    Submit-Notarisation $archive
    Invoke-MacTool xcrun @('stapler','staple',$App) | Out-Null
    Invoke-MacTool xcrun @('stapler','validate',$App) | Out-Null
    Invoke-MacTool spctl @('--assess','--type','execute','--verbose=2',$App) | Out-Null

    Invoke-MacTool hdiutil @('create','-volname','DashyNMS','-srcfolder',$ImageDirectory,'-format','UDZO',$DiskImage) | Out-Null
    Invoke-MacTool codesign @('--force','--sign',$env:MACOS_SIGNING_IDENTITY,'--keychain',$keychain,'--timestamp',$DiskImage) | Out-Null
    Submit-Notarisation $DiskImage
    Invoke-MacTool xcrun @('stapler','staple',$DiskImage) | Out-Null
    Invoke-MacTool xcrun @('stapler','validate',$DiskImage) | Out-Null
    Invoke-MacTool codesign @('--verify','--strict',$DiskImage) | Out-Null
    Invoke-MacTool spctl @('--assess','--type','open','--context','context:primary-signature','--verbose=2',$DiskImage) | Out-Null
} finally {
    try {
        if ($keychainCreated) { Invoke-MacTool security @('delete-keychain',$keychain) | Out-Null }
    } finally {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
        if (-not $resolvedTemporary.StartsWith($temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar) -or
            [IO.Path]::GetFileName($resolvedTemporary) -notmatch '^dashynms-signing-[a-f0-9]{32}$') {
            throw 'Refusing to clean an unexpected signing directory.'
        }
        if (Test-Path -LiteralPath $resolvedTemporary) { Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force }
        $keychainPassword = $null
    }
}
