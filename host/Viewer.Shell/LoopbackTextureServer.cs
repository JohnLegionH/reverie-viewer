// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Viewer.Shell;

/// <summary>How the <see cref="LoopbackTextureServer"/> answers (the fault cases the fetch must survive).</summary>
public enum RangeServerMode
{
    /// <summary>206 with a correct Content-Range (416 when the start is past the end).</summary>
    Honour,
    /// <summary>200 with the whole file, whatever the Range header says.</summary>
    IgnoreRange,
    /// <summary>416 for every range request.</summary>
    Always416,
    /// <summary>206 whose Content-Range starts one byte later than asked (and says so).</summary>
    WrongContentRange,
    /// <summary>206 without a Content-Range header.</summary>
    NoContentRange,
    /// <summary>206 with a correct Content-Range and Content-Length, but the connection closes halfway through the body.</summary>
    ShortBody,
    /// <summary>206 with correct headers, then the body stalls after its first bytes until the client gives up.</summary>
    SlowBody,
    /// <summary>Reads the request and never answers (no headers) until the client gives up.</summary>
    NoAnswer,
}

/// <summary>
/// A minimal HTTP/1.1 texture server for tests and the offline harness, on LOOPBACK ONLY (127.0.0.1,
/// an ephemeral port chosen by the OS), serving <c>GET /?texture_id=&lt;id&gt;</c> from a lookup, honouring (or,
/// by <see cref="Mode"/>, mishandling) Range requests. One request per connection (Connection: close). It counts what it
/// sent. Never used by the viewer itself; started only by tests and by the render harness's --tr-texture-server option.
/// </summary>
public sealed class LoopbackTextureServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Guid, byte[]?> _lookup;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private long _requests, _bodyBytes, _rangeRequests, _plainRequests;
    private int _concurrent, _maxConcurrent;
    private readonly ConcurrentDictionary<Guid, long> _perTexture = new();

    public LoopbackTextureServer(Func<Guid, byte[]?> lookup, RangeServerMode mode = RangeServerMode.Honour)
    {
        _lookup = lookup;
        Mode = mode;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoop);
    }

    public RangeServerMode Mode { get; set; }
    public int Port { get; }
    public Uri Base => new($"http://127.0.0.1:{Port}/");
    public Uri UriFor(Guid id) => new(Base, "?texture_id=" + id.ToString("D"));
    public long Requests => Interlocked.Read(ref _requests);
    public long RangeRequests => Interlocked.Read(ref _rangeRequests);
    /// <summary>Requests without a Range header.</summary>
    public long PlainRequests => Interlocked.Read(ref _plainRequests);
    /// <summary>The most requests being served at once (from reading the request to just before the last byte
    /// of its answer).</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
    /// <summary>A wait before every answer (so requests overlap and concurrency can be seen).</summary>
    public TimeSpan ResponseDelay { get; set; }
    /// <summary>Tests only: a wait after an answer is written and before the server is done with the
    /// request: a slow continuation, as under the suite's load, when the client already has the whole answer.</summary>
    public TimeSpan LingerAfterAnswer { get; set; }
    /// <summary>Body bytes written (all textures).</summary>
    public long BodyBytes => Interlocked.Read(ref _bodyBytes);
    public long BodyBytesFor(Guid id) => _perTexture.TryGetValue(id, out var n) ? n : 0;
    public IReadOnlyDictionary<Guid, long> BodyBytesPerTexture => _perTexture;
    /// <summary>Tests only: when set, every request's start (its head read) and end (the moment its
    /// in-flight count ends, as <see cref="MaxConcurrent"/> counts it), on the <see cref="System.Diagnostics.Stopwatch"/> clock.</summary>
    public bool RecordTimes { get; set; }
    public IReadOnlyList<(long Start, long End)> RequestTimes => _times.ToArray();
    private readonly ConcurrentQueue<(long Start, long End)> _times = new();
    /// <summary>Tests only: completes once a <see cref="RangeServerMode.SlowBody"/> answer has written its headers and first
    /// bytes and stalls (the moment a client is inside the body), so a test acts on that, not on a timer.</summary>
    public Task BodyStalled => _bodyStalled.Task;
    private readonly TaskCompletionSource _bodyStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var head = await ReadHead(stream).ConfigureAwait(false);
            if (head is null) return;
            Interlocked.Increment(ref _requests);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int now = Interlocked.Increment(ref _concurrent);
            for (int m = Volatile.Read(ref _maxConcurrent); now > m && Interlocked.CompareExchange(ref _maxConcurrent, now, m) != m; m = Volatile.Read(ref _maxConcurrent)) { }
            // a request is in flight until the client can have its whole answer, so its count ends just
            // before the answer's last byte goes out (the client cannot finish, and send its next request, before that
            // byte); an answer that never completes (no answer, a stalled or cut-short body) ends it when it closes.
            int ended = 0;
            void End()
            {
                if (Interlocked.Exchange(ref ended, 1) != 0) return;
                if (RecordTimes) _times.Enqueue((start, System.Diagnostics.Stopwatch.GetTimestamp()));   // before the count ends
                Interlocked.Decrement(ref _concurrent);
            }
            try
            {
                await Answer(stream, head, End).ConfigureAwait(false);
                if (LingerAfterAnswer > TimeSpan.Zero) await Task.Delay(LingerAfterAnswer).ConfigureAwait(false);
            }
            finally { End(); }
        }
        catch (Exception) { /* the client went away: nothing to do */ }
    }

    private async Task Answer(NetworkStream stream, string head, Action end)
    {
        if (Mode == RangeServerMode.NoAnswer)
        {
            try { await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            return;
        }
        if (ResponseDelay > TimeSpan.Zero) await Task.Delay(ResponseDelay).ConfigureAwait(false);
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        string target = parts.Length > 1 ? parts[1] : "";
        int q = target.IndexOf("texture_id=", StringComparison.Ordinal);
        Guid id = Guid.Empty;
        byte[]? data = q >= 0 && Guid.TryParse(target[(q + "texture_id=".Length)..].Split('&')[0], out id) ? _lookup(id) : null;
        if (data is null) { await Write(stream, "404 Not Found", [], ReadOnlyMemory<byte>.Empty, end).ConfigureAwait(false); return; }
        string? range = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))?[6..].Trim();
        if (range is null) Interlocked.Increment(ref _plainRequests);
        if (range is null || Mode == RangeServerMode.IgnoreRange)
        {
            await Body(stream, id, "200 OK", [], data, 0, data.Length, end).ConfigureAwait(false);
            return;
        }
        Interlocked.Increment(ref _rangeRequests);
        if (!TryParse(range, data.Length, out long from, out long to) || Mode == RangeServerMode.Always416)
        {
            await Write(stream, "416 Range Not Satisfiable", [$"Content-Range: bytes */{data.Length}"], ReadOnlyMemory<byte>.Empty, end).ConfigureAwait(false);
            return;
        }
        long start = Mode == RangeServerMode.WrongContentRange ? Math.Min(from + 1, to) : from;
        var headers = Mode == RangeServerMode.NoContentRange ? new List<string>() : [$"Content-Range: bytes {start}-{to}/{data.Length}"];
        await Body(stream, id, "206 Partial Content", headers, data, start, to - start + 1, end).ConfigureAwait(false);
    }

    private async Task Body(NetworkStream s, Guid id, string status, List<string> headers, byte[] data, long start, long length, Action end)
    {
        headers.Add($"Content-Length: {length}");
        headers.Add("Content-Type: image/x-j2c");
        long send = Mode == RangeServerMode.ShortBody ? length / 2 : Mode == RangeServerMode.SlowBody ? Math.Min(length, 16) : length;
        // counted before the write, not after it: the client can read the whole answer and its test
        // read the count before this continuation runs (seen once under suite load: 0 bytes counted for a complete answer)
        Interlocked.Add(ref _bodyBytes, send);
        _perTexture.AddOrUpdate(id, send, (_, n) => n + send);
        await Write(s, status, headers, data.AsMemory((int)start, (int)send), send == length ? end : null).ConfigureAwait(false);
        if (Mode == RangeServerMode.SlowBody)
        {
            _bodyStalled.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    /// <param name="end">Called just before the answer's last byte is written (a complete answer only); null: not called.</param>
    private static async Task Write(NetworkStream s, string status, IEnumerable<string> headers, ReadOnlyMemory<byte> body, Action? end)
    {
        var sb = new StringBuilder($"HTTP/1.1 {status}\r\nConnection: close\r\n");
        foreach (var h in headers) sb.Append(h).Append("\r\n");
        if (!headers.Any(h => h.StartsWith("Content-Length", StringComparison.Ordinal))) sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        if (end is null)
        {
            await s.WriteAsync(head).ConfigureAwait(false);
            await s.WriteAsync(body).ConfigureAwait(false);
            await s.FlushAsync().ConfigureAwait(false);
            return;
        }
        // everything but the last byte, then the count ends, then the last byte
        ReadOnlyMemory<byte> last;
        if (body.IsEmpty)
        {
            await s.WriteAsync(head.AsMemory(0, head.Length - 1)).ConfigureAwait(false);
            last = head.AsMemory(head.Length - 1);
        }
        else
        {
            await s.WriteAsync(head).ConfigureAwait(false);
            await s.WriteAsync(body[..^1]).ConfigureAwait(false);
            last = body[^1..];
        }
        await s.FlushAsync().ConfigureAwait(false);
        end();
        await s.WriteAsync(last).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>bytes=a-b or bytes=a- (clamped to the file); false when a is past the end or the header is malformed.</summary>
    private static bool TryParse(string range, long length, out long from, out long to)
    {
        from = to = 0;
        if (!range.StartsWith("bytes=", StringComparison.Ordinal)) return false;
        var ab = range[6..].Split('-');
        if (ab.Length != 2 || !long.TryParse(ab[0], out from) || from >= length) return false;
        to = ab[1].Length == 0 ? length - 1 : long.TryParse(ab[1], out var e) && e >= from ? Math.Min(e, length - 1) : -1;
        return to >= from;
    }

    private static async Task<string?> ReadHead(NetworkStream s)
    {
        var buf = new byte[8192];
        int n = 0;
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n)).ConfigureAwait(false);
            if (r == 0) return null;
            n += r;
            int end = Encoding.ASCII.GetString(buf, 0, n).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return Encoding.ASCII.GetString(buf, 0, end);
        }
        return null;   // headers over 8 KiB: dropped
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _accept.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _stop.Dispose();
    }
}
