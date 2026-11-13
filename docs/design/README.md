# 系统设计

给**要改代码的人**：系统怎么分层、线协议长什么样、安全边界画在哪。

三块主要内容读法不同：**分层与模块职责**决定你把代码放哪；**通信协议**是跨端契约，改动要两端一起动；
**安全边界**是"保护了什么、还没保护什么"的清单，其中"还没有做的事"一节是读这份文档最该看的地方。
**运维监控台**一节讲它的构成，**客户端本机存储**一节讲本机存的"最近设备列表"。

依赖规则不是靠自觉：`tests/RemoteDesktop.Architecture.UnitTests` 会在违反时让构建变红。

## 1. 分层与模块职责

本项目采用**领域驱动设计 (DDD)** 的思想与**整洁架构 (Clean Architecture)**/洋葱架构分层原则，
配合 `.NET 10` 的模块化特性构建。

### 整体分层规则

依赖流向**只能由外向内**。内层绝对不能引用外层的类型或接口。

```text
Domain ← Abstractions ← Protocol ← Transport ← Application ← Features ← Hosts
            ↑     ↑                    ↑
      Platform.*  Media             Signaling（独立契约，无内部依赖）
```

*说明：箭头方向是"被依赖 ← 依赖者"。`Platform.*` 依赖 `Abstractions` 提供的接口以及 `Domain` 的实体；`Media`
只依赖 `Abstractions`，不依赖 `Domain` 或其他业务逻辑层；`Platform.Linux` 与 `Platform.MacOS` 另外共用 `Platform.Posix`。
`Features.Desktop` 与 `Features.Camera` 引用 `Media`；宿主（`Client`、`HostService`、`SessionAgent`）除了
`Application` 与各 `Features.*`，还直接引用 `Media` 和 `Platform.*`，在启动时把它们接起来。`Signaling` 为了保证
服务端不引入任何客户端依赖，是一个完全独立的协议包——`Transport` 与 `Server.Shared` 引用它，它不引用任何人。*

**平台层不只一个。** `Platform.Windows`、`Platform.MacOS`、`Platform.Linux` 各自实现同一批端口，其中 Linux 与
macOS 共用 `Platform.Posix`（各层的职责见下）。宿主在启动时按操作系统选一个，业务代码一行都不用知道自己在哪个平台上。

### 核心业务层 (Inner Core)

* **`RemoteDesktop.Domain`**: 最内层。定义核心领域模型与业务规则。包含设备身份、权限校验模型
  (`AccessGrant` - 控制读写或观看模式)、连接会话实体等。完全独立，不依赖任何库。
* **`RemoteDesktop.Abstractions`**: 定义了由外层（尤其是平台层）必须实现的所有"端口"（Ports/Interfaces）。
  包括：屏幕采集器、输入注入器、硬件编解码器、音频采集设备、PTY(终端)宿主与会话（`IPtyHost` / `IPtySession`）、剪贴板读写器、密钥持久化等接口。

### 通信协议层 (Communication)

* **`RemoteDesktop.Protocol`**: 应用层协议模型。定义通道类型 (`ChannelType`)、数据流拆包/封包规则
  （`[len][type][payload]` 帧格式），以及各种 Protobuf 文件。依赖 `Abstractions`。
* **`RemoteDesktop.Signaling`**: 信令层协议。负责 WebSocket 通信机制和 Rendezvous 的 Envelope 消息定义
  （`ClientMessage` / `ServerMessage`），以及底层的 UDP 控制包解析。无任何内部项目依赖，服务端与客户端共用。

这两层在线上长什么样，见“通信协议”一节。

### 网络与应用逻辑层 (Middle Layer)

* **`RemoteDesktop.Transport`**: 网络连接基建。负责 QUIC 对端连接与监听（`QuicPeerConnector` / `QuicPeerListener`）、证书指纹绑定校验、NAT 地址反射与
  打洞探测、中继回退逻辑，以及 Rendezvous 的客户端实现。
* **`RemoteDesktop.Application`**: 会话生命周期与鉴权。实现基于 SRP-6a 的密码认证（`PasswordAuthenticator`）、失败限流机制（`AuthAttemptLimiter`），并定义所有
  功能模块接入的契约 (`IFeatureModule`) 和信道路由分发器 (`ChannelDispatcher`)。另有 `Hosting/`（数据目录
  `DataDirectory`、本机控制管道协议 `HostControl*`、`UserSettings` 与 `MachineSettings`）和 `Links/`
  （`ConnectionLink`，即 `remotedesktop://` 链接的解析）。

### 功能特性层 (Features)

*位于外层，组合下层模块以实现独立功能，可独立增减（在各宿主的组合根里静态注册，没有插件加载）。*

* **`Desktop`**: 桌面协同（视频流、输入注入、本地/远程鼠标指针合并）。
* **`FileTransfer`**: 文件管理器（断点续传、文件校验、限速管理）。
* **`Terminal`**: 远程终端（纯文本交互和 VT 序列解析；终端本身由平台层提供——Windows 是 ConPTY，
  macOS 与 Linux 是共享 `Posix` 层的 PTY）。
* **`Camera`**: 摄像头采集与推送（经被控端授权，使用中被控端有提示）。

### 具体实现层 (Implementations & Platforms)

* **`RemoteDesktop.Media`**: 编解码的公共部分。`VideoCodecSelector` 按"硬件优先、软件回退"挑选编解码器
  （H.264 的具体实现在 `Platform.*`），另有码率控制（`BitrateController`）、BGRA↔NV12 (BT.709) 色彩转换、
  Opus 音频编解码。
* **`RemoteDesktop.Platform.Windows`**: 对 `Abstractions` 的 Windows 原生实现。调用 DXGI 进行超低延迟截屏、
  使用 SendInput 模拟键鼠、对接 Media Foundation、读写 Windows DPAPI。
* **`RemoteDesktop.Platform.MacOS`**: 对 `Abstractions` 的 macOS (Apple Silicon) 原生实现。调用
  ScreenCaptureKit 截屏与环回音频、CGEvent 模拟键鼠、VideoToolbox 编解码、AVFoundation 摄像头、
  AVAssetWriter 录制、Keychain 存密钥、`SMAppService` 开机自启。平台项目同样保持 `net10.0`
  （**不是** `net10.0-macos`），C 框架直连 P/Invoke，ObjC-only 框架经一个小型辅助库（`native/macos` →
  `libRemoteDesktopMacNative.dylib`）暴露扁平 C ABI。详见 [平台移植总览](../ports/README.md)。
* **`RemoteDesktop.Platform.Linux`**: 对 `Abstractions` 的 Linux 原生实现。屏幕走桌面 portal + PipeWire，
  输入走 libei（**不需要 `/dev/uinput` 那种机器级特权**），编解码走 libavcodec（NVENC / VA-API / x264 回落），
  音频走 PulseAudio，防休眠走 logind，密钥是 0600 文件。
* **`RemoteDesktop.Platform.Posix`**: 两个类 Unix 系统语义相同的部分——PTY（`posix_spawn`，不用 `fork`
  ——多线程 .NET 进程里 fork 会死锁）、0600 文件密钥库、Unix 用户上下文守卫。

### 宿主与程序入口 (Hosts)

* **`RemoteDesktop.Client`**: 基于 Avalonia 12 编写的跨平台图形客户端，**同一份代码在 Windows、macOS 与
  Linux 上构建运行**（`ClientPlatformRegistration` 在运行时按 OS 选择平台层，ILLink 按目标 OS 裁掉其余分支）。
* **`RemoteDesktop.HostService`**: （被控端核心）守护进程：注册设备、鉴权、分发通道、对接信令服务器。
  **形态按平台不同**——Windows 上它是 LocalSystem 服务并把屏幕/输入转发给 Agent；Linux 上它是一个跑在
  图形会话里的 `systemd --user` 服务，自己就是采集和注入的那一方；macOS 上它是 host 包里的
  `Remote Desktop Host.app`，同样自己采集和注入（见下面"macOS 采用单进程宿主模型"）。
* **`RemoteDesktop.SessionAgent`**: **仅 Windows。** 由 HostService 在当前激活的用户桌面（或登录界面）
  创建的工作子进程，真正执行截屏和操作，保证权限安全（防止 SYSTEM 权限滥用）。Linux 与 macOS 没有
  Session 0——portal 与 TCC 都只在用户会话里应答——所以没有需要拆出去的那一半：不是没做，是不需要。
* **`RemoteDesktop.Hosts.Composition`**: 不是可执行文件，只把被控端要提供的四个特性收成一份
  `AddRemoteDesktopHostFeatures(dataDirectory)`；客户端独立托管、HostService 的非 Windows 分支与
  SessionAgent 都调用它，新增特性时不必在四个组合根里各改一遍。
