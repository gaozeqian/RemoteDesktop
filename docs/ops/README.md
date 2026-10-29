# 部署与运维

给**要部署和运维的人**：架服务器、发版、把客户端与被控端装到机器上、出问题时查什么。
打包脚本的内部细节见 §八，客户端本机存了什么见 §九。

> 运维监控台（节点健康、会话、告警、审计）已经可用：怎么装、怎么让总览出现数字见 §二 10，
> 它的构成与设计见 §十一。

被控端与服务端各自需要什么系统见 §一；功能怎么用、哪些平台支持什么见 [使用说明](../usage/README.md)；
"部署者必须自己做的事"（换共享密钥、信令上 TLS、装包签名）见 [系统设计](../design/README.md) §3。

这套软件是**自建服务器**的：两台机器互相连接，靠的是一台它们都能连上的**信令服务器（Rendezvous）**。
没有它，被控端拿不到设备代码，控制端也无从发现对方——"设备代码是空的"几乎总是这个原因。

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
| 服务器 | Windows 11 / Windows Server 2022+（x64），或 Linux（x64，glibc；需要 ICU 与 OpenSSL，`.deb` 已声明）；.NET 10 运行时（或自包含发布，不需要装运行时）；开放 TCP 5080、UDP 5081、UDP 5082 |
| 被控端（Windows） | Windows 11 / Windows Server 2022+（x64）；**管理员权限**装 MSI（服务以 LocalSystem 运行） |
| 被控端（Linux） | **Ubuntu 26.04+**（x64，FFmpeg 8 的 soname 固定）或有 Wayland 会话与 portal 的同等发行版；装 `.deb` 后是一条 `systemd --user` 服务，**不需要 root 常驻** |
| 控制端 | Windows 11 / Windows Server 2022+（x64），或 **Ubuntu 26.04+**（x64） |

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

### 1a. 服务端：Debian 包（Linux）

```sh
build/installer/linux/server/build-deb.sh
sudo apt install ./artifacts/installer/linux/RemoteDesktop-Server-Linux-amd64-<版本>.deb
```

`apt install ./...deb` 会一并解决包声明的依赖（ICU、OpenSSL、debconf、adduser 等）。用 `dpkg -i` 也可以，
但目标机缺少其中任何一个依赖时包会停在未配置状态，需要再跑一次 `sudo apt-get -f install`。

包把四个程序装到 `/usr/lib/remotedesktop-server/{Rendezvous,Relay,Ops,Gateway}`，四个 **system** 单元装到
`/lib/systemd/system`，装完即启用并启动。**配置在 `/etc/remotedesktop-server/`**，不在安装目录里——
升级不会覆盖运维改过的东西。Windows 现在也把 live settings 放在 `%ProgramData%\Remote Desktop Server`，
三平台在这一点上一致。

安装过程会问两组问题（`dpkg -i` 也会问，不只是 `apt install`）：

| 问题 | 取值 | 说明 |
|---|---|---|
| 数据库 | `sqlite` / `postgres` / `none` | 前两个是正式选择；`none` 只把设备码放内存，重启即失效。Ops 用同一个 provider，`none` 时仍保留自己的 SQLite 库 |
| SQLite 文件 | 绝对路径 | 默认 `/var/lib/remotedesktop-server/rendezvous.db`；同目录下放 `ops.db`。目录会创建并交给服务账户 |
| PostgreSQL 连接串 | Npgsql 连接串 | 选 postgres 时必填；数据库本身要先存在，表由服务创建 |
| 信令 TLS | `generate` / `existing` / `none` | 自签证书 / 已有证书（PKCS#12 或 PEM + 同名 `.key`）/ 明文 HTTP |
| 证书文件与口令 | 路径 / 口令 | `existing` 时询问；文件会被复制到 `/etc/remotedesktop-server/tls/custom/`，服务账户可读 |
| 公网主机名 | 可逗号分隔 | 仅 `generate` 时询问；写进自签证书的 SAN，客户端用 DNS 名或 NAT 地址访问时必须填 |

设置仍由程序自己写（`--configure-system`），`postinst` 只负责把答案变成参数——两个平台因此产出同样的
文件，走同一段代码。控制台的设置也写在 `/etc/remotedesktop-server/Ops/appsettings.json`，和
`--create-admin` 读的是同一份。

> **同一版本重装会重新询问**（默认值保留上一次的答案）；**版本升级不会**重新询问；`apt purge` 会连同
> debconf 答案一起清除，之后重装按全新安装询问。`dpkg-reconfigure` 则总是重新询问。

改配置：

```sh
sudo dpkg-reconfigure remotedesktop-server
# 非交互安装可以预先写入答案：
# printf '%s\n' 'remotedesktop-server database-provider select postgres' \
#   'remotedesktop-server postgres-connection password Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=...' \
#   | debconf-set-selections
systemctl status remotedesktop-rendezvous remotedesktop-relay remotedesktop-ops remotedesktop-gateway
# 这个包的默认答案是 generate，即自签 HTTPS；-k 是因为证书不是公共 CA 签的
curl -k https://127.0.0.1:5080/health
curl http://127.0.0.1:5090/health
```

`/etc/default/remotedesktop-server` 仍是高级设置与非交互安装的入口（监听地址、中继地址、共享密钥、
防火墙开关，以及数据库/TLS 问题的预置值）；交互式改数据库或 TLS 请用 `dpkg-reconfigure`。

`postinst` **只在 ufw 处于 active 时**才加防火墙规则，不动 nftables/iptables；purge 时只删除安装时加过
的那三条（用数据目录里的标记判断）。

### 1b. 服务端：安装包（Windows）

不想手工发布 + `sc.exe` + `netsh`（也就是下面 2、3、6、8 节那些步骤）的话，用仓库里的服务端安装包。

```powershell
pwsh -File build/installer/windows/server/Build-ServerInstaller.ps1
# 产物：artifacts/installer/windows/RemoteDesktop-Server-Windows-x64-<版本>.msi
```

下面的命令里 `<版本>` 是**四段**版本（`Directory.Build.props` 的三段加 `.0`，例如 `1.0.0.0`）：Windows Installer
只认四段数字。Linux/macOS 的同名占位符仍是三段，见 §三 的版本形态表。以 `artifacts/installer/windows/` 里的
实际文件名为准，本文不再写死某一版。

安装时可以把该定的都定掉（全部可省略，省略即用下表默认值）：

```powershell
msiexec /i RemoteDesktop-Server-Windows-x64-<版本>.msi /qn `
  RENDEZVOUSURL="https://0.0.0.0:5080" `
  RELAYPUBLICENDPOINT="203.0.113.10:5082" `
  POSTGRESCONNECTIONSTRING="Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=..." `
  TLSCERTIFICATEPATH="C:\rd\signaling.pfx" TLSCERTIFICATEPASSWORD="..."
```

它做的事：把四个服务器（信令、中继、监控台和 Ops HTTPS 网关，各自自包含）装进
`%ProgramFiles%\Remote Desktop Server`，注册四个自动启动、崩溃后自动重启的服务
（`RemoteDesktopRendezvous`、`RemoteDesktopRelay`、`RemoteDesktopOps`、`RemoteDesktopGateway`），
加四条入站规则（TCP 5080、UDP 5081、UDP 5082、TCP 5443），并按下面的属性写好信令与中继的设置。
**真正生效的设置、数据库、TLS 公钥和 `server-setup.log` 在 `%ProgramData%\Remote Desktop Server\`**，
不在 Program Files：MSI 的大版本升级会先删除旧产品在 Program Files 里的文件，放在那里的
`appsettings.json` 会在每次升级时丢失。ProgramData 下的目录只有 SYSTEM 和 Administrators 能读。
设置由信令服务器自己写入（`--configure-system`），它把做过什么记在
`%ProgramData%\Remote Desktop Server\server-setup.log` 里——安装程序不会给任何人看这一步的输出。

监控台（Ops）没有需要填写的安装属性：它默认只监听 `127.0.0.1:5090`，数据库在
`%ProgramData%\Remote Desktop Server\ops.db`。同一安装包还会安装 Gateway，默认以
`https://0.0.0.0:5443` 对外提供 HTTPS；指定 `GATEWAYLISTEN` 可改监听地址。安装时会自动把本机信令/中继登记为
两个节点并打开信令的 `/metrics`，新装环境不需要手工添加节点。本机节点固定使用回环地址（`https://127.0.0.1:5080` /
`http://127.0.0.1:5083`）；没有配置 `Ops:NodeToken` 时，服务端的 `/metrics` 和 `/internal/*` 只接受回环调用，
用机器名或局域网地址访问会返回 401。**它没有默认账号**，装完要自己建第一个管理员（见 §二 10）：

```powershell
& "C:\Program Files\Remote Desktop Server\Ops\RemoteDesktop.Server.Ops.exe" --create-admin admin
Restart-Service RemoteDesktopOps
```

**以上命令要在“管理员 PowerShell”里执行。** 第一条管理员命令要读写 `%ProgramData%` 下只有
SYSTEM/Administrators 能访问的配置和数据库，`Restart-Service` 也需要管理员令牌；普通窗口会报
“无法打开计算机“.”上的 RemoteDesktopOps 服务”。第一条默认读写
`%ProgramData%\Remote Desktop Server\Ops\appsettings.json`，和服务是同一份配置；需要指到别处时加
`--config-directory`。

| 属性 | 默认 | 说明 |
|---|---|---|
| `RENDEZVOUSURL` | `http://0.0.0.0:5080` | 信令监听的地址。给了证书会自动变成 `https://` |
| `RELAYPUBLICENDPOINT` | `auto` | 客户端被要求使用的中继**地址**。`auto` 取本机第一个非回环地址；也可以写 `IP:端口`，或写域名（安装时解析一次，地址就此固定） |
| `RELAYSHAREDKEY` | `auto` | 中继票据的签名密钥。`auto`：没有就生成一个，**已经有就保持不动**——升级换掉它会让另一台机器上的中继被悄悄切断 |
| `DATABASEPROVIDER` | `auto` | 设备代码存在哪：`auto` / `none` / `postgres` / `sqlite`。`auto` = 本机已有 PostgreSQL 就继续用它，否则建一个 SQLite 库 |
| `DATABASEPATH` | 空 | SQLite 文件。空 = `%ProgramData%\Remote Desktop Server\rendezvous.db` |
| `POSTGRESCONNECTIONSTRING` | `auto` | PostgreSQL 连接串。`auto` 或空 = 不用 PostgreSQL，由 `DATABASEPROVIDER` 决定 |
| `TLSCERTIFICATEPATH` / `TLSCERTIFICATEPASSWORD` | `auto` / 空 | 给了就写进 Kestrel 配置并切到 https。填 `generate` 则由这台机器生成一张自签名证书、放进本机证书存储，公钥导出到 `%ProgramData%\Remote Desktop Server\tls\signaling.cer`（装完还能改：`Set-ServerConfiguration.cmd -TlsGenerate`）。**证书文件不存在时不切**（服务器还能起得来），只在 `server-setup.log` 里说明 |
| `TLSCERTIFICATEHOSTNAME` | 机器名 | 生成证书时写进 SAN 的 DNS 名/IP，逗号分隔。客户端用域名或 NAT 地址访问时必须填，否则即使信任了证书也会名称不匹配 |
| `NOFIREWALL` | `0` | 设成 `1` 就不加那三条防火墙规则 |
| `REMOVEDATA` | `0` | 卸载时设成 `1` 才删除 `%ProgramData%\Remote Desktop Server` 下的数据库、设置与生成的证书 |

**这些属性只在第一次安装时生效。** 升级/同版本重装会保留 ProgramData 下的现有设置（与 Linux/macOS 的
“升级不重新询问”一致）；要改请用 `Set-ServerConfiguration.cmd`（见“装完之后改配置”）。

四件必须知道的事：

- **安装包默认用 SQLite，装完就能用。** 不填连接串时设备代码存在本机的
  `%ProgramData%\Remote Desktop Server\rendezvous.db`，重启、升级都不变。**这个文件要备份**：删掉它等于
  所有设备码重发，无人值守的机器会因此失联；它也**只能放本地磁盘**，放网络共享上会损坏。
  要回到内存模式（每次重启换码）用 `DATABASEPROVIDER=none`。
- **中继装在另一台机器上时**，那把自动生成的密钥要抄过去：两边都传 `RELAYSHAREDKEY=<同一个值>`，
  或者把 `Rendezvous:RelaySharedKey` 的值填进那台的 `Relay\appsettings.json`。
