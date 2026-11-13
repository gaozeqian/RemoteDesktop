# CLAUDE.md

本文件是 Claude Code 的最短上手与易踩坑清单。项目规范以 [CONTRIBUTING.md](CONTRIBUTING.md) 与
[docs/](docs/README.md) 为准；文档与测试不一致时以测试为准。

## 项目概览

跨平台远程桌面软件（类 ToDesk）：远程控制、观看模式、文件传输、远程终端、远程摄像头。.NET 10，SDK 由
`global.json` 固定为 `10.0.402`（`rollForward: disable`）；控制端 UI 为 Avalonia 12；支持 Windows、
macOS (Apple Silicon) 与 Linux。仓库的文档、PR 模板与提交信息用中文，代码标识符与注释以英文为主。

三个角色：控制端 `RemoteDesktop.Client`；被控端 `RemoteDesktop.HostService`（Windows 是服务 +
`SessionAgent`，macOS/Linux 是用户图形会话里的单进程）；服务端 `Server.Rendezvous`、`Server.Relay`、
`Server.Ops*`、`Server.Gateway` 与共享的 `Server.Storage`/`Server.Shared`。启动方式与进程模型见
[docs/design/README.md](docs/design/README.md)。

## 常用命令

```bash
# 还原与编译（CI、Release、Docker 与打包脚本都要求锁文件模式）
dotnet restore RemoteDesktop.slnx --locked-mode
dotnet build RemoteDesktop.slnx --no-restore

# 全部测试（含架构依赖、仓库结构与文档规范断言）
dotnet test --solution RemoteDesktop.slnx --no-build
```

C# 没有单独的 lint 命令：`TreatWarningsAsErrors`、`AnalysisLevel=latest-recommended` 与
`EnforceCodeStyleInBuild` 已打开，编译即 lint。只改了文档也要跑
`dotnet test --project tests/RemoteDesktop.Architecture.UnitTests`。

单项目或单个用例（Microsoft.Testing.Platform 语法，过滤参数放在 `--` 之后）：

```bash
dotnet test --project tests/RemoteDesktop.Core.UnitTests/RemoteDesktop.Core.UnitTests.csproj \
  -- --filter-class "*VideoCodecSelectorTests*"
```

这不是 VSTest：`--logger` 会被拒绝或忽略，`--nologo` 会让测试程序以退出码 5 失败；解决方案必须用
`--solution` 指定，位置参数会让整次运行跑 0 个用例。需要 TRX 时用
`--report-trx --results-directory artifacts/test-results/<平台>`；不带 `--results-directory` 会在当前目录
留下一个空的 `TestResults/`，那是 MTP 的默认值。

## 测试地图

- `tests/` 项目名一律 `<区域>.<种类>Tests`，种类只有 `UnitTests` 与 `IntegrationTests`。
- `RemoteDesktop.Core.UnitTests` 覆盖 Core **加 Features 与 Media**：功能层单测默认放这里，不按项目名推。
- 平台专有用例放对应平台的 `UnitTests`，其他系统用 `Assert.SkipUnless(...)` 跳过。
- 需要环境变量或本机能力才跑的用例：`RD_TEST_POSTGRES`、`RD_INSTALLER_MSI`/`RD_SERVER_INSTALLER_MSI`、
  `RD_PORTAL_TEST=1`、`RD_CAMERA_RATE_TEST=1`。本机起法与完整项目表见
  [CONTRIBUTING.md](CONTRIBUTING.md) 的“测试项目命名”。

## 本地联调

```bash
# 终端 1、2：Development 配置自带开发用的中继共享密钥
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server/RemoteDesktop.Server.Rendezvous
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server/RemoteDesktop.Server.Relay

# 终端 3、4：不同 DataDirectory = 不同设备身份
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory /tmp/rd1
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory /tmp/rd2
```

客户端默认连 `ws://127.0.0.1:5080/ws`。PowerShell 写法、macOS 的 `.app`/`.pkg` 步骤与 Linux 的 `libmsquic`
要求见 [CONTRIBUTING.md](CONTRIBUTING.md) 的“本地联调”。跨机器测试需要两端显式放开非回环明文信令（仅限隔离
实验网）；生产环境必须用 `wss://` 并更换 `RelaySharedKey`。

## 容易踩的坑

- **线协议要两端一起改**：信令/Protobuf、QUIC 通道与帧格式、证书指纹、SRP-6a、`AccessGrant` 都属于跨端契约；
  `PeerTls`、`RendezvousClient`、`SignalingEndpoint`、`RendezvousOptions` 里的超时与限额改一处等于改两端。
  协议全文见 [docs/design/README.md](docs/design/README.md)。
- **分层由测试强制**：依赖只能由外向内，规则在
  `tests/RemoteDesktop.Architecture.UnitTests/DependencyRuleTests.cs`；`Features.*` 不得引用 `Platform` 或宿主，
  平台由宿主在启动时接入。新代码放哪见 [docs/design/README.md](docs/design/README.md)。
- **构建产物**：`dotnet build`/`publish`/`pack` 输出在 `artifacts/`。清空删 `artifacts/bin` 与 `artifacts/obj`；
  `build/installer/windows/` 下的 WiX 工程是例外，要一起清用 `git clean -xdf build/installer`（先 `-xdn` 看）。
- **依赖变更**：版本只写 `Directory.Packages.props`。用 `pwsh -File build/tools/Update-LockFiles.ps1` 更新锁文件
  （`-Check` 只验证），再生成 `THIRD-PARTY-NOTICES.md`
  （`build/tools/Export-ThirdPartyNotices.ps1`，不要手改，CI 会 diff）。
- **版本与发版**：版本只写 `Directory.Build.props` 的 `<Version>`；tag `v<版本>`、`CHANGELOG.md` 的
  `## [<版本>]` 小节必须匹配。日常改动写进 `## [未发布]`。
- **文档**：新文档必须加进 [docs/README.md](docs/README.md)（唯一索引），否则 `DocumentationTests` 变红；写或改
  文档前先读 [docs/STYLE.md](docs/STYLE.md)；移动或改名后全仓搜旧路径（`.md`、`.cs` 注释、`.yml`、`.sh`、`.ps1`）。
  markdownlint：`npx --yes markdownlint-cli2@0.23.3 "**/*.md"`。
- **换行符**由 `.gitattributes` 统一，不在 `.editorconfig` 里另设：`.sh`、`Dockerfile`、Linux 包脚本、`.plist`、
  `.service`、`.yml` 必须 LF；`*.ps1`、`*.wxs`、`*.csproj`、`*.props`、`*.slnx` 为 CRLF。
  `build/installer/windows` 下由 Windows PowerShell 5.1 读取的打包脚本还要求 UTF-8 带 BOM，编辑时保留原编码。
- **服务端配置**：`--configure-system` 的参数为 null 表示“这次不改这一项”，空串或 `auto` 才是明确表态；
  不要改成“空值即清空”。细节见 [docs/design/README.md](docs/design/README.md)。
