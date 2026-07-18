# wow-realmchat — Claude Code instructions

.NET WinForms self-updating launcher (net48 target, built with the
.NET 8 SDK) that runs the local Ollama chat model behind a WoW private
server's AI bot chatter, on an unattended non-technical user's PC.
Reliability of the update path and zero-support-call operation outrank
features: an unsigned-update regression on an unattended machine is the
worst possible bug here.

## Layout
- `src/RealmChat` — the app. `tools/Tests` — the test suite (runs
  against `tools/OllamaStub`). `tools/IconGen` — icon assets.
  `VERSION` drives releases.

## Invariants
- Release artifacts are SIGNED and verified against a pinned key before
  install. Any change near the update path keeps verification intact
  and adds explicit failure-case tests (bad signature, partial
  download, version regression).
- The app self-reports its version + install location prominently —
  it's the first remote diagnostic; keep it working.

## Definition of done (in order — mirrors ci.yml)
1. `dotnet build src/RealmChat -c Release -o out`
2. Build the stub, then build and run the tests with
   `REALMCHAT_TEST_STUB` and `REALMCHAT_TEST_FIXTURES` set (exact
   steps in `.github/workflows/ci.yml`):
   `dotnet build tools/Tests -c Release -o out-tests` →
   `./out-tests/RealmChat.Tests.exe`
3. UI changes: run the app and screenshot it.
4. PR body states "touches update flow: yes/no" — mandatory.

## Release model
- The `VERSION` file drives releases; the release workflow resolves the
  next free `vX.Y.Z` tag itself. Bump VERSION only when the task is an
  actual release.
