# WinUI management client

`Client/XFEEliteDangerous.Desktop` follows the local XFEExtension VSIX WinUI template whose default project name is `XFEWinUIApplication`. It uses WinUI 3, WinUIHelper PageManager, native navigation/message/loading services and AutoConfig preferences. Publishing includes the PRI and compiled XBF files.

The shared desktop code follows the local XFE WinUI application template architecture:

- `AppShellPage`: native WinUI `TitleBar`, navigation through WinUIHelper, MVVM state and native loading/message services.
- `NavigationHostPage.xaml`: compiled navigation metadata and an activation boundary for the programmatic feature pages.
- `DesktopState`: the authenticated account role comes from login and profile responses. Anonymous users see a placeholder account card and login action. Ordinary players see allowed features; administrators additionally see account management, Journal import, server configuration and permission management.
- Login, registration and edits use separate dialogs. Public registration does not submit a role. Administrators can choose a role when creating an account, or edit another selected account's role with confirmation.
- Navigation is rebuilt on identity changes. The previous role-specific page is detached and navigation history is cleared, so logout or role downgrade does not retain administrator controls.
- `SystemProfile`: preferences under `%LOCALAPPDATA%/XFEEliteDangerous/Desktop/Profiles`. Server URLs, directories, theme, display language and optionally the account email persist. Passwords, session bearers and trusted roles never persist.
- English and Simplified Chinese use embedded resources shared with console tools and API clients. Language changes rebuild navigation and the current page while retaining account identity, session and history. Save other inputs before switching. API calls send `Accept-Language`; request scopes never change numeric wire culture. Stable error codes preserve save-conflict handling across languages.
- `LocalClient` adapts the pages to `XFEEliteDangerous.ManagementClient.ManagementApi`; the desktop never opens the server state file. Explicit HTTP loopback addresses are accepted, proxies and redirects disabled, and response size bounded. Session invalidation is dispatched to the UI; stale responses cannot invalidate a newer session.
- On this machine the app exchanges the Windows-user-restricted `Server/admin-access.json` key for a short-lived local owner session. The key is not a default account password and is never saved in desktop preferences or command-line arguments. Ordinary accounts use registration/login and server-enforced permissions.
- Settings use community `SettingsCard` / `SettingsExpander`; inventory, credit and module edits use dialogs. Original material categories and string commander versions are preserved.
- Administrator-only Journal import reads and serializes on the thread pool, previews the partial snapshot and warnings, then confirms replacing the selected private account copy. The server backs up the previous copy. No game file or official account is modified.
- The fleet workshop creates ships from existing templates, selects the active ship, edits management labels, fits compatible recorded modules and stores/retrieves modules. New private commanders can initialize an independent Sidewinder. Native save import/export retains identity and revision checks. The inventory page edits credits, cargo, engineering materials and module settings.
- Native edits require the running game to have saved back to the main menu and take effect on the next SOLO load. The workshop launches the selected commander through the deployed bootstrapper without sending keyboard or mouse input. Usage and service deployment are documented in `docs/LOCAL-DEPLOYMENT.md`.

The project references the local XFE WinUI template dependencies and copies the application PRI plus all generated XBF files on publish, preserving `Desktop/NavigationHostPage.xbf`.

Build without launching the client:

```powershell
dotnet build Client/XFEEliteDangerous.Desktop/XFEEliteDangerous.Desktop.csproj -c Release -p:Platform=x64
```

The opt-in `--verify-desktop=absolute-report-path.json` entry constructs native controls without activating a window, sending input, making network requests or starting a game. Its role scenes cover anonymous, ordinary player and administrator states, navigation rebuilding, settings controls, administrator-only actions, permission visibility and wire projections. It also checks session invalidation, stale-session protection and rejection of nonlocal API URLs.

The localization regression reports `desktop-en-US.json` and `desktop-zh-CN.json` contain 188 and 161 passing checks respectively. Actual HTTP, native game load and user acceptance have separate reports; hidden construction does not validate visible layout or live typing. All generated reports remain outside Git.
