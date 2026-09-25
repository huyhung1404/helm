// Helm sync tokens, modelled on GitHub personal access tokens:
//
//   helm_pat_<accountId:16>_<secret:40><checksum:6>        (all base62)
//
// - The account id routes the request to that account's Durable Object; the secret (~238 bits) proves possession.
// - The checksum is CRC32 of everything before it, so clients catch typos offline and secret scanners can recognise
//   leaked tokens without asking the server. It is not a security feature: anyone can compute it.
// - The server stores only SHA-256(token). A token is shown exactly once, when it is created.
// Keep in sync with src/Helm.Core/Sync/SyncToken.cs.

export const TOKEN_PREFIX = "helm_pat_";
export const INVITE_PREFIX = "helm_inv_";
const BASE62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
const ACCOUNT_ID_LENGTH = 16;
const SECRET_LENGTH = 40;
const CHECKSUM_LENGTH = 6;
const TOKEN_PATTERN = new RegExp(
  `^${TOKEN_PREFIX}([0-9A-Za-z]{${ACCOUNT_ID_LENGTH}})_([0-9A-Za-z]{${SECRET_LENGTH}})([0-9A-Za-z]{${CHECKSUM_LENGTH}})$`,
);
const ACCOUNT_ID_PATTERN = new RegExp(`^[0-9A-Za-z]{${ACCOUNT_ID_LENGTH}}$`);
const INVITE_PATTERN = new RegExp(`^${INVITE_PREFIX}([0-9A-Za-z]{${SECRET_LENGTH}})([0-9A-Za-z]{${CHECKSUM_LENGTH}})$`);

/** Uniform random base62 string (rejection sampling, so no character is more likely than another). */
export function randomBase62(length: number): string {
  let out = "";
  const bytes = new Uint8Array(length * 2);
  while (out.length < length) {
    crypto.getRandomValues(bytes);
    for (const b of bytes) {
      if (b < 248) out += BASE62[b % 62]; // 248 = 62 * 4
      if (out.length === length) break;
    }
  }
  return out;
}

export function newAccountId(): string {
  return randomBase62(ACCOUNT_ID_LENGTH);
}

export function isAccountId(value: string): boolean {
  return ACCOUNT_ID_PATTERN.test(value);
}

export function createToken(accountId: string): string {
  if (!isAccountId(accountId)) throw new Error("invalid account id");
  const body = `${TOKEN_PREFIX}${accountId}_${randomBase62(SECRET_LENGTH)}`;
  return body + checksum(body);
}

/** The account a well-formed token belongs to, or null when the format or checksum is wrong. */
export function parseToken(token: string): { accountId: string } | null {
  const match = TOKEN_PATTERN.exec(token);
  if (!match) return null;
  const body = token.slice(0, token.length - CHECKSUM_LENGTH);
  if (checksum(body) !== match[3]) return null;
  return { accountId: match[1] };
}

/**
 * Single-use invite codes: helm_inv_<secret:40><checksum:6>. Whoever redeems one gets a new account and its first
 * token; the registry stores only the SHA-256 of the code.
 */
export function createInviteCode(): string {
  const body = INVITE_PREFIX + randomBase62(SECRET_LENGTH);
  return body + checksum(body);
}

export function isInviteCode(code: string): boolean {
  const match = INVITE_PATTERN.exec(code);
  return match !== null && checksum(code.slice(0, code.length - CHECKSUM_LENGTH)) === match[2];
}

export async function hashToken(token: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(token));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export function checksum(text: string): string {
  let value = crc32(new TextEncoder().encode(text));
  let out = "";
  for (let i = 0; i < CHECKSUM_LENGTH; i++) {
    out = BASE62[value % 62] + out;
    value = Math.floor(value / 62);
  }
  return out;
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

/** CRC-32/ISO-HDLC (the zlib/PNG one). */
export function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (const b of bytes) crc = CRC_TABLE[(crc ^ b) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

/** Constant-time comparison of two strings via their SHA-256 digests (lengths may differ). */
export async function secretsEqual(a: string, b: string): Promise<boolean> {
  const [x, y] = await Promise.all([hashToken(a), hashToken(b)]);
  let diff = 0;
  for (let i = 0; i < x.length; i++) diff |= x.charCodeAt(i) ^ y.charCodeAt(i);
  return diff === 0;
}
