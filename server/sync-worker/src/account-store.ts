// One account's data and tokens. Runs inside that account's Durable Object, which handles requests one at a time,
// so compare-and-set and the global seq need no further locking. Behaviour must match
// tests/Helm.Tests/FakeSyncServer.cs and docs/sync-protocol.md.

import { HttpError, asBytes, fromBase64, toBase64, type Sql } from "./sql.ts";
import { createToken, hashToken, randomBase62 } from "./token.ts";

export const LIMITS = {
  maxItemsPerPush: 100,
  maxPayloadBytes: 1024 * 1024,
  defaultPullLimit: 500,
  maxPullLimit: 1000,
  maxKeyringBytes: 16 * 1024,
  maxTokenNameLength: 100,
  maxTokensPerAccount: 100,
  maxTokenLifetimeDays: 3650,
  defaultQuotaMb: 256,
  maxQuotaMb: 100 * 1024,
};

// tokens:manage lets a device list, create and revoke the account's tokens (Helm → General → Sync → Devices).
export const SCOPES = ["sync:read", "sync:write", "tokens:manage"] as const;
export type Scope = (typeof SCOPES)[number];

const COLLECTION_PATTERN = /^[a-z0-9][a-z0-9._-]{0,63}$/;
// Do not bother writing last_used_at more often than this.
const LAST_USED_RESOLUTION_MS = 60 * 60 * 1000;

export interface TokenInfo {
  id: string;
  name: string;
  scopes: Scope[];
  createdAt: number;
  expiresAt: number | null;
  lastUsedAt: number | null;
  revokedAt: number | null;
}

export interface RecordJson {
  collection: string;
  id: string;
  version: number;
  seq: number;
  deleted: boolean;
  payload: string;
}

export interface PushOutcomeJson {
  collection: string;
  id: string;
  accepted: boolean;
  version: number;
  seq: number;
  current: RecordJson | null;
}

interface RecordRow {
  collection: string;
  id: string;
  version: number;
  seq: number;
  deleted: number;
  payload: Uint8Array;
}

interface TokenRow {
  id: string;
  name: string;
  scopes: string;
  created_at: number;
  expires_at: number | null;
  last_used_at: number | null;
  revoked_at: number | null;
}

export class AccountStore {
  private readonly sql: Sql;
  private readonly now: () => number;

  constructor(sql: Sql, now: () => number = Date.now) {
    this.sql = sql;
    this.now = now;
  }

  /** False for an id nobody created: such an object must stay empty (reads only), whatever the request. */
  get initialized(): boolean {
    const tables = this.sql.all("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'meta'");
    return tables.length > 0 && this.meta("account_id") !== null;
  }

