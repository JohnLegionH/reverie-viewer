// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Session;

namespace Viewer.Shell.Social;

/// <summary>
/// Shows or hides this account's online status for all friends at once, through the rights the viewer already sends. Turning it off
/// takes the see-online right (and the map right that depends on it) from every friend who has it, after keeping what each friend
/// had; turning it on gives back what was kept. It changes only friends it needs to, so it can be called again at any time
/// (when the friend list arrives, when a friend is added) without sending a right twice.
/// <para>What each friend had is kept in the settings file under a key of the account, so it survives a restart between turning the
/// setting off and on again. The friend rights themselves live on the grid; this only decides which to send.</para>
/// </summary>
public sealed class OnlineStatusSharing(FriendsModel friends, SettingsService settings, string account)
{
    private const FriendRightsFlags Online = FriendRightsFlags.SeeOnline, Visible = FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap;

    // friends a take-away has been sent to and the grid has not yet confirmed (so a second call does not send it again)
    private readonly HashSet<Guid> _taking = [], _giving = [];

    private string MemoryKey => "share_online_prior_" + account;

    /// <summary>Brings every friend's rights in line with <paramref name="share"/>.</summary>
    public void Apply(bool share)
    {
        if (share)
            GiveBack();
        else
            TakeAway();
    }

    private void TakeAway()
    {
        var kept = Load();
        bool changed = false;
        foreach (var row in friends.Friends)
        {
            if (!row.Granted.HasFlag(Online))
            {
                _taking.Remove(row.Id);   // without the right already (confirmed, or never had it)
                continue;
            }
            if (_taking.Contains(row.Id))
                continue;
            _taking.Add(row.Id);   // before the send: the friend list may announce a change from inside it
            if (!friends.SetRights(row.Id, row.Granted & ~Visible, confirmedModify: false))
            {
                _taking.Remove(row.Id);
                continue;
            }
            kept[row.Id] = row.Granted;
            changed = true;
        }
        if (changed)
            Save(kept);
    }

    private void GiveBack()
    {
        var kept = Load();
        if (kept.Count == 0)
            return;
        foreach (var (id, rights) in kept.ToList())
        {
            var row = friends.Friends.FirstOrDefault(f => f.Id == id);
            if (row is null || row.Granted == rights)
            {
                kept.Remove(id);   // no longer a friend, or already as before: nothing to give back
                continue;
            }
            if (!_giving.Add(id))
                continue;   // being sent already (the friend list announced a change from inside the send)
            // the user gave these rights before, so giving them again needs no new confirmation
            if (friends.SetRights(id, rights, confirmedModify: true))
            {
                kept.Remove(id);
                _taking.Remove(id);
            }
            _giving.Remove(id);
        }
        Save(kept);
    }

    private Dictionary<Guid, FriendRightsFlags> Load()
    {
        var result = new Dictionary<Guid, FriendRightsFlags>();
        try
        {
            if (JsonNode.Parse(settings.GetText(MemoryKey) ?? "{}") is JsonObject o)
                foreach (var (key, value) in o)
                    if (Guid.TryParse(key, out var id) && value is JsonValue v && v.TryGetValue(out int flags))
                        result[id] = (FriendRightsFlags)flags;
        }
        catch (JsonException)
        {
            // an unreadable memory is the same as none
        }
        return result;
    }

    private void Save(Dictionary<Guid, FriendRightsFlags> kept)
    {
        var o = new JsonObject();
        foreach (var (id, rights) in kept)
            o[id.ToString()] = (int)rights;
        settings.SetText(MemoryKey, o.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }
}
