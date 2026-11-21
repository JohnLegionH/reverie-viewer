// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>The grid's own settings from the agent's region's simulator features, and the money balance.</summary>
internal sealed partial class LmvTransport : IGridFeaturesSource
{
    /// <summary>The grid's own settings from the agent's region's OpenSim extras, with the login reply's values behind them. Read when
    /// asked: the region's features arrive once per region and the library raises no event for them.</summary>
    public GridFeatures GridFeatures(GridLoginValues? gridInfo = null)
    {
        IReadOnlyDictionary<string, object?>? extras = null;
        try { extras = LmvGridFeatures.Extras(_client.Network.CurrentSim?.Features?.Get("OpenSimExtras")); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warn("grid.features-unreadable", CoreLog.F("error", ex.GetType().Name)); }
        string? map = _client.Network.LoginResponseData?.MapServerUrl;
        var login = (gridInfo ?? new GridLoginValues()) with { MapServerUrl = string.IsNullOrWhiteSpace(map) ? gridInfo?.MapServerUrl : map };
        var features = Session.GridFeatures.From(extras, login);
        if (features.GridUrl is { } url)
            _grids.Learned(url);   // the region says which grid it is on
        return features;
    }

    /// <summary>Asks the region for the money balance (MoneyBalanceRequest; the answer is a <see cref="BalanceChanged"/> event).</summary>
    public void RequestBalance()
    {
        if (_disposed || !_client.Network.Connected)
            return;
        _client.Self.RequestBalance();
    }

    public bool HasRegionCapability(string name) => _client.Network.CurrentSim?.Caps?.CapabilityURI(name) is not null;

    private void OnMoneyBalance(object? sender, BalanceEventArgs e) => Raise(new BalanceChanged(e.Balance));
}
