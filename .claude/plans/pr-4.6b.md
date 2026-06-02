# PR 4.6b — CLI setup commands + `skink` binary

**Branch:** `pr/4.6b-cli-setup-commands`
**Blueprint sections:** §24.3 (two-stage CLI flow), §24.4 (guides are embedded resources), §24.5 (loopback OAuth)
**Dev plan section:** phase-4-providers §4.6 (second of a two-PR split; depends on **4.6a** for `AddTailAsync`/`RemoveTailAsync`/`ListTailsAsync`)
**Phase-4 cross-cutting decisions touched:** 3 (single OAuth helper), 7 (setup commands live in `CLI/Commands/Setup/`)

**Depends on:** PR 4.6a merged (public tail API, `VolumeCreationOptions.ProviderSetups`, `TailConfiguration`/`TailInfo`).

---

## Scope

Turn the empty `Program.cs` stub into a real command tree and rename the user-facing command to **`skink`**:

1. **`skink` command name as a compile-time constant.** A single `CliMetadata.CommandName = "skink"` constant drives the root-command name (shown in `--help`/usage) and is substituted into the printed setup guides (so guide examples read `skink setup add …`). Recompiling with a different constant changes the name everywhere it is displayed.
2. **`skink` binary.** `<AssemblyName>skink</AssemblyName>` so `dotnet publish` emits `skink` / `skink.exe` — the command the user actually types. CI publish paths and docs updated to match.
3. **Four setup commands** under `CLI/Commands/Setup/` wired into a `setup` parent command: `guide`, `add`, `remove`, `list`. Embedded guide resources. `LoopbackOAuthCapture`, the cloud `IProviderSetup` instances, and the concrete `NotificationBus` are wired in a thin composition root.

File-operation commands (`write`/`read`/`ls`/`daemon`/`status`/`logs`) are out of scope (Phase 6).

---

## Discrepancies surfaced (Gate 1)

1. **Command name vs. Blueprint §24.3.** Blueprint §24.3 examples show `flashskink-cli setup add …`. This PR changes the user-facing command to `skink` (user decision). Per the authority rule, the blueprint is updated in this same PR (causal link — the guides ship with the rule): §24.3's example lines and the §24 guide prose change `flashskink-cli` → `skink`. Also update the `CLAUDE.md` references to `flashskink-cli`. Flagged in the PR body.
2. **`System.CommandLine` has no "sensitive option" API** (confirmed against MS Learn for the 2.0.8 GA surface). The dev plan's "mark `--client-secret` sensitive" is satisfied by *intent*: the secret/auth-code/password is never echoed to stdout, never logged, never placed in `ErrorContext.Metadata` (Principle 26).
3. **`RootCommand` displayed name.** In `System.CommandLine` 2.0 a `RootCommand`'s name defaults to the executable filename. With `<AssemblyName>skink</AssemblyName>` (scope item 2) the default usage name is already `skink`; the constant is still used for guide templating and any explicit name set. The implementer must verify the exact API for forcing the displayed usage name to `CliMetadata.CommandName` rather than assuming.

---

## Files to create

- `src/FlashSkink.CLI/CliMetadata.cs` — `public static class CliMetadata { public const string CommandName = "skink"; public const string Description = "FlashSkink — portable nomadic backup."; }`. ~15 lines.
- `src/FlashSkink.CLI/Commands/Setup/SetupGuideCommand.cs` — `setup guide --provider <name>`; prints embedded guide; no skink open. ~75 lines.
- `src/FlashSkink.CLI/Commands/Setup/SetupAddCommand.cs` — `setup add`; OAuth/loopback (cloud) or path (filesystem); calls `AddTailAsync`. ~200 lines.
- `src/FlashSkink.CLI/Commands/Setup/SetupRemoveCommand.cs` — `setup remove`; confirm-unless-`--yes`; `RemoveTailAsync`; remote-cleanup reminder. ~115 lines.
- `src/FlashSkink.CLI/Commands/Setup/SetupListCommand.cs` — `setup list`; `ListTailsAsync`; table. ~100 lines.
- `src/FlashSkink.CLI/Setup/SetupGuideResources.cs` — embedded-resource reader + `{cli}`-placeholder substitution with `CliMetadata.CommandName`; provider-name→resource map. ~65 lines.
- `src/FlashSkink.CLI/Setup/IPasswordReader.cs` + `ConsolePasswordReader.cs` — password seam. ~60 lines.
- `src/FlashSkink.CLI/Setup/CliSetupFactory.cs` — composition helper: builds the `IProviderSetup` list (Google/Dropbox/OneDrive), `LoopbackOAuthCapture`, `NotificationBus`, and the `setup` command subtree; gives tests a seam to inject fakes. ~110 lines.
- `src/FlashSkink.CLI/Resources/setup-guides/google-drive.txt` — Drive BYOC walkthrough (uses `{cli}` placeholder). ~30 lines.
- `src/FlashSkink.CLI/Resources/setup-guides/dropbox.txt` — ~30 lines.
- `src/FlashSkink.CLI/Resources/setup-guides/onedrive.txt` — ~30 lines.
- `src/FlashSkink.CLI/Resources/setup-guides/filesystem.txt` — ~20 lines.
- `tests/FlashSkink.Tests/Engine/SetupCommandsEndToEndTests.cs` — full CLI-command flow with fakes + real FileSystem path. ~380 lines.

