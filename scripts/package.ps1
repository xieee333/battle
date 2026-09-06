$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot 'src\BattlegroundsVisionAgent.App\BattlegroundsVisionAgent.App.csproj'
$output = Join-Path $projectRoot 'dist\win-x64'
$localDotnet = Join-Path $projectRoot 'tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet -PathType Leaf) { $localDotnet } else { 'dotnet' }

& $dotnet publish $appProject `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE"
}

$executable = Join-Path $output 'BattlegroundsVisionAgent.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Publish completed without creating $executable"
}

Write-Output "Published: $executable"
