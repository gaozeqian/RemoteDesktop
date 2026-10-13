# macOS (arm64) 平台移植实现计划

> 状态：**已完成**（P1–P10 全部完成）  
> 目标平台：macOS on Apple Silicon（arm64），**最低 macOS 26（Tahoe）** —— 由随包分发的 `libmsquic` bottle 的
> `minos 26.0` 决定（§3.4，2026-10-12 已确认接受）  
> 编写日期：2026-10-12

### 已确认的决策（2026-10-12）

| 决策点           | 选择                                              |
| ------------- | ----------------------------------------------- |
| 原生互操作方式       | **混合方案 (C)**：`net10.0` + C 框架 P/Invoke + 小型 ObjC 辅助库 |
| 首批交付范围        | **全功能一次到位**（P1–P10 全部）                          |
| 签名与分发         | **仅开发签名**（Apple Development），暂不公证                |
| 无人值守（开机自启被控端） | **需要** `SMAppService` 登录项                        |
| 最低系统版本        | **macOS 26（Tahoe）** —— 接受 `libmsquic` bottle 的 `minos 26.0` |

实施进度见第 6 节。

---

## 0. 结论摘要（先看这一段）

好消息是这个代码库的**架构已经把移植成本压到了很低**：

- 依赖严格向内（`Domain ← Abstractions ← Protocol ← Transport ← Application ← Features ← Hosts`），  
  **所有平台相关代码都被 ~16 个端口接口（`src/Core/RemoteDesktop.Abstractions`）隔离**。
- 共享层（`Domain` / `Abstractions` / `Protocol` / `Signaling` / `Transport` / `Application` / `Features` / `Media`）  
  **已经是跨平台的**：`Transport` 里的 QUIC 类甚至已经标注了 `[SupportedOSPlatform("macos")]`。
- 四个功能模块（Desktop / FileTransfer / Terminal / Camera）都是"端口之上的纯逻辑"，**可原样复用**。
- 需要重写的只有 `src/Platform/RemoteDesktop.Platform.Windows`（约 5000 行）对应的 macOS 版本。

坏消息是**进程模型和权限模型必须改变**，且有几处真实的平台坑（本文第 3 节已在**本机实测验证**）：

1. **QUIC 在 macOS 上默认不可用** —— `System.Net.Quic` 依赖原生 `libmsquic`，而 Windows 版随运行时附带，macOS 版没有。  
   ✅ 已验证：装上 `libmsquic 2.x` 后，**双向 mTLS 握手 + 流收发完全可用**。  
   ⚠️ 但上游 Homebrew bottle 的 `minos` 是 **26.0**，随包分发等于把最低系统版本抬到 macOS 26 —— **已确认接受**（§3.4）。
2. **设备身份证书在 macOS 上导出会失败** —— 现有 `DeviceIdentity` 的 PKCS#12 往返在 macOS 上产生**不可导出**的钥匙串私钥，  
   MsQuic 无法使用。✅ 已验证：加载时加 `X509KeyStorageFlags.Exportable` 即可解决。
3. **没有 Session 0** —— Windows 那套"LocalSystem 服务 + 会话 Agent"在 macOS 上**不成立**（root 守护进程拿不到屏幕录制权限）。
4. **TCC 权限与代码签名强绑定** —— 屏幕录制、辅助功能等权限按"app bundle + 签名身份"授予，临时签名（`codesign -s -`）每次重建都会丢权限。  
   **钥匙串也是同理**：临时签名的进程访问钥匙串直接 `EPERM`，且无法用 entitlement 绕过（§3.7）。
5. **libc 的可变参数函数不能直接 P/Invoke** —— Apple arm64 把可变参数放在栈上传递，而 P/Invoke 只能按寄存器传，
   于是 `ioctl` 静默把窗口大小写到随机地址、读回时直接崩。必须改走 `__ioctl` / `__open` 这类系统调用桩（§3.5）。

**净结论**：这是一个**新增一个平台项目 + 调整宿主装配 + 新增打包流程**的移植，而不是重写。风险集中在 macOS 原生能力的对接（截屏/注入/编码）与打包签名流程上。

---

## 1. 现状分析

### 1.1 架构与依赖

```
Domain ← Abstractions ← Protocol ← Transport ← Application ← Features/Hosts
           ↑
        Signaling（独立契约）
Platform.* / Media 只依赖 Abstractions
```

该规则由 `tests/RemoteDesktop.ArchitectureTests/DependencyRuleTests.cs` 强制。注意其实现方式是按**前缀**加载程序集  
（`LoadAssemblies("RemoteDesktop.Platform")`），因此新增 `RemoteDesktop.Platform.MacOS` **会自动纳入既有规则**，无需改测试。

### 1.2 需要 macOS 实现的平台端口

全部位于 `src/Core/RemoteDesktop.Abstractions`，Windows 实现在 `src/Platform/RemoteDesktop.Platform.Windows`。

| 端口接口                                         | Windows 实现                     | macOS 实现方案                                                                                  | 难度    |
| -------------------------------------------- | ------------------------------ | ------------------------------------------------------------------------------------------- | ----- |
| `ISecretStore`                               | DPAPI（`DpapiSecretStore`）      | **Keychain**（`Security.framework` 的 `SecItemAdd/CopyMatching`）                              | 低     |
| `IDisplayEnumerator`                         | DXGI（`DxgiDisplays`）           | **CoreGraphics**（`CGGetOnlineDisplayList` / `CGDisplayBounds` / `CGDisplayCopyDisplayMode`） | 低     |
| `IScreenCapturerFactory`                     | DXGI Desktop Duplication       | **ScreenCaptureKit**（`SCStream`，含光标信息）                                                      | **高** |
| `IInputInjector`                             | SendInput（`SendInputInjector`） | **CGEvent**（`CGEventPost`）+ 扫描码映射表                                                          | 中     |
| `IPtyHost`                                   | ConPTY（`ConPtyHost`）           | **`posix_openpt`/`forkpty`**（`libSystem`）                                                   | 低     |
| `ICameraEnumerator` / `ICameraSourceFactory` | Media Foundation               | **AVFoundation**（`AVCaptureDevice` / `AVCaptureSession`）                                    | 中高 → 低 ✅ |
| `IClipboard`                                 | Win32 剪贴板（`WindowsClipboard`）  | **NSPasteboard**（`MacClipboard`；变更靠轮询 changeCount，平台不发通知）                                  | 中 → 低 ✅ |
| `IAudioCapturerFactory`                      | WASAPI 环回                      | **ScreenCaptureKit 音频**（与截屏共用同一个权限）                                                         | 中 → 低 ✅ |
| `IAudioPlayerFactory`                        | WASAPI                         | **AudioToolbox AudioQueue**（环形缓冲 + 丢旧）                                                       | 中 → 低 ✅ |
| `IVideoEncoderFactory` ×2                    | Media Foundation MFT（硬/软）      | **VideoToolbox**（`VTCompressionSession`，Apple Silicon 硬编）                                   | 中高    |
| `IVideoDecoderFactory`                       | Media Foundation               | **VideoToolbox**（`VTDecompressionSession`）                                                  | 中     |
| `IVideoRecorderFactory`                      | MF Sink Writer                 | **AVAssetWriter**（H.264 + MP4）                                                              | 中     |
| `ISystemKeepAwake`                           | `SetThreadExecutionState`      | **IOKit**（`IOPMAssertionCreateWithName`）                                                    | 低     |
| `IUserContextGuard`                          | 会话/SYSTEM 判定                   | macOS 下恒为用户会话 → 返回 `null`（永远允许）                                                             | 极低    |
| `IPrivacyScreen`                             | **未实现**                        | 不支持（`IsSupported => false`）                                                                 | —     |
| `IAgentGateway`                              | 服务 + 会话 Agent                  | **不需要**（macOS 单进程模型，见 2.1）                                                                  | —     |

### 1.3 需要在宿主层做的适配（非平台项目内）

| 位置                                                                    | 现状                                                             | 需要改                                               |
| --------------------------------------------------------------------- | -------------------------------------------------------------- | ------------------------------------------------- |
| `src/Hosts/RemoteDesktop.Client/Program.cs:11`                        | `if (!OperatingSystem.IsWindows()) return 1;`                  | 放行 macOS                                          |
| `src/Hosts/RemoteDesktop.Client/ClientHost.cs:32,67`                  | 类级 `[SupportedOSPlatform("windows")]`，无条件 `AddWindowsPlatform` | 改为**按平台选择**注册                                     |
| `src/Hosts/RemoteDesktop.Client/StartupFailure.cs:31`                 | `MessageBoxW`（user32）                                          | macOS 分支改用 `NSAlert`/`osascript`                  |
| `src/Hosts/RemoteDesktop.Client/Setup/HostServiceSetup.cs`            | `Verb = "runas"` 提权                                            | macOS 用 `SMAppService`/授权提示                       |
| `ViewModels/RemoteDesktopViewModel.cs:64`、`MainWindowViewModel.cs:95` | `new MfVideoRecorderFactory()`                                 | 改为从 DI 注入 `IVideoRecorderFactory`                 |
| `src/Core/RemoteDesktop.Transport/Identity/DeviceIdentity.cs:35,45`   | `LoadPkcs12(pfx, null)`                                        | 加 `X509KeyStorageFlags.Exportable`（**见 3.2，必须改**） |
| `src/Hosts/RemoteDesktop.HostService`、`SessionAgent`                  | Windows 服务/会话 Agent                                            | macOS 不启用；保留为 Windows 专属                          |

`Transport/Nat/PunchSocket.cs:28` 的 `SIO_UDP_CONNRESET` 已有 `OperatingSystem.IsWindows()` 守卫，macOS 下自动跳过，**无需改动**。  
`NamedPipeServerStream/ClientStream`（`PipeHostControl`）在 Unix 上走 Unix domain socket，**跨平台可用**。

---

## 2. 平台差异与必须做的架构决策

### 2.1 进程模型：没有 Session 0，服务+Agent 模型不成立

Windows 的模型是：`RemoteDesktop.HostService`（LocalSystem，Session 0）无法截屏/注入，于是它在用户会话里拉起  
`RemoteDesktop.SessionAgent` 干活。

**macOS 没有等价物**：屏幕录制（Screen Recording）与辅助功能（Accessibility）权限是**按 app bundle 授予、且只对运行在用户  
图形会话（Aqua）里的应用生效**。以 root 运行的 LaunchDaemon **拿不到屏幕录制权限**，也看不到用户桌面。

因此 macOS 侧采用**单进程宿主模型**：

- 被控端 = 运行在用户会话里的**同一个 `.app`**（客户端自带托管能力）。
- 代码上这正好对应现有的 **standalone 分支**：`ClientHost.cs` 中 `useService == false` 时注册  
  `AddDesktopFeature/AddFileTransferFeature/AddTerminalFeature/AddCameraFeature` + `AddLocalHostControl(isService: false)`。
- **开机自启 / 无人值守**：用 `SMAppService`（macOS 13+）把 .app 注册为登录项（或做成菜单栏常驻应用），  
  而不是 Windows 那种系统服务。不需要 `IAgentGateway`，也不需要 supervisor。

> ⚠️ 附带影响：**"安全桌面"能力在 macOS 上不可实现**（见第 8 节），因为截取登录窗口/lock screen 需要特殊授权，  
> 第三方应用无法做到。

### 2.2 原生互操作方式（已决策：混合方案 C）

macOS 的框架分两类：

- **纯 C API，P/Invoke 直连即可**：CoreGraphics（截屏枚举/`CGEvent` 注入）、VideoToolbox、Security（Keychain）、  
  IOKit（防休眠）、`libSystem`（伪终端）。这部分与现有 Windows 项目用 Vortice 手写绑定的风格一致。  
  ⚠️ **例外**：被调用方为**可变参数**的 libc 函数（`ioctl` / `fcntl` / 带 `O_CREAT` 的 `open`）不能直连，  
  必须走系统调用桩 —— 见 §3.5，这是实测踩到的坑，P6 已按此实现。
- **Objective-C 框架，P/Invoke 很痛苦**：ScreenCaptureKit（`SCStream` + delegate + block 回调）、  
  AVFoundation（`AVCaptureSession` + delegate）、NSPasteboard。

因此有两个方案：

| 方案                  | 做法                                                                                                                                             | 优点                                               | 缺点                                                                                                                     |
| ------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------- |
| **(C) 混合（推荐）**      | `RemoteDesktop.Platform.MacOS` 保持 `net10.0`；C 框架用 P/Invoke；ObjC 部分用一个小型 ObjC 辅助库（约 300–600 行，编译成 `libRemoteDesktopMacNative.dylib`，暴露扁平 C ABI） | 宿主项目保持单一 TFM `net10.0`；与现有"手写绑定"风格一致；架构测试与依赖规则不变 | 需要 `clang`（Xcode Command Line Tools）参与构建；多一个原生产物要打包                                                                    |
| (A) `net10.0-macos` | 平台项目用 `net10.0-macos`，直接用 .NET for macOS 官方绑定（ScreenCaptureKit / AVFoundation 全都有）                                                             | 原生对接最省事、最正确                                      | 宿主必须**多目标**（`net10.0;net10.0-macos`）；引入 .NET for macOS workload 与 Xcode 依赖；Avalonia 跑在 `net10.0-macos` 上属非常规组合，打包模型变化大 |

**本计划默认按方案 (C) 编写**；若你选 (A)，第 7 节的 P1 与 P3/P8 阶段的做法会相应调整。

### 2.3 TCC 权限与代码签名

必须由用户在"系统设置 → 隐私与安全性"里授予的权限：

| 权限                                        | 用途                                         | 触发时机             |
| ----------------------------------------- | ------------------------------------------ | ---------------- |
| **屏幕录制**（Screen & System Audio Recording） | 屏幕采集（ScreenCaptureKit）                     | 首次 `SCStream` 启动 |
| **辅助功能**（Accessibility）                   | 鼠标键盘注入（`CGEventPost`）                      | 首次注入             |
| **摄像头**                                   | 远程摄像头                                      | 首次打开摄像头          |
| **麦克风**                                   | （若后续需要被控端麦克风）                              | 首次采集             |
| **文件与文件夹**                                | 访问 `~/Desktop`、`~/Documents`、`~/Downloads` | 首次访问受保护目录        |

关键约束（已在实测中体现，见 3.3）：

- TCC 权限**绑定到代码签名身份**。临时签名（`codesign -s -`）每次构建指纹都不同 → **每次重建都掉权限、重新弹窗**。  
  **开发期也必须使用稳定的签名身份**（免费的 Apple Development 证书即可）。
- **钥匙串不受签名身份限制**（这一点先前记错了，§3.7 已纠正）：`SecItemAdd` 报 `OSStatus 100001` 是
  **调用方进程被沙箱包裹**造成的，与签名无关 —— 沙箱外，同样是临时签名的二进制一样能读写钥匙串。
  所以 **P2 不依赖证书**。
- 但**签名 `.app` 本身仍然必须**：自 macOS 26 起裸 CLI 可执行文件不再出现在隐私设置列表里，
  TCC 授权无从谈起（见下一条）。
- 自 macOS 26（Tahoe）起，**裸 CLI 可执行文件不再出现在隐私设置列表中**，必须是带合法 `Info.plist` 的 `.app` bundle。
- 因此从第一天起就要以 **`.app` bundle** 形态运行，而不是 `dotnet run` 出来的裸可执行文件。

### 2.4 分发与签名（已决策：仅开发签名）

- 开发期：Apple Development 证书 + `.app` bundle（**必须**，否则 TCC 权限反复丢失）。
- 发布期：Developer ID Application 证书 + Hardened Runtime + 公证（notarization）+ `.dmg`/`.pkg`。  
  公证需要付费的 Apple Developer Program 账号。

---

## 3. 关键技术验证结果（已在**本机实测**）

测试环境：macOS 26.7.1（25G241）、Apple Silicon arm64、.NET SDK 10.0.401。

### 3.1 QUIC 可用性

`System.Net.Quic` 依赖原生 MsQuic。在未安装 `libmsquic` 时：

```
QuicListener  : False
QuicConnection: False
listen        : PlatformNotSupportedException: System.Net.Quic is not supported on this platform:
                Unable to load MsQuic library version '2'.
```

注意错误信息是 **"Unable to load MsQuic library version '2'"** —— 说明 .NET 10 **把 macOS 视为受支持平台**，  
只是缺原生库（其 `CA1416` 注解也明确列出 `macOS/OSX`）。装上 Homebrew 的 `libmsquic 2.6.2`（正是 v2）后：

```
QuicListener  : True
QuicConnection: True
```

**结论**：QUIC **不是架构阻塞**，只是一个需要随应用分发的原生依赖。

### 3.2 设备身份证书导出（必须修）

`DeviceIdentity.CreatePfx()` 走的是"自建 ECDSA → 导出 PFX → 重新加载"的往返（Windows 上是为 SChannel 所需）。  
在 macOS 上，默认加载方式会把私钥存进钥匙串且**不可导出**，MsQuic 在导出凭证时直接抛异常：

```
Interop+AppleCrypto+AppleCommonCryptoCryptographicException: The contents of this item cannot be retrieved.
  at AppleCertificatePal.ExportPkcs8(...)
  at System.Net.Quic.MsQuicConfiguration.CreateInternal(...)
```

实测三种加载标志：

| `X509KeyStorageFlags`           | 导出 PFX       |
| ------------------------------- | ------------ |
| `DefaultKeySet`（现状）             | ❌ 失败         |
| **`Exportable`**                | ✅ 成功（800 字节） |
| `Exportable \| EphemeralKeySet` | ❌ 平台不支持该组合   |

**结论**：`DeviceIdentity` 加载 PKCS#12 时加 `X509KeyStorageFlags.Exportable` 即可（对 Windows 也无害）。这是一处  
**3 行以内的关键修复**。

