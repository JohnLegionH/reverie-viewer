// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>
/// Notecards in the running viewer: the windows come with the inventory when the first session logs in; opening a notecard from
/// the inventory (a double-click, Enter, or Open on its menu) reads it from the grid and shows it; every window closes when the
/// session ends.
/// </summary>
public partial class Main
{
    private NotecardFeature? _notecards;
    private bool _notecardsHooked;

    /// <summary>A session's inventory is shown: notecards open from it.</summary>
    private void AttachNotecards(ViewerSession session)
    {
        if (_shell is null || _inventoryPanel is null)
            return;
        _notecards ??= NewNotecards(_shell);
        if (!_notecardsHooked)
        {
            _inventoryPanel.OpenRequested += OpenNotecard;
            _inventoryPanel.OpenRequested += id => OpenTexturePreview(id);   // a texture opens in the texture preview
            _notecardsHooked = true;
        }
        _notecards.Bind(session.Inventory, session.Notecards, session.Actions.Submit);
    }

    /// <summary>The windows, with a log line for a card that did not open (its status and the fixed reason, never its content).</summary>
    private NotecardFeature NewNotecards(ShellRoot shell)
    {
        HostLinks.Perform = PerformLink;   // a link in a card works before a session too (it then only says what it would need)
        return new(shell, (status, reason) =>
            Log.Info("notecard.not-opened", Viewer.Core.Diagnostics.CoreLog.F("status", status), Viewer.Core.Diagnostics.CoreLog.F("reason", reason ?? "")));
    }

    private void DetachNotecards() => _notecards?.Bind(null, null, null);

    private void OpenNotecard(Guid itemId)
    {
        if (_inventoryFor is not { IsEnded: false } session || _notecards is null)
            return;
        var item = session.Inventory.Item(itemId);
        Guid target = item?.LinkedId is { } linked && session.Inventory.Item(linked) is { Kind: ItemKind.Notecard } ? linked : itemId;
        if (session.Inventory.Item(target) is not { Kind: ItemKind.Notecard })
            return;   // not a card (a texture opens in its own preview)
        Log.Info("notecard.open", Viewer.Core.Diagnostics.CoreLog.F("library", session.Inventory.IsInLibrary(target)));
        _notecards.OpenItem(target, session.Inventory.Item(target)?.Name ?? "");
    }

#if NV_AUTOMATION
    /// <summary>
    /// Notecards for the development automation: "list" gives notecard ids of the inventory (own and library, ids only); "open" opens
    /// one by id as a double-click does; "fixture" opens a notecard file from disk in a window (text is a path; the card is shown
    /// as the user's own, copyable); "state" describes the windows; "copy" copies embedded item <paramref name="index"/> out of the
    /// front window. Live cards are described by counts, kinds and lengths, never their names or text; a fixture's text is given.
    /// </summary>
    public object? NotecardCall(string action, string? text, Guid id, int index)
    {
        if (_shell is null)
            return null;
        var session = _inventoryFor is { IsEnded: false } s ? s : null;
        switch (action)
        {
            case "list":
            {
                if (session is null)
                    return null;
                var inventory = session.Inventory;
                var cards = inventory.AllItems().Where(i => i.Kind == ItemKind.Notecard).OrderBy(i => i.Id).ToArray();
                return new
                {
                    own = cards.Where(c => !inventory.IsInLibrary(c.Id)).Select(c => c.Id).ToArray(),
                    library = cards.Where(c => inventory.IsInLibrary(c.Id)).Select(c => c.Id).ToArray(),
                    folders_unloaded = inventory.UnloadedFolderCount,
                    folders_asked = text == "load" ? inventory.EnsureAllLoaded() : 0,
                };
            }
            case "open":
                if (session is null || id == Guid.Empty)
                    return null;
                OpenNotecard(id);
                break;
            case "fixture":
            {
                if (text is not { Length: > 0 } path || !System.IO.File.Exists(path))
                    return new { error = "no such file" };
                _notecards ??= NewNotecards(_shell);
                Guid agent = Guid.NewGuid();
                // one item id per file, so opening the same file again is opening the same card again
                if (!_notecardFixtures.TryGetValue(path, out Guid card))
                    _notecardFixtures[path] = card = Guid.NewGuid();
                var access = new NotecardAccess(card, Guid.NewGuid(), agent, new NotecardPermissions(agent, agent, 0x7fffffff, 0x7fffffff, 0, 0, 0x7fffffff), InLibrary: false);
                var opened = NotecardReader.FromBytes(access.ItemId, System.IO.Path.GetFileNameWithoutExtension(path), "fixture", access, System.IO.File.ReadAllBytes(path));
                _notecards.ShowOpened(opened);
                break;
            }
            case "copy":
                if (_notecards?.Open is not { Count: > 0 } open || _notecards.PanelOf(open[^1]) is not { } front)
                    return new { error = "no window" };
                return new { copied = front.CopyItem(index) };
            case "links" or "link":
            {
                if (_notecards?.Open is not { Count: > 0 } open2 || _notecards.PanelOf(open2[^1]) is not { } card)
                    return new { error = "no window" };
                string bb = card.TextView.Text;
                var targets = HostLinks.Targets(bb);
                if (action == "links")
                    return LinkReport(bb, fixture: _notecardFixtures.ContainsValue(open2[^1]));
                if (index < 0 || index >= targets.Length)
                    return new { error = "no such link", count = targets.Length };
                card.ClickTarget(targets[index]);
                return LinkClickReport(card.StatusLabel.Text, text);
            }
            case "chat_link":
            {
                if (_chatPanels?.Nearby is not { } nearby)
                    return new { error = "no chat" };
                var targets = HostLinks.Targets(nearby.HistoryView.Text);
                // index counts from the newest link back (0 = the last link shown)
                if (index < 0 || index >= targets.Length)
                    return new { error = "no such link", count = targets.Length };
                nearby.ClickTarget(targets[targets.Length - 1 - index]);
                return LinkClickReport(nearby.StatusLabel.Text, text);
            }
            case "render":
            {
                // a text as each chat place formats it (nearby, object, an IM or group line all share the format) and as a card shows it
                string t = text ?? "";
                var strings = _strings ?? Viewer.Shell.StringTable.English();
                var theme = UiTheme.Spec;
                var lines = new (string Place, Viewer.Shell.Chat.ChatLine Line)[]
                {
                    ("nearby", new(DateTimeOffset.Now, Viewer.Shell.Chat.ChatLineStyle.Agent, Guid.NewGuid(), "Test User", t)),
                    ("object", new(DateTimeOffset.Now, Viewer.Shell.Chat.ChatLineStyle.Object, Guid.NewGuid(), "Test Object", t, ObjectId: Guid.NewGuid())),
                    ("object_im", new(DateTimeOffset.Now, Viewer.Shell.Chat.ChatLineStyle.Object, Guid.NewGuid(), "Test Object", t, ObjectId: Guid.NewGuid(), Mark: Viewer.Shell.Chat.ChatMark.ObjectMessage)),
                    ("own", new(DateTimeOffset.Now, Viewer.Shell.Chat.ChatLineStyle.Own, Guid.Empty, "", t)),
                };
                var card = new NotecardWindowModel(new NotecardOpenResult(NotecardOpenStatus.Ok, Guid.NewGuid(), "card", "", null, new NotecardDocument(2, t, [])));
                return new
                {
                    chat = lines.Select(l => new { place = l.Place, links = LinkReport(Viewer.Shell.Chat.ChatText.Format(l.Line, theme, strings, false, links: HostLinks.Router), true) }).ToArray(),
                    notecard = LinkReport(card.RenderText(s => s.Replace("[", "[lb]", StringComparison.Ordinal), HostLinks.Router), true),
                };
            }
        }
        return NotecardState();
    }

