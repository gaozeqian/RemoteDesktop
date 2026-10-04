# RemoteDesktop

跨平台远程桌面软件（类 ToDesk）：远程控制、观看模式、文件传输、远程终端、远程摄像头。
后端 .NET 10，前端 Avalonia 12。第一阶段仅支持 Windows，macOS / Linux 在后续阶段通过新增平台项目接入。

## 构建与测试

```
dotnet build RemoteDesktop.slnx
dotnet test RemoteDesktop.slnx
```

## 本地运行（单机两个客户端互连）

```
# 1. 服务端（Development 配置里带有开发用的中继共享密钥）
$env:ASPNETCORE_ENVIRONMENT="Development"; dotnet run --project src/Server/RemoteDesktop.Server.Rendezvous
$env:DOTNET_ENVIRONMENT="Development";     dotnet run --project src/Server/RemoteDesktop.Server.Relay

# 2. 两个客户端，使用不同的数据目录（即不同的设备身份）
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory D:\rd1
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory D:\rd2
```

在一个客户端输入另一个客户端显示的设备代码和临时密码，点击连接，状态栏会显示"直连 / 中继"和延迟。
远程桌面窗口顶部显示分辨率、帧率、码率、编码器、解码耗时、画面延迟（采集到解码完成，仅在双方时钟同步时准确）和 RTT。
测性能请用 Release 构建（`-c Release`），Debug 构建的颜色转换慢约 10 倍。
`--Transport:ForceRelay true` 强制走中继，`--Transport:RendezvousUri ws://<服务器>:5080/ws` 连接其他服务器。
跨机器测试时需要放通服务器的 TCP 5080、UDP 5081（地址反射）和 UDP 5082（中继），并把 Rendezvous 的
`Rendezvous:RelayEndpoint` 配置为客户端可达的中继公网地址。生产环境必须为两个服务配置新的 `RelaySharedKey`。

## 后台服务（被控端）

被控端由一个 Windows 服务（LocalSystem）托管，它本身无法采集屏幕或注入输入（Session 0），所以它在当前控制台会话里启动并监督一个 **Agent**
（`RemoteDesktop.SessionAgent`），屏幕和输入由 Agent 完成，服务只负责连接、鉴权、授权和转发：

- 输入桌面在用户桌面和登录界面 / 锁屏 / UAC 之间切换时，服务会换上另一种 Agent（用户令牌 / winlogon 的 SYSTEM 令牌），
  控制端的画面不中断；注销、登录、快速用户切换、Agent 崩溃同理。
- Ctrl+Alt+Del 不能用按键注入，只能由服务调用 `SendSAS`（安装脚本会打开所需策略 `SoftwareSASGeneration`）。
- 客户端检测到服务后，设备代码和临时密码显示的是服务的；客户端自己只负责发起连接。`--HostMode standalone` 可忽略服务。

```
# 管理员 PowerShell
dotnet publish src/Hosts/RemoteDesktop.HostService -c Release -o artifacts/host
.uildserviceinstall-service.ps1 -SourceDirectory artifactshost -RendezvousUri ws://<服务器>:5080/ws
.uildserviceNinstall-service.ps1
```

日志在 `%ProgramData%RemoteDesktopogs`（`service-*.log`、`agent-*.log`），设备身份在同目录的 `secrets`（仅 SYSTEM、管理员可访问）。
不安装服务也能开发调试：以普通用户直接运行 `RemoteDesktop.HostService.exe --DataDirectory <目录> --HostControlPipe RemoteDesktop.HostControl.Dev`，
客户端加同样的 `--HostControlPipe` 参数；此时只能控制用户桌面，看不到登录界面和 UAC。

## 代码结构



依赖只能向内：`Domain ← Abstractions ← Protocol ← Transport ← Application ← Features ← Hosts`。
`Platform.*` 和 `Media` 只依赖 `Abstractions`（平台端口接口）。以上规则由 `tests/RemoteDesktop.ArchitectureTests` 强制检查。

| 目录 | 内容 |
|---|---|
| `src/Core/RemoteDesktop.Domain` | 设备 ID、会话、权限、观看模式规则（`AccessGrant`） |
| `src/Core/RemoteDesktop.Abstractions` | 平台端口：屏幕采集、输入注入、编解码、音频、摄像头、PTY、剪贴板、会话启动、密钥存储 |
| `src/Core/RemoteDesktop.Protocol` | 通道类型、协议版本、帧格式 `[len][type][payload]` |
| `src/Core/RemoteDesktop.Signaling` | 客户端与服务器之间的信令协议（protobuf）和 UDP 控制包 |
| `src/Core/RemoteDesktop.Transport` | QUIC 对端连接、证书指纹固定、NAT 打洞、中继回退、Rendezvous 客户端 |
| `src/Core/RemoteDesktop.Application` | 会话握手与密码鉴权、失败限流、功能模块契约 `IFeatureModule`、按权限分发通道的 `ChannelDispatcher` |
| `src/Features/*` | 各功能模块：Desktop（视频与输入，已实现）、FileTransfer、Terminal、Camera |
| `src/Media` | 编解码器选择（硬件优先、软件兜底）、BT.709 颜色转换、码率自适应 |
| `src/Platform/RemoteDesktop.Platform.Windows` | Windows 平台实现：DXGI 屏幕采集、SendInput、Media Foundation H.264 编解码（NVENC / QSV / AMF，回退到系统自带软件编码器）、DPAPI |
| `src/Hosts/RemoteDesktop.Client` | Avalonia 客户端 |
| `src/Hosts/RemoteDesktop.HostService` | 被控端 Windows 服务：注册设备、鉴权、把屏幕和输入通道转发给 Agent、给 UI 提供控制管道 |
| `src/Hosts/RemoteDesktop.SessionAgent` | 由服务在交互会话（用户桌面或登录界面）里启动的 Agent：屏幕采集、编码、输入注入 |
| `src/Server/*` | Rendezvous（ID / 信令）与 Relay（UDP 中继）服务器 |

## 新增功能或平台

- 新功能：新建 `src/Features/RemoteDesktop.Features.X`，实现 `IFeatureModule`，提供 `AddXFeature()` 扩展方法，在宿主中注册。
- 新平台：新建 `src/Platform/RemoteDesktop.Platform.X`，实现 `Abstractions` 中的端口，提供 `AddXPlatform()` 扩展方法。

## 里程碑

M0 骨架 → M1 网络（QUIC、打洞、中继） → M2 远程控制 MVP → M3 服务化（UAC / 登录界面，当前） → M4 观看模式 / 剪贴板 / 音频 / 多显示器 → M5 文件传输 → M6 终端 → M7 摄像头 → M8 安装包（WiX v5）与加固。
