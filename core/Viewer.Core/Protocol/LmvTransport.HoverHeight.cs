// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>The agent's own hover height, stated to the region through its AgentPreferences capability.</summary>
internal sealed partial class LmvTransport : IHoverHeightTransport
{
    /// <summary>The body of the AgentPreferences request that states the hover height.</summary>
    internal static OSDMap HoverHeightBody(float metres) => new() { ["hover_height"] = OSD.FromReal(HoverHeightRules.Clamp(metres)) };

    public bool HoverHeightSupported
    {
        get
        {
            try
            {
                var sim = _client.Network.CurrentSim;
                bool feature = sim?.Features?.Get(HoverHeightRules.Feature) is { } on && on.AsBoolean();
                return HoverHeightRules.Supported(sim?.Caps?.CapabilityURI(HoverHeightRules.Capability) is not null, feature);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }
    }

    public async Task<bool> SendHoverHeightAsync(float metres, CancellationToken ct)
    {
        if (_disposed || !HoverHeightSupported || _client.Network.CurrentSim?.Caps?.CapabilityURI(HoverHeightRules.Capability) is not { } cap)
            return false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            var (response, _) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, HoverHeightBody(metres), limit.Token).ConfigureAwait(false);
            bool ok = response.IsSuccessStatusCode;
            _log.Info("avatar.hover-sent", CoreLog.F("metres", HoverHeightRules.Clamp(metres)), CoreLog.F("ok", ok));
            return ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("avatar.hover-failed", CoreLog.F("error", ex.GetType().Name));
            return false;
        }
    }
}
