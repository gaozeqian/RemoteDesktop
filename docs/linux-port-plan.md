# Linux 平台移植实现计划

> 状态：**进行中**（P1、P2、P4、P6、P7、P9 完成；P11 服务端完成；P10 的录制完成；P3 完成不需要授权的部分）
> 目标平台：Linux 桌面（Ubuntu 24.04+ 为主要验证目标），x86_64
> 编写日期：2026-10-14

### 已确认的决策

| 决策点 | 选择 |
| --- | --- |
| 交付范围 | **三段全做**：被控端 + Avalonia 客户端 + `Server.*` 原生部署 |
| 采集与注入 | **Wayland 优先**（xdg-desktop-portal + PipeWire），X11 后续 |
| 打包 | 客户端与被控端 **`.deb` + systemd 用户服务**；服务端 **`.deb` + systemd 系统单元 + debconf** |
| 视频编解码 | **硬件与软件都经 libavcodec 一把桥**，不手绑 VA-API / NVENC |
| POSIX 共享 | 新建 `src/Platform/RemoteDesktop.Posix`，与将来的 macOS 实现共用语义相同的部分 |
| 基线 | 从 `master` 起分支，不依赖未合并的 `feat/macos-arm64-port` |

---

## 0. 结论摘要（先看这一段）

这个代码库本来就是为移植设计的，**不需要重写**：

- 依赖严格向内（`Domain ← Abstractions ← Protocol ← Transport ← Application ← Features ← Hosts`），由
  `tests/RemoteDesktop.ArchitectureTests` 按**程序集名前缀**强制。新增 `RemoteDesktop.Platform.Linux`
  自动纳入既有规则，规则表一行都不用改。
- 每一个平台相关能力都在 `src/Core/RemoteDesktop.Abstractions` 里有端口接口，Windows 实现集中在
  `src/Platform/RemoteDesktop.Platform.Windows`（34 文件 / 约 5000 行）——**这就是要写的部分**。
- 共享层（`Domain`/`Abstractions`/`Protocol`/`Signaling`/`Transport`/`Application`/`Features`/`Media`）
  **是纯 .NET，没有任何 Windows API**。`Transport` 的 QUIC 类甚至已经标注了
  `[SupportedOSPlatform("linux")]`，`Nat/PunchSocket.cs` 里唯一的 Windows 调用也已有 OS 守卫。
- 客户端**已经是 Avalonia 12 + `net10.0`**（不是 `net10.0-windows`），只被一处
  `if (!OperatingSystem.IsWindows()) return 1;` 挡住。
- 接口的文档注释里写着移植意图：`ISecretStore` 写了 "DPAPI, Keychain, **libsecret**"，
  `IAgentGateway` 写了 "Session 0 on Windows, **a daemon elsewhere**"，`IPtyHost` 写了
  "PowerShell and cmd on Windows, **bash and zsh elsewhere**"。

**净结论**：这是「新增一个平台项目 + 调整宿主装配 + 新增打包流程」，不是重写。

已经实测确认的事实（Ubuntu 26.04 / GNOME 50 / Wayland / .NET 10）：

- **未改动的解决方案在 Linux 上构建零警告零错误**，全部 18 个工程，包括 `Platform.Windows` 和
  Avalonia 客户端。`net10.0` 而非 `net10.0-windows` 这个决定在这里兑现。
- **`RemoteDesktop.Transport` 无需改动即可在 Linux 上工作**——QUIC 双向握手、真实 UDP 打洞、
  中继回退，集成测试全绿。唯一的前提是原生 MsQuic 库（见 §3.1）。

---

## 1. 现状分析

### 1.1 需要 Linux 实现的平台端口

全部位于 `src/Core/RemoteDesktop.Abstractions`；`src/Platform/RemoteDesktop.Platform.Windows/ServiceCollectionExtensions.cs`
里的 `AddWindowsPlatform` 就是要镜像的那份清单。

| 端口 | Windows 实现 | Linux 方案 | 状态 |
| --- | --- | --- | --- |
| `ISecretStore` | `DpapiSecretStore` | 0700 目录下的 0600 文件（`FileSecretStore`，在 `Platform.Posix`） | ✅ P1/P6 |
| `IUserContextGuard` | 判 SYSTEM | 判 `geteuid()==0`（`UnixUserContextGuard`，在 `Platform.Posix`） | ✅ P1/P6 |
| `ISystemKeepAwake` | `SetThreadExecutionState` | logind `Inhibit`（**系统**总线，持有返回的 fd） | ✅ P2 |
| `IDisplayEnumerator` | DXGI | `org.gnome.Mutter.DisplayConfig`，有 portal 会话后优先用 portal 的 `Streams` | P3 |
| `IScreenCapturerFactory` | DXGI Desktop Duplication | portal ScreenCast + PipeWire（`pw_thread_loop` + `MAP_BUFFERS`） | P3 |
| `IInputInjector` | `SendInput` | libei / `ConnectToEIS`（与采集同一个 portal 会话） | P3 |
| `IVideoEncoderFactory` ×3 | Media Foundation ×2 | `h264_nvenc` / `h264_vaapi` / 软件 x264，都经 libavcodec | ✅ P4 |
| `IVideoDecoderFactory` | Media Foundation | libavcodec | ✅ P4 |
| `IVideoRecorderFactory` | MF Sink Writer | libavformat（MP4 封装） | ✅ P10 |
| `IClipboard` | Win32 剪贴板 | portal `Clipboard` 优先，退到 XWayland X11 | P5 |
| `IPtyHost` | ConPTY | `posix_openpt` + `posix_spawn`（落在共享 `Platform.Posix`） | ✅ P6 |
| `ICameraEnumerator` / `ICameraSourceFactory` | Media Foundation | portal `Camera` 的 fd + V4L2 | P8 |
| `IAudioCapturerFactory` / `IAudioPlayerFactory` | WASAPI | PulseAudio，主循环由本侧驱动、状态靠轮询 | ✅ P9 |
| `IPrivacyScreen` | **未实现** | 不支持（`IsSupported => false`），与 Windows 一致 | — |
| `IAgentGateway` | 服务 + 会话 Agent | **不需要**，见 §2.1 | — |

### 1.2 宿主层需要改的地方

| 位置 | 现状 | 需要改 |
| --- | --- | --- |
| `Hosts/RemoteDesktop.Client/Program.cs` | `if (!OperatingSystem.IsWindows()) return 1;` | 放行 Linux |
| `Hosts/RemoteDesktop.Client/ClientHost.cs` | 类级 `[SupportedOSPlatform("windows")]`，无条件 `AddWindowsPlatform` | 改为按平台选择注册 |
| `Hosts/RemoteDesktop.Client/StartupFailure.cs` | `MessageBoxW`（user32） | 非 Windows 写 stderr 与 crash.log |
| `Hosts/RemoteDesktop.HostService/Program.cs` | 程序集级 `[SupportedOSPlatform("windows")]`，`if (!IsWindows()) throw` | 删掉程序集属性，放宽门禁，拆 `AddHostPlatform` / `AddHostAgent` |
| `Hosts/RemoteDesktop.HostService/HostControlServer.cs` | `WindowsPipes.CreateServer`（PipeSecurity / SID） | 换成 `$XDG_RUNTIME_DIR` 下的 Unix socket |
| `Hosts/RemoteDesktop.Client/ClientHost.cs` 数据目录 | `SpecialFolder.ApplicationData` | Linux 下那是 `~/.config`（配置目录，不是数据目录），改走 `LocalApplicationData` |
| `Hosts/RemoteDesktop.HostService` 数据目录 | `SpecialFolder.CommonApplicationData` | Linux 下那是 `/usr/share`，改走 `$XDG_DATA_HOME/RemoteDesktop` |
| `ViewModels/*` | 三处硬编码 `new MfVideoRecorderFactory()` 藏在 `IsWindows()` 分支后 | 删掉分支，从 DI 注入 `IVideoRecorderFactory` |
| `Client/Setup/HostServiceSetup.cs` | `Verb = "runas"` 提权 | 改成写配置 + `systemctl --user restart`，不需要提权 |
| `Server/Server.Shared/SelfSignedCertificate.cs` | `X509Store("My", LocalMachine)`、`MachineKeySet\|PersistKeySet` | Linux 没有证书存储，改写 PEM 文件 |
| `Server/Server.Shared/ServerSetup.cs` | Kestrel 写 `Subject`/`Store`/`Location`/`AllowInvalid` | 改写 `Path` / `KeyPath` |

