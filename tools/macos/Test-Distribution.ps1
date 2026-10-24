$ErrorActionPreference = 'Stop'
$scriptDirectory = $PSScriptRoot
$environmentNames = @('GITHUB_ACTIONS','GITHUB_REF','GITHUB_EVENT_NAME','RUNNER_TEMP',
    'MACOS_CERTIFICATE_BASE64','MACOS_CERTIFICATE_PASSWORD','MACOS_SIGNING_IDENTITY',
    'APPLE_API_KEY','APPLE_API_KEY_ID','APPLE_API_ISSUER_ID')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
$scratch = Join-Path ([IO.Path]::GetTempPath()) "dashynms-distribution-test-$([Guid]::NewGuid().ToString('N'))"
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    New-Item $scratch -ItemType Directory | Out-Null
    $env:GITHUB_ACTIONS = 'true'
    $env:GITHUB_REF = 'refs/heads/master'
    $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
    $env:RUNNER_TEMP = $scratch
    $env:MACOS_CERTIFICATE_BASE64 = [Convert]::ToBase64String([byte[]]@(1,2,3))
    $env:MACOS_CERTIFICATE_PASSWORD = 'synthetic-password'
    $env:MACOS_SIGNING_IDENTITY = 'Developer ID Application: Test (ABCDEFGHIJ)'
    $env:APPLE_API_KEY = 'synthetic-key'
    $env:APPLE_API_KEY_ID = 'TESTKEY'
    $env:APPLE_API_ISSUER_ID = 'test-issuer'

    $testModule = New-Module -ArgumentList $scriptDirectory -ScriptBlock {
        param($Directory)
        $script:IsMacOS = $true
        $global:DashySigningTestState = @{ Calls = [Collections.Generic.List[object]]::new(); Failure = @(); Output = '' }

        . "$Directory/Distribution-Common.ps1"
        function Record([string]$Tool, [object[]]$Arguments) {
            $global:DashySigningTestState.Calls.Add([pscustomobject]@{ Tool=$Tool; Arguments=$Arguments })
            $selectors = @($Tool,"$Tool $($Arguments[0])","$Tool $($Arguments[0]) $($Arguments[1])")
            $global:LASTEXITCODE = if (@($selectors | Where-Object { $_ -in $global:DashySigningTestState.Failure }).Count) { 1 } else { 0 }
        }
        function security {
            Record security $args
            if ($global:DashySigningTestState.Output) {
                Write-Output $global:DashySigningTestState.Output
                Write-Error $global:DashySigningTestState.Output -ErrorAction Continue
            }
        }
        function chmod { Record chmod $args }
        function codesign { Record codesign $args }
        function spctl { Record spctl $args }
        function ditto { Record ditto $args; [IO.File]::WriteAllText($args[-1], 'archive') }
        function hdiutil { Record hdiutil $args; [IO.File]::WriteAllText($args[-1], 'disk image') }
        function xcrun {
            Record xcrun $args
            if ($args[0] -eq 'notarytool' -and $args[1] -eq 'submit') {
                $status = if ($global:DashySigningTestState.Failure -eq 'notary-rejected') { 'Invalid' } else { 'Accepted' }
                @{ status=$status; id='synthetic-submission' } | ConvertTo-Json -Compress
            }
        }
        function Run-Packager($App, $Image, $DiskImage) {
            & "$Directory/Publish-Distribution.ps1" -App $App -ImageDirectory $Image -DiskImage $DiskImage
        }
        function Get-Calls { $global:DashySigningTestState.Calls.ToArray() }
        function Reset-Calls([string[]]$Failure = @(), [string]$Output = '') {
            $global:DashySigningTestState.Calls.Clear()
            $global:DashySigningTestState.Failure = $Failure
            $global:DashySigningTestState.Output = $Output
        }
        Export-ModuleMember -Function Assert-MacOSDistribution,Test-MachO,Invoke-MacTool,Run-Packager,Get-Calls,Reset-Calls
    }
    Import-Module $testModule -Force -DisableNameChecking
    foreach ($invalidRef in @('refs/heads/feature/test','refs/tags/v2.0.0','refs/pull/1/merge')) {
        $env:GITHUB_REF = $invalidRef
        $rejected = $false
        try { Assert-MacOSDistribution } catch { $rejected = $true }
        Require $rejected "Signing accepted $invalidRef."
    }
    $env:GITHUB_REF = 'refs/heads/master'
    $env:GITHUB_EVENT_NAME = 'pull_request'
    $rejected = $false
    try { Assert-MacOSDistribution } catch { $rejected = $true }
    Require $rejected 'Signing accepted a pull request event.'
    $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
    foreach ($name in $environmentNames | Where-Object { $_ -match '^(MACOS_|APPLE_)' }) {
        $value = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, '')
        $rejected = $false
        try { Assert-MacOSDistribution } catch { $rejected = $true }
        Require $rejected "Signing accepted a missing $name."
        [Environment]::SetEnvironmentVariable($name, $value)
    }

    $image = Join-Path $scratch 'image'
    $app = Join-Path $image 'DashyNMS.app'
    $mac = Join-Path $app 'Contents/MacOS'
    New-Item "$mac/mcp" -ItemType Directory -Force | Out-Null
    foreach ($file in @('DashyNMS','dashynms-login','libdashynms-notifications.dylib',
        'mcp/dashynms-mcp','mcp/libcoreclr.dylib','other-native.so')) {
        [IO.File]::WriteAllBytes((Join-Path $mac $file), [byte[]]@(0xCF,0xFA,0xED,0xFE))
    }
    [IO.File]::WriteAllText((Join-Path $mac 'managed.dll'), 'managed code')
    Require (Test-MachO "$mac/other-native.so") 'Mach-O detection depended on a filename extension.'
    Require (-not (Test-MachO "$mac/managed.dll")) 'Managed code was mistaken for Mach-O.'
    $diskImage = Join-Path $scratch 'test.dmg'
    Run-Packager $app $image $diskImage
    $calls = @(Get-Calls)
    $signs = @($calls | Where-Object { $_.Tool -eq 'codesign' -and $_.Arguments -contains '--sign' })
    Require ($signs.Count -eq 8) 'Every native file, app bundle and disk image must be signed.'
    $nestedPaths = @([IO.Path]::GetFullPath("$mac/mcp/dashynms-mcp"),[IO.Path]::GetFullPath("$mac/mcp/libcoreclr.dylib"))
    Require ($signs[0].Arguments[-1] -in $nestedPaths -and $signs[1].Arguments[-1] -in $nestedPaths) 'Nested MCP code was not signed first.'
    Require ($signs[6].Arguments[-1] -eq $app) 'App bundle must seal the nested signatures.'
    Require ($signs[7].Arguments[-1] -eq $diskImage) 'Disk image was not signed last.'
    foreach ($sign in $signs) {
        $needsJit = $sign.Arguments[-1] -in @($app,[IO.Path]::GetFullPath("$mac/DashyNMS"),[IO.Path]::GetFullPath("$mac/mcp/dashynms-mcp"))
        Require (($sign.Arguments -contains '--entitlements') -eq $needsJit) 'JIT entitlement leaked to non-JIT code.'
        Require ($sign.Arguments -contains '--timestamp') 'Distribution signature omitted a secure timestamp.'
    }
    $submissions = @($calls | Where-Object { $_.Tool -eq 'xcrun' -and $_.Arguments[1] -eq 'submit' })
    Require ($submissions.Count -eq 2) 'App and disk image must both be notarised.'
    Require (@(Get-ChildItem $scratch -Directory -Filter 'dashynms-signing-*').Count -eq 0) 'Successful signing left credential files.'
    foreach ($failure in @('codesign','xcrun','security','xcrun notarytool submit','xcrun stapler staple',
        'hdiutil','security delete-keychain','notary-rejected')) {
        Reset-Calls $failure
        $rejected = $false
        try { Run-Packager $app $image $diskImage } catch { $rejected = $true }
        Require $rejected "A failing $failure was ignored."
        Require (@(Get-ChildItem $scratch -Directory -Filter 'dashynms-signing-*').Count -eq 0) "Failed $failure left credential files."
    }
    foreach ($stage in @(
        @{ Command = 'create-keychain'; Operation = 'CreateKeychain' },
        @{ Command = 'set-keychain-settings'; Operation = 'ConfigureKeychain' },
        @{ Command = 'unlock-keychain'; Operation = 'UnlockKeychain' },
        @{ Command = 'import'; Operation = 'ImportCertificate' },
        @{ Command = 'set-key-partition-list'; Operation = 'ConfigureKeyAccess' },
        @{ Command = 'delete-keychain'; Operation = 'DeleteKeychain' }
    )) {
        Reset-Calls "security $($stage.Command)"
        $failureMessage = ''
        try { Run-Packager $app $image $diskImage } catch { $failureMessage = $_.Exception.Message }
        Require ($failureMessage.StartsWith("$($stage.Operation) failed with exit code 1.")) 'A keychain failure lost its operation.'
    }
    foreach ($case in @(
        @{ Native = 'MAC verification failed during PKCS12 import'; Expected = 'PKCS#12 verification failed.' },
        @{ Native = 'Unknown format in import'; Expected = 'The certificate import format was rejected.' },
        @{ Native = 'The user name or passphrase you entered is not correct.'; Expected = 'The keychain rejected its password.' },
        @{ Native = 'The specified item could not be found in the keychain.'; Expected = 'A required keychain item was not found.' },
        @{ Native = 'User interaction is not allowed.'; Expected = 'The keychain requires interaction unavailable on the build runner.' },
        @{ Native = 'unrecognised native error'; Expected = 'Native details withheld' }
    )) {
        Reset-Calls 'security import' ($case.Native + ' synthetic-secret-output')
        $visible = (& {
            try {
                Invoke-MacTool security @('import','synthetic-secret-argument') -Operation ImportCertificate
                throw 'Failed native import was accepted.'
            } catch { $_.Exception.Message }
        } *>&1) -join "`n"
        Require ($visible.Contains('ImportCertificate failed with exit code 1.')) 'Import failure lost its operation.'
        Require ($visible.Contains($case.Expected)) 'Native failure did not receive its fixed diagnostic.'
        Require ($visible -notmatch 'synthetic-secret|unrecognised native error') 'A native diagnostic exposed secret output or arguments.'
    }
    Reset-Calls @('security import','security delete-keychain') 'MAC verification failed during PKCS12 import synthetic-secret-output'
    $visible = (& {
        try { Run-Packager $app $image $diskImage } catch { $_.Exception.Message }
    } *>&1) -join "`n"
    Require ($visible.Contains('ImportCertificate failed with exit code 1.')) 'Cleanup masked the original import failure.'
    Require ($visible.Contains('keychain deletion also failed')) 'The secondary keychain cleanup failure was hidden.'
    Require ($visible -notmatch 'synthetic-secret') 'Failure cleanup exposed native secret output.'
    Require (@(Get-ChildItem $scratch -Directory -Filter 'dashynms-signing-*').Count -eq 0) 'Combined import and keychain cleanup failures left credential files.'
    Reset-Calls -Output 'synthetic-secret-success-output'
    $visible = (Run-Packager $app $image $diskImage *>&1) -join "`n"
    Require ($visible -notmatch 'synthetic-secret') 'A successful keychain operation exposed native output.'
    Write-Host 'Distribution guards, signing, safe diagnostics, notarisation and failure cleanup passed with mocked macOS tools.'
} finally {
    if ($testModule) { Remove-Module $testModule -ErrorAction SilentlyContinue }
    Remove-Variable DashySigningTestState -Scope Global -ErrorAction SilentlyContinue
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name]) }
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ([IO.Path]::GetFileName($resolved) -notmatch '^dashynms-distribution-test-[a-f0-9]{32}$') { throw 'Unexpected test directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
