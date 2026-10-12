using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

internal static class HttpSentinelProof
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Requirements(JsonArray configuration) =>
        "gAAAAAC" + Convert.ToBase64String(Encoding.UTF8.GetBytes(configuration.ToJsonString(JsonOptions)));

    internal static string Solve(string seed, string difficulty, JsonArray configuration, CancellationToken ct)
    {
        if (seed.Length is < 1 or > 1024 || difficulty.Length is < 1 or > 8 || difficulty.Any(c => !char.IsAsciiHexDigit(c)))
            throw new SdkException("Unsupported HTTP Sentinel proof parameters.", "unsupported_http_sentinel_challenge");
        if (configuration.Count != 18) throw new ArgumentException("Sentinel configuration requires 18 fields.");
        // Only the nonce and elapsed time change. Serialize every other field once.
        var fields = configuration.Select(value => value?.ToJsonString(JsonOptions) ?? "null").ToArray();
        var prefix = "[" + string.Join(",", fields[..3]) + ",";
        var middle = "," + string.Join(",", fields[4..9]) + ",";
        var suffix = "," + string.Join(",", fields[10..]) + "]";
        var timer = Stopwatch.StartNew();
        for (var nonce = 0; nonce < 500_000; nonce++)
        {
            if ((nonce & 255) == 0) ct.ThrowIfCancellationRequested();
            var json = string.Concat(prefix, nonce.ToString(CultureInfo.InvariantCulture), middle,
                timer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture), suffix);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            if (Compare(Hash(seed, encoded), difficulty)) return "gAAAAAB" + encoded + "~S";
        }
        throw new SdkException("The HTTP Sentinel proof exceeded its work budget.", "http_sentinel_proof_exhausted");
    }

    internal static uint Hash(string seed, string encoded)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in seed) hash = (hash ^ ch) * 16777619;
            foreach (var ch in encoded) hash = (hash ^ ch) * 16777619;
            hash ^= hash >> 16; hash *= 2246822507; hash ^= hash >> 13; hash *= 3266489909;
            return hash ^ (hash >> 16);
        }
    }
    private static bool Compare(uint hash, string difficulty)
    {
        for (var i = 0; i < difficulty.Length; i++)
        {
            var actual = (hash >> (28 - i * 4)) & 15;
            var expected = difficulty[i] <= '9' ? difficulty[i] - '0' : char.ToLowerInvariant(difficulty[i]) - 'a' + 10;
            if (actual != expected) return actual < expected;
        }
        return true;
    }
}
