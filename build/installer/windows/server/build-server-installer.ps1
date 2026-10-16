<#
.SYNOPSIS
  Builds the Windows installer (MSI) for the RemoteDesktop servers.

.DESCRIPTION
  1. Publishes the two server programs (the rendezvous server and the relay) self-contained for win-x64, each into a
     folder of its own (Rendezvous, Relay) under one publish directory.
  2. Generates the WiX file list from that directory (WiX v5 has no heat, and the list changes with every build
     anyway).
  3. Builds the MSI with the WiX v5 SDK and, when a signing certificate is given, signs it.

  The MSI installs both servers into Program Files\Remote Desktop Server, registers each as a Windows service
  (RemoteDesktopRendezvous and RemoteDesktopRelay), adds the three inbound firewall rules they need, and configures
  the two of them - the address signaling listens on, the relay endpoint clients are told, the key they share, and
  optionally a certificate to serve TLS with.

  This is a package of its own, next to the client installer in ..\client: a machine can run the servers, the client,
  or both, and upgrading one never touches the other.

.EXAMPLE
  # Development build, servers reachable on this machine's name and on all interfaces
  .\build\installer\windows\server\build-server-installer.ps1

.EXAMPLE
  # A server built for a deployment with TLS, signed
  .\build\installer\windows\server\build-server-installer.ps1 -RendezvousUrl https://0.0.0.0:5080 `
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
    # are told to use. Empty leaves whatever the publish output already has in place.
    [string]$RendezvousUrl = 'http://0.0.0.0:5080',

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

# $PSScriptRoot is empty inside parameter defaults on Windows PowerShell 5.1 (it works in PowerShell 6 and later), so
# every path that means "next to this script" is resolved here instead. Running a release build with the PowerShell
# that ships with Windows is common enough that it has to work there as well as in PowerShell 7.
$installerDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $installerDirectory '..\..\..\..')

# Same single artifacts/ root as the client installer, same <kind>/<os>/ shape — see the longer note in
# build/installer/windows/client/build-installer.ps1. The role directory is what keeps the two servers' publish
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
    # Every PropertyGroup, not just the first — see the longer note in build-installer.ps1. Reading .Version off the
    # PropertyGroup array works in Windows PowerShell 5.1 and throws in PowerShell 7, which is what CI runs.
    $version = $props.SelectNodes('/Project/PropertyGroup/Version') |
        ForEach-Object { $_.InnerText } |
        Select-Object -First 1
    if (-not $version) { throw 'Directory.Build.props has no <Version>.' }

    # MSI versions are four numbers; anything else (e.g. "0.1.0-beta") is refused by the WiX compiler.
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version '$version' is not a valid MSI version (major.minor.build)."
    }

    return "$version.0"
}

# One component per file, the subfolders of the publish output mirrored as directories. Ids are positional: component
# GUIDs are derived from each file's key path, so they stay stable as long as a file keeps its name and place.
#
# Two things differ from the client installer's copy of this function, both of them because the servers have what it
# does not (one folder per server, each with the runtime's own localization folders under it):
#
#   * the folders directly under the publish directory are named after those servers and get their id from
#     $TopLevelDirectoryIds rather than from the counter. The hand-written components in Components.wxs install into
#     them (the two executables, the two settings files), and a counter would tie those to the order the publish
#     output happens to be read in. A folder the caller does not name falls back to the counter, and the components
#     that expect the named id then fail to link - loudly, which is the point;
#   * the directory tree is written under a DirectoryRef and the components stay in the ComponentGroup, each one
#     carrying the id of the folder it belongs to. WiX 5 has no Directory element inside a ComponentGroup (the client's
#     publish output is flat, so its generator never met that), and a component that nothing references is not
#     installed at all, so the two cannot simply be written together.
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

            [void]$directories.AppendLine("$Indent<Directory Id=""$id"" Name=""$($directory.Name)"">")
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
    [void]$builder.AppendLine('<!-- Generated by build-server-installer.ps1 from the publish output: do not edit, do not commit. -->')
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


