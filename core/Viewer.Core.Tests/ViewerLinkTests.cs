// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Core.Links;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Inventory;
using Viewer.Shell.Links;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Location and app links: reading every form (secondlife://, /app/region, maps.secondlife.com, hop://), the labels, same-grid and
/// other-grid hop links, the trust rules and the throttle, what a click does, and the one renderer in notecards and every chat place.
/// Neutral hosts and names; every member builds its own objects (the clock is per test), so the class runs in parallel.
/// </summary>
public class ViewerLinkTests
{
    private const string Login = "http://grid.example.org:8002/";
    private static readonly Guid Agent = Guid.Parse("3f1c2a4e-5b6d-4e7f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid Group = Guid.Parse("7a8b9c0d-1e2f-4a3b-9c4d-5e6f7a8b9c0d");

    private sealed class Clock { public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero); }

    private static LinkRouter Router(Clock? clock = null, string? login = Login)
    {
        clock ??= new Clock();
        return new LinkRouter(new LinkGate(() => clock.Now), () => login,
            id => id == Agent ? "Test User" : null, id => id == Group ? "Example Group" : null);
    }

    // ---------------- reading ----------------

    [Fact]
    public void A_secondlife_location_reads_its_region_and_place()
    {
        var link = ViewerLinkParser.Parse("secondlife://Sandbox%20One/10/20/30");
        Assert.NotNull(link);
        Assert.Equal(ViewerLinkKind.Location, link!.Kind);
        Assert.Equal(ViewerLinkScheme.SecondLife, link.Scheme);
        Assert.Equal("Sandbox One", link.Region);
        Assert.Equal(new Vector3(10, 20, 30), link.Position);
        Assert.Equal(3, link.Coordinates);
        Assert.Equal("Sandbox One (10, 20, 30)", ViewerLinkParser.LocationLabel(link));
    }

    [Fact]
    public void A_location_without_coordinates_leads_to_the_middle_of_the_region()
    {
        var link = ViewerLinkParser.Parse("secondlife://Example")!;
        Assert.Equal(ViewerLinkKind.Location, link.Kind);
        Assert.Equal(new Vector3(128, 128, 0), link.Position);
        Assert.Equal(0, link.Coordinates);
        Assert.Equal("Example", ViewerLinkParser.LocationLabel(link));
        var two = ViewerLinkParser.Parse("secondlife://Example/40/50/")!;
        Assert.Equal(new Vector3(40, 50, 0), two.Position);
        Assert.Equal("Example (40, 50)", ViewerLinkParser.LocationLabel(two));
        var one = ViewerLinkParser.Parse("secondlife://Example/40")!;
        Assert.Equal(new Vector3(40, 128, 0), one.Position);
    }

    [Fact]
    public void Out_of_range_coordinates_are_clamped_and_non_numbers_are_left_out()
    {
        var high = ViewerLinkParser.Parse("secondlife://Example/-5/9000/5000")!;
        Assert.Equal(new Vector3(0, ViewerLinkParser.MaxRegionWidth, ViewerLinkParser.MaxHeight), high.Position);
        var bad = ViewerLinkParser.Parse("secondlife://Example/abc/20/30")!;
        Assert.Equal(ViewerLinkKind.Location, bad.Kind);
        Assert.Equal(new Vector3(128, 20, 30), bad.Position);
        var frac = ViewerLinkParser.Parse("secondlife://Example/10.6/20.4/30.5")!;
        Assert.Equal("Example (11, 20, 31)", ViewerLinkParser.LocationLabel(frac));
    }

    [Fact]
    public void Escapes_and_unprintable_characters_in_a_region_name_are_handled()
    {
        Assert.Equal("Two Words", ViewerLinkParser.Parse("secondlife://Two%20Words/1/2/3")!.Region);
        Assert.Equal("Odd'Name", ViewerLinkParser.Parse("secondlife://Odd%27Name/1/2/3")!.Region);
        Assert.Equal("AB", ViewerLinkParser.Parse("secondlife://A%07B/1/2/3")!.Region);
        Assert.Equal(ViewerLinkKind.Invalid, ViewerLinkParser.Parse("secondlife://%20/1/2/3")!.Kind);
    }

    [Fact]
    public void The_app_region_and_maps_forms_are_locations()
    {
        var app = ViewerLinkParser.Parse("secondlife:///app/region/Sandbox%20One/1/2/3")!;
        Assert.Equal(ViewerLinkKind.Location, app.Kind);
        Assert.Equal("Sandbox One", app.Region);
        Assert.Equal(new Vector3(1, 2, 3), app.Position);
        var maps = ViewerLinkParser.Parse("https://maps.secondlife.com/secondlife/Example/7/8/9")!;
        Assert.Equal(ViewerLinkKind.Location, maps.Kind);
        Assert.Equal(ViewerLinkScheme.Maps, maps.Scheme);
        Assert.Equal("Example", maps.Region);
        Assert.Equal(new Vector3(7, 8, 9), maps.Position);
        Assert.Null(ViewerLinkParser.Parse("https://www.example.org/secondlife/Example/7/8/9"));
        Assert.Null(ViewerLinkParser.Parse("https://maps.secondlife.com/"));
    }

    [Fact]
    public void A_hop_link_reads_its_grid_host_and_port()
    {
        var hop = ViewerLinkParser.Parse("hop://Grid.Example.org:8002/Sandbox%20One/10/20/30")!;
        Assert.Equal(ViewerLinkKind.Location, hop.Kind);
        Assert.Equal(ViewerLinkScheme.Hop, hop.Scheme);
        Assert.Equal("grid.example.org", hop.Host);
        Assert.Equal(8002, hop.Port);
        Assert.Equal("Sandbox One", hop.Region);
        Assert.Equal(new Vector3(10, 20, 30), hop.Position);
        var noPort = ViewerLinkParser.Parse("hop://other.example.net/Far%20Place")!;
        Assert.Null(noPort.Port);
        Assert.Equal("Far Place", noPort.Region);
        Assert.Equal(ViewerLinkKind.Invalid, ViewerLinkParser.Parse("hop://other.example.net/")!.Kind);
    }

    [Fact]
    public void App_links_read_their_command_id_and_verb()
    {
        var agent = ViewerLinkParser.Parse($"secondlife:///app/agent/{Agent}/about")!;
        Assert.Equal(ViewerLinkKind.App, agent.Kind);
        Assert.Equal("agent", agent.Command);
        Assert.Equal(Agent, agent.Id);
        Assert.Equal("about", agent.Verb);
        var group = ViewerLinkParser.Parse($"secondlife:///app/group/{Group}/about")!;
        Assert.Equal("group", group.Command);
        Assert.Equal(Group, group.Id);
        var tp = ViewerLinkParser.Parse("secondlife:///app/teleport/Example/1/2/3")!;
        Assert.Equal("teleport", tp.Command);
        Assert.Equal("Example", tp.Region);
        Assert.Equal(new Vector3(1, 2, 3), tp.Position);
        var map = ViewerLinkParser.Parse("secondlife:///app/worldmap/Two%20Words/5/6/7")!;
        Assert.Equal("worldmap", map.Command);
        Assert.Equal("Two Words", map.Region);
        var inv = ViewerLinkParser.Parse("secondlife:///app/inventory/show")!;
        Assert.Equal("inventory", inv.Command);
        Assert.Equal(["show"], inv.Path);
        var hopApp = ViewerLinkParser.Parse($"hop:///app/agent/{Agent}/inspect")!;
        Assert.Equal(ViewerLinkKind.App, hopApp.Kind);
        Assert.Equal("inspect", hopApp.Verb);
    }

    [Fact]
    public void Text_that_is_not_a_link_reads_as_nothing()
    {
        Assert.Null(ViewerLinkParser.Parse(null));
        Assert.Null(ViewerLinkParser.Parse(""));
        Assert.Null(ViewerLinkParser.Parse("ftp://example.org/x"));
        Assert.Null(ViewerLinkParser.Parse("secondlife:"));
        Assert.Equal(ViewerLinkKind.Invalid, ViewerLinkParser.Parse("secondlife:///")!.Kind);
        Assert.Equal(ViewerLinkKind.Invalid, ViewerLinkParser.Parse("secondlife:///app/")!.Kind);
        Assert.Null(ViewerLinkParser.Parse($"secondlife:///app/agent/not-an-id/about")!.Id);
    }

    // ---------------- grids ----------------

    [Fact]
    public void A_hop_link_to_this_grid_is_a_local_location_and_one_to_another_grid_asks_to_teleport()
    {
        var router = Router();
        Assert.True(router.SameGrid(ViewerLinkParser.Parse("hop://GRID.example.org:8002/Example/1/2/3")!));
        Assert.False(router.SameGrid(ViewerLinkParser.Parse("hop://grid.example.org:9000/Example/1/2/3")!));
        Assert.False(router.SameGrid(ViewerLinkParser.Parse("hop://other.example.net:8002/Example/1/2/3")!));
        Assert.True(Router(login: "http://grid.example.org/").SameGrid(ViewerLinkParser.Parse("hop://grid.example.org/Example")!));

        var local = router.Click("hop://grid.example.org:8002/Sandbox%20One/10/20/30")!;
        Assert.Equal(LinkActionKind.ShowOnMap, local.Kind);
        Assert.Equal("Sandbox One", local.Region);
        Assert.Equal(new Vector3(10, 20, 30), local.Position);

        // another grid has no map here: the click asks first, naming the region and its host, and nothing is sent until the answer
        var other = router.Click("hop://other.example.net:8002/Far%20Place/1/2/3")!;
        Assert.Equal(LinkActionKind.ConfirmTeleport, other.Kind);
        Assert.Equal("Far Place", other.Travel!.Region);
        Assert.Equal("other.example.net:8002", other.Travel.Grid);
        Assert.Equal("Far Place (other.example.net:8002)", other.NoteArg);
        Assert.Null(other.Intent);
        // unclicked (a script, a page) it never runs
        Assert.Equal("link.blocked", router.Click("hop://other.example.net:8002/Far%20Place", LinkNavigation.Other)!.NoteKey);
    }

    [Fact]
    public void A_maps_secondlife_place_opens_the_map_on_Second_Life_and_is_a_note_elsewhere()
    {
        Assert.False(Router().OnSecondLife());
        var note = Router().Click("https://maps.secondlife.com/secondlife/Example/7/8/9")!;
        Assert.Equal(LinkActionKind.Note, note.Kind);
        Assert.Equal("link.sl_place", note.NoteKey);
        Assert.Equal("Example", note.NoteArg);
        var sl = Router(login: "https://login.secondlife.com/cgi-bin/login.cgi");
        Assert.True(sl.OnSecondLife());
        Assert.Equal(LinkActionKind.ShowOnMap, sl.Click("https://maps.secondlife.com/secondlife/Example/7/8/9")!.Kind);
    }

    // ---------------- clicks ----------------

    [Fact]
    public void A_location_click_opens_the_map_at_the_place_and_sends_nothing()
    {
        var a = Router().Click("secondlife://Sandbox%20One/10/20/30")!;
        Assert.Equal(LinkActionKind.ShowOnMap, a.Kind);
        Assert.Equal("Sandbox One", a.Region);
        Assert.Equal(new Vector3(10, 20, 30), a.Position);
        Assert.Null(a.Intent);
        foreach (var form in new[] { "secondlife:///app/teleport/Example/1/2/3", "secondlife:///app/worldmap/Example/1/2/3", "secondlife:///app/region/Example/1/2/3" })
        {
            var b = Router().Click(form)!;
            Assert.Equal(LinkActionKind.ShowOnMap, b.Kind);
            Assert.Equal("Example", b.Region);
            Assert.Null(b.Intent);
        }
    }

    [Fact]
    public void App_links_open_what_exists_and_say_what_is_not_built()
    {
        Assert.Equal(LinkActionKind.OpenInventory, Router().Click("secondlife:///app/inventory/show")!.Kind);
        var agent = Router().Click($"secondlife:///app/agent/{Agent}/about")!;
        Assert.Equal(LinkActionKind.Note, agent.Kind);
        Assert.Equal("link.profile_not_built", agent.NoteKey);
        Assert.Equal("Test User", agent.NoteArg);
        var group = Router().Click($"secondlife:///app/group/{Group}/about")!;
        Assert.Equal("link.group_not_built", group.NoteKey);
        Assert.Equal("Example Group", group.NoteArg);
        var other = Router().Click("secondlife:///app/sharewithavatar/x")!;
        Assert.Equal("link.not_built", other.NoteKey);
        Assert.Null(Router().Click("plain words"));
        var web = Router().Click("https://www.example.org/page")!;
        Assert.Equal(LinkActionKind.OpenWeb, web.Kind);
        Assert.Equal(new OpenUrlIntent("https://www.example.org/page"), web.Intent);
    }

    // ---------------- trust ----------------

    [Fact]
    public void The_access_levels_are_the_official_ones()
    {
        Assert.Equal(LinkAccess.ClickOnly, LinkGate.AccessOf(ViewerLinkParser.Parse("secondlife:///app/teleport/Example/1/2/3")!));
        Assert.Equal(LinkAccess.ClickOnly, LinkGate.AccessOf(ViewerLinkParser.Parse("secondlife://Example/1/2/3")!));
        foreach (var cmd in new[] { $"agent/{Agent}/about", $"group/{Group}/about", "worldmap/Example", "inventory/show" })
            Assert.Equal(LinkAccess.Throttle, LinkGate.AccessOf(ViewerLinkParser.Parse("secondlife:///app/" + cmd)!));
        Assert.Equal(LinkAccess.Block, LinkGate.AccessOf(ViewerLinkParser.Parse("secondlife:///app/unknowncommand")!));
    }

    [Fact]
    public void An_untrusted_link_never_runs_without_a_click()
    {
        var clock = new Clock();
        var gate = new LinkGate(() => clock.Now);
        foreach (var raw in new[] { "secondlife://Example/1/2/3", "secondlife:///app/teleport/Example/1/2/3", "secondlife:///app/inventory/show" })
        {
            var link = ViewerLinkParser.Parse(raw)!;
            Assert.Equal(LinkVerdict.Blocked, gate.Check(link, LinkNavigation.Other, trusted: false));
            Assert.Equal(LinkVerdict.Run, gate.Check(link, LinkNavigation.Clicked, trusted: false));
            Assert.Equal(LinkVerdict.Run, gate.Check(link, LinkNavigation.Other, trusted: true));
        }
        var note = Router(clock).Click("secondlife://Example/1/2/3", LinkNavigation.Other)!;
        Assert.Equal(LinkActionKind.Note, note.Kind);
        Assert.Equal("link.blocked", note.NoteKey);
    }

    [Fact]
    public void Throttled_links_run_once_per_throttle_period()
    {
        var clock = new Clock();
        var gate = new LinkGate(() => clock.Now);
        var inv = ViewerLinkParser.Parse("secondlife:///app/inventory/show")!;
        var map = ViewerLinkParser.Parse("secondlife:///app/worldmap/Example")!;
        Assert.Equal(LinkVerdict.Run, gate.Check(inv, LinkNavigation.Clicked, false));
        Assert.Equal(LinkVerdict.Throttled, gate.Check(map, LinkNavigation.Clicked, false));   // one period for all throttled commands
        clock.Now += LinkGate.ThrottlePeriod - TimeSpan.FromMilliseconds(1);
        Assert.Equal(LinkVerdict.Throttled, gate.Check(inv, LinkNavigation.Clicked, false));
        clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.Equal(LinkVerdict.Run, gate.Check(inv, LinkNavigation.Clicked, false));
        Assert.Equal(LinkVerdict.Run, gate.Check(inv, LinkNavigation.Clicked, true));         // trusted text is never throttled
        // a location is click-only, not throttled
        var loc = ViewerLinkParser.Parse("secondlife://Example/1/2/3")!;
        Assert.Equal(LinkVerdict.Run, gate.Check(loc, LinkNavigation.Clicked, false));
        Assert.Equal(LinkVerdict.Run, gate.Check(loc, LinkNavigation.Clicked, false));
        var router = Router(clock);
        Assert.Equal(LinkActionKind.OpenInventory, router.Click("secondlife:///app/inventory/show")!.Kind);
        Assert.Equal("link.throttled", router.Click("secondlife:///app/inventory/show")!.NoteKey);
    }

    // ---------------- labels and rendering ----------------

    [Fact]
    public void Labels_are_the_official_display_forms()
    {
        var r = Router();
        Assert.Equal("Sandbox One (10, 20, 30)", r.Label(ViewerLinkParser.Parse("secondlife://Sandbox%20One/10/20/30")!));
        Assert.Equal("Example (1, 2, 3)", r.Label(ViewerLinkParser.Parse("hop://grid.example.org:8002/Example/1/2/3")!));
        foreach (var verb in new[] { "about", "inspect", "displayname", "completename" })
            Assert.Equal("Test User", r.Label(ViewerLinkParser.Parse($"secondlife:///app/agent/{Agent}/{verb}")!));
        Assert.Equal("Example Group", r.Label(ViewerLinkParser.Parse($"secondlife:///app/group/{Group}/about")!));
        Assert.Null(r.Label(ViewerLinkParser.Parse($"secondlife:///app/agent/{Guid.NewGuid()}/about")!));   // not known yet: as written
        Assert.Null(r.Label(ViewerLinkParser.Parse("secondlife:///app/sharewithavatar/x")!));
        Assert.False(LinkRouter.Clickable(ViewerLinkParser.Parse("secondlife:///app/sharewithavatar/x")!));
        Assert.True(LinkRouter.Clickable(ViewerLinkParser.Parse("secondlife:///app/inventory/show")!));
    }

    [Fact]
    public void The_renderer_makes_links_clickable_and_leaves_the_rest_escaped()
    {
        string Esc(string s) => s.Replace("[", "[lb]", StringComparison.Ordinal);
        string text = $"Go to secondlife://Sandbox%20One/10/20/30 or see secondlife:///app/agent/{Agent}/about, [b]not bold[/b] secondlife:///app/sharewithavatar/x";
        string bb = LinkText.Render(text, Esc, Router());
        Assert.Contains("[url=link:secondlife%3A%2F%2FSandbox%2520One%2F10%2F20%2F30]Sandbox One (10, 20, 30)[/url]", bb);
        Assert.Contains("]Test User[/url]", bb);
        Assert.Contains("[lb]b]not bold[lb]/b]", bb);
        Assert.Contains(" secondlife:///app/sharewithavatar/x", bb);          // not built: plain text
        Assert.DoesNotContain("sharewithavatar/x[/url]", bb);
        Assert.Equal("secondlife://Sandbox%20One/10/20/30", LinkText.LinkOf("link:secondlife%3A%2F%2FSandbox%2520One%2F10%2F20%2F30"));
        Assert.Null(LinkText.LinkOf("agent:" + Agent));
        // a link cannot carry formatting of its own into the label
        string tricky = LinkText.Render("secondlife://A%5Burl%3Dx%5D/1/2/3", Esc, Router());
        Assert.DoesNotContain("[url=x]", tricky);
    }

    [Fact]
    public void Every_chat_place_renders_links_through_the_one_renderer()
    {
        var strings = StringTable.English();
        var theme = ThemeSpec.Default;
        var router = Router();
        const string text = "meet at secondlife://Sandbox%20One/10/20/30";
        var lines = new[]
        {
            new ChatLine(DateTimeOffset.Now, ChatLineStyle.Agent, Agent, "Test User", text),                                          // nearby chat, IMs, group chat
            new ChatLine(DateTimeOffset.Now, ChatLineStyle.Object, Guid.NewGuid(), "A box", text, ObjectId: Guid.NewGuid()),          // object chat
            new ChatLine(DateTimeOffset.Now, ChatLineStyle.Object, Guid.NewGuid(), "A box", text, ObjectId: Guid.NewGuid(), Mark: ChatMark.ObjectMessage), // object IM
            new ChatLine(DateTimeOffset.Now, ChatLineStyle.Own, Guid.Empty, "", text),                                               // own line
            new ChatLine(DateTimeOffset.Now, ChatLineStyle.Agent, Agent, "Test User", text, Emote: true),
        };
        foreach (var line in lines)
            Assert.Contains("]Sandbox One (10, 20, 30)[/url]", ChatText.Format(line, theme, strings, false, links: router));
        Assert.DoesNotContain("[/url]", ChatText.Format(lines[3], theme, strings, false));   // without a router: as before
    }

    [Fact]
    public void A_notecard_renders_its_links_through_the_one_renderer()
    {
        var access = new NotecardAccess(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new NotecardPermissions(Guid.Empty, Guid.Empty, 0x7fffffff, 0x7fffffff, 0, 0, 0), false);
        var doc = new NotecardDocument(2, "See secondlife://Sandbox%20One/10/20/30 and https://www.example.org/ now", []);
        var model = new NotecardWindowModel(new NotecardOpenResult(NotecardOpenStatus.Ok, Guid.NewGuid(), "Card", "", access, doc));
        string bb = model.RenderText(s => s.Replace("[", "[lb]", StringComparison.Ordinal), Router());
        Assert.Contains("]Sandbox One (10, 20, 30)[/url]", bb);
        Assert.Contains("]https://www.example.org/[/url]", bb);
        Assert.Equal(2, NotecardLinks.Split(doc.Text).Count(s => s.Kind != NotecardSegmentKind.Text));
        var maps = NotecardLinks.Split("at https://maps.secondlife.com/secondlife/Example/1/2/3 ok");
        Assert.Equal(NotecardSegmentKind.PlaceLink, maps.Single(s => s.Kind != NotecardSegmentKind.Text).Kind);
    }
}
