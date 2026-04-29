# CLAUDE.md — FlashSkink project standing orders

**Purpose of this file:** You are Claude Code. This document defines who you are on this project, what you do, and where to find things. Read it fully at session start. It is the only document you must read before the user's first message.

**Authority:** This file + `BLUEPRINT.md` are the two authoritative documents. When they disagree, the blueprint wins; flag the discrepancy in your response and ask the user whether to update `CLAUDE.md`.

---

## Project summary

**FlashSkink** is a portable, nomadic backup system that distributes complete encrypted replicas of a user's data across a local USB flash drive ("the skink") and one or more cloud storage providers ("the tails"). The skink is the body the user carries; each tail is a full, independently recoverable copy. The application runs directly from the USB — nothing is installed on the host, no host state is written, no traces remain after unplugging.

**The product promise:** any single surviving part — the skink, or any one tail — plus the 24-word BIP-39 recovery phrase, regenerates everything. Lose the USB? Any tail regenerates the volume. Cloud account suspended? The skink or another tail regenerates it. You need exactly one surviving part. Nothing more.

**Architectural shape:** mirror model (not RAID-5; not striping). Every tail is a complete replica. Writes commit to the skink synchronously (Phase 1); uploads to tails happen asynchronously and resumably (Phase 2), surviving disconnect and host change.

**Tech shape:** C# 14 on .NET 10. Five projects in `src/` (`Core.Abstractions`, `Core`, `Presentation`, `UI.Avalonia`, `CLI`) and a single `tests/FlashSkink.Tests` project with subfolders per area. GUI is Avalonia via MVVM; CLI is `System.CommandLine`. Brain is SQLCipher-encrypted SQLite. V1 providers: Google Drive, Dropbox, OneDrive, and a local FileSystem adapter that doubles as the cloud-provider test double.

**Solo developer.** One person, with Claude Code as the implementation assistant. Visual Studio is used for review/compile/test, not authoring.

**Full scope lives in `BLUEPRINT.md`.** Do not restate blueprint content in responses unless asked.

---

## Session protocol

This is the core of this document. Follow it literally.

### Canonical prompt and response

When the user writes a prompt of the form:

> `read section X.Y of the dev plan and perform`

or similar ("read section 3.2 and perform", "do section 3.2", "start PR for section 3.2"), execute the following multi-step protocol. **Do not skip steps. Do not combine steps. Stop at every stop point.**

### Step 0 — Branch check

Before reading anything, run `git status` and `git branch --show-current`. If the current branch is `main`, stop and say:

> "Current branch is `main`. I need a PR branch before starting work. Should I create `pr/X.Y-<short-description>`? Please confirm the description."

Wait for the user to confirm the branch name. Then `git checkout -b pr/X.Y-<description>`.

If the current branch is already a `pr/X.Y-...` branch matching the section, continue. If it's a `pr/` branch for a different section, stop and ask the user which branch is correct.

### Step 1 — Read the dev plan and blueprint references

1. Read `dev-plan/phase-N.md` (pick the phase file containing section X.Y). Locate section X.Y.
2. Read every blueprint section referenced by section X.Y.
3. Read any prior `.claude/plans/pr-*.md` files for PRs that section X.Y depends on — you need to know their final public API, not just what was planned.

Do not write anything yet.

### Step 2 — Produce the PR task plan

Create `.claude/plans/pr-X.Y.md` using the template below. Include every checklist item. The standard is "complete enough that a competent implementer can follow without needing to guess the architecture." Not mechanical-transcription-grade — the user has chosen Sonnet as the implementer and the checklist is relaxed accordingly.

**Plan template:**

