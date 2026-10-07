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

### 1. 构建

```
dotnet publish src/Server/RemoteDesktop.Server.Rendezvous -c Release -r win-x64 --self-contained true -o artifacts/rendezvous
dotnet publish src/Server/RemoteDesktop.Server.Relay      -c Release -r win-x64 --self-contained true -o artifacts/relay
```

（Linux/macOS 把 `-r win-x64` 换成 `linux-x64` / `osx-arm64` 等即可，服务端是跨平台的。）

### 2. 配置

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
    "ConnectTimeout": "00:00:20"
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

### 3. 作为服务运行

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

### 4. 用 Docker 部署（Linux 服务器首选）

仓库里已经准备好两个镜像和一份 compose 文件（`build/docker/`），从源码在容器里构建，运行镜像基于 .NET 10 运行时（不装 SDK）：

```bash
cd build/docker
export RELAY_SHARED_KEY="$(openssl rand -base64 48)"   # PowerShell: $env:RELAY_SHARED_KEY = "..."
export RELAY_PUBLIC_ENDPOINT="rd.example.com:5082"     # 客户端要能连到的中继地址
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

### 5. 防火墙

```
netsh advfirewall firewall add rule name="RemoteDesktop Rendezvous" dir=in action=allow protocol=TCP localport=5080
netsh advfirewall firewall add rule name="RemoteDesktop Reflect"    dir=in action=allow protocol=UDP localport=5081
netsh advfirewall firewall add rule name="RemoteDesktop Relay"      dir=in action=allow protocol=UDP localport=5082
```

### 6. 健康检查

```
curl http://127.0.0.1:5080/health      # 或浏览器打开，返回 200 即正常（不需要认证）
```

## 三、部署客户端 / 被控端（MSI）

### 1. 构建安装包时带上你的服务器地址

```
pwsh -File build/installer/windows/build-installer.ps1 -RendezvousUri ws://192.168.1.10:5080/ws
```

不加 `-RendezvousUri` 时用的是开发默认值 `ws://127.0.0.1:5080/ws`，脚本会给出警告：**这样装出来的机器只能被服务器所在的机器连**。

### 2. 安装

```powershell
# 图形界面（推荐给一般用户）
msiexec /i RemoteDesktop-0.1.0.0-x64.msi

# 静默安装，指定服务器地址
msiexec /i RemoteDesktop-0.1.0.0-x64.msi /qn RENDEZVOUSURI="ws://192.168.1.10:5080/ws"

# 静默安装到别的目录
msiexec /i RemoteDesktop-0.1.0.0-x64.msi /qn INSTALLFOLDER="D:\Apps\RemoteDesktop" RENDEZVOUSURI="ws://192.168.1.10:5080/ws"
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
5. 服务起来了吗：`Get-Service RemoteDesktopHost`。

### 4. 升级与卸载

```powershell
# 升级（保留设备身份、日志和服务器地址；服务器地址以安装包里的为准）
msiexec /i RemoteDesktop-0.1.0.0-x64.msi /qn

# 卸载（保留设备身份和日志，重装后设备代码不变）
msiexec /x RemoteDesktop-0.1.0.0-x64.msi /qn

# 卸载并删除设备身份与日志（这台机器下次安装会得到新的设备代码）
msiexec /x RemoteDesktop-0.1.0.0-x64.msi /qn REMOVEDATA=1
```

**升级前最好先退出客户端**。安装包会把残留的 `RemoteDesktop.Client.exe` 和 `RemoteDesktop.SessionAgent.exe` 关掉，
但这一步排在"删除旧版本文件"之后（WiX 的顺序），所以如果客户端正在运行时升级，旧文件可能被动等待重启替换，
个别文件要重启后才生效。退出客户端再升级就没有这个问题：

```powershell
Get-Process RemoteDesktop.Client -ErrorAction SilentlyContinue | Stop-Process
msiexec /i RemoteDesktop-0.1.0.0-x64.msi
```

服务不用担心：安装包会先停服务，服务停止时会结束它启动的 Agent（实测 2 秒内退出）。
出问题时用日志装：

```powershell
msiexec /i RemoteDesktop-0.1.0.0-x64.msi /l*v "$env:TEMP\rd-install.log"
```

安装失败时 Windows 也会在"应用程序"事件日志里记下来（来源 `MsiInstaller`，含具体错误码和失败的那一步）。

客户端自己的配置在 `%AppData%\RemoteDesktop`（每用户），MSI 不管它；重装系统或换用户后会在那边重建。

### 5. 签名

```
pwsh -File build/installer/windows/build-installer.ps1 -RendezvousUri ws://<服务器>:5080/ws
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
本地测试签名用 `build/signing/New-SigningCertificate.ps1` 生成证书、`build/signing/Install-TrustedCertificate.ps1` 让机器信任它。

## 四、运行期注意事项

- **摄像头**作为常规功能随控制会话开放；观看模式下不开放。
- **临时密码**可以随时刷新；**固定密码**适合无人值守，由 DPAPI 强加密持久化存储，无需现场人员即可随时远程接入，可在被控端界面的"设置"页随时开启、修改或关闭。
- **中继带宽**：直连打洞成功时不走中继。`RelayEndpoint` 配错会让所有连接都失败。
- **多台服务器**：目前 Rendezvous 不共享状态，设备要连到同一台服务器才能互相发现。
- **日志**：服务器是标准输出（服务方式运行时进 Windows 事件日志或用 `--console` 重定向）；被控端在
  `%ProgramData%\RemoteDesktop\logs`；客户端没有文件日志，问题看界面提示。

## 五、故障速查

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 设备代码显示 `--- --- ---`，状态"未连接服务器" | 连不上信令服务器 | 见上面第三节第 3 条 |
| 两台机器都有代码，但连接超时 | 双方都连到服务器了，但打洞和中继都不通 | 检查 `RelayEndpoint` 是否为客户端可达地址、UDP 5081/5082 是否放通；先试 `--Transport:ForceRelay true` 判断是不是打洞问题 |
| 连接提示"密码错误" | 密码过期（临时密码刷新过）或被限流 | 在被控端界面刷新密码重试；同一控制端 5 分钟内失败 5 次会被锁 5 分钟 |
| 能看到画面但键盘鼠标没反应 | 会话是"观看模式" | 连接时不要勾"观看模式"；被控端也可以把会话降级为仅观看 |
| 文件/终端/摄像头不可用 | 被控端只有登录界面/UAC（Agent 是 SYSTEM）或权限被收回 | 登录到用户桌面再试；确保会话不是观看模式 |
| 锁屏后终端断开 | Agent 切换会结束终端会话 | 重新打开终端；文件传输则可以在重连后继续 |
