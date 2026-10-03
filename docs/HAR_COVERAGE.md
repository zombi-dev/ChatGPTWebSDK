# Supplied HAR coverage

The 2026-10-02 capture has 688 entries: 292 ChatGPT service requests, 362 frontend assets/telemetry requests, 10 Cloudflare browser resources and 24 external resources. Its 292 service requests normalize to the 55 operations below. The external signed upload PUT is supported by UploadFileAsync, which sends no ChatGPT authentication to the storage host. External image/static resources and preflight requests belong to browser resource loading. They are not independent SDK service operations.

All 55 operations have request-routing and parameter-validation tests. The 11 captured generation streams have sanitized decoder fixtures. Those tests establish SDK mapping and decoding, not that every account can successfully call every backend route. Status 0 means the capture did not receive a response; connector-logo 404s are preserved rather than presented as successful functionality. See VERIFICATION.md for independently exercised live features.

Use runtime.Web.Transport.CapturedOperations.SendJsonAsync with the operation ID for JSON, SendAsync for binary responses (dispose the response), and StreamAsync for SSE. Use the scoped ChatGptWebClient or official clients for conversation persistence. Generation through the captured-operation stream still obtains fresh Sentinel authorization. The catalog accepts caller-supplied parameters and bodies and never replays captured tokens, IDs or private payloads. Field names/query names and observed response status codes are exposed in WebCapturedOperationsClient.Operations.

