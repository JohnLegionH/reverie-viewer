// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The script editor: who may open, read or edit a script (in the inventory and inside an object), the compiler's lines from Second Life
/// and OpenSim read for their place, the save's two requests and the answers both grids send, the reset message, the editor window's model
/// (changes, save, the unsaved-changes question, the running state, a click on an error, go to line, find and replace) and the LSL and OSSL
/// words. Pure data; no shared state, so the class runs in parallel.
/// </summary>
public class ScriptEditorTests
{
    private static Guid G(int n) => new(n, 0, 0, new byte[8]);
    private static readonly Guid Agent = G(1), Other = G(2), Item = G(3), Asset = G(4), Box = G(5);
    private const uint Copy = NotecardPermissions.Copy, Modify = NotecardPermissions.Modify, Transfer = NotecardPermissions.Transfer;

    private static ScriptAccess Inv(uint ownerMask, bool library = false, Guid? owner = null, uint everyone = 0) =>
        new(Item, Asset, Agent, new NotecardPermissions(Agent, owner ?? Agent, 0x7fffffff, ownerMask, 0, everyone, 0), library);

    private static ScriptAccess InObj(uint ownerMask) =>
        new(Item, Asset, Agent, new NotecardPermissions(Agent, Agent, 0x7fffffff, ownerMask, 0, 0, 0), false, Box, 77);

    // ---------------- who may open a script ----------------

    [Fact]
    public void A_script_in_the_inventory_follows_the_official_preview_rules()
    {
        Assert.Equal(ScriptView.Editable, ScriptRules.ViewOf(Inv(Copy | Modify | Transfer)));
        Assert.Equal(ScriptView.NotAllowed, ScriptRules.ViewOf(Inv(Modify | Transfer)));           // no copy: not shown
        Assert.Equal(ScriptView.NotAllowed, ScriptRules.ViewOf(Inv(Copy | Transfer)));             // copy without modify, not the library
        Assert.Equal(ScriptView.ReadOnly, ScriptRules.ViewOf(Inv(0, library: true, owner: Other, everyone: Copy)));   // the library: read only
        Assert.Equal(ScriptView.Editable, ScriptRules.ViewOf(Inv(0, owner: Other, everyone: Copy | Modify)));        // someone else's: everyone's rights
    }

    [Fact]
    public void A_script_inside_an_object_needs_copy_and_modify()
    {
        Assert.Equal(ScriptView.Editable, ScriptRules.ViewOf(InObj(Copy | Modify)));
        Assert.Equal(ScriptView.NotAllowed, ScriptRules.ViewOf(InObj(Modify | Transfer)));
        Assert.Equal(ScriptView.NotAllowed, ScriptRules.ViewOf(InObj(Copy | Transfer)));
    }

    [Fact]
    public void A_script_listed_inside_an_object_becomes_an_access_with_the_object_and_its_rights()
    {
        var script = new TaskItem(Item, "New Script", 10, 10, Agent, Copy | Modify, 0, Asset, Agent, 0x7fffffff, 0, 0);
        var a = ScriptEditing.AccessFor(script, Box, 77, Agent);
        Assert.NotNull(a);
        Assert.Equal(new ScriptAccess(Item, Asset, Agent, new NotecardPermissions(Agent, Agent, 0x7fffffff, Copy | Modify, 0, 0, 0), false, Box, 77), a);
        Assert.True(a!.InObject);
        Assert.Null(ScriptEditing.AccessFor(script with { AssetType = 7, InvType = 7 }, Box, 77, Agent));   // a notecard is not a script
    }

    // ---------------- the compiler's lines ----------------

    [Fact]
    public void Compiler_lines_from_second_life_and_opensim_name_their_line_and_column_from_zero()
    {
        Assert.Equal(new ScriptMessage("(12, 4) : ERROR : Syntax error", 12, 4), ScriptMessage.Parse("(12, 4) : ERROR : Syntax error"));
        Assert.Equal(new ScriptMessage("(3,10) Error: undefined name foo", 3, 10), ScriptMessage.Parse("(3,10) Error: undefined name foo"));
        Assert.Equal(new ScriptMessage("(0,0) Error compiling, see exception in log", 0, 0), ScriptMessage.Parse("(0,0) Error compiling, see exception in log"));
        Assert.Equal(new ScriptMessage("Compile error", -1, -1), ScriptMessage.Parse("Compile error"));
        Assert.Equal(new ScriptMessage("(1, 2) : ERROR : x", 1, 2), ScriptMessage.Parse("(1, 2) : ERROR : x\r\n"));   // non-printing characters go
    }