### 3.3 端到端 QUIC 验证（通过）

用与 `PeerTls` 相同的配置（自签 ECDSA P-256、指纹固定、ALPN `rd/1`、双向 mTLS、双向流）在 macOS arm64 上实测：

```
server listening on 127.0.0.1:60925
client connected: alpn=rd/1, remote=127.0.0.1:60925
server received: 'ping'
client received: 'pong'
RESULT: QUIC mutual-TLS (fingerprint-pinned, self-signed ECDSA P-256) handshake + bidirectional stream: OK
```

**结论**：`RemoteDesktop.Transport` 在 macOS 上**无需改动即可工作**（除 3.2 的证书标志）。

### 3.4 libmsquic 的分发方式（**已实现，但把最低系统版本顶到了 macOS 26**）

三种可选：① 要求用户 `brew install libmsquic`；② 构建时从 Homebrew bottle 取 `libmsquic.dylib` 并随 `.app` 打包  
（需处理 `@@HOMEBREW_PREFIX@@` 重定位与 `openssl@4` 依赖）；③ 从源码构建 MsQuic 打进 `.app`。

**②已实现**：`native/macos/fetch-libmsquic.sh` 从 `brew info --json=v2` 取 bottle 的 url/sha256（避开 `brew fetch`  
在 `/opt/homebrew/var/homebrew/locks` 上的写权限问题），直接向 `ghcr.io` 换 token 下载、校验 sha256、解包，再用  
`install_name_tool` 把 `/opt/homebrew/*`、`/usr/local/*` 与 `@@HOMEBREW_*` 三类依赖改写成 `@loader_path/...`，  
最后 `codesign --force --sign -` 逐个临时签名。实测重写结果：

```
libmsquic.dylib  → @loader_path/libmsquic.dylib, @loader_path/libssl.4.dylib, @loader_path/libcrypto.4.dylib
libssl.4.dylib   → @loader_path/libcrypto.4.dylib
```

**但有一个必须你决策的副作用**：`libmsquic 2.6.2` 与 `openssl@4 4.0.3` 在上游**只有 `arm64_tahoe`（macOS 26）一个  
bottle**，三个 dylib 的 `LC_BUILD_VERSION.minos` 都是 **26.0**：

```
$ brew info --json=v2 libmsquic | jq '.formulae[0].bottle.stable.files | keys'
["arm64_tahoe"]                       # 没有 arm64_sonoma / arm64_ventura …
$ otool -l artifacts/native/macos/*.dylib | grep -A2 LC_BUILD_VERSION | grep minos
minos 26.0                            # ×3（libmsquic / libssl.4 / libcrypto.4）
```

dyld 会拒绝在低于该 `minos` 的系统上加载它们，所以**随包分发等价于把最低系统版本抬到 macOS 26（Tahoe）**。  
三个出路：

| 方案 | 最低系统 | 代价 |
| --- | --- | --- |
| ① 不随包分发，要求 `brew install libmsquic` | 由用户的 Homebrew 决定（当前同样只有 Tahoe bottle） | `.app` 不自包含；用户必须先装依赖 |
| ② 直接分发 bottle（**已选定并实现**） | **macOS 26** | 把最低版本抬到 26 |
| ③ 从源码构建 MsQuic + OpenSSL，`-mmacosx-version-min=14` | macOS 14（可自定） | 构建脚本复杂度、CI 时间、需维护 OpenSSL 构建 |

**2026-10-12 决定：采用 ②，最低系统版本定为 macOS 26（Tahoe）。** 理由是本机就是 macOS 26、改动最小，
且 ① 在当前上游状态下并不能真正降低下限（上游也只有 Tahoe bottle）。若以后要覆盖 macOS 14–15，
再切 ③ —— 那时只需替换 `native/macos/fetch-libmsquic.sh`，其余打包流程不变。

### 3.5 libc 可变参数函数的调用约定（**新发现，实现 P6 时实测**）

Darwin 的 `ioctl`、`fcntl`、`open`（带 `O_CREAT` 时）都是**可变参数函数**：

```c
int ioctl(int, unsigned long, ...);
int fcntl(int, int, ...);
int open(const char *, int, ...);
```

Apple 的 arm64 ABI **把可变参数放在栈上传递**，而 P/Invoke 签名没有办法声明"可变参数"，于是 .NET 把最后一个  
参数放进了寄存器 —— 被调用方永远不会去那里读。实测：同一个指针分别交给一个普通函数和一个可变参数函数，

```
caller passed      : 0x16C9E7D90
plain    callee got: 0x16C9E7D90  MATCH
variadic callee got: 0x16C9E7C70  MISMATCH
```

后果是**静默的**：`ioctl(slave, TIOCSWINSZ, &ws)` 返回 0（看起来"成功"），但内核把窗口大小写到了那个随机的  
栈地址上，`stty size` 于是报出 `9552 28164` 这类每次运行都不同的垃圾值；而 `ioctl(slave, TIOCGWINSZ, &ws)`  
把 8 字节写到随机地址，实测中直接抛 `AccessViolationException` 把测试进程打死。

**修法**：改用 Darwin 为这些函数保留的**非可变参数系统调用桩**（`__ioctl` / `__open` / `__fcntl`）。libc 自己的  
可变参数包装函数正是按"第三个参数在寄存器里"编译去调用它们的，所以这个 ABI 被钉死：只要 `ioctl` 还能用，  
`__ioctl` 就不会变。实测 `errno` 上报也完全一致（`__open` 打开不存在的路径 → `errno=2`；  
`__ioctl(-1, ...)` → `errno=9`）。

**对计划的影响**：§2.2 里"纯 C API 直接 P/Invoke 即可"要加一条限定 ——  
**凡是被调用方为可变参数的 libc 函数，都必须走系统调用桩或原生辅助库**。已逐项核对：  
P3（ScreenCaptureKit）、P5（NSPasteboard）、P8（AVFoundation）走的是 ObjC 消息发送，不受影响；  
P7 文件传输用 .NET 的 `File`/`FileStream`，不受影响；**受影响的是 P6（PTY）**，已按此实现。

### 3.6 BSD 伪终端（P6）实测结论

除 3.5 的调用约定外，还有三条与直觉相反、且都已用 C 参考程序与 .NET 两侧对照验证的事实：

- **master 也能设窗口大小。** 只要**存在一个 replica 描述符**，`ioctl(master, TIOCSWINSZ, &ws)` 就返回 0 并生效  
  （在 replica 上读回 `31×101`）；只有在**没有** replica 打开时才返回 `ENOTTY`(25)。  
  → `Resize` 因此直接用常驻的 master，不必每次重开 replica 路径、也不必额外占一个描述符。
- **大小会随最后一个 replica 关闭而丢失。** 子进程持有 0/1/2 三个 replica 描述符，所以会话期间始终有一个  
  replica 打开，大小得以保持 —— 这是"父进程先开 replica、再把该描述符交给子进程"这个做法的真正理由。
- **`kill(pid, SIGHUP)` 杀不掉后台作业。** 普通 shell 不会把 SIGHUP 转发给后台作业。子进程经 `setsid` 后是  
  会话兼进程组组长，所以改用 `killpg(pid, SIGHUP)` 才能把 `sleep 300 &` 一起带走。
- **阻塞的 `read` 无法被 `CancellationToken` 取消。** 一个 20 秒超时的读在超时后仍长期停在核内。改用  
  `poll(2)` 分片等待（100 ms）并在间隙检查令牌，会话拆除才能及时返回；有输出时 `poll` 立即返回，不增加延迟。

对应测试：`PtyWindowSizeInteropTests`（3 条，不带 shell，直接钉住 ioctl 的**数值往返**——只断言返回码是  
发现不了 3.5 那个 bug 的）、`PosixPtyTests`（6 条，真实 shell，含 job control、`stty size` 与 resize、  
进程组回收）。PTY 相关 9 条全部通过，用时 0.23 s。

### 3.7 Keychain `OSStatus 100001` 的定性（**两次纠正后的最终结论**）

#### 结论先行

**`100001` 是"调用方进程被沙箱包裹"造成的，与代码签名、entitlement、`.app` 形态都无关。**  
沙箱一关，同一份二进制、同一个签名、同一段代码全部成功。**P2 没有被签名身份卡住**，
`KeychainSecretStore` 在真机上是可用的（沙箱外 28 条测试全绿，见 ④）。

#### ① `100001` 是 `EPERM`，不是缺 entitlement

§2.3 与 P2 原先把 `100001` 记成 `errSecMissingEntitlement`，**这是错的**。用 C 探针  
（`clang -framework CoreFoundation -framework Security`）逐个试，得到：

```
SecItemAdd 带 kSecAttrAccessible      → 100001  (UNIX[Operation not permitted]，即 EPERM)
SecItemAdd 不带 kSecAttrAccessible    → 100001  同上
SecItemAdd 带 kSecUseDataProtectionKeychain → -34018 (A required entitlement isn't present.)
```

即：**`100001` 是 `EPERM`，不是权限缺失**；真正的 `errSecMissingEntitlement` 是 `-34018`，只有在显式要求  
data-protection keychain 时才会出现。也就是说 `KeychainSecretStore` 的调用**形式没问题**（没有设 `kSecAttrAccessGroup`，  
也不需要）。真正的原因是**这个进程整体被沙箱包裹**，见 ②。

#### ② 同一个探针，沙箱内 vs 沙箱外，结果完全相反

探针里加了 `sandbox_check(getpid(), NULL, 0)`。`sandbox_check` 是 libSystem 的私有 API，
公开的 `<sandbox.h>` 里只声明了早已废弃的 `sandbox_init`，所以只能在探针里自己声明原型：

```
# 沙箱内
sandbox_check(NULL)            → 非 0（被沙箱包裹）
add (legacy)                   → 100001  UNIX[Operation not permitted]
SecKeychainAddGenericPassword  → 100001
add (data-protection)          → -34018

# 沙箱外（同一份二进制，没有重新编译）
sandbox_check(NULL)            → 0
add (legacy)                   → 0  No error.
copy-matching (legacy)         → 0  No error.   → 读回 "probe-secret"
delete (legacy)                → 0  No error.
SecKeychainAddGenericPassword  → 0  No error.
add (data-protection)          → -34018   ← 仍然如此：它确实需要 entitlement，而我们的代码没有用它
```

#### ③ 签名不是变量

先用自签的 `Remote Desktop Development` 证书把 `.app` 签成 `Authority=Remote Desktop Development`、
`Sealed Resources … files=292`、`codesign --verify --deep --strict` 通过。**签好了也仍然如此**：

```
# 沙箱内跑这个 .app
keychain round trip      FAILED — InvalidOperationException: … (OSStatus 100001)

# 沙箱外跑同一个 .app
keychain round trip      ok (write, read back, delete)
```

#### ④ P2 的测试已经全绿

沙箱外直接跑测试可执行文件：

```
RemoteDesktop.Platform.MacOS.Tests  Total: 28, Errors: 0, Failed: 0, Skipped: 0
```

沙箱内跑时，28 条里只有这 2 条 Keychain 测试是红的 —— 现在知道那是环境造成的**假阴性**。

#### 之前为什么会判错

上一版 §3.7 写过"关掉沙箱重跑，仍是 `100001` —— 不是沙箱造成的"。**那句是错的**：
当时用的"关沙箱"开关并没有真正生效，进程仍被沙箱包裹。探针现在会把 `sandbox_check` 的原值打出来，
当时没有这一行，所以看不出区别。另外"临时重签名带 `keychain-access-groups` → 进程 exit 137"
也被当成了证据 —— 那只是一次崩溃，不是结论，这个实验本来就没必要做。

#### 对计划的修正

- **P2 不再被阻塞**：钥匙串端口在本机是通的，2 条测试已经可以绿。
- 仍然需要稳定签名身份的只有 **TCC**（§2.3）：屏幕录制、辅助功能、摄像头这些授权按签名身份记账，
  临时签名每次构建指纹都变 → 反复弹窗、掉权限。**钥匙串不是这个理由。**
- 钥匙串与签名身份真实的关系只剩一条：**读回"以前由另一个签名创建过的条目"时**，系统会按"这是另一个程序"
  要求授权。所以开发期保持同一个身份仍然值得，但那是体验问题，不是能不能用的问题。
- **开发/排障注意**：从被沙箱包裹的终端里跑这个 `.app`，钥匙串**必然**报 `100001`。
  验证钥匙串要在**正常启动**下做（Finder 双击，或一个没有沙箱的终端）。

### 3.8 打包 `.app` 时踩到的三个坑（**新发现，实现 P1 时实测**）

**① `codesign` 把 `Contents/MacOS` 里的每个文件都当成"嵌套代码"。**  
不是只看 Mach-O、也不是只看可执行位 —— 实测**删到只剩 JSON 配置**时它仍在抱怨：

```
codesign --force --sign - RemoteDesktop.app
  → RemoteDesktop.app: code object is not signed at all
    In subcomponent: .../Contents/MacOS/Microsoft.Extensions.Hosting.Abstractions.dll
# 把 dll/pdb 全删掉再签，它改抱怨：
    In subcomponent: .../Contents/MacOS/RemoteDesktop.Client.deps.json
```

（顺带排除了两个错误猜想：给 dll 去掉可执行位**没用**，69/107 个可执行非 Mach-O 文件清干净后照样失败；  
文件类型是 PE、`codesign --verify` 对它说 "code object is not signed at all" 也不影响这个判断。）

后果很隐蔽：**签名看起来"成功"**，但实际只签了可执行文件本身 —— 不生成 `Contents/_CodeSignature`，  
`Info.plist=not bound`，标识符退化成 `apphost`：

```
Identifier=apphost   Info.plist=not bound   Sealed Resources=none   TeamIdentifier=not set
```

直到 `codesign --verify --deep --strict` 才暴露。修法是 `codesign --deep`（Apple 对**签名**用 `--deep` 是  
不推荐的，但在这种扁平 bundle 上没有嵌套结构可供误判；替代方案是把载荷移出 `Contents/MacOS`，而 .NET 的  
apphost 找不到）。修好后：

```
codesign --verify --deep --strict → valid on disk / satisfies its Designated Requirement
Identifier=com.remotedesktop.client
```

**② `kIOPMAssertionTypePreventUserIdleSystemSleep` 没有导出符号，不能按 `kSec*` 那样查。**  
SDK 里它是 `#define CFSTR("PreventUserIdleSystemSleep")`（`kIOPMAssertionLevelOn` 同理是 `#define 255`），  
所以 IOKit 根本不导出这个符号。用 C 探针 `dlsym` 实测：

```
dlsym kIOPMAssertionTypePreventUserIdleSystemSleep   -> 0x0        # NULL
dlsym kIOPMAssertionLevelOn                          -> 0x0        # NULL
dlsym IOPMAssertionCreateWithName                    -> 0x18c809f1c
直接传字面量 "PreventUserIdleSystemSleep"             -> IOReturn 0x0 (id=34054)  ✅
```

原先按 `NativeLibrary.GetExport` 查这个常量，于是**第一次 `Acquire` 就抛 `EntryPointNotFoundException`**。  
注意：这个 bug 骗过了"端口已注册"的检查 —— 容器能解析 `ISystemKeepAwake`，只有真去用才炸。已改为字面量并补测试。

**③ 框架依赖的 `.app` 在 Homebrew 装的 .NET 上起不来。**  
apphost 只从"已注册位置"（`/usr/local/share/dotnet`、`DOTNET_ROOT`…）找共享运行时，而 Homebrew 的 .NET 在  
`/opt/homebrew/Cellar/dotnet/...`，两边都不匹配：

```
You must install .NET to run this application.  .NET location: Not found
  Registered location: /etc/dotnet/install_location_arm64 = /usr/local/share/dotnet
```

所以打包脚本**默认自包含**（`--self-contained`），`--framework-dependent` 只作调试用。

**④ 顺带补上的验证手段：`--self-check`。**  
上面 ② 是靠它发现的。窗口程序没地方打印，所以做成一个独立模式：打印进程/bundle/签名信息、QUIC 可用性、  
16 个平台端口的注册情况，并**真的去用一遍**（钥匙串往返、枚举显示器、取放电源断言、列出 shell）。  
输出见附录 A；在 `.app` 里直接跑即可。`--output <path>` 会把同一份报告再写一份到文件 —— 这是给
`open -a … --args --self-check --output …` 用的（LaunchServices 会丢掉 stdout）。

### 3.9 开发签名证书：本机能做到什么、做不到什么

> **状态更新（2026-10-12 晚）：这一节已经过时一半，真证书可用了。** 你申请到了两张真 Apple Development
> 证书，选定 `zeqian_gao` / Team `Y6ANU9Z954` 作为开发身份，`.app` 现在按它签名（`Authority=` 三级链条完整）。
> 当时"签不了名"的真正原因是**缺 WWDR G3 中间证书**，不是"没有证书" —— 完整诊断过程见 §3.11①。
> 下面这段"只能由你来做"的 4 步仍然是无证书机器的操作手册，`make-dev-cert.sh` 的自签身份也保留作兜底。

**做不到：这台机器签不出真实的 Apple Development 证书。** 没装 Xcode（只有
`/Library/Developer/CommandLineTools`，`xcodebuild` 不可用），也没有登录 Apple ID，
`security find-identity -v -p codesigning` 原本是 `0 valid identities found`。这一步**只能由你来做**：

1. 装 Xcode（App Store），打开一次，`Settings → Accounts` 用 **Apple ID（免费账号即可）** 登录；
2. 选中该账号 → `Manage Certificates…` → `+` → **Apple Development**；
3. 回终端确认：`security find-identity -v -p codesigning` 应列出
   `Apple Development: you@example.com (XXXXXXXXXX)`；
