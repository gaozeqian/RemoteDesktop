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

## 远程桌面窗口的功能

- **被控端指针**：画面里不含鼠标，客户端把被控端真实的指针形状和位置画在叠加层上（控制时隐藏本地指针，观看时保留）。
- **多显示器**：被控端有多个显示器时，工具栏出现下拉框可切换；显示器拔插时自动回落到主显示器。
- **剪贴板**：文本和图片双向同步，只同步连接之后的变化；观看模式下不开放。
- **音频**：被控端正在播放的声音（WASAPI 环回，Opus 编码）在控制端播放，可静音；输出设备切换后自动跟随；观看模式下仍可听。
- **被控端的权限**：被控端界面列出正在控制本机的人，可随时点"仅观看"（立即关闭其输入、剪贴板等通道，控制端界面随之变化）或"断开"。
- **文件传输**：见下一节。
- **远程终端**：见"远程终端"一节。

## 文件传输

工具栏的"文件"按钮（观看模式或被控端收回权限后不显示）打开双栏文件管理器：左边本机，右边被控端。

- 选中文件或文件夹后点"上传 →"/"← 下载"，文件夹会递归复制；也可以把资源管理器里的文件直接拖到右侧。
  "文件已存在时"可选覆盖、跳过或保留两者（重命名为 `名称 (1).扩展名`）。
- 传输列表里每项可暂停、继续、取消；失败的可以重试。最多同时传 3 个，其余排队。暂停、网络中断、关闭窗口都不会丢进度（取消才会丢弃）。
- **断点续传与校验**：没传完的文件以 `<目标名>.<大小>-<修改时间>.rdpart` 保存在目标文件夹，下次从已有长度继续（大小或修改时间变了就不会误续）。
  传完后比较整个文件的 xxHash64，一致才改成目标名并设置修改时间，所以目标名下永远不会出现写了一半的文件；
  校验不一致时丢弃临时文件并自动从头重传一次。取消会删除临时文件。
- **安全**：文件操作由 Agent 以登录用户的身份执行，从不以 SYSTEM 执行；被控端只有登录界面 / UAC 可用时一律拒绝。
  路径必须是绝对路径，拒绝设备路径（`\\.\`、`\\?\`）、NTFS 备用数据流和 `CON`、`NUL` 等保留名，`..` 先解析再检查。
  `AddFileTransferFeature(new FileAccessPolicy([根目录…]))` 可把可访问范围限制在指定目录内（默认不限制，即登录用户能访问的都可以）。
  观看模式由被控端拒绝文件通道。会话 ID、操作类型和路径写入 Agent 日志（`agent-*.log`）作为审计记录。
- 服务到 Agent 的文件通道是无损转发：带背压、不丢帧、不因为 Agent 被替换而续传到另一个 Agent；Agent 丢失时传输中止，重试即可续传。

## 远程终端

工具栏的"终端"按钮（观看模式或被控端收回权限后不显示）打开终端窗口：每个标签是被控端上的一个 shell，可以同时开多个。

- 被控端用 Windows 伪控制台（ConPTY）运行 PowerShell 7（有则用）、Windows PowerShell 或 cmd，窗口顶部可选 shell。
  控制端只能选被控端列出的名字，不能指定程序路径；同一台被控端最多同时开 8 个终端。
- shell 由 Agent 以登录用户的身份启动，从不以 SYSTEM 启动；只有登录界面 / UAC 可用时拒绝。shell 及它启动的子进程放在作业对象里，
  关闭标签、断开连接或 Agent 退出都会把它们一起结束。打开、退出和关闭写入 Agent 日志（`agent-*.log`），不记录键入内容。
- 终端窗口是自带的 VT 模拟器（`Features.Terminal/Emulation`）：xterm 兼容的光标与滚动区、16 / 256 / 真彩色、粗体斜体下划线、
  备用屏幕（vim、less、nano 用）、滚动缓冲（5000 行）、宽字符（中日韩和 emoji 占两格）、括号粘贴、鼠标上报（vim、htop 用）、窗口大小随窗口变化。
  已用真实的 PowerShell（含 PSReadLine 的历史与行编辑）、vim、less、nano 验证。
- 操作：选中文字即可复制（Ctrl+Shift+C，或有选中时按 Ctrl+C；双击选词，三击选行）；Ctrl+V、Shift+Insert、右键或中键粘贴**控制端**剪贴板的内容；
  Shift+PageUp / PageDown 或滚轮翻看历史；程序开启鼠标上报时按住 Shift 可以照常选中文字。
- 限制：被控端锁屏、注销或切换到安全桌面时 Agent 会被替换，终端随之结束（窗口会提示，重新开一个即可）。

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
.\build\service\install-service.ps1 -SourceDirectory artifacts\host -RendezvousUri ws://<服务器>:5080/ws
.\build\service\uninstall-service.ps1
```

日志在 `%ProgramData%\RemoteDesktop\logs`（`service-*.log`、`agent-*.log`），设备身份在同目录的 `secrets`（仅 SYSTEM、管理员可访问）。
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
| `src/Features/*` | 各功能模块：Desktop（视频、输入、剪贴板、音频）、FileTransfer（文件浏览与传输）、Terminal（ConPTY 终端与 VT 模拟器）、Camera |
| `src/Media` | 编解码器选择（硬件优先、软件兜底）、BT.709 颜色转换、码率自适应 |
| `src/Platform/RemoteDesktop.Platform.Windows` | Windows 平台实现：DXGI 屏幕采集、SendInput、Media Foundation H.264 编解码（NVENC / QSV / AMF，回退到系统自带软件编码器）、DPAPI |
| `src/Hosts/RemoteDesktop.Client` | Avalonia 客户端 |
| `src/Hosts/RemoteDesktop.HostService` | 被控端 Windows 服务：注册设备、鉴权、把屏幕、输入、剪贴板、音频、文件、终端通道转发给 Agent、给 UI 提供控制管道 |
| `src/Hosts/RemoteDesktop.SessionAgent` | 由服务在交互会话（用户桌面或登录界面）里启动的 Agent：屏幕采集、编码、输入注入、文件操作和终端（以登录用户身份） |
| `src/Server/*` | Rendezvous（ID / 信令）与 Relay（UDP 中继）服务器 |

## 新增功能或平台

- 新功能：新建 `src/Features/RemoteDesktop.Features.X`，实现 `IFeatureModule`，提供 `AddXFeature()` 扩展方法，在宿主中注册。
- 新平台：新建 `src/Platform/RemoteDesktop.Platform.X`，实现 `Abstractions` 中的端口，提供 `AddXPlatform()` 扩展方法。

## 里程碑

M0 骨架 → M1 网络（QUIC、打洞、中继） → M2 远程控制 MVP → M3 服务化（UAC / 登录界面） → M4 观看模式 / 剪贴板 / 音频 / 多显示器 → M5 文件传输 → M6 终端（当前） → M7 摄像头 → M8 安装包（WiX v5）与加固。
