# Windows 安装包评审（2026-10-26）

> **历史快照 · 已冻结** — 本文件是 2026-10-26 对 Windows 三个安装包的评审记录，**不再更新**。
> 评审里写的建议**不是现行约定**；此后要知道“现在怎么做”，以
> [`build/README.md`](../../build/README.md)、[`docs/ops/README.md`](../ops/README.md) 与
> [`build/signing/README.md`](../../build/signing/README.md) 为准。归档说明见 [`README.md`](README.md)。
>
> 评审对象：`master` @ `70df90c`（2026-10-26）。仓库里的 `artifacts/installer/windows/` 仍是 0.3.0 的两个 MSI，
> 用于核对 MSI 表结构与升级序列；源码、WXS、自定义动作与文档以当前 HEAD 为准。
> 评审环境：Windows 11 Pro for Workstations 10.0.26300（x64）；本机没有可用的代码签名证书。
> 结论：**包的骨架、角色划分、服务定义与包内容检查是正确的；但服务器包升级会丢掉三份 live settings，
> Ops 控制台装上了却没有接上两个服务器，安装日志与进程列表里能看到部署秘密，Windows 服务端也无法把部署用的
> DNS/NAT 名写进自签证书。** 详见 §3–§5。

---

## 0. 结论摘要

| 维度 | 结论 | 说明 |
|---|---|---|
| 三个角色的包结构 | ✅ 正确 | client（客户端 + 服务 + Agent）、host（只有服务 + Agent）、server（Rendezvous/Relay/Ops）分工清楚；独立 UpgradeCode，client/host 互相拒绝并存 |
| 服务与崩溃恢复 | ✅ 正确 | 三个服务自动启动、LocalSystem、`ServiceControl` 停/启/删、三次 5 秒重启、失败计数一天后清零 |
| 防火墙 | ✅ 正确 | client/host 按程序开 UDP；server 按程序 + 端口开 TCP 5080 / UDP 5081 / UDP 5082；Ops 只监听回环、不开规则 |
| UIAccess 与签名 | ✅ 基本正确 | 发布时带 `UiAccessAgent=true` 并检查清单；先签产品 exe/dll 再签 MSI；自签名证书装进受信任根，CA 证书不装；server 证书单独可选 |
| MSI 包内容检查 | ✅ 很好 | 三个测试打开 MSI 数据库核对载荷、服务、防火墙、快捷方式、对话框、许可证、证书组件与自定义动作架构 |
| 版本 / 许可证 / 第三方声明 | ✅ 正确 | 单一版本来源、四段 MSI 版本、LICENSE/THIRD-PARTY-NOTICES 随包、RTF 由 LICENSE 生成 |
| 服务器包升级 | ❌ **P0** | `RemoveExistingProducts` 排在 `InstallFiles` 之前；`NeverOverwrite` 保不住 live settings；重启后 Relay 因空密钥起不来，Rendezvous 退回内存数据库 |
| client/host 升级 | ❌ **P1** | 升级会把服务器地址恢复成**构建默认值**；安装期 `RENDEZVOUSURI` 与界面里改过的地址都会丢，host 包没有界面可以再设置 |
| Ops 首装 | ❌ **P1** | 安装包不传 `--metrics-enabled`、不 seed 本地节点、不 pin 证书；新装的监控台没有节点，信令的 `/metrics` 是 404 |
| 秘密处理 | ❌ **P1** | server 的 `RELAYSHAREDKEY` / `POSTGRESCONNECTIONSTRING` / `TLSCERTIFICATEPASSWORD` 进 MSI 日志；host 的固定密码虽不在日志里，但在安装期的进程命令行里 |
| TLS 生成 | ❌ **P1** | Windows 安装包没有传 `--tls-hostname`；生成的自签证书只覆盖本机名与本地 IPv4，客户端用 DNS/NAT 名访问时即使信任证书也会报名称不匹配 |
| 卸载清理 | ⚠️ **P2** | server 卸载不删 `%ProgramData%\Remote Desktop Server` 下的库、`tls\signaling.cer`，也不处理证书存储里的私钥 |
| 文档与行为 | ⚠️ **P2** | “升级不会覆盖 appsettings”“服务器地址以安装包里的为准 / 手改会保留”等句子互相矛盾，且与 MSI 的实际行为不符 |

**P0 一项、P1 四项、P2 七项、P3 若干。** 最容易被“包能生成、测试全绿”掩盖的是升级路径：包内容测试只打开 MSI
数据库，不会安装旧版再装新版，也不会启动服务。

---

## 1. 评审范围与方法

**范围**

- [`build/installer/windows/client/`](../../build/installer/windows/client/)：`Build-ClientInstaller.ps1`、`Package.wxs`、
  `Components.wxs`、`RemoteDesktop.Installer.wixproj`
- [`build/installer/windows/host/Build-HostInstaller.ps1`](../../build/installer/windows/host/Build-HostInstaller.ps1)
- [`build/installer/windows/server/`](../../build/installer/windows/server/)：`Build-ServerInstaller.ps1`、`Package.wxs`、
  `Components.wxs`、`Set-ServerConfiguration.ps1`、`how-to-configure.txt`、`CustomActions/`
- [`build/service/windows/`](../../build/service/windows/)：手动安装 / 卸载脚本
- 相关的产品代码：[`ServerSetup.cs`](../../src/Server/RemoteDesktop.Server.Shared/ServerSetup.cs)、
  [`SelfSignedCertificate.cs`](../../src/Server/RemoteDesktop.Server.Shared/SelfSignedCertificate.cs)、
  [`Ops/SetupCommands.cs`](../../src/Server/RemoteDesktop.Server.Ops/SetupCommands.cs)、
  [`OpsServer.cs`](../../src/Server/RemoteDesktop.Server.Ops/OpsServer.cs)、
  [`ServerSetup` 的调用方](../../src/Server/RemoteDesktop.Server.Rendezvous/SetupCommands.cs)、
  [`SystemSetup.cs`](../../src/Platform/RemoteDesktop.Platform.Windows/Setup/SystemSetup.cs)
- 包内容测试：[`InstallerPackageTests.cs`](../../tests/RemoteDesktop.Platform.Windows.UnitTests/InstallerPackageTests.cs)、
  [`HostInstallerPackageTests.cs`](../../tests/RemoteDesktop.Platform.Windows.UnitTests/HostInstallerPackageTests.cs)、
  [`ServerInstallerPackageTests.cs`](../../tests/RemoteDesktop.Platform.Windows.UnitTests/ServerInstallerPackageTests.cs)
- CI / 发版：[`ci.yml`](../../.github/workflows/ci.yml)、[`release.yml`](../../.github/workflows/release.yml)、
  [`verify-packages`](../../.github/actions/verify-packages/action.yml)

**方法**

1. 逐行阅读上述脚本、WXS、自定义动作与文档，并和 Linux / macOS 的对应实现对照。
2. 用仓库里已有的 0.3.0 client / host MSI 做静态核对（Windows Installer COM，不安装、不提权）：
   `InstallExecuteSequence`、`Component`、`ControlEvent`、`Dialog`/`Control`。
3. 用 PowerShell 验证路径语义：`Path.IsPathRooted('%ProgramData%\...')`、`Path.Combine`、`ExpandEnvironmentVariables`。
4. 用 `grep` 对比三个平台的服务端安装后步骤：`--metrics-enabled`、`--seed-local-nodes`、`--sync-local-nodes`、
   `--tls-hostname`、`--ops-node-certificate-path`、`--set-fixed-password-file`。

**没有做的**

- **没有安装或升级任何 MSI**（会改动本机的服务、防火墙规则、证书存储与 `%ProgramData%`）。
  §3 的升级结论来自 MSI 的 `RemoveExistingProducts` 序列、仓库 1.0.0 CHANGELOG 里记录的 0.3.0 升级故障，
  以及 Microsoft 对 `RemoveExistingProducts` 的说明，不是本机实跑结果。发版前必须做一次真实的“旧版 → 新版”升级测试。
- 没有构建 1.0.0 的 MSI（现有产物是 0.3.0）；没有用真实证书走签名 / SmartScreen / UIAccess 实机。
- 没有安装 server MSI，也没有启动三个服务；Ops 的节点与 `/metrics` 结论来自源码与 Linux/macOS 的对照。