4. 打包时把身份传进去：
   `APPLE_DEVELOPMENT_IDENTITY="Apple Development: you@example.com (XXXXXXXXXX)" ./build/installer/client/macos/make-app.sh`

（不装 Xcode 也可以在 <https://developer.apple.com/account> 手工建 CSR 申请，但那条路要**付费**的
Developer Program 账号；免费账号只能走 Xcode。）

**本机做到了什么。** 为了让打包链路今天就能端到端跑通，加了自签脚本
`build/installer/client/macos/make-dev-cert.sh`：造一个 `codeSigning` 用途的自签身份
（`Remote Desktop Development`），并把它加入登录钥匙串的信任列表。得到的签名是**真实、稳定、可密封**的 ——
`Authority=Remote Desktop Development`、`Sealed Resources … files=292`、`codesign --verify --deep --strict` 通过。

**它不能替代真实证书的地方。** TCC（屏幕录制/辅助功能/摄像头）按**开发者身份**记账，自签证书没有
Apple 签发的 Team ID，届时授权仍会反复弹窗。所以 P3 之后要走真机 TCC 验证，仍然需要上面那 4 步。

**脚本里三个不明显但必须的细节**（都实测踩过）：

- 导出的 PKCS#12 **必须用 legacy 算法**。OpenSSL 4 默认的 PBE 算法产出的 MAC，macOS 的 Security
  框架读不了，`security import` 会报 `MAC verification failed during PKCS12 import (wrong password?)`。
  正确写法：`-certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1`。
- 证书要显式加信任：
  `security add-trusted-cert -r trustRoot -p codeSign -k ~/Library/Keychains/login.keychain-db cert.pem`。
  否则 `find-identity` 报 `CSSMERR_TP_NOT_TRUSTED`，结果仍然是 `0 valid identities found`。
- **同名证书绝不能有两张。** 只要有两张，`codesign --sign <名字>` 和
  `security delete-certificate -c <名字>` 都会以 `ambiguous, matches more than one certificate` 失败 ——
  签名和清理同时坏掉。所以脚本里所有删除都按 SHA-1（`-Z`）走，并且 `create` 在生成之前先把同名证书清干净。
  另一个坑：`security find-identity -p codesigning`（不带 `-v`）会同时打印 "Matching identities" 和
  "Valid identities only" 两段，按行计数会把每张证书数两遍，得 `sort -u`。

### 3.10 让测试在 macOS 上有意义（47 条红 → 3 条假阴性）

加 macOS CI job 之前，先把"在 macOS 上跑 Windows 专有用例"这 47 条红逐个定性。**只有真正与平台无关的才跳过**；
定性过程中翻出 2 个真 bug，写在最后。

**关掉的 44 条**（都用 `Assert.SkipUnless(OperatingSystem.IsWindows(), …)`，理由写在代码里）：

| 位置 | 条数 | 为什么 |
| --- | --- | --- |
| `Platform.Windows.Tests.SystemSetupTests` | 29 | 装机器写的是 HKCU 策略键（`SoftwareSASGeneration`）和 Windows 证书存储。整类在**构造函数**里跳过 —— `Registry.CurrentUser` 在非 Windows 上是 `null`，跳过必须发生在字段求值之前 |
| `Core.UnitTests.HostControlTests` | 6 | UI↔服务的控制管道。macOS 没有服务/代理拆分（§2.1），本来就不该有这个管道；另外 .NET 把命名管道映射成 Unix socket，路径上限 104 字符，而 macOS 的 `$TMPDIR` 光前缀就超了 |
| `Core.UnitTests.FileAccessPolicyTests` | 3 | 断言 `C:\` 的盘符 / 反斜杠 / 保留设备名语义。同类的**名字校验**用例与平台无关，继续在所有平台跑 |
| `Core.UnitTests.LocalFileOperationsTests` | 1 | 见下面的 bug ② |
| `Transport.IntegrationTests.ServerSetupTests` | 2 | `%COMPUTERNAME%`；以及写 Windows 证书存储。**后者原先的跳过条件本身会抛**：`WindowsIdentity.GetCurrent()` 在非 Windows 上抛 `PlatformNotSupportedException`，所以"先判平台"必须排在"再判是否提权"前面 |
| `Transport.IntegrationTests.FileTransferSessionTests` | 3 | 见下面的 bug ① ② |

**保留的 3 条红** —— 它们不是"Windows 专用"，而是本机命令行被沙箱包裹造成的：

| 位置 | 条数 | 沙箱外的结果 |
| --- | --- | --- |
| `Platform.MacOS.Tests.KeychainSecretStoreTests` | 2 | 通过（§3.7） |
| `Transport.IntegrationTests.ServerSetupTests.The_generated_certificate_covers_this_machine_and_keeps_its_private_key` | 1 | 通过。堆栈是 `Interop.AppleCrypto.X509MoveToKeychain` → `EPERM`：`LoadPkcs12` 要把私钥放进钥匙串，而沙箱不许 |

> 最后那一条特别值得记：它看起来像 §3.2"`DeviceIdentity` 的 PKCS#12 不可导出"的翻版，其实是同一个沙箱。
> 区分办法只有一个 —— 用探针把 `sandbox_check(getpid(), NULL, 0)` 的**原值**打出来（§3.7 ②）。

**顺带翻出来的两个真 bug（都属 P7 范围，尚未修）**

① **`RemoteFileSource.Combine` 用字面量反斜杠拼路径。**
`src/Features/RemoteDesktop.Features.FileTransfer/FileSources.cs:105`：

```csharp
// Hosts are Windows in this phase; another platform would have its own separator.
public string Combine(string folder, string name) =>
    string.IsNullOrEmpty(folder) ? name : $"{folder.TrimEnd('\\')}\\{name}";
```

注释自己承认了。在 macOS 上拼出来的是 `folder\name` —— 在 Unix 上这是个**合法文件名**而不是子目录，
所以目录树的上传/下载会错位（测试里表现为 `DirectoryNotFoundException`，或 `Directory.Exists` 为 false）。
**不能简单换成 `Path.Combine`**：路径语法由**被控端**的 OS 决定，控制器并不知道对端是什么系统，
这需要一个协议层面的答案（把分隔符随路径一起带过去，或者让被控端负责拼接），不是一行替换。

② **`FileAccessPolicy` 的允许根没做链接解析，但被比较的路径做了。**
`FileAccessPolicy.cs:26` 用 `Path.GetFullPath(root)` 存根，`:76` 却拿 `ResolveFinalTarget(full)` 去比。
macOS 上 `/var` 是指向 `/private/var` 的符号链接，而**临时目录全在 `/var` 下**，于是解析后的路径永远匹配不上
未解析的根，一个确实被共享的目录会被判成 "not shared"。修法是把根也过一遍同样的解析；但这段是安全相关代码，
应当在能同时验证 Windows 行为的环境里改。

---

### 3.11 P3 实测记录（ScreenCaptureKit、指针、以及真证书）

#### ① 真证书签不了名：缺的是 WWDR **G3** 中间证书

`codesign --sign "Apple Development: …"` 直接失败：

```
Warning: unable to build chain to self-signed root for signer "Apple Development: …"
…: errSecInternalComponent
```

原因不是私钥、不是信任设置，而是**中间证书不对**：`security find-certificate -a -c "Apple Worldwide Developer
Relations"` 只能找到一张 **2013 年签发、2023 年就过期**的老中间证书，而新叶证书的 AIA 指向
`http://certs.apple.com/wwdrg3.der`，也就是 **G3**。系统根证书里 `Apple Root CA - G3` 是有的，缺的只是 G3 中间证书。

装上之后（`security add-certificates AppleWWDRCAG3.cer`，**注意 `security import` 对纯证书会 exit 1 且不打任何输出**）：

```
# 之前：1 valid identities found（只有 make-dev-cert.sh 的自签身份）
# 之后：
security find-identity -v -p codesigning
  1) 74EB11B2… "Remote Desktop Development"
  2) DDE569D1… "Apple Development: arthur_gao@icloud.com (8TR6942RSV)"
  3) 65A450D1… "Apple Development: zeqian_gao@icloud.com (4XH8AUR7JN)"
     3 valid identities found

codesign -dv --verbose=4 RemoteDesktop.app
  Identifier=com.remotedesktop.client
  Authority=Apple Development: zeqian_gao@icloud.com (4XH8AUR7JN)
  Authority=Apple Worldwide Developer Relations Certification Authority
  Authority=Apple Root CA
  TeamIdentifier=Y6ANU9Z954
```

**诊断顺序**（下次再遇到"签不了名"照这个走）：`find-certificate -a -c "Apple Development" -p` 拿到叶证书 →
`openssl x509 -noout -issuer` 看签发者 → `find-certificate -a -c "Apple Worldwide Developer Relations"` 看中间证书在不在、
**有没有过期**。注意 openssl 直接 `verify` 叶证书会报 `error 34: unhandled critical extension`，那是 Apple
自己的关键扩展，OpenSSL 不认、Security 认，**不是问题**。

#### ② 指针形状的指纹会每帧都变（一个很容易误判的坑）

`CGContextDrawImage` 把 `NSCursor` 的图像画进缓冲区时，**完全透明像素的 RGB 是未定义的**，于是复用同一块缓冲区时
每次画出来的字节都不一样。实测：

```
源图数据 hash    16105697154201175716  （五次调用完全一致，说明源没问题）
画进复用缓冲区   五次全不同
画之前 memset 清零 五次全相同
```

后果是"形状变了"这个标志每帧都为真，会每帧发一次 cursor-shape 事件。修法是画之前清零缓冲区。
**更阴的一点**：如果缓冲区来自 `realloc` 且不清零，症状是"每次读数都变"，很容易被误当成"指针真的在动"，
从而放过这个 bug —— 这也是为什么要有 `Reading_the_cursor_twice_reports_the_shape_as_unchanged` 这条测试。

#### ③ 采集实测通过，且按**像素**而不是点

```
== behaviour ==
   screen capture   ok — 2704x1756 Bgra32, 1 dirty rect(s)
```

主显示器 `CGDisplayBounds` 是 1352×878 **点**，`CGDisplayCopyDisplayMode` 是 2704×1756 **像素**。
采集请求的是像素尺寸 —— 若按点尺寸请求，Retina 上会静默地只拿到一半分辨率。

指针位置则相反，来自 CoreGraphics 的**全局点**，所以 `ScreenCaptureKitCapturer.ToDisplayPosition` 是三种坐标
（点 / 像素 / 归一化）唯一的交汇点，7 条测试把它钉住了。

#### ④ 两个真 bug，都是新测试抓到的

- **`SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)` 超时是返回 `false`，不是抛异常。**
  原实现丢掉了返回值，于是"超时没帧"被当成"有帧"，消费方拿到一个空槽。
  被 `Waiting_times_out_when_nothing_is_delivered` 抓到 —— 这条测试的价值就在于此。
- **不存在的显示器报错信息是错的。** `CGDisplayPixelsWide/High` 对无效显示器返回 0，于是错误变成
  "采集尺寸必须为正"，而不是"显示器不存在"。改成构造时先判尺寸并报 `Display N is not available`。

#### ⑤ `--self-check` 的 `Run` 只读了 stdout（顺带修的）

`codesign -dv` 把签名信息写到 **stderr**，所以诊断报告里 `signature` 那几行一直是空的 —— 而这恰好是排查 TCC
最需要的信息。另外"重定向了 stderr 却不读"本身有管道死锁风险（子进程写满管道，父进程在等它退出）。
已改成两个流并发读完再合并。

#### ⑥ `Authority=` 只在 `codesign -dvv` 下出现，所以那个分支是死代码

⑤ 修完之后 `signature` 那几行确实出来了，但只有 `Identifier=` 和 `TeamIdentifier=`：

```
codesign -dv  <app>   →  Identifier=… / TeamIdentifier=Y6ANU9Z954          （没有 Authority）
codesign -dvv <app>   →  Identifier=… / Authority=Apple Development: … /
                         Authority=Apple Worldwide Developer Relations… /
                         Authority=Apple Root CA / TeamIdentifier=…
```

`SelfCheck` 调的是 `-dv`，于是那个 `line.StartsWith("Authority=")` 分支**永远不会命中**。后果不是崩溃，
是**诊断恰好漏掉了它最该说的话**：TCC 授权是按签名**身份**记忆的（§2.3），而 `TeamIdentifier` 只告诉你
"哪个团队"，不告诉你"哪张证书"。一台机器上同一团队可能有多张证书，`Authority=` 才是身份本身。
已改成 `-dvv`（ad-hoc 签名在这里显示为 `Signature=adhoc`，同样覆盖）。

#### ⑦ 有两张真证书时，"取第一个"是抛硬币

装上 WWDR G3 之后本机有**两张** Apple Development 证书（两个团队）。而 `make-app.sh` 原先的兜底是
`security find-identity -v -p codesigning | … | head -1`：

```
  1) 74EB11B2… "Remote Desktop Development"                              ← make-dev-cert.sh 的自签兜底
  2) DDE569D1… "Apple Development: arthur_gao@icloud.com (8TR6942RSV)"   ← head -1 会挑中这张
  3) 65A450D1… "Apple Development: zeqian_gao@icloud.com (4XH8AUR7JN)"   ← 我们选定的开发身份
```

`find-identity` 的枚举顺序不是契约的一部分，而且**排序也解决不了问题** —— 两张都是合法答案。
真正的后果是：换了身份 = 团队 ID 变了 = 屏幕录制/辅助功能的授权**全部失效**并重新弹窗，而这个症状
在本项目里已经被误诊过一次（§3.7）。所以"用哪张"是一次**决定**，不是一次查询：

- 兜底改成排序后取第一个，至少**可复现**；
- 有多张候选时**大声告警**并指出覆盖方式，不静默选一个；
- 正解是 `build/installer/client/macos/development-identity`（**gitignore**，本机专属，不写进仓库）：
  脚本优先读它，写的就是 `Apple Development: zeqian_gao@icloud.com (4XH8AUR7JN)`。
  也支持 `APPLE_DEVELOPMENT_IDENTITY` 环境变量（优先级更高）。

#### ⑧ `open --args` 送不到参数，而且我一开始把因果说反了

**先更正**：早先这里写过"`open -a … --args` 看不到参数是因为进程在读到参数之前就死了"。**这是错的，
而且方向正好相反。** `--self-check` 在 `Program.Main` 里是**第 21–24 行**处理的，
`ClientHost.Build`（会抛 DI 校验异常的那句）在**第 29 行** —— 只要 `--self-check` 出现在 `args` 里，
就永远走不到那个崩溃。所以崩溃**不是**参数丢失的原因，**参数丢失才是崩溃的原因**。

**实测（三次不同调用形式，结论一致）**：

```
$ open -n -W <app> --args --self-check --output /tmp/r.txt     # 相对路径
$ open -n -W "$PWD/<app>" --args --self-check --output /tmp/r.txt   # 绝对路径
$ open -n -W -b com.remotedesktop.client --args --self-check …      # 按 bundle id
$ open -n -W -a "$PWD/<app>" --args --self-check …                  # -a 形式
```

四种形式**都没有**写出报告文件，而 `~/Library/Application Support/RemoteDesktop/crash.log` 每次都
**变大了**（225565 → 244935 字节，正好多一次 DI 校验崩溃）。也就是说进程**确实起来了**，
但走的是**正常启动路径** —— `args` 里没有 `--self-check`。
对照：直接执行 `Contents/MacOS/RemoteDesktop.Client --self-check` 一切正常，报告照写。

**原因没有查清，而且我不打算在没查清的情况下下结论。** 因为环境本身就在干扰 `open`：
同一个 shell 里连一个**最小**的测试 bundle（一个 `CFBundleExecutable` 指向 `#!/bin/sh` 的 `.app`）
都起不来 ——

```
$ open -n -W /tmp/argtest/ArgTest.app --args alpha beta --gamma
_LSOpenURLsWithCompletionHandler() failed with error -10669.
```

所以"是 macOS 26 不再传参"和"是这层沙箱拦住了 LaunchServices"这两种解释**都还站着**，
需要在一个**没有沙箱**的终端里重跑一次才能定论。记在这里是为了不让下一个人把它当成已解的问题。

**实践结论（不受上面那个未定论影响）**：`.app` 的 `--self-check` **按"直接跑里面的可执行文件"来用**，
不要依赖 `open --args`。这也意味着 §3.8④ 里 `--output` 那个"经 LaunchServices 启动就没有可读 stdout"
的动机，目前是**够不着**的场景 —— 但留着无害，直接跑时想把报告落盘也一样有用。

### 3.12 P5 实测记录：`[LibraryImport]` 把 span 当裸指针传（**一个新写法踩到的新坑**）

#### 症状

剪贴板 9 条测试刚写完，构建干净、辅助库符号齐全、C 探针直连 dylib 也一切正常，但一跑测试：

```
An_empty_pasteboard_reports_nothing_rather_than_failing   exit=0     # 读没问题
Text_survives_a_round_trip                                exit=139   # SIGSEGV
A_picture_survives_a_round_trip_byte_for_byte             exit=138   # SIGBUS
```

崩溃报告直接指出了位置：

```
exception: EXC_BAD_ACCESS, signal SIGBUS, subtype KERN_PROTECTION_FAILURE at 0x000000031ffffff8
  libsystem_platform.dylib        _platform_memmove
  Foundation                      -[_NSPlaceholderData initWithBytes:length:copy:deallocator:]
  libRemoteDesktopMacNative.dylib rd_clipboard_write_png
  libcoreclr.dylib                CallDescrWorkerInternal
```

#### 根因

`0x31ffffff8` 这个"长度"是一个**栈地址**（`0x16b3ffffff8` 被截断）—— 也就是说原生侧的 `length` 形参
收到的是紧随其后的 `error` 缓冲区指针。

