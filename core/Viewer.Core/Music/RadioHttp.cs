// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Viewer.Core.Music;

/// <summary>An answer to a stream request: the status, the headers (names in lower case), the body as a stream, and the address that finally answered
/// (after redirects). Disposing it closes the connection.</summary>
internal sealed class RadioResponse : IDisposable
{
    private readonly TcpClient _client;
    private readonly Stream _network;

    public RadioResponse(int status, Dictionary<string, string> headers, Stream body, Uri uri, TcpClient client, Stream network)
    {
        Status = status;
        Headers = headers;
        Body = body;
        Uri = uri;
        _client = client;
        _network = network;
    }

    public int Status { get; }
    public Dictionary<string, string> Headers { get; }
    public Stream Body { get; }
    public Uri Uri { get; }

    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";

    public void Dispose()
    {
        try { _network.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}

/// <summary>
/// A minimal HTTP/1.0 client for radio streams. The system HTTP client refuses the answer old Shoutcast servers give to a bare address ("ICY 200 OK"
/// in place of an HTTP status line), so the request is made here: GET with the Icy-MetaData header, the status line read as HTTP or ICY, redirects
/// followed (http and https only), the body handed back as the open connection. A request for HTTP/1.0 keeps a server from chunking its answer.
/// </summary>
internal static class RadioHttp
{
    private const int MaxRedirects = 5;
    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private const int ReadTimeoutMs = 15000;

    public static async Task<RadioResponse> GetAsync(Uri uri, CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            var response = await OpenAsync(uri, ct);
            if (response.Status is 301 or 302 or 303 or 307 or 308 && response.Header("location") is { Length: > 0 } location)
            {
                response.Dispose();
                if (hop >= MaxRedirects)
                    throw new IOException("too many redirects");
                if (!Uri.TryCreate(uri, location, out var next) || !RadioUrl.TryParse(next.ToString(), out uri))
                    throw new IOException("the redirect does not lead to an http or https address");
                continue;
            }
            return response;
        }
    }

    private static async Task<RadioResponse> OpenAsync(Uri uri, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        Stream? network = null;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(uri.Host, uri.Port, connect.Token);
            }
            client.ReceiveTimeout = ReadTimeoutMs;
            client.GetStream().ReadTimeout = ReadTimeoutMs;
            network = client.GetStream();
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                var tls = new SslStream(network);
                network = tls;
                await tls.AuthenticateAsClientAsync(uri.Host);
            }
            string host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            string target = uri.PathAndQuery.Length == 0 ? "/" : uri.PathAndQuery;
            var request = Encoding.ASCII.GetBytes($"GET {target} HTTP/1.0\r\nHost: {host}\r\nUser-Agent: Reverie-Radio/1.0\r\nAccept: */*\r\nIcy-MetaData: 1\r\nConnection: close\r\n\r\n");
            await network.WriteAsync(request, ct);
            var (status, headers) = await ReadHead(network, ct);
            Stream body = network;
            if (headers.TryGetValue("icy-metaint", out var interval) && int.TryParse(interval, out int metaint) && metaint > 0)
                body = new IcyStripStream(network, metaint);
            return new RadioResponse(status, headers, body, uri, client, network);
        }
        catch
        {
            try { network?.Dispose(); } catch { }
            client.Dispose();
            throw;
        }
    }

    private static async Task<(int Status, Dictionary<string, string> Headers)> ReadHead(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0)
                throw new IOException("the server closed the connection before it answered");
            bytes.Add(one[0]);
            int n = bytes.Count;
            if (n >= 2 && bytes[n - 1] == '\n' && bytes[n - 2] == '\n' || n >= 4 && bytes[n - 1] == '\n' && bytes[n - 2] == '\r' && bytes[n - 3] == '\n' && bytes[n - 4] == '\r')
                break;
            if (n > MaxHeaderBytes)
                throw new IOException("the answer's header is too long");
        }
        var lines = Encoding.Latin1.GetString(bytes.ToArray()).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 2 || !(first[0].StartsWith("HTTP/", StringComparison.Ordinal) || first[0] == "ICY") || !int.TryParse(first[1], out int status))
            throw new IOException("the answer is not HTTP");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        return (status, headers);
    }
}

/// <summary>Takes the station's title blocks out of a Shoutcast or Icecast stream: after every <c>interval</c> bytes of audio comes one length byte
/// (times 16) and that many bytes of text. The decoder sees audio only.</summary>
internal sealed class IcyStripStream : Stream
{
    private readonly Stream _inner;
    private readonly int _interval;
    private int _left;

    public IcyStripStream(Stream inner, int interval)
    {
        _inner = inner;
        _interval = interval;
        _left = interval;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_left == 0)
        {
            int length = _inner.ReadByte();
            if (length < 0)
                return 0;
            for (int skipped = 0, want = length * 16; skipped < want;)
            {
                int got = _inner.Read(_skip, 0, Math.Min(_skip.Length, want - skipped));
                if (got == 0)
                    return 0;
                skipped += got;
            }
            _left = _interval;
        }
        int n = _inner.Read(buffer, offset, Math.Min(count, _left));
        _left -= n;
        return n;
    }

    private readonly byte[] _skip = new byte[4096];

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A stream that first gives back bytes already read (the start of a stream, read to tell its format) and then carries on with the stream itself.</summary>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _rest;
    private int _at;
    private long _read;

    public PrefixedStream(byte[] prefix, Stream rest)
    {
        _prefix = prefix;
        _rest = rest;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_at < _prefix.Length)
        {
            int n = Math.Min(count, _prefix.Length - _at);
            Array.Copy(_prefix, _at, buffer, offset, n);
            _at += n;
            _read += n;
            return n;
        }
        int got = _rest.Read(buffer, offset, count);
        _read += got;
        return got;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    /// <summary>How many bytes were read so far (NLayer asks for it even on a stream that cannot seek).</summary>
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
