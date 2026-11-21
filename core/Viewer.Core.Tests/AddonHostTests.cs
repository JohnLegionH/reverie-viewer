// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.Loader;
using Viewer.Addons;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Shell;
using Viewer.Shell.Addons;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The add-on host and interface 0.1: manifests, built-in and outside add-ons, permissions, jobs, events, actions, filters
/// through the action pipeline, windows and menu items, an add-on's own settings, errors that switch an add-on off, and
/// loading a library in its own load context. Each test makes its own host and its own temp folders, so the class runs in
/// parallel with the others.
/// </summary>
public class AddonHostTests
{
    private static readonly Guid Dance = Guid.Parse("8f1c2a6e-3b0d-4c2e-9f4a-6d7e8a9b0c11");
    private static readonly Guid Wave = Guid.Parse("8f1c2a6e-3b0d-4c2e-9f4a-6d7e8a9b0c22");
    private static readonly Guid Chair = Guid.Parse("8f1c2a6e-3b0d-4c2e-9f4a-6d7e8a9b0c33");

    private sealed class Services : IAddonServices
    {
        public double Clock;
        public readonly List<IReadOnlyList<(Guid Animation, bool Start)>> Sent = [];
        public readonly List<string> Said = [];
        public double Now => Clock;
        public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests) => Sent.Add([.. requests]);
        public bool Say(string text) { Said.Add(text); return true; }
        public IAddonInventory? Inventory => null;
    }

    /// <summary>A test add-on: records what it hears and does what the test sets up.</summary>
    private sealed class Probe : IAddon
    {
        public IAddonContext? Context;
        public readonly List<string> Heard = [];
        public Action<IAddonContext>? OnStart;
        public int Stops;
        public void Start(IAddonContext context)
        {
            Context = context;
            Heard.Add("start");
            OnStart?.Invoke(context);
        }
        public void Stop() => Stops++;
    }

    private static AddonManifest Manifest(string id, string? job = null, params string[] permissions) =>
        new(id, "Name " + id, "1.0", "Someone", "MIT", "0.1", job, permissions, id + ".dll");

    private static (AddonHost Host, Services Services, ListSink Sink) NewHost(string? stateFile = null)
    {
        var services = new Services();
        var sink = new ListSink();
        return (new AddonHost(services, new CoreLog(sink, "test"), stateFile), services, sink);
    }

    // ---------------- manifests ----------------

    [Fact]
    public void A_manifest_is_read_with_every_field_and_a_bad_one_is_refused_with_its_problems()
    {
        var m = AddonManifest.Parse("""
            { "id": "hello", "name": "Hello", "version": "1.2.0", "author": "Someone", "licence": "MIT",
              "interface": "0.1", "permissions": ["window", "chat.send"], "library": "Hello.dll" }
            """, out var none);
        Assert.NotNull(m);
        Assert.Empty(none);
        Assert.Equal(("hello", "Hello", "1.2.0", "Someone", "MIT", "0.1", "Hello.dll"), (m!.Id, m.Name, m.Version, m.Author, m.Licence, m.Interface, m.Library));
        Assert.Null(m.Job);
        Assert.Equal(["window", "chat.send"], m.Permissions);

        Assert.Null(AddonManifest.Parse("""{ "id": "Bad Id", "name": "x", "version": "1", "author": "a", "licence": "l", "interface": "1.0", "permissions": ["network"], "library": "../x.dll" }""", out var problems));
        Assert.Contains(problems, p => p.Contains("'id'"));
        Assert.Contains(problems, p => p.Contains("interface 1.0"));
        Assert.Contains(problems, p => p.Contains("unknown permission 'network'"));
        Assert.Contains(problems, p => p.Contains("'library'"));
    }

    // ---------------- on, off, permissions, jobs ----------------

    [Fact]
    public void A_built_in_add_on_starts_by_default_and_an_outside_one_stays_off_until_its_permissions_are_granted()
    {
        var (host, _, _) = NewHost();
        var inside = new Probe();
        var outside = new Probe();
        host.Register(Manifest("inside"), () => inside, builtIn: true);
        var entry = host.Register(Manifest("outside", null, AddonPermissions.ChatSend), () => outside, builtIn: false);
        host.StartEnabled();

        Assert.Equal(["start"], inside.Heard);
        Assert.Empty(outside.Heard);
        Assert.False(entry.Enabled);
        Assert.False(host.SetEnabled("outside", true));                 // not without the user's grant
        Assert.Empty(outside.Heard);
        Assert.True(host.SetEnabled("outside", true, grantPermissions: true));
        Assert.Equal(["start"], outside.Heard);
        Assert.Equal([AddonPermissions.ChatSend], entry.Granted);
        Assert.True(entry.Running);
    }

    [Fact]
    public void Only_one_add_on_fills_a_job_and_choosing_another_stops_the_first()
    {
        var (host, _, _) = NewHost();
        var first = new Probe();
        var second = new Probe();
        host.Register(Manifest("ao-one", "ao"), () => first, builtIn: true);
        host.Register(Manifest("ao-two", "ao"), () => second, builtIn: true);
        host.StartEnabled();

        Assert.Equal("ao-one", host.ActiveFor("ao")?.Id);
        Assert.Empty(second.Heard);                                     // a second built-in for the same job waits
        Assert.True(host.SetEnabled("ao-two", true));
        Assert.Equal(1, first.Stops);
        Assert.Equal(["start"], second.Heard);
        Assert.Equal("ao-two", host.ActiveFor("ao")?.Id);
        Assert.Equal(["ao"], host.Jobs);
    }

    [Fact]
    public void An_add_ons_settings_are_written_to_a_temporary_file_and_renamed_so_a_failed_write_keeps_the_old_file()
    {
        string folder = TestTemp.NewFolder("addon-settings-atomic");
        try
        {
            var (host, _, sink) = NewHost();
            host.SettingsRoot = folder;
            host.SaveSettings("one", new Dictionary<string, string> { ["k"] = "old" });
            string file = Path.Combine(host.SettingsFolderOf("one"), "settings.json");
            Assert.Contains("old", File.ReadAllText(file));
            Assert.False(File.Exists(file + ".new"));   // the rename leaves no temporary file behind

            // the temporary file cannot be made (a folder holds its name): the write fails, and the file already there is untouched
            Directory.CreateDirectory(file + ".new");
            host.SaveSettings("one", new Dictionary<string, string> { ["k"] = "new" });
            Assert.Contains("old", File.ReadAllText(file));
            Assert.DoesNotContain("new", File.ReadAllText(file));
            Assert.Contains("addon.settings_unwritable", sink.Text);
        }
        finally { TestTemp.Delete(folder); }
    }

    [Fact]
    public void The_on_and_off_state_is_written_to_a_temporary_file_and_renamed_so_a_failed_write_keeps_the_old_file()
    {
        string folder = TestTemp.NewFolder("addon-state-atomic");
        try
        {
            string state = Path.Combine(folder, AddonHost.StateFileName);
            var (host, _, sink) = NewHost(state);
            host.Register(Manifest("inside"), () => new Probe(), builtIn: true);
            host.StartEnabled();
            Assert.True(host.SetEnabled("inside", false));
            string before = File.ReadAllText(state);
            Assert.False(File.Exists(state + ".new"));

            Directory.CreateDirectory(state + ".new");
            Assert.True(host.SetEnabled("inside", true));
            Assert.Equal(before, File.ReadAllText(state));
            Assert.Contains("addon.state_unwritable", sink.Text);
        }
        finally { TestTemp.Delete(folder); }
    }

    [Fact]
    public void Switching_off_is_kept_between_runs_and_a_built_in_add_on_stays_off_after_a_restart()
    {
        string folder = TestTemp.NewFolder("addon-state");
        try
        {
            string state = Path.Combine(folder, AddonHost.StateFileName);
            var (host, _, _) = NewHost(state);
            var probe = new Probe();
            host.Register(Manifest("inside"), () => probe, builtIn: true);
            host.StartEnabled();
            Assert.True(host.SetEnabled("inside", false));
            Assert.Equal(1, probe.Stops);

            var (again, _, _) = NewHost(state);
            var next = new Probe();
            again.Register(Manifest("inside"), () => next, builtIn: true);
            again.StartEnabled();
            Assert.Empty(next.Heard);
            Assert.False(Assert.Single(again.Addons).Enabled);
        }
        finally { TestTemp.Delete(folder); }
    }

    // ---------------- errors ----------------

    [Fact]
    public void An_exception_inside_an_add_on_switches_it_off_with_a_note_and_the_others_carry_on()
    {
        var (host, _, sink) = NewHost();
        var bad = new Probe();
        var good = new Probe();
        bad.OnStart = c => c.RegionChanged += _ => throw new InvalidOperationException("broken");
        good.OnStart = c => c.RegionChanged += r => good.Heard.Add("region " + r);
        host.Register(Manifest("bad", null, AddonPermissions.Session), () => bad, builtIn: true);
        host.Register(Manifest("good", null, AddonPermissions.Session), () => good, builtIn: true);
        host.StartEnabled();

        host.RegionChanged("Somewhere");                               // does not throw

        var entry = Assert.Single(host.Addons, a => a.Id == "bad");
        Assert.False(entry.Running);
        Assert.False(entry.Enabled);
        Assert.Contains("InvalidOperationException", entry.Note);
        Assert.Equal(1, bad.Stops);
        Assert.Contains("region Somewhere", good.Heard);
        Assert.Contains(sink.Entries, e => e.Event == "addon.failed");
    }

    [Fact]
    public void An_exception_in_start_leaves_the_add_on_off_with_a_note()
    {
        var (host, _, _) = NewHost();
        var probe = new Probe { OnStart = _ => throw new FormatException("at start") };
        host.Register(Manifest("crashy"), () => probe, builtIn: true);
        host.StartEnabled();
        var entry = Assert.Single(host.Addons);
        Assert.False(entry.Running);
        Assert.Contains("FormatException", entry.Note);
    }

    [Fact]
    public void Using_something_not_granted_is_refused_and_switches_the_add_on_off()
    {
        var (host, services, _) = NewHost();
        var probe = new Probe { OnStart = c => c.Say("hello") };        // asks for nothing, then speaks
        host.Register(Manifest("quiet"), () => probe, builtIn: true);
        host.StartEnabled();

        Assert.Empty(services.Said);
        var entry = Assert.Single(host.Addons);
        Assert.False(entry.Running);
        Assert.Contains(AddonPermissions.ChatSend, entry.Note);
    }

    // ---------------- events and actions ----------------

    [Fact]
    public void An_add_on_reads_the_grid_type_but_has_no_way_to_change_what_the_grid_allows()
    {
        var (host, _, _) = NewHost();
        var probe = new Probe();
        host.Register(Manifest("grid-reader"), () => probe, builtIn: true);   // no permission needed to read the grid type
        host.StartEnabled();
        Assert.NotNull(probe.Context);
        Assert.Equal(AddonGridType.Unknown, probe.Context.GridType);

        host.LoggedIn(Viewer.Core.Session.GridKind.SecondLife);
        Assert.Equal(AddonGridType.SecondLife, probe.Context.GridType);
        host.LoggedOut();
        Assert.Equal(AddonGridType.Unknown, probe.Context.GridType);
        host.LoggedIn(Viewer.Core.Session.GridKind.OpenSim);
        Assert.Equal(AddonGridType.OpenSim, probe.Context.GridType);

        // the grid's switch lives in the core; the add-on interface only reads the grid type
        var gridMembers = typeof(IAddonContext).GetMembers().Where(m => m.Name.Contains("Grid", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Policy", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Feature", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(["get_GridType", "GridType"], gridMembers.Select(m => m.Name).Order().ToArray());
        Assert.False(typeof(IAddonContext).GetProperty(nameof(IAddonContext.GridType))!.CanWrite);
    }

    [Fact]
    public void An_add_on_hears_login_logout_region_motion_animations_and_chat_it_was_granted()
    {
        var (host, _, _) = NewHost();
        var probe = new Probe();
        probe.OnStart = c =>
            {
                c.LoggedIn += () => Heard("in");
                c.LoggedOut += () => Heard("out");
                c.RegionChanged += r => Heard("region " + r);
                c.MotionChanged += m => Heard("motion " + m);
                c.PlayingChanged += p => Heard("playing " + p.Count);
                c.ChatReceived += l => Heard($"chat {l.Kind} {l.Text}");
            };
        void Heard(string s) => probe.Heard.Add(s);
        host.Register(Manifest("ears", null, AddonPermissions.Session, AddonPermissions.Motion, AddonPermissions.AnimationsRead, AddonPermissions.ChatRead),
            () => probe, builtIn: true);
        host.StartEnabled();

        host.LoggedIn();
        host.RegionChanged("Elsewhere");
        host.Motion(MotionState.Walking);
        host.Motion(MotionState.Walking);                               // no change, no event
        host.Playing([new PlayingAnimation(Dance, 1)]);
        host.Chat(new ChatLine(ChatKind.Local, Guid.NewGuid(), "Someone", "hi"));
        host.Chat(new ChatLine(ChatKind.OwnerSay, Guid.NewGuid(), "My object", "ping"));
        Assert.NotNull(probe.Context);
        Assert.Equal(MotionState.Walking, probe.Context.Motion);
        Assert.Equal([new PlayingAnimation(Dance, 1)], probe.Context.Playing);
        host.LoggedOut();

        Assert.Equal(["start", "in", "region Elsewhere", "motion Walking", "playing 1", "chat Local hi", "chat OwnerSay ping", "out"], probe.Heard);
        Assert.Equal(MotionState.Unknown, probe.Context.Motion);         // nothing is known of the avatar once logged out
        Assert.Empty(probe.Context.Playing);
    }

    [Fact]
    public void Animation_requests_go_out_together_at_the_end_of_the_frame_and_are_stopped_when_the_add_on_is_switched_off()
    {
        var (host, services, _) = NewHost();
        var probe = new Probe();
        host.Register(Manifest("dancer", null, AddonPermissions.AnimationsPlay), () => probe, builtIn: true);
        host.StartEnabled();

        Assert.NotNull(probe.Context);
        probe.Context.PlayAnimation(Dance);
        probe.Context.PlayAnimation(Wave);
        probe.Context.StopAnimation(Wave);
        Assert.Empty(services.Sent);
        host.Frame();
        Assert.Equal([(Dance, true), (Wave, true), (Wave, false)], Assert.Single(services.Sent));

        host.SetEnabled("dancer", false);                               // the dance it left playing is stopped at once
        Assert.Equal([(Dance, false)], services.Sent[^1]);
    }

    [Fact]
    public void An_add_on_says_a_line_in_local_chat_through_the_viewer()
    {
        var (host, services, _) = NewHost();
        var probe = new Probe { OnStart = c => c.Say("hello") };
        host.Register(Manifest("talker", null, AddonPermissions.ChatSend), () => probe, builtIn: true);
        host.StartEnabled();
        Assert.Equal(["hello"], services.Said);
        Assert.True(Assert.Single(host.Addons).Running);
    }

    // ---------------- refusals ----------------

    [Fact]
    public void A_filter_refuses_a_sit_through_the_action_pipeline_with_its_reason_and_lets_it_through_once_removed()
    {
        var (host, _, _) = NewHost();
        IDisposable? filter = null;
        var probe = new Probe { OnStart = c => filter = c.AddFilter(AddonActionKind.Sit, _ => AddonVerdict.Refuse("Not now")) };
        host.Register(Manifest("keeper", null, AddonPermissions.Filter), () => probe, builtIn: true);
        host.StartEnabled();
        var refused = new List<(string, string)>();
        host.Refused += (name, reason) => refused.Add((name, reason));

        var transport = new FakeTransport();
        var pipeline = new ActionPipeline(() => transport, () => true, new CoreLog(new ListSink(), "test"));
        pipeline.AddVeto(host);

        var sit = pipeline.Submit(new SitIntent(Chair));
        Assert.Equal(ActionStatus.Vetoed, sit.Status);
        Assert.Equal([("Name keeper", "Not now")], refused);
        Assert.Empty(transport.Commands);
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new StandIntent()).Status);   // stand is not filtered

        filter!.Dispose();
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new SitIntent(Chair)).Status);
    }

    [Fact]
    public void A_filter_that_throws_switches_its_add_on_off_and_the_action_goes_ahead()
    {
        var (host, _, _) = NewHost();
        var probe = new Probe { OnStart = c => c.AddFilter(AddonActionKind.Teleport, _ => throw new InvalidOperationException()) };
        host.Register(Manifest("broken-filter", null, AddonPermissions.Filter), () => probe, builtIn: true);
        host.StartEnabled();
        Assert.True(host.Check(new TeleportHomeIntent(), ActionPhase.Submit).Allowed);
        Assert.False(Assert.Single(host.Addons).Running);
    }

    // ---------------- the screen and settings ----------------

    [Fact]
    public void Windows_and_menu_items_come_and_go_with_the_add_on_and_a_button_press_reaches_it()
    {
        var (host, services, _) = NewHost();
        var probe = new Probe
        {
            OnStart = c =>
            {
                var w = c.AddWindow("main", "Hello window");
                var b = w.Add(new AddonButton("hi") { Text = "Say hello" });
                b.Pressed += () => c.Say("hello");
                c.AddMenuItem("Hello", w.Open);
            },
        };
        host.Register(Manifest("hello", null, AddonPermissions.Window, AddonPermissions.ChatSend), () => probe, builtIn: true);
        int screen = 0;
        host.ScreenChanged += () => screen++;
        host.StartEnabled();

        var (owner, window) = Assert.Single(host.Windows);
        Assert.Equal(("hello", "Hello window"), (owner, window.Title));
        Assert.Equal("Hello", Assert.Single(host.MenuItems).Label);
        host.Run("hello", ((AddonButton)window.Controls[0]).Press);
        Assert.Equal(["hello"], services.Said);
        Assert.True(screen > 0);

        host.SetEnabled("hello", false);
        Assert.Empty(host.Windows);
        Assert.Empty(host.MenuItems);
    }

    [Fact]
    public void A_window_needs_the_window_permission()
    {
        var (host, _, _) = NewHost();
        var probe = new Probe { OnStart = c => c.AddWindow("w", "W") };
        host.Register(Manifest("no-window"), () => probe, builtIn: true);
        host.StartEnabled();
        Assert.Empty(host.Windows);
        Assert.Contains(AddonPermissions.Window, Assert.Single(host.Addons).Note);
    }

    [Fact]
    public void An_add_on_keeps_its_settings_in_its_own_folder_between_runs()
    {
        string folder = TestTemp.NewFolder("addon-settings");
        try
        {
            File.WriteAllText(Path.Combine(folder, AddonManifest.FileName), """
                { "id": "keeper", "name": "Keeper", "version": "1", "author": "a", "licence": "MIT", "interface": "0.1", "library": "Keeper.dll" }
                """);
            var (host, _, _) = NewHost();
            var probe = new Probe { OnStart = c => c.SetSetting("colour", "blue") };
            var entry = host.Register(AddonManifest.Parse(File.ReadAllText(Path.Combine(folder, AddonManifest.FileName)), out _)!, () => probe, builtIn: true);
            host.SettingsRoot = folder;
            host.StartEnabled();
            Assert.NotNull(probe.Context);
            Assert.Equal(Path.Combine(folder, "keeper"), probe.Context.SettingsFolder);
            Assert.True(File.Exists(Path.Combine(folder, "keeper", "settings.json")));

            var (again, _, _) = NewHost();
            again.SettingsRoot = folder;
            var next = new Probe();
            again.Register(entry.Manifest, () => next, builtIn: true);
            again.StartEnabled();
            Assert.NotNull(next.Context);
            Assert.Equal("blue", next.Context.GetSetting("colour"));
        }
        finally { TestTemp.Delete(folder); }
    }

    // ---------------- loading ----------------

    [Fact]
    public void An_add_on_folder_is_loaded_in_its_own_load_context_sharing_the_interface_assembly()
    {
        string root = TestTemp.NewFolder("addon-load");
        try
        {
            string folder = Path.Combine(root, "ao");
            Directory.CreateDirectory(folder);
            string bin = AppContext.BaseDirectory;
            foreach (string file in new[] { "Viewer.Addon.Ao.dll", "Viewer.Overrider.dll" })
                File.Copy(Path.Combine(bin, file), Path.Combine(folder, file));
            File.Copy(Path.Combine(bin, "addons-ao", AddonManifest.FileName), Path.Combine(folder, AddonManifest.FileName));

            var (host, _, _) = NewHost();
            host.SettingsRoot = Path.Combine(root, "settings");
            Assert.Equal(1, host.Discover(root, builtIn: true));
            host.StartEnabled();

            var entry = Assert.Single(host.Addons);
            Assert.Equal(("ao", "ao"), (entry.Id, entry.Manifest.Job));
            Assert.True(entry.Running, entry.Note);
            var instanceContext = host.LoadContextOf("ao");
            Assert.NotNull(instanceContext);
            Assert.NotSame(AssemblyLoadContext.Default, instanceContext);
            Assert.Contains(instanceContext!.Assemblies, a => a.GetName().Name == "Viewer.Addon.Ao");
            Assert.DoesNotContain(instanceContext.Assemblies, a => a.GetName().Name == "Viewer.Addons.Api");   // shared, not loaded twice

            host.SetEnabled("ao", false);
            Assert.Null(host.LoadContextOf("ao"));
        }
        finally { TestTemp.Delete(root); }
    }

    /// <summary>The sample's build output (the tests build it beside themselves, in the same configuration).</summary>
    private static string SampleOutput()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));   // .../bin/<configuration>/net10.0
        string configuration = bin.Parent!.Name;
        var root = bin;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Viewer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "samples", "HelloAddon", "bin", configuration, bin.Name);
    }

    [Fact]
    public void The_sample_add_on_installed_by_the_user_stays_off_until_granted_then_its_button_says_hello()
    {
        string root = TestTemp.NewFolder("addon-sample");
        try
        {
            string folder = Path.Combine(root, "hello-sample");
            Directory.CreateDirectory(folder);
            foreach (string file in new[] { "HelloAddon.dll", AddonManifest.FileName })
                File.Copy(Path.Combine(SampleOutput(), file), Path.Combine(folder, file));
            Assert.False(File.Exists(Path.Combine(SampleOutput(), "Viewer.Addons.Api.dll")));   // the viewer's copy is used, none ships with it

            var (host, services, _) = NewHost();
            Assert.Equal(1, host.Discover(root, builtIn: false));
            host.StartEnabled();
            var entry = Assert.Single(host.Addons);
            Assert.Equal([AddonPermissions.Window, AddonPermissions.ChatSend], entry.Manifest.Permissions);
            Assert.False(entry.Running);
            Assert.Empty(host.MenuItems);

            Assert.True(host.SetEnabled("hello-sample", true, grantPermissions: true));
            Assert.True(entry.Running, entry.Note);
            Assert.Equal("Hello Sample", Assert.Single(host.MenuItems).Label);
            var window = Assert.Single(host.Windows).Window;
            var say = Assert.IsType<AddonButton>(window.Controls.Single(c => c.Id == "say"));
            host.Run("hello-sample", say.Press);
            Assert.Equal(["Hello from an add-on!"], services.Said);
            Assert.Equal("Said hello.", window.Controls.Single(c => c.Id == "said").Text);

            Assert.True(host.SetEnabled("hello-sample", false));
            Assert.Empty(host.Windows);
            Assert.Empty(host.MenuItems);
        }
        finally { TestTemp.Delete(root); }
    }

    [Fact]
    public void A_folder_whose_manifest_is_bad_is_listed_with_its_problem_and_not_loaded()
    {
        string root = TestTemp.NewFolder("addon-bad");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "broken"));
            File.WriteAllText(Path.Combine(root, "broken", AddonManifest.FileName), """{ "id": "broken" }""");
            var (host, _, sink) = NewHost();
            Assert.Equal(0, host.Discover(root, builtIn: false));
            Assert.Empty(host.Addons);
            Assert.Contains(sink.Entries, e => e.Event == "addon.manifest_bad");
        }
        finally { TestTemp.Delete(root); }
    }

    // ---------------- motion ----------------

    [Fact]
    public void The_motion_state_is_read_from_the_animations_the_region_lists()
    {
        static Guid Id(string name) => Viewer.Core.Avatars.AgentAnimations.Names.Single(kv => kv.Value == name).Key;
        Assert.Equal(MotionState.Standing, AddonMotion.From([Id("stand")], seated: false));
        Assert.Equal(MotionState.Walking, AddonMotion.From([Id("stand"), Id("walk")], seated: false));
        Assert.Equal(MotionState.Running, AddonMotion.From([Id("run")], seated: false));
        Assert.Equal(MotionState.Flying, AddonMotion.From([Id("fly")], seated: false));
        Assert.Equal(MotionState.Hovering, AddonMotion.From([Id("hover")], seated: false));
        Assert.Equal(MotionState.SittingOnGround, AddonMotion.From([Id("sit_ground")], seated: false));
        Assert.Equal(MotionState.SittingOnObject, AddonMotion.From([Dance], seated: true));
        Assert.Equal(MotionState.Unknown, AddonMotion.From([], seated: false));
    }

    // ---------------- strings ----------------

    [Fact]
    public void An_add_ons_own_text_can_be_shown_under_a_key_of_its_own_and_cannot_replace_the_viewers_text()
    {
        var table = StringTable.English();
        Assert.True(table.Define("addon.hello.menu", "Hello"));
        Assert.Equal("Hello", table.Get("addon.hello.menu"));
        Assert.False(table.Define("menu.me.quit", "Gone"));
        Assert.NotEqual("Gone", table.Get("menu.me.quit"));
    }
}
