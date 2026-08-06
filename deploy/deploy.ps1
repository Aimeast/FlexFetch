<#
.SYNOPSIS
    FlexFetch unified deployment script (Windows PowerShell / pwsh on Linux/macOS).

.DESCRIPTION
    Detects the runtime and external components, installs missing dependencies
    (through the proxy policy), publishes the app from source, generates
    configuration, registers/starts the service (systemd on Linux, Windows
    service, or Docker compose), and runs a health check.

    The script is idempotent: re-running it does not reinstall or break data.
    Data directory is independent of the application directory.

.PARAMETER AppDir
    Directory where the published application will be placed (default: ./flexfetch-app).

.PARAMETER DataDir
    Data directory (database, files, logs, profiles). Independent of AppDir
    so upgrades never lose data (default: ./.flexfetch).

.PARAMETER Port
    HTTP port the service listens on (default: 8080).

.PARAMETER Proxy
    Optional proxy URL used both for dependency installs and by the app
    (e.g. http://127.0.0.1:7890 or socks5://127.0.0.1:1080).

.PARAMETER Mode
    Service mode: Auto (detect), Docker, Systemd, WindowsService, or None
    (publish + configure only, no service registration).

.PARAMETER SkipInstall
    Skip dependency installation (assume everything is already present).
#>
[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path (Get-Location) 'flexfetch-app'),
    [string]$DataDir = (Join-Path (Get-Location) '.flexfetch'),
    [int]$Port = 8080,
    [string]$Proxy = '',
    [ValidateSet('Auto', 'Docker', 'Systemd', 'WindowsService', 'None')]
    [string]$Mode = 'Auto',
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Test-Command([string]$name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

# --- 1. Environment detection -------------------------------------------------

Write-Step 'Environment detection'
if (-not (Test-Command dotnet)) {
    throw 'dotnet CLI not found. Install the .NET 10 SDK first.'
}
$sdk = dotnet --list-sdks | Select-String -Pattern '^10\.'
if (-not $sdk) {
    throw '.NET 10 SDK not found. Install it (https://dotnet.microsoft.com/download).'
}
Write-Host "  .NET SDK: $($sdk.Line.Trim())"

$isLinuxEnv = $IsLinux -or ($env:OS -eq 'Unix')
$isWindowsEnv = -not $isLinuxEnv
Write-Host "  OS: $(if ($isLinuxEnv) { 'Linux' } else { 'Windows' })"

# --- 2. Dependency installation (through the proxy policy) --------------------

if (-not $SkipInstall) {
    Write-Step 'Dependency check/install (through proxy policy)'
    $proxyArgs = @()
    if ($Proxy) {
        $proxyArgs = @("--proxy=$Proxy")
    }

    if (-not (Test-Command yt-dlp)) {
        Write-Host '  yt-dlp not found, downloading...'
        $ytUrl = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe'
        if ($isLinuxEnv) { $ytUrl = $ytUrl -replace '\.exe$', '' }
        $ytTarget = Join-Path $DataDir 'components'
        New-Item -ItemType Directory -Force -Path $ytTarget | Out-Null
        if ($Proxy) {
            curl.exe -L --proxy $Proxy -o (Join-Path $ytTarget 'yt-dlp') $ytUrl | Out-Null
        }
        else {
            curl.exe -L -o (Join-Path $ytTarget 'yt-dlp') $ytUrl | Out-Null
        }
    }
    else {
        Write-Host '  yt-dlp found'
    }
}
else {
    Write-Step 'Skipping dependency installation (-SkipInstall)'
}

# --- 3. Publish from source ----------------------------------------------------

Write-Step 'Publishing from source (dotnet publish)'
$publishDir = Join-Path $AppDir 'publish'
if (Test-Path $AppDir) {
    Write-Host "  App dir exists ($AppDir); publish artifacts will be refreshed in place"
}
& dotnet publish (Join-Path $repoRoot 'FlexFetch/FlexFetch.csproj') `
    -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# --- 4. Configuration generation ----------------------------------------------

Write-Step 'Generating configuration'
$dataRoot = [IO.Path]::GetFullPath($DataDir)
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot 'logs') | Out-Null

# appsettings.Production.json: data dir + proxy (proxy optional).
$production = @{
    Serilog = @{
        MinimumLevel = @{
            Default = 'Information'
            Override = @{ 'Microsoft.AspNetCore' = 'Warning' }
        }
    }
    Data = @{ Dir = $dataRoot }
}
if ($Proxy) {
    $production.Network = @{ Proxy = $Proxy }
}
$productionJson = $production | ConvertTo-Json -Depth 5
$settingsPath = Join-Path $publishDir 'appsettings.Production.json'
Set-Content -Path $settingsPath -Value $productionJson -Encoding utf8
Write-Host "  Wrote $settingsPath"

# --- 5. Service registration & start -------------------------------------------

function Resolve-Mode {
    if ($Mode -ne 'Auto') { return $Mode }
    if (Test-Command docker) { return 'Docker' }
    if ($isLinuxEnv -and (Test-Command systemctl)) { return 'Systemd' }
    if ($isWindowsEnv) { return 'WindowsService' }
    return 'None'
}

$mode = Resolve-Mode
Write-Step "Service registration ($mode)"

switch ($mode) {
    'None' {
        Write-Host '  Mode None: skipping service registration'
    }
    'Docker' {
        & docker compose -f (Join-Path $PSScriptRoot 'compose.yaml') build
        if ($LASTEXITCODE -ne 0) { throw 'docker compose build failed' }
        & docker compose -f (Join-Path $PSScriptRoot 'compose.yaml') up -d
        if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed' }
    }
    'Systemd' {
        $serviceFile = '/etc/systemd/system/flexfetch.service'
        $unit = @"
[Unit]
Description=FlexFetch - Flexible, Plugin-based Media Downloader
After=network.target

[Service]
Type=simple
WorkingDirectory=$publishDir
ExecStart=$(Get-Command dotnet) $publishDir/FlexFetch.dll
Restart=on-failure
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:$Port

[Install]
WantedBy=multi-user.target
"@
        if (-not (Test-Path $serviceFile)) {
            Write-Host '  Writing systemd unit (requires root)'
            Set-Content -Path $serviceFile -Value $unit -Encoding utf8
            & systemctl daemon-reload
            & systemctl enable --now flexfetch
        }
        else {
            Write-Host '  systemd unit already installed; restarting'
            & systemctl restart flexfetch
        }
    }
    'WindowsService' {
        if (-not (Test-Command sc.exe)) {
            throw 'sc.exe not available; cannot register a Windows service'
        }
        $serviceName = 'FlexFetch'
        $existing = & sc.exe query $serviceName 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host '  Registering Windows service (requires admin)'
            & sc.exe create $serviceName binPath= "`"$(Get-Command dotnet)`" `"$publishDir\FlexFetch.dll`"" start= auto
            & sc.exe description $serviceName 'FlexFetch - Flexible, Plugin-based Media Downloader'
            & sc.exe start $serviceName
        }
        else {
            Write-Host '  Service already registered; restarting'
            & sc.exe stop $serviceName 2>$null
            & sc.exe start $serviceName
        }
    }
}

# --- 6. Health check ------------------------------------------------------------

if ($mode -eq 'None') {
    Write-Host '  Mode None: service not started, skipping health check' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Publish + configuration complete. Start the app manually:' -ForegroundColor Green
    Write-Host "  cd $publishDir && dotnet FlexFetch.dll" -ForegroundColor Green
    Write-Host "  (Web UI: http://localhost:$Port/ ; set ASPNETCORE_URLS to bind a specific address)" -ForegroundColor Green
    exit 0
}

Write-Step 'Health check'
$healthUrl = "http://localhost:$Port/api/system/info"
$attempt = 0
$ok = $false
while ($attempt -lt 30) {
    try {
        $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $ok = $true; break }
    }
    catch {
        Start-Sleep -Seconds 1
    }
    $attempt++
}
if ($ok) {
    Write-Host "  Health check OK: $healthUrl" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Deployment complete. Web UI: http://localhost:PORT/' -ForegroundColor Green
    if (-not $Proxy) {
        Write-Host 'Note: no proxy configured; set Network:Proxy if you need downloads through a proxy.' -ForegroundColor Yellow
    }
}
else {
    Write-Warning 'Health check failed: the service did not respond within 30s. Check logs under the data directory.'
    exit 1
}
