# 目录结构评审（第三轮 · 全量普查）

- **评审日期**：2026-10-16
- **评审对象**：分支 `chore/repo-structure`（起点 `4f14524`，及其上的本轮修复）
- **评审范围**：仓库根、`src/`、`tests/`、`build/`、`native/`、`docs/`、`.github/`，以及全部产物目录
  （`artifacts/`、`TestResults/`、工程内残留的 `bin/`/`obj/`）
- **评审依据**：跨平台 .NET 单体仓库通行约定，加上本项目自己在 `CONTRIBUTING.md` 与 `build/README.md`
  里声明的约定——**含对约定本身的质疑**（见第三、五节）
- **与前两轮的关系**：第一、二轮的正文原样保留，本文件不重复它们的结论，只记录本轮新看到的东西

---

## 一、这一轮为什么换了做法

前两轮是**抽检**：挑几类问题看，改掉，再挑几类。所以每轮都能发现上一轮没看的地方——第一轮漏了 `build/`
的分类轴，第二轮漏了产物目录本身。

这一轮改成**普查**：`git ls-files` 全量 566 个文件逐个过名字与位置；产物目录在磁盘上量真实形状（那部分
读 diff 和读代码都看不见，只能看真机）。

但普查本身不是解法。把三轮的发现并排看，病因很清楚：

> **分层约定有测试强制，所以它从没坏过；其余约定只写在文档里，所以每轮都能挑出新的违反。**

`RemoteDesktop.Architecture.UnitTests` 从三平台移植开始就在逐层校验依赖方向，那些规则一次没被打破过。
而 `tests/` 命名规则、产物路径、脚本命名同属"写在 `CONTRIBUTING.md` 里的约定"，全无强制——它们只能靠读到
文档的人自觉。这样修完 27 个点，第四轮还会冒第 28 个。

所以本轮的主要产出不是修复清单，而是**把六条约定变成断言**（第六节）。评审范围里的"是否符合规范"因此从
"这次有没有抽查到"变成"跑 `dotnet test` 红不红"。

---

## 二、实测数据

| 项 | 实测 |
|---|---|
| 顶层分层 | 根目录 8 个元文件 + `src`/`tests`/`build`/`native`/`docs`/`.github`，无散落脚本 |
| 产物收敛 | `find src tests native -type d \( -name bin -o -name obj \)` → 空；`artifacts/bin` 27 个项目目录 = 21 产品 + 6 测试，与 `.slnx` 一致 |
| 命名空间 = 目录 | 293 个 `.cs`，0 处漂移；21/21 个 `.csproj` 名 == 目录名 |
| `build/` 分类轴 | `installer/{windows,macos,linux}/{client,server,host}`、`service/{linux,macos,windows}` 全部成立 |
| 测试命名 | 6/6 落在 `{UnitTests, IntegrationTests}` 闭集内 |
| 行尾 | 556 `i/lf` + 10 `i/-text`，与 `.gitattributes` 零冲突；13 个 `.sh` 全部 `i/lf w/lf`；12 个无扩展名部署关键文件逐条声明且为 LF |
| 路径可移植性 | 非 ASCII 0 处、大小写冲突 0 处、无空格路径、最长 96 字符 |
| 平台代码分离 | 全仓 **0 个 `#if`**；靠独立工程 + 运行时派发 + `[SupportedOSPlatform]` + CA1416 |
| 未跟踪文件 | 无"未跟踪且未忽略"的文件;每个未跟踪字节都有明确的忽略规则 |
| 构建 / 测试基线（修复前） | `dotnet build` 0 警告 0 错误；`dotnet test` 787 总 / 531 通过 / 256 跳过 / **0 失败** |

> 第一轮记录的"6 个环境性失败"（符号链接权限、缺 `vim`/`nano`/`less`）在本机已不复现，本轮基线是 0 失败。

---

## 三、问题清单

### A. 真缺陷

**A-1 `CopySessionAgent` 在产物收敛后成了静默空操作（本分支引入的回归）**

`src/Hosts/RemoteDesktop.HostService/RemoteDesktop.HostService.csproj` 的 glob 指向
`..\RemoteDesktop.SessionAgent\bin\$(Configuration)\$(TargetFramework)\**\*`，而那条路径已被
`UseArtifactsOutput` 搬走。**MSBuild 对空 item 列表不报错**，`Copy` 静默不做事，构建照样绿。

| 位置 | `RemoteDesktop.SessionAgent.*` 文件数 |
|---|---|
| `artifacts/bin/RemoteDesktop.HostService/release` | **0** |
| `artifacts/bin/RemoteDesktop.HostService/release_win-x64` | **0** |
| `artifacts/app/windows/client` | 4（安装器显式 publish，不受影响） |

