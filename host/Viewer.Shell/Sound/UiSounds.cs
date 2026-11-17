// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Assets;

namespace Viewer.Shell.Sound;

/// <summary>What plays a sound: the engine side (the UI audio bus). Main thread only.</summary>
public interface IUiSoundOutput
{
    /// <summary>Plays our file for the slot at a linear gain (the interface level times the slot's level).</summary>
    void PlayOurs(UiSoundSlot slot, float gain);

    /// <summary>Plays the grid's Ogg Vorbis sound. False when the bytes do not decode (nothing is played).</summary>
    bool PlayGrid(UiSoundSlot slot, Guid id, byte[] ogg, float gain);
}

/// <summary>What a request to play a slot came to.</summary>
public enum UiSoundOutcome { PlayedOurs, PlayedGrid, MutedAll, MutedMinimized, Disabled, Off, NotOnGrid, NoGridSound, Fetching }

/// <summary>What the Preferences page says beside a slot.</summary>
public enum UiSoundStatus { Fine, NotOnGrid, NoGridSound, Fetching }

/// <summary>One decision, for checks (the automation's sound log).</summary>
public sealed record UiSoundEvent(UiSoundSlot Slot, UiSoundOutcome Outcome);

/// <summary>Where a grid sound stands: not asked for, being fetched, ready to play, not on this grid, fetched but not playable, or the fetch failed (asked again at the next play).</summary>
public enum GridSoundState { Unknown, Fetching, Ready, NotOnGrid, Undecodable, Failed }

/// <summary>
/// The grid's sound assets by id, fetched through the viewer's asset path (the region's asset capability, with the content cache in
/// front of it) and kept for the session. A grid that answers "not found" does not have the sound; bytes that do not start as an Ogg
/// stream are not a sound this viewer can play (a headless check may say what else its own fake grid plays). Fetches finish on any thread;
/// <see cref="Pump"/> applies them on the main thread.
/// </summary>
public sealed class GridSounds(Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch, Func<byte[], bool>? playable = null)
{
    /// <summary>The largest sound asset accepted (the official viewer's upload limit is ten seconds of audio, far under this).</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private readonly Dictionary<Guid, GridSoundState> _state = new();
    private readonly Dictionary<Guid, byte[]> _bytes = new();
    private readonly ConcurrentQueue<(Guid Id, AssetFetchResult Result)> _done = new();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Raised on the main thread when a sound's state changed after a fetch.</summary>
    public event Action<Guid>? Changed;

    public GridSoundState StateOf(Guid id) => _state.GetValueOrDefault(id, GridSoundState.Unknown);

    /// <summary>Why a sound is not playable: "not found", "larger than 2 MiB", "not Ogg (starts ...)", "fetch failed", "undecodable"; null otherwise.</summary>
    public string? WhyOf(Guid id) => _why.GetValueOrDefault(id);

    private readonly Dictionary<Guid, string> _why = new();

    public byte[]? BytesOf(Guid id) => _bytes.GetValueOrDefault(id);

