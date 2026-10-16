# 更新日志 (Changelog)

本项目所有的显著变更都会记录在此文件中。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)。

## [未发布] · macOS (Apple Silicon) 与 Linux 平台支持

新增 `RemoteDesktop.Platform.MacOS` 与 `RemoteDesktop.Platform.Linux` 两个平台项目（`net10.0`，与 Windows 侧
同形），实现全部平台端口，使同一份客户端代码可在三个系统上构建运行；两个移植共享新增的
`RemoteDesktop.Platform.Posix` 层（伪终端、0600 文件密钥库、用户上下文守卫）。核心层（`Domain` /
`Application` / `Media` / `Protocol` / `Signaling`）**零改动**，依赖规则（`Architecture.UnitTests`）无需修改。

### Added

#### macOS

- [Platform] **屏幕采集与输入注入**：ScreenCaptureKit 截屏（含光标）、CGEvent 键鼠注入，统一坐标换算
  （虚拟桌面用逻辑点、采集返回像素、输入用归一化 0..1）。
- [Platform] **视频编解码**：VideoToolbox H.264（硬件优先 + 软件兜底），线上 Annex-B、内部 AVCC 显式互转。
- [Platform] **音频**：ScreenCaptureKit 环回采集（复用屏幕录制授权，**不新增麦克风权限**）+ AudioQueue 播放。
- [Platform] **摄像头**：AVFoundation 枚举与采集。
- [Platform] **录制**：AVAssetWriter（H.264 + MP4），复用共享 `Media` 层录制契约。
- [Platform] **无人值守**：`SMAppService` 登录项（新增 `ILoginItem` 端口）。
- [Platform] **剪贴板 / 防休眠 / 远程终端 / 密钥**：NSPasteboard、IOKit 电源断言、BSD 伪终端（`posix_spawn`）、
  Keychain。
- [Build] **`.app` 打包脚本** `build/installer/client/macos/make-app.sh`（自包含、`codesign --deep`、身份钉住）
  与原生辅助库构建 `native/macos/build-native.sh`（扁平 C ABI，44 个导出入口）。
- [CI] 新增 `build-macos` job（`macos-26`，arm64）。

#### Linux

- [Platform] **Linux 被控端与客户端**：屏幕走桌面 portal + PipeWire，输入走 libei，编解码走 libavcodec，
  音频走 PulseAudio，防休眠走 logind。
- **Linux 被控端是一条 `systemd --user` 服务**，不是系统服务：Wayland 的 portal 只在该会话内应答，
  所以它跑在图形会话里，自己就是采集和注入的那一方——**没有 Agent**，因为不需要拆。
- **`.deb` 安装包**：`remotedesktop-client`、`remotedesktop-host`、`remotedesktop-server`。都是自包含发布，
  目标机器不需要装 .NET；`libmsquic` 随包放在可执行文件旁，所以也不需要设 `LD_LIBRARY_PATH`。
  服务端的配置在 `/etc/remotedesktop-server/`，升级不覆盖。
- **Linux 上的配置分两层**：随包装在 `/usr/lib` 下的那份提供默认值，用户自己的
  `~/.local/share/RemoteDesktop/appsettings.json` 叠在上面。客户端里那个「修改服务器地址」在 Linux 上也能用，
  写的就是这一份，客户端和被控端共用。

#### 两端共通

- **`--self-check`**：一条命令问完这台机器能不能用。macOS 的版本在 bundle 内逐一验证每个平台端口与行为探针；
  Linux 的版本检查数据目录、QUIC、会话类型、会话总线、运行时目录、portal、显示布局、PipeWire、libei、
  libavcodec 版本与可用的 H.264 编码器。全部只读、不弹对话框，在一台起不来的机器上照样能跑；
  有必需项不通过时退出码 1。**两个包都带**，不只客户端——只装被控端的机器更需要它。
- **发布流水线产出三平台产物**：`release.yml` 的三个打包 job（`build-linux` / `build-macos` / `build-and-release`）
  把两个 MSI、两个 `.pkg` 和三个 `.deb` 一起发出；CI 在每个 PR 上构建三平台的包并检查包内容——不检查的话，
  构建脚本坏了要等到发版才知道。

#### 仓库结构与跨平台一致性

- **`.gitattributes`**：此前仓库里**一个 git 属性都没有**，行尾全靠各人机器上的 `core.autocrlf`——而那是个本地
  设置，对这个跨平台保证没有任何强制力。现在在仓库层面声明：`.sh` / `Dockerfile` / Debian 维护脚本 / `.plist` /
  `.service` / `.desktop` / `.yml` 一律 LF，`.ps1` / `.wxs` / `.wixproj` / `.csproj` / `.props` / `.slnx` 一律 CRLF，
  图片与库文件标为二进制。Windows 上工作区里的 13 个 `#!/bin/sh` 脚本原本是 CRLF，意味着 WSL、Docker 挂载源码
  目录、`scp` 到 Linux 主机这些走法都会以 `bad interpreter: /bin/sh^M` 失败。
- **`.editorconfig`**：编码与缩进基线（C# 4 空格、XML 类文件与 JSON/YAML 2 空格）。**不设行尾**——那是
  `.gitattributes` 的职责，两处都写就会有两个答案。
- **`build/tools/`**：仓库维护类脚本从 `build/` 根下收拢进来并统一命名——`Generate-ThirdPartyNotices.ps1`、
  `Invoke-SecurityAudit.ps1`（原 `security-audit.ps1`）、`New-AppIcons.ps1`（原 `generate-icons.ps1`）。
  新增 `build/README.md` 的 `tools/` 一节写明哪几份能跨平台跑：前两份只读文件、三平台都可用 `pwsh`，`New-AppIcons.ps1`
  依赖 `mstsc.exe` 的资源段与 GDI+，只能在 Windows 上跑（产物是提交进仓库的，所以不需要别处能跑）。
- **`LICENSE`**：安装包一直带着一份专有 EULA（`build/installer/License.txt`），但仓库根本身没有任何许可声明。
  现在那份文本就是仓库根的 `LICENSE`，MSI 与 `.pkg` 都从它生成，不再是"仓库一份、包一份"的两份拷贝。
