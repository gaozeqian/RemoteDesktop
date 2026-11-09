#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs the RemoteDesktop host service (development / manual deployment; the MSI installer under build/installer/windows is the normal route).

.DESCRIPTION
  Copies the build output of RemoteDesktop.HostService (which already contains RemoteDesktop.SessionAgent.exe) to the
  install directory, registers the service to start automatically as LocalSystem, restarts it if it crashes, and lets
  services raise Ctrl+Alt+Del (policy SoftwareSASGeneration, bit 0). Starts the service.

.EXAMPLE
  dotnet publish src/Hosts/RemoteDesktop.HostService -c Release -o artifacts/app/windows/host
  .\build\service\windows\Install-HostService.ps1 -SourceDirectory artifacts\app\windows\host

.NOTES
  The service connects to the rendezvous server configured in appsettings.json ("Transport:RendezvousUri") next to the
  executable. Edit it in the install directory before the first start, or pass -RendezvousUri.
#>
param(
    [Parameter(Mandatory)]
    [string]$SourceDirectory,

    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'RemoteDesktop'),

    [string]$RendezvousUri,

    [string]$ServiceName = 'RemoteDesktopHost',

    # Leave the Ctrl+Alt+Del policy alone (then "Ctrl+Alt+Del" in the remote window will not work).
    [switch]$SkipSasPolicy
)

$ErrorActionPreference = 'Stop'

$source = Resolve-Path $SourceDirectory
foreach ($file in 'RemoteDesktop.HostService.exe', 'RemoteDesktop.SessionAgent.exe') {
    if (-not (Test-Path (Join-Path $source $file))) {
        throw "$file not found in $source. Publish or build RemoteDesktop.HostService first."
    }
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping and removing the existing '$ServiceName' service..."
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    # Give the service control manager a moment to release the name.
    Start-Sleep -Seconds 2
}

# An agent or client left over from a previous run keeps the executables mapped, and the copy then fails after the
# old service has already been removed. The MSI kills them for the same reason.
Get-Process -Name RemoteDesktop.SessionAgent, RemoteDesktop.Client -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Copying files to $InstallDirectory ..."
New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null

# appsettings.json is the operator's file, not the build's: a re-run to upgrade must not reset the server address the
# machine was configured with. Only a first install takes the packaged default.
$settingsPath = Join-Path $InstallDirectory 'appsettings.json'
$hasSettings = Test-Path -LiteralPath $settingsPath
Copy-Item -Path (Join-Path $source '*') -Destination $InstallDirectory -Recurse -Force -Exclude 'appsettings.json'
if (-not $hasSettings) {
    Copy-Item -LiteralPath (Join-Path $source 'appsettings.json') -Destination $InstallDirectory -Force
}

if ($RendezvousUri) {
    $settingsPath = Join-Path $InstallDirectory 'appsettings.json'
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings.Transport.RendezvousUri = $RendezvousUri
    $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8
    Write-Host "Rendezvous server set to $RendezvousUri"
}

$exe = Join-Path $InstallDirectory 'RemoteDesktop.HostService.exe'
New-Service -Name $ServiceName -DisplayName 'Remote Desktop Host' `
    -Description 'Lets other devices connect to this computer with RemoteDesktop.' `
    -BinaryPathName "`"$exe`"" -StartupType Automatic | Out-Null

# Restart after a crash: 5 s, 5 s, then 30 s; forget the failure count after a day.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

# The same program-scoped UDP rule the MSI creates. Without it direct connections are silently replaced by the relay.
$ruleName = 'Remote Desktop peer connections (UDP)'
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Description 'Incoming connections from other Remote Desktop devices.' `
        -Direction Inbound -Program $exe -Protocol UDP -Action Allow -Profile Any | Out-Null
    Write-Host "Firewall rule '$ruleName' added."
}

if (-not $SkipSasPolicy) {
    # Windows ignores SendSAS from services unless this policy allows it (1 = services, 2 = ease-of-access apps, 3 = both).
    $policyPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    $current = (Get-ItemProperty -Path $policyPath -Name SoftwareSASGeneration -ErrorAction SilentlyContinue).SoftwareSASGeneration
    $value = ([int]$current) -bor 1
    Set-ItemProperty -Path $policyPath -Name SoftwareSASGeneration -Type DWord -Value $value
    Write-Host "Policy SoftwareSASGeneration = $value (allows the service to send Ctrl+Alt+Del)."
}

Start-Service -Name $ServiceName
Write-Host "Service '$ServiceName' installed and started. Logs: $env:ProgramData\RemoteDesktop\logs"
