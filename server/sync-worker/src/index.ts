// Helm sync Worker. Routes:
//
//   Sync API (Authorization: Bearer helm_pat_…)
//     GET    /v1/me                       account (with storage usage) and this token
//     POST   /v1/sync/push                { items: [...] } -> { outcomes: [...] }
//     GET    /v1/sync/pull?since=&limit=  -> { records, nextSeq, hasMore }
//     GET    /v1/keyring                  wrapped master key (opaque), 404 until the first device uploads it
//     PUT    /v1/keyring                  { baseVersion, data } compare-and-set
//     GET    /v1/tokens                   the account's devices           (scope tokens:manage)
//     POST   /v1/tokens                   { name, scopes?, expiresInDays? } -> token (shown once)
//     DELETE /v1/tokens/:tokenId          revoke a device
//
//   Invites (no Authorization)
//     POST   /v1/redeem                   { invite, accountName, deviceName } -> new account + its first token
//
//   Admin page: GET /admin (sign in with ADMIN_TOKEN in the browser)
//
//   Admin API (Authorization: Bearer <ADMIN_TOKEN secret>)
//     POST   /admin/invites                          { note?, days?, quotaMb? } -> invite code (shown once)
//     GET    /admin/invites
//     DELETE /admin/invites/:id
//     POST   /admin/accounts                         { name, quotaMb? } -> account
//     GET    /admin/accounts
//     GET    /admin/accounts/:id                     account with storage usage
//     PUT    /admin/accounts/:id/quota               { quotaMb }
//     POST   /admin/accounts/:id/tokens              { name, scopes?, expiresInDays? } -> token (shown once)
//     GET    /admin/accounts/:id/tokens
//     DELETE /admin/accounts/:id/tokens/:tokenId
//     POST   /admin/accounts/:id/disable             revoke every token of the account
//     POST   /admin/accounts/:id/restore             { key, confirm: <accountId> } restore from an R2 backup
//     GET    /admin/backups?prefix=                  list backups
//     POST   /admin/backups/run                      back up now (also runs nightly from the cron trigger)
//
//   Rate limits (per client IP): /v1/redeem, and failed admin sign-ins.
//
// See docs/sync-protocol.md. There is no open sign-up: an account needs an invite or the admin.

import { LIMITS } from "./account-store.ts";
import { adminAsset } from "./admin-page.ts";
import { listBackups, readBackup, runBackup } from "./backup.ts";
import type { ApiInput, ApiOp, Env, Result } from "./objects.ts";
import { INVITE_LIMITS } from "./registry-store.ts";
import { HttpError } from "./sql.ts";
import {
  createInviteCode, hashToken, isAccountId, isInviteCode, newAccountId, parseToken, randomBase62, secretsEqual,
} from "./token.ts";

export { AccountObject, RegistryObject } from "./objects.ts";

const MAX_BODY_BYTES = 8 * 1024 * 1024;
const MAX_NAME_LENGTH = 100;

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const url = new URL(request.url);
      if (url.pathname === "/v1/redeem") return respond(await handleRedeem(request, env));
      if (url.pathname.startsWith("/v1/")) return respond(await handleApi(request, env, url));
      // The admin page itself is static and holds no secret; everything it shows comes from the API below.
      const page = request.method === "GET" ? adminAsset(url.pathname) : null;
      if (page) return page;
      if (url.pathname.startsWith("/admin/")) return respond(await handleAdmin(request, env, url));
      if (url.pathname === "/" || url.pathname === "/health") return respond({ status: 200, body: { service: "helm-sync" } });
      return respond({ status: 404, body: { error: "not_found" } });
    } catch (error) {
      if (error instanceof HttpError) return respond({ status: error.status, body: { error: error.code, message: error.message } });
      console.error(error);
      return respond({ status: 500, body: { error: "internal" } });
    }
  },

  async scheduled(controller: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    if (!env.BACKUPS) return;
    ctx.waitUntil(runBackup(env, new Date(controller.scheduledTime)).then((run) => {
      console.log(`backup ${run.date}: ${run.accounts} accounts, ${run.bytes} bytes, ${run.deleted} pruned, failed: ${run.failed.join(",") || "none"}`);
    }));
  },
} satisfies ExportedHandler<Env>;

