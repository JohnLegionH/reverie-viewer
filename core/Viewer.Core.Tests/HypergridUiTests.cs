// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Inventory;
using Viewer.Core.Links;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Viewer.Shell.Links;
using Viewer.Shell.Notifications;
using Xunit;
using static Viewer.Core.Tests.InventoryModelTests;

namespace Viewer.Core.Tests;

/// <summary>
/// A Hypergrid visit as a person meets it in the interface: the automation reaches the alert on screen, the alert takes every click in its
/// own area and sits inside the window clear of the dock, the location field never hides the visited grid's host, a link back to the home
/// grid asks before it teleports, and the Recent tab filters what is held by the official viewer's rules. Every member builds its own
/// objects and uses fixed times, so the class runs in parallel.
/// </summary>
public class HypergridUiTests
{
    private static string Host => Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
    private static string Read(string file) => File.ReadAllText(Path.Combine(Host, file)).Replace("\r\n", "\n");

    // ---------------- the automation reaches the alert ----------------

    private static Notification Alert(List<int> answers) => new()
    {
        Kind = NotificationKind.Alert, Body = NoticeText.Of("home.refused.body"),
        Buttons = [new(NoticeText.Of("home.refused.relog"), 0), new(NoticeText.Of("button.cancel"), 1)],
        DefaultButton = 0, CancelButton = 1, Responder = a => answers.Add(a.Button),
    };

    [Fact]
    public void The_alert_index_names_the_alert_on_screen_though_the_list_leaves_it_out()
    {
        var model = new NotificationModel(() => 10);
        var answers = new List<int>();
        long id = model.Post(Alert(answers));
        Assert.Empty(model.List);   // why answering by a list index could never reach it
        var picked = DialogTarget.Pick(model, DialogTarget.Alert);
        Assert.Equal(id, picked?.Id);
        Assert.True(picked is not null && model.Answer(picked.Id, 1));
        Assert.Equal([1], answers);
        Assert.Null(DialogTarget.Pick(model, DialogTarget.Alert));
    }

    [Fact]
    public void The_alert_report_gives_each_buttons_key_node_and_role_but_no_text()
    {
        var model = new NotificationModel(() => 10);
        model.Post(Alert(new List<int>()));
        var buttons = DialogTarget.Buttons(model.Modal);
        Assert.Equal(2, buttons.Count);
        Assert.Equal(new DialogTarget.AlertButton(0, "home.refused.relog", "button_0", true, false), buttons[0]);
        Assert.Equal(new DialogTarget.AlertButton(1, "button.cancel", "button_1", false, true), buttons[1]);
    }

    [Fact]
    public void The_dialog_command_answers_through_the_picked_target_and_reports_the_alerts_buttons()
    {
        string text = Read("Main.Notifications.cs");
        Assert.Contains("DialogTarget.Pick(model, nth)", text);
        Assert.Contains("DialogTarget.Buttons(model.Modal)", text);
        Assert.DoesNotContain("model.List.ElementAtOrDefault(nth)", text);
    }

    // ---------------- the alert takes its own clicks and stays in view ----------------

    // the 1280 x 720 window at interface size 200 %: 640 x 360 interface units, the world view between the bars, the dock above the chat row
    private static readonly UiSize Window200 = new(640, 360);
    private static readonly UiRect Main200 = new(0, 52, 640, 268);
    private static readonly UiRect Dock200 = new(80, 236, 480, 70);

    [Fact]
    public void An_alert_that_fits_is_centred_between_the_bars_and_the_dock()
    {
        var size = new UiSize(300, 120);
        var r = ModalPlacement.Place(Window200, Main200, Dock200, size);
        Assert.Equal((640 - 300) / 2f, r.X);
        Assert.False(r.Overlaps(Dock200));
        Assert.True(r.Y >= Main200.Y + ModalPlacement.Margin);
        Assert.True(r.Bottom <= Dock200.Y - ModalPlacement.Margin);
        Assert.Equal(size, new UiSize(r.W, r.H));
    }

    [Fact]
    public void An_alert_too_tall_for_the_space_above_the_dock_still_lies_wholly_inside_the_window()
    {
        var size = new UiSize(460, 300);
        var r = ModalPlacement.Place(Window200, Main200, Dock200, size);
        Assert.True(new UiRect(0, 0, Window200.W, Window200.H).Encloses(r));
        Assert.True(r.Y >= ModalPlacement.Margin && r.Bottom <= Window200.H - ModalPlacement.Margin);
    }

