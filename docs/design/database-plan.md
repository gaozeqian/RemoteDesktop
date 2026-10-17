# 服务端持久化：SQLite 支持与多数据库扩展（计划）

这份文件是**计划类**（`docs/STYLE.md` §1）：记录还要做什么、为什么这么做、哪些还没验证。做完之后，
结论并入 [`design/README.md`](README.md) 的"服务端持久化"一节，本文件删除。

一句话：把"信令服务器用什么存设备代码"从**一个连接串的存在与否**变成**一个显式的 provider**，
然后 SQLite 只是第一个新 provider，不是特例。

## 1. 目标与非目标

目标：

- 不装 PostgreSQL 的单机也能持久化设备代码，重启、升级后设备码不变（无人值守可用）。
- "再加一种数据库"的成本是**加文件**，不是改一遍宿主。
- 顺手修掉两处现存缺陷（§7），它们正好都在这条链路上。

明确不做：

- **不引入 EF Core，也不引入迁移框架。** 仓库已有决定（见 `docs/ops/README.md` 的"账号体系上线时怎么接"），
  而这次的表仍然只有一张。触发重新评估的条件写在 §3.3。
- **不做多库之间的自动数据迁移**（§6.2 给出文档化的一次性步骤）。
- 不改协议、不改客户端、不改 `device_id` 的语义与取值范围。
- 不承诺 SQLite 下的多实例部署（§6.1）。

## 2. 现状

服务端的数据面比看上去小得多：

| 项 | 数量 | 位置 |
|---|---|---|
| 表 | 1 张（`device_registrations`） | — |
| SQL 语句 | 4 条（建表+索引 / SELECT / upsert / UPDATE） | `PostgresDeviceRegistry.cs` |
| 相关文件 | 2 个（153 + 33 行） | `src/Server/RemoteDesktop.Server.Rendezvous/` |
| ORM / 迁移 | 无（启动时 `EnsureSchemaAsync` 建表） | — |
| 其余状态 | 全在内存（presence、配额、限流、会话、metrics） | — |

所以工作量不在 SQL 翻译。真正的两个结构性问题是：

1. **组成根用类型嗅探决定要不要建表**：`RendezvousServer.PostgresSchemaInitializer` 里
   `if (registry is PostgresDeviceRegistry postgres)`。加第二个 provider 就得在这里加第二个分支，
   加第三个再加一个——宿主成了 provider 清单的副本。
2. **健康检查和注册表读的不是同一个来源**：注册表走 `RendezvousServer.PostgresConnectionString(configuration)`
   （`Rendezvous:PostgresConnectionString`，回退 `ConnectionStrings:Postgres`），
   而 `PostgresHealthCheck` 只读 `RendezvousOptions.PostgresConnectionString`。
   只配了 `ConnectionStrings:Postgres` 的部署，健康检查会在**构造时**抛 `InvalidOperationException`。

## 3. 设计

### 3.1 分层：存储层独立成一个项目

新增 `src/Server/RemoteDesktop.Server.Storage`（`Microsoft.NET.Sdk`，不引用 ASP.NET）：

```text
RemoteDesktop.Server.Rendezvous  ──▶  RemoteDesktop.Server.Storage  ──▶  Npgsql
        （宿主：DI、健康检查端点、生命周期）        （端口、算法、方言、配置）      └─▶  Microsoft.Data.Sqlite
```

方向由编译器保证，不是约定。宿主不认识任何 provider 名字；存储层不认识 `IHealthCheck` 和 `IHostedService`
——它们是宿主关注点，留在 Rendezvous。

| 文件 | 职责 | 约 |
|---|---|---|
| `DeviceRegistry.cs` | `DeviceRegistration`、`IDeviceRegistry`、`InMemoryDeviceRegistry`（从宿主搬来） | 60 |
| `IDeviceRegistryStore.cs` | 第二个端口：建表 + 探活 | 20 |
| `RelationalDeviceRegistry.cs` | 缓存、分配、重试、写串行化——**唯一一份** | 150 |
| `IDeviceRegistryDialect.cs` | 方言接缝（5 个成员） | 25 |
| `PostgresDeviceRegistry.cs` | 子类 + 内嵌方言（保留现有类型名与日志类别） | 110 |
| `SqliteDeviceRegistry.cs` | 子类 + 内嵌方言 + 内嵌 `DbDataSource` | 130 |
| `DatabaseSettings.cs` | 配置模型、新旧键解析、启动时校验 | 90 |
| `DatabaseRegistration.cs` | `AddDeviceRegistry(IServiceCollection, IConfiguration)`，唯一的 switch | 50 |

