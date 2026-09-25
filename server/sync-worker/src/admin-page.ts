// The admin web page served at /admin. It only calls the admin API below it with the ADMIN_TOKEN the admin types
// in; the token lives in page memory (never in storage) and is gone on reload. All data is rendered with
// textContent, and the strict CSP allows no inline script or third-party origin.

const CSP = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self'",
  "connect-src 'self'",
  "img-src 'self' data:",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join("; ");

const SECURITY_HEADERS = {
  "Content-Security-Policy": CSP,
  "X-Frame-Options": "DENY",
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
  "Cache-Control": "no-store",
};

/** GET /admin, /admin/app.js, /admin/app.css; null for anything else. */
export function adminAsset(pathname: string): Response | null {
  const asset =
    pathname === "/admin" || pathname === "/admin/" ? { body: HTML, type: "text/html; charset=utf-8" }
    : pathname === "/admin/app.js" ? { body: JS, type: "text/javascript; charset=utf-8" }
    : pathname === "/admin/app.css" ? { body: CSS, type: "text/css; charset=utf-8" }
    : null;
  return asset ? new Response(asset.body, { headers: { "Content-Type": asset.type, ...SECURITY_HEADERS } }) : null;
}

const HTML = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>Helm Sync Admin</title>
<link rel="stylesheet" href="/admin/app.css">
<script src="/admin/app.js" defer></script>
</head>
<body>
<main>
  <header><h1>Helm Sync Admin</h1><button id="lock" class="ghost" hidden>Lock</button></header>

  <section id="login" class="card">
    <h2>Sign in</h2>
    <p>Enter the <code>ADMIN_TOKEN</code> secret of this Worker. It stays in this tab's memory only.</p>
    <div class="row">
      <input id="admin-token" type="password" autocomplete="off" spellcheck="false" placeholder="ADMIN_TOKEN">
      <button id="unlock">Unlock</button>
    </div>
    <p id="login-error" class="error" hidden></p>
  </section>

  <section id="app" hidden>
    <div class="card">
      <h2>Invites</h2>
      <p class="muted">An invite code lets one person create their own account in Helm → General → Sync. It works once.</p>
      <table><thead><tr><th>Note</th><th>Quota</th><th>Created</th><th>Expires</th><th>Status</th><th></th></tr></thead>
        <tbody id="invites"></tbody></table>
      <div class="row">
        <input id="invite-note" maxlength="100" placeholder="Note, e.g. for Minh">
        <input id="invite-days" type="number" min="1" max="90" placeholder="Valid for days (default 7)">
        <input id="invite-quota" type="number" min="1" placeholder="Quota MB (default 256)">
        <button id="create-invite">Create invite</button>
      </div>
      <div id="new-invite" class="reveal" hidden>
        <p><strong>Copy this invite code now. It is shown only once.</strong> Send it privately; it expires unused after the chosen days.</p>
        <div class="row"><code id="new-invite-value"></code><button id="copy-invite">Copy</button></div>
      </div>
    </div>

    <div class="card">
      <h2>Accounts</h2>
      <table><thead><tr><th>Name</th><th>Account id</th><th>Created</th><th>Status</th><th></th></tr></thead>
        <tbody id="accounts"></tbody></table>
      <div class="row">
        <input id="new-account-name" maxlength="100" placeholder="New account name, e.g. Anh">
        <input id="new-account-quota" type="number" min="1" placeholder="Quota MB (default 256)">
        <button id="create-account">Create account</button>
      </div>
    </div>

    <div id="account" class="card" hidden>
      <h2 id="account-title"></h2>
      <p class="muted" id="account-id"></p>
      <div class="row">
        <span id="account-usage"></span>
        <input id="account-quota" type="number" min="1" placeholder="New quota MB">
        <button id="save-quota" class="ghost">Set quota</button>
      </div>
      <table><thead><tr><th>Token</th><th>Scopes</th><th>Created</th><th>Expires</th><th>Last used</th><th>Status</th><th></th></tr></thead>
        <tbody id="tokens"></tbody></table>
      <h3>New token</h3>
      <div class="row">
        <input id="token-name" maxlength="100" placeholder="Device name, e.g. PC nhà">
        <input id="token-days" type="number" min="1" max="3650" placeholder="Expires in days (empty = never)">
        <label class="check"><input id="token-readonly" type="checkbox"> Read only</label>
        <button id="create-token">Create token</button>
      </div>
      <div id="new-token" class="reveal" hidden>
        <p><strong>Copy this token now. It is shown only once.</strong> Paste it into Helm → General → Sync on that device.</p>
        <div class="row"><code id="new-token-value"></code><button id="copy-token">Copy</button></div>
      </div>
      <div class="danger">
        <button id="disable-account" class="danger-button">Disable account (revoke every token)</button>
      </div>
    </div>
  </section>

  <p id="status" class="status" role="status"></p>
