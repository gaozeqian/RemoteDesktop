# 目录结构评审（第二轮 · PR Review）

评审日期：2026-10-16
评审对象：分支 `chore/repo-structure`（相对 `master` 前 4 个提交：产物收敛、Posix/测试项目改名、行尾仓库级声明、CI 三平台对齐）
评审范围：仓库根、`src/`、`tests/`、`build/`、`native/`、`docs/`、`.github/`，以及产物根 `artifacts/`
评审依据：跨平台 .NET 单体仓库通行约定（src/tests/docs/build/native 顶层分离、单一产物根、行尾由版本控制约束、脚本按平台分目录）+ 本仓库 `CONTRIBUTING.md` §构建产物布局 的自我约定
与第一轮的关系：`docs/directory-structure-review.md` 的只读记录保留原样，本文只做**落地核验**与**新增问题**，不重复其结论。

---

## 一、总体结论

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

## 二、上一轮 8 项修复的落地核验（逐项实测）

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

## 三、本轮发现的问题

### P2-1 `build/` 的分类轴不统一：Linux 与 Windows/macOS 走两条路

```
build/installer/client/windows/      # 角色 → 平台
build/installer/client/macos/
build/installer/server/windows/
build/installer/server/macos/
build/debian/{client,host,server}/   # 发行版 → 角色（且"debian"是包格式，不是平台）
```

同一个"新增一个平台"的动作有两个落点，而且维度不同：一边是 `<角色>/<平台>`，一边是 `<发行版>/<角色>`。这与 `CONTRIBUTING.md` §构建产物布局 里"不要再另起一个根，否则每加一个平台都要重新选边"是同一类问题，只是发生在源码树而不是产物树。

建议统一为 `build/packaging/<平台>/<角色>/`（`debian` 归入 `packaging/linux/`），或至少降到 `build/installer/<平台>/<角色>/`。

### P2-2 服务定义分散在三处

| 平台 | 位置 |
|---|---|
| Windows | `build/service/windows/{install,uninstall}-service.ps1` |
| Linux | `build/debian/host/remotedesktop-host.service`、`build/debian/server/*.service` |
| macOS | `build/installer/server/macos/com.remotedesktop.*.plist` |

"改一个服务的启动方式"要先知道它在哪个平台分支下，而这三个位置的父目录语义互不相同（service / debian包 / installer）。建议统一到 `build/service/<平台>/`，让 systemd unit、launchd plist、Windows 服务安装脚本同层并列。

### P2-3 `build/installer/License.rtf` 是生成物，却被跟踪

`New-LicenseRtf.ps1` 从根 `LICENSE` 生成 `License.rtf`，而 `License.rtf` 本身在版本库里。结果是：改了 `LICENSE` 但忘了重跑脚本 → 安装器许可页静默停留在旧版本。许可文本不一致在合规上不是小事，而且这种漂移不会被任何构建步骤发现。

建议二选一：

1. 不跟踪 `License.rtf`（加入 `.gitignore`），打包脚本在构建时生成；或
2. 保留跟踪，但在 CI 加一条校验：重新生成后 `git diff --exit-code`，非空即失败。

### P2-4 `build/signing/` 与 `build/installer/` 层级混放（tools/ 已修，这两处同类问题仍在）

```
build/signing/New-ServerCertificate.ps1   # 与 windows/ 子目录同层
build/signing/windows/…
build/installer/License.rtf               # 与 client/、server/ 子目录同层
build/installer/New-LicenseRtf.ps1
```

`build/tools/` 已经把这类"脚本直接躺在分类根上"收拢过一次，`signing/` 和 `installer/` 还留着同样的形状。建议要么各自下沉一层（`build/signing/tools/`、`build/installer/license/`），要么在 `build/` 下补一个统一的 `build/scripts/` / `build/assets/`。

### P3-5 `CONTRIBUTING.md` 的项目数已过期

