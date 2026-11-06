# build/ — 打包、部署与仓库维护脚本

**本目录不是构建系统。** 构建是 `dotnet build RemoteDesktop.slnx`，由根目录的 `Directory.Build.props` 与
`Directory.Packages.props` 定义——这里没有任何参与其中的东西：没有 Makefile，也没有把所有东西串起来的聚合脚本。CI 与发布流程会按需**逐个**调用这里的
打包脚本（`installer/` 下的 `Build-*Installer.ps1`、`build-deb.sh`、`make-pkg.sh`，以及 `tools/` 与 `version.sh`），
但它们是独立的打包步骤，不是构建的一部分。
它叫 `build/`，因为装的是"跑一下就能产出交付物"的脚本；冲着"构建在哪定义"进来的，请回根目录。

## 这个目录怎么分层

两条轴，顺序固定：

1. **平台在最外面一层**（`windows` / `macos` / `linux`）。一个脚本能不能在这台机器上运行、一个包能不能在这里
   被构建，都是平台说了算，所以平台是第一个要回答的问题。
2. **角色在平台里面一层**（`client` / `server` / `host`）。同一个平台上的三个角色是三件不同的交付物，但它们
   面对的是同一台机器、同一套工具链。

```text
build/
├─ installer/<平台>/<角色>/    打包脚本与安装包工程（MSI / .pkg / .deb）
├─ service/<平台>/             服务定义：systemd unit、launchd plist、Windows 服务的安装卸载脚本
├─ signing/                    代码签名：本目录放跨平台脚本，windows/ 放只在 Windows 上有意义的
├─ docker/                     服务端容器镜像
└─ tools/                      仓库维护脚本（与平台无关，见下"tools/"一节）
```

`signing/` 的签名与公证流程、CI 需要的 Secrets 与变量写在 [`signing/README.md`](signing/README.md)。

平台名取 `windows` / `macos` / `linux`——是**平台**，不是发行版也不是包格式。`.deb` 因此归在
`installer/linux/` 而不是 `installer/debian/`，和 `artifacts/installer/<平台>/` 的取法一致（见 `CONTRIBUTING.md`
的“构建产物布局”一节）。

三平台的**角色集合是一样的**：`client`（控制端）、`host`（被控端）、`server`（Rendezvous + Relay + Ops + Gateway）。
角色目录仍然只为该平台真实存在的东西而建，三个平台都有这三样：

| 平台 | `installer/` 下的角色 | 被控端包与控制端包的关系 |
|---|---|---|
| Linux | `client`、`host`、`server` | 两个独立的程序与包：`RemoteDesktop.Client` 与 `RemoteDesktop.HostService` |
| Windows | `client`、`host`、`server` | 客户端 MSI 也带着被控端服务与 Agent；`host` 那个只装这两样，没有客户端窗口 |
| macOS | `client`、`host`、`server` | 两个独立组件：`Remote Desktop.app`（完整客户端，在 `/Applications`）与 `Remote Desktop Host.app`（LSUIElement、无窗口、只被控，藏在 `/Library/Application Support/RemoteDesktop/Host`，不出现在“应用程序”）；host 包另装登录项 |