`Transport/Nat/PunchSocket.cs` 的 `SIO_UDP_CONNRESET` 已有 `OperatingSystem.IsWindows()` 守卫，**无需改动**。
`NamedPipeServerStream` 在 Unix 上走 Unix domain socket 可用，但见 §2.5 为何仍然要换掉传输层。

### 1.3 共享层里与平台有关的缺陷（P1 已修）

这些是移植前就存在的真实缺陷，不是移植引入的：

- `FileAccessPolicy` 的规则全是照着 Windows 写的，两个方向都错——既拒绝合法路径（**任何含冒号的
  Linux 路径都被当成备用数据流拒绝**，因为驱动器前缀的判断不成立），又漏掉该拒绝的
  （`/proc/kcore`、`/proc/self/mem`、`/dev/sda` 在未配置共享根时可通过文件浏览器读到）。
  根路径比较还用 `OrdinalIgnoreCase`，在大小写敏感的文件系统上会让根之外、仅大小写不同的路径被判为
  「在根内」。已按平台区分，并补上 Unix 侧的 `/proc`、`/sys`、`/dev` 拒绝与符号链接环处理。
  注意 `/run` **故意不在黑名单里**：可移动介质挂在 `/run/media/<user>/…`，从 U 盘拷文件正是这个功能的用途。
- `RemoteFileSource.Combine` 硬编码 `\`。分隔符属于**读这个路径的那台机器**，而双方都不告知对方平台；
  被控端刚返回的目录字符串是唯一证据，且可靠——绝对 Windows 路径必定以驱动器号或 UNC 前缀开头。
- `SystemSetupTests` 是全仓唯一没有平台守卫的测试类，且在**构造函数**里碰 `Registry`；xUnit 的构造函数
  先于测试体，所以写在测试体开头的 `Assert.SkipUnless` 永远够不着。改掉之后该类里有 12 个与注册表无关
  的用例（写服务器地址、解析地址、删数据目录）在 Linux 上真的跑起来了——`ServerSetup` 那几个方法是纯
  文件操作，正是将来 `.deb` 的 `postinst` 要复用的。

---

## 2. 平台差异与必须做的架构决策

### 2.1 进程模型：单进程，不要 Agent

Windows 的模型是：`HostService`（LocalSystem，**Session 0**）看不见桌面，于是在用户会话里拉起
`SessionAgent`，两者走命名管道。

**Linux 没有 Session 0，而且 Wayland 的 portal 会话总线是 per-user 的——采集与注入必须位于用户的图形
会话之内。** 所以 Agent 分裂整体消失：`IAgentGateway` 不注册，`AddAgentForwardedDesktop()` 不调用，
宿主直接注册四个 `Add…Feature()`（客户端的 standalone 模式本来就这么写）。

`LocalHostControl` 的 `IAgentGateway? agent` 参数本来就接受 null，`HostStatus.AgentState` 本来就是
`string?`——**UI 不需要任何改动**。

代价要讲清楚：Linux 上只有一个进程，以登录用户身份运行在其图形会话内。「服务」这个概念的含义变了。

### 2.2 通用化 HostService，不要新建宿主项目

- 架构测试按前缀从输出目录加载程序集，`HostService`/`SessionAgent`/`Client` 只出现在**禁止**数组里、
  从不作为层被加载。新建 `RemoteDesktop.Host.Linux` 等于**不被任何规则覆盖**。
- 一个 OS 一个二进制：`.deb`、systemd unit、`.desktop`、发布流水线、`HostServiceSetup.cs` 都写着
  `RemoteDesktop.HostService`。
- 需要拆成 `HostPlatformRegistration.AddHostPlatform` + `AddHostAgent`，后者在 Linux 上**什么都不注册**
  ——方法的「空」就是设计本身。

### 2.3 共享 `Platform.Posix` 层

PTY 是真正值得共享的部分：`posix_openpt`/`grantpt`/`unlockpt`、`posix_spawn` + file actions、会话组长、
按**进程组**发信号而非发给 shell 本身、argv 封送、dispose 语义——这些在 macOS 与 Linux 上完全相同，
约 455 行。不同的只有常量与入口点：

| | Darwin | glibc |
| --- | --- | --- |
| `TIOCSWINSZ` | `0x8008_7467` | `0x5414` |
| `TIOCGWINSZ` | `0x4008_7468` | `0x5413` |
| `O_NOCTTY` | `0x20000` | `0x100` |
| `open` / `ioctl` | `__open` / `__ioctl` | 普通 `open` / `ioctl` |

**把 455 行复制两份是错误的设计**：两份的差异恰好是最容易出错、且出错后表现为「shell 能用但 Ctrl+C
没反应」的那几个常量。共享层落地的时机是 P6（PTY 那一步），此前只有 `FileSecretStore` 一个类，
为一门课开一个工程不值当——它现在放在 `Platform.Linux`，随 PTY 一起搬过去。

架构测试已允许：`Rules` 表里 `Platform` 的 forbidden 列表不含 `Platform` 自身。

### 2.4 原生互操作

沿用 Windows 项目手写 P/Invoke 的风格，`net10.0` 单一 TFM：

- 按**带主版本号的 soname** 绑定（`libavcodec.so.62`），soname 漂移时**大声加载失败**，而不是静默跑坏内存。
- **D-Bus 用 `Tmds.DBus.Protocol`**（纯托管、MIT、支持 `UnixFd` 的 fd 传递）。这不是偏好问题：
  portal 的 `OpenPipeWireRemote` 与 `ConnectToEIS` 都用 D-Bus 类型 `h` 返回**文件描述符**，
  `gdbus` / `busctl` 命令行**传不出来**；portal 还是「先挂匹配规则、后发请求」的异步模型
  （`CreateSession` 返回 request handle，结果作为 `org.freedesktop.portal.Request::Response` **信号**到达），
  同步一次性 CLI 做不到。自写 D-Bus 栈约 1500 行，不划算。
  用一个薄的包装让 portal 代码看不到 Tmds 类型，把依赖的暴露面控制在一个文件的宽度。

### 2.5 宿主控制通道

`PipeHostControl` 用 `NamedPipeServerStream`，.NET 在 Unix 上把它映射到 `$TMPDIR/.dotnet/corefx/pipe/`
——一个**共享**目录，谁先建谁以 0700 拥有，同机两个用户会互相干扰；`WindowsPipes.CreateServer` 用的
`PipeSecurity` / SID 在 Unix 上也不存在。

保留 `IHostControl` / `HostControlHandler`（它们本来就是 `Stream` 抽象的、平台中立），**只换传输层**：
`Socket(AddressFamily.Unix)` + `UnixDomainSocketEndPoint`，路径绑到 `$XDG_RUNTIME_DIR/remotedesktop/`。
顺带得到 Linux 上正确的安全模型（仅同用户），这与 Windows 的「任意已认证本地用户」是有意差异。

### 2.6 Wayland 采集：光标必须用 Metadata

`cursor_mode` 必须选 `Metadata`(2) 而不是 `Embedded`：抽象接口的文档写死了「两个光标事件都从
`CaptureAsync` 内部触发」且客户端自己画指针。`Embedded` 会把指针烧进像素里，客户端再画一个——重影。
`Metadata` 每帧带 `SPA_META_Cursor`，与 `CursorChanged` / `CursorMoved` 几乎 1:1。位图是预乘 BGRA，
要转直通 alpha。

格式请求 `BGRx` 并把尺寸钉死在 portal 给的矩形上，这样 `param_changed` 只需断言、无需协商；
`BGRx` 与 BGRA32 逐字节相同，`ColorConversion.BgraToNv12` **原样可用**。

诚实提示：光标元数据依赖合成器。若首帧后若干秒仍无 cursor meta，只警告一次并停止通告光标，
不要试图在会话内重新协商（`cursor_mode` 在 `SelectSources` 时就固定了，重开会再弹一次授权窗）。

### 2.7 输入注入：EIS 为主，uinput 为文档化的后备

- **一次授权同时覆盖采集与控制**：在 `org.freedesktop.portal.RemoteDesktop` 的同一个 session handle 上
  依次 `SelectDevices` / `SelectSources` / `Start`，然后 `OpenPipeWireRemote` 和 `ConnectToEIS` 都可用。
  一个对话框同时授予共享屏幕与远程控制，与 Windows 的体验一致。
- **`/dev/uinput` 是机器级特权**：加入可写它的组意味着「可随时、无需同意地向本机任何会话注入输入」。
  为了屏幕共享而让 `.deb` 授权这个，是笔坏账。uinput 只作为显式启用的后备，udev 规则随包安装但默认不生效，
  **不自动把用户加进组**。
- **`TextInputEvent` 在 EIS 下可解**：`ei_device_keyboard_key` 只收 keycode，但同一会话的
  `NotifyKeyboardKeysym` 收 X11 keysym，Unicode 映射为 `0x01000000 | codepoint`。uinput 下任意 Unicode
  无路可走。

必须设绝对坐标 region：libei 1.x 要求先 `ei_device_pointer_motion_region_add`，且坐标是 region 内归一化的。
设一个覆盖所有逻辑显示器并集的 region，抽象接口的 0..1 归一化几乎原样通过。

### 2.8 设备选择留在自己手里

枚举 `/sys/class/drm/renderD*`，读 `device/vendor`（`0x8086` Intel / `0x1002` AMD / `0x10de` NVIDIA）与
`device/driver`，逐个用 `vaInitialize` + `vaQueryConfigEntrypoints(H264Main/High, EncSlice)` 探测，
把选中的节点交给 `av_hwdevice_ctx_create`。**绝不假设 `renderD128` 就是编码器**——索引跨重启与 GPU
热插拔都不稳定。

已经实测：在一张完全不支持编码的显卡上，`vaInitialize` 会成功、`vaQueryConfigEntrypoints` 就是判别器，
**探测不会崩**。

---

## 3. 关键技术验证结果（本机实测）

测试环境：Ubuntu 26.04 "resolute"、GNOME 50 on Wayland、x86_64、Xeon E3-1240 v5、AMD FirePro W2100
（Oland / GCN1，**没有 VCE**）、.NET SDK 10.0.112。

### 3.1 MsQuic（已解决）

`System.Net.Quic` 是原生 MsQuic 的托管包装。Windows 由运行时包提供 `msquic.dll`，**Linux 没有**，
Ubuntu 仓库也没有。缺它时每一个开 QUIC 连接的测试都失败，错误是
`QUIC is not supported on this system`——看起来像平台不支持，其实是缺库。

用 `LD_DEBUG=libs` 追出运行时实际向加载器请求的名字是 **`libmsquic.so.2`**（带主版本号，
不是无版本号的那个）。三种加载方式实测：

| 方式 | 框架依赖发布 | 自包含发布 |
| --- | --- | --- |
| 库放在可执行文件旁 | ❌ | **✅** |
| 同上 + `System.Net.Quic.AppLocalMsQuic=true` | ❌（开关读到了但无用） | ✅（开关其实**不需要**） |
| `LD_LIBRARY_PATH` | ✅ | ✅ |

机制是 DllImport 的探测目录 = `System.Net.Quic.dll` 所在目录。框架依赖时那是共享框架目录（改不了），
自包含时就是 app 目录。**发布形态按计划走 self-contained，所以不需要开关、不需要 `LD_LIBRARY_PATH`、
也不需要装到系统路径。**

`native/linux/fetch-libmsquic.sh` 从 `packages.microsoft.com` 取 `.deb`、按固定 sha256 校验后再解包，
只留共享库。与 macOS 不同的是这里不需要改 rpath：该库只依赖发行版自带的 `libcrypto.so.3` 与 `libnuma.so.1`。

测试与 CI 仍需要它（测试是框架依赖的），CI 用 Microsoft 自己的仓库按 runner 的发行版装。

### 3.2 显示枚举可以免授权（已实测）

```
busctl --user call org.gnome.Mutter.DisplayConfig /org/gnome/Mutter/DisplayConfig \
    org.gnome.Mutter.DisplayConfig GetCurrentState