## Files to modify

- `src/FlashSkink.CLI/Program.cs` — Serilog → `ILoggerFactory`; invoke `CliSetupFactory` to build the `RootCommand` (name from `CliMetadata`); `rootCommand.Parse(args).InvokeAsync()`. ~+45.
- `src/FlashSkink.CLI/FlashSkink.CLI.csproj` — `<AssemblyName>skink</AssemblyName>`; mark `Resources/setup-guides/*.txt` as `<EmbeddedResource>`; keep `CliSetupFactory` + command classes `public` so the test project (project-ref) can drive them. ~+8.
- `tests/FlashSkink.Tests/FlashSkink.Tests.csproj` — add `ProjectReference` to `FlashSkink.CLI`. ~+1.
- `.github/workflows/release.yml` and `.github/workflows/nightly.yml` — update publish/output references to the `skink` assembly name (4-RID publish artifacts become `skink`/`skink.exe`; smoke/load-check steps that reference `FlashSkink.CLI.dll`/`.exe`). ~edits.
- `BLUEPRINT.md` §24.3 + §24 guide prose — `flashskink-cli` → `skink`. ~edits.
- `CLAUDE.md` — references to `flashskink-cli` → `skink`; note the `CliMetadata.CommandName` constant as the single source of truth. ~edits.

## Dependencies
- **NuGet:** none new (System.CommandLine + Serilog from Phase 0; SDK packages from §4.3–4.5).
- **Project references:** `tests/FlashSkink.Tests` → `src/FlashSkink.CLI` (new).

---

## Public API surface
This PR adds no new Core public API (4.6a did). The CLI command classes + `CliSetupFactory` + `CliMetadata` are the new public surface inside `FlashSkink.CLI`, exposed `public` solely so the test project (project-ref) can drive them. No cross-assembly contract beyond that.

---

## Internal types & composition

### `CliMetadata` (public static)
`CommandName = "skink"` (compile-time constant). Used by: `Program.cs` root command, and `SetupGuideResources` `{cli}` substitution. **Single source of truth** for the displayed/printed name.

### CLI command classes
Each `public sealed`, constructed by `CliSetupFactory` with injected dependencies (never `new`-ed inside) so the e2e tests substitute `FakeOAuthCaptureFlow`, `FakeProviderSetup`, a `StringWriter` for output, an `IPasswordReader` fake. Each exposes `public Command Build()` returning the configured `System.CommandLine.Command`. Output via an injected `TextWriter` (default `Console.Out`); errors via a `TextWriter` (default `Console.Error`).

### `SetupGuideResources` (internal static)
`Result<string> Read(string providerName)` → maps to `FlashSkink.CLI.Resources.setup-guides.{provider}.txt`, reads via `typeof(SetupGuideResources).Assembly.GetManifestResourceStream(...)`, then `text.Replace("{cli}", CliMetadata.CommandName)`. Unknown provider → `Fail(InvalidArgument)`.

### `IPasswordReader` / `ConsolePasswordReader`
`string ReadPassword(string prompt)`. **Resolution order in the command action (headless/scriptable robustness — Gate 1 feedback):**
1. `--password` supplied → use it, never touch the reader.
2. Else `Console.IsInputRedirected == true` → read one line from `Console.In` (no `ReadKey`; `ReadKey` throws when stdin is redirected). Keeps `echo … | skink setup add` and CI working.
3. Else (interactive TTY) → masked `Console.ReadKey(intercept:true)` loop.
`ConsolePasswordReader` checks `Console.IsInputRedirected` internally. Tests inject a fake reader or pass `--password`.

### `CliSetupFactory` (composition helper)
Production: builds `[new GoogleDriveSetup(lf), new DropboxSetup(lf), new OneDriveSetup(lf)]`, `new LoopbackOAuthCapture(lf)`, the concrete `NotificationBus` (confirm ctor at impl time), an `IPasswordReader`, then constructs the four command classes and assembles the `setup` parent + `RootCommand`. Disposes the cloud setups (Google/OneDrive are IDisposable) and the OAuth capture after invocation. Tests call an overload that accepts injected `IReadOnlyList<IProviderSetup>`, `IOAuthCaptureFlow`, `IPasswordReader`, and `TextWriter`s.

