# build/ — 打包、部署与仓库维护脚本

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

## "本目录"与子目录各放什么

一个分类目录下的**本层放共享的东西，子目录放专属的东西**：

| 位置 | 本层 | 子目录 |
|---|---|---|
| `installer/` | client 与 server 共用的许可文本及其生成脚本 | `<平台>/<角色>/`：该平台该角色的打包脚本与工程 |
| `service/` | — | `<平台>/`：只有那个平台上的服务管理器读得懂的定义 |
| `signing/` | 跨平台的证书脚本（纯 .NET API，`pwsh` 三平台可跑） | `windows/`：只在 Windows 上成立的两件事 |
| `tools/` | 三个仓库维护脚本，与平台无关 | — |

`installer/` 与 `signing/` 看起来是"脚本和子目录混在一层"，其实不是混放：**本层 = 共用，子目录 = 专属**。
判断标准只有一句——这个文件是被两个以上的角色/平台用到，还是只被一个用到。

## 新增一个平台

加一层 `<平台>/`，不要另起一个和 `installer/`、`service/` 并列的目录。

以前 Linux 的打包脚本住在 `build/debian/{client,host,server}/`，Windows 和 macOS 的住在
`build/installer/{client,server}/{windows,macos}/`：一个按发行版分、一个按角色分，加一个平台时要先选边站。
现在只剩一个答案。

## 产物落在哪里

不在这里。所有构建与打包产物都写到仓库根的 `artifacts/`，按 `<类别>/<平台>/` 组织，见 `CONTRIBUTING.md`。
唯一的例外是 `installer/windows/` 下的 WiX 与 net48 `CustomActions` 工程——它们带着自己那份（故意留空的）
`Directory.Build.props`，不导入根目录的构建属性，产物仍在工程目录内。
