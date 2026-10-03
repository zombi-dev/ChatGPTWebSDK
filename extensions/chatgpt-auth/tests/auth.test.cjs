const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
require("../auth.js");
const auth = globalThis.ChatGPTWebAuth;
const token = "eyJhbGciOiJub25lIn0." + Buffer.from(JSON.stringify({ sub: "synthetic-user", exp: 2524608000 })).toString("base64url") + ".synthetic";
const session = { origin: "https://chatgpt.com", accessToken: token, expiresAt: "2099-01-01T00:00:00Z", userAgent: "Synthetic Firefox/150", language: "en-GB" };
const cookie = { name: "__Secure-next-auth.session-token", value: "synthetic-session", domain: ".chatgpt.com", path: "/", httpOnly: true, secure: true, hostOnly: false, sameSite: "lax", storeId: "container" };
const decode = value => JSON.parse(Buffer.from(value.slice(auth.PREFIX?.length || 7), "base64url").toString("utf8"));
const now = new Date("2026-10-03T12:00:00Z");

test("exports only authentication, preserves HttpOnly cookies, and uses JWT rather than session expiry", () => {
  const payload = decode(auth.buildExport({ ...session, email: "private@example.test", sentinelToken: "not-exported" }, [cookie], now));
  assert.equal(payload.version, 1);
  assert.equal(payload.expiresAt, "2050-01-01T00:00:00.000Z");
  assert.equal(payload.cookies[0].httpOnly, true);
  assert.equal(payload.cookies[0].secure, true);
  assert.equal(payload.cookies[0].domain, ".chatgpt.com");
  assert.equal(JSON.stringify(payload).includes("private@example.test"), false);
  assert.equal(JSON.stringify(payload).includes("not-exported"), false);
  assert.equal(payload.cookies[0].storeId, undefined);
});

test("isolates cookie domains, first-party partitions and expired cookies", () => {
  const cookies = [cookie, { ...cookie, domain: "auth.openai.com" },
    { ...cookie, domain: "evil.chatgpt.com" },
    { ...cookie, partitionKey: { topLevelSite: "https://example.test" } },
    { ...cookie, partitionKey: { topLevelSite: session.origin, hasCrossSiteAncestor: true } },
    { ...cookie, name: "cf_clearance", partitionKey: { topLevelSite: session.origin } },
    { ...cookie, expirationDate: 1 }, { ...cookie, name: "conv_key_private-conversation" }, { ...cookie, name: "g_state" }];
  assert.deepEqual(decode(auth.buildExport(session, cookies, now)).cookies.map(c => c.name), [cookie.name, "cf_clearance"]);
});

test("unicode client hints survive UTF-8 encoding and device cookie supplies the context header", () => {
  const payload = decode(auth.buildExport({ ...session, clientHints: {
    brands: [{ brand: "Chromium", version: "153" }, { brand: "Ünicode Browser", version: "1" }], platform: "Windows", mobile: false
  } }, [cookie, { ...cookie, name: "oai-did", value: "synthetic-device" }], now));
  assert.match(payload.headers["sec-ch-ua"], /Ünicode Browser/);
  assert.equal(payload.headers["oai-device-id"], "synthetic-device");
  assert.equal(payload.headers["sec-ch-ua-mobile"], "?0");
});

test("only the exact HTTPS ChatGPT origin is accepted", () => {
  for (const value of ["http://chatgpt.com", "https://evil.chatgpt.com", "https://chatgpt.com.evil.test", "file:///chatgpt.com"]) assert.equal(auth.isChatGPT(value), false);
  assert.equal(auth.isChatGPT("https://chatgpt.com/c/example"), true);
  assert.throws(() => auth.buildExport({ ...session, accessToken: "" }, [], now), /signed_out/);
  assert.throws(() => auth.encodeExport({ oversized: "x".repeat(512 * 1024) }), /export_failed/);
});

test("active Firefox containers and Chromium incognito stores determine which cookies are read", async () => {
  let details;
  const api = { cookies: {
    getAllCookieStores: async () => [{ id: "default", tabIds: [1] }, { id: "container", tabIds: [2] }],
    getAll: async value => { details = value; return [cookie]; }
  } };
  await auth.readCookies(api, { id: 2, cookieStoreId: "container" });
  assert.deepEqual(details, { domain: "chatgpt.com", storeId: "container" });
  await auth.readCookies(api, { id: 1 });
  assert.equal(details.storeId, "default");
  await assert.rejects(auth.readCookies(api, { id: 2, cookieStoreId: "default" }), /cookies_failed/);
});

