<#
.SYNOPSIS
  检查一份生产部署的配置文件是否还处在"开发默认"的不安全状态。

.DESCRIPTION
  这份脚本对部署者、发布流水线或上线前自查都有用：它不修任何东西，只指出哪些设置会让一台放在公网上的
  服务器处于危险状态。每一项检查对应 docs/design/README.md 里"部署者必须自己做的事"。

  退出码：
    0  一切正常（没有发现不安全默认值）
    2  发现不安全默认值（下面的"发现"清单）
    3  文件不存在或读不了（可能是指错了目录）

.EXAMPLE
  pwsh -File build/tools/Invoke-SecurityAudit.ps1 -InstallDirectory "C:\Program Files\Remote Desktop Server"

.EXAMPLE
  pwsh -File build/tools/Invoke-SecurityAudit.ps1 -SettingsPath artifacts/app/windows/server/appsettings.json

.NOTES
  这份脚本只读文件、不改任何东西。它不能代替真正的渗透测试，但能在"看着正常、其实在裸奔"的部署上线前
  拦住它。

  三个平台都能跑：它只读文件、不调用任何平台专有 API，所以在 Linux 或 macOS 上用 pwsh 跑同一份脚本，就能
  审那边的服务端部署（配置换成 /etc/remotedesktop-server/ 下那两份）。图标与第三方许可清单那两份脚本没有
  这个性质，见 build/README.md。