    // ---------------- the save's requests and answers ----------------

    [Fact]
    public void The_inventory_save_asks_with_the_item_and_the_target_and_the_object_save_adds_the_object_and_running()
    {
        var inv = LmvTransport.ScriptUploadBody(Inv(Copy | Modify), ScriptTarget.Mono, running: true);
        Assert.Equal(Item, inv["item_id"].AsUUID().Guid);
        Assert.Equal("mono", inv["target"].AsString());
        Assert.False(inv.ContainsKey("task_id"));
        Assert.False(inv.ContainsKey("is_script_running"));
        var obj = LmvTransport.ScriptUploadBody(InObj(Copy | Modify), ScriptTarget.Lsl2, running: false);
        Assert.Equal(Item, obj["item_id"].AsUUID().Guid);
        Assert.Equal(Box, obj["task_id"].AsUUID().Guid);
        Assert.False(obj["is_script_running"].AsBoolean());
        Assert.True(obj.ContainsKey("is_script_running"));
        Assert.Equal("lsl2", obj["target"].AsString());
    }

    [Fact]
    public void The_first_answer_names_the_uploader()
    {
        Assert.Equal(new Uri("http://example.org/upload/1"), LmvTransport.ScriptUploader(new OSDMap { ["uploader"] = "http://example.org/upload/1", ["state"] = "upload" }));
        Assert.Null(LmvTransport.ScriptUploader(new OSDMap { ["state"] = "upload" }));
    }

    [Fact]
    public void A_second_life_answer_that_compiled_is_saved_with_its_new_asset()
    {
        var r = LmvTransport.ParseScriptAnswer(new OSDMap { ["state"] = "complete", ["compiled"] = true, ["errors"] = new OSDArray(), ["new_asset"] = OSD.FromUUID(new UUID(Asset)) });
        Assert.NotNull(r);
        Assert.Equal(ScriptSaveStatus.Saved, r.Status);
        Assert.True(r.Compiled);
        Assert.Equal(Asset, r.NewAssetId);
        Assert.Empty(r.Messages);
    }

    [Fact]
    public void A_second_life_answer_with_errors_lists_them_with_their_places()
    {
        var r = LmvTransport.ParseScriptAnswer(new OSDMap
        {
            ["state"] = "complete", ["compiled"] = false, ["new_asset"] = OSD.FromUUID(new UUID(Asset)),
            ["errors"] = new OSDArray { "(4, 8) : ERROR : Syntax error", "(6, 0) : ERROR : Name not defined within scope" },
        });
        Assert.NotNull(r);
        Assert.Equal(ScriptSaveStatus.Saved, r.Status);
        Assert.False(r.Compiled);
        Assert.Equal([(4, 8), (6, 0)], r.Messages.Select(m => (m.Line, m.Column)).ToArray());
    }

    [Fact]
    public void An_opensim_inventory_answer_saves_without_saying_anything_about_compiling()
    {
        // OpenSim's UpdateScriptAgent answer: new_asset, new_inventory_item and state, no "compiled" and no "errors"
        var r = LmvTransport.ParseScriptAnswer(new OSDMap { ["new_asset"] = OSD.FromUUID(new UUID(Asset)), ["new_inventory_item"] = OSD.FromUUID(new UUID(Item)), ["state"] = "complete" });
        Assert.NotNull(r);
        Assert.Equal(ScriptSaveStatus.Saved, r.Status);
        Assert.Null(r.Compiled);
        Assert.Equal(Asset, r.NewAssetId);
    }

