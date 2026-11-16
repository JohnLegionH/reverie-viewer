// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Server bakes come from the appearance service the login reply names, at texture/avatar id/slot/bake id, as the
/// official viewer fetches them (LLVOAvatar::getImageURL, llvoavatar.cpp:5889-5908), for every avatar; with no service URL,
/// a region that does not bake avatars, or an appearance that is not server-baked, a bake is fetched by id from the region's
/// texture capability as before. One fake server plays both: it records every path asked.
/// Runs in parallel: each test has its own server and transport, and nothing process-wide is touched.
/// </summary>
public sealed class BakeServiceTests
{
    private static readonly Guid Me = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid Them = new("bbbbbbbb-0000-4000-8000-000000000002");

    // the eleven slots in bake-type order, with the TextureEntry face each arrives on (llavatarappearancedefines.cpp:81-91)
    private static readonly (string Slot, int Face)[] Slots =
        [("head", 8), ("upper", 9), ("lower", 10), ("eyes", 11), ("skirt", 19), ("hair", 20), ("leftarm", 40), ("leftleg", 41), ("aux1", 42), ("aux2", 43), ("aux3", 44)];

    private static Guid Bake(Guid agent, int slot) => new($"{agent.ToString("D")[..8]}-1111-4000-8000-{slot:D12}");

    private static AvatarAppearanceData Look(Guid agent, int version = 1)
    {
        var faces = new Guid[45];
        for (int i = 0; i < Slots.Length; i++) faces[Slots[i].Face] = Bake(agent, i);
        return new AvatarAppearanceData(agent, faces.ToImmutableArray(), VisualParamSet.Empty, version, 3);
    }

    private static LoopbackTextureServer Server() => new(_ => PartialFetchTests.Codestream.Value);

