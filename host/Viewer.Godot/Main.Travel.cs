// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Viewer.Core.Map;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Social;
using Viewer.Shell.Travel;

namespace Viewer.Godot;

/// <summary>
/// Teleport, the world map and the mini-map in the running viewer: the shell gains the World menu, the toolbar buttons, the
/// floaters and the progress panel when the first session logs in; each session gets its own hub and tile cache, attached as soon as
/// the login reply names the agent so no early message is missed, and dropped when the session goes.
/// </summary>
public partial class Main
{
    private TravelFeature.Panels? _travelPanels;
    private TravelHub? _travel;
    private MapTileCache? _mapTiles;
    private ViewerSession? _travelFor;
    private Presentation.BanWallLayer? _banWall;
    private readonly BanLineMode _banLineMode = BanLineModeFromArgs(global::Godot.OS.GetCmdlineUserArgs());

    /// <summary>The ban lines' setting: --ban-lines=hide|collision|proximity (default collision, as the official viewer's).</summary>
    private static BanLineMode BanLineModeFromArgs(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--ban-lines=", StringComparison.Ordinal));
        return arg?["--ban-lines=".Length..].Trim('"') switch { "hide" => BanLineMode.Hide, "proximity" => BanLineMode.OnProximity, _ => BanLineMode.OnCollision };
    }

    /// <summary>Every frame: draws the wall at the edge of a parcel that refuses the agent, while the notice model says it is on show.</summary>
    private void UpdateBanWall()
    {
        if (_stage is null || _travel is not { } hub || _travelFor is not { } s || s.World.LastSelfMotion is not { } self)
        {
            _banWall?.Update(new BanLineState(), default, (_, _) => 0f, 0);   // nothing to draw: hide it
            return;
        }
        if (_banWall is null)
        {
            _banWall = new Presentation.BanWallLayer { Name = "ban-wall" };
            _stage.AddChild(_banWall);
        }
        var state = hub.Notices.BanLines;
        state.Mode = _banLineMode;
        // the wall belongs to the region the collision message came from (a neighbour's parcel when the agent walks along the border);
        // it is built in that region's own coordinates and placed at the region's offset from the anchor
        var world = s.World;
        var p = self.Motion.Position;
        var at = new System.Numerics.Vector2(p.X + world.OriginOffset.X, p.Y + world.OriginOffset.Y);   // the agent in the anchor's frame
        if (state.Sim is { } wallSim && s.Worlds.FirstOrDefault(w => Equals(w.Sim, wallSim)) is { } wallRegion)
            world = wallRegion.World;
        var offset = new System.Numerics.Vector2(world.OriginOffset.X, world.OriginOffset.Y);
        _banWall.RegionOffset = offset;
        _banWall.Update(state, at - offset, (x, y) => Presentation.BanWallLayer.GroundFrom(world, x, y), hub.Notices.Now, state.Cells);
    }

    private void FreeBanWall()
    {
        if (_banWall is null) return;
        if (IsInstanceValid(_banWall)) { _banWall.GetParent()?.RemoveChild(_banWall); _banWall.Free(); }
        _banWall = null;
    }

