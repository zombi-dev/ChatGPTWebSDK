using System.Diagnostics;
using System.Text.Json;
using ChatGPTWebSdk.Browser;

namespace ChatGPTWebSdk.Tests;

public sealed class InvisibleBrowserTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Visibility_and_acceleration_are_independent(bool hidden, bool software)
    {
        var start = OwnedBrowser.CreateStartInfo("browser", "profile", 12000, hidden, software);
        Assert.Equal(hidden, start.CreateNoWindow);
        Assert.Equal(hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal, start.WindowStyle);
        Assert.Equal(hidden, start.ArgumentList.Contains("--headless=new"));
        Assert.Equal(hidden, start.ArgumentList.Contains("--window-size=1440,900"));
        Assert.Equal(software, start.ArgumentList.Contains("--disable-gpu"));
        Assert.Equal(software, start.ArgumentList.Contains("--disable-gpu-compositing"));
        Assert.DoesNotContain("--no-sandbox", start.ArgumentList);
        Assert.DoesNotContain("--enable-automation", start.ArgumentList);
        Assert.DoesNotContain("--ignore-gpu-blocklist", start.ArgumentList);
    }
    [Theory]
    [InlineData("enabled", true)]
    [InlineData("enabled_on", true)]
    [InlineData("enabled_force", true)]
    [InlineData("disabled_software", false)]
    [InlineData("disabled_off", false)]
    [InlineData("unavailable_software", false)]
    [InlineData("unavailable_off", false)]
    [InlineData("enabled_software", false)]
    [InlineData("unknown", null)]
    public void Compositing_status_controls_hardware_fallback(string status, bool? expected)
    {
        using var info = JsonDocument.Parse(JsonSerializer.Serialize(new { gpu = new { featureStatus = new { gpu_compositing = status } } }));
        Assert.Equal(expected, OwnedBrowser.HasHardwareAcceleration(info.RootElement));
    }
    [Theory]
    [InlineData("ANGLE (Google, Vulkan SwiftShader Device)", false)]
    [InlineData("llvmpipe (LLVM 15)", false)]
    [InlineData("Microsoft Basic Render Driver", false)]
    [InlineData("Software Rasterizer", false)]
    [InlineData("ANGLE (NVIDIA RTX)", null)]
    [InlineData("Apple M3", null)]
    public void Software_renderer_is_detected_without_assuming_a_physical_device_means_acceleration(string renderer, bool? expected)
    {
        using var info = JsonDocument.Parse(JsonSerializer.Serialize(new { gpu = new { auxAttributes = new { glRenderer = renderer } } }));
        Assert.Equal(expected, OwnedBrowser.HasHardwareAcceleration(info.RootElement));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"gpu\":null}")]
    [InlineData("{\"gpu\":{\"featureStatus\":[]}}")]
    [InlineData("{\"gpu\":{\"featureStatus\":{\"gpu_compositing\":123}}}")]
    [InlineData("{\"gpu\":{\"auxAttributes\":null}}")]
    public void Missing_or_malformed_gpu_information_does_not_force_a_restart(string json)
    {
        using var info = JsonDocument.Parse(json);
        Assert.Null(OwnedBrowser.HasHardwareAcceleration(info.RootElement));
    }
    [Fact]
    public void Default_acceleration_prefers_hardware_and_visibility_preserves_interactive_signin()
    {
        var options = new BrowserSentinelOptions();
        Assert.Equal(BrowserAcceleration.Automatic, options.Acceleration);
        Assert.False(options.Headless);
        Assert.Null(OwnedBrowser.HasHardwareAcceleration(null));
    }
}
