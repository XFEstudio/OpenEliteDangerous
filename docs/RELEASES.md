# Windows release packages / Windows 发布包

## English

Download the `win-x64.zip` asset from [GitHub Releases](https://github.com/XFEstudio/OpenEliteDangerous/releases) and verify its SHA-256 against `SHA256SUMS.txt`. Extract the entire archive into a writable local directory. Keep all subdirectories together. `win-x64-symbols.zip` is optional diagnostic symbols; extract it over the main package when detailed debugging is needed.

Windows x64 and **PowerShell 7** (`pwsh`) are required. .NET and Windows App SDK runtime files are included; compiling source, the .NET SDK and Visual Studio are not required to use the binary package. Unsigned executables may show Windows publisher/reputation prompts.

1. Run `Initialize.cmd`, then `Start-Admin.cmd`. Initialization generates this machine's configuration; the server creates its own local administrator credential and TLS certificate. No shared password is shipped.
2. The default data root is `%LOCALAPPDATA%\XFEEliteDangerous\Server`. Existing saves are preserved. To reuse a separately prepared **private native workspace**, run `Initialize.cmd -SourceWorkspace "C:\your\workspace"` only when the destination workspace does not already exist. Save and stop the source workspace first. Never import another person's account or official-session credentials.
3. An empty installation supports management/account setup. Native play additionally requires your own complete compatible native commander and reference data. Create/import those through the management app or supply your prepared workspace; they are **not included**. Use the management app's Play action, or `Start-Game.cmd -Account <private-account-id>` / `Start-Online.cmd -Account <private-account-id>` after setup.
4. Before upgrading, save and exit the private game and run the old package's `Stop-Server.cmd`. Initialize the new package against the same data root. Keep configuration backups and the old package if rollback is needed. Initialization backs up previous deployment configuration and the native business manifest; it does not overwrite existing saves.

Management listens on `127.0.0.1:48080`; the private HTTPS game endpoint defaults to `0.0.0.0:48081`. `Initialize.cmd -Port 48080 -GameBindAddress 0.0.0.0 -GamePort 48081` sets these explicitly. Existing listener settings are retained unless a corresponding option is supplied. `-DataRoot` supports isolated server instances; desktop local-owner discovery currently expects the default data root, so custom roots need explicit account login/server address configuration.

**Compatibility is deliberately restricted:** the native adapter currently pins Odyssey `2026.09.03.332841` and `E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe`, SHA-256 `e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988`. Changing a UI path or moving a different build there does not create compatibility. No official game executable, game assets, galaxy database, playable personal save, capture, token or private key is distributed.

This is an experimental **educational/research, non-commercial** preview. Read [LICENSE](../LICENSE), [third-party notices](../THIRD-PARTY-NOTICES.md), [native scope](NATIVE-ONLINE-TESTING.md) and [live-refit scope](LIVE-REFIT.md). Prior same-computer tests do not establish full game parity or cross-computer/Internet readiness. Hidden UI checks are not a visual acceptance claim. Packages are unsigned; checksums detect corruption but are not publisher signatures.

Maintainers can package a locally published, native-validated build without including deployment data:

```powershell
./scripts/Pack-Release.ps1 -Version v0.1.0-preview.1 -CompiledRelease <published-release-directory>
```

The script verifies native/server hashes against `reports/live-refit-validation.json`, separates symbols, copies resolved dependency notices and produces a per-file manifest and archive checksums. Its inputs are compiled output folders, not a user's data root.

## 简体中文

从 [GitHub Releases](https://github.com/XFEstudio/OpenEliteDangerous/releases) 下载 `win-x64.zip`，对照 `SHA256SUMS.txt` 核对 SHA-256，完整解压到可写的本机目录。所有子目录须保留。`win-x64-symbols.zip` 为可选调试符号，需要诊断时按相同目录结构覆盖解压。

需要 Windows x64 和 **PowerShell 7**（`pwsh`）。包内包含 .NET 与 Windows App SDK 运行文件，使用二进制包无需安装 .NET SDK 或 Visual Studio。程序未签名，Windows 可能显示发布者或信誉提示。

1. 运行 `Initialize.cmd`，再运行 `Start-Admin.cmd`。初始化生成本机配置，服务器自行生成本机管理凭据与 TLS 证书；包内没有共享密码。
2. 默认数据目录为 `%LOCALAPPDATA%\XFEEliteDangerous\Server`，已有存档会保留。仅当目标工作区尚不存在时，可执行 `Initialize.cmd -SourceWorkspace "C:\你的\工作区"` 导入自己准备的私有原生工作区；先保存并停止源工作区。不要导入他人账号或官方会话凭据。
3. 空白安装可使用管理与账号功能。原生游戏还需要自备兼容且完整的原生指挥官与参考数据，发布包**不提供**这些内容。通过管理端创建/导入，或提供准备好的工作区，再使用管理端的启动按钮；也可运行 `Start-Game.cmd -Account <私有账号ID>` 或 `Start-Online.cmd -Account <私有账号ID>`。
4. 升级前先保存退出私有游戏，并运行旧包的 `Stop-Server.cmd`。在新包中对同一数据目录初始化。初始化会备份原部署配置和业务模块清单，不覆盖已有存档；需要回滚时保留旧包和备份配置。

管理入口监听 `127.0.0.1:48080`，私有 HTTPS 游戏入口默认监听 `0.0.0.0:48081`。可用 `Initialize.cmd -Port 48080 -GameBindAddress 0.0.0.0 -GamePort 48081` 显式指定；已有配置在未传入对应参数时保留。`-DataRoot` 支持隔离的服务器数据目录；管理端的本机所有者自动发现目前使用默认目录，自定义目录需手动配置服务器地址并登录账号。

**兼容范围固定：**Odyssey `2026.09.03.332841`，安装路径与程序哈希见上方英文说明。修改界面路径或将其他版本移动到该目录不会自动兼容。包内不包含官方游戏程序、游戏资源、银河数据库、可玩个人存档、抓包、令牌或私钥。

这是**仅供非商业教学研究**的实验预览。使用前阅读许可、第三方声明和原生功能范围。此前同机测试不代表完整还原官方功能，也不代表跨电脑或公网联机已就绪。隐藏界面测试不等于视觉验收。程序未签名；校验值用于检测文件损坏，不是发布者签名。