    /// <summary>Starts a fetch unless one is going on or the answer is known (a failed fetch is tried again).</summary>
    public void Request(Guid id)
    {
        if (id == Guid.Empty || StateOf(id) is not (GridSoundState.Unknown or GridSoundState.Failed))
            return;
        _state[id] = GridSoundState.Fetching;
        Task<AssetFetchResult> task;
        try { task = fetch(id, _stop.Token); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { task = Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)); }
        task.ContinueWith(t => _done.Enqueue((id, t.IsCompletedSuccessfully ? t.Result : AssetFetchResult.Of(AssetFetchStatus.Failed))),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The engine could not decode the bytes: the sound counts as not playable from now on.</summary>
    public void MarkUndecodable(Guid id)
    {
        _bytes.Remove(id);
        _state[id] = GridSoundState.Undecodable;
        _why[id] = "undecodable";
    }

    /// <summary>Main thread: applies the fetches that finished.</summary>
    public void Pump()
    {
        while (_done.TryDequeue(out var d))
        {
            _state[d.Id] = Classify(d.Result, out var bytes, out var why);
            if (bytes is not null)
                _bytes[d.Id] = bytes;
            if (why is null)
                _why.Remove(d.Id);
            else
                _why[d.Id] = why;
            Changed?.Invoke(d.Id);
        }
    }

    /// <summary>Whether bytes start as an Ogg stream (its capture pattern).</summary>
    public static bool LooksLikeOgg(byte[] b) => b.Length > 4 && b[0] == (byte)'O' && b[1] == (byte)'g' && b[2] == (byte)'g' && b[3] == (byte)'S';

    private GridSoundState Classify(AssetFetchResult r, out byte[]? bytes, out string? why)
    {
        bytes = null;
        why = null;
        if (r.Status == AssetFetchStatus.NotFound)
        {
            why = "not found";
            return GridSoundState.NotOnGrid;
        }
        if (r.Status != AssetFetchStatus.Ok || r.Bytes.Length == 0)
        {
            why = "fetch failed";
            return GridSoundState.Failed;
        }
        if (r.Bytes.Length > MaxBytes)
        {
            why = "larger than 2 MiB";
            return GridSoundState.NotOnGrid;
        }
        var b = r.Bytes.ToArray();
        if (!(playable ?? LooksLikeOgg)(b))
        {
            why = "not Ogg (starts " + Convert.ToHexString(b.AsSpan(0, Math.Min(4, b.Length))) + ")";
            return GridSoundState.NotOnGrid;
        }
        bytes = b;
        return GridSoundState.Ready;
    }

    /// <summary>Stops fetches going on (the session ended).</summary>
    public void Cancel() => _stop.Cancel();
}

/// <summary>
/// The interface sounds service: one call per event, <see cref="Play"/>, and the Preferences page's <see cref="Audition"/>. It reads the
/// settings at every play (so a change in Preferences counts at once) and decides: mute all, minimized, the slot's switch and source,
/// then our file or the grid's sound. The grid's sound plays only in the world; on the login screen a slot set to the grid plays our
/// file. A grid sound the grid does not have plays nothing, and the page shows it as not on this grid. A first play of a grid sound
/// that is still being fetched plays when it arrives, if that is within <see cref="PendingWindowSeconds"/>. Main thread only.
/// </summary>
public sealed class UiSounds
{
    /// <summary>How late a grid sound may arrive and still play for the event that asked for it.</summary>
    public const double PendingWindowSeconds = 3;

    /// <summary>How many decisions <see cref="Recent"/> keeps.</summary>
    public const int RecentLimit = 256;

    private readonly SettingsService _settings;
    private readonly IUiSoundOutput _output;
    private GridSounds? _grid;
    private readonly Func<bool> _minimized;
    private readonly Func<double> _clock;
    private readonly List<UiSoundEvent> _recent = new();
    private readonly List<(UiSoundSlot Slot, Guid Id, double At)> _pending = new();
    private int _balance;

    public UiSounds(SettingsService settings, IUiSoundOutput output, GridSounds? grid, Func<bool> minimized, Func<double> clock)
    {
        _settings = settings;
        _output = output;
        _grid = grid;
        _minimized = minimized;
        _clock = clock;
        if (grid is not null)
            grid.Changed += OnGridChanged;
    }

    /// <summary>Takes the grid sounds of a new session (null: none), dropping the last session's and any play still waiting for it.</summary>
    public void UseGrid(GridSounds? grid)
    {
        if (_grid is not null)
        {
            _grid.Changed -= OnGridChanged;
            _grid.Cancel();
        }
        _pending.Clear();
        _grid = grid;
        if (grid is not null)
            grid.Changed += OnGridChanged;
    }

    /// <summary>The kind of grid the session is on: on Second Life every slot plays the grid's sound (see <see cref="UiSoundSettings.SourceOn"/>).</summary>
    public Viewer.Core.Session.GridKind Grid { get; set; }

    /// <summary>Whether a session is in the world (the grid's sounds can be fetched). False on the login screen.</summary>
    public bool InWorld { get; set; }

    /// <summary>The latest decisions, oldest first.</summary>
    public IReadOnlyList<UiSoundEvent> Recent => _recent.ToArray();

    /// <summary>Raised for every decision.</summary>
    public event Action<UiSoundEvent>? Decided;

    public void ClearRecent() => _recent.Clear();

    /// <summary>The slot's event happened.</summary>
    public UiSoundOutcome Play(UiSoundSlot slot) => Record(slot, Decide(slot, audition: false));

    /// <summary>The slot's Play button: plays the current source even when the slot's switch is off.</summary>
    public UiSoundOutcome Audition(UiSoundSlot slot) => Record(slot, Decide(slot, audition: true));

    /// <summary>The level a slot plays at: the interface level times the slot's own.</summary>
    public float GainOf(UiSoundSlot slot) => (float)(_settings.Get(UiSoundSettings.Volume) * _settings.Get(UiSoundSettings.VolumeOf(slot)));

