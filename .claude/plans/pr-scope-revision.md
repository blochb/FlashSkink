# PR scope-revision — Split V1 into FlashSkink-Core (OSS, CLI-only) and a separate paid layer

**Branch:** `docs-change-v1-scope`
**Blueprint sections (impact map):** §1, §3, §4.1, §4.2, §4.3, §5 (no semantic change), §8 (relocation), §10 (no change), §11 (no change), §12 (REMOVED), §23, §24, §25 (REMOVED), §26.2, §28, §29.2 (A12, A13, B10, B11), §29.3, §30, §31
**Dev plan section:** none — this is a scope revision, not a dev-plan section PR. Affects future phases only; phases 0–3 are unchanged in spirit (only the Presentation/UI project tree is removed).

---

## Scope

Split V1 into two licensing layers. The **OSS Core layer** ("FlashSkink-Core") ships as CLI only — free, open-source, this repository. The **paid layer** (GUI, MVVM ViewModels, BYOC setup automation) is closed-source and lives in a separate private repo; its design lives in a blueprint stored under the user's `.claude` directory, not in this repo.

This PR makes that split mechanical. After it lands:

- `BLUEPRINT.md` describes the OSS Core layer exclusively. No GUI surface, no Avalonia, no MVVM, no Presentation layer, no setup automation. Post-V1 sections discuss only deferred OSS features, never paid-layer features.
- `FlashSkink_blueprint_paid.md` is created **outside the repo**, at `C:\Users\bb87855\.claude\projects\C--Users-bb87855-source-repos-FlashSkink\FlashSkink_blueprint_paid.md`. It carries every paid-layer section moved out of `BLUEPRINT.md`, plus the framing that ties paid → Core.
- `src/FlashSkink.Presentation/` and `src/FlashSkink.UI.Avalonia/` are deleted from the tree, along with `tests/FlashSkink.Tests/Presentation/`. The `FlashSkink.slnx` solution file drops both project entries.
- `NotificationBus` and `NotificationDispatcher` (currently in `FlashSkink.Presentation/Notifications/`) move to `FlashSkink.Core/Notifications/`. They are pure infrastructure (Channel-based fan-out) with no UI dependency; the only reason they lived in Presentation was historical. The contracts already live in `Core.Abstractions.Notifications` and don't move. Tests move from `tests/FlashSkink.Tests/Presentation/Notifications/` to `tests/FlashSkink.Tests/Notifications/`.
- The CLI surface in BLUEPRINT.md §23 is expanded so a CLI-only user can operate FlashSkink-Core end-to-end: write/read/list/move/delete/mkdir, status, heal, rebuild-tail, view logs, list/ack background failures, plus daemon mode for keeping the upload worker attached. The original V1 CLI restriction "no write operations — GUI is the primary write surface" is lifted.
- BYOC setup in OSS is **instructions only**: each provider's setup is a documented step-by-step guide the user follows manually in the provider's developer console. The CLI command `flashskink-cli setup` prints these steps and accepts the resulting `clientId` / `clientSecret` / `refreshToken` as input. No browser automation, no Google Cloud project creation, no Dropbox App Console calls. Provider setup *automation* is a paid-layer feature.
- `CLAUDE.md` is updated: principles 8–11 collapse into a single CLI-only layering rule; the file-layout section drops Presentation and UI.Avalonia; the project-summary, principles, conventions, and quick-reference sections lose every paid-layer reference; the standing-orders document is reframed for FlashSkink-Core.
- `README.md` is updated: positioning, status table, project structure, and contributor sections reflect the CLI-only OSS Core. The paid layer is mentioned briefly in a single sentence ("A separately-licensed paid layer adds a GUI and setup automation on top of Core; it is not part of this repository.") so readers aren't surprised to discover it elsewhere later.
- `tests/FlashSkink.Tests/ArchitectureTests.cs` drops the four tests referencing `FlashSkink.Presentation` and `FlashSkink.UI.Avalonia` and keeps the three Core/Abstractions/Avalonia-absence tests, rewritten to assert the new shape: Core/Abstractions/CLI assemblies contain no UI-framework reference.
- No production code under `FlashSkink.Core` or `FlashSkink.Core.Abstractions` is touched in this PR. No phase 0–3 commits are reverted or rewritten. The only code movement is the `NotificationBus`/`NotificationDispatcher` relocation, which is a namespace + folder move — the type bodies are unchanged.

The product promise — *any single surviving part + 24-word recovery phrase regenerates everything* — is unchanged. The mirror model is unchanged. The brain schema is unchanged. The blob format is unchanged. The key hierarchy is unchanged. Crash consistency is unchanged. The provider contract is unchanged. The only things that change are: the project tree, the surface (CLI-only), the docs, and a small relocation of two infrastructure types.

