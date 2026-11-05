# 平台移植总览

给**要把产品搬到某个平台的人**，以及**要改平台相关代码的人**：两个移植各定下了什么、每个平台有哪些
绕不过去的硬约束、哪些地方还没验证过。

这两个移植**都已完成**（阶段状态见表），所以这份文档不再承担"计划"的职责，它记的是**结论**：
哪条路走通了、哪条路被平台挡住了、以及踩过哪些"编得过、跑起来才炸"的坑。

> **实测过程不在这里。** 实测用的命令、输出和逐条探针记录不在文档里——
> 需要复现某条结论时，去 git 历史里找 `docs/ports/macos-port-plan.md` 与 `docs/ports/linux-port-plan.md`
> （合并成本文件之前的两份移植计划）的最后一版：它们在提交 `2b01997` 中被删除，
> 用 `git show 2b01997^:docs/ports/macos-port-plan.md`（Linux 那份同理）即可取出。这里只留结论和它成立的理由。
> 功能与平台能力对照见 [使用说明](../usage/README.md)。

## 1. 三平台对照

| | Windows | macOS (Apple Silicon) | Linux (Wayland) |
|---|---|---|---|
| 平台项目 | `Platform.Windows` | `Platform.MacOS` | `Platform.Linux`（与 macOS 共享 `Platform.Posix`） |
| 进程模型 | LocalSystem 服务 + 用户会话 Agent | **单进程**，`.app` 自己是被控端 | **单进程**，`systemd --user` 服务 |
| 屏幕 | DXGI Desktop Duplication（失败时回退 GDI） | ScreenCaptureKit | portal + PipeWire |
| 输入 | `SendInput` / `SendSAS` | CGEvent | libei（EIS） |
| 编解码 | Media Foundation | VideoToolbox | libavcodec（NVENC / VA-API / x264） |
| 音频 | WASAPI 环回 | ScreenCaptureKit 环回 + AudioQueue | PulseAudio |
| 摄像头 | Media Foundation | AVFoundation | portal 摄像头节点 + PipeWire |
| 录制 | Media Foundation（`MfVideoRecorder`） | AVAssetWriter | libavcodec（`FfmpegVideoRecorder`） |
| 终端 | ConPTY | PTY（`posix_spawn`，共享 Posix 层） | PTY（同一份共享实现） |
| 密钥 | DPAPI | Keychain | 0600 文件 |
| 防休眠 | `SetThreadExecutionState` | IOKit 电源断言 | logind `Inhibit` |
| 登录前 / 安全桌面 | 支持（换 Agent 接管） | **不支持** | **不支持** |
| 无人值守 | 服务 + 固定密码，登录前也在线 | `SMAppService`（client）或 LaunchAgent（host 包）+ 固定密码（仅登录后） | **控制无法无人值守**（portal 规定，见 §4） |
| 打包 | MSI（WiX v5） | `.app` + `.pkg` | `.deb` + systemd |
| 原生代码 | 无（纯 P/Invoke） | ObjC 辅助库 `native/macos` | 无（纯 P/Invoke） |

产品形态为什么不同、每个平台不支持什么，见 [使用说明](../usage/README.md) §1。

## 2. 两个移植共用的做法

这几条是被两个平台各自验证过一遍的，新做平台时照抄即可：

- **不新建宿主项目，把 HostService 通用化。** 架构测试按前缀从输出目录加载程序集，而
  `HostService` / `SessionAgent` / `Client` 只出现在**禁止**数组里、从不作为层被加载——新建一个
  `RemoteDesktop.Host.Linux` 等于**不被任何规则覆盖**。而且一个 OS 一个二进制这件事被 `.deb`、systemd unit、
  `.desktop`、发布流水线到处写着。做法是在宿主里按平台分支注册。
- **语义相同的部分抽到 `Platform.Posix`，不要各写一份。** 两个移植里真正值得共享的是 PTY：
  `posix_openpt` / `grantpt` / `unlockpt`、`posix_spawn` + file actions、会话组长、按**进程组**发信号而不是
  发给 shell 本身、argv 封送、dispose 语义——这些在 macOS 与 Linux 上完全相同，
  不同的只有常量与入口点。0600 文件密钥库与 Unix 用户上下文守卫同理。
  `Platform.Linux → Platform.Posix` 不违反分层规则（规则表不含 `Platform` 自身，一行未改）。
