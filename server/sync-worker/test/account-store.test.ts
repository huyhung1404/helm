// The server half of the sync protocol. Mirrors tests/Helm.Tests/FakeSyncServer.cs semantics.

import assert from "node:assert/strict";
import { test } from "node:test";
import { AccountStore, LIMITS } from "../src/account-store.ts";
import { HttpError, toBase64 } from "../src/sql.ts";
import { hashToken, newAccountId, parseToken } from "../src/token.ts";
import { nodeSql } from "./node-sql.ts";

function setup(quotaMb?: number) {
  let now = 1_800_000_000_000;
  const store = new AccountStore(nodeSql(), () => now);
  const accountId = newAccountId();
  store.init(accountId, "Anh", quotaMb);
  return { store, accountId, advance: (ms: number) => (now += ms) };
}

const payload = (text: string) => toBase64(new TextEncoder().encode(text));
const item = (id: string, baseVersion: number, text = id, deleted = false) => ({
  collection: "notes", id, baseVersion, deleted, payload: payload(text),
});

function rejects(fn: () => unknown, status: number, code: string) {
  assert.throws(fn, (e: unknown) => e instanceof HttpError && e.status === status && e.code === code);
}

test("push is compare-and-set on the record version and assigns a global seq", () => {
  const { store } = setup();
  const [created] = store.push({ items: [item("a", 0)] });
  assert.deepEqual([created.accepted, created.version, created.seq], [true, 1, 1]);

  const [stale] = store.push({ items: [item("a", 0, "other")] });
  assert.equal(stale.accepted, false);
  assert.equal(stale.current?.version, 1);
  assert.equal(stale.current?.payload, payload("a"));

  const [updated] = store.push({ items: [item("a", 1, "a2")] });
  assert.deepEqual([updated.accepted, updated.version, updated.seq], [true, 2, 2]);
});

test("a base version for a record that does not exist is rejected with no current record", () => {
  const { store } = setup();
  const [outcome] = store.push({ items: [item("ghost", 3)] });
  assert.equal(outcome.accepted, false);
  assert.equal(outcome.current, null);
});

test("pull pages by seq and returns only the latest state of each record", () => {
  const { store } = setup();
  store.push({ items: [item("a", 0), item("b", 0), item("c", 0)] });
  store.push({ items: [item("a", 1, "a2")] });

  const first = store.pull("0", "2");
  assert.deepEqual(first.records.map((r) => r.id), ["b", "c"]);
  assert.equal(first.hasMore, true);
  const second = store.pull(String(first.nextSeq), "2");
  assert.deepEqual(second.records.map((r) => [r.id, r.version]), [["a", 2]]);
  assert.equal(second.hasMore, false);
  assert.deepEqual(store.pull(String(second.nextSeq), null), { records: [], nextSeq: second.nextSeq, hasMore: false });
});

test("tombstones are stored and pulled like any other write", () => {
  const { store } = setup();
  store.push({ items: [item("a", 0)] });
  store.push({ items: [item("a", 1, "tomb", true)] });
  const [record] = store.pull("0", null).records;
  assert.equal(record.deleted, true);
});

test("push validates every field and the batch size", () => {
  const { store } = setup();
  rejects(() => store.push({}), 400, "invalid_body");
  rejects(() => store.push({ items: [{ ...item("a", 0), collection: "Notes" }] }), 400, "invalid_item");
  rejects(() => store.push({ items: [{ ...item("a", 0), id: "bad\nid" }] }), 400, "invalid_item");
  rejects(() => store.push({ items: [{ ...item("a", 0), baseVersion: -1 }] }), 400, "invalid_item");
  rejects(() => store.push({ items: [{ ...item("a", 0), payload: "" }] }), 400, "invalid_item");
  rejects(() => store.push({ items: [{ ...item("a", 0), payload: "%%%" }] }), 400, "invalid_item");
  const big = toBase64(new Uint8Array(LIMITS.maxPayloadBytes + 1));
  rejects(() => store.push({ items: [{ ...item("a", 0), payload: big }] }), 413, "payload_too_large");
  const many = Array.from({ length: LIMITS.maxItemsPerPush + 1 }, (_, i) => item(`n${i}`, 0));
  rejects(() => store.push({ items: many }), 413, "too_many_items");
  // A rejected batch stores nothing.
  assert.equal(store.pull("0", null).records.length, 0);
});

test("pull validates its parameters", () => {
  const { store } = setup();
  rejects(() => store.pull("-1", null), 400, "invalid_since");
  rejects(() => store.pull("abc", null), 400, "invalid_since");
  rejects(() => store.pull("0", "0"), 400, "invalid_limit");
  rejects(() => store.pull("0", String(LIMITS.maxPullLimit + 1)), 400, "invalid_limit");
});

