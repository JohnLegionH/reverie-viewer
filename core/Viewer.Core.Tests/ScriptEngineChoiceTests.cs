// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Choosing a script's engine or compile target: what a save sends back for a script as the region described it, Second Life's four compiler
/// choices, the engine drop-down and its tick box on OpenSim grids (the region's list, the saved choice, the first line, the region default),
/// the contents menu's Engine entry for a script that may not be modified, and the region's reply read as the region sends it. The regions here
/// are fakes with the replies the grid contract names. Pure data; no shared state, so the class runs in parallel.
/// </summary>
public class ScriptEngineChoiceTests
{
    private static Guid G(int n) => new(n, 0, 0, new byte[8]);
    private static readonly Guid Agent = G(1), Item = G(3), Asset = G(4), Box = G(5);
    private const uint Copy = NotecardPermissions.Copy, Modify = NotecardPermissions.Modify;

    private static ScriptAccess InObj(bool luaSubtype = false) =>
        new(Item, Asset, Agent, new NotecardPermissions(Agent, Agent, 0x7fffffff, Copy | Modify, 0, 0, 0), false, Box, 77, luaSubtype);

    private static ScriptAccess Inv(bool luaSubtype = false) =>
        new(Item, Asset, Agent, new NotecardPermissions(Agent, Agent, 0x7fffffff, Copy | Modify, 0, 0, 0), false, LuaSubtype: luaSubtype);

    private static ScriptRegion Region(string[] engines, bool lua = false, bool choice = false, bool noMod = false) => new(engines, lua, choice, noMod);

    private static readonly ScriptRegion Ngc = Region(["YEngine", "Example.Alpha", "SLua"], lua: true, choice: true);

    private static ScriptEditing Opened(ScriptAccess access, ScriptRegion region, bool secondLife = false, string text = "default{}")
    {
        var e = new ScriptEditing(access, "x") { SecondLife = secondLife, Region = region };
        e.TextFetched(AssetFetchResult.Ok(System.Text.Encoding.UTF8.GetBytes(text)));
        return e;
    }

    private static string Sent(ScriptEditing e) => LmvTransport.ScriptUploadBody(e.Access, e.Target, running: true)["target"].AsString();

    // ---------------- item 1: a save sends back the target the script has ----------------

