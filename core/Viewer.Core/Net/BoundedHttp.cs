// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Net;

/// <summary>What a bounded GET got: the status (null when no answer), the body when it was within the cap, why not
/// otherwise, and how many body bytes were read (for the tests: the cap is enforced while reading).</summary>
public sealed record BoundedResponse(int? Status, byte[]? Body, string? Why, long BytesRead)
{
    /// <summary>The answer's Content-Type header as sent (null when it sent none).</summary>
    public string? ContentType { get; init; }
}

/// <summary>
/// A GET whose answer is bounded BEFORE it is held. LibreMetaverse's HttpCapsClient.GetAsync buffers
/// the whole answer and the old check ran afterwards, counting characters. Here the request goes through the SAME client
/// (the session's HttpCapsClient is an HttpClient, so the outbound-policy handler still applies) with
/// ResponseHeadersRead; a declared Content-Length over the cap is refused without reading the body, and the body is read
/// in fixed buffers and abandoned as soon as it passes the cap, whether or not a length was declared (chunked answers
/// included). At most cap + one buffer is ever held.
/// With the handler's AutomaticDecompression, Content-Length is the compressed length and the cap applies to the
/// DECOMPRESSED bytes read: the safe direction (a small compressed answer cannot inflate past the cap).
/// </summary>
public static class BoundedHttp
{
    public const int BufferBytes = 16 * 1024;

    /// <param name="timing">: optional, told when the response headers arrive (the caller ends it).</param>
    public static async Task<BoundedResponse> GetBoundedAsync(HttpClient client, Uri uri, int capBytes, CancellationToken ct, IHttpTiming? timing = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        timing?.Headers(status);
        string? type = response.Content.Headers.ContentType?.ToString();
        if (!response.IsSuccessStatusCode)
            return new(status, null, "answered " + status, 0) { ContentType = type };
        if (response.Content.Headers.ContentLength is long declared && declared > capBytes)
            return new(status, null, $"declared {declared} bytes, larger than {capBytes}, refused", 0) { ContentType = type };
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[BufferBytes];
        using var body = new MemoryStream();
        long total = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
            if (total > capBytes)
                return new(status, null, $"larger than {capBytes} bytes, refused", total) { ContentType = type };
            body.Write(buffer, 0, n);
        }
        return new(status, body.ToArray(), null, total) { ContentType = type };
    }
}
