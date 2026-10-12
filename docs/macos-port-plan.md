# macOS (arm64) 平台移植实现计划

> 状态：**已确认，实施中**（P1 / P2 / P6 已完成，P3 / P5 进行中）  
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
| `ICameraEnumerator` / `ICameraSourceFactory` | Media Foundation               | **AVFoundation**（`AVCaptureDevice` / `AVCaptureSession`）                                    | 中高    |
| `IClipboard`                                 | Win32 剪贴板（`WindowsClipboard`）  | **NSPasteboard**                                                                            | 中     |
| `IAudioCapturerFactory`                      | WASAPI 环回                      | **ScreenCaptureKit 音频**（与截屏共用同一个权限）                                                         | 中     |
| `IAudioPlayerFactory`                        | WASAPI                         | **AVAudioEngine** / AudioQueue                                                              | 中     |
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

---

## 4. 可复用 / 需新增 清单

**完全复用（零改动或近乎零改动）**  
`Domain`、`Abstractions`、`Protocol`（含全部 `.proto`）、`Signaling`、`Transport`（除 3.2）、`Application`、  
`Features.Desktop`、`Features.FileTransfer`、`Features.Terminal`、`Features.Camera`、  
`Media`（`BitrateController`、`ColorConversion`、`OpusAudio`、`VideoCodecSelector`）。  
客户端 UI（Avalonia 的 Views/ViewModels/Controls，含自研 VT 终端模拟器）**全部复用**。

**新增**

- `src/Platform/RemoteDesktop.Platform.MacOS/`（`AddMacOSPlatform()` + 各端口实现）。P3 补上的部分：
  - `Display/ScreenCaptureKitCapturer.cs`、`Display/ScreenCaptureKitFrameSink.cs`（`IScreenCapturer`）
  - `TccGrants.cs`（屏幕录制 / 辅助功能的预检、申请与 `x-apple.systempreferences` 深链）
  - `Interop/MacNativeInterop.cs`、`Interop/MacNativeLibraryResolver.cs`（辅助库的 P/Invoke 面与加载器）
- `native/macos/` —— §2.2 方案 C 的原生辅助库：
  - `rd_native.h`：**扁平 C ABI**（6 个入口 + 一个反向帧回调）。`RD_EXPORT` 不是装饰：
    库以 `-fvisibility=hidden` 编译，不标就一个符号都不导出，而症状是"编得过、签得过、第一次 `DllImport` 才炸"。
  - `rd_screen_capture.m`（ScreenCaptureKit）、`rd_cursor.m`（指针位置 + 形状）、`rd_system.m`（打开系统设置）
  - `build-native.sh`：编 `libRemoteDesktopMacNative.dylib`，`install_name_tool -id @rpath/…` 后 ad-hoc 签名；
    由 `RemoteDesktop.Platform.MacOS.csproj` 的 `BuildMacNativeHelper` target 调起
  - `fetch-libmsquic.sh`（把 libmsquic + openssl@4 重定位成 `@loader_path` 并随包分发，§3.4）
- `build/installer/client/macos/`：`make-app.sh`（`.app` 打包、`Info.plist`、entitlements、`codesign --deep`）、
  `make-dev-cert.sh`（自签开发身份，§3.9）、`development-identity`（**gitignore** 的本机文件，把签名身份钉住，
  避免 TCC 授权因"换了一张证书"而失效，§3.11⑦）。公证与 `.dmg` 待做。
- `src/Hosts/RemoteDesktop.Client/SelfCheck.cs`（`--self-check` 诊断模式，§3.8④）。
- `.github/actions/run-test-suite/action.yml`（Windows / macOS 两个 job 共用的"重试一次 + TRX + 汇总表"，§3.10）。
- `build/service/macos/`（`SMAppService` 登录项注册脚本）。

**修改**

- `DeviceIdentity`（3.2）、`ClientHost` / `ClientPlatformRegistration` / `Program`（平台选择）、
  两个 ViewModel（recorder 走 DI）、`StartupFailure`（macOS 消息框走 `osascript`）、
  `RemoteDesktop.Client.csproj`（平台条件）、`RemoteDesktop.slnx`、`.github/workflows/ci.yml`（加 `macos-26` job）、
  `README.md`/`docs/*`。
- 测试：`SystemSetupTests` / `HostControlTests` / `FileAccessPolicyTests` / `LocalFileOperationsTests` /
  `ServerSetupTests` / `FileTransferSessionTests` 加平台跳过（§3.10）；新增
  `RemoteDesktop.Platform.MacOS.Tests`（其中 `ScreenCaptureTests.cs` 是 P3 的，50 条里的 22 条）、
  `MsQuicAvailabilityTests`、`DeviceIdentityTests`。

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
- [x] 坐标约定统一：虚拟桌面用**逻辑点**、采集返回**像素**、输入用**归一化 0..1**，换算集中在一处并补 7 条测试