    private readonly Dictionary<string, Guid> _notecardFixtures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The links in a rich text: how many, and for fixture text the label and the link each shows.</summary>
    private static object LinkReport(string bbcode, bool fixture)
    {
        var found = System.Text.RegularExpressions.Regex.Matches(bbcode, @"\[url=(link:[^\]]*)\](.*?)\[/url\]");
        return new
        {
            count = found.Count,
            links = fixture ? found.Select(m => new { link = Viewer.Shell.Links.LinkText.LinkOf(m.Groups[1].Value), label = m.Groups[2].Value.Replace("[lb]", "[", StringComparison.Ordinal) }).ToArray() : null,
        };
    }

    /// <summary>What the last link click did: the action's kind, the note shown, and the map (open, the place asked for or chosen;
    /// <paramref name="expect"/> is a region name compared here, never returned).</summary>
    private object LinkClickReport(string note, string? expect)
    {
        var a = HostLinks.LastAction;
        var map = _travel?.WorldMap;
        string? region = map?.Selected?.RegionName ?? map?.PendingDestination?.Name;
        var local = map?.Selected?.Local ?? map?.PendingDestination?.Position;
        return new
        {
            kind = a?.Kind.ToString(),
            note_key = a?.NoteKey,
            note_shown = note.Length > 0,
            map_open = _shell?.Shell.Floaters.IsOpen(TravelFeature.MapId) ?? false,
            map_chosen = map?.Selected is not null,
            map_pending = map?.PendingDestination is not null,
            map_region_matches = expect is { Length: > 0 } && region is not null && string.Equals(region, expect, StringComparison.OrdinalIgnoreCase),
            map_local = local is { } l ? new[] { l.X, l.Y, l.Z } : null,
            teleport_ready = map?.Selected is not null,
        };
    }

    private object NotecardState()
    {
        var windows = new List<object>();
        foreach (var item in _notecards?.Open ?? [])
        {
            var panel = _notecards!.PanelOf(item);
            var model = panel?.Model;
            var doc = model?.Opened.Document;
            bool fixture = _notecardFixtures.ContainsValue(item);
            windows.Add(new
            {
                id = item,
                loaded = model is not null,
                status = model?.Opened.Status.ToString(),
                error = model?.Opened.Error,
                text_chars = doc?.PlainText.Length ?? -1,
                shown_chars = panel?.TextView.GetParsedText().Length ?? -1,
                items = doc?.Items.Select(i => new
                {
                    index = i.Index, kind = i.Kind.ToString(),
                    teleport = model!.Offer(i.Index)?.Teleport ?? false, copy = model.Offer(i.Index)?.Copy ?? false,
                    name = fixture ? i.Name : null,
                }).ToArray() ?? [],
                item_rows = panel?.ItemRows.GetChildCount() ?? 0,
                links = model?.Segments.Count(x => x.Kind == NotecardSegmentKind.WebLink) ?? 0,
                places = model?.Segments.Count(x => x.Kind == NotecardSegmentKind.PlaceLink) ?? 0,
                text = fixture ? panel?.TextView.GetParsedText() : null,
                title = fixture ? model?.Title : null,
                status_line = fixture ? panel?.StatusLabel.Text : null,
            });
        }
        return new { open = _notecards?.Open.Count ?? 0, front = _notecards?.Open is { Count: > 0 } o ? o[^1] : Guid.Empty, windows };
    }
#endif
}
