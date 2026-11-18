// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell.Build;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>
/// Notecards and scripts in the running viewer: the windows come with the inventory when the first session logs in; opening a notecard
/// or a script from the inventory (a double-click, Enter, or Open on its menu) reads it from the grid and shows it, and Open on a script in
/// the Build window's Content tab opens it in the script editor; every window closes when the session ends.
/// </summary>
public partial class Main
{
    private NotecardFeature? _notecards;
    private ScriptEditorFeature? _scripts;
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
            _inventoryPanel.OpenRequested += OpenInventoryScript;            // a script opens in the script editor
            // an item dragged out of the Build window's Contents onto a row goes into that folder (or the folder the row's item is in)
            _inventoryPanel.TreeView.CanTakeTaskItem = (drag, row) => TaskDropFolder(row) != Guid.Empty && _edit?.ContentItem(drag.ItemId) is not null;
            _inventoryPanel.TreeView.TaskItemDropped += (drag, row) =>
            {
                if (TaskDropFolder(row) is var folder && folder != Guid.Empty && _edit is { } e && e.Object() is { } o && o.FullId == drag.ObjectId
                    && _interactFor is { IsEnded: false } s)
                    Log.Info("contents.drag-out", Viewer.Core.Diagnostics.CoreLog.F("sent", e.CopyItemToFolder(drag.ItemId, folder, s.AgentId)));
            };
            _notecardsHooked = true;
        }
        _notecards.Bind(session.Inventory, session.Notecards, session.Actions.Submit, session.NotecardSaves);
        _notecardsBoundFor = session;
        Scripts().Bind(session.Scripts, !Viewer.Shell.Inventory.ScriptEditing.IsSecondLifeGrid(session.LoginUri));
        AttachNewItems();
        AttachGestures(session);
    }

    /// <summary>The script editor windows, made once.</summary>
    private ScriptEditorFeature Scripts() => _scripts ??= new ScriptEditorFeature(_shell!, (where, view) =>
        Log.Info("script.open", Viewer.Core.Diagnostics.CoreLog.F("where", where), Viewer.Core.Diagnostics.CoreLog.F("view", view ?? "")));

    private void OpenInventoryScript(Guid itemId)
    {
        if (_inventoryFor is not { IsEnded: false } session || _shell is null)
            return;
        var item = session.Inventory.Item(itemId);
        Guid target = item?.LinkedId is { } linked && session.Inventory.Item(linked) is { Kind: ItemKind.Script } ? linked : itemId;
        if (session.Inventory.Item(target) is not { Kind: ItemKind.Script } script)
            return;
        Scripts().OpenInventory(target, script.Name);
    }

    /// <summary>Open on a row of the Build window's Content tab: a script opens in the script editor as one inside the prim.</summary>
    private void OpenContentScript(Guid itemId)
    {
        if (_shell is null || _edit is null || (_interactFor ?? _inventoryFor) is not { IsEnded: false } session)
            return;
        if (_edit.ScriptToOpen(itemId, session.AgentId) is not { } access)
            return;
        string name = _edit.Contents.FirstOrDefault(i => i.ItemId == itemId)?.Name ?? "";
        Scripts().Open(access, name);
    }

    /// <summary>Open on a row of the Build window's Contents section, by the item's kind (each bridge's openItem in
    /// llpanelobjectinventory.cpp): a script in the script editor when the object may be modified (else CannotOpenScriptObjectNoMod), a
    /// notecard read-only in the notecard window, a texture in the texture preview.</summary>
    private void OpenContentItem(Guid itemId)
    {
        if (_shell is null || _edit is null || (_interactFor ?? _inventoryFor) is not { IsEnded: false } session)
            return;
        if (_edit.ContentItem(itemId) is not { } item || _edit.Object() is not { } o)
            return;
        bool modify = _edit.Access(o.Id.LocalId).CanModify;
        switch (TaskItemRules.OpenKind(item))
        {
            case TaskOpenKind.Script:
                if (!modify)
                {
                    _editPanel?.ContentTab.Say(_shell.Shell.Strings.Get("edit.content.no_mod_script"));
                    return;
                }
                OpenContentScript(itemId);
                break;
            case TaskOpenKind.Notecard:
                _notecards ??= NewNotecards(_shell);
                if (_notecardsBoundFor != session)
                {
                    // the notecard windows are bound with the inventory; bind them here when the Build window opens a card first
                    _notecards.Bind(session.Inventory, session.Notecards, session.Actions.Submit, session.NotecardSaves);
                    _notecardsBoundFor = session;
                }
                Log.Info("notecard.open-task", Viewer.Core.Diagnostics.CoreLog.F("modify", modify));
                _notecards.OpenTask(item, o.FullId, session.AgentId, modify, session.Notecards);
                break;
            case TaskOpenKind.Texture:
                OpenTaskTexturePreview(item);
                break;
        }
    }

    private ViewerSession? _notecardsBoundFor;

    /// <summary>The own-inventory folder a drop on a row goes into: the row's folder, or the folder of the row's item; empty in the Library.</summary>
    private Guid TaskDropFolder(Guid row)
    {
        if (_inventoryFor is not { IsEnded: false } session)
            return Guid.Empty;
        var inv = session.Inventory;
        Guid folder = inv.Folder(row) is not null ? row : inv.Item(row)?.ParentId ?? Guid.Empty;
        return folder == Guid.Empty || inv.IsInLibrary(folder) ? Guid.Empty : folder;
    }

    /// <summary>An item deleted from the Contents section: its script or notecard window closes.</summary>
    private void ContentItemDeleted(Guid itemId)
    {
        if (_edit?.Object() is { } o)
            _scripts?.CloseItem(o.FullId, itemId);
        _notecards?.CloseItem(itemId);
    }

    /// <summary>The windows, with a log line for a card that did not open (its status and the fixed reason, never its content).</summary>
    private NotecardFeature NewNotecards(ShellRoot shell)
    {
        HostLinks.Perform = PerformLink;   // a link in a card works before a session too (it then only says what it would need)
        var feature = new NotecardFeature(shell, (status, reason) =>
            Log.Info("notecard.not-opened", Viewer.Core.Diagnostics.CoreLog.F("status", status), Viewer.Core.Diagnostics.CoreLog.F("reason", reason ?? "")));
        feature.OpenEmbedded = OpenEmbeddedItem;
        feature.Saved += _ => Log.Info("notecard.saved");
        return feature;
    }

    /// <summary>A click on an embedded texture opens it in the texture preview. The official viewer opens a landmark's place information
    /// (llviewertexteditor.cpp openEmbeddedLandmark); this viewer has no place window yet, so the card's window points to the landmark's
    /// Teleport button instead. A sound, calling card, settings or material has no preview here yet.</summary>
    private void OpenEmbeddedItem(NotecardEmbeddedItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Texture:
                ShowTexturePreview(item.ItemId, item.Name, item.Description, item.AssetId == Guid.Empty ? null : item.AssetId);
                break;
        }
        Log.Info("notecard.embedded-open", Viewer.Core.Diagnostics.CoreLog.F("kind", item.Kind.ToString()));
    }

    private void DetachNotecards()
    {
        _notecards?.Bind(null, null, null);
        _scripts?.Bind(null, true);
        DetachGestures();
    }

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
            case "new":   // Inventory > New: text is the entry id (notecard, script, gesture, skirt, ...)
                if (session is null || text is not { Length: > 0 })
                    return new { error = "no session or entry" };
                OnNewRequested(text, Guid.Empty);
                return new { asked = true };
            case "find_item":   // an own item, by its exact name ("*" any) and kind (text "name|Kind"), the newest first: its id only
            {
                if (session is null || text?.Split('|') is not [var n, var k] || !Enum.TryParse<ItemKind>(k, out var kind))
                    return new { error = "text must be name|Kind" };
                var hit = session.Inventory.AllItems().Where(i => i.Kind == kind && (n == "*" || i.Name == n) && !session.Inventory.IsInLibrary(i.Id)).OrderByDescending(i => i.CreatedAt).FirstOrDefault();   // "*": any of that kind
                return new { id = hit?.Id ?? Guid.Empty };
            }
            case "front":   // the front editor: editable, changed, the text's size, its embedded items, whether it holds a marker (text), saved
            {
                if (_notecards?.Front is not { } f || f.Session is not { } es)
                    return new { open = _notecards?.Open.Count ?? 0, editor = false };
                var inText = es.Buffer.FromDisplay(f.Editor.Text).Items;
                return new
                {
                    open = _notecards.Open.Count, editor = true, item = es.Access.ItemId, can_edit = es.CanEdit, dirty = es.IsDirty, saving = es.Saving,
                    bytes = es.TextBytes, items = inText.Count, kinds = inText.Select(i => i.Kind.ToString()).ToArray(),
                    has_marker = text is { Length: > 0 } && f.Editor.Text.Contains(text, StringComparison.Ordinal),
                    save_visible = f.SaveButton.Visible, saved = f.StatusLabel.Text == (_strings ?? Viewer.Shell.StringTable.English()).Get("notecard.saved"),
                };
            }
            case "type":   // types text at the end of the front editor, as the keyboard does
            {
                if (_notecards?.Front is not { } tf)
                    return new { error = "no window" };
                tf.Editor.SetCaretLine(tf.Editor.GetLineCount() - 1);
                tf.Editor.SetCaretColumn(tf.Editor.GetLine(tf.Editor.GetLineCount() - 1).Length);
                tf.Editor.InsertTextAtCaret(text ?? "");
                return new { typed = (text ?? "").Length };
            }
            case "embed":   // drops an inventory item on the end of the front editor's text, as a drag from the inventory does
            {
                if (_notecards?.Front is not { } ef || id == Guid.Empty)
                    return new { error = "no window or item" };
                ef.DropItems([id], ef.Editor.Text.Length);
                return new { dropped = true };
            }
            case "save":
                return new { sent = _notecards?.Front?.Save() ?? false };
            case "close":   // closes the front window as its close button does (the unsaved question shows when it has changes)
            {
                if (_notecards?.Front is not { } cf)
                    return new { error = "no window" };
                bool closed = cf.MayClose();
                if (closed)
                    cf.Closing?.Invoke();
                return new { closed, asked = cf.UnsavedPrompt.Visible };
            }
            case "gesture_setup":   // the gesture window: trigger (text), a chat step saying "<trigger> works", Active on, then Save
            {
                if (_gesturePanel is not { } gp || !gp.IsVisibleInTree())
                    return new { error = "no gesture window" };
                gp.TriggerField.Text = text ?? "";
                gp.TriggerField.EmitSignal(LineEdit.SignalName.TextChanged, text ?? "");
                gp.Library.Select(2);
                gp.AddStep();
                gp.ChatField.Text = (text ?? "") + " works";
                gp.ChatField.EmitSignal(LineEdit.SignalName.TextChanged, gp.ChatField.Text);
                if (!gp.ActiveCheck.ButtonPressed)
                    gp.ActiveCheck.ButtonPressed = true;
                gp.SaveButton.EmitSignal(BaseButton.SignalName.Pressed);
                return new { steps = gp.Document.Steps.Count, trigger = gp.Document.Trigger };
            }
            case "gesture_state":
                return new
                {
                    open = _gesturePanel?.IsVisibleInTree() ?? false, item = _gestureItem, dirty = _gesturePanel?.IsDirty ?? false,
                    active = _gestureSet?.IsActive(_gestureItem) ?? false, saved = _gesturePanel?.StatusLabel.Text == (_strings ?? Viewer.Shell.StringTable.English()).Get("gesture.saved"),
                    steps = _gesturePanel?.Document.Steps.Count ?? 0,
                };
            case "say":   // says a line in nearby chat as the chat bar does
                if (_comms is null)
                    return new { error = "no chat" };
                return new { cleared = _comms.Nearby.Send(text ?? "") };
            case "heard":   // whether nearby chat holds a line with the marker (text)
                return new { heard = _comms?.Nearby.Lines.Any(l => text is { Length: > 0 } && l.Text.Contains(text, StringComparison.Ordinal)) ?? false };
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

    /// <summary>
    /// The script editor for the development automation: "state" describes the front window (whether it is open, editable, changed, saving,
    /// running, its status key, the errors' places, the caret, the text's length and, for <paramref name="text"/> = "text", the text itself;
    /// live scripts are this check's own); "where" gives the screen centre of a control of the front window by its name (save, running,
    /// reset, mono, code, find, goto, prompt_save, prompt_dont_save, prompt_cancel, error:N, line:N for the start of a line of the code),
    /// or of the Content tab's Open button and a Content row (content_open, content_row:NAME); "list" gives the inventory's own script ids.
    /// Every press and key is sent through the input command by the check itself.
    /// </summary>
    public object? ScriptCall(string action, string? text, string? control)
    {
        if (_shell is null)
            return null;
        var front = _scripts?.Front;
        switch (action)
        {
            case "list":
                if (_inventoryFor is not { IsEnded: false } s)
                    return null;
                return new { scripts = s.Inventory.AllItems().Where(i => i.Kind == ItemKind.Script && !s.Inventory.IsInLibrary(i.Id)).Select(i => new { id = i.Id, name = i.Name }).ToArray() };
            case "where":
            {
                global::Godot.Control? c = null;
                global::Godot.Vector2? at = null;
                if (control == "content_open")
                    c = _editPanel?.ContentTab.OpenButton;
                else if (control is { } row && row.StartsWith("content_row:", StringComparison.Ordinal) && _editPanel?.ContentTab.List is global::Godot.ItemList list)
                {
                    for (int i = 0; i < list.ItemCount; i++)
                        if (list.GetItemText(i) == row["content_row:".Length..])
                        {
                            var r = list.GetItemRect(i);
                            at = list.GetGlobalTransformWithCanvas() * (r.Position + r.Size / 2);
                        }
                }
                else if (control is { } err && err.StartsWith("error:", StringComparison.Ordinal) && front is not null && int.TryParse(err[6..], out int n) && n < front.Errors.ItemCount)
                {
                    var r = front.Errors.GetItemRect(n);
                    at = front.Errors.GetGlobalTransformWithCanvas() * (r.Position + r.Size / 2);
                }
                else if (control is { } ln && ln.StartsWith("line:", StringComparison.Ordinal) && front is not null && int.TryParse(ln[5..], out int line))
                {
                    var p = front.Code.GetPosAtLineColumn(line, 0);
                    if (p.X >= 0)
                        at = front.Code.GetGlobalTransformWithCanvas() * new global::Godot.Vector2(p.X + 4, p.Y - 4);
                }
                else
                    c = front?.FindChild(control ?? "", true, false) as global::Godot.Control;
                if (c is { } ctl && ctl.IsVisibleInTree())
                    at = ctl.GetGlobalTransformWithCanvas() * (ctl.Size / 2);
                return at is { } a ? new { x = a.X, y = a.Y } : new { error = "not shown" };
            }
        }
        var e = front?.Editing;
        return new
        {
            open = _scripts?.OpenPanels.Count ?? 0,
            loaded = e?.Loaded ?? false,
            in_object = e?.InObject ?? false,
            view = e?.View.ToString(),
            can_edit = e?.CanEdit ?? false,
            dirty = e?.IsDirty ?? false,
            saving = e?.Saving ?? false,
            running = e?.Running,
            can_set_running = e?.CanSetRunning ?? false,
            status = e?.StatusKey,
            errors = e?.Messages.Select(m => new { line = m.Line, column = m.Column }).ToArray(),
            caret = front is null ? null : new[] { front.Code.GetCaretLine(), front.Code.GetCaretColumn() },
            code_editable = front?.Code.Editable ?? false,
            prompt = front?.UnsavedPrompt.Visible ?? false,
            text_chars = front?.Code.Text.Length ?? -1,
            text = text == "text" ? front?.Code.Text : null,
            focused = front is not null && front.GetViewport().GuiGetFocusOwner() == front.Code,
        };
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
