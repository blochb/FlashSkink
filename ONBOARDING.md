# ONBOARDING.md — FlashSkink dev setup and Phase 0 bootstrap

This file has three audiences and three roles:

1. **You, the dev, setting up the repo for the first time** → read "Pre-Phase-0" then run Phase 0, then "Post-Phase-0".
2. **Claude Code, during the Phase 0 bootstrap session** → reads the "Phase 0 execution" section and follows it as a self-contained instruction set.
3. **You, later, needing to remember how something was set up** → index at the end points to the relevant section.

**If you've already completed setup, you don't need this file for day-to-day work.** Day-to-day operation is driven by [`CLAUDE.md`](CLAUDE.md)'s session protocol.

---

## Index

- [Pre-Phase-0 instructions (you, by hand)](#pre-phase-0-instructions-you-by-hand)
- [Phase 0 execution (Claude Code, single bootstrap session)](#phase-0-execution-claude-code-single-bootstrap-session)
- [Post-Phase-0 instructions (you, by hand)](#post-phase-0-instructions-you-by-hand)
- [CI and automation reference](#ci-and-automation-reference)
- [Dealing with GitHub UI variations](#dealing-with-github-ui-variations)
- [Troubleshooting](#troubleshooting)

---

## Pre-Phase-0 instructions (you, by hand)

You'll do these **before** running Claude Code for the first time. Estimated time: 15 minutes.

### Step 1 — Create the empty repository

1. Open https://github.com in your browser and sign in.
2. At the top-right of the page, find the **`+`** icon next to your profile picture. Click it.
3. From the dropdown, click **"New repository"**.
   - *UI variation:* if there's no `+` icon, go directly to https://github.com/new
4. A form titled "Create a new repository" appears. Fill in:
   - **Repository name**: type `FlashSkink` exactly (case-sensitive for URLs)
   - **Description** (optional): "Portable nomadic backup system"
   - **Public / Private**: select **Public**
   - **Initialize this repository with**: leave **all three checkboxes unchecked** (README, .gitignore, license) — Claude Code creates these
5. At the bottom, click the green **"Create repository"** button.
6. You'll land on a "Quick setup" page showing commands. Keep this page open; note the repo URL `https://github.com/<your-username>/FlashSkink`.

### Step 2 — Install `gh` CLI

Pick the line for your operating system and run it in a terminal:

- **macOS** (with Homebrew): `brew install gh`
- **Windows** (with winget): `winget install --id GitHub.cli`
- **Linux** (Debian/Ubuntu): `sudo apt update && sudo apt install gh`
- **Other**: https://cli.github.com

Verify: `gh --version`

### Step 3 — Authenticate `gh`

In a terminal, run:

```
gh auth login
```

Prompts and answers:
1. *"What account do you want to log into?"* → **GitHub.com**
2. *"What is your preferred protocol for Git operations?"* → **HTTPS**
3. *"Authenticate Git with your GitHub credentials?"* → **Yes**
4. *"How would you like to authenticate GitHub CLI?"* → **Login with a web browser**
5. A code like `XXXX-XXXX` is shown. Press Enter. A browser opens.
6. In the browser, paste the code, click **Authorize**, sign in if prompted.
7. Return to the terminal. You should see `✓ Logged in as <your-username>`.

Verify: `gh auth status`

### Step 4 — Install Claude Code

If not already installed, follow https://docs.claude.com to install Claude Code for your platform.

Verify: `claude --version`

### Step 5 — Clone and seed the empty repo

In a terminal, navigate to where you keep your code projects, then:

```
gh repo clone <your-username>/FlashSkink
cd FlashSkink
```

The folder is empty. Now seed it with the four documents Claude Code will read during Phase 0:

1. Copy the four documents you reviewed in chat into the folder:
   - `README.md`
   - `BLUEPRINT.md`
   - `CLAUDE.md`
   - `ONBOARDING.md` (this file)
   - `dev-plan/phase-0-foundation.md` (in a `dev-plan/` subfolder you create)

   Exact procedure: whichever way you want — save-from-chat, manual download, drag-and-drop from a cloned copy. The four files at the repo root and the phase file under `dev-plan/` are what matters.

2. Commit and push as the first commit:

   ```
   git add -A
   git commit -m "docs: seed project documentation"
   git branch -M main
   git remote add origin https://github.com/<your-username>/FlashSkink.git
   git push -u origin main
   ```

   - *Note:* if `gh repo clone` already set the remote, the `git remote add` line will error — that's fine, skip it.

Verify by refreshing your repo page in the browser. You should see the five files listed.

### Step 6 — Start Claude Code for Phase 0

In a terminal inside the `FlashSkink` folder, run:

```
claude
```

When Claude Code starts, paste this prompt exactly:

> Read the `Phase 0 execution` section of `ONBOARDING.md` and execute every step in order. This is a bootstrap session — do not follow the session protocol in `CLAUDE.md`; this session creates the protocol. Commit directly to `main` at the end.

Phase 0 takes roughly 20–40 minutes of Claude Code work depending on your machine. Let it run to completion.

---

## Phase 0 execution (Claude Code, single bootstrap session)

**This section is the instruction set Claude Code reads during Phase 0.**

Read it top-to-bottom. Execute each sub-section in order. Do not stop for gates or approvals — this is a bootstrap session. Commit all work to `main` at the end.

### 0.1 — Solution scaffold

Create at repo root:

- `FlashSkink.sln` — Visual Studio solution file listing all `src/` and `tests/` projects.
- `Directory.Build.props` — applies to every project:
  - `<TargetFramework>net10.0</TargetFramework>` (or closest available; pick what `dotnet --version` supports)
  - `<Nullable>enable</Nullable>`
  - `<ImplicitUsings>enable</ImplicitUsings>`
  - `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
  - `<LangVersion>latest</LangVersion>`
  - `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`
  - `<AnalysisLevel>latest</AnalysisLevel>`
  - `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
- `Directory.Packages.props` — Central Package Management. All `PackageVersion` entries live here; individual projects only reference by name. Pre-seed with the packages listed in `BLUEPRINT.md` §28.
- `global.json` — pin the .NET SDK version. Use the minor-version pin (`rollForward: latestMinor`) so Phase 0 tolerates the specific dev-machine patch version.
- `.editorconfig` — standard .NET editorconfig with 4-space indent, CRLF for Windows-specific files, LF otherwise.
- `.gitignore` — standard Visual Studio / .NET template (bin, obj, .vs, .idea, user, suo, rider). Add `.env`, `.env.local`, `secrets.json`, `**/test-credentials/`, and `*.pfx`.
- `LICENSE` — MIT, copyright year 2026, "the FlashSkink authors".

### 0.2 — Project skeletons

Create six projects, each with a minimal placeholder class so `dotnet build` succeeds:

**`src/FlashSkink.Core.Abstractions/FlashSkink.Core.Abstractions.csproj`** — library.
- Placeholder: `namespace FlashSkink.Core.Abstractions; internal static class Placeholder { }`
- No project references. No NuGet references.

**`src/FlashSkink.Core/FlashSkink.Core.csproj`** — library.
- References: `FlashSkink.Core.Abstractions`.
- NuGet (central versions): `Microsoft.Extensions.Logging.Abstractions` only for now.
- Placeholder: `namespace FlashSkink.Core; internal static class Placeholder { }`

**`src/FlashSkink.Presentation/FlashSkink.Presentation.csproj`** — library.
- References: `FlashSkink.Core.Abstractions`, `FlashSkink.Core`.
- NuGet: `Microsoft.Extensions.Logging.Abstractions`, `CommunityToolkit.Mvvm`.
- Placeholder: `namespace FlashSkink.Presentation; internal static class Placeholder { }`

**`src/FlashSkink.UI.Avalonia/FlashSkink.UI.Avalonia.csproj`** — executable, `OutputType=Exe`.
- References: `FlashSkink.Presentation`.
- NuGet: Avalonia + `Serilog` + `Serilog.Extensions.Logging` + `Serilog.Sinks.File` + `Serilog.Formatting.Compact`.
- `Program.cs` with an empty `Main` that returns 0.

**`src/FlashSkink.CLI/FlashSkink.CLI.csproj`** — executable, `OutputType=Exe`.
- References: `FlashSkink.Core`.
- NuGet: `System.CommandLine` (beta), Serilog as above.
- `Program.cs` with an empty `Main` that returns 0.

**`tests/FlashSkink.Tests/FlashSkink.Tests.csproj`** — test project.
- References: all five src projects.
- NuGet: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Moq`, `FsCheck.Xunit`.
- One smoke-test file `SmokeTests.cs` with a single passing test `Smoke_Passes`.

Run `dotnet build` and `dotnet test` before proceeding. Both must succeed.

### 0.3 — Documentation layout

The four top-level docs (`README.md`, `BLUEPRINT.md`, `CLAUDE.md`, `ONBOARDING.md`) already exist from the seed commit. Do not modify them.

Create placeholder skeleton files (empty but present with title + "TBD" content):

- `docs/architecture.md`
- `docs/error-handling.md`
- `docs/provider-setup.md`
- `docs/recovery.md`
- `docs/spike-findings.md`

Create the plans directory with a `.gitkeep` file:

- `.claude/plans/.gitkeep`

### 0.4 — CI workflows

Create seven workflow files under `.github/workflows/`. The exact content matters — copy carefully from the specs below.

#### `ci.yml` — per-PR gate

Triggers: `pull_request: [opened, synchronize, reopened, ready_for_review]` and `push: branches: [main]`.

Jobs:

1. **`build-test`** — matrix on `ubuntu-latest` + `windows-latest`:
   - `actions/checkout@v4`
   - `actions/setup-dotnet@v4` with `global.json` version source
   - `dotnet restore`
   - `dotnet build --no-restore -c Release`
   - `dotnet test --no-build -c Release --logger "trx" --collect:"XPlat Code Coverage"`
   - Upload coverage to Codecov via `codecov/codecov-action@v4` (token not required for public repos)

2. **`assembly-layering`** — `ubuntu-latest`:
   - Same setup as build-test
   - Run the layering test in `tests/FlashSkink.Tests/ArchitectureTests.cs` (a file Phase 0 creates in this step) that verifies:
     - `FlashSkink.Core` references no Avalonia assembly
     - `FlashSkink.Presentation` references no Avalonia assembly
     - `FlashSkink.UI.Avalonia` references no `FlashSkink.Core` directly (only via Presentation)
     - `FlashSkink.CLI` references no `FlashSkink.Presentation`

3. **`plan-check`** — `ubuntu-latest`:
   - Parses the PR branch name. Matches regex `^pr/(\d+\.\d+)-`.
   - If match: asserts `.claude/plans/pr-<X.Y>.md` exists and contains headings: `## Scope`, `## Files to create`, `## Files to modify`, `## Public API surface`, `## Principles touched`, `## Test spec`, `## Acceptance criteria`, `## Line-of-code budget`, `## Non-goals`. Also asserts the plan references at least one `§` blueprint citation.
   - If no match (e.g. `fix/...` or `docs/...` branches): skip with success.
   - Runs only on `pull_request` trigger, not on `push`.

4. **`principle-audit`** — `ubuntu-latest`:
   - `actions/checkout@v4` with `fetch-depth: 0`
   - Uses `anthropics/claude-code-action@v1` with `anthropic_api_key: ${{ secrets.ANTHROPIC_API_KEY }}`
   - `claude_args: --model claude-opus-4-7 --max-turns 15`
   - Prompt instructs Claude to read the PR diff, `.claude/plans/pr-X.Y.md` (if present), and `CLAUDE.md`, then post a single PR comment with three labelled sections: (1) principle violations with file:line refs, (2) plan-template compliance issues, (3) scope-creep — files in diff not listed in plan's Files-to-create/modify.
   - Non-blocking (`continue-on-error: true` at the job level so it comments but doesn't fail the PR).

5. **`conventional-commits`** — `ubuntu-latest`:
   - Uses `amannn/action-semantic-pull-request@v5`
   - Allowed types: `feat`, `fix`, `docs`, `test`, `chore`, `refactor`, `perf`, `ci`.

6. **`codeql`** — `ubuntu-latest`:
   - Standard GitHub CodeQL workflow for C# from `github/codeql-action/init@v3` + `github/codeql-action/analyze@v3`.

7. **`dotnet-format`** — `ubuntu-latest`:
   - Same setup as build-test
   - `dotnet format --verify-no-changes --severity warn`

All jobs except `principle-audit` are blocking.

#### `pr-review.yml` — general auto-review

Triggers: `pull_request: [opened, ready_for_review, reopened]` (**not** `synchronize` — keeps cost bounded).

One job using `anthropics/claude-code-action@v1`:
- `anthropic_api_key: ${{ secrets.ANTHROPIC_API_KEY }}`
- `track_progress: true`
- `claude_args: --model claude-opus-4-7 --max-turns 20`
- Prompt: review the PR diff for logic bugs, missing tests, unclear naming, edge cases, error-handling gaps. **Explicitly do not repeat principle-audit findings.** Post one summary comment plus inline comments on specific lines. Permissions include `pull-requests: write`.

#### `pr-review-crypto.yml` — deep crypto review

Triggers: `pull_request: [opened, ready_for_review, reopened]` with `paths`:
```
- 'src/FlashSkink.Core/Crypto/**'
- 'src/FlashSkink.Core/Engine/WritePipeline.cs'
- 'src/FlashSkink.Core/Engine/ReadPipeline.cs'
- 'src/FlashSkink.Core.Abstractions/Models/*.cs'
```

One job using `anthropics/claude-code-action@v1` with Opus 4.7. Prompt references `BLUEPRINT.md` §18 (key hierarchy), §13.6 (blob format), and principles 26 & 31 from `CLAUDE.md`. Checks: nonce reuse, AAD correctness, key zeroization on all failure paths including unwinding, timing side channels, any accidental logging of secret material, `stackalloc` across `await` (compiler should reject but verify absence), AES-GCM tag handling, HKDF context strings.

#### `pr-review-recovery.yml` — deep recovery/WAL review

Triggers: `pull_request: [opened, ready_for_review, reopened]` with `paths`:
```
- 'src/FlashSkink.Core/Metadata/Migrations/**'
- 'src/FlashSkink.Core/Metadata/WalRepository.cs'
- 'src/FlashSkink.Core/Healing/**'
- 'src/FlashSkink.Core/Usb/**'
- 'src/FlashSkink.Core/Orchestration/**'
```

One job using `anthropics/claude-code-action@v1` with Opus 4.7. Prompt references `BLUEPRINT.md` §21 (WAL recovery), §13.4 (atomic writes), principle 17 (`CancellationToken.None` literal in compensation), principle 29 (atomic write protocol), principle 30 (crash-consistency invariant). Checks: idempotent recovery steps, fsync sequencing, transaction boundaries, the `CancellationToken.None` literal pattern at every await site inside compensation paths.

#### `claude-mentions.yml` — `@claude` Q&A and fix requests

Triggers: `issue_comment: [created]`, `pull_request_review_comment: [created]`, `pull_request_review: [submitted]`, `issues: [opened, assigned]`.

Gate with `if: contains(github.event.comment.body, '@claude') || contains(github.event.issue.body, '@claude') || contains(github.event.review.body, '@claude') || contains(github.event.issue.title, '@claude')` — adapt per event type.

One job using `anthropics/claude-code-action@v1` in default (tag) mode:
- `anthropic_api_key: ${{ secrets.ANTHROPIC_API_KEY }}`
- `track_progress: true`
- `claude_args: --model claude-opus-4-7 --max-turns 30`

Permissions: `contents: write`, `pull-requests: write`, `issues: write`, `id-token: write`.

#### `nightly.yml` — full 4-RID matrix + slow tests

Triggers: `schedule: cron '0 4 * * *'` (04:00 UTC daily), `workflow_dispatch`.

Jobs:

1. **`full-matrix`** — matrix across `win-x64` (runner: windows-latest), `linux-x64` (ubuntu-latest), `osx-x64` (macos-13), `osx-arm64` (macos-14):
   - Restore, build, test (as per `ci.yml`)
   - `dotnet publish src/FlashSkink.UI.Avalonia -c Release --self-contained -r <RID> -p:PublishSingleFile=true`
   - `dotnet publish src/FlashSkink.CLI -c Release --self-contained -r <RID> -p:PublishSingleFile=true`
   - Assert native deps load by running the published CLI with `--version` (which should print and exit 0).

2. **`crash-consistency`** — `ubuntu-latest`:
   - `dotnet test tests/FlashSkink.Tests --filter Category=CrashConsistency` with environment variable `FSCHECK_ITERATIONS=5000` (tests read this to scale iteration budget).

3. **`portable-publish-smoke`** — `ubuntu-latest`:
   - Published binary copied to `/tmp/usb-sim/`, executed from there, confirms no writes to `$HOME` or `/tmp` outside `/tmp/usb-sim/` by comparing `find` output before/after.

#### `release.yml` — tag-triggered release

Triggers: `push: tags: ['v*.*.*']`.

Jobs:

1. **`build-binaries`** — matrix across 4 RIDs:
   - Publish self-contained single-file binaries
   - Archive each as `FlashSkink-${{ github.ref_name }}-<RID>.zip`
   - Upload as workflow artifact

2. **`generate-changelog`** — `ubuntu-latest`:
   - Uses `orhun/git-cliff-action@v3` with conventional-commit config
   - Produces changelog section for this release

3. **`create-release`** — `ubuntu-latest`, needs both above:
   - Downloads all artifacts
   - Uses `softprops/action-gh-release@v2` with `draft: true` so human review happens before public.

### 0.5 — Repository automation files

#### `.github/dependabot.yml`

- `package-ecosystem: "nuget"`, directory `/`, schedule `weekly`, grouped by `minor-and-patch` update type (single PR per week unless there's a security update).
- `package-ecosystem: "github-actions"`, directory `/`, schedule `weekly`.
- Both targeting `main`, rebase strategy `auto`.

#### `.github/pull_request_template.md`

Sections (as markdown checkboxes / fill-ins):

- **Dev plan section:** `phase-N §X.Y`
- **Plan file:** `[.claude/plans/pr-X.Y.md](...)`
- **Principles touched:** list
- **Acceptance criteria:** checklist (copied from plan)
- **Drift notes:** usually empty
- **Manual smoke test notes:** for USB- or host-touching PRs only

#### `.github/CODEOWNERS`

Empty file with a single comment:

```
# CODEOWNERS — populated when contributors join beyond the solo dev.
```

### 0.6 — GitHub configuration via `gh`

Run each command. If any fails, capture the error and include it in the final commit's description but do NOT rollback — partial configuration is recoverable.

#### Apply branch ruleset on `main`

Create a JSON payload file `/tmp/ruleset.json` with the ruleset described below, then apply via:

```bash
gh api \
  --method POST \
  -H "Accept: application/vnd.github+json" \
  /repos/{owner}/{repo}/rulesets \
  --input /tmp/ruleset.json
```

Ruleset contents (summary — generate the actual JSON):

- **Name:** `main protection`
- **Target:** default branch (`~DEFAULT_BRANCH`)
- **Enforcement:** `active`
- **Rules:**
  - `deletion` (prevent deletion)
  - `non_fast_forward` (block force pushes)
  - `pull_request` with `required_approving_review_count: 0` (solo dev; review is by self) but `dismiss_stale_reviews_on_push: true`, `require_last_push_approval: false`, `required_review_thread_resolution: true`, `allowed_merge_methods: ["squash"]`
  - `required_linear_history`
  - `required_status_checks` with `strict_required_status_checks_policy: true` and contexts: `build-test (ubuntu-latest)`, `build-test (windows-latest)`, `assembly-layering`, `plan-check`, `conventional-commits`, `codeql`, `dotnet-format`

#### Create labels via `gh label create`

Create these labels (idempotent — `--force` replaces existing):

- `size/xs` (color `c2e0c6`)
- `size/s` (color `bfd4f2`)
- `size/m` (color `fef2c0`)
- `size/l` (color `fbca04`)
- `size/xl` (color `d93f0b`)
- `needs-security-review` (color `d93f0b`)
- `needs-perf-review` (color `fbca04`)
- `blocked` (color `b60205`)
- `spike` (color `5319e7`)
- `good-first-issue` (color `7057ff`)

#### Verify Dependabot accepted the config

```bash
gh api /repos/{owner}/{repo}/dependabot/alerts >/dev/null 2>&1 || true
```

(Don't fail the bootstrap if Dependabot isn't enabled yet — it activates automatically when the config lands.)

### 0.7 — Final commit and push

Single commit:

```bash
git add -A
git commit -m "chore: phase 0 foundation — scaffolding, CI, automation"
git push origin main
```

After push, verify:

```bash
gh run list --limit 5
```

The `ci.yml` run triggered by this push is expected to **fail** the `principle-audit` job because `ANTHROPIC_API_KEY` isn't set yet — that's expected and the user will set it in Post-Phase-0. The `build-test`, `assembly-layering`, `plan-check`, `conventional-commits`, `codeql`, and `dotnet-format` jobs must all pass.

If any of the six blocking checks fail, fix the underlying issue and commit a follow-up before ending the session. Do not leave `main` broken.

### 0.8 — Session summary

Write a summary to stdout (not a file — the bootstrap session ends here):

- Files created: count
- Tests passing: count
- CI workflow runs: list with status
- Known gaps: `principle-audit` pending `ANTHROPIC_API_KEY`
- Next action for user: "Run Post-Phase-0 steps in ONBOARDING.md"

End session.

---

## Post-Phase-0 instructions (you, by hand)

After Claude Code finishes Phase 0, do these three things. Estimated time: 10 minutes.

### Step 7 — Add the ANTHROPIC_API_KEY secret

Get a Claude API key from https://console.anthropic.com → **API Keys** → **Create Key**. Copy it (shown only once).

**Option A — via `gh` CLI (fastest, 1 command):**

In a terminal inside the `FlashSkink` folder:

```
gh secret set ANTHROPIC_API_KEY
```

When prompted `? Paste your secret`, paste the key and press Enter. Done.

**Option B — via GitHub web UI:**

1. Open `https://github.com/<your-username>/FlashSkink` in your browser.
2. Along the top of the repo page you'll see tabs: Code, Issues, Pull requests, Actions, Projects, Security, Insights, **Settings**. Click **Settings** (rightmost).
   - *UI variation:* if Settings isn't visible, click `...` or `⋯` at the end of the tab row.
3. In the left sidebar, find **"Security"** section. Under it, click **"Secrets and variables"**, then **"Actions"**.
   - *UI variation:* older UI nests this under a "Secrets" entry directly. Look for whichever leads to "Actions secrets".
4. On the Actions secrets page, click the green **"New repository secret"** button (top-right).
5. Form:
   - **Name**: `ANTHROPIC_API_KEY` (exactly, case-sensitive)
   - **Secret**: paste your API key
6. Click **"Add secret"**.

You should now see `ANTHROPIC_API_KEY` listed under "Repository secrets" with a timestamp.

### Step 8 — Install the Claude GitHub App

Connects the `@claude` mention action and review workflows to your repo.

1. In a terminal, run: `claude`
2. At the Claude Code prompt, type: `/install-github-app` and press Enter.
3. Follow prompts. A browser opens.
4. In the browser, on a page titled something like "Install Claude on GitHub":
   - Under **"Repository access"**, select **"Only select repositories"**.
   - From the dropdown, select **`<your-username>/FlashSkink`**.
   - Click **"Install"** (or **"Install & Authorize"**).
5. Browser redirects; you can close it.
6. Claude Code terminal confirms installation.

### Step 9 — Verify the branch ruleset is active

Phase 0 applied it via `gh`, but verify in the UI.

1. Go to `https://github.com/<your-username>/FlashSkink` → click **Settings**.
2. Left sidebar, under **"Code and automation"**, click **"Rules"**, then **"Rulesets"**.
   - *UI variation:* if "Rules" is collapsed or named differently, look for "Ruleset" anywhere in the sidebar. Older instances show "Branches" → "Branch protection rules" — Phase 0 uses rulesets, which are separate. If you only see branch protection rules, the ruleset may not have been created; check `gh api /repos/<owner>/<repo>/rulesets` in a terminal.
3. You should see a ruleset named **"main protection"** with status **Active**.
4. Click it to review — rules enabled should include: prevent deletion, require PR, require linear history, block force pushes, require status checks.

### Step 10 — Verify Actions are running

1. Go to the **Actions** tab (`https://github.com/<your-username>/FlashSkink/actions`).
2. You should see at least one workflow run from Phase 0's initial push.
3. If `principle-audit` failed and other jobs passed: re-run it. In the failed run's page, top-right, click **"Re-run jobs"** → **"Re-run failed jobs"**. It should succeed now that the secret is set.
4. If any of the six blocking jobs failed: open the run, click the failing job, read the error. Common early issues:
   - `dotnet-format` fails on the scaffolding → run `dotnet format` locally, commit, push.
   - `codeql` fails → investigate; CodeQL is usually clean on minimal scaffolding.
   - `build-test` fails on Windows but passes on Ubuntu → line endings or path separator issue; check `.editorconfig` and `.gitattributes`.

### Step 11 — First real session

You're now ready to start Phase 1.

Before you can, **one more piece is needed**: a Phase 1 dev-plan file at `dev-plan/phase-1-crypto-and-brain.md`. This is written in a normal chat session with Claude (not Claude Code) — "please draft `dev-plan/phase-1-crypto-and-brain.md` based on `BLUEPRINT.md`" — reviewed by you, and committed via a normal PR using the session protocol from `CLAUDE.md`.

Once Phase 1's plan file exists on `main`, subsequent sessions begin with:

```
read section 1.1 of the dev plan and perform
```

and the session protocol runs every PR from there on.

---

## CI and automation reference

Summary of what's in `.github/workflows/` and what each is for.

| Workflow | When it runs | Blocks merge? | Cost |
|---|---|---|---|
| `ci.yml` | Every PR | Yes (except principle-audit) | Free (public repo) + modest API tokens for audit |
| `pr-review.yml` | PR opened / ready-for-review | No | Opus 4.7, 1× per PR |
| `pr-review-crypto.yml` | PR touching crypto paths | No | Opus 4.7, rarely |
| `pr-review-recovery.yml` | PR touching recovery paths | No | Opus 4.7, rarely |
| `claude-mentions.yml` | `@claude` mention in PR/issue | N/A | Opus 4.7, on demand |
| `nightly.yml` | Cron 04:00 UTC + manual | No | Free (public repo) |
| `release.yml` | Tag push `v*.*.*` | N/A | Free |

API cost control:
- `pr-review.yml` skips `synchronize` (every commit push) — runs at most ~2× per PR regardless of how many commits.
- Deep reviewers are path-scoped; they don't fire on most PRs.
- `principle-audit` uses focused prompts, not broad code review.

Secrets required:
- `ANTHROPIC_API_KEY` — all Claude-powered workflows.
- `GITHUB_TOKEN` — auto-provided.
- (Future, V1 release) code-signing certs.

---

## Dealing with GitHub UI variations

GitHub renames and reorganises settings every few months. Rules when my instructions don't match your screen:

1. **"Settings" tab is always rightmost** in the repo's top tab row. If not visible, tabs are truncated — click `...` or resize your window.
2. **Secrets live under "Security"** (newer) or **directly under "Secrets"** (older). Look for anything mentioning "Actions secrets".
3. **Rulesets vs Branch protection rules**: both exist, sometimes both offered. Rulesets are newer and preferred; Phase 0 uses rulesets. If you only see "Branch protection rules" under "Branches", the ruleset might not have been created — check via `gh api`.
4. **Dropdown buttons sometimes split** into a main button + a small chevron for the dropdown. Re-read; a "click the dropdown next to X" instruction may mean the tiny arrow.
5. **Confirmation modals** appear centre-screen or slide-in-right. Read the title, not the position.
6. **When in doubt, tell whoever's helping (me, in chat) what you see.** Paste the exact label or describe the page. Don't click around guessing — GitHub rarely has undo.

---

## Troubleshooting

**`gh auth login` fails with "network error"** — corporate proxies block the device flow. Try `gh auth login --with-token` instead, generating a PAT from https://github.com/settings/tokens with `repo`, `workflow`, `admin:org` (latter only if org-level later).

**`gh secret set` returns "Resource not accessible by integration"** — your `gh` auth may lack workflow scope. Re-run `gh auth refresh -s workflow`.

**Phase 0's `gh api /repos/.../rulesets` fails with 404** — rulesets require either a non-free plan *or* a public repo. Confirm the repo is public. If private and on GitHub Free, the ruleset apply step is a no-op and you'll need to rely on branch protection rules instead; tell me and we'll adjust.

**`codeql` job never runs** — needs Code Scanning enabled. In **Settings** → **Code security and analysis**, enable **"CodeQL analysis"** manually. Free for public repos.

**`principle-audit` consistently posts false positives** — refine its prompt in `.github/workflows/ci.yml`. The prompt lives in the workflow file; edit, commit, the next PR uses the new prompt.

**Dependabot PRs flood your notifications** — adjust `.github/dependabot.yml`: increase `open-pull-requests-limit: 3` → `1`, or change schedule to `monthly`.

**Workflow using `anthropics/claude-code-action@v1` fails with "API key not set"** — either the secret isn't configured (Step 7), or the workflow is referencing the wrong secret name (must be exactly `ANTHROPIC_API_KEY`).

---

*This file is maintained by hand. Last updated: Phase 0.*
