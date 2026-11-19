// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A script in the inventory saves with the target its own item says (the runtime in its metadata where a grid sends one, the Lua subtype and
/// the LSL-on-Lua bit in its flags), and where nothing says, a grid that keeps the target gets none from the save. Pure data and canned
/// answers; no shared state, so the class runs in parallel.
/// </summary>
public class ScriptItemTargetTests
{
    private static Guid G(int n) => new(n, 0, 0, new byte[8]);
    private static readonly Guid Agent = G(1), Item = G(3), Asset = G(4);
    private const uint All = NotecardPermissions.Copy | NotecardPermissions.Modify;

    private static ScriptAccess Inv(string runtime = "", bool lua = false, bool lslLuau = false) =>
        new(Item, Asset, Agent, new NotecardPermissions(Agent, Agent, 0x7fffffff, All, 0, 0, 0), false, LuaSubtype: lua, LslOnLuau: lslLuau, Runtime: runtime);

    private static ScriptEditing Opened(ScriptAccess a, bool secondLife)
    {
        var e = new ScriptEditing(a, "x") { SecondLife = secondLife };
        e.TextFetched(AssetFetchResult.Ok("default{}"u8.ToArray()));
        return e;
    }

    private static OSDMap Body(ScriptEditing e) => LmvTransport.ScriptUploadBody(e.Access, e.Target, running: true);

    [Theory]
    [InlineData("lsl2", "lsl2")]
    [InlineData("mono", "mono")]
    [InlineData("lsl-luau", "lsl-luau")]
    [InlineData("luau", "luau")]
    [InlineData("LSL-LUAU", "lsl-luau")]
    public void An_item_that_names_its_runtime_saves_with_it(string runtime, string expected)
    {
        Assert.Equal(expected, Inv(runtime).KnownTarget?.Value);
        Assert.Equal(expected, Body(Opened(Inv(runtime), secondLife: true))["target"].AsString());
    }

    [Fact]
    public void An_lsl_on_lua_inventory_script_saves_as_lsl_on_lua_not_mono()
    {
        Assert.Equal("lsl-luau", Body(Opened(Inv(lslLuau: true), secondLife: false))["target"].AsString());
        Assert.Equal("lsl-luau", Body(Opened(Inv(lslLuau: true), secondLife: true))["target"].AsString());
    }

    [Fact]
    public void A_lua_subtype_beats_the_lsl_on_lua_bit_and_the_runtime_beats_both()
    {
        Assert.Equal("luau", Inv(lua: true, lslLuau: true).KnownTarget?.Value);
        Assert.Equal("mono", Inv("mono", lua: true, lslLuau: true).KnownTarget?.Value);
    }

    [Fact]
    public void An_unknown_runtime_text_says_nothing()
    {
        Assert.Null(Inv("somethingelse").KnownTarget);
        Assert.Null(ScriptTarget.FromName(""));
        Assert.Null(ScriptTarget.FromName(null));
    }

    [Fact]
    public void Where_nothing_is_known_second_life_gets_mono_and_another_grid_gets_no_target()
    {
        Assert.Equal("mono", Body(Opened(Inv(), secondLife: true))["target"].AsString());
        var other = Body(Opened(Inv(), secondLife: false));
        Assert.False(other.ContainsKey("target"));
        Assert.Equal(Item, other["item_id"].AsUUID().Guid);
    }

    [Fact]
    public void The_reply_of_a_script_in_an_object_still_decides_when_it_comes()
    {
        var e = Opened(Inv(), secondLife: false);
        e.RunningKnown(new ScriptRuntime(true, false, Luau: true));
        Assert.Equal("lsl-luau", Body(e)["target"].AsString());
    }

    // ---------------- the runtime an inventory answer names ----------------

    private static OSDMap ItemMap(Guid id, string? runtime) => new()
    {
        ["item_id"] = OSD.FromUUID(new LibreMetaverse.UUID(id)),
        ["name"] = OSD.FromString("s"),
        ["metadata"] = runtime is null ? new OSDMap() : new OSDMap { ["script"] = new OSDMap { ["runtime"] = OSD.FromString(runtime) } },
    };

