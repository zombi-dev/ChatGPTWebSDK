using System.Net;

namespace ChatGPTWebSdk.Protocol;

public class SdkException(string message, string code = "sdk_error", HttpStatusCode? statusCode = null) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class UnsupportedWebFeatureException(string feature) : SdkException(
    $"'{feature}' has no verified ChatGPT web mapping. Use the official API client or provide a capture of this web operation.",
    "unsupported_web_feature", HttpStatusCode.NotImplemented);

public sealed class WebChallengeException(string challenge) : SdkException(
    $"ChatGPT requires {challenge}. Select Hybrid or supply fresh authorized answers through an external session/challenge provider.",
    "web_challenge_required", HttpStatusCode.Forbidden);

public sealed class ConversationReconciliationException() : SdkException(
    "The previous send ended without a confirmed completion. Reconcile the linked conversation before appending another message; the SDK will not retry a possibly accepted turn.",
    "conversation_reconciliation_required", HttpStatusCode.Conflict);
