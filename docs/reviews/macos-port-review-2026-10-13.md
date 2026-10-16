# macOS (arm64) 移植评审

> **历史快照 · 已冻结** — 本文件是 2026-10-13 的评审记录，**不再更新**。后续进展与最终做法见现行的
> [`docs/ports/README.md`](../ports/README.md)。归档说明见 [`docs/reviews/README.md`](README.md)。
>
> 评审对象：`feat/macos-arm64-port` 分支（P1–P10，`master`..`633c837`）
> 评审日期：2026-10-13
> 结论：**架构方案正确，技术选型基本最优，对原体系影响极小且高度契合。**
> 遗留 1 项主要技术债（最低系统版本）、2 项一致性改进项、若干文档整合缺口。详见下。

---

## 0. 结论摘要

| 维度 | 结论 | 说明 |
| --- | --- | --- |
| 原生互操作方案 | ✅ **最优** | 方案 C（`net10.0` + C P/Invoke + 小型 ObjC 辅助库）是本代码库下的最优解 |
| 端口抽象 / 依赖倒置 | ✅ **最优** | 核心层零改动，是原架构红利的兑现 |
| DI 装配 | ✅ **正确** | 与 Windows 逐项镜像，运行时选平台 |
| 技术选型 | ✅ **基本最优** | 11 项能力里 10 项最优，1 项（`libmsquic`）是有记录的权衡 |
| 对原体系影响 | ✅ **高度契合** | 依赖规则完好，共享层仅 ~10 处必要改动，无一处"为 macOS 让路" |
| 主要技术债 | ⚠️ **1 项** | `libmsquic` bottle 的 `minos 26.0` 把最低系统版本顶到 macOS 26 |
| 一致性改进 | 🔸 **2 项** | `ILoginItem` 注解、原生互操作面按域拆分 |

改动面：**90 文件 / +16360 / −121**。其中平台层新增 ~5.4k 行托管代码 + 4814 行原生（11 个 `.m`/`.h`，44 个导出入口），共享层只动了 ~10 处。

---

## 1. 架构设计评估

### 1.1 原生互操作方案：混合方案 C —— 最优

| 方案 | 做法 | 评价 |
| --- | --- | --- |
| **(C) 混合** | 平台项目保持 `net10.0`；C 框架直连 P/Invoke；ObjC-only 框架走一个小型辅助库（`native/macos` → `libRemoteDesktopMacNative.dylib`，扁平 C ABI） | ✅ **采用** |
| (A) `net10.0-macos` | 直接用 .NET for macOS 官方绑定（ScreenCaptureKit/AVFoundation 全有） | ❌ 宿主必须多目标；引入 Xcode/workload；Avalonia 跑在 `net10.0-macos` 属非常规组合 |

**为什么 C 是本代码库下的最优解：**

1. **宿主保持单一 TFM**：`ClientPlatformRegistration.AddClientPlatform` 在运行时二选一，一个 Client 项目同时支持 Windows 与 macOS；ILLink 按目标 OS 把另一分支整段裁掉。方案 A 会让宿主变成多目标，直接破坏这个结构。
2. **与既有风格一致**：Windows 侧本来就是"手写绑定"（Vortice / 手写 COM 互操作）。C 方案让两个平台层风格统一。
3. **架构测试零成本覆盖**：`DependencyRuleTests` 按**前缀**加载程序集（`LoadAssemblies("RemoteDesktop.Platform")`），新增 `RemoteDesktop.Platform.MacOS` 自动纳入既有规则，无需改测试。
4. **代价可接受**：多一个原生产物（dylib）+ 构建需要 `clang`（Xcode CLT）；`Platform.MacOS.csproj` 用 `IsOSPlatform('OSX')` 条件把它限制在 Mac 上触发。

**结论：没有更优方案。** 方案 A 在"单宿主、双平台、Avalonia"这个约束下是退步。

### 1.2 端口抽象与依赖倒置 —— 最优，且是原架构红利的兑现

macOS 实现的是与 Windows **完全相同**的 ~16 个端口接口，因此：

