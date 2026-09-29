// Thin Durable Object shells around the stores. Errors cannot cross the RPC boundary with their status intact,
// so every call returns { status, body } and the Worker turns it into a Response.

import { DurableObject } from "cloudflare:workers";
import { AccountStore, limitsFromEnv, parsePullFilter, type Scope } from "./account-store.ts";
import { RegistryStore } from "./registry-store.ts";
import { presignConfigured } from "./presign.ts";
import { HttpError, type Sql, type SqlValue } from "./sql.ts";

export interface Env {
  ACCOUNT: DurableObjectNamespace<AccountObject>;
  REGISTRY: DurableObjectNamespace<RegistryObject>;
  ADMIN_TOKEN?: string;
  /** Nightly backups (R2 bucket helm-sync-backups). Without it, backups are off. */
  BACKUPS?: R2Bucket;
  /** Encrypted blob chunks (R2 bucket helm-sync-blobs). Without it, the blob routes answer 503 blobs_disabled. */
  BLOBS?: R2Bucket;
  /** Limits an operator can raise without a code change ([vars] in wrangler.toml, or the dashboard). */
  MAX_BLOB_MB?: string;
  MAX_QUOTA_MB?: string;
  /** Days a deletion stays on the server before the nightly clean-up (90 by default). */
  TOMBSTONE_DAYS?: string;
  /**
   * R2 S3 API credentials for presigned blob URLs (chunks then go straight between the device and R2). Without all
   * three, blobs keep streaming through the Worker. R2_BLOBS_BUCKET defaults to helm-sync-blobs.
   */
  R2_ACCOUNT_ID?: string;
  R2_ACCESS_KEY_ID?: string;
  R2_SECRET_ACCESS_KEY?: string;
  R2_BLOBS_BUCKET?: string;
  /** Per-IP limits for invite redemption and failed admin sign-ins (Workers rate limiting). */
  REDEEM_LIMITER?: RateLimit;
  ADMIN_LIMITER?: RateLimit;
}

export interface Result {
  status: number;
  body: unknown;
}

export type ApiOp =
  | "me" | "push" | "pull" | "getKeyring" | "putKeyring" | "listTokens" | "createToken" | "revokeToken"
  | "reserveBlob" | "authorizeChunk" | "recordChunk" | "commitBlob" | "getBlob" | "listBlobs" | "readChunk"
  | "deleteBlob" | "restoreBlob" | "authorizeChunks" | "readChunks" | "recordChunks";
export type AccountAdminOp =
  | "init" | "info" | "setQuota" | "createToken" | "listTokens" | "revokeToken" | "revokeAll" | "export" | "import"
  | "blobsDue" | "blobsPurged" | "purgeTombstones";

export interface ApiInput {
  body?: unknown;
  since?: string | null;
  limit?: string | null;
  tokenId?: string;
  blobId?: string;
  index?: number;
  length?: number;
  after?: string | null;
  only?: string | null;
  exclude?: string | null;
}

const REQUIRED_SCOPE: Record<ApiOp, Scope> = {
  me: "sync:read",
  pull: "sync:read",
  getKeyring: "sync:read",
  push: "sync:write",
  putKeyring: "sync:write",
  listTokens: "tokens:manage",
  createToken: "tokens:manage",
  revokeToken: "tokens:manage",
  getBlob: "sync:read",
  listBlobs: "sync:read",
  readChunk: "sync:read",
  reserveBlob: "sync:write",
  authorizeChunk: "sync:write",
  recordChunk: "sync:write",
  commitBlob: "sync:write",
  deleteBlob: "sync:write",
  restoreBlob: "sync:write",
  authorizeChunks: "sync:write",
  recordChunks: "sync:write",
  readChunks: "sync:read",
};

/** Live connections per account; a new one beyond this closes the oldest (a device that vanished without closing). */
const MAX_LIVE_SOCKETS = 32;

/** Close codes the client acts on: stop (token gone) vs reconnect later. */
const CLOSE_REVOKED = 4001;
const CLOSE_REPLACED = 4008;

interface LiveAttachment {
  tokenId: string;
  expiresAt: number | null;
}

export class AccountObject extends DurableObject<Env> {
  private readonly store: AccountStore;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.store = new AccountStore(durableSql(ctx.storage), Date.now, limitsFromEnv(env));
    // Keep-alives are answered by the runtime without waking the object (WebSocket hibernation).
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  /**
   * GET /v1/sync/live, forwarded by the Worker with the token's hash: a WebSocket that says when the account
   * changed, so devices pull at once instead of waiting for their next poll. It carries only seq numbers:
   *   {"type":"hello","seq":N} on connect, {"type":"changed","seq":N} after every accepted push or restore.
   */
  async fetch(request: Request): Promise<Response> {
    const result = await run(async () => {
      if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") throw new HttpError(426, "upgrade_required");
      const token = this.store.authenticate(request.headers.get("X-Helm-Token-Hash") ?? "", "sync:read");
      const open = this.ctx.getWebSockets();
      for (const stale of open.slice(0, Math.max(0, open.length - MAX_LIVE_SOCKETS + 1))) closeQuietly(stale, CLOSE_REPLACED, "too many connections");
      const [client, server] = Object.values(new WebSocketPair());
      this.ctx.acceptWebSocket(server, [token.id]);
      server.serializeAttachment({ tokenId: token.id, expiresAt: token.expiresAt } satisfies LiveAttachment);
      server.send(JSON.stringify({ type: "hello", seq: this.store.currentSeq() }));
      return { status: 101, body: client };
    });
    if (result.status === 101) return new Response(null, { status: 101, webSocket: result.body as WebSocket });
    return new Response(JSON.stringify(result.body), { status: result.status, headers: { "Content-Type": "application/json; charset=utf-8" } });
  }

