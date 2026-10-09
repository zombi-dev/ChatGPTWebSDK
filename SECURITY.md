# Security policy

Security fixes target the latest release. Upgrade older versions before reporting a problem that has already been fixed. BrowserOnly is reserved and has no supported implementation.

## Report a vulnerability

Use [GitHub's private vulnerability reporting](https://github.com/zombi-dev/ChatGPTWebSDK/security/advisories/new) when available. If the private form is unavailable, open an issue asking for a private reporting channel without disclosing the vulnerability. Do not put exploit details or credentials in a public issue.

Include the affected version, platform, relevant configuration with credentials removed, impact, and a minimal reproduction. Never attach an original HAR, authentication export, cookie, bearer token, Sentinel proof, private conversation or signed download URL. Use synthetic data and redacted traces.

## Credential and application boundaries

The extension's copied string grants access to the signed-in ChatGPT session. Store it like a password, restrict its use to trusted processes and rotate the session if it leaks. The extension exports credentials only after an explicit toolbar click and copies them locally. The SDK's MCP registrations may contain credentials and can execute tools; register trusted servers and use allowlists and approval callbacks appropriate to your application.

Bind each proxy key to its account and application user, use TLS for remote access and keep the proxy private unless you deliberately deploy it. Conversation isolation is an application boundary; it does not make multiple application users independent ChatGPT accounts.

## Automation

Pull requests receive read-only test permissions and no ChatGPT monitor credentials. Build is a reusable workflow called only after all tests pass. Release publishing receives repository write permission but has no checkout, executes no downloaded code and validates the exact default-branch test run and its artifact set. The daily monitor uses a dedicated CHATGPT_WEB_MONITOR_AUTH repository secret in its default-branch probe job; the issue-writing job receives only sanitized report data and no ChatGPT credentials.

This unofficial SDK depends on private ChatGPT web behavior. Access checks and service protections remain in effect. A passing offline test suite cannot establish current access for every account or server.