#>
[CmdletBinding()]
param(
    # 服务端安装目录（Rendezvous\appsettings.json 与 Relay\appsettings.json 都在它下面）。
    [string]$InstallDirectory = '',

    # 单个 appsettings.json 文件（手工部署时用；给了它就只查这一份）。
    [string]$SettingsPath = '',

    # 客户端 / 被控端的 appsettings.json（检查它是不是还在连本机、信令是不是明文）。
    [string]$ClientSettingsPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 仓库里开发配置带的那把中继共享密钥。它被提交进了 appsettings.Development.json，是"开发用"的；生产环境
# 必须换成自己生成的（openssl rand -base64 48）。见到这把钥匙等于见到"忘了换"。
$DevelopmentRelayKey = 'FWNwaTxD3q0LBCdapWf8qLbfhyGxLFQOMRAtMt/PKl0='

$findings = New-Object System.Collections.Generic.List[string]

function Add-Finding([string]$Text) {
    $findings.Add($Text)
    Write-Host "  [不安全] $Text" -ForegroundColor Yellow
}

function Add-Ok([string]$Text) {
    Write-Host "  [正常]   $Text" -ForegroundColor Green
}

function Read-Settings([string]$Path) {
    if (-not $Path) { return $null }
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "文件不存在：$Path" -ForegroundColor Red
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        Write-Host "读不了 $Path ：$($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

function Test-WeakRelayKey([string]$Key, [string]$Where) {
    if ([string]::IsNullOrWhiteSpace($Key)) {
        Add-Finding "$Where 的共享密钥是空的：中继票据没有任何保护。用 openssl rand -base64 48 生成一个，两端一致。"
        return
    }

    if ($Key -eq $DevelopmentRelayKey) {
        Add-Finding "$Where 还在用仓库里的开发默认共享密钥。任何人从仓库拿到这把钥匙就能白用你的中继带宽。"
        return
    }

    try {
        $bytes = [Convert]::FromBase64String($Key)
        if ($bytes.Length -lt 32) {
            Add-Finding "$Where 的共享密钥太短（只有 $($bytes.Length) 字节，至少 32）。"
        }
        else {
            Add-Ok "$Where 的共享密钥不是默认值，长度足够。"
        }
    }
    catch {
        Add-Finding "$Where 的共享密钥不是合法的 base64。"
    }
}

function Test-SignalingUrl([string]$Url, [string]$Where) {
    if ([string]::IsNullOrWhiteSpace($Url)) {
        Add-Finding "$Where 没有写明信令监听地址（Urls 为空）。"
        return
    }

    if ($Url.StartsWith('https://')) {
        Add-Ok "$Where 信令走 TLS（$Url）。"
    }
    elseif ($Url.StartsWith('http://')) {
        Add-Finding "$Where 信令是明文 http（$Url）：设备代码、IP、中继票据在途可被读取和抢用。见 docs/ops/README.md 的 TLS 一节。"
    }
    else {
        Add-Finding "$Where 的信令地址 $Url 既不是 http:// 也不是 https://。"
    }
}

function Test-RendezvousUri([string]$Uri, [string]$Where) {
    if ([string]::IsNullOrWhiteSpace($Uri)) {
        Add-Finding "$Where 没有配置服务器地址（Transport:RendezvousUri 为空），设备拿不到设备代码。"
        return
    }

    if ($Uri.StartsWith('wss://')) {
        Add-Ok "$Where 服务器地址走 wss://（$Uri）。"
        return
    }

    if ($Uri.StartsWith('ws://127.0.0.1') -or $Uri.StartsWith('ws://localhost') -or $Uri.StartsWith('ws://[::1]')) {
        Add-Finding "$Where 还在连本机（$Uri）：装到别的机器上谁都连不上。"
    }
    elseif ($Uri.StartsWith('ws://')) {
        Add-Finding "$Where 信令是明文 ws://（$Uri）：见 docs/ops/README.md 的 TLS 一节。"
    }
    else {
        Add-Finding "$Where 的服务器地址 $Uri 格式不对（应为 ws:// 或 wss://）。"
    }
}

function Test-Database([string]$Provider, [string]$ConnectionString, [string]$Path, [string]$Where) {
    # 没有 provider、或者 provider 是 none：设备代码只在内存里。这是最容易被当成"配好了"的一处。
    if ([string]::IsNullOrWhiteSpace($Provider) -or $Provider -eq 'none') {
        Add-Finding "$Where 没有数据库：设备代码只存在内存里，服务器一重启所有设备代码都会变，无人值守的机器会失联。"
        return
    }

    if ($Provider -eq 'sqlite') {
        if ([string]::IsNullOrWhiteSpace($Path)) {
            Add-Finding "$Where 配的是 SQLite 但没写文件路径（Rendezvous:Database:Path），服务器起不来。"
        }
        elseif (-not (Test-Path -LiteralPath $Path)) {
            Add-Finding "$Where 配的 SQLite 文件 $Path 还不存在：第一次启动会创建它，但如果服务账号写不了那个目录，服务器就起不来。"
        }
        else {
            Add-Ok "$Where 用 SQLite 保存设备代码（$Path）。这个文件要备份：丢了等于所有设备码重发。"
        }

        return
    }

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        Add-Finding "$Where 配的是 $Provider 但没有连接串（Rendezvous:Database:ConnectionString），服务器起不来。"
        return
    }

    Add-Ok "$Where 用 $Provider 保存设备代码。"
}

# ---- 收集要查的配置 ----
$settingsToCheck = @()

if ($InstallDirectory) {
    if (-not (Test-Path -LiteralPath $InstallDirectory)) {
        Write-Host "安装目录不存在：$InstallDirectory" -ForegroundColor Red
        exit 3
    }

    $rendezvous = Join-Path $InstallDirectory 'Rendezvous\appsettings.json'
    $relay = Join-Path $InstallDirectory 'Relay\appsettings.json'

    if (-not (Test-Path -LiteralPath $rendezvous) -and -not (Test-Path -LiteralPath $relay)) {
        Write-Host "在 $InstallDirectory 下没找到 Rendezvous\appsettings.json 或 Relay\appsettings.json。" -ForegroundColor Red
        exit 3
    }

    if (Test-Path -LiteralPath $rendezvous) { $settingsToCheck += [pscustomobject]@{ Role = 'Rendezvous'; Path = $rendezvous } }
    if (Test-Path -LiteralPath $relay) { $settingsToCheck += [pscustomobject]@{ Role = 'Relay'; Path = $relay } }
}
elseif ($SettingsPath) {
    if (-not (Test-Path -LiteralPath $SettingsPath)) {
        Write-Host "配置文件不存在：$SettingsPath" -ForegroundColor Red
        exit 3
    }
    $settingsToCheck += [pscustomobject]@{ Role = '配置'; Path = $SettingsPath }
}
else {
    Write-Host '没有给任何要查的东西。用 -InstallDirectory <服务端安装目录> 或 -SettingsPath <appsettings.json>。'
    exit 3
}

# ---- 逐份检查 ----
foreach ($entry in $settingsToCheck) {
    Write-Host "检查 $($entry.Role)（$($entry.Path)）："
    $settings = Read-Settings -Path $entry.Path
    if ($null -eq $settings) { exit 3 }

    # 中继共享密钥：Rendezvous 里叫 RelaySharedKey，Relay 里叫 SharedKey。
    $relayKey = $null
    if ($settings.PSObject.Properties['Rendezvous'] -and $settings.Rendezvous.PSObject.Properties['RelaySharedKey'] -and $settings.Rendezvous.RelaySharedKey) { $relayKey = [string]$settings.Rendezvous.RelaySharedKey }
    if ($settings.PSObject.Properties['Relay'] -and $settings.Relay.PSObject.Properties['SharedKey'] -and $settings.Relay.SharedKey) { $relayKey = [string]$settings.Relay.SharedKey }
    Test-WeakRelayKey -Key $relayKey -Where $entry.Role

    # 信令监听地址。
    if ($settings.PSObject.Properties['Urls']) { Test-SignalingUrl -Url ([string]$settings.Urls) -Where $entry.Role }

    # 数据库（只有 Rendezvous 才有意义）。新的写法是 Rendezvous:Database:{Provider,ConnectionString,Path}；
    # 老机器上只有 PostgresConnectionString，它仍然意味着"用 PostgreSQL"，所以在这里折算成同一个 provider。
    if ($settings.PSObject.Properties['Rendezvous']) {
        $provider = $null
        $cs = $null
        $dbPath = $null

        if ($settings.Rendezvous.PSObject.Properties['Database'] -and $settings.Rendezvous.Database) {
            $db = $settings.Rendezvous.Database
            if ($db.PSObject.Properties['Provider']) { $provider = [string]$db.Provider }
            if ($db.PSObject.Properties['ConnectionString']) { $cs = [string]$db.ConnectionString }
            if ($db.PSObject.Properties['Path']) { $dbPath = [string]$db.Path }
        }

        if ([string]::IsNullOrWhiteSpace($provider) -and $settings.Rendezvous.PSObject.Properties['PostgresConnectionString'] -and $settings.Rendezvous.PostgresConnectionString) {
            $provider = 'postgres'
            $cs = [string]$settings.Rendezvous.PostgresConnectionString
        }

        Test-Database -Provider $provider -ConnectionString $cs -Path $dbPath -Where $entry.Role
    }

    Write-Host ''
}

# ---- 客户端 / 被控端（可选）----
if ($ClientSettingsPath) {
    Write-Host "检查客户端/被控端（$ClientSettingsPath）："
    $client = Read-Settings -Path $ClientSettingsPath
    if ($null -ne $client) {
        $uri = $null
        if ($client.PSObject.Properties['Transport'] -and $client.Transport.PSObject.Properties['RendezvousUri'] -and $client.Transport.RendezvousUri) { $uri = [string]$client.Transport.RendezvousUri }
        Test-RendezvousUri -Uri $uri -Where '客户端'
    }
    Write-Host ''
}

# ---- 结论 ----
if ($findings.Count -eq 0) {
    Write-Host '没有发现不安全默认值。' -ForegroundColor Green
    exit 0
}

Write-Host "发现 $($findings.Count) 处不安全默认值（上面标 [不安全] 的）。上线公网前请逐条处理。见 docs/design/README.md 与 docs/ops/README.md。" -ForegroundColor Red
exit 2
