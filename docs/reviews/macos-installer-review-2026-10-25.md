# macOS 安装包评审（2026-10-25）

> **历史快照 · 已冻结** — 本文件是 2026-10-25 对 macOS 三个安装包的评审记录，**不再更新**。
> 评审里写的建议**不是现行约定**；此后要知道"现在怎么做"，以
> [`build/README.md`](../../build/README.md)、[`docs/ops/README.md`](../ops/README.md) 与
> [`build/signing/README.md`](../../build/signing/README.md) 为准。归档说明见 [`README.md`](README.md)。
>
> 评审对象：`master` @ `c442392`（2026-10-25 09:09 +0800）
> 评审环境：macOS 26.7.1 (25G241)、Apple Silicon；本机无 Developer ID 证书、无公证凭据。
> 结论：**包的骨架、角色划分与"签名身份 / TCC"的基本判断是正确的；但签名与公证链有三处阻断，
> host 包的安装后生命周期、Ops 的首装体验与升级保留也有实质缺陷。** 详见 §3–§5。

---

## 0. 结论摘要

| 维度 | 结论 | 说明 |
|---|---|---|
| 三个角色的包结构 | ✅ 正确 | client（`Remote Desktop.app`）、host（`Remote Desktop Host.app` + LaunchAgent）、server（Rendezvous/Relay/Ops + 三个 LaunchDaemon）分工清楚，文件名带角色 |
| `.app` 的组装与签名 | ✅ 基本正确 | 从 publish 输出重建、先签嵌套 dylib 再签 bundle、分发时硬化运行时 + 安全时间戳；TCC 与签名身份的关系判断正确 |
| client `.pkg` 构建 | ❌ **P0** | 最后一步 `pkgutil --payload-files \| head -8` 在 `pipefail` 下必然 SIGPIPE，脚本以 141 退出（包已生成，但 CI/发版会红） |
| 分发签名身份查找 | ❌ **P0** | 三个脚本都用 `security find-identity -p codesigning` 找 `Developer ID Installer`；该策略不包含 Installer 证书，`--distribution` 在未显式给 `--identity` 时必然失败 |
| server `.pkg` 公证 | ❌ **P0** | 载荷里的 3 个 apphost 与 41 个 dylib 全是 ad-hoc 签名，`--notarize` 会被 Apple 拒绝；脚本没有签载荷的入口 |
| host 包安装后生命周期 | ❌ **P1** | 没有 postinstall：装完当前会话不会加载 LaunchAgent；首次登录前文档里的 `launchctl kickstart …` 必然报 "Could not find service"；升级也不会重启旧进程 |
| server 首装体验（Ops） | ❌ **P1** | 未传 `--seed-local-nodes` 与 `--metrics-enabled`（Linux 包已传），新装控制台没有节点、信令没有 `/metrics` |
| 升级保留配置 | ⚠️ **P1** | `--database=auto` 会把 `none` 悄悄改成 SQLite；`--ops-*` 每次安装/升级都会重写 Ops 的库路径与监听地址 |
| 局域网权限 | ⚠️ **P1** | client/host 的 `Info.plist` 都没有 `NSLocalNetworkUsageDescription`；macOS 15+ 的局域网访问可能无提示被拒 |
| 包内版本 / 许可证 / 校验 | ✅ 正确 | 版本单一来源 `version.sh`；app 内带 LICENSE 与第三方声明；CI 展开 `.pkg` 核对 component 版本与载荷 |

**P0 三项、P1 四项、P2 四项、P3 若干。** 其中 P0 都是"包能生成但发版流程过不去"的问题，
最容易被本地 `ls artifacts/installer/macos/*.pkg` 掩盖。

---

## 1. 评审范围与方法

**范围**

- [`build/installer/macos/client/`](../../build/installer/macos/client/)：`make-app.sh`、`make-pkg.sh`、
  `Info.plist`、`entitlements.plist`、`make-icns.sh`、`make-dev-cert.sh`
- [`build/installer/macos/host/`](../../build/installer/macos/host/)：`make-app.sh`、`make-pkg.sh`、`Info.plist`
- [`build/installer/macos/server/`](../../build/installer/macos/server/)：`make-pkg.sh`、`configure-server.sh`、
  `uninstall-server.sh`、`scripts/postinstall`、`how-to-configure.txt`
- [`build/service/macos/`](../../build/service/macos/) 的四个 launchd plist
- [`build/installer/version.sh`](../../build/installer/version.sh) 与
  [`build/signing/README.md`](../../build/signing/README.md)
- CI / 发版里调用这些脚本的步骤：[`ci.yml`](../../.github/workflows/ci.yml)、
  [`release.yml`](../../.github/workflows/release.yml)、
  [`verify-packages`](../../.github/actions/verify-packages/action.yml)

**方法**