后果：目标上方那段注释（"a plain build puts the agent's build output next to it"）已经不成立，而
`build/service/windows/install-service.ps1` 会对一个刚 build 过的人说
`RemoteDesktop.SessionAgent.exe not found ... Publish or build RemoteDesktop.HostService first.`
——给的补救办法是错的。

**A-2 `CONTRIBUTING.md` 写的安装包文件名不存在**

该文档称角色与平台写在文件名里，举的是 `RemoteDesktop-Windows-x64-*` 与 `RemoteDesktop-Server-Windows-*`。
实测 `artifacts/installer/windows/` 里是 `RemoteDesktop-0.3.0.0-x64.msi` 与
`RemoteDesktop.Server-0.3.0.0-x64.msi`——**没有平台词**，客户端那份**连角色词也没有**，服务端还是
`RemoteDesktop.Server`（点）而非 `RemoteDesktop-Server`（连字符）。带 `Windows` 的名字只是发布时重命名的
**发布资产**。`.github/actions/verify-packages/action.yml` 里已经写着这个坑"这个仓库已经踩过一次"——
文档是它会再被踩的第三个地方。

**A-3 文档写的 `artifacts/packages/` SDK 根本不产生**

实测 MSBuild 求值：`PackageOutputPath` = `artifacts/package/debug/`——**单数**，且**没有项目层**
（`bin`/`obj`/`publish` 才是 `<项目>/<pivot>`）。而 `CONTRIBUTING.md` 与 `Directory.Build.props` 的注释都写
`artifacts/packages/`。名字和形状两处都错。一直没暴露是因为仓库里没有任何可打包项目。

**A-4 `docs/ops/deployment.md` 的手工构建路径与打包脚本不一致**

文档教读者往 `artifacts/app/windows/rendezvous` 与 `.../relay` 发布。实际布局是
`artifacts/app/windows/server/Rendezvous` 与 `.../server/Relay`（`build-server-installer.ps1` 用
`-o (Join-Path $PublishDirectory $server.Folder)`）——**层级和大小写都错了**，照文档做会得到和打包脚本不同的
目录树。

**A-5 `DependencyRuleTests` 把 feature 列表写了两遍**

`[InlineData]` 四个 feature，方法体里又写一遍同名的数组。加第 5 个 feature 时只改一处，就会得到"没有测试"
或"`others` 为空、怎么都通过"。这个文件的全部意义就是不可协商，而它自己藏了一个静默失效点。

**A-6 `CONTRIBUTING.md` 的依赖规则与测试实际放行的不一致**

文档写 `Platform.*` 与 `Media`"**只能**依赖 `Abstractions`"；而测试的禁止列表里没有 `Domain`。
实测 `Platform.Linux` 与 `Platform.Windows` **确实引用了 `Domain`**——所以是文档错了，不是代码违规。
（先想到的是收紧测试，一测就发现那样会直接编译失败。）

**A-7 `THIRD-PARTY-NOTICES.md` 在 HEAD 上已经过期**

`Directory.Packages.props` 钉的是 Avalonia 12.1.4 / Tmds.DBus 0.95.1，并有 `FFmpeg.AutoGen` 8.0.0；
而清单里是 12.1.3 / 0.94.1，且**整个缺了 FFmpeg.AutoGen 一项**。有人升了包没重新生成清单。

这与第二轮修掉的 `License.rtf` 是同一类问题——**生成物提交进仓库、又没有防止过期的机制**，于是静默分叉。
第二轮的处理是"不跟踪 + 每次构建重新生成"，这份清单不能照搬（它是发布物的一部分），所以本轮只是重新生成，
**没有加防护**：这是本轮留的口子，见第八节。

**A-8 `icon.png` 与 `icon-256.png` 是同一张图**

两者 md5 相同（`9d2a0d8a…`，53804 字节）。`New-AppIcons.ps1` 把**同一份字节**同时写进这两个名字——
从 `mstsc.exe` 抠出来的就是 256×256，所以"母版"和"256 尺寸"是同一张图的两个名字。

### B. 产物目录

**B-1 `TestResults/` 是仓库根上第二个产物根**

`CONTRIBUTING.md` 声明"所有构建产物都在 `artifacts/`"，但 CI 在四处把 TRX 写到仓库根，且这个目录
**不在类别表里**。它被忽略所以不脏仓库，但"产物在哪"在根上有了两个答案。

**B-2 `artifacts/app/<平台>/` 的下一层不是角色**

文档说 Windows 下是"各角色的 publish 输出（`client/`、`server/`、`host/`）"。实测
`artifacts/app/windows/client/` 里同时是 `RemoteDesktop.Client.exe`、`RemoteDesktop.HostService.exe`、
`RemoteDesktop.SessionAgent.exe`——**三个角色一个目录**（一个 MSI 装三个角色）。而同一份文档的上一行对
`installer/` 明说"角色写在文件名里"。两个并列类别两条相反规则，事实上两条都按文件名走。