宿主侧新增两个小文件：`DeviceRegistryHealthCheck.cs`（`IHealthCheck` 适配器，依赖 `IDeviceRegistryStore`）、
`DeviceRegistryInitializer.cs`（`IHostedService`，调 `EnsureSchemaAsync`）。原来的
`PostgresHealthCheck.cs` 与内嵌的 `PostgresSchemaInitializer` 删除。

### 3.2 两个端口

```csharp
public interface IDeviceRegistry            // 不变
{
    Task<DeviceRegistration> GetOrAllocateAsync(string fingerprint, CancellationToken ct = default);
}

public interface IDeviceRegistryStore : IDeviceRegistry
{
    Task EnsureSchemaAsync(CancellationToken ct);   // 启动时建表；失败即启动失败
    Task ProbeAsync(CancellationToken ct);          // 探活；不可达时抛
}
```

`InMemoryDeviceRegistry` **不**实现 `IDeviceRegistryStore`。于是"要不要建表、要不要挂健康检查"变成
"这个服务注册了没有"，两个 `if` 都消失。`ProbeAsync` 不返回 `HealthCheckResult`，端口就不被 ASP.NET 污染。

### 3.3 一套算法 + 方言

`RelationalDeviceRegistry` 持有全部逻辑，方言只回答"这个数据库怎么写这四句 SQL、怎么认唯一冲突"：

```csharp
internal interface IDeviceRegistryDialect
{
    string CreateSchemaSql { get; }      // 可含多条语句（SQLite 的 PRAGMA 在这里）
    string SelectDeviceIdSql { get; }
    string InsertIfAbsentSql { get; }    // INSERT … ON CONFLICT (fingerprint) DO NOTHING
    string TouchLastSeenSql { get; }
    bool IsUniqueViolation(Exception ex); // device_id 撞车 → 换一个随机 id 重试
    bool SerializeWrites { get; }        // 只有单写者的数据库需要
}
```

两个决定值得写下来：

- **upsert 改成 `DO NOTHING` + 受影响行数，不再用 `RETURNING` 猜 id。** 现在 `finalId == allocatedId`
  是"用返回值反推有没有插入"，而 MySQL 没有 `RETURNING`、SQL Server 要用 `OUTPUT`，
  这个形状搬不过去。改成"插进去 1 行 = 新设备，0 行 = 已存在，再 SELECT 一次"之后，
  `created` 从推断变成事实，且四种数据库同形。代价是重连且缓存未命中时多一次 SELECT——那是冷路径。
- **时间戳由 .NET 传参（`@now`），不在 SQL 里取当前时间。** 否则方言里永远有
  `NOW()` / `datetime('now')` / `SYSDATE` 三份差异。现有 PG 表的 `DEFAULT NOW()` 保留不动
  （永远用不到，但改了就是一次没必要的 schema 变更）。

为什么不选另外两条路：

- **每个库一份独立实现**（复制这 150 行）：最直白，但"随机 id 重试、缓存、`created` 的判定"
  这三处细节要复制 N 份，而它们恰恰是最容易抄错的部分。契约测试也会退化成 N 份。
- **EF Core**：一套模型多库确实省事，但会引入迁移体系、约 1 MB 依赖和启动开销，为一张表不值。
  **触发重新评估的条件**：表数量到 5 张以上，或者需要跨表事务——那时这笔账要重算。

### 3.4 SQLite 的三个具体决定

1. **WAL + busy 重试 + 单写者串行化。** 连接串 `Data Source=…;Mode=ReadWriteCreate;Cache=Shared;Default Timeout=5`，
   建表时执行 `PRAGMA journal_mode=WAL`。`Microsoft.Data.Sqlite` 的 `Default Timeout` 就是命令在
   `SQLITE_BUSY` 上的重试窗口。`SerializeWrites = true` 时，基类用一把 `SemaphoreSlim(1,1)` 串起写，
   读不进锁。
2. **`last_seen` 拿不到写锁就跳过。** 现在重连时是 `_ = UpdateLastSeenAsync(...)`（fire-and-forget）。
   串行化之后，一次重连风暴会在锁后面堆出无界队列——而这个字段本来就是尽力而为（失败只记 Debug）。
   所以走 `Wait(0)`：抢不到就放弃，队列恒为空。PG 不需要这个约束（`SerializeWrites = false`）。
