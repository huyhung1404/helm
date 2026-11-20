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

/**
 * Pairing a new device by a code (docs/sync-protocol.md, "Pairing"). The new device posts a public key under
 * SHA-256(code); a signed-in device that typed the code reads the key and leaves a sealed package; the new device
 * picks it up with a secret only it knows. Everything here is opaque to the server, lives 5 minutes and is used once.
 */
export const PAIRING_LIMITS = { ttlMs: 5 * 60_000, maxPublicKeyChars: 400, maxSealedChars: 64 * 1024 };

export interface PairingOffer {
  publicKey: string;
  deviceName: string;
  expiresAt: number;
}

interface PairingRow {
  id: string;
  public_key: string;
  device_name: string;
  pickup_hash: string;
  created_at: number;
  expires_at: number;
  sealed: string | null;
  sealed_account_id: string | null;
}

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
    this.sql.run(`CREATE TABLE IF NOT EXISTS pairings (
      id TEXT PRIMARY KEY, public_key TEXT NOT NULL, device_name TEXT NOT NULL, pickup_hash TEXT NOT NULL,
      created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, sealed TEXT, sealed_account_id TEXT)`);
  }

  /** A new device offers its public key under the code's hash. 409 if that hash is taken (the device makes a new key). */
  offerPairing(id: string, input: { publicKey: string; deviceName: string; pickupHash: string }): { expiresAt: number } {
    const now = this.now();
    this.sql.run("DELETE FROM pairings WHERE expires_at <= ?", now);
    if (this.sql.all("SELECT id FROM pairings WHERE id = ?", id).length > 0) throw new HttpError(409, "pairing_exists");
    const expiresAt = now + PAIRING_LIMITS.ttlMs;
    this.sql.run(
      "INSERT INTO pairings (id, public_key, device_name, pickup_hash, created_at, expires_at) VALUES (?, ?, ?, ?, ?, ?)",
      id, input.publicKey, input.deviceName, input.pickupHash, now, expiresAt,
    );
    return { expiresAt };
  }

  /** What a signed-in device needs to check the code and seal for the new one. Unknown, expired and sealed: 404. */
  readPairing(id: string): PairingOffer {
    const row = this.livePairing(id);
    if (!row || row.sealed !== null) throw new HttpError(404, "pairing_not_found", "This code is unknown, used or expired.");
    return { publicKey: row.public_key, deviceName: row.device_name, expiresAt: row.expires_at };
  }

  /** The signed-in device leaves the sealed package once; the first one wins. */
  sealPairing(id: string, accountId: string, sealed: string): void {
    const row = this.livePairing(id);
    if (!row) throw new HttpError(404, "pairing_not_found", "This code is unknown, used or expired.");
    if (row.sealed !== null) throw new HttpError(409, "pairing_sealed");
    this.sql.run("UPDATE pairings SET sealed = ?, sealed_account_id = ? WHERE id = ?", sealed, accountId, id);
  }

  /**
   * The new device asks for its package with its pickup secret's hash: null while nothing is sealed yet; once picked
   * up, the pairing is gone. A wrong secret looks like an unknown code.
   */
  pickUpPairing(id: string, pickupHash: string): { sealed: string; accountId: string } | null {
    return this.sql.transaction(() => {
      const row = this.livePairing(id);
      if (!row || row.pickup_hash !== pickupHash) throw new HttpError(404, "pairing_not_found", "This code is unknown, used or expired.");
      if (row.sealed === null) return null;
      this.sql.run("DELETE FROM pairings WHERE id = ?", id);
      return { sealed: row.sealed, accountId: row.sealed_account_id ?? "" };
    });
  }

  private livePairing(id: string): PairingRow | null {
    const row = this.sql.all<PairingRow>("SELECT * FROM pairings WHERE id = ?", id)[0];
    return row && row.expires_at > this.now() ? row : null;
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