---

## 2. 确认无问题的部分

1. **角色划分与包名**：`RemoteDesktop-{Client,Host,Server}-Windows-x64-<版本>.msi` 形状一致；client 同时装客户端、
   `RemoteDesktopHost` 服务与会话 Agent；host 只装后两者，没有客户端可执行文件与开始菜单项；
   server 装 Rendezvous / Relay / Ops 三个自包含程序。client 与 host 各有自己的 UpgradeCode，
   并在 `Upgrade` 表里检测对方，第二个包会被 `LaunchCondition` 拒绝；server 与它们互不影响。
2. **服务定义**：三个服务都是 `Start="auto"`、`Account="LocalSystem"`、`ErrorControl="normal"`；
   `ServiceControl` 在安装时启动、升级/卸载时停止并删除（`Wait="yes"`）；`util:ServiceConfig` 配了三次重启、
   5 秒间隔、一天后清零。对一个没人看着的远程访问产品，这是对的。
3. **防火墙**：client/host 的 UDP 规则按 `Program="[#HostServiceExeFile]"` 开，而不是按端口开；
   server 的三条规则同时限定程序与端口（TCP 5080、UDP 5081、UDP 5082），Ops 只监听 `127.0.0.1:5090` 且没有规则；
   `NOFIREWALL=1` 可以把三条规则整组跳过；加规则失败用 `IgnoreFailure`，不会让安装失败。
4. **UIAccess 的构建链**：`-p:UiAccessAgent=true` 传给每次 publish（包括作为项目引用的那次），发布后直接读 exe
   检查 `uiAccess="true"`；先签 `RemoteDesktop*.exe` / `*.dll`，再签 MSI；自签名证书的公开部分作为 `Binary`
   装进 `LocalMachine\root`，且只在“签发者等于主体”时这样做；CA 证书不塞根存储。这个判断是正确的。
5. **安装期设置**：`RENDEZVOUSURI` / `RENDEZVOUSURL` / `RELAYPUBLICENDPOINT` / `RELAYSHAREDKEY` /
   `DATABASEPROVIDER` / `POSTGRESCONNECTIONSTRING` / `TLSCERTIFICATEPATH` 都有值、`Secure="yes"`，deferred 动作
   能收到；`REMOVEDATA` 默认保留设备身份；host 的 `FIXEDPASSWORD` 在 `MsiHiddenProperties` 里且动作带
   `HideTarget="yes"`，安装日志里看不到密码。
6. **`RestoreDefaultSettings` 的安全网**：client/host 随包带 `appsettings.default.json`，并在 `InstallFiles` 之后
   只在 `appsettings.json` 不存在时复制。它确实能避免“升级后服务完全没有设置、回退到 localhost”的最坏情况。
   （它保不住操作员原来的地址，见 W2。）
7. **包内容测试**：三个测试打开 MSI 数据库，检查文件数下限与关键文件、服务名/启动类型/失败动作、防火墙规则、
   快捷方式、许可证 RTF、升级条件、证书组件与 Binary 的对应关系、`CloseApplication`/`taskkill` 的用法、
   `ConfigureSystem` 的条件、对话框与控件不越界/不重叠、没有 `SetProperty` 事件、
   SfxCA 的 x64 头与 `CustomAction.config`。这些正是 WiX 5 会静默丢内容的地方，测试选得很准。
8. **版本与交付物**：版本只来自 `Directory.Build.props`，MSI 追加第四段 `.0`，数值范围也检查；
   `verify-packages` 在三个平台各自打开包核对版本；LICENSE 与 THIRD-PARTY-NOTICES 随包；许可页 RTF 从 LICENSE 生成。
9. **发布流程**：CI 在每次 PR 上构建三个 MSI 并跑包内容测试；release 重新构建、签名、用同一份 `verify-packages`
   再检查，并强制九个资产齐全。这是正确的分工。
10. **文档覆盖**：服务器地址、TLS、数据库、签名、UIAccess、升级/卸载、排错都有专门段落；
    服务端还随包带 `how-to-configure.txt` 与 `Set-ServerConfiguration.ps1`，比只给一个 MSI 好得多。

---

## 3. 阻断级（P0）

### W1 服务器包升级会丢掉三份 live settings，Relay 直接起不来

**位置**

