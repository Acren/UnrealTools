param(
    # Default to the full solution so one command exercises every test project unless the caller narrows it.
    [string]$Target = 'UnrealAutomationTools.sln',

    # Allows one focused rerun without editing the script. Example: FullyQualifiedName~ExecutionPlanSchedulerTests.Foo
    [string]$Filter,

    # Keeps normal reruns lean by default. Pass -NoBuild:$false when you need a rebuild.
    [bool]$NoBuild = $true,

    # Keeps normal reruns lean by default. Pass -NoRestore:$false when you need a restore.
    [bool]$NoRestore = $true,

    # Uses the built-in hang collector so test-host stalls fail with diagnostics instead of appearing to run forever.
    [int]$HangTimeoutSeconds = 20,

    # Leaves the default console noise low while still surfacing failures quickly.
    [string]$Verbosity = 'minimal',

    # Keeps solution-level test project execution sequential by default so failures are easier to interpret and one noisy
    # test host cannot obscure another project's progress.
    [int]$MaxCpuCount = 1,

    # Writes TRX results for per-test durations and post-run failure inspection.
    [string]$ResultsDirectory = 'TestResults',

    # When provided, writes one explicit TRX file name. Leave empty for the default per-test-project file naming.
    [string]$LogFileName = ''
)

$ErrorActionPreference = 'Stop'

# Resolve paths relative to the repository root so callers can launch the script from anywhere.
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$targetPath = Join-Path $repositoryRoot $Target
$resolvedResultsDirectory = Join-Path $repositoryRoot $ResultsDirectory

if (-not (Test-Path -LiteralPath $targetPath)) {
    throw "Could not find test target at '$targetPath'."
}

if ($HangTimeoutSeconds -le 0) {
    throw 'HangTimeoutSeconds must be greater than zero.'
}

if ($MaxCpuCount -le 0) {
    throw 'MaxCpuCount must be greater than zero.'
}

# Build one explicit dotnet test argument list so timeout, logging, and filter behavior stay standardized across reruns.
$loggerValue = if ([string]::IsNullOrWhiteSpace($LogFileName)) {
    'trx'
}
else {
    "trx;LogFileName=$LogFileName"
}

$arguments = @(
    'test'
    $targetPath
    '--blame-hang'
    '--blame-hang-timeout'
    ("{0}s" -f $HangTimeoutSeconds)
    ("-maxcpucount:{0}" -f $MaxCpuCount)
    '--results-directory'
    $resolvedResultsDirectory
    '--logger'
    $loggerValue
    '-v'
    $Verbosity
)

if ($NoBuild) {
    $arguments += '--no-build'
}

if ($NoRestore) {
    $arguments += '--no-restore'
}

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $arguments += '--filter'
    $arguments += $Filter
}

& dotnet @arguments
exit $LASTEXITCODE