| Operation ID | Method and path | Response | Requests | Captured status |
| --- | --- | --- | --- | --- |
| DeleteConversation | DELETE /backend-api/conversation/id/{conversation_id} | Json | 2 | 200 |
| GetAuthSession | GET /api/auth/session | Json | 2 | 200 |
| GetAccounts | GET /backend-api/accounts/check/v4-2023-04-27 | Json | 4 | 200 |
| GetOptimizedAccounts | GET /backend-api/accounts/optimized/check | Json | 2 | 200 |
| GetConnectorLogo | GET /backend-api/aip/connectors/{connector_id}/logo | Binary | 2 | 404 |
| GetConnectorEligibility | GET /backend-api/aip/first-party/eligibility | Json | 2 | 200 |
| ListNotifications | GET /backend-api/amphora/notifications | Json | 4 | 200 |
| GetHomeBeacons | GET /backend-api/beacons/home | Json | 5 | 0 |
| GetNotificationConnection | GET /backend-api/celsius/ws/user | Json | 7 | 200 |
| ListComposerItems | GET /backend-api/composer/items | Json | 2 | 200 |
| GetConversation | GET /backend-api/conversation/{conversation_id} | Json | 22 | 200, 404 |
| ListConversations | GET /backend-api/conversations | Json | 38 | 0, 200 |
| GetConversationTurns | GET /backend-api/conversations/{conversation_id} | Json | 3 | 200, 404 |
| DownloadEstuaryContent | GET /backend-api/estuary/content | Binary | 2 | 200, 304 |
| GetFile | GET /backend-api/files/{file_id}/simple | Json | 2 | 200 |
| GetFileDownloadInfo | GET /backend-api/files/download/{file_id} | Json | 2 | 200 |
| GetProject | GET /backend-api/gizmos/{project_id} | Json | 3 | 200 |
| ListProjectConversations | GET /backend-api/gizmos/{project_id}/conversations | Json | 14 | 0, 200 |
| ListProjects | GET /backend-api/gizmos/snorlax/sidebar | Json | 2 | 200 |
| GetConnectorPermissions | GET /backend-api/hazelnuts | Json | 2 | 200 |
| GetUser | GET /backend-api/me | Json | 3 | 200 |
| GetModels | GET /backend-api/models | Json | 2 | 200 |
| GetBillingPage | GET /backend-api/pageConfigs/billing | Json | 2 | 200 |
| ListPins | GET /backend-api/pins | Json | 10 | 200 |
| GetProfile | GET /backend-api/profiles/me | Json | 2 | 200 |
| ListProjectConnectorScopes | GET /backend-api/projects/{project_id}/connector_scopes | Json | 1 | 200 |
| ListProjectSaves | GET /backend-api/projects/{project_id}/saves | Json | 3 | 200 |
| GetPromptLibrary | GET /backend-api/prompt_library/ | Json | 2 | 200 |
| GetPluginsHome | GET /backend-api/ps/plugins/home | Json | 2 | 200 |
| ListInstalledPlugins | GET /backend-api/ps/plugins/installed | Json | 2 | 200 |
| GetAdultStatus | GET /backend-api/settings/is_adult | Json | 3 | 200 |
| GetUserSettings | GET /backend-api/settings/user | Json | 4 | 200 |
| GetSubscriptions | GET /backend-api/subscriptions | Json | 2 | 200 |
| GetSystemHints | GET /backend-api/system_hints | Json | 11 | 200 |
| GetDefaultTabRecommendation | GET /backend-api/tpp/default-tab-recommendation | Json | 2 | 200 |
| GetAlternateModels | GET /backend-api/tpp/models/ | Json | 2 | 200 |
| GetGranularConsent | GET /backend-api/user_granular_consent | Json | 2 | 200 |
| GetCodexAccounts | GET /backend-api/wham/accounts/check | Json | 3 | 200 |
| GetCodexSitesAccess | GET /backend-api/wham/sites/access | Json | 2 | 200 |
| ListCodexTasks | GET /backend-api/wham/tasks/list | Json | 18 | 200 |
| GetCodexUsage | GET /backend-api/wham/usage | Json | 5 | 200 |
| ListAccessibleConnectorLinks | POST /backend-api/aip/connectors/links/list_accessible | Json | 2 | 200 |
| ClearSettingsCache | POST /backend-api/amphora/clear_settings_cache | Json | 2 | 200 |
| InitializeConversation | POST /backend-api/conversation/init | Json | 14 | 0, 200 |
| GetConversationsBatch | POST /backend-api/conversations/batch | Json | 2 | 200 |
| GenerateConversation | POST /backend-api/f/conversation | EventStream | 11 | 200 |
| PrepareConversation | POST /backend-api/f/conversation/prepare | Json | 20 | 200 |
| CreateFile | POST /backend-api/files | Json | 1 | 200 |
| ProcessUpload | POST /backend-api/files/process_upload_stream | EventStream | 1 | 200 |
| SubmitComparisonFeedback | POST /backend-api/paragen_submission | Json | 1 | 200 |
| GetAppsBatch | POST /backend-api/ps/apps/batch | Json | 2 | 200 |
| FinalizeSentinel | POST /backend-api/sentinel/chat-requirements/finalize | Json | 13 | 200 |
| PrepareSentinel | POST /backend-api/sentinel/chat-requirements/prepare | Json | 13 | 200 |
| SubmitUserSignal | POST /backend-api/unified_user_signals | Json | 2 | 200 |
| SubmitCodexAnalytics | POST /backend-api/wham/analytics-events/events | Json | 3 | 200 |

User actions captured and supported: normal/project/temporary chat creation and append; edit branches; regeneration variants; image/document upload and processing; image input; image generation/editing and binary download; chat deletion; project/sidebar reads; comparison feedback. Other named transport methods expose conversation filters, settings, profiles, notifications, plugins, connectors, subscriptions and usage. Shared-conversation methods and title/archive operations additionally exist in the SDK, although this HAR did not record their complete mutation flows.

The capture contains no voice, realtime, project creation/deletion or project-move mutation sequence. Those behaviors are not advertised as verified from this HAR. Creating more mappings requires a new observed request sequence; the same-origin SendJsonAsync transport is available for callers who know an endpoint. BrowserOnly mode remains reserved. Platform-only API features continue to return explicit 501 errors on the web adapter.
