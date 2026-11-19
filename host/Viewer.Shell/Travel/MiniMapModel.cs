// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;

namespace Viewer.Shell.Travel;

// Behaviour follows the official viewer's mini-map (its zoom steps, its coarse-location dots, friends told apart, the turn
// with the camera and the north marker), written from a behaviour spec in our own words (no code, text or layout copied).

/// <summary>One avatar dot of the mini-map, in window pixels (y down).</summary>
public sealed record MiniMapDot(Guid Id, float X, float Y, bool Friend, bool UnknownHeight, float RelativeHeight);

/// <summary>What the mini-map is drawn from, for one frame. <paramref name="CameraHeading"/> is the camera's direction in radians
/// counter-clockwise from east (north is a quarter turn); <paramref name="CameraFov"/> its horizontal field of view in radians.</summary>
public sealed record MiniMapInput(Guid Self, Vector3 SelfPosition, float CameraHeading, float CameraFov, int RegionSizeX, int RegionSizeY,
    IReadOnlyList<CoarseAvatar> Avatars, Func<Guid, bool> IsFriend);

/// <summary>One frame of the mini-map: where everything goes in window pixels (y down). Angles are counter-clockwise from the right
/// of the window.</summary>
public sealed record MiniMapFrame(float CentreX, float CentreY, IReadOnlyList<(float X, float Y)> RegionCorners, IReadOnlyList<MiniMapDot> Dots,
    float NorthX, float NorthY, float NorthAngle, float FrustumCentre, float FrustumHalf, double PixelsPerRegion, bool Rotated,
    IReadOnlyList<MiniMapCompassPoint>? CompassPoints = null)
{
    /// <summary>The eight compass labels (N, NE, E, SE, S, SW, W, NW), turned with the map.</summary>
    public IReadOnlyList<MiniMapCompassPoint> Compass => CompassPoints ?? [];
}

/// <summary>One compass label at the edge of the mini-map: its text (N, NE, E ...), where it sits in window pixels (y down) and its angle
/// counter-clockwise from the right of the window.</summary>
public sealed record MiniMapCompassPoint(string Label, float X, float Y, float Angle);

/// <summary>The mini-map as data: the area around the agent, other avatars as dots, north and the turn with the camera. No engine types.</summary>
public sealed class MiniMapModel
{
    public const double DefaultScale = 128, MinScale = 32, MaxScale = 4096, Far = 32, Close = 256, VeryClose = 1024, ZoomPerClick = 1.04;

    /// <summary>A reported height at or above this is "unknown" (the region's coarse locations cannot say more).</summary>
    public const float UnknownHeight = 1020f;

    /// <summary>The north marker sits this many pixels inside the nearer edge of the window.</summary>
    public const float NorthMargin = 8f;

    /// <summary>The compass labels, clockwise from north.</summary>
    public static readonly string[] CompassLabels = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private double _scale = DefaultScale;
    private bool _rotate = true;

    public double PixelsPerRegion => _scale;

    /// <summary>Whether the map turns with the camera so that the camera's direction is up (the official default); off: north is up.</summary>
    public bool Rotate
    {
        get => _rotate;
        set
        {
            if (_rotate == value)
                return;
            _rotate = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;

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

    /// <summary>Zooms by mouse-wheel clicks: each click scales by four percent, in or out.</summary>
    public void ZoomClicks(double clicks)
    {
        if (!double.IsFinite(clicks))
            return;
        SetScale(_scale * Math.Pow(ZoomPerClick, clicks));
    }

    /// <summary>The frame for a window of <paramref name="width"/> x <paramref name="height"/> pixels, centred on the agent.</summary>
    public MiniMapFrame Build(MiniMapInput input, float width, float height)
    {
        bool rotated = _rotate;
        var empty = new MiniMapFrame(0, 0, [], [], 0, 0, 0, 0, 0, _scale, rotated);
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            return empty;
        float heading = float.IsFinite(input.CameraHeading) ? input.CameraHeading : MathF.PI / 2;
        double ppm = _scale / 256.0;
        float cx = width / 2f, cy = height / 2f;
        // a world offset (metres east, metres north) from the agent as window pixels: turned so the camera's direction is up, or north up
        float turn = rotated ? MathF.PI / 2 - heading : 0;
        (float X, float Y) Place(double east, double north)
        {
            double a = Math.Atan2(north, east) + turn;
            double r = Math.Sqrt(east * east + north * north) * ppm;
            return ((float)(cx + r * Math.Cos(a)), (float)(cy - r * Math.Sin(a)));
        }

        var self = input.SelfPosition;
        if (!float.IsFinite(self.X) || !float.IsFinite(self.Y))
            return empty;
        int sx = input.RegionSizeX > 0 ? input.RegionSizeX : 256, sy = input.RegionSizeY > 0 ? input.RegionSizeY : 256;
        var corners = new List<(float X, float Y)>
        {
            Place(-self.X, -self.Y), Place(sx - self.X, -self.Y), Place(sx - self.X, sy - self.Y), Place(-self.X, sy - self.Y),
        };

        var dots = new List<MiniMapDot>();
        foreach (var a in input.Avatars)
        {
            if (a.Id == input.Self || !float.IsFinite(a.Position.X) || !float.IsFinite(a.Position.Y) || !float.IsFinite(a.Position.Z))
                continue;
            var (x, y) = Place(a.Position.X - self.X, a.Position.Y - self.Y);
            if (x < 0 || x > width || y < 0 || y > height)
                continue;
            dots.Add(new MiniMapDot(a.Id, x, y, input.IsFriend(a.Id), a.Position.Z >= UnknownHeight, a.Position.Z - self.Z));
        }

        // true north: a quarter turn from east, turned like everything else; the marker sits on a circle just inside the nearer edge
        float northAngle = MathF.PI / 2 + turn;
        float radius = MathF.Max(0, MathF.Min(width, height) / 2f - NorthMargin);
        float fov = float.IsFinite(input.CameraFov) && input.CameraFov > 0 ? input.CameraFov : 1f;
        var compass = new List<MiniMapCompassPoint>();
        for (int i = 0; i < CompassLabels.Length; i++)
        {
            float a = MathF.PI / 2 - i * MathF.PI / 4 + turn;   // north, then clockwise in eighths of a turn
            compass.Add(new MiniMapCompassPoint(CompassLabels[i], cx + radius * MathF.Cos(a), cy - radius * MathF.Sin(a), a));
        }
        return new MiniMapFrame(cx, cy, corners, dots, cx + radius * MathF.Cos(northAngle), cy - radius * MathF.Sin(northAngle), northAngle,
            rotated ? MathF.PI / 2 : heading, fov / 2f, _scale, rotated, compass);
    }
}