- **改了端口就要自己改防火墙规则**：三条规则是按 5080/5081/5082 开的，`RENDEZVOUSURL` 或中继端口改了
  它们就对不上。

升级：同版本号重装即可，**`%ProgramData%\Remote Desktop Server` 下的设置、数据库和证书都保留**，
手改过的值和已经生成的密钥不会被包内默认值覆盖。
卸载用「应用和功能」或 `msiexec /x`，服务和防火墙规则一并移除；默认保留 ProgramData 下的数据与生成的证书
（重装后设备码不变）。要连同数据与证书一起删除：`msiexec /x ... /qn REMOVEDATA=1`。

#### macOS

```bash
build/installer/macos/server/make-pkg.sh --configuration Release
# 产物：artifacts/installer/macos/RemoteDesktop-Server-macOS-arm64-<版本>.pkg
```

装到 `/usr/local/remotedesktop-server`，并把四个服务器注册成 **launchd 守护进程**
（`com.remotedesktop.rendezvous` / `com.remotedesktop.relay` / `com.remotedesktop.ops` /
`com.remotedesktop.gateway`，开机自启、崩溃重启）—— 这就是 Windows 那四个服务的对应物。

> **已知边界**：这三个守护进程目前以 **root** 运行（plist 没有 `UserName`，安装脚本也没有创建专用账户）。
> Linux 包会创建 `remotedesktop` 账户并以它运行；macOS 包目前还没有做到这一点。把服务端放在公网前，
> 请按最小权限原则自行评估，或用一个普通账户手工加载 plist。

```bash
sudo installer -pkg <文件>.pkg -target /
curl http://127.0.0.1:5080/health      # 返回 Healthy 即两个网络服务器都活着
curl http://127.0.0.1:5090/health      # 监控台
sudo /usr/local/remotedesktop-server/Ops/RemoteDesktop.Server.Ops --create-admin admin
sudo launchctl kickstart -k system/com.remotedesktop.ops
```

**安装时选择配置（与 Linux 的 debconf 对应）。** `.pkg` 由安装器里的自定义面板收集答案，和 Linux 安装时
问的是同一组问题：

- **数据库**：SQLite（默认，文件在 `var/rendezvous.db`）或 PostgreSQL（填写连接串）。监控台跟随同一个
  provider。
- **TLS**：不使用（`http`）、生成自签名证书（可填客户端将使用的域名/IP），或使用已有证书
  （PKCS#12，或与 `.key` 同目录同名的 PEM）。
- 证书口令与连接串写进 `appsettings.json`（`root:wheel`，0644）——安装目录本机其他用户可读，敏感部署
  要另行收紧目录权限。安装器面板的答案在 `/var/tmp` 里只存在到 postinstall 读取为止。
- 升级时同一页默认勾选 **保留现有设置**，不会把运维改过的数据库、TLS 或地址覆盖掉。

**无人值守安装**（`sudo installer`、MDM）把同样的答案写到
`/Library/Application Support/RemoteDesktop/install-answers.plist`（`root:wheel`，0600）。这个文件在用完后
**不会删除**：下次没有面板答案的 `installer -pkg` 或 MDM 安装会再次应用它（`PreserveExisting=true` 除外）。
不再需要这套预设时请删掉它，否则它会覆盖现场运维后来的修改。

| 键 | 值 |
|---|---|
| `DatabaseProvider` | `sqlite` / `postgres` / `none` |
| `DatabasePath` | SQLite 文件（绝对路径） |
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
  别的机器要连这台，自己按需放通 TCP 5080 / UDP 5081 / UDP 5082。
- **卸载**：`.pkg` 不带卸载器，安装目录里有 `uninstall-server.sh`（停守护进程、删 plist、安装目录、
  文档目录与安装回执；`--keep-settings` 可只删程序）。
- **改配置**：安装目录里的 `configure-server.sh`，与 Windows 的 `Set-ServerConfiguration.cmd` 一一对应 ——
  同样的选项，跑的是同一个 `--configure-system` 步骤；它现在会重启**三个**守护进程（数据库 provider 与
  监控台共享）。

> 两个平台的服务器是**同一份可执行文件**、同一个 `--configure-system` 步骤，所以写出来的
> `appsettings.json` 与生成的中继密钥完全一致：换平台不需要换配置方式。
> 自签名证书既可以让安装器生成，也可以用 `build/signing/New-ServerCertificate.ps1` 生成后把 `.pfx`
> 交给 `configure-server.sh --tls-certificate`。公开部分总会导出到
> `/usr/local/share/doc/remotedesktop-server/signaling.crt`——生成的证书、PEM 或 `.pfx` 都一样，
> 客户端/被控端安装器可以直接选它；Ops 控制台也用这份 PEM 固定信令证书（`.pfx` 带口令，不能直接当 pin）。

### 装完之后改配置

安装向导里填的那些，装完还能改。安装目录（`%ProgramFiles%\Remote Desktop Server`）里有：

- **`Set-ServerConfiguration.cmd`** —— 推荐的路子。在**管理员**命令提示符或 PowerShell 里给它想改的项即可，
  没给的保持不动；它写完之后会登记/更新监控台的本机节点，然后重启三个服务让设置生效：

  ```powershell
  cd "C:\Program Files\Remote Desktop Server"
  .\Set-ServerConfiguration.cmd -DatabaseProvider sqlite
  .\Set-ServerConfiguration.cmd -PostgresConnectionString 'Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=...'
  .\Set-ServerConfiguration.cmd -TlsGenerate -TlsHostName rd.example.com   # 生成自签证书并切 https
  .\Set-ServerConfiguration.cmd -RelayPublicEndpoint 203.0.113.10:5082
  .\Set-ServerConfiguration.cmd -DatabaseProvider none -NoRestart   # 回到不用数据库，先不重启
  ```

  `.cmd` 只是用 `-ExecutionPolicy Bypass` 启动旁边的 `.ps1`，所以 Windows 11 客户端默认的 Restricted 策略
  不会挡住它；也可以直接 `powershell -ExecutionPolicy Bypass -File .\Set-ServerConfiguration.ps1 ...`。
  它调用与安装程序**同一个**配置步骤（`Rendezvous\RemoteDesktop.Server.Rendezvous.exe --configure-system`），
  所以两者的行为不会分叉；做过什么会追加到 `%ProgramData%\Remote Desktop Server\server-setup.log`。
  拿不准就先加 `-WhatIf` 看一眼（不打印口令）。
