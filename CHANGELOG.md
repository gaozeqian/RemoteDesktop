# 更新日志 (Changelog)

本项目所有的显著变更都会记录在此文件中。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)。

## [未发布] · Linux 平台支持

### Added
- **Linux 被控端与客户端**：`RemoteDesktop.Platform.Linux`（屏幕走桌面 portal + PipeWire，输入走 libei，
  编解码走 libavcodec，音频走 PulseAudio，防休眠走 logind），以及两个类 Unix 系统共用的 `RemoteDesktop.Posix`
  （PTY、0600 文件密钥库、用户上下文守卫）。
- **Linux 被控端是一条 `systemd --user` 服务**，不是系统服务：Wayland 的 portal 只在该会话内应答，
  所以它跑在图形会话里，自己就是采集和注入的那一方——**没有 Agent**，因为不需要拆。
- **`.deb` 安装包**：`remotedesktop-client`、`remotedesktop-host`、`remotedesktop-server`。都是自包含发布，
  目标机器不需要装 .NET；`libmsquic` 随包放在可执行文件旁，所以也不需要设 `LD_LIBRARY_PATH`。
  服务端的配置在 `/etc/remotedesktop-server/`，升级不覆盖。
- **发布流水线产出 Linux 包**：`release.yml` 多了一个 ubuntu job，三个 `.deb` 和两个 MSI 一起发出。

### Changed
- **宿主装配按平台分开**：`Platform.Windows` 与 `Platform.Linux` 各自注册同一批端口，业务代码不知道自己在哪个平台上。
  客户端的结构、文件传输、终端、摄像头在 Linux 上同样可用。
- `MfVideoRecorderFactory` 从客户端三个 ViewModel 里删掉了——录制工厂本来就由平台层通过 DI 提供，
  那个 `IsWindows()` 分支只会在非 Windows 上让录制**静默不工作**。

### Fixed
- **`SpaPod` 解析器少读 4 个字节**：SPA 对象体是 `{type, id}`，没有属性个数，而解析器在 id 后面读了一个"个数"
  ——那其实是第一个属性的 key（真实值是 131073）。此前的测试全绿是因为用例字节按同一个错误理解手写。
  这类错误在真实链路上的表现是"格式协商莫名其妙失败"。
- **portal 文件描述符的竞态**：`CallForFdAsync` 原本在 `CallMethodAsync` 返回之后才 dup 那个 fd，而 fd 属于
  message、message 在读取委托返回后就释放了——偶尔 dup 到一个已关闭的编号，报 EBADF，而连接本身好好的。
  平时不显形，测试并行度上来才出现。

### Linux 上不会工作的事（相对 Windows 的真实回退）
- **登录界面 / 登录前的远程访问**：GNOME 的登录界面是另一个用户、另一个 VT，私有用户会话够不着。
  Windows 上能换 Agent 接管安全桌面，这里没有对应物。
- **无人值守下的操作**：portal 不允许远程桌面会话持久化，所以**控制权限每次连接都要有人在机器前点一次同意**。
  共享屏幕授权一次就记住了，但无人值守的机器因此**可以被观看、不能被操作**。
- **剪贴板已实现并双向验证**（走 portal：在远程桌面会话开始前申请，发布用 `SetSelection`、读取用
  `SelectionRead`，本机程序复制的内容也能触发同步）。摄像头已实现（走 portal 授权 + PipeWire 节点，
  JPEG 与 4:2:2 都支持），但**本机没有摄像头，那条路一次都没跑过**，请当成未验证。
  Ctrl+Alt+Del、快速用户切换、隐私屏同 Windows 一样不支持。
- 光标元数据在本机收不到（合成器不送），控制端只能画自己最后知道的那个指针。

## [0.3.0] - 2026-10-11 · 安装向导与自签名证书

### Changed
- [CI] **集成测试失败后自动重跑一次**（`ci.yml` 与 `release.yml` 同策略）。这些测试要跑真实进程、真实 PTY、
  真实 UDP 打洞和真实 Media Foundation 编码器，而 runner 又慢又抢：同一提交常常这次红一个测试、下次红另一个
  ——2026-10-11 一天里三个不同的测试各红过一次，其中一个在 Release 流水线里通过、几分钟后的 master CI 里失败。
  现在两次都失败才判红，第一次失败会打成 GitHub 警告并**列出失败的测试名**。
  **代价说清**：真实存在的偶发 bug 同样会被这一次重试掩盖，所以那行警告不是噪音，别忽略它。