---

## Files to create

- `.claude/plans/pr-scope-revision.md` — this plan. ~600 lines.
- `C:\Users\bb87855\.claude\projects\C--Users-bb87855-source-repos-FlashSkink\FlashSkink_blueprint_paid.md` — paid-layer blueprint, **outside repo**. Lifts current §8.3 implementation paragraphs (the bus location story), §12 in full, §24 (setup automation flow), §25 in full, plus a new §1 framing the layer's relationship to Core and a new §2 listing the contracts the paid layer consumes from Core. ~600–800 lines.

## Files to modify

- `BLUEPRINT.md` — major rewrite. Sections affected:
  - §1 Project Overview — remove "small GUI" mention; reposition Core as CLI-only.
  - §3 Core Design Principles — remove "Core / UI separation" and "UI-agnostic ViewModels" rows; reword "Appliance positioning" to drop GUI from the surface list.
  - §4.1 Project Layout — remove `FlashSkink.Presentation/` and `FlashSkink.UI.Avalonia/` subtrees from the diagram; remove `tests/.../Presentation/` subfolder; add `FlashSkink.Core/Notifications/` to the Core tree.
  - §4.2 Dependency Graph — redraw without Presentation/UI nodes; arrows: Abstractions ← Core ← CLI; Tests → Core, Core.Abstractions. Rewrite the bulleted rules accordingly.
  - §8.3 Notification Bus — move `NotificationBus` and `NotificationDispatcher` location language from "FlashSkink.Presentation.Notifications" to "FlashSkink.Core.Notifications". The contracts stay in `Core.Abstractions.Notifications`. Update the "Where the types live" paragraph and the namespace block in the code example. Drop the "GUI registers one handler" framing — CLI is the only handler-registrant in OSS.
  - §8.6 End-to-End Flow — drop `UiHandler` from the fan-out diagram; keep `PersistenceHandler` + `CliHandler`.
  - §12 Presentation Layer Contracts — **DELETE** the section and renumber subsequent sections (13→12, 14→13, …, 31→30). Adjust every cross-reference in the doc.
  - §23 CLI Reference — expand to full surface (command tree below in "CLI command surface" section). Drop "CLI write operations are V2" wording.
  - §24 Setup CLI (Provider Automation) — replace with §24 "Provider Setup (manual)": each provider has a step-by-step guide the CLI prints; the CLI accepts the resulting credentials and stores them encrypted. The Google/Dropbox automation flows are removed from this blueprint (they live in the paid blueprint).
  - §25 GUI Surface — **DELETE** the section.
  - §26.2 Skink Directory Layout — remove the `FlashSkink.exe / FlashSkink ← GUI executable` line; keep only `flashskink-cli`.
  - §28 Tech Stack — remove rows for `Avalonia`, `CommunityToolkit.Mvvm`, the `UI.Avalonia` publish target. Adjust the "Notification channel" row's "Project" column to `Core` (was `Presentation`). Adjust "Logging abstractions" row to drop `Presentation`.
  - §29.2 Settled questionnaire decisions — review A12, A13, B10, B11 rows. A12 ("Progress visibility: status indicator + expandable detail panel") rewrites to "CLI `status` command with optional `--watch` live-refresh". A13 ("V1 Restore UX: minimal restore GUI") rewrites to "CLI `restore` command". B10 ("Setup CLI scope: Google + Dropbox automated; OneDrive manual") rewrites to "Setup CLI prints manual guides for all providers; automation is not in OSS Core". B11 ("Core GUI; advanced via CLI") rewrites to "CLI is the OSS Core surface".
  - §29.3 Post-V1 directions — review C1–C8. Drop "Full GUI surface" from V2 candidate list. C2 (Snapshot product) and C6 (Mobile) reference UIs that are now paid concerns; reword to discuss the engine and protocol angles only.
  - §30 Out of Scope (V1) — drop "GUI surface for verify / export / activity / full settings" (no GUI exists in OSS at all). Drop "CLI write operations (add/overwrite files via CLI)" — that becomes V1 in OSS. Add a single bullet under "Deferred to V2+" or "Out of OSS-Core scope" noting "GUI and setup automation live in a separately-licensed paid layer (out of this repository)."
  - §31 Post-V1 Direction — reword V1.1/V2 candidate lists; drop GUI-polish bullets; keep OneDrive provider, additional providers, performance tuning, plugin architecture, dedup. Add a single bullet acknowledging the paid layer split without describing its contents.

