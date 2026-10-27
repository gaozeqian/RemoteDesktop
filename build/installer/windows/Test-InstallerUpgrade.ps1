#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs, upgrades and uninstalls the three Windows MSIs and checks that the live settings survive.

.DESCRIPTION
  The package tests open the MSI database; they cannot see what a major upgrade does to files on disk. This script
  exercises that path on a disposable machine or CI runner:

    * install with a non-default address/password;
    * move the live settings into the old Program Files layout, delete the ProgramData copy, and upgrade - the
      migration action must copy the old settings forward;
    * change the live settings and upgrade again - the upgrade must not overwrite them;
    * uninstall with REMOVEDATA=1 and check the data and generated certificate are gone.

  It changes services, the firewall (unless the package's NOFIREWALL is used) and ProgramData. Run it on a machine you
  can throw away, and never on a workstation whose services matter.

  An upgrade is only an upgrade when the new package has a different ProductCode; installing the same file twice is a
  maintenance install, which skips every action conditioned on WIX_UPGRADE_DETECTED. Building every package twice just
  to obtain new identities would double the CI time, so this script writes the identities into copies of the built
  packages: ProductCode in the Property table and PackageCode in the summary information. The ProductVersion stays the
  same because MajorUpgrade is authored with AllowSameVersionUpgrades (see Package.wxs). The copies are identical to the
  shipped package in every other respect, so the sequences, conditions and custom actions are the real ones. If the
  build was signed, the copies are no longer signed; the test runs on a disposable runner and does not verify the
  signature.

.EXAMPLE
  .\Test-InstallerUpgrade.ps1
  # Uses the newest three MSIs under artifacts/installer/windows/.
#>
[CmdletBinding()]
param(
    [string]$ClientMsi = '',
    [string]$HostMsi = '',
    [string]$ServerMsi = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerFolder = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)))) 'artifacts\installer\windows'
function Find-Newest([string]$pattern) {
    $package = Get-ChildItem -Path (Join-Path $installerFolder $pattern) -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $package) { throw "No package matching $pattern under $installerFolder. Build the installers first." }
    return $package.FullName
}

if (-not $ClientMsi) { $ClientMsi = Find-Newest 'RemoteDesktop-Client-Windows-x64-*.msi' }
if (-not $HostMsi) { $HostMsi = Find-Newest 'RemoteDesktop-Host-Windows-x64-*.msi' }
if (-not $ServerMsi) { $ServerMsi = Find-Newest 'RemoteDesktop-Server-Windows-x64-*.msi' }

# A first install of the client MSI over an existing product is an upgrade, so ConfigureSystem is skipped and the
# fresh-install assertions below describe a machine that was never in that state. Refuse rather than upgrade (and
# later uninstall) somebody's real installation.
$installed = @()
foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
    $installed += Get-ChildItem -Path $root -ErrorAction SilentlyContinue | ForEach-Object {
        Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue
    } | Where-Object { $_.PSObject.Properties.Name -contains 'DisplayName' } |
        Where-Object { $_.DisplayName -like 'Remote Desktop*' }
}
if ($installed) {
    throw "This machine already has a Remote Desktop product installed ($(($installed | ForEach-Object { $_.DisplayName }) -join ', ')). Run this test on a clean machine; it installs, upgrades and uninstalls the packages."
}

# ProgramFiles64Folder is what the packages use; the script may itself be running as 32-bit PowerShell.
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$clientConfig = Join-Path $env:ProgramData 'RemoteDesktop\appsettings.json'
$clientCertificatePin = Join-Path $env:ProgramData 'RemoteDesktop\certificates\signaling.cer'
$clientInstall = Join-Path $programFiles 'RemoteDesktop'
$serverConfig = Join-Path $env:ProgramData 'Remote Desktop Server'
$serverInstall = Join-Path $programFiles 'Remote Desktop Server'

