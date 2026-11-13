# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

跨平台远程桌面软件（类 ToDesk）：远程控制、观看模式、文件传输、远程终端、远程摄像头。后端 .NET 10，控制端 UI 为 Avalonia 12，支持 Windows、macOS (Apple Silicon) 与 Linux。SDK 由 `global.json` 固定为 `10.0.402`（`rollForward: disable`）。

三个角色：

- **控制端** `RemoteDesktop.Client`：Avalonia 客户端，同一份代码在三平台构建运行，启动时按 OS 选择平台层。
- **被控端** `RemoteDesktop.HostService`：Windows 上是 LocalSystem 服务加用户会话里的 `RemoteDesktop.SessionAgent`（Session 0 看不到桌面）；macOS 与 Linux 上是用户图形会话里的单进程宿主（macOS 为 `Remote Desktop Host.app`，Linux 为 `systemd --user` 服务）。
- **服务端**：`Server.Rendezvous`（WebSocket 信令）、`Server.Relay`（UDP 中继）、`Server.Ops*`（运维监控台）、`Server.Gateway`（监控台 YARP HTTPS 入口）、`Server.Storage`（设备注册表持久化）、`Server.Shared`（服务端公共契约）。

仓库的文档、PR 模板与 git 提交信息是中文；代码标识符与注释以英文为主。写或改文档前先读 [docs/STYLE.md](docs/STYLE.md)。

## 常用命令

```bash
# 还原与编译（CI、Release、Docker 与打包脚本都要求锁文件模式）
dotnet restore RemoteDesktop.slnx --locked-mode
dotnet build RemoteDesktop.slnx --no-restore

# 全部测试（含架构依赖、仓库结构与文档规范断言）
dotnet test --solution RemoteDesktop.slnx --no-build
```

C# 没有单独的 lint 命令：`Directory.Build.props` 打开了 `TreatWarningsAsErrors`、`AnalysisLevel=latest-recommended` 与 `EnforceCodeStyleInBuild`，编译即 lint。

只改了文档也要跑：

```bash
dotnet test --project tests/RemoteDesktop.Architecture.UnitTests
```

单项目或单个用例（Microsoft.Testing.Platform 语法，过滤参数放在 `--` 之后）：

```bash
dotnet test --project tests/RemoteDesktop.Core.UnitTests/RemoteDesktop.Core.UnitTests.csproj \
  -- --filter-class "*VideoCodecSelectorTests*"
```

这条命令不是 VSTest：`--logger` 会被拒绝或忽略，`--nologo` 会让测试程序以退出码 5 失败；解决方案必须用 `--solution` 指定，位置参数会让整次运行跑 0 个用例。需要 TRX 报告时用 `--report-trx --results-directory artifacts/test-results/<平台>`；不带 `--results-directory` 会在当前目录留下一个空的 `TestResults/`，那是 MTP 的默认值。

## 测试组织

`tests/` 项目名一律 `<区域>.<种类>Tests`，种类只有 `UnitTests`（不需要外部服务）与 `IntegrationTests`（真网络、真进程或容器）。后缀必须从这两个里取，否则按类型筛选会静默漏掉。

区域不总等于目录名：

- `RemoteDesktop.Core.UnitTests`：Core **加 Features 与 Media**（引用 `Application`、四个 `Features.*`、`Media`、`Server.Shared`）。功能层单测默认放这里。
- `RemoteDesktop.Transport.IntegrationTests`：信令、中继与功能通道的端到端联调（也引用 `Application`、`Features.*`、`Platform.Windows` / `Platform.MacOS` 与两个服务端项目）。
- `RemoteDesktop.Architecture.UnitTests`：不是某个区域，是规则本身——分层依赖 `DependencyRuleTests`、目录与文件命名 `RepositoryStructureTests`、文档规范 `DocumentationTests`。
- 平台专有用例放对应平台的 `UnitTests` 项目，其他系统用 `Assert.SkipUnless(...)` 跳过。

默认会跳过、需要环境变量或本机能力才跑的用例：

- `RD_TEST_POSTGRES`：真实 PostgreSQL（设备注册表与监控台存储）。本机起法与连接串见 [CONTRIBUTING.md](CONTRIBUTING.md)。
- `RD_INSTALLER_MSI` / `RD_SERVER_INSTALLER_MSI`：安装包测试。不设时去 `artifacts/installer/windows/` 找最新的 `RemoteDesktop-<角色>-Windows-*.msi`，找不到就跳过。
- `RD_PORTAL_TEST=1`：Linux portal 用例，会弹真实授权框。
- `RD_CAMERA_RATE_TEST=1`：真实摄像头帧率用例。

## 本地联调

