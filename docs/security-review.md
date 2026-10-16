# 安全审查报告

> 审查时间：2026-10-12　审查范围：全项目（代码、构建、部署、网络协议）  
> 方法：代码审查 + 单元/集成测试覆盖盘点 + 对抗性实测（MITM 抓包、反射放大、爆破成本、soak、协议 fuzz、连接洪泛）

## 概览

共发现 **13 项问题**，按严重程度分级：**高危 2 项、中危 3 项、低危 5 项、技术债 3 项**。  
其中 1 项已修复，2 项待决策，其余为已知/需记录。

| 级别    | 数量 | 状态            |
| ----- | -- | ------------- |
| 🔴 高危 | 2  | 1 已修复，1 待修复决策 |
| 🟡 中危 | 3  | 待决策           |
| 🟢 低危 | 5  | 已知/有意设计，记录    |
| ⚪ 技术债 | 3  | 过时方案，记录       |

---

## 🔴 高危

### 1. QuicListener 每连接泄漏 native 内存（~1.2MB/次）

**问题**：`QuicPeerListener.HandleAsync` 每收到一个入站连接请求，就 `QuicListener.ListenAsync` 新建一个 listener（绑定打洞端口），用后 `DisposeAsync`。但 MsQuic 的 listener 原生资源 Dispose 后未完全释放，**每连接泄漏约 1.2MB**，长时间运行会 OOM。

**证据**（三个隔离实验，300 轮连接对比）：

| 实验 | 场景                        | 工作集增长   | 趋势       |
| -- | ------------------------- | ------- | -------- |
| C  | 纯 MsQuic 连接，listener 复用   | +42 MB  | 趋于平稳     |
| D  | 纯 MsQuic 连接，listener 每轮新建 | +363 MB | **线性增长** |
| B  | 项目完整连接                    | +375 MB | 线性增长     |

托管堆稳定（+0~1MB）、句柄稳定，证明是纯 native 泄漏。

**影响**：无人值守设备反复被连（远程运维/监控），内存持续上涨直到崩溃。

**解决方案**（追查后修正，见下方结论）：
- 实验 F 证明泄漏在 `QuicListener.ListenAsync`+`DisposeAsync` **本身**（无连接、callback 未调用即泄漏 ~1.1MB/轮），是 **.NET/MsQuic 的 listener 生命周期原生 bug**。
- 原方案 A（listener 端口池）**不可行**：NAT 打洞要求"打洞端口 = candidate 端口 = QUIC 监听端口"，复用 listener 会破坏打洞。
- 实际路径：① 向 dotnet/runtime 上报 bug；② 或投入 QUIC 层底层重构（改用 MsQuic C API）；③ 过渡期监控内存。

**位置**：`src/Core/RemoteDesktop.Transport/Quic/QuicPeerListener.cs` 第 65 行  
**状态**：🔶 已确认根因（.NET/MsQuic 原生 bug），需上游修复

---

### 2. 符号链接（junction）沙盒逃逸

**问题**：`FileAccessPolicy.Normalize` 之前只做纯字符串归一化（`Path.GetFullPath` + 前缀比较），不解析 reparse point。攻击者在共享根目录放一个指向根外的 junction，就能"合法"读写/删除根外文件，绕过路径沙盒。

**证据**：新增 `SymbolicLinkEscapeTests` 证明逃逸（4 个测试，逃逸被拒 + 根内链接放行）。

**影响**：文件传输的"根目录限制"形同虚设，越权读写。

**解决方案**：`Normalize` 中新增 `ResolveFinalTarget`，逐段用 `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)` 解析已存在的链接段，用最终真实路径做根检查（已实现）。

**位置**：`src/Features/RemoteDesktop.Features.FileTransfer/FileAccessPolicy.cs`  
**状态**：✅ 已修复 + 回归测试（核心单测 207 + 文件传输集成 20 全部通过）

---

## 🟡 中危

### 3. 固定密码熵低，GPU 秒级/分钟级可破

**问题**：固定密码最短 6 位、由人自选，熵很低。实测 PBKDF2-HMAC-SHA256(60万轮) 本机 CPU 单核仅 2.6 次/秒，但 GPU 比 CPU 快约 100~1000 倍。

**实测数据**（固定密码 6 位枚举时间）：

