$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot 'BattlegroundsVisionAgent.sln'
$nugetConfig = Join-Path $projectRoot 'NuGet.config'
$localDotnet = Join-Path $projectRoot 'tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet -PathType Leaf) { $localDotnet } else { 'dotnet' }

Push-Location $projectRoot
try {
    & $dotnet restore $solution --configfile $nugetConfig --ignore-failed-sources -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
    & $dotnet build $solution -c Release --no-restore -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
    & $dotnet test $solution -c Release --no-build --no-restore -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }

    $gitRoot = $null
    try {
        $gitRoot = & git rev-parse --show-toplevel 2>$null
    }
    catch {
        $gitRoot = $null
    }
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitRoot)) {
        git diff --check
    }
    else {
        Write-Warning 'Git metadata is unavailable for this checkout; skipped git diff --check.'
    }
}
finally {
    Pop-Location
}
