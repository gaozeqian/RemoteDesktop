#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the RemoteDesktop host service.

.DESCRIPTION
  Stops and deletes the service and removes the installed files. The device identity and logs under
  %ProgramData%\RemoteDesktop are kept unless -RemoveData is given (deleting the identity gives the computer a new
  device code the next time it is installed). The Ctrl+Alt+Del policy is not reverted, as other software may use it.

  -InstallDirectory is taken literally (no wildcards) and is checked before anything is stopped or removed: a drive
  root, Program Files, Program Files (x86), the Windows folder and the system drive root are refused.
#>
param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'RemoteDesktop'),

    [string]$ServiceName = 'RemoteDesktopHost',

    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

# The directory is removed recursively, so a wrong value must be refused before anything is stopped or deleted: a
# service that was stopped and removed while the directory is then rejected is a half-done uninstall. The name is not
# required to be RemoteDesktop, because Install-HostService.ps1 accepts a custom directory; only places that are never
# an installation of this program are refused.
$installFull = [System.IO.Path]::GetFullPath(
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDirectory))
$installTrimmed = $installFull.TrimEnd('\', '/')
$driveRoot = [System.IO.Path]::GetPathRoot($installFull)
$protected = @(
    $env:ProgramFiles,
    ${env:ProgramFiles(x86)},
    $env:windir,
    $(if ($env:SystemDrive) { "$($env:SystemDrive)\" })
) | Where-Object { $_ } | ForEach-Object { [System.IO.Path]::GetFullPath($_).TrimEnd('\', '/') }
if ($installFull -eq $driveRoot -or -not $installTrimmed -or
    $protected -contains $installTrimmed) {
    throw "Refusing to remove '$installFull': it is a drive root or a system folder, not an installation directory. Nothing was changed."
}

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

# The manual installer adds this rule; removing the program without it leaves an inbound UDP exception aimed at a
# program that no longer exists. The MSI removes its own rule through its component.
$ruleName = 'Remote Desktop peer connections (UDP)'
$rules = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($rules) {
    $rules | Remove-NetFirewallRule
    Write-Host "Firewall rule '$ruleName' removed."
}

if (Test-Path -LiteralPath $installFull) {
    Remove-Item -LiteralPath $installFull -Recurse -Force
    Write-Host "Removed $installFull"
}

if ($RemoveData) {
    $data = Join-Path $env:ProgramData 'RemoteDesktop'
    if (Test-Path -LiteralPath $data) {
        Remove-Item -LiteralPath $data -Recurse -Force
        Write-Host "Removed $data"
    }
}