- **原生互操作保留 `net10.0` 单一 TFM，手写 P/Invoke。** 不用 `net10.0-macos` / `net10.0-linux` TFM，
  不引入绑定生成器。只有 macOS 的 ObjC-only 框架额外需要一个小的辅助库暴露扁平 C ABI（见 §3）。
- **架构规则会自动覆盖新平台**：`DependencyRuleTests` 按前缀加载程序集
  （`LoadAssemblies("RemoteDesktop.Platform")`），新增 `Platform.X` 无需改测试。
- **端口按阶段逐个注册，不预先铺桩。** 每个端口都要先是真实现才有意义——构造函数抛异常的桩只会把
  "这个功能还没做"变成"宿主起不来"。
- **`--self-check` 是每个平台都要有的东西。** 原生子系统多数经 `DllImport` 接入，失败形式常常是没有任何
  上下文的 `DllNotFoundException` 或一个永不返回的调用，而**发现这件事的地方是已经坏了的那台机器**。
  自检要探到能力本身，而不只是列出"端口已注册"。各平台现状不同：macOS 的自检自己走一遍真实读写 / 真帧 / 真注入；
  Linux 做只读的能力探测（会话、portal、PipeWire、libei、libavcodec 与可用编码器），不弹窗、不开始会话；
  Windows 只报告数据目录和传输层。

## 3. macOS (Apple Silicon)

### 已确认的决策

| 决策点 | 选择 |
|---|---|
| 原生互操作方式 | **混合方案 C**：`net10.0` + C 框架 P/Invoke + 小型 ObjC 辅助库 |
| 交付范围 | 全功能一次到位（P1–P10） |
| 签名与分发 | 开发期 Apple Development 签名；发布走 Developer ID + 公证（脚本已实现，载荷逐个签名；公证的验证状态见 §5） |
| 无人值守 | `SMAppService`（client）或 LaunchAgent（host 包）；新增 `ILoginItem` 端口 |
| 最低系统版本 | **macOS 26 (Tahoe)**（成因见下方硬约束） |

### 硬约束与结论

**最低系统版本是 macOS 26。** 由随包分发的 `libmsquic` / `openssl@4` bottle 记录的 `minos 26.0` 决定，
dyld 拒绝在更老的系统上加载它们。要降低下限只能改走"从源码构建 MsQuic"，
或者让用户自己 `brew install libmsquic`（那就不能开箱即用）。打包脚本与 CI 都按这个下限写。

**进程模型：没有 Session 0，"服务 + Agent"模型不成立。** 以 root 运行的 LaunchDaemon 拿不到屏幕录制权限，
所以 Windows 那套拆分在 macOS 上无法成立。被控端 = 运行在用户图形会话里的一个**已签名 `.app`**：
client 包是完整的 `Remote Desktop.app`（自带托管能力），host 包是独立的 `Remote Desktop Host.app`
（`LSUIElement`，无窗口、无控制端，就是 Linux 那个 `HostService` 二进制；装在
`Library/Application Support`，不出现在“应用程序”）。
开机自启：client 用 `SMAppService`，host 包用 `/Library/LaunchAgents` 的 LaunchAgent。两者都只在用户图形登录后生效，这直接导致「登录界面 / 登录前访问」没有对应物。

**TCC 授权按签名身份记账**，这是 macOS 独有的一条：屏幕录制、辅助功能、摄像头授权都记在**签名身份**上，
换了签名（哪怕同一份代码）就要重新授权一次。所以**打包脚本坚持用稳定身份签名**——开发期也必须用
一个固定的 Apple Development 身份，否则 TCC 权限反复丢失。开发调试时注意：**从被沙箱包裹的终端里跑
`.app`，读到的权限结论全是假的**，验证 TCC 必须走正常启动（Finder 双击，或一个没有沙箱的终端）。

**设备身份证书必须显式可导出。** `DeviceIdentity.CreatePfx()` 走的是"自建 ECDSA → 导出 PFX → 重新加载"
的往返（Windows 上是为 SChannel 所需）。在 macOS 上默认加载方式会把私钥存进钥匙串且**不可导出**，
MsQuic 导出凭证时直接抛异常。修法是加载 PKCS#12 时加 `X509KeyStorageFlags.Exportable`（3 行以内）。

