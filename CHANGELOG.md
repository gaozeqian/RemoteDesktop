# 更新日志 (Changelog)

本项目所有的显著变更都会记录在此文件中。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)。

## [未发布]

### Fixed

- **Windows 被控端防休眠**：`SetThreadExecutionState` 的调用现在固定在一条专用线程上。此前会话开始与结束时
  常落在不同线程池线程，`Release` 清不掉 `Acquire` 设下的状态，机器在第一次会话后不再自动睡眠。
- **并发握手不再“Accepted 后断开”**：会话上限在发送 `AuthResult` 之前预留名额，超限的控制端在握手内收到
  `TOO_MANY_SESSIONS`，而不是先被接受再看到连接关闭。
- **全局拒绝摄像头会停止现有流**：本机设置“拒绝”后，运行中的会话立刻失去 `Camera` 权限并关闭摄像头通道；
  重新允许只影响新会话。
- **异常断线不再中断特性循环与关闭链**：QUIC 的 `QuicException` 在传输层翻译为 `IOException`；关闭远程桌面窗口
  时每一步都会执行，即使剪贴板/音频/输入的释放失败，QUIC 会话也会被释放。
- **上传失败会回执**：被控端在接收阶段遇到 IO/协议错误时发送 `Complete{FileError.Io}`，控制端不再挂到
  finish 超时（最长 1 小时）。
- **主机控制管道错误语义**：未知命令、无效的会话 id、非法的摄像头参数返回 `error`，不再静默回答 `ok`。
- **设备注册表缓存有界**：超过上限时整体丢弃并回读数据库，长期运行的服务不再随累计设备数增长内存。
- **Ops 摄取校验**：签名批次中的 `null` 元素返回 400 而不是 500；事件 id/node/kind/correlation id 有长度上限；
  SQLite schema 迁移在 `BEGIN IMMEDIATE` 下读取版本并应用，两个进程同时启动不再可能重复应用同一迁移。
- **SRP 挑战单次使用**：`HostPasswordChallenge` 增加消费标志，同一个挑战第二次 `VerifyHost` 直接拒绝，兑现文档里的
  “exactly one call”。
- **macOS 编码失败与句柄**：VideoToolbox 编码失败保留原生错误文本；解码器工厂在 `Open` 失败时释放已构造的解码器
  （包括其 GCHandle）。
- **采集故障可观测**：ScreenCaptureKit 的启动等待现在受 15 秒上限约束；原生层暴露“流被系统停止”的状态，
  采集器的 `IsFaulted` 据此返回；GDI 回退在 `BitBlt` 失败时也报告故障，恢复逻辑不再把死流当成静态桌面。
- **信令会话目录清理有界**：`SessionDirectory.Register` 不再每次扫描全部 7 天会话；每次注册只检查固定 256 条，
  存活的轮转到队尾，清理成本与表大小无关。`LastReport` 改用 ticks 原子读写，多线程上报不再可能读到撕裂值。
- **`/health` 不再按请求打库**：控制台把数据库探活结果缓存 2 秒，并发探活共享同一次查询；数据库恢复后
  最多 2 秒反映，未认证请求无法再按请求驱动数据库连接。
- **控制台登录挑战上限差一**：`ChallengeStore` 在已有 10,000 条时创建第 10,001 条，要等下一次调用才裁剪；现在
  插入后立即裁剪，并保证刚创建、即将返回给调用方的挑战不会被裁掉。
- **保留清理可索引**：会话与已解决告警的删除谓词改成能分别命中索引的 OR 形式，迁移 9 为
  `(state, resolved_at)` 增加索引；通知尝试的删除与告警删除受同一个批次上限约束；滚动桶删除通过
  `step_seconds` 等值加 `bucket` 范围走 `(step_seconds, bucket)` 索引，不再每批全表扫描。
- **Linux 门户剪贴板不跟随外部改动**：GNOME 不会为其他程序取得选区发送 `SelectionOwnerChanged`，
  被控端只监听信号时收不到真实的双机剪贴板。`WaylandClipboard` 增加 1 秒轮询作为回退，按内容哈希去重，
  并忽略门户重复播报的同格式信号；双机两方向的手工复测与真实门户回归测试已覆盖。

### Changed

- **Linux NVENC 探测**：只检查 `h264_nvenc` 是否存在，不再要求 NVIDIA 的 DRM render node；无 `/dev/dri` 的
  headless/容器机器也能尝试 NVENC，打开失败仍由选择器回退。
- **音频帧上限**：控制端音频通道以 64 KiB 为单帧上限，而不是默认的 16 MiB。

### Build

- 新增 `build/tools/Update-LockFiles.ps1`（`-Check` 只验证），CI 的 `lock-files` job 对 `win-x64`、
  `linux-x64`、`osx-arm64` 三个 RID 检查锁文件，不再等到打包阶段才发现漂移。
- **发布可重入**：Release 已存在时用 `gh release upload --clobber` 补资产并更新说明，不再因
  `gh release create` 拒绝已存在的 Release 而中断；上传后回读资产列表，按精确的九个（或六个）角色名校验。
- **CI 成本**：漏洞审计只在 Windows job 跑一次；setup-dotnet 启用 NuGet 缓存；PR 上的旧运行会被 concurrency
  取消；第三方声明生成器的失败不再被后续 `git diff` 掩盖。
- **打包版本来源**：WiX SDK 固定到 `global.json` 的 `msbuild-sdks`，安装器包版本集中到
  `build/installer/windows/Directory.Packages.props`；`Set-ServerConfiguration.ps1` 改为 UTF-8 带 BOM，
  Windows PowerShell 5.1 读取中文提示不再乱码。
- **Ops 指标降采样增量执行**：每轮从最新桶继续（并重算该桶），不再把 30 天历史全部重算并逐行重写；首次运行
  只回看 24 小时，迁移 8 增加 `(ts)` 与 `(step_seconds, bucket)` 索引。
- **删除孤立 E2E 脚本**：`build/tools/run-e2e-automated-tests.sh` 没有任何调用方，且依赖 Linux 的
  `/etc/os-release`；同类场景已由 `E2E.IntegrationTests` 覆盖。
- **锁文件同步**：项目依赖版本范围更新到 1.0.1，并为新组合项目补齐四个 `packages*.lock.json`。
- **告警评估租约续期**：一次 pass 时间较长时按租期一半续租；租约被另一个副本拿走时取消本次 pass，避免同一批
  通知被两个实例各发一次。
- **组合根收敛**：新增 `RemoteDesktop.Hosts.Composition`，客户端独立托管、HostService 的 Linux/macOS 分支与
  SessionAgent 都调用同一个 `AddRemoteDesktopHostFeatures(dataDirectory)`，新增特性只改一处。
- **`ServerSetup` 按职责拆分**：原来的 1,485 行单文件拆成 `Database`、`Tls`、`OpsEvents`、`Gateway`、`Ops`、
  `Io` 六个 partial，`Configure` 仍只负责编排；只是移动代码，行为不变。
- **测试时钟与通道收敛**：共享 `TestClock` 与 `TestChannel`，删除 4 份手写时钟和重复的流通道桩；
  定时器型时钟与带行为断言的通道保留。
- **`MainWindowViewModel` 拆分**：约 1,400 行的单文件拆成 `.Connection`、`.Settings`、`.RecentDevices`、
  `.HostStatus` 四个 partial，方法体未改。
- **服务端宿主收敛**：新增 `RemoteDesktop.Server.Hosting`，四个宿主共用配置分层、Windows 服务/systemd 注册与
  节点令牌端点过滤器；`Server.Shared` 继续保持不含 ASP.NET，`Ops.Core` 的依赖闭包不变。Hosting 使用类库 SDK +
  ASP.NET 框架引用，避免 Web SDK 把锁文件当作发布内容而与宿主重名（验收中发现的发布失败已修复）。
- **平台实现继续收窄**：Windows/macOS/Linux/Posix 的六十余个只被测试或同程序集使用的实现类型改为 `internal`；
  宿主真正使用的 setup、self-check 与控制管道 API 仍为 public。
- **测试脚手架收敛**：E2E 与 Transport 集成测试共用 `TestSupport`（节点宿主组合、在线等待、`TestRendezvous`
  与 `TestWait`）；约 20 份 `WaitForAsync`/`WaitUntilAsync` 改为同一实现，超时抛 `TimeoutException`，不再静默返回。
- **文档规范收敛**：`CLAUDE.md` 改为最短上手与易踩坑清单，规范、索引与细节分别指向 `CONTRIBUTING.md` 与 `docs/`；
  `docs/ops` 中与 `build/README.md` 重复的打包脚本分层段落改为链接。
- **观看端时钟统一**：解码器监管与关键帧看门狗改用注入的 `TimeProvider`（生产仍是系统单调时钟）；停滞判定不再
  需要测试真实等待两秒，观看端测试从约 32 秒缩短到数秒。
- **平台实现收窄与坐标纯函数**：`SendInputInjector`、`MediaFoundationH264EncoderFactory`、`PipeWireScreenCapturer`、
  `PortalConnection` 改为 `internal`；显示选择与归一化坐标到虚拟桌面的映射抽成 `SendInputCoordinates` 纯函数，
  并补齐边界/回退单测。
- **平台能力接口**：新增 `IHostInstallation` 与 `IPlatformIdentity`，由各平台层注册；客户端不再用
  `OperatingSystem.Is*` 判断宿主安装形态、摄像头授权或遥测平台。摄像头授权节只在注册了 `ICameraAccess` 时显示；
  Windows 服务名、Linux unit 与 macOS 宿主路径常量从 `HostControlProtocol` 移到平台层，遥测平台名缺失时
  报告 `unknown` 而不是默认成 Linux。

## [1.0.1] - 2026-11-13 · 安装与发布流水线：三平台 CI 全绿、macOS 服务端安装修复

### Security

**上线前加固**

- **密码认证迁移到 SRP-6a**：替换原来的挑战—HMAC 证明，使用 BouncyCastle 的 RFC 5054 3072 位群实现，双方证书
  指纹绑定到 SRP identity。被动监听者不再能拿到可离线爆破的 verifier，被控端返回服务器证据（M2）实现双向认证。
  协议主版本升到 3，旧客户端会明确报告版本不兼容，不会降级回弱交换。
- **信令默认只监听回环**：服务端和安装器默认 `http://127.0.0.1:5080`。非回环明文 `http://` / `ws://` 必须显式
  设置 `Rendezvous:AllowInsecureSignaling` / `Transport:AllowInsecureSignaling`，否则启动时拒绝。
- **取消全局认证失败锁**：只锁定实际失败的控制端设备，避免攻击者轮换设备身份后无需密码即可阻断所有合法控制端。
- **文件沙盒改为打开句柄后复核真实目标**：下载和上传不再只依赖 `Normalize` 的路径字符串；文件打开后按平台查询
  句柄最终路径，确认仍在共享根目录内，并复用同一个句柄读写，关闭链接替换的检查/使用竞态。上传完成后的落盘也
  通过已校验的目标句柄复制，不再用可被链接重定向的路径重命名。
- **中继无效票据日志限流**：公网 UDP 上每个无效票据不再写一条 Warning；改为每分钟汇总一次并记录被抑制的数量。
- **Windows 本机控制管道完成双向端点与调用者授权**：UI 验证真实服务进程；服务只接受管理员或当前 Agent 用户会话，
  其他本机账户不能读取密码或修改固定密码。
- **安装输入与凭据文件收紧**：macOS 面板答案校验属主/链接并原子暂存，PFX 在 Unix 固定 0600，OpenSSL 密码不再进入 argv。
- **管理断开需要主机确认**：Rendezvous/Ops 不再把 WebSocket 发送尝试当作已执行；遥测与结束报告校验会话参与方。
- **依赖与发布可复现**：NuGet 使用锁文件和 locked restore，Actions/容器/原生库按内容固定，正式 Release 强制签名与公证。

**macOS 服务端不再以 root 运行**

- **四个守护进程改为专用账号 `_remotedesktop`**：plist 增加 `UserName`/`GroupName`；postinstall 创建隐藏账号
  （UID/GID 从 300 起找空位、`/usr/bin/false`、无自己的家目录），把 `var/`（数据库与 DataProtection 密钥环）
  与 `logs/` 交给它，并把 `appsettings.json`、`tls/` 收紧为 `root:_remotedesktop` 的 0640/0750——此前私钥是
  0600 root，除了 root 谁也读不到，降权也无从谈起。启动前会以该账号实测读设置、读私钥、写两个状态目录；
  任何一项不通过都会使安装失败并停止服务启动，不会退回 root 运行。自定义数据库目录也必须通过实际写入检查。
- **`configure-server.sh` 重配后重新应用同一组权限**：`--configure-system` 以 root 写出的 0640 `root:wheel`
  会让服务读不到刚写的设置并进入重启循环。
- **控制台的 DataProtection 密钥环有了可写的 HOME**：LaunchDaemon 的环境里没有 HOME，密钥环无处落地会让每次
  重启都让所有人重新登录；Ops 的 plist 把 `HOME` 指向 `var/`。
- **首个管理员改用服务账号创建**：`sudo -u _remotedesktop ... --create-admin`；用 root 建会让 SQLite 的
  `-wal`/`-shm` 归 root，监控台之后写不进去。macOS 的卸载不清除 `_remotedesktop` 账号，与 Linux 一致。

**节点令牌的出站白名单**

- **管理员可以把节点指向任意地址，令牌会跟着去**：新增 `Ops:NodeTokenHosts`（裸主机名或 IP）。填了之后，
  节点令牌只能发往列表内的主机，回环始终允许（安装包登记的本机节点不受影响）；列表外的地址在登记、改地址、
  抓取、设备代理与管理动作上都直接拒绝，错误信息点名这个键。不填时行为与之前一致。
- **四处拒绝共用同一句原因**：`NodeHttp.RefusalReason` 统一"明文 HTTP"与"主机不在白名单"两种拒绝的措辞，
  抓取失败、节点详情、设备代理与动作失败不再各写一份。

**文件传输沙箱与信令入口的加固**

- **`\服务器\共享\…` 在拒绝之前就被探测**：被控端的文件传输沙箱先逐段检查路径是否存在，之后才拒绝网络共享。Windows 上控制端发来 UNC 路径时，这一步已经向对方服务器发起 SMB 连接并做 NetNTLM 认证，当前用户的哈希在被拒绝之前就泄露了。现在 UNC 路径仅凭字符串就会被拒绝（除非已配置同一 `\服务器\共享` 的根目录），指向网络共享的链接目标同样不再被逐段探测。
- **信令 `Urls` 的明文检查与 Kestrel 的解析对齐**：`http://*:5080`、`http://+:5080` 这类通配写法以前会被报成"不是合法地址"，分号分隔的多个地址也会让安装器把 `AllowInsecureSignaling` 写成 `false`，升级后原本能用的部署起不来。现在通配主机按"所有网卡"处理，列表里任何一个公网明文地址都需要（也会被记录为）显式放行。
- **每地址连接数限制按 IPv6 /64 归并**：以前每个 IPv6 地址单独计数，同一个 /64 内的一台机器可以用无数个地址绕开每地址上限，把全局连接数占满；IPv4 映射地址也被当成另一个来源。现在映射地址按 IPv4 计，IPv6 按 /64 计。
- **监控台的 CSRF 令牌绑定到会话**：令牌改为 HMAC-SHA256（会话令牌，固定标签），请求头必须等于当前会话对应的值（且仍与 Cookie 一致）。此前服务端只比较 Cookie 与请求头是否相等，能写 Cookie 的同站其它子域可以自己编一对通过校验。登出、过期、重新登录后旧令牌立即失效；无需数据库迁移、无新密钥、前端不用改。升级时已登录的浏览器需要重新登录一次。

### Added

**Windows 服务端安装包：向导拆成三页，第一页能填监听地址**

- **原来的单页「服务器设置」拆成三页**，按"回答的是哪个问题"分：**服务器地址**（信令监听地址、中继公网地址、
  TLS 三选一，370×330）、**证书**（证书域名、`.pfx` 路径与浏览、口令，370×270）、**数据库**（PostgreSQL 连接串，
  370×250）。原来那一页已经长到 496 高、十来个字段挤在一起。`Back` 逐页回退，确认页的 `Back` 回到最后一页；
  证书确认框与"还没选证书"框回到证书页，地址错误框回到第一页。
- **第一页第一行是信令监听地址**（`RENDEZVOUSURL`，带出构建时烤进去的值），与客户端连接页的地址框对齐：改别的机器
  能连的地址不必再回到命令行。地址在离开这一页时校验——必须是以 `http://` / `https://` 开头的绝对地址、不能带用户名
  密码，**且填 `https://` 就必须在同一页启用 TLS**（自动生成或指定证书）；不满足时弹一个说明原因的对话框，留在本页
  而不是装出一个起不来的服务。检查是自定义动作 `ValidateServerUrl`（immediate，`PageActions`），跳转以
  `SERVERURLERROR` 为条件。
- **数据库页的"留空"不再自相矛盾**：属性表里的默认值是 `auto`（没指定，由 `DATABASEPROVIDER=auto` 决定：以前配过
  PostgreSQL 就沿用，否则建本机 SQLite；空的 `""` 是"不用数据库"，设备码只存内存，两者不能混）。拆页后
  `NormalizeSettings` 每页都跑，会把 `POSTGRESCONNECTIONSTRING` 提前写成 `auto`，于是数据库页的输入框一打开就显示
  `auto`，而标签写着"留空 = 本机 SQLite"。现在整理动作拆成两份：前两页用 `RestoreEmptyBoxes`（只补空格、裁口令，
  不动数据库框），最后一页才用 `NormalizeSettings` 把空值写成 `auto`。回退会重建页面，被清空的属性仍由前者补回。
  页面说明与运维文档也写清了 `auto` 的含义。
- 包内容测试跟着拆：三个页面各自的可输入属性、页面流向与回退、校验动作的时机与门控，`RestoreEmptyBoxes` /
  `NormalizeSettings` 各在哪一页跑，布局检查（控件越界/重叠）也扩到六张对话框。

**每节点令牌**

- **控制台可以给单个节点配自己的令牌**：`POST /api/nodes/{id}/token`（管理员，写审计）生成或接受一个令牌并
  只返回这一次，`DELETE` 清除并退回部署级 `Ops:NodeToken`。节点详情里多了「生成独立令牌 / 重置 / 清除」。
  节点自己的令牌优先，没设的节点继续用部署级令牌——升级不改变任何现有行为。
- **抓取、设备代理、管理动作与节点详情都按节点的令牌发请求**：此前四处各写一遍 `options.NodeToken`，现在统一
  走 `NodeHttp.TokenFor` / `ApplyAuthorization`，出口规则（`Ops:NodeTokenHosts`、https 或回环）对每一条令牌
  一视同仁。
- **节点 API 只说有没有令牌，不说是什么**：`hasOwnToken` 是响应里唯一的令牌相关字段；令牌明文存在控制台库里，
  与 `Ops:NodeToken` 存在配置文件同理。
- **schema v7**：`ops_node` 增加 `token` 列，旧行是 `NULL`，即继续使用部署级令牌。

**告警的邮件通知与静默**

- **邮件通知**：新增 `Ops:Notifications:Smtp:*`，在 Webhook 之外多一条通道。SMTP 客户端是自己写的——STARTTLS、
  AUTH PLAIN/LOGIN、DATA 的点转义、非 ASCII 主题的 RFC 2047——没有引入新依赖。口令只写进 AUTH 交换，不进日志、
  审计与投递记录。
- **两条通道各自重试**：`ops_notification_attempt` 按通道分行，Webhook 挂着不会让邮件重发，反之亦然；退避与
  去重沿用原有规则。一条通道的失败不会阻塞另一条。
- **静默**：告警页可直接「静默 1 小时」，`GET/POST/DELETE /api/silences` 支持按规则、按节点或两者组合。静默
  只压通知：告警照常打开、恢复、写审计；被压掉的转换不会在窗口结束后补发。创建与解除都是管理员动作，各写审计。
- **schema v6**：`ops_silence` 表从 1.0.0 起就在基线里（matcher_json / starts_at / ends_at / created_by），本次
  只补 `reason` 列，因此是 `ALTER TABLE` 而不是建表。

**`remotedesktop://` 链接带入设备代码**

- **链接只带设备代码，不自动连接**：`remotedesktop://connect?code=902226105` 与 `remotedesktop://902226105`
  两种写法，容忍空格、连字符与百分号编码。链接来自浏览器、聊天消息或邮件，所以密码由人输入、"连接"由人点；
  解析器里没有存放密码的位置，`?password=` 之类参数被忽略。
- **第二次启动把链接交给已打开的窗口**：控制端身份在信令里每设备只保留一个会话，开出第二个窗口会让两个进程
  每秒互踢。第一个客户端占一条每用户命名管道，带链接的启动把 URL 写进去后退出；不带链接的启动行为不变，
  只是不会拿到那条管道。
- **Windows 与 Linux 注册协议**：MSI 在 `HKLM\Software\Classes\remotedesktop` 写下 `URL Protocol` 与
  `shell\open\command`（卸载即删，包内容测试会打开 MSI 核对这两行），`.desktop` 增加
  `MimeType=x-scheme-handler/remotedesktop` 与 `%u`。**macOS 未做**：LaunchServices 走 Apple Event
  （`kAEGetURL`），原生辅助库还没有这条通道。

**Docker Ops HTTPS 网关**

- **Docker Ops 访问方式与安装包对齐**：新增 `Dockerfile.gateway` 与 ops overlay 的 `gateway` 服务，默认监听
  `https://0.0.0.0:5443` 并转发到内部 `http://ops:5090`；Ops 不再发布宿主机 5090，只有 Gateway 的 5443 对外。
- **IP 自签名证书**：Gateway 首次启动按 `OPS_GATEWAY_IP` 生成包含 IP SAN 的证书，持久化在
  `ops-gateway-data` 卷；`OPS_GATEWAY_DNS` 可追加 DNS 名称，公钥可用 `docker compose cp` 导出。
- **精确可信代理范围**：Ops 与 Gateway 加入专用的 `ops-gateway` 网络，Ops 只信任该子网的
  `X-Forwarded-For` / `X-Forwarded-Proto`，不再信任整个 Docker bridge 网段。
- **文档与配置**：`.env.example` 增加 Gateway 地址、端口、绑定接口和子网；Docker 文档补充证书导出、指纹校验、
  续签与防火墙说明。

**运维监控台评审修复**

- **账号管理页与命令行**：管理员可在控制台新建 viewer/operator/admin、改口令、重置 TOTP、停用/启用和
  删除账号；每种操作立即撤销该账号的全部会话，最后一个可用管理员不能被停用或删除，管理员也不能停用或
  删除自己。控制台进不去时用 `--reset-password` / `--reset-totp`。所有变更写审计。
- **节点地址可编辑**：节点管理页新增「改地址」，`PATCH /api/nodes/{id}` 接受 `baseUrl`；新增、改地址、启停、
  删除都写审计。配置了 `Ops:NodeToken` 时，非回环的 http 节点会被拒绝，避免 token 明文上线。
- **通知重试**：Webhook 失败后按 1/2/4/8 分钟退避重试，最多 5 次；重试只在告警仍处于对应状态时进行。
- **数据保留**：新增每小时清理任务，默认删除 30 天指标样本、30 天事件、90 天会话、180 天已恢复告警和过期
  管理员会话；`Ops:Retention:*` 可调，打开中的告警与审计哈希链不删。
- **反向代理支持**：新增 `Ops:TrustedProxies`，只采信指定代理的 `X-Forwarded-For` / `X-Forwarded-Proto`；
  `/health` 拒绝任何带转发头的请求，避免被同机代理转发出去。
- **后台任务租约与动作恢复**：抓取、告警求值、清理和动作恢复通过 `ops_lease` 只让一个实例工作；处于
  `approved` 超过 `Ops:ActionStaleSeconds` 的动作由恢复任务标记失败并写审计，进程崩溃不再留下永久卡住的动作。

**Linux 安装包按部署配置**

- **服务端 .deb 安装时选择数据库与 TLS**：debconf 询问 SQLite / PostgreSQL（含连接串）/ 仅内存，以及
  自签证书 / 已有证书（PKCS#12 或 PEM + `.key`）/ 明文 HTTP；`generate` 还可以填写客户端将使用的 DNS
  名，写进自签证书的 SAN。Ops 控制台跟随同一 provider；`none` 时仍保留自己的 SQLite 库。答案也可以
  通过 `debconf-set-selections` 预置或 `dpkg-reconfigure remotedesktop-server` 修改。
