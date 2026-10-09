using System.Net;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

/// <summary>The verified ChatGPT web model families. This is independent of platform API availability.</summary>
public sealed class WebModelPolicy(bool ignoreRestrictions = false, TimeProvider? timeProvider = null)
{
    // The retirement notice in ChatGPT names a date, not a time. The SDK uses the start of that date in UTC.
    public static DateTimeOffset Gpt55Retirement { get; } = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> Current = new(StringComparer.Ordinal)
    {
        "gpt-6", "gpt-6-instant", "gpt-6-thinking",
        "gpt-5-6", "gpt-5-6-instant", "gpt-5-6-thinking", "gpt-5.6-sol-wm"
    };
    private static readonly HashSet<string> Retiring = new(StringComparer.Ordinal)
    {
        "gpt-5-5", "gpt-5-5-instant", "gpt-5-5-thinking", "gpt-5.5-wm"
    };
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["GPT-6"] = "gpt-6", ["GPT-5.6 Sol"] = "gpt-5-6", ["gpt-5.6-sol"] = "gpt-5-6",
        ["GPT-5.5"] = "gpt-5-5"
    };
    public bool IgnoreRestrictions { get; } = ignoreRestrictions;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public bool IsAllowed(string model) => IgnoreRestrictions || Current.Contains(model) ||
        Retiring.Contains(model) && _clock.GetUtcNow() < Gpt55Retirement;

    /// <summary>Resolves the three displayed model names and validates the resulting backend slug.</summary>
    public string Resolve(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Specify an available ChatGPT web model.", nameof(model));
        var slug = Aliases.TryGetValue(model, out var alias) ? alias : model;
        if (!IsAllowed(slug)) throw new SdkException(
            "The default web model policy permits GPT-6 and GPT-5.6 Sol, plus GPT-5.5 before October 14, 2026 UTC. Set IgnoreModelRestrictions=true to attempt another backend slug.",
            "model_restricted", HttpStatusCode.BadRequest);
        return slug;
    }
}