**B-3 `artifacts/native/` 与顶层 `native/` 同名不同义**

| | `native/` | `artifacts/native/` |
|---|---|---|
| 是什么 | 手写 Objective-C 源码（15 文件），删了就没了 | 可再生的 `.dylib`/`.so` |
| 方向 | **产出到** `artifacts/native/` | 被 CI、测试、deb 脚本消费 |

相隔一个词，一个删得一个删不得。仓库里已经为同类歧义写过解释性注释，说明它真的费读者时间。

**B-4 `artifacts/installer/windows/` 里混着非交付物**

该类别定义为"**交付物**：安装包"，但里面有 `RemoteDesktop-0.3.0.0-x64.wixpdb`（WiX 的调试符号库）。

**B-5 "只有一个产物根目录"这条规则没说准**

`artifacts/` 下五个类别由 SDK 拥有（`bin`/`obj`/`publish`/`package`/`log`），它们第二层是 `<项目>` 与
`<pivot>`，**不可能**按平台加层；只有自建的几个适用 `<类别>/<平台>/`。文档把两组放在同一张表里讲，读起来
像"加一个平台到处都加一层"。

**B-6 `.gitignore` 的构建产物块已不描述本仓库**

- `bin/x64/`、`obj/x64/`、`[Dd]ebug/x64/` 这类含 `/` 的规则**锚定在仓库根**，实测永远不命中真实路径
  `build/installer/windows/server/CustomActions/bin/x64/...`——它是由宽规则 `**/[Bb]in/*` 兜住的。
  那条"专门为它留的规则"是死的。
- `[Dd]ebug/`、`[Rr]elease/`、`[Ll]og/`、`[Oo]bj/` 都是**无锚定**模式，会忽略树里**任何**同名目录。
  当前 0 个 tracked 文件被误伤（已实测），但将来出现 `docs/design/release/` 这类目录就会被静默吞掉。

### C. 目录名 / 文件名

**C-1 7 个 `.cs` 文件名与所声明的类型不符**（只声明一个类型、名字却是另一个）

`SystemIntegration.cs`（声明 `IOPMAssertionKeepAwake`）、`SystemIntegration.LoginItem.cs`（声明
`MacLoginItem`，且这两个文件**不是** partial 拆分）、`CameraExceptions.cs`（声明 `CameraUnavailableException`）、
`PortalResponse.cs`（声明 `PortalRequestResult`）、`DxgiDisplays.cs`（声明 `DxgiDisplayEnumerator`）、
`tests/.../MacOS.UnitTests/ClipboardTests.cs`（声明 `MacClipboardTests`）、
`tests/.../Transport.IntegrationTests/TransferManagerSessionTests.cs`（声明 `FileTransferSessionTests`）。

**C-2 `build/service/linux/default` 的名字不说明它是什么**

它变成 `/etc/default/remotedesktop-server`。同目录下另三个文件都带 `remotedesktop-*` 前缀，只有它没有。

**C-3 `HOW-TO-CONFIGURE.txt` 是全仓唯一的 SCREAMING-KEBAB 文件名**

其余大写文件名（`README.md`、`CHANGELOG.md`）是工具认定的固定名，`.txt` 不是。

**C-4 `.ps1` 有两套命名（7 个 Verb-Noun，5 个 kebab-case）**

`build/tools/` 与 `build/signing/` 全是 Verb-Noun；`build/installer/windows/` 与 `build/service/windows/`
全是 kebab-case。规则只写在叶子 `build/tools/README.md` 里，整棵树的手册 `build/README.md` 一个字没提。
另外 `Generate-ThirdPartyNotices.ps1` 的 `Generate` **不在** PowerShell 批准动词表上——而 `build/tools/README.md`
恰恰把它当范例列出来。

**C-5 `.github/workflows/release.yml` 的 job 名破坏了 `build-<平台>` 模式**