- **客户端与被控端 .deb 安装时填写服务器地址并导入证书**：`wss://` 且服务端使用自签证书时，安装过程
  接受 PEM 证书路径并写入系统信任库；`ws://` 不需要证书。构包期的 `RD_RENDEZVOUS_URI` /
  `RD_SERVER_CERTIFICATE` 仍可作为站点默认值。
- **host .deb 安装时设置无人值守密码**：debconf 新增 `remotedesktop-host/unattended-password`（至少 8 位）。
  postinst 以安装时每个已登录用户的身份写进各自的 `~/.local/share/RemoteDesktop/secrets`，并在服务已运行时
  重启它；密码经 0600 临时文件交给 `--set-fixed-password-file`，不进 argv 或日志。留空表示不设置，升级/重装
  时留空保留现有密码；没有用户登录时提示登录后手工设置。可以用 `debconf-set-selections` 预置，
  `dpkg-reconfigure remotedesktop-host` 修改。与 macOS 一致，写入成功后 debconf 中的那份口令会被清空。
- **Ops 的设置改在 `/etc/remotedesktop-server/Ops/appsettings.json`**，单元不再用 `Environment=` 提供
  数据库位置；`--create-admin` 新增 `--config-directory`，手工建管理员与服务工作在同一份配置上。
- **重装会重新询问安装问题**：同一版本重装（`dpkg -i`/`apt install --reinstall`）重新显示数据库与
  TLS / 地址与证书问题，并以上次答案为默认值；版本升级不重新询问；purge 时清除 debconf 答案并删除
  preconfigure 标记，之后重装按全新安装处理。
- **服务端安装包声明真实运行时依赖**（ICU、OpenSSL、zlib、libstdc++、adduser、debconf）；客户端补上
  `libavformat62`（录制）、PipeWire 与 libei（无 host 包时的进程内被控端）以及 `ca-certificates`。
- **Ops 安装即登记本地节点**：新增 `--seed-local-nodes`，服务端 postinst 以服务用户执行，只在节点表为空时
  登记本机的 rendezvous/relay 地址；生成的证书同时导入系统信任库，安装还传 `--metrics-enabled`。新装环境
  的设备管理页与节点状态不再需要手工添加节点或打开指标。

**macOS 安装包安装期配置**

- **自定义 Installer 面板**：新增 `native/macos/rd_installer_plugin.m`（代码构建视图，无 nib）与
  `native/macos/build-installer-plugin.sh`，三个 `.pkg` 用 `productbuild --plugins` 内置一页配置面板。
  服务端在安装时选择 SQLite / PostgreSQL（含连接串）与 TLS（不用 / 生成自签名 / 已有证书），
  客户端与被控端在安装时填写信令地址并导入自签名服务器证书——与 Linux 的 debconf 问题对齐。
- **安装期答案文件**：无人值守安装可把同样的键写到
  `/Library/Application Support/RemoteDesktop/install-answers.plist`（root:wheel，0600）；答案先由
  `preinstall` 从用户可写的 `/var/tmp` 移到 root 副本，`postinstall` 用完即删。
- **整机级配置层**：macOS 的安装器把 `Transport:RendezvousUri` 写到
  `/Library/Application Support/RemoteDesktop/appsettings.json`（签名过的 `.app` 内文件不能改），
  client 与 host 按"随包默认 → 整机 → 用户 → 环境/命令行"的顺序读取；host 同时补上
  `ContentRootPath = AppContext.BaseDirectory`，bundle 内的 appsettings 现在真的会被读到。
- **服务端证书导出与证书固定**：自签名证书生成后复制到
  `/usr/local/share/doc/remotedesktop-server/signaling.crt`；Ops 控制台通过 `Ops:NodeCertificatePath`
  **只信任这一张证书**，客户端/被控端安装器选择同一份 PEM/DER 后也写入
  `Transport:RendezvousCertificatePath` 做证书固定。安装脚本不修改系统钥匙串——macOS 会以
  "no user interaction was possible" 拒绝 postinstall 的信任设置。Ops 的 pin 始终指向导出的公开 PEM：
  选"已有证书"时 Kestrel 路径常是带口令的 `.pfx`，控制台无法直接加载，写进去会让它启动失败。
- **host 安装时设置无人值守密码**：被控端配置页新增密码与确认栏（至少 8 位），postinstall 以当前登录
  用户的身份把密码写进该用户的登录钥匙串并启动 host；留空表示不设置，升级时留空保留现有密码。密码通过
  一个 0600 的临时文件交给 `--set-fixed-password-file`，不出现在 argv、日志或进程列表里，结束即删除。
  没有图形会话时无法确定写入哪个用户的钥匙串，postinstall 会提示登录后手工执行 `--set-fixed-password`。
- **host 卸载脚本**：安装到 `/Library/Application Support/RemoteDesktop/uninstall-host.sh`，
  停登录项、删 plist、`.app` 与安装回执。

**Windows 安装包评审**

- **client/host 安装程序的服务器连接页**：安装时询问 `RENDEZVOUSURI`；地址为 `wss://` 时可选服务器证书（PEM/DER 公钥），
  静默安装用 `SERVERCERTIFICATEPATH`。安装器只把公钥导出到
  `%ProgramData%\RemoteDesktop\certificates\signaling.cer` 并写 `Transport:RendezvousCertificatePath` 固定它；`ws://`
  或公开 CA 证书时留空。与 macOS 的 pin 行为一致，不修改系统信任库；host 的连接页后面仍是无人值守密码页。
- **图形界面升级会带出当前地址/证书**：修改后随安装应用；静默升级不运行界面，因此保留现有地址、证书和设备身份。
  连接页只在全新安装和 major upgrade 出现；修复/维护安装不显示它，也不会重写 live 设置。
- **安装/升级/卸载冒烟测试**：`build/installer/windows/Test-InstallerUpgrade.ps1` 在 CI 与 release 的 Windows
  job 上安装三个 MSI、修改 live 设置、模拟旧布局，再用带新 ProductCode/PackageCode 的同版本副本触发真实
  major upgrade，验证迁移、设置保留与 `REMOVEDATA=1` 卸载清理；另外安装一次带自签名证书的 `wss://` client/host，
  检查公钥被导出并固定到设置、卸载时随 `REMOVEDATA=1` 删除。
- **client/host 安装包的自定义动作 DLL**：net48、x64，负责首次升级迁移与固定密码文件；与 server 包一样由
  包内容测试检查 SfxCA 位数和 `CustomAction.config`。

### Changed

**Windows 安装包：目录名与卸载清理**

- **产品建的所有目录都不带空格**：client/host 的安装目录与机器级数据仍是 `RemoteDesktop`（`%ProgramFiles%`、
  `%ProgramData%`），每用户数据 `%APPDATA%\RemoteDesktop`，macOS 的 `Application Support/RemoteDesktop` 与
  Linux 的 `$XDG_DATA_HOME/RemoteDesktop` 不变，录屏与摄像头录制仍是「视频」下的 `RemoteDesktop`；
  **服务端改了名**：`%ProgramFiles%\RemoteDesktopServer` / `%ProgramData%\RemoteDesktopServer` 统一成
  `RemoteDesktopServer`（安装目录、配置目录、`Set-ServerConfiguration.ps1` 的默认配置目录、`ServerSetup` 的默认
  数据库路径、包内速查说明与运维文档）。带空格的只剩**给人看的名字**：产品名 `Remote Desktop Server`、
  开始菜单目录、macOS 的 `.app` 与 bundle 名。安装/卸载服务脚本、macOS 的 `.pkg` / launchd plist / 安装器面板同步。
  系统包管理器拥有的路径与标识符（`/usr/lib/remotedesktop-server`、`/etc/remotedesktop-server`、
  `/var/lib/remotedesktop-server`、`/usr/local/remotedesktop-server`、systemd 单元名、launchd 标签、dpkg/bundle id）
  保持原样。
- **旧布局迁移代码已删除**：`MigrateClientSettings`（升级时把 Program Files 里的 `appsettings.json` 抄到 ProgramData）、
  它的 `CLIENTINSTALLFOLDER` 属性与 `SetClientInstallDir`，以及连接页读取安装目录那份文件的回退分支，全部移除——旧版安装
  都已卸载，这段代码只会把包内的默认值（回环地址）当成"旧设置"抄过去。`RemoveExistingProducts` 仍固定在
  `InstallInitialize` 之后 1501，包内容测试继续核对这一条。
- **三个包卸载时默认删除数据**：`REMOVEDATA` 的默认值从 `0` 改为 `1`。client/host 卸载会删掉
  `%ProgramData%\RemoteDesktop`（设备身份、日志、地址与证书固定），server 卸载会删掉
  `%ProgramData%\RemoteDesktopServer`（数据库、设置、生成的证书，并移除它自己生成的证书）。
  `msiexec /x ... REMOVEDATA=0` 保留这些数据，供重装后沿用同一设备代码；大版本升级两条路都不走，
  升级不会动数据（条件里的 `NOT UPGRADINGPRODUCTCODE` 不变）。`%AppData%\RemoteDesktop` 是每用户数据，MSI 仍然不管。

**客户端界面的遗留观察项收尾**

- **固定密码区块的"关闭"改为"删除"**（评审 R6）：它本来就是删除已保存的固定密码，现在按钮名、ToolTip 与
  无障碍名称说的是同一件事；使用文档的"修改或关闭"随之改为"修改或删除"，并写明删除后只能用临时密码连接。
- **终端在浅色主题下保持深色定为有意保留**（W10）：ANSI 16 色按深色背景设定，浅底会失去对比；这条现在写在
  使用文档的终端一节，不再作为待决定项。
- **其余观察项关闭**：四段版本号（R5/W9）在版本收敛时已改为三段 `ProductVersion.Display`；隐藏文件整行
  半透明（R9）随 W8 改为只淡化图标与文件名。只剩图标栅格（R3）待设计决定。

**发布流水线的 job 名对齐**

- **`release.yml` 的 `build-windows-and-release` 拆成 `build-windows` + `release`**：前者只打包、自检并上传三个
  MSI 的 artifact，后者（唯一的 `contents: write`）等三个平台的包都到了再用 `gh` 建 Release。三个平台的 job 现在
  同名同形状（`ci.yml` 与 `release.yml` 都是 `build-windows` / `build-linux` / `build-macos`），"发版"只剩一处。
  发布说明改在 `release` 里读同一批 Secret 与 `DEFAULT_RENDEZVOUS_URI` 生成，不靠 job 之间的环境变量传递。

**测试归类与用例确定性整理（产品行为不变）**

- **设备注册表直连 PostgreSQL 的那组用例迁到新的 `tests/RemoteDesktop.Server.Storage.IntegrationTests`**：`UnitTests` 只留内存与 SQLite 两种实现，与"需要真实服务的用例不进 UnitTests"的分类一致；`CONTRIBUTING.md` 的容器跑法同步改指新项目。为让迁移后的用例仍能覆盖 PostgreSQL 方言的 SQL，`Server.Storage` 对该程序集开放了内部类型。
- **测试自身的确定性修复**：等待改成有上限的信号或截止时间（传输列表、PulseAudio 上下文就绪、进程存活都按 PID/事件判断，不再用固定睡眠）；启动失败时释放已经建好的 host 与临时目录；非 Windows 上静默返回的用例改为断言级跳过；预期无回包的 UDP 绑定与"丢包"区分开，后者现在会让用例失败而不是冒充"被拒绝"。

### Fixed

**Windows 客户端装好后打不开（本机实测）**

- **装完客户端双击没反应，进程在启动路径上崩掉**：界面探测后台服务时校验管道服务端身份（管道名字是全局的，服务没占住时别的进程可以先抢注），校验用 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` 读服务端进程的映像路径；而服务以 LocalSystem 运行，该访问权对服务进程只发给 SYSTEM 与管理员，普通用户拿到的是错误 5，抛出的 `Win32Exception` 又不在探测函数的捕获列表里，于是整个进程以 `0xe0434352` 在 `KERNELBASE.dll` 里终止（应用日志里只有一条 APPCRASH）。现在被拒时改问服务控制管理器同样两件事：管道服务端的进程号是不是 `RemoteDesktopHost` 服务的进程、该服务登记的可执行文件是不是预期的那一个——两项交互用户都读得到，能改它们的只有管理员，校验强度不变。探测本身也不再让 `Win32Exception` 逃出去：问不出来就按“没有可用的服务”处理，不再让窗口起不来。客户端与被控端 MSI 已重新构建（含签名）。

**测试与评审收尾**

- **输入合并用例不再偶发失败**：`InputSenderTests` 的 `BlockingWriteStream` 把 `WriteAsync` 调用次数当成了帧数，而
  `FrameCodec` 每帧写两次（长度头 + 载荷），于是"写完了"在最后一帧还没落盘时就发出，测试随即 `DisposeAsync` 取消
  后续写入，偶发读到少一帧。改为按**完成的帧**计数后，完整 Core 套件连跑三次全绿；产品代码未改。
- **管道服务端身份校验的 SCM 分支有了自动化用例**：决策逻辑（进程号比对、服务命令行去掉引号/参数/展开环境变量、
  路径规范化与不匹配时的原因）拆成 `DescribeMismatch` / `ServiceExecutable` 并配 10 条单测；另有一条连接真实
  `RemoteDesktopHost` 控制管道的端到端断言（机器没装服务时明确跳过），结束前只能靠临时探针手工验证的状态。

**Windows 安装包（在 Windows Sandbox 中实测）**

- **固定密码不再禁止双引号**：这条限制来自旧实现——密码当时作为 `--set-fixed-password` 的参数放在命令行上，双引号会破坏命令。现在密码由立即动作写到仅 SYSTEM/Administrators 可读的临时文件、再以 `--set-fixed-password-file` 交给服务；实测含双引号、`$`、反引号、反斜杠、`;`、`&`、`<`、`>` 的 13 位口令从 MSI 属性一路到服务的 DPAPI 密钥库逐字符一致（Host MSI）。安装向导说明、源码注释与运维文档随之更新；静默安装时值里的 `"` 要写成 `""` 并按单个参数传给 msiexec——PowerShell 5.1 的自然写法或 `Start-Process -ArgumentList` 数组会让 msiexec 无声挂起，文档给了 cmd 与 PowerShell 的可复制示例。
- **运维文档的静默安装示例改用 `wss://`**：非回环明文 `ws://` 会被客户端/被控端拒绝，实测这样安装会在服务启动时失败并让整个 MSI 回滚（错误 1920 / 1603），而不是在参数阶段报错。Windows 的 build 与静默安装示例、macOS 答案文件说明、Docker 局域网一节同步修正，并写明明文需要显式 `Transport:AllowInsecureSignaling`。

**Linux 安装包（在 Ubuntu 26.04 上实测）**

- **首次安装客户端或被控端 `.deb` 时不再误报库不在 loader 缓存**：postinst 在 dpkg 配置阶段检查 `ldconfig -p`，而缓存由 libc-bin 的 trigger 在本事务所有包配置完成之后才刷新，所以同一次安装里刚装好的 libavcodec、libei 等必然被列成“缺失”，提示窗口会黑屏。检查前先刷新一次缓存（已是最新时为空操作）；真正缺库（例如 `--force-depends`）仍会警告。

**PostgreSQL 后端（在 postgres:18 上实测）**

- **管理动作列表在 PostgreSQL 上打不开**：按状态筛选时把可空的 `@state` 直接与 `IS NULL` 比较，Postgres 无法推断参数类型，请求以 `42P08: could not determine data type of parameter $1` 失败（状态为空时必现）。改为与同库其它查询一致的 `CAST(@state AS TEXT) IS NULL`。SQLite 不受影响。
- **多个服务进程同时启动时建表可能与迁移冲突**：`CREATE TABLE IF NOT EXISTS` 并不防并发创建，两个进程同时启动会有一个因目录唯一冲突失败；两个都读到旧版本还会同时应用同一迁移。设备注册表与监控台 schema 在 PostgreSQL 上改为先取会话级咨询锁（`pg_advisory_lock`）再建表/迁移，结束时释放；SQLite 自身的文件锁保持不变。
- **PostgreSQL 存储层首次有直连测试**：设备注册表与监控台存储各一组用例，覆盖建库与迁移、并发启动、旧库原地升级、租约竞争、审计链、会话与指标读写等；不设 `RD_TEST_POSTGRES`（Npgsql 连接串）时跳过，`CONTRIBUTING.md` 写明用 `postgres:18` 容器跑法。

**评审后修复（第五批）**

- **帧编解码拒绝负的长度上限**：`FrameCodec.ReadAsync` 用无符号比较长度前缀，负的 `maxPayloadSize` 会回绕成一个巨大的上限而关掉对恶意长度前缀的校验；现在入口处直接拒绝。所有现有调用方传的都是正的常量，所以此前不会触发，属潜伏缺陷。
- 内部整理（行为不变）：桌面与摄像头窗口里复制粘贴的“解码卡死自愈”合并成 `Media` 层的 `VideoDecoderSupervisor`；Ops 与会合服务器各一份的可信代理解析抽成共用的 `TrustedProxyList`（没有可信代理仍不添加转发头中间件）；中继、会合服务器与安装器里 base64 密钥长度校验抽成共用的 `Base64Key`，各处报错文案不变。
- 文档：说明 `RD_SELF_CHECK` 任何非空值（含 0、false）都会触发自检；记录 `build-deb.sh` 的输出目录参数与 `DEB_ARCHITECTURE` / `DEB_RUNTIME`（构建者开发旋钮，需成对修改，CI 与发布仅支持 amd64）；清理残留的变更日记式措辞。
- 补充测试：SRP 畸形输入、会话通道上限与权限撤销、经产品代码路径的 wss 证书固定、帧编解码与 `ProtobufFrames` 的边界、文件路径换行与进度账本的剩余分支。

**评审后修复（第四批）**

- **认证失败限流对并发握手也生效**：握手开始时先为该控制端设备预占一个名额（已记录失败数加进行中的握手数达到 5 即拒绝），口令正确则退还并清零，口令错误则转为一次失败，出示口令证据之前就中断、超时、取消或出错则只退还、不计失败。同一设备不能再靠并发握手在被锁定前多猜几次，网络抖动也不会误锁合法控制端。
- **文件管理器的变更类请求不再在复用通道上重发**：复用的连接在收到响应前中断时，只有列目录、取位置这类只读请求自动重试；新建文件夹、重命名、删除改为提示“连接中断，操作可能已经生效，请刷新列表确认”，不再得到误导的 NotFound / AlreadyExists。
- **只提供高于 30 fps 模式的摄像头**（60/120 fps 专用摄像头、采集卡）不再被拒绝为“没有可用格式”：选用帧率最低的模式并丢帧到 30 fps 发送；有 30 fps 以下模式的摄像头行为不变。尚未在真实摄像头上验证。
- Linux 录像器在没有录制时调用 `StopAsync` 不再抛异常，与 Windows / macOS 一致并写入接口文档；终端读写循环和输出处理器出现非预期异常时现在会记录警告日志（含异常），终端仍按原样结束。
- 内部整理：Windows 客户端与服务端安装包构建脚本里逐字重复的 6 个签名/清理函数抽到共用的 `build/installer/windows/Import-SigningHelpers.ps1`，行为不变（未做真实 MSI 构建验证）。

**评审后修复（第三批）**

- **没有可信代理时不再采信任何调用方的 `X-Forwarded-*`**（安全）：Ops 与 Rendezvous 清空了框架默认信任的回环代理，但 ASP.NET 的转发头中间件在“已知代理”两个列表都为空时会应用任何调用方的头，所以没配 `TrustedProxies` 时，调用方可以自己写来源地址和协议，绕过按地址的注册限流与连接配额、登录失败限流，伪造审计来源，并决定 Cookie 的 Secure 标志。现在没有配置可信代理就不添加该中间件。打包部署与 Docker compose 本来就配置了 `TrustedProxies`，不受影响；前面放了反向代理的部署必须按文档配置它。
- **文件传输**：剩余空间预检改为按目的路径所在的卷判断（Unix 上为最长挂载点）；Windows 设备路径（含正斜杠写法）一律拒绝；把文件系统根配置为共享根时其下路径不再被误判；控制端对主机回传的文件大小与续传偏移做范围校验；日志中的路径和名字里的换行等控制字符改为可见转义；文件管理器在请求进行中被释放时不再抛 `ObjectDisposedException`；暂停后的任务在旧运行收尾前被恢复时，新运行仍可被暂停或取消。
- **会话**：控制端会话在订阅结束事件之前就已关闭时，结束上报与界面通知不再丢失；对端宣告的协议版本超出 16 位时不再被误判为兼容；被控端输入通道收尾时单个按键释放失败不再让其余按键卡住；会话进行中的异常不再被记成“握手失败”。
- **服务端与 Ops**：并发创建同名账号或节点返回 409 而不是 500；SMTP 连接阶段超时的告警原因改为明确的“timed out connecting”；配置阶段空的 `--database-path` 立即报错；被控端对控制端提供的候选地址最多取前 64 个；在线状态查询的限流按信令连接而不是按设备，设计文档已如实说明。
- **平台与 CI**：Linux 重启被控端服务时不再可能因管道写满而卡死（并发读 stdout/stderr，30 秒超时）；Linux 录像器不再读已释放的编码器上下文；Docker 的 `.env.example` 不再带 `change-me` 占位值，必填项留空，未填时 `docker compose` 明确报错；macOS 服务端安装包的 `--arch` 只接受 arm64；服务端安装包生成 WiX 时对目录名做 XML 转义；CI 补充服务端包（Relay、Gateway）、`selfsigned` compose 叠加层和安全审计脚本的检查，依赖规则测试新增“Features 不得引用 Platform 与宿主”。

**评审后修复（第二批）**

- **被控端会话数已满时明确拒绝**：同时会话数达到上限（16）时，第 17 个控制端以前先收到“认证已接受”再被静默断开；现在口令验证通过后立即收到“对方当前连接的会话数已满，请稍后再试”（新增 `AUTH_FAILURE_TOO_MANY_SESSIONS`，协议主版本不变，旧客户端显示通用的“会话建立失败”）。
- **信令断开不再丢待发的“会话结束”报告**：报告描述的是 QUIC 会话而不是信令连接，断线时未发出或发送失败的报告现在留在队列里，重连后补发。
- **本机安全加固**：Linux/macOS 伪终端的 master/slave 描述符带 close-on-exec，不再被 shell 及其子进程继承（macOS 上用 FIOCLEX，未经实机验证）；Windows 被控端以 `FirstPipeInstance` 创建本机控制管道、界面端以 Identification 级别连接，名字被别的进程占用时服务记 Error 并退避重试而不是与其共用（仍不校验服务端进程身份）；服务端设置、自签私钥与 POSIX 文件密钥库在创建时即为仅属主权限，不再有“先宽权限创建再 chmod”的窗口。
- **安装与发布脚本**：macOS 客户端/被控端预安装脚本忽略符号链接的答案文件，信令地址整串校验、拒绝空白/控制字符/双引号/反斜杠；发布流水线的 job 级环境只保留“是否配置”的布尔值，证书、口令与公证密钥只进入确实使用它们的步骤；Linux 服务端包只在现有设置确为 PostgreSQL（或无 provider 的旧布局）时才沿用旧连接串，否则重新询问；Linux 客户端/被控端打包脚本能拦下地址中的单个反斜杠。
- 文档：认证失败表被已锁定设备占满时表外设备被暂时一律拒绝，是有意的 fail-closed 取舍，已写入设计文档。

**评审后修复：客户端**

- **取消“记住密码”现在会清除已存的密码**：以前只是不再写入，已存密码下次启动仍被读回并预填。副作用：对存过固定密码的设备取消勾选再连一次，会抹掉那个密码。
- **设备列表读不出来时不再覆盖整份列表**：文件被别的进程短暂占用时以前会被当成空列表，下一次保存就把旧列表替换掉；现在读失败会抛出并保留原文件，条目不合格的文件改名为 `.corrupt-` 保留。
- **一帧畸形视频帧不再结束整个桌面或摄像头窗口**：现在只丢这一帧并请求关键帧，窗口因流描述无法使用而关闭时会记日志。
- **本机驱动器列表**遇到无权读取容量的驱动器时不再抛出。

**评审后修复：被控端与核心**

