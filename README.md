# FlashSkink

**Portable, nomadic backup on a USB stick, with encrypted cloud replicas that catch up in the background.**

> ⚠️ **FlashSkink is under active development. V1 has not shipped. This repository is public for transparency; do not use FlashSkink with data you cannot afford to lose.**

---

## What is it?

FlashSkink distributes complete encrypted replicas of your data across a local USB flash drive (*the skink*) and one or more cloud storage providers (*the tails*). The skink is the body you carry; each tail is a full, independently recoverable copy that grows behind it.

The product's single guarantee:

> **Any single surviving part — the USB skink, or any one cloud tail — plus your 24-word recovery phrase, regenerates everything.**

Lose the USB? Any tail regenerates the volume. Cloud account suspended? The skink or another tail regenerates it. Provider shuts down? Other tails are untouched. You need exactly one surviving part. Nothing more.

The whole application runs directly from the USB — nothing is installed on the host machine, no host state is written, no traces remain after unplugging.

For the architecture and design rationale, see [`BLUEPRINT.md`](BLUEPRINT.md).

---

## Current state

| Phase | Status |
|---|---|
| Phase 0 — Foundation (scaffolding, CI, docs) | 🚧 In progress |
| Phase 1 — Crypto and brain | Not started |
| Phase 2 — Write pipeline and Phase 1 commit | Not started |
| Phase 3 — Upload queue and resumable uploads | Not started |
| Phase 4 — Providers (FileSystem, Google Drive, Dropbox, OneDrive) | Not started |
| Phase 5 — Recovery, healing, verification | Not started |
| Phase 6 — GUI, CLI, setup automation | Not started |
| V1 release | Not started |

This table is updated as phases land.

---

## For users

Not installable yet. There are no releases.

When V1 ships, portable self-contained binaries will be published for Windows, macOS (Intel and Apple Silicon), and Linux on the [Releases page](../../releases). No installer. Copy the executable to a USB drive and run.

---

## For contributors

Currently a solo development effort assisted by Claude Code. Contributions will be welcomed once V1 alpha ships.

If you're reading the code anyway:

- [`BLUEPRINT.md`](BLUEPRINT.md) — architecture and decision records
- [`CLAUDE.md`](CLAUDE.md) — development protocol and principles
- [`ONBOARDING.md`](ONBOARDING.md) — repo setup and automation
- [`dev-plan/`](dev-plan/) — phase-by-phase work breakdown

---

## Project structure

```
FlashSkink/
├── src/                        # production code
│   ├── FlashSkink.Core.Abstractions/
│   ├── FlashSkink.Core/
│   ├── FlashSkink.Presentation/
│   ├── FlashSkink.UI.Avalonia/
│   └── FlashSkink.CLI/
├── tests/                      # all tests
├── dev-plan/                   # phase-by-phase work breakdown
├── docs/                       # supplemental docs
├── .claude/plans/              # per-PR task plans
├── .github/workflows/          # CI and automation
├── BLUEPRINT.md                # architecture source of truth
├── CLAUDE.md                   # development protocol
├── ONBOARDING.md               # repo + CI setup
└── README.md                   # this file
```

Top-level structure is explained in [`BLUEPRINT.md` §4.1](BLUEPRINT.md) and the file-layout section of [`CLAUDE.md`](CLAUDE.md).

---

## License

[MIT](LICENSE) — © the FlashSkink authors.
