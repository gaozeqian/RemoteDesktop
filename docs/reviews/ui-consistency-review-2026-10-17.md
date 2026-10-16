# 客户端 UI 跨平台一致性评审（2026-10-17）

> **历史快照 · 已冻结** — 这是 2026-10-17 对客户端 UI 在 Windows / macOS / Linux 三个平台上是否一致的评审记录，
> **不再更新**。评审里写的修复建议**不是现行约定**；此后如需知道"现在应该是什么样"，以
> [`docs/usage/README.md`](../usage/README.md)（支持矩阵）与代码为准。归档说明见
> [`docs/reviews/README.md`](README.md)。
>
> 评审对象：`master` @ `baa33cc`（2026-10-17 06:33 +0800）
> 评审日期：2026-10-17
> 结论：**界面是三平台共享的单份代码——视图、样式、主题均无平台分支；实际差异全部由"平台能力与托管模型"驱动。
> 但其中三处说明文案把 macOS 的说法固化给了 Linux（U1–U3），是用户可见的真实缺陷；另有一处 Linux 平台
> 限制未在该显示的地方显示（U4）。**

---

## 0. 结论摘要

| 维度 | 结论 | 说明 |
|---|---|---|
| 视图与样式 | ✅ 一致 | 7 个 `.axaml` 与 3 个样式文件三平台共享，零条件编译，零平台专属视图 |
| 深浅色主题 | ✅ 一致 | 跟随系统，颜色令牌单一来源（`Styles/Tokens.axaml` 的 ThemeDictionaries） |
| 功能入口可见性 | ✅ 一致 | 由**被控端授权**驱动（`grant.Allows(...)`），不由控制端平台驱动 |
| 平台驱动的内容差异 | ⚠️ 有意 | 登录项区块、启动失败提示、自检报告深度等，见 §3；与现行文档对得上 |
| 说明文案 | ❌ 3 处缺陷 | Linux 上显示 macOS 的密钥存储与进程模型说明，见 §4 |
| 平台限制告知 | ❌ 1 处缺口 | 固定密码区块在 Linux 上没有 portal 限制说明，见 §5 |

---

## 1. 评审范围与方法

范围：`src/Hosts/RemoteDesktop.Client` 的全部界面代码（`Views/`、`Styles/`、`Controls/`、`App.axaml`）、
承载平台分支的 ViewModel（重点是 `MainWindowViewModel.cs` 与 `RemoteDesktopViewModel.cs`）、启动与失败路径
（`Program.cs`、`StartupFailure.cs`、`SelfCheck*.cs`），以及三个平台层的 `Platform.*` 是否包含 UI 代码。

方法：逐文件阅读 + 全目录平台分支扫描（`OperatingSystem.Is*` / `#if`）+ `git log -S` 追溯每处文案的引入与
改动 + 与 [`docs/usage/README.md`](../usage/README.md) 的支持矩阵逐条对照。**本评审以代码层证据为准**；
涉及字体渲染、emoji 字形这类只有实机才能比较的项，单独列入 §6 观察项并注明未实机复核。

## 2. 三平台一致的部分（零平台分支）

以下为扫描结论，不是抽样：

1. **视图层零平台分支**：`grep -rn "IsWindows|IsMacOS|IsLinux|OperatingSystem" --include="*.axaml" src/`
   命中 0 个文件。主窗口、远程桌面、文件管理器、摄像头、终端五个窗口与两个子视图（`FilePaneView`、
   `TerminalTabView`）全部单份共享；`RemoteDesktop.Client.csproj` 只对**构建产物**分平台（`WinExe` +
   manifest + 图标 vs `Exe`），对 XAML 无任何条件项。
2. **样式系统单一来源**：颜色、间距、圆角、字号都在 `Styles/Tokens.axaml` 的 Light/Dark 两套
   ThemeDictionaries 里；控件外观在 `Styles/Controls.axaml`；导航与功能图标是 `Styles/Icons.axaml` 里的
   `PathIcon` 矢量路径（该文件注释明确"deliberately no icon font"，正是为了绕开各平台字体差异）。