- **会话结束一定会上报**：对端正常离开或断网时，被控端以前不上报“会话结束”（监控台里直连会话一直“进行中”），开启遥测时心跳循环也不会停；现在收尾时取消。
- **Windows 服务模式下残片清扫器终于会运行**：会话 Agent 以前从不启动宿主，超过 7 天的 `.rdpart` 永远不被清理；同时清扫只处理绝对路径且以 `.rdpart` 结尾的条目，路径含换行的残片不再记入清单。
- **本机控制通道出错不再停掉整个被控端**：管道或套接字创建、接收连接失败时记 Error 日志并按 1 到 30 秒退避重试。
- **大文件上传不再在收尾时误报超时**：等待主机把残片复制成最终文件的时间随文件大小放宽（每 MiB 1 秒，上限 1 小时）。
- 会话 Agent 关闭时通道没能在 2 秒内退出不再让它以 Fatal 崩溃；会话降级与撤销权限并发时不再互相覆盖。
- Windows 屏幕采集故障恢复后，订阅者收到画面时不会再短暂读到“仍在故障”：先清除故障标志再发布帧。

**评审后修复：服务端**

- **Ops 重启后后台任务立即接手**：抓取、告警评估、动作恢复以前要等旧租约过期（最长约 5 分钟）才恢复；现在租期随轮询间隔缩短，停止时主动释放。
- **注册限速按网段归并**：同一 IPv6 /64 内换地址不再能绕过“每地址铸造设备身份”的限速。
- **失败的管理动作如实记入审计**：以前节点不可达或拒绝执行也记为成功，且审计链不可修改。
- **监控台的“最后活跃”不再停在很久以前**：设备在同一信令进程内重连现在也会刷新（每设备每分钟最多写一次）。
- **改了 `Ops:Listen` 的端口后 HTTPS 网关仍可访问控制台**：网关的转发目标跟随该端口，不再固定为 5090；无人读取的 `Gateway:Upstream` 配置键已删除。
- Relay 接收循环遇到套接字错误或处理异常时不再退出整个进程。

**构建、部署与 CI 的评审修复**

- **Docker ops 容器没有登记本机节点**：镜像的 `ENTRYPOINT` 就是控制台，compose 里 `command: [bash, -ec, ...]` 只会被
  当成参数追加，脚本从未执行。改为一次性服务 `ops-seed`（同镜像、同环境）运行 `--seed-local-nodes`，`ops` 依赖它成功退出；
  `ops` 不再带 `command:`，`run --rm -it ops --create-admin admin` 的参数直接交给控制台。CI 增加对应的 compose 断言。
- **macOS 服务端安装完成页的 `--create-admin` 以 root 运行**：改为 `sudo -u _remotedesktop ...`，否则 SQLite 的
  `-wal`/`-shm` 归 root，监控台之后写不进去。
- **Linux/macOS 安装脚本不再把密钥放进命令行参数**：中继密钥、PostgreSQL 连接串、PFX 口令改经 `RD_RELAY_KEY` /
  `RD_POSTGRES` / `RD_TLS_PASSWORD` 环境变量交给 `--configure-system`（Windows 安装器本来就这样做）。
- **`Uninstall-HostService.ps1` 对 `-InstallDirectory` 无保护**：改按字面路径处理，并在停服务之前拒绝盘符根、Program Files、
  Program Files (x86)、Windows 目录。
- **发布流水线的签名降级**：Windows 缺 `CODE_SIGN_CERT_BASE64` 或 `CODE_SIGN_CERT_PASSWORD` 任一项都写 `::warning` 并在
  Release 说明里标未签名；macOS 已签名但未公证也写进 Release 说明；`DEFAULT_RENDEZVOUS_URI` 不再内插进脚本文本；
  `audit-packages` 在 `dotnet list package` 退出码非 0 或出现 NU1900（漏洞数据取不到）时同样失败。
- **macOS 录屏遇到带行填充的帧**：队列项保存重新打包后的行宽，而不是调用方的 stride，否则原生校验必然失败。
- **`Invoke-SecurityAudit.ps1` 帮助文本过期**：示例改为 MSI 部署的 ProgramData 路径与存在的产物路径。

**Docker 部署：信令监听改回容器外可达**

- **默认 compose 部署连不上信令**：信令默认监听改为回环后，容器里没有任何地方覆盖 `Urls`，宿主机的端口映射、Caddy 和监控台访问的 `rendezvous:5080` 全部连不上，而容器内的健康检查仍然显示正常。`docker-compose.yml` 现在为 rendezvous 设置 `Urls=http://0.0.0.0:5080` 并显式放行 `AllowInsecureSignaling`，CI 的冒烟用例同步修改；暴露范围仍由 `ports:` 与 `RENDEZVOUS_BIND_IP` 决定，发布到可信网络之外要叠加 tls 或 selfsigned overlay。selfsigned overlay 里的 `http://0.0.0.0:5082` 以前同样会因缺少放行而无法启动。

**文件传输的落盘与边界处理**

- 修复上传收尾阶段磁盘写满时，控制端收不到任何结果、只看到通道被关闭的问题：现在会收到"文件无法放到位"的失败响应，残留的半截目标文件会被删除，`.rdpart` 保留以便续传。
- 修复覆盖已有文件时先截断再复制，中途失败会丢失原文件的问题：现在先写入同目录的临时文件，写完再替换，失败时原文件保持不变。上传前的空间预检相应改为要求容纳"待接收数据 + 一份完整副本"。
- 修复"接收期间同名文件被他人创建"时，重命名策略不会自动改为 `名称 (1)`、整个上传在传完后失败的问题。
- 修复控制端声明的文件大小超过 `long.MaxValue` 时变为负数、绕过空间检查并得到空文件的问题，现在直接拒绝。
- 修复文件名接近 255 字符时，加上 `.{大小}-{时间}.rdpart` 后缀的续传文件名超出文件系统限制、上传失败的问题：过长的续传文件名会被确定性地缩短（带全名哈希），未超长的名称保持不变，旧的续传文件仍可继续。
- 修复恢复接收时，续传文件在读取过程中被截断会导致无限空转的问题。
- 修复链接在路径检查与打开之间被替换为指向共享目录之外时，会在目录外留下一个空文件的问题。

**解码器自愈与会话收尾的健壮性**

- 控制端解码器静默丢帧的判定更保守：只有连续 3 个非关键帧都没有出画面、并且已超过 2 秒，才重建解码器；解码器对仅含参数集的关键帧返回空不再被误判为失步，也就不会丢掉刚学到的 SPS/PPS。桌面与摄像头一致。
- 重建解码器时先创建新的再替换旧的：创建失败（硬件解码器丢失等）会保留旧解码器并继续请求关键帧，不再因为旧解码器已被释放而关闭整个画面。Windows 的 H.264 解码器在排空输出后重试仍被拒绝时，也改为请求关键帧，不再退出画面。
- 被控端 Agent 遇到未处理异常时，先把致命错误日志落盘再退出，不再因为每秒一次的刷盘间隔丢掉导致崩溃的那一行。
- 被控端处理连接的后台任务增加兜底异常处理：握手期间连接被释放等意外异常会记录警告并关闭连接，不再成为无人观察的任务异常。
- 密码尝试限制表不再整表扫描：过期项按分钟摊销清理，并设 10000 个设备的上限；表满时优先淘汰未被锁定的最旧设备，已锁定设备的锁不会被身份轮换冲掉。
- 中继对 token 不匹配、cookie 错误、会话表已满的拒绝同样按分钟合并为一条摘要日志，不再每个包一行 Warning；摘要中分别列出各类拒绝的数量。

**Linux 多显示器：按被共享的那块屏幕的尺寸采集**

- **请求另一块显示器时画面尺寸对不上**：portal 只能共享操作员勾选的屏幕，而采集格式此前被钉在"请求的那块"的
  尺寸上。一旦实际流是另一块显示器（分辨率不同），拷贝会因为步幅不匹配而丢帧，表现为黑屏或卡住。现在匹配在
  合成器的**逻辑坐标**里做（容忍 1–2 px 的分数缩放进位），并把端口流的真实尺寸用于采集与编码。
- **只有一块流时按它自己的尺寸与身份采集**：采集器与对端宣告的显示器都换成实际被共享的那块，指针、工具栏和画面
  不再各说各话；切换时写一条 warning 说明"请求的是 X、共享的是 Y"。
- **共享多块而请求的不在其中时明确报错**：错误信息列出已共享的尺寸，不再拿别的显示器冒充请求的那块。
- **视频流信息改宣告采集器的显示器**（`capturer.Display`），而不是最初请求的 id。

**控制端一次性请求没有超时**

- **被控端接受通道后不回答，界面会一直等下去**：文件面板的列目录/改名/删除、终端的列出 shell 与打开、摄像头
  列表与打开、文件传输的接受与最终校验，现在都通过新增的 `RequestTimeout` 等第一个应答，上限 30 秒。超时给出
  "被控端没有及时响应"而不是让文件面板、终端窗口或摄像头列表挂到会话结束。传输的数据阶段、终端输出与摄像头
  画面仍只受调用方的取消令牌约束（见 `docs/design/README.md` 的"还没有做的事"）。
- **摄像头打开的 30 秒超时改用同一套机制**：窗口仍把它显示为"摄像头权限未授予"的提示，但不再靠
  `OperationCanceledException` 识别超时——用户主动取消与对端沉默不再混为一谈。

**未完成传输的清单无界增长**

- **清单随请求无限增长，每次写入重写整个文件**：`PartialFileLedger` 现在最多保留 1024 条
  （`PartialFileLedgerOptions.MaxEntries` 可调），超出时丢弃最旧的记录；读取旧版或手工写下的超长清单同样先
  裁到上限。文件大小、内存占用与单次写入成本因此都有上界，不再是"每次上传重写一遍整张列表"。
- **残片清扫只在进程启动时跑一次**：`PartialFileSweeper` 改为启动时扫一遍、之后每 6 小时一轮，长期开机的
  被控端不会把 7 天前放弃的 `.rdpart` 留到下次重启。

**控制台看不见本机节点**

- **Linux 包没有让控制台固定本机信令证书**：本机节点固定登记为 `https://127.0.0.1:5080`，而生成的证书 SAN 只含
  机器名与本机 IPv4（Debian/Ubuntu 上主机名解析到 `127.0.1.1`），控制台按系统信任校验时报名称不匹配，节点管理
  显示「抓取失败」、设备管理为空。`postinst` 现在把公钥路径传给 `--ops-node-certificate-path`：生成证书用
  `tls/signaling.crt`，自带 PEM 用复制到 `tls/custom` 的那份；明文 HTTP 清空 pin，PKCS#12 不留 pin（无法在没有口令
  的情况下读出公钥）。Windows 与 macOS 安装器一直这么做，只有 Linux 少了这一步。
- **自签证书不含回环地址**：`SelfSignedCertificate` 的 SAN 现在始终包含 `127.0.0.1`，用回环地址访问本机信令或
  Gateway 的客户端不再因为名称不匹配而拒绝证书；已经签发的证书不会重建，由上面那条 pin 覆盖。

**远程画面卡住**

- **画面卡住后永不恢复**：采集循环遇到非预期异常（设备移除、驱动重置、后端自身报错）时不再静默结束或对同一个
  已失效的 duplication/device 无限重试，而是报告 `IsFaulted` 并由共享采集重建整个采集器；重建本身失败也记日志，
  不再有"看起来像静态桌面"的静默路径。
- **重连后还是同一张旧图**：共享采集在空闲停止时丢弃它留下的最新一帧，并为下一个订阅者重建采集器；订阅者的第一帧
  若比订阅时刻早 2 秒以上直接丢弃。此前重连拿到的是上一会话冻结前的那张图，并被当作新流的首帧 IDR 发出。
- **空闲期间的画面变化被吞掉**：无人订阅时不再丢弃已取到的帧（DXGI 只交付"自上次取帧以来的变化"，丢掉即永久丢失）。
- **首帧超时每秒重建一次显卡设备**：静止桌面上的首帧重建限制为 3 次，之后等画面真正变化，不再每秒新建 device 与
  duplication。
- **被控端无帧看门狗**：`VideoChannelModule` 在"2 秒没有帧且采集器自报故障"时重建采集、丢掉旧图并要求关键帧，日志
  带 session id（此前视频链路日志既无 session id，也无法区分"桌面静止"和"采集坏了"）。
- **控制端停流不可见**：`DesktopViewer` 在 2 秒没有新画面时请求关键帧（2/4/8/16/30 秒退避，静止桌面不会刷 IDR），
  请求超过 4 秒仍无回应即判定画面已过期，窗口顶部明示"被控端已停止回应画面请求"，不再用旧像素假装正常。

**运维监控台评审**

- **后台服务异常会停掉整个控制台**：告警求值与指标抓取现在逐轮、逐节点捕获异常并继续；启动时校验
  Webhook URL 与节点证书路径（`Ops:NodeCertificatePath`），并设置 `BackgroundServiceExceptionBehavior=Ignore`
  作为兜底。
- **并发 ingest 丢失会话投影**：`AppendEventsAsync` 的读-合并-写现在由进程内串行门保护，避免 PostgreSQL
  读已提交下后写覆盖前写，也避免 SQLite 同时写导致 500。
- **TOTP 可重放**：验证通过的时间步写入 `last_totp_step`，同一六位码在 ±30 秒窗口内不能再次登录。
- **Setup CLI 路径与服务不一致**：Windows 默认读 ProgramData、Linux 默认读 `/etc/remotedesktop-server/Ops`、
  macOS 读安装目录；配置目录不存在时给出说明而不是堆栈；服务与 CLI 共用 `OpsDatabaseSettings.ResolvePath`
  解析相对路径、展开环境变量、trim provider，空展开按缺失处理。`--create-admin` 不再静默落到已安装的生产库。
- **审计链可能因 CLI 与服务并发写而分叉**：`AppendAuditAsync` 现在在同一数据库事务内读前一条并写入，
  SQLite 用 `BEGIN IMMEDIATE`、PostgreSQL 用 advisory lock。
- **登录限流容量可被填满导致全站登录锁定**：表满时淘汰最旧的未锁定项，不再锁定未知 key；另加按来源地址的
  宽预算，挡住跨用户名喷洒。
- **节点响应无大小上限**：`/metrics`、设备列表与动作错误响应最多读取 8 MB（错误文本 64 KB），超限按失败处理；
  设备代理对超时和非法 JSON 返回 502 而不是 500。
- **PostgreSQL 的 `REAL` 只有 4 字节**：基线改为 `DOUBLE PRECISION`，v5 迁移修正已有库。
- **ingest 不校验 `OccurredAt`**：拒绝 7 天以前或 5 分钟以后的事件，避免错误时钟污染会话时间线。
- **会话分页同毫秒漏页**：游标改为 `(timestamp, id)`，API 返回 `nextBeforeId`。

**服务端安装**

- **默认安装后 Ops 会话监控为空**：原生 Windows / Linux / macOS 服务端的共享安装步骤现在自动生成独立的
  32 字节 Ops HMAC 密钥，并写入 Rendezvous 与 Relay 的 `OpsEvents` 以及控制台的 `Ops:Ingest:Keys`；升级会
  补齐旧安装缺失的一侧而不轮换已有密钥。多机 Relay 可用 `--ops-events-endpoint` 和 `RD_OPS_INGEST_KEY`
  指向中央控制台。自定义 KeyId、额外接收密钥和 Endpoint 保持不变；同一 KeyId 对应不同密钥时安装明确失败。

**运维监控台数据接入**

- **节点指标只采不展**：节点管理页新增「详情」，展示节点版本、启动时间、运行时长和该节点全部最新指标；新增
  `/api/metrics` 目录供告警规则选择。Windows 升级现在同样开启信令 `/metrics`；macOS/Linux TLS 本机节点统一
  用回环地址，Linux 升级同步节点地址，避免无 `NodeToken` 时的 401。
- **Docker 节点不再手工登记**：Ops 容器启动前幂等登记 `rendezvous` / `relay`；Compose 显式开启
  `Ops:AllowInsecureNodeHttp` 以允许令牌在隔离网络内明文传输，公网默认仍拒绝。
- **告警规则补比值与校验**：UI 增加 `ratio` 和指标选择器；保存 `single`/`ratio` 时校验指标确实被抓取过，
  避免拼写错误静默失效。
- **链路占比不再把“无数据”当成 0**：只统计收到过客户端心跳的会话，`session.direct` 规则同样受益。

**Linux 安装包评审**

- **Ops 首次管理员命令在 .deb 布局下必然失败**（`Ops:Database:Path is required`）：现在读
  `/etc/remotedesktop-server/Ops/appsettings.json`，并在文档中给出 `--config-directory`。
- **`/etc/remotedesktop-server` 下的中继密钥与 PostgreSQL 密码全局可读**：生成的 `appsettings.json`
  为 `0640 root:remotedesktop`，目录 `0750`；`/etc/default/remotedesktop-server` 同样收紧。
- **systemd 把 `/var/lib/remotedesktop-server` 重置为 0755**：三个单元加 `StateDirectoryMode=0700`
  与 `UMask=0077`，Ops 库（含口令散列与 TOTP 密钥）不再对本地用户可读。
- **`StartLimitIntervalSec=0` 写在 `[Service]` 里被忽略**：移到 `[Unit]`。
- **客户端包缺 `libavformat62`**：录制在干净机器上会以 `DllNotFoundException` 失败；已补依赖与安装检查。
- **客户端“不会被控制”的描述与代码不符**：安装后的客户端在没有 host 服务时仍会在进程内充当被控端；
  依赖已补齐，描述已改正。
- **`Recommends: libmsquic` 与服务端无关**：服务端不使用 QUIC，已移除；描述中“缺少时 postinst 会提示”
  的段落一并删除。
- **自签证书只覆盖本机名/本地 IPv4**：新增 `RD_TLS_HOSTNAME`，并让 `--configure-system` 校验证书与口令
  能否实际加载；已有证书会被复制到服务可读的位置。Linux 补上 `RD_TLS_PASSWORD`。
- **`RD_DATABASE_PATH` 自定义目录在 `ProtectSystem=strict` 下不可写**：postinst 创建并授权目录，为
  rendezvous/ops 写 `ReadWritePaths` drop-in；切回默认位置或 PostgreSQL 时移除。
- **`RD_SERVER_CERTIFICATE` 不校验 PEM**：构包时用 `openssl x509` 验证，私钥不会被误打进客户端包；
  安装时同样校验，`update-ca-certificates` 失败会中止安装而不是静默继续。
- **host 包缺图标**、**ufw 规则不随 purge 移除**、**libmsquic 版本号硬编码**、**无 changelog/md5sums**
  等发布卫生问题一并修复。
- **Ops 在 `/etc` 工作目录下登录页 404**：静态资源改从 `AppContext.BaseDirectory/wwwroot` 提供，不再依赖
  进程的当前目录。
- **Linux 服务模式下 client 与 host 共用 `device-identity`**：两者以同一设备 ID 注册，信号令每设备只保留
  一个会话，连接每秒互踢。客户端在 Linux 服务模式改用独立的 `controller-identity`；Windows 本来按数据目录
  分开，macOS 没有独立服务。
- **purge 后立即重装 client/host 时证书被判为不可读**：`/etc/remotedesktop-server/tls/signing.crt`
  在卸载重建后会被 `test`/`cat`/`openssl` 间歇性报告为不存在。client/host 的 config 与 postinst 改为
  进入证书目录按相对路径读取，先复制到临时文件再校验；server 的信任库导入同样处理。证书确实不存在或
  不是 PEM 时仍会明确失败。
- **Linux 上 client 先于 host 服务启动时退化为独立被控端**：随后 host 服务启动，两者再次争用同一设备身份。
  host 包已安装时 client 最多等待服务控制 socket 3 秒；服务在窗口打开后才出现时，界面提示重启客户端以交给
  它托管。
- **Linux 上卸载 host 包后进程仍在运行**：客户端继续把残留的控制 socket 当作后台服务，显示“由后台服务托管”。
  现在 postrm 会停止每个已登录用户的 `app-RemoteDesktopHost.service`；客户端也只在 host 包（unit 文件）
  仍然安装时才使用该 socket，否则按 standalone 运行。
- **Linux 安装 host 包后要等下次登录才生效**：已登录用户的客户端一直看不到新服务。postinst 现在会为已有
  图形会话的用户立即启动 `app-RemoteDesktopHost.service`；没有会话时仍随下次登录启动。
- **远端会话窗口在主机未上报光标形状时把本地光标也隐藏**：窗口里没有任何指针。现在本地光标保留到第一张
  远端光标图到达为止；会话窗口关闭时重置光标，主窗口显式使用普通箭头光标，避免空白光标残留。
- **桌面启动器把产品名翻译成“远程桌面”**：移除 `.desktop` 的 `Name[zh_CN]`，与窗口标题、安装包名称统一为
  `Remote Desktop`；描述和关键词仍保留中文。
- **macOS 服务端 postinstall 未给 Ops 传数据库路径与监听地址**：`--create-admin` 与服务可能打开不同的
  `ops.db`；现在与 Linux 一样写在同一份配置上。

**macOS 安装包评审**

- **客户端 `.pkg` 构建在最后一步以 141 退出**：`pkgutil --payload-files | head` 在 `pipefail` 下触发
  SIGPIPE；改为一次性读取列表。发版不再"包已生成、构建却失败"。
- **三个脚本都找不到 `Developer ID Installer`**：`-p codesigning` 不包含 Installer 证书（无 Code Signing
  EKU），改用不过滤的 `find-identity -v`。带证书的 `--distribution` 现在能真正走通。
- **server `.pkg` 无法公证**：`--distribution` 现在逐个签载荷里的 Mach-O（apphost 带 JIT entitlements），
  `--notarize` 要求 Developer ID Application；只签外层 `.pkg` 的旧路径被移除。
- **host 包装完不启动**：新增 preinstall/postinstall，安装时 `bootout`/`bootstrap` 当前图形会话的
  LaunchAgent，升级会重启旧进程，首次启动的 TCC 提示不再要等下次登录。
- **新装 Ops 没有节点、信令没有 `/metrics`**：postinstall 补 `--seed-local-nodes` 与 `--metrics-enabled`，
  与 Linux 安装包一致；切换 http/https 后新增 `--sync-local-nodes` 更新安装器建的两条节点，避免控制台
  继续抓取旧协议而一直失败。
- **`.pkg` 安装报告成功却没有装进 `/Applications`**：Installer 默认按 bundle id 重定位，找到本机构建目录里的
  同名 `.app` 就"升级"那一份（实测 macOS 26：安装成功、`/Applications` 为空、构建目录被 root 改写，
  登录项指向的路径不存在）。client/host 的应用组件现在标记为不可重定位，并用 `ditto` 暂存以保留
  逐文件签名所需的扩展属性。
- **重装 client/host 会丢掉自签名服务器证书的固定**：读取整机配置时用了 PlistBuddy 的 `:` 键路径，
  `plutil -extract` 需要 `Transport.RendezvousCertificatePath`，于是地址不变、证书栏留空时误判为没有
  pin 并把它删掉。已改正，并实测第二次安装后 pin 仍在。
- **安装器介绍页出现乱码**（`Rendezvous â€” signaling`）：欢迎/完成页的 HTML 没有声明字符集，Installer 的
  WebView 按 Latin-1 解码 UTF-8。三个包的六个 HTML 资源都补上 `Content-Type: text/html; charset=utf-8`。
- **服务器配置页的数据库和 TLS 无法选择**：两个独立原因叠加。其一，Installer 26 取走 `contentView` 后立刻
  释放了 `firstPane` 返回的 pane，而 `NSControl.target` 与 `NSTextField.delegate` 都是弱引用，所有
  action/delegate 都发给已释放对象，下拉框只会改标题、不会更新字段；section 现在强持有 pane。其二，
  菜单项被 `NSMenu` 交给 InstallerPane 做自动校验而全部变灰；下拉菜单关闭 `autoenablesItems`，由控件自身
  的启用状态决定能否选择。两者都在真实 Installer 里逐项点过验证。
- **配置页超出 Installer 给的 421×329 内容区**：旧视图是 640×430，标题、字段右端和底部说明都被裁掉；
  三个角色的面板按该尺寸重排，升级时的“保留现有设置”说明、数据库/TLS 说明均完整可见。
- **PostgreSQL 占位符和 TLS 生成说明被截断**：完整连接串示例约 450 pt，塞不进 272 pt 的输入框；改为完整但
  精简的 `Host=db;Database=rd;Username=rd`，鼠标悬停显示带端口/口令的完整示例。TLS“生成自签名证书”的说明
  含两个长路径，11 pt 下要三行、放不进两行高的说明区；说明字号改为 10.5 pt 并精简措辞，两条路径完整可见。
- **macOS client 与 host 同时安装后 client 反复连接/断开**：客户端的控制端连接在委托 Host 服务时仍加载
  `device-identity`，和 Host 使用同一设备代码，信令服务器每个设备只保留一个会话，两个进程互相踢下线。
  现在与 Linux 一致：检测到 Host 服务时客户端改用 `controller-identity`，两者设备代码不同、可以同时在线；
  Host 包已安装但控制 socket 尚未就绪时，客户端最多等 3 秒，并在服务稍后出现时提示重启客户端。安装器不再
  互斥，两个包可以同时安装。