```

**无需授权、无需 portal**，直接返回完整布局。两个必须设计进去的后果：

- **物理 vs 逻辑**。本机当前模式是 2560×1440（物理），但逻辑显示器是 1920×1080（scale 4/3）。
  `DisplayInfo.Bounds` 按既有契约必须是**物理像素**，而 `DisplayInfo.Scale` 要填 `1.33333`
  ——**这是该字段第一次不为 1.0**。合成器坐标（EIS region 用、匹配 portal stream `position` 用）
  是**逻辑**的。多显示器的 `x`/`y` 也是逻辑坐标，所以要乘各自 scale；混合 DPI 下不是单一全局系数。
  把这段算术收在一个地方并写单元测试。
- portal 的 `Streams` 可能是 Mutter 列表的**子集**（用户可以在对话框里取消勾选某些屏），
  按逻辑位置换算成物理后匹配，**portal 列表优先**——只有它知道用户实际授权了哪些屏。

### 3.3 环境事实（影响打包）

- `/dev/dri/renderD128` 是 `root:render 0660`，**桌面用户靠 seat ACL 拿到访问权**（systemd-logind 给
  active seat 上的用户加 `uaccess` ACL）。被控端是 `systemd --user` 服务、以该用户身份运行且有 logind
  seat，所以通常无需加组；只有无登录会话的 system/systemd 服务 / 纯 headless 场景才需要加入
  `render`（通常还有 `video`）组。
- `video` 与 `input` 两个组在本机**都存在但为空**，当前用户不在其中。所以摄像头要走 portal 的 fd，
  直接开 `/dev/video0` 开箱即败。
- `/etc/hosts` 把主机名映射到 `127.0.1.1`，`Dns.GetHostName()` / `Environment.MachineName` 可解析。

### 3.4 在本机跑测试套件（两个会白费半小时的坑）

```bash
native/linux/fetch-libmsquic.sh                       # 只需一次
LD_LIBRARY_PATH=$PWD/artifacts/native/linux dotnet test --solution RemoteDesktop.slnx -c Release
```

- **必须带 `LD_LIBRARY_PATH`。** 测试是框架依赖发布的，`libmsquic.so.2` 不在共享框架目录里；缺它时每一个
  开 QUIC 连接的用例都失败，错误是 `QUIC is not supported on this system`——看起来像平台不支持，
  其实是缺库。自包含发布不需要这一条（库就在可执行文件旁），测试需要。
- **`--nologo` 不能传给 `dotnet test`**（见 CI action 里的注释），它会转发给测试程序并以退出码 5 拒绝整次运行。
- **inotify 实例上限。** `fs.inotify.max_user_instances` 默认 128，而一个普通的 GNOME 桌面
  （VSCode 的 Roslyn、1Password、wireplumber、gvfs…）自己就用掉 90 多个。`TestDevice` 原本用
  `Host.CreateApplicationBuilder()`，它会为 `appsettings.json` 装一个**reload 型**文件监视器，
  每个 host 占一个 inotify 实例——而那个项目里根本没有 `appsettings.json`。141 个用例并发起 host 时
  就会撞上限，报 `IOException: ... inotify instances has been reached`，在**构造 host 时**失败，
  看起来与代码无关。已改为 `DisableDefaults = true` + 手动加回内容根与环境变量
  （`TestRendezvous` 早就在用 slim builder 规避同一件事）。

---

## 4. 实施阶段

### P1 — 平台骨架、构建、CI ✅ 已完成

- `src/Platform/RemoteDesktop.Platform.Linux`（`net10.0`、只依赖 `Abstractions`），
  `AddLinuxPlatform(dataDirectory)` 镜像 `AddWindowsPlatform` 的清单。
- 首批落地两个端口：`FileSecretStore`（0700 目录 / 0600 文件，照抄 `DpapiSecretStore` 的
  `.tmp` + `File.Move(overwrite)` 原子替换）与 `LinuxUserContextGuard`。
  **端口按阶段逐个注册，不预先铺桩**：每个端口都要先是真实现才有意义，而构造函数抛异常的桩只会把
  「这个功能还没做」变成「宿主起不来」。
- `tests/RemoteDesktop.Platform.Linux.Tests`：建真实容器并逐个解析，让「构造函数会抛」的端口无法
  蒙混过注册；另有用例断言 `IAgentGateway` **不在**容器里（见 §2.1）。
- 给 `RemoteDesktop.ArchitectureTests` 补 `ProjectReference`——不加的话按前缀加载的规则根本看不到新工程，
  而测试仍然全绿。
- `.github/actions/run-test-suite/action.yml`（与 macOS 分支同一份，逐字节一致）+ `ci.yml` 的
  `build-linux` 作业。
- `native/linux/fetch-libmsquic.sh`。

结果：**构建 0 警告 0 错误；全仓 486 个测试 0 失败**（起始 449 个里 85 个失败）。

### P2 — 密钥、身份、传输、guard、防休眠（D-Bus 桥落地）✅ 已完成

顺序上的一个关键调整：**不要**把「防休眠」放到后面。logind `Inhibit` 是整个移植里最简单的 D-Bus 调用，
用它把 D-Bus 桥打通并验证，到 P3 开始时传输层已确认可用，新东西只剩 portal 的 request/session 状态机。

落地内容：

- `LogindKeepAwake` 实现 `ISystemKeepAwake`。logind 的 inhibitor **就是一个文件描述符**：要一个就是开始
  抑制、关掉它就是结束，没有单独的调用。所以这个类持有的是一个描述符而不是一个标志位——也因此必须把
  描述符从回复里 **dup** 出来，连接拥有消息里那一个并在读完后立刻关掉，不 dup 的话抑制会立刻失效。
  测试断言的不是这个类自己的标志位，而是 **logind 自己报告的**抑制列表（`ListInhibitors`），因为描述符
  被悄悄关掉从类内部看和正常一模一样。
- 宿主控制通道换成 `$XDG_RUNTIME_DIR/remotedesktop/` 下的 Unix socket（`HostControlEndpoint` +
  `HostControlSocketListener`）。`IHostControl` / `HostControlHandler` 未动——它们本来就是对 `Stream`
  抽象的。安全模型说明写进了 `HostControlEndpoint` 的注释：Windows 上任意已认证本地用户都能问服务要设备码，
  这里只有拥有那个运行时目录的账号能，实际就是宿主本来就运行的那个账号。
- `DeviceIdentity` 的三处 PKCS#12 加载统一走一个 `Import` 辅助方法并加 `X509KeyStorageFlags.Exportable`
  ——MsQuic 要把私钥取出来交给 TLS 层，取不出来时报的错与证书毫无关系。macOS 分支已证实这是必需的。
- `DataDirectory.ForUser()` / `ForMachine()`。`Environment.SpecialFolder` 在 Linux 上会给出一个**看起来
  合理但错误**的答案：`ApplicationData` 是 `~/.config`（那是配置目录），`CommonApplicationData` 是
  `/usr/share`（那是发行版的地盘）。非 Windows 下两者返回同一个目录——被控端以登录用户身份运行在该用户的
  会话里，没有第二个账号的状态需要分开存放。
- 删掉了 `HostService` 的 `[assembly: SupportedOSPlatform("windows")]`。它让 CA1416 对**整个程序**闭嘴；
  删掉之后分析器立刻指出了 `HostControlServer` 里三处 Windows 专有调用没有守卫，这正是它应该做的。

安装 `Tmds.DBus.Protocol` 时的一个观察：**计划里说的「薄包装让 portal 代码看不到 Tmds 类型」达不到它想要
的目的**——读取回复必须传 `Message`/`Reader`，那些类型**就是**这个 API，包装一层并不缩小爆炸半径。改为
按约定把 Tmds 的使用限制在少数几个文件里，并在 `Directory.Packages.props` 里注明上游仍是 0.x。

验证：全仓 **499 个测试 0 失败**；`KeepAwakeTests` 通过 `ListInhibitors` 观察到抑制器真的出现与消失，
即 .NET 经 D-Bus 传递文件描述符已端到端验证；`HostControlTests` 现在跑在真实的 Unix socket 传输上
（此前它用 `NamedPipeServerStream` 搭假服务端，与新的客户端传输对不上，这也是一处真实的移植影响）。

### P3 — Wayland 采集与输入（核心，风险最高）✅ 已完成

**portal 会话流程已在真实实现里跑通**（2026-10-14，真机授权一次）。
`XdgPortal/PortalConnection.cs` + `XdgPortal/PortalResponse.cs` 完成
`CreateSession` → `SelectSources` → `Start` → `OpenPipeWireRemote`，实测输出：

```
session=/org/freedesktop/portal/desktop/session/1_1407/rdSession1393a5fe
  node=82 at (0,0) 1920x1080
