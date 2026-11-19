// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The script-engine contract as a region builds it: the seed capability request lists SetScriptEngine (so the region sends Engine and
/// EngineChoice), a missing EngineChoice reads as empty, the SetScriptEngine reply in its success and failure forms, and early-access engines
/// labelled in the drop-down with one warning per install. Fake regions and an in-memory settings service only; no shared state, so the
/// class runs in parallel.
/// </summary>
public class ScriptEngineContractTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public byte[] Posted = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Posted = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }

    private static async Task<List<string>> SeedRequestSeenByRegion(params string[] asked)
    {
        var region = new Recorder();
        var tap = new LmvLoginTap(region, new CoreLog(new ListSink(), "test"));
        var names = new OSDArray();
        foreach (var n in asked) names.Add(OSD.FromString(n));
        var content = new ByteArrayContent(OSDParser.SerializeLLSDXmlBytes(names));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/llsd+xml");
        using var invoker = new HttpMessageInvoker(tap);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://region.example.org/seed") { Content = content }, default);
        return ((OSDArray)OSDParser.Deserialize(region.Posted)).Select(o => o.AsString()).ToList();
    }

    // ---------------- item 1: opt-in ----------------

    [Fact]
    public async Task The_seed_capability_request_lists_SetScriptEngine()
    {
        var sent = await SeedRequestSeenByRegion("EventQueueGet", "ViewerAsset");
        Assert.Contains("SetScriptEngine", sent);
        Assert.Contains("EventQueueGet", sent);   // what the library asked for is still asked for
        Assert.Contains("ViewerAsset", sent);
    }

    [Fact]
    public async Task A_seed_request_that_already_lists_it_is_not_given_it_twice()
    {
        var sent = await SeedRequestSeenByRegion("EventQueueGet", "SetScriptEngine");
        Assert.Single(sent, n => n == "SetScriptEngine");
    }

    [Fact]
    public async Task A_list_of_text_that_is_not_a_seed_request_goes_out_as_it_was()
    {
        var sent = await SeedRequestSeenByRegion("alpha", "beta");
        Assert.Equal(["alpha", "beta"], sent);
    }

    // ---------------- item 2: reply details ----------------

    private static OSDMap RunningReply(Action<OSDMap> fill)
    {
        var script = new OSDMap
        {
            ["ObjectID"] = OSD.FromUUID(UUID.Random()), ["ItemID"] = OSD.FromUUID(UUID.Random()), ["Running"] = OSD.FromBoolean(true),
        };
        fill(script);
        var ev = new OSDMap { ["message"] = OSD.FromString("ScriptRunningReply"), ["body"] = new OSDMap { ["Script"] = new OSDArray { script } } };
        return new OSDMap { ["id"] = OSD.FromInteger(1), ["events"] = new OSDArray { ev } };
    }

    [Fact]
    public void A_running_reply_without_EngineChoice_reads_as_empty_and_keeps_the_full_engine_name()
    {
        var read = LmvScriptReply.Read(RunningReply(s => s["Engine"] = OSD.FromString("Example.Alpha"))).Single().Runtime;
        Assert.Equal("", read.EngineChoice);
        Assert.Equal("Example.Alpha", read.Engine);
    }

    [Fact]
    public void The_full_engine_names_of_a_reply_name_entries_of_the_regions_engine_list()
    {
        var region = new ScriptRegion(["YEngine", "Example.Alpha", "SLua"], true, true, false);
        var runtime = LmvScriptReply.Read(RunningReply(s => { s["Engine"] = OSD.FromString("Example.Alpha"); s["EngineChoice"] = OSD.FromString("Example.Alpha"); })).Single().Runtime;
        Assert.Contains(runtime.Engine, region.Engines);
        Assert.Contains(runtime.EngineChoice, region.Engines);
        Assert.Equal(new ScriptEngineWhy("Example.Alpha", "script.engine.why.saved"), ScriptEngineChoices.Why(region, runtime, "default{}"));
    }

    [Fact]
    public void A_set_engine_success_reply_carries_the_state_the_result_and_the_choice()
    {
        var body = new OSDMap { ["state"] = OSD.FromString("complete"), ["result"] = OSD.FromString("Changed"), ["EngineChoice"] = OSD.FromString("Example.Alpha") };
        var reply = LmvScriptEngineReply.Read(body, true);
        Assert.True(reply.Ok);
        Assert.Equal("Changed", reply.Result);
        Assert.Equal("Example.Alpha", reply.EngineChoice);
        Assert.Equal("", reply.Error);
    }

    [Fact]
    public void A_set_engine_success_without_EngineChoice_reads_the_choice_as_empty()
    {
        var body = new OSDMap { ["state"] = OSD.FromString("complete"), ["result"] = OSD.FromString("Unchanged") };
        var reply = LmvScriptEngineReply.Read(body, true);
        Assert.True(reply.Ok);
        Assert.Equal("", reply.EngineChoice);
    }

    [Fact]
    public void A_set_engine_failure_reply_carries_the_result_and_the_error_and_is_not_ok()
    {
        var body = new OSDMap { ["state"] = OSD.FromString("error"), ["result"] = OSD.FromString("UnknownEngine"), ["error"] = OSD.FromString("No engine named Nope.") };
        var reply = LmvScriptEngineReply.Read(body, true);
        Assert.False(reply.Ok);
        Assert.Equal("UnknownEngine", reply.Result);
        Assert.Equal("No engine named Nope.", reply.Error);
    }

    [Fact]
    public void A_reply_that_is_not_a_complete_state_is_not_ok_even_over_a_good_http_status()
    {
        Assert.False(LmvScriptEngineReply.Read(new OSDMap(), true).Ok);
        Assert.False(LmvScriptEngineReply.Read(null, true).Ok);
        Assert.False(LmvScriptEngineReply.Read(new OSDMap { ["state"] = OSD.FromString("complete") }, false).Ok);
    }

    // ---------------- item 3: early access ----------------

    private static readonly string[] Engines = ["YEngine", "Example.Alpha", "SLua"];

    private static ScriptRegion Region(object? early) =>
        ScriptRegion.From(new Dictionary<string, object?> { ["ScriptEngines"] = Engines.ToList(), ["ScriptEngineChoice"] = true, ["ScriptEngineEarlyAccess"] = early }, true);

    [Fact]
    public void The_early_access_list_is_read_as_a_list_or_as_comma_separated_text_and_is_empty_when_absent()
    {
        Assert.Equal(["Example.Alpha"], Region(new List<string> { "Example.Alpha" }).EarlyAccess);
        Assert.Equal(["Example.Alpha", "SLua"], Region("Example.Alpha, SLua").EarlyAccess);
        Assert.Empty(ScriptRegion.From(new Dictionary<string, object?> { ["ScriptEngines"] = Engines.ToList() }, true).EarlyAccess);
        Assert.Empty(ScriptRegion.None.EarlyAccess);
    }

    [Fact]
    public void A_listed_engines_entry_says_early_access_and_the_others_do_not()
    {
        var all = ScriptEngineChoices.OpenSim(Region(new List<string> { "Example.Alpha" }));
        Assert.Equal(["script.engine.lsl", "script.engine.lsl_early", "script.engine.lsl", "script.engine.slua_lsl", "script.engine.slua_lua"], all.Select(c => c.LabelKey));
        Assert.Equal([false, true, false, false, false], all.Select(c => c.EarlyAccess));
        Assert.Equal("Example.Alpha", all[1].LabelArg);
    }

    [Fact]
    public void When_SLua_is_listed_both_lua_entries_say_early_access()
    {
        var all = ScriptEngineChoices.OpenSim(Region(new List<string> { "slua" }));
        Assert.Equal(["script.engine.lsl", "script.engine.lsl", "script.engine.lsl_early", "script.engine.slua_lsl_early", "script.engine.slua_lua_early"], all.Select(c => c.LabelKey));
        Assert.Equal([false, false, true, true, true], all.Select(c => c.EarlyAccess));
    }

    [Fact]
    public void The_early_access_labels_and_the_warning_are_in_the_string_table()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "host", "Viewer.Shell", "Strings", "en.json"));
        foreach (var key in new[] { "script.engine.lsl_early", "script.engine.slua_lsl_early", "script.engine.slua_lua_early" })
            Assert.Matches("\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\": \"[^\"]*early access[^\"]*\"", json);
        Assert.Contains("\"script.engine.early_warning\": \"Early access: this engine may not keep script state across future updates.\"", json);
    }

    private static ScriptChoice Entry(bool early) => new("script.engine.lsl", "X", ScriptTarget.Engine("X"), true, null, "", early);

    [Fact]
    public void The_warning_shows_once_on_the_first_early_access_pick_and_not_for_other_entries()
    {
        var notice = new EarlyAccessNotice(new SettingsService(null));
        Assert.False(notice.TakeWarning(Entry(false)));   // a normal pick neither warns nor uses up the warning
        Assert.True(notice.TakeWarning(Entry(true)));
        Assert.False(notice.TakeWarning(Entry(true)));
        Assert.False(notice.TakeWarning(Entry(false)));
    }

    [Fact]
    public void The_warning_is_kept_once_per_install_across_restarts()
    {
        var store = new MemoryStore();
        Assert.True(new EarlyAccessNotice(new SettingsService(store)).TakeWarning(Entry(true)));
        Assert.False(new EarlyAccessNotice(new SettingsService(store)).TakeWarning(Entry(true)));
    }

    private sealed class MemoryStore : ISettingsStore
    {
        private string? _text;
        public string? Read() => _text;
        public bool Write(string text) { _text = text; return true; }
    }
}
