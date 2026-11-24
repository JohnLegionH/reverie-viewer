// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Music;

/// <summary>The addresses parcel music may use: http and https only (a parcel owner's address is never opened by any other scheme).</summary>
public static class RadioUrl
{
    /// <summary>True when <paramref name="text"/> (trimmed) is an absolute http or https address with a host.</summary>
    public static bool TryParse(string? text, out Uri uri)
    {
        uri = new Uri("http://invalid.invalid/");
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps || parsed.Host.Length == 0)
            return false;
        uri = parsed;
        return true;
    }

    /// <summary>The site an address belongs to (what a person is asked about once): its host name in lower case.</summary>
    public static string Site(Uri uri) => uri.Host.ToLowerInvariant();
}

/// <summary>Reads the two playlist kinds radio sites hand out: .pls (<c>File1=</c>, <c>File2=</c>, ...) and .m3u (one address a line, <c>#</c> lines are comments).
/// Only http and https entries are returned; a relative entry is taken relative to the playlist's own address.</summary>
public static class PlaylistParser
{
    private const int MaxEntries = 50;

    public static IReadOnlyList<Uri> Parse(string text, Uri baseUri)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var raw = new List<string>();
        if (lines.Count > 0 && lines[0].StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase))
        {
            var files = new SortedDictionary<int, string>();
            foreach (var line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq > 4 && line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && int.TryParse(line.AsSpan(4, eq - 4), out int n) && n >= 0)
                    files.TryAdd(n, line[(eq + 1)..].Trim());
            }
            raw.AddRange(files.Values);
        }
        else
            raw.AddRange(lines.Where(l => l[0] != '#'));
        var found = new List<Uri>();
        foreach (var entry in raw)
        {
            if (!Uri.TryCreate(baseUri, entry, out var resolved) || !RadioUrl.TryParse(resolved.ToString(), out var ok))
                continue;
            found.Add(ok);
            if (found.Count == MaxEntries)
                break;
        }
        return found;
    }
}

/// <summary>The stream formats the player reads.</summary>
public enum AudioFormat { Unknown, Mp3, Aac, OggVorbis }

/// <summary>Tells the format of a stream from its first bytes. A live station is joined in the middle of its data, so a frame header is looked for
/// in the first few thousand bytes, and (when there is room) checked by finding the next frame's header where this frame says it ends.</summary>
public static class FormatSniffer
{
    public static AudioFormat Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S')
            return AudioFormat.OggVorbis;
        if (head.Length >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            return AudioFormat.Mp3;
        int last = Math.Min(head.Length - 2, 4096);
        for (int i = 0; i <= last; i++)
        {
            if (head[i] != 0xFF || (head[i + 1] & 0xE0) != 0xE0)
                continue;
            int layer = (head[i + 1] >> 1) & 3;
            if (layer == 0 && (head[i + 1] & 0xF0) == 0xF0)
            {
                int length = AdtsLength(head[i..]);
                if (length < 0 || length > 0 && (i + length + 1 >= head.Length || head[i + length] == 0xFF && (head[i + length + 1] & 0xF6) == 0xF0))
                    return AudioFormat.Aac;
            }
            else if (layer != 0)
            {
                int length = MpegLength(head[i..]);
                if (length < 0 || length > 0 && (i + length + 1 >= head.Length || head[i + length] == 0xFF && (head[i + length + 1] & 0xE0) == 0xE0))
                    return AudioFormat.Mp3;
            }
        }
        return AudioFormat.Unknown;
    }

    /// <summary>The length of an ADTS frame from its header, or 0 when the header does not hold together (-1: too few bytes to tell).</summary>
    internal static int AdtsLength(ReadOnlySpan<byte> h)
    {
        if (h.Length < 7)
            return -1;   // too little to measure: the caller accepts the header as it stands
        int sampleIndex = (h[2] >> 2) & 0xF;
        int length = ((h[3] & 3) << 11) | (h[4] << 3) | (h[5] >> 5);
        return sampleIndex < 12 && length >= 7 ? length : 0;
    }

    private static ReadOnlySpan<int> V1L3 => [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static ReadOnlySpan<int> V1L2 => [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static ReadOnlySpan<int> V1L1 => [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static ReadOnlySpan<int> V2L3 => [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static ReadOnlySpan<int> V2L1 => [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static ReadOnlySpan<int> Rates => [44100, 48000, 32000];

    /// <summary>The length of an MPEG audio frame (layers I to III) from its header, or 0 when the header does not hold together.</summary>
    internal static int MpegLength(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4)
            return -1;   // too little to measure
        int version = (h[1] >> 3) & 3, layer = (h[1] >> 1) & 3, bitrateIndex = h[2] >> 4, rateIndex = (h[2] >> 2) & 3, padding = (h[2] >> 1) & 1;
        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rateIndex == 3)
            return 0;
        bool v1 = version == 3;
        int rate = Rates[rateIndex] / (v1 ? 1 : version == 2 ? 2 : 4);
        int kbps = layer switch
        {
            3 => v1 ? V1L1[bitrateIndex] : V2L1[bitrateIndex],
            2 => v1 ? V1L2[bitrateIndex] : V2L3[bitrateIndex],
            _ => v1 ? V1L3[bitrateIndex] : V2L3[bitrateIndex],
        };
        return layer == 3 ? (12 * kbps * 1000 / rate + padding) * 4
            : layer == 1 && !v1 ? 72 * kbps * 1000 / rate + padding
            : 144 * kbps * 1000 / rate + padding;
    }
}
