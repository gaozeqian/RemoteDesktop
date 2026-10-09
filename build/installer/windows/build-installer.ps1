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

.EXAMPLE
  # Development build with the built-in default server address
  .\build\installer\windows\build-installer.ps1

.EXAMPLE
  # An installer for your own server, signed
  .\build\installer\windows\build-installer.ps1 -RendezvousUri ws://rd.example.com:5080/ws `
      -SignCertificate (Get-ChildItem Cert:\CurrentUser\My\ABCDEF...)

.NOTES
  A signed installer is what keeps SmartScreen and enterprise policy from blocking the install. Without -SignCertificate
  the MSI is built but unsigned, which is fine for testing on your own machines.
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

    # Code-signing certificate to use (thumbprint, or the subject of one). Left empty, a suitable certificate is looked
    # for in this user's and the machine's store, and signing is skipped when there is none; -SkipSigning turns it off
    # even when one is there. Store certificates are used by signtool and by PowerShell.
    [string]$SignCertificate = '',

    # Which signing tool to use: auto, signtool, osslsigncode or powershell. "auto" picks the one that needs the least
    # configuration (signtool or PowerShell with a store certificate, osslsigncode when certificate files are given).
    [ValidateSet('auto', 'signtool', 'osslsigncode', 'powershell')]
    [string]$SigningTool = 'auto',

    # osslsigncode credentials: a PKCS#12 file (.pfx/.p12, with -CertificatePassword) or a PEM certificate together
    # with -PrivateKeyFile. osslsigncode is the only tool here that runs off Windows, and the only one that signs MSI
    # files without the Windows SDK.
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

    # Ship the symbol files too (they are about a third of the payload): useful for stack traces from other machines,
    # not needed for a normal installation.
    [switch]$KeepSymbols
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is empty inside parameter defaults on Windows PowerShell 5.1 (it works in PowerShell 6 and later), so
# every path that means "next to this script" is resolved here instead. Running the release build with the PowerShell
# that ships with Windows is common enough that it has to work there as well as in PowerShell 7.
$installerDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $installerDirectory 'artifacts\publish' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $installerDirectory 'artifacts' }

$repoRoot = Resolve-Path (Join-Path $installerDirectory '..\..\..')

function Get-ProductVersion {
    $props = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))
    $version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw 'Directory.Build.props has no <Version>.' }

    # MSI versions are four numbers; anything else (e.g. "0.1.0-beta") is refused by the WiX compiler.
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version '$version' is not a valid MSI version (major.minor.build)."
    }

    return "$version.0"
}

