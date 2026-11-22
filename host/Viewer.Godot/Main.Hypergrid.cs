// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Core.Map;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Viewer.Shell.Notifications;
using Viewer.Shell.Reconnect;
using Viewer.Shell.Travel;

namespace Viewer.Godot;

/// <summary>
/// Travel between grids (Hypergrid) as the interface meets it: the world map follows the grid the agent is on, a landmark is made with
/// the star and opened from the inventory (its place and grid shown, with Teleport), and a teleport home that the home grid refuses
/// during a visit offers to log out and log back in at home. Main thread only.
/// </summary>
public partial class Main
{
    private MapLinks? _mapLinks;
    private string? _mapGrid;
    private bool _landmarkOpenHooked;

    /// <summary>Every frame: when the agent's region is on another grid than the map shows, the map starts afresh with that grid's own
    /// regions and tiles (two grids have their own maps; a cell on one is not the same cell on the other).</summary>
    private void FollowMapGrid()
    {
        HookLandmarkOpen();
        if (_travel is not { } hub || _travelFor is not { } s)
            return;
        string grid = s.CurrentGrid;
        if (grid.Length == 0 || string.Equals(grid, _mapGrid, StringComparison.OrdinalIgnoreCase))
            return;
        bool first = _mapGrid is null;
        _mapGrid = grid;
        hub.WorldMap.ShowGrid(grid);
        if (first || _mapLinks is not { } links)
            return;
        _mapTiles?.Dispose();
        var tiles = _mapTiles = new MapTileCache((key, ct) => s.FetchMapTileAsync(key, ct));
        links.RequestTile = tiles.Request;
        _travelPanels?.Map.Bind(hub, _mapTiles, Friends);
    }

    /// <summary>The star in the location field: a landmark of where the agent stands, in the Landmarks folder (the region writes it; on a
    /// Hypergrid grid it names that grid).</summary>
    private void MakeLandmark()
    {
        var strings = _strings ?? StringTable.English();
        if (Model?.Session is not { IsEnded: false } s || s.Region is not { } region)
            return;
        var where = s.World.LastSelfMotion is { } self ? self.Motion.Position - s.World.OriginOffset : System.Numerics.Vector3.Zero;
        if (LandmarkMaker.For(s.Inventory.FolderOfKind(FolderKind.Landmark), region.Name, s.AgentParcelName, where) is not { } intent)
        {
            _shell?.ShowNotice(strings.Get("topbar.landmark.no_folder"));
            return;
        }
        var sent = s.Actions.Submit(intent);
        if (sent.Sent)
            _shell?.ShowNotice(strings.Get("topbar.landmark.made"));
    }

    private void HookLandmarkOpen()
    {
        if (_landmarkOpenHooked || _inventoryPanel is null)
            return;
        _inventoryPanel.OpenRequested += id => { if (_inventoryFor?.Inventory.Item(id) is { Kind: ItemKind.Landmark } item) OpenLandmark(item); };
        _landmarkOpenHooked = true;
    }

    /// <summary>A landmark opened from the inventory: its asset is read for the place and the grid it names, and shown with Teleport.</summary>
    private void OpenLandmark(InventoryItemData item)
    {
        if (_inventoryFor is not { IsEnded: false } s || item.AssetId is not { } asset || asset == Guid.Empty)
            return;
        var strings = _strings ?? StringTable.English();
        _ = Task.Run(async () =>
        {
            LandmarkAsset? lm = null;
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var fetched = await s.FetchEditAssetAsync(asset, item.AssetType, limit.Token).ConfigureAwait(false);
                if (fetched.Status == AssetFetchStatus.Ok)
                    lm = LandmarkAsset.Parse(fetched.Bytes.ToArray());
            }
            catch (OperationCanceledException) { }
            Callable.From(() => ShowLandmark(s, item, asset, lm, strings)).CallDeferred();
        });
    }

    private void ShowLandmark(ViewerSession s, InventoryItemData item, Guid asset, LandmarkAsset? lm, StringTable strings)
    {
        string? grid = lm?.Grid ?? LandmarkAsset.GridOfName(item.Name);
        var lines = new System.Collections.Generic.List<string> { item.Name };
        if (!string.IsNullOrWhiteSpace(item.Description))
            lines.Add(strings.Get("landmark.info.place", item.Description));
        lines.Add(lm is null && grid is null ? strings.Get("landmark.info.unreadable") : grid is not null ? strings.Get("landmark.info.grid", grid) : strings.Get("landmark.info.grid_unnamed"));
        _notes?.Model.Post(Confirmations.LandmarkInfo(string.Join("\n", lines), () =>
        {
            if (!s.IsEnded)
                _travel?.Teleport.ToLandmark(asset, grid);
        }));
    }

    /// <summary>The teleport home was refused. While visiting another grid the shared dialog says so, with the grid's words, and offers
    /// to log out and log back in at home; at home the teleport's own failure notice is enough.</summary>
    private void OnHomeRefused(ViewerSession s, (string Key, string? Arg) failure)
    {
        if (s.IsEnded || s.VisitedGrid is null)
            return;
        var strings = _strings ?? StringTable.English();
        string reason = failure.Arg is null ? strings.Get(failure.Key) : strings.Get(failure.Key, failure.Arg);
        _notes?.Model.Post(Confirmations.HomeRefused(reason, () => _ = RelogHomeAsync(s)));
    }

    /// <summary>"Log out and back in at home": the session logs out, and the same login starts again at home with the login held in
    /// memory; with none held, the saved password is used, and only when there is none does the login screen ask, set to start at home.</summary>
    private async Task RelogHomeAsync(ViewerSession s)
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try { await s.LogoutAsync(cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { GD.Print("relog-home logout: " + ex.GetType().Name); }
        }
        _loginScreen?.SetStart(ReconnectModel.HomeStart);
        if (_reconnect is { HasLogin: true } reconnect)
        {
            if (await Task.Run(() => reconnect.HomeAsync(CancellationToken.None)))
                return;
        }
        else if (Model is { } m && m.HasSavedPassword && m.UseSavedPassword())
        {
            string before = m.StartLocation;
            m.StartLocation = ReconnectModel.HomeStart;
            bool ok = await Task.Run(m.LoginAsync);
            m.StartLocation = before;
            if (ok)
                return;
        }
        // the login screen asks for the password; its Start at is set to home
    }
}
