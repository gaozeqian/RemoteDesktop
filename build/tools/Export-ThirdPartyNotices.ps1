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
    $listing = & dotnet list (Join-Path $root $project) package --include-transitive
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed for $project." }

    foreach ($line in $listing) {
        # Top-level packages have three columns (id, requested, resolved); transitive ones have two (id, resolved).
        # Taking the optional third column is what includes a package that only the new gateway project references
        # directly, such as Yarp.ReverseProxy, instead of silently omitting its license.
        if ($line -match '^\s*>\s+(\S+)\s+(\S+)(?:\s+(\S+))?\s*$') {
            $packages[$Matches[1]] = if ($Matches[3]) { $Matches[3] } else { $Matches[2] }
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
    $id = $entry.Key
    $version = $entry.Value
    $nuspec = Join-Path $packagesDirectory "$($id.ToLowerInvariant())/$version/$($id.ToLowerInvariant()).nuspec"

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

    [pscustomobject]@{ Id = $id; Version = $version; License = $license; Url = $url }
}

$header = @'
# 第三方组件声明

本产品使用以下开源组件。它们按各自的许可条款提供，版权归各自的作者所有（许可证全文见各组件的项目主页）。
列表取自随安装包发布的程序的依赖闭包（客户端、被控端、信令服务器、中继服务器、运维监控台与 Ops HTTPS 网关），因此也包含只在构建时用到的包。

本文件由 `build/tools/Export-ThirdPartyNotices.ps1` 从 NuGet 元数据生成，请不要手改——改了下次生成就没了。
依赖变化后（例如 Dependabot 升级了某个包）重新运行一次：

```
pwsh -File build/tools/Export-ThirdPartyNotices.ps1
```

此外，程序以自包含方式发布，因此还包含 .NET 运行时（MIT 许可，https://github.com/dotnet/runtime）。

| 组件 | 版本 | 许可 | 项目主页 |
|---|---|---|---|
'@

$table = $rows | ForEach-Object { "| $($_.Id) | $($_.Version) | $($_.License) | $($_.Url) |" }

$content = @($header) + $table + @('')
Set-Content -Path $OutputPath -Value $content -Encoding utf8
Write-Host "Wrote $OutputPath ($($rows.Count) packages)."
