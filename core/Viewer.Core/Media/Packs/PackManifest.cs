// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;

namespace Viewer.Core.Media.Packs;

/// <summary>One optional download the viewer knows: what it is, where it comes from, how large it is and the hash its bytes must have.</summary>
public sealed record PackOffer(string Id, string Version, Uri Url, long Bytes, string Sha256);

/// <summary>
/// The list of packs the viewer may offer on one platform, and the versions it no longer accepts. The text is
/// <c>{"packs":[{"id","version","platform","url","bytes","sha256"}],"withdrawn":[{"id","version"}]}</c>. An entry that
/// is not complete and well formed is left out, as is a withdrawn version; text that cannot be read offers nothing.
/// Addresses are https only; plain http is taken from this computer (loopback) only when the caller says so, for tests.
/// </summary>
public sealed class PackManifest
{
    /// <summary>The largest download an entry may state.</summary>
    public const long MaxBytes = 1L << 30;

    private readonly Dictionary<string, PackOffer> _offers = new(StringComparer.Ordinal);
    private readonly HashSet<(string Id, string Version)> _withdrawn = [];

    private PackManifest() { }

    public static PackManifest Empty { get; } = new();

    public static PackManifest Parse(string? json, string platform, bool allowLoopbackHttp = false)
    {
        var manifest = new PackManifest();
        if (string.IsNullOrWhiteSpace(json))
            return manifest;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return manifest;
            foreach (var entry in Entries(root, "withdrawn"))
                if (Text(entry, "id") is { } id && Text(entry, "version") is { } version)
                    manifest._withdrawn.Add((id, version));
            foreach (var entry in Entries(root, "packs"))
            {
                if (Text(entry, "platform") != platform
                    || Text(entry, "id") is not { } id || !IsId(id)
                    || Text(entry, "version") is not { } version || !IsVersion(version)
                    || !Uri.TryCreate(Text(entry, "url"), UriKind.Absolute, out var url) || !UrlAllowed(url, allowLoopbackHttp)
                    || !entry.TryGetProperty("bytes", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes <= 0 || bytes > MaxBytes
                    || Text(entry, "sha256") is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigit)
                    || manifest._withdrawn.Contains((id, version)))
                    continue;
                manifest._offers.TryAdd(id, new PackOffer(id, version, url, bytes, sha.ToLowerInvariant()));
            }
        }
        catch (JsonException)
        {
            return Empty;
        }
        return manifest;
    }

    /// <summary>The pack offered under <paramref name="id"/>; null when this platform has none.</summary>
    public PackOffer? Offer(string id) => _offers.GetValueOrDefault(id);

    public bool IsWithdrawn(string id, string version) => _withdrawn.Contains((id, version));

    /// <summary>A pack's id is a folder name: lower-case letters, digits and hyphens.</summary>
    internal static bool IsId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>A version is a folder name too: letters, digits, dots, hyphens and plus signs, starting with a letter or digit and not ending in a dot.</summary>
    internal static bool IsVersion(string version) => version.Length is > 0 and <= 60 && char.IsAsciiLetterOrDigit(version[0]) && version[^1] != '.'
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    /// <summary>May a pack be fetched from this address? Also asked about every address a download is redirected to.</summary>
    internal static bool UrlAllowed(Uri url, bool allowLoopbackHttp) =>
        url.UserInfo.Length == 0 && url.Host.Length > 0
        && (url.Scheme == Uri.UriSchemeHttps || allowLoopbackHttp && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback);

    private static IEnumerable<JsonElement> Entries(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