  async webSocketMessage(): Promise<void> {
    // Clients only send "ping", which the auto-response answers; anything else is ignored.
  }

  async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    closeQuietly(ws, code === 1005 || code === 1006 ? 1000 : code, reason);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    closeQuietly(ws, 1011, "error");
  }

  /** Tells every live device the new seq; closes the sockets of tokens that expired meanwhile. */
  private notify(seq: number): void {
    const now = Date.now();
    const message = JSON.stringify({ type: "changed", seq });
    for (const ws of this.ctx.getWebSockets()) {
      const attachment = ws.deserializeAttachment() as LiveAttachment | null;
      if (attachment?.expiresAt != null && attachment.expiresAt <= now) {
        closeQuietly(ws, CLOSE_REVOKED, "token expired");
        continue;
      }
      try {
        ws.send(message);
      } catch {
        // Closing or gone: webSocketClose cleans up.
      }
    }
  }

  private closeToken(tokenId: string): void {
    for (const ws of this.ctx.getWebSockets(tokenId)) closeQuietly(ws, CLOSE_REVOKED, "token revoked");
  }

  private closeAll(): void {
    for (const ws of this.ctx.getWebSockets()) closeQuietly(ws, CLOSE_REVOKED, "token revoked");
  }

  async api(tokenHash: string, op: ApiOp, input: ApiInput): Promise<Result> {
    return run(async () => {
      const token = this.store.authenticate(tokenHash, REQUIRED_SCOPE[op]);
      switch (op) {
        case "me": {
          const { maxChunkBytes, maxChunksPerBlob, maxBlobBytes, maxPayloadBytes } = this.store.limits;
          const limits = { maxChunkBytes, maxChunksPerBlob, maxBlobBytes, maxPayloadBytes };
          const features = ["live", "pull-filter", "tombstone-gc", ...(presignConfigured(this.env) ? ["presigned-blobs"] : [])];
          return { status: 200, body: { account: this.store.info(), token, limits, features, tombstoneDays: this.store.limits.tombstoneDays } };
        }
        case "push": {
          const outcomes = this.store.push(input.body);
          const seq = Math.max(0, ...outcomes.filter((o) => o.accepted).map((o) => o.seq));
          if (seq > 0) this.notify(seq);
          return { status: 200, body: { outcomes } };
        }
        case "pull":
          return { status: 200, body: this.store.pull(input.since ?? null, input.limit ?? null, parsePullFilter(input.only ?? null, input.exclude ?? null)) };
        case "getKeyring": {
          const keyring = this.store.getKeyring();
          return keyring ? { status: 200, body: keyring } : { status: 404, body: { error: "no_keyring" } };
        }
        case "putKeyring": {
          const result = this.store.putKeyring(input.body);
          return { status: result.accepted ? 200 : 409, body: result };
        }
        case "listTokens":
          return { status: 200, body: { tokens: this.store.listTokens(), currentTokenId: token.id } };
        case "createToken": {
          const { token: created, info } = await this.store.createToken(input.body, token);
          return { status: 201, body: { token: created, ...info } };
        }
        case "revokeToken": {
          const revoked = this.store.revokeToken(input.tokenId ?? "");
          if (revoked) this.closeToken(input.tokenId ?? "");
          return revoked ? { status: 200, body: { revoked: true } } : { status: 404, body: { error: "token_not_found" } };
        }
        case "authorizeChunks":
          return { status: 200, body: { chunks: this.store.blobs.authorizeChunks(input.blobId, input.body) } };
        case "recordChunks":
          return { status: 200, body: { chunks: this.store.blobs.recordChunks(input.blobId, input.body) } };
        case "readChunks":
          return { status: 200, body: this.store.blobs.readChunks(input.blobId) };
        case "reserveBlob": {
          const { created, blob } = this.store.blobs.reserve(input.body);
          return { status: created ? 201 : 200, body: blob };
        }
        case "authorizeChunk":
          this.store.blobs.authorizeChunk(input.blobId, input.index, input.length);
          return { status: 200, body: { authorized: true } };
        case "recordChunk":
          return { status: 200, body: this.store.blobs.recordChunk(input.blobId, input.index, input.length) };
        case "commitBlob":
          return { status: 200, body: this.store.blobs.commit(input.blobId) };
        case "getBlob":
          return { status: 200, body: this.store.blobs.get(input.blobId) };
        case "listBlobs":
          return { status: 200, body: this.store.blobs.list(input.after ?? null, input.limit ?? null) };
        case "readChunk":
          return { status: 200, body: this.store.blobs.readChunk(input.blobId, input.index) };
        case "deleteBlob":
          return { status: 200, body: this.store.blobs.remove(input.blobId) };
        case "restoreBlob":
          return { status: 200, body: this.store.blobs.restore(input.blobId) };
      }
    });
  }

  async admin(op: AccountAdminOp, arg: unknown): Promise<Result> {
    return run(async () => {
      switch (op) {
        case "init": {
          const { accountId, name, quotaMb } = arg as { accountId: string; name: string; quotaMb?: number };
          this.store.init(accountId, name, quotaMb);
          return { status: 201, body: this.store.info() };
        }
        case "info":
          return { status: 200, body: this.store.info() };
        case "setQuota":
          this.store.setQuota(arg);
          return { status: 200, body: this.store.info() };
        case "createToken": {
          const { token, info } = await this.store.createToken(arg);
          return { status: 201, body: { token, ...info } };
        }
        case "listTokens":
          return { status: 200, body: { tokens: this.store.listTokens() } };
        case "revokeToken": {
          const revoked = this.store.revokeToken(String(arg));
          if (revoked) this.closeToken(String(arg));
          return revoked ? { status: 200, body: { revoked: true } } : { status: 404, body: { error: "token_not_found" } };
        }
        case "revokeAll": {
          const revoked = this.store.revokeAllTokens();
          this.closeAll();
          return { status: 200, body: { revoked } };
        }
        case "export":
          return { status: 200, body: this.store.exportSnapshot() };
        case "import": {
          const imported = this.store.importSnapshot(arg);
          this.notify(this.store.currentSeq());
          return { status: 200, body: imported };
        }
        case "purgeTombstones":
          return { status: 200, body: this.store.purgeTombstones(Number(arg)) };
        case "blobsDue":
          return { status: 200, body: { ids: this.store.blobs.due(Number(arg)) } };
        case "blobsPurged":
          return { status: 200, body: this.store.blobs.purged(arg) };
      }
    });
  }
}