- **测试项目命名统一为 `<区域>.<种类>Tests`**：`ArchitectureTests` → `Architecture.UnitTests`、
  `Platform.{Windows,Linux,MacOS}.Tests` → `Platform.{Windows,Linux,MacOS}.UnitTests`，命名空间同步。
  此前同一层混用三种后缀，按 `**/*.UnitTests` 之类的模式筛选会**静默漏掉**项目；规则写进了 `CONTRIBUTING.md`。
- **`build/` 的分层统一为 `<平台>/<角色>`**：原先 Linux 的打包脚本住在 `build/debian/{client,host,server}/`
  （按发行版分），Windows 与 macOS 的住在 `build/installer/{client,server}/{windows,macos}/`（按角色再按平台），
  加一个平台时要先选边站。现在一律是 `build/installer/<平台>/<角色>/`——平台在外，因为它决定一个脚本能不能在
  这台机器上运行；平台名取 `windows` / `macos` / `linux`，不是发行版也不是包格式。分层规则写在新增的
  `build/README.md` 里。
- **服务定义收敛到 `build/service/<平台>/`**：systemd unit 与 `/etc/default` 原先随 `.deb` 脚本放在
  `build/installer/linux/{host,server}/`，launchd plist 原先放在 `build/installer/macos/server/`，Windows 的
  服务安装脚本在 `build/service/windows/`。现在三个平台的服务定义同层并列，"改一个服务的启动方式"只有一个地方
  要找。
- **`build/installer/License.rtf` 不再进仓库**：它是 `New-LicenseRtf.ps1` 从根 `LICENSE` 生成的产物，而两个
  Windows 打包脚本每次构建都会重新生成它。原来它躺在版本库里，改了 `LICENSE` 却忘了重跑脚本时，安装器的许可页
  会静默停留在旧版本，且没有任何构建步骤会发现。现在已 gitignore。
- **`docs/` 按读者分层**：`design/`（架构、协议、安全及其评审）、`ops/`（部署、存储）、`ports/`（macOS / Linux
  移植计划与评审）；文档索引与两份目录结构评审留在 `docs/` 根（这一步后来又被整体重排成"一个目录一份文档"，
  见"未发布"一节）。`docs/README.md` 同时补上了此前漏掉的目录结构评审。