- `CLAUDE.md` — sections affected:
  - "Project summary" paragraph — remove "GUI is Avalonia via MVVM" and the five-project enumeration; replace with three-project enumeration (`Core.Abstractions`, `Core`, `CLI`) and CLI-only framing. Add one sentence noting the paid layer is a separate repo (no further detail here).
  - "File layout" diagram — drop `FlashSkink.Presentation/` and `FlashSkink.UI.Avalonia/` subtrees; drop `tests/.../Presentation/` subfolder; add `tests/.../Notifications/`.
  - "Principles" §8–11 — collapse into a **single principle keeping the literal numbering `8-11`** (i.e., the principle list reads `6. ... 7. ... 8-11. ... 12. ...`). Body: *"Open-source FlashSkink-Core is `Abstractions + Core + CLI` only. No UI framework reference appears anywhere in the tree. `Core` and `Core.Abstractions` have no project references to a Presentation or UI assembly (no such assembly exists in this repo). `CLI` references `Core` directly. Verified at the assembly level in CI. Numbered `8-11` because the pre-revision blueprint had four separate principles covering the `Presentation → Core`, `UI → Presentation → Core`, and `CLI ⊥ Presentation` arrangement; with the GUI and Presentation layer moved out of OSS, the four collapse to one. The numbering range is preserved so existing `.claude/plans/pr-*.md` cross-references to principles 12 and beyond remain resolvable without sweep edits."* Principles 1–7 and 12–32 keep their existing numbers verbatim.
  - "Conventions / Testing" — drop the "ViewModel tests run without a GUI" wording; drop the `Presentation/` subfolder from the test-project subfolder list.
  - "CI and automation" — drop references to GUI publish in nightly.yml; drop "principle-audit … FlashSkink.UI.Avalonia" phrasing if present (it isn't, but check).
  - "Quick reference" — drop any GUI / Presentation rows.
  - References to "GUI", "ViewModel", "Presentation" elsewhere — sweep and remove. The phrase "appliance positioning" stays but "CLI, GUI, copy" becomes "CLI, copy".

- `README.md` — sections affected:
  - One-line positioning under H1 — drop "encrypted cloud replicas that catch up in the background" (keep as-is, fine).
  - "Current state" status table — drop "Phase 6 — GUI, CLI, setup automation". Replace with "Phase 6 — Recovery, restore, healing CLI" or similar (final phase naming is a future-phase decision; the placeholder is fine).
  - "For users" — clarify portable CLI binaries; drop GUI wording.
  - "Project structure" — drop `FlashSkink.Presentation/` and `FlashSkink.UI.Avalonia/`. Adjust the brace tree.
  - Add a short paragraph under "License" or as a new section: *"FlashSkink-Core is MIT-licensed and CLI-only. A separately-licensed paid layer adding GUI and BYOC setup automation lives in a private repository; it is not part of this project."*

- `FlashSkink.slnx` — remove the two `<Project Path="..." />` entries for `FlashSkink.Presentation` and `FlashSkink.UI.Avalonia`. Result: 3 src projects + 1 test project.

- `tests/FlashSkink.Tests/ArchitectureTests.cs` — drop the four tests that exercise the removed projects: `Presentation_DoesNotReference_Avalonia`, `UI_DoesNotReference_Core_Directly`, `CLI_DoesNotReference_Presentation`, `Core_DoesNotReference_Presentation`. Keep and rename:
  - `Core_DoesNotReference_Avalonia` → keep as-is (defends against accidental Avalonia ref slipping into Core).
  - `CoreAbstractions_DoesNotReference_Avalonia` → keep as-is.
  - Add: `CLI_DoesNotReference_Avalonia` (defence in depth; CLI must stay UI-agnostic).
  - Add: `Core_DoesNotReference_UiOrPresentation` — asserts no referenced assembly name contains "Avalonia", "WPF", "WinForms", "Maui", or "FlashSkink.Presentation"/"FlashSkink.UI.*".

- `src/FlashSkink.CLI/FlashSkink.CLI.csproj` — no change. Already references `FlashSkink.Core` only.

- Move `src/FlashSkink.Presentation/Notifications/NotificationBus.cs` → `src/FlashSkink.Core/Notifications/NotificationBus.cs`. Namespace change: `FlashSkink.Presentation.Notifications` → `FlashSkink.Core.Notifications`. No body change.

- Move `src/FlashSkink.Presentation/Notifications/NotificationDispatcher.cs` → `src/FlashSkink.Core/Notifications/NotificationDispatcher.cs`. Same namespace change.

- Move `tests/FlashSkink.Tests/Presentation/Notifications/NotificationBusTests.cs` → `tests/FlashSkink.Tests/Notifications/NotificationBusTests.cs`. Update `using FlashSkink.Presentation.Notifications;` → `using FlashSkink.Core.Notifications;`. Update test namespace.

- Move `tests/FlashSkink.Tests/Presentation/Notifications/NotificationDispatcherTests.cs` → `tests/FlashSkink.Tests/Notifications/NotificationDispatcherTests.cs`. Same updates.

## Files to delete

- `src/FlashSkink.Presentation/` — the entire project directory (csproj, `Placeholder.cs`, the now-emptied `Notifications/` folder after the file moves, `obj/`, `bin/`).
- `src/FlashSkink.UI.Avalonia/` — the entire project directory (csproj, `Program.cs`, `obj/`, `bin/`).
- `tests/FlashSkink.Tests/Presentation/` — directory becomes empty after file moves; remove it.

## Dependencies

No NuGet changes. No project reference changes (CLI → Core is already the only chain in `FlashSkink.CLI.csproj`).

Side effect: `Directory.Packages.props` may carry `Avalonia.*` and `CommunityToolkit.Mvvm` `<PackageVersion>` entries that are no longer referenced by any project. Plan: scan `Directory.Packages.props` during implementation, and remove any `<PackageVersion>` that no project consumes after the deletion. Central Package Management does not error on unreferenced versions but they're dead weight.

## Public API surface

No public Core API changes in this PR.

Surface change is in namespace only:

### `FlashSkink.Core.Notifications.NotificationBus` (was `FlashSkink.Presentation.Notifications.NotificationBus`)

Body unchanged. Namespace changes. `public sealed` retained. Constructor signature retained. `INotificationBus` interface unchanged (lives in `Core.Abstractions.Notifications`).

### `FlashSkink.Core.Notifications.NotificationDispatcher` (was `FlashSkink.Presentation.Notifications.NotificationDispatcher`)

Body unchanged. Namespace changes. `public sealed` retained.

No other public API surface changes.

## CLI command surface (proposal — please review carefully; this is the new public surface for OSS Core)

The CLI subsumes everything the GUI was going to do, plus the existing read/audit/recovery surface. Command tree below uses `flashskink-cli` as the binary name (matches §26.2). Output is human-readable by default; every command accepts `--json` for machine-readable output. Long-running commands accept `--watch` where it makes sense (live refresh).

```
# Volume lifecycle
flashskink-cli init        --skink <path>                          # First-run init: prompts for password, generates mnemonic, shows recovery-phrase ceremony.
flashskink-cli unlock      --skink <path> [--password <pwd>]       # Unlock + start daemon-attached session (foreground unless detached, see daemon).
flashskink-cli lock        --skink <path>                          # Explicit close: flushes brain, zeroes keys.
flashskink-cli info        --skink <path>                          # Volume identifier, schema version, tail count, key-derivation params.
flashskink-cli status      --skink <path> [--watch]                # Aggregate sync state per tail; --watch refreshes every 2s.

# Setup (manual; automation is a paid-layer feature)
flashskink-cli setup guide        --provider <google|dropbox|onedrive|filesystem>
                                                                   # Prints the step-by-step manual setup guide for the given provider.
flashskink-cli setup add          --skink <path> --provider <type> --client-id <id> [--client-secret <secret>] [--root <path>]
                                                                   # Registers a provider after the user has completed the manual setup; opens OAuth consent in the user's browser via system browser launcher for OAuth providers.
flashskink-cli setup test         --skink <path> --tail <providerId>
                                                                   # Dry-run a small upload + verify to confirm credentials work end-to-end.

# Tail management
flashskink-cli tail list          --skink <path>
flashskink-cli tail add           --skink <path> --provider <type> [...]   # Alias of `setup add`.
flashskink-cli tail remove        --skink <path> --tail <providerId> [--purge-remote]
flashskink-cli tail health        --skink <path> [--tail <providerId>] [--watch]
flashskink-cli tail rebuild       --skink <path> --tail <providerId>       # Force a complete re-upload of all blobs to this tail (recovery from severe drift).
flashskink-cli tail pause         --skink <path> --tail <providerId>
flashskink-cli tail resume        --skink <path> --tail <providerId>

# File operations (NEW in V1 OSS — was V2 under the GUI-primary plan)
flashskink-cli ls          --skink <path> [<virtualPath>] [--recursive] [--long]
flashskink-cli stat        --skink <path> <virtualPath>
flashskink-cli find        --skink <path> --name <glob> [--type file|folder] [<rootVirtualPath>]
flashskink-cli put         --skink <path> <hostPath> [--to <virtualPath>] [--recursive]
                                                                   # Write file/folder from host into the skink (Phase 1 commit).
flashskink-cli get         --skink <path> <virtualPath> --output <hostPath>
                                                                   # Read a file from the skink to the host.
flashskink-cli mv          --skink <path> <fromVirtualPath> <toVirtualPath>
flashskink-cli rm          --skink <path> <virtualPath> [--recursive] [--purge]
                                                                   # Default: soft-delete (grace period). --purge: bypass grace; --recursive for folders.
flashskink-cli mkdir       --skink <path> <virtualPath> [--parents]
flashskink-cli restore     --skink <path> --file <virtualPath> --output <hostPath> [--since <timestamp>]
                                                                   # Restore (from grace-period soft-delete or normal copy-out). Equivalent to get + grace lookup.
flashskink-cli prune       --skink <path>                           # Force grace-period purge sweep (normally automatic on a timer).

# Daemon / queue
flashskink-cli daemon      --skink <path> [--detach]               # Run the background upload + audit + healing services in the foreground (default) or detached.
                                                                   # Exits cleanly on SIGINT; resumes from session state on next launch.
flashskink-cli queue list  --skink <path> [--tail <providerId>] [--status <pending|inflight|failed>]
flashskink-cli queue retry --skink <path> --file <fileId> [--tail <providerId>]
                                                                   # Force a stuck queue row to retry now.

# Integrity / healing
flashskink-cli verify      --skink <path> [--tail <providerId>] [--scope local|remote|both]
                                                                   # Walk blobs + (optionally) tails, confirm hashes. Long-running; respects --watch and Ctrl-C.
flashskink-cli heal        --skink <path> [--tail <providerId>]    # Force a self-healing pass (normally automatic).
flashskink-cli scrub       --skink <path>                          # Walk the brain for orphans (Files without Blobs, Blobs without files) and reconcile.

# Activity / logs / failures
flashskink-cli activity    --skink <path> [--since <ts>] [--category <cat>] [--tail-n <N>]
flashskink-cli log         --skink <path> [--lines <N>] [--follow]
                                                                   # Read the on-skink Serilog file at .flashskink/logs/. --follow tails.
flashskink-cli failures list   --skink <path> [--unacked-only]     # List rows from BackgroundFailures.
flashskink-cli failures ack    --skink <path> --id <failureId>
flashskink-cli failures ack-all --skink <path>

# Recovery / secrets
flashskink-cli reveal-phrase   --skink <path>                       # Password-required, enforces 60s countdown.
flashskink-cli change-password --skink <path>
flashskink-cli reset-password  --skink <path> --mnemonic <words>
flashskink-cli recover     --mnemonic <words> --provider <type> --client-id <id> --client-secret <secret> --output <newSkinkPath>
                                                                   # Reconstruct a new skink from any one tail + the recovery phrase.
flashskink-cli export      --skink <path> --output <directory>     # Walk every file, write to the host in original tree shape.

# Configuration
flashskink-cli config get  --skink <path> <key>
flashskink-cli config set  --skink <path> <key> <value>
flashskink-cli config list --skink <path>

# Diagnostics
flashskink-cli support-bundle --skink <path> --output <bundleFile>
flashskink-cli wal-recover    --skink <path>                        # Explicit WAL replay (normally automatic at unlock).
flashskink-cli version
flashskink-cli completion <bash|zsh|fish|powershell>                # Emit a shell-completion script.
flashskink-cli --help
```

### Rationale for the additions over the original §23 list

1. **File operations (`put`, `get`, `ls`, `mv`, `rm`, `mkdir`, `stat`, `find`)** — the original V1 deferred CLI write to V2 because the GUI was the primary write surface. With the GUI moved out of OSS, the OSS user has no other way to put files onto the skink. This is the largest scope addition.

2. **`daemon`** — without a GUI process to host the background services, the user needs an explicit way to "run the upload queue while my USB is plugged in." `daemon --detach` is the headless analog of the GUI's always-on background. Default foreground mode is ergonomic for `Ctrl-C` shutdown.

3. **`tail rebuild`** — a severely diverged tail (long offline, partial corruption) recovers faster by re-uploading everything than by waiting for self-healing to converge. This is a power-user lever.

4. **`failures ack` / `failures ack-all`** — the persistence handler writes to `BackgroundFailures` (§8.5). Without a GUI, the user needs CLI commands to view and dismiss those rows; otherwise they accumulate forever.

5. **`log --follow`** — the on-skink Serilog file is the user's window into what the daemon is doing. `tail -f` over a USB path works but mixing in JSON-formatted Serilog Compact output is awkward; an integrated viewer that pretty-prints is friendlier.

6. **`scrub`, `prune`, `wal-recover`** — diagnostic / explicit-trigger versions of normally-automatic background tasks. Useful for support sessions and recovery-from-broken-state.

7. **`completion`** — shell completion script generation. `System.CommandLine` has a built-in generator; exposing it removes guesswork from the user's terminal.

8. **`--watch`** on `status`, `queue list`, `tail health`, `activity` — TUI-style live-refresh without a separate viewer. Implemented with `Console.Clear` + redraw; not curses, just simple loops.

9. **`--json`** on every command — already in §23.3; carries over. Critical for scripting.

10. **`setup test`** — dry-run after manual setup. Confirms the credentials the user just pasted actually work before they commit to using the provider as a tail. Saves "I configured Dropbox and the first 500 GB silently failed" support sessions.

### Commands deliberately NOT added (out of scope for this PR)

- `flashskink-cli sync` / `flashskink-cli upload-now` — the daemon handles this; an explicit sync command is a confusing duplicate.
- Interactive TUI mode (full curses-style file browser) — large scope, separate decision. The `--watch` flag covers the live-status need; a full TUI is post-V1.
- `flashskink-cli serve` (REST API for third-party clients) — out of scope; would invite plugins / remote access concerns that don't fit OSS Core's threat model.
- `flashskink-cli benchmark` — not a user-facing feature.

## Method-body contracts

No method-body contracts change in this PR. The `NotificationBus`/`NotificationDispatcher` move is namespace-only.

The CLI command surface above is a **design proposal**; this PR's implementation step does NOT build any new CLI commands. It only documents them in `BLUEPRINT.md §23`. The actual CLI implementation lands in future phases (currently `phase-6-*.md`, or split across more granular phases — that's a separate planning decision).