- **没有 UI 的 host 在“应用程序”里像一个可以打开的 app**：它和 client 并排、图标相同，用户会双击后
  以为坏了，也容易与 client 混淆。host 现在安装到
  `/Library/Application Support/RemoteDesktop/Host/Remote Desktop Host.app`，不出现在“应用程序”或启动台；
  LaunchAgent、client 的 host 检测路径、卸载器和文档同步更新。升级时会停止旧登录项并删除
  `/Applications/Remote Desktop Host.app`；TCC 授权窗口仍显示 **Remote Desktop Host**，如有旧路径的授权
  残留，在系统设置里移除旧项后按新路径重新勾选。
- **升级会重写 Ops 设置、把 `none` 改成 SQLite**：`--configure-system` 现在区分"没给"与"给默认值"，
  没给的地址/中继地址保持不变；`--database=auto` 与 `--ops-*` 只在首装应用，升级默认保留。
  安装器还会写一份不含秘密的 `install-summary.plist`，供下次升级的面板预填。
- **TLS 已有证书在 macOS 上必然失败**：`X509CertificateLoader` 的 `EphemeralKeySet` 在 macOS 上不支持，
  改用 `DefaultKeySet`；`configure-server.sh --tls-certificate` 现在能工作。
- **client/host 缺 `NSLocalNetworkUsageDescription`**：补上；局域网部署首次连接会正确请求本地网络权限。
- **host 开发签名身份不固定**：复用 client 的 `development-identity` 与多证书警告；两个包都检查
  `libmsquic.dylib` / `libRemoteDesktopMacNative.dylib`，分发构建缺库直接失败。
- **构建期秘密随 `.pkg` 分发**：`--distribution` 拒绝 `--postgres` / `--relay-key` / `--tls-password`；
  安装期答案或 `configure-server.sh` 是唯一推荐路径。
- **server publish 不清理旧文件**：发布前 `rm -rf` 每个程序的输出目录。
- **其他**：`configure-server.sh` 改数据库后同时重启 Ops；卸载器删除文档目录与回执；服务端日志目录 0700；
  `.pkg` 加 `allowed-os-versions`；服务端包不再带 `appsettings.Development.json`；client/host 的
  `--arch` 明确只支持 arm64；`pkgutil`/`lsbom` 的 `grep -q` 与 `pipefail` 组合改为读完整输入。

**Windows 安装包评审**

- **server MSI 升级会丢掉 live settings，Relay 起不来**：三份 `appsettings.json` 从 `Program Files` 移到
  `%ProgramData%\RemoteDesktopServer\`，不再在 MSI 的文件表里；第一次从旧布局升级时，迁移动作在
  `RemoveExistingProducts` 之前把旧文件复制到新位置。升级不再把空密钥/空数据库模板写回，Relay 不会再因
  `Relay:SharedKey` 为空而启动失败，Rendezvous 也不会退回内存数据库。
- **真实 major upgrade 会以 MSI error 2613 失败**：`RemoveExistingProducts` 必须是 `InstallInitialize` 之后的
  第一个动作；原先迁移动作排在它前面，REP 被推到 1503，Windows Installer 直接拒绝升级。client/host 的
  `MigrateClientSettings`、server 的 `SecureConfigDirectory`/`MigrateLiveSettings` 改为 immediate 并在
  `InstallInitialize` 之前执行；`RemoveExistingProducts` 回到 1501。包内容测试现在核对这个顺序。
- **deferred 自定义动作读不到普通 MSI 属性**：host 的 `.fixed-password` 删除、server 的 `ConfigureServer`/
  `SyncLocalNodes`/`CleanupServer` 在 deferred 阶段访问 `session[...]` 会抛 “Cannot access session details
  from a non-immediate custom action”。host 改为按固定路径删除；server 新增 immediate 的
  `PrepareServerData`，把值以 base64 的 `name=value` 放进 `SERVERCONFIGDATA`，再由三个 `Set...Data` 动作
  交给 deferred 动作；`HideTarget` 与 `Hidden` 保证密钥不进 MSI 日志。
- **server 安装的自定义动作把带空格的安装目录拼成非法参数**：`ProcessStartInfo.Arguments` 里
  `"C:\Program Files\RemoteDesktopServer\"` 的尾部反斜杠转义了闭合引号，子进程把后面的命令当成路径的
  一部分，`--configure-system` 因此失败并回滚。现在 `Quote` 按 CommandLineToArgvW 规则加倍尾部反斜杠，
  `SERVERINSTALLDIR`/`SERVERCONFIGDIR` 去掉尾分隔符后再传给子进程。
- **client/host MSI 升级会把服务器地址恢复成构建默认值**：live 地址改到
  `%ProgramData%\RemoteDesktop\appsettings.json`（`MachineSettings` 的 Windows 层），安装目录里的
  `appsettings.json` 只是随包默认值；首次升级同样有迁移动作。安装期 `RENDEZVOUSURI` 只在首装生效，
  升级保留操作员改过的地址。
- **安装期属性在升级时被静默忽略**：文档明确“首装生效，升级保留”，与 Linux/macOS 的升级行为一致；
  server 的 `Set-ServerConfiguration.cmd`、client 的「修改服务器地址」是升级后的入口。
- **Ops 控制台装上了却没有节点、信令没有 `/metrics`**：server MSI 的配置动作现在传 `--metrics-enabled`、
  在首装后执行 `--seed-local-nodes`、在 TLS 生成时 pin `tls\signaling.cer`，升级时 `--sync-local-nodes`；
  与 Linux/macOS 的 postinstall 对齐。
- **部署秘密进 MSI 日志与进程命令行**：server 的 relay key、PostgreSQL 连接串、TLS 口令改由自定义动作通过
  子进程环境变量传递，动作加 `HideTarget`、属性加 `Hidden`；host 固定密码改用
  `--set-fixed-password-file` 和 SYSTEM/Administrators 可读的临时文件（与 Linux 包一致），不再出现在
  `RemoteDesktop.HostService.exe` 的命令行里。
- **自动生成的自签证书没有部署用的 DNS/NAT 名**：新增 `TLSCERTIFICATEHOSTNAME` 属性与安装页字段，
  `Set-ServerConfiguration.cmd -TlsHostName` 同样支持；名字写进证书 SAN，Ops 的本地节点 URL 也用它。
- **server 卸载残留数据库/证书/日志**：新增 `REMOVEDATA=1`，删除 `%ProgramData%\RemoteDesktopServer`
  与证书存储里生成的证书；默认保留（重装后设备码不变）。文档列全三类残留。
- **`Set-ServerConfiguration.ps1` 改数据库不重启 Ops**：现在重启三个服务，并在写完后登记/同步本机节点；
  `-WhatIf` 不再打印口令。
- **server 配置目录对本机用户可读**：`%ProgramData%\RemoteDesktopServer` 在写入前由自定义动作收紧为
  SYSTEM/Administrators 完全控制，relay key、PostgreSQL 口令、TLS 口令与 Ops 库不再对普通用户可读。
- **server 包在图形界面安装时提前失败**：立即自定义动作以安装用户身份运行；把配置目录的 ACL 收紧到
  SYSTEM/Administrators 后，同一进程紧接着的枚举（以及首次升级的迁移）失去访问，安装回滚，只留下一个空目录。
  现在立即动作先创建目录并保留安装用户的临时访问，迁移完成后由 deferred、以 SYSTEM 运行的
  `SecureConfigDirectoryFinal` 收紧 ACL；静默安装与从资源管理器双击的图形安装走同一条路径。
- **Ops 本机信令节点被登记成机器名，节点抓取和设备管理返回 401**：没有 `NodeToken` 时，信令服务端的
  `/metrics` 和 `/internal/*` 只授权回环访问；安装器在 TLS 下把 `rendezvous-local` 写成
  `https://<机器名>:5080`，Ops 从局域网地址调用得到 401，控制台显示“抓取失败”、设备页为空。现在两个本机节点
  固定登记为 `127.0.0.1`；证书仍通过 `NodeCertificatePath` 固定，不依赖主机名。已有安装可在 Ops 节点页把
  地址改为 `https://127.0.0.1:5080`，或安装新的 server 包让升级动作同步。
- **Ops 管理员命令与重启服务未在文档中强调管理员 PowerShell**：普通窗口执行 `--create-admin` 读不到受限的
  配置/数据库，`Restart-Service` 会报“无法打开...服务”；文档已注明。
- **升级后 Relay 的 `SharedKey` 为空，中继票据被拒**：升级跳过 `ConfigureServer`，旧包留下的
  `Relay\appsettings.json` 不会补写 `SharedKey`，而 `Rendezvous` 已经有 `RelaySharedKey`。现在升级动作
  `SyncLocalNodes` 会用不带地址/TLS/数据库选项的 `--configure-system` 同步两个密钥（并重新开启信令
  `/metrics`），不改动操作员的其他配置，也让中继能校验票据；升级冒烟测试会先清空 Relay 密钥再断言恢复。
- **证书确认页显示 `auto` 而不是刚选的文件**：确认页改显示 `TLSCERTIFICATEFILE`；选择“已有证书文件”却没选
  文件时会弹出提示，不再静默安装成明文 http。
- **Ops CLI 不展开 `%ProgramData%`**：`--create-admin` / `--seed-local-nodes` 现在展开环境变量，并按
  `%ProgramData%\RemoteDesktopServer\Ops` 读取与安装版相同的配置。
- **WebSocket 地址不带 `/ws` 时连到 `/`**：`RendezvousAddress` 把空路径补成 `/ws`，反向代理使用的
  `wss://host/rd/ws` 之类路径原样保留；构包、安装期与启动期使用同一条规则。
- **Windows 安装包在默认 PowerShell 5.1 下构建失败**：PDB 删除改为按路径删除（5.1 的管道 `Remove-Item`
  会抛 `InputObjectNotBound`）；`Set-ServerConfiguration.cmd` 用 `-ExecutionPolicy Bypass` 绕开 Windows 11
  客户端默认的 Restricted 策略。
- **文档与行为不一致**：修正“升级不会覆盖 appsettings”“服务器地址以安装包里的为准/手改会保留”、
  ReadyToRun、构建产物命名、残留进程关闭顺序等句子。

**并发、资源释放与性能（评审 16 处）**

- **WebSocket 发送串行化**：连接请求、心跳与会话结束报告三条发送路径现在共用同一把发送锁（`ClientWebSocket` 不允许并发
  `SendAsync`，此前三者可能在帧中间交错）；重连循环释放套接字的瞬间发送，按"连接丢失"处理而不是抛出。
- **信令服务器的待处理连接请求上限原子化**：总量上限、单设备上限与插入在同一把门闩下完成，两个并发请求不再能同时看到
  空位而越过上限；单设备计数随表一起维护，不再每次遍历全表。
- **固定密码的校验耗时不再泄露"是否设置了固定密码"**：没有固定密码时也做同样的 PBKDF2 运算（以空串对比，永远不会匹配）。
- **资源释放与并发**：PipeWire 帧与原生句柄泄漏、`DxgiScreenCapturer` 的 COM 对象泄漏、会话事件处理器的异常隔离、
  共享采集与查看器的锁内拷贝，以及小帧串行化等修复，涉及 Application、Transport、Features、Media、Platform、
  Server 共 15 个文件。
- **性能**：`UdpRelay` 查"端点是否已绑定到别的会话"改为反向索引而不是扫描所有配对；终端模拟器的 O(n) 路径同步优化
  （见下一条）。

**终端缩放与文本提取的开销**

- **`ResizeRows` 不再 O(n²)**：缩小时用一次 `RemoveRange` 代替循环 `RemoveAt(0)`，放大时先从回滚区取回再一次
  `InsertRange`（取回的行按原顺序，最旧的在上）；`GetText` 复用同一个 `StringBuilder`；OSC 解析用 `CollectionsMarshal.AsSpan`
  直接解码，不再为每条 OSC 复制一份缓冲。

**解码器静默丢帧时自愈重建**

- **控制端画面卡住但解码器没报错**：被控端仍在送非关键帧、解码器却持续不出图（失去同步后静默丢弃一切）时，
  `DesktopViewer` 在距上一幅画面超过 2 秒时直接重建解码器并请求关键帧，不再只请求关键帧后等看门狗的
  2/4/8/16/30 秒退避。日志里会有一条 Warning "The decoder stopped producing pictures while being fed"。
- **摄像头同类问题**：`CameraViewer` 加入同样的 2 秒判定与重建。摄像头画面不会像桌面那样合法地长时间不变，
  所以这里无出图即视为停滞；此前看门狗会一直等一个永远不会被请求的关键帧。

**被控端日志及时落盘；Agent 日志按桌面类型分文件**

- **日志不再滞留在缓冲区**：`service-.log` 与 Agent 日志都按 1 秒周期落盘，卡流时诊断需要的编码器/采集日志在进程仍
  运行时就已经在磁盘上。`service-.log` 只有一个进程写入，去掉了 `shared`（按代码注释，shared 的 sink 会忽略落盘
  间隔）。
- **Agent 日志文件名改为 `agent-{desktop}-<日期>.log`**：用户桌面与登录/UAC 安全桌面的 Agent 分别写
  `agent-user-*.log` 与 `agent-secure-*.log`，因此不再需要 `shared`；保留 7 天不变。

**Linux：会话总线连不上时按"没有门户"报告，而不是抛传输层异常**

- **`PortalConnection` 把会话总线的连接失败包成 `PortalException`**：此前只有"根本没有会话总线"被包装，地址还在但连不上
  （会话崩溃后遗留的 `DBUS_SESSION_BUS_ADDRESS`）会原样透传 D-Bus 库的 `DBusConnectFailedException`。按 `PortalException`
  判断"这台机器没有门户"的调用点因此漏掉它——`--self-check` 在那种机器上直接崩溃，而不是报告门户不可用。摄像头授权的
  错误映射保持 `InvalidOperationException`（文案改为"桌面没有接受摄像头的授权/撤销"），窗口与主机控制通道继续把它显示给用户。

**三平台打包与 CI：从"每个 job 都红"到九个安装包全部构建通过**

这一轮把发布流水线本身修通了：`ci.yml` 的五个 job 在同一个提交上全绿，`release.yml` 的四个打包 job 才有意义。

- **Windows 安装包的自定义动作在 Linux/macOS 上必然还原失败**：两个 `net48` 的 CustomActions 工程只靠 SDK 隐式加
  `Microsoft.NETFramework.ReferenceAssemblies`，而 SDK 只在没有 .NET Framework targeting pack 的机器上加——Windows
  生成的 `packages.lock.json` 因此缺这一项，Linux/macOS 的 locked restore 以 NU1004 失败。显式引用该包后包图三平台一致。
- **`THIRD-PARTY-NOTICES.md` 随 runner 的运行时版本漂移**：脚本读 `dotnet --list-runtimes`（机器上装了什么就是什么），
  runner 镜像升到 10.0.15 后文件在没有任何依赖变化的提交上被改写。改读 SDK 的 `BundledNETCoreAppPackageVersion`
  （SDK 由 `global.json` 锁定），与自包含发布实际带走的版本一致。
- **容器构建的 SDK 镜像与 `global.json` 脱节**：四个 Dockerfile 钉在 `sdk:10.0` 的同一个 digest 上，而该镜像已升到
  10.0.403，`rollForward: disable` 让镜像里的还原直接报 "A compatible .NET SDK was not found"。改用带版本号的 10.0.402。
- **健康检查断言与服务端的实际响应不符**：中转与监控台的 `/health` 返回 JSON（监控台还带探测到的数据库类型），只有
  信令服务器回答纯文本 `Healthy`；同时 Linux 的 deb 包默认在 TLS 问题里选"生成自签名证书"，信令监听的是 https。
  三平台 CI 与 Windows 安装后自检都按纯文本 + http 比较，于是分别在中转与信令上失败。断言改为两种形状都认，Linux 走 https。
- **macOS 服务端安装在中途中止**：postinstall 的"专用账号能否读到服务器需要的东西"探针调用 `/usr/bin/test`，而 macOS
  把 test 放在 `/bin`——`sudo` 回一句 `command not found`，每条探针都以失败告终，安装报 "could not prepare the
  unprivileged _remotedesktop account"。安装失败现在还会打印 `/var/log/install.log` 尾部，`installer` 本身只说一句
  "an error occurred while running scripts"。
- **`osx-arm64` 的锁文件缺失**：`.pkg` 的 locked restore 与 `.deb` 一样需要按 RID 分开的锁文件。
- **macOS 的空摄像头 id 报成"摄像头未连接"**：调用方传了空 id 时按硬件问题报错，排查会找错方向；改为与 Linux 工厂
  一致地抛 `ArgumentException`。
- **过长的控制管道名会让客户端崩溃**：`LinkActivation.TryForward` 没有接住"名字超出套接字路径上限"的
  `ArgumentOutOfRangeException`（macOS 的临时目录很长），一次转发尝试就终止进程；现在与其它几种情况一样按"没有运行中的
  客户端"处理，正常启动。
- **测试侧**：本机控制管道的授权判定拆成 `IsAuthorizedHostControl` 并用六条用例覆盖（管理员账号的 runner 走不到拒绝分支）；
  汇总用例改用当前分钟对齐的时间戳（2023 年的固定时间戳会被 30 天保留策略在写入途中删掉）；伪终端的 shell 交互用例
  限定在 Linux（托管 macOS 的登录 shell 启动期会丢弃输入行，ioctl 往返等确定性用例在两个系统上仍运行）。
- **缺 macOS 签名凭据时的发布行为**：`release.yml` 新增 `check-signing`——Developer ID 证书或公证凭据不全时
  `build-macos` 不构建（绝不产出未签名或未公证的 `.pkg`），发布只含 Windows 与 Linux 的六个包，发布说明第一段
  写明少了哪个平台、为什么，以及补齐哪些 Secret 后重跑即可。真正失败的构建仍然拦下整个发布。
- **发布资产清点漏掉 Windows 的三个 MSI**：发布 job 里三个变量混着 `FileInfo` 与 `FullName` 字符串两种形状，
  `$windows.FullName` 静默成了空数组——三包清点通过，`gh release create` 却只上传了 Linux 的三个包和一份缺项的
  `SHA256SUMS`。统一成 `FileInfo`，并加了一条"资产数必须等于 6 + macOS 包数"的断言，让这种错在发布前就红。

## [1.0.0] - 2026-10-23 · 跨平台正式版：三平台九个安装包与运维控制台

首个正式版。自 0.3.0 起，产品从 Windows 单平台扩展到 Windows、macOS (Apple Silicon) 与 Linux，三个平台各出
控制端、被控端与服务端三个安装包；服务端补齐 SQLite/PostgreSQL 可插拔存储与 Rendezvous + Relay + Ops 的
完整交付形态。下面按主题记录这一轮的全部变更。

### 上线前评审修复（2026-10-23）

发布前的全量审查发现了下面这些问题，都在这一轮修复；其余已记录、但需要产品决策或上游修复的问题见
`docs/design/README.md` 的"还没有做的事"。

#### Security

- **控制端下载时，远端目录名可以写出目标目录**：递归下载把被控端返回的目录名直接 `Path.Combine` 到本地
  目标目录，而本地文件访问策略是 `Unrestricted`。一个恶意被控端把目录叫作 `../../.ssh`，就能让控制端在
  用户目录里建目录并写入文件（例如 `authorized_keys`），进而取得控制端账号的代码执行。现在每个远端名字
  （文件和目录）都先过 `FileAccessPolicy.NormalizeName`，绝对路径、`..` 与分隔符在拼接前被拒绝。
- **被控端会话表的"查重 + 插入"不是原子的**：两个同时通过认证的连接可以声明同一个 `Hello.session_id`，
  第二个覆盖第一个，第一个会话仍在运行却不在 `ActiveSessions` 里，降级/断开/运维控制台都够不到它，
  `MaxSessions` 也挡不住。现在容量检查与 id 预留放在同一把锁里，id 冲突改用新 id，会话结束按
  （id, 实例）成对移除。
- **Ops 登录锁定可以靠用户名拼写绕过**：账户查找会 trim + 转大写，锁定键却用原始字符串，
  `admin` / `Admin` / `admin`（末尾带空格）各自有一份失败预算。现在两边用同一个规范化键。
- **Ops 二次验证没有失败预算**：知道密码的人可以无限次 `/login` 换新 challenge，每个 challenge 再试 5 次
  六位码。新增一个与密码预算分离、跨 challenge 与成功密码都不清零的 TOTP 失败预算。
- **信令默认信任本机回环代理的 `X-Forwarded-For`**：同机 nginx/Caddy 转发的请求可以自带任意来源地址，
  按 IP 的限流与注册预算因此失效，构造 `127.0.0.1` 还能碰到未配置 `NodeToken` 时的回环兜底。默认信任已
  移除，只有 `Rendezvous:TrustedProxies` 列出的代理才被采信；文档的 nginx 示例补上 XFF，并说明只代理
  `/ws` 与 `/health`。
- **固定密码升级后仍接受低于 8 位的旧值**：`Load` 现在拒绝低于 `MinimumLength` 的存量密码，把它当作未设置；
  操作员重新设置时走已有的长度校验。
- **Windows 文件传输接受 UNC 路径**：未配置共享根时，`\\attacker\share\x` 会让被控端以登录用户身份向任意
  SMB 服务器认证（泄露 NetNTLM 哈希）。现在没有配置 UNC 根时一律拒绝网络共享路径。
- **UIAccess Agent 继承了用户的环境块**：标准用户可以在自己的环境里设置 `DOTNET_STARTUP_HOOKS`，
  .NET 宿主会在 `Main` 之前执行它——而 Agent 是以 UIAccess 启动的，于是用户代码进入了本应突破 UIPI 的那个
  进程。现在服务重建环境块，去掉 `DOTNET_*` / `CORECLR_*` / `COMPLUS_*` / `COREHOST_*`，PATH 与 COMSPEC
  改用机器级值。
- **Windows 数据目录的 ACL 只在"目录不存在"时设置**：`%ProgramData%` 默认允许普通用户创建子目录，先占住
  `RemoteDesktop\secrets` 的用户会让服务跳过加固，机器级 DPAPI 密文随后落在该用户控制的目录里。现在每次启动
  都校验并重设 ACL，拒绝重解析点；无法加固时服务启动失败而不是接受。
- **macOS 签名/公证构建缺少摄像头 entitlement**：硬化运行时下 AVFoundation 会拒绝采集，而 ad-hoc 开发构建
  看不出问题。两个 app 使用的 entitlements 加入 `com.apple.security.device.camera`。
- **Linux 客户端重启了一个不存在的 systemd 单元**：改服务器地址后调用 `systemctl --user restart
  remotedesktop-host`，实际单元名是 `app-RemoteDesktopHost.service`，失败被吞掉。现在用正确的名字。
- **Linux 控制 socket 的 /tmp 回退目录不校验属主与权限**：`CreateDirectory(mode)` 不会收紧已存在的目录；
  现在显式设置 0700（非属主会失败）并拒绝符号链接。
- **macOS 被控端崩溃后不会重启**：launchd 作业的 `KeepAlive` 从 `false` 改为 `SuccessfulExit=false`，
  异常退出后 10 秒节流重启；正常退出不受影响。
- **控制管道读取没有超时**：Windows 命名管道是全局命名空间，另一个本机进程可以占住同名实例不回应，客户端
  启动会一直等。现在连接后 5 秒无应答即失败。
- **手动安装脚本升级时会覆盖配置、缺少防火墙规则**：`Install-HostService.ps1` 不再覆盖已有的
  `appsettings.json`，会先结束残留的 Agent/客户端进程，并补上与 MSI 相同的程序级 UDP 入站规则。

#### Fixed

- **`Ops:RequireTotp=false` 时登录页永远进不去**：`/login` 已经不产生 challenge，却没有写入会话 cookie，
  前端显示已登录、之后每个 `/api` 请求都是 401。现在 `/login` 拿到 session 时同样设置两个 cookie。
- **审计链在两条记录同一毫秒时会误报被篡改**：追加按 `(ts DESC, id DESC)` 取前一条，校验却按
  `(ts ASC, id ASC)` 走，随机 id 可能让新记录排到前一条之前。现在追加时保证存储时间戳严格递增。
- **管理动作在节点调用抛异常后永久卡在 approved**：DNS 失败、超时或连接拒绝时 `CompleteActionAsync` 从未执行，
  重试又只接受 pending。现在所有结果（包括取消）都会写入终态；关闭行时不再使用可能已取消的请求 token。
