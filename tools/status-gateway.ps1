param(
    [string]$Profile,
    [string]$ConfigPath,
    [switch]$All,
    [int]$TailLog = 0
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

$tcpConnections = @(Get-NetTCPConnection -ErrorAction SilentlyContinue)

foreach ($profileConfig in $profiles) {
    $pidFile = Get-GatewayPidFilePath -Profile $profileConfig
    $logPath = Get-GatewayLogPath -Profile $profileConfig
    $errorLogPath = Get-GatewayErrorLogPath -Profile $profileConfig
    $statusJsonPath = Get-GatewayStatusJsonPath -Profile $profileConfig
    $pidFromFile = Get-GatewayPidFromFile -PidFile $pidFile
    $pidFileAlive = $false
    if ($null -ne (Get-GatewayProcessByPid -ProcessId $pidFromFile)) {
        $pidFileAlive = $true
    }

    $listener = Get-GatewayListener -Profile $profileConfig | Select-Object -First 1
    $listenerPid = $null
    if ($null -ne $listener) {
        $listenerPid = [int]$listener.OwningProcess
    }

    $clientSessions = @($tcpConnections | Where-Object {
        $matchesAddress = $_.LocalAddress -eq [string]$profileConfig.listenAddress
        $matchesPort = $_.LocalPort -eq [int]$profileConfig.listenPort
        $isEstablished = $_.State -eq "Established"
        $matchesAddress -and $matchesPort -and $isEstablished
    })

    [pscustomobject]@{
        Profile = $profileConfig.name
        Listen = "$($profileConfig.listenAddress):$($profileConfig.listenPort)"
        Plcsim = "$($profileConfig.plcsimAddress) rack/slot $($profileConfig.rack)/$($profileConfig.slot)"
        ListenerPid = $listenerPid
        PidFilePid = $pidFromFile
        PidFileAlive = $pidFileAlive
        ClientSessions = $clientSessions.Count
        LogPath = $logPath
        ErrorLogPath = $errorLogPath
        StatusJsonPath = $statusJsonPath
        PidFile = $pidFile
    }

    if ($TailLog -gt 0 -and (Test-Path -LiteralPath $logPath)) {
        Write-Output ""
        Write-Output "Last $TailLog log lines for '$($profileConfig.name)':"
        Get-Content -LiteralPath $logPath -Tail $TailLog -Encoding UTF8
    }

    if ($TailLog -gt 0 -and (Test-Path -LiteralPath $errorLogPath)) {
        $errorLines = @(Get-Content -LiteralPath $errorLogPath -Tail $TailLog -Encoding UTF8)
        if ($errorLines.Count -gt 0) {
            Write-Output ""
            Write-Output "Last $TailLog stderr lines for '$($profileConfig.name)':"
            $errorLines
        }
    }
}
