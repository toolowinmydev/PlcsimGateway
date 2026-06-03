param(
    [string]$Profile,
    [string]$ConfigPath,
    [switch]$Restart
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "gateway-common.ps1")

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-GatewayDefaultConfigPath
}

$config = Read-GatewayProfileConfig -ConfigPath $ConfigPath
$profileConfig = Get-GatewayProfile -Config $config -Name $Profile
$pidFile = Get-GatewayPidFilePath -Profile $profileConfig
$logPath = Get-GatewayLogPath -Profile $profileConfig
$errorLogPath = Get-GatewayErrorLogPath -Profile $profileConfig
$statusJsonPath = Get-GatewayStatusJsonPath -Profile $profileConfig
$gatewayExe = Get-GatewayExecutablePath

if ($Restart) {
    & (Join-Path $PSScriptRoot "stop-gateway.ps1") -Profile $profileConfig.name -ConfigPath $config.ConfigPath -ByListener -Quiet

    for ($i = 0; $i -lt 20; $i++) {
        if ($null -eq (Get-GatewayListener -Profile $profileConfig | Select-Object -First 1)) {
            break
        }

        Start-Sleep -Milliseconds 250
    }
}
else {
    $existingPid = Get-GatewayPidFromFile -PidFile $pidFile
    $existingProcess = Get-GatewayProcessByPid -ProcessId $existingPid
    if ($null -ne $existingProcess -and (Test-GatewayProcessMatches -ProcessId $existingPid)) {
        Write-Output "Gateway profile '$($profileConfig.name)' is already running as PID $existingPid."
        Write-Output "Log: $logPath"
        exit 0
    }
}

$listener = Get-GatewayListener -Profile $profileConfig | Select-Object -First 1
if ($null -ne $listener) {
    throw "Cannot start profile '$($profileConfig.name)': $($profileConfig.listenAddress):$($profileConfig.listenPort) is already listened by PID $($listener.OwningProcess)."
}

$argumentList = @(
    "--profile", [string]$profileConfig.name,
    "--config", [string]$config.ConfigPath,
    "--status-json", [string]$statusJsonPath
)

Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $errorLogPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $statusJsonPath -Force -ErrorAction SilentlyContinue

$process = Start-Process `
    -FilePath $gatewayExe `
    -ArgumentList (Join-GatewayArguments -Arguments $argumentList) `
    -WindowStyle Hidden `
    -RedirectStandardOutput $logPath `
    -RedirectStandardError $errorLogPath `
    -PassThru

Set-Content -LiteralPath $pidFile -Value $process.Id -Encoding ASCII

$started = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 250
    $listener = Get-GatewayListener -Profile $profileConfig | Select-Object -First 1
    if ($null -ne $listener) {
        $started = $true
        break
    }

    if ($null -eq (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
        break
    }
}

if (-not $started) {
    $tail = ""
    if (Test-Path -LiteralPath $logPath) {
        $tail = (Get-Content -LiteralPath $logPath -Tail 20 -Encoding UTF8) -join [Environment]::NewLine
    }

    throw "Gateway profile '$($profileConfig.name)' did not open $($profileConfig.listenAddress):$($profileConfig.listenPort). Recent log: $tail"
}

[pscustomobject]@{
    Profile = $profileConfig.name
    Listen = "$($profileConfig.listenAddress):$($profileConfig.listenPort)"
    Plcsim = "$($profileConfig.plcsimAddress) rack/slot $($profileConfig.rack)/$($profileConfig.slot)"
    ProcessId = $process.Id
    PidFile = $pidFile
    LogPath = $logPath
    ErrorLogPath = $errorLogPath
    StatusJsonPath = $statusJsonPath
}