- **`/ingest/events` 对 chunked 请求先缓冲再查长度**：没有 `Content-Length` 时能把整个请求读进内存再拒绝。
  现在边读边计数，超过 1 MiB 立即 413。
- **被控端/客户端会被越界的远端数值打成崩溃**：信令 `Registered` 的设备号与反射端口、中继端点端口、
  视频帧时间戳、文件修改时间都来自对端。现在在构造 `DeviceId` / `IPEndPoint` / `DateTimeOffset` 前校验，
  非法值按协议错误处理，而不是把传输线程（以及整个宿主服务）带走。
- **摄像头流信息可以要求 1 GiB 帧缓冲**：上限从 16384×16384 收到 8K（8192 边长、7680×4320 像素），与桌面流
  一致。
- **终端中间序列缓冲无上限**：被控端程序持续输出 `ESC [` + 0x20–0x2F 就能把控制端堆撑爆；现在与 OSC 一样
  上限 4096 字节，超限按忽略序列处理。
- **每个会话可以无上限地开通道**：每个通道都占一份帧缓冲。现在每会话最多 24 个并发通道，超出的通道直接关闭。
- **QUIC 入站请求无并发上限**：每次连接请求都会新建一个 QUIC listener 与 UDP socket；现在同时准备的上限为
  32，接受队列有界，满了丢弃而不是无限排队。listener 本身每次约 1.2 MB 的上游原生泄漏见
  `docs/design/README.md` 的"还没有做的事"，1.0.0 没有解决它。
- **Windows MSI 升级/修复会覆盖已配置的服务器地址 / 监听地址与 TLS**：`ConfigureSystem` 在升级与修复时也会运行，把
  `NeverOverwrite` 保留的 `appsettings.json` 重写成安装包默认值（客户端回到 `ws://127.0.0.1:5080/ws`，
  服务端回到明文 `http://0.0.0.0:5080`）。现在它只在全新安装时运行；升级保留操作员配置。
- **Windows MSI 从 0.3.0 升级后丢失 `appsettings.json`**：major upgrade 移除了旧包的设置文件，而新包的同 GUID
  组件被跳过，服务于是回退到 `ws://127.0.0.1:5080/ws`，日志每 30 秒报一次 `Rendezvous connection lost`，设备
  永远不再注册。MSI 现在随包携带 `appsettings.default.json`，并新增 deferred action `RestoreDefaultSettings`：
  安装、升级或修复后如果 `appsettings.json` 不存在就从默认文件恢复；已存在的配置不会被覆盖。
- **第三方组件清单过期且漏掉运维控制台**：Dependabot 升到 10.0.13 / NAudio 3.2.0 后清单仍是 10.0.12 /
  3.1.0，生成脚本也没有遍历 `RemoteDesktop.Server.Ops`。已加入 Ops 并重新生成。
- **安装包不带许可证与第三方声明、Linux/macOS 还带 PDB**：三个 `.deb` 现在安装
  `/usr/share/doc/<包>/copyright` 与声明；macOS 客户端/被控端 app 内带 `LICENSE.txt` 与声明，服务端载荷带
  `/usr/local/share/doc/remotedesktop-server/`；Windows MSI 把两份文件装进安装目录；Linux 与 macOS 包去掉
  `*.pdb`（Windows 原本就去）。
- **发布前的版本与变更日志**：`<Version>` 升到 1.0.0，21 个 `[未发布]` 小节收进本版本；文档与模板里过期的
  `0.3.0` / `0.4.0` 示例一并更新。

#### Added

- **Linux 安装包可按部署定制**：客户端与被控端的 `build-deb.sh` 支持 `RD_RENDEZVOUS_URI` 与
  `RD_SERVER_CERTIFICATE`。前者写进包内的 `Transport:RendezvousUri`，后者通过 postinst 的
  `update-ca-certificates` 装进系统信任库，卸载时由 postrm 移除；这样部署自签名 TLS 的服务器不用每台机器
  手工导入证书。

### Windows 端到端测试

在 Windows 11 + NVIDIA 上以 Docker（rendezvous + relay + ops + PostgreSQL）跑真实用户流程：直连与中继、终端、
文件传输、服务端容器重建、摄像头、观看模式，以及屏幕与摄像头录制。发现并修复：

#### Fixed

- **中继空闲回收覆盖客户端的结束时间**：`session.ended` 先到时，约两分钟后的 `session.relay_closed` 会把
  `endedAt` / `endReason` 改成更晚的 idle；现在只合并字节数，结束时间取最早、原因保留第一次。`SessionFailed`
  同样不再把已结束的会话往后推。

### Docker 部署评审修复

服务端的三条部署路径里，Docker 是唯一没有被 CI 构建过的一条。一次针对 `build/docker/` 的评审同时发现了两个
启动级阻断（compose 文件不是合法 YAML、中继镜像缺少 ASP.NET 运行时）、一个默认公网可达的未授权管理接口，以及
一条文档承诺但实际起不来的 SQLite 路径。这一轮修复连同同批发现的部署隐患一起处理，并给这条路径补上 CI 的
构建与启动冒烟测试。

#### Fixed

- **compose 文件不是合法 YAML**：`POSTGRES_PASSWORD` 的必填提示里有一个冒号加空格，未加引号的普通标量让它变成了
  映射，`docker compose up` 在解析阶段就失败。改为单引号标量。
- **中继容器起不来**：`Dockerfile.relay` 基于 `dotnet/runtime`，而中继是 ASP.NET Core 宿主（`/health`、
  `/metrics`、`/internal/v1/info` 都是 Kestrel 端点），发布产物要求 `Microsoft.AspNetCore.App`，容器以退出码
  150 结束。基础镜像改为 `dotnet/aspnet`。
- **内部管理接口默认公网可达**：`/internal/v1/*` 与信令共用 5080，`NodeToken` 为空时守卫直接放行，任何人都能
  枚举 9 位设备码、封禁设备或断开会话。`NodeTokenGuard` 在未配置 token 时只接受回环调用；compose 要求
  `NODE_TOKEN` 并同时传给信令与中继。新增单元测试与真宿主集成测试。既有的跨机 Ops 部署如果没有配置 token，
  升级后会收到 401——按 `docs/ops/README.md` 在三个进程上配置同值。
- **默认明文信令**：5080 现在默认只绑 `127.0.0.1`；新增 `docker-compose.tls.yml` + `Caddyfile`（自动证书，只
  代理 `/ws` 与 `/health`）。局域网明文直连需要显式设置 `RENDEZVOUS_BIND_IP=0.0.0.0`。
- **SQLite 单机方案起不来**：镜像以 UID 1654 运行，新建的 `/data` 卷属于 root，SQLite 报 `SQLITE_CANTOPEN`。
  信令与监控台镜像在 `USER` 之前创建 `/data` 并 chown；文档补上卷与依赖的移除步骤。
- **监控台没有 Docker 部署**：新增 `Dockerfile.ops` 与 `docker-compose.ops.yml`，复用 PostgreSQL（与设备注册表
  同库，表名以 `ops_` 开头）、带 DataProtection 卷、管理员创建命令、节点地址与事件签名密钥；5090 只发布到回环。
- **容器启动时的两条噪声日志**：rendezvous/ops 镜像补装 `libgssapi-krb5-2`，Npgsql 不再打印
  `Cannot load library libgssapi_krb5.so.2`；ops 的 DataProtection 密钥目录由镜像创建并挂到 `ops-dataprotection`
  卷，ASP.NET Core 不再警告密钥会随容器消失。
- **中继没有健康检查、日志无上限**：compose 给中继加 `/health` 探针，并给所有服务加 json-file 轮转
  （10 MB × 5）。
- **compose 的必填项与依赖关系**：`RELAY_SHARED_KEY`、`RELAY_PUBLIC_ENDPOINT`、`NODE_TOKEN` 改为 compose 级
  必填（缺失即失败，而不是带默认值跑起来）；去掉中继对信令的无意义 `depends_on`；数据库改用新键
  `Rendezvous:Database:Provider` / `ConnectionString`。
- **部署缺少必填值清单**：新增 `build/docker/.env.example`，集中列出四个必填变量及其生成方式。
- **`.env` 会进入 Docker 构建上下文**：`.env.example` 被复制成 `.env` 后，旧的 `.dockerignore` 没有排除它，里面
  的数据库口令、中继密钥与 node token 会随构建上下文发给远端 builder。`.dockerignore` 现在排除 `**/.env*`。
- **内部 API 的部署自查**：`Invoke-SecurityAudit.ps1` 在监听地址不是回环且 `NodeToken` 为空时报告不安全默认值；
  `docs/design/README.md` 的“部署者必须自己做的事”补上这一条。三个应用容器同时加了 `cap_drop: [ALL]`。
- **构建缓存说明与事实不符**：信令与中继的 Dockerfile 原本在 `dotnet publish` 前复制全部源码，注释却说
  restore 会被缓存。三个 Dockerfile 现在都用 BuildKit 的 NuGet cache mount，注释也改成真实行为。
- **开发配置进入发布物**：`appsettings.Development.json` 里有仓库公开的固定中继密钥，`dotnet publish` 会把它
  带进安装包与镜像。两个服务端工程的发布输出现在排除该文件。
- **中继对非法 base64 密钥抛原始 `FormatException`**：改为与信令相同的、说明怎么生成的错误消息。
- **PostgreSQL 基础镜像不必要**：compose 从 `pgvector/pgvector:pg18` 改为官方 `postgres:18`（代码没有使用任何
  向量扩展）。
- **`docs/ops/README.md` 的限额表过时**：`MaxConnectionsPerAddress` 从 64 改为代码实际的 256，
  `RegistrationBurst` / `RegistrationRefill` 从 8 / 30 秒改为 256 / 5 秒；Docker 小节补齐 TLS、
  `NODE_TOKEN`、`TrustedProxies`、备份、Ops overlay、Docker 防火墙行为与 SQLite 步骤。
- **Docker 路径没有 CI**：`ci.yml` 新增 `docker` job，解析三个 compose 配置、构建三个镜像，并启动信令与中继
  检查 `/health` 与内部接口的 401/200 行为。

### 版本设置评审修复

版本只有一个来源（根 `Directory.Build.props` 的 `<Version>`），但它在产物名、程序集、打包脚本与文档里派生出了
三种拼写和两套顺序。这一轮把派生规则收敛成两件事：**包与界面用三段，遥测用带提交哈希的完整串**；构建产物名
三平台统一为 `<产品>-<角色>-<平台>-<架构>-<版本>`。发布流程同时补上了机器检查——此前 Tag、包内版本与
CHANGELOG 三者是否一致，只靠人记得。

#### Fixed

- **下一次打 Tag 会在“找 MSI”这步失败**：两个 wixproj 的 `OutputName` 是 `<平台>-<版本>-<架构>`，而
  `release.yml` 与 `verify-packages` 按 `<平台>-<架构>-<版本>` 找——命名统一的改动（`fe48153`）不在 `v0.3.0`
  里，所以这条路径从没跑过一次发布。Windows 三个包改为架构在前、版本在后，与 macOS/Linux 一致，现有 glob 即可
  匹配。Windows 的构建产物名从 `RemoteDesktop-Client-Windows-0.3.0.0-x64.msi` 变为
  `RemoteDesktop-Client-Windows-x64-0.3.0.0.msi`（被控端、服务端同理）；发布资产名不变。
- **Tag 与包内版本可以各说各话**：`release.yml` 新增 `check-version` job，核对 `GITHUB_REF_NAME == v<Version>`
  且 `CHANGELOG.md` 有 `## [<版本>]` 小节；三个打包 job 都依赖它。`docs/ops/README.md` 的发布步骤补上了
  “先升 `<Version>`、把 `[未发布]` 收成版本小节、再打 Tag”，并写明预发布版本无法用 MSI 表达。
- **运维控制台的版本号写死在 `index.html`**：新增匿名端点 `GET /api/version`（读程序集），侧栏改为运行时读取；
  顺带去掉了过期的“阶段 M1”。
- **Docker 镜像 tag 写死 `0.3.0`**：默认改为 `dev`，用 `REMOTEDESKTOP_VERSION` 指定；两个镜像各加
  `org.opencontainers.image.version` 标签，随构建参数写入。
- **macOS 客户端 `Info.plist` 的版本是字面量**：改为 `__VERSION__`，与 host 用同一套替换，仓库里的文件不再
  假装自己是一个已发布的版本。
- **文档与测试里的版本示例会过期**：`docs/ops/README.md`、`build/signing/README.md` 改成 `<版本>` 占位符；
  遥测“产品真实版本可通过”的用例改为读取 `ProductVersion.Full`，不再拿写死的 `0.3.0+…` 当样本。
- **同一产品三种版本拼写**：新增 `RemoteDesktop.Application.ProductVersion` 与
  `RemoteDesktop.Server.Shared.ProductVersion`（客户端与服务端各一份，因为两侧不共享工具程序集；
  `ProductVersionTests` 断言两份一致）。界面侧栏、服务端节点信息用 `Display`（三段），遥测用 `Full`（带提交
  哈希）；`InstallerPackageTests` 新增断言，MSI 的 `ProductVersion` 必须等于 `<Version>` 加 `.0`。
- **macOS 会在读不到版本时静默产出 `0.0.0`**：五个 macOS 脚本都有 `VERSION:-0.0.0` 回退，而 Windows 会抛错、
  Linux 会退出；一个 props 路径变化就可能发出一个“合法”的 0.0.0 包。新增 `build/installer/version.sh`，八个
  macOS/Linux 打包脚本统一 source 它，空值、三段数字与 MSI 数值上限都在这里失败；Windows 的两个
  `Get-ProductVersion` 也补上了数值上限检查，`check-version` 复用同一份规则。
- **macOS/Linux 的安装包版本没有在 CI 里核对**：`verify-packages` 的 Linux 分支现在比对 `dpkg-deb` 的 `Version`
  与文件名，macOS 分支展开 `.pkg` 后比对每个 component 的 `PackageInfo`，并检查 app bundle 的
  `CFBundleShortVersionString` / `CFBundleVersion`；Windows 的 MSI 版本由既有测试覆盖。一个上次构建留下的包
  不再能混进来。
- **Windows 文件属性的“产品版本”带着提交哈希**：Explorer 读的是 `AssemblyInformationalVersion`，于是
  `0.3.0+<sha>` 出现在一个给人看的字段里。提交哈希改为写进 `AssemblyMetadata("Commit", …)`，
  `AssemblyInformationalVersion` 保持三段；`ProductVersion.Full` 在运行时把两者拼回 `0.3.0+<sha>`，遥测不变，
  文件属性、UI 与包内版本都是三段。macOS 的 `CFBundleVersion` 与营销版本同值这一决定也写进了两个 `Info.plist`
  的注释（Developer ID 分发不受 App Store 的构建号规则约束）。
- **`<版本>` 在三平台文档里会串**：`docs/ops/README.md` 增加版本形态表，明确 Windows 四段、macOS/Linux 三段、
  发布资产用 Tag；`build/README.md` 说明 `version.sh` 的位置与职责。

### 运维监控台：端到端测试

在 Windows 11 上把控制台放进真实部署里跑：Rendezvous、Relay、Ops 三个进程，真实 SQLite，两个真实客户端分别走
直连与中继，事件用真实 HMAC 通道，Webhook 用本机接收器；页面用 Edge（Playwright）逐页操作。API 侧 96 项、
浏览器侧 34 项断言全部通过，并发现、修复了下面七处。

#### Fixed

- **直连会话在控制台变成两行**：控制器在直连路径构造 `QuicPeerConnection` 时没有带上服务端签发的
  `RelaySessionId`（中继路径带了），控制端心跳因此无法归并到信令行，控制台为同一次会话多出一行只有质量数据的
  记录。直连路径现在同样携带该 id；`PeerSessionTests` 断言两端拿到同一个 id。
- **客户端心跳全部被拒**：`SessionTelemetry.MaxVersionLength` 是 32，而产品自身的
  `AssemblyInformationalVersion` 是 `0.3.0+<40 位提交哈希>`（46 字符），于是每一份心跳都以
  “version is not a product version” 被丢弃——会话质量、平台分布永远为空。上限改为 64，并补了按真实版本串
  通过的用例。
- **中继空闲会话永远不回收**：`UdpRelay` 只在收到下一个包之后才清理，两个对端都静默时配对一直留在表里，
  `relay_closed` 永不发出，控制台的“中继会话”与配对表一直显示活跃。接收循环现在每个清理周期都会醒来执行
  一次清理；新增“无后续包的空闲配对也会被回收”的回归用例。
- **删除节点后告警永久悬挂**：抓取失败告警的去重键带节点名，节点被删除后求值时不再产生这个信号，
  状态机就再也无法把它恢复。求值现在会收尾所有“本轮没有信号的未恢复告警”（节点被删、规则被停用或删除），
  照常写审计并发送恢复 Webhook；新增删除节点即恢复的集成用例。
- **`OpsEvents:Endpoint` 按文档填根地址时事件全部丢失**：文档与安装包都写 `http://127.0.0.1:5090`，
  而发送端按原样 POST，控制台只在 `/ingest/events` 接收，结果每次都是 HTTP 405、重试三次后丢弃。
  发送端现在在端点没有路径时补上 `/ingest/events`，两种写法都可用；新增按文档根地址投递的集成用例。
- **直连会话断开后一直显示“进行中”**：中继会话有 `relay_closed`，直连会话没有任何结束事实。信令新增
  `ClientMessage.session_ended`（字段 7），`SessionLifecycle` 在两端会话收尾时各上报一次——**不受心跳开关
  影响**；信令服务校验、按设备限流后发布 `session.ended`，投影取最早的结束时间并保留第一次原因。
  另补了投影、入库与客户端上报的单元/集成用例。
- **控制台没有 favicon**：浏览器每次打开都记一条 `/favicon.ico` 404。控制台现在带产品图标
  （`wwwroot/favicon.ico`），`index.html` 明确引用；端到端断言该路径返回 200。

#### Notes

- 真实场景覆盖：登录（错误口令、错误动态码、TOTP）、三角色权限、节点登记/抓取/停用/删除、签名事件与重放、
  会话投影与详情、告警开/认领/恢复/Webhook、两段式断开与封禁/解封、设备代理与分页、审计链篡改检测、
  深浅主题、退出登录，以及真实客户端直连/中继两种会话（直连会话关闭后确认行变为已结束、原因为 `closed`）。
- 全量 `dotnet test` 中 `MediaFoundationCodecTests.Hardware_encoder_round_trips_through_the_decoder`
  仍在本机因 Intel QSV `E_UNEXPECTED` 失败（与上一轮 Windows 端到端记录一致，属环境/驱动问题，与本轮改动无关）；
  其余项目全部通过。

### Windows UI 与产品图标评审

在 Windows 11 Enterprise 26200 上对客户端界面与产品图标做逐屏评审（深浅色各一轮、真实服务端与客户端），
并用 UI Automation、图标逐尺寸渲染与实际构建的最小 MSI 核对，第二轮逐项复核了修复结果。

#### Fixed

- **独立被控端模式的说明与事实相反**：被控端在本进程内运行时，界面仍说“本机控制端代码”与主页不同、
  主页显示的是后台服务代码；首帧也用默认文案，等轮询回来才纠正。托管模式现在由 `ClientEnvironment`
  传入，说明在窗口构造时即正确，并随主机状态刷新。
- **独立模式下“被控端信令服务器”是走不通的编辑入口**：界面提供修改/保存并承诺 UAC 与服务重启，
  实际报“找不到后台服务程序”。非服务托管时编辑入口不再显示，说明改为关闭客户端后改启动参数
  （`--Transport:RendezvousUri`）或 `appsettings.json`。
- **最近连接多于约 5 条时底部说明被压到状态栏下**：设备列表卡片改为 `Auto,*,Auto` 的 Grid，
  列表自己滚动，在线状态说明固定在底部。
- **数据目录路径过长时被裁切、复制按钮被挤出**：路径改为可换行，复制按钮固定在右列。
- **下拉框无障碍值与名称**：`CameraChoice`、`CameraModeChoice`、`ConflictChoice`、`PlaceInfo`
  的 `ToString()` 返回界面文字，不再让读屏念整条记录；快速访问、摄像头、分辨率、文件已存在时、
  连接方式与主导航补 `AutomationProperties.Name`。
- **MSI 没有产品图标**：client 与 server 包加入 `Icon`/`ARPPRODUCTICON`，client 开始菜单快捷方式
  显式引用 `AppIcon`；“设置 > 应用”不再回落到通用图标。新增
  `InstallerPackageTests.The_package_carries_the_product_icon`，用 Windows Installer API 查询
  `Icon` 表、`ARPPRODUCTICON` 与快捷方式图标，无安装包时跳过。
- **`.ico` 缺少 20/40px 槽位**：`icon.ico` 从 7 档补到 9 档（16/20/24/32/40/48/64/128/256，均为 32bpp
  PNG 压缩），20px 沿用 16/24 的简化图形、40px 沿用完整图形；Windows 在 125%/150% 缩放下直接取这两档，
  不再由 shell 缩放。`build/README.md` 说明这两档只存在于 `.ico`，不单独导出 PNG（Linux hicolor 按
  `icon-*.png` 遍历）。
- **浅色下隐藏文件元数据对比度不足**：不再对整行应用 50% 透明度，只淡化图标与文件名，大小和日期保持
  不透明；浅色下这两列恢复到 tertiary 文字的 4.82:1，隐藏状态仍能从图标与文件名看出。

#### Notes

- 图标主体占比 89% 在 Windows 上不构成问题，第一轮 R3 的 macOS 栅格决定仍待定。
- 四段版本号 `0.3.0.0`、浅色主题下终端保持深色：均为既有观察，本轮未改。评审为深色 12 张、浅色 11 张
  截图与 UIA 读取，未实际安装 MSI，也未覆盖混合 DPI。

### Windows 多显示器端到端测试

在 Dell Latitude 5420 / Intel Iris Xe（驱动 32.0.101.7092）上接两块显示器：DISPLAY2 2560×1440 主屏 +
DISPLAY1 1920×1080（位于主屏左侧，桌面 X 为负）。用真实服务端和两个客户端跑控制/观看会话，逐项验证：

#### Fixed

- **显示器下拉框的无障碍名称与值**：ComboBox 没有 `AutomationProperties.Name`，折叠状态下的值取自
  `DisplayChoice.ToString()`，读屏会念成 `DisplayChoice { Id = …, Label = … }`。现在名称是“显示器”，
  `DisplayChoice` 的 `ToString()` 返回标签（`DISPLAY2 · 2560×1440 · 主`）。

#### Verified

- 控制端下拉框列出两块显示器并正确标注主屏；双向切换后状态栏、画面分辨率和被控端日志
  （`Now streaming display …`）同步变化。
- **输入映射**：控制左置副屏时，注入的指针落在负 X 坐标（实测 -961,543），切回主屏后落在正 X（2142,673）；
  两块屏都未出现坐标被钳到主屏的情况。
- **指针叠加层**：副屏流中，真实指针形状/位置绘制在画面中心，与物理指针在副屏上的位置一致。
- **拔插回落**：正在观看副屏时用 `DisplaySwitch /external` 禁用副屏，流自动切回主屏（2560×1440），
  下拉框随“只剩一块屏”隐藏；`/extend` 恢复后下拉框重新出现且选择跟随当前流。
- **并发会话**：控制端看副屏、观看端看主屏可同时拉流；两者都切到副屏时共用同一份采集。
- **录制**：副屏录制的 MP4 为 1920×1080/H.264；录制中切换显示器会按设计中断并提示
  “录制已中断：画面尺寸发生变化”，已保存文件仍可读取。
- 新增 `DxgiCaptureTests.Captures_every_attached_display_at_its_own_size`：逐一复制每块已连接显示器并断言
  帧尺寸与枚举结果一致，覆盖左置副屏的负坐标与“每输出一个复制接口”的限制。

#### Notes

- 两块显示器都是 16:9，非 16:9 的 letterbox 映射未覆盖；拔插是用 `DisplaySwitch` 切换拓扑模拟的，
  不是物理拔线；混合 DPI 缩放未单独覆盖。QSV 硬件编码器仍按前述限制回退软件编码。

### Windows 端到端测试修复

在 Windows 11 Enterprise（26200）/ .NET 10.0.13 上用 Release 构建、真实服务端与多个独立客户端实例跑用户场景：
先在本机以未安装后台服务的 standalone 被控端连接，再启动三个控制器同时观看，另跑文件传输、终端、摄像头、
录制、固定密码、设备列表与服务器中断恢复。

#### Fixed