- **目录结构约定改由测试强制**：新增 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs`，
  把六条约定变成断言——`tests/` 的项目名闭集、项目目录名 == `.csproj` 名、`.cs` 文件名与所声明类型的关系、
  `.sh` 的 kebab-case、`.ps1` 的 Verb-Noun（动词须在 PowerShell 批准表上）、`build/` 下的平台与角色目录名。
  三轮目录结构评审每轮都能挑出新的违反，原因是同一个：分层规则有架构测试盯着、三平台移植期间一次没坏过，
  而这些规则只写在 `CONTRIBUTING.md` 里，靠读到的人自觉。现在违反即构建红。
- **产物目录三处修正**：`.trx` 从仓库根的 `TestResults/` 迁到 `artifacts/test-results/<平台>/`；`artifacts/native/`
  改名 `artifacts/lib/`，免得与顶层那棵手写 Objective-C 源码树 `native/` 同名不同义（一个删了就没了、一个随时
  可再生）；安装包的 `.wixpdb` 是调试符号不是交付物，用 `-p:PdbOutputDir=` 留在工程 `obj/` 里，不再落进
  `artifacts/installer/<平台>/`。
- **`TestResults/` 不再由仓库自己的东西产生了**——补完上一条漏掉的一半。改 `run-test-suite` 只把**报告文件**
  挪进了 `artifacts/`，而 `dotnet test` 在没给 `--results-directory` 时仍会在当前目录建一个**空**的
  `TestResults/`；仓库里还有三处这么调用（`verify-packages` 的 Windows 分支、两个安装器脚本的自查步骤），
  所以根上照样会长出目录，只是被 `.gitignore` 挡着看不见。三处都补上了 `--results-directory`（两个脚本指向
  `artifacts\test-results\windows`，CI 那处指向 `artifacts/test-results/windows`）。
  第四处修不掉也不打算修：`CONTRIBUTING.md` 教贡献者跑的那条裸 `dotnet test RemoteDesktop.slnx` 同样会建它，
  那是 Microsoft.Testing.Platform 的默认值而不是本仓库的约定。MTP 的
  `TestingPlatformCommandLineArguments` 属性能把参数烤进测试程序，但烤进去的和 CI 显式传的是同一个选项，
  重复选项很可能让 MTP 直接报错——CI 的正确性比一个空的本地目录重要，所以接受默认值，改在 `CONTRIBUTING.md`
  里写明看到它不必处理。忽略规则保留。
- **修一个静默回归**：产物收敛之后，`HostService` 的 `CopySessionAgent` 仍在往已被搬走的
  `RemoteDesktop.SessionAgent\bin\` 里找文件，而 MSBuild 对空 item 列表不报错——"普通构建会把 agent 放到服务
  旁边"这句话已不成立，构建却一直是绿的，直到 `install-service.ps1` 抛出一句指错方向的提示。改为用
  `GetTargetPath` 向那个工程要输出目录，从此不可能再与布局脱节。
- **`CONTRIBUTING.md` 产物表的三处硬错误**：`artifacts/packages/` 改成 `package/<pivot>/`（SDK 用的是单数，
  且没有项目层）；安装包文件名改成实际的两个 `RemoteDesktop-<版本>-x64.msi` 与
  `RemoteDesktop.Server-<版本>-x64.msi`——文档原先写的带 `Windows` 的名字只是发布时重命名的资产，
  `verify-packages` 的注释里早就记着这个坑"这个仓库已经踩过一次"；`app/<平台>/` 的下一层写明是**交付物**
  而不是角色（`app/windows/client/` 里同时有三个 `.exe`，因为一个 MSI 装三个角色）。
- **`THIRD-PARTY-NOTICES.md` 重新生成**：它已经静默过期——`Directory.Packages.props` 里的 Avalonia 12.1.4 与
  Tmds.DBus 0.95.1 没同步进去，`FFmpeg.AutoGen` 更是整个缺项。与 `License.rtf` 是同一类问题（生成物进了仓库
  却没有防过期的机制），但这份不能靠"不跟踪"解决，CI 侧的防护留待后续。
- **7 个文件名与所声明类型不符的 `.cs` 改名**：`SystemIntegration.cs` 里装的是 `IOPMAssertionKeepAwake`、
  `SystemIntegration.LoginItem.cs` 里是 `MacLoginItem`（两者还都不是 partial 拆分）、`DxgiDisplays.cs` 里是
  `DxgiDisplayEnumerator`、`PortalResponse.cs` 里是 `PortalRequestResult`、`CameraExceptions.cs` 里是
  `CameraUnavailableException`，另有两个测试文件。判据同时写进 `CONTRIBUTING.md`：文件名不是类型时它必须是
  个分组（声明两个以上类型），这样 `IAudio.cs` 这类"一个概念的整组契约"仍然合法。
- **三处改名让名字说明自己是什么**：`build/service/linux/default` → `remotedesktop-server.default`（它变成
  `/etc/default/remotedesktop-server`，`.gitattributes` 里那条逐名规则也随之改成 `*.default` 通配）；
  两份 `HOW-TO-CONFIGURE.txt` → `how-to-configure.txt`；`Generate-ThirdPartyNotices.ps1` →
  `Export-ThirdPartyNotices.ps1`——`Generate` 不在 PowerShell 的批准动词表上，而 `build/README.md`
  原先恰好把它当范例列着。
- **`release.yml` 的 `build-and-release` → `build-windows-and-release`**：它是三个 job 里唯一不带平台词的，
  读 job 列表看不出 Windows 在哪。
- **`icon.png` 与 `icon-256.png` 去重**：从 `mstsc.exe` 抠出来的就是 256×256，所以"母版"与"256 尺寸"是同一张图
  的两个名字，生成器每次都会重造这份重复。现在只写 `icon-256.png`（应用与 `icon.svg` 本来就指向它）。
- **`native/README.md` 与 `build/README.md` 补齐**：前者说明这层放什么、为什么没有 `windows/`（Windows 走 C#
  P/Invoke，没有编译出来的辅助库）与 `rd_` 前缀规则；后者首行声明"本目录不是构建系统"、补上三平台角色集合不
  对称的表（Linux 单独出被控端包，Windows 并进客户端 MSI，macOS 客户端自己就是被控端）、补 `docker/` 是第三条
  轴，并把脚本命名规则从 `build/README.md` 提升上来。
- **`THIRD-PARTY-NOTICES.md` 有了过期防护**：CI 在一个平台 job 里重新生成它并比对，有差异就红并把 diff 打出来。
  上一节记的正是它已经静默过期过一次（缺 `FFmpeg.AutoGen` 整个一项、两个版本号过时），而这类漂移在 review 里
  看不出来。只放一个 job 是因为清单不带 RID 读，三平台同一份。
- **5 个安装器与服务脚本改成 Verb-Noun**：`Build-ClientInstaller.ps1`、`Build-ServerInstaller.ps1`、
  `Set-ServerConfiguration.ps1`（`Configure` 不在 PowerShell 的批准动词表上）、`Install-HostService.ps1`、
  `Uninstall-HostService.ps1`（后两个装/卸的是**被控端**服务，旧名只说 "service"，与 mac 的
  `uninstall-server.sh` 一词之差而角色相反）。结构断言里的豁免清单随之删掉，`build/` 下 12 个 `.ps1` 现在
  全部受约束。`CHANGELOG.md` 与旧评审里的旧名保持原样。
- **`.gitignore` 的构建产物段从 13 条无锚定模式砍到 2 条**：只留 `[Oo]bj/` 与 `**/[Bb]in/*`，并把例外规则
  扩成 `build/installer/windows/**/{bin,obj}/` 盖住深一层的 `CustomActions`。删掉的 `[Dd]ebug/`、`[Rr]elease/`、
  `[Ll]og/` 等**一条都没在干活**——它们按名字匹配树里任何同名目录，实测被忽略的路径集合删改前后
  6019 条逐行相同。留着只是等着 `Log/`、`Release/` 这类目录第一次出现在源码树里时被静默吞掉。
- **发布资产的命名对齐**：客户端资产此前只在 Linux 上带 `Client` 词（那边有三个包，本来就得靠角色词区分），
  现在三个平台都带，七个资产的形状统一为 `<产品>-<角色>-<平台>-<架构>-<tag>.<扩展名>`。架构词**保持**各平台
  自己的说法（Windows `x64` / macOS `arm64` / Linux `amd64`）——Linux 那个与 `.deb` 内的 `Architecture` 字段
  同源（`control` 模板），统一成 `x64` 会让文件名与字段不同词。约定写进 `CONTRIBUTING.md` 的产物布局一节，
  含三平台对照表与两条成因。**产物名没有跟着改**（`artifacts/installer/windows/` 里仍是
  `RemoteDesktop-<版本>-x64.msi`），所以 `verify-packages` 的分平台 glob 不受影响；产物名与资产名不一致这件事
  本身仍在，两份文档里都写着——那是这个仓库踩过的坑。
  顺带修掉 `docs/ops/README.md` 发布一节开头的过期说法：那里写"把**两个**安装包放进同一个 Release"并只列了
  两个 MSI，而流水线实际发**七个**（两个 MSI、两个 `.pkg`、三个 `.deb`），现在按平台列全了。
- **`build-deb.sh` 不再依赖 GNU 专属的 `sed -i`**：改用 `awk` 写临时文件再 `mv`（BSD 的 `sed -i` 把参数当成备份
  后缀，替换里的 `\n` 也不是换行）。

#### 文档与仓库约定

- **文档体系重排：从 27 份收敛到 13 份**（`docs/` 一份正文对应一类读者，`build/` 与 `native/` 各一份），
  目标是"一个目录一份完整文档"——读者按目录名找到东西，打开就是全文，不必猜该看哪一份。
  `docs/` 现在是 `usage/`（使用说明）、`design/`（分层 + 协议 + 安全）、`ops/`（部署 + 打包 + 本机存储）、
  `ports/`（两平台移植总览）、`reviews/`（归档）五份正文，加索引与规范本身。
- **三份设计文档合并为 `docs/design/README.md`**，三份运维文档合并为 `docs/ops/README.md`
  （去掉了打包一节里与部署一节重复的构建说明），两份使用文档合并为 `docs/usage/README.md`
  （平台差异表原先是两份各写一遍）。
- **两份移植计划合并为 `docs/ports/README.md` 并删掉约 1300 行本机实测记录**：那两份文档共 2877 行，
  其中三分之二是"哪条命令、什么输出"的一次性证据，长在"计划"里而计划早已 100% 完成。
  合并后只留**结论**——已确认的决策、每个平台绕不过去的硬约束、明确不做的范围、未验证项与风险。
  现场记录仍可从 git 历史里取回。**代码与构建脚本里 15 处指向 `§3.x` 实测小节的注释同步改成按小节名引用**，
  否则删掉实测就等于留下一堆指向不存在小节的注释。
- **同一天三轮的目录结构评审合订为一份**（`repo-structure-review-2026-10-16.md`，按轮分节），
  不再为每一轮单开文件；命名规则随之简化为"评审用日期"。
- **根 `README.md` 从 395 行的手册精简为落地页**：功能说明、平台支持与限制、被控端进程模型、打包细节全部
  下沉到 `docs/`，README 只留摘要与导航。它此前也重列了 `docs/README.md` 的子集，且已经漏掉四份文档——
  手工维护的第二份索引必然分叉，现在没有第二份了。
- **新增 `docs/STYLE.md`**：文档放哪、叫什么、怎么写、什么时候更新，以及**哪些规则由测试和 CI 强制**。
  此前这些约定散在 `CONTRIBUTING.md`、`build/README.md` 与 `docs/README.md` 三处。
- **新增 `DocumentationTests`（8 条断言）**：必备文件存在、每个文档恰好一个 H1、`docs/` 下没有孤儿文档、
  相对链接与锚点可解析、文件命名规范、评审必须归档在 `docs/reviews/`、README 必备章节。它第一次运行就抓出
  四处死链——`docs/design/README.md` 指向 `ports/`、`docs/design/README.md` 指向 `ops/`，
  两处都是 `docs/` 按读者分目录那一轮留下的，之后一直没人点到。
- **新增 GitHub 标准社区文件**：根 `SECURITY.md`（私有漏洞上报通道，并列出"已知的、不算新发现的问题"）、
  `.github/PULL_REQUEST_TEMPLATE.md`、`.github/ISSUE_TEMPLATE/`（bug 表单带自检与日志提示，config 把安全问题
  指向私有通道）。
- **`build/tools/README.md` 并入 `build/README.md`**：49 行的目录说明不值得单开一份文件，
  而 `build/README.md` 本来就有它那一行索引。
- **CI 新增 `docs` 作业跑 markdownlint**，版本钉在 `0.23.3` 而不是 `@latest`：新规则在次版本里就默认开启
  （MD060 表格列风格就是这么来的，一次报了 249 条本仓库长期在用的表格写法），`@latest` 会让构建在某天早上
  无故变红。同时把全仓 60 处未标语言的代码围栏补上语言（`text` / `bash` / `powershell`），并修掉其余格式问题。

### Changed

- **宿主装配按平台分开**：`Platform.Windows` / `Platform.MacOS` / `Platform.Linux` 各自注册同一批端口，
  业务代码不知道自己在哪个平台上；客户端由 `ClientPlatformRegistration` 在运行时选层。
- [Posix] **伪终端一份实现服务两个系统**：原先 macOS 与 Linux 各写了一份（会话、等待退出、清理在内约 455 行
  相同逻辑）。现在差异（`TIOCSWINSZ`、`O_NOCTTY`、`POSIX_SPAWN_SETSID`、Darwin 的非变参 `__open` / `__ioctl`）
  全部封在共享 `LibC` 里。顺带修掉一个真 bug：Darwin 上的 spawn flag 曾用 `0x0002`——那是
  `POSIX_SPAWN_SETPGROUP`（进程组，不是会话）——shell 因此拿不到控制终端；现按 Apple 头文件用 `0x0400`。
- [Platform] **`UserContextGuard` 与"用户自己的设置文件"统一到共享层**：macOS 与 Linux 用同一份实现与同一个
  文件格式（`<数据目录>/appsettings.json`，由 `UserSettings` 读写），不再各写一份；数据目录也按平台修正
  （Linux `~/.local/share`、macOS `~/Library/Application Support`，Windows 不变）。
- [Transport] `DeviceIdentity` 加载 PKCS#12 时加 `X509KeyStorageFlags.Exportable`（macOS 上不加则私钥
  不可导出、MsQuic 握手失败；Windows 不受影响，一个代码路径服务三平台）。
- [FileTransfer] 修掉两个"假设被控端是 Windows"的真 bug：`RemoteFileSource.Combine` 不再硬编码 `\`（分隔符
  从被控端返回的路径字符串上读）；`FileAccessPolicy` 按平台区分路径规则（大小写、保留名、内核路径），
  并让根与入参同做链接解析（修 macOS `/var→/private/var` 误判）。
- [Hosts] `ClientHost` / `Program` 去掉 Windows 硬编码；录制器改由 DI 注入——
  `MfVideoRecorderFactory` 从客户端三个 ViewModel 里删掉，那个 `IsWindows()` 分支只会在非 Windows 上让录制
  **静默不工作**。
- [Tests] Windows 专有用例改为 `SkipUnless` 跳过（其他平台上不再计为失败）；`SystemSetupTests` 里与注册表
  无关的用例现在在三个平台都真跑。
- [CI] **三个平台的 CI 与发布流程对齐**。依赖漏洞审计与"Linux runner 跑测试前的准备"各自抽成共享组合动作
  （`audit-packages`、`prepare-linux-tests`），与 `run-test-suite` 一样只有一份，六处调用不会各改一半；
  作业名统一为 `build-windows` / `build-linux` / `build-macos`（Release 侧另有 `build-and-release`）。
  两个 workflow 都声明顶层 `permissions: contents: read`，只有发版那个 job 自己申请 `contents: write`。
- [CI] **CI 现在构建三平台的安装包**：Windows 的两个 MSI 与 macOS 的两个 `.pkg` 也在每个 PR 上构建并校验
  包内容（此前只有 Linux 的三个 `.deb`）。这些脚本原本只在打 tag 时跑，坏了要等到发版才发现；而 `.pkg`
  那条路径此前**一次都没在 runner 上执行过**。
- [CI] **Release 三个平台都自己跑测试与依赖审计**：Linux 那个 job 此前只做 `dotnet publish`（既不跑测试也
  不审依赖），现在与另外两个平台调用同一批共享动作；它的 runner 从 `ubuntu-latest` 钉到 `ubuntu-26.04`，
  与 CI 验证过的那台一致。Windows 那个 job 里内联复制的一份测试脚本（不上传 TRX、不写汇总表）改回调用
  共享动作；macOS 那个 job 补上了此前缺失的依赖审计。
- [CI] **三平台的安装包校验统一成一份共享动作**（`verify-packages`）：打开每个包看里面有什么，而不是数文件
  个数——`.deb` 用 `dpkg-deb --contents` 列内容并读它必须带的手写依赖行，`.pkg` 用 `pkgutil --payload-files`
  列 payload（客户端要带 `Info.plist`、服务端要带各自的 launchd plist），MSI 用仓库自己的
  `InstallerPackageTests` 打开数据库（payload、服务崩溃重启、防火墙规则、快捷方式、向导页与许可文本）。
  CI 与 Release 各三处调用，发版路径上 Linux 与 macOS 的包此前**只被数过数量**：内容不对的包照样会被数到。
- [Build] **构建产物收敛到 `artifacts/`**：根 `Directory.Build.props` 打开 `UseArtifactsOutput` 并把
  `ArtifactsPath` 指向仓库根，于是 23 个项目不再各自长出 `bin/` 与 `obj/`（此前是 46 个目录要清、要忽略、
  要解释），输出统一落在 `artifacts/bin|obj|publish|packages/<项目>/<pivot>/`——与打包脚本一直在写的
  `artifacts/{app,installer,native}/<平台>/` 同一个根。`build/installer/` 下的 WiX 与 net48 CustomActions
  项目带着自己那份（故意留空的）`Directory.Build.props`，不受影响，产物仍在项目内。
- [Build] **`.deb` 的输出路径从 `artifacts/debian/` 改为 `artifacts/installer/linux/`**：`CONTRIBUTING.md` 写死了
  `<类别>/<平台>/` 的形状、并明确禁止"同一类别两个根"，而 `debian` 是发行版不是平台。现在三个平台的安装包
  都在 `installer/<平台>/` 下，`.deb` 与 `.msi`、`.pkg` 并列。
- [Build] `make-app.sh` 不再写死客户端的 publish 目录（原先硬编码
  `src/Hosts/RemoteDesktop.Client/bin/<配置>/net10.0/osx-arm64/publish`，既耦合布局也耦合 RID），改为向 MSBuild
  询问 `PublishDir`；`build-installer.ps1` 清理 SessionAgent 产物时同理改用 `artifacts/` 下的新位置。
- [Docs] `docs/README.md` 补上漏列的 `security-review.md`；`HOW-TO-CONFIGURE.txt` 取代原先带中文的
  `如何配置.txt` 文件名（开始菜单里的入口名「服务器配置说明」不变），仓库内已无任何非 ASCII 路径。
- [Naming] `RemoteDesktop.Posix` → `RemoteDesktop.Platform.Posix`：与 `Platform.{Windows,Linux,MacOS}` 对称。
  这同时堵上一个洞——架构规则按**程序集名前缀**加载程序集，`RemoteDesktop.Posix` 因此一直没被
  `Platform` 那条规则看到（见 Fixed）。

### Fixed

- **`SpaPod` 解析器少读 4 个字节**：SPA 对象体是 `{type, id}`，没有属性个数，而解析器在 id 后面读了一个"个数"
  ——那其实是第一个属性的 key（真实值是 131073）。此前的测试全绿是因为用例字节按同一个错误理解手写。
  这类错误在真实链路上的表现是"格式协商莫名其妙失败"。
- **portal 文件描述符的竞态**：`CallForFdAsync` 原本在 `CallMethodAsync` 返回之后才 dup 那个 fd，而 fd 属于
  message、message 在读取委托返回后就释放了——偶尔 dup 到一个已关闭的编号，报 EBADF，而连接本身好好的。
  平时不显形，测试并行度上来才出现。
- **Linux 上启动失败不再一声不吭**：`StartupFailure` 原本只在 Windows 弹消息框，非 Windows 什么都不做，
  于是失败只留下退出码 1 和一个**没人打印出路径**的 `crash.log`。现在同一句话也写到标准错误。
- **没有摄像头的机器不再白弹一次授权窗**：枚举先读 `IsCameraPresent`（属性，不弹窗），为假就直接返回空。
- [FileTransfer/macOS] **符号链接目标要再解析**：macOS 上根内合法链接曾被误拒（`/var` 是 `/private/var`
  的链接）。
- [macOS/打包] **`fetch-libmsquic.sh` 随包分发的依赖集原先靠写死的 openssl@4**，而 Homebrew 当前
  libmsquic bottle 直接链接 `libcrypto.3`（openssl@3）——**装出来的 .app / .pkg 里 QUIC 会起不来**，
  且所有不真正加载的检查（文件在、`otool` 正常、签名已打）都会放行。现在依赖从 bottle 的声明里读、
  引用按二进制里的实际形状收集（bottle 占位符与 `@loader_path` 两种），并且**结束前把每个 staged
  文件真的 `dlopen` 一遍**，缺件当场报出 dyld 的原因而不是等到运行时"QUIC is not supported"。
- [CI/发布] **`release.yml` 按错的名字去找两个 MSI**：构建产物叫 `RemoteDesktop-<版本>-x64.msi` 与
  `RemoteDesktop.Server-<版本>-x64.msi`（两个 wixproj 的 `OutputName`），而重命名那一步按**发布产物**的名字
  （`RemoteDesktop-Windows-*.msi`）去 glob，两个都找不到，会在"少一个就什么都不发"的检查上直接抛错。
  这条路径在 v0.3.0 之后一次都没跑过，所以一直没被发现；同一次改动把三平台的产物校验统一成了共享动作，
  它按构建产物的真实名字找包，并断言"一个 glob 恰好命中一个"。
- [Tests] **架构规则有两个平台层从未被检查过**：`DependencyRuleTests` 按程序集名前缀从测试输出目录里加载
  程序集，于是 `RemoteDesktop.Posix`（前缀对不上 `RemoteDesktop.Platform`）和 `RemoteDesktop.Platform.MacOS`
  （根本没被 `ArchitectureTests.csproj` 引用）都不在检查范围内——而那个文件第 5 行的注释正是警告"没被引用的
  平台会让套件全绿却什么都没检查"。前者随 `RemoteDesktop.Platform.Posix` 改名解决，后者补上项目引用；
  现在四个平台程序集都在规则内，且都合规。
- [Tests/macOS] **`The_frame_can_be_disposed_and_another_captured` 在 CI 上高频失败**：它先捕获一帧、释放它、
  再捕获一帧，用来验证"释放上一帧之后捕获仍然正常"。守卫只盖了第一帧（"runner 什么都没画，所以没有帧"），
  却要求第二帧必须到来——而 ScreenCaptureKit 是**画面有变化时**才交付新帧，runner 的显示器正好不会变化：
  第一次捕获拿走了当前那帧，第二次要等一帧新的。**这个假设在静止显示器上恰好是反的**，第二帧比第一帧更容易
  为空。同一份代码三次运行里挂了两次，master 因此变红。现在第二帧缺席也按同一理由跳过；生命周期检查仍在
  （非空时那两行断言照跑），只是不再把一个环境事实判成失败。

### 平台上的边界与未验证项

**macOS**

- 最低系统版本为 **macOS 26 (Tahoe)**，由随包分发的 `libmsquic` / `openssl@4` bottle 的 `minos 26.0` 决定
  （见 `docs/ports/README.md` 的"最低系统版本是 macOS 26"）。降低下限需改走"从源码构建 MsQuic"。
- 已知未覆盖（需人工授权，自动化测试无法验证）：辅助功能授权下的真实输入注入、受保护目录
  （`~/Desktop` / `~/Documents` / `~/Downloads`）的 TCC 弹窗路径。

**Linux**

- **登录界面 / 登录前的远程访问**：GNOME 的登录界面是另一个用户、另一个 VT，私有用户会话够不着。
  Windows 上能换 Agent 接管安全桌面，这里没有对应物。
- **无人值守下的操作**：portal 不允许远程桌面会话持久化，所以**控制权限每次连接都要有人在机器前点一次同意**。
  共享屏幕授权一次就记住了，但无人值守的机器因此**可以被观看、不能被操作**。
- 剪贴板已实现并双向验证（走 portal：在远程桌面会话开始前申请，发布用 `SetSelection`、读取用
  `SelectionRead`，本机程序复制的内容也能触发同步）。摄像头已实现（走 portal 授权 + PipeWire 节点，
  JPEG 与 4:2:2 都支持），但**本机没有摄像头，那条路一次都没跑过**，请当成未验证。
  Ctrl+Alt+Del、快速用户切换、隐私屏同 Windows 一样不支持。
- 光标元数据在本机收不到（合成器不送），控制端只能画自己最后知道的那个指针。

## [0.3.0] - 2026-10-11 · 安装向导与自签名证书

### Changed

- [CI] **集成测试失败后自动重跑一次**（`ci.yml` 与 `release.yml` 同策略）。这些测试要跑真实进程、真实 PTY、
  真实 UDP 打洞和真实 Media Foundation 编码器，而 runner 又慢又抢：同一提交常常这次红一个测试、下次红另一个
  ——2026-10-11 一天里三个不同的测试各红过一次，其中一个在 Release 流水线里通过、几分钟后的 master CI 里失败。
  现在两次都失败才判红，第一次失败会打成 GitHub 警告并**列出失败的测试名**。
  **代价说清**：真实存在的偶发 bug 同样会被这一次重试掩盖，所以那行警告不是噪音，别忽略它。

### Added

- [Server] **服务端安装包有向导页了**（"服务器设置"，插在选目录与确认之间）：数据库连接串、中继公网地址、
  以及信令是否走 TLS（不启用／**自动生成一张自签名证书**／使用已有证书文件+口令）。此前这些只能在命令行上
  传 `msiexec POSTGRESCONNECTIONSTRING=...`——双击安装会全取默认值，而默认值是"不用数据库"和"明文 http"，
  正是后果最重的两项：前者让设备代码每次重启都变（无人值守的机器就此失联），后者让设备代码与中继票据
  在途中可被读取。这两条警告其实一直写在 `server-setup.log` 里，只是双击安装的人不会去看。
- [Server] **安装时自动生成自签名证书**（向导里勾一下，或 `TLSCERTIFICATEPATH=generate`）：按本机名字与
  地址生成，**私钥进本机证书存储**（`LocalMachine\My`——安装目录在 `Program Files` 下、本机所有用户可读，
  私钥不能放那儿），公钥导出到 `tls\signaling.cer` 供客户端信任；Kestrel 按 Subject/Store 引用它。
- [Server] **装完之后的配置路径**：安装目录里多了一个 `configure-server.ps1`（改设置并重启两个服务，
  与安装程序共用同一个配置步骤，支持 `-WhatIf` / `-NoRestart`）和一份 `如何配置.txt`。此前改配置只能手改
  `appsettings.json`，而服务端程序自带的 `--configure-system` 从未写进文档、等于不存在。

### Fixed

- [Server] `TLSCERTIFICATEPATH=auto`（安装包的默认值）以前会走到文件检查，报出"证书 'auto' 不存在"——
  读起来像用户填错了。现在它正确地等同于"不给证书"，并给出那条明文 http 的警告。
- [Server] **"自动生成自签名证书"那条路以前装出来的服务根本起不来**，现在真的能用了（已按端到端跑过：服务启动、
  TLS 1.3 握手成功、出示的证书与导出给客户端的 `tls\signaling.cer` 是同一张）。两个原因，症状一模一样，都是
  "证书明明在存储里，却报找不到"，而且都出现在同一句会误导人的错误里：
  其一，写进 Kestrel 的配置缺 `AllowInvalid`——它查证书时默认只认链验证通过的，而自签名证书在**任何**机器上
  都过不了（本机也没把它装进受信任根）；其二，`Subject` 写成了带 `CN=` 的可分辨名称，而 Kestrel 是按**简单名**
  查的，带前缀永远查不到。客户端信不信任是客户端的事，服务端得先能把证书拿出来。
  **注意**：这条路径要写本机证书存储，覆盖它的测试在未提权时会跳过，所以 CI 覆盖不到——它是靠一次真实的
  安装式配置加上一次真实的 TLS 连接验出来的。
- [Server] 安装包的 MSI 代码页设为 936：向导页是这个包里第一处**存在于 MSI 表内的中文**（此前的中文都在
  许可 RTF 文件里），默认的 1252 编码装不下它。

## [0.2.1] - 2026-10-11 · 连接可靠性

### Fixed

- [Transport] **同一台机器上的两台设备现在有一条可靠的路**：候选地址里**无条件**加上回环地址（排在最后）。
  此前回环只会在反射器答上来、且它报回来的恰好就是回环时才出现；于是"网卡地址不可用"（CI runner 上防火墙
  挡入站 UDP、虚拟网卡没有路由）叠加"反射器超时"，就只剩中继一条路——中继再失败就彻底连不上。2026-10-11 的
  发布流水线正是这么红的：一个只做连接的测试报了 `No network path to the remote device worked.`，
  本地连跑 30 次都复现不出来。
  已用实验证实（把网卡地址换成不可达、两端都不采用反射结果、中继也指向不可达）：关掉这条修复时 8 个测试
  失败 7 个，打开后 6 个通过——剩下 2 个是强制走中继的，实验里它们本来就该失败。
- [Transport] **连不上时说明试过什么**：`PeerConnectException` 的消息现在带上反射器是否答复、每个候选地址的
  失败原因（超时秒数或具体错误），以及中继是否被提供/是否认领了绑定。例如
  `No network path ... (the reflector did not report a public endpoint). Tried: direct 192.0.2.1:62572 did not
  answer within 2s; relay 192.0.2.9:5082 did not acknowledge the bind.`
  此前只有一句"没有可用的路径"，线上或 CI 里出现时无从判断是候选错了、反射器没答还是中继没理。
  单次候选尝试的日志同时从 Debug 提到 Information（连不上的第一手材料），界面不受影响——客户端按
  `Failure` 枚举显示自己的中文句子，不显示这句话。
- [Transport] 中继绑定（两步 UDP 握手，所有直连都失败后的最后一条路）失败后会**重试一次**：过去丢一个包
  就等于这条路不存在。重试只在已经失败的路径上多花一次超时。

### Changed

- [Build] **测试运行方式迁到 Microsoft.Testing.Platform**（`global.json` 里加 `test.runner`）：xunit.v3 4.x 带来的
  MTP 在 .NET 10 SDK 上**不再支持 VSTest 目标**，不迁的话 `dotnet test` 直接报错（那正是两个 xunit 依赖 PR
  一直红着的原因，不是升级本身有问题）。CI 的 TRX 改由 `Microsoft.Testing.Extensions.TrxReport` 产出
  （`--report-trx`），文件名与格式没变，测试汇总表照旧解析（已用真实 TRX 验过）。
  两个安装包脚本改用 `--project` + `--filter-class <完全限定类名>`：新的 `dotnet test` 不认位置参数给的目录，
  也不认 `--nologo`（会被转发给测试程序，后者以退出码 5 拒绝整次运行）。
- [Build] xunit.v3 3.2.2 → 4.0.2、xunit.runner.visualstudio 3.1.5 → 4.0.1。迁移之外，测试代码一行没改。

### Added

- [Build] `Microsoft.Testing.Extensions.TrxReport` 2.5.1：TRX 报告是 MTP 的扩展而不是平台本身，没有它测试程序
  会拒绝 `--report-trx`（退出码 5），而 CI 的测试汇总正是从 TRX 里读的。

## [0.2.0] - 2026-10-11 · 公网发布准备

### Security

- [Client] **修复提权命令注入**：修改服务器地址不再经过 PowerShell，改为把参数作为独立 argv 交给服务程序
  （`HostServiceSetup`），地址先经严格解析。此前一个伪装成服务器地址的字符串能以管理员权限执行。
- [Protocol] **密码证明改用 PBKDF2 派生密钥**（60 万轮，nonce 作盐），**临时密码从 8 位加长到 12 位**
  （约 2^40 → 2^60）。两者合起来让"拿到证明后离线猜口令"从几天变成不现实；这仍不是 PAKE，
  固定密码仍需要它，见 `docs/design/README.md`。协议主版本随之升到 2，新旧版本会明确报"版本不兼容"。
- [Server] **注册与连接请求限流、连接配额**：单地址注册速度、单设备连接请求速度、挂起请求上限、
  同时连接数与单地址连接数（超出的在 WebSocket 升级前拒绝）。身份是这台服务器上所有限额的计量单位，
  所以注册限流是让其余限额真正有效的那一条。
- [Server] **中继绑定需要令牌**：中继先向来源地址发一个与票据、角色、地址、时间窗绑定的令牌，带回来才生效。
  这堵住了"拿票据把中继指向别人地址"的隐藏来源发包路径；另加中继会话数上限与角色迁移日志。
- [Server] **`X-Forwarded-For` 支持**（`Rendezvous:TrustedProxies`）：反向代理后按真实客户端地址限流，
  不配则只信本机回环。
- [Host] **固定密码下限由服务端强制**（此前只在界面上检查），**同时会话数上限 16**，
  **临时密码 30 分钟后自动更换**（固定密码不过期）。
- [Platform] 密钥文件改为原子写入：写坏不再让设备静默变成"另一台设备"。
- [Host] **配置读不出来时说明原因**：服务把原因写进 `logs\startup-error.log` 并以退出码 3 结束（此前是启动前的
  未处理异常——服务"就是不在了"，事件日志里只有运行时的一条记录）；客户端写进 `crash.log` 并弹出对话框，
  不再是一个不声不响没启动的程序。
- [Host] **文件传输残片会自动清理**：被控端把未完成的 `.rdpart` 记进清单，启动时删掉超过 7 天没人续传的。
- [Transport] 流尺寸上限收紧到 8K（此前 16384×16384 可让控制端单次分配 1 GiB）；
  信令给的设备号不合法时不再产生无人观察的异常。

### Added

- [Server] **`/metrics`**（Prometheus 文本格式，默认关闭，`Rendezvous:MetricsEnabled`）。
- [Server] `/health` 现在真的检查数据库（配置了 PostgreSQL 时）。
- [Build] 打包脚本在许可文本仍含占位符时**拒绝出包**（`-AllowDraftLicense` 可显式跳过）。这道检查现在放在最前面
  （和证书路径检查在一起），所以标签触发的流水线在第一秒就停下，而不是白跑几分钟发布之后才失败。
- [Build] **内网 TLS 的证书路子**：`build/signing/New-ServerCertificate.ps1` 生成服务器证书（名字与地址都进
  SAN），`build-installer.ps1 -ServerCertificate` 把它的公钥部分打进安装包并装进受信任根，于是内网也能跑
  `wss://` 而不需要公网 CA。已装好的机器可用 `Import-Certificate` 单独补。