```markdown
# PR X.Y — <short title>

**Branch:** pr/X.Y-<description>
**Blueprint sections:** §A.B, §C.D (exact references)
**Dev plan section:** phase-N §X.Y

## Scope
<!-- One paragraph summarizing what this PR accomplishes. -->

## Files to create
- `src/FlashSkink.Xxx/File.cs` — <purpose, ~N lines>
- ...

## Files to modify
- `src/FlashSkink.Yyy/OtherFile.cs` — <scope of change, e.g. "add one
  method, one property">
- ...

## Dependencies
- NuGet: <package> <version>
- Project references: <added references>

## Public API surface
<!-- Every new public type/method/property/event with:
     - exact name, namespace, signature
     - XML <summary> intent (one line; prose is the implementer's) -->

### `FlashSkink.Core.Abstractions.Results.Result<T>` (readonly record struct)
Summary intent: result type carrying either a success value or an
ErrorContext. Returned from every public Core method that can fail.

- `bool Success { get; }`
- `T? Value { get; }`
- `ErrorContext? Error { get; }`
- `static Result<T> Ok(T value)`
- ...

## Internal types
<!-- Same precision as public; no XML intent. -->

## Method-body contracts
<!-- For non-trivial public methods: preconditions,
     postconditions, which ErrorCode values can be returned,
     cancellation behaviour. No pseudocode. -->

## Integration points
<!-- Which existing types/methods are called. Include their
     signatures so the implementer doesn't re-read the definitions. -->

## Principles touched
<!-- List of principle numbers from §"Principles" below. Gate 2 checks each. -->
- Principle 1 (Core never throws across its public API)
- Principle 13 (CancellationToken named `ct`, always last)
- ...

## Test spec
<!-- Per test file:
     - exact file path
     - exact test class name
     - each test method's name + what it asserts
     - [Theory] data sets when non-obvious -->

### `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs`
- `Encrypt_ThenDecrypt_ProducesOriginalPlaintext` — round-trip
  asserts ciphertext ≠ plaintext and decrypt output ≡ input bytes.
- ...

## Acceptance criteria
- [ ] Builds with zero warnings on all targets
- [ ] All new tests pass
- [ ] No existing tests break
- [ ] <plan-specific criteria>

## Line-of-code budget
- src/FlashSkink.Core/Crypto/CryptoPipeline.cs — ~200 lines
- tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs — ~250 lines
- Total: ~200 lines non-test, ~250 lines test

## Non-goals
<!-- Explicit list of things NOT to do in this PR.
     This is the primary defence against scope creep. -->
- Do NOT wire CryptoPipeline into WritePipeline yet (planned PR X.Y+1).
- Do NOT implement key zeroization here (PR X.Y+2).
- ...
```

### Step 3 — Stop for plan approval (Gate 1)

After writing the plan, stop. Say exactly:

> "Plan ready for review at `.claude/plans/pr-X.Y.md`. Approve to proceed to implementation, or tell me what needs revision."

Do not write any production code. Do not start implementation. Wait for explicit approval ("approved", "go", "proceed", "yes", etc.).

If the user requests revisions, revise the plan in place and stop again. Repeat until approved.

### Step 4 — Implementation

After approval:

1. Implement per the plan, creating and modifying only the files listed.
2. If during implementation you discover the plan is wrong or incomplete, **stop and escalate** — do not silently deviate. Say:
   > "The plan says X but implementing it requires Y. Should I (a) update the plan and re-approve, (b) treat this as a fundamental disagreement and abandon this PR, or (c) something else?"
3. When done, do not run tests yet.

### Step 5 — Stop for implementation approval (Gate 2)

Stop. Say:

> "Implementation complete. <N> files created, <M> files modified. No tests run yet. Approve to proceed to test writing and build, or flag issues."

Wait for approval.

### Step 6 — Tests and build loop

After approval:

1. Write tests per the plan's test spec.
2. Run `dotnet build` — fix until clean.
3. Run `dotnet test` — fix until green.
4. Iteration cap: 3 attempts. If the third attempt is still red, stop and write a diagnosis to `.claude/plans/pr-X.Y-stuck.md` containing:
    - what you tried on each attempt
    - what failed and the exact error
    - what you suspect is wrong
    - whether the plan seems under-specified in this area

   Then say:
   > "Stuck after 3 attempts. Diagnosis at `.claude/plans/pr-X.Y-stuck.md`. Handing back to you."

5. After dotnet test is green, run `dotnet format --verify-no-changes`. If it reports changes, run dotnet format, stage the changes, and amend the commit before pushing.

### Step 7 — Open the PR

Once tests are green:

1. `git add -A && git commit -m "<conventional commit message>"`.
2. `git push -u origin pr/X.Y-<description>`.
3. Open the PR via `gh pr create --base main --title "..." --body-file <generated body>`.
4. Fill in the PR template (`.github/pull_request_template.md`):
    - Dev plan section reference
    - Link to `.claude/plans/pr-X.Y.md`
    - Principles touched (copied from the plan)
    - Acceptance criteria checklist, marked off
    - Drift notes (usually empty)
    - Manual smoke test notes (for USB/host-touching PRs only — tell the user what they need to test manually)
5. Say:
   > "PR opened: <URL>. Session complete."