* **`RemoteDesktop.Server.*`**: 服务端，可以部署在 Windows、Linux（含 Docker）和 macOS 上。它不引用
  `Abstractions`、`Protocol`、`Transport`、`Application`、`Features`、`Media`、`Platform` 和各宿主（架构测试保证），
  与客户端只共享 `Signaling` 契约。项目分工如下：
  * `Server.Shared`：服务端公共契约与配置步骤——`RelayTicket`、`RelayCookie`、`OpsEventEnvelope` / `OpsEventKinds` / `OpsEventSigner`、`NodeTokenGuard`、
    `ServerSetup` 等；引用 `Signaling`。
  * `Server.Rendezvous`（信令协调）和 `Server.Relay`（UDP 流量中继）：引用 `Server.Shared`；Rendezvous 还引用
    `Server.Storage`（见下）。
  * `Server.Storage`：设备注册表的持久化，没有项目依赖。
  * `Server.Ops.Core` / `Server.Ops.Storage` / `Server.Ops`：运维监控台的三层，见“运维监控台”一节。依赖只向内：`Ops.Core` →
    `Shared`，`Ops.Storage` → `Ops.Core`，`Ops`（Web 宿主）→ 两者，另外还引用 `Server.Shared`；三者都**不**引用
    `Server.Storage`（`The_console_layers_only_point_inwards` 断言）。
  * `Server.Gateway`：监控台的 HTTPS 入口（YARP 反向代理），引用 `Server.Shared`。

> **macOS 采用单进程宿主模型**：macOS 没有 Session 0，以 root 运行的 LaunchDaemon 拿不到屏幕录制权限，
> 因此 Windows 的"服务 + 会话 Agent"模型在 macOS 上不成立。macOS 被控端 = 运行在用户图形会话里的一个
> **已签名 `.app`**：client 包是自带托管能力的 `Remote Desktop.app`，host 包是无窗口、
> 安装在 `Library/Application Support` 的 `Remote Desktop Host.app`（开机自启用 `/Library/LaunchAgents`
> 的 LaunchAgent，而非系统服务）。
> 三个平台的宿主形态对照表见 [使用说明](../usage/README.md)。

### 防御性编程与权限设计

* **最低权限原则**：一切文件传输、终端操作默认以当前登录用户 (Agent 所属用户 / Linux 上的会话用户)
  的权限执行，而非服务宿主 (System)。
* **模块级隔离**：在握手与 `AccessGrant` 分发时，各子 Feature (如剪贴板、文件) 是按需授权的；
  一旦控制端被降级为"观看模式"，相关的上行通信 Channel 会被立即熔断。

### 服务端持久化 (Storage)

信令服务器唯一需要活过重启的状态，是**设备证书指纹 → 9 位设备代码**的映射。它落在一个表
`device_registrations` 里，其余状态（在线状态、配额、限流、会话、指标）全在内存。

```text
Rendezvous ──▶ Storage ──▶ Npgsql
（宿主：DI、健康检查、生命周期）  （端口、算法、方言、配置）  └─▶ Microsoft.Data.Sqlite
```

* **三个端口。** `IDeviceRegistry` 是"指纹换设备码"，另带 `IsRevokedAsync` / `SetRevokedAsync`（封禁，在下一次
  注册时生效）；`IDeviceRegistryStore` 在它之上加"建表"和"探活"；`IDeviceCatalog` 给监控台的设备列表做分页读取。
  内存实现**不**实现 `IDeviceRegistryStore`——于是宿主判断"要不要建表、要不要挂健康检查"的依据是配置里的
  `Provider != none`（`RendezvousServer`），而不是 `is PostgresDeviceRegistry` 这样的类型嗅探。
* **一份算法，每种数据库一个方言。** 缓存、随机九位数、撞车重试、"这次调用是不是它给了设备码"都在
  `RelationalDeviceRegistry` 里；方言提供七句 SQL（建表、查 id、
  不存在才插入、刷新最后活跃时间、补 `revoked_at` 列、设置封禁、查封禁）、两个可选的建表锁语句
  （`SchemaLockSql` / `SchemaUnlockSql`，PostgreSQL 用 `pg_advisory_lock` 防两个进程并发建表）、
  "列已存在"与"唯一冲突"的判定、以及是否需要串行写（SQLite 只有一个写者）。插入用
  `INSERT … ON CONFLICT (fingerprint) DO NOTHING` 加受影响行数，而不是 `RETURNING` 猜 id——MySQL 没有
  `RETURNING`，这个形状才搬得过去；时间戳由 .NET 传参，方言里就没有 `NOW()` 与 `datetime('now')` 的差别。
* **加一种数据库要动这几处**（信令库）：`Directory.Packages.props` 加包、Storage 的 csproj 加引用、写一个
  `XxxDeviceRegistry.cs`（子类 + 内嵌方言）、`DeviceRegistryRegistration` 的 switch 加一支、
  `DatabaseSettings.cs`（名字常量、`Validate()`、`Canonical()`——后者对未知名字直接抛异常）、契约测试加一个派生类。
  若还要让 `--configure-system` 能选它，再改 `Server.Shared` 的 `ServerSetup.cs`（`Provider` 常量与 `Decide`）。
  端口与 Rendezvous 宿主不用动。
* **监控台是另一套 provider。** `OpsStore.Open` 只认 `sqlite` / `postgres`，`ServerSetup` 对 ops 配置也只写这两种；
  新增方言时监控台不要求跟随，但要在 `ServerSetup` 里明确决定，否则 `--configure-system` 会把监控台静默落回
  SQLite。反过来，只改监控台也不会影响设备注册表。
* **配置是显式的**：`Rendezvous:Database:{Provider,ConnectionString,Path}`，`Provider` 取
  `none` / `postgres` / `sqlite`。旧的 `Rendezvous:PostgresConnectionString` 与
  `ConnectionStrings:Postgres` 仍然生效且含义不变——它们是已装机器的配置，读它们就是升级不换设备码。
* **"没给"与"给空"是两件事。** `--configure-system` 的参数为 null 表示"这次不改这一项"，空串或 `auto`
  才是明确表态。这条约定是整个配置步骤的地基：它同时也是给运维用的（改证书的那次不该顺手清掉数据库设置），
  所以别把它改回"空值即清空"。
* **不引入 EF Core**：一张表、没有跨表事务、也没有迁移需求（建表在启动时做，`IF NOT EXISTS`）。
  表数量上到五张、或需要跨表事务时，这笔账要重算。

SQLite 的三个细节都有原因：WAL（读不等那一个写者）、写串行化（它只允许一个写者）、
`CHECK (device_id BETWEEN …)`（SQLite 按值定类型，没有约束就会把文本存进数字列）。
`/health` 探的是"表在不在"，不是"连接通不通"——SQLite 打开一个不存在的文件会**新建空库**，
只问连接的话，一个刚丢光设备码的服务器会回答 Healthy。

**SQLite 是单节点。** 共享 PostgreSQL 时，多个 Rendezvous 实例会给同一台设备发同样的码；SQLite 每个实例一个文件，
两个实例会给同一台设备发不同的码。这是存储形态决定的，不是配置能绕开的。

共库也不等于可以直接挂负载均衡：在线状态（`DevicePresence`）和待处理的连接握手（`ConnectionBroker`）都在各自进程的内存里，
不跨实例共享，控制端与被控端落在不同实例上时握手不会成功。

**换一种数据库会重发设备码**，因为 `device_id` 在旧库里。手工搬数据的命令在
[运维文档](../ops/README.md)的"数据库怎么选"一节。

**已经验证到什么程度。** 契约测试同时跑内存与 SQLite（同一指纹并发、32 路并发、撞车重试、跨实例持久化、
删文件后探活失败、`CHECK` 约束），`/health` 有 SQLite 起服务的用例，三个 RID 的自包含发布都带
`e_sqlite3`。**验证边界**：三平台安装包在 CI 的一次性 runner 上真实安装并检查服务（Windows 还做升级/卸载）；
Linux job 用 PostgreSQL 18 跑设备注册表与监控台存储的契约测试，Windows/macOS job 只跑内存与 SQLite。
真机长期运行、生产数据库迁移与跨机器安装仍由人工验收。动这些地方时，别把"测试通过"当成"在生产上装过一遍"。

## 2. 通信协议

本项目属于远程控制软件，没有传统意义上的 "RESTful API"，其核心 API 就是**网络通信协议**。
整个协议栈分为三层：信令层（Rendezvous）、传输层（Transport）与应用层（Multiplexed Channels）。

### 信令层 (Signaling)

信令用于让控制端和被控端发现彼此、交换证书指纹、并协商 NAT 穿透方案。信令服务器本身
**不接触加密后的桌面画面和按键**。

