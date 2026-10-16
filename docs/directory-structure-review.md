# 目录结构评审（跨平台项目规范符合性）

评审日期：2026-10-16
评审范围：仓库根目录、`src/`、`tests/`、`build/`、`native/`、`docs/`、`.github/`，以及全部产物目录（`bin/`、`obj/`、`artifacts/`）
评审依据：跨平台 .NET 单体仓库通行约定（src/tests/docs/build/native 顶层分离、构建产物单一根、行尾由版本控制约束、脚本按平台分目录）+ 本项目自身在 `CONTRIBUTING.md` §构建产物布局 中声明的约定

---

## 一、总体结论

**结构主体符合跨平台仓库规范，但"跨平台一致性"目前没有版本控制层面的强制约束，且构建产物未收敛到已经存在的单一产物根。**

| 维度 | 评价 |
|---|---|
| 顶层目录分层 | ✅ 符合（`src` / `tests` / `build` / `native` / `docs` / `.github`） |
| 解决方案组织 | ✅ 符合（`.slnx` 逻辑分组与物理目录一致） |
| 跨平台 CI | ✅ 符合（三平台矩阵 + composite actions） |
| 产物目录收敛 | ⚠️ 部分符合（打包产物有 `artifacts/` 单一根，构建产物 `bin`/`obj` 未收敛） |
| 行尾 / 编码一致性 | ❌ 不符合（缺 `.gitattributes`，`.sh` 工作区为 CRLF） |
| 平台目录对称性 | ⚠️ 部分符合（`RemoteDesktop.Posix` 命名破例） |
| 路径可移植性 | ⚠️ 部分符合（存在非 ASCII 文件名） |

实测数据：`git ls-files --eol` 统计 **551 个文本文件为 `i/lf w/crlf`**，**0 个文件带 git 属性**（`attr/` 全空）；仓库内 **46 个 `bin`/`obj` 目录**（23 个项目各一对）；被跟踪的 `bin`/`obj` 文件 **0 个**（忽略规则有效）。

---

## 二、符合规范的部分

1. **顶层职责分离清晰**：`src`（产品代码）/ `tests`（测试）/ `build`（打包与构建脚本）/ `native`（平台原生代码）/ `docs`（文档）/ `.github`（CI）。没有把脚本堆在仓库根，根目录只保留 8 个必要元文件。
2. **`src/` 分层与架构一致**：`Core`（Domain/Abstractions/Protocol/Signaling/Transport/Application）、`Features.*`、`Media`、`Platform.*`、`Hosts.*`、`Server.*`，依赖方向由 `RemoteDesktop.ArchitectureTests` 强制校验，目录即架构。
3. **CI 真三平台**：`.github/workflows/ci.yml` 的 `build-windows`（windows-latest）、`build-linux`（ubuntu-26.04）、`build-macos`（macos-26），且把复用逻辑抽成 `.github/actions/` 下 4 个 composite action，未复制粘贴。
4. **`artifacts/` 单一产物根已建立并有文档约束**：`CONTRIBUTING.md` §构建产物布局 明确写死 `<类别>/<平台>/` 形状，并写明"**只有一个产物根目录**……不要再另起一个 `artifacts/` 目录（比如写在 `build/` 里面）"。
5. **忽略规则有效**：`bin`/`obj` 实测 0 个被跟踪（`.gitignore:45 [Oo]bj/`、`.gitignore:51 **/[Bb]in/*`），`artifacts/` 与 `.workbuddy-ai/` 均已忽略。
6. **无杂散文件**：全仓扫描无 `.user` / `.suo` / `.DS_Store` / `Thumbs.db` / `*.log` / `*.tmp` / `*.bak`。
7. **元文件齐全度较高**：`Directory.Build.props`（统一 TFM/分析器/集中包管理）、`Directory.Packages.props`、`global.json`（SDK 固定 + 测试运行器）、`CHANGELOG.md`、`CONTRIBUTING.md`、`THIRD-PARTY-NOTICES.md`、`.dockerignore`、`docs/README.md` 文档索引。
8. **专业细节处理到位**：`build/installer/*/windows/Directory.Build.props` 用空文件阻断父级 props 导入（WiX 项目不适用集中包管理），并在注释中说明原因；`CustomActions.csproj` 注释解释了 net48 + `Platform=x64` 而非 `PlatformTarget` 的原因。这类"为什么这样摆"的说明在跨平台仓库里非常关键。

---

## 三、问题清单

### P0 — 跨平台一致性缺少强制约束