`ci.yml` 是 `build-windows`/`build-linux`/`build-macos`；`release.yml` 是
`build-linux`/`build-macos`/**`build-and-release`**——Windows 那条腿同时也是聚合者，于是平台身份从名字里
消失了，读 job 列表看不出 Windows 在哪。

### D. 目录组织

**D-1 `build/README.md` 缺它自己规则的几个例外**

- 角色集合三平台不对称（`installer/linux/` 有 `host/`，另两个没有），是产品差异但目录树里看不出来。
- `docker/` 不属它声明的两条轴，也不在它的"本层/子目录"规则表里。

**D-2 `native/` 没有 README**

`build/` 下三个子目录都有，唯独 `native/` 没有——而它恰是形状最需要解释的一棵（为什么没有 `windows/`）。

**D-3 `build/installer/` 的"共用层"里放的是 Windows 专属物**

该层只有 `New-LicenseRtf.ps1`（只被两个 Windows 工程调用）与它生成的、已 gitignore 的 `License.rtf`。

**D-4 两处文档索引重复且已漂移**

根 `README.md` 手工重列了 `docs/README.md` 的子集，已经漏了 4 份文档。两份无人校验的索引必然继续分叉。

**D-5 `docs/` 的 `-plan` / `-review` 后缀无定义**

`docs/README.md` 依赖这个区分（"这两份是当时的评审记录"），但没有一处写明它是什么；三个 `-review` 各指
三样东西（对代码树的安全审计、对分支的评审、对仓库结构的评审）。

**D-6 `src/` 的媒体家族文件夹在三个平台上是三个名字**

| 概念 | Abstractions | Linux | MacOS | Windows |
|---|---|---|---|---|
| 视频编解码 | `Media/` | `Video/` | `Video/` | **`MediaFoundation/`** |
| 视频录制 | `Media/IVideoRecorder.cs` | **`Video/`** | `Recording/` | `Recording/` |

同一个 `IVideoEncoder` 的三个实现分别住在 `Video/`、`Video/`、`MediaFoundation/`。

**D-7 17 个单文件目录**，其中一部分镜像了 `Abstractions` 的端口名（合理），另一部分（如
`Client/Setup/`、`Transport/Identity/`）没有对应概念，只是多了一层。

**D-8 `RemoteDesktop.Core.UnitTests` 的"区域"不等于 `src/Core/`**

它测的是 Core + Features + Media + Protocol + Transport。拿 `tests/` 的项目名去找"某个 Feature 在哪测"会找不到。

### E. 约定本身

**E-1 `build/` 名字里没有构建系统**——构建是 `dotnet build RemoteDesktop.slnx`，由根 `Directory.Build.props`
定义；这棵树装的是打包、部署与仓库维护脚本。

**E-2 平台名的三套拼写**：`src/` 用 `MacOS`（程序集名的一部分）、路径用 `macos`、散文用 `macOS`。

**E-3 "分类桶"写法没有写下来**——一个文件承载"一个概念的接口族与它的 DTO"（`IAudio.cs` 里没有 `IAudio`
类型，`FileModel.cs` 里没有 `FileModel`）是允许的，但没有任何地方说明它允许。

> 关于散文里的 `MacOS`：本轮**没有**把它当作问题。抽样后发现绝大多数是 `Contents/MacOS/`（`.app` bundle 里
> 真实存在的目录名）、`AddMacOSPlatform`、`OperatingSystem.IsMacOS()` 这类标识符或路径，62 处里有 41 处本来
> 就在反引号内。**改掉会破坏文档事实**，所以不动。

---

## 四、做了什么

按"一次提交只做一类事"分组。

**回归与产物（第 1-5 项）**

1. `CopySessionAgent` 改为用 `<MSBuild Targets="GetTargetPath">` 向 SessionAgent 工程要输出目录，
   不再手写 `bin\<Configuration>\<TFM>\`。该工程本来就有一个只为构建顺序存在的 `ProjectReference`，
   `GetTargetPath` 不触发构建，且对布局零假设。
2. TRX 迁到 `artifacts/test-results/<平台>/`；`run-test-suite` action 的四处同步。上传与摘要用
   `artifacts/test-results/*/*.trx` 通配，**不用** `${{ runner.os }}`（那是 `Linux`/`macOS` 大小写，
   在 Linux runner 上匹配不到小写目录名）。`.gitignore` 的 `[Tt]est[Rr]esult*/` 保留——实测裸跑
   `dotnet test --report-trx` 确实仍在根上建它。
3. `artifacts/native/` → `artifacts/lib/`。实测 20 处引用：9 处代码/CI/脚本/文档改掉，
   5 处在 `docs/ports/*` 里**保持原样**（那是当时的实测记录，改了就是篡改证据）。
4. `.wixpdb` 用 `-p:PdbOutputDir=obj\<Configuration>\` 留在工程 `obj/` 里，不进交付物目录。
   用 `PdbOutputDir` 而不是 `SuppressPdbOutput`：符号文件对读用户机的崩溃报告有用，不该丢。
5. `.gitignore` 构建产物块：删掉永不适用的根锚定规则，给 `build/installer/windows/*/{bin,obj}/` 一条
   锚定真实路径的规则，让忽略规则**描述布局**而不是靠宽通配兜底。

**文档与名字（第 6-18 项）**

6. `CONTRIBUTING.md` 产物表：`packages/` → `package/<pivot>/`（并注明单数与无项目层是 SDK 定的）；
   安装包文件名改成实际的两个；`app/<平台>/` 的下一层定义为**交付物**；补 `test-results/` 与 `lib/` 两行；
   补一句"两组不是同一条规则"。
7. `docs/ops/deployment.md` 的手工构建路径改为 `server/Rendezvous`、`server/Relay`。
8. `CONTRIBUTING.md` 的依赖规则改为"只能依赖 `Abstractions` **与** `Domain`"，与测试一致。
9. `DependencyRuleTests` 的 feature 列表只留一处，`[InlineData]` 换成从它派生的 `MemberData`。
10. `TransferManagerSessionTests.cs` → `FileTransferSessionTests.Transfer.cs`（沿用仓库已有的
    `TerminalEmulator.Parser.cs` 那套 partial 拆分的点号后缀写法）。
11. `README.md` 与 `docs/design/architecture.md` 的两处分层图对齐：`Features` 与 `Hosts` 是**并列**的，
    都是只依赖 `Application`，不是一个接一个。
12. `build/README.md`：首行声明"本目录不是构建系统"；补三平台角色集合不对称的表；补 `docker/` 是第三条轴；
    补脚本命名规则（从 `build/tools/README.md` 提升上来）；修正"安装器产物仍在工程内"这句只说对一半的话。
13. 新建 `native/README.md`：放什么、为什么没有 `windows/`、`rd_` 前缀规则、产物落 `artifacts/lib/<平台>/`。
14. `docs/README.md` 定义 `-plan` / `-review`，并写明之后用日期而非序号命名；根 `README.md` 的重复索引
    收成一行，终止分叉。
15. `build/service/linux/default` → `remotedesktop-server.default`；`.gitattributes` 里那条逐名规则
    改成 `*.default` 通配，避免下次再忘。
16. `HOW-TO-CONFIGURE.txt` → `how-to-configure.txt`（两份）。
17. `release.yml` 的 `build-and-release` → `build-windows-and-release`。
18. `icon.png` 去重，生成器只写 `icon-256.png`（应用与 SVG 本来就指向它）。
19. 顺带修 `Directory.Build.props` 注释里过期的"23 个项目"（第二轮改了 `CONTRIBUTING.md` 的同款数字，
    漏了这一处），并重新生成 `THIRD-PARTY-NOTICES.md`（见 A-7）。

**把约定变成断言（第六节）**

20. 新增 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs`，六条断言。