    [Fact]
    public void Without_a_dock_the_alert_is_centred_in_the_world_view()
    {
        var r = ModalPlacement.Place(Window200, Main200, null, new UiSize(300, 100));
        Assert.Equal(Main200.Y + (Main200.H - 100) / 2f, r.Y);
    }

    [Fact]
    public void The_alert_is_never_taller_or_wider_than_the_window_allows()
    {
        Assert.Equal(360 - 2 * ModalPlacement.Margin, ModalPlacement.MaxHeight(Window200));
        Assert.Equal(400, ModalPlacement.TextWidth(new UiSize(1280, 720), 400, 40));
        float narrow = ModalPlacement.TextWidth(new UiSize(380, 300), 400, 40);
        Assert.True(narrow + 40 <= 380 - 2 * ModalPlacement.Margin);
        Assert.True(narrow > 100);
    }

    [Fact]
    public void The_alert_layer_is_the_shells_last_child_so_it_takes_clicks_before_the_dock_and_the_windows()
    {
        // The engine picks the control under the pointer by tree order, not by draw order (ZIndex): the old dim and dialog lived under the world
        // view, the shell's first child, so the dock, the top bar and every window took clicks meant for the alert.
        string shell = Read("ShellRoot.cs"), panels = Read("NotificationPanels.cs");
        Assert.Contains("public Control ModalLayer { get; }", shell);
        Assert.Contains("_modalLayer?.MoveToFront()", panels);
        Assert.Contains("toasts.UseModalLayer(shell.ModalLayer", panels);
        Assert.DoesNotContain("Dim.ZIndex", panels);
    }

    // ---------------- the location field never hides the grid ----------------

    [Fact]
    public void While_visiting_the_host_is_its_own_part_and_the_place_reads_as_before()
    {
        var parts = LocationField.Display("Visit Landing", "Example Parcel", new Vector3(10, 20, 30), 0, "visit.example.org:9100")!;
        Assert.Equal("Visit Landing", parts.Region);
        Assert.Equal("(visit.example.org:9100)", parts.Host);
        Assert.Equal("Visit Landing (visit.example.org:9100)", parts.Place);
        Assert.Equal("Example Parcel Visit Landing (visit.example.org:9100) 10, 20, 30", parts.Full);
        Assert.Null(LocationField.Display("Alder", null, Vector3.Zero, 0)!.Host);
    }

    [Fact]
    public void With_room_every_part_shows_in_full()
    {
        var w = LocationFit.Widths(room: 500, parcel: 80, region: 100, host: 120, coordinates: 70, gap: 6);
        Assert.Equal((80f, 100f, true, 120f), w);
    }

    [Fact]
    public void The_coordinates_go_first_then_the_parcel_is_shortened_then_the_region_and_the_host_never()
    {
        // without the coordinates everything fits
        var a = LocationFit.Widths(room: 320, parcel: 80, region: 100, host: 120, coordinates: 70, gap: 6);
        Assert.Equal((80f, 100f, false, 120f), a);
        // the parcel gives way first
        var b = LocationFit.Widths(room: 290, parcel: 80, region: 100, host: 120, coordinates: 70, gap: 6);
        Assert.False(b.Coordinates);
        Assert.Equal(100f, b.Region);
        Assert.Equal(120f, b.Host);
        Assert.Equal(290f - 100 - 120 - 12, b.Parcel);
        // then the region, down to its least width; the host keeps its whole width
        var c = LocationFit.Widths(room: 220, parcel: 80, region: 100, host: 120, coordinates: 70, gap: 6);
        Assert.Equal(120f, c.Host);
        Assert.Equal(LocationFit.MinName, c.Parcel);
        Assert.Equal(220f - 120 - LocationFit.MinName - 12, c.Region);
        Assert.True(c.Parcel + c.Region + c.Host + 12 <= 220.01f);
    }

    [Fact]
    public void The_location_field_cuts_names_with_an_ellipsis_and_its_tooltip_has_the_whole_place()
    {
        string bar = Read(Path.Combine("Reverie", "TopBar.cs"));
        Assert.Contains("_region = new Label { ThemeTypeVariation = ReverieStyle.Region, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis", bar);
        Assert.Contains("LocationFit.Widths(", bar);
        Assert.Contains("_display.TooltipText = parts?.Full ?? \"\"", bar);
    }

    // ---------------- a link home while visiting asks first ----------------

