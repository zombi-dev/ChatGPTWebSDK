# v1.5.1

A patch release for fuller changelogs and updated GitHub Actions dependencies. Release pages now explain the changes in more detail while keeping the complete commit history folded away until requested.

## Release notes

- Add a categorized appendix containing every commit in the update, with a direct link to each commit. The section stays collapsed when readers first open the release page.
- Put a version note at the top when an internal release checkpoint was never publicly released, and explain that its changes are included in the next public update.
- Match historical release boundaries by source tree after a history rebuild, preserving the original published tags and download targets.
- Use the same formatter for automatic workflow publication and manual releases.
- Expand the existing public changelogs, including the note that internal v1.4.0 shipped through v1.5.0.

## Dependencies

- Include [Dependabot #4](https://github.com/zombi-dev/ChatGPTWebSDK/pull/4): setup-node 7.1.0, upload-artifact 7.0.2 and download-artifact 8.0.2, pinned to exact commits.

## Build and CI

- Run Apple Silicon tests and native browser verification on GitHub's macOS 15 ARM image after repeated macOS 26 runner stalls. Automatic and software rendering checks remain required before publishing a browser bundle.
- Accept release versions followed by sentence punctuation in commit bodies, while rejecting partial matches against other versions. The workflow now uses the tested commit validator for both title and version checks.

## Validation and downloads

- Run 1,199 SDK tests, 158 automation tests and 12 extension tests before building release assets on each of the four supported platforms.
- Publish synchronized SDK, proxy and extension packages, plus native Chromium bundles for Windows, Linux, Intel macOS and Apple Silicon, with SHA256 checksums.
