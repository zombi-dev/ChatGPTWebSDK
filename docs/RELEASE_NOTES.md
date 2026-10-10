# v1.5.0

CI fixes, dependency updates and quieter compatibility monitoring.

## Fixes

- Handle Windows line endings in workflow checks and synchronization.
- Check missing release tags through the Git references endpoint.
- Separate browser download, native checks and archiving in CI.
- Warn on blocked monitor checks without opening false change issues.

## Dependencies

- Include Dependabot #1: updated github-script pin.
- Include Dependabot #3: updated .NET client and test dependencies.
- Preserve OpenAI 2.14 streaming signatures after the ClientModel type rename.

## Maintenance

- Use focused, categorized Conventional Commits and concise release notes.
- Rebuild the main history while preserving published release tags.