# Finds a certificate to sign with: the one asked for, or the best code-signing certificate in the stores. A
# certificate needs a private key, the code-signing usage and a validity that has not run out; among several, the one
# expiring last wins, which is usually the current release certificate.
function Find-SigningCertificate {
    param([string]$Wanted, [string]$Product)

    $candidates = @()
    foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
        if (-not (Test-Path $store)) { continue }
        foreach ($certificate in Get-ChildItem $store) {
            if (-not $certificate.HasPrivateKey) { continue }
            if ($certificate.NotAfter -lt (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { continue }
            if (-not $certificate.EnhancedKeyUsageList) { continue }
            if ($certificate.EnhancedKeyUsageList.ObjectId -notcontains '1.3.6.1.5.5.7.3.3') { continue }
            $candidates += $certificate
        }
    }

    if ($Wanted) {
        $chosen = $candidates | Where-Object { $_.Thumbprint -eq $Wanted.Replace(' ', '') -or $_.Subject -like "*$Wanted*" } | Select-Object -First 1
        if (-not $chosen) { throw "No usable code-signing certificate matches '$Wanted' (it must have a private key and be valid)." }
        return @{ Chosen = $chosen; Candidates = $candidates }
    }

    if (@($candidates).Count -eq 0) {
        return @{ Chosen = $null; Candidates = @() }
    }

    # Certificates for other products must not be used here: whoever installs this should see who signed it. Only a
    # certificate naming this product is chosen automatically; anything else has to be asked for by name.
    $matching = $candidates | Where-Object { $_.Subject -like "*$Product*" }
    if (@($matching).Count -ge 1) {
        return @{ Chosen = ($matching | Sort-Object NotAfter -Descending | Select-Object -First 1); Candidates = $candidates }
    }

    return @{ Chosen = $null; Candidates = $candidates }
}

# The certificate (with its private key) inside a PKCS#12 file. Through the constructor, and with a null password rather
# than an empty one: an X509Certificate2 is immutable on .NET (PowerShell 6 and later), so loading a file with a separate
# Import call - which is how this reads on Windows PowerShell 5.1 - is refused there with "X509Certificate is immutable
# on this platform".
function Read-PfxCertificate {
    param([string]$Path, [string]$Password)

    $secret = if ($Password) { $Password } else { $env:RD_SIGNING_PASSWORD }
    if (-not $secret) { $secret = $null }

    return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $Path, $secret, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)
}