```bash
# 终端 1、2：Development 配置自带开发用的中继共享密钥
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server/RemoteDesktop.Server.Rendezvous
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server/RemoteDesktop.Server.Relay

# 终端 3、4：不同 DataDirectory = 不同设备身份
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory /tmp/rd1
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory /tmp/rd2
```

客户端默认连 `ws://127.0.0.1:5080/ws`。Windows PowerShell 要先设 `$env:ASPNETCORE_ENVIRONMENT`，路径用 `"$env:TEMP\rd1"` 这类写法。macOS 不用 `dotnet run`：按 [CONTRIBUTING.md](CONTRIBUTING.md) 的“本地联调”用 `.pkg` 安装服务端，再直接运行 `.app` 里的可执行文件。Linux 开发时要让 `libmsquic` 可被找到（`native/linux/fetch-libmsquic.sh` 或 `LD_LIBRARY_PATH`），否则报 `QUIC is not supported on this system`。跨机器测试需要两端显式放开非回环明文信令（仅限隔离实验网）；生产环境必须用 `wss://` 并更换 `RelaySharedKey`。

## 分层与模块职责

依赖只能由外向内，由 `tests/RemoteDesktop.Architecture.UnitTests/DependencyRuleTests.cs` 强制。文档与测试不一致时以测试为准。

```text
Domain ← Abstractions ← Protocol ← Transport ← Application ← Features ← Hosts
           ↑      ↑                  ↑
     Platform.*  Media           Signaling（独立契约，无内部依赖）
```

- `Domain`：设备身份、`AccessGrant`、会话实体。无项目依赖。
- `Abstractions`：平台必须实现的端口（屏幕采集、输入注入、编解码、音频、PTY、剪贴板、密钥存储等）。
- `Protocol`：应用层消息、`ChannelType`、帧格式、`*.proto`。
- `Signaling`：信令 WebSocket 契约与 UDP 控制包；不引用任何内部项目，`Transport` 与 `Server.Shared` 引用它。
- `Transport`：QUIC 连接与监听、证书指纹固定、NAT 反射与打洞、中继回退、Rendezvous 客户端。
- `Application`：会话生命周期、SRP-6a 鉴权、失败限流、`IFeatureModule` 契约与 `ChannelDispatcher` 授权、`DataDirectory`、本机控制管道、`remotedesktop://` 链接。
- `Features.*`：`Desktop`、`FileTransfer`、`Terminal`、`Camera`。彼此不得引用，只能通过 `Application` 协作；`Desktop` 与 `Camera` 另引用 `Media`。没有插件加载，各宿主在组合根里静态注册（`AddDesktopFeature()` 等）。
- `Media`：编解码选择、码率控制、BGRA 与 NV12 转换、Opus。
- `Platform.Windows` / `MacOS` / `Linux`：同一批端口的原生实现；Linux 与 macOS 共用 `Platform.Posix`（PTY、0600 密钥库等）。平台项目一律是 `net10.0`（不是 `net10.0-windows` 或 `net10.0-macos`），宿主运行时选择，发布时由 ILLink 裁掉其余平台。
- `Hosts`：组合根。`ClientPlatformRegistration` 按 OS 注册平台层；`HostService` 在 Windows 走“服务 + SessionAgent 转发”，在 macOS 与 Linux 直接采集与注入。
- `Server.*`：不引用 `Abstractions`、`Protocol`、`Transport`、`Application`、`Features`、`Media`、`Platform` 或宿主，只与客户端共享 `Signaling`。监控台三层只能向内：`Ops.Core` ← `Ops.Storage` ← `Ops`（Web 宿主），且都不引用 `Server.Storage`。

新代码放哪由这张图决定：`Features` 不得引用 `Platform` 或宿主，平台由宿主在启动时接入。

## 运行时协议要点

- **信令**：WebSocket 路径 `/ws`，默认 TCP 5080；二进制 Protobuf `ClientMessage` / `ServerMessage`，单条上限 64 KiB。设备注册用证书私钥对 nonce 签名，服务器分配 9 位设备代码。
- **传输**：QUIC（TLS 1.3，ALPN `rd/1`），双方固定对端证书指纹，实现端到端加密；默认 UDP 反射端口 5081、Relay 端口 5082。中转服务器只转发密文。
- **应用层**：每条 QUIC 流首字节是 `ChannelType`，之后每帧为 `[uint32 payload_length][uint16 message_type][payload]`（大端）。通道编号：0 Control、1 Video、2 Audio、3 Input、4 Clipboard、5 FileTransfer、6 Terminal、7 Camera。
- **鉴权**：协议主版本 3，SRP-6a；密码从不明文传输，双方证书指纹进入 SRP identity；被控端返回服务器证据 M2，控制端必须验证。
- **授权**：`ChannelDispatcher` 在功能模块看到通道之前按 `AccessGrant` 授权；观看模式强制只读；被控端可随时降级或收回，执行不依赖控制端配合。

