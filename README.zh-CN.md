# OpenEliteDangerous

[English](README.md) | [简体中文](README.zh-CN.md)

面向 **Elite Dangerous: Odyssey** 的非官方互操作性实验项目，使用 C#、WinUI 和 XFE ServerInteractive，提供管理端、离线适配器，以及统一持有指挥官、存档和世界定义的服务器。

> **仅供非商业教学、学习和研究参考，禁止商业用途。** 本项目与 Frontier Developments 无隶属、合作、背书或官方支持关系。Elite Dangerous 的商标和游戏内容属于各自权利人。使用者须自行合法取得兼容游戏副本；本仓库不提供官方程序、资源包、账号权益或官方服务访问资格。
>
> 项目自有源码采用[教学研究非商业许可](LICENSE)，另附[中文参考译文](LICENSE.zh-CN.md)。这是**源码公开许可，并非 OSI 批准的开源许可**。第三方材料继续适用各自条款，见[第三方声明](THIRD-PARTY-NOTICES.md)。软件按现状提供，不作担保；声明无法消除全部法律、合同或商业风险。

## 功能

- 本机账号注册、登录、管理员/玩家角色，以及信用点、货舱、材料和舰船的独立编辑权限。
- 搜索库存、修改数量与信用点，从本机 Player Journal 导入部分状态副本。
- 根据已记录的船型和配装创建飞船、切换活动飞船、替换兼容模块，以及存入和取出仓库模块。
- 原生档案导入/导出、版本冲突检查、备份、跨进程文件锁与原子提交。编辑前保存并返回主菜单，修改在下一次载入时生效。
- 同一 ServerInteractive 进程提供回环管理 API 与 HTTPS 游戏入口，统一持有联机账号、存档、势力、商品定义和公共世界数据。
- 启动器通过固定证书的 HTTPS 连接服务器，原生游戏通过本机 IPC 与适配器通信。
- 管理界面、启动提示和应用业务报错支持中英文。在**偏好设置 → 显示语言**切换，选择会保存，当前登录会话会保留。

## 当前范围

当前适配 **Windows x64 Odyssey `2026.09.03.332841`**。原生兼容检查固定游戏程序及已授权的 `E:\Elite Dangerous\Products\elite-dangerous-odyssey-64` 副本；更改界面目录不会自动改变底层钩子目标。其他版本或目录需要更新适配器并重新验证。

此前原生验收覆盖 SOLO 飞行、保存与重载，以及**同一电脑的两个客户端**互见、双向位置和姿态同步、中央保存与同进程重进。双客户端测试通过 81 项检查，最终位置误差小于 0.069 米。这不代表官方全部功能已还原：跨电脑、局域网或公网联机，断线恢复、战斗、NPC、跃迁和空间站交易覆盖仍未验证或尚不完整。详见[原生联机测试](docs/NATIVE-ONLINE-TESTING.md)和 [SOLO 说明](docs/OFFLINE-SOLO.md)。

私有抓包、个人存档、报告和部署产物不进入仓库。原生验收需要自行准备兼容游戏和本机数据；克隆仓库不会获得可玩的指挥官或官方银河数据库。

## 编译与测试