Then stop. Do not start another PR in the same session.

---

## File layout

```
/
├── CLAUDE.md                  ← this file (standing orders)
├── BLUEPRINT.md               ← architectural source of truth
├── FlashSkink.sln
├── Directory.Build.props      ← shared MSBuild settings (TFM, nullable, analyzers)
├── Directory.Packages.props   ← Central Package Management (all PackageVersion here)
├── global.json                ← pins .NET SDK version
├── .editorconfig
├── docs/
│   ├── architecture.md        ← derived/condensed from blueprint; reader-facing
│   ├── error-handling.md      ← Result pattern + cancellation worked examples
│   ├── provider-setup.md      ← BYOC setup walkthroughs (Google, Dropbox, OneDrive)
│   ├── recovery.md            ← recovery procedures (USB loss, brain corrupt, tail loss)
│   └── spike-findings.md      ← spike decisions archive
├── dev-plan/
│   ├── phase-0-foundation.md
│   ├── phase-1-crypto-and-brain.md
│   └── …                      ← one file per phase
├── .claude/
│   ├── plans/
│   │   ├── pr-1.1.md          ← task plans, committed permanently
│   │   ├── pr-1.1-stuck.md    ← diagnoses when stuck (deleted if PR succeeds)
│   │   └── …
│   └── commands/              ← custom slash commands (future)
├── .github/
│   ├── pull_request_template.md
│   └── workflows/             ← CI + publish workflows
├── src/
│   ├── FlashSkink.Core.Abstractions/   ← Result, ErrorCode, IStorageProvider, models
│   ├── FlashSkink.Core/                ← Engine, Crypto, Metadata, Providers, Upload, Healing, Usb, Orchestration
│   ├── FlashSkink.Presentation/        ← ViewModels, NotificationBus, UI-agnostic services
│   ├── FlashSkink.UI.Avalonia/         ← Views, Avalonia-specific services, Program.cs
│   └── FlashSkink.CLI/                 ← Commands, Program.cs
└── tests/
    └── FlashSkink.Tests/
        ├── Engine/
        ├── Crypto/
        ├── Metadata/
        ├── Providers/
        ├── Upload/
        ├── Healing/
        ├── CrashConsistency/   ← property-based invariant tests
        └── Presentation/
```

**Never** create files outside this layout without explicit user approval. The `src/` + `tests/` split is the prevailing convention in modern .NET OSS and is load-bearing for CI scripting (`dotnet test tests/**/*.csproj` vs `dotnet publish src/FlashSkink.UI.Avalonia/...`). See blueprint §4.1.

---

## Branch naming

- `pr/X.Y-short-description` — feature PR tied to dev-plan §X.Y. Example: `pr/3.2-write-pipeline-phase1-commit`.
- `spike/topic` — exploratory spike. Code may be discarded. Example: `spike/sqlcipher-native-rids`.
- `fix/short-description` — bug fix not tied to dev-plan section.
- `docs/short-description` — documentation-only change.

---

## Principles (load-bearing rules)

These are checked at every gate. Each plan lists which apply to the PR; each implementation is audited against them at Gate 2. **Violating a principle is a Gate 2 rejection — not a discussion.**

1. **Core never throws across its public API boundary.** Every `public` method on every type in `FlashSkink.Core` and `FlashSkink.Core.Abstractions` returns `Result` or `Result<T>`. A public `Create()` that returns a raw `SqliteConnection` is a violation — it must return `Result<SqliteConnection>`. Exceptions flow as data inside `ErrorContext`, never across the boundary. (Blueprint §6.1)
   - **Sanctioned exception:** `IAsyncEnumerable<readonly record struct>` brain hot-path readers (e.g., `UploadQueueRepository.DequeueNextBatchAsync`) may propagate `SqliteException` and `OperationCanceledException` to the caller rather than wrapping in `Result`. The caller (a background service) is expected to handle them. This deviation must be documented with an XML comment citing blueprint §9.7. No other public method in Core may use this carve-out.

2. **Single survivor recovers everything — mirror, not stripe.** Every tail is a complete, independently recoverable encrypted replica. No parity. No dependence between tails. Any change that introduces cross-tail dependency contradicts the product promise. (Blueprint §5, DR-1)

3. **The skink is authoritative; tails are catch-up replicas.** Reads on a healthy skink read from the local blob, never from a tail. Tails are touched only for upload, integrity verification, recovery, and self-healing. (Blueprint §5.3)