- **第二个及之后的并发会话一建立就断开**：Windows 一个进程对同一个输出只允许一个
  `IDXGIOutput1::DuplicateOutput`，第二个返回 `E_INVALIDARG`，于是被控端日志是
  “Video channel failed”，控制端只看到会话结束。采集现在按显示共享：DXGI 复制接口每个显示只开一份，
  各会话订阅同一份最新画面（引用计数、空闲两秒后暂停采集但不释放复制接口）。新增
  `SharedScreenCaptureTests`（两个订阅者共享一个 capturer、光标事件分发、空闲后新订阅者仍能拿到最后一帧）；
  实机三个控制器同时出画面，日志中不再有 `DuplicateOutput` 失败。
- **成功连接后设备列表里的密码不会更新**：行里的密码框在上次连接后保存了旧密码，之后用新密码（例如清空固定
  密码后改用临时密码）连上，列表刷新时仍把旧密码带回来；从该行点“终端/文件/摄像头”会以旧密码连接，
  被控端记 “Wrong password”，界面只停在设备列表，直到重启客户端。未被用户改过的密码框现在跟随
  `ISecretStore` 里刚保存的密码，用户亲手改过的仍保留；新增 `PasswordAfterReload` 的回归用例。
- **摄像头 424×240 及以下的小尺寸模式打不开**：这台机器的内置摄像头在这些尺寸只提供 YUY2，源读取器拒绝
  把它转成 NV12（`MF_E_INVALIDMEDIATYPE`），用户选中后只会看到“摄像头无法打开”。NV12 被拒绝时现在改设
  YUY2 输出，再由平台层把 4:2:2 降采样成 4:2:0；对外的帧仍是 NV12。新增合成 YUY2→NV12 单测与“最小模式
  仍出 NV12”的实机用例；此前 `CameraTests.Stopping_the_capture_releases_the_camera_so_it_can_be_opened_again`
  在这台机器上失败，现在通过。

#### Notes

- **硬件编码器在这台机器上每次都回退到软件编码**：Intel Iris Xe（Windows 11 26200）的 QSV H.264 MFT 是
  异步、D3D11-aware 的硬件 MFT；它接受解锁、输入/输出类型和每一帧输入，但第一次 `ProcessOutput` 返回
  `E_UNEXPECTED`，此后桌面与摄像头都走 `H264 Encoder MFT` 软件编码。为排除我们自己的设置问题，用
  `D3D11CreateDevice`（`VideoSupport` + 多线程保护）+ `MFCreateDXGIDeviceManager` + `ResetDevice` 正确
  建立设备管理器后重试：`MFT_MESSAGE_SET_D3D_MANAGER` 被接受，事件状态均为 0（NeedInput/HaveOutput 正常），
  `ProcessOutput` 仍以 `E_UNEXPECTED` 失败；软件 MFT 的同一往返测试通过。把 Intel 驱动从 32.0.101.7088
  更新到 32.0.101.7092（2026-09-03）并重启后重测，失败点与 HRESULT 完全相同；实机连接仍正常回退到软件
  编码（2560×1440、29 fps、约 42 ms 延迟）。结论是故障在 Intel 的 MFT/驱动内部，不在封装层。
  另外，硬件+异步枚举里还有一个 **Microsoft AVC DX12 Encoder**（产品原先的硬件枚举少了
  `MFT_ENUM_FLAG_ASYNCMFT`，所以完全看不到它）；它接受设备管理器，但拒绝我们手工构造的 H.264 输出类型。
  要让它工作，需要按 MFT 的可用类型协商并改用 D3D11/DX12 纹理输入，这是一条独立的、较大的硬件路径，
  本轮未实现，实验性改动已全部撤回。`MediaFoundationCodecTests.Hardware_encoder_round_trips` 因此在这台
  机器上是红的（软件编码往返通过）；换一台 QSV 正常的机器或更新/回退 Intel 驱动后才能判定。解码侧也只
  枚举同步 MFT，所以即使编码修好，也不是全硬件管线。
- **Windows 上的两处测试环境假设**：Ops 集成测试清理时 `File.Delete` 撞上仍被 SQLite 连接池占用的数据库
  文件（Unix 允许删除已打开的文件，所以此前没暴露），`OpsHostFixture` 与 `SchemaMigrationTests` 现在先调用
  `SqliteConnection.ClearAllPools()`；`RealProgramTerminalTests` 等的是旧版 nano 的 “Write to File”，
  而 Git for Windows 的 nano 8.2 提示 “File Name to Write:”，两种写法都接受。两类用例在本机已通过。
- **三路 2560×1440 软件编码同时观看时，延迟会累积到数十秒**：这是本机把被控端与三个控制端放在同一颗 CPU 上
  的饱和场景，去掉多余会话后会逐步回落；单会话实测 26–40 fps、35–50 ms 画面延迟。当前管线不会为了追平而
  丢弃旧帧，慢客户端只能等队列排空——在真实双机、硬件编解码的场景下未复现。
- **Windows 服务 + 会话 Agent 的托管形态未实测**：测试账户没有提升权限，无法安装 LocalSystem 服务，
  因此本轮走的是客户端进程内 standalone 被控端。登录界面 / UAC 接管与 `SendSAS` 仍需在管理员环境补测。
- **摄像头首次打开出现过一次全黑画面**（1280×720、10 fps、0.4 Mbps），停止再开始后同一进程内连续三次均为
  正常画面（29–30 fps、3–5 Mbps）；硬件编码器仍每次回退。未再复现，记录待查。
- **开发用内存注册表重启即换设备码**：停掉 Rendezvous 再启动后两台客户端都拿到新代码（文档已说明
  `DATABASEPROVIDER=none` 的行为）；用新代码 + 固定密码/临时密码重连均正常，旧代码在设备列表中显示离线。
- 剪贴板文本双向稳定；图片剪贴板在连接期间可保持，但两端在同一台机器上共享同一个系统剪贴板，
  跨机传输未被这轮验证覆盖。

### macOS UI 与图标评审

在 macOS 26 上逐屏核对界面与产品图标。

#### Changed

- **macOS 客户端 bundle 改名为 `Remote Desktop.app`**：Dock 悬停提示读的是 `.app` 文件名，而不是
  `CFBundleDisplayName`——即使 LaunchServices 已报告 “Remote Desktop”，旧的 `RemoteDesktop.app` 仍显示
  `RemoteDesktop`。改名后与 host bundle（`Remote Desktop Host.app`）一致，Dock 提示、菜单栏与窗口标题
  统一为 “Remote Desktop”。打包脚本、CI 校验路径与文档同步更新。**升级注意**：pkg 不会删除旧名字的
  bundle，从旧版升级需先删掉 `/Applications/RemoteDesktop.app`；若开过登录项，需在新 bundle 里重新开启。
- **主窗口标题从“远程桌面”改为 “Remote Desktop”**：与 Dock 提示、菜单栏与产品名一致（撤销上一轮为
  Linux 中文一致性所做的改动）。

#### Fixed

- **三个控制器时，第三个会话的“仅观看/断开”落在横幅视口外**：每项说明与按钮原先竖排，约 100pt 高，
  150pt 的视口只露出两项半。改为说明与操作同一行；三个控制器的实机截图确认三项操作全部可见。
- **`Remote Desktop Host.app` 在 Finder/安装包中显示通用应用图标**：host bundle 没有 `AppIcon.icns`，
  也没有 `CFBundleIconFile`。`make-app.sh` 现在用客户端同一套图标资产生成并签入图标，product 只有一份图标源。
- **远程桌面与摄像头的录制按钮比同一工具栏里的其他按钮高**：录制/停止文案的前缀是 emoji（`⏺`/`⏹`），
  Apple Color Emoji 的行高把按钮撑高。改为文本呈现的 `●`/`■` 后，远程桌面工具栏六个按钮（静音、录制、
  文件、终端、摄像头、断开）实测均为 64px（32pt），摄像头工具栏的“开始/停止/录制摄像头”也完全一致；
  viewmodel 里的注释说明了不要换回 emoji 的原因。
- **终端窗口的下拉框标签是英文 “Shell”**：改为“终端类型”，`AutomationProperties.Name` 同步更新。

#### Notes

- **亮色外观已实机核验**：主页、设备列表、三项高级设置、远程桌面、文件传输、终端、摄像头与被控端警告
  横幅逐屏截图；正文、次级文字、警告色与按钮均清晰。唯一观察：文件传输中的隐藏文件整行以 50% 透明度
  呈现，亮色下大小/日期（本来就是 `caption`）对比度低于 4.5:1；属可访问性观察，未改设计。
- macOS 图标主体占画布 89%，比 Apple 的 824/1024 栅格大约 10%；本轮只记录（R3），未改图形比例。
- 版本号段数与固定密码“关闭”按钮等 P3 观察项本轮只记录，未改。

### macOS 端到端测试修复

在 macOS 26.7 / Apple Silicon 上用真实签名的客户端 bundle、真实服务端与两个独立客户端实例跑端到端场景时发现的问题。

#### Fixed

- **设置 6–7 位固定密码会让客户端直接崩溃**：窗口自己留了一份长度下限（6），而
  `FixedPasswordPolicy.MinimumLength` 早已提高到 8。密码通过窗口校验后在 `LocalHostControl` 抛出
  `ArgumentException`，异常从 `AsyncRelayCommand` 逃到 UI 线程，进程退出。窗口现在直接问策略
  （`ValidateFixedPassword`），保存失败也只显示在编辑框里，不再结束进程。
- **被控端拒绝固定密码时界面仍以为保存成功**：`PipeHostControl` 读到了服务端 `error` 应答却把它丢掉。
  现在会把它转成异常，由界面显示；新增回归测试（旧的实现失败）。
- **macOS 摄像头未授权时提示是英文、且不会主动询问**：控制器窗口只显示原生辅助库的
  “Camera access has not been granted to this application.”。现在打开摄像头时若尚未授权，会先通过
  `AVCaptureDevice requestAccessForMediaType:` 弹出系统授权框（由本机用户决定）；被拒绝后才显示
  `TccGrants.Describe` 的中文提示。摄像头权限不需要重启应用即可生效。
- **macOS 摄像头授权后第一次打开会把被控端进程带崩**：30 fps 的帧间隔被换算成 33333/1000000 秒
  （30.000299 fps），略高于设备支持范围的上限 30.00，AVFoundation 抛出
  `NSInvalidArgumentException`；原生辅助库没有兜住，进程被直接终止。帧间隔现在向上取整，保证有效
  帧率不超过请求值；`RDCameraApplyFormat` 与 `rd_camera_start` 的 `NSException` 也都在 C 边界转成
  普通错误返回，不再越过边界。
- **macOS 摄像头有画面尺寸、却一帧都不出**：AVFoundation 交付 BGRA，而 `CameraModule` 只接受 NV12，
  于是每一帧都在“格式不是 NV12”处被静默丢弃（Windows/Linux 的摄像头源本来就直接给 NV12）。
  模块现在把 BGRA 转成 NV12 后再编码，与桌面采集走同一条转换；新增合成 BGRA 摄像头的回归测试。
- **摄像头实际分辨率与用户选择不一致**：`startRunning` 会根据会话 preset 最后一次改写
  `activeFormat`，选 1280×720 实际流出 1920×1080。原生层在启动后再应用一次选定的格式。
- **摄像头授权弹窗无人应答时，控制端会在 30 秒后崩溃**：`CameraViewer` 的打开超时抛
  `OperationCanceledException`，`CameraWindowViewModel` 没有捕获，异常从 UI 线程逃出并终止进程。
  现在显示“被控端没有在 30 秒内响应”的提示，窗口保持可用。
- **录制保存后的状态文本会压住工具栏按钮**：远程桌面窗口的状态区是横向 `StackPanel`，子元素按无限宽度
  测量，`TextTrimming` 不生效，完整保存路径直接盖在“静音/录制屏幕/文件…”上。改为 `DockPanel`：
  录制状态右对齐、限宽 520px、超出显示省略号，并带完整路径的 ToolTip。
- **设备别名较长时，列表里的设备代码会丢掉第一位**：`TextBlock.caption` 默认换行，设备列表行右侧的
  Auto 列被标题挤窄后，“902 226 105”显示成“002 226 105”。代码改为 `NoWrap`，需要让位时由标题省略号
  截断。

### 当前平台 UI 与图标修复

针对 Ubuntu 26.04 / GNOME Wayland 的实机评审，按 P0→P3 处理。

#### Changed

- **应用图标改为原创矢量**：不再从 Windows `mstsc.exe` 提取图标，`icon.svg` 现在是真正的矢量（此前是
  base64 包装的 256px PNG）。16/24px 使用去掉支架、放大显示器与指针的简化版，512/1024px 由几何直接渲染；
  新增 `icon-1024.png` 供 macOS `.icns` 使用。删除 `build/tools/New-AppIcons.ps1` 并更新 `build/README.md`。
- **统一强调色**：用 `FluentTheme.Palettes` 的 `ColorPaletteResources.Accent` 把 Fluent 自绘控件
  （CheckBox、TextBox 焦点框、ProgressBar、Tab 下划线）统一为应用蓝 `#3B82F6`，不再跟随桌面强调色
  （Ubuntu 默认橙色）。之前无效的 `SystemAccentColor` ThemeDictionary 覆盖已移除。
- **对比度**：浅色 caption 2.39:1 → 4.97:1，深色 caption 3.89:1 → 4.82:1；主按钮改为 `#2563EB`，
  白字 5.17:1；浅色主题在线圆点改为 `#149A45`（3.35:1）。
- **文件管理器图标**：文件夹/文件从 emoji（📁/📄）改为与导航一致的 PathIcon。
- **窗口标题**：主窗口从 “Remote Desktop” 改为“远程桌面”，与其它窗口及 `.desktop` 的 `Name[zh_CN]` 一致。
- **路径占位符**从“所有驱动器”改为“所有位置”，不再使用 Windows 术语。

#### Fixed

- **Linux 无人值守限制不够醒目**：固定密码区块的 portal 限制从 12px tertiary caption 改为 warning banner。
- **可访问性名称**：主页输入框、连接方式、文件行、路径/名称输入、复制按钮与终端关闭按钮补齐
  `AutomationProperties.Name`；终端标签标题增加 ToolTip。
- **文件传输空状态**：没有传输时显示“暂无传输”，不再是一块空白。
- **Linux standalone 托管说明**：高级设置显示“被控端在本进程内运行（未安装后台服务）”，不再是空行。

#### Notes

- **Wayland 后端已评估，本次不切换**：`Avalonia.Wayland` 12.1.4 能启动原生窗口，但该窗口没有出现在 AT-SPI
  树中（会回退无障碍与自动化），且 Fluent 12.1.4 没有 HighContrast 变体。XWayland 在 1.333 分数缩放下的
  模糊仍是已知限制，待 Wayland 后端与无障碍/高对比度一起移植。
- 高对比度模式仍不跟随；`_NET_WM_ICON` 仍只有 128×128 一档（Avalonia X11 后端行为）；AppStream metainfo
  仍未提供。

### Linux 端到端测试修复

在 Ubuntu 26.04 / GNOME Wayland 上用真实服务端、客户端与 HostService 跑端到端场景时发现的三处问题。

#### Fixed

- **关闭终端会在 `crash.log` 里留下一条“崩溃”**：结束终端时先关闭 PTY，仍在读取输出的那一端会以
  `EBADF` 失败；收尾里的 `ContinueWith` 没有观察该异常，终结器把它当作未观察异常交给 `CrashLog`。
  现在收尾会显式观察这类预期失败，正常关闭不再留下 `crash.log`。
- **SIGTERM / SIGINT 不能结束客户端**：信号被 host 的 `ConsoleLifetime` 消费后后台服务停了，但 Avalonia
  的事件循环还在，进程不退出，注销或 `systemctl --user stop` 只能等超时。客户端现在订阅
  `IHostApplicationLifetime.ApplicationStopping` 并关闭桌面生命周期，两种信号都能在 1 秒内退出。
- **libei 设备就绪前的输入会打断整个输入通道**：设备尚未就绪时每个输入事件都会再调用一次
  `ConnectToEIS`，portal 以 “Session is already connected” 拒绝，异常穿过输入通道后该会话的键鼠永久失效。
  现在 EIS 描述符只连接一次，设备到达前的输入与以前一样被丢弃；portal 的 D-Bus 拒绝也统一转成
  `PortalException`，不再以未预期异常的形式结束通道。

### 九个安装包：三平台 × 控制端 / 被控端 / 服务端

三个平台现在各出三个角色，共九个安装包。服务端那一个包统一是 **Rendezvous + Relay + Ops**：
控制台没有前两者就没有数字可看，分成两个包只会多出"装了一半"的状态。

#### Added

- [Windows] **被控端 MSI**（`RemoteDesktop-Host-Windows-<版本>-x64.msi`）：只装 `RemoteDesktop.HostService` 与
  `RemoteDesktop.SessionAgent`，没有客户端窗口与开始菜单项。走 `Build-ClientInstaller.ps1 -HostOnly`，
  服务定义、崩溃重启、UDP 防火墙规则与机器清理步骤只有一份；产品名与 UpgradeCode 独立。两者共用
  `RemoteDesktopHost` 服务与机器的设备身份，所以一台机器只能装一个——安装条件会拒绝第二个。
- [Windows] **安装向导可设固定密码**：host 没有窗口，临时密码只在服务内存里，所以新增“无人值守设置”页，
  把 `FIXEDPASSWORD` 通过 deferred 自定义动作写进服务的 DPAPI LocalMachine 密钥库；静默安装可用同名属性，
  `CLEARFIXEDPASSWORD=1` 清除。属性在 `MsiHiddenProperties` 里、动作 `HideTarget="yes"`；密码少于 8 位时
  安装失败，不会装出一个连不上的 host。
- [macOS] **独立的 host-only app**：`Remote Desktop Host.app`（`LSUIElement`、无窗口、bundle id
  `com.remotedesktop.host`），里面是 `RemoteDesktop.HostService`，不含 Avalonia / 控制端 UI；配套
  `/Library/LaunchAgents/com.remotedesktop.host.plist` 每次图形登录自启。它不再复用客户端的 `.app`——TCC 授权
  按 bundle 的签名身份记账，host 必须能被单独授予屏幕录制/辅助功能。
- [macOS] **host 无界面下的固定密码/设备码入口**：`HostService --set-fixed-password` 写入登录 Keychain；
  设备码从 `~/Library/Application Support/RemoteDesktop/logs/` 里查 `Registered with the rendezvous server as`。
- [Linux] **文档明确无人值守只能“观看”**：Wayland portal 不允许持久化远程桌面会话，控制每次要现场授权；
  固定密码能让连接通过认证，但不能让无人值守变成可操作。
- [Windows/macOS] **服务端包收编 Ops**：Windows 多一个 `RemoteDesktopOps` 服务、`Ops/` 目录与 `OpsFolder` 组件；
  macOS 多一个 `com.remotedesktop.ops` LaunchDaemon，库在 `var/ops.db`。两个平台都在安装说明里写了首个管理员怎么建。
- [CI] **`release.yml` 出九个资产**：Windows 三个 MSI、macOS 三个 `.pkg`、Linux 三个 `.deb`，少一个就不发版；
  `verify-packages` 按平台逐个打开包，检查 Ops、被控端登录项与客户端载荷都在。

#### Fixed

- **跨平台发布会把 macOS 的 dylib 塞进 Linux 包**：`RemoteDesktop.Transport` 与 `Platform.MacOS` 的条件看的是
  **构建机**是不是 macOS，而不是**目标**是不是；而项目引用在内部构建时又被去掉了 `RuntimeIdentifier`，所以
  在 Mac 上交叉发布 `linux-x64` 时，`libmsquic.dylib`、`libssl.dylib`、`libcrypto.dylib` 与
  `libRemoteDesktopMacNative.dylib` 都进了 `.deb`。现在条件同时看目标 RID 与 Linux 三个打包脚本显式传入的
  `-p:RemoteDesktopCrossTargeting=true`（这个属性会流到项目引用，RID 不会）。
- **Windows 服务端 `Components.wxs` 的 XML 错误**：`RelaySettings` 前多了一个 `>`，WiX 编译会直接失败。
- **macOS 服务端升级会覆盖 `appsettings.json`**：`.pkg` 没有 conffile 概念，payload 里的同名文件在升级时会被
  写回构建默认值，连 relay key 一起丢掉。现在 payload 里放的是 `appsettings.default.json`，postinstall 只在
  `appsettings.json` 不存在时复制；升级保留运维改过的配置。三平台在这一点上这才一致。
- **Ops 作为 Windows 服务时数据库落在 `System32`**：服务的工作目录是 `System32`，`Ops:Database:Path` 的相对路径
  就跟着它走。现在相对路径按程序目录解析，`%ProgramData%` 也会展开；Windows 包据此把库放在
  `%ProgramData%\Remote Desktop Server\ops.db`。

#### Notes

- macOS 的 client（`RemoteDesktop.app`）与 host（`Remote Desktop Host.app`）是**两个不同 bundle**，TCC 授权也分开；
  一台机器通常只装一个。两个都装会得到两个被控端（两个设备码、两个监听），不建议。
- Windows 客户端 MSI 仍然同时装客户端、服务与被控端 Agent——**一台机器想同时具备控制与被控两种角色，装客户端包就够了**；
  新的 Host 包是给"只能被控、不允许控制别人"的机器用的，两者不能并行安装。

### 运维监控台：交付面

M6：把控制台从"能跑"变成"干净机器按文档从零装到能登录"。核心是文档与打包，不是新功能。

#### Added

- [Linux] **`remotedesktop-ops.service`**：systemd 单元，回环监听 `127.0.0.1:5090`，库落在
  `/var/lib/remotedesktop-server/ops.db`，与信令/中继同账号、同样加固。
- [Linux] **.deb 收编控制台**：`build-deb.sh` 一并发布 `RemoteDesktop.Server.Ops`，装上并启用 `remotedesktop-ops`；
  `postrm` 移除时停用、卸载无残留单元。
- [Docs] **`docs/ops/README.md` §二 10「运维监控台」**：启动三种方式、首个管理员、关键配置、让总览出现数字的三件事
  （指标 + 登记节点 + 事件通道密钥）、管理动作与审计、安全边界。

#### Notes

- 控制台是**可选**的：不装它远程桌面照常工作，装上后默认只在回环监听，对外是反向代理的决定。

### 运维监控台：管理动作

M5：控制台第一次能**反向影响数据面**——断开会话、封禁 / 解封设备。任何会改到设备或会话的动作都要两个人：
一个发起、一个批准，发起人不能批准自己的动作，两步各留一条审计。

#### Added

- [Signaling] **`ServerMessage.admin_command`** 与 **`Register.client_capabilities`**：能力位协商——被控端声明支持后，
  信令才会下发 `AdminCommand`；旧客户端永远收不到。
- [Client] **`AdminCommandService`**：读 `disconnect_session` 命令，落到 `HostSessionService.Disconnect`；日志里记命令
  id，可与控制台的审计条目对上。
- [Storage] **设备封禁**：`device_registrations.revoked_at`（含对旧表的迁移）；`IDeviceRegistry.SetRevokedAsync` /
  `IsRevokedAsync`；注册路径在发 `Registered` 之前拒绝被封禁的设备。
- [Rendezvous] **南向管理面**：`POST /internal/v1/admin/disconnect`（设备离线 404、不支持 409）与
  `/internal/v1/admin/revoke`（封禁时顺带踢掉在线连接）。
- [Ops] **`ops_admin_action` 复核队列** + `/api/actions`（发起 / 列表 / 批准）；库表迁移到 v3（`ops_session.client_session_id`
  加一张动作表）。会话页"请求断开"、设备页"封禁 / 解封"、管理动作页。
- [Tests] 集成 30（新增两段式复核闭环、发起人不能自批、封禁经批准到达节点），存储 29（新增封禁持久化与旧表迁移）。

#### Notes

- 断开会话按**被控端所知的会话 id**（`client_session_id`，来自心跳）寻址：中继的 `correlation_id` 被控端并不认识。
- `admin_command` 发后不管：控制台不等待被控端确认，被控端不支持时节点返回 409、动作记为 `failed` 并写审计。

### 运维监控台：客户端心跳

M4：控制台补上最后一块只有客户端才知道的事实——这次会话跑在什么系统上、延迟多少、走的是直连还是中继。
**默认关闭**：打开 `Telemetry:Enabled` 之前，客户端不发一个字节。

#### Added

- [Signaling] **`TelemetryReport`**（`ClientMessage.telemetry`）：会话 id、角色、模式、链路、RTT、平台、版本，
  每 30 秒一条，走已有信令连接。字段集合是封闭的，没有设备名、地址、窗口标题或输入的位置。
