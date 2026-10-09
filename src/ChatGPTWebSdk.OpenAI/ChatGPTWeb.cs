#nullable enable
using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Capture;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using ChatGPTWebSdk.Mcp;

namespace OpenAI;

public enum ChatGPTWebMode { ApiOnly, Hybrid, BrowserOnly }
public enum ChatGPTWebHttpDriver { DotNet, SystemCurl }

public sealed class ChatGPTWebRuntimeOptions
{
    public required IWebCredentialProvider Credentials { get; init; }
    public string AccountId { get; init; } = "default";
    public string UserId { get; init; } = "default";
    public string? ClientKey { get; init; }
    public IReadOnlyDictionary<string, ConversationScope>? Clients { get; init; }
    public ChatGPTWebMode Mode { get; init; } = ChatGPTWebMode.Hybrid;
    public ChatGPTWebHttpDriver HttpDriver { get; init; } = ChatGPTWebHttpDriver.SystemCurl;
    public string CurlExecutable { get; init; } = "curl";
    public Uri BaseUri { get; init; } = new("https://chatgpt.com/");
    public HttpClient? HttpClient { get; init; }
    public string SessionDirectory { get; init; } = ".sessions";
    public IConversationStore? ConversationStore { get; init; }
    public WebEndpointProfile Endpoints { get; init; } = WebEndpointProfile.Modern;
    public BrowserSentinelOptions Browser { get; init; } = new();
    public IWebSentinelSessionProvider? SentinelSessionProvider { get; init; }
    public IWebSentinelChallengeProvider? SentinelChallengeProvider { get; init; }
    public IWebRequirementsBodyProvider? RequirementsBodyProvider { get; init; }
    public IReadOnlyDictionary<string, string>? ModelAliases { get; init; }
    public bool IgnoreModelRestrictions { get; init; }
    public string? ProjectId { get; init; }
    public bool TemporaryChat { get; init; }
    public McpConversationOptions? Mcp { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Owns the HTTP web backend and per-user conversation bindings used by the official public clients.</summary>
public sealed class ChatGPTWebRuntime : IDisposable
{
    private readonly ChatGPTWebRuntimeOptions _options;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, ConversationScope> _clients;
    private readonly HttpClientPipelineTransport _transport;
    private readonly HttpClient _compatibilityHttp;
    private readonly AsyncLocal<MessageMcpScope?> _messageMcp = new();
    private bool _disposed;
    public string ClientKey { get; }
    public ChatGptWebClient Web { get; }
    public ChatGPTWebMode Mode => _options.Mode;
    public ChatGPTWebRuntime(ChatGPTWebRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Credentials);
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.HttpDriver)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown web mode or HTTP driver.");
        if (options.Mode == ChatGPTWebMode.BrowserOnly) throw new NotSupportedException("Browser-only mode is reserved and is not implemented.");
        _options = options;
        new WebChatContext { ProjectId = options.ProjectId, TemporaryChat = options.TemporaryChat }.ResolveGizmoId();
        if (options.RequestTimeout <= TimeSpan.Zero) throw new ArgumentException("RequestTimeout must be positive.");
        ClientKey = options.ClientKey ?? (options.Clients is { Count: 1 } single ? System.Linq.Enumerable.Single(single.Keys) :
            options.Clients is null ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() : throw new ArgumentException("Set ClientKey when configuring multiple application users."));
        _clients = options.Clients ?? new Dictionary<string, ConversationScope> { [ClientKey] = new(options.AccountId, options.UserId) };
        if (!_clients.ContainsKey(ClientKey)) throw new ArgumentException("ClientKey must identify a configured application user.");
        _http = options.HttpClient ?? (options.HttpDriver == ChatGPTWebHttpDriver.SystemCurl ? WebHttpClient.CreateCurl(options.CurlExecutable) : WebHttpClient.Create());
        var browser = options.SentinelSessionProvider ?? (options.Mode == ChatGPTWebMode.Hybrid ? new BrowserSentinelProvider(options.Browser) : null);
        var transport = new ChatGptWebTransport(_http, options.Credentials, new() { BaseUri = options.BaseUri, Endpoints = options.Endpoints, SentinelSessionProvider = browser, IgnoreModelRestrictions = options.IgnoreModelRestrictions,
            SentinelChallengeProvider = options.SentinelChallengeProvider, RequirementsBodyProvider = options.RequirementsBodyProvider });
        var store = options.ConversationStore ?? new FileConversationStore(options.SessionDirectory);
        Web = options.Mcp is null ? new(transport, store) : new(transport, store, options.Mcp);
        _compatibilityHttp = new(new OpenAiWebHttpHandler(new(Web, options.ModelAliases, new() { ProjectId = options.ProjectId, TemporaryChat = options.TemporaryChat }), ResolveScope, TakeMessageMcp)) { Timeout = options.RequestTimeout };
        _transport = new(_compatibilityHttp);
        ChatGPTWeb.RegisterTransport(_transport);
    }
    public ConversationScope ResolveScope(string clientKey, string? threadId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_clients.TryGetValue(clientKey, out var scope)) throw new SdkException("Unknown application client key.", "authentication_required", System.Net.HttpStatusCode.Unauthorized);
        return threadId is null ? scope : new(scope.AccountId, scope.UserId, threadId);
    }
    public OpenAIClientOptions CreateClientOptions(string? threadId = null, string? projectId = null, bool? temporaryChat = null)
    {
        new WebChatContext { ProjectId = projectId ?? _options.ProjectId, TemporaryChat = temporaryChat ?? _options.TemporaryChat }.ResolveGizmoId();
        var options = new OpenAIClientOptions { Transport = _transport, RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = _options.RequestTimeout };
        if (threadId is not null) options.AddPolicy(new ThreadPolicy(threadId), PipelinePosition.PerCall);
        if (projectId is not null || temporaryChat is not null) options.AddPolicy(new ContextPolicy(projectId, temporaryChat), PipelinePosition.PerCall);
        return options;
    }
    public OpenAIClient CreateClient(string? clientKey = null, string? threadId = null, string? projectId = null, bool? temporaryChat = null) => new(new System.ClientModel.ApiKeyCredential(clientKey ?? ClientKey), CreateClientOptions(threadId, projectId, temporaryChat));
    /// <summary>Configures MCP servers for one application user's thread. Null restores initialization defaults.</summary>
    public void SetChatMcp(McpScopeOptions? mcp, string? threadId = null, string? clientKey = null) =>
        Web.SetChatMcp(ResolveScope(clientKey ?? ClientKey, threadId), mcp);
    public McpScopeOptions? GetChatMcp(string? threadId = null, string? clientKey = null) =>
        Web.GetChatMcp(ResolveScope(clientKey ?? ClientKey, threadId));
    /// <summary>Applies servers to the next Chat/Responses generation attempt in this async context only.</summary>
    public IDisposable UseMessageMcp(McpScopeOptions mcp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mcp);
        var scope = new MessageMcpScope(this, mcp.Snapshot(), _messageMcp.Value);
        _messageMcp.Value = scope;
        return scope;
    }
    private McpScopeOptions? TakeMessageMcp() => _messageMcp.Value?.Take();
    internal void Apply(ClientPipelineOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options.Transport = _transport;
        options.RetryPolicy = new ClientRetryPolicy(0);
        options.NetworkTimeout = _options.RequestTimeout;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _compatibilityHttp.Dispose(); if (_options.HttpClient is null) _http.Dispose();
        ChatGPTWeb.Unconfigure(this);
    }
    private sealed class MessageMcpScope(ChatGPTWebRuntime owner, McpScopeOptions options, MessageMcpScope? parent) : IDisposable
    {
        private int _used;
        private int _disposed;
        public McpScopeOptions? Take() => Volatile.Read(ref _disposed) == 0 && Interlocked.CompareExchange(ref _used, 1, 0) == 0 ? options : null;
        public void Dispose()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!ReferenceEquals(owner._messageMcp.Value, this)) throw new InvalidOperationException("Dispose message MCP scopes in reverse creation order in their originating async context.");
            Interlocked.Exchange(ref _disposed, 1);
            owner._messageMcp.Value = parent;
        }
    }
    private sealed class ThreadPolicy(string thread) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index) { message.Request.Headers.Set("X-ChatGPT-Thread-Id", thread); ProcessNext(message, pipeline, index); }
        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index) { message.Request.Headers.Set("X-ChatGPT-Thread-Id", thread); return ProcessNextAsync(message, pipeline, index); }
    }
    private sealed class ContextPolicy(string? project, bool? temporary) : PipelinePolicy
    {
        private void Set(PipelineMessage message)
        {
            if (project is not null) message.Request.Headers.Set("X-ChatGPT-Project-Id", project);
            if (temporary is not null) message.Request.Headers.Set("X-ChatGPT-Temporary-Chat", temporary.Value ? "true" : "false");
        }
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index) { Set(message); ProcessNext(message, pipeline, index); }
        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index) { Set(message); return ProcessNextAsync(message, pipeline, index); }
    }
}