- [Server] **服务端安装包**（Windows）：`build/installer/server/windows/build-server-installer.ps1` 产出
  `RemoteDesktop-Server-<版本>-x64.msi`，把信令与中继两个服务器装到 `%ProgramFiles%\Remote Desktop Server`，
  注册成两个自动启动、崩溃后重启的服务，加三条只针对这两个程序的入站防火墙规则（可用 `NOFIREWALL=1` 关掉），
  并写好两份 appsettings.json。改用安装包后不再需要手工 `dotnet publish` + `sc.exe create` + `netsh`。
  安装时可传 `RENDEZVOUSURL` / `RELAYPUBLICENDPOINT` / `RELAYSHAREDKEY` / `POSTGRESCONNECTIONSTRING` /
  `TLSCERTIFICATEPATH` / `TLSCERTIFICATEPASSWORD`；中继密钥默认在安装时生成，重装或升级不会更换。
  安装包**不装数据库**：没有 PostgreSQL 时设备代码只在内存里，服务器一重启就全变——这一点在安装输出和
  文档里都写明。配置步骤把做了什么写在 `server-setup.log` 里，因为安装程序不会给任何人看它的输出。
- [Server] `Rendezvous:RelayEndpoint` 那条报错信息原来写着"must look like host:port"，而它其实只接受 IP
  （客户端拿到就直接发 UDP）。报错信息改对了，部署文档和 compose 里那个会**让容器起不来**的示例
  （`rd.example.com:5082`）也换成了 IP。