- [Signaling] **`RelayInfo.relay_session_id`** 与 **`Hello.session_id`**：前者让心跳落回中继已经写好的那条会话记录，
  后者让两端对同一个会话用同一个 id——少了任何一个，一次 P2P 会话就会在控制台变成两行。
- [Rendezvous] **`TelemetryReceiver`**：校验 + 每设备限流（60 条/分钟，先扣预算再校验），转成 `session.telemetry`
  事件。`remotedesktop_telemetry_reports_total` / `_refused_total` 记两个计数。
- [Ops] **`session.telemetry` 投影**：有 `relaySessionId` 的落在中继那一行（`source` 仍是 `relay`），没有的创建
  `source=client` 的行；控制端的 RTT 不会被被控端的 0 覆盖。`transport` 在库里统一为 `relay`，协议里的 `relayed`
  在投影时归一。
- [Ops] **库表迁移到 v2**：`ops_session` 增加 `platform`、`version`，按版本号逐条在事务里执行；老库直接升级，
  重启第二次不会重复 `ADD COLUMN`。
- [Ops] **页面**：会话表新增"质量"列（平台 · RTT，没有心跳时显示"—"）；总览新增"链路与平台"卡片（近 24 小时）。
- [Ops] **告警规则**：`session.rtt`（窗口内平均 RTT）与 `session.direct`（直连占比），阈值型，不需要指标名。
- [Client] **`SessionTelemetry`**：开关关闭时不启动定时器；开启后第一个点等 5 秒（让第一次 ping 回来），之后按
  `IntervalSeconds`。发送只入队，队列满或断线时丢弃，绝不阻塞会话。
- [Docs] `docs/usage/README.md` §8 写明打开后到底有什么字段、没有什么字段。

#### Changed

- [Transport] `IPeerConnection.RelaySessionId` 由连接自己携带，控制器与被控端都从服务端下发的中继信息里拿到它。

#### Notes

- **丢包与码率仍未上报**：.NET 的 QUIC API 不暴露每条连接的丢包统计，编码器的码率是设置值不是实测吞吐量。
  与其填两个永远是 0 的字段，不如让它们保持"—"。
- 心跳的最坏滞后是一个上报周期（30 秒）：会话刚开始、或被控端刚连上时，质量列先是"—"。

### 运维监控台：骨架、登录与审计

运维监控台的第一个可运行版本（计划里的 M0）。它现在能做的是**登录一个空控制台**：账号、TOTP、CSRF、审计链、
基线库表都已落地，数据源还没有接——页面上每个数字都是"—"，这是有意的，见 `docs/ops/README.md` 的
"数据从哪来（范围合同）"一节。

#### Added

- [Ops] **`RemoteDesktop.Server.Ops`**：独立部署的 Web 控制台，默认只监听 `127.0.0.1:5090`；Windows 服务
  `RemoteDesktopOps` / systemd 单元 `remotedesktop-ops`。默认 SQLite，可换 PostgreSQL（`Ops:Database`）。
- [Ops] **登录**：PBKDF2-HMAC-SHA256（60 万次迭代，参数随哈希存储）+ TOTP（RFC 6238，SHA-1/6 位/30 秒），
  会话 Cookie 只存 SHA-256，登录时重新签发。首个管理员由 `--create-admin` 创建，没有默认口令，没有网页初始化入口。
- [Ops] **CSRF 与响应头**：写操作要求 `X-Ops-CSRF` 与 Cookie 双提交；CSP `default-src 'self'` 等一组头由
  中间件统一加，前端零外链、零内联脚本。
- [Ops] **审计日志**：哈希链写入（长度前缀字段，避免字段移位撞哈希），登录成功 / 失败 / 账号禁用均有记录。
- [Ops] **基线数据模型**：节点、指标采样与汇总、事件、会话投影、告警与规则、通知、静默、管理员、会话、租约、
  模式版本共 16 张表；时间统一存 Unix 毫秒，两种数据库共用一份 DDL。
- [Ops] **`ops_lease` 租约**：抓取、告警求值、保留清理将来都要独占运行；单实例下永远拿得到，多副本时不重复发通知。
- [Shared] **事件信封与签名器**：`OpsEventEnvelope` 带 `v` / `eventId` / 服务端生成的 `correlationId`；
  HMAC-SHA256 over `timestamp.body`，同一套代码给信令、中继、控制台用。
- [Tests] 单元 51 项（口令、TOTP 的 RFC 向量、Base32、审计链、锁闭、签名、配置、指标类型），集成 8 项（真 Kestrel +
  SQLite：401、登录全流程、CSRF、锁闭、伪造 Cookie）；架构测试新增"控制台分层"规则。

### 运维监控台：告警与审计

M3：控制台开始会喊人。规则是结构化的（单指标 / 比值 / 抓取失败），条件必须持续满足一段时间才开告警；每条告警
只开一次、可认领、恢复后自动关闭。审计日志补上列表与哈希链校验。

#### Added

- [Ops] **规则与求值**：`AlertStateMachine` 用 `forSeconds` 判断"持续多久"，未命中立刻清零；去重键同一时刻只允许
  一条未恢复告警。求值每 15 秒一次（`Ops:AlertIntervalSeconds`），新建的部署自动种一条"节点抓取失败"规则。
- [Ops] **指标口径**：counter 先求速率再比较，gauge 按 `avg/max/min` 聚合；`ratio` 用分子/分母；没有读数时规则
  不触发——不知道不等于出事了。
- [Ops] **告警生命周期**：`GET /api/alerts`、`POST /api/alerts/{id}/ack`（operator 及以上）；恢复由求值自动完成。
  开、认领、恢复都写入审计。
- [Ops] **Webhook**：`Ops:Notifications:WebhookUrl`，开/恢复各发一次 JSON；`ops_notification_attempt` 以
  `(alert_id, channel, transition)` 去重，重启与重试都不会重复发。SMTP 未做。
- [Ops] **审计**：`GET /api/audit`（管理员）、`GET /api/audit/verify` 重算哈希链并指出第一条不一致的记录。
- [Ops] **页面**：告警页（列表、认领、规则表格与新增表单）与审计页（列表、一键校验）；总览的"今日告警"接上真实计数。
- [Tests] 单元 86 项（新增状态机、比较器、哈希链篡改与删除检测），集成 22 项（新增完整告警闭环：失败节点 → 开告警
  → 认领 → 停用节点 → 自动恢复，以及审计链校验）。

### 运维监控台：事件与会话

M2：信令与中继把"服务器看见了什么"推给控制台，控制台把它折成一条可以点开的会话；设备页通过信令服务的分页查询
读注册表，控制台仍然不碰别人的数据库。

#### Added

- [Shared] **`IOpsEventSink`**：有界队列（4096）+ 后台批量发送，HMAC-SHA256 签名（`X-Ops-Key-Id` /
  `X-Ops-Timestamp` / `X-Ops-Signature`），失败重试三次后丢弃并计数。`Publish` 永不阻塞、永不抛——它站在握手与
  转发路径上。未配置 `OpsEvents:Endpoint` 时是空实现，调用点完全不变。
- [Rendezvous] **握手事件**：`session.requested` / `session.accepted` / `session.failed`（拒绝与超时）。
  `correlationId` 由服务端铸造，恰好就是中继票据的 `SessionId`，因此中继侧不需要任何协议改动就能对上同一条会话。
- [Relay] **配对事件**：`session.relay_paired` / `session.relay_closed`，后者带本次配对的双向字节数
  （每个 `Pairing` 自己累计，不再只有全局计数）。
- [Ops] **`POST /ingest/events`**：按 Key-Id 选密钥、校验签名与 60 秒时间窗；`ops_event` 以 `event_id` 唯一索引
  做幂等，重投一批不会重复计数。事件表是事实，`ops_session` 是同一事务里的投影（`SessionProjection`，纯函数）。
- [Ops] **会话 API 与页面**：`GET /api/sessions`（按开始时间分页）、`GET /api/sessions/{id}`（含事件时间线）；
  页面显示传输方式、时长、入/出字节、来源与结束原因，点开可看四条事件。明确写出"P2P 会话服务器看不到"。
- [Storage] **`IDeviceCatalog`**：设备按 id 分页，游标独占、按 id 排序；关系型注册表与内存注册表各实现一份，
  时间戳由各自的存储格式解析成 Unix 毫秒（SQLite 存文本、PostgreSQL 存 timestamptz）。
- [Rendezvous] **`GET /internal/v1/devices`**：一页设备（代码、首次注册、最后活跃、当前在线），多取一行判断是否还有
  下一页；在线状态来自 presence，不是数据库里的猜测。
- [Ops] **设备页**：`GET /api/devices?node=&cursor=` 代理到选定的信令节点，页面可按设备代码过滤、加载更多；
  未选择或节点不可达时给 404 / 502，而不是一张空表。
- [Tests] 单元 75 项（新增 8 项投影规则，含乱序到达与坏 payload），集成 20 项（新增签名入库、重放不重复、
  伪造签名 401、过期时间戳 401、发送端签名可验证、设备代理与分页）；存储单测新增设备目录分页。

### 运维监控台：节点抓取与总览

M1：控制台开始显示真实数字。中继有了 HTTP 面和字节统计，控制台每 10 秒抓一次，总览的四个 KPI 与流量图都从
抓到的序列算出来——没有读数时仍然是"—"，不补 0。

#### Added

- [Relay] **HTTP 面**：`/metrics`（Prometheus 文本）、`/health`、`/internal/v1/info`，默认监听
  `http://127.0.0.1:5083`（`Relay:HttpEndpoint`）。宿主从 Worker SDK 改为 Web SDK，UDP 循环不变。
- [Relay] **转发计数**：配对建立 / 关闭、双向字节数、转发报文数与配对总数。计数器用 `Interlocked` 维护，
  HTTP 线程读取时不再遍历只属于接收循环的配对表。
- [Rendezvous] **`remotedesktop_relay_tickets_issued_total`**：每签发一张中继票据加一；`/internal/v1/info` 返回名称、
  角色、版本与启动时间；`/metrics` 与 `/internal/*` 可选 Bearer 校验（`Rendezvous:NodeToken`）。
- [Ops] **抓取任务**：按 `Ops:ScrapeIntervalSeconds`（默认 10）并发抓取所有启用节点，失败也更新
  `last_scrape_at`，节点卡片因此能显示"刚刚试过、失败了"而不是把上次成功当成现状。
- [Ops] **节点管理**：`GET/POST /api/nodes`、`PATCH/DELETE /api/nodes/{id}`，仅管理员可写；登记是手动的，
  不自动发现。节点页可启用 / 停用 / 删除，删除同时清掉该节点的历史读数。
- [Ops] **总览**：`GET /api/overview` 返回四个 KPI、30 分钟双向流量（30 秒分桶）与节点状态；KPI 只取 60 秒内的读数，
  过期的 gauge 视为缺失。告警数保持 `null`，等 M3 的规则引擎。
- [Ops] **页面**：总览接上 KPI、Canvas 流量图与节点状态；新增节点管理页（登记表单仅管理员可见），
  全部 DOM 用 `textContent` 构造，配 `default-src 'self'` 的 CSP。
- [Core] **Prometheus 文本解析器**：标签规范化（排序、去空白、解转义）、`# TYPE` 决定 counter/gauge、
  NaN 与畸形行跳过并计数；**计数器速率**处理重启归零，不产生负流量。
- [Tests] 单元 67 项（新增解析器与速率），集成 12 项（新增假节点 HTTP 服务 → 登记 → 抓取 → 总览断言，
  含抓取失败与重名拒绝）。

### 服务端：SQLite 与可插拔存储

信令服务器的设备代码不再只能存在 PostgreSQL 里。新增 SQLite（单机部署装完即用）和一个显式的 provider 设置，
存储层独立成 `RemoteDesktop.Server.Storage`：再加一种数据库是一个文件加一行 switch，不是改一遍宿主。
换库仍然意味着设备码重发，除非按 `docs/ops/README.md` 的手工步骤迁移。

#### Added

- [Server] **SQLite 存储**：`SqliteDeviceRegistry`，WAL 模式、写串行化、`device_id` 的 `CHECK` 约束
  （SQLite 按值定类型，没有约束就会存进文本）。文件位置由 `Rendezvous:Database:Path` 指定。
- [Server] **`Rendezvous:Database:{Provider,ConnectionString,Path}`**：`none` / `postgres` / `sqlite`。
  旧键 `Rendezvous:PostgresConnectionString` 与 `ConnectionStrings:Postgres` 继续生效，含义不变（= PostgreSQL）。
- [Storage] **`RemoteDesktop.Server.Storage` 项目**：`IDeviceRegistry` + `IDeviceRegistryStore` 两个端口、
  一份共享算法、每种数据库一个方言（四句 SQL + 唯一冲突判定 + 是否串行写）。
  加一种数据库：加包、加一个方言文件、switch 加一支、契约测试派生一个类。
- [Installer] **安装包默认改用 SQLite**：Windows MSI / macOS `.pkg` / Linux `.deb` 的 `auto` 现在是
  "本机已有 PostgreSQL 就继续用它，否则建一个 SQLite 库"。装完重启设备码就不变了，不用先架数据库。
- [Installer] `Set-ServerConfiguration.ps1` 的 `-DatabaseProvider` / `-DatabasePath`，
  `configure-server.sh` 的 `--database` / `--database-path`，`.deb` 的 `RD_DATABASE` / `RD_DATABASE_PATH`，
  MSI 的 `DATABASEPROVIDER` / `DATABASEPATH` 属性。

#### Changed

- **`Rendezvous:Database` 的 provider 是显式的**，不再靠"有没有连接串"推断。没有新设置时行为与之前完全一致。
- **健康检查改为询问已注册的存储实例**，不再自己读一遍连接串——那两处读的键本来就可能不是同一个
  （注册表有 `ConnectionStrings:Postgres` 回退，健康检查没有），只配了回退键的部署会在 `/health` 上抛异常。
- **契约测试**：`DeviceRegistryContractTests` 同时跑内存与 SQLite；原来那三条只测内存的用例已由它取代。

#### Fixed

- **配置步骤不再清空没提到的数据库设置。** `--configure-system` 以前把"没给"和"给空"当成一回事：
  `Set-ServerConfiguration.ps1 -TlsGenerate` 只传 TLS 参数，却会把 `PostgresConnectionString` 写成空串，
  下次重启所有设备码改变——而文档承诺的是"没给的保持不动"。现在 null = 不动，`auto` / 空串才是明确表态。

### macOS (Apple Silicon) 与 Linux 平台支持

新增 `RemoteDesktop.Platform.MacOS` 与 `RemoteDesktop.Platform.Linux` 两个平台项目（`net10.0`，与 Windows 侧
同形），实现全部平台端口，使同一份客户端代码可在三个系统上构建运行；两个移植共享新增的
`RemoteDesktop.Platform.Posix` 层（伪终端、0600 文件密钥库、用户上下文守卫）。核心层（`Domain` /
`Application` / `Media` / `Protocol` / `Signaling`）**零改动**，依赖规则（`Architecture.UnitTests`）无需修改。

#### Added

##### macOS

- [Platform] **屏幕采集与输入注入**：ScreenCaptureKit 截屏（含光标）、CGEvent 键鼠注入，统一坐标换算
  （虚拟桌面用逻辑点、采集返回像素、输入用归一化 0..1）。
- [Platform] **视频编解码**：VideoToolbox H.264（硬件优先 + 软件兜底），线上 Annex-B、内部 AVCC 显式互转。
- [Platform] **音频**：ScreenCaptureKit 环回采集（复用屏幕录制授权，**不新增麦克风权限**）+ AudioQueue 播放。
- [Platform] **摄像头**：AVFoundation 枚举与采集。
- [Platform] **录制**：AVAssetWriter（H.264 + MP4），复用共享 `Media` 层录制契约。
- [Platform] **无人值守**：`SMAppService` 登录项（新增 `ILoginItem` 端口）。
- [Platform] **剪贴板 / 防休眠 / 远程终端 / 密钥**：NSPasteboard、IOKit 电源断言、BSD 伪终端（`posix_spawn`）、
  Keychain。
- [Build] **`.app` 打包脚本** `build/installer/client/macos/make-app.sh`（自包含、`codesign --deep`、身份钉住）
  与原生辅助库构建 `native/macos/build-native.sh`（扁平 C ABI，44 个导出入口）。
- [CI] 新增 `build-macos` job（`macos-26`，arm64）。

##### Linux

- [Platform] **Linux 被控端与客户端**：屏幕走桌面 portal + PipeWire，输入走 libei，编解码走 libavcodec，
  音频走 PulseAudio，防休眠走 logind。
- **Linux 被控端是一条 `systemd --user` 服务**，不是系统服务：Wayland 的 portal 只在该会话内应答，
  所以它跑在图形会话里，自己就是采集和注入的那一方——**没有 Agent**，因为不需要拆。
- **`.deb` 安装包**：`remotedesktop-client`、`remotedesktop-host`、`remotedesktop-server`。都是自包含发布，
  目标机器不需要装 .NET；`libmsquic` 随包放在可执行文件旁，所以也不需要设 `LD_LIBRARY_PATH`。
  服务端的配置在 `/etc/remotedesktop-server/`，升级不覆盖。
- **Linux 上的配置分两层**：随包装在 `/usr/lib` 下的那份提供默认值，用户自己的
  `~/.local/share/RemoteDesktop/appsettings.json` 叠在上面。客户端里那个「修改服务器地址」在 Linux 上也能用，
  写的就是这一份，客户端和被控端共用。

##### 两端共通

- **`--self-check`**：一条命令问完这台机器能不能用。macOS 的版本在 bundle 内逐一验证每个平台端口与行为探针；
  Linux 的版本检查数据目录、QUIC、会话类型、会话总线、运行时目录、portal、显示布局、PipeWire、libei、
  libavcodec 版本与可用的 H.264 编码器。全部只读、不弹对话框，在一台起不来的机器上照样能跑；
  有必需项不通过时退出码 1。**两个包都带**，不只客户端——只装被控端的机器更需要它。
- **发布流水线产出三平台产物**：`release.yml` 的三个打包 job（`build-linux` / `build-macos` / `build-and-release`）
  把两个 MSI、两个 `.pkg` 和三个 `.deb` 一起发出；CI 在每个 PR 上构建三平台的包并检查包内容——不检查的话，
  构建脚本坏了要等到发版才知道。

##### 仓库结构与跨平台一致性

- **`.gitattributes`**：此前仓库里**一个 git 属性都没有**，行尾全靠各人机器上的 `core.autocrlf`——而那是个本地
  设置，对这个跨平台保证没有任何强制力。现在在仓库层面声明：`.sh` / `Dockerfile` / Debian 维护脚本 / `.plist` /
  `.service` / `.desktop` / `.yml` 一律 LF，`.ps1` / `.wxs` / `.wixproj` / `.csproj` / `.props` / `.slnx` 一律 CRLF，
  图片与库文件标为二进制。Windows 上工作区里的 13 个 `#!/bin/sh` 脚本原本是 CRLF，意味着 WSL、Docker 挂载源码
  目录、`scp` 到 Linux 主机这些走法都会以 `bad interpreter: /bin/sh^M` 失败。
- **`.editorconfig`**：编码与缩进基线（C# 4 空格、XML 类文件与 JSON/YAML 2 空格）。**不设行尾**——那是
  `.gitattributes` 的职责，两处都写就会有两个答案。
- **`build/tools/`**：仓库维护类脚本从 `build/` 根下收拢进来并统一命名——`Generate-ThirdPartyNotices.ps1`、
  `Invoke-SecurityAudit.ps1`（原 `security-audit.ps1`）、`New-AppIcons.ps1`（原 `generate-icons.ps1`）。
  新增 `build/README.md` 的 `tools/` 一节写明哪几份能跨平台跑：前两份只读文件、三平台都可用 `pwsh`，`New-AppIcons.ps1`
  依赖 `mstsc.exe` 的资源段与 GDI+，只能在 Windows 上跑（产物是提交进仓库的，所以不需要别处能跑）。
- **`LICENSE`**：安装包一直带着一份专有 EULA（`build/installer/License.txt`），但仓库根本身没有任何许可声明。
  现在那份文本就是仓库根的 `LICENSE`，MSI 与 `.pkg` 都从它生成，不再是"仓库一份、包一份"的两份拷贝。
- **测试项目命名统一为 `<区域>.<种类>Tests`**：`ArchitectureTests` → `Architecture.UnitTests`、
  `Platform.{Windows,Linux,MacOS}.Tests` → `Platform.{Windows,Linux,MacOS}.UnitTests`，命名空间同步。
  此前同一层混用三种后缀，按 `**/*.UnitTests` 之类的模式筛选会**静默漏掉**项目；规则写进了 `CONTRIBUTING.md`。
- **`build/` 的分层统一为 `<平台>/<角色>`**：原先 Linux 的打包脚本住在 `build/debian/{client,host,server}/`
  （按发行版分），Windows 与 macOS 的住在 `build/installer/{client,server}/{windows,macos}/`（按角色再按平台），
  加一个平台时要先选边站。现在一律是 `build/installer/<平台>/<角色>/`——平台在外，因为它决定一个脚本能不能在
  这台机器上运行；平台名取 `windows` / `macos` / `linux`，不是发行版也不是包格式。分层规则写在新增的
  `build/README.md` 里。
- **服务定义收敛到 `build/service/<平台>/`**：systemd unit 与 `/etc/default` 原先随 `.deb` 脚本放在
  `build/installer/linux/{host,server}/`，launchd plist 原先放在 `build/installer/macos/server/`，Windows 的
  服务安装脚本在 `build/service/windows/`。现在三个平台的服务定义同层并列，"改一个服务的启动方式"只有一个地方
  要找。
- **`build/installer/License.rtf` 不再进仓库**：它是 `New-LicenseRtf.ps1` 从根 `LICENSE` 生成的产物，而两个
  Windows 打包脚本每次构建都会重新生成它。原来它躺在版本库里，改了 `LICENSE` 却忘了重跑脚本时，安装器的许可页
  会静默停留在旧版本，且没有任何构建步骤会发现。现在已 gitignore。
- **`docs/` 按读者分层**：`design/`（架构、协议、安全及其评审）、`ops/`（部署、存储）、`ports/`（macOS / Linux
  移植计划与评审）；文档索引与两份目录结构评审留在 `docs/` 根（这一步后来又被整体重排成"一个目录一份文档"，
  见"未发布"一节）。`docs/README.md` 同时补上了此前漏掉的目录结构评审。
