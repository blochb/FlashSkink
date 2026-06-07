# Proposal: Cloud-provider OAuth onboarding model (BYOC secret vs. shipped public client ID vs. native FS)

**Status:** Proposed — not accepted. `BLUEPRINT.md` is unchanged. This document captures a decision for review; adopting any option is a separate, explicitly-approved change.

**Scope:** OSS Core. Affects how a *user* (not just a developer running live tests) authorizes a cloud tail in `skink setup add`. Touches Blueprint §24 (BYOC setup), §3 (zero-trust host), and the "no shared credentials" stance.

**Origin:** Surfaced during Phase 4.5 live verification. Google Drive and Dropbox BYOC app creation proved low-friction; **OneDrive did not**. Microsoft has deprecated registering an app "outside a directory," so a user whose only OneDrive is a **personal/consumer** account can no longer create a BYOC app without first obtaining an Entra directory — which today means either signing up for Azure (**credit card** required for identity verification) or the Microsoft 365 Developer Program (**eligibility-gated**; sandboxes are no longer granted to most accounts). This was hit first-hand and is reproducible.

---

## Problem

FlashSkink's BYOC model asks every user to register their own OAuth app in the provider's developer console and supply a **client ID + client secret**. For Google Drive and Dropbox this is a few minutes of clicking. For OneDrive it is now a hard wall for the consumer segment:

- **Personal OneDrive user (outlook.com / an MSA):** no Entra directory → cannot register an app → must get a directory via Azure (card) or the Dev Program (disqualified for most). Effectively blocked, or pushed into a credit-card signup, *just to back up to their own OneDrive*.
- **Work/school OneDrive user:** has a directory, but app registration and third-party consent are frequently disabled by tenant admin policy.

So a meaningful fraction of "I have OneDrive" users cannot complete onboarding under the current model. The question: **should FlashSkink keep BYOC-with-secret, ship a public client ID, or integrate the native cloud file system?**

### Constraints the answer must respect (the non-negotiables)
- **P7 — zero trust in the host; no install; no host state; leave no trace.** FlashSkink is a nomadic USB appliance run on arbitrary, untrusted, ephemeral hosts.
- **P12 — OS-agnostic; identical behaviour on Windows, macOS, Linux.** Platform-branching the *core data path* needs strong justification.
- **P6 — zero-knowledge at every external boundary.** All data (and the brain mirror) leaves encrypted.
- **P4/P5 — sharp commit boundaries; resumable upload session state lives on the skink** so uploads survive disconnect and resume on a different host.
- **§"Secrets hygiene"** — no shared client *secrets* in the repo. (Note: a client **ID** is a public identifier, not a secret — see Option B.)
- **B12/B13 — no telemetry, no phone-home.**

---

## Option A — Status quo: BYOC confidential client (client ID + secret)

Each user registers their own app and supplies ID + secret; `OneDriveSetup`/CLI require both; the token exchange is the confidential auth-code + PKCE flow.

- **Pros:** Maximum isolation — each user is their own app to the provider, so throttling, abuse-detection, app suspension, and rate limits are per-user. No shared dependency the FlashSkink developer must own or maintain. Cleanest expression of "zero shared anything."
- **Cons:** The onboarding wall above. Acute for OneDrive consumer accounts (directory + card), real-but-mild for work tenants (admin policy), low for Google/Dropbox. Also asks non-technical users to navigate a cloud developer console — the steepest part of setup.
- **Verdict:** Correct in spirit, but it makes OneDrive onboarding impossible for a large user segment. Not acceptable as the *only* path for OneDrive.

---

## Option B — Ship a public client ID (PKCE, no secret), with optional BYOC override  *(recommended)*

FlashSkink ships a provider **client ID** for a developer-owned app registration configured as a **public client** (native/desktop, PKCE, **no client secret**). The user runs `skink setup add --provider onedrive`, a browser opens, they sign in and consent — **no console, no app registration, no directory, no card.** Power users / enterprises may still pass `--client-id/--client-secret` to use their own (BYOC override).

```
skink setup add --provider onedrive
→ browser opens → sign in to your OneDrive → consent. Done.
```

- **Why it doesn't break secrets hygiene:** a client **ID** is a public identifier embedded in every OAuth redirect; it is not a credential. PKCE (RFC 7636) is precisely the mechanism that makes a *secretless* public client safe — the per-session code verifier, not a shared secret, authenticates the token exchange. No shared *secret* ships. Per-user refresh tokens and all data remain encrypted and per-user (P6 intact).
- **Precedent:** rclone and most OSS cloud tools ship a default client ID and *recommend* BYOC override for heavy users — exactly this hybrid.
- **Pros:** Eliminates the OneDrive wall (and lowers Google/Dropbox friction too); preserves nomadic / zero-install / host-independent / contract-uniform uploads (FlashSkink still drives the upload through `IStorageProvider`); keeps BYOC available for those who want isolation.
- **Cons / what BYOC was protecting against:**
  - **Shared throttling & abuse surface.** All default-ID users appear to the provider as one app, sharing its rate-limit quota; one user's abuse pattern could trigger app-wide throttling or review. *Mitigation:* BYOC override for heavy users; document it.
  - **Single point of failure.** The provider could rate-limit, flag, or disable the shared app ID. *Mitigation:* BYOC override is always available as a fallback; the FlashSkink app registration is owned/maintained by the project.
  - **Someone must own the registration.** A FlashSkink-controlled Entra/Google/Dropbox app must exist and be maintained (redirect URIs, verification, branding on the consent screen). For OneDrive specifically, the FlashSkink developer does the `127.0.0.1` loopback redirect registration **once**, instead of every user.
  - **Consent-screen identity.** Users consent to "FlashSkink" rather than their own app — a minor trust/branding consideration, arguably a *benefit* (recognizable).
