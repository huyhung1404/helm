// Presigned R2 URLs (AWS Signature Version 4, query-string form), so blob chunks travel straight between the device
// and R2 instead of through the Worker. Needs an R2 API token with Object Read & Write on the blob bucket, set as
// R2_ACCESS_KEY_ID / R2_SECRET_ACCESS_KEY secrets plus the R2_ACCOUNT_ID variable (see README.md).
//
// The device never learns the credentials, only URLs for the exact keys the account's Durable Object authorized,
// valid for PRESIGN_SECONDS. An upload URL also signs the Content-Length, so R2 refuses a body of another size.

import type { Env } from "./objects.ts";

export const PRESIGN_SECONDS = 600;

/** True when the Worker can presign R2 URLs (the three R2 settings are there). */
export function presignConfigured(env: Env): boolean {
  return Boolean(env.R2_ACCOUNT_ID && env.R2_ACCESS_KEY_ID && env.R2_SECRET_ACCESS_KEY);
}

export interface PresignRequest {
  method: "GET" | "PUT";
  host: string;
  /** Absolute path, not yet encoded: "/bucket/key/with/slashes". */
  path: string;
  region: string;
  accessKeyId: string;
  secretAccessKey: string;
  now: Date;
  expiresSeconds: number;
  /** Signed when set: the PUT must send exactly this Content-Length. */
  contentLength?: number;
}

/** The URL for one R2 object of the blob bucket. */
export function presignR2(env: Env, method: "GET" | "PUT", key: string, now: Date, contentLength?: number): Promise<string> {
  return presign({
    method,
    host: `${env.R2_ACCOUNT_ID}.r2.cloudflarestorage.com`,
    path: `/${env.R2_BLOBS_BUCKET || "helm-sync-blobs"}/${key}`,
    region: "auto",
    accessKeyId: env.R2_ACCESS_KEY_ID!,
    secretAccessKey: env.R2_SECRET_ACCESS_KEY!,
    now,
    expiresSeconds: PRESIGN_SECONDS,
    contentLength,
  });
}

export async function presign(r: PresignRequest): Promise<string> {
  const amzDate = r.now.toISOString().replace(/[-:]/g, "").replace(/\.\d{3}/, "");
  const day = amzDate.slice(0, 8);
  const scope = `${day}/${r.region}/s3/aws4_request`;
  const signedHeaders = r.contentLength === undefined ? "host" : "content-length;host";
  const query = [
    ["X-Amz-Algorithm", "AWS4-HMAC-SHA256"],
    ["X-Amz-Credential", `${r.accessKeyId}/${scope}`],
    ["X-Amz-Date", amzDate],
    ["X-Amz-Expires", String(r.expiresSeconds)],
    ["X-Amz-SignedHeaders", signedHeaders],
  ]
    .map(([k, v]) => `${encode(k)}=${encode(v)}`)
    .sort()
    .join("&");
  const path = r.path.split("/").map(encode).join("/");
  const headers = (r.contentLength === undefined ? "" : `content-length:${r.contentLength}\n`) + `host:${r.host}\n`;
  const canonical = [r.method, path, query, headers, signedHeaders, "UNSIGNED-PAYLOAD"].join("\n");
  const toSign = ["AWS4-HMAC-SHA256", amzDate, scope, hex(await sha256(canonical))].join("\n");

  let key = await hmac(new TextEncoder().encode("AWS4" + r.secretAccessKey), day);
  for (const part of [r.region, "s3", "aws4_request"]) key = await hmac(key, part);
  const signature = hex(await hmac(key, toSign));
  return `https://${r.host}${path}?${query}&X-Amz-Signature=${signature}`;
}

/** RFC 3986 percent-encoding, as SigV4 requires (encodeURIComponent leaves !'()* alone). */
function encode(text: string): string {
  return encodeURIComponent(text).replace(/[!'()*]/g, (c) => "%" + c.charCodeAt(0).toString(16).toUpperCase());
}

async function hmac(key: Uint8Array, text: string): Promise<Uint8Array> {
  const cryptoKey = await crypto.subtle.importKey("raw", key, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return new Uint8Array(await crypto.subtle.sign("HMAC", cryptoKey, new TextEncoder().encode(text)));
}

async function sha256(text: string): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)));
}

function hex(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}
