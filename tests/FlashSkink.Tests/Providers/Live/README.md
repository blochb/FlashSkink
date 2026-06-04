# Live provider verification tests

These tests exercise the **real** cloud-provider APIs (Google Drive, Dropbox, OneDrive) end-to-end
with your own BYOC OAuth credentials. They are part of **Phase 4.5** and verify the trivial building
blocks of each provider: OAuth setup, upload, download, resumable upload, and the
exists/delete/list/health/quota surface.

> **They never run in CI and never gate a merge.** Each test self-skips at discovery time unless its
> credentials (and cached token) are present, so on CI — and on any machine without credentials —
> they report **Skipped**.

## Running them

```bash
# Run only the live suite
dotnet test --filter Category=LiveProvider
```

With no credentials set, every live test is reported **Skipped** (this is also what CI does). To run
them for real, set the credentials below and run the **setup** test first.

## Credentials

FlashSkink ships no shared OAuth app — you supply your own (the same BYOC model the product uses).
Create an OAuth app in the provider's developer console, then provide its client id/secret via
environment variables **or** a gitignored `.env` file at the repo root:

```
FLASHSKINK_LIVE_GDRIVE_CLIENTID=...
FLASHSKINK_LIVE_GDRIVE_CLIENTSECRET=...
FLASHSKINK_LIVE_DROPBOX_CLIENTID=...
FLASHSKINK_LIVE_DROPBOX_CLIENTSECRET=...
FLASHSKINK_LIVE_ONEDRIVE_CLIENTID=...
FLASHSKINK_LIVE_ONEDRIVE_CLIENTSECRET=...
```

Shell environment variables take precedence over `.env`. **Never commit credentials** — `.env` and
`.livetest-cache/` are gitignored.

## One-time browser consent + token cache

The `*_Setup_*` test opens your system browser for the real OAuth consent **once**, then caches the
DEK-encrypted refresh token (plus a throwaway test-only DEK) under `<repoRoot>/.livetest-cache/`.
Every other test loads that cache and runs **headless** (no browser). If the cache is missing, the
non-setup tests are reported **Skipped** with a message telling you to run the setup test first.

> The cache file is as sensitive as a plaintext refresh token (it holds the token and the key that
> decrypts it). It is gitignored and local-only — treat it like `.env`.

Run order for a provider (e.g. Google Drive):

```bash
# 1. one-time consent (opens a browser), caches the token
dotnet test --filter "FullyQualifiedName~GoogleDrive_Setup"

# 2. the rest run headless from the cache
dotnet test --filter "Category=LiveProvider&FullyQualifiedName~GoogleDrive"
```

## Cleanup

Every test writes under a unique `_livetest/{runId}/…` prefix and deletes only the objects it
created — so running concurrently (multiple terminals, shared account) never removes another run's
in-flight data. Stale leftovers from a crashed run are cleared by the **opt-in** purge test, which
deletes everything under `_livetest/`:

```bash
# Destructive — run only when no other live run is active against this account
FLASHSKINK_LIVE_PURGE=1 dotnet test --filter "FullyQualifiedName~PurgeAllLiveTestObjects"
```

## Parallelism

The three provider classes run in parallel by default (distinct accounts, quarantined prefixes). To
serialize them — e.g. if you are rate-limited or point more than one class at a single account — add
a shared `[Collection("LiveProvider")]` to each live class; in xUnit v2 a shared collection name
disables their mutual parallelism.
