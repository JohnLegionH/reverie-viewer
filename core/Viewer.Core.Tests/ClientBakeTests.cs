// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using OpenSimNGC.Appearance.Baking;
using Viewer.Core.Appearance;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Xunit;
using BakeKind = OpenSimNGC.Appearance.Baking.WearableKind;
using TextureSlot = OpenSimNGC.Appearance.Baking.TextureSlot;

namespace Viewer.Core.Tests;

/// <summary>
/// Client-side baking for regions that do not bake on the server: the choice made at each region, the all-or-nothing run, and the
/// message that is sent. No grid and no character data are needed; the compositor is a stand-in.
/// </summary>
public class ClientBakeControllerTests
{
    private sealed class Rig
    {
        public int Bakes, Resends, Sends;
        public readonly List<string> Why = [];
        public TaskCompletionSource<bool>? Hold;
        public bool HaveLook;
        public ClientBakeController C = null!;
        public CancellationToken LastRunToken;

        public Rig()
        {
            C = new ClientBakeController(async (why, ct) =>
            {
                Bakes++;
                Why.Add(why);
                LastRunToken = ct;
                if (Hold is { } h) await h.Task.ConfigureAwait(false);
                HaveLook = true;
                C.TrySend(ct, () => { Sends++; return true; });
            }, () => HaveLook && C.TrySend(CancellationToken.None, () => { Resends++; return true; }), new CoreLog(new ListSink(), "test"));
        }
    }

    private static async Task Until(Func<bool> done)
    {
        var give_up = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < give_up, "the run did not start");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_server_baked_region_starts_no_bake_and_sends_nothing()
    {
        var r = new Rig();
        var action = r.C.OnRegion("Alpha", serverBaked: true);
        await r.C.Idle();
        Assert.Equal(AppearanceMode.Server, r.C.Mode);
        Assert.Equal(BakeRegionAction.None, action);
        Assert.Equal(0, r.Bakes);
        Assert.Equal(0, r.Sends + r.Resends);
        Assert.False(r.C.RequestBake("outfit"));
        Assert.False(r.C.TrySend(CancellationToken.None, () => { r.Sends++; return true; }));
        Assert.Equal(0, r.Sends);
    }

    [Fact]
    public async Task A_region_without_the_appearance_service_starts_one_bake_and_the_bake_sends_once()
    {
        var r = new Rig();
        var action = r.C.OnRegion("Alpha", serverBaked: false);
        await r.C.Idle();
        Assert.Equal(AppearanceMode.Client, r.C.Mode);
        Assert.Equal(BakeRegionAction.Started, action);
        Assert.Equal(1, r.Bakes);
        Assert.Equal(1, r.Sends);
    }

    [Fact]
    public async Task A_crossing_into_a_server_baked_region_mid_bake_stops_the_send_and_switches_mode()
    {
        var r = new Rig { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        r.C.OnRegion("Alpha", serverBaked: false);
        Assert.True(r.C.Running);
        await Until(() => r.Bakes == 1);   // the run has started and holds
        var action = r.C.OnRegion("Beta", serverBaked: true);
        r.Hold.SetResult(true);   // the bake finishes after the crossing
        await r.C.Idle();
        Assert.Equal(BakeRegionAction.Aborted, action);
        Assert.Equal(AppearanceMode.Server, r.C.Mode);
        Assert.True(r.LastRunToken.IsCancellationRequested);
        Assert.Equal(0, r.Sends);
    }

    [Fact]
    public async Task A_crossing_from_a_server_baked_region_into_one_without_the_service_starts_a_bake()
    {
        var r = new Rig();
        r.C.OnRegion("Alpha", serverBaked: true);
        var action = r.C.OnRegion("Beta", serverBaked: false);
        await r.C.Idle();
        Assert.Equal(BakeRegionAction.Started, action);
        Assert.Equal(AppearanceMode.Client, r.C.Mode);
        Assert.Equal(1, r.Bakes);
        Assert.Equal(1, r.Sends);
    }

    [Fact]
    public async Task Arriving_again_in_a_client_region_sends_the_look_already_made_instead_of_baking()
    {
        var r = new Rig();
        r.C.OnRegion("Alpha", serverBaked: false);
        await r.C.Idle();
        var action = r.C.OnRegion("Beta", serverBaked: false);   // a teleport: the new region does not know the look
        await r.C.Idle();
        Assert.Equal(BakeRegionAction.Resent, action);
        Assert.Equal(1, r.Bakes);
        Assert.Equal(1, r.Resends);
    }

    [Fact]
    public async Task A_change_of_the_worn_set_in_a_client_region_bakes_again_and_a_stopped_run_never_sends()
    {
        var r = new Rig();
        r.C.OnRegion("Alpha", serverBaked: false);
        await r.C.Idle();
        Assert.True(r.C.RequestBake("outfit"));
        await r.C.Idle();
        Assert.Equal(2, r.Bakes);
        Assert.Equal("outfit", r.Why[1]);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.False(r.C.TrySend(stopped.Token, () => { r.Sends++; return true; }));
        Assert.Equal(2, r.Sends);
    }
}

public class ClientBakePipelineTests
{
    private static readonly Guid SkinTex = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static string Wearable(int type, string name, string textures = "") =>
        $"LLWearable version 22\n{name}\n\n\tpermissions 0\n\t{{\n\t}}\ntype {type}\nparameters 1\n80 0.5\ntextures {(textures.Length == 0 ? 0 : 1)}\n{textures}\n";