- **目录结构约定改由测试强制**：新增 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs`，
  把六条约定变成断言——`tests/` 的项目名闭集、项目目录名 == `.csproj` 名、`.cs` 文件名与所声明类型的关系、
  `.sh` 的 kebab-case、`.ps1` 的 Verb-Noun（动词须在 PowerShell 批准表上）、`build/` 下的平台与角色目录名。
  三轮目录结构评审每轮都能挑出新的违反，原因是同一个：分层规则有架构测试盯着、三平台移植期间一次没坏过，
  而这些规则只写在 `CONTRIBUTING.md` 里，靠读到的人自觉。现在违反即构建红。
- **产物目录三处修正**：`.trx` 从仓库根的 `TestResults/` 迁到 `artifacts/test-results/<平台>/`；`artifacts/native/`
  改名 `artifacts/lib/`，免得与顶层那棵手写 Objective-C 源码树 `native/` 同名不同义（一个删了就没了、一个随时
  可再生）；安装包的 `.wixpdb` 是调试符号不是交付物，用 `-p:PdbOutputDir=` 留在工程 `obj/` 里，不再落进
  `artifacts/installer/<平台>/`。
- **`TestResults/` 不再由仓库自己的东西产生了**——补完上一条漏掉的一半。改 `run-test-suite` 只把**报告文件**
  挪进了 `artifacts/`，而 `dotnet test` 在没给 `--results-directory` 时仍会在当前目录建一个**空**的
  `TestResults/`；仓库里还有三处这么调用（`verify-packages` 的 Windows 分支、两个安装器脚本的自查步骤），
  所以根上照样会长出目录，只是被 `.gitignore` 挡着看不见。三处都补上了 `--results-directory`（两个脚本指向
  `artifacts\test-results\windows`，CI 那处指向 `artifacts/test-results/windows`）。
  第四处修不掉也不打算修：`CONTRIBUTING.md` 教贡献者跑的那条裸 `dotnet test RemoteDesktop.slnx` 同样会建它，
  那是 Microsoft.Testing.Platform 的默认值而不是本仓库的约定。MTP 的
  `TestingPlatformCommandLineArguments` 属性能把参数烤进测试程序，但烤进去的和 CI 显式传的是同一个选项，
  重复选项很可能让 MTP 直接报错——CI 的正确性比一个空的本地目录重要，所以接受默认值，改在 `CONTRIBUTING.md`
  里写明看到它不必处理。忽略规则保留。
- **修一个静默回归**：产物收敛之后，`HostService` 的 `CopySessionAgent` 仍在往已被搬走的
  `RemoteDesktop.SessionAgent\bin\` 里找文件，而 MSBuild 对空 item 列表不报错——"普通构建会把 agent 放到服务
  旁边"这句话已不成立，构建却一直是绿的，直到 `install-service.ps1` 抛出一句指错方向的提示。改为用
  `GetTargetPath` 向那个工程要输出目录，从此不可能再与布局脱节。
- **`CONTRIBUTING.md` 产物表的三处硬错误**：`artifacts/packages/` 改成 `package/<pivot>/`（SDK 用的是单数，
  且没有项目层）；安装包文件名改成实际的两个 `RemoteDesktop-<版本>-x64.msi` 与
  `RemoteDesktop.Server-<版本>-x64.msi`——文档原先写的带 `Windows` 的名字只是发布时重命名的资产，
  `verify-packages` 的注释里早就记着这个坑"这个仓库已经踩过一次"；`app/<平台>/` 的下一层写明是**交付物**
  而不是角色（`app/windows/client/` 里同时有三个 `.exe`，因为一个 MSI 装三个角色）。
- **`THIRD-PARTY-NOTICES.md` 重新生成**：它已经静默过期——`Directory.Packages.props` 里的 Avalonia 12.1.4 与
  Tmds.DBus 0.95.1 没同步进去，`FFmpeg.AutoGen` 更是整个缺项。与 `License.rtf` 是同一类问题（生成物进了仓库
  却没有防过期的机制），但这份不能靠"不跟踪"解决，CI 侧的防护留待后续。
- **7 个文件名与所声明类型不符的 `.cs` 改名**：`SystemIntegration.cs` 里装的是 `IOPMAssertionKeepAwake`、
  `SystemIntegration.LoginItem.cs` 里是 `MacLoginItem`（两者还都不是 partial 拆分）、`DxgiDisplays.cs` 里是
  `DxgiDisplayEnumerator`、`PortalResponse.cs` 里是 `PortalRequestResult`、`CameraExceptions.cs` 里是
  `CameraUnavailableException`，另有两个测试文件。判据同时写进 `CONTRIBUTING.md`：文件名不是类型时它必须是
  个分组（声明两个以上类型），这样 `IAudio.cs` 这类"一个概念的整组契约"仍然合法。
- **三处改名让名字说明自己是什么**：`build/service/linux/default` → `remotedesktop-server.default`（它变成
  `/etc/default/remotedesktop-server`，`.gitattributes` 里那条逐名规则也随之改成 `*.default` 通配）；
  两份 `HOW-TO-CONFIGURE.txt` → `how-to-configure.txt`；`Generate-ThirdPartyNotices.ps1` →
  `Export-ThirdPartyNotices.ps1`——`Generate` 不在 PowerShell 的批准动词表上，而 `build/README.md`
  原先恰好把它当范例列着。
- **`release.yml` 的 `build-and-release` → `build-windows-and-release`**：它是三个 job 里唯一不带平台词的，
  读 job 列表看不出 Windows 在哪。
- **`icon.png` 与 `icon-256.png` 去重**：从 `mstsc.exe` 抠出来的就是 256×256，所以"母版"与"256 尺寸"是同一张图
  的两个名字，生成器每次都会重造这份重复。现在只写 `icon-256.png`（应用与 `icon.svg` 本来就指向它）。
- **`native/README.md` 与 `build/README.md` 补齐**：前者说明这层放什么、为什么没有 `windows/`（Windows 走 C#
  P/Invoke，没有编译出来的辅助库）与 `rd_` 前缀规则；后者首行声明"本目录不是构建系统"、补上三平台角色集合不
  对称的表（Linux 单独出被控端包，Windows 并进客户端 MSI，macOS 客户端自己就是被控端）、补 `docker/` 是第三条
  轴，并把脚本命名规则从 `build/README.md` 提升上来。
- **`THIRD-PARTY-NOTICES.md` 有了过期防护**：CI 在一个平台 job 里重新生成它并比对，有差异就红并把 diff 打出来。
  上一节记的正是它已经静默过期过一次（缺 `FFmpeg.AutoGen` 整个一项、两个版本号过时），而这类漂移在 review 里
  看不出来。只放一个 job 是因为清单不带 RID 读，三平台同一份。
- **5 个安装器与服务脚本改成 Verb-Noun**：`Build-ClientInstaller.ps1`、`Build-ServerInstaller.ps1`、
  `Set-ServerConfiguration.ps1`（`Configure` 不在 PowerShell 的批准动词表上）、`Install-HostService.ps1`、
  `Uninstall-HostService.ps1`（后两个装/卸的是**被控端**服务，旧名只说 "service"，与 mac 的
  `uninstall-server.sh` 一词之差而角色相反）。结构断言里的豁免清单随之删掉，`build/` 下 12 个 `.ps1` 现在
  全部受约束。`CHANGELOG.md` 与旧评审里的旧名保持原样。
- **`.gitignore` 的构建产物段从 13 条无锚定模式砍到 2 条**：只留 `[Oo]bj/` 与 `**/[Bb]in/*`，并把例外规则
  扩成 `build/installer/windows/**/{bin,obj}/` 盖住深一层的 `CustomActions`。删掉的 `[Dd]ebug/`、`[Rr]elease/`、
  `[Ll]og/` 等**一条都没在干活**——它们按名字匹配树里任何同名目录，实测被忽略的路径集合删改前后
  6019 条逐行相同。留着只是等着 `Log/`、`Release/` 这类目录第一次出现在源码树里时被静默吞掉。
- **发布资产的命名对齐**：客户端资产此前只在 Linux 上带 `Client` 词（那边有三个包，本来就得靠角色词区分），
  现在三个平台都带，七个资产的形状统一为 `<产品>-<角色>-<平台>-<架构>-<tag>.<扩展名>`。架构词**保持**各平台
  自己的说法（Windows `x64` / macOS `arm64` / Linux `amd64`）——Linux 那个与 `.deb` 内的 `Architecture` 字段
  同源（`control` 模板），统一成 `x64` 会让文件名与字段不同词。约定写进 `CONTRIBUTING.md` 的产物布局一节，
  含三平台对照表与两条成因。**产物名没有跟着改**（`artifacts/installer/windows/` 里仍是
  `RemoteDesktop-<版本>-x64.msi`），所以 `verify-packages` 的分平台 glob 不受影响；产物名与资产名不一致这件事
  本身仍在，两份文档里都写着——那是这个仓库踩过的坑。
  顺带修掉 `docs/ops/README.md` 发布一节开头的过期说法：那里写"把**两个**安装包放进同一个 Release"并只列了
  两个 MSI，而流水线实际发**七个**（两个 MSI、两个 `.pkg`、三个 `.deb`），现在按平台列全了。
- **`build-deb.sh` 不再依赖 GNU 专属的 `sed -i`**：改用 `awk` 写临时文件再 `mv`（BSD 的 `sed -i` 把参数当成备份
  后缀，替换里的 `\n` 也不是换行）。

##### 文档与仓库约定

- **文档体系重排：从 27 份收敛到 13 份**（`docs/` 一份正文对应一类读者，`build/` 与 `native/` 各一份），
  目标是"一个目录一份完整文档"——读者按目录名找到东西，打开就是全文，不必猜该看哪一份。
  `docs/` 现在是 `usage/`（使用说明）、`design/`（分层 + 协议 + 安全）、`ops/`（部署 + 打包 + 本机存储）、
  `ports/`（两平台移植总览）、`reviews/`（归档）五份正文，加索引与规范本身。
- **三份设计文档合并为 `docs/design/README.md`**，三份运维文档合并为 `docs/ops/README.md`
  （去掉了打包一节里与部署一节重复的构建说明），两份使用文档合并为 `docs/usage/README.md`
  （平台差异表原先是两份各写一遍）。
- **两份移植计划合并为 `docs/ports/README.md` 并删掉约 1300 行本机实测记录**：那两份文档共 2877 行，
  其中三分之二是"哪条命令、什么输出"的一次性证据，长在"计划"里而计划早已 100% 完成。
  合并后只留**结论**——已确认的决策、每个平台绕不过去的硬约束、明确不做的范围、未验证项与风险。
  现场记录仍可从 git 历史里取回。**代码与构建脚本里 15 处指向 `§3.x` 实测小节的注释同步改成按小节名引用**，
  否则删掉实测就等于留下一堆指向不存在小节的注释。
- **同一天三轮的目录结构评审合订为一份**（`repo-structure-review-2026-10-16.md`，按轮分节），
  不再为每一轮单开文件；命名规则随之简化为"评审用日期"。
- **评审快照正文里的旧文档路径统一替换为现路径**：三份归档此前按"冻结"原样保留旧路径，靠
  `docs/reviews/README.md` 的"以前叫什么"对照表解读；现在直接改成现路径（评审结论一字未动，
  各文件顶部有溯源说明），对照表随之删除——快照冻结的是判断，不是让读者顺着死链接扑空的地址。
- **根 `README.md` 从 395 行的手册精简为落地页**：功能说明、平台支持与限制、被控端进程模型、打包细节全部
  下沉到 `docs/`，README 只留摘要与导航。它此前也重列了 `docs/README.md` 的子集，且已经漏掉四份文档——
  手工维护的第二份索引必然分叉，现在没有第二份了。
- **新增 `docs/STYLE.md`**：文档放哪、叫什么、怎么写、什么时候更新，以及**哪些规则由测试和 CI 强制**。
  此前这些约定散在 `CONTRIBUTING.md`、`build/README.md` 与 `docs/README.md` 三处。
- **新增 `DocumentationTests`（8 条断言）**：必备文件存在、每个文档恰好一个 H1、`docs/` 下没有孤儿文档、
  相对链接与锚点可解析、文件命名规范、评审必须归档在 `docs/reviews/`、README 必备章节。它第一次运行就抓出
  四处死链——`docs/design/README.md` 指向 `ports/`、`docs/design/README.md` 指向 `ops/`，
  两处都是 `docs/` 按读者分目录那一轮留下的，之后一直没人点到。
- **新增 GitHub 标准社区文件**：根 `SECURITY.md`（私有漏洞上报通道，并列出"已知的、不算新发现的问题"）、
  `.github/PULL_REQUEST_TEMPLATE.md`、`.github/ISSUE_TEMPLATE/`（bug 表单带自检与日志提示，config 把安全问题
  指向私有通道）。
- **`build/tools/README.md` 并入 `build/README.md`**：49 行的目录说明不值得单开一份文件，
  而 `build/README.md` 本来就有它那一行索引。
- **CI 新增 `docs` 作业跑 markdownlint**，版本钉在 `0.23.3` 而不是 `@latest`：新规则在次版本里就默认开启
  （MD060 表格列风格就是这么来的，一次报了 249 条本仓库长期在用的表格写法），`@latest` 会让构建在某天早上
  无故变红。同时把全仓 60 处未标语言的代码围栏补上语言（`text` / `bash` / `powershell`），并修掉其余格式问题。

#### Changed

- **宿主装配按平台分开**：`Platform.Windows` / `Platform.MacOS` / `Platform.Linux` 各自注册同一批端口，
  业务代码不知道自己在哪个平台上；客户端由 `ClientPlatformRegistration` 在运行时选层。
- [Posix] **伪终端一份实现服务两个系统**：原先 macOS 与 Linux 各写了一份（会话、等待退出、清理在内约 455 行
  相同逻辑）。现在差异（`TIOCSWINSZ`、`O_NOCTTY`、`POSIX_SPAWN_SETSID`、Darwin 的非变参 `__open` / `__ioctl`）
  全部封在共享 `LibC` 里。顺带修掉一个真 bug：Darwin 上的 spawn flag 曾用 `0x0002`——那是
  `POSIX_SPAWN_SETPGROUP`（进程组，不是会话）——shell 因此拿不到控制终端；现按 Apple 头文件用 `0x0400`。
- [Platform] **`UserContextGuard` 与"用户自己的设置文件"统一到共享层**：macOS 与 Linux 用同一份实现与同一个
  文件格式（`<数据目录>/appsettings.json`，由 `UserSettings` 读写），不再各写一份；数据目录也按平台修正
  （Linux `~/.local/share`、macOS `~/Library/Application Support`，Windows 不变）。
- [Transport] `DeviceIdentity` 加载 PKCS#12 时加 `X509KeyStorageFlags.Exportable`（macOS 上不加则私钥
  不可导出、MsQuic 握手失败；Windows 不受影响，一个代码路径服务三平台）。
- [FileTransfer] 修掉两个"假设被控端是 Windows"的真 bug：`RemoteFileSource.Combine` 不再硬编码 `\`（分隔符
  从被控端返回的路径字符串上读）；`FileAccessPolicy` 按平台区分路径规则（大小写、保留名、内核路径），
  并让根与入参同做链接解析（修 macOS `/var→/private/var` 误判）。
- [Hosts] `ClientHost` / `Program` 去掉 Windows 硬编码；录制器改由 DI 注入——
  `MfVideoRecorderFactory` 从客户端三个 ViewModel 里删掉，那个 `IsWindows()` 分支只会在非 Windows 上让录制
  **静默不工作**。
- [Tests] Windows 专有用例改为 `SkipUnless` 跳过（其他平台上不再计为失败）；`SystemSetupTests` 里与注册表
  无关的用例现在在三个平台都真跑。
- [CI] **三个平台的 CI 与发布流程对齐**。依赖漏洞审计与"Linux runner 跑测试前的准备"各自抽成共享组合动作
  （`audit-packages`、`prepare-linux-tests`），与 `run-test-suite` 一样只有一份，六处调用不会各改一半；
  作业名统一为 `build-windows` / `build-linux` / `build-macos`（Release 侧另有 `build-and-release`）。
  两个 workflow 都声明顶层 `permissions: contents: read`，只有发版那个 job 自己申请 `contents: write`。
- [CI] **CI 现在构建三平台的安装包**：Windows 的两个 MSI 与 macOS 的两个 `.pkg` 也在每个 PR 上构建并校验
  包内容（此前只有 Linux 的三个 `.deb`）。这些脚本原本只在打 tag 时跑，坏了要等到发版才发现；而 `.pkg`
  那条路径此前**一次都没在 runner 上执行过**。
- [CI] **Release 三个平台都自己跑测试与依赖审计**：Linux 那个 job 此前只做 `dotnet publish`（既不跑测试也
  不审依赖），现在与另外两个平台调用同一批共享动作；它的 runner 从 `ubuntu-latest` 钉到 `ubuntu-26.04`，
  与 CI 验证过的那台一致。Windows 那个 job 里内联复制的一份测试脚本（不上传 TRX、不写汇总表）改回调用
  共享动作；macOS 那个 job 补上了此前缺失的依赖审计。
- [CI] **三平台的安装包校验统一成一份共享动作**（`verify-packages`）：打开每个包看里面有什么，而不是数文件
  个数——`.deb` 用 `dpkg-deb --contents` 列内容并读它必须带的手写依赖行，`.pkg` 用 `pkgutil --payload-files`
  列 payload（客户端要带 `Info.plist`、服务端要带各自的 launchd plist），MSI 用仓库自己的
  `InstallerPackageTests` 打开数据库（payload、服务崩溃重启、防火墙规则、快捷方式、向导页与许可文本）。
  CI 与 Release 各三处调用，发版路径上 Linux 与 macOS 的包此前**只被数过数量**：内容不对的包照样会被数到。
- [Build] **构建产物收敛到 `artifacts/`**：根 `Directory.Build.props` 打开 `UseArtifactsOutput` 并把
  `ArtifactsPath` 指向仓库根，于是 23 个项目不再各自长出 `bin/` 与 `obj/`（此前是 46 个目录要清、要忽略、
  要解释），输出统一落在 `artifacts/bin|obj|publish|packages/<项目>/<pivot>/`——与打包脚本一直在写的
  `artifacts/{app,installer,native}/<平台>/` 同一个根。`build/installer/` 下的 WiX 与 net48 CustomActions
  项目带着自己那份（故意留空的）`Directory.Build.props`，不受影响，产物仍在项目内。
- [Build] **`.deb` 的输出路径从 `artifacts/debian/` 改为 `artifacts/installer/linux/`**：`CONTRIBUTING.md` 写死了
  `<类别>/<平台>/` 的形状、并明确禁止"同一类别两个根"，而 `debian` 是发行版不是平台。现在三个平台的安装包
  都在 `installer/<平台>/` 下，`.deb` 与 `.msi`、`.pkg` 并列。
- [Build] `make-app.sh` 不再写死客户端的 publish 目录（原先硬编码
  `src/Hosts/RemoteDesktop.Client/bin/<配置>/net10.0/osx-arm64/publish`，既耦合布局也耦合 RID），改为向 MSBuild
  询问 `PublishDir`；`build-installer.ps1` 清理 SessionAgent 产物时同理改用 `artifacts/` 下的新位置。
- [Docs] `docs/README.md` 补上漏列的 `security-review.md`；`HOW-TO-CONFIGURE.txt` 取代原先带中文的
  `如何配置.txt` 文件名（开始菜单里的入口名「服务器配置说明」不变），仓库内已无任何非 ASCII 路径。
- [Naming] `RemoteDesktop.Posix` → `RemoteDesktop.Platform.Posix`：与 `Platform.{Windows,Linux,MacOS}` 对称。
  这同时堵上一个洞——架构规则按**程序集名前缀**加载程序集，`RemoteDesktop.Posix` 因此一直没被
  `Platform` 那条规则看到（见 Fixed）。

#### Fixed

- [UI] **Linux 上的三处平台说明文案说成了 macOS 的**：`高级设置` 里由 `OperatingSystem.Is*()` 选边的说明文字
  （固定密码存储、服务器地址改动、本机控制端代码）写的是"Windows / 非 Windows"二分支，非 Windows 即
  macOS——macOS 移植期定下的，Linux 合入后读到的全是 macOS 的说法：密钥说成"macOS 钥匙串"（实际是仅本
  用户可读写的 0600 文件）、进程模型说成"客户端与被控端是同一个进程"（实际是 `systemd --user` 服务）、
  设备代码说成"和主页是同一个"（服务模式下是两串代码，与 Windows 同构）。现在按平台三分支
  （本机控制端代码按被控端形态二分，Windows 与 Linux 同文），"无法联系后台服务"一并恢复在 Linux 上的
  准确措辞；新增 `MainWindowPlatformNoteTests`，三个 CI 平台各验自己那支。
- [UI] **固定密码区块在 Linux 上没有说明它不能使其无人值守**：portal 不允许远程桌面会话持久授权，控制权限
  每次连接都要现场重新授权，只有被观看不受此限（`docs/usage/README.md` §1）。Linux 的设置页现在补一行
  说明；另把 macOS 登录项说明里自相矛盾的"不需要有人先登录"改为"不需要有人手动打开"。
- **`SpaPod` 解析器少读 4 个字节**：SPA 对象体是 `{type, id}`，没有属性个数，而解析器在 id 后面读了一个"个数"
  ——那其实是第一个属性的 key（真实值是 131073）。此前的测试全绿是因为用例字节按同一个错误理解手写。
  这类错误在真实链路上的表现是"格式协商莫名其妙失败"。
- **portal 文件描述符的竞态**：`CallForFdAsync` 原本在 `CallMethodAsync` 返回之后才 dup 那个 fd，而 fd 属于
  message、message 在读取委托返回后就释放了——偶尔 dup 到一个已关闭的编号，报 EBADF，而连接本身好好的。
  平时不显形，测试并行度上来才出现。
- **Linux 上启动失败不再一声不吭**：`StartupFailure` 原本只在 Windows 弹消息框，非 Windows 什么都不做，
  于是失败只留下退出码 1 和一个**没人打印出路径**的 `crash.log`。现在同一句话也写到标准错误。
- **没有摄像头的机器不再白弹一次授权窗**：枚举先读 `IsCameraPresent`（属性，不弹窗），为假就直接返回空。
- [FileTransfer/macOS] **符号链接目标要再解析**：macOS 上根内合法链接曾被误拒（`/var` 是 `/private/var`
  的链接）。
- [macOS/打包] **`fetch-libmsquic.sh` 随包分发的依赖集原先靠写死的 openssl@4**，而 Homebrew 当前
  libmsquic bottle 直接链接 `libcrypto.3`（openssl@3）——**装出来的 .app / .pkg 里 QUIC 会起不来**，
  且所有不真正加载的检查（文件在、`otool` 正常、签名已打）都会放行。现在依赖从 bottle 的声明里读、
  引用按二进制里的实际形状收集（bottle 占位符与 `@loader_path` 两种），并且**结束前把每个 staged
  文件真的 `dlopen` 一遍**，缺件当场报出 dyld 的原因而不是等到运行时"QUIC is not supported"。
- [CI/发布] **`release.yml` 按错的名字去找两个 MSI**：构建产物叫 `RemoteDesktop-<版本>-x64.msi` 与
  `RemoteDesktop.Server-<版本>-x64.msi`（两个 wixproj 的 `OutputName`），而重命名那一步按**发布产物**的名字
  （`RemoteDesktop-Windows-*.msi`）去 glob，两个都找不到，会在"少一个就什么都不发"的检查上直接抛错。
  这条路径在 v0.3.0 之后一次都没跑过，所以一直没被发现；同一次改动把三平台的产物校验统一成了共享动作，
  它按构建产物的真实名字找包，并断言"一个 glob 恰好命中一个"。
- [Tests] **架构规则有两个平台层从未被检查过**：`DependencyRuleTests` 按程序集名前缀从测试输出目录里加载
  程序集，于是 `RemoteDesktop.Posix`（前缀对不上 `RemoteDesktop.Platform`）和 `RemoteDesktop.Platform.MacOS`
  （根本没被 `ArchitectureTests.csproj` 引用）都不在检查范围内——而那个文件第 5 行的注释正是警告"没被引用的
  平台会让套件全绿却什么都没检查"。前者随 `RemoteDesktop.Platform.Posix` 改名解决，后者补上项目引用；
  现在四个平台程序集都在规则内，且都合规。
- [Tests/macOS] **`The_frame_can_be_disposed_and_another_captured` 在 CI 上高频失败**：它先捕获一帧、释放它、
  再捕获一帧，用来验证"释放上一帧之后捕获仍然正常"。守卫只盖了第一帧（"runner 什么都没画，所以没有帧"），
  却要求第二帧必须到来——而 ScreenCaptureKit 是**画面有变化时**才交付新帧，runner 的显示器正好不会变化：
  第一次捕获拿走了当前那帧，第二次要等一帧新的。**这个假设在静止显示器上恰好是反的**，第二帧比第一帧更容易
  为空。同一份代码三次运行里挂了两次，master 因此变红。现在第二帧缺席也按同一理由跳过；生命周期检查仍在
  （非空时那两行断言照跑），只是不再把一个环境事实判成失败。

#### 平台上的边界与未验证项

**macOS**

- 最低系统版本为 **macOS 26 (Tahoe)**，由随包分发的 `libmsquic` / `openssl@4` bottle 的 `minos 26.0` 决定
  （见 `docs/ports/README.md` 的"最低系统版本是 macOS 26"）。降低下限需改走"从源码构建 MsQuic"。
- 已知未覆盖（需人工授权，自动化测试无法验证）：辅助功能授权下的真实输入注入、受保护目录
  （`~/Desktop` / `~/Documents` / `~/Downloads`）的 TCC 弹窗路径。

**Linux**

- **登录界面 / 登录前的远程访问**：GNOME 的登录界面是另一个用户、另一个 VT，私有用户会话够不着。
  Windows 上能换 Agent 接管安全桌面，这里没有对应物。
- **无人值守下的操作**：portal 不允许远程桌面会话持久化，所以**控制权限每次连接都要有人在机器前点一次同意**。
  共享屏幕授权一次就记住了，但无人值守的机器因此**可以被观看、不能被操作**。
- 剪贴板已实现并双向验证（走 portal：在远程桌面会话开始前申请，发布用 `SetSelection`、读取用
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
  固定密码仍需要它，见 `docs/design/README.md`。协议主版本随之升到 2，新旧版本会明确报"版本不兼容"。
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
- [Docs] 新增系统架构设计与网络通信协议文档（现在合并在 `docs/design/README.md`）。
- [Docs] 新增网络通信协议规范文档（现在合并在 `docs/design/README.md`）。
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
  部署文档（现 `docs/ops/README.md`，时称 `deployment.md`）用 `sc.exe failure` 手工安装那条路才有——而
  README 已经把它当成安装包的行为写着了。
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