改动线协议要两端一起改（控制端与被控端或服务端）。`PeerTls`、`RendezvousClient`、`SignalingEndpoint`、`RendezvousOptions` 里的超时与限额常量是跨端契约，改一处等于改两端。

## 仓库约定

- **中央包管理**：版本只写在 `Directory.Packages.props`，项目文件不写 `Version`。所有项目提交 `packages.lock.json`，生产项目另有 `packages.win-x64.lock.json`、`packages.linux-x64.lock.json`、`packages.osx-arm64.lock.json`。改依赖后按 RID 用 `dotnet restore ... --force-evaluate` 更新锁文件，并重新生成 `THIRD-PARTY-NOTICES.md`（`build/tools/Export-ThirdPartyNotices.ps1`，不要手改，CI 会 diff）。
- **产物布局**：所有 `dotnet build` / `publish` / `pack` 输出在 `artifacts/`（`UseArtifactsOutput`）。仓库脚本另按 `<类别>/<平台>/` 写 `artifacts/installer|app|lib|test-results/<平台>/`。清空构建产物删 `artifacts/bin` 与 `artifacts/obj`；`build/installer/windows/` 下的 WiX 工程是例外（自带空的 `Directory.Build.props`，产物在工程目录内），要一起清用 `git clean -xdf build/installer`（先 `git clean -xdn` 看一遍）。
- **版本与发版**：版本号只写在 `Directory.Build.props` 的 `<Version>`。发版时 `release.yml` 的 `check-version` 要求 tag `v<版本>` 与之相等，且 `CHANGELOG.md` 有 `## [<版本>]` 小节。日常改动写进 `CHANGELOG.md` 的 `## [未发布]`。
- **文档**：新文档必须加进 `docs/README.md` 索引，否则 `DocumentationTests` 变红；`docs/` 根只放索引与 `STYLE.md`；`docs/` 下文件名用小写 kebab-case；移动或改名后全仓搜旧路径（`.md`、`.cs` 注释、`.yml`、`.sh`、`.ps1`）。格式由 markdownlint 管：`npx --yes markdownlint-cli2@0.23.3 "**/*.md"`。
- **换行符**：由 `.gitattributes` 统一，不在 `.editorconfig` 里另设。`.sh`、`Dockerfile`、Linux 包脚本、`.plist`、`.service`、`.yml` 必须 LF；`*.ps1`、`*.wxs`、`*.csproj`、`*.props`、`*.slnx` 为 CRLF。`build/installer/windows` 下由 Windows PowerShell 5.1 读取的打包脚本还要求 UTF-8 带 BOM；编辑时保留原编码，不要用会去掉 BOM 的工具重写。
- **仓库结构断言**：`src/` 与 `tests/` 的目录名等于 `.csproj` 名；`.cs` 文件名要么是声明的类型，要么是声明两个以上类型的分组概念；`.sh` 用 kebab-case，`.ps1` 用 Verb-Noun（动词取自 PowerShell 批准动词表）。改这些脚本前先看 `RepositoryStructureTests.cs`。
- **平台名拼写**：文件系统路径全小写（`build/installer/macos/`、`artifacts/installer/macos/`）；C# 项目与命名空间 PascalCase（`RemoteDesktop.Platform.MacOS`）；RID 按 SDK 写（`osx-arm64`）；散文里写 Apple 的 `macOS`。
- **服务端配置**：`--configure-system` 的参数为 null 表示“这次不改这一项”，空串或 `auto` 才是明确表态；不要改成“空值即清空”。

## 详细文档入口

- [CONTRIBUTING.md](CONTRIBUTING.md)：环境、测试命名、架构依赖规则、产物布局、发布约定。
- [docs/design/README.md](docs/design/README.md)：分层细节、通信协议全文、安全边界（含“还没有做的事”）。
- [docs/usage/README.md](docs/usage/README.md)：能力矩阵与各平台不支持项。
- [docs/ops/README.md](docs/ops/README.md)：部署、安装包构建、TLS、监控台、故障速查。
- [docs/ports/README.md](docs/ports/README.md)：平台移植的硬约束与未验证项。
- [build/README.md](build/README.md)：打包脚本分层与命名。
- [native/README.md](native/README.md)：原生源码与本地库获取。
- [SECURITY.md](SECURITY.md)：漏洞上报与安全边界。

文档索引只有 [docs/README.md](docs/README.md) 一份，不要在别处重列。