# Signing needs a tool. signtool comes with the Windows SDK, which not every build machine has; Windows itself can
# sign Authenticode just as well through PowerShell, so that is used when signtool is missing. Anything that cannot
# sign reports what to install instead of failing halfway through a release build.
function Resolve-SigningTool {
    # Credentials decide which tools can do the job: all three work with a PKCS#12 file, signtool and PowerShell also
    # with a certificate in a store, and a PEM certificate with its separate key file is something only osslsigncode
    # takes. Without either credential there is nothing to sign with, whatever the tools are.
    #
    # -CertificateFile is the more specific instruction, so it wins over a certificate found in a store: whoever passes
    # files means those files to sign, not whichever certificate this machine happens to have.
    $haveFiles = -not [string]::IsNullOrEmpty($CertificateFile)
    $havePfx = $haveFiles -and $CertificateFile -match '\.p(12|fx)$'
    $haveStore = -not $haveFiles -and $null -ne $signing.Chosen
    $haveCredentials = $haveStore -or $havePfx

    $tools = [ordered]@{}
    foreach ($name in 'signtool', 'osslsigncode') {
        # By base name: signtool is signtool.exe on Windows, osslsigncode has no extension on Linux and macOS.
        $command = Get-Command $name -ErrorAction SilentlyContinue | Where-Object { $_.CommandType -in 'Application', 'ExternalScript' } | Select-Object -First 1
        if ($command) { $tools[$name] = $command.Source }
    }

    $powershell = $null
    # Windows PowerShell (5.1) and PowerShell 7 on Windows can both sign Authenticode; the same commands on Linux or
    # macOS cannot. $IsWindows only exists in PowerShell 6 and later, so this asks the platform instead of the variable
    # - running the script with Windows PowerShell is common enough that it has to work there too.
    $onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
    $powershell = if ($onWindows) { 'powershell' } else { $null }

    if ($SigningTool -eq 'signtool') {
        if (-not $tools['signtool']) { throw 'signtool.exe was not found. Install the Windows SDK (Signing Tools), or use -SigningTool osslsigncode.' }
        if (-not $haveCredentials) { throw 'signtool signs with a certificate in a store (-SignCertificate <thumbprint>) or with a PKCS#12 file (-CertificateFile <pfx> [-CertificatePassword <pw>]). A PEM certificate with a separate key file needs -SigningTool osslsigncode.' }
        return [pscustomobject]@{ Kind = 'signtool'; Path = $tools['signtool']; Certificate = (Get-SigningCertificate) }
    }

    if ($SigningTool -eq 'osslsigncode') {
        if (-not $tools['osslsigncode']) { throw 'osslsigncode was not found. Install it (winget install MichalTrojnara.osslsigncode, apt install osslsigncode, brew install osslsigncode), or leave -SigningTool on auto.' }
        if (-not $haveFiles) { throw 'osslsigncode signs with files: pass -CertificateFile <pfx|pem> [-CertificatePassword <pw>], or -CertificateFile <pem> -PrivateKeyFile <key>.' }
        # No Certificate: osslsigncode is handed files and reads them itself. The property is set (to nothing) on every
        # tool all the same - reading a property that is not there at all is an error under Set-StrictMode.
        return [pscustomobject]@{ Kind = 'osslsigncode'; Path = $tools['osslsigncode']; Certificate = $null }
    }

    if ($SigningTool -eq 'powershell') {
        if (-not $powershell) { throw 'PowerShell signs Authenticode on Windows only; use -SigningTool osslsigncode here.' }
        if (-not $haveCredentials) { throw 'PowerShell signing needs a code-signing certificate in a store (-SignCertificate) or a PKCS#12 file (-CertificateFile <pfx> [-CertificatePassword <pw>]). A PEM certificate with a separate key file needs -SigningTool osslsigncode.' }
        return [pscustomobject]@{ Kind = 'powershell'; Path = $powershell; Certificate = (Get-SigningCertificate) }
    }

    # Automatic: apart from osslsigncode, the tools only need what the build already has. A certificate in a store goes
    # to signtool when it is installed, as before. A PKCS#12 file goes to PowerShell even then, because PowerShell is
    # part of Windows: the build does not depend on the Windows SDK being installed (or on signtool's folder being on
    # the PATH, which is what a CI runner decides), and the password stays in this process instead of on a command line
    # that every other process on the machine can read. osslsigncode is what is left for a PEM certificate with its own
    # key file, and the only one that runs off Windows.
    if ($havePfx -and $powershell) { return [pscustomobject]@{ Kind = 'powershell'; Path = $powershell; Certificate = (Get-SigningCertificate) } }
    if ($tools['signtool'] -and $haveCredentials) { return [pscustomobject]@{ Kind = 'signtool'; Path = $tools['signtool']; Certificate = (Get-SigningCertificate) } }
    if ($powershell -and $haveCredentials) { return [pscustomobject]@{ Kind = 'powershell'; Path = $powershell; Certificate = (Get-SigningCertificate) } }
    if ($tools['osslsigncode'] -and $haveFiles) { return [pscustomobject]@{ Kind = 'osslsigncode'; Path = $tools['osslsigncode']; Certificate = $null } }

    return $null
}