1. 逐行阅读上述脚本与源码里的对应实现（`ServerSetup`、HostService 启动路径、Ops 配置读取）。
2. 用仓库里已经产出的 0.3.0 `.pkg` 做静态核对：
   `pkgutil --expand` / `--expand-full` / `--payload-files`、`codesign -dv`、`otool -l`。
3. 在本机复现构建脚本的退出码：用 `--skip-app-build` 把现有 `.app` 打进临时目录，不触碰仓库产物。
4. 用 `launchctl print` / `launchctl kickstart` 确认未加载的 LaunchAgent 的实际报错。
5. 涉及 Apple 策略的两条结论查了 Apple DTS / 公证要求（见 §8）。

**没有做的**

- 没有一次真正的 `--distribution --notarize` 构建（本机没有 Developer ID Application / Installer 证书，
  也没有公证凭据）。§3 的 M2、M3 是基于脚本与 Apple 的规则推出来的，不是实跑结果。
- 没有在第二台 Mac 上走完安装 → 授权 → 连接；本地网络权限（M7）需要局域网实机验证。
- 没有安装 server `.pkg`（会改动 `/usr/local` 与 `/Library/LaunchDaemons`）；server 的行为按脚本与源码核对。

---

## 2. 确认无问题的部分

1. **角色划分与包名**：三个包 `RemoteDesktop-{Client,Host,Server}-macOS-arm64-<版本>.pkg` 形状一致，
   client/host 是两个独立 bundle（`com.remotedesktop.client` / `com.remotedesktop.host`），TCC 可以分开授权。
   三个 `.pkg` 共用一个输出目录但角色词在文件名里，不靠"没有角色词的那个"来辨认。
2. **`.app` 的组装思路**：每次从 publish 输出重建而不是覆盖，避免旧文件被签进 bundle；
   先签 `Contents/MacOS` 里的 `.dylib`/`.so`，再用 `--deep` 签 bundle；
   `--deep` 在这里是平铺 `.NET` 发布的必要妥协，脚本注释把"为什么"写清楚了。
3. **分发签名的两个开关**：`--distribution` 同时打开硬化运行时与安全时间戳，并拒绝
   "Apple Development" 证书；`--notarize` 缺凭据时明确报错。这个分层是对的。
4. **app 内附带许可证**：client/host 的 `Contents/Resources` 有 `LICENSE.txt` 与
   `THIRD-PARTY-NOTICES.md`；server 载荷带 `/usr/local/share/doc/remotedesktop-server/`。
   不只依赖安装器许可页。
5. **server 的 live settings 策略**：payload 里是 `appsettings.default.json`，postinstall 只在
   `appsettings.json` 不存在时复制。这一层思路正确（M6 批评的是它后面又被 `--configure-system` 改写）。
6. **server 的 launchd 处理**：`bootout` → `bootstrap` → `enable`，并在 upgrade 前清理旧 job；
   plist 归 `root:wheel`、`0644`。三个 job 的重启语义与 Windows 服务一致。
7. **版本单一来源**：三个平台都 `source version.sh`，空值/非三段/超 MSI 上限都会失败，
   不再回退 `0.0.0`。CI 的 `verify-packages` 还会展开 `.pkg` 比对每个 component 与 app bundle 的版本。
8. **`pkgbuild --component` 的取舍**：host 注释里"用 `--root` 会丢扩展属性、导致 app 的签名失效"
   与实测一致；server 用 `--root` 则是因为载荷是裸目录，没有 bundle 签名要保留。

---

## 3. 阻断级（P0）

### M1 client `.pkg` 构建脚本在最后一步以 141 退出

