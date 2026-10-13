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
*   **`Terminal`**: 远程终端（利用 ConPTY 实现的纯文本交互和 VT 序列解析）。
*   **`Camera`**: 摄像头画面劫持与推送。

### 5. 具体实现层 (Implementations & Platforms)
*   **`RemoteDesktop.Media`**: 编解码算法落地。智能调度硬件编解码（H.264）或 CPU 回退方案，负责画面缩放、色彩空间转换 (BT.709) 等。
*   **`RemoteDesktop.Platform.Windows`**: 对 `Abstractions` 的 Windows 原生实现。调用 DXGI 进行超低延迟截屏、使用 SendInput 模拟键鼠、对接 Media Foundation、读写 Windows DPAPI。
*   **`RemoteDesktop.Platform.MacOS`**: 对 `Abstractions` 的 macOS (Apple Silicon) 原生实现。调用 ScreenCaptureKit 截屏与环回音频、CGEvent 模拟键鼠、VideoToolbox 编解码、AVFoundation 摄像头、AVAssetWriter 录制、Keychain 存密钥、`SMAppService` 开机自启。平台项目同样保持 `net10.0`（**不是** `net10.0-macos`），C 框架直连 P/Invoke，ObjC-only 框架经一个小型辅助库（`native/macos` → `libRemoteDesktopMacNative.dylib`）暴露扁平 C ABI。详见 [macos-port-plan.md](macos-port-plan.md)。

### 6. 宿主与程序入口 (Hosts)
*   **`RemoteDesktop.Client`**: 基于 Avalonia 12 编写的跨平台图形客户端，**同一份代码在 Windows 与 macOS 上构建运行**（`ClientPlatformRegistration` 在运行时按 OS 选择平台层，ILLink 按目标 OS 裁掉另一分支）。
*   **`RemoteDesktop.HostService`**: （被控端核心，**Windows 专属**）以 Windows LocalSystem 服务身份运行的守护进程。负责维持系统级存活并对接信令服务器。
*   **`RemoteDesktop.SessionAgent`**: （**Windows 专属**）由 HostService 在当前激活的用户桌面（或登录界面）创建的工作子进程，真正执行截屏和操作，保证权限安全（防止 SYSTEM 权限滥用）。
*   **`RemoteDesktop.Server.*`**: 包含 Rendezvous（信令协调）和 Relay（流量中继）服务器。可以部署在 Linux/Docker 上。

> **macOS 采用单进程宿主模型**：macOS 没有 Session 0，以 root 运行的 LaunchDaemon 拿不到屏幕录制权限，因此
> Windows 的"服务 + 会话 Agent"模型在 macOS 上不成立。macOS 被控端 = 运行在用户图形会话里的**同一个 `.app`**
> （自带托管能力），开机自启用 `SMAppService` 登录项而非系统服务。见 [macos-port-plan.md §2.1](macos-port-plan.md)。

## 防御性编程与权限设计
*   **最低权限原则**：一切文件传输、终端操作默认以当前登录用户 (Agent 所属用户) 的权限执行，而非服务宿主 (System)。
*   **模块级隔离**：在握手与 `AccessGrant` 分发时，各子 Feature (如剪贴板、文件) 是按需授权的；一旦控制端被降级为“观看模式”，相关的上行通信 Channel 会被立即熔断。
