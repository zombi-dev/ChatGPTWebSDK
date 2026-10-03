# HAR import and additional protocol captures

The supplied 2026-10-02 capture already includes 688 entries and 11 generation streams, covering images, edits, regeneration, temporary chats and deletion. Those flows do not require another HAR for structural implementation. A HAR cannot guarantee acceptance of SDK HTTP traffic or replace fresh Sentinel answers.

For future changes, start with a **sanitized HAR with response content**, created using a disposable test conversation. Preserve URLs, JSON field names, message/conversation IDs and SSE structure. Remove unrelated personal content. An authenticated capture containing real cookies/tokens should remain local; the importer can consume it without printing secrets.

## Baseline capture procedure

1. Open your signed-in ChatGPT tab, then open Developer Tools > Network.
2. Enable Preserve log and clear the existing requests.
3. Reload the ChatGPT page to capture session, account and model requests.
4. Start a new conversation. Send: Remember this marker for the next turn: SDK-TEST-42.
5. Wait for the response to finish.
6. Send: What marker did I give you in the previous message?
7. Wait for the response to finish, then export the Network log as HAR with response content.
8. Include the individual streaming response/event data separately if the HAR does not retain it. A HAR entry showing only an empty streaming response is insufficient to validate the decoder.

Keep the session/model requests, every requirements/preflight request, both conversation POST payloads, their streaming responses, and the final conversation-history GET. Do not remove intermediate preparation requests; they may be necessary for the current handshake.

Chrome exports sanitized HARs that omit Cookie, Set-Cookie and Authorization headers. That is appropriate for protocol analysis. For local authentication import, Chrome exposes an optional sensitive-data export through Settings > Preferences > Network > Allow to generate HAR with sensitive data. Use that export locally or provide credentials separately through the local configuration; do not paste them into chat. See [Chrome's Network reference](https://developer.chrome.com/docs/devtools/network/reference/#save-all-network-requests-to-a-har-file).

## Additional captures for broader web mappings

Separate disposable captures make these flows easier to interpret:

| Feature | Capture |
| --- | --- |
| Conversation management | Reload/read a conversation, rename it, archive/unarchive it; use a disposable conversation for deletion |
| Image/PDF attachment | Upload a harmless small image or PDF, wait for upload confirmation, send it with a text question, capture the answer and download URL requests |
| Image generation | Generate a harmless test image, capture the generation events and image retrieval |
| Search or tool use | Submit a simple search request, capture the selected tool/mode fields, citation events and final output |
| Custom GPT/project context | Create a conversation using the target GPT/project and capture selection plus follow-up |
| Voice/audio | Separate recording, transcription, response playback and any WebSocket frames |
| Shares | Create and revoke a link for a disposable conversation, including both create/update requests |

The first text capture is the prerequisite. Platform-only resources such as fine-tuning cannot be mapped just by adding a ChatGPT conversation payload.

## Local inspection and import

~~~powershell
dotnet run --project tools/ChatGPTWebSdk.Cli -- inspect-har C:\path\chatgpt.har
dotnet run --project tools/ChatGPTWebSdk.Cli -- import-har C:\path\chatgpt.har .\local.session.json
~~~

Inspection prints only request metadata and credential-presence booleans. Import creates a fresh proxy configuration and refuses to overwrite an existing file. It never replays captured requests.

A sanitized file generally cannot authenticate the SDK. The importer extracts credentials only when they are actually present, from HTTPS requests on chatgpt.com or chat.openai.com. It imports stable device/language/browser-context headers and selected protocol defaults. Prompts, old conversation IDs, attachments and transient challenge/proof tokens are excluded from the reusable profile.

## Explicit live smoke test

~~~powershell
# Read-only authenticated models request.
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke .\local.session.json

# Creates and retains a new two-turn test conversation.
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke .\local.session.json --send AVAILABLE_WEB_MODEL_SLUG
~~~

The --send test checks remembered context, matching conversation IDs, and independently retrieves the remote graph to verify both assistant messages. It does not delete the test conversation. For restart acceptance, rerun the proxy with the same session directory and use the same client key/thread to append a new message.

If the server requires a fresh preparation body, proof token or another transport, retain that exact request/response sequence in the capture. The default hybrid mode opens and cleans up a temporary visible browser for fresh Sentinel answers. ApiOnly reports unmet challenge requirements and does not automatically open a browser. BrowserOnly is reserved and unimplemented.


The `smoke-sdk` command repeats the same live acceptance using the actual official OpenAI client types. `resume-sdk <config> <saved-sdk-smoke-thread> <web-model-slug>` checks remembered context and independent remote history after a runtime restart.