## Integration points

- `FlashSkink.Core.Notifications.NotificationBus` and `NotificationDispatcher` are constructed in the CLI's `Program.cs` host-bootstrap. The CLI registers handlers: a `PersistenceNotificationHandler` (already in Core, no change) and a CLI-stderr handler (future PR). No code changes in this PR — only namespace + folder.
- `tests/FlashSkink.Tests/Notifications/NotificationBusTests.cs` and `NotificationDispatcherTests.cs` consume the moved types. `using` changes only.

## Principles touched

This PR rewrites the principles section, so "touched" is broader than usual:

- **Principles 8, 9, 10, 11** — collapsed into a single new Principle 8 covering OSS Core layering: `Abstractions + Core + CLI` only; no UI framework reference anywhere; CLI references Core directly. The old principle 11 ("CLI has no Presentation reference") becomes vacuous (no Presentation exists) and folds in.
- **Principle 13 (CancellationToken `ct` last)** — unchanged.
- **Principle 25 (Appliance vocabulary discipline)** — wording adjustment: the user-visible surfaces list drops "GUI". Internal/external vocabulary lists are unchanged.
- **Principle 32 (No telemetry, no update checks, no network chatter)** — unchanged.

All other principles are unchanged.

## Test spec

### `tests/FlashSkink.Tests/ArchitectureTests.cs` — rewritten

