# 评审快照归档

这里放的是**某一次评审在当时看到的东西**——审计、结构普查、选型评估。它们是记录，不是规范。

## 这里的文件会变吗

**不会。** 每份文件顶部都有一行"历史快照 · 已冻结"的标注。一条结论写进来之后就定在那里，
哪怕后来又改了主意也不回头修改它——否则"当时怎么想的"就没有地方可查了。

由此推出两条规矩：

- **不要拿这里的结论当现行约定。** 现行约定在 [`docs/STYLE.md`](../STYLE.md)、
  [`CONTRIBUTING.md`](../../CONTRIBUTING.md) 与 [`build/README.md`](../../build/README.md) 里，
  结构类约定另有 `tests/RemoteDesktop.Architecture.UnitTests/RepositoryStructureTests.cs` 断言兜底。
- **发现了新问题，不要往这里追加。** 新开一份新的评审（命名见 [`docs/STYLE.md`](../STYLE.md) §3），
  在这里加一行索引。

## 归档了什么

| 文件 | 主题 | 日期 | 它的结论现在在哪 |
|---|---|---|---|
| [`windows-installer-review-2026-10-26.md`](windows-installer-review-2026-10-26.md) | Windows 三个安装包（client / host / server：Rendezvous + Relay + Ops）的打包、升级、签名与安装后生命周期 | 2026-10-26 | W1–W12 已修复（见 `CHANGELOG.md` 的 `[未发布]` 下 Added 与 Fixed 两处的"Windows 安装包评审"）；现行做法以 `build/README.md`、`docs/ops/README.md`、`build/signing/README.md` 为准 |
| [`macos-installer-review-2026-10-25.md`](macos-installer-review-2026-10-25.md) | macOS 三个安装包（client / host / server）的打包、签名、公证与安装后生命周期 | 2026-10-25 | 评审后的修复见 `CHANGELOG.md` 的 `[未发布]` 下 Added 的"macOS 安装包安装期配置"与 Fixed 的"macOS 安装包评审"；现行做法以 `build/README.md`、`docs/ops/README.md`、`build/signing/README.md` 为准 |
| [`ui-and-icon-review-2026-10-18.md`](ui-and-icon-review-2026-10-18.md) | 客户端 UI 与产品图标：macOS 26 与 Windows 11 两轮实机逐屏（含亮色外观）+ 图标资产/打包核对 | 2026-10-18 | R1/R2/R4/R7/R10、W1–W8 已修复；R5/W9（四段版本号）已解决，R6、R9 已改，W10 定为有意保留；只剩图标栅格（R3）待设计决定 |
| [`ui-consistency-review-2026-10-17.md`](ui-consistency-review-2026-10-17.md) | 客户端 UI 跨平台一致性：视图/样式盘点 + 平台驱动差异逐项核对 | 2026-10-17 | 三处文案缺陷（U1–U3）与一处限制告知缺口（U4）已修复；现行事实以 [`docs/usage/README.md`](../usage/README.md) 为准 |
| [`repo-structure-review-2026-10-16.md`](repo-structure-review-2026-10-16.md) | 目录结构：同一天三轮（跨平台规范核对 → 第一轮修复的落地核验 → 逐目录逐文件全量普查） | 2026-10-16 | 约定进 `CONTRIBUTING.md` 与 `build/README.md`；当时的六条约定进了 `RepositoryStructureTests`（该文件现有 7 个测试）；第八节留给下一轮的 A-7（`THIRD-PARTY-NOTICES.md` 过期检查）后来已加进 CI |
| [`security-review-2026-10-12.md`](security-review-2026-10-12.md) | 安全审计：逐项定级与对抗性实测 | 2026-10-12 | 规范性结论并入 [`docs/design/README.md`](../design/README.md) §3；其中 #4"密码证明不是 PAKE"已由 SRP-6a 解决（见 `CHANGELOG.md` 的"上线前加固"），全局认证失败锁与对应的限流结论也已不再成立 |
| [`macos-port-review-2026-10-13.md`](macos-port-review-2026-10-13.md) | macOS 移植的架构与技术选型评估 | 2026-10-13 | 后续做法见 [`docs/ports/README.md`](../ports/README.md)；建议①（`ILoginItem` 不带平台特性）已落实，建议②（原生互操作面按域拆分）仍未做 |

同一主题的多轮评审**合订成一份**（`repo-structure-review-2026-10-16.md` 里按轮分节），
不为每一轮单开一个文件——一轮一个文件只会让归档变成一串读不出差别的名字。