export class RegistryObject extends DurableObject<Env> {
  private readonly store: RegistryStore;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.store = new RegistryStore(durableSql(ctx.storage));
  }

  async add(accountId: string, name: string): Promise<Result> {
    return run(async () => ({ status: 201, body: this.store.add(accountId, name) }));
  }

  async list(): Promise<Result> {
    return run(async () => ({ status: 200, body: { accounts: this.store.list() } }));
  }

  async find(accountId: string): Promise<Result> {
    return run(async () => {
      const found = this.store.find(accountId);
      return found ? { status: 200, body: found } : { status: 404, body: { error: "account_not_found" } };
    });
  }

  async markDisabled(accountId: string): Promise<Result> {
    return run(async () => {
      this.store.markDisabled(accountId);
      return { status: 200, body: { disabled: true } };
    });
  }

  async createInvite(id: string, codeHash: string, input: { note: string; days: number; quotaMb: number }): Promise<Result> {
    return run(async () => ({ status: 201, body: this.store.createInvite(id, codeHash, input) }));
  }

  async listInvites(): Promise<Result> {
    return run(async () => ({ status: 200, body: { invites: this.store.listInvites() } }));
  }

  async revokeInvite(id: string): Promise<Result> {
    return run(async () =>
      this.store.revokeInvite(id)
        ? { status: 200, body: { revoked: true } }
        : { status: 404, body: { error: "invite_not_found" } });
  }

  async exportSnapshot(): Promise<Result> {
    return run(async () => ({ status: 200, body: this.store.exportSnapshot() }));
  }

  async redeem(codeHash: string, accountId: string, name: string): Promise<Result> {
    return run(async () => ({ status: 200, body: this.store.redeem(codeHash, accountId, name) }));
  }
}

function closeQuietly(ws: WebSocket, code: number, reason: string): void {
  try {
    ws.close(code, reason);
  } catch {
    // Already closed.
  }
}

async function run(fn: () => Promise<Result>): Promise<Result> {
  try {
    return await fn();
  } catch (error) {
    if (error instanceof HttpError) return { status: error.status, body: { error: error.code, message: error.message, ...error.details } };
    console.error(error);
    return { status: 500, body: { error: "internal" } };
  }
}

/** Durable Object SQLite behind the Sql seam. BLOBs are bound as ArrayBuffer, which is what the runtime accepts. */
function durableSql(storage: DurableObjectStorage): Sql {
  const bind = (params: SqlValue[]) =>
    params.map((v) => (v instanceof Uint8Array ? v.buffer.slice(v.byteOffset, v.byteOffset + v.byteLength) : v));
  return {
    all: <T>(query: string, ...params: SqlValue[]) => storage.sql.exec(query, ...bind(params)).toArray() as T[],
    run: (query: string, ...params: SqlValue[]) => {
      storage.sql.exec(query, ...bind(params)).toArray();
    },
    transaction: <T>(fn: () => T) => storage.transactionSync(fn),
  };
}
