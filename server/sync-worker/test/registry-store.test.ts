import assert from "node:assert/strict";
import { test } from "node:test";
import { RegistryStore } from "../src/registry-store.ts";
import { HttpError } from "../src/sql.ts";
import { createInviteCode, isInviteCode } from "../src/token.ts";
import { nodeSql } from "./node-sql.ts";

function setup() {
  let now = 1_800_000_000_000;
  const store = new RegistryStore(nodeSql(), () => now);
  return { store, advance: (ms: number) => (now += ms) };
}

const invalid = (e: unknown) => e instanceof HttpError && e.status === 400 && e.code === "invalid_invite";

test("invite codes are well formed and checksummed", () => {
  const code = createInviteCode();
  assert.match(code, /^helm_inv_[0-9A-Za-z]{46}$/);
  assert.equal(isInviteCode(code), true);
  assert.equal(isInviteCode(code.slice(0, -1) + (code.endsWith("a") ? "b" : "a")), false);
  assert.equal(isInviteCode("helm_pat_" + code.slice(9)), false);
});

test("an invite creates exactly one account", () => {
  const { store } = setup();
  store.createInvite("inv_1", "hash-1", { note: "for Minh", days: 7, quotaMb: 50 });

  assert.deepEqual(store.redeem("hash-1", "AAAAAAAAAAAAAAAA", "Minh"), { quotaMb: 50 });
  assert.throws(() => store.redeem("hash-1", "BBBBBBBBBBBBBBBB", "Minh again"), invalid);

  const [invite] = store.listInvites();
  assert.equal(invite.usedByAccountId, "AAAAAAAAAAAAAAAA");
  assert.deepEqual(store.list().map((a) => a.name), ["Minh"]);
  assert.equal(store.revokeInvite("inv_1"), false, "a used invite cannot be revoked");
});

test("expired, revoked and unknown invites fail the same way and create nothing", () => {
  const { store, advance } = setup();
  store.createInvite("inv_old", "hash-old", { note: "", days: 1, quotaMb: 10 });
  store.createInvite("inv_off", "hash-off", { note: "", days: 7, quotaMb: 10 });
  assert.equal(store.revokeInvite("inv_off"), true);
  advance(2 * 86_400_000);

  assert.throws(() => store.redeem("hash-old", "AAAAAAAAAAAAAAAA", "x"), invalid);
  assert.throws(() => store.redeem("hash-off", "BBBBBBBBBBBBBBBB", "x"), invalid);
  assert.throws(() => store.redeem("nope", "CCCCCCCCCCCCCCCC", "x"), invalid);
  assert.equal(store.list().length, 0);
});

const gone = (e: unknown) => e instanceof HttpError && e.status === 404 && e.code === "pairing_not_found";
const offer = { publicKey: "QUJD", deviceName: "fairy-box", pickupHash: "pickup-hash" };

test("a pairing goes offer, read, seal, pick up, and is then gone", () => {
  const { store } = setup();
  const { expiresAt } = store.offerPairing("id-1", offer);
  assert.equal(expiresAt, 1_800_000_000_000 + 5 * 60_000);
  assert.deepEqual(store.readPairing("id-1"), { publicKey: "QUJD", deviceName: "fairy-box", expiresAt });
  assert.equal(store.pickUpPairing("id-1", "pickup-hash"), null, "nothing sealed yet: the new device keeps waiting");

  store.sealPairing("id-1", "AAAAAAAAAAAAAAAA", "c2VhbGVk");
  assert.throws(() => store.readPairing("id-1"), gone, "a sealed pairing is no longer offered");
  assert.throws(() => store.sealPairing("id-1", "BBBBBBBBBBBBBBBB", "b3RoZXI="),
    (e: unknown) => e instanceof HttpError && e.status === 409, "the first seal wins");

  assert.deepEqual(store.pickUpPairing("id-1", "pickup-hash"), { sealed: "c2VhbGVk", accountId: "AAAAAAAAAAAAAAAA" });
  assert.throws(() => store.pickUpPairing("id-1", "pickup-hash"), gone, "picked up once");
});

test("only the pickup secret picks a pairing up", () => {
  const { store } = setup();
  store.offerPairing("id-1", offer);
  store.sealPairing("id-1", "AAAAAAAAAAAAAAAA", "c2VhbGVk");
  assert.throws(() => store.pickUpPairing("id-1", "someone-else"), gone);
  assert.deepEqual(store.pickUpPairing("id-1", "pickup-hash"), { sealed: "c2VhbGVk", accountId: "AAAAAAAAAAAAAAAA" });
});

test("a pairing lives five minutes and its code cannot be offered twice meanwhile", () => {
  const { store, advance } = setup();
  store.offerPairing("id-1", offer);
  assert.throws(() => store.offerPairing("id-1", offer), (e: unknown) => e instanceof HttpError && e.status === 409);
  advance(5 * 60_000);
  assert.throws(() => store.readPairing("id-1"), gone);
  assert.throws(() => store.sealPairing("id-1", "AAAAAAAAAAAAAAAA", "c2VhbGVk"), gone);
  assert.throws(() => store.pickUpPairing("id-1", "pickup-hash"), gone);
  store.offerPairing("id-1", offer); // expired rows are cleared, so the id is free again
  assert.equal(store.readPairing("id-1").deviceName, "fairy-box");
});

test("unknown pairings fail the same way", () => {
  const { store } = setup();
  assert.throws(() => store.readPairing("nope"), gone);
  assert.throws(() => store.sealPairing("nope", "AAAAAAAAAAAAAAAA", "c2VhbGVk"), gone);
  assert.throws(() => store.pickUpPairing("nope", "pickup-hash"), gone);
});
