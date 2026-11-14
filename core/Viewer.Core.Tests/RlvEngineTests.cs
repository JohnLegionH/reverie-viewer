// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addon.Rlv;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The RLV add-on's engine: parsing as RLVa parses, restrictions per object, @clear, garbage collection, queries and
/// @notify, and the version replies.</summary>
public class RlvEngineTests
{
    private static readonly Guid Collar = Guid.Parse("3a1f0c2e-7b4d-4e8a-9c6f-1d2e3f405162");
    private static readonly Guid Cuffs = Guid.Parse("3a1f0c2e-7b4d-4e8a-9c6f-1d2e3f405163");
    private static readonly Guid Chair = Guid.Parse("3a1f0c2e-7b4d-4e8a-9c6f-1d2e3f405164");

    private sealed class Sink : IRlvSink
    {
        public readonly List<(int Channel, string Text)> Replies = [];
        public readonly List<string> Forced = [];
        public readonly HashSet<Guid> Present = [];
        public int Changes;
        public bool Refuse;
        public void Reply(int channel, string text) => Replies.Add((channel, text));
        public bool ForceSit(Guid issuer, Guid objectId) { Forced.Add($"sit {objectId} by {issuer}"); return !Refuse; }
        public bool ForceStand(Guid issuer) { Forced.Add($"stand by {issuer}"); return !Refuse; }
        public bool ForceTeleport(Guid issuer, double x, double y, double z) { Forced.Add($"tp {x}/{y}/{z} by {issuer}"); return !Refuse; }
        public bool ObjectPresent(Guid objectId) => Present.Contains(objectId);
        public void Changed() => Changes++;
    }

    private static (RlvEngine Engine, Sink Sink) NewEngine()
    {
        var sink = new Sink();
        sink.Present.Add(Collar);
        sink.Present.Add(Cuffs);
        return (new RlvEngine(sink, "Example Viewer", new Version(1, 0, 0)), sink);
    }

    private static string[] Held(RlvEngine e, Guid? obj = null) =>
        e.Restrictions.Where(r => obj is null || r.ObjectId == obj).Select(r => r.Option.Length > 0 ? r.Behaviour + ":" + r.Option : r.Behaviour).ToArray();

    // ---------------- parsing ----------------

    [Theory]
    [InlineData("sit=n", "sit", "", "n", RlvParamType.Add)]
    [InlineData("sit=add", "sit", "", "add", RlvParamType.Add)]
    [InlineData("sit=y", "sit", "", "y", RlvParamType.Remove)]
    [InlineData("sit=rem", "sit", "", "rem", RlvParamType.Remove)]
    [InlineData("sendim:00000000-0000-0000-0000-000000000001=add", "sendim", "00000000-0000-0000-0000-000000000001", "add", RlvParamType.Add)]
    [InlineData("unsit=force", "unsit", "", "force", RlvParamType.Force)]
    [InlineData("tpto:1/2/3=force", "tpto", "1/2/3", "force", RlvParamType.Force)]
    [InlineData("version=2222", "version", "", "2222", RlvParamType.Reply)]
    [InlineData("getstatus:sit;|=-5", "getstatus", "sit;|", "-5", RlvParamType.Reply)]
    [InlineData("clear", "clear", "", "", RlvParamType.Clear)]
    [InlineData("clear=tp", "clear", "", "tp", RlvParamType.Clear)]
    [InlineData("detach:=n", "detach", "", "n", RlvParamType.Add)]
    public void A_command_is_read_as_RLVa_reads_it(string text, string behaviour, string option, string param, RlvParamType type)
    {
        var c = RlvCommand.Parse(text);
        Assert.NotNull(c);
        Assert.Equal((behaviour, option, param, type), (c!.Behaviour, c.Option, c.Param, c.Type));
    }

    [Theory]
    [InlineData("=n")]
    [InlineData(":x=n")]
    [InlineData("sit")]
    [InlineData("sit=")]
    [InlineData("sit=maybe")]
    [InlineData("")]
    public void A_badly_formed_command_is_not_a_command(string text) => Assert.Null(RlvCommand.Parse(text));

    [Fact]
    public void An_owner_say_line_is_split_at_commas_in_lower_case_with_empty_parts_dropped()
    {
        Assert.Equal(["sit=n", "tplm=n", " unsit=n"], RlvCommand.Split("@SIT=n,,TpLm=N, unsit=n,"));
        Assert.Null(RlvCommand.Split("sit=n"));      // no '@'
        Assert.Null(RlvCommand.Split("@a="));        // three characters or fewer
        Assert.Null(RlvCommand.Split("hello"));
    }