**位置**：[`client/make-pkg.sh:292`](../../build/installer/macos/client/make-pkg.sh#L292)

```bash
set -euo pipefail
...
pkgutil --payload-files "$FINAL_PKG" | head -8 | sed 's/^/      /'
```

`pkgutil --payload-files` 的输出远大于 `head -8` 需要的行数（0.3.0 的 client 包是 588 行）。
`head` 读够 8 行就退出并关闭管道，`pkgutil` 继续写时收到 `SIGPIPE`，`PIPESTATUS` 为 `141`；
`pipefail` 让整条流水线以 141 结束，`set -e` 随即终止脚本。

**实测**（用现有 `.app`，输出到临时目录，不碰 `artifacts/`）：

```text
$ build/installer/macos/client/make-pkg.sh --skip-app-build --output "$TMP"
...
==> Building RemoteDesktop-Client-macOS-arm64-1.0.0.pkg
productbuild: Wrote product to .../RemoteDesktop-Client-macOS-arm64-1.0.0.pkg
==> Verifying
...
$ echo $?
141
```

单独复现这一行同样返回 141：

```bash
$ bash -c 'set -euo pipefail; pkgutil --payload-files \
    artifacts/installer/macos/RemoteDesktop-Client-macOS-arm64-0.3.0.pkg | head -8'
$ echo $?
141
```

**影响**：`.pkg` 已经写到磁盘，所以只看文件会以为构建成功；但脚本退出码非零，
`ci.yml` 的 `build-macos` 与 `release.yml` 的 `Build the macOS installers (.pkg)` 都会失败，
发版不会产生客户端包。host 与 server 没有这个写法（host 的 `grep -q` 检查在 284 行载荷下
实测 `PIPESTATUS=0 0`，暂时安全；但同样的写法不应再出现）。

**建议**：像 `verify-packages` 那样先把列表读进变量，一次读取两用：

```bash
payload="$(pkgutil --payload-files "$FINAL_PKG")"
printf '%s\n' "$payload" | sed -n '1,8p' | sed 's/^/      /'
echo "      … ($(printf '%s\n' "$payload" | wc -l | tr -d ' ') entries)"
```

或者把 `head` 换成会读完输入的 `sed -n '1,8p'`。CI 里最好再加一条"脚本退出码为 0 且包存在"
的断言——现在 `verify-packages` 只看文件内容，不会发现"包在、脚本失败"。

---

### M2 `Developer ID Installer` 身份自动查找必然失败

**位置**：

- [`client/make-pkg.sh:85-92`](../../build/installer/macos/client/make-pkg.sh#L85-L92)
- [`host/make-pkg.sh:73-80`](../../build/installer/macos/host/make-pkg.sh#L73-L80)
- [`server/make-pkg.sh:280-286`](../../build/installer/macos/server/make-pkg.sh#L280-L286)

三个脚本都用同一个函数：

```bash
security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Installer: [^"]*\)".*/\1/p'
```

`-p codesigning` 是按"代码签名"策略过滤。Apple 的 Developer ID 分两种：
**Developer ID Application** 签代码，**Developer ID Installer** 签安装包；
Installer 证书不在 `codesigning` 策略里，因此 `-p codesigning` **不会列出它**。
Apple DTS 在论坛里说得很直接："If you run `security find-identity` and apply the code signing filter,
you'll only see the first type."（见 §8）

**影响**：所有 `--distribution` 构建，只要没有显式传 `--identity`，都会停在

```text
No 'Developer ID Installer' certificate found. Install one, or pass --identity.
```

`release.yml` 只传 `--distribution`（证书通过 Secret 导入临时钥匙串），不传 `--identity`；
`build/signing/README.md` 也写"证书在钥匙串里，自动找"。也就是说：**一旦真的配了证书，三个包的签名发版
都会失败**；不配证书时反而"正常"（走未签名路径）。这是一个在无证书环境里完全看不出来的阻断。

**建议**：Installer 身份改用 `security find-identity -v`（不加 `-p`）或 `-p basic` 后按前缀过滤；
Application 身份继续用 `-p codesigning`。两个查找函数分开命名，避免下一个人再把同一个函数套到两种证书上。
CI 里加一个"导入证书后能打印出两种身份"的检查；本地文档也写明：证书多于一张时用
`--app-identity` / `--identity` 显式指定。

---

### M3 server `.pkg` 无法通过公证

**位置**：[`server/make-pkg.sh`](../../build/installer/macos/server/make-pkg.sh) 全文；
关键在 [`278-288`](../../build/installer/macos/server/make-pkg.sh#L278-L288) 只签 `.pkg`，
以及 [`97-107`](../../build/installer/macos/server/make-pkg.sh#L97-L107) 直接发布裸程序。

`--distribution` 对 server 只做一件事：用 `Developer ID Installer` 给 product archive 签名。
载荷里的三个 apphost 与原生库从未被 `codesign` 过。用现有 0.3.0 包核对：

```text
RemoteDesktop.Server.Rendezvous: CodeDirectory flags=0x2(adhoc)  Signature=adhoc  TeamIdentifier=not set
RemoteDesktop.Server.Relay:      Signature=adhoc  TeamIdentifier=not set
RemoteDesktop.Server.Ops:        Signature=adhoc  TeamIdentifier=not set
41 个 dylib:                     adhoc / linker-signed
```

Apple 的公证要求是：**提交物里的每一个可执行文件**都必须用 Developer ID 证书签名、
带安全时间戳、启用硬化运行时；只签外层 `.pkg` 不够。TUGboat 上 BasicTeX 的例子就是
因为 `.pkg` 里 30 个未签名的 Mach-O CLI 而被拒；Apple DTS 也明确说 "each executable must be signed
with the hardened runtime"（见 §8）。因此 `release.yml` 在配齐公证凭据后调用

```bash
./build/installer/macos/server/make-pkg.sh --distribution --notarize
```

会在 `notarytool submit --wait` 处失败，整个发版停在 macOS job。client/host 没有这个问题，
因为它们的 `.app` 已经用 Developer ID Application 签过。

**建议**（按代价从小到大）：

1. 先给 server 增加 `--app-identity`，在 `pkgbuild` 之前遍历
   `$SERVER_DIR/{Rendezvous,Relay,Ops}`，对所有 Mach-O（apphost 与 `.dylib`）执行
   `codesign --force --options runtime --timestamp --sign "$APP_IDENTITY"`；
   apphost 带上只含 `com.apple.security.cs.allow-jit` 与
   `com.apple.security.cs.allow-unsigned-executable-memory` 的 entitlements（不要复用 client 的 camera 权限）。
2. 在 `release.yml` 里把 server 的 `--notarize` 与 client/host 分开；在签名载荷实现之前，
   至少不要对 server 传 `--notarize`，并明确写出"server 只签名、不公证"。
3. `build/signing/README.md` 的"Developer ID Application 签 `.app` 及其內的二进制"要补一句：
   没有 `.app` 的 server 包同样需要逐个签载荷里的 Mach-O，否则无法公证。

---

## 4. 高（P1）

### M4 host 包装完不会启动，文档里的 `kickstart` 必然失败；升级也不重启

**位置**：[`host/make-pkg.sh`](../../build/installer/macos/host/make-pkg.sh) 没有 `--scripts`；
[`host/make-pkg.sh:181-190`](../../build/installer/macos/host/make-pkg.sh#L181-L190) 的结论页让用户
`launchctl kickstart -k gui/$(id -u)/com.remotedesktop.host`。

`/Library/LaunchAgents` 里的 plist 由 launchd 在**下一次图形登录**时加载；安装器把文件放进去，
不会让当前会话加载它。host 包没有 postinstall，所以：

- 装完当前会话里没有 `com.remotedesktop.host` 这个 job；
- 在首次图形登录（或显式 `bootstrap`）之前，结论页的 `kickstart` 会得到
  `Could not find service "com.remotedesktop.host" in domain for user gui: 501`（本机实测，退出码 113）；
- 升级时旧进程继续运行旧二进制，直到用户下次登录才换新版本（没有 `bootout` / `bootstrap`）。

host 的启动路径本身没问题：`RemoteDesktop.HostService` 在启动时会请求 Screen Recording 与
Accessibility（[`Program.cs:219-227`](../../src/Hosts/RemoteDesktop.HostService/Program.cs#L219-L227)），
只要 job 真的被加载，授权提示就会出现。问题只在于"装完没加载"。

**建议**：给 host 包加一个 `postinstall`：

1. 用 `stat -f%Su /dev/console` 找到当前图形会话用户（没有则跳过并打印说明）；
2. `launchctl bootout gui/<uid> /Library/LaunchAgents/com.remotedesktop.host.plist 2>/dev/null || true`；
3. `launchctl bootstrap gui/<uid> /Library/LaunchAgents/com.remotedesktop.host.plist`；
4. 失败时把 `launchctl bootstrap …` 原样打印给用户，而不是假装成功。

这同时解决升级重启与首装 TCC 提示两个问题。Linux 包已经用 postinst 为已登录用户立即启动
`app-RemoteDesktopHost.service`（[`CHANGELOG.md`](../../CHANGELOG.md) 第 64–65 行），macOS 缺的是同一件事。
结论页的 `kickstart` 命令可以保留，但应改成 `bootstrap` 后再 `kickstart`，或说明"若未加载先 bootstrap"。

---

### M5 新装的 Ops 控制台没有节点，信令也没有 `/metrics`

**位置**：[`server/scripts/postinstall`](../../build/installer/macos/server/scripts/postinstall) 只调用
`--configure-system`，不调用 Ops 的 `--seed-local-nodes`，也不传 `--metrics-enabled`。

对比 Linux 包的 postinst：

- [`build/installer/linux/server/postinst:215-223`](../../build/installer/linux/server/postinst#L215-L223)
  传了 `--metrics-enabled`；
- [`build/installer/linux/server/postinst:316-337`](../../build/installer/linux/server/postinst#L316-L337)
  以服务用户调用 `--seed-local-nodes --rendezvous-base-url … --relay-base-url http://127.0.0.1:5083`。

macOS 包两者都没有。结果是：

- `/metrics` 在 rendezvous 上是 404（[`RendezvousServer.cs:108`](../../src/Server/RemoteDesktop.Server.Rendezvous/RendezvousServer.cs#L108)
  只在 `MetricsEnabled` 时映射），Ops 的 `MetricScraper` 抓取失败；
- Ops 的 `ops_node` 表为空，节点页与设备页没有数据，直到运维手工在"节点管理"里添加两个地址。

`CHANGELOG.md:23-25` 把"Ops 安装即登记本地节点"写成 Linux 安装包的修复；macOS 没有跟上，
而 macOS 包是**把三个程序装在一起**的，更没有理由让用户手工登记本机节点。文档也没有告诉 macOS 用户
需要手工添加节点。

**建议**：在 postinstall 的 `--configure-system` 之后追加：

```bash
"$INSTALL_DIR/Ops/RemoteDesktop.Server.Ops" \
    --seed-local-nodes \
    --config-directory "$INSTALL_DIR/Ops" \
    --rendezvous-base-url "http://127.0.0.1:5080" \
    --relay-base-url "http://127.0.0.1:5083" || true
```

并在 rendezvous 的参数里加 `--metrics-enabled`（与 Linux 同一语义）。seeding 已经是幂等的
（节点表非空时不做修改），升级重跑安全。若 TLS 已配置，rendezvous 的 base URL 需按实际协议选择，
可参考 Linux 的 `tls_mode` 分支。

---

### M6 升级会重写 Ops 设置，并把 `none` 悄悄改成 SQLite

**位置**：

- [`server/make-pkg.sh:183`](../../build/installer/macos/server/make-pkg.sh#L183) 永远写 `--database=auto`；
- [`server/scripts/postinstall:56`](../../build/installer/macos/server/scripts/postinstall#L56) 永远追加
  `--ops-database-path` 与 `--ops-listen`；
- [`ServerSetup.ConfigureOps`](../../src/Server/RemoteDesktop.Server.Shared/ServerSetup.cs#L458-L505)
  只要收到这两个参数就写；
- [`ServerSetup.Automatic`](../../src/Server/RemoteDesktop.Server.Shared/ServerSetup.cs#L577-L595)
  对 `Provider=none` 且没有连接串的已有配置返回 SQLite。

两个具体场景：

1. **`--database none` 的安装升级后变成 SQLite**。第一次用 `--database none` 装（内存模式），
   `appsettings.json` 里是 `Rendezvous:Database:Provider=none`；升级包默认带 `--database=auto`，
   `Automatic()` 看到 `none`、没有连接串，落到 `(sqlite, existing.Path ?? DefaultSqlitePath())`。
   运维明确选择的"每次重启换设备码"被改成了本地 SQLite。
2. **Ops 的两个键每次安装都被重写**。`--ops-*` 每次安装都传，`ConfigureOps` 无条件覆盖；
   而 Ops 服务的实际值又被 LaunchDaemon 里的 `Ops__Database__Path` / `Ops__Listen`
   环境变量盖住。于是 `appsettings.json` 里的 Ops 键既不是服务的唯一来源，
   也不是"手改就保留"的稳定配置：`--create-admin` 读 appsettings，服务读 env + appsettings，
   两者可能指向不同的库。文档把 appsettings 描述成统一配置，这里对不上。

这与 [`how-to-configure.txt:66`](../../build/installer/macos/server/how-to-configure.txt#L66)
"appsettings.json 在升级时不会被覆盖，所以手改的值会保留"和
[`CHANGELOG.md`](../../CHANGELOG.md) 的承诺不一致（第 598–600 行）：文件本身没被替换，
但其中几个键被 `--configure-system` 重写了。

**建议**：把"首装"与"升级"分开：

- postinstall 在复制 `appsettings.default.json` 前记录 `first_install`；
  `--database=auto` 与 `--ops-*` 只在首装时传，升级只传明确发生变化的值；
- 或者让 `Automatic()` 在 `Provider=none` 时保留 `none`，并让 `ConfigureOps` 区分
  "没给"与"给的是包默认值"；
- 把 `--ops-listen` / `--ops-database-path` 从 postinstall 的固定参数里拿掉，
  改为像 `configure-server.sh` 那样由运维显式修改（同时补上 Ops 的重启，见 M11）。

---

### M7 client / host 的 `Info.plist` 缺 `NSLocalNetworkUsageDescription`

**位置**：

- [`client/Info.plist:50-63`](../../build/installer/macos/client/Info.plist#L50-L63)
- [`host/Info.plist:59-75`](../../build/installer/macos/host/Info.plist#L59-L75)

两个 bundle 都声明了摄像头、麦克风、桌面/文稿/下载目录的用途，但都没有
`NSLocalNetworkUsageDescription`。macOS 15 (Sequoia) 起，应用访问局域网地址
（`10/8`、`172.16/12`、`192.168/16`）需要局域网权限；多份 2024–2025 的开发者报告
（Hermes、Superset、Cursor）在 macOS 15/26 上观察到：缺这个键时**没有弹窗、系统设置里也没有条目、
连接报 `No route to host`**。产品的最低系统是 macOS 26，典型部署又往往把 rendezvous 放在同一局域网，
P2P 也会直连局域网地址，所以这不是理论问题。

需要注意的边界：

- 默认的 `ws://127.0.0.1:5080/ws` 是 loopback，不受影响；公网服务器也不受影响。
- host 由 LaunchAgent 启动，Apple 论坛里有"launch agent 的局域网权限在批准后仍被拒"的报告；
  加键是必要条件，不是充分条件，必须实机走一遍授权流程。
- 这个键只是声明；权限弹窗由实际访问触发。代码里需要在首次连接前让系统有机会弹窗，
  并在结论页/排错文档里告诉用户"系统设置 → 隐私与安全性 → 本地网络"。

**建议**：给两个 `Info.plist` 加中文用途说明（例如"Remote Desktop 需要访问局域网，才能连接同一网络内的
服务器与其他设备。"），在局域网环境实测 client 与 host 的首连授权；把该权限写进结论页与
`docs/ops/README.md` 的 macOS 排错一节。

---

## 5. 中（P2）

### M8 host 的开发签名身份没有钉住，也没有检查随包原生库

**位置**：[`host/make-app.sh:106-109`](../../build/installer/macos/host/make-app.sh#L106-L109)

```bash
if [[ -z "$IDENTITY" ]]; then
    IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null \
        | sed -n 's/.*"\(.*\)".*/\1/p' | sort | sed -n '1p')"
fi
```

client 已经有一套成熟的机制：`development-identity` 文件、`APPLE_DEVELOPMENT_IDENTITY` 覆盖、
多张 Apple Development 证书时的警告（[`client/make-app.sh:142-184`](../../build/installer/macos/client/make-app.sh#L142-L184)）。
host 一个都没有：有第二张证书时静默挑排序第一张，下一次构建就可能换身份。
而 host 恰恰是**最依赖 TCC 稳定签名**的包（屏幕录制、辅助功能都记在它上面），
换身份等于让所有授权失效。

另外，client 的 `make-app.sh` 会检查 `libmsquic.dylib` 与 `libRemoteDesktopMacNative.dylib` 是否存在，
host 没有；`verify-packages` 也只检查 `RemoteDesktop.HostService` 可执行文件。也就是说：

- host 的 publish 少了 QUIC 库或原生辅助库时，包照样能打出来、`verify-packages` 也照样通过；
- 症状要到第一次连接/第一帧才出现（`DllNotFoundException` 或 QUIC 起不来）。

**建议**：

1. 把 client 的 identity pin / 多证书警告逻辑抽成共享片段，host 复用；
2. host 的 `make-app.sh` 增加与 client 相同的两个 dylib 检查；
3. `verify-packages` 的 macOS 分支对 client/host 两个包补查 `libmsquic.dylib` 与
   `libRemoteDesktopMacNative.dylib`（至少对分发构建要求存在）。

---

### M9 构建期的秘密随 `.pkg` 一起分发（`setup-options`）

**位置**：[`server/make-pkg.sh:169-189`](../../build/installer/macos/server/make-pkg.sh#L169-L189)

`setup-options` 是 payload 里的明文文件，可能包含：

- `--relay-key=<base64>`（中继票据签名密钥）
- `--postgres=<连接串>`（可能带数据库口令）
- `--tls-password=<口令>`

postinstall 用完会 `rm -f`，所以安装目录里不留；但 **`.pkg` 文件本身**仍然带着这些值，
`pkgutil --expand-full` 谁都能读出来。包又常常被归档、复制、放进共享目录。
文档的 macOS 选项表鼓励构建时直接传这些值（[`docs/ops/README.md`](../ops/README.md) 的对照表），
没有提醒"这个包要当作秘密保存"。

**建议**：

- 对可分发/会长期留存的包，只接受非秘密选项（`--urls`、`--relay-endpoint`、`--database` 等）；
  relay key、Postgres 口令、TLS 口令改用装完后的 `configure-server.sh` 设置；
- 如果必须支持构建期秘密，在文档和脚本输出里明确写"含秘密，安装后删除安装包"，并考虑
  `--relay-key`/`--tls-password` 从文件或环境读取，避免出现在 `ps` / shell history / CI 日志里；
- `--tls-certificate` 只是路径，证书文件本身不在包里；目标机的同一路径必须已有文件，这一点也应在选项说明里写清。

---

### M10 server 的 publish 输出不清理，旧文件会进包

**位置**：[`server/make-pkg.sh:97-107`](../../build/installer/macos/server/make-pkg.sh#L97-L107)

```bash
dotnet publish ... --output "$PUBLISH_DIR/$project"
```

`dotnet publish --output` 不会删除上一次发布留下、这一次不再产出的文件。host 的做法是发布前 `rm -rf "$PUBLISH_DIR"`
（[`host/make-app.sh:61-70`](../../build/installer/macos/host/make-app.sh#L61-L70)）；client 则交给 MSBuild 自己的
publish 目录。server 两者都不是：删除某个依赖、改名某个文件、换 RID 之后，旧文件仍会被 `cp -R` 进 payload。
`verify-packages` 只检查"该有的在里面"，不会发现"不该有的也在里面"。

**建议**：每个 project 发布前 `rm -rf "$PUBLISH_DIR/$project"`，或发布到 `mktemp -d` 再整体替换。
CHANGELOG 里已经出现过"macOS 的 dylib 被塞进 Linux 包"这类问题（[`CHANGELOG.md`](../../CHANGELOG.md) 第 592–596 行），
同一个包内残留是同一类风险。

---

### M11 `configure-server.sh` 改数据库后不重启 Ops

**位置**：[`configure-server.sh:84-89`](../../build/installer/macos/server/configure-server.sh#L84-L89)

脚本只 `kickstart` `com.remotedesktop.rendezvous` 与 `com.remotedesktop.relay`。但
`ServerSetup.ConfigureOps` 在数据库 provider 变化时**也会写 Ops 的 `appsettings.json`**
（`postgres` 时把 ConnectionString 写进 Ops；`sqlite` 时写 Path）。Ops 服务不重启，
就继续用旧库运行；下次重启才切到新库，此时旧库里的历史看起来"消失"了。

同样，如果以后给 `configure-server.sh` 增加 `--ops-listen` / `--ops-database-path`，也必须让 Ops 重启，
否则脚本打印的设置与实际运行的进程不一致。

**建议**：数据库 provider 变化时把 `com.remotedesktop.ops` 也加入重启列表；或者让脚本每次都重启三个
job（`--no-restart` 保持原语义）。

---

## 6. 低（P3）与次要观察

1. **server 卸载器不删文档目录，也不 forget receipts**：[`uninstall-server.sh:50-63`](../../build/installer/macos/server/uninstall-server.sh#L50-L63)
   只删 `/usr/local/remotedesktop-server` 与三个 plist；`make-pkg.sh:149-152` 安装的
   `/usr/local/share/doc/remotedesktop-server/` 会留下，`pkgutil --forget com.remotedesktop.server` 也没做。
   建议补上，或明确写"receipts 保留"。
2. **host 没有卸载器**：删掉 `.app` 后 `/Library/LaunchAgents/com.remotedesktop.host.plist` 仍在，
   每次登录 launchd 都会尝试启动一个不存在的可执行文件。文档给了手工命令（`docs/ops/README.md`），
   但包里没有对应脚本。建议仿照 server 提供 `uninstall-host.sh`，或至少把命令放进结论页。
3. **host bundle 里的 `appsettings.json` 实际不会被读取**：
   [`Program.cs:109`](../../src/Hosts/RemoteDesktop.HostService/Program.cs#L109) 用
   `Host.CreateApplicationBuilder(args)`，内容根是进程当前目录；LaunchAgent 没有
   `WorkingDirectory`，当前目录不是 `Contents/MacOS`。今天这个文件里的值（`Transport:RendezvousUri`
   默认值、`Telemetry:Enabled=false`）与代码默认值一致，所以看不出问题；但"随包的默认配置"这一层是死的。
   client 已经显式 `ContentRootPath = AppContext.BaseDirectory`（[`ClientHost.cs:51`](../../src/Hosts/RemoteDesktop.Client/ClientHost.cs#L51)）。
   建议 host 也设置内容根，或在 plist 里加 `WorkingDirectory`。
4. **安装器不检查 macOS 版本**：三个 `distribution.xml` 都没有 `<allowed-os-versions>`；
   client/host 的 `LSMinimumSystemVersion` 是 26.0，但 Installer 仍允许在 macOS 15 上装完，
   之后 app 才因 dyld 找不到 `libmsquic.dylib` 的依赖而失败。建议在 Distribution 里加 26.0 的下限，
   并在 welcome 里写清。
5. **server 包带 `appsettings.Development.json`**：Rendezvous/Relay 的发布输出里有这个文件，
   内含一份硬编码的开发 relay key。`ASPNETCORE_ENVIRONMENT` 默认是 Production，不会加载；
   但把开发配置打进交付物没有意义，建议发布时排除或改名。
6. **脚本卫生**：
   - [`server/scripts/postinstall:57-61`](../../build/installer/macos/server/scripts/postinstall#L57-L61)
     追加 `--ops-*` 之后 `$#` 永远大于 0，`else` 分支不可达；
   - client/host 的 `--arch` 只改包元数据（client 的 app 始终是 `osx-arm64`），传 `x86_64` 会得到
     "元数据说 Intel、内容是 arm64"的包；帮助文本里也没列出 `--arch`/`--app`；
   - [`host/make-pkg.sh:97-98`](../../build/installer/macos/host/make-pkg.sh#L97-L98) 的注释
     "The same bundle the client package installs" 是复制粘贴残留，实际是 host bundle。
7. **server 日志目录对本机用户可读**：`/usr/local/remotedesktop-server` 是 0755，`logs/` 也是 0755，
   日志里会记录 device id（`SignalingEndpoint.cs` 的 "Device {DeviceId} registered."）。
   `var/` 已经 0700，日志目录也可以收紧到 0700 或至少 0750；Linux 包同样如此，属于跨平台清理项。

---

## 7. 与 Linux / Windows 包的对照

这一轮 Linux 安装包刚做过一轮修复（`CHANGELOG.md` 的 "Linux 安装包按部署配置" 与
"Fixed — Linux 安装包评审"），其中三条在 macOS 上仍然缺失：

| Linux 已做 | macOS 现状 | 对应 |
|---|---|---|
| postinst 传 `--metrics-enabled` + `--seed-local-nodes` | 两者都没有 | M5 |
| 已登录用户的 host 服务立即启动，不等下次登录 | host 包没有 postinstall | M4 |
| 服务用户 `remotedesktop`，目录 0750 / 文件 0640 | 三个 LaunchDaemon 以 root 运行 | 已知边界（`docs/ops/README.md`），本轮不重复定级 |
| `.deb` 的 `appsettings.json` 不被覆盖 | 文件在，但 `--configure-system` 会重写 Ops 键 | M6 |

Windows 侧同样没有 `--metrics-enabled` / `--seed-local-nodes`（`build/installer/windows/server` 里
grep 不到），所以 M5 严格说是"macOS 与 Windows 都落后于 Linux"；本评审只要求 macOS 先补齐，
Windows 是否跟进属于另一个平台的决定。

---

## 8. 验证记录与外部依据

**本机命令（节选）**

```text
# 1. 三个包能展开，载荷与组件齐全
pkgutil --expand-full artifacts/installer/macos/RemoteDesktop-Client-macOS-arm64-0.3.0.pkg ...
pkgutil --expand      artifacts/installer/macos/RemoteDesktop-Host-macOS-arm64-0.3.0.pkg   ...
pkgutil --expand      artifacts/installer/macos/RemoteDesktop-Server-macOS-arm64-0.3.0.pkg ...

# 2. client 的 verify 行在 pipefail 下返回 141（M1）
bash -c 'set -euo pipefail; pkgutil --payload-files <client.pkg> | head -8'; echo $?   # 141

# 3. 用现有 .app 走完整 make-pkg（输出到临时目录）
build/installer/macos/client/make-pkg.sh --skip-app-build --output "$TMP"; echo $?        # 141

# 4. server 载荷签名状态（M3）
codesign -dv .../Rendezvous/RemoteDesktop.Server.Rendezvous   # Signature=adhoc, TeamIdentifier=not set
codesign -dv .../Rendezvous/libcoreclr.dylib                  # adhoc

# 5. 最低系统版本（M7 相关上下文）
otool -l .../RemoteDesktop.Client     | grep minos   # 12.0
otool -l .../libmsquic.dylib          | grep minos   # 26.0
otool -l .../libssl.4.dylib           | grep minos   # 26.0

# 6. 未加载的 LaunchAgent（M4）
launchctl print gui/$(id -u)/com.remotedesktop.host     # Could not find service
launchctl kickstart -k gui/$(id -u)/com.remotedesktop.host; echo $?   # 113

# 7. 三个脚本的语法
bash -n build/installer/macos/{client,host,server}/make-*.sh ...
```

**Apple 依据**

- `security find-identity -p codesigning` 不包含 Developer ID Installer：
  [Apple Developer Forums — "Developer ID Installer cert not usable for pkg signing (no Code Signing / 0 identities)"](https://developer.apple.com/forums/thread/822661)
- 公证要求载荷内每个可执行文件都用 Developer ID 签名并启用硬化运行时：
  [Apple Developer Forums thread 766950](https://developer.apple.com/forums/thread/766950)、
  [TUGboat 40(2) — "Harden"](https://tug.org/tug2019/preprints/koch-harden.pdf)
- macOS 15+ 本地网络权限与 `NSLocalNetworkUsageDescription`：
  [Hermes issue #81563](https://github.com/NousResearch/hermes-agent/issues/81563)、
  [Apple Developer Forums thread 778457](https://developer.apple.com/forums/thread/778457)、
  [Apple TN3179](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)

---

## 9. 建议的修复顺序

1. **M1 / M2 / M3**：不发版就永远发现不了的三处。先修 client 的 141 与 Installer 身份查找，
   再决定 server 是"签载荷并公证"还是"只签包、明确不公证"。
2. **M4 / M5**：装完不能用和装完没数据，直接影响首装验收。两者都可以照 Linux 的 postinst 补。
3. **M6**：升级保留配置是 1.0.0 已经承诺过的行为，越早修越少运维踩坑。
4. **M7**：如果目标部署包含局域网，尽快加键并做一次真机授权验证；如果只支持公网服务器，至少把限制写进文档。
5. **M8–M11** 与 §6：排在发布之后，属于包质量与运维体验。

## 10. 一句话总结

**包装配方的骨架是对的，问题集中在"最后一公里"：签名身份的查找方式、server 载荷的签名、
host 装完之后的启动，以及 Ops 首装/升级的配置语义。** 前三项让带证书的发版根本发不出去，
后两项让不带证书的包"装得上、跑不起来/没有数据"。
