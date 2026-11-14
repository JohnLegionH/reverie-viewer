// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Viewer.Core.Assets;

namespace Viewer.Core.Inventory;

/// <summary>The permission masks of an item as a notecard's embedded item list or the grid carries them.</summary>
public readonly record struct NotecardPermissions(Guid Creator, Guid Owner, uint BaseMask, uint OwnerMask, uint GroupMask, uint EveryoneMask, uint NextOwnerMask)
{
    /// <summary>The protocol's permission bits.</summary>
    public const uint Transfer = 0x2000, Modify = 0x4000, Copy = 0x8000;
}

/// <summary>One inventory item embedded in a notecard. Its place in the text is the character
/// <see cref="NotecardFormat.FirstEmbeddedChar"/> + <see cref="Index"/>.</summary>
public sealed record NotecardEmbeddedItem(int Index, Guid ItemId, Guid AssetId, string Name, string Description, int AssetType, int InventoryType,
    ItemKind Kind, NotecardPermissions Permissions);

/// <summary>A notecard as read: the text (embedded items stand in it as single characters from
/// <see cref="NotecardFormat.FirstEmbeddedChar"/>) and the items.</summary>
public sealed record NotecardDocument(int Version, string Text, IReadOnlyList<NotecardEmbeddedItem> Items)
{
    /// <summary>The embedded item a character of the text stands for, or null.</summary>
    public NotecardEmbeddedItem? ItemFor(int codePoint)
    {
        if (codePoint < NotecardFormat.FirstEmbeddedChar || codePoint > NotecardFormat.LastEmbeddedChar)
            return null;
        int index = codePoint - NotecardFormat.FirstEmbeddedChar;
        return Items.FirstOrDefault(i => i.Index == index);
    }

    /// <summary>The text with each embedded item's character replaced by the item's name: what is shown and copied as text.
    /// A character that stands for no item is left out.</summary>
    public string PlainText
    {
        get
        {
            var sb = new StringBuilder(Text.Length);
            foreach (var rune in Text.EnumerateRunes())
            {
                if (rune.Value >= NotecardFormat.FirstEmbeddedChar)
                    sb.Append(ItemFor(rune.Value)?.Name);
                else
                    sb.Append(rune.ToString());
            }
            return sb.ToString();
        }
    }

    /// <summary>The text cut into plain runs, web addresses, location links and embedded items, in order.</summary>
    public IReadOnlyList<NotecardSegment> Segments()
    {
        var segments = new List<NotecardSegment>();
        var run = new StringBuilder();
        foreach (var rune in Text.EnumerateRunes())
        {
            if (rune.Value < NotecardFormat.FirstEmbeddedChar)
            {
                run.Append(rune.ToString());
                continue;
            }
            Flush();
            if (ItemFor(rune.Value) is { } item)
                segments.Add(new NotecardSegment(NotecardSegmentKind.Item, item.Name, item.Index));
        }
        Flush();
        return segments;

        void Flush()
        {
            if (run.Length == 0)
                return;
            segments.AddRange(NotecardLinks.Split(run.ToString()));
            run.Clear();
        }
    }
}

public enum NotecardSegmentKind { Text, WebLink, PlaceLink, Item }

/// <summary>A run of a notecard's text. For <see cref="NotecardSegmentKind.Item"/>, <see cref="ItemIndex"/> names the embedded item and
/// <see cref="Text"/> is its name.</summary>
public sealed record NotecardSegment(NotecardSegmentKind Kind, string Text, int ItemIndex = -1);

/// <summary>The result of reading a notecard file: the document, or why it could not be read.</summary>
public sealed record NotecardParseResult(NotecardDocument? Document, string? Error)
{
    public bool Ok => Document is not null;
}