    [Fact]
    public void A_line_with_a_bad_command_runs_the_good_ones_and_ignores_the_bad_ones()
    {
        var (e, _) = NewEngine();
        var results = e.Process(Collar, "Collar", "@sit=n,nonsense,unsit=maybe, tplm=n,tploc=n", 1);
        Assert.Equal([RlvResult.Success, RlvResult.FailedSyntax, RlvResult.FailedSyntax, RlvResult.FailedUnknown, RlvResult.Success], results);
        Assert.Equal(["sit", "tploc"], Held(e));
    }

    // ---------------- per object ----------------

    [Fact]
    public void Restrictions_are_kept_per_object_and_listed_with_their_name_and_time()
    {
        var (e, sink) = NewEngine();
        e.Process(Collar, "Collar", "@sit=n,sendim=n", 5);
        e.Process(Cuffs, "Cuffs", "@sit=n,detach=n", 7);
        Assert.Equal(["sit", "sendim"], Held(e, Collar));
        Assert.Equal(["sit", "detach"], Held(e, Cuffs));
        var detach = e.Restrictions.Single(r => r.ObjectId == Cuffs && r.Behaviour == "detach");
        Assert.Equal(("Cuffs", 7.0, true), (detach.ObjectName, detach.SetAt, detach.Enforced));
        Assert.True(sink.Changes >= 2);

        Assert.Equal([RlvResult.SuccessDuplicate], e.Process(Collar, "Collar", "@sit=n", 9));
        Assert.Equal([RlvResult.Success], e.Process(Collar, "Collar", "@sit=y", 9));
        Assert.Equal([RlvResult.SuccessUnset], e.Process(Collar, "Collar", "@sit=y", 9));
        Assert.Equal(["sendim"], Held(e, Collar));
        Assert.Equal(["sit", "detach"], Held(e, Cuffs));   // the other object's sit still holds
    }

    [Fact]
    public void A_known_behaviour_that_is_not_built_yet_is_accepted_and_listed_as_not_enforced()
    {
        var (e, _) = NewEngine();
        Assert.Equal([RlvResult.Success], e.Process(Collar, "Collar", "@fly=n", 1));
        var fly = Assert.Single(e.Restrictions);
        Assert.Equal(("fly", false), (fly.Behaviour, fly.Enforced));
        Assert.True(RlvEngine.IsKnown("fly"));
        Assert.False(RlvEngine.IsEnforced("fly"));
        Assert.True(RlvEngine.IsEnforced("sendchat"));
        Assert.True(RlvEngine.IsEnforced("recvchat_sec"));
        Assert.False(RlvEngine.IsKnown("teleportanywhere"));
    }

    [Fact]
    public void Clear_drops_every_restriction_of_the_object_or_those_matching_its_filter()
    {
        var (e, _) = NewEngine();
        e.Process(Collar, "Collar", "@tplm=n,tploc=n,sit=n", 1);
        e.Process(Cuffs, "Cuffs", "@tplm=n", 1);
        e.Process(Collar, "Collar", "@clear=tp", 2);
        Assert.Equal(["sit"], Held(e, Collar));
        Assert.Equal(["tplm"], Held(e, Cuffs));
        e.Process(Collar, "Collar", "@clear", 3);
        Assert.Empty(Held(e, Collar));
        Assert.Equal(["tplm"], Held(e, Cuffs));
    }

    // ---------------- garbage collection ----------------

    [Fact]
    public void An_object_that_was_seen_and_is_gone_loses_its_restrictions_at_the_next_collection()
    {
        var (e, sink) = NewEngine();
        e.Process(Collar, "Collar", "@detach=n,sit=n", 1);
        e.Process(Cuffs, "Cuffs", "@tplm=n", 1);
        e.Collect(31);
        Assert.Equal(3, e.Restrictions.Count);
        sink.Present.Remove(Collar);   // detached, deleted or gone from the region
        e.Collect(62);
        Assert.Empty(Held(e, Collar));
        Assert.Equal(["tplm"], Held(e, Cuffs));
    }

