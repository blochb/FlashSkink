## Scope

The `Build review context` step in `pr-review-crypto.yml` and `pr-review-recovery.yml` fails with exit 141 (SIGPIPE) whenever the PR diff exceeds the 200 KB cap. The pipeline is `git diff … | head -c 200000`; `head` closes stdin after the cap, `git diff` gets SIGPIPE, and `pipefail` + `set -e` (default for `bash` actions) propagates that as a step failure. Observed on [run 26468592406](https://github.com/blochb/FlashSkink/actions/runs/26468592406) for PR #91.

Fix both workflows by removing the pipe: write the full diff to a temp file with `git diff --output=…`, then truncate with `head -c 200000 < file`. No pipe between two processes means no SIGPIPE. Truncation still happens (the diff just gets capped to the same 200 KB), and a real `git diff` failure still surfaces because we keep its exit code on the only-process line.

## Files to create

None.

## Files to modify

- `.github/workflows/pr-review-crypto.yml` — replace the `git diff … | head -c 200000` line in the `Build review context` step.
- `.github/workflows/pr-review-recovery.yml` — same replacement in its `Build review context` step.

## Dependencies

None.

## Public API surface

None — workflow change only.

## Internal types

None.

## Method-body contracts

The replacement snippet (identical structure in both files):

```bash
echo "## Full diff"
echo '```diff'
git diff "origin/$BASE_REF...HEAD" --output=/tmp/review/diff.full
head -c 200000 /tmp/review/diff.full
echo '```'
```

Why this avoids the failure:
- `git diff --output=PATH` writes directly to a file; no pipe, no SIGPIPE.
- `head -c 200000 < FILE` reads from a regular file (no upstream process to kill); it returns 0 whether the file is shorter or longer than 200 KB.
- If `git diff` itself fails, `set -e` still catches it (the line is a single command, not a pipeline).

## Integration points

None beyond the two workflow files. The `claude-code-action` step downstream reads `/tmp/review/context.md` — unchanged interface.

## Principles touched

None of the C# principles apply (no production code). CI changes ship under their own PR per the "CI ship as own PRs" rule in CLAUDE.md § "When to deviate from this CI scheme".

## Test spec

No unit tests — workflow change. Verification is empirical:

- Push the branch and open a PR. The PR diff itself is small, so the `Build review context` step in `pr-review-crypto.yml`/`pr-review-recovery.yml` will not fire on this PR (path filters exclude `.github/workflows/**`). That's fine — the previous failing run already demonstrated the bug; this PR's value is the fix landing.
- Manual smoke test note for the PR body: next time a PR touches `src/FlashSkink.Core/Crypto/**` (e.g. an upcoming Phase 1 crypto PR) with a diff >200 KB, the step should complete successfully and the review comment should still appear.

## Acceptance criteria

- [ ] Both workflow files updated with the file-staging pattern
- [ ] YAML still valid (no syntax errors — `gh workflow view` or local parse)
- [ ] No behavioural change for diffs ≤200 KB (still truncated to ≤200 KB)
- [ ] No SIGPIPE failure for diffs >200 KB

## Line-of-code budget

- `.github/workflows/pr-review-crypto.yml` — 1 line changed, 1 line added (~+1 net)
- `.github/workflows/pr-review-recovery.yml` — 1 line changed, 1 line added (~+1 net)
- Total: ~4 lines diff

## Non-goals

- Do NOT raise or lower the 200 KB cap.
- Do NOT change the prompt, allowed tools, model, or trigger conditions for either review workflow.
- Do NOT touch `ci.yml`, `pr-review.yml`, `claude-mentions.yml`, `nightly.yml`, or `release.yml`.
- Do NOT update the `actions/checkout@v4` Node 20 deprecation warning — separate concern, separate PR.
- Do NOT add a fallback for `git diff` failure beyond what `set -e` already provides.
