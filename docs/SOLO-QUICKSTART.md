# Play SOLO from a fresh installation / 一键开始 SOLO

## English

Download the **SOLO Windows x64** package from [GitHub Releases](https://github.com/XFEstudio/OpenEliteDangerous/releases). Extract the whole ZIP into a writable local folder and run **Play-SOLO.cmd**. No server setup, login, imported save, reference bundle, Powerplay extraction, PowerShell, .NET SDK or Visual Studio is required for this entry point.

You need Windows x64 and your own compatible **Elite Dangerous: Odyssey 2026.09.03.332841** installation. This adapter still uses the pinned executable:

`E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe`

Its SHA-256 is `e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988`. Other builds or paths remain outside the current adapter. First launch can take longer while the game builds its shader cache.

On first launch the starter creates a private commander, a Sidewinder and an empty-space practice spawn in Sol. The world and catalogue are generated locally. No online service data is fetched. The native game still makes the normal login, save and location requests through its local adapter; the launcher uses the game's enabled Continue and SOLO actions. You can save and exit normally. Later launches reopen the same save at `%LOCALAPPDATA%\XFEEliteDangerous\SoloStarter`. This directory is separate from the management server's workspace.

This is a limited flight sandbox. The Powerplay seed supplies the structure required by the native reader and grants no pledge, reward or effect. Empty private recipe, permit and construction collections do not implement those services. A few common game definitions come from the project's existing bundled interoperability catalogue. No Frontier binary, game asset package, personal save or private capture is included.

If launch fails, keep the error text and use the per-run logs printed by the launcher. Do not share your entire workspace. `--prepare-solo --workspace <new-local-directory>` prepares the data without starting a game; `--play-solo --workspace <path>` can select a separate starter workspace. An existing directory without this launcher's starter marker is left intact.

The [combined management/server package](RELEASES.md) remains available separately. This standalone package does not add multiplayer, full galaxy/mission simulation or support for other game builds.

## 简体中文

从 [GitHub Releases](https://github.com/XFEstudio/OpenEliteDangerous/releases) 下载 **SOLO Windows x64** 包，完整解压到可写的本机目录，双击 **Play-SOLO.cmd**。无需启动服务器、登录、导入存档、找参考数据、提取势力目录或安装 PowerShell、.NET SDK、Visual Studio。

只需 Windows x64 和自备兼容的 **Odyssey 2026.09.03.332841** 游戏本体。当前适配器仍校验上方固定安装路径和哈希；其他路径和版本尚未适配。首次生成着色器缓存可能需要稍等。

启动器会自动创建私有指挥官、Sidewinder 和 Sol 的空域练习出生点，并生成本地参考数据。它通过原版菜单的可用按钮进入 SOLO。正常保存退出后，再次运行会继续读取 `%LOCALAPPDATA%\XFEEliteDangerous\SoloStarter` 中的进度；管理端和原有服务器存档不受影响。

这是基础飞行练习环境。随包生成的势力条目仅供原版读取器初始化，不提供效忠、奖励或势力活动。空配方、许可证、殖民建设目录表示本地没有对应内容。发布包不含官方游戏程序、资源、个人存档或抓包。

如启动失败，请保留启动器的报错与对应日志；不要公开整个工作区。需要另建练习存档时，可用 `--workspace` 指定一个新本机目录。
