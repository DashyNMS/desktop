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
        $global:DashySigningTestState = @{ Calls = [Collections.Generic.List[object]]::new(); Failure = @(); Output = ''; Identity = 'Valid'; SearchListWrites = 0 }

        . "$Directory/Distribution-Common.ps1"
        function Record([string]$Tool, [object[]]$Arguments) {
            $global:DashySigningTestState.Calls.Add([pscustomobject]@{ Tool=$Tool; Arguments=$Arguments })
            $selectors = @($Tool,"$Tool $($Arguments[0])","$Tool $($Arguments[0]) $($Arguments[1])")
            if ($Tool -eq 'security' -and $Arguments[0] -eq 'list-keychains' -and $Arguments -contains '-s') {
                $selectors += if ($global:DashySigningTestState.SearchListWrites -eq 0) { 'security configure-search-list' } else { 'security restore-search-list' }
            }
            $global:LASTEXITCODE = if (@($selectors | Where-Object { $_ -in $global:DashySigningTestState.Failure }).Count) { 1 } else { 0 }
        }
        function security {
            Record security $args
            if ($args[0] -eq 'list-keychains') {
                if ($args -contains '-s') { $global:DashySigningTestState.SearchListWrites++ } else {
                    '    "/Users/runner/Library/Keychains/login.keychain-db"'
                    '    "/Library/Keychains/System.keychain"'
                }
            }
            if ($args[0] -eq 'find-identity' -and $global:LASTEXITCODE -eq 0) {
                $scenario = $global:DashySigningTestState.Identity
                $validOnly = $args -contains '-v'
                if ($scenario -ne 'Missing' -and -not ($scenario -eq 'Untrusted' -and $validOnly)) {
                    $identityName = if ($scenario -eq 'Mismatch') { 'Developer ID Application: Other (ABCDEFGHIJ)' } else { $env:MACOS_SIGNING_IDENTITY }
                    '  1) ' + ('A' * 40) + ' "' + $identityName + '"'
                    if ($scenario -eq 'Ambiguous') { '  2) ' + ('B' * 40) + ' "' + $identityName + '"' }
                }
            }
            if ($global:DashySigningTestState.Output) {
                Write-Output $global:DashySigningTestState.Output
                Write-Error $global:DashySigningTestState.Output -ErrorAction Continue
            }
        }
        function chmod { Record chmod $args }
        function codesign {
            Record codesign $args
            if ($global:LASTEXITCODE -ne 0 -and $global:DashySigningTestState.Output) {
                Write-Output $global:DashySigningTestState.Output
                Write-Error $global:DashySigningTestState.Output -ErrorAction Continue
            }
        }
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
        function Reset-Calls([string[]]$Failure = @(), [string]$Output = '', [string]$Identity = 'Valid') {
            $global:DashySigningTestState.Calls.Clear()
            $global:DashySigningTestState.Failure = $Failure
            $global:DashySigningTestState.Output = $Output
            $global:DashySigningTestState.Identity = $Identity
            $global:DashySigningTestState.SearchListWrites = 0
        }
        Export-ModuleMember -Function Assert-MacOSDistribution,Test-MachO,Invoke-MacTool,Resolve-MacSigningIdentity,Protect-MacCodeSigningDiagnostic,Run-Packager,Get-Calls,Reset-Calls
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
        Require ($sign.Arguments[$sign.Arguments.IndexOf('--sign') + 1] -eq ('A' * 40)) 'Signing did not use the validated certificate fingerprint.'
    }
    $intermediateImports = @($calls | Where-Object { $_.Tool -eq 'security' -and $_.Arguments[0] -eq 'import' -and $_.Arguments[1] -like '*.cer' })
    Require ($intermediateImports.Count -eq 2) 'Both Apple Developer ID intermediate issuers must be available in the temporary keychain.'
    Require (-not ($calls | Where-Object { $_.Arguments -contains 'add-trusted-cert' })) 'Certificate preparation must not override trust.'
    $searchListWrites = @($calls | Where-Object { $_.Tool -eq 'security' -and $_.Arguments[0] -eq 'list-keychains' -and $_.Arguments -contains '-s' })
    Require ($searchListWrites.Count -eq 2) 'The signing keychain search list must be configured and restored.'
    Require ($searchListWrites[0].Arguments[4] -eq '/Users/runner/Library/Keychains/login.keychain-db' -and
        $searchListWrites[0].Arguments[5] -eq '/Library/Keychains/System.keychain' -and $searchListWrites[0].Arguments.Count -eq 7) 'Signing replaced existing keychain search entries.'
    Require ($searchListWrites[1].Arguments.Count -eq 6 -and $searchListWrites[1].Arguments[4] -eq $searchListWrites[0].Arguments[4] -and
        $searchListWrites[1].Arguments[5] -eq $searchListWrites[0].Arguments[5]) 'Cleanup did not restore the original keychain search entries.'
    $deleteCall = @($calls | Where-Object { $_.Tool -eq 'security' -and $_.Arguments[0] -eq 'delete-keychain' })[0]
    Require ([array]::IndexOf($calls,$searchListWrites[1]) -lt [array]::IndexOf($calls,$deleteCall)) 'The temporary keychain was deleted before restoring its search list.'
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
        $writes = @(Get-Calls | Where-Object { $_.Tool -eq 'security' -and $_.Arguments[0] -eq 'list-keychains' -and $_.Arguments -contains '-s' })
        if ($writes.Count) { Require ($writes.Count -eq 2 -and $writes[-1].Arguments.Count -eq 6) "Failed $failure did not restore the original keychain search list." }
    }
    foreach ($stage in @(
        @{ Command = 'create-keychain'; Operation = 'CreateKeychain' },
        @{ Command = 'set-keychain-settings'; Operation = 'ConfigureKeychain' },
        @{ Command = 'unlock-keychain'; Operation = 'UnlockKeychain' },
        @{ Command = 'import'; Operation = 'ImportCertificate' },
        @{ Command = 'set-key-partition-list'; Operation = 'ConfigureKeyAccess' },
        @{ Command = 'delete-keychain'; Operation = 'DeleteKeychain' },
        @{ Command = 'find-identity'; Operation = 'ValidateSigningIdentity' },
        @{ Command = 'list-keychains'; Operation = 'ReadKeychainSearchList' },
        @{ Command = 'configure-search-list'; Operation = 'ConfigureKeychainSearchList' },
        @{ Command = 'restore-search-list'; Operation = 'RestoreKeychainSearchList' }
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
    Reset-Calls @('security import','security restore-search-list')
    $visible = (& { try { Run-Packager $app $image $diskImage } catch { $_.Exception.Message } } *>&1) -join "`n"
    Require ($visible.Contains('ImportCertificate failed with exit code 1.')) 'Search-list cleanup masked the original signing failure.'
    Require ($visible.Contains('original keychain search list failed')) 'Search-list restoration failure was hidden.'
    Require (@(Get-ChildItem $scratch -Directory -Filter 'dashynms-signing-*').Count -eq 0) 'Search-list restoration failure left credential files.'
    Reset-Calls -Output 'synthetic-secret-success-output'
    $visible = (Run-Packager $app $image $diskImage *>&1) -join "`n"
    Require ($visible -notmatch 'synthetic-secret') 'A successful keychain operation exposed native output.'
    foreach ($scenario in @(
        @{ Identity = 'Missing'; Expected = 'No signing identity was imported.' },
        @{ Identity = 'Mismatch'; Expected = 'does not exactly match' },
        @{ Identity = 'Untrusted'; Expected = 'not valid for code signing' },
        @{ Identity = 'Ambiguous'; Expected = 'Multiple valid certificates' }
    )) {
        Reset-Calls -Identity $scenario.Identity
        $failureMessage = ''
        try { Run-Packager $app $image $diskImage } catch { $failureMessage = $_.Exception.Message }
        Require ($failureMessage.Contains($scenario.Expected)) 'Invalid signing identity did not receive its expected diagnostic.'
        Require (@(Get-Calls | Where-Object { $_.Tool -eq 'codesign' }).Count -eq 0) 'An invalid identity reached code signing.'
    }
    Reset-Calls
    Require ((Resolve-MacSigningIdentity -Keychain 'test.keychain') -eq ('A' * 40)) 'A valid configured identity did not resolve to its fingerprint.'
    $env:APPLE_API_KEY_ID = 'secret-one'
    $env:APPLE_API_ISSUER_ID = 'secret-two'
    $nativeOutput = 'unable to build chain to self-signed root for signer ' + $env:MACOS_SIGNING_IDENTITY +
        ' secret-one secret-two ' + $env:MACOS_CERTIFICATE_PASSWORD + ' target.dylib: errSecInternalComponent'
    Reset-Calls codesign $nativeOutput
    $visible = (& { try { Run-Packager $app $image $diskImage } catch { $_.Exception.Message } } *>&1) -join "`n"
    Require ($visible.Contains('could not reach an Apple root')) 'Certificate-chain errors did not receive clear guidance.'
    Require ($visible.Contains('target.dylib: errSecInternalComponent')) 'Code-signing diagnostics hid the native failure.'
    Require ($visible -notmatch 'secret-one|secret-two|synthetic-password|Developer ID Application: Test') 'A code-signing diagnostic exposed a configured credential.'
    $env:APPLE_API_KEY = "private-key-line-one`nprivate-key-line-two"
    $protected = Protect-MacCodeSigningDiagnostic @("new native error`nprivate-key-line-one`nprivate-key-line-two", ('x' * 3000))
    Require ($protected.Contains('new native error')) 'Unknown code-signing errors were hidden.'
    Require ($protected -notmatch 'private-key-line|\r|\n') 'Diagnostic redaction exposed key lines or control characters.'
    Require ($protected.Length -lt 2100 -and $protected.Contains('[truncated]')) 'Native diagnostics were not bounded.'
    Write-Host 'Distribution guards, signing, safe diagnostics, notarisation and failure cleanup passed with mocked macOS tools.'
} finally {
    if ($testModule) { Remove-Module $testModule -ErrorAction SilentlyContinue }
    Remove-Variable DashySigningTestState -Scope Global -ErrorAction SilentlyContinue
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name]) }
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ([IO.Path]::GetFileName($resolved) -notmatch '^dashynms-distribution-test-[a-f0-9]{32}$') { throw 'Unexpected test directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
