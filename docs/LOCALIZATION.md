# English and Simplified Chinese

The management app stores `system`, `en-US` or `zh-CN` in its AutoConfig
`SystemProfile.Language` preference. Use Preferences → Display language. Page
and navigation reconstruction retains authentication and navigation history;
save other page inputs before switching. It does not restart or control the game.

Console tools and the server accept `--language en-US|zh-CN|system`, and launch
scripts accept `-Language`. An explicit command-line value overrides
`XFE_LANGUAGE`; otherwise the desktop uses its saved preference. `system` selects
Chinese for a Chinese system UI culture and English for other cultures.

The 480 shared resource entries are embedded in
`Client/XFEEliteDangerous.Client/Localization/{en-US,zh-CN}.json`. Resource keys
are stable; new messages should use descriptive keys rather than renumbering
existing entries. Keep formatting placeholders identical in both languages.
The resource loader works without reflection-based JSON deserialization.

`AppText` controls display text without changing numeric `CurrentCulture`.
Management and HTTPS clients set `Accept-Language` on each request. The server
uses an async-local language scope, honors quality weights and restores the
previous language after each request. Authentication, operation names, IDs,
categories, JSON schemas, native packets and user-authored values are not
translated. Save conflicts include the invariant `save_conflict` error code;
the management client also recognizes older translated conflict messages.

Original game languages and third-party diagnostic output are outside this
setting. Internal technical logs may remain in their original language.

## Validation

The localization regression on 2026-10-22 passed 766 unit tests (454 client and
312 core), 188 English and 161 Chinese hidden WinUI checks, and 12 real
HTTP/HTTPS language checks. The HTTP suite includes certificate pinning,
language-dependent authentication errors, stable identities and numeric edits,
save-conflict exception mapping and 32 concurrent mixed-language requests.

```powershell
./scripts/Test.ps1
./scripts/Test-Localization.ps1
# Test an exact self-contained server publication:
./scripts/Test-Localization.ps1 -ServerDirectory <published-server-directory>
# Construct hidden controls; never activates a window or sends desktop input:
<published-admin-exe> --language en-US --verify-desktop=<absolute-report-path>
<published-admin-exe> --language zh-CN --verify-desktop=<absolute-report-path>
```

Generated reports, fixture credentials and certificates stay in ignored output
directories. These tests do not repeat the earlier native multiplayer gameplay
acceptance. `scripts/Deploy-ManagementUpdate.ps1` publishes management components,
tests the exact published server and hidden UI, retains the existing native
bridge byte-for-byte, then updates an existing deployment. It requires private
games to be stopped before promotion and preserves workspace and game files.