    private static LmvTransport Transport(LoopbackTextureServer server, string? service, bool regionBakes = true)
    {
        var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "bake"));
        t.TextureCapabilityOverride = () => new Uri(server.Base, "caps/viewerasset/");
        t.AppearanceServiceOverride = () => service;
        t.RegionBakesOverride = () => regionBakes;
        return t;
    }

    private static string Service(LoopbackTextureServer s) => new Uri(s.Base, "agentappearance/").ToString();

    [Fact]
    public async Task Every_bake_of_every_avatar_comes_from_the_appearance_service_by_avatar_slot_and_id()
    {
        using var server = Server();
        using var t = Transport(server, Service(server));
        t.NoteAppearance(Look(Me));
        t.NoteAppearance(Look(Them));
        var expected = new List<string>();
        foreach (var agent in new[] { Me, Them })
            for (int i = 0; i < Slots.Length; i++)
            {
                var whole = await t.FetchTextureAsync(Bake(agent, i), CancellationToken.None);
                Assert.Equal(AssetFetchStatus.Ok, whole.Status);
                var part = await t.GetTextureRangeAsync(Bake(agent, i), 0, 599, 1 << 20, CancellationToken.None);
                Assert.Equal(RangeAnswerKind.Part, part.Kind);
                var path = $"/agentappearance/texture/{agent:D}/{Slots[i].Slot}/{Bake(agent, i):D}";
                expected.Add(path);
                expected.Add(path);
            }
        Assert.Equal(expected, server.Targets);
    }

    [Fact]
    public async Task With_no_appearance_service_a_bake_is_fetched_by_id_from_the_texture_capability()
    {
        using var server = Server();
        foreach (var none in new[] { null, "" })
        {
            using var t = Transport(server, none);
            t.NoteAppearance(Look(Them));
            Assert.Equal(AssetFetchStatus.Ok, (await t.FetchTextureAsync(Bake(Them, 0), CancellationToken.None)).Status);
            Assert.Equal(RangeAnswerKind.Part, (await t.GetTextureRangeAsync(Bake(Them, 3), 0, 599, 1 << 20, CancellationToken.None)).Kind);
        }
        var byId = (Guid id) => $"/caps/viewerasset/?texture_id={id:D}";
        Assert.Equal([byId(Bake(Them, 0)), byId(Bake(Them, 3)), byId(Bake(Them, 0)), byId(Bake(Them, 3))], server.Targets);
    }

    [Fact]
    public async Task A_region_that_does_not_bake_avatars_or_an_appearance_that_is_not_server_baked_fetches_by_id()
    {
        using var server = Server();
        using (var t = Transport(server, Service(server), regionBakes: false))
        {
            t.NoteAppearance(Look(Them));
            await t.FetchTextureAsync(Bake(Them, 1), CancellationToken.None);
        }
        using (var t = Transport(server, Service(server)))
        {
            t.NoteAppearance(Look(Them, version: 0));
            await t.FetchTextureAsync(Bake(Them, 2), CancellationToken.None);
        }
        Assert.Equal([$"/caps/viewerasset/?texture_id={Bake(Them, 1):D}", $"/caps/viewerasset/?texture_id={Bake(Them, 2):D}"], server.Targets);
    }

    [Fact]
    public async Task A_texture_that_is_no_avatars_bake_is_fetched_by_id_even_with_an_appearance_service()
    {
        using var server = Server();
        using var t = Transport(server, Service(server));
        t.NoteAppearance(Look(Me));
        var plain = new Guid("cccccccc-0000-4000-8000-000000000003");
        await t.FetchTextureAsync(plain, CancellationToken.None);
        await t.FetchTextureAsync(Bake(Me, 5), CancellationToken.None);
        Assert.Equal([$"/caps/viewerasset/?texture_id={plain:D}", $"/agentappearance/texture/{Me:D}/hair/{Bake(Me, 5):D}"], server.Targets);
    }

    /// <summary>IMG_INVISIBLE is a shared texture, never one avatar's bake: the official viewer builds no appearance-service address for it, nor for the two
    /// default ids (getBakedTextureImage, llvoavatar.cpp:2602-2608), so a face naming it is still fetched by id.</summary>
    [Fact]
    public async Task The_invisible_texture_on_a_bake_face_is_fetched_by_id()
    {
        var invisible = new Guid("3a367d1c-bef1-6d43-7595-e88c1e3aadb3");
        using var server = Server();
        using var t = Transport(server, Service(server));
        var look = Look(Them);
        t.NoteAppearance(look with { FaceTextures = look.FaceTextures.SetItem(19, invisible) });
        await t.FetchTextureAsync(invisible, CancellationToken.None);
        Assert.Equal([$"/caps/viewerasset/?texture_id={invisible:D}"], server.Targets);
    }

    /// <summary>An avatar still in view keeps its routes however many others pass: seeing its appearance again puts its bakes at the back of the line.</summary>
    [Fact]
    public void An_appearance_seen_again_keeps_its_routes_past_the_bound()
    {
        var routes = new BakeRoutes();
        var stays = new Guid("dddddddd-0000-4000-8000-000000000004");
        routes.Note(Look(stays));
        int avatars = BakeRoutes.Capacity / Slots.Length + 2;
        for (int i = 0; i < avatars; i++)
        {
            routes.Note(Look(new Guid($"{i:D8}-0000-4000-8000-000000000000")));
            if (i % 50 == 0) routes.Note(Look(stays));
        }
        Assert.Equal(BakeRoutes.Capacity, routes.Count);
        Assert.Equal(new Uri($"http://example.org/aa/texture/{stays:D}/head/{Bake(stays, 0):D}"), routes.UriFor(Bake(stays, 0), "http://example.org/aa/", true));
    }

    [Fact]
    public void The_routes_are_bounded_and_the_oldest_go_first()
    {
        var routes = new BakeRoutes();
        int avatars = BakeRoutes.Capacity / Slots.Length + 2;
        var agents = Enumerable.Range(0, avatars).Select(i => new Guid($"{i:D8}-0000-4000-8000-000000000000")).ToList();
        foreach (var a in agents) routes.Note(Look(a));
        Assert.Equal(BakeRoutes.Capacity, routes.Count);
        Assert.Null(routes.UriFor(Bake(agents[0], 0), "http://example.org/aa/", true));
        Assert.Equal(new Uri($"http://example.org/aa/texture/{agents[^1]:D}/aux3/{Bake(agents[^1], 10):D}"), routes.UriFor(Bake(agents[^1], 10), "http://example.org/aa/", true));
    }
}