* **协议**：WebSocket (`ws://` 或 `wss://`)，路径 `/ws`
* **端口**：默认 TCP 5080
* **数据格式**：二进制，直接发送 Protobuf 序列化后的 Envelope (`ClientMessage` / `ServerMessage`)，
  一条 WebSocket 消息一个 Envelope，单条上限 64 KiB（接收侧强制；发送侧不检查，超限会被对端断开）。
* **Protobuf 定义**：`src/Core/RemoteDesktop.Signaling/signaling.proto`

**核心流程**：

1. **上线注册**：WebSocket 打开后，服务器先发 `RegisterChallenge`（随机 nonce）；设备回 `Register`，带自己的
   证书、用证书私钥对 nonce 做的 ECDSA-P256 签名，以及声明的 `client_capabilities`（服务器只向声明过某项能力的设备
   发对应的消息）。验签通过后，服务器从证书推出指纹，回复 `Registered`：设备代码（`device_id`）和 UDP 地址反射端口
   （`reflector_port`，默认 UDP 5081）。连接密码不经过服务器，由被控端本机生成。
2. **状态查询**：控制端可发送 `PresenceQuery`，询问一组设备是否在线。
3. **发起连接**：控制端发送 `ConnectRequest`，提供 `request_id`、目标设备代码和自己的候选地址（`candidates`）。
4. **握手转发**：服务器将请求转化为 `IncomingConnection`（含控制端指纹、候选地址、中继信息和服务器签发的
   `session_id`）转发给被控端；被控端接受后返回 `ConnectAccept`（带自己的候选地址），拒绝则返回 `ConnectReject`。
5. **打洞与中继票据**：服务器把被控端的指纹、候选地址和中继信息用 `ConnectReady` 交给控制端（用于 NAT 打洞），
   中继信息里带**中继票据 (Relay Ticket)**，以备直连失败时使用；目标不在线、被拒绝或超时则回 `ConnectFailed`
   （带 `FailureReason`）。
6. **遥测与管理**（可选）：设备可用 `TelemetryReport` / `SessionEndedReport` 上报会话质量与结束事实，服务器按签发的
   correlation id 校验报告者确为会话的 controller/host 且角色相符；平台和主机本地会话 id 只接受 host 心跳，
   controller 不能覆盖这些字段。管理动作经 `AdminCommand` 下发，主机应用后返回
   `AdminResult`；Rendezvous 与 Ops 在收到匹配确认前不会把断开操作记为已执行，发送失败或确认超时均返回失败。

### 传输层 (Transport)

实际的音视频画面和控制指令直接在两端之间传输，或经过 Relay 服务器透明转发（Relay 看不到明文）。

* **协议**：QUIC (基于 TLS 1.3 的可靠 UDP 传输协议)，ALPN 为 `rd/1`
* **加密机制**：端到端加密（E2EE）。双方在 QUIC 握手时强制验证对方的证书指纹（通过信令层提前获取到的
  指纹），防御中间人攻击。
* **契约常量**：QUIC 的 `IdleTimeout` 30 秒、`KeepAliveInterval` 10 秒、单向/双向入站流各 64；信令 WebSocket
  的 `KeepAlive` 30 秒，注册必须在 10 秒内完成，连接请求 20 秒超时。这些数字写在 `PeerTls`、`RendezvousClient`、
  `SignalingEndpoint` 与 `RendezvousOptions` 里，改一处等于改两端契约。
* **穿透方式**：
  * **地址反射与直连 (Hole Punching)**：端点先向信令服务器的 UDP 反射端口（默认 5081）查询自己的公网地址；
    控制端再向被控端发出的内网和公网 UDP 端口发送 UDP 探测包，尝试建立 P2P QUIC 连接。
  * **中继回退 (Relay Fallback)**：如果 P2P 失败，双方拿着信令服务器签发的"中继票据"连接到 Relay
    服务器（默认 UDP 5082）。Relay 服务器验证票据后，在内存中直接拼接双方的 UDP 数据包，充当哑管道。

### 应用层 (Multiplexed Channels)

当 QUIC 连接建立后，应用层会基于 QUIC 的多路复用特性，为不同的业务功能打开多个独立的 Stream（单向或双向）。

* **信道分配**：由 `ChannelType` 枚举定义，包含：
  * 0: `Control` (鉴权、版本协商、权限控制)
  * 1: `Video` (桌面画面，H.264/硬件编码)
  * 2: `Audio` (Opus 编码音频环回)
  * 3: `Input` (鼠标、键盘事件)
  * 4: `Clipboard` (文本、图片剪贴板双向同步)
  * 5: `FileTransfer` (文件传输块)
  * 6: `Terminal` (PTY 字符流与大小重置命令；Windows 是 ConPTY，macOS/Linux 是 POSIX PTY)
  * 7: `Camera` (远程摄像头数据帧)

* **帧格式 (Wire Format)**：每条 QUIC 流的**第一个字节**是 `ChannelType`（打开流时写一次，位于第一个帧头之前；
  对端要等有数据到达才知道有这条流）。此后的每一个应用层消息都使用统一的帧头包装（大端序）：

```text
[ uint32 payload_length ]  (4 bytes)
[ uint16 message_type ]    (2 bytes)
[ payload ]                (变长；FrameCodec 默认单帧上限 16 MiB，超出按协议错误处理)
```

  `payload` 是 protobuf 还是原始字节由通道和 `message_type` 决定：Protobuf 信封的 `message_type` 为 1；视频帧（2，
  `[flags u8][采集时间 µs i64][Annex-B 访问单元]`，摄像头流复用同一种帧）、音频帧（3，Opus 包）、文件块（4）、
  终端数据（5）直接承载原始字节，省掉一次 protobuf 拷贝。
  16 MiB 只是默认值，各通道按自己的需要收得更紧（例如文件、终端、摄像头的请求消息各 64 KiB）。

* **Protobuf 定义**：所有应用层消息结构定义在 `src/Core/RemoteDesktop.Protocol/*.proto` 目录下。

**鉴权过程**（协议主版本 3，SRP-6a，实现见 `PasswordAuthenticator`）：

1. 在 `Control` 信道建立后，控制端必须立刻发送 `Hello`（协议版本、会话模式、请求的权限）。
2. 被控端回 `AuthChallenge`：每个口令槽（临时密码、固定密码）一个 `SrpChallenge`（salt 与服务器公值 B）。固定密码
   未配置时该槽用随机假口令，消息形状不暴露它是否存在。
3. 控制端回 `AuthResponse`：每个槽一个 `SrpResponse`（客户端公值 A 与证据 M1）。密码**绝不明文传输**，双方
   证书指纹进入 SRP identity。
4. 被控端回 `AuthResult`：是否通过、失败原因（`AuthFailure`）、授予的权限、匹配的槽下标 `credential_index`，以及
   服务器证据 M2。控制端必须验证 M2，否则被控端未被认证。失败原因有：`AUTH_FAILURE_BAD_PASSWORD`（口令错误）、
   `AUTH_FAILURE_TOO_MANY_ATTEMPTS`（该控制端设备已被锁定）、`AUTH_FAILURE_INCOMPATIBLE_VERSION`（协议主版本
   不兼容）、`AUTH_FAILURE_TOO_MANY_SESSIONS`（被控端已达会话数上限 16，口令验证通过后、发出"通过"之前就明确
   拒绝，而不是先通过再断开连接）；`AUTH_FAILURE_NONE` 表示没有失败。`TOO_MANY_SESSIONS` 是协议 3 内新增的枚举值
   （加性变更，主版本不变）；不认识它的旧控制端会把它当作一般的"会话建立失败"，不会崩溃，只是看不到具体原因。
5. 验证通过后，被控端依据策略（如是否为仅观看模式）开放其他信道的数据收发权限。
6. **会话期消息**：控制信道在鉴权后继续承载四种消息（`src/Core/RemoteDesktop.Protocol/control.proto`）：
   * `Ping` / `Pong`：两端每 2 秒一次（`RemoteSession`），只用于保活与 RTT 统计，不是业务心跳。
   * `GrantChanged`（被控端 → 控制端）：会话中权限或模式变化时发送。控制端必须停止提供不再允许的功能；
     被控端同时关闭对应通道，执行不依赖控制端配合。
   * `InputAccessChanged`（被控端 → 控制端）：操作系统层面的输入授权状态（Wayland portal 的授予 / 拒绝 /
     不可用）。带单调递增的 `sequence`，接收方丢弃比已应用值更旧的更新。
   * `RequestInputAccess`（控制端 → 被控端）：请求桌面重新询问输入授权，忽略此前拒绝留下的退避。

`AuthChallenge.nonce` 与 `AuthResponse.proof` 是为线格式兼容保留的字段，版本 3 不再使用。协议主版本不同的对端
会被明确拒绝（`INCOMPATIBLE_VERSION`），而不是被告知"密码错误"。

