# 更新日志 (Changelog)

本项目所有的显著变更都会记录在此文件中。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)。

## [Unreleased] - 里程碑 (M8 & 持续优化)

### Added
- [Docs] 新增系统架构设计文档 (`docs/architecture.md`)。
- [Docs] 新增网络通信协议规范文档 (`docs/protocol.md`)。
- [Docs] 新增文档中心索引与贡献指南 (`CONTRIBUTING.md`)。
- [Installer] 引入基于 WiX v5 的 Windows 安装包打包脚本。

### Changed
- [Repo] 优化项目文档组织结构，统一采用 Kebab-case 命名并提供全局导航。

### Fixed
- (暂无)

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
