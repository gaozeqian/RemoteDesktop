# 部署文档

这套软件是**自建服务器**的：两台机器互相连接，靠的是一台它们都能连上的**信令服务器（Rendezvous）**。
没有它，被控端拿不到设备代码，控制端也无从发现对方——"设备代码是空的"几乎总是这个原因。

```
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
| 服务器 | .NET 10 运行时（或自包含发布，不需要装运行时）；开放 TCP 5080、UDP 5081、UDP 5082 |
| 被控端 | Windows 10/11 x64；**管理员权限**装 MSI（服务以 LocalSystem 运行） |
| 控制端 | Windows 10/11 x64；同一块 MSI |

服务器地址必须是**两台机器都能访问到的地址**（局域网 IP、域名或公网地址）。`127.0.0.1` 只能自己连自己。

## 二、部署服务器

### 1. 最省事的一条路：服务端安装包（Windows）

不想手工发布 + `sc.exe` + `netsh`（也就是下面 2、3、6、8 节那些步骤）的话，用仓库里的服务端安装包。
它只做 Windows；Linux 用手工发布或 Docker。

```powershell
pwsh -File build/installer/server/windows/build-server-installer.ps1
# 产物：build/installer/server/windows/artifacts/RemoteDesktop.Server-<版本>-x64.msi
```

安装时可以把该定的都定掉（全部可省略，省略即用下表默认值）：

```powershell
msiexec /i RemoteDesktop.Server-0.2.1.0-x64.msi /qn `
  RENDEZVOUSURL="https://0.0.0.0:5080" `
  RELAYPUBLICENDPOINT="203.0.113.10:5082" `
  POSTGRESCONNECTIONSTRING="Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=..." `
  TLSCERTIFICATEPATH="C:\rd\signaling.pfx" TLSCERTIFICATEPASSWORD="..."
```

它做的事：把两个服务器（信令 + 中继，各自自包含）装进 `%ProgramFiles%\Remote Desktop Server`，
注册两个自动启动、崩溃后自动重启的服务（`RemoteDesktopRendezvous`、`RemoteDesktopRelay`），
加三条**只针对这两个程序**的入站规则（TCP 5080、UDP 5081、UDP 5082），并按下面的属性写好两份
`appsettings.json`。设置是由信令服务器自己写进去的（`--configure-system`），它把做过什么记在安装目录的
`server-setup.log` 里——安装程序不会给任何人看这一步的输出。

| 属性 | 默认 | 说明 |
|---|---|---|
| `RENDEZVOUSURL` | `http://0.0.0.0:5080` | 信令监听的地址。给了证书会自动变成 `https://` |
| `RELAYPUBLICENDPOINT` | `auto` | 客户端被要求使用的中继**地址**。`auto` 取本机第一个非回环地址；也可以写 `IP:端口`，或写域名（安装时解析一次，地址就此固定） |
| `RELAYSHAREDKEY` | `auto` | 中继票据的签名密钥。`auto`：没有就生成一个，**已经有就保持不动**——升级换掉它会让另一台机器上的中继被悄悄切断 |
| `POSTGRESCONNECTIONSTRING` | `auto` | `auto` = 不用数据库 |
| `TLSCERTIFICATEPATH` / `TLSCERTIFICATEPASSWORD` | `auto` / 空 | 给了就写进 Kestrel 配置并切到 https。填 `generate` 则由这台机器生成一张自签名证书、放进本机证书存储，公钥导出到安装目录 `tls\signaling.cer`（装完还能改：`configure-server.ps1 -TlsGenerate`）。**证书文件不存在时不切**（服务器还能起得来），只在 `server-setup.log` 里说明 |
| `NOFIREWALL` | `0` | 设成 `1` 就不加那三条防火墙规则 |

三件必须知道的事：

- **安装包不装数据库。** 不填 `POSTGRESCONNECTIONSTRING` 时设备代码只存在内存里，**信令服务器一重启，
  所有设备代码都会变**——被设成无人值守的机器会因此失联。长期使用请给它一个 PostgreSQL。