验收：`RemoteDesktop.Platform.MacOS.Tests` **50/50** 通过（非沙箱；其中 2 条真实采集用例在授予屏幕录制后运行）。
`--self-check` 在 `.app` 内外都能报告采集结果（**直接执行 `Contents/MacOS/RemoteDesktop.Client`**，见 §3.11⑧）。

> ⚠️ **P3 顺带暴露、但没修的一件事**：macOS 客户端目前**起不来**。`ClientHost.Build` 在 `ValidateOnBuild`
> 阶段就抛 `Unable to resolve service for type 'RemoteDesktop.Abstractions.Clipboard.IClipboard' while
> attempting to activate 'RemoteDesktop.Features.Desktop.ClipboardChannelModule'`（后面还跟着
> `IAudioCapturerFactory` / `IAudioPlayerFactory` / `ICameraEnumerator`）。这是**既有问题**，不是 P3 引入的
> （本机更早的崩溃日志早于本次改动），根因是剪贴板（P5）、音频（P9）、摄像头（P8）在 macOS 上还没有实现。
> **不要用"只注册存在的服务"糊过去** —— 那会把独立宿主静默降级成"能启动但功能残缺"，
> 反而更难发现。等 P5 落地后自然解除。

> ⚠️ **P3 的第二件未了事：输入注入只验到了映射层。** `CGEventInputInjector` 的 9 条测试覆盖的是
> 扫描码→`CGKeyCode`、修饰键、拖拽、滚轮换算，**没有一条真的发过事件** —— 因为本机 `Accessibility`
> 还是 `NOT GRANTED`（`--self-check` 会如实打印）。而"真的发一个事件"不能在自动化测试里做：
> 它会动用户的鼠标、敲用户的键盘。所以这一段要**手动验证**（授予辅助功能后，用 `--self-check` 之外的方式
> 驱动一次），在 P10 的联调里补。

### P4 视频编解码（VideoToolbox）⬜

`VTCompressionSession` / `VTDecompressionSession` 手写绑定，对齐 Windows 侧的 Media Foundation 实现。

### P5 剪贴板与防休眠 🔄

- [x] `ISystemKeepAwake` → `IOPMAssertionKeepAwake`（`IOPMAssertionCreateWithName` / `Release`，嵌套计数）
      —— **修过一次**：断言类型原先按导出符号查，第一次 `Acquire` 必抛（§3.8②），已改为字面量并补 4 条测试
- [x] `IUserContextGuard` → 恒返回 `null`；`IPrivacyScreen` → 未注册（`IsSupported` 语义见 §5）
- [ ] `IClipboard` → NSPasteboard（ObjC）

### P6 远程终端（PTY）✅

- [x] `IPtyHost` → `PosixPtyHost`：`posix_openpt`/`grantpt`/`unlockpt` + `posix_spawn`（**不用 `forkpty`**，
      避免 fork 一个托管进程）、登录 shell（`-l`）、`addopen(fd 3)`+`addclose(3)` 认领控制终端、拒绝路径形式的 shell 名
- [x] 窗口大小与 resize —— 走非可变参数桩 `__ioctl`（§3.5）；`Resize` 直接用常驻 master（§3.6）
- [x] 可取消的读（`poll` 分片 + `CancellationToken`）与进程组回收（`killpg`）

验收：`PtyWindowSizeInteropTests` 3 条 + `PosixPtyTests` 6 条全绿（0.23 s）。

### P7 文件传输适配 ⬜

复用 .NET `File` / `FileStream`，不涉及原生互操作。要确认的是**受保护目录**（`~/Desktop`、`~/Documents`、
`~/Downloads`）的 TCC 行为与拒绝时的错误提示。

**已经知道要修的两个点**（都是"这一层还假设 Windows"，§3.10 里有完整证据）：

