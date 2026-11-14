// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using Viewer.Core.Protocol;

namespace Viewer.Core.World;

/// <summary>
/// Reads a sky settings asset into one sky frame. The asset is LLSD, as the official viewer reads it
/// (LLSettingsVOBase::onAssetDownloadComplete): notation text with a "&lt;? llsd/notation ?&gt;" line (what stock OpenSim
/// serves) or XML. Its keys are those of a day cycle's sky frame, so the frame is read by the same code as the region's own.
/// Bounded: over <see cref="MaxBytes"/>, or nested or malformed beyond the readers' limits, it is refused with a reason.
/// </summary>
public static class SettingsSky
{
    public const int MaxBytes = 256 * 1024;

    /// <summary>The sky frame (key 0) the asset holds; null, with the reason, when the bytes are not a sky settings asset.</summary>
    public static SkyFrame? Parse(ReadOnlySpan<byte> bytes, out string? why)
    {
        why = null;
        if (bytes.Length > MaxBytes) { why = "over " + MaxBytes + " bytes"; return null; }
        string text = Encoding.UTF8.GetString(bytes).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (text.Length == 0) { why = "empty"; return null; }
        object? root;
        try
        {
            if (text.StartsWith("<?", StringComparison.Ordinal) && text.Contains("llsd/notation", StringComparison.OrdinalIgnoreCase)
                && text.IndexOf("?>", StringComparison.Ordinal) is var end and >= 0)
                root = FromJson(LlsdNotation.Parse(text[(end + 2)..]));
            else if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<llsd", StringComparison.OrdinalIgnoreCase))
                root = Llsd.Parse(text);
            else if (text.StartsWith('{'))
                root = FromJson(LlsdNotation.Parse(text));
            else { why = "not LLSD notation or XML"; return null; }
        }
        catch (Exception e) when (e is FormatException or XmlException or InvalidDataException or OverflowException or InvalidOperationException)
        {
            why = "not readable LLSD: " + e.GetType().Name;
            return null;
        }
        if (root is not Dictionary<string, object?> map) { why = "not a map"; return null; }
        var type = map.GetValueOrDefault("type") as string;
        if (type != "sky") { why = "settings type is " + (type is null ? "missing" : "'" + type + "'") + ", not 'sky'"; return null; }
        return RegionEnvironment.SkyFromSettings(map);
    }

    // JSON from the notation reader into the plain values the LLSD XML reader gives (maps, lists, double, long, string, bool, Guid).
    private static object? FromJson(JsonNode? n)
    {
        switch (n)
        {
            case null: return null;
            case JsonObject o:
                var d = new Dictionary<string, object?>();
                foreach (var (k, v) in o) d[k] = FromJson(v);
                return d;
            case JsonArray a:
                var l = new List<object?>(a.Count);
                foreach (var v in a) l.Add(FromJson(v));
                return l;
            case JsonValue v:
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<double>(out var r)) return r;
                if (v.TryGetValue<long>(out var i)) return i;
                if (v.TryGetValue<string>(out var s)) return s.Length == 36 && Guid.TryParse(s, out var g) ? g : s;
                return null;
            default: return null;
        }
    }
}
