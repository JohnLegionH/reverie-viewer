// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;
using Viewer.Core.Social;

namespace Viewer.Shell.Social;

// Behaviour follows the official viewer's blocked list, written from a behaviour spec in our own words (no code, text or layout
// copied): NAME-1's spec, section 2.1.

/// <summary>One entry of the Blocked tab. <paramref name="TypeKey"/> is the string-table key of its kind's name.</summary>
public sealed record BlockedRow(MuteKind Kind, Guid Id, string Name, string TypeKey, uint Flags);

/// <summary>
/// The Blocked tab as data: the session's block list as rows, and the actions on it. Every action goes through the session (which
/// sends it by the action pipeline); the rows change when the list does. Main thread only.
/// </summary>
public sealed class BlockedModel
{
    private readonly MuteList _list;
    private readonly Func<MuteKind, Guid, string, BlockOutcome> _block;
    private readonly Func<Guid, string, bool> _unblock;

    public BlockedModel(MuteList list, Func<MuteKind, Guid, string, BlockOutcome> block, Func<Guid, string, bool> unblock)
    {
        _list = list;
        _block = block;
        _unblock = unblock;
        list.Changed += () => Changed?.Invoke();
    }

    /// <summary>The entries, sorted by name ignoring case.</summary>
    public IReadOnlyList<BlockedRow> Rows => _list.Entries
        .Select(e => new BlockedRow(e.Kind, e.Id, e.Name, TypeKey(e.Kind), e.Flags))
        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToArray();

    private static string TypeKey(MuteKind kind) => kind switch
    {
        MuteKind.ByName => "blocked.type.byname",
        MuteKind.Resident => "blocked.type.resident",
        MuteKind.Object => "blocked.type.object",
        MuteKind.Group => "blocked.type.group",
        _ => "blocked.type.external",
    };

    /// <summary>A string-table key for the note over the list (asking the grid; the grid keeps none), or null.</summary>
    public string? NoteKey => _list.State switch
    {
        MuteListState.Requested => "blocked.asking",
        MuteListState.Unavailable => "blocked.unavailable",
        _ => null,
    };

    /// <summary>A string-table key (and its argument) saying what the last action did; null before any.</summary>
    public (string Key, string? Arg)? Status { get; private set; }

    public event Action? Changed;

    public bool BlockResident(Guid id, string name) => Block(MuteKind.Resident, id, name);

    public bool BlockObject(Guid id, string name) => Block(MuteKind.Object, id, name);

    public bool BlockObjectName(string name) => Block(MuteKind.ByName, Guid.Empty, name);

    private bool Block(MuteKind kind, Guid id, string name)
    {
        string shown = (name ?? "").Trim();
        var outcome = _block(kind, id, shown);
        Status = outcome switch
        {
            { Done: true } => (outcome.Result == MuteAddResult.Changed ? "status.blocked.changed" : "status.blocked.added", shown),
            { Sent: { Sent: false } } => ("status.blocked.refused", null),
            { Result: MuteAddResult.Duplicate } => ("status.blocked.duplicate", shown),
            { Result: MuteAddResult.RefusedLinden } => ("status.blocked.linden", null),
            { Result: MuteAddResult.RefusedSelf } => ("status.blocked.self", null),
            { Result: MuteAddResult.RefusedLimit } => ("status.blocked.limit", null),
            _ => ("status.blocked.invalid", null),
        };
        Changed?.Invoke();
        return outcome.Done;
    }

    /// <summary>Unblocks a row. False when nothing matched or the pipeline refused it.</summary>
    public bool Unblock(BlockedRow row)
    {
        bool done = _unblock(row.Id, row.Name);
        if (done)
        {
            Status = ("status.blocked.removed", row.Name);
            Changed?.Invoke();
        }
        return done;
    }
}
