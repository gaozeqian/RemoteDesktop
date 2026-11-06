# native/ — 平台原生源码与本地库获取脚本

这层放两类东西，都**不是**构建产物（产物见文末）：

| 目录 | 是什么 |
|---|---|
| [`macos/`](macos/) | macOS 的 Objective-C 辅助库源码（除 `rd_installer_plugin.m` 外的 `rd_*.m` 与头文件，由 `build-native.sh` 编译成 `libRemoteDesktopMacNative.dylib`）、`fetch-libmsquic.sh`，以及安装器配置面板 `rd_installer_plugin.m` / `build-installer-plugin.sh` |
| [`linux/`](linux/) | `fetch-libmsquic.sh`：从微软的 apt 源下载 `libmsquic` 的 `.deb`，取出 `libmsquic.so.<版本>` 并建好 `libmsquic.so.2` 软链 |

`rd_*.h` 是**手写的契约头**，不是生成物：`rd_native.h` 声明扁平 C ABI，托管侧在
`src/Platform/RemoteDesktop.Platform.MacOS/Interop/MacNativeInterop.cs` 里手工镜像同一组枚举与结构体。
两边没有生成器，改名或加字段时**要同时改两处**。

## 为什么没有 `windows/`

Windows 侧不需要编译出来的辅助库：它对系统 API 的调用是 C# 的 P/Invoke（`DllImport`），
分散在 `src/Platform/RemoteDesktop.Platform.Windows/` 下各功能文件里（`Agent/`、`Display/`、`Input/`、
`Clipboard/`、`Terminal/` 等），没有集中的 `Interop` 目录。macOS 之所以有这一层，
是因为 ScreenCaptureKit / VideoToolbox / AVFoundation 这几个框架没有可用的 C# 绑定，必须自己写一层 ObjC。

所以 `native/<平台>/` 的规则不是"每个平台一个目录"，而是"**需要编译原生代码的平台一个目录**"。

## 命名

| 语言 | 风格 | 例子 |
|---|---|---|
| Objective-C 源码与头文件 | snake_case，统一 `rd_` 前缀（`rd_` = RemoteDesktop） | `rd_screen_capture.m`、`rd_video_encoder.m`、`rd_native.h` |
| shell 脚本 | kebab-case | `build-native.sh`、`fetch-libmsquic.sh` |

`rd_` 前缀是为了让这些符号在 `nm` 输出和崩溃日志里一眼能认出来（导出的函数是 `rd_capture_*`、
`rd_clipboard_*` 这类）。脚本的 kebab-case 与 `build/` 下一致，见 [`build/README.md`](../build/README.md)。
这两条里**只有脚本命名**由 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs` 断言
（`build/` 与 `native/` 下的 `.sh` 必须是 kebab-case）；`.m` / `.h` 的 `rd_` 前缀与 snake_case 靠约定和评审，没有测试。

## 两份 `fetch-libmsquic.sh` 长得不一样，是有意的

同名、同职责（把 `libmsquic` 弄到手），但内容不同，各自的开头都写了原因：

* **Linux**：从微软的 apt 源（`packages.microsoft.com/ubuntu/26.04/prod`，固定 Ubuntu 26.04）下载固定版本的
  `libmsquic` `.deb`，按写死的 sha256 校验，只取里面的 `.so`（`libmsquic.so.<版本>`，另建 `libmsquic.so.2` 软链）。
  OpenSSL 用系统那份，不重定位。
* **macOS**：还要一并处理 OpenSSL 的 dylib，把它们的 install name 改写成 `@loader_path`，再 `dlopen` 验一遍，
  确认这组库在打包前就是自洽的。

Windows 没有对应脚本——`msquic.dll` 由运行时包提供。

## 产物落在哪里

不在这里。两个 `fetch` 脚本与 `build-native.sh` 的输出都写到仓库根的 `artifacts/lib/<平台>/`
（已 gitignore）。产物目录特意不叫 `artifacts/native/`：
与这棵源码树同名会让"删了就没了的源码"和"删了可以再生的产物"看起来像一回事。约定见
[`CONTRIBUTING.md`](../CONTRIBUTING.md) 的“构建产物布局”一节。

安装器面板是例外：它不是随应用运行的原生库，而是打包时才编、装进 `.pkg` 的 Installer 插件。
`build-installer-plugin.sh --role … --output …` 把它写到调用方给的目录（三个 `make-pkg.sh` 用的是
`mktemp -d` 里的临时目录）；手动构建时默认落在 `artifacts/installer/macos/plugins/<角色>/`。
