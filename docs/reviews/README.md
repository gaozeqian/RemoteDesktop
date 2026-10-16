# 评审快照归档

这里放的是**某一次评审在当时看到的东西**——审计、结构普查、选型评估。它们是记录，不是规范。

## 这里的文件会变吗

**不会。** 每份文件顶部都有一行"历史快照 · 已冻结"的标注。一条结论写进来之后就定在那里，
哪怕后来又改了主意也不回头修改它——否则"当时怎么想的"就没有地方可查了。

唯一的例外是**路径**：2026-10-17 把三份快照正文里的旧文档路径统一替换成了现路径（结论与建议
一字未动）——快照冻结的是判断，不是让读者顺着死链接扑空的地址。

由此推出两条规矩：

- **不要拿这里的结论当现行约定。** 现行约定在 [`docs/STYLE.md`](../STYLE.md)、
  [`CONTRIBUTING.md`](../../CONTRIBUTING.md) 与 [`build/README.md`](../../build/README.md) 里，
  结构类约定另有 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs` 断言兜底。
- **发现了新问题，不要往这里追加。** 新开一份新的评审（命名见 [`docs/STYLE.md`](../STYLE.md) §3），
  在这里加一行索引。

## 归档了什么

| 文件 | 主题 | 日期 | 它的结论现在在哪 |
|---|---|---|---|
| [`repo-structure-review-2026-10-16.md`](repo-structure-review-2026-10-16.md) | 目录结构：同一天三轮（跨平台规范核对 → 第一轮修复的落地核验 → 逐目录逐文件全量普查） | 2026-10-16 | 约定进 `CONTRIBUTING.md` 与 `build/README.md`；六条断言进 `RepositoryStructureTests` |
| [`security-review-2026-10-12.md`](security-review-2026-10-12.md) | 安全审计：逐项定级与对抗性实测 | 2026-10-12 | 规范性结论并入 [`docs/design/README.md`](../design/README.md) §3 |
| [`macos-port-review-2026-10-13.md`](macos-port-review-2026-10-13.md) | macOS 移植的架构与技术选型评估 | 2026-10-13 | 后续做法见 [`docs/ports/README.md`](../ports/README.md) |

同一主题的多轮评审**合订成一份**（`repo-structure-review-2026-10-16.md` 里按轮分节），
不为每一轮单开一个文件——一轮一个文件只会让归档变成一串读不出差别的名字。
