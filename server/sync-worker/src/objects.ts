// Thin Durable Object shells around the stores. Errors cannot cross the RPC boundary with their status intact,
// so every call returns { status, body } and the Worker turns it into a Response.

import { DurableObject } from "cloudflare:workers";
import { AccountStore, limitsFromEnv, type Scope } from "./account-store.ts";
import { RegistryStore } from "./registry-store.ts";
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
  | "deleteBlob" | "restoreBlob";
export type AccountAdminOp =
  | "init" | "info" | "setQuota" | "createToken" | "listTokens" | "revokeToken" | "revokeAll" | "export" | "import"
  | "blobsDue" | "blobsPurged";

export interface ApiInput {
  body?: unknown;
  since?: string | null;
  limit?: string | null;
  tokenId?: string;
  blobId?: string;
  index?: number;
  length?: number;
  after?: string | null;
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
};

export class AccountObject extends DurableObject<Env> {
  private readonly store: AccountStore;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.store = new AccountStore(durableSql(ctx.storage), Date.now, limitsFromEnv(env));
  }

  async api(tokenHash: string, op: ApiOp, input: ApiInput): Promise<Result> {
    return run(async () => {
      const token = this.store.authenticate(tokenHash, REQUIRED_SCOPE[op]);
      switch (op) {
        case "me": {
          const { maxChunkBytes, maxChunksPerBlob, maxBlobBytes, maxPayloadBytes } = this.store.limits;
          const limits = { maxChunkBytes, maxChunksPerBlob, maxBlobBytes, maxPayloadBytes };
          return { status: 200, body: { account: this.store.info(), token, limits } };
        }
        case "push":
          return { status: 200, body: { outcomes: this.store.push(input.body) } };
        case "pull":
          return { status: 200, body: this.store.pull(input.since ?? null, input.limit ?? null) };
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
        case "revokeToken":
          return this.store.revokeToken(input.tokenId ?? "")
            ? { status: 200, body: { revoked: true } }
            : { status: 404, body: { error: "token_not_found" } };
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
        case "revokeToken":
          return this.store.revokeToken(String(arg))
            ? { status: 200, body: { revoked: true } }
            : { status: 404, body: { error: "token_not_found" } };
        case "revokeAll":
          return { status: 200, body: { revoked: this.store.revokeAllTokens() } };
        case "export":
          return { status: 200, body: this.store.exportSnapshot() };
        case "import":
          return { status: 200, body: this.store.importSnapshot(arg) };
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
