<#
.SYNOPSIS
  Builds the Windows installer (MSI) for the RemoteDesktop servers.

.DESCRIPTION
  1. Publishes the server programs (rendezvous, relay, monitoring console and its HTTPS gateway) self-contained for
     win-x64, each into a folder of its own (Rendezvous, Relay, Ops, Gateway) under one publish directory.
  2. Generates the WiX file list from that directory (WiX v5 has no heat, and the list changes with every build
     anyway).
  3. Builds the MSI with the WiX v5 SDK and, when a signing certificate is given, signs it.

  The MSI installs all four into Program Files\RemoteDesktopServer, registers each as a Windows service
  (RemoteDesktopRendezvous, RemoteDesktopRelay, RemoteDesktopOps, RemoteDesktopGateway), adds the inbound firewall
  rules the public listeners need, and configures them - the addresses signaling and the console gateway listen on,
  the relay endpoint clients are told, the shared key, and optionally a certificate for signaling TLS. The console
  itself stays on loopback; the gateway is the only network-facing listener for it.

  This is a package of its own, next to the client installer in ..\client: a machine can run the servers, the client,
  or both, and upgrading one never touches the other.

.EXAMPLE
  # Development build; signaling listens on loopback until an address is chosen at install time
  .\build\installer\windows\server\Build-ServerInstaller.ps1

.EXAMPLE
  # A server built for a deployment with TLS, signed
  .\build\installer\windows\server\Build-ServerInstaller.ps1 -RendezvousUrl https://0.0.0.0:5080 `
      -SignCertificate (Get-ChildItem Cert:\CurrentUser\My\ABCDEF...)

.NOTES
  A signed installer is what keeps SmartScreen and enterprise policy from blocking the install. Without -SignCertificate
  the MSI is built but unsigned, which is fine for testing on your own machines.

  Unlike the client package, this one carries no certificate: nothing on a server needs UIAccess, so there is no
  trusted root to install. Sign the package and move the certificate to the machines that need to trust it with
  build/signing/windows/Install-TrustedCertificate.ps1.

  The certificate the signaling server serves TLS with is not a build input: it belongs to the deployment, so it is
  given to the installer (TLSCERTIFICATEPATH) rather than baked into the package.
