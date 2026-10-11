#nullable enable
using System;
using System.ClientModel;
using System.ClientModel.Primitives;

#if CHATGPTWEB_NATIVE
using OpenAI;
namespace ChatGPTWebSdk.Native;
#else
namespace OpenAI;
#endif

/// <summary>Creates explicit official API clients alongside web and Codex clients, without changing global defaults.</summary>
public static class ChatGPTOfficial
{
    public static OpenAIClient CreateClient(string apiKey, OpenAIClientOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return CreateClient(new ApiKeyCredential(apiKey), options);
    }

    public static OpenAIClient CreateClient(ApiKeyCredential credential, OpenAIClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new(credential, CreateClientOptions(options));
    }

    /// <summary>Use the returned fresh options with individual official client constructors. Supplied options are configured in place.</summary>
    public static OpenAIClientOptions CreateClientOptions(OpenAIClientOptions? options = null)
    {
        options ??= new();
        // An explicit native transport also opts out of the drop-in package's global web defaults.
        options.Transport ??= HttpClientPipelineTransport.Shared;
        return options;
    }
}