    [Fact]
    public void An_opensim_object_answer_with_errors_lists_the_engines_lines()
    {
        var r = LmvTransport.ParseScriptAnswer(new OSDMap
        {
            ["new_asset"] = OSD.FromUUID(new UUID(Item)), ["compiled"] = false, ["state"] = "complete",
            ["errors"] = new OSDArray { "(2,5) Error: syntax error, unexpected ';'" },
        });
        Assert.NotNull(r);
        Assert.False(r.Compiled);
        Assert.Equal(new ScriptMessage("(2,5) Error: syntax error, unexpected ';'", 2, 5), Assert.Single(r.Messages));
    }

    [Fact]
    public void An_answer_that_is_not_complete_is_a_failure_with_its_message()
    {
        var r = LmvTransport.ParseScriptAnswer(new OSDMap { ["message"] = "Failed to update inventory item asset", ["identifier"] = OSD.FromUUID(new UUID(Item)) });
        Assert.NotNull(r);
        Assert.Equal(ScriptSaveStatus.Failed, r.Status);
        Assert.Equal("Failed to update inventory item asset", r.Reason);
    }

    [Fact]
    public void Reset_names_the_object_and_the_script()
    {
        var p = LmvTransport.ScriptReset(new UUID(Agent), new UUID(Other), Box, Item);
        Assert.Equal(Agent, p.AgentData.AgentID.Guid);
        Assert.Equal(Other, p.AgentData.SessionID.Guid);
        Assert.Equal(Box, p.Script.ObjectID.Guid);
        Assert.Equal(Item, p.Script.ItemID.Guid);
    }

    // ---------------- the editor window's model ----------------

    private static ScriptEditing Opened(ScriptAccess access, string text)
    {
        var e = new ScriptEditing(access, "Test script");
        e.TextFetched(AssetFetchResult.Ok(Encoding.UTF8.GetBytes(text)));
        return e;
    }

    [Fact]
    public void An_editable_script_reads_its_text_counts_changes_and_saves()
    {
        var e = Opened(Inv(Copy | Modify), "default\n{\n}\n");
        Assert.True(e.Loaded);
        Assert.Equal("default\n{\n}\n", e.Text);
        Assert.True(e.CanEdit);
        Assert.False(e.IsDirty);
        Assert.False(e.CanSave);
        e.Text = "default\n{\n    state_entry() { llSay(0, \"hi\"); }\n}\n";
        Assert.True(e.IsDirty);
        Assert.True(e.CanSave);
        var bytes = e.BeginSave();
        Assert.Equal(Encoding.UTF8.GetBytes(e.Text), bytes);
        Assert.True(e.Saving);
        Assert.False(e.CanSave);
        e.SaveFinished(new ScriptSaveResult(ScriptSaveStatus.Saved, true, G(9), []));
        Assert.False(e.Saving);
        Assert.False(e.IsDirty);
        Assert.Equal("script.save_complete", e.StatusKey);
        Assert.Equal(G(9), e.Access.AssetId);
    }

    [Fact]
    public void A_save_with_errors_lists_them_and_an_opensim_inventory_save_just_says_saved()
    {
        var e = Opened(InObj(Copy | Modify), "x");
        e.Text = "y";
        e.BeginSave();
        e.SaveFinished(new ScriptSaveResult(ScriptSaveStatus.Saved, false, G(9), [ScriptMessage.Parse("(0, 0) : ERROR : Syntax error")]));
        Assert.Single(e.Messages);
        Assert.Null(e.StatusKey);
        Assert.False(e.IsDirty);   // the grid keeps the text even when it does not compile
        var o = Opened(Inv(Copy | Modify), "x");
        o.Text = "y";
        o.BeginSave();
        o.SaveFinished(new ScriptSaveResult(ScriptSaveStatus.Saved, null, G(9), []));
        Assert.Equal("script.save_complete", o.StatusKey);
        Assert.Empty(o.Messages);
    }

    [Fact]
    public void A_failed_upload_keeps_the_changes_and_says_why()
    {
        var e = Opened(Inv(Copy | Modify), "x");
        e.Text = "y";
        e.BeginSave();
        e.SaveFinished(ScriptSaveResult.Of(ScriptSaveStatus.Failed, "no_answer"));
        Assert.True(e.IsDirty);
        Assert.True(e.CanSave);
        Assert.Equal("script.upload_failed", e.StatusKey);
        Assert.Equal("no_answer", e.StatusArg);
    }