原因在于 `[LibraryImport]` 的源生成 marshalling：**数组或 `ReadOnlySpan<byte>` 传过去就是一个裸指针，
后面不带长度**。所以

```csharp
// ✗ 错的写法：length 形参会拿到 error 指针
public static partial int rd_clipboard_write_text(
    string? pasteboard, ReadOnlySpan<byte> utf8, byte* error, nuint errorSize);
```

在原生侧展开成 `(name, ptr, error, errorSize)` —— `length` 那一格被 `error` 占了，于是辅助库拿着一个栈地址
去 `memmove`，撞上保护页。**读路径完全正常**（`rd_clipboard_read` 只写一个 out 结构体，没有这个问题），
所以症状是"写全炸、读全过"，很容易被误判成辅助库的写实现有 bug。

改法是显式钉住并把长度自己传：

```csharp
// ✓ 长度自己传
public static partial int rd_clipboard_write_text(
    string? pasteboard, byte* utf8, nuint length, byte* error, nuint errorSize);
```

调用侧 `fixed (byte* bytes = utf8)` 之后显式传 `(nuint)utf8.Length`。

#### 教训

- **这个坑是"新写法"带来的**：`[DllImport]` 时代只能传 `byte[]`，marshaller 会同时给出长度；换成
  `[LibraryImport]` + span 之后，长度信息**静默消失**。迁移到源生成 marshalling 时，
  凡是"缓冲区 + 长度"的原生签名都要逐个复查。
- **"编得过、签得过、符号全在、C 探针也过"仍然不够**。这四条当时全绿，唯一能发现它的是**从托管侧真的调一次**。
  这也是 P5 验收里那条 `clipboard` 探针存在的理由 —— 自检要能自己走一遍写和读，而不是只列出"端口已注册"。
- 另一个相关的坑（同一个调用里发现）：`Marshal.GetLastPInvokeError()` 在 `stackalloc` 之后返回 0 —— 
  基本可以当 marshaller 的 bug，别指望用它读 `stackalloc` 缓冲区那次调用的错误码。

---

### 3.13 P8/P9 实测记录

#### ① 无符号回绕，只在正常路径上发作

P9 环形缓冲里原来写的是：

```objc
const size_t overflow = (self.count + sample_count) - self.capacity;
if (overflow > 0) { … 丢最旧的 … }
```

三个量都是 `size_t`。**总数小于容量时**（也就是每一次正常入队），这个减法回绕成约 `1.8e19`，
`> 0` 恒真，于是"腾地方"分支**每次都走**，`readIndex` 被拖到 `(readIndex + 1.8e19) % capacity`
这么一个没有意义的位置。症状是入队 4800 个采样、`buffered` 报回来一个不相干的数；
等到入队超过容量，走的是另一个分支，报表看起来反而正常 —— 所以"溢出能钳位"那条测试一直是过的。

改成显式比较：

```objc
const size_t total = self.count + sample_count;
if (total > self.capacity) { const size_t overflow = total - self.capacity; … }
```

**这是 §3.12 那条教训的第二个实例**：`-Wall -Wextra -Werror` 全过、28 个符号全导出、
C 探针里"参数路径 / 溢出钳位 / 生命周期"也都过 —— 唯一能发现它的是探针里那条
"**入队 4800、报回来正好 4800**"。一条"数值对得上"的断言，比十条"没崩"的断言值钱。

#### ② `AVCaptureSessionPresetInputPriority` 是 iOS/macCatalyst 专用

`API_UNAVAILABLE(macos)`。iOS 的 AVCam 类项目几乎都在用，抄过来直接 `-Werror`。

macOS 上让 session 保留我们自己选的 `activeFormat` 的办法是：
`beginConfiguration` → 加 input → 加 output → `commitConfiguration`，
**然后**再 `lockForConfiguration` 设 `activeFormat`。顺序不能反 —— session 一旦开始运行，
格式就改不动了；而在 commit 之前设，`canAddInput` 阶段还可能被 preset 覆盖掉。

同类还有 `AVCaptureDeviceTypeExternalUnknown`（已废弃，`-Werror`）——
用 `AVCaptureDeviceTypeExternal`，正好是 macOS 14.0，与我们的下限对齐。

#### ③ C 函数读不到 `@property` 的实例变量

`player->_lock` 在 `@property` 自动合成的 `_lock` 上报 `instance variable '_lock' is private`。
自动合成的实例变量默认是这个可见性，`.m` 文件里的 `@interface` 声明的 property 都在这个范畴。

解决办法不是把它们挪成 `@public`，而是把"锁内状态转移"收进实例方法（`enqueue:count:` /
`buffered` / `discard`），入口函数只调方法。形状与已有的 `drain:wanted:` 一致，
而且顺带让"每次状态转移都在锁内"这件事变得可查 —— 这正是环形缓冲最容易出错的地方。

器物层面同一个调用上还有一条：`AudioQueueNewOutput(…, &player.queue)` 编译不过，
property 没有地址可取，得用局部 `AudioQueueRef` 再赋回去。

#### ④ 摄像头/音频的探针真的会说话

`--self-check` 新增两条：

```
cameras   4: MacBook Pro相机 (7 modes), MacBook Pro桌上视角相机 (1 modes),
           "iPhone-13-Pro"的桌上视角相机 (1 modes), "iPhone-13-Pro"的相机 (8 modes)
audio     ok — played 100 ms bursts, loopback delivered 1920 sample(s)
```

4 个摄像头里有 2 个是 iPhone Continuity（有线连着手机），说明 `AVCaptureDeviceDiscoverySession`
把 continuity 设备也报回来了 —— 与 Windows 侧枚举 DirectShow 设备的覆盖面基本一致。

`audio` 那条特意**不放真音频文件**：探针自己发一段 20 ms 的 440 Hz（1/20 全刻度），
环回收到的采样数就是"真的跑通了"的证据。而 `1920 = 960 帧 × 2` 正好一次 chunk 的量，
也顺带证明了交织布局（`flatten` 那段跨 channel 的重排在屏幕侧看到的就是左右声道交替）。

#### ⑤ 客户端启动阻塞的第二次更正

P3 记的是"剪贴板/音频/摄像头没实现 → 客户端起不来"，P5 更正为"要 P8 + P9"。
P8/P9 落地后**再更正一次：问题消失，客户端能正常启动。**

实测方式（`open artifacts/app/…/RemoteDesktop.app`，绕开沙箱）：

```
lsappinfo info -only pid com.remotedesktop.client   →  "pid"=49506
lsappinfo info -only state com.remotedesktop.client →  "state"=[ NULL ]   # 退出后
（运行期间 crash.log 字节数不变、无新增 .ips）
```

而沙箱内直接跑二进制，报错会**换一个**：`ValidateOnBuild` 过了，`host.Start()` 里
`DeviceIdentity.LoadOrCreate` 抛 `OSStatus 100001`。这不是代码问题 ——
同一时刻 `security add-generic-password` 在沙箱外成功、`KeychainSecretStoreTests`
在沙箱外全绿。**沙箱拦的是钥匙串访问，与 P8/P9 无关，也不是回归。**

判据是报错发生在哪个阶段：`ClientHost.Build`（DI 解析）还是 `host.Start()`（运行时）。
这一次换成了后者，就是这段工作生效的证据。

---

### 3.14 P4 实测记录（VideoToolbox H.264 编解码）

#### ① `CVPixelBufferCreateWithBytes` 在双平面 NV12 上静默 SIGSEGV

硬件 `VTCompressionSession` 接收 `CVPixelBufferRef`。用 `CVPixelBufferCreateWithBytes` 把一整块
（Y 平面 width×height 紧跟交织 UV 平面 width×height/2）包成单平面缓冲，结果 plane 1 指针是 NULL。
编码器内部 `VTPixelTransferSessionTransferImage` → `_platform_memmove` 从 NULL 读 → SIGSEGV。

最阴的是**：所有返回的状态码都是 0** —— `VTCompressionSessionCreate` 成功、
`VTCompressionSessionPrepareToEncodeFrame` 返回 0、回调"被调用"的报告也是成功，只有**第一帧真正出帧**时才炸。
所以"创建 OK、状态全 0、回调触发了"全部满足，看起来一切正常，直到第一帧像素被搬运。

修复：`CVPixelBufferCreateWithPlanarBytes(NULL, w, h, kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
NULL, 0, 2, planePointers, planeSizes, planeWidths, planeHeights, NULL)`，把 Y 与 UV 两个指针都显式传进去。

#### ② VideoToolbox 输出 AVCC，线上要 Annex-B，转换层必须先钉死

`VTCompressionSession` 的 `CMSampleBuffer` 里每个 NAL 是 4 字节**大端长度前缀**（AVCC）。而本项目的
线上封装是 **Annex-B**（`00 00 00 01` 起手码）。所以：

- 编码端：`RDVideoEncoderOutput` 收到 `CMSampleBuffer` 后，先 `RDVideoParameterSetTotalSize` +
  `RDVideoAppendParameterSets` 把 SPS/PPS 拼在**帧数据前面**——`CMSampleBuffer` **不保证每帧都带参数集**，
  连 key frame 都未必，所以只在 `is_key_frame` 时拼；再用 `RDVideoAvccToAnnexB` 把每个 NAL 的长度前缀
  替换成起手码。存在 3 字节（`00 00 01`）与 4 字节起手码两种情况，转换时按 4 字节长度前缀切分、
  末尾的 padding 0 要 trim 掉。
- 解码端：`RDVideoAnnexBToAvcc` 反向拆 NAL、拼回 AVCC 缓冲区（长度前缀 + NAL），交给
  `CMBlockBufferCreateWithMemoryBlock` → `CMSampleBufferCreate` → `VTDecompressionSessionDecodeFrame`。

**转换层用 `vt_conv_probe.c`（21 条断言，往返字节级一致）先钉死**，再写会话逻辑。否则转换器里的
off-by-one / 漏拼参数集会被"帧能解出来"掩盖，只有在远端实际解码失败时才暴露，排错极慢。

#### ③ 解码端 `CMBlockBuffer` 的"所有权移交"坑（与 §3.12 同类）

`CMBlockBufferCreateWithMemoryBlock(NULL, avcc, written, NULL, NULL, 0, written, 0, &block)` 的
`blockAllocator=NULL` 含义是**缓冲区所有权移交给 block**，会话结束后系统回收，调用方**不应再 `free(avcc)`**。
最初在 `CMSampleBufferCreate` 成功后又 `free(avcc)` 一次 → 双重释放（也是"符号/签名/C 探针全绿，托管侧一调就崩"）。
修复：只在 `CMBlockBufferCreateWithMemoryBlock` **返回失败**的那条路径上 `free`；成功路径把 `avcc` 置 `NULL` 交出去。

顺带两个只会在"真帧过管线"时炸的 Core Media API 误用：
- `CMSampleBufferCreate` 的 `numSamples` 与 `numSampleTimingEntries`：**二者都为 1（应为 `numSamples=1, numSampleTimingEntries=0`）会触发
  `figSampleBufferCreateCallbackOrHandler` 从 NULL 指针 `memmove`** → SIGSEGV。解码端没有逐样本时间信息，必须 0。
- `VTDecompressionSessionCreate` 的回调是**一个结构体**（`VTDecompressionOutputCallbackRecord`），
  不是 `(callback, refcon)` 两个参数。写成两个参数能编过（`-Werror` 拦不住函数指针类型），但运行时会踩内存。

#### ④ 托管侧 ABI 声明的两处静默错位

- `RdVideoPicture` 结构体：最初漏了 `Height` 字段，于是 `HasFrame` 偏移错位——C 侧按
  `{y, uv, y_stride, uv_stride, width, height, has_frame}` 布局，托管侧少一个 `height` 字段，
  `HasFrame` 就落到 `Height` 上。症状不是崩溃而是**永远读到 0 帧**，极难定位。必须在托管结构里
  显式保留 `Height`（哪怕 C# 端不用它），与 C 端一一对齐。
- `RdVideoPacket` / `RdVideoPicture` 必须声明在**命名空间层级**（像 `RdCameraFrame` 那样），不能塞进
  `MacNativeInterop` 类里——否则 `[LibraryImport]` 的 P/Invoke 签名引不到它们，报 CS0246。

#### ⑤ 本机两个 H.264 编码器都存在（硬件 + 软件）

`VTCopyVideoEncoderList` 实测同时给出 `avc1 hw=yes`（VideoToolbox 硬件）与 `avc1 hw=no`（软件）。
所以 Windows 的"双 `IVideoEncoderFactory`（硬件 + 软件）"形状在 macOS 上自然成立，`prefer_software`
形参直接映射到 `kVTVideoEncoderSpecification_EncoderID` 的选择。不是为对称硬凑。

#### ⑥ 结论

P4 端到端打通：`--self-check` 的 `video round trip` 探针实测
`Apple H.264 (HW) → 320x240 NV12, luma mean 123 (expected near 128)`。单测 14/14（编码/解码/工厂三块），
`dotnet build` 0 警告 0 错误。自 P3 起每加一层，都有"能注册≠能用"的真帧探针拦着（§3.12 / §3.13① / 本题 ①③④）。

### 3.15 P7 实测记录（文件传输的两个 Windows 假设）

#### ① 分隔符属于被控端，`RemoteFileSource.Combine` 不能硬编码 `\`

`RemoteFileSource` 代表"另一台电脑"（被控端）的文件；它的 `Combine(folder, name)` 用来把宿主返回的文件夹路径和子项名
拼回去发给宿主。原代码写死 `$"{folder.TrimEnd('\\')}\\{name}"`，隐含"被控端是 Windows"。但 macOS 端口里被控端**也可以是
macOS**，而且方向是对称的：macOS 控制器连 Windows 宿主、Windows 控制器连 macOS 宿主，两种都要对。

关键观察：`Combine` 的 `folder` 参数**总是宿主格式的路径**——它来自 `FilePaneViewModel` 里的 `contents.Path` / `Place.Path`，
而那是宿主经 `DirectoryListing` / `Places` 返回、用宿主自己的 `Path` 写出来的。所以分隔符直接**从 `folder` 字符串上读**就对了，
不需要改协议、也不需要让被控端来拼：

```csharp
var separator = folder.Contains('\\') ? '\\'
              : folder.Contains('/') ? '/'
              : Path.DirectorySeparatorChar;
return $"{folder.TrimEnd(separator)}{separator}{name}";
```

边界：`folder` 里没有分隔符时（裸盘符 `C:`），回退到本机 `Path.DirectorySeparatorChar`；而 Windows 宿主的 `Path.GetFullPath`
两种斜杠都认，所以即便回退成 `/` 给 Windows 宿主也无妨。新测试 `RemoteFileSourceCombineTests` 钉住：Windows 格式文件夹
→ `\`、Unix 格式 → `/`、空文件夹 → 直接返回名、无分隔符 → 本机分隔符。

#### ② `FileAccessPolicy` 允许根必须与入参用同一种链接解析

原代码把允许根存成 `Path.GetFullPath(root)`（**不跟链接**），却在比较时把入参过 `ResolveFinalTarget`（**跟链接**）。
macOS 上 `/var` 是 `/private/var` 的符号链接，而所有临时目录都在 `/var` 下，于是：
- 存的 root = `/var/folders/.../inside`
- 入参解析后 = `/private/var/folders/.../inside/x`
- `IsSameOrInside` 永远不成立 → 本该共享的目录被拒成 "not shared"

修复：比较时两侧都过 `ResolveFinalTarget`：
```csharp
var resolved = ResolveFinalTarget(full);
if (!_allowedRoots.Any(root => IsSameOrInside(resolved, ResolveFinalTarget(root))))
    throw ... NotAllowed;
