# 目录结构评审（2026-10-16，三轮）

> **历史快照 · 已冻结** — 这是 2026-10-16 三轮目录结构评审的合订记录，**不再更新**；其中写了"建议"
> 而后来未被采纳的项，一律以现行约定为准（[`docs/STYLE.md`](../STYLE.md)、[`CONTRIBUTING.md`](../../CONTRIBUTING.md)、
> [`build/README.md`](../../build/README.md)）。归档说明见 [`docs/reviews/README.md`](README.md)。
> （2026-10-17：正文引用的旧文档路径已统一替换为现路径，评审结论未改动。）

同一天做了三轮，一轮比一轮彻底：第一轮按跨平台仓库规范逐项核对并给出目标结构，第二轮核验第一轮的修复是否
真的落地、并挑出 `build/` 分类轴这类新问题，第三轮放弃抽检、把每个目录与每个文件名逐个过一遍，
并把几条"只写在文档里、无人强制"的约定变成 `RepositoryStructureTests` 的断言。

三轮的正文各自保留原样与原来的日期。**它们的价值是"当时看到什么、为什么这么定"**——
现行约定见上面链接的那三份文件，结构类约定另有测试兜底。

## 第一轮 · 跨平台项目规范符合性

评审日期：2026-10-16
评审范围：仓库根目录、`src/`、`tests/`、`build/`、`native/`、`docs/`、`.github/`，以及全部产物目录（`bin/`、`obj/`、`artifacts/`）
评审依据：跨平台 .NET 单体仓库通行约定（src/tests/docs/build/native 顶层分离、构建产物单一根、行尾由版本控制约束、脚本按平台分目录）+ 本项目自身在 `CONTRIBUTING.md` §构建产物布局 中声明的约定

---

### 一、总体结论

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

### 二、符合规范的部分

1. **顶层职责分离清晰**：`src`（产品代码）/ `tests`（测试）/ `build`（打包与构建脚本）/ `native`（平台原生代码）/ `docs`（文档）/ `.github`（CI）。没有把脚本堆在仓库根，根目录只保留 8 个必要元文件。
2. **`src/` 分层与架构一致**：`Core`（Domain/Abstractions/Protocol/Signaling/Transport/Application）、`Features.*`、`Media`、`Platform.*`、`Hosts.*`、`Server.*`，依赖方向由 `RemoteDesktop.ArchitectureTests` 强制校验，目录即架构。
3. **CI 真三平台**：`.github/workflows/ci.yml` 的 `build-windows`（windows-latest）、`build-linux`（ubuntu-26.04）、`build-macos`（macos-26），且把复用逻辑抽成 `.github/actions/` 下 4 个 composite action，未复制粘贴。
4. **`artifacts/` 单一产物根已建立并有文档约束**：`CONTRIBUTING.md` §构建产物布局 明确写死 `<类别>/<平台>/` 形状，并写明"**只有一个产物根目录**……不要再另起一个 `artifacts/` 目录（比如写在 `build/` 里面）"。
5. **忽略规则有效**：`bin`/`obj` 实测 0 个被跟踪（`.gitignore:45 [Oo]bj/`、`.gitignore:51 **/[Bb]in/*`），`artifacts/` 与 `.workbuddy-ai/` 均已忽略。
6. **无杂散文件**：全仓扫描无 `.user` / `.suo` / `.DS_Store` / `Thumbs.db` / `*.log` / `*.tmp` / `*.bak`。
7. **元文件齐全度较高**：`Directory.Build.props`（统一 TFM/分析器/集中包管理）、`Directory.Packages.props`、`global.json`（SDK 固定 + 测试运行器）、`CHANGELOG.md`、`CONTRIBUTING.md`、`THIRD-PARTY-NOTICES.md`、`.dockerignore`、`docs/README.md` 文档索引。
8. **专业细节处理到位**：`build/installer/*/windows/Directory.Build.props` 用空文件阻断父级 props 导入（WiX 项目不适用集中包管理），并在注释中说明原因；`CustomActions.csproj` 注释解释了 net48 + `Platform=x64` 而非 `PlatformTarget` 的原因。这类"为什么这样摆"的说明在跨平台仓库里非常关键。

---

### 三、问题清单

#### P0 — 跨平台一致性缺少强制约束

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

#### P1 — 构建产物未收敛到单一产物根

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

#### P2 — 一致性与可维护性

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

`docs/` 实有 10 个文件，索引列出 9 个（含自身），漏掉安全评审（现 [`security-review-2026-10-12.md`](security-review-2026-10-12.md)）。而 macOS 移植评审（现 [`macos-port-review-2026-10-13.md`](macos-port-review-2026-10-13.md)）已在索引中，说明这是遗漏而非有意排除。

**12. 缺 `LICENSE`**

存在 `THIRD-PARTY-NOTICES.md`，但仓库根无自身许可文件。跨平台开源项目的惯例必备项（也是部分包管理器与镜像仓库的硬性要求）。

