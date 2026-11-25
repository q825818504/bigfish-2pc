# LeaguePatchCollection 启动游戏修复说明

基于 **Hextech 1.0.7（海克斯）** 的“启动游戏”实现，对开源项目
[IllumiLove/LeaguePatchCollection](https://github.com/IllumiLove/LeaguePatchCollection)（v6.3.2，net8.0-windows）
的启动链路做了 4 处修改，成品见同目录 `LeaguePatchCollection.exe`。

---

## 一、为什么旧版“启动游戏”会报错

原版 `StartButton_Click` 的实际行为：

```
1) ProcessUtil.TerminateRiotServices()        // 无条件强杀 RiotClientServices / LeagueClient / Lion-Win64-Shipping
2) RiotClient.Launch(args)
     → RiotClientServices.exe
         --client-config-url=http://127.0.0.1:<ConfigPort>     // ← 关键
         --launch-product=league_of_legends --launch-patchline=live
```

三个根因：

| # | 根因 | 后果 |
|---|---|---|
| 1 | **强制注入 `--client-config-url`**：把 Riot Client 的 keystone 配置源改成本地 ConfigProxy | 新版 Riot Client 的配置结构（`keystone.*` / `lol.client_settings.*`）已变，本地代理返回的 JSON 缺字段或类型不符，客户端在启动阶段直接报错/白屏/卡在 “Loading” |
| 2 | **无条件 Kill 客户端进程**：即便客户端已经登录，也被杀掉重建 | 登录会话丢失 → 重启后要重新登录，叠加 #1 就是“启动失败/报错” |
| 3 | **LCU 路径写死 `C:\Riot Games\League of Legends`** | 装在 D 盘 / WeGame 版时读不到 `lockfile`，依赖 LCU 的功能（自动接受、重启 UX、躲避等）全部静默失败 |

Hextech（海克斯）之所以“启动不报错”，是因为它的启动路径是**标准启动**：
先探测 `LeagueClientUx.exe / LeagueClient.exe / RiotClientServices.exe`，
已运行就直接接管（从命令行/`lockfile` 取 `--app-port` + `--remoting-auth-token`），
没运行才用官方参数拉起，**全程不篡改 `--client-config-url`**。

---

## 二、改了哪些文件

| 文件 | 修改 |
|---|---|
| `Launcher.cs` | `RiotClient.Launch(args, useConfigProxy=false)`；默认**不再**注入 `--client-config-url`；自动补齐 `--launch-product`/`--launch-patchline`；失败自动回退纯净启动；新增 `RiotClient.IsRunning()` |
| `LeagueProxy.cs` | `LaunchRCS(args, useConfigProxy=false)`；代理没起来时自动降级为纯净启动 |
| `LeaguePatchCollectionUX.cs` | `StartButton_Click` 改为 `async`：解析 `--config-proxy` 开关；**客户端已在运行就不再杀进程**（直接复用单实例）；启动后 `await LcuProbe.WaitUntilReadyAsync(90s)` 确认客户端真的起来 |
| `RiotHelperLib/LcuProbe.cs`（新增） | 按 Hextech 的方式定位 LCU：进程 `MainModule` → 注册表 → 磁盘多路径；解析 `lockfile` 的 `port:password`；提供 `WaitUntilReadyAsync` |
| `RiotHelperLib/RiotLocalApi.cs` | 去掉 `C:\Riot Games\...` 硬编码，改用 `LcuProbe.FindInstallDir()` |

---

## 三、怎么用

### 1. 默认模式（推荐，不报错）

直接双击 `LeaguePatchCollection.exe` → 点 **LAUNCH CLIENT**。

等价命令：

```
RiotClientServices.exe --launch-product=league_of_legends --launch-patchline=live
```

- 客户端**已登录**：Riot Client 单实例接管参数，直接进 LoL，不重启、不丢登录态。
- 客户端**未运行**：干净拉起，进 Riot 登录器 → 登录后进 LoL。
- 这时 **禁用 Vanguard / 旧版荣誉 / 改名绕过 / 去臃肿** 这些配置补丁**不会生效**（它们依赖本地配置代理），但启动一定不报错。

### 2. 补丁模式（需要配置修补时）

在界面的 **Args** 框里加一个开关：

```
--launch-product=league_of_legends --launch-patchline=live --config-proxy
```

只有带 `--config-proxy` 才会把 `--client-config-url` 指向本地代理，
此时 Vanguard 禁用等补丁生效；如果启动异常，删掉 `--config-proxy` 即可回到默认模式。

> 其它参数（`--allow-multiple-clients` 等）保持原样，`--config-proxy` 不会被传给 Riot Client。

### 3. 自检

```
%APPDATA%\LeaguePatchCollection\settings.json
```

确认 `Args` 字段。默认值：

```json
"Args": "--launch-product=league_of_legends --launch-patchline=live"
```

---

## 四、验证记录

- `dotnet build -c Release` ✅ 0 error（1 个既有 nullable 警告）
- 单文件发布 ✅ `LeaguePatchCollection.exe`（3.6 MB，framework-dependent，需 .NET 8 Desktop Runtime x64；本机已装 8.0.34）
- 运行 ✅ 主窗口正常，`settings.json` 正确生成
- `C:\ProgramData\Riot Games\RiotClientInstalls.json` 解析 ✅
  → `rc_live = D:/Riot Games/Riot Client/RiotClientServices.exe`
- LCU 定位 ✅ 兼容 `D:\Riot Games\League of Legends` 与 WeGame 版
  （旧代码只认 `C:\Riot Games\...`，你机器上必然读不到 lockfile）

---

## 五、如果你想要“海克斯那样的一键上游戏”

Hextech 的完整链路是：

1. 确认 Riot Client 已登录（`LeagueClientUx.exe` 在跑）
2. 读 `--app-port` / `--remoting-auth-token`
3. 通过 LCU API 自动接受对局、跳过结算、启动对局
4. 把 `core.dll` 释放成 `<Game>\Game\version.dll`（DLL 劫持注入）

本修复已经把 1、2 做进 `LcuProbe`；3 对应项目里已有的
`/lol-matchmaking/v1/ready-check/accept`、`/lol-lobby/v2/play-again`、`/riotclient/kill-and-restart-ux`；
4 是注入环节，本程序不涉及（那是外挂本体）。

需要的话可以再加一个「LAUNCH + 自动接受」按钮，把 3 串起来。

---

## 六、源码与产物

- 源码（已修改）：`_src\LeaguePatchCollection-main\`
- 构建输出：`_build\`
- 单文件发布：`_publish_fx\LeaguePatchCollection.exe`
- 成品：`LeaguePatchCollection.exe`（本目录）
- 本地 .NET 8 SDK：`_dotnet\sdk\`（仅构建用，可删）
