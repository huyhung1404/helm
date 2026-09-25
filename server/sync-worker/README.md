# Helm sync server

A Cloudflare Worker with one Durable Object (SQLite) per account. It stores Helm's end-to-end encrypted sync records. The protocol is in [docs/sync-protocol.md](../../docs/sync-protocol.md).

Anyone can install Helm, but **only someone with an invite or a token can sync**. There is no open sign-up.
- The admin creates **invite codes** (`helm_inv_…`). Each one works once and creates one person's own account.
- Each device then uses a **token**, in the style of GitHub personal access tokens:

```
helm_pat_<accountId>_<secret><checksum>
```

- A token is shown once, when it is created. The server stores only its SHA-256.
- Each token has a name and scopes (`sync:read`, `sync:write`), and optionally an expiry date.
- A token can be revoked on its own. Give each device its own token, so a lost laptop can be locked out without affecting the others.
- Tokens only grant access. The data is encrypted on the device, so neither the server nor the admin can read it.

## Setup from the Cloudflare dashboard (no commands)

Cloudflare builds and deploys the Worker from this GitHub repository (Workers Builds), and the admin page at `/admin` replaces `admin.ps1`. The code must be on the branch that Cloudflare watches (`main` by default).

1. **Connect the repository.** Workers & Pages → **Create application** → **Import a repository** → connect GitHub. When GitHub asks which repositories to allow, select only `helm`.
2. **Configure the project**, then select **Save and Deploy**:
   - Project name: `helm-sync`. It must match `name` in `wrangler.toml`, or the build fails.
   - Branch: `main`
   - Root directory: `server/sync-worker`
   - Build command: `npm ci`
   - Deploy command: `npx wrangler deploy` (the default)

   The deploy creates both Durable Objects from the migration in `wrangler.toml` and the custom domain `sync.huyhung1404.com`.
3. **Add the admin secret.** Worker `helm-sync` → **Settings** → **Variables and Secrets** → **Add**:
   - Type: **Secret**
   - Name: `ADMIN_TOKEN`
   - Value: 40 or more random characters from your password manager's generator. Keep it there.

   Then select **Deploy**.
4. **Check the domain** under Settings → **Domains & Routes**. It should list `sync.huyhung1404.com`. If it does not, use **Add** → **Custom Domain**. The hostname must not already have a CNAME record.
5. **Optional build settings** (Settings → Build):
   - Limit builds to changes under `server/sync-worker/`, so that Helm-only commits do not redeploy the Worker.
   - Turn off builds for non-production branches.
6. **Check that it works:** `https://sync.huyhung1404.com/health` should show `{"service":"helm-sync"}`.
7. **Hand out access** at `https://sync.huyhung1404.com/admin`. Sign in with `ADMIN_TOKEN`, then:
   - **Invites:** set a note, how long the code stays valid, and the storage quota, then select **Create invite**. Send the code privately. The person enters it in Helm → General → Sync (**I have an invite code**), which creates their own account. They add their other devices themselves under **Devices**.
   - **Accounts:** see storage use, change quotas, revoke tokens, or disable an account.

   An invite code is shown only once, so copy it when it appears.

The admin page keeps `ADMIN_TOKEN` in the tab's memory only. It is forgotten on reload and when you select **Lock**.

For a second lock on `/admin`, add Cloudflare Access: Zero Trust → Access → Applications → **Add an application** → Self-hosted. Use the domain `sync.huyhung1404.com` with the path `admin`, and a policy that allows only your email. After that, `admin.ps1` needs an Access service token.

## One-time setup (command line)

1. **Log in to Cloudflare** (this opens a browser):
   ```powershell
   cd server/sync-worker
   npm ci
   npx wrangler login
   ```
2. **Check the domain.** `wrangler.toml` serves the Worker on `sync.huyhung1404.com`. The domain must be a zone in the same Cloudflare account; `wrangler deploy` creates the DNS record and the certificate. To use another hostname, change `routes`, and pass `-Server` to `admin.ps1`.
3. **Create the admin secret.** Keep it in your password manager, because it creates and revokes every token:
   ```powershell
   $bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
   [Convert]::ToBase64String($bytes)
   npx wrangler secret put ADMIN_TOKEN        # paste the value
   ```
   If `ADMIN_TOKEN` is missing, or shorter than 32 characters, the admin API refuses every request.
4. **Deploy:**
   ```powershell
   npx wrangler deploy
   ```
5. **Create your account and one token per device:**
   ```powershell
   $env:HELM_SYNC_ADMIN = '<ADMIN_TOKEN>'
   ./admin.ps1 new-account 'Anh'
   ./admin.ps1 new-token <accountId> 'PC nhà'
   ./admin.ps1 new-token <accountId> 'Laptop công ty' -Days 365
   ```
   Paste each token into Helm under General → Sync on that device.

The Free Workers plan is enough to start (SQLite Durable Objects are included). R2 is not needed yet; it arrives with file attachments.

## Everyday admin

```powershell
./admin.ps1 accounts
./admin.ps1 tokens <accountId>              # names, scopes, expiry, last used, revoked
./admin.ps1 revoke <accountId> <tokenId>    # that device stops syncing immediately
./admin.ps1 new-token <accountId> 'Viewer' -ReadOnly
./admin.ps1 disable <accountId>             # revokes every token of the account
```

A revoked or expired token puts the device in the Unauthorized state. Its local data is kept, and it syncs again once a new token is entered.

## Development

```powershell
npm ci
npm test            # store and token tests on Node's built-in SQLite
npm run typecheck
npm run dev         # local Worker on http://127.0.0.1:8787 (needs .dev.vars, below)
```

`.dev.vars` is gitignored and holds the local admin secret: `ADMIN_TOKEN=<any 32+ chars>`.

End-to-end tests run the real Helm engine against the local Worker:

```powershell
$env:HELM_SYNC_TEST_URL = 'http://127.0.0.1:8787'
$env:HELM_SYNC_TEST_ADMIN = '<the ADMIN_TOKEN from .dev.vars>'
dotnet test ../../tests/Helm.Tests
```

Without these variables the `SyncServerTests` are skipped. CI runs them against `wrangler dev`.

## Security notes

- Secrets live only in `wrangler secret`. This folder is public and contains none.
- Malformed tokens, including ones with a bad checksum, are rejected before any Durable Object is touched. A well-formed token for an id nobody created reaches an empty object that writes nothing.
- Account ids come from the admin API only, so no one can create an account.
- The `/admin` page is static (strict CSP, no inline script, no third-party origins, framing denied). It renders data only as text, and every action goes through the admin API with the token you type.
- For extra protection, add a Cloudflare WAF rate-limiting rule for `sync.huyhung1404.com/admin/*` and `/v1/*`.
