// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

public enum NotecardOpenStatus { Ok, NotANotecard, NotAllowed, Missing, Failed, Unreadable }

/// <summary>The outcome of opening a notecard: the document when it could be read, else why not.</summary>
/// <param name="Error">Why the file did not read as a notecard (a fixed reason, never the card's content); null otherwise.</param>
public sealed record NotecardOpenResult(NotecardOpenStatus Status, Guid ItemId, string Name, string Description, NotecardAccess? Access, NotecardDocument? Document, string? Error = null)
{
    /// <summary>The string-table key of the line the window shows when there is no text to show; null when it was read.</summary>
    public string? MessageKey => Status switch
    {
        NotecardOpenStatus.Ok => null,
        NotecardOpenStatus.NotAllowed => "notecard.not_allowed",
        NotecardOpenStatus.Missing => "notecard.missing",
        NotecardOpenStatus.Unreadable => "notecard.unreadable",
        NotecardOpenStatus.NotANotecard => "notecard.not_a_notecard",
        _ => "notecard.failed",
    };
}

/// <summary>
/// Opens a notecard from the inventory as the official viewer's preview does (indra/newview/llpreviewnotecard.cpp loadAsset): the
/// item must be a notecard the agent may copy, a card with no asset is empty, otherwise its file is fetched from the grid and read.
/// No engine types; the answer may come on any thread.
/// </summary>
public sealed class NotecardReader(InventoryModel model, INotecardTransport? transport)
{
    private readonly InventoryModel _model = model;
    private readonly INotecardTransport? _transport = transport;

    public async Task<NotecardOpenResult> OpenAsync(Guid itemId, CancellationToken ct = default)
    {
        // a link opens the card it points to
        var item = _model.Item(itemId);
        if (item is { LinkedId: { } target } && _model.Item(target) is { } linked)
            (itemId, item) = (target, linked);
        string name = item?.Name ?? "", description = item?.Description ?? "";
        if (item is null || item.Kind != ItemKind.Notecard || _transport?.NotecardAccessOf(itemId) is not { } access)
            return new(NotecardOpenStatus.NotANotecard, itemId, name, description, null, null);
        if (!NotecardRules.CanRead(access))
            return new(NotecardOpenStatus.NotAllowed, itemId, name, description, access, null);
        if (access.AssetId == Guid.Empty)
            return new(NotecardOpenStatus.Ok, itemId, name, description, access, new NotecardDocument(2, "", []));
        var fetched = await _transport.FetchNotecardAsync(access, ct).ConfigureAwait(false);
        if (fetched.Status != AssetFetchStatus.Ok)
        {
            var status = fetched.Status switch
            {
                AssetFetchStatus.NotFound => NotecardOpenStatus.Missing,
                AssetFetchStatus.Denied => NotecardOpenStatus.NotAllowed,
                _ => NotecardOpenStatus.Failed,
            };
            return new(status, itemId, name, description, access, null);
        }
        return FromBytes(itemId, name, description, access, fetched.Bytes.Span);
    }

    /// <summary>Opens a notecard inside an object, as the official Open on it does (indra/newview/llpanelobjectinventory.cpp
    /// LLTaskNotecardBridge::openItem): it is read when the agent may copy the card or may modify the object; the file is asked for with the
    /// object's id. Shown read-only; an embedded item cannot be copied out of it here.</summary>
    public static async Task<NotecardOpenResult> OpenTaskAsync(INotecardTransport? transport, Viewer.Core.Session.TaskItem item, Guid objectId, Guid agent,
        bool objectModify, CancellationToken ct = default)
    {
        var access = new NotecardAccess(item.ItemId, item.AssetId, agent,
            new NotecardPermissions(item.CreatorId, item.OwnerId, item.BaseMask, item.OwnerMask, item.GroupMask, item.EveryoneMask, item.NextOwnerMask),
            false, objectId);
        if (item.AssetType != 7)
            return new(NotecardOpenStatus.NotANotecard, item.ItemId, item.Name, item.Description, null, null);
        if (!Viewer.Shell.Build.TaskItemRules.CanOpenNotecard(item, agent, objectModify))
            return new(NotecardOpenStatus.NotAllowed, item.ItemId, item.Name, item.Description, access, null);
        if (transport is null)
            return new(NotecardOpenStatus.Failed, item.ItemId, item.Name, item.Description, access, null);
        var fetched = await transport.FetchNotecardAsync(access, ct).ConfigureAwait(false);
        if (fetched.Status != AssetFetchStatus.Ok)
            return new(fetched.Status switch
            {
                AssetFetchStatus.NotFound => NotecardOpenStatus.Missing,
                AssetFetchStatus.Denied => NotecardOpenStatus.NotAllowed,
                _ => NotecardOpenStatus.Failed,
            }, item.ItemId, item.Name, item.Description, access, null);
        return FromBytes(item.ItemId, item.Name, item.Description, access, fetched.Bytes.Span);
    }

