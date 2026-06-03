$ErrorActionPreference = "Stop"

function Get-GatewayRepoRoot {
    return (Split-Path -Parent $PSScriptRoot)
}

function Get-GatewayDefaultConfigPath {
    return (Join-Path (Get-GatewayRepoRoot) "config\gateway-profiles.json")
}

function Read-GatewayProfileConfig {
    param(
        [string]$ConfigPath = (Get-GatewayDefaultConfigPath)
    )

    $resolved = Resolve-Path -LiteralPath $ConfigPath -ErrorAction Stop
    $config = Get-Content -LiteralPath $resolved.Path -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Member -InputObject $config -NotePropertyName ConfigPath -NotePropertyValue $resolved.Path -Force
    return $config
}

function Get-GatewayProfiles {
    param(
        [Parameter(Mandatory = $true)]$Config
    )

    return @($Config.profiles)
}

function Get-GatewayProfile {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        $Name = $Config.defaultProfile
    }

    $profile = @(Get-GatewayProfiles -Config $Config | Where-Object { $_.name -eq $Name }) | Select-Object -First 1
    if ($null -eq $profile) {
        $available = (Get-GatewayProfiles -Config $Config | ForEach-Object { $_.name }) -join ", "
        throw "Gateway profile '$Name' was not found. Available profiles: $available"
    }

    return $profile
}

function Get-GatewayRuntimeDir {
    $tempRoot = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        [System.IO.Path]::GetTempPath()
    }
    else {
        Join-Path $env:LOCALAPPDATA "Temp"
    }

    $dir = Join-Path $tempRoot "nettoplcsim-gateway"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Get-GatewaySafeName {
    param(
        [Parameter(Mandatory = $true)][string]$Name
    )

    return ($Name -replace '[^a-zA-Z0-9._-]', '_')
}

function Get-GatewayEndpointSafeName {
    param(
        [Parameter(Mandatory = $true)]$Profile
    )

    $listen = ([string]$Profile.listenAddress) -replace '[^a-zA-Z0-9._-]', '_'
    return "$listen-$($Profile.listenPort)"
}

function Get-GatewayPidFilePath {
    param(
        [Parameter(Mandatory = $true)]$Profile
    )

    $safeName = Get-GatewaySafeName -Name $Profile.name
    return (Join-Path (Get-GatewayRuntimeDir) "gateway-$safeName.pid")
}

function Get-GatewayLogPath {
    param(
        [Parameter(Mandatory = $true)]$Profile
    )

    if (-not [string]::IsNullOrWhiteSpace($Profile.logPath)) {
        return [Environment]::ExpandEnvironmentVariables([string]$Profile.logPath)
    }

    $safeName = Get-GatewaySafeName -Name $Profile.name
    $endpoint = Get-GatewayEndpointSafeName -Profile $Profile
    return (Join-Path (Get-GatewayRuntimeDir) "gateway-$safeName-$endpoint.log")
}

function Get-GatewayErrorLogPath {
    param(
        [Parameter(Mandatory = $true)]$Profile
    )

    $logPath = Get-GatewayLogPath -Profile $Profile
    return [System.IO.Path]::ChangeExtension($logPath, ".err.log")
}

function Get-GatewayStatusJsonPath {
    param(
        [Parameter(Mandatory = $true)]$Profile
    )

    $logPath = Get-GatewayLogPath -Profile $Profile
    return [System.IO.Path]::ChangeExtension($logPath, ".status.json")
}

function Get-GatewayExecutablePath {
    $repoRoot = Get-GatewayRepoRoot
    $candidates = @(
        (Join-Path $repoRoot "src\PlcsimGateway.Host\bin\x86\Release\PlcsimGateway.Host.exe"),
        (Join-Path $repoRoot "src\PlcsimGateway.Host\bin\x86\Debug\PlcsimGateway.Host.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "PlcsimGateway.Host.exe was not found. Build src\PlcsimGateway.Host\PlcsimGateway.Host.csproj first."
}

function Get-GatewayPidFromFile {
    param(
        [Parameter(Mandatory = $true)][string]$PidFile
    )

    if (-not (Test-Path -LiteralPath $PidFile)) {
        return $null
    }

    $text = (Get-Content -LiteralPath $PidFile -Raw -ErrorAction Stop).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    return [int]$text
}

function Get-GatewayProcessByPid {
    param(
        [Nullable[int]]$ProcessId
    )

    if ($null -eq $ProcessId) {
        return $null
    }

    return (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

function Get-GatewayProcessCommandLine {
    param(
        [Nullable[int]]$ProcessId
    )

    if ($null -eq $ProcessId) {
        return ""
    }

    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return ""
    }

    return [string]$process.CommandLine
}

function Test-GatewayProcessMatches {
    param(
        [Nullable[int]]$ProcessId
    )

    $commandLine = Get-GatewayProcessCommandLine -ProcessId $ProcessId
    return ($commandLine -like "*PlcsimGateway.Host.exe*")
}

function Get-GatewayListener {
    param(
        [Parameter(Mandatory = $true)]$Profile
    )

    $parameters = @{
        LocalAddress = [string]$Profile.listenAddress
        LocalPort = [int]$Profile.listenPort
        State = "Listen"
        ErrorAction = "SilentlyContinue"
    }

    return @(Get-NetTCPConnection @parameters)
}

function Join-GatewayArguments {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $quoted = foreach ($argument in $Arguments) {
        $text = [string]$argument
        if ($text -match '[\s"]') {
            '"' + ($text -replace '"', '\"') + '"'
        }
        else {
            $text
        }
    }

    return ($quoted -join " ")
}