### Added
- [Server] **服务端安装包有向导页了**（"服务器设置"，插在选目录与确认之间）：数据库连接串、中继公网地址、
  以及信令是否走 TLS（不启用／**自动生成一张自签名证书**／使用已有证书文件+口令）。此前这些只能在命令行上
  传 `msiexec POSTGRESCONNECTIONSTRING=...`——双击安装会全取默认值，而默认值是"不用数据库"和"明文 http"，
  正是后果最重的两项：前者让设备代码每次重启都变（无人值守的机器就此失联），后者让设备代码与中继票据
  在途中可被读取。这两条警告其实一直写在 `server-setup.log` 里，只是双击安装的人不会去看。
- [Server] **安装时自动生成自签名证书**（向导里勾一下，或 `TLSCERTIFICATEPATH=generate`）：按本机名字与
  地址生成，**私钥进本机证书存储**（`LocalMachine\My`——安装目录在 `Program Files` 下、本机所有用户可读，
  私钥不能放那儿），公钥导出到 `tls\signaling.cer` 供客户端信任；Kestrel 按 Subject/Store 引用它。
- [Server] **装完之后的配置路径**：安装目录里多了一个 `configure-server.ps1`（改设置并重启两个服务，
  与安装程序共用同一个配置步骤，支持 `-WhatIf` / `-NoRestart`）和一份 `如何配置.txt`。此前改配置只能手改
  `appsettings.json`，而服务端程序自带的 `--configure-system` 从未写进文档、等于不存在。

### Fixed
- [Server] `TLSCERTIFICATEPATH=auto`（安装包的默认值）以前会走到文件检查，报出"证书 'auto' 不存在"——
  读起来像用户填错了。现在它正确地等同于"不给证书"，并给出那条明文 http 的警告。
- [Server] **"自动生成自签名证书"那条路以前装出来的服务根本起不来**，现在真的能用了（已按端到端跑过：服务启动、
  TLS 1.3 握手成功、出示的证书与导出给客户端的 `tls\signaling.cer` 是同一张）。两个原因，症状一模一样，都是
  "证书明明在存储里，却报找不到"，而且都出现在同一句会误导人的错误里：
  其一，写进 Kestrel 的配置缺 `AllowInvalid`——它查证书时默认只认链验证通过的，而自签名证书在**任何**机器上
  都过不了（本机也没把它装进受信任根）；其二，`Subject` 写成了带 `CN=` 的可分辨名称，而 Kestrel 是按**简单名**
  查的，带前缀永远查不到。客户端信不信任是客户端的事，服务端得先能把证书拿出来。
  **注意**：这条路径要写本机证书存储，覆盖它的测试在未提权时会跳过，所以 CI 覆盖不到——它是靠一次真实的
  安装式配置加上一次真实的 TLS 连接验出来的。
- [Server] 安装包的 MSI 代码页设为 936：向导页是这个包里第一处**存在于 MSI 表内的中文**（此前的中文都在
  许可 RTF 文件里），默认的 1252 编码装不下它。

## [0.2.1] - 2026-10-11 · 连接可靠性

### Fixed
- [Transport] **同一台机器上的两台设备现在有一条可靠的路**：候选地址里**无条件**加上回环地址（排在最后）。
  此前回环只会在反射器答上来、且它报回来的恰好就是回环时才出现；于是"网卡地址不可用"（CI runner 上防火墙
  挡入站 UDP、虚拟网卡没有路由）叠加"反射器超时"，就只剩中继一条路——中继再失败就彻底连不上。2026-10-11 的
  发布流水线正是这么红的：一个只做连接的测试报了 `No network path to the remote device worked.`，
  本地连跑 30 次都复现不出来。
  已用实验证实（把网卡地址换成不可达、两端都不采用反射结果、中继也指向不可达）：关掉这条修复时 8 个测试
  失败 7 个，打开后 6 个通过——剩下 2 个是强制走中继的，实验里它们本来就该失败。
- [Transport] **连不上时说明试过什么**：`PeerConnectException` 的消息现在带上反射器是否答复、每个候选地址的
  失败原因（超时秒数或具体错误），以及中继是否被提供/是否认领了绑定。例如
  `No network path ... (the reflector did not report a public endpoint). Tried: direct 192.0.2.1:62572 did not
  answer within 2s; relay 192.0.2.9:5082 did not acknowledge the bind.`
  此前只有一句"没有可用的路径"，线上或 CI 里出现时无从判断是候选错了、反射器没答还是中继没理。
  单次候选尝试的日志同时从 Debug 提到 Information（连不上的第一手材料），界面不受影响——客户端按
  `Failure` 枚举显示自己的中文句子，不显示这句话。
