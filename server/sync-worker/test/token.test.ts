import assert from "node:assert/strict";
import { test } from "node:test";
import { checksum, crc32, createToken, newAccountId, parseToken, randomBase62, secretsEqual } from "../src/token.ts";

test("tokens round-trip and carry their account id", () => {
  const accountId = newAccountId();
  const token = createToken(accountId);
  assert.match(token, /^helm_pat_[0-9A-Za-z]{16}_[0-9A-Za-z]{46}$/);
  assert.deepEqual(parseToken(token), { accountId });
});

test("a single changed character breaks the checksum", () => {
  const token = createToken(newAccountId());
  const i = token.length - 10;
  const typo = token.slice(0, i) + (token[i] === "a" ? "b" : "a") + token.slice(i + 1);
  assert.equal(parseToken(typo), null);
  assert.equal(parseToken("ghp_" + token.slice(9)), null);
  assert.equal(parseToken(""), null);
});

test("crc32 matches the standard check value", () => {
  assert.equal(crc32(new TextEncoder().encode("123456789")), 0xcbf43926);
});

test("checksum vector shared with the C# client", () => {
  // SyncTokenTests in tests/Helm.Tests asserts the same literal.
  assert.equal(checksum("helm_pat_0123456789abcdef_" + "A".repeat(40)), "0MfsOR");
});

test("random base62 uses the whole alphabet and the requested length", () => {
  const sample = randomBase62(20000);
  assert.equal(sample.length, 20000);
  assert.equal(new Set(sample).size, 62);
});

test("secretsEqual compares by value", async () => {
  assert.equal(await secretsEqual("a".repeat(40), "a".repeat(40)), true);
  assert.equal(await secretsEqual("a".repeat(40), "a".repeat(39) + "b"), false);
  assert.equal(await secretsEqual("short", "a".repeat(40)), false);
});