async function handleApi(request: Request, env: Env, url: URL): Promise<Result> {
  const token = bearer(request);
  const parsed = token ? parseToken(token) : null;
  // Malformed tokens never reach a Durable Object, so random ids cannot create objects.
  if (!token || !parsed) throw new HttpError(401, "invalid_token");

  const input: ApiInput = { since: url.searchParams.get("since"), limit: url.searchParams.get("limit") };
  let op: ApiOp | undefined = ({
    "GET /v1/me": "me",
    "POST /v1/sync/push": "push",
    "GET /v1/sync/pull": "pull",
    "GET /v1/keyring": "getKeyring",
    "PUT /v1/keyring": "putKeyring",
    "GET /v1/tokens": "listTokens",
    "POST /v1/tokens": "createToken",
  } as Record<string, ApiOp>)[`${request.method} ${url.pathname}`];
  const revoke = /^\/v1\/tokens\/(tok_[0-9A-Za-z]{12})$/.exec(url.pathname);
  if (!op && revoke && request.method === "DELETE") {
    op = "revokeToken";
    input.tokenId = revoke[1];
  }
  if (!op) throw new HttpError(404, "not_found");

  if (request.method !== "GET" && request.method !== "DELETE") input.body = await readJson(request);
  return rpc(account(env, parsed.accountId).api(await hashToken(token), op, input));
}

async function handleRedeem(request: Request, env: Env): Promise<Result> {
  if (request.method !== "POST") throw new HttpError(405, "method_not_allowed");
  await enforceLimit(env.REDEEM_LIMITER, request, "Too many invite attempts. Wait a minute.");
  const body = await readJson(request);
  if (!isRecord(body)) throw new HttpError(400, "invalid_body");
  const invite = typeof body.invite === "string" ? body.invite.trim() : "";
  // A malformed code (or a typo, caught by the checksum) never reaches the registry.
  if (!isInviteCode(invite)) throw new HttpError(400, "invalid_invite", "This is not a Helm invite code.");
  const accountName = requireName(body.accountName, "accountName");
  const deviceName = requireName(body.deviceName, "deviceName");

  const registry = registryObject(env);
  const accountId = newAccountId();
  const redeemed = await rpc(registry.redeem(await hashToken(invite), accountId, accountName));
  if (redeemed.status !== 200) return redeemed;
  const { quotaMb } = redeemed.body as { quotaMb: number };

  const target = account(env, accountId);
  const created = await rpc(target.admin("init", { accountId, name: accountName, quotaMb }));
  if (created.status !== 201) return created;
  const token = await rpc(target.admin("createToken", { name: deviceName }));
  if (token.status !== 201) return token;
  return { status: 201, body: { account: created.body, token: token.body } };
}

