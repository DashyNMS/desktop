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

function Invoke-MacTool([string]$Command, [string[]]$Arguments) {
    $result = & $Command @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
    return $result
}
