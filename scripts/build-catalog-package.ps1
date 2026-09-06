param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$source = (Resolve-Path -LiteralPath $SourceDirectory -ErrorAction Stop).Path
$manifestPath = Join-Path $source 'catalog.manifest.json'
$databasePath = Join-Path $source 'catalog.db'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Missing $manifestPath"
}
if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
    throw "Missing $databasePath"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$manifest.version)) {
    throw 'Catalog manifest must contain a non-empty version.'
}

$destination = [System.IO.Path]::GetFullPath($OutputPath)
$destinationDirectory = [System.IO.Path]::GetDirectoryName($destination)
if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
}
if (Test-Path -LiteralPath $destination) {
    throw "Output already exists: $destination"
}

$currentDirectory = Get-Location
try {
    Set-Location -LiteralPath $source
    Compress-Archive -Path .\* -DestinationPath $destination
}
finally {
    Set-Location -LiteralPath $currentDirectory
}

Write-Output "Catalog package created: $destination"
