<#
.SYNOPSIS
  Creates a certificate for the signaling server and exports it for the server and for the machines that trust it.

.DESCRIPTION
  For a deployment whose clients reach the server by a name no public authority knows: an address on a local network,
  or a machine in a workshop that has no domain. TLS is what keeps the signaling channel from being read or changed on
  the way, and TLS needs a certificate the client will accept - which, without an authority, means one you install on
  the client machines yourself.

  The certificate is exported as:
    <Name>.pfx  the certificate *with* its private key - it goes on the server, and nowhere else
    <Name>.cer  the certificate alone - it goes into the installer that the client machines run

  It is not put into any certificate store: the server reads the .pfx, and the files are the only copies.

  The server side then needs two settings in its appsettings.json (see docs/deployment.md):
    "Urls": "https://0.0.0.0:5080"
    "Kestrel": { "Certificates": { "Default": { "Path": "<Name>.pfx", "Password": "<the password>" } } }

  And the client side is one step in the installer build, which puts the .cer into the MSI so that every machine
  installing it trusts this certificate:
    build-installer.ps1 ... -ServerCertificate <Name>.cer

  The address has to be in the certificate: clients compare what they dialed against the names and addresses listed
  here, so give it every name and address they will use - a short name, a full name, and the address itself.

  Keep the .pfx safe. A self-signed certificate installed as a trusted root can vouch for any name on the machines
  that trust it, so whoever holds this key can pretend to be any server those machines talk to.

.EXAMPLE
  .\New-ServerCertificate.ps1 -DnsName rd.lan,rd -IpAddress 192.168.1.10
.EXAMPLE
  .\New-ServerCertificate.ps1 -DnsName rd.example.com -Years 3 -Password (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    # Names the clients will dial, the first one becomes the subject. localhost is always added.
    [string[]]$DnsName = @('rd.lan'),

    # Addresses the clients will dial, e.g. 192.168.1.10.
    [string[]]$IpAddress = @(),

    # Subject of the certificate. Empty derives it from the first name.
    [string]$Subject = '',

    # How long the certificate is valid. Long on purpose - see New-SigningCertificate.ps1 for the same reasoning.
    [int]$Years = 3,

    # Where the .pfx and .cer are written. Empty means a "certificates" folder next to this script.
    [string]$OutDirectory = '',

    # Password for the .pfx. Asked for interactively when not given; pass an empty string for a passwordless file.
    [securestring]$Password
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is empty in parameter defaults on Windows PowerShell 5.1, so this default lives here.
if (-not $OutDirectory) { $OutDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'certificates' }

# The server is reached as "localhost" by something on the same machine sooner or later (a health check, the
# deployment's own smoke test); a certificate that does not cover it turns that into a certificate error.
$names = @($DnsName + 'localhost' | Where-Object { $_ } | Select-Object -Unique)
$addresses = @($IpAddress | Where-Object { $_ } | ForEach-Object { [System.Net.IPAddress]::Parse($_) })
if (-not $Subject) { $Subject = "CN=$($names[0])" }

if (-not $Password) {
    $Password = Read-Host -Prompt "Password for the .pfx (empty for none)" -AsSecureString
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
$name = ($Subject -replace '^CN=', '') -replace '[^\w\.-]', '-'

New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$pfxPath = Join-Path $OutDirectory "$name.pfx"
$cerPath = Join-Path $OutDirectory "$name.cer"

# Built through CertificateRequest rather than New-SelfSignedCertificate: the addresses a local deployment is dialed
# by have to be *address* entries in the subject alternative names, and the text form New-SelfSignedCertificate takes
# refuses an IP= entry outright.
$key = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $Subject,
        $key,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    $alternativeNames = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    foreach ($entry in $names) { $alternativeNames.AddDnsName($entry) }
    foreach ($entry in $addresses) { $alternativeNames.AddIpAddress($entry) }
    $request.CertificateExtensions.Add($alternativeNames.Build())

    $purposes = [System.Security.Cryptography.OidCollection]::new()
    $purposes.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1')) | Out-Null   # server authentication
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($purposes, $false))
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment,
            $false))

    $certificate = $request.CreateSelfSigned(
        [System.DateTimeOffset]::UtcNow.AddDays(-1),
        [System.DateTimeOffset]::UtcNow.AddYears($Years))

    try {
        # Written with the certificate's own Export: it never went into a store, so there is nothing for
        # Export-PfxCertificate to read it back from.
        [System.IO.File]::WriteAllBytes(
            $pfxPath,
            $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $plain))
        [System.IO.File]::WriteAllBytes(
            $cerPath,
            $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }
    finally {
        $certificate.Dispose()
    }
}
finally {
    $key.Dispose()
}

Write-Host "Created $Subject"
Write-Host "  valid until:  $((Get-Date).AddYears($Years).ToString('yyyy-MM-dd'))"
Write-Host "  covers:       DNS=$($names -join ', DNS=')"
if ($addresses.Count) { Write-Host "                IP=$($addresses -join ', IP=')" }
Write-Host "  server file:  $pfxPath"
Write-Host "  trust file:   $cerPath"
Write-Host ''
Write-Host 'On the signaling server, point Kestrel at the .pfx and serve https (docs/deployment.md):'
Write-Host '  "Urls": "https://0.0.0.0:5080",'
Write-Host "  `"Kestrel`": { `"Certificates`": { `"Default`": { `"Path`": `"$pfxPath`", `"Password`": `<the password>` } } }"
Write-Host ''
Write-Host 'Build the installer that makes client machines trust it:'
Write-Host "  pwsh -File build/installer/client/windows/build-installer.ps1 -RendezvousUri wss://$($names[0]):5080/ws -ServerCertificate `"$cerPath`""
Write-Host ''
Write-Host 'Machines that already have the software can be given the certificate without reinstalling, as an administrator:'
Write-Host "  Import-Certificate -FilePath `"$cerPath`" -CertStoreLocation Cert:\LocalMachine\Root"
Write-Host ''
if ([string]::IsNullOrEmpty($plain)) {
    Write-Warning 'The .pfx has no password: keep it somewhere only the server can read it.'
}
