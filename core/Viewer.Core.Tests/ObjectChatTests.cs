// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Vector3 = System.Numerics.Vector3;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Object chat and object instant messages in nearby chat: owner-only and direct marks, script errors, the object's name and where it
/// is, instant messages that open no conversation, and the world map showing a place a script named. Every member builds its own
/// objects, so the class runs in parallel.
/// </summary>
public class ObjectChatTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Obj = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");
    private static readonly Guid Owner = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000b2");
    private static readonly DateTimeOffset T0 = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);

    private static NearbyChat Chat() =>
        new(Self, _ => new ActionResult(ActionStatus.Sent, 1), () => 500, () => true, () => T0);

    private static ChatReceived Say(ChatLineType type, string text, ChatSource source = ChatSource.Object, Guid? owner = null, string name = "Thing") =>
        new(null, Obj, owner ?? Owner, name, source, type, ChatAudible.Fully, Vector3.Zero, new MessageBody(text, MessageChannel.LocalChat));

    // ---------------- chat marks ----------------

    [Fact]
    public void Owner_say_is_marked_owner_only_and_keeps_the_object_and_its_owner()
    {
        var c = Chat();
        c.Receive(Say(ChatLineType.OwnerSay, "only you hear this"));
        var line = Assert.Single(c.Lines);
        Assert.Equal(ChatLineStyle.Object, line.Style);
        Assert.Equal(ChatMark.OwnerOnly, line.Mark);
        Assert.Equal("Thing", line.Name);
        Assert.Equal(Obj, line.ObjectId);
        Assert.Equal(Owner, line.OwnerId);
        Assert.Equal("only you hear this", line.Text);
    }

    [Fact]
    public void Region_say_to_is_marked_direct_a_script_error_as_one_and_ordinary_object_speech_is_unmarked()
    {
        var c = Chat();
        c.Receive(Say(ChatLineType.RegionSayTo, "for you alone"));
        c.Receive(Say(ChatLineType.Debug, "Script run-time error"));
        c.Receive(Say(ChatLineType.Say, "hello"));
        Assert.Equal(new[] { ChatMark.Direct, ChatMark.ScriptError, ChatMark.None }, c.Lines.Select(l => l.Mark).ToArray());
    }

    [Fact]
    public void A_direct_line_that_starts_with_an_at_sign_is_for_scripts_and_is_not_shown()
    {
        var c = Chat();
        c.Receive(Say(ChatLineType.RegionSayTo, "@getaccess"));
        Assert.Empty(c.Lines);
        c.Receive(Say(ChatLineType.RegionSay, "@not direct, shown"));
        Assert.Single(c.Lines);
    }

    [Fact]
    public void An_agents_speech_has_no_object_and_no_mark()
    {
        var c = Chat();
        c.Receive(new ChatReceived(null, Owner, Owner, "Some Person", ChatSource.Agent, ChatLineType.Say, ChatAudible.Fully, Vector3.Zero, new MessageBody("hi", MessageChannel.LocalChat)));
        var line = Assert.Single(c.Lines);
        Assert.Equal(ChatMark.None, line.Mark);
        Assert.Equal(Guid.Empty, line.ObjectId);
    }

    [Fact]
    public void An_object_that_spoke_can_be_found_by_its_id_with_its_name_and_owner()
    {
        var c = Chat();
        c.Receive(Say(ChatLineType.OwnerSay, "x"));
        var info = c.Objects.Find(Obj);
        Assert.NotNull(info);
        Assert.Equal("Thing", info!.Name);
        Assert.Equal(Owner, info.OwnerId);
        Assert.Null(c.Objects.Find(Guid.NewGuid()));
    }

    [Fact]
    public void The_object_directory_is_bounded_and_forgets_the_oldest()
    {
        var d = new Viewer.Shell.Notifications.ObjectDirectory();
        var first = Guid.NewGuid();
        d.Remember(new Viewer.Shell.Notifications.ObjectInfo(first, Owner, "first", false, ""));
        for (int i = 0; i < Viewer.Shell.Notifications.ObjectDirectory.Capacity; i++)
            d.Remember(new Viewer.Shell.Notifications.ObjectInfo(Guid.NewGuid(), Owner, "n" + i, false, ""));
        Assert.Equal(Viewer.Shell.Notifications.ObjectDirectory.Capacity, d.Count);
        Assert.Null(d.Find(first));
    }

    // ---------------- object instant messages ----------------

    private static ObjectInstantMessage ObjIm(string text = "hello owner", string place = "Far Region/12/34/56", bool system = false) =>
        new(system ? Guid.Empty : Obj, Owner, "Thing", false, place, system, new MessageBody(text, MessageChannel.InstantMessage));

    [Fact]
    public void An_object_instant_message_is_a_line_in_nearby_chat_with_its_name_place_and_owner()
    {
        var c = Chat();
        c.ReceiveObjectMessage(ObjIm());
        var line = Assert.Single(c.Lines);
        Assert.Equal(ChatLineStyle.Object, line.Style);
        Assert.Equal(ChatMark.ObjectMessage, line.Mark);
        Assert.Equal("Thing", line.Name);
        Assert.Equal("hello owner", line.Text);
        Assert.Equal("Far Region/12/34/56", line.Place);
        Assert.Equal(Obj, line.ObjectId);
        Assert.Equal(Owner, line.OwnerId);
        Assert.Equal("Far Region/12/34/56", c.Objects.Find(Obj)!.Place);
    }

    [Fact]
    public void The_grids_own_message_is_a_system_line_with_no_object()
    {
        var c = Chat();
        c.ReceiveObjectMessage(ObjIm("The region restarts", "", system: true));
        var line = Assert.Single(c.Lines);
        Assert.Equal(ChatLineStyle.System, line.Style);
        Assert.Equal(Guid.Empty, line.ObjectId);
        Assert.Equal(0, c.Objects.Count);
    }

    [Fact]
    public void An_object_message_opens_no_conversation_when_it_comes_through_the_hub()
    {
        var hub = new CommsHub(Self, new NameCache(), _ => new ActionResult(ActionStatus.Sent, 1), new SettingsService(null), () => T0);
        hub.Enqueue(ObjIm());
        hub.Pump();
        Assert.Single(hub.Nearby.Lines);
        Assert.Empty(hub.Ims.Open);
    }

    // ---------------- how the lines are drawn ----------------

    private static string Draw(ChatLine line) => ChatText.Format(line, ThemeSpec.Default, StringTable.English(), showTime: false);

    private static ChatLine ObjectLine(ChatMark mark, string name = "Thing", string text = "words") =>
        new(T0, ChatLineStyle.Object, Obj, name, text, ObjectId: Obj, OwnerId: Owner, Mark: mark);

    [Fact]
    public void An_object_lines_name_is_a_link_to_the_object_and_the_mark_is_in_words()
    {
        string owner = Draw(ObjectLine(ChatMark.OwnerOnly));
        Assert.Contains($"[url=object:{Obj:D}]Thing[/url]", owner);
        Assert.Contains("(owner only)", owner);
        Assert.Contains("(to you)", Draw(ObjectLine(ChatMark.Direct)));
        Assert.Contains("(script error)", Draw(ObjectLine(ChatMark.ScriptError)));
        Assert.Contains("(message)", Draw(ObjectLine(ChatMark.ObjectMessage)));
        Assert.DoesNotContain("(", Draw(ObjectLine(ChatMark.None)).Replace("[url=object:", ""));
    }

    [Fact]
    public void A_script_error_is_drawn_in_the_error_colour_and_other_object_lines_in_the_object_colour()
    {
        var theme = ThemeSpec.Default;
        Assert.Contains($"[color={theme.Error.Hex}]", Draw(ObjectLine(ChatMark.ScriptError)));
        Assert.Contains($"[color={theme.ChatObject.Hex}]", Draw(ObjectLine(ChatMark.OwnerOnly)));
    }

    [Fact]
    public void An_object_name_cannot_add_formatting_or_end_its_own_link_early()
    {
        string line = Draw(ObjectLine(ChatMark.None, name: "[b]Evil[/url][url=agent:x]"));
        Assert.DoesNotContain("[b]", line);
        Assert.DoesNotContain("[url=agent:", line);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(line, @"\[url=").Count);
    }

    [Fact]
    public void The_link_names_the_object_and_other_links_name_nothing()
    {
        Assert.Equal(Obj, ChatText.ObjectOf($"object:{Obj:D}"));
        Assert.Null(ChatText.ObjectOf($"agent:{Obj:D}"));
        Assert.Null(ChatText.ObjectOf("object:not-a-guid"));
        Assert.Null(ChatText.ObjectOf($"object:{Guid.Empty:D}"));
    }

    [Fact]
    public void An_agents_line_and_an_object_line_with_no_id_are_drawn_as_before()
    {
        var agent = new ChatLine(T0, ChatLineStyle.Agent, Owner, "Some Person", "hi");
        Assert.Contains($"[url=agent:{Owner:D}]Some Person[/url]", Draw(agent));
        Assert.DoesNotContain("object:", Draw(agent));
        var legacy = new ChatLine(T0, ChatLineStyle.Object, Obj, "Thing", "hi");   // no object id: no link
        Assert.DoesNotContain("[url=", Draw(legacy));
    }

    // ---------------- an object's place text ----------------

    [Fact]
    public void A_place_text_gives_the_region_and_the_position_when_it_has_one()
    {
        var p = Viewer.Shell.Notifications.PlaceText.Parse("Far Region/12/34/56");
        Assert.NotNull(p);
        Assert.Equal("Far Region", p!.Region);
        Assert.Equal(new Vector3(12, 34, 56), p.Position);
        var d = Viewer.Shell.Notifications.PlaceText.Parse("  Far Region/12.5/34.25/56  ");
        Assert.NotNull(d);
        Assert.Equal("Far Region", d!.Region);
        Assert.Equal(new Vector3(12.5f, 34.25f, 56), d.Position);
    }

    [Theory]
    [InlineData("Far Region", "Far Region")]
    [InlineData("Far Region/12/34", "Far Region/12/34")]
    [InlineData("Far Region/a/b/c", "Far Region/a/b/c")]
    public void A_place_text_without_three_numbers_is_just_a_region_name(string text, string region)
    {
        var p = Viewer.Shell.Notifications.PlaceText.Parse(text);
        Assert.NotNull(p);
        Assert.Equal(region, p!.Region);
        Assert.Null(p.Position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("/1/2/3")]
    [InlineData("/Far Region")]
    public void A_place_text_with_no_region_name_is_nothing(string? text) =>
        Assert.Null(Viewer.Shell.Notifications.PlaceText.Parse(text));

    // ---------------- the world map shows a place a script named ----------------

    private static WorldMapModel Map(List<string> asked) => new(new MapLinks { RequestName = asked.Add }, clock: () => T0);

    [Fact]
    public void A_destination_asks_for_the_region_and_chooses_the_spot_when_the_grid_names_it()
    {
        var asked = new List<string>();
        var map = Map(asked);
        Assert.True(map.ShowDestination("Far Region", new Vector3(10, 20, 30)));
        Assert.Equal("Far Region", Assert.Single(asked));
        Assert.Null(map.Selected);   // not known yet
        map.Receive(new MapBlockSize(1000, 1001, 256, 256, "Far Region", null, 13));
        Assert.NotNull(map.Selected);
        Assert.Equal("Far Region", map.Selected!.RegionName);
        Assert.Equal(new Vector3(10, 20, 30), map.Selected.Local);
        Assert.Equal(MapMathHandle(1000, 1001), map.Selected.Handle);
    }

    [Fact]
    public void A_destination_in_a_region_the_map_already_knows_is_chosen_at_once_and_an_empty_name_is_refused()
    {
        var asked = new List<string>();
        var map = Map(asked);
        map.Receive(new MapBlockSize(1000, 1001, 256, 256, "Far Region", null, 13));
        Assert.True(map.ShowDestination("far region", new Vector3(5, 6, 7)));
        Assert.NotNull(map.Selected);
        Assert.Equal(new Vector3(5, 6, 7), map.Selected!.Local);
        Assert.False(map.ShowDestination("  ", Vector3.Zero));
    }

    [Fact]
    public void A_later_destination_replaces_an_earlier_one_that_never_answered()
    {
        var map = Map([]);
        Assert.True(map.ShowDestination("First Region", new Vector3(1, 1, 1)));
        Assert.True(map.ShowDestination("Second Region", new Vector3(2, 2, 2)));
        map.Receive(new MapBlockSize(1, 1, 256, 256, "First Region", null, 13));
        Assert.Null(map.Selected);
        map.Receive(new MapBlockSize(2, 2, 256, 256, "Second Region", null, 13));
        Assert.NotNull(map.Selected);
        Assert.Equal(new Vector3(2, 2, 2), map.Selected!.Local);
    }

    private static ulong MapMathHandle(int gx, int gy) => Viewer.Core.Map.MapMath.HandleOf(gx, gy);
}