/// <summary>
/// The Linden notecard file: "Linden text version 1|2", a brace, the embedded item list ("LLEmbeddedItems version 1", a count,
/// and each item as "ext char index N" with the inventory item in the legacy text form), then "Text length N" and N bytes of
/// text, then a closing brace. In version 2 the text is UTF-8 and an embedded item is the character 0x100000 + its index; in
/// version 1 it is the byte 0x80 + its index. Written from the format as the official viewer reads it (indra/llinventory/
/// llnotecard.cpp importStream, llinventory.cpp importLegacyStream); no code of it is used.
/// </summary>
public static class NotecardFormat
{
    /// <summary>The first character that stands for an embedded item (the official viewer's FIRST_EMBEDDED_CHAR).</summary>
    public const int FirstEmbeddedChar = 0x100000;
    /// <summary>The last such character (the official viewer's LAST_EMBEDDED_CHAR).</summary>
    public const int LastEmbeddedChar = 0x10ffff;
    /// <summary>The largest notecard file the viewer reads (the official viewer's limit is 65536 bytes of text).</summary>
    public const int MaxBytes = 1 << 20;
    /// <summary>The most embedded items a card may list (one per character the version 2 range holds).</summary>
    private const int MaxItems = LastEmbeddedChar - FirstEmbeddedChar + 1;

    /// <summary>The key the legacy form XORs an asset id with when it writes "shadow_id" (the official viewer's MAGIC_ID).</summary>
    private const string ShadowKey = "3c115e51-04f4-523c-9fa6-98aff1034730";

    private sealed class Malformed(string reason) : Exception(reason);