- [ ] `RemoteFileSource.Combine` 用字面量 `\` 拼路径（`FileSources.cs:105`）。**不是换成 `Path.Combine` 就完了** ——
      路径语法由被控端决定，控制器不知道对端是什么系统，需要协议层面给出分隔符（或让被控端拼接）
- [ ] `FileAccessPolicy` 的允许根用 `Path.GetFullPath` 存、却拿 `ResolveFinalTarget` 去比（`FileAccessPolicy.cs:26` vs `:76`）。
      macOS 上 `/var → /private/var`，所有临时目录都在 `/var` 下，于是共享目录被判成 "not shared"。
      改动落在安全相关代码上，要在能同时验证 Windows 行为的环境里做
- [ ] 受保护目录的 TCC：`~/Desktop`、`~/Documents`、`~/Downloads` 的首次访问弹窗与拒绝路径

### P8 远程摄像头（AVFoundation）⬜

`AVCaptureSession` + delegate，走 ObjC 辅助库；需 `NSCameraUsageDescription` 与摄像头 TCC 授权。

### P9 音频采集与播放 ⬜

首选与截屏共用权限的 ScreenCaptureKit 音频；必要时降级为"不支持"。

### P10 录制、打包与无人值守 ⬜

录制（复用共享层）、`.app` 打包与签名、`SMAppService` 登录项（无人值守被控端）。


## 7. 风险与缓解

| 风险                                | 等级 | 缓解                                                                              |
| --------------------------------- | -- | ------------------------------------------------------------------------------- |
| ScreenCaptureKit 的 ObjC 互操作复杂度    | 高  | 方案 C 用小型 ObjC 辅助库收敛复杂度；先做最小可用截屏 PoC 再展开                                         |
| TCC 权限在开发期反复丢失                    | 高  | 第一天就用 `.app` + 稳定 Apple Development 签名（2.3）                                     |
| macOS 上 `CGEvent` 注入在某些受保护 UI 上无效 | 中  | 与 Windows 的 UIAccess 限制对齐，在文档中说明边界                                              |
| 音频环回权限与稳定性                        | 中  | 首选与截屏共用权限的 ScreenCaptureKit 音频；必要时降级为"不支持"                                      |
| `libmsquic` 分发（重定位、openssl 依赖）    | 中  | 打包时用 `install_name_tool` 重写 `@@HOMEBREW_PREFIX@@` 并随包携带 `openssl@4` 依赖（**已实现**，见 `native/macos/fetch-libmsquic.sh`） |
| **bottle 的 `minos 26.0` 顶高最低系统版本** | **高** | 已决策接受 macOS 26 为下限（§3.4/§8）；要降到 macOS 14 需改走"从源码构建 MsQuic + OpenSSL" |
| 公证需要付费开发者账号                       | 中  | 开发期用 Development 签名；发布前再决定是否公证                                                  |
| **开发签名证书**                          | 低  | **已解决（2026-10-12）**：两张真 Apple Development 证书已可用（`zeqian_gao` / Team `Y6ANU9Z954`，选定为开发身份）。此前的"签不了名"是缺 **WWDR G3** 中间证书，不是没证书（§3.11①）。`make-dev-cert.sh` 的自签身份保留作为无证书机器的兜底 |
| **文件传输层仍假设 Windows 被控端**         | **中** | 两处：`Combine` 硬编码 `\`；允许根未做链接解析（§3.10）。P7 处理；在此之前 3 条文件传输用例在 macOS 上跳过 |
| **`codesign` 静默只签可执行文件**          | **高** | 不加 `--deep` 时它拒绝密封 bundle，却**不报错**，只把标识符退化成 `apphost`、`Info.plist` 不绑定（§3.8①）。打包脚本必须用 `codesign --verify --deep --strict` 断言，不能只看退出码 |
| 框架依赖的 `.app` 在 Homebrew 的 .NET 上起不来 | 中  | 打包默认自包含（§3.8③）；`--framework-dependent` 仅用于调试 |
| 端口"能注册"≠"能用"                      | 中  | 电源断言就是这么坏的：容器能解析、第一次调用才抛（§3.8②）。`--self-check` 会真的把每个端口用一遍 |
| Retina/多屏坐标与像素换算错误                | 中  | 统一约定：虚拟桌面用逻辑点、采集返回像素、输入用归一化 0..1；在 P3 建立坐标单元测试                                  |

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

- 屏幕采集走 ScreenCaptureKit 的最小可用 PoC（P3 第一件事）。
- 受保护目录 TCC 行为的实际表现（P7）。
- 把 Windows 专有的测试用 `Assert.SkipUnless` 关掉，macOS 上的 CI 才有意义（P1 的 CI 项）。

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

# 原生辅助库被真的打进 bundle 并导出（make-app.sh 的验证输出，§3.11）
    libRemoteDesktopMacNative.dylib present; exported entry points:
      0000000000001234 T _rd_capture_create
      0000000000002064 T _rd_capture_destroy
      0000000000001bb8 T _rd_capture_start
      0000000000001f5c T _rd_capture_stop
      0000000000002130 T _rd_cursor_read
      000000000000254c T _rd_open_settings

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

# 测试（macOS 26.7.1 本机，dotnet test RemoteDesktop.slnx，**沙箱外**）：
dotnet build RemoteDesktop.slnx              → 0 警告 0 错误
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
# 沙箱内跑会看到 3 条失败（502/402/3/97）。先跑 kcprobe3 看 sandbox_check(NULL) 是不是 0，
# 不是 0 就说明这次运行被沙箱包住了，结论不可信 —— 这一步不能省（§3.7 那次误判就是这么来的）。

# CI（.github/workflows/ci.yml）
build        → runs-on: windows-latest（步骤未变，测试部分抽到组合动作）
build-macos  → runs-on: macos-26（arm64；macOS 26 GA 于 2026-02-26，当前镜像 26.6.2）
               先跑 native/macos/fetch-libmsquic.sh，再 restore / build / 漏洞审计 / test
两个 job 共用 .github/actions/run-test-suite（重试一次 + TRX 上传 + 汇总表）
# 汇总脚本用本机 pwsh 对着真实 TRX 跑过：表格行数与上面的总计/成功/失败/跳过完全对得上
```
