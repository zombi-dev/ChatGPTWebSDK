# OpenAI OAuth reference and adaptations

Source: https://github.com/EvanZhouDev/openai-oauth
Reviewed revision: 9079e62840a7f4b19114cb8f4bb0009b5bcf1773 (2026-10-11).
License: Apache-2.0. The upstream LICENSE and NOTICE are retained alongside this file.

The C# OAuth module adapts the authorization-code PKCE flow, token/claim handling, Codex request normalization, account-aware model capabilities, bounded in-memory response replay, image request normalization, and terminal SSE handling from packages/core and packages/local. It implements C# HTTP lifetimes, cancellation, isolated history, explicit credential-file persistence and Chat Completions translation for this SDK. The TypeScript package and its web application are not runtime dependencies.

The implementation adds callback/state validation, sanitized failures, stricter terminal-event requirements, cross-thread/account cache isolation, atomic credential writes and a total history size bound. It does not adopt automatic reads or writes of shared Codex credentials, raw error logging, detached daemons, or unrelated browser-extension authorization.
