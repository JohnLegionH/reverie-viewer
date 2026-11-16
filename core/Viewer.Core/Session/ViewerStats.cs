// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using Viewer.Core.Identity;

namespace Viewer.Core.Session;

/// <summary>A transport that can post the viewer statistics report to the current region's ViewerStats capability.</summary>
public interface IViewerStatsTransport
{
    /// <summary>Posts <paramref name="report"/> (a tree of dictionaries, strings, numbers and booleans, sent as LLSD) to the region's
    /// ViewerStats capability. False when the region has no such capability or the post failed.</summary>
    Task<bool> PostViewerStatsAsync(IReadOnlyDictionary<string, object?> report, CancellationToken ct);

    /// <summary>The device ids the login sent (the hashed network card address as "mac", the hashed device id as "id0"); empty
    /// strings before a login.</summary>
    (string Mac, string Id0) DeviceIds { get; }

    /// <summary>The traffic so far on the session's circuits.</summary>
    ViewerStatsTraffic Traffic { get; }
}

/// <summary>Bytes and packets in and out, and the failure counts the official report carries.</summary>
public sealed record ViewerStatsTraffic(double KilobytesIn, int PacketsIn, double KilobytesOut, int PacketsOut, int Resent, int Dropped)
{
    public static ViewerStatsTraffic None { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>What the host knows that the core does not: the frame rate, the graphics adapter, the interface language and the name
/// settings. Read on a worker thread; the host gives values it can read from any thread.</summary>
public sealed record ViewerStatsFigures(double Fps, string Gpu, string GpuVendor, string GpuVersion, string Language, int AgentsInView,
    bool DisplayNamesEnabled, bool ShowUsernames)
{
    public static ViewerStatsFigures Unknown { get; } = new(0, "", "", "", "en", 0, true, true);
}

/// <summary>
/// The viewer statistics report, in the shape the official viewer sends it (llviewerstats.cpp send_viewer_stats): the agent block
/// (times, frame rates, version, language, travel, memory), the system block (memory, OS, CPU, the device ids, the graphics adapter),
/// downloads, network traffic and failures, the inventory's state, the misc block and the name settings. The official viewer adds
/// its own debug settings that differ from their defaults to the report it sends on quitting; this viewer has no such settings and sends
/// none. The device ids are the same hashed values the login sends, never a raw network card address.
/// </summary>
public static class ViewerStatsReport
{
    /// <summary>The official period between reports (llviewerstats.h SEND_STATS_PERIOD), in seconds.</summary>
    public const double SendPeriodSeconds = 300;

    /// <summary>Everything the report is built from.</summary>
    public sealed record Inputs(ViewerIdentity Identity, DateTimeOffset Now, double RunSeconds, double ForegroundSeconds, ViewerStatsFigures Figures,
        double PingMs, double MetersTravelled, int RegionsVisited, double MemoryKb, long RamKb, (string Mac, string Id0) DeviceIds,
        ViewerStatsTraffic Traffic, bool InventoryUsable);

    public static Dictionary<string, object?> Build(Inputs i)
    {
        double fps = i.Figures.Fps;
        var agent = new Dictionary<string, object?>
        {
            ["start_time"] = (int)(i.Now.ToUnixTimeSeconds() - (long)i.RunSeconds),
            // the first report of a run carries a run time of 0 until half a period has passed, so its frame rates are not counted
            ["run_time"] = i.RunSeconds < SendPeriodSeconds / 2 ? 0.0 : i.RunSeconds,
            ["foreground_time"] = i.ForegroundSeconds,
            ["fps"] = fps,
            ["normalized_session_jitter"] = 0.0,
            ["normalized_frametime_variance"] = 0.0,
            ["normalized_period_jitter"] = 0.0,
            ["version"] = i.Identity.Channel + " " + i.Identity.Version,
            ["language"] = i.Figures.Language,
            ["sim_fps"] = fps,
            ["agents_in_view"] = i.Figures.AgentsInView,
            ["ping"] = i.PingMs,
            ["meters_traveled"] = i.MetersTravelled,
            ["regions_visited"] = i.RegionsVisited,
            ["mem_use"] = i.MemoryKb,
            ["translation"] = new Dictionary<string, object?>(),
        };
        var system = new Dictionary<string, object?>
        {
            ["ram"] = (int)Math.Min(int.MaxValue, i.RamKb),
            ["os"] = RuntimeInformation.OSDescription,
            ["cpu"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["cpu_sse"] = new List<object?>(),
            ["address_size"] = IntPtr.Size * 8,
            ["os_bitness"] = Environment.Is64BitOperatingSystem ? 64 : 32,
            ["hardware_concurrency"] = Environment.ProcessorCount,
            ["mac_address"] = i.DeviceIds.Mac,
            ["serial_number"] = i.DeviceIds.Id0,
            ["gpu"] = i.Figures.Gpu,
            ["gpu_class"] = 0,
            ["gpu_memory_bandwidth"] = 0.0,
            ["gpu_vendor"] = i.Figures.GpuVendor,
            ["gpu_version"] = i.Figures.GpuVersion,
            ["opengl_version"] = "",
            ["shader_level"] = 5,
        };
        var t = i.Traffic;
        return new Dictionary<string, object?>
        {
            ["agent"] = agent,
            ["system"] = system,
            ["downloads"] = new Dictionary<string, object?> { ["world_kbytes"] = t.KilobytesIn, ["object_kbytes"] = 0.0, ["texture_kbytes"] = 0.0, ["mesh_kbytes"] = 0.0 },
            ["stats"] = new Dictionary<string, object?>
            {
                ["net"] = new Dictionary<string, object?>
                {
                    ["in"] = new Dictionary<string, object?> { ["kbytes"] = t.KilobytesIn, ["packets"] = t.PacketsIn, ["compressed_packets"] = 0, ["savings"] = 0.0 },
                    ["out"] = new Dictionary<string, object?> { ["kbytes"] = t.KilobytesOut, ["packets"] = t.PacketsOut, ["compressed_packets"] = 0, ["savings"] = 0.0 },
                },
                ["failures"] = new Dictionary<string, object?>
                {
                    ["send_packet"] = 0, ["dropped"] = t.Dropped, ["resent"] = t.Resent, ["failed_resends"] = 0, ["off_circuit"] = 0, ["invalid"] = 0, ["missing_updater"] = false,
                },
                ["misc"] = new Dictionary<string, object?> { ["string_1"] = "Unused", ["string_2"] = "Unused", ["int_1"] = 0, ["int_2"] = 0 },
            },
            ["inventory"] = new Dictionary<string, object?> { ["usable"] = i.InventoryUsable, ["validation_info"] = new Dictionary<string, object?>() },
            ["ui"] = new Dictionary<string, object?>(),
            ["DisplayNamesEnabled"] = i.Figures.DisplayNamesEnabled,
            ["DisplayNamesShowUsername"] = i.Figures.ShowUsernames,
        };
    }
}

/// <summary>
/// Sends the report for one session: once on arrival, then every <see cref="ViewerStatsReport.SendPeriodSeconds"/> on a timer of its own
/// (never on the main thread), and once more when the session logs out (llappviewer.cpp: the stats timer starts already elapsed, and
/// the quit sends a last report). Only made for a grid whose policy allows it (<see cref="ViewerFeature.ViewerStatistics"/>).
/// </summary>
public sealed class ViewerStatsSender : IDisposable
{
    private readonly IViewerStatsTransport _transport;
    private readonly Func<ViewerStatsReport.Inputs> _inputs;
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private int _sending;
    private int _sent;

    public ViewerStatsSender(IViewerStatsTransport transport, Func<ViewerStatsReport.Inputs> inputs, TimeSpan? period = null, TimeSpan? firstRetry = null)
    {
        _transport = transport;
        _inputs = inputs;
        var every = period ?? TimeSpan.FromSeconds(ViewerStatsReport.SendPeriodSeconds);
        _timer = new Timer(_ => _ = SendAsync(), null, TimeSpan.Zero, every);
    }

    /// <summary>Reports sent so far.</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>Sends one report now, unless one is already on its way.</summary>
    public async Task<bool> SendAsync()
    {
        if (_stop.IsCancellationRequested || Interlocked.Exchange(ref _sending, 1) == 1)
            return false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            bool ok = await _transport.PostViewerStatsAsync(ViewerStatsReport.Build(_inputs()), limit.Token).ConfigureAwait(false);
            if (ok) Interlocked.Increment(ref _sent);
            return ok;
        }
        catch (Exception) { return false; }   // a report that cannot be sent is dropped, as the official viewer drops it
        finally { Volatile.Write(ref _sending, 0); }
    }

    /// <summary>The last report, on logout: the timer stops first, so nothing follows it.</summary>
    public async Task SendFinalAsync(TimeSpan wait)
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        var send = SendAsync();
        await Task.WhenAny(send, Task.Delay(wait)).ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _stop.Cancel();
    }

    /// <summary>The process's figures the core can read itself: its run time, its memory in use and the machine's memory.</summary>
    public static (double RunSeconds, double MemoryKb, long RamKb) ProcessFigures()
    {
        using var self = Process.GetCurrentProcess();
        double run = (DateTime.Now - self.StartTime).TotalSeconds;
        return (run, self.WorkingSet64 / 1024.0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024);
    }
}