**1. 缺少 `.gitattributes`，`.sh` 脚本在 Windows 工作区是 CRLF**

实测：全仓 **0 个 git 属性**（`git ls-files --eol` 的 `attr/` 列全空），551 个文本文件均为 `i/lf w/crlf`。

**当前没有故障，原因是这台机器设了 `core.autocrlf=true`**——它在提交时把 CRLF 归一化为 LF，所以 index 是干净的，Linux/macOS CI 不会炸。问题在于：

1. **`core.autocrlf` 是每台机器的本地配置，不在仓库里。** 它对这个跨平台保证没有任何强制力。任何一位 `core.autocrlf=false`（Linux/macOS 默认值，也是很多 Windows 开发者的习惯设置）的贡献者在 Windows 上改一个 `.sh` 并提交，CRLF 就会进 index，随后 Linux 上 13 个 `#!/bin/sh` 脚本以 `bad interpreter: /bin/sh^M` 失败。故障现场在 CI 而非本地，排查成本高。
2. **工作区里的 `.sh` 已经是 CRLF。** 这意味着"把仓库目录直接给 Linux 用"的常见做法全部失效：WSL 里 `/mnt/c/...` 下执行、Docker 挂载源码目录、`scp`/共享盘拷到 Linux 主机——脚本都会因 shebang 尾部的 `^M` 报错。而这些恰恰是跨平台项目最常走的路。

`.gitattributes` 的作用就是把这条保证从"个人配置"变成"仓库声明"，对所有人一致生效。

受影响文件（工作区当前均为 CRLF）：`native/linux/fetch-libmsquic.sh`、`native/macos/build-native.sh`、`native/macos/fetch-libmsquic.sh`、`build/debian/{client,host,server}/build-deb.sh`、`build/installer/client/macos/{make-app,make-dev-cert,make-icns,make-pkg}.sh`、`build/installer/server/macos/{configure-server,make-pkg,uninstall-server}.sh`。

建议新增 `.gitattributes`：

```gitattributes
* text=auto

# 必须在所有平台上以 LF 检出
*.sh          text eol=lf
*.plist       text eol=lf
*.desktop     text eol=lf
*.service     text eol=lf

# Windows 脚本与工程文件以 CRLF 检出
*.ps1         text eol=crlf
*.psm1        text eol=crlf
*.cmd         text eol=crlf
*.bat         text eol=crlf
*.wxs         text eol=crlf
*.wixproj     text eol=crlf
*.slnx        text eol=crlf

# 二进制，禁止任何换行转换
*.png binary
*.ico binary
*.icns binary
*.dylib binary
*.dll binary
*.pdb binary
*.snk binary
```

落地后需执行一次 `git add --renormalize .`。注意 `.gitattributes` 生效后，工作区里的 `.sh` 会从 CRLF 变回 LF（这是期望结果）；`.github/` 下的 YAML 目前也是 CRLF，是否纳入 `*.yml text eol=lf` 取决于是否需要在 Linux 上直接执行——建议纳入，统一为 LF。

### P1 — 构建产物未收敛到单一产物根

**2. `bin`/`obj` 分散在 23 个项目内，与"只有一个产物根目录"的自我约定冲突**

`CONTRIBUTING.md` 的单一产物根约定目前只覆盖**打包产物**，构建产物仍走 SDK 默认布局，产生 **46 个 `bin`/`obj` 目录**散落在 `src/`、`tests/`、`build/` 内。对跨平台仓库的实际影响：清理产物要靠 `git clean -xdf` 或遍历 46 个目录；`.gitignore` 必须依赖 `**/[Bb]in/*` 这类通配兜底；新增平台/项目时产物位置不可预测。

建议在根 `Directory.Build.props` 启用 .NET 8+ 的产物收敛（`artifacts/` 已在 `.gitignore` 中忽略，规则现成）：

```xml
<PropertyGroup>
  <UseArtifactsOutput>true</UseArtifactsOutput>
  <ArtifactsPath>$(MSBuildThisFileDirectory)artifacts</ArtifactsPath>
</PropertyGroup>
```

收敛后形状为 `artifacts/bin/<Project>/<config>_<rid>/`、`artifacts/obj/...`、`artifacts/packages/`，与现有 `artifacts/{app,installer,native}/<平台>/` 并列，形状一致。

**⚠️ 前置依赖（已实测确认，不改这两处会直接构建失败）**：

