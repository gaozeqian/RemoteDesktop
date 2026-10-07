# 设备列表的存储

这份文档说明"最近连接过的设备"这个列表今天存在哪里、为什么这么存，以及**账号体系上线后**要把它搬到
服务器时，哪些决定必须现在做、哪些坑必须提前避开。今天没有账号，也没有同步——这里描述的是已经落地的
数据格式和明确留出的接口。

## 今天怎么存

客户端把列表放在数据目录下的 `recent_devices.json`（默认 `%AppData%\RemoteDesktop\`，由 `DataDirectory`
配置决定）。结构是带版本号的：

```json
{
  "Version": 1,
  "Devices": [
    {
      "DeviceId": "123 456 789",
      "Alias": "办公室台式机",
      "LastConnectedUtc": "2026-10-07T02:11:04.5120000+00:00",
      "UpdatedAtUtc": "2026-10-07T02:11:04.5120000+00:00",
      "DeletedAtUtc": null
    }
  ]
}
```

读写由 `IRecentDevicesStore` / `RecentDevicesStore`（`src/Hosts/RemoteDesktop.Client/Storage/`）负责，界面只
通过接口访问它——这个接口就是将来接同步时的缝，见下文。

几处与旧格式不同的地方：

- **写入是原子的**：先写 `recent_devices.json.tmp` 再替换。中途崩溃不会再留下一个截断的文件，也就不会出现
  "下次启动读不出来 → 当成空列表 → 一保存就把全部设备抹掉"这条链。
- **读不出来的文件会被让开**：解析失败或版本比自己新时，文件被改名为 `recent_devices.json.corrupt-<时间戳>`
  保留，而不是留在原位等着被下一次保存覆盖。列表因此永远可以人工找回。
- **旧格式会被迁移**：早期文件是一个裸数组，条目里还带着明文 `SavedPassword`。读到这种文件时，密码被搬进
  系统保护的密钥库，文件当场重写成上面的带版本结构——升级即把明文从磁盘上拿掉，而不是等用户下次改点什么。

## 密码为什么不进列表

`RecentDeviceEntry` 里**没有**密码字段，这是刻意的，三条理由：

1. **跨机器没有意义**。密码用 DPAPI 以 `CurrentUser` 作用域加密，换一台机器根本解不开，同步过去也是废数据。
2. **服务器不该看到它**。`docs/security.md` 把中转服务器列为对手，整套设计是端到端加密、服务器只转发密文。
   把设备密码放上去，等于要求用户相信一个我们明确假设不可信的组件。
3. **它是本机的事**。"记住密码"是这台机器上的便利，不是账号的属性。

所以密码存在 `ISecretStore`（客户端是 DPAPI，`secrets/` 目录）里，名字为 `recent-device-<设备代码>`，
由 `IRecentDevicesStore.GetPasswordAsync` / `UpdatePasswordAsync` 读写。`UpdatePasswordAsync(id, null)` 会
**删除**密钥——这条路径以前在 API 上是缺失的，"取消勾选记住密码"因此永远清不掉已存的密码。

将来如果产品坚持要"新机器登录后自动带上密码"，唯一可接受的形态是：客户端用账号口令派生密钥（Argon2id）
做端到端加密，服务器只存不透明密文。因为密码不在条目里，这个决定**不需要改动数据格式**就能后加。

## 字段语义与合并规则

账号同步一上线，"两台机器上的同一份列表怎么合"就必须有确定答案。三个时间字段就是为它准备的：

| 字段 | 含义 | 合并规则 |
|---|---|---|
| `LastConnectedUtc` | 最后一次连上这台设备的时间 | 取两边**较晚**的值。这是各机器的观测，不是编辑 |
| `UpdatedAtUtc` | 用户最后一次**改这个条目**的时间（改别名、或删除） | 较晚者胜。连接行为不动它 |
| `DeletedAtUtc` | 用户删除它的时间；null 表示还在列表里 | 非空即视为删除；与对方的 `UpdatedAtUtc` 比较，较晚者胜 |

注意 `LastConnectedUtc` 用的是取最大值而不是"最后写入者胜"：连接是观测，谁先谁后无所谓，晚的那次才是事实。
把连接的写入也当成编辑（顺带推进 `UpdatedAtUtc`）会让"在 A 机改名"被"在 B 机连接"无声覆盖。

**墓碑**（`DeletedAtUtc`）是删除必须软删的原因：在 A 机上删掉设备，如果只是从文件里拿掉，B 机的副本会在
下次同步时把它原样送回来，用户会觉得"删不掉"。带时间戳的墓碑让删除可以被合并、被传播，合并完再清理。
在没有同步的今天，墓碑只是留在文件里、被 `GetAllAsync` 过滤掉——所以现在删掉的设备**看起来**是没了。
连上同一个设备会让它复活（`DeletedAtUtc` 清空、`UpdatedAtUtc` 推进），不会出现"删过一次就再也回不来"。

## 账号作用域

`RecentDevicesStore` 的构造函数接受一个可选的 `accountId`：

- 不传（今天的情况）：列表在 `dataDirectory/recent_devices.json`，密钥名 `recent-device-<设备代码>`。
- 传入：列表在 `dataDirectory/accounts/<accountId>/recent_devices.json`，密钥名带账号前缀。

这样同一个 Windows 用户登录两个账号时，两份列表和两套记住的密码互不串扰。现在就把这个参数留出来，是因为
它的成本是零；等到有了账号再改，已经写下的数据就要迁移。`accountId` 来自服务器，所以构造时会校验它不含
路径分隔符和 `..`——它必须只是一个名字，不能决定文件写到哪儿。

## 账号体系上线时怎么接

**客户端**：`MainWindowViewModel` 依赖的是接口，不是具体类型，所以换实现不用动界面。预期是在中间加一层：

```
IRecentDevicesStore
  └─ SyncedRecentDevicesStore(本地: RecentDevicesStore, 远端: IAccountDeviceListClient)
       读：本地立即返回，后台按 UpdatedAtUtc 合并
       写：本地先落（离线可用）+ 入队推送