    private static LinkRouter VisitingRouter(string? current) =>
        new(new LinkGate(() => DateTimeOffset.UnixEpoch), () => "http://home.example.org:9000/", currentGrid: () => current);

    [Fact]
    public void While_visiting_a_hop_link_to_the_home_grid_asks_before_teleporting()
    {
        var a = VisitingRouter("visit.example.org:9100").Click("hop://home.example.org:9000/Alder/128/128/25")!;
        Assert.Equal(LinkActionKind.ConfirmTeleport, a.Kind);
        Assert.Equal("Alder", a.Region);
        Assert.Equal("home.example.org", a.Travel?.Host);
        Assert.Equal(9000, a.Travel?.Port);
    }

    [Fact]
    public void While_visiting_a_hop_link_to_the_visited_grid_is_a_place_here_and_opens_the_map()
    {
        var a = VisitingRouter("visit.example.org:9100").Click("hop://visit.example.org:9100/Visit%20Landing/10/20/30")!;
        Assert.Equal(LinkActionKind.ShowOnMap, a.Kind);
        Assert.Equal("Visit Landing", a.Region);
    }

    [Fact]
    public void At_home_links_are_routed_as_before()
    {
        var home = VisitingRouter(null);
        Assert.Equal(LinkActionKind.ShowOnMap, home.Click("hop://home.example.org:9000/Alder/1/2/3")!.Kind);
        Assert.Equal(LinkActionKind.ConfirmTeleport, home.Click("hop://visit.example.org:9100/Visit%20Landing/1/2/3")!.Kind);
        Assert.Equal(LinkActionKind.ShowOnMap, VisitingRouter("home.example.org:9000").Click("hop://home.example.org:9000/Alder/1/2/3")!.Kind);
    }

    [Fact]
    public void The_running_viewers_router_follows_the_grid_the_agent_is_on()
    {
        string links = Read("Main.Links.cs");
        Assert.Contains("currentGrid: () => CurrentGrid()", links);
        Assert.Contains("HostLinks.CurrentGrid = () => s.IsEnded ? null : s.VisitedGrid;", links);
    }

    // ---------------- Recent, by the official viewer's rules ----------------

    private static readonly DateTimeOffset Logout = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Logout.AddHours(5);

    [Fact]
    public void Since_logoff_shows_items_made_after_the_last_logout_only()
    {
        var f = RecentFilter.Default;
        Assert.True(f.SinceLogoff);
        Assert.True(f.Passes(Logout.AddMinutes(1), ItemKind.Landmark, Now, Logout));
        Assert.False(f.Passes(Logout.AddMinutes(-1), ItemKind.Landmark, Now, Logout));
        Assert.True(f.Passes(Logout.AddYears(-3), ItemKind.Landmark, Now, null));   // no logout kept yet: every item, as the official viewer's 0
    }

    [Fact]
    public void Since_logoff_with_hours_ago_reaching_further_back_uses_the_earlier_time()
    {
        var f = RecentFilter.Default with { Hours = 8 };
        Assert.Equal(8, f.HoursAgo);
        Assert.True(f.Passes(Now.AddHours(-7), ItemKind.Notecard, Now, Logout));    // before the logout, within 8 hours
        Assert.False(f.Passes(Now.AddHours(-9), ItemKind.Notecard, Now, Logout));
        var shorter = RecentFilter.Default with { Hours = 2 };
        Assert.True(shorter.Passes(Logout.AddMinutes(1), ItemKind.Notecard, Now, Logout));   // the logout is earlier than 2 hours ago: it wins
    }

    [Fact]
    public void Hours_and_days_ago_newer_or_older_without_since_logoff()
    {
        var newer = new RecentFilter(SinceLogoff: false, Hours: 3, Days: 1);
        Assert.Equal(27, newer.HoursAgo);
        Assert.True(newer.Passes(Now.AddHours(-26), ItemKind.Object, Now, Logout));
        Assert.False(newer.Passes(Now.AddHours(-28), ItemKind.Object, Now, Logout));
        var older = newer with { Older = true };
        Assert.False(older.Passes(Now.AddHours(-26), ItemKind.Object, Now, Logout));
        Assert.True(older.Passes(Now.AddHours(-28), ItemKind.Object, Now, Logout));
        var none = new RecentFilter(SinceLogoff: false);
        Assert.True(none.Passes(Logout.AddYears(-10), ItemKind.Object, Now, Logout));   // no date filter: every date passes
    }

