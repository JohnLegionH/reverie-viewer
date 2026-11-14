// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Inventory;

/// <summary>How the login-time background fetch of the whole inventory is paced.</summary>
/// <param name="StartDelay">How long after <see cref="InventoryBackgroundFetch.Begin"/> nothing is asked for, so the first frames and the arrival are not competing with it.</param>
/// <param name="Interval">The least time between two folders asked for.</param>
/// <param name="MaxOutstanding">The most requests out at once while the background fetch is the one asking.</param>
public sealed record BackgroundFetchOptions(TimeSpan StartDelay, TimeSpan Interval, int MaxOutstanding)
{
    public static BackgroundFetchOptions Default { get; } = new(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250), 2);
}

/// <summary>
/// Fetches every folder of the inventory in the background after login, a few at a time, so the window and search have the
/// whole tree without the user asking. Driven by <see cref="Tick"/> from the frame loop with a clock that is passed in.
/// The official viewer does the same after login: the root and the system folders first, the library last (its fetch queue
/// puts the library root at the back); it paces by a cap on requests in flight and a small time slice per frame. This one
/// waits a start delay so the first frames and the region arrival are left alone, asks for one folder per interval, and
/// counts every request out (the user's own included) against its limit, so it yields to what the user opens.
/// </summary>
public sealed class InventoryBackgroundFetch
{
    private readonly InventoryModel _model;
    private readonly Func<TimeSpan> _clock;
    private readonly BackgroundFetchOptions _options;
    private readonly Queue<Guid> _queue = new();
    private readonly HashSet<Guid> _asked = new();
    private TimeSpan _beganAt;
    private TimeSpan _nextAt;
    private bool _begun;
    private int _builtRevision = -1;
    private bool _done;

    // preparing the queue, a slice per tick
    private enum Prep { Idle, Collect, Sort, Enqueue }
    private Prep _prep = Prep.Idle;
    private IReadOnlyList<InventoryFolderData>? _all;
    private List<(int Rank, string Name, Guid Id)>? _keys;
    private int _pos;
    private int _prepRevision;

    public InventoryBackgroundFetch(InventoryModel model, Func<TimeSpan> clock, BackgroundFetchOptions? options = null)
    {
        _model = model;
        _clock = clock;
        _options = options ?? BackgroundFetchOptions.Default;
    }

    /// <summary>The most folders one tick looks at while the queue is being prepared.</summary>
    public const int WorkPerTick = 250;

    /// <summary>How long before the start delay ends the queue starts to be prepared, so it is ready when the first folder is due.</summary>
    internal static readonly TimeSpan PrepareLead = TimeSpan.FromSeconds(2);

    /// <summary>How many folders the last tick looked at (asked the model about) while preparing the queue.</summary>
    public int LastTickLookups { get; private set; }

    /// <summary>True while the queue is being prepared over several ticks.</summary>
    public bool Preparing => _prep != Prep.Idle;

    /// <summary>True when every folder has been asked for once (or there is nothing left to ask for).</summary>
    public bool Done => _done;

    /// <summary>Starts the delay. Call once the session is in world.</summary>
    public void Begin()
    {
        if (_begun)
            return;
        _begun = true;
        _beganAt = _clock();
        _nextAt = _beganAt + _options.StartDelay;
    }

    /// <summary>
    /// Asks for the next folder when it is time. Returns how many folders it asked for (0 or 1). Cheap when it is not time. The
    /// queue is prepared in slices, one per tick, starting <see cref="PrepareLead"/> before the first folder is due, so no tick
    /// pays for the whole tree: the first tick that built it for a 2,000-folder account cost several milliseconds.
    /// </summary>
    public int Tick()
    {
        LastTickLookups = 0;
        if (!_begun || !_model.HasSkeleton)
            return 0;
        var now = _clock();
        if ((_prep != Prep.Idle || (_queue.Count == 0 && _model.Revision != _builtRevision)) && now + PrepareLead >= _nextAt)
        {
            PrepareSlice();
            return 0;
        }
        if (now < _nextAt)
            return 0;
        if (_model.InFlight >= _options.MaxOutstanding)
            return 0;
        while (_queue.Count > 0)
        {
            var id = _queue.Dequeue();
            if (_model.LoadState(id) != FolderLoad.NotLoaded)
                continue;   // the user got there first, or it is gone
            if (_model.EnsureLoaded(id))
            {
                _nextAt = now + _options.Interval;
                return 1;
            }
        }
        return 0;
    }

    /// <summary>
    /// One slice of listing the folders not yet asked for, in the order described above: the folders are looked at (and ranked) a
    /// slice at a time, then sorted, then put in the queue a slice at a time. At most one of those steps runs per tick.
    /// </summary>
    private void PrepareSlice()
    {
        if (_prep == Prep.Idle)
        {
            _prepRevision = _model.Revision;
            _all = _model.AllFolders();
            _keys = new List<(int, string, Guid)>(_all.Count);
            _pos = 0;
            _prep = Prep.Collect;
        }
        switch (_prep)
        {
            case Prep.Collect:
            {
                int start = _pos, end = Math.Min(_pos + WorkPerTick, _all!.Count);
                for (; _pos < end; _pos++)
                {
                    var f = _all[_pos];
                    if (!_asked.Contains(f.Id) && _model.LoadState(f.Id) == FolderLoad.NotLoaded)
                        _keys!.Add((Rank(f), f.Name, f.Id));
                }
                LastTickLookups = end - start;
                if (_pos >= _all.Count)
                {
                    _all = null;
                    _prep = Prep.Sort;
                }
                break;
            }
            case Prep.Sort:
                _keys!.Sort(CompareKeys);
                _pos = 0;
                _prep = Prep.Enqueue;
                break;
            case Prep.Enqueue:
            {
                int end = Math.Min(_pos + WorkPerTick, _keys!.Count);
                for (; _pos < end; _pos++)
                {
                    _asked.Add(_keys[_pos].Id);
                    _queue.Enqueue(_keys[_pos].Id);
                }
                if (_pos >= _keys.Count)
                {
                    _builtRevision = _prepRevision;
                    _done = _queue.Count == 0;
                    _keys = null;
                    _prep = Prep.Idle;
                }
                break;
            }
        }
    }

    private int Rank(InventoryFolderData f) =>
        _model.IsInLibrary(f.Id) ? (f.Id == _model.LibraryRootId ? 40 : 50)
        : f.Id == _model.RootId ? 0
        : f.Kind == FolderKind.CurrentOutfit ? 1
        : f.Kind == FolderKind.MyOutfits ? 2
        : _model.IsSystemFolder(f.Id) ? 10
        : 20;

    private static int CompareKeys((int Rank, string Name, Guid Id) a, (int Rank, string Name, Guid Id) b)
    {
        int c = a.Rank.CompareTo(b.Rank);
        if (c != 0) return c;
        c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }
}
