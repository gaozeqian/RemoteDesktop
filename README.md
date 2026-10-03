# RemoteDesktop

跨平台远程桌面软件（类 ToDesk）：远程控制、观看模式、文件传输、远程终端、远程摄像头。
后端 .NET 10，前端 Avalonia 12。第一阶段仅支持 Windows，macOS / Linux 在后续阶段通过新增平台项目接入。

## 构建与测试

```
dotnet build RemoteDesktop.slnx
dotnet test RemoteDesktop.slnx
```

## 代码结构

依赖只能向内：`Domain ← Abstractions ← Protocol ← Transport ← Application ← Features ← Hosts`。
`Platform.*` 和 `Media` 只依赖 `Abstractions`（平台端口接口）。以上规则由 `tests/RemoteDesktop.ArchitectureTests` 强制检查。

| 目录 | 内容 |
|---|---|
| `src/Core/RemoteDesktop.Domain` | 设备 ID、会话、权限、观看模式规则（`AccessGrant`） |
| `src/Core/RemoteDesktop.Abstractions` | 平台端口：屏幕采集、输入注入、编解码、音频、摄像头、PTY、剪贴板、会话启动、密钥存储 |
| `src/Core/RemoteDesktop.Protocol` | 通道类型、协议版本、帧格式 `[len][type][payload]` |
| `src/Core/RemoteDesktop.Transport` | 对端连接 / 通道接口（QUIC 实现见 M1） |
| `src/Core/RemoteDesktop.Application` | 功能模块契约 `IFeatureModule`，按权限分发通道的 `ChannelDispatcher` |
| `src/Features/*` | 各功能模块：Desktop、FileTransfer、Terminal、Camera |
| `src/Media` | 编解码器选择（硬件优先、软件兜底） |
| `src/Platform/RemoteDesktop.Platform.Windows` | Windows 平台实现 |
| `src/Hosts/RemoteDesktop.Client` | Avalonia 客户端 |
| `src/Hosts/RemoteDesktop.HostService` | 被控端 Windows 服务 |
| `src/Hosts/RemoteDesktop.SessionAgent` | 在用户会话内负责采集和输入的 Agent |
| `src/Server/*` | Rendezvous（ID / 信令）与 Relay（UDP 中继）服务器 |

## 新增功能或平台

- 新功能：新建 `src/Features/RemoteDesktop.Features.X`，实现 `IFeatureModule`，提供 `AddXFeature()` 扩展方法，在宿主中注册。
- 新平台：新建 `src/Platform/RemoteDesktop.Platform.X`，实现 `Abstractions` 中的端口，提供 `AddXPlatform()` 扩展方法。

## 里程碑

M0 骨架（当前） → M1 网络（QUIC、打洞、中继） → M2 远程控制 MVP → M3 服务化（UAC / 登录界面） → M4 观看模式 / 剪贴板 / 音频 / 多显示器 → M5 文件传输 → M6 终端 → M7 摄像头 → M8 安装包（WiX v5）与加固。
