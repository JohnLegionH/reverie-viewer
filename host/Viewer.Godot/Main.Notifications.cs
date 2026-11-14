// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Viewer.Core.Actions;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Notifications;

namespace Viewer.Godot;

/// <summary>
/// Notifications in the running viewer: the shell gains the toast layer, the list floater and the object window when the first session
/// logs in (after chat and travel, whose models the feeder reads); each session gets its own hub, attached as soon as the login reply
/// names the agent, and dropped when the session goes. What a script asks (a dialog, a permission, a web address, a map destination)
/// arrives here; the user's answer goes out through the action pipeline, and a web address opens only after the user chose to.
/// </summary>
public partial class Main
{
    private NotificationFeature.Panels? _notePanels;
    private NotificationHub? _notes;
    private ViewerSession? _notesFor;
    private bool _backendNoticeShown;

    /// <summary>Every frame: attaches a hub to a new session once its chat and travel models exist, drops it from an ended one, and pumps it.</summary>
    private void UpdateNotifications()
    {
        var session = Model?.Session;
        if (_notesFor is not null && (_notesFor != session || _notesFor.IsEnded))
        {
            if (_notes is not null)
                _notesFor.Communication -= _notes.Enqueue;
            _notes = null;
            _notesFor = null;
            _notePanels?.Toasts.Bind(null);
            _notePanels?.List.Bind(null);
        }
        bool ready = session is not null && session != _notesFor && !session.IsEnded && session.AgentId != Guid.Empty
            && _shell is not null && _comms is not null && _commsFor == session && _travel is not null && _travelFor == session && _chatPanels is not null;
        if (ready)
        {
            var s = session!;
            if (_notePanels is null)
            {
                _notePanels = NotificationFeature.Install(_shell!);
                _chatPanels!.Value.Nearby.ObjectClicked += OnObjectClicked;
                _notePanels.Object.MapRequested += OnObjectMapRequested;
            }
            var hub = new NotificationHub(s.Actions.Submit, () => global::Godot.Time.GetTicksMsec() / 1000.0, () => $"{Model?.FirstName} {Model?.LastName}".Trim(), id => s.Names.Get(id));
            s.Actions.LocalExecutor = LocalCommands.Executor(url => global::Godot.OS.ShellOpen(url) == global::Godot.Error.Ok);
            hub.Attach(_comms!.Friends, _travel!.Teleport, _travel.Notices, id => s.Names.Get(id) ?? NameCache.Placeholder(id));
            hub.Prompts.MapDestination += OnMapDestination;
            // blocked objects' and owners' dialogs and requests are dropped; a Block button on a prompt or an offer adds to the block list
            hub.Prompts.UseMuteList(s.Mutes);
            hub.Prompts.BlockObject = (id, name) => s.Block(MuteKind.Object, id, name);
            hub.Offers.IsBlocked = (id, name) => s.Mutes.IsMuted(id, name);
            hub.Offers.Block = (kind, id, name) => s.Block(kind, id, name);
            hub.Offers.FolderFor = type => Enum.IsDefined((Viewer.Core.Inventory.FolderKind)type) ? s.Inventory.FolderOfKind((Viewer.Core.Inventory.FolderKind)type) : Guid.Empty;
            hub.Offers.TrashFolder = () => s.Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.Trash);
            // the notifications now show what the nearby-chat lines and the strip over the view showed
            _comms.FriendNoticesInNearbyChat = false;
            _travelPanels!.Overlay.NoticesShownHere = false;
            s.Communication += hub.Enqueue;
            _notes = hub;
            _notesFor = s;
            _notePanels.Toasts.Bind(hub);
            _notePanels.List.Bind(hub);
            if (!_backendNoticeShown && _strings is not null && GraphicsBackendInfo.Status?.NoticeText(_strings) is { } backendNotice)
            {
                _backendNoticeShown = true;   // said once
                hub.Model.Post(new Notification { Kind = NotificationKind.Tip, Body = NoticeText.Raw(backendNotice), UniqueKey = "backend" });
            }
        }
        _notes?.Pump();
    }

    /// <summary>A script asked for a place on the map: the World Map opens and shows it.</summary>
    private void OnMapDestination(MapDestinationRequested m)
    {
        if (_travel is null)
            return;
        _shell?.Shell.Floaters.Open(TravelFeature.MapId);
        _travel.WorldMap.ShowDestination(m.RegionName, m.Position);
    }

    /// <summary>An object's name in chat was clicked: its window opens with what the viewer knows of it.</summary>
    private void OnObjectClicked(Guid objectId)
    {
        if (_comms?.Nearby.Objects.Find(objectId) is not { } info || _notePanels is null || _commsFor is not { } s)
            return;
        _notePanels.Object.Show(info, info.OwnerId == Guid.Empty ? null : s.Names.Get(info.OwnerId));
        _shell?.Shell.Floaters.Open(NotificationFeature.ObjectId);
    }

    /// <summary>The object window's Show on map: the place the object gave, else the region the user is in and where the object spoke from.</summary>
    private void OnObjectMapRequested(ObjectInfo info)
    {
        if (_travel is null)
            return;
        var place = PlaceText.Parse(info.Place);
        string? region = place?.Region ?? _travelFor?.Region?.Name;
        var position = place?.Position ?? info.RegionPosition ?? System.Numerics.Vector3.Zero;
        if (string.IsNullOrEmpty(region))
            return;
        _shell?.Shell.Floaters.Open(TravelFeature.MapId);
        _travel.WorldMap.ShowDestination(region, position);
    }