- `Core_DoesNotReference_Avalonia` — kept verbatim.
- `CoreAbstractions_DoesNotReference_Avalonia` — kept verbatim.
- `CLI_DoesNotReference_Avalonia` — NEW. Same shape as the Core test, checking `FlashSkink.CLI`.
- `Core_DoesNotReference_UiOrPresentation` — NEW. Asserts no referenced assembly name starts with `Avalonia`, `System.Windows`, `PresentationFramework`, `WindowsBase`, `Microsoft.Maui`, `Microsoft.UI`, `FlashSkink.Presentation`, or `FlashSkink.UI.`.
- `CoreAbstractions_DoesNotReference_UiOrPresentation` — NEW. Same check against `FlashSkink.Core.Abstractions`.
- `CLI_DoesNotReference_UiOrPresentation` — NEW. Same check against `FlashSkink.CLI`.
- Deleted: `Presentation_DoesNotReference_Avalonia`, `UI_DoesNotReference_Core_Directly`, `CLI_DoesNotReference_Presentation`, `Core_DoesNotReference_Presentation`.

### `tests/FlashSkink.Tests/Notifications/NotificationBusTests.cs`

- Moved from `tests/FlashSkink.Tests/Presentation/Notifications/`. `using` and `namespace` updated.
- All existing tests retained; assertions unchanged.

