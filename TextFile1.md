# Plan — Get GitHub Actions usage below 2,000 min/month (solo dev)

## Context

If FlashSkink switches from public to private, GitHub Actions stops being free and the repo will be billed against the GitHub Free private-repo allowance: **2,000 minutes/month** + 500 MB artifact storage + 10 GB cache.

Last 30 days of actual usage (from `gh api`):

| Workflow | Runs | Minutes/run (rounded up per job) | Subtotal |
|---|---:|---|---:|
| CI (`ci.yml`) | 128 | ~14 Linux + 2 Windows | 1,792 Linux + 256 Windows |
| Nightly (`nightly.yml`) | 18 | 3 Linux + 3 Windows + 3 macOS | 54 + 54 + 54 |
| PR Review (`pr-review.yml`) | 50 | ~4 Linux | 200 Linux |
| PR Review — Crypto | 9 | ~2 Linux | 18 Linux |
| PR Review — Recovery/WAL | 3 | ~3 Linux | 9 Linux |
| Dependabot | 14 | ~3 Linux | 42 Linux |
| Claude Mentions | 49 | 0 (all skipped at workflow level) | 0 |
| **Totals** | | | **~2,115 Linux + 310 Windows + 54 macOS** |

**Two readings of the cap:**
- **No multiplier (literal current docs):** 2,479 raw min/month → ~25% over.
- **With historical multipliers (Linux 1×, Windows 2×, macOS 10×):** 3,275 equiv-min/month → ~65% over.

The plan targets the worse reading (multipliers apply) so it's robust either way. Goal: **comfortably under 2,000 min/month with ~15% headroom for variability** (target ~1,700 min, leaving slack for end-of-month review pushes).

**Cheaper alternative the user should weigh first:** GitHub Pro at $4/month bumps the cap to 3,000 min, which fits today's usage under the no-multiplier reading and is close under the multiplier reading. If the user picks Pro, none of this plan is needed; the plan only matters if the user wants to stay on Free.

**Assumptions (please flag if wrong before approving):**
- Skipping principle-audit / Windows build / dotnet-format / codeql on PR `synchronize` events is acceptable. They still run on `opened`, `ready_for_review`, `reopened`, and post-merge `push: main`.
- Nightly cadence can drop from daily to weekly, and macOS RIDs can drop from Nightly (Release still validates all 4 on tag push).

---

## Recommended approach — four tiers, ship as separate `pr/` branches

Per CLAUDE.md, CI changes ship as their own PRs through Gates 1/2. Each tier below is one PR. They are independent; ship in order, measure after each, stop once under target.

### Tier 0 — Concurrency cancellation (zero signal loss)

**Files:** `.github/workflows/ci.yml`, `pr-review.yml`, `pr-review-crypto.yml`, `pr-review-recovery.yml`.

Add to each:

```yaml
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}
```

**What it does:** when you push a fix-up commit to a PR while CI is still running on the previous commit, the older run is cancelled. Don't cancel on `push: main` (that signal must complete).

**Estimated savings:** highly variable, but the run dump shows clusters of 5-10 PR runs within minutes (e.g., 2026-05-02 morning had ~10 CI runs in 5 minutes). Realistic estimate: 10-15% reduction in effective CI minutes.

**Risk:** none for this workflow set. Cancelled runs leave no stale state since each run is hermetic.

---

### Tier 1 — Skip heavy jobs on `synchronize` (largest single lever)

**File:** `.github/workflows/ci.yml`.

Add a job-level `if:` to `principle-audit`, `build-test (windows-latest)` (via matrix `exclude` or job-level `if`), `dotnet-format`, and `codeql`:

```yaml
if: github.event_name != 'pull_request' || github.event.action != 'synchronize'
```

The Linux `build-test`, `assembly-layering`, `plan-check`, `changes`, and `conventional-commits` jobs continue to run on every push to a PR, so the green/red signal is preserved on `synchronize`.

**Estimated savings (per month):**
- principle-audit: ~64 synchronize events × 5 min = **320 Linux min** saved.
- Windows build-test: 64 × 2 min = **128 Windows min** saved (256 equiv with 2× multiplier).
- dotnet-format: 64 × 1 min = **64 Linux min** saved.
- codeql: 64 × 3 min = **192 Linux min** saved.
- **Total: ~700 Linux + 128 Windows raw min** → ~830 raw min, ~960 equiv-min.

**Risk:** a bug introduced in a fix-up commit that is *principle-violating*, *Windows-only*, *formatting-related*, or *flagged by CodeQL* slips past automated CI until the next `opened` / `ready_for_review` / `reopened` event, or until merge to main. Mitigations:
- These categories are uncommon in fix-up commits (which are typically typo fixes or test tweaks).
- The post-merge `push: main` run still catches everything before any tag is cut.
- The user can manually re-trigger via `gh pr ready` / `gh pr ready --undo` to fire `ready_for_review` and run the full suite if a fix-up touches risky areas.

---

### Tier 2 — Nightly: weekly cadence + drop macOS RIDs

**File:** `.github/workflows/nightly.yml`.

- Change cron from `0 4 * * *` (daily) to `0 4 * * 0` (Sunday 04:00 UTC).
- Reduce `full-matrix` to `[win-x64, linux-x64]` only. Drop `osx-x64` and `osx-arm64`.

**Estimated savings (per month):**
- Daily → weekly: 18 - 4 = 14 fewer runs. At 9 raw min/run: **126 raw min** (or 546 equiv-min with multipliers).
- Drop macOS from remaining 4 weekly runs: 4 × 3 = **12 raw macOS min** (120 equiv-min).
- **Total: ~138 raw min, ~666 equiv-min.**