- [Transport] 中继绑定（两步 UDP 握手，所有直连都失败后的最后一条路）失败后会**重试一次**：过去丢一个包
  就等于这条路不存在。重试只在已经失败的路径上多花一次超时。

### Changed
- [Build] **测试运行方式迁到 Microsoft.Testing.Platform**（`global.json` 里加 `test.runner`）：xunit.v3 4.x 带来的
  MTP 在 .NET 10 SDK 上**不再支持 VSTest 目标**，不迁的话 `dotnet test` 直接报错（那正是两个 xunit 依赖 PR
  一直红着的原因，不是升级本身有问题）。CI 的 TRX 改由 `Microsoft.Testing.Extensions.TrxReport` 产出
  （`--report-trx`），文件名与格式没变，测试汇总表照旧解析（已用真实 TRX 验过）。
  两个安装包脚本改用 `--project` + `--filter-class <完全限定类名>`：新的 `dotnet test` 不认位置参数给的目录，
  也不认 `--nologo`（会被转发给测试程序，后者以退出码 5 拒绝整次运行）。
- [Build] xunit.v3 3.2.2 → 4.0.2、xunit.runner.visualstudio 3.1.5 → 4.0.1。迁移之外，测试代码一行没改。

### Added
- [Build] `Microsoft.Testing.Extensions.TrxReport` 2.5.1：TRX 报告是 MTP 的扩展而不是平台本身，没有它测试程序
  会拒绝 `--report-trx`（退出码 5），而 CI 的测试汇总正是从 TRX 里读的。

## [0.2.0] - 2026-10-11 · 公网发布准备

### Security
- [Client] **修复提权命令注入**：修改服务器地址不再经过 PowerShell，改为把参数作为独立 argv 交给服务程序
  （`HostServiceSetup`），地址先经严格解析。此前一个伪装成服务器地址的字符串能以管理员权限执行。
- [Protocol] **密码证明改用 PBKDF2 派生密钥**（60 万轮，nonce 作盐），**临时密码从 8 位加长到 12 位**
  （约 2^40 → 2^60）。两者合起来让"拿到证明后离线猜口令"从几天变成不现实；这仍不是 PAKE，
  固定密码仍需要它，见 `docs/security.md`。协议主版本随之升到 2，新旧版本会明确报"版本不兼容"。
- [Server] **注册与连接请求限流、连接配额**：单地址注册速度、单设备连接请求速度、挂起请求上限、
  同时连接数与单地址连接数（超出的在 WebSocket 升级前拒绝）。身份是这台服务器上所有限额的计量单位，
  所以注册限流是让其余限额真正有效的那一条。
- [Server] **中继绑定需要令牌**：中继先向来源地址发一个与票据、角色、地址、时间窗绑定的令牌，带回来才生效。
  这堵住了"拿票据把中继指向别人地址"的隐藏来源发包路径；另加中继会话数上限与角色迁移日志。
- [Server] **`X-Forwarded-For` 支持**（`Rendezvous:TrustedProxies`）：反向代理后按真实客户端地址限流，
  不配则只信本机回环。
- [Host] **固定密码下限由服务端强制**（此前只在界面上检查），**同时会话数上限 16**，
  **临时密码 30 分钟后自动更换**（固定密码不过期）。
- [Platform] 密钥文件改为原子写入：写坏不再让设备静默变成"另一台设备"。
- [Host] **配置读不出来时说明原因**：服务把原因写进 `logs\startup-error.log` 并以退出码 3 结束（此前是启动前的
  未处理异常——服务"就是不在了"，事件日志里只有运行时的一条记录）；客户端写进 `crash.log` 并弹出对话框，
  不再是一个不声不响没启动的程序。
- [Host] **文件传输残片会自动清理**：被控端把未完成的 `.rdpart` 记进清单，启动时删掉超过 7 天没人续传的。
- [Transport] 流尺寸上限收紧到 8K（此前 16384×16384 可让控制端单次分配 1 GiB）；
  信令给的设备号不合法时不再产生无人观察的异常。