---

## 五、明确没做（及理由）

| 项 | 理由 |
|---|---|
| 5 个安装器 / 服务脚本改 Verb-Noun | 它们的名字出现在约 **80 处**（CI、`.wxs` 注释、`.wixproj` 错误消息、部署文档、测试）。属于独立的一次机械改动，混进来会让本 PR 无法细看。已在断言里**具名豁免**，新脚本照样受约束 |
| `src/` 媒体家族文件夹统一（D-6） | 会动 Windows 平台程序集的编译布局，且 `MediaFoundation/` 里确有纯 vendor 互操作。是判断不是缺陷 |
| 17 个单文件目录（D-7） | 镜像 `Abstractions` 端口名的那些是合理的；逐个判断的成本高于收益 |
| `src/Media/` 单项目区域、`Hosts`/`Server` 合并、`Platform.Posix` 的位置 | 架构决策而非目录卫生，会牵动命名空间与架构测试 |
| 发布资产的架构词统一（`amd64` vs `x64`） | deb 的 `control` 字段必须是 dpkg 的 `amd64`；让文件名与字段不同词更绕 |
| 散文里的 62 处 `MacOS` | 抽样证明绝大多数是正确的标识符与路径引用，改了反而错 |
| `.editorconfig` 补 `.sh`/`.m` 段 | 实测 `[*]` 本来就覆盖它们，**不存在缺口** |
| `**/[Bb]in/*` 与无锚定的 `[Dd]ebug/` 等模式 | 它们是当前唯一真正生效的规则；只收敛了已死的锚定块，没有动它们（B-6 记着这个隐患） |

---

## 九、补记（同日）：把上一节推迟的项做完

第五节的表与第八节那段，是这份评审落笔时的状态。其后又把三件推迟的做完，第四件改成写下来而不是改掉。

**已完成**

1. **`THIRD-PARTY-NOTICES.md` 的过期防护（第八节点名的那项）**。`ci.yml` 的 `build-windows` job 里新增一步：
   重新生成，然后比对，有差异就红，并把 diff 打出来。**只放在一个 job 上**——清单是用不带 RID 的
   `dotnet list package` 读的，三个平台同一份（提交的清单里 `Avalonia.Win32`、`.X11`、`.Native` 同时在列，
   就是证据），跑三次只是多两次可能变红的机会。本地已验两边：干净状态通过，把清单改坏并暂存后失败。

   写这一步时先踩了自己的坑：最初写的是 `if (-not (git diff --quiet ...))`。`--quiet` 不输出任何东西，
   而 PowerShell 在条件里取的是**输出**不是退出码，所以那个条件恒为真，这一步会**在每次 PR 上无脑红**。
   改成读 `$LASTEXITCODE`，并在注释里写明为什么不能写成前者。

