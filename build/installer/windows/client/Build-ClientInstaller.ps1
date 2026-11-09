<#
.SYNOPSIS
  Builds the Windows installer (MSI) for RemoteDesktop.

.DESCRIPTION
  1. Publishes the client and the host service (which carries the session agent with it) self-contained for win-x64 into
     one output folder.
  2. Generates the WiX file list from that folder (WiX v5 has no heat, and the list changes with every build anyway).
  3. Builds the MSI with the WiX v5 SDK and, when a signing certificate is given, signs it.

  The MSI installs the client, the background service and the session agent, adds a start menu shortcut, an inbound UDP
  firewall rule for peer connections, and configures Ctrl+Alt+Del support and the server address.

  -HostOnly builds the controlled end's own package instead: the same service and session agent, without the client
  window and its shortcut. It exists because a machine that is only ever connected to should not have to install the
  controller as well. Both packages share this script so the service, its recovery settings, the firewall rule and the
  machine setup actions cannot drift apart.

.EXAMPLE
  # Development build with the built-in default server address
  .\build\installer\windows\client\Build-ClientInstaller.ps1

.EXAMPLE
  # An installer for your own server, signed
  .\build\installer\windows\client\Build-ClientInstaller.ps1 -RendezvousUri wss://rd.example.com:5080/ws `
      -SignCertificate (Get-ChildItem Cert:\CurrentUser\My\ABCDEF...)

.NOTES
  A signed installer is what keeps SmartScreen and enterprise policy from blocking the install. Without -SignCertificate
  the MSI is built but unsigned, which is fine for testing on your own machines.

  A build that signs with a *self-signed* certificate also puts that certificate's public half into the installer, which
  adds it to the trusted roots of the machine it is installed on. That is what a UIAccess agent needs to start without a
  prompt, and it is why the certificate does not have to be installed there by hand first. A certificate from an
  authority is left alone: it is trusted through its chain, and adding a leaf to the root store would be a downgrade.

  -ServerCertificate does the same thing for the *signaling server's* certificate, which is what a deployment on a
  network no authority issues certificates for needs: every machine that runs this installer then trusts that server's
  TLS, so wss:// works without a certificate error. Make one with build/signing/New-ServerCertificate.ps1.
#>
[CmdletBinding()]
param(
    # Publish output; removed first unless -KeepPublishOutput is given. Empty means the default below.
    [string]$PublishDirectory = '',

    # Where the MSI ends up. Empty means the default below.
    [string]$OutputDirectory = '',

    # Server address baked into the installer (ws:// or wss://). Empty leaves the published default in place.
    [string]$RendezvousUri = '',

    [string]$Configuration = 'Release',

    # Build the controlled end's package: the service and the session agent, no client window, no shortcut. The default
    # output folder and the MSI name change with it (artifacts\app\windows\host, RemoteDesktop-Host-Windows-x64-<version>.msi).
    [switch]$HostOnly,

    # Code-signing certificate to use (thumbprint, or the subject of one). Left empty, a suitable certificate is looked
    # for in this user's and the machine's store, and signing is skipped when there is none; -SkipSigning turns it off
    # even when one is there. Store certificates are used by signtool and by PowerShell. Ignored when -CertificateFile
    # is given: files are the more specific instruction.
    [string]$SignCertificate = '',

    # Which signing tool to use: auto, signtool, osslsigncode or powershell. "auto" picks the one that needs the least
    # configuration (signtool if it is installed, otherwise PowerShell - both on Windows, with a store certificate or a
    # PKCS#12 file; osslsigncode is what is left for a PEM certificate with its own key file).
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
    # against a server whose address no public authority would issue a certificate for. Made by
    # build/signing/New-ServerCertificate.ps1; leave empty when the server uses a certificate from an authority.
    [string]$ServerCertificate = '',

    # Timestamp server for signing; an empty value signs without a timestamp (not recommended: the signature then
    # expires together with the certificate).
    [string]$TimestampServer = 'http://timestamp.digicert.com',

    # Keep the publish output between runs.
    [switch]$KeepPublishOutput,

    # Skip "signtool verify" after signing. Needed only when testing the signing path with a self-signed
    # certificate, which no policy can build a chain to; a release build must never pass this.
    [switch]$SkipSignatureVerification,

    # Ship the symbol files too (they are about a third of the payload): useful for stack traces from other machines,
    # not needed for a normal installation.
    [switch]$KeepSymbols,

    # Build even though the repository's LICENSE still has 【placeholders】 in it. For a test build that nobody will be
    # handed; a build meant to be published should have the real details in the license instead.
    [switch]$AllowDraftLicense
)