3. **主题跟随系统**：`App.axaml` 保持 `RequestedThemeVariant="Default"`，两套主题对三平台用同一份定义。
4. **功能入口的可见性判据与平台无关**：远程桌面的"文件 / 终端 / 摄像头 / Ctrl+Alt+Del"按钮由
   `session.Context.Grant.Allows(...)` 决定（`RemoteDesktopViewModel.cs:65-77`），主页五个连接方式入口对三
   平台相同的 `ConnectionModes` 列表（`MainWindowViewModel.cs:467-474`）。这与
   [`docs/usage/README.md`](../usage/README.md) §1"被控端不会假装成功"的声明一致。
5. **平台层（`Platform.*`）不含任何 UI 代码**：扫描后全部是采集、注入、音频、剪贴板、密钥等实现，
   没有窗口/控件参与。UI 只在 `RemoteDesktop.Client` 里。

## 3. 有意的平台差异（设计声明，不算缺陷）

以下差异在现行文档里有对应声明，是"产品形状不同"的落地，不列为问题：

| # | 位置 | Windows | macOS | Linux | 依据 |
|---|---|---|---|---|---|
| B1 | 高级设置 → 安全「开机自启（无人值守）」区块 | 隐藏 | **显示**（`SMAppService` 登录项） | 隐藏 | `MainWindow.axaml:364`（`HasLoginItem`），`MainWindowViewModel.cs:174`；[`docs/ports/README.md`](../ports/README.md) |
| B2 | 远程桌面窗口「Ctrl+Alt+Del」按钮 | 仅当被控端为"服务托管"的 Windows 时显示 | 永不显示 | 永不显示 | `RemoteDesktopWindow.axaml:43-45`、`RemoteDesktopViewModel.cs:74-76`；[`docs/usage/README.md`](../usage/README.md) §1 |
| B3 | 启动失败提示方式 | `MessageBoxW` 弹窗 | `osascript` 弹窗 | 无弹窗，stderr + `crash.log` | `StartupFailure.cs:31-43`、`Program.cs:56-60`；`8ada304` 提交说明"消息框按平台守住" |
| B4 | `--self-check` 报告深度 | 短报告（数据目录 + QUIC 两项） | 全栈报告 | 全栈报告（平台层 `LinuxSelfCheck`） | `SelfCheck.cs:91-102`、`SelfCheck.cs:104-158` 注释所述分工 |
| B5 | 高级设置「被控端托管方式」行 | "由后台服务托管 · 代理 …" | "被控端在本进程内运行；……" | "由后台服务托管"（无"代理"段，Linux 没有 Agent） | `MainWindowViewModel.cs:1043-1051`；Linux 形态见 [`docs/usage/README.md`](../usage/README.md) §1 |

另有一组"运行时状态差异"容易被误记为平台差异，实际不是：观看模式下功能按钮消失、被控端收回权限后按钮
消失、多显示器下拉框只在被控端有多屏时出现——这些在任何平台上只要条件相同表现就相同。

## 4. 缺陷 U1–U3：三处说明文案在 Linux 上说的是 macOS

三个属性都是 `MainWindowViewModel` 的 `static` 计算属性，视图用 `{x:Static}` 取，**只有"Windows / 非
Windows"两条分支**——"非 Windows"分支写的是 macOS 的说法。macOS 上显示正确；**Linux 上显示的是错误的
事实陈述**，且与 [`docs/usage/README.md`](../usage/README.md) 已声明的 Linux 事实冲突。

### U1 「固定密码」说明给出错误的存储介质

- 位置：`MainWindowViewModel.cs:239-242`（`FixedPasswordStorageNote`），使用处
  `MainWindow.axaml:332-333`。
- Linux 上实际显示：`……密码以 macOS 钥匙串加密保存在本机。`
- 事实：Linux 的密钥存储是 **owner-only 文件（0600）**——平台层 `RemoteDesktop.Platform.Posix/FileSecretStore.cs`；
  [`docs/usage/README.md`](../usage/README.md) §1 支持矩阵写着"密钥存储：DPAPI / Keychain / **0600 文件**"，
  §7 再次写明"（Windows 是 DPAPI，macOS 是 Keychain，**Linux 是 0600 文件**）"。
