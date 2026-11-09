<#
.SYNOPSIS
  Writes THIRD-PARTY-NOTICES.md: the open source packages that end up inside the shipped product, with their licenses.

.DESCRIPTION
  The list is read from NuGet rather than written by hand: the packages come from "dotnet list package
  --include-transitive" for the server programs the installers publish, and each license comes from that package's own
  .nuspec in the local NuGet cache. Run it again whenever the dependencies change — Dependabot will make that happen.

  Only the packages that are actually redistributed are listed, which is what the licenses of MIT, BSD and Apache
  components ask for when their code is shipped in a binary.
#>
[CmdletBinding()]
param(
    # Where the file is written. Empty means the repository root, next to the README.
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
# Two levels up: this script lives in build/tools/. The repository root is what the project list below and the
# default -OutputPath are both relative to, so it is worked out from the script's own location rather than from the
# caller's working directory.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrEmpty($OutputPath)) { $OutputPath = Join-Path $root 'THIRD-PARTY-NOTICES.md' }
function Read-LicenseText([string]$Path) {
    $text = [System.IO.File]::ReadAllText($Path)
    return [regex]::Replace($text, '[ \t]+(?=\r?\n)', '').Trim()
}

# The programs the installers ship. Their dependencies together are what is redistributed — the servers included:
# the server MSI and .pkg carry Npgsql and the SQLite driver, and a license file that omits them is the drift this
# script exists to prevent. The Ops console and its gateway are separate programs in the same server package, not
# separate products: leaving either out makes every package's notice list silently incomplete for its dependencies.
$shipped = @(
    'src/Hosts/RemoteDesktop.Client/RemoteDesktop.Client.csproj'
    'src/Hosts/RemoteDesktop.HostService/RemoteDesktop.HostService.csproj'
    'src/Hosts/RemoteDesktop.SessionAgent/RemoteDesktop.SessionAgent.csproj'
    'src/Server/RemoteDesktop.Server.Rendezvous/RemoteDesktop.Server.Rendezvous.csproj'
    'src/Server/RemoteDesktop.Server.Relay/RemoteDesktop.Server.Relay.csproj'
    'src/Server/RemoteDesktop.Server.Ops/RemoteDesktop.Server.Ops.csproj'
    'src/Server/RemoteDesktop.Server.Gateway/RemoteDesktop.Server.Gateway.csproj'
)

$packages = @{}
foreach ($project in $shipped) {
    $listing = & dotnet list (Join-Path $root $project) package --include-transitive --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed for $project." }

    foreach ($line in $listing) {
        # Top-level packages have three columns (id, requested, resolved); transitive ones have two (id, resolved).
        # Taking the optional third column is what includes a package that only the new gateway project references
        # directly, such as Yarp.ReverseProxy, instead of silently omitting its license.
        if ($line -match '^\s*>\s+(\S+)\s+(\S+)(?:\s+(\S+))?\s*$') {
            $id = $Matches[1]
            $version = if ($Matches[3]) { $Matches[3] } else { $Matches[2] }
            $packages["$id@$version"] = $version
        }
    }
}

Write-Host "Found $($packages.Count) packages."

# Where NuGet keeps the packages, asked of the SDK rather than assembled from $env:USERPROFILE: that variable does not
# exist outside Windows, and the SDK's answer also follows NUGET_PACKAGES when something sets it to move the cache.
# The key is not translated, so this reads the same in any locale.
function Get-PackagesDirectory {
    $line = & dotnet nuget locals global-packages --list 2>$null |
        Select-String -Pattern '^global-packages:\s*(.+)$' |
        Select-Object -First 1
    if ($LASTEXITCODE -eq 0 -and $line) {
        return $line.Matches[0].Groups[1].Value.Trim()
    }

    # What the SDK itself falls back to when nothing is configured.
    return Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
}

$packagesDirectory = Get-PackagesDirectory
Write-Host "Packages are read from $packagesDirectory."

$rows = foreach ($entry in $packages.GetEnumerator() | Sort-Object Name) {
    $id = $entry.Key.Substring(0, $entry.Key.LastIndexOf('@'))
    $version = $entry.Value
    $packageDirectory = Join-Path $packagesDirectory "$($id.ToLowerInvariant())/$version"
    $nuspec = Join-Path $packageDirectory "$($id.ToLowerInvariant()).nuspec"

    $license = 'see the package'
    $url = "https://www.nuget.org/packages/$id"
    if (Test-Path $nuspec) {
        [xml]$spec = Get-Content -Raw $nuspec

        # <license type="expression">MIT</license> is the modern form and the only one that names the license; the
        # older form is a link, and a package that ships its own license file only gives that file a name.
        $licenseElement = $spec.package.metadata.license
        $expression = $licenseElement.'#text'
        $licenseUrl = $spec.package.metadata.licenseUrl
        if ($licenseElement.type -eq 'expression' -and -not [string]::IsNullOrWhiteSpace($expression)) {
            $license = $expression
        }
        elseif (-not [string]::IsNullOrWhiteSpace($licenseUrl) -and $licenseUrl -notmatch 'aka\.ms/deprecateLicenseUrl') {
            $license = $licenseUrl
        }
        elseif (-not [string]::IsNullOrWhiteSpace($licenseUrl)) {
            # NuGet's own "this field is deprecated" link, which says nothing about the license.
            $license = '见项目主页'
        }
        elseif (-not [string]::IsNullOrWhiteSpace($expression)) {
            $license = "$expression (in the package)"
        }

        $projectUrl = $spec.package.metadata.projectUrl
        if (-not [string]::IsNullOrWhiteSpace($projectUrl)) { $url = $projectUrl }
    }
    else {
        Write-Warning "No .nuspec for $id $version; its license needs looking up by hand."
    }

    $licensePaths = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $nuspec) {
        [xml]$spec = Get-Content -Raw $nuspec
        if ($spec.package.metadata.license.type -eq 'file') {
            $candidate = Join-Path $packageDirectory $spec.package.metadata.license.'#text'
            if (Test-Path $candidate) { $licensePaths.Add($candidate) }
        }
    }
    foreach ($candidate in Get-ChildItem -LiteralPath $packageDirectory -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)(\.|$)' } | Sort-Object Name) {
        if (-not $licensePaths.Contains($candidate.FullName)) { $licensePaths.Add($candidate.FullName) }
    }
    $licenseText = if ($licensePaths.Count) {
        ($licensePaths | ForEach-Object { Read-LicenseText $_ }) -join "`n`n"
    } else { $null }
    if (-not $licenseText -and -not [string]::IsNullOrWhiteSpace($expression)) {
        $template = switch ($expression) {
            'MIT' { 'mit-template.txt' }
            'BSD-3-Clause' { 'bsd-3-clause-template.txt' }
            'PostgreSQL' { 'postgresql-template.txt' }
            'Apache-2.0' { 'openssl.txt' }
            default { $null }
        }
        if ($template) {
            $licenseText = Read-LicenseText (Join-Path $root "native/licenses/$template")
            $authors = [string]$spec.package.metadata.authors
            $copyright = "Copyright (c) $authors"
            $licenseText = $licenseText.Replace('{{COPYRIGHT}}', $copyright).Trim()
            if ($expression -eq 'Apache-2.0') { $licenseText = "$copyright`n`n$licenseText" }
        }
    }
    if (-not $licenseText) {
        throw "No redistributable license text was found inside $id $version ($packageDirectory)."
    }

    [pscustomobject]@{
        Id = $id
        Version = $version
        License = $license
        Url = $url
        LicenseText = $licenseText
    }
}

