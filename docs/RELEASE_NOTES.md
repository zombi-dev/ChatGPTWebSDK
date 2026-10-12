# v1.6.2

Hide a leading `<escape>[[final:NONCE]]</escape>` before an MCP-assisted answer. The answer now reaches streamed output, returned responses and the saved visible conversation without the escaped protocol prefix. This fixes final-answer formatting after successful tool execution; the escaped tool-request support introduced in v1.6.1 remains in place.

## Fixes

- Recognize the current exchange's final marker when only that marker is wrapped in escape tags and the answer follows the closing tag. Preserve the answer's original whitespace, Unicode and subsequent literal text.
- Buffer partial opening tags, final markers and closing tags across SSE snapshots. Apply the same normalization to every cumulative snapshot, including the completed response used for persistence, so a later update cannot reintroduce the prefix.
- Preserve quoted markers in prose or explicitly marked answers, ordinary escaped text and text that does not form a complete matching wrapped final marker. The decoder removes the leading protocol wrapper rather than replacing matching strings throughout an answer.
- Keep application approval, bounded repair and durable tool outcomes on their existing paths. Formatting cleanup uses the current response and requires neither another generation nor repeated tool execution.

## Validation and downloads

- Add 40 regression cases covering fresh chats, follow-ups, mixed and resubmitted history, six stream chunk sizes, saved responses, visible history, empty answers, partial wrappers, quoted text and replacement updates. Typed Chat Completions and Responses clients are checked with streamed and collected output.
- Require at least 1,484 core/drop-in SDK cases, plus 18 native SDK, 167 automation and 12 extension cases: 1,681 passing checks in the local gate. Four platform test jobs and all bundled-browser checks remain required before publication.
- Synchronize SDK packages, Chromium and Firefox extensions, install instructions and download links at 1.6.2. The OpenAI compatibility assembly retains version 2.14.0.0.

See the [verification record](https://github.com/zombi-dev/ChatGPTWebSDK/blob/v1.6.2/docs/VERIFICATION.md) for the deterministic binary probe and test scope. Fresh live generation, OAuth, images and the full historical edit/regenerate suite were not rerun for this formatting patch. All commits are linked in the collapsed appendix below.
