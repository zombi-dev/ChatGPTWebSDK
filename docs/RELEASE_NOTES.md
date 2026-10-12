# v1.6.1

Fix MCP tool requests wrapped in `<escape>...</escape>` becoming literal final answers on follow-ups. Valid wrapped requests now execute through the existing tool pipeline, while malformed requests reach bounded protocol repair. This closes a prefix-recognition gap also present in v1.5.1; it is not identified as a new v1.6.0 regression.

## Fixes

- Buffer partial escape tags and tool prefixes across SSE snapshots. Keep wrapped requests out of final-answer text, streaming deltas and the visible transcript while the tool exchange completes.
- Require the current nonce, matching tool markers and a closing escape tag before validating the complete JSON batch. Ordinary escaped text and commands quoted in prose or explicitly marked final answers retain their original text.
- Apply the existing hidden correction budget to malformed JSON, incomplete wrappers and stale wrapped markers. Strict mode still rejects invalid requests without executing tools; repeated failures stop at the configured limit.
- Preserve application approval, progress events and durable outcome records. Confirmed calls execute once; uncertain calls remain pending and block continuation until explicitly resolved.

## Validation and downloads

- Add 83 regression cases covering fresh chats, follow-ups, mixed and resubmitted history, stream boundaries, three-call batches, approval and uncertain outcomes. Typed Chat Completions and Responses clients are checked with both streaming and collected results.
- Require at least 1,444 core/drop-in SDK cases plus 18 native SDK, 167 automation and 12 extension cases. Four platform test jobs and all bundled-browser checks remain required before publication.
- Update SDK packages, browser extensions, installation instructions and download links to 1.6.1. The OpenAI compatibility assembly retains version 2.14.0.0.

See the [verification record](https://github.com/zombi-dev/ChatGPTWebSDK/blob/v1.6.1/docs/VERIFICATION.md) for reproduction and test details. All commits are linked in the collapsed appendix below.
