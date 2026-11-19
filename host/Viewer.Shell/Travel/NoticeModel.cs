// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Shell.Travel;

public enum NoticeSeverity { Tip, Important, Error }

/// <summary>One notice on show: a string-table key, its argument (the grid's own words when this viewer has none of its own), and what it is about.</summary>
public sealed record Notice(string Key, string? Arg, AlertKind Kind, NoticeSeverity Severity);

/// <summary>
/// The grid's alerts as notices: the answer to a set-home request and every reason a parcel keeps the agent out (banned, not on the access
/// list, not in the group, any other), whether met by walking or on arriving from a teleport. The official viewer shows a server alert as a
/// transient tip (a leading slash: an important system message, the modal flag: an error), and draws a coloured wall where a nearby parcel
/// refuses the agent; it shows no words for that wall. Here the wall is kept as state (<see cref="Barrier"/>) and the words are the alerts'.
/// An alert with no wording of this viewer's is shown exactly as the grid sent it. Main thread only.
/// </summary>
public sealed class NoticeModel
{
    /// <summary>The most notices kept; the oldest is let go first.</summary>
    public const int Capacity = 20;

    private readonly List<Notice> _notices = new();

    private readonly Func<double> _clock;

    /// <param name="clock">Seconds, for the ban lines' display times; the system clock when null.</param>
    public NoticeModel(Func<double>? clock = null) { _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency); }

    /// <summary>The clock the ban lines run on, in seconds.</summary>
    public double Now => _clock();

    /// <summary>The wall drawn at a refusing parcel's edge.</summary>
    public BanLineState BanLines { get; } = new();

    /// <summary>The notices, oldest first.</summary>
    public IReadOnlyList<Notice> Notices => _notices.ToArray();

    /// <summary>The newest notice; null when there is none.</summary>
    public Notice? Latest => _notices.Count > 0 ? _notices[^1] : null;

    /// <summary>The reason a nearby parcel refuses the agent, as the region last said; null when it has said nothing in this region.</summary>
    public ParcelDenial? Barrier { get; private set; }

    /// <summary>Raised when anything shown changed.</summary>
    public event Action? Changed;

    /// <summary>Raised with the grid's words when it confirms that a notecard or script was saved or updated: the editors show them in their
    /// status line, and no notice is made.</summary>
    public event Action<string>? AssetSaved;

    /// <summary>Raised for the grid's region-restart warning, known by its notification id (RegionRestartMinutes or RegionRestartSeconds),
    /// the alert the official viewer plays its restart sound for, and for OpenSim's warning sent as plain text without that id.</summary>
    public event Action? RestartWarned;

    public void Receive(TransportEvent e)
    {
        switch (e)
        {
            case ServerAlert a:
                Add(a);
                if (a.Key is "RegionRestartMinutes" or "RegionRestartSeconds" || Viewer.Shell.Sound.OpenSimRestartNotice.Matches(a.Text))
                    RestartWarned?.Invoke();
                break;
            case ParcelCollision c:
                Barrier = c.Kind;
                BanLines.Collision(c, _clock());
                Changed?.Invoke();
                break;
            case SimConnected or Disconnected:
                BanLines.Clear();
                if (Barrier is not null)
                {
                    Barrier = null;
                    Changed?.Invoke();
                }
                break;
        }
    }

    private void Add(ServerAlert a)
    {
        var kind = ServerAlerts.Classify(a.Text, a.Key, a.Modal);
        if (kind is AlertKind.ParcelBanned or AlertKind.ParcelBannedWarning or AlertKind.ParcelNoAccess or AlertKind.ParcelNotOnList or AlertKind.ParcelNotInGroup
            || (a.Text ?? "").Contains("Cannot enter parcel", StringComparison.Ordinal))
            BanLines.Denied(_clock());   // the official viewer starts its ban lines on this text or on the matching notifications
        string text = ServerAlerts.Words(a.Text ?? "");   // the region server's leading slash is not part of the words
        if (kind == AlertKind.AssetSaved)
        {
            AssetSaved?.Invoke(text);   // the editor's status line shows it; no pop-up
            return;
        }
        Notice? notice = kind switch
        {
            AlertKind.HomeSet => new("notice.home.set", null, kind, NoticeSeverity.Tip),
            AlertKind.HomeNotAllowed => new("notice.home.not_allowed", null, kind, NoticeSeverity.Tip),
            AlertKind.HomeFailed => new("notice.home.failed", text, kind, NoticeSeverity.Tip),
            AlertKind.ParcelBanned => new("notice.parcel.banned", null, kind, NoticeSeverity.Tip),
            AlertKind.ParcelBannedWarning => new("notice.parcel.banned_warning", null, kind, NoticeSeverity.Tip),
            AlertKind.ParcelNoAccess => new("notice.parcel.no_access", null, kind, NoticeSeverity.Tip),
            AlertKind.ParcelNotOnList => new("notice.parcel.not_on_list", null, kind, NoticeSeverity.Tip),
            AlertKind.ParcelNotInGroup => new("notice.parcel.not_in_group", null, kind, NoticeSeverity.Tip),
            AlertKind.Important => text.Length > 0 ? new("notice.raw", text, kind, NoticeSeverity.Important) : null,
            AlertKind.Error => text.Length > 0 ? new("notice.raw", text, kind, NoticeSeverity.Error) : null,
            _ => text.Length > 0 ? new("notice.raw", text, kind, NoticeSeverity.Tip) : null,
        };
        if (notice is null)
            return;
        _notices.Add(notice);
        if (_notices.Count > Capacity)
            _notices.RemoveAt(0);
        Changed?.Invoke();
    }

    /// <summary>Closes every notice.</summary>
    public void Dismiss()
    {
        if (_notices.Count == 0)
            return;
        _notices.Clear();
        Changed?.Invoke();
    }
}