    [Fact]
    public void The_runtime_is_read_from_items_in_an_inventory_answer_wherever_they_sit()
    {
        var a = G(10); var b = G(11); var c = G(12);
        var answer = new OSDMap
        {
            ["folders"] = new OSDArray { new OSDMap { ["items"] = new OSDArray { ItemMap(a, "lsl-luau"), ItemMap(b, null) } } },
            ["_embedded"] = new OSDMap { ["items"] = new OSDMap { ["x"] = ItemMap(c, "mono") } },
        };
        var read = LmvInventoryRuntime.Read(answer).ToDictionary(r => r.ItemId, r => r.Runtime);
        Assert.Equal(new Dictionary<Guid, string> { [a] = "lsl-luau", [c] = "mono" }, read);
    }

    [Fact]
    public void An_answer_without_items_names_nothing() => Assert.Empty(LmvInventoryRuntime.Read(new OSDMap { ["ok"] = OSD.FromBoolean(true) }));

    private sealed class Canned(string type, byte[] body) : HttpMessageHandler
    {
        public int Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return Task.FromResult(r);
        }
    }

    private static async Task<(byte[] Seen, List<OSD> Handed)> Get(string type, byte[] body)
    {
        var handed = new List<OSD>();
        var tap = new LmvLoginTap(new Canned(type, body), new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test")) { InventoryAnswer = handed.Add };
        using var client = new HttpClient(tap);
        using var response = await client.GetAsync("http://example.org/cap");
        return (await response.Content.ReadAsByteArrayAsync(), handed);
    }

    [Fact]
    public async Task The_tap_hands_over_an_llsd_answer_that_names_a_runtime_and_the_library_still_gets_the_whole_answer()
    {
        var body = OSDParser.SerializeLLSDXmlBytes(new OSDMap { ["items"] = new OSDArray { ItemMap(G(10), "lsl-luau") } });
        var (seen, handed) = await Get("application/llsd+xml", body);
        Assert.Equal(body, seen);
        Assert.Single(LmvInventoryRuntime.Read(Assert.Single(handed)));
    }

    [Fact]
    public async Task The_tap_leaves_other_kinds_of_answer_alone()
    {
        var textureBytes = System.Text.Encoding.ASCII.GetBytes("runtime metadata script");
        var (seen, handed) = await Get("image/x-j2c", textureBytes);
        Assert.Equal(textureBytes, seen);
        Assert.Empty(handed);
        var plain = OSDParser.SerializeLLSDXmlBytes(new OSDMap { ["ok"] = OSD.FromBoolean(true) });
        Assert.Empty((await Get("application/llsd+xml", plain)).Handed);
    }

    // ---------------- a script inside an object, before the region says how it runs ----------------

    private static Viewer.Core.Session.TaskItem InObject(uint flags) =>
        new(Item, "Script", 10, 10, Agent, All, 0, Asset, Agent, 0x7fffffff, 0, 0, Flags: flags);

    private static ScriptEditing OpenedInObject(uint flags, bool secondLife) =>
        Opened(ScriptEditing.AccessFor(InObject(flags), G(5), 77, Agent)!, secondLife);

    [Fact]
    public void A_lua_script_in_an_object_saves_as_lua_before_the_region_says_how_it_runs()
    {
        Assert.Equal("luau", Body(OpenedInObject(1, secondLife: true))["target"].AsString());
        Assert.Equal("luau", Body(OpenedInObject(1, secondLife: false))["target"].AsString());
    }

    [Fact]
    public void An_lsl_on_lua_script_in_an_object_saves_as_lsl_on_lua_before_the_region_says_how_it_runs()
    {
        Assert.Equal("lsl-luau", Body(OpenedInObject(ScriptAccess.LslOnLuauFlag, secondLife: true))["target"].AsString());
        Assert.Equal("lsl-luau", Body(OpenedInObject(ScriptAccess.LslOnLuauFlag, secondLife: false))["target"].AsString());
    }

    [Fact]
    public void A_plain_lsl_script_in_an_object_still_saves_as_mono_on_second_life_until_the_region_says_otherwise()
    {
        Assert.Equal("mono", Body(OpenedInObject(0, secondLife: true))["target"].AsString());
        Assert.False(Body(OpenedInObject(0, secondLife: false)).ContainsKey("target"));
    }
}
