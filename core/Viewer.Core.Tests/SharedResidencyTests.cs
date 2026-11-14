// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>One texture manager for several regions: users are told apart by their space, a texture two regions use is one entry that is
/// loaded, uploaded and held once, and a region leaving frees only what no other region uses.</summary>
public class SharedResidencyTests
{
    private const long MiB = 1024 * 1024;
    private static readonly Bounds Near = new(new(10, 0, 0), 2f);

    private static ResidencyCamera Cam() =>
        new(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);

    private static TextureUser Face(uint id, int space, int face = 0) => new(TextureUserKind.Face, id, face, space);
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private static TextureResidency Manager() => new(new() { BudgetBytes = 256 * MiB });

    private static void Load(TextureResidency r, long frame, long size)
    {
        foreach (var a in r.Update(Cam(), TestClock.Frames(frame)).Where(a => a.Kind == ResidencyActionKind.Load))
            r.Uploaded(a.Texture, size, PlaceholderColour.Of(100, 110, 120, 255), TestClock.Frames(frame));
    }

    [Fact]
    public void Equal_local_ids_in_two_spaces_are_two_users_of_one_texture()
    {
        var r = Manager();
        r.SetUser(Face(7, space: 1), G(1), Near);
        r.SetUser(Face(7, space: 2), G(1), Near);
        Assert.Equal(2, r.Users);
        Assert.Equal(1, r.Textures);
        r.RemoveUser(Face(7, space: 1));
        Assert.Equal(1, r.Users);
        Assert.Equal(1, r.Textures);
    }

    [Fact]
    public void A_texture_two_spaces_use_is_asked_for_once_and_held_once()
    {
        var r = Manager();
        r.SetUser(Face(7, space: 1), G(1), Near);
        r.SetUser(Face(7, space: 2), G(1), Near);
        var first = r.Update(Cam(), TestClock.Frames(0));
        Assert.Single(first.Where(a => a.Kind == ResidencyActionKind.Load && a.Texture == G(1)));
        r.Uploaded(G(1), 4 * MiB, PlaceholderColour.Of(1, 2, 3, 255), TestClock.Frames(0));
        var later = r.Update(Cam(), TestClock.Frames(30));
        Assert.DoesNotContain(later, a => a.Kind == ResidencyActionKind.Load);
        Assert.Equal(1, r.Resident);
        Assert.Equal(4 * MiB, r.ResidentBytes);
    }

    [Fact]
    public void Dropping_a_space_evicts_only_what_nothing_else_uses()
    {
        var r = Manager();
        r.SetUser(Face(7, space: 1), G(1), Near);   // used by both regions
        r.SetUser(Face(8, space: 1), G(2), Near);   // only region 1
        r.SetUser(Face(7, space: 2), G(1), Near);
        Load(r, 0, 4 * MiB);
        Assert.Equal(8 * MiB, r.ResidentBytes);

        var acts = r.DropSpace(1, TestClock.Frames(10));

        var evicted = acts.Where(a => a.Kind == ResidencyActionKind.Evict).Select(a => a.Texture).ToList();
        Assert.Equal(new[] { G(2) }, evicted);
        Assert.Equal(ResidencyState.Resident, r.StateOf(G(1)));
        Assert.Equal(4 * MiB, r.ResidentBytes);
        Assert.Equal(1, r.Users);
        Assert.Equal(1, r.Textures);
    }

    [Fact]
    public void Dropping_the_last_space_that_uses_a_texture_frees_it()
    {
        var r = Manager();
        r.SetUser(Face(7, space: 1), G(1), Near);
        r.SetUser(Face(7, space: 2), G(1), Near);
        Load(r, 0, 4 * MiB);
        Assert.Empty(r.DropSpace(1, TestClock.Frames(10)).Where(a => a.Kind == ResidencyActionKind.Evict));
        var acts = r.DropSpace(2, TestClock.Frames(20));
        Assert.Contains(acts, a => a.Kind == ResidencyActionKind.Evict && a.Texture == G(1));
        Assert.Equal(0, r.ResidentBytes);
        Assert.Equal(0, r.Textures);
        Assert.Equal(0, r.Users);
    }

    [Fact]
    public void Dropping_a_space_with_no_users_changes_nothing()
    {
        var r = Manager();
        r.SetUser(Face(7, space: 1), G(1), Near);
        Load(r, 0, 4 * MiB);
        Assert.Empty(r.DropSpace(5, TestClock.Frames(1)));
        Assert.Equal(4 * MiB, r.ResidentBytes);
        Assert.Equal(1, r.Users);
    }
}