#>
[CmdletBinding()]
param(
    # Publish output; removed first unless -KeepPublishOutput is given. Empty means the default below.
    [string]$PublishDirectory = '',

    # Where the MSI ends up. Empty means the default below.
    [string]$OutputDirectory = '',

    # The address the rendezvous server binds to (http:// or https://), baked into the settings template the installer
    # ships and used as the default of the RENDEZVOUSURL property. This is what the server listens on, not what clients
    # are told to use. Empty leaves whatever the publish output already has in place. The default is loopback; a public
    # deployment passes https:// explicitly (or opts into insecure plaintext at install time).
    [string]$RendezvousUrl = 'http://127.0.0.1:5080',

    [string]$Configuration = 'Release',

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

    # Timestamp server for signing; an empty value signs without a timestamp (not recommended: the signature then
    # expires together with the certificate).
    [string]$TimestampServer = 'http://timestamp.digicert.com',

    # Keep the publish output between runs.
    [switch]$KeepPublishOutput,

    # Skip "signtool verify" after signing. Needed only when testing the signing path with a self-signed
    # certificate, which no policy can build a chain to; a release build must never pass this.
    [switch]$SkipSignatureVerification,

    # Ship the symbol files too (they are a good part of the payload): useful for stack traces from a deployment,
    # not needed for a normal installation.
    [switch]$KeepSymbols,

    # Build even though LICENSE at the repository root (the same text the client package shows) still has
    # 【placeholders】 in it. For a test build that nobody will be handed; a build meant to be published should have the
    # real details in the license instead.
    [switch]$AllowDraftLicense
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The signing functions (Find-SigningCertificate, Resolve-SigningTool, Get-SigningCertificate, Invoke-Signing,
# Read-PfxCertificate) and Remove-GeneratedDirectory are the same in the client and the server packaging script, so
# they live in one file. It is dot-sourced, not run: the functions have to be defined in this script's own scope to
# read its parameters. $PSScriptRoot is only empty in parameter defaults on Windows PowerShell 5.1; in the script body
# it works there as well, and this runs before the first of the functions is called.
. (Join-Path $PSScriptRoot '..\Import-SigningHelpers.ps1')

# $PSScriptRoot is empty inside parameter defaults on Windows PowerShell 5.1 (it works in PowerShell 6 and later), so
# every path that means "next to this script" is resolved here instead. Running a release build with the PowerShell
# that ships with Windows is common enough that it has to work there as well as in PowerShell 7.
$installerDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $installerDirectory '..\..\..\..')

# Same single artifacts/ root as the client installer, same <kind>/<os>/ shape — see the longer note in
# build/installer/windows/client/Build-ClientInstaller.ps1. The role directory is what keeps the two servers' publish
# outputs apart; the MSIs share one directory because their names already say which is which.
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot 'artifacts\app\windows\server' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\installer\windows' }

# The license text is the product's, shared by every package (the agreement covers running the server components too),
# and it is the repository's own LICENSE rather than a copy kept beside the packages: one text, and the RTF the MSI
# license page needs is generated from it. No package can show an agreement the repository no longer states.
$sharedDirectory = Join-Path $installerDirectory '..\..'
$licenseText = Join-Path $repoRoot 'LICENSE'
$licenseRtf = Join-Path $sharedDirectory 'License.rtf'

# Checked here, before anything is published: a certificate path that does not exist (a step that was supposed to write
# it, a typo) is worth a plain sentence now, not a cryptographic error five minutes into the build.
foreach ($name in 'CertificateFile', 'PrivateKeyFile') {
    $path = Get-Variable -Name $name -ValueOnly
    if ($path -and -not (Test-Path -LiteralPath $path)) { throw "The certificate -$name '$path' does not exist." }
}

# The license is a draft with the company's own details left as 【placeholders】. Shipping one is publishing a
# document that says "insert your company name here" to whoever installs the product, so the build stops instead.
# Fill in LICENSE at the repository root (or pass -AllowDraftLicense for a build that is not going anywhere).
# Checked here for the same reason as the certificate paths above: a release that is going to stop should stop in the
# first second, not after the servers have been published.
$placeholders = Select-String -Path $licenseText -Pattern '【[^】]+】' -AllMatches |
    ForEach-Object { $_.Matches.Value } | Sort-Object -Unique
if ($placeholders -and -not $AllowDraftLicense) {
    throw @"
The license text still has placeholders in it: $($placeholders -join ', ')
Edit $licenseText and put your own details in their place, or pass -AllowDraftLicense to build anyway
(for a test build that will not be handed to anybody).
"@
}
if ($placeholders) {
    Write-Warning "The license text still has placeholders ($($placeholders -join ', ')); this build should not be published."
}

function Get-ProductVersion {
    $props = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))
    # Every PropertyGroup, not just the first — see the longer note in Build-ClientInstaller.ps1. Reading .Version off the
    # PropertyGroup array works in Windows PowerShell 5.1 and throws in PowerShell 7, which is what CI runs.
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

