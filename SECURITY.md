# Security policy

Helm stores passwords, notes and tasks, encrypts them on the device and syncs them through a server. Security reports are taken seriously and handled before anything else.

## Supported versions

Only the latest release receives fixes. Helm updates itself, so a fix reaches installed copies with the next release.

| Version | Supported |
|---|---|
| Latest release | Yes |
| Older releases | No |

## Reporting a vulnerability

**Do not open a public issue.** Report privately through GitHub instead:

1. Open the repository's [Security tab](https://github.com/huyhung1404/helm/security) → **Report a vulnerability**.
2. Describe the problem, the affected version and platform (Windows or Android), and the steps to reproduce it. A proof of concept helps.

You can expect an acknowledgement within a few days. Once the problem is confirmed, a fix is released as soon as possible and the advisory is published afterwards, crediting you unless you prefer otherwise.

## Scope

In scope:

- The Windows and Android apps, including the Vault, its backups and `helm-vault-restore`.
- The encryption and sync protocol ([docs/sync-protocol.md](docs/sync-protocol.md), [docs/vault-design.md](docs/vault-design.md)).
- The sync server in `server/sync-worker`.
- The update pipeline (release workflow, installer, in-app updaters).

Out of scope: attacks that need an already compromised device or administrator access to it, and denial of service against the sync server.
