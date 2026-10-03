"use strict";
// Chromium loads the shared code in its service worker; Firefox lists it first in background.scripts.
if (typeof importScripts === "function") importScripts("auth.js");
const extensionApi = typeof browser !== "undefined" ? browser : chrome;
extensionApi.action.onClicked.addListener(tab => {
  // All expected failures are redacted and shown in-page/by the action badge.
  ChatGPTWebAuth.handleClick(extensionApi, tab).catch(() => {});
});
