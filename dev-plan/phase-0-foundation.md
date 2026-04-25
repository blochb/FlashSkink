# Phase 0 — Foundation

**Status marker:** This phase is the bootstrap. Execution instructions live in `ONBOARDING.md` §"Phase 0 execution instructions" and are run as a single Claude Code session against an empty repository. Unlike every later phase, Phase 0 does NOT follow the Gate 1/Gate 2 session protocol — the protocol machinery is what Phase 0 creates.

**This file exists for two reasons:**
1. Post-hoc reference — what Phase 0 delivered, for you (and for future Claude Code sessions) to audit.
2. Template example — the shape that Phase 1+ files will take.

---

## Goal

After Phase 0:

- The repository is scaffolded with all source projects and a test project.
- `dotnet build` and `dotnet test` both succeed.
- CI enforces the session protocol mechanically via 7 workflows.
- A branch ruleset protects `main`: PR required, squash-only, linear history, required status checks.
- Dependabot watches for NuGet and Actions updates.
- Every document that later phases depend on exists.
- The first real PR (1.1) can start by typing `read section 1.1 of the dev plan and perform`.

---

## Section index

| Section | Title | Deliverables |
|---|---|---|
| §0.1 | Solution scaffold | `FlashSkink.sln`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, `.gitignore`, `LICENSE` |
| §0.2 | Project skeletons | 5 `src/` projects + `tests/FlashSkink.Tests`, all compiling, 1 placeholder test passing |
| §0.3 | Documentation root | `docs/` skeleton, `.claude/plans/`, `.claude/commands/` folders |
| §0.4 | CI workflows | 7 workflow YAMLs in `.github/workflows/` |
| §0.5 | Repository automation | `dependabot.yml`, `pull_request_template.md`, `CODEOWNERS`, issue templates |
| §0.6 | GitHub configuration | Branch ruleset applied, custom labels created, Dependabot verified |
| §0.7 | Initial commit and push | Single bootstrap commit on `main` |

Section-level detail — files to create, acceptance criteria, exact content — lives in `ONBOARDING.md` §"Phase 0 execution instructions". Duplication is deliberately avoided: that file is what Claude Code reads to execute; this file is your later reference.

---

## What Phase 0 does NOT do

- Does not write any real `FlashSkink.Core` code. The six projects contain only placeholder classes so the solution builds.
- Does not set up code signing. Certificates are procured closer to V1 ship; the `release.yml` workflow is designed so signing slots in without restructure.
- Does not configure `nightly.yml` to actually exercise `CrashConsistency/` — that folder is populated in Phase 4+.
- Does not write documentation beyond the placeholders in `docs/`. Real content lands as the corresponding features land.
- Does not install Visual Studio, Avalonia templates, or any host-side tooling. Phase 0 is fully inside the repository.

---

## Acceptance — Phase 0 is complete when

- [ ] All files listed in §0.1 through §0.5 exist and are committed.
- [ ] `dotnet build` succeeds with zero warnings on the bootstrap commit.
- [ ] `dotnet test` reports 1 passing test (the smoke test).
- [ ] GitHub Actions shows the initial commit triggered `ci.yml`, and all non-Claude-dependent jobs passed. (Claude-dependent jobs fail pending the `ANTHROPIC_API_KEY` secret; this is expected.)
- [ ] `gh api /repos/:owner/:repo/rulesets` returns a ruleset named "main protection" with enforcement "active".
- [ ] `gh label list` shows 9 custom labels.
- [ ] Direct `git push origin main` from the user's machine is rejected by the ruleset.

The user completes the Post-Phase-0 manual steps (secret, GitHub App, verification) after which Phase 1 can begin.

---

## Principles established by Phase 0

Phase 0 is the first application of several principles from `CLAUDE.md`. Later phases inherit the infrastructure Phase 0 built:

- **Principle 8** (Core holds no UI framework reference) — enforced by `assembly-layering` job in `ci.yml`.
- **Principle 9** (Presentation holds no UI framework reference) — same.
- **Principle 10** (UI project consumes Core through Presentation) — same.
- **Principle 11** (CLI has no Presentation reference) — same.
- **Session protocol** (Gates 1 and 2) — enforced by `plan-check` job in `ci.yml` plus the branch ruleset.
- **Conventional commits** — enforced by `conventional-commits` job.
- **Code formatting** — enforced by `dotnet-format` job.

---

## Post-Phase-0 hand-off

After Phase 0 the user performs manual steps 6–9 in `ONBOARDING.md` (secret, Claude GitHub App, ruleset verification, Actions verification). Phase 1 begins with the prompt `read section 1.1 of the dev plan and perform`, which triggers the full session protocol in `CLAUDE.md`.

---

*Phase 0 is a single bootstrap session with no sub-PRs. Every phase thereafter decomposes into PR-sized sub-sections §X.1, §X.2, ... each flowing through Gates 1 and 2 of the session protocol.*
