// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Assets;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The texture residency manager.</summary>
public class ResidencyTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>At the origin looking east (+X), z up, 60 degrees, 16:9, 360 px high.</summary>
    private static ResidencyCamera Cam(Vector3? at = null, Vector3? forward = null) =>
        new(at ?? Vector3.Zero, Vector3.Normalize(forward ?? Vector3.UnitX), Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);

    private static TextureUser Face(uint id, int face = 0) => new(TextureUserKind.Face, id, face);
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    /// <summary>Applies a frame's actions as a host would: every load uploads at once with its known size.</summary>
    private static List<ResidencyAction> Step(TextureResidency r, ResidencyCamera cam, long frame, Func<Guid, long> size)
    {
        var acts = r.Update(cam, TestClock.Frames(frame)).ToList();
        foreach (var a in acts.Where(a => a.Kind == ResidencyActionKind.Load))
            r.Uploaded(a.Texture, size(a.Texture), PlaceholderColour.Of(100, 110, 120, 255), TestClock.Frames(frame));
        return acts;
    }

    [Fact]
    public void Near_and_large_beats_far_and_small()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        r.SetUser(Face(1), G(1), new Bounds(new(10, 0, 0), 2f));
        r.SetUser(Face(2), G(2), new Bounds(new(100, 0, 0), 0.5f));
        r.Update(Cam(), TestClock.Frames(0));
        Assert.True(r.PriorityOf(G(1)) > r.PriorityOf(G(2)));
        Assert.True(r.IsVisible(G(1)) && r.IsVisible(G(2)));
    }

    [Fact]
    public void In_frustum_beats_out_of_frustum_even_when_the_outside_one_is_near_and_large()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        r.SetUser(Face(1), G(1), new Bounds(new(-8, 0, 0), 3f));    // behind the camera, near and large
        r.SetUser(Face(2), G(2), new Bounds(new(300, 0, 0), 0.1f)); // ahead, far and tiny
        r.Update(Cam(), TestClock.Frames(0));
        Assert.False(r.IsVisible(G(1)));
        Assert.True(r.IsVisible(G(2)));
        Assert.True(r.PriorityOf(G(2)) > r.PriorityOf(G(1)));
        Assert.True(r.PriorityOf(G(1)) < 1.0);
    }

    [Fact]
    public void A_shared_texture_takes_its_highest_users_priority()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        var near = new Bounds(new(6, 1, 0), 1.5f);
        r.SetUser(Face(1), G(7), new Bounds(new(200, 0, 0), 0.5f));
        r.SetUser(Face(2, 3), G(7), near);
        r.SetUser(Face(3), G(7), new Bounds(new(-50, 0, 0), 4f));
        r.Update(Cam(), TestClock.Frames(0));
        Assert.Equal(Cam().Priority(near), r.PriorityOf(G(7)));
        // the top user leaves: the texture falls to its next user, not to 0
        r.RemoveUser(Face(2, 3));
        r.Update(Cam(), TestClock.Frames(1));
        Assert.Equal(Cam().Priority(new Bounds(new(200, 0, 0), 0.5f)), r.PriorityOf(G(7)));
    }

    /// <summary>A seeded scene: 200 textures of random sizes on 400 faces spread over a 256 m region.</summary>
    private static (TextureResidency R, Dictionary<Guid, long> Size) Scene(long budget, int seed = 7, int minResidence = 60, double lowWater = 0.9)
    {
        var rnd = new Random(seed);
        var r = new TextureResidency(new() { BudgetBytes = budget, MinResidence = TestClock.Frames(minResidence), LowWater = lowWater, UsersPerFrame = 128 });
        var size = new Dictionary<Guid, long>();
        for (int t = 0; t < 200; t++)
        {
            int side = 32 << rnd.Next(0, 6);   // 32.. 1024
            size[G(t)] = TextureFootprint.Bytes(side, side, true);
        }
        for (uint f = 0; f < 400; f++)
            r.SetUser(Face(f), G(rnd.Next(0, 200)), new Bounds(new(rnd.NextSingle() * 256, rnd.NextSingle() * 256, rnd.NextSingle() * 40), 0.2f + rnd.NextSingle() * 6));
        return (r, size);
    }

    /// <summary>A deterministic walk through the region, turning as it goes.</summary>
    private static ResidencyCamera Walk(int frame)
    {
        float a = frame * 0.01f;
        var at = new Vector3(128 + 100 * MathF.Cos(a * 0.7f), 128 + 100 * MathF.Sin(a * 0.5f), 20);
        return Cam(at, new Vector3(MathF.Cos(a), MathF.Sin(a), -0.1f));
    }

    [Fact]
    public void The_budget_holds_after_every_eviction_pass()
    {
        var (r, size) = Scene(24 * MiB);
        long evictions = 0;
        for (int f = 0; f < 1500; f++)
        {
            var acts = r.Update(Walk(f), TestClock.Frames(f)).ToList();   // the eviction pass
            Assert.True(r.ResidentBytes + r.LoadingBytes <= r.BudgetBytes, $"frame {f}: {r.ResidentBytes} resident > budget {r.BudgetBytes}");
            foreach (var a in acts.Where(a => a.Kind == ResidencyActionKind.Load))
                r.Uploaded(a.Texture, size[a.Texture], PlaceholderColour.Of(100, 110, 120, 255), TestClock.Frames(f));
            evictions = r.Evictions;
        }
        Assert.True(evictions > 0, "the walk must force evictions");
        Assert.True(r.Reloads > 0, "and reloads");
    }

    [Fact]
    public void The_budget_holds_even_when_a_load_turns_out_larger_than_reserved()
    {
        // every size unknown until upload: loads reserve 512^2 but some are 1024^2 (4x): the next pass must restore the budget
        var (r, size) = Scene(24 * MiB, seed: 3);
        for (int f = 0; f < 600; f++)
        {
            var acts = r.Update(Walk(f), TestClock.Frames(f)).ToList();
            Assert.True(r.ResidentBytes + r.LoadingBytes <= r.BudgetBytes, $"frame {f}: after the pass {r.ResidentBytes} > {r.BudgetBytes}");
            foreach (var a in acts.Where(a => a.Kind == ResidencyActionKind.Load))
                r.Uploaded(a.Texture, size[a.Texture], PlaceholderColour.Of(1, 2, 3, 255), TestClock.Frames(f));
        }
    }

    [Fact]
    public void The_minimum_residence_time_is_respected()
    {
        long each = TextureFootprint.Bytes(256, 256, true);
        var r = new TextureResidency(new() { BudgetBytes = 2 * each, MinResidence = TestClock.Frames(60), LowWater = 1.0, UnknownSizeEstimate = each });
        r.SetUser(Face(1), G(1), new Bounds(new(50, 0, 0), 1));
        r.SetUser(Face(2), G(2), new Bounds(new(60, 0, 0), 1));
        Step(r, Cam(), 0, _ => each);
        Assert.Equal(ResidencyState.Resident, r.StateOf(G(1)));
        Assert.Equal(ResidencyState.Resident, r.StateOf(G(2)));
        // a better texture arrives at frame 10: it must wait until the lowest resident has been resident 60 frames
        r.SetUser(Face(3), G(3), new Bounds(new(5, 0, 0), 2));
        for (long f = 10; f < 60; f++)
        {
            Step(r, Cam(), f, _ => each);
            Assert.NotEqual(ResidencyState.Resident, r.StateOf(G(3)));
            Assert.Equal(0, r.Evictions);
        }
        Step(r, Cam(), 60, _ => each);
        Assert.Equal(ResidencyState.Resident, r.StateOf(G(3)));
        Assert.Equal(ResidencyState.Evicted, r.StateOf(G(2)));   // the farther one goes
        Assert.Equal(1, r.Evictions);
    }

    [Fact]
    public void When_the_visible_set_alone_exceeds_the_budget_the_minimum_residence_gives_way()
    {
        long each = TextureFootprint.Bytes(256, 256, true);
        var r = new TextureResidency(new() { BudgetBytes = 3 * each, MinResidence = TestClock.Frames(1000), UnknownSizeEstimate = each });
        for (uint i = 1; i <= 3; i++) r.SetUser(Face(i), G((int)i), new Bounds(new(10 * i, 0, 0), 1));
        Step(r, Cam(), 0, _ => each);
        Assert.Equal(3 * each, r.ResidentBytes);
        r.SetBudget(2 * each);   // everything resident is visible and it no longer fits
        Step(r, Cam(), 1, _ => each);
        Assert.True(r.ResidentBytes <= 2 * each);
        Assert.Equal(ResidencyState.Evicted, r.StateOf(G(3)));   // the farthest
    }

    /// <summary>Two poses a few metres apart, facing opposite ways, switched every 5 frames; each sees its own 12 textures
    /// and the budget holds 18: every pose change NEEDS 6 swaps to show what is on screen.</summary>
    private static (long Thrash, long Evictions, long Reloads, long Loads) Oscillate(int minResidence, double lowWater, double margin)
    {
        long each = TextureFootprint.Bytes(256, 256, true);
        var r = new TextureResidency(new() { BudgetBytes = 18 * each, MinResidence = TestClock.Frames(minResidence), LowWater = lowWater, DisplaceMargin = margin, UnknownSizeEstimate = each });
        for (uint i = 0; i < 12; i++)
        {
            r.SetUser(Face(i), G((int)i), new Bounds(new(20 + i, (int)(i % 4) - 2, 0), 1));        // east
            r.SetUser(Face(100 + i), G(100 + (int)i), new Bounds(new(-20 - i, (int)(i % 4) - 2, 0), 1));   // west
        }
        for (int f = 0; f < 600; f++)
            Step(r, (f / 5) % 2 == 0 ? Cam() : Cam(new Vector3(-1, 0, 0), -Vector3.UnitX), f, _ => each);
        return (r.Thrash, r.Evictions, r.Reloads, r.Loads);
    }

    /// <summary>The boundary case hysteresis is for: 20 similar textures in view, the budget holds 15, and the camera jitters
    /// 1.5 m sideways every 5 frames, so the ranks around the 15th place keep flipping.</summary>
    private static (long Thrash, long Evictions, long Reloads, long Loads) Jitter(int minResidence, double lowWater, double margin)
    {
        long each = TextureFootprint.Bytes(256, 256, true);
        var r = new TextureResidency(new() { BudgetBytes = 15 * each, MinResidence = TestClock.Frames(minResidence), LowWater = lowWater, DisplaceMargin = margin, UnknownSizeEstimate = each });
        for (int i = 0; i < 20; i++)
            r.SetUser(Face((uint)i), G(i), new Bounds(new(30, i - 9.5f, 0), 1));
        for (int f = 0; f < 600; f++)
            Step(r, Cam(new Vector3(0, (f / 5) % 2 == 0 ? -1.5f : 1.5f, 0)), f, _ => each);
        return (r.Thrash, r.Evictions, r.Reloads, r.Loads);
    }

    [Fact]
    public void Hysteresis_prevents_thrash_under_an_oscillating_camera()
    {
        var with = Jitter(60, 0.9, 1.25);
        var without = Jitter(0, 1.0, 1.0);   // negative control: no residence time, no low-water gap, no rank margin
        Assert.True(without.Thrash > 100, $"the jitter must thrash without the guards ({without})");
        Assert.Equal(0, with.Thrash);
        Assert.Equal(15, with.Loads);   // the first fill, then nothing moves
    }

    [Fact]
    public void Where_every_pose_change_needs_swaps_the_minimum_residence_bounds_them()
    {
        var with = Oscillate(60, 0.9, 1.25);
        var without = Oscillate(0, 1.0, 1.0);
        Assert.True(without.Thrash > 500, $"negative control: a swap on every pose change ({without})");
        // measured: 156 with the guards, 708 without. Swaps are needed here to show what is on screen, so the
        // guards can only slow them (steps a texture down a level instead)
        Assert.True(with.Thrash * 4 < without.Thrash, $"with {with} vs without {without}");
    }

    [Fact]
    public void The_placeholder_colour_is_the_textures_average_never_black_or_magenta()
    {
        // 2 x 2: red, green, blue, white -> (128, 128, 128)
        byte[] px = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        Assert.Equal(PlaceholderColour.Of(128, 128, 128, 255), PlaceholderColour.Average(px));
        Assert.Equal(PlaceholderColour.Of(90, 140, 30, 200), PlaceholderColour.ForPlaceholder(PlaceholderColour.Of(90, 140, 30, 200)));
        var black = PlaceholderColour.ForPlaceholder(PlaceholderColour.Of(0, 0, 0, 255));
        Assert.False(PlaceholderColour.IsBlack(black));
        var magenta = PlaceholderColour.ForPlaceholder(PlaceholderColour.Of(255, 0, 255, 255));
        Assert.False(PlaceholderColour.IsMagenta(magenta));
        // the average is recorded at the FIRST decode; a reload does not change it
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        r.Uploaded(G(1), 100, PlaceholderColour.Of(10, 20, 30, 255), TestClock.Frames(0));
        r.Uploaded(G(1), 100, PlaceholderColour.Of(200, 200, 200, 255), TestClock.Frames(5));
        Assert.Equal(PlaceholderColour.Of(10, 20, 30, 255), r.AverageOf(G(1)));
    }

    [Fact]
    public void Reloads_come_from_the_local_content_store_not_the_network()
    {
        var root = TestTemp.NewFolderPath("residency-store");   // deleted at the end of the run by the helper
        var view = new ContentStore(new ContentStoreOptions { Root = root }).View("grid", "s1");
        int network = 0;
        byte[] bytes = [1, 2, 3, 4, 5];
        Task<AssetFetchResult> Net(Guid id, CancellationToken ct) { network++; return Task.FromResult(AssetFetchResult.Ok(bytes)); }
        var fetch = view.Caching("texture", Net);
        // first load: from the network, written to the store
        using (var first = new SessionAssets(new SharedAssetStore(), _ => true, fetch))
            Assert.Equal(AssetFetchStatus.Ok, first.GetAsync(G(1), default).Result.Status);
        Assert.Equal(1, network);
        // the reload after an eviction (decoded copy gone, a fresh in-memory store): from the content store
        using (var reload = new SessionAssets(new SharedAssetStore(), _ => true, fetch))
        {
            var again = reload.GetAsync(G(1), default).Result;
            Assert.Equal(AssetFetchStatus.Ok, again.Status);
            Assert.Equal(bytes, again.Bytes.ToArray());
        }
        Assert.Equal(1, network);
        // negative control: a texture the store does not have goes to the network
        using (var other = new SessionAssets(new SharedAssetStore(), _ => true, fetch))
            other.GetAsync(G(2), default).Wait();
        Assert.Equal(2, network);
    }

    [Fact]
    public void The_same_inputs_always_give_the_same_decisions()
    {
        static string Run()
        {
            var (r, size) = Scene(20 * MiB, seed: 11);
            var log = new System.Text.StringBuilder();
            for (int f = 0; f < 800; f++)
                foreach (var a in Step(r, Walk(f), f, id => size[id]))
                    log.Append(f).Append(a.Kind).Append(a.Texture).Append(';');
            return log.Append(r.Evictions).Append('/').Append(r.Reloads).Append('/').Append(r.Thrash).ToString();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void The_priority_update_cost_per_frame_is_bounded_by_the_users_per_frame()
    {
        var (r, size) = Scene(24 * MiB);
        for (int f = 0; f < 10; f++)
        {
            Step(r, Walk(f), f, id => size[id]);
            Assert.Equal(128, r.LastUsersScored);   // 400 users, 128 per frame
        }
    }
}
