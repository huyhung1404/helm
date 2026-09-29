// Encrypted blobs (Vault documents). The client encrypts every chunk; the server sees ids, sizes and timing only.
// Metadata lives in the account's SQLite (this store), ciphertext in R2 under blobs/<accountId>/<blobId>/<index>
// (the Worker streams it, see blobs.ts). Blobs count against the same storage quota as records.
//
//   pending ──commit──▶ committed ──delete──▶ committed + deletedAt ──30 days──▶ purging ──▶ (rows gone)
//      └──delete, or 7 days──▶ purging                    └──restore──▶ committed

import { HttpError, type Sql } from "./sql.ts";

const MIB = 1024 * 1024;

export interface BlobLimits {
  maxChunkBytes: number;
  maxChunksPerBlob: number;
  maxBlobBytes: number;
}

/** 4 MiB of plaintext plus room for the per-chunk envelope. Part of the client format: not configurable. */
export const CHUNK_BYTES = 4 * MIB + 4096;

/** The blob limits for a MAX_BLOB_MB setting: enough chunks for that much plaintext, plus one. */
export function blobLimits(maxBlobMb: number): BlobLimits {
  const maxChunksPerBlob = Math.ceil(maxBlobMb / 4) + 1;
  return { maxChunkBytes: CHUNK_BYTES, maxChunksPerBlob, maxBlobBytes: maxChunksPerBlob * CHUNK_BYTES };
}

export const BLOB_LIMITS = {
  defaultMaxBlobMb: 256,
  maxBlobMbCeiling: 64 * 1024,
  defaultBlobListLimit: 500,
  maxBlobListLimit: 1000,
  purgeDeletedAfterMs: 30 * 86_400_000,
  purgePendingAfterMs: 7 * 86_400_000,
};

const BLOB_ID_PATTERN = /^[0-9A-HJKMNP-TV-Z]{26}$/;

export type BlobState = "pending" | "committed" | "purging";

export interface BlobJson {
  id: string;
  size: number;
  chunkCount: number;
  state: BlobState;
  createdAt: number;
  committedAt: number | null;
  deletedAt: number | null;
}

/** BlobJson plus the indexes uploaded so far, so a client can resume an upload. */
export interface BlobDetailJson extends BlobJson {
  chunks: number[];
}

/** A committed blob in an account backup: its metadata and the size of each chunk (R2 is not part of it). */
export interface BlobSnapshotJson extends BlobJson {
  chunkSizes: number[];
}

/** How blobs count against the account's storage quota; grow throws 413 quota_exceeded when a reservation does not fit. */
export interface QuotaMeter {
  grow(bytes: number): void;
  shrink(bytes: number): void;
}

interface BlobRow {
  id: string;
  size: number;
  chunk_count: number;
  state: BlobState;
  created_at: number;
  committed_at: number | null;
  deleted_at: number | null;
}

export function isBlobId(value: unknown): value is string {
  return typeof value === "string" && BLOB_ID_PATTERN.test(value);
}

export function blobPrefix(accountId: string, blobId: string): string {
  return `blobs/${accountId}/${blobId}/`;
}

export function blobKey(accountId: string, blobId: string, index: number): string {
  return blobPrefix(accountId, blobId) + index;
}

export class BlobStore {
  private readonly sql: Sql;
  private readonly now: () => number;
  private readonly limits: BlobLimits;
  private readonly quota: QuotaMeter;
  private schemaReady = false;

  constructor(sql: Sql, now: () => number, limits: BlobLimits, quota: QuotaMeter) {
    this.sql = sql;
    this.now = now;
    this.limits = limits;
    this.quota = quota;
  }

  /** Accounts created before blobs existed get the tables on their first blob operation. */
  ensureSchema(): void {
    if (this.schemaReady) return;
    this.sql.run(`CREATE TABLE IF NOT EXISTS blobs (
      id TEXT PRIMARY KEY, size INTEGER NOT NULL, chunk_count INTEGER NOT NULL, state TEXT NOT NULL,
      created_at INTEGER NOT NULL, committed_at INTEGER, deleted_at INTEGER)`);
    this.sql.run(`CREATE TABLE IF NOT EXISTS blob_chunks (
      blob_id TEXT NOT NULL, idx INTEGER NOT NULL, size INTEGER NOT NULL, PRIMARY KEY (blob_id, idx))`);
    this.schemaReady = true;
  }

