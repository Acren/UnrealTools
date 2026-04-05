param(
    [string[]]$Include
)

$ErrorActionPreference = 'Stop'

$formatScriptPath = Join-Path $PSScriptRoot 'Format.ps1'

if (-not (Test-Path -LiteralPath $formatScriptPath)) {
    throw "Could not find format script at '$formatScriptPath'."
}

& $formatScriptPath -Verify -Include $Include
exit $LASTEXITCODE
