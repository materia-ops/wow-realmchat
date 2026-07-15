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
  exactly the one scoped rule.
- **Self-updating**: a Scheduled Task checks daily and swaps in new versions
  silently (the update task never starts the chat — that stays a human
  decision). Pinned model/version changes ship as app updates, so the host PC
  never needs hands-on maintenance.

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
(`{tag, updaterVersion}`), and `SHA256SUMS`. Clients poll
`releases/latest/download/manifest.json` (no API, no auth); `updaterVersion`
is the git tree hash of `src/RealmChat`, so clients self-update exactly when
the source changed. Downloads are verified against `SHA256SUMS` before the
running exe is swapped (rename-aside + relaunch, with rollback).

## Options

Everything the retired setup script exposed as editable constants, and where
it lives now:

| knob | where |
|---|---|
| allowed firewall subnets | Settings (server subnet field; the PC's own LAN subnet is auto-derived) |
| expected address check | Settings ("DNS name" — validated by lookup, no hardcoded IP) |
| model storage folder | Settings (Browse; defaults to `C:\ProgramData\Ollama\models`) |
| pinned Ollama version / model / keep-alive | `src/RealmChat/Constants.cs` — deliberately maintainer-only: a merge rolls the change to the host PC via self-update |
| port | `config.json` `port` override only — deliberately hidden: the game server expects the default, changing it unilaterally silences the bots |

## Development

- Build: `dotnet build src/RealmChat -c Release` (any .NET 8+ SDK; the net48
  reference assemblies restore via the project's `nuget.config`).
- `REALMCHAT_HOME=<dir>` makes the exe fully portable (config/log/install under
  that dir; no Scheduled Task or Start Menu writes).
- `config.json` overrides for testing: `base_url` (any static server hosting
  the three release assets), `ollama_exe` (e.g. the stub in
  `tools/OllamaStub`), `port`, `models_dir`.
- `--silent` is what the Scheduled Task runs; `--configure` re-runs setup.