- `Domain` / `Application` / `Media` / `Protocol` / `Signaling` **零改动**（见 §3.1 的改动面清单）。
- `Features` 仅 FileTransfer 因两个"假设 Windows 被控端"的**真 bug** 而改（属跨平台修 bug，不是为 macOS 让步）。
- 四个功能模块（Desktop / FileTransfer / Terminal / Camera）**原样复用**。

这正是原架构（Clean Architecture + 端口/适配器）的价值兑现：**新增一个平台 = 新增一个适配器项目，而不是改核心。**

### 1.3 DI 装配 —— 正确

`AddMacOSPlatform` 与 `AddWindowsPlatform` 逐项镜像，连"为什么用单例 / 为什么用工厂"的理由都一一对应（摄像头枚举器单例、剪贴板用工厂以便容器 Dispose 停掉轮询）。`ClientPlatformRegistration` 在运行时二选一。干净、可对照。

### 1.4 需要改进项（一致性）

**① `ILoginItem` 接口不应带 `[SupportedOSPlatform("macos")]`（低风险，建议改）**

- 现状：15 个既有端口（`ISecretStore`、`IScreenCapturer`、`IVideoRecorder`…）**全部是平台中立的**，OS 标注一律在**实现类**上。`ISecretStore` 的注释甚至写着 "DPAPI, Keychain, libsecret"。
- 而新端口 `ILoginItem` 把 `[SupportedOSPlatform("macos")]` 加在了**接口**上，锁死了一个本可跨平台的概念（Windows 可用计划任务/启动项，Linux 可用 systemd user unit）。
- 后果：调用方被强制做 OS 判断 —— `SelfCheck` 因此不得不多加一层 `OperatingSystem.IsMacOS()` 守卫与 `[SupportedOSPlatform("macos")]`。
- **建议**：把特性从 `ILoginItem` 移到 `MacLoginItem`，与其它 15 个端口对齐。

**② 原生互操作面随里程碑线性增长，建议按域拆分（中风险，可选）**

- 现状：`rd_native.h` 637 行 / 44 个导出入口；`MacNativeInterop.cs` 619 行（已含 Video / Recording / Login item 等 region）。
- 目前仍在可控范围，但**每个里程碑都在往同一个头文件/类里追加**，趋势是单调增长。
- **建议**：头文件按域拆分（`rd_capture.h` / `rd_audio.h` / `rd_video.h` / `rd_recorder.h` / `rd_system.h`），`MacNativeInterop` 改 `partial class` 按域分文件。**扁平 C ABI 不变**，只是文件组织。
- 注意：这是可选项，当前规模不构成缺陷。

---

## 2. 技术选型评估

| 能力 | 选型 | 评价 | 备注 / 备选 |
| --- | --- | --- | --- |
| 屏幕采集 | **ScreenCaptureKit** | ✅ 最优 | `CGDisplayStream` 已废弃；SCK 是 macOS 12.3+ 的正路 |
| 音频环回 | **ScreenCaptureKit 音频** | ✅ 最优 | 复用屏幕录制授权，**不新增麦克风权限**；备选 BlackHole 需用户装驱动 |
| 视频编解码 | **VideoToolbox H.264** | ✅ 最优 | 硬件优先 + 软件兜底，双工厂自然成立；HEVC 故意不做（线上格式是 H.264） |
| 录制封装 | **AVAssetWriter（H.264 + MP4）** | ✅ 最优 | 复用系统 muxer；备选手写 MP4 = 重复造轮子 |
| 摄像头 | **AVFoundation** | ✅ 最优 | `AVCaptureDevice` / `AVCaptureSession` |
| 键鼠注入 | **CGEvent** | ✅ 最优 | 平台唯一公开路径 |
| 剪贴板 | **NSPasteboard + changeCount 轮询** | ✅ 平台所迫 | macOS 在别的进程抢剪贴板时**不发任何通知**，轮询是唯一选择 |
| 终端 | **posix_spawn + posix_openpt** | ✅ 优于 forkpty | 避免 fork 一个托管进程；`forkpty` 在此语境下更危险 |
| 密钥 | **Keychain（`SecItem*`）** | ✅ 最优 | 按用户而非作用域，与 DPAPI 的差异已在注册层体现 |
| 开机自启 | **SMAppService** | ✅ 最优 | macOS 13+ 现代 API；备选 LaunchAgents plist 已过时 |
| 录制/编解码复用 | **共享 `Media` 层契约** | ✅ 正确 | macOS 只写适配器，不重写管线 |