- [`server/Package.wxs:34-36`](../../build/installer/windows/server/Package.wxs#L34-L36)：`MajorUpgrade Schedule="afterInstallInitialize"`
- [`server/Components.wxs:47-49`](../../build/installer/windows/server/Components.wxs#L47-L49)、
  [`75-77`](../../build/installer/windows/server/Components.wxs#L75-L77)、
  [`107-109`](../../build/installer/windows/server/Components.wxs#L107-L109)：三份 `appsettings.json` 的组件
- [`server/Components.wxs:233-263`](../../build/installer/windows/server/Components.wxs#L233-L263)：`ConfigureSystem` 只在
  `NOT WIX_UPGRADE_DETECTED AND NOT Installed` 时运行
- [`CHANGELOG.md:236-242`](../../CHANGELOG.md)：1.0.0 自己记录的 0.3.0 → 1.0.0 升级故障

**发生了什么**

MSI 的 `RemoveExistingProducts` 被排在 **1501**（`InstallInitialize` 之后、`InstallFiles` 的 4000 之前）。
早移除的语义是：先把旧产品完整卸载，再安装新产品。旧产品卸载时，`Settings` 组件的引用计数归零，
`appsettings.json` 被删除；`NeverOverwrite` 只在“安装时目标已存在”这一条路径上起作用，**不阻止卸载**。
随后新产品安装的是随包的**模板**：

- `Rendezvous\appsettings.json`：`RelayEndpoint=""`、`RelaySharedKey=""`，没有 `Database` 段
- `Relay\appsettings.json`：`SharedKey=""`
- `Ops\appsettings.json`：`Path="%ProgramData%\Remote Desktop Server\ops.db"`

而 `ConfigureSystem` / `ConfigureSystemWithTls` 的 fresh-install 条件在升级时为假，所以没有任何一步把
运维原来的地址、密钥、数据库与 TLS 写回去。**升级时显式传 `RENDEZVOUSURL` / `RELAYPUBLICENDPOINT` /
`RELAYSHAREDKEY` / `DATABASEPROVIDER` / TLS 属性同样不会生效**——这些属性只在首装的那次 `--configure-system`
里被读取；要改只能装完后用 `Set-ServerConfiguration.ps1`。用 0.3.0 client MSI（同一套 WiX `MajorUpgrade` 生成，
server 源码里的 `Schedule` 相同）核对到的序列是：

```text
InstallInitialize        1500
RemoveExistingProducts   1501
InstallFiles             4000
StartServices            5900
```

如果新产品的同名组件被跳过（0.3.0 升级记录里的另一种表现），三份文件根本不存在：Rendezvous 用 Kestrel
默认的 `http://localhost:5000`，Relay 的 `SharedKey` 为空，Ops 的数据库落到程序目录；后果同样是服务不可用。

**影响**

- **Relay 服务起不来**：`Relay:SharedKey` 为空，[`Relay/Program.cs:25-40`](../../src/Server/RemoteDesktop.Server.Relay/Program.cs#L25-L40)
  在构造 `UdpRelay` 时做 base64 解码并检查至少 32 字节，直接抛 `InvalidOperationException`。`ServiceConfig`
  会重启三次，然后服务保持停止。
- **Rendezvous 退回内存数据库**：模板没有 `Rendezvous:Database` 段，`DatabaseSettings.Resolve` 看到 provider 与
  旧键都为空，返回 `None`（[`DatabaseSettings.cs:45-63`](../../src/Server/RemoteDesktop.Server.Storage/DatabaseSettings.cs#L45-L63)）。
  所有设备码在下次重启时重发，无人值守的机器失联。模板的 `RelayEndpoint` 也是空的，客户端无法使用中继
  （直连仍可能成功）。
- **Ops 的自定义配置丢失**：如果模板成为 live 文件，PostgreSQL 连接串、`NodeToken`、ingest key、TLS pin 都没了；
  SQLite 库仍在 `%ProgramData%`，但配置已经指向别处。`--create-admin` 还会碰到 W10 的路径问题。
- **TLS 静默降级或起不来**：模板的 `Urls` 是 `http://0.0.0.0:5080`，升级后若没有再配置，TLS 就没了；
  如果模板是 `https://` 而没有证书，Kestrel 起不来。

**为什么这不是“理论上可能”**

仓库的 1.0.0 CHANGELOG 已经把同一件事写在 client 包上：“major upgrade 移除了旧包的设置文件，而新包的
同 GUID 组件被跳过，服务于是回退到 `ws://127.0.0.1:5080/ws`”。server 包使用同一种 `MajorUpgrade` 调度、
同一种 `NeverOverwrite` 组件，却**没有**对应的 `appsettings.default.json` + `RestoreDefaultSettings`。
Microsoft 的 `RemoveExistingProducts` 文档与 Aaron Stebner 的 “How to retain user-customized files during a
Windows Installer major upgrade” 都说明了早移除会删除用户修改过的文件，推荐把移除排到 `InstallFiles` 之后。
本次评审没有安装 MSI；发版前必须在干净机器/VM 上实际走一遍“装旧版 → 配置 → 装新版 → 检查三个服务与设备码”。

**建议**

1. 把 live settings 移出 MSI 管理的安装目录，放到 `%ProgramData%\Remote Desktop Server\`（Linux 的 `/etc` 模型），
   安装目录里只放模板。这是最稳的修法：卸载、升级、修复都不会碰它。
2. 若暂时保留在安装目录：在 `RemoveExistingProducts` 之前用一个 immediate/deferred 动作把三份 settings 备份到
   `%ProgramData%`（或 `%TEMP%` 的受保护目录），`InstallFiles` 之后恢复；恢复时不要覆盖安装期显式给出的新值。
3. 或者把 `RemoveExistingProducts` 排到 `InstallFiles` 之后，但必须同时处理 `ServiceControl Remove="uninstall"`
   会把新装的服务删掉的问题（旧产品的卸载会停/删同名服务），以及 `StartServices` 的顺序。
4. 给 server 包补上 `appsettings.default.json` + 只在缺失时复制的兜底动作（至少让服务能起来），
   并把自动生成的数据库路径写进 `server-setup.log`；relay key 只记录“已生成/在哪”，不要打印密钥本身。
5. CI 增加升级冒烟测试（见 W15）。

---

## 4. 高（P1）

### W2 client/host 升级会把服务器地址恢复成构建默认值

**位置**

- [`client/Package.wxs:34-37`](../../build/installer/windows/client/Package.wxs#L34-L37)：同样是 `afterInstallInitialize`
- [`client/Components.wxs:72-76`](../../build/installer/windows/client/Components.wxs#L72-L76)：`Settings` 组件
- [`client/Components.wxs:153-170`](../../build/installer/windows/client/Components.wxs#L153-L170)、
  [`217-238`](../../build/installer/windows/client/Components.wxs#L217-L238)：`RestoreDefaultSettings`
- [`Build-ClientInstaller.ps1:597-600`](../../build/installer/windows/client/Build-ClientInstaller.ps1#L597-L600)：
  `appsettings.default.json` 是**构建时**的副本

**发生了什么**

早移除删除 `appsettings.json` 之后，新包要么装上自己的 `appsettings.json`（构建时地址），要么被 MSI 跳过组件、
由 `RestoreDefaultSettings` 复制 `appsettings.default.json`（也是构建时地址）。两条路都得到**包内默认地址**，
不是运维安装时给的 `RENDEZVOUSURI`，也不是界面里改过的地址。安装期属性只存在于那一次安装的 MSI 会话里，
没有写进注册表或其他持久位置；**升级时显式传 `RENDEZVOUSURI` 也不会生效**，因为 `ConfigureSystem` 的
条件里还有 `NOT Installed` 与 `NOT WIX_UPGRADE_DETECTED`。要改只能装完后在客户端界面（client 包）或命令行
（host 包）重新配置。

**影响**

- **host 包最严重**：它没有客户端界面，地址丢了以后只能在命令行重新执行 `--configure-system --rendezvous`，
  或者手工改 `appsettings.json`。对一台无人值守的机器，这意味着升级后就不再上线。
- **client 包**：客户端自己的用户级设置里可能还留着地址，出站连接可能仍然可用；但 `RemoteDesktopHost` 服务只读
  安装目录的 `appsettings.json`，被控端会注册到包内默认地址（常见是 `ws://127.0.0.1:5080/ws`）。
- **发布流程的默认值**：`release.yml` 的 `DEFAULT_RENDEZVOUS_URI` 是可选的；没有配置时包内就是 localhost，
  而发布说明又让用户“装完后在客户端里改地址”。这类部署只要升级一次，地址就回到 localhost。

**文档与行为的冲突**

- [`docs/ops/README.md:1310`](../ops/README.md)：`appsettings.json` 在升级时不会被覆盖，手改的地址会保留。
- [`docs/ops/README.md:899`](../ops/README.md)：升级保留服务器地址，但“服务器地址以安装包里的为准”。
- [`client/Components.wxs:72-76`](../../build/installer/windows/client/Components.wxs#L72-L76)：注释说升级不会把
  构建默认值放回去。

实际行为是**包内默认值胜出**；三处说法互相矛盾。

**建议**

同 W1：把 live settings 移出安装目录；或者备份/恢复 `appsettings.json`，并在安装期显式给出 `RENDEZVOUSURI` 时
优先用它；至少把安装期地址持久化（例如 HKLM 的 `Software\RemoteDesktop` 下），恢复时按“安装期值 > 旧文件 > 构建默认”
的优先级写回。补一条“装 0.3.0 → 改地址 → 装 1.0.0 → 地址仍是改过的值”的测试。

---

### W3 Ops 控制台装上了，但没有接上两个服务器

**位置**

- [`server/Components.wxs:186-189`](../../build/installer/windows/server/Components.wxs#L186-L189)、
  [`228-231`](../../build/installer/windows/server/Components.wxs#L228-L231)：两条 `--configure-system` 命令行
- [`RendezvousServer.cs:106-110`](../../src/Server/RemoteDesktop.Server.Rendezvous/RendezvousServer.cs#L106-L110)：
  `/metrics` 只在 `MetricsEnabled` 时挂载
- [`MetricScraper.cs:60-80`](../../src/Server/RemoteDesktop.Server.Ops/MetricScraper.cs#L60-L80)：控制台按节点抓 `/metrics`
- [`Ops/SetupCommands.cs:131-180`](../../src/Server/RemoteDesktop.Server.Ops/SetupCommands.cs#L131-L180)：
  `--seed-local-nodes` 的实现
- Linux / macOS 已经做了：[`postinst:215-223`](../../build/installer/linux/server/postinst#L215-L223)、
  [`postinst:316-337`](../../build/installer/linux/server/postinst#L316-L337)、
  [`macOS postinstall:441`](../../build/installer/macos/server/scripts/postinstall#L441)、
  [`463`](../../build/installer/macos/server/scripts/postinstall#L463)

**发生了什么**

server MSI 把 Ops 作为第三个服务装进去，但安装后的配置步骤只写了 Rendezvous 与 Relay 的 settings：

- 没有 `--metrics-enabled`，所以 `Rendezvous:MetricsEnabled` 是默认的 `false`，`/metrics` 路径**根本没有挂载**，
  访问返回 404。控制台的 `MetricScraper` 对 rendezvous 节点每次都记一次失败。
- 没有 `--seed-local-nodes`，所以 `ops_node` 表是空的；节点页、设备页没有数据，直到运维手工在“节点管理”里
  添加两个地址。Linux 的 postinst 与 macOS 的 postinstall 都会登记本机的 rendezvous/relay。
- 没有 `--ops-node-certificate-path`：信令用 `generate` 生成的自签证书在 `LocalMachine\My`，没有进系统信任根；
  Ops 调节点时走系统信任库（`NodeCertificatePin.Handler` 在路径为空时返回默认 handler），即使手动加了
  `https://<hostname>:5080` 节点也会因证书链不受信任而失败。macOS 用 pin，Linux 用 `update-ca-certificates`。
- 没有 `--sync-local-nodes`：macOS 的 postinstall 在升级时更新两条本地节点的地址；Windows 连 seed 都没有。
- Windows 的 `Ops\appsettings.json` 里 `Ops:Listen` 是 `127.0.0.1:5090`，这部分是对的；缺的是“把控制台和
  它同一台机器上的两个服务器连起来”这一步。

**影响**

新装的 Ops 控制台看起来“装了、能打开、能建管理员”，但登录后节点页是空的，设备页没有数据；
即使手工加节点，rendezvous 也会一直显示抓取失败（404），TLS 部署还会因为证书不受信任而失败。
`docs/ops/README.md:136-142` 与 `how-to-configure.txt` 都没有告诉 Windows 用户需要手工做这些事
（Docker 一节才提到“节点管理里登记两个节点”）。

**建议**

在 `ConfigureSystem`（无 TLS）与 `ConfigureSystemWithTls` 两条命令行都加上：

```text
--metrics-enabled
--seed-local-nodes --rendezvous-base-url <http://127.0.0.1:5080 | https://<hostname>:5080> --relay-base-url http://127.0.0.1:5083
--ops-node-certificate-path <install dir>\tls\signaling.cer   # 仅生成/自签证书时
```

`--seed-local-nodes` 已经是幂等的（节点表非空时不改），首次安装与升级重跑都安全；升级时再执行
`--sync-local-nodes` 以处理 http↔https 的切换。安装包测试应断言这些参数出现在 `CustomAction.Target` 里。
Linux 与 macOS 的 postinstall 是现成的对照。

---

### W4 安装日志与进程列表里的部署秘密

**位置**

- [`server/Components.wxs:186-189`](../../build/installer/windows/server/Components.wxs#L186-L189)：
  `ConfigureSystem` 没有 `HideTarget`
- [`server/Components.wxs:228-231`](../../build/installer/windows/server/Components.wxs#L228-L231)：
  `ConfigureSystemWithTls` 也没有
- [`server/Package.wxs:74`](../../build/installer/windows/server/Package.wxs#L74)、
  [`90`](../../build/installer/windows/server/Package.wxs#L90)、
  [`131`](../../build/installer/windows/server/Package.wxs#L131)：`RELAYSHAREDKEY`、`POSTGRESCONNECTIONSTRING`、
  `TLSCERTIFICATEPASSWORD` 是 `Secure` 但不是 `Hidden`
- [`client/Components.wxs:182-189`](../../build/installer/windows/client/Components.wxs#L182-L189)：固定密码动作
- [`HostCredentialCommands.cs:60`](../../src/Hosts/RemoteDesktop.HostService/HostCredentialCommands.cs#L60)：从
  `args[1]` 读取密码

**发生了什么**

server 的两条配置命令行都包含 `--relay-key`、`--postgres`（可能带数据库口令）；TLS 那条还包含
`--tls-password`。WiX 的 deferred type 34 动作把完整的 Target 写进 MSI 日志，除非设置 `HideTarget="yes"`；
这两个动作都没有设置。
属性本身也没有 `Hidden`/`MsiHiddenProperties`，所以日志的属性行同样可能出现它们。`docs/ops/README.md:118-120`
只承认 TLS 口令会进日志，没有提 relay key 与 PostgreSQL 连接串。

host 包的固定密码使用了 `Hidden="yes"` + `HideTarget="yes"`，日志是干净的；但动作是 type 18（`FileRef`），
msiexec 会用 `--set-fixed-password "<密码>"` 启动 `RemoteDesktop.HostService.exe`，这条命令行在安装窗口期内
对本机其他用户可见（WMI / 进程查看器能读到）。Linux 的 host postinst 已经改成写 0600 临时文件并传
`--set-fixed-password-file`（[`postinst:234-246`](../../build/installer/linux/host/postinst#L234-L246)），
Windows 仍走 argv。

**影响**

- MSI 日志经常被附到工单、邮件、共享目录里；relay 共享密钥与 PostgreSQL 口令是可用的凭据。
- 安装期进程命令行暴露固定密码/证书口令/TLS 口令；对一台多人共用、或安装时有人登录的机器，这是可读的。
- 配置失败时用户往往被要求加 `/l*v` 再发日志，这会把泄漏面扩大。

**建议**

- 给 server 的两条配置动作加 `HideTarget="yes"`，并给三个秘密属性加 `Hidden="yes"`；这至少让日志干净。
- 不要把秘密放进命令行：relay key / PostgreSQL 连接串 / TLS 口令改成安装期写入一个受 ACL 保护的临时文件，
  由配置程序读取后删除；或者用一个最小的自定义动作（server 包已经有 net48 CA 工程）传递。
- host 固定密码改用已经存在的 `--set-fixed-password-file`：自定义动作把属性写进 `%TEMP%` 下 0600（对
  SYSTEM/管理员可读）的文件，传路径，用完删除；这与 Linux 的做法一致。
- 文档明确“安装日志里有秘密”的现状，直到修好为止。

---

### W5 生成的自签证书没有部署用的 DNS/NAT 名

**位置**

- [`Build-ServerInstaller.ps1:52`](../../build/installer/windows/server/Build-ServerInstaller.ps1#L52)：
  `-RendezvousUrl` 是唯一的地址参数
- [`Build-ServerInstaller.ps1:667-672`](../../build/installer/windows/server/Build-ServerInstaller.ps1#L667-L672)：
  `wixArguments` 没有 `TlsHostName`
- [`server/Components.wxs:186-189`](../../build/installer/windows/server/Components.wxs#L186-L189)、
  [`228-231`](../../build/installer/windows/server/Components.wxs#L228-L231)：命令行没有 `--tls-hostname`
- [`Set-ServerConfiguration.ps1:29-63`](../../build/installer/windows/server/Set-ServerConfiguration.ps1#L29-L63)：
  没有 `-TlsHostName`
- [`ServerSetup.cs:74-78`](../../src/Server/RemoteDesktop.Server.Shared/ServerSetup.cs#L74-L78)：
  代码支持 `TlsHostName`
- [`SelfSignedCertificate.cs:167-223`](../../src/Server/RemoteDesktop.Server.Shared/SelfSignedCertificate.cs#L167-L223)：
  SAN 只有本机名、DNS 主机名与本地 IPv4
- Linux / macOS 已经传了：`postinst:221`、`macOS postinstall:169`

**发生了什么**

Windows 安装包的 TLS “自动生成”路径调用 `SelfSignedCertificate.Ensure(installDirectory, notes)`，
没有 `additionalDnsNames`。证书的 SAN 只有 `Environment.MachineName`、`Dns.GetHostName()` 与本机所有 IPv4。
客户端如果通过 `wss://rd.example.com:5080/ws` 或 NAT 地址访问，TLS 名称校验会失败——**即使**
`-ServerCertificate` 已经把这张证书装进受信任根，或者用户手工 `Import-Certificate` 了 `tls\signaling.cer`。
`New-ServerCertificate.ps1 -DnsName ... -IpAddress ...` 可以生成带名字的证书，但那要求不用“自动生成”这一页，
而是先手工生成 `.pfx` 再传 `TLSCERTIFICATEPATH`。

Linux 的 debconf 问“公网主机名”，macOS 的安装面板/答案文件也传 `--tls-hostname`；Windows 两者都没有。
`Set-ServerConfiguration.ps1 -TlsGenerate` 也无法补名字。

**建议**

- 增加 `TLSCERTIFICATEHOSTNAME`（或 `TLSHOSTNAME`）属性，同时在安装向导的 TLS 区加一个输入框；
  传给 `--tls-hostname`，可接受逗号/分号分隔的多个名字。
- `Set-ServerConfiguration.ps1` 增加 `-TlsHostName`，并传给同一个参数。
- 生成的证书 SAN 至少包含：本机名、所有部署会用的 DNS 名、所有客户端会拨的 IP（NAT 场景写公网 IP）。
- 在证书确认页/文档里写清“客户端拨哪个名字，就把它填进来”；`docs/ops/README.md` 的 TLS 表补一行。
- 如果不做，至少把“自动生成只适合用机器名/IP 直连的部署”写成限制，避免用户以为信任了证书就能用 DNS。

---

## 5. 中（P2）

### W6 卸载 server 包会留下数据库、证书和日志

**位置**

- [`server/Package.wxs`](../../build/installer/windows/server/Package.wxs) / [`Components.wxs`](../../build/installer/windows/server/Components.wxs)：
  没有 `REMOVEDATA` 属性，也没有 `--cleanup-system` 之类的卸载动作
- [`ServerSetup.cs:677-690`](../../src/Server/RemoteDesktop.Server.Shared/ServerSetup.cs#L677-L690)：
  `rendezvous.db` / `ops.db` 都在 `%ProgramData%\Remote Desktop Server`
- [`SelfSignedCertificate.cs:73-91`](../../src/Server/RemoteDesktop.Server.Shared/SelfSignedCertificate.cs#L73-L91)：
  生成的证书在 `LocalMachine\My`
- [`docs/ops/README.md:167`](../ops/README.md)：只提到 `server-setup.log` 会留下

**影响**

`msiexec /x` 之后，以下东西仍在机器上：

- `%ProgramData%\Remote Desktop Server\rendezvous.db`（全部设备码）
- `%ProgramData%\Remote Desktop Server\ops.db`（管理员口令散列、TOTP 密钥、审计/告警历史）
- `<安装目录>\tls\signaling.cer` 与 `server-setup.log`
- 证书存储里的 `RemoteDesktop Server (<机器名>)` 私钥

机器退役、转手或重装成别的角色时，这些是残留凭据/数据。client/host 有 `REMOVEDATA=1`，
server 没有对应选项，文档也只提了日志。

**建议**

- 给 server 增加 `REMOVEDATA`（默认 0）与一个 deferred cleanup 动作，删除 `%ProgramData%\Remote Desktop Server`
  下的库与日志；证书存储里的证书是否删除单独用一个开关（重装复用同一张证书是合理的）。
- 卸载时删除 `<安装目录>\tls`（如果 MSI 不管理它）；或者明确写“卸载保留数据/证书”，并提供手工清理命令。
- 文档的“卸载”一节把三类残留列全。

### W7 `Set-ServerConfiguration.ps1` 改数据库不重启 Ops

**位置**

- [`Set-ServerConfiguration.ps1:69-70`](../../build/installer/windows/server/Set-ServerConfiguration.ps1#L69-L70)：
  `$services = @('RemoteDesktopRendezvous', 'RemoteDesktopRelay')`
- [`Set-ServerConfiguration.ps1:121-129`](../../build/installer/windows/server/Set-ServerConfiguration.ps1#L121-L129)：
  只重启这两个
- [`ServerSetup.cs:504-573`](../../src/Server/RemoteDesktop.Server.Shared/ServerSetup.cs#L504-L573)：
  `ConfigureOps` 在数据库 provider 变化时写 `Ops\appsettings.json`
- [`how-to-configure.txt:17`](../../build/installer/windows/server/how-to-configure.txt#L17)、
  [`docs/ops/README.md:245`](../ops/README.md)：文档也说“重启两个服务”

**影响**

`-DatabaseProvider postgres` / `-DatabaseProvider sqlite` / `-PostgresConnectionString ...` 会把 Ops 的库也改掉，
但 `RemoteDesktopOps` 不重启，继续用旧库。之后 `--create-admin` 读的是新配置，账号建在新库里，服务却在旧库上，
登录失败；旧的监控历史看起来“消失”，其实是新库还是空的。这与 macOS 评审里的 M11 是同一类问题。

**建议**

数据库相关参数变化时把 `RemoteDesktopOps` 也加入重启列表（或每次都重启三个服务，`-NoRestart` 保持原语义）；
`how-to-configure.txt` 与 `docs/ops/README.md` 改成“重启受影响的三个服务”。

### W8 服务端秘密对本机用户可读

**位置**

- [`server/Components.wxs:20-40`](../../build/installer/windows/server/Components.wxs#L20-L40)、
  [`57-72`](../../build/installer/windows/server/Components.wxs#L57-L72)、
  [`86-103`](../../build/installer/windows/server/Components.wxs#L86-L103)：三个服务都跑 `LocalSystem`
- [`ServerSetup.cs:934-943`](../../src/Server/RemoteDesktop.Server.Shared/ServerSetup.cs#L934-L943)：
  只在非 Windows 上设置 Unix 权限
- [`how-to-configure.txt:56-58`](../../build/installer/windows/server/how-to-configure.txt#L56-L58)：
  文档自己承认“本机任何用户都能读”
- Linux 的对照：[`postinst:225-240`](../../build/installer/linux/server/postinst#L225-L240)（`0640 root:remotedesktop`、
  `0750`、`/var/lib` `0700`）

**影响**

`Rendezvous\appsettings.json` 里有 relay 共享密钥、可能还有 PostgreSQL 口令与 TLS `.pfx` 口令；
`Relay\appsettings.json` 有同一个密钥；`Ops\appsettings.json` 可能有 `NodeToken` 与 ingest key；
`%ProgramData%\Remote Desktop Server\ops.db` 里有管理员口令散列与 TOTP 密钥。
Program Files 与 ProgramData 的默认 ACL 都允许 `Users` 读取。对一台有非管理员本地账号的服务器，
这些秘密对它们是可读的。Linux 包已经收紧了，Windows 没有。

**建议**

- 安装时用 `icacls` 把 `%ProgramData%\Remote Desktop Server` 设成 `SYSTEM`/`Administrators` 完全控制、
  去掉 `Users` 继承；三个 `appsettings.json` 同样收紧（`SYSTEM` + `Administrators` 只读，服务以 SYSTEM 运行）。
- `tls\signaling.cer` 是公钥，可以保留可读；私钥不在文件里（Windows 存在证书存储）。
- 如果以后改成专用服务账号，ACL 跟着账号走；文档补上“本机用户能读到哪些秘密”。
- 至少把 `ops.db` 收紧，它和 client/host 的“本机用户可读设备码/临时密码”边界不是一回事。

### W9 证书确认框显示的是 `TLSCERTIFICATEPATH`，不是刚选的文件

**位置**

- [`SettingsPageActions.cs:96-102`](../../build/installer/windows/server/CustomActions/SettingsPageActions.cs#L96-L102)：
  Browse 只设置 `TLSCERTIFICATEFILE` 与 `TLSCERTIFICATECHOSEN`
- [`server/Components.wxs:369-374`](../../build/installer/windows/server/Components.wxs#L369-L374)：浏览按钮
- [`server/Components.wxs:419-430`](../../build/installer/windows/server/Components.wxs#L419-L430)：
  `CertificateChosenDlg` 显示 `[TLSCERTIFICATEPATH]`
- [`server/Components.wxs:399-401`](../../build/installer/windows/server/Components.wxs#L399-L401)：
  `TLSCERTIFICATEFILE` → `TLSCERTIFICATEPATH` 的映射在 **Next** 按钮上
- [`ServerInstallerPackageTests.cs:212-216`](../../tests/RemoteDesktop.Platform.Windows.UnitTests/ServerInstallerPackageTests.cs#L212-L216)：
  测试断言的也是 `[TLSCERTIFICATEPATH]`

**发生了什么**

用户点“浏览…”，文件对话框返回后动作把路径写进 `TLSCERTIFICATEFILE`，然后导航到 `CertificateChosenDlg`。
这个对话框显示的是 `TLSCERTIFICATEPATH`，而它在用户按 Next 之前仍然是 `auto`（首次浏览）或上一次的路径。
于是“已选择证书文件”页显示 `auto`，不是刚选的文件。注释说这一页是为了让用户确认路径，实际确认的是另一个值。

另外，如果用户选择“使用已有的证书文件”但**没有浏览**，`TLSCERTIFICATEFILE` 仍是那个单空格；按 Next 后
`TLSCERTIFICATEPATH` 变成空格，`ServerSetup` 把空白当“没有证书”，安装**静默走明文 http**。页面没有阻止
Next，也没有提示。

**建议**

- Browse 动作同时设置 `TLSCERTIFICATEPATH`（或让确认框显示 `TLSCERTIFICATEFILE`），确认页就能显示真实路径。
- 在 Next 上加条件：`TLSCHOICE <> "file" OR TLSCERTIFICATEFILE <> " "`，否则弹一个说明或留在本页；
  不要让“选了文件模式但没选文件”静默变成 http。
- 更新 `ServerInstallerPackageTests`，让它断言确认页显示的是刚选的文件，而不是把当前属性名写死。

### W10 Ops CLI 不展开 `%ProgramData%`，升级/修复后可能建到错误的库

**位置**

- [`Build-ServerInstaller.ps1:579`](../../build/installer/windows/server/Build-ServerInstaller.ps1#L579)：
  模板写 `Path="%ProgramData%\Remote Desktop Server\ops.db"`
- [`OpsServer.cs:27-36`](../../src/Server/RemoteDesktop.Server.Ops/OpsServer.cs#L27-L36)：服务启动时展开环境变量
- [`Ops/SetupCommands.cs:266-286`](../../src/Server/RemoteDesktop.Server.Ops/SetupCommands.cs#L266-L286)：
  `--create-admin` / `--seed-local-nodes` 读配置时**没有**展开
- [`OpsStore.cs:42-60`](../../src/Server/RemoteDesktop.Server.Ops.Storage/OpsStore.cs#L42-L60)：
  `Path.GetFullPath` + `Directory.CreateDirectory`
- 文档命令：[`docs/ops/README.md:674-676`](../ops/README.md)、
  [`how-to-configure.txt:65-72`](../../build/installer/windows/server/how-to-configure.txt#L65-L72)

**发生了什么**

正常首装时 `ConfigureSystem` 会传 `--database auto`，`ConfigureOps` 把 Ops 的路径改写成**绝对**的
`C:\ProgramData\...\ops.db`，所以文档里的 `--create-admin` 能打开服务同一个库。
但在**升级、同版本重装、修复**时 `ConfigureSystem` 不运行；如果此时 live 文件是包内模板（W1），
`Path` 就是字面量 `%ProgramData%\...`。服务的 `OpsServer.AddOps` 会展开它；`SetupCommands.ReadOptions` 只做
`Path.IsPathRooted` 判断，而 `%ProgramData%\...` 不是 rooted 路径，于是被拼到 Ops 目录下：

```text
C:\Program Files\Remote Desktop Server\Ops\%ProgramData%\Remote Desktop Server\ops.db
```

`OpsStore.Open` 会创建这些目录和文件，`--create-admin` 打印成功，但账号在错误的库里；服务读的是
`C:\ProgramData\...`，登录永远失败。对 `--seed-local-nodes` 也会是同样结果。

**建议**

在 `ReadOptions` 里先 `Environment.ExpandEnvironmentVariables`，再做 rooted/相对路径判断；
与 `OpsServer.AddOps` 用同一段解析逻辑，避免两个入口再次分叉。补一个单测：`Path` 为
`%ProgramData%\Remote Desktop Server\ops.db` 时，CLI 与服务得到同一个绝对路径。

### W11 地址校验比运行时弱，安装期失败被吞掉

**位置**

- [`Build-ClientInstaller.ps1:575-583`](../../build/installer/windows/client/Build-ClientInstaller.ps1#L575-L583)：
  `^wss?://[^/]+` 正则
- [`client/Components.wxs:148-151`](../../build/installer/windows/client/Components.wxs#L148-L151)、
  [`229-231`](../../build/installer/windows/client/Components.wxs#L229-L231)：`ConfigureSystem` + `Return="ignore"`
- [`RendezvousAddress.cs:40-65`](../../src/Core/RemoteDesktop.Domain/RendezvousAddress.cs#L40-L65)：
  只要求 scheme 与 host，不要求 `/ws`
- [`TransportOptions.cs:53-60`](../../src/Core/RemoteDesktop.Transport/TransportOptions.cs#L53-L60)：同样只查 scheme/host
- [`RendezvousClient.cs:87`](../../src/Core/RemoteDesktop.Transport/Rendezvous/RendezvousClient.cs#L87)：
  直接用 `RendezvousUri` 连接
- [`RendezvousServer.cs:13`](../../src/Server/RemoteDesktop.Server.Rendezvous/RendezvousServer.cs#L13)：
  服务端只映射 `/ws`
- [`Build-ServerInstaller.ps1:548`](../../build/installer/windows/server/Build-ServerInstaller.ps1#L548)、
  [`ServerSetup.cs:718-759`](../../src/Server/RemoteDesktop.Server.Shared/ServerSetup.cs#L718-L759)：
  服务端地址只检查 `http(s)://` 前缀

**发生了什么**

- 构包时 `-RendezvousUri ws://server:5080`（没有 `/ws`）能通过正则，写进 appsettings，打包成功；
  客户端连的是 `/`，服务端只监听 `/ws`，永远注册不上。安装期的 `RENDEZVOUSURI` 同样能通过；
  `SystemSetup.SetRendezvousUri` 的 `RendezvousAddress.TryParse` 也不要求路径。
- 安装期如果地址真的非法（例如带空格、非法端口），`--configure-system` 返回 2，但动作是 `Return="ignore"`，
  安装继续成功，`appsettings.json` 保持包内默认值；host 的 `SetupCommands` 没有写日志，用户什么都看不到。
- 服务端 `RENDEZVOUSURL=http://`（没有 host）能通过 `^https?://`，安装后 Kestrel 起不来；构建脚本只警告 TLS 缺失，
  不校验 URL 是否可解析。

**建议**

- 构包与安装期都复用 `RendezvousAddress`（或更严格的 `Uri` 校验），并要求路径是服务端实际映射的 `/ws`；
  或者在客户端把空路径补成 `/ws`，让两种写法都可用。
- 服务端 URL 用 `Uri.TryCreate` + host/port 校验；失败在构包/安装时直接报错。
- 安装期 `ConfigureSystem` 对“显式给出的地址非法”不要静默 ignore：至少写一条 `%ProgramData%` 下的日志，
  并在 MSI 日志/完成页提示；更好的做法是让该动作失败并回滚，或保留但把错误显示出来。
- 加测试：`ws://host:5080`、`ws://host:5080/ws`、`wss://host/ws`、非法端口分别走构包/安装/启动三条路径。

### W12 服务器配置失败会回滚，原因可能只剩一个随目录消失的日志

**位置**

- [`server/Components.wxs:186-189`](../../build/installer/windows/server/Components.wxs#L186-L189)、
  [`228-231`](../../build/installer/windows/server/Components.wxs#L228-L231)：`Return="check"`
- [`Rendezvous/SetupCommands.cs:75-94`](../../src/Server/RemoteDesktop.Server.Rendezvous/SetupCommands.cs#L75-L94)：
  失败原因写 `server-setup.log`
- [`docs/ops/README.md:133-134`](../ops/README.md)：安装程序不会给任何人看这一步的输出

**影响**

`RELAYPUBLICENDPOINT` 解析失败、PostgreSQL 没给连接串、证书口令错误等会让 `ConfigureSystem` 以非零退出，
`Return="check"` 使整个安装回滚。用户看到的是 MSI 的通用错误（1722/1603），真正的句子在
`<安装目录>\server-setup.log`；回滚时这个目录可能被清掉/改名，用户拿不到原因。这会让“配置错”看起来像“安装包坏了”。

**建议**

- 把 `server-setup.log` 写到 `%ProgramData%\Remote Desktop Server\`（不受 MSI 安装目录回滚影响），
  或者让自定义动作把 exe 的 stdout 写进 MSI 日志并显示在完成页。
- 在 `docs/ops/README.md` 的排错一节写明“安装失败先看 MSI 日志与 `%ProgramData%` 下的 setup 日志”。
- 如果保留 `Return="check"`，至少保证失败原因可见；否则会重复 macOS 评审里“配置步骤失败但没人知道为什么”的问题。

---

## 6. 低（P3）与次要观察

1. **`Set-ServerConfiguration.ps1` 在 Windows 11 默认策略下可能跑不起来**：Windows 客户端版的默认
   ExecutionPolicy 是 `Restricted`，文档却让人直接在管理员 PowerShell 里执行 `.\Set-ServerConfiguration.ps1`
   （[`docs/ops/README.md:244-253`](../ops/README.md)、[`how-to-configure.txt:11-15`](../../build/installer/windows/server/how-to-configure.txt#L11-L15)）。
   在 Windows Server（默认 `RemoteSigned`）上没问题，在 Windows 11 上会报“running scripts is disabled”。
   建议文档给出 `powershell -ExecutionPolicy Bypass -File .\Set-ServerConfiguration.ps1 ...`，或随包装一个 `.cmd` 入口。
2. **`TLSCERTIFICATEPASSWORD` / `RELAYSHAREDKEY` 在 `-WhatIf` 下会被打印**：
   [`Set-ServerConfiguration.ps1:97-99`](../../build/installer/windows/server/Set-ServerConfiguration.ps1#L97-L99)
   把整个参数数组作为 `ShouldProcess` 的 target；文档又鼓励“拿不准先加 `-WhatIf`”。建议 target 只列参数名，
   或在打印时把秘密替换成 `***`。
3. **服务端安装包的语言**：`SummaryInformation` 说 “Chinese UI”，但 `WixUI_InstallDir` 的标准页
   （Welcome/License/InstallDir/Progress/Finish）按钮与标题来自 `WixUI_en-us.wxl`。0.3.0 MSI 的
   `LicenseAgreementDlg` 里是 `Please read the following license agreement carefully`、`&Next`、`Cancel`；
   只有自定义页是中文。要么本地化 WixUI，要么把 SummaryInformation/文档改成“中文自定义页 + 英文标准向导”。
4. **数据库输入框显示 `auto`**：`PrefillSettings` 在属性未设置时写入 `"auto"`
   （[`SettingsPageActions.cs:168-179`](../../build/installer/windows/server/CustomActions/SettingsPageActions.cs#L168-L179)），
   而 `PostgresEdit` 绑定该属性，于是框里显示 `auto`；标签却写“留空 = 用本机 SQLite”。
   `NormalizeSettings` 在 Next 时会把空白改成 `auto`，所以 Prefill 完全可以留一个空格让框显示为空。
5. **`DATABASEPATH` 不展开环境变量**：`Package.wxs:83-86` 的说明暗示可以写 `%ProgramData%\...`，但
   `ServerSetup.ConfigureDatabase` 不展开，`DatabaseSettings`/`DeviceRegistryRegistration` 也不展开；
   传字面量会在 System32 下建一个名为 `%ProgramData%` 的目录。要么展开（与 `RelayEndpoint` 的 `Expand` 一致），
   要么明确写“必须是绝对路径，不展开环境变量”。
6. **client 的 `New-FileList` 生成器只适用于扁平输出**：
   [`Build-ClientInstaller.ps1:205-220`](../../build/installer/windows/client/Build-ClientInstaller.ps1#L205-L220)
   把 `<Directory>` 写在 `<ComponentGroup>` 里，而 server 的生成器注释明确说 WiX 5 不支持这种结构
   （[`Build-ServerInstaller.ps1:168-182`](../../build/installer/windows/server/Build-ServerInstaller.ps1#L168-L182)）。
   今天 win-x64 自包含发布是扁平的（296 个文件、0 个子目录），所以没暴露；将来 publish 多一个子目录
   （例如 native 资源目录）就会构包失败。建议让 client 复用 server 的 DirectoryRef 生成方式。
7. **server 包没有 client 包那样的 `taskkill` 兜底**：client 用 `StopLeftoverProcesses` 在 `InstallValidate` 之前
   杀掉另一个会话里的 Client/Agent（[`client/Components.wxs:209-219`](../../build/installer/windows/client/Components.wxs#L209-L219)）；
   server 只有 `util:CloseApplication`，而它自己的注释说 Restart Manager 拒绝处理另一个会话的进程
   （[`server/Package.wxs:182-195`](../../build/installer/windows/server/Package.wxs#L182-L195)）。
   运维在 RDP 会话里以前台方式跑过服务器程序时，升级可能因文件占用失败。建议加同样的 taskkill 动作。
8. **自签证书不会续期**：`SelfSignedCertificate.Ensure` 只要找到同 subject 且带私钥的证书就复用，不检查
   `NotAfter`（[`SelfSignedCertificate.cs:76-88`](../../src/Server/RemoteDesktop.Server.Shared/SelfSignedCertificate.cs#L76-L88)）。
   三年后证书过期，`-TlsGenerate` 仍会把这张过期证书写回配置，Kestrel 继续提供它，客户端拒绝连接。
   建议检查有效期，或文档写清“到期前手工删除证书/换证书”。
9. **`New-LicenseRtf.ps1` 的非 ASCII 诊断永远为真**：
   [`New-LicenseRtf.ps1:75`](../../build/installer/New-LicenseRtf.ps1#L75) 对 `Get-Content -Raw` 的单个字符串做
   `Where-Object`，`-ne $null` 恒为真；日志里的 `non-ASCII passthrough: True` 没有信息量。不影响包内容。
10. **`ConfigureSystem` 的注释与代码相反**：server `Components.wxs:172-185` 说“它们忽略退出码”，
    但两条动作都是 `Return="check"`；client `Components.wxs:138-147` 才用 `Return="ignore"`。
    注释是从 client 复制的。把注释改成“server 配置失败要回滚”或改代码，别让下一个人按注释理解。
11. **`Set-ServerConfiguration.ps1` 单独给 `-TlsCertificatePassword` 是空操作**：脚本会通过
    `$arguments.Count` 检查（[`Set-ServerConfiguration.ps1:93-95`](../../build/installer/windows/server/Set-ServerConfiguration.ps1#L93-L95)），
    然后调用 `--configure-system` 只带口令，`ServerSetup` 没有路径就忽略口令，脚本仍重启服务并打印成功。
    建议“口令必须与路径一起给”。
12. **host 页面的提示“密码保存后需要重启 RemoteDesktopHost 服务才生效”**：安装动作在 `StartServices` 之前运行，
    首装/升级本来就会启动服务；这句话会让用户以为还要手工重启。对客户端界面里的修改是对的，对安装页不准确。
13. **文档与错误信息的小漂移**：
    - [`docs/ops/README.md:1296`](../ops/README.md) 说 server 发布用 ReadyToRun；`Build-ServerInstaller.ps1:520-522`
      明确说 server **不**用 R2R（client/host 用）。
    - [`docs/ops/README.md:1384`](../ops/README.md) 说 `artifacts/installer/windows/` 里没有带 `Windows` 的文件；
      实际产物是 `RemoteDesktop-Client-Windows-x64-0.3.0.0.msi` 等，发布时才改成 tag。
    - [`docs/ops/README.md:909-911`](../ops/README.md) 说残留进程在删旧文件**之后**才关；`StopLeftoverProcesses`
      是 `Before="InstallValidate"`，在删文件之前。
    - [`client/RemoteDesktop.Installer.wixproj:58`](../../build/installer/windows/client/RemoteDesktop.Installer.wixproj#L58)
      的错误信息让人传 `-SourceDirectory`，实际参数是 `-PublishDirectory`。
    - `ServerInstallerPackageTests` 的 skip 消息写成 `build\installer\server\windows\...`，实际路径是
      `build\installer\windows\server\...`；client/host 的测试也有同类反向路径。
14. **手动安装/卸载脚本**：[`Install-HostService.ps1:84-90`](../../build/service/windows/Install-HostService.ps1#L84-L90)
    添加 UDP 防火墙规则，[`Uninstall-HostService.ps1`](../../build/service/windows/Uninstall-HostService.ps1) 不删除它。
    手动升级也不清理源目录里已经不存在的旧文件。建议卸载时按显示名删除规则，升级时先清空目标目录。
15. **服务账号**：server 三个服务都以 `LocalSystem` 运行；macOS 评审把 root 守护进程列为已知边界，Linux 包用专用
    `remotedesktop` 账号。Windows 可以用虚拟服务账号（`NT SERVICE\...`）或专用账号配合 ACL（见 W8），
    但这是跨平台的一致性问题，不作为本轮阻断项。
16. **`ServerSetup.Configure` 的注释说 Windows 安装包不传数据库选项**：`Components.wxs:186-189` 实际总是传
    `--database auto --database-path "" --postgres auto`，所以 `ConfigureOps` 在首装一定会写 Ops 的库路径。
    功能上通常无害（写的是同一个绝对位置），但注释会误导下一个人。
17. **`Set-ServerConfiguration.ps1` 没有 Ops 的入口**：Linux/macOS 的 configure 脚本有 `--ops-listen` /
    `--ops-database-path`；Windows 只能手工改 `Ops\appsettings.json`。不一定是缺陷，但文档应说明。

---

## 7. 与 Linux / macOS 包的对照

| 事项 | Linux | macOS | Windows | 对应 |
|---|---|---|---|---|
| 安装时 seed 本地节点 | ✅ `postinst` | ✅ `postinstall` + `--sync-local-nodes` | ❌ 没有 | W3 |
| 信令 `/metrics` | ✅ `--metrics-enabled` | ✅ `--metrics-enabled` | ❌ 没有 | W3 |
| Ops 对自签证书的信任 | 导入系统信任库 | `--ops-node-certificate-path` pin | 两者都没有 | W3 |
| 自签证书的 DNS/NAT 名 | debconf `tls-hostname` | 面板/答案文件 | 没有入口 | W5 |
| live settings 位置 | `/etc/remotedesktop-server`，升级不碰 | `appsettings.default.json` + 缺失时复制 | Program Files + 早移除 | W1/W2 |
| 升级保留运维配置 | ✅ | ✅（postinstall 只在缺失时复制） | ❌ | W1/W2 |
| 固定密码的传递 | 0600 临时文件 + `--set-fixed-password-file` | Keychain + 答案文件 | 命令行 argv | W4 |
| 服务端配置可读性 | `0640 root:remotedesktop` / `0750` / `/var/lib` `0700` | root:wheel 守护进程（已知边界） | 继承 Program Files/ProgramData ACL | W8 |
| 卸载数据 | purge 删除数据/证书 | `uninstall-server.sh` 删除安装目录与 plist | 留下 DB/证书/日志 | W6 |
| 改配置后重启 Ops | `systemctl restart` 明确 | kickstart | 只重启两个 | W7 |
| 安装/升级冒烟测试 | 有端到端脚本（见 CHANGELOG） | 评审后补了 postinstall 检查 | 只有 MSI 数据库检查 | W15 |

Linux 的 `postinst` 在 seed 节点前把生成的证书导入系统信任库并传 `--metrics-enabled`
（[`postinst:215-223`](../../build/installer/linux/server/postinst#L215-L223)、
[`316-337`](../../build/installer/linux/server/postinst#L316-L337)）；macOS 的 postinstall 在 TLS 时 pin 证书并
seed/sync 节点（[`postinstall:441`](../../build/installer/macos/server/scripts/postinstall#L441)、
[`463`](../../build/installer/macos/server/scripts/postinstall#L463)）。Windows 在“装完能用”的这一层落后于两者。

---

## 8. 验证记录与外部依据

**本机命令（节选）**

```text
# 0.3.0 client MSI：升级序列（Windows Installer COM，只读）
InstallInitialize        1500
RemoveExistingProducts   1501
InstallFiles             4000
StartServices            5900

# 0.3.0 client MSI：Settings 组件（384 = 64-bit + NeverOverwrite）与 AppSettingsFile
Settings | {C3D4E5F6-A7B8-4C9D-8E0F-1A2B3C4D5E6F} | 384
AppSettingsFile | khe-vcmc.jso|appsettings.json | Settings

# 0.3.0 host MSI：密码页的转场顺序（Order 6 覆盖内置的 4）
InstallDirDlg | Next | NewDialog | HostSettingsDlg | 1 | 6
VerifyReadyDlg | Back | NewDialog | HostSettingsDlg | 1 | 6

# 0.3.0 client MSI：标准向导页仍是英文
LicenseAgreementDlg | Title | {\WixUI_Font_Title}End-User License Agreement
LicenseAgreementDlg | Next  | &Next
LicenseAgreementDlg | Cancel| Cancel

# %ProgramData% 的路径语义（PowerShell）
[System.IO.Path]::IsPathRooted('%ProgramData%\Remote Desktop Server\ops.db')   # False
[System.IO.Path]::Combine('C:\Program Files\Remote Desktop Server\Ops',
    '%ProgramData%\Remote Desktop Server\ops.db')
# C:\Program Files\Remote Desktop Server\Ops\%ProgramData%\Remote Desktop Server\ops.db
[System.Environment]::ExpandEnvironmentVariables('%ProgramData%\Remote Desktop Server\ops.db')
# C:\ProgramData\Remote Desktop Server\ops.db

# 三个平台的 server 安装后步骤对照（grep）
# Windows: 无 --metrics-enabled / --seed-local-nodes / --tls-hostname / --ops-node-certificate-path
# Linux:   postinst 传 --metrics-enabled 与 --seed-local-nodes
# macOS:   postinstall 传 --metrics-enabled / --seed-local-nodes / --sync-local-nodes / --ops-node-certificate-path

# client 的 publish 输出是扁平的（所以 §6 第 6 条的生成器问题暂未暴露）
artifacts/app/windows/client: 296 files, 0 subdirectories
```

**没有执行的命令**

- `msiexec /i ...` / `msiexec /x ...`：会改动本机服务、防火墙、证书存储与 ProgramData。
- 构建 1.0.0 MSI：仓库产物是 0.3.0；源码已足够判断序列与动作。
- 用真实证书签名 / 验证 SmartScreen / UIAccess。

**外部依据**

- Windows Installer `RemoveExistingProducts` 的放置与共享/用户修改文件的引用计数：
  [Microsoft Learn — RemoveExistingProducts action](https://learn.microsoft.com/en-us/windows/win32/msi/removeexistingproducts-action)、
  [Aaron Stebner — How to retain user-customized files during a Windows Installer major upgrade](https://learn.microsoft.com/en-us/archive/blogs/astebner/mailbag-how-to-retain-user-customized-files-during-a-windows-installer-major-upgrade)
- Windows 服务的内容根：`UseWindowsService` 在服务模式下把 `ContentRootPath` 设为 `AppContext.BaseDirectory`：
  [Host ASP.NET Core in a Windows Service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service)
- Windows 客户端默认 ExecutionPolicy 为 `Restricted`，Windows Server 为 `RemoteSigned`：
  [about_Execution_Policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies)
- 仓库自己的记录：[`CHANGELOG.md:236-242`](../../CHANGELOG.md)（0.3.0 → 1.0.0 丢失 appsettings）、
  [`CHANGELOG.md:663-664`](../../CHANGELOG.md)（host 固定密码的隐藏属性）

---

## 9. 建议的修复顺序

1. **W1 / W2**：升级丢配置是唯一会让“装完好好的服务器，升级后不工作”的问题。先决定 live settings 放哪
   （推荐 `%ProgramData%`），再补真实的升级测试；server 的 Relay 空密钥必须在新版本里不可能出现。
2. **W3**：Ops 是 server 包的一部分，装完没有节点/没有 `/metrics` 等于首装验收不通过。照 Linux/macOS 的
   postinstall 补 `--metrics-enabled`、`--seed-local-nodes`、（TLS 时）`--ops-node-certificate-path` 与
   `--sync-local-nodes`。
3. **W4 / W5**：秘密不进日志/argv；自签证书支持部署名。两项都影响“能不能安全地交付给客户”。
4. **W6 / W7 / W8**：卸载清理、Ops 重启、ACL 收紧，属于上线后的运维质量。
5. **W9–W12**：证书确认页、Ops CLI 路径、地址校验、配置失败可见性。
6. **§6 的 P3**：文档校正、语言、生成器、执行策略与手动脚本清理。

## 10. 一句话总结

**Windows 包的骨架、服务定义、签名链与包内容检查都是对的；问题集中在“升级之后”和“服务器装完之后的最后一公里”：
live settings 在早移除的 major upgrade 里被删掉，Ops 没有被接到两个服务器上，部署秘密在日志/进程列表里，
自签证书也没有部署用的名字。** 前三项里，第一项会在下一次升级时直接让 Relay 服务起不来；
后两项会让新装的监控台“能打开、没有数据”。
