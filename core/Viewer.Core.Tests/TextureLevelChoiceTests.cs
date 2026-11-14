// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Imaging;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The level a texture wants for its size on screen, against the official viewer's choice, written here from its
/// behaviour (llviewertexturelist.cpp updateImageDecodePriority, llface.cpp calcPixelArea and calcImportanceToCamera,
/// llviewertexture.cpp the desired discard level, llviewercamera.cpp the importance cone):
/// the face's pixel area is pi x (atan(r / d) x viewport height / vertical field of view)^2, with d the distance to
/// the face less its radius (at least 0.001); it is divided by the face's repeats squared (the smaller of the two
/// directions, clamped to 0.0095..25); a face in the view cone (half angle: half the vertical field of view times the
/// aspect, at least 1) nearer than 96.1 m is boosted by 8 x its importance (distance weight 1 / 0.5 / 0.2 / 0.05 up to
/// 16.1 / 32.1 / 48.1 / 96.1 m, angle weight 1 / 0.8 / 0.64 / 0.36 from 10 / 20 / 30 degrees off the view's centre;
/// importance 1 when the camera is inside the face's radius), never below 1 x; the wanted level is
/// floor(log4(texels / area)), at least 1 for a texture over 2048 texels across, at most the coarsest level; an area of
/// 10 px or less wants the coarsest. The texture budget does not change what a texture wants.
/// </summary>
public class TextureLevelChoiceTests
{
    private const int Height = 1080;
    private const float FovY = MathF.PI / 3, Aspect = 16f / 9;
    private static readonly uint Avg = PlaceholderColour.Of(90, 100, 110, 255);

    private static ResidencyCamera Cam() => new(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, FovY, Aspect, 0.1f, 1024f, Height);

    private static int Official(int w, int h, int coarsest, Bounds b, float repeats)
    {
        double dist = b.Centre.Length();
        double d = Math.Max(dist - b.Radius, 0.001);
        double px = Math.Atan(b.Radius / d) * Height / FovY;
        double area = px * px * 3.14159;
        area /= Math.Clamp((double)repeats * repeats, 0.0095, 25);
        double cos = Vector3.Dot(b.Centre / (float)dist, Vector3.UnitX);
        double importance = 0;
        if (d < b.Radius) importance = 1;
        else if (cos > Math.Cos(0.5 * FovY * Math.Max(1, Aspect)) && d < 96.1)
        {
            double dw = d <= 16.1 ? 1 : d <= 32.1 ? 0.5 : d <= 48.1 ? 0.2 : 0.05;
            double aw = cos >= 0.985 ? 1 : cos >= 0.94 ? 0.8 : cos >= 0.866 ? 0.64 : 0.36;
            importance = dw * aw;
        }
        area *= Math.Max(importance * 8, 1);
        if (area <= 10) return coarsest;
        double level = Math.Floor(Math.Log((double)w * h / area) / Math.Log(4));
        int min = w > 2048 || h > 2048 ? 1 : 0;
        return (int)Math.Clamp(level, min, coarsest);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (w, h) in new[] { (1024, 1024), (512, 512), (512, 128), (2048, 2048), (4096, 4096) })
            foreach (float repeats in new[] { 1f, 0.25f, 4f, 0.05f })
                yield return [w, h, repeats];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_wanted_level_equals_the_official_choice_for_its_size_on_screen(int w, int h, float repeats)
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 40, UsersPerFrame = 100_000 });
        int levels = TextureLevels.Count(w, h, 6), coarsest = levels - 1;
        var cases = new List<(Guid Id, Bounds B)>();
        int n = 0;
        foreach (float radius in new[] { 0.5f, 2f, 7f, 15f })
            foreach (float dist in new[] { 3f, 8f, 16f, 24f, 32f, 48f, 64f, 96f, 128f, 200f, 400f })
                foreach (float degrees in new[] { 0f, 8f, 15f, 25f, 40f })
                {
                    float a = degrees * MathF.PI / 180f;
                    var b = new Bounds(new Vector3(dist * MathF.Cos(a), dist * MathF.Sin(a), 0), radius);
                    var id = new Guid(++n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
                    r.SetUser(new TextureUser(TextureUserKind.Face, (uint)n, 0), id, b, repeats);
                    cases.Add((id, b));
                }
        r.Update(Cam(), TimeSpan.Zero);
        foreach (var (id, _) in cases)
            r.Uploaded(id, coarsest, levels, w, h, TextureFootprint.Bytes(TextureLevels.Size(w, coarsest), TextureLevels.Size(h, coarsest), true), Avg, TimeSpan.Zero);
        r.Update(Cam(), TimeSpan.FromSeconds(1));
        var wrong = cases.Select(c => (c.B, Ours: r.DesiredLevelOf(c.Id), Official: Official(w, h, coarsest, c.B, repeats)))
            .Where(x => x.Ours != x.Official).Take(8)
            .Select(x => $"radius {x.B.Radius} at ({x.B.Centre.X:0.#}, {x.B.Centre.Y:0.#}): ours {x.Ours}, official {x.Official}").ToList();
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void A_face_out_of_view_still_wants_its_coarsest_level()
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 40 });
        var id = new Guid(1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
        r.SetUser(new TextureUser(TextureUserKind.Face, 1, 0), id, new Bounds(new Vector3(-20, 0, 0), 3f), 1f);
        r.Update(Cam(), TimeSpan.Zero);
        int levels = TextureLevels.Count(1024, 1024, 6);
        r.Uploaded(id, levels - 1, levels, 1024, 1024, 1024, Avg, TimeSpan.Zero);
        Assert.Equal(levels - 1, r.DesiredLevelOf(id));
    }
}
