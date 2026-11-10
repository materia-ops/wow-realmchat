# CI/CD workflow

How to interact with this repo so the pipelines — and the self-updating
fleet behind them — keep working. Read [ARCHITECTURE.md](ARCHITECTURE.md)
first for what the release assets mean.

## The one thing to internalize

**Merging to main IS deploying.** Any push to main that touches `src/**`
(or `VERSION`, or `release.yml`) publishes a release, and every installed
client picks it up at the next daily check or on next app open —
unattended, with no human in the loop after the merge. The daily check is
skipped while the app is running (single instance), so a host that keeps
the chat open updates only when the app is next reopened. There is no staging
environment; the PR gate is the staging environment.

The corollary: `updaterVersion` is the git **tree hash of `src/RealmChat`**,
so the fleet updates exactly when something under that directory changes.
Tests (`tools/Tests`), the stub, and docs live outside it on purpose —
changing them never triggers a client update (and `tools/**`/`docs/**`
don't even run the release workflow).

## Normal change workflow

1. Branch from `main`, make the change, keep commits
   [Conventional](https://www.conventionalcommits.org/) (`type(scope):`).
2. Open a PR. `ci.yml` runs on `windows-latest`:
   - `dotnet build src/RealmChat` — same toolchain the release uses;
   - compiles `tools/OllamaStub` with the legacy Framework `csc`;
   - builds and **runs `tools/Tests`** (unit + signature fixtures + the
     full controller E2E against the stub). A red suite blocks the merge.
3. Merge. `release.yml` then, in order:
   - resolves the next tag from `VERSION` (auto-increments patch past any
     existing tag/release — two same-day merges can't collide);
   - builds the exe with `-p:UpdaterVersion=<tree hash>`;
   - **re-runs the whole test suite** (pushes to main can bypass PRs; the
     release must not);
   - writes `manifest.json`, hashes exe + manifest into `SHA256SUMS`, signs
     it with the release key — fetched from 1Password at that moment via
     `1password/load-secrets-action`, authenticated by the `OP_RELEASE_TOKEN`
     secret of the `ci-main` Environment — and **self-verifies the signature against the committed
     `src/RealmChat/release-key.pub`**: a key that fielded exes wouldn't
     trust fails the release here, loudly, instead of stranding the fleet;
   - publishes the release (exe, sums, sig, manifest);
   - bumps `VERSION` back via a **PR, not a direct commit** (the org ruleset
     blocks direct pushes to main): a separate `bump` job (on
     `ubuntu-latest`, after the Windows `release` job has published) opens
     `release-bump/<tag>` with a `materia-minion` GitHub App token — the
     App this doc otherwise calls "github-bot", see the table below — and
     auto-merges it (squash, subject `Release <tag> [skip ci]` so the merge
     doesn't re-trigger a release).
4. Done. Do not create releases or tags by hand; the workflow owns them.

### Running the tests locally

```
dotnet build src/RealmChat -c Release -o out          # any .NET 8+ SDK
csc /out:out\ollama-stub.exe tools\OllamaStub\ollama-stub.cs
dotnet build tools/Tests -c Release -o out-tests
out-tests\RealmChat.Tests.exe                          # needs Windows (net48)
```

Optional env: `REALMCHAT_TEST_STUB` / `REALMCHAT_TEST_FIXTURES` (defaults
match the paths above, run from the repo root).

## Rules that keep the pipeline honest

- **Don't touch `src/RealmChat/` casually.** Any byte changed there rolls
  the fleet. Comment-only cleanups still ship an update; batch them with
  real changes.
- **Never edit `VERSION` by hand** except to deliberately bump major/minor
  (the workflow only auto-increments patch). The bump-back PR
  (`release-bump/<tag>`) is the workflow's; leave it alone — it self-merges.
- **`manifest.json`, `SHA256SUMS`, `SHA256SUMS.sig`, `RealmChat.exe` are a
  compatibility contract.** Additive changes only — fielded exes must always
  be able to parse the current latest release.
- **The stub is the executable spec** of the Ollama API surface. If the app
  starts using a new endpoint or CLI verb, extend
  `tools/OllamaStub/ollama-stub.cs` in the same PR or the E2E fails.
- **The fixtures are signed bytes.** `tools/Tests/fixtures/` is protected by
  `.gitattributes` (`-text`); never let an editor or git setting normalize
  their line endings, and never regenerate them except during key rotation
  (below).
- **This repo is public and environment-blank.** No addresses, hostnames,
  subnets, or site names anywhere — code, docs, tests, commit messages.
  Site specifics belong only in the machine-local `config.json`.
- **Workflow edits**: `release.yml` is in its own `paths:` filter, so merging
  a change to it publishes a release even with no code change. Expected, but
  know it's coming. Keep third-party actions pinned by commit SHA.

## Renovate and the pinned Ollama version

Dependency PRs come from Renovate, configured in `.renovaterc.json5`. It
runs on home-ops' self-hosted Renovate workflow, not in this repo: this
repo has to be in that workflow's `RENOVATE_AUTODISCOVER_FILTER` and
app-token repository list, with the `materia-minion` App installed here.

**Commit types don't drive versioning here.** Unlike the release-please
repos in the fleet, a release is cut by *which paths* a merge touches (top
of this doc), never by `fix` vs `chore`. The types Renovate uses only keep
the history conventional:

| update | PR title | what merging it does |
|---|---|---|
| pinned Ollama (`OllamaVersion` in `src/RealmChat/Constants.cs`, tracked by a regex manager against `ollama/ollama` GitHub releases) | `fix(deps): update pinned ollama to vX.Y.Z` | **cuts a release and rolls the fleet** — never automerged |
| NuGet / GitHub Actions | `chore(deps): …` | build/CI-only by intent, but the path rule still applies: a bump to `release.yml` or to `src/RealmChat/RealmChat.csproj` publishes a release (and the csproj one changes `updaterVersion`, so clients update); `ci.yml` and `tools/Tests` bumps don't |

The Ollama pin is the repo's most frequent change, and it is the one update
that does **not** finish by itself:

1. Skim the [Ollama release notes](https://github.com/ollama/ollama/releases)
   for model / GPU regressions, then merge the PR by hand.
2. `release.yml` publishes a new RealmChat; the host PC self-updates at
   the next daily check, or on next app open if the app was running. Only the app changed — the Ollama install
   on the PC is still the previous version.
3. The health check wants the *exact* pinned version, so the health card now
   shows the Ollama row as a warning ("version X installed"). Health runs
   when the window opens, not in the daily silent task, so nothing toasts:
   the warning is seen the next time someone opens Realm Chat.
4. One **Fix problems** click downloads the pinned Ollama installer (about
   1 GB) and, after a single admin prompt, installs it silently. That click
   needs a person at the PC.

Until step 4 the chat still starts and runs on the previously installed
Ollama, so a pending bump is a warning, not an outage — and several bumps
can pile up behind one click, since **Fix problems** always installs the
currently pinned version.

## The signing key (and the other release authorities)

| where | what |
|---|---|
| 1Password | the only home of the RSA-3072 private key PEM. The release job loads it at signing time (`1password/load-secrets-action`; the env var is still named `SIGNING_KEY`) — it is **not** a GitHub repo secret. No key, no releases the fleet will accept |
| `ci-main` Environment secret `OP_RELEASE_TOKEN` | 1Password service-account token that authorizes that load — the only signing-related credential GitHub holds |
| `ci-main` Environment secret `OP_CI_SERVICE_ACCOUNT_TOKEN` | 1Password service-account token the `bump` job uses to load the github-bot App credentials |
| `ci-main` GitHub Environment | deployable from `main` only; the `release` and `bump` jobs declare it, so no PR or branch run can read either 1Password token |
| `materia-minion` GitHub App, a.k.a. "github-bot" | one App, two names: `materia-minion` is the App itself (what org settings and PR authorship show, and the "minion app token" in `release.yml`'s comment); `github-bot` is the 1Password item holding its credentials (`op://ci/github-bot/…`), which is where the secret names and this doc's "github-bot" come from. It mints the short-lived token that opens and auto-merges the `release-bump/<tag>` VERSION PR (the org disables PRs from `GITHUB_TOKEN` and the ruleset blocks direct pushes) |
| `src/RealmChat/release-key.pub` | committed public key; CI's pre-publish self-verify |
| `src/RealmChat/ReleaseKey.cs` | the same key's modulus, pinned in the exe; clients verify with it (RSA PKCS#1 v1.5, SHA-256) |

Failure modes: missing/expired `OP_RELEASE_TOKEN` or a wrong 1Password item
path → the key-load step fails the release (fleet unaffected, keeps last
version). The loaded key is a *valid but different* key → the self-verify
step fails the release (same safe outcome). github-bot auth failure only
breaks the bump PR — the release is already published by then, `VERSION`
just goes stale (see troubleshooting). The fleet can only be affected by a
release signed with the real key.

### Key rotation (two-phase — order is everything)

Fielded exes verify with their pinned key until they self-update, and that
self-update is itself gated by the old key. Therefore:

1. Generate the new keypair offline. **Don't** touch the 1Password item CI
   signs from yet.
2. Ship an exe that trusts the new key: update `ReleaseKey.cs` +
   `release-key.pub`, **re-sign the test fixtures with the new private
   key**, PR, merge. This release is still signed by the OLD key — every
   fielded exe accepts it.
   ```
   openssl dgst -sha256 -sign new-key.pem \
     -out tools/Tests/fixtures/SHA256SUMS.sig tools/Tests/fixtures/SHA256SUMS
   ```
3. Wait for the fleet to update (check the version under the window title,
   or the `--- silent update check (<version>) ---` lines in
   `realmchat.log`, on the host PC(s) — or just give it >24 h).
4. Only now put the new private key in the 1Password item CI signs from.
   The next release is signed by it; the (updated) fleet accepts it.
   Refresh the key fingerprint in the materia-ops/.github profile README in
   the same change window (the in-repo `release-key.pub` copy was already
   updated in step 2).

Swapping steps 2 and 4 bricks self-update on every fielded exe: they'd
refuse the new signature forever and need a manual reinstall. If the private
key is ever **compromised**, that manual reinstall IS the recovery path —
rotate the key in 1Password and revoke `OP_RELEASE_TOKEN` immediately
(halting the attacker's ability to publish
acceptable releases requires revoking their repo access too), ship a
new-key exe, and hand-install it on each host PC.

### Verifying a release by hand

```
gh release download --repo <this repo> -p 'RealmChat.exe' -p 'SHA256SUMS*' -p 'manifest.json'
sha256sum -c SHA256SUMS
openssl dgst -sha256 -verify src/RealmChat/release-key.pub \
  -signature SHA256SUMS.sig SHA256SUMS
```

## Troubleshooting the pipeline

| symptom | cause / fix |
|---|---|
| release run failed at "Load signing key" | `OP_RELEASE_TOKEN` secret missing/expired/revoked, or the workflow's `op://` item path no longer matches the 1Password item — fix the token or the item |
| release run failed at "Manifest + signed checksums" | the loaded key isn't a valid PEM — check the 1Password item's contents |
| release run failed at the openssl verify | the 1Password key doesn't match the committed pubkey — you're mid-rotation out of order; restore the old key or finish phase 1 first |
| release published but the `bump` job failed | `OP_CI_SERVICE_ACCOUNT_TOKEN` or the github-bot App credentials/auth broke, or the bump PR wouldn't merge — the release itself is fine, but `VERSION` is stale until it lands; fix the auth and re-run the failed job (the tag-collision loop keeps stale-`VERSION` releases safe meanwhile) |
| release published but clients don't update | did `src/RealmChat/` actually change? Test/doc/tool-only merges don't bump `updaterVersion` (by design) |
| clients toast "updater needs attention" | 3+ failed checks — usually the release is missing an asset (all four are required) or was created by hand |
| CI green locally, red on the runner | line endings on fixtures (check `.gitattributes` survived), or the stub wasn't rebuilt after an API-surface change |
| two merges raced, second release has a higher patch than `VERSION` | normal — the tag-collision loop did its job; `VERSION` catches up via the bump-back PR |