  /** Reserves quota for a blob the client is about to upload. The same id and shape again returns it (created: false). */
  reserve(body: unknown): { created: boolean; blob: BlobDetailJson } {
    this.ensureSchema();
    if (!isObject(body)) throw new HttpError(400, "invalid_body", "Expected { id, size, chunkCount }.");
    const id = requireBlobId(body.id);
    const size = body.size as number;
    const chunkCount = body.chunkCount as number;
    if (!Number.isSafeInteger(chunkCount) || chunkCount < 1) throw new HttpError(400, "invalid_chunk_count");
    if (!Number.isSafeInteger(size) || size < 1) throw new HttpError(400, "invalid_size");
    if (chunkCount > this.limits.maxChunksPerBlob || size > this.limits.maxBlobBytes) {
      throw new HttpError(413, "blob_too_large",
        `A blob is at most ${this.limits.maxBlobBytes} bytes in ${this.limits.maxChunksPerBlob} chunks.`);
    }
    if (size < chunkCount || size > chunkCount * this.limits.maxChunkBytes) {
      throw new HttpError(400, "invalid_size", `${chunkCount} chunk(s) hold ${chunkCount}–${chunkCount * this.limits.maxChunkBytes} bytes.`);
    }

    return this.sql.transaction(() => {
      const current = this.row(id);
      if (current) {
        if (current.state === "purging" || current.size !== size || current.chunk_count !== chunkCount) {
          throw new HttpError(409, "blob_exists", "A different blob already has this id.");
        }
        return { created: false, blob: this.detail(current) };
      }
      this.quota.grow(size);
      this.sql.run("INSERT INTO blobs (id, size, chunk_count, state, created_at) VALUES (?, ?, ?, 'pending', ?)",
        id, size, chunkCount, this.now());
      return { created: true, blob: this.detail(this.row(id)!) };
    });
  }

  /** Checked before the Worker streams a chunk to R2: the blob is pending and the chunk fits in its reservation. */
  authorizeChunk(id: unknown, index: unknown, length: unknown): void {
    const blob = this.uploadTarget(id, index, length);
    if (this.otherChunkBytes(blob.id, index as number) + (length as number) > blob.size) throw chunkExceedsBlob(blob);
  }

  /** Called once the chunk is in R2. Uploading an index again replaces it. */
  recordChunk(id: unknown, index: unknown, length: unknown): { id: string; index: number; size: number } {
    const blob = this.uploadTarget(id, index, length);
    if (this.otherChunkBytes(blob.id, index as number) + (length as number) > blob.size) {
      // A concurrent upload of another index got there first. Forget this one, so commit reports it missing.
      this.sql.run("DELETE FROM blob_chunks WHERE blob_id = ? AND idx = ?", blob.id, index as number);
      throw chunkExceedsBlob(blob);
    }
    this.sql.run(
      "INSERT INTO blob_chunks (blob_id, idx, size) VALUES (?, ?, ?) ON CONFLICT (blob_id, idx) DO UPDATE SET size = excluded.size",
      blob.id, index as number, length as number,
    );
    return { id: blob.id, index: index as number, size: length as number };
  }

  /**
   * Presigned uploads: checks a batch of chunks { index, length } at once, like authorizeChunk, and returns them.
   * The device then PUTs each chunk straight to R2 and reports it with recordChunks.
   */
  authorizeChunks(id: unknown, body: unknown): { index: number; length: number }[] {
    const chunks = parseChunkList(body, "length");
    const blob = this.requireLive(id);
    for (const c of chunks) this.uploadTarget(blob.id, c.index, c.length);
    const indexes = new Set(chunks.map((c) => c.index));
    const others = [...this.chunkSizes(blob.id)].filter(([i]) => !indexes.has(i)).reduce((n, [, size]) => n + size, 0);
    if (others + chunks.reduce((n, c) => n + c.length, 0) > blob.size) throw chunkExceedsBlob(blob);
    return chunks.map((c) => ({ index: c.index, length: c.length }));
  }

  /**
   * Records chunks the Worker found in R2 after presigned uploads, with the sizes R2 reports (never the device's
   * word). All or nothing: a batch that would not fit the reservation records none of them.
   */
  recordChunks(id: unknown, body: unknown): { index: number; size: number }[] {
    const chunks = parseChunkList(body, "size");
    return this.sql.transaction(() => chunks.map((c) => this.recordChunk(id, c.index, c.size)).map(({ index, size }) => ({ index, size })));
  }