    [Theory]
    [InlineData(true, false, false, "", "", "mono")]
    [InlineData(false, false, false, "", "", "lsl2")]
    [InlineData(false, true, true, "", "", "luau")]
    [InlineData(false, true, false, "", "", "lsl-luau")]
    [InlineData(true, false, false, "Alpha", "Alpha", "engine:Alpha")]
    [InlineData(true, false, false, "YEngine", "", "mono")]
    public void The_target_a_script_has_is_read_from_the_regions_reply(bool mono, bool luau, bool luauLanguage, string engine, string choice, string expected) =>
        Assert.Equal(expected, new ScriptRuntime(true, mono, luau, luauLanguage, engine, choice).Current.Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_lua_script_saved_in_the_editor_stays_lua_on_second_life_and_on_opensim(bool secondLife)
    {
        var e = Opened(InObj(), Ngc, secondLife);
        e.RunningKnown(new ScriptRuntime(true, false, Luau: true, LuauLanguage: true));
        e.Text = "x = 1";
        Assert.NotNull(e.BeginSave());
        Assert.Equal("luau", Sent(e));
    }

    [Fact]
    public void A_script_that_runs_lsl_on_lua_stays_lsl_on_lua()
    {
        var e = Opened(InObj(), Ngc);
        e.RunningKnown(new ScriptRuntime(true, false, Luau: true, LuauLanguage: false));
        Assert.Equal("lsl-luau", Sent(e));
    }

    [Fact]
    public void A_legacy_script_stays_legacy_and_a_mono_script_stays_mono()
    {
        var legacy = Opened(InObj(), ScriptRegion.None, secondLife: true);
        legacy.RunningKnown(new ScriptRuntime(true, false));
        Assert.Equal("lsl2", Sent(legacy));
        var mono = Opened(InObj(), ScriptRegion.None, secondLife: true);
        mono.RunningKnown(new ScriptRuntime(true, true));
        Assert.Equal("mono", Sent(mono));
    }

    [Fact]
    public void A_later_reply_does_not_undo_the_persons_choice()
    {
        var e = Opened(InObj(), Region([], lua: true), secondLife: true);
        e.Choose(ScriptTarget.Lsl2);
        e.RunningKnown(new ScriptRuntime(true, false, Luau: true, LuauLanguage: true));
        Assert.Equal("lsl2", Sent(e));
    }

    [Fact]
    public void A_script_in_the_inventory_that_the_grid_marks_as_lua_is_saved_as_lua_and_any_other_as_mono()
    {
        Assert.Equal("luau", Sent(Opened(Inv(luaSubtype: true), ScriptRegion.None)));
        Assert.Equal("mono", Sent(Opened(Inv(), ScriptRegion.None)));
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("lsl2")]
    [InlineData("lsl-luau")]
    [InlineData("luau")]
    [InlineData("engine:Example.Alpha")]
    public void The_save_request_carries_the_target_as_it_is(string value)
    {
        var inv = LmvTransport.ScriptUploadBody(Inv(), new ScriptTarget(value), running: true);
        Assert.Equal(value, inv["target"].AsString());
        var obj = LmvTransport.ScriptUploadBody(InObj(), new ScriptTarget(value), running: false);
        Assert.Equal(value, obj["target"].AsString());
        Assert.Equal(Box, obj["task_id"].AsUUID().Guid);
    }

    // ---------------- item 2: Second Life's four choices ----------------

    [Fact]
    public void Second_life_offers_the_four_compiler_choices_with_the_official_labels_and_values()
    {
        var all = ScriptEngineChoices.SecondLife(Region([], lua: true));
        Assert.Equal(["script.target.lsl2", "script.target.mono", "script.target.lsl_luau", "script.target.luau"], all.Select(c => c.LabelKey));
        Assert.Equal(["lsl2", "mono", "lsl-luau", "luau"], all.Select(c => c.Target.Value));
        Assert.All(all, c => Assert.True(c.Enabled));
    }

    [Fact]
    public void The_two_lua_entries_are_greyed_unless_the_region_sends_lua_scripts_enabled()
    {
        var all = ScriptEngineChoices.SecondLife(ScriptRegion.None);
        Assert.Equal([true, true, false, false], all.Select(c => c.Enabled));
        Assert.Equal([null, null, "script.target.lua_off", "script.target.lua_off"], all.Select(c => c.DisabledKey));
    }

    [Fact]
    public void On_second_life_the_editor_lists_the_four_for_a_script_in_an_object_and_in_the_inventory_and_saves_the_pick()
    {
        foreach (var access in new[] { InObj(), Inv() })
        {
            var e = Opened(access, Region([], lua: true), secondLife: true);
            Assert.Equal(4, e.Choices.Count);
            e.Choose(ScriptTarget.LslLuau);
            Assert.Equal("lsl-luau", Sent(e));
        }
    }

    [Fact]
    public void A_greyed_lua_entry_cannot_be_picked()
    {
        var e = Opened(InObj(), ScriptRegion.None, secondLife: true);
        e.Choose(ScriptTarget.Luau);
        Assert.Equal("mono", Sent(e));
    }

    // ---------------- item 3: OpenSim and NGC grids ----------------

    [Fact]
    public void Ticked_the_drop_down_lists_each_lsl_engine_then_slua_lsl_and_slua_lua_with_the_targets_they_save()
    {
        var all = ScriptEngineChoices.OpenSim(Ngc);
        Assert.Equal(["engine:YEngine", "engine:Example.Alpha", "engine:SLua", "lsl-luau", "luau"], all.Select(c => c.Target.Value));
        Assert.Equal(["script.engine.lsl", "script.engine.lsl", "script.engine.lsl", "script.engine.slua_lsl", "script.engine.slua_lua"], all.Select(c => c.LabelKey));
        Assert.Equal(["YEngine", "Example.Alpha", "SLua"], all.Take(3).Select(c => c.LabelArg));
        Assert.All(all, c => Assert.True(c.Enabled));
    }

    [Fact]
    public void Without_lua_there_are_no_slua_entries()
    {
        var all = ScriptEngineChoices.OpenSim(Region(["YEngine", "Example.Alpha"], lua: false, choice: true));
        Assert.Equal(["engine:YEngine", "engine:Example.Alpha"], all.Select(c => c.Target.Value));
    }

    [Fact]
    public void A_region_that_does_not_save_a_choice_greys_the_lsl_engines_and_keeps_the_lua_entries()
    {
        var all = ScriptEngineChoices.OpenSim(Region(["YEngine", "Example.Alpha"], lua: true, choice: false));
        Assert.Equal([false, false, true, true], all.Select(c => c.Enabled));
        Assert.Equal(["script.engine.no_choice", "script.engine.no_choice", null, null], all.Select(c => c.DisabledKey));
    }

    [Fact]
    public void A_region_that_lists_no_engines_shows_only_the_lua_choices_where_lua_is_on()
    {
        Assert.Equal(["lsl-luau", "luau"], ScriptEngineChoices.OpenSim(Region([], lua: true)).Select(c => c.Target.Value));
        Assert.Empty(ScriptEngineChoices.OpenSim(Region([], lua: false)));
        Assert.Empty(Opened(InObj(), Region([], lua: false)).Choices);
    }

    [Fact]
    public void Unticked_the_drop_down_says_what_runs_the_script_and_why()
    {
        var region = Ngc;
        // the reply names the engine and a saved choice
        Assert.Equal(new ScriptEngineWhy("Example.Alpha", "script.engine.why.saved"),
            ScriptEngineChoices.Why(region, new ScriptRuntime(true, true, Engine: "Example.Alpha", EngineChoice: "Example.Alpha"), "default{}"));
        // the reply names the engine and the first line asked for it
        Assert.Equal(new ScriptEngineWhy("Example.Alpha", "script.engine.why.first_line"),
            ScriptEngineChoices.Why(region, new ScriptRuntime(true, true, Engine: "Example.Alpha"), "//Alpha:\ndefault{}"));
        // the reply names the engine and nothing asked for it
        Assert.Equal(new ScriptEngineWhy("YEngine", "script.engine.why.default"),
            ScriptEngineChoices.Why(region, new ScriptRuntime(true, true, Engine: "YEngine"), "default{}"));
    }

    [Fact]
    public void Without_the_replys_engine_it_is_worked_out_from_the_first_line_and_the_region_default()
    {
        Assert.Equal(new ScriptEngineWhy("Example.Alpha", "script.engine.why.first_line"), ScriptEngineChoices.Why(Ngc, null, "//Alpha:\ndefault{}"));
        Assert.Equal(new ScriptEngineWhy("SLua", "script.engine.why.first_line"), ScriptEngineChoices.Why(Ngc, null, "--!slua\nprint(1)"));
        Assert.Equal(new ScriptEngineWhy("YEngine", "script.engine.why.default"), ScriptEngineChoices.Why(Ngc, null, "default{}"));
        Assert.Equal(new ScriptEngineWhy("YEngine", "script.engine.why.default"), ScriptEngineChoices.Why(Ngc, new ScriptRuntime(true, true), "//Alpha\ndefault{}"));   // no colon: not a header
        Assert.Equal(new ScriptEngineWhy("Example.Alpha", "script.engine.why.saved"), ScriptEngineChoices.Why(Ngc, new ScriptRuntime(true, true, EngineChoice: "Example.Alpha"), "//YEngine:\n"));
    }

    [Fact]
    public void The_first_line_names_an_engine_only_with_the_colon_and_only_one_the_region_lists()
    {
        Assert.Equal("YEngine", ScriptEngineChoices.HeaderEngine("//YEngine:lsl\nx", Ngc));
        Assert.Equal("Example.Alpha", ScriptEngineChoices.HeaderEngine("//alpha:\nx", Ngc));
        Assert.Equal("Example.Alpha", ScriptEngineChoices.HeaderEngine("//Example.Alpha:\nx", Ngc));
        Assert.Null(ScriptEngineChoices.HeaderEngine("//Alpha\nx", Ngc));
        Assert.Null(ScriptEngineChoices.HeaderEngine("//Unknown:\nx", Ngc));
        Assert.Null(ScriptEngineChoices.HeaderEngine("x\n//Alpha:", Ngc));
        Assert.Null(ScriptEngineChoices.HeaderEngine("", Ngc));
        Assert.Equal("SLua", ScriptEngineChoices.HeaderEngine("--!slua\nx", Ngc));
    }

    [Fact]
    public void Unticked_a_save_sends_the_current_target_unchanged()
    {
        var plain = Opened(InObj(), Ngc);
        plain.RunningKnown(new ScriptRuntime(true, true, Engine: "YEngine"));
        Assert.False(plain.ChooseEngine);
        Assert.Equal("mono", Sent(plain));

        var saved = Opened(InObj(), Ngc);
        saved.RunningKnown(new ScriptRuntime(true, true, Engine: "Example.Alpha", EngineChoice: "Example.Alpha"));
        Assert.Equal("engine:Example.Alpha", Sent(saved));

        var lua = Opened(InObj(), Ngc);
        lua.RunningKnown(new ScriptRuntime(true, false, Luau: true, LuauLanguage: true, Engine: "SLua"));
        Assert.Equal("luau", Sent(lua));
    }

    [Fact]
    public void Ticked_each_pick_saves_its_own_target()
    {
        var expected = new[] { "engine:YEngine", "engine:Example.Alpha", "engine:SLua", "lsl-luau", "luau" };
        foreach (var value in expected)
        {
            var e = Opened(InObj(), Ngc);
            e.RunningKnown(new ScriptRuntime(true, true, Engine: "YEngine"));
            e.SetChooseEngine(true);
            Assert.True(e.ChooseEngine);
            e.Choose(new ScriptTarget(value));
            Assert.Equal(value, Sent(e));
        }
    }

    [Fact]
    public void Ticked_a_greyed_engine_cannot_be_picked_and_a_lua_entry_can()
    {
        var region = Region(["YEngine", "Example.Alpha"], lua: true, choice: false);
        var e = Opened(InObj(), region);
        e.RunningKnown(new ScriptRuntime(true, true));
        e.SetChooseEngine(true);
        e.Choose(ScriptTarget.Engine("Example.Alpha"));
        Assert.Equal("mono", Sent(e));
        e.Choose(ScriptTarget.Luau);
        Assert.Equal("luau", Sent(e));
    }

    [Fact]
    public void Unticking_goes_back_to_the_current_target_and_asks_to_clear_a_saved_choice_only_when_there_was_one()
    {
        var saved = Opened(InObj(), Ngc);
        saved.RunningKnown(new ScriptRuntime(true, true, Engine: "Example.Alpha", EngineChoice: "Example.Alpha"));
        saved.SetChooseEngine(true);
        saved.Choose(ScriptTarget.Engine("YEngine"));
        Assert.True(saved.SetChooseEngine(false));   // the saved choice is to be cleared
        Assert.False(saved.ChooseEngine);
        Assert.Equal("mono", Sent(saved));

        var fresh = Opened(InObj(), Ngc);
        fresh.RunningKnown(new ScriptRuntime(true, true, Engine: "YEngine"));
        fresh.SetChooseEngine(true);
        fresh.Choose(ScriptTarget.Engine("Example.Alpha"));
        Assert.False(fresh.SetChooseEngine(false));   // nothing saved to clear
        Assert.Equal("mono", Sent(fresh));
    }

    [Fact]
    public void The_editor_offers_the_engine_row_to_scripts_in_the_inventory_too()
    {
        var e = Opened(Inv(), Ngc);
        Assert.Equal(5, ScriptEngineChoices.OpenSim(Ngc).Count);
        e.SetChooseEngine(true);
        e.Choose(ScriptTarget.LslLuau);
        Assert.Equal("lsl-luau", Sent(e));
    }

    // ---------------- item 4: the contents menu ----------------

    [Fact]
    public void The_contents_menu_is_offered_only_where_the_region_saves_a_choice()
    {
        Assert.False(ScriptEngineChoices.ContentsMenu(Region(["YEngine"], choice: false), modify: true).Offered);
        Assert.False(ScriptEngineChoices.ContentsMenu(ScriptRegion.None, modify: true).Offered);
        var on = ScriptEngineChoices.ContentsMenu(Ngc, modify: true);
        Assert.True(on.Offered);
        Assert.True(on.Enabled);
        Assert.Null(on.DisabledKey);
    }

    [Fact]
    public void A_no_modify_script_gets_the_menu_only_where_the_region_allows_it_otherwise_greyed_with_a_reason()
    {
        var closed = ScriptEngineChoices.ContentsMenu(Ngc, modify: false);
        Assert.True(closed.Offered);
        Assert.False(closed.Enabled);
        Assert.Equal("script.engine.no_mod", closed.DisabledKey);
        var open = ScriptEngineChoices.ContentsMenu(Region(["YEngine"], choice: true, noMod: true), modify: false);
        Assert.True(open.Offered);
        Assert.True(open.Enabled);
        Assert.Null(open.DisabledKey);
    }

    [Fact]
    public void The_set_engine_request_names_the_object_the_script_and_the_target_and_an_empty_target_clears()
    {
        var set = LmvTransport.ScriptEngineBody(InObj(), ScriptTarget.Engine("Example.Alpha"));
        Assert.Equal(Box, set["task_id"].AsUUID().Guid);
        Assert.Equal(Item, set["item_id"].AsUUID().Guid);
        Assert.Equal("engine:Example.Alpha", set["target"].AsString());
        var clear = LmvTransport.ScriptEngineBody(InObj(), null);
        Assert.True(clear.ContainsKey("target"));
        Assert.Equal("", clear["target"].AsString());
    }

    // ---------------- the region's replies ----------------

    private static OSDMap Reply(string obj, string item, Action<OSDMap> fill)
    {
        var script = new OSDMap { ["ObjectID"] = OSD.FromUUID(new UUID(obj)), ["ItemID"] = OSD.FromUUID(new UUID(item)), ["Running"] = OSD.FromBoolean(true) };
        fill(script);
        var ev = new OSDMap { ["message"] = OSD.FromString("ScriptRunningReply"), ["body"] = new OSDMap { ["Script"] = new OSDArray { script } } };
        return new OSDMap { ["id"] = OSD.FromInteger(5), ["events"] = new OSDArray { ev } };
    }

    [Fact]
    public void The_reply_gains_the_engine_and_the_saved_choice_and_an_old_region_sends_neither()
    {
        var o = Guid.NewGuid(); var i = Guid.NewGuid();
        var full = Assert.Single(LmvScriptReply.Read(Reply(o.ToString(), i.ToString(), s =>
        {
            s["Mono"] = OSD.FromBoolean(true);
            s["Engine"] = OSD.FromString("Example.Alpha");
            s["EngineChoice"] = OSD.FromString("Example.Alpha");
        })));
        Assert.Equal((o, i), (full.ObjectId, full.ItemId));
        Assert.Equal(new ScriptRuntime(true, true, false, false, "Example.Alpha", "Example.Alpha"), full.Runtime);

        var old = Assert.Single(LmvScriptReply.Read(Reply(o.ToString(), i.ToString(), s => { s["Mono"] = OSD.FromBoolean(true); })));
        Assert.Equal(new ScriptRuntime(true, true), old.Runtime);

        var lua = Assert.Single(LmvScriptReply.Read(Reply(o.ToString(), i.ToString(), s => { s["Mono"] = OSD.FromBoolean(false); s["Luau"] = OSD.FromBoolean(true); s["LuauLanguage"] = OSD.FromBoolean(true); })));
        Assert.Equal(ScriptTarget.Luau, lua.Runtime.Current);
    }

    [Fact]
    public void Other_events_and_other_answers_are_not_scripts()
    {
        Assert.Empty(LmvScriptReply.Read(new OSDMap { ["events"] = new OSDArray { new OSDMap { ["message"] = OSD.FromString("TeleportFailed"), ["body"] = new OSDMap() } } }));
        // another event that happens to carry a Script list is not a script's running state
        var notScript = Reply(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), _ => { });
        ((OSDMap)((OSDArray)notScript["events"])[0])["message"] = OSD.FromString("SomethingElse");
        Assert.Empty(LmvScriptReply.Read(notScript));
        Assert.Empty(LmvScriptReply.Read(new OSDMap()));
        Assert.Empty(LmvScriptReply.Read(OSD.FromString("x")));
    }