# One component per file, the subfolders of the publish output mirrored as directories. Ids are positional: component
# GUIDs are derived from each file's key path, so they stay stable as long as a file keeps its name and place.
#
# Two things are worth knowing next to the client installer's copy of this function:
#
#   * the servers have what it does not (one folder per server, each with the runtime's own localization folders under
#     it), so the folders directly under the publish directory are named after those servers and get their id from
#     $TopLevelDirectoryIds rather than from the counter. The hand-written components in Components.wxs install into
#     them (the two executables, the two settings files), and a counter would tie those to the order the publish
#     output happens to be read in. A folder the caller does not name falls back to the counter, and the components
#     that expect the named id then fail to link - loudly, which is the point;
#   * the directory tree is written under a DirectoryRef and the components stay in the ComponentGroup, each one
#     carrying the id of the folder it belongs to, as in the client's copy. WiX 5 has no Directory element inside a
#     ComponentGroup, and a component that nothing references is not installed at all, so the two cannot simply be
#     written together.
function New-FileList {
    param([string]$Source, [string]$Destination, [string[]]$Exclude, [hashtable]$TopLevelDirectoryIds = @{})

    $directories = [System.Text.StringBuilder]::new()
    $components = [System.Text.StringBuilder]::new()
    $count = @{ Files = 0; Directories = 0 }

    function Add-Component {
        param([string]$Indent, [string]$SourcePath, [string]$DirectoryId)

        $count.Files++
        $escaped = [System.Security.SecurityElement]::Escape($SourcePath)
        # No Directory attribute when the file lies in the install folder itself: the ComponentGroup says where its
        # components go, and saying it twice is how the two drift apart.
        $where = if ($DirectoryId) { " Directory=""$DirectoryId""" } else { '' }
        [void]$components.AppendLine("$Indent<Component Id=""cmp$($count.Files)"" Guid=""*""$where>")
        [void]$components.AppendLine("$Indent  <File Id=""file$($count.Files)"" Source=""$escaped"" />")
        [void]$components.AppendLine("$Indent</Component>")
    }

    function Add-Directory {
        param([string]$Path, [string]$Indent, [int]$Depth)

        foreach ($directory in Get-ChildItem -LiteralPath $Path -Directory | Sort-Object Name) {
            if ($Depth -eq 1 -and $TopLevelDirectoryIds.ContainsKey($directory.Name)) {
                $id = $TopLevelDirectoryIds[$directory.Name]
            }
            else {
                $count.Directories++
                $id = "dir$($count.Directories)"
            }

            # The lookup above uses the real name; only the attribute written into the WiX source is escaped, as in the
            # client script: a folder name with an ampersand would otherwise produce a source that does not parse.
            $name = [System.Security.SecurityElement]::Escape($directory.Name)
            [void]$directories.AppendLine("$Indent<Directory Id=""$id"" Name=""$name"">")
            Add-Directory -Path $directory.FullName -Indent "$Indent  " -Depth ($Depth + 1)
            [void]$directories.AppendLine("$Indent</Directory>")

            foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -File | Sort-Object Name) {
                if ($Exclude -notcontains $file.Name) {
                    Add-Component -Indent '      ' -SourcePath $file.FullName -DirectoryId $id
                }
            }
        }
    }

    # Files directly in the publish folder first (there are none today: it holds the two server folders), then the
    # folders themselves (the runtime, the localizations).
    foreach ($file in Get-ChildItem -LiteralPath $Source -File | Sort-Object Name) {
        if ($Exclude -notcontains $file.Name) {
            Add-Component -Indent '      ' -SourcePath $file.FullName -DirectoryId ''
        }
    }

    Add-Directory -Path $Source -Indent '      ' -Depth 1

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine('<!-- Generated by Build-ServerInstaller.ps1 from the publish output: do not edit, do not commit. -->')
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
    # The two folders the servers live in are named by hand, so they are not in this count — it is the folders that
    # were left for the generator to name. Saying "0 subfolders" for a payload that plainly has two of them sends
    # whoever reads the log looking for a problem that is not there.
    $extra = if ($count.Directories -gt 0) { " plus $($count.Directories) folders named for it" } else { '' }
    Write-Host "  $($count.Files) files$extra"
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

The client package puts a self-signed certificate into the machine's trusted roots by itself; this server package
carries no certificate, so a machine that installs only this one needs the step above - or the binaries copied to it
without the installer, which needs it in every case.

For machines you do not control, only a certificate from a certificate authority helps: they have no reason to trust
yours, and Windows keeps warning about an unknown publisher. See build/signing/README.md.

To skip this check on the build machine, pass -SkipSignatureVerification.
"@

    return $advice
}

Write-Host 'Publishing the servers...'
if (Test-Path $PublishDirectory) {
    if (-not $KeepPublishOutput) { Remove-GeneratedDirectory $PublishDirectory }
}
New-Item -ItemType Directory -Force -Path $PublishDirectory | Out-Null

# The folder each server is published into is the folder the installer creates under Program Files, and the one the
# configuration step looks in (ServerSetup.RendezvousFolder / RelayFolder), so the two have to be spelled the same.
# Ops is the third program: the monitoring console, which reads the other two and is useless without them, so it is
# installed by the same package and by no package of its own.
$servers = @(
    @{ Project = 'RemoteDesktop.Server.Rendezvous'; Folder = 'Rendezvous' }
    @{ Project = 'RemoteDesktop.Server.Relay'; Folder = 'Relay' }
    @{ Project = 'RemoteDesktop.Server.Ops'; Folder = 'Ops' }
    @{ Project = 'RemoteDesktop.Server.Gateway'; Folder = 'Gateway' }
)

