using System.Runtime.CompilerServices;
using System.Text;

namespace ChatGPTWebSdk.Protocol;

public sealed record ServerSentEvent(string Data, string? Event = null, string? Id = null);

public static class ServerSentEvents
{
    public static async IAsyncEnumerable<ServerSentEvent> ReadAsync(Stream stream,
        int maxEventCharacters = 4 * 1024 * 1024, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var data = new StringBuilder();
        string? eventName = null, id = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return new(data.ToString(0, data.Length - 1), eventName, id);
                data.Clear();
                eventName = null;
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "data":
                    if (data.Length + value.Length + 1 > maxEventCharacters)
                        throw new SdkException("The SSE event exceeds the configured limit.", "event_too_large");
                    data.Append(value).Append('\n');
                    break;
                case "event": eventName = value; break;
                case "id" when !value.Contains('\0'): id = value; break;
            }
        }
        // EOF is not proof that a generation completed. Consumers decide which terminal markers count.
        if (data.Length > 0) yield return new(data.ToString(0, data.Length - 1), eventName, id);
    }
}