## 3. 安全边界

说明这套软件**保护什么、怎么保护、以及还没有保护什么**。前半部分是代码里已经实现并测试过的机制，
后半部分是使用者和部署者必须自己承担的边界。写这一节的目的不是让人放心，而是让人知道该在哪里设防。

上报漏洞请走 [`SECURITY.md`](../../SECURITY.md) 的私有通道。

### 谁在防谁

被控端（本机）默认是"关着"的：要连上来必须知道设备代码和密码。连接建好之后，控制端能做什么由被控端逐项授予，
而且**授予和收回都由被控端执行**，不依赖控制端界面是否配合。

要防的主要是这几类人：

| 对手 | 他能做什么 | 靠什么挡住 |
|---|---|---|
| 网络上的陌生人 | 扫描端口、往被控端发连接、猜密码 | QUIC + TLS 1.3、证书指纹固定、密码证明、失败限流、握手并发上限 |
| 中转服务器（或劫持服务器的人） | 转发流量、看到谁连谁 | 端到端加密（中转只转发密文）、中继票据只由 Rendezvous 签发 |
| 信令服务器（或劫持服务器的人） | 看到谁在关注哪些设备代码（在线状态查询）、谎报在线或离线 | 查询按信令连接限流（每个已认证的设备连接一个令牌桶，断线重连会得到新桶；新身份的创建另有注册限流）；答案只是所问代码的子集；状态灯不参与任何权限判断，连接仍以实际握手结果为准；可用 `Rendezvous:PresenceEnabled=false` 关掉 |
| 已授权但意图越界的控制端 | 试图绕过权限、看登录界面时的磁盘、读别人的文件 | 被控端逐通道授权、观看模式在被控端强制、文件和终端只能以登录用户身份运行 |
| 被控端本机的其他账号或网络来源 | 读取设备身份、经控制管道改设置 | 密钥保护（Windows 用 DPAPI + 目录 ACL，macOS 用 Keychain，Linux 用仅属主可读的 0600 文件）；Windows 控制管道拒绝网络访问，并只接受管理员或当前 Agent 用户的对应 Windows 会话 |

### 连接与身份

* **传输**：QUIC（TLS 1.3）。双方在自己这边**固定对端证书指纹**，指纹通过 Rendezvous 信令交换，
  所以中间人拿不到一个双方都接受的证书。中转服务器只看到密文。
* **设备身份**：每台设备第一次启动生成密钥与自签证书，设备代码由服务器分配。私钥用 DPAPI 加密存放
  （服务用 `LocalMachine` 作用域、放在只允许 SYSTEM 和管理员访问的目录；客户端用 `CurrentUser`）。
  macOS 用 Keychain；Linux 用仅属主可读的 0600 文件（目录 0700），防的是同机其他账号，不防被拿走的磁盘。
  Linux 故意不用 libsecret：被控端是 `systemd --user` 服务，启动时登录钥匙环未必已解锁。
* **密码**：临时密码（可刷新）或可选的固定密码。密码**从不明文传输**；认证使用 SRP-6a（RFC 5054 3072 位群，
  SHA-256，实现在 BouncyCastle），双方证书指纹进入 SRP identity，换一条连接重放无效。被控端同时给出临时和
  固定两个凭据槽；固定密码未配置时该槽用随机假密码，因此消息形状不暴露它是否存在。
  被控端返回 SRP 的服务器证据（M2），控制端验证后才建立会话，所以信令服务器即使替换了双方证书指纹也不能
  冒充被控端。SRP 让被动窃听者拿不到可离线验证的 verifier，也让主动冒充者每次握手最多试一个口令；
  它不会让低熵口令变强，信令仍必须使用 TLS。
* **临时密码会自己过期**：30 分钟（`HostCredentials.TemporaryPasswordLifetime`）之后，下一次有人看它就被换掉。
  它是给人念出来、可能被拍照或粘进聊天窗的那一个，不该长期有效。**固定密码不过期**——长期有效正是它存在的理由。
* **中继绑定要证明"收得到"**：中继收到绑定请求后，先往来源地址发一个令牌，只有带着这个令牌回来的绑定才会生效
  （`RelayCookie`）。令牌与票据、角色、来源地址和时间窗绑定。没有这一步，任何拿到票据的人都能把中继指向一个
  别人的地址，让那些 UDP 包以中继的身份到达——一份隐藏来源的发包工具。
* **猜密码**：同一控制端设备 5 分钟内失败 5 次即锁定。不做全局失败锁：未认证的攻击者能轮换设备身份，
  全局锁会变成无需密码即可阻断所有合法控制端的开关；并发握手由独立的连接准入上限约束。并发握手也按“预占”计入，
  不能靠并发绕过这 5 次：握手开始时先为该设备预占一个名额（已记录的失败数 + 进行中的预占数 ≥ 5 即回 `TooManyAttempts`），
  口令验证成功则退还并清零失败记录，口令证据错误则转为一次失败，握手在出示口令证据之前就中断、超时、取消或出错则只退还、
  不计失败（网络抖动不会误锁合法控制端）。预占只在内存中，不随窗口过期，正在预占的设备不会被表满时的淘汰清掉。失败会记日志
  （含控制端设备代码）。失败表（`AuthAttemptLimiter`）最多记 10000 个设备槽、窗口 5 分钟：表满时先清过期项，再批量
  淘汰最旧的**未锁定**设备（已锁定的设备保留锁）；若所有槽都被已锁定的设备占满，表里没有的设备会被暂时**一律拒绝**
  （返回 `TooManyAttempts`），直到有锁定项过期腾出空位（最迟约 5～6 分钟，过期清扫每分钟至多一次）。这是有意的
  fail-closed 取舍：不记录这个新设备的失败就等于给它无限次猜测机会（见 `AuthAttemptLimiter` 的注释与
  `SessionSecurityTests` 中对应的测试）。攻击成本很高——需要上万个已注册身份各失败 5 次，且每个身份都要过注册限速。
* **半开连接洪泛**：被控端同时只允许 32 个连接请求处于准备阶段（`QuicPeerListener.MaxConcurrentRequests`），
  超出的请求直接丢弃（洪泛者付出一次往返，被控端几乎不付出代价）。已接受的连接另有 32 个队列上限，满了
  **静默丢弃**。已建好的会话不受影响，因为配额只覆盖握手。

### 信令服务器自己的暴露面

**一旦部署到公网**，服务器就是唯一一个"谁都连得上"的组件（出厂默认只监听回环，`Urls` 为
`http://127.0.0.1:5080`，见 `SECURITY.md`），所以它自己也要有账算。下面的限额都是默认开着的
（完整清单和调法见 [部署与运维](../ops/README.md) 的"公网上的限额"）：

* **同时服务的设备数**有上限，超出的连接在 WebSocket 升级**之前**就被拒绝——洪泛者付出一次往返，
  服务器不留内存。单个来源地址另有更低的上限，一台机器占不满整个服务器。
* **注册要花配额**：注册就是领一个身份，而这台服务器上其它所有限额都是按身份计的（在线状态查询、连接请求）。
  没有这条，脚本可以无限注册身份，把"每设备"的限额摊薄成没有。这是 Sybil 那道闸。
  令牌在校验证书**之前**就扣（按与连接配额相同的来源键计，同一台机器换地址不能换到新配额）；验签失败不退，
  已知设备掉线、休眠或服务器重启后重连通过验签，令牌退还——它不是这道闸要拦的对象；新分配的身份不退。
* **连接请求要花配额**，而且一台设备能同时挂起的请求数有上限：否则任何人都能拿它去轰炸某台在线设备的弹窗，
  顺带无限签发中继票据。
* **限流事件会写日志**。注册与连接配额的拒绝日志带来源地址——这是服务器唯一记录地址的地方，只在拒绝时写；
  连接请求、在线状态与遥测的限流只带设备代码。正常连接的日志里没有地址。
* **TLS 由部署者打开**（`wss://`）。客户端对非回环明文信令默认拒绝；只有显式设置
  `Transport:AllowInsecureSignaling=true` 才允许，供隔离的明文实验网使用。客户端对信令服务器默认走系统信任链，所以证书域名要对得上；
  自签证书可以用 `Transport:RendezvousCertificatePath` 固定单个证书（不改系统信任库），见 [部署与运维](../ops/README.md)。SRP 保护的是口令，不能替代 TLS 对元数据和中继票据的保护。

反过来，服务器**看不到**画面、按键、文件和终端内容：那些走两端之间的 QUIC，服务器只经手中继密文。

### 授权（会话里能做什么）

权限在被控端算出来，规则在 `AccessGrant` 里，任何一条通道在被功能模块看到之前先经 `ChannelDispatcher` 授权：

