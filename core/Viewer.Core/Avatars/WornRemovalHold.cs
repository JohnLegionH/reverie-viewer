// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>
/// Keeps another avatar's worn pieces drawn for a moment when several are removed in a burst, so an avatar logging out (its region kills every piece,
/// then the avatar) is not seen losing its outfit piece by piece. This is a deliberate deviation from the official viewer, which removes each piece at
/// its own kill (process_kill_object, llviewermessage.cpp; LLViewerObjectList::killObject).
/// <list type="bullet">
/// <item>A removal that comes more than <see cref="BurstGapSeconds"/> after the wearer's previous one is a single removal: it applies at once.</item>
/// <item>A removal within that gap of the previous one is part of a burst: the piece stays drawn, and so does every further piece of the burst, until
/// <see cref="HoldSeconds"/> have passed since the burst's latest removal (and never longer than <see cref="MaxHoldSeconds"/> from its first held one).
/// Then all of them go together.</item>
/// <item>If the wearer itself goes while pieces are held, they go with it, in the same frame.</item>
/// </list>
/// Only for other avatars; the caller never offers the user's own avatar's pieces. Times are seconds on any one clock. Main thread only.
/// </summary>
public sealed class WornRemovalHold
{
    /// <summary>How long a held burst stays drawn after its latest removal.</summary>
    public const double HoldSeconds = 0.5;
    /// <summary>Two removals of one wearer this close together are a burst.</summary>
    public const double BurstGapSeconds = 0.25;
    /// <summary>The longest a burst is held, from its first held removal.</summary>
    public const double MaxHoldSeconds = 2.0;

    private sealed class Burst
    {
        public double First, Latest;
        public readonly List<uint> Pieces = new();
    }

    private readonly Dictionary<Guid, double> _lastRemoval = new();   // wearer -> its latest removal
    private readonly Dictionary<Guid, Burst> _held = new();

    /// <summary>Pieces held now.</summary>
    public int Count => _held.Values.Sum(b => b.Pieces.Count);

    /// <summary>A worn piece of <paramref name="wearer"/> was removed. True: keep drawing it (it is held); false: remove it now.</summary>
    public bool Removed(Guid wearer, uint piece, double now)
    {
        bool burst = _lastRemoval.TryGetValue(wearer, out var last) && now - last <= BurstGapSeconds;
        _lastRemoval[wearer] = now;
        if (_held.TryGetValue(wearer, out var b))
        {
            b.Pieces.Add(piece);
            b.Latest = now;
            return true;
        }
        if (!burst) return false;
        b = new Burst { First = now, Latest = now };
        b.Pieces.Add(piece);
        _held[wearer] = b;
        return true;
    }

    /// <summary>The held pieces whose hold has ended by <paramref name="now"/>: remove them now.</summary>
    public IReadOnlyList<uint> Due(double now)
    {
        if (_held.Count == 0) return [];
        List<uint>? due = null;
        foreach (var (wearer, b) in _held.ToList())
            if (now - b.Latest >= HoldSeconds || now - b.First >= MaxHoldSeconds)
            {
                (due ??= new()).AddRange(b.Pieces);
                _held.Remove(wearer);
            }
        return due is null ? [] : due;
    }

    /// <summary>The wearer went: every piece held for it, to remove now with it.</summary>
    public IReadOnlyList<uint> WearerGone(Guid wearer)
    {
        _lastRemoval.Remove(wearer);
        return _held.Remove(wearer, out var b) ? b.Pieces : [];
    }

    /// <summary>A held piece came back (the region sent it again): it is a live piece again and no longer held. True when it was held.</summary>
    public bool Forget(uint piece)
    {
        foreach (var (wearer, b) in _held)
            if (b.Pieces.Remove(piece))
            {
                if (b.Pieces.Count == 0) _held.Remove(wearer);
                return true;
            }
        return false;
    }

    /// <summary>Whether a burst of <paramref name="wearer"/> is held now.</summary>
    public bool Holding(Guid wearer) => _held.ContainsKey(wearer);
}

/// <summary>
/// When the worn meshes of another avatar stop hiding a part of its system body (a mesh body was removed), the part is uncovered only after
/// <see cref="Seconds"/>: the first piece of a logout burst applies at once (<see cref="WornRemovalHold"/>), and if it is the mesh body the system body
/// must not show in the moment before the avatar itself goes. A part hidden again in that time is never uncovered; a part newly hidden is hidden at once.
/// A deviation from the official viewer (updateMeshVisibility uncovers at the detach), towards hiding. Only for other avatars. Main thread only.
/// </summary>
public sealed class BodyUncoverDelay
{
    /// <summary>How long an uncovered part stays hidden.</summary>
    public const double Seconds = WornRemovalHold.HoldSeconds;

    /// <summary>Whether an uncovering is waiting (the caller works the set out again each frame while it is).</summary>
    public bool Pending { get; private set; }

    /// <summary>The slots to hide now: <paramref name="computed"/> (what the worn meshes hide now), plus the slots of <paramref name="before"/> it leaves
    /// out while their delay runs. <paramref name="holding"/>: a burst of the avatar's removed pieces is still drawn; the delay starts once it is not.</summary>
    public IReadOnlySet<string> Next(IReadOnlySet<string> before, IReadOnlySet<string> computed, double now, bool holding = false)
    {
        if (before.All(computed.Contains)) { Pending = false; _since = double.NaN; return computed; }
        if (double.IsNaN(_since) || holding) _since = now;
        if (now - _since >= Seconds) { Pending = false; _since = double.NaN; return computed; }
        Pending = true;
        return computed.Union(before).ToHashSet();
    }

    private double _since = double.NaN;
}