- **Code/Blueprint implications if adopted (not done here):**
  - `OneDriveSetup` already supports secret-optional PKCE (public client). The CLI `setup add` currently *requires* `--client-secret` for all cloud providers — this would relax to "secret optional when a shipped/public client ID is used."
  - A shipped default client ID per provider would be embedded (config/constant), with `--client-id` override.
  - Blueprint §24 would gain a "default app vs. BYOC" subsection; the "no shared credentials" stance would be refined to "no shared *secrets*; a shared public client **ID** is permitted."
  - The live-test harness/README stay BYOC (developers supply their own) — unaffected.
- **Verdict:** Best balance. Removes the friction for the common case while preserving BYOC's isolation for those who need it.

---

## Option C — Native cloud file system / sync-folder integration (`#if` Win/macOS, fallback Linux)  *(rejected for the core)*

Write encrypted blobs into the vendor's local sync folder (or via Windows Cloud Filter / macOS File Provider) and let the installed desktop client upload; Linux falls back to the API.

- **Rejected as a core mechanism**, because it conflicts with the non-negotiables:
  - **Requires the vendor client installed and signed into the user's account on this host** — defeats the nomadic, zero-install premise (P7). FlashSkink runs from a USB on arbitrary hosts that won't have your client.
  - **Writes host state and leaves traces** — the client caches/indexes/logs the blobs under the user profile (P7 / §26.4 violation), even though the bytes are encrypted.
  - **Cedes the commit boundary** — handing bytes to the OS sync engine means FlashSkink can no longer observe progress via `UploadSessions`, resume on another host, or run its own integrity/health verification through the contract (P4/P5 violation).
  - **The heavyweight native APIs require installation** (sync-root registration, signed app extensions/entitlements) — disqualifying for a zero-install appliance.
  - **Linux has no official OneDrive client**, so this can never be universal; the API adapter must be maintained regardless. The native path would be *net-additional* surface on 2 of 3 platforms, not a simplification — and it would `#if`-branch the core data path, inverting P12.
- **Where it is legitimate:** as a **user-managed convenience**, not a FlashSkink feature. A user *may* point an existing `FileSystemProvider` tail at their sync folder on a trusted desktop, and set that folder to online-only **in the vendor client's own settings**. That needs zero new code and stays inside the frozen contract. The moment FlashSkink *automates* the dehydration (placeholder/unpin APIs) it re-acquires the P7/P12 conflicts. Caveat: verification/heal/recovery reads against an online-only tail force re-hydration and require the client present — write-friendly, read-awkward.
- **Verdict:** No for the core data path; available today as an opt-in `FileSystemProvider` tail with no new architecture.

---

## Recommendation

Adopt **Option B (hybrid)**, prioritized for OneDrive:

1. **Ship a FlashSkink-owned public client ID for OneDrive** (PKCE, no secret), so consumer-OneDrive users onboard with consent only — no console, directory, or card. Keep **BYOC override** (`--client-id/--client-secret`) for power users and enterprises.
2. **Consider extending the shipped-default-ID model to Google Drive and Dropbox** for uniform low-friction onboarding, with BYOC override everywhere. (Lower urgency — their BYOC path is already easy; uniformity is the only driver.)
3. **Do not** pursue native cloud-FS integration as a core mechanism. Document the user-managed `FileSystemProvider`-at-sync-folder option as an existing convenience.

Refine the "no shared credentials" stance to **"no shared *secrets*; a shared public client *ID* is permitted, because PKCE (not a secret) authenticates the public-client flow."**

### Open questions for the decision
- Do we ship default IDs for **all three** providers, or **OneDrive-only** to start?
- Who owns/maintains the FlashSkink app registrations, and where is the consent-screen branding/verification handled (OSS Core vs. paid layer §24.1 automation)?
- Throttling policy: do we proactively recommend BYOC override above some usage, or only document it?
- Does shipping a default ID interact with **B12/B13 (no telemetry / no phone-home)**? (It should not — a default client ID is not a callback to FlashSkink; the OAuth traffic is user↔provider only. Worth stating explicitly.)

---

## Decision

*Pending.* Awaiting maintainer go-ahead before any change to `BLUEPRINT.md`, the CLI, `OneDriveSetup`, or the credential model.
