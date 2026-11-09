# Generates the three server-package quick guides from one source.
# The complete operational manual remains docs/ops/README.md; these files are offline, platform-specific entry points.
[CmdletBinding()]
param(
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$generatedBy = 'GENERATED FILE — source: build/tools/Export-ServerConfigurationGuides.ps1. Do not edit directly.'
$manualUrl = 'https://github.com/gaozeqian/RemoteDesktop/blob/master/docs/ops/README.md'

function Get-LinuxGuide {
    @"
$generatedBy

Remote Desktop Server (Linux) — post-install quick guide
=========================================================

The package installs Rendezvous, Relay, Ops and the HTTPS Gateway. Live settings are in
/etc/remotedesktop-server/{Rendezvous,Relay,Ops,Gateway}/appsettings.json; the generated
signaling/Gateway public certificate is /etc/remotedesktop-server/tls/signaling.crt.

Complete deployment, TLS, database, backup and troubleshooting instructions:
  $manualUrl

1. Open the Ops console
-----------------------

Ops listens only on http://127.0.0.1:5090. Gateway is the remote entry point:

    https://<server-ip>:5443

Gateway uses the generated certificate, whose IP SAN contains this machine's IPv4 addresses.
On each administration computer, copy and verify the public certificate, then trust it:

    sudo install -m 0644 /etc/remotedesktop-server/tls/signaling.crt /tmp/ops-gateway.crt
    openssl x509 -in /tmp/ops-gateway.crt -noout -fingerprint -sha256
    scp <admin>@<server-ip>:/tmp/ops-gateway.crt ./ops-gateway.crt

Do not expose Ops port 5090. With ufw enabled, either set RD_OPEN_FIREWALL="1" in
/etc/default/remotedesktop-server and run dpkg-reconfigure, or allow TCP 5443 manually.

2. Create and recover administrators
------------------------------------

There is no default account or web bootstrap page:

    sudo -u remotedesktop /usr/lib/remotedesktop-server/Ops/RemoteDesktop.Server.Ops \
      --create-admin admin --config-directory /etc/remotedesktop-server/Ops
    sudo systemctl restart remotedesktop-ops

The command asks for a password and prints a TOTP secret once. If the TOTP secret is lost,
reset the existing account; this revokes its active sessions and prints a new enrolment URI:

    sudo -u remotedesktop /usr/lib/remotedesktop-server/Ops/RemoteDesktop.Server.Ops \
      --reset-totp admin --config-directory /etc/remotedesktop-server/Ops

3. Change configuration and inspect health
------------------------------------------

Use debconf for database/TLS changes; /etc/default/remotedesktop-server supplies runtime
values such as RD_URLS, RD_RELAY_ENDPOINT, RD_RELAY_KEY and RD_GATEWAY_LISTEN:

    sudo dpkg-reconfigure remotedesktop-server
    systemctl status remotedesktop-rendezvous remotedesktop-relay remotedesktop-ops remotedesktop-gateway
    curl http://127.0.0.1:5090/health
    curl -k https://127.0.0.1:5443/health    # expected: 404 (Gateway does not expose Ops health)

Logs are in journalctl -u remotedesktop-{rendezvous,relay,ops,gateway}. Remove /tmp/ops-gateway.crt
when it is no longer needed. apt purge remotedesktop-server removes configuration, database and certificates.
"@
}

function Get-MacOSGuide {
    @"
$generatedBy

Remote Desktop Server (macOS) — post-install quick guide
=========================================================

The package installs Rendezvous, Relay, Ops and the HTTPS Gateway in
/usr/local/remotedesktop-server. Live appsettings.json files are root:_remotedesktop with
mode 0640: root writes them and the service group reads them. They can contain the relay key
and PostgreSQL password; do not weaken those permissions.

Complete deployment, TLS, database, backup and troubleshooting instructions:
  $manualUrl

1. Configure and open the Ops console
-------------------------------------

Run as an administrator. Omitted options keep their current values; the script restarts all
four daemons unless --no-restart is supplied:

    cd /usr/local/remotedesktop-server
    sudo ./configure-server.sh --database sqlite
    sudo ./configure-server.sh --help

Ops listens only on http://127.0.0.1:5090. Access it remotely through Gateway:

    https://<server-ip>:5443

The Gateway certificate is exported to:

    /usr/local/share/doc/remotedesktop-server/ops-gateway.crt

Copy it to each administration computer, verify its SHA-256 fingerprint through a trusted
channel, and trust it in that computer's system keychain. Do not expose Ops port 5090.

2. Create and recover administrators
------------------------------------

There is no default account or web bootstrap page:

    sudo -u _remotedesktop /usr/local/remotedesktop-server/Ops/RemoteDesktop.Server.Ops --create-admin admin
    sudo launchctl kickstart -k system/com.remotedesktop.ops

The command asks for a password and prints a TOTP secret once. If the TOTP secret is lost,
reset the existing account; this revokes its active sessions and prints a new enrolment URI:

    sudo -u _remotedesktop /usr/local/remotedesktop-server/Ops/RemoteDesktop.Server.Ops --reset-totp admin

3. Health, logs and removal
---------------------------

    curl http://127.0.0.1:5080/health
    curl http://127.0.0.1:5090/health
    tail -f /usr/local/remotedesktop-server/logs/ops.log
    sudo /usr/local/remotedesktop-server/uninstall-server.sh

The uninstaller stops all four daemons and removes the install directory, documentation and
package receipt. Add --keep-settings to retain settings, logs and exported certificates.
"@
}

function Get-WindowsGuide {
    @"
$generatedBy

Remote Desktop Server (Windows) — post-install quick guide
===========================================================

The MSI installs Rendezvous, Relay, Ops and Gateway under Program Files. Live settings,
databases, certificate public keys and server-setup.log are under:

    %ProgramData%\RemoteDesktopServer\{Rendezvous,Relay,Ops,Gateway}\appsettings.json

This directory is readable only by SYSTEM and Administrators because it can contain the relay
key and PostgreSQL password. Complete deployment, TLS, database, backup and troubleshooting
instructions:
  $manualUrl

1. Configure and open the Ops console
-------------------------------------

In an elevated PowerShell or Command Prompt:

    cd "C:\Program Files\RemoteDesktopServer"
    .\Set-ServerConfiguration.cmd -DatabaseProvider sqlite
    .\Set-ServerConfiguration.cmd -WhatIf -GatewayHostName ops.example.com

Ops listens only on http://127.0.0.1:5090. Gateway is the remote entry point:

    https://<server-ip>:5443

Trust %ProgramData%\RemoteDesktopServer\tls\signaling.cer on every administration computer
before opening Gateway. Do not expose Ops port 5090.

2. Create and recover administrators
------------------------------------

There is no default account or web bootstrap page. In an elevated PowerShell:

    & "C:\Program Files\RemoteDesktopServer\Ops\RemoteDesktop.Server.Ops.exe" --create-admin admin
    Restart-Service RemoteDesktopOps

The command asks for a password and prints a TOTP secret once. If the TOTP secret is lost,
reset the existing account; this revokes its active sessions and prints a new enrolment URI:

    & "C:\Program Files\RemoteDesktopServer\Ops\RemoteDesktop.Server.Ops.exe" --reset-totp admin
    Restart-Service RemoteDesktopOps

3. Health, logs and removal
---------------------------

    curl http://127.0.0.1:5090/health
    sc.exe query RemoteDesktopOps
    Get-WinEvent -LogName Application -MaxEvents 50
    msiexec /x RemoteDesktop-Server-Windows-x64-<version>.msi /qn

Gateway intentionally returns 404 for /health. Uninstalling removes the ProgramData settings,
databases and generated certificate; use REMOVEDATA=0 to keep them for a reinstall.
"@
}

$files = [ordered]@{
    'build/installer/linux/server/how-to-configure.txt' = Get-LinuxGuide
    'build/installer/macos/server/how-to-configure.txt' = Get-MacOSGuide
    'build/installer/windows/server/how-to-configure.txt' = Get-WindowsGuide
}

$changed = $false
foreach ($entry in $files.GetEnumerator()) {
    $path = Join-Path $repositoryRoot $entry.Key
    # The Linux/macOS package files are executed or copied from any checkout and must stay LF; the Windows copy follows
    # its .gitattributes CRLF contract. Do not use Environment.NewLine for every target while running on Windows.
    $lineEnding = if ($entry.Key -like '*windows*') { "`r`n" } else { "`n" }
    $content = $entry.Value.TrimEnd("`r", "`n").Replace("`r`n", "`n").Replace("`n", $lineEnding) + $lineEnding
    $existing = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllText($path) } else { $null }

    if ($existing -ceq $content) {
        continue
    }

    if ($Check) {
        Write-Error "$($entry.Key) is not generated from this script. Run pwsh -File build/tools/Export-ServerConfigurationGuides.ps1."
        $changed = $true
        continue
    }

    [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
    Write-Host "Wrote $($entry.Key)"
}

if ($Check -and $changed) {
    exit 1
}
