# 部署与运维

给**要部署和运维的人**：架服务器、发版、把客户端与被控端装到机器上、出问题时查什么。
打包脚本的内部细节见“安装包怎么构建（三平台）”一节，客户端本机存了什么见“客户端本机的存储（设备列表）”一节。

> 运维监控台（节点健康、会话、告警、审计）已经可用：怎么装、怎么让总览出现数字见 “运维监控台（可选）”一节，
> 它的构成与设计见 [系统设计](../design/README.md) 的"运维监控台"一节。

被控端与服务端各自需要什么系统见“先决条件”一节；功能怎么用、哪些平台支持什么见 [使用说明](../usage/README.md)；
"部署者必须自己做的事"（换共享密钥、信令上 TLS、装包签名）见 [系统设计](../design/README.md) 的“安全边界”一节。

这套软件是**自建服务器**的：两台机器互相连接，靠的是一台它们都能连上的**信令服务器（Rendezvous）**。
没有它，被控端拿不到设备代码，控制端也无从发现对方——"设备代码是空的"几乎总是这个原因。

## 目录

- [先决条件](#一先决条件)
- [部署服务器（原生包、Docker、TLS、防火墙、健康检查和 Ops）](#二部署服务器)
- [自动化发布到 GitHub Releases](#三自动化发布到-github-releases)
- [部署客户端 / 被控端（Windows MSI）](#四部署客户端--被控端msi)
- [部署客户端 / 被控端（Linux Debian 包）](#五部署客户端--被控端debian-包linux)
- [部署客户端 / 被控端（macOS `.pkg`）](#六部署客户端--被控端pkgmacos)
- [运行期注意事项](#七运行期注意事项)
- [安装包构建](#八安装包怎么构建三平台)
- [客户端本机存储](#九客户端本机的存储设备列表)
- [故障速查](#十故障速查)
- [上线前真机验收](#十一上线前真机验收)

```text
        ┌──────────────┐   信令 WebSocket(TCP 5080)   ┌───────────────────┐
        │  控制端       │ ───────────────────────────► │  Rendezvous       │
        │  (客户端)     │ ◄─────────────────────────── │  （信令/设备代码） │
        └──────┬───────┘                              └─────────┬─────────┘
               │                                                │ 签发中继票据
               │  QUIC（直连打洞，失败时经中继）                   ▼
        ┌──────┴───────┐        UDP 5081 地址反射        ┌───────────────────┐
        │  被控端       │ ◄────────────────────────────► │  Relay (UDP 5082) │
        │ (MSI 安装)    │ ◄──────── 中继转发密文 ────────► └───────────────────┘
        └──────────────┘
```

## 一、先决条件

| 机器 | 需要什么 |
|---|---|
| 服务器 | Windows 11 / Windows Server 2022+（x64），或 Linux（x64，glibc；需要 ICU 与 OpenSSL，`.deb` 已声明），或 macOS（arm64）；.NET 10 运行时（或自包含发布，不需要装运行时）；按 [防火墙](#8-防火墙) 的端口表开放端口（信令、地址反射、中继；要从别的电脑访问运维监控台，再开放网关） |
| 被控端（Windows） | Windows 11 / Windows Server 2022+（x64）；**管理员权限**装 MSI（服务以 LocalSystem 运行） |
| 被控端（Linux） | **Ubuntu 26.04+**（x64，FFmpeg 8 的 soname 固定）或有 Wayland 会话与 portal 的同等发行版；装 `.deb` 后是一条 `systemd --user` 服务，**不需要 root 常驻** |
| 控制端 | Windows 11 / Windows Server 2022+（x64），**macOS 26+（Tahoe，Apple Silicon）**，或 **Ubuntu 26.04+**（x64） |

> **服务器可以混着来。** 信令和中继跟两端跑什么系统无关，一台 Linux 服务器可以同时服务 Windows 和 Linux 的
> 客户端与被控端。只有「被控端装什么包」是跟系统绑的。
>
> **Windows 上的 `.deb` 装不了，Linux 上的 MSI 也装不了**——这不是限制，是两个平台各有一套安装方式，
> 下面每节都写明了哪条路属于哪个平台。
>
> **为什么不是 Windows 10**：传输层走 QUIC（`System.Net.Quic` / MsQuic），它在 Windows 上依赖 Schannel 的
> TLS 1.3 加密 API，而这只在 Windows 11、Windows Server 2022 及更新系统里提供。Windows 10 缺这些 API，
> `QuicListener.IsSupported` / `QuicConnection.IsSupported` 会返回 `false`，被控端日志打出
> `QUIC is not supported on this system; incoming connections are disabled.`，表现就是控制端报
> 「无法建立网络连接」、对方报「对方无响应」。要支持 Windows 10，要么升级系统，要么换用自建的
> OpenSSL 版 `msquic.dll`（不在这份文档的官方支持矩阵里）。

服务器地址必须是**两台机器都能访问到的地址**（局域网 IP、域名或公网地址）。`127.0.0.1` 只能自己连自己。

## 二、部署服务器

### 1. 服务端安装包（三平台）

三个平台各有一个服务端安装包，装的都是同样四个程序（信令 Rendezvous、中继 Relay、监控台 Ops、Ops 的 HTTPS 网关
Gateway）：Linux 是 Debian 包，Windows 是 MSI，macOS 是 `.pkg`。四个程序放在同一个包里，是因为监控台没有另外三个
就没有数字可看，分开装只会多出"装了一半"的状态。下面按平台分节。

#### Linux：Debian 包

```sh
build/installer/linux/server/build-deb.sh
sudo apt install ./artifacts/installer/linux/RemoteDesktop-Server-Linux-amd64-<版本>.deb
```

`apt install ./...deb` 会一并解决包声明的依赖（ICU、OpenSSL、debconf、adduser 等）。用 `dpkg -i` 也可以，
但目标机缺少其中任何一个依赖时包会停在未配置状态，需要再跑一次 `sudo apt-get -f install`。

包把四个程序装到 `/usr/lib/remotedesktop-server/{Rendezvous,Relay,Ops,Gateway}`，四个 **system** 单元装到
`/lib/systemd/system`，装完即启用并启动。**Linux 的配置在 `/etc/remotedesktop-server/`**，不在安装目录里——
升级不会覆盖运维改过的东西。Windows 也把生效中的设置放在 `%ProgramData%\RemoteDesktopServer`。
**macOS 是例外**：它的设置就在安装根 `/usr/local/remotedesktop-server/<角色>/appsettings.json`，`.pkg`
不删除自己没装过的文件，所以升级同样不会覆盖，但位置与 Linux/Windows 不同。

安装过程会问两组问题（`dpkg -i` 也会问，不只是 `apt install`）：

| 问题 | 取值 | 说明 |
|---|---|---|
| 数据库 | `sqlite` / `postgres` / `none` | 前两个是正式选择；`none` 只把设备码放内存，重启即失效。Ops 用同一个 provider，`none` 时仍保留自己的 SQLite 库 |
| SQLite 文件 | 绝对路径 | 默认 `/var/lib/remotedesktop-server/rendezvous.db`；同目录下放 `ops.db`。自定义路径必须使用名为 `remotedesktop`/`remotedesktop-*`（或对应 `remote-desktop` 形式）的专用目录；安装器解析真实路径、拒绝 `/etc` 等系统/共享根目录，只设置该目录本身的属主与 0700，不递归处理已有文件 |
| PostgreSQL 连接串 | Npgsql 连接串 | 选 postgres 时必填；数据库本身要先存在，表由服务创建。升级或重配时，只有现有设置本来就用 PostgreSQL（`Rendezvous:Database:Provider` 为 `postgres`，或没有 Provider 的旧布局里留着连接串）才可留空沿用已有连接串；现有设置是 sqlite / none、只是手工留了一个旧连接串时，不会被当作答案，仍会再问一次，仍留空则安装失败 |
| 信令 TLS | `generate` / `existing` / `none` | 自签证书 / 已有证书（PKCS#12 或 PEM + 同名 `.key`）/ 明文 HTTP |
| 证书文件与口令 | 路径 / 口令 | `existing` 时询问；文件会被复制到 `/etc/remotedesktop-server/tls/custom/`，服务账户可读 |
| 公网主机名 | 可逗号分隔 | 仅 `generate` 时询问；写进自签证书的 SAN，客户端用 DNS 名或 NAT 地址访问时必须填 |

设置仍由程序自己写（`--configure-system`），`postinst` 只负责把答案变成参数——两个平台因此产出同样的
文件，走同一段代码。监控台的设置也写在 `/etc/remotedesktop-server/Ops/appsettings.json`，和
`--create-admin` 读的是同一份。

> **同一版本重装会重新询问**（默认值保留上一次的答案）；**版本升级不会**重新询问；`apt purge` 会连同
> debconf 答案一起清除，之后重装按全新安装询问。`dpkg-reconfigure` 则总是重新询问。

改配置：

```sh
sudo dpkg-reconfigure remotedesktop-server
# 非交互安装必须在安装前预置答案（第一列是包名，第二列是问题名）：
# printf '%s\n' 'remotedesktop-server remotedesktop-server/database-provider select postgres' \
#   'remotedesktop-server remotedesktop-server/postgres-connection password Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=...' \
#   | debconf-set-selections
systemctl status remotedesktop-rendezvous remotedesktop-relay remotedesktop-ops remotedesktop-gateway
# 这个包的默认答案是 generate，即自签 HTTPS；-k 是因为证书不是公共 CA 签的
curl -k https://127.0.0.1:5080/health
curl http://127.0.0.1:5090/health
```

信令 TLS 选 `generate` 时，安装脚本还会把 `/etc/remotedesktop-server/tls/signaling.crt` 写进监控台的
`Ops:NodeCertificatePath`：本机节点固定用 `127.0.0.1`，而生成的证书不一定包含这个地址（主机名在 Debian/Ubuntu
上解析到 `127.0.1.1`），固定公钥后监控台不必依赖系统信任库也能抓取 `/metrics` 并读出设备列表。选 `existing`
时，PEM 证书的情况下指向复制后的 `tls/custom/<原文件名>`；PKCS#12 文件没有可直接固定的公钥，不写这个键
（已有的固定保持不动，否则回退到系统信任库）。

`/etc/default/remotedesktop-server` 是**运行期**高级设置的入口，也是安装提问的预置值；它**不是**非交互安装的
入口——`apt`/`dpkg` 在解包这个文件之前就会提问（文件自己的注释也这么写）。非交互安装必须在安装前用
`debconf-set-selections` 预置（见上）。文件里的注释是各键的权威说明，下表只是摘要（默认值以该文件为准）。
交互式改数据库或 TLS 请用 `dpkg-reconfigure`：文件里的数据库/TLS 键只是安装问题的预置值，地址、中继、
共享密钥、网关和防火墙开关则是每次运行都从这里读。

| 键 | 含义 |
|---|---|
| `RD_URLS` | 信令监听地址（URL），默认 `http://0.0.0.0:5080` |
| `RD_RELAY_ENDPOINT` | 客户端被要求使用的中继地址，默认 `auto`（取本机第一个非回环 IPv4） |
| `RD_RELAY_KEY` | 中继票据签名密钥，默认 `auto`（已有则保持不动）；与另一台机器上的中继共用时填 base64 密钥 |
| `RD_DATABASE` / `RD_DATABASE_PATH` / `RD_POSTGRES` | 设备码存哪、SQLite 文件路径、PostgreSQL 连接串；交互问题的预置值 |
| `RD_TLS` / `RD_TLS_PASSWORD` / `RD_TLS_HOSTNAME` | 信令 TLS 模式（`generate` / `auto` 即明文 / 证书路径）、PKCS#12 口令、写进自签证书的额外 DNS 名；交互问题的预置值 |
| `RD_GATEWAY_LISTEN` / `RD_GATEWAY_HOSTNAME` | Ops HTTPS 网关的监听地址（默认 `https://0.0.0.0:5443`）与生成证书的额外 IP/DNS 名 |
| `RD_OPEN_FIREWALL` | `1` 且 ufw 处于 active 时放行端口，默认 `0` |

**防火墙默认不动。** `postinst` 只有在 `/etc/default/remotedesktop-server` 里设了 `RD_OPEN_FIREWALL="1"`（默认
`0`）**并且** ufw 处于 active 时，才放行 [防火墙](#8-防火墙) 表里需要对外开放的那四个端口（四条规则），不动 nftables/iptables；
purge 时只删除安装时加过的那四条（用数据目录里的标记判断）。所以装完后别的机器连不上，先检查防火墙有没有放行。

#### Windows：MSI

不想手工发布 + `sc.exe` + `netsh`（也就是下面 2、3、6、8 节那些步骤）的话，用仓库里的服务端安装包。

```powershell
pwsh -File build/installer/windows/server/Build-ServerInstaller.ps1
# 产物：artifacts/installer/windows/RemoteDesktop-Server-Windows-x64-<版本>.msi
```

下面的命令里 `<版本>` 是**四段**版本（`Directory.Build.props` 的三段加 `.0`，例如 `1.0.0.0`）：Windows Installer
只认四段数字。Linux/macOS 的同名占位符仍是三段，见“自动化发布到 GitHub Releases”开头的版本表。以 `artifacts/installer/windows/` 里的
实际文件名为准，本文不再写死某一版。

安装时可以把该定的都定掉（全部可省略，省略即用下表默认值）：

```powershell
msiexec /i RemoteDesktop-Server-Windows-x64-<版本>.msi /qn `
  RENDEZVOUSURL="https://0.0.0.0:5080" `
  RELAYPUBLICENDPOINT="203.0.113.10:5082" `
  POSTGRESCONNECTIONSTRING="Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=..." `
  TLSCERTIFICATEPATH="C:\rd\signaling.pfx" TLSCERTIFICATEPASSWORD="..."
```

图形界面安装时，这些值分在三页上（`选目录 → 服务器地址 → 证书 → 数据库 → 确认`）：

- **服务器地址**：信令监听地址（默认是构建时烤进去的那个，通常是 `http://127.0.0.1:5080`）、中继公网地址、TLS 三选一。
- **证书**：https 用哪张证书——证书域名、`.pfx` 路径（带「浏览…」）、口令。上一页选了"自动生成"就不用管这一页。
- **数据库**：PostgreSQL 连接串。留空就用本机 SQLite；填 `auto` 与留空等价。

`auto` 是"没指定，由安装程序按这台机器决定"的意思：这台机器以前配过 PostgreSQL 连接串就沿用（否则升级会把所有设备码
重发），否则在本机数据目录建一份 SQLite 库。它是属性表里的默认值（静默安装直接带它），向导里不该由人去填：进入数据库页时
输入框是空的，`NormalizeSettings` 只在离开最后一页时才把空值写成 `auto` 交给配置步骤——真正传给服务的空字符串是
"不用数据库、设备码只存内存"，两者不能混。

地址在离开第一页时校验：必须是 `http://` / `https://` 开头的绝对地址，且**填了 `https://` 就必须在同一页启用 TLS**
（自动生成或指定证书），否则向导弹框说明原因并留在本页——这正是"服务装完了却起不来"最常见的一种。只监听本机时留
`127.0.0.1` 即可。

它做的事：把四个服务器（信令、中继、监控台和 Ops HTTPS 网关，各自自包含）装进
`%ProgramFiles%\RemoteDesktopServer`，注册四个自动启动、崩溃后自动重启的服务
（`RemoteDesktopRendezvous`、`RemoteDesktopRelay`、`RemoteDesktopOps`、`RemoteDesktopGateway`），
加四条入站规则（端口见 [防火墙](#8-防火墙)），并按下面的属性写好信令与中继的设置。
**真正生效的设置、数据库、TLS 公钥和 `server-setup.log` 在 `%ProgramData%\RemoteDesktopServer\`**，
不在 Program Files：MSI 的大版本升级会先删除旧产品在 Program Files 里的文件，放在那里的
`appsettings.json` 会在每次升级时丢失。ProgramData 下的目录只有 SYSTEM 和 Administrators 能读。
设置由信令服务器自己写入（`--configure-system`），它把做过什么记在
`%ProgramData%\RemoteDesktopServer\server-setup.log` 里——安装程序不会给任何人看这一步的输出。

监控台（Ops）没有需要填写的安装属性：它默认只监听 `127.0.0.1:5090`，数据库在
`%ProgramData%\RemoteDesktopServer\ops.db`。同一安装包还会安装 Gateway，默认以
`https://0.0.0.0:5443` 对外提供 HTTPS；指定 `GATEWAYLISTEN` 可改监听地址。安装时会自动把本机信令/中继登记为
两个节点并打开信令的 `/metrics`，新装环境不需要手工添加节点。本机节点固定使用回环地址（`https://127.0.0.1:5080` /
`http://127.0.0.1:5083`）；没有配置 `Ops:NodeToken` 时，服务端的 `/metrics` 和 `/internal/*` 只接受回环调用，
用机器名或局域网地址访问会返回 401。**它没有默认账号**，装完要自己建第一个管理员——命令见 “运维监控台（可选）”一节 的"启动"与"首个管理员"（Windows 要在管理员
PowerShell 里执行）。

| 属性 | 默认 | 说明 |
|---|---|---|
| `RENDEZVOUSURL` | `http://127.0.0.1:5080` | 信令监听的地址；默认只监听回环，**安装向导「服务器地址」页第一行就能改**，静默安装用这个属性。填 `https://` 必须在同一页启用 TLS，否则向导拦住并说明原因。给了证书会自动变成 `https://`。公网明文监听需要 `Rendezvous:AllowInsecureSignaling=true`：用安装包或 `--configure-system` 配置时会按生效的地址自动写入，只有手工编辑 `appsettings.json` 才要自己设 |
| `RELAYPUBLICENDPOINT` | `auto` | 客户端被要求使用的中继**地址**。`auto` 取本机第一个非回环地址；也可以写 `IP:端口`，或写域名（安装时解析一次，地址就此固定） |
| `RELAYSHAREDKEY` | `auto` | 中继票据的签名密钥。`auto`：没有就生成一个，**已经有就保持不动**——升级换掉它会让另一台机器上的中继被悄悄切断 |
| `DATABASEPROVIDER` | `auto` | 设备代码存在哪：`auto` / `none` / `postgres` / `sqlite`。`auto` = 本机已有 PostgreSQL 就继续用它，否则建一个 SQLite 库 |
| `DATABASEPATH` | 空 | SQLite 文件。空 = `%ProgramData%\RemoteDesktopServer\rendezvous.db` |
| `POSTGRESCONNECTIONSTRING` | `auto` | PostgreSQL 连接串。`auto`（默认值）= 没指定，由 `DATABASEPROVIDER=auto` 按这台机器决定：已有 PostgreSQL 连接串就沿用，否则建本机 SQLite；空的 `""` 不是同一个意思，那是"不用数据库、设备码只存内存"，别在向导里清空后手工传空串 |
| `TLSCERTIFICATEPATH` / `TLSCERTIFICATEPASSWORD` | `auto` / 空 | 给了就写进 Kestrel 配置并切到 https。填 `generate` 则由这台机器生成一张自签名证书、放进本机证书存储，公钥导出到 `%ProgramData%\RemoteDesktopServer\tls\signaling.cer`（装完还能改：`Set-ServerConfiguration.cmd -TlsGenerate`）。**证书文件不存在时不切**（服务器还能起得来），只在 `server-setup.log` 里说明 |
| `TLSCERTIFICATEHOSTNAME` | 机器名 | 生成证书时写进 SAN 的 DNS 名/IP，逗号分隔。客户端用域名或 NAT 地址访问时必须填，否则即使信任了证书也会名称不匹配 |
| `GATEWAYLISTEN` | `https://0.0.0.0:5443` | Ops HTTPS 网关的监听地址，必须是不带路径的 `https://` 地址 |
| `GATEWAYHOSTNAME` | 空 | 给网关证书追加的 IP/DNS 名，逗号分隔；本机 IPv4 已自动包含 |
| `NOFIREWALL` | `0` | 设成 `1` 就不加防火墙规则（信令、反射、中继三条，外加网关的 TCP 5443 那一条） |
| `REMOVEDATA` | `1` | 卸载时默认删除 `%ProgramData%\RemoteDesktopServer` 下的数据库、设置与生成的证书；设成 `0` 保留（重装后设备码与监控台历史不变） |

**这些属性只在第一次安装时生效。** 升级/同版本重装会保留 ProgramData 下的现有设置（与 Linux/macOS 的
“升级不重新询问”一致）；要改请用 `Set-ServerConfiguration.cmd`（见“装完之后改配置”）。

三件必须知道的事：

- **安装包默认用 SQLite，装完就能用。** 不填连接串时设备代码存在本机的
  `%ProgramData%\RemoteDesktopServer\rendezvous.db`，重启、升级都不变。**这个文件要备份**：删掉它等于
  所有设备码重发，无人值守的机器会因此失联；它也**只能放本地磁盘**，放网络共享上会损坏。
  要回到内存模式（每次重启换码）用 `DATABASEPROVIDER=none`。
- **中继装在另一台机器上时**，那把自动生成的密钥要抄过去：两边都传 `RELAYSHAREDKEY=<同一个值>`，
  或者把 `Rendezvous:RelaySharedKey` 的值填进那台的 `Relay\appsettings.json` 的 `Relay:SharedKey`。
- **改了端口就要自己改防火墙规则**：四条规则是按 [防火墙](#8-防火墙) 里的默认端口开的，
  `RENDEZVOUSURL`、中继端口或 `GATEWAYLISTEN` 改了它们就对不上。

升级：同版本号重装即可，**`%ProgramData%\RemoteDesktopServer` 下的设置、数据库和证书都保留**，
手改过的值和已经生成的密钥不会被包内默认值覆盖。
卸载用「应用和功能」或 `msiexec /x`，服务和防火墙规则一并移除，**默认连同 ProgramData 下的数据与生成的证书一起删除**。
要保留它们（重装后设备码与监控台历史不变）：`msiexec /x ... /qn REMOVEDATA=0`。

#### macOS：.pkg

```bash
build/installer/macos/server/make-pkg.sh --configuration Release
# 产物：artifacts/installer/macos/RemoteDesktop-Server-macOS-arm64-<版本>.pkg
```

装到 `/usr/local/remotedesktop-server`，并把四个服务器注册成 **launchd 守护进程**
（`com.remotedesktop.rendezvous` / `com.remotedesktop.relay` / `com.remotedesktop.ops` /
`com.remotedesktop.gateway`，开机自启、崩溃重启）—— 这就是 Windows 那四个服务的对应物。

四个守护进程以专用账号 **`_remotedesktop`** 运行（与 Linux 的 `remotedesktop` 对应）：plist 里写
`UserName`/`GroupName`，postinstall 创建隐藏账号（UID/GID 从 300 起找空位，`/usr/bin/false`、无自己的家目录），
把 `var/`（数据库、DataProtection 密钥环）与 `logs/`（`launchd` 的 stdout）交给它，并让 `appsettings.json`
与 `tls/` 变成 `root:_remotedesktop`、0640/0750 —— 服务读得到、其他本机用户读不到。postinstall 会按实际数据库路径
准备父目录，并以该账号实测读设置、读私钥、写数据库目录与 `logs`；任何一项不通过都会使安装失败，**不会回退为 root
运行**。四个服务 bootstrap、active 检查和信令 `/health` 检查也同样是安装成功的条件。`configure-server.sh` 改写设置后会重新应用同一组权限。

```bash
sudo installer -pkg <文件>.pkg -target /
curl http://127.0.0.1:5080/health      # 返回 Healthy 即信令服务器活着（含数据库）
curl http://127.0.0.1:5083/health      # 中继的 HTTP 面（回环）
curl http://127.0.0.1:5090/health      # 监控台
```

装完同样要自己建第一个管理员，见 “运维监控台（可选）”一节（macOS 必须用 `_remotedesktop` 账号运行那条命令）。

**安装时选择配置（与 Linux 的 debconf 对应）。** `.pkg` 由安装器里的自定义面板收集答案，和 Linux 安装时
问的是同一组问题：

- **数据库**：SQLite（默认，文件在 `var/rendezvous.db`）或 PostgreSQL（填写连接串）。监控台跟随同一个
  provider。
- **TLS**：不使用（`http`）、生成自签名证书（可填客户端将使用的域名/IP），或使用已有证书
  （PKCS#12，或与 `.key` 同目录同名的 PEM）。
- **监听地址**：面板不问。TLS 选「不使用」时信令绑 `http://0.0.0.0:5080` 并开启
  `Rendezvous:AllowInsecureSignaling`（明文、所有网卡），选生成或已有证书时绑 `https://0.0.0.0:5080`；升级且 TLS 模式
  没变则沿用上次的地址。只有完全没有答案（`--urls auto`）时才是只监听回环的 `http://127.0.0.1:5080`。
- 证书口令与连接串写进 `appsettings.json`（`root:_remotedesktop`，0640，只有服务账号读得到）。面板暂存的
  `/var/tmp` 答案必须是 root 或当前 console user 所有的普通单链接文件；其他账户预置的文件、符号链接和硬链接会被忽略。
  root 暂存副本由唯一临时文件原子重命名产生，并在 postinstall 读取后删除。
- 升级时同一页默认勾选 **保留现有设置**，不会把运维改过的数据库、TLS 或地址覆盖掉。

**无人值守安装**（`sudo installer`、MDM）把同样的答案写到
`/Library/Application Support/RemoteDesktop/install-answers.plist`（`root:wheel`，0600）。这个文件在用完后
**不会删除**：下次没有面板答案的 `installer -pkg` 或 MDM 安装会再次应用它（`PreserveExisting=true` 除外）。
不再需要这套预设时请删掉它，否则它会覆盖现场运维后来的修改。

| 键 | 值 |
|---|---|
| `DatabaseProvider` | `sqlite` / `postgres` / `none` |
| `DatabasePath` | SQLite 文件（绝对路径；自定义父目录须为 `remotedesktop`/`remotedesktop-*` 专用目录） |
| `PostgresConnectionString` | PostgreSQL 连接串 |
| `TlsMode` | `none` / `generate` / `existing` |
| `TlsCertificatePath` / `TlsPassword` | 已有证书的绝对路径与口令 |
| `TlsHostName` | `generate` 时写进证书 SAN 的域名/IP，逗号分隔 |
| `PreserveExisting` | `true` 表示只更新程序、保留现有设置（升级） |

打包脚本上的 `--database` / `--postgres` / `--tls-certificate` 等选项仍然保留，作为**首装且没有答案文件**
时的回退（CI 就靠它）。**分发构建（`--distribution`）会拒绝 `--postgres`、`--relay-key`、`--tls-password`**：
`.pkg` 是一个可以被拷走的文件，把数据库口令放进去等于把它交给了每个拿到安装包的人。

**三件与 Windows 不同的事**：

- **防火墙**：macOS 没有"给某个程序开某个端口"的安装步骤（它的应用防火墙默认关闭，且是按应用授权）。
  别的机器要连这台，自己按 [防火墙](#8-防火墙) 的端口表按需放通；要从别的电脑访问监控台，再放通网关的端口。
- **卸载**：`.pkg` 不带卸载器，安装目录里有 `uninstall-server.sh`（停守护进程、删 plist、安装目录、
  文档目录与安装回执；`--keep-settings` 可只删程序）。`_remotedesktop` 账号与组会留下，和 Linux 上
  `remotedesktop` 账号的处理一致——重装后同一份数据仍然属于它。
- **改配置**：安装目录里的 `configure-server.sh`，对应 Windows 的 `Set-ServerConfiguration.cmd` ——
  两者跑的是同一个 `--configure-system` 步骤，但选项不完全相同：`configure-server.sh` 没有
  `--tls-generate` / `--tls-hostname`（Windows 的 `-TlsGenerate` / `-TlsHostName`），要自签证书请在安装时让安装器生成，
  或用 `build/signing/New-ServerCertificate.ps1` 生成 `.pfx` 后交给 `--tls-certificate`。它会重启**四个**守护进程
  （信令、中继、监控台与网关；数据库 provider 与监控台共享）。

> 两个平台的服务器是**同一份可执行文件**、同一个 `--configure-system` 步骤，所以写出来的
> `appsettings.json` 与生成的中继密钥完全一致：换平台不需要换配置方式。
> 自签名证书既可以让安装器生成，也可以用 `build/signing/New-ServerCertificate.ps1` 生成后把 `.pfx`
> 交给 `configure-server.sh --tls-certificate`。启用 TLS（`Urls` 为 `https://`）时，公开部分会导出到
> `/usr/local/share/doc/remotedesktop-server/signaling.crt`——生成的证书、PEM 或 `.pfx` 都一样，
> 客户端/被控端安装器可以直接选它；Ops 监控台也用这份 PEM 固定信令证书（`.pfx` 带口令，不能直接当 pin）。

#### 装完之后改配置（Windows）

安装向导里填的那些，装完还能改。安装目录（`%ProgramFiles%\RemoteDesktopServer`）里有：

- **`Set-ServerConfiguration.cmd`** —— 推荐的路子。在**管理员**命令提示符或 PowerShell 里给它想改的项即可，
  没给的保持不动；它写完之后会登记/更新监控台的本机节点，然后重启四个服务（信令、中继、监控台、网关）让设置生效：

  ```powershell
  cd "C:\Program Files\RemoteDesktopServer"
  .\Set-ServerConfiguration.cmd -DatabaseProvider sqlite
  .\Set-ServerConfiguration.cmd -PostgresConnectionString 'Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=...'
  .\Set-ServerConfiguration.cmd -TlsGenerate -TlsHostName rd.example.com   # 生成自签证书并切 https
  .\Set-ServerConfiguration.cmd -RelayPublicEndpoint 203.0.113.10:5082
  .\Set-ServerConfiguration.cmd -DatabaseProvider none -NoRestart   # 回到不用数据库，先不重启
  ```

  `.cmd` 只是用 `-ExecutionPolicy Bypass` 启动旁边的 `.ps1`，所以 Windows 11 客户端默认的 Restricted 策略
  不会挡住它；也可以直接 `powershell -ExecutionPolicy Bypass -File .\Set-ServerConfiguration.ps1 ...`。
  它调用与安装程序**同一个**配置步骤（`Rendezvous\RemoteDesktop.Server.Rendezvous.exe --configure-system`），
  所以两者的行为不会分叉；做过什么会追加到 `%ProgramData%\RemoteDesktopServer\server-setup.log`。
  拿不准就先加 `-WhatIf` 看一眼（不打印口令）。
- **`how-to-configure.txt`** —— 随包的离线速查，面向拿到机器的人（开始菜单里的入口叫「服务器配置说明」）。三平台副本由 `build/tools/Export-ServerConfigurationGuides.ps1` 从统一源生成，不要直接编辑。
- 四份 `appsettings.json`（`Rendezvous\`、`Relay\`、`Ops\`、`Gateway\`）在
  `%ProgramData%\RemoteDesktopServer\` 下 —— 也可以直接改，改完重启对应服务。
  它们在升级时不会被覆盖，所以手改的值会保留；目录只有 SYSTEM 与 Administrators 能读。

#### 数据库怎么选

设备代码（那 9 位数字）是无人值守访问的地址，它必须活过重启。两种存法：

| | SQLite（默认） | PostgreSQL |
|---|---|---|
| 装之前要准备 | 什么都不用 | 一个库、一个账号 |
| 适合 | 一台服务器 | 多台服务器，或已有备份与监控体系 |
| 多实例 | **不行**：每个实例一个文件，同一台设备会拿到不同的码 | 设备码可以共库；在线状态与连接握手不跨实例共享（见“运行期注意事项”里“多台服务器”） |
| 备份 | 停服务后拷文件，或 `sqlite3 <db> ".backup out.db"` | `pg_dump` |
| 换到另一种 | 设备码全部重发（除非按下面搬数据） | — |

SQLite 文件的位置：Windows `%ProgramData%\RemoteDesktopServer\rendezvous.db`、
macOS `/usr/local/remotedesktop-server/var/rendezvous.db`、Linux `/var/lib/remotedesktop-server/rendezvous.db`。
三件事要记住：

- **只放本地磁盘。** SQLite 在网络共享（NFS/SMB）上会损坏，这不是配置问题。
- **目录权限就是保护。** 目录是 0700 / 仅服务账号——能写这个文件的人可以改 `device_id`，等于能劫持设备码。
- **它是唯一的一份数据。** 删掉文件、或挂载点没挂上，服务启动时会建表并继续跑：所有设备会在下一次连接时
  拿到新码。`/health` 探的是表在不在（不是连接通不通），运行中文件被删才会变红；备份仍然只有你自己能做。

从 PostgreSQL 换到 SQLite 会重发所有设备码，反过来也一样，除非先把行搬过去。`device_id` 必须原样搬——
它就是那台设备的地址：

```bash
# 1. 先用 SQLite 启动一次，让服务把表建出来，然后停掉。
# 2. 导出、导入。created_at / last_seen_at 是 TEXT，原样搬即可。
psql "$PG" -c "\copy device_registrations TO 'devices.csv' CSV HEADER"
sqlite3 rendezvous.db ".mode csv" ".import --skip 1 devices.csv device_registrations"
#    revoked_at 没有封禁时导出为空字段；保险起见把空串还原成 NULL（代码以 revoked_at IS NOT NULL 判断封禁）：
sqlite3 rendezvous.db "UPDATE device_registrations SET revoked_at = NULL WHERE revoked_at = '';"
# 3. 重启信令服务，curl 它的 /health（HTTP 或自签 HTTPS 加 -k）应为 Healthy。
```

### 2. 手工构建

```bash
dotnet publish src/Server/RemoteDesktop.Server.Rendezvous -c Release -r win-x64 --self-contained true -o artifacts/app/windows/server/Rendezvous
dotnet publish src/Server/RemoteDesktop.Server.Relay      -c Release -r win-x64 --self-contained true -o artifacts/app/windows/server/Relay
dotnet publish src/Server/RemoteDesktop.Server.Ops        -c Release -r win-x64 --self-contained true -o artifacts/app/windows/server/Ops
dotnet publish src/Server/RemoteDesktop.Server.Gateway    -c Release -r win-x64 --self-contained true -o artifacts/app/windows/server/Gateway
```

四个服务端各自一个子目录，因为它们是四个程序，装在同一台机器上时目录不能混。Gateway 只是监控台的 HTTPS 入口，
不装监控台就不需要它；它随带的 `appsettings.json` 已经内置了 `ReverseProxy` 的路由与集群（转发到
`http://127.0.0.1:5090`），默认监听 `https://0.0.0.0:5443`。手工部署缺的是 Kestrel 证书：证书由 `--configure-system`
生成并写入，手工部署时要自己提供并配置，所以要网关请优先用服务端安装包。
`build/installer/windows/server/Build-ServerInstaller.ps1` 用的就是这个布局（见它的 `$PublishDirectory` 与
`-o (Join-Path $PublishDirectory $server.Folder)`），包里的 `Set-ServerConfiguration.cmd`/`.ps1`、`how-to-configure.txt`
与信令/中继的两份 `appsettings.json` 也在这一层；监控台自己那份在 `Ops\appsettings.json`。

（Linux/macOS 把 `-r win-x64` 换成 `linux-x64` / `osx-arm64` 等，并把 `artifacts/app/windows/` 里的 `windows`
换成对应平台 —— 服务端是跨平台的。产物目录的约定见 [CONTRIBUTING.md](../../CONTRIBUTING.md) 的"构建产物布局"。）

### 3. 配置

`appsettings.json` 与各自的可执行文件放在一起。Windows 上，如果 `%ProgramData%\RemoteDesktopServer\<角色>\appsettings.json`
已经存在（服务端安装包写的就是这一份），它会叠加在可执行文件旁那份之上；环境变量和命令行仍然优先。

`RemoteDesktop.Server.Rendezvous/appsettings.json`：

```jsonc
{
  "Urls": "http://127.0.0.1:5080",              // 默认只监听回环；公网请用 https，或显式允许明文。支持 *、+ 通配主机名和分号分隔的多个地址
  "Rendezvous": {
    "AllowInsecureSignaling": false,            // 只在隔离的明文实验网里设为 true
    "ReflectorPort": 5081,                      // UDP 地址反射端口：告诉双方彼此看到的公网地址
    "RelayEndpoint": "203.0.113.10:5082",        // 应当是客户端能连到的 IP:端口；域名过不了校验，0.0.0.0 或 127.0.0.1 能过校验但客户端连不到
    "RelaySharedKey": "<和 Relay 相同的长随机串>", // 生产环境必须换掉开发默认值（appsettings.Development.json 里是明文的开发密钥）
    "RelayTicketLifetime": "00:02:00",
    "ConnectTimeout": "00:00:20",
    "MetricsEnabled": false                     // 打开后在 /metrics 暴露 Prometheus 指标（仍需 NodeToken 或回环）；默认关闭，用法见"健康检查"一节
  }
}
```

`RemoteDesktop.Server.Relay/appsettings.json`：

```jsonc
{
  "Relay": {
    "Port": 5082,
    "SharedKey": "<和上边完全一致>",               // 用来验证 Rendezvous 签发的票据签名，至少 32 字节的 base64
    "IdleTimeout": "00:02:00"
  }
}
```

密钥用一条命令生成：`openssl rand -base64 48`。**两边必须一致**；不一致时中继会拒绝所有票据，
而客户端只会看到"连不上"（被控端日志里会有中继相关的失败）。

`Rendezvous:RelayEndpoint` 留空等于关闭中继回退：此时不校验 `Rendezvous:RelaySharedKey`，直连失败的连接没有退路；
填了就必须是合法的 `IP:端口`，并且要有至少 32 字节的 base64 `RelaySharedKey`，否则服务启动失败。

上面的示例只列了最常改的几项。其余设置：

| 设置 | 默认 | 说明 |
|---|---|---|
| `Rendezvous:Database:Provider` / `ConnectionString` / `Path` | 未设置 | 设备代码存哪：`none` / `postgres` / `sqlite`；`postgres` 填 `ConnectionString`，`sqlite` 填 `Path`。没写 `Provider` 时仍会读旧键 `Rendezvous:PostgresConnectionString` 与 `ConnectionStrings:Postgres`，所以已装机器升级后设备代码不变；都没有就退回内存（见"数据库怎么选"） |
| `Rendezvous:PresenceEnabled` | `true` | 是否回答在线状态查询；关掉后客户端的状态灯显示"未知" |
| `Rendezvous:NodeToken` / `Relay:NodeToken` | 空 | 监控台访问 `/metrics` 与 `/internal/*` 的 Bearer；空 = 只接受回环调用。三处同值，见"内部 API 与 NODE_TOKEN" |
| `Rendezvous:Name` / `Relay:Name` | `rendezvous` / `relay` | 监控台节点列表里这台服务器的名字 |
| `Relay:HttpEndpoint` | `http://127.0.0.1:5083` | 中继的 HTTP 面（`/health`、`/metrics`、`/internal/v1/info`），默认只在回环 |

### 4. 生产环境：信令必须走 TLS

`ws://` 是开发用的默认值，公网上不要用：设备代码、你的公网地址、谁在连谁，以及**中继票据**都在明文里
（票据是一张 2 分钟有效的凭据，在途被拿走就能抢用你的中继带宽）。两种做法任选一种。

**做法 A：服务器自己上 TLS**（有公网 IP 和证书文件时最省事）。改 `appsettings.json`：

```jsonc
{
  "Urls": "https://0.0.0.0:5080",
  "Kestrel": {
    "Certificates": {
      "Default": { "Path": "/etc/rd/signaling.pfx", "Password": "<pfx 口令>" }
    }
  }
}
```

证书用 PEM 时给 `Path` 指证书、再加一个 `KeyPath` 指私钥。客户端地址改成 `wss://你的域名:5080/ws`。
**证书里的域名要和客户端填的地址一致**：客户端默认走系统信任链校验（`Transport:RendezvousCertificatePath`
为空时），所以 SAN 必须匹配。若安装时钉住了服务器公钥（“部署客户端 / 被控端（MSI）”一节 的 `SERVERCERTIFICATEPATH` → 设置里的
`Transport:RendezvousCertificatePath`），客户端只信任这一张证书、不再做主机名校验，那时域名对不上也能连。
这条路径有集成测试（`tests/RemoteDesktop.Transport.IntegrationTests/TlsSignalingTests.cs`），
用的是和上面完全相同的两个设置项。

**做法 B：前面放反向代理**（Docker 部署，或想用 Let's Encrypt 自动续期）。Caddy：

```text
rd.example.com {
    reverse_proxy /ws* 127.0.0.1:5080
    reverse_proxy /health 127.0.0.1:5080
}
```

nginx：

```nginx
server {
    listen 443 ssl;
    server_name rd.example.com;
    ssl_certificate     /etc/letsencrypt/live/rd.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/rd.example.com/privkey.pem;

    location /ws {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_read_timeout 1h;   # 信令是长连接，默认 60 秒会把设备反复踢下线
    }

    location /health { proxy_pass http://127.0.0.1:5080; }
}
```

代理后面要告诉服务器**谁才是真客户端**，否则按来源地址的限流会把整个代理看成一台机器：

```jsonc
"Rendezvous": { "TrustedProxies": ["127.0.0.1"] }       // 同一台机器上的 nginx/Caddy
"Rendezvous": { "TrustedProxies": ["172.16.0.0/12"] }   // 容器网络里的代理
```

`X-Forwarded-For` 谁都能写，所以只有这里列出的地址发来的才被采信；**不配就谁都不信，包括本机回环**——
连接来自 127.0.0.1 只说明它是本机某个进程，不说明它写进去的地址是真是假。代理必须自己把真实地址写进
`X-Forwarded-For`（上面的 nginx 配置已经这么做；Caddy 默认会写）。同时记得把 5080 从公网关上，只让代理能连它，
并且**只代理 `/ws` 与 `/health`**：`/internal/v1/*` 是给监控台用的，不配 `NodeToken` 时只接受回环调用，
一个把任意路径都转发的本机代理会替外面的请求满足这个条件。

> UDP 5081（地址反射）和 5082（中继）不走这个代理，仍然是明文 UDP：中继只转发 QUIC 密文，看不到内容；
> 反射服务只回一句"你从哪来"。凭据（中继票据）走的是信令，已经在上面的 TLS 里了。

**内网也一样，而且不需要公网 CA。** 局域网上的明文信令同样会被同网段的人读到（设备代码、谁在连谁）和改动，
区别只是"谁能站到这条线上"。用一张自己签的证书就能开 TLS：

```powershell
# 1. 给服务器生成证书。把客户端会用到的名字和地址都列上——客户端会拿它去对地址，对不上就报证书错误。
.\build\signing\New-ServerCertificate.ps1 -DnsName rd.lan -IpAddress 192.168.1.10
#    产物：rd.lan.pfx（放服务器，含私钥）+ rd.lan.cer（给客户端机器）
# 2. 服务器端按"做法 A"配置：Urls 改 https，Kestrel 指向那个 .pfx。
# 3. 客户端：打包时带上证书的公钥，之后装这个包的机器就信任它。
.\build\installer\windows\client\Build-ClientInstaller.ps1 -RendezvousUri wss://rd.lan:5080/ws `
    -ServerCertificate build\signing\certificates\rd.lan.cer
```

`-ServerCertificate` 把证书的公钥部分放进安装包，安装时装进那台机器的"受信任的根证书颁发机构"——和自签名
代码签名证书走的是同一条路（`Components.wxs` 里的两个条件组件）。已经装好软件的机器不重装也能补上，管理员执行：

```powershell
Import-Certificate -FilePath rd.lan.cer -CertStoreLocation Cert:\LocalMachine\Root
```

> 把一张自签证书装进受信任根，等于**持有它私钥的人可以在那些机器上冒充任何服务器**。私钥（.pfx）只放在
> 服务器上，别外传；证书换新时，客户端机器要跟着换。

也可以在安装时选择服务器证书（见 “部署客户端 / 被控端（MSI）”一节）：那条路不会修改系统受信任根，而是把公钥固定到
`%ProgramData%\RemoteDesktop\certificates\signaling.cer`，只信任这一个服务器证书，范围比装进根证书更窄。
两种方式可以并存；`-ServerCertificate` 仍然适合希望整台机器信任该自签 CA 的部署。

### 5. 公网上的限额

默认值按"公网上的自建服务"选的，除非你的规模和它差很远，否则不用动。被限流的事件以 Warning 写进日志。
连接数满、注册限流和注册校验失败这三类**带来源地址**——这是服务器唯一把地址写下来的地方，专门为了让你能看出
有人在滥用；连接请求限流与在线状态查询限流记的是设备 ID，不带地址。

| 设置 | 默认 | 管什么 |
|---|---|---|
| `Rendezvous:MaxConnections` | 4096 | 同时服务多少台设备；超出的在 WebSocket 升级前就被拒，不占内存 |
| `Rendezvous:MaxConnectionsPerAddress` | 256 | 一个来源地址最多占多少条连接 |
| `Rendezvous:RegistrationBurst` / `RegistrationRefill` | 256 / 5 秒 | 一个来源地址注册设备的速度（IPv6 按 /64 合并计数，IPv4 映射地址按其 IPv4 形式计数，与每地址连接数上限一致）。注册就是领一个身份，而"每设备"的限额都以身份计数，所以这条最要紧 |
| `Rendezvous:ConnectRequestBurst` / `ConnectRequestRefill` | 10 / 3 秒 | 一台设备发起连接请求的速度（防止拿它去轰炸别人的弹窗） |
| `Rendezvous:MaxPendingRequestsPerDevice` | 8 | 一台设备同时挂起的连接请求数 |
| `Rendezvous:MaxPendingRequests` | 4096 | 全服务器同时挂起的连接请求数 |

### 6. 作为服务运行

以**管理员** PowerShell 执行（四个服务端可执行文件都能直接作为 Windows 服务运行，同一份文件也能在终端里直接跑；
下面只演示信令与中继两个，监控台 `RemoteDesktopOps` 同理）：

```powershell
sc.exe create RemoteDesktopRendezvous binPath= "\"C:\rd\rendezvous\RemoteDesktop.Server.Rendezvous.exe\"" start= auto
sc.exe create RemoteDesktopRelay      binPath= "\"C:\rd\relay\RemoteDesktop.Server.Relay.exe\""           start= auto
sc.exe failure RemoteDesktopRendezvous reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc.exe failure RemoteDesktopRelay      reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc.exe start RemoteDesktopRendezvous
sc.exe start RemoteDesktopRelay
```

- 作为服务运行时日志走**Windows 事件日志**（"应用程序"日志，来源是程序名，如 `RemoteDesktop.Server.Rendezvous`；服务名是 `RemoteDesktopRendezvous`），配置文件仍读可执行文件旁边的 `appsettings.json`。
- 想看程序的标准输出，就先停掉服务，再直接运行可执行文件（`.\RemoteDesktop.Server.Rendezvous.exe`），日志打到标准输出。
- 手工 `sc.exe` 部署不带 Gateway（见上面 “手工构建”一节 的说明）；用安装包的话四个服务都由 MSI 注册。
- Linux 用 systemd 单元调用同样的可执行文件即可（`Restart=always`）；服务生命周期那一层在非 Windows 上是空操作。

### 7. 用 Docker 部署（Linux 服务器首选）

仓库里已经准备好四个镜像和一组 compose 文件（`build/docker/`），从源码在容器里构建，运行镜像基于 .NET 10
运行时（不装 SDK）。四个镜像分别是信令、中继、监控台和 Ops HTTPS 网关；监控台与网关是可选 overlay。

```bash
cd build/docker
cp .env.example .env      # 四个必填值在文件里是空的，必须填好（留空时 compose 会拒绝启动）；启用 Ops 时再填 OPS_GATEWAY_IP
docker compose up -d --build
```

> **需要 Docker Engine 23+。** 四个 Dockerfile 用 BuildKit 的 NuGet cache mount 加速构建；Docker Desktop 和
> 现代 Docker Engine 默认启用 BuildKit。

`docker compose` 自动读取 `build/docker/.env`。基础 compose 必填的四项：

| 变量 | 生成方式 | 用途 |
|---|---|---|
| `POSTGRES_PASSWORD` | `openssl rand -base64 24` | PostgreSQL 口令；没有默认值 |
| `RELAY_SHARED_KEY` | `openssl rand -base64 48` | 中继票据签名密钥；信令与中继必须同值 |
| `RELAY_PUBLIC_ENDPOINT` | 例如 `203.0.113.10:5082` | 客户端被要求使用的中继**地址**（必须是地址，不是域名） |
| `NODE_TOKEN` | `openssl rand -base64 32` | `/internal/*` 与 `/metrics` 的 Bearer；信令、中继、监控台同值 |

加载 `docker-compose.ops.yml` 时还必须设置 `OPS_INGEST_KEY` 和 `OPS_GATEWAY_IP`：后者是管理员在浏览器中访问
Ops 时使用的 Docker 宿主机 IPv4 地址（有 NAT 时填 NAT 地址），会写进自签名证书的 IP SAN。

两个网络服务器都会在**启动时校验密钥**：`RELAY_SHARED_KEY` 没设时，compose 在解析阶段就会报错
（`:?` 语法），根本走不到启动容器那一步；设了但太短（少于 32 字节）或不是 base64 时，容器启动校验失败、立刻退出并说明原因。
两种情况都不会带着弱密钥跑起来。`RELAY_PUBLIC_ENDPOINT` 必须是客户端可达的地址；
写成 `127.0.0.1:5082` 只有所有东西都在同一台机器上时才对。

#### 公网：用 TLS overlay

容器里的信令监听 `http://0.0.0.0:5080`（compose 里的 `Urls`，并显式设了 `Rendezvous__AllowInsecureSignaling`），
这样发布端口、Caddy 和监控台才连得到它；真正的暴露范围由 `ports:` 决定——默认 compose 把信令发布在
`127.0.0.1:5080`，公网客户端连不到它。公网部署用 Caddy overlay 终止 TLS：

```bash
export RENDEZVOUS_DOMAIN=rd.example.com   # 写进 .env 也可以
docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d --build
```

Caddy 为该域名申请并续期证书，只转发 `/ws` 与 `/health`；`/internal` 与 `/metrics` 不经过代理。客户端地址是
`wss://rd.example.com/ws`。80/443 必须可用；如果 80 被占用，Caddy 的 HTTP-01 验证与 HTTP→HTTPS 跳转会失败，
需要改用 DNS 验证或把证书交给别的代理。

#### 局域网：显式选择明文直连

如果只在可信局域网里用，可以跳过 TLS，把信令绑到所有网卡：

```bash
RENDEZVOUS_BIND_IP=0.0.0.0 docker compose up -d --build
```

客户端用 `ws://<服务器>:5080/ws`，并在客户端/被控端设置 `Transport:AllowInsecureSignaling=true`（非回环明文
默认被拒绝）。这是**明文**：设备码、在线状态、谁在连谁以及中继票据都在线上可读。不要把它
用在公网；公网请用上一节的 TLS overlay。把信令发布到可信网络之外之前，要先加上 TLS overlay 或下面的 selfsigned overlay。

#### 局域网：自签 TLS（selfsigned overlay）

没有公网域名、但要让局域网里的客户端走 `wss://` 时，用 `docker-compose.selfsigned.yml`：信令在 5080 直接提供 TLS，
并另开一个只给监控台用的内部明文口 5082/tcp（监控台按服务名访问，不必信任自签证书；中继的 5082/udp 不受影响）。

```bash
export TLS_CERT_PATH=../signing/certificates/rd.lan.pfx   # 用 build/signing/New-ServerCertificate.ps1 生成
export TLS_CERT_PASSWORD=...
export RENDEZVOUS_BIND_IP=0.0.0.0
export OPS_GATEWAY_IP=192.168.1.10                        # 同时启用监控台时需要
docker compose -f docker-compose.yml -f docker-compose.selfsigned.yml -f docker-compose.ops.yml up -d --build
```

`.pfx` 以只读方式挂进容器，而容器以 UID 1654 运行。仓库脚本在 Unix 上把 PFX 固定为 0600（Windows 使用受限 ACL），
再给容器用户读权限、又不能让所有人可读：`setfacl -m u:1654:r-- <.pfx>`（证书重新生成后再来一次）。
监听地址可用 `RENDEZVOUS_URLS` 覆盖，默认
`https://0.0.0.0:5080;http://0.0.0.0:5082`。启用监控台时把 `OPS_RENDEZVOUS_NODE_URL` 设为 `http://rendezvous:5082`。

#### 端口与防火墙

各端口的用途与是否需要对外开放见 [防火墙](#8-防火墙)，Docker 部署特有的几点是：

- `5080/tcp`：默认只绑回环；TLS overlay 通过 Caddy 暴露 443。
- `5081/udp`、`5082/udp`：**必须放通**（原因见 “防火墙”一节）。
- `5443/tcp`：加载 `docker-compose.ops.yml` 后的 Ops HTTPS 网关（宿主机端口用 `OPS_GATEWAY_PORT` 改，默认 5443；
  监听的网卡用 `OPS_GATEWAY_BIND_IP`）。Ops 的 5090 不再发布到宿主机。
- 中继的 HTTP 面（`/health`、`/metrics`，见 `Relay:HttpEndpoint`）默认只在容器内回环，不对外发布；加载 ops overlay
  后改为 `0.0.0.0:5083`（只在 compose 内部网络、仍不发布到宿主机，靠 `NODE_TOKEN` 保护）。
- Docker 通过自己的 iptables 链发布 `ports:`，常见配置下 **ufw 不会拦住这些端口**。要限制 5080/5443，用
  Compose 的绑定地址（`RENDEZVOUS_BIND_IP`、`OPS_GATEWAY_BIND_IP`）或 DOCKER-USER 规则，不要只依赖 ufw。
- `RENDEZVOUS_TRUSTED_PROXIES` 默认 `172.16.0.0/12`，覆盖 Docker 的默认 bridge 网段。这个范围很宽：如果局域网
  本身也用 172.16/12，就把它缩到代理的实际地址或 compose 专用网段，别把客户端网段一起包含进去。
- 地址反射依赖容器看到客户端真实的 UDP 源地址。标准 Docker bridge + iptables DNAT 会保留它；rootless Docker、
  slirp4netns 或某些 userland-proxy 配置会改写源地址，表现为双方拿到同一个地址、直连打洞失效（连接退回中继）。
  这类环境里对两个 UDP 服务使用 `network_mode: host`，或先用两个客户端验证一次直连。
- `POSTGRES_PASSWORD`、`RELAY_SHARED_KEY`、`NODE_TOKEN` 通过环境变量传入，宿主机上的 `docker inspect` 能看到；
  自建单机通常可以接受，要求更严时把 compose 放在只有运维能登录的宿主机上。

#### 内部 API 与 `NODE_TOKEN`

信令的 `/internal/v1/*` 与 `/metrics` 和 `/ws` 共用 5080。`NODE_TOKEN` 为空时，节点只接受**回环地址**发来的
调用；Docker 里从别的容器或网络来的请求会被 401，所以监控台和跨机部署必须设置它。三个进程读取的键不同：

```text
信令    Rendezvous:NodeToken
中继    Relay:NodeToken
监控台  Ops:NodeToken
```

三处必须同值。只设 `Ops:NodeToken` 时监控台会带着 token 去请求，而节点因为没有配置而只接受回环调用：跨机或
容器里的监控台会 401（同机回环仍然可用，但那是回环豁免，不是 token 生效）。

#### 监控台（可选）

监控台不在基础 compose 里，用 overlay 启动：

```bash
export OPS_INGEST_KEY="$(openssl rand -base64 32)"   # 写进 .env 也可以
export OPS_GATEWAY_IP=192.168.1.10                    # 管理员在浏览器里使用的宿主机地址
docker compose -f docker-compose.yml -f docker-compose.ops.yml up -d --build
docker compose -f docker-compose.yml -f docker-compose.ops.yml run --rm -it ops --create-admin admin
```

Ops 只加入 Compose 内部网络，不发布宿主机端口；`gateway` 是唯一对外监听，使用
`https://<OPS_GATEWAY_IP>:5443` 访问并转发到 `http://ops:5090`。自签名证书由 gateway 首次启动时生成，
包含 `OPS_GATEWAY_IP` 的 IP SAN；`OPS_GATEWAY_DNS` 可以追加 DNS 名称。证书保存在 `ops-gateway-data` 卷中：

```bash
docker compose -f docker-compose.yml -f docker-compose.ops.yml \
  cp gateway:/data/gateway/tls/ops-gateway.crt ./ops-gateway.crt
docker compose -f docker-compose.yml -f docker-compose.ops.yml \
  exec gateway openssl x509 -in /data/gateway/tls/ops-gateway.crt \
  -noout -subject -ext subjectAltName -fingerprint -sha256
```

把导出的公钥按 “运维监控台（可选）”一节「在运维电脑上信任证书」的步骤安装到每台运维电脑。证书有效期三年；在到期前 30 天内重启 gateway 容器即可
重新签发。`OPS_GATEWAY_IP`、`OPS_GATEWAY_DNS` 改变后，重建 gateway 容器即可：入口脚本按 SAN 的哈希发现
变化，会自动重新签发；证书一旦重新签发，都要重新分发给运维电脑。删除证书卷只用于强制重签（例如网络配置改变但
SAN 没变），做法是：

```bash
docker compose -f docker-compose.yml -f docker-compose.ops.yml down
docker volume rm remotedesktop_ops-gateway-data
docker compose -f docker-compose.yml -f docker-compose.ops.yml up -d
```

首次登录用刚建的管理员（口令 + TOTP）。两个节点由一次性服务 `ops-seed` 在监控台启动前登记：`ops` 依赖它成功退出
（`service_completed_successfully`），所以登记失败时监控台不会启动，而不是带着空节点表上线。登记是幂等的，节点表里已有行时
不做任何修改（操作员删掉的节点不会被补回）。`ops-seed` 与 `ops` 使用同一镜像与同一份环境，因此写入的是监控台将要使用的
PostgreSQL；镜像的 `ENTRYPOINT` 就是监控台本身，`ops` 不再带 `command:`，`run --rm -it ops --create-admin admin` 的参数
直接交给监控台的安装命令。默认 baseUrl 为：

```text
rendezvous   http://rendezvous:5080
relay        http://relay:5083
```

可用 `OPS_RENDEZVOUS_NODE_URL` / `OPS_RELAY_NODE_URL` 覆盖；用 `docker-compose.selfsigned.yml` 时把前者设为
`http://rendezvous:5082`（内部明文口，见上一节）。因为 Compose 网络是隔离的，overlay 同时显式开启
`Ops:AllowInsecureNodeHttp`，允许节点令牌在该内部 HTTP 网络上传输；这仍是默认关闭的公网安全边界。

`OPS_INGEST_KEY` 是节点与监控台共享的 HMAC 密钥（Base64，至少 32 字节），不设置 overlay 不会启动。Ops 只信任
`OPS_GATEWAY_SUBNET`（默认 `172.30.0.0/24`）上的 Gateway，这个专用网络只有 Ops 和 Gateway 加入；不要把它换成整个
Docker bridge 网段，否则其他容器可以伪造 `X-Forwarded-For`。会话、告警与审计数据存在 PostgreSQL（与设备注册表
同一个库，表名以 `ops_` 开头）；备份 `postgres-data` 或 `pg_dump` 一次覆盖两者。监控台的 DataProtection 键存在
`ops-dataprotection` 卷里，Gateway 证书存在 `ops-gateway-data` 卷里，容器重建不会丢。`Ops:RequireTotp` 默认开启，别关。

#### 数据持久化与备份

- 默认把设备码存在 PostgreSQL（`postgres-data` 卷）。容器重建、镜像升级不会改变设备码。
- 备份：`docker compose exec -T postgres pg_dump -U remotedesktop remotedesktop > remotedesktop-$(date +%F).sql`。
- `docker compose down -v` 会连数据卷一起删掉；不要在不确认备份时加 `-v`。
- 监控台的数据也在 PostgreSQL；备份 `postgres-data`（或 `pg_dump`）就同时覆盖设备码、监控历史与审计。它不影响
  远程连接。
- **单机可以不用 PostgreSQL**：设置 `Rendezvous__Database__Provider=sqlite`、
  `Rendezvous__Database__Path=/data/rendezvous.db`，给 `rendezvous` 挂一个 `/data` 卷，并删掉 `postgres` 服务、
  `Rendezvous__Database__ConnectionString` 与 `depends_on`。镜像里的 `/data` 属于运行用户（UID 1654），新建的
  命名卷会继承它，所以直接可用。**不要**同时起两个 Rendezvous 容器连 SQLite——两个文件会给同一台设备发不同的码。
  这个做法**与监控台 overlay 不兼容**：`docker-compose.ops.yml` 里 `ops` 的数据库是 PostgreSQL（`Ops__Database__*`）
  并且 `depends_on` 了 `postgres`。要同时用两者，就保留 PostgreSQL；只想要 SQLite，则不加载 ops overlay，或自己改
  ops 的 `Ops__Database__*` 并去掉它的 `depends_on: postgres`。
- PostgreSQL 与 SQLite 之间切换会重发所有设备码，除非按“数据库怎么选”一节的步骤搬数据。

#### 健康检查与日志

```bash
curl http://127.0.0.1:5080/health      # 默认只绑回环；TLS overlay 走 https://<域名>/health
docker compose ps                      # 各服务都应显示 healthy
```

健康检查只探端口；数据库通不通看 `/health` 的返回（rendezvous 的 `/health` 会查库）。所有服务都配了
json-file 日志轮转（10 MB × 5）；更长的日志交给宿主机或日志驱动。

#### 升级

```bash
git pull
docker compose up -d --build
```

用了 TLS 或监控台 overlay 的话，把同样的 `-f` 参数带上。设备码在数据卷里，不受影响。TLS 证书在 `caddy-data` 卷里，Caddy 自动续期。升级前先看 `CHANGELOG.md` 的
“不兼容变更”；配置里新增的必填项会在 `docker compose up` 时报出来。基础镜像用浮动 tag（`10.0`、`postgres:18`、
`caddy:2`），Dependabot 会跟踪更新；需要可复现构建时把它们换成 digest。compose 里的内存上限是“公网上的限额”
那组默认值的起点，调大 `MaxConnections` 之类时一起调。

### 8. 防火墙

整套服务只用下面这几个端口，本文其他地方提到端口时以这张表为准：

| 端口 | 协议 | 用途 | 是否需要对外开放 |
|---|---|---|---|
| 5080 | TCP | 信令（`/ws`）与 `/health`，即 `Urls` 的默认端口 | 是：客户端与被控端都连它；前面放了反向代理或 Caddy 时，对外开放的是代理的 443，5080 本身只让代理连 |
| 5081 | UDP | 地址反射（`Rendezvous:ReflectorPort`） | 是，必须：否则直连打洞拿不到自己的公网地址 |
| 5082 | UDP | 中继（`Relay:Port`） | 是，必须：否则直连失败的连接没有退路 |
| 5443 | TCP | Ops HTTPS 网关（`Gateway:Listen`） | 只在要从别的电脑访问监控台时开放 |
| 5083 | TCP | 中继的 HTTP 面（`Relay:HttpEndpoint`：`/health`、`/metrics`） | 否：默认只在回环，仅供监控台抓取 |
| 5090 | TCP | 监控台自身（`Ops:Listen`） | 否：只在回环，对外只经过 Gateway 的 5443 |

改了端口，防火墙规则要跟着改。下面是 Windows 手工部署时的开放命令：

```powershell
netsh advfirewall firewall add rule name="RemoteDesktop Rendezvous" dir=in action=allow protocol=TCP localport=5080
netsh advfirewall firewall add rule name="RemoteDesktop Reflect"    dir=in action=allow protocol=UDP localport=5081
netsh advfirewall firewall add rule name="RemoteDesktop Relay"      dir=in action=allow protocol=UDP localport=5082
# 只有要从别的电脑访问监控台（Ops HTTPS 网关）时才需要：
netsh advfirewall firewall add rule name="RemoteDesktop Ops Gateway" dir=in action=allow protocol=TCP localport=5443
```

### 9. 健康检查

```bash
# Windows 包默认只在回环上走 HTTP；macOS 包在没有任何答案时才是回环 HTTP
# （安装面板里 TLS 选「不使用」会绑 http://0.0.0.0:5080，见 “服务端安装包（三平台）”一节 的 macOS 一节）：
curl http://127.0.0.1:5080/health      # 或浏览器打开，返回 200 即正常（不需要认证）
# Linux 包默认自签 HTTPS（RD_TLS=generate），对自签证书要加 -k；
# 启用了 TLS 的 Windows / macOS 包同样用这条：
curl -k https://127.0.0.1:5080/health
```

**监控指标（可选，默认关闭）**：把 `Rendezvous:MetricsEnabled` 设为 `true` 后，服务器会额外挂载 `/metrics`，
按 Prometheus 文本格式（`text/plain; version=0.0.4`，指标名都以 `remotedesktop_` 开头）给出：当前连接数、
在线设备数（瞬时值），以及注册、连接请求、在线状态查询各自被接受和被限流的次数（`*_total`，只增不减，适合
`rate()` 出图）。

默认关闭，而且关闭时这个路径**根本没有挂载**，访问返回 404：这些数字本身就说明了这台服务器上现在有多少台
设备、拒绝了多少请求，是运营者自己的信息，所以打开是一个有意的动作。打开后 `/metrics` 仍受 `NodeToken` 保护
（与 `/internal/*` 同一套规则）：没配 `Rendezvous:NodeToken` 时只接受回环调用，配了就要求
`Authorization: Bearer <NodeToken>`。因此 Prometheus 在本机抓取时不用带凭据；从别的机器抓取则必须配 token，
并建议再用防火墙只放行监控的来源地址。反向代理只转发 `/ws` 和 `/health` 时，`/metrics` 自然到不了公网。

```yaml
scrape_configs:
  - job_name: remotedesktop
    # 非本机的 Prometheus 才需要下面两行；token 与 Rendezvous:NodeToken 同值
    # authorization:
    #   credentials: "<NodeToken>"
    static_configs:
      - targets: ["127.0.0.1:5080"]
```

### 10. 运维监控台（可选）

监控台（`RemoteDesktop.Server.Ops`）是独立于信令与中继的第三个服务，用来**看**数字和**管**设备与会话。不装它，
远程桌面照常工作。监控台自身默认**只监听 `127.0.0.1:5090`**。三平台服务端安装包同时安装
`RemoteDesktop.Server.Gateway`，默认监听 `https://0.0.0.0:5443` 并转发到该回环地址；它使用包含本机 IPv4 IP SAN
的自签名证书，所以没有域名也能直接通过 `https://<服务器IP>:5443` 访问。运维电脑只需一次性信任安装包导出的
证书公钥。不要把 Ops 的 5090 本身对外开放。

#### 启动

本地（开发或试用）：

```bash
# 开发机没有安装包时，显式指向仓库里的设置目录；不传时命令找的是安装后的位置（Linux /etc，Windows ProgramData）
dotnet run --project src/Server/RemoteDesktop.Server.Ops -- --create-admin admin \
  --config-directory src/Server/RemoteDesktop.Server.Ops
dotnet run --project src/Server/RemoteDesktop.Server.Ops                          # http://127.0.0.1:5090
```

随**服务端安装包**一起装，三个平台都是：Linux 的 `.deb` 带 `remotedesktop-ops.service` 和
`remotedesktop-gateway.service`（安装时选了 PostgreSQL 就用同一个库，否则在
`/var/lib/remotedesktop-server/ops.db`），macOS 的 `.pkg` 带 `com.remotedesktop.ops` 与
`com.remotedesktop.gateway` LaunchDaemon（库在 `/usr/local/remotedesktop-server/var/ops.db`），Windows 的
MSI 带 `RemoteDesktopOps` 和 `RemoteDesktopGateway` 服务（库在
`%ProgramData%\RemoteDesktopServer\ops.db`）。它不需要单独安装，也没有必须填写的安装属性。

```bash
# Linux — the console keeps its settings in /etc, not next to the program
sudo -u remotedesktop /usr/lib/remotedesktop-server/Ops/RemoteDesktop.Server.Ops \
  --create-admin admin --config-directory /etc/remotedesktop-server/Ops
sudo systemctl restart remotedesktop-ops

# macOS — 以服务账号运行：数据库与它的 -wal/-shm 属于该账号，用 root 建会让监控台之后写不进去
sudo -u _remotedesktop /usr/local/remotedesktop-server/Ops/RemoteDesktop.Server.Ops --create-admin admin
sudo launchctl kickstart -k system/com.remotedesktop.ops
```

```powershell
# Windows（管理员 PowerShell）
& "C:\Program Files\RemoteDesktopServer\Ops\RemoteDesktop.Server.Ops.exe" --create-admin admin
Restart-Service RemoteDesktopOps
```

Windows 这两条命令都要在“管理员 PowerShell”里执行：管理员命令要读写 `%ProgramData%` 下只有
SYSTEM/Administrators 能访问的配置和数据库，`Restart-Service` 也需要管理员令牌；普通窗口会报
“无法打开计算机“.”上的 RemoteDesktopOps 服务”。命令默认读写
`%ProgramData%\RemoteDesktopServer\Ops\appsettings.json`，和服务是同一份配置；需要指到别处时加
`--config-directory`。

Docker 用可选的 ops overlay，建管理员的命令见 “用 Docker 部署（Linux 服务器首选）”一节 的"监控台（可选）"。

#### 首个管理员

没有默认口令、没有网页初始化入口。`--create-admin <用户名>` 交互式设口令（至少 12 位），打印 TOTP 密钥与 `otpauth://` URI，
导入验证器即可。**密钥只显示这一次**；丢失后用 `--reset-totp <用户名>` 重置（或让另一位管理员在监控台重置），
不需要再建一个账号。口令之后还要过 TOTP 二次验证才能登录（`Ops:RequireTotp=true` 默认开着，别关）。

#### 账号管理

管理员登录后侧栏有「账号管理」页，可以新建 `viewer` / `operator` / `admin`、改口令、重置 TOTP、停用/启用和删除。
**每一种改密、重置或停用都会立刻撤销该账号的所有会话**，下一个请求就要重新登录。最后一个可用的管理员不能被停用
或删除（否则就没有人能再登录）；管理员也不能停用或删除自己。所有变更都写审计。

监控台进不去时，命令行做同样的事：

```bash
RemoteDesktop.Server.Ops --reset-password <用户名> [--config-directory <目录>]
RemoteDesktop.Server.Ops --reset-totp <用户名> [--config-directory <目录>]
```

两条命令都会撤销该账号的现有会话。`--create-admin`、`--reset-password`、`--reset-totp` 默认读服务的那份
`appsettings.json`（Windows `%ProgramData%\RemoteDesktopServer\Ops`，Linux
`/etc/remotedesktop-server/Ops`，macOS 安装根下的 `Ops`）；显式传入的 `--config-directory` 不存在时会直接
说明，而不是落到另一个数据库。容器没有 `/etc` 副本时回退到程序目录（compose 的数据库配置来自环境变量）。

#### 关键配置（`Ops` 段）

| 配置项 | 默认 | 说明 |
|---|---|---|
| `Ops:Listen` | `http://127.0.0.1:5090` | 监控台自身的 Kestrel 监听地址；保持回环，对外只经过 Gateway。配置步骤（`--configure-system`）会把 Gateway 的转发目标同步成它的回环形式；手工改了这个端口后要重新运行配置步骤，否则 Gateway 仍转发到旧端口 |
| `Gateway:Listen` | `https://0.0.0.0:5443` | 安装包内置的 HTTPS Gateway 监听地址；证书自动生成并包含本机 IPv4 IP SAN |
| `GATEWAYHOSTNAME` / `--gateway-hostname` | 空 | 安装参数：给生成证书追加 IP/DNS；本机 IPv4 已自动包含 |
| `Ops:Database:Provider` / `Path` | `sqlite` / `ops.db` | 监控台自己的库；换成 `postgres` 时填 `ConnectionString`。Docker 的 ops overlay 用 PostgreSQL，与设备注册表同库 |
| `Ops:RequireTotp` | `true` | 口令之后强制 TOTP |
| `Ops:NodeToken` | 空 | 监控台调节点内部 API 的 Bearer；空 = 节点只接受回环调用。必须与 `Rendezvous:NodeToken`、`Relay:NodeToken` 同值（规则见 “用 Docker 部署（Linux 服务器首选）”一节"内部 API 与 NODE_TOKEN"）。**节点自己的令牌优先**（见「每节点令牌」），这个键是没设独立令牌的节点的默认值 |
| `Ops:AllowInsecureNodeHttp` | `false` | 允许节点令牌经非回环明文 HTTP 发送；仅用于 Compose 等隔离网络，公网节点保持关闭 |
| `Ops:NodeTokenHosts` | 空 | 节点令牌只允许发往这些主机（裸主机名或 IP，不带协议/端口/路径；回环始终允许）。空 = 沿用"https 或回环"的规则。节点地址由管理员在监控台填写，填了这个列表后，即使管理员会话被拿走也无法把令牌引到列表外的地址 |
| `Ops:NodeCertificatePath` | 安装包在 TLS 时写入 | 调节点时只信任这张公钥证书，并跳过主机名校验：三个平台的服务端安装包在信令用 TLS 时都会写入本机信令公钥（本机节点用 `127.0.0.1`，证书未必包含它；Linux 上选 `existing` 并给 PKCS#12 文件时没有可固定的公钥，不写）。这是全局设置——登记了用另一张证书的远程节点时，删掉此键改走系统信任库 |
| `Ops:SessionLifetime` | `08:00:00` | 管理员会话的有效期（TimeSpan，必须大于 0） |
| `Ops:ScrapeIntervalSeconds` | `10` | 多久抓一次节点指标 |
| `Ops:ScrapeTimeoutSeconds` | `5` | 单次抓取的超时 |
| `Ops:AlertIntervalSeconds` | `15` | 多久求值一次告警规则。配置校验只要求至少 1，但实际最小间隔是 5 秒（更小的值按 5 秒处理） |
| `Ops:ActionStaleSeconds` | `60` | 处于 `approved` 超过这个秒数的动作由恢复任务标记失败并写审计；进程在批准与执行之间崩溃时不会永久卡住 |
| `Ops:Notifications:WebhookUrl` | 空 | 告警开/恢复各 POST 一次 JSON；空 = 不通知。失败后按 1/2/4/8/16 分钟退避重试，最多 5 次 |
| `Ops:Notifications:Smtp:Host` | 空 | 邮件投递的 SMTP 服务器；空 = 不发邮件。配了它就要给 `From`、`To`，否则启动时按配置错误拒绝 |
| `Ops:Notifications:Smtp:Port` / `UseStartTls` | `587` / `true` | 提交端口与 STARTTLS。不支持 465 的隐式 TLS：那种部署请在前置一个本机中继 |
| `Ops:Notifications:Smtp:From` / `To` | 空 | 信封与头部的发件人；收件人可用逗号或分号分隔多个 |
| `Ops:Notifications:Smtp:Username` / `Password` | 空 | 中继需要认证时填写；支持 AUTH PLAIN 与 AUTH LOGIN。**口令只写进 AUTH 交换**，不进日志、不进审计、不进投递记录 |
| `Ops:TrustedProxies` | 空 | 可信反向代理的 IP 或 CIDR 列表；只有这里的代理写来的 `X-Forwarded-For` / `X-Forwarded-Proto` 会被采信。见“反向代理” |
| `Ops:Retention:SampleDays` / `RollupDays` / `EventDays` / `SessionDays` / `AlertDays` | `30` / `365` / `30` / `90` / `180` | 原始指标样本、1 分钟/1 小时降采样指标桶、事件、会话、已恢复告警的保留天数；`0` 关闭对应清理。`RollupDays` 默认比原始样本长，以较低粒度保留趋势；审计日志不自动删除（见"数据保留"） |
| `Ops:Retention:IntervalMinutes` | `60` | 清理任务间隔 |
| `Ops:Ingest:Keys:<id>` | 空 | 节点推事件的 HMAC 密钥（Base64）；见下 |

> 内部 API 的 token 不是 `Ops` 段独有的：监控台读 `Ops:NodeToken`，信令读 `Rendezvous:NodeToken`，中继读
> `Relay:NodeToken`，三处必须同值，规则见 “用 Docker 部署（Linux 服务器首选）”一节 的"内部 API 与 NODE_TOKEN"。

#### 每节点令牌（可选）

部署级 `Ops:NodeToken` 的问题是它同时是每台节点的密钥：任何一台节点泄露它，等于泄露全部。监控台允许给单个
节点配自己的令牌——**节点自己的令牌优先，没设的节点继续用 `Ops:NodeToken`**，所以升级不会改变任何现有行为。

设置方式：节点管理 → 某节点「详情」→「生成独立令牌」（`POST /api/nodes/{id}/token`，管理员动作，写审计）。
监控台只会显示这一次——没有读取接口，页面拿不回已存的令牌。把显示出来的值填到那台节点的
`Rendezvous:NodeToken` 或 `Relay:NodeToken`，重启该节点，抓取随即改用新令牌。

- **重置**会让旧令牌立刻失效：在节点改好配置并重启之前，该节点会显示「抓取失败」并在节点详情里给出 401。
- **清除**（`DELETE /api/nodes/{id}/token`）把节点改回部署级令牌。
- 出口规则不变：`Ops:NodeTokenHosts` 与「https 或回环」对**每一条**令牌都生效，独立令牌不是把密钥发到别处的许可。
- 令牌以明文存在监控台的库里（与 `Ops:NodeToken` 存在于配置文件同理）。监控台代码本身不设库文件的权限：Linux 靠 0700 的数据库目录，macOS 的 SQLite 文件按进程 umask 创建、靠 0700 的
`var` 目录保护，Docker overlay 的库是 PostgreSQL。备份与只读副本要按密钥对待。

#### 反向代理

服务端安装包自带一个专用 Gateway：它只监听 HTTPS，固定转发到监控台的回环地址（默认 `http://127.0.0.1:5090`，随 `Ops:Listen` 的端口变化），覆盖客户端伪造的
`X-Forwarded-*` 头，并把 `127.0.0.1` 写入 `Ops:TrustedProxies`。Windows 的 MSI 会为 TCP 5443 添加规则；
Linux 的 `/etc/default/remotedesktop-server` 用 `RD_GATEWAY_LISTEN`、`RD_GATEWAY_HOSTNAME` 配置，并在
`RD_OPEN_FIREWALL=1` 时放行 5443；macOS 的 `configure-server.sh` 使用 `--gateway-listen` 和
`--gateway-hostname`。

自签名证书公钥的位置：

- Windows：`%ProgramData%\RemoteDesktopServer\tls\signaling.cer`
- Linux：`/etc/remotedesktop-server/tls/signaling.crt`
- macOS：`/usr/local/share/doc/remotedesktop-server/ops-gateway.crt`

每台运维电脑都必须信任该证书；不能通过忽略浏览器警告代替。服务器 IP 改变后要重新生成并分发证书。
安装后的副本也可在这些位置查看：Linux 为 `/usr/share/doc/remotedesktop-server/how-to-configure.txt`，
Windows 为安装目录下的 `how-to-configure.txt`，macOS 为
`/usr/local/remotedesktop-server/how-to-configure.txt`。

##### 在运维电脑上信任证书

先从服务器把公钥复制到运维电脑。Linux 服务器上配置目录只有 root 和服务器服务组可读，
所以先在服务器上复制一份可下载的公钥，再在运维电脑上下载：

```bash
# 服务器上执行
sudo install -m 0644 /etc/remotedesktop-server/tls/signaling.crt /tmp/ops-gateway.crt

# 运维电脑上执行；完成后可删除服务器上的 /tmp/ops-gateway.crt
scp <管理员账号>@<服务器IP>:/tmp/ops-gateway.crt ./ops-gateway.crt
```

macOS 服务器使用：

```bash
scp <管理员>@<服务器IP>:/usr/local/share/doc/remotedesktop-server/ops-gateway.crt ./ops-gateway.crt
```

Windows 服务器可在有管理员权限的 PowerShell 中从管理共享复制：

```powershell
Copy-Item '\\<服务器>\C$\ProgramData\RemoteDesktopServer\tls\signaling.cer' .\ops-gateway.cer
```

导入前应通过可信渠道核对服务器上证书的 SHA-256 指纹。例如 Linux/macOS 服务器执行：

```bash
openssl x509 -in <证书路径> -noout -fingerprint -sha256
```

**Windows 运维电脑**（管理员 PowerShell，写入本机受信任根，Chrome/Edge 会使用）：

```powershell
Import-Certificate -FilePath .\ops-gateway.cer -CertStoreLocation Cert:\LocalMachine\Root
```

**Linux 运维电脑**（Debian/Ubuntu）：

```bash
sudo cp ops-gateway.crt /usr/local/share/ca-certificates/remotedesktop-ops-gateway.crt
sudo update-ca-certificates
```

Fedora/RHEL：

```bash
sudo cp ops-gateway.crt /etc/pki/ca-trust/source/anchors/remotedesktop-ops-gateway.crt
sudo update-ca-trust
```

Linux 上的 Chromium/Chrome 如果仍不读取系统信任库，需要导入当前用户的 NSS 库（先安装 `libnss3-tools`）：

```bash
# 若数据库尚不存在，先初始化（可留空密码）：
#   mkdir -p "$HOME/.pki/nssdb" && certutil -d sql:"$HOME/.pki/nssdb" -N
# 该证书是自签名叶子证书（BasicConstraints CA:FALSE），必须用受信任对端标志 "P,," 直接信任它；
# 用 "C,,"（信任为 CA）会被 NSS 拒绝，报 "issuer is not recognized"。
certutil -d sql:"$HOME/.pki/nssdb" -A -t "P,," \
  -n "Remote Desktop Ops Gateway" -i ops-gateway.crt
```

**macOS 运维电脑**（写入系统钥匙串，Safari/Chrome 会使用）：

```bash
sudo security add-trusted-cert -d -r trustRoot \
  -k /Library/Keychains/System.keychain ops-gateway.crt
```

也可以在“钥匙串访问”中导入证书，打开证书详情的“信任”，把“使用此证书时”设为“始终信任”。

**Firefox 使用独立的信任库**，上述系统导入通常不会生效。在
“设置 → 隐私与安全 → 证书 → 查看证书 → 证书颁发机构 → 导入”中选择证书，并勾选
“信任由此证书颁发机构来标识网站”。

导入后关闭并重新打开浏览器，访问 `https://<服务器IP>:5443`。地址栏应显示正常的锁标识且没有证书警告。
服务器 IP 变化或证书到期重新生成后，应删除旧信任并重新导入新证书。把自签名证书加入受信任根意味着
**持有对应私钥的人能在这台电脑上冒充该证书覆盖的服务**，因此证书私钥只能留在服务器上，分发时必须核对指纹。

如果部署不使用内置 Gateway，监控台仍可只监听回环，远程访问用 SSH 隧道或自行维护的 TLS 反向代理。用外部代理时
**必须**把代理地址写进 `Ops:TrustedProxies`，并让代理写 `X-Forwarded-For` 与 `X-Forwarded-Proto`：

```json
"Ops": { "TrustedProxies": [ "127.0.0.1" ] }
```

不配时，监控台看到的是代理的地址：审计日志和登录失败预算都会按代理计，TLS 终止在代理时
`Request.IsHttps` 为 false，会话 Cookie 也不会带 `Secure`。框架自带的“相信回环代理”默认被移除——本机任何
进程都能连回环，不能因为连接来自 `127.0.0.1` 就相信它写的头。

`/health` 只给本机用：连接必须来自回环，且请求不能带任何转发头。反向代理**不要**转发 `/health`；即使转了，
带 `X-Forwarded-For` / `Forwarded` 的请求也会得到 404。

#### 数据保留

监控台每小时清理一次过期历史（保留天数和关闭方式见上面配置表里的 `Ops:Retention:*`）：原始指标样本默认 30 天，
1 分钟/1 小时降采样指标桶默认 365 天，事件、会话与已恢复告警按各自配置清理；并同时删除过期的管理员会话。
打开中的告警永远不会被删除。
**审计日志（`ops_audit_log`）是有哈希链的，不自动删除**——需要更长的保留期就备份数据库，或定期整库归档。

#### 让总览出现数字：三件事缺一不可

1. **节点能上报指标**：信令服务设 `Rendezvous:MetricsEnabled=true`；中继默认就有 `/metrics`。Docker 的 ops overlay 已为信令打开。
2. **监控台里登记节点**：原生安装包会自动登记本机节点（信令 `http://127.0.0.1:5080`、中继 `http://127.0.0.1:5083`）；Docker overlay 也会在启动前自动登记服务名节点。手工部署在节点管理页添加即可。
3. **事件通道**：信令/中继把会话事件推给监控台，三处要同一个 HMAC 密钥。原生 Windows / Linux / macOS
   Server 安装包会自动生成 32 字节密钥并写好三处配置；升级会补齐旧安装缺少的配置，但不会轮换已有密钥：

   ```text
   # 信令服务、中继服务的 appsettings.json（或环境变量）
   "OpsEvents": { "Endpoint": "http://127.0.0.1:5090", "Key": "<自动生成的 Base64>", "KeyId": "k1" }
   # 监控台
   "Ops": { "Ingest": { "Keys": { "k1": "<同一个 Base64>" } } }
   ```

   Docker 的 ops overlay 用环境变量 `OPS_INGEST_KEY` 配这一组值，两边的 Endpoint 是 `http://ops:5090`。
   额外安装在其他机器上的 Relay 必须显式使用中央 Ops 的地址和同一密钥，可通过 `--ops-events-endpoint` 与
   `RD_OPS_INGEST_KEY` 传给 `--configure-system`；不能让那台机器自行生成另一把密钥。
   密钥不配，监控台收不到任何事件——`/ingest/events` 返回 503，而不是收到伪造数据。会话、链路、告警、心跳
   全是这条通道进来的；只有节点指标走抓取那条。

#### 管理动作与审计

- **断开会话 / 封禁设备**要两个人：一个发起、一个批准，发起人不能批准自己的动作；两步各留一条审计。
- 断开会话需要被控端开着 `Telemetry:Enabled`（否则监控台不知道被控端所知的会话 id）。
- 审计日志是哈希链，`审计日志 → 校验链` 能指出第一条被改动的记录。
- 构成与设计见 [系统设计](../design/README.md) 的"运维监控台"一节。

## 三、自动化发布到 GitHub Releases

项目已经配置好了 GitHub Actions 自动化流水线 (`.github/workflows/release.yml`)，当您推送形如 `vX.Y.Z` 的 Tag（例如 `v1.0.0`）时，云端会自动跑测试、打包，并把安装包放进同一个 Release——三个平台各出三个角色，装的机器不同，版本是同一个。**配置了 macOS 签名与公证凭据时是九个；缺这些凭据时 macOS 整组不构建，本次发布只有 Windows 与 Linux 的六个**，发布说明会把这一点写在最上面。

同一个 `<Version>` 在三个平台上的最终形态不同，这是安装包格式决定的，不是漂移：

| 平台 | 包内版本 | 构建产物名 | 发布资产名 |
|---|---|---|---|
| Windows | `1.0.0.0`（三段 + `.0`，MSI 只认四段数字） | `RemoteDesktop-Client-Windows-x64-1.0.0.0.msi` | `RemoteDesktop-Client-Windows-x64-v1.0.0.msi` |
| macOS | `1.0.0`（`CFBundleShortVersionString`、`CFBundleVersion`、`pkgbuild --version` 同值） | `RemoteDesktop-Client-macOS-arm64-1.0.0.pkg` | `RemoteDesktop-Client-macOS-arm64-v1.0.0.pkg` |
| Linux | `1.0.0`（`control` 的 `Version`） | `RemoteDesktop-Client-Linux-amd64-1.0.0.deb` | `RemoteDesktop-Client-Linux-amd64-v1.0.0.deb` |

三段的源只有 `Directory.Build.props`；Windows 的第四段由打包脚本追加，macOS/Linux 不改写。发布资产名用 Tag，所以包内版本与资产名不会逐字相同。`1.0.0-rc.1` 这类后缀不受支持：MSI 装不下，macOS 的 bundle version 也不接受。Linux 的 `Version` 不带 Debian revision（是 `1.0.0`，不是 `1.0.0-1`）：这是自发布包；若将来由发行版重打包，revision 由打包者追加。Windows 文件属性里的“产品版本”也是三段；提交哈希只出现在遥测的 `Full` 里（`AssemblyMetadata("Commit")`），不在文件元数据里。

| 角色 | 装的是什么 |
|---|---|
| 控制端 | Windows 的客户端包也装被控端服务与会话 Agent（一台机器两种角色都要时用它）；macOS 是同一个 `.app`（也能被控，但不会自动启动）；Linux 只装客户端 |
| 被控端 | Windows 只有服务与会话 Agent，没有客户端窗口；macOS 是独立的 `Remote Desktop Host.app`（无窗口，装在 `/Library/Application Support/RemoteDesktop/Host`，不出现在“应用程序”）+ 登录项；Linux 是 HostService |
| 服务端 | Rendezvous + Relay + Ops + Gateway |

最多九个资产的完整名单、命名约定（角色词一律写全，架构词各平台用自己的说法，Linux 那个与包内 `Architecture`
字段同源）见 [`CONTRIBUTING.md`](../../CONTRIBUTING.md) 的“构建产物布局”一节；这里只写每个角色装什么，不再重列。

Release 还包含 `SHA256SUMS`，覆盖**本次实际发布的资产**（六个或九个）；下载后可用 `sha256sum -c SHA256SUMS`
或 PowerShell `Get-FileHash` 复核。
同一个平台上的包版本号相同。三个角色各自装在不同的机器上是常态；Windows 的客户端包与被控端包不能同机共存
（见 [安装](#2-安装)），而服务端包和它们互不影响。

发布前要准备好下面这些。服务器地址可选；**正式 tag 的 Windows 代码签名凭据是必填项**，缺失会使流水线失败而不是
降级；macOS 的签名与公证凭据可选，缺任意一项时 `check-signing` 会把 macOS 整组标为跳过：

- **服务器地址**（可选）：配了 `DEFAULT_RENDEZVOUS_URI`，客户端安装包就内置这个地址，下载的人装完直接可用；
  不配就是"不内置"，见"配置默认服务器地址"那一节。
- **代码签名证书**（必填）：`CODE_SIGN_CERT_BASE64` 与 `CODE_SIGN_CERT_PASSWORD` 缺一不可；未签名的 Agent 无法获得
  UIAccess，见"准备代码签名证书"那一节。
- **macOS 证书与公证**（可选）：配齐两张 `MACOS_*` 证书及三个公证 Secrets 时，`release.yml` 会构建、签名、公证并
  验证 `.pkg`；缺任意一项时 `build-macos` 不运行，本次只发布 Windows 与 Linux 的六个资产（发布说明会标注）。
  具体是哪几个、怎么准备，见
  [build/signing/README.md](../../build/signing/README.md#github-secrets-与变量) 的「GitHub Secrets 与变量」。
- **许可文本**：仓库根的 `LICENSE` 已经是填好的正式文本，不需要动。只有里面重新出现【方括号】占位符时
  打包脚本才会拒绝出包——那道闸门是留给"换个人/换主体来发布"的，构建日志会列出是哪几处。

### 1. 准备代码签名证书 (非常重要)

为了突破 Windows 的 UIPI 限制（例如远程操作任务管理器时不卡鼠标），本程序的 `SessionAgent` 开启了 `UIAccess` 权限。这意味着发布出去的 MSI 及可执行文件 **必须被有效签名** 才能获得 Windows 安全中心放行，否则在 UAC 或高权限窗口下将无法注入鼠标键盘。

请在 GitHub 仓库中配置证书 Secrets（如果没有商业证书，也可使用自签名代码证书测试）：

1. 导出您的代码签名证书为 PKCS#12 格式 (`.pfx`)。
2. 在本地使用 PowerShell 将其转为 Base64：

   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) | Set-Clipboard
   ```

3. 在您的 GitHub Repository -> **Settings** -> **Secrets and variables** -> **Actions** 中添加：
   - `CODE_SIGN_CERT_BASE64` = 刚刚复制的 Base64 字符串
   - `CODE_SIGN_CERT_PASSWORD` = 您的证书密码

*正式 Release 缺少其中任何一项都会失败；本地测试包仍可用打包脚本的显式 `-SkipSigning` 选项。*

配置好之后，流水线用这个 `.pfx` 给客户端的 exe/dll 和 MSI 签名。自签名证书还会随 MSI 装进目标机器的受信任根
（UIAccess 要求签名链能被本机信任），机制见
[build/signing/README.md](../../build/signing/README.md#用安装包装的机器不用手工装-cer)。

### 2. 配置默认服务器地址（可选）

在 GitHub Repository -> **Settings** -> **Secrets and variables** -> **Actions** -> **Variables** 标签页下，新建一个变量：

- `DEFAULT_RENDEZVOUS_URI` = `wss://您的域名:5080/ws`

配了：这个地址会被编进客户端安装包，下载的人装完就能连上你的服务器，不用自己填。

**不配也完全可以发布**：出的包不内置地址（客户端默认连本机），装完后在客户端的「高级设置 → 服务器与托管」里点「修改」，
填自己的服务器地址再点「保存并重启生效」就行（被控端以后台服务托管时可用）——地址会交给被控端服务写入并重启它，
客户端随后重启。客户端连不上时界面会直接显示"○ 未连接服务器
（当前地址）"以及去哪儿改，所以不是一个不声不响的坏包。

本软件公网和内网都要用，服务器地址往往是**部署的时候**才定的，所以"不内置"是正常选择，不是漏配。
两种情况下 Release 说明的最前面都会写清楚这一版是哪种。

地址用 `wss://` 就要求服务器自己有 TLS 证书（见 “生产环境：信令必须走 TLS”一节）；局域网里用自签证书时，构建客户端安装包加
`-ServerCertificate` 把服务器证书的公钥打进包并装进受信任根，就不用每台机器手工导入。装机时选证书是另一套机制，
见 “安装”一节。

### 3. 升版本并触发自动发布

版本只有一处：仓库根 `Directory.Build.props` 的 `<Version>`。安装包（最多九个）、`.app` 的 `Info.plist` 与遥测都从它派生，
所以发布前**先改它，再打 Tag**：

1. 把 `Directory.Build.props` 里的 `<Version>` 改成这次要发的三段数字（例如 `1.0.0`）。Windows Installer 只认
   `major.minor.build`，脚本会补成四段 `1.0.0.0`；`1.0.0-rc.1` 这类版本拒绝构建，所以**预发布也只能用数值版本**。
2. 在 `CHANGELOG.md` 里把 `[未发布]` 的各小节合并成 `## [1.0.0] - YYYY-MM-DD`（没有内容就补一节）。
3. 提交这两处改动。
4. 打 Tag 并推送：

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

流水线第一步 `check-version` 会核对 Tag 与 `<Version>`、以及 `CHANGELOG.md` 里有没有对应小节；任何一处不一致
都会在打包前失败。之后 GitHub Actions 拉三个平台各跑测试、打包，几分钟后您的 Releases 页面会出现**最多九个**包
（macOS 凭据不全时为六个），名字统一是 `<产品>-<角色>-<平台>-<架构>-<tag>`（例如
`RemoteDesktop-Client-Windows-x64-v1.0.0.msi`）。

服务器地址可以在安装时填写；构建时给的 `-RendezvousUri` 只是安装页和静默安装的默认值。

## 四、部署客户端 / 被控端（MSI）

### 1. 构建安装包时给出默认服务器地址

构建方法见 [安装包怎么构建](#八安装包怎么构建三平台) 的 Windows 一节，**构建时最好给 `-RendezvousUri`**（不给时的默认值与警告也在那里）。
安装时可以在图形界面第一页改，或者给静默安装传 `RENDEZVOUSURI`。

### 2. 安装

下面的 `<版本>` 同样是四段版本（`Directory.Build.props` 的三段加 `.0`，例如 `1.0.0.0`），以实际产物文件名为准。
图形界面会先问服务器地址；地址是 `wss://` 且服务器使用自签名证书时，再选择它的公钥证书。host 包随后还会问无人值守密码。

```powershell
# 图形界面（推荐给一般用户）
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi

# 静默安装，明文 ws://（只允许回环地址）
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="ws://127.0.0.1:5080/ws"

# 静默安装，wss:// + 自签名服务器证书（PEM/DER 公钥）
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="wss://rd.example.com:5080/ws" SERVERCERTIFICATEPATH="C:\certs\signaling.cer"

# wss:// 使用公开 CA 签发的证书：不要传 SERVERCERTIFICATEPATH
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="wss://rd.example.com:5080/ws"

# 静默安装到别的目录
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn INSTALLFOLDER="D:\Apps\RemoteDesktop" RENDEZVOUSURI="wss://rd.example.com:5080/ws"
```

**`ws://` 只接受回环地址。** 给非回环地址（例如 `ws://192.168.1.10:5080/ws`）填明文，客户端/被控端
启动时会以 `Transport:RendezvousUri uses plain ws:// to non-loopback host …` 拒绝，MSI 则在启动服务那一步
失败并回滚。MSI 没有传 `AllowInsecureSignaling` 的属性；确实要在隔离实验网里用非回环明文，装完后改
`%ProgramData%\RemoteDesktop\appsettings.json` 里的 `Transport:AllowInsecureSignaling`，再重启服务。
生产环境用 `wss://`。

`SERVERCERTIFICATEPATH` 只在地址是 `wss://` 时使用，且应指向**公钥**（`.cer`/`.crt`/`.pem`/`.der`，PEM 或 DER）。
安装器只把公钥导出到 `%ProgramData%\RemoteDesktop\certificates\signaling.cer`，在
`%ProgramData%\RemoteDesktop\appsettings.json` 里写 `Transport:RendezvousCertificatePath`，**不会修改系统信任库**，
也不会把源文件里的私钥复制过去。地址是 `ws://` 时即使传了证书也不会导入；路径不是绝对路径、文件不存在或不是可读的
PEM/DER 证书时安装会失败。使用公开 CA 证书时留空即可，客户端走系统信任链。

这和构建期的 `-ServerCertificate`（“生产环境：信令必须走 TLS”一节）是两套机制：构建期是把服务器证书的公钥打进包，安装时装入这台机器的
"受信任的根证书颁发机构"，整台机器都信任它；安装期的 `SERVERCERTIFICATEPATH` 只导出公钥并固定到
`Transport:RendezvousCertificatePath`，不动系统信任库，范围更窄。

只做被控端、不需要客户端窗口的机器，装 `RemoteDesktop-Host-Windows-x64-<版本>.msi`：同样的服务与会话 Agent、同样的
`RENDEZVOUSURI`/`SERVERCERTIFICATEPATH` 属性，只是没有客户端与开始菜单项。它和客户端包装在同一个目录、用同一个
`RemoteDesktopHost` 服务与 `%ProgramData%\RemoteDesktop` 设备身份，所以**一台机器只装其中一个**：想同时具备
"控制别人"与"被别人控制"两种角色，只装**客户端**包就够了（它本来就带被控端）；Host 包是给"只能被控、不允许控制
别人"的机器用的。装第二个时安装程序会明确拒绝，先卸掉已有的那个再装。

host 没有安装后的窗口，所以**服务器地址、TLS 证书和无人值守密码都在安装程序里设置**：连接页问地址和证书，下一页问
密码。安装期设置对 **host** 尤其重要；之后改地址可以再运行安装程序（图形界面会在连接页带出当前值），或手工运行
`RemoteDesktop.HostService.exe --configure-system --rendezvous ...`，再重启服务。

```powershell
# host，明文（只允许回环地址；非回环明文见上面的说明）
msiexec /i RemoteDesktop-Host-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="ws://127.0.0.1:5080/ws"

# host，wss + 自签名服务器证书 + 无人值守密码
msiexec /i RemoteDesktop-Host-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="wss://rd.example.com:5080/ws" SERVERCERTIFICATEPATH="C:\certs\signaling.cer" FIXEDPASSWORD="<至少 8 位>"
```

安装完成后：

- 服务 `RemoteDesktopHost` 自动启动（LocalSystem），客户端在开始菜单里。
- 装完第一次打开客户端，会显示 9 位设备代码和临时密码——**两台机器互相输入对方的代码和密码即可连接**。
- 装安装包时会顺手打开"服务可以发送 Ctrl+Alt+Del"的策略位（只加这一位，并记住是自己加的），并添加一条**只针对本程序**的
  入站 UDP 防火墙规则——直连打洞要靠它，没有这条规则时所有连接都会绕道中继（能连上，但更慢）。

### 3. 已经装好了，但设备代码是空的

按顺序查：

1. **服务器地址对不对**：`C:\ProgramData\RemoteDesktop\appsettings.json` 里的 `Transport:RendezvousUri`。
   客户端界面在连不上时也会把它显示出来，并给出提示。自签名证书固定的路径是
   `Transport:RendezvousCertificatePath`，指向 `%ProgramData%\RemoteDesktop\certificates\signaling.cer`。
2. **服务器在不在**：在服务器上 `curl http://127.0.0.1:5080/health`，在客户端这台机器上
   `curl http://<服务器>:5080/health`（把地址换成 5080 端口的 http 形式）。连不上就是网络或防火墙问题。
3. **服务起来了吗**：`Get-Service RemoteDesktopHost`。显示已停止，或者客户端说"无法联系后台服务"，先看同目录下的
   `C:\ProgramData\RemoteDesktop\logs\startup-error.log` —— 配置读不出来时，服务会在启动前把原因写在这里并以
   退出码 3 结束，而不是留下一个没人看得懂的崩溃。客户端对应的文件是 `%AppData%\RemoteDesktop\crash.log`，
   同时会在屏幕上弹一个说明原因的对话框。
4. **改了配置要重启服务**：`Restart-Service RemoteDesktopHost`（配置只在启动时读一次）。
5. **看日志**：`C:\ProgramData\RemoteDesktop\logs\service-<日期>.log`。
   `Rendezvous connection lost: Unable to connect to the remote server` 就是在说"连不上信令服务器"。

### 4. 升级与卸载

```powershell
# 升级（保留设备身份、日志和 ProgramData 下的服务器地址）
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn

# 卸载（默认连同设备身份、日志和 ProgramData 下的地址一起删除）
msiexec /x RemoteDesktop-Client-Windows-x64-<版本>.msi /qn

# 卸载但保留设备身份与日志（重装后设备代码不变）
msiexec /x RemoteDesktop-Client-Windows-x64-<版本>.msi /qn REMOVEDATA=0
```

- **图形界面升级**：连接页会带出当前地址和已固定的证书，可以改；离开这一页后安装程序才会应用新值。
- **静默升级**：不运行界面，现有地址、证书和设备身份都保留；`RENDEZVOUSURI`/`SERVERCERTIFICATEPATH` 只在全新安装时
  或图形界面升级时生效。要换自签名证书（例如服务器证书续期后），用**新的安装包**（新版本或至少一次新的构建，具有新的
  ProductCode）的图形界面重新运行，在连接页选择新的公钥；同一个 MSI 的修复安装不会重新走连接页。也可以先卸载再用新参数安装。
- 卸载默认删除 `%ProgramData%\RemoteDesktop`（地址、证书固定和设备身份）；要保留它用 `REMOVEDATA=0`。
  客户端在 `%AppData%\RemoteDesktop` 下的身份与最近设备列表不归 MSI 管，卸载不会删。

**升级前最好先退出客户端**。安装包会在 `InstallValidate` 之前把残留的 `RemoteDesktop.Client.exe` 和
`RemoteDesktop.SessionAgent.exe` 关掉，所以正常升级是普通的文件替换；但客户端正在运行时，个别文件仍可能
被动等待重启替换。退出客户端再升级就没有这个问题：

```powershell
Get-Process RemoteDesktop.Client -ErrorAction SilentlyContinue | Stop-Process
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi
```

服务不用担心：安装包会先停服务，服务停止时会结束它启动的 Agent（实测 2 秒内退出）。
出问题时用日志装：

```powershell
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /l*v "$env:TEMP\rd-install.log"
```

安装失败时 Windows 也会在"应用程序"事件日志里记下来（来源 `MsiInstaller`，含具体错误码和失败的那一步）。

客户端自己的配置在 `%AppData%\RemoteDesktop`（每用户），MSI 不管它；重装系统或换用户后会在那边重建。

### 5. 签名

打包脚本会自动找证书并分两步签名（先程序、再 MSI）；用 `-SignCertificate <证书指纹>`（或 `"CN=你的公司"`）指定证书，
用 `-SkipSigning` 明确不签名。自动选证书的规则、不签名的后果、怎么买证书、有哪些替代方案、怎么检查签名，以及本地测试用的
自签名证书（`New-SigningCertificate.ps1` / `Install-TrustedCertificate.ps1`），见
[`build/signing/README.md`](../../build/signing/README.md)。

## 五、部署客户端 / 被控端（Debian 包，Linux）

### 1. 构建

控制端与被控端两个包各装各的：一台机器按需要选一个，或者两个都装。构建命令见
[安装包怎么构建](#八安装包怎么构建三平台) 的 Linux 一节：自包含发布，**目标机器不需要装 .NET**，也不需要 `LD_LIBRARY_PATH`。

### 2. 服务器地址与证书

**安装时会问**（`dpkg -i` 也会问）：信令服务器地址，形如 `wss://主机:5080/ws`，或本机回环的
`ws://127.0.0.1:5080/ws`；当地址是 `wss://` 且服务端用自签证书时，再问证书文件（PEM）。答案是：

- `ws://`：不需要证书；服务端也没开 TLS。**只接受回环地址**，填非回环地址（例如 `ws://192.168.1.10`）时
  程序启动会拒绝，并写 `startup-error.log`。
- `wss://` + 公共 CA 签的证书：证书问题留空。
- `wss://` + 自签证书：把服务端 `/etc/remotedesktop-server/tls/signaling.crt` 拷到本机，安装时填写
  它的路径；包把它装进 `/usr/local/share/ca-certificates/` 并运行 `update-ca-certificates`。

构包时仍可用 `RD_RENDEZVOUS_URI` / `RD_SERVER_CERTIFICATE` 把站点默认值烤进包（见“安装包怎么构建（三平台）”的 Linux 一节），安装问题会
以它为默认值。非交互安装可以预先写答案：

```sh
printf '%s\n' \
  'remotedesktop-client remotedesktop-client/server-address string wss://rd.example.com:5080/ws' \
  'remotedesktop-client remotedesktop-client/server-certificate string /root/signaling.crt' \
  | debconf-set-selections
```

已安装的包用 `dpkg-reconfigure` 改答案（`/etc/default/remotedesktop-{client,host}` 只在安装提问时作为
预置值生效；非交互安装要在安装前用 `debconf-set-selections` 预置。装完再改它不会覆盖 debconf 里的答案）。
同一版本重装会重新询问并保留上次的答案；
版本升级不重新询问；`apt purge` 会清除答案，重装按全新安装询问：

```sh
sudo dpkg-reconfigure remotedesktop-client     # 被控端同理：remotedesktop-host
```

**客户端和被控端读的最终值**来自随包的 `appsettings.json`（安装时写入），用户自己的那份可以覆盖它：

```text
~/.local/share/RemoteDesktop/appsettings.json     # { "Transport": { "RendezvousUri": "wss://主机:5080/ws" } }
```

**客户端和被控端读的是同一个用户文件**，所以写一次两边都生效。

> 随包的 `appsettings.json` 在 `/usr/lib/remotedesktop-{client,host}/` 下、属于 root，普通用户改不了；
> 程序会把自己的那份**叠在**随包的那份之上，只覆盖你写进去的键。升级替换包里的文件不影响你写的。

客户端连不上时会在界面上直说"未连接服务器（地址）"，不会变成"看着正常、就是连不上"。

### 3. 安装

```sh
sudo apt install ./RemoteDesktop-Client-Linux-amd64-<版本>.deb
sudo apt install ./RemoteDesktop-Host-Linux-amd64-<版本>.deb
```

（`apt install ./...deb` 同时装依赖；只用 `dpkg -i` 时，缺依赖会停在未配置状态。）

装完之后：

- 客户端出现在应用菜单里（`remotedesktop`）；装完第一次打开会显示 9 位设备代码和临时密码——两台机器互相输入对方
  的代码和密码即可连接。
- 被控端是一条 `systemd --user` 服务，安装时对**所有用户**启用。安装时已有图形会话的用户会**立即启动**
  该服务；没有会话（容器、无头镜像、还没有人登录）时，随用户下次登录启动。
  启动本身不会弹 portal 对话框——对话框在第一次有人连接并请求控制权限时才出现。

> **两个包可以同时安装。** host 服务常驻负责“被控制”，client 检测到它的控制 socket 后只做控制端
> （`AcceptIncoming=false`），两者使用不同的设备身份。客户端的模式在启动时决定：host 包已安装时，
> client 最多等 3 秒让服务创建 socket；如果服务在窗口打开后才启动，界面会提示重启客户端。
> `--HostMode standalone` 会强制忽略服务，和 host 服务同时运行是不受支持的状态。
>
> 卸载或 purge host 包会停止所有已登录用户的 host 服务。client 只有在 host 包仍安装（unit 文件存在）
> 时才把控制 socket 当作后台服务；否则按 standalone 运行，不会继续委托给一个包里已经没有的进程。
>
> **Linux 的“无人值守”只能到“可观看”为止。**
> Wayland portal 不允许持久化远程桌面会话（请求会被拒绝：`Remote desktop sessions cannot persist`）：
> 共享屏幕可以记住，**控制权限每次连接都要有人当场同意**。所以本机无人时，别人可以看到屏幕，
> 但**不能操作键鼠**。固定密码也不能改变这一点——它不是本软件的取舍，是 portal 的规定。
> 要在无人值守的机器上被操作，用 Windows 的被控端（macOS 的 host 包仅在用户登录后可用，见 [使用说明](../usage/README.md) 的能力矩阵）。

host 包没有界面，临时密码只存在进程内存里，所以**无人值守必须设固定密码**（至少 8 位）。安装过程会询问
**无人值守密码**，装完以安装时每个已登录用户的身份写进各自的
`~/.local/share/RemoteDesktop/secrets`（目录 0700、文件 0600）。留空表示不设置；升级或重装时留空则保留现有
密码，不会把它清掉。安装时一个登录会话都没有（无人在图形界面或 SSH 登录）就无法确定写进谁的密钥库，
安装脚本会提示登录后以运行 host 的用户手工执行：

```sh
/usr/lib/remotedesktop-host/RemoteDesktop.HostService --set-fixed-password '至少8位'
systemctl --user restart app-RemoteDesktopHost.service
```

修改已安装的固定密码用 `sudo dpkg-reconfigure remotedesktop-host`（留空保留现有密码）；清除或设置也可以用
运行 host 的用户执行 `--set-fixed-password` / `--clear-fixed-password` 后重启服务。无人值守安装
（`debconf-set-selections` 预置）可以写：

```sh
debconf-set-selections <<'EOF'
remotedesktop-host remotedesktop-host/unattended-password password a-long-enough-secret
EOF
```

安装时填写的密码由 debconf 暂存在 `/var/cache/debconf/passwords.dat`（仅 root 可读）；安装脚本写入用户密钥库后
会把它从 debconf 中清空，升级时按“保留现有密码”处理。`/etc/default/remotedesktop-host` 是全局可读的，
所以**不**支持在其中设置密码。

```sh
systemctl --user status app-RemoteDesktopHost.service
# 应用日志只写文件，不进 journald
tail -f ~/.local/share/RemoteDesktop/logs/service-*.log
# journal 里只有单元的启动失败与标准错误
journalctl --user -u app-RemoteDesktopHost.service -f
```

> 单元名是 `app-RemoteDesktopHost.service`，不是过去的 `remotedesktop-host`。桌面的 portal 从 systemd
> 单元名反推应用 ID（这里是 `RemoteDesktopHost`），摄像头权限按这个 ID 记账，包里还要有同名
> `RemoteDesktopHost.desktop`。改单元名等于换了应用身份，已经授予的摄像头权限会失效。

### 4. 第一次连接会弹两个对话框（摄像头权限是另一次授权）

第一次共享屏幕时，桌面会问一次"允许共享哪块屏"，**答过就记住了**（restore token 存在
`~/.local/share/RemoteDesktop/secrets`）。第一次要控制时还会再问一次，而且**每次连接都会问**——
portal 不允许远程桌面会话持久化，这是它的规定而不是本程序的取舍。

所以一台无人值守的 Linux 机器只能被观看，不能被操作（见上面 “安装”一节 的"无人值守"说明）。

控制对话框里要打开"允许远程交互"再点"分享"；只点"分享"而没有打开它，键鼠不会被授权。控制端会一直显示
当前状态：对话框弹出期间显示"已请求被控端授权控制"，被拒绝或对话框被关掉后显示"被控端未允许控制：当前
只能观看"并带"重试授权"按钮。被拒绝后宿主不会立刻重复弹窗（固定退避 30 秒，不可配置），点"重试授权"会立即再请求
一次，不必重新连接。这个状态来自宿主控制通道的 `InputAccessChanged`，因此即使控制端和被控端不在同一台
机器上，操作者也能知道该去被控端点什么。

摄像头权限和上面两个对话框不同：后台宿主没有焦点，GNOME 不允许它弹出 portal 的摄像头授权框。需要在被控端
本机安装客户端，打开「高级设置 → 安全 → 摄像头权限」点"允许"；没有图形客户端时可运行（以运行宿主的登录
用户执行，服务需已在运行）：

```sh
/usr/lib/remotedesktop-host/RemoteDesktop.HostService --camera-access allow
/usr/lib/remotedesktop-host/RemoteDesktop.HostService --camera-access deny    # 撤销
```

允许一次后，portal 对这台宿主不再询问；权限记在 `RemoteDesktopHost` 应用 ID 下，换单元名或换用户会失效。

### 5. 设备代码是空的

1. **服务器地址**：「高级设置」里填的是不是另一台机器也能访问到的地址（`127.0.0.1` 只能自己连自己）。
2. **服务器在不在**：`curl http://<服务器>:5080/health`。
3. **改完配置要重启**：`systemctl --user restart app-RemoteDesktopHost.service`。
4. **看日志**：`~/.local/share/RemoteDesktop/logs/`。
5. **服务起不来**：`journalctl --user -u app-RemoteDesktopHost.service` 和同目录下的 `startup-error.log`。

### 6. 屏幕共享不起来

按顺序查：

1. **有没有 portal**：`ls /usr/share/xdg-desktop-portal/portals`。没有 portal 就什么都共享不了，
   这是被控端唯一的入口。
2. **是不是 Wayland 会话**：只在 Wayland 上验证过。
3. **日志里有没有 `The compositor is not describing the pointer`**：那意味着光标位置不会实时更新，画面本身正常。
4. **日志里有没有 `libavcodec` 相关的拒绝**：编码器缺失或版本不对会明确报出来，不会静默黑屏。若 FFmpeg 装在非标准路径，可用环境变量 `RD_FFMPEG_PATH=/path/to/lib` 指向包含 `libavcodec.so.62` 的目录。

## 六、部署客户端 / 被控端（.pkg，macOS）

### 1. 构建与安装

macOS 没有 Windows 那种“服务 + Agent”的拆分，但不等于只能有一个 app。有两个不同 bundle 的应用：

- **client**：`/Applications/Remote Desktop.app`，Avalonia 客户端，完整功能；不自动启动；可在应用内手动开
  “登录时自动启动”（SMAppService）。
- **host**：`/Library/Application Support/RemoteDesktop/Host/Remote Desktop Host.app`，`LSUIElement` 的后台
  应用，**没有窗口**；里面是 `RemoteDesktop.HostService`，只负责被控，不能控制别人；由 LaunchAgent 在每次
  图形登录时启动。它不装进“应用程序”或启动台，避免被误认为一个可以打开的 app，也避免与 client 混淆。
  从旧包升级时，安装器会先停掉旧 LaunchAgent，再删除 `/Applications` 里的旧副本。
- 两者 bundle id 不同（`com.remotedesktop.client` / `com.remotedesktop.host`），TCC 授权也分开。
  **可以同时安装**：client 检测到 Host 的控制服务后只做控制端（`AcceptIncoming=false`），Host 常驻负责被控；
  两者使用不同的设备身份（client 用 `controller-identity`，Host 用 `device-identity`），不会出现同一设备
  代码互相踢下线。client 启动时如果 Host 包已安装，最多等 3 秒让服务创建控制 socket；如果服务在窗口打开后
  才启动，界面会提示重启客户端以交给 Host 托管。卸载 Host 后也要重启 client 才会在当前进程内重新承担被控端。

两个包的构建命令见 [安装包怎么构建](#八安装包怎么构建三平台) 的 macOS 一节，产物在 `artifacts/installer/macos/`。

| 装的包 | 文件 | 登录行为 | 能做什么 |
|---|---|---|---|
| 只装 client | `Remote Desktop.app` | 不自动启动；应用内可选开 SMAppService 登录项 | 打开后既能控制别人，也能被别人控制 |
| 只装 host | `Library/Application Support/RemoteDesktop/Host/Remote Desktop Host.app` + LaunchAgent | 每次图形登录自动启动；退出后不强行拉起 | **无窗口、只能被控**，不能控制别人；没有登录前访问；不显示在“应用程序” |
| 两个都装 | 两个 app 各一份 | host 的登录项照常启动 | client 只做控制端，host 负责后台被控；两者设备代码不同，可同时在线 |

host 没有界面，临时密码只在进程内存里，所以**无人值守必须设固定密码**（至少 8 位）。安装器的“被控端配置”页
直接提供**无人值守密码**和确认栏，安装时以当前登录用户的身份写进该用户的钥匙串；留空表示不设置，升级时留空
则保留现有密码。安装时没有用户登录（例如纯命令行/MDM）时无法写入某个用户的钥匙串，需要在该用户登录后执行：

```bash
"/Library/Application Support/RemoteDesktop/Host/Remote Desktop Host.app/Contents/MacOS/RemoteDesktop.HostService" --set-fixed-password '至少8位'
launchctl kickstart -k gui/$(id -u)/com.remotedesktop.host
```

设备码在 `~/Library/Application Support/RemoteDesktop/logs/` 的日志里，搜索
`Registered with the rendezvous server as`。

**安装**：双击 `.pkg`，或 `sudo installer -pkg <文件>.pkg -target /`。需要**管理员权限**（装到 `/Applications`）。

安装器的下一页会要求填写**信令服务器地址**（`ws://` 或 `wss://`）。如果服务器用 `wss://` 和自签名证书，
在那里选择证书的公钥文件（PEM/DER）；安装器会把它复制到
`/Library/Application Support/RemoteDesktop/certificates/signaling.crt`，客户端/被控端**只信任这一张证书**
（证书固定），不会修改系统钥匙串或系统信任设置。公开可信证书或 `ws://` 不需要文件。
无人值守安装可以把同样的答案写到
`/Library/Application Support/RemoteDesktop/install-answers.plist`：

| 键 | 值 |
|---|---|
| `RendezvousUri` | `wss://主机:5080/ws`（非回环地址必须是 `wss://`，明文 `ws://` 只允许回环或显式设 `Transport:AllowInsecureSignaling`）；整个地址里不能有空白、控制字符、双引号或反斜杠，否则安装失败 |
| `ServerCertificatePath` | 自签名服务器证书的绝对路径（PEM/DER），仅在 `wss://` 时需要 |
| `UnattendedPassword` | 仅 host 包：写入当前登录用户钥匙串的无人值守密码（至少 8 位）；不填则保持现有密码不变 |

和服务器包一样，这个答案文件在用完后不会删除；下次没有面板答案的安装会再次应用它。不需要预设时请删除。
host 的 `UnattendedPassword` 也会留在这份文件里（`root:wheel`，0600）；只做一次无人值守安装时，安装完成后
应删除该文件，避免密码长期留在磁盘上。

面板答案和这份答案文件都不存在时，安装脚本（`apply-answers.sh`）才会回退读安装进程环境里的 `RD_RENDEZVOUS_URI` /
`RD_SERVER_CERTIFICATE`。这只是脚本里的后备，没有在真机上验证过环境变量能传到 `installer` 运行的安装脚本里，
不要当作受支持的配置方式；需要预设就用答案文件。

服务器地址（`Transport:RendezvousUri`，默认 `ws://127.0.0.1:5080/ws`）写在
**`/Library/Application Support/RemoteDesktop/appsettings.json`**（root:wheel，0644）——它不在签名过的 `.app` 里，
因为改 bundle 内的文件会让签名失效、连带丢掉 TCC 授权。当前用户自己的
`~/Library/Application Support/RemoteDesktop/appsettings.json` 优先级更高（client 的设置页写的就是这份，host
也可以手工改这份来覆盖，改完 `launchctl kickstart -k gui/$(id -u)/com.remotedesktop.host`）。

五件必须知道的事：

- **包默认未签名**（没有 Apple Developer ID 证书）：在别人的 Mac 上 Gatekeeper 会提示
  "无法打开，因为来自身份不明的开发者"，**右键 → 打开**即可（或在「系统设置 → 隐私与安全性」里放行）。
  有证书时的签名与公证（下载即开）见 [build/signing/README.md](../../build/signing/README.md) 的 macOS 一节。
- **被控端需要手动授权两项权限**：在「系统设置 → 隐私与安全性」里把 **Remote Desktop Host** 勾进
  **屏幕录制**（截屏与系统声音环回）和 **辅助功能**（远程键鼠）。host 没有窗口；安装器已经在当前图形
  会话里启动它，首次启动时系统会弹提示，也可以在系统设置里手动添加
  `/Library/Application Support/RemoteDesktop/Host/Remote Desktop Host.app`。
  授权后执行 `launchctl kickstart -k gui/$(id -u)/com.remotedesktop.host` 重启（屏幕录制只在新进程启动时读取）。
  如果安装时没有用户登录，先
  `launchctl bootstrap gui/$(id -u) /Library/LaunchAgents/com.remotedesktop.host.plist` 再 kickstart。
- **局域网权限**：macOS 15 起，访问同一局域网内的服务器或对端需要「本地网络」权限（client 与 host 的
  `Info.plist` 已声明用途）。首次连接时系统会提示，或到「系统设置 → 隐私与安全性 → 本地网络」里允许；
  缺少这一步时连接会失败，而不是回退到公网地址。
- **无人值守**：host 包用 LaunchAgent 自启，并提供固定密码命令（见上）；client 包可以在应用里手动开
  `SMAppService` 登录项。授权按**已签名且路径稳定的应用**记账：host 固定在
  `/Library/Application Support/RemoteDesktop/Host`，不要移动或拷贝它；client 固定在 `/Applications`。
- **卸载 host**（pkg 没有卸载器，但包内带了脚本）：

  ```bash
  sudo "/Library/Application Support/RemoteDesktop/uninstall-host.sh"
  ```

  它停掉登录项、删 plist、`.app` 与安装回执；每用户数据和已固定的服务器证书保留。
  手工卸载等价于：

  ```bash
  launchctl bootout gui/$(id -u)/com.remotedesktop.host 2>/dev/null || true
  sudo rm -f /Library/LaunchAgents/com.remotedesktop.host.plist
  sudo rm -rf "/Library/Application Support/RemoteDesktop/Host" \
              "/Applications/Remote Desktop Host.app"
  sudo pkgutil --forget com.remotedesktop.host.app
  sudo pkgutil --forget com.remotedesktop.host.agent
  ```

  卸载 client 只需删 `/Applications/Remote Desktop.app` 并
  `sudo pkgutil --forget com.remotedesktop.client`；若在应用里开过登录项，先在应用里关掉。

> **最低系统版本 macOS 26 (Tahoe)**，成因见 [平台移植总览](../ports/README.md#3-macos-apple-silicon) 的“最低系统版本是 macOS 26”。
> `.pkg` 只面向 Apple Silicon（arm64）。

## 七、运行期注意事项

- **摄像头**作为常规功能随控制会话开放；观看模式下不开放。
- **临时密码**可以随时刷新；**固定密码**适合无人值守，由平台自己的密钥库持久化存储（Windows 是 DPAPI，macOS 是 Keychain，Linux 是 0600 文件），无需现场人员即可随时远程接入（macOS 仅在用户登录后可用），可在被控端界面的"高级设置 → 安全"中随时开启、修改或关闭。**Linux 例外**：固定密码能让连接通过认证，但 Wayland portal 仍要求每次控制有人当场授权，所以 Linux 上的无人值守只能观看（见 “安装”一节）。
- **中继带宽**：直连打洞成功时不走中继。`RelayEndpoint` 格式写错（不是 `IP:端口`）服务起不来；格式对但客户端够不到时，
  服务照常运行，只是直连失败的连接没有退路。
- **多台服务器**：设备码可以共库（PostgreSQL），但在线状态与连接握手（待处理的连接请求）都在进程内、不跨实例共享，
  所以设备要连到同一台服务器才能互相发现。
- **日志**：服务端程序只写标准输出（Windows 服务方式运行时进事件日志，Linux/macOS 见“上线前真机验收”的日志表）；Windows
  被控端在 `%ProgramData%\RemoteDesktop\logs`；客户端没有常规文件日志，只有崩溃时写的 `crash.log`，其余问题看
  界面提示。

## 八、安装包怎么构建（三平台）

**怎么产出交付物**，以及打包脚本里几个不显眼但会咬人的地方。装到机器上之后的事（安装、升级、卸载、排错）
“部署客户端 / 被控端”三节与“故障速查”；脚本的目录分层与命名规则见 [`build/README.md`](../../build/README.md)。

安装包是**自包含发布**（目标机器不需要装 .NET），所以每个平台各自打自己的包，都在对应平台上构建；
构建命令本身是 `dotnet build` / `dotnet test`，环境要求见 [`CONTRIBUTING.md`](../../CONTRIBUTING.md)。
所有产物落在仓库根的 `artifacts/installer/<平台>/`，命名与角色约定见同一份文档的"构建产物布局"。

### Windows（MSI）

```powershell
# 管理员 PowerShell 或普通 PowerShell（MSI 自己会请求提升）
# 正式发布请带上自己服务器的地址（不带就用内置的 ws://127.0.0.1:5080/ws 并给出警告，装到别的机器上连不上）
# 非回环地址必须是 wss://（明文 ws:// 只允许回环，或显式设置 Transport:AllowInsecureSignaling）
.\build\installer\windows\client\Build-ClientInstaller.ps1 -RendezvousUri wss://<服务器>:5080/ws

# 指定用哪张证书签名（不指定就在证书存储里自动找一张可用的）
.\build\installer\windows\client\Build-ClientInstaller.ps1 -SignCertificate <证书指纹或 CN 名称>
```

**客户端那一个 MSI 同时装下客户端、后台服务和会话 Agent**（三者本来就在同一个目录里协作），产物数十 MB
（随 SDK 与运行时更新变化）。
只做被控端的机器用**另一个**包：它只装服务与 Agent，没有客户端窗口，也没有开始菜单项。

```powershell
.\build\installer\windows\host\Build-HostInstaller.ps1 `
    -RendezvousUri wss://<服务器>:5080/ws
# 产出 artifacts/installer/windows/RemoteDesktop-Host-Windows-x64-<版本>.msi
```

两个包走同一个脚本（`Build-ClientInstaller.ps1 -HostOnly`）：服务定义、崩溃重启、UDP 防火墙规则和
`--configure-system` 只有一份，所以不会出现"客户端那个修好了、被控端那个还是旧行为"。产品名与 UpgradeCode
各自独立；两者共用服务名、不能同机共存，规则见 [安装](#2-安装)。

**Host 包的安装向导多一页“无人值守设置”**：host 装完没有窗口，临时密码只存在进程内存里，所以这一页是全新
机器唯一能直接输入固定密码的地方。留空表示不修改已有密码；勾选“清除已设置”则清掉。静默安装等价于：

```powershell
msiexec /i RemoteDesktop-Host-Windows-x64-<版本>.msi /qn `
  RENDEZVOUSURI="wss://rd.example.com:5080/ws" `
  FIXEDPASSWORD="至少8位"
# 清除已有固定密码：CLEARFIXEDPASSWORD=1
```

密码先由立即动作 `WriteFixedPasswordFile` 写到 `%ProgramData%\RemoteDesktop\.fixed-password`（仅 SYSTEM 与
Administrators 可读），再由 deferred 自定义动作用 `--set-fixed-password-file` 让服务读入自己的 DPAPI
**LocalMachine** 密钥库，随后另一个动作把该文件删除；`FIXEDPASSWORD` 用 `Hidden="yes"` 加进
`MsiHiddenProperties`（这个属性本身不可直接编写，WiX 5 会报 WIX0070）、动作带 `HideTarget="yes"`，所以不进
安装日志。密码本身没有字符集限制（至少 8 位），因为它经文件传给服务、不经过产品自己的命令行；少于 8 位时
安装会失败并在日志里说明，而不是装出一个连不上的 host。

静默安装时密码仍要穿过 `msiexec` 的**属性语法**：值里的 `"` 写成 `""`，并且整个 `FIXEDPASSWORD="…"` 作为
一个参数传给 `msiexec`。cmd 里直接这样写：

```cmd
msiexec /i RemoteDesktop-Host-Windows-x64-<版本>.msi /qn FIXEDPASSWORD="ab""cd"
```

Windows PowerShell 5.1 里**不要**写成自然的 `"FIXEDPASSWORD=$p"`，也不要用 `Start-Process -ArgumentList` 的
数组：实测这两种写法会让 msiexec 无声挂起（没有日志、没有错误码）。要手工双写并按单个参数传：

```powershell
$p = 'ab"cd'
$arg = 'FIXEDPASSWORD="' + ($p -replace '"','""') + '"'
& msiexec.exe /i RemoteDesktop-Host-Windows-x64-<版本>.msi /qn $arg
```

含 `& | < > ;` 等 shell 元字符时同理：它们在 MSI 属性值内部不需要转义，但必须整体留在上面的引号里。拿不准时
用安装向导输入，或装完后运行
`"C:\Program Files\RemoteDesktop\RemoteDesktop.HostService.exe" --set-fixed-password-file <口令文件>`
（安装包内部走的也是这条路）。

静默安装期间口令仍会出现在 `msiexec` 的命令行上（`Hidden` 只遮安装日志），对本机其他管理员可见；装完后用
`--set-fixed-password` 换一次口令，可以缩短这个窗口。

静默安装传入的地址不合法时（例如明文 `ws://` 指向非回环主机），MSI 会在配置动作阶段拒绝并回滚。
静默部署前先确认地址是 `wss://`（或确实是回环地址）。

设备码在服务的日志里：`%ProgramData%\RemoteDesktop\logs\service-<日期>.log`，搜索
`Registered with the rendezvous server as`。host 没有 UI 可以显示它。

服务端有**自己的**安装包，装的是另一套东西（Rendezvous + Relay + Ops + Gateway），和这两个包互不影响：

```powershell
.\build\installer\windows\server\Build-ServerInstaller.ps1
# 产出 artifacts/installer/windows/RemoteDesktop-Server-Windows-x64-<版本>.msi
```

打包脚本做四件事：发布可执行文件（自包含；client/host 用 ReadyToRun，server 不用）→ 生成 WiX 文件清单 →
编译 MSI → 自动签名，
最后**不安装就直接检查包内容**：`InstallerPackageTests` / `HostInstallerPackageTests` /
`ServerInstallerPackageTests` 打开 MSI 数据库核对载荷、服务、防火墙规则、开始菜单、自定义操作和属性，
缺什么就报错。

几个容易踩的地方：

- **包内容检查不是可选的。** 见下面那条 WiX 陷阱——它就是为了在包"看着正常但什么都没做"时喊出来。
- **许可页**：界面用 `WixUI_InstallDir`（欢迎、许可、选目录、进度、完成），client / host 包在选目录之后多一页「服务器连接」
  （host 再多一页「无人值守设置」），server 包在选目录之后多三页「服务器地址」「证书」「数据库」；许可页显示仓库根 `LICENSE` 里的专有 EULA，
  构建时由 `build/installer/New-LicenseRtf.ps1` 转成 RTF（非 ASCII 写成 `\uN?` 转义，任何代码页下都不会乱码）。
  该文件里若还有**【方括号】占位符**，打包脚本会拒绝出包——那是留给"换个人/换主体来发布"时的闸门，
  不是待办事项（当前内容是完整的）。
- `-KeepSymbols` 把 PDB 也打进安装包（默认不打）；需要从别的机器解析堆栈时才有用。
- **签名是自动的**，细节见 [`build/signing/README.md`](../../build/signing/README.md)。
- 安装时把服务器地址写进 `%ProgramData%\RemoteDesktop\appsettings.json`；这个文件不在 MSI 的文件表里，
  升级时**不会被覆盖**，所以就地改过的地址会保留。安装命令行的 `RENDEZVOUSURI` 只在**第一次安装**，或图形界面
  升级时在连接页改过值的情况下生效（见 “升级与卸载”一节）；要改已装好的机器，用客户端的「高级设置 → 服务器与托管 → 修改」
  （host 包用命令行 `--configure-system`）。静默首装的命令见 “安装”一节。

> **WiX 版本陷阱。** 安装包用的是 WiX v5（v6 起商业使用要付 OSMF 费用，v5 已停止社区支持）。
> 改动 `build/installer/windows/client` 时注意：**WiX 5.0.2 会把不含组件的 Fragment 静默丢掉**，
> 自定义操作和安装序列要放在有组件的那一段里，否则打出来的包看着正常却什么都没做——
> 上面那个包内容检查就是为了这个。

**打包脚本在 Windows PowerShell 5.1 与 PowerShell 7 下都能跑。** 删除 PDB 时按路径逐个删除
（`Get-ChildItem ... -Filter *.pdb -Recurse | ForEach-Object { [System.IO.File]::Delete($_.FullName) }`），
不走 `| Remove-Item` 管道——后者在 5.1 下会抛 `InputObjectNotBound`。
CI 用的是 pwsh 7，但发布机只需要 Windows 自带的 PowerShell 也能出包。

### macOS（`.app` / `.pkg`）

需要 Xcode Command Line Tools；打包为**自包含**应用。

```bash
# .app（bundle 目录，用 Apple Development 身份签名，便于本机开发调试）
build/installer/macos/client/make-app.sh --configuration Release

# .pkg（可分发安装包，装到 /Applications；默认未签名）
build/installer/macos/client/make-pkg.sh --configuration Release

# 被控端 .pkg：独立的 Remote Desktop Host.app（LSUIElement，无窗口）+ 登录时自动启动的 LaunchAgent
build/installer/macos/host/make-pkg.sh --configuration Release

# 服务端 .pkg（信令、中继、监控台与 HTTPS 网关 + 四个 launchd 守护进程，装到 /usr/local/remotedesktop-server）
build/installer/macos/server/make-pkg.sh --configuration Release
```

`.pkg` **默认未签名**（未签名时的 Gatekeeper 提示与放行方法见 “构建与安装”一节）。有证书时加 `--distribution` 与
`--notarize` 即可做到下载即开，两张证书各签什么、公证凭据怎么配，见
[`build/signing/README.md`](../../build/signing/README.md) 的 macOS 一节。

macOS 打包有三处值得知道的构建细节：

- **安装面板是编出来的**：`native/macos/build-installer-plugin.sh` 用 `clang` 编译
  `native/macos/rd_installer_plugin.m`，三个 make-pkg 都会调用它，并用 `productbuild --plugins` 放进产品归档。
  传 `--distribution` 且找到签名身份时，它用同一个 `Developer ID Application` 身份签名；分发时被公证一起扫描。
- **服务端载荷逐个签名**：`make-pkg.sh --distribution` 会对 payload 里的每个 Mach-O（四个 apphost 与原生库）
  单独签名；做法与原因见 [`build/signing/README.md`](../../build/signing/README.md) 的 macOS 一节。
- **一次性密码不进包**：`--distribution` 拒绝 `--postgres` / `--relay-key` / `--tls-password`。安装期答案由
  面板或 `install-answers.plist` 提供，见 “服务端安装包（三平台）”一节 的 macOS 一节。

### Linux（`.deb`）

三个包各装各的，在 Linux 上构建（自包含发布，目标机器不需要装 .NET）：

```bash
build/installer/linux/client/build-deb.sh   # 用来控制别人
build/installer/linux/host/build-deb.sh     # 允许被别人控制
build/installer/linux/server/build-deb.sh   # 信令、中继、监控台与网关
```

`libmsquic` 随包放在可执行文件旁（自包含程序会在那里找原生库，别的程序不会），所以装出来的包不需要设
`LD_LIBRARY_PATH`。安装与服务管理见 “部署客户端 / 被控端（Debian 包，Linux）”一节。

客户端和被控端构包时可以给安装问题一个站点默认值：

```bash
RD_RENDEZVOUS_URI=wss://rd.example.com:5080/ws \
RD_SERVER_CERTIFICATE=/secure/signaling.crt \
build/installer/linux/client/build-deb.sh
```

`RD_SERVER_CERTIFICATE` 必须是 PEM **证书**（不是私钥）；它随包分发，安装时仍可换成另一份证书。
服务端没有构包期属性：数据库与 TLS 在安装时询问，也可以用 `debconf-set-selections` 预置。

三个 `build-deb.sh` 还有几个**构建者的开发旋钮**，不是对外支持的特性：第一个参数 `[output-directory]` 指定输出目录
（默认 `artifacts/installer/linux`）；环境变量 `DEB_ARCHITECTURE`（默认 `amd64`，写进包的 Architecture 与文件名）
和 `DEB_RUNTIME`（默认 `linux-x64`，传给 `dotnet publish -r`）。**CI 与发布只支持 amd64 / linux-x64**（`ci.yml` 与
`release.yml` 都直接调用脚本，不设这两个变量），随包的 `libmsquic` 也只取 amd64 版本（`native/linux/fetch-libmsquic.sh`）。
这两个变量**必须成对修改**：脚本不校验它们是否一致，只改一个会产出标签与二进制不符的包。

### 产物在哪

一律在 `artifacts/installer/<平台>/`，文件名的形状、发布时的改名，以及完整的产物路径与 SDK 自己的四层布局，见
[`CONTRIBUTING.md`](../../CONTRIBUTING.md#-构建产物布局)；构建产物与发布资产名各平台的版本形态见 [自动化发布到 GitHub Releases](#三自动化发布到-github-releases) 开头的版本表。

## 九、客户端本机的存储（设备列表）

客户端把"最近连接过的设备"放在数据目录下的 `recent_devices.json`（Windows 默认 `%AppData%\RemoteDesktop\`，
Linux 是 `$XDG_DATA_HOME/RemoteDesktop/` 或 `~/.local/share/RemoteDesktop/`，macOS 是
`~/Library/Application Support/RemoteDesktop/`）。

运维上只需要知道两件事：**列表里没有密码**（记住的密码在平台密钥库里，换机器不跟着走），以及**读不出来的文件会被
保留**（改名为 `recent_devices.json.corrupt-<时间戳>`；只是暂时打不开时抛异常，不会当成空列表覆盖）。写入是原子的、
旧格式自动迁移；字段、备份范围与"为什么不放密码"见 [系统设计](../design/README.md) 的"客户端本机存储"一节。

## 十、故障速查

**Linux 上先跑这一条**，它一次问完这台机器缺什么，比照表格逐条猜快得多：

```bash
remotedesktop --self-check                                           # 装了客户端包
/usr/lib/remotedesktop-host/RemoteDesktop.HostService --self-check   # 只装了 host 包
```

`/usr/bin/remotedesktop` 只有客户端包提供，所以只装 host 包的机器用第二条（host 包自带的自检）。检查项与退出码的说明见
[使用说明](../usage/README.md) 的"先确认这台机器能不能用"。

它不弹对话框、不改任何设置（唯一的写操作是数据目录的可写探测：必要时创建数据目录，并在里面建一个 `.writable`
探测文件后马上删掉），所以在一台还没授权过、甚至起不来的机器上照样能跑；有必需项不通过时退出码
是 1。它答不上来的（连不上服务器、连上了但画面不动、服务没起来）再看下面。

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 设备代码显示 `--- --- ---`，状态"未连接服务器" | 连不上信令服务器 | 见"已经装好了，但设备代码是空的"一节；用了 `wss://` 时先确认证书域名和信任链 |
| Ops 节点 `rendezvous-local` 显示"抓取失败"、设备管理为空 | ① 本机节点被登记成机器名/IP：没有 NodeToken 时 `/metrics` 与 `/internal/*` 只授权回环调用，非回环访问得到 401；② 地址已经是 `127.0.0.1` 且信令用 TLS：证书 SAN 里没有 `127.0.0.1`（Debian/Ubuntu 把主机名映射到 127.0.1.1），又没有配 `Ops:NodeCertificatePath`，握手报名称不匹配 | ① 在节点管理页「改地址」改回 `https://127.0.0.1:5080`（TLS）或 `http://127.0.0.1:5080`（明文）；远程节点配置 `Ops:NodeToken`（配了 token 时非回环 http 地址会被拒绝）；② 在 `Ops/appsettings.json` 的 `Ops` 段加 `"NodeCertificatePath": "/etc/remotedesktop-server/tls/signaling.crt"` 后重启 `remotedesktop-ops`；安装脚本会自动 pin，它新生成的证书也带 `127.0.0.1` |
| 直连打洞失败、改走中继时连接被拒绝 | `Relay\appsettings.json` 的 `Relay:SharedKey` 与 `Rendezvous:RelaySharedKey` 不一致（旧包升级后 Relay 可能为空） | 安装新 server 包，升级时的配置步骤（`--configure-system`）会把两边对齐到同一把密钥（已有的保留）；或管理员 PowerShell 运行 `Set-ServerConfiguration` 后 `Restart-Service RemoteDesktopRelay` |
| 设备反复掉线重连 | 反向代理把长连接掐了 | nginx 加 `proxy_read_timeout 1h`（见"生产环境：信令必须走 TLS"） |
| 客户端说证书错误 | 证书里的名字/地址与客户端填的地址对不上，或这台机器不信任那张证书 | 用 `New-ServerCertificate.ps1` 把客户端实际使用的地址加进去重新生成；机器没信任证书就装一次 `.cer`（见 “生产环境：信令必须走 TLS”一节「内网也一样」一段） |
| 日志里成片 "Throttled registrations from {地址}"（连接请求、在线状态查询的限流日志则带设备代码） | 限额挡住了某人（或挡错了：代理没配 `TrustedProxies`，整个代理被当成一台机器） | 先确认 `TrustedProxies` 配上没有，再按需要调"公网上的限额"里的数字 |
| 两台机器都有代码，但连接超时 | 双方都连到服务器了，但打洞和中继都不通 | 检查 `RelayEndpoint` 是否为客户端可达地址、UDP 5081/5082 是否放通；先试 `--Transport:ForceRelay true` 判断是不是打洞问题 |
| 连接提示"密码错误" | 密码过期（临时密码刷新过）或被限流 | 在被控端界面刷新密码重试；按控制端设备计，5 分钟窗口内累计失败 5 次，窗口结束前拒绝 |
| 能看到画面但键盘鼠标没反应 | 会话是"观看模式" | 连接时不要勾"观看模式"；被控端也可以把会话降级为仅观看 |
| 文件/终端/摄像头不可用 | 被控端只有登录界面/UAC（Agent 是 SYSTEM）或权限被收回 | 登录到用户桌面再试；确保会话不是观看模式 |
| 锁屏后终端断开 | Agent 切换会结束终端会话 | 重新打开终端；文件传输则可以在重连后继续 |
| 日志出现 "QUIC is not supported on this system" | Windows 10 缺 Schannel 的 TLS 1.3；或 Linux 上开发运行时找不到 libmsquic | Windows：升级到 Windows 11 / Server 2022+，或换 OpenSSL 版 msquic.dll。Linux：装 `.deb` 的话是随包带的，开发运行时用 `native/linux/fetch-libmsquic.sh` 再设 `LD_LIBRARY_PATH` |
| Linux：控制端每次连接都要重新同意 | portal 不允许远程桌面会话持久化 | 这是规定，不是配置项。Linux 上的无人值守只能观看；要无人值守被操作，用 Windows 被控端（macOS 的 host 仅登录后可用） |
| Linux：剪贴板不同步 | 桌面没有授予剪贴板（和键盘鼠标在同一次授权里，点"拒绝"就没有）；或内容超过上限（文本 4 MB、图片 8 MB，各平台相同） | 看一眼被控端日志（`~/.local/share/RemoteDesktop/logs/service-*.log`）：授予失败会有 "did not grant control"。超限内容会被丢弃而不是截断 |
| Linux：摄像头列表是空的 | 两种可能：机器上确实没有摄像头（portal 的 `IsCameraPresent` 答 false），或者授权被拒了 | 被控端日志（`~/.local/share/RemoteDesktop/logs/service-*.log`）里会有 "did not grant access to the cameras"。后台宿主弹不出 portal 的摄像头授权框，要在被控端显式允许一次（见 “第一次连接会弹两个对话框”一节）。采集链路已在真机验证；这一条摄像头授权缺口在 [平台移植总览](../ports/README.md) 的"未验证与已知缺口"里也有记录 |
| Linux：光标位置不更新 | 合成器没有送光标元数据 | 画面本身正常；被控端日志会警告一次 |
| macOS：被控端在线（有设备代码）但画面黑屏或键鼠没反应 | 没有授予「屏幕录制」或「辅助功能」 | 看 `~/Library/Application Support/RemoteDesktop/logs/permissions.log`（host 发现缺权限时写入）；在「系统设置 → 隐私与安全性」里把 **Remote Desktop Host** 勾进这两项，再 `launchctl kickstart -k gui/$(id -u)/com.remotedesktop.host`（见 “构建与安装”一节） |

## 十一、上线前真机验收

这一节是发布前要**逐条跑过**的清单：每条给出可直接粘贴的命令、可以对照的判据、以及失败时先看哪个文件。
V1–V3 为发布阻断项（P0）；V4–V8 按需抽查，结果填进文末的记录表。**V2–V3 当前没有已归档的通过证据；下一次发布前必须重新执行并填写。**

| 编号 | 平台 | 验证什么 | 优先级 |
|---|---|---|---|
| V1 | Windows | 服务托管 + SessionAgent 在登录会话里 | P0 |
| V2 | macOS | 服务端四个守护进程以 `_remotedesktop` 运行 | P0 |
| V3 | Linux / Wayland | 多显示器按被共享的那块屏采集 | P0 |
| V4 | macOS | 公证与 Gatekeeper 放行 | P1 |
| V5 | Linux | 摄像头授权 / VA-API 硬编 / X11 会话 | P1 |
| V6 | Windows | 摄像头首帧黑屏（复现记录） | P2 |
| V7 | 被控端 | QUIC listener 泄漏观察 | P2 |
| V8 | 服务器 | 监控台新功能抽查（独立令牌 / 静默 / 邮件） | P2 |

**日志在哪。** 失败时先看这张表，不要先重建环境。

| 组件 | Windows | Linux | macOS |
|---|---|---|---|
| 客户端崩溃 | `%APPDATA%\RemoteDesktop\crash.log` | `~/.local/share/RemoteDesktop/crash.log` | `~/Library/Application Support/RemoteDesktop/crash.log` |
| 被控端服务 | `%ProgramData%\RemoteDesktop\logs\service-*.log`（保留 14 天）、`agent-user-*.log` / `agent-secure-*.log`（用户桌面与登录/UAC 桌面各一份，保留 7 天，每秒落盘）、`startup-error.log` | `~/.local/share/RemoteDesktop/logs/service-*.log`（应用日志只写文件，不进 journald）、`startup-error.log`；`journalctl --user -u app-RemoteDesktopHost` 只有单元的启动失败与标准错误 | `~/Library/Application Support/RemoteDesktop/logs/service-*.log`；缺屏幕录制/辅助功能授权时另有 `permissions.log` |
| 服务端 | 四个服务 `RemoteDesktopRendezvous` / `RemoteDesktopRelay` / `RemoteDesktopOps` / `RemoteDesktopGateway`；**没有文件日志**，排错要停服务后在终端里运行同一份设置 | `journalctl -u remotedesktop-{rendezvous,relay,ops,gateway}`（只进 journal；`/var/log/remotedesktop-server/` 由安装包创建并允许服务写入，但目前没有程序往里写文件） | `/usr/local/remotedesktop-server/logs/{rendezvous,relay,ops,gateway}.log` |

### V1 · Windows：服务托管与 SessionAgent（P0）

**前置**：客户端 MSI 已安装，当前是**管理员** PowerShell，机器上有一个已登录的交互会话。

```powershell
Get-Service RemoteDesktopHost | Format-List Status, StartType
Get-CimInstance Win32_Service -Filter "Name='RemoteDesktopHost'" | Select-Object -ExpandProperty StartName
Get-Process RemoteDesktop.SessionAgent -IncludeUserName | Select-Object Id, UserName
Restart-Service RemoteDesktopHost; Start-Sleep 5
Get-Process RemoteDesktop.SessionAgent -IncludeUserName | Select-Object Id, UserName
```

**预期**：`Status=Running`、`StartType=Automatic`；`LocalSystem`；第三行与第五行都能看到 `RemoteDesktop.SessionAgent`，
`UserName` 是**登录用户名**而不是 `NT AUTHORITY\SYSTEM`。客户端「高级设置 → 服务器与托管」应显示被控端托管方式为
「由后台服务托管」，且"本机控制端代码"与主页的设备代码是两个不同的值。

**失败看哪里**：`%ProgramData%\RemoteDesktop\logs\service-*.log`（服务起不来、起不了 agent）；同目录的
`agent-user-*.log`（用户桌面的 agent 起来了但连不上服务或接管会话失败；登录/UAC 桌面的在 `agent-secure-*.log`）；`startup-error.log`（设置读不出来时服务直接退出）。
服务被系统拒绝启动时另外看 `Get-WinEvent -LogName System -MaxEvents 50` 里的 Service Control Manager 条目。

**渲染**：从控制端连上后，流尺寸应如实显示、画面保持比例不拉伸，鼠标移到画面四角时被控端指针落在对应的角上。
letterbox 的"等比缩放 + 居中"路径不依赖源宽高比，上面这一条即覆盖；要字面验证 4:3 源，在 Hyper-V 里把 vmconnect
切到"基础会话"（guest 落到 1024×768）即可。"多显示器不同 DPI"对 Windows **不适用**：单显示器抓屏走物理像素，
DPI 缩放不影响鼠标映射（Hyper-V 里也无法构造这种环境）。

2026-11-07 已在管理员 PowerShell 完成实测：服务重启前后均为运行状态，SessionAgent 从 PID 3360 重建为 PID 22312，始终以登录用户运行在交互会话 1。证据见文末 V1 记录。

### V2 · macOS：服务端专用账户（P0）

**前置**：服务端 `.pkg` 已安装（`sudo installer -pkg RemoteDesktop-Server-macOS-arm64-<版本>.pkg -target /`）。

```bash
dscl . -read /Users/_remotedesktop UniqueID PrimaryGroupID NFSHomeDirectory UserShell
ps -o pid=,user= -p $(pgrep -f RemoteDesktop.Server.Rendezvous)
ls -ld /usr/local/remotedesktop-server/var
ls -l /usr/local/remotedesktop-server/Ops/appsettings.json
grep -c UserName /Library/LaunchDaemons/com.remotedesktop.rendezvous.plist
```

**预期**：`UniqueID` / `PrimaryGroupID` ≥ 300（安装脚本从 300 起找空位）；`NFSHomeDirectory` 是 `/usr/local/remotedesktop-server/var`；
`UserShell` 是 `/usr/bin/false`；`ps` 的 user 是 `_remotedesktop`；`var` 是 `drwx------ _remotedesktop _remotedesktop`；
`appsettings.json` 是 `-rw-r----- root _remotedesktop`；最后一条输出 `1`。

最后一条输出 `0` 说明交接没有通过；当前版本会使安装失败且不以 root 启动服务，不会形成可接受的降级状态。
修复目录、设置或证书权限后重新安装。原因写在安装日志里：

```bash
grep -n "could not prepare the unprivileged" /var/log/install.log | tail -3
```

**再验证运维入口**：第一个管理员必须用服务账号创建——用 root 创建会让 SQLite 的 `-wal`/`-shm` 归 root，
之后监控台写不进去。

```bash
sudo -u _remotedesktop /usr/local/remotedesktop-server/Ops/RemoteDesktop.Server.Ops --create-admin <用户名>
sudo launchctl kickstart -k system/com.remotedesktop.ops
```

**失败看哪里**：`/var/log/install.log`（安装器与 postinstall 的全部输出）；`/usr/local/remotedesktop-server/logs/`
下对应角色的 `.log`（守护进程读不到设置、证书或数据库时在这里说原因）。

### V3 · Linux / Wayland：多显示器（P0）

**前置**：GNOME Wayland 会话，两块显示器（最好分辨率不同，其中一块 1.5 倍缩放更能暴露进位问题）；被控端已安装并在跑。

```bash
loginctl show-session "$XDG_SESSION_ID" -p Type -p Active
systemctl --user status app-RemoteDesktopHost --no-pager
```

**预期**：`Type=wayland`、`Active=yes`；服务是 `active (running)`。

**场景 A：请求一块没有共享的屏幕。** 从控制端发起连接、被控端弹出共享对话框时**只勾选左屏**；随后在控制端工具栏
改选右屏。预期：画面是**左屏**，工具栏随之显示左屏的名字，状态栏分辨率等于左屏的物理分辨率。

```bash
grep "is sharing" ~/.local/share/RemoteDesktop/logs/service-*.log
```

预期出现一条形如 `The desktop is sharing 'DP-1' (2560x1440), not the requested 'HDMI-1'; ...` 的记录（屏幕名与
尺寸以你的机器为准）。

**场景 B：两块都共享。** 重新连接并在对话框里两块都勾选，来回切换。预期：每次切换后状态栏分辨率与系统设置里那块屏
一致；把鼠标移到画面四角，被控端指针落在对应的角上，不是边框外，也不是另一块屏。

**自动化部分**（仓库里的真实链路测试；需要 .NET SDK、仓库源码，以及一块能弹对话框的桌面）：

```bash
RD_PORTAL_TEST=1 dotnet test --project tests/RemoteDesktop.Platform.Linux.UnitTests/RemoteDesktop.Platform.Linux.UnitTests.csproj \
  -- --filter-class "*PipeWireScreenCapturerTests*" --filter-class "*PortalScreenCastTests*" \
     --filter-class "*LibEiInputInjectorTests*" --filter-class "*SessionEndToEndTests*"
```

预期 `0 failed, 0 skipped`；过程中桌面会弹出真实的共享与控制授权框，需要点"允许"。

**失败看哪里**：`~/.local/share/RemoteDesktop/logs/service-*.log`（`is sharing`、授权被拒、拷贝帧失败都在这里，
用 `tail -n 100` 看最后一部分）；服务根本没起来时再看 `journalctl --user -u app-RemoteDesktopHost -n 100 --no-pager`。

### V4 · macOS：公证与 Gatekeeper（P1）

**前置**：用**真实的 Developer ID** 构建并公证过的 `.pkg`，不是自签名包。

```bash
spctl -a -vvv -t install RemoteDesktop-Client-macOS-arm64-<版本>.pkg
pkgutil --check-signature RemoteDesktop-Client-macOS-arm64-<版本>.pkg
xcrun stapler validate RemoteDesktop-Client-macOS-arm64-<版本>.pkg
```

**预期**：第一条输出 `accepted` 且含 `source=Notarized Developer ID`；第二条
`Status: signed by a developer certificate issued by Apple for distribution`；第三条 `The validate action worked!`。

**失败看哪里**：`spctl` 自己给出的拒绝原因；`xcrun notarytool log <submission-id>` 是 Apple 的逐项说明；打包脚本的
输出里有签名与公证那一步的原始命令与响应。

### V5 · Linux：摄像头、VA-API 与 X11（P1）

**摄像头**：在被控机上打开客户端窗口「高级设置 → 安全 → 摄像头权限」点「允许」，然后从控制端打开摄像头窗口。

```bash
grep -i camera ~/.local/share/RemoteDesktop/logs/service-*.log
```

**预期**：控制端能列出摄像头并出图；日志里是打开成功的记录（设备名、分辨率），没有 portal 拒绝。

**VA-API 硬编**：

```bash
vainfo | grep -E "VAProfileH264|VAEntrypointEncSlice"
```

**预期**：至少一条 `VAProfileH264High : VAEntrypointEncSlice`；此时控制端状态栏显示"硬件编码"。没有输出说明驱动
不支持编码，回退软件编码是预期行为，不算失败。

**X11 会话**：注销后选择 "Ubuntu on Xorg" 登录：

```bash
echo "$XDG_SESSION_TYPE"
```

**预期**：`x11`；重复 V3 场景 B 的单屏连接，画面与指针都正常。

**失败看哪里**：同样是 `~/.local/share/RemoteDesktop/logs/service-*.log`——授权被拒、摄像头被占用、设备打不开都会写在那里。

### V6 · Windows：摄像头首帧黑屏（P2，观察）

**做法**：从控制端打开摄像头窗口 → 开始 → 看 2 秒内是否出图 → 关闭，重复 20 次；记录黑帧出现的次数、每次的分辨率
与编码器（窗口底部那行）。

**预期**：20 次都在 2 秒内出图。历史上出现过一次 1280×720 首帧全黑、随后正常，至今没有稳定复现路径；这份记录的价值
是下一次能对上具体的机器、摄像头与次数。

**证据**：`%ProgramData%\RemoteDesktop\logs\agent-user-*.log` 里同一时刻的摄像头行
（`Select-String -Path <log> -Pattern camera`），加一张黑帧截图与时间点。

### V7 · 被控端：QUIC listener 泄漏（P2，观察）

**背景**：被控端每准备一次入站连接就会新建一个 `QuicListener`，.NET/MsQuic 侧有一个已知的原生泄漏
（约 1.2 MB/次，2026-10-12 实测；本仓库不保存上游链接），来龙去脉见 [系统设计](../design/README.md) 的“还没有做的事”第一条。
信令服务器走 WebSocket，不跑 QuicListener，不受影响，所以要量的是**被控端的 `RemoteDesktop.HostService` 进程**。
这一条不是通过/失败，而是长期开机的体检。

**做法**：服务稳定后取基线，正常使用一天后再取一次，同时记下当天的入站连接量。

```bash
# Linux / macOS（在运行被控端的那台机器上）
ps -o rss= -p $(pgrep -f RemoteDesktop.HostService)
```

macOS 上这条 `pgrep` 只对装了 host 包的机器成立；只装 client 包时，被控端在 `RemoteDesktop.Client` 进程内，要量那个进程。

```powershell
# Windows
[math]::Round((Get-Process RemoteDesktop.HostService).WorkingSet64 / 1MB)
```

**判据**：RSS 增长与连接数大致线性、每个连接超过 1 MB 时记下来。没有连接也持续增长，说明是另一类问题，按上面的
日志继续查。

**失败看哪里**：Windows `%ProgramData%\RemoteDesktop\logs\service-*.log`；Linux
`~/.local/share/RemoteDesktop/logs/service-*.log`；macOS `~/Library/Application Support/RemoteDesktop/logs/service-*.log`。

### V8 · 监控台：独立令牌、静默与邮件（P2，抽查）

三条都是最近才加的实现，CI 里只有合成数据；在真实部署上各走一遍。

**独立令牌**：节点管理 → 某节点「详情」→「生成独立令牌」并复制；**先不改节点配置**，等一个抓取周期（默认 10 秒），
该节点应变成"抓取失败"。把值填进这台节点的 `Rendezvous:NodeToken`（或 `Relay:NodeToken`），重启节点服务，下一个
周期恢复"正常"。再点「清除」，节点回到部署级令牌后仍正常。

**静默**：规则页先有一条 `scrape`（抓取失败）规则；把某节点地址临时改成 `http://127.0.0.1:1` 触发一条告警；
告警出现后点「静默 1 小时」，再把地址改回、让规则恢复并再次触发。预期：告警列表里两次都有记录，但静默窗口内 Webhook/邮件没有新增；审计页有一条
`silence.create`，解除后有一条 `silence.delete`。

**邮件**：把 `Ops:Notifications:Smtp:Host` 指到 `127.0.0.1:1` 并重启监控台，触发一次告警。预期日志里出现
`The mail for rule ... was not delivered: cannot reach 127.0.0.1:1`，而 Webhook 仍收到——两条通道互不影响。

**失败看哪里**：Linux `journalctl -u remotedesktop-ops`；macOS `/usr/local/remotedesktop-server/logs/ops.log`；
Windows 上在终端里运行 `RemoteDesktop.Server.Ops.exe` 才有输出。

### 记录表

复制这张表进发布记录，每条验收一行；"证据"写日志文件名与时间点，不要只写"已通过"。V1 有一份已归档记录；每次发布仍应从空表重新填写。

| 日期 | 版本 | 条目 | 结果 | 证据 / 备注 |
|---|---|---|---|---|
| 2026-11-07 | 1.0.0 | V1 | 通过 | 管理员 PowerShell：`RemoteDesktopHost` = Running/Automatic、`StartName=LocalSystem`。重启前 `RemoteDesktop.SessionAgent.exe` PID 3360、重启后 PID 22312；两次均在 Session 1，以 `HTZVYL3\Arthur` 运行，路径为 `C:\Program Files\RemoteDesktop\RemoteDesktop.SessionAgent.exe`。 |
|  |  | V2 |  |  |
|  |  | V3 |  |  |
|  |  | V4 |  |  |
|  |  | V5 |  |  |
|  |  | V6 |  |  |
|  |  | V7 |  |  |
|  |  | V8 |  |  |