### 2.1 唯一的主要技术债：最低系统版本被顶到 macOS 26 ⚠️

- **来源**：随包分发的 `libmsquic` / `openssl@4` Homebrew bottle 的 `minos` 是 **26.0**，dyld 拒绝在更老的系统上加载。
- **影响**：macOS 26（Tahoe，2026-02 GA）是很新的系统，等于**放弃了 macOS 14/15 的用户群**。对一个"跨平台远程桌面"这是实打实的限制。
- **备选（推荐，需决策）**：**从源码构建 MsQuic + OpenSSL 并静态链接**（或走 vcpkg），把下限降到 macOS 14。§3.4 / §8 已记录该路径，且只需替换 `native/macos/fetch-libmsquic.sh`，其余打包流程不变。
- **判断**：这是"已确认接受"的权衡，**不是错误**；但它是**最值得投入的下一步**。

### 2.2 一个值得表扬的细节

原生库编译时 `-mmacosx-version-min=14.0`，**低于** app 的下限 26。这是**正确**的做法 —— 库不该替 app 背一个它本身不施加的限制（注释里写明了这一点）。同类工程里常见反例是把库也钉到 app 的下限，白白缩小复用面。

---

## 3. 对原有体系的影响评估

### 3.1 改动面（`master`..`HEAD`）

总计 **90 文件 / +16360 / −121**，结构如下：

**新增（平台层，不触碰共享层）：**

- `src/Platform/RemoteDesktop.Platform.MacOS`：30 个 `.cs`，~5.4k 行
- `native/macos`：11 个 `.m`/`.h`，4814 行，44 个导出入口
- `tests/RemoteDesktop.Platform.MacOS.UnitTests`：99 条用例
- `build/installer/macos/client`：`make-app.sh` / `Info.plist` / `entitlements.plist` / `make-dev-cert.sh`
- `.github/actions/run-test-suite`：Win/macOS 共用的测试组合动作

**共享层改动（关键 —— 只有 ~10 处小改）：**

| 文件 | 改动 | 性质 |
| --- | --- | --- |
| `Abstractions/Platform/ILoginItem.cs` | 新增端口（+25） | 新增 |
| `Transport/Identity/DeviceIdentity.cs` | PKCS#12 加载加 `Exportable` | 跨平台安全（见下） |
| `Transport/RemoteDesktop.Transport.csproj` | OSX 条件的 libmsquic glob（+11） | 平台条件 |
| `Features.FileTransfer/FileAccessPolicy.cs` | 根与入参同做链接解析 | **修既有 bug** |
| `Features.FileTransfer/FileSources.cs` | 分隔符从被控端路径上读 | **修既有 bug** |
| `Hosts.Client/ClientPlatformRegistration.cs` | 运行时选平台（新增） | 新增 |
| `Hosts.Client/ClientHost.cs`、`Program.cs`、`StartupFailure.cs`、`SelfCheck.cs` | 去 Windows 硬编码 / 新增诊断 | 宿主装配 |
| `Hosts.Client/ViewModels/*`（3 处） | 录制器改由 DI 注入 | 去硬编码 |
| `Directory.Packages.props` +1 / `.gitignore` +5 / `RemoteDesktop.slnx` +2 | 登记 | 登记 |
| 测试：Windows 专有用例改 `SkipUnless` | 让 macOS CI 有意义 | 测试 |
| `.github/workflows/ci.yml` | 新增 `build-macos` job | CI |

### 3.2 契合度判断：**高度契合**