* 控制端请求的权限 ∩ 被控端策略允许的权限。
* **观看模式**强制只读，与请求和策略无关；没有屏幕权限时，输入和剪贴板一并去掉。
* 被控端可以随时把某个会话降级为"仅观看"或收回摄像头：相关通道立刻被取消，控制端只是被通知（界面跟着变），
  执行不依赖它。
* **同时最多 16 个会话**（`HostSessionService` 的内部上限，默认 `DefaultMaxSessions = 16`，不参与配置绑定）。
  每个会话都要占用这台机器的内存直到断开，而知道
  密码就能走到这一步，所以没有上限时，一个人就能一直开会话直到机器撑不住。平时从手机和笔记本各连一个，
  离这个数还很远。
* 摄像头作为常规远程功能，默认随控制会话开放（`HostPolicy`）；观看模式下强制不开放。
* **会话授权不等于系统授权**：`AccessGrant` 回答"这台机器的策略允许这个控制端做什么"；在 Wayland 上，键鼠
  还要过桌面的远程控制授权（portal 对话框里的"允许远程交互"）。后者不是本程序能替用户决定的，也不是按
  控制端区分的——它属于宿主进程、所有控制端共用。宿主把它的状态（未请求 / 请求中 / 已允许 / 已拒绝 /
  不可用 / 不适用）通过控制通道的 `InputAccessChanged` 上报；控制端在拒绝时停止捕获输入并显示"只能观看"，
  可通过 `RequestInputAccess` 让宿主立即再问一次。这样"策略允许但系统拒绝"不再表现为"会话坏了"。

### 各功能模块的边界

| 功能 | 谁能用 | 关键限制 |
|---|---|---|
| 屏幕（视频） | `ViewScreen`（观看模式也有） | 只读；被控端不接收任何输入 |
| 输入 | `ControlInput` | 观看模式下被控端拒绝该通道 |
| 剪贴板 | `Clipboard` | 只同步会话期间的**变化**；观看模式不开放 |
| 音频 | `Audio` | 只送被控端正在播放的声音 |
| 文件 | `FileTransfer` | 以**登录用户**身份执行；路径过 `FileAccessPolicy`；内容读写打开后按文件句柄复核真实目标，复用该句柄传输。根目录限制只是 `FileAccessPolicy` 的能力：当前版本没有任何宿主传入策略，不可配置，所以默认不限制根目录；但无论有没有根目录，都拒绝 Windows 的 UNC 网络共享和 Unix 的 `/proc`、`/sys`、`/dev` |
| 终端 | `Terminal` | 只能选被控端列出的 shell 名字；并发上限 8；shell 随会话结束 |
| 摄像头 | `Camera` | 作为常规功能开放；观看模式不开放；被控端可单会话关停；使用中被控端有提示；与文件、终端一样过 `IUserContextGuard` |
| 安全注意序列（Ctrl+Alt+Del） | `SecureAttention` | 不是策略项，由宿主平台能力决定：做不到就不授予（`IInputInjector.SupportsSecureAttention`），控制端的按钮随之隐藏 |

具体到容易出问题的地方：

* **文件和终端绝不以 SYSTEM 运行**：它们在会话 Agent 里跑，且 `IUserContextGuard` 在进程身份是 SYSTEM 时
  直接拒绝（登录界面 / 锁屏 / UAC 桌面的 Agent 就是 SYSTEM）。所以锁屏状态下连上来的人不能借机读整块磁盘。
* **文件路径**：必须是绝对路径，拒绝设备路径（`\\.\`、`\\?\`）、NTFS 备用数据流、`CON`/`NUL` 等保留名，
  `..` 先解析再判断是否越出允许的根目录。当前宿主都没有传根目录，所以除两类位置外，能访问的就是登录用户自己有权访问的东西：UNC 网络共享
  （`\\server\share`，访问它会让 Windows 以该用户身份向对方服务器认证）和 Unix 的 `/proc`、`/sys`、`/dev`（内核接口）始终被拒绝。
* **文件完整性**：目标文件夹里出现完整文件之前，内容一直写在 `<目标名>.<大小>-<修改时间>.rdpart` 里，
  校验（xxHash64）通过才改名，所以目标名下不会出现写了一半的文件。
* **残片不会永远留着**：被控端把残片记在一份清单里，超过 7 天没人续传的会被删除（`PartialFileSweeper`）。
  没有这一步，一个反复开始又放弃传输的控制端可以不断在某人的文件夹里留下文件——每个的大小都不一样，
  所以每个都是新名字。正在写入的残片有最新的修改时间，不会被碰。清单每行一个路径，所以路径里含换行的
  残片不记账（Linux/macOS 的目录名可以含换行，记进去会在重载时被拆成互不相干的几条）；清扫只处理
  "绝对路径且以 `.rdpart` 结尾"的条目，手改或损坏的清单不能让它去删别的文件。Windows 服务模式下文件模块由
  会话 Agent 执行，所以清扫也由 Agent 运行（Agent 启动时会启动它的宿主，清扫器才会真正跑起来）。
* **终端**：控制端不能指定程序路径，只能选被控端列出的名字；Windows 上 shell 和它启动的子进程放在作业对象
  （ConPTY + JobObject）里，关标签、断连接、Agent 退出都会一起结束；Linux 与 macOS 用独立会话和进程组
  （`PosixPtySession`），收尾时按进程组发信号结束。终端**不记录键入内容**（只记录打开/退出/关闭）。
* **终端里的剪贴板**：控制端的复制粘贴是本地行为；被控端程序发来的 OSC 52（让终端写系统剪贴板）被刻意忽略，
  否则被控端上的程序就能写控制端的剪贴板。
* **审计**：文件的新建/重命名/删除/上传/下载、终端的打开/退出、摄像头的打开/关闭都写进日志，带真实会话 ID；
  不记录文件内容，也不记录终端输入。日志落点随平台不同：Windows 上是会话 Agent 的日志
  （`agent-user-*.log` / `agent-secure-*.log`），Linux 与 macOS 没有 Agent，这些功能由 `HostService` 自己承载，
  写进它的 `service-*.log`；独立运行的客户端没有文件日志。

### 本机的进程边界

* **后台服务**（LocalSystem）只做网络、鉴权、授权和转发——它不采集屏幕、不注入输入、不碰文件。真正干活的
  Agent 在交互会话里以**登录用户**运行；只有登录界面 / UAC 时服务才换成 SYSTEM 令牌的 Agent，
  而那时上表的功能全部拒绝。
* **UIPI 突破机制 (UIAccess)**：为了能够远程操作"任务管理器"或管理员权限的窗口而不被系统卡住鼠标，
  安装包里的 `SessionAgent` 开启了 `uiAccess="true"`（只有 `-p:UiAccessAgent=true` 的安装包构建才用
  `app.uiaccess.manifest`，默认构建是 `uiAccess="false"`）。这意味着该 Agent 必须具有合法的代码签名并安装在安全的
  系统目录 (Program Files) 中，由 SYSTEM 权限的 HostService 拉起。
* **服务 ↔ Agent 命名管道**：ACL 只允许启动 Agent 的那个用户，并且校验连接方的进程 ID 就是它启动的进程。
* **界面 ↔ 服务控制管道**：这条管道共 7 个命令：`status`（读状态）、`rotate`（刷新临时密码）、`downgrade`、
  `disconnect`、`camera-stop`、`fixed-password` 与 `camera-permission`。ACL 拒绝网络访问；服务端还逐次核验调用者的
  用户 SID、进程 ID 和 Windows session，只接受 LocalSystem、管理员或当前 Agent 所服务的用户会话，其他本机账户不能读取密码或改设置。
  Windows 上服务每次开始提供该管道时，第一个实例带 `FirstPipeInstance` 创建；界面一侧以 `Identification` 级别连接，
  并在发送任何命令前用 `GetNamedPipeServerProcessId` 与 `QueryFullProcessImageName` 验证服务端是预期安装目录里的
  `RemoteDesktop.HostService.exe`。抢先占名或伪造服务因而既不能冒充调用者，也不能收到固定密码。
  管道（Linux/macOS 上是套接字）创建失败或接收连接时出错，只会让界面暂时连不上服务：服务按 1 秒起、每次翻倍、最长 30 秒的间隔记
  Error 日志并重试，不会停掉整个被控端服务，成功接收一次连接后间隔复位。

> macOS 与 Linux 没有"服务 ↔ Agent"这一层：采集与注入 API 本身就只在用户图形会话里可用，
> 所以没有需要拆出去的那一半。见 [平台移植总览](../ports/README.md)。

### 部署者必须自己做的事

1. **生产环境必须换掉中继共享密钥**（Rendezvous 侧是 `Rendezvous:RelaySharedKey`，Relay 侧是 `Relay:SharedKey`，两边同值）。仓库里的开发配置带的是开发用密钥；这个密钥用来
   签发中继票据，泄露的话别人可以白用你的中继带宽。
2. **信令必须放在 TLS 后面**（`wss://`），做法见 [部署与运维](../ops/README.md) 的"生产环境：信令必须走 TLS"
   （服务器自带证书，或前面放反向代理并配 `TrustedProxies`）。**这不是可选项**：不加密时，信令里的中继票据
   在途可被拿走抢用，设备代码、IP 和连接时间也会暴露。SRP 消除了口令证明的离线爆破面，但不会保护这些元数据。
   它属于可用性和元数据层面的信任面，不是内容层面的。
   **在线状态查询**让这个面变宽了一点：客户端可以问服务器"这些设备代码现在在线吗"，
   而**被问到的设备不会知道**。在这之前，想知道一台设备在不在线只能去连它，对方会看到连接请求。
   代价是：服务器（或被劫持的服务器）按定时器就能拿到这台机器的整份最近设备列表，并且两个方向都能撒谎
   ——谎报在线只会让人白试一次（连接仍要过密码证明、指纹固定和 QUIC 握手），谎报离线是软性拒绝服务，
   也正是状态灯不禁用任何按钮的原因。按信令连接的令牌桶只能收窄批量抓取，**消除不了这个暴露面**，
   所以它是文档里的一条而不是一个协议技巧。不需要这个功能的部署把 `Rendezvous:PresenceEnabled` 设为
   `false`：行为与旧版服务端完全一致，客户端显示"未知"。
