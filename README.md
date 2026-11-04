# Remote Desktop

[![CI](https://github.com/gaozeqian/RemoteDesktop/actions/workflows/ci.yml/badge.svg)](https://github.com/gaozeqian/RemoteDesktop/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-proprietary-blue)](LICENSE)

跨平台远程桌面软件（类 ToDesk）：远程控制、观看模式、文件传输、远程终端、远程摄像头。
后端 .NET 10，前端 Avalonia 12，**支持 Windows、macOS (Apple Silicon) 与 Linux**。

端到端加密、连接密码从不传输明文、被控端逐项授权与随时收回——安全边界与"还没做什么"都写在
[`docs/design/README.md`](docs/design/README.md) 里。

## 这是什么

被控端（本机）默认是"关着"的：要连上来必须知道设备代码和密码。连接建好后，控制端能做什么由被控端逐项授予，
**授予和收回都由被控端执行**，不依赖控制端界面是否配合。

三个平台的产品形状不同，因为桌面不同：

| | Windows | macOS | Linux |
|---|---|---|---|
| 被控端是什么 | LocalSystem 服务 + 用户会话里的 Agent | 用户会话里的 app（`Remote Desktop.app`，或独立的、隐藏于 `Library/Application Support` 的 `Remote Desktop Host.app` 后台组件） | 图形会话里的 `systemd --user` 服务 |
| 登录前的远程访问 | 支持 | 不支持 | 不支持 |
| 无人值守（固定密码） | 支持 | 仅登录后 | **不支持**（portal 不允许控制会话持久化） |

**没有的能力不会假装成功**：被控端不提供的能力，控制端界面里不会出现对应的按钮，而不是放一个点了没反应的按钮。
完整的能力矩阵、各平台不支持的东西及其原因见 [`docs/usage/README.md`](docs/usage/README.md)。

## 能力

- **远程桌面**：被控端真实指针、多显示器切换、剪贴板双向同步、被控端音频回传。
- **观看模式**：只读，被控端可对单个会话随时降级为"仅观看"或断开。
- **文件传输**：双栏管理器、文件夹递归、拖放、可暂停/重试、断点续传与 xxHash64 校验。
- **远程终端**：自带 VT 模拟器（16/256/真彩色、备用屏幕、宽字符、鼠标上报），已用真实 PowerShell / vim / less / nano 验证。
- **远程摄像头**：被控端始终可见且可单会话关停。
- **无人值守**：固定密码 + 防休眠（Keep-Awake）。

每项功能怎么用、有什么边界见 [`docs/usage/README.md`](docs/usage/README.md)。

## 构建与测试

```bash
dotnet build RemoteDesktop.slnx
dotnet test RemoteDesktop.slnx
```

依赖只能向内：`Domain ← Abstractions ← Protocol ← Transport ← Application ← {Features, Hosts}`
（`Signaling` 是 `Transport` 依赖的独立信令契约，自己不依赖任何内部项目），
由 `tests/RemoteDesktop.Architecture.UnitTests` 强制检查。环境要求、测试项目命名、架构规则与产物布局见
[`CONTRIBUTING.md`](CONTRIBUTING.md)；系统架构见 [`docs/design/README.md`](docs/design/README.md)。

安装包（自包含发布，目标机器不需要装 .NET）。三个平台各出三个角色——控制端、被控端、服务端——共九个：

```bash
# Windows MSI（需要 .NET SDK 与网络还原 WiX 包；用 pwsh 7 跑）
pwsh -File build/installer/windows/client/Build-ClientInstaller.ps1
pwsh -File build/installer/windows/host/Build-HostInstaller.ps1
pwsh -File build/installer/windows/server/Build-ServerInstaller.ps1

# macOS .app / .pkg（需要 Xcode Command Line Tools）
build/installer/macos/client/make-app.sh --configuration Release
build/installer/macos/client/make-pkg.sh --configuration Release
build/installer/macos/host/make-pkg.sh   --configuration Release
build/installer/macos/server/make-pkg.sh --configuration Release

# Linux .deb（在 Linux 上构建）
build/installer/linux/client/build-deb.sh
build/installer/linux/host/build-deb.sh
build/installer/linux/server/build-deb.sh
```

各角色装什么、怎么安装与配置（服务端那一个包装的是信令、中继、监控台和监控台的 HTTPS 网关四个程序；macOS 的被控端是独立的
无窗口后台组件，TCC 授权与客户端分开）见 [`docs/ops/README.md`](docs/ops/README.md)，三个角色的边界见
[`docs/usage/README.md`](docs/usage/README.md)。

## 快速开始

下面的命令适用于 Windows 与 Linux；**macOS 不需要 `dotnet run`**，改用服务端 `.pkg` 加直接运行 bundle 里的
可执行文件，见 [`CONTRIBUTING.md`](CONTRIBUTING.md) §本地联调。

每条 `dotnet run` 都会一直运行，所以下面四条各开一个终端。

```bash
# 1. 服务端（Development 配置里带有开发用的中继共享密钥）
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server/RemoteDesktop.Server.Rendezvous
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server/RemoteDesktop.Server.Relay

# 2. 两个客户端，各用不同的数据目录（即不同的设备身份）
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory /tmp/rd1
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory /tmp/rd2
```

Windows PowerShell 不支持 `VAR=值 命令` 的前缀写法，要先设环境变量，数据目录也用 Windows 路径：

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Server/RemoteDesktop.Server.Rendezvous      # Relay 同理，另开一个终端

dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory "$env:TEMP\rd1"
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory "$env:TEMP\rd2"
```

客户端默认连 `ws://127.0.0.1:5080/ws`，所以服务端一起来就能联调：两个窗口互相输入对方的设备代码和临时密码。

跨机器测试**不是**放通端口、改个服务器地址就行：信令默认只监听回环，客户端也默认拒绝连非回环的 `ws://`，
两端要显式放开（或改用 `wss://`）。步骤、端口和启动参数见 [`CONTRIBUTING.md`](CONTRIBUTING.md) §本地联调
的"跨机器测试"。**生产环境必须为两个服务配置新的 `RelaySharedKey`，并且信令要走 `wss://`**——做法见
[`docs/ops/README.md`](docs/ops/README.md)。各平台的联调细节（Windows 的 PowerShell 写法、Linux 的 libmsquic
`LD_LIBRARY_PATH`、macOS 怎么跑两个实例）也在 `CONTRIBUTING.md` 的同一节。

## 文档

**[文档中心（索引）→](docs/README.md)** —— 使用、设计、运维、平台移植四份正文和文档规范都从那里进入；那份清单是唯一的一份，这里不再重列。出问题了先看 [运维文档](docs/ops/README.md) 的"故障速查"，写代码前先看 [`CONTRIBUTING.md`](CONTRIBUTING.md)。

变更历史见 [`CHANGELOG.md`](CHANGELOG.md)；第三方组件见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)；
安全漏洞上报方式见 [`SECURITY.md`](SECURITY.md)。

## 许可证

专有许可，见 [`LICENSE`](LICENSE)。**包内许可页显示的文本就是这一份**，打包时由
`build/installer/New-LicenseRtf.ps1` 转成 RTF，所以要改许可条款改这个文件就够了。
