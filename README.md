# WoW Realm Chat

A small Windows app (`RealmChat.exe`, C# WinForms, .NET Framework 4.8 — built
into Windows 10/11, nothing to install) that runs the local
[Ollama](https://ollama.com) chat model a WoW private server's AI bot chatter
talks to. One big **Start chat / Stop chat** button, a tray icon, and it keeps
everything on the machine healthy and up to date by itself.

## What it does

- **Start / stop the chat brain**: launches a pinned Ollama version with the
  right bind + keep-alive + model-store settings, waits for it to answer,
  downloads the pinned chat model if missing, and warms it onto the GPU.
  Stopping frees the GPU again. If Ollama dies on its own, the app notices
  and says so.
- **Tray-safe**: closing the window while the chat runs minimizes to the tray
  instead of silencing the bots; right-click the tray icon to stop or exit.
- **Health & repair**: on every open it verifies the Ollama install (exact
  pinned version), system settings, and the scoped firewall rule — one
  **Fix problems** click (a single admin prompt) repairs all of it, including
  installing Ollama itself. There is no separate setup script to run.
- **Firewall watch**: the Ollama app / Windows likes to add its own firewall
  rules for `ollama.exe`, which can silently block the game server's access to
  a perfectly healthy local model. While the chat runs, the app re-checks the
  firewall every minute (toggleable), warns with a toast the moment access
  breaks, and **Fix problems** deletes every foreign Ollama rule and keeps
  exactly the one scoped rule. That rule is bound to `ollama.exe` itself,
  which is what stops Windows' "allow access" popup from appearing on every
  chat start — the popup fires for any program that listens with no firewall
  rule naming its exe, and both popup buttons create exactly the foreign
  rules the watch then has to clean up.
- **Disk cleanup**: when leftovers exist — the old per-user model store, a
  previous model folder after moving it in Settings, or models the realm
  doesn't use — a **Clean up** button appears showing exactly what will be
  deleted and how much space returns. It always asks first, never runs
  automatically, and unused models are removed via `ollama rm` (never raw
  file deletion — blobs are shared and refcounted).
- **Self-updating**: a Scheduled Task checks daily and swaps in new versions
  silently (the update task never starts the chat — that stays a human
  decision, with one opt-in exception below). Pinned model/version changes
  ship as app updates, so the host PC never needs hands-on maintenance.
- **Auto-resume (opt-in)**: a checkbox in setup makes the chat come back by
  itself shortly after a reboot — but only when it was running before the
  machine went down. Windows Update restarting the PC overnight no longer
  silences the bots until someone notices. Deliberate stops stay stopped.

## Install (once)

1. Download **`RealmChat.exe`** from the [latest release](../../releases/latest).
2. Run it (SmartScreen may warn once: More info → Run anyway).
3. Enter the two values from your server admin in the one-time setup, then
   click **Fix problems** if anything shows a warning.

## Privacy of this repo

The published app is **environment-blank**: no addresses, hostnames, or
network details ship in this repository or the binary. The machine's own LAN
subnet is derived from its network adapter at runtime; the game server's
subnet and an optional DNS name are entered at first run and stored only in
the machine-local config (`%LOCALAPPDATA%\RealmChat\config.json`).

## How updates work

Every release carries `RealmChat.exe`, `manifest.json`
(`{tag, updaterVersion}`), `SHA256SUMS`, and `SHA256SUMS.sig`. Clients poll
`releases/latest/download/` (no API, no auth); `updaterVersion` is the git
tree hash of `src/RealmChat`, so clients self-update exactly when the source
changed.

Nothing is trusted unverified: `SHA256SUMS.sig` must be a valid RSA-SHA256
signature over `SHA256SUMS` by the release key pinned inside the exe
(`src/RealmChat/ReleaseKey.cs`, committed as `release-key.pub`), the manifest
must hash-match its entry in those sums, and so must the downloaded exe
before the running one is swapped (rename-aside + relaunch, with rollback).
CI signs with the release key, which lives only in 1Password and is injected
at release time via the `OP_RELEASE_TOKEN` repo secret, and refuses to publish
a release the committed public key wouldn't verify — so even a compromised repo or
Actions token cannot feed installed clients a tampered build. Key rotation
is two-phase: ship an exe trusting the new key first, sign with it only
after the fleet updated.

## Options

Everything the retired setup script exposed as editable constants, and where
it lives now:

| knob | where |
|---|---|
| allowed firewall subnets | Settings (server subnet field; the PC's own LAN subnet is auto-derived) |
| expected address check | Settings ("DNS name" — validated by lookup, no hardcoded IP) |
| auto-resume after reboot | Settings checkbox (default off; resumes only a chat that was running) |
| model storage folder | Settings (Browse; defaults to `C:\ProgramData\Ollama\models`) |
| pinned Ollama version / model / keep-alive | `src/RealmChat/Constants.cs` — deliberately maintainer-only: a merge rolls the change to the host PC via self-update |
| port | `config.json` `port` override only — deliberately hidden: the game server expects the default, changing it unilaterally silences the bots |

## Troubleshooting / FAQ

- **Windows warned me when I first ran it (SmartScreen).** Expected for a
  small unsigned-by-a-CA download: click **More info → Run anyway**. It only
  happens on a freshly downloaded exe — self-updates install silently. Release
  integrity comes from the signed checksums described above, and you can
  [verify a release by hand](docs/CI-CD-WORKFLOW.md#verifying-a-release-by-hand).
- **Where is the log?** `%LOCALAPPDATA%\RealmChat\realmchat.log` (next to
  `config.json`; under `REALMCHAT_HOME` instead if you set that). One line per
  event, self-trimming — the log plus tray toasts are the app's only
  observability, so it's the first thing to read or send when asking for help.
- **What version am I on?** Open the window: the version is shown right under
  the title. It's also written to the log on every daily update check.
- **The bots went silent.** In order:
  1. Is Realm Chat still running? Look for its tray icon (closing the window
     while the chat runs only minimizes to the tray). If it's gone, start the
     app and press **Start chat**.
  2. Open the window — did it stop the chat? If Ollama died on its own the
     app toasts "Ollama exited unexpectedly" and the big button is back to
     **Start chat**; press it.
  3. Any warnings in the health list (Ollama version, system settings,
     firewall, address)? Click **Fix problems**. A blocking firewall rule
     added by Windows or the Ollama app is the #1 silent killer — the app
     toasts "Realm chat firewall problem" when its minute-by-minute firewall
     watch catches one, and **Fix problems** removes it.
  4. Still silent? Check `realmchat.log` for the start/stop history and any
     `FAILED` lines.

## Development

- Build: `dotnet build src/RealmChat -c Release` (any .NET 8+ SDK; the net48
  reference assemblies restore via the project's `nuget.config`).
- Tests: `dotnet build tools/Tests -c Release -o out-tests`, compile the stub
  (`csc /out:out\ollama-stub.exe tools\OllamaStub\ollama-stub.cs`), then run
  `out-tests\RealmChat.Tests.exe` — unit tests plus a full controller E2E
  against the stub. CI runs the same suite on every PR and release.
- `REALMCHAT_HOME=<dir>` makes the exe fully portable (config/log/install under
  that dir; no Scheduled Task or Start Menu writes).
- `config.json` overrides for testing: `base_url` (any static server hosting
  the release assets), `ollama_exe` (e.g. the stub in
  `tools/OllamaStub`), `port`, `models_dir`.
- `--silent` is what the Scheduled Task runs; `--configure` re-runs setup.
- Deeper docs: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) (system/
  components/data flow/interfaces) and
  [`docs/CI-CD-WORKFLOW.md`](docs/CI-CD-WORKFLOW.md) — read the latter
  before your first merge: **merging to main is deploying**.
