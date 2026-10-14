# 系统架构设计

本项目采用**领域驱动设计 (DDD)** 的思想与**整洁架构 (Clean Architecture)**/洋葱架构分层原则，配合 `.NET 10` 的模块化特性构建。依赖关系通过自动化单元测试 (`RemoteDesktop.ArchitectureTests`) 严格执行。

## 整体分层规则

依赖流向**只能由外向内**。内层绝对不能引用外层的类型或接口。

```text
Domain ← Abstractions ← Protocol ← Transport ← Application ← Features/Hosts
           ↑
        Signaling (独立契约)
```

*说明：`Platform.*` 和 `Media` 等具体实现层只依赖 `Abstractions` 提供的接口，不依赖其他业务逻辑层。`Signaling` 为了保证服务端不引入任何客户端依赖，是一个完全独立的协议包。*

**平台层不只一个。** `Platform.Windows`、`Platform.Linux` 各自实现同一批端口，`Platform.Posix` 放两个
类 Unix 系统语义相同的部分（PTY、0600 文件密钥库、用户上下文守卫）。宿主在启动时按操作系统选一个，
业务代码一行都不用知道自己在哪个平台上。

## 核心模块职责说明

### 1. 核心业务层 (Inner Core)
*   **`RemoteDesktop.Domain`**: 最内层。定义核心领域模型与业务规则。包含设备身份、权限校验模型 (`AccessGrant` - 控制读写或观看模式)、连接会话实体等。完全独立，不依赖任何库。
*   **`RemoteDesktop.Abstractions`**: 定义了由外层（尤其是平台层）必须实现的所有“端口”（Ports/Interfaces）。包括：屏幕采集器、输入注入器、硬件编解码器、音频采集设备、PTY(终端)工厂、剪贴板读写器、密钥持久化等接口。

### 2. 通信协议层 (Communication)
*   **`RemoteDesktop.Protocol`**: 应用层协议模型。定义通道类型 (`ChannelType`)、数据流拆包/封包规则（`[len][type][payload]` 帧格式），以及各种 Protobuf 文件。依赖 `Abstractions`。
*   **`RemoteDesktop.Signaling`**: 信令层协议。负责 WebSocket 通信机制和 Rendezvous 的 Envelope 消息定义（`ClientMessage` / `ServerMessage`），以及底层的 UDP 控制包解析。无任何内部项目依赖，服务端与客户端共用。

### 3. 网络与应用逻辑层 (Middle Layer)
*   **`RemoteDesktop.Transport`**: 网络连接基建。负责 QUIC 对端连接池管理、证书指纹绑定校验、NAT 地址反射与打洞探测、中继回退逻辑，以及 Rendezvous 的客户端实现。
*   **`RemoteDesktop.Application`**: 会话生命周期与鉴权。实现密码验证（HMAC）、失败限流机制，并定义所有功能模块接入的契约 (`IFeatureModule`) 和信道路由分发器 (`ChannelDispatcher`)。

### 4. 功能特性层 (Features)
*位于外层，组合下层模块以实现独立功能，可按需热插拔。*
*   **`Desktop`**: 桌面协同（视频流、输入注入、本地/远程鼠标指针合并）。
*   **`FileTransfer`**: 文件管理器（断点续传、文件校验、限速管理）。
*   **`Terminal`**: 远程终端（纯文本交互和 VT 序列解析；终端本身由平台层提供——Windows 是 ConPTY，Linux 是 PTY）。
*   **`Camera`**: 摄像头画面劫持与推送。

### 5. 具体实现层 (Implementations & Platforms)
*   **`RemoteDesktop.Media`**: 编解码算法落地。智能调度硬件编解码（H.264）或 CPU 回退方案，负责画面缩放、色彩空间转换 (BT.709) 等。
*   **`RemoteDesktop.Platform.Windows`**: 对 `Abstractions` 的 Windows 原生实现。调用 DXGI 进行超低延迟截屏、使用 SendInput 模拟键鼠、对接 Media Foundation、读写 Windows DPAPI。
*   **`RemoteDesktop.Platform.Linux`**: 对 `Abstractions` 的 Linux 原生实现。屏幕走桌面 portal + PipeWire，输入走 libei（**不需要 `/dev/uinput` 那种机器级特权**），编解码走 libavcodec（NVENC / VA-API / x264 回落），音频走 PulseAudio，防休眠走 logind，密钥是 0600 文件。
*   **`RemoteDesktop.Posix`**: 两个类 Unix 系统语义相同的部分——PTY（`posix_spawn`，不用 `fork`——多线程 .NET 进程里 fork 会死锁）、0600 文件密钥库、Unix 用户上下文守卫。

### 6. 宿主与程序入口 (Hosts)
*   **`RemoteDesktop.Client`**: 基于 Avalonia 12 编写的图形客户端，运行在 Windows 与 Linux 上。
*   **`RemoteDesktop.HostService`**: （被控端核心）守护进程：注册设备、鉴权、分发通道、对接信令服务器。**形态按平台不同**——Windows 上它是 LocalSystem 服务并把屏幕/输入转发给 Agent；Linux 上它是一个跑在图形会话里的 `systemd --user` 服务，自己就是采集和注入的那一方。
*   **`RemoteDesktop.SessionAgent`**: **仅 Windows。** 由 HostService 在当前激活的用户桌面（或登录界面）创建的工作子进程，真正执行截屏和操作，保证权限安全（防止 SYSTEM 权限滥用）。Linux 没有 Session 0，portal 只在该会话内应答，所以没有需要拆出去的那一半——不是没做，是不需要。
*   **`RemoteDesktop.Server.*`**: 包含 Rendezvous（信令协调）和 Relay（流量中继）服务器。可以部署在 Linux/Docker 上。

## 防御性编程与权限设计
*   **最低权限原则**：一切文件传输、终端操作默认以当前登录用户 (Agent 所属用户 / Linux 上的会话用户) 的权限执行，而非服务宿主 (System)。
*   **模块级隔离**：在握手与 `AccessGrant` 分发时，各子 Feature (如剪贴板、文件) 是按需授权的；一旦控制端被降级为“观看模式”，相关的上行通信 Channel 会被立即熔断。