2. **5 个安装器 / 服务脚本改 Verb-Noun（C-4）**。改名如下，名字里的角色词保留——它们被
   CI、`.wxs` 注释、`.wixproj` 的错误消息和部署文档引用，检索时能区分开比省一个词重要：

   | 原名 | 新名 |
   |---|---|
   | `build-installer.ps1` | `Build-ClientInstaller.ps1` |
   | `build-server-installer.ps1` | `Build-ServerInstaller.ps1` |
   | `configure-server.ps1` | `Set-ServerConfiguration.ps1`（`Configure` 不在批准动词表上） |
   | `install-service.ps1` | `Install-HostService.ps1`（它装的是**被控端**服务，旧名没说清楚） |
   | `uninstall-service.ps1` | `Uninstall-HostService.ps1` |

   顺带解决了第五轮列过的 `-service` / `-server` 一词之差、角色相反的问题。
   **断言里的豁免清单随之删掉**——这正是那张清单存在的目的，现在 `build/` 下 12 个 `.ps1` 全部受约束。
   `CHANGELOG.md`、两份旧评审与 `docs/ports/*` 里的旧名**保持原样**（历史记录）；
   `Files.generated.wxs` 是生成物，下次构建自己会改。

3. **`.gitignore` 里那组会吞同名目录的宽规则（B-6）**。13 条无锚定模式砍到 2 条（`[Oo]bj/` 与
   `**/[Bb]in/*`，它们是唯一真的会命中的），锚定的例外规则从 `build/installer/windows/*/{bin,obj}/`
   扩成 `**/{bin,obj}/` 以盖住深一层的 `CustomActions`。

   验收标准是**被忽略的集合一字未变**：删改前后各跑一次 `git status --ignored --porcelain
   --untracked-files=all`，6019 条路径逐行相同——也就是说那 11 条模式今天一条都没在干活，只是站在那里，
   等着 `src/` 或 `docs/` 下第一次出现叫 `Log/`、`Release/` 的目录时把它静默吞掉。

**改成写下来、不改名，其后又按指示把角色词对齐了**

4. **发布资产的架构词（`amd64` vs `x64`）**。上一节只说了"不改"，这一步把它变成显式约定：`CONTRIBUTING.md`
   的产物布局一节现在有一张三平台资产名对照表和成因说明。写的时候核对了三个打包脚本与 `release.yml`
   的改名步骤，发现**最终资产名里角色一律用连字符**（`RemoteDesktop-Server-`），只有 Windows 的**源** MSI
   用点（`RemoteDesktop.Server-`）——我原先在表里写成了点，核对后改正。

4b. **随后把角色词对齐了**（`release.yml` 两处 + 三处文档）。客户端资产此前只在 Linux 上带 `Client`，
   现在三个平台都带，七个资产的形状统一为 `<产品>-<角色>-<平台>-<架构>-<tag>.<扩展名>`：

   | | 改前 | 改后 |
   |---|---|---|
   | Windows 客户端 | `RemoteDesktop-Windows-x64-<tag>.msi` | `RemoteDesktop-Client-Windows-x64-<tag>.msi` |
   | macOS 客户端 | `RemoteDesktop-macOS-arm64-<tag>.pkg` | `RemoteDesktop-Client-macOS-arm64-<tag>.pkg` |

   **架构词保持不同**（Windows `x64` / macOS `arm64` / Linux `amd64`）。这不是漏改：`release.yml` 里原本就
   写着"各随各的约定，不为了让列表整齐而互相将就"，而 Linux 那个词与 `.deb` 内的 `Architecture` 字段同源
   （`build/installer/linux/*/control`），换成 `x64` 会让文件名与字段不同词。那两条判据的差别是：**角色词
   是产品自己的约定，可以统一；架构词是各平台包格式的约定，统一了反而失真。**

   注意**产物名没有跟着改**（`artifacts/installer/windows/` 里仍是 `RemoteDesktop-<版本>-x64.msi`），所以
   `verify-packages` 的三组分平台 glob 与 `release.yml` 找产物的两处 glob 都不受影响。产物名与资产名不一致
   这件事本身仍然存在，`CONTRIBUTING.md` 与 `verify-packages` 的注释都写着——那是这个仓库踩过的坑。

**回头看时补上的两条**

这一节写完后把第一到第四节的清单逐条对了一遍现状，发现 D-3 与 D-8 被我列进了问题清单却在"做了什么"里没有对应项——
漏了，不是有意推迟。两条都是纯文档：

