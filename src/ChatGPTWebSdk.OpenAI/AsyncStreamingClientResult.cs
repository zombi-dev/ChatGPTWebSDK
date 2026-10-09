using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.ServerSentEvents;
using System.Threading;
using System.Threading.Tasks;
using System.ClientModel.Primitives;

namespace System.ClientModel;

// ClientModel 1.16 renamed these experimental types. Keep the OpenAI 2.14
// signatures available while using the supported streaming implementation.
[Experimental("SCME0005")]
public static class AsyncStreamingClientResult
{
    public static AsyncStreamingClientResult<T> Create<T>(PipelineResponse response,
        Func<Stream, CancellationToken, IAsyncEnumerable<T>> producer,
        CancellationToken operationCancellationToken = default) =>
        new(AsyncStreamingResult.Create(response, producer, operationCancellationToken));

    public static AsyncStreamingClientResult<SseItem<T>> CreateSse<T>(PipelineResponse response,
        SseItemParser<T> itemParser, Func<SseItem<BinaryData>, bool> isTerminal = null,
        CancellationToken operationCancellationToken = default) =>
        new(AsyncStreamingResult.CreateSse(response, itemParser, isTerminal, operationCancellationToken));

    public static AsyncStreamingClientResult<SseItem<BinaryData>> CreateSse(PipelineResponse response,
        Func<SseItem<BinaryData>, bool> isTerminal = null,
        CancellationToken operationCancellationToken = default) =>
        new(AsyncStreamingResult.CreateSse(response, isTerminal, operationCancellationToken));

    public static AsyncStreamingClientResult<T> CreateJsonLines<T>(PipelineResponse response,
        Func<BinaryData, T> itemParser, CancellationToken operationCancellationToken = default) =>
        new(AsyncStreamingResult.CreateJsonLines(response, itemParser, operationCancellationToken));

    public static AsyncStreamingClientResult<BinaryData> CreateJsonLines(PipelineResponse response,
        CancellationToken operationCancellationToken = default) =>
        new(AsyncStreamingResult.CreateJsonLines(response, operationCancellationToken));
}

[Experimental("SCME0005")]
public sealed class AsyncStreamingClientResult<T> : IAsyncEnumerable<T>, IAsyncDisposable
{
    private readonly AsyncStreamingResult<T> _inner;

    internal AsyncStreamingClientResult(AsyncStreamingResult<T> inner) => _inner = inner;

    public int Status => _inner.Status;
    public string ReasonPhrase => _inner.ReasonPhrase;
    public PipelineResponseHeaders Headers => _inner.Headers;

    IAsyncEnumerator<T> IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken cancellationToken) =>
        ((IAsyncEnumerable<T>)_inner).GetAsyncEnumerator(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
