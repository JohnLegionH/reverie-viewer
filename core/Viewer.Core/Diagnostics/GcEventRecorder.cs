// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics.Tracing;

namespace Viewer.Core.Diagnostics;

/// <summary>
/// The runtime's own garbage-collection events, read in process
/// (an <see cref="EventListener"/> on the runtime's event source; nothing installed, nothing outside the process). Each
/// collection: its index (the value <see cref="GC.CollectionCount"/>(0) reaches with it), generation, reason (why the
/// runtime started it: small-object budget, large-object budget...) and type (blocking or background). With
/// <c>allocationTicks</c>, also the runtime's allocation samples (one about every 100 KB allocated): the type allocated
/// and whether it went on the large-object heap. Unlike <see cref="AllocationMeter"/> this allocates (the runtime makes
/// an event object per event, on its own dispatch thread): a few per collection, and one per sample with ticks on.
/// </summary>
public sealed class GcEventRecorder : EventListener
{
    public readonly record struct Collection(long Index, int Generation, int Reason, int Type, long AtTicks);
    public readonly record struct Tick(long AtTicks, bool Large, string Type, long Bytes, long OsThread);

    // the runtime's GC reason values (no static table: Viewer.Core holds no static array, ADR-006)
    public static string ReasonName(int reason) => reason switch
    {
        0 => "alloc_small", 1 => "induced", 2 => "low_memory", 3 => "empty", 4 => "alloc_large", 5 => "out_of_space_soh",
        6 => "out_of_space_loh", 7 => "induced_not_forced", 8 => "internal", 9 => "induced_low_memory", 10 => "induced_compacting",
        11 => "low_memory_host", 12 => "pm_full_gc", 13 => "low_memory_host_blocking", _ => $"reason_{reason}",
    };
    public static string TypeName(int type) => type switch { 0 => "blocking", 1 => "background", 2 => "foreground", _ => $"type_{type}" };

    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private readonly object _lock = new();
    private readonly List<Collection> _collections = new();
    private readonly List<Tick> _ticks = new();
    private int _maxTicks = int.MaxValue;
    private bool _ticksOn, _constructed;
    private EventSource? _runtime;
    private long _ticksDropped;

    /// <summary>Starts recording. <paramref name="allocationTicks"/>: also the allocation samples (verbose level).
    /// <paramref name="maxTicks"/>: samples kept at most (the rest counted as dropped).</summary>
    public static GcEventRecorder Start(bool allocationTicks, int maxTicks = 400_000) => new(allocationTicks, maxTicks);

    // The base constructor reports a source that already exists before this constructor's body runs. Enabling it there at
    // one level and here again at another did not take in the exported viewer (no samples), so the source is enabled once,
    // here, at its level; a source created later is enabled when it is reported.
    private GcEventRecorder(bool allocationTicks, int maxTicks)
    {
        lock (_lock)
        {
            _maxTicks = maxTicks;
            _ticksOn = allocationTicks;
            _constructed = true;
            if (_runtime is { } r) EnableEvents(r, _ticksOn ? EventLevel.Verbose : EventLevel.Informational, GcKeyword);
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name != "Microsoft-Windows-DotNETRuntime") return;
        lock (_lock)
        {
            _runtime = source;
            if (_constructed) EnableEvents(source, _ticksOn ? EventLevel.Verbose : EventLevel.Informational, GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || e.Payload is null || e.PayloadNames is null) return;
        if (e.EventName.StartsWith("GCStart", StringComparison.Ordinal))
        {
            var c = new Collection(Long(e, "Count"), (int)Long(e, "Depth"), (int)Long(e, "Reason"), (int)Long(e, "Type"), e.TimeStamp.Ticks);
            lock (_lock) _collections.Add(c);
        }
        else if (e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            long bytes = e.PayloadNames.Contains("AllocationAmount64") ? Long(e, "AllocationAmount64") : Long(e, "AllocationAmount");
            var t = new Tick(e.TimeStamp.Ticks, Long(e, "AllocationKind") == 1, Payload(e, "TypeName") as string ?? "?", bytes, e.OSThreadId);
            lock (_lock)
            {
                if (_ticks.Count < _maxTicks) _ticks.Add(t);
                else _ticksDropped++;
            }
        }
    }

    private static object? Payload(EventWrittenEventArgs e, string name)
    {
        int i = e.PayloadNames!.IndexOf(name);
        return i < 0 ? null : e.Payload![i];
    }

    private static long Long(EventWrittenEventArgs e, string name) => Payload(e, name) switch
    {
        null => -1,
        var v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    public List<Collection> Collections() { lock (_lock) return _collections.ToList(); }
    public List<Tick> Ticks() { lock (_lock) return _ticks.ToList(); }
    public long TicksDropped { get { lock (_lock) return _ticksDropped; } }
    public bool TicksOn { get { lock (_lock) return _ticksOn; } }
}