    /// <summary>The same reading from a file already in hand (the development automation's fixtures).</summary>
    public static NotecardOpenResult FromBytes(Guid itemId, string name, string description, NotecardAccess access, ReadOnlySpan<byte> data)
    {
        var parsed = NotecardFormat.Parse(data);
        return parsed.Document is { } doc
            ? new(NotecardOpenStatus.Ok, itemId, name, description, access, doc)
            : new(NotecardOpenStatus.Unreadable, itemId, name, description, access, null, parsed.Error);
    }
}

/// <summary>What one embedded item offers in the window.</summary>
/// <param name="Teleport">A landmark: the window shows where it leads and offers a teleport.</param>
/// <param name="Copy">"Copy to inventory" is enabled.</param>
public sealed record NotecardItemOffer(NotecardEmbeddedItem Item, bool Teleport, bool Copy, string Place);

/// <summary>One open notecard's window as data: its title and description, the text in runs, what each embedded item offers and
/// what a click on a link or an item does (an intent for the action pipeline, or nothing).</summary>
public sealed class NotecardWindowModel(NotecardOpenResult opened)
{
    public NotecardOpenResult Opened { get; } = opened;
    private readonly IReadOnlyList<NotecardSegment> _segments = opened.Document?.Segments() ?? [];
    private readonly string _plain = opened.Document?.PlainText ?? "";

    public string Title => Opened.Name;
    public IReadOnlyList<NotecardSegment> Segments => _segments;

    /// <summary>What a text selection copies: the plain text (embedded items as their names), as the official editor copies any
    /// selection of a card it could open (indra/llui/lltexteditor.cpp canCopy: any selection).</summary>
    public string CopyText(int start, int length)
    {
        if (start < 0 || length <= 0 || start >= _plain.Length)
            return "";
        return _plain.Substring(start, Math.Min(length, _plain.Length - start));
    }

    public NotecardItemOffer? Offer(int index)
    {
        if (Opened.Document?.Items.FirstOrDefault(i => i.Index == index) is not { } item || Opened.Access is not { } access)
            return null;
        bool landmark = item.Kind == ItemKind.Landmark && item.AssetId != Guid.Empty;
        return new NotecardItemOffer(item, landmark, NotecardRules.CanCopyOut(access, item), landmark ? item.Description : "");
    }

    /// <summary>The intent a click on a link asks for: a web address opens through the pipeline (the viewer's own handling of
    /// web addresses, which opens only http and https); a location link has no handler in this viewer yet and asks for nothing.</summary>
    public ActionIntent? LinkClicked(NotecardSegment link)
    {
        if (link.Kind != NotecardSegmentKind.WebLink)
            return null;
        string url = link.Text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + link.Text : link.Text;
        return OpenUrlIntent.IsOpenable(url) ? new OpenUrlIntent(url) : null;
    }

    /// <summary>The card's text as rich text: plain runs escaped, embedded items in bold, links through the one link renderer.</summary>
    public string RenderText(Func<string, string> escape, Viewer.Shell.Links.LinkRouter? router)
    {
        var bb = new System.Text.StringBuilder();
        foreach (var segment in _segments)
        {
            if (segment.Kind == NotecardSegmentKind.Item)
                bb.Append("[b]").Append(escape(segment.Text)).Append("[/b]");
            else if (router is not null)
                Viewer.Shell.Links.LinkText.AppendSegment(bb, segment, escape, router);
            else
                bb.Append(escape(segment.Text));
        }
        return bb.ToString();
    }

    public ActionIntent? TeleportTo(int index) =>
        Offer(index) is { Teleport: true } offer ? new TeleportLandmarkIntent(offer.Item.AssetId) : null;

    public ActionIntent? CopyToInventory(int index) =>
        Offer(index) is { Copy: true } offer ? new NotecardCopyItemIntent(Opened.ItemId, offer.Item.ItemId) : null;
}

/// <summary>The notecards open at once, front last. Opening one already open brings it to the front instead of opening it twice.</summary>
public sealed class NotecardWindowSet
{
    private readonly List<Guid> _open = [];

    public IReadOnlyList<Guid> Open => _open;

    /// <summary>Opens a window for the item, or brings its window to the front. True when a new window was opened.</summary>
    public bool Show(Guid itemId)
    {
        bool fresh = !_open.Remove(itemId);
        _open.Add(itemId);
        return fresh;
    }

    public void Close(Guid itemId) => _open.Remove(itemId);

    public Guid Front => _open.Count == 0 ? Guid.Empty : _open[^1];
}