3. **安装包要签名**：`Build-ClientInstaller.ps1` 会自动找本机可用的代码签名证书（名字与本产品相符才自动选），
   也可以用 `-SignCertificate <指纹或 CN>` 指定；不签名时 SmartScreen 和企业策略可能拦住安装。
4. **不要随便开固定密码**：固定密码适合无人值守（存进平台密钥库：Windows 是 DPAPI、macOS 是 Keychain，Linux 是仅属主可读的 0600 文件，没有额外加密），它是长期有效的第二把钥匙；
   临时密码更适合有人值守的临时协助场景。下限 8 位由被控端服务强制（不只是在界面上检查），但
   **8 位只是下限不是建议**——SRP 把攻击从离线爆破降为每次握手一次在线猜测，口令仍然是这套设计里最薄的一环，
   能设多长就设多长，并依赖失败限流阻止持续在线猜测。
5. **排查问题时注意日志里有文件路径**（不含内容）。
6. **内部管理 API 要设置 token**（`Rendezvous:NodeToken`、`Relay:NodeToken`、`Ops:NodeToken`，三处同值）。
   Rendezvous 的这组接口与信令共用 5080；Relay 的内部 API 默认只监听 `127.0.0.1:5083`，Ops 监控台默认 `127.0.0.1:5090`。未配置时节点只接受回环调用，跨机或容器里的监控台因此不可用，而把它误当成“不校验”
   暴露到公网，等于把设备枚举、封禁与断开会话交出去。Docker 的 compose 用必填的 `NODE_TOKEN` 把三处接起来。

### 还没有做的事（按重要程度）

* **每个入站连接准备一个 QUIC listener，而它有一个上游原生泄漏**：NAT 打洞要求"打洞端口 = candidate 端口 =
  QUIC 监听端口"，所以每次连接请求都会新建一个 `QuicListener`，用完即 `DisposeAsync`。2026-10-12 的实测表明
  .NET/MsQuic 的 listener 生命周期会泄漏约 1.2 MB/次（与是否真的连上无关）；1.0.0 把并发准备的
  请求限制在 32 个，但**不能阻止累计泄漏**。长期开机的被控端应当监控进程工作集；彻底解决需要换掉 QUIC 层或
  等上游修复。
* **传输过程本身没有空闲检测**：一次性请求——列目录、改名/删除、列出 shell 与打开终端、摄像头列表与打开、
  文件传输的接受与最终校验——都由 `RequestTimeout` 给出 30 秒上限，被控端接受通道后不回答会得到一句
  "被控端没有及时响应"而不是让面板挂到会话结束。（上传发完最后一个字节后等被控端的结果是个例外：被控端要先把
  整个残片复制成最终文件才回复，所以这一次的上限按"30 秒 + 每 MiB 1 秒、最长 1 小时"放宽。）但**数据阶段**仍只等调用方的取消令牌：被控端传到一半停止
  发送时，任务会一直停在"传输中"，用户只能暂停或取消后重试。
* **残片清单有上限，超出上限的旧记录就不再被清理**：清单最多记 1024 条（`PartialFileLedgerOptions.MaxEntries`），
  超出时丢最旧的记录，清扫每 6 小时一轮——条目数、内存与单次写入因此都不再随请求增长。代价是一个已认证的
  控制端在 6 小时内制造超过 1024 个未完成传输时，最早的若干条 `.rdpart` 会失去记录、从此不被自动删除；
  被控端的磁盘配额仍是它自己的事。
* **Wayland 下只能串流操作员在 portal 对话框里勾选的那块屏**：协议里没有"请求某块显示器"的调用，换一块要重新
  授权。现在的做法是：按逻辑坐标把 portal 的流对回合成器的显示器（容忍 1–2 px 的分数缩放进位）；只剩一块流时，
  **按那块流自己的尺寸采集并对外宣告它**，而不是按请求的显示器尺寸去钉格式（那会得到步幅对不上、拷不出来的一串帧）；
  共享了多块而请求的不在其中时，直接报错并列出已共享的尺寸，不再拿别的显示器冒充。剩下的缺口是"请求未共享的
  显示器"仍必须由操作员重新授权，控制端不会因此看到错误的画面或错位的指针。
* **运维节点的 URL 由管理员填写，抓取时会带上该节点的令牌**：管理员（或拿到管理员会话的人）可以把节点指向
  任意地址，监控台会向那里发请求并附上令牌。`Ops:NodeTokenHosts` 把令牌的出口收窄到自有的几台服务器（列表外的
  地址在登记、改地址、抓取与执行管理动作时都被拒绝），所以公网部署应当把它设上（回环地址总是放行）；不设时仍按"https 或回环"放行，明文 http 到非回环地址另需 `Ops:AllowInsecureNodeHttp`。
  每节点令牌可以给单个节点单独配密钥，但**默认仍是部署级**：没配独立令牌的节点共用 `Ops:NodeToken`，因此泄露
  一份节点配置仍可能暴露其余节点，直到它们都换成各自的令牌。
* **PAKE 降低的是离线爆破面，不是口令熵**：机制见"连接与身份"。低熵固定密码仍会被在线猜测（每次握手最多试
  一个口令，且受失败限流和信令访问控制约束），所以固定密码要足够长、信令必须加密，也不要把临时密码给不信任的人。
  这套实现依赖 BouncyCastle 的 SRP 代码，不自行实现群运算；协议主版本是 3，旧客户端会被明确拒绝，
  不会降级回旧的弱交换。
* **没有账号体系**：只有"设备代码 + 密码"这一个因素，没有二次验证，也没有跨设备的登录审计中心。
* **URL 协议只在 Windows 与 Linux 上注册**：`remotedesktop://` 会打开客户端并填入设备代码，但**不自动连接**——
  链接来自本项目控制不了的地方，密码和"连接"必须由人给。macOS 还差一步：LaunchServices 用 Apple Event
  （`kAEGetURL`）投递 URL，原生辅助库没有导出这条通道，所以那边点链接不会有反应。
* **P2P NAT 穿透已支持 IPv4 与 IPv6 双栈**：`CandidateGatherer` 与 `PunchSocket` 支持双栈候选收集与打洞；IPv6 链路本地（link-local）地址在跨网段无路由意义，会被主动过滤，仅保留全球单播（GUA）与唯一本地（ULA）地址。
* **没有做长时间对抗性测试**：握手洪泛上限、限流窗口这些数字是按常见情况选的，没有在真实攻击流量下压过。
* **没有独立的第三方审计**：上面每一条都能在代码里对上，但都出自同一个实现者视角。

### 已知限制与部署须知

下面几条不是"还没做的功能"，而是使用和发布时要心里有数的事实：

* **许可文本里的主体信息要换成你自己的**：仓库根的 `LICENSE` 是专有 EULA，方括号里的公司名称、联系方式和
  司法管辖区是留给"换个人/换主体来发布"的闸门——**只要里面还有全角方括号占位符（【…】），三个 Windows MSI 打包脚本就拒绝出包**
  （可用 `-AllowDraftLicense` 跳过；macOS 与 Linux 的打包脚本不做这个检查；当前内容是完整的，见 [部署与运维](../ops/README.md) 的"安装包怎么构建（三平台）"）。改这个文件就够了，构建会把它转成安装向导的许可页。
