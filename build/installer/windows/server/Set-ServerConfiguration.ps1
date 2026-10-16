<#
.SYNOPSIS
  Changes what the installed servers are configured with, after the installation.

.DESCRIPTION
  The installer asks for these once. This is the way back in: give it the settings you want to change and it writes
  them, then restarts the two services so they take effect. Anything you leave out is left alone.

  It runs the same configuration step the installer does (Rendezvous\RemoteDesktop.Server.Rendezvous.exe
  --configure-system), so the two cannot drift apart, and what it did is appended to server-setup.log in this folder.

.EXAMPLE
  # Point the servers at a database (the one setting whose default has the worst consequence: without it device codes
  # only live in memory and change on every restart).
  .\Set-ServerConfiguration.ps1 -PostgresConnectionString 'Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=...'

.EXAMPLE
  # Move signaling to https with a certificate this machine makes for itself, and print what would happen first.
  .\Set-ServerConfiguration.ps1 -TlsGenerate -WhatIf

.EXAMPLE
  # Tell clients where the relay is (required when the relay runs on another machine).
  .\Set-ServerConfiguration.ps1 -RelayPublicEndpoint 203.0.113.10:5082

.NOTES
  Needs an elevated prompt: it writes the settings next to the programs and restarts services.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # The signaling address the server binds to, e.g. https://0.0.0.0:5080. Omit to leave it as it is.
    [string]$RendezvousUrl,

    # What clients are told to reach the relay on: an IP and port. Omit to leave it as it is ("auto" means the
    # first address of this machine that is not loopback, which picks a VPN adapter when there is one).
    [string]$RelayPublicEndpoint,

    # PostgreSQL connection string. Pass an empty string to go back to the in-memory registry.
    [string]$PostgresConnectionString,

    # The signing key relay tickets are protected with. Only for a relay that runs elsewhere: both machines need the
    # same value, and changing it on one side cuts the other off until it is changed there too.
    [string]$RelaySharedKey,

    # A certificate file (.pfx) and its password: signaling goes to https with it.
    [string]$TlsCertificatePath,
    [string]$TlsCertificatePassword,

    # Or let this machine make its own certificate and switch to https. The public half is written to
    # tls\signaling.cer, which clients have to trust or they will refuse the connection.
    [switch]$TlsGenerate,

    # Leave the services alone: the settings are written but only take effect on the next restart.
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$rendezvous = Join-Path $installDirectory 'Rendezvous\RemoteDesktop.Server.Rendezvous.exe'
$services = @('RemoteDesktopRendezvous', 'RemoteDesktopRelay')

if (-not (Test-Path -LiteralPath $rendezvous)) {
    throw "This script belongs next to the servers it configures; $rendezvous is not there."
}

if ($TlsGenerate -and $TlsCertificatePath) {
    throw 'Give either -TlsGenerate or -TlsCertificatePath, not both.'
}

# The executable takes a flag per setting; only the ones that were actually given are passed on, so the rest stay as
# they are. -TlsGenerate is the executable's "make one" value rather than a flag of its own.
$arguments = @('--configure-system', '--install-directory', $installDirectory)
if ($PSBoundParameters.ContainsKey('RendezvousUrl')) { $arguments += @('--urls', $RendezvousUrl) }
if ($PSBoundParameters.ContainsKey('RelayPublicEndpoint')) { $arguments += @('--relay-endpoint', $RelayPublicEndpoint) }
if ($PSBoundParameters.ContainsKey('PostgresConnectionString')) { $arguments += @('--postgres', $PostgresConnectionString) }
if ($PSBoundParameters.ContainsKey('RelaySharedKey')) { $arguments += @('--relay-key', $RelaySharedKey) }
if ($TlsGenerate) { $arguments += @('--tls-certificate', 'generate') }
if ($PSBoundParameters.ContainsKey('TlsCertificatePath')) { $arguments += @('--tls-certificate', $TlsCertificatePath) }
if ($PSBoundParameters.ContainsKey('TlsCertificatePassword')) { $arguments += @('--tls-password', $TlsCertificatePassword) }

if ($arguments.Count -le 3) {
    throw 'Nothing to change: give at least one of -RendezvousUrl, -RelayPublicEndpoint, -PostgresConnectionString, -RelaySharedKey, -TlsCertificatePath, -TlsGenerate.'
}

$target = $arguments -join ' '
if (-not $PSCmdlet.ShouldProcess($installDirectory, $target)) {
    return
}

# Elevated from here on: writing next to the programs and restarting services both need it, and failing halfway is
# worse than not starting, so it is checked before anything is written.
$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    throw 'Run this from an elevated PowerShell: it writes the settings next to the programs and restarts services.'
}

& $rendezvous @arguments
if ($LASTEXITCODE -ne 0) {
    throw "The configuration step failed with exit code $LASTEXITCODE. Nothing was changed if it said so; server-setup.log in $installDirectory has the details."
}

Write-Host "Settings written. What it did is appended to $(Join-Path $installDirectory 'server-setup.log')."

if ($NoRestart) {
    Write-Host 'The services were not restarted (-NoRestart), so the new settings are not in effect yet.'
    return
}

foreach ($name in $services) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Write-Warning "The service $name is not installed; skipping it."
        continue
    }

    Restart-Service -Name $name -Force
    Write-Host "Restarted $name."
}
