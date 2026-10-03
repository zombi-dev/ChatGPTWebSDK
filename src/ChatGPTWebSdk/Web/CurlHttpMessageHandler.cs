using System.Diagnostics;
using System.Net;
using System.Text;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

/// <summary>Optional system curl transport. Credentials go through stdin; SSE remains a streaming response.</summary>
public sealed class CurlHttpMessageHandler(string executable = "curl") : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? bodyFile = null;
        Process? process = null;
        try
        {
            var config = new StringBuilder("silent\nshow-error\ninclude\nsuppress-connect-headers\nno-buffer\ncompressed\nmax-redirs = 0\n");
            config.Append("url = ").Append(Quote(request.RequestUri?.AbsoluteUri ?? throw new ArgumentException("Request URI is missing."))).Append('\n');
            config.Append("request = ").Append(Quote(request.Method.Method)).Append('\n');
            foreach (var header in request.Headers)
            {
                var separator = header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ";
                config.Append("header = ").Append(Quote(header.Key + ": " + string.Join(separator, header.Value))).Append('\n');
            }
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    config.Append("header = ").Append(Quote(header.Key + ": " + string.Join(", ", header.Value))).Append('\n');
                bodyFile = Path.Combine(Path.GetTempPath(), "websdk-curl-" + Guid.NewGuid().ToString("N") + ".body");
                await using (var output = new FileStream(bodyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await request.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                config.Append("data-binary = ").Append(Quote("@" + bodyFile)).Append('\n');
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--config"); start.ArgumentList.Add("-");
            process = Process.Start(start) ?? throw new SdkException("Could not start system curl.", "curl_unavailable");
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(config.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            var stream = process.StandardOutput.BaseStream;
            HttpResponseMessage response;
            while (true)
            {
                var status = await LineAsync(stream, cancellationToken).ConfigureAwait(false);
                var fields = status.Split(' ', 3);
                if (fields.Length < 2 || !fields[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(fields[1], out var code))
                    throw new SdkException("curl did not return HTTP response headers. Check the executable and network connection.", "curl_transport_error", HttpStatusCode.BadGateway);
                response = new((HttpStatusCode)code) { RequestMessage = request };
                var contentHeaders = new List<KeyValuePair<string, string>>();
                while (true)
                {
                    var line = await LineAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (line.Length == 0) break;
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = line[..colon]; var value = line[(colon + 1)..].Trim();
                    if (!response.Headers.TryAddWithoutValidation(name, value)) contentHeaders.Add(new(name, value));
                }
                if (code is >= 100 and < 200) { response.Dispose(); continue; }
                // Only the final response owns the process, cancellation registration and request-body file.
                response.Content = new StreamContent(new CurlResponseStream(stream, process, stderr, bodyFile, cancellationToken));
                foreach (var header in contentHeaders) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (response.Content.Headers.ContentEncoding.Count > 0)
                { response.Content.Headers.ContentEncoding.Clear(); response.Content.Headers.ContentLength = null; }
                break;
            }
            process = null; bodyFile = null;
            return response;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SdkException("System curl was not found. Install curl or use the .NET HTTP transport.", "curl_unavailable", HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            if (process is not null) { if (!process.HasExited) process.Kill(); process.Dispose(); }
            if (bodyFile is not null) File.Delete(bodyFile);
        }
    }

    private static string Quote(string value)
    {
        if (value.Any(c => c is '\r' or '\n' or '\0')) throw new ArgumentException("HTTP configuration contains a control character.");
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
    private static async Task<string> LineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(); var single = new byte[1];
        while (await stream.ReadAsync(single, ct).ConfigureAwait(false) != 0)
        {
            if (single[0] == 10) return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            if (bytes.Count >= 65536) throw new SdkException("HTTP response header is too long.", "curl_transport_error");
            bytes.Add(single[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    private sealed class CurlResponseStream(Stream inner, Process process, Task<string> stderr, string? bodyFile, CancellationToken originalToken) : Stream
    {
        private readonly CancellationTokenRegistration _registration = originalToken.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
        private bool _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            var count = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0)
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new SdkException("curl ended before the response completed. No automatic resend was attempted.", "curl_transport_error", HttpStatusCode.BadGateway);
            }
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true; _registration.Dispose(); inner.Dispose();
                if (!process.HasExited) process.Kill();
                process.Dispose();
                if (bodyFile is not null) File.Delete(bodyFile);
            }
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