需要 Windows x64、`global.json` 指定的 .NET 10 SDK、PowerShell 7，以及用于 NativeAOT 发布的 Visual Studio C++/Windows SDK 工具。WinUI 和 XFE 依赖通过 NuGet 还原。

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
dotnet run --project Client/XFEEliteDangerous.Tool -- --help --language en-US
dotnet run --project Client/XFEEliteDangerous.Bootstrapper -- --status --language zh-CN
```

语言值为 `system`、`en-US`、`zh-CN`。控制台使用 `--language`，启动脚本使用 `-Language`，也支持 `XFE_LANGUAGE` 环境变量。HTTP 客户端通过 `Accept-Language` 指定服务器报错语言。协议字段、账号/飞船编号、网络数值格式和用户数据不会被翻译。该设置控制项目工具，原版游戏的语言另行配置。

回归测试覆盖原生格式与加载器、存档冲突、协议和资源一致性。隐藏管理端检查会构造原生控件并验证语言切换，不激活窗口或发送桌面输入。独立的 HTTP/HTTPS 双语言检查：

```powershell
./scripts/Test-Localization.ps1
```

## 本机部署

先准备自己有权使用的兼容原生工作区，再发布：

```powershell
./scripts/Deploy-Local.ps1 -SourceWorkspace <你的原生工作区> -EnableOnline
./Start-Admin.cmd -Language zh-CN
```

部署保留已有工作区，并生成 `runtime/deployment.json`。默认数据目录为 `%LOCALAPPDATA%\XFEEliteDangerous\Server`，`Workspace` 保存存档，`inventory-backups` 保存备份，`logs` 保存日志。凭据、私有证书和会话须保持私有。

| 入口 | 功能 |
| --- | --- |
| `Start-Admin.cmd` | 启动本机服务器与管理端 |
| `Start-Server.cmd` / `Stop-Server.cmd` | 启动 / 正常停止服务器 |
| `Start-Game.cmd` | 启动默认本机指挥官 |
| `Start-Online.cmd` | 通过已配置服务器启动默认指挥官 |

管理 API 默认地址为 `http://127.0.0.1:48080/api/`，HTTPS 游戏端口为 `48081`。管理端的**服务器联机**页面可填写管理员提供的地址、证书指纹和账号。管理 API 应保持回环监听；跨电脑的网络可达性需单独验证。详情见[本机部署](docs/LOCAL-DEPLOYMENT.md)与[统一服务器联机](docs/ONLINE-PROTOCOL.md)。

## 项目布局

| 路径 | 内容 |
| --- | --- |
| `Client/XFEEliteDangerous.Desktop`、`Desktop/SharedUI` | WinUI 管理端与按角色显示的页面 |
| `Server/XFEEliteDangerous.Server` | ServerInteractive 控制台服务器 |
| `Client/XFEEliteDangerous.ManagementClient` | 回环管理客户端 |
| `Client/XFEEliteDangerous.OnlineClient` | HTTPS 客户端与原生 IPC 宿主 |
| `Local/XFEEliteDangerous.Core` | 账号、权限、库存、存档和公共世界 |
| `Client/XFEEliteDangerous.Client/Localization` | 中英文资源 |
| `Client/XFEEliteDangerous.Tool` | 本机命令行工具 |
| `Client/XFEEliteDangerous.Bootstrapper` | 兼容检查与原生启动器 |
| `Client/XFEEliteDangerous.OfflineRuntime`、`Local/XFEEliteDangerous.ReadBusiness` | NativeAOT 运行时与可重载业务模块 |
| `Client/XFEEliteDangerous.NativeFormat`、`Local/data` | 原生格式与互操作元数据 |
| `scripts` | 构建、部署、诊断和自动化检查 |

分段 JSONL 日志包含请求关联、原生函数位置与 C# 源码行号，敏感值会脱敏。可使用 `scripts/Read-OfflineLog.ps1 -RunDirectory <运行目录>` 检查。`scripts/Publish-ReadBusiness.ps1` 支持替换业务模块，修改固定原生钩子仍需重启游戏。

## 使用与公开边界

仅在有权使用的副本、数据和环境中开展研究。本仓库不授予游戏资产、官方服务、付费功能或他人账号的权利。禁止销售项目、使用项目自有代码提供收费服务，或冒充官方产品。再分发须保留许可和第三方声明。提交或公开问题中不得包含令牌、凭据、私有抓包、个人 Journal、游戏程序或资源包。

源码公开与 OSI 开源的区别见[开源定义](https://opensource.org/osd)。免责声明不等于逆向、再分发或商业部署已获得法律授权。
