# Contributing

Read [docs/CI-CD-WORKFLOW.md](docs/CI-CD-WORKFLOW.md) before your first
merge. The one thing to internalize: **merging to main IS deploying.**
Releases are automatic — any merge touching `src/**` (or `VERSION`, or
`release.yml`) publishes a signed release that every installed client picks
up unattended within 24 hours. There is no staging environment; the PR gate
is the staging environment.

## Rules

- **Commits are [Conventional](https://www.conventionalcommits.org/)**:
  `type(scope): summary`.
- **Every PR body must state `touches update flow: yes/no`** — mandatory,
  no exceptions. An unsigned-update regression on an unattended machine is
  the worst possible bug here, so changes near the update path must keep
  signature verification intact and add explicit failure-case tests (bad
  signature, partial download, version regression).
- **Never edit `VERSION` or create tags/releases by hand** — the release
  workflow owns them (it resolves the next tag and bumps `VERSION` back
  itself). The only manual `VERSION` change is a deliberate major/minor
  bump.
- **Don't touch `src/RealmChat/` casually** — any byte changed there rolls
  the fleet; batch comment-only cleanups with real changes.
- **This repo is public and environment-blank**: no addresses, hostnames,
  subnets, or site names anywhere — code, docs, tests, commit messages.

## Before opening a PR

CI (build + full test suite, including the controller E2E against the
Ollama stub) must be green; a red suite blocks the merge. Local steps and
the rest of the pipeline rules — the stub as executable spec, the signed
test fixtures, key rotation — are in
[docs/CI-CD-WORKFLOW.md](docs/CI-CD-WORKFLOW.md). UI changes: run the app,
check the result visually, and attach a screenshot to the PR.
