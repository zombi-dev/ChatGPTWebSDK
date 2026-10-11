using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.OAuth;

/// <summary>Codex OAuth credentials. These are separate from ChatGPT web cookies and Sentinel answers.</summary>
public sealed class OAuthSession
{
    public required string AccessToken { get; init; }
    public required string AccountId { get; init; }
    public string? RefreshToken { get; init; }
    public string? IdToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsFedRamp { get; init; }
    public override string ToString() => "OAuthSession { credentials redacted }";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccessToken) || AccessToken.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(AccountId) || AccountId.Any(char.IsControl))
            throw new SdkException("OAuth access token and account ID are required.", "oauth_credentials_invalid");
    }

    internal static JsonObject? Claims(string? token)
    {
        try
        {
            var part = token?.Split('.')[1];
            if (part is null || part.Length > 128 * 1024) return null;
            part = part.Replace('-', '+').Replace('_', '/').PadRight((part.Length + 3) / 4 * 4, '=');
            return JsonNode.Parse(Convert.FromBase64String(part)) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or JsonException) { return null; }
    }

    internal static OAuthSession Parse(JsonObject tokens, OAuthSession? previous = null)
    {
        var access = tokens["access_token"]?.GetValue<string>() ?? throw new SdkException("The OAuth response is missing its access token.", "oauth_response_invalid");
        var returnedId = tokens["id_token"]?.GetValue<string>();
        var id = returnedId ?? previous?.IdToken;
        var claims = Claims(returnedId); var accessClaims = Claims(access);
        string? Account(JsonObject? value) => value?["https://api.openai.com/auth"]?["chatgpt_account_id"]?.GetValue<string>() ?? value?["chatgpt_account_id"]?.GetValue<string>();
        var account = tokens["account_id"]?.GetValue<string>() ?? Account(claims) ?? Account(accessClaims) ?? previous?.AccountId;
        if (account is null) throw new SdkException("The OAuth session is missing its ChatGPT account ID.", "oauth_account_missing");
        if (previous is not null && account != previous.AccountId || Account(claims) is { } idAccount && idAccount != account || Account(accessClaims) is { } accessAccount && accessAccount != account)
            throw new SdkException("OAuth tokens disagree about the authenticated account.", "oauth_account_mismatch");
        var expiry = tokens["expires_in"]?.GetValue<double>() is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : accessClaims?["exp"]?.GetValue<long>() is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : (DateTimeOffset?)null;
        var session = new OAuthSession
        {
            AccessToken = access, AccountId = account, IdToken = id, RefreshToken = tokens["refresh_token"]?.GetValue<string>() ?? previous?.RefreshToken,
            ExpiresAt = expiry, IsFedRamp = claims?["https://api.openai.com/auth"]?["chatgpt_account_is_fedramp"]?.GetValue<bool>() == true ||
                accessClaims?["https://api.openai.com/auth"]?["chatgpt_account_is_fedramp"]?.GetValue<bool>() == true || previous?.IsFedRamp == true
        };
        session.Validate(); return session;
    }

    /// <summary>Reads only the explicit file supplied by the caller; does not inspect CODEX_HOME or the OS keyring.</summary>
    public static async Task<OAuthSession> LoadCodexAuthFileAsync(string path, CancellationToken ct = default)
    {
        var file = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject;
        if (file?["tokens"] is not JsonObject tokens) throw new SdkException("The selected file has no Codex OAuth tokens; API-key authentication is a different flow.", "oauth_credentials_invalid");
        return Parse(tokens);
    }

    /// <summary>Explicitly persists rotated credentials in Codex-compatible format, preserving unrelated fields.</summary>
    public async Task SaveCodexAuthFileAsync(string path, CancellationToken ct = default)
    {
        Validate(); path = Path.GetFullPath(path);
        var root = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject ?? throw new JsonException("Invalid authentication file.") : new JsonObject();
        var tokens = root["tokens"] as JsonObject ?? new();
        tokens["access_token"] = AccessToken; tokens["account_id"] = AccountId;
        tokens["refresh_token"] = RefreshToken; tokens["id_token"] = IdToken;
        root["tokens"] = tokens; root["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            { await JsonSerializer.SerializeAsync(file, root, cancellationToken: ct).ConfigureAwait(false); await file.FlushAsync(ct).ConfigureAwait(false); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public interface IOAuthSessionProvider
{
    ValueTask<OAuthSession> GetSessionAsync(CancellationToken ct = default);
}

public sealed class StaticOAuthSessionProvider(OAuthSession session) : IOAuthSessionProvider
{
    public ValueTask<OAuthSession> GetSessionAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); session.Validate(); return ValueTask.FromResult(session); }
}

/// <summary>Serializes refresh and rotation before requests; never retries generation or refresh-token exchange.</summary>
public sealed class RefreshingOAuthSessionProvider : IOAuthSessionProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OpenAIOAuthClient _client;
    private readonly Func<OAuthSession, CancellationToken, Task>? _persist;
    private readonly TimeSpan _margin;
    private OAuthSession _session;
    private bool _pendingPersistence;
    public RefreshingOAuthSessionProvider(OAuthSession session, OpenAIOAuthClient client,
        Func<OAuthSession, CancellationToken, Task>? persist = null, TimeSpan? refreshMargin = null)
    {
        session.Validate(); _session = session; _client = client ?? throw new ArgumentNullException(nameof(client)); _persist = persist;
        _margin = refreshMargin ?? TimeSpan.FromMinutes(5);
        if (_margin < TimeSpan.Zero || _margin > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(refreshMargin));
    }
    public async ValueTask<OAuthSession> GetSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pendingPersistence && _persist is not null)
            { await _persist(_session, ct).ConfigureAwait(false); _pendingPersistence = false; }
            if (_session.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow + _margin)
            {
                if (_session.RefreshToken is null) throw new SdkException("The OAuth access token expired. Sign in again or supply a refresh-capable session.", "oauth_login_required");
                _session = await _client.RefreshAsync(_session, ct).ConfigureAwait(false);
                if (_persist is not null)
                { _pendingPersistence = true; await _persist(_session, ct).ConfigureAwait(false); _pendingPersistence = false; }
            }
            return _session;
        }
        finally { _gate.Release(); }
    }
}
