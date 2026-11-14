// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llwearable.cpp
 * @brief LLWearable class implementation
 *
 * $LicenseInfo:firstyear=2002&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */


// Adapted 2026 by Legion Builds. The wearable file's layout and limits (header, name, description, permissions and sale blocks, type, the
// parameter and texture lists, 1,000 and 100 entries at most, version 22) and the terse form of a weight (TerseWeight) follow importStream,
// exportStream and terse_F32_to_string of the file named above, rewritten in C# for our own types. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Globalization;
using System.Text;

using Viewer.Core.Inventory;

namespace Viewer.Core.Appearance;

/// <summary>
/// A wearable asset: the text file a shape, skin, hair, eyes or clothing item points at. Name, description, the
/// permission and sale blocks (kept as they were read), the wearable's type, the visual parameter weights and the texture slots.
/// The layout and limits follow the official viewer's wearable import and export (indra/llappearance/llwearable.cpp).
/// </summary>
public sealed class WearableAsset
{
    public const int CurrentVersion = 22, MaxParams = 1000, MaxTextures = 100, MaxBytes = 256 * 1024;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The permissions block as read, from its first line to its closing brace, one line per element.</summary>
    public IReadOnlyList<string> PermissionsLines { get; set; } = [];
    public IReadOnlyList<string> SaleInfoLines { get; set; } = [];
    public WearableType Type { get; set; }
    public SortedDictionary<int, float> Params { get; } = new();
    public SortedDictionary<int, Guid> Textures { get; } = new();

    public static bool TryParse(ReadOnlySpan<byte> data, out WearableAsset asset, out string error)
    {
        asset = new WearableAsset();
        if (data.Length > MaxBytes)
        {
            error = "file over the size limit";
            return false;
        }
        var lines = Encoding.UTF8.GetString(data).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int at = 0;
        string? Next() => at < lines.Length ? lines[at++] : null;
        string? NextPopulated()
        {
            while (at < lines.Length)
            {
                var l = lines[at++];
                if (l.Trim().Length > 0)
                    return l;
            }
            return null;
        }
        var header = Next();
        if (header is null || !header.StartsWith("LLWearable version ", StringComparison.Ordinal)
            || !int.TryParse(header.AsSpan("LLWearable version ".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            return Bad(out error, "bad header");
        if (version > CurrentVersion && version != 24)
            return Bad(out error, "newer file version");
        var name = Next();
        var description = Next();
        if (name is null || description is null)
            return Bad(out error, "file ends before the name or description");

        List<string>? Block(string word)
        {
            var first = NextPopulated();
            if (first is null)
                return null;
            var parts = first.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] != word || parts[1] != "0")
                return null;
            var block = new List<string> { first };
            var open = NextPopulated();
            if (open is null || open.Trim() != "{")
                return null;
            block.Add(open);
            while (Next() is { } l)
            {
                block.Add(l);
                if (l.Trim() == "}")
                    return block;
            }
            return null;
        }

        var perms = Block("permissions");
        if (perms is null)
            return Bad(out error, "missing permissions block");
        var sale = Block("sale_info");
        if (sale is null)
            return Bad(out error, "missing sale_info block");

        int Count(string word, int max, out string why)
        {
            why = "";
            var l = NextPopulated();
            var parts = l?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts is not { Length: 2 } || parts[0] != word || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
            {
                why = "missing " + word + " line";
                return -1;
            }
            if (n > max)
            {
                why = "too many " + word;
                return -1;
            }
            return n;
        }

        var typeLine = NextPopulated()?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (typeLine is not { Length: 2 } || typeLine[0] != "type" || !int.TryParse(typeLine[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeNumber))
            return Bad(out error, "bad type line");
        if (!WearableKinds.TryFromNumber(typeNumber, out var type))
            return Bad(out error, "type outside the table");

        int np = Count("parameters", MaxParams, out var why1);
        if (np < 0)
            return Bad(out error, why1);
        var weights = new SortedDictionary<int, float>();
        for (int i = 0; i < np; i++)
        {
            var p = NextPopulated()?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (p is not { Length: 2 } || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float w) || !float.IsFinite(w))
                return Bad(out error, "bad parameter line " + i);
            weights[id] = w;
        }
        int nt = Count("textures", MaxTextures, out var why2);
        if (nt < 0)
            return Bad(out error, why2);
        var textures = new SortedDictionary<int, Guid>();
        for (int i = 0; i < nt; i++)
        {
            var p = NextPopulated()?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (p is not { Length: 2 } || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int te) || !Guid.TryParse(p[1], out var g))
                return Bad(out error, "bad texture line " + i);
            textures[te] = g;
        }

        asset = new WearableAsset
        {
            Version = version == 24 ? CurrentVersion : version,
            Name = name,
            Description = description,
            PermissionsLines = perms,
            SaleInfoLines = sale,
            Type = type,
        };
        foreach (var (k, v) in weights) asset.Params[k] = v;
        foreach (var (k, v) in textures) asset.Textures[k] = v;
        error = "";
        return true;
    }

    private static bool Bad(out string error, string why)
    {
        error = why;
        return false;
    }

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        sb.Append("LLWearable version ").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(Name).Append('\n').Append(Description).Append('\n');
        foreach (var l in PermissionsLines) sb.Append(l).Append('\n');
        foreach (var l in SaleInfoLines) sb.Append(l).Append('\n');
        sb.Append("type ").Append(((int)Type).ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("parameters ").Append(Params.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (id, w) in Params)
            sb.Append(id.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(TerseWeight(w)).Append('\n');
        sb.Append("textures ").Append(Textures.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (te, g) in Textures)
            sb.Append(te.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(g.ToString("D")).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public WearableAsset Clone()
    {
        var c = new WearableAsset
        {
            Version = Version,
            Name = Name,
            Description = Description,
            PermissionsLines = PermissionsLines.ToArray(),
            SaleInfoLines = SaleInfoLines.ToArray(),
            Type = Type,
        };
        foreach (var (k, v) in Params) c.Params[k] = v;
        foreach (var (k, v) in Textures) c.Textures[k] = v;
        return c;
    }

    /// <summary>A weight as the file writes it: two decimals, trailing zeros and a bare point dropped, no zero before the point.</summary>
    public static string TerseWeight(float f)
    {
        var r = f.ToString("F2", CultureInfo.InvariantCulture);
        int len = r.Length;
        while (len > 0 && r[len - 1] == '0')
            len--;
        r = r[..len];
        if (r.Length > 0 && r[^1] == '.')
            return r[..^1];
        if (r.Length > 1 && r[0] == '-' && r[1] == '0')
            return "-" + r[2..];
        if (r.Length > 0 && r[0] == '0')
            return r[1..];
        return r;
    }
}