* **卸载默认清理机器数据**：MSI 卸载默认删除 `%ProgramData%\RemoteDesktop`（设备身份 + 日志）与服务器包的
  `%ProgramData%\RemoteDesktopServer`（数据库、设置、生成的证书）；`msiexec /x ... REMOVEDATA=0` 保留它们，
  供重装后沿用同一设备代码。客户端在 `%AppData%` 下的身份文件 MSI 不管：那是每用户的位置，卸载时无法枚举其他用户。

### 怎么验证这些说法

* 单元测试：`AccessGrant`（观看模式、缺屏幕权限）、SRP 双向认证与限流、临时密码过期、路径沙盒与打开句柄复核、
  终端按键编码、握手配额、注册与连接请求的令牌桶、中继绑定令牌。
* 集成测试：真实 QUIC 会话上的权限执行（观看模式拒绝输入/剪贴板/文件/终端/摄像头）、守卫（`IUserContextGuard`）拒绝时模块拒绝服务、
  摄像头同意与收回、会话隔离、**信令走 TLS**（含"不受信任的证书会被拒"）、连接配额与注册限流、
  中继两步绑定、`/health` 反映数据库。
* 真机验证记录见 [`CHANGELOG.md`](../../CHANGELOG.md) 与 [平台移植总览](../ports/README.md)。
* 测试盯着的是同一个实现者的那套假设，这一点见"还没有做的事"最后一条（没有独立的第三方审计）。

## 4. 运维监控台

给**要改监控台代码、或想理解它为什么这样**的人。怎么装、怎么让总览出现数字见 [部署与运维](../ops/README.md) 的"运维监控台（可选）"，这里只讲构成与设计。

### 定位与边界

第三个服务 `RemoteDesktop.Server.Ops`，与信令、中继并列部署。它**采集不碰数据面**：监控台宕机、重启、升级，
不影响任何一次远程连接。

**做什么**：节点健康与全部已抓取指标、节点版本/uptime、在线设备、会话、告警（含比值规则）、审计，浅色/深色主题。

**不做什么**（有意不做）：日志聚合、远程命令执行、存画面/剪贴板/文件内容、替代 Prometheus/Grafana。

### 为什么独立成一个进程

1. 监控台要跨节点聚合，而每个信令实例只持有自己的在线状态和待处理的握手（都在进程内，只有设备码可以共库）——UI 和库塞进去等于给信令加一个必须跨实例一致的状态。
2. 信令在连接建立路径上，管理面的一次错误查询、一次库锁，都不该有机会影响握手。
3. 认证面独立：后台账号被盗不该等于信令被接管。

### 三条边界

| 边界 | 方向 | 协议 | 用途 |
|---|---|---|---|
| 指标 | 监控台 → 节点 | Prometheus 文本 | 数字、趋势 |
| 对象查询 / 管理 | 监控台 → 节点 | JSON `/internal/v1`，Bearer | 设备列表、断开会话、封禁 |
| 事件 | 节点 → 监控台 | JSON 批量 POST，HMAC | 会话事实 |

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
| `RemoteDesktop.Server.Ops.Core` | 纯逻辑：Prometheus 解析、会话投影、告警规则与状态机（求值在宿主的 `AlertService`）、TOTP、审计链 |
| `RemoteDesktop.Server.Ops.Storage` | 表结构、方言、读写 |
| `RemoteDesktop.Server.Gateway` | 监控台的 HTTPS 入口：只监听 HTTPS，经 YARP 固定转发到回环上的监控台 |
| `RemoteDesktop.Server.Ops.UnitTests` / `.IntegrationTests` | 纯逻辑单测 / 真宿主 + 假节点 |

两条强制的依赖规则（架构测试兜底）：`Ops.Core` 只依赖 BCL + `Server.Shared`（契约），不碰 Storage/宿主/ASP.NET；
`Ops.Storage` 不依赖宿主。**不引用 `Server.Storage`**：设备注册表是信令服务的库，监控台经内部 API 拿设备数据，
不直连对方数据库。

### 事件与投影

* 信封 `{ v, eventId, node, kind, occurredAt, correlationId, payload }`；`eventId` 唯一（重放无害），`correlationId`
  是服务端取的会话 ID（中继事件用中继会话 ID；心跳与 `session.ended` 用客户端回报的 `relaySessionId` / `sessionId`，
  只接受合法 UUID）——**绝不用客户端自选的 `RequestId`**（可被构造，能撞别人的时间线）。
* 事件是事实（`ops_event`），会话是投影（`ops_session`），同事务写；投影可由事件重放重建。
* `source ∈ {relay, signaling, client}` 是诚实性字段：不把三者加成一个"活跃会话总数"。
* **会话结束由客户端上报**：信令新增 `ClientMessage.session_ended`（字段 7），两端各报一次，监控台按同一个
  `correlationId` 合并（取最早的结束时间，原因保留第一次）。它**不受 `Telemetry:Enabled` 影响**——直连会话没有
  中继记录，这是它唯一的结束事实；少了它，行会一直显示"进行中"。服务端按设备限流后转成 `session.ended`。
  被控端无论会话怎么结束（本机点断开、控制端正常离开、断网）都会上报，心跳也随之停止。结束报告描述的是 QUIC 会话，
  与信令 WebSocket 无关，所以信令断开时尚未发出（或发送失败）的报告会留在队列里，重连后补发（按会话去重，
  发送成功即移除；队列不设固定上限，也不淘汰别的会话的结束事实）；
  心跳则相反，断开即丢弃。服务端按设备限流，重连后一次冲出多条时可能丢一部分，这是已接受的取舍。

### 数据模型

基线表 + 迁移（`ops_schema_version` 只前进）：基线是 `OpsSchema.cs` 里只增不改的那份 DDL，之后的每一步变更都是
`src/Server/RemoteDesktop.Server.Ops.Storage/OpsMigrations.cs` 里的一条迁移，各自在事务里执行；有哪些版本、每个版本改了什么，
以那个文件为准。即使一列"感觉上属于 CREATE TABLE"，也写成迁移而不是改基线——改基线只帮得了新库，帮不了已有的库。
时间统一存 Unix 毫秒，SQLite 与 PostgreSQL 共用一份 DDL（个别迁移按方言分写）。

核心表：`ops_node`（节点）、`ops_metric_sample` / `ops_metric_catalog` / `ops_metric_rollup`（指标；后台任务每 10 分钟从最新桶继续增量聚合为 1 分钟与 1 小时桶，首次只回看 24 小时，并按较长保留期保存趋势数据）、
`ops_event` / `ops_session`（事实与投影）、`ops_alert_rule` / `ops_alert`（规则与告警）、`ops_notification_channel`
（预留，当前未使用：通道是代码里的常量 `webhook` / `smtp`，不是表）/ `ops_notification_attempt`（投递记录）、
`ops_silence`（静默）、`ops_admin_user` / `_session`（账号）、
`ops_audit_log`（哈希链审计）、`ops_admin_action`（两段式复核）、`ops_lease`（单实例租约）、
`ops_schema_version`（迁移版本）。

### 监控台的认证与安全

| 项 | 决定 |
|---|---|
| 口令 | PBKDF2-HMAC-SHA256，60 万次迭代，16 字节盐，32 字节输出 |
| 二次验证 | 自实现 RFC 6238 TOTP（30s，±1 窗口），`Ops:RequireTotp` 默认开；验证通过的时间步写入 `last_totp_step`，同一个码不能再次登录 |
| 会话 | 32 字节随机 token，库里只存 SHA-256；登录成功重新签发；默认 8 小时；改密、重置 TOTP、停用或删除账号都会撤销该账号的全部会话 |
| 角色 | `viewer` 只读 / `operator` 确认告警、发起动作 / `admin` 改规则、管账号、批准动作、看审计 |
| CSRF | `X-Ops-CSRF` 头 = `ops_csrf` Cookie，且必须等于**当前会话派生**的令牌（`HMAC-SHA256(会话 token, "ops-csrf-v1")`，服务端按请求里的会话 Cookie 重算并常量时间比较，不落库、无额外密钥）；能写 Cookie 但读不到 HttpOnly 会话 Cookie 的一方（同站子域）编不出有效令牌；登出、过期、重新登录后旧令牌随会话失效；`ops_csrf` 为 `SameSite=Strict`、非 HttpOnly（前端要读）。 |
| 审计 | `SHA256(prev_hash ‖ 字段)` 哈希链，`/api/audit/verify` 报第一条不一致 |
| 管理动作 | 两段式：发起（operator）+ 批准（admin），发起人不能批准自己的 |

### 告警

规则是**结构化列，不是表达式**：`single`（单指标）、`ratio`（两指标比值）、`scrape`（抓取失败）、
`session.rtt`、`session.direct`（直连率）。`forSeconds` 状态机——条件持续满足才开，抖动一次不叫人；
去重键保证同一对象同时只有一条未恢复告警。