Push-Location $repoRoot
try {
    foreach ($server in $servers) {
        Write-Host "  $($server.Project)"

        # Self-contained, so a machine that installs this needs no .NET runtime of its own, and one folder per server
        # because each is a whole application: they share nothing at run time, and an operator looking at Program Files
        # sees which two programs are there.
        #
        # No -p:PublishReadyToRun, unlike the client: the servers do not need the start-up time it buys (they start
        # once, as services), and precompiling the whole runtime is what makes a package of this size - the
        # self-contained output alone is already most of it.
        dotnet publish "src\Server\$($server.Project)" -c $Configuration -r win-x64 --self-contained true -p:RestoreLockedMode=true `
            -o (Join-Path $PublishDirectory $server.Folder) -nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Publishing $($server.Project) failed." }
    }
}
finally {
    Pop-Location
}

foreach ($expected in @('Rendezvous\RemoteDesktop.Server.Rendezvous.exe', 'Rendezvous\appsettings.json',
                        'Relay\RemoteDesktop.Server.Relay.exe', 'Relay\appsettings.json',
                        'Ops\RemoteDesktop.Server.Ops.exe', 'Ops\appsettings.json',
                        'Gateway\RemoteDesktop.Server.Gateway.exe', 'Gateway\appsettings.json')) {
    if (-not (Test-Path (Join-Path $PublishDirectory $expected))) {
        throw "$expected is missing from $PublishDirectory; the publish output is not complete."
    }
}

# The address the signaling server binds to. It is stored in the settings template the installer ships (which a
# configuration step reads and an upgrade never overwrites) and is also the default of the RENDEZVOUSURL property, so
# the deferred custom action always writes a consistent value. An installation can override it:
# msiexec /i <msi> RENDEZVOUSURL=https://0.0.0.0:5080
$settingsPath = Join-Path $PublishDirectory 'Rendezvous\appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json

if ($RendezvousUrl) {
    $parsed = $null
    $valid = [Uri]::TryCreate($RendezvousUrl, [UriKind]::Absolute, [ref]$parsed)
    if (-not $valid -or $parsed.Scheme -notin @('http', 'https') -or $parsed.Host.Length -eq 0) {
        throw "RendezvousUrl must look like http://0.0.0.0:5080 (got '$RendezvousUrl')."
    }

    # A published settings file that has no Urls at all is a server that listens where its own default says; adding the
    # property here is what keeps this step from depending on that file's shape.
    if ($settings.PSObject.Properties.Name -contains 'Urls') { $settings.Urls = $RendezvousUrl }
    else { $settings | Add-Member -NotePropertyName Urls -NotePropertyValue $RendezvousUrl }
    $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8
}

$defaultRendezvousUrl = if ($settings.PSObject.Properties.Name -contains 'Urls') { [string]$settings.Urls } else { '' }
if (-not $defaultRendezvousUrl) { throw 'The published Rendezvous\appsettings.json has no Urls to bake in.' }
Write-Host "Signaling listens on: $defaultRendezvousUrl"

# https:// without a certificate is a server that does not start: Kestrel refuses to configure an HTTPS endpoint with
# nothing to serve it with. The certificate belongs to the deployment and is given to the installer, not to the build,
# so all this can do is say it - before somebody wonders why the service they just installed is not running.
if ($defaultRendezvousUrl -match '^https://') {
    Write-Warning @"
This installer serves TLS ($defaultRendezvousUrl) but carries no certificate of its own. Install it with one, or
signaling will not start:
  msiexec /i <msi> TLSCERTIFICATEPATH=C:\certs\rd.pfx [TLSCERTIFICATEPASSWORD=<password>]
"@
}
elseif ($defaultRendezvousUrl -match '^http://(127\.0\.0\.1|localhost|\[::1\])(:|/)') {
    Write-Warning @"
This installer binds signaling to $defaultRendezvousUrl, so only this machine can reach it. For a public or LAN
deployment install with TLS and an externally reachable address:
  msiexec /i <msi> RENDEZVOUSURL=https://0.0.0.0:5080 TLSCERTIFICATEPATH=C:\certs\rd.pfx
"@
}

$version = Get-ProductVersion

# The console's own database. A Windows service starts in System32, so a relative path in its settings would be a file
# in System32 — the one place a database must not live. %ProgramData% is expanded by the program at start-up (see
# OpsServer.AddOps), which keeps the location right on a machine whose ProgramData is not on C:.
$opsSettingsPath = Join-Path $PublishDirectory 'Ops\appsettings.json'
$opsSettings = Get-Content $opsSettingsPath -Raw | ConvertFrom-Json
$opsSettings.Ops.Database.Path = '%ProgramData%\RemoteDesktopServer\ops.db'
$opsSettings | ConvertTo-Json -Depth 10 | Set-Content $opsSettingsPath -Encoding utf8

# The license page needs RTF, the text lives in a file anybody can edit; one is generated from the other every build.
# Same text as the client installer's, so the two packages agree; generated next to this script, which is where this
# package's project looks for it.
Write-Host 'Generating the license RTF...'
& (Join-Path $sharedDirectory 'New-LicenseRtf.ps1') -Text $licenseText -Rtf $licenseRtf
if (-not (Test-Path -LiteralPath $licenseRtf)) { throw "The license RTF was not written to $licenseRtf." }

# The licence and the third-party notices go into the installed folder with the binaries they cover. The wizard's
# licence page shows the same LICENSE, but a page is not a copy anybody can read after the installer has closed.
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $PublishDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $PublishDirectory -Force

Write-Host "Generating the file list (version $version)..."
$fileList = Join-Path $installerDirectory 'Files.generated.wxs'

# The files that make an installation maintainable rather than only installable: a script that writes the settings the
# same way the installer does, a .cmd wrapper that survives the Windows 11 client execution policy, and a short page
# for whoever has to do it. They go into the package root, where the servers' folders and server-setup.log already
# are — an operator looking for "how do I change this" starts there.
foreach ($extra in 'Set-ServerConfiguration.ps1', 'Set-ServerConfiguration.cmd', 'how-to-configure.txt') {
    Copy-Item -LiteralPath (Join-Path $installerDirectory $extra) -Destination $PublishDirectory -Force
}

# Declared by hand in Components.wxs (they carry the service or the settings). The settings file is excluded by name,
# which is what leaves out all three copies of it: the publish output has one in each program folder.
$handWritten = @('RemoteDesktop.Server.Rendezvous.exe', 'RemoteDesktop.Server.Relay.exe', 'RemoteDesktop.Server.Ops.exe', 'RemoteDesktop.Server.Gateway.exe', 'appsettings.json')

# Symbol files are left out of the installer unless asked for.
if (-not $KeepSymbols) {
    # Remove-Item through the pipeline with -Recurse throws InputObjectNotBound on Windows PowerShell 5.1; passing the
    # path explicitly works on both 5.1 and 7. The scripts support both because a release build often runs in the
    # PowerShell that ships with Windows.
    Get-ChildItem $PublishDirectory -Filter *.pdb -Recurse | ForEach-Object { [System.IO.File]::Delete($_.FullName) }
}

# The product's own binaries are signed before the package is built, so what ends up installed is signed as well and
# not just the installer: SmartScreen and antivirus look at the executables a service runs. Only our files are
# touched - the .NET runtime and Microsoft's libraries next to them already carry Microsoft's signature.
$signing = if ($SkipSigning) { @{ Chosen = $null; Candidates = @() } } else { Find-SigningCertificate -Wanted $SignCertificate -Product 'RemoteDesktop' }
$resolvedSigner = if ($SkipSigning) { $null } else { Resolve-SigningTool }

# Signing is what the certificate is for; which tool does it and where its credentials come from is decided in
# Resolve-SigningTool, so the certificate it settled on is what everything else here uses.
$signer = $resolvedSigner
$signingCertificate = if ($signer) { $signer.Certificate } else { $null }
if ($signer) {
    $own = Get-ChildItem $PublishDirectory -Recurse -Include 'RemoteDesktop*.exe', 'RemoteDesktop*.dll'
    Write-Host "Signing $($own.Count) product file(s) before packaging..."
    foreach ($file in $own) {
        # Discarded: the per-file result of Set-AuthenticodeSignature is a row of the same table for every file, and a
        # build that signs hundreds of them does not need it printed hundreds of times.
        [void](Invoke-Signing -Tool $signer -Certificate $signingCertificate -File $file.FullName)
    }
}

New-FileList -Source $PublishDirectory -Destination $fileList -Exclude $handWritten `
    -TopLevelDirectoryIds @{ 'Rendezvous' = 'RendezvousFolder'; 'Relay' = 'RelayFolder'; 'Ops' = 'OpsFolder'; 'Gateway' = 'GatewayFolder' }

# The hand-written components own those files; a second component for one of them would install it twice. The
# settings files here are the shipped defaults; the live files are in %ProgramData% and are not in the MSI's file
# table. The exclusion is by file name, so it is checked here rather than assumed: a renamed binary would quietly
# come back into the generated list, and WiX would accept it.
foreach ($name in $handWritten) {
    if (Select-String -LiteralPath $fileList -SimpleMatch $name -Quiet) {
        throw "$name is in the generated file list as well as in the hand-written components in Components.wxs."
    }
}

Write-Host 'Building the MSI...'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# The custom actions the settings page needs: asking for a certificate file, and finding out whether a database
# answers. A separate net48 project (msiexec hosts the .NET Framework runtime, so a custom action assembly has to be
# one it can load), built here because it is installer plumbing rather than part of the product's solution.
Write-Host 'Building the installer custom actions...'
dotnet build (Join-Path $installerDirectory 'CustomActions\RemoteDesktop.Server.Installer.CustomActions.csproj') -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The installer custom actions failed to build.' }

# The packaging step turns the assembly into a single DLL that carries its dependencies and the DTF runtime with it;
# that one is what goes into the package, not the plain assembly next to it.
# "x64" in the path is part of the path on purpose: the project sets Platform=x64 so that SfxCA packs the 64-bit native
# host stub, and that is what satisfies the x64 package below. Packed with the x86 stub instead, msiexec cannot load the
# action at all and it fails without a word in the log, so the folder spelling out the bitness is worth keeping.
# bin\ and not artifacts\bin\ is also on purpose: the installer projects carry their own (empty) Directory.Build.props,
# so they never import the root one and never pick up its ArtifactsPath — see build/installer/windows/client/
# Directory.Build.props. The path here is relative to the project, as it has always been.
$customActions = Resolve-Path (Join-Path $installerDirectory "CustomActions\bin\x64\$Configuration\net48\RemoteDesktop.Server.Installer.CustomActions.CA.dll")
if (-not (Test-Path -LiteralPath $customActions)) { throw "The custom actions were not packaged into $customActions." }

$wixArguments = @(
    "-p:ProductVersion=$version"
    "-p:SourceRoot=$PublishDirectory"
    "-p:DefaultRendezvousUrl=$defaultRendezvousUrl"
    "-p:CustomActionsPath=$customActions"
)

# Same as the client script: the .msi is the deliverable and the .wixpdb is not, but WiX writes the pdb into
# $(TargetDir), which OutputPath above has pointed at artifacts/installer/windows. PdbOutputDir puts it back in the
# project's own obj; a relative value is normalized against the project directory.
dotnet build (Join-Path $installerDirectory 'RemoteDesktop.Server.Installer.wixproj') -c $Configuration -nologo -v minimal @wixArguments -p:OutputPath=$OutputDirectory -p:PdbOutputDir=obj\$Configuration\
if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }

# The newest, not merely the first: an older package left in the output folder must not be the one that gets signed
# and checked (a rebuild of the same version overwrites its name, a new version does not). The pattern is role-specific
# on purpose — "*.msi" would pick the client or host package built before this one, and the tests would then pass while
# describing a package this run never produced.
$package = Get-ChildItem $OutputDirectory -Filter 'RemoteDesktop-Server-Windows-*.msi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $package) { throw "No server MSI was produced in $OutputDirectory." }

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
# drops some content without a word (see the verify notes in that test), so building is not proof.
Write-Host 'Checking the package...'
$env:RD_SERVER_INSTALLER_MSI = $package.FullName

# 完全限定类名，和客户端脚本同样的原因（见那里的注释）：--filter-class 不认通配形式的"精确匹配"，
# 而 --nologo 在新的 dotnet test 下会让测试程序以退出码 5 拒绝整次运行。
# 同理 --results-directory：不给它，dotnet test 会在当前目录建一个空的 TestResults/。
dotnet test --project (Join-Path $repoRoot 'tests\RemoteDesktop.Platform.Windows.UnitTests') --filter-class RemoteDesktop.Platform.Windows.UnitTests.ServerInstallerPackageTests --results-directory (Join-Path $repoRoot 'artifacts\test-results\windows') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The installer package is missing something (see the test output above).' }
Remove-Item Env:\RD_SERVER_INSTALLER_MSI

Write-Host 'Done. Install with: msiexec /i <msi> (or RENDEZVOUSURL=... to set the listening address now).'