$lockedNative = @{}
foreach ($line in [System.IO.File]::ReadAllLines((Join-Path $root 'native/macos/native-dependencies.lock'))) {
    if ($line -match '^([A-Z0-9_]+)=(.*)$') { $lockedNative[$Matches[1]] = $Matches[2] }
}
function Add-NativeRow([string]$Id, [string]$Version, [string]$License, [string]$Url, [string]$LicenseFile) {
    $path = Join-Path $root "native/licenses/$LicenseFile"
    $script:rows = @($script:rows) + [pscustomobject]@{
        Id = $Id
        Version = $Version
        License = $License
        Url = $Url
        LicenseText = Read-LicenseText $path
    }
}
$runtimeVersions = @(& dotnet --list-runtimes | ForEach-Object {
    if ($_ -match '^Microsoft\.NETCore\.App\s+(\S+)\s') { [version]$Matches[1] }
})
if (-not $runtimeVersions) { throw 'Could not determine the redistributed .NET Runtime version.' }
$runtimeVersion = ($runtimeVersions | Sort-Object -Descending | Select-Object -First 1).ToString()
$linuxFetch = [System.IO.File]::ReadAllText((Join-Path $root 'native/linux/fetch-libmsquic.sh'))
if (-not $linuxFetch.Contains("`nversion=")) { throw 'Could not read the pinned Linux MsQuic version.' }
$linuxMsQuicVersion = [regex]::Match($linuxFetch, '(?m)^version=([^\r\n]+)$').Groups[1].Value
if (-not $linuxMsQuicVersion) { throw 'Could not read the pinned Linux MsQuic version.' }

