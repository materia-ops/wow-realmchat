# Security policy

Please report vulnerabilities privately via GitHub's built-in reporting:
**Security → Report a vulnerability** on this repository (or the
`gh api` / "Privately reporting a security vulnerability" flow). Do not
open a public issue for anything exploitable — installed clients
self-update unattended, so update-path issues are the most sensitive kind.

Trust model in one line: every release is verified by fielded clients
against an RSA public key pinned in the exe before anything is installed;
the signing key, its custody, and the rotation/compromise runbook are
documented in [docs/CI-CD-WORKFLOW.md](docs/CI-CD-WORKFLOW.md).