4. **Two commit boundaries stay sharp.** Phase 1 (skink write) is synchronous and transactional — returns success only when the brain row is committed. Phase 2 (tail upload) is asynchronous, resumable, and best-effort. A tail being slow, offline, or full never blocks Phase 1. (Blueprint §5.2, DR-3)

5. **Upload session state lives on the skink, not in memory.** `UploadSessions` rows persist resumable-upload context so uploads survive disconnection and resume on a different host. An upload that loses its session state on process exit is a defect. (Blueprint §15.1, DR-4)

6. **Zero-knowledge at every external boundary.** All file content and all metadata leave the skink encrypted — including the brain mirror uploaded to tails. No plaintext data and no raw keys ever cross a provider boundary. (Blueprint §3)

7. **Zero trust in the host.** No installation. No host state. `Path.GetTempPath()` is not used in the normal write or upload paths. Staging lives on the skink at `.flashskink/staging/`. (Blueprint §3, §13.3, §26.4)

8. **Core holds no UI framework reference.** `FlashSkink.Core` and `FlashSkink.Core.Abstractions` compile and pass tests with zero reference to Avalonia or any UI toolkit. Verified at the assembly level in CI. (Blueprint §4.2)

9. **Presentation holds no UI framework reference.** `FlashSkink.Presentation` is UI-agnostic; ViewModel tests run without a GUI. Adding an Avalonia or WPF reference to Presentation is a Gate 2 rejection. (Blueprint §3, §4.2)

10. **UI project has no direct Core reference** except `Program.cs` for DI wiring. `FlashSkink.UI.Avalonia` consumes Core through `FlashSkink.Presentation`. (Blueprint §4.2)

11. **CLI has no Presentation reference.** `FlashSkink.CLI` consumes `FlashSkink.Core` directly. (Blueprint §4.2)

12. **OS-agnostic by default.** No platform-specific APIs, paths, or assumptions. Behaviour identical on Windows, macOS (Intel and Apple Silicon), and Linux. Platform-branching code requires explicit justification in the plan. (Blueprint §3)

13. **`CancellationToken` parameters are always named `ct` and always last.** Every public async method that performs I/O, network, or CPU work accepts `CancellationToken ct` as its final parameter. `ct = default` is acceptable; absence is not. (Blueprint §4.3, §6.6)

14. **`OperationCanceledException` is always the first catch** on any method that accepts a `CancellationToken` or awaits something cancellable. It maps to `ErrorCode.Cancelled` and is logged at `Information` or `Debug` — never `Error`. Cancellation is not a fault. (Blueprint §6.6)

15. **A bare `catch (Exception ex)` is never the only catch.** Catch specific types first (narrow to broad), then `Exception` as a final fallback mapped to `ErrorCode.Unknown`. `SqliteException` filtered by `SqliteErrorCode`, `HttpRequestException` filtered by `StatusCode`, etc. Distinct failure modes the caller might handle differently get distinct `ErrorCode` values. (Blueprint §6.5)

16. **Every failure path disposes partially-constructed resources.** `using` / `using var` when the method shape allows; explicit `Dispose` in each catch block when the resource must be returned on success. Nullable-tracking makes the pattern legible. (Blueprint §6.5)

17. **`CancellationToken.None` in compensation paths is a literal, never a local.** Operations that must not be cancelled mid-flight — WAL compensation, staging cleanup, DEK/KEK zeroization, post-upload verification, soft-delete purge, brain-mirror finalisation — observe cancellation before the critical section, then pass `CancellationToken.None` spelled out at every await site. Writing `var none = CancellationToken.None; await Foo(none);` defeats the readability goal and is a violation. (Blueprint §6.7)

18. **Allocation-conscious hot paths.** `WritePipeline`, `ReadPipeline`, `CryptoPipeline`, `CompressionService`, `UploadQueueService`, `RangeUploader`, `AuditService`, `SelfHealingService`, `FileTypeService`, and the brain hot-path readers use pooled buffers, `Span<T>`, `stackalloc`, and value-type DTOs. These rules do NOT apply to OAuth flows, schema operations, folder-tree navigation, setup paths, UI binding, or configuration reads — applying them there is over-engineering. (Blueprint §9.1, §9.9)

19. **Pipeline buffer ownership is explicit via `IMemoryOwner<byte>`.** Methods that produce a buffer return `IMemoryOwner<byte>`; the caller disposes the owner when done. Methods never return `Memory<byte>` views over pooled buffers — the caller would have no way to return them. (Blueprint §9.2)

