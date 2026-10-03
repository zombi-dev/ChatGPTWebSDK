using System.Text.Json.Nodes;

namespace ChatGPTWebSdk.Web;

/// <summary>Short-lived authorized browser answers. These are credentials, not reusable protocol defaults.</summary>
public sealed class WebSentinelSession
{
    public required string Token { get; init; }
    public bool IsPrepareToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string? ProofToken { get; init; }
    public string? TurnstileToken { get; init; }
    public string? ObserverToken { get; init; }
    public string? EchoLogs { get; init; }
    public bool IsCurrent => ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30);
    public override string ToString() => "[WebSentinelSession redacted]";
    internal Dictionary<string, string> Headers()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { [IsPrepareToken ? "OpenAI-Sentinel-Chat-Requirements-Prepare-Token" : "OpenAI-Sentinel-Chat-Requirements-Token"] = Token };
        if (!string.IsNullOrWhiteSpace(ProofToken)) result["OpenAI-Sentinel-Proof-Token"] = ProofToken;
        if (!string.IsNullOrWhiteSpace(TurnstileToken)) result["OpenAI-Sentinel-Turnstile-Token"] = TurnstileToken;
        if (!string.IsNullOrWhiteSpace(ObserverToken)) result["OpenAI-Sentinel-SO-Token"] = ObserverToken;
        if (!string.IsNullOrWhiteSpace(EchoLogs)) result["OAI-Echo-Logs"] = EchoLogs;
        return result;
    }
}

public sealed record WebAuthorizedSession(WebCredentials Credentials, WebSentinelSession Sentinel);
public interface IWebSentinelSessionProvider
{
    ValueTask<WebAuthorizedSession> GetSessionAsync(string accountId, WebCredentials credentials, JsonObject turnBody, CancellationToken ct = default);
}

public sealed record WebSentinelChallengeContext(string AccountId, JsonObject TurnBody, JsonNode Requirements);

/// <summary>Supplies fresh, authorized challenge answers for the current prepare response.</summary>
public interface IWebSentinelChallengeProvider
{
    ValueTask<WebSentinelChallengeAnswers> GetAnswersAsync(WebSentinelChallengeContext context, CancellationToken ct = default);
}

public sealed class WebSentinelChallengeAnswers
{
    public string? ProofToken { get; init; }
    public string? TurnstileToken { get; init; }
    public string? ObserverToken { get; init; }
    public string? ArkoseToken { get; init; }
    public override string ToString() => "[WebSentinelChallengeAnswers redacted]";
    internal Dictionary<string, string> Headers()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ProofToken is not null) result["OpenAI-Sentinel-Proof-Token"] = ProofToken;
        if (TurnstileToken is not null) result["OpenAI-Sentinel-Turnstile-Token"] = TurnstileToken;
        if (ObserverToken is not null) result["OpenAI-Sentinel-SO-Token"] = ObserverToken;
        if (ArkoseToken is not null) result["OpenAI-Sentinel-Arkose-Token"] = ArkoseToken;
        return result;
    }
}