#if NV_AUTOMATION
    /// <summary>The dialog command (development builds only): report what waits for the user, answer one, or open the list. The report holds
    /// ids, kinds, counts and the button layout; text is only ever compared with a caller's token (<c>contains</c>), never returned.</summary>
    public object? DialogCall(string action, int nth, int button, string? text, string? token, string[]? labels)
    {
        if (_notes is not { } hub)
            return null;
        hub.Pump();
        var model = hub.Model;
        switch (action)
        {
            case "answer":
            {
                var n = model.List.ElementAtOrDefault(nth);
                if (n is null)
                    return new { answered = false, reason = "no_such_notification" };
                bool done = model.Answer(n.Id, button, text ?? "");
                return new { answered = done, id = n.Id };
            }
            case "open":
                _shell?.Shell.Floaters.Open(NotificationFeature.ListId);
                return new { opened = true };
            default:
            {
                string t = token ?? "";
                bool Has(Notification n) => t.Length > 0 && (Words(n.Body).Contains(t, StringComparison.Ordinal) || n.Details.Any(d => Words(d).Contains(t, StringComparison.Ordinal)));
                string Words(NoticeText? x) => x?.Literal ?? x?.Arg ?? x?.Key ?? "";
                return new
                {
                    unread = model.UnreadCount,
                    toasts = model.Toasts.Count,
                    modal = model.Modal is not null,
                    history = model.History.Count,
                    throttled = hub.Prompts.Throttled, unknown = hub.Prompts.Unknown, muted = hub.Prompts.Muted,
                    waiting = model.List.Select(n => new
                    {
                        id = n.Id, kind = n.Kind.ToString(), source = n.Source, channel = n.Channel, script_dialog = n.ScriptDialog, text_entry = n.TextEntry, caution = n.Caution,
                        buttons = n.Buttons.Count, button_indices = n.Buttons.Select(b => b.Index).ToArray(),
                        rows = n.ScriptDialog ? DialogLayout.For(n.Buttons.Count).Rows.Select(r => r.ToArray()).ToArray() : null,
                        labels_match = labels is null ? (bool?)null : n.Buttons.Select(b => b.Label.Literal).SequenceEqual(labels),
                        permissions = n.Details.Where(d => d.Key is not null).Select(d => d.Key).ToArray(),
                        details = n.Details.Count,
                        contains = Has(n),
                    }).ToArray(),
                };
            }
        }
    }
#endif
}
