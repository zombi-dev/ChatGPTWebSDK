namespace ChatGPTWebSdk.OAuth;

public sealed class CodexOAuthOptions
{
    public Uri BaseUri { get; init; } = new("https://chatgpt.com/backend-api/codex/");
    public string ClientVersion { get; init; } = "0.144.1";
    public string UserId { get; init; } = "default";
    public string Instructions { get; init; } = "";
    public bool CacheResponseHistory { get; init; } = true;
    public bool DiscoverModelCapabilities { get; init; } = true;
    public int MaxCachedResponses { get; init; } = 256;
    public int MaxContextCharacters { get; init; } = 8 * 1024 * 1024;
    public TimeSpan ModelCacheDuration { get; init; } = TimeSpan.FromMinutes(5);
    internal void Validate()
    {
        OpenAIOAuthClient.ValidateEndpoint(BaseUri);
        if (string.IsNullOrWhiteSpace(ClientVersion) || ClientVersion.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) ||
            string.IsNullOrWhiteSpace(UserId) || MaxCachedResponses is < 1 or > 2000 || MaxContextCharacters is < 1024 or > 32 * 1024 * 1024 ||
            ModelCacheDuration < TimeSpan.Zero || ModelCacheDuration > TimeSpan.FromHours(1))
            throw new ArgumentException("Invalid Codex OAuth transport limits.");
    }
}
