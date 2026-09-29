// The Worker half of blob storage. Chunk bodies stream between the client and R2 and never enter the account's
// Durable Object, which only authorizes and records them (blob-store.ts). Also the blob garbage collection that
// the nightly cron runs:
//
//   blobs/<accountId>/<blobId>/<index>

import { LIMITS, limitsFromEnv } from "./account-store.ts";
import { blobKey, blobPrefix, type BlobJson } from "./blob-store.ts";
import type { AccountAdminOp, ApiInput, ApiOp, Env, Result } from "./objects.ts";
import { PRESIGN_SECONDS, presignConfigured, presignR2 } from "./presign.ts";
import { HttpError } from "./sql.ts";

export type AccountApi = (op: ApiOp, input: ApiInput) => Promise<Result>;
export type AccountAdmin = (op: AccountAdminOp, arg: unknown) => Promise<Result>;

export interface BlobGcRun {
  accounts: number;
  blobs: number;
  objects: number;
  bytes: number;
  failed: string[];
}

export function requireBlobs(env: Env): R2Bucket {
  if (!env.BLOBS) throw new HttpError(503, "blobs_disabled", "Bind an R2 bucket as BLOBS to enable blobs.");
  return env.BLOBS;
}

/** Authorize, stream the body to R2, record. The body is never buffered, and never parsed as JSON. */
export async function putChunk(request: Request, bucket: R2Bucket, api: AccountApi, accountId: string, blobId: string, index: number): Promise<Result> {
  const header = request.headers.get("Content-Length");
  if (header === null) throw new HttpError(411, "length_required", "Send the chunk with a Content-Length.");
  if (!/^\d{1,16}$/.test(header)) throw new HttpError(400, "invalid_length");
  const length = Number(header);
  if (length > LIMITS.maxChunkBytes) throw new HttpError(413, "chunk_too_large", `A chunk is at most ${LIMITS.maxChunkBytes} bytes.`);
  if (length === 0 || !request.body) throw new HttpError(400, "empty_chunk");

  const allowed = await api("authorizeChunk", { blobId, index, length });
  if (allowed.status !== 200) return allowed;
  const key = blobKey(accountId, blobId, index);
  const object = await bucket.put(key, request.body, { httpMetadata: { contentType: "application/octet-stream" } });
  if (!object) throw new Error("R2 put returned no object");
  const recorded = await api("recordChunk", { blobId, index, length: object.size });
  // Nobody will ever record this object: remove it. Not after a 409, though: the blob was committed meanwhile,
  // and the key is one of its chunks.
  if (recorded.status !== 200 && recorded.status !== 409) await bucket.delete(key);
  return recorded;
}

export async function getChunk(bucket: R2Bucket, api: AccountApi, accountId: string, blobId: string, index: number): Promise<Result | Response> {
  const allowed = await api("readChunk", { blobId, index });
  if (allowed.status !== 200) return allowed;
  const object = await bucket.get(blobKey(accountId, blobId, index));
  if (!object) return { status: 404, body: { error: "chunk_not_found" } };
  return new Response(object.body, {
    headers: {
      "Content-Type": "application/octet-stream",
      "Content-Length": String(object.size),
      "Cache-Control": "no-store",
    },
  });
}

/** A committed blob goes to the trash. A pending one is thrown away now: its chunks, its rows and its quota. */
export async function deleteBlob(bucket: R2Bucket, api: AccountApi, admin: AccountAdmin, accountId: string, blobId: string): Promise<Result> {
  const deleted = await api("deleteBlob", { blobId });
  if (deleted.status !== 200 || (deleted.body as BlobJson).state !== "purging") return deleted;
  await deletePrefix(bucket, blobPrefix(accountId, blobId));
  const purged = await admin("blobsPurged", [blobId]);
  if (purged.status !== 200) return purged;
  return { status: 200, body: { id: blobId, state: "purged" } };
}

/**
 * Purges, in every account, the blobs in the trash for more than 30 days and the uploads abandoned for more than 7.
 * The rows go only after the chunks are gone from R2, so an interrupted run is finished by the next one.
 */
export async function runBlobGc(env: Env, now: number): Promise<BlobGcRun> {
  const bucket = requireBlobs(env);
  const registry = env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
  const listed = (await registry.list()) as Result;
  const run: BlobGcRun = { accounts: 0, blobs: 0, objects: 0, bytes: 0, failed: [] };

  for (const { accountId } of (listed.body as { accounts: { accountId: string }[] }).accounts) {
    try {
      const stub = env.ACCOUNT.get(env.ACCOUNT.idFromName(accountId));
      const due = (await stub.admin("blobsDue", now)) as Result;
      if (due.status !== 200) throw new Error(`blobsDue returned ${due.status}`);
      const ids = (due.body as { ids: string[] }).ids;
      if (ids.length > 0) {
        for (const id of ids) run.objects += await deletePrefix(bucket, blobPrefix(accountId, id));
        const purged = (await stub.admin("blobsPurged", ids)) as Result;
        if (purged.status !== 200) throw new Error(`blobsPurged returned ${purged.status}`);
        const { purged: count, bytes } = purged.body as { purged: number; bytes: number };
        run.blobs += count;
        run.bytes += bytes;
      }
      run.accounts += 1;
    } catch (error) {
      // One broken account must not stop the others.
      console.error(`blob gc of ${accountId} failed`, error);
      run.failed.push(accountId);
    }
  }
  return run;
}