20. **`stackalloc` never crosses an `await` boundary.** 12-byte nonces, 16-byte GCM tags, 32-byte digests, and 8-byte blob headers use `stackalloc` and are consumed synchronously. The compiler enforces; the rule is worth understanding. (Blueprint §9.4)

21. **`RecyclableMemoryStream` replaces `new MemoryStream()` in Core.** A single `RecyclableMemoryStreamManager` is registered as a singleton. `new MemoryStream()` in a hot path is a defect. (Blueprint §9.6)

22. **Brain hot paths use raw `SqliteDataReader`; general queries use Dapper.** Upload-queue scanning, audit reads, self-healing scans, and provider-health aggregation use raw readers yielding `readonly record struct` rows. Folder listings, settings reads, and setup queries use Dapper. Dapper in a hot path is a Gate 2 rejection. (Blueprint §9.7)

23. **Provider contract is frozen before V1 ships.** `IStorageProvider`, `IProviderSetup`, `UploadSession`, `ProviderHealth`, and the `Result` family are additive-only in V1. New providers may be added; method signatures may not change. (Blueprint §10.5)

24. **No background failure is silent.** Every failure in `UploadQueueService`, `AuditService`, `SelfHealingService`, `UsbMonitorService`, `HealthMonitorService`, or the brain mirror task is (a) logged through `ILogger<T>`, (b) published to `INotificationBus`, and (c) if `Error` or `Critical`, persisted to `BackgroundFailures` so the next launch surfaces it to the user. (Blueprint §8.5, §8.6)

25. **Appliance vocabulary discipline.** Internal vocabulary — stripe, blob, WAL, OAuth, AAD, KEK, DEK, Argon2, SQLCipher, nonce — does not appear in any user-visible string (CLI output, GUI label, error message, notification title). The user's vocabulary is: skink, tail, recovery phrase, file, folder. (Blueprint §3, §25.4)

26. **Logging never contains secrets.** DEK, KEK, OAuth tokens, passwords, mnemonics, recovery phrases, encrypted blob bytes, and file content are never logged. `ErrorContext.Metadata` never contains keys matching `*Token`, `*Key`, `*Password`, `*Secret`, `*Mnemonic`, or `*Phrase`. CI lint enforces. (Blueprint §7.6)

27. **Core logs internally; callers log the Result.** The site that constructs `Result.Fail` logs once through `ILogger<T>`. Callers (ViewModels, CLI handlers) log the returned `ErrorContext` when handling it. The same event is never logged twice. (Blueprint §7.2)

28. **Core depends only on Microsoft.Extensions.Logging abstractions.** `FlashSkink.Core` and `FlashSkink.Presentation` reference `Microsoft.Extensions.Logging.Abstractions` only. Serilog is wired exclusively in the host projects (`FlashSkink.UI.Avalonia` and `FlashSkink.CLI`) in their `Program.cs`. Tests use the MEL in-memory or xUnit sink. (Blueprint §7.1)

29. **Atomic file-level writes on the skink.** Phase 1 commit writes to `.flashskink/staging/{BlobID}.tmp`, `fsync`s, atomically renames to the sharded destination path, `fsync`s the destination directory, then commits the brain transaction. Deviating from this sequence breaks the crash-consistency invariant. (Blueprint §13.4, §21.3)

30. **The crash-consistency invariant is preserved across every failure interleaving.** For every `Files` row, its `BlobID` either references an existing `Blobs` row with an existing on-disk blob, or is NULL. For every `Blobs` row, the on-disk blob exists. For every `UploadSessions` row, its `(FileID, ProviderID)` exists in `TailUploads` with `Status != UPLOADED`. For every unfinished `WAL` row, recovery restores the invariant. Property-based tests in `CrashConsistency/` verify across "crash at line N of operation X" interleavings. (Blueprint §21.3)

31. **Keys are zeroed on volume close.** DEK, KEK, brain key, password buffer, and decrypted OAuth tokens use `CryptographicOperations.ZeroMemory` on release. Session keys live only in process memory and never touch disk unencrypted. (Blueprint §18.6, §18.8)

32. **No telemetry, no update checks, no network chatter.** FlashSkink never phones home. Decisions B12-a and B13-a. Violating this is a product-positioning failure, not just a bug. (Blueprint §29.2)

---

## Conventions

### C# style