**Keychain 的 `OSStatus 100001` 是 `EPERM`，不是缺 entitlement。** 这条很容易被误判成别的原因，所以留在这里：
`100001` = `EPERM`（缺 entitlement 是 `-34018`），成因是**调用方进程被沙箱包裹**，与代码签名、
entitlement、`.app` 形态都无关。**钥匙串不需要稳定签名身份**；需要稳定身份的只有 TCC。
两者真正的关系只剩一条：读回"以前由另一个签名创建过的条目"时系统会按"这是另一个程序"要求授权——
那是体验问题，不是能不能用的问题。

**采集请求的是像素尺寸，不是点尺寸。** 主显示器 `CGDisplayBounds` 是 1352×878 **点**，
`CGDisplayCopyDisplayMode` 是 2704×1756 **像素**。按点尺寸请求会在 Retina 上静默地只拿到一半分辨率。

**指针形状的指纹每帧都会变。** `CGContextDrawImage` 把 `NSCursor` 画进缓冲区时，**完全透明像素的 RGB
是未定义的**，复用同一块缓冲区就会每次画出的字节都不同。所以"比较两帧是否相同"这类测试不能直接比指针位图，
否则会得到一个每帧都在变的假指纹。

**`[LibraryImport]` 会把 span 当裸指针传——迁移到源生成 marshalling 时的一个新坑。**
`[DllImport]` 时代只能传 `byte[]`，marshaller 会同时给出长度；换成 `[LibraryImport]` + span 之后，
长度信息**静默消失**，原生侧收到的 `length` 形参实际是紧随其后的另一个缓冲区指针。
症状是崩溃在 `memmove` 里，而且**"编得过、签得过、符号全在、C 探针也过"四条全绿也照样发生**——
唯一能发现它的是从托管侧真的调一次。凡是"缓冲区 + 长度"的原生签名，迁移时都要逐个复查。
（同一个调用里还发现 `Marshal.GetLastPInvokeError()` 在 `stackalloc` 之后返回 0，别指望用它读错误码。）

**`open --args` 送不到参数。** 通过 `open -n -W <app> --args a b` 启动时，参数**到不了**进程
（进程确实起来了，走的是正常启动路径）。四种调用形式（相对路径、绝对路径、`-b bundle id`、`-a`）实测一致。
所以脚本里要传参数就直接执行 `Contents/MacOS/RemoteDesktop.Client`，不要经 `open`。

**VideoToolbox：线上是 Annex-B，工具链给的是 AVCC，转换层必须先钉死。** `VTCompressionSession` 给出的
每个 NAL 是 4 字节**大端长度前缀**（AVCC），而本项目线上封装是 Annex-B（`00 00 00 01` 起手码）。
另外本机同时存在硬件与软件 H.264 编码器，所以 Windows 的"双 `IVideoEncoderFactory`（硬件 + 软件）"形状
在 macOS 上自然成立，不是为对称硬凑。**编解码器必须给一整块正确的 `CVPixelBuffer`**：
把双平面 NV12 硬包成单平面缓冲会让 plane 1 指针是 NULL，编码器内部 memmove 直接 SIGSEGV——
症状是段错误而不是报错。

**音频用 ScreenCaptureKit 环回，不新增麦克风权限。** 它复用的是屏幕录制那一次授权，语义与端口契约
（"捕获主机正在播放的声音"）完全对上；原生侧 `excludesCurrentProcessAudio = YES`，避免把远端的语音环回去。
采集固定 48 kHz 立体声 float32 交织，与线上格式一致，**不做重采样**。

**ObjC 侧有几条与直觉相反的规矩**（都踩过）：`codesign` 把 `Contents/MacOS` 里的**每个文件**都当嵌套代码；
C 函数读不到 `@property` 自动合成的实例变量（`instance variable is private`）；
`AVCaptureSessionPresetInputPriority` 是 iOS/macCatalyst 专用（`API_UNAVAILABLE(macos)`）；
托管侧的结构体必须与 C 端**逐字段对齐**，漏一个字段不会崩，只会让 `HasFrame` 永远读到 0。

