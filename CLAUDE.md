# wow-realmchat — Claude Code instructions

.NET WinForms self-updating launcher (net48 target, built with the
.NET 8 SDK) that runs the local Ollama chat model behind a WoW private
server's AI bot chatter, on an unattended remote machine.
Reliability of the update path and zero-support-call operation outrank
features: an unsigned-update regression on an unattended machine is the
worst possible bug here.

## Layout
- `src/RealmChat` — the app. `tools/Tests` — the test suite (runs
  against `tools/OllamaStub`). `tools/IconGen` — icon assets.
  `VERSION` — workflow-owned release base (see Release model).

## Invariants
- Release artifacts are SIGNED and verified against a pinned key before
  install. Any change near the update path keeps verification intact
  and adds explicit failure-case tests (bad signature, tampered
  manifest, hash mismatch / truncated download). There is no "version
  regression" case: `updaterVersion` is a tree hash compared by
  equality only, on purpose.
- The app self-reports its version prominently — in the subtitle under
  the window title, and in the `--- silent update check (<version>) ---`
  log line of every scheduled check. It's the first remote diagnostic;
  keep it working. (The install location is not shown in the UI; it is
  always `%LOCALAPPDATA%\RealmChat`, or `REALMCHAT_HOME` when set.)

## Definition of done (in order — mirrors ci.yml)
1. `dotnet build src/RealmChat -c Release -o out`
2. Build the stub, then build and run the tests with
   `REALMCHAT_TEST_STUB` and `REALMCHAT_TEST_FIXTURES` set (exact
   steps in `.github/workflows/ci.yml`):
   `dotnet build tools/Tests -c Release -o out-tests` →
   `./out-tests/RealmChat.Tests.exe`
3. UI changes: run the app, check it visually, and attach a screenshot
   to the PR.
4. PR body states "touches update flow: yes/no" — mandatory.

## Release model
- Merging to main IS deploying. Any merge touching `src/**`, `VERSION`
  or `.github/workflows/release.yml` publishes a signed release —
  whatever the commit prefix; this repo is not on release-please — and
  a change under `src/RealmChat` is then installed by every fielded
  client, unattended: at the next daily check, or on next app open if
  the app was running (a running instance skips the scheduled check).
- The release workflow resolves the next free `vX.Y.Z` tag from
  `VERSION` and bumps `VERSION` back itself via an auto-merged
  `release-bump/<tag>` PR. Never edit `VERSION` or create tags/releases
  by hand; the only manual `VERSION` change is a deliberate major/minor
  bump.
- Renovate's pinned-Ollama PRs edit `src/RealmChat/Constants.cs`, so
  merging one is a fleet release too, and the host PC then needs one
  **Fix problems** click. Details: `docs/CI-CD-WORKFLOW.md`.

## Work docs

Code-describing docs (architecture, CI, troubleshooting) live in this
repo. Work-describing docs — decisions, plans, handovers, reviews,
prompts, anything cross-repo — live in the private
`materia-ops/materia-vault` Obsidian vault; its root CLAUDE.md holds
the note contract.
