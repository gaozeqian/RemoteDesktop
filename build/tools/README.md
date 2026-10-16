# 仓库维护脚本

## 这个目录怎么放东西

`build/` 下其余文件夹各管一件事（`debian/`、`docker/`、`installer/`、`service/`、`signing/`），都属于**打包或部署某
个平台**的流程。放不进这些抽屉的脚本在这里：它们维护的是**仓库本身**（图标、第三方许可清单、部署配置自查），
和打哪个平台的包无关。

| 脚本 | 做什么 | 平台 |
|---|---|---|
| `Generate-ThirdPartyNotices.ps1` | 从 NuGet 元数据生成根目录的 `THIRD-PARTY-NOTICES.md` | **三平台**（`pwsh`） |
| `Invoke-SecurityAudit.ps1` | 检查一份生产部署的配置是否还停在"开发默认"的不安全状态 | **三平台**（`pwsh`） |
| `New-AppIcons.ps1` | 从 Windows 的 `mstsc.exe` 提取图标，生成客户端的各尺寸 PNG 与 `icon.svg` | **仅 Windows**，原因见下 |

命名按语言各走各的：`.ps1` 用 PowerShell 的动词-名词 PascalCase（`Generate-`、`Invoke-`、`New-`），
`build/` 下的 `.sh` 用 kebab-case（`build-deb.sh`、`make-pkg.sh`）。两者混在同一目录里时这条尤其要守住——
不然"这是个 PowerShell 脚本还是个 shell 脚本"要靠打开文件才知道。

## 用法

```powershell
# 依赖变化后重新生成第三方许可清单（写入仓库根，不要手改生成物）
pwsh -File build/tools/Generate-ThirdPartyNotices.ps1

# 上线前自查一份服务端配置；退出码 0 正常 / 2 有不安全默认值 / 3 文件读不到
pwsh -File build/tools/Invoke-SecurityAudit.ps1 -InstallDirectory "C:\Program Files\Remote Desktop Server"
pwsh -File build/tools/Invoke-SecurityAudit.ps1 -SettingsPath artifacts/app/windows/server/appsettings.json

# 图标只在换图标时跑一次，产物是提交进仓库的
pwsh -File build/tools/New-AppIcons.ps1
```

三个脚本的默认输出位置都相对**仓库根**解析（由脚本自身的 `$PSScriptRoot` 往上找），所以在哪个目录下敲命令都
一样。

## 为什么 `New-AppIcons.ps1` 只能在 Windows 上跑

它做的两件事都没有跨平台等价物：

1. 主图标是从 `C:\Windows\System32\mstsc.exe` 的**资源段**里用 `LoadLibraryEx` + `FindResource` 抠出来的
   （P/Invoke `kernel32.dll`）。微软的远程桌面客户端图标是本产品图标的来源，这个来源只存在于 Windows。
2. 各尺寸是用 `System.Drawing`（GDI+）缩放出来的，这个程序集在 .NET 上只有 Windows 实现。

这两点都不需要修：生成的 PNG 与 `icon.svg` **是提交进仓库的**，脚本只在图标本身要换的时候跑一次，而且换图标的
人手上多半就是一台 Windows。写在这里是为了让在 Linux/macOS 上翻到这个文件的人不必先读 200 行再发现跑不了。

另外两份脚本没有这个性质——它们只读文件、不调用平台专有 API——所以在 Linux 或 macOS 上可以用 `pwsh` 直接跑，
审那边的服务端部署（配置在 `/etc/remotedesktop-server/` 下）。