    private sealed class Env : IBakeEnvironment
    {
        public List<WornItem>? Worn = [];
        public readonly Dictionary<Guid, string> Assets = [];
        public readonly HashSet<Guid> Textures = [];
        public int Uploads, FailUploadFrom = int.MaxValue;
        public readonly List<Guid> FetchedTextures = [];
        public Task<IReadOnlyList<WornItem>?> ListWornAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WornItem>?>(Worn);
        public Task<byte[]?> FetchWearableAsync(Guid assetId, bool bodyPart, CancellationToken ct) =>
            Task.FromResult(Assets.TryGetValue(assetId, out var t) ? (byte[]?)System.Text.Encoding.UTF8.GetBytes(t) : null);
        public Task<byte[]?> FetchTextureAsync(Guid id, CancellationToken ct)
        {
            FetchedTextures.Add(id);
            return Task.FromResult(Textures.Contains(id) ? new byte[] { 1 } : null);
        }
        public Task<Guid> UploadBakeAsync(byte[] j2k, CancellationToken ct)
        {
            Uploads++;
            return Task.FromResult(Uploads >= FailUploadFrom ? Guid.Empty : Guid.NewGuid());
        }
    }

    private sealed class Compositor : IBakeCompositor
    {
        public BakeRequest? Seen;
        public IReadOnlyList<string> RefusalList = [];
        public IReadOnlyList<string> Refusals(IReadOnlyList<ParsedWearable> worn, IReadOnlyList<BakeChannel> channels) => RefusalList;
        public IReadOnlyCollection<TextureSlot> SlotsOf(IReadOnlyList<BakeChannel> channels) => [TextureSlot.HeadBodypaint];
        public bool IsMale(IReadOnlyList<ParsedWearable> worn) => false;
        public Task<IReadOnlyList<BakeResult>> BakeAsync(BakeRequest request, CancellationToken ct)
        {
            Seen = request;
            var fid = new FidelityReport([], [], [], []);
            IReadOnlyList<BakeResult> l = new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair }
                .Select(c => new BakeResult(c, [1, 2, 3], "hash" + c, fid)).ToList();
            return Task.FromResult(l);
        }
        public byte[] EncodeVisualParams(IEnumerable<(BakeKind Kind, IReadOnlyDictionary<int, float> Params)> worn) => [1, 2, 3, 4];
    }

    private static Env FullOutfit(out Guid skinAsset)
    {
        var e = new Env();
        skinAsset = Guid.NewGuid();
        var types = new[] { (0, "shape"), (1, "skin"), (2, "hair"), (3, "eyes"), (4, "shirt") };
        foreach (var (t, n) in types)
        {
            var a = t == 1 ? skinAsset : Guid.NewGuid();
            e.Worn!.Add(new WornItem(Guid.NewGuid(), a, t));
            e.Assets[a] = Wearable(t, n, t == 1 ? $"0 {SkinTex}" : "");
        }
        e.Textures.Add(SkinTex);
        return e;
    }

    private static ClientBakePipeline Pipe(Env e, Compositor c) => new(e, c, new CoreLog(new ListSink(), "test"));

    [Fact]
    public async Task A_complete_outfit_gives_a_look_with_every_bake_uploaded()
    {
        var e = FullOutfit(out _);
        var c = new Compositor();
        var r = await Pipe(e, c).RunAsync(CancellationToken.None);
        Assert.True(r.Ok, r.Detail);
        Assert.NotNull(r.Look);
        Assert.Equal(5, r.Look!.Bakes.Count);
        Assert.Equal(5, e.Uploads);
        Assert.Equal([1, 2, 3, 4], r.Look.VisualParams);
        Assert.Equal(5, c.Seen!.Wearables.Count);
        Assert.Contains(new LibreMetaverse.UUID(SkinTex), c.Seen.Textures.Keys);
        Assert.Equal(1024, c.Seen.BakeSize);
    }

