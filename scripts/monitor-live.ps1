param(
    [int]$IntervalMs = 500
)

$root = Split-Path -Parent $PSScriptRoot
$bundledDotnet = Join-Path $root "tools\dotnet\dotnet.exe"
$dotnet = if (Test-Path $bundledDotnet) { $bundledDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$project = Join-Path $root "tools\LiveVisionMonitor\LiveVisionMonitor.csproj"
$dll = Join-Path $root "tools\LiveVisionMonitor\bin\Release\net9.0-windows\LiveVisionMonitor.dll"
$profile = Join-Path $root "dist\win-x64\data\vision\profile.json"
$catalog = Join-Path $root "dist\win-x64\data\catalog\catalog.db"
$log = Join-Path $root "logs\live-vision-monitor-current.log"

if (-not (Test-Path $profile)) { throw "未找到视觉配置：$profile" }
if (-not (Test-Path $catalog)) { throw "未找到卡库数据库：$catalog" }
if (-not (Test-Path $dll)) {
    & $dotnet build $project --configuration Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
$arguments = @($dll, $profile, $catalog, $log, [Math]::Clamp($IntervalMs, 250, 5000))
$process = Start-Process -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -PassThru
Write-Output "只读监听器已启动：PID $($process.Id)"
Write-Output "日志：$log"