### Added
- [Server] **`/metrics`**（Prometheus 文本格式，默认关闭，`Rendezvous:MetricsEnabled`）。
- [Server] `/health` 现在真的检查数据库（配置了 PostgreSQL 时）。
- [Build] 打包脚本在许可文本仍含占位符时**拒绝出包**（`-AllowDraftLicense` 可显式跳过）。这道检查现在放在最前面
  （和证书路径检查在一起），所以标签触发的流水线在第一秒就停下，而不是白跑几分钟发布之后才失败。
- [Build] **内网 TLS 的证书路子**：`build/signing/New-ServerCertificate.ps1` 生成服务器证书（名字与地址都进
  SAN），`build-installer.ps1 -ServerCertificate` 把它的公钥部分打进安装包并装进受信任根，于是内网也能跑
  `wss://` 而不需要公网 CA。已装好的机器可用 `Import-Certificate` 单独补。
- [Server] **服务端安装包**（Windows）：`build/installer/server/windows/build-server-installer.ps1` 产出
  `RemoteDesktop-Server-<版本>-x64.msi`，把信令与中继两个服务器装到 `%ProgramFiles%\Remote Desktop Server`，
  注册成两个自动启动、崩溃后重启的服务，加三条只针对这两个程序的入站防火墙规则（可用 `NOFIREWALL=1` 关掉），
  并写好两份 appsettings.json。改用安装包后不再需要手工 `dotnet publish` + `sc.exe create` + `netsh`。
  安装时可传 `RENDEZVOUSURL` / `RELAYPUBLICENDPOINT` / `RELAYSHAREDKEY` / `POSTGRESCONNECTIONSTRING` /
  `TLSCERTIFICATEPATH` / `TLSCERTIFICATEPASSWORD`；中继密钥默认在安装时生成，重装或升级不会更换。
  安装包**不装数据库**：没有 PostgreSQL 时设备代码只在内存里，服务器一重启就全变——这一点在安装输出和
  文档里都写明。配置步骤把做了什么写在 `server-setup.log` 里，因为安装程序不会给任何人看它的输出。
- [Server] `Rendezvous:RelayEndpoint` 那条报错信息原来写着"must look like host:port"，而它其实只接受 IP
  （客户端拿到就直接发 UDP）。报错信息改对了，部署文档和 compose 里那个会**让容器起不来**的示例
  （`rd.example.com:5082`）也换成了 IP。
- [Docs] `THIRD-PARTY-NOTICES.md` 与生成脚本 `build/Generate-ThirdPartyNotices.ps1`。
- [CI] 依赖漏洞审计（含传递依赖）、Dependabot、发布前跑测试。
- [CI] 打 Tag 出的 Release 现在**同时带客户端与服务端两个安装包**（`RemoteDesktop-Server-Windows-x64-<tag>.msi`），
  不再只发客户端——服务端安装包此前只能在本地构建。
- [Docs] 新增系统架构设计文档 (`docs/architecture.md`)。
- [Docs] 新增网络通信协议规范文档 (`docs/protocol.md`)。
- [Docs] 新增文档中心索引与贡献指南 (`CONTRIBUTING.md`)。
- [Docs] 部署文档新增"生产环境：信令必须走 TLS"（自带证书或反向代理两种做法）与"公网上的限额"两节。
- [Installer] 引入基于 WiX v5 的 Windows 安装包打包脚本。

### Changed
- [Build] **许可文本从草案变成正式文本**：著作权人 Zeqian GAO、项目主页为联系方式、适用中国法律、
  争议提交作者住所地有管辖权的人民法院；开头那段"本文件是草案、请替换方括号"的说明也删掉了——它原本会被
  转成 RTF **印在每个用户的许可页上**。打包脚本"有占位符就拒绝出包"的闸门保留，留给将来换主体发布时用。
- [CI] **发布时不再要求必须配置服务器地址**：没配就出一个不内置地址的客户端包（默认连本机，装完在客户端里
  用「修改服务器地址」改），并在 Release 说明最前面写明这一版是哪种。本软件公网和内网都要用，地址往往是
  部署时才定的，"不内置"是正常选择而不是漏配；客户端连不上时界面本来就会显示当前地址和怎么改。