    /// <summary>Every frame: attaches a hub to a new session, drops it from an ended one, and pumps the hub.</summary>
    private void UpdateTravel()
    {
        var session = Model?.Session;
        bool attach = session is not null && session != _travelFor && !session.IsEnded && session.AgentId != Guid.Empty;
        bool drop = _travelFor is not null && (_travelFor != session || _travelFor.IsEnded);
        if (attach || drop)
        {
            if (_travelFor is not null && _travel is not null)
                _travelFor.Communication -= _travel.Enqueue;
            _travel = null;
            _travelFor = null;
            _mapTiles?.Dispose();
            _mapTiles = null;
            if (attach && _shell is not null)
            {
                _travelPanels ??= TravelFeature.Install(_shell, UserSettings(null));
                InstallEnvironmentMenu();
                var s = session!;
                var tiles = _mapTiles = new MapTileCache((key, ct) => s.FetchMapTileAsync(key, ct));
                var links = _mapLinks = new MapLinks
                {
                    RequestBlocks = (a, b, c, d) => s.RequestMapBlocks(a, b, c, d),
                    RequestName = name => s.RequestRegionNamed(name),
                    RequestFriend = friend => s.RequestFriendLocation(friend),
                    FriendGrantsMap = id => Friends().Any(f => f.Id == id && f.Received.HasFlag(FriendRightsFlags.SeeOnMap)),
                    Own = () => OwnPlace(s),
                    RequestTile = tiles.Request,
                };
                _travel = new TravelHub(s.AgentId, s.Names, s.Actions.Submit, UserSettings(null), () => DateTimeOffset.Now, links,
                    () => s.Region is { } here ? (MapMath.HandleOf(here.GridX, here.GridY), here.Name) : null,
                    () => s.World.AvatarOf(s.AgentId) is { } me ? s.World.AttachmentRoots(me.Id.LocalId).Count : 0);
                s.Communication += _travel.Enqueue;
                _travel.Teleport.Arrived += () => EnvChoice.TeleportFinished();   // a finished teleport puts the region's own environment back when the setting asks for it
                _travel.Teleport.Arrived += () => _addons?.TeleportArrived();   // add-ons that read the world hear of each teleport that ends
                _travel.Teleport.HomeFailed += failure => OnHomeRefused(s, failure);
                _mapGrid = null;
                _travelFor = s;
            }
            _travelPanels?.Overlay.Bind(_travel);
            _travelPanels?.Teleport.Bind(_travel, Friends);
            _travelPanels?.Map.Bind(_travel, _mapTiles, Friends);
            _travelPanels?.Mini.Bind(_travel, MiniMapInputNow);
        }
        FollowMapGrid();
        _travel?.Pump();
        UpdateBanWall();
    }

    private IReadOnlyList<FriendRow> Friends() => _comms?.Friends.Friends ?? [];

    /// <summary>Where the agent is: its region (by handle) and its position in it, once the region has stated both.</summary>
    private static (ulong Handle, System.Numerics.Vector3 Position)? OwnPlace(ViewerSession s) =>
        s.Region is { } r && s.World.LastSelfMotion is { } m ? (MapMath.HandleOf(r.GridX, r.GridY), m.Motion.Position - s.World.OriginOffset) : null;   // the motion is in the anchor region's frame; the place is the region's own

