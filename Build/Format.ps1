param(
    [string[]]$Include,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

function Resolve-IncludedPaths {
    param(
        [string]$RepositoryRoot,
        [string[]]$Paths
    )

    if (-not $Paths -or $Paths.Count -eq 0) {
        return @()
    }

    $resolvedPaths = New-Object System.Collections.Generic.List[string]

    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $resolvedPath = Resolve-Path -LiteralPath $path
        $fullPath = $resolvedPath.ProviderPath
        $repositoryRootUri = [System.Uri]([System.IO.Path]::GetFullPath($RepositoryRoot) + [System.IO.Path]::DirectorySeparatorChar)
        $fullPathUri = [System.Uri][System.IO.Path]::GetFullPath($fullPath)
        $relativePath = $repositoryRootUri.MakeRelativeUri($fullPathUri).ToString()
        $normalizedPath = $relativePath.Replace('\\', '/')
        $resolvedPaths.Add($normalizedPath)
    }

    return $resolvedPaths.ToArray()
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'UnrealAutomationTools.sln'

if (-not (Test-Path -LiteralPath $solutionPath)) {
    throw "Could not find solution at '$solutionPath'."
}

$arguments = @(
    'format'
    $solutionPath
    '--verbosity'
    'minimal'
)

if ($Verify) {
    $arguments += '--verify-no-changes'
}

$includedPaths = Resolve-IncludedPaths -RepositoryRoot $repositoryRoot -Paths $Include
if ($includedPaths.Count -gt 0) {
    $arguments += '--include'
    $arguments += $includedPaths
}

& dotnet @arguments
exit $LASTEXITCODE