- **中继装在另一台机器上时**，那把自动生成的密钥要抄过去：两边都传 `RELAYSHAREDKEY=<同一个值>`，
  或者把 `Rendezvous:RelaySharedKey` 的值填进那台的 `Relay\appsettings.json`。
- **改了端口就要自己改防火墙规则**：三条规则是按 5080/5081/5082 开的，`RENDEZVOUSURL` 或中继端口改了
  它们就对不上。

升级：同版本号重装即可，**`appsettings.json` 不会被覆盖**，手改过的值和已经生成的密钥都保留。
卸载用「应用和功能」或 `msiexec /x`，服务和防火墙规则一并移除；`server-setup.log` 不在安装清单里，
会留在目录里，删不删随你。

### 装完之后改配置

安装向导里填的那些，装完还能改。安装目录（`%ProgramFiles%\Remote Desktop Server`）里有：

- **`configure-server.ps1`** —— 推荐的路子。在**管理员** PowerShell 里给它想改的项即可，没给的保持不动；
  它写完之后会重启两个服务让设置生效：

  ```powershell
  cd "C:\Program Files\Remote Desktop Server"
  .\configure-server.ps1 -PostgresConnectionString 'Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=...'
  .\configure-server.ps1 -TlsGenerate            # 本机生成自签名证书并切 https
  .\configure-server.ps1 -RelayPublicEndpoint 203.0.113.10:5082
  .\configure-server.ps1 -PostgresConnectionString '' -NoRestart   # 回到不用数据库，先不重启
  ```

  它会调用与安装程序**同一个**配置步骤（`Rendezvous\RemoteDesktop.Server.Rendezvous.exe --configure-system`），
  所以两者的行为不会分叉；做过什么会追加到 `server-setup.log`。拿不准就先加 `-WhatIf` 看一眼。
- **`如何配置.txt`** —— 同一件事的短说明，面向拿到机器的人。
- 两个 `appsettings.json`（`Rendezvous\` 与 `Relay\`）—— 也可以直接改，改完重启对应服务。
  它们在升级时不会被覆盖，所以手改的值会保留。**注意它们在安装目录里，本机任何用户都能读**：
  带口令的连接串放这儿，等于本机所有用户都能看到。

### 2. 手工构建

```
dotnet publish src/Server/RemoteDesktop.Server.Rendezvous -c Release -r win-x64 --self-contained true -o artifacts/rendezvous
dotnet publish src/Server/RemoteDesktop.Server.Relay      -c Release -r win-x64 --self-contained true -o artifacts/relay
```

（Linux/macOS 把 `-r win-x64` 换成 `linux-x64` / `osx-arm64` 等即可，服务端是跨平台的。）

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

```
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
        proxy_read_timeout 1h;   # 信令是长连接，默认 60 秒会把设备反复踢下线
    }

    location /health { proxy_pass http://127.0.0.1:5080; }
}
```

代理后面要告诉服务器**谁才是真客户端**，否则按来源地址的限流会把整个代理看成一台机器：

```jsonc
"Rendezvous": { "TrustedProxies": ["172.16.0.0/12"] }   // 你的代理地址或网段
```

`X-Forwarded-For` 谁都能写，所以只有这里列出的地址发来的才被采信（不配就只信本机回环）。同时记得把
5080 从公网关上，只让代理能连它。

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
.\build\installer\client\windows\build-installer.ps1 -RendezvousUri wss://rd.lan:5080/ws `
    -ServerCertificate build\signing\certificates\rd.lan.cer
