<#
.SYNOPSIS
  Creates a self-signed code-signing certificate for this product and exports it for signing and for trusting.

.DESCRIPTION
  For signing builds that you or your organisation install: a self-signed certificate is trusted on the machines where
  its public part is installed (see Install-TrustedCertificate.ps1), which is enough for an internal fleet - and it is
  what makes AppLocker/WDAC style rules ("only signed programs may run") usable.

  It is NOT a replacement for a certificate from a certificate authority when the software goes to machines you do not
  control: those machines have no reason to trust this certificate, so Windows still shows "unknown publisher" and
  SmartScreen still warns. See README.md in this folder for the alternatives and their cost.

  The certificate is created in the current user's store (no administrator needed) and exported as:
    <Name>.pfx  the certificate *with* its private key - the secret that signs builds
    <Name>.cer  the certificate alone - the part that gets installed on the machines that should trust it

.EXAMPLE
  .\New-SigningCertificate.ps1                       # five years, certificate password asked for
  .\New-SigningCertificate.ps1 -Years 10 -Subject 'CN=Contoso IT' -Password (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=RemoteDesktop',

    # How long the certificate is valid. Longer than you would get from a certificate authority, on purpose: for an
    # internal certificate nobody enforces the renewal, and a build that stops verifying after a year is a nasty
    # surprise. Keep the private key safe instead.
    [int]$Years = 5,

    # Where the .pfx and .cer are written. Empty means a "certificates" folder next to this script.
    [string]$OutDirectory = '',

    # Password for the .pfx. Asked for interactively when not given; pass an empty string for a passwordless file
    # (convenient, but then anybody who can read the file can sign as you).
    [securestring]$Password,

    # Take the certificate out of the current user's store again (the exported files are left alone).
    [switch]$RemoveFromStore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is empty in parameter defaults on Windows PowerShell 5.1, so this default lives here.
if (-not $OutDirectory) { $OutDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'certificates' }

if ($RemoveFromStore) {
    $name = ($Subject -replace '^CN=', '') -replace '[^\w\.-]', '-'
    $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey }
    foreach ($certificate in $existing) {
        Remove-Item -Path $certificate.PSPath -Force
        Write-Host "Removed $($certificate.Thumbprint) from this user's store"
    }

    if (-not $existing) { Write-Host "No certificate with subject '$Subject' in this user's store." }
    Write-Host "The exported files (if any) are still in $OutDirectory - delete them yourself if they are no longer needed."
    return
}

if (-not $Password) {
    $Password = Read-Host -Prompt "Password for the .pfx (empty for none)" -AsSecureString
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
$name = ($Subject -replace '^CN=', '') -replace '[^\w\.-]', '-'

New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$pfxPath = Join-Path $OutDirectory "$name.pfx"
$cerPath = Join-Path $OutDirectory "$name.cer"

$certificate = New-SelfSignedCertificate `
    -Subject $Subject `
    -Type CodeSigningCert `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears($Years) `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable

Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $Password | Out-Null
Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null

# The certificate stays in the store so that builds can sign with it directly (no password needed then); the files are
# for other machines and for keeping safe.
Write-Host "Created $($certificate.Subject)"
Write-Host "  thumbprint:   $($certificate.Thumbprint)"
Write-Host "  valid until:  $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  signing file: $pfxPath"
Write-Host "  trust file:   $cerPath"
Write-Host ''
Write-Host 'Sign a build with the certificate that is now in this user''s store:'
Write-Host '  pwsh -File build/installer/windows/client/Build-ClientInstaller.ps1 -RendezvousUri wss://<server>:5080/ws'
Write-Host 'Or with the .pfx (also works on another machine, or off Windows):'
Write-Host "  ... -SigningTool osslsigncode -CertificateFile `"$pfxPath`" [-CertificatePassword <pw>]"
Write-Host ''
Write-Host 'On the machines that should trust builds signed with it, as an administrator:'
Write-Host "  pwsh -File build/signing/windows/Install-TrustedCertificate.ps1 -CertificatePath `"$cerPath`""
Write-Host ''
if ([string]::IsNullOrEmpty($plain)) {
    Write-Warning 'The .pfx has no password: keep it somewhere only you can read.'
}
