// SigV4 query-string signing, checked against the example in the AWS S3 documentation
// ("Authenticating Requests: Using Query Parameters").

import assert from "node:assert/strict";
import { test } from "node:test";
import { presign } from "../src/presign.ts";

const aws = {
  host: "examplebucket.s3.amazonaws.com",
  region: "us-east-1",
  accessKeyId: "AKIAIOSFODNN7EXAMPLE",
  secretAccessKey: "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
  now: new Date(Date.UTC(2013, 4, 24)),
  expiresSeconds: 86400,
};

test("a presigned GET matches the AWS documentation example", async () => {
  const url = await presign({ ...aws, method: "GET", path: "/test.txt" });
  assert.equal(url,
    "https://examplebucket.s3.amazonaws.com/test.txt?X-Amz-Algorithm=AWS4-HMAC-SHA256"
    + "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request&X-Amz-Date=20130524T000000Z"
    + "&X-Amz-Expires=86400&X-Amz-SignedHeaders=host"
    + "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404");
});

test("a presigned PUT signs the content length, so another size gets another signature", async () => {
  const a = await presign({ ...aws, method: "PUT", path: "/b/blobs/acc/01ARZ3NDEKTSV4RRFFQ69G5FAV/0", contentLength: 100 });
  const b = await presign({ ...aws, method: "PUT", path: "/b/blobs/acc/01ARZ3NDEKTSV4RRFFQ69G5FAV/0", contentLength: 101 });
  assert.match(a, /X-Amz-SignedHeaders=content-length%3Bhost/);
  assert.match(a, /^https:\/\/examplebucket\.s3\.amazonaws\.com\/b\/blobs\/acc\/01ARZ3NDEKTSV4RRFFQ69G5FAV\/0\?/);
  assert.notEqual(new URL(a).searchParams.get("X-Amz-Signature"), new URL(b).searchParams.get("X-Amz-Signature"));
});