| 位置 | 硬编码内容 | 说明 |
|---|---|---|
| `build/installer/client/macos/make-app.sh:71` | `$REPO_ROOT/src/Hosts/RemoteDesktop.Client/bin/$CONFIGURATION/net10.0/osx-arm64/publish` | 依赖 SDK 默认 publish 目录（第 69-70 行注释说明了为何刻意不用自建 `--output`）。收敛后应改为 `dotnet publish --getProperty:PublishDir` 动态取值，顺带消除硬编码的 `osx-arm64` |
| `build/installer/server/windows/build-server-installer.ps1:632` | `CustomActions\bin\x64\$Configuration\net48\...CA.dll` | `CustomActions.csproj` 用 `<Platform>x64</Platform>` 把输出移到 `bin\x64\`（`.gitignore:30` 专门为它留了 `bin/x64/` 规则）。该项目为 net48，需确认其输出是否也走 `ArtifactsPath` |

`build/debian/*/build-deb.sh` 与 `build/installer/*/{windows,macos}/*` 的 publish 均显式传 `-o` / `-p:OutputPath=`，不受影响。

**3. `artifacts/debian/` 违反项目自己文档的 `<类别>/<平台>/` 规则**

`CONTRIBUTING.md:29` 规定交付物统一落在 `artifacts/installer/<平台>/`。但 `build/debian/{client,host,server}/build-deb.sh` 默认输出到 `$root/artifacts/debian`（共 11 处引用）。结果是**同一类别（交付安装包）有两个根**：`artifacts/installer/windows|macos/` 与 `artifacts/debian/`，正是 `CONTRIBUTING.md:33` 明确警告的情况。且 `debian` 是发行版而非平台，形状上缺一层。

建议改为 `artifacts/installer/linux/`，与 `installer/windows`、`installer/macos` 对齐。

**4. 角色命名漂移：`artifacts/app/windows/host`**

`build/service/windows/install-service.ps1:12` 使用 `artifacts/app/windows/host`，而 `CONTRIBUTING.md:30` 与两个 installer 脚本使用 `app/windows/client/` 和 `app/windows/server/`。同一层出现第三种角色名。建议统一为 `client` / `server` / `host` 三者之一并在文档中列全。

**5. `build/` 根下 3 个脚本职责与命名不统一，且 Windows-only**

| 文件 | 命名风格 | 平台 |
|---|---|---|
| `build/Generate-ThirdPartyNotices.ps1` | PascalCase | 仅 Windows |
| `build/generate-icons.ps1` | kebab-case | 仅 Windows |
| `build/security-audit.ps1` | kebab-case | 仅 Windows |

而 `build/` 下其余逻辑全部按用途分子目录（`debian/`、`docker/`、`installer/`、`service/`、`signing/`）。三个文件既破坏命名一致性，也意味着**在 Linux/macOS 上无法生成图标与第三方许可清单**——对一个以跨平台为目标的仓库，这是实质性的贡献者体验缺口（`.ps1` 无对应 `.sh` 或跨平台入口）。

建议：移入 `build/tools/`（或 `tools/`）并统一命名；为 `generate-icons` 与 `Generate-ThirdPartyNotices` 提供跨平台实现（`pwsh` 可在三平台运行，或改为 `dotnet run` 的小工具）。

### P2 — 一致性与可维护性

**6. 非 ASCII 文件名**

`build/installer/server/macos/如何配置.txt`、`build/installer/server/windows/如何配置.txt`。git 已将其输出为转义引号路径（`"build/installer/.../\345\246\202\344\275\225\351\205\215\347\275\256.txt"`），说明其已落在非 ASCII 区。这类路径在 Windows 控制台代码页、无 UTF-8 locale 的 Linux 容器、CI 日志、压缩包解压等环节是经典踩坑点。建议改为 `CONFIGURE-SERVER.md` 或 `configure-server.zh-CN.txt`。

**7. 平台项目命名不对称**

`src/Platform/` 下为 `RemoteDesktop.Platform.Windows` / `.Linux` / `.MacOS`，但共享层叫 `RemoteDesktop.Posix`（缺 `.Platform.` 前缀）。建议改为 `RemoteDesktop.Platform.Posix`，否则按前缀检索平台实现时会漏掉它。

**8. 3 个可构建项目不在 `RemoteDesktop.slnx` 内**

`build/installer/client/windows/RemoteDesktop.Installer.wixproj`、`build/installer/server/windows/RemoteDesktop.Server.Installer.wixproj`、`build/installer/server/windows/CustomActions/RemoteDesktop.Server.Installer.CustomActions.csproj`。这是**有意的合理取舍**（WiX / net48 与 net10 产品代码不同构），且 `CustomActions.csproj` 注释已说明。但 `CONTRIBUTING.md` 未提及，导致 `dotnet build RemoteDesktop.slnx` 的覆盖面看起来比实际窄。建议在 `CONTRIBUTING.md` 补一句说明这三个项目的构建入口。

**9. 测试项目命名不统一**

`RemoteDesktop.Core.UnitTests`、`RemoteDesktop.ArchitectureTests`、`RemoteDesktop.Transport.IntegrationTests`、`RemoteDesktop.Platform.{Windows,Linux,MacOS}.Tests`。同一层混用 `UnitTests` / `Tests` / `ArchitectureTests` 三种后缀，按测试类型筛选（如 `**/*.UnitTests`）会漏。建议统一为 `<Area>.<Kind>Tests`。

**10. 缺少源码级 `.editorconfig`**

实测源码目录下 **0 个** `.editorconfig`（`src/` 子树里的 21 个全部位于 `obj/` 内，是 MSBuild 生成物 `GeneratedMSBuildEditorConfig.editorconfig`）。虽已启用 `EnforceCodeStyleInBuild`，但缺少仓库级 `.editorconfig` 意味着 IDE 格式化、缩进/字符集、以及各编辑器间的一致性没有声明式来源。建议补一份根 `.editorconfig`（`charset = utf-8`、`end_of_line = lf`、`indent_style = space`，与 `.gitattributes` 呼应）。

**11. `docs/README.md` 索引漏项**

`docs/` 实有 10 个文件，索引列出 9 个（含自身），漏掉 `docs/design/security-review.md`。而 `macos-port-review.md` 已在索引中，说明这是遗漏而非有意排除。

**12. 缺 `LICENSE`**

存在 `THIRD-PARTY-NOTICES.md`，但仓库根无自身许可文件。跨平台开源项目的惯例必备项（也是部分包管理器与镜像仓库的硬性要求）。

---

## 四、建议的目标结构

```
RemoteDesktop/
├─ .gitattributes                 # 新增：行尾/二进制声明（P0）
├─ .editorconfig                  # 新增：编码与格式基线
├─ .gitignore  .dockerignore
├─ LICENSE                        # 新增
├─ README.md  CONTRIBUTING.md  CHANGELOG.md  THIRD-PARTY-NOTICES.md
├─ Directory.Build.props          # 增补 ArtifactsPath / UseArtifactsOutput
├─ Directory.Packages.props  global.json  RemoteDesktop.slnx
├─ src/                           # 产品代码，按架构分层（保持现状）
│  ├─ Core/  Features/  Media/  Hosts/  Server/
│  └─ Platform/{Windows,Linux,MacOS,Posix}   # Posix 补齐前缀
├─ tests/                         # 测试，命名统一 <Area>.<Kind>Tests
├─ build/                         # 打包与构建脚本，仅保留子目录
│  ├─ tools/                      # 新增：收拢 3 个散落脚本，命名统一
│  ├─ debian/  docker/  installer/  service/  signing/
├─ native/{linux,macos}/
├─ docs/
└─ artifacts/                     # 唯一产物根（gitignore）
   ├─ bin/  obj/  packages/       # 新增：构建产物收敛目标
   ├─ app/<平台>/{client,server,host}
   ├─ installer/{windows,macos,linux}   # linux 取代 debian
   └─ native/<平台>/
