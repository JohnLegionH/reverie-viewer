// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

/// <summary>
/// A notecard's text as the editor holds it. In the file an embedded item is one character (0x100000 + its index); a text box
/// cannot draw that, so in the editor each item stands as its name between two <see cref="Mark"/> characters (the invisible
/// separator, which nobody types). Reading the editor's text back turns each such name into the item's character again,
/// numbering the items from 0 in the order they appear, as the official viewer's writer does (llviewertexteditor.cpp exportBuffer);
/// an item no longer in the text leaves the file, a mark left without its partner is dropped, and the same item twice is one entry.
/// Two different items with one name are kept apart by zero-width spaces after the name.
/// </summary>
public sealed class NotecardEditBuffer
{
    public const char Mark = '⁣';

    // every item the editor has seen (read from the card or dropped in), in the order first seen
    private readonly List<NotecardEmbeddedItem> _known = new();

    public NotecardEditBuffer(NotecardDocument? document)
    {
        if (document is not null)
            _known.AddRange(document.Items.OrderBy(i => i.Index));
    }

    /// <summary>The items the editor knows, read or dropped in.</summary>
    public IReadOnlyList<NotecardEmbeddedItem> Known => _known;

    /// <summary>The name an item shows as (a mark inside a name would end it early, so none is kept).</summary>
    private static string NameOf(NotecardEmbeddedItem item) => item.Name.Replace(Mark.ToString(), "", StringComparison.Ordinal).Replace(Twin.ToString(), "", StringComparison.Ordinal);

    /// <summary>The offset in the text (UTF-16 units) of a line and a column, the column counted in characters as the text box counts them
    /// (a character outside the basic plane, such as an emoji, is one column but two units).</summary>
    public static int IndexOf(string text, int line, int column)
    {
        int at = 0;
        for (int l = 0; l < line && at <= text.Length; l++)
        {
            int nl = text.IndexOf('\n', at);
            if (nl < 0)
                return text.Length;
            at = nl + 1;
        }
        for (int c = 0; c < column && at < text.Length && text[at] != '\n'; c++)
            at += char.IsHighSurrogate(text[at]) && at + 1 < text.Length ? 2 : 1;
        return at;
    }

