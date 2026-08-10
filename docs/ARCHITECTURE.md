# Architecture

What RealmChat is, how its pieces fit, and how data moves through it.
Companion doc: [CI-CD-WORKFLOW.md](CI-CD-WORKFLOW.md) for how changes reach
the fleet.

Deliberately **not** covered, because the app deliberately doesn't have them:

- **Database** — the only persistent state is one machine-local JSON file
  (`config.json`, schema below) and a plain-text log. Ollama owns its own
  model store (content-addressed blobs with refcounting, which is why models
  are only ever removed via `ollama rm`, never by deleting files).
- **Caching** — nothing is cached by the app. The one residency concern,
  keeping the model on the GPU between bot messages, is delegated to Ollama
  via the pinned `OLLAMA_KEEP_ALIVE` value.
- **Telemetry / remote logging** — a single attended desktop app; the log
  file and tray toasts are the observability surface.

## 1. System architecture

Three parties, two trust boundaries:

```
┌────────────────────────────────────────────┐
│ GitHub: this repo (public)                 │
│   release assets: RealmChat.exe,           │
│   manifest.json, SHA256SUMS, SHA256SUMS.sig│
└──────────────┬─────────────────────────────┘
               │ HTTPS, tokenless releases/latest/download/
               │ TRUST BOUNDARY 1: crossed only after
               │ SHA256SUMS.sig verifies against the RSA key
               │ pinned inside the exe (ReleaseKey.cs)
               ▼
┌────────────────────────────────────────────┐
│ Host PC (Windows)                          │
│   RealmChat.exe (tray app)                 │
│     ├─ owns: ollama serve (child process)  │
│     ├─ repairs: install, env, firewall     │
│     └─ self-updates via a Scheduled Task   │
│   Ollama :11434, bound 0.0.0.0             │
└──────────────┬─────────────────────────────┘
               │ HTTP :11434
               │ TRUST BOUNDARY 2: a Windows Firewall rule
               │ scoped to exactly [local LAN subnet(s) +
               │ the configured game-server subnet(s)]
               ▼
┌────────────────────────────────────────────┐
│ Game server (consumer)                     │
│   mod-ollama-chat → POST /api/generate     │
│   (falls back to canned chatter when the   │
│    endpoint is unreachable — an Ollama     │
│    outage degrades, never breaks, the game)│
└────────────────────────────────────────────┘
```

Design invariants:

- **Environment-blank binary.** Nothing site-specific ships in the repo or
  the exe. The PC's own LAN subnet is derived from its adapters at runtime;
  the game-server subnet and an optional DNS name are entered once at first
  run and live only in the machine-local config.
- **Pinned everything.** Ollama version, model tag (quantization included),
  and keep-alive are constants (`Constants.cs`). Changing them is a code
  change that rolls out via self-update — the host PC never needs hands-on
  maintenance, and the fleet can't drift.
- **Graceful consumer degradation.** The app never needs to be "highly
  available": the game server treats an unreachable model as "use canned
  chatter", so every failure mode here is a quality reduction, not an
  outage.
- **One instance.** A named mutex serializes every mode (GUI, silent,
  post-update); elevated fix runs are the only exception because the parent
  GUI holds the mutex while waiting for them.

## 2. Component structure

`src/RealmChat/` — one class per concern, no external dependencies beyond
the .NET Framework:

| component | role |
|---|---|
| `Program.cs` | entry point + mode dispatch (`--silent`, `--resume`, `--fix`, `--configure`, `--postupdate`), single-instance mutex, self-install to `%LOCALAPPDATA%\RealmChat`, the auto-resume decision |
| `Constants.cs` | the pinned knobs (Ollama version, model tag, keep-alive, default port, firewall rule identity). The only tuning surface that ships in the binary |
| `AppConfig.cs` | `config.json` load/save; property names ARE the JSON schema (see §4) |
| `OllamaController.cs` | owns the `ollama serve` child process and everything HTTP about it: health, model presence, warm-up, pull, rm. No WinForms dependency — this is the E2E-testable core |
| `HealthCheck.cs` | the unprivileged checks (pinned version installed, machine env vars, firewall state, DNS name) + `ElevatedFix`, the single elevated self-invocation that repairs the admin-fixable ones (install/env/firewall — a DNS mismatch is a server-side record, not fixable from this PC) |
| `SelfUpdater.cs` | the update engine: verified fetch (`Fetch()`), download, hash check, rename-aside swap with rollback |
| `ReleaseKey.cs` | the pinned RSA-3072 release public key and signature verification |
| `ScheduledTask.cs` | registers the logon + daily `--silent` task via `schtasks /XML`; `StartMenu` shortcut |
| `Cleanup.cs` | reclaimable-disk scan/delete: old model stores, unused models (via `ollama rm`) |
| `SubnetHelper.cs` | derives the machine's LAN CIDR(s) from its adapters; CIDR validation |
| `MainForm.cs` / `SetupForm.cs` / `Ui/` | the WinForms shell: one Start/Stop card, health card, activity log, tray icon, first-run wizard, theming |
| `Logger.cs` / `Toast.cs` / `AppIcons.cs` | log file, tray balloons, generated icons |

