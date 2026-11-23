// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The pictures of the Reverie check box: an unticked box is a rounded outline in a colour that holds 3:1 against the surface behind it
/// (<see cref="CheckColours"/>), a ticked box the accent filled with a tick in the text-on-accent colour, and a disabled box the same
/// at 45 %, an unticked one a dashed outline (so it differs by shape, not only by dimness). Drawn here by distance to the shape, so the edges are smooth at any look's colours.
/// </summary>
public static class CheckBoxIcons
{
    /// <summary>The picture's size in interface units.</summary>
    public const int Size = 18;

    private const float Radius = 4.5f, Stroke = 1.6f;

    /// <summary>One picture of the box.</summary>
    public static ImageTexture Make(ReverieTokens r, bool ticked, bool disabled)
    {
        var outline = CheckColours.Outline(r);
        var fill = ticked ? r.Accent : outline;
        var tick = r["text.on-accent"];
        // a disabled, unticked box is a dashed outline at its own strength (CheckColours.Disabled); a ticked one is the box at 45 %
        var off = CheckColours.Disabled(r);
        bool dashed = disabled && !ticked;
        float fade = !disabled ? 1f : ticked ? ControlStates.DisabledStrength : off.Strength;
        var image = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        const int samples = 4;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float body = 0, line = 0, mark = 0;
                for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                    {
                        float px = x + (sx + 0.5f) / samples, py = y + (sy + 0.5f) / samples;
                        float d = BoxDistance(px, py);
                        if (d <= 0)
                            body++;
                        if (d <= 0 && d >= -Stroke && (!dashed || DashOn(px, py)))
                            line++;
                        if (ticked && TickDistance(px, py) <= 1.0f)
                            mark++;
                    }
                float n = samples * samples;
                // layers, bottom to top: the filled body (ticked only), the outline, the tick
                float a = 0, rr = 0, gg = 0, bb = 0;
                void Over(UiColour c, float cover)
                {
                    float ca = cover;
                    float oa = ca + a * (1 - ca);
                    if (oa <= 0) return;
                    rr = (c.R * ca + rr * a * (1 - ca)) / oa;
                    gg = (c.G * ca + gg * a * (1 - ca)) / oa;
                    bb = (c.B * ca + bb * a * (1 - ca)) / oa;
                    a = oa;
                }
                if (ticked)
                    Over(fill, body / n);
                Over(ticked ? r.Accent : outline, line / n);
                if (ticked)
                    Over(tick, mark / n);
                image.SetPixel(x, y, new Color(rr, gg, bb, a * fade));
            }
        return ImageTexture.CreateFromImage(image);
    }

    // the dashed outline of a disabled box: sixteen arcs around the centre, every other one drawn
    private static bool DashOn(float x, float y) =>
        (int)MathF.Floor((MathF.Atan2(y - Size / 2f, x - Size / 2f) + MathF.PI) / MathF.Tau * 16f) % 2 == 0;

    // signed distance to the rounded square that fills the picture (negative inside), in picture units
    private static float BoxDistance(float x, float y)
    {
        float half = Size / 2f - 0.5f;
        float qx = MathF.Abs(x - Size / 2f) - (half - Radius), qy = MathF.Abs(y - Size / 2f) - (half - Radius);
        float outside = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0));
        return outside + MathF.Min(MathF.Max(qx, qy), 0) - Radius;
    }

    // distance to the tick's two strokes
    private static float TickDistance(float x, float y) =>
        MathF.Min(Segment(x, y, 4.6f, 9.4f, 7.7f, 12.5f), Segment(x, y, 7.7f, 12.5f, 13.6f, 5.9f));

    private static float Segment(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0f, 1f);
        float cx = ax + t * dx - px, cy = ay + t * dy - py;
        return MathF.Sqrt(cx * cx + cy * cy);
    }
}