通知有两条通道，开/恢复各发一次：Webhook（`Ops:Notifications:WebhookUrl`）与邮件（`Ops:Notifications:Smtp`）。
两条通道**各自**在 `ops_notification_attempt` 里记一行，失败各自按 1/2/4/8/16 分钟退避重试（单次间隔上限 30 分钟）、
最多 5 次，彼此不影响；
一条通道挂着不会让另一条重发。

**静默**（告警页「静默 1 小时」或 `POST /api/silences`）只压通知：告警照常打开、恢复、认领，照常写审计。
范围可以是某条规则、某个节点（抓取失败类）或两者；窗口结束需要重新触发一次才会再通知——被跳过的消息不会补发。
静默的创建与解除都是管理员动作，各写一条审计。

### 已知边界与未做

* 丢包 / 码率：无来源（见上）。
* 通知有 Webhook 与 SMTP 两条通道，各自独立重试与去重；静默按"规则 / 节点 / 两者"生效。通道目前就这两条。
* 节点鉴权可以每节点一份令牌（见 [部署与运维](../ops/README.md) 的"每节点令牌"），但**默认仍是部署级**：没设独立令牌的节点共用 `Ops:NodeToken`，
  泄露一个节点的配置仍会暴露其余节点。全部换完后可以把 `Ops:NodeToken` 留空，只有回环调用能直连节点。
  `Ops:NodeTokenHosts` 仍决定令牌能发往哪些主机（见 [部署与运维](../ops/README.md) 的"关键配置"表）。
* 抓取、告警求值、清理和动作恢复都通过 `ops_lease` 只让一个实例工作；多副本仍需要共享数据库和一致的时钟。抓取、告警与动作恢复的租期是其轮询间隔的三倍（至少 30 秒），清理的租期是清理间隔加 5 分钟；
  服务正常停止时会主动释放租约，所以单实例重启后新进程立刻接手，只有进程被强杀时才需要等租约过期。
* **历史靠清理任务收敛，不是无限保留**：原始指标样本、降采样指标桶、事件、会话与已恢复告警按
  `Ops:Retention:*` 每小时删除一次（见 [部署与运维](../ops/README.md) 的"数据保留"），过期管理员会话每轮都删；
  **打开中的告警与审计哈希链不动**。原始样本默认只留 30 天（`SampleDays`），1 分钟/1 小时的
  `ops_metric_rollup` 桶默认留 365 天（`RollupDays`），以较低粒度保留长期趋势；各自设为 `0` 会关闭对应清理。
  清理不覆盖 `ops_admin_action`（管理动作记录）、已过期的 `ops_silence` 和 `ops_audit_log`，它们会随使用缓慢增长。
  备份、归档与磁盘容量仍是运维自己的事。
* Windows 侧监控台是服务端 MSI 的一部分（`RemoteDesktopOps` 服务，自动启动、崩溃重启）；单独拿可执行文件
  手工 `sc.exe create` 仍然可行，但不是安装包的做法。

## 5. 客户端本机存储（最近设备列表）

> **2026-10-10 决定：不做账号体系。** 列表只存在这台机器上，没有同步，也没有服务端的对应表。

本节说明"最近连接过的设备"这个列表存在哪里、为什么这么存。

### 今天怎么存

客户端把列表放在数据目录下的 `recent_devices.json`（默认 Windows 是 `%AppData%\RemoteDesktop\`，macOS 是 `~/Library/Application Support/RemoteDesktop/`，
Linux 是 `$XDG_DATA_HOME/RemoteDesktop/`（未设置时为 `~/.local/share/RemoteDesktop/`）；由 `DataDirectory`
配置决定；运维视角见 [部署与运维](../ops/README.md) 的"客户端本机的存储"）。结构是带版本号的：

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
通过接口访问它。

存取方式的几个要点：

* **写入是原子的**：先写 `recent_devices.json.tmp` 再替换。中途崩溃不会留下一个截断的文件，也就不会出现
  "下次启动读不出来 → 当成空列表 → 一保存就把全部设备抹掉"这条链。
* **读不出来的文件会被让开**：JSON 解析失败、条目反序列化失败（缺少必填的 `UpdatedAtUtc`、字段类型不对）、
  `Devices` 缺失或不是数组、条目的 `DeviceId` 为空，或版本比自己新时，文件被改名为
  `recent_devices.json.corrupt-<时间戳>` 保留，而不是留在原位等着被下一次保存覆盖。不合格的条目不会被"跳过再保存"
  ——那样会无声丢条目，所以是整份文件让开。
* **暂时打不开的文件不会被当成空列表**：文件存在但被别的进程占着（杀毒软件、第二个实例）时，读取会短暂重试几次，
  仍失败就把 `IOException` / `UnauthorizedAccessException` 抛给调用方，**不**返回空列表——否则紧接着的保存会用
  一个只有一条记录的列表替换整份旧列表。界面把这类失败记进日志并在状态栏提示，列表原样保留。
* **旧格式会被迁移**：早期文件是一个裸数组，条目里还带着明文 `SavedPassword`。读到这种文件时，密码被搬进
  系统保护的密钥库；含密码时文件立即重写成上面的带版本结构——明文随升级从磁盘上拿掉，而不是等用户下次改点什么。

`RecentDevicesStore` 的构造函数还接受一个可选的 `accountId`：传入时列表在
`dataDirectory/accounts/<accountId>/recent_devices.json`，密钥名带账号前缀（`recent-device-<accountId>-<id>`），
并且校验它不含路径分隔符和 `..`。目前所有宿主都不传，只有测试用到。

### 密码为什么不进列表

`RecentDeviceEntry` 里**没有**密码字段，这是刻意的，三条理由：

1. **跨机器没有意义**。Windows 上密码用 DPAPI 以 `CurrentUser` 作用域加密、macOS 上放在登录 Keychain 里，
   换一台机器都解不开。Linux 上只是仅属主可读的 0600 文件（代码有意不用 libsecret），
   文件本身能被带走：备份数据目录会连同密码一起带走，所以这条理由在 Linux 上更多是"不该带走"而不是"带走了也解不开"。
2. **服务器不该看到它**。本文“安全边界”一节把中转服务器列为对手，整套设计是端到端加密、服务器只转发密文。
   把设备密码放上去，等于要求用户相信一个我们明确假设不可信的组件。
3. **它是本机的事**。"记住密码"是这台机器上的便利，不是账号的属性。

所以密码存在 `ISecretStore`（Windows 是数据目录下 `secrets/` 里的 DPAPI 文件，macOS 是 Keychain，Linux 是 `secrets/` 里的 0600 文件）里，
名字为 `recent-device-<设备代码去掉空格后的数字 id>`，
由 `IRecentDevicesStore.GetPasswordAsync` / `UpdatePasswordAsync` 读写。`UpdatePasswordAsync(id, null)` 会
**删除**密钥。连接成功后记录设备时（`MainWindowViewModel.RecordConnectedDeviceAsync`），如果该设备的"记住密码"
没有勾选，就会调用它：设备条目照常保存，但此前存下的密码被清掉，下次不会再预填。副作用是：对一台之前存过密码
的设备取消勾选后再连一次，会抹掉之前存的那个密码（包括固定密码）。"记住密码"这个勾选本身仍只是内存里的状态，
不会持久化；`SaveOrUpdateAsync` 对 null/空密码的含义依旧是"不动"。

### 字段语义

| 字段 | 含义 |
|---|---|
| `LastConnectedUtc` | 最后一次连上这台设备的时间 |
| `UpdatedAtUtc` | 用户最后一次**改这个条目**的时间（改别名、或删除）。连接行为不动它 |
| `DeletedAtUtc` | 用户删除它的时间；null 表示还在列表里 |

删除是软删：条目留在文件里，`DeletedAtUtc` 非空，被 `GetAllAsync` 过滤掉，所以删掉的设备**看起来**是没了。
连上同一个设备会让它复活（`DeletedAtUtc` 清空、`UpdatedAtUtc` 推进），不会出现"删过一次就再也回不来"。

### 设备代码会失效，在线状态只是观测

设备代码绑定证书指纹，设备重装就是新指纹、新代码，列表里的旧条目会变成死链接，不能假设 `device_id` 永远有效。
界面显示 **在线 / 离线 / 状态未知** 三态，而**未知不等于离线**：服务器对"从未注册"和"当前离线"的回答故意完全相同
（见“安全边界”里“部署者必须自己做的事”关于在线状态查询的一段），所以界面只能说这么多。
连接返回"对方设备不在线"时，界面只是把那一行置为离线并提示"对方设备不在线。"；"该设备可能已重新注册"这类提示目前**没有实现**。
这三态只存在内存里，不写进 `recent_devices.json`：它是机器的观测值，不是用户的意图。
