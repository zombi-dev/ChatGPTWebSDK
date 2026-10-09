using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;

#pragma warning disable SCME0005

namespace ChatGPTWebSdk.Tests;

public sealed class StreamingCompatibilityTests
{
    [Theory]
    [InlineData("sse")]
    [InlineData("typed-sse")]
    [InlineData("json")]
    [InlineData("typed-json")]
    [InlineData("producer")]
    public async Task Legacy_factories_read_the_established_response_without_resending(string kind)
    {
        using var fixture = await Fixture.CreateAsync(kind.Contains("sse")
            ? "event: text\ndata: hello 😀\n\ndata: [DONE]\n\n" : "\"hello 😀\"\n");
        var values = new List<string>();
        if (kind == "sse")
        {
            await using var result = AsyncStreamingClientResult.CreateSse(fixture.Response, e => e.Data.ToString() == "[DONE]");
            Assert.Equal(200, result.Status); Assert.Equal("OK", result.ReasonPhrase);
            Assert.True(result.Headers.TryGetValue("x-fixture", out var header)); Assert.Equal("present", header);
            await foreach (var item in result) { Assert.Equal("text", item.EventType); values.Add(item.Data.ToString()); }
        }
        else if (kind == "typed-sse")
        {
            await using var result = AsyncStreamingClientResult.CreateSse(fixture.Response,
                (type, bytes) => Encoding.UTF8.GetString(bytes), e => e.Data.ToString() == "[DONE]");
            await foreach (var item in result) values.Add(item.Data);
        }
        else if (kind == "json")
        {
            await using var result = AsyncStreamingClientResult.CreateJsonLines(fixture.Response);
            await foreach (var item in result) values.Add(item.ToObjectFromJson<string>()!);
        }
        else if (kind == "typed-json")
        {
            await using var result = AsyncStreamingClientResult.CreateJsonLines(fixture.Response, item => item.ToObjectFromJson<string>()!);
            await foreach (var item in result) values.Add(item);
        }
        else
        {
            await using var result = AsyncStreamingClientResult.Create(fixture.Response, ReadLines);
            await foreach (var item in result) values.Add(item.Trim('"'));
        }
        Assert.Equal(new[] { "hello 😀" }, values); Assert.Equal(1, fixture.Requests);
        Assert.False(fixture.Stream.CanRead);
    }

    [Fact]
    public async Task Terminal_predicate_requires_the_terminal_event()
    {
        using var fixture = await Fixture.CreateAsync("data: first\n\n");
        await using var result = AsyncStreamingClientResult.CreateSse(fixture.Response, e => e.Data.ToString() == "[DONE]");
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var _ in result) { } });
        Assert.False(fixture.Stream.CanRead);
    }

    [Fact]
    public async Task Second_enumeration_cannot_reuse_a_consumed_response()
    {
        using var fixture = await Fixture.CreateAsync("data: first\n\n");
        await using var result = AsyncStreamingClientResult.CreateSse(fixture.Response);
        await foreach (var _ in result) { }
        Assert.Throws<InvalidOperationException>(() => ((IAsyncEnumerable<SseItem<BinaryData>>)result).GetAsyncEnumerator());
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("ReasonPhrase")]
    [InlineData("Headers")]
    [InlineData("Enumeration")]
    public async Task Disposed_legacy_results_reject_further_access(string member)
    {
        using var fixture = await Fixture.CreateAsync("data: first\n\n");
        var result = AsyncStreamingClientResult.CreateSse(fixture.Response);
        await result.DisposeAsync(); await result.DisposeAsync();
        Assert.False(fixture.Stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            if (member == "Status") _ = result.Status;
            else if (member == "ReasonPhrase") _ = result.ReasonPhrase;
            else if (member == "Headers") _ = result.Headers;
            else _ = ((IAsyncEnumerable<SseItem<BinaryData>>)result).GetAsyncEnumerator();
        });
    }

    [Fact]
    public async Task Ending_enumeration_early_closes_the_response_stream()
    {
        using var fixture = await Fixture.CreateAsync("data: first\n\ndata: second\n\n");
        await using var result = AsyncStreamingClientResult.CreateSse(fixture.Response);
        await foreach (var item in result) { Assert.Equal("first", item.Data.ToString()); break; }
        Assert.False(fixture.Stream.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_of_either_lifetime_reaches_the_producer(bool operation)
    {
        using var fixture = await Fixture.CreateAsync("unused");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<int> Wait(Stream stream, [EnumeratorCancellation] CancellationToken ct)
        {
            started.SetResult(); await Task.Delay(Timeout.Infinite, ct); yield return 1;
        }
        await using var result = AsyncStreamingClientResult.Create(fixture.Response, Wait, operation ? cancellation.Token : default);
        await using var enumerator = ((IAsyncEnumerable<int>)result).GetAsyncEnumerator(operation ? default : cancellation.Token);
        var move = enumerator.MoveNextAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public void Invalid_established_responses_fail_before_enumeration()
    {
        Assert.Throws<ArgumentNullException>(() => AsyncStreamingClientResult.CreateSse(null!));
        Assert.Throws<ArgumentNullException>(() => AsyncStreamingClientResult.CreateJsonLines(null!));
        Assert.Throws<ArgumentNullException>(() => AsyncStreamingClientResult.Create<string>(null!, ReadLines));
    }

    private static async IAsyncEnumerable<string> ReadLines(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line) yield return line;
    }

    private sealed class Fixture : IDisposable
    {
        private HttpClient _http = null!;
        private PipelineMessage _message = null!;
        public int Requests { get; private set; }
        public MemoryStream Stream { get; private set; } = null!;
        public PipelineResponse Response => _message.Response!;

        public static async Task<Fixture> CreateAsync(string text)
        {
            var fixture = new Fixture { Stream = new(Encoding.UTF8.GetBytes(text)) };
            fixture._http = new(new RecordingHandler((_, _) =>
            {
                fixture.Requests++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(fixture.Stream) };
                response.Headers.Add("x-fixture", "present"); return Task.FromResult(response);
            }));
            var pipeline = ClientPipeline.Create(new() { Transport = new HttpClientPipelineTransport(fixture._http) });
            fixture._message = pipeline.CreateMessage(new Uri("https://fixture.invalid/stream"), "GET");
            fixture._message.BufferResponse = false;
            await pipeline.SendAsync(fixture._message); return fixture;
        }

        public void Dispose() { _message.Dispose(); _http.Dispose(); Stream.Dispose(); }
    }
}
