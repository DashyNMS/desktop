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

function Resolve-MacSigningIdentity([Parameter(Mandatory)][string]$Keychain) {
    $identities = @(Invoke-MacTool security @('find-identity','-p','codesigning',$Keychain) -Operation ValidateSigningIdentity)
    $validIdentities = @(Invoke-MacTool security @('find-identity','-v','-p','codesigning',$Keychain) -Operation ValidateSigningIdentity)
    $parsed = @{}
    foreach ($entry in @(@{ Name='All'; Lines=$identities }, @{ Name='Valid'; Lines=$validIdentities })) {
        $parsed[$entry.Name] = @(
            foreach ($line in $entry.Lines) {
                if ([string]$line -match '^\s*\d+\)\s+(?<Fingerprint>[A-Fa-f0-9]{40})\s+"(?<Identity>.*)"(?:\s+\(.*\))?\s*$') {
                    [pscustomobject]@{ Fingerprint=$Matches.Fingerprint.ToUpperInvariant(); Identity=$Matches.Identity }
                }
            }
        )
    }
    if ($parsed.All.Count -eq 0) {
        throw 'No signing identity was imported. Check that the certificate export includes its private key.'
    }
    $matching = @($parsed.All | Where-Object { $_.Identity -ceq $env:MACOS_SIGNING_IDENTITY })
    if ($matching.Count -eq 0) {
        throw 'The configured signing identity does not exactly match an imported certificate name.'
    }
    $fingerprints = @($parsed.Valid | Where-Object { $_.Identity -ceq $env:MACOS_SIGNING_IDENTITY } |
        Select-Object -ExpandProperty Fingerprint -Unique)
    if ($fingerprints.Count -eq 0) {
        throw 'The imported signing identity is not valid for code signing. Check certificate dates, its Apple intermediate issuer and trust.'
    }
    if ($fingerprints.Count -ne 1) {
        throw 'Multiple valid certificates match the configured signing identity. Export only the intended identity.'
    }
    Write-Host 'macOS distribution: the configured signing identity is present and valid.'
    return $fingerprints[0]
}

function Protect-MacCodeSigningDiagnostic([object[]]$Output) {
    $diagnostic = $Output -join "`n"
    $secretParts = @(
        foreach ($name in @('MACOS_CERTIFICATE_BASE64','MACOS_CERTIFICATE_PASSWORD','MACOS_SIGNING_IDENTITY',
            'APPLE_API_KEY','APPLE_API_KEY_ID','APPLE_API_ISSUER_ID')) {
            $value = [Environment]::GetEnvironmentVariable($name)
            if (-not [string]::IsNullOrEmpty($value)) {
                $value
                $value -split '\r?\n' | Where-Object { -not [string]::IsNullOrEmpty($_) }
            }
        }
    )
    foreach ($secret in ($secretParts | Select-Object -Unique | Sort-Object Length -Descending)) {
        $diagnostic = $diagnostic.Replace($secret, '[redacted]')
    }
    $diagnostic = $diagnostic -replace '\x1B\[[0-?]*[ -/]*[@-~]', '' -replace '[\p{C}\s]+', ' '
    $diagnostic = $diagnostic.Trim()
    if ($diagnostic.Length -gt 2048) { $diagnostic = $diagnostic.Substring(0,2048) + ' [truncated]' }
    if ([string]::IsNullOrEmpty($diagnostic)) { return 'codesign returned no diagnostic text.' }
    return $diagnostic
}

function Invoke-MacTool(
    [string]$Command,
    [string[]]$Arguments,
    [Parameter(Mandatory)]
    [ValidateSet('SetPermissions','CreateKeychain','ConfigureKeychain','UnlockKeychain','ImportCertificate',
        'ValidateSigningIdentity','ImportIntermediateCertificate',
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
        } elseif ($Command -eq 'codesign') {
            $diagnostics = @(
                @{ Pattern = 'unable to build chain to self-signed root'; Message = 'The signing certificate chain could not reach an Apple root. Check the Developer ID intermediate issuer.' },
                @{ Pattern = 'CSSMERR_TP_CERT_EXPIRED|certificate.*expired'; Message = 'The signing certificate has expired.' },
                @{ Pattern = 'no identity found|specified item could not be found'; Message = 'The requested signing identity was not found in the selected keychain.' },
                @{ Pattern = 'ambiguous'; Message = 'The requested signing identity is ambiguous.' },
                @{ Pattern = 'interaction is not allowed'; Message = 'Signing requires keychain interaction unavailable on the build runner.' },
                @{ Pattern = 'errSecInternalComponent'; Message = 'The keychain could not complete signing. Check identity trust and unattended private-key access.' },
                @{ Pattern = 'resource fork|Finder information'; Message = 'The signing target contains unsupported extended metadata.' },
                @{ Pattern = 'bundle format unrecognized|invalid or unsuitable'; Message = 'The signing target format was rejected.' },
                @{ Pattern = 'timestamp.*(not available|unavailable)|timestamp service'; Message = 'The secure timestamp service could not complete the request.' }
            )
            $reason = 'Code signing failed.'
            foreach ($diagnostic in $diagnostics) {
                if (($result -join "`n") -match $diagnostic.Pattern) {
                    $reason = $diagnostic.Message
                    break
                }
            }
            $reason += ' codesign diagnostic: ' + (Protect-MacCodeSigningDiagnostic $result)
        }
        throw "$Operation failed with exit code $exitCode. $reason"
    }
    return $result
}
