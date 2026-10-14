# Remote Desktop 文档中心

欢迎来到项目文档中心。这里详细记录了 RemoteDesktop 的架构设计、通信协议、安全机制以及部署指南。

## 📖 目录 (Table of Contents)

### 1. 核心设计 (Design & Architecture)
* [**系统架构设计 (architecture.md)**](architecture.md)
  * 领域驱动设计 (DDD)、Clean Architecture 依赖流向、组件指责与进程模型。
* [**网络通信协议规范 (protocol.md)**](protocol.md)
  * 信令层 (WebSocket) 与 传输层 (QUIC) 交互流程，多路复用帧结构。
* [**安全机制说明 (security.md)**](security.md)
  * 端到端加密 (E2EE)、密码证明防重放攻击、权限隔离与降级。

### 2. 部署与运维 (Ops)
* [**部署指南 (deployment.md)**](deployment.md)
  * Rendezvous / Relay 服务端的部署要求、网络端口开放及客户端直连说明。
* [**设备列表与存储 (device-list-storage.md)**](device-list-storage.md)
  * 客户端本地存储结构及账号系统同步升级预留接口。

### 3. 平台移植 (Platform Ports)
* [**macOS (arm64) 移植实现计划 (macos-port-plan.md)**](macos-port-plan.md)
  * P1–P10 的分阶段计划、本机实测记录（原生互操作、TCC、打包签名、VideoToolbox 等）与风险清单。
* [**macOS (arm64) 移植评审 (macos-port-review.md)**](macos-port-review.md)
  * 架构与技术选型评估、对原体系的影响评估、遗留技术债与改进建议。
* [**Linux 平台移植计划 (linux-port-plan.md)**](linux-port-plan.md)
  * 端口清单、进程模型与权限模型的差异、本机实测结论（MsQuic、portal 能力、显示枚举）与实施阶段。

### 4. 开发指引
* 请参阅根目录下的 [`CONTRIBUTING.md`](../CONTRIBUTING.md) 了解代码提交、环境搭建与架构测试规范。
* 项目历史与版本迭代请参考 [`CHANGELOG.md`](../CHANGELOG.md)。