    /// <summary>The mini-map's input for this frame: the agent's place, the camera's heading and field of view, the region's list of
    /// avatars and who among them is a friend.</summary>
    private MiniMapInput? MiniMapInputNow()
    {
        if (_travelFor is not { } s || s.Region is not { } region || s.World.LastSelfMotion is not { } self || _camera is null)
            return null;
        var forward = -_camera.GlobalTransform.Basis.Z;   // the engine's axes: x east, y up, z south
        float heading = MathF.Atan2(-forward.Z, forward.X);
        var view = GetViewport().GetVisibleRect().Size;
        float aspect = view.Y > 0 ? view.X / view.Y : 16f / 9f;
        float fov = 2f * MathF.Atan(MathF.Tan(float.DegreesToRadians(_camera.Fov) / 2f) * aspect);
        var friends = Friends().Select(f => f.Id).ToHashSet();
        return new MiniMapInput(s.AgentId, self.Motion.Position - s.World.OriginOffset, heading, fov, region.SizeX, region.SizeY, s.CoarseLocations, friends.Contains);
    }

#if NV_AUTOMATION
    /// <summary>The teleport command (development builds only): start a teleport, cancel, answer an offer, or report the state. Answers
    /// ids, flags, keys and counts; never a name or a line of text.</summary>
    public object? TeleportCall(string action, string? region, float x, float y, float z, Guid agent, double gridX = -1, double gridY = -1)
    {
        if (_travel is not { } hub)
            return null;
        hub.Pump();
        var t = hub.Teleport;
        switch (action)
        {
            case "region":
            {
                // a grid cell (grid_x, grid_y, in regions) names a region without its name; the position is within that region
                bool sent = gridX >= 0 && gridY >= 0
                    ? t.ToHandle(MapMath.HandleOf((int)gridX, (int)gridY), new System.Numerics.Vector3(x, y, z))
                    : t.ToRegion(region ?? "", new System.Numerics.Vector3(x, y, z));
                return new { sent, status = t.Status?.Key };
            }
            case "home":
            {
                bool sent = t.Home();
                return new { sent, status = t.Status?.Key };
            }
            case "set_home":
            {
                bool sent = t.SetHome();
                return new { sent, status = t.Status?.Key };
            }
            case "ask":
            {
                bool sent = t.AskToBeTeleportedBy(agent, "May I come to you?");
                return new { sent, status = t.Status?.Key };
            }
            case "offer_back":
            case "refuse":
            {
                bool sent = action == "offer_back" ? t.OfferToRequester(agent) : t.DeclineRequest(agent);
                return new { sent, status = t.Status?.Key };
            }
            case "cancel":
            {
                bool sent = t.Cancel();
                return new { sent, status = t.Status?.Key };
            }
            case "offer":
            {
                bool sent = t.OfferTo(agent, "Join me here.");
                return new { sent, status = t.Status?.Key };
            }
            case "accept":
            case "decline":
            {
                bool sent = action == "accept" ? t.Accept(agent) : t.Decline(agent);
                return new { sent, status = t.Status?.Key };
            }
            default:
                return new
                {
                    stage = t.Stage.ToString(), busy = t.Busy, can_cancel = t.CanCancel, arrivals = t.Arrivals,
                    progress = t.Progress?.Key, failure = t.Failure?.Key, failure_reason = t.Failure?.Arg,   // the grid's own words for a reason this viewer has no text for: a grid message, nobody's
                    offers = t.Offers.Select(o => new { from = o.From }).ToArray(),
                    requests = t.Requests.Select(o => new { from = o.From }).ToArray(),
                    notices = hub.Notices.Notices.Select(n => new { kind = n.Kind.ToString(), key = n.Key, words = n.Arg }).ToArray(),   // the words, when this viewer has none of its own, are the grid's (a region's alert), nobody's
                    barrier = hub.Notices.Barrier?.ToString(),
                    ban_lines = new { shown = _banWall?.Shown ?? false, pieces = _banWall?.Pieces ?? 0 },   // the wall at a refusing parcel: drawn now, and how many pieces
                    region = _travelFor?.Region is { } r ? new { grid_x = r.GridX, grid_y = r.GridY } : null,
                    position = _travelFor?.World.LastSelfMotion is { } me ? new { x = me.Motion.Position.X, y = me.Motion.Position.Y, z = me.Motion.Position.Z } : null,   // the agent's own place in its region
                };
        }
    }

