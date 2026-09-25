// The list of accounts, for the admin API only. Sync requests never touch it: the token carries its account id.

import { HttpError, type Sql } from "./sql.ts";

export interface AccountSummary {
  accountId: string;
  name: string;
  createdAt: number;
  disabledAt: number | null;
}

export interface InviteSummary {
  id: string;
  note: string;
  quotaMb: number;
  createdAt: number;
  expiresAt: number;
  usedAt: number | null;
  usedByAccountId: string | null;
  revokedAt: number | null;
}

interface InviteRow {
  id: string;
  code_hash: string;
  note: string;
  quota_mb: number;
  created_at: number;
  expires_at: number;
  used_at: number | null;
  used_account_id: string | null;
  revoked_at: number | null;
}

export const INVITE_LIMITS = { defaultDays: 7, maxDays: 90, maxNoteLength: 100 };

export class RegistryStore {
  private readonly sql: Sql;
  private readonly now: () => number;

  constructor(sql: Sql, now: () => number = Date.now) {
    this.sql = sql;
    this.now = now;
    this.sql.run(`CREATE TABLE IF NOT EXISTS accounts (
      account_id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at INTEGER NOT NULL, disabled_at INTEGER)`);
    this.sql.run(`CREATE TABLE IF NOT EXISTS invites (
      id TEXT PRIMARY KEY, code_hash TEXT NOT NULL UNIQUE, note TEXT NOT NULL, quota_mb INTEGER NOT NULL,
      created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, used_at INTEGER, used_account_id TEXT, revoked_at INTEGER)`);
  }

  createInvite(id: string, codeHash: string, input: { note: string; days: number; quotaMb: number }): InviteSummary {
    const now = this.now();
    this.sql.run(
      "INSERT INTO invites (id, code_hash, note, quota_mb, created_at, expires_at) VALUES (?, ?, ?, ?, ?, ?)",
      id, codeHash, input.note, input.quotaMb, now, now + input.days * 86_400_000,
    );
    return this.listInvites().find((i) => i.id === id)!;
  }

  listInvites(): InviteSummary[] {
    return this.sql.all<InviteRow>("SELECT * FROM invites ORDER BY created_at").map((r) => ({
      id: r.id, note: r.note, quotaMb: r.quota_mb, createdAt: r.created_at, expiresAt: r.expires_at,
      usedAt: r.used_at, usedByAccountId: r.used_account_id, revokedAt: r.revoked_at,
    }));
  }

  revokeInvite(id: string): boolean {
    const live = this.sql.all("SELECT id FROM invites WHERE id = ? AND used_at IS NULL AND revoked_at IS NULL", id).length > 0;
    if (live) this.sql.run("UPDATE invites SET revoked_at = ? WHERE id = ?", this.now(), id);
    return live;
  }

  /**
   * Consumes an invite and registers the new account in one step, so a code can never create two accounts.
   * Unknown, used, revoked and expired codes all fail the same way.
   */
  redeem(codeHash: string, accountId: string, name: string): { quotaMb: number } {
    return this.sql.transaction(() => {
      const invite = this.sql.all<InviteRow>("SELECT * FROM invites WHERE code_hash = ?", codeHash)[0];
      if (!invite || invite.used_at !== null || invite.revoked_at !== null || invite.expires_at <= this.now()) {
        throw new HttpError(400, "invalid_invite", "This invite code is not valid (unknown, already used, revoked or expired).");
      }
      this.sql.run("UPDATE invites SET used_at = ?, used_account_id = ? WHERE id = ?", this.now(), accountId, invite.id);
      this.add(accountId, name);
      return { quotaMb: invite.quota_mb };
    });
  }

  add(accountId: string, name: string): AccountSummary {
    if (this.find(accountId)) throw new HttpError(409, "account_exists");
    const summary: AccountSummary = { accountId, name, createdAt: this.now(), disabledAt: null };
    this.sql.run("INSERT INTO accounts (account_id, name, created_at) VALUES (?, ?, ?)", accountId, name, summary.createdAt);
    return summary;
  }

  list(): AccountSummary[] {
    return this.sql
      .all<{ account_id: string; name: string; created_at: number; disabled_at: number | null }>(
        "SELECT * FROM accounts ORDER BY created_at",
      )
      .map((r) => ({ accountId: r.account_id, name: r.name, createdAt: r.created_at, disabledAt: r.disabled_at }));
  }

  find(accountId: string): AccountSummary | null {
    return this.list().find((a) => a.accountId === accountId) ?? null;
  }

  /** Accounts and invites (code hashes only), for the nightly backup. */
  exportSnapshot(): { format: "helm-sync-registry"; version: 1; exportedAt: number; accounts: AccountSummary[]; invites: InviteSummary[] } {
    return { format: "helm-sync-registry", version: 1, exportedAt: this.now(), accounts: this.list(), invites: this.listInvites() };
  }

  markDisabled(accountId: string): void {
    this.sql.run("UPDATE accounts SET disabled_at = ? WHERE account_id = ? AND disabled_at IS NULL", this.now(), accountId);
  }
}