**Risk:** macOS-specific regressions caught later. Mitigations:
- `release.yml` still publishes all 4 RIDs on tag push, so any release-track build catches macOS issues before shipping.
- The codebase is principle-12 OS-agnostic — macOS-specific bugs are rare by design.
- The user can `workflow_dispatch` Nightly manually before any milestone or release.

---

### Tier 3 — PR Review tightening (only if Tier 0-2 are insufficient)

**File:** `.github/workflows/pr-review.yml`.

- Drop `track_progress: "true"`. The progress comment costs turns and the final summary is what matters.
- Reduce `--max-turns` from 20 to 15 (matches crypto/recovery reviews, which already finish reliably).
- Optionally swap `--model claude-opus-4-6` → `claude-sonnet-4-6` (faster, cheaper API; shorter wall-clock).

**Estimated savings (per month):**
- track_progress drop: ~1 min/run × 50 runs = **50 Linux min**.
- Sonnet swap: ~30-40% wall-clock reduction on top of that, ~60-80 additional min.
- **Total: ~50-130 Linux min depending on whether the model is swapped.**

**Risk:** lower-quality review output. Sonnet is generally adequate for the focus areas in `pr-review.yml` (logic bugs, missing tests, naming, edge cases) but Opus catches more subtle issues. Recommendation: try Sonnet for one month; if signal-to-noise stays acceptable, keep it.

Also: if `pr-review.yml` continues to hit max-turns frequently (as PR #53 did), that's a separate failure mode — bumping turns wastes minutes; tightening the prompt or reducing the diff context is the real fix. Flag for follow-up but not part of this plan.

---

## Net savings projection

| Tier | Raw min saved | Equiv-min saved (with multipliers) |
|---|---:|---:|
| 0 — Concurrency | ~150 | ~200 |
| 1 — Skip on synchronize | ~830 | ~960 |
| 2 — Nightly weekly + no macOS | ~138 | ~666 |
| 3 — PR Review tightening (track_progress only) | ~50 | ~50 |
| **Total** | **~1,170** | **~1,876** |

**Projected monthly usage after all four tiers:**
- No-multiplier reading: 2,479 - 1,170 = **~1,309 min** ✅ comfortably under 2,000.
- Multiplier reading: 3,275 - 1,876 = **~1,399 equiv-min** ✅ comfortably under 2,000.

After Tiers 0-2 only (skipping Tier 3): **~1,361 raw / ~1,449 equiv-min** — still under, with comfortable headroom. Tier 3 is optional insurance.

---

## What's explicitly NOT being cut

These were considered and rejected as too risky for the savings:

- **principle-audit removed entirely.** It's the mechanical enforcement of CLAUDE.md's 32 principles — load-bearing. Skip-on-synchronize is the right knob, not removal.
- **PR Review (`pr-review.yml`) removed entirely.** It's the general code-quality net. Tightening (Tier 3) preserves the signal; removal kills it.
- **CodeQL disabled.** It's the only security scan in the pipeline. Skip-on-synchronize is the right knob.
- **Crypto/Recovery reviews touched.** They're already path-scoped, only ~12 runs/month combined, ~30 min/month total. Not worth optimizing.
- **`push: main` trigger removed from CI.** Discussed in conversation — the post-merge "is `main` actually green?" signal is worth ~15-20 runs/month, especially because workflow-file changes only get exercised on main with this trigger.

---

## Critical files (workflows only — no source changes)

- `.github/workflows/ci.yml` — Tier 0 (concurrency) + Tier 1 (skip-on-synchronize)
- `.github/workflows/pr-review.yml` — Tier 0 (concurrency) + Tier 3 (tightening)
- `.github/workflows/pr-review-crypto.yml` — Tier 0 (concurrency only)
- `.github/workflows/pr-review-recovery.yml` — Tier 0 (concurrency only)
- `.github/workflows/nightly.yml` — Tier 2 (cadence + matrix)

No source code changes. No CLAUDE.md or BLUEPRINT.md changes (this plan documents *operational* tuning, not architectural constraints).

---

## Verification

Per-tier verification, after each PR merges:

1. **Functional smoke** — open one throwaway PR per tier; confirm the expected jobs run (and the skipped ones don't appear in the checks list). For Tier 0, push two commits in quick succession and confirm the older run shows `cancelled` in `gh run list`.
2. **30-day measurement** — re-run the `gh api repos/blochb/FlashSkink/actions/runs` aggregation from this conversation (counts by workflow + per-run job durations). Compare actuals to the projection above.
3. **GitHub billing dashboard (after going private)** — verify the dashboard's monthly usage matches the projection within ±10%. The dashboard is the only ground truth on whether multipliers apply to the free quota.

Roll back individually if a tier turns out to cost more signal than projected: each tier is a separate workflow PR, so reverting is one-commit.

---

## Decision points the user needs to confirm at approval

1. Stay on GitHub Free or upgrade to Pro ($4/month → 3,000 min cap)? If Pro, this plan is unnecessary.
2. Skip-on-synchronize for the heavy CI jobs is acceptable (Tier 1 assumption).
3. Nightly weekly + no-macOS is acceptable (Tier 2 assumption).
4. Bundle all four tiers into one PR, or ship one tier per PR? Recommendation: **one tier per PR**, in order, measuring after each — this matches CLAUDE.md's "CI changes ship as their own PRs" rule and lets the user stop early if Tier 0-2 already gets under the cap.