```

能这么接的前提正是"条目里没有密码"——同步层搬运 `RecentDeviceEntry` 这件事本身就不可能泄漏密码。

**服务端**：PostgreSQL 已经在跑（Rendezvous 的 `PostgresDeviceRegistry`，裸 Npgsql + 启动时
`EnsureSchemaAsync`，连接串为空则回退内存），设备代码与证书指纹的映射已经持久化在
`device_registrations` 里，新表直接引用它的 `device_id`：

```sql
CREATE TABLE IF NOT EXISTS account_devices (
    account_id        BIGINT NOT NULL,
    device_id         BIGINT NOT NULL,      -- 对应 device_registrations.device_id
    alias             TEXT,
    last_connected_at TIMESTAMPTZ,
    updated_at        TIMESTAMPTZ NOT NULL,
    deleted_at        TIMESTAMPTZ,
    PRIMARY KEY (account_id, device_id)
);
```

沿用既有的写法（裸 SQL、启动建表、`Rendezvous:PostgresConnectionString` 配置），不要为这一张表引入 EF Core。

两点别低估：

- **账号体系本身的工作量远大于列表同步**。现在的 Rendezvous 对客户端只有 `/ws` 信令和 `/health`，没有注册、
  登录、令牌、吊销、找回中的任何一样。还要决定设备证书怎么绑定到账号——最自然的做法是设备证书注册进账号，
  这样设备开机不需要输账号密码。
- **设备代码会失效**。它绑定证书指纹，设备重装就是新指纹、新代码。列表里的旧条目会变成死链接，不能假设
  `device_id` 永远有效。界面现在显示 **在线 / 离线 / 状态未知** 三态，而**未知不等于离线**：服务器对"从未注册"
  和"当前离线"的回答故意完全相同（见 `docs/security.md` 的在线状态查询一段），所以界面只能说这么多，
  "该设备可能已重新注册"的提示只在有证据时（连接返回"对方设备不在线"）出现。
  **这三态只存在内存里，不写进 `recent_devices.json`**：它是机器的观测值而不是用户意图，写进去只会给将来的
  账号同步增加一条需要合并的字段。合并规则因此完全不受影响。

## 现在有意不做的

- 不引入 EF Core，不写客户端同步框架：一个接口、一种格式、一组时间字段就够了。
- 不把 `DeviceId` 换成 GUID 之类的"分布式友好"标识：服务端已经用 `device_id BIGINT UNIQUE` 关联好了。
- 不清理墓碑：合并规则需要它们。等同步真正落地时再定保留期（比如超过 N 天的墓碑可以丢弃）。
- **不做密码同步**，也不为此预留字段。