    /// <summary>The line and column (in characters) of an offset in the text (UTF-16 units).</summary>
    public static (int Line, int Column) PlaceOf(string text, int index)
    {
        int line = 0, column = 0;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n') { line++; column = 0; continue; }
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
                i++;
            column++;
        }
        return (line, column);
    }

    private const char Twin = '\u200B';

    /// <summary>What an item shows as between its marks: its name, and for each earlier item of the same name (another item, not the same
    /// one again) one zero-width space, so two items called alike stay apart (the official editor gives each its own character).</summary>
    private string KeyOf(NotecardEmbeddedItem item)
    {
        string name = NameOf(item);
        int earlier = 0;
        foreach (var k in _known)
        {
            if (k.ItemId == item.ItemId && NameOf(k) == name)
                break;
            if (NameOf(k) == name)
                earlier++;
        }
        return name + new string(Twin, earlier);
    }

    private NotecardEmbeddedItem? ByKey(string key) => _known.FirstOrDefault(k => KeyOf(k) == key);

    public string Token(NotecardEmbeddedItem item) => Mark + KeyOf(item) + Mark;

    /// <summary>A card's text (embedded items as their characters) as the editor shows it.</summary>
    public string ToDisplay(string fileText)
    {
        var sb = new StringBuilder(fileText.Length);
        for (int i = 0; i < fileText.Length; i++)
        {
            int cp = char.IsHighSurrogate(fileText[i]) && i + 1 < fileText.Length ? char.ConvertToUtf32(fileText[i], fileText[i + 1]) : fileText[i];
            if (cp > 0xffff)
                i++;
            if (cp is >= NotecardFormat.FirstEmbeddedChar and <= NotecardFormat.LastEmbeddedChar)
            {
                int index = cp - NotecardFormat.FirstEmbeddedChar;
                if (_known.FirstOrDefault(k => k.Index == index) is { } item)
                    sb.Append(Token(item));
                continue;   // a character that stands for no item is left out, as the reader does
            }
            if (cp == Mark)
                continue;
            sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    /// <summary>The editor's text as the file holds it, and the items in it numbered in order.</summary>
    public (string Text, IReadOnlyList<NotecardEmbeddedItem> Items) FromDisplay(string display)
    {
        var sb = new StringBuilder(display.Length);
        var items = new List<NotecardEmbeddedItem>();
        var indexOf = new Dictionary<Guid, int>();
        int i = 0;
        while (i < display.Length)
        {
            char c = display[i];
            if (c != Mark)
            {
                sb.Append(c);
                i++;
                continue;
            }
            int close = display.IndexOf(Mark, i + 1);
            if (close < 0)
            {
                i++;   // a mark without its partner: dropped, the rest stays as text
                continue;
            }
            string key = display[(i + 1)..close];
            if (ByKey(key) is not { } item)
            {
                // not an item: the first mark is dropped and the scan goes on from the second, which may open a real item
                sb.Append(key);
                i = close;
                continue;
            }
            // the same item again stands as the same character (one entry in the file)
            if (!indexOf.TryGetValue(item.ItemId, out int index) && items.Count < NotecardFormat.LastEmbeddedChar - NotecardFormat.FirstEmbeddedChar + 1)
            {
                index = indexOf[item.ItemId] = items.Count;
                items.Add(item with { Index = index });
            }
            if (indexOf.ContainsKey(item.ItemId))
                sb.Append(char.ConvertFromUtf32(NotecardFormat.FirstEmbeddedChar + index));
            i = close + 1;
        }
        return (sb.ToString(), items);
    }

    /// <summary>Puts an item into the editor's text at a place (a character offset), and returns the new text.</summary>
    public string Insert(string display, int at, NotecardEmbeddedItem item)
    {
        if (!_known.Any(k => k.ItemId == item.ItemId && NameOf(k) == NameOf(item)))
            _known.Add(item);
        at = Math.Clamp(at, 0, display.Length);
        return display.Insert(at, Token(item));
    }

    /// <summary>The editor's text as a notecard file.</summary>
    public byte[] ToFile(string display)
    {
        var (text, items) = FromDisplay(display);
        return NotecardFormat.Write(text, items);
    }

    /// <summary>The item whose name holds a place in the editor's text (a click), or null.</summary>
    public NotecardEmbeddedItem? ItemAt(string display, int index)
    {
        int at = 0;
        while (at < display.Length)
        {
            int open = display.IndexOf(Mark, at);
            int close = open < 0 ? -1 : display.IndexOf(Mark, open + 1);
            if (close < 0 || open > index)
                return null;
            if (ByKey(display[(open + 1)..close]) is not { } item)
            {
                at = close;   // not an item; the second mark may open one
                continue;
            }
            if (index <= close)
                return item;
            at = close + 1;
        }
        return null;
    }

    /// <summary>Every embedded item in the editor's text: where its name starts, how long it is (the marks left out), and the item.</summary>
    public IReadOnlyList<(int Start, int Length, NotecardEmbeddedItem Item)> ItemSpans(string display)
    {
        var spans = new List<(int, int, NotecardEmbeddedItem)>();
        int at = 0;
        while (at < display.Length)
        {
            int open = display.IndexOf(Mark, at);
            int close = open < 0 ? -1 : display.IndexOf(Mark, open + 1);
            if (close < 0)
                break;
            if (ByKey(display[(open + 1)..close]) is not { } item)
            {
                at = close;   // not an item; the second mark may open one
                continue;
            }
            spans.Add((open + 1, close - open - 1, item));
            at = close + 1;
        }
        return spans;
    }

    /// <summary>
    /// The item a caret touches: the span (start, length, marks included) of the item that holds the caret, or that ends just before it.
    /// A Backspace or Delete there takes the whole item, as the official editor deletes an embedded item as one character.
    /// </summary>
    public static (int Start, int Length)? TokenSpanAt(string display, int caret)
    {
        int at = 0;
        while (at < display.Length)
        {
            int open = display.IndexOf(Mark, at);
            if (open < 0)
                return null;
            int close = display.IndexOf(Mark, open + 1);
            if (close < 0)
                return null;
            if (caret > open && caret <= close + 1)
                return (open, close - open + 1);
            if (open >= caret)
                return null;
            at = close + 1;
        }
        return null;
    }
}

public enum NotecardCloseVerdict { Close, Ask }

/// <summary>
/// One notecard open in the editor: what may be done with it, its text as last saved, and Save and Close as the official notecard window
/// does them (llpreviewnotecard.cpp): the card is editable only when <see cref="NotecardEditRules.CanModify"/> allows; Save is offered once
/// the text differs from the saved one and fits the limit; Close with unsaved changes asks Save, Don't Save or Cancel
/// (canClose and the SaveChanges notification); a failed save keeps the changes.
/// </summary>
public sealed class NotecardEditSession
{
    private readonly INotecardSaveTransport? _transport;
    private string _savedText;
    private string _text;
    private IReadOnlyList<NotecardEmbeddedItem> _items;
    private IReadOnlyList<NotecardEmbeddedItem> _savedItems;

    public NotecardEditSession(NotecardAccess access, bool objectModify, NotecardDocument document, INotecardSaveTransport? transport)
    {
        Access = access;
        _transport = transport;
        CanEdit = NotecardEditRules.CanModify(access, objectModify);
        CanCopyText = NotecardRules.CanRead(access);
        _savedText = _text = document.Text;
        _savedItems = _items = document.Items;
        Buffer = new NotecardEditBuffer(document);
    }

    public NotecardAccess Access { get; private set; }
    public NotecardEditBuffer Buffer { get; }
    public bool CanEdit { get; }
    public bool CanCopyText { get; }
    public bool Saving { get; private set; }

    /// <summary>The card's text as it stands (embedded items as their characters).</summary>
    public string Text => _text;

    public bool IsDirty => CanEdit && (_text != _savedText || !SameItems(_items, _savedItems));
    public int TextBytes => NotecardEditRules.TextBytes(_text);
    public bool OverLimit => TextBytes > NotecardEditRules.MaxTextBytes;
    public bool CanSave => CanEdit && IsDirty && !OverLimit && !Saving && _transport is not null;

    /// <summary>The editor's text changed (items as named marks; <paramref name="known"/> are items dropped in since the last call).</summary>
    public void SetText(string display, IReadOnlyList<NotecardEmbeddedItem> known)
    {
        if (!CanEdit)
            return;
        foreach (var k in known)
            Buffer.Insert("", 0, k);
        (_text, _items) = Buffer.FromDisplay(display);
    }

    public async Task<NotecardSaveResult> SaveAsync(CancellationToken ct)
    {
        if (!CanSave || _transport is null)
            return NotecardSaveResult.Of(NotecardSaveStatus.Failed, CanEdit ? "nothing_to_save" : "read_only");
        string text = _text;
        var items = _items;
        Saving = true;
        try
        {
            var result = await _transport.SaveNotecardAsync(Access, NotecardFormat.Write(text, items), ct).ConfigureAwait(false);
            if (result.Status == NotecardSaveStatus.Saved)
            {
                _savedText = text;
                _savedItems = items;
                if (result.NewAssetId != Guid.Empty)
                    Access = Access with { AssetId = result.NewAssetId };
            }
            return result;
        }
        finally
        {
            Saving = false;
        }
    }

    public NotecardCloseVerdict RequestClose() => IsDirty ? NotecardCloseVerdict.Ask : NotecardCloseVerdict.Close;

    private static bool SameItems(IReadOnlyList<NotecardEmbeddedItem> a, IReadOnlyList<NotecardEmbeddedItem> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.ItemId == p.Second.ItemId && p.First.Index == p.Second.Index);
}
