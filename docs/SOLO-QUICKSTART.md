# Play SOLO from a fresh installation / 一键开始 SOLO

## English

Download the **SOLO Windows x64** package from [GitHub Releases](https://github.com/XFEstudio/OpenEliteDangerous/releases). Extract the whole ZIP into a writable local folder and run **Play-SOLO.cmd**. No server setup, login, imported save, reference bundle, Powerplay extraction, PowerShell, .NET SDK or Visual Studio is required for this entry point.

You need Windows x64 and your own compatible **Elite Dangerous: Odyssey 2026.09.03.332841** installation. The launcher checks Steam's main and added libraries. If it cannot find a compatible copy, select `Products\elite-dangerous-odyssey-64\EliteDangerous64.exe` in the file picker. The launcher remembers a verified selection. A local install on any drive is fine; network and redirected paths such as junctions are unsupported.

The executable must match SHA-256 `e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988`. A Steam update may leave you with a different, unsupported build. To specify a copy from the command line, run `Play-SOLO.cmd --game "<installation directory or executable>"`. First launch can take longer while the game builds its shader cache.

On first launch the starter creates a private commander, a Sidewinder and an empty-space practice spawn in Sol. The world and catalogue are generated locally. No online service data is fetched. The native game still makes the normal login, save and location requests through its local adapter; the launcher uses the game's enabled Continue and SOLO actions. You can save and exit normally. Later launches reopen the same save at `%LOCALAPPDATA%\XFEEliteDangerous\SoloStarter`. This directory is separate from the management server's workspace.

To upgrade from preview.3, save and exit the game, back up your existing starter workspace, and extract the new ZIP into a new application folder. Run its **Play-SOLO.cmd** with the same workspace. The default workspace path above is reused automatically. If you previously selected a custom workspace, run `Play-SOLO.cmd --workspace "<existing workspace>"`. The launcher updates its bundled business module while retaining the commander and save. A manually published custom business module retains its own version.

Preview.4 accepts body-relative location addresses, fixing a reproduced RPC 2 rejection near Earth, and recovers its private profile's exact access grant after an interrupted launcher exit. The station approach and docking reported in [issue #1](https://github.com/XFEstudio/OpenEliteDangerous/issues/1) still need a player replay. For that replay, note the station and test entering supercruise, dropping near the station, requesting docking, saving and relaunching in sequence. If a step fails, retain the launcher error and the nearby `rpc.module.failed` and `replication.rejected` log entries, with timestamps.

This is a limited flight sandbox. The Powerplay seed supplies the structure required by the native reader and grants no pledge, reward or effect. Empty private recipe, permit and construction collections do not implement those services. A few common game definitions come from the project's existing bundled interoperability catalogue. No Frontier binary, game asset package, personal save or private capture is included.

If launch fails, keep the error text and use the per-run logs printed by the launcher. Do not share your entire workspace. `--prepare-solo --workspace <new-local-directory>` prepares the data without starting a game; `--play-solo --workspace <path>` can select a separate starter workspace. An existing directory without this launcher's starter marker is left intact.

The [combined management/server package](RELEASES.md) remains available separately. This standalone package does not add multiplayer, full galaxy/mission simulation or support for other game builds.

The management/server tools provide credit balance editing, creating ships from the recorded catalogue, and supported equipment and live refitting operations. The standalone SOLO ZIP currently provides the launcher and local flight/save flow. It has no integrated ship or credit editor. Credit editing sets a finite balance. An automatic unlimited-credit mode and complete exploration, combat, mining and mission gameplay remain outside this preview's verified scope.

## 简体中文

从 [GitHub Releases](https://github.com/XFEstudio/OpenEliteDangerous/releases) 下载 **SOLO Windows x64** 包，完整解压到可写的本机目录，双击 **Play-SOLO.cmd**。无需启动服务器、登录、导入存档、找参考数据、提取势力目录或安装 PowerShell、.NET SDK、Visual Studio。

只需 Windows x64 和自己合法安装的 **Odyssey 2026.09.03.332841** 游戏本体。启动器会扫描 Steam 主库和添加的游戏库。如果找不到兼容副本，在文件选择窗口中选择 `Products\elite-dangerous-odyssey-64\EliteDangerous64.exe`。校验通过后会记住所选路径。任意本机盘符均可；网络路径和目录联接等重定向路径不受支持。

游戏程序须与上方 SHA-256 一致。Steam 更新后可能变成暂不支持的版本。也可用 `Play-SOLO.cmd --game "<安装目录或游戏程序路径>"` 直接指定副本。首次生成着色器缓存可能需要稍等。

启动器会自动创建私有指挥官、Sidewinder 和 Sol 的空域练习出生点，并生成本地参考数据。它通过原版菜单的可用按钮进入 SOLO。正常保存退出后，再次运行会继续读取 `%LOCALAPPDATA%\XFEEliteDangerous\SoloStarter` 中的进度；管理端和原有服务器存档不受影响。

从 preview.3 升级时，先保存退出并备份现有 SOLO 工作区，再把新 ZIP 解压到新的程序目录。新启动器会继续使用上方默认工作区。原先使用自定义工作区时，运行 `Play-SOLO.cmd --workspace "<原工作区>"`。启动器更新随包业务模块并保留指挥官和存档；手动发布的自定义业务模块保留原版本。

Preview.4 修复了地球附近带天体编号的地点请求被 RPC 2 拒绝的问题，并支持接管异常退出后遗留的当前私有配置授权。[Issue #1](https://github.com/XFEstudio/OpenEliteDangerous/issues/1) 中的具体空间站进站与停靠仍待玩家复测。复测可按进入超巡、空间站附近退出超巡、请求停靠、保存和重启的顺序进行，并记录空间站名称。遇到失败时保留报错及附近带时间戳的 `rpc.module.failed`、`replication.rejected` 日志。

这是基础飞行练习环境。随包生成的势力条目仅供原版读取器初始化，不提供效忠、奖励或势力活动。空配方、许可证、殖民建设目录表示本地没有对应内容。发布包不含官方游戏程序、资源、个人存档或抓包。

管理端与服务器工具已有信用点余额编辑、从已记录目录创建飞船，以及支持范围内的配装和实时改装功能。独立 SOLO ZIP 当前提供启动器、本地飞行和存档流程，尚未集成改船与信用点编辑界面。余额编辑设置的是有限数值，自动无限信用点模式及完整探索、战斗、采矿和任务仍超出此预览版的已验证范围。

如启动失败，请保留启动器的报错与对应日志；不要公开整个工作区。需要另建练习存档时，可用 `--workspace` 指定一个新本机目录。
