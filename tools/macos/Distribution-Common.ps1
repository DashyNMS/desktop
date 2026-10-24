function Assert-MacOSDistribution {
    if (-not $IsMacOS) { throw 'Developer ID distribution requires macOS.' }
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REF -ne 'refs/heads/master' -or
        $env:GITHUB_EVENT_NAME -ne 'workflow_dispatch') {
        throw 'Developer ID distribution requires a manually dispatched master build.'
    }
    foreach ($name in @('MACOS_CERTIFICATE_BASE64','MACOS_CERTIFICATE_PASSWORD','MACOS_SIGNING_IDENTITY',
        'APPLE_API_KEY','APPLE_API_KEY_ID','APPLE_API_ISSUER_ID')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Missing distribution credential: $name."
        }
    }
    if ($env:MACOS_SIGNING_IDENTITY -notmatch '^Developer ID Application: .+ \([A-Z0-9]{10}\)$') {
        throw 'Use a Developer ID Application signing identity.'
    }
}

function Test-MachO([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $bytes = [byte[]]::new(4)
        if ($stream.Read($bytes, 0, 4) -ne 4) { return $false }
        return [Convert]::ToHexString($bytes) -in @('FEEDFACE','CEFAEDFE','FEEDFACF','CFFAEDFE',
            'CAFEBABE','BEBAFECA','CAFEBABF','BFBAFECA')
    } finally { $stream.Dispose() }
}

function Invoke-MacTool(
    [string]$Command,
    [string[]]$Arguments,
    [Parameter(Mandatory)]
    [ValidateSet('SetPermissions','CreateKeychain','ConfigureKeychain','UnlockKeychain','ImportCertificate',
        'ConfigureKeyAccess','StoreNotaryCredentials','SignCode','VerifySignature','ArchiveApp','StapleTicket',
        'ValidateTicket','AssessTrust','CreateDiskImage','DeleteKeychain')]
    [string]$Operation
) {
    Write-Host "macOS distribution: $Operation."
    $result = & $Command @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $reason = 'Native details withheld because this operation handles signing credentials.'
        if ($Command -eq 'security') {
            $diagnostics = @(
                @{ Pattern = 'MAC verification failed'; Message = 'PKCS#12 verification failed. Check the certificate password and export compatibility.' },
                @{ Pattern = 'Unknown format in import|unknown format|unable to decode'; Message = 'The certificate import format was rejected. Check the PKCS#12 export.' },
                @{ Pattern = 'user name or passphrase.*not correct|password.*not correct'; Message = 'The keychain rejected its password.' },
                @{ Pattern = 'specified item could not be found|no keys found'; Message = 'A required keychain item was not found. Check that the certificate export includes its private key.' },
                @{ Pattern = 'interaction is not allowed'; Message = 'The keychain requires interaction unavailable on the build runner.' }
            )
            foreach ($diagnostic in $diagnostics) {
                if (($result -join "`n") -match $diagnostic.Pattern) {
                    $reason = $diagnostic.Message
                    break
                }
            }
        }
        throw "$Operation failed with exit code $exitCode. $reason"
    }
    return $result
}
