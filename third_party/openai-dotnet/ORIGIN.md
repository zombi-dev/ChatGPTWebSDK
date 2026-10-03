Runtime C# source from https://github.com/openai/openai-dotnet at commit
`2e77b08828145f658ec04e49aec87abb1543c553` (OpenAI_2.14.0 release tag; retrieved 2026-10-02).

The original MIT license is preserved in LICENSE. This is an independent,
unofficial fork. OpenAI does not support or endorse its ChatGPT web transport.

Public clients, constructors, models, serializers and method signatures are
retained. Local patches are restricted to transport selection in
OpenAIClientUtilities and guarding the direct Realtime WebSocket entry point
when web transport is active. Web behavior lives outside the upstream source.
The replacement is for source compatibility when replacing the OpenAI NuGet
reference. It is unsigned; binary replacement of strongly named assemblies
and coexistence with the official OpenAI package are not supported.