3. **`CHECK (device_id BETWEEN 100000000 AND 999999999)`。** SQLite 是动态类型，`INTEGER` 不挡脏数据；
   而 `(uint)(long)result` 读到 TEXT 会直接 `InvalidCastException`。约束是免费的。

### 3.5 配置模型与兼容

新增 `Rendezvous:Database` 一节，旧键全部继续生效：

| 键 | 含义 | 优先级 |
|---|---|---|
| `Rendezvous:Database:Provider` | `none` / `postgres` / `sqlite` | 最高 |
| `Rendezvous:Database:ConnectionString` | PostgreSQL 连接串 | — |
| `Rendezvous:Database:Path` | SQLite 文件路径 | — |
| `Rendezvous:PostgresConnectionString` | 旧键，等价于 `Provider=postgres` | 次之 |
| `ConnectionStrings:Postgres` | 更旧的键，同上 | 最低 |

解析规则（`DatabaseSettings.Resolve`，一处实现，`ValidateOnStart` 里跑）：

- 显式 `Provider` 优先；未知值 → 启动失败，错误信息里列出支持的值。
- 没写 `Provider` 但有任一旧键 → `postgres`，并在启动日志里说一句"建议改用 `Database:Provider`"。
- 都没有 → `none`（今天的默认行为，零配置升级不受影响）。
- `sqlite` 没有 `Path` → 启动失败并点名缺哪个设置。**不猜默认路径**：
  代码不该知道安装布局，路径由安装器写。

`auto` 在新安装里的含义（安装器把 provider 的默认值写成 `auto`）：

- 已有非空 `PostgresConnectionString` → 保持 `postgres`（升级不改行为）。
- 否则 → `sqlite` + 该平台的默认路径。即"auto = 这台机器能给的最好的"，
  和 `RelayEndpoint`/`RelaySharedKey` 里 `auto` 的语义一致。

### 3.6 文件位置与权限

| 平台 | 路径 | 属主 |
|---|---|---|
| Windows | `%ProgramData%\Remote Desktop Server\rendezvous.db` | 服务以 LocalSystem 运行 |
| Linux | `/var/lib/remotedesktop/rendezvous.db` | 服务账号 |
| macOS | `/usr/local/remotedesktop-server/var/rendezvous.db` | launchd 以 root 运行 |
| Docker | 卷挂载，`Data Source=/data/rendezvous.db` | 容器 `APP_UID` |

目录由安装器创建并设权限；服务器启动时只做 `Directory.CreateDirectory`（幂等）并在失败时把话说清楚。
**文件权限是安全项**：能写这个文件的人可以改 `device_id`，等于能劫持设备码。目标 0600 / 仅服务账号。
WAL 会在旁边生成 `-wal`、`-shm`，备份必须用 `VACUUM INTO` 或 `.backup`，不能直接 `cp`。

### 3.7 再加一种数据库要动什么

以 MySQL 为例，五步，不需要碰 Rendezvous：

1. `Directory.Packages.props` 加 `MySqlConnector`。
2. `RemoteDesktop.Server.Storage.csproj` 加那条 `PackageReference`。
3. 新增 `MySqlDeviceRegistry.cs`（子类 + 内嵌方言，约 120 行；`INSERT IGNORE` 恰好同形）。
4. `DatabaseRegistration.cs` 的 switch 加一支，`DatabaseSettings` 的枚举加一个值。
5. 复用既有契约测试：`DeviceRegistryContractTests` 加一个派生类，六条用例免费到手。

安装器与文档按需跟进（不跟进也不影响程序能跑，只是没人能配置它）。

## 4. 阶段

每个阶段都能独立合入、独立回滚。预估是"一个人、熟悉这个仓库"的量级。

| 阶段 | 内容 | 验收 | 预估 | 状态 |
|---|---|---|---|---|
| 1 | 建 Storage 项目；端口搬家；`RelationalDeviceRegistry` + PG 方言；宿主改为依赖端口 | 现有测试全绿；PG 部署配置不变仍能启动、两次连接拿到同一设备码 | 2 天 | 已完成 |
| 2 | SQLite 适配器、健康检查、路径与权限、契约测试（并发/文件消失/碰撞重试） | `RemoteDesktop.Server.Storage.UnitTests` 全绿；临时目录里建库、重启后 id 不变 | 1.5 天 | 已完成 |
| 3 | `DatabaseSettings` 新旧键解析；`--configure-system` 新参数；`--postgres` 兼容；修 §7 的缺陷 | 兼容矩阵单测（6 条）；`ServerSetupTests` 补"不给就保持不动" | 1 天 | 已完成 |
| 4 | 三平台安装器、docker-compose、`Invoke-SecurityAudit.ps1` | CI 的打包作业通过；手工安装后 `/health` 为 Healthy 且设备码跨重启不变 | 1.5 天 | 代码已完成，**未在真机安装验证** |
| 5 | 文档（design 一节、ops 多处、CHANGELOG）、重生成 `THIRD-PARTY-NOTICES.md` | `DocumentationTests` + markdownlint 通过 | 0.5 天 | 已完成 |
| 6 | 验证矩阵逐项实测；未验证项标注 | §8 的表全部有结论 | 0.5 天 | 进行中（见 §8） |

