// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Map;
using Viewer.Core.Session;

namespace Viewer.Shell.Travel;

// Behaviour follows the official viewer's world map (its zoom levels, tile grid, region blocks, name search, chosen spot and
// friend tracking), written from a behaviour spec in our own words (no code, text or layout copied).

/// <summary>A region the grid's map told us about.</summary>
public sealed record MapRegion(string Name, int GridX, int GridY, int SizeX, int SizeY, Guid? MapImageId, int Access)
{
    public ulong Handle => MapMath.HandleOf(GridX, GridY);
}

/// <summary>A chosen spot: a region (by handle and name) and a position in it.</summary>
public sealed record MapSpot(ulong Handle, string RegionName, Vector3 Local);

/// <summary>A tile placed in the window: its key and its top-left corner and side in pixels.</summary>
public sealed record MapTilePlacement(MapTileKey Key, float X, float Y, float Size);

public enum MapMarkerKind { Own, Selected, Friend }

public sealed record MapMarker(MapMarkerKind Kind, float X, float Y, Guid Id = default);

/// <summary>What the map needs from outside: asking the grid, asking for tiles, the teleport and who may be found.</summary>
public sealed class MapLinks
{
    public Action<int, int, int, int> RequestBlocks = (_, _, _, _) => { };
    public Action<string> RequestName = _ => { };
    public Action<Guid> RequestFriend = _ => { };
    public Func<Guid, bool> FriendGrantsMap = _ => false;
    public Func<ulong, Vector3, bool> Teleport = (_, _) => false;
    /// <summary>A teleport to a place on another grid by its address (the search box took a Hypergrid address).</summary>
    public Func<Viewer.Core.Links.TravelAddress, bool> TeleportAddress = _ => false;
    public Func<bool> Home = () => false;
    public Func<(ulong Handle, Vector3 Position)?> Own = () => null;
    public Func<MapTileKey, MapTileState> RequestTile = _ => MapTileState.None;
}

/// <summary>
/// The world map as data: the regions known, the window (centre and zoom), the tiles it needs, search, the chosen spot and the
/// friends found. Coordinates are global metres (x east, y north); the window is in pixels with y down. No engine types; main thread only.
/// </summary>
public sealed class WorldMapModel
{
    public const double DefaultScale = 128, MinScale = 8, MaxScale = 4096;

    /// <summary>Region details (blocks, names) are wanted up to this detail level; coarser views are tiles only.</summary>
    public const int DetailLevel = 3;

    /// <summary>A region's name is drawn once a region is at least this wide in pixels.</summary>
    public const double NameScale = 96;

    public const int BlockCells = 4;
    public const int MaxResults = 20;
    public const int MaxNewBlocksPerUpdate = 8;