    /// <summary>What the Preferences page shows beside a slot set to the grid.</summary>
    public UiSoundStatus StatusOf(UiSoundSlot slot)
    {
        if (UiSoundSettings.SourceOn(_settings, slot, Grid) != UiSoundSource.Grid)
            return UiSoundStatus.Fine;
        var id = UiSoundCatalog.Of(slot).GridId;
        if (id == Guid.Empty)
            return UiSoundStatus.NoGridSound;
        return _grid?.StateOf(id) switch
        {
            GridSoundState.NotOnGrid or GridSoundState.Undecodable => UiSoundStatus.NotOnGrid,
            GridSoundState.Fetching => UiSoundStatus.Fetching,
            _ => UiSoundStatus.Fine,
        };
    }

    /// <summary>Asks for every grid sound a slot is set to, as the official viewer preloads its interface sounds (in the world only).</summary>
    public void Preload()
    {
        if (!InWorld || _grid is null)
            return;
        foreach (var info in UiSoundCatalog.All)
            if (UiSoundSettings.SourceOn(_settings, info.Slot, Grid) == UiSoundSource.Grid)
                _grid.Request(info.GridId);
    }

    /// <summary>Main thread, every frame: applies finished grid fetches (and plays what was waiting for them).</summary>
    public void Pump() => _grid?.Pump();

    /// <summary>A new balance: plays the money sound the thresholds call for and returns it.</summary>
    public UiSoundSlot? Balance(int balance)
    {
        var slot = UiSoundRules.Money(_balance, balance, _settings.Get(UiSoundSettings.MoneyThreshold));
        _balance = balance;
        if (slot is { } s)
            Play(s);
        return slot;
    }

    private UiSoundOutcome Decide(UiSoundSlot slot, bool audition)
    {
        if (_settings.Get(UiSoundSettings.MuteAll))
            return UiSoundOutcome.MutedAll;
        if (_settings.Get(UiSoundSettings.MuteWhenMinimized) && _minimized())
            return UiSoundOutcome.MutedMinimized;
        var source = UiSoundSettings.SourceOn(_settings, slot, Grid);
        if (source == UiSoundSource.Off)
            return UiSoundOutcome.Off;
        if (!audition && !_settings.Get(UiSoundSettings.EnabledOf(slot)))
            return UiSoundOutcome.Disabled;
        if (source == UiSoundSource.Ours || !InWorld || _grid is null)
        {
            _output.PlayOurs(slot, GainOf(slot));
            return UiSoundOutcome.PlayedOurs;
        }
        var id = UiSoundCatalog.Of(slot).GridId;
        if (id == Guid.Empty)
            return UiSoundOutcome.NoGridSound;
        switch (_grid.StateOf(id))
        {
            case GridSoundState.Ready:
                return PlayGrid(slot, id);
            case GridSoundState.NotOnGrid or GridSoundState.Undecodable:
                return UiSoundOutcome.NotOnGrid;
            default:
                _pending.RemoveAll(p => p.Slot == slot);
                _pending.Add((slot, id, _clock()));
                _grid.Request(id);
                return UiSoundOutcome.Fetching;
        }
    }

    private UiSoundOutcome PlayGrid(UiSoundSlot slot, Guid id)
    {
        if (_grid!.BytesOf(id) is { } bytes && _output.PlayGrid(slot, id, bytes, GainOf(slot)))
            return UiSoundOutcome.PlayedGrid;
        _grid.MarkUndecodable(id);
        return UiSoundOutcome.NotOnGrid;
    }

    private void OnGridChanged(Guid id)
    {
        double now = _clock();
        foreach (var p in _pending.Where(p => p.Id == id).ToArray())
        {
            _pending.Remove(p);
            if (now - p.At > PendingWindowSeconds)
                continue;
            Record(p.Slot, _grid!.StateOf(id) == GridSoundState.Ready ? PlayGrid(p.Slot, id) : UiSoundOutcome.NotOnGrid);
        }
    }

    private UiSoundOutcome Record(UiSoundSlot slot, UiSoundOutcome outcome)
    {
        var e = new UiSoundEvent(slot, outcome);
        _recent.Add(e);
        if (_recent.Count > RecentLimit)
            _recent.RemoveRange(0, _recent.Count - RecentLimit);
        Decided?.Invoke(e);
        return outcome;
    }
}