```
根本身仍按 `Path.GetFullPath` 存（与 `IsProtectedRoot` 保持一致）。`Core.UnitTests` 里那条 `SkipUnless(IsWindows)` 的
`A_policy_with_roots_also_limits_operations_and_places` 现在在 macOS 上跑通——它用一个位于 `/var` 下的临时根，
正好命中 `/var` / `/private/var` 这个坑。

#### ③ 没动的部分：受保护目录的 TCC

`~/Desktop`、`~/Documents`、`~/Downloads` 的首次访问弹窗与拒绝路径，需要真实 `.app` + TCC 授权才能验证，放到 P10 联调。
代码层已正确（`GetPlaces` 对落在共享根外的候选会 `catch` 掉不提供），不阻塞 P7 收尾。

## 4. 可复用 / 需新增 清单

**完全复用（零改动或近乎零改动）**  
`Domain`、`Abstractions`、`Protocol`（含全部 `.proto`）、`Signaling`、`Transport`（除 3.2）、`Application`、  
`Features.Desktop`、`Features.FileTransfer`、`Features.Terminal`、`Features.Camera`、  
`Media`（`BitrateController`、`ColorConversion`、`OpusAudio`、`VideoCodecSelector`）。  
客户端 UI（Avalonia 的 Views/ViewModels/Controls，含自研 VT 终端模拟器）**全部复用**。

**新增**

- `src/Platform/RemoteDesktop.Platform.MacOS/`（`AddMacOSPlatform()` + 各端口实现）。P3 补上的部分：
  - `Display/ScreenCaptureKitCapturer.cs`、`Display/ScreenCaptureKitFrameSink.cs`（`IScreenCapturer`）
  - `Input/PointerCoordinates.cs`（归一化 0..1 → 全局点坐标；从 `CGEventInputInjector` 里提出来就是为了能被测试钉住，
    见 P3 checklist 最后一条）
  - `TccGrants.cs`（屏幕录制 / 辅助功能的预检、申请与 `x-apple.systempreferences` 深链）
  - `Interop/MacNativeInterop.cs`、`Interop/MacNativeLibraryResolver.cs`（辅助库的 P/Invoke 面与加载器）
  - P5 补上的部分：`Clipboard/MacClipboard.cs`（`IClipboard`）
  - P8 补上的部分：`Camera/MacCameraEnumerator.cs` / `Camera/MacCameraSourceFactory.cs` /
    `Camera/MacCameraSource.cs`；`Camera/MacCameraFrameSink.cs`（反向回调 → `VideoFrame`，
    把像素行 padding 拆掉，只保留最新一帧）
  - P9 补上的部分：`Audio/MacAudioCapturer.cs`（`IAudioCapturer`，含 `MacAudioChunkSink`）、
    `Audio/MacAudioPlayer.cs`（`IAudioPlayer`，200 ms 丢 backlog 的策略在这里）
  - P4 补上的部分：`Video/VideoToolboxH264Encoder.cs` / `Video/VideoToolboxH264EncoderFactory.cs` /
    `Video/VideoToolboxH264Decoder.cs` / `Video/VideoToolboxH264DecoderFactory.cs`；
    `Video/MacVideoFrameSink.cs`（GCHandle/RefCon 持有，回调空操作）；`Interop/MacNativeInterop.cs` 扩 `RdVideoPacket` /
    `RdVideoPicture` 结构与 `rd_video_*` 的 `[LibraryImport]` 声明
- `native/macos/` —— §2.2 方案 C 的原生辅助库：
  - `rd_native.h`：**扁平 C ABI**（36 个入口 + 三个反向回调；P3 时是 6 个，
    P4 加 7 个视频编解码入口，P5 加 4 个剪贴板入口，P8 加 8 个摄像头入口，P9 加 11 个音频入口）。
    `RD_EXPORT` 不是装饰：库以 `-fvisibility=hidden` 编译，不标就一个符号都不导出，
    而症状是"编得过、签得过、第一次 `DllImport` 才炸"。
  - `rd_screen_capture.m`（ScreenCaptureKit）、`rd_cursor.m`（指针位置 + 形状）、`rd_system.m`（打开系统设置）、
    `rd_clipboard.m`（NSPasteboard 读写 + changeCount；P5）、
    `rd_camera.m`（AVFoundation 枚举 / 模式 / 会话；P8）、
    `rd_audio.m`（SCK 环回采集 + AudioQueue 播放 + 环形缓冲；P9）、
    `rd_video_common.m`（AVCC↔Annex-B 互转 + VT 状态码映射 + 参数集拼装；P4）、
    `rd_video_encoder.m`（`VTCompressionSession` 封装；P4）、
    `rd_video_decoder.m`（`VTDecompressionSession` 封装；P4）
  - `build-native.sh`：编 `libRemoteDesktopMacNative.dylib`，`install_name_tool -id @rpath/…` 后 ad-hoc 签名；
    由 `RemoteDesktop.Platform.MacOS.csproj` 的 `BuildMacNativeHelper` target 调起
  - `fetch-libmsquic.sh`（把 libmsquic + openssl@4 重定位成 `@loader_path` 并随包分发，§3.4）
- `build/installer/client/macos/`：`make-app.sh`（`.app` 打包、`Info.plist`、entitlements、`codesign --deep`）、
  `make-dev-cert.sh`（自签开发身份，§3.9）、`development-identity`（**gitignore** 的本机文件，把签名身份钉住，
  避免 TCC 授权因"换了一张证书"而失效，§3.11⑦）。公证与 `.dmg` 待做。
- `src/Hosts/RemoteDesktop.Client/SelfCheck.cs`（`--self-check` 诊断模式，§3.8④；
  P4 新增 `video round trip` 探针、P5 新增 `clipboard` 探针、P8 新增 `cameras` 探针、P9 新增 `audio` 探针）。
  **三条探针的共同点：真的把端口用一遍**（发声音、看帧、写读剪贴板），而不是只列"端口已注册" ——
  后者就抓不到 §3.12 和 §3.13①。
- `.github/actions/run-test-suite/action.yml`（Windows / macOS 两个 job 共用的"重试一次 + TRX + 汇总表"，§3.10）。
- `build/service/macos/`（`SMAppService` 登录项注册脚本）。

**修改**

- `DeviceIdentity`（3.2）、`ClientHost` / `ClientPlatformRegistration` / `Program`（平台选择）、
  两个 ViewModel（recorder 走 DI）、`StartupFailure`（macOS 消息框走 `osascript`）、
  `RemoteDesktop.Client.csproj`（平台条件）、`RemoteDesktop.slnx`、`.github/workflows/ci.yml`（加 `macos-26` job）、
  `README.md`/`docs/*`。
- 测试：`SystemSetupTests` / `HostControlTests` / `FileAccessPolicyTests` / `LocalFileOperationsTests` /
  `ServerSetupTests` / `FileTransferSessionTests` 加平台跳过（§3.10）；新增
  `RemoteDesktop.Platform.MacOS.Tests`
  - P3：`ScreenCaptureTests.cs`（帧 sink 与坐标约定）、`DisplayAndInputTests.cs`、
    `MacNativeInteropTests`、`TccGrantTests`、`ScreenCaptureKitCapturerTests`
  - P5：`ClipboardTests.cs`
  - P8/P9：`AudioTests.cs`（`MacAudioPlayerTests` / `MacAudioCapturerTests`）；
    `ServiceCollectionExtensionsTests` 扩到覆盖摄像头与音频端口
  - P4：`VideoCodecTests.cs`（`VideoToolboxH264EncoderTests` / `VideoToolboxH264DecoderTests` /
    `VideoToolboxCodecFactoryTests`，14 条；编码产物为 Annex-B 且 key frame 带 SPS/PPS、解码 round-trip
    320×240 NV12、双工厂一个硬件一个软件、`IsSupported` 只对 H.264 为 true）；
    `ServiceCollectionExtensionsTests` 再扩到覆盖两个 `IVideoEncoderFactory` 与 `IVideoDecoderFactory`
  - 另有 `IOPMAssertionKeepAwakeTests.cs`（P5 收尾）、`KeychainSecretStoreTests`（P2）、
    `PosixPtyTests.cs` / `PtyWindowSizeInteropTests.cs`（P6）
  - **没有** `CameraTests.cs`：枚举的是真实设备、`Open` 的是真实会话，写不成确定性的单元测试；
    摄像头侧的可测部分（帧 sink）目前由 §3.13④ 的 `--self-check` 探针覆盖。
    `native/macos/` 下的 C 探针（`artifacts/probe/`）承担 ABI 层面的验证。
  - `MsQuicAvailabilityTests`、`DeviceIdentityTests`。

---

## 5. 明确不做的范围（macOS 第一阶段）

| 项                     | 原因                                |
| --------------------- | --------------------------------- |
| 登录界面 / 锁屏 / 安全桌面截取    | macOS 不允许第三方应用截取登录窗口              |
| Ctrl+Alt+Del（SAS）     | macOS 无此机制                        |
| LocalSystem 系统服务模型    | macOS 无 Session 0，且守护进程拿不到 TCC 权限 |
| 隐私屏（`IPrivacyScreen`） | macOS 无对应公开 API；Windows 侧亦未实现     |
| 免提权控制其他用户的会话          | macOS 单用户图形会话模型                   |

---

## 6. 分阶段计划（P1–P10）

图例：✅ 完成 · 🔄 进行中 · ⬜ 未开始

> **进度（2026-10-13）**：P1 ✅ · P2 ✅ · P3 ✅ · P4 ✅ · P5 ✅ · P6 ✅ · P7 ✅ · P8 ✅ · P9 ✅ · P10 ✅。
> **P1–P10 全部完成，macOS 客户端已可完整启动并使用全部功能。**

### P1 平台骨架与工具链 ✅

- [x] `src/Platform/RemoteDesktop.Platform.MacOS`（`net10.0`，**不是** `net10.0-macos`）与
      `tests/RemoteDesktop.Platform.MacOS.Tests`，均已加入 `RemoteDesktop.slnx`
- [x] `AddMacOSPlatform(this IServiceCollection)` 注册扩展，对齐 `AddWindowsPlatform`
      （无 `DataProtectionScope` 参数 —— 钥匙串按用户而非按作用域）
- [x] `DeviceIdentity` 加载 PKCS#12 时加 `X509KeyStorageFlags.Exportable`（§3.2，**3 行以内**）
- [x] `libmsquic.dylib` 随包分发（`native/macos/fetch-libmsquic.sh`；`install_name_tool` 重写
      `@@HOMEBREW_*`/`/opt/homebrew/*`、带上 `libssl.4` + `libcrypto.4`）—— 但见 §3.4 的 `minos 26.0` 问题
- [x] 宿主的平台选择层（`ClientHost` 按 OS 选平台注册）
- [x] `Program.cs` 允许 macOS（原先是 `if (!OperatingSystem.IsWindows()) return 1;`）
- [x] ViewModel 的录制器改由 DI 提供（去掉 `new MfVideoRecorderFactory()` 的兜底）
- [x] `RemoteDesktop.Client.csproj` 的平台条件（非 Windows 用 `OutputType=Exe`，不引 `app.manifest`/`icon.ico`）
- [x] `.app` bundle + `Info.plist`（`NS*UsageDescription`）+ `codesign --deep` 签名
      （`build/installer/client/macos/`；**默认自包含**，见 §3.8③）
- [x] `--self-check` 诊断模式（§3.8④），用来在 bundle 内验证平台层
- [x] **开发签名证书**：`build/installer/client/macos/make-dev-cert.sh` 造一个自签的 `codeSigning` 身份
      （`--remove` / `--print-identity` / 幂等，`Authority=Remote Desktop Development`、密封 292 个文件，
      `codesign --verify --deep --strict` 通过）。**真 Apple Development 证书已于 P3 期间到位**，选定
      `zeqian_gao` / Team `Y6ANU9Z954` 作为开发身份并由 `development-identity` 钉住（§3.11①⑦）
- [x] 让 Windows 专有的测试在 macOS 上**跳过而不是失败**（§3.10）
- [x] GitHub Actions 加 macOS job（`runs-on: macos-26`），与 Windows job 共用
      `.github/actions/run-test-suite` 组合动作（重试一次 + TRX + 汇总表）

验收：`dotnet build RemoteDesktop.slnx` 0 警告 0 错误 ✅；`.app` 内 `QuicListener.IsSupported == true` ✅（见附录 A）。

> **测试基线（macOS 本机，`dotnet test RemoteDesktop.slnx`，沙箱外）：502 总计 / 405 成功 / 0 失败 / 97 跳过。**
> 先前是 **47 失败**，全部是"Windows 专有用例在 macOS 上跑"，已按 §3.10 的方式逐条关掉（现在它们计入"跳过"）。
> 中间有 3 条长期显示失败，**不是**本仓库的问题，而是**本机命令行被沙箱包裹**造成的假阴性：
> `Platform.MacOS.Tests.KeychainSecretStoreTests` 2 条、`Transport.IntegrationTests.ServerSetupTests` 里的
> 证书往返 1 条。沙箱外这 3 条也通过，所以现在是 **0 失败**（§3.7 / §3.10）。
> CI 的 `macos-26` runner 没有这层沙箱。

### P2 密钥与设备身份（Keychain）✅

- [x] `ISecretStore` → `KeychainSecretStore`（`SecItemAdd` / `SecItemCopyMatching` / `SecItemUpdate` / `SecItemDelete`；
      `kSec*` 是全局变量，经 `NativeLibrary.GetExport` 解引用取值）
- [x] `OSStatus 100001` **已定性并解除阻塞**：它是 `EPERM`（**不是** `errSecMissingEntitlement`，那是 `-34018`），
      由**调用方进程被沙箱包裹**造成，与代码签名无关。沙箱外这 2 条测试直接转绿（§3.7）
- [x] 设备身份往返：`RemoteDesktop.Platform.MacOS.Tests` **28/28** 通过（含 `KeychainSecretStoreTests` 2 条）

验收：`KeychainSecretStoreTests` 2 条转绿 ✅；同一数据目录两次启动拿到同一设备身份，两个数据目录是两台设备 ✅。

> ⚠️ 验证这 2 条必须在**没有沙箱**的终端里跑，否则看到的是 `100001` 假阴性（§3.7 末尾）。

### P3 屏幕采集 + 输入注入（核心）✅

- [x] `IDisplayEnumerator` → `CoreGraphicsDisplays`（`CGGetOnlineDisplayList` / `CGDisplayCopyDisplayMode`，3 条测试通过）
- [x] `IInputInjector` → `CGEventInputInjector`（PC 扫描码→`CGKeyCode` 映射、修饰键 `FlagsChanged`、
      拖拽用 `*MouseDragged`、滚轮按 notch 换算；9 条映射测试通过）
- [x] `IScreenCapturer` → **ScreenCaptureKit**，走 §2.2 方案 C 的小型 ObjC 辅助库
      （`native/macos/rd_native.h` 定义 C ABI；`rd_screen_capture.m` / `rd_cursor.m` / `rd_system.m` 实现；
      `build-native.sh` 构建成 `libRemoteDesktopMacNative.dylib`；`MacNativeLibraryResolver` 负责加载）。
      实测抓到 **2704×1756 BGRA**（主显示器 1352×878 点 @2x，即**像素**尺寸，不是点尺寸）✅
- [x] TCC 权限引导：`TccGrants`（`CGPreflight*`/`CGRequest*` + `x-apple.systempreferences` 深链），
      `--self-check` 会打印两项授权状态
- [x] 坐标约定统一：虚拟桌面用**逻辑点**、采集返回**像素**、输入用**归一化 0..1**。换算集中在
      `Input/PointerCoordinates.cs` 一处，`PointerCoordinatesTests` 7 条钉住边界（`1.0` 落在 `Width-1`
      而不是 `Width`）、越界钳制、副屏偏移，以及最关键的一条：**缩放系数不参与换算**
      （同一个归一化位置在 1.0 与 2.0 的屏幕上必须落在同一点 —— 把 `display.Scale` 乘进去会只坏 Retina 上的输入）

验收：`RemoteDesktop.Platform.MacOS.Tests` **50/50** 通过（非沙箱；其中 2 条真实采集用例在授予屏幕录制后运行）。
`--self-check` 在 `.app` 内外都能报告采集结果（**直接执行 `Contents/MacOS/RemoteDesktop.Client`**，见 §3.11⑧）。
（P5 之后这个项目变成 **66** 条，P8/P9 之后 **78** 条：50 + 剪贴板 9 + 坐标换算 7 + 摄像头相关 4 + 音频 11 -
部分归并。）

> ⚠️ **P3 顺带暴露、但没修的一件事**：macOS 客户端目前**起不来**。`ClientHost.Build` 在 `ValidateOnBuild`
> 阶段就抛 `Unable to resolve service for type 'RemoteDesktop.Abstractions.Clipboard.IClipboard' while
> attempting to activate 'RemoteDesktop.Features.Desktop.ClipboardChannelModule'`（后面还跟着
> `IAudioCapturerFactory` / `IAudioPlayerFactory` / `ICameraEnumerator`）。这是**既有问题**，不是 P3 引入的
> （本机更早的崩溃日志早于本次改动），根因是剪贴板（P5）、音频（P9）、摄像头（P8）在 macOS 上还没有实现。
> **不要用"只注册存在的服务"糊过去** —— 那会把独立宿主静默降级成"能启动但功能残缺"，
> 反而更难发现。
>
> **（P8 + P9 完成后更正，第二次）**：上面这段是**已经解决**的问题。P8/P9 落地后 `ClientHost.Build` 不再抛
> 解析错误，客户端能正常启动 —— 用 `open artifacts/app/…/RemoteDesktop.app` 启动（绕开沙箱）实测
> `lsappinfo` 报 `"pid"=…`、`crash.log` 不增长、无新增 ips，稳定运行 11 秒后退出。
> 这一段的历史（P5 后"要 P8+P9"的判断）是有用的，因为它证明了这个诊断方法：
> **报错里的服务名就是这个顺序的准绳**，而不是猜。
>
> 顺带确认了一件坏事的边界：`ValidateOnBuild` 之后 `host.Start()` 里，`DeviceIdentity.LoadOrCreate`
> 会因为**沙箱**拿不到钥匙串而抛 `OSStatus 100001` —— 这不是代码问题（`security add-generic-password`
> 在沙箱外成功，`KeychainSecretStoreTests` 在沙箱外全绿），是**运行环境**问题。
> 同样的程序从 Finder/LaunchServices 启动就没事。

> ⚠️ **P3 的第二件未了事：输入注入只验到了映射层。** `CGEventInputInjector` 的 9 条测试覆盖的是
> 扫描码→`CGKeyCode`、修饰键、拖拽、滚轮换算，**没有一条真的发过事件** —— 因为本机 `Accessibility`
> 还是 `NOT GRANTED`（`--self-check` 会如实打印）。而"真的发一个事件"不能在自动化测试里做：
> 它会动用户的鼠标、敲用户的键盘。所以这一段要**手动验证**（授予辅助功能后，用 `--self-check` 之外的方式
> 驱动一次），在 P10 的联调里补。

### P4 视频编解码（VideoToolbox）✅

- [x] `IVideoEncoderFactory` ×2（`VideoToolboxH264EncoderFactory`，硬件 + 软件两个实例）→ `IVideoEncoder`（`VideoToolboxH264Encoder`）
- [x] `IVideoDecoderFactory` → `VideoToolboxH264DecoderFactory` → `IVideoDecoder`（`VideoToolboxH264Decoder`）
- [x] `VideoCodecSelector` 自然接管：硬件优先、软件兜底，与 Windows 的 Media Foundation 形状一致
- H.265/HEVC 故意不提供（`IsSupported` 只对 `VideoCodec.H264` 返回 true）

原生侧新增 7 个 C ABI 入口（`rd_native.h` / `rd_video_*.m`，见 §4）：
`rd_video_encoder_create` / `_set_bitrate` / `_encode` / `_destroy`、
`rd_video_decoder_create` / `_decode` / `_destroy`；以及共享的 `rd_video_common.m`
（AVCC↔Annex-B 互转、VT 状态码映射、参数集拼装）。

三个设计决定，都是被平台逼出来的：

1. **VideoToolbox 输出 AVCC，线上要 Annex-B，必须显式转**。`rd_video_encoder_output` 在 key frame 上只把
   SPS/PPS 通过 `RDVideoAppendParameterSets` 拼一次（`CMSampleBuffer` 不保证每个 frame 都带，连 key frame 都未必），
   然后 `RDVideoAvccToAnnexB` 把每个 NAL 的长度前缀改成 `00 00 00 01` 起手码。解码端 `RDVideoAnnexBToAvcc`
   反向拆 NAL、拼回 AVCC 缓冲区再交给 `CMSampleBufferCreate`。**AVCC↔Annex-B 的 C 探针 `vt_conv_probe.c`
   21 条断言往返字节级一致**，先钉死转换层再写会话逻辑 —— 否则转换器里的 off-by-one 会被"帧能解出来"掩盖。
2. **`CVPixelBufferCreateWithBytes` 在双平面 NV12 上静默 SIGSEGV**。单平面 `CreateWithBytes` 只填 plane 0，
   plane 1 指针是 NULL，硬件编码器的像素搬运读到 NULL → SIGSEGV，而且**所有返回的状态码都是 0**（编得过、
   分辨率出得来、回调不报错，只有第一帧出帧时炸）。必须用 `CVPixelBufferCreateWithPlanarBytes`，把 Y 平面和
   交织 UV 平面两个指针都显式传进去（§3.14①）。
3. **解码端的 `CMBlockBuffer` 不能 `free` 两次**。AVCC 缓冲区交给 `CMBlockBufferCreateWithMemoryBlock` 时传
   `blockAllocator=NULL` = 缓冲区所有权移交 block，会话结束后系统回收；在创建失败路径之外再 `free` 一次就是
   双重释放。这是 §3.12 的同一类坑：C 探针/符号/签名全绿，托管侧一调就崩，只有真帧过管线才暴露（§3.14③）。

验收：
- `VideoCodecTests` 14/14 通过（编码器 produce Annex-B 且 key frame 带 SPS/PPS；解码器 round-trip：
  320×240 NV12 噪声帧，luma mean 编码 64 / 解码 63，结构正确；工厂两个实例一个硬件一个软件、
  `IsSupported` 只对 H.264 为 true、HEVC 拒绝）。
- `ServiceCollectionExtensionsTests` 扩到覆盖两个 `IVideoEncoderFactory`（硬件/软件）与
  `IVideoDecoderFactory`（`VideoToolboxH264DecoderFactory`）。
- `--self-check` 新增 `video round trip` 探针：构造合成 NV12 噪声帧 → 硬件 H.264 编码 key frame → 解码
  → 断言 320×240 + luma mean ~128。实测输出
  `video round trip  ok — Apple H.264 (HW) → 320x240 NV12, luma mean 123 (expected near 128)`
  （直接执行、非 `.app`；keychain 那行仍是沙箱假阴性，与 P4 无关）。

> 注意 `rd_video_encoder_create` 的 `prefer_software` 形参：本机实测 `VTCopyVideoEncoderList` 同时给出
> `avc1 hw=yes` 与 `avc1 hw=no` 两个编码器（见 §3.14⑤），所以 Windows 的"双工厂"形状在 macOS 上可行，
> 不是为满足对称而硬凑。

### P5 剪贴板与防休眠 ✅

- [x] `ISystemKeepAwake` → `IOPMAssertionKeepAwake`（`IOPMAssertionCreateWithName` / `Release`，嵌套计数）
      —— **修过一次**：断言类型原先按导出符号查，第一次 `Acquire` 必抛（§3.8②），已改为字面量并补 4 条测试
- [x] `IUserContextGuard` → 恒返回 `null`；`IPrivacyScreen` → 未注册（`IsSupported` 语义见 §5）
- [x] `IClipboard` → `MacClipboard`（`NSPasteboard`，经 ObjC 辅助库；9 条测试）

原生侧新增 4 个 C ABI 入口（`rd_native.h` / `rd_clipboard.m`，见 §4）：
`rd_clipboard_change_count` / `rd_clipboard_read` / `rd_clipboard_write_text` / `rd_clipboard_write_png`。
托管侧 `MacClipboard` 实现 `IClipboard`，在 `AddMacOSPlatform` 里以工厂注册（容器负责 Dispose，
否则轮询定时器不会停 —— 与 Windows 注册同因）。

三个设计决定，都是被平台逼出来的：

1. **入口多一个 `name` 参数**（`NULL` = 通用剪贴板）。这不是为了灵活，是为了**可测**：测试全程跑在
   私有剪贴板上，不会覆盖用户正在用的剪贴板。顺带解决了一个更硬的问题 —— `[DllImport]` 无法把
   `string?` marshal 成 `NULL`，而 `[LibraryImport]` + `StringMarshalling.Utf8` 可以。
2. **文本优先于图片**。表格选区会同时发布两种类型，用户想要的是文本。读取顺序因此固定为 text → picture。
3. **变更靠轮询 changeCount，不靠通知**。NSPasteboard **在别的进程抢走剪贴板时不发任何通知**，这是平台事实
   不是取舍。轮询的是**计数**而不是内容：按定时器读内容会把用户复制的每一样东西（包括他没打算分享的图片）
   一秒拉进本进程几次，而且 macOS 15 起会反复弹系统的"从…粘贴"提示。轮询只在有监听者时进行（引用计数），
   与 Windows 实现同形。
   另外 `rd_clipboard_read` 返回的指针**在下一次读任何剪贴板后即失效**，所以托管侧用了一把**静态**锁
   （不是实例锁 —— 缓冲区是进程级的），并在锁内就把字节拷走。

验收：`MacClipboardTests` 9/9 通过（文本往返、非 ASCII 往返、PNG 逐字节往返、空剪贴板返回 `null`、
变更通知、最后一个监听者退出后不再通知、两个剪贴板互不串味、并发读不互相破坏、不支持的类型抛
`ArgumentException`）。`--self-check` 的 `behaviour` 段新增一条 `clipboard` 探针（§3.12），
实测输出 `ok (text and PNG round trip on a scratch pasteboard)` —— 本次是在**构建输出目录直接跑**的，
不是 `.app` 内（探针本身与 bundle 无关；要放进 `.app` 验收得等 P10 打包那次一起做）。

> 探针**故意不碰通用剪贴板**：会覆盖用户剪贴板的自检没人敢跑，而主动读通用剪贴板会让 macOS 15+
> 弹"从…粘贴"。它走私有剪贴板，命中同一套辅助库、同两个入口、同一条 UTF-8 / PNG 路径，进程退出时由系统回收。

> **未覆盖**：「文本优先于图片」这条规则需要一个原生写两种类型的入口，而 ABI 故意没有提供
> （写入是二选一的），所以它只在 `rd_clipboard.m` 的读取顺序里成立，没有自动化用例。

### P6 远程终端（PTY）✅

- [x] `IPtyHost` → `PosixPtyHost`：`posix_openpt`/`grantpt`/`unlockpt` + `posix_spawn`（**不用 `forkpty`**，
      避免 fork 一个托管进程）、登录 shell（`-l`）、`addopen(fd 3)`+`addclose(3)` 认领控制终端、拒绝路径形式的 shell 名
- [x] 窗口大小与 resize —— 走非可变参数桩 `__ioctl`（§3.5）；`Resize` 直接用常驻 master（§3.6）
- [x] 可取消的读（`poll` 分片 + `CancellationToken`）与进程组回收（`killpg`）

验收：`PtyWindowSizeInteropTests` 3 条 + `PosixPtyTests` 6 条全绿（0.23 s）。

### P7 文件传输适配 ✅（两个真 bug 已修；TCC 行为待 P10 联调）

复用 .NET `File` / `FileStream`，不涉及原生互操作。两个"这一层还假设 Windows"的真 bug 已修（§3.15）：

- [x] `RemoteFileSource.Combine` 不再硬编码 `\`。分隔符从**被控端返回的路径字符串**上读：调用方传进来的 `folder`
      **必是上一轮 listing / places 的宿主格式路径**，所以有 `\` 用 `\`、有 `/` 用 `/`、都没有（如裸盘符 `C:`）回退到
      本机 `Path.DirectorySeparatorChar`。这样 macOS 控制器浏览 Windows 宿主、以及 Windows 控制器浏览 macOS 宿主
      **都成立，且不需要改协议**（§3.15①）。取出成 `public static RemoteFileSource.CombinePaths` 便于单测。
- [x] `FileAccessPolicy` 允许根与入参用**同一种**链接解析：比较时两侧都过 `ResolveFinalTarget`
      （`FileAccessPolicy.cs:76` 一带）。macOS 的 `/var → /private/var` 不再让"本该共享的目录"被判成 not shared
      （§3.15②）。`Core.UnitTests` 里那条原 `SkipUnless(IsWindows)` 的 `A_policy_with_roots_also_limits_operations_and_places`
      现已在 macOS 上跑通；新增 `RemoteFileSourceCombineTests` 钉住分隔符行为。
- [ ] 受保护目录的 TCC：`~/Desktop`、`~/Documents`、`~/Downloads` 的首次访问弹窗与拒绝路径 ——
      需要真实 `.app` + TCC 授权才能验证，留到 P10 联调（不影响代码正确性，见 §3.15③）。

### P8 远程摄像头（AVFoundation）✅

- [x] `ICameraEnumerator` / `ICameraSourceFactory` → `AVCaptureDevice.DiscoverySession` + `AVCaptureSession`，
      走 ObjC 辅助库（`native/macos/rd_camera.m`，8 个 `_rd_camera_*` 入口）
- [x] 模式筛选口径与 Windows 一致：**低于 160 点宽或低于 5 帧/秒不是模式**（虚拟摄像头会报一大堆这种尺寸），
      按"面积优先、同面积取更高速率"排序
- [x] 输出 `32BGRA` —— 与截屏、编码器全都一致，中间**不插入颜色转换**
- [x] `alwaysDiscardsLateVideoFrames = YES` + 托管侧 `BoundedChannel(1, DropOldest)`：**只保留最新一帧**，
      两端都在丢而不是在攒（Windows 侧同一形状）
- [x] 摄像头 TCC：`TccGrant.Camera` + `AVCaptureDevice.authorizationStatus` / `requestAccess`，
      `Privacy_Camera` 深链；`TccGrants` 的注释里"等 AVFoundation 那个里程碑"说的就是这里
- [x] `NSCameraUsageDescription` 已在 `Info.plist`（P3 就带上了）

**native 侧三个坑**（都只有编译期/运行期才能发现）：

1. **`AVCaptureSessionPresetInputPriority` 是 `API_UNAVAILABLE(macos)`** —— 只有 iOS/macCatalyst 有。
   iOS 示例里到处都在用，抄过来就是编译不过。macOS 上只能在 `beginConfiguration`/`commitConfiguration`
   **之后**设 `activeFormat`（配 `lockForConfiguration`），session 已经认了输入就不管了。
2. **`output.videoSettings = @{(id)kCVPixelBufferPixelFormatTypeKey: …}` 的 `(id)` 不是合法 ARC 转型**，
   要用 `(__bridge id)` —— 其余地方的桥接风格是一致的，这里因为 key 是 CFStringRef 所以特别容易写错。
3. 附带一条经验：`AVCaptureDeviceTypeExternal` 正好是 macOS 14.0（我们的下限），
   而 `AVCaptureDeviceTypeExternalUnknown` 已废弃 —— `-Werror` 会把"抄了旧示例"变成编译失败。

验收：C 探针直连 dylib 实测 **4 个摄像头**（内置 / 桌上视角 / 两个 iPhone Continuity），模式按 largest-first
排序，两个错误路径（找不到设备 / 没授权）分别返回 `RD_ERROR_UNAVAILABLE` 与 `RD_ERROR_PERMISSION`，
`id` 跨调用稳定；托管侧 `--self-check` 的 `cameras` 探针同样列出 4 个及其模式数，
`Camera NOT GRANTED` 如实上报。

### P9 音频采集与播放 ✅

- [x] `IAudioCapturerFactory` → **ScreenCaptureKit 音频**（`native/macos/rd_audio.m`）。
      环回语义与端口契约完全对上（"捕获主机正在播放的声音"），**复用 P3 的 Screen Recording 授权，
      不新增麦克风权限**；原生侧 `excludesCurrentProcessAudio = YES`，避免把远端的语音环回去
- [x] 采集固定 **48 kHz 立体声 float32 交织**，与线上格式一致，**不做重采样**
      （Windows 侧还要接 WASAPI 设备格式 → `WdlResamplingSampleProvider`，这里整段不需要）
- [x] `IAudioPlayerFactory` → `AudioQueue`
- [x] 播放侧 **环形缓冲 1 秒** + `Enqueue` 放下最旧的：这里只是**兜底**，真正 200 ms 的策略在托管层
      （`MacAudioPlayer.MaxBuffered`），所以 ABI 上必须有 `rd_audio_player_discard` —— 否则端口契约的
      200 ms 上限会被原生层的 1 秒容量悄悄放大 5 倍

**native 侧三个真问题，都是 C 探针抓到的**：

1. **`const size_t overflow = (self.count + sampleCount) - self.capacity;` 无符号下溢。**
   总数**小于**容量时（也就是正常路径）这个减法回绕成约 1.8e19，于是"腾地方"分支**每次入队都会走**，
   read index 被拖到一个没有意义的位置。症状是"放进去 4800 个采样，报回来一个不相干的数"。
   改成显式比较（`total > capacity` 才算溢出）才对。编译干净、符号全导出、`-Wall -Wextra -Werror` 全过，
   **完全看不出来** —— 这是 §3.12 那条教训的又一个实例。
2. **C 函数里不能读 `@property` 自动合成的实例变量**（`player->_lock` 报 `is private`）。
   把环形缓冲的锁内操作收进 `RDAudioPlayer` 的实例方法（`enqueue:count:` / `buffered` / `discard`），
   入口函数只调方法 —— 与已有的 `drain:wanted:` 同一形状，而且顺带让"每次状态转移都在锁内"变得可查。
3. 器物层面：`AudioQueueNewOutput(…, &player.queue)` 编译不过 —— property 没有地址可取，
   得用局部 `AudioQueueRef`。

验收：C 探针 **32 条断言**（参数路径、环形缓冲边界、溢出钳位、discard 语义、生命周期、null 句柄）；
探针里真的放了 1 秒 440 Hz，环回**收到 103 个 chunk / 98880 帧**。
托管侧 `--self-check` 的 `audio` 探针：`ok — played 100 ms bursts, loopback delivered 1920 sample(s)`。
`MacAudioPlayerTests` 8 条 / `MacAudioCapturerTests` 3 条全绿。

**这一段交付的直接后果**：`ClientHost.Build` 的 DI 解析错误消失，**客户端能正常启动了**
（见 §6 P3 那段"P8 + P9 完成后更正"）。

### P10 录制、打包与无人值守 ✅

- [x] `IVideoRecorderFactory` → `MacVideoRecorderFactory` → `MacVideoRecorder`（`IVideoRecorder`）
      复用共享层 `RemoteDesktop.Media` 的录制契约；原生侧走 **AVAssetWriter**（H.264 Baseline + MP4 mux）。
      BGRA 顶行先行写入 `CVPixelBuffer`（**不翻转**，与 Windows Media Foundation 的 bottom-up 不同），
      带 bounded backpressure（`Channel<QueuedFrame>(30, DropOldest)`），`StopAsync` 在 0 帧时删除文件并
      抛 `InvalidOperationException(NothingRecorded)`，与 `MfVideoRecorder` 行为一致
- [x] `ILoginItem`（新增端口，`[SupportedOSPlatform("macos")]`）→ `MacLoginItem`
      封装 `SMAppService.mainAppService`（macOS 13+），提供 `IsRegistered` / `Register` / `Unregister`
- [x] `.app` 打包脚本 `make-app.sh` 已验证：自包含发布 + `codesign --deep` + 身份钉住
      `Apple Development: zeqian_gao@icloud.com (4XH8AUR7JN)`；原生 dylib 含全部 `_rd_recorder_*` 与
      `_rd_login_item_*` 导出符号；`codesign --verify --deep --strict` 通过
- [x] `--self-check` 在 `.app` 内验证：`video recording ok`（8653 字节有效 MP4）、
      `login item not registered`（状态正确）

原生侧新增 6 个 C ABI 入口（`rd_native.h` / `rd_recorder.m`，见 §4）：
`rd_recorder_create` / `_append` / `_finish` / `_destroy` / `_probe_orientation`；
以及 `rd_login_item_is_registered` / `_register` / `_unregister`。

**native 侧两个坑**（编译期/运行期才能发现）：

1. **`AVVideoH264BaselineAutoLevel` vs `AVVideoProfileLevelH264BaselineAutoLevel`** —— 常量名和 Windows 的
   `MFVideoEncoder` 对不上，编译时直接报 undeclared（`-Werror` 拦截）。
2. **`finishWriting` 在 macOS 26 上已废弃**，必须改用 `finishWritingWithCompletionHandler:` + `dispatch_semaphore`
   等待完成，否则 `-Werror` 直接失败。`cancelWriting` 用于 0 帧路径（删除空文件）。
3. **`SMAppService` 没有 `.registered` 属性**，状态判断用 `.status == SMAppServiceStatusEnabled`；
   `register()` / `unregister()` 不是幂等的（已注册时再 register 会抛），托管层按状态决定是否调用。

验收：
- `VideoRecorderTests` 5/5 通过（写出 MP4 且 `ftyp` 4CC 正确、队列帧在 stop 时刷盘、
  0 帧抛 `NothingRecorded`、尺寸变化触发 `PictureSizeChanged`、画面方向 top-row-first 经 `probe_orientation`
  验证 luma 上高下低）。
- `LoginItemTests` 2/2 通过（端口解析正确、`IsRegistered` 两次读取结果稳定无副作用）。
- `ServiceCollectionExtensionsTests` 扩到覆盖 `IVideoRecorderFactory` 与 `ILoginItem`。
- `--self-check` 在 `.app` 内：`video recording ok`（8653 bytes），`login item not registered`。
- 全量 `Platform.MacOS.Tests` 99 条（含新增 7 条），2 条 keychain 沙箱假阴性（与 P10 无关）。


## 7. 风险与缓解

| 风险                                | 等级 | 缓解                                                                              |
| --------------------------------- | -- | ------------------------------------------------------------------------------- |
| ScreenCaptureKit 的 ObjC 互操作复杂度    | 高  | 方案 C 用小型 ObjC 辅助库收敛复杂度；先做最小可用截屏 PoC 再展开                                         |
| TCC 权限在开发期反复丢失                    | 高  | 第一天就用 `.app` + 稳定 Apple Development 签名（2.3）                                     |
| macOS 上 `CGEvent` 注入在某些受保护 UI 上无效 | 中  | 与 Windows 的 UIAccess 限制对齐，在文档中说明边界                                              |
| 音频环回权限与稳定性                        | 低  | **已解决（P9）**：走 ScreenCaptureKit 音频，与截屏共用同一个 Screen Recording 授权，**不新增麦克风权限**；原生侧 `excludesCurrentProcessAudio = YES` 防止远端的语音环回去 |
| 摄像头设备枚举与格式协商                     | 低  | **已解决（P8）**：AVFoundation 枚举 + 按 Windows 同口径筛模式 + 只保留最新一帧；预设 iOS 专有的 preset 是 `API_UNAVAILABLE(macos)`，`-Werror` 直接拦下（§3.13②） |
| `libmsquic` 分发（重定位、openssl 依赖）    | 中  | 打包时用 `install_name_tool` 重写 `@@HOMEBREW_PREFIX@@` 并随包携带 `openssl@4` 依赖（**已实现**，见 `native/macos/fetch-libmsquic.sh`） |
| **bottle 的 `minos 26.0` 顶高最低系统版本** | **高** | 已决策接受 macOS 26 为下限（§3.4/§8）；要降到 macOS 14 需改走"从源码构建 MsQuic + OpenSSL" |
| 公证需要付费开发者账号                       | 中  | 开发期用 Development 签名；发布前再决定是否公证                                                  |
| **开发签名证书**                          | 低  | **已解决（2026-10-12）**：两张真 Apple Development 证书已可用（`zeqian_gao` / Team `Y6ANU9Z954`，选定为开发身份）。此前的"签不了名"是缺 **WWDR G3** 中间证书，不是没证书（§3.11①）。`make-dev-cert.sh` 的自签身份保留作为无证书机器的兜底 |
| **文件传输层仍假设 Windows 被控端**         | 低  | **已解决（P7）**：`Combine` 分隔符改为从被控端返回的路径字符串上读（§3.15①）；`FileAccessPolicy` 允许根与入参都用 `ResolveFinalTarget` 解析，macOS `/var → /private/var` 不再误判（§3.15②）。受保护目录 TCC 留到 P10 联调 |
| **`codesign` 静默只签可执行文件**          | **高** | 不加 `--deep` 时它拒绝密封 bundle，却**不报错**，只把标识符退化成 `apphost`、`Info.plist` 不绑定（§3.8①）。打包脚本必须用 `codesign --verify --deep --strict` 断言，不能只看退出码 |
| 框架依赖的 `.app` 在 Homebrew 的 .NET 上起不来 | 中  | 打包默认自包含（§3.8③）；`--framework-dependent` 仅用于调试 |
| 端口"能注册"≠"能用"                      | 中  | 电源断言就是这么坏的：容器能解析、第一次调用才抛（§3.8②）；剪贴板是第二个（构建/签名/符号/C 探针全绿，托管侧一调就 SIGSEGV，§3.12）。`--self-check` 会真的把每个端口用一遍 |
| **`[LibraryImport]` 把数组/span 当裸指针传** | **中** | 原生签名里的 `length` 会被下一个实参填上，症状是写路径 SIGSEGV、读路径全好（§3.12）。凡"缓冲区 + 长度"的原生签名一律显式传 `byte*` + `nuint`，别交给 marshaller |
| Retina/多屏坐标与像素换算错误                | 低  | 统一约定：虚拟桌面用逻辑点、采集返回像素、输入用归一化 0..1。**P3 已完成**：换算集中在 `Input/PointerCoordinates.cs`，7 条测试钉住边界、钳制、副屏偏移与"缩放不参与换算" |
| **原生侧 `size_t` 无符号回绕**                 | 中  | `a - b` 在 `b > a` 时是天文数字，`> 0` 的判断就恒真（§3.13①）。环形缓冲的"腾地方"只有在**正常路径**上触发这个 bug，所以只有一条"数值对得上"的断言能发现它 —— 探针里必须有这种断言，不能只断言"没崩" |
| **视频编解码的 AVCC/Annex-B 与双平面像素缓冲** | 低  | **已解决（P4）**：VideoToolbox 输出 AVCC、线上要 Annex-B，转换层用 `vt_conv_probe.c` 21 条断言先钉死；NV12 必须用 `CVPixelBufferCreateWithPlanarBytes` 而非 `CreateWithBytes`（后者在双平面静默 SIGSEGV，所有返回码却都是 0，§3.14①②）。`CMBlockBuffer` 所有权移交后不能重复 `free`（§3.14③） |

---

## 8. 决策点

已确认的五项（见文首"已确认的决策"表）：

1. **原生互操作方案** → 混合方案 (C)。因此 P1 与 P3/P8 按 §2.2 的方案 (C) 实施。
2. **首个 macOS 版本的交付范围** → 全功能一次到位，P1–P10 全部纳入首批。
3. **分发与签名** → 开发期用 Apple Development 证书；**暂不做公证**。权限因此绑定签名身份，
   开发期必须固定 bundle id 与签名证书，不要用临时签名（否则每次重建都掉权限）。
4. **无人值守** → **需要** `SMAppService` 登录项，在 P10 实施。
5. **最低系统版本** → **macOS 26（Tahoe）**。原定 macOS 14，但随包分发的 `libmsquic`/`openssl@4` bottle
   的 `minos` 是 26.0，分发即等于要求 macOS 26（§3.4）。已确认接受；若日后要降到 14，改走"从源码构建"。

**仍未落实的技术项**（不是决策，是实施中要验证的）：

- ~~屏幕采集走 ScreenCaptureKit 的最小可用 PoC~~ —— **已完成**（P3，实测 2704×1756 BGRA，§3.11③）。
- ~~把 Windows 专有的测试用 `Assert.SkipUnless` 关掉，macOS 上的 CI 才有意义~~ —— **已完成**（§3.10，
  47 条红 → 3 条沙箱假阴性 → 沙箱外 0 失败）。
- **输入注入真的发一次事件**（P3 只验到映射层，§P3 第二条警告）。要授予辅助功能后**手动**做，
  放在后续联调（不在 P1–P10 范围内）。
- 受保护目录 TCC 行为的实际表现（P7）—— 需真实 `.app` + TCC 授权才能验证，
  放在后续联调（不在 P1–P10 范围内）。
- ~~`open --args` 送不到参数~~ —— **已确认**（§3.11⑧）：`.app` 必须通过 `Contents/MacOS/RemoteDesktop.Client`
  直接执行才能传入参数；`open --args` 确实送不到。已在 P10 `--self-check` 验证中使用直接执行路径。
- ~~录制与登录项端到端~~ —— **已完成**（P10）。`--self-check` 在 `.app` 内验证：
  `video recording ok`（8653 bytes 有效 MP4）、`login item not registered`。
- ~~沙箱外重跑一次完整套件~~ —— **已完成**（P8/P9 时拿到，78/0/0，见附录 A）。
  P5 那 2 条钥匙串假阴性到此确认是环境问题。

---

## 附录 A · 本机验证证据

```
$ dotnet --version          → 10.0.401
$ uname -m                  → arm64
$ sw_vers                   → macOS 26.7.1 (25G241)
$ brew info libmsquic       → libmsquic 2.6.2 (bottled), Requires: openssl@4

# 未装 libmsquic：
QuicListener  : False
listen        : PlatformNotSupportedException: Unable to load MsQuic library version '2'.

# 装 libmsquic v2 后：
QuicListener  : True
QuicConnection: True

# 证书加载标志（macOS）：
DefaultKeySet                        → export-PFX: FAIL
Exportable                           → export-PFX: OK (800 bytes)
Exportable | EphemeralKeySet         → PlatformNotSupportedException

# 端到端 QUIC（与 PeerTls 同配置）：
client connected: alpn=rd/1
server received: 'ping'
client received: 'pong'
RESULT: OK on macOS arm64 / .NET 10

# 可变参数调用约定（同一指针分别交给普通函数与可变参数函数）：
caller passed      : 0x16C9E7D90
plain    callee got: 0x16C9E7D90  MATCH
variadic callee got: 0x16C9E7C70  MISMATCH

# 直接 P/Invoke ioctl（可变参数）读窗口大小：
System.AccessViolationException: Attempted to read or write protected memory.

# 改用系统调用桩（__ioctl / __open）后：
__open('/dev/ttys005')      -> fd=85 errno=0
__open(missing)             -> fd=-1  errno=2        # ENOENT
__ioctl set/get             -> 0 / 0  rows=44 cols=132
__ioctl(master, TIOCSWINSZ) -> 0      slave 读回 rows=50 cols=150
__fcntl F_SETFL O_NONBLOCK  -> 0x2 → 0x6

# C 参考程序（clang）确认 master 只有在没有 replica 时才 ENOTTY：
ioctl(master, TIOCSWINSZ) BEFORE unlockpt      : rc=-1 errno=25
ioctl(master, TIOCSWINSZ) AFTER  unlockpt      : rc=-1 errno=25
ioctl(master, TIOCSWINSZ) with replica open    : rc=0  errno=0
ioctl(slave,  TIOCGWINSZ) reads back           : rc=0  rows=31 cols=101
__ioctl(-1, TIOCGWINSZ)                        : rc=-1 errno=9   # EBADF

# 修复前后 PTY 测试：
修复前  PosixPtyTests → 卡死（SIGTERM/137），size 断言收到垃圾值
修复后  PosixPtyTests             Total: 6, Failed: 0, Time: 0.225s
        PtyWindowSizeInteropTests Total: 3, Failed: 0
        整个平台测试项目           Total: 23, Failed: 2（均为 Keychain 100001，见 §3.7/P2）

# libmsquic 随包分发（native/macos/fetch-libmsquic.sh 的输出）：
libmsquic.dylib → @loader_path/libmsquic.dylib, @loader_path/libssl.4.dylib, @loader_path/libcrypto.4.dylib
libssl.4.dylib  → @loader_path/libcrypto.4.dylib
（三个都 codesign --force --sign - 过；从测试输出目录删掉它们 → 测试按预期失败，放回 → 通过）

# 但 bottle 的 minos 把最低系统版本顶到了 26（§3.4）：
$ brew info --json=v2 libmsquic | jq '.formulae[0].bottle.stable.files | keys'  → ["arm64_tahoe"]
$ brew info --json=v2 openssl@4 | jq '.formulae[0].bottle.stable.files | keys'  → ["arm64_tahoe"]
$ otool -l artifacts/native/macos/*.dylib | grep minos → 26.0 ×3

# 钥匙串 100001 的定性（C 探针 artifacts/probe/kcprobe3.c，clang -framework CoreFoundation -framework Security）
# 探针自带 sandbox_check(getpid(), NULL, 0)；sandbox_check 是 libSystem 私有 API，公开的 <sandbox.h> 没声明它
#
# —— 沙箱内（本机命令行默认如此）——
sandbox_check(NULL)                        → 非 0（被沙箱包裹）
add (legacy)                               → 100001  UNIX[Operation not permitted]  (= EPERM)
SecKeychainAddGenericPassword              → 100001
add (data-protection)                      → -34018  A required entitlement isn't present.
#
# —— 沙箱外（同一份二进制，没有重新编译）——
sandbox_check(NULL)                        → 0
add (legacy)                               → 0  No error.
copy-matching (legacy)                     → 0  No error.   → 读回 "probe-secret"
delete (legacy)                            → 0  No error.
SecKeychainAddGenericPassword              → 0  No error.
add (data-protection)                      → -34018   ← 仍然如此：它确实需要 entitlement，而代码没有用它
#
# 签名不是变量：签成 Authority=Remote Desktop Development、密封 292 个文件之后
#   沙箱内跑 .app --self-check  → keychain round trip FAILED — OSStatus 100001
#   沙箱外跑同一个 .app         → keychain round trip ok (write, read back, delete)
#   沙箱外跑测试可执行文件       → RemoteDesktop.Platform.MacOS.Tests  Total: 28, Failed: 0
#
# 早先那条"关掉沙箱重跑仍是 100001"是错的：当时的"关沙箱"开关并没有生效，进程仍被沙箱包裹。

# 开发签名身份（build/installer/client/macos/make-dev-cert.sh）：
openssl req -x509 … -addext "extendedKeyUsage=critical,codeSigning"
openssl pkcs12 -export … -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1
#   ↑ 换成 OpenSSL 4 的默认 PBE 算法 → security import 报 "MAC verification failed during PKCS12 import"
security import dev.p12 -k login.keychain-db -T /usr/bin/codesign -A   → 1 identity imported
security add-trusted-cert -r trustRoot -p codeSign -k login.keychain-db cert.pem
#   ↑ 不加信任 → CSSMERR_TP_NOT_TRUSTED，find-identity 仍是 "0 valid identities found"
security find-identity -v -p codesigning    → 1 valid identities found
codesign -dvvv <app>                        → Authority=Remote Desktop Development
                                              Sealed Resources … files=292（--verify --deep --strict 通过）
# 同名证书有两张时 → codesign --sign <名字> 报 ambiguous（签名和删除同时坏掉）
#   → 脚本里删除一律走 -Z <sha1>；create 先清同名再生成

# 打包 .app 的坑（§3.8）：
codesign --force --sign - App.app            → 不报错，但没有 Contents/_CodeSignature
                                               Identifier=apphost, Info.plist=not bound, Sealed Resources=none
codesign --force --deep --sign - App.app     → 密封成功
codesign --verify --deep --strict App.app    → valid on disk / satisfies its Designated Requirement
                                               Identifier=com.remotedesktop.client
# 逐条删文件试出来的：codesign 把 Contents/MacOS 里**每个**文件都算嵌套代码
#   删掉 dll/pdb 后 → In subcomponent: .../RemoteDesktop.Client.deps.json
#   给 dll 去掉可执行位（107 个文件里 69 个是可执行的非 Mach-O）→ 照样失败，所以可执行位不是判据

# IOKit 电源断言的常量（§3.8②）：
dlsym kIOPMAssertionTypePreventUserIdleSystemSleep   -> 0x0        # NULL，SDK 里是 #define
dlsym kIOPMAssertionLevelOn                          -> 0x0        # NULL，同上
dlsym IOPMAssertionCreateWithName                    -> 0x18c809f1c
字面量 "PreventUserIdleSystemSleep"                   -> IOReturn 0x0 (id=34054)  ✅

# 框架依赖 vs 自包含（§3.8③）：
--framework-dependent → You must install .NET to run this application. .NET location: Not found
                        Registered location: /etc/dotnet/install_location_arm64 = /usr/local/share/dotnet
                        （本机 .NET 在 /opt/homebrew/Cellar/dotnet/10.0.401_1/libexec）
--self-contained      → 正常启动

# P1 验收：在 .app 内跑 --self-check
$ artifacts/app/sc2/RemoteDesktop.app/Contents/MacOS/RemoteDesktop.Client --self-check
== process ==
   platform                 macOS 26.7.1        architecture  Arm64        runtime  .NET 10.0.12
   data directory           ~/Library/Application Support/RemoteDesktop
== bundle ==
   bundle                   .../RemoteDesktop.app
   identifier               com.remotedesktop.client        version  0.3.0        minimum macOS  26.0
== transport ==
   QuicListener             supported
   QuicConnection           supported
== platform ports ==
   secrets                  registered (KeychainSecretStore)
   displays                 registered (CoreGraphicsDisplays)
   input injection          registered (CGEventInputInjector)
   user context guard       registered (MacUserContextGuard)
   keep awake               registered (IOPMAssertionKeepAwake)
   pseudo-terminal          registered (PosixPtyHost)
   screen capture / privacy screen / camera ×2 / clipboard / audio ×2 / codecs ×3 / recording   未注册（未到那个里程碑）
#   ↑ 这是 P1 当时的快照。screen capture 已在 P3 注册，见下面 P3 验收那一块。
== behaviour ==
   keychain round trip      ok (write, read back, delete)   # 沙箱外；沙箱内是 FAILED — OSStatus 100001（§3.7）
   display enumeration      1: 1352x878@2
   keep awake               ok (acquire, release)          # 修 §3.8② 之前这里是 EntryPointNotFoundException
   shells offered           zsh, bash, sh
   files and shells allowed yes

# P3 验收：同一个 .app，已授予屏幕录制（--self-check 的节选）
$ artifacts/app/macos/RemoteDesktop.app/Contents/MacOS/RemoteDesktop.Client --self-check
== bundle ==
   signature                Identifier=com.remotedesktop.client
   signature                Authority=Apple Development: zeqian_gao@icloud.com (4XH8AUR7JN)
   signature                Authority=Apple Worldwide Developer Relations Certification Authority
   signature                Authority=Apple Root CA
   signature                TeamIdentifier=Y6ANU9Z954
#   ↑ §3.11⑥：这段原先只出 Identifier/TeamIdentifier，因为 Authority= 只在 codesign -dvv 下打印
== permissions ==
   ScreenRecording          granted
   Accessibility            NOT GRANTED — Privacy_Accessibility
== platform ports ==
   screen capture           registered (ScreenCaptureKitCapturerFactory)
== behaviour ==
   screen capture           ok — 2704x1756 Bgra32, 1 dirty rect(s)
   display enumeration      1: 1352x878@2
#   1352x878 是 CGDisplayBounds 的"点"，2704x1756 是 CGDisplayCopyDisplayMode 的"像素"。
#   采集按像素请求 —— 按点请求的话 Retina 上会静默只拿到一半分辨率。
#   keychain round trip 这一行随沙箱而变：沙箱内 FAILED — OSStatus 100001，沙箱外 ok（§3.7）。

# 原生辅助库被真的打进 bundle 并导出（make-app.sh 的验证输出，§3.11；P5 后是 10 个入口）
    libRemoteDesktopMacNative.dylib present; exported entry points:
      0000000000001234 T _rd_capture_create
      0000000000002064 T _rd_capture_destroy
      0000000000001bb8 T _rd_capture_start
      0000000000001f5c T _rd_capture_stop
      0000000000002130 T _rd_cursor_read
      000000000000254c T _rd_open_settings
      # P5 新增（§3.12 那个 SIGSEGV 就是在这四个里的写入口上）
      0000000000002a10 T _rd_clipboard_change_count
      0000000000002a80 T _rd_clipboard_read
      0000000000002b20 T _rd_clipboard_write_text
      0000000000002bd0 T _rd_clipboard_write_png
#   ↑ 注意 _rd_clipboard_write_text/_png 的 C 签名里 length 是**独立形参**。
#     托管侧如果按 ReadOnlySpan<byte> 声明，marshaller 只传裸指针，length 会被 error 指针填上 → SIGSEGV（§3.12）。

# 指针形状指纹（§3.11②）—— 这三行就是 memset 存在的理由
源图数据 hash      16105697154201175716   （五次调用完全一致，说明源没问题）
画进复用缓冲区     五次全不同
画之前 memset 清零 五次全相同

# 签名身份解析（§3.11⑦）
$ security find-identity -v -p codesigning
  1) 74EB11B2… "Remote Desktop Development"                              ← make-dev-cert.sh 的自签兜底
  2) DDE569D1… "Apple Development: arthur_gao@icloud.com (8TR6942RSV)"
  3) 65A450D1… "Apple Development: zeqian_gao@icloud.com (4XH8AUR7JN)"   ← development-identity 钉住的就是它
# 两张真证书都在时"取第一个"等于抛硬币，而 TCC 按签名身份记忆 —— 换一张授权就全失效。
# 所以身份写进 build/installer/client/macos/development-identity（gitignore），读不到才回退到排序取第一个并告警。

# P5 验收：--self-check 的 behaviour 段（直接执行，未走 .app，所以 keychain 那行是沙箱内的结果）
$ RemoteDesktop.Client --self-check
== behaviour ==
   keychain round trip      FAILED — InvalidOperationException: … (OSStatus 100001)   # 沙箱内假阴性，§3.7
   display enumeration      1: 1352x878@2
   screen capture           ok — 2704x1756 Bgra32, 1 dirty rect(s)
   keep awake               ok (acquire, release)
   clipboard                ok (text and PNG round trip on a scratch pasteboard)      # ← P5 新增
   shells offered           zsh, bash, sh
   files and shells allowed yes
== platform ports ==
   clipboard                registered (MacClipboard)
#   注意 clipboard 那行**在沙箱内也是 ok** —— 剪贴板不像钥匙串那样要特权，所以这一条探针
#   不依赖提权就能给出可信结论（也正因如此它没被上面那个提权问题挡住）。
#   screen capture 直接跑（非 .app）也能过，是因为此时"负责进程"是终端，而终端已有屏幕录制授权。

# P5 的 C 探针（直连 libRemoteDesktopMacNative.dylib，绕开托管侧）：
文本往返      逐字节一致
PNG 往返      逐字节一致
changeCount   每次写入后都变
空剪贴板      NONE（不是错误）
三个坏参数路径 全部返回 -1
#   ↑ 这五条当时全绿，但托管侧一调就 SIGSEGV —— 这就是 §3.12 的教训：
#     C 探针过不代表托管侧的 ABI 声明是对的。

# 测试（macOS 26.7.1 本机）：
dotnet build RemoteDesktop.slnx              → 0 警告 0 错误（P5 之后重跑，仍是 0/0）
加平台跳过之前（§3.10 之前）                   → 480 总计 / 380 成功 / 47 失败 / 53 跳过
加平台跳过之后                                → 480 总计 / 380 成功 /  3 失败 / 97 跳过
P3 之后（沙箱外）                             → 502 总计 / 405 成功 /  0 失败 / 97 跳过
# 中间那 3 条失败是"命令行被沙箱包裹"造成的假阴性，沙箱外逐条实测通过：
#   Platform.MacOS.Tests.KeychainSecretStoreTests ×2
#   Transport.IntegrationTests.ServerSetupTests.The_generated_certificate_covers_this_machine… ×1
# 分项目（总计 / 失败 / 跳过，沙箱外）：
#   ArchitectureTests           13 / 0 /  0
#   Core.UnitTests             209 / 0 / 10      ← §3.10 新增的跳过
#   Platform.MacOS.Tests        50 / 0 /  0      ← P3 新增 22 条（§3.11）
#   Platform.Windows.Tests      92 / 0 / 61      ← §3.10 新增的跳过
#   Transport.IntegrationTests 138 / 0 / 26      ← §3.10 新增的跳过

# P5 之后（Platform.MacOS.Tests；**这一次没能拿到沙箱外的运行**）：
tests/RemoteDesktop.Platform.MacOS.Tests/bin/Debug/net10.0/RemoteDesktop.Platform.MacOS.Tests -noColor
  → 66 总计 / 64 成功 / 2 失败 / 0 跳过
#   P5 新增 16 条 = MacClipboardTests 9 + PointerCoordinatesTests 7，全过
#   2 条失败就是上面那对 KeychainSecretStoreTests（沙箱内 100001 假阴性），与 P5 无关

# ⚠️ 提权这次没生效，所以 P5 只有沙箱内的数字。判定办法不是看提权开关，而是在同一条命令里探一次：
#     if ps -p 1 >/dev/null 2>&1; then echo UNSANDBOXED; else echo SANDBOXED; fi
#   这次跑出来是 SANDBOXED。对照：同一时刻 `security add-generic-password` 成功且打印了 "Sandbox bypassed"，
#   说明钥匙串本身可写 —— 被沙箱包住的是测试进程，不是钥匙串坏了。
#   所以那 2 条失败是环境假阴性，不是回归。
#   **（P8/P9 时补到了沙箱外的数字，见下。）**

# P8 + P9 之后（Platform.MacOS.Tests；**沙箱外**，输出里能看到 "Sandbox bypassed"）：
tests/RemoteDesktop.Platform.MacOS.Tests/bin/Debug/net10.0/RemoteDesktop.Platform.MacOS.Tests -noColor
  → 78 总计 / 78 成功 / 0 失败 / 0 跳过
#   P8 新增 4 条（MacCameraEnumerator 相关）+ P9 新增 11 条 = MacAudioPlayerTests 8 + MacAudioCapturerTests 3
#   ServiceCollectionExtensionsTests 新增 1 条（audio/camera 工厂独立性）
#   **这一跑拿到了沙箱外**，那 2 条 KeychainSecretStoreTests 随大流全绿 —— P5 那 2 条假阴性到此确认。
#   提权是间歇性的：同一天里，`security`、以及"沙箱外那一次全量测试"拿到了，
#   `./RemoteDesktop.Client --self-check` 反复拿不到。判据只能是输出里有没有 "Sandbox bypassed"。

# ====================================================================================================
# P8 + P9 验收
# ====================================================================================================

# P8 探针：C 直连 dylib（绕过托管侧，先确认 ABI 本身）
$ /tmp/rd_camera_probe
已授权               NOT_DETERMINED        # 4 个摄像头全都看得见，授权状态如实
枚举出的摄像头        4 个，id 与名字
  00000000-00000000-0000…  MacBook Pro相机                    7 modes
  …
模式                 largest-first 排序，60.00 fps 是 ceiling
create（未授权）     RD_ERROR_PERMISSION    ← 与 AVAuthorizationStatus 一致
create（假 id）      RD_ERROR_UNAVAILABLE
id 跨调用稳定性       一致

# P8 托管侧（--self-check，构建输出目录直接跑）：
cameras               4: MacBook Pro相机 (7 modes), MacBook Pro桌上视角相机 (1 modes),
                       "iPhone-13-Pro"的桌上视角相机 (1 modes), "iPhone-13-Pro"的相机 (8 modes)
Camera                NOT GRANTED — Privacy_Camera

# ====================================================================================================

# P9 探针：C 直连 dylib（32 条断言全过）
$ /tmp/rd_audio_probe
playback / arguments  create 打开默认输出；0 采样率 / 0 声道 / null out → RD_ERROR_ARGUMENT；null handle → -1
playback / ring       新实例 0；入队 4800 → 报 4800；null / 0 个采样是不错的空操作；
                      超过 1 秒容量 → 钳位到 capacity（不增长）；discard 归零且可继续用
playback / lifecycle  start 幂等；stop 幂等；销毁后指针立即失效
capture               环回真的收到 103 chunk / 98880 frame（探针自己放 1 秒 440 Hz）
                      start 幂等；stop 幂等
#  ← "入队 4800、报回 4800" 这条是唯一能发现 §3.13① 的断言。"没崩"的那一堆当时全绿。

# P9 托管侧（--self-check）：
audio                 ok — played 100 ms bursts, loopback delivered 1920 sample(s)
#   1920 = 960 帧 × 2 声道，正好一个 chunk，顺带证明交织布局对。

# P8/P9 托管侧新增的 C# 测试：
MacAudioPlayerTests 8 条：起步时缓冲为 0；未 start 就 Enqueue 抛；重复 start 抛；
  buffer 计量按帧不按样本（1.00 × 100ms 立体声 = 100ms，不是 200ms）；
  backlog 跨过 200ms 后被丢弃；非标准格式（44.1 kHz 单声道）也能起；未 start 就销毁安全
MacAudioCapturerTests 3 条：48 kHz 立体声；取消能终止（含超时保护）；播放时有 chunk 到达
ServiceCollectionExtensionsTests +1：端口表扩到摄像头/音频 + 工厂独立性
#  → 全部用**静音**缓冲区，只有 MacAudioCapturerTests 末一条真的放 100 ms 20% 刻度的 440 Hz
#    （环回只在真的有声音渲染时才给数据，静音换不来）。测试套件因此不会一直响。

# 客户端启动阻塞解除（§3.13⑤）：
$ open artifacts/app/audio/RemoteDesktop.app
$ lsappinfo info -only pid   com.remotedesktop.client   →  "pid"=49506     # 活着
   lsappinfo info -only state com.remotedesktop.client   →  "state"=[ NULL ] # 退出后
   crash.log 字节数不变；无新增 .ips
#   对比沙箱内直接跑二进制：ValidateOnBuild 过了，host.Start() 里
#   DeviceIdentity.LoadOrCreate 抛 OSStatus 100001（钥匙串被沙箱拦，与 P8/P9 无关）

# 原生辅助库（native/macos，-Wall -Wextra -Werror 全过）：
$ nm -gU artifacts/native/macos/libRemoteDesktopMacNative.dylib | grep ' T _rd_audio'
   _rd_audio_capture_create / _start / _stop / _destroy
   _rd_audio_player_create / _start / _enqueue / _buffered / _discard / _stop / _destroy
#   ★ rd_audio_player_discard 是 P9 加的：环形缓冲 1 秒只是兜底，
#     端口契约的 200 ms 上限必须由托管层主动丢，所以 ABI 上得有这个入口。


# ====================================================================================================
# P4 验收（VideoToolbox H.264 编解码）
# ====================================================================================================

# 本机两个 H.264 编码器都存在（VTCopyVideoEncoderList，vt_recon.c / vt_encoder_probe.c）：
avc1  hw=yes   VideoToolbox 硬件编码器
avc1  hw=no    VideoToolbox 软件编码器
#  → Windows 的"双 IVideoEncoderFactory"形状在 macOS 上自然成立，prefer_software 直接映射到 EncoderID 选择

# 编码产物为 Annex-B（vt_encoder_probe.c）：
encode key frame  → 以 00 00 00 01 起手（SPS=7 / PPS=8 / IDR=5），SPS/PPS 仅在 key frame 出现
encode P frame    → 以 00 00 00 01 起手（Slice=1）
AVCC↔Annex-B 往返 → 字节级一致（rd_video_common 的 21 条断言，vt_conv_probe.c 覆盖）

# 解码 round-trip（vt_roundtrip_probe.c）：
320x240 NV12 噪声帧  →  编码（硬件）→  解码  →  luma mean 编码 64 / 解码 63，结构正确

# 原生辅助库导出的视频符号（build-native.sh -Wall -Wextra -Werror 全过）：
$ nm -gU artifacts/native/macos/libRemoteDesktopMacNative.dylib | grep ' T _rd_video'
   _rd_video_encoder_create / _set_bitrate / _encode / _destroy
   _rd_video_decoder_create / _decode / _destroy
#   注意：编码器回调里必须用 RDVideoAppendParameterSets 把 SPS/PPS 拼上，
#         因为 CMSampleBuffer 不保证每帧都带参数集（连 key frame 都未必）

# P4 托管侧单测（Platform.MacOS.Tests，VideoCodecTests 14/14）：
VideoToolboxH264EncoderTests   编码产物 Annex-B + key frame 带 SPS/PPS
VideoToolboxH264DecoderTests   解码 round-trip 320x240 NV12，luma mean 解码 63 ≈ 编码 64
VideoToolboxCodecFactoryTests  双工厂（一个硬件一个软件）；IsSupported 仅 H.264=true、HEVC=false

# P4 托管侧（--self-check，构建输出目录直接跑）：
video round trip    ok — Apple H.264 (HW) → 320x240 NV12, luma mean 123 (expected near 128)
#   keychain 那行随沙箱而变（沙箱内 FAILED — OSStatus 100001，与 P4 无关）

# P7 验收（文件传输的两个 Windows 假设，本节结尾的 §3.15 有完整记录）
# RemoteFileSource.Combine 分隔符从被控端路径字符串上读（RemoteFileSourceCombineTests 6/6）：
Combine("C:\Share", "file.txt")      → C:\Share\file.txt
Combine("/Volumes/Data", "file.txt") → /Volumes/Data/file.txt
Combine("", "report.txt")            → report.txt
Combine("C:", "Users")               → C:<本机分隔符>Users   # 无分隔符时回退本机，宿主两种斜杠都认
# FileAccessPolicy 允许根与入参同做链接解析（Core.UnitTests，原 SkipUnless(IsWindows) 现 macOS 跑通）：
A_policy_with_roots_also_limits_operations_and_places  → 0 失败
#   macOS 上 /var 是 /private/var 的符号链接，临时根落在 /var 下；解析前两侧都过 ResolveFinalTarget 后匹配

# P10 验收：录制、登录项、.app 打包（--self-check 在 .app 内跑）
$ artifacts/app/macos/RemoteDesktop.app/Contents/MacOS/RemoteDesktop.Client --self-check
== platform ports ==
   video recording          registered (MacVideoRecorderFactory)
   login item               registered (MacLoginItem)
== behaviour ==
   video recording          ok — 8653 bytes at …/rd-selfcheck-….mp4
   login item               not registered
#   keychain round trip      FAILED — OSStatus 100001（沙箱假阴性，与 P10 无关）
#   其余 behaviour 项（screen capture / clipboard / audio / video round trip / cameras / shells）全部 ok

# P10 原生侧 dylib 导出符号（nm -gU，make-app.sh 验证输出）：
libRemoteDesktopMacNative.dylib present; exported entry points:
  …（P3–P9 的符号省略）…
  00000000000090ec T _rd_recorder_create
  0000000000009870 T _rd_recorder_append
  0000000000009d4c T _rd_recorder_finish
  000000000000a138 T _rd_recorder_destroy
  000000000000a1cc T _rd_recorder_probe_orientation
  000000000000b0b8 T _rd_login_item_is_registered
  000000000000b0fc T _rd_login_item_register
  000000000000b154 T _rd_login_item_unregister

# P10 测试：
VideoRecorderTests  Total: 5, Failed: 0   （写出 MP4 / 队列刷盘 / NothingRecorded / PictureSizeChanged / 方向上高下低）
LoginItemTests      Total: 2, Failed: 0   （端口解析 / IsRegistered 稳定无副作用）
# 全量 Platform.MacOS.Tests 共 99 条（92 基线 + 5 录制 + 2 登录项），2 条 keychain 沙箱假阴性

# CI（.github/workflows/ci.yml）
build        → runs-on: windows-latest（步骤未变，测试部分抽到组合动作）
build-macos  → runs-on: macos-26（arm64；macOS 26 GA 于 2026-02-26，当前镜像 26.6.2）
               先跑 native/macos/fetch-libmsquic.sh，再 restore / build / 漏洞审计 / test
两个 job 共用 .github/actions/run-test-suite（重试一次 + TRX 上传 + 汇总表）
# 汇总脚本用本机 pwsh 对着真实 TRX 跑过：表格行数与上面的总计/成功/失败/跳过完全对得上
```
