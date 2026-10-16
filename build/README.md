# build/ — 打包、部署与仓库维护脚本

**本目录不是构建系统。** 构建是 `dotnet build RemoteDesktop.slnx`，由根目录的 `Directory.Build.props` 与
`Directory.Packages.props` 定义——这里没有任何参与其中的东西：没有 Makefile，没有聚合脚本，也没有 CI 调用的入口。
它叫 `build/`，因为装的是"跑一下就能产出交付物"的脚本；冲着"构建在哪定义"进来的，请回根目录。

## 这个目录怎么分层

两条轴，顺序固定：

1. **平台在最外面一层**（`windows` / `macos` / `linux`）。一个脚本能不能在这台机器上运行、一个包能不能在这里
   被构建，都是平台说了算，所以平台是第一个要回答的问题。
2. **角色在平台里面一层**（`client` / `server` / `host`）。同一个平台上的三个角色是三件不同的交付物，但它们
   面对的是同一台机器、同一套工具链。

```
build/
├─ installer/<平台>/<角色>/    打包脚本与安装包工程（MSI / .pkg / .deb）
├─ service/<平台>/             服务定义：systemd unit、launchd plist、Windows 服务的安装卸载脚本
├─ signing/                    代码签名：本目录放跨平台脚本，windows/ 放只在 Windows 上有意义的
├─ docker/                     服务端容器镜像
└─ tools/                      仓库维护脚本（与平台无关，见 tools/README.md）
```

平台名取 `windows` / `macos` / `linux`——是**平台**，不是发行版也不是包格式。`.deb` 因此归在
`installer/linux/` 而不是 `installer/debian/`，和 `artifacts/installer/<平台>/` 的取法一致（见 `CONTRIBUTING.md`
§构建产物布局）。

三平台的**角色集合不一样**，这不是漏了目录，而是产品形状本来不同：

| 平台 | `installer/` 下的角色 | 为什么少了一个 |
|---|---|---|
| Linux | `client`、`host`、`server` | — |
| Windows | `client`、`server` | 被控端服务与会话 Agent 并进客户端那一个 MSI，`app/windows/client/` 里因此有三个 `.exe` |
| macOS | `client`、`server` | 客户端自己就是被控端，没有服务/Agent 的拆分 |

规则是**只建该平台真实存在的角色目录**，不为对称补齐。三种产品形状见根目录 `README.md` 开头。

`docker/` 是第三条轴，不在这两条里：它装的是服务端镜像，镜像本身就是跨平台的，也不按角色分。上面那张树里
其余四项都服从 `<平台>/<角色>`，只有它不分层。

## "本目录"与子目录各放什么

一个分类目录下的**本层放共享的东西，子目录放专属的东西**：

| 位置 | 本层 | 子目录 |
|---|---|---|
| `installer/` | `New-LicenseRtf.ps1`：把仓库根的 `LICENSE` 转成 WiX 许可页要的 RTF | `<平台>/<角色>/`：该平台该角色的打包脚本与工程 |
| `service/` | — | `<平台>/`：只有那个平台上的服务管理器读得懂的定义 |
| `signing/` | 跨平台的证书脚本（纯 .NET API，`pwsh` 三平台可跑） | `windows/`：只在 Windows 上成立的两件事 |
| `tools/` | 三个仓库维护脚本，与平台无关 | — |

`installer/` 与 `signing/` 看起来是"脚本和子目录混在一层"，其实不是混放：**本层 = 共用，子目录 = 专属**。
判断标准只有一句——这个文件是被两个以上的角色/平台用到，还是只被一个用到。

`installer/` 本层那个脚本是这条判据里唯一的窄例，值得说清楚：它**只被 Windows 用**（RTF 是 WiX 许可页的格式），
之所以还放在共用层，是因为它服务的是 client 与 server **两个角色**。许可文本本身不在这一层——它是仓库根的
`LICENSE`，macOS 的 `.pkg` 直接拷那一份（`make-pkg.sh` 里的 `<license file="License.txt"/>`），根本不经过这个脚本。
换句话说："两平台共用的东西"放本层，"两角色共用的东西"也放，这一层收的是后者。

## 脚本怎么命名

按语言各走各的。两种脚本混在同一目录里时这条尤其要守住——不然"这是个 PowerShell 脚本还是个 shell 脚本"
要靠打开文件才知道：

| 语言 | 风格 | 例子 |
|---|---|---|
| shell（`.sh`） | kebab-case | `build-deb.sh`、`make-pkg.sh`、`fetch-libmsquic.sh` |
| PowerShell（`.ps1`） | Verb-Noun PascalCase，动词取自 [PowerShell 的批准动词表](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/approved-verbs-for-windows-powershell-commands) | `New-AppIcons.ps1`、`Invoke-SecurityAudit.ps1`、`Export-ThirdPartyNotices.ps1` |

注意 `Generate-` **不**在批准动词表上（这是它改成 `Export-` 的原因），`Build-` 与 `Deploy-` 则只在 PowerShell 7
上算批准。两条命名规则都由 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs` 断言。

## 新增一个平台

加一层 `<平台>/`，不要另起一个和 `installer/`、`service/` 并列的目录。

以前 Linux 的打包脚本住在 `build/debian/{client,host,server}/`，Windows 和 macOS 的住在
`build/installer/{client,server}/{windows,macos}/`：一个按发行版分、一个按角色分，加一个平台时要先选边站。
现在只剩一个答案。

## 产物落在哪里

不在这里。所有构建与打包产物都写到仓库根的 `artifacts/`，按 `<类别>/<平台>/` 组织，见 `CONTRIBUTING.md`。

唯一的例外是 `installer/windows/` 下的 WiX 与 net48 `CustomActions` 工程：它们带着自己那份（故意留空的）
`Directory.Build.props`，不导入根目录的构建属性，所以**中间产物**（`obj/`、`bin/x64/`）留在工程目录内，删
`artifacts/bin` 清不掉它们。但最终产物不留在那里——`.msi` 由两个打包脚本用 `-p:OutputPath=` 写进
`artifacts/installer/windows/`，而 `.wixpdb` 是调试符号、不是交付物，被 `-p:PdbOutputDir=` 留在工程的 `obj/` 里，
不跟着进交付物目录。

同一类还有三个**生成物是写进这棵源码树**的，都不在 `artifacts/` 下，也都在 `.gitignore` 里——它们由打包脚本每次
构建时重造，不需要提交，也不该提交：

| 文件 | 谁生成 | 为什么留在原地 |
|---|---|---|
| `installer/License.rtf` | `New-LicenseRtf.ps1` | 两个 WiX 工程按相对路径读它（`WixUILicenseRtf`），必须与 `.wxs` 相对位置固定 |
| `installer/windows/{client,server}/Files.generated.wxs` | 两个打包脚本 | 由 publish 输出生成；WiX SDK 编译工程文件夹下**所有** `*.wxs`，所以它必须生成在那里 |
| `installer/windows/**/{obj,bin}/` | 上述 WiX 与 net48 工程 | 见上，`Directory.Build.props` 那条例外 |