### `tests/FlashSkink.Tests/Notifications/NotificationDispatcherTests.cs`

- Moved from `tests/FlashSkink.Tests/Presentation/Notifications/`. `using` and `namespace` updated.
- All existing tests retained; assertions unchanged.

### No new functional tests

The PR is documentation + project-restructure + namespace move. There is no new logic to test. Build-clean + existing-tests-green is the bar.

## Acceptance criteria

- [ ] `dotnet build -c Release` is clean on Windows and Ubuntu, zero warnings.
- [ ] `dotnet test -c Release` is green; no test count regression beyond the four architecture tests deliberately removed and the two new ones added (`CLI_DoesNotReference_Avalonia` + the three `*_DoesNotReference_UiOrPresentation` checks).
- [ ] `dotnet format --verify-no-changes` is clean.
- [ ] `BLUEPRINT.md` contains zero matches for `Avalonia`, `Presentation` (layer name), `MVVM`, `ViewModel`, `GUI`, `setup automation`, `setup wizard`, `IDialogService`, `INavigationService`, `IFilePickerService`, `IBrowserService`, `IClipboardService`, `CommunityToolkit.Mvvm`. (Grep audit.)
- [ ] `BLUEPRINT.md` table of contents matches the new section numbering; no broken section references; no orphan `§N` citations.
- [ ] `CLAUDE.md` contains no references to `FlashSkink.Presentation`, `FlashSkink.UI.Avalonia`, `Avalonia`, `MVVM`, `ViewModel`. (Grep audit; principle wording allowed only in the section that explicitly removes them.)
- [ ] `README.md` reflects the three-project structure and the CLI-only positioning.
- [ ] `FlashSkink.slnx` lists exactly 4 projects: `CLI`, `Core`, `Core.Abstractions`, `Tests`.
- [ ] `src/FlashSkink.Presentation/` and `src/FlashSkink.UI.Avalonia/` directories no longer exist.
- [ ] `tests/FlashSkink.Tests/Presentation/` directory no longer exists.
- [ ] `tests/FlashSkink.Tests/Notifications/` exists with the two moved test files.
- [ ] `src/FlashSkink.Core/Notifications/` exists with the two moved type files.
- [ ] `Directory.Packages.props` carries no `<PackageVersion>` for `Avalonia.*` or `CommunityToolkit.Mvvm` if those packages are no longer referenced by any project.
- [ ] `C:\Users\bb87855\.claude\projects\C--Users-bb87855-source-repos-FlashSkink\FlashSkink_blueprint_paid.md` exists at the user-level location specified, **not in the repo**.
- [ ] The 4 phase-0..3 dev-plan files are untouched (verified by git diff: `dev-plan/` modifications = none).
- [ ] No production code under `src/FlashSkink.Core/` or `src/FlashSkink.Core.Abstractions/` is modified, except for the namespace headers of the two moved Notification files.