/// <summary>One-time initialization for unchanged ChatClient, ResponsesClient and OpenAIClient constructors.</summary>
public static class ChatGPTWeb
{
    private static ChatGPTWebRuntime? _current;
    private static readonly ConditionalWeakTable<PipelineTransport, object> WebTransports = new();
    private static readonly ConditionalWeakTable<ClientPipeline, object> WebPipelines = new();
    internal static void RegisterTransport(PipelineTransport transport) => WebTransports.GetValue(transport, _ => new());
    internal static bool IsWebPipeline(ClientPipeline pipeline) => WebPipelines.TryGetValue(pipeline, out _);
    internal static void RecordPipeline(ClientPipeline pipeline, ClientPipelineOptions options) { if (options.Transport is not null && WebTransports.TryGetValue(options.Transport, out _)) WebPipelines.GetValue(pipeline, _ => new()); }
    internal static void ApplyDefaults(ClientPipelineOptions options)
    {
        if (options.Transport is not null) return;
        var current = Volatile.Read(ref _current) ?? throw new InvalidOperationException("Initialize OpenAI.ChatGPTWeb before constructing a client, or provide an explicit transport in client options.");
        current.Apply(options);
    }
    public static ChatGPTWebRuntime Initialize(ChatGPTWebRuntimeOptions options)
    {
        var runtime = new ChatGPTWebRuntime(options);
        Configure(runtime);
        return runtime;
    }
    /// <summary>Initializes from the one string copied by the Chromium or Firefox authentication extension.</summary>
    public static ChatGPTWebRuntime Initialize(string authenticationString, string sessionDirectory = ".sessions",
        string userId = "default", ChatGPTWebMode mode = ChatGPTWebMode.Hybrid, BrowserSentinelOptions? browser = null, string accountId = "default", string? projectId = null, bool temporaryChat = false) =>
        Initialize(new ChatGPTWebRuntimeOptions
        {
            Credentials = new StaticWebCredentialProvider(accountId, WebAuthentication.Import(authenticationString)),
            AccountId = accountId, UserId = userId, SessionDirectory = sessionDirectory, Mode = mode, Browser = browser ?? new(), ProjectId = projectId, TemporaryChat = temporaryChat
        });
    public static void Configure(ChatGPTWebRuntime runtime) => Interlocked.Exchange(ref _current, runtime ?? throw new ArgumentNullException(nameof(runtime)));
    /// <summary>Opt out of the default web model restrictions when initializing from an extension export.</summary>
    public static ChatGPTWebRuntime Initialize(string authenticationString, bool ignoreModelRestrictions, string sessionDirectory = ".sessions",
        string userId = "default", ChatGPTWebMode mode = ChatGPTWebMode.Hybrid, BrowserSentinelOptions? browser = null, string accountId = "default", string? projectId = null, bool temporaryChat = false, McpConversationOptions? mcp = null) =>
        Initialize(new ChatGPTWebRuntimeOptions
        {
            Credentials = new StaticWebCredentialProvider(accountId, WebAuthentication.Import(authenticationString)), IgnoreModelRestrictions = ignoreModelRestrictions, Mcp = mcp,
            AccountId = accountId, UserId = userId, SessionDirectory = sessionDirectory, Mode = mode, Browser = browser ?? new(), ProjectId = projectId, TemporaryChat = temporaryChat
        });
    /// <summary>Initializes extension authentication and registered MCP servers for automatic conversation tool calls.</summary>
    public static ChatGPTWebRuntime Initialize(string authenticationString, McpConversationOptions mcp, string sessionDirectory = ".sessions",
        string userId = "default", ChatGPTWebMode mode = ChatGPTWebMode.Hybrid, BrowserSentinelOptions? browser = null, string accountId = "default", string? projectId = null, bool temporaryChat = false) =>
        Initialize(new ChatGPTWebRuntimeOptions
        {
            Credentials = new StaticWebCredentialProvider(accountId, WebAuthentication.Import(authenticationString)), Mcp = mcp,
            AccountId = accountId, UserId = userId, SessionDirectory = sessionDirectory, Mode = mode, Browser = browser ?? new(), ProjectId = projectId, TemporaryChat = temporaryChat
        });
    internal static void Unconfigure(ChatGPTWebRuntime runtime) => Interlocked.CompareExchange(ref _current, null, runtime);
    public static Task<ChatGPTWebRuntime> InitializeFromHarAsync(string harPath, string sessionDirectory,
        string userId = "default", ChatGPTWebMode mode = ChatGPTWebMode.Hybrid, BrowserSentinelOptions? browser = null, CancellationToken ct = default) =>
        InitializeFromHarAsync(harPath, sessionDirectory, false, userId, mode, browser, ct);
    public static async Task<ChatGPTWebRuntime> InitializeFromHarAsync(string harPath, string sessionDirectory, bool ignoreModelRestrictions,
        string userId = "default", ChatGPTWebMode mode = ChatGPTWebMode.Hybrid, BrowserSentinelOptions? browser = null, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(harPath);
        var capture = await HarCapture.ImportAsync(input, ct).ConfigureAwait(false);
        return Initialize(new() { Credentials = new StaticWebCredentialProvider("default", capture.Credentials), SessionDirectory = sessionDirectory,
            UserId = userId, Mode = mode, Endpoints = capture.Endpoints, Browser = browser ?? new(), IgnoreModelRestrictions = ignoreModelRestrictions });
    }
}
