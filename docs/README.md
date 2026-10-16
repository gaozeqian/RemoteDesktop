# Remote Desktop 文档中心

欢迎来到项目文档中心。这里详细记录了 RemoteDesktop 的架构设计、通信协议、安全机制以及部署指南。

文档按**读者**分目录，而不是平铺在一层：

| 目录 | 给谁看 |
|---|---|
| [`design/`](design/) | 要改代码的人：架构、协议、安全机制及其评审 |
| [`ops/`](ops/) | 要部署和运维的人：服务端部署、客户端本地存储 |
| [`ports/`](ports/) | 要把产品搬到某个平台的人：macOS / Linux 的移植计划与评审 |
| 本目录 | 仓库自身的记录：文档索引（本文件）与目录结构评审 |

## 📖 目录 (Table of Contents)

### 1. 核心设计 (Design & Architecture) — `design/`
* [**系统架构设计 (architecture.md)**](design/architecture.md)
  * 领域驱动设计 (DDD)、Clean Architecture 依赖流向、组件指责与进程模型。
* [**网络通信协议规范 (protocol.md)**](design/protocol.md)
  * 信令层 (WebSocket) 与 传输层 (QUIC) 交互流程，多路复用帧结构。
* [**安全机制说明 (security.md)**](design/security.md)
  * 端到端加密 (E2EE)、密码证明防重放攻击、权限隔离与降级。
* [**安全评审 (security-review.md)**](design/security-review.md)
  * 对现有安全设计的逐项复核：已缓解的风险、仍待产品决策的项与上线前检查清单。

### 2. 部署与运维 (Ops) — `ops/`
* [**部署指南 (deployment.md)**](ops/deployment.md)
  * Rendezvous / Relay 服务端的部署要求、网络端口开放及客户端直连说明。
* [**设备列表与存储 (device-list-storage.md)**](ops/device-list-storage.md)
  * 客户端本地存储结构及账号系统同步升级预留接口。

### 3. 平台移植 (Platform Ports) — `ports/`
* [**macOS (arm64) 移植实现计划 (macos-port-plan.md)**](ports/macos-port-plan.md)
  * P1–P10 的分阶段计划、本机实测记录（原生互操作、TCC、打包签名、VideoToolbox 等）与风险清单。
* [**macOS (arm64) 移植评审 (macos-port-review.md)**](ports/macos-port-review.md)
  * 架构与技术选型评估、对原体系的影响评估、遗留技术债与改进建议。
* [**Linux 平台移植计划 (linux-port-plan.md)**](ports/linux-port-plan.md)
  * 端口清单、进程模型与权限模型的差异、本机实测结论（MsQuic、portal 能力、显示枚举）与实施阶段。

### 4. 仓库结构评审（本目录）
* [**目录结构评审（第一轮）**](directory-structure-review.md)
  * 按跨平台仓库规范逐项核对：顶层分层、产物根、行尾声明、平台目录对称性；末尾附"修复状态"表。
* [**目录结构评审（第二轮 · PR Review）**](directory-structure-review-2.md)
  * 对第一轮修复的落地核验，以及 `build/` 分类轴、服务定义位置等新增问题；末尾附修复状态。
* [**目录结构评审（第三轮 · 全量普查）**](directory-structure-review-3.md)
  * 前两轮是抽检，这轮把每个目录与每个文件名逐个过一遍，并把产物目录在磁盘上的真实形状量出来；
    同时把几条一直只写在文档里、无人强制的约定变成 `RepositoryStructureTests` 的断言。

> 这三份是**当时的评审记录**，不是现行约定。现行约定在 [`CONTRIBUTING.md`](../CONTRIBUTING.md) 与根目录的
> `README.md` 里；评审里写了"建议"而后来没被采纳的项，以这两份文件的记录为准。

#### 文档后缀的含义

`docs/` 下的文件名后缀只有两种含义，别混用：

| 后缀 | 指什么 | 会更新吗 |
|---|---|---|
| `-plan` | **还要做的事**：计划、阶段划分、风险清单 | 会，随进展更新 |
| `-review` | **某次评审的快照**：写日期，记当时看到的事实与结论 | **不会**，写到哪算哪——它是记录，不是规范 |

按这条，`security-review.md` 是快照（虽然它的内容读起来像现行清单），`ports/` 下只有 macOS 有 review 是因为
只有那次移植单独评过，不是 Linux 漏了。三份 `directory-structure-review*` 同理，是快照。

新的评审用**日期**命名（`xxx-review-2026-11-02.md`），不再用 `-2`、`-3` 这类序号：序号读不出时间，而且会一直
加下去。最后一份序号命名的就是本轮的 `directory-structure-review-3.md`。

### 5. 开发指引
* 请参阅根目录下的 [`CONTRIBUTING.md`](../CONTRIBUTING.md) 了解代码提交、环境搭建与架构测试规范。
* 项目历史与版本迭代请参考 [`CHANGELOG.md`](../CHANGELOG.md)。
