// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class Main
{
    /// <summary>A resident's name in nearby chat or in a message was clicked: the avatar menu opens at the pointer (View Profile, IM,
    /// Offer and Request Teleport, Add Friend, and the rest of the official name menu; what is not built is greyed with its reason).</summary>
    private void OnNameClicked(Guid agent) => OpenNameMenu(agent, GetViewport().GetMousePosition());

    private void OpenNameMenu(Guid agent, Vector2 at)
    {
        if (_interactUi is null)
            return;
        var entries = NameMenuEntries(agent);
        if (entries.Count == 0)
            return;
        _interactUi.Menu.Open(at, entries, id => ChooseNameEntry(agent, id), GetViewport().GetVisibleRect().Size);
    }

    private IReadOnlyList<ContextEntry> NameMenuEntries(Guid agent)
    {
        if (_commsFor is not { } s || _comms is not { } hub)
            return [];
        return ContextMenus.ForName(new AvatarMenuContext(agent == s.AgentId, hub.Friends.Friends.Any(f => f.Id == agent), s.Mutes.Find(agent) is not null, s.GridFeatures.NoCurrency));
    }

    /// <summary>Runs one entry of the name menu. False when it is not an enabled entry of that menu.</summary>
    private bool ChooseNameEntry(Guid agent, string id)
    {
        if (_commsFor is not { } s || _comms is not { } hub || _shell is null)
            return false;
        if (NameMenuEntries(agent).FirstOrDefault(e => e.Id == id) is not { Enabled: true })
            return false;
        var strings = _shell.Shell.Strings;
        string name = s.Names.Get(agent) ?? NameCache.Placeholder(agent);
        switch (id)
        {
            case "im":
                hub.Ims.OpenWith(agent);
                _shell.Shell.Floaters.Open(ChatFeature.ConversationsId);
                break;
            case "offer_teleport": _travel?.Teleport.OfferTo(agent, strings.Get("interact.tp.offer")); break;
            case "request_teleport": _travel?.Teleport.AskToBeTeleportedBy(agent, strings.Get("interact.tp.request")); break;
            case "add_friend": hub.Friends.Offer(agent, ""); break;
            case "block": s.Block(MuteKind.Resident, agent, name); break;
            case "unblock": s.Unblock(agent, name); break;
            case "copy_name": DisplayServer.ClipboardSet(name); break;
            default: return false;
        }
        return true;
    }

    /// <summary>A friend's online or offline toast was pressed: the conversation with that friend opens and shows.</summary>
    private void OpenConversationWith(Guid agent)
    {
        if (_comms?.Ims.OpenWith(agent) is not null)
            _shell?.Shell.Floaters.Open(ChatFeature.ConversationsId);
    }

    /// <summary>The name-menu command of the development automation: the menu's entries (id and whether enabled, never a name), and with an
    /// entry id the entry run as a click on it does.</summary>
    public object? NameMenuCall(Guid agent, string? entry)
    {
        if (_comms is null || _commsFor is null)
            return null;
        var entries = NameMenuEntries(agent);
        bool? chosen = string.IsNullOrEmpty(entry) ? null : ChooseNameEntry(agent, entry);
        return new { entries = entries.Select(e => e.Id).ToArray(), enabled = entries.Where(e => e.Enabled).Select(e => e.Id).ToArray(), chosen };
    }
}
