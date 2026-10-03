/* Shared by Chromium's service worker and Firefox's event page. No credentials are persisted. */
"use strict";

(() => {
  const ORIGIN = "https://chatgpt.com";
  const PREFIX = "cgweb1.";
  const MAX_EXPORT_CHARACTERS = 512 * 1024;
  const busyTabs = new Set();
  const authenticationCookies = new Set([
    "oai-did", "oai-sc", "__Secure-oai-is", "oai-client-session-epoch", "_puid", "_account", "oai-wm",
    "cf_clearance", "__cf_bm", "_cfuvid", "__cflb", "__oailb"
  ]);
  const messages = Object.freeze({
    wrong_tab: "Open a signed-in ChatGPT tab, then click the extension.",
    signed_out: "Sign in to ChatGPT, then click the extension again.",
    session_failed: "Could not read your ChatGPT session. Reload ChatGPT and try again.",
    cookies_failed: "Allow this extension access to chatgpt.com, then try again.",
    clipboard_failed: "Could not copy. Focus your ChatGPT window and click the extension again.",
    export_failed: "Could not export this session. Reload ChatGPT and try again."
  });

  function isChatGPT(url) {
    try { return new URL(url).origin === ORIGIN; } catch { return false; }
  }

  function jwtExpiry(token) {
    try {
      const parts = token.split(".");
      if (parts.length !== 3) return null;
      const payload = JSON.parse(atob(parts[1].replace(/-/g, "+").replace(/_/g, "/")));
      return Number.isSafeInteger(payload.exp) ? new Date(payload.exp * 1000).toISOString() : null;
    } catch { return null; }
  }

  function encodeExport(payload) {
    const bytes = new TextEncoder().encode(JSON.stringify(payload));
    let binary = "";
    for (let offset = 0; offset < bytes.length; offset += 16384) {
      binary += String.fromCharCode(...bytes.subarray(offset, offset + 16384));
    }
    const value = PREFIX + btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    if (value.length > MAX_EXPORT_CHARACTERS) throw new Error("export_failed");
    return value;
  }

  function buildExport(session, cookies, now = new Date()) {
    if (!session || typeof session.accessToken !== "string" || !session.accessToken.trim()) throw new Error("signed_out");
    if (!isChatGPT(session.origin) || typeof session.userAgent !== "string" || !session.userAgent) throw new Error("export_failed");
    const filtered = cookies.filter(cookie =>
      (authenticationCookies.has(cookie.name) || /^__(?:Secure|Host)-(?:next-auth|authjs)\.(?:session-token(?:\.\d+)?|csrf-token)$/.test(cookie.name)) &&
      (cookie.domain === "chatgpt.com" || cookie.domain === ".chatgpt.com") &&
      (!cookie.partitionKey || cookie.partitionKey.topLevelSite === ORIGIN && !cookie.partitionKey.hasCrossSiteAncestor) &&
      (!cookie.expirationDate || cookie.expirationDate * 1000 > now.getTime())
    ).map(cookie => ({
      name: cookie.name, value: cookie.value, domain: cookie.domain, path: cookie.path || "/",
      hostOnly: !!cookie.hostOnly, secure: !!cookie.secure, httpOnly: !!cookie.httpOnly,
      sameSite: cookie.sameSite || "unspecified",
      expiresAt: Number.isFinite(cookie.expirationDate) ? new Date(cookie.expirationDate * 1000).toISOString() : null
    }));
    const headers = {};
    const device = filtered.find(cookie => cookie.name === "oai-did");
    if (device) headers["oai-device-id"] = device.value;
    if (session.language) {
      headers["oai-language"] = session.language;
      headers["accept-language"] = session.language;
    }
    if (session.clientHints) {
      headers["sec-ch-ua"] = session.clientHints.brands.map(brand =>
        '"' + brand.brand.replace(/["\\]/g, "\\$&") + '";v="' + brand.version.replace(/["\\]/g, "\\$&") + '"'
      ).join(", ");
      headers["sec-ch-ua-mobile"] = session.clientHints.mobile ? "?1" : "?0";
      headers["sec-ch-ua-platform"] = '"' + session.clientHints.platform.replace(/["\\]/g, "\\$&") + '"';
    }
    return encodeExport({
      version: 1, origin: ORIGIN, createdAt: now.toISOString(), accessToken: session.accessToken,
      expiresAt: jwtExpiry(session.accessToken) || session.expiresAt || null,
      userAgent: session.userAgent, headers, cookies: filtered
    });
  }

  // These injected functions must be self-contained: executeScript serializes their source.
  async function readSessionInPage() {
    if (location.origin !== "https://chatgpt.com") return { error: "wrong_tab" };
    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(), 15000);
    try {
      const response = await fetch("/api/auth/session", { credentials: "include", cache: "no-store", signal: abort.signal });
      if (!response.ok) return { error: response.status === 401 ? "signed_out" : "session_failed" };
      const session = await response.json();
      if (typeof session.accessToken !== "string" || !session.accessToken) return { error: "signed_out" };
      return {
        origin: location.origin, accessToken: session.accessToken, expiresAt: session.expires || null,
        userAgent: navigator.userAgent, language: navigator.language,
        clientHints: navigator.userAgentData ? {
          brands: navigator.userAgentData.brands, mobile: navigator.userAgentData.mobile, platform: navigator.userAgentData.platform
        } : null
      };
    } catch { return { error: "session_failed" }; }
    finally { clearTimeout(timer); }
  }

  async function copyAndToastInPage(authenticationString, errorMessage = null) {
    if (location.origin !== "https://chatgpt.com") return { error: "wrong_tab" };
    let message = errorMessage;
    let copied = false;
    if (!message) {
      try {
        await navigator.clipboard.writeText(authenticationString);
        copied = true;
        message = "Copied to clipboard!";
      } catch {
        message = "Could not copy. Focus your ChatGPT window and click the extension again.";
      }
    }
    document.querySelector("[data-chatgpt-web-sdk-toast]")?.remove();
    const host = document.createElement("div");
    host.setAttribute("data-chatgpt-web-sdk-toast", "");
    host.style.cssText = "position:fixed;top:20px;right:20px;z-index:2147483647;pointer-events:none";
    const root = host.attachShadow({ mode: "closed" });
    const toast = document.createElement("div");
    toast.setAttribute("role", "status");
    toast.setAttribute("aria-live", "polite");
    toast.textContent = message;
    toast.style.cssText = "padding:14px 20px;border:1px solid #555;border-radius:12px;background:#202123;color:#fff;box-shadow:0 4px 24px #0005;font:14px/1.5 system-ui,sans-serif;max-width:340px";
    root.append(toast);
    document.documentElement.append(host);
    setTimeout(() => host.remove(), 3500);
    return copied ? { copied: true } : { error: errorMessage ? "export_failed" : "clipboard_failed" };
  }

  async function readCookies(api, tab) {
    const stores = await api.cookies.getAllCookieStores();
    const storeId = tab.cookieStoreId || stores.find(store => store.tabIds.includes(tab.id))?.id;
    if (!storeId || !stores.some(store => store.id === storeId && store.tabIds.includes(tab.id))) throw new Error("cookies_failed");
    const details = { domain: "chatgpt.com", storeId };
    // Empty partitionKey selects all partitions in Chrome; buildExport keeps only the current first-party site.
    if (typeof api.cookies.getPartitionKey === "function") details.partitionKey = {};
    return api.cookies.getAll(details);
  }

  async function handleClick(api, tab) {
    if (!tab || !Number.isInteger(tab.id) || !isChatGPT(tab.url)) {
      await api.action.setBadgeText({ tabId: tab?.id, text: "!" });
      await api.action.setTitle({ tabId: tab?.id, title: messages.wrong_tab });
      return { error: "wrong_tab" };
    }
    if (busyTabs.has(tab.id)) return { error: "busy" };
    busyTabs.add(tab.id);
    let failure = "session_failed";
    try {
      await api.action.setBadgeText({ tabId: tab.id, text: "" });
      const results = await api.scripting.executeScript({ target: { tabId: tab.id }, func: readSessionInPage });
      const session = results.find(result => result.frameId === 0)?.result;
      if (session?.error) { failure = session.error; throw new Error(failure); }
      failure = "cookies_failed";
      const cookies = await readCookies(api, tab);
      failure = "export_failed";
      const authenticationString = buildExport(session, cookies);
      failure = "clipboard_failed";
      const copied = await api.scripting.executeScript({
        target: { tabId: tab.id }, func: copyAndToastInPage, args: [authenticationString]
      });
      if (copied.find(result => result.frameId === 0)?.result?.copied !== true) throw new Error(failure);
      await api.action.setTitle({ tabId: tab.id, title: "ChatGPT Web SDK Auth — copied" });
      return { copied: true };
    } catch {
      const message = messages[failure] || messages.export_failed;
      await api.action.setBadgeText({ tabId: tab.id, text: "!" }).catch(() => {});
      await api.action.setTitle({ tabId: tab.id, title: message }).catch(() => {});
      // Clipboard failures already displayed a toast. Never display the caught exception or any credentials.
      if (failure !== "clipboard_failed") {
        await api.scripting.executeScript({ target: { tabId: tab.id }, func: copyAndToastInPage, args: [null, message] }).catch(() => {});
      }
      return { error: failure };
    } finally { busyTabs.delete(tab.id); }
  }

  globalThis.ChatGPTWebAuth = Object.freeze({
    handleClick, buildExport, encodeExport, isChatGPT, jwtExpiry, readSessionInPage, copyAndToastInPage, readCookies
  });
})();
