// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell.Chat;

namespace Viewer.Shell.Social;

// Behaviour follows the official viewer's friend list, online notices and friendship offers, written from a behaviour
// spec in our own words (no code, text or layout copied).

/// <summary>What a friend notice says.</summary>
public enum FriendNoticeKind { Online, Offline, Accepted, Declined, Ended, ModifyGranted, ModifyRevoked }

/// <summary>Something the user should be told about a friend (the interface words it from the string table).</summary>
public sealed record FriendNotice(FriendNoticeKind Kind, Guid Friend);

/// <summary>One friend as the list shows them.</summary>
public sealed record FriendRow(Guid Id, string Name, bool Online, FriendRightsFlags Granted, FriendRightsFlags Received);

/// <summary>A friendship offer waiting for an answer.</summary>
public sealed record PendingOffer(Guid From, string Name, string Message, Guid OfferId, bool Online);

/// <summary>
/// The friend list, online marks, pending offers and the actions on them, as data. The list is the login reply's, kept
/// current by the grid's notices; every action goes through the action pipeline, and a friend's row changes when the grid
/// says so (rights) or when the pipeline confirms the send (accepting, ending). Main thread only.
/// </summary>
public sealed class FriendsModel
{
    private sealed class Entry(Guid id, string name, FriendRightsFlags granted, FriendRightsFlags received)
    {
        public Guid Id = id;
        public string Name = name;
        public bool Online;
        public FriendRightsFlags Granted = granted, Received = received;
    }

    /// <summary>What a new friendship starts as: each may see the other online, nothing else.</summary>
    private const FriendRightsFlags DefaultRights = FriendRightsFlags.SeeOnline;

    private readonly Guid _self;
    private readonly NameCache _names;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<bool> _noticesOn;
    private readonly Dictionary<Guid, Entry> _friends = new();
    private readonly HashSet<Guid> _onlineBeforeListed = new();
    private readonly List<PendingOffer> _offers = new();

    public FriendsModel(Guid self, NameCache names, Func<ActionIntent, ActionResult> submit, Func<bool> noticesOn)
    {
        _self = self;
        _names = names;
        _submit = submit;
        _noticesOn = noticesOn;
    }

    /// <summary>The friends, sorted by name ignoring case. A friend with no name of their own shows the cached name, else the short id.</summary>
    public IReadOnlyList<FriendRow> Friends =>
        _friends.Values.Select(f => new FriendRow(f.Id, NameOf(f.Id, f.Name), f.Online, f.Granted, f.Received))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToArray();

    /// <summary>Offers waiting for an answer, oldest first. One per person.</summary>
    public IReadOnlyList<PendingOffer> Offers => _offers.ToArray();

    /// <summary>A string-table key (and its argument) saying why the last action was refused; null after one went through.</summary>
    public (string Key, string? Arg)? Status { get; private set; }

    /// <summary>Raised when anything shown changed.</summary>
    public event Action? Changed;

    /// <summary>Raised when the user should be told something about a friend.</summary>
    public event Action<FriendNotice>? Notice;

    private string NameOf(Guid id, string own) =>
        !string.IsNullOrWhiteSpace(own) ? own : _names.Get(id) ?? NameCache.Placeholder(id);

    /// <summary>A name arrived late: the rows are read again.</summary>
    public void Refresh() => Changed?.Invoke();

