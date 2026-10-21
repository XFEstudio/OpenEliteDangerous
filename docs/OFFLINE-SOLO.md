# 原生 SOLO 的离线接入边界

游戏使用 C# 进程内数据回调、本地会话逻辑和存档库，游戏通道不创建 HTTP/UDP 端口，也不回退到官方服务。Odyssey 副本在无网络能力的 AppContainer 内已通过离线登录、原生 SOLO 世界载入、程序化飞行及保存退出重载。**用户于 2026-10-21 确认此前交付的 SOLO 验收完成。** 本轮按用户要求增加了独立的 ServerInteractive 本机管理服务器和 WinUI 管理端，使用方式见 [本地部署](LOCAL-DEPLOYMENT.md)。站内业务未全部实现，`NativeSoloReady` 与 `gameIntegrationReady` 仍保留整体功能门禁；下文旧目录中的断线和等待验收状态属于历史排查证据。

本轮管理数据验证进程为 `artifacts/offline-game-ca44be7fe3874d259edf1dd45e1deedf`，PID 11416。Journal 实际记录了管理 API 修改后的飞船 ID 2、信用点 1、Platinum 1 和 iron 1；`native-gameplay-test-e211a4573751.json` 验证约 160.7 米飞行、保存返回菜单和重载，原生完整 reader 返回 0，位置差异为 0 米。测试后恢复原档案，再次进入稳定 InGame。验收对应的旧进程为 `offline-game-124cec21a0454ec6b272dc86f3d35a86`；其 `user-acceptance.json` 单独保存用户确认。状态脚本只在对应验收记录与程序检查都存在时设置最终已验证字段，不把单独 InGame 当作用户验收。

旧 PID 45800 在退出到桌面的最后阶段出现 `dbghelp.dll / 0xC0000409 / RVA 0x60045`，原因尚未确认。这次退出到桌面没有计为通过；保存返回主菜单及重载已经通过。

重进断线来自 RPC 9：发送入口 `0x986150`、方法表 `0x5130858`、reader `0x982970`、无回复回调 `0x9861B0` 确认单个 U32。拒绝它曾回滚同组的 17/2 回复，使 PointOfFocus 卡在 AwaitingReadyToGoLive 后超时。热模块现确认合法的单向收据；错误长度、标志、对齐方式和会话角色仍被拒绝。正常保存退出必须使用原始 ExitOptions 的 `Confirmed Quit To Main Menu` 动作，直接选择内部 SaveAndExit 状态不包含完整前置流程。JSON 存档上传完成按抓包返回 XML `<data />`。

原生 Odyssey 谓词 `0x2609980` 真实返回 1，观察器不改返回值。私有 user XML 使用捕获的 data/capabilities/item 结构，包括 SPACE_LEGS、LANDING_ATMOS_TENUOUS 等模式能力，同时保留空 sku；这不表示地面玩法已完成。

程序化飞行在原始输入读取 `0x2E47E40` 核对物理组件 `0x5337830` 和 IShipControl `0x5311220` 后，短时替换前向轴，每次原调用返回即恢复输入。原生物理 `0x2E6BF40` 观测到速度增加和输入归零后减速，最终位置由真实存档提交验证。测试不写位置、速度、就绪或菜单资格。之前 `0x25E3790` 的油门覆盖值没有被实际驾驶路径消费，其零位移测试保留为失败证据。

`/survey/info` 先前虽补齐当前星系行，bodies 仍为空。隔离进程自己的 Location Journal 确认 Sol/BodyID0 为 Star；最新热模块为本地 Sol 起点提供这颗主星的基本知识（bitmask 1），不复制在线玩家发现或测绘身份，其他星系仍保留明确空记录。原生 reader 与实际地图显示分别验收，完整行星知识和地图视觉尚未通过。

地图依赖的殖民地查询也已补齐：`colonisation/starsystem/info` 使用小写 `systemaddress`，无本地项目时按在线响应返回 204 空正文；`constructioneffortmaterials` 使用 `systemAddress`，返回 `{"construction_efforts":[]}`。非法地址返回 400，未记录星系返回 404。原生客户端已实际接收前者的 204；后者已通过原生 C ABI 契约检查，尚待该进程再次发起请求。两项都不代表建设玩法已实现。