§构建产物布局 写"**23 个项目**不再各长出两个目录"，实际 27 个（21 个 `src` + 6 个 `tests`，`artifacts/bin/` 27 个目录可证）。同一节示例 pivot 写作 `release_linux-x64`，而当前实测只有 `debug` / `release`（RID pivot 只在显式 `-r` 时出现）——示例本身没错，但"23"是硬错误，会让人以为有 4 个项目没被收敛。建议改为 27 并注明"含 6 个测试项目"。

### P3-6 `docs/README.md` 索引漏掉本 PR 新增的文档

`docs/` 实有 11 个文件，索引列出 9 个，漏掉 `docs/directory-structure-review.md`（第一轮评审产物）。上一轮刚补过 `security-review.md`，同一类漏项复发。建议：要么补进索引，要么明确规定"评审类文档不进索引"（目前 `macos-port-review.md` 在索引里，说明是无意遗漏而非有意排除）。

### P3-7 `docs/` 平铺 11 个文件，无子目录

第一轮明确标为"规模未到必须，未做"。现在 11 个文件里混了三类读者：设计/协议（architecture、protocol、security、security-review）、运维（deployment、device-list-storage）、移植计划与评审（macos/linux port plan + review，其中两份各 77KB / 137KB）。建议按读者分层：

```
docs/
├─ design/    architecture.md protocol.md security.md security-review.md
├─ ops/       deployment.md device-list-storage.md
└─ ports/     macos-port-plan.md macos-port-review.md linux-port-plan.md
```

（含评审记录 `*-review.md` 的归属：是"当时的结论"还是"现行规范"，值得在 `docs/README.md` 里说一句，否则新贡献者会拿旧评审当现行约定。）

### P3-8 `build/debian/*/build-deb.sh` 用了 GNU-only 的 `sed -i`

三处 `sed -i "s/…/…/"`（无备份后缀）是 GNU 语法；BSD/macOS 的 `sed -i` 会把它当成备份后缀并报 `undefined label`。实际风险低——`.deb` 只能在与 `dpkg-deb` 同在的 Linux 上构建，脚本也不会在 macOS 上跑——但脚本没有任何自检。建议开头加一行 `command -v dpkg-deb >/dev/null || { echo "需要 Linux + dpkg-deb"; exit 1; }`，把"跑错平台"变成一句人话而不是一行 sed 报错。

### P3-9（观察，非阻塞）产物根的"例外"没有清理说明

`CONTRIBUTING.md` 已声明 WiX / net48 `CustomActions` 的产物仍在项目内，是**有意的破例**，这点没问题。但同一节的"要清空构建产物，删 `artifacts/bin` 与 `artifacts/obj` 即可"因此并不彻底：`build/installer/**/bin`、`obj` 清不掉。建议补一句例外目录的清理路径，或给一个总的清理命令。

---

## 四、建议动作

| 优先级 | 事项 | 建议时机 |
|---|---|---|
| P2-1 | `build/` 分类轴统一为 `<平台>/<角色>` | 单独 PR（涉及 3 个 deb 脚本 + CI 引用 + deployment.md） |
| P2-2 | 服务定义收敛到 `build/service/<平台>/` | 同上，可与 P2-1 合并做 |
| P2-3 | `License.rtf` 改为构建时生成，或加 CI 漂移校验 | **本 PR 内**（改动小、合规相关） |
| P2-4 | `signing/`、`installer/` 层级混放 | 本 PR 内（纯移动） |
| P3-5 | `CONTRIBUTING.md` 项目数 23 → 27 | 本 PR 内（一行） |
| P3-6 | `docs/README.md` 补索引 / 明确评审类文档是否入索引 | 本 PR 内（一行） |
| P3-7 | `docs/` 分层 | 后续 PR |
| P3-8 | deb 脚本加平台自检 | 后续 PR |
| P3-9 | 补例外产物清理说明 | 本 PR 内（一句） |

> P2-1 / P2-2 涉及目录移动，会与 `docs/ops/deployment.md`、`CONTRIBUTING.md`、CI 与打包脚本的路径引用联动，建议在三平台 CI 全绿后再合入，与上一轮对第 8 项的处理方式一致。

---

## 五、修复状态（同日更新）

