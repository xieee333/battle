$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $projectRoot 'dist\win-x64\BattlegroundsVisionAgent.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published application was not found: $executable"
}

$clientId = Read-Host '请输入 Battle.net Client ID'
if ([string]::IsNullOrWhiteSpace($clientId)) {
    throw 'Client ID 不能为空。'
}

$secureSecret = Read-Host '请输入 Battle.net Client Secret' -AsSecureString
$secretPointer = [IntPtr]::Zero
try {
    $secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureSecret)
    $clientSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)
    if ([string]::IsNullOrWhiteSpace($clientSecret)) {
        throw 'Client Secret 不能为空。'
    }

    $env:BVA_BLIZZARD_CLIENT_ID = $clientId.Trim()
    $env:BVA_BLIZZARD_CLIENT_SECRET = $clientSecret
    $env:BVA_BLIZZARD_REGION = if ([string]::IsNullOrWhiteSpace($env:BVA_BLIZZARD_REGION)) { 'us' } else { $env:BVA_BLIZZARD_REGION }
    $env:BVA_BLIZZARD_LOCALE = if ([string]::IsNullOrWhiteSpace($env:BVA_BLIZZARD_LOCALE)) { 'zh_CN' } else { $env:BVA_BLIZZARD_LOCALE }

    & $executable
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    if ($secretPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer)
    }
    Remove-Item Env:BVA_BLIZZARD_CLIENT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:BVA_BLIZZARD_CLIENT_SECRET -ErrorAction SilentlyContinue
}