    /// <summary>A block the grid has not answered at all is asked for again after this long.</summary>
    public static readonly TimeSpan BlockRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>How long a name search waits for any matching answer before it says there was none. The request is the official viewer's
    /// name request (indra/newview/llworldmapmessage.cpp, sendNamedRegionRequest) and each reply is used as it arrives, so this is only the
    /// limit for a grid that never answers, not a wait before every answer.</summary>
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);

    private readonly MapLinks _links;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<(int X, int Y), MapRegion> _regions = new();
    private readonly Dictionary<(int X, int Y), (int X, int Y)> _cells = new();
    private readonly HashSet<(int X, int Y)> _answered = new();
    private readonly Dictionary<(int X, int Y), DateTimeOffset> _blocksAsked = new();
    private readonly List<MapRegion> _results = new();
    private readonly HashSet<Guid> _pendingFriends = new();
    private readonly Dictionary<Guid, (ulong Handle, Vector3 Position)> _friendMarks = new();
    private List<MapTilePlacement> _tiles = new();
    private string? _query;
    private DateTimeOffset? _searchStarted;
    private double _scale;

    public WorldMapModel(MapLinks links, double scale = DefaultScale, Func<DateTimeOffset>? clock = null)
    {
        _links = links;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _scale = double.IsFinite(scale) ? Math.Clamp(scale, MinScale, MaxScale) : DefaultScale;
    }

    public double PixelsPerRegion => _scale;
    public int Level => MapMath.LevelFor(_scale);

    /// <summary>The middle of the window in global metres.</summary>
    public double CentreX { get; private set; }

    public double CentreY { get; private set; }

    public IReadOnlyCollection<MapRegion> Regions => _regions.Values;
    public IReadOnlyList<MapRegion> Results => _results.ToArray();

    /// <summary>Whether a name search is waiting for its first matching answer.</summary>
    public bool SearchPending => _searchStarted is not null;

    /// <summary>The time from the last name search to its first matching answer (or to the grid's "no such region"); null before one came.</summary>
    public TimeSpan? LastSearchTime { get; private set; }
    public MapSpot? Selected { get; private set; }

    /// <summary>A string-table key (and its argument) saying why the last action was refused; null after one went through.</summary>
    public (string Key, string? Arg)? Status { get; private set; }

    /// <summary>The tiles the window needs as of the last <see cref="Update"/>, the one nearest the middle first.</summary>
    public IReadOnlyList<MapTilePlacement> Tiles => _tiles;

    /// <summary>How many blocks of regions have been asked for.</summary>
    public int BlockRequests { get; private set; }

    public event Action? Changed;

    /// <summary>Raised when a teleport is asked for with no place chosen (the official map plays its invalid-operation sound then).</summary>
    public event Action? Refused;

    private double PixelsPerMetre => _scale / MapMath.RegionMetres;

    // ---------------- the window ----------------

    public void SetScale(double pixelsPerRegion)
    {
        if (!double.IsFinite(pixelsPerRegion))
            return;
        double next = Math.Clamp(pixelsPerRegion, MinScale, MaxScale);
        if (next == _scale)
            return;
        _scale = next;
        Changed?.Invoke();
    }

    public void CentreOnMetres(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return;
        CentreX = x;
        CentreY = y;
        _placed = true;
        Changed?.Invoke();
    }

    /// <summary>Whether the window has been put somewhere (by the person, a choice or the agent's own place); until then it opens on the agent.</summary>
    private bool _placed;

    public void CentreOnOwn()
    {
        if (_links.Own() is { } own)
        {
            var (gx, gy) = MapMath.CellOf(own.Handle);
            CentreOnMetres(gx * (double)MapMath.RegionMetres + own.Position.X, gy * (double)MapMath.RegionMetres + own.Position.Y);
        }
    }

    /// <summary>Drags the map: the content follows the pointer, so a drag right and down moves the middle west and north.</summary>
    public void Pan(float dx, float dy)
    {
        if (!float.IsFinite(dx) || !float.IsFinite(dy))
            return;
        CentreX -= dx / PixelsPerMetre;
        CentreY += dy / PixelsPerMetre;
        _placed = true;
        Changed?.Invoke();
    }

    /// <summary>Scales the zoom by <paramref name="factor"/> keeping the point under the pivot (window pixels) where it is.</summary>
    public void ZoomBy(double factor, float pivotX, float pivotY, float width, float height)
    {
        if (!(factor > 0) || !double.IsFinite(factor) || !float.IsFinite(pivotX) || !float.IsFinite(pivotY))
            return;
        var under = ToWorld(pivotX, pivotY, width, height);
        double next = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (next == _scale)
            return;
        _scale = next;
        CentreX = under.X - (pivotX - width / 2.0) / PixelsPerMetre;
        CentreY = under.Y + (pivotY - height / 2.0) / PixelsPerMetre;
        _placed = true;
        Changed?.Invoke();
    }

    public (double X, double Y) ToWorld(float px, float py, float width, float height) =>
        (CentreX + (px - width / 2.0) / PixelsPerMetre, CentreY - (py - height / 2.0) / PixelsPerMetre);

    public (float X, float Y) ToScreen(double x, double y, float width, float height) =>
        ((float)(width / 2.0 + (x - CentreX) * PixelsPerMetre), (float)(height / 2.0 - (y - CentreY) * PixelsPerMetre));

    private static bool Usable(float width, float height) => float.IsFinite(width) && float.IsFinite(height) && width > 0 && height > 0;

    // ---------------- tiles and blocks ----------------

    /// <summary>Works out the tiles the window needs, asks for each, and asks the grid for the blocks of regions it has not yet heard of.</summary>
    public void Update(float width, float height)
    {
        ExpireSearch();
        if (!_placed && _links.Own() is not null)
            CentreOnOwn();   // a map nobody has moved opens on the agent's own region and position, not on the grid's empty corner
        if (!Usable(width, height))
        {
            _tiles = new();
            return;
        }
        var nw = ToWorld(0, 0, width, height);
        var se = ToWorld(width, height, width, height);
        int gx0 = Cell(nw.X), gx1 = Cell(se.X), gy0 = Cell(se.Y), gy1 = Cell(nw.Y);
        int level = Level;
        int span = MapMath.RegionsPerTile(level);
        var tiles = new List<MapTilePlacement>();
        for (int ty = FloorTo(gy0, span); ty <= gy1; ty += span)
            for (int tx = FloorTo(gx0, span); tx <= gx1; tx += span)
            {
                var top = ToScreen(tx * (double)MapMath.RegionMetres, (ty + span) * (double)MapMath.RegionMetres, width, height);
                tiles.Add(new MapTilePlacement(new MapTileKey(level, tx, ty), top.X, top.Y, (float)(span * _scale)));
            }
        double mx = CentreX / MapMath.RegionMetres, my = CentreY / MapMath.RegionMetres;
        tiles.Sort((a, b) => Dist(a, span, mx, my).CompareTo(Dist(b, span, mx, my)));
        _tiles = tiles;
        foreach (var t in tiles)
            _links.RequestTile(t.Key);
        if (level <= DetailLevel)
            AskForBlocks(gx0, gy0, gx1, gy1);
    }

    private static double Dist(MapTilePlacement t, int span, double mx, double my)
    {
        double dx = t.Key.X + span / 2.0 - mx, dy = t.Key.Y + span / 2.0 - my;
        return dx * dx + dy * dy;
    }

    private static int Cell(double metres) => (int)Math.Floor(Math.Clamp(metres / MapMath.RegionMetres, -1, 70000));

    private static int FloorTo(int value, int span) => value - (((value % span) + span) % span);

    private void AskForBlocks(int gx0, int gy0, int gx1, int gy1)
    {
        var now = _clock();
        int fresh = 0;
        for (int by = Math.Max(0, gy0) / BlockCells; by <= Math.Min(ushort.MaxValue, gy1) / BlockCells; by++)
            for (int bx = Math.Max(0, gx0) / BlockCells; bx <= Math.Min(ushort.MaxValue, gx1) / BlockCells; bx++)
            {
                var block = (bx, by);
                if (_blocksAsked.TryGetValue(block, out var asked) && (BlockAnswered(bx, by) || now - asked < BlockRetryAfter))
                    continue;
                if (fresh++ >= MaxNewBlocksPerUpdate)
                    return;
                _blocksAsked[block] = now;
                BlockRequests++;
                _links.RequestBlocks(bx * BlockCells, by * BlockCells, bx * BlockCells + BlockCells - 1, by * BlockCells + BlockCells - 1);
            }
    }

    private bool BlockAnswered(int bx, int by)
    {
        for (int y = 0; y < BlockCells; y++)
            for (int x = 0; x < BlockCells; x++)
                if (_answered.Contains((bx * BlockCells + x, by * BlockCells + y)))
                    return true;
        return false;
    }

    // ---------------- regions ----------------

    public MapRegion? RegionAt(int gridX, int gridY) =>
        _cells.TryGetValue((gridX, gridY), out var origin) && _regions.TryGetValue(origin, out var region) ? region : null;

    /// <summary>The names to draw: regions whose middle is in the window, and only once a region is wide enough to hold one.</summary>
    public IReadOnlyList<(MapRegion Region, float X, float Y)> Labels(float width, float height)
    {
        var labels = new List<(MapRegion, float, float)>();
        if (_scale < NameScale || !Usable(width, height))
            return labels;
        foreach (var r in _regions.Values)
        {
            var (x, y) = ToScreen(r.GridX * (double)MapMath.RegionMetres + r.SizeX / 2.0, r.GridY * (double)MapMath.RegionMetres + r.SizeY / 2.0, width, height);
            if (x >= 0 && x <= width && y >= 0 && y <= height)
                labels.Add((r, x, y));
        }
        return labels;
    }

    public IReadOnlyList<MapMarker> Markers(float width, float height)
    {
        var markers = new List<MapMarker>();
        if (_links.Own() is { } own)
        {
            var (gx, gy) = MapMath.CellOf(own.Handle);
            var (x, y) = ToScreen(gx * (double)MapMath.RegionMetres + own.Position.X, gy * (double)MapMath.RegionMetres + own.Position.Y, width, height);
            markers.Add(new MapMarker(MapMarkerKind.Own, x, y));
        }
        foreach (var (friend, mark) in _friendMarks)
        {
            var (gx, gy) = MapMath.CellOf(mark.Handle);
            var (x, y) = ToScreen(gx * (double)MapMath.RegionMetres + mark.Position.X, gy * (double)MapMath.RegionMetres + mark.Position.Y, width, height);
            markers.Add(new MapMarker(MapMarkerKind.Friend, x, y, friend));
        }
        // a chosen spot that is a friend's place is drawn once, as the friend
        if (Selected is { } s && !_friendMarks.Values.Any(m => m.Handle == s.Handle && m.Position == s.Local))
        {
            var (gx, gy) = MapMath.CellOf(s.Handle);
            var (x, y) = ToScreen(gx * (double)MapMath.RegionMetres + s.Local.X, gy * (double)MapMath.RegionMetres + s.Local.Y, width, height);
            markers.Add(new MapMarker(MapMarkerKind.Selected, x, y));
        }
        return markers;
    }

    // ---------------- search ----------------

    /// <summary>Asks the grid for the regions with this name. The replies that start with what was typed become the results.</summary>
    public bool Search(string name)
    {
        string query = (name ?? "").Trim();
        if (query.Length == 0)
        {
            Status = ("map.search.empty", null);
            Changed?.Invoke();
            return false;
        }
        if (Viewer.Core.Links.TravelAddressParser.Parse(query) is { IsHypergrid: true } hg)
        {
            // another grid's region is found by that grid's map, through the region's lookup, and the teleport follows at once (as
            // Firestorm's map search does for a Hypergrid address): no result list, the teleport's own progress and failure show instead
            _query = null;
            _results.Clear();
            Status = ("map.search.hypergrid", hg.Display);
            Changed?.Invoke();
            return _links.TeleportAddress(hg);
        }
        _query = query;
        _results.Clear();
        Status = null;
        LastSearchTime = null;
        _searchStarted = _clock();
        _links.RequestName(query);
        Changed?.Invoke();
        return true;
    }

    // ---------------- the grid ----------------

    private string? _grid;

    /// <summary>The grid the map shows ("host:port"); null until known.</summary>
    public string? Grid => _grid;

    /// <summary>The agent is on another grid (a Hypergrid arrival, or home again): that grid has its own map, so everything learned
    /// from the old one (regions, answers, results, the chosen spot, friends found) is dropped. The same grid changes nothing.</summary>
    public void ShowGrid(string? grid)
    {
        if (string.IsNullOrEmpty(grid) || string.Equals(grid, _grid, StringComparison.OrdinalIgnoreCase))
            return;
        bool had = _grid is not null;
        _grid = grid;
        if (!had)
            return;
        _regions.Clear();
        _cells.Clear();
        _answered.Clear();
        _blocksAsked.Clear();
        _results.Clear();
        _friendMarks.Clear();
        _pendingFriends.Clear();
        _destination = null;
        _query = null;
        _searchStarted = null;
        Selected = null;
        Status = null;
        _tiles = new();
        Changed?.Invoke();
    }

    // ---------------- the chosen spot ----------------

    /// <summary>Shows a place a script named: asks for the region, and when it is known chooses the spot in it and centres the map there. Nothing is sent but the lookup.</summary>
    public bool ShowDestination(string regionName, Vector3 position)
    {
        string name = (regionName ?? "").Trim();
        if (name.Length == 0)
            return false;
        _destination = null;
        var known = _regions.Values.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            ChooseSpot(known, position);
            return true;
        }
        _destination = (name, position);
        return Search(name);
    }

    private void ChooseSpot(MapRegion region, Vector3 local)
    {
        Selected = new MapSpot(region.Handle, region.Name, local);
        Status = null;
        CentreOnMetres(region.GridX * (double)MapMath.RegionMetres + local.X, region.GridY * (double)MapMath.RegionMetres + local.Y);
        Changed?.Invoke();
    }

    private (string Name, Vector3 Position)? _destination;

    /// <summary>A place asked for whose region the map is still looking up; null when none waits.</summary>
    public (string Name, Vector3 Position)? PendingDestination => _destination;

    /// <summary>Chooses the middle of a region (a search result) and centres the map on it. Nothing is sent.</summary>
    public bool Choose(MapRegion region)
    {
        var local = new Vector3(region.SizeX / 2f, region.SizeY / 2f, 0);
        Selected = new MapSpot(region.Handle, region.Name, local);
        Status = null;
        CentreOnMetres(region.GridX * (double)MapMath.RegionMetres + local.X, region.GridY * (double)MapMath.RegionMetres + local.Y);
        return true;
    }

    /// <summary>Chooses the spot under a click. Only a spot in a known region can be chosen. Nothing is sent.</summary>
    public bool Click(float px, float py, float width, float height)
    {
        if (!float.IsFinite(px) || !float.IsFinite(py) || !Usable(width, height))
            return false;
        var (x, y) = ToWorld(px, py, width, height);
        int gx = (int)Math.Floor(x / MapMath.RegionMetres), gy = (int)Math.Floor(y / MapMath.RegionMetres);
        if (RegionAt(gx, gy) is not { } region)
        {
            Status = ("map.no_region", null);
            Changed?.Invoke();
            return false;
        }
        // a click asks for the height the agent is at, as the official viewer's map does (llworldmapview.cpp viewPosToGlobal: "Want agent's
        // height, not camera's"); the middle of a searched region stays at 0, as it does there. The region lifts any spot below the ground.
        float z = _links.Own() is { } own && float.IsFinite(own.Position.Z) ? own.Position.Z : 0f;
        var local = new Vector3((float)(x - region.GridX * (double)MapMath.RegionMetres), (float)(y - region.GridY * (double)MapMath.RegionMetres), z);
        Selected = new MapSpot(region.Handle, region.Name, local);
        Status = null;
        Changed?.Invoke();
        return true;
    }

    public bool TeleportToSelected()
    {
        if (Selected is not { } spot)
        {
            Status = ("map.nothing_selected", null);
            Changed?.Invoke();
            Refused?.Invoke();
            return false;
        }
        Status = null;
        return _links.Teleport(spot.Handle, spot.Local);
    }

    public bool TeleportHome()
    {
        Status = null;
        return _links.Home();
    }

    // ---------------- friends ----------------

    /// <summary>Asks where a friend is. Only a friend who has given the map right is asked for.</summary>
    public bool ShowFriend(Guid friend)
    {
        if (friend == Guid.Empty)
            return false;
        if (!_links.FriendGrantsMap(friend))
        {
            Status = ("map.friend_not_shared", null);
            Changed?.Invoke();
            return false;
        }
        Status = null;
        _pendingFriends.Add(friend);
        _links.RequestFriend(friend);
        return true;
    }

    // ---------------- the grid's answers ----------------

    public void Receive(TransportEvent e)
    {
        switch (e)
        {
            case MapBlockSize m:
                Learn(m);
                break;
            case FriendLocated f when _pendingFriends.Remove(f.Friend):
                var (gx, gy) = MapMath.CellOf(f.RegionHandle);
                _friendMarks[f.Friend] = (f.RegionHandle, f.Position);
                Selected = new MapSpot(f.RegionHandle, RegionAt(gx, gy)?.Name ?? "", f.Position);
                CentreOnMetres(gx * (double)MapMath.RegionMetres + f.Position.X, gy * (double)MapMath.RegionMetres + f.Position.Y);
                break;
        }
    }

    /// <summary>Ends a search that has had no matching answer for <see cref="SearchTimeout"/>: says so, and drops a destination a script named.</summary>
    private void ExpireSearch()
    {
        if (_searchStarted is not { } started || _clock() - started < SearchTimeout)
            return;
        _searchStarted = null;
        _destination = null;
        Status = ("map.search.no_answer", _query);
        Changed?.Invoke();
    }

    /// <summary>A reply that matches the search just made: stops the clock. The grid names a cell with no region by echoing the searched name with
    /// the no-region access level; that ends the wait at once, with a message instead of a result.</summary>
    private void SearchAnswered(MapBlockSize m)
    {
        if (_searchStarted is not { } started || _query is not { } q || string.IsNullOrEmpty(m.Name) || !m.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            return;
        LastSearchTime = _clock() - started;
        _searchStarted = null;
        if (m.Access == 255 && string.Equals(m.Name, q, StringComparison.OrdinalIgnoreCase))
        {
            _destination = null;
            Status = ("map.search.none", q);
        }
    }

    private void Learn(MapBlockSize m)
    {
        if (m.Grid is { } from && _grid is not null && !string.Equals(from, _grid, StringComparison.OrdinalIgnoreCase))
            return;   // a late answer from the grid the agent left: about another map
        if (m.Name is { } linked && linked.Contains(':', StringComparison.Ordinal))
            return;   // a Hypergrid link the grid made for a lookup ("host:port:Region" at a made-up spot): not a region of this map
        _answered.Add((m.GridX, m.GridY));
        SearchAnswered(m);
        if (string.IsNullOrEmpty(m.Name) || m.Access == 255)
        {
            if (Status is { Key: "map.search.none" })
                Changed?.Invoke();
            return;
        }
        int sizeX = m.SizeX > 0 ? m.SizeX : MapMath.RegionMetres, sizeY = m.SizeY > 0 ? m.SizeY : MapMath.RegionMetres;
        var region = new MapRegion(m.Name, m.GridX, m.GridY, sizeX, sizeY, m.MapImageId, m.Access);
        var origin = (m.GridX, m.GridY);
        foreach (var cell in _cells.Where(c => c.Value == origin).Select(c => c.Key).ToList())
            _cells.Remove(cell);
        _regions[origin] = region;
        if (_destination is { } wanted && string.Equals(wanted.Name, m.Name, StringComparison.OrdinalIgnoreCase))
        {
            _destination = null;
            ChooseSpot(region, wanted.Position);   // the place a script named, now that the grid has said where it is
        }
        for (int y = 0; y < Math.Min(16, (sizeY + 255) / 256); y++)
            for (int x = 0; x < Math.Min(16, (sizeX + 255) / 256); x++)
                _cells[(m.GridX + x, m.GridY + y)] = origin;
        if (_query is { } q && m.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            _results.RemoveAll(r => r.GridX == region.GridX && r.GridY == region.GridY);
            if (_results.Count < MaxResults)
            {
                _results.Add(region);
                _results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
        }
        Changed?.Invoke();
    }
}
