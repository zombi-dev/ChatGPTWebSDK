using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class CapturedProtocolTests
{
    public static IEnumerable<object[]> CapturedTurns() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "captured-turns.json")))!.AsArray().Select(f => new object[] { f!.ToJsonString() });
    [Theory]
    [MemberData(nameof(CapturedTurns))]
    public void Every_captured_turn_decodes_primary_text_identity_and_generated_assets(string fixtureJson)
    {
        var fixture = JsonNode.Parse(fixtureJson)!; var decoder = new WebStreamDecoder(); WebStreamUpdate? last = null;
        foreach (var item in fixture["events"]!.AsArray()) last = decoder.Decode(new(item!["data"]!.GetValue<string>(), item["event"]?.GetValue<string>()));
        Assert.True(decoder.Completed);
        Assert.Equal(fixture["expectedMessageId"]?.GetValue<string>(), decoder.MessageId);
        Assert.Equal(fixture["expectedTextLength"]!.GetValue<int>(), last!.Text.Length);
        Assert.Equal(fixture["expectedAssetCount"]!.GetValue<int>(), decoder.Assets.Count);
    }
}
