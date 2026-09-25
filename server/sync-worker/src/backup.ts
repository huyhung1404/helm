// Nightly backups to R2: one gzip'd JSON snapshot per account per day (ciphertext only, as stored) plus one of
// the registry. Snapshots older than KEEP_DAYS are deleted. Restore is an admin action (POST /admin/accounts/:id/restore).
//
//   accounts/<accountId>/<YYYY-MM-DD>.json.gz
//   registry/<YYYY-MM-DD>.json.gz

import type { Env, Result } from "./objects.ts";
import { HttpError } from "./sql.ts";

export const KEEP_DAYS = 30;

export interface BackupRun {
  date: string;
  accounts: number;
  bytes: number;
  deleted: number;
  failed: string[];
}

export async function runBackup(env: Env, now: Date): Promise<BackupRun> {
  const bucket = requireBucket(env);
  const date = now.toISOString().slice(0, 10);
  const registry = env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
  const run: BackupRun = { date, accounts: 0, bytes: 0, deleted: 0, failed: [] };

  const registrySnapshot = (await registry.exportSnapshot()) as Result;
  run.bytes += await put(bucket, `registry/${date}.json.gz`, registrySnapshot.body);
  run.deleted += await prune(bucket, "registry/", now);

  const accounts = (registrySnapshot.body as { accounts: { accountId: string }[] }).accounts;
  for (const { accountId } of accounts) {
    try {
      const stub = env.ACCOUNT.get(env.ACCOUNT.idFromName(accountId));
      const snapshot = (await stub.admin("export", null)) as Result;
      if (snapshot.status !== 200) throw new Error(`export returned ${snapshot.status}`);
      run.bytes += await put(bucket, `accounts/${accountId}/${date}.json.gz`, snapshot.body);
      run.deleted += await prune(bucket, `accounts/${accountId}/`, now);
      run.accounts += 1;
    } catch (error) {
      // One broken account must not stop the others.
      console.error(`backup of ${accountId} failed`, error);
      run.failed.push(accountId);
    }
  }
  return run;
}

export async function listBackups(env: Env, prefix: string): Promise<{ key: string; size: number; uploaded: string }[]> {
  const bucket = requireBucket(env);
  const items: { key: string; size: number; uploaded: string }[] = [];
  let cursor: string | undefined;
  do {
    const page = await bucket.list({ prefix, cursor, limit: 1000 });
    for (const object of page.objects) items.push({ key: object.key, size: object.size, uploaded: object.uploaded.toISOString() });
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  return items.sort((a, b) => b.key.localeCompare(a.key));
}

export async function readBackup(env: Env, key: string): Promise<unknown> {
  const object = await requireBucket(env).get(key);
  if (!object) throw new HttpError(404, "backup_not_found");
  const text = await new Response(object.body.pipeThrough(new DecompressionStream("gzip"))).text();
  return JSON.parse(text);
}

async function put(bucket: R2Bucket, key: string, value: unknown): Promise<number> {
  const gzipped = await new Response(
    new Blob([JSON.stringify(value)]).stream().pipeThrough(new CompressionStream("gzip")),
  ).arrayBuffer();
  await bucket.put(key, gzipped, { httpMetadata: { contentType: "application/json", contentEncoding: "gzip" } });
  return gzipped.byteLength;
}

async function prune(bucket: R2Bucket, prefix: string, now: Date): Promise<number> {
  const cutoff = new Date(now.getTime() - KEEP_DAYS * 86_400_000).toISOString().slice(0, 10);
  let deleted = 0;
  let cursor: string | undefined;
  do {
    const page = await bucket.list({ prefix, cursor, limit: 1000 });
    const old = page.objects.map((o) => o.key).filter((key) => (/(\d{4}-\d{2}-\d{2})\.json\.gz$/.exec(key)?.[1] ?? "9999") < cutoff);
    if (old.length > 0) {
      await bucket.delete(old);
      deleted += old.length;
    }
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  return deleted;
}

function requireBucket(env: Env): R2Bucket {
  if (!env.BACKUPS) throw new HttpError(503, "backups_disabled", "Bind an R2 bucket as BACKUPS to enable backups.");
  return env.BACKUPS;
}