```

`-ServerCertificate` 把证书的公钥部分放进安装包，安装时装进那台机器的"受信任的根证书颁发机构"——和自签名
代码签名证书走的是同一条路（`Components.wxs` 里的两个条件组件）。已经装好软件的机器不重装也能补上，管理员执行：

```powershell
Import-Certificate -FilePath rd.lan.cer -CertStoreLocation Cert:\LocalMachine\Root
```

> 把一张自签证书装进受信任根，等于**持有它私钥的人可以在那些机器上冒充任何服务器**。私钥（.pfx）只放在
> 服务器上，别外传；证书换新时，客户端机器要跟着换。

### 5. 公网上的限额

默认值按"公网上的自建服务"选的，除非你的规模和它差很远，否则不用动。被限流的事件以 Warning 写进日志，
**带来源地址**——这是服务器唯一把地址写下来的地方，专门为了让你能看出有人在滥用。

| 设置 | 默认 | 管什么 |
|---|---|---|
| `Rendezvous:MaxConnections` | 4096 | 同时服务多少台设备；超出的在 WebSocket 升级前就被拒，不占内存 |
| `Rendezvous:MaxConnectionsPerAddress` | 64 | 一个来源地址最多占多少条连接 |
| `Rendezvous:RegistrationBurst` / `RegistrationRefill` | 8 / 30 秒 | 一个来源地址注册设备的速度。注册就是领一个身份，而"每设备"的限额都以身份计数，所以这条最要紧 |
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

仓库里已经准备好两个镜像和一份 compose 文件（`build/docker/`），从源码在容器里构建，运行镜像基于 .NET 10 运行时（不装 SDK）：

```bash
cd build/docker
export RELAY_SHARED_KEY="$(openssl rand -base64 48)"   # PowerShell: $env:RELAY_SHARED_KEY = "..."
export RELAY_PUBLIC_ENDPOINT="203.0.113.10:5082"     # 客户端要能连到的中继**地址**（不能写域名）
docker compose up -d --build
```

- 暴露的端口：`5080/tcp`（信令 + `/health`）、`5081/udp`（地址反射）、`5082/udp`（中继）。
  **三个都要放通**，UDP 那两个少一个都会让直连打洞或中继失效。
- 两个服务器都会在**启动时校验密钥**：`RELAY_SHARED_KEY` 缺失、太短（少于 32 字节）或不是 base64 时，
  容器会立刻退出并说明原因，不会带着弱密钥跑起来。
- `RELAY_PUBLIC_ENDPOINT` 必须是客户端可达的地址。写成 `127.0.0.1:5082` 只有所有东西都在同一台机器上时才对。
- 健康检查：`curl http://<服务器>:5080/health` 返回 `Healthy`；`docker compose ps` 里 rendezvous 会显示 `(healthy)`。
- 停掉：`docker compose down`（加 `-v` 会连同数据库数据卷一起清掉，慎用）。
- **设备代码持久化（PostgreSQL）**：Compose 默认启动 PostgreSQL 服务与独立数据卷（`postgres-data`）。
  Rendezvous 会自动建立数据表，将设备证书指纹与 9 位设备代码的映射持久化在数据库中；
  即使服务重启、升级或重建容器，所有被控端的设备代码永久固定不变，完全保障无人值守长效可用。
  如果本地开发不需要 PostgreSQL，留空 `Rendezvous:PostgresConnectionString` 会自动回退为纯内存模式。

### 8. 防火墙

```
netsh advfirewall firewall add rule name="RemoteDesktop Rendezvous" dir=in action=allow protocol=TCP localport=5080
netsh advfirewall firewall add rule name="RemoteDesktop Reflect"    dir=in action=allow protocol=UDP localport=5081
netsh advfirewall firewall add rule name="RemoteDesktop Relay"      dir=in action=allow protocol=UDP localport=5082
```

### 9. 健康检查

