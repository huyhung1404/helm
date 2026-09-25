// Thin Durable Object shells around the stores. Errors cannot cross the RPC boundary with their status intact,
// so every call returns { status, body } and the Worker turns it into a Response.

import { DurableObject } from "cloudflare:workers";
import { AccountStore, type Scope } from "./account-store.ts";
import { RegistryStore } from "./registry-store.ts";
import { HttpError, type Sql, type SqlValue } from "./sql.ts";

export interface Env {
  ACCOUNT: DurableObjectNamespace<AccountObject>;
  REGISTRY: DurableObjectNamespace<RegistryObject>;
  ADMIN_TOKEN?: string;
  /** Nightly backups (R2 bucket helm-sync-backups). Without it, backups are off. */
  BACKUPS?: R2Bucket;
  /** Per-IP limits for invite redemption and failed admin sign-ins (Workers rate limiting). */
  REDEEM_LIMITER?: RateLimit;
  ADMIN_LIMITER?: RateLimit;
}

export interface Result {
  status: number;
  body: unknown;
}

export type ApiOp = "me" | "push" | "pull" | "getKeyring" | "putKeyring" | "listTokens" | "createToken" | "revokeToken";
export type AccountAdminOp =
  | "init" | "info" | "setQuota" | "createToken" | "listTokens" | "revokeToken" | "revokeAll" | "export" | "import";

export interface ApiInput {
  body?: unknown;
  since?: string | null;
  limit?: string | null;
  tokenId?: string;
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
};

export class AccountObject extends DurableObject<Env> {
  private readonly store: AccountStore;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.store = new AccountStore(durableSql(ctx.storage));
  }

  async api(tokenHash: string, op: ApiOp, input: ApiInput): Promise<Result> {
    return run(async () => {
      const token = this.store.authenticate(tokenHash, REQUIRED_SCOPE[op]);
      switch (op) {
        case "me":
          return { status: 200, body: { account: this.store.info(), token } };
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
    if (error instanceof HttpError) return { status: error.status, body: { error: error.code, message: error.message } };
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