合计约 **7 人日**。比"直接加一个 SQLite 实现"（约 1 人日）多出来的部分，就是抽象层、契约测试和
§7 的两个缺陷——这笔钱买的是"第三种数据库只要 120 行"，以及一张能跑的回归网。

与计划不一致的三处，都记在这里：

- **SQLite 只接受文件路径，不接受连接串。** 计划里写的是 `Path` 或 `ConnectionString` 二选一；两种写法
  指向同一个东西，只是让配置多一种说法，所以收成 `Rendezvous:Database:Path`。
- **Linux 的默认路径是 `/var/lib/remotedesktop-server/rendezvous.db`**，不是计划里的
  `/var/lib/remotedesktop`——安装包已经把这个目录建成了服务账号的家目录，数据放同一个地方。
- **MSI 的设置页没有加"数据库类型"下拉。** 交互安装的默认值就是 `auto`（= SQLite），框里只留 PostgreSQL
  连接串这个进阶用法；换 SQLite 路径用 `DATABASEPATH` 属性或 `Set-ServerConfiguration.ps1`。
  少改一次对话框布局，也就少一次把 TLS 那一块挤出页面的机会。

## 5. 影响面

- **运维**：多了一个要备份、要盯磁盘的文件；`docs/ops/README.md` 里"安装包不装数据库"那段警告已重写，
  并新增"数据库怎么选"一节（含换库时搬数据的命令）。
- **部署拓扑**：SQLite 意味着单节点（§6.1）。Docker 默认仍是 PostgreSQL，不跟着变。
- **安全**：少了明文口令（PG 连接串现在明文躺在所有人可读的 `appsettings.json` 里），
  多了一个必须正确设权限的文件（安装器建 0700 目录）。`Invoke-SecurityAudit.ps1` 的 `Test-Database`
  已改成按 provider 说话：SQLite 会检查文件在不在，并提醒备份。
- **包体积**：每个 RID 多约 1.5–2 MB 的 `e_sqlite3` 原生库。自包含发布三个平台都有对应 RID；
  Docker 是 Debian 基座（glibc），无额外系统依赖。`verify-packages` 只查关键二进制是否存在，不受影响。
- **CI**：`THIRD-PARTY-NOTICES.md` 已重新生成。顺带发现它原来只扫三个客户端程序、不含两个服务器，
  Npgsql 一直没被列进去；这次把服务器项目加进了生成脚本，于是多出 8 行（Npgsql + SQLite 全家）。
- **日志类别**：基类接 `ILogger`（非泛型），子类传 `ILogger<PostgresDeviceRegistry>`，
  现有日志过滤规则不变。

## 6. 风险

### 6.1 单机化

共享 PG 时可以起多个 Rendezvous 挂负载均衡；SQLite 每个实例一个文件，两个实例会给同一指纹发不同设备码。
**选 SQLite 就是承诺单节点。** 安装器与文档都要写这一句，不能只写在计划里。

### 6.2 换 provider 会丢设备码

`device_id` 存在 PG 里，切到 SQLite 是空库，所有无人值守机器下次连接拿到新码、旧地址失效。
不做自动迁移工具，但文档必须给出一次性步骤（`\copy` 导出 CSV → `sqlite3 .import`），
并在 CHANGELOG 里明说"换库即重发，除非按文档迁移"。

### 6.3 新的故障模式

- **文件被删 = 静默重置**：SQLite 打开不存在的文件会**新建空库**，服务照样报 healthy，所有设备换码。
  所以 `ProbeAsync` 要检查表是否存在，而不是只 `SELECT 1`。
- **只读挂载 / 磁盘满**：启动时建表失败，服务器起不来（比静默降级好，但要有清楚的错误信息）。
- **网络盘**：SQLite 在 NFS/SMB 上会损坏。文档写死"只支持本地盘"。
- **容器属主**：`.NET` 镜像以 `APP_UID` 运行，卷属主不对就写不进去。