**文件传输里有两处曾假设"被控端就是 Windows"**，macOS 让它们暴露出来，值得记住：
`RemoteFileSource.Combine` 不能硬编码 `\`（**分隔符属于被控端**）；`FileAccessPolicy` 的允许根与入参
必须用**同一种链接解析**方式（macOS 上 `/var` 是 `/private/var` 的符号链接，而所有临时目录都在 `/var` 下）。

### 明确不做的范围

| 项 | 原因 |
|---|---|
| 登录界面 / 锁屏 / 安全桌面截取 | macOS 不允许第三方应用截取登录窗口 |
| Ctrl+Alt+Del（SAS） | macOS 无此机制 |
| LocalSystem 系统服务模型 | 无 Session 0，且守护进程拿不到 TCC 权限 |
| 隐私屏（`IPrivacyScreen`） | macOS 无对应公开 API；Windows 侧亦未实现 |
| 免提权控制其他用户的会话 | macOS 是单用户图形会话模型 |

### 阶段状态

| 阶段 | 结果 |
|---|---|
| P1 平台骨架与工具链 | ✅ |
| P2 密钥与设备身份（Keychain） | ✅ 有单测（`tests/RemoteDesktop.Platform.MacOS.UnitTests`） |
| P3 屏幕采集 + 输入注入 | ✅ ScreenCaptureKit + CGEvent，`--self-check` 有真帧与真注入探针 |
| P4 视频编解码（VideoToolbox） | ✅ 端到端探针通过，有单测（`VideoCodecTests`） |
| P5 剪贴板与防休眠 | ✅ NSPasteboard + IOKit 电源断言 |
| P6 远程终端（PTY） | ✅ 落在共享 `Platform.Posix`，不用 `forkpty` |
| P7 文件传输适配 | ✅ 两个真 bug 已修；受保护目录的 TCC 行为已联调 |
| P8 远程摄像头（AVFoundation） | ✅ 含"能注册 ≠ 能用"的真帧探针 |
| P9 音频采集与播放 | ✅ ScreenCaptureKit 环回 + AudioQueue |
| P10 录制、打包与无人值守 | ✅ AVAssetWriter + `.app` / `.pkg` + `SMAppService` |

## 4. Linux (Wayland)

### 已确认的决策

| 决策点 | 选择 |
|---|---|
| 交付范围 | 被控端 + Avalonia 客户端 + `Server.*` 原生部署，三段全做 |
| 采集与注入 | **Wayland 优先**（portal + PipeWire），X11 不做承诺 |
| 打包 | 客户端与被控端 `.deb` + systemd **用户**服务；服务端 `.deb` + systemd 系统单元 + debconf |
| 视频编解码 | 硬件与软件**都经 libavcodec 一把桥**，不手绑 VA-API / NVENC |
| POSIX 共享 | 新建 `Platform.Posix`，与 macOS 共用语义相同的部分 |
| 输入注入 | **只用 EIS（libei）**；`/dev/uinput` 评估后弃用，没有实现 |

### 硬约束与结论

**单进程，不要 Agent。** 理由与 macOS 相同但成因不同：portal **只在该会话内应答**，所以被控端是一个跑在
图形会话里的 `systemd --user` 服务，自己就是采集和注入的那一方。这也意味着「登录界面 / 登录前访问」
没有对应物——GNOME greeter 是另一个用户、另一个 VT，私有用户会话既看不见也驱动不了。

**显示枚举可以免授权。** 走合成器自己的 D-Bus 接口（GNOME 上是 Mutter 的 `DisplayConfig`）
即返回完整布局，**无需 portal、无需授权**。所以"列出显示器"这件事不需要弹窗。

**光标必须用 `Metadata` 模式，而且本机收不到元数据。** `cursor_mode` 必须选 `Metadata`（值是 4，
**不是 2**——2 是 `Embedded`）：抽象接口写死了指针由客户端自己画，选 `Embedded` 会把指针烧进像素里，
客户端再画一个就成了重影。但实测本机**始终收不到 `SPA_META_Cursor`**（授权成功，`AvailableCursorModes`
含 Metadata），所以被控端会警告一次然后照常出帧，控制端只能画自己最后知道的那个指针。
降级检测做了（连续 10 帧没有 cursor meta 就警告一次），**配置开关没做**——宁可抑制客户端光标
也不要重影。

**输入注入用 EIS，`/dev/uinput` 是坏账。** 加入可写 `/dev/uinput` 的组意味着"可随时、无需同意地向本机
任何会话注入输入"。为了屏幕共享而让 `.deb` 授权这个不值得，所以**既没有 uinput 后备实现，包里也没有 udev 规则，
也不会自动把用户加进任何组**。

**一次授权管两件事被 portal 拒绝了。** 采集和控制不能放在同一个会话上、一次授权两用；实测报
`org.freedesktop.portal.Error.InvalidArgument: Remote desktop sessions cannot persist`——
**远程桌面会话不允许持久化**。这是本平台最影响使用的一条：共享屏幕授权一次就记住了，
**控制屏幕每次连接都要重新授权**。所以一台无人值守的 Linux 机器可以*被观看*，但没人坐在跟前就没法*被操作*。
反过来有一条好处：**polkit 的提权提示渲染在用户会话里**，所以它和 Windows 的安全桌面不同，**能被采集到**。

**采集侧两个只表现为一句没头没尾错误的坑**：`pw_context_connect_fd` 返回时 core 还没绑定完，
此时带 `AUTOCONNECT` 去查目标节点会得到 `no target node available`（节点下一刻就在）——正确做法是
先等目标节点出现（注册表监听 + 5 秒超时），而不是等一个固定时长；portal 的 `node_id` 与流要指名的
`object.serial` **不是同一个数**。

**编解码器设备要自己枚举，绝不假设 `renderD128` 就是编码器**（索引跨重启与 GPU 热插拔都不稳定）。
做法是枚举 `/sys/class/drm/renderD*`，读 `device/vendor` 与 `device/driver`，对每个节点用
`av_hwdevice_ctx_create` 建设备并试开 libavcodec 的 VA-API 编码器，能打开的才算可用。不直接问 libva
支持什么——硬件与软件编码都只经 libavcodec 一把桥。

**MsQuic 在 Linux 上要自己弄到。** Windows 由运行时包提供 `msquic.dll`，**Linux 没有**，Ubuntu 仓库也没有。
缺它时每个开 QUIC 连接的测试都失败并报 `QUIC is not supported on this system`——看着像平台不支持，
其实是缺库。`.deb` 里随包分发；**开发时是框架依赖运行，库不在可执行文件旁，要么把 `libmsquic` 装到系统里，要么让 `LD_LIBRARY_PATH` 指向它**（自包含发布不需要）。

**密钥是 0600 文件，服务端证书的私钥也只能是文件。** Windows 把证书放进机器证书存储、只导出公钥；
Linux 没有存储可用，**私钥只能是一个文件**，于是文件权限就是私钥与其他人之间唯一的屏障，
所以它必须在相信那一点之前先把自己设成 0600。生成证书那一段是共用代码，**保存方式才是平台相关的部分**。

**宿主控制通道在 Unix 上是共享目录里的命名管道。** `PipeHostControl` 用 `NamedPipeServerStream`，
.NET 在 Unix 上把它映射到 `$TMPDIR/.dotnet/corefx/pipe/`（一个**共享**目录，谁先建谁以 0700 拥有，
同机两个用户会互相干扰）；`WindowsPipes.CreateServer` 用的 `PipeSecurity` / SID 在 Unix 上不存在。

**XDG 目录不能被 `SpecialFolder` 取代。** 中文桌面上 `SpecialFolder` 答的 `~/Desktop` **根本不存在**，
必须读 `~/.config/user-dirs.dirs`（`XdgUserDirectories`）。另外 `ListDrives` 在 Unix 上按 `DriveFormat`
过滤掉容器 / loop / snap 挂载，并且**不去问网络挂载有多大**——问一次 NFS 就是等一次服务器，
而这是在画列表的线程上。

**打包要点**：`.deb` 的依赖**必须手写**——`dh_shlibdeps` 从 ELF `NEEDED` 推导，而我们的库是经 `DllImport`
到达的，**扫描器看不见**，漏写的结果是包装得干干净净、首次使用 `DllNotFoundException`。
（含专利的编码器在 `-extra` 包里这一顾虑，实测是多余的：`libavcodec62` 自己就依赖 `libx264`。）

### 明确不做的范围

1. **Ctrl+Alt+Del**（`SendSecureAttentionSequence`）——无对应概念，抛 `NotSupportedException`，
   `InputChannelModule` 已捕获并记录。
2. **登录界面 / 登录前远程访问**——Windows 的 `DesktopKind.Secure` + Agent 替换机制**没有对应物**。
   **这是相对 Windows 的真实能力回退。**
3. **快速用户切换 / 多用户**——一个守护进程、一个用户、一个会话。
4. **隐私屏**——Windows 上也没实现。

### 阶段状态

| 阶段 | 结果 |
|---|---|
| P1 平台骨架、构建、CI | ✅ |
| `--self-check`（补做） | ✅ |
| P2 密钥、身份、传输、guard、防休眠（D-Bus 桥） | ✅ |
| P3 Wayland 采集与输入 | ✅ portal 会话流程 + libei 注入都在真机跑通 |
| P4 视频编解码 | ✅ 三个工厂都走 libavcodec（**硬件路径待真 GPU 机器验证**） |
| P5 剪贴板 | ✅ portal 路径可用，两个方向都端到端验证过 |
| P6 终端（PTY 落在共享 `Platform.Posix`） | ✅ |
| P7 文件传输 | ✅ XDG 目录 + 挂载点过滤 |
| P8 摄像头（PipeWire 节点，不是 V4L2） | ✅ 真机验证（Ubuntu 26.04 + 4K USB 摄像头，MJPEG/YUY2 → NV12 → NVENC） |
| P9 音频 | ✅ PulseAudio（轮询而非回调） |
| P10 录制 / 被控端宿主 / 打包 | ✅ |
| P11 服务端原生部署 | ✅ Linux 侧 |
| P12 客户端打包 / `release.yml` / 文档 | ✅ |

## 5. 未验证与已知缺口

按"先说不会工作的部分"的规矩列在这里，**未验证不等于能用**：

| 缺口 | 状态 |
|---|---|
| **Linux 摄像头的授权** | 采集链路已在 Ubuntu 26.04 + 4K USB 摄像头上验证；但后台宿主无法弹出 portal 的摄像头授权框（GNOME 只允许焦点应用弹），需要显式允许一次：客户端「高级设置 → 安全 → 摄像头权限」或 `RemoteDesktop.HostService --camera-access allow` |
| Linux 硬件编码 | NVENC 已在 NVIDIA RTX A4000（驱动 595）上端到端验证（摄像头与桌面流）；软件 x264 已验证；VA-API 仍未在真硬件上跑过 |
| Linux X11 会话 | 只在 Wayland 上验证过，portal 路径在 X11 下大概率能用，但不作承诺 |
| Linux 光标元数据 | 本机收不到 `SPA_META_Cursor`，见 §4 |
| macOS 公证（notarization） | 打包脚本已补：`.app`、Installer 面板与 server 载荷的 Mach-O 逐个 Developer ID 签名，`--notarize` 提交并 staple；尚未用真实证书与公证凭据实跑 |
| 长时间对抗性测试 | 握手洪泛上限、限流窗口这些数字按常见情况选的，没在真实攻击流量下压过 |

## 6. 风险与缓解

| 风险 | 缓解 |
|---|---|
| portal 会话状态机繁琐（先订阅后调用、request handle、`Start` 以 variant 返回 streams、restore token、合成器重启后 PipeWire fd 失效） | 先用 shell spike 跑通；持久化 restore token；把"会话失效"当**正常状态**而不是异常 |
| TCC 权限在开发期反复丢失 | 第一天就用 `.app` + 稳定 Apple Development 签名；不用沙箱环境验证权限 |
| ScreenCaptureKit 的 ObjC 互操作复杂度 | 用小型 ObjC 辅助库收敛，只暴露扁平 C ABI；新功能先做最小 PoC 再展开 |
| "能注册 ≠ 能用" | 每加一层都配一条**真帧 / 真注入 / 真读写**探针；macOS 的 `--self-check` 会自己走一遍，Linux 的只做能力探测 |
| 迁移到 `[LibraryImport]` 时静默丢失长度信息 | 凡是"缓冲区 + 长度"的原生签名逐个复查，并从托管侧真的调一次 |
