<!-- markdownlint-disable MD041 -- GitHub renders this as a form, and its first line is deliberately a comment for the submitter rather than a title. -->

<!--
提交前把不适用的小节删掉，不要留空标题。格式与内容要求见 CONTRIBUTING.md 与 docs/STYLE.md。
-->

## 改动背景

<!-- 为什么改？解决什么问题、对应哪个 issue / 需求。没有背景的 diff 无法评审。 -->

## 改了什么

<!-- 一句话能说清就一句话；涉及多处就列出来。 -->

## 受影响的模块

<!-- 勾选，或写明具体项目路径。 -->
- [ ] `src/Core/*`（Domain / Abstractions / Protocol / Signaling / Transport / Application）
- [ ] `src/Features/*`
- [ ] `src/Platform/*` / `src/Media` / `native/`
- [ ] `src/Hosts/*` / `src/Server/*`
- [ ] 构建、打包或 CI（`build/`、`.github/`）
- [ ] 文档（`docs/`、`README.md`、`CONTRIBUTING.md`）

## 如何验证

<!-- 评审者按这段应该能自己复现你的结论。命令、步骤、预期结果。 -->

## 检查清单

- [ ] `dotnet test --solution RemoteDesktop.slnx` 通过（含架构依赖与仓库结构断言）
- [ ] 新增的核心网络 / 编解码 / 平台功能补了对应测试
- [ ] 改了用户可见行为或平台能力，已同步更新对应的 `docs/` 文档
- [ ] 有值得记录的变更，已写入 `CHANGELOG.md` 的 `[未发布]` 一节
- [ ] 移动或重命名了文件？已全仓搜索旧路径（引用散在 `.md`、`.cs`、`.yml`、`.sh`、`.ps1` 里）
- [ ] 只改了文档？`dotnet test --project tests/RemoteDesktop.Architecture.UnitTests` 通过（含文档规范断言）

## 备注

<!-- 有意的取舍、已知的遗留问题、还没验证的部分（"代码写了但没在真机上跑过"要明说）。 -->