### 6.4 默认值变更

安装包默认从"内存"变成"sqlite"只影响**新装**（`appsettings.json` 升级时不覆盖）。
但"安装时无法创建文件"会让一个原本能跑的部署起不来——这是有意的取舍：静默降级意味着设备码每次重启都变，
那比启动失败更难排查。安装器在写配置时先试创建目录，失败就退回 `none` 并在 `server-setup.log` 里说明。

## 7. 顺带修掉的两个缺陷

1. **配置被误清空。** `ServerSetup.Configure` 对"没给"和"给空"不作区分：`SetupCommands` 在
   `--postgres` 缺席时传 `null`，`Expand(null)` 得到 `""`，`IsAuto("")` 为真，于是写入空串。
   而 `Set-ServerConfiguration.ps1` 只在参数被绑定时才传 `--postgres`，文档也承诺"没给的保持不动"。
   结果：只想改 TLS 的人跑一次 `-TlsGenerate`，数据库设置就被清掉，下次重启所有设备码改变。
   修法：`null` = 不动，`"auto"`/`""` = 明确清空。现有测试 `Having_no_database_is_said_out_loud`
   要跟着改成显式传 `auto`。
2. **健康检查与注册表读不同的键**（§2 第 2 条）。端口化之后两者共用同一个实例，这个问题自然消失。

## 8. 验证矩阵

| 项 | 方式 | 状态 |
|---|---|---|
| SQLite 跨重启 id 不变 | `Ids_survive_a_new_registry_instance`（新实例、空缓存、同一文件） | 已通过 |
| 32 并发写不抛 busy | 契约测试，内存与 SQLite 各跑一遍 | 已通过 |
| 同一指纹并发注册只有一个 `Created` | 契约测试（16 路并发） | 已通过 |
| 设备 id 撞车后换一个重试 | `A_colliding_device_id_is_retried_with_another_draw`（注入固定分配器） | 已通过 |
| 文件被删后探活失败 | `A_deleted_database_file_is_reported_as_unreachable` | 已通过 |
| 真实进程起得来 | `Rendezvous__Database__Provider=sqlite` 起真进程，`/health` 返回 `Healthy`，目录里出现 `rd.db`、`-wal`、`-shm` | 已通过（macOS 26 / arm64，2026-10-17） |
| 自包含发布带 `e_sqlite3` | 三个 RID 各 `dotnet publish --self-contained`，检查产物 | 已通过：`e_sqlite3.dll` / `libe_sqlite3.so` / `libe_sqlite3.dylib` 都在 |
| PG 部署零行为变化 | DDL 与 upsert 由 `PostgresDialectTests` 钉成文本；真 PG 未跑（本机没有 Docker） | **部分验证** |
| Windows MSI 新装默认 sqlite | 真机安装 + `server-setup.log` + 重启服务 | 未验证（CI 只构建 MSI，不安装） |
| macOS `.pkg` 同上 | 同上 | 未验证 |
| Linux `.deb` 同上 | 同上 | 未验证 |
| Docker 卷权限 | `docker compose up` 后看容器能否建库 | 未验证（本机没有 Docker） |

实测记录（2026-10-17，macOS 26.7.1 / arm64）：

```text
$ Rendezvous__Database__Provider=sqlite Rendezvous__Database__Path=/tmp/rd-smoke/rd.db \
  Urls=http://127.0.0.1:5099 Rendezvous__ReflectorPort=5098 dotnet RemoteDesktop.Server.Rendezvous.dll
  → Device registry schema verified. / Now listening on: http://127.0.0.1:5099
$ curl http://127.0.0.1:5099/health
  Healthy
$ ls /tmp/rd-smoke
  rd.db  rd.db-shm  rd.db-wal
```

"未验证"是现状，不是结论；每项做完把状态改成"已通过 + 在哪台机器上"（`docs/STYLE.md` §6）。

## 9. 已确认的决定

- **D1 安装包默认值：新装默认 SQLite。** 落地为 `auto`——本机已有 PostgreSQL 就继续用它，否则建 SQLite 库。
  升级与重跑配置步骤因此不会改变已有部署用哪种数据库。
- **D2 迁移：只给文档步骤，不做工具。** `docs/ops/README.md` 的"数据库怎么选"一节有 `\copy` + `.import`
  的命令；`device_id` 必须原样搬。
- **D3 项目拆分：接受新增 `RemoteDesktop.Server.Storage`。** 驱动依赖留在那一层，宿主不认识 provider 名字。