- [Docs] `THIRD-PARTY-NOTICES.md` 与生成脚本 `build/Generate-ThirdPartyNotices.ps1`。
- [CI] 依赖漏洞审计（含传递依赖）、Dependabot、发布前跑测试。
- [CI] 打 Tag 出的 Release 现在**同时带客户端与服务端两个安装包**（`RemoteDesktop-Server-Windows-x64-<tag>.msi`），
  不再只发客户端——服务端安装包此前只能在本地构建。
- [Docs] 新增系统架构设计与网络通信协议文档（现在合并在 `docs/design/README.md`）。
- [Docs] 新增网络通信协议规范文档（现在合并在 `docs/design/README.md`）。
- [Docs] 新增文档中心索引与贡献指南 (`CONTRIBUTING.md`)。
- [Docs] 部署文档新增"生产环境：信令必须走 TLS"（自带证书或反向代理两种做法）与"公网上的限额"两节。
- [Installer] 引入基于 WiX v5 的 Windows 安装包打包脚本。

### Changed

- [Build] **许可文本从草案变成正式文本**：著作权人 Zeqian GAO、项目主页为联系方式、适用中国法律、
  争议提交作者住所地有管辖权的人民法院；开头那段"本文件是草案、请替换方括号"的说明也删掉了——它原本会被
  转成 RTF **印在每个用户的许可页上**。打包脚本"有占位符就拒绝出包"的闸门保留，留给将来换主体发布时用。
