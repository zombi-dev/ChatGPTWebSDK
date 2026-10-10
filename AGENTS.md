# Repository instructions

- Complete and verify everything the user requested before considering a task finished.
- Use relevant MCP tools and primary sources when they help ground implementation or verification.
- Fire confetti when the requested work is finished.
- Commit completed changes after each finished update. Do not push unless the user authorizes it. The user authorized rewriting and force-pushing the initial two commits for the v1.0.0 update; that authorization does not apply to later updates.
- Every Git commit created or rewritten by Codex must end its commit body with this exact trailer, separated from the preceding body by a blank line:

  Co-authored-by: Codex (GPT 6.1 Sol max) <noreply@openai.com>

  Keep the trailer out of the commit title.
- For release updates, bump VERSION once and include the resulting version in the commit title or body. SDK packages and browser extensions share that version. Preserve the upstream OpenAI assembly version used for compatibility.
- Use short Conventional Commit titles: `type(scope): summary`. Always include a category/component scope, including `security` for security fixes, `deps` for dependency updates and `ci` or a platform for CI fixes. Keep each commit focused; split independent code, tests, documentation and packaging changes. Avoid filler or empty commits.
- Keep commit bodies empty or very short, apart from the required Codex co-author trailer. Use `chore(release): X.Y.Z` for version checkpoints. Keep release titles to `vX.Y.Z`. Release bodies should have a summary and categorized changelog with roughly 1.5 times the detail of the earlier brief notes. Put all linked commits inside a closed-by-default `<details>` section. Explain skipped internal versions at the top, including that they were not publicly released and their changes are included. See CONTRIBUTING.md.
- Never commit or print real HAR contents, authentication exports, cookies, access tokens, Sentinel proofs, private prompts, or signed download URLs. Keep live test data in ignored local directories.