  /** Presigned downloads: every chunk of a committed blob (also in the trash) with its size. */
  readChunks(id: unknown): { chunkCount: number; chunks: { index: number; size: number }[] } {
    const blob = this.requireAny(id);
    if (blob.state !== "committed") throw new HttpError(404, "blob_not_found");
    const chunks = [...this.chunkSizes(blob.id)].sort(([a], [b]) => a - b).map(([index, size]) => ({ index, size }));
    return { chunkCount: blob.chunk_count, chunks };
  }

  /** Seals a blob once every chunk is uploaded and their sizes add up to the reserved size. */
  commit(id: unknown): BlobDetailJson {
    const blob = this.requireLive(id);
    if (blob.state === "committed") return this.detail(blob);
    const sizes = this.chunkSizes(blob.id);
    const missing: number[] = [];
    let total = 0;
    for (let index = 0; index < blob.chunk_count; index++) {
      const size = sizes.get(index);
      if (size === undefined) missing.push(index);
      else total += size;
    }
    if (missing.length > 0 || total !== blob.size) {
      throw new HttpError(409, "blob_incomplete",
        missing.length > 0 ? `${missing.length} of ${blob.chunk_count} chunks are missing.` : `The chunks hold ${total} bytes, not ${blob.size}.`,
        { missing });
    }
    this.sql.run("UPDATE blobs SET state = 'committed', committed_at = ? WHERE id = ?", this.now(), blob.id);
    return this.detail(this.row(blob.id)!);
  }

  get(id: unknown): BlobDetailJson {
    return this.detail(this.requireAny(id));
  }

  list(afterParam: string | null, limitParam: string | null): { blobs: BlobJson[]; hasMore: boolean; next: string | null } {
    this.ensureSchema();
    if (afterParam !== null && !isBlobId(afterParam)) throw new HttpError(400, "invalid_after");
    const limitText = limitParam ?? String(BLOB_LIMITS.defaultBlobListLimit);
    const limit = /^\d{1,5}$/.test(limitText) ? Number(limitText) : 0;
    if (limit < 1 || limit > BLOB_LIMITS.maxBlobListLimit) throw new HttpError(400, "invalid_limit");
    const rows = this.sql.all<BlobRow>("SELECT * FROM blobs WHERE id > ? ORDER BY id LIMIT ?", afterParam ?? "", limit + 1);
    const page = rows.slice(0, limit);
    return {
      blobs: page.map(toBlobJson),
      hasMore: rows.length > limit,
      next: page.length > 0 ? page[page.length - 1].id : afterParam,
    };
  }

  /** A chunk may be downloaded once its blob is committed, also while the blob sits in the trash. */
  readChunk(id: unknown, index: unknown): { size: number } {
    const blob = this.requireAny(id);
    if (blob.state !== "committed") throw new HttpError(404, "blob_not_found");
    if (!Number.isSafeInteger(index) || (index as number) < 0) throw new HttpError(400, "invalid_index");
    const size = this.chunkSizes(blob.id).get(index as number);
    if (size === undefined) throw new HttpError(404, "chunk_not_found");
    return { size };
  }

  /**
   * A committed blob goes to the trash (still readable, still counted, purged after 30 days). A pending one is
   * marked purging at once: the Worker deletes its chunks from R2, then calls purged().
   */
  remove(id: unknown): BlobDetailJson {
    const blob = this.requireAny(id);
    if (blob.state === "committed" && blob.deleted_at === null) {
      this.sql.run("UPDATE blobs SET deleted_at = ? WHERE id = ?", this.now(), blob.id);
    } else if (blob.state === "pending") {
      this.sql.run("UPDATE blobs SET state = 'purging' WHERE id = ?", blob.id);
    }
    return this.detail(this.row(blob.id)!);
  }

  /** Takes a blob out of the trash. Nothing to do for one that is not in it. */
  restore(id: unknown): BlobDetailJson {
    const blob = this.requireLive(id);
    if (blob.deleted_at !== null) this.sql.run("UPDATE blobs SET deleted_at = NULL WHERE id = ?", blob.id);
    return this.detail(this.row(blob.id)!);
  }