- [CI] **发布时不再要求必须配置服务器地址**：没配就出一个不内置地址的客户端包（默认连本机，装完在客户端里
  用「修改服务器地址」改），并在 Release 说明最前面写明这一版是哪种。本软件公网和内网都要用，地址往往是
  部署时才定的，"不内置"是正常选择而不是漏配；客户端连不上时界面本来就会显示当前地址和怎么改。
- [Build] **构建目录按"角色→平台"重组**，为后面 macOS / Linux 的安装包让路。此前 `installer/windows/` 是按平台
  命名的（装的却是客户端/被控端那一套），`installer/server/` 是按角色命名的——两个轴混在一起，第二个平台一到
  就说不清"macOS 的客户端包放哪儿"。现在：
  `installer/client/windows/`、`installer/server/windows/`；许可文本与它的 RTF 生成脚本提到 `installer/`
  共用（服务端包不再伸手进客户端包拿 EULA）；`service/windows/`（sc.exe 那套只在 Windows 有意义）；
  `signing/windows/`（`New-SelfSignedCertificate` 与证书存储都是 Windows 专有），跨平台的
  `New-ServerCertificate.ps1` 留在 `signing/` 根下——它用纯 .NET API，在 Linux/macOS 的 pwsh 上同样能跑，
  服务端将来在那边部署时 Kestrel 用的就是它生成的证书。