    [Fact]
    public void A_script_that_may_not_be_modified_opens_read_only_and_one_that_may_not_be_copied_says_so()
    {
        var ro = Opened(Inv(0, library: true, owner: Other, everyone: Copy), "x");
        Assert.True(ro.Loaded);
        Assert.Equal(ScriptView.ReadOnly, ro.View);
        Assert.False(ro.CanEdit);
        ro.Text = "changed";
        Assert.False(ro.IsDirty);
        Assert.False(ro.CanSave);
        Assert.Null(ro.BeginSave());
        Assert.Equal("script.read_only", ro.StatusKey);
        var no = new ScriptEditing(InObj(Modify), "Test script");
        Assert.Equal(ScriptView.NotAllowed, no.View);
        Assert.Equal("script.not_allowed", no.StatusKey);
        Assert.False(no.CanEdit);
        Assert.Null(no.BeginSave());
    }

    [Fact]
    public void A_fetch_that_fails_says_why_as_the_official_viewer_does()
    {
        var d = new ScriptEditing(Inv(Copy | Modify), "s");
        d.TextFetched(AssetFetchResult.Of(AssetFetchStatus.Denied));
        Assert.Equal("script.no_permission", d.StatusKey);
        Assert.False(d.CanEdit);
        var m = new ScriptEditing(Inv(Copy | Modify), "s");
        m.TextFetched(AssetFetchResult.Of(AssetFetchStatus.NotFound));
        Assert.Equal("script.missing", m.StatusKey);
        var f = new ScriptEditing(Inv(Copy | Modify), "s");
        f.TextFetched(AssetFetchResult.Of(AssetFetchStatus.Failed));
        Assert.Equal("script.unable_to_load", f.StatusKey);
        var n = new ScriptEditing(Inv(Copy | Modify), "s");
        n.LoadedEmpty();
        Assert.True(n.Loaded);
        Assert.True(n.CanEdit);
    }

    [Fact]
    public void Closing_with_changes_asks_and_each_answer_does_what_the_official_question_does()
    {
        var clean = Opened(Inv(Copy | Modify), "x");
        Assert.True(clean.MayClose());
        var e = Opened(Inv(Copy | Modify), "x");
        e.Text = "y";
        Assert.False(e.MayClose());
        Assert.Equal(ScriptCloseStep.Stay, e.CloseAnswered(ScriptCloseAnswer.Cancel));
        Assert.True(e.IsDirty);
        Assert.Equal(ScriptCloseStep.Close, e.CloseAnswered(ScriptCloseAnswer.DontSave));
        Assert.Equal(ScriptCloseStep.SaveThenClose, e.CloseAnswered(ScriptCloseAnswer.Save));
        Assert.True(e.Saving);
        Assert.False(e.CloseAfterSave);
        e.SaveFinished(new ScriptSaveResult(ScriptSaveStatus.Saved, true, G(9), []));
        Assert.True(e.CloseAfterSave);
        var failed = Opened(Inv(Copy | Modify), "x");
        failed.Text = "y";
        failed.CloseAnswered(ScriptCloseAnswer.Save);
        failed.SaveFinished(ScriptSaveResult.Of(ScriptSaveStatus.Failed, "x"));
        Assert.False(failed.CloseAfterSave);   // a save that failed keeps the window and the changes
    }

    [Fact]
    public void The_running_tick_waits_for_the_regions_answer_and_is_not_used_while_saving()
    {
        var e = Opened(InObj(Copy | Modify), "x");
        Assert.False(e.CanSetRunning);
        e.RunningKnown(running: true, mono: false);
        Assert.True(e.Running);
        Assert.Equal(ScriptTarget.Lsl2, e.Target);
        Assert.True(e.CanSetRunning);
        Assert.True(e.SetRunning(false));
        Assert.False(e.Running);
        e.Text = "y";
        e.BeginSave();
        Assert.False(e.CanSetRunning);
        var inv = Opened(Inv(Copy | Modify), "x");
        inv.RunningKnown(true, true);
        Assert.False(inv.CanSetRunning);   // a script in the inventory does not run
        Assert.False(inv.SetRunning(true));
    }