Add-NativeRow '.NET Runtime' $runtimeVersion 'MIT' 'https://github.com/dotnet/runtime' 'dotnet-runtime.txt'
Add-NativeRow 'MsQuic (Linux)' $linuxMsQuicVersion 'MIT' 'https://github.com/microsoft/msquic' 'msquic.txt'
Add-NativeRow 'MsQuic (macOS)' $lockedNative.MSQUIC_VERSION 'MIT' 'https://github.com/microsoft/msquic' 'msquic.txt'
Add-NativeRow 'OpenSSL (macOS)' $lockedNative.OPENSSL_VERSION 'Apache-2.0' 'https://github.com/openssl/openssl' 'openssl.txt'
$rows = $rows | Sort-Object Id, Version

$header = @'
# 第三方组件声明

本产品使用以下开源组件，版权归各自作者所有。每个组件的许可证全文都附在本文后半部分。
列表取自随安装包发布的 NuGet 依赖闭包，以及实际随包分发的 .NET Runtime、MsQuic 与 OpenSSL 原生库。

本文件由 `build/tools/Export-ThirdPartyNotices.ps1` 从包内许可证和锁定的原生依赖生成，请不要手改。
依赖变化后（例如 Dependabot 升级了某个包）重新运行一次：

```
pwsh -File build/tools/Export-ThirdPartyNotices.ps1
```

| 组件 | 版本 | 许可 | 项目主页 |
|---|---|---|---|
'@

$table = $rows | ForEach-Object { "| $($_.Id) | $($_.Version) | $($_.License) | $($_.Url) |" }
$licenseSections = @('', '## 许可证全文', '')
foreach ($row in $rows) {
    $licenseSections += @(
        "### $($row.Id) $($row.Version)",
        "",
        "许可：$($row.License)；来源：$($row.Url)",
        '',
        '```text',
        $row.LicenseText,
        '```',
        ''
    )
}

$content = @($header) + $table + $licenseSections
# The encoding is spelled out instead of "Set-Content -Encoding utf8": that value means UTF-8 *with* a BOM in Windows
# PowerShell 5.1 but *without* one in PowerShell 7, so the same script produced a different first byte on a developer
# machine than on the CI runner (pwsh), and the "notices are current" check failed on a file nobody had changed. The BOM
# is kept so the Chinese text still opens correctly in Windows editors that guess the code page.
[System.IO.File]::WriteAllLines($OutputPath, [string[]]$content, [System.Text.UTF8Encoding]::new($true))
Write-Host "Wrote $OutputPath ($($rows.Count) packages)."
