$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $projectRoot 'tools\dotnet'
$installerScript = Join-Path $env:TEMP 'bva-dotnet-install.ps1'
$dotnet = Join-Path $toolsRoot 'dotnet.exe'

New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    Write-Host "Downloading the .NET 9 SDK installer script..."
    Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' `
        -OutFile $installerScript -TimeoutSec 60

    Write-Host "Installing the project-local .NET 9 SDK..."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerScript `
        -Channel 9.0 -Architecture x64 -InstallDir $toolsRoot -NoPath
    if ($LASTEXITCODE -ne 0) {
        throw "The .NET SDK installer exited with code $LASTEXITCODE"
    }
}
else {
    Write-Host "Project-local .NET SDK already exists; skipping download."
}

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "The .NET SDK installer completed without creating $dotnet"
}

& $dotnet --info
Write-Output "Project-local .NET SDK is ready: $dotnet"