```

---

## 五、修复优先级

| 优先级 | 事项 | 影响面 | 成本 |
|---|---|---|---|
| 1 | 新增 `.gitattributes` + `git add --renormalize .` | 把行尾保证从本机 `core.autocrlf` 配置变成仓库声明，使 WSL/Docker/拷贝到 Linux 可用 | 低 |
| 2 | `artifacts/debian/` → `artifacts/installer/linux/` | 3 个脚本、11 处引用 | 低 |
| 3 | 收拢 `build/` 根 3 个脚本到 `build/tools/` 并统一命名 | 文档与 CI 引用 | 低 |
| 4 | 补根 `.editorconfig`、`LICENSE`、`docs/README.md` 索引漏项 | 贡献者体验、合规 | 低 |
| 5 | 非 ASCII 文件名改为 ASCII | 路径可移植性 | 低 |
| 6 | `RemoteDesktop.Posix` → `RemoteDesktop.Platform.Posix` | 涉及 slnx、csproj、ArchitectureTests、CI | 中 |
| 7 | 统一测试项目命名后缀 | 涉及 slnx、CI 筛选 | 中 |
| 8 | 启用 `UseArtifactsOutput` 收敛 `bin`/`obj` | **须先改 `make-app.sh:71` 与 `build-server-installer.ps1:632`**，再验证三平台打包链路 | 高 |

> 第 8 项收益最大但风险最高，建议单独一个 PR，并在三平台 CI 上完整跑通 `installer` / `debian` / `docker` 打包后再合入。

---

---

## 六、修复状态（2026-10-16 更新）

上文是评审当时的只读记录，保留原样。下列各项已按优先级修复，验证方式一并记下。

| 优先级 | 事项 | 状态 | 落地内容 |
|---|---|---|---|
| 1 | 缺 `.gitattributes` | ✅ | 新增 `.gitattributes`；`.sh`/`Dockerfile`/Debian 维护脚本/`.plist`/`.service`/`.desktop`/`.yml` 为 LF，`.ps1`/`.wxs`/`.wixproj`/`.csproj`/`.props`/`.slnx` 为 CRLF，图片与库文件为二进制。实测：42 个文件 `w/lf`、53 个 `w/crlf`、0 个混合、0 个与声明不符 |
| 2 | `artifacts/debian/` 越位 | ✅ | 三个 `build-deb.sh` 改为 `artifacts/installer/linux`；CI 的 `verify-packages`、`release.yml` 与 `docs/ops/deployment.md` 同步 |
| 3 | `build/` 根下散落脚本 | ✅ | 移入 `build/tools/` 并统一命名（`Invoke-SecurityAudit.ps1`、`New-AppIcons.ps1`），新增 `build/tools/README.md` 说明三份脚本各自的平台能力；两个跨平台脚本已实跑验证（许可清单生成 66 个包；审计脚本对缺失文件返回退出码 3） |
| 4 | `.editorconfig` / `LICENSE` / docs 索引 | ✅ | 新增根 `.editorconfig`（只管编码与缩进，行尾交给 `.gitattributes`）；`build/installer/License.txt` 上移为根 `LICENSE`，MSI 与 `.pkg` 都从它生成（`License.rtf` 重新生成后与改动前**字节完全相同**，证明迁移对许可页透明）；`docs/README.md` 补上 `security-review.md` |
| 5 | 非 ASCII 文件名 | ✅ | 两份 `如何配置.txt` → `HOW-TO-CONFIGURE.txt`（快捷方式显示名「服务器配置说明」不变）；全仓已无非 ASCII 路径 |
| 6 | `RemoteDesktop.Posix` 命名不对称 | ✅ | 改为 `RemoteDesktop.Platform.Posix`；**顺带堵上一个洞**——架构规则按程序集名前缀加载，旧名一直没被 `Platform` 规则看到。另发现 `Platform.MacOS` 因未被 `Architecture.UnitTests.csproj` 引用而同样从未被检查，已补引用。现在四个平台程序集全部在规则内，13 项架构测试通过 |
| 7 | 测试项目命名不统一 | ✅ | 统一为 `<区域>.<种类>Tests`：`Architecture.UnitTests`、`Platform.{Windows,Linux,MacOS}.UnitTests`，命名空间同步；规则写进 `CONTRIBUTING.md` |
| 8 | `bin`/`obj` 未收敛 | ✅ | 根 `Directory.Build.props` 打开 `UseArtifactsOutput` + `ArtifactsPath`；项目内 54 个 `bin`/`obj` 目录归零。两处前置耦合已修：`make-app.sh` 改为向 MSBuild 询问 `PublishDir`（实测返回 `artifacts/publish/<项目>/<pivot>/`，并非 `bin/.../publish`），`build-installer.ps1` 的 SessionAgent 清理改用新位置。`build/installer/` 下的 WiX 与 net48 项目被其空 `Directory.Build.props` 隔离，产物仍在项目内（实测 `BaseOutputPath` 仍是 `bin\`） |

**验证**：Release 全量构建 0 警告 0 错误；`dotnet test RemoteDesktop.slnx -c Release` 共 787 项，通过 524、跳过 257、失败 6。这 6 项（3 个 `SymbolicLinkEscapeTests` + 3 个 `RealProgramTerminalTests`）在改动前的 HEAD 上以同样的方式失败，属本机环境（符号链接权限、缺少 vim/nano/less），与本次改动无关。

**未做**：`docs/` 平铺 10 个文件的再分层（规模未到必须）、测试项目 `.Tests` 与 `.UnitTests` 之外的其他分类需求。