- **依赖规则未被破坏**：`ArchitectureTests` **13/13 通过**（前缀加载自动纳入 macOS 项目）。核心层 `Domain` / `Application` / `Media` / `Protocol` / `Signaling` **零改动**。
- **共享层每一处改动都是"必要的、跨平台正确的"**，没有一处是"为了让 macOS 编译过而给 Windows 让路"：
  - `Exportable`：macOS 上不加载可导出私钥则 MsQuic 握手失败；Windows SChannel 不关心该标志 —— **一个代码路径服务两个平台**，注释写明了原因。
  - FileTransfer 两处：是**修既有 bug**（`Combine` 硬编码 `\`、`/var→/private/var` 误判），对两个平台都是改进。
  - 平台选择 / OS 守卫：`ClientPlatformRegistration` 本就是为多平台预留的既有设计。
- **CI 对称**：`build-windows`（windows-latest）与 `build-macos`（macos-26）共用 `run-test-suite` 组合动作；
  Release 三个平台各有自己的打包 job（`build-linux` / `build-macos` / `build-and-release`），且都在发版时
  自己跑测试与依赖审计。
  （2026-10-16 更新：原文是"`build` … Release 只在 Windows（因 macOS 暂无安装包）"。macOS 后来有了 `.pkg`，
  Release 随之分成三个 job；作业名与共享动作也随之统一。）
- **构建耦合**：`Platform.MacOS.csproj` 通过相对路径引 `native/macos` 并触发 `build-native.sh`。这是"平台项目自建原生产物"的合理做法，`IsOSPlatform('OSX')` 条件已保证非 Mac 上不触发；唯一代价是该项目在 Mac 上构建依赖 clang。**可接受。**

### 3.3 影响结论

macOS 移植**没有污染原体系**：核心零改动、依赖规则完好、共享层只动了必要且正确的地方。这是一次高质量的"适配器式"扩展 —— 正是 Clean Architecture 期望的移植形态。

---

## 4. 清理与整合（本次已执行）

见文末"清理与整合记录"。要点：

- 把 macOS 文档接入 `docs/README.md` 索引；
- 更新根 `README.md`（去掉"第一阶段仅支持 Windows"）、`docs/design/README.md`（补 macOS 平台与单进程模型）、`CHANGELOG.md`（补 macOS 条目）；
- 清理 `artifacts/` 下 811MB 陈旧 bundle 与 `TestResults/` 陈旧 TRX。

---

## 5. 建议（未执行，需决策）

| 优先级 | 建议 | 影响 | 代价 |
| --- | --- | --- | --- |
| **高** | 从源码构建 MsQuic + OpenSSL，把最低系统版本降到 macOS 14 | 扩大用户群 | 中（只需替换 `fetch-libmsquic.sh`） |
| 中 | `ILoginItem` 的 `[SupportedOSPlatform]` 从接口移到实现类 | 一致性 | 极低 |
| 中 | 原生头文件 + `MacNativeInterop` 按域拆分 | 可维护性 | 低（纯机械） |
| 低 | `macos-port-plan.md` 从"计划"转为"移植说明"（P1–P10 已完成） | 文档定位 | 低 |

---

## 6. 清理与整合记录

本次执行（均为 gitignore 覆盖的可再生产物 / 文档，无源码语义改动）：

**文档整合**

- `docs/README.md`：新增"平台移植"章节，接入 `macos-port-plan.md` 与 `macos-port-review.md`。
- `README.md`：去掉"第一阶段仅支持 Windows"的过时表述；文档列表补入两份 macOS 文档；补 macOS `.app` 打包与
  最低系统版本说明。
- `docs/design/README.md`：具体实现层补 `RemoteDesktop.Platform.MacOS`；宿主章节标明 HostService/SessionAgent 为
  Windows 专属，并补 macOS 单进程宿主模型说明。
- `CHANGELOG.md`：新增 `[Unreleased] · macOS 平台移植` 章节（Added / Changed / Notes）。
- 新增本评审文档 `docs/ports/macos-port-review.md`。

**目录清理**（`artifacts/` 与 `TestResults/` 均为 gitignore 覆盖，可再生产物）

- 删除 `artifacts/app/` 下 6 个陈旧 bundle：`audio`、`devcert`、`devcert2`、`macos-selfcheck`、`sc`、`sc2`。
- 删除 `TestResults/*.trx` 陈旧测试报告。
- 保留 `artifacts/app/macos`（当前 bundle）、`artifacts/native`（构建依赖）、`artifacts/probe`（文档引用的 C 探针）。
- 结果：`artifacts/` 由 **811MB → 168MB**。

**未改动**（源码语义）

- §1.4 / §5 的代码级建议（`ILoginItem` 注解、原生互操作按域拆分）**未执行**，留待决策。