- [Build] **构建目录按"角色→平台"重组**，为后面 macOS / Linux 的安装包让路。此前 `installer/windows/` 是按平台
  命名的（装的却是客户端/被控端那一套），`installer/server/` 是按角色命名的——两个轴混在一起，第二个平台一到
  就说不清"macOS 的客户端包放哪儿"。现在：
  `installer/client/windows/`、`installer/server/windows/`；许可文本与它的 RTF 生成脚本提到 `installer/`
  共用（服务端包不再伸手进客户端包拿 EULA）；`service/windows/`（sc.exe 那套只在 Windows 有意义）；
  `signing/windows/`（`New-SelfSignedCertificate` 与证书存储都是 Windows 专有），跨平台的
  `New-ServerCertificate.ps1` 留在 `signing/` 根下——它用纯 .NET API，在 Linux/macOS 的 pwsh 上同样能跑，
  服务端将来在那边部署时 Kestrel 用的就是它生成的证书。
- [Build] `Generate-ThirdPartyNotices.ps1` 改为向 SDK 询问 NuGet 包目录（`dotnet nuget locals global-packages`），
  不再用 `$env:USERPROFILE` 拼路径——那个变量在 Linux/macOS 上不存在，会让整张许可表退化成"见项目主页"。
  顺带跟随 `NUGET_PACKAGES`（CI 用它挪缓存）。
- [Build] Docker Compose 不再内置数据库口令（未设置即拒绝启动），加资源上限与 `no-new-privileges`。
- [Build] 安装包摘要不再声称"中英双语界面"（界面目前只有中文）。
- [Client] 侧栏版本号改为读程序集版本，不再硬编码在界面文件里。
- [Client] "数据与诊断"新增**本机控制端代码**：这台机器连出去时用的身份（和主页显示的被控端代码不是同一个），
  别人控制本机时他那边的会话里显示的就是它，用来回答"谁在连我"。
- [Repo] 优化项目文档组织结构，统一采用 Kebab-case 命名并提供全局导航。

### Fixed
- [Build] **Windows PowerShell 5.1 下构建脚本里的中文是乱码**：含中文的 `.ps1` 是**无 BOM** 的 UTF-8，而 5.1 会按
  GBK 解码——提示信息变乱码是轻的，重则脚本压根解析不了（`TerminatorExpectedAtEndOfString`）；更要紧的是
  许可占位符那个正则 `'【[^】]+】'` 会**静默匹配不到任何东西**，闸门等于不存在（脚本注释里却写着"用 Windows
  自带的 PowerShell 跑发布构建也要能work"）。`build/` 下 10 个脚本都补上了 UTF-8 BOM，5.1 与 pwsh 7 现在都正常。
- [Installer] **客户端安装包现在真的配置了服务崩溃重启**：`ServiceInstall` 本身不含失败动作，此前只有按
  deployment.md 用 `sc.exe failure` 手工安装那条路才有——而 README 已经把它当成安装包的行为写着了。
  现在安装即生效：崩溃后 5 秒重启，连续三次后不再自动重启，稳定运行一天后重新计数。README 也改成与代码一致。
  包内容测试会检查这一行，缺了就构建失败。

---

## [M7] - 远程摄像头支持
### Added
- [Feature] 完整支持远程摄像头串流 (`Features.Camera`)。
- [Feature] 支持被控端指示灯激活与 UI 强制关闭控制。
- [Platform] 基于 Media Foundation 实现 Windows 下的摄像头采集与 NV12 转换。

## [M6] - 远程终端 (Terminal)
### Added
- [Feature] 支持基于 ConPTY 的远程终端，提供完整的 VT 序列解析。
- [UI] 终端窗口支持多标签页，并增加鼠标上报与剪贴板支持。

## [M5] - 文件传输 (File Transfer)
### Added
- [Feature] 双栏文件管理器，支持拖拽、断点续传（基于 `.rdpart` 和 xxHash64 校验）。
- [Security] 文件传输强制以被控端当前登录用户身份 (Agent) 执行，防范 SYSTEM 权限越界。

## [M1 ~ M4] - 核心功能迭代
- **M4**: 完成观看模式、剪贴板同步、双向音频转发、多显示器切换。
- **M3**: 完成 HostService 守护进程化，支持 UAC 和 Windows 登录界面突破（Session Agent 隔离）。
- **M2**: 完成远程控制 MVP，基于 DXGI 和 SendInput 实现极低延迟桌面协同。
- **M1**: 确立网络底座，实现 QUIC 证书指纹校验、Rendezvous 信令交换、UDP 打洞与 Relay 回退机制。