  /**
   * Garbage collection, step 1: marks as purging the blobs in the trash for more than 30 days and the uploads
   * abandoned for more than 7, and returns every purging id (including ones an earlier run did not finish).
   */
  due(now: number): string[] {
    this.ensureSchema();
    return this.sql.transaction(() => {
      this.sql.run(
        `UPDATE blobs SET state = 'purging'
         WHERE (state = 'committed' AND deleted_at IS NOT NULL AND deleted_at < ?) OR (state = 'pending' AND created_at < ?)`,
        now - BLOB_LIMITS.purgeDeletedAfterMs, now - BLOB_LIMITS.purgePendingAfterMs,
      );
      return this.sql.all<{ id: string }>("SELECT id FROM blobs WHERE state = 'purging' ORDER BY id").map((r) => r.id);
    });
  }

  /** Step 2, once their chunks are gone from R2: drops the rows and gives the space back. Ignores ids not purging. */
  purged(ids: unknown): { purged: number; bytes: number } {
    this.ensureSchema();
    if (!Array.isArray(ids) || !ids.every(isBlobId)) throw new HttpError(400, "invalid_blob_ids");
    return this.sql.transaction(() => {
      let purged = 0;
      let bytes = 0;
      for (const id of ids) {
        const blob = this.row(id);
        if (!blob || blob.state !== "purging") continue;
        this.sql.run("DELETE FROM blob_chunks WHERE blob_id = ?", id);
        this.sql.run("DELETE FROM blobs WHERE id = ?", id);
        this.quota.shrink(blob.size);
        purged += 1;
        bytes += blob.size;
      }
      return { purged, bytes };
    });
  }

  /** Bytes held by blobs: every state counts until the rows are purged. */
  totalBytes(): number {
    this.ensureSchema();
    return this.sql.all<{ n: number | null }>("SELECT SUM(size) AS n FROM blobs")[0].n ?? 0;
  }

  exportSnapshot(): BlobSnapshotJson[] {
    this.ensureSchema();
    return this.sql.all<BlobRow>("SELECT * FROM blobs WHERE state = 'committed' ORDER BY id").map((row) => {
      const sizes = this.chunkSizes(row.id);
      return { ...toBlobJson(row), chunkSizes: Array.from({ length: row.chunk_count }, (_, i) => sizes.get(i) ?? 0) };
    });
  }

  /** Validates a backup's blobs before anything is written. A snapshot from before blobs has none. */
  parseSnapshot(raw: unknown): BlobSnapshotJson[] {
    if (raw === undefined || raw === null) return [];
    if (!Array.isArray(raw)) throw new HttpError(400, "invalid_snapshot", "blobs");
    return raw.map((b: unknown, index): BlobSnapshotJson => {
      const bad = () => new HttpError(400, "invalid_snapshot", `blobs[${index}]`);
      if (!isObject(b) || !isBlobId(b.id) || b.state !== "committed") throw bad();
      const { size, chunkCount, createdAt, committedAt, deletedAt, chunkSizes } = b;
      if (!Number.isSafeInteger(size) || !Number.isSafeInteger(chunkCount) || (chunkCount as number) < 1
        || !Number.isSafeInteger(createdAt) || !Number.isSafeInteger(committedAt)
        || !(deletedAt === null || Number.isSafeInteger(deletedAt)) || !Array.isArray(chunkSizes)
        || chunkSizes.length !== chunkCount || !chunkSizes.every((s) => Number.isSafeInteger(s) && s > 0)
        || chunkSizes.reduce((n: number, s: number) => n + s, 0) !== size) {
        throw bad();
      }
      return {
        id: b.id, size: size as number, chunkCount: chunkCount as number, state: "committed",
        createdAt: createdAt as number, committedAt: committedAt as number, deletedAt: deletedAt as number | null,
        chunkSizes: chunkSizes as number[],
      };
    });
  }

  /**
   * Recreates the rows of backed-up blobs this account no longer has. Existing rows are never touched, and the
   * chunks are expected to still be in R2 (a blob purged since the backup reads as chunk_not_found).
   * The caller recomputes used_bytes.
   */
  importSnapshot(blobs: BlobSnapshotJson[]): number {
    this.ensureSchema();
    let restored = 0;
    for (const blob of blobs) {
      if (this.row(blob.id)) continue;
      this.sql.run(
        "INSERT INTO blobs (id, size, chunk_count, state, created_at, committed_at, deleted_at) VALUES (?, ?, ?, 'committed', ?, ?, ?)",
        blob.id, blob.size, blob.chunkCount, blob.createdAt, blob.committedAt, blob.deletedAt,
      );
      blob.chunkSizes.forEach((size, index) =>
        this.sql.run("INSERT INTO blob_chunks (blob_id, idx, size) VALUES (?, ?, ?)", blob.id, index, size));
      restored += 1;
    }
    return restored;
  }