热模块 `r20261021-014254-3b482073` 还提供好友、屏蔽和社区活动列表的本地空集合，原生重载报告 `native-gameplay-test-39f4dee8a188.json` 已通过，三项读取均实际返回 200。这三项属于 **Stub**：只表达本地世界没有关系记录或活动，不提供添加好友、屏蔽、参加活动或联网社交。模块合计 12 个 GET 路径；不支持的写入仍拒绝。其身份校验、写入拒绝、双代切换与损坏发布回退均有独立 C ABI 检查。

Windows 事件 1000/1001 将 01:00 的异常退出定位到 frida-agent.dll、0xC0000409。诊断助手随后加入独占锁，日志回调在卸载和分离后才关闭，并将脚本异常判为失败。后续业务修改及上述测试均复用 PID 45800。

离线测试只使用 `E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe`，SHA-256 为 `e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988`。启动器检查固定路径、文件哈希和无网络能力的进程令牌，测试结束仅关闭它自己创建的子进程。`--native-check` 记录原生状态，并在符合原生按钮可用条件时选择真实的 Continue/SOLO 菜单；没有强制就绪标记或用训练场替代 SOLO。

近期修正了原生 libcurl 响应头回调选项：应使用 `20079`，此前的 `20056` 是进度回调。NativeAOT 自检包含真实签名的进度回调，验证其不会被当作响应头或正文调用。修正后的隔离状态证据为 `artifacts/offline-game-5007a8d95c5b49ac823abd39bf542c89/logs/native-game-flow.tsv`。旧的 `FailedToConnect` 和旧 HTTP/UDP 回复测试仍保留为历史记录，不能证明原生世界已完成。

进程内通道接入后的隔离证据为 `artifacts/offline-game-2fe5851ea6ef454c94aead5da8bcc855` 和 `artifacts/offline-game-d6eb355aac83487ab982ed282e5d0926`。本地 server/list 提供每次启动新建的主、任务会话身份和密钥；数据包经 `OfflineWorldAuthority` 处理，响应在自然更新线程交给原生接收队列。没有操作系统端口、外部转发或同步业务回调。原生菜单日志记录了 SOLO 可用标志从 `10` 变为 `11`，随后选择按钮。两条管理会话均已加入，任务连接的重连也完成了握手。

任务记录的 `78/79` 请求与响应已根据原生 reader 和抓包对齐：使用相关编号、压缩字节向量和 Snappy，返回新建本地角色实际为空的记录集合。角色声望、凭证、工程师进度与交易记录的最小读取，以及日志的耐久去重保存已接入。这些日志收据不等同于角色存档提交。

历史目录 `artifacts/offline-game-2157ce2d97b24161829b3475acea898b` 的观察定位了断开分支：`CheckForDisconnect` 的 `0x1D13836` 检测到连接失败，继而使载入活动的 `0x1D18265` 分支退出。该次完整存档 reader 尚未被调用；后续修复与最新通过记录见本文开头。未更改连接标志或强制推进载入。

需要本地替换的边界：

| 边界 | 已知依据 | 目前状态 |
| --- | --- | --- |
| 账号、角色、库存与存档事务 | `OfflineWorkspace` / `StateStore` | WinUI 经本机管理 API 调用；原生游戏由进程内运行时调用 |
| 原生 HTTP 数据完成回调 | `OfflineCurl` / `OfflineGameData` | 进程内处理；未实现的业务明确失败 |
| 原生登录和主菜单 | 隔离原生状态日志 | 已观察到离线 `LoginSuccess`、`MainMenu` |
| 主、任务会话通道 | `InProcessNativeChannels` / `OfflineWorldAuthority` | 原生握手、共享客户端对象编号池、ServerAdmin 会话加入及重连已验证；世界业务仍不完整 |
| 公共规则、材料、商品、配方和势力定义 | `NativeReferenceLibrary` | 十一类固定版本定义可在请求时重新加载，不包含账号或会话 |
| 完整存档读写和舰船数据 | 原生 reader RVA `0x2404620` / `NativeCommanderSave` | 本地舰队、模块仓库、货舱和工程材料已适配；未记录的原生字段仍需逐项验证 |
| SOLO 启动与世界载入 | `LoadGameActivity` RVA `0x1D82470` | 已到达地点 lifecycle 6、加入本地 Island/Wake，并进入稳定的 InGame |
| 地点准备完成判断 | 原生 RVA `0x1C4B620` | 依赖真实地点和实体初始化；不能仅跳过等待 |
| 游戏内舰船实体 | 原生 RVA `0x24DBB10`、`0x2E47E40`、`0x2E6BF40` 等 | 内部轴输入产生实际飞行与保存位移；此前交付流程已获用户验收 |