---

## Method-body contracts (command actions)

### `guide`
`--provider <name>` (required). `SetupGuideResources.Read(name)` → write guide to output (exit 0) or error to err (exit 1). No skink.

### `add`
Options: `--skink`(req), `--provider`(req), `--client-id`, `--client-secret`, `--root`, `--password`, `--force`. Action:
1. Resolve `setup` for `--provider` from the injected setup list (filesystem is handled by the volume's built-in setup; the CLI needs the cloud setup instance for `GetAuthorizationUriAsync`). Unknown → error exit 1.
2. Read password (resolution order above). Open volume: `FlashSkinkVolume.OpenAsync(skink, password)` with `VolumeCreationOptions{ ProviderSetups = injectedCloudSetups, NotificationBus, LoggerFactory, ForceUnlock = --force }`.
3. If `setup.SetupKind == OAuth`: `credentials = new ProviderCredentials{ClientId,ClientSecret}`; `ctx = oauthCapture.Prepare()`; `authUri = setup.GetAuthorizationUriAsync(ctx.RedirectUri, ctx.CodeChallenge, credentials, ct)`; `code = oauthCapture.AwaitAuthorizationCodeAsync(ctx, authUri, ct)`; build `TailConfiguration{ ProviderType, ClientId, ClientSecret, AuthorizationCode=code, CodeVerifier=ctx.CodeVerifier, RedirectUri=ctx.RedirectUri }`.
   Else (LocalPath): `TailConfiguration{ ProviderType="filesystem", LocalPath=--root }`.
4. `result = await volume.AddTailAsync(config, ct)`. Print "✓ {DisplayName} tail configured." (Principle 25) or a user-vocabulary error. Always dispose the volume (and cloud setups via the factory).
5. **Never echo/log** `--client-secret`, `--password`, the auth code, or the token (Principle 26).

### `remove`
`--skink`(req), `--provider`(=providerId, req), `--password`, `--yes`. Open volume; unless `--yes`, prompt confirm (via an injected prompt seam so tests can answer); `RemoveTailAsync`; print success + "Your data on {DisplayName} is not deleted — remove it there yourself if you want." Dispose volume.

### `list`
`--skink`(req), `--password`. Open volume; `ListTailsAsync`; print a table (DisplayName, Health, uploaded/pending counts, last upload). Empty → "No tails configured yet." Dispose volume.

### `Program.cs`
Build Serilog logger → `SerilogLoggerFactory`/`ILoggerFactory`; `var root = CliSetupFactory.Build(loggerFactory, deps)`; `return await root.Parse(args).InvokeAsync();`. Root command name = `CliMetadata.CommandName`, description = `CliMetadata.Description`. (Dev plan also mentions a Serilog `ProviderID` log scope on the upload pipeline — the pipeline already logs with `ProviderID`; ensure the Serilog config preserves scopes. Keep minimal.)

All user-facing strings use Principle-25 vocabulary (skink, tail, folder, recovery phrase) — never "provider", "OAuth", "DEK", "refresh token".

---

## Integration points
- `FlashSkinkVolume.AddTailAsync`/`RemoveTailAsync`/`ListTailsAsync` + `TailConfiguration`/`TailInfo` (from **4.6a**).
- `VolumeCreationOptions.ProviderSetups` (from 4.6a) — the CLI injects the cloud setups.
- `IOAuthCaptureFlow`/`OAuthCaptureContext` + `LoopbackOAuthCapture(ILoggerFactory)` (§4.1/§4.2).
- `GoogleDriveSetup`/`DropboxSetup`/`OneDriveSetup` `(ILoggerFactory)` (§4.3–4.5, public ctors).
- `NotificationBus` (concrete, `Core.Notifications`).
- `FakeOAuthCaptureFlow` (§4.2 test support), `FakeProviderSetup` (4.6a test support), `FaultInjectingStorageProvider` (§3.1).
- `System.CommandLine` 2.0.8 GA API (confirmed via MS Learn): `RootCommand`/`Command`/`Option<T>` with `{ Description=…, Required=… }` initializers; `command.SetAction(async (ParseResult pr, CancellationToken ct) => { … return exit; })`; `pr.GetValue(option)`; `root.Parse(args).InvokeAsync()`; subcommands via `command.Subcommands.Add(...)`.

---

## Principles touched
- **6/26** — CLI never echoes/logs secrets; no credential object in any log template.
- **12** — only OS branch is §4.2's browser launcher (unchanged); the password reader uses `Console.IsInputRedirected` (cross-platform).
- **13/14** — async actions take `ct`; cancellation surfaces as a clean exit, not a stack trace.
- **23** — no Core contract edits.
- **25** — `skink`-era user vocabulary throughout commands and guides.
- **32** — no telemetry; the only network is the §4.2 loopback during OAuth.

---

## Test spec

### `Engine/SetupCommandsEndToEndTests.cs` — class `SetupCommandsEndToEndTests`
Commands are constructed via the `CliSetupFactory` test overload with fakes, then driven by `root.Parse(args).InvokeAsync()` (or `command.Parse(...).InvokeAsync()`), capturing a `StringWriter`.
- `Guide_KnownProvider_PrintsEmbeddedGuide_WithSkinkName` — `guide --provider google-drive`: output contains the guide and the substituted `skink setup add` example (asserts `{cli}` was replaced, not literal). Exit 0.
- `Guide_UnknownProvider_PrintsErrorExit1`.
- `Add_FileSystem_ThenWrite_FileLandsOnTail` — **the §4.6 acceptance e2e**: create volume (helper), `setup add --provider filesystem --root <tailDir> --skink <skink> --password …`, write a file via the volume, poll `TailUploads`→`UPLOADED`, assert the sharded tail blob decrypts to the original (dev-plan lines 207/363).
- `Add_Cloud_WithFakeOAuthAndFakeSetup_ConfiguresTail` — `setup add --provider google-drive --client-id x --client-secret y` with `FakeOAuthCaptureFlow` (canned code) + `FakeProviderSetup` returning a `FaultInjectingStorageProvider`-wrapped `FileSystemProvider`; assert success + `Providers` row + adapter registered.
- `Add_Cloud_SecretNotEchoed` — neither stdout nor the in-memory log buffer contains the client secret or auth code.
- `Add_PasswordViaRedirectedStdin_Works` — no `--password`; pipe the password through a redirected `Console.In`/fake reader; assert success (no `ReadKey` exception).
- `List_AfterAdd_ShowsTailRow`.
- `Remove_AfterAdd_RemovesRowAndPrintsReminder`.
- `Remove_WithoutYes_PromptsAndAbortsOnNo` (inject "no" via the prompt seam).

Reuses `FakeOAuthCaptureFlow`, `FakeProviderSetup`, `FaultInjectingStorageProvider`, `FakeClock`/`TestNetworkAvailabilityMonitor`, and the volume harness.

### Build/rename verification
- The csproj change + a manual `dotnet publish` smoke (noted in the PR for the user) confirm the `skink` artifact name; the nightly `portable-publish-smoke` job exercises the renamed artifact.

---

## Acceptance criteria
- [ ] Builds with zero warnings on all targets; `tests` references `CLI`.
- [ ] `skink setup guide|add|remove|list` runnable; `--help` usage shows `skink`.
- [ ] `<AssemblyName>skink</AssemblyName>`; `dotnet publish` emits `skink`/`skink.exe`; `release.yml`/`nightly.yml` reference the new name.
- [ ] Guides are embedded; `guide` prints them with `{cli}`→`skink` substitution and no skink open.
- [ ] §4.6 e2e: `setup add --provider filesystem` → write → file lands on the FileSystem tail, decrypts to original.
- [ ] No secret echoed/logged (Principle 26; verified by `Add_Cloud_SecretNotEchoed`).
- [ ] Redirected-stdin password path works (no `ReadKey` exception).
- [ ] Blueprint §24.3/§24 and `CLAUDE.md` updated `flashskink-cli`→`skink`.
- [ ] No new `ErrorCode`; no UI-framework refs; `dotnet format --verify-no-changes` clean.

## Line-of-code budget
| Area | Lines |
|---|---|
| `CliMetadata` | ~15 |
| 4 command classes | ~490 |
| `CliSetupFactory` + `SetupGuideResources` + password | ~235 |
| `Program.cs` + csproj | ~55 |
| Embedded guides (4 txt) | ~110 |
| **src delta** | **~900** |
| `SetupCommandsEndToEndTests` + tests csproj | ~390 |
| Docs/CI edits (BLUEPRINT/CLAUDE/release/nightly) | ~40 |
| **Total delta** | **~1,330** |

## Non-goals
- No `write`/`read`/`ls`/`daemon`/`status`/`logs` commands (Phase 6).
- No interactive masked-password prompt as the *only* path — `--password` is the scriptable path; the masked prompt + redirected-stdin paths are provided, richer UX is Phase 6.
- No Core API changes (all in 4.6a).
- No healing/verification (Phase 5).
- No automated provider-console OAuth-app creation (paid layer).
- No edits to `IStorageProvider`/`UploadSession`/`IProviderSetup`/`ProviderCredentials`/`ProviderSetupKind`/`ProviderHealth` (Principle 23).