Windows 的 `host` 走的是 `client/Build-ClientInstaller.ps1 -HostOnly`：服务定义、崩溃重启、防火墙规则与机器清理
步骤只有一份，两个包不会各改一半。两个包共用服务名、不能同机共存，规则见
[`docs/ops/README.md`](../docs/ops/README.md#2-安装)的“安装”一节。
macOS 的 `host/make-pkg.sh` 用 `host/make-app.sh` 组装**独立的** `Remote Desktop Host.app`：里面是同一个
`RemoteDesktop.HostService`，外面是 `LSUIElement` 的空壳，bundle id 是 `com.remotedesktop.host`。它安装到
`/Library/Application Support/RemoteDesktop/Host`，而不是 `/Applications`：没有窗口的组件不应看起来像一个
可以打开的 app，也不应与 client 混淆。不能复用客户端 bundle —— TCC 授权按 bundle 的签名身份记账，而 host
必须能被单独授予屏幕录制/辅助功能，且不能带着控制端 UI。

macOS 的三个包共用一个 **Installer 面板**：源码是 `native/macos/rd_installer_plugin.m`（Objective-C，
`InstallerSection`/`InstallerPane` 代码构建视图，无 nib），由 `native/macos/build-installer-plugin.sh` 按角色
编出 `.bundle`，三个 `make-pkg.sh` 用 `productbuild --plugins` 放进产品归档。它属于 `native/` 而不是
`installer/`，因为它是**要编译的原生代码**，与 `rd_*.m` 放在一起；`installer/macos/<角色>/` 里放该角色的
打包脚本与脚本目录。有一处明确的例外：host 包依赖 `client/` 里的共用物，没有再抽到平台一层——
`host/make-app.sh` 调用 `client/make-icns.sh` 生成图标、用 `client/entitlements.plist` 签名、读
`client/development-identity` 里的开发签名身份；`host/make-pkg.sh` 在组装脚本目录时把
`client/scripts/apply-answers.sh` 拷过去（client 与 host 的答案应用逻辑只有这一份）。

`docker/` 是第三条轴，不在这两条里：它装的是服务端镜像，镜像本身就是跨平台的，也不按角色分。镜像有四个
（信令、中继、监控台、Ops HTTPS 网关），`docker-compose.yml` 只启动前两个；监控台、网关与信令 TLS 是显式
加载的 overlay：`docker-compose.ops.yml`、`docker-compose.tls.yml`（带一个 Caddyfile，公网 TLS）与
`docker-compose.selfsigned.yml`（信令直接用自签名证书在 5080 上提供 TLS，用于局域网；用法见该文件开头的注释）。上面那张树里
只有 `installer/` 服从 `<平台>/<角色>`；`service/` 只分到平台，`signing/`、`tools/` 与 `docker/` 各有自己的规则，
其中 `docker/` 不分平台也不分角色。

## "本目录"与子目录各放什么

一个分类目录下的**本层放共享的东西，子目录放专属的东西**：

| 位置 | 本层 | 子目录 |
|---|---|---|
| `installer/` | `New-LicenseRtf.ps1`：把仓库根的 `LICENSE` 转成 WiX 许可页要的 RTF；`version.sh`：打包脚本与 CI 共用的版本读取与校验 | `<平台>/<角色>/`：该平台该角色的打包脚本与工程 |
| `installer/<平台>/` 本层 | 平台内跨角色共用：`linux/common.sh`（三个 `build-deb.sh` 共用）、`windows/Test-InstallerUpgrade.ps1`（`ci.yml`、`release.yml` 调用）、`windows/Import-SigningHelpers.ps1`（`client` 与 `server` 两个打包脚本共用的签名与清理函数，被它们点源引入，不能单独运行） | `<角色>/` |
| `service/` | — | `<平台>/`：只有那个平台上的服务管理器读得懂的定义 |
| `signing/` | 跨平台的证书脚本（纯 .NET API，`pwsh` 三平台可跑） | `windows/`：只在 Windows 上成立的两件事 |
| `tools/` | 两个仓库维护脚本，与平台无关，见下 | — |

`installer/` 与 `signing/` 看起来是"脚本和子目录混在一层"，其实不是混放：**本层 = 共用，子目录 = 专属**。
判断标准只有一句——这个文件是被两个以上的角色/平台用到，还是只被一个用到。

`installer/version.sh` 就是这条规则的一个例子：它只有一份，打包脚本与 CI（`release.yml` 的 `check-version` 与 `verify-packages` 这个复合 action）都 source 它。
版本本身仍来自根 `Directory.Build.props` 的 `<Version>`；这个脚本负责空值、三段数字与 MSI 数值上限的检查，
让 macOS/Linux 在本地打包时也得到与 Windows 相同的失败，而不是各自回退到 `0.0.0` 或用另一种解释接受它。
Windows 的脚本直接读 XML 并在 MSI 里补第四段 `.0`；三个平台的最终形态见
[`docs/ops/README.md`](../docs/ops/README.md) “自动化发布到 GitHub Releases”开头的版本表。

`installer/windows/Import-SigningHelpers.ps1` 是同一条规则在平台层的例子，但它**不是脚本**，而是一组函数：
`Find-SigningCertificate`、`Resolve-SigningTool`、`Get-SigningCertificate`、`Read-PfxCertificate`、`Invoke-Signing` 与
`Remove-GeneratedDirectory`。它们由 `client/Build-ClientInstaller.ps1` 与 `server/Build-ServerInstaller.ps1`
共用，不各复制一份（逐字重复时改一份就会漏另一份）。两个打包脚本都用 `. (Join-Path $PSScriptRoot '..\Import-SigningHelpers.ps1')` **点源**（行首的点号加空格
不能少：函数要定义在打包脚本自己的作用域里，才读得到它的 `$CertificateFile`、`$SigningTool` 等参数与 `$signing`）。
文件名用 `Import-` 只是为了满足 Verb-Noun 命名规则。它和两个打包脚本一样是 UTF-8 带 BOM + CRLF（Windows PowerShell 5.1
读无 BOM 的文件会按本地代码页解码）。两个脚本里**写法不同**的函数（`Get-ProductVersion`、`New-FileList`、
`Get-UntrustedCertificateAdvice`）与只有客户端用的 `Export-TrustedRootCertificate` 不在其中，仍留在各自脚本里；
`RepositoryStructureTests` 断言两个脚本不再重复定义上述六个函数。

`installer/New-LicenseRtf.ps1` 是这条判据里的一个窄例（`version.sh` 是标准情形：两个平台、三个角色都用到），
值得说清楚：它**只被 Windows 用**（RTF 是 WiX 许可页的格式），
之所以还放在共用层，是因为它服务的是 client 与 server **两个角色**。许可文本本身不在这一层——它是仓库根的
`LICENSE`，macOS 的 `.pkg` 直接拷那一份（`make-pkg.sh` 里的 `<license file="License.txt"/>`），根本不经过这个脚本。
换句话说："两平台共用的东西"放本层，"两角色共用的东西"也放，这一层收的是后者。

## 脚本怎么命名

按语言各走各的。两种脚本混在同一目录里时这条尤其要守住——不然"这是个 PowerShell 脚本还是个 shell 脚本"
要靠打开文件才知道：

| 语言 | 风格 | 例子 |
|---|---|---|
| shell（`.sh`） | kebab-case | `build-deb.sh`、`make-pkg.sh`、`fetch-libmsquic.sh` |
| PowerShell（`.ps1`） | Verb-Noun PascalCase，动词取自 [PowerShell 的批准动词表](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/approved-verbs-for-windows-powershell-commands) | `New-LicenseRtf.ps1`、`Invoke-SecurityAudit.ps1`、`Export-ThirdPartyNotices.ps1` |

注意 `Generate-` **不**在批准动词表上（所以导出类脚本用 `Export-`），`Build-` 与 `Deploy-` 则只在 PowerShell 7
上算批准。两条命名规则都由 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs` 断言。

## 新增一个平台

加一层 `<平台>/`，不要另起一个和 `installer/`、`service/` 并列的目录。

三个平台的打包脚本都在 `build/installer/<平台>/<角色>/` 下（`.deb` 也在 `installer/linux/` 下，不按发行版另设目录），
加一个平台时没有第二种放法可选。

## 产物落在哪里

不在这里。所有构建与打包产物都写到仓库根的 `artifacts/`，按 `<类别>/<平台>/` 组织；布局，以及
`installer/windows/` 下 WiX 与 net48 `CustomActions` 工程因自带空 `Directory.Build.props` 而把中间产物留在工程目录的
例外，见 [`CONTRIBUTING.md`](../CONTRIBUTING.md#-构建产物布局)。最终产物不留在工程目录：`.msi` 由两个打包脚本用
`-p:OutputPath=` 写进 `artifacts/installer/windows/`，而 `.wixpdb` 是调试符号、不是交付物，被 `-p:PdbOutputDir=`
留在工程的 `obj/` 里，不跟着进交付物目录。

同一类还有三个**生成物是写进这棵源码树**的，都不在 `artifacts/` 下，也都在 `.gitignore` 里——它们由打包脚本每次
构建时重造，不需要提交，也不该提交：

| 文件 | 谁生成 | 为什么留在原地 |
|---|---|---|
| `installer/License.rtf` | `New-LicenseRtf.ps1` | 两个 WiX 工程按相对路径读它（`WixUILicenseRtf`），必须与 `.wxs` 相对位置固定 |
| `installer/windows/{client,server}/Files.generated.wxs` | 两个打包脚本 | 由 publish 输出生成；WiX SDK 编译工程文件夹下**所有** `*.wxs`，所以它必须生成在那里 |
| `installer/windows/**/{obj,bin}/` | 上述 WiX 与 net48 工程 | 见上，`Directory.Build.props` 那条例外 |

## `tools/`：仓库维护脚本

`build/` 下其余文件夹各管一件事（`installer/`、`service/`、`signing/`、`docker/`），都属于**打包或部署某个平台**
的流程。放不进这些抽屉的脚本在 `tools/`：它们维护的是**仓库本身**（第三方许可清单、部署配置自查），
和打哪个平台的包无关。

| 脚本 | 做什么 | 平台 |
|---|---|---|
| `Export-ThirdPartyNotices.ps1` | 从 NuGet 元数据生成根目录的 `THIRD-PARTY-NOTICES.md` | **三平台**（`pwsh`） |
| `Invoke-SecurityAudit.ps1` | 检查一份生产部署的配置是否还停在"开发默认"的不安全状态 | **三平台**（`pwsh`） |

```powershell
# 依赖变化后重新生成第三方许可清单（写入仓库根，不要手改生成物）
pwsh -File build/tools/Export-ThirdPartyNotices.ps1

# 上线前自查一份服务端配置；退出码 0 正常 / 2 有不安全默认值 / 3 路径或参数不可用
# MSI 部署：Program Files 下的是随包出厂默认，实际生效的配置在 %ProgramData%\Remote Desktop Server\<角色>\
pwsh -File build/tools/Invoke-SecurityAudit.ps1 -InstallDirectory "$env:ProgramData\Remote Desktop Server"

# 开发时查发布产物（服务端的发布目录按程序分子目录）
pwsh -File build/tools/Invoke-SecurityAudit.ps1 -SettingsPath artifacts/app/windows/server/Rendezvous/appsettings.json
```

`-InstallDirectory <目录>` 会读 `<目录>\Rendezvous\appsettings.json` 与 `<目录>\Relay\appsettings.json` 两份，
适合手工解压或拷贝出来的部署。对 MSI 装出来的 `C:\Program Files\Remote Desktop Server` 它读到的是**随包出厂的默认值**，
不是实际生效的配置（后者在 `%ProgramData%\Remote Desktop Server\`，升级不会覆盖），所以审 MSI 部署请把 `-InstallDirectory` 指向 `%ProgramData%\Remote Desktop Server`（上面第一条），或用 `-SettingsPath` 指向其中某一份。

`Export-ThirdPartyNotices.ps1` 把清单写在**仓库根**（由脚本自身的 `$PSScriptRoot` 往上找）；
`Invoke-SecurityAudit.ps1` 只读不写，它的路径参数同样以仓库根为基准解析，所以在哪个目录下敲命令都一样。
两者都不调用平台专有 API，所以在 Linux 或 macOS 上可以用 `pwsh` 直接跑，审那边的服务端部署
（配置在 `/etc/remotedesktop-server/` 下）。

客户端图标不在这张表里：`src/Hosts/RemoteDesktop.Client/Assets/icon.svg` 是**原创矢量源**，
`icon-*.png` 与 `icon.ico` 是提交进仓库的渲染产物。换图标时从 SVG 重新导出各尺寸；16/24 px 使用去掉支架、
放大显示器与指针的简化版，避免细节缩成一团。`icon.ico` 另外含 20/40 px 两档（Windows 在 125%/150% 缩放
下取这两个尺寸；20 px 沿用简化版，由 24 px 高质缩放，40 px 由 48 px 高质缩放）；它们只存在于 `.ico` 中，
不单独导出 PNG——Linux 的 hicolor 安装遍历 `icon-*.png`，而 20/40 不是标准 hicolor 尺寸。
不要从某个系统程序的资源段提取图标。
