using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Browser;

/// <summary>Locates Chromium included in a platform-specific SDK download.</summary>
public static class BundledBrowser
{
    public static string Platform => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static string? FindExecutable(string? directory = null)
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(BundledBrowser).Assembly.Location);
        var candidates = directory is null
            ? new[] { Path.Combine(AppContext.BaseDirectory, "browsers"), assemblyDirectory is null ? null : Path.Combine(assemblyDirectory, "browsers"), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "browsers")) }
            : new[] { Path.GetFullPath(directory) };
        foreach (var candidate in candidates.Where(c => c is not null).Distinct())
        {
            var manifest = Path.Combine(candidate!, "browser.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = json.RootElement;
                var relative = root.GetProperty("relativeExecutable").GetString();
                if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("platform").GetString() != Platform || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw Unavailable();
                var basePath = Path.GetFullPath(candidate!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var executable = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!executable.StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(executable)) throw Unavailable();
                return executable;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
            { throw Unavailable(); }
        }
        if (directory is not null) throw Unavailable();
        return null;
    }
    private static SdkException Unavailable() => new("The bundled browser is missing, invalid, or built for another platform. Extract the complete SDK download for this platform or set Browser.ExecutablePath.", "sentinel_browser_unavailable", HttpStatusCode.ServiceUnavailable);
}
