// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Net;

/// <summary>What a range GET got. <see cref="Kind"/> says how to use it: a checked part of the file, the
/// whole file (a server that ignored the range), or a failed partial fetch (anything else: 416, a Content-Range that
/// does not match the request, a short or long body, an unreadable answer).</summary>
public enum RangeAnswerKind { Part, Whole, NotFound, Failed }

/// <summary>Told when a request's response headers arrive and when it ends (measurement only).</summary>
public interface IHttpTiming
{
    void Headers(int status);
    /// <summary>The request ended (answered, failed or cancelled), with the body bytes read. Called once.</summary>
    void Done(long bytes);
}

public sealed record RangeAnswer(RangeAnswerKind Kind, int? Status, byte[]? Body, long From, long? Total, string? Why, long BytesRead)
{
    public static RangeAnswer Fail(int? status, string why, long read = 0) => new(RangeAnswerKind.Failed, status, null, 0, null, why, read);
}

/// <summary>
/// A GET with a <c>Range: bytes=from-to</c> (or <c>bytes=from-</c>) header, bounded like
/// <see cref="BoundedHttp"/> (headers first; a declared length over the cap is refused unread; the body is read in fixed
/// buffers and abandoned as soon as it passes what was asked for). The answer is accepted only when it is exactly what
/// was asked for:
///   - 206 with a <c>Content-Range: bytes from-end/total</c> whose start is the requested start, whose end is within the
///     request (the requested end, or the file's last byte when the file ends first), with a known total, and a body of
///     exactly end - from + 1 bytes: a <see cref="RangeAnswerKind.Part"/>;
///   - 200 (the server ignored the range): the whole file, up to the cap: <see cref="RangeAnswerKind.Whole"/>;
///   - 404: <see cref="RangeAnswerKind.NotFound"/>;
///   - anything else, including 416, a 206 without a Content-Range or with one that does not match, and a body cut short
///     or running long: <see cref="RangeAnswerKind.Failed"/>. The caller falls back to one whole-file fetch.
/// Cancellation (the token) stops the request and the body read at once.
/// </summary>
public static class RangeHttp
{
    public const int BufferBytes = 16 * 1024;

    /// <param name="to">The last byte wanted (inclusive), or null for the rest of the file.</param>
    /// <param name="capBytes">The most bytes ever held: the whole-file limit (a 200, or a total larger than this, fails).</param>
    /// <param name="timing">: optional, told the time to the response headers and the end (measurement only).</param>
    public static async Task<RangeAnswer> GetAsync(HttpClient client, Uri uri, long from, long? to, int capBytes, CancellationToken ct, IHttpTiming? timing = null)
    {
        if (from < 0 || to < from) throw new ArgumentOutOfRangeException(nameof(from));
        long read = 0;
        try
        {
            var a = await SendAsync(client, uri, from, to, capBytes, ct, timing).ConfigureAwait(false);
            read = a.BytesRead;
            return a;
        }
        catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            return RangeAnswer.Fail(null, "transport: " + e.GetType().Name);   // e.g. the body ended before its declared length
        }
        finally
        {
            timing?.Done(read);
        }
    }

    private static async Task<RangeAnswer> SendAsync(HttpClient client, Uri uri, long from, long? to, int capBytes, CancellationToken ct, IHttpTiming? timing)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        timing?.Headers(status);
        if (status == 404) return new(RangeAnswerKind.NotFound, status, null, 0, null, "answered 404", 0);
        if (status == 200)
        {
            var whole = await ReadAsync(response, capBytes, ct).ConfigureAwait(false);
            return whole.Body is null ? RangeAnswer.Fail(status, whole.Why!, whole.Read) : new(RangeAnswerKind.Whole, status, whole.Body, 0, whole.Body.Length, null, whole.Read);
        }
        if (status != 206) return RangeAnswer.Fail(status, "answered " + status);
        var cr = response.Content.Headers.ContentRange;
        if (cr is null || !cr.HasRange || cr.Unit != "bytes" || cr.From is not long f || cr.To is not long t)
            return RangeAnswer.Fail(status, "206 without a usable Content-Range");
        if (cr.Length is not long total) return RangeAnswer.Fail(status, "206 with an unknown total");
        if (total > capBytes) return RangeAnswer.Fail(status, $"total {total} bytes, larger than {capBytes}");
        long wantEnd = to is long e ? Math.Min(e, total - 1) : total - 1;
        if (f != from || t != wantEnd || t >= total)
            return RangeAnswer.Fail(status, $"Content-Range {f}-{t}/{total} does not match the request {from}-{(to is null ? "" : to)}");
        long expected = t - f + 1;
        if (response.Content.Headers.ContentLength is long declared && declared != expected)
            return RangeAnswer.Fail(status, $"declared {declared} bytes for a {expected}-byte range");
        var part = await ReadAsync(response, (int)expected, ct).ConfigureAwait(false);
        if (part.Body is null) return RangeAnswer.Fail(status, part.Why!, part.Read);
        if (part.Body.Length != expected) return RangeAnswer.Fail(status, $"body {part.Body.Length} bytes, the range is {expected}", part.Read);
        return new(RangeAnswerKind.Part, status, part.Body, f, total, null, part.Read);
    }

    private static async Task<(byte[]? Body, string? Why, long Read)> ReadAsync(HttpResponseMessage response, int capBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > capBytes)
            return (null, $"declared {declared} bytes, more than {capBytes}", 0);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[BufferBytes];
        using var body = new MemoryStream();
        long total = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (total > capBytes) return (null, $"more than {capBytes} bytes, refused", total);
            body.Write(buffer, 0, n);
        }
        return (body.ToArray(), null, total);
    }
}
