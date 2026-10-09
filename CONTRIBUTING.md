# Contributing

Use `type(scope): summary` commit titles, up to 100 characters. Keep bodies to a sentence when needed. Each commit should describe one focused change; split independent implementation, tests, docs and packaging work.

| Type | Use |
| --- | --- |
| feat | Features |
| fix | Bug fixes |
| perf | Performance |
| refactor | Internal changes |
| test | Test coverage |
| docs | Documentation |
| build | Dependencies and packaging |
| ci | Workflows |
| chore | Repository maintenance and releases |
| style | Formatting |
| revert | Reverting a change |

Always include a scope. Use `security` for security fixes and `deps` for dependency updates; otherwise name the affected feature or platform. Examples: `fix(security): validate release provenance`, `ci(windows): support CRLF checkouts`, `feat(mcp): scope servers to one message`, `build(deps): update test tooling`.

Codex commits end with this trailer, separated by a blank line:

```text
Co-authored-by: Codex (GPT 6.1 Sol max) <noreply@openai.com>
```

For a release, update the shared version and use `chore(release): X.Y.Z`. Keep `docs/RELEASE_NOTES.md` brief: one optional summary, then categorized bullets such as **Features**, **Fixes**, **Security**, **Dependencies** and **Maintenance**. Release titles are just `vX.Y.Z`. Existing published release tags remain fixed.

Run `./test.ps1` before packaging. Repository pushes check the commit title, release version and full test suite before build artifacts can be published. Keep private captures, authentication and generated build output out of commits.
