# Official specification provenance

- Source: https://raw.githubusercontent.com/openai/openai-openapi/refs/heads/main/openapi.yaml
- Retrieved: 2026-10-02
- Upstream commit associated with the retrieved file: 145fd3b2e30e3ac87a4ed915913f6fa1217974fa
- Spec info.version: 2.3.0
- SHA-256 of untouched YAML: 2cf225eb2eb480bf7d57e04e3e7d3198fe7f47c49bc5defb53bf86966706c5f9
- Generated REST operations: 353

The operation manifest is embedded in the SDK. It includes method/path, required path/query/header parameters, request-body presence, request/response media types and deprecation flags. Generated C# methods route through that manifest.

The core operation client provides generic REST coverage with JsonNode or HttpContent; it is not a local JSON-schema validator. The separate OpenAI replacement package preserves the released official SDK's schema-specific C# models and public clients. Web compatibility is separately constrained and documented.

The supplied YAML has an invalid literal-block indentation in a schema example around line 75569. The generator excludes components.schemas from its metadata parsing projection; paths and the relevant referenced components remain. The source fingerprint always uses the untouched bytes. No schema content is modified or silently republished.

## Reproduce the pinned output

~~~powershell
New-Item -ItemType Directory -Force .references | Out-Null
Invoke-WebRequest -Uri https://raw.githubusercontent.com/openai/openai-openapi/145fd3b2e30e3ac87a4ed915913f6fa1217974fa/openapi.yaml -OutFile .references\openapi.yaml
dotnet run --project tools/ChatGPTWebSdk.SpecGen -- .references\openapi.yaml .
dotnet test ChatGPTWebSdk.sln -c Release
~~~

The generator uses YamlDotNet only at development time. Generated artifacts have no runtime dependency on it.

The tests enumerate every generated REST operation and exercise its actual HTTP routing, media handling and required parameters. They also check the pinned source fingerprint. Regenerating against a new upstream revision requires reviewing changes and updating the intentional snapshot assertion; passing tests against the old snapshot is not evidence of coverage of a future revision.

The spec describes the platform REST API. ChatGPT web backend routes are not part of that document. Realtime REST session/call operations are included; a WebSocket media transport is not implemented.