- Target framework: `net10.0` (with platform-specific variants where needed for native RIDs).
- `<Nullable>enable</Nullable>` everywhere. No `!` suppression without a comment explaining why.
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- `<ImplicitUsings>enable</ImplicitUsings>`.
- `readonly record struct` for hot-path DTOs and brain-reader rows.
- `sealed` by default; unseal explicitly when inheritance is intended.
- File-scoped namespaces.
- XML doc comments on all public types and members.

### Async

- `ValueTask` for hot paths (upload ranges, pipeline stages); `Task` for cold paths.
- Never `async void` (blocks structured exception flow; violates principle 14 indirectly).
- Every public async method takes `CancellationToken ct` as its final parameter (principle 13).

### Result pattern (summary — full detail in blueprint §6)

- `Result`, `Result<T>`, `ErrorContext`, `ErrorCode` live in `FlashSkink.Core.Abstractions.Results`.
- Public Core methods that can fail return `Result` or `Result<T>`. Programming errors (argument validation inside a private helper) may still throw; public boundaries may not.
- `ErrorContext` carries `Code`, `Message`, `ExceptionType`, `ExceptionMessage`, `StackTrace`, and optional `Metadata`. The raw `Exception` object never escapes Core.
- Catch-block ordering:
    1. `catch (OperationCanceledException ex) { ... return Result.Fail(ErrorCode.Cancelled, ..., ex); }` — always first.
    2. Specific expected exception types, narrow to broad, with `when` filters for sub-codes (`SqliteException.SqliteErrorCode`, `HttpRequestException.StatusCode`).
    3. `catch (Exception ex) { ... return Result.Fail(ErrorCode.Unknown, ..., ex); }` — always last, always present.
- Reference implementation: `BrainConnectionFactory.CreateAsync` in blueprint §6.8.

### Testing

- xUnit for unit tests. Project: `tests/FlashSkink.Tests`, with subfolders per area (`Engine/`, `Crypto/`, `Metadata/`, `Providers/`, `Upload/`, `Healing/`, `CrashConsistency/`, `Presentation/`).
- Moq for mocking.
- FsCheck for property-based tests (crash-consistency invariant verification).
- The `FileSystemProvider` implementation is the cloud-provider test double — deterministic, fast, no network.
- Every public API has at least one unit test.
- Test class names: `{ClassUnderTest}Tests`. Method names: `Method_State_ExpectedBehavior`.
- Single test project is the V1 decision; split into per-area test projects only when justified (separate TFM, separate runner invocation). See blueprint §4.1 note.

### Git

- Conventional commits: `feat:`, `fix:`, `docs:`, `test:`, `chore:`, `refactor:`.
- PRs merge to `main` via squash.
- PR title matches the commit subject.

### Secrets hygiene

FlashSkink uses BYOC — users supply their own OAuth app credentials. The codebase therefore contains no shared OAuth client secrets. But the **developer's test accounts** must stay out of the repo:

- Never commit OAuth client IDs, client secrets, refresh tokens, or test-account credentials — not even in commits that "will be rewritten later."
- Test OAuth credentials for Google Drive / Dropbox / OneDrive live in developer-local environment variables or a `.env` file that is `.gitignore`d by default.
- Code-signing material (Windows Authenticode PFX, Apple Developer ID certificate) lives in GitHub Secrets, never in files.
- `.env`, `.env.local`, `secrets.json`, `appsettings.*.json` with populated values, and anything under `**/test-credentials/` are `.gitignore`d.
- If you notice a secret being committed or about to be committed (pasted into a test file, logged into a sample config), stop and flag it immediately. Git history rewrites do not help against GitHub's internal caches; the only remediation is rotating the secret.

---

## CI and automation

The project is a public repo on GitHub Free, with no CI-minute constraint. The CI setup enforces the session protocol mechanically and uses Claude as a review layer on every PR.

### Workflows

Seven workflows in `.github/workflows/`. Each is described briefly here; full YAML is in the files. See `ONBOARDING.md` § "CI and automation reference" for the operational summary table.

**`ci.yml`** — the per-PR gate. Triggers on every PR and on push to `main`. Seven jobs:

1. `build-test` — matrix on `ubuntu-latest` + `windows-latest`. Restores, builds with `--warnaserror`, runs tests, uploads coverage to Codecov.
2. `assembly-layering` — asserts the reference rules of principles 8–11 via an xUnit test. Mechanical enforcement of the core/presentation/UI/CLI layering.
3. `plan-check` — for `pr/X.Y-*` branches, asserts `.claude/plans/pr-X.Y.md` exists, contains every required heading from the plan template, and references at least one `§` blueprint citation. Skips with success on `fix/`, `docs/`, `spike/` branches.
4. `principle-audit` — uses `anthropics/claude-code-action@v1` with Opus 4.7 to read the PR diff + plan file + CLAUDE.md and post a single comment with three sections: principle violations, plan-template compliance issues, scope-creep. Non-blocking — comments only.
5. `conventional-commits` — validates PR title against conventional commit types.
6. `codeql` — GitHub's CodeQL C# analysis.
7. `dotnet-format` — `dotnet format --verify-no-changes`.

All jobs except `principle-audit` are required status checks for merging to `main`.

**`pr-review.yml`** — general code review on PR open/ready. Opus 4.7 with `track_progress: true`. Prompt explicitly instructs Claude **not** to repeat principle-audit findings — focuses on logic bugs, missing tests, unclear naming, edge cases. Triggers on `[opened, ready_for_review, reopened]` — not `synchronize`, to bound cost during iteration. Non-blocking.

**`pr-review-crypto.yml`** — deep crypto-focused review, path-scoped to `src/FlashSkink.Core/Crypto/**` and the pipeline entry/exit files. Opus 4.7 with a prompt citing blueprint §18 (key hierarchy), §13.6 (blob format), and principles 26 & 31. Checks nonce reuse, AAD correctness, key zeroization, timing side channels, accidental secret-logging, `stackalloc` misuse. Fires rarely.

**`pr-review-recovery.yml`** — deep recovery/WAL review, path-scoped to `src/FlashSkink.Core/Metadata/Migrations/**`, `WalRepository.cs`, `Healing/**`, `Usb/**`, and `Orchestration/**`. Opus 4.7 with a prompt citing §21 (WAL recovery), §13.4 (atomic writes), principles 17, 29, 30. Checks idempotent recovery steps, fsync sequencing, transaction boundaries, and the `CancellationToken.None` literal pattern at every compensation-path await site. Fires rarely.

**`claude-mentions.yml`** — `@claude` trigger phrase in PR comments, review comments, issue bodies, or issue titles. Default tag mode, Opus 4.7. Has `contents: write` + `pull-requests: write` + `issues: write` permissions so Claude can commit fixes when asked. Use for: "explain this failure", "add a test for the cancellation path", "refactor this catch block to match principle 15".