pipewire fd=98
```

另有 `tests/RemoteDesktop.Platform.Linux.Tests/PortalScreenCastTests.cs` 两个用例覆盖同一流程，
默认跳过、`RD_PORTAL_TEST=1` 时运行（要有人在真实桌面上点一次「共享」）。

**五个把前几版实现卡住的坑**，全是"错了不报错、只表现为连接断开、进程消失或干脆挂死"的那类：

- **`MessageWriter` 是 `ref struct`，绝不能穿过委托。** 把"写方法体"这件事交给一个
  `Action<MessageWriter, string>` 去做——那是与跑通的版本**唯一**的差别——连接立刻断开、进程核心转储。
  方法体必须写在持有写入器的那个方法里，一个请求一段。
- **同一条规则的另一半：`Reader` 不能交给另一个方法，`ref` 也不行。** 把「读一个 `a{sv}`」抽成
  `ReadVariants(ref Reader reader)` 看起来天经地义，实测结果是**每个字典都返回空、调用方的读取位置还留在
  原地**——于是后面几百字节的地方报 `unexpected end of data` 或 `invalid UTF-8`，而真正的原因隔着一整段
  栈。Mutter 的 `GetCurrentState`（`ua((ssss)a(siiddada{sv})a{sv})a(iiduba(ssss)a{sv})a{sv}`）整个遍历
  因此写在 `Parse` 一个方法里。**只有 `VariantValue` 能跨方法**，因为它自带字节。这是那个库的
  `ref struct` 游标的性质，不是这条消息的性质——排查时先怀疑它。
- **`AddMatchAsync` 必须 `await`，不能阻塞等待。** 用 `.GetAwaiter().GetResult()` 会死锁：应答要靠
  连接自己的读循环推进。
- **`VariantValue.ToString()` 不是值本身**，它会给对象路径加上 ` [String]`。把这个字符串当作对象路径
  发回去，总线因为收到非法路径而**直接断开这个连接**——错误表现是"连接被对端关闭"，与真正的原因隔着
  好几层。必须在消息还活着的时候用 `GetObjectPathAsString()` / `GetString()` 取出来；消息一过手，
  那些值也成了悬垂引用。
- **同一个值，portal 文档写 `o`，GNOME 后端发 `s`。** `session_handle` 实测 `Type = String`，
  对它调 `GetObjectPathAsString()` 抛 `InvalidOperationException`；`GetString()` 才拿到干净的路径。
  按文档类型去断言并不会让值变成那个类型，只决定能不能读到。`PortalResponse.ReadText` 两种都收。
- **订阅里的读取器一旦抛异常，就等于这个请求永远没有应答。** Tmds 不会把读取器抛出的异常交给
  observer，**一个通知都不会发**——于是调用方一直等到自己的超时，然后被告知"portal 没有回答"，
  而 portal 其实**当场就回答了**。上面那条 `GetObjectPathAsString()` 就是这么表现的：`CreateSession`
  的应答 3ms 就到了，代码却挂了 5 分钟。所以 `PortalRequestResult.Read` 写成**不抛**的：整个函数体包在
  `try/catch` 里，失败作为结果的一个字段返回，由 `AwaitAsync` 明确报出来。

  这条是花时间最久的，因为三个"证据"全都指向别处：`dbus-monitor` 显示总线**接受了**我们的 `AddMatch`、
  portal **确实**在订阅的那个路径上发了 `Response` 信号、目的地址就是我们的连接。总线这一侧完全正确，
  问题在客户端的派发。**下次再遇到"订阅不到"，先怀疑读取器，而不是先怀疑规则和路径。**

另外记下与计划一致的接口事实：`ScreenCast.version = 5`、`AvailableCursorModes = 7`（Metadata 可用）、
`RemoteDesktop.version = 2`（有 `ConnectToEIS`）、`AvailableDeviceTypes = 7`。
`CreateSession` 与 `SelectSources` 都没有弹窗（后者直接返回 0），**授权对话框出现在 `Start`**。

**`persist_mode = 2` + restore token 确实有效**：token 存在 `ISecretStore` 里，第一次授权之后再跑
（probe 和测试都试过）**不再弹窗**。这是无人值守访问能成立的前提，值得确认。失败时（拒绝/取消）
portal 返回非 0，`OpenScreenCastAsync` 抛 `PortalException`，其中拒绝是 2、取消是 1。

**风险表里那条未验证项现在有答案了**：portal 给的 `size` 是**逻辑（合成器）坐标**——本机物理模式
2560×1440 / scale 4/3，portal 报的是 **1920×1080**。`DisplayGeometry` 的单测钉的正是这组数字，方向对的。

**先做 spike**：用 shell + `busctl` 把整个 portal 流程手写一遍并看它工作，再写 C#。

**已完成的是不需要 portal 授权的那一半**（可以纯单元测试，不碰真实桌面）：

- `Display/SpaPod.cs` —— PipeWire 序列化格式的读取器。格式协商的答案就是一个 pod，而屏幕尺寸/像素格式
  在驱动之间可能是定值、区间或候选列表，所以这一层非有不可。写成自己的类型而不是一堆解析函数，是为了
  能把「读一段字节」和「连着一台真实桌面」分开——**事实证明确实如此**：写测试时用字面字节数组钉住布局，
  立刻抓到解析器把类型字放在偏移 8 而不是 4 的错（读到了 body 的第一个字，一个"看起来合理"的小数字，
  于是类型错了却像对的）。这个错在真实链路上会表现为"协商总是失败"而毫无线索。
- `Input/ScanCodeToEvdev.cs` —— 线上协议发的是 PC 扫描码 set 1，evdev 按位置编号，两者之间没有公式。
  表的值逐条对着 `/usr/include/linux/input-event-codes.h` 抄。`IsExtended` 用来消歧 `E0` 前缀那一批：
  `0x1C` 不加前缀是回车、加了是小键盘回车——发错一个就是"按了回车却打出别的东西"，而且只对按下那个键的人
  发生。没有对应键的扫描码（BIOS 的 SysRq、`E1` 序列的 Pause）**明确报告而不是猜一个**。
- `Display/DisplayGeometry.cs` —— 两套坐标系的换算。合成器用缩放后的单位排布窗口，而一个显示器的位置是
  按**它自己的**缩放算的，所以混合 DPI 桌面上两台显示器之间的距离不是处处相同的设备像素数。
  测试直接钉本机实测的数字（2560×1440 物理 / 1920×1080 逻辑 / scale 4/3）。

**已完成**：`Display/MutterDisplayConfig.cs` + `Display/WaylandDisplayEnumerator.cs`（已注册为
`IDisplayEnumerator`）。本机实测输出：

```
DP-1 'Philips Consumer Electronics Company 24"'
  logical 1920x1080  scale 1.3333333730697632  physical 2560x1440  primary=True
