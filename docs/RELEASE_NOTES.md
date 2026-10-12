# v1.7.0

## Up to 100× faster Sentinel startup

ChatGPT web generation now defaults to **ApiOnly**, a native C# HTTP Sentinel implementation. It obtains fresh requirements, solves proof of work and decodes supported challenges without starting Chromium or Python. A complete cold authenticated handshake measured **0.89 seconds**, with three warm handshakes at **0.38–0.42 seconds**. The headline compares this path with the up-to-100-second browser waits reported in the live example; it describes Sentinel preparation, and total response time still depends on the service and network.

## Features and compatibility

- Add `HttpSentinelProvider` for direct web clients and select it automatically in both the drop-in and native OpenAI runtimes, proxy, CLI and quick-start example. Signed-in access, append-only remote conversations, SSE and MCP use the existing shared conversation implementation.
- Rename the previous `ApiOnly` mode to **ExternalApi**. Keep externally supplied session/challenge providers, explicit **Hybrid** browser assistance and reserved **BrowserOnly**. Preserve compiled enum values 0, 1 and 2; the new native `ApiOnly` is 3. Source consumers should rebuild to adopt the new initializer default.
- Default native `ApiOnly` to managed .NET HTTP; retain system curl defaults for `Hybrid` and `ExternalApi`, with explicit driver overrides. The daily UI monitor selects Hybrid explicitly so it continues observing frontend changes.

## Performance and safeguards

- Read build and location metadata from ChatGPT itself, eliminating the reference client's two external geolocation requests. Cache metadata separately by account and credentials for 30 minutes, reuse HTTP connections and keep `WarmUpAsync()` free of proof collection.
- Decode bounded symbolic instructions directly instead of generating JavaScript and parsing an AST. The same captured challenge measured **3.1 ms** median warm C# decoding versus **1.3 seconds** for the Python reference. Pre-serialize fixed proof fields and bound instruction depth, size and CPU work; no downloaded code executes.
- Obtain fresh answers for every turn, preserve credential redaction and never replay proofs or automatically repeat uncertain generations. Unsupported formats and Cloudflare blocks remain explicit errors; the application can select Hybrid or ExternalApi when needed.

## Verification and downloads

- Add **100 core regression cases**, **four native integration cases** and one workflow check. The gate requires **1,584 core/drop-in, 22 native, 168 automation and 12 extension checks**. Tests on all four platforms and native browser bundle verification precede publication.
- A fresh authenticated live run passed a collected reply, an SSE follow-up with only the new message, an independent remote conversation read and a temporary-chat MCP tool loop. The test conversation was deleted afterwards. The extension export and challenge fixtures stay private.
- Synchronize all four NuGet packages, DLL/proxy archives, Chromium/Firefox extensions and four browser bundles at **1.7.0**. The OpenAI compatibility assembly remains **2.14.0.0**. The existing OAuth and official-platform backends retain their separate authentication contracts.

See the [verification record](https://github.com/zombi-dev/ChatGPTWebSDK/blob/v1.7.0/docs/VERIFICATION.md) for timings, protocol scope and remaining live-test limits. All commits are linked in the collapsed appendix below.