## Line-of-code budget

- `BLUEPRINT.md` — net delta ≈ -250 lines (paid-layer sections removed are larger than the CLI surface additions). New total ≈ 2750.
- `CLAUDE.md` — net delta ≈ -30 lines (principles 8–11 collapse to one; file-layout diagram shrinks).
- `README.md` — net delta ≈ -8 lines (project-structure shrinks, status table trims, paid-layer paragraph adds).
- `FlashSkink_blueprint_paid.md` — new ≈ 700 lines.
- `.claude/plans/pr-scope-revision.md` — this file ≈ 500 lines.
- `tests/FlashSkink.Tests/ArchitectureTests.cs` — net delta ≈ +10 lines (deletions outweighed by the new generic-UI-reference test methods).
- `FlashSkink.slnx` — -2 lines.
- Notification file moves — 0 LOC delta (folder/namespace changes only).

Total: roughly +1200 net lines (driven by the new paid blueprint that lives outside the repo), of which -290 are removed from this repo.

## Non-goals

- **Do NOT** implement any new CLI commands in this PR. The new command tree is documented in BLUEPRINT.md §23 only; implementation lands in future phases.
- **Do NOT** rewrite phases 0–3 dev-plan files. Past PRs already shipped per the old plan; rewriting history adds churn without benefit. Future dev-plan files (4+) will be written against the updated blueprint.
- **Do NOT** delete the `Phase 6 — GUI, CLI, setup automation` status row from `README.md` without replacing it; just rename to a CLI-only equivalent. Leaving the row blank breaks the table.
- **Do NOT** change anything in `BLUEPRINT.md §6, §7, §9, §10, §11, §13, §14, §15, §16, §17, §18, §19, §20, §21, §22, §27` (Result pattern, logging, memory management, providers, public API, storage, pipeline, upload, brain, file type, security, USB, healing, crash recovery, health monitoring, built-in providers). These are unaffected by the scope split.
- **Do NOT** modify `.github/workflows/*.yml`. The assembly-layering CI job invokes the architecture tests by name pattern; updating the test file is sufficient. The path-scoped review jobs (`pr-review-crypto.yml`, `pr-review-recovery.yml`) don't fire on doc-only changes. `principle-audit` reads `CLAUDE.md` "## Principles" section dynamically — no workflow change needed.
- **Do NOT** modify `Directory.Build.props` or `global.json`. Unaffected.
- **Do NOT** open the paid-layer repo, set up its CI, or sketch its plan structure in this PR. The paid blueprint at the user-level path is the only paid-layer artifact this PR produces.
- **Do NOT** introduce any "if paid layer present" conditional logic anywhere in Core or CLI. The paid layer is additive to Core — it consumes Core's existing public surface — never the reverse.
- **Do NOT** publish or push the new paid blueprint anywhere. It exists only on the user's local disk.
- **Do NOT** add an `<InternalsVisibleTo>` for a hypothetical paid assembly. The paid layer consumes only public Core API per principle 23 (frozen provider contract); same discipline applies to every Core type the paid layer touches.