    public static NotecardParseResult Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes)
            return new(null, "too_large");
        // a file without the header is a "version 0" card: plain text, no items (the official viewer shows it as text,
        // llpreviewnotecard.cpp onLoadComplete); a NUL some servers end a file with is not part of it
        if (!data.StartsWith("Linden text version"u8))
            return new(new NotecardDocument(0, Utf8(data).TrimEnd('\0'), []), null);
        try
        {
            var reader = new LineReader(data.ToArray());
            int version = ReadNumber(reader.Line(), "Linden text version ");
            if (version is not (1 or 2))
                throw new Malformed("version");
            Expect(reader.Line(), "{");
            if (ReadNumber(reader.Line(), "LLEmbeddedItems version ") != 1)
                throw new Malformed("embedded_version");
            Expect(reader.Line(), "{");
            int count = ReadNumber(reader.Line(), "count ");
            if (count < 0 || count > MaxItems)
                throw new Malformed("count");
            var items = new List<NotecardEmbeddedItem>(Math.Min(count, 256));
            for (int i = 0; i < count; i++)
                items.Add(ReadItem(reader));
            Expect(reader.Line(), "}");
            // the text's length line is looked for, not expected at once: a blank or unknown line before it is passed over, as
            // OpenSim's own reader (SLUtil.cs) searches for it
            string? line = reader.Line();
            for (int skipped = 0; line is not null && !line.TrimStart().StartsWith("Text length", StringComparison.Ordinal) && skipped < 64; skipped++)
                line = reader.Line();
            int length = ReadNumber(line, "Text length ");
            if (length < 0 || length > reader.Remaining)
                throw new Malformed("text_length");
            var bytes = reader.Take(length);
            string text = version == 2 ? Utf8(bytes) : Ascii(bytes);
            // what follows the text (a closing brace, and from some servers a NUL) is not checked: the official viewer reads the
            // counted text and stops there
            return new(new NotecardDocument(version, text, items), null);
        }
        catch (Malformed m)
        {
            return new(null, m.Message);
        }
    }

    private static string Utf8(ReadOnlySpan<byte> bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);

    /// <summary>Version 1: bytes from 0x80 stand for embedded items, the rest are ASCII.</summary>
    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
            sb.Append(b >= 0x80 ? char.ConvertFromUtf32(FirstEmbeddedChar + (b - 0x80)) : ((char)b).ToString());
        return sb.ToString();
    }

    private static int ReadNumber(string? line, string prefix)
    {
        string t = (line ?? throw new Malformed("truncated")).Trim();
        if (!t.StartsWith(prefix.Trim(), StringComparison.Ordinal))
            throw new Malformed("expected " + prefix.Trim());
        return int.TryParse(t.AsSpan(prefix.Trim().Length).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)
            ? n : throw new Malformed("number " + prefix.Trim());
    }

    private static void Expect(string? line, string what)
    {
        if ((line ?? throw new Malformed("truncated")).Trim() != what)
            throw new Malformed("expected " + what);
    }

    /// <summary>One "{ ext char index N, inv_item 0 { ... } }" block.</summary>
    private static NotecardEmbeddedItem ReadItem(LineReader reader)
    {
        Expect(reader.Line(), "{");
        int index = ReadNumber(reader.Line(), "ext char index ");
        if (index < 0 || index >= MaxItems)
            throw new Malformed("index");
        string? head = reader.Line()?.Trim();
        if (head is null || !head.StartsWith("inv_item", StringComparison.Ordinal))
            throw new Malformed("expected inv_item");
        Expect(reader.Line(), "{");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = "";
        while (true)
        {
            string line = (reader.Line() ?? throw new Malformed("truncated")).Trim();
            if (line == "}")
            {
                if (section.Length == 0)
                    break;
                section = "";
                continue;
            }
            if (line == "{" || line.Length == 0)
                continue;
            int split = line.IndexOfAny([' ', '\t']);
            string key = split < 0 ? line : line[..split];
            string value = split < 0 ? "" : line[(split + 1)..].TrimStart(' ', '\t');
            if (key is "permissions" or "sale_info")
            {
                section = key + ".";
                continue;
            }
            fields[section + key] = value;
        }
        Expect(reader.Line(), "}");

        Guid assetId = GuidOf(fields, "asset_id");
        if (assetId == Guid.Empty && fields.TryGetValue("shadow_id", out var shadow) && Guid.TryParse(shadow, out var hidden))
            assetId = Unshadow(hidden);
        int assetType = AssetTypeNumber(fields.GetValueOrDefault("type", ""));
        return new NotecardEmbeddedItem(index, GuidOf(fields, "item_id"), assetId, Text(fields, "name"), Text(fields, "desc"), assetType,
            InventoryTypeNumber(fields.GetValueOrDefault("inv_type", "")), InventoryKinds.ItemKindOf(assetType),
            new NotecardPermissions(GuidOf(fields, "permissions.creator_id"), GuidOf(fields, "permissions.owner_id"),
                Mask(fields, "permissions.base_mask"), Mask(fields, "permissions.owner_mask"), Mask(fields, "permissions.group_mask"),
                Mask(fields, "permissions.everyone_mask"), Mask(fields, "permissions.next_owner_mask")));
    }

    private static Guid GuidOf(Dictionary<string, string> f, string key) => f.TryGetValue(key, out var v) && Guid.TryParse(v, out var g) ? g : Guid.Empty;

    private static uint Mask(Dictionary<string, string> f, string key) =>
        f.TryGetValue(key, out var v) && uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint m) ? m : 0;

    /// <summary>A name or description: the text up to the "|" that ends it.</summary>
    private static string Text(Dictionary<string, string> f, string key)
    {
        string v = f.GetValueOrDefault(key, "");
        int bar = v.IndexOf('|');
        return bar < 0 ? v : v[..bar];
    }

    private static Guid Unshadow(Guid hidden)
    {
        byte[] id = hidden.ToByteArray(bigEndian: true);
        byte[] key = Guid.Parse(ShadowKey).ToByteArray(bigEndian: true);
        for (int i = 0; i < 16; i++)
            id[i] ^= key[i];
        return new Guid(id, bigEndian: true);
    }

    /// <summary>The legacy form's asset type names (the official viewer's LLAssetType names), by number; null for a number it has no name for.</summary>
    private static string? AssetTypeName(int number) => number switch
    {
        0 => "texture", 1 => "sound", 2 => "callcard", 3 => "landmark", 5 => "clothing", 6 => "object", 7 => "notecard", 8 => "category",
        10 => "lsltext", 11 => "lslbyte", 12 => "txtr_tga", 13 => "bodypart", 17 => "snd_wav", 18 => "img_tga", 19 => "jpeg", 20 => "animatn",
        21 => "gesture", 24 => "link", 25 => "link_f", 49 => "mesh", 56 => "settings", 57 => "material",
        _ => null,
    };

    /// <summary>The legacy form's inventory type names (the official viewer's LLInventoryType names), by number.</summary>
    private static string? InventoryTypeName(int number) => number switch
    {
        0 => "texture", 1 => "sound", 2 => "callcard", 3 => "landmark", 6 => "object", 7 => "notecard", 8 => "category", 9 => "root",
        10 => "script", 15 => "snapshot", 17 => "attach", 18 => "wearable", 19 => "animation", 20 => "gesture", 22 => "mesh",
        25 => "settings", 26 => "material",
        _ => null,
    };

    /// <summary>The largest type number either table names.</summary>
    private const int LastTypeNumber = 57;

    private static int AssetTypeNumber(string name) => Lookup(AssetTypeName, name);
    private static int InventoryTypeNumber(string name) => Lookup(InventoryTypeName, name);

    private static int Lookup(Func<int, string?> table, string name)
    {
        for (int number = 0; number <= LastTypeNumber; number++)
            if (table(number) == name)
                return number;
        return -1;
    }

    /// <summary>Writes a notecard file in version 2 (used for fixtures and tests; the viewer does not save notecards yet).</summary>
    public static byte[] Write(string text, IReadOnlyList<NotecardEmbeddedItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount ").Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var item in items)
        {
            var p = item.Permissions;
            sb.Append("{\next char index ").Append(item.Index.ToString(CultureInfo.InvariantCulture)).Append("\n\tinv_item\t0\n\t{\n");
            sb.Append("\t\titem_id\t").Append(item.ItemId.ToString("D")).Append("\n\t\tparent_id\t").Append(Guid.Empty.ToString("D")).Append('\n');
            sb.Append("\tpermissions 0\n\t{\n");
            sb.Append("\t\tbase_mask\t").Append(p.BaseMask.ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("\t\towner_mask\t").Append(p.OwnerMask.ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("\t\tgroup_mask\t").Append(p.GroupMask.ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("\t\teveryone_mask\t").Append(p.EveryoneMask.ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("\t\tnext_owner_mask\t").Append(p.NextOwnerMask.ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("\t\tcreator_id\t").Append(p.Creator.ToString("D")).Append("\n\t\towner_id\t").Append(p.Owner.ToString("D")).Append('\n');
            sb.Append("\t\tlast_owner_id\t").Append(p.Owner.ToString("D")).Append("\n\t\tgroup_id\t").Append(Guid.Empty.ToString("D")).Append("\n\t}\n");
            sb.Append("\t\tasset_id\t").Append(item.AssetId.ToString("D")).Append('\n');
            sb.Append("\t\ttype\t").Append(AssetTypeName(item.AssetType) ?? "-1").Append("\n\t\tinv_type\t").Append(InventoryTypeName(item.InventoryType) ?? "-1").Append('\n');
            sb.Append("\t\tflags\t00000000\n\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\n");
            sb.Append("\t\tname\t").Append(item.Name.Replace("|", "")).Append("|\n\t\tdesc\t").Append(item.Description.Replace("|", "")).Append("|\n");
            sb.Append("\t\tcreation_date\t0\n\t}\n}\n");
        }
        byte[] body = Encoding.UTF8.GetBytes(text);
        sb.Append("}\nText length ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] tail = "}\n"u8.ToArray();
        return [.. head, .. body, .. tail];
    }

    /// <summary>Reads lines (as Latin-1, the header is ASCII) and then raw bytes, from the start of a file.</summary>
    private sealed class LineReader(byte[] data)
    {
        private int _at;
        public int Remaining => data.Length - _at;

        public string? Line()
        {
            if (_at >= data.Length)
                return null;
            int end = Array.IndexOf(data, (byte)'\n', _at);
            if (end < 0)
                end = data.Length;
            string line = Encoding.Latin1.GetString(data, _at, end - _at).TrimEnd('\r');
            _at = Math.Min(end + 1, data.Length);
            return line;
        }

        public ReadOnlySpan<byte> Take(int count)
        {
            var span = data.AsSpan(_at, count);
            _at += count;
            return span;
        }

        public string Rest() => Encoding.Latin1.GetString(data, _at, data.Length - _at);
    }
}

/// <summary>Finds web addresses and location links in text, the forms the viewer knows: http and https addresses, www. addresses,
/// secondlife:// links (the official viewer's SLURLs) and the OpenSim hop:// form. Other schemes stay plain text.</summary>
public static class NotecardLinks
{
    // a plain Regex, not the source generator: the generated class's name changes with the build configuration, and the
    // shipping and development exports must hold the same core
    private static readonly Regex Pattern = new(@"(?<web>\bhttps?://[^\s/<>""'`]+[^\s<>""'`]*|\bwww\.[^\s<>""'`]+\.[^\s<>""'`]+)|(?<place>\b(?:secondlife|hop)://[^\s<>""'`]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static Regex LinkPattern() => Pattern;

    public static IReadOnlyList<NotecardSegment> Split(string text)
    {
        var segments = new List<NotecardSegment>();
        int at = 0;
        foreach (Match m in LinkPattern().Matches(text))
        {
            string link = Trim(m.Value);
            if (link.Length == 0 || m.Index < at)
                continue;
            if (m.Index > at)
                segments.Add(new NotecardSegment(NotecardSegmentKind.Text, text[at..m.Index]));
            // a maps.secondlife.com address names a place, so it is a location link, not a web page
            bool web = m.Groups["web"].Success && !Viewer.Core.Links.ViewerLinkParser.IsMapsAddress(link);
            segments.Add(new NotecardSegment(web ? NotecardSegmentKind.WebLink : NotecardSegmentKind.PlaceLink, link));
            at = m.Index + link.Length;
        }
        if (at < text.Length)
            segments.Add(new NotecardSegment(NotecardSegmentKind.Text, text[at..]));
        return segments;
    }

    /// <summary>Punctuation that ends a sentence is not part of the address; a closing bracket is, only when the address opened one.</summary>
    private static string Trim(string link)
    {
        int end = link.Length;
        while (end > 0)
        {
            char c = link[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?')
                end--;
            else if (c is ')' && link.AsSpan(0, end).Count('(') < link.AsSpan(0, end).Count(')'))
                end--;
            else if (c is ']' && link.AsSpan(0, end).Count('[') < link.AsSpan(0, end).Count(']'))
                end--;
            else
                break;
        }
        return link[..end];
    }
}

/// <summary>What the viewer knows about a notecard item before reading it: whose it is and what its masks allow.</summary>
/// <param name="InLibrary">The item is in the shared library, not the user's own inventory.</param>
public sealed record NotecardAccess(Guid ItemId, Guid AssetId, Guid AgentId, NotecardPermissions Permissions, bool InLibrary);

/// <summary>
/// The official viewer's rules for notecards (indra/newview/llpreviewnotecard.cpp loadAsset, llviewertexteditor.cpp copyInventory),
/// in our words: a notecard's text is fetched and shown only when the agent may copy it (the owner mask for the owner, the
/// everyone mask for anyone else, the library included); a notecard with no asset is empty. An embedded item can be copied
/// into the inventory only from a card that could be read, that is in the user's own inventory (the grid looks the card up
/// there), and whose embedded item the owner may transfer (the grid refuses any other).
/// </summary>
public static class NotecardRules
{
    public static bool CanRead(NotecardAccess access)
    {
        var p = access.Permissions;
        uint mask = p.Owner == access.AgentId ? p.OwnerMask : p.EveryoneMask;
        return (mask & NotecardPermissions.Copy) != 0;
    }

    public static bool CanCopyOut(NotecardAccess notecard, NotecardEmbeddedItem item) =>
        CanRead(notecard) && !notecard.InLibrary && (item.Permissions.OwnerMask & NotecardPermissions.Transfer) != 0;
}

/// <summary>Reads notecards from the grid. Implemented by the protocol adapter.</summary>
public interface INotecardTransport
{
    /// <summary>What the grid's copy of an inventory item says about it as a notecard; null when it is unknown or not a notecard.</summary>
    NotecardAccess? NotecardAccessOf(Guid itemId);

    /// <summary>Fetches a notecard's file, the way the official viewer asks for an inventory item's asset.</summary>
    Task<AssetFetchResult> FetchNotecardAsync(NotecardAccess access, CancellationToken ct);
}
