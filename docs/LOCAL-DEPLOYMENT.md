# 本机服务器与 WinUI 管理端

管理端基于本机 XFEExtension.NetCore.VSIX 的 WinUI 模板，模板默认项目名为 `XFEWinUIApplication`。保留 WinUI 3、WinUIHelper 的 PageManager、导航/消息/加载服务、主题与 AutoConfig 配置。现有 `Client/XFEEliteDangerous.Desktop` 已改为通过 API 管理服务端数据，不再自行打开存档。

控制台服务器位于 `Server/XFEEliteDangerous.Server`，使用 `XFEExtension.NetCore.ServerInteractive 4.0.0` 与 `XFEExtension.NetCore 5.2.1`，绑定 `http://127.0.0.1:48080/api/`。游戏继续使用进程内离线数据回调；管理服务器停止不会让正在运行的游戏连接官方服务。

最新部署包含同进程的游戏 HTTPS 入口 `0.0.0.0:48081/game/`，统一存档与公共世界数据。751 项单元测试、22 项真实网络检查和 81 项原生双客户端检查通过，覆盖本机互见、双向飞行/转向同步及中央保存重进。使用和验证范围见 [统一服务器联机](ONLINE-PROTOCOL.md)，自动化步骤见 [原生联机测试](NATIVE-ONLINE-TESTING.md)。旧离线模式仍不依赖管理服务器，联机模式必须保持服务器连接。

双击根目录 `Start-Admin.cmd`：自动启动本机服务器并打开管理端，通过当前 Windows 用户可读取的本机访问文件登录管理员。普通账号仍支持邮箱/密码和按类别授权。`Start-Server.cmd` 单独启动服务器，`Stop-Server.cmd` 正常停止。服务器自身是控制台程序，开发时可运行：

```powershell
dotnet run --project Server/XFEEliteDangerous.Server -- --background false
```

启动脚本启动后台服务时隐藏窗口，服务自己写控制台日志，不依赖启动脚本的输出管道。`server.json` 配置及存档位于 `%LOCALAPPDATA%\XFEEliteDangerous\Server`；`Workspace` 为独立部署副本，原先 artifacts 中的已验收档案保留。已有部署存档不会被重新发布覆盖。

在“仓库与装备”选择指挥官后，可以调整信用点，添加、删除和修改货舱物品及工程材料。旧档案先点击“初始化原生材料目录”。材料采用原生 `microresources.inventory` 的 ID、类型、容量和数量结构，数量不能超过容量。当前包含 84 种有已确认容量的工程材料和 455 种公共商品定义，未复制在线玩家的数量或发现记录。

“飞船工坊”支持从现有船型与配装创建独立飞船、编辑管理端名称和呼号、切换活动飞船、删除非活动飞船、选择已记录的兼容模块、卸下存入仓库及取出装备。模块目录来自该原生档案；不按任意字符串生成未记录的船型或模块。名称与呼号目前用于管理端辨识。新账号可以从本地起始星系建立 Sidewinder 档案；高级导入/导出保留完整原生档案的身份、版本和结构校验。

进入游戏前，可在工坊点击“启动此指挥官的 SOLO”，或双击 `Start-Game.cmd` 启动默认指挥官。启动器使用固定 E 盘 Odyssey 副本、文件哈希与零网络能力的 AppContainer，并通过原生可用菜单进入 SOLO。

游戏运行时，请先保存并返回主菜单再编辑。服务器根据当前启动记录与原生流程日志拒绝在 InGame 等状态写入；所有修改在下一次载入 SOLO 生效。新旧运行时的存档结构不同，旧运行时仍在运行时会明确拒绝编辑。每次写入检查版本，冲突返回 409；结构修改和完整导入保留原生备份，提交使用跨进程文件锁与原子替换。

从源码重新部署：

```powershell
./scripts/Deploy-Local.ps1 -SourceWorkspace <已有原生工作区>
```

脚本发布服务器、管理端、启动器、NativeAOT 运行时和对应业务模块，保留稳定数据目录，并生成忽略目录 `runtime/deployment.json`。构建日志和验收报告放在每代发布的 `reports` 中。服务日志位于数据目录的 `logs`；分段 JSONL 保留关联编号、C# 文件行号与异常堆栈。本机访问文件只由当前用户和 SYSTEM 读取，访问值和登录会话不出现在启动参数或日志中。

管理 API 以 POST `/api/control` 接收 `{"operation":"...","arguments":{...}}`，会话放在 Authorization Bearer 头。操作包含 auth、accounts、permissions、settings、commander、catalog、inventory/edit、fleet、fleet/edit、native/initialize、native/import、native/export 和 materials/prepare。GET `/api/health` 可检查就绪状态；POST `/api/shutdown` 需要本机访问凭据。错误状态为 400（输入）、401（会话）、403（权限）、409（版本或游戏状态冲突）和 500（内部错误）。

验证覆盖 731 项单元测试、48 项真实 HTTP 集成检查、实际 C# API 客户端，以及 99 项隐藏 WinUI 控件检查。修改后的原生游戏 Journal 已确认飞船 ID 2、信用点 1、Platinum 1 和 iron 1；实际飞行约 160.7 米，保存退出至菜单和重载通过，位置差异 0 米。测试后恢复了原飞船和余额。原生读写、热模块双代切换、坏更新回退、错误输入及身份检查另有 C ABI 验证。

已知问题：旧 PID 45800 在从游戏退出到桌面时记录了 `dbghelp.dll / 0xC0000409 / RVA 0x60045`，尚未确认原因；这项退出到桌面的结果未列为通过。保存返回主菜单、编辑和重进已通过。原版空间站交易等未实现业务仍与管理端直接编辑区分记录。