test("tokens: created once, authenticate by hash, carry scopes, expire and revoke", async () => {
  const { store, accountId, advance } = setup();
  const { token, info } = await store.createToken({ name: "PC nhà", expiresInDays: 30 });
  assert.deepEqual(parseToken(token), { accountId });
  assert.deepEqual(info.scopes, ["sync:read", "sync:write", "tokens:manage"]);

  const hash = await hashToken(token);
  assert.equal(store.authenticate(hash, "sync:write").id, info.id);
  assert.ok(store.listTokens()[0].lastUsedAt !== null);
  // The plaintext token is never stored.
  assert.ok(!JSON.stringify(store.listTokens()).includes(token));

  const readOnly = await store.createToken({ name: "viewer", scopes: ["sync:read"] });
  const readHash = await hashToken(readOnly.token);
  assert.equal(store.authenticate(readHash, "sync:read").name, "viewer");
  rejects(() => store.authenticate(readHash, "sync:write"), 403, "insufficient_scope");

  advance(31 * 86_400_000);
  rejects(() => store.authenticate(hash, "sync:read"), 401, "token_expired");

  assert.equal(store.revokeToken(readOnly.info.id), true);
  assert.equal(store.revokeToken(readOnly.info.id), false);
  rejects(() => store.authenticate(readHash, "sync:read"), 401, "invalid_token");
  rejects(() => store.authenticate("0".repeat(64), "sync:read"), 401, "invalid_token");
});

test("revoking all tokens locks every device out", async () => {
  const { store } = setup();
  const a = await store.createToken({ name: "a" });
  const b = await store.createToken({ name: "b" });
  const hashes = await Promise.all([a, b].map((t) => hashToken(t.token)));
  assert.equal(store.revokeAllTokens(), 2);
  for (const hash of hashes) rejects(() => store.authenticate(hash, "sync:read"), 401, "invalid_token");
});

test("token requests are validated", async () => {
  const { store } = setup();
  await assert.rejects(store.createToken({ name: "" }), (e: unknown) => e instanceof HttpError && e.code === "invalid_name");
  await assert.rejects(store.createToken({ name: "x", scopes: ["admin"] }), (e: unknown) => e instanceof HttpError && e.code === "invalid_scopes");
  await assert.rejects(store.createToken({ name: "x", expiresInDays: 0 }), (e: unknown) => e instanceof HttpError && e.code === "invalid_expires_in_days");
});

test("an account nobody created stays empty and answers like a bad token", () => {
  const store = new AccountStore(nodeSql());
  rejects(() => store.authenticate("0".repeat(64), "sync:read"), 401, "invalid_token");
  rejects(() => store.push({ items: [] }), 404, "account_not_found");
  assert.equal(store.initialized, false);
});

test("the keyring is compare-and-set too", () => {
  const { store } = setup();
  assert.equal(store.getKeyring(), null);
  assert.deepEqual(store.putKeyring({ baseVersion: 0, data: "wrapped-v1" }), { accepted: true, version: 1 });
  assert.deepEqual(store.putKeyring({ baseVersion: 0, data: "racing device" }), { accepted: false, version: 1 });
  assert.deepEqual(store.getKeyring(), { version: 1, data: "wrapped-v1" });
  rejects(() => store.putKeyring({ baseVersion: 1, data: "x".repeat(LIMITS.maxKeyringBytes + 1) }), 400, "invalid_keyring");
});

test("init twice is refused", () => {
  const { store, accountId } = setup();
  rejects(() => store.init(accountId, "again"), 409, "account_exists");
});

test("storage quota: growth beyond it rejects the whole batch, shrinking is always allowed", () => {
  const { store } = setup(1);
  const half = toBase64(new Uint8Array(512 * 1024));
  store.push({ items: [{ ...item("a", 0), payload: half }] });
  assert.equal(store.info().usedBytes, 512 * 1024);

  const more = toBase64(new Uint8Array(600 * 1024));
  rejects(() => store.push({ items: [item("small", 0), { ...item("b", 0), payload: more }] }), 413, "quota_exceeded");
  assert.equal(store.pull("0", null).records.length, 1, "nothing of the rejected batch is stored");
  assert.equal(store.info().usedBytes, 512 * 1024);

  // Replacing a record counts only the difference; a tombstone frees the space.
  store.push({ items: [{ ...item("a", 1), payload: toBase64(new Uint8Array(900 * 1024)) }] });
  assert.equal(store.info().usedBytes, 900 * 1024);
  store.push({ items: [item("a", 2, "x", true)] });
  assert.equal(store.info().usedBytes, 1);

  store.setQuota(2);
  assert.equal(store.info().quotaBytes, 2 * 1024 * 1024);
  rejects(() => store.setQuota(0), 400, "invalid_quota");
});

test("a device can create tokens for other devices, never with more scopes than it has", async () => {
  const { store } = setup();
  const manager = (await store.createToken({ name: "PC" })).info;
  const reader = (await store.createToken({ name: "viewer", scopes: ["sync:read", "tokens:manage"] })).info;

  const inherited = await store.createToken({ name: "Laptop" }, manager);
  assert.deepEqual(inherited.info.scopes, manager.scopes);

  const narrowed = await store.createToken({ name: "Phone", scopes: ["sync:read"] }, reader);
  assert.deepEqual(narrowed.info.scopes, ["sync:read"]);
  await assert.rejects(store.createToken({ name: "escalate", scopes: ["sync:write"] }, reader),
    (e: unknown) => e instanceof HttpError && e.code === "insufficient_scope");
  // Without explicit scopes a grantor passes on exactly its own.
  assert.deepEqual((await store.createToken({ name: "copy" }, reader)).info.scopes, ["sync:read", "tokens:manage"]);
});
