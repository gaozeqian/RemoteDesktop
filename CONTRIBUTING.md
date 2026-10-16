# 贡献指南 (Contributing)

感谢您关注并为 Remote Desktop 贡献代码！为了保证代码质量与架构一致性，请在提交 Pull Request 前阅读以下指南。

## 🛠 开发环境准备

1. **SDK 要求**: .NET 10 SDK。
2. **IDE 推荐**: Visual Studio 2022 (Preview) / JetBrains Rider / VS Code (配合 C# Dev Kit)。
3. **构建系统**: 本项目使用较新的 `.slnx` 解决方案格式，请确保您的 IDE 支持。

## 🏗 本地编译与测试

在根目录执行以下命令，确保所有代码编译通过并且没有破坏架构规则：

```bash
# 1. 还原与编译
dotnet build RemoteDesktop.slnx

# 2. 运行单元测试与架构校验（非常重要！）
dotnet test RemoteDesktop.slnx
```

### 测试项目命名

`tests/` 下的项目一律是 `<区域>.<种类>Tests`，种类只有两种：

| 种类 | 含义 | 例子 |
|---|---|---|
| `UnitTests` | 不需要外部服务或网络，随时可跑 | `RemoteDesktop.Core.UnitTests`、`RemoteDesktop.Platform.Windows.UnitTests` |
| `IntegrationTests` | 需要真实网络、容器或其他进程 | `RemoteDesktop.Transport.IntegrationTests` |

**后缀必须从这个集合里取。** 名字里没有 `UnitTests` / `IntegrationTests` 的项目会让「按类型筛选」变成一件靠不住的事——
`**/*.UnitTests` 会漏掉它，而漏掉是静默的。加新测试项目时请照这个模式命名，别用 `FooTests` 或 `Foo.Test`。

平台专有的用例（只在某个 OS 上有意义）仍然放在对应平台的 `UnitTests` 项目里，用 `Assert.SkipUnless(...)`
在别的系统上跳过，而不是为每个平台单开一种后缀。

## 📦 构建产物布局

所有构建产物都在仓库根的 `artifacts/`（已 gitignore），按 `<类别>/<平台>/` 组织：

| 路径 | 是什么 |
|---|---|
| `artifacts/installer/<平台>/` | **交付物**：安装包（`installer/windows/` 的 `.msi`、`installer/macos/` 的 `.pkg`、`installer/linux/` 的 `.deb`）。角色写在文件名里（`RemoteDesktop-Windows-x64-*` 与 `RemoteDesktop-Server-Windows-*`） |
| `artifacts/app/<平台>/` | 打包前的应用本体。macOS 是 `RemoteDesktop.app`；Windows 是各角色的 publish 输出（`app/windows/client/`、`app/windows/server/`、`app/windows/host/`），因为同一台机器上会同时出多个角色 |
| `artifacts/native/<平台>/` | 原生辅助库与随包分发的本地库（macOS 的 `libRemoteDesktopMacNative.dylib`、`libmsquic.dylib`） |

`dotnet build` / `dotnet publish` 的产物也在这里，用 SDK 自己的布局——`Directory.Build.props` 设了
`UseArtifactsOutput` + `ArtifactsPath`：

| 路径 | 是什么 |
|---|---|
| `artifacts/bin/<项目>/<pivot>/` | 编译输出（`pivot` 形如 `debug`、`release_linux-x64`） |
| `artifacts/obj/<项目>/<pivot>/` | 中间产物 |
| `artifacts/publish/<项目>/<pivot>/` | `dotnet publish` 的输出（**不在** `bin/` 下面，这是 SDK 的约定） |
| `artifacts/packages/` | `dotnet pack` 的输出 |

所以项目目录里**没有** `bin/` 和 `obj/` 了：23 个项目不再各长出两个目录。要清空构建产物，删 `artifacts/bin` 与
`artifacts/obj` 即可，不必逐个项目找。`build/installer/` 下的安装器项目（WiX 与 net48 的 CustomActions）是例外，
它们带着自己那份（故意留空的）`Directory.Build.props`，不导入根目录的这份，产物仍在项目内。

**只有一个产物根目录。** 新增平台照 `<类别>/<平台>/` 加一层即可；不要再另起一个 `artifacts/` 目录（比如写在 `build/` 里面）—— 那样"产物在哪"就会有两个答案，每加一个平台都要重新选边。

`<平台>` 取 `windows` / `macos` / `linux`——是**平台**，不是发行版或包格式：`.deb` 因此落在 `installer/linux/` 而不是 `installer/debian/`。

## 📐 架构与依赖规则

本项目采用严格的**洋葱架构/整洁架构**，依赖方向只能**由外向内**。此规则由 `RemoteDesktop.Architecture.UnitTests` 强制校验。

* `Domain` (领域层) 不能引用任何其他层。
* `Abstractions` (抽象端口) 只能引用 `Domain`。
* `Platform.*` (平台实现) 和 `Media` (具体技术) **只能**依赖 `Abstractions`，绝不能直接引用其他实现或业务逻辑层。
* `Features.*` (独立功能模块) 彼此之间禁止互相引用，它们通过 `Application` 层的统一接口交互。

如果你引入了非法的依赖关系，`dotnet test` 将会失败。

## 📝 提交规范

1. 提交前确保通过 `dotnet test` 所有的单元测试。
2. 新增的核心网络或编解码功能，需补充对应的测试用例。
3. 请在 PR 描述中详细说明改动背景、受影响的模块，以及如何测试该改动。
