# Signing changes the executable. Run after signing nested code, before sealing the app.
function Update-McpVersionHash([string]$Executable, [string]$Metadata) {
    $data = Get-Content -LiteralPath $Metadata -Raw | ConvertFrom-Json
    if ($data.SchemaVersion -ne 1) { throw 'Unsupported MCP build metadata.' }
    $data.Sha256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    $data | ConvertTo-Json | Set-Content -LiteralPath $Metadata -Encoding utf8NoBOM
}