6. **D-3 `build/installer/` 本层的描述不准**。`build/README.md` 的规则表把它写成"client 与 server 共用的**许可文本**及其
   生成脚本"。实情是：许可文本是仓库根的 `LICENSE`，macOS 的 `.pkg` 直接拷那一份、根本不经过这个脚本；这一层的
   `New-LicenseRtf.ps1` 只被 **Windows** 用（RTF 是 WiX 许可页的格式）。现在表里如实写了，并补一句说明它是
   "本层 = 共用"这条判据里唯一的窄例——它满足"两个角色"但不满足"两个平台"。
   同时把三个**写进源码树**的生成物（`License.rtf`、两份 `Files.generated.wxs`）列入"产物落在哪里"的例外表，
   附上它们不能挪走的原因（WiX 按 `..\..\License.rtf` 相对路径读后者之一；WiX SDK 编译工程文件夹下所有 `*.wxs`，
   所以另一个必须生成在工程文件夹里）。

7. **D-8 测试项目名里的"区域"没有定义**。`CONTRIBUTING.md` 只说 `<区域>.<种类>Tests`，没说"区域"是什么；而
   `RemoteDesktop.Core.UnitTests` 覆盖的是 Core **加 Features 与 Media**（`ProjectReference` 里就是
   `Application`、`Features.{Desktop,FileTransfer,Terminal}`、`Media`、`Server.Shared`），拿 `tests/` 的项目名
   去找"某个功能在哪测"会找不到。现在补了一张四行对照表，并写明默认规则：功能层单测放 `Core.UnitTests`，
   平台专有的才进对应平台项目，需要真网络/真进程的才进 `IntegrationTests`。

**仍未做，且成本比上一节估的高**

5. **`src/` 媒体家族文件夹统一（D-6）**。上一节写它"会动 Windows 平台程序集的编译布局"，去查了一下，
   实情更重：**这个仓库的命名空间跟着文件夹走**（`MediaFoundation/MfRuntime.cs` 的命名空间就是
   `RemoteDesktop.Platform.Windows.MediaFoundation`）。所以那不是"挪 8 个文件"，而是"挪文件 + 改命名空间 +
   改所有引用 + 改测试"的级联，且三平台都要验。它本身不是缺陷，是判断——所以仍然不做，但理由从
   "会动编译布局"更正为"命名空间级联"。

   其余未做的见上一节的表，理由不变。

---

## 六、本轮的主要产出：六条断言

`RepositoryStructureTests` 把下列约定从"文档里的话"变成"构建会红"：

| 断言 | 此前靠什么保证 |
|---|---|
| `tests/` 项目名匹配 `^RemoteDesktop\..+\.(Unit\|Integration)Tests$` | 无——文档自己说漏掉是静默的 |
| 项目目录名 == `.csproj` 名 | 无 |
| `.cs` 文件名要么是它声明的类型，要么是它分组的概念（此时必须声明 ≥2 个类型） | 无 |
| `.sh` 全 kebab-case | 无 |
| `.ps1` 全 Verb-Noun 且动词在批准表上（具名豁免 5 个待改名的） | 只写在 `build/tools/README.md` 里 |
| `build/` 下只出现平台名与角色名 | 无 |

第三条的判据值得记一笔：不是"文件名必须是类型名"，而是**"名字不是类型时，它必须是个分组"**。这样
`IAudio.cs`（音频端口的整组契约）与 `FileModel.cs` 这类写法被允许，而只声明一个类型、名字却是另一个的
单件文件会被拦下。七条真漂移就是这条规则找出来的（第五节的其余候选经检查是分组，不报）。

**断言确实会红**：加进去时它们立刻报了 7 个 `.cs` 与 1 个 `.ps1`，修完才转绿——不是先改文件再写一条永远绿的
测试。

---

## 七、验证

| 项 | 结果 |
|---|---|
| `dotnet build RemoteDesktop.slnx -c Release` | **0 警告 0 错误** |
| `dotnet test RemoteDesktop.slnx -c Release` | **793 总 / 537 通过 / 256 跳过 / 0 失败**（修复前 787/531/256/0；+6 为本轮新增断言） |
| A-1 的直接证据 | 强制重建后 `artifacts/bin/RemoteDesktop.HostService/release/` 出现 5 个 `RemoteDesktop.SessionAgent.*`（修复前为 0） |
| B-4 的直接证据 | 重建客户端 MSI 后 `artifacts/installer/windows/` **只剩 `.msi`**，`.wixpdb` 在 `build/installer/windows/client/obj/Release/` |
| 产物收敛 | `find src tests native -type d \( -name bin -o -name obj \)` → 空 |
| 忽略规则 | 无 tracked 文件变成被忽略；无"未跟踪且未忽略"的文件 |
| 行尾 | 被改动的文件逐个核对 `git ls-files --eol`，与 `.gitattributes` 声明一致（期间发现并修回了两处被编辑器改成 LF 的 `.csproj`） |
| 13 个 `.sh` | `bash -n` 全部通过 |
| 大小写冲突 | `git ls-files \| tr 'A-Z' 'a-z' \| sort \| uniq -d` → 空 |