$ErrorActionPreference = 'Stop'

# The signing functions (Find-SigningCertificate, Resolve-SigningTool, Get-SigningCertificate, Invoke-Signing,
# Read-PfxCertificate) and Remove-GeneratedDirectory are the same in the client and the server packaging script, so
# they live in one file. It is dot-sourced, not run: the functions have to be defined in this script's own scope to
# read its parameters. $PSScriptRoot is only empty in parameter defaults on Windows PowerShell 5.1; in the script body
# it works there as well, and this runs before the first of the functions is called.
. (Join-Path $PSScriptRoot '..\Import-SigningHelpers.ps1')

Set-StrictMode -Version Latest

# $PSScriptRoot is empty inside parameter defaults on Windows PowerShell 5.1 (it works in PowerShell 6 and later), so
# every path that means "next to this script" is resolved here instead. Running the release build with the PowerShell
# that ships with Windows is common enough that it has to work there as well as in PowerShell 7.
$installerDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $installerDirectory '..\..\..\..')

# Both outputs live under the repository's single artifacts/ root, laid out as <kind>/<os>/ — the same shape the macOS
# scripts use and the one a Linux package will use. One root means "where did the build put it" has one answer; before
# this, Windows wrote under build/installer/…/artifacts while macOS wrote under artifacts/, so every new platform had
# to pick a side.
#
# The publish output is an intermediate and the MSI is the deliverable, hence two kinds. The role is a directory only
# where one OS has more than one of that kind — Windows publishes a client and a server, so their publish outputs need
# separate trees; the MSIs do not, because the role is already in their file names.
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot "artifacts\app\windows\$(if ($HostOnly) { 'host' } else { 'client' })" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\installer\windows' }

# The two roles this script can build. The name is what the MSI is called and what the package tests are selected by,
# so it is decided once here rather than spelled out at each of the four places that need it.
$role = if ($HostOnly) { 'host' } else { 'client' }

# Checked here, before anything is published: a certificate path that does not exist (a step that was supposed to write
# it, a typo) is worth a plain sentence now, not a cryptographic error five minutes into the build.
foreach ($name in 'CertificateFile', 'PrivateKeyFile', 'ServerCertificate') {
    $path = Get-Variable -Name $name -ValueOnly
    if ($path -and -not (Test-Path -LiteralPath $path)) { throw "The certificate -$name '$path' does not exist." }
}

# The license is a draft with the company's own details left as 【placeholders】. Shipping one is publishing a
# document that says "insert your company name here" to whoever installs the product, so the build stops instead.
# Fill in LICENSE at the repository root (shared by every package) or pass -AllowDraftLicense for a build that is not going anywhere.
# Checked here for the same reason as the certificate paths: a release that is going to stop should stop in the first
# second, not after five minutes of publishing.
$licensePath = Join-Path $repoRoot 'LICENSE'
$placeholders = Select-String -Path $licensePath -Pattern '【[^】]+】' -AllMatches |
    ForEach-Object { $_.Matches.Value } | Sort-Object -Unique
if ($placeholders -and -not $AllowDraftLicense) {
    throw @"
The license text still has placeholders in it: $($placeholders -join ', ')
Edit $licensePath and put your own details in their place, or pass -AllowDraftLicense to build anyway
(for a test build that will not be handed to anybody).
"@
}
if ($placeholders) {
    Write-Warning "The license text still has placeholders ($($placeholders -join ', ')); this build should not be published."
}

function Get-ProductVersion {
    $props = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))
    # Every PropertyGroup, not just the first. $props.Project.PropertyGroup is an array as soon as the file has more
    # than one group, and reading .Version off that array is version-dependent: Windows PowerShell 5.1 enumerates the
    # member and hands back the one non-null value, PowerShell 7 throws "The property 'Version' cannot be found on this
    # object" because the other group has no such element. The build runs under pwsh, so the file's shape decides
    # whether this works — which is the wrong thing for it to depend on.
    $version = $props.SelectNodes('/Project/PropertyGroup/Version') |
        ForEach-Object { $_.InnerText } |
        Select-Object -First 1
    if (-not $version) { throw 'Directory.Build.props has no <Version>.' }

    # MSI versions are four numbers; anything else (e.g. "0.1.0-beta") is refused by the WiX compiler.
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version '$version' is not a valid MSI version (major.minor.build)."
    }

    # The numeric ranges MSI allows. Matching the shape is not enough: WiX refuses 0.300.0 or 0.3.70000 later, with a
    # message about the package rather than about the version that caused it.
    $parts = $version.Split('.') | ForEach-Object { [double]$_ }
    if ($parts[0] -gt 255 -or $parts[1] -gt 255 -or $parts[2] -gt 65535) {
        throw "Version '$version' is outside the MSI limits (major/minor 0-255, build 0-65535)."
    }

    return "$version.0"
}

