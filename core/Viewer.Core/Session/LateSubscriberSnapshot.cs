// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// What the grid said about the agent's friends and groups, for an interface that attaches to the session late. The grid sends the friend
/// list in the login reply with nobody online and tells the client who already is as online notices a moment after the circuit opens (the
/// official viewer reads the same two things: the buddy list of the login reply and the online notices of the region,
/// indra/newview/llcallingcard.cpp, processOnlineNotification); the group list and the active group are asked for as the session reaches the
/// world. If the interface attaches in between, what it is handed must carry what was already said, or it is lost for good. Not
/// thread-safe: the session calls it under its communication lock.
/// </summary>
internal sealed class LateSubscriberSnapshot
{
    private FriendsListed? _list;
    private readonly Dictionary<Guid, bool> _online = new();
    private GroupsListed? _groups;
    private ActiveGroupChanged? _activeGroup;

    /// <summary>Takes in one communication event; only the friend list, a friend's online or offline notice, an ended friendship, the group
    /// list and the active group matter. A friend list decides for the friends it names (the protocol library merges any notice that came
    /// before it into the list it reports).</summary>
    public void Record(TransportEvent e)
    {
        switch (e)
        {
            case FriendsListed listed:
                _list = listed;
                _online.Clear();
                foreach (var f in listed.Friends)
                    _online[f.Id] = f.Online;
                break;
            case FriendPresenceChanged p when _list is not null:
                _online[p.Friend] = p.Online;
                break;
            case FriendshipEnded ended when _list is not null:
                _online.Remove(ended.Friend);
                break;
            case GroupsListed groups:
                _groups = groups;
                break;
            case ActiveGroupChanged active:
                _activeGroup = active;
                break;
        }
    }

    /// <summary>The events that bring a subscriber up to date: the friend list with each friend's last known online state, the group list
    /// and the active group. Empty before the grid has said anything.</summary>
    public IReadOnlyList<TransportEvent> Current()
    {
        var events = new List<TransportEvent>(3);
        if (_list is not null)
            events.Add(new FriendsListed(_list.Friends.Where(f => _online.ContainsKey(f.Id)).Select(f => f with { Online = _online[f.Id] }).ToArray()));
        if (_groups is not null)
            events.Add(_groups);
        if (_activeGroup is not null)
            events.Add(_activeGroup);
        return events;
    }

    public void Clear()
    {
        _list = null;
        _online.Clear();
        _groups = null;
        _activeGroup = null;
    }
}