- 影响：这是**安全相关的错误陈述**——用户被明确告知密码由"钥匙串"保护，实际是一个用户可读的文件。定级最高的一处。

### U2 「服务器地址」说明描述的是 macOS 的进程模型

- 位置：`MainWindowViewModel.cs:244-247`（`ServerAddressNote`），使用处 `MainWindow.axaml:421-422`。
- Linux 上实际显示：`两台机器必须连同一台服务器。修改后本应用会自动重启：macOS 上客户端与被控端是同一个
  进程，重启后两边一起换到新地址。`
- 事实：Linux 上被控端是 `systemd --user` 服务（`remotedesktop-host` 单元，`Platform.Linux/Setup/LinuxSetup.cs`），
  **客户端与被控端是两个进程**；保存地址的实际路径是 `UserSettings.SaveRendezvousUri` +
  `LinuxSetup.RestartHostServiceIfInstalled()`（`MainWindowViewModel.cs:538-542`），与 Windows"改完要重启服务"
  的结构同族，而不是 macOS 的"一个进程整体重启"。
- 附带证据：这个落差是**功能后补、文案没跟**——`ca5b036`（2026-10-14）"界面里能改服务器地址了"补上了 Linux
  的实现，但 `ServerAddressNote` 没有相应更新。

### U3 「本机控制端代码」说明在服务模式下是错的

- 位置：`MainWindowViewModel.cs:249-252`（`ClientCodeNote`），使用处 `MainWindow.axaml:454-456`。
- Linux 上实际显示：`……macOS 上客户端与被控端是同一个进程，所以它和主页显示的是同一个代码——……`
- 事实：Linux **服务模式**下（`.deb` 装完的默认状态），主页的设备代码来自被控端服务（经 Unix domain socket
  读到，`PipeHostControl`），而"本机控制端代码"是客户端自己注册连出时的身份（`ClientHost.cs:88-96`，
  服务可用时 `AcceptIncoming = false`）——**是两串不同的代码**，与 Windows 的双身份模型同构。
  反过来"同一个代码"只在 Linux standalone（内核宿主模式）下碰巧成立。
- 影响：这句话出现在"查『谁在连我』时用它对"的语境里（Windows 分支原文），在 Linux 上直接把排查引向错误结论。

### 4.4 共同根因与出处

三处由同一提交引入：`dcbf316`（2026-10-13，"Ctrl+Alt+Del 按宿主能力决定……"，提交说明自称
"macOS/Windows 体验一致性复查剩下的……几处 Windows 专有文案"）。**当时是 macOS 移植分支上的复查，语境里
只有两个平台**，实现为 `IsWindows() ? 甲 : 乙`，乙即 macOS。Linux 移植在次日合入（`876d86b`，2026-10-14），
其后的多个 Linux 提交（`ca5b036`、`8ada304` 等）更新了**行为**但没有回头改这三处**文案**。
同类分支还有第四处见 O4。

## 5. 缺陷 U4：「固定密码（无人值守）」区块没有 Linux 的限制说明

- 位置：`MainWindow.axaml:328-360`（区块无平台条件，Linux 上照常显示、可设置），配文即 U1 的
  `FixedPasswordStorageNote`。
- 事实矛盾：[`docs/usage/README.md`](../usage/README.md) §1"授权的差别"与 §7 明确写着：
  **"Linux 上固定密码并不能让控制变成无人值守"**（portal 不允许远程桌面会话持久化，控制权限每次连接
  都要现场点一次同意），并且"要在无人值守的机器上被操作，只能用 Windows 的被控端"。