```
curl http://127.0.0.1:5080/health      # 或浏览器打开，返回 200 即正常（不需要认证）
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

## 三、自动化发布到 GitHub Releases

项目已经配置好了 GitHub Actions 自动化流水线 (`.github/workflows/release.yml`)，当您打上 `v` 开头的 Tag 时，云端会自动跑测试、打包，并把**两个**安装包放进同一个 Release：

| 产物 | 装的是什么 |
|---|---|
| `RemoteDesktop-Windows-x64-<tag>.msi` | 客户端 / 被控端 |
| `RemoteDesktop-Server-Windows-x64-<tag>.msi` | 服务端（信令 + 中继） |

两个包版本号相同，但各自独立安装、互不影响：一台机器可以只做被控端、只做服务器，或者两个都做。

发布前要准备好下面这些。**没有哪一件是必填的**，但流水线会根据你配了什么调整产物，并把结果写在 Release 说明
最前面：

- **服务器地址**（可选）：配了 `DEFAULT_RENDEZVOUS_URI`，客户端安装包就内置这个地址，下载的人装完直接可用；
  不配就是"不内置"，见"配置默认服务器地址"那一节。
- **代码签名证书**（可选，但强烈建议）：不配置也能出包（流水线跳过签名），但没签名的包在目标机器上过不了
  UIAccess，见"准备代码签名证书"那一节。
- **许可文本**：`build/installer/License.txt` 已经是填好的正式文本，不需要动。只有里面重新出现【方括号】占位符时
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
   *   `CODE_SIGN_CERT_BASE64` = 刚刚复制的 Base64 字符串
   *   `CODE_SIGN_CERT_PASSWORD` = 您的证书密码

*(如果不配置这些 Secrets，流水线会自动跳过签名步骤并正常打包)*

配置好之后，流水线用这个 `.pfx` 给客户端的 exe/dll 和 MSI 签名（Windows 自带的方式，不需要额外装工具）。
如果这张证书是**自签名**的，构建还会把它的公开部分一起打进 MSI：安装时安装程序会把它装进目标机器的
"受信任的根证书颁发机构"，卸载时移除——UIAccess 要求签名链能被本机信任，这一步让装好的机器立刻满足这个条件，
不需要再手工 `certutil -addstore`。CA 签发的证书不会被这样做（它的链本来就受信任）。详见
[build/signing/README.md](../build/signing/README.md)。

### 2. 配置默认服务器地址（可选）

在 GitHub Repository -> **Settings** -> **Secrets and variables** -> **Actions** -> **Variables** 标签页下，新建一个变量：
*   `DEFAULT_RENDEZVOUS_URI` = `wss://您的域名:5080/ws`

配了：这个地址会被编进客户端安装包，下载的人装完就能连上你的服务器，不用自己填。

**不配也完全可以发布**：出的包不内置地址（客户端默认连本机），装完后在客户端里用「修改服务器地址」填自己的
服务器就行——它会同时写给客户端和被控端服务，并重启两边。客户端连不上时界面会直接显示"○ 未连接服务器
（当前地址）"以及去哪儿改，所以不是一个不声不响的坏包。

本软件公网和内网都要用，服务器地址往往是**部署的时候**才定的，所以"不内置"是正常选择，不是漏配。
两种情况下 Release 说明的最前面都会写清楚这一版是哪种。

地址用 `wss://` 就要求服务器自己有 TLS 证书（见第 4 节）；局域网里用自签证书时，构建客户端安装包加
`-ServerCertificate` 把服务器证书的公钥打进包并装进受信任根，就不用每台机器手工导入。

### 3. 触发自动发布

在本地完成开发后：
```bash
git tag v1.0.0
git push origin v1.0.0
```
GitHub Actions 会自动拉起 Windows 构建机，几分钟后，您的 Releases 页面就会出现
`RemoteDesktop-Windows-x64-v1.0.0.msi` 和 `RemoteDesktop-Server-Windows-x64-v1.0.0.msi`。

服务器地址是**编进包里的**，所以同一个 Tag 用不同地址出包没有意义：要换地址就出新的 Tag。

## 四、部署客户端 / 被控端（MSI）

### 1. 构建安装包时带上你的服务器地址

```
pwsh -File build/installer/client/windows/build-installer.ps1 -RendezvousUri ws://192.168.1.10:5080/ws
```