/** Deletes every object under a prefix, 1000 at a time. Everything listed is deleted, so each page starts over. */
async function deletePrefix(bucket: R2Bucket, prefix: string): Promise<number> {
  let deleted = 0;
  for (;;) {
    const page = await bucket.list({ prefix, limit: 1000 });
    const keys = page.objects.map((o) => o.key);
    if (keys.length > 0) {
      await bucket.delete(keys);
      deleted += keys.length;
    }
    if (!page.truncated || keys.length === 0) return deleted;
  }
}

// ---------------------------------------------------------------- presigned URLs

/** The chunks' R2 URLs for uploading: authorized by the Durable Object first, valid for PRESIGN_SECONDS. */
export async function uploadUrls(env: Env, api: AccountApi, accountId: string, blobId: string, body: unknown): Promise<Result> {
  requirePresign(env);
  const allowed = await api("authorizeChunks", { blobId, body });
  if (allowed.status !== 200) return allowed;
  const now = new Date();
  const chunks = (allowed.body as { chunks: { index: number; length: number }[] }).chunks;
  const urls = await Promise.all(chunks.map(async (c) => ({
    index: c.index,
    url: await presignR2(env, "PUT", blobKey(accountId, blobId, c.index), now, c.length),
  })));
  return { status: 200, body: { urls, expiresAt: now.getTime() + PRESIGN_SECONDS * 1000 } };
}

/**
 * After presigned uploads: looks each chunk up in R2 and records the size R2 reports. A chunk that is not there
 * answers 409 chunk_missing with the indexes to upload again.
 */
export async function recordUploaded(bucket: R2Bucket, api: AccountApi, accountId: string, blobId: string, body: unknown): Promise<Result> {
  const indexes = isRecord(body) && Array.isArray(body.indexes) ? body.indexes : null;
  if (!indexes || indexes.length === 0 || indexes.length > 1024 || !indexes.every((i) => Number.isSafeInteger(i) && i >= 0)) {
    throw new HttpError(400, "invalid_body", "Expected { indexes: [...] }.");
  }
  const found = await Promise.all((indexes as number[]).map(async (index) => ({ index, head: await bucket.head(blobKey(accountId, blobId, index)) })));
  const missing = found.filter((f) => !f.head).map((f) => f.index);
  if (missing.length > 0) return { status: 409, body: { error: "chunk_missing", message: `${missing.length} chunk(s) are not in storage.`, missing } };
  return api("recordChunks", { blobId, body: { chunks: found.map((f) => ({ index: f.index, size: f.head!.size })) } });
}

/** Every chunk's R2 URL for downloading a committed blob, with its size. */
export async function downloadUrls(env: Env, api: AccountApi, accountId: string, blobId: string): Promise<Result> {
  requirePresign(env);
  const allowed = await api("readChunks", { blobId });
  if (allowed.status !== 200) return allowed;
  const now = new Date();
  const { chunkCount, chunks } = allowed.body as { chunkCount: number; chunks: { index: number; size: number }[] };
  const urls = await Promise.all(chunks.map(async (c) => ({
    index: c.index,
    size: c.size,
    url: await presignR2(env, "GET", blobKey(accountId, blobId, c.index), now),
  })));
  return { status: 200, body: { chunkCount, urls, expiresAt: now.getTime() + PRESIGN_SECONDS * 1000 } };
}

function requirePresign(env: Env): void {
  if (!presignConfigured(env)) throw new HttpError(503, "presign_disabled", "Set R2_ACCOUNT_ID, R2_ACCESS_KEY_ID and R2_SECRET_ACCESS_KEY to enable presigned URLs.");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

// ---------------------------------------------------------------- tombstones

export interface TombstoneGcRun {
  accounts: number;
  purged: number;
  bytes: number;
  failed: string[];
}

/** Removes, in every account, deletions older than TOMBSTONE_DAYS (see AccountStore.purgeTombstones). */
export async function runTombstoneGc(env: Env, now: number): Promise<TombstoneGcRun> {
  const registry = env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
  const listed = (await registry.list()) as Result;
  const cutoff = now - limitsFromEnv(env).tombstoneDays * 86_400_000;
  const run: TombstoneGcRun = { accounts: 0, purged: 0, bytes: 0, failed: [] };
  for (const { accountId } of (listed.body as { accounts: { accountId: string }[] }).accounts) {
    try {
      const stub = env.ACCOUNT.get(env.ACCOUNT.idFromName(accountId));
      const result = (await stub.admin("purgeTombstones", cutoff)) as Result;
      if (result.status !== 200) throw new Error(`purgeTombstones returned ${result.status}`);
      const { purged, bytes } = result.body as { purged: number; bytes: number };
      run.purged += purged;
      run.bytes += bytes;
      run.accounts += 1;
    } catch (error) {
      console.error(`tombstone gc of ${accountId} failed`, error);
      run.failed.push(accountId);
    }
  }
  return run;
}
