// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Session;
using Viewer.Shell.Chat;

namespace Viewer.Shell.Social;

// Behaviour follows the official viewer's group list, active group and title, and group notices, written from a
// behaviour spec in our own words (no code, text or layout copied).

/// <summary>Where a group notice's attachment stands: none, offered and waiting for the user, or answered.</summary>
public enum AttachmentState { None, Offered, Accepted, Declined, Failed }

public sealed record GroupRow(Guid Id, string Name, bool AcceptNotices);

public sealed record GroupTitleRow(Guid Role, string Title, bool Selected);

/// <summary>A received group notice. <see cref="Id"/> is local; the attachment, if any, waits for the user's answer.</summary>
public sealed class GroupNoticeItem
{
    public Guid Id { get; init; }
    public Guid Group { get; init; }
    public Guid Sender { get; init; }
    public string FromName { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTimeOffset At { get; init; }
    public bool HasAttachment { get; init; }
    public string AttachmentName { get; init; } = "";
    public int AssetType { get; init; }
    public Guid OfferId { get; init; }
    public AttachmentState Attachment { get; internal set; }
    public bool Unread { get; internal set; }
}

/// <summary>
/// The groups, the active group and title, and the notices received, as data. The active-group marker moves when the region
/// says so, not when the user clicks. A notice's attachment is only ever answered because the user chose to, once. Main thread only.
/// </summary>
public sealed class GroupsModel
{
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<Guid>? _requestTitles;
    private readonly List<GroupRow> _groups = new();
    private readonly Dictionary<Guid, GroupTitleRow[]> _titles = new();
    private readonly HashSet<Guid> _titlesAsked = new();
    private readonly List<GroupNoticeItem> _notices = new();   // newest first

    public GroupsModel(Func<ActionIntent, ActionResult> submit, Func<DateTimeOffset> clock, Action<Guid>? requestTitles = null)
    {
        _submit = submit;
        _clock = clock;
        _requestTitles = requestTitles;
    }

    public IReadOnlyList<GroupRow> Groups => _groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id).ToArray();

    /// <summary>The active group and this agent's title in it (the empty id: none).</summary>
    public (Guid Id, string Name, string Title) Active { get; private set; }

    public IReadOnlyList<GroupTitleRow> TitlesOf(Guid group) => _titles.TryGetValue(group, out var t) ? t : [];

    /// <summary>Notices received, newest first.</summary>
    public IReadOnlyList<GroupNoticeItem> Notices => _notices.ToArray();

    public int UnreadNotices => _notices.Count(n => n.Unread);

    public (string Key, string? Arg)? Status { get; private set; }

    public event Action? Changed;

    public void Receive(TransportEvent e)
    {
        switch (e)
        {
            case GroupsListed list:
                _groups.Clear();
                _groups.AddRange(list.Groups.Select(g => new GroupRow(g.Id, g.Name, g.AcceptNotices)));
                Changed?.Invoke();
                break;
            case ActiveGroupChanged active:
                Active = active.GroupId == Guid.Empty ? (Guid.Empty, "", "") : (active.GroupId, active.GroupName, active.Title);
                Changed?.Invoke();
                break;
            case GroupTitlesListed titles:
                _titles[titles.GroupId] = titles.Titles.Select(t => new GroupTitleRow(t.Role, t.Title, t.Selected)).ToArray();
                _titlesAsked.Remove(titles.GroupId);
                Changed?.Invoke();
                break;
            case GroupNoticeReceived n:
                _notices.Insert(0, new GroupNoticeItem
                {
                    Id = Guid.NewGuid(), Group = n.GroupId, Sender = n.Sender, FromName = n.FromName,
                    Subject = n.Subject.Reveal(), Text = n.Text.Reveal(), At = n.SentAt ?? _clock(),
                    HasAttachment = n.HasAttachment, AttachmentName = n.AttachmentName, AssetType = n.AssetType, OfferId = n.OfferId,
                    Attachment = n.HasAttachment ? AttachmentState.Offered : AttachmentState.None, Unread = !n.Requested,
                });
                Changed?.Invoke();
                break;
        }
    }

    /// <summary>Asks for the titles a group offers, once, unless they are known or already asked for.</summary>
    public void LoadTitles(Guid group)
    {
        if (group == Guid.Empty || _titles.ContainsKey(group) || !_titlesAsked.Add(group))
            return;
        _requestTitles?.Invoke(group);
    }

    private bool Finish(ActionResult r)
    {
        Status = ChatText.StatusOf(r);
        Changed?.Invoke();
        return r.Sent;
    }

    /// <summary>Asks for a group to become the active one (the empty id: none). The marker moves when the region says so.</summary>
    public bool Activate(Guid group) => Finish(_submit(new ActiveGroupIntent(group)));

    public bool SetTitle(Guid group, Guid role) => Finish(_submit(new ActiveTitleIntent(group, role)));

    /// <summary>The user's answer to a notice's attachment. Sent once: after it went out the offer is answered. When the answer
    /// did not leave (nothing sent), the offer stays open to try again.</summary>
    public bool AnswerAttachment(Guid notice, bool accept)
    {
        var n = _notices.Find(x => x.Id == notice);
        if (n is null || !n.HasAttachment || n.Attachment != AttachmentState.Offered)
            return false;
        if (!Finish(_submit(new GroupNoticeAnswerIntent(n.Sender, n.OfferId, accept, n.AssetType))))
            return false;
        n.Attachment = accept ? AttachmentState.Accepted : AttachmentState.Declined;
        Changed?.Invoke();
        return true;
    }

    public void MarkRead(Guid notice)
    {
        var n = _notices.Find(x => x.Id == notice);
        if (n is { Unread: true })
        {
            n.Unread = false;
            Changed?.Invoke();
        }
    }
}