---

### 四、建议的目标结构

```text
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

### 五、修复优先级

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

### 六、修复状态（2026-10-16 更新）

上文是评审当时的只读记录，保留原样。下列各项已按优先级修复，验证方式一并记下。

| 优先级 | 事项 | 状态 | 落地内容 |
|---|---|---|---|
| 1 | 缺 `.gitattributes` | ✅ | 新增 `.gitattributes`；`.sh`/`Dockerfile`/Debian 维护脚本/`.plist`/`.service`/`.desktop`/`.yml` 为 LF，`.ps1`/`.wxs`/`.wixproj`/`.csproj`/`.props`/`.slnx` 为 CRLF，图片与库文件为二进制。实测：42 个文件 `w/lf`、53 个 `w/crlf`、0 个混合、0 个与声明不符 |
| 2 | `artifacts/debian/` 越位 | ✅ | 三个 `build-deb.sh` 改为 `artifacts/installer/linux`；CI 的 `verify-packages`、`release.yml` 与 `docs/ops/README.md` 同步 |
| 3 | `build/` 根下散落脚本 | ✅ | 移入 `build/tools/` 并统一命名（`Invoke-SecurityAudit.ps1`、`New-AppIcons.ps1`），新增 `build/README.md` 说明三份脚本各自的平台能力；两个跨平台脚本已实跑验证（许可清单生成 66 个包；审计脚本对缺失文件返回退出码 3） |
| 4 | `.editorconfig` / `LICENSE` / docs 索引 | ✅ | 新增根 `.editorconfig`（只管编码与缩进，行尾交给 `.gitattributes`）；`build/installer/License.txt` 上移为根 `LICENSE`，MSI 与 `.pkg` 都从它生成（`License.rtf` 重新生成后与改动前**字节完全相同**，证明迁移对许可页透明）；`docs/README.md` 补上 `security-review.md` |
| 5 | 非 ASCII 文件名 | ✅ | 两份 `如何配置.txt` → `HOW-TO-CONFIGURE.txt`（快捷方式显示名「服务器配置说明」不变）；全仓已无非 ASCII 路径 |
| 6 | `RemoteDesktop.Posix` 命名不对称 | ✅ | 改为 `RemoteDesktop.Platform.Posix`；**顺带堵上一个洞**——架构规则按程序集名前缀加载，旧名一直没被 `Platform` 规则看到。另发现 `Platform.MacOS` 因未被 `Architecture.UnitTests.csproj` 引用而同样从未被检查，已补引用。现在四个平台程序集全部在规则内，13 项架构测试通过 |
| 7 | 测试项目命名不统一 | ✅ | 统一为 `<区域>.<种类>Tests`：`Architecture.UnitTests`、`Platform.{Windows,Linux,MacOS}.UnitTests`，命名空间同步；规则写进 `CONTRIBUTING.md` |
| 8 | `bin`/`obj` 未收敛 | ✅ | 根 `Directory.Build.props` 打开 `UseArtifactsOutput` + `ArtifactsPath`；项目内 54 个 `bin`/`obj` 目录归零。两处前置耦合已修：`make-app.sh` 改为向 MSBuild 询问 `PublishDir`（实测返回 `artifacts/publish/<项目>/<pivot>/`，并非 `bin/.../publish`），`build-installer.ps1` 的 SessionAgent 清理改用新位置。`build/installer/` 下的 WiX 与 net48 项目被其空 `Directory.Build.props` 隔离，产物仍在项目内（实测 `BaseOutputPath` 仍是 `bin\`） |

**验证**：Release 全量构建 0 警告 0 错误；`dotnet test RemoteDesktop.slnx -c Release` 共 787 项，通过 524、跳过 257、失败 6。这 6 项（3 个 `SymbolicLinkEscapeTests` + 3 个 `RealProgramTerminalTests`）在改动前的 HEAD 上以同样的方式失败，属本机环境（符号链接权限、缺少 vim/nano/less），与本次改动无关。

**未做**：`docs/` 平铺 10 个文件的再分层（规模未到必须）、测试项目 `.Tests` 与 `.UnitTests` 之外的其他分类需求。

## 第二轮 · 落地核验（PR Review）

评审日期：2026-10-16
评审对象：分支 `chore/repo-structure`（相对 `master` 前 4 个提交：产物收敛、Posix/测试项目改名、行尾仓库级声明、CI 三平台对齐）
评审范围：仓库根、`src/`、`tests/`、`build/`、`native/`、`docs/`、`.github/`，以及产物根 `artifacts/`
评审依据：跨平台 .NET 单体仓库通行约定（src/tests/docs/build/native 顶层分离、单一产物根、行尾由版本控制约束、脚本按平台分目录）+ 本仓库 `CONTRIBUTING.md` §构建产物布局 的自我约定
与第一轮的关系：第一轮的正文原样保留在本文 §第一轮 里，本文只做**落地核验**与**新增问题**，不重复其结论。

---

### 一、总体结论

**建议合入（Approve with comments）。** 第一轮列出的 8 项问题**已全部实测落地**，仓库主体符合跨平台目录结构规范；本轮发现的问题集中在 `build/` 的分类轴不统一与两处文档滞后，均不影响构建正确性，可在本 PR 内或后续 PR 处理。

| 维度 | 第一轮 | 本轮实测 |
|---|---|---|
| 顶层目录分层 | ✅ | ✅ 保持 |
| 产物收敛到单一根 | ⚠️ → 已改 | ✅ 项目内 `bin`/`obj` **0 个** |
| 行尾 / 编码仓库级声明 | ❌ → 已改 | ✅ 声明与实际**零不符** |
| 平台目录对称性 | ⚠️ → 已改 | ✅ `Platform.{Windows,Linux,MacOS,Posix}` |
| 路径可移植性 | ⚠️ → 已改 | ✅ 非 ASCII 0、大小写冲突 0、最长路径 96 字符 |
| `build/` 分类轴 | 未评 | ⚠️ **新增问题**：按角色/按平台两套轴并存 |
| 文档与结构同步 | ⚠️ | ⚠️ 两处滞后（项目数、docs 索引） |

---

### 二、上一轮 8 项修复的落地核验（逐项实测）

| # | 事项 | 实测方法 | 结果 |
|---|---|---|---|
| 1 | `.gitattributes` | `git ls-files --eol` | ✅ index 全 LF（555 `i/lf` + 10 `i/-text`）；工作区 108 `w/lf` + 447 `w/crlf`；**声明的 `eol=` 与实际不符者 0 个**，未声明属性却为 CRLF 的文本文件 0 个 |
| 2 | `artifacts/debian/` → `installer/linux/` | 全仓 grep `artifacts/debian` | ✅ 活跃引用 0（仅 `CHANGELOG.md:108` 为历史记录，应保留）；CI `verify-packages` 与 `release.yml` 均已同步 |
| 3 | `build/` 根散落脚本 → `build/tools/` | 目录清单 | ✅ `Generate-ThirdPartyNotices.ps1`、`Invoke-SecurityAudit.ps1`、`New-AppIcons.ps1` + `README.md` 就位；grep 旧名 `security-audit` / `generate-icons` 无活跃引用 |
| 4 | `.editorconfig` / `LICENSE` / docs 索引 | 文件存在性 | ✅ 三者在位；`.editorconfig` 明确把行尾交给 `.gitattributes`（注释写明"两个答案会打架"），设计正确 |
| 5 | 非 ASCII 文件名 | `git ls-files \| grep -P '[^\x00-\x7F]'` | ✅ **0 个** |
| 6 | `RemoteDesktop.Posix` 命名 | 目录 + slnx | ✅ `src/Platform/RemoteDesktop.Platform.Posix`；`RemoteDesktop.slnx` 四个平台程序集并列 |
| 7 | 测试项目命名 `<区域>.<种类>Tests` | 目录 + slnx | ✅ 6 个项目全部为 `*.UnitTests` / `*.IntegrationTests` |
| 8 | `UseArtifactsOutput` 产物收敛 | `find src tests build native -type d \( -name bin -o -name obj \)` | ✅ **0 个**；`artifacts/bin/` 下 27 个项目目录 = 21 个 `src` + 6 个 `tests`，与 `RemoteDesktop.slnx` 项目数一致 |

补充核验（第一轮未覆盖）：

- **工作树干净**：`git status --untracked-files=all` 无输出；`artifacts/` 由 `.gitignore:88` 整体忽略，`.workbuddy/` 由 `.gitignore:449` 忽略。
- **无大小写冲突**：全路径小写化后无重复（Linux 检出不会出现"两个文件塌成一个"）。
- **路径长度**：最长 96 字符（`build/installer/server/windows/CustomActions/…csproj`），远低于 Windows `MAX_PATH` 260；无含空格路径。
- **无旧命名残留**：`RemoteDesktop.Posix`、`bin/Release/net10.0` 等活跃引用 0 处。
- **CI 真三平台且都打包**：`build-windows`（MSI）、`build-linux`（deb）、`build-macos`（pkg），四个共享 composite action 复用；`.dockerignore` 排除 `**/bin/`、`**/obj/`、`**/artifacts/`。
- **隔离设计正确**：`build/installer/**/windows/Directory.Build.props` 为空文件，阻断根 props 导入，WiX 与 net48 的 `CustomActions` 产物仍在项目内，与 `CONTRIBUTING.md` 声明一致。

---

### 三、本轮发现的问题

#### P2-1 `build/` 的分类轴不统一：Linux 与 Windows/macOS 走两条路

```text
build/installer/client/windows/      # 角色 → 平台
build/installer/client/macos/
build/installer/server/windows/
build/installer/server/macos/
build/debian/{client,host,server}/   # 发行版 → 角色（且"debian"是包格式，不是平台）
```

同一个"新增一个平台"的动作有两个落点，而且维度不同：一边是 `<角色>/<平台>`，一边是 `<发行版>/<角色>`。这与 `CONTRIBUTING.md` §构建产物布局 里"不要再另起一个根，否则每加一个平台都要重新选边"是同一类问题，只是发生在源码树而不是产物树。

建议统一为 `build/packaging/<平台>/<角色>/`（`debian` 归入 `packaging/linux/`），或至少降到 `build/installer/<平台>/<角色>/`。

#### P2-2 服务定义分散在三处

| 平台 | 位置 |
|---|---|
| Windows | `build/service/windows/{install,uninstall}-service.ps1` |
| Linux | `build/debian/host/remotedesktop-host.service`、`build/debian/server/*.service` |
| macOS | `build/installer/server/macos/com.remotedesktop.*.plist` |

"改一个服务的启动方式"要先知道它在哪个平台分支下，而这三个位置的父目录语义互不相同（service / debian包 / installer）。建议统一到 `build/service/<平台>/`，让 systemd unit、launchd plist、Windows 服务安装脚本同层并列。

#### P2-3 `build/installer/License.rtf` 是生成物，却被跟踪

`New-LicenseRtf.ps1` 从根 `LICENSE` 生成 `License.rtf`，而 `License.rtf` 本身在版本库里。结果是：改了 `LICENSE` 但忘了重跑脚本 → 安装器许可页静默停留在旧版本。许可文本不一致在合规上不是小事，而且这种漂移不会被任何构建步骤发现。

建议二选一：

1. 不跟踪 `License.rtf`（加入 `.gitignore`），打包脚本在构建时生成；或
2. 保留跟踪，但在 CI 加一条校验：重新生成后 `git diff --exit-code`，非空即失败。

#### P2-4 `build/signing/` 与 `build/installer/` 层级混放（tools/ 已修，这两处同类问题仍在）

```text
build/signing/New-ServerCertificate.ps1   # 与 windows/ 子目录同层
build/signing/windows/…
build/installer/License.rtf               # 与 client/、server/ 子目录同层
build/installer/New-LicenseRtf.ps1
```

`build/tools/` 已经把这类"脚本直接躺在分类根上"收拢过一次，`signing/` 和 `installer/` 还留着同样的形状。建议要么各自下沉一层（`build/signing/tools/`、`build/installer/license/`），要么在 `build/` 下补一个统一的 `build/scripts/` / `build/assets/`。

#### P3-5 `CONTRIBUTING.md` 的项目数已过期

§构建产物布局 写"**23 个项目**不再各长出两个目录"，实际 27 个（21 个 `src` + 6 个 `tests`，`artifacts/bin/` 27 个目录可证）。同一节示例 pivot 写作 `release_linux-x64`，而当前实测只有 `debug` / `release`（RID pivot 只在显式 `-r` 时出现）——示例本身没错，但"23"是硬错误，会让人以为有 4 个项目没被收敛。建议改为 27 并注明"含 6 个测试项目"。

#### P3-6 `docs/README.md` 索引漏掉本 PR 新增的文档

`docs/` 实有 11 个文件，索引列出 9 个，漏掉第一轮评审产物（现 [`repo-structure-review-2026-10-16.md`](repo-structure-review-2026-10-16.md) §第一轮）。上一轮刚补过安全评审（现 [`security-review-2026-10-12.md`](security-review-2026-10-12.md)），同一类漏项复发。建议：要么补进索引，要么明确规定"评审类文档不进索引"（移植评审在索引里——现 [`macos-port-review-2026-10-13.md`](macos-port-review-2026-10-13.md)——说明是无意遗漏而非有意排除）。

#### P3-7 `docs/` 平铺 11 个文件，无子目录

第一轮明确标为"规模未到必须，未做"。现在 11 个文件里混了三类读者：设计/协议（四份已并入 [`../design/README.md`](../design/README.md)，安全评审另入 [`security-review-2026-10-12.md`](security-review-2026-10-12.md)）、运维（两份已并入 [`../ops/README.md`](../ops/README.md)）、移植计划与评审（计划已并入 [`../ports/README.md`](../ports/README.md)，评审另入 [`macos-port-review-2026-10-13.md`](macos-port-review-2026-10-13.md)；其中两份计划原各 77KB / 137KB）。建议按读者分层：

```text
docs/
├─ design/    README.md（架构、协议、安全合并为一份）
├─ ops/       README.md（部署、设备列表存储合并为一份）
└─ ports/     README.md（两份移植计划合并为一份；评审另入 reviews/）
```

（含评审记录 `*-review.md` 的归属：是"当时的结论"还是"现行规范"，值得在 `docs/README.md` 里说一句，否则新贡献者会拿旧评审当现行约定。）

#### P3-8 `build/debian/*/build-deb.sh` 用了 GNU-only 的 `sed -i`

三处 `sed -i "s/…/…/"`（无备份后缀）是 GNU 语法；BSD/macOS 的 `sed -i` 会把它当成备份后缀并报 `undefined label`。实际风险低——`.deb` 只能在与 `dpkg-deb` 同在的 Linux 上构建，脚本也不会在 macOS 上跑——但脚本没有任何自检。建议开头加一行 `command -v dpkg-deb >/dev/null || { echo "需要 Linux + dpkg-deb"; exit 1; }`，把"跑错平台"变成一句人话而不是一行 sed 报错。

#### P3-9（观察，非阻塞）产物根的"例外"没有清理说明

`CONTRIBUTING.md` 已声明 WiX / net48 `CustomActions` 的产物仍在项目内，是**有意的破例**，这点没问题。但同一节的"要清空构建产物，删 `artifacts/bin` 与 `artifacts/obj` 即可"因此并不彻底：`build/installer/**/bin`、`obj` 清不掉。建议补一句例外目录的清理路径，或给一个总的清理命令。

---

### 四、建议动作

| 优先级 | 事项 | 建议时机 |
|---|---|---|
| P2-1 | `build/` 分类轴统一为 `<平台>/<角色>` | 单独 PR（涉及 3 个 deb 脚本 + CI 引用 + 部署文档（现 [`../ops/README.md`](../ops/README.md)）） |
| P2-2 | 服务定义收敛到 `build/service/<平台>/` | 同上，可与 P2-1 合并做 |
| P2-3 | `License.rtf` 改为构建时生成，或加 CI 漂移校验 | **本 PR 内**（改动小、合规相关） |
| P2-4 | `signing/`、`installer/` 层级混放 | 本 PR 内（纯移动） |
| P3-5 | `CONTRIBUTING.md` 项目数 23 → 27 | 本 PR 内（一行） |
| P3-6 | `docs/README.md` 补索引 / 明确评审类文档是否入索引 | 本 PR 内（一行） |
| P3-7 | `docs/` 分层 | 后续 PR |
| P3-8 | deb 脚本加平台自检 | 后续 PR |
| P3-9 | 补例外产物清理说明 | 本 PR 内（一句） |

> P2-1 / P2-2 涉及目录移动，会与 `docs/ops/README.md`、`CONTRIBUTING.md`、CI 与打包脚本的路径引用联动，建议在三平台 CI 全绿后再合入，与上一轮对第 8 项的处理方式一致。

---

### 五、修复状态（同日更新）

上文是评审当时的只读记录，保留原样。下列各项已修复，P2-4 经复核后**改变了处理方式**，一并说明。

| 优先级 | 事项 | 状态 | 落地内容 |
|---|---|---|---|
| P2-1 | `build/` 分类轴 | ✅ | 一律 `build/installer/<平台>/<角色>/`：`installer/windows/{client,server}`、`installer/macos/{client,server}`、`installer/linux/{client,host,server}`（原 `build/debian/*`）。脚本、两个 `.wixproj`、CI（6 处）、`README.md`、`CONTRIBUTING.md`、`docs/ops/README.md` 与两份移植文档的路径同步；三个 `build-deb.sh` 的仓库根推导从三级改为四级。分层规则写入新增的 `build/README.md` |
| P2-2 | 服务定义分散 | ✅ | `build/service/{linux,macos,windows}/`：systemd unit 与 `/etc/default` 从 `installer/linux/*` 移入 `service/linux/`，launchd plist 从 `installer/macos/server/` 移入 `service/macos/`。`host`/`server` 的 `build-deb.sh` 与 macOS `make-pkg.sh` 改为按新位置取文件；`.gitattributes` 里 `default` 的 LF 规则随之改路径（这类无扩展名文件只能逐个点名） |
| P2-3 | `License.rtf` | ✅ | 取消跟踪并写入 `.gitignore`（文件仍在磁盘上，两个 Windows 打包脚本每次构建都从根 `LICENSE` 重新生成，WiX 的 `WixUILicenseRtf` 因此总有得读）。选"不跟踪"而不是"加 CI 漂移校验"：既然每次都重新生成，一份会过期的拷贝就没有存在理由 |
| P2-4 | 层级混放 | ✅ 复核后改为立规矩，不动文件 | 复核时发现这不是混放而是**共享层**：`build/signing/README.md` 早已写明"跨平台的脚本放在本目录"，`installer/` 本层放的是 client 与 server 共用的许可文本。判断标准只有一句——被两个以上角色/平台用到就放本层，否则进子目录。这条规则现在写在 `build/README.md` 里；按它判断，`tools/` 是唯一真正需要扁平的目录（与平台无关） |
| P3-5 | 项目数过期 | ✅ | `CONTRIBUTING.md` 的"23 个项目"改为 27（21 个产品项目 + 6 个测试项目） |
| P3-6 | docs 索引漏项 | ✅ | `docs/README.md` 重写：按 `design/` / `ops/` / `ports/` 分节，两份结构评审单列一节，并注明"评审是当时的记录，现行约定以 `CONTRIBUTING.md` 与 `README.md` 为准" |
| P3-7 | `docs/` 平铺 | ✅ | 9 个文档分入 `docs/design/`、`docs/ops/`、`docs/ports/`；`README.md` 与两份结构评审留在根。部署文档（现 [`../ops/README.md`](../ops/README.md)）里指向仓库根与 `build/` 的 4 处相对链接从 `../` 改为 `../../` |
| P3-8 | `sed -i` | ✅ | 三处改为 `awk` 写临时文件 + `mv`。复核时另有一处原判有误：脚本开头已有 `command -v dpkg-deb` 的工具自检，"跑错平台"其实会被挡住，`sed -i` 在 Linux 上也不会出问题；真正该去掉的是 GNU 语法依赖本身 |
| P3-9 | 例外产物清理 | ✅ | `CONTRIBUTING.md` 写明唯一破例是 `build/installer/windows/` 下的三个安装器工程，并给出 `git clean -xdf build/installer`（附 `-n` 预演提示） |

**未做**：`.deb` 脚本的 `dpkg-deb` 自检之外的平台断言（已足够）、`build/docker/` 归入统一分层（它不按平台分，是镜像而非安装包）。

**验证**：9 个 `.sh` 脚本 `bash -n` 全部通过；全仓 grep 旧路径（`build/debian`、`installer/client`、`installer/server`）无残留（历史记录除外：`CHANGELOG.md` 与本文档第三节按原样保留当时的**构建目录**路径）。三平台的打包链路（`installer/windows`、`installer/linux`、`installer/macos`）需在 CI 上实跑确认。

## 第三轮 · 全量普查

- **评审日期**：2026-10-16
- **评审对象**：分支 `chore/repo-structure`（起点 `4f14524`，及其上的本轮修复）
- **评审范围**：仓库根、`src/`、`tests/`、`build/`、`native/`、`docs/`、`.github/`，以及全部产物目录
  （`artifacts/`、`TestResults/`、工程内残留的 `bin/`/`obj/`）
- **评审依据**：跨平台 .NET 单体仓库通行约定，加上本项目自己在 `CONTRIBUTING.md` 与 `build/README.md`
  里声明的约定——**含对约定本身的质疑**（见第三、五节）
- **与前两轮的关系**：第一、二轮的正文原样保留，本文件不重复它们的结论，只记录本轮新看到的东西

---

### 一、这一轮为什么换了做法

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

### 二、实测数据

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

### 三、问题清单

#### A. 真缺陷

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

**A-4 `docs/ops/README.md` 的手工构建路径与打包脚本不一致**

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

#### B. 产物目录

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

#### C. 目录名 / 文件名

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
全是 kebab-case。规则只写在叶子 `build/README.md` 里，整棵树的手册 `build/README.md` 一个字没提。
另外 `Generate-ThirdPartyNotices.ps1` 的 `Generate` **不在** PowerShell 批准动词表上——而 `build/README.md`
恰恰把它当范例列出来。

**C-5 `.github/workflows/release.yml` 的 job 名破坏了 `build-<平台>` 模式**

`ci.yml` 是 `build-windows`/`build-linux`/`build-macos`；`release.yml` 是
`build-linux`/`build-macos`/**`build-and-release`**——Windows 那条腿同时也是聚合者，于是平台身份从名字里
消失了，读 job 列表看不出 Windows 在哪。

#### D. 目录组织

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

#### E. 约定本身

**E-1 `build/` 名字里没有构建系统**——构建是 `dotnet build RemoteDesktop.slnx`，由根 `Directory.Build.props`
定义；这棵树装的是打包、部署与仓库维护脚本。

**E-2 平台名的三套拼写**：`src/` 用 `MacOS`（程序集名的一部分）、路径用 `macos`、散文用 `macOS`。

**E-3 "分类桶"写法没有写下来**——一个文件承载"一个概念的接口族与它的 DTO"（`IAudio.cs` 里没有 `IAudio`
类型，`FileModel.cs` 里没有 `FileModel`）是允许的，但没有任何地方说明它允许。

> 关于散文里的 `MacOS`：本轮**没有**把它当作问题。抽样后发现绝大多数是 `Contents/MacOS/`（`.app` bundle 里
> 真实存在的目录名）、`AddMacOSPlatform`、`OperatingSystem.IsMacOS()` 这类标识符或路径，62 处里有 41 处本来
> 就在反引号内。**改掉会破坏文档事实**，所以不动。

---

### 四、做了什么

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

1. `CONTRIBUTING.md` 产物表：`packages/` → `package/<pivot>/`（并注明单数与无项目层是 SDK 定的）；
   安装包文件名改成实际的两个；`app/<平台>/` 的下一层定义为**交付物**；补 `test-results/` 与 `lib/` 两行；
   补一句"两组不是同一条规则"。
2. `docs/ops/README.md` 的手工构建路径改为 `server/Rendezvous`、`server/Relay`。
3. `CONTRIBUTING.md` 的依赖规则改为"只能依赖 `Abstractions` **与** `Domain`"，与测试一致。
4. `DependencyRuleTests` 的 feature 列表只留一处，`[InlineData]` 换成从它派生的 `MemberData`。
5. `TransferManagerSessionTests.cs` → `FileTransferSessionTests.Transfer.cs`（沿用仓库已有的
    `TerminalEmulator.Parser.cs` 那套 partial 拆分的点号后缀写法）。
6. `README.md` 与 `docs/design/README.md` 的两处分层图对齐：`Features` 与 `Hosts` 是**并列**的，
    都是只依赖 `Application`，不是一个接一个。
7. `build/README.md`：首行声明"本目录不是构建系统"；补三平台角色集合不对称的表；补 `docker/` 是第三条轴；
    补脚本命名规则（从 `build/README.md` 提升上来）；修正"安装器产物仍在工程内"这句只说对一半的话。
8. 新建 `native/README.md`：放什么、为什么没有 `windows/`、`rd_` 前缀规则、产物落 `artifacts/lib/<平台>/`。
9. `docs/README.md` 定义 `-plan` / `-review`，并写明之后用日期而非序号命名；根 `README.md` 的重复索引
    收成一行，终止分叉。
10. `build/service/linux/default` → `remotedesktop-server.default`；`.gitattributes` 里那条逐名规则
    改成 `*.default` 通配，避免下次再忘。
11. `HOW-TO-CONFIGURE.txt` → `how-to-configure.txt`（两份）。
12. `release.yml` 的 `build-and-release` → `build-windows-and-release`。
13. `icon.png` 去重，生成器只写 `icon-256.png`（应用与 SVG 本来就指向它）。
14. 顺带修 `Directory.Build.props` 注释里过期的"23 个项目"（第二轮改了 `CONTRIBUTING.md` 的同款数字，
    漏了这一处），并重新生成 `THIRD-PARTY-NOTICES.md`（见 A-7）。

**把约定变成断言（第六节）**

1. 新增 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs`，六条断言。

---

### 五、明确没做（及理由）

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

### 九、补记（同日）：把上一节推迟的项做完

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

1. **发布资产的架构词（`amd64` vs `x64`）**。上一节只说了"不改"，这一步把它变成显式约定：`CONTRIBUTING.md`
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

1. **D-3 `build/installer/` 本层的描述不准**。`build/README.md` 的规则表把它写成"client 与 server 共用的**许可文本**及其
   生成脚本"。实情是：许可文本是仓库根的 `LICENSE`，macOS 的 `.pkg` 直接拷那一份、根本不经过这个脚本；这一层的
   `New-LicenseRtf.ps1` 只被 **Windows** 用（RTF 是 WiX 许可页的格式）。现在表里如实写了，并补一句说明它是
   "本层 = 共用"这条判据里唯一的窄例——它满足"两个角色"但不满足"两个平台"。
   同时把三个**写进源码树**的生成物（`License.rtf`、两份 `Files.generated.wxs`）列入"产物落在哪里"的例外表，
   附上它们不能挪走的原因（WiX 按 `..\..\License.rtf` 相对路径读后者之一；WiX SDK 编译工程文件夹下所有 `*.wxs`，
   所以另一个必须生成在工程文件夹里）。

2. **D-8 测试项目名里的"区域"没有定义**。`CONTRIBUTING.md` 只说 `<区域>.<种类>Tests`，没说"区域"是什么；而
   `RemoteDesktop.Core.UnitTests` 覆盖的是 Core **加 Features 与 Media**（`ProjectReference` 里就是
   `Application`、`Features.{Desktop,FileTransfer,Terminal}`、`Media`、`Server.Shared`），拿 `tests/` 的项目名
   去找"某个功能在哪测"会找不到。现在补了一张四行对照表，并写明默认规则：功能层单测放 `Core.UnitTests`，
   平台专有的才进对应平台项目，需要真网络/真进程的才进 `IntegrationTests`。

**仍未做，且成本比上一节估的高**

1. **`src/` 媒体家族文件夹统一（D-6）**。上一节写它"会动 Windows 平台程序集的编译布局"，去查了一下，
   实情更重：**这个仓库的命名空间跟着文件夹走**（`MediaFoundation/MfRuntime.cs` 的命名空间就是
   `RemoteDesktop.Platform.Windows.MediaFoundation`）。所以那不是"挪 8 个文件"，而是"挪文件 + 改命名空间 +
   改所有引用 + 改测试"的级联，且三平台都要验。它本身不是缺陷，是判断——所以仍然不做，但理由从
   "会动编译布局"更正为"命名空间级联"。

   其余未做的见上一节的表，理由不变。

---

### 六、本轮的主要产出：六条断言

`RepositoryStructureTests` 把下列约定从"文档里的话"变成"构建会红"：

| 断言 | 此前靠什么保证 |
|---|---|
| `tests/` 项目名匹配 `^RemoteDesktop\..+\.(Unit\|Integration)Tests$` | 无——文档自己说漏掉是静默的 |
| 项目目录名 == `.csproj` 名 | 无 |
| `.cs` 文件名要么是它声明的类型，要么是它分组的概念（此时必须声明 ≥2 个类型） | 无 |
| `.sh` 全 kebab-case | 无 |
| `.ps1` 全 Verb-Noun 且动词在批准表上（具名豁免 5 个待改名的） | 只写在 `build/README.md` 里 |
| `build/` 下只出现平台名与角色名 | 无 |

第三条的判据值得记一笔：不是"文件名必须是类型名"，而是**"名字不是类型时，它必须是个分组"**。这样
`IAudio.cs`（音频端口的整组契约）与 `FileModel.cs` 这类写法被允许，而只声明一个类型、名字却是另一个的
单件文件会被拦下。七条真漂移就是这条规则找出来的（第五节的其余候选经检查是分组，不报）。

**断言确实会红**：加进去时它们立刻报了 7 个 `.cs` 与 1 个 `.ps1`，修完才转绿——不是先改文件再写一条永远绿的
测试。

---

### 七、验证

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

### 八、留给下一轮的

**本轮该加而没加的防护（A-7）**：`THIRD-PARTY-NOTICES.md` 是提交进仓库的生成物，已经静默过期过一次
（缺了 `FFmpeg.AutoGen` 整个一项）。`License.rtf` 那次的解法是"不跟踪"，它不适用——清单是发布物的一部分。
可行的做法是加一个 CI 检查：重新生成后 `git diff --exit-code`，非空即红。**本轮没做**，因为这会多出一个需要
三平台验证的 CI 步骤，而本轮已经改动了测试上传路径。**这是本轮最应该被下一轮先接手的项。**

其余已知但未处理的：第五节的整张表；B-6 里那两条会吞同名目录的宽规则（当前无害，但没人拦着它变有害）。

---

### 十、由读者发现的一处"已修"其实是半修

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

---

### 十一、CI 抓到的另一处漏网

推上去之后 macOS job 失败（Windows 与 Linux 通过），报的是：

```text
error MSB3030: Could not copy the file
".../artifacts/native/macos/libRemoteDesktopMacNative.dylib" because it was not found.
```

第十节那条说的是"清单上打了勾、实际只做了一半"。这一条是同一类的另一面：**做完之后用错了验证方法**。

`artifacts/native/` 改名 `artifacts/lib/` 时，我是用带正斜杠的模式 `artifacts/native` 去搜引用的。结果是它
匹配到了注释行（注释里写的是正斜杠），却**漏掉了两个 `.csproj` 里真正的 `<None Include>`**——那些路径写的是
反斜杠（`artifacts\native\macos\...`）。而 macOS 的构建脚本本身是改对了的（它把 dylib 写进了
`artifacts/lib/macos`，CI 日志里那句 `replacing existing signature` 就是证据），坏的是引用端还在找旧位置。

所以这次的问题不是"没改全"那么简单，而是**"我改完并复查过"这句话本身不成立**：复查用的模式与被复查的内容
不在同一个表示法里（正斜杠 vs 反斜杠），复查就只是确认了我自己的假设。改名同时牵动源码树名与产物目录名时
（`native/` 与 `artifacts/native/` 同名不同义正是这次要修的东西），任何带路径分隔符的搜索模式都会在斜杠方向
上静默失效。

已修：两个 `.csproj` 里那两行改为 `..\..\..\artifacts\lib\macos\...`；同文件里 `..\..\..\native\macos\` 那两处
指向的是**源码树**，不动。复查改用斜杠无关的模式（`artifacts[\/]native`），全仓无残留。

**这一条只能由 CI 验**：那两个 `<None Include>` 都带 `Condition="$([MSBuild]::IsOSPlatform('OSX'))"`，在
Windows 上根本不求值，所以本机构建永远是绿的。上一节末尾列"本机无法验证、需 CI 实跑确认"的项时写的是
"`artifacts/lib/<平台>/` 在 macOS/Linux 上的产出"——它确实在名单上，也确实被 CI 抓到了。这条写在这里不是
为了说明流程没起作用，恰恰相反：**那份名单是有效的，而这份评审里所有声称"已修"的项都应当先问一句
"它是怎么被验的"。**
