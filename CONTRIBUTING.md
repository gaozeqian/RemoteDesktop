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

## 📦 构建产物布局

所有构建产物都在仓库根的 `artifacts/`（已 gitignore），按 `<类别>/<平台>/` 组织：

| 路径 | 是什么 |
|---|---|
| `artifacts/installer/<平台>/` | **交付物**：安装包（`.msi` / `.pkg` / 未来的 `.deb`）。角色写在文件名里（`RemoteDesktop-Windows-x64-*` 与 `RemoteDesktop-Server-Windows-*`） |
| `artifacts/app/<平台>/` | 打包前的应用本体。macOS 是 `RemoteDesktop.app`；Windows 是各角色的 publish 输出（`app/windows/client/`、`app/windows/server/`），因为同一台机器上会同时出两个角色 |
| `artifacts/native/<平台>/` | 原生辅助库与随包分发的本地库（macOS 的 `libRemoteDesktopMacNative.dylib`、`libmsquic.dylib`） |

**只有一个产物根目录。** 新增平台照 `<类别>/<平台>/` 加一层即可；不要再另起一个 `artifacts/` 目录（比如写在 `build/` 里面）—— 那样"产物在哪"就会有两个答案，每加一个平台都要重新选边。

## 📐 架构与依赖规则

本项目采用严格的**洋葱架构/整洁架构**，依赖方向只能**由外向内**。此规则由 `RemoteDesktop.ArchitectureTests` 强制校验。

* `Domain` (领域层) 不能引用任何其他层。
* `Abstractions` (抽象端口) 只能引用 `Domain`。
* `Platform.*` (平台实现) 和 `Media` (具体技术) **只能**依赖 `Abstractions`，绝不能直接引用其他实现或业务逻辑层。
* `Features.*` (独立功能模块) 彼此之间禁止互相引用，它们通过 `Application` 层的统一接口交互。

如果你引入了非法的依赖关系，`dotnet test` 将会失败。

## 📝 提交规范

1. 提交前确保通过 `dotnet test` 所有的单元测试。
2. 新增的核心网络或编解码功能，需补充对应的测试用例。
3. 请在 PR 描述中详细说明改动背景、受影响的模块，以及如何测试该改动。