后续在这些边界补齐进程内的本地数据和世界逻辑，并使用指定副本及固定版本校验。游戏协议字节只在进程内交给本地逻辑，本机管理 API 单独服务于 WinUI。训练场可以用于分析本地初始化流程，但不能作为原版 SOLO 已通过的交付证据。

启动器支持 `--status`、`--runtime-check`、`--hot-read-check`、`--native-check`、`--debug-solo` 和 `--launch`。可用参数为 `--workspace`、`--account`、`--runtime`；`--native-check` 还可指定 `--seconds`，仅用于有界测试。日常原生调试使用 `--debug-solo`：可见、按原生可用条件选择首次 Continue/SOLO，随后一直保持进程，直到游戏退出或用户取消启动器。必须先准备具有原生存档的本地测试账号和发布后的 NativeAOT DLL。

C# 热更新业务位于 `Local/XFEEliteDangerous.ReadBusiness`：`ReadServices.cs` 处理读取、`RpcServices.cs` 处理已确认的 RPC、`SaveServices.cs` 处理存档候选。修改后运行 `./scripts/Publish-ReadBusiness.ps1 -Workspace <离线工作区>`，脚本先编译并写入新的 DLL/PDB，最后原子发布 `native-business/active.json`。固定宿主按游戏版本、ABI 和 SHA-256 校验，在下一次匹配请求切换；错误更新保留上一代并记录异常。两套 NativeAOT 运行时之间只传 UTF-8 JSON 和具有明确释放函数的原生缓冲区，不传托管对象。旧 DLL 保持映射直到游戏退出。固定宿主保留登录、会话身份、可靠传输以及最终存档事务；存档候选不能改变货币、库存定义或世界身份等受保护字段。底层钩子与宿主 ABI 的变化仍需重启。

`--hot-read-check` 在同一检查进程执行两个原生 C# 模块版本，验证损坏发布回退、角色校验与只读约束，并检查运行编号、世界 authority 对象和保存内容未改变。实际 E 游戏进程也已记录两代模块：`artifacts/offline-game-de059b14302941ff9bed24add890e123`，PID 55212。第一次 Codex 区域响应仍失败，原生 `0x1155F45` 显示它必须在已加载摘要中找到区域；第二代改为 42 个全新、零发现计数的区域条目，同一游戏进程返回菜单并重新选择 SOLO 后重新调用了摘要和区域请求。没有复制在线角色的发现记录。

原生诊断还支持 `--visible true`，在可见窗口内仍只选择原生判断为可用的 Continue/SOLO 按钮。每个启动目录的 `logs/bootstrapper.*.jsonl` 与 `logs/offline-runtime.*.jsonl` 分别保存启动、退出及进程内记录。后者覆盖引擎格式化日志入口 `0x560DE0`（在其自身级别过滤之前观察）、数据请求和原生完成通知、传输与 RPC、存档事务、异常以及载入状态。原生地址按固定 EXE 的 `.pdata`/链式 unwind 信息映射到实际函数范围；无符号函数保留 RVA，不虚构 C++ 源码行号。C# 记录包含编译时调用文件、行号和方法，异常保留嵌套堆栈。日志每 16 MiB 新建一段，保留此前分段。

`Read-OfflineLog.ps1` 可按关联编号读取或合并重复错误。结构化日志不写请求/响应正文，令牌、密钥及长编码串脱敏；原生消息超过 4096 字节会标记截断，读取失败和重入数量也会记录。进程退出码由启动器记录；这不等同于已获取完整原生崩溃转储。

`NativeLocationProbe` 在原生地点检查 `0x1C4B620` 读取容器和地点的 lifecycle、对象类型及 activation-delay 字符串，只在依赖变化时记录。它不调用原生 getter、不更改对象状态。历史目录 `artifacts/offline-game-8e89ece3486640928fd8d0854c312610/logs/offline-runtime.*.jsonl` 曾记录容器到 6、地点停在 5，以及 Powerplay、Polities、ConflictZoneManager 和 `IGalacticPolitics` 依赖。最新运行地点已到 6。日志已分段，排查时必须读取全部分段，不能只检查 `0000`。

