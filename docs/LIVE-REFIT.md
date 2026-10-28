# Live ship refitting

The WinUI fleet workbench follows the category / slot-card / inspector arrangement of XFEEDToolBox. It edits this project's private commander and running game rather than calculating a separate outfitting plan. Game artwork and 3D models from the reference project are not bundled.

## Using the workbench

Start the private game through the project launcher, enter the world, then select its commander and active ship in **Fleet**. Choose a category and slot. **Apply module properties**, **Fit module**, **Store module** and **Retrieve module** wait for a response from the running native game. Success means the native result was read back and the server save was committed. These operations do not restart the process or navigate its menus.

The inspector provides module enable state, the game's displayed power-priority scale of 1–5, and integrity. Modules without an independent damage component show read-only integrity. Modules outside the power-priority system do not offer a priority editor; their serialized defaults are preserved separately from runtime placeholders. Compatible recorded module recipes are loaded through the original game resource library on demand, including recipes not previously fitted in the current session. The displayed cargo capacity comes from the live native cargo component.

The engineering tab selects a recorded compatible blueprint and experimental effect, and allows its relative parameters to be edited as percentages. Applying or removing engineering rebuilds the module with the original native modifier pipeline. Both the modifier vector and independent physical values (such as maximum integrity and magazine capacity) are read from the game. Magazine and reserve ammunition use their live native setters and current-count getters; capacities are displayed separately and bound the editors. Engineering and ammunition remain attached to stored modules and are restored when retrieved.

The public capture-derived catalogue contains 263 module identities, 50 blueprint/grade/roll entries and 85 experimental definitions. Presets retain their recorded quality rather than inventing missing roll curves. Compatibility filters and the original native installation checks still apply. Unknown catalogue identities, unverified nondefault boost/class/damage-override state and incompatible assets are rejected. Creating/selecting ships, importing full saves and other inventory operations retain their existing main-menu guard. This adapter does not attach to a separately running Steam installation or an official-service session.

## Transaction and diagnostics

The management API authenticates the actor, checks ship-edit permission and save version, and binds an expiring command to the account, runtime, native ship entity and slot identity. The adapter invokes pinned original functions after the original game update. It waits for live module creation/removal and subsequent native readback before acknowledging.

The server merges only the requested changes into the latest save, retaining concurrent location, fuel and other commander state. Warehouse conflicts fail instead of replacing another transaction. Storage retains the native counts observed immediately before removal. If a native operation fails, recovery runs through subsequent native updates and is confirmed before reporting a restored rejection. If save commit fails after native application, a compensating native command restores the original module and reconciles the save. An unconfirmed recovery is reported explicitly.

The GET save document preserves `recipeName`, `recipeLevel`, `craftCount`, the nested native `modifiers` envelope, and ammunition. The replaceable save/read business module must be built with this model too. `Deploy-ManagementUpdate.ps1 -ReadBusinessModule ...` checks its hash against the real-game validation, publishes an immutable generation and restores the previous manifest if server promotion fails.

Diagnostics include `native.refit` and `refit` events with source files and lines. Per-account command history is retained under `workspace/live-refit/<account>/history/`; it includes the before/after module state for recovery. These files, saves and per-run captures remain private and ignored by Git.

## Verification

`scripts/Test-LiveRefit.ps1` requires an owned pinned E-copy launch receipt, an isolated artifact workspace and a matching test server. It verifies property changes, module storage/retrieval, native acknowledgement and server persistence without desktop input. `-ModuleReplacement` additionally swaps a shield for a cargo rack and back, checking the live cargo capacity change. `-VerifyReload` checks the saved enable state, priority and integrity after an original native save/re-entry operation in the same process.

`scripts/Test-AdvancedLiveRefit.ps1` additionally checks an initially unseen multicannon recipe, live ammunition, capacity rejection/recovery, engineering and experimental effects, independent physical capacity/integrity changes, warehouse restoration and removal of engineering. `-VerifyReload -Expectations ...` checks the same fields after an original native save/re-entry operation. `inspect_offline_controls.py --refit-modules` also records independent read-only native module data during re-entry. The same suite runs through offline and certificate-pinned HTTPS game sessions.

These checks cover the pinned Odyssey build and explicit fixture scenarios. They do not establish complete outfitting parity across every ship, preset and experimental combination. Hidden WinUI verification checks control construction and both languages; visual acceptance remains with the user.