    /// <summary>The map command (development builds only): open the windows, search, choose, teleport to the chosen place, find a friend,
    /// or report the world map's and the mini-map's state as counts and ids.</summary>
    public object? MapCall(string action, string? name, Guid agent)
    {
        if (_travel is not { } hub || _travelFor is not { } s)
            return null;
        hub.Pump();
        var map = hub.WorldMap;
        switch (action)
        {
            case "open":
                _shell?.Shell.Floaters.Open(TravelFeature.MapId);
                _shell?.Shell.Floaters.Open(TravelFeature.MiniMapId);
                return new { opened = true };
            case "search":
            {
                bool sent = map.Search(name ?? "");
                return new { sent, status = map.Status?.Key };
            }
            case "choose":
            {
                var found = map.Results.Concat(map.Regions).FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                bool chosen = found is not null && map.Choose(found);
                return new { chosen, handle = map.Selected?.Handle };
            }
            case "centre":
                map.CentreOnOwn();
                return new { x = map.CentreX, y = map.CentreY };
            case "click":
            {
                var size = _travelPanels?.Map.View?.Size ?? new global::Godot.Vector2(800, 600);
                bool chosen = map.Click(size.X / 2, size.Y / 2, size.X, size.Y);
                return new { chosen, handle = map.Selected?.Handle, spot_z = map.Selected?.Local.Z, status = map.Status?.Key };
            }
            case "teleport":
            {
                bool sent = map.TeleportToSelected();
                return new { sent, status = map.Status?.Key ?? hub.Teleport.Status?.Key };
            }
            case "neighbour":
            {
                // name is the grid offset "dx,dy" from the agent's own region, or "any" for any other region the map holds: teleports to the middle of that region by its handle (a name is never answered)
                var own = s.Region;
                var offset = (name ?? "").Split(',');
                MapRegion? next = null;
                if (own is not null && name == "any")
                    next = map.Regions.FirstOrDefault(r => r.GridX != own.GridX || r.GridY != own.GridY);   // any other region the map knows
                else if (own is not null && offset.Length == 2 && int.TryParse(offset[0], out int dx) && int.TryParse(offset[1], out int dy))
                    next = map.Regions.FirstOrDefault(r => r.GridX == own.GridX + dx && r.GridY == own.GridY + dy);
                if (next is null)
                    return new { sent = false, found = false, status = (string?)null };
                bool sent = hub.Teleport.ToHandle(MapMath.HandleOf(next.GridX, next.GridY), new System.Numerics.Vector3(128, 128, 40));
                return new { sent, found = true, status = hub.Teleport.Status?.Key };
            }
            case "friend":
            {
                bool asked = map.ShowFriend(agent);
                return new { asked, status = map.Status?.Key };
            }
            default:
            {
                var size = _travelPanels?.Map.View?.Size ?? new global::Godot.Vector2(800, 600);
                var mini = MiniMapInputNow() is { } input ? hub.MiniMap.Build(input, 200, 200) : null;
                return new
                {
                    scale = map.PixelsPerRegion, level = map.Level, regions = map.Regions.Count, results = map.Results.Count,
                    search_pending = map.SearchPending, search_ms = map.LastSearchTime?.TotalMilliseconds, search_status = map.Status?.Key,
                    tiles = map.Tiles.Count, tiles_ready = map.Tiles.Count(t => _mapTiles?.TryGet(t.Key, out _) == true),
                    tile_fetches = _mapTiles?.Fetches ?? 0, tile_in_flight = _mapTiles?.InFlight ?? 0, blocks = map.BlockRequests,
                    selected = map.Selected is { } sel ? new { handle = sel.Handle, x = sel.Local.X, y = sel.Local.Y, z = sel.Local.Z } : null,
                    map_open = _shell?.Shell.Floaters.IsOpen(TravelFeature.MapId) ?? false,
                    markers = map.Markers(size.X, size.Y).Select(m => m.Kind.ToString()).ToArray(),
                    coarse = s.CoarseLocations.Count,
                    region_sizes = map.Regions.Select(r => new { grid_x = r.GridX, grid_y = r.GridY, size_x = r.SizeX, size_y = r.SizeY }).ToArray(),
                    minimap = mini is null ? null : new
                    {
                        rotated = mini.Rotated, scale = mini.PixelsPerRegion,
                        // the drawn outline of the agent's region, in metres (one edge's pixels over pixels per 256 m)
                        outline_m = mini.RegionCorners.Count < 2 || mini.PixelsPerRegion <= 0 ? 0 : Math.Round(Math.Sqrt(Math.Pow(mini.RegionCorners[1].X - mini.RegionCorners[0].X, 2) + Math.Pow(mini.RegionCorners[1].Y - mini.RegionCorners[0].Y, 2)) / mini.PixelsPerRegion * 256),
                        dots = mini.Dots.Select(d => new { id = d.Id, friend = d.Friend }).ToArray(),
                    },
                };
            }
        }
    }
#endif
}
