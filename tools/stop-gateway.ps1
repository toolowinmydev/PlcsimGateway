param(
    [string]$Profile,
    [string]$ConfigPath,
    [switch]$All,
    [switch]$ByListener,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "gateway-common.ps1")

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-GatewayDefaultConfigPath
}

$config = Read-GatewayProfileConfig -ConfigPath $ConfigPath
$profiles = if ($All) {
    Get-GatewayProfiles -Config $config
}
else {
    @(Get-GatewayProfile -Config $config -Name $Profile)
}

foreach ($profileConfig in $profiles) {
    $pidFile = Get-GatewayPidFilePath -Profile $profileConfig
    $statusJsonPath = Get-GatewayStatusJsonPath -Profile $profileConfig
    $gatewayPid = Get-GatewayPidFromFile -PidFile $pidFile
    $process = Get-GatewayProcessByPid -ProcessId $gatewayPid

    if ($null -eq $process) {
        if (Test-Path -LiteralPath $pidFile) {
            Remove-Item -LiteralPath $pidFile -Force
        }

        Remove-Item -LiteralPath $statusJsonPath -Force -ErrorAction SilentlyContinue

        if (-not $Quiet -and -not $ByListener) {
            Write-Output "Gateway profile '$($profileConfig.name)' is not running."
        }
    }
    else {
        if (-not (Test-GatewayProcessMatches -ProcessId $gatewayPid)) {
            throw "PID file for profile '$($profileConfig.name)' points to PID $gatewayPid, but that process does not look like PlcsimGateway.Host.exe. Refusing to stop it."
        }

        Stop-Process -Id $gatewayPid -Force
        Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $statusJsonPath -Force -ErrorAction SilentlyContinue

        if (-not $Quiet) {
            Write-Output "Stopped gateway profile '$($profileConfig.name)' PID $gatewayPid."
        }
    }

    if (-not $ByListener) {
        continue
    }

    $listener = Get-GatewayListener -Profile $profileConfig | Select-Object -First 1
    if ($null -eq $listener) {
        continue
    }

    $listenerPid = [int]$listener.OwningProcess
    if (-not (Test-GatewayProcessMatches -ProcessId $listenerPid)) {
        throw "Listener $($profileConfig.listenAddress):$($profileConfig.listenPort) is owned by PID $listenerPid, but it does not look like PlcsimGateway.Host.exe. Refusing to stop it."
    }

    Stop-Process -Id $listenerPid -Force
    Remove-Item -LiteralPath $statusJsonPath -Force -ErrorAction SilentlyContinue
    if (-not $Quiet) {
        Write-Output "Stopped gateway listener for profile '$($profileConfig.name)' PID $listenerPid."
    }
}