| 字符集    | 空间       | CPU(16核) | GPU(A4000/4090 量级) |
| ------ | -------- | -------- | ------------------ |
| 纯数字    | 10⁶      | 6.6 小时   | **秒级**             |
| 小写+数字  | 2.2×10⁹  | 2 年      | **数分钟**            |
| 大小写+数字 | 5.7×10¹⁰ | 43 年     | ~1.6 小时            |

对比：临时密码 12 位（32 字符）≈ 2^60，即便 GPU 也不可行——**固定密码才是薄弱点**。

**解决方案**：

- 上调固定密码最短长度（如 ≥10 位，服务端强制）；
- 或加速 PAKE 迁移（见下一条）。

**位置**：`src/Core/RemoteDesktop.Application/Sessions/FixedPasswordPolicy.cs`（最短 6 位）、`PasswordProof.cs`（60万轮 PBKDF2）  
**状态**：🔶 待决策

---

### 4. 密码证明不是 PAKE（可离线爆破）

**问题**：`PasswordProof` 是挑战-应答，不是 PAKE。任何能冒充被控端完成 TLS 握手的一方（恶意/被劫持的信令服务器，或明文信令下的中间人），都能拿到一份可离线爆破的证明。

**缓解（已做）**：口令先经 60 万轮 PBKDF2；盐是每次连接新生的 nonce；配合失败限流。这让每次猜测贵了约 6 个数量级。

**未做**：把攻击从"离线可爆破"变成"不可爆破"。

**解决方案**：迁 SPAKE2 或 OPAQUE（需选经过审阅的实现或成熟库，**不手写 PAKE**）。

**位置**：`src/Core/RemoteDesktop.Application/Sessions/PasswordProof.cs`（文档已声明非 PAKE）  
**状态**：🔶 已知 + 文档已声明，待迁移

---

### 5. 明文信令下中继票据可被抓（MITM）

**问题**：`ws://` 明文信令下，中继票据（ticket）、设备证书、公钥、指纹、所有内网 IP 在途可被抓。已实测抓取到完整票据（57 字节 hex）——任何中间人都能抢用中继带宽。

**证据**：MITM 透明代理实测抓到 `ConnectReady` 帧中的 `relay.ticket`，与工具独立输出完全一致。

**影响**：中继带宽被盗用；元数据（谁连谁、设备代码、IP）泄露。

**解决方案**：

- **部署者必须配 wss://**（`docs/deployment.md` 已详述，两种做法：服务器自带证书 / 反向代理）；
- 可选增强：客户端对信令服务器做证书指纹固定（当前只走系统信任链）。

**位置**：`docs/security.md`、`docs/deployment.md` 已声明  
**状态**：🔶 已知，依赖部署者配置（风险已实测证实）

---

## 🟢 低危（已知 / 有意设计，记录）

### 6. 开发密钥提交进仓库

仓库 `appsettings.Development.json` 里 `RelaySharedKey` 为明文开发密钥（`FWNwaTxD3q0...`）。

- **风险**：误用开发配置上公网会裸奔。
- **缓解（已有）**：Docker/服务器启动时校验密钥长度；文档已声明生产必须换。
- **建议**：README 顶部加醒目提醒，或提供"上线前检查清单"（已有 `build/tools/Invoke-SecurityAudit.ps1` 可自动扫描）。

**状态**：✅ 已提供 `build/tools/Invoke-SecurityAudit.ps1` 自动扫描（三个平台都可用 pwsh 运行）

### 7. 本机用户之间不区分

任何已登录本机用户都能通过控制管道读到设备代码、临时密码、开关摄像头。

- **性质**：**有意设计**（堵住它会让 RDP 管理员/无头机器场景失效）。
- **建议**：多人共用且互不信任的机器上，应将本机用户都视为可信；需产品决策。

**状态**：🔵 记录（有意设计）

### 8. .gitignore 未忽略 `.cer` 导出证书

`.gitignore` 忽略了 `.pfx` 和 `build/signing/certificates/`，但未忽略 `.cer`。

- **建议**：加 `*.cer`（公钥本身非机密，但混入仓库会造成混乱）。

**状态**：🔵 记录（当前无害，待补规则）

### 9. Docker Compose 的 PostgreSQL 数据卷路径

`docker-compose.yml` 数据卷挂到 `/var/lib/postgresql`（父目录），而 pg18 的 `PGDATA` 默认是版本化子目录（`/var/lib/postgresql/18/docker`）。