不加 `-RendezvousUri` 时用的是开发默认值 `ws://127.0.0.1:5080/ws`，脚本会给出警告：**这样装出来的机器只能被服务器所在的机器连**。

### 2. 安装

```powershell
# 图形界面（推荐给一般用户）
msiexec /i RemoteDesktop-0.2.1.0-x64.msi

# 静默安装，指定服务器地址
msiexec /i RemoteDesktop-0.2.1.0-x64.msi /qn RENDEZVOUSURI="ws://192.168.1.10:5080/ws"

# 静默安装到别的目录
msiexec /i RemoteDesktop-0.2.1.0-x64.msi /qn INSTALLFOLDER="D:\Apps\RemoteDesktop" RENDEZVOUSURI="ws://192.168.1.10:5080/ws"
```

安装完成后：

- 服务 `RemoteDesktopHost` 自动启动（LocalSystem），客户端在开始菜单里。
- 装完第一次打开客户端，会显示 9 位设备代码和临时密码——**两台机器互相输入对方的代码和密码即可连接**。
- 装安装包时会顺手打开"服务可以发送 Ctrl+Alt+Del"的策略位（只加这一位，并记住是自己加的），并添加一条**只针对本程序**的
  入站 UDP 防火墙规则——直连打洞要靠它，没有这条规则时所有连接都会绕道中继（能连上，但更慢）。

### 3. 已经装好了，但设备代码是空的

按顺序查：

1. **服务器地址对不对**：`C:\Program Files\RemoteDesktop\appsettings.json` 里的 `Transport:RendezvousUri`。
   客户端界面在连不上时也会把它显示出来，并给出提示。
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
# 升级（保留设备身份、日志和服务器地址；服务器地址以安装包里的为准）
msiexec /i RemoteDesktop-0.2.1.0-x64.msi /qn

# 卸载（保留设备身份和日志，重装后设备代码不变）
msiexec /x RemoteDesktop-0.2.1.0-x64.msi /qn