# One component per file, nested directories mirroring the publish output. Ids are positional: component GUIDs are
# derived from each file's key path, so they stay stable as long as a file keeps its name and place.
function New-FileList {
    param([string]$Source, [string]$Destination, [string[]]$Exclude)

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine('<!-- Generated by build-installer.ps1 from the publish output: do not edit, do not commit. -->')
    [void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    [void]$builder.AppendLine('  <Fragment>')
    [void]$builder.AppendLine('    <ComponentGroup Id="Payload" Directory="INSTALLFOLDER">')

    $count = @{ Files = 0; Directories = 0 }

    function Add-Component {
        param([string]$Indent, [string]$SourcePath)

        $count.Files++
        $escaped = [System.Security.SecurityElement]::Escape($SourcePath)
        [void]$builder.AppendLine("$Indent<Component Id=""cmp$($count.Files)"" Guid=""*"">")
        [void]$builder.AppendLine("$Indent  <File Id=""file$($count.Files)"" Source=""$escaped"" />")
        [void]$builder.AppendLine("$Indent</Component>")
    }

    function Add-Directory {
        param([string]$Path, [string]$Indent)

        foreach ($directory in Get-ChildItem -LiteralPath $Path -Directory | Sort-Object Name) {
            $count.Directories++
            [void]$builder.AppendLine("$Indent<Directory Id=""dir$($count.Directories)"" Name=""$($directory.Name)"">")
            Add-Directory -Path $directory.FullName -Indent "$Indent  "
            foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -File | Sort-Object Name) {
                if ($Exclude -notcontains $file.Name) {
                    Add-Component -Indent "$Indent  " -SourcePath $file.FullName
                }
            }

            [void]$builder.AppendLine("$Indent</Directory>")
        }
    }

    # Files directly in the install folder first, then the subfolders (runtime natives, localizations).
    foreach ($file in Get-ChildItem -LiteralPath $Source -File | Sort-Object Name) {
        if ($Exclude -notcontains $file.Name) {
            Add-Component -Indent '      ' -SourcePath $file.FullName
        }
    }

    Add-Directory -Path $Source -Indent '    '

    [void]$builder.AppendLine('    </ComponentGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('</Wix>')
    Set-Content -LiteralPath $Destination -Value $builder.ToString() -Encoding utf8
    Write-Host "  $($count.Files) files in $($count.Directories) subfolders"
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

# Signing needs a tool. signtool comes with the Windows SDK, which not every build machine has; Windows itself can
# sign Authenticode just as well through PowerShell, so that is used when signtool is missing. Anything that cannot
# sign reports what to install instead of failing halfway through a release build.
function Resolve-SigningTool {
    # Credentials decide which tools can do the job: signtool and PowerShell work with a certificate in a store,
    # osslsigncode works with certificate files (a PKCS#12 or a PEM certificate with its key).
    $haveFiles = -not [string]::IsNullOrEmpty($CertificateFile)
    $haveStore = $null -ne $signing.Chosen

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
        return [pscustomobject]@{ Kind = 'signtool'; Path = $tools['signtool'] }
    }

    if ($SigningTool -eq 'osslsigncode') {
        if (-not $tools['osslsigncode']) { throw 'osslsigncode was not found. Install it (winget install MichalTrojnara.osslsigncode, apt install osslsigncode, brew install osslsigncode), or leave -SigningTool on auto.' }
        if (-not $haveFiles) { throw 'osslsigncode signs with files: pass -CertificateFile <pfx|pem> [-CertificatePassword <pw>], or -CertificateFile <pem> -PrivateKeyFile <key>.' }
        return [pscustomobject]@{ Kind = 'osslsigncode'; Path = $tools['osslsigncode'] }
    }

    if ($SigningTool -eq 'powershell') {
        if (-not $powershell -or -not $haveStore) { throw 'PowerShell signing needs a code-signing certificate in a store; give one with -SignCertificate, or use certificate files with osslsigncode.' }
        return [pscustomobject]@{ Kind = 'powershell'; Path = $powershell }
    }

    # Automatic: whatever needs the least configuration. signtool and PowerShell use the certificate that is already
    # in a store; osslsigncode is the only option for certificate files (and the only one that runs off Windows).
    if ($tools['signtool'] -and $haveStore) { return [pscustomobject]@{ Kind = 'signtool'; Path = $tools['signtool'] } }
    if ($tools['osslsigncode'] -and $haveFiles) { return [pscustomobject]@{ Kind = 'osslsigncode'; Path = $tools['osslsigncode'] } }
    if ($powershell -and $haveStore) { return [pscustomobject]@{ Kind = 'powershell'; Path = $powershell } }

    return $null
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
  pwsh -File build/signing/Install-TrustedCertificate.ps1 -CertificatePath <the .cer file>

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
        $arguments = @('sign', '/sha1', $Certificate.Thumbprint, '/fd', 'sha256')
        if ($TimestampServer) { $arguments += @('/tr', $TimestampServer, '/td', 'sha256') }
        $arguments += @('/d', 'RemoteDesktop', $File)

        & $Tool.Path @arguments
        if ($LASTEXITCODE -ne 0) { throw "Signing $File failed." }
        return
    }

    # PowerShell's own Authenticode signing: no SDK needed, the certificate has to be in a store it can read.
    $parameters = @{ FilePath = $File; Certificate = $Certificate; HashAlgorithm = 'SHA256' }
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

Write-Host 'Publishing the applications...'
if (Test-Path $PublishDirectory) {
    if (-not $KeepPublishOutput) { Remove-Item -Recurse -Force $PublishDirectory }
}
New-Item -ItemType Directory -Force -Path $PublishDirectory | Out-Null

Push-Location $repoRoot
try {
    # Each executable is published self-contained into the same folder: the service starts the agent from its own
    # directory, so the agent has to be there with the same runtime (a plain build copies the agent, but a
    # framework-dependent agent would look for the .NET runtime on the target machine).
    foreach ($project in 'RemoteDesktop.HostService', 'RemoteDesktop.SessionAgent', 'RemoteDesktop.Client') {
        Write-Host "  $project"
        dotnet publish "src\Hosts\$project" -c $Configuration -r win-x64 --self-contained true -p:PublishReadyToRun=true `
            -o $PublishDirectory -nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }
}
finally {
    Pop-Location
}

foreach ($expected in 'RemoteDesktop.Client.exe', 'RemoteDesktop.HostService.exe', 'RemoteDesktop.SessionAgent.exe', 'appsettings.json', 'msquic.dll') {
    if (-not (Test-Path (Join-Path $PublishDirectory $expected))) {
        throw "$expected is missing from $PublishDirectory; the publish output is not complete."
    }
}

# The address this installation talks to. It is stored in the settings file the installer ships (which an upgrade never
# overwrites) and is also the default of the RENDEZVOUSURI property, so the deferred custom action always writes a
# consistent value. An installation can override it: msiexec /i <msi> RENDEZVOUSURI=ws://other.
$settingsPath = Join-Path $PublishDirectory 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
if ($RendezvousUri) {
    if ($RendezvousUri -notmatch '^wss?://[^/]+') { throw "RendezvousUri must look like ws://host:port/ws (got '$RendezvousUri')." }

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
  -RendezvousUri ws://<server>:5080/ws
"@
}

$version = Get-ProductVersion
# The license page needs RTF, the text lives in a file anybody can edit; one is generated from the other every build.
Write-Host 'Generating the license RTF...'
& (Join-Path $installerDirectory 'New-LicenseRtf.ps1') | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { throw 'The license RTF could not be generated.' }

Write-Host "Generating the file list (version $version)..."
$fileList = Join-Path $installerDirectory 'Files.generated.wxs'

# Declared by hand in Components.wxs (they carry the service, the shortcuts or the settings).
$handWritten = @('RemoteDesktop.Client.exe', 'RemoteDesktop.HostService.exe', 'RemoteDesktop.SessionAgent.exe', 'appsettings.json')

# Symbol files are left out of the installer unless asked for.
if (-not $KeepSymbols) {
    Get-ChildItem $PublishDirectory -Filter *.pdb -Recurse | Remove-Item -Force
}

# The product's own binaries are signed before the package is built, so what ends up installed is signed as well and
# not just the installer: SmartScreen and antivirus look at the executables a user actually runs. Only our files are
# touched - the .NET runtime and Microsoft's libraries next to them already carry Microsoft's signature.
$signing = if ($SkipSigning) { @{ Chosen = $null; Candidates = @() } } else { Find-SigningCertificate -Wanted $SignCertificate -Product 'RemoteDesktop' }
$resolvedSigner = if ($SkipSigning) { $null } else { Resolve-SigningTool }

# A tool needs credentials: a certificate in a store, or certificate files for osslsigncode. Which one it is is
# decided by the tool, so both are carried along and passed on to Invoke-Signing.
$signer = $resolvedSigner
if ($signer) {
    $own = Get-ChildItem $PublishDirectory -Recurse -Include 'RemoteDesktop*.exe', 'RemoteDesktop*.dll'
    Write-Host "Signing $($own.Count) product file(s) before packaging..."
    foreach ($file in $own) {
        Invoke-Signing -Tool $signer -Certificate $signing.Chosen -File $file.FullName
    }
}

New-FileList -Source $PublishDirectory -Destination $fileList -Exclude $handWritten

Write-Host 'Building the MSI...'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

dotnet build (Join-Path $installerDirectory 'RemoteDesktop.Installer.wixproj') -c $Configuration -nologo -v minimal `
    -p:ProductVersion=$version -p:SourceRoot=$PublishDirectory -p:DefaultRendezvousUri=$defaultRendezvousUri `
    -p:OutputPath=$OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }

$package = Get-ChildItem $OutputDirectory -Filter *.msi | Select-Object -First 1
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
            Write-Warning 'Certificate files were given, but no tool that can use them was found. Install osslsigncode, or use a store certificate with signtool or PowerShell.'
        }
        else {
            Write-Host 'No code-signing certificate and no tool to sign with; the package stays unsigned.'
            Write-Host '  In a store:  build\signing\New-SigningCertificate.ps1 for a test certificate, or import the one you bought.'
            Write-Host '  As files:    -SigningTool osslsigncode -CertificateFile <pfx|pem>'
        }
    }
    else {
        Write-Host "Signing with $($resolvedSigner.Kind)..."
        [void](Invoke-Signing -Tool $resolvedSigner -Certificate $signing.Chosen -File $package.FullName)

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

                    Write-Warning (Get-UntrustedCertificateAdvice -Certificate $signing.Chosen -Package $package)
                }
            }
            else {
                $check = Get-AuthenticodeSignature $package.FullName
                if ($check.Status -ne 'Valid') {
                    if ($check.Status -in 'UnknownError', 'NotTrusted') {
                        Write-Warning (Get-UntrustedCertificateAdvice -Certificate $signing.Chosen -Package $package)
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
dotnet test (Join-Path $repoRoot 'tests\RemoteDesktop.Platform.Windows.Tests') --filter 'FullyQualifiedName~InstallerPackageTests' --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The installer package is missing something (see the test output above).' }
Remove-Item Env:\RD_INSTALLER_MSI

Write-Host 'Done. Install with: msiexec /i <msi> (or RENDEZVOUSURI=ws://... to set the server now).'