Powerplay 两类摘要的原生 JSON reader `0x1417BC0` / `0x1417230` 接受空的 `systems` 数组，但这不等于分页结束。分页执行器 `0x3464C40` 在后续请求收到 HTTP 204 才结束；持续回复空 200 会一直创建新页。只读业务模块现提供空的初始 200 页和后续 204，保留独立的非空全局势力定义。生成号 `r20261020-225828-ab5cc7b5` 已由上述同一游戏进程加载，两类请求均记录 204；独立 `--hot-read-check` 验证了初始页、结束页、非法页码与会话/存档不变。这些证据尚不代表地点、驾驶或保存重载验收通过。

视觉验收由用户完成。`Test-NativeSolo.ps1` 通过有期限的命令文件，在自然更新线程选择真实、可用的 Continue/SOLO 按钮，并通过原生错误确认回调处理失败。它不发送操作系统输入、不读取屏幕、不覆盖就绪状态。测试报告分别记录原生完整存档解析、地点准备、实际 save.commit 和稳定 InGame；不会把这些检查冒充飞行或保存重载通过。`scripts/capture/inspect_offline_controls.py` 在严格核对启动记录、E 副本哈希与离线运行时后，可进行有界的原生油门、暂停和保存退出检查。其记录与生产 C# 运行时分开保存。

原生 RPC 2/3 已接入新建本地 Island/Wake，会话身份由固定宿主生成。17/18 使用原生空实例定义集合，89/90 在 primary 与 missions 会话分别返回本地空记录映射。RPC 32 是单向退出通知，可提交零业务响应并由原有可靠层确认，不得伪造回复或直接更改断开标志。

实际存档上传暴露了两个问题：电容器的能量是按百万倍率编码的绝对量，不能套用健康值的 0～1000000 范围；Windows 原子 File.Replace 合并 DACL 还需要离线工作区的 WRITE_DAC 权限。二者已修正。存档错误保留具体字段、数值、C# 堆栈及原子替换的源/目标路径。`/survey/info` 当前提供本地 Sol 主星的初始记录，`/fleetcarrier/systemmap` 返回当前系统的空本地航母目的地集合；地图实际显示仍由用户验收。

首次完整引擎记录 `artifacts/offline-game-f23df821b5d049328f0a93603d83fc47` 定位了载入时的时钟错误：`0x7F337D` 报告约 143 万秒的帧间隔，随后 `0x4C3F236` 因超时断开，`0x1D7AC02` 报告 `IDProvider` 断开，最终显示 Mauve Adder。正常抓包中 272 个时钟响应均在第一个位置回显请求时间；其秒值相对 Unix 时间的偏移为 FILETIME 的 11644473600 秒。时钟 RPC 的本地响应已改为 UTC FILETIME 秒/纳秒；重传超时继续使用单调计时。修复后原生完整存档 reader 已返回 0，管理会话不再因巨大时间跳变断开。之后日志定位 `RootDataValidation_Run` 的许可证定义格式错误，已将数组改为 ID 索引的定义映射，未授予角色任何许可证；载入现已推进到 `EnteringLocation` / `ActivateLocation`。

2026-10-20 用户授权从 D 盘 Steam 副本采集正常联网行为，随后明确要求停止。采集没有加载离线运行时，没有修改请求、响应、账号业务或系统网络设置；采集助手均已退出。正常联网证据见 `docs/protocol-evidence/capture-20261020.md`，只能用来还原数据与协议。

用户已确认此前交付流程验收完成。仍未实现的范围包含完整 NPC/旅行世界逻辑和原版空间站服务、装配、商品交易事务；本轮管理端可直接编辑舰队、装备和库存，二者分别记录。程序化世界、飞行、保存返回菜单和重载证据见本文开头，整体功能门禁仍保持 false。

`scripts/Get-NativeSoloStatus.ps1 -RunDirectory <启动目录> -OutputPath <报告文件>` 在不附加进程和不操作窗口的情况下，读取全部日志分段、最新流程、热模块、各接口最近一次完成状态及原生游戏测试报告。文件以共享读方式打开，可在游戏持续写入时使用。输出明确区分历史失败、最近一次观测结果和仍待视觉验收；不会根据 InGame 字符串自动宣告完整可玩。

本机测试启动入口为忽略目录中的 `artifacts/offline-conversion-20261020/Start-OfflineSolo-Test.cmd`，固定使用已验证运行时和独立本地测试账号，按原生可用条件自动进入 SOLO。已有 E 盘进程时会拒绝重复启动。通用 `Start-PrivateGame.cmd` 现可转交 `--workspace`、`--account`、`--runtime` 参数，不会自动将测试档案导入正式工作区。
