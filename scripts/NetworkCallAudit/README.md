# 游戏网络调用清单

```powershell
dotnet run --project scripts/NetworkCallAudit -c Release -- --self-test
dotnet run --project scripts/NetworkCallAudit -c Release -- --scan-game
dotnet run --project scripts/NetworkCallAudit -c Release -- --solo-static
```

只读取 `E:\Elite Dangerous` 的主产品及启动器目录中 EXE/DLL。解析 PE 导入表、系统导出序号、x64 异常表函数范围、指令、.NET IL 和 ASCII/UTF-16 字符串；不加载游戏模块、不操控游戏、不发送请求。

输出到全新 `artifacts/network-call-audit-*/`：

- `README.md`：主程序统计，区分实际收发 API、套接字管理、纯本地辅助和适配器查询。
- `inventory.json`：文件哈希、逐调用点、直接调用关系、API 路径引用、静态端点及覆盖限制。
- `call-sites.csv`：便于筛选的网络相关 API 调用点。
- `api-paths.csv`：游戏请求路径、字符串 RVA、引用位置及所属异常表函数范围。

静态调用点不是实际请求数，也不能证明一个联网功能必需。寄存器间接调用、动态解析、外部模块和未纳入异常表的叶函数仍有覆盖缺口；x86 模块只统计导入和字符串，不宣称完成反汇编。纯地址转换、错误查询以及读取适配器信息均单列，不能算成发起网络访问。

`--solo-static` 只读取固定哈希的主程序以及仓库中的 API 清单、响应适配登记，输出全新 `artifacts/solo-static-dependency-audit-*/`：

- `activity-classes.json`：原生名称 getter、对应虚表、方法和存储该虚表的函数。存储函数也可能是析构函数，不能全部称为构造函数。
- `call-graph.csv`：区分直接 CALL/JUMP 与潜在的只读回调表边。
- 每阶段 JSON：菜单、警告、指挥官读取、完整载入、LoadGame、UDP、舰船应用、保存退出、位置活动的固定入口和未解析间接调用。
- `api-phase-matrix.json`：全部 406 条路径、方法、原生引用 RVA 和阶段候选。
- `minimum-api-matrix.csv`、`minimum-solo-plan.json`：首个飞行/保存里程碑的候选核心接口、可裁剪外围功能、已核清的可选活动分支及缺口。
- `decompile-targets.txt`：供 `SoloProtocolDecompile.java` 批量离线反编译的固定 RVA 清单。

直接图在服务虚调用处会断开，潜在回调表又会经过共享工厂而大幅扩大。两者都不能独立证明某条 API 是 SOLO 必需。固定遍历深度为 12，未解析调用和截断均写入报告；不把未知依赖记为“无需网络”。具体人工核对结果见 [SOLO 最小流程](../../docs/SOLO-MINIMUM.md)。此命令不启动游戏、不读取运行进程、不发送网络请求。