---

## Review-of-existing-code (rule 5: "should not affect already specified plans and implemented code")

A direct audit of what's been built so far and whether this PR disturbs it:

- **`src/FlashSkink.Core.Abstractions/`** — untouched in this PR.
- **`src/FlashSkink.Core/`** — only addition is a new `Notifications/` folder receiving two moved files. No existing Core code is modified.
- **`src/FlashSkink.CLI/`** — `Program.cs` is currently a near-empty stub; untouched in this PR. New commands are *documented*, not *implemented*, in this PR.
- **`src/FlashSkink.Presentation/`** — deleted. Contents: a `Placeholder.cs`, two `Notifications/` files (moved into Core). No production logic is lost.
- **`src/FlashSkink.UI.Avalonia/`** — deleted. Contents: an empty `Program.cs` stub. No production logic is lost.
- **`tests/FlashSkink.Tests/`** — only the `Presentation/` subfolder is removed; its two test files move into a new `Notifications/` subfolder with `using`/`namespace` updates. Four architecture tests delete; three new ones add. No assertion logic changes.
- **`dev-plan/phase-0..3-*.md`** — untouched.
- **`BLUEPRINT.md`** — rewritten. None of phases 0–3's already-implemented code depends on the GUI/Presentation paragraphs being present; they were forward-looking sections.
- **`CLAUDE.md`** — principle renumbering. The numbers are referenced in past plan files (`.claude/plans/pr-*.md`) and in past PR descriptions. **Concern:** post-renumber, "Principle 8" in an old plan refers to a different rule. **Mitigation:** the renumbering only affects principles ≥ 9; principle numbers 1–7 and the high-numbered crash-consistency / portability principles keep their numbers. I'll keep principle 8 as the "OSS Core layering" rule (a different statement than the current §8 but in the same position) and explicitly note in CLAUDE.md that old plan files reference the pre-revision numbering. Acceptable for a solo-dev project; alternative would be re-anchoring every principle number, which is more invasive.
- **CI workflows** — no change. The `assembly-layering` job runs the architecture tests by filter; the architecture-test file *contents* change, not the job. The `plan-check` job will detect that this is not a `pr/X.Y-*` branch and skip with success. `principle-audit` will skip because `plan-check` did not produce `has_plan=true`.
- **Branch ruleset** — no change.

**Conclusion:** rule 5 holds. No phase 0–3 commit is rewritten, no shipped code is broken, no test that exercises real logic is lost.

---

## Implementation order (for Gate 2 execution)

1. Move `NotificationBus.cs` + `NotificationDispatcher.cs` from `Presentation/Notifications/` to `Core/Notifications/`. Update namespaces.
2. Move the two test files from `tests/.../Presentation/Notifications/` to `tests/.../Notifications/`. Update `using` and namespaces.
3. Update `ArchitectureTests.cs` per the test spec above.
4. Delete `src/FlashSkink.Presentation/` and `src/FlashSkink.UI.Avalonia/` directories.
5. Delete `tests/FlashSkink.Tests/Presentation/` directory.
6. Update `FlashSkink.slnx` — remove the two project entries.
7. Update `Directory.Packages.props` — remove any now-unreferenced `Avalonia.*` and `CommunityToolkit.Mvvm` `<PackageVersion>` entries.
8. Rewrite `BLUEPRINT.md` per the file-modify list. Section numbering pass: §12, §25 deletions cause renumbering of §13–§31 down by 1 for §12-removal and down by another 1 for §25-removal — total -2 for sections after §25, -1 for sections §13–§24. Re-anchor every internal `§N` citation.
9. Rewrite `CLAUDE.md` per the file-modify list. Renumber principles in the Principles section. Update file-layout diagram. Sweep for residual GUI/MVVM/Presentation references.
10. Rewrite `README.md` per the file-modify list.
11. Write `FlashSkink_blueprint_paid.md` to the user-level path. Content: lifted §12, §24, §25 from current BLUEPRINT.md, plus a new §1 "Relationship to FlashSkink-Core" and a new §2 "Core public API consumed by the paid layer."
12. `dotnet build -c Release` (expect clean).
13. `dotnet test -c Release` (expect green).
14. `dotnet format --verify-no-changes`.
15. Commit + push + open PR.
