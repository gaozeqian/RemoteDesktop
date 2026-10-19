<#
.SYNOPSIS
  Builds the Windows installer (MSI) for the controlled end (被控端) only.

.DESCRIPTION
  This package installs the two things that let this computer be connected to: the background service and the session
  agent. It deliberately does not install the client window — a machine that is only ever controlled should not carry a
  way to control others, and an operator who wants both installs both packages.

  The work is done by ..\client\Build-ClientInstaller.ps1 -HostOnly: the service definition, its crash recovery, the
  inbound UDP rule, the Ctrl+Alt+Del policy and the machine cleanup step are the same code for both roles, and two
  copies of that would be two things to keep in step. What differs is the payload (no client), the product identity
  (its own name and UpgradeCode, so one role's upgrade never removes the other) and the product's shortcut (there is
  none).

  Output: artifacts\installer\windows\RemoteDesktop-Host-Windows-x64-<version>.msi

.EXAMPLE
  # Development build with the published default server address
  .\build\installer\windows\host\Build-HostInstaller.ps1

.EXAMPLE
  # An installer for your own server, signed with the certificate already in the store
  .\build\installer\windows\host\Build-HostInstaller.ps1 -RendezvousUri ws://rd.example.com:5080/ws `
      -SignCertificate (Get-ChildItem Cert:\CurrentUser\My\ABCDEF...)

.NOTES
  The installer's wizard has a page for the fixed password. It is not decoration: this package has no window after
  installation, and the temporary password lives only in the service process, so without a fixed password a fresh
  machine has no password anybody can be told. Silent installs use the same value as a property:

      msiexec /i RemoteDesktop-Host-Windows-x64-<version>.msi /qn FIXEDPASSWORD="..."

  A machine installs either this package or the client package, not both: they share the RemoteDesktopHost service and
  the machine's single device identity, so a second one would be a second service competing for the same pipe and device
  code. Someone who wants both roles on one machine installs the client package — it already carries the same service
  and agent, and adds the window. This package exists for a machine that must be controllable but must not be able to
  control others.

  Everything the client package says about signing applies here unchanged: a signed installer is what keeps SmartScreen
  and enterprise policy from blocking the install, and a build signed with a self-signed certificate also carries that
  certificate's public half so the UIAccess agent can start without a prompt.

  The agent asks for UIAccess, which is why this package is signed and installed under Program Files just like the
  client's. Building it without a certificate works, but the agent will not be able to drive elevated windows.
#>
[CmdletBinding()]
param(
    # Publish output; removed first unless -KeepPublishOutput is given. Empty means artifacts\app\windows\host.
    [string]$PublishDirectory = '',

    # Where the MSI ends up. Empty means artifacts\installer\windows.
    [string]$OutputDirectory = '',

    # Server address baked into the installer (ws:// or wss://). Empty leaves the published default in place.
    [string]$RendezvousUri = '',

    [string]$Configuration = 'Release',

    # Code-signing certificate to use (thumbprint, or the subject of one). Left empty, a suitable certificate is looked
    # for in this user's and the machine's store, and signing is skipped when there is none; -SkipSigning turns it off
    # even when one is there.
    [string]$SignCertificate = '',

    # Which signing tool to use: auto, signtool, osslsigncode or powershell.
    [ValidateSet('auto', 'signtool', 'osslsigncode', 'powershell')]
    [string]$SigningTool = 'auto',

    # Signing credentials as files: a PKCS#12 (.pfx/.p12, with -CertificatePassword) that every tool here can use, or a
    # PEM certificate together with -PrivateKeyFile, which only osslsigncode takes.
    [string]$CertificateFile = '',
    [string]$CertificatePassword = '',
    [string]$PrivateKeyFile = '',

    [switch]$SkipSigning,

    # The certificate the signaling server serves TLS with, as a .cer or .pfx (only the public part is used). It is
    # installed into the trusted roots of every machine that runs this installer, which is what makes wss:// work
    # against a server whose address no public authority would issue a certificate for.
    [string]$ServerCertificate = '',

    # Timestamp server for signing; an empty value signs without a timestamp (not recommended).
    [string]$TimestampServer = 'http://timestamp.digicert.com',

    # Keep the publish output between runs.
    [switch]$KeepPublishOutput,

    # Skip "verify" after signing. Needed only when testing the signing path with a self-signed certificate.
    [switch]$SkipSignatureVerification,

    # Ship the symbol files too.
    [switch]$KeepSymbols,

    # Build even though the repository's LICENSE still has 【placeholders】 in it.
    [switch]$AllowDraftLicense
)

$ErrorActionPreference = 'Stop'

$engine = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\client\Build-ClientInstaller.ps1'

# Forward exactly what the caller gave, plus the one answer this script exists to supply. Splatting a hashtable rather
# than an array matters: an array is positional, so '-Configuration' would become the first argument's value (the
# mistake the release workflow made once with this very script).
$parameters = @{} + $PSBoundParameters
$parameters['HostOnly'] = $true

& $engine @parameters
exit $LASTEXITCODE