- 问题：这条限制在**最需要它的地方**（设置固定密码的那个界面）没有任何提示，界面文案反而说
  "设置后本机可用同一密码随时接入，无需现场人员告知临时密码"——按本项目自己"不假装成功"的原则，
  这里缺一句 Linux 特有说明（例如"本平台上控制权限每次连接仍需现场同意，固定密码不能免除这一步"）。
  固定密码在 Linux 上并非无用（它仍是登录后场景的凭据、观看模式不受影响），缺的是**边界告知**，
  所以修复方向是加说明而不是隐藏区块。

## 6. 观察项（不阻塞，列此备查）

| # | 项 | 说明 |
|---|---|---|
| O1 | 文件面板路径框占位符「路径（留空显示所有驱动器）」 | `FilePaneView.axaml:25`。"驱动器"是 Windows 术语；macOS / Linux 上留空列出的是根与挂载点。行为合理，措辞平台化程度不足 |
| O2 | 特殊字符图标依赖系统字体 | 📁 📄 ⏺ ⏹ ✕ ● ○ 等（`FilePaneViewModel.cs:18`、`RemoteDesktopViewModel.cs:157/298`、`TerminalWindow.axaml:27`、状态行）由 emoji/符号字体渲染，三平台字形不同；Linux 缺 emoji 字体时可能不如预期。项目已为导航图标建了 `PathIcon` 体系，这类可后续同化。**未实机核验三平台字形**，仅登记 |
| O3 | macOS 登录项说明的句子歧义 | `MainWindow.axaml:367`："每次登录 macOS 都会自动启动本应用……不需要有人先登录"——前半句与后半句字面上互相矛盾（本意应为"不需要有人手动先启动应用"）。仅 macOS 可见 |
| O4 | 「无法联系后台服务 / 无法读取本机被控状态」二分 | `MainWindowViewModel.cs:258`。Linux 服务模式下被控端同样是后台服务，措辞用"无法读取本机被控状态"不如 Windows 侧的准确；另见 4.4 的同一根因 |
| O5 | 主窗口最小尺寸 860×560 的测量基准 | `MainWindow.axaml:11-18` 注释写明按"跑着的客户端"实测得出（设备列表页 840 宽为临界），未注明测量平台。三平台字体度量不同，建议在 Linux（不同字体/字体缩放）上复核设备列表页三行按钮是否仍不换行 |
| O6 | Linux standalone 时「被控端托管方式」整行隐藏 | `MainWindowViewModel.cs:1047-1051`：非服务且非 macOS 时为 `null`。macOS 对"无服务"给出了文案（"被控端在本进程内运行……"），Linux standalone 留白。属边缘模式（`.deb` 默认带服务），仅一致性尾巴 |

## 7. 建议（本评审只记录，未改动任何代码）

1. **U1–U3 改为三路分支**（或一张"平台 → 文案"表）：`FixedPasswordStorageNote`、`ServerAddressNote`、
   `ClientCodeNote` 各补 Linux 分支——存储说"仅本用户可读的文件"、进程模型说"客户端与 `systemd --user`
   被控服务是两个进程、各自重启"，代码身份按"服务模式两串代码 / standalone 一串代码"表述。
2. **U4 在 Linux 上补一句**：位于固定密码说明之下，点明"本平台控制权限每次连接仍需现场同意"，
   与 [`docs/usage/README.md`](../usage/README.md) §1 的措辞保持一致。
3. **注意测试盲区**：这些属性是 `static` + 运行时 `OperatingSystem.Is*()` 判断，在任一平台上跑测试都只能
   覆盖一个分支——修完此类文案后**要在三个平台各跑一次相关页面**，不要用本平台测试结果推及其它平台。
4. 观察项 O1–O6 可不随本次修复走；O3、O5 建议各用一次文案/实机核对顺手处理。

## 8. 明确没做的

- **没有修改任何代码**——本评审是只读快照，修复应另起提交。
- **没有在三台真机上并排运行比对**：§4、§5 的缺陷判定是代码层证据（分支条件 + 文档对照 + git 历史），
  不依赖实机；§2 的"一致"结论同理。字体渲染、emoji 字形（O2）与最小尺寸（O5）属于需要实机复核的项，
  文中已逐条注明。评审时唯一在场的运行环境是 Windows（本机），未用它替代三平台比较。
