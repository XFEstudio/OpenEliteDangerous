# OpenEliteDangerous

[English](README.md) | [简体中文](README.zh-CN.md)

An experimental, unofficial interoperability project for **Elite Dangerous: Odyssey**, built with C#, WinUI and XFE ServerInteractive. It provides a management app, an offline adapter and a shared server for commanders, saves and world definitions.

> **Educational and research use only. Non-commercial use only.** This project is not affiliated with, endorsed by, or supported by Frontier Developments. Elite Dangerous, its trademarks and game content belong to their respective owners. A separately and lawfully obtained compatible game installation is required. No official game executable, asset package, account entitlement or official service access is provided.
>
> Project-owned source uses the [Educational and Research Non-Commercial License](LICENSE): a **source-available license, not an OSI-approved open-source license**. Third-party material retains its own terms; see [third-party notices](THIRD-PARTY-NOTICES.md). Software is supplied **as is**, without warranty. These notices do not eliminate legal, contractual or commercial risks.

## Features

- Local accounts, administrator/player roles and separate permissions for credits, cargo, materials and ship editing.
- Inventory search, quantity/credit editing and partial imports from local Player Journals.
- Custom ships from recorded hull/outfitting templates, active ship selection, compatible modules and module storage.
- A categorized outfitting workbench with slot cards, module properties, engineering presets, experimental effects and ammunition editing. Recorded module recipes load on demand; supported edits, storage and retrieval apply inside the running private game and persist after native readback. See [live refitting](docs/LIVE-REFIT.md) for scope and tests.
- Versioned native save import/export, backups, cross-process locking and atomic updates. Other inventory/fleet operations require saving and returning to the game menu before editing.
- One ServerInteractive process hosts the loopback management API and HTTPS game endpoint, owning online accounts, saves, factions, commodities and shared world definitions.
- Certificate-pinned HTTPS between launcher and server; local IPC between the native game and adapter.
- English and Simplified Chinese screens, launch messages and application errors. Choose **Preferences → Display language**; the choice is remembered and your session is retained.

## Current scope

The adapter targets **Windows x64 Odyssey `2026.09.03.332841`**. Native compatibility checks currently pin the executable and authorized installation at `E:\Elite Dangerous\Products\elite-dangerous-odyssey-64`. Changing the UI directory does not retarget native hooks. Other builds or installations require an adapter update and renewed validation.

Prior native validation covered SOLO flight/save/reload and **two clients on the same computer**: mutual ship visibility, bidirectional movement/orientation, central saving and same-process re-entry. The two-client run passed 81 checks, with a final position error below 0.069 m. This does not establish full official feature parity. Cross-computer/LAN/Internet play, reconnect recovery, combat, NPCs, jumps and station transaction coverage remain unverified or incomplete. See [native online testing](docs/NATIVE-ONLINE-TESTING.md) and [SOLO notes](docs/OFFLINE-SOLO.md).

Private captures, personal saves, reports and deployment outputs are excluded. Native acceptance tests require your own compatible installation and locally prepared data. Cloning the repository does not supply a playable commander or the official galaxy database.

## Build and test

Prebuilt Windows packages and binary-only setup instructions are available in [GitHub Releases](https://github.com/XFEstudio/OpenEliteDangerous/releases) and the [release guide](docs/RELEASES.md). They include project tools and runtimes; compatible game files, private saves and native reference data must be supplied separately.

Use Windows x64, the .NET 10 SDK specified in `global.json`, PowerShell 7 and Visual Studio C++/Windows SDK tools for NativeAOT publishing. WinUI/XFE dependencies restore through NuGet.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
dotnet run --project Client/XFEEliteDangerous.Tool -- --help --language en-US
dotnet run --project Client/XFEEliteDangerous.Bootstrapper -- --status --language zh-CN
```

Languages are `system`, `en-US` and `zh-CN`. Console apps accept `--language`, launch scripts accept `-Language`, and `XFE_LANGUAGE` sets an environment override. Clients send `Accept-Language` for server errors. Protocol fields, account/ship IDs, numeric wire values and user data remain unchanged. This controls project tools; configure the original game's language separately.

Regression tests cover native formats/loaders, save conflicts, protocols and language-resource parity. The hidden desktop verifier constructs controls and checks language switching without activating a window or sending desktop input. Run isolated HTTP/HTTPS language checks with:

```powershell
./scripts/Test-Localization.ps1
```

## Local deployment

Prepare your own compatible native workspace, then publish:

```powershell
./scripts/Deploy-Local.ps1 -SourceWorkspace <your-native-workspace> -EnableOnline
./Start-Admin.cmd -Language en-US
```

Deployment preserves an existing workspace and generates `runtime/deployment.json`. Default storage is `%LOCALAPPDATA%\XFEEliteDangerous\Server`, with saves in `Workspace`, backups in `inventory-backups`, and logs in `logs`. Keep credentials, private certificates and sessions private.

| Entry | Purpose |
| --- | --- |
| `Start-Admin.cmd` | Start the local server and management app |
| `Start-Server.cmd` / `Stop-Server.cmd` | Start / gracefully stop the server |
| `Start-Game.cmd` | Launch the default local commander |
| `Start-Online.cmd` | Launch the default commander through the configured server |

Management defaults to `http://127.0.0.1:48080/api/`; HTTPS game traffic uses port `48081`. Use **Server multiplayer** in the management app for a supplied address, certificate fingerprint and server account. Keep management on loopback. Network reachability across computers needs separate testing. See [local deployment](docs/LOCAL-DEPLOYMENT.md) and [online protocol](docs/ONLINE-PROTOCOL.md) (Chinese).

## Layout

| Path | Purpose |
| --- | --- |
| `Client/XFEEliteDangerous.Desktop`, `Desktop/SharedUI` | WinUI management app and role-based pages |
| `Server/XFEEliteDangerous.Server` | ServerInteractive console server |
| `Client/XFEEliteDangerous.ManagementClient` | Loopback management client |
| `Client/XFEEliteDangerous.OnlineClient` | HTTPS client and native IPC host |
| `Local/XFEEliteDangerous.Core` | Accounts, permissions, inventory, saves and shared world |
| `Client/XFEEliteDangerous.Client/Localization` | English and Simplified Chinese resources |
| `Client/XFEEliteDangerous.Tool` | Local command-line tool |
| `Client/XFEEliteDangerous.Bootstrapper` | Compatibility checks and native launcher |
| `Client/XFEEliteDangerous.OfflineRuntime`, `Local/XFEEliteDangerous.ReadBusiness` | NativeAOT runtime and reloadable business modules |
| `Client/XFEEliteDangerous.NativeFormat`, `Local/data` | Native formats and interoperability metadata |
| `scripts` | Build, deployment, diagnostics and automated checks |

Per-run JSONL diagnostics include correlation IDs, native function locations and managed source lines. Secrets are redacted. Use `scripts/Read-OfflineLog.ps1 -RunDirectory <run-directory>`. Business modules can be replaced through `scripts/Publish-ReadBusiness.ps1`; changes to fixed native hooks still require restarting the game.

## Responsible publication

Use only copies, data and environments you are authorized to use. No rights to game assets, official services, paid features or another person's account are granted. Do not sell the project, run paid services using project-owned code, or present it as official. Retain license and third-party notices. Do not commit credentials, tokens, private captures, personal Journals, game binaries or asset packages, or post them in public issues.

See the [Open Source Definition](https://opensource.org/osd) for the distinction between public source and OSI open source. A disclaimer is not legal clearance for reverse engineering, redistribution or commercial deployment.