</main>
</body>
</html>`;

const CSS = `:root { color-scheme: light dark; --bg: #f6f7f9; --card: #fff; --text: #1b1f24; --muted: #5f6b7a; --line: #dde2e8;
  --accent: #2f6feb; --danger: #c93c37; --ok: #1f883d; }
@media (prefers-color-scheme: dark) { :root { --bg: #0f1216; --card: #171b21; --text: #e6e9ee; --muted: #9aa5b1; --line: #2a313a;
  --accent: #5b8def; --danger: #f06b66; --ok: #4ac26b; } }
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.5 system-ui, "Segoe UI", sans-serif; }
main { max-width: 1000px; margin: 0 auto; padding: 24px 16px 64px; }
header { display: flex; align-items: center; justify-content: space-between; }
h1 { font-size: 22px; margin: 0 0 16px; } h2 { font-size: 17px; margin: 0 0 12px; } h3 { font-size: 15px; margin: 20px 0 8px; }
.card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 18px; margin-bottom: 16px; overflow-x: auto; }
.row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-top: 12px; }
input { font: inherit; color: inherit; background: transparent; border: 1px solid var(--line); border-radius: 6px; padding: 7px 10px; min-width: 220px; flex: 1; }
input[type=checkbox] { min-width: 0; flex: none; }
.check { display: flex; gap: 6px; align-items: center; }
button { font: inherit; border: 0; border-radius: 6px; padding: 8px 14px; background: var(--accent); color: #fff; cursor: pointer; white-space: nowrap; }
button:disabled { opacity: .5; cursor: default; }
button.ghost { background: transparent; color: var(--muted); border: 1px solid var(--line); }
button.link { background: transparent; color: var(--accent); padding: 2px 6px; }
button.link.revoke { color: var(--danger); }
.danger { margin-top: 20px; padding-top: 12px; border-top: 1px solid var(--line); }
.danger-button { background: var(--danger); }
table { width: 100%; border-collapse: collapse; }
th, td { text-align: left; padding: 7px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
th { color: var(--muted); font-weight: 600; font-size: 12px; }
tr.selected td { background: color-mix(in srgb, var(--accent) 10%, transparent); }
code { font-family: ui-monospace, Consolas, monospace; font-size: 13px; word-break: break-all; }
.reveal { margin-top: 12px; padding: 12px; border: 1px solid var(--ok); border-radius: 8px; }
.reveal code { flex: 1; padding: 8px; background: var(--bg); border-radius: 6px; user-select: all; }
.muted { color: var(--muted); } .error { color: var(--danger); }
.badge { font-size: 12px; padding: 1px 8px; border-radius: 10px; border: 1px solid var(--line); }
.badge.ok { color: var(--ok); border-color: var(--ok); } .badge.bad { color: var(--danger); border-color: var(--danger); }
.status { position: fixed; left: 50%; bottom: 16px; transform: translateX(-50%); margin: 0; padding: 0 12px; color: var(--muted); }
`;

const JS = `"use strict";
(() => {
  let adminToken = "";
  let selected = null;
  const $ = (id) => document.getElementById(id);

  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    for (const [k, v] of Object.entries(props || {})) {
      if (k === "onclick") node.addEventListener("click", v);
      else if (k === "className") node.className = v;
      else node.setAttribute(k, v);
    }
    for (const c of children) node.append(c instanceof Node ? c : document.createTextNode(c == null ? "" : String(c)));
    return node;
  }

  const fmt = (ms) => (ms == null ? "" : new Date(ms).toLocaleString());
  const mb = (bytes) => (bytes / 1048576).toFixed(1);
  const optionalNumber = (id) => { const v = $(id).value.trim(); return v ? Number(v) : undefined; };
  const status = (text) => { $("status").textContent = text || ""; };

  async function api(method, path, body) {
    const res = await fetch(path, {
      method,
      headers: Object.assign({ Authorization: "Bearer " + adminToken }, body ? { "Content-Type": "application/json" } : {}),
      body: body ? JSON.stringify(body) : undefined,
      cache: "no-store",
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      const err = new Error(data.message || data.error || ("HTTP " + res.status));
      err.status = res.status;
      throw err;
    }
    return data;
  }

  async function run(button, work) {
    if (button) button.disabled = true;
    status("Working…");
    try { await work(); status(""); }
    catch (e) {
      if (e.status === 401) { lock(); showLoginError("The admin token was rejected."); return; }
      status("Error: " + e.message);
    }
    finally { if (button) button.disabled = false; }
  }

  function showLoginError(text) { const p = $("login-error"); p.textContent = text; p.hidden = !text; }

  function lock() {
    adminToken = ""; selected = null;
    $("new-invite").hidden = true; $("new-invite-value").textContent = "";
    $("admin-token").value = "";
    $("app").hidden = true; $("account").hidden = true; $("lock").hidden = true;
    $("login").hidden = false; $("new-token").hidden = true; $("new-token-value").textContent = "";
    $("admin-token").focus();
  }

  async function unlock() {
    const value = $("admin-token").value.trim();
    if (!value) return;
    adminToken = value;
    showLoginError("");
    await run($("unlock"), async () => {
      await Promise.all([loadAccounts(), loadInvites()]);
      $("login").hidden = true; $("app").hidden = false; $("lock").hidden = false;
      $("admin-token").value = "";
    });
  }

  async function loadAccounts() {
    const { accounts } = await api("GET", "/admin/accounts");
    const body = $("accounts");
    body.replaceChildren(...accounts.map((a) => {
      const row = el("tr", { className: selected === a.accountId ? "selected" : "" },
        el("td", {}, a.name),
        el("td", {}, el("code", {}, a.accountId)),
        el("td", {}, fmt(a.createdAt)),
        el("td", {}, a.disabledAt ? el("span", { className: "badge bad" }, "disabled") : el("span", { className: "badge ok" }, "active")),
        el("td", {}, el("button", { className: "link", onclick: () => run(null, () => openAccount(a)) }, "Manage")));
      return row;
    }));
    if (accounts.length === 0) body.append(el("tr", {}, el("td", { colspan: "5", className: "muted" }, "No accounts yet.")));
  }

  async function loadInvites() {
    const { invites } = await api("GET", "/admin/invites");
    const now = Date.now();
    const body = $("invites");
    body.replaceChildren(...invites.slice().reverse().map((i) => {
      const state = i.usedAt ? ["used", "ok"] : i.revokedAt ? ["revoked", "bad"] : i.expiresAt <= now ? ["expired", "bad"] : ["waiting", ""];
      const live = state[0] === "waiting";
      return el("tr", {},
        el("td", {}, i.note || "(no note)", el("div", { className: "muted" }, el("code", {}, i.id))),
        el("td", {}, i.quotaMb + " MB"),
        el("td", {}, fmt(i.createdAt)),
        el("td", {}, fmt(i.expiresAt)),
        el("td", {}, el("span", { className: "badge " + state[1] }, state[0]),
          i.usedByAccountId ? el("div", { className: "muted" }, el("code", {}, i.usedByAccountId)) : ""),
        el("td", {}, live ? el("button", { className: "link revoke", onclick: (e) => revokeInvite(i, e.currentTarget) }, "Revoke") : ""));
    }));
    if (invites.length === 0) body.append(el("tr", {}, el("td", { colspan: "6", className: "muted" }, "No invites yet.")));
  }

  async function createInvite() {
    const body = { note: $("invite-note").value.trim() };
    const days = optionalNumber("invite-days"); if (days !== undefined) body.days = days;
    const quota = optionalNumber("invite-quota"); if (quota !== undefined) body.quotaMb = quota;
    await run($("create-invite"), async () => {
      const created = await api("POST", "/admin/invites", body);
      $("invite-note").value = ""; $("invite-days").value = ""; $("invite-quota").value = "";
      $("new-invite-value").textContent = created.code;
      $("new-invite").hidden = false;
      await loadInvites();
    });
  }

  async function revokeInvite(invite, button) {
    if (!confirm("Revoke this invite? The code will stop working.")) return;
    await run(button, async () => {
      await api("DELETE", "/admin/invites/" + encodeURIComponent(invite.id));
      await loadInvites();
    });
  }

  async function loadUsage() {
    const info = await api("GET", "/admin/accounts/" + encodeURIComponent(selected));
    $("account-usage").textContent = "Storage: " + mb(info.usedBytes) + " of " + mb(info.quotaBytes) + " MB";
  }

  async function saveQuota() {
    const quotaMb = optionalNumber("account-quota");
    if (!quotaMb) return status("Enter the new quota in MB.");
    await run($("save-quota"), async () => {
      await api("PUT", "/admin/accounts/" + encodeURIComponent(selected) + "/quota", { quotaMb });
      $("account-quota").value = "";
      await loadUsage();
    });
  }

  async function openAccount(account) {
    selected = account.accountId;
    $("account-title").textContent = account.name;
    $("account-id").textContent = "Account " + account.accountId;
    $("new-token").hidden = true; $("new-token-value").textContent = "";
    $("account").hidden = false;
    await Promise.all([loadTokens(), loadAccounts(), loadUsage()]);
  }

  async function loadTokens() {
    const { tokens } = await api("GET", "/admin/accounts/" + encodeURIComponent(selected) + "/tokens");
    const now = Date.now();
    const body = $("tokens");
    body.replaceChildren(...tokens.slice().reverse().map((t) => {
      const state = t.revokedAt ? ["revoked", "bad"] : t.expiresAt && t.expiresAt <= now ? ["expired", "bad"] : ["active", "ok"];
      const action = t.revokedAt ? "" : el("button", { className: "link revoke", onclick: (e) => revoke(t, e.currentTarget) }, "Revoke");
      return el("tr", {},
        el("td", {}, t.name, el("div", { className: "muted" }, el("code", {}, t.id))),
        el("td", {}, t.scopes.join(" ")),
        el("td", {}, fmt(t.createdAt)),
        el("td", {}, t.expiresAt ? fmt(t.expiresAt) : "never"),
        el("td", {}, fmt(t.lastUsedAt)),
        el("td", {}, el("span", { className: "badge " + state[1] }, state[0])),
        el("td", {}, action));
    }));
    if (tokens.length === 0) body.append(el("tr", {}, el("td", { colspan: "7", className: "muted" }, "No tokens yet.")));
  }

  async function createAccount() {
    const name = $("new-account-name").value.trim();
    if (!name) return status("Enter a name for the account.");
    const body = { name };
    const quota = optionalNumber("new-account-quota"); if (quota !== undefined) body.quotaMb = quota;
    await run($("create-account"), async () => {
      const account = await api("POST", "/admin/accounts", body);
      $("new-account-name").value = ""; $("new-account-quota").value = "";
      await openAccount(account);
    });
  }

  async function createToken() {
    const name = $("token-name").value.trim();
    if (!name) return status("Enter a device name for the token.");
    const body = { name };
    const days = $("token-days").value.trim();
    if (days) body.expiresInDays = Number(days);
    if ($("token-readonly").checked) body.scopes = ["sync:read"];
    await run($("create-token"), async () => {
      const created = await api("POST", "/admin/accounts/" + encodeURIComponent(selected) + "/tokens", body);
      $("token-name").value = ""; $("token-days").value = ""; $("token-readonly").checked = false;
      $("new-token-value").textContent = created.token;
      $("new-token").hidden = false;
      await loadTokens();
    });
  }

  async function revoke(token, button) {
    if (!confirm("Revoke '" + token.name + "'? That device stops syncing immediately.")) return;
    await run(button, async () => {
      await api("DELETE", "/admin/accounts/" + encodeURIComponent(selected) + "/tokens/" + encodeURIComponent(token.id));
      await loadTokens();
    });
  }

  async function disableAccount() {
    if (!confirm("Disable this account? Every one of its tokens is revoked. Data is kept.")) return;
    await run($("disable-account"), async () => {
      await api("POST", "/admin/accounts/" + encodeURIComponent(selected) + "/disable");
      await Promise.all([loadTokens(), loadAccounts()]);
    });
  }

  async function copyText(id) {
    const value = $(id).textContent;
    try { await navigator.clipboard.writeText(value); status("Copied."); }
    catch { status("Select the token and copy it manually."); }
  }

  document.addEventListener("DOMContentLoaded", () => {
    $("unlock").addEventListener("click", unlock);
    $("admin-token").addEventListener("keydown", (e) => { if (e.key === "Enter") unlock(); });
    $("lock").addEventListener("click", lock);
    $("create-account").addEventListener("click", createAccount);
    $("create-token").addEventListener("click", createToken);
    $("copy-token").addEventListener("click", () => copyText("new-token-value"));
    $("copy-invite").addEventListener("click", () => copyText("new-invite-value"));
    $("create-invite").addEventListener("click", createInvite);
    $("save-quota").addEventListener("click", saveQuota);
    $("disable-account").addEventListener("click", disableAccount);
    $("admin-token").focus();
  });
})();
`;
