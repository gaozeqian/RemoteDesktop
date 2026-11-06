<#
.SYNOPSIS
  Changes what the installed servers are configured with, after the installation.

.DESCRIPTION
  The installer asks for these once. This is the way back in: give it the settings you want to change and it writes
  them, then restarts the services so they take effect. Anything you leave out is left alone.

  It runs the same configuration step the installer does (Rendezvous\RemoteDesktop.Server.Rendezvous.exe
  --configure-system), so the two cannot drift apart, and what it did is appended to server-setup.log in
  the settings folder (%ProgramData%\Remote Desktop Server), not the install folder.

.EXAMPLE
  # Keep device codes across restarts, which is the one setting whose default has the worst consequence: without it
  # they live in memory and change on every restart, and unattended machines stop being reachable.
  .\Set-ServerConfiguration.ps1 -DatabaseProvider sqlite
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

    # Where device codes are kept: auto, none, postgres or sqlite. "auto" keeps the PostgreSQL this machine already
    # has and otherwise starts a SQLite database of its own. Omit to leave it as it is.
    [string]$DatabaseProvider,

    # The SQLite file, when the provider is sqlite. Omit for the default under %ProgramData%\Remote Desktop Server.
    [string]$DatabasePath,

    # PostgreSQL connection string. Pass an empty string to stop using PostgreSQL; a value here means PostgreSQL
    # whatever -DatabaseProvider says.
    [string]$PostgresConnectionString,

    # The signing key relay tickets are protected with. Only for a relay that runs elsewhere: both machines need the
    # same value, and changing it on one side cuts the other off until it is changed there too.
    [string]$RelaySharedKey,

    # A certificate file (.pfx) and its password: signaling goes to https with it.
    [string]$TlsCertificatePath,
    [string]$TlsCertificatePassword,

    # DNS names (comma/semicolon separated) clients dial. Only used with -TlsGenerate: a self-signed certificate only
    # answers the names put in it. Empty uses the machine name.
    [string]$TlsHostName,

    # The console's own settings. Omit to leave them as they are.
    [string]$OpsListen,
    [string]$OpsDatabasePath,

    # The public HTTPS listener of the Ops reverse proxy, e.g. https://0.0.0.0:5443. The generated certificate covers
    # this machine's IPv4 addresses; use -GatewayHostName for an address or name that is not assigned locally.
    [string]$GatewayListen,
    [string]$GatewayHostName,

    # Or let this machine make its own certificate and switch to https. The public half is written to
    # tls\signaling.cer, which clients have to trust or they will refuse the connection.
    [switch]$TlsGenerate,

    # Leave the services alone: the settings are written but only take effect on the next restart.
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
# The programs live under Program Files; the live settings live in ProgramData, where an MSI major upgrade does not
# remove them. This is the same split the server programs themselves use.
$configDirectory = Join-Path $env:ProgramData 'Remote Desktop Server'
$rendezvous = Join-Path $installDirectory 'Rendezvous\RemoteDesktop.Server.Rendezvous.exe'
$ops = Join-Path $installDirectory 'Ops\RemoteDesktop.Server.Ops.exe'
$services = @('RemoteDesktopRendezvous', 'RemoteDesktopRelay', 'RemoteDesktopOps', 'RemoteDesktopGateway')

if (-not (Test-Path -LiteralPath $rendezvous)) {
    throw "This script belongs next to the servers it configures; $rendezvous is not there."
}

if ($TlsGenerate -and $TlsCertificatePath) {
    throw 'Give either -TlsGenerate or -TlsCertificatePath, not both.'
}

if ($PSBoundParameters.ContainsKey('TlsCertificatePassword') -and -not $PSBoundParameters.ContainsKey('TlsCertificatePath') -and -not $TlsGenerate) {
    throw '-TlsCertificatePassword is only used together with -TlsCertificatePath (or -TlsGenerate).'
}