  init(accountId: string, name: string, quotaMb: number = LIMITS.defaultQuotaMb): void {
    const quota = parseQuotaMb(quotaMb);
    if (this.initialized) throw new HttpError(409, "account_exists");
    this.sql.transaction(() => {
      this.sql.run("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
      this.sql.run(`CREATE TABLE IF NOT EXISTS records (
        collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, seq INTEGER NOT NULL,
        deleted INTEGER NOT NULL, payload BLOB NOT NULL, PRIMARY KEY (collection, id))`);
      this.sql.run("CREATE UNIQUE INDEX IF NOT EXISTS records_seq ON records (seq)");
      this.sql.run(`CREATE TABLE IF NOT EXISTS tokens (
        id TEXT PRIMARY KEY, name TEXT NOT NULL, hash TEXT NOT NULL UNIQUE, scopes TEXT NOT NULL,
        created_at INTEGER NOT NULL, expires_at INTEGER, last_used_at INTEGER, revoked_at INTEGER)`);
      this.sql.run(`CREATE TABLE IF NOT EXISTS keyring (
        id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL, data TEXT NOT NULL)`);
      this.setMeta("account_id", accountId);
      this.setMeta("name", name);
      this.setMeta("created_at", String(this.now()));
      this.setMeta("seq", "0");
      this.setMeta("quota_bytes", String(quota));
      this.setMeta("used_bytes", "0");
    });
  }

  info(): { accountId: string; name: string; createdAt: number; usedBytes: number; quotaBytes: number } {
    this.requireInitialized();
    return {
      accountId: this.meta("account_id")!,
      name: this.meta("name")!,
      createdAt: Number(this.meta("created_at")),
      usedBytes: Number(this.meta("used_bytes")),
      quotaBytes: Number(this.meta("quota_bytes")),
    };
  }

  setQuota(quotaMb: unknown): void {
    this.requireInitialized();
    this.setMeta("quota_bytes", String(parseQuotaMb(quotaMb)));
  }

  // ---------------------------------------------------------------- tokens

  /**
   * Creates a token. With <paramref name="grantor"/> (a device creating a token for another device) the new token
   * gets at most the grantor's scopes; the admin API passes none and may grant any scope.
   */
  async createToken(input: unknown, grantor?: TokenInfo): Promise<{ token: string; info: TokenInfo }> {
    this.requireInitialized();
    const { name, scopes: requested, expiresInDays } = parseTokenRequest(input);
    const scopes = grantor && !isObjectWithScopes(input) ? [...grantor.scopes] : requested;
    if (grantor && scopes.some((s) => !grantor.scopes.includes(s))) {
      throw new HttpError(403, "insufficient_scope", "A token cannot grant scopes it does not have.");
    }
    const live = this.sql.all<{ n: number }>("SELECT COUNT(*) AS n FROM tokens WHERE revoked_at IS NULL")[0].n;
    if (live >= LIMITS.maxTokensPerAccount) throw new HttpError(409, "too_many_tokens");

    const token = createToken(this.meta("account_id")!);
    const hash = await hashToken(token);
    const now = this.now();
    const info: TokenInfo = {
      id: "tok_" + randomBase62(12),
      name,
      scopes,
      createdAt: now,
      expiresAt: expiresInDays === null ? null : now + expiresInDays * 86_400_000,
      lastUsedAt: null,
      revokedAt: null,
    };
    this.sql.run(
      "INSERT INTO tokens (id, name, hash, scopes, created_at, expires_at) VALUES (?, ?, ?, ?, ?, ?)",
      info.id, info.name, hash, info.scopes.join(" "), info.createdAt, info.expiresAt,
    );
    return { token, info };
  }

  listTokens(): TokenInfo[] {
    this.requireInitialized();
    return this.sql.all<TokenRow>("SELECT * FROM tokens ORDER BY created_at").map(toTokenInfo);
  }

  revokeToken(id: string): boolean {
    this.requireInitialized();
    const found = this.sql.all("SELECT id FROM tokens WHERE id = ? AND revoked_at IS NULL", id).length > 0;
    if (found) this.sql.run("UPDATE tokens SET revoked_at = ? WHERE id = ?", this.now(), id);
    return found;
  }

  revokeAllTokens(): number {
    this.requireInitialized();
    const live = this.sql.all<{ n: number }>("SELECT COUNT(*) AS n FROM tokens WHERE revoked_at IS NULL")[0].n;
    this.sql.run("UPDATE tokens SET revoked_at = ? WHERE revoked_at IS NULL", this.now());
    return live;
  }

  /** Resolves a token by its hash; every failure looks the same to the caller except expiry and scope. */
  authenticate(tokenHash: string, required: Scope): TokenInfo {
    if (!this.initialized) throw new HttpError(401, "invalid_token");
    const row = this.sql.all<TokenRow>("SELECT * FROM tokens WHERE hash = ?", tokenHash)[0];
    if (!row || row.revoked_at !== null) throw new HttpError(401, "invalid_token");
    const now = this.now();
    if (row.expires_at !== null && row.expires_at <= now) throw new HttpError(401, "token_expired");
    const info = toTokenInfo(row);
    if (!info.scopes.includes(required)) throw new HttpError(403, "insufficient_scope", `This token lacks ${required}.`);
    if (row.last_used_at === null || now - row.last_used_at >= LAST_USED_RESOLUTION_MS) {
      this.sql.run("UPDATE tokens SET last_used_at = ? WHERE id = ?", now, row.id);
      info.lastUsedAt = now;
    }
    return info;
  }

  // ---------------------------------------------------------------- records

  push(body: unknown): PushOutcomeJson[] {
    this.requireInitialized();
    const items = parsePushItems(body);
    return this.sql.transaction(() => {
      let delta = 0;
      const outcomes = items.map((item): PushOutcomeJson => {
        const current = this.sql.all<RecordRow>(
          "SELECT collection, id, version, seq, deleted, payload FROM records WHERE collection = ? AND id = ?",
          item.collection, item.id,
        )[0];
        const currentVersion = current?.version ?? 0;
        if (item.baseVersion !== currentVersion) {
          return { collection: item.collection, id: item.id, accepted: false, version: 0, seq: 0, current: current ? toRecordJson(current) : null };
        }
        const seq = Number(this.meta("seq")) + 1;
        const version = currentVersion + 1;
        this.setMeta("seq", String(seq));
        this.sql.run(
          `INSERT INTO records (collection, id, version, seq, deleted, payload) VALUES (?, ?, ?, ?, ?, ?)
           ON CONFLICT (collection, id) DO UPDATE SET
             version = excluded.version, seq = excluded.seq, deleted = excluded.deleted, payload = excluded.payload`,
          item.collection, item.id, version, seq, item.deleted ? 1 : 0, item.payload,
        );
        delta += item.payload.length - (current ? asBytes(current.payload).length : 0);
        return { collection: item.collection, id: item.id, accepted: true, version, seq, current: null };
      });

      const used = Number(this.meta("used_bytes")) + delta;
      const quota = Number(this.meta("quota_bytes"));
      // Throwing rolls the whole batch back. Shrinking writes (deletes, smaller edits) are always allowed.
      if (delta > 0 && used > quota) {
        throw new HttpError(413, "quota_exceeded", `Storage quota exceeded (${mb(used)} of ${mb(quota)} MB).`);
      }
      this.setMeta("used_bytes", String(Math.max(0, used)));
      return outcomes;
    });
  }

  pull(sinceParam: string | null, limitParam: string | null): { records: RecordJson[]; nextSeq: number; hasMore: boolean } {
    this.requireInitialized();
    const since = parseInteger(sinceParam ?? "0", "since", 0, Number.MAX_SAFE_INTEGER);
    const limit = parseInteger(limitParam ?? String(LIMITS.defaultPullLimit), "limit", 1, LIMITS.maxPullLimit);
    const rows = this.sql.all<RecordRow>(
      "SELECT collection, id, version, seq, deleted, payload FROM records WHERE seq > ? ORDER BY seq LIMIT ?",
      since, limit + 1,
    );
    const page = rows.slice(0, limit);
    return {
      records: page.map(toRecordJson),
      nextSeq: page.length > 0 ? page[page.length - 1].seq : since,
      hasMore: rows.length > limit,
    };
  }

  // ---------------------------------------------------------------- keyring

  /** The client's wrapped master key (opaque to the server), so a new device can unlock with the passphrase. */
  getKeyring(): { version: number; data: string } | null {
    this.requireInitialized();
    const row = this.sql.all<{ version: number; data: string }>("SELECT version, data FROM keyring WHERE id = 1")[0];
    return row ? { version: row.version, data: row.data } : null;
  }

  putKeyring(body: unknown): { accepted: boolean; version: number } {
    this.requireInitialized();
    if (!isObject(body)) throw new HttpError(400, "invalid_body");
    const baseVersion = body.baseVersion;
    const data = body.data;
    if (!Number.isSafeInteger(baseVersion) || (baseVersion as number) < 0) throw new HttpError(400, "invalid_base_version");
    if (typeof data !== "string" || data.length === 0 || data.length > LIMITS.maxKeyringBytes) {
      throw new HttpError(400, "invalid_keyring");
    }
    return this.sql.transaction(() => {
      const current = this.getKeyring();
      const currentVersion = current?.version ?? 0;
      if (baseVersion !== currentVersion) return { accepted: false, version: currentVersion };
      const version = currentVersion + 1;
      this.sql.run(
        "INSERT INTO keyring (id, version, data) VALUES (1, ?, ?) ON CONFLICT (id) DO UPDATE SET version = excluded.version, data = excluded.data",
        version, data,
      );
      return { accepted: true, version };
    });
  }

  // ---------------------------------------------------------------- helpers

  private requireInitialized(): void {
    if (!this.initialized) throw new HttpError(404, "account_not_found");
  }

  private meta(key: string): string | null {
    const row = this.sql.all<{ value: string }>("SELECT value FROM meta WHERE key = ?", key)[0];
    return row ? row.value : null;
  }

  private setMeta(key: string, value: string): void {
    this.sql.run("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT (key) DO UPDATE SET value = excluded.value", key, value);
  }
}

function mb(bytes: number): string {
  return (bytes / (1024 * 1024)).toFixed(1);
}

function parseQuotaMb(value: unknown): number {
  if (!Number.isSafeInteger(value) || (value as number) < 1 || (value as number) > LIMITS.maxQuotaMb) {
    throw new HttpError(400, "invalid_quota", `Quota must be 1–${LIMITS.maxQuotaMb} MB.`);
  }
  return (value as number) * 1024 * 1024;
}

function isObjectWithScopes(input: unknown): boolean {
  return isObject(input) && input.scopes !== undefined;
}

function toTokenInfo(row: TokenRow): TokenInfo {
  return {
    id: row.id,
    name: row.name,
    scopes: row.scopes.split(" ").filter((s): s is Scope => (SCOPES as readonly string[]).includes(s)),
    createdAt: row.created_at,
    expiresAt: row.expires_at,
    lastUsedAt: row.last_used_at,
    revokedAt: row.revoked_at,
  };
}

function toRecordJson(row: RecordRow): RecordJson {
  return {
    collection: row.collection,
    id: row.id,
    version: row.version,
    seq: row.seq,
    deleted: row.deleted !== 0,
    payload: toBase64(asBytes(row.payload)),
  };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseInteger(text: string, name: string, min: number, max: number): number {
  if (!/^\d{1,16}$/.test(text)) throw new HttpError(400, `invalid_${name}`);
  const value = Number(text);
  if (!Number.isSafeInteger(value) || value < min || value > max) throw new HttpError(400, `invalid_${name}`);
  return value;
}

function isValidId(id: unknown): id is string {
  // eslint-disable-next-line no-control-regex
  return typeof id === "string" && id.length >= 1 && id.length <= 128 && !/[\u0000-\u001f\u007f-\u009f]/.test(id);
}

interface PushItem {
  collection: string;
  id: string;
  baseVersion: number;
  deleted: boolean;
  payload: Uint8Array;
}

function parsePushItems(body: unknown): PushItem[] {
  if (!isObject(body) || !Array.isArray(body.items)) throw new HttpError(400, "invalid_body", "Expected { items: [...] }.");
  if (body.items.length > LIMITS.maxItemsPerPush) throw new HttpError(413, "too_many_items");
  return body.items.map((raw, index) => {
    const bad = (what: string) => new HttpError(400, "invalid_item", `items[${index}]: ${what}`);
    if (!isObject(raw)) throw bad("not an object");
    if (typeof raw.collection !== "string" || !COLLECTION_PATTERN.test(raw.collection)) throw bad("collection");
    if (!isValidId(raw.id)) throw bad("id");
    if (!Number.isSafeInteger(raw.baseVersion) || (raw.baseVersion as number) < 0) throw bad("baseVersion");
    if (typeof raw.deleted !== "boolean") throw bad("deleted");
    const payload = typeof raw.payload === "string" ? fromBase64(raw.payload) : null;
    if (!payload || payload.length === 0) throw bad("payload");
    if (payload.length > LIMITS.maxPayloadBytes) throw new HttpError(413, "payload_too_large", `items[${index}]`);
    return { collection: raw.collection, id: raw.id, baseVersion: raw.baseVersion as number, deleted: raw.deleted, payload };
  });
}

function parseTokenRequest(input: unknown): { name: string; scopes: Scope[]; expiresInDays: number | null } {
  if (!isObject(input)) throw new HttpError(400, "invalid_body");
  const name = typeof input.name === "string" ? input.name.trim() : "";
  if (name.length === 0 || name.length > LIMITS.maxTokenNameLength) throw new HttpError(400, "invalid_name");

  let scopes: Scope[] = [...SCOPES];
  if (input.scopes !== undefined) {
    if (!Array.isArray(input.scopes) || input.scopes.length === 0) throw new HttpError(400, "invalid_scopes");
    for (const s of input.scopes) {
      if (!(SCOPES as readonly unknown[]).includes(s)) throw new HttpError(400, "invalid_scopes", `Unknown scope ${String(s)}.`);
    }
    scopes = [...new Set(input.scopes as Scope[])];
  }

  let expiresInDays: number | null = null;
  if (input.expiresInDays !== undefined && input.expiresInDays !== null) {
    const days = input.expiresInDays;
    if (!Number.isSafeInteger(days) || (days as number) < 1 || (days as number) > LIMITS.maxTokenLifetimeDays) {
      throw new HttpError(400, "invalid_expires_in_days");
    }
    expiresInDays = days as number;
  }
  return { name, scopes, expiresInDays };
}