**本机无法验证、需 CI 实跑确认的**：三个平台 job 的 TRX 上传路径与上传 glob；`artifacts/lib/<平台>/`
在 macOS/Linux 上的产出；三平台打包链路。合入前应在 `chore/repo-structure` 上跑一次全绿。

---

## 八、留给下一轮的

**本轮该加而没加的防护（A-7）**：`THIRD-PARTY-NOTICES.md` 是提交进仓库的生成物，已经静默过期过一次
（缺了 `FFmpeg.AutoGen` 整个一项）。`License.rtf` 那次的解法是"不跟踪"，它不适用——清单是发布物的一部分。
可行的做法是加一个 CI 检查：重新生成后 `git diff --exit-code`，非空即红。**本轮没做**，因为这会多出一个需要
三平台验证的 CI 步骤，而本轮已经改动了测试上传路径。**这是本轮最应该被下一轮先接手的项。**

其余已知但未处理的：第五节的整张表；B-6 里那两条会吞同名目录的宽规则（当前无害，但没人拦着它变有害）。

---

## 十、由读者发现的一处"已修"其实是半修

评审者复核 workspace 时发现仓库根上又出现了 `TestResults/`，而这一节原本把它记作已修。

**事实**：第四节的第 2 项改掉的是 **TRX 文件的落点**（CI 的 `run-test-suite` 现在把报告写进
`artifacts/test-results/<平台>/`），而**目录本身**还会被建出来。仓库里有三处 `dotnet test` 没传
`--results-directory`，只要传了 `--report-trx` 之外什么都不传，Microsoft.Testing.Platform 就会在当前目录建一个
空的 `TestResults/`：

| 位置 | 谁在跑 |
|---|---|
| `.github/actions/verify-packages/action.yml` | Windows 分支复核两个 MSI 时 |
| `build/installer/windows/client/Build-ClientInstaller.ps1` | 打完客户端 MSI 后自查 |
| `build/installer/windows/server/Build-ServerInstaller.ps1` | 打完服务端 MSI 后自查 |

所以"根上不再有第二个产物根"这句话当时只说对了一半。**另一半是空目录**，而被 `.gitignore` 的
`[Tt]est[Rr]esult*/` 挡着，`git status` 里看不见——这条忽略规则本身是对的（开发者裸跑
`dotnet test --report-trx` 仍需要它），但它同时把这次的不完整也一起遮住了。

**已修**：三处都补上 `--results-directory`，两个脚本指向 `artifacts\test-results\windows`（它们是 Windows 专用），
CI 那处指向 `artifacts/test-results/windows`。实测两条形式（bash 形式与 PowerShell 的 `Join-Path` 形式）都不再
在根上建目录，报告落在 `artifacts/` 下。

**还有第四处，而且修不掉**：`CONTRIBUTING.md` 教每个贡献者跑的那条 `dotnet test RemoteDesktop.slnx`
（不带 `--results-directory`）同样会建它——实测确认。这不是仓库的脚本，是 Microsoft.Testing.Platform 的默认值，
任何人都能敲出来。

可以试试把它也改掉：MTP 的 MSBuild 包里有个 `TestingPlatformCommandLineArguments` 属性，能把参数烤进测试程序。
**没采用**，理由是风险大于收益：烤进去的参数与 CI 显式传的那个是同一个选项，MTP 遇到重复选项很可能直接报错，
而 CI 的正确性比一个空的本地目录重要；另外它会让每一次 `dotnet test` 行为都变，读者无法从命令看出原因。
所以这条的结论是**接受 SDK 的默认值**，并在 `CONTRIBUTING.md` 的测试一节写明：仓库的脚本与 CI 都写进
`artifacts/test-results/`，你自己裸跑时 SDK 会在仓库根建一个空的 `TestResults/`，已被忽略，不必处理。

**为什么这一节值得单独写**：它是这份评审里唯一一处"清单上打了勾、实际只做了一半"的项，而且是被读者而不是被
自查发现的。上一节记着"逐条对了一遍现状"——那次对的是**问题清单**，对的依据是"我改过相关文件"，而不是
"我复现过现象"。补上的那两条（D-3、D-8）是同一类错误的温和版本：凭"我提到过这个文件"当作"我处理了这件事"。
以后这类结论应当以**可复现的观察**为准，而不是以改动过哪些文件为准。