    [Fact]
    public async Task A_missing_required_wearable_gives_no_look_and_no_upload()
    {
        var e = FullOutfit(out _);
        e.Worn!.RemoveAll(w => w.WearableType == 3);   // no eyes
        var r = await Pipe(e, new Compositor()).RunAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Null(r.Look);
        Assert.Contains(r.Missing, m => m.Contains("Eyes", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, e.Uploads);
    }

    [Fact]
    public async Task A_wearable_that_cannot_be_fetched_gives_no_look()
    {
        var e = FullOutfit(out var skin);
        e.Assets.Remove(skin);
        var r = await Pipe(e, new Compositor()).RunAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Null(r.Look);
        Assert.Equal(0, e.Uploads);
    }

    [Fact]
    public async Task A_texture_that_cannot_be_fetched_gives_no_look_and_no_upload()
    {
        var e = FullOutfit(out _);
        e.Textures.Clear();
        var r = await Pipe(e, new Compositor()).RunAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Null(r.Look);
        Assert.Equal(0, e.Uploads);
        Assert.Contains(SkinTex, e.FetchedTextures);
    }

    [Fact]
    public async Task An_outfit_the_compositor_cannot_reproduce_is_refused_before_any_texture_is_fetched()
    {
        var e = FullOutfit(out _);
        var c = new Compositor { RefusalList = ["wearable type 99 is not composited"] };
        var r = await Pipe(e, c).RunAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Empty(e.FetchedTextures);
        Assert.Equal(0, e.Uploads);
        Assert.Contains(r.Missing, m => m.Contains("99"));
    }

    [Fact]
    public async Task One_failed_upload_gives_no_look_even_though_the_others_went_up()
    {
        var e = FullOutfit(out _);
        e.FailUploadFrom = 4;   // the first three succeed, the rest fail (every attempt)
        var r = await Pipe(e, new Compositor()).RunAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Null(r.Look);
    }

    [Fact]
    public async Task A_cancelled_run_gives_no_look()
    {
        var e = FullOutfit(out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipe(e, new Compositor()).RunAsync(cts.Token));
        Assert.Equal(0, e.Uploads);
    }
}

public class ClientBakeMessageTests
{
    private static ParsedWearable Worn(BakeKind kind, params (TextureSlot, Guid)[] tex) =>
        new(kind, kind.ToString(), new Dictionary<int, float> { [80] = 0f }, tex.ToDictionary(t => t.Item1, t => new LibreMetaverse.UUID(t.Item2)));

    [Fact]
    public void The_five_classic_bakes_are_made_for_a_plain_outfit()
    {
        var worn = new[] { Worn(BakeKind.Shape), Worn(BakeKind.Skin), Worn(BakeKind.Hair), Worn(BakeKind.Eyes), Worn(BakeKind.Shirt) };
        Assert.Equal([BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair], BakeBackend.ChannelsFor(worn));
    }

    [Fact]
    public void A_skirt_and_each_extra_bakes_on_mesh_channel_a_worn_wearable_paints_are_added()
    {
        var worn = new[]
        {
            Worn(BakeKind.Shape), Worn(BakeKind.Skin), Worn(BakeKind.Hair), Worn(BakeKind.Eyes),
            Worn(BakeKind.Skirt),
            Worn(BakeKind.Universal, (TextureSlot.LeftArmTattoo, Guid.NewGuid()), (TextureSlot.Aux2Tattoo, Guid.NewGuid()),
                (TextureSlot.Aux1Tattoo, BakeConstants.DefaultAvatarTexture.Guid)),   // the placeholder paints nothing
        };
        var channels = BakeBackend.ChannelsFor(worn);
        Assert.Contains(BakeChannel.Skirt, channels);
        Assert.Contains(BakeChannel.LeftArm, channels);
        Assert.Contains(BakeChannel.Aux2, channels);
        Assert.DoesNotContain(BakeChannel.Aux1, channels);
        Assert.DoesNotContain(BakeChannel.LeftLeg, channels);
        Assert.Equal(8, channels.Count);
    }

    private static PreparedLook Look(IReadOnlyList<WornItem> worn, params BakeChannel[] channels)
    {
        var parsed = worn.Select(w => Worn((BakeKind)w.WearableType)).ToList();
        return new PreparedLook(worn, parsed, channels.ToDictionary(c => c, _ => Guid.NewGuid()), [9, 8, 7], false);
    }