# The certificate the build signs with: the one inside the PKCS#12 file when files were given, otherwise the one found
# in a store. Read once, when the tool is chosen, and not once per file: signing several hundred files must not
# re-open the file for each of them.
function Get-SigningCertificate {
    if ($CertificateFile) { return Read-PfxCertificate -Path $CertificateFile -Password $CertificatePassword }
    return $signing.Chosen
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

function Invoke-Signing {
    param([object]$Tool, [object]$Certificate, [string]$File)

    if ($Tool.Kind -eq 'osslsigncode') {
        # osslsigncode writes a new file rather than changing one in place, and it takes its credentials as files:
        # either a PKCS#12 (with its password) or a PEM certificate plus its private key.
        $temporary = "$File.signed"
        try {
            $arguments = @('sign', '-h', 'sha256', '-n', 'RemoteDesktop')
            if ($TimestampServer) { $arguments += @('-ts', $TimestampServer) }

            if ($CertificateFile -match '\.p(12|fx)$') {
                $arguments += @('-pkcs12', $CertificateFile)

                # An environment variable keeps the password out of the shell history and out of the process list of
                # the build script itself (the child process still sees it, so a passwordless build certificate or a
                # signing service is better where that matters).
                $password = if ($CertificatePassword) { $CertificatePassword } else { $env:RD_SIGNING_PASSWORD }
                if ($password) { $arguments += @('-pass', $password) }
            }
            elseif ($PrivateKeyFile) {
                $arguments += @('-certs', $CertificateFile, '-key', $PrivateKeyFile)
            }
            else {
                throw "Signing $File failed: give a PKCS#12 file, or a PEM certificate together with -PrivateKeyFile."
            }

            $arguments += @('-in', $File, '-out', $temporary)

            & $Tool.Path @arguments
            if ($LASTEXITCODE -ne 0) { throw "Signing $File failed (osslsigncode exit code $LASTEXITCODE)." }

            Move-Item -LiteralPath $temporary -Destination $File -Force
        }
        finally {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }

        return
    }

    if ($Tool.Kind -eq 'signtool') {
        $arguments = @('sign', '/fd', 'sha256')
        if ($CertificateFile) {
            # A PKCS#12 file. signtool takes the password as an argument, where every other process on this machine can
            # read it: a passwordless build certificate, RD_SIGNING_PASSWORD in the environment, or a certificate in a
            # store (signtool then takes only the thumbprint) keeps it out of sight.
            $arguments += @('/f', $CertificateFile)
            $secret = if ($CertificatePassword) { $CertificatePassword } else { $env:RD_SIGNING_PASSWORD }
            if ($secret) { $arguments += @('/p', $secret) }
        }
        else {
            $arguments += @('/sha1', $Certificate.Thumbprint)
        }

        if ($TimestampServer) { $arguments += @('/tr', $TimestampServer, '/td', 'sha256') }
        $arguments += @('/d', 'RemoteDesktop', $File)

        & $Tool.Path @arguments
        if ($LASTEXITCODE -ne 0) { throw "Signing $File failed." }
        return
    }

    # PowerShell's own Authenticode signing: no SDK needed, and with a PKCS#12 file no certificate store either. The
    # password stays inside this process, unlike with the tools that take it as an argument.
    $credentials = if ($Tool.Certificate) { $Tool.Certificate } else { $Certificate }
    $parameters = @{ FilePath = $File; Certificate = $credentials; HashAlgorithm = 'SHA256' }
    if ($TimestampServer) { $parameters.TimestampServer = $TimestampServer }

    $result = Set-AuthenticodeSignature @parameters

    # The status describes whether the *certificate* is trusted, not whether the file was signed: a self-signed test
    # certificate gives UnknownError ("the chain ends in an untrusted root") while the signature and its timestamp are
    # written correctly. Only a missing signer means the signing itself failed.
    if (-not $result.SignerCertificate) {
        throw "Signing $File failed: $($result.Status) $($result.StatusMessage)"
    }

    return $result
}

Write-Host 'Publishing the servers...'
if (Test-Path $PublishDirectory) {
    if (-not $KeepPublishOutput) { Remove-Item -Recurse -Force $PublishDirectory }
}
New-Item -ItemType Directory -Force -Path $PublishDirectory | Out-Null

# The folder each server is published into is the folder the installer creates under Program Files, and the one the
# configuration step looks in (ServerSetup.RendezvousFolder / RelayFolder), so the two have to be spelled the same.
$servers = @(
    @{ Project = 'RemoteDesktop.Server.Rendezvous'; Folder = 'Rendezvous' }
    @{ Project = 'RemoteDesktop.Server.Relay'; Folder = 'Relay' }
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
        dotnet publish "src\Server\$($server.Project)" -c $Configuration -r win-x64 --self-contained true `
            -o (Join-Path $PublishDirectory $server.Folder) -nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Publishing $($server.Project) failed." }
    }
}
finally {
    Pop-Location
}

foreach ($expected in @('Rendezvous\RemoteDesktop.Server.Rendezvous.exe', 'Rendezvous\appsettings.json',
                        'Relay\RemoteDesktop.Server.Relay.exe', 'Relay\appsettings.json')) {
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
    if ($RendezvousUrl -notmatch '^https?://') { throw "RendezvousUrl must look like http://0.0.0.0:5080 (got '$RendezvousUrl')." }

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

$version = Get-ProductVersion

# The license page needs RTF, the text lives in a file anybody can edit; one is generated from the other every build.
# Same text as the client installer's, so the two packages agree; generated next to this script, which is where this
# package's project looks for it.
Write-Host 'Generating the license RTF...'
& (Join-Path $sharedDirectory 'New-LicenseRtf.ps1') -Text $licenseText -Rtf $licenseRtf
if (-not (Test-Path -LiteralPath $licenseRtf)) { throw "The license RTF was not written to $licenseRtf." }

Write-Host "Generating the file list (version $version)..."
$fileList = Join-Path $installerDirectory 'Files.generated.wxs'

# The two files that make an installation maintainable rather than only installable: a script that writes the settings
# the same way the installer does, and a short page for whoever has to do it. They go into the package root, where the
# servers' folders and server-setup.log already are — an operator looking for "how do I change this" starts there.
foreach ($extra in 'configure-server.ps1', 'HOW-TO-CONFIGURE.txt') {
    Copy-Item -LiteralPath (Join-Path $installerDirectory $extra) -Destination $PublishDirectory -Force
}

# Declared by hand in Components.wxs (they carry the service or the settings). The settings file is excluded by name,
# which is what leaves out both copies of it: the publish output has one in each server folder.
$handWritten = @('RemoteDesktop.Server.Rendezvous.exe', 'RemoteDesktop.Server.Relay.exe', 'appsettings.json')

# Symbol files are left out of the installer unless asked for.
if (-not $KeepSymbols) {
    Get-ChildItem $PublishDirectory -Filter *.pdb -Recurse | Remove-Item -Force
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
    -TopLevelDirectoryIds @{ 'Rendezvous' = 'RendezvousFolder'; 'Relay' = 'RelayFolder' }

# The hand-written components own those files; a second component for one of them would install it twice, and for the
# settings files it would fight the NeverOverwrite an upgrade depends on. The exclusion is by file name, so it is
# checked here rather than assumed: a renamed binary would quietly come back into the generated list, and WiX would
# accept it.
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

dotnet build (Join-Path $installerDirectory 'RemoteDesktop.Server.Installer.wixproj') -c $Configuration -nologo -v minimal @wixArguments -p:OutputPath=$OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }

# The newest, not merely the first: an older package left in the output folder must not be the one that gets signed
# and checked (a rebuild of the same version overwrites its name, a new version does not).
$package = Get-ChildItem $OutputDirectory -Filter *.msi | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $package) { throw "No MSI was produced in $OutputDirectory." }

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
dotnet test --project (Join-Path $repoRoot 'tests\RemoteDesktop.Platform.Windows.UnitTests') --filter-class RemoteDesktop.Platform.Windows.UnitTests.ServerInstallerPackageTests -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The installer package is missing something (see the test output above).' }
Remove-Item Env:\RD_SERVER_INSTALLER_MSI

Write-Host 'Done. Install with: msiexec /i <msi> (or RENDEZVOUSURL=... to set the listening address now).'