上文是评审当时的只读记录，保留原样。下列各项已修复，P2-4 经复核后**改变了处理方式**，一并说明。

| 优先级 | 事项 | 状态 | 落地内容 |
|---|---|---|---|
| P2-1 | `build/` 分类轴 | ✅ | 一律 `build/installer/<平台>/<角色>/`：`installer/windows/{client,server}`、`installer/macos/{client,server}`、`installer/linux/{client,host,server}`（原 `build/debian/*`）。脚本、两个 `.wixproj`、CI（6 处）、`README.md`、`CONTRIBUTING.md`、`docs/ops/deployment.md` 与两份移植文档的路径同步；三个 `build-deb.sh` 的仓库根推导从三级改为四级。分层规则写入新增的 `build/README.md` |
| P2-2 | 服务定义分散 | ✅ | `build/service/{linux,macos,windows}/`：systemd unit 与 `/etc/default` 从 `installer/linux/*` 移入 `service/linux/`，launchd plist 从 `installer/macos/server/` 移入 `service/macos/`。`host`/`server` 的 `build-deb.sh` 与 macOS `make-pkg.sh` 改为按新位置取文件；`.gitattributes` 里 `default` 的 LF 规则随之改路径（这类无扩展名文件只能逐个点名） |
| P2-3 | `License.rtf` | ✅ | 取消跟踪并写入 `.gitignore`（文件仍在磁盘上，两个 Windows 打包脚本每次构建都从根 `LICENSE` 重新生成，WiX 的 `WixUILicenseRtf` 因此总有得读）。选"不跟踪"而不是"加 CI 漂移校验"：既然每次都重新生成，一份会过期的拷贝就没有存在理由 |
| P2-4 | 层级混放 | ✅ 复核后改为立规矩，不动文件 | 复核时发现这不是混放而是**共享层**：`build/signing/README.md` 早已写明"跨平台的脚本放在本目录"，`installer/` 本层放的是 client 与 server 共用的许可文本。判断标准只有一句——被两个以上角色/平台用到就放本层，否则进子目录。这条规则现在写在 `build/README.md` 里；按它判断，`tools/` 是唯一真正需要扁平的目录（与平台无关） |
| P3-5 | 项目数过期 | ✅ | `CONTRIBUTING.md` 的"23 个项目"改为 27（21 个产品项目 + 6 个测试项目） |
| P3-6 | docs 索引漏项 | ✅ | `docs/README.md` 重写：按 `design/` / `ops/` / `ports/` 分节，两份结构评审单列一节，并注明"评审是当时的记录，现行约定以 `CONTRIBUTING.md` 与 `README.md` 为准" |
| P3-7 | `docs/` 平铺 | ✅ | 9 个文档分入 `docs/design/`、`docs/ops/`、`docs/ports/`；`README.md` 与两份结构评审留在根。`deployment.md` 里指向仓库根与 `build/` 的 4 处相对链接从 `../` 改为 `../../` |
| P3-8 | `sed -i` | ✅ | 三处改为 `awk` 写临时文件 + `mv`。复核时另有一处原判有误：脚本开头已有 `command -v dpkg-deb` 的工具自检，"跑错平台"其实会被挡住，`sed -i` 在 Linux 上也不会出问题；真正该去掉的是 GNU 语法依赖本身 |
| P3-9 | 例外产物清理 | ✅ | `CONTRIBUTING.md` 写明唯一破例是 `build/installer/windows/` 下的三个安装器工程，并给出 `git clean -xdf build/installer`（附 `-n` 预演提示） |

**未做**：`.deb` 脚本的 `dpkg-deb` 自检之外的平台断言（已足够）、`build/docker/` 归入统一分层（它不按平台分，是镜像而非安装包）。

**验证**：9 个 `.sh` 脚本 `bash -n` 全部通过；全仓 grep 旧路径（`build/debian`、`installer/client`、`installer/server`）无残留（历史记录除外：`CHANGELOG.md` 与本文档第三节按原样保留当时的路径）。三平台的打包链路（`installer/windows`、`installer/linux`、`installer/macos`）需在 CI 上实跑确认。