**`nightly.yml`** — cron 04:00 UTC + `workflow_dispatch`. Three jobs: full 4-RID publish matrix (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`) with native-dependency load checks; `crash-consistency` property tests with 5000-iteration FsCheck budget (too slow for per-PR); `portable-publish-smoke` confirming zero host-side writes when running from a USB-simulated temp dir.

**`release.yml`** — triggered by tags matching `v*.*.*`. Publishes self-contained single-file binaries for all 4 RIDs, generates changelog from conventional commits via `git-cliff`, creates a **draft** GitHub release with the four zips attached. Signing is intentionally not wired yet — when V1 ships and certs are procured, signing steps slot in between build and release.

### Automation files

- `.github/dependabot.yml` — weekly NuGet + GH Actions updates, grouped by minor-and-patch, targeting `main`.
- `.github/pull_request_template.md` — prefills dev plan section, plan file link, principles touched (copied from the plan), acceptance criteria checklist, drift notes, and smoke-test notes.
- `.github/CODEOWNERS` — empty placeholder.

### Branch ruleset

Applied on `main` by Phase 0 via `gh api`. Name: `main protection`. Rules:

- Pull request required (0 approving reviews for solo dev, but stale-review dismissal enabled; review-thread resolution required)
- Squash-merge only (linear history)
- Block force pushes
- Block branch deletion
- Require status checks to pass: `build-test (ubuntu-latest)`, `build-test (windows-latest)`, `assembly-layering`, `plan-check`, `conventional-commits`, `codeql`, `dotnet-format`
- Strict status-check policy (require branch up to date before merging)

`principle-audit`, `pr-review.yml`, `pr-review-crypto.yml`, and `pr-review-recovery.yml` are **not** required — they comment but don't block. Converting any of them to required is a later decision based on signal-to-noise observed during V1 build.

### Secrets

- `ANTHROPIC_API_KEY` — used by `ci.yml` (principle-audit), `pr-review*.yml`, and `claude-mentions.yml`. Set via `gh secret set ANTHROPIC_API_KEY`. Must be present before Claude-powered jobs succeed.
- `GITHUB_TOKEN` — auto-provided by GitHub Actions. No manual setup.
- (Deferred to V1 release) Windows Authenticode PFX and Apple Developer ID certificate. Not needed before then.

### Cost discipline

API cost is bounded by trigger scoping:

- `pr-review.yml` runs on `opened`/`ready_for_review` only, not `synchronize` — at most ~2× per PR.
- `pr-review-crypto.yml` and `pr-review-recovery.yml` are path-scoped — fire only when the relevant folders change.
- `principle-audit` prompt is narrow (principles + plan + scope-creep only, not general code review) — token usage per run is small.

The high-volume flow (per-PR builds + tests + CodeQL + format) is free on public repos. Claude-powered jobs are the only line item.

### When to deviate from this CI scheme

Do not propose changes to the workflow set in a regular PR. CI changes ship as their own PRs under `pr/`, go through Gates 1 and 2 normally, and typically update both this section and the workflow files in the same PR. The session protocol's causality rule applies: automation changes ship alongside the rule they mechanise.

---

## Escalation rules

### When a plan seems wrong mid-implementation (Gate 2-ish)

Stop. Do not silently deviate. Three options to present:

1. Update the plan and re-approve (for small corrections).
2. Abandon the PR, return to Gate 1 with a revised plan (for fundamental disagreement).
3. Update the blueprint first if a principle is contradicting itself (rare).

### When tests are stuck (Step 6)

3-attempt cap. Diagnosis to `.claude/plans/pr-X.Y-stuck.md`. Hand back.

### When a blueprint section contradicts CLAUDE.md

Blueprint wins. Flag in your response:
> "Noticed a discrepancy — `CLAUDE.md` says X, blueprint §A.B says Y. Treating Y as authoritative for this task. Update `CLAUDE.md` in this PR?"

### When the user's short prompt is ambiguous

Ask one clarifying question before reading anything. Examples of ambiguity: section number doesn't exist, multiple possible interpretations, PR branch already exists with uncommitted work.

### What NOT to do

- Do not combine Gates 1 and 2 into one step ("here's the plan and here's the implementation, approve both").
- Do not self-approve after a rejection ("I've revised and proceeded").
- Do not add principles, conventions, or rules without the user's explicit approval — propose first.
- Do not touch files outside the plan's listed files without stopping and asking.
- Do not create new branches mid-session.
- Do not merge PRs. The user merges manually after review.

---

## `CLAUDE.md` update policy

This file grows slowly. New principles and conventions are added only when:

- A PR introduces a pattern worth preserving across sessions, or
- A Gate 2 or Gate 3 rejection reveals a rule that should have been here, or
- The blueprint changes in a way that affects session protocol.

Updates to `CLAUDE.md` ship in the same PR as the change they document. Not in a separate housekeeping PR. Causal link matters.

**Expected growth:** 500–700 lines by end of V1. Current: check with `wc -l CLAUDE.md`. If over 900 lines, something is wrong — likely duplicating blueprint content that should live there instead.

---

## Quick reference — where things live

| If you need… | Look here |
|---|---|
| Architectural decisions / rationale | `BLUEPRINT.md` |
| Repo setup and bootstrap | `ONBOARDING.md` |
| CI workflow details | `.github/workflows/` + `ONBOARDING.md` |
| Current PR's task plan | `.claude/plans/pr-X.Y.md` |
| Past PR's final API | `.claude/plans/pr-*.md` (committed) |
| Phase breakdown of work | `dev-plan/phase-N.md` |
| Principles | this file, §"Principles" |
| C# conventions | this file, §"Conventions" |
| Session protocol | this file, §"Session protocol" |
| Result pattern / cancellation | blueprint §6, `docs/error-handling.md` |
| Crash-consistency invariant | blueprint §21.3 |
| Provider interface contract | blueprint §10 |
| Resumable upload lifecycle | blueprint §15 |
| Brain schema | blueprint §16.2 |
| Blob format | blueprint §13.6 |
| Key hierarchy / crypto | blueprint §18 |
| BYOC provider setup | blueprint §24, `docs/provider-setup.md` |
| Recovery procedures | blueprint §21.4, `docs/recovery.md` |
| Spike decisions | `docs/spike-findings.md` |

---

*End of standing orders. Proceed to the user's first message.*
