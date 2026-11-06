<#
.SYNOPSIS
  Signing helpers shared by Build-ClientInstaller.ps1 and Build-ServerInstaller.ps1. Not a script you can run.

.DESCRIPTION
  This file only defines functions. It is dot-sourced by the two packaging scripts, which would otherwise each carry
  an identical copy of them:

      . (Join-Path $PSScriptRoot '..\Import-SigningHelpers.ps1')

  It has to be dot-sourced (with the leading ". "), not called with & or run with -File: the functions must be
  defined in the packaging script's own scope, where they can see the script's parameters and variables.

  The functions read these from the calling script and do not take them as parameters, exactly as they did when they
  were written inline: $CertificateFile, $CertificatePassword, $PrivateKeyFile, $TimestampServer and $SigningTool (all
  parameters of the packaging scripts), and $signing (the hashtable Find-SigningCertificate returned, with its Chosen
  entry) which Resolve-SigningTool and Get-SigningCertificate use. Nothing in here depends on where this file is, so
  $PSScriptRoot and $MyInvocation are deliberately not used.

  Only functions that were identical in both scripts live here. The ones that differ between the client and the server
  package (Get-ProductVersion, New-FileList, Get-UntrustedCertificateAdvice) stay in their own scripts.

  The file is UTF-8 with a byte order mark, like the scripts that load it: Windows PowerShell 5.1 reads a file
  without one in the machine's code page.
#>

# Generated directories only. Remove-Item is intentionally avoided here: some hardened hosts redirect it to the
# recycle bin, which fails for a directory tree and would make an otherwise clean package build stop.
function Remove-GeneratedDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        [System.IO.Directory]::Delete($Path, $true)
    }
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
