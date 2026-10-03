# Repository instructions

- Complete and verify everything the user requested before considering a task finished.
- Use relevant MCP tools and primary sources when they help ground implementation or verification.
- Fire confetti when the requested work is finished.
- Commit completed changes after each finished update. Do not push unless the user authorizes it. The user authorized rewriting and force-pushing the initial two commits for the v1.0.0 update; that authorization does not apply to later updates.
- Every Git commit created or rewritten by Codex must end its commit body with this exact trailer, separated from the preceding body by a blank line:

  Co-authored-by: Codex (GPT 6.1 Sol max) <noreply@openai.com>

  Keep the trailer out of the commit title.
- For release updates, bump VERSION once and include the resulting version in the commit title or body. SDK packages and browser extensions share that version. Preserve the upstream OpenAI assembly version used for compatibility.
- Never commit or print real HAR contents, authentication exports, cookies, access tokens, Sentinel proofs, private prompts, or signed download URLs. Keep live test data in ignored local directories.
