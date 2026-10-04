using System.Collections.ObjectModel;

namespace ChatGPTWebSdk.Mcp;

/// <summary>Servers for one chat or one logical message, including all of its tool rounds.</summary>
public sealed class McpScopeOptions
{
    public IReadOnlyList<McpServerConfiguration> Servers { get; init; } = [];
    /// <summary>Merge with broader scopes by label. False replaces their entire server set.</summary>
    public bool InheritServers { get; init; } = true;
    /// <summary>Remove these labels from the broader scopes without changing their configuration.</summary>
    public IReadOnlyList<string> ExcludedServers { get; init; } = [];

    /// <summary>Validates and copies connection collections. Application-supplied clients remain owned by the application.</summary>
    public McpScopeOptions Snapshot()
    {
        ArgumentNullException.ThrowIfNull(Servers);
        ArgumentNullException.ThrowIfNull(ExcludedServers);
        var servers = SnapshotServers(Servers);
        var excluded = ExcludedServers.ToArray();
        if (excluded.Any(s => !McpServerConfiguration.IsValidLabel(s)) || excluded.Distinct(StringComparer.Ordinal).Count() != excluded.Length)
            throw new ArgumentException("Excluded MCP server labels must be valid and unique.");
        if (servers.Any(s => excluded.Contains(s.Label, StringComparer.Ordinal)))
            throw new ArgumentException("An MCP scope cannot both register and exclude the same label.");
        return new() { Servers = servers, InheritServers = InheritServers, ExcludedServers = Array.AsReadOnly(excluded) };
    }

    internal static IReadOnlyList<McpServerConfiguration> SnapshotServers(IReadOnlyList<McpServerConfiguration> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var copy = servers.Select(s =>
        {
            ArgumentNullException.ThrowIfNull(s);
            s.Validate();
            return s with
            {
                Arguments = Array.AsReadOnly(s.Arguments.ToArray()),
                Environment = new ReadOnlyDictionary<string, string?>(s.Environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)),
                Headers = new ReadOnlyDictionary<string, string>(s.Headers.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)),
                AllowedTools = s.AllowedTools is null ? null : Array.AsReadOnly(s.AllowedTools.ToArray())
            };
        }).ToArray();
        if (copy.Select(s => s.Label).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("MCP server labels must be unique within each scope.");
        return Array.AsReadOnly(copy);
    }

    internal void Apply(Dictionary<string, McpServerConfiguration> servers)
    {
        if (!InheritServers) servers.Clear();
        foreach (var label in ExcludedServers) servers.Remove(label);
        foreach (var server in Servers) servers[server.Label] = server;
    }
}