- **`how-to-configure.txt`** —— 同一件事的短说明，面向拿到机器的人（开始菜单里的入口叫「服务器配置说明」）。
- 三份 `appsettings.json`（`Rendezvous\`、`Relay\`、`Ops\`）在
  `%ProgramData%\Remote Desktop Server\` 下 —— 也可以直接改，改完重启对应服务。
  它们在升级时不会被覆盖，所以手改的值会保留；目录只有 SYSTEM 与 Administrators 能读。

### 数据库怎么选

设备代码（那 9 位数字）是无人值守访问的地址，它必须活过重启。两种存法：

| | SQLite（默认） | PostgreSQL |
|---|---|---|
| 装之前要准备 | 什么都不用 | 一个库、一个账号 |
| 适合 | 一台服务器 | 多台服务器，或已有备份与监控体系 |
| 多实例 | **不行**：每个实例一个文件，同一台设备会拿到不同的码 | 可以，几个实例共用一个库 |
| 备份 | 停服务后拷文件，或 `sqlite3 <db> ".backup out.db"` | `pg_dump` |
| 换到另一种 | 设备码全部重发（除非按下面搬数据） | — |

SQLite 文件的位置：Windows `%ProgramData%\Remote Desktop Server\rendezvous.db`、
macOS `/usr/local/remotedesktop-server/var/rendezvous.db`、Linux `/var/lib/remotedesktop-server/rendezvous.db`。
三件事要记住：

- **只放本地磁盘。** SQLite 在网络共享（NFS/SMB）上会损坏，这不是配置问题。
- **目录权限就是保护。** 目录是 0700 / 仅服务账号——能写这个文件的人可以改 `device_id`，等于能劫持设备码。
- **它是唯一的一份数据。** 删掉文件、或挂载点没挂上，服务会建一个空库并继续跑：所有设备会在下一次连接时
  拿到新码。`/health` 会因此变红（它查的是表在不在，不是连接通不通），但备份仍然只有你自己能做。

从 PostgreSQL 换到 SQLite 会重发所有设备码，反过来也一样，除非先把行搬过去。`device_id` 必须原样搬——
它就是那台设备的地址：

```bash
# 1. 先用 SQLite 启动一次，让服务把表建出来，然后停掉。
# 2. 导出、导入。created_at / last_seen_at 是 TEXT，原样搬即可。
psql "$PG" -c "\copy device_registrations TO 'devices.csv' CSV HEADER"
sqlite3 rendezvous.db ".mode csv" ".import --skip 1 devices.csv device_registrations"
# 3. 重启信令服务，curl 它的 /health（HTTP 或自签 HTTPS 加 -k）应为 Healthy。
```

### 2. 手工构建

```bash
dotnet publish src/Server/RemoteDesktop.Server.Rendezvous -c Release -r win-x64 --self-contained true -o artifacts/app/windows/server/Rendezvous
dotnet publish src/Server/RemoteDesktop.Server.Relay      -c Release -r win-x64 --self-contained true -o artifacts/app/windows/server/Relay
dotnet publish src/Server/RemoteDesktop.Server.Ops        -c Release -r win-x64 --self-contained true -o artifacts/app/windows/server/Ops
```

四个服务端各自一个子目录，因为它们是四个程序，装在同一台机器上时目录不能混。
`build/installer/windows/server/Build-ServerInstaller.ps1` 用的就是这个布局（见它的 `$PublishDirectory` 与
`-o (Join-Path $PublishDirectory $server.Folder)`），包里的 `Set-ServerConfiguration.cmd`/`.ps1`、`how-to-configure.txt`
与信令/中继的两份 `appsettings.json` 也在这一层；控制台自己那份在 `Ops\appsettings.json`。

（Linux/macOS 把 `-r win-x64` 换成 `linux-x64` / `osx-arm64` 等，并把 `artifacts/app/windows/` 里的 `windows`
换成对应平台 —— 服务端是跨平台的。产物目录的约定见 [CONTRIBUTING.md](../../CONTRIBUTING.md) 的"构建产物布局"。）

### 3. 配置

`appsettings.json` 与各自的可执行文件放在一起。

`RemoteDesktop.Server.Rendezvous/appsettings.json`：

```jsonc
{
  "Urls": "http://0.0.0.0:5080",                // 信令监听地址，WebSocket 挂在 /ws，健康检查在 /health
  "Rendezvous": {
    "ReflectorPort": 5081,                      // UDP 地址反射端口：告诉双方彼此看到的公网地址
    "RelayEndpoint": "你的服务器公网地址:5082",   // 必须写客户端能连到的地址，不能是 0.0.0.0 或 127.0.0.1
    "RelaySharedKey": "<和 Relay 相同的长随机串>", // 生产环境必须换掉开发默认值（appsettings.Development.json 里是明文的开发密钥）
    "RelayTicketLifetime": "00:02:00",
    "ConnectTimeout": "00:00:20",
    "MetricsEnabled": false                     // 打开后在 /metrics 暴露 Prometheus 指标；默认关闭，用法见"健康检查"一节
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
**证书里的域名要和客户端填的地址一致**：客户端走系统信任链校验，没有对信令服务器做指纹固定。
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
并且**只代理 `/ws` 与 `/health`**：`/internal/v1/*` 是给控制台用的，不配 `NodeToken` 时只接受回环调用，
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

也可以在安装时选择服务器证书（见 §四）：那条路不会修改系统受信任根，而是把公钥固定到
`%ProgramData%\RemoteDesktop\certificates\signaling.cer`，只信任这一个服务器证书，范围比装进根证书更窄。
两种方式可以并存；`-ServerCertificate` 仍然适合希望整台机器信任该自签 CA 的部署。

### 5. 公网上的限额

默认值按"公网上的自建服务"选的，除非你的规模和它差很远，否则不用动。被限流的事件以 Warning 写进日志，
**带来源地址**——这是服务器唯一把地址写下来的地方，专门为了让你能看出有人在滥用。

| 设置 | 默认 | 管什么 |
|---|---|---|
| `Rendezvous:MaxConnections` | 4096 | 同时服务多少台设备；超出的在 WebSocket 升级前就被拒，不占内存 |
| `Rendezvous:MaxConnectionsPerAddress` | 256 | 一个来源地址最多占多少条连接 |
| `Rendezvous:RegistrationBurst` / `RegistrationRefill` | 256 / 5 秒 | 一个来源地址注册设备的速度。注册就是领一个身份，而"每设备"的限额都以身份计数，所以这条最要紧 |
| `Rendezvous:ConnectRequestBurst` / `ConnectRequestRefill` | 10 / 3 秒 | 一台设备发起连接请求的速度（防止拿它去轰炸别人的弹窗） |
| `Rendezvous:MaxPendingRequestsPerDevice` | 8 | 一台设备同时挂起的连接请求数 |
| `Rendezvous:MaxPendingRequests` | 4096 | 全服务器同时挂起的连接请求数 |

### 6. 作为服务运行

以**管理员** PowerShell 执行（两个可执行文件都能直接作为 Windows 服务运行，同一份文件也能当控制台程序跑）：

```powershell
sc.exe create RemoteDesktopRendezvous binPath= "\"C:\rd\rendezvous\RemoteDesktop.Server.Rendezvous.exe\"" start= auto
sc.exe create RemoteDesktopRelay      binPath= "\"C:\rd\relay\RemoteDesktop.Server.Relay.exe\""           start= auto
sc.exe failure RemoteDesktopRendezvous reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc.exe failure RemoteDesktopRelay      reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc.exe start RemoteDesktopRendezvous
sc.exe start RemoteDesktopRelay
```

- 作为服务运行时日志走**Windows 事件日志**（"应用程序"日志，来源是服务名），配置文件仍读可执行文件旁边的 `appsettings.json`。
- 想同时看控制台输出，就以控制台方式运行（`.\RemoteDesktop.Server.Rendezvous.exe`），日志打到标准输出。
- Linux 用 systemd 单元调用同样的可执行文件即可（`Restart=always`）；服务生命周期那一层在非 Windows 上是空操作。

### 7. 用 Docker 部署（Linux 服务器首选）

仓库里已经准备好四个镜像和一组 compose 文件（`build/docker/`），从源码在容器里构建，运行镜像基于 .NET 10
运行时（不装 SDK）。四个镜像分别是信令、中继、监控台和 Ops HTTPS 网关；监控台与网关是可选 overlay。

```bash
cd build/docker
cp .env.example .env      # 填好四个必填值；启用 Ops 时再填 OPS_GATEWAY_IP
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

两个网络服务器都会在**启动时校验密钥**：`RELAY_SHARED_KEY` 缺失、太短（少于 32 字节）或不是 base64 时，
容器会立刻退出并说明原因，不会带着弱密钥跑起来。`RELAY_PUBLIC_ENDPOINT` 必须是客户端可达的地址；
写成 `127.0.0.1:5082` 只有所有东西都在同一台机器上时才对。

#### 公网：用 TLS overlay

默认 compose 把信令发布在 `127.0.0.1:5080`，公网客户端连不到它。公网部署用 Caddy overlay 终止 TLS：

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

客户端用 `ws://<服务器>:5080/ws`。这是**明文**：设备码、在线状态、谁在连谁以及中继票据都在线上可读。不要把它
用在公网；公网请用上一节的 TLS overlay。

#### 端口与防火墙

- `5080/tcp`：信令 + `/health`，默认只绑回环；TLS overlay 通过 Caddy 暴露 443。
- `5081/udp`：地址反射。**必须放通**，否则直连打洞拿不到自己的公网地址。
- `5082/udp`：中继。**必须放通**，否则直连失败的连接没有退路。
- `5443/tcp`：加载 `docker-compose.ops.yml` 后的 Ops HTTPS 网关。Ops 的 5090 不再发布到宿主机。
- 中继的 HTTP 面（`/health`、`/metrics`，见 `Relay:HttpEndpoint`）默认只在容器内回环，不对外发布。
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
控制台  Ops:NodeToken
```

三处必须同值。只设 `Ops:NodeToken` 时控制台会带着 token 去请求，而节点因为没有配置而只接受回环调用：跨机或
容器里的控制台会 401（同机回环仍然可用，但那是回环豁免，不是 token 生效）。

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

把导出的公钥按本节前面的三平台步骤安装到每台运维电脑。证书有效期三年；在到期前 30 天内重启 gateway 容器即可
重新签发。`OPS_GATEWAY_IP`、`OPS_GATEWAY_DNS` 或网络配置改变后，也需要删除证书卷并重新生成，然后重新分发证书：

```bash
docker compose -f docker-compose.yml -f docker-compose.ops.yml down
docker volume rm remotedesktop_ops-gateway-data
docker compose -f docker-compose.yml -f docker-compose.ops.yml up -d
```

首次登录用刚建的管理员（口令 + TOTP）。两个节点会在控制台启动前自动登记（幂等），默认 baseUrl 为：

```text
rendezvous   http://rendezvous:5080
relay        http://relay:5083
```

可用 `OPS_RENDEZVOUS_NODE_URL` / `OPS_RELAY_NODE_URL` 覆盖；用 `docker-compose.selfsigned.yml` 时把前者设为
`http://rendezvous:5082`（内部明文口）。因为 Compose 网络是隔离的，overlay 同时显式开启
`Ops:AllowInsecureNodeHttp`，允许节点令牌在该内部 HTTP 网络上传输；这仍是默认关闭的公网安全边界。

`OPS_INGEST_KEY` 是节点与控制台共享的 HMAC 密钥（Base64，至少 32 字节），不设置 overlay 不会启动。Ops 只信任
`OPS_GATEWAY_SUBNET`（默认 `172.30.0.0/24`）上的 Gateway，这个专用网络只有 Ops 和 Gateway 加入；不要把它换成整个
Docker bridge 网段，否则其他容器可以伪造 `X-Forwarded-For`。会话、告警与审计数据存在 PostgreSQL（与设备注册表
同一个库，表名以 `ops_` 开头）；备份 `postgres-data` 或 `pg_dump` 一次覆盖两者。控制台的 DataProtection 键存在
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

```powershell
netsh advfirewall firewall add rule name="RemoteDesktop Rendezvous" dir=in action=allow protocol=TCP localport=5080
netsh advfirewall firewall add rule name="RemoteDesktop Reflect"    dir=in action=allow protocol=UDP localport=5081
netsh advfirewall firewall add rule name="RemoteDesktop Relay"      dir=in action=allow protocol=UDP localport=5082
```

### 9. 健康检查

```bash
# Windows / macOS 包默认走 HTTP：
curl http://127.0.0.1:5080/health      # 或浏览器打开，返回 200 即正常（不需要认证）
# Linux 包默认自签 HTTPS（RD_TLS=generate），对自签证书要加 -k：
curl -k https://127.0.0.1:5080/health
```

**监控指标（可选，默认关闭）**：把 `Rendezvous:MetricsEnabled` 设为 `true` 后，服务器会额外挂载 `/metrics`，
按 Prometheus 文本格式（`text/plain; version=0.0.4`，指标名都以 `remotedesktop_` 开头）给出：当前连接数、
在线设备数（瞬时值），以及注册、连接请求、在线状态查询各自被接受和被限流的次数（`*_total`，只增不减，适合
`rate()` 出图）。

默认关闭，而且关闭时这个路径**根本没有挂载**，访问返回 404：这些数字本身就说明了这台服务器上现在有多少台
设备、拒绝了多少请求，公网上谁都能读到，是运营者自己的信息，所以打开是一个有意的动作。打开后应当配一个只有
自己的监控能到的入口，例如不动 `Urls`、让 Prometheus 从本机或内网抓 `http://127.0.0.1:5080/metrics`
（反向代理只转发 `/ws` 和 `/health` 时，`/metrics` 自然到不了公网），或者用防火墙只放行监控的来源地址：

```yaml
scrape_configs:
  - job_name: remotedesktop
    static_configs:
      - targets: ["127.0.0.1:5080"]
```

### 10. 运维监控台（可选）

控制台（`RemoteDesktop.Server.Ops`）是独立于信令与中继的第三个服务，用来**看**数字和**管**设备与会话。不装它，
远程桌面照常工作。控制台自身默认**只监听 `127.0.0.1:5090`**。三平台服务端安装包同时安装
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
`%ProgramData%\Remote Desktop Server\ops.db`）。它不需要单独安装，也没有必须填写的安装属性。

```bash
# Linux — the console keeps its settings in /etc, not next to the program
sudo -u remotedesktop /usr/lib/remotedesktop-server/Ops/RemoteDesktop.Server.Ops \
  --create-admin admin --config-directory /etc/remotedesktop-server/Ops
sudo systemctl restart remotedesktop-ops

# macOS
sudo /usr/local/remotedesktop-server/Ops/RemoteDesktop.Server.Ops --create-admin admin
sudo launchctl kickstart -k system/com.remotedesktop.ops
```

```powershell
# Windows（管理员 PowerShell）
& "C:\Program Files\Remote Desktop Server\Ops\RemoteDesktop.Server.Ops.exe" --create-admin admin
Restart-Service RemoteDesktopOps
```

Docker 用可选的 ops overlay（见 §二 7）：

```bash
cd build/docker
export OPS_GATEWAY_IP=192.168.1.10   # required by the overlay; use the host address administrators reach
docker compose -f docker-compose.yml -f docker-compose.ops.yml run --rm -it ops --create-admin admin
```

#### 首个管理员

没有默认口令、没有网页初始化入口。`--create-admin <用户名>` 交互式设口令，打印 TOTP 密钥与 `otpauth://` URI，
导入验证器即可。**密钥只显示这一次**；丢失后用 `--reset-totp <用户名>` 重置（或让另一位管理员在控制台重置），
不需要再建一个账号。口令之后还要过 TOTP 二次验证才能登录（`Ops:RequireTotp=true` 默认开着，别关）。

#### 账号管理

管理员登录后侧栏有「账号管理」页，可以新建 `viewer` / `operator` / `admin`、改口令、重置 TOTP、停用/启用和删除。
**每一种改密、重置或停用都会立刻撤销该账号的所有会话**，下一个请求就要重新登录。最后一个可用的管理员不能被停用
或删除（否则就没有人能再登录）；管理员也不能停用或删除自己。所有变更都写审计。

控制台进不去时，命令行做同样的事：

```bash
RemoteDesktop.Server.Ops --reset-password <用户名> [--config-directory <目录>]
RemoteDesktop.Server.Ops --reset-totp <用户名> [--config-directory <目录>]
```

两条命令都会撤销该账号的现有会话。`--create-admin`、`--reset-password`、`--reset-totp` 默认读服务的那份
`appsettings.json`（Windows `%ProgramData%\Remote Desktop Server\Ops`，Linux
`/etc/remotedesktop-server/Ops`，macOS 安装根下的 `Ops`）；显式传入的 `--config-directory` 不存在时会直接
说明，而不是落到另一个数据库。容器没有 `/etc` 副本时回退到程序目录（compose 的数据库配置来自环境变量）。

#### 关键配置（`Ops` 段）

| 配置项 | 默认 | 说明 |
|---|---|---|
| `Ops:Listen` | `http://127.0.0.1:5090` | 控制台自身的 Kestrel 监听地址；保持回环，对外只经过 Gateway |
| `Gateway:Listen` | `https://0.0.0.0:5443` | 安装包内置的 HTTPS Gateway 监听地址；证书自动生成并包含本机 IPv4 IP SAN |
| `Gateway:Upstream` | `http://127.0.0.1:5090` | Gateway 的固定回环上游；安装步骤每次都会重写，避免被改成任意外部代理 |
| `GATEWAYHOSTNAME` / `--gateway-hostname` | 空 | 安装参数：给生成证书追加 IP/DNS；本机 IPv4 已自动包含 |
| `Ops:Database:Provider` / `Path` | `sqlite` / `ops.db` | 控制台自己的库；换成 `postgres` 时填 `ConnectionString`。Docker 的 ops overlay 用 PostgreSQL，与设备注册表同库 |
| `Ops:RequireTotp` | `true` | 口令之后强制 TOTP |
| `Ops:NodeToken` | 空 | 控制台调节点内部 API 的 Bearer；空 = 节点只接受回环调用。必须与 `Rendezvous:NodeToken`、`Relay:NodeToken` 同值 |
| `Ops:AllowInsecureNodeHttp` | `false` | 允许节点令牌经非回环明文 HTTP 发送；仅用于 Compose 等隔离网络，公网节点保持关闭 |
| `Ops:ScrapeIntervalSeconds` | `10` | 多久抓一次节点指标 |
| `Ops:AlertIntervalSeconds` | `15` | 多久求值一次告警规则 |
| `Ops:ActionStaleSeconds` | `60` | 处于 `approved` 超过这个秒数的动作由恢复任务标记失败并写审计；进程在批准与执行之间崩溃时不会永久卡住 |
| `Ops:Notifications:WebhookUrl` | 空 | 告警开/恢复各 POST 一次 JSON；空 = 不通知。失败后按 1/2/4/8 分钟重试，最多 5 次 |
| `Ops:TrustedProxies` | 空 | 可信反向代理的 IP 或 CIDR 列表；只有这里的代理写来的 `X-Forwarded-For` / `X-Forwarded-Proto` 会被采信。见“反向代理” |
| `Ops:Retention:SampleDays` / `EventDays` / `SessionDays` / `AlertDays` | `30` / `30` / `90` / `180` | 指标样本、事件、会话、已恢复告警的保留天数；`0` 关闭对应清理。审计日志不自动删除 |
| `Ops:Retention:IntervalMinutes` | `60` | 清理任务间隔 |
| `Ops:Ingest:Keys:<id>` | 空 | 节点推事件的 HMAC 密钥（Base64）；见下 |

> 内部 API 的 token 不是 `Ops` 段独有的：控制台读 `Ops:NodeToken`，信令读 `Rendezvous:NodeToken`，中继读
> `Relay:NodeToken`。三处必须同值；只设 `Ops:NodeToken` 时，跨机或容器里的控制台会 401（同机回环仍然可用，
> 但那是回环豁免，不是 token 生效）。Docker 的基础 compose 已经把同一个 `NODE_TOKEN` 传给两个节点。

#### 反向代理

服务端安装包自带一个专用 Gateway：它只监听 HTTPS，固定转发到 `http://127.0.0.1:5090`，覆盖客户端伪造的
`X-Forwarded-*` 头，并把 `127.0.0.1` 写入 `Ops:TrustedProxies`。Windows 的 MSI 会为 TCP 5443 添加规则；
Linux 的 `/etc/default/remotedesktop-server` 用 `RD_GATEWAY_LISTEN`、`RD_GATEWAY_HOSTNAME` 配置，并在
`RD_OPEN_FIREWALL=1` 时放行 5443；macOS 的 `configure-server.sh` 使用 `--gateway-listen` 和
`--gateway-hostname`。

自签名证书公钥的位置：

- Windows：`%ProgramData%\Remote Desktop Server\tls\signaling.cer`
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
Copy-Item '\\<服务器>\C$\ProgramData\Remote Desktop Server\tls\signaling.cer' .\ops-gateway.cer
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
certutil -d sql:$HOME/.pki/nssdb -A -t "C,," \
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

如果部署不使用内置 Gateway，控制台仍可只监听回环，远程访问用 SSH 隧道或自行维护的 TLS 反向代理。用外部代理时
**必须**把代理地址写进 `Ops:TrustedProxies`，并让代理写 `X-Forwarded-For` 与 `X-Forwarded-Proto`：

```json
"Ops": { "TrustedProxies": [ "127.0.0.1" ] }
```

不配时，控制台看到的是代理的地址：审计日志和登录失败预算都会按代理计，TLS 终止在代理时
`Request.IsHttps` 为 false，会话 Cookie 也不会带 `Secure`。框架自带的“相信回环代理”默认被移除——本机任何
进程都能连回环，不能因为连接来自 `127.0.0.1` 就相信它写的头。

`/health` 只给本机用：连接必须来自回环，且请求不能带任何转发头。反向代理**不要**转发 `/health`；即使转了，
带 `X-Forwarded-For` / `Forwarded` 的请求也会得到 404。

#### 数据保留

控制台每小时清理一次过期历史：默认保留 30 天指标样本、30 天事件、90 天会话、180 天已恢复告警，并删除过期的
管理员会话。打开中的告警永远不会被删除。`Ops:Retention:*` 可以按部署调整；设为 `0` 表示不清理那一类。
**审计日志（`ops_audit_log`）是有哈希链的，不自动删除**——需要更长的保留期就备份数据库，或定期整库归档。

#### 让总览出现数字：三件事缺一不可

1. **节点能上报指标**：信令服务设 `Rendezvous:MetricsEnabled=true`；中继默认就有 `/metrics`。Docker 的 ops overlay 已为信令打开。
2. **控制台里登记节点**：原生安装包会自动登记本机节点（信令 `http://127.0.0.1:5080`、中继 `http://127.0.0.1:5083`）；Docker overlay 也会在启动前自动登记服务名节点。手工部署在节点管理页添加即可。
3. **事件通道**：信令/中继把会话事件推给控制台，三处要同一个 HMAC 密钥。原生 Windows / Linux / macOS
   Server 安装包会自动生成 32 字节密钥并写好三处配置；升级会补齐旧安装缺少的配置，但不会轮换已有密钥：

   ```text
   # 信令服务、中继服务的 appsettings.json（或环境变量）
   "OpsEvents": { "Endpoint": "http://127.0.0.1:5090", "Key": "<自动生成的 Base64>", "KeyId": "k1" }
   # 控制台
   "Ops": { "Ingest": { "Keys": { "k1": "<同一个 Base64>" } } }
   ```

   Docker 的 ops overlay 用环境变量 `OPS_INGEST_KEY` 配这一组值，两边的 Endpoint 是 `http://ops:5090`。
   额外安装在其他机器上的 Relay 必须显式使用中央 Ops 的地址和同一密钥，可通过 `--ops-events-endpoint` 与
   `RD_OPS_INGEST_KEY` 传给 `--configure-system`；不能让那台机器自行生成另一把密钥。
   密钥不配，控制台收不到任何事件——`/ingest/events` 返回 503，而不是收到伪造数据。会话、链路、告警、心跳
   全是这条通道进来的；只有节点指标走抓取那条。

#### 管理动作与审计

- **断开会话 / 封禁设备**要两个人：一个发起、一个批准，发起人不能批准自己的动作；两步各留一条审计。
- 断开会话需要被控端开着 `Telemetry:Enabled`（否则控制台不知道被控端所知的会话 id）。
- 审计日志是哈希链，`审计日志 → 校验链` 能指出第一条被改动的记录。
- 构成与设计见 §十一。

## 三、自动化发布到 GitHub Releases

项目已经配置好了 GitHub Actions 自动化流水线 (`.github/workflows/release.yml`)，当您打上 `v` 开头的 Tag 时，云端会自动跑测试、打包，并把**九个**安装包放进同一个 Release——三个平台各出三个角色，装的机器不同，版本是同一个。

同一个 `<Version>` 在三个平台上的最终形态不同，这是安装包格式决定的，不是漂移：

| 平台 | 包内版本 | 构建产物名 | 发布资产名 |
|---|---|---|---|
| Windows | `1.0.0.0`（三段 + `.0`，MSI 只认四段数字） | `RemoteDesktop-Client-Windows-x64-1.0.0.0.msi` | `RemoteDesktop-Client-Windows-x64-v1.0.0.msi` |
| macOS | `1.0.0`（`CFBundleShortVersionString`、`CFBundleVersion`、`pkgbuild --version` 同值） | `RemoteDesktop-Client-macOS-arm64-1.0.0.pkg` | `RemoteDesktop-Client-macOS-arm64-v1.0.0.pkg` |
| Linux | `1.0.0`（`control` 的 `Version`） | `RemoteDesktop-Client-Linux-amd64-1.0.0.deb` | `RemoteDesktop-Client-Linux-amd64-v1.0.0.deb` |

三段的源只有 `Directory.Build.props`；Windows 的第四段由打包脚本追加，macOS/Linux 不改写。发布资产名用 Tag，所以包内版本与资产名不会逐字相同。`1.0.0-rc.1` 这类后缀不受支持：MSI 装不下，macOS 的 bundle version 也不接受。Linux 的 `Version` 不带 Debian revision（是 `1.0.0`，不是 `1.0.0-1`）：这是自发布包；若将来由发行版重打包，revision 由打包者追加。Windows 文件属性里的“产品版本”也是三段；提交哈希只出现在遥测的 `Full` 里（`AssemblyMetadata("Commit")`），不在文件元数据里。

| 产物 | 平台 | 装的是什么 |
|---|---|---|
| `RemoteDesktop-Client-Windows-x64-<tag>.msi` | Windows | 控制端；同一个包也装被控端服务与会话 Agent（一台机器两种角色都要时用它） |
| `RemoteDesktop-Host-Windows-x64-<tag>.msi` | Windows | 被控端：只有服务与会话 Agent，没有客户端窗口 |
| `RemoteDesktop-Server-Windows-x64-<tag>.msi` | Windows | 服务端（Rendezvous + Relay + Ops） |
| `RemoteDesktop-Client-macOS-arm64-<tag>.pkg` | macOS | 控制端（同一个 `.app` 也能被控，但不会自动启动） |
| `RemoteDesktop-Host-macOS-arm64-<tag>.pkg` | macOS | 被控端：独立的 `Remote Desktop Host.app`（无窗口，装在 `/Library/Application Support/RemoteDesktop/Host`，不出现在“应用程序”）+ 登录项 |
| `RemoteDesktop-Server-macOS-arm64-<tag>.pkg` | macOS | 服务端（Rendezvous + Relay + Ops） |
| `RemoteDesktop-Client-Linux-amd64-<tag>.deb` | Linux | 控制端 |
| `RemoteDesktop-Host-Linux-amd64-<tag>.deb` | Linux | 被控端 |
| `RemoteDesktop-Server-Linux-amd64-<tag>.deb` | Linux | 服务端（Rendezvous + Relay + Ops） |

命名约定（角色词一律写全；架构词各平台用自己的说法，Linux 那个与包内 `Architecture` 字段同源）见
[`CONTRIBUTING.md`](../../CONTRIBUTING.md) 的"构建产物布局"。

同一个平台上的包版本号相同。三个角色各自装在不同的机器上是常态；Windows 的客户端包与被控端包共用
`RemoteDesktopHost` 服务名，**一台机器只能装其中一个**（装第二个会被安装程序拒绝），而服务端包和它们互不影响。

发布前要准备好下面这些。**没有哪一件是必填的**，但流水线会根据你配了什么调整产物，并把结果写在 Release 说明
最前面：

- **服务器地址**（可选）：配了 `DEFAULT_RENDEZVOUS_URI`，客户端安装包就内置这个地址，下载的人装完直接可用；
  不配就是"不内置"，见"配置默认服务器地址"那一节。
- **代码签名证书**（可选，但强烈建议）：不配置也能出包（流水线跳过签名），但没签名的包在目标机器上过不了
  UIAccess，见"准备代码签名证书"那一节。
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

*(如果不配置这些 Secrets，流水线会自动跳过签名步骤并正常打包)*

配置好之后，流水线用这个 `.pfx` 给客户端的 exe/dll 和 MSI 签名（Windows 自带的方式，不需要额外装工具）。
如果这张证书是**自签名**的，构建还会把它的公开部分一起打进 MSI：安装时安装程序会把它装进目标机器的
"受信任的根证书颁发机构"，卸载时移除——UIAccess 要求签名链能被本机信任，这一步让装好的机器立刻满足这个条件，
不需要再手工 `certutil -addstore`。CA 签发的证书不会被这样做（它的链本来就受信任）。详见
[build/signing/README.md](../../build/signing/README.md)。

### 2. 配置默认服务器地址（可选）

在 GitHub Repository -> **Settings** -> **Secrets and variables** -> **Actions** -> **Variables** 标签页下，新建一个变量：

- `DEFAULT_RENDEZVOUS_URI` = `wss://您的域名:5080/ws`

配了：这个地址会被编进客户端安装包，下载的人装完就能连上你的服务器，不用自己填。

**不配也完全可以发布**：出的包不内置地址（客户端默认连本机），装完后在客户端里用「修改服务器地址」填自己的
服务器就行——它会同时写给客户端和被控端服务，并重启两边。客户端连不上时界面会直接显示"○ 未连接服务器
（当前地址）"以及去哪儿改，所以不是一个不声不响的坏包。

本软件公网和内网都要用，服务器地址往往是**部署的时候**才定的，所以"不内置"是正常选择，不是漏配。
两种情况下 Release 说明的最前面都会写清楚这一版是哪种。

地址用 `wss://` 就要求服务器自己有 TLS 证书（见第 4 节）；局域网里用自签证书时，构建客户端安装包加
`-ServerCertificate` 把服务器证书的公钥打进包并装进受信任根，就不用每台机器手工导入。

### 3. 升版本并触发自动发布

版本只有一处：仓库根 `Directory.Build.props` 的 `<Version>`。九个安装包、`.app` 的 `Info.plist` 与遥测都从它派生，
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
都会在打包前失败。之后 GitHub Actions 拉三个平台各跑测试、打包，几分钟后您的 Releases 页面会出现**九个**包，
名字统一是 `<产品>-<角色>-<平台>-<架构>-<tag>`（例如 `RemoteDesktop-Client-Windows-x64-v1.0.0.msi`）。

服务器地址现在可以在安装时填写；构建时给的 `-RendezvousUri` 只是安装页和静默安装的默认值。

## 四、部署客户端 / 被控端（MSI）

### 1. 构建安装包时给出默认服务器地址

先按 §八 把包装出来。**构建时最好给 `-RendezvousUri`**：不加时默认是
`ws://127.0.0.1:5080/ws`，脚本会给出警告。安装时可以在图形界面第一页改，或者给静默安装传
`RENDEZVOUSURI`。

### 2. 安装

下面的 `<版本>` 同样是四段版本（`Directory.Build.props` 的三段加 `.0`，例如 `1.0.0.0`），以实际产物文件名为准。
图形界面会先问服务器地址；地址是 `wss://` 且服务器使用自签名证书时，再选择它的公钥证书。host 包随后还会问无人值守密码。

```powershell
# 图形界面（推荐给一般用户）
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi

# 静默安装，明文 ws://
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="ws://192.168.1.10:5080/ws"

# 静默安装，wss:// + 自签名服务器证书（PEM/DER 公钥）
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="wss://rd.example.com:5080/ws" SERVERCERTIFICATEPATH="C:\certs\signaling.cer"

# wss:// 使用公开 CA 签发的证书：不要传 SERVERCERTIFICATEPATH
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="wss://rd.example.com:5080/ws"

# 静默安装到别的目录
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn INSTALLFOLDER="D:\Apps\RemoteDesktop" RENDEZVOUSURI="ws://192.168.1.10:5080/ws"
```

`SERVERCERTIFICATEPATH` 只在地址是 `wss://` 时使用，且应指向**公钥**（`.cer`/`.crt`/`.pem`/`.der`，PEM 或 DER）。
安装器只把公钥导出到 `%ProgramData%\RemoteDesktop\certificates\signaling.cer`，在
`%ProgramData%\RemoteDesktop\appsettings.json` 里写 `Transport:RendezvousCertificatePath`，**不会修改系统信任库**，
也不会把源文件里的私钥复制过去。地址是 `ws://` 时即使传了证书也不会导入；路径不是绝对路径、文件不存在或不是可读的
PEM/DER 证书时安装会失败。使用公开 CA 证书时留空即可，客户端走系统信任链。

只做被控端、不需要客户端窗口的机器，装 `RemoteDesktop-Host-Windows-x64-<版本>.msi`：同样的服务与会话 Agent、同样的
`RENDEZVOUSURI`/`SERVERCERTIFICATEPATH` 属性，只是没有客户端与开始菜单项。它和客户端包装在同一个目录、用同一个
`RemoteDesktopHost` 服务与 `%ProgramData%\RemoteDesktop` 设备身份，所以**一台机器只装其中一个**：想同时具备
"控制别人"与"被别人控制"两种角色，只装**客户端**包就够了（它本来就带被控端）；Host 包是给"只能被控、不允许控制
别人"的机器用的。装第二个时安装程序会明确拒绝，先卸掉已有的那个再装。

host 没有安装后的窗口，所以**服务器地址、TLS 证书和无人值守密码都在安装程序里设置**：连接页问地址和证书，下一页问
密码。安装期设置对 **host** 尤其重要；之后改地址可以再运行安装程序（图形界面会在连接页带出当前值），或手工运行
`RemoteDesktop.HostService.exe --configure-system --rendezvous ...`，再重启服务。

```powershell
# host，明文
msiexec /i RemoteDesktop-Host-Windows-x64-<版本>.msi /qn RENDEZVOUSURI="ws://192.168.1.10:5080/ws"

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
3. **改了配置要重启服务**：`Restart-Service RemoteDesktopHost`（配置只在启动时读一次）。
4. **看日志**：`C:\ProgramData\RemoteDesktop\logs\service-<日期>.log`。
   `Rendezvous connection lost: Unable to connect to the remote server` 就是在说"连不上信令服务器"。
5. **服务根本没起来**（`Get-Service RemoteDesktopHost` 显示已停止，或者客户端说"无法联系后台服务"）：
   先看同目录下的 `startup-error.log` —— 配置读不出来时，服务会在启动前把原因写在这里并以退出码 3 结束，
   而不是留下一个没人看得懂的崩溃。客户端对应的文件是 `%AppData%\RemoteDesktop\crash.log`，
   同时会在屏幕上弹一个说明原因的对话框。
6. 服务起来了吗：`Get-Service RemoteDesktopHost`。

### 4. 升级与卸载

```powershell
# 升级（保留设备身份、日志和 ProgramData 下的服务器地址）
msiexec /i RemoteDesktop-Client-Windows-x64-<版本>.msi /qn

# 卸载（保留设备身份和日志，重装后设备代码不变）
msiexec /x RemoteDesktop-Client-Windows-x64-<版本>.msi /qn

# 卸载并删除设备身份与日志（这台机器下次安装会得到新的设备代码）
msiexec /x RemoteDesktop-Client-Windows-x64-<版本>.msi /qn REMOVEDATA=1
```

- **图形界面升级**：连接页会带出当前地址和已固定的证书，可以改；离开这一页后安装程序才会应用新值。
- **静默升级**：不运行界面，现有地址、证书和设备身份都保留；`RENDEZVOUSURI`/`SERVERCERTIFICATEPATH` 只在全新安装时
  或图形界面升级时生效。要换自签名证书（例如服务器证书续期后），用**新的安装包**（新版本或至少一次新的构建，具有新的
  ProductCode）的图形界面重新运行，在连接页选择新的公钥；同一个 MSI 的修复安装不会重新走连接页。也可以先卸载再用新参数安装。
- 卸载默认保留 `%ProgramData%\RemoteDesktop`（地址、证书固定和设备身份）；`REMOVEDATA=1` 会一并删除。

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

打包脚本自动在当前用户和本机证书存储里找可用的代码签名证书（有私钥、未过期、用途是代码签名），
**只自动使用名字里含 `RemoteDesktop`** 的证书——别的产品的证书不会被默默拿来用，那种情况会列出候选让你指定：

```powershell
... -SignCertificate <证书指纹>          # 或 -SignCertificate "CN=你的公司"
... -SkipSigning                          # 明确不签名
```

签名分两步：先签发布目录里本产品自己的 exe/dll（这样**装到机器上的程序**也带签名），再签 MSI。

不签名时 SmartScreen 和企业策略可能拦住安装；自签名证书在装过它 `.cer` 的机器之前只会得到一条"不受信任的根"的
**警告**，构建不会失败。完整说明（怎么买证书、有哪些替代方案、怎么检查签名、osslsigncode 怎么用）见
[`build/signing/README.md`](../../build/signing/README.md)；
本地测试签名用 `build/signing/windows/New-SigningCertificate.ps1` 生成证书、`build/signing/windows/Install-TrustedCertificate.ps1` 让机器信任它。

## 五、部署客户端 / 被控端（Debian 包，Linux）

### 1. 构建

控制端与被控端两个包各装各的：一台机器按需要选一个，或者两个都装。

```sh
build/installer/linux/client/build-deb.sh   # 用来控制别人
build/installer/linux/host/build-deb.sh     # 允许被别人控制
```

`build-deb.sh` 自包含发布，所以**目标机器不需要装 .NET**，也不需要 `LD_LIBRARY_PATH`——
`libmsquic` 随包放在可执行文件旁边，那是自包含程序会找的地方、别的程序不会找的地方。

### 2. 服务器地址与证书

**安装时会问**（`dpkg -i` 也会问）：信令服务器地址，形如 `ws://主机:5080/ws` 或 `wss://主机:5080/ws`；
当地址是 `wss://` 且服务端用自签证书时，再问证书文件（PEM）。答案是：

- `ws://`：不需要证书；服务端也没开 TLS。
- `wss://` + 公共 CA 签的证书：证书问题留空。
- `wss://` + 自签证书：把服务端 `/etc/remotedesktop-server/tls/signaling.crt` 拷到本机，安装时填写
  它的路径；包把它装进 `/usr/local/share/ca-certificates/` 并运行 `update-ca-certificates`。

构包时仍可用 `RD_RENDEZVOUS_URI` / `RD_SERVER_CERTIFICATE` 把站点默认值烤进包（见 §六），安装问题会
以它为默认值。非交互安装可以预先写答案：

```sh
printf '%s\n' \
  'remotedesktop-client remotedesktop-client/server-address string wss://rd.example.com:5080/ws' \
  'remotedesktop-client remotedesktop-client/server-certificate string /root/signaling.crt' \
  | debconf-set-selections
```

已安装的包用 `dpkg-reconfigure` 改答案（`/etc/default/remotedesktop-{client,host}` 只在首次安装前预置或
非交互安装时生效，装完再改它不会覆盖 debconf 里的答案）。同一版本重装会重新询问并保留上次的答案；
版本升级不重新询问；`apt purge` 会清除答案，重装按全新安装询问：

```sh
sudo dpkg-reconfigure remotedesktop-client     # 被控端同理：remotedesktop-host
```

**客户端和被控端读的最终值**来自随包的 `appsettings.json`（安装时写入），用户自己的那份可以覆盖它：

```text
~/.local/share/RemoteDesktop/appsettings.json     # { "Transport": { "RendezvousUri": "ws://主机:5080/ws" } }
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

- 客户端出现在应用菜单里（`remotedesktop`）。
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
> 要在无人值守的机器上被操作，只能用 Windows 的被控端。

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
journalctl --user -u app-RemoteDesktopHost.service -f
```

> 单元名是 `app-RemoteDesktopHost.service`，不是过去的 `remotedesktop-host`。桌面的 portal 从 systemd
> 单元名反推应用 ID（这里是 `RemoteDesktopHost`），摄像头权限按这个 ID 记账，包里还要有同名
> `RemoteDesktopHost.desktop`。改单元名等于换了应用身份，已经授予的摄像头权限会失效。

- 装完第一次打开客户端，会显示 9 位设备代码和临时密码——两台机器互相输入对方的代码和密码即可连接。

### 4. 第一次连接会弹两个对话框（摄像头权限是另一次授权）

第一次共享屏幕时，桌面会问一次"允许共享哪块屏"，**答过就记住了**（restore token 存在
`~/.local/share/RemoteDesktop/secrets`）。第一次要控制时还会再问一次，而且**每次连接都会问**——
portal 不允许远程桌面会话持久化，这是它的规定而不是本程序的取舍。

所以一台无人值守的 Linux 机器可以**被观看**，但**没人坐在跟前就没法被操作**。要无人值守地操作，
只能用 Windows 的被控端。

控制对话框里要打开"允许远程交互"再点"分享"；只点"分享"而没有打开它，键鼠不会被授权。控制端会一直显示
当前状态：对话框弹出期间显示"已请求被控端授权控制"，被拒绝或对话框被关掉后显示"被控端未允许控制：当前
只能观看"并带"重试授权"按钮。被拒绝后宿主不会立刻重复弹窗（默认退避 30 秒），点"重试授权"会立即再请求
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
3. **日志里有没有"合成器没有描述指针"**：那意味着光标位置不会实时更新，画面本身正常。
4. **日志里有没有 `libavcodec` 相关的拒绝**：编码器缺失或版本不对会明确报出来，不会静默黑屏。

## 六、部署客户端 / 被控端（.pkg，macOS）

### 1. 构建与安装

macOS 没有 Windows 那种“服务 + Agent”的拆分，但不等于只能有一个 app。现在是两个不同 bundle 的应用：

- **client**：`/Applications/Remote Desktop.app`，Avalonia 客户端，完整功能；不自动启动；可在应用内手动开
  “登录时自动启动”（SMAppService）。
- **host**：`/Library/Application Support/RemoteDesktop/Host/Remote Desktop Host.app`，`LSUIElement` 的后台
  应用，**没有窗口**；里面是 `RemoteDesktop.HostService`，只负责被控，不能控制别人；由 LaunchAgent 在每次
  图形登录时启动。它不装进“应用程序”或启动台，避免被误认为一个可以打开的 app，也避免与 client 混淆。
  从旧包升级时，安装器会先停掉旧 LaunchAgent，再删除 `/Applications` 里的旧副本。
- 两者 bundle id 不同（`com.remotedesktop.client` / `com.remotedesktop.host`），TCC 授权也分开。
  **可以同时安装**：client 检测到 Host 的控制服务后只做控制端（`AcceptIncoming=false`），Host 常驻负责被控；
  两者使用不同的设备身份（client 用 `controller-identity`，Host 用 `device-identity`），不会再出现同一设备
  代码互相踢下线。client 启动时如果 Host 包已安装，最多等 3 秒让服务创建控制 socket；如果服务在窗口打开后
  才启动，界面会提示重启客户端以交给 Host 托管。卸载 Host 后也要重启 client 才会在当前进程内重新承担被控端。

```bash
# 控制端
build/installer/macos/client/make-pkg.sh --configuration Release
# 产物：artifacts/installer/macos/RemoteDesktop-Client-macOS-arm64-<版本>.pkg

# 被控端：独立的 Remote Desktop Host.app（隐藏在 Library/Application Support）+ 登录项
build/installer/macos/host/make-pkg.sh --configuration Release
# 产物：artifacts/installer/macos/RemoteDesktop-Host-macOS-arm64-<版本>.pkg
```

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
`Registered with the rendezvous server as`。服务器地址由安装器写在
`/Library/Application Support/RemoteDesktop/appsettings.json` 的 `Transport:RendezvousUri`，
每用户的 `~/Library/Application Support/RemoteDesktop/appsettings.json` 可以覆盖它。

**安装**：双击 `.pkg`，或 `sudo installer -pkg <文件>.pkg -target /`。需要**管理员权限**（装到 `/Applications`）。

安装器的下一页会要求填写**信令服务器地址**（`ws://` 或 `wss://`）。如果服务器用 `wss://` 和自签名证书，
在那里选择证书的公钥文件（PEM/DER）；安装器会把它复制到
`/Library/Application Support/RemoteDesktop/certificates/signaling.crt`，客户端/被控端**只信任这一张证书**
（证书固定），不会修改系统钥匙串或系统信任设置。公开可信证书或 `ws://` 不需要文件。
无人值守安装可以把同样的答案写到
`/Library/Application Support/RemoteDesktop/install-answers.plist`：

| 键 | 值 |
|---|---|
| `RendezvousUri` | `ws://主机:5080/ws` 或 `wss://主机:5080/ws` |
| `ServerCertificatePath` | 自签名服务器证书的绝对路径（PEM/DER），仅在 `wss://` 时需要 |
| `UnattendedPassword` | 仅 host 包：写入当前登录用户钥匙串的无人值守密码（至少 8 位）；不填则保持现有密码不变 |

和服务器包一样，这个答案文件在用完后不会删除；下次没有面板答案的安装会再次应用它。不需要预设时请删除。
host 的 `UnattendedPassword` 也会留在这份文件里（`root:wheel`，0600）；只做一次无人值守安装时，安装完成后
应删除该文件，避免密码长期留在磁盘上。

地址写在 **`/Library/Application Support/RemoteDesktop/appsettings.json`**（root:wheel，0644）——它不在
签名过的 `.app` 里，因为改 bundle 内的文件会让签名失效、连带丢掉 TCC 授权。当前用户自己的
`~/Library/Application Support/RemoteDesktop/appsettings.json` 优先级更高（client 的设置页写的就是这份）。

三件必须知道的事：

- **包默认未签名**（没有 Apple Developer ID 证书）：在别人的 Mac 上 Gatekeeper 会提示
  "无法打开，因为来自身份不明的开发者"，**右键 → 打开**即可（或在「系统设置 → 隐私与安全性」里放行）。
  有证书时加 `--distribution` 与 `--notarize` 做签名 + 公证，下载即开；证书与凭据的配置见
  [build/signing/README.md](../../build/signing/README.md) 的 macOS 一节。
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

服务器地址：安装器写入 `/Library/Application Support/RemoteDesktop/appsettings.json` 的
`Transport:RendezvousUri`（默认 `ws://127.0.0.1:5080/ws`）；host 的每用户文件
`~/Library/Application Support/RemoteDesktop/appsettings.json` 可以覆盖它，改完 kickstart。
client 也可以直接在界面里改。

> **最低系统版本 macOS 26 (Tahoe)**：由随包分发的 `libmsquic` / `openssl@4` bottle 的 `minos 26.0` 决定
> （见 [平台移植总览](../ports/README.md) 的"最低系统版本"）。`.pkg` 只面向 Apple Silicon（arm64）。

## 七、运行期注意事项

- **摄像头**作为常规功能随控制会话开放；观看模式下不开放。
- **临时密码**可以随时刷新；**固定密码**适合无人值守，由平台自己的密钥库持久化存储（Windows 是 DPAPI，macOS 是 Keychain，Linux 是 0600 文件），无需现场人员即可随时远程接入，可在被控端界面的"高级设置 → 安全"中随时开启、修改或关闭。**Linux 例外**：固定密码能让连接通过认证，但 Wayland portal 仍要求每次控制有人当场授权，所以无人值守只能观看，不能操作。
- **中继带宽**：直连打洞成功时不走中继。`RelayEndpoint` 配错会让所有连接都失败。
- **多台服务器**：目前 Rendezvous 不共享状态，设备要连到同一台服务器才能互相发现。
- **日志**：服务器是标准输出（服务方式运行时进 Windows 事件日志或用 `--console` 重定向）；被控端在
  `%ProgramData%\RemoteDesktop\logs`；客户端没有文件日志，问题看界面提示。

## 八、安装包怎么构建（三平台）

**怎么产出交付物**，以及打包脚本里几个不显眼但会咬人的地方。装到机器上之后的事（安装、升级、卸载、排错）
见 §四–§六与 §十；脚本的目录分层与命名规则见 [`build/README.md`](../../build/README.md)。

安装包是**自包含发布**（目标机器不需要装 .NET），所以每个平台各自打自己的包，都在对应平台上构建；
构建命令本身是 `dotnet build` / `dotnet test`，环境要求见 [`CONTRIBUTING.md`](../../CONTRIBUTING.md)。
所有产物落在仓库根的 `artifacts/installer/<平台>/`，命名与角色约定见同一份文档的"构建产物布局"。

### Windows（MSI）

```powershell
# 管理员 PowerShell 或普通 PowerShell（MSI 自己会请求提升）
# 正式发布请带上自己服务器的地址（不带就用内置的 127.0.0.1，装到别的机器上连不上）
.\build\installer\windows\client\Build-ClientInstaller.ps1 -RendezvousUri ws://<服务器>:5080/ws

# 指定用哪张证书签名（不指定就在证书存储里自动找一张可用的）
.\build\installer\windows\client\Build-ClientInstaller.ps1 -SignCertificate <证书指纹或 CN 名称>
```

**客户端那一个 MSI 同时装下客户端、后台服务和会话 Agent**（三者本来就在同一个目录里协作），产物约 49 MB。
只做被控端的机器用**另一个**包：它只装服务与 Agent，没有客户端窗口，也没有开始菜单项。

```powershell
.\build\installer\windows\host\Build-HostInstaller.ps1 `
    -RendezvousUri ws://<服务器>:5080/ws
# 产出 artifacts/installer/windows/RemoteDesktop-Host-Windows-x64-<版本>.msi
```

两个包走同一个脚本（`Build-ClientInstaller.ps1 -HostOnly`）：服务定义、崩溃重启、UDP 防火墙规则和
`--configure-system` 只有一份，所以不会出现"客户端那个修好了、被控端那个还是旧行为"。产品名与 UpgradeCode
各自独立，但因为共用服务名，一台机器只能装其中一个——第二个会拒绝安装并说明先卸哪个。

**Host 包的安装向导多一页“无人值守设置”**：host 装完没有窗口，临时密码只存在进程内存里，所以这一页是全新
机器唯一能直接输入固定密码的地方。留空表示不修改已有密码；勾选“清除已设置”则清掉。静默安装等价于：

```powershell
msiexec /i RemoteDesktop-Host-Windows-x64-<版本>.msi /qn `
  RENDEZVOUSURI="ws://rd.example.com:5080/ws" `
  FIXEDPASSWORD="至少8位"
# 清除已有固定密码：CLEARFIXEDPASSWORD=1
```

密码通过 deferred 自定义动作写进服务的 DPAPI **LocalMachine** 密钥库；`FIXEDPASSWORD` 在
`MsiHiddenProperties` 里、动作带 `HideTarget="yes"`，不进安装日志。密码不能包含双引号（Windows Installer
的命令行限制）；少于 8 位时安装会失败并在日志里说明，而不是装出一个连不上的 host。

设备码在服务的日志里：`%ProgramData%\RemoteDesktop\logs\service-<日期>.log`，搜索
`Registered with the rendezvous server as`。host 没有 UI 可以显示它。

服务端有**自己的**安装包，装的是另一套东西（Rendezvous + Relay + Ops），和这两个包互不影响：

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
- **许可页**：界面用 `WixUI_InstallDir`（欢迎、许可、选目录、进度、完成），许可页显示仓库根 `LICENSE` 里的专有 EULA，
  构建时由 `installer/New-LicenseRtf.ps1` 转成 RTF（非 ASCII 写成 `\uN?` 转义，任何代码页下都不会乱码）。
  该文件里若还有**【方括号】占位符**，打包脚本会拒绝出包——那是留给"换个人/换主体来发布"时的闸门，
  不是待办事项（当前内容是完整的）。
- `-KeepSymbols` 把 PDB 也打进安装包（默认不打，省掉约三分之二的体积）；需要从别的机器解析堆栈时才有用。
- **签名是自动的**，细节见 [`build/signing/README.md`](../../build/signing/README.md)。
- 安装时把服务器地址写进 `%ProgramData%\RemoteDesktop\appsettings.json`；这个文件不在 MSI 的文件表里，
  升级时**不会被覆盖**，所以就地改过的地址会保留。安装命令行的 `RENDEZVOUSURI` 只在**第一次安装**时生效；
  要改已装好的机器，用客户端的「修改服务器地址」（host 包用命令行 `--configure-system`）。
  静默首装可以给：`msiexec /i ... /qn RENDEZVOUSURI=ws://rd.example.com:5080/ws`。

> **WiX 版本陷阱。** 安装包用的是 WiX v5（v6 起商业使用要付 OSMF 费用，v5 已停止社区支持）。
> 改动 `build/installer/windows/client` 时注意：**WiX 5.0.2 会把不含组件的 Fragment 静默丢掉**，
> 自定义操作和安装序列要放在有组件的那一段里，否则打出来的包看着正常却什么都没做——
> 上面那个包内容检查就是为了这个。

**打包脚本在 Windows PowerShell 5.1 与 PowerShell 7 下都能跑。** 以前 5.1 会在
`Get-ChildItem ... -Filter *.pdb -Recurse | Remove-Item` 处抛 `InputObjectNotBound` 而中断；现在改为按路径删除。
CI 用的仍是 pwsh 7，但发布机只需要 Windows 自带的 PowerShell 也能出包。

### macOS（`.app` / `.pkg`）

需要 Xcode Command Line Tools；打包为**自包含**应用。

```bash
# .app（bundle 目录，用 Apple Development 身份签名，便于本机开发调试）
build/installer/macos/client/make-app.sh --configuration Release

# .pkg（可分发安装包，装到 /Applications；默认未签名）
build/installer/macos/client/make-pkg.sh --configuration Release

# 被控端 .pkg：独立的 Remote Desktop Host.app（LSUIElement，无窗口）+ 登录时自动启动的 LaunchAgent
build/installer/macos/host/make-pkg.sh --configuration Release

# 服务端 .pkg（信令、中继与监控台 + 三个 launchd 守护进程，装到 /usr/local/remotedesktop-server）
build/installer/macos/server/make-pkg.sh --configuration Release
```

`.pkg` **默认未签名**（没有 Apple Developer ID 证书）：在别人的 Mac 上 Gatekeeper 会提示
"unidentified developer"，需要右键 → 打开。有证书时加 `--distribution`（Developer ID Installer 签 `.pkg`，
Developer ID Application 签 app 与 Installer 面板）和 `--notarize`（提交 Apple 公证并 staple）即可做到下载即开
——证书与公证凭据的配置见 [`build/signing/README.md`](../../build/signing/README.md) 的 macOS 一节。

三处与旧版不同的构建细节：

- **安装面板是编出来的**：`native/macos/build-installer-plugin.sh` 用 `clang` 编译
  `native/macos/rd_installer_plugin.m`，三个 make-pkg 都会调用它，并用 `productbuild --plugins` 放进产品归档。
  它用同一个 `Developer ID Application` 身份签名；分发时被公证一起扫描。
- **服务端载荷逐个签名**：`make-pkg.sh --distribution` 会对 payload 里的每个 Mach-O（三个 apphost 与原生库）
  执行 `codesign --options runtime --timestamp`，apphost 带
  `build/installer/macos/server/entitlements.plist` 的 JIT 权限。只签外层 `.pkg` 无法通过公证。
- **一次性密码不进包**：`--distribution` 拒绝 `--postgres` / `--relay-key` / `--tls-password`。安装期答案由
  面板或 `install-answers.plist` 提供，见 §六 的 macOS 服务端一节。

### Linux（`.deb`）

三个包各装各的，在 Linux 上构建（自包含发布，目标机器不需要装 .NET）：

```bash
build/installer/linux/client/build-deb.sh   # 用来控制别人
build/installer/linux/host/build-deb.sh     # 允许被别人控制
build/installer/linux/server/build-deb.sh   # 信令与中继服务器
```

`libmsquic` 随包放在可执行文件旁，所以装出来的包不需要设 `LD_LIBRARY_PATH`。安装与服务管理见 §五。

客户端和被控端构包时可以给安装问题一个站点默认值：

```bash
RD_RENDEZVOUS_URI=wss://rd.example.com:5080/ws \
RD_SERVER_CERTIFICATE=/secure/signaling.crt \
build/installer/linux/client/build-deb.sh
```

`RD_SERVER_CERTIFICATE` 必须是 PEM **证书**（不是私钥）；它随包分发，安装时仍可换成另一份证书。
服务端没有构包期属性：数据库与 TLS 在安装时询问，也可以用 `debconf-set-selections` 预置。

### 产物在哪

一律在 `artifacts/installer/<平台>/`，角色写在**文件名**里，不另开子目录。发布时 `release.yml` 会把它们改名成
用户看到的那份资产，形状是 `<产品>-<角色>-<平台>-<架构>-<tag>.<扩展名>`：九个资产一个形状。

构建产物与发布资产名形状相同，只有版本形态不同：构建产物是
`RemoteDesktop-Client-Windows-x64-<四段版本>.msi`，发布时改名为
`RemoteDesktop-Client-Windows-x64-<tag>.msi`。`artifacts/installer/windows/` 里找得到带 `Windows` 的文件。
完整的四类产物路径与 SDK 自己的四层布局见 [`CONTRIBUTING.md`](../../CONTRIBUTING.md) §构建产物布局。

## 九、客户端本机的存储（设备列表）

> **2026-10-10 决定：不做账号体系。** 下面关于"账号体系上线后"的部分因此不会发生，留在这里是因为它记录了
> 当前数据格式为什么长这样（条目里没有密码、时间字段的合并规则、墓碑、`accountId` 参数），
> 以及万一将来改变主意时已经踩过的坑。清单同步、`SyncedRecentDevicesStore`、服务端的 `account_devices` 表都不实现。

这份文档说明"最近连接过的设备"这个列表今天存在哪里、为什么这么存，以及**账号体系上线后**要把它搬到
服务器时，哪些决定必须现在做、哪些坑必须提前避开。今天没有账号，也没有同步——这里描述的是已经落地的
数据格式和明确留出的接口。

### 今天怎么存

客户端把列表放在数据目录下的 `recent_devices.json`（默认 `%AppData%\RemoteDesktop\`，由 `DataDirectory`
配置决定）。结构是带版本号的：

```json
{
  "Version": 1,
  "Devices": [
    {
      "DeviceId": "123 456 789",
      "Alias": "办公室台式机",
      "LastConnectedUtc": "2026-10-07T02:11:04.5120000+00:00",
      "UpdatedAtUtc": "2026-10-07T02:11:04.5120000+00:00",
      "DeletedAtUtc": null
    }
  ]
}
```

读写由 `IRecentDevicesStore` / `RecentDevicesStore`（`src/Hosts/RemoteDesktop.Client/Storage/`）负责，界面只
通过接口访问它——这个接口就是将来接同步时的缝，见下文。

几处与旧格式不同的地方：

- **写入是原子的**：先写 `recent_devices.json.tmp` 再替换。中途崩溃不会再留下一个截断的文件，也就不会出现
  "下次启动读不出来 → 当成空列表 → 一保存就把全部设备抹掉"这条链。
- **读不出来的文件会被让开**：解析失败或版本比自己新时，文件被改名为 `recent_devices.json.corrupt-<时间戳>`
  保留，而不是留在原位等着被下一次保存覆盖。列表因此永远可以人工找回。
- **旧格式会被迁移**：早期文件是一个裸数组，条目里还带着明文 `SavedPassword`。读到这种文件时，密码被搬进
  系统保护的密钥库，文件当场重写成上面的带版本结构——升级即把明文从磁盘上拿掉，而不是等用户下次改点什么。

### 密码为什么不进列表

`RecentDeviceEntry` 里**没有**密码字段，这是刻意的，三条理由：

1. **跨机器没有意义**。密码用 DPAPI 以 `CurrentUser` 作用域加密，换一台机器根本解不开，同步过去也是废数据。
2. **服务器不该看到它**。`docs/design/README.md` 把中转服务器列为对手，整套设计是端到端加密、服务器只转发密文。
   把设备密码放上去，等于要求用户相信一个我们明确假设不可信的组件。
3. **它是本机的事**。"记住密码"是这台机器上的便利，不是账号的属性。

所以密码存在 `ISecretStore`（客户端是 DPAPI，`secrets/` 目录）里，名字为 `recent-device-<设备代码>`，
由 `IRecentDevicesStore.GetPasswordAsync` / `UpdatePasswordAsync` 读写。`UpdatePasswordAsync(id, null)` 会
**删除**密钥——这条路径以前在 API 上是缺失的，"取消勾选记住密码"因此永远清不掉已存的密码。

将来如果产品坚持要"新机器登录后自动带上密码"，唯一可接受的形态是：客户端用账号口令派生密钥（Argon2id）
做端到端加密，服务器只存不透明密文。因为密码不在条目里，这个决定**不需要改动数据格式**就能后加。

### 字段语义与合并规则

账号同步一上线，"两台机器上的同一份列表怎么合"就必须有确定答案。三个时间字段就是为它准备的：

| 字段 | 含义 | 合并规则 |
|---|---|---|
| `LastConnectedUtc` | 最后一次连上这台设备的时间 | 取两边**较晚**的值。这是各机器的观测，不是编辑 |
| `UpdatedAtUtc` | 用户最后一次**改这个条目**的时间（改别名、或删除） | 较晚者胜。连接行为不动它 |
| `DeletedAtUtc` | 用户删除它的时间；null 表示还在列表里 | 非空即视为删除；与对方的 `UpdatedAtUtc` 比较，较晚者胜 |

注意 `LastConnectedUtc` 用的是取最大值而不是"最后写入者胜"：连接是观测，谁先谁后无所谓，晚的那次才是事实。
把连接的写入也当成编辑（顺带推进 `UpdatedAtUtc`）会让"在 A 机改名"被"在 B 机连接"无声覆盖。

**墓碑**（`DeletedAtUtc`）是删除必须软删的原因：在 A 机上删掉设备，如果只是从文件里拿掉，B 机的副本会在
下次同步时把它原样送回来，用户会觉得"删不掉"。带时间戳的墓碑让删除可以被合并、被传播，合并完再清理。
在没有同步的今天，墓碑只是留在文件里、被 `GetAllAsync` 过滤掉——所以现在删掉的设备**看起来**是没了。
连上同一个设备会让它复活（`DeletedAtUtc` 清空、`UpdatedAtUtc` 推进），不会出现"删过一次就再也回不来"。

### 账号作用域

`RecentDevicesStore` 的构造函数接受一个可选的 `accountId`：

- 不传（今天的情况）：列表在 `dataDirectory/recent_devices.json`，密钥名 `recent-device-<设备代码>`。
- 传入：列表在 `dataDirectory/accounts/<accountId>/recent_devices.json`，密钥名带账号前缀。

这样同一个 Windows 用户登录两个账号时，两份列表和两套记住的密码互不串扰。现在就把这个参数留出来，是因为
它的成本是零；等到有了账号再改，已经写下的数据就要迁移。`accountId` 来自服务器，所以构造时会校验它不含
路径分隔符和 `..`——它必须只是一个名字，不能决定文件写到哪儿。

### 账号体系上线时怎么接

**客户端**：`MainWindowViewModel` 依赖的是接口，不是具体类型，所以换实现不用动界面。预期是在中间加一层：

```text
IRecentDevicesStore
  └─ SyncedRecentDevicesStore(本地: RecentDevicesStore, 远端: IAccountDeviceListClient)
       读：本地立即返回，后台按 UpdatedAtUtc 合并
       写：本地先落（离线可用）+ 入队推送
```

能这么接的前提正是"条目里没有密码"——同步层搬运 `RecentDeviceEntry` 这件事本身就不可能泄漏密码。

**服务端**：设备代码已经持久化（Rendezvous 经 `RemoteDesktop.Server.Storage`，裸 SQL + 启动时
`EnsureSchemaAsync`；按 `Rendezvous:Database:Provider` 选 PostgreSQL 或 SQLite，没配则回退内存），
映射在 `device_registrations` 里，新表直接引用它的 `device_id`：

```sql
CREATE TABLE IF NOT EXISTS account_devices (
    account_id        BIGINT NOT NULL,
    device_id         BIGINT NOT NULL,      -- 对应 device_registrations.device_id
    alias             TEXT,
    last_connected_at TIMESTAMPTZ,
    updated_at        TIMESTAMPTZ NOT NULL,
    deleted_at        TIMESTAMPTZ,
    PRIMARY KEY (account_id, device_id)
);
```

沿用既有的写法（裸 SQL、启动建表、`Rendezvous:Database` 配置），不要为这一张表引入 EF Core。

两点别低估：

- **账号体系本身的工作量远大于列表同步**。现在的 Rendezvous 对客户端只有 `/ws` 信令和 `/health`，没有注册、
  登录、令牌、吊销、找回中的任何一样。还要决定设备证书怎么绑定到账号——最自然的做法是设备证书注册进账号，
  这样设备开机不需要输账号密码。
- **设备代码会失效**。它绑定证书指纹，设备重装就是新指纹、新代码。列表里的旧条目会变成死链接，不能假设
  `device_id` 永远有效。界面现在显示 **在线 / 离线 / 状态未知** 三态，而**未知不等于离线**：服务器对"从未注册"
  和"当前离线"的回答故意完全相同（见 `docs/design/README.md` 的在线状态查询一段），所以界面只能说这么多，
  "该设备可能已重新注册"的提示只在有证据时（连接返回"对方设备不在线"）出现。
  **这三态只存在内存里，不写进 `recent_devices.json`**：它是机器的观测值而不是用户意图，写进去只会给将来的
  账号同步增加一条需要合并的字段。合并规则因此完全不受影响。

### 现在有意不做的

- 不引入 EF Core，不写客户端同步框架：一个接口、一种格式、一组时间字段就够了。
- 不把 `DeviceId` 换成 GUID 之类的"分布式友好"标识：服务端已经用 `device_id BIGINT UNIQUE` 关联好了。
- 不清理墓碑：合并规则需要它们。等同步真正落地时再定保留期（比如超过 N 天的墓碑可以丢弃）。
- **不做密码同步**，也不为此预留字段。

## 十、故障速查

**Linux 上先跑这一条**，它一次问完这台机器缺什么，比照表格逐条猜快得多：

```bash
remotedesktop --self-check
```

每一项都是只读、不弹对话框，所以在一台还没授权过、甚至起不来的机器上照样能跑；有必需项不通过时退出码
是 1。它答不上来的（连不上服务器、连上了但画面不动、服务没起来）再看下面。

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 设备代码显示 `--- --- ---`，状态"未连接服务器" | 连不上信令服务器 | 见"已经装好了，但设备代码是空的"一节；用了 `wss://` 时先确认证书域名和信任链 |
| Ops 节点 `rendezvous-local` 显示"抓取失败"、设备管理为空 | 本机节点被登记成机器名/IP；没有 NodeToken 时服务端只允许回环调用，`/metrics` 和 `/internal/*` 返回 401 | 在 Ops 节点管理页点「改地址」改为 `https://127.0.0.1:5080`（TLS）或 `http://127.0.0.1:5080`（明文）；新 server 包升级会自动改回回环；远程节点需要配置 `Ops:NodeToken`（配了 token 时非回环 http 地址会被拒绝） |
| 直连打洞失败、改走中继时连接被拒绝 | `Relay\appsettings.json` 的 `Relay:SharedKey` 与 `Rendezvous:RelaySharedKey` 不一致（旧包升级后 Relay 可能为空） | 安装新 server 包，升级动作 `SyncLocalNodes` 会同步两个密钥；或管理员 PowerShell 运行 `Set-ServerConfiguration` 后 `Restart-Service RemoteDesktopRelay` |
| 设备反复掉线重连 | 反向代理把长连接掐了 | nginx 加 `proxy_read_timeout 1h`（见"生产环境：信令必须走 TLS"） |
| 客户端说证书错误 | 证书里的名字/地址与客户端填的地址对不上，或这台机器不信任那张证书 | 用 `New-ServerCertificate.ps1` 把客户端实际使用的地址加进去重新生成；机器没信任证书就装一次 `.cer`（见"内网也一样"） |
| 日志里成片 "Throttled ... from {地址}" | 限额挡住了某人（或挡错了：代理没配 `TrustedProxies`，整个代理被当成一台机器） | 先确认 `TrustedProxies` 配上没有，再按需要调"公网上的限额"里的数字 |
| 两台机器都有代码，但连接超时 | 双方都连到服务器了，但打洞和中继都不通 | 检查 `RelayEndpoint` 是否为客户端可达地址、UDP 5081/5082 是否放通；先试 `--Transport:ForceRelay true` 判断是不是打洞问题 |
| 连接提示"密码错误" | 密码过期（临时密码刷新过）或被限流 | 在被控端界面刷新密码重试；同一控制端 5 分钟内失败 5 次会被锁 5 分钟 |
| 能看到画面但键盘鼠标没反应 | 会话是"观看模式" | 连接时不要勾"观看模式"；被控端也可以把会话降级为仅观看 |
| 文件/终端/摄像头不可用 | 被控端只有登录界面/UAC（Agent 是 SYSTEM）或权限被收回 | 登录到用户桌面再试；确保会话不是观看模式 |
| 锁屏后终端断开 | Agent 切换会结束终端会话 | 重新打开终端；文件传输则可以在重连后继续 |
| 日志出现 "QUIC is not supported on this system" | Windows 10 缺 Schannel 的 TLS 1.3；或 Linux 上开发运行时找不到 libmsquic | Windows：升级到 Windows 11 / Server 2022+，或换 OpenSSL 版 msquic.dll。Linux：装 `.deb` 的话是随包带的，开发运行时用 `native/linux/fetch-libmsquic.sh` 再设 `LD_LIBRARY_PATH` |
| Linux：控制端每次连接都要重新同意 | portal 不允许远程桌面会话持久化 | 这是规定，不是配置项。无人值守只能靠 Windows 被控端 |
| Linux：剪贴板不同步 | 桌面没有授予剪贴板（和键盘鼠标在同一次授权里，点"拒绝"就没有）；或内容超过 8 MB | 看一眼被控端日志：授予失败会有 "did not grant control"。超大内容会被丢弃而不是截断 |
| Linux：摄像头列表是空的 | 两种可能：机器上确实没有摄像头（portal 的 `IsCameraPresent` 答 false），或者授权被拒了 | 被控端日志里会有 "did not grant access to the cameras"。这条路径**没有被验证过**——见 [平台移植总览](../ports/README.md) 的"未验证项" |
| Linux：光标位置不更新 | 合成器没有送光标元数据 | 画面本身正常；被控端日志会警告一次 |

## 十一、运维监控台设计

给**要改监控台代码、或想理解它为什么这样**的人。怎么装、怎么让总览出现数字见 §二 10，这里只讲构成与设计。

### 定位与边界

第三个服务 `RemoteDesktop.Server.Ops`，与信令、中继并列部署。它**采集不碰数据面**：控制台宕机、重启、升级，
不影响任何一次远程连接。

**做什么**：节点健康与全部已抓取指标、节点版本/uptime、在线设备、会话、告警（含比值规则）、审计，浅色/深色主题。

**不做什么**（有意不做）：日志聚合、远程命令执行、存画面/剪贴板/文件内容、替代 Prometheus/Grafana。

### 为什么独立成一个进程

1. 控制台要跨节点聚合，而信令是无状态横向扩展的——UI 和库塞进去等于给信令加一个必须跨实例一致的状态。
2. 信令在连接建立路径上，管理面的一次错误查询、一次库锁，都不该有机会影响握手。
3. 认证面独立：后台账号被盗不该等于信令被接管。

### 三条边界

| 边界 | 方向 | 协议 | 用途 |
|---|---|---|---|
| 指标 | 控制台 → 节点 | Prometheus 文本 | 数字、趋势 |
| 对象查询 / 管理 | 控制台 → 节点 | JSON `/internal/v1`，Bearer | 设备列表、断开会话、封禁 |
| 事件 | 节点 → 控制台 | JSON 批量 POST，HMAC | 会话事实 |

### 数据从哪来（范围合同）

每块面板都要能指到来源，指不到的就不做。**"服务器没有这个数"是常态，不是缺陷**：

| 面板 | 来源 | 状态 |
|---|---|---|
| 在线设备 | `remotedesktop_devices_online` gauge | 有 |
| 中继带宽 / 流量图 | Relay 字节计数器求速率 | 有 |
| 活跃会话 | Relay 配对/关闭事件 + 客户端会话结束/心跳，`source` 分开计数 | 有 |
| 链路类型 / 平台分布 | 客户端心跳（被控端报平台） | 有（心跳默认关） |
| 会话 RTT | 客户端心跳（控制端测） | 有 |
| 丢包 / 码率 | **无来源** | 不做，显示"—" |
| 文件传输任务 / 审计 | **无来源** | 不做 |
| 设备列表 | `/internal/v1/devices` | 有（代码、注册时间、最后活跃、在线、封禁） |

**丢包与码率为什么没有**：.NET 的 QUIC API 不暴露每连接的丢包统计，编码器的码率是设置值不是吞吐量。
两列保持"—"。想填只能改传输层去做字节计数或读 MsQuic 的 native 统计。

### 代码落位与依赖

| 项目 | 职责 |
|---|---|
| `RemoteDesktop.Server.Ops` | Web 宿主：端点、认证、静态页、后台任务 |
| `RemoteDesktop.Server.Ops.Core` | 纯逻辑：Prometheus 解析、会话投影、规则求值、TOTP、审计链 |
| `RemoteDesktop.Server.Ops.Storage` | 表结构、方言、读写 |
| `RemoteDesktop.Server.Ops.UnitTests` / `.IntegrationTests` | 纯逻辑单测 / 真宿主 + 假节点 |

两条强制的依赖规则（架构测试兜底）：`Ops.Core` 只依赖 BCL + `Server.Shared`（契约），不碰 Storage/宿主/ASP.NET；
`Ops.Storage` 不依赖宿主。**不引用 `Server.Storage`**：设备注册表是信令服务的库，控制台经内部 API 拿设备数据，
不直连对方数据库。

### 事件与投影

- 信封 `{ v, eventId, node, kind, occurredAt, correlationId, payload }`；`eventId` 唯一（重放无害），`correlationId`
  由服务端生成——**绝不用客户端的 `RequestId`**（可被构造，能撞别人的时间线）。
- 事件是事实（`ops_event`），会话是投影（`ops_session`），同事务写；投影可由事件重放重建。
- `source ∈ {relay, signaling, client}` 是诚实性字段：不把三者加成一个"活跃会话总数"。
- **会话结束由客户端上报**：信令新增 `ClientMessage.session_ended`（字段 7），两端各报一次，控制台按同一个
  `correlationId` 合并（取最早的结束时间，原因保留第一次）。它**不受 `Telemetry:Enabled` 影响**——直连会话没有
  中继记录，这是它唯一的结束事实；少了它，行会一直显示"进行中"。服务端按设备限流后转成 `session.ended`。

### 数据模型

基线表 + 迁移（`ops_schema_version` 只前进）：v1 基线；v2 加 `ops_session.platform / version`；v3 加
`ops_session.client_session_id` 与 `ops_admin_action` 表；v4 加通知重试时间 `ops_notification_attempt.last_attempt_at`
与单次 TOTP 时间步 `ops_admin_user.last_totp_step`；v5 把 PostgreSQL 的实数列改成 `DOUBLE PRECISION`（SQLite 的
`REAL` 本来就是 8 字节，v5 在 SQLite 上是记录版本的空操作）。时间统一存 Unix 毫秒，SQLite 与 PostgreSQL 共用一份 DDL。

核心表：`ops_node`（节点）、`ops_metric_sample` / `_catalog`（指标）、`ops_event` / `ops_session`（事实与投影）、
`ops_alert_rule` / `ops_alert`（规则与告警）、`ops_admin_user` / `_session`（账号）、`ops_audit_log`（哈希链审计）、
`ops_admin_action`（两段式复核）、`ops_lease`（单实例租约）。

### 认证与安全

| 项 | 决定 |
|---|---|
| 口令 | PBKDF2-HMAC-SHA256，60 万次迭代，16 字节盐，32 字节输出 |
| 二次验证 | 自实现 RFC 6238 TOTP（30s，±1 窗口），`Ops:RequireTotp` 默认开；验证通过的时间步写入 `last_totp_step`，同一个码不能再次登录 |
| 会话 | 32 字节随机 token，库里只存 SHA-256；登录成功重新签发；默认 8 小时；改密、重置 TOTP、停用或删除账号都会撤销该账号的全部会话 |
| 角色 | `viewer` 只读 / `operator` 确认告警、发起动作 / `admin` 改规则、管账号、批准动作、看审计 |
| CSRF | 双提交 Cookie + `X-Ops-CSRF`，`SameSite=Strict` |
| 审计 | `SHA256(prev_hash ‖ 字段)` 哈希链，`/api/audit/verify` 报第一条不一致 |
| 管理动作 | 两段式：发起（operator）+ 批准（admin），发起人不能批准自己的 |

### 告警

规则是**结构化列，不是表达式**：`single`（单指标）、`ratio`（两指标比值）、`scrape`（抓取失败）、
`session.rtt`、`session.direct`（直连率）。`forSeconds` 状态机——条件持续满足才开，抖动一次不叫人；
去重键保证同一对象同时只有一条未恢复告警。通知走一个 Webhook（`Ops:Notifications:WebhookUrl`），开/恢复各一次；
发送失败按 1/2/4/8 分钟退避重试，最多 5 次，且只在该告警仍处于对应状态时重试（已恢复的告警不会补发“打开”）。

### 已知边界与未做

- 丢包 / 码率：无来源（见上）。
- 通知只有 Webhook；SMTP、多通道、静默匹配未做（表已建，等有 UI 再填）。
- 节点鉴权只有部署级 NodeToken（`Ops:NodeToken` / `Rendezvous:NodeToken` / `Relay:NodeToken`）；每节点令牌未做。
- 抓取、告警求值、清理和动作恢复都通过 `ops_lease` 只让一个实例工作；多副本仍需要共享数据库和一致的时钟，
  未做 SignalR backplane。
- **没有保留清理 worker**：指标样本、事件、会话记录和过期管理会话会一直增长（每个节点每 10 秒一条样本）。
  `OpsStore` 里有按时间删除的方法，但没有宿主服务调用它们。长期部署需要自行备份/归档并定期清理，否则数据库
  会持续变大直到磁盘耗尽。
- Windows 侧控制台现在是服务端 MSI 的一部分（`RemoteDesktopOps` 服务，自动启动、崩溃重启）；单独拿可执行文件
  手工 `sc.exe create` 仍然可行，但不是安装包的做法。