- [Build] `Generate-ThirdPartyNotices.ps1` 改为向 SDK 询问 NuGet 包目录（`dotnet nuget locals global-packages`），
  不再用 `$env:USERPROFILE` 拼路径——那个变量在 Linux/macOS 上不存在，会让整张许可表退化成"见项目主页"。
  顺带跟随 `NUGET_PACKAGES`（CI 用它挪缓存）。
- [Build] Docker Compose 不再内置数据库口令（未设置即拒绝启动），加资源上限与 `no-new-privileges`。
- [Build] 安装包摘要不再声称"中英双语界面"（界面目前只有中文）。
- [Client] 侧栏版本号改为读程序集版本，不再硬编码在界面文件里。
- [Client] "数据与诊断"新增**本机控制端代码**：这台机器连出去时用的身份（和主页显示的被控端代码不是同一个），
  别人控制本机时他那边的会话里显示的就是它，用来回答"谁在连我"。
- [Repo] 优化项目文档组织结构，统一采用 Kebab-case 命名并提供全局导航。

### Fixed

- [Build] **Windows PowerShell 5.1 下构建脚本里的中文是乱码**：含中文的 `.ps1` 是**无 BOM** 的 UTF-8，而 5.1 会按
  GBK 解码——提示信息变乱码是轻的，重则脚本压根解析不了（`TerminatorExpectedAtEndOfString`）；更要紧的是
  许可占位符那个正则 `'【[^】]+】'` 会**静默匹配不到任何东西**，闸门等于不存在（脚本注释里却写着"用 Windows
  自带的 PowerShell 跑发布构建也要能work"）。`build/` 下 10 个脚本都补上了 UTF-8 BOM，5.1 与 pwsh 7 现在都正常。
- [Installer] **客户端安装包现在真的配置了服务崩溃重启**：`ServiceInstall` 本身不含失败动作，此前只有按
  deployment.md 用 `sc.exe failure` 手工安装那条路才有——而 README 已经把它当成安装包的行为写着了。
  现在安装即生效：崩溃后 5 秒重启，连续三次后不再自动重启，稳定运行一天后重新计数。README 也改成与代码一致。
  包内容测试会检查这一行，缺了就构建失败。

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