# One component per file, nested directories mirroring the publish output. Ids are positional: component GUIDs are
# derived from each file's key path, so they stay stable as long as a file keeps its name and place.
function New-FileList {
    param([string]$Source, [string]$Destination, [string[]]$Exclude)

    # The directory tree goes under a DirectoryRef and the components stay in the ComponentGroup, each carrying the
    # id of the folder it belongs to. WiX 5 has no Directory element inside a ComponentGroup, which the old shape
    # used; it happened not to be exercised because a win-x64 publish is flat today. A future subfolder (native
    # assets, a runtime layout) would have produced XML the compiler rejects, not a wrong package.
    $directories = [System.Text.StringBuilder]::new()
    $components = [System.Text.StringBuilder]::new()
    $count = @{ Files = 0; Directories = 0 }

    function Add-Component {
        param([string]$SourcePath, [string]$DirectoryId)

        $count.Files++
        $escaped = [System.Security.SecurityElement]::Escape($SourcePath)
        $where = if ($DirectoryId) { " Directory=""$DirectoryId""" } else { '' }
        [void]$components.AppendLine("      <Component Id=""cmp$($count.Files)"" Guid=""*""$where>")
        [void]$components.AppendLine("        <File Id=""file$($count.Files)"" Source=""$escaped"" />")
        [void]$components.AppendLine('      </Component>')
    }

    function Add-Directory {
        param([string]$Path, [string]$Indent)

        foreach ($directory in Get-ChildItem -LiteralPath $Path -Directory | Sort-Object Name) {
            $count.Directories++
            $id = "dir$($count.Directories)"
            $name = [System.Security.SecurityElement]::Escape($directory.Name)
            [void]$directories.AppendLine("$Indent<Directory Id=""$id"" Name=""$name"">")
            Add-Directory -Path $directory.FullName -Indent "$Indent  "
            [void]$directories.AppendLine("$Indent</Directory>")

            foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -File | Sort-Object Name) {
                if ($Exclude -notcontains $file.Name) {
                    Add-Component -SourcePath $file.FullName -DirectoryId $id
                }
            }
        }
    }

    # Files directly in the install folder first, then the subfolders (runtime natives, localizations).
    foreach ($file in Get-ChildItem -LiteralPath $Source -File | Sort-Object Name) {
        if ($Exclude -notcontains $file.Name) {
            Add-Component -SourcePath $file.FullName -DirectoryId ''
        }
    }

    Add-Directory -Path $Source -Indent '      '

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine('<!-- Generated by Build-ClientInstaller.ps1 from the publish output: do not edit, do not commit. -->')
    [void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    [void]$builder.AppendLine('  <Fragment>')
    if ($directories.Length -gt 0) {
        [void]$builder.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
        [void]$builder.Append($directories.ToString())
        [void]$builder.AppendLine('    </DirectoryRef>')
    }

    [void]$builder.AppendLine('    <ComponentGroup Id="Payload" Directory="INSTALLFOLDER">')
    [void]$builder.Append($components.ToString())
    [void]$builder.AppendLine('    </ComponentGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('</Wix>')
    Set-Content -LiteralPath $Destination -Value $builder.ToString() -Encoding utf8
    Write-Host "  $($count.Files) files in $($count.Directories) subfolders"
}

# Writes the public half of the certificate this build signs with next to the publish output, and returns its file name
# - the installer then puts it into the machine's trusted root store, which is what lets a UIAccess agent signed with a
# self-signed certificate start without a prompt.
#
# Only a self-signed certificate is exported, and nothing is exported when the build did not sign at all:
#   * a certificate from a certificate authority is trusted through its chain already, and putting a leaf into the
#     machine's root store would make that one key - not the authority - the root of everything the machine trusts,
#     which is both unnecessary and a way to lose the authority's rotation;
#   * a certificate that signed nothing has no business in a trusted root store. That also keeps an empty placeholder
#     out of the package, which would otherwise try to install "nothing" as a root certificate.
function Export-TrustedRootCertificate {
    param([object]$Certificate, [string]$Destination)

    # signtool and PowerShell are handed the certificate; osslsigncode is handed files and keeps it to itself, so what
    # signed this build is asked of a file it has just signed - one answer that covers every tool.
    $certificate = $Certificate
    if (-not $certificate) {
        $signed = Get-ChildItem $Destination -Recurse -Include 'RemoteDesktop*.exe' | Select-Object -First 1
        if ($signed) { $certificate = (Get-AuthenticodeSignature $signed.FullName).SignerCertificate }
    }

    if (-not $certificate) {
        Write-Warning 'The certificate this build signed with could not be read back from a signed file; the installer carries no root certificate.'
        return $null
    }

    if ($certificate.Subject -ne $certificate.Issuer) {
        Write-Host "The signing certificate is issued by '$($certificate.Issuer)', not by itself; it is trusted through that authority already, so the installer adds no root certificate."
        return $null
    }

    $name = 'RemoteDesktop-TrustedRoot.cer'
    $path = Join-Path $Destination $name
    [System.IO.File]::WriteAllBytes($path, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Host "The signing certificate is self-signed; the installer will add its public half ($name) to the trusted roots of the machine it is installed on."
    return $name
}

# The message for "the signature is fine, this machine just does not trust the certificate yet" - which is what a
# self-signed certificate looks like everywhere until its .cer is installed.
function Get-UntrustedCertificateAdvice {
    param([object]$Certificate, [object]$Package)

    $who = if ($Certificate) { $Certificate.Subject } else { 'the certificate' }
    $selfSigned = $Certificate -and $Certificate.Subject -eq $Certificate.Issuer

    $advice = @"
$($Package.Name) is signed with $who, but this machine does not trust that certificate$(
    if ($selfSigned) { ' (it is self-signed)' }), so the chain ends in an untrusted root. The signature itself is
in place - nothing about the build is wrong.

On every machine that should accept this software, once, as an administrator:
  pwsh -File build/signing/windows/Install-TrustedCertificate.ps1 -CertificatePath <the .cer file>
$(if ($selfSigned) { 'A machine that installs this package does not need that step: it puts this certificate into its trusted roots itself. Copying the binaries without the installer is what needs it.' })

For machines you do not control, only a certificate from a certificate authority helps: they have no reason to trust
yours, and Windows keeps warning about an unknown publisher. See build/signing/README.md.

To skip this check on the build machine, pass -SkipSignatureVerification.
"@

    return $advice
}

Write-Host 'Publishing the applications...'
if (Test-Path $PublishDirectory) {
    if (-not $KeepPublishOutput) { Remove-GeneratedDirectory $PublishDirectory }
}
New-Item -ItemType Directory -Force -Path $PublishDirectory | Out-Null

# The agent is built more than once per run: on its own, and as a project reference of the service, whose build copies
# the agent's output next to itself. Both have to be the packaging build (see -p:UiAccessAgent below), and the copies
# of the last few builds have to go: the SDK generates the executable that carries the manifest with an incremental
# check that does not look at the manifest at all (Microsoft.NET.Sdk.targets, _CreateAppHost: inputs are the assembly
# and the apphost source), so an apphost generated by an earlier build - a development build, without UIAccess - is
# considered up to date and is what ends up in the package. It builds in seconds.
#
# The two folders are the SDK's artifacts layout, which Directory.Build.props points at the repository root: the shape
# is artifacts/<kind>/<project> and not <project>/<kind>, which is the whole point of that setting.
foreach ($kind in 'obj', 'bin') {
    Remove-GeneratedDirectory (Join-Path $repoRoot "artifacts\$kind\RemoteDesktop.SessionAgent")
}

Push-Location $repoRoot
try {
    # Each executable is published self-contained into the same folder: the service starts the agent from its own
    # directory, so the agent has to be there with the same runtime (a plain build copies the agent, but a
    # framework-dependent agent would look for the .NET runtime on the target machine). The client is left out of
    # -HostOnly: a controlled machine has no window, and shipping the executable without a shortcut would only be a
    # second way to start it.
    $projects = @('RemoteDesktop.HostService', 'RemoteDesktop.SessionAgent')
    if (-not $HostOnly) { $projects += 'RemoteDesktop.Client' }

    foreach ($project in $projects) {
        Write-Host "  $project"

        # -p:UiAccessAgent=true goes to every publish, not only the agent's own: the agent project is also built as part
        # of the others, and a build without it is an agent that cannot drive elevated windows - whichever copy lands
        # last in the shared output folder would decide. This is the build that ends up signed in Program Files, which
        # is what UIAccess requires; a development build keeps the manifest without it (see the project file).
        dotnet publish "src\Hosts\$project" -c $Configuration -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:RestoreLockedMode=true `
            -p:UiAccessAgent=true -o $PublishDirectory -nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }
}
finally {
    Pop-Location
}

$expectedFiles = @('RemoteDesktop.HostService.exe', 'RemoteDesktop.SessionAgent.exe', 'appsettings.json', 'msquic.dll')
if (-not $HostOnly) { $expectedFiles = @('RemoteDesktop.Client.exe') + $expectedFiles }

foreach ($expected in $expectedFiles) {
    if (-not (Test-Path (Join-Path $PublishDirectory $expected))) {
        throw "$expected is missing from $PublishDirectory; the publish output is not complete."
    }
}

# The agent asks for UIAccess, and only this build does: Windows refuses to start such an image unless it is signed and
# installed in a secure location, so a development build carries the manifest without it. Published without
# -p:UiAccessAgent the feature would silently be missing from every installation, and nothing in the package would look
# wrong - hence the check on the file itself (the manifest travels inside the executable as plain XML).
$agentManifest = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes((Join-Path $PublishDirectory 'RemoteDesktop.SessionAgent.exe')))
if ($agentManifest -notmatch 'uiAccess="true"') {
    throw 'The published agent does not ask for UIAccess; it would not be able to drive elevated windows (see the SessionAgent project file).'
}

# The address this installation talks to. It is stored in the settings file the installer ships (which an upgrade never
# overwrites) and is also the default of the RENDEZVOUSURI property, so the deferred custom action always writes a
# consistent value. An installation can override it: msiexec /i <msi> RENDEZVOUSURI=wss://server:5080/ws.
$settingsPath = Join-Path $PublishDirectory 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
if ($RendezvousUri) {
    # The same shape the client and host enforce at startup. A package that carries an address they reject would
    # install cleanly and then fail on the first connection, which is the failure mode this check exists to avoid.
    $parsed = $null
    $valid = [Uri]::TryCreate($RendezvousUri, [UriKind]::Absolute, [ref]$parsed)
    if (-not $valid -or $parsed.Scheme -notin @('ws', 'wss') -or $parsed.Host.Length -eq 0) {
        throw "RendezvousUri must look like ws://host:port/ws or wss://host:port/ws (got '$RendezvousUri')."
    }
    if ($parsed.Scheme -eq 'ws' -and -not $parsed.IsLoopback) {
        throw "RendezvousUri uses plain ws:// for non-loopback host '$($parsed.Host)'. Use wss://; a non-loopback plain-text server is rejected at runtime."
    }

    $settings.Transport.RendezvousUri = $RendezvousUri
    $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8
}

$defaultRendezvousUri = [string]$settings.Transport.RendezvousUri
if (-not $defaultRendezvousUri) { throw 'The published appsettings.json has no Transport:RendezvousUri to bake in.' }
Write-Host "Server address: $defaultRendezvousUri"

# The published default points at the machine itself, which is right for developing on one computer and wrong for
# every other installation: a device that cannot reach a signaling server gets no device code and cannot be connected
# to at all. Say it loudly rather than letting somebody find out after installing on two machines.
if ($defaultRendezvousUri -match '^wss?://(127\.0\.0\.1|localhost|\[::1\])') {
    Write-Warning @"
This installer points at $defaultRendezvousUri - the computer it runs on. Devices installed from it can only be
connected to if the signaling server runs there. For anything else, build again with your server's address:
  -RendezvousUri wss://<server>:5080/ws
"@
}

# The appsettings.json in the publish folder is the set of shipped defaults. The live address is written by the
# installer to %ProgramData%\RemoteDesktop\appsettings.json (MachineSettings), which the MSI does not own; a major
# upgrade may replace this default file without touching the operator's address.

$version = Get-ProductVersion

# The license page needs RTF, the text lives in a file anybody can edit; one is generated from the other every build.
Write-Host 'Generating the license RTF...'
# The generator reads the repository's LICENSE and writes License.rtf next to itself, which is where the packages look
# for it (WixUILicenseRtf points at ..\..\License.rtf in both .wxs files).
& (Join-Path $installerDirectory '..\..\New-LicenseRtf.ps1') | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { throw 'The license RTF could not be generated.' }

# The licence and the third-party notices go into the installed folder with the binaries they cover. The wizard's
# licence page shows the same LICENSE, but a page is not a copy anybody can read after the installer has closed.
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $PublishDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $PublishDirectory -Force

Write-Host "Generating the file list (version $version)..."
$fileList = Join-Path $installerDirectory 'Files.generated.wxs'

# Declared by hand in Components.wxs (they carry the service, the shortcuts or the settings).
$handWritten = @('RemoteDesktop.HostService.exe', 'RemoteDesktop.SessionAgent.exe', 'appsettings.json')
if (-not $HostOnly) { $handWritten += 'RemoteDesktop.Client.exe' }

# Symbol files are left out of the installer unless asked for.
if (-not $KeepSymbols) {
    # Remove-Item through the pipeline with -Recurse throws InputObjectNotBound on Windows PowerShell 5.1; passing the
    # path explicitly works on both 5.1 and 7. The scripts support both because a release build often runs in the
    # PowerShell that ships with Windows.
    Get-ChildItem $PublishDirectory -Filter *.pdb -Recurse | ForEach-Object { [System.IO.File]::Delete($_.FullName) }
}

# The product's own binaries are signed before the package is built, so what ends up installed is signed as well and
# not just the installer: SmartScreen and antivirus look at the executables a user actually runs. Only our files are
# touched - the .NET runtime and Microsoft's libraries next to them already carry Microsoft's signature.
$signing = if ($SkipSigning) { @{ Chosen = $null; Candidates = @() } } else { Find-SigningCertificate -Wanted $SignCertificate -Product 'RemoteDesktop' }
$resolvedSigner = if ($SkipSigning) { $null } else { Resolve-SigningTool }

# Signing is what the certificate is for; which tool does it and where its credentials come from is decided in
# Resolve-SigningTool, so the certificate it settled on is what everything else here uses.
$signer = $resolvedSigner
$signingCertificate = if ($signer) { $signer.Certificate } else { $null }
$trustedRootCertificate = $null
if ($signer) {
    $own = Get-ChildItem $PublishDirectory -Recurse -Include 'RemoteDesktop*.exe', 'RemoteDesktop*.dll'
    Write-Host "Signing $($own.Count) product file(s) before packaging..."
    foreach ($file in $own) {
        # Discarded: the per-file result of Set-AuthenticodeSignature is a row of the same table for every file, and a
        # build that signs 287 of them does not need it printed 287 times.
        [void](Invoke-Signing -Tool $signer -Certificate $signingCertificate -File $file.FullName)
    }

    # Nothing comes of this when the build did not sign, or signed with a certificate from an authority: see the
    # function for why. It is the file's presence that decides whether the installer carries the component at all.
    $trustedRootCertificate = Export-TrustedRootCertificate -Certificate $signingCertificate -Destination $PublishDirectory
    if ($trustedRootCertificate) { Write-Host "  $trustedRootCertificate" }
}

# The signaling server's certificate, when one was handed in: the installer puts its public part into the trusted root
# store of every machine that runs it, which is what makes wss:// work against a server this deployment made a
# certificate for itself. A .pfx is accepted and reduced to its public part - only the certificate travels, never a key.
$trustedServerCertificate = ''
if ($ServerCertificate) {
    $trustedServerCertificate = 'RemoteDesktop-Signaling.cer'
    $destination = Join-Path $PublishDirectory $trustedServerCertificate

    if ([System.IO.Path]::GetExtension($ServerCertificate) -in '.pfx', '.p12') {
        $fromPfx = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            (Resolve-Path -LiteralPath $ServerCertificate).Path, $CertificatePassword)
        Export-Certificate -Cert $fromPfx -FilePath $destination -Type CERT | Out-Null
        $fromPfx.Dispose()
    }
    else {
        Copy-Item -LiteralPath $ServerCertificate -Destination $destination -Force
    }

    $installed = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $destination).Path)
    Write-Host "  server certificate to trust: $($installed.Subject) ($($installed.Thumbprint))"
    $installed.Dispose()
}

New-FileList -Source $PublishDirectory -Destination $fileList -Exclude @($handWritten + 'RemoteDesktop-TrustedRoot.cer', 'RemoteDesktop-Signaling.cer')

# The custom actions are installer plumbing, not part of the product solution: the first-upgrade migration and the
# fixed-password file live in a net48 assembly msiexec can host. Built here so the WXS has a DLL to embed.
Write-Host 'Building the installer custom actions...'
dotnet build (Join-Path $installerDirectory 'CustomActions\RemoteDesktop.Client.Installer.CustomActions.csproj') -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The installer custom actions failed to build.' }

# Platform=x64 in the project is what makes SfxCA pack the 64-bit native host; the package is x64, so an x86 stub
# would fail to load with no line of managed logging. The path spells that out.
$customActions = Resolve-Path (Join-Path $installerDirectory "CustomActions\bin\x64\$Configuration\net48\RemoteDesktop.Client.Installer.CustomActions.CA.dll")
if (-not (Test-Path -LiteralPath $customActions)) { throw "The custom actions were not packaged into $customActions." }

Write-Host 'Building the MSI...'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# -p:TrustedRootCertificate is what compiles the component that installs the certificate; without it neither the
# component nor the binary it needs exists in the package (Components.wxs).
$wixArguments = @(
    "-p:ProductVersion=$version"
    "-p:SourceRoot=$PublishDirectory"
    "-p:DefaultRendezvousUri=$defaultRendezvousUri"
    "-p:CustomActionsPath=$customActions"
)
if ($trustedRootCertificate) { $wixArguments += "-p:TrustedRootCertificate=$trustedRootCertificate" }
if ($trustedServerCertificate) { $wixArguments += "-p:TrustedServerCertificate=$trustedServerCertificate" }
# What makes the same .wxs files produce the host's package: no client component, no shortcut, its own product name and
# UpgradeCode (a host package and a client package are two products and have to upgrade separately).
if ($HostOnly) { $wixArguments += '-p:HostOnly=1' }

# The .msi is the deliverable; the .wixpdb that comes with it is not. WiX writes the pdb next to the package by
# default, and "next to" is $(TargetDir) -- which OutputPath above has just pointed at artifacts/installer/windows,
# the directory CONTRIBUTING.md defines as deliverables. PdbOutputDir sends it back into the project's own obj, where
# it belongs (a relative value is normalized against the project directory, so this needs no absolute path). Kept
# rather than suppressed with SuppressPdbOutput: it is what makes a crash report from a customer's install readable.
# A separate intermediate directory per role. The client and host packages share this project; without this, the
# second build's incremental clean deletes the first role's MSI from the shared output folder, so building client
# then host (as CI and release do) left only the host package behind.
dotnet build (Join-Path $installerDirectory 'RemoteDesktop.Installer.wixproj') -c $Configuration -nologo -v minimal @wixArguments `
    -p:OutputPath=$OutputDirectory -p:BaseIntermediateOutputPath=obj\$role\ -p:IntermediateOutputPath=obj\$role\$Configuration\ -p:PdbOutputDir=obj\$role\$Configuration\
if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }

# The newest package of this role, not merely the first: an older one left in the output folder must not be the one
# that gets signed and checked. The pattern is role-specific on purpose - "*.msi" would pick the server package built
# before this one, and the tests would then pass while describing a package this run never produced.
$packagePattern = if ($HostOnly) { 'RemoteDesktop-Host-Windows-*.msi' } else { 'RemoteDesktop-Client-Windows-*.msi' }
$package = Get-ChildItem $OutputDirectory -Filter $packagePattern | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $package) { throw "No $role MSI was produced in $OutputDirectory." }

Write-Host "  $($package.FullName) ($([math]::Round($package.Length / 1MB, 1)) MB)"

if (-not $SkipSigning) {
    if (-not $resolvedSigner) {
        # Saying which half is missing is the difference between "it silently produced an unsigned release" and
        # "install a tool" or "give me a certificate".
        if (@($signing.Candidates).Count -gt 0) {
            Write-Warning "This machine has $(@($signing.Candidates).Count) usable code-signing certificate(s), none naming RemoteDesktop, so none was used. Pick one yourself with -SignCertificate:"
            $signing.Candidates | Sort-Object NotAfter -Descending | ForEach-Object { Write-Host "  $($_.Thumbprint)  $($_.Subject)" }
        }
        elseif ($CertificateFile) {
            Write-Warning "Certificate files were given, but no tool that can use them was found. A PKCS#12 file works with signtool or PowerShell (both on Windows); a PEM certificate with a separate key file needs osslsigncode."
        }
        else {
            Write-Host 'No code-signing certificate and no tool to sign with; the package stays unsigned.'
            Write-Host '  In a store:  build\signing\windows\New-SigningCertificate.ps1 for a test certificate, or import the one you bought.'
            Write-Host '  As files:    -CertificateFile <pfx> -CertificatePassword <pw>'
        }
    }
    else {
        Write-Host "Signing with $($resolvedSigner.Kind)..."
        [void](Invoke-Signing -Tool $resolvedSigner -Certificate $signingCertificate -File $package.FullName)

        if (-not $SkipSignatureVerification) {
            # Verification answers two different questions, and only one of them is a build failure:
            #
            #   is the signature intact?      that is what a release needs, and a broken one fails the build
            #   is the certificate trusted?   that is a decision of the machine the software lands on. A self-signed
            #                                 certificate - which is a legitimate choice for an internal fleet - is
            #                                 trusted nowhere until its .cer is installed, so "chain ends in an
            #                                 untrusted root" is a warning with instructions, not an error.
            if ($resolvedSigner.Kind -eq 'osslsigncode') {
                # Arguments go through splatting: passing them as a comma-separated list hands the program a single
                # argument and it reports "Unknown option: 'verify',".
                $verify = @('verify', '-in', $package.FullName)
                $output = (& $resolvedSigner.Path @verify 2>&1 | Out-String)

                # osslsigncode says whether the signature matches the file; a non-zero exit code usually means it could
                # not build a chain to a trusted root, or had no CA bundle for the timestamp authority - worth a
                # warning, not a failed build, because the signature itself is what the release needs.
                if ($output -notmatch 'Number of verified signatures:\s*[1-9]') {
                    throw "The signature of $($package.Name) could not be verified:`n$output"
                }

                if ($LASTEXITCODE -ne 0) {
                    $problems = @($output -split "`n" | Where-Object { $_ -match 'failed|error' } | Select-Object -First 3) -join '; '
                    Write-Warning "osslsigncode reported problems while verifying (missing CA bundles for the certificate or the timestamp authority are common): $problems"
                }
            }
            elseif ($resolvedSigner.Kind -eq 'signtool') {
                $verify = @('verify', '/pa', $package.FullName)
                $output = (& $resolvedSigner.Path @verify 2>&1 | Out-String)
                if ($LASTEXITCODE -ne 0) {
                    # 0x800B0109 is "a certificate chain processed, but terminated in a root certificate which is not
                    # trusted" - the self-signed case, and the only one that is not the build's problem.
                    if ($output -notmatch '0x800B0109|not trusted|不受信任') {
                        throw "The signature of $($package.Name) could not be verified:`n$output"
                    }

                    Write-Warning (Get-UntrustedCertificateAdvice -Certificate $signingCertificate -Package $package)
                }
            }
            else {
                $check = Get-AuthenticodeSignature $package.FullName
                if ($check.Status -ne 'Valid') {
                    if ($check.Status -in 'UnknownError', 'NotTrusted') {
                        Write-Warning (Get-UntrustedCertificateAdvice -Certificate $signingCertificate -Package $package)
                    }
                    else {
                        throw "The signature of $($package.Name) could not be verified: $($check.Status) $($check.StatusMessage)"
                    }
                }
            }
        }
    }
}


# The package is checked without installing it: a test opens the database and reports what is missing from it. WiX 5
# drops some content without a word (see verify notes in that test), so building is not proof.
Write-Host 'Checking the package...'
$env:RD_INSTALLER_MSI = $package.FullName

# What this build meant to put in, so that a component WiX dropped without a word is caught here rather than on a
# machine that installs it: the certificates it was given, and the difference between that and what the package has.
$expectedCertificates = 0
if ($trustedRootCertificate) { $expectedCertificates++ }
if ($trustedServerCertificate) { $expectedCertificates++ }
$env:RD_EXPECTED_CERTIFICATES = "$expectedCertificates"

# 一个类一个类地跑（--filter-class 认的是完全限定类名；写成 `*InstallerPackageTests` 会把服务端那个类也选上）。
# 没有 --nologo：新的 dotnet test（Microsoft.Testing.Platform 模式）不认它，会把它转发给测试程序，测试程序
# 以退出码 5 拒绝整次运行。
#
# --results-directory 不能省：不给它，dotnet test 会在当前目录建一个空的 TestResults/，于是仓库根上又长出第二个
# 产物根（见 CONTRIBUTING.md 的"构建产物布局"）。指到 artifacts/ 下面，位置就只有一个答案。
$packageTestClass = if ($HostOnly) { 'HostInstallerPackageTests' } else { 'InstallerPackageTests' }
dotnet test --project (Join-Path $repoRoot 'tests\RemoteDesktop.Platform.Windows.UnitTests') --filter-class "RemoteDesktop.Platform.Windows.UnitTests.$packageTestClass" --results-directory (Join-Path $repoRoot 'artifacts\test-results\windows') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The installer package is missing something (see the test output above).' }
Remove-Item Env:\RD_INSTALLER_MSI
Remove-Item Env:\RD_EXPECTED_CERTIFICATES

Write-Host "Done ($role). Install with: msiexec /i <msi> (or RENDEZVOUSURI=wss://... to set a non-loopback server now)."