$workDirectory = Join-Path $env:TEMP ("rd-installer-upgrade-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDirectory | Out-Null
$createdPackages = @()

function Invoke-MsiExec {
    param([string[]]$Arguments, [string]$LogPath, [switch]$AllowMissing)
    $all = @($Arguments) + @('/norestart', '/l*v', "`"$LogPath`"")
    $process = Start-Process -FilePath msiexec.exe -ArgumentList $all -Wait -PassThru
    if ($process.ExitCode -eq 1605 -and $AllowMissing) { return }
    if ($process.ExitCode -notin 0, 3010, 1641) {
        $tail = if (Test-Path -LiteralPath $LogPath) { Get-Content -LiteralPath $LogPath -Tail 80 } else { @() }
        throw "msiexec $($Arguments -join ' ') failed with $($process.ExitCode).`n$($tail -join [Environment]::NewLine)"
    }
}

function Install-Msi([string]$Msi, [string[]]$Properties, [string]$Role) {
    Invoke-MsiExec -Arguments (@('/i', "`"$Msi`"", '/qn') + $Properties) -LogPath (Join-Path $env:TEMP "rd-${Role}-install.log")
}

function Uninstall-Msi([string]$Msi, [string[]]$Properties, [string]$Role, [switch]$AllowMissing) {
    Invoke-MsiExec -Arguments (@('/x', "`"$Msi`"", '/qn') + $Properties) -LogPath (Join-Path $env:TEMP "rd-${Role}-uninstall.log") -AllowMissing:$AllowMissing
}

function Read-Settings([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Expected settings file $Path, but it is not there." }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Assert-RendezvousUri([string]$Path, [string]$Expected) {
    $settings = Read-Settings $Path
    if ($settings.Transport.RendezvousUri -ne $Expected) {
        throw "$Path has Transport:RendezvousUri '$($settings.Transport.RendezvousUri)', expected '$Expected'."
    }
}

function Assert-RendezvousCertificate([string]$Path, [string]$SourcePath, [string]$PinPath) {
    $settings = Read-Settings $Path
    if ($settings.Transport.RendezvousCertificatePath -ne $PinPath) {
        throw "$Path has Transport:RendezvousCertificatePath '$($settings.Transport.RendezvousCertificatePath)', expected '$PinPath'."
    }
    if (-not (Test-Path -LiteralPath $PinPath)) {
        throw "The pinned certificate $PinPath is not there."
    }

    $source = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($SourcePath)
    try {
        $pin = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PinPath)
        try {
            if ($source.Thumbprint -ne $pin.Thumbprint) {
                throw "The pinned certificate $PinPath does not match $SourcePath."
            }
        }
        finally { $pin.Dispose() }
    }
    finally { $source.Dispose() }
}

function Set-RendezvousUri([string]$Path, [string]$Value) {
    $settings = Read-Settings $Path
    $settings.Transport.RendezvousUri = $Value
    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Stop-RdServices {
    foreach ($name in 'RemoteDesktopHost', 'RemoteDesktopRendezvous', 'RemoteDesktopRelay', 'RemoteDesktopOps') {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') {
            Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
        }
    }
}

# The Windows Installer API is used directly rather than through the COM automation object: the parameterized
# summary-information setter does not bind from PowerShell, and avoiding COM also keeps the script identical under
# Windows PowerShell 5.1 (local) and PowerShell 7 (the CI and release workflows).
if (-not ('MsiVariantEditor' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class MsiVariantEditor
{
    private const int OpenTransact = 1;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint MsiOpenDatabase(string szDatabasePath, IntPtr szPersist, out uint phDatabase);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint MsiDatabaseOpenView(uint hDatabase, string szQuery, out uint phView);

    [DllImport("msi.dll", SetLastError = true)]
    private static extern uint MsiViewExecute(uint hView, uint hRecord);

    [DllImport("msi.dll", SetLastError = true)]
    private static extern uint MsiViewClose(uint hView);

    [DllImport("msi.dll", SetLastError = true)]
    private static extern uint MsiDatabaseCommit(uint hDatabase);

    [DllImport("msi.dll", SetLastError = true)]
    private static extern uint MsiCloseHandle(uint hAny);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint MsiGetSummaryInformation(IntPtr hDatabase, string szDatabasePath, uint uiUpdateCount, out uint phSummaryInfo);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint MsiSummaryInfoSetProperty(uint hSummaryInfo, uint uiProperty, uint uiDataType, int iValue, IntPtr pftValue, string szValue);

    [DllImport("msi.dll", SetLastError = true)]
    private static extern uint MsiSummaryInfoPersist(uint hSummaryInfo);

    public static void SetProperty(string path, string property, string value)
    {
        var query = "UPDATE Property SET Value='" + value.Replace("'", "''") +
                    "' WHERE Property='" + property.Replace("'", "''") + "'";
        uint database;
        Check(MsiOpenDatabase(path, new IntPtr(OpenTransact), out database), "MsiOpenDatabase");
        try
        {
            uint view;
            Check(MsiDatabaseOpenView(database, query, out view), "MsiDatabaseOpenView");
            try
            {
                Check(MsiViewExecute(view, 0), "MsiViewExecute");
            }
            finally
            {
                MsiViewClose(view);
                MsiCloseHandle(view);
            }

            Check(MsiDatabaseCommit(database), "MsiDatabaseCommit");
        }
        finally
        {
            MsiCloseHandle(database);
        }
    }

    public static void SetPackageCode(string path, string packageCode)
    {
        uint summary;
        Check(MsiGetSummaryInformation(IntPtr.Zero, path, 1, out summary), "MsiGetSummaryInformation");
        try
        {
            // Property 9 is PID_REVNUMBER, which Windows Installer defines as the package code; VT_LPSTR is 30.
            Check(MsiSummaryInfoSetProperty(summary, 9, 30, 0, IntPtr.Zero, packageCode), "MsiSummaryInfoSetProperty");
            Check(MsiSummaryInfoPersist(summary), "MsiSummaryInfoPersist");
        }
        finally
        {
            MsiCloseHandle(summary);
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException(operation + " failed with " + result + ".");
    }
}
"@
}

function New-MsiVariant {
    [CmdletBinding()]
    param([string]$SourceMsi, [string]$DestinationMsi)

    Copy-Item -LiteralPath $SourceMsi -Destination $DestinationMsi -Force
    $productCode = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $packageCode = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    [void][MsiVariantEditor]::SetProperty($DestinationMsi, 'ProductCode', $productCode)
    [void][MsiVariantEditor]::SetPackageCode($DestinationMsi, $packageCode)

    return $DestinationMsi
}

function New-PackageVariants([string]$Msi, [string]$Role) {
    $old = New-MsiVariant -SourceMsi $Msi -DestinationMsi (Join-Path $workDirectory "$Role-old.msi")
    $later = New-MsiVariant -SourceMsi $Msi -DestinationMsi (Join-Path $workDirectory "$Role-later.msi")
    return @($old, $later)
}

# A self-signed public certificate for the wss:// installs. It is never used to serve TLS; the installers only have to
# export/pin its public half. Generating one here avoids depending on the PKI module or the certificate store.
function New-TestServerCertificate {
    $path = Join-Path $workDirectory 'server.cer'
    $key = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Remote Desktop Installer Test',
            $key,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $request.CreateSelfSigned((Get-Date).AddDays(-1), (Get-Date).AddDays(2))
        try {
            [System.IO.File]::WriteAllBytes(
                $path,
                $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }
        finally { $certificate.Dispose() }
    }
    finally { $key.Dispose() }
    return $path
}

try {
Write-Host '--- client/host: wss address and server certificate pin'

$testCertificate = New-TestServerCertificate

$tlsClientAddress = 'wss://127.0.0.1:5199/ws'
$createdPackages += $ClientMsi
Install-Msi $ClientMsi @("RENDEZVOUSURI=$tlsClientAddress", "SERVERCERTIFICATEPATH=$testCertificate") 'client-tls'
Assert-RendezvousUri $clientConfig $tlsClientAddress
Assert-RendezvousCertificate $clientConfig $testCertificate $clientCertificatePin
Uninstall-Msi $ClientMsi @('REMOVEDATA=1') 'client-tls'
if (Test-Path -LiteralPath (Join-Path $env:ProgramData 'RemoteDesktop')) {
    throw 'REMOVEDATA=1 left %ProgramData%\RemoteDesktop behind after the client TLS install.'
}

$tlsHostAddress = 'wss://127.0.0.1:5499/ws'
$createdPackages += $HostMsi
Install-Msi $HostMsi @("RENDEZVOUSURI=$tlsHostAddress", "SERVERCERTIFICATEPATH=$testCertificate") 'host-tls'
Assert-RendezvousUri $clientConfig $tlsHostAddress
Assert-RendezvousCertificate $clientConfig $testCertificate $clientCertificatePin
Uninstall-Msi $HostMsi @('REMOVEDATA=1') 'host-tls'
if (Test-Path -LiteralPath (Join-Path $env:ProgramData 'RemoteDesktop')) {
    throw 'REMOVEDATA=1 left %ProgramData%\RemoteDesktop behind after the host TLS install.'
}

Write-Host '--- client: install, migrate the old layout, upgrade preserves the address, uninstall'

$clientVariants = New-PackageVariants $ClientMsi 'client'
$clientOld = $clientVariants[0]
$clientLater = $clientVariants[1]
$createdPackages += @($clientOld, $ClientMsi, $clientLater)

$clientAddress = 'ws://127.0.0.1:5199/ws'
Install-Msi $clientOld @("RENDEZVOUSURI=$clientAddress") 'client-old'
Assert-RendezvousUri $clientConfig $clientAddress

# The pre-fix layout kept the live file under Program Files. Simulate an upgrade from it: put the live file there,
# remove the ProgramData copy, change the address, and install the real package. The migration action runs before
# RemoveExistingProducts copies the new package's files and removes the old product.
$oldAddress = 'ws://127.0.0.1:5299/ws'
Set-RendezvousUri (Join-Path $clientInstall 'appsettings.json') $oldAddress
# The host service watches its settings file; stop it before removing the ProgramData copy so it cannot recreate the
# file (and so the upgrade has to use the migration action, which is what this step verifies).
Stop-RdServices
Remove-Item -LiteralPath $clientConfig -Force
Install-Msi $ClientMsi @() 'client-real'
Assert-RendezvousUri $clientConfig $oldAddress

# A normal upgrade must not replace what the operator changed.
$newAddress = 'ws://127.0.0.1:5399/ws'
Set-RendezvousUri $clientConfig $newAddress
Install-Msi $clientLater @() 'client-later'
Assert-RendezvousUri $clientConfig $newAddress

Uninstall-Msi $clientLater @('REMOVEDATA=1') 'client'
if (Test-Path -LiteralPath (Join-Path $env:ProgramData 'RemoteDesktop')) {
    throw 'REMOVEDATA=1 left %ProgramData%\RemoteDesktop behind.'
}

Write-Host '--- host: unattended password file, upgrade preserves the address, uninstall'

$hostVariants = New-PackageVariants $HostMsi 'host'
$hostOld = $hostVariants[0]
$hostLater = $hostVariants[1]
$createdPackages += @($hostOld, $HostMsi, $hostLater)

$hostAddress = 'ws://127.0.0.1:5499/ws'
Install-Msi $hostOld @("RENDEZVOUSURI=$hostAddress", 'FIXEDPASSWORD=TestPassword-123') 'host-old'
Assert-RendezvousUri $clientConfig $hostAddress
if (Get-ChildItem -LiteralPath (Join-Path $env:ProgramData 'RemoteDesktop') -Filter '.fixed-password' -Force -ErrorAction SilentlyContinue) {
    throw 'The fixed-password temporary file was not deleted after the service read it.'
}
if (-not (Get-Service -Name RemoteDesktopHost -ErrorAction SilentlyContinue)) {
    throw 'The host package did not install the RemoteDesktopHost service.'
}

$hostAddress2 = 'ws://127.0.0.1:5599/ws'
Set-RendezvousUri $clientConfig $hostAddress2
Install-Msi $HostMsi @() 'host-real'
Assert-RendezvousUri $clientConfig $hostAddress2
Install-Msi $hostLater @() 'host-later'
Assert-RendezvousUri $clientConfig $hostAddress2

Uninstall-Msi $hostLater @('REMOVEDATA=1') 'host'
if (Test-Path -LiteralPath (Join-Path $env:ProgramData 'RemoteDesktop')) {
    throw 'REMOVEDATA=1 left %ProgramData%\RemoteDesktop behind after the host uninstall.'
}

Write-Host '--- server: configured live files, migration, upgrade, data removal'

$serverVariants = New-PackageVariants $ServerMsi 'server'
$serverOld = $serverVariants[0]
$serverLater = $serverVariants[1]
$createdPackages += @($serverOld, $ServerMsi, $serverLater)

Install-Msi $serverOld @(
    'RENDEZVOUSURL=http://0.0.0.0:5080',
    'RELAYPUBLICENDPOINT=127.0.0.1:5082',
    'DATABASEPROVIDER=sqlite',
    'TLSCERTIFICATEPATH=auto',
    'NOFIREWALL=1'
) 'server-old'

$rendezvousSettings = Join-Path $serverConfig 'Rendezvous\appsettings.json'
$relaySettings = Join-Path $serverConfig 'Relay\appsettings.json'
$opsSettings = Join-Path $serverConfig 'Ops\appsettings.json'
$rendezvous = Read-Settings $rendezvousSettings
$relay = Read-Settings $relaySettings
$ops = Read-Settings $opsSettings
if (-not $ops.Ops.Database.Path) { throw 'The console settings have no database path.' }
if ($rendezvous.Rendezvous.MetricsEnabled -ne $true) { throw 'The server package did not enable /metrics.' }
if (-not $rendezvous.Rendezvous.RelaySharedKey) { throw 'The rendezvous settings have no relay shared key.' }
if ($rendezvous.Rendezvous.RelaySharedKey -ne $relay.Relay.SharedKey) {
    throw 'Rendezvous and Relay do not share the same relay key.'
}
if (-not (Test-Path -LiteralPath (Join-Path $serverConfig 'server-setup.log'))) {
    throw 'server-setup.log was not written under ProgramData.'
}
if (-not (Test-Path -LiteralPath (Join-Path $serverConfig 'ops.db'))) {
    throw 'The Ops database was not created under ProgramData.'
}
foreach ($service in 'RemoteDesktopRendezvous', 'RemoteDesktopRelay', 'RemoteDesktopOps') {
    if (-not (Get-Service -Name $service -ErrorAction SilentlyContinue)) {
        throw "The server package did not install the $service service."
    }
}

# Simulate the old layout: live files under Program Files, no ProgramData copy. The migration must bring them back.
# Stop the services first: Ops keeps its SQLite database open, and deleting the directory under a running service can
# fail with a sharing violation (or let it recreate the database before the upgrade).
foreach ($role in 'Rendezvous', 'Relay', 'Ops') {
    Copy-Item -LiteralPath (Join-Path $serverConfig "$role\appsettings.json") -Destination (Join-Path $serverInstall "$role\appsettings.json") -Force
}
$markerKey = $rendezvous.Rendezvous.RelaySharedKey
Stop-RdServices
Remove-Item -LiteralPath $serverConfig -Recurse -Force
Install-Msi $ServerMsi @('NOFIREWALL=1') 'server-real'
$migrated = Read-Settings $rendezvousSettings
if ($migrated.Rendezvous.RelaySharedKey -ne $markerKey) {
    throw 'The first-upgrade migration did not preserve the relay key.'
}
$relayMigrated = Read-Settings $relaySettings
if ($relayMigrated.Relay.SharedKey -ne $migrated.Rendezvous.RelaySharedKey) {
    throw 'The first upgrade did not write the relay key into the Relay settings.'
}
if ($migrated.Rendezvous.MetricsEnabled -ne $true) {
    throw 'The first-upgrade migration lost MetricsEnabled.'
}

# A normal upgrade must not overwrite the live settings, but it must repair the relay key: an upgrade skips
# ConfigureServer, and before SyncRelayKey existed the Relay kept an empty SharedKey while Rendezvous had the key.
$migrated.Rendezvous.MetricsEnabled = $false
$migrated | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $rendezvousSettings -Encoding UTF8
$relayWithoutKey = Read-Settings $relaySettings
$relayWithoutKey.Relay.SharedKey = ''
$relayWithoutKey | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $relaySettings -Encoding UTF8
Install-Msi $serverLater @('NOFIREWALL=1') 'server-later'
$afterUpgrade = Read-Settings $rendezvousSettings
if ($afterUpgrade.Rendezvous.MetricsEnabled -ne $false) {
    throw 'A normal server upgrade overwrote the operator''s MetricsEnabled setting.'
}
$relayAfterUpgrade = Read-Settings $relaySettings
if ($relayAfterUpgrade.Relay.SharedKey -ne $afterUpgrade.Rendezvous.RelaySharedKey) {
    throw 'A normal server upgrade did not repair the Relay SharedKey.'
}

Uninstall-Msi $serverLater @('REMOVEDATA=1') 'server'
if (Test-Path -LiteralPath $serverConfig) {
    throw 'REMOVEDATA=1 left %ProgramData%\Remote Desktop Server behind.'
}
foreach ($role in 'Rendezvous', 'Relay', 'Ops') {
    if (Get-Service -Name "RemoteDesktop$role" -ErrorAction SilentlyContinue) {
        throw "The $role service was left installed."
    }
}

Write-Host 'All installer install/upgrade/uninstall checks passed.'
}
finally {
    # A failed check must not leave three services and a firewall rule on the machine. The normal path has already
    # uninstalled them; msiexec reports 1605 for a product that is not installed, which is ignored here.
    Stop-RdServices
    $cleanupPackages = @($createdPackages | Select-Object -Unique)
    [array]::Reverse($cleanupPackages)
    foreach ($package in $cleanupPackages) {
        $role = 'cleanup-' + [System.IO.Path]::GetFileNameWithoutExtension($package)
        try { Uninstall-Msi $package @('REMOVEDATA=1') $role -AllowMissing }
        catch { Write-Warning "Cleanup of $package was not needed or did not finish: $($_.Exception.Message)" }
    }
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