    public void Receive(TransportEvent e)
    {
        switch (e)
        {
            case FriendsListed list:
                _friends.Clear();
                foreach (var f in list.Friends)
                    _friends[f.Id] = new Entry(f.Id, f.Name, f.Granted, f.Received) { Online = f.Online || _onlineBeforeListed.Contains(f.Id) };
                Changed?.Invoke();
                break;
            case FriendPresenceChanged p:
                Presence(p);
                break;
            case FriendRightsChanged r when _friends.TryGetValue(r.Friend, out var entry):
                bool hadModify = entry.Received.HasFlag(FriendRightsFlags.ModifyObjects);
                entry.Granted = r.Granted;
                entry.Received = r.Received;
                bool hasModify = entry.Received.HasFlag(FriendRightsFlags.ModifyObjects);
                if (hasModify != hadModify)
                    Notice?.Invoke(new FriendNotice(hasModify ? FriendNoticeKind.ModifyGranted : FriendNoticeKind.ModifyRevoked, r.Friend));
                Changed?.Invoke();
                break;
            case FriendshipOffered o:
                _offers.RemoveAll(x => x.From == o.From);
                _offers.Add(new PendingOffer(o.From, string.IsNullOrWhiteSpace(o.FromName) ? NameOf(o.From, "") : o.FromName, o.Message.Reveal(), o.OfferId, o.Online));
                Changed?.Invoke();
                break;
            case FriendshipAnswered a:
                if (a.Accepted)
                {
                    AddDefault(a.From, a.FromName);
                    Notice?.Invoke(new FriendNotice(FriendNoticeKind.Accepted, a.From));
                }
                else
                    Notice?.Invoke(new FriendNotice(FriendNoticeKind.Declined, a.From));
                Changed?.Invoke();
                break;
            case FriendshipEnded ended when _friends.Remove(ended.Friend):
                Notice?.Invoke(new FriendNotice(FriendNoticeKind.Ended, ended.Friend));
                Changed?.Invoke();
                break;
        }
    }

    private void Presence(FriendPresenceChanged p)
    {
        if (_friends.TryGetValue(p.Friend, out var f))
        {
            if (f.Online == p.Online)
                return;
            f.Online = p.Online;
        }
        else if (!(p.Online ? _onlineBeforeListed.Add(p.Friend) : _onlineBeforeListed.Remove(p.Friend)))
            return;   // an unlisted person's notice that changes nothing we know
        if (_noticesOn())
            Notice?.Invoke(new FriendNotice(p.Online ? FriendNoticeKind.Online : FriendNoticeKind.Offline, p.Friend));
        Changed?.Invoke();
    }

    private void AddDefault(Guid id, string name)
    {
        if (!_friends.ContainsKey(id))
            _friends[id] = new Entry(id, name ?? "", DefaultRights, DefaultRights) { Online = _onlineBeforeListed.Contains(id) };
    }

    private bool Finish(ActionResult r)
    {
        var status = ChatText.StatusOf(r);
        if (Status != status)
            Status = status;
        Changed?.Invoke();
        return r.Sent;
    }

    /// <summary>Offers friendship. Nobody joins the list until they accept.</summary>
    public bool Offer(Guid agent, string message)
    {
        if (agent == _self)
        {
            Status = ("status.friend.no_target", null);
            Changed?.Invoke();
            return false;
        }
        return Finish(_submit(new FriendshipOfferIntent(agent, message)));
    }

    public bool Accept(Guid from) => Answer(from, true);

    public bool Decline(Guid from) => Answer(from, false);

    private bool Answer(Guid from, bool accept)
    {
        var offer = _offers.Find(o => o.From == from);
        if (offer is null)
            return false;
        // an offer stored while this agent was away is answered through the grid's capability
        bool sent = Finish(_submit(new FriendshipAnswerIntent(from, offer.OfferId, accept, ViaCapability: !offer.Online)));
        if (!sent)
            return false;
        _offers.Remove(offer);
        if (accept)
            AddDefault(from, offer.Name);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Ends a friendship. A protected action: <paramref name="confirmed"/> says the user confirmed it.</summary>
    public bool End(Guid friend, bool confirmed)
    {
        if (!_friends.ContainsKey(friend))
            return false;
        if (!Finish(_submit(new FriendshipEndIntent(friend, confirmed))))
            return false;
        _friends.Remove(friend);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sets the complete set of rights this agent gives a friend. The map right goes only with the online right; giving the
    /// modify-objects right where it was not given needs <paramref name="confirmedModify"/>. The row changes when the grid answers.</summary>
    public bool SetRights(Guid friend, FriendRightsFlags rights, bool confirmedModify)
    {
        if (!_friends.TryGetValue(friend, out var f))
            return false;
        if (!rights.HasFlag(FriendRightsFlags.SeeOnline))
            rights &= ~FriendRightsFlags.SeeOnMap;
        bool modifyOk = confirmedModify || !rights.HasFlag(FriendRightsFlags.ModifyObjects) || f.Granted.HasFlag(FriendRightsFlags.ModifyObjects);
        return Finish(_submit(new FriendRightsIntent(friend, rights, modifyOk)));
    }
}