```

物理 2560x1440 与 `busctl` 一致，逻辑 1920x1080 与 portal 报的一致——**两套坐标系的换算在两条独立来源上
对上了**，这比单测更说明问题。`DisplayInfo.Bounds` **必须是物理像素**、`Scale` 单独带出来，因为客户端
再画一个逻辑矩形的话，在 scale=1 的屏上完全看不出来、在别的屏上全错。

> 踩坑记录：Mutter 的 `GetCurrentState` 里，显示器尺寸**不在**任何一张表里——它是标了 `is-current` 的那个
> mode 的尺寸。取第一个 mode、或者取 preferred，在「用户改过分辨率」的机器上就是错的，而那几乎包括每台机器。

**采集已经通了。** `PipeWireScreenCapturer` 拿到真实画面，实测：

```
frame 2560x1440 Bgra32 stride=10240 bytes=14745600
distinct colours (sampled 1/16): 2608
```

导出成 PNG 看过——就是桌面本身：编辑器、终端、顶栏时钟、dock 全在，**颜色也对**（蓝色按钮是蓝的，
说明 BGRA 顺序没搞反）。而且**画面里没有指针**，正是 `cursor_mode = Metadata` 应有的样子。
尺寸是 **2560x1440（物理）**，不是 portal 报的 1920x1080（逻辑）——与 `DisplayInfo.Bounds` 的契约一致，
也正是编码器要的。

`tests/.../PipeWireScreenCapturerTests.cs` 覆盖三条：帧的尺寸与 stride、画面确实是一幅图（不是单一颜色
或错位的图案）、静止画面返回 null 而不是把上一帧再发一次。默认跳过，`RD_PORTAL_TEST=1` 时运行。

### 采集中间踩过的两个坑（都只表现为一句没头没尾的错）

- **`pw_context_connect_fd` 返回时 core 还没绑定完，对象表是空的。** 此时 `pw_stream_connect` 带
  `AUTOCONNECT` 去查目标节点，查了个空，回 **`no target node available`**——节点其实下一刻就在。
  现在会先等目标节点出现（用注册表监听 + `ManualResetEventSlim`，5 秒超时），而不是等一个固定时长。
- **portal 的 `node_id` 和流要找的不是同一个数。** 本机那个被授权的节点 id 是 78，而它的
  `object.serial` 是 1199；流要用后者来指名。

还有一个更隐蔽的：**读注册表时不能在持有 thread-loop 锁的情况下等待。** 事件是在 loop 自己的线程上派发的，
调用方握着锁等，等的是一个永远不会发生的事——这正是第一版枚举在"明明有节点的连接"上看到空列表的原因。
写完一个等价的 C 程序对照才确认 API 用法没问题、问题在锁。**排这一类"库说不了话"的问题时，写个 C 对照
是最快的一条路**，这次两个坑都是这么定位的。

### P3 剩下的最后一件：输入注入（已完成）

`Input/LibEiInputInjector.cs` 已通过 `ConnectToEIS` + libei 注入输入并实测有效：把指针移到左上角
（**只移动、不点击**，GNOME 的热区会自己弹出概览），画面随之改变；测试结束时把指针放回中间，
概览随之关闭。`LibEiInputInjectorTests` 覆盖这一条，并把 `SendSecureAttentionSequence` 的
`NotSupportedException` 也钉住。

实测日志（说明链路每一环都到位）：

```
A seat was offered; asked for pointer, buttons, keyboard and scroll.
The desktop offered a device with: pointer, buttons, scroll.
Started emulating (0).
The desktop offered a device with: keyboard.
Started emulating (0).
```

四个坑，都是"错了不报错"那类：

- **方法名是 `ConnectToEIS`，不是 `ConnectToEis`。** 大小写不同 → `UnknownMethod`。
- **RemoteDesktop 会话必须 `Start`。** 只做 `CreateSession` + `SelectDevices` 的话，后面所有调用都回
  **"Session is not ready"**，包括那个本该把 fd 交出来的 `ConnectToEIS`。
- **桌面给的是多个设备，不是一个万能的。** 本机先来一个 `pointer, buttons, scroll`，键盘是**另一个**
  设备。只记一个"当前设备"的话，后到的键盘会覆盖掉指针，指针事件发到键盘设备上被**静默丢弃**——
  表现为"注入没反应，但也不报错"。现在按能力分别记住，并且**按设备 `frame`**（libei 是按设备提交的）。
- **`SafeFileHandle` 会把 libei 的 fd 关掉。** `pw`/libei 接管 fd 之后必须 `SetHandleAsInvalid()`，
  否则 wrapper 析构时关掉一个已经属于 libei、甚至可能已经分配给别人的描述符。

### 一个必须记下来的发现：远程桌面会话不能持久化

本来打算按原计划把采集和控制放在**同一个会话**上（一次授权管两件事）。实测被 portal 拒绝：

```
org.freedesktop.portal.Error.InvalidArgument: Remote desktop sessions cannot persist
```

**只要在 RemoteDesktop 会话上请求 `persist_mode`，就会整条失败。** 也就是说**输入控制无法被预先授权**——
portal 在这件事上是刻意的：看一块屏和操作一台机器不是同一个许可。

所以最终是**两个会话**：

| | 会话 | 能否持久化 | 后果 |
| --- | --- | --- | --- |
| 采集 | ScreenCast | ✅ | 授权一次即可，无人值守可用 |
| 控制 | RemoteDesktop | ❌ | **每次连接都要人在场点一次** |

这是相对 Windows 的真实差异（那边一次登录会话同时拿到画面和输入），**要写进文档**。
把两个塞进一个会话看似更省事，代价是连"只看不控"也要每次弹窗，对无人值守是倒退。

### 光标元数据在本机收不到

`cursor_mode = Metadata` 的授权是成功的（`AvailableCursorModes = 7` 含 Metadata），但 PipeWire 流里
**始终没有 `SPA_META_Cursor`**：`PipeWireScreenCapturer` 在若干帧之后会警告一次
「合成器没有描述指针」，然后照常出帧。这正是风险表里那条"光标元数据可能不送达"。

后果：**控制器只能画自己最后知道的那个指针**，位置不会实时更新。要接着查的话，方向是先确认
`SPA_PARAM_Meta` 那个 pod 走 `update_params` 有没有被真正应用（connect 时带 Meta 参数是被接受的，
带 Format 不是——见上），再看 mutter 是否要求别的条件。


### P4 — 视频编解码 ✅ 已完成（硬件路径待真 GPU 机器验证）

三个编码工厂（NVENC / VA-API / 软件 x264）都经 libavcodec，配置镜像 Windows 侧：CBR、`bf=0`、
`GOP = frameRate × 10`、Main profile、NV12 进、**Annex-B 出**（不设 `AV_CODEC_FLAG_GLOBAL_HEADER`，
否则 SPS/PPS 会跑进 extradata，控制器永远看不到）、每个 IDR 带内联 SPS/PPS。

**用 `FFmpeg.AutoGen` 8.0.0**（按头文件生成的绑定），不是手写 P/Invoke。理由：`AVCodecContext` 是个每
个版本都在长的结构体，**偏移写错是破坏内存而不是报错**。生成的绑定把这个风险整类消掉，而且版本选择与
「按主版本 soname 绑定」的既有决定一致——绑定就是 ffmpeg 8 / libavcodec 62，`FfmpegLibraries` 去找的
也正是 `libavcodec.so.62`，找不到就带版本号大声报错。Windows 侧对 Direct3D 用的 Vortice 是同一个取舍。
`AVFrame`/`AVPacket` 的头部字段本可手写（自 FFmpeg 2.x 起位置未变），但既然引了绑定就没必要两套。

**计划里的一处被改掉**：原计划让 `IsSupported` 调 `vaInitialize` + `vaQueryConfigEntrypoints(EncSlice)`
来判断设备能不能编码。实际动手时本机没装 libva 头文件，**那几个 entrypoint 常量我无法就地核对**，而抄错
的方向恰好是「有能力编码的机器被判成不能」——静默永远用软件。改成**直接在设备上开一个编码器来试**：
这正是宿主真正关心的问题，而且它不可能答错。代价是一台机器上每个 render node 开一次编码器（毫秒级，
每进程一次），结果记在 `Lazy` 里（包括否定的结果）。

**计划里的启动自检也不再需要**：它本来是用来防 ABI 漂移的，而版本锁定 + 生成的绑定已经把那类风险移走了；
真出问题时宿主既有的 `allowHardware=false` 降级路径会接住。

实测（本机 Oland/GCN1，无 VCE）：

- 编码器产出 Annex-B、**关键帧只在被要求时出现**（GOP 是 10 秒，所以没有周期性 I 帧）、`bf` 读回来是 0、
  码率可以在不重建编码器的情况下上下移动。
- 编解码往返：关键帧解出 640×480 的 NV12、亮帧解出来比暗帧亮、**先解出的帧不会被后续解码改掉**
  （解码器内部复用一张图，直接交出它意味着渲染器正在显示的帧会在下一次解码时变掉）。
- **VA-API 工厂在本机正确判定为不可用并让位给软件**，这正是这台机器作为反面测试的价值。
- 一个反证：`h264_vaapi`、`h264_nvenc`、`libx264` 在本机的 FFmpeg 构建里**都存在**。存在不等于可用，
  这就是为什么探测要去开一个编码器而不是问「有没有这个编码器」。

**真硬件编码仍需一台 AMD Polaris/VCN+、Intel Gen9+ 或 NVIDIA 的机器验证**，本机做不到。

### P5 — 剪贴板 🚫 portal 拒绝，需要 X11 后备

**spike 做完了，结论是此路不通。** 计划里写的风险（"后端可能拒绝 `RequestClipboard`"）确认成立。四种
组合全部被拒，错误各不相同，说明卡在不同的检查上：

| 会话 | 时机 | 结果 |
| --- | --- | --- |
| ScreenCast | `Start` 之后 | `Invalid session type` |
| RemoteDesktop | `Start` 之后 | `Invalid state` |
| RemoteDesktop | `Start` 之前 | `Invalid state` |
| RemoteDesktop + `SelectSources` | `Start` 之前 | `Invalid state` |

接口本身在总线上、GNOME 后端也实现了（`RequestClipboard` / `SelectionRead` / `SelectionWrite` /
`SelectionWriteDone` / `SetSelection` 都在），所以不是"没实现"，是**不让这个应用用**——与 portal
文档里"剪贴板面向沙箱应用"的说法一致。

计划里写的后备是**在 XWayland 上自己持有 `CLIPBOARD` selection**（libX11 + XFixes）：X11 客户端可以
持有 selection，合成器会把它镜像到 Wayland 剪贴板。这条路没有试过，是独立的一块工作。

**当前状态**：`WaylandClipboard` 注册了但不工作——`Get()` 返回空、`Set()` 记一条警告然后什么都不做、
`Changed` 永不触发（用显式的空 add/remove 表示，而不是留一个没人触发的字段）。这样"剪贴板空"和
"剪贴板不支持"在日志和文档里能分开，不会让人复制完再粘贴发现什么都没有还以为是偶发。
### P6 — 终端（PTY 落在共享 `Platform.Posix`）✅ 已完成

`Platform.Posix` 工程建立，`FileSecretStore` 与用户上下文守卫（改名 `UnixUserContextGuard`）搬了进去，
加上 `Interop/LibC.cs` 与 `PosixPtyHost` / `PosixPtySession`。本仓库的 `Platform` 层规则不含 `Platform`
自身，所以 `Platform.Linux → Platform.Posix` 合法，规则表一行未改。

**子进程用 `posix_spawn` 而不是 `fork`。** .NET 进程 fork 出来的子进程与其余所有线程共享地址空间，而运行时
的锁处于那些线程留下的任意状态；子进程在 `exec` 之前碰任何一个都可能死锁。`posix_spawn` 是带
`CLONE_VM|CLONE_VFORK` 的 `clone`，没有第二个地址空间可以处于不一致状态。这也是为什么
`posix_spawn_file_actions_t` / `posix_spawnattr_t` 是按不透明缓冲传的：它们只由 glibc 填写、由内核执行，
这个代码一个字段都不读。缓冲区按**宽裕**分配（512 / 128 字节，glibc 2.39 实际需要 336 / 80）——尺寸算错会
让 glibc 写到缓冲区之外，而分配大一点是免费的。

**常量逐个对着头文件核，不凭记忆**，因为这几个恰好是错了也不报错的那类：

| | glibc | Darwin |
| --- | --- | --- |
| `POSIX_SPAWN_SETSID` | **0x80** | 0x0400 |
| `TIOCSWINSZ` / `TIOCGWINSZ` | **0x5414 / 0x5413** | 0x80087467 / 0x40087468 |
| `O_NOCTTY` | **0x100** | 0x20000 |

抄错的后果分别是：子进程不在自己的会话里、终端没有控制终端——表现为**一个看起来完全健康但 Ctrl+C 没反应
的 shell**；以及窗口尺寸永远设不上——表现为全屏程序画成错误的形状。macOS 分支那份 `PosixPtyHost` 里
`SpawnSetsid` 写的是 `0x0002`，两个平台都不是——这正好说明为什么这一层要共享而不是复制两份。

测试跑的是真实 shell（8 个用例）：`sh` 能起来并跑命令、`stty size` 报出控制器要求的尺寸、**Resize 之后
`stty size` 跟着变**、**Ctrl+C 真的停掉 shell 正在跑的东西**（这条就是上面那个常量的判据）、退出码传回、
**关闭会话时后台作业跟着走**（普通 shell 不会把挂断转发给它启动的后台作业，所以只给 shell 发信号会留下
孤儿进程），以及路径被当作 shell 名时被拒绝（否则 shell 选择器就成了执行任意程序的方式）。
### P7 — 文件传输（§1.3 的共享层修复已在 P1 完成，这里是 `LocalFileOperations` 与 `XdgUserDirectories`）
### P8 — 摄像头（portal fd + V4L2）
### P9 — 音频 ✅ 已完成

**计划里的一处被改掉：不是 `pa_threaded_mainloop` + 回调，而是 `pa_mainloop` 由本侧驱动、状态靠轮询。**
回调式绑定要给每个事件配一个托管入口点，每个都在 PulseAudio 的线程上跑、都要说清自己来自哪里；轮询那几个
真正关心的状态机械少得多，也少几个能藏生命周期 bug 的地方。唯一保留的回调（读默认输出名字）是在
`pa_mainloop_iterate` 内部、调用方线程上跑的，所以它连同步都不需要。

**跟随用户的耳机**用的是「隔一段时间问一次默认输出叫什么」，不是订阅事件。订阅是显而易见的做法，也是第一版
实现，但它需要事件的类别编号——本机没有头文件可核，猜错的方向是**采集在任何事件发生时立刻结束，包括采集
流自己创建时产生的那个事件**。改成问名字就不需要那个编号，而且答案本来就是我们要看的东西。

**静音门控是契约履行，不是优化。** 接口上写着「设备在放静音时不会有数据到达」——Windows 的环回设备天然
如此，而 PulseAudio 的 monitor 源**永远**在送数据，包括数字静音。照单全收会让被控端在一台没人用的机器上、
在整个连接期间，持续以满码率编码并发送静音。

两个被测试抓出来的错误，都是「从记忆里写的常量」：

- **上下文和流的状态枚举不是同一个枚举，编号也不一样**：`PA_CONTEXT_READY = 4`，而 `PA_STREAM_READY = 2`。
  只用一套常量会让流永远等一个不会出现的值。表现是「流一直没就绪」，而实际上它早就绪了。
- `pa_sample_format_t` 的 `FLOAT32LE = 5` 本机无法核对，所以改成**运行期验证**：调
  `pa_sample_size_of_format(5)`，它必须回答 4。这一条把「猜的值」变成了「代码在相信它之前先验证的值」。

测试（9 个，跑在真实的 pipewire-pulse 上）：格式常量自证、静音判定、**采集连上并干净运行**、**播放连上并
接受音频、能报告积压**、**塞入远超设备能承受的数据不会长出队列**（否则声音会永久落后于画面且无法追上）、
以及**切换默认输出会让采集结束**（用机器上已有的两个输出在模拟与 HDMI 之间切，并在 `finally` 里切回原值）。

写测试时踩到并修掉的：助手用 `GCHandle` 传回调的上下文，释放句柄后原生代码仍可能回调——**use-after-free，
整个测试进程直接消失且没有任何信息**；助手没有等上下文进入就绪就去加载模块，于是错误地得出「这台机器的
声音服务不支持 null sink」。以及第一版测试为了有个第二输出**加载了 null sink 模块，两次在测试中途挂掉时
把它留在了机器上**——改成用已有的两个输出，不再改动用户的声音配置。
### P10 — 录制 ✅ / 被控端宿主 ✅ / 打包 ⏳

**被控端现在能在 Linux 上跑起来了**，宿主装配这一段补完了（原先 Client 与 HostService 都还硬编码
Windows，平台层其实没有宿主）。实测：

```
[ERR] QUIC is not supported on this system; incoming connections are disabled.   ← 第一次没带 LD_LIBRARY_PATH
[WRN] Rendezvous connection lost: Unable to connect to the remote server.        ← 没起服务端，按退避重试
```

带上 `LD_LIBRARY_PATH` 之后 QUIC 那条消失；控制 socket 出现在
`$XDG_RUNTIME_DIR/remotedesktop/RemoteDesktop.HostControl.sock` 并且**能接受连接**（目录是 0700，
socket 本身受目录保护）。

装配上最大的差别是**没有 agent**：Windows 的特权服务在 Session 0，看不见桌面，所以画面与输入在它监管的
session agent 里；这里没有 Session 0，Wayland 的 portal 只在该会话内应答，所以宿主以登录用户身份跑在图形
会话里，**它自己就是采集和注入的那一方**。`IAgentGateway` 不注册，四个 `Add…Feature()` 直接上。

两个为让宿主能装配而补的端口（`WaylandClipboard`、`WaylandCameraEnumerator`）都在上面各自的小节里
说明了自己不做什么。

**打包还差最后一步**，而且不再是"被挡住"，是"还没做"：`build/debian/host/`（`.deb` + systemd
**用户**单元、`WantedBy=graphical-session.target`、`PartOf=`、`Restart=on-failure`）与 `.desktop` 文件。
形状可以直接沿用 `build/debian/server/`。

### 端到端已经跑通 ✅

`SessionEndToEndTests` 把真实的被控端、真实的控制器和真实的 rendezvous（loopback）凑到一起，走完整的
链路。实测输出：

```
host is device 838004535, password t8saruyidhck
connected over Direct
the controller can see 1 display(s)
moved the pointer to the top-left corner through the session
THE PICTURE CHANGED — input reached the screen through the session
[h264_vaapi] No usable encoding entrypoint found for profile VAProfileH264Main (6).
```

也就是说：信令 → QUIC 直连 → 口令握手 → 会话授权 → **portal 采集真实屏幕 → x264 编码 → 传输 → 解码**，
并且**反向**：控制器注入 → libei → 真实指针移动 → 画面变化被解码验证。顺带确认了硬件编码器探测在
这块没有 VCE 的 GPU 上**干净地回落到软件**（正是风险表里本机作为试验台的那条）。

有一条容易被忽略但很要命的细节：端到端的用例必须把自己数据目录里的 restore token 从**已授权**的那个
目录拷过去。授权是**按数据目录**记的，测试用自己的空目录就会卡在一个没人应答的对话框上，表现成超时。
第一版就是这么挂的。

**顺带修掉一个真 race**：`CallForFdAsync` 原本在 `CallMethodAsync` **返回之后**才 dup 那个 fd，而 fd 属于
message、message 在读取委托返回后就被释放——于是偶尔 dup 到一个已经关掉的编号，报 EBADF，而连接本身好
好的。平时不显形，直到测试并行度上来。现在 dup 在读取委托**内部**做，与 `VariantValue`、`Reader` 那两条
是同一类错误：**值必须在它还活着的时候取出来**。

### P10（原始计划） — 录制 + 被控端打包
### P11 — 服务端原生部署 ✅ 已完成（Linux 侧）

**证书在 Windows 之外的形态。** Windows 把证书放进机器证书存储、只把公钥写出来，理由是安装目录人人可读；那套逻辑在这里整个反过来——没有存储可用，**私钥只能是一个文件**，于是文件权限就是私钥与其他人之间唯一的屏障，所以它必须在相信那一点之前先把自己设成 0600。证书本身用的是同一段代码：生成从来不是平台相关的部分，保存才是。

`ServerSetup` 相应分成两支：Windows 写 `Subject`/`Store`/`Location`/`AllowInvalid`，Linux 写 `Path` + `KeyPath`。切过去之前会把两种形态的键都清掉，否则上一次运行留下的一种会压过这次生成的另一种。另外补上了同级私钥的约定：`signaling.crt` 旁边有 `signaling.key` 时自动用 PEM 形态——这是系统上其它东西都遵守的约定，已有证书对的运维不需要额外配置。

**systemd：`AddSystemd()` / `UseSystemd()`，只在 Linux 上注册。** 两个调用在别的平台都是空操作，但登记一个本就不可能适用的管理器，会让下一个读这段代码的人理解错。

**`--configure-system` 本来就是跨平台的**——检查过，`SetupCommands` 里没有任何 Windows 专有代码。所以 `postinst` 调的就是 Windows 安装器调的那一步，两个平台由同一段代码产出同样的文件。这正是当初把它写成纯 .NET 的好处。

**打包**：`build/debian/server/` 出一个 `.deb`，两个服务器装在一起（它们共享一把钥匙、分开放只会多一个能装错的步骤）。程序 self-contained 发布，配置在 `/etc/remotedesktop-server`（**服务的 working directory 就是那里**——.NET 从启动目录读 `appsettings.json`，所以启动在 `/etc` 就等于配置在 `/etc`，而升级只换程序、不碰运维改过的配置）。服务以专用账号运行，`ProtectSystem=strict` 之类能开的都开着。

**计划里的一处被改掉：不用 debconf，用 `/etc/default/remotedesktop-server`。** debconf 是 MSI 向导在 Debian 上最接近的对应物，但它为交互式安装设计；服务器需要的是**无人值守也能装**，而 `/etc/default/<pkg>` 是 Debian 服务配置的标准做法：幂等、可脚本化、是 conffile 所以运维的修改能跨升级保留。`postinst` 从它读设置再调 `--configure-system`。

**防火墙默认不动**。`RD_OPEN_FIREWALL=1` 且 ufw 处于 active 时才加规则；不直接改 nftables/iptables——包去改一台机器的防火墙是在替管理员做决定。

验证（把包解到临时目录、用真实二进制走完整条链路）：`--configure-system` 生成两张 PEM 与两个 appsettings（`Path`/`KeyPath`，无存储键）；**私钥 0600、证书 0644**；服务端真的起来并在 TLS 上应答 `/health` 返回 `Healthy`；证书校验三态都对——**信任且名字匹配 → 200**、不信任 → 拒绝、信任但名字不在证书里 → 拒绝。

### P11（原始计划） — 服务端原生部署
### P12 — 客户端打包 + 文档 ⏳ 客户端已能在 Linux 上运行

---

## 5. 服务端在 Linux 上怎么部署

Windows 的 MSI 向导在 Linux 上最接近的对应物是 **debconf + 包脚本**。

| Windows MSI 做的事 | Linux 对应 |
| --- | --- |
| 装到 `%ProgramFiles%\Remote Desktop Server\{Rendezvous,Relay}` | `/usr/lib/remotedesktop-server/{Rendezvous,Relay}`（self-contained publish） |
| `appsettings.json` 写在安装目录里 | **写在 `/etc/remotedesktop-server/`**——FHS 正确，升级不会覆盖用户配置 |
| 注册两个 Windows 服务（auto、失败重启、1 天重置） | 两个 **system** systemd 单元（`Restart=always`、`StartLimitIntervalSec=0`） |
| 三条防火墙规则 5080/tcp、5081/udp、5082/udp | debconf 问一句；`postinst` **只在 ufw 活跃时才加**，不动 nftables/iptables |
| 向导页收集 DB / 中继地址 / 共享密钥 / TLS | debconf 高优先级问题，字段一一对应 |
| `--configure-system` 合并写 appsettings 并记 `server-setup.log` | **原样复用**：`ServerSetup.Configure` 是纯 .NET，`postinst` 直接调 |
| `configure-server.ps1` 装后重配 | `/usr/bin/remotedesktop-configure-server` + `dpkg-reconfigure` |
| `generate` 自签名证书 → **Windows 证书存储** | **必须改**：写 PEM 到 `/etc/remotedesktop-server/tls/`（私钥 0600），Kestrel 用 `Path` + `KeyPath` |
| 把 `signaling.cer` 交给客户端导入受信任根 | `cp signaling.crt /usr/local/share/ca-certificates/ && update-ca-certificates` |

配置面完整清单：Rendezvous 是 `Urls`、`Rendezvous:{ReflectorPort=5081, RelayEndpoint, RelaySharedKey,
PostgresConnectionString, RelayTicketLifetime, ConnectTimeout, TrustedProxies, MetricsEnabled, …}`、
`Kestrel:Certificates:Default`；Relay 是 `Relay:{Port=5082, SharedKey, IdleTimeout}`。

两个必须讲清楚的约束：

- `RelayEndpoint` **必须是 `IP:port`，不能是域名**（`RendezvousOptions.Validate()` 会拒），
  域名只在装定时解析一次并写死。
- `RelaySharedKey` / `Relay:SharedKey` 必须是 base64 且 ≥32 字节，两端**必须一致**，否则中继拒绝所有
  票据、客户端只看到「连不上」。生成：`openssl rand -base64 48`。

**数据库是软依赖**：配了就持久化「设备证书指纹 → 9 位设备码」的映射，无人值守的设备码才能跨重启稳定；
不配则内存模式，**每次重启所有设备码都变**。服务端只建表不建库（`CREATE TABLE IF NOT EXISTS`，
裸 Npgsql，无 EF Core），库必须用户先建好。配了之后 `/health` 会带 postgres 检查。

---

## 6. 打包要点

- **`.deb` 的依赖必须手写**。`dh_shlibdeps` 从 ELF `NEEDED` 推导，而我们的库是经 `DllImport` 到达的，
  **扫描器看不见**。漏写的结果是包安装干净、首次使用 `DllNotFoundException`。
- **`libavcodec-extra62` 是最大的「在我机器上能跑」陷阱**：Debian/Ubuntu 把含专利的编码器放在
  **`-extra`** 包里，干净的 Ubuntu 上 `avcodec_find_encoder_by_name("libx264")` **返回 NULL**。
  本机有 `libx264-165` 只是因为装了 `ffmpeg`。必须 `Depends: libavcodec-extra62 | libavcodec62`，
  同时工厂的 `IsSupported` 要返回 false 并给出可操作的提示。
- **systemd 用户单元**必须有 `WantedBy=graphical-session.target`、`After=`、`PartOf=`、
  `Restart=on-failure`；**portal 会话必须在首个控制器连接时惰性建立，绝不在启动时**——否则服务会比合成器
  先启动，然后卡在等一个还不存在的会话上。

---

## 7. Linux 上不会工作的事

1. **Ctrl+Alt+Del**（`SendSecureAttentionSequence`）——Linux 无对应概念。抛 `NotSupportedException`，
   `InputChannelModule` 已经捕获并记录。
2. **登录界面 / 登录前远程访问**——GNOME greeter 是另一个用户、另一个 VT，私有用户会话看不见也驱动不了。
   Windows 的 `DesktopKind.Secure` + agent 替换机制**没有对应物**。**这是相对 Windows 的真实能力回退。**
3. **快速用户切换 / 多用户**——一个守护进程、一个用户、一个会话。
4. **隐私屏**（`IPrivacyScreen`）——Windows 上也没实现。
5. **uinput 后备路径下的 `TextInputEvent`**——任意 Unicode 无键码路径（EIS 下经 `NotifyKeyboardKeysym`
   可用）。这是 EIS 作主路径的又一条理由。
6. **H.265**——Windows 上也没实现。
7. **部分合成器下的指针形状**——见 §2.6。
8. **X11 会话**——本期不做（不过 portal 路径在 X11 下大概率也能用，不作承诺）。

**一个意外的好消息**：polkit 提权提示渲染在**用户会话内**，因此与 Windows 的安全桌面不同，
**它是能被采集到的**。

---

## 8. 风险

| 风险 | 缓解 |
| --- | --- |
| portal 会话状态机繁琐（先订阅后调用、request handle、`Start` 以 variant 返回 streams、restore token、合成器重启后 PipeWire fd 失效） | P3 先用 shell spike 跑通；持久化 restore token；把「会话失效」当**正常状态**而非异常 |
| 光标元数据可能不送达（依赖合成器） | 配置开关 + 检测降级 + 宁可抑制客户端光标也不要重影 |
| PipeWire stream 的 `position`/`size` 是逻辑还是物理未验证 | P3 spike 实测；用本机数字（2560×1440 物理 / 1920×1080 逻辑 / scale 4/3）写单测钉住 |
| libavcodec ABI 跨发行版漂移 | 带主版本 soname + resolver + 启动自检 + 大声拒绝。能用 `av_opt_set*` 就用，只碰自 FFmpeg 2.x 起就在结构体头部的字段 |
| **本机没有能编码的 GPU，硬件编码路径无法在本机验证** | 本机能验证软件路径、VA-API 探测、以及「硬件工厂报告支持但 `Create` 失败→回落软件」的完整降级链。**真硬件编码需要一台 AMD Polaris/VCN+、Intel Gen9+ 或 NVIDIA 的机器** |
| 多 GPU 时选错 render node / 索引跨重启不稳定 | 见 §2.8，逐个探测，绝不假设索引 |
| 发行版剥离 H.264 编码（Fedora 的 Mesa、Intel 的 `-non-free` 包） | 一律靠**运行时 entrypoint 探测**决定，不维护硬编码支持列表；日志里打 `vaQueryVendorString` |
| portal 授权在部分系统/升级后需重授 | 持久化 restore token；文档说明可能重弹 |
| `ValidateOnBuild = true` 遇上还没实现的端口 | 端口按阶段注册（P1 起就是这个做法），不铺会抛异常的桩 |
| Avalonia 在 Linux 上默认走 X11 后端 | 明确接受：XWayland 下客户端是普通 X11 程序，输入按焦点窗口走，对本产品够用。**不要让人误以为客户端跑在原生 Wayland 上** |