function apiFixture(result = { copied: true }) {
  const calls = [];
  return { calls, action: {
    setBadgeText: async value => calls.push({ badge: value }),
    setTitle: async value => calls.push({ title: value })
  }, cookies: {
    getAllCookieStores: async () => [{ id: "container", tabIds: [42] }],
    getAll: async value => { calls.push({ cookies: value }); return [cookie]; }
  }, scripting: {
    executeScript: async value => {
      calls.push({ script: value });
      return [{ frameId: 0, result: value.func === auth.readSessionInPage ? session : result }];
    }
  } };
}

test("one toolbar click reads session, copies exactly one export and reports success", async () => {
  const api = apiFixture();
  assert.deepEqual(await auth.handleClick(api, { id: 42, url: session.origin }), { copied: true });
  const copy = api.calls.find(c => c.script?.func === auth.copyAndToastInPage);
  assert.equal(copy.script.args.length, 1);
  assert.equal(decode(copy.script.args[0]).accessToken, token);
  assert.equal(api.calls.some(c => c.title?.title.includes("copied")), true);
});

test("clipboard rejection never reports success and does not repeat the copy", async () => {
  const api = apiFixture({ error: "clipboard_failed" });
  assert.deepEqual(await auth.handleClick(api, { id: 42, url: session.origin }), { error: "clipboard_failed" });
  assert.equal(api.calls.filter(c => c.script?.func === auth.copyAndToastInPage).length, 1);
  assert.equal(api.calls.some(c => c.title?.title.endsWith("— copied")), false);
});

test("wrong tabs never read credentials or inject scripts", async () => {
  const api = apiFixture();
  assert.deepEqual(await auth.handleClick(api, { id: 42, url: "https://example.test" }), { error: "wrong_tab" });
  assert.equal(api.calls.some(c => c.script || c.cookies), false);
});

test("a signed-out session displays a safe error without reading cookies", async () => {
  const api = apiFixture();
  api.scripting.executeScript = async value => {
    api.calls.push({ script: value });
    return [{ frameId: 0, result: { error: "signed_out" } }];
  };
  assert.deepEqual(await auth.handleClick(api, { id: 42, url: session.origin }), { error: "signed_out" });
  assert.equal(api.calls.some(c => c.cookies), false);
  const errorToast = api.calls.find(c => c.script?.func === auth.copyAndToastInPage);
  assert.deepEqual(errorToast.script.args, [null, "Sign in to ChatGPT, then click the extension again."]);
});

test("the actual injected copy function displays the exact toast only after clipboard success", async () => {
  const saved = { location: global.location, navigator: Object.getOwnPropertyDescriptor(global, "navigator"), document: global.document, setTimeout: global.setTimeout };
  const nodes = [];
  global.location = { origin: session.origin };
  Object.defineProperty(global, "navigator", { value: { clipboard: { writeText: async value => assert.equal(value, "synthetic-export") } }, configurable: true });
  global.document = { querySelector: () => null, createElement: () => {
    const node = { setAttribute() {}, style: {}, attachShadow: () => ({ append: child => nodes.push(child) }), remove() {} };
    return node;
  }, documentElement: { append() {} } };
  global.setTimeout = () => 1;
  try {
    assert.deepEqual(await auth.copyAndToastInPage("synthetic-export"), { copied: true });
    assert.equal(nodes[0].textContent, "Copied to clipboard!");
    navigator.clipboard.writeText = async () => { throw new Error("synthetic private error"); };
    assert.deepEqual(await auth.copyAndToastInPage("synthetic-export"), { error: "clipboard_failed" });
    assert.equal(nodes[1].textContent.includes("synthetic private error"), false);
    assert.notEqual(nodes[1].textContent, "Copied to clipboard!");
  } finally {
    global.location = saved.location; global.document = saved.document; global.setTimeout = saved.setTimeout;
    if (saved.navigator) Object.defineProperty(global, "navigator", saved.navigator); else delete global.navigator;
  }
});

test("the checked-in synthetic export fixture is byte-for-byte generated by the extension and consumed by C#", () => {
  const fixture = JSON.parse(fs.readFileSync(path.resolve(__dirname, "../../../tests/ChatGPTWebSdk.Tests/Fixtures/auth-export.json"), "utf8"));
  assert.equal(fixture.authenticationString, auth.buildExport(session, [cookie], now));
});

test("release version, browser manifests and package metadata stay synchronized", () => {
  const version = fs.readFileSync(path.resolve(__dirname, "../../../VERSION"), "utf8").trim();
  for (const name of ["manifest.chromium.json", "manifest.firefox.json", "package.json"]) {
    assert.equal(JSON.parse(fs.readFileSync(path.resolve(__dirname, "../" + name), "utf8")).version, version);
  }
});