async function handleAdmin(request: Request, env: Env, url: URL): Promise<Result> {
  const expected = env.ADMIN_TOKEN;
  if (!expected || expected.length < 32) throw new HttpError(503, "admin_disabled", "Set the ADMIN_TOKEN secret (32+ chars).");
  const provided = bearer(request);
  if (!provided || !(await secretsEqual(provided, expected))) {
    // Only failures count, so a correct token is never throttled.
    await enforceLimit(env.ADMIN_LIMITER, request, "Too many failed sign-ins. Wait a minute.");
    throw new HttpError(401, "invalid_admin_token");
  }

  const registry = registryObject(env);
  const parts = url.pathname.split("/").filter(Boolean); // ["admin", "accounts" | "invites", id?, ...]
  const method = request.method;

  if (parts[1] === "invites") {
    if (parts.length === 2 && method === "GET") return rpc(registry.listInvites());
    if (parts.length === 2 && method === "POST") {
      const input = await readJson(request);
      const options = isRecord(input) ? input : {};
      const note = typeof options.note === "string" ? options.note.trim().slice(0, INVITE_LIMITS.maxNoteLength) : "";
      const days = options.days ?? INVITE_LIMITS.defaultDays;
      const quotaMb = options.quotaMb ?? LIMITS.defaultQuotaMb;
      if (!Number.isSafeInteger(days) || (days as number) < 1 || (days as number) > INVITE_LIMITS.maxDays) {
        throw new HttpError(400, "invalid_days", `Days must be 1–${INVITE_LIMITS.maxDays}.`);
      }
      if (!Number.isSafeInteger(quotaMb) || (quotaMb as number) < 1 || (quotaMb as number) > LIMITS.maxQuotaMb) {
        throw new HttpError(400, "invalid_quota", `Quota must be 1–${LIMITS.maxQuotaMb} MB.`);
      }
      const code = createInviteCode();
      const created = await rpc(registry.createInvite("inv_" + randomBase62(12), await hashToken(code),
        { note, days: days as number, quotaMb: quotaMb as number }));
      return created.status === 201 ? { status: 201, body: { code, ...(created.body as object) } } : created;
    }
    if (parts.length === 3 && method === "DELETE") return rpc(registry.revokeInvite(parts[2]));
    throw new HttpError(404, "not_found");
  }

  if (parts[1] === "backups") {
    if (parts.length === 2 && method === "GET") {
      const prefix = url.searchParams.get("prefix") ?? "";
      if (!/^[A-Za-z0-9/_.-]{0,100}$/.test(prefix)) throw new HttpError(400, "invalid_prefix");
      return { status: 200, body: { backups: await listBackups(env, prefix) } };
    }
    if (parts.length === 3 && parts[2] === "run" && method === "POST") return { status: 200, body: await runBackup(env, new Date()) };
    throw new HttpError(404, "not_found");
  }

  if (parts[1] !== "accounts") throw new HttpError(404, "not_found");

  if (parts.length === 2) {
    if (method === "GET") return rpc(registry.list());
    if (method === "POST") {
      const input = await readJson(request);
      const name = requireName(isRecord(input) ? input.name : undefined, "name");
      const quotaMb = isRecord(input) && input.quotaMb !== undefined ? input.quotaMb : undefined;
      const accountId = newAccountId();
      const added = await rpc(registry.add(accountId, name));
      if (added.status !== 201) return added;
      return rpc(account(env, accountId).admin("init", { accountId, name, quotaMb }));
    }
    throw new HttpError(405, "method_not_allowed");
  }

  const accountId = parts[2];
  if (!isAccountId(accountId)) throw new HttpError(404, "account_not_found");
  const known = await rpc(registry.find(accountId));
  if (known.status !== 200) return known;
  const target = account(env, accountId);

  if (parts.length === 3 && method === "GET") return rpc(target.admin("info", null));
  if (parts.length === 4 && parts[3] === "quota" && method === "PUT") {
    const input = await readJson(request);
    return rpc(target.admin("setQuota", isRecord(input) ? input.quotaMb : undefined));
  }
  if (parts.length === 4 && parts[3] === "tokens") {
    if (method === "GET") return rpc(target.admin("listTokens", null));
    if (method === "POST") return rpc(target.admin("createToken", await readJson(request)));
    throw new HttpError(405, "method_not_allowed");
  }
  if (parts.length === 5 && parts[3] === "tokens" && method === "DELETE") return rpc(target.admin("revokeToken", parts[4]));
  if (parts.length === 4 && parts[3] === "restore" && method === "POST") {
    const input = await readJson(request);
    const key = isRecord(input) && typeof input.key === "string" ? input.key : "";
    // Typing the account id is the confirmation: a restore rewrites what every device of the account sees.
    if (!isRecord(input) || input.confirm !== accountId) throw new HttpError(400, "confirmation_required", "Pass confirm: <accountId>.");
    if (!key.startsWith(`accounts/${accountId}/`)) throw new HttpError(400, "backup_of_another_account");
    return rpc(target.admin("import", await readBackup(env, key)));
  }
  if (parts.length === 4 && parts[3] === "disable" && method === "POST") {
    const revoked = await rpc(target.admin("revokeAll", null));
    await rpc(registry.markDisabled(accountId));
    return revoked;
  }
  throw new HttpError(404, "not_found");
}

async function enforceLimit(limiter: RateLimit | undefined, request: Request, message: string): Promise<void> {
  if (!limiter) return;
  const key = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const { success } = await limiter.limit({ key });
  if (!success) throw new HttpError(429, "rate_limited", message);
}

/** RPC stubs type an `unknown` body as unserializable; every object method returns a plain Result. */
async function rpc(call: Promise<unknown>): Promise<Result> {
  return (await call) as Result;
}

function account(env: Env, accountId: string) {
  return env.ACCOUNT.get(env.ACCOUNT.idFromName(accountId));
}

function registryObject(env: Env) {
  return env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
}

function requireName(value: unknown, field: string): string {
  const name = typeof value === "string" ? value.trim() : "";
  if (name.length === 0 || name.length > MAX_NAME_LENGTH) throw new HttpError(400, `invalid_${field}`);
  return name;
}

function bearer(request: Request): string | null {
  const header = request.headers.get("Authorization") ?? "";
  return header.startsWith("Bearer ") ? header.slice(7).trim() : null;
}

async function readJson(request: Request): Promise<unknown> {
  const length = Number(request.headers.get("Content-Length") ?? "0");
  if (length > MAX_BODY_BYTES) throw new HttpError(413, "body_too_large");
  const text = await request.text();
  if (text.length > MAX_BODY_BYTES) throw new HttpError(413, "body_too_large");
  if (text.length === 0) return undefined;
  try {
    return JSON.parse(text);
  } catch {
    throw new HttpError(400, "invalid_json");
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function respond(result: Result): Response {
  return new Response(JSON.stringify(result.body), {
    status: result.status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" },
  });
}
