// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>The viewer statistics report posted to the current region's ViewerStats capability as LLSD XML
/// (llviewerstats.cpp send_viewer_stats posts to the same capability).</summary>
internal sealed partial class LmvTransport : IViewerStatsTransport
{
    public (string Mac, string Id0) DeviceIds => _loginTap.DeviceIds;

    // the library keeps no byte or packet counts per circuit that this build can read, so the traffic block is sent as zeros
    public ViewerStatsTraffic Traffic => ViewerStatsTraffic.None;

    public async Task<bool> PostViewerStatsAsync(IReadOnlyDictionary<string, object?> report, CancellationToken ct)
    {
        Uri? cap;
        try { cap = _client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerStats"); }
        catch (Exception) { return false; }
        if (cap is null)
            return false;
        try
        {
            await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, ToOsd(report), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    internal static OSD ToOsd(object? value) => value switch
    {
        null => new OSD(),
        IReadOnlyDictionary<string, object?> map => ToMap(map),
        IDictionary<string, object?> map => ToMap((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(map)),
        IEnumerable<object?> list when value is not string => new OSDArray(list.Select(ToOsd).ToList()),
        string s => OSD.FromString(s),
        bool b => OSD.FromBoolean(b),
        int i => OSD.FromInteger(i),
        long l => OSD.FromReal(l),
        double d => OSD.FromReal(d),
        float f => OSD.FromReal(f),
        _ => OSD.FromString(value.ToString() ?? ""),
    };

    private static OSDMap ToMap(IReadOnlyDictionary<string, object?> map)
    {
        var m = new OSDMap();
        foreach (var (k, v) in map) m[k] = ToOsd(v);
        return m;
    }
}
