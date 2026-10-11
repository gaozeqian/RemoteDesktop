# 更新日志 (Changelog)

本项目所有的显著变更都会记录在此文件中。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)。

## [Unreleased] - 公网发布准备

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
- [Build] 打包脚本在许可文本仍含占位符时**拒绝出包**（`-AllowDraftLicense` 可显式跳过）。
- [Build] **内网 TLS 的证书路子**：`build/signing/New-ServerCertificate.ps1` 生成服务器证书（名字与地址都进
  SAN），`build-installer.ps1 -ServerCertificate` 把它的公钥部分打进安装包并装进受信任根，于是内网也能跑
  `wss://` 而不需要公网 CA。已装好的机器可用 `Import-Certificate` 单独补。
- [Server] **服务端安装包**（Windows）：`build/installer/server/build-server-installer.ps1` 产出
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
- [CI] 依赖漏洞审计（含传递依赖）、Dependabot、发布前跑测试、未配置服务器地址则拒绝发布。
- [Docs] 新增系统架构设计文档 (`docs/architecture.md`)。
- [Docs] 新增网络通信协议规范文档 (`docs/protocol.md`)。
- [Docs] 新增文档中心索引与贡献指南 (`CONTRIBUTING.md`)。
- [Docs] 部署文档新增"生产环境：信令必须走 TLS"（自带证书或反向代理两种做法）与"公网上的限额"两节。
- [Installer] 引入基于 WiX v5 的 Windows 安装包打包脚本。

### Changed
- [Build] Docker Compose 不再内置数据库口令（未设置即拒绝启动），加资源上限与 `no-new-privileges`。
- [Build] 安装包摘要不再声称"中英双语界面"（界面目前只有中文）。
- [Client] 侧栏版本号改为读程序集版本，不再硬编码在界面文件里。
- [Client] "数据与诊断"新增**本机控制端代码**：这台机器连出去时用的身份（和主页显示的被控端代码不是同一个），
  别人控制本机时他那边的会话里显示的就是它，用来回答"谁在连我"。
- [Repo] 优化项目文档组织结构，统一采用 Kebab-case 命名并提供全局导航。

### Fixed
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