# The executable takes a flag per setting; only the ones that were actually given are passed on, so the rest stay as
# they are. -TlsGenerate is the executable's "make one" value rather than a flag of its own.
$arguments = @('--configure-system', '--install-directory', $installDirectory, '--config-directory', $configDirectory)
if ($PSBoundParameters.ContainsKey('RendezvousUrl')) { $arguments += @('--urls', $RendezvousUrl) }
if ($PSBoundParameters.ContainsKey('RelayPublicEndpoint')) { $arguments += @('--relay-endpoint', $RelayPublicEndpoint) }
if ($PSBoundParameters.ContainsKey('DatabaseProvider')) { $arguments += @('--database', $DatabaseProvider) }
if ($PSBoundParameters.ContainsKey('DatabasePath')) { $arguments += @('--database-path', $DatabasePath) }
if ($PSBoundParameters.ContainsKey('PostgresConnectionString')) { $arguments += @('--postgres', $PostgresConnectionString) }
if ($PSBoundParameters.ContainsKey('RelaySharedKey')) { $arguments += @('--relay-key', $RelaySharedKey) }
if ($PSBoundParameters.ContainsKey('TlsHostName')) { $arguments += @('--tls-hostname', $TlsHostName) }
if ($PSBoundParameters.ContainsKey('OpsListen')) { $arguments += @('--ops-listen', $OpsListen) }
if ($PSBoundParameters.ContainsKey('OpsDatabasePath')) { $arguments += @('--ops-database-path', $OpsDatabasePath) }
if ($PSBoundParameters.ContainsKey('GatewayListen')) { $arguments += @('--gateway-listen', $GatewayListen) }
if ($PSBoundParameters.ContainsKey('GatewayHostName')) { $arguments += @('--gateway-hostname', $GatewayHostName) }
if ($TlsGenerate) {
    $arguments += @('--tls-certificate', 'generate')
    # The console must pin the public half the server just generated: it is in LocalMachine\My, not in the system
    # trust store, so Ops would reject its own node's certificate without this.
    $arguments += @('--ops-node-certificate-path', (Join-Path $configDirectory 'tls\signaling.cer'))
}
if ($PSBoundParameters.ContainsKey('TlsCertificatePath')) { $arguments += @('--tls-certificate', $TlsCertificatePath) }
if ($PSBoundParameters.ContainsKey('TlsCertificatePassword')) { $arguments += @('--tls-password', $TlsCertificatePassword) }

if ($arguments.Count -le 5) {
    throw 'Nothing to change: give at least one of -RendezvousUrl, -RelayPublicEndpoint, -DatabaseProvider, -DatabasePath, -PostgresConnectionString, -RelaySharedKey, -TlsCertificatePath, -TlsHostName, -TlsGenerate, -OpsListen, -OpsDatabasePath, -GatewayListen, -GatewayHostName.'
}

# The WhatIf target names the settings, never their values: a connection string or relay key printed here would end up
# in a transcript. ShouldProcess still says what will be done and to which directory.
$given = $PSBoundParameters.Keys |
    Where-Object { $_ -notin @('Verbose', 'Debug', 'WhatIf', 'Confirm') } |
    Sort-Object
if (-not $PSCmdlet.ShouldProcess($configDirectory, "apply: $($given -join ', ')")) {
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
    throw "The configuration step failed with exit code $LASTEXITCODE. Nothing was changed if it said so; server-setup.log in $configDirectory has the details."
}

Write-Host "Settings written. What it did is appended to $(Join-Path $configDirectory 'server-setup.log')."

# The console is part of this package, so the two local nodes should follow whatever scheme/port just took effect.
# Seed adds them only when the node table is empty; sync updates the two rows the installer created, without touching
# an operator's own nodes or a row they deleted. Both are safe to repeat.
if (Test-Path -LiteralPath $ops) {
    $rendezvousSettings = Join-Path $configDirectory 'Rendezvous\appsettings.json'
    $urls = $null
    if (Test-Path -LiteralPath $rendezvousSettings) {
        try { $urls = (Get-Content -LiteralPath $rendezvousSettings -Raw | ConvertFrom-Json).Urls } catch { }
    }

    $rendezvousNodeUrl = 'http://127.0.0.1:5080'
    if ($urls -and $urls -match '^https://') {
        $port = 5080
        try { $port = ([Uri]$urls).Port } catch { }
        if ($port -le 0) { $port = 5080 }
        $rendezvousNodeUrl = "https://127.0.0.1:$port"
    }
    elseif ($urls) {
        try {
            $port = ([Uri]$urls).Port
            if ($port -gt 0) { $rendezvousNodeUrl = "http://127.0.0.1:$port" }
        }
        catch { }
    }

    $opsArguments = @(
        '--config-directory', (Join-Path $configDirectory 'Ops'),
        '--rendezvous-base-url', $rendezvousNodeUrl,
        '--relay-base-url', 'http://127.0.0.1:5083'
    )
    & $ops --seed-local-nodes @opsArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'The local nodes could not be registered; add them on the console''s 节点管理 page.'
    }
    & $ops --sync-local-nodes @opsArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'The local node addresses could not be updated; check them on the console''s 节点管理 page.'
    }
}

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
