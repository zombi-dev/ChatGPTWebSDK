#nullable enable
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using ChatGPTWebSdk.OAuth;

#if CHATGPTWEB_NATIVE
using OpenAI;
namespace ChatGPTWebSdk.Native;
#else
namespace OpenAI;
#endif

/// <summary>Explicit browser-free Codex OAuth clients, separate from linked ChatGPT website conversations.</summary>
public sealed class ChatGPTCodex : IDisposable
{
    private readonly HttpClient _compatibility;
    private readonly HttpClientPipelineTransport _transport;
    private bool _disposed;
    public ChatGPTCodex(IOAuthSessionProvider sessions, CodexOAuthOptions? options = null, HttpClient? upstream = null)
    {
        _compatibility = new(new CodexOAuthHttpHandler(sessions, options, upstream)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _transport = new(_compatibility);
    }
    public OpenAIClient CreateClient(string threadId = "default")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var options = new OpenAIClientOptions { Transport = _transport, RetryPolicy = new ClientRetryPolicy(0) };
        options.AddPolicy(new ThreadPolicy(threadId), PipelinePosition.PerCall);
        return new(new ApiKeyCredential("local-oauth-transport"), options);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _compatibility.Dispose(); }
    private sealed class ThreadPolicy(string threadId) : PipelinePolicy
    {
        private void Set(PipelineMessage message) => message.Request.Headers.Set("X-ChatGPT-Thread-Id", threadId);
        public override void Process(PipelineMessage message, System.Collections.Generic.IReadOnlyList<PipelinePolicy> pipeline, int index)
        { Set(message); ProcessNext(message, pipeline, index); }
        public override System.Threading.Tasks.ValueTask ProcessAsync(PipelineMessage message, System.Collections.Generic.IReadOnlyList<PipelinePolicy> pipeline, int index)
        { Set(message); return ProcessNextAsync(message, pipeline, index); }
    }
}