  // ---------------------------------------------------------------- helpers

  private uploadTarget(id: unknown, index: unknown, length: unknown): BlobRow {
    const blob = this.requireLive(id);
    if (!Number.isSafeInteger(index) || (index as number) < 0 || (index as number) >= blob.chunk_count) {
      throw new HttpError(400, "invalid_index", `Chunk indexes are 0–${blob.chunk_count - 1}.`);
    }
    if (!Number.isSafeInteger(length) || (length as number) < 1) throw new HttpError(400, "invalid_length");
    if ((length as number) > this.limits.maxChunkBytes) throw new HttpError(413, "chunk_too_large");
    if (blob.state === "committed") throw new HttpError(409, "blob_committed", "A committed blob cannot change.");
    return blob;
  }

  /** Pending or committed; a purging blob is as good as gone. */
  private requireLive(id: unknown): BlobRow {
    const blob = this.requireAny(id);
    if (blob.state === "purging") throw new HttpError(404, "blob_not_found");
    return blob;
  }

  private requireAny(id: unknown): BlobRow {
    this.ensureSchema();
    const blob = this.row(requireBlobId(id));
    if (!blob) throw new HttpError(404, "blob_not_found");
    return blob;
  }

  private row(id: string): BlobRow | undefined {
    return this.sql.all<BlobRow>("SELECT * FROM blobs WHERE id = ?", id)[0];
  }

  private chunkSizes(id: string): Map<number, number> {
    const rows = this.sql.all<{ idx: number; size: number }>("SELECT idx, size FROM blob_chunks WHERE blob_id = ?", id);
    return new Map(rows.map((r) => [r.idx, r.size]));
  }

  private otherChunkBytes(id: string, index: number): number {
    return this.sql.all<{ n: number | null }>(
      "SELECT SUM(size) AS n FROM blob_chunks WHERE blob_id = ? AND idx != ?", id, index)[0].n ?? 0;
  }

  private detail(row: BlobRow): BlobDetailJson {
    const chunks = this.sql.all<{ idx: number }>("SELECT idx FROM blob_chunks WHERE blob_id = ? ORDER BY idx", row.id).map((r) => r.idx);
    return { ...toBlobJson(row), chunks };
  }
}

function toBlobJson(row: BlobRow): BlobJson {
  return {
    id: row.id,
    size: row.size,
    chunkCount: row.chunk_count,
    state: row.state,
    createdAt: row.created_at,
    committedAt: row.committed_at,
    deletedAt: row.deleted_at,
  };
}

function requireBlobId(id: unknown): string {
  if (!isBlobId(id)) throw new HttpError(400, "invalid_blob_id", "Blob ids are ULIDs (26 Crockford base32 characters).");
  return id;
}

/** { chunks: [{ index, <field> }] } with distinct indexes, at most one blob's worth. */
function parseChunkList(body: unknown, field: "length" | "size"): { index: number; length: number; size: number }[] {
  if (!isObject(body) || !Array.isArray(body.chunks) || body.chunks.length === 0 || body.chunks.length > 1024) {
    throw new HttpError(400, "invalid_body", `Expected { chunks: [{ index, ${field} }] }.`);
  }
  const seen = new Set<number>();
  return body.chunks.map((raw: unknown, i) => {
    if (!isObject(raw) || !Number.isSafeInteger(raw.index) || !Number.isSafeInteger(raw[field]) || seen.has(raw.index as number)) {
      throw new HttpError(400, "invalid_chunk", `chunks[${i}]`);
    }
    seen.add(raw.index as number);
    const value = raw[field] as number;
    return { index: raw.index as number, length: value, size: value };
  });
}

function chunkExceedsBlob(blob: BlobRow): HttpError {
  return new HttpError(400, "chunk_exceeds_blob", `The chunks would hold more than the ${blob.size} bytes reserved.`);
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