- **风险**：数据可能落到非预期子目录，或与镜像内部结构冲突。
- **建议**：挂到 `PGDATA` 实际路径（`/var/lib/postgresql/data`），或显式设置 `PGDATA` 环境变量。

**状态**：🔵 记录（需验证 pg18 行为）

### 10. CI 测试"失败自动重跑一次"会掩盖偶发 bug

`ci.yml`/`release.yml` 的集成测试失败后重跑一次，两次都失败才判红。

- **性质**：集成测试（真实 PTY/UDP 打洞/Media Foundation）在 runner 上确实偶发，注释已诚实声明代价。
- **建议**：保留，但记录——真实偶发 bug 可能被这一次重试掩盖。

**状态**：🔵 记录（已知权衡）

---

## ⚪ 技术债（过时方案）

| #  | 问题                               | 说明                                           | 建议                               |
| -- | -------------------------------- | -------------------------------------------- | -------------------------------- |
| 11 | WiX v5                           | 已停止社区支持；v6 商用需 OSMF 授权                       | 无商业化计划则评估 v6；否则保留 v5 接受靠包内容测试兜底  |
| 12 | `softprops/action-gh-release@v3` | 第三方 Action 上游基本停维护                           | 迁官方 `gh release` CLI 或仍在维护的 fork |
| 13 | `xunit.runner.visualstudio` 冗余   | 已迁 Microsoft.Testing.Platform，该 VSTest 适配器冗余 | 移除                               |

---

## ✅ 已验证安全（无需处理）

以下机制经代码审查 + 测试确认安全，作为本次审查的"排除清单"：

- **密码证明绑定**：`HMAC(PBKDF2(password, nonce) ‖ nonce ‖ 双方证书指纹)`，`FixedTimeEquals` 防时序，`ZeroMemory` 清密钥
- **限流**：单设备 5 次/5 分钟 + 全局 20 次/1 分钟；握手并发 32；会话上限 16
- **服务器限额**：全局 4096 / 每地址 64，**实测升级前拒绝（503）**
- **中继两步绑定**：RelayCookie 与票据/角色/地址/时间窗绑定，防"隐藏来源发包"
- **反射放大**：反射器 1.375x（无放大面），中继对无效票据不回包（实测）
- **UDP 协议健壮性**：24 种畸形包全部静默忽略（fuzz 实测）
- **TLS 信令**：不受信任证书被拒（集成测试）
- **文件沙盒**：设备路径/ADS/保留名/`..` 越界拒绝（修复符号链接后）
- **SYSTEM 身份拒绝**：文件/终端/摄像头在锁屏/登录界面下拒绝
- **UIAccess 设计**：签名 + Program Files 目录 + SYSTEM 拉起，分层授权正确
- **设备密钥存储**：DPAPI + 目录 ACL（仅 SYSTEM/管理员）
- **依赖漏洞门禁**：CI + release 均跑 `dotnet list package --vulnerable`（release 已补）

---

## 待办清单（按优先级）

> 更新于 2026-10-12 下午：第 2–5 项已完成，第 1 项已确认根因（.NET/MsQuic 原生 bug）。

1. 🔴 **修复 QuicListener 泄漏**——已追查定位：实验 F 证明仅 `ListenAsync`+`DisposeAsync`（无连接、callback 未调用）即泄漏 ~1.1MB/轮，是 **.NET/MsQuic 的 QuicListener 生命周期原生 bug**；且"NAT 打洞端口 = candidate 端口 = QUIC 监听端口"是架构硬约束，复用 listener 会破坏打洞。**当前无法安全修复**，需向 dotnet/runtime 上报，或投入 QUIC 层底层重构。
2. ✅ **固定密码最短长度 6→8 位**——已改 `FixedPasswordPolicy.MinimumLength`、UI、README、docs/security.md。PAKE 迁移仍是根治方案。
3. ✅ **补 `.gitignore` 的 `*.cer` 规则**——已加。
4. ✅ **Docker PG 数据卷路径**——查证确认 PG 18+ 的正确挂载点就是 `/var/lib/postgresql`（父目录），项目已正确，无需修改。
5. ✅ **替换过时依赖**——删除冗余 `xunit.runner.visualstudio`，`softprops/action-gh-release@v3` 改为官方 `gh release create`。
