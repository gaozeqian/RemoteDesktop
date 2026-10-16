#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the RemoteDesktop host service.

.DESCRIPTION
  Stops and deletes the service and removes the installed files. The device identity and logs under
  %ProgramData%\RemoteDesktop are kept unless -RemoveData is given (deleting the identity gives the computer a new
  device code the next time it is installed). The Ctrl+Alt+Del policy is not reverted, as other software may use it.
#>
param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'RemoteDesktop'),

    [string]$ServiceName = 'RemoteDesktopHost',

    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service '$ServiceName' removed."
} else {
    Write-Host "Service '$ServiceName' is not installed."
}

# The agent may still be shutting down.
Get-Process -Name 'RemoteDesktop.SessionAgent' -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $InstallDirectory) {
    Remove-Item -Recurse -Force $InstallDirectory
    Write-Host "Removed $InstallDirectory"
}

if ($RemoveData) {
    $data = Join-Path $env:ProgramData 'RemoteDesktop'
    if (Test-Path $data) {
        Remove-Item -Recurse -Force $data
        Write-Host "Removed $data"
    }
}