    [Fact]
    public void A_click_on_an_error_puts_the_caret_on_its_line_and_column_kept_inside_the_text()
    {
        var e = Opened(InObj(Copy | Modify), "default\n{\n    state_entry()\n}\n");
        e.Text = "default\n{\n    state_entry()\n}";
        e.BeginSave();
        e.SaveFinished(new ScriptSaveResult(ScriptSaveStatus.Saved, false, G(9),
            [ScriptMessage.Parse("(2, 4) : ERROR : x"), ScriptMessage.Parse("(40, 2) : ERROR : y"), ScriptMessage.Parse("(1, 30) : ERROR : z"), ScriptMessage.Parse("no place")]));
        Assert.Equal((2, 4), e.CaretFor(0));
        Assert.Equal((3, 0), e.CaretFor(1));    // past the end: the last line
        Assert.Equal((1, 1), e.CaretFor(2));    // past the line's end: its end
        Assert.Null(e.CaretFor(3));
        Assert.Null(e.CaretFor(9));
    }

    [Fact]
    public void Go_to_line_takes_the_number_shown_beside_the_line()
    {
        var e = Opened(Inv(Copy | Modify), "a\nb\nc\nd");
        Assert.Equal(2, e.GoToLine("3"));
        Assert.Equal(0, e.GoToLine("1"));
        Assert.Equal(3, e.GoToLine("99"));
        Assert.Equal(0, e.GoToLine("0"));
        Assert.Null(e.GoToLine("three"));
    }

    [Fact]
    public void Find_goes_on_from_the_caret_and_round_and_replace_all_counts()
    {
        const string text = "llSay(0, \"a\");\nLLSAY(0, \"b\");\nllSay(0, \"c\");";
        Assert.Equal(0, ScriptEditing.FindNext(text, "llsay", 0, matchCase: false));
        Assert.Equal(15, ScriptEditing.FindNext(text, "llsay", 1, matchCase: false));
        Assert.Equal(30, ScriptEditing.FindNext(text, "llSay", 1, matchCase: true));
        Assert.Equal(0, ScriptEditing.FindNext(text, "llSay", 31, matchCase: true));   // round to the start
        Assert.Equal(-1, ScriptEditing.FindNext(text, "llOwnerSay", 0, matchCase: false));
        var (all, n) = ScriptEditing.ReplaceAll(text, "llsay", "llWhisper", matchCase: false);
        Assert.Equal(3, n);
        Assert.Equal("llWhisper(0, \"a\");\nllWhisper(0, \"b\");\nllWhisper(0, \"c\");", all);
        Assert.Equal(2, ScriptEditing.ReplaceAll(text, "llSay", "x", matchCase: true).Count);
    }

    // ---------------- the words ----------------

    [Fact]
    public void The_shipped_lsl_words_have_their_kinds_and_tooltips()
    {
        var k = LslKeywords.Shipped(openSim: false);
        Assert.Equal(LslWordKind.Function, k.Find("llSay")?.Kind);
        Assert.Contains("channel", k.Find("llSay")?.Tooltip ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LslWordKind.Event, k.Find("state_entry")?.Kind);
        Assert.Equal(LslWordKind.Type, k.Find("integer")?.Kind);
        Assert.Equal(LslWordKind.Constant, k.Find("PI")?.Kind);
        Assert.Equal(LslWordKind.Control, k.Find("if")?.Kind);
        Assert.Equal(LslWordKind.Deprecated, k.Find("ATTACH_LPEC")?.Kind);
        Assert.Null(k.Find("osGetNotecard"));
        Assert.True(k.Words.Count > 1000);
    }

    [Fact]
    public void On_opensim_the_ossl_words_are_added()
    {
        var k = LslKeywords.Shipped(openSim: true);
        Assert.Equal(LslWordKind.Function, k.Find("osGetNotecard")?.Kind);
        Assert.True(k.Find("osGetNotecard")?.Ossl);
        Assert.Equal(LslWordKind.Constant, k.Find("OS_ATTACH_MSG_ALL")?.Kind);
        Assert.Equal(LslWordKind.Function, k.Find("llSay")?.Kind);
        Assert.False(k.Find("llSay")?.Ossl);
    }
}