    private sealed class Canned(byte[] answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(answer) });
    }

    private static async Task<(byte[] Seen, List<OSD> Handed)> Poll(OSDMap request, byte[] answer)
    {
        var handed = new List<OSD>();
        var tap = new LmvLoginTap(new Canned(answer), new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test")) { EventQueueAnswer = handed.Add };
        using var client = new HttpClient(tap);
        using var response = await client.PostAsync("http://example.org/eq", new ByteArrayContent(OSDParser.SerializeLLSDXmlBytes(request)));
        return (await response.Content.ReadAsByteArrayAsync(), handed);
    }

    [Fact]
    public async Task The_event_queue_tap_hands_over_a_script_reply_and_the_library_still_gets_the_whole_answer()
    {
        var answer = OSDParser.SerializeLLSDXmlBytes(Reply(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), s => s["Engine"] = OSD.FromString("SLua")));
        var (seen, handed) = await Poll(new OSDMap { ["ack"] = new OSD(), ["done"] = OSD.FromBoolean(false) }, answer);
        Assert.Equal(answer, seen);
        Assert.Single(LmvScriptReply.Read(Assert.Single(handed)));
    }

    [Fact]
    public async Task The_tap_leaves_other_answers_and_other_requests_alone()
    {
        var other = OSDParser.SerializeLLSDXmlBytes(new OSDMap { ["events"] = new OSDArray { new OSDMap { ["message"] = OSD.FromString("TeleportFailed"), ["body"] = new OSDMap() } } });
        var (seen, handed) = await Poll(new OSDMap { ["ack"] = new OSD(), ["done"] = OSD.FromBoolean(false) }, other);
        Assert.Equal(other, seen);
        Assert.Empty(handed);
        // a post that is no event-queue poll is not read at all
        var withReply = OSDParser.SerializeLLSDXmlBytes(Reply(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), _ => { }));
        Assert.Empty((await Poll(new OSDMap { ["item_id"] = OSD.FromString("x") }, withReply)).Handed);
    }

    [Fact]
    public void The_region_reads_its_engines_choice_flags_and_lua_switch()
    {
        var extras = new Dictionary<string, object?>
        {
            ["ScriptEngines"] = new List<string> { "YEngine", "SLua" },
            ["ScriptEngineChoice"] = true,
            ["ScriptEngineChoiceNoMod"] = true,
        };
        var r = ScriptRegion.From(extras, luaEnabled: true);
        Assert.Equal(["YEngine", "SLua"], r.Engines);
        Assert.True(r.LuaEnabled);
        Assert.True(r.ChoiceSaved);
        Assert.True(r.ChoiceNoMod);

        var old = ScriptRegion.From(null, luaEnabled: false);
        Assert.Empty(old.Engines);
        Assert.False(old.ChoiceSaved);
        Assert.False(old.ChoiceNoMod);

        // the list as one comma-separated string, and a flag that is not true
        var text = ScriptRegion.From(new Dictionary<string, object?> { ["ScriptEngines"] = "YEngine, Example.Alpha", ["ScriptEngineChoice"] = "yes" }, false);
        Assert.Equal(["YEngine", "Example.Alpha"], text.Engines);
        Assert.False(text.ChoiceSaved);
    }

    [Fact]
    public void The_extras_reader_keeps_a_list_of_text_as_a_list()
    {
        var map = new OSDMap { ["ScriptEngines"] = new OSDArray { OSD.FromString("YEngine"), OSD.FromString("SLua") }, ["ScriptEngineChoice"] = OSD.FromBoolean(true) };
        var values = LmvGridFeatures.Extras(map)!;
        Assert.Equal(["YEngine", "SLua"], Assert.IsAssignableFrom<IReadOnlyList<string>>(values["ScriptEngines"]));
        Assert.Equal(true, values["ScriptEngineChoice"]);
    }
}