    [Fact]
    public void An_object_never_seen_is_kept_for_twenty_collections_before_it_goes()
    {
        var (e, _) = NewEngine();
        e.Process(Chair, "Chair", "@unsit=n", 0);   // never present in the object list
        for (int i = 1; i <= 20; i++)
            e.Collect(i * RlvEngine.CollectSeconds);
        Assert.Equal(["unsit"], Held(e));
        e.Collect(21 * RlvEngine.CollectSeconds);
        Assert.Empty(e.Restrictions);
    }

    [Fact]
    public void Collection_runs_at_most_every_thirty_seconds()
    {
        var (e, sink) = NewEngine();
        e.Process(Collar, "Collar", "@sit=n", 0);
        e.Collect(30);
        sink.Present.Remove(Collar);
        e.Collect(40);
        Assert.Single(e.Restrictions);
        e.Collect(60);
        Assert.Empty(e.Restrictions);
    }

    // ---------------- queries ----------------

    [Fact]
    public void Version_replies_name_this_viewer_honestly_in_RLVas_format()
    {
        var (e, sink) = NewEngine();
        e.Process(Collar, "Collar", "@version=1001,versionnew=1002,versionnum=1003,versionnum:impl=1004,versionnumbl=1005", 1);
        Assert.Equal(
        [
            (1001, "RestrainedLife viewer v3.4.3 (Example Viewer RLV 1.0.0)"),
            (1002, "RestrainedLove viewer v3.4.3 (Example Viewer RLV 1.0.0)"),
            (1003, "3040300"),
            (1004, "1000000"),
            (1005, "3040300"),
        ], sink.Replies);
        Assert.DoesNotContain(sink.Replies, r => r.Text.Contains("RLVa") || r.Text.Contains("Firestorm"));
    }

    [Fact]
    public void Getstatus_lists_the_objects_own_restrictions_and_getstatusall_everyones_with_filter_and_separator()
    {
        var (e, sink) = NewEngine();
        e.Process(Collar, "Collar", "@sit=n,tplm=n,sendim:00000000-0000-0000-0000-000000000001=add", 1);
        e.Process(Cuffs, "Cuffs", "@detach=n", 1);
        e.Process(Collar, "Collar", "@getstatus=1,getstatus:tp=2,getstatus:;|=3,getstatusall=4", 2);
        Assert.Equal(
        [
            (1, "/sit/tplm/sendim:00000000-0000-0000-0000-000000000001"),
            (2, "/tplm"),
            (3, "|sit|tplm|sendim:00000000-0000-0000-0000-000000000001"),
            (4, "/sit/tplm/sendim:00000000-0000-0000-0000-000000000001/detach"),
        ], sink.Replies);
    }

    [Fact]
    public void A_reply_on_channel_zero_is_not_sent()
    {
        var (e, sink) = NewEngine();
        Assert.Equal([RlvResult.FailedParam], e.Process(Collar, "Collar", "@version=0", 1));
        Assert.Empty(sink.Replies);
    }

    [Fact]
    public void Notify_reports_each_change_on_its_channel_with_its_filter()
    {
        var (e, sink) = NewEngine();
        e.Process(Collar, "Collar", "@notify:2000=add,notify:3000;tp=add", 1);
        sink.Replies.Clear();
        e.Process(Cuffs, "Cuffs", "@sit=n,tplm=n,tplm=y", 2);
        Assert.Equal(
        [
            (2000, "/sit=n"),
            (2000, "/tplm=n"),
            (3000, "/tplm=n"),
            (2000, "/tplm=y"),
            (3000, "/tplm=y"),
        ], sink.Replies);
    }

    // ---------------- forced actions ----------------

    [Fact]
    public void Force_commands_go_to_the_viewer_with_the_issuing_object()
    {
        var (e, sink) = NewEngine();
        var r = e.Process(Collar, "Collar", $"@sit:{Chair}=force,unsit=force,tpto:256/512/30=force,tpto:bad=force", 1);
        Assert.Equal([RlvResult.Success, RlvResult.Success, RlvResult.Success, RlvResult.FailedOption], r);
        Assert.Equal([$"sit {Chair} by {Collar}", $"stand by {Collar}", $"tp 256/512/30 by {Collar}"], sink.Forced);
        Assert.Empty(e.Restrictions);   // a force command holds nothing
    }

    [Fact]
    public void ClearAll_drops_everything()
    {
        var (e, _) = NewEngine();
        e.Process(Collar, "Collar", "@sit=n", 1);
        e.ClearAll();
        Assert.Empty(e.Restrictions);
    }
}