`tools/` — build/test support, deliberately outside `src/` so changes here
never bump `updaterVersion` (see CI doc):

| tool | role |
|---|---|
| `OllamaStub/` | a fake `ollama.exe` (CLI + HTTP API subset) the E2E tests run against |
| `Tests/` | the zero-dependency test runner; compiles the app sources in |
| `IconGen/` | generates `app.ico` (no external art assets) |

## 3. Data flow

### Start chat (the one big button)

```
GUI ─▶ OllamaController.Start()
         spawn: ollama serve  with env OLLAMA_HOST=0.0.0.0:<port>,
                OLLAMA_KEEP_ALIVE, OLLAMA_MODELS   (env travels WITH the
                child — no dependency on machine-scope vars being seen)
     ─▶ WaitUntilUp: poll GET /api/version
     ─▶ ModelPresent? (GET /api/tags)  ──no──▶ ollama pull <pinned model>
     ─▶ Warm: POST /api/generate (1-word prompt) → model resident on GPU
     ─▶ state Ready; firewall re-check scheduled ~10 s after start
```

While running: a 5 s reconcile poll (adopts/notices external servers), a
per-minute firewall watch (the Ollama installer/Windows rewriting rules is
the #1 silent-breakage source), and a `Process.Exited` handler for crash
detection. Stop kills the process tree and frees the GPU.

### Self-update (Scheduled Task `--silent`, and every GUI open)

```
GET SHA256SUMS + SHA256SUMS.sig
  └─ ReleaseKey.Verify() against the pinned key   ── invalid ──▶ abort
GET manifest.json ── hash must match its entry in the verified sums
manifest.updaterVersion == my version?  ──yes──▶ done ("up to date")
GET RealmChat.exe ── hash must match the verified sums
rename installed exe aside ─▶ move new exe in ─▶ relaunch
  └─ any failure: move the old exe back (rollback)
```

Nothing downstream of `Fetch()` ever sees unverified bytes. Dev builds
(version `dev`) skip only the signature step, loudly, so local testing works
without the private key.

### Auto-resume (opt-in)

`config.json` tracks `chat_was_running` (set on Ready, cleared on deliberate
Stop/Exit, kept on crash). The `--silent` logon run relaunches the GUI with
`--resume` — straight into the tray, chat started — only when the setting is
on, the flag is set, and the machine booted <15 min ago, so the daily-noon
check can never start the chat by itself.

### Elevation (Fix problems)

The GUI never elevates itself. It re-invokes its own exe elevated with
`--fix <a,b,c>` (one UAC prompt for all broken items); the worker applies
install/env/firewall repairs and exits, the parent refreshes the health card.

## 4. External interfaces (API contracts)

### Consumed: Ollama HTTP API + CLI

| call | used for |
|---|---|
| `GET /api/version` | liveness + wait-until-up |
| `GET /api/tags` | is the pinned model downloaded; model list + sizes for Cleanup |
| `GET /api/ps` | is the model loaded on the GPU |
| `POST /api/generate` (`stream:false`) | warm-up |
| `ollama pull <model>` (CLI) | model download with progress lines |
| `ollama rm <model>` (CLI) | model removal (blob refcounting stays correct) |

`tools/OllamaStub` implements exactly this surface; anything the app starts
depending on must be added there or the E2E tests fail — the stub is the
contract's executable spec.

### Published: the release-asset contract

Every release carries four assets, fetched tokenlessly from
`releases/latest/download/`:

| asset | contract |
|---|---|
| `manifest.json` | `{"tag": "vX.Y.Z", "updaterVersion": "<12-hex git tree hash of src/RealmChat>"}` — the update trigger is `updaterVersion != running version`, not the tag |
| `SHA256SUMS` | `sha256sum` format, covers `RealmChat.exe` and `manifest.json` |
| `SHA256SUMS.sig` | RSA-SHA256 (PKCS#1 v1.5) signature over `SHA256SUMS` by the release key |
| `RealmChat.exe` | the app, `InformationalVersion` stamped with the same tree hash |

Compatibility rule: fielded exes must always be able to parse the CURRENT
latest release — additive changes only (extra sums lines and extra assets
are ignored by old clients; renaming/removing any of the four is a breaking
change to the fleet).

### `config.json` (machine-local state, `%LOCALAPPDATA%\RealmChat`)

Property names are the schema (snake_case on purpose). Site config:
`server_subnets`, `dns_name`, `models_dir`, `theme`, `auto_resume`,
`disable_firewall_watch`. Dev/test overrides: `releases_repo`, `base_url`,
`ollama_exe`, `port`. App-managed state: `setup_done`, `tray_hint_shown`,
`chat_was_running`, and the update bookkeeping (`consecutive_failures`,
`last_*`). `old_models_dirs` remembers previous model stores so Cleanup can
offer the space back. Unknown fields are ignored on load — add fields, never
repurpose them.