    [Fact]
    public void The_message_names_each_bake_on_its_own_face_and_carries_the_visual_parameters_and_serial()
    {
        var worn = new[] { new WornItem(Guid.NewGuid(), Guid.NewGuid(), 1), new WornItem(Guid.NewGuid(), Guid.NewGuid(), 3) };
        var look = Look(worn, BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair, BakeChannel.LeftArm);
        var m = SetAppearanceBuilder.Build(look, 7, 1.8f);
        Assert.Equal(7u, m.Serial);
        Assert.Equal([9, 8, 7], m.VisualParams);
        Assert.Equal(look.Bakes[BakeChannel.Head], m.Faces[8]);
        Assert.Equal(look.Bakes[BakeChannel.Upper], m.Faces[9]);
        Assert.Equal(look.Bakes[BakeChannel.Lower], m.Faces[10]);
        Assert.Equal(look.Bakes[BakeChannel.Eyes], m.Faces[11]);
        Assert.Equal(look.Bakes[BakeChannel.Hair], m.Faces[20]);
        Assert.Equal(look.Bakes[BakeChannel.LeftArm], m.Faces[40]);
        Assert.Equal(6, m.Faces.Count);
        Assert.Equal(1.8f, m.SizeZ);
        Assert.Equal(0.45f, m.SizeX);
        Assert.Equal(0.6f, m.SizeY);
    }

    [Fact]
    public void The_cache_hash_of_a_layer_is_the_worn_assets_that_feed_it_combined_with_the_layer_constant_and_zero_when_nothing_feeds_it()
    {
        var skin = Guid.NewGuid();
        var worn = new[] { new WornItem(Guid.NewGuid(), skin, 1) };
        var m = SetAppearanceBuilder.Build(Look(worn, BakeChannel.Head), 1, 1.8f);
        Assert.Equal(LibreMetaverse.AppearanceManager.BAKED_TEXTURE_COUNT, m.Cache.Count);
        var head = m.Cache.Single(c => c.TextureIndex == 8);
        var expected = new LibreMetaverse.UUID(skin) ^ LibreMetaverse.AppearanceManager.BAKED_TEXTURE_HASH[0];
        Assert.Equal(expected.Guid, head.CacheId);
        Assert.Equal(Guid.Empty, m.Cache.Single(c => c.TextureIndex == 19).CacheId);   // no skirt worn
        var other = SetAppearanceBuilder.Build(Look([new WornItem(Guid.NewGuid(), Guid.NewGuid(), 1)], BakeChannel.Head), 1, 1.8f);
        Assert.NotEqual(head.CacheId, other.Cache.Single(c => c.TextureIndex == 8).CacheId);
    }

    [Fact]
    public void Without_a_height_from_the_region_the_size_comes_from_the_worn_shape()
    {
        var worn = new[] { new WornItem(Guid.NewGuid(), Guid.NewGuid(), 0) };
        var m = SetAppearanceBuilder.Build(Look(worn, BakeChannel.Head), 1, null);
        Assert.InRange(m.SizeZ, 1.3f, 2.4f);
        Assert.Equal(0.45f, m.SizeX);
    }
}

/// <summary>The real compositor over the character folder (local input, never in the repo): skipped with its reason unless NV_CHARACTER_DIR names it.</summary>
public class ClientBakeCharacterDataTests
{
    private static string Wearable(int type, string name) =>
        $"LLWearable version 22\n{name}\n\n\tpermissions 0\n\t{{\n\t}}\ntype {type}\nparameters 1\n80 0.5\ntextures 0\n";

    [CharacterFact]
    public async Task A_plain_outfit_composites_the_four_required_bakes_and_each_decodes_as_a_five_component_picture()
    {
        var folder = Environment.GetEnvironmentVariable(CharacterFactAttribute.Variable)!;
        var comp = LibraryCompositor.FromFolder(folder);
        var wearables = new[] { (0, "shape"), (1, "skin"), (2, "hair"), (3, "eyes") }
            .Select(w => new WearableInput(new LibreMetaverse.UUID(Guid.NewGuid()), w.Item1, Wearable(w.Item1, w.Item2))).ToList();
        var request = new BakeRequest(wearables, new Dictionary<int, float>(), new Dictionary<LibreMetaverse.UUID, TextureInput>(), 256);
        var parsed = wearables.Select(w => WearableParser.Parse(w.RawText)).ToList();
        var channels = BakeBackend.ChannelsFor(parsed);
        Assert.Empty(comp.Refusals(parsed, channels));
        var results = await comp.BakeAsync(request, CancellationToken.None);
        foreach (var c in new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes })
        {
            var r = Assert.Single(results, x => x.Channel == c);
            Assert.False(r.NothingDrawn, $"{c} drew nothing");
            var decoded = J2kCodec.Decode(r.J2kBytes);
            Assert.Equal(256, decoded.W);
            Assert.NotNull(decoded.Mask);   // the fifth component, the morph mask a viewer's own bakes carry
        }
        var visual = comp.EncodeVisualParams(parsed.Select(p => (p.Kind, p.Params)));
        Assert.InRange(visual.Length, 200, 400);
    }
}
