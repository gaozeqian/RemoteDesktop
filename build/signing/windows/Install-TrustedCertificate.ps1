<#
.SYNOPSIS
  Makes a machine trust code signed with your own certificate.

.DESCRIPTION
  Installs the public part of a certificate into the two stores that matter for code signing:

    Trusted Root Certification Authorities   the chain ends here, so the signature verifies as valid
    Trusted Publishers                       Windows stops asking "do you want to run software from this publisher?"

  With both, a build signed by your certificate shows up as valid on this machine, and policies such as AppLocker or
  WDAC ("only signed programs may run") can be used with it. Nothing changes on machines where the certificate is not
  installed: they still treat the software as unsigned.

  Think about what this means before running it: whoever holds the matching *private* key can sign code that this
  machine accepts without asking. Treat the private key as the secret it is, and remove the certificate again if it is
  no longer used:

    .\Install-TrustedCertificate.ps1 -CertificatePath <cer> -Remove

.EXAMPLE
  # On every machine of your fleet, as an administrator:
  .\Install-TrustedCertificate.ps1 -CertificatePath .\certificates\RemoteDesktop.cer

  # Just for the user running it, no administrator needed:
  .\Install-TrustedCertificate.ps1 -CertificatePath .\certificates\RemoteDesktop.cer -Scope CurrentUser
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CertificatePath,

    # LocalMachine needs administrator rights and covers every user; CurrentUser needs none.
    [ValidateSet('LocalMachine', 'CurrentUser')]
    [string]$Scope = 'LocalMachine',

    # Take the certificate out of both stores again.
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = (Resolve-Path $CertificatePath).Path
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($path)
$storeNames = @('Root', 'TrustedPublisher')

if ($Scope -eq 'LocalMachine' -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installing for all users needs administrator rights. Run PowerShell as an administrator, or use -Scope CurrentUser.'
}

foreach ($storeName in $storeNames) {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($storeName, $Scope)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        if ($Remove) {
            # Positional arguments only: PowerShell has no named arguments for .NET methods.
            $existing = $store.Certificates.Find(
                [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                $certificate.Thumbprint,
                $false)
            if ($existing.Count -gt 0) {
                $store.Remove($existing[0])
                Write-Host "Removed from $Scope\$storeName"
            }
            else {
                Write-Host "Not in $Scope\$storeName"
            }
        }
        else {
            $store.Add($certificate)
            Write-Host "Added to $Scope\$storeName"
        }
    }
    finally {
        $store.Close()
    }
}

Write-Host ''
Write-Host "$($certificate.Subject) ($($certificate.Thumbprint))"