# 卸载并删除设备身份与日志（这台机器下次安装会得到新的设备代码）
msiexec /x RemoteDesktop-0.2.1.0-x64.msi /qn REMOVEDATA=1
```

**升级前最好先退出客户端**。安装包会把残留的 `RemoteDesktop.Client.exe` 和 `RemoteDesktop.SessionAgent.exe` 关掉，
但这一步排在"删除旧版本文件"之后（WiX 的顺序），所以如果客户端正在运行时升级，旧文件可能被动等待重启替换，
个别文件要重启后才生效。退出客户端再升级就没有这个问题：

```powershell
Get-Process RemoteDesktop.Client -ErrorAction SilentlyContinue | Stop-Process
msiexec /i RemoteDesktop-0.2.1.0-x64.msi
```

服务不用担心：安装包会先停服务，服务停止时会结束它启动的 Agent（实测 2 秒内退出）。
出问题时用日志装：

```powershell
msiexec /i RemoteDesktop-0.2.1.0-x64.msi /l*v "$env:TEMP\rd-install.log"
```

安装失败时 Windows 也会在"应用程序"事件日志里记下来（来源 `MsiInstaller`，含具体错误码和失败的那一步）。

客户端自己的配置在 `%AppData%\RemoteDesktop`（每用户），MSI 不管它；重装系统或换用户后会在那边重建。

### 5. 签名

```
pwsh -File build/installer/client/windows/build-installer.ps1 -RendezvousUri ws://<服务器>:5080/ws
```

脚本会自动在当前用户和本机证书存储里找可用的代码签名证书（有私钥、未过期、用途是代码签名），找到就签名并验证，
签名时间戳默认打到 DigiCert。它只自动使用**名字里含 `RemoteDesktop`** 的证书；别的产品的证书不会被默默拿来用，
那种情况会列出候选让你指定：

```
... -SignCertificate <证书指纹>          # 或 -SignCertificate "CN=你的公司"
... -SkipSigning                          # 明确不签名
... -SkipSignatureVerification            # 跳过签名验证（一般不需要）
```

签名分两步：先签发布目录里本产品自己的 exe/dll（这样**装到机器上的程序**也带签名），再签 MSI。
工具按"最少配置"自动选：证书存储里有可用证书就用 signtool，没有就退回 Windows 自带的 PowerShell
Authenticode 签名（**不需要 Windows SDK**）；也可以用开源跨平台的 osslsigncode 直接签证书文件（支持 MSI，
Linux CI 也能签）：`-SigningTool osslsigncode -CertificateFile <pfx|pem> [-CertificatePassword <密码>] [-PrivateKeyFile <key.pem>]`。
自签名证书在装过它 `.cer` 的机器之前只会得到一条"不受信任的根"的**警告**，构建不会失败。
完整说明（怎么买证书、有哪些替代方案、怎么检查签名）见 [`build/signing/README.md`](../build/signing/README.md)；
本地测试签名用 `build/signing/windows/New-SigningCertificate.ps1` 生成证书、`build/signing/windows/Install-TrustedCertificate.ps1` 让机器信任它。

## 五、运行期注意事项

- **摄像头**作为常规功能随控制会话开放；观看模式下不开放。
- **临时密码**可以随时刷新；**固定密码**适合无人值守，由 DPAPI 强加密持久化存储，无需现场人员即可随时远程接入，可在被控端界面的"高级设置 → 安全"中随时开启、修改或关闭。
- **中继带宽**：直连打洞成功时不走中继。`RelayEndpoint` 配错会让所有连接都失败。
- **多台服务器**：目前 Rendezvous 不共享状态，设备要连到同一台服务器才能互相发现。
- **日志**：服务器是标准输出（服务方式运行时进 Windows 事件日志或用 `--console` 重定向）；被控端在
  `%ProgramData%\RemoteDesktop\logs`；客户端没有文件日志，问题看界面提示。

## 六、故障速查

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 设备代码显示 `--- --- ---`，状态"未连接服务器" | 连不上信令服务器 | 见"已经装好了，但设备代码是空的"一节；用了 `wss://` 时先确认证书域名和信任链 |
| 设备反复掉线重连 | 反向代理把长连接掐了 | nginx 加 `proxy_read_timeout 1h`（见"生产环境：信令必须走 TLS"） |
| 客户端说证书错误 | 证书里的名字/地址与客户端填的地址对不上，或这台机器不信任那张证书 | 用 `New-ServerCertificate.ps1` 把客户端实际使用的地址加进去重新生成；机器没信任证书就装一次 `.cer`（见"内网也一样"） |
| 日志里成片 "Throttled ... from {地址}" | 限额挡住了某人（或挡错了：代理没配 `TrustedProxies`，整个代理被当成一台机器） | 先确认 `TrustedProxies` 配上没有，再按需要调"公网上的限额"里的数字 |
| 两台机器都有代码，但连接超时 | 双方都连到服务器了，但打洞和中继都不通 | 检查 `RelayEndpoint` 是否为客户端可达地址、UDP 5081/5082 是否放通；先试 `--Transport:ForceRelay true` 判断是不是打洞问题 |
| 连接提示"密码错误" | 密码过期（临时密码刷新过）或被限流 | 在被控端界面刷新密码重试；同一控制端 5 分钟内失败 5 次会被锁 5 分钟 |
| 能看到画面但键盘鼠标没反应 | 会话是"观看模式" | 连接时不要勾"观看模式"；被控端也可以把会话降级为仅观看 |
| 文件/终端/摄像头不可用 | 被控端只有登录界面/UAC（Agent 是 SYSTEM）或权限被收回 | 登录到用户桌面再试；确保会话不是观看模式 |
| 锁屏后终端断开 | Agent 切换会结束终端会话 | 重新打开终端；文件传输则可以在重连后继续 |