    [Fact]
    public void The_filter_window_links_since_logoff_and_the_time_ago_as_the_official_one()
    {
        var f = RecentFilter.Default.WithTimeAgo(5, 2);
        Assert.False(f.SinceLogoff);
        Assert.Equal((5, 2), (f.Hours, f.Days));
        var g = f.WithSinceLogoff(true);
        Assert.True(g.SinceLogoff);
        Assert.Equal((0, 0), (g.Hours, g.Days));
        Assert.Equal(RecentFilter.MaxHours, RecentFilter.Default.WithTimeAgo(int.MaxValue, 0).Hours);
        Assert.Equal(0, RecentFilter.Default.WithTimeAgo(-4, 0).Hours);
        Assert.True(RecentFilter.Default.IsDefault);
        Assert.False(f.IsDefault);
    }

    [Fact]
    public void Kinds_can_be_left_out_and_follow_the_official_windows_groups()
    {
        var f = RecentFilter.Default.WithKind(RecentKind.Landmark, false);
        Assert.False(f.Shows(RecentKind.Landmark));
        Assert.False(f.Passes(Now, ItemKind.Landmark, Now, Logout));
        Assert.True(f.Passes(Now, ItemKind.Notecard, Now, Logout));
        Assert.False(f.IsDefault);
        Assert.True(f.WithKind(RecentKind.Landmark, true).IsDefault);
        Assert.Equal(RecentKind.Clothing, RecentFilter.KindOf(ItemKind.Bodypart));
        Assert.Equal(RecentKind.Object, RecentFilter.KindOf(ItemKind.Mesh));
        Assert.Equal(RecentKind.Landmark, RecentFilter.KindOf(ItemKind.Landmark));
    }

    private static readonly Guid Suitcase = Guid.Parse("00000000-0000-4000-8000-0000000000c1");
    private static readonly Guid SuitcaseLandmarks = Guid.Parse("00000000-0000-4000-8000-0000000000c2");

    private static (InventoryViewModel View, InventoryModel Model, List<(Guid Folder, Guid Owner)> Requests) Visited()
    {
        var requests = new List<(Guid, Guid)>();
        var model = new InventoryModel((f, o) => { lock (requests) requests.Add((f, o)); }, 8);
        model.Apply(Skeleton());
        static InventoryFolderFetched Fetched(Guid folder, IEnumerable<InventoryFolderData>? folders = null, IEnumerable<InventoryItemData>? items = null) =>
            new(folder, true, 9, 0, (folders ?? Array.Empty<InventoryFolderData>()).ToList(), (items ?? Array.Empty<InventoryItemData>()).ToList());
        model.Apply(Fetched(Root, folders: new[] { F(Suitcase, Root, "My Suitcase", InventoryKinds.FolderKindOf(100)) },
            items: new[] { I(1, Root, "old note", at: Logout.AddDays(-2)) }));
        model.Apply(Fetched(Suitcase, folders: new[] { F(SuitcaseLandmarks, Suitcase, "Landmarks", FolderKind.Landmark) }));
        model.Apply(Fetched(SuitcaseLandmarks, items: new[] { I(2, SuitcaseLandmarks, "Visit Landing", ItemKind.Landmark, at: Now.AddMinutes(-30)) }));
        var view = new InventoryViewModel(model, "My Inventory", "Library") { Tab = InventoryTab.Recent, RecentSince = Logout, Now = () => Now };
        return (view, model, requests);
    }

    [Fact]
    public void Recent_lists_a_landmark_made_while_visiting_from_the_suitcase()
    {
        var (v, _, _) = Visited();
        Assert.Equal(new[] { "Visit Landing" }, v.Rows().Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Recent_applies_the_filter_options()
    {
        var (v, _, _) = Visited();
        v.Recent = RecentFilter.Default with { Days = 3 };
        Assert.Equal(new[] { "Visit Landing", "old note" }, v.Rows().Select(r => r.Name).ToArray());
        v.Recent = RecentFilter.Default.WithKind(RecentKind.Landmark, false);
        Assert.Empty(v.Rows());
    }

    [Fact]
    public void Searching_on_the_recent_tab_filters_what_is_held_and_asks_the_grid_for_nothing()
    {
        var (v, _, requests) = Visited();
        v.Rows();
        requests.Clear();
        v.SearchText = "visit";
        Assert.Single(v.Rows());
        Assert.Empty(requests);
        v.Tab = InventoryTab.All;
        v.Rows();
        Assert.NotEmpty(requests);   // the All tab's search still looks through the whole tree, as before
    }
}
