# 贡献指南 (Contributing)

感谢您关注并为 Remote Desktop 贡献代码！为了保证代码质量与架构一致性，请在提交 Pull Request 前阅读以下指南。

## 🛠 开发环境准备

1. **SDK 要求**: .NET 10 SDK，**不低于 10.0.112**。版本由根目录的 `global.json` 固定（`rollForward: latestFeature`，
   同一 主.次 版本（10.0）内更新的功能带也可以），测试运行器也在那里指定为 Microsoft.Testing.Platform。
2. **IDE 推荐**: Visual Studio 2022 (Preview) / JetBrains Rider / VS Code (配合 C# Dev Kit)。
3. **构建系统**: 本项目使用较新的 `.slnx` 解决方案格式，请确保您的 IDE 支持。

## 🏗 本地编译与测试

在根目录执行以下命令，确保所有代码编译通过并且没有破坏架构规则：

```bash
# 1. 按提交的锁文件还原与编译
dotnet restore RemoteDesktop.slnx --locked-mode
dotnet build RemoteDesktop.slnx --no-restore

# 2. 运行单元测试与架构校验（非常重要！）
dotnet test --solution RemoteDesktop.slnx --no-build
```

所有项目提交 `packages.lock.json`；生产项目另提交 `packages.win-x64.lock.json`、`packages.linux-x64.lock.json` 与
`packages.osx-arm64.lock.json`。CI、Release、Docker 和打包脚本均使用 locked mode。变更依赖时按对应 RID 用
`dotnet restore ... --force-evaluate` 更新锁文件，并重新生成 `THIRD-PARTY-NOTICES.md`。

> `dotnet test` 不带 `--results-directory` 时会在当前目录建一个**空的** `TestResults/`，这是
> Microsoft.Testing.Platform（`global.json` 指定的测试运行器）的默认值，不是本仓库的约定——仓库自己的脚本与 CI 都把报告写进 `artifacts/test-results/<平台>/`（见下文"构建产物布局"）。
> 根上那个空目录已被 gitignore，看到它不必处理；想自己留一份报告就加
> `--results-directory artifacts/test-results/<平台>`。

### 测试项目命名

`tests/` 下的项目一律是 `<区域>.<种类>Tests`，种类只有两种：

| 种类 | 含义 | 例子 |
|---|---|---|
| `UnitTests` | 不需要外部服务或网络，随时可跑（个别用例例外：依赖已构建好的服务端 MSI 的用例，在 `artifacts/installer/windows/` 里找不到、也没设 `RD_SERVER_INSTALLER_MSI` 指向它时跳过；要人工点"分享"的 Linux portal 用例，不设 `RD_PORTAL_TEST=1` 就跳过；测真实摄像头帧率的用例，帧率取决于设备与接线，不设 `RD_CAMERA_RATE_TEST=1` 就跳过） | `RemoteDesktop.Core.UnitTests`、`RemoteDesktop.Platform.Windows.UnitTests` |
| `IntegrationTests` | 需要真实网络、容器或其他进程 | `RemoteDesktop.Transport.IntegrationTests` |

安装包那三个类各读一个环境变量：客户端（`InstallerPackageTests`）与被控端（`HostInstallerPackageTests`）读
`RD_INSTALLER_MSI`——构建脚本把它指向刚产出的那个包，角色写在文件名里——服务端（`ServerInstallerPackageTests`）读
`RD_SERVER_INSTALLER_MSI`。两个都不设时，用例去 `artifacts/installer/windows/` 找最新的
`RemoteDesktop-<角色>-Windows-*.msi`，找不到就跳过。

**后缀必须从这个集合里取。** 名字里没有 `UnitTests` / `IntegrationTests` 的项目会让「按类型筛选」变成一件靠不住的事——
`**/*.UnitTests` 会漏掉它，而漏掉是静默的。加新测试项目时请照这个模式命名，别用 `FooTests` 或 `Foo.Test`。

平台专有的用例（只在某个 OS 上有意义）仍然放在对应平台的 `UnitTests` 项目里，用 `Assert.SkipUnless(...)`
在别的系统上跳过，而不是为每个平台单开一种后缀。

需要真实 PostgreSQL 的用例（设备注册表与监控台存储各一组）同样随时可跑：不设环境变量时跳过。本机跑法：

```bash
docker run -d --name rd-pg-test -e POSTGRES_PASSWORD=test -p 127.0.0.1:55432:5432 postgres:18@sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42
RD_TEST_POSTGRES='Host=127.0.0.1;Port=55432;Username=postgres;Password=test;Database=postgres' \
  dotnet test --project tests/RemoteDesktop.Server.Storage.IntegrationTests
docker rm -f rd-pg-test
```

设备注册表那组在 `tests/RemoteDesktop.Server.Storage.IntegrationTests`（`UnitTests` 只留内存与 SQLite 两种实现），
监控台存储的那组在 `tests/RemoteDesktop.Server.Ops.IntegrationTests`，同样用这个环境变量启用。

**`<区域>` 指的是"被测代码在哪一块"，但它不总是等于 `src/` 下的某个目录名**，这一点容易看错：

| 项目 | 实际覆盖 |
|---|---|
| `RemoteDesktop.Core.UnitTests` | Core **加 Features 与 Media**——它的 `ProjectReference` 是 `Application`、`Features.{Camera,Desktop,FileTransfer,Terminal}`、`Media`、`Server.Shared`。所以 `VideoCodecSelectorTests`、`TerminalEmulatorTests` 都在这里，而不在叫 `Features` 或 `Media` 的项目里 |
| `RemoteDesktop.Transport.IntegrationTests` | 传输层的端到端用例（要真网络，所以是 `IntegrationTests`，与上面那个是**种类**的区别不是区域的区别）。它的 `ProjectReference` 不止 Transport：`Application`、`Features.*`、`Platform.Windows` / `Platform.MacOS`、`Server.Rendezvous`、`Server.Relay` 都在里面，所以信令、中继与功能通道的联调用例放这里 |
| `RemoteDesktop.E2E.IntegrationTests` | 跨系统角色完整闭环端到端集成测试（信令集群编排、中继与直连通道、双向 SRP-6a 鉴权、固定密码无人值守、访问授权协商与会话生命周期） |
| `RemoteDesktop.WindowsUi.IntegrationTests` | 客户端图形界面与集成工作流测试（ViewModel 状态绑定、深度连接激活、最近设备持久化、固定密码安全表单校验与界面会话流转） |
| `RemoteDesktop.Server.Storage.UnitTests` | `Server.Storage`（信令服务器的设备注册表）：内存与 SQLite 两种实现 |
| `RemoteDesktop.Server.Storage.IntegrationTests` | 同一个设备注册表直连真实 PostgreSQL 的那一组（不设 `RD_TEST_POSTGRES` 时跳过） |
| `RemoteDesktop.Server.Ops.UnitTests` | 监控台核心 `Server.Ops.Core` 与 `Server.Shared` |
| `RemoteDesktop.Server.Ops.IntegrationTests` | 监控台三层（`Server.Ops`、`Server.Ops.Core`、`Server.Ops.Storage`）合起来跑 |
| `RemoteDesktop.Architecture.UnitTests` | 不是某个区域，是**规则本身**：分层依赖、`RepositoryStructureTests` 里的结构约定、以及 `DocumentationTests` 里的文档规范 |
| `RemoteDesktop.Platform.<OS>.UnitTests` | 那一个平台的实现 |

所以"某个功能在哪测"不能只靠项目名推。默认是**功能层的单测放 `Core.UnitTests`**；只有平台专有的才进对应平台的
项目，只有需要真网络/真进程/容器的才进 `IntegrationTests`。

## 🖥 本地联调

单机两个客户端互连的完整步骤在根 [`README.md`](README.md) 的"快速开始"里（含 Windows PowerShell 写法）。
各平台多出来的那几步在这里：

**Windows：** 不需要额外步骤。`README.md` 里 `ASPNETCORE_ENVIRONMENT=…` 的前缀写法是 POSIX shell 的，PowerShell 里
要先 `$env:ASPNETCORE_ENVIRONMENT = 'Development'`，数据目录用 `"$env:TEMP\rd1"` 这类 Windows 路径。

**Linux：要么把 `libmsquic` 装到系统里，要么让 `LD_LIBRARY_PATH` 指向它。** 开发时是框架依赖运行，库不在可执行文件旁，
缺它的报错是 `QUIC is not supported on this system`——看着像平台不支持，其实是缺库。`.deb` 里是随包带的，
所以装出来的包不会有这个问题。仓库里有 `native/linux/fetch-libmsquic.sh`。

**macOS：不需要 `dotnet run`。** 用服务端 `.pkg` 把服务端装到本机，再开两个客户端实例——
`open --args` 送不到参数（`args` 会丢，原因见 [`docs/ports/README.md`](docs/ports/README.md) 的"`open --args` 送不到参数"一节），
所以直接跑 bundle 里的可执行文件：

```bash
# 客户端 .app 不会自己出现：先构建一次（也可以用 make-pkg.sh，它内部会调用这个脚本）
build/installer/macos/client/make-app.sh --configuration Release
build/installer/macos/server/make-pkg.sh --configuration Release

sudo installer -pkg artifacts/installer/macos/RemoteDesktop-Server-macOS-arm64-*.pkg -target /
curl http://127.0.0.1:5080/health        # Healthy 就可以开始了

"artifacts/app/macos/Remote Desktop.app/Contents/MacOS/RemoteDesktop.Client" --DataDirectory ~/rd1
"artifacts/app/macos/Remote Desktop.app/Contents/MacOS/RemoteDesktop.Client" --DataDirectory ~/rd2
```

### 跨机器测试

先按 [`docs/ops/README.md`](docs/ops/README.md) 的“防火墙”一节放通服务器的信令、地址反射和中继三个端口。**信令默认只监听回环，客户端默认也拒绝连非回环的
`ws://`**，所以只放通端口、改服务器地址是连不上的——两端都要显式放开（只限隔离的明文实验网；其余情况改走 `wss://`）：

```bash
# 服务器：监听所有网卡、允许明文信令，并把中继地址配成客户端可达的 IP:端口（不能是域名）。
# Development 配置提供了与 Relay 相同的开发密钥；Windows 的写法同上，先设 $env:ASPNETCORE_ENVIRONMENT
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server/RemoteDesktop.Server.Rendezvous -- \
    --Urls http://0.0.0.0:5080 --Rendezvous:AllowInsecureSignaling true \
    --Rendezvous:RelayEndpoint <服务器IP>:5082

# 客户端：换服务器地址并允许明文；--Transport:ForceRelay true 可强制走中继
dotnet run --project src/Hosts/RemoteDesktop.Client -- --DataDirectory /tmp/rd1 \
    --Transport:RendezvousUri ws://<服务器>:5080/ws --Transport:AllowInsecureSignaling true
```

不放开时的报错分别是 `Urls exposes signaling as plain HTTP …`（服务器，启动即失败）和
`Transport:RendezvousUri uses plain ws:// to non-loopback host …`（客户端）。Relay 照 `README.md` 的方式启动即可：
两个服务在 Development 配置里用的是同一把开发密钥。

**这只是联调。** 明文信令里的设备代码和**中继票据**都可被在途读取；生产环境必须换掉 `RelaySharedKey` 并让信令走
`wss://`，做法见 [`docs/ops/README.md`](docs/ops/README.md) 的"生产环境：信令必须走 TLS"，边界见
[`SECURITY.md`](SECURITY.md)。

## 📦 构建产物布局

所有构建产物都在仓库根的 `artifacts/`（已 gitignore）。仓库自己的脚本写的四类按 `<类别>/<平台>/` 组织：

| 路径 | 是什么 |
|---|---|
| `artifacts/installer/<平台>/` | **交付物**：安装包。三个平台各三个：Windows `.msi`、macOS `.pkg`、Linux `.deb`，对应客户端、被控端、服务端三个角色。角色写在**文件名**里，不另开子目录；名字的形状与下面发布资产的一致，只是 `<tag>` 换成 `<版本>`。`.wixpdb` 之类的构建中间物不在这里，它跟着工程留在 `obj/` |
| `artifacts/app/<平台>/` | 打包前的应用本体。macOS 是 `Remote Desktop.app`，另有 `host/`（`Remote Desktop Host.app`）与 `server/`（服务端发布内容）两个子目录；Windows 的下一层是角色：`client/` 同时装客户端、被控端服务与会话 Agent，所以这个目录里有三个 `.exe`；`host/` 只有服务与 Agent；`server/` 下再按程序分成 `Rendezvous/`、`Relay/`、`Ops/` 与 `Gateway/`（Ops 的 HTTPS 网关） |
| `artifacts/lib/<平台>/` | 原生辅助库与随包分发的本地库（macOS 的 `libRemoteDesktopMacNative.dylib`、`libmsquic.dylib`；Linux 的 `libmsquic.so.<版本>` 加软链 `libmsquic.so.2`，由 `native/linux/fetch-libmsquic.sh` 取来） |
| `artifacts/test-results/<平台>/` | 测试报告（`dotnet test --report-trx` 产出的 `.trx`），按跑测试的机器分平台 |

发布时 `release.yml` 会把上面这些包**改名**成用户看到的那份资产，形状是
`<产品>-<角色>-<平台>-<架构>-<tag>.<扩展名>`：

| 平台 | 客户端 | 被控端 | 服务端 |
|---|---|---|---|
| Windows | `RemoteDesktop-Client-Windows-x64-<tag>.msi` | `RemoteDesktop-Host-Windows-x64-<tag>.msi` | `RemoteDesktop-Server-Windows-x64-<tag>.msi` |
| macOS | `RemoteDesktop-Client-macOS-arm64-<tag>.pkg` | `RemoteDesktop-Host-macOS-arm64-<tag>.pkg` | `RemoteDesktop-Server-macOS-arm64-<tag>.pkg` |
| Linux | `RemoteDesktop-Client-Linux-amd64-<tag>.deb` | `RemoteDesktop-Host-Linux-amd64-<tag>.deb` | `RemoteDesktop-Server-Linux-amd64-<tag>.deb` |

**九个资产一个形状**。角色词一律写上——三个平台都出三个包，下载列表里每一行都要能直接看出它是哪一端。
三平台的打包脚本产出的名字也是同一个形状（Windows 的 `OutputName`、macOS 的 `PKG_NAME`、Linux 的 `$package`），
发布时只把 `<版本>` 换成 `<tag>`，所以 `artifacts/installer/` 里的文件名可以直接照发布资产名去找。

只有一处不同，且有成因：**架构词用各平台自己的说法**——Windows `x64`、macOS `arm64`、Linux `amd64`。Linux 那个是
dpkg 的词，与包内 `Architecture` 字段（`build/installer/linux/*/control`）保持一致；文件名与字段不同词只会更绕。

`dotnet build` / `dotnet publish` 的产物也在这里，用 SDK 自己的布局——`Directory.Build.props` 设了
`UseArtifactsOutput` + `ArtifactsPath`：

| 路径 | 是什么 |
|---|---|
| `artifacts/bin/<项目>/<pivot>/` | 编译输出（`pivot` 形如 `debug`、`release_linux-x64`） |
| `artifacts/obj/<项目>/<pivot>/` | 中间产物 |
| `artifacts/publish/<项目>/<pivot>/` | `dotnet publish` 的输出（**不在** `bin/` 下面，这是 SDK 的约定） |
| `artifacts/package/<pivot>/` | `dotnet pack` 的输出（**单数**，且**没有**项目层——两点都是 SDK 定的，不是笔误） |

**这两组不是同一条规则。** 上面那组是我们自己的脚本写的，`<类别>/<平台>/` 由我们定；下面那组由 SDK 写，
第二层是 `<项目>` 与 `<pivot>`，**不按平台分**——加一个平台不会在这里多一层。所以"新增平台"是往
`installer/`、`app/`、`lib/`、`test-results/` 各加一层，SDK 的那四个不动。Microsoft.Testing.Platform 默认的
`TestResults/` 在当前目录（通常是仓库根），只有不带 `--results-directory` 手跑时才会出现，所以
`.gitignore` 里留着这条规则。

所以项目目录里**没有** `bin/` 和 `obj/` 了：`RemoteDesktop.slnx` 里的每个项目（目前 38 个：`src/` 下 26 个，`tests/` 下 12 个）
不再各长出两个目录。要清空构建产物，删 `artifacts/bin` 与 `artifacts/obj` 即可，不必逐个项目找。

**唯一的例外**是 `build/installer/windows/` 下的四个安装器工程（`client/` 与 `server/` 各一个 WiX 工程，以及各自目录下 net48 的 `CustomActions`）：它们
带着自己那份（故意留空的）`Directory.Build.props`，不导入根目录的这份，产物仍在工程目录内（`bin/`、`obj/`、
`bin/x64/`）。所以"删 `artifacts/bin` 与 `artifacts/obj`"清不掉它们——要连它们一起清，用
`git clean -xdf build/installer`（`-x` 会连被忽略的文件一起删，用之前先看一眼 `git clean -xdn`）。

打包脚本的位置另有一套分层（`<平台>/<角色>`），见 [`build/README.md`](build/README.md)。

**只有一个产物根目录。** 新增平台照 `<类别>/<平台>/` 加一层即可；不要再另起一个 `artifacts/` 目录（比如写在 `build/` 里面）—— 那样"产物在哪"就会有两个答案，每加一个平台都要重新选边。

`<平台>` 取 `windows` / `macos` / `linux`——是**平台**，不是发行版或包格式：`.deb` 因此落在 `installer/linux/` 而不是 `installer/debian/`。

## 📐 架构与依赖规则

本项目采用严格的**洋葱架构/整洁架构**，依赖方向只能**由外向内**。此规则由 `RemoteDesktop.Architecture.UnitTests` 强制校验。

* `Domain` (领域层) 不能引用任何其他层。
* `Abstractions` (抽象端口) 只能引用 `Domain`。
* `Signaling`（信令契约）是独立的一层：不引用任何其他内部项目；`Transport` 引用它，反过来不行。
* `Protocol`、`Transport`、`Application` 依次向外（`Protocol` 不得引用 `Signaling`、`Transport`、`Application`，
  `Transport` 不得引用 `Application`），且都不得引用 `Features`、`Media`、`Platform`、`Server` 或三个宿主
  （`Client`、`HostService`、`SessionAgent`）。
* `Platform.*` (平台实现) 和 `Media` (具体技术) 不得引用 `Protocol`、`Signaling`、`Transport`、`Application`、
  `Features`、`Server` 与宿主（二者彼此也不引用），也就是只面对 `Abstractions` 与 `Domain`。**例外**：
  `Platform.Linux` 与 `Platform.MacOS` 引用 `Platform.Posix`（两者共用的 POSIX 代码）。测试只禁止上面列出的方向，
  不禁止 `Platform.*` 之间互相引用，所以这类引用靠评审把关。
* `Features.*` (独立功能模块) 彼此之间禁止互相引用，它们通过 `Application` 层的统一接口交互。
* `Features.*` 也不得引用 `Platform` 或三个宿主：平台由宿主在启动时接进来，功能模块只面对 `Application`（`Desktop`、`Camera` 另外引用 `Media`）。
* `Server.*` 不得引用 `Abstractions`、`Protocol`、`Transport`、`Application`、`Features`、`Media`、`Platform` 或宿主。
* 运维监控台分三层，只能向内：`Server.Ops.Core` 不得引用 `Server.Ops.Storage`、`Server.Ops`、`Microsoft.AspNetCore`
  和 `Server.Storage`；`Server.Ops.Storage` 不得引用 `Server.Ops` 和 `Server.Storage`；`Server.Ops` 不得引用
  `Server.Storage`（监控台不读信令服务器的库）。

如果你引入了非法的依赖关系，`dotnet test` 将会失败。

（上面几条概括的是 `DependencyRuleTests` 里的禁止列表，不是逐字翻译。改成文档容易、改漏测试也容易，所以**以测试为准**：
两者不一致时先怀疑文档。）

## 🗂 仓库结构规则

`tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs` 把下面几条变成了断言，违反时构建会红：

| 规则 | 出处 |
|---|---|
| `src/` 与 `tests/` 下的项目，目录名 == `.csproj` 名；`tests/` 下的项目名一律 `<区域>.<种类>Tests` | “测试项目命名”一节 |
| `.cs` 文件名要么是它声明的类型，要么是它分组的概念（此时必须声明 **两个以上** 类型） | 见下 |
| `.sh` 用 kebab-case；`.ps1` 用 Verb-Noun，动词取自 PowerShell 的批准动词表 | `build/README.md` |
| `build/installer/<平台>/<角色>/`、`build/service/<平台>/`，平台取 `windows`/`macos`/`linux` | `build/README.md` |
| Linux 服务端 `.deb` 的 `postinst` 要让监控台钉住本机节点自己的证书（含 `--ops-node-certificate-path` 与 `tls/signaling.crt`），对应 `The_linux_package_lets_the_console_pin_its_own_certificate` | `build/installer/linux/server/postinst` |

表里只是最常碰到的五条；同一个文件里还有安装 / 发布脚本的安全断言（服务端包脚本的密文只走环境变量、
macOS `apply-answers.sh` 拒符号链接并校验地址、`release.yml` 的 job 级 env 不得含 Secret、卸载脚本先校验目录、
WiX 转义等），改这些脚本前先看一眼那个测试文件。

第二条值得展开：一个文件承载"一个概念的接口族与它的 DTO"是允许的（`IAudio.cs` 里没有 `IAudio` 类型，
但它是音频端口的整组契约），所以判据不是"文件名必须是类型名"，而是**"名字不是类型时，它必须是个分组"**。
真正会被拦下的是只声明一个类型、名字却是另一个的单件文件。

平台名的拼写也分三处，各有各的道理，别互相搬：**文件系统路径**用全小写（`build/installer/macos/`、
`artifacts/installer/macos/`）；**C# 项目名与命名空间**用 PascalCase（`RemoteDesktop.Platform.MacOS`，
因为 `MacOS` 在这里是 .NET 标识符的一部分）；**RID** 按 SDK 写（`osx-arm64`）；**散文**里写 Apple 的
`macOS`。注意 `.app` bundle 里的 `Contents/MacOS/` 是 Apple 定的目录名，不是这里的拼写问题。

## 📄 文档

文档的规范在 [`docs/STYLE.md`](docs/STYLE.md)：放哪、叫什么、怎么写、什么时候更新。两条最常被违反的：

1. **新文档要进索引**（[`docs/README.md`](docs/README.md)）。索引只有一份，漏加会让 `dotnet test` 变红。
2. **移动或改名文档时全仓搜旧路径**——引用散在 `.md`、`.cs` 注释、`.yml`、`.sh`、`.ps1` 里，不只 Markdown。

哪些规则由测试强制、哪些交给 markdownlint，以及测试文件在哪，见 STYLE 的“靠测试，不靠自觉”一节（不要在这里再列一份）。
**格式**（空行、缩进、代码块标注）由 CI 的 `docs` 作业跑。所以只改了文档也要跑一次测试：

```bash
dotnet test --project tests/RemoteDesktop.Architecture.UnitTests
```

## 📝 提交规范

1. 提交前确保通过 `dotnet test` 所有的单元测试。
2. 新增的核心网络或编解码功能，需补充对应的测试用例。
3. 请在 PR 描述中详细说明改动背景、受影响的模块，以及如何测试该改动。
4. 有值得记录的变更，写进 `CHANGELOG.md` 的 `## [未发布]` 一节。**发版时**，`release.yml` 的 `check-version` 会要求
   tag（`v<版本>`）与 `Directory.Build.props` 的 `<Version>` 一致，且 `CHANGELOG.md` 里有 `## [<版本>]` 这一小节，
   缺一个就不发布——发版前把 `[未发布]` 的内容改成该版本的小节。
