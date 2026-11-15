// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Viewer.Core;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// S-1 native smoke. Runs only in the EXPORTED build, then quits:
///   (e) ADR-002 startup check first (StartupCheck + RenderFloor, the same code as a normal start): an unapproved
///       rendering driver is refused (exit 3), an approved one below the floor (Vulkan 1.3 / D3D12 FL 12_0) too (exit 4),
///       with the string-table message;
///   (a) the net10 Viewer.Core loads (its version);
///   (b) the physics lifecycle, 1,000 create/destroy cycles in THIS process, same bounds;
///   (c) which joltc is loaded (path + SHA-256, must be the patched build);
///   (d) the actual rendering driver, method and GPU adapter;
///   (f) Godot's own 3D physics is the Dummy server;
///   (h): engine errors/warnings/prints routed through EngineLogger reach the log redacted (ADR-019);
///   (i): the login screen, in English and pseudo-localised, fits: nothing clipped or outside the window, and every
///       control has an accessible name (ADR-020);
///   (g) the JSON result, written ONLY to the directory given as a user argument (ADR-019: nothing beside the
///       executable): Reverie.exe -- --s1-out=&lt;dir&gt; [--s1-tag=&lt;tag&gt;] writes &lt;dir&gt;\s1-&lt;tag&gt;.json (tag default "result").
///       Without --s1-out, S-1 prints an error and exits 2 without writing anything.
/// Exit 0 = PASS, 1 = FAIL, 2 = error (including a missing --s1-out), 3 = refused backend, 4 = below the floor.
/// Started by <see cref="Main"/> when any "--s1-" user argument is present.
/// </summary>
public partial class S1Smoke : Node
{
    private const string PatchedJoltc = "16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5";

    public const int ExitPass = 0, ExitFail = 1, ExitError = 2;

    private readonly Dictionary<string, object?> _result = new();
    private string _resultPath = "";
    private readonly List<string> _failures = new();

    public override async void _Ready()
    {
        // S-1 is a statement about the EXPORTED build. Anywhere else (the editor, a debug run) it does nothing and
        // writes nothing, so no result file ever lands next to the editor executable.
        if (!OS.HasFeature("template"))
        {
            Log("S-1 runs only in the exported build (feature 'template' absent); nothing done, nothing written");
            GetTree().Quit(ExitError);
            return;
        }

        // User arguments come after "--" (OS.GetCmdlineUserArgs, Godot 4.7 OS.get_cmdline_user_args).
        string[] userArgs = OS.GetCmdlineUserArgs();
        string? outDir = UserArg(userArgs, "--s1-out=");
        if (string.IsNullOrWhiteSpace(outDir) || !Path.IsPathFullyQualified(outDir))
        {
            GD.PrintErr("S1 ERROR: no result directory. Run as: Reverie.exe [engine args] -- --s1-out=<absolute dir> [--s1-tag=<tag>]. " +
                        "Nothing is written beside the executable (ADR-019); nothing was written.");
            GetTree().Quit(ExitError);
            return;
        }
        string tag = UserArg(userArgs, "--s1-tag=") is { Length: > 0 } t ? t : "result";
        _resultPath = Path.Combine(outDir, $"s1-{tag}.json");

        int exit;
        try
        {
            exit = Run();
            if (exit == ExitPass)
            {
                EngineLogCheck();
                await UiCheck();
                await ShellCheck();
                await KeysCheck();
                await MenuChoiceCheck();
                FontCheck();
                await ChatCheck();
                await PeopleCheck();
                await TravelCheck();
                await InventoryCheck();
                await PreferencesCheck();
                await OutfitCheck();
                await AppearanceCheck();
                await NotificationsCheck();
                exit = _failures.Count == 0 ? ExitPass : ExitFail;
            }
        }
        catch (Exception e)
        {
            _failures.Add($"exception: {e}");
            exit = ExitError;
        }

        // The check at quit: the engine corrupts its heap when it quits with resources the viewer made still live.
        try
        {
            string live = Presentation.Rids.CheckAtQuit(out int leaked);
            _result["live_at_quit"] = new { count = leaked, summary = live };
            if (leaked > 0)
            {
                _failures.Add("resources still live at quit: " + live);
                if (exit == ExitPass) exit = ExitFail;
            }
        }
        catch (Exception e) { _failures.Add("check at quit: " + e.GetType().Name + ": " + e.Message); if (exit == ExitPass) exit = ExitError; }

        _result["failures"] = _failures;
        _result["exit_code"] = exit;
        _result["verdict"] = exit switch { ExitPass => "PASS", RenderFloor.ExitUnapprovedBackend or RenderFloor.ExitBelowFloor => "REFUSED", ExitFail => "FAIL", _ => "ERROR" };
        WriteResult();
        Log($"VERDICT {_result["verdict"]} exit={exit}{(_failures.Count > 0 ? " failures: " + string.Join(" | ", _failures) : "")}");
        GetTree().Quit(exit);
    }

    private int Run()
    {
        _result["stage"] = "S-1";
        _result["timestamp_utc"] = DateTime.UtcNow.ToString("o");
        _result["godot_version"] = Engine.GetVersionInfo()["string"].AsString();
        _result["exported_build"] = OS.HasFeature("template");
        _result["executable"] = OS.GetExecutablePath();
        _result["cmdline_args"] = OS.GetCmdlineArgs();
        _result["cmdline_user_args"] = OS.GetCmdlineUserArgs();
        _result["dotnet_runtime"] = RuntimeInformation.FrameworkDescription;
        _result["window"] = Main.TestWindowInfo;

        // (d) + (e): the driver that actually initialised, then the ADR-002 check, before anything else.
        string driver = RenderingServer.GetCurrentRenderingDriverName();
        _result["rendering"] = new Dictionary<string, object?>
        {
            ["driver"] = driver,
            ["method"] = RenderingServer.GetCurrentRenderingMethod(),
            ["adapter_name"] = RenderingServer.GetVideoAdapterName(),
            ["adapter_vendor"] = RenderingServer.GetVideoAdapterVendor(),
            ["adapter_type"] = RenderingServer.GetVideoAdapterType().ToString(),
            ["api_version"] = RenderingServer.GetVideoAdapterApiVersion(),
            ["driver_info"] = OS.GetVideoAdapterDriverInfo(),
        };
        Log($"rendering driver={driver} method={RenderingServer.GetCurrentRenderingMethod()} adapter=\"{RenderingServer.GetVideoAdapterName()}\" " +
            $"vendor=\"{RenderingServer.GetVideoAdapterVendor()}\" api={RenderingServer.GetVideoAdapterApiVersion()} " +
            $"driver_info=[{string.Join(", ", OS.GetVideoAdapterDriverInfo())}]");

        var decision = StartupCheck.Run(Main.Log);
        _result["floor"] = new Dictionary<string, object?>
        {
            ["verdict"] = decision.Verdict.ToString(),
            ["backend"] = decision.Backend,
            ["api_version"] = decision.ApiVersion,
            ["parsed"] = decision.Parsed,
            ["exit_code"] = decision.ExitCode,
        };
        if (!decision.Approved)
        {
            string message = decision.Message(StringTable.English());
            _result["refusal_message"] = message;
            Console.Error.WriteLine(message);
            return decision.ExitCode;
        }

        // (a) the net10 core loads.
        _result["core"] = new Dictionary<string, object?>
        {
            ["version"] = CoreInfo.Version,
            ["target_framework"] = CoreInfo.TargetFramework,
            ["location"] = typeof(CoreInfo).Assembly.Location,
        };
        Log($"Viewer.Core {CoreInfo.Version} ({CoreInfo.TargetFramework}) on {RuntimeInformation.FrameworkDescription}");

        // (b) the physics lifecycle, in-process, both workarounds on (their defaults).
        var lifecycle = PhysicsLifecycleProbe.Run(1000, 100, Log);
        _result["physics_lifecycle"] = new Dictionary<string, object?>
        {
            ["pass"] = lifecycle.Pass,
            ["cycles"] = lifecycle.Cycles,
            ["threads_before"] = lifecycle.Before.Threads,
            ["threads_after"] = lifecycle.After.Threads,
            ["private_mib_before"] = Math.Round(lifecycle.Before.PrivateMiB, 1),
            ["private_mib_after"] = Math.Round(lifecycle.After.PrivateMiB, 1),
            ["working_set_mib_before"] = Math.Round(lifecycle.Before.WorkingSetMiB, 1),
            ["working_set_mib_after"] = Math.Round(lifecycle.After.WorkingSetMiB, 1),
            ["samples"] = lifecycle.Samples.Select(s => s.ToString()).ToArray(),
            ["verdict"] = lifecycle.Verdict,
        };
        if (!lifecycle.Pass)
            _failures.Add("physics lifecycle: " + lifecycle.Verdict);

        // (c) which joltc is loaded.
        using (var self = Process.GetCurrentProcess())
        {
            var joltc = self.Modules.Cast<ProcessModule>()
                .Where(m => m.ModuleName.StartsWith("joltc", StringComparison.OrdinalIgnoreCase))
                .Select(m => new Dictionary<string, object?> { ["path"] = m.FileName, ["sha256"] = Sha256(m.FileName) })
                .ToList();
            _result["joltc_loaded"] = joltc;
            foreach (var j in joltc)
                Log($"joltc loaded: {j["path"]} sha256={j["sha256"]}");
            if (joltc.Count != 1)
                _failures.Add($"expected exactly one loaded joltc module, found {joltc.Count}");
            else if (!string.Equals((string?)joltc[0]["sha256"], PatchedJoltc, StringComparison.OrdinalIgnoreCase))
                _failures.Add($"loaded joltc is NOT the patched build: {joltc[0]["sha256"]}");
        }

        // (f) Godot's own 3D physics is the Dummy server: the setting, and the server refusing to create anything.
        string engine = ProjectSettings.GetSetting("physics/3d/physics_engine").AsString();
        Rid space = PhysicsServer3D.SpaceCreate();
        Rid body = PhysicsServer3D.BodyCreate();
        bool worldSpaceValid = GetViewport().World3D.Space.IsValid;
        _result["godot_physics"] = new Dictionary<string, object?>
        {
            ["physics_3d_engine_setting"] = engine,
            ["space_create_valid"] = space.IsValid,
            ["body_create_valid"] = body.IsValid,
            ["world3d_space_valid"] = worldSpaceValid,
        };
        Log($"godot physics: setting={engine} space_create_valid={space.IsValid} body_create_valid={body.IsValid} world3d_space_valid={worldSpaceValid}");
        if (space.IsValid) PhysicsServer3D.FreeRid(space);
        if (body.IsValid) PhysicsServer3D.FreeRid(body);
        if (engine != "Dummy" || space.IsValid || body.IsValid || worldSpaceValid)
            _failures.Add("Godot 3D physics is not the Dummy server");

        return _failures.Count == 0 ? ExitPass : ExitFail;
    }

    // (h) ADR-019: engine text carrying a capability-shaped URL, a "$" digest and a session-id-shaped value reaches the
    // log only redacted. A dedicated logger into a capturing sink, next to Main's, so the check reads exactly what the
    // route wrote.
    private void EngineLogCheck()
    {
        const string url = "https://grid.invalid:9000/CAPS/0f1e2d3c-secret-path/";
        const string digest = "$1$0123456789abcdef0123456789abcdef";
        const string sid = "11111111-2222-3333-4444-555555555555";
        var sink = new CaptureSink();
        var probe = new EngineLogger(new EngineLogRoute(new CoreLog(sink, "s1")));
        OS.AddLogger(probe);
        string text = $"S1 engine-log probe {url} {digest} {sid}";
        GD.PushError(text);
        GD.PushWarning(text);
        GD.PrintErr(text);
        OS.RemoveLogger(probe);

        var lines = sink.Entries.Select(e => e.Format()).ToList();
        var events = sink.Entries.Select(e => e.Event).Distinct().ToList();
        bool leaked = lines.Any(l => l.Contains("/CAPS/") || l.Contains("secret-path") || l.Contains(digest) || l.Contains(sid));
        bool routed = events.Contains("engine.error") && events.Contains("engine.warning") && events.Contains("engine.stderr");
        bool redacted = lines.Any(l => l.Contains("[redacted:path]")) && lines.Any(l => l.Contains("[redacted:PasswordDigest]")) && lines.Any(l => l.Contains("[redacted:SessionId]"));
        _result["engine_log"] = new Dictionary<string, object?> { ["pass"] = routed && redacted && !leaked, ["events"] = events, ["leaked"] = leaked, ["lines"] = lines };
        Log($"engine log: routed={routed} redacted={redacted} leaked={leaked} events=[{string.Join(",", events)}]");
        if (!(routed && redacted && !leaked))
            _failures.Add("engine log route: routed=" + routed + " redacted=" + redacted + " leaked=" + leaked);
    }

    // (i) ADR-020: the login screen in English and pseudo-localised: every control and label inside the window and at
    // least its minimum size, every text (and placeholder) fully visible, every control with an accessible name.
    private async Task UiCheck()
    {
        var ui = new Dictionary<string, object?>();
        bool pass = true;
        foreach (var (name, table) in new[] { ("en", StringTable.English()), ("pseudo", StringTable.Pseudo()) })
        {
            var screen = new LoginScreen(table, null);
            AddChild(screen);
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var window = GetViewport().GetVisibleRect();
            var problems = new List<string>();
            var controls = screen.ById.Values.Concat(screen.Labels).Distinct().ToList();
            foreach (var c in controls)
            {
                var rect = c.GetGlobalRect();
                var min = c.GetCombinedMinimumSize();
                if (!window.Encloses(rect)) problems.Add($"{c.Name}: outside the window ({rect} in {window})");
                if (c.Size.X + 0.5f < min.X || c.Size.Y + 0.5f < min.Y) problems.Add($"{c.Name}: smaller than its minimum ({c.Size} < {min})");
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"{c.Name}: no accessible name");
                if (c is Label l && l.AutowrapMode == TextServer.AutowrapMode.Off)
                {
                    float w = l.GetThemeFont("font").GetStringSize(l.Text, HorizontalAlignment.Left, -1, l.GetThemeFontSize("font_size")).X;
                    if (w > l.Size.X + 0.5f) problems.Add($"{c.Name}: text clipped ({w} > {l.Size.X})");
                }
                if (c is LineEdit e && e.PlaceholderText.Length > 0)
                {
                    float w = e.GetThemeFont("font").GetStringSize(e.PlaceholderText, HorizontalAlignment.Left, -1, e.GetThemeFontSize("font_size")).X;
                    float room = e.Size.X - e.GetThemeStylebox("normal").GetMinimumSize().X;
                    if (w > room + 0.5f) problems.Add($"{c.Name}: placeholder clipped ({w} > {room})");
                }
            }
            ui[name] = new Dictionary<string, object?> { ["controls"] = controls.Count, ["window"] = window.ToString(), ["problems"] = problems };
            Log($"ui {name}: {controls.Count} controls, {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            RemoveChild(screen);
            screen.QueueFree();
        }
        ui["pass"] = pass;
        _result["ui"] = ui;
        if (!pass)
            _failures.Add("login screen layout or accessible names (see ui)");
    }

    // (i-a2) The interface font: Inter with the shipped Noto Sans attached for what Inter lacks, so a machine without the system fonts
    // still shows that text; a Devanagari letter (in Noto, not in Inter) resolves in the chain; and at each of the spec's type sizes a
    // line keeps Inter's own height (the engine sizes a line by the tallest font in a chain, so attaching Noto must not raise it).
    private void FontCheck()
    {
        var problems = new List<string>();
        var heights = new Dictionary<string, object?>();
        var theme = ShellTheme.Build(UiTheme.Tokens);
        var chain = theme.DefaultFont;
        var inter = Fonts.InterRegular;
        if (chain is null || inter is null)
            problems.Add("no interface font");
        else
        {
            if (!chain.GetFallbacks().Any(f => f.ResourceName == "NotoSans-Regular" || f.GetFontName().Contains("Noto", StringComparison.Ordinal)))
                problems.Add("Noto Sans is not attached to the interface font");
            if (!chain.HasChar(0x0915))
                problems.Add("a Devanagari letter has no font in the interface font's chain");
            static float Line(Font font, int size, string text)
            {
                var line = new TextLine();
                line.AddString(text, font, size);
                return line.GetSize().Y;
            }
            foreach (int size in new[] { 11, 12, 13, 14 })
            {
                float want = inter.GetHeight(size), got = chain.GetHeight(size);
                float latinWant = Line(inter, size, "Reverie Ag"), latinGot = Line(chain, size, "Reverie Ag"), mixedGot = Line(chain, size, "Reverie क");
                heights[size.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new[] { want, got, latinWant, latinGot, mixedGot };
                if (Math.Abs(got - want) > 0.5f)
                    problems.Add($"at {size} px the font is {got} high, Inter's is {want}");
                if (Math.Abs(latinGot - latinWant) > 0.5f || Math.Abs(mixedGot - latinWant) > 0.5f)
                    problems.Add($"at {size} px a line is {latinGot} high ({mixedGot} with Devanagari), Inter's is {latinWant}");
            }
        }
        _result["font"] = new Dictionary<string, object?> { ["pass"] = problems.Count == 0, ["problems"] = problems, ["heights"] = heights };
        if (problems.Count > 0)
            _failures.Add("interface font (see font)");
    }

    // (i-b) The window frame holding the login screen, in English and pseudo-localised: the menu bar, the main view and the
    // About floater inside the window, every control with an accessible name and its text whole, the menus carrying the
    // shell's entries, the floater opening and closing through its command, and the keyboard going to a text field and
    // back (Escape lets go of it).
    private async Task ShellCheck()
    {
        var results = new Dictionary<string, object?>();
        bool pass = true;
        foreach (var (name, table) in new[] { ("en", StringTable.English()), ("pseudo", StringTable.Pseudo()) })
        {
            var shell = new ShellRoot(new ShellController(table, () => { }));
            var login = new LoginScreen(table, null);
            shell.MainView.AddChild(login);
            AddChild(shell);
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var window = GetViewport().GetVisibleRect();
            var problems = new List<string>();
            var model = shell.Shell;

            // the top bar: the app menu first, then one title per menu of the model, each dropdown with every entry
            if (!window.Encloses(shell.TopBar.GetGlobalRect())) problems.Add($"top bar outside the window ({shell.TopBar.GetGlobalRect()})");
            if (shell.Menu.Titles.Count != model.Menus.Menus.Count + 1) problems.Add($"menu bar holds {shell.Menu.Titles.Count} menus, expected {model.Menus.Menus.Count + 1}");
            if (shell.Menu.Titles.Count > 0 && shell.Menu.Titles[0].Text != table.Get("topbar.app")) problems.Add($"the app menu reads {shell.Menu.Titles[0].Text}");
            if (shell.Menu.Titles.Count > 0 && shell.Menu.Filled(0).ItemCount != TopBarMenuMap.AppEntries.Length) problems.Add($"the app menu holds {shell.Menu.Filled(0).ItemCount} items");
            for (int m = 0; m < Math.Min(shell.Menu.Titles.Count - 1, model.Menus.Menus.Count); m++)
            {
                var def = model.Menus.Menus[m];
                if (shell.Menu.Titles[m + 1].Text != table.Get(def.LabelKey)) problems.Add($"menu {def.Id}: title {shell.Menu.Titles[m + 1].Text}");
                var popup = shell.Menu.Filled(m + 1);
                if (popup.Name != def.Id) problems.Add($"menu {def.Id}: its dropdown is named {popup.Name}");
                if (popup.ItemCount != def.Entries.Count) problems.Add($"menu {def.Id}: {popup.ItemCount} items, expected {def.Entries.Count}");
                for (int i = 0; i < popup.ItemCount; i++)
                    if (popup.GetItemText(i).Contains("[[")) problems.Add($"menu {def.Id}: item {i} has no string");
            }
            // the dock: hidden on the login screen (it would cover the form), every standard button there for the world, inside the
            // window, its names from the string table
            if (shell.Dock.Visible) problems.Add("the dock shows over the login screen");
            if (shell.Dock.Buttons.Count != shell.DockModel.Items.Count) problems.Add($"the dock shows {shell.Dock.Buttons.Count} buttons, expected {shell.DockModel.Items.Count}");
            if (!window.Encloses(shell.Dock.GetGlobalRect())) problems.Add($"dock outside the window ({shell.Dock.GetGlobalRect()})");
            foreach (var b in shell.Dock.Buttons.Values)
                if (b.Text.Contains("[[") || string.IsNullOrEmpty(b.AccessibilityName)) problems.Add($"dock {b.Name}: no string");
            var login0 = login.GetGlobalRect();
            if (!shell.MainView.GetGlobalRect().Encloses(login0)) problems.Add($"login screen outside the main view ({login0} in {shell.MainView.GetGlobalRect()})");

            // the About floater, opened through its command
            model.Commands.Execute("floater.about");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var frame = shell.Frames[ShellController.AboutFloaterId];
            if (!frame.Visible) problems.Add("the About floater did not open");
            var frameRect = frame.GetGlobalRect();
            if (!window.Encloses(frameRect)) problems.Add($"About floater outside the window ({frameRect} in {window})");
            var min = frame.GetCombinedMinimumSize();
            if (frame.Size.X + 0.5f < min.X || frame.Size.Y + 0.5f < min.Y) problems.Add($"About floater smaller than its minimum ({frame.Size} < {min})");
            foreach (var c in Descendants(frame).OfType<Control>().Where(c => c is Label or Button))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"{c.Name}: no accessible name");
            if (string.IsNullOrWhiteSpace(frame.AccessibilityName)) problems.Add("About floater: no accessible name");
            model.Commands.Execute("floater.about");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (frame.Visible) problems.Add("the About floater did not close");

            // the keyboard: a text field in use takes it from the avatar, Escape gives it back
            var first = login.ById["username"] as LineEdit;
            first!.GrabFocus();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!model.Focus.TextEntryActive) problems.Add("a focused text field was not seen as text entry");
            if (model.Focus.MovementAllowed) problems.Add("movement stayed allowed while typing");
            model.Focus.OnEscape();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (model.Focus.TextEntryActive) problems.Add("Escape did not let go of the text field");

            results[name] = new Dictionary<string, object?> { ["window"] = window.ToString(), ["problems"] = problems };
            Log($"shell {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["shell"] = results;
        if (!pass)
            _failures.Add("window frame layout, menus, floater or keyboard focus (see shell)");
    }

    // The keyboard's way to the menus, with key events sent through the engine's own input path as the operating system's arrive, in a
    // shell whose menus have never been opened: every shortcut the menus show (listed from the menu model, so a new entry is covered
    // without a new check) runs its entry when the entry is enabled and nothing when it is not, in and out of build mode; a check entry
    // (Build) toggles; while a text field holds the keyboard a Ctrl chord still runs its entry and a plain key is typed instead; Esc with
    // the right-click menu open closes the menu with no choice and never reaches the world's handling (which would reset the camera).
    private async Task KeysCheck()
    {
        var problems = new List<string>();
        var ran = new List<string>();
        var table = StringTable.English();
        var shell = new ShellRoot(new ShellController(table, () => ran.Add("app.quit")));
        var model = shell.Shell;
        bool selection = true;
        var edit = new Viewer.Shell.Build.EditController(new Viewer.Core.World.WorldState(new CoreLog(new KeysNoSink(), "s1")),
            _ => new Viewer.Core.Actions.ActionResult(Viewer.Core.Actions.ActionStatus.Sent, 1));
        BuildMenu.Install(model.Menus, () => true);
        BuildMenu.InstallEditing(model.Menus, () => true, () => edit, () => selection);
        IEnumerable<MenuEntry> Flat(IEnumerable<MenuEntry> es) => es.SelectMany(e => e.Children is { } k ? Flat(k).Prepend(e) : [e]);
        model.Menus.AddMenu("keys", "menu.help");
        foreach (var id in ViewerShortcuts.ByEntry.Keys)
            if (!Flat(model.Menus.Menus.SelectMany(m => m.Entries)).Any(e => e.Id == id))
                model.Menus.AddEntry("keys", new MenuEntry(id, "menu.help.about", "cmd." + id));
        foreach (var e in Flat(model.Menus.Menus.SelectMany(m => m.Entries)))
            if (e.CommandId.Length > 0 && !model.Commands.Has(e.CommandId))
            {
                string c = e.CommandId;
                // the entries with a check mark do what the viewer's commands do to the edit controller, so the mark can be checked
                Action act = c switch
                {
                    BuildMenu.ToggleCommand => edit.ToggleBuild,
                    BuildMenu.GridSnapCommand => edit.ToggleSnap,
                    BuildMenu.FocusCommand => () => edit.SelectTool(Viewer.Shell.Build.BuildTool.Focus),
                    BuildMenu.MoveCommand => () => edit.SelectTool(Viewer.Shell.Build.BuildTool.Move),
                    BuildMenu.EditCommand => () => edit.SelectTool(Viewer.Shell.Build.BuildTool.Edit),
                    _ => () => { },
                };
                model.Commands.Register(c, () => { ran.Add(c); act(); });
            }
        var panels = InteractFeature.Install(shell);
        AddChild(shell);
        var world = new S1KeySink();
        AddChild(world);
        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Press(Key key, bool ctrl, bool shift, bool alt)
        {
            long unicode = ctrl || alt ? 0 : key is >= Key.A and <= Key.Z ? (shift ? (long)key : char.ToLowerInvariant((char)key)) : (long)key;
            global::Godot.Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = (char)unicode, Pressed = true, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt });
            global::Godot.Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt });
            await Frames(2);
        }
        Task PressChord(KeyChord c) => Press((Key)char.ToUpperInvariant(c.Key[0]), c.Control, c.Shift, c.Alt);
        IReadOnlyList<TopMenuView> View() => TopBarMenuMap.Build(model.Menus, model.Commands);
        await Frames(3);
        GetViewport().GuiReleaseFocus();

        int tried = 0, enabledRuns = 0, disabledRuns = 0;
        var listed = MenuShortcuts.All(View());
        if (listed.Count < ViewerShortcuts.ByEntry.Count + 8) problems.Add($"only {listed.Count} shortcuts listed from the menus");
        // twice: out of build mode, then in it (the grid's keys work only there)
        foreach (bool building in new[] { false, true })
        {
            if (edit.BuildMode != building) edit.ToggleBuild();
            foreach (var (listedEntry, chord) in listed)
            {
                if (listedEntry.Id == "me.quit" && building) continue;
                var entry = FindView(View(), listedEntry.Id)!;
                ran.Clear();
                world.Keys.Clear();
                bool? checkedBefore = entry.Checked;
                await PressChord(chord);
                tried++;
                if (entry.Enabled)
                {
                    enabledRuns++;
                    if (ran.Count != 1 || ran[0] != entry.CommandId) problems.Add($"{chord.Key}{(chord.Control ? "+ctrl" : "")}{(chord.Shift ? "+shift" : "")}{(chord.Alt ? "+alt" : "")} ({entry.Id}, building {building}): ran [{string.Join(",", ran)}]");
                    if (world.Keys.Count > 0) problems.Add($"{entry.Id}: the key also reached the world");
                    if (checkedBefore is { } was)
                    {
                        // a tool is one of a group: its key marks it; Build and Snap turn on and off
                        bool tool = entry.CommandId is BuildMenu.FocusCommand or BuildMenu.MoveCommand or BuildMenu.EditCommand;
                        if (FindView(View(), entry.Id)?.Checked != (tool || !was)) problems.Add($"{entry.Id}: its check mark shows {FindView(View(), entry.Id)?.Checked} after its key (was {was})");
                        if (!tool)
                            await PressChord(chord);   // back as it was
                    }
                }
                else
                {
                    disabledRuns++;
                    if (ran.Count != 0) problems.Add($"{entry.Id} is disabled but its key ran [{string.Join(",", ran)}]");
                }
                if (edit.BuildMode != building) edit.ToggleBuild();
            }
        }
        if (shell.Menu.Popups.Any(p => p.ItemCount > 0 || p.Visible)) problems.Add("a dropdown was filled or opened during the key checks");

        // after every menu has been opened and closed once, a shortcut still runs its entry once only
        for (int m = 0; m < shell.Menu.Titles.Count; m++)
        {
            shell.Menu.ClickTitle(m);
            await Frames(2);
            shell.Menu.CloseMenus();
            await Frames(1);
        }
        if (!edit.BuildMode) edit.ToggleBuild();
        foreach (var (key, ctrl, command) in new[] { (Key.I, true, "cmd.content.inventory"), (Key.G, false, BuildMenu.GridSnapCommand), (Key.Key3, true, BuildMenu.EditCommand) })
        {
            ran.Clear();
            await Press(key, ctrl, shift: false, alt: false);
            if (ran.Count != 1 || ran[0] != command) problems.Add($"{key}{(ctrl ? "+ctrl" : "")} after the menus were opened ran [{string.Join(",", ran)}]");
        }

        // a text field holds the keyboard: Ctrl+I still opens what the menu's entry opens; G (in build mode, so enabled) is typed
        if (!edit.BuildMode) edit.ToggleBuild();
        var field = new LineEdit { Name = "keys_field" };
        shell.MainView.AddChild(field);
        await Frames(1);
        field.GrabFocus();
        await Frames(2);
        ran.Clear();
        await Press(Key.I, ctrl: true, shift: false, alt: false);
        if (!ran.Contains("cmd.content.inventory")) problems.Add($"Ctrl+I while typing ran [{string.Join(",", ran)}]");
        ran.Clear();
        await Press(Key.G, ctrl: false, shift: false, alt: false);
        if (ran.Count != 0) problems.Add($"G while typing ran [{string.Join(",", ran)}]");
        if (field.Text != "g") problems.Add($"G while typing: the field holds '{field.Text}'");
        field.ReleaseFocus();
        shell.MainView.RemoveChild(field);
        field.QueueFree();
        await Frames(1);

        // Esc with the right-click menu open: the menu closes with no choice, the world never sees the key
        int dismissed = 0;
        string? chosen = null;
        panels.Menu.Dismissed += () => dismissed++;
        panels.Menu.Open(new Vector2(200, 200), [new ContextEntry("touch", "menu.help.about")], id => chosen = id, GetViewport().GetVisibleRect().Size);
        await Frames(2);
        world.Keys.Clear();
        await Press(Key.Escape, ctrl: false, shift: false, alt: false);
        if (panels.Menu.IsOpen) problems.Add("Esc left the right-click menu open");
        if (dismissed != 1) problems.Add($"Esc closed the right-click menu {dismissed} times as dismissed");
        if (chosen is not null) problems.Add($"Esc chose {chosen}");
        if (world.Keys.Contains(Key.Escape)) problems.Add("Esc with the right-click menu open reached the world (the camera would reset)");
        // with no menu open, Esc goes on to the world as before
        world.Keys.Clear();
        await Press(Key.Escape, ctrl: false, shift: false, alt: false);
        if (!world.Keys.Contains(Key.Escape)) problems.Add("Esc with no menu open did not reach the world");

        _result["keys"] = new Dictionary<string, object?> { ["shortcuts"] = listed.Count, ["presses"] = tried, ["enabled"] = enabledRuns, ["disabled"] = disabledRuns, ["problems"] = problems };
        Log($"keys: {listed.Count} shortcuts, {tried} presses ({enabledRuns} enabled, {disabledRuns} disabled), {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
        RemoveChild(world);
        world.QueueFree();
        RemoveChild(shell);
        shell.QueueFree();
        if (problems.Count > 0 || enabledRuns == 0 || disabledRuns == 0)
            _failures.Add("menu shortcuts or Esc through the engine's key input (see keys)");
    }

    private static TopMenuEntryView? FindView(IEnumerable<TopMenuView> menus, string id)
    {
        static IEnumerable<TopMenuEntryView> Flat(IEnumerable<TopMenuEntryView> es) => es.SelectMany(e => e.Children is { } k ? Flat(k).Prepend(e) : [e]);
        return Flat(menus.SelectMany(m => m.Entries)).FirstOrDefault(e => e.Id == id);
    }

    private sealed class KeysNoSink : ILogSink
    {
        public void Write(LogEntry entry) { }
    }

    // Chat and conversations, with no grid: the feature installs itself into a shell already drawn (menu and toolbar drawn
    // again), both floaters open inside the window with every control named, a synthetic heard line shows with its name and
    // with text that tried to add formatting shown as typed, a typing mark shows, the input takes the keyboard from the avatar,
    // Enter, Shift+Enter and Ctrl+Enter send say, whisper and shout through the submit function, and an arriving message opens
    // the conversations floater with a tab carrying its unread count.
    private async Task ChatCheck()
    {
        var results = new Dictionary<string, object?>();
        bool pass = true;
        foreach (var (name, table) in new[] { ("en", StringTable.English()), ("pseudo", StringTable.Pseudo()) })
        {
            var problems = new List<string>();
            var shell = new ShellRoot(new ShellController(table, () => { }));
            AddChild(shell);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var model = shell.Shell;
            int menusBefore = shell.Menu.Titles.Count;
            var settings = new SettingsService(null);
            var (nearby, conversations) = ChatFeature.Install(shell, settings);
            var self = Guid.NewGuid();
            var other = Guid.NewGuid();
            var sent = new List<Viewer.Core.Actions.ActionIntent>();
            var names = new Viewer.Core.Social.NameCache();
            var hub = new Viewer.Shell.Chat.CommsHub(self, names, i => { sent.Add(i); return new Viewer.Core.Actions.ActionResult(Viewer.Core.Actions.ActionStatus.Sent, sent.Count); },
                settings, () => DateTimeOffset.Now);
            nearby.Bind(hub);
            conversations.Bind(hub);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (shell.Menu.Titles.Count != menusBefore + 1) problems.Add($"menu bar holds {shell.Menu.Titles.Count} menus after the feature added one (was {menusBefore})");
            if (shell.Dock.Buttons.Count != shell.DockModel.Items.Count) problems.Add($"dock: {shell.Dock.Buttons.Count} buttons, expected {shell.DockModel.Items.Count}");

            model.Commands.Execute("floater.chat");
            hub.Enqueue(new ChatReceived(null, other, other, "Example Resident", ChatSource.Agent, ChatLineType.Say, ChatAudible.Fully, System.Numerics.Vector3.Zero,
                new Viewer.Core.Diagnostics.MessageBody("hello [b]there[/b]", Viewer.Core.Diagnostics.MessageChannel.LocalChat)));
            hub.Enqueue(new ChatReceived(null, Guid.NewGuid(), Guid.Empty, "Typing Resident", ChatSource.Agent, ChatLineType.StartTyping, ChatAudible.Fully, System.Numerics.Vector3.Zero,
                new Viewer.Core.Diagnostics.MessageBody("", Viewer.Core.Diagnostics.MessageChannel.LocalChat)));
            hub.Pump();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var window = GetViewport().GetVisibleRect();
            var frame = shell.Frames[ChatFeature.NearbyId];
            if (!frame.Visible) problems.Add("nearby chat did not open");
            if (!window.Encloses(frame.GetGlobalRect())) problems.Add($"nearby chat outside the window ({frame.GetGlobalRect()})");
            string shown = nearby.HistoryView.GetParsedText();
            if (!shown.Contains("Example Resident") || !shown.Contains("hello [b]there[/b]")) problems.Add($"history shows '{shown}'");
            if (!nearby.TypingLabel.Text.Contains("Typing Resident")) problems.Add($"typing mark shows '{nearby.TypingLabel.Text}'");
            foreach (var c in Descendants(frame).OfType<Control>().Where(c => c is Label or Button or LineEdit or RichTextLabel or OptionButton))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"nearby {c.Name}: no accessible name");

            nearby.Input.GrabFocus();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (model.Focus.MovementAllowed) problems.Add("movement stayed allowed while typing in chat");
            foreach (var (text, shift, ctrl) in new[] { ("one", false, false), ("two", true, false), ("three", false, true) })
            {
                nearby.Input.Text = text;
                nearby.Input.EmitSignal(LineEdit.SignalName.GuiInput, new InputEventKey { Keycode = Key.Enter, Pressed = true, ShiftPressed = shift, CtrlPressed = ctrl });
            }
            var says = sent.OfType<Viewer.Core.Actions.SayIntent>().ToList();
            var expected = new[] { new Viewer.Core.Actions.SayIntent("one", 0, ChatKind.Say), new Viewer.Core.Actions.SayIntent("two", 0, ChatKind.Whisper), new Viewer.Core.Actions.SayIntent("three", 0, ChatKind.Shout) };
            if (!says.SequenceEqual(expected)) problems.Add($"keys sent {string.Join(", ", says)}");
            if (nearby.Input.Text.Length != 0) problems.Add("the input was not cleared after a send");
            model.Focus.OnEscape();

            hub.Enqueue(new InstantMessageReceived(other, "Example Resident", self, Viewer.Core.Social.ImSessionId.For(self, other), ImKind.Message, 0, false, null, false,
                new Viewer.Core.Diagnostics.MessageBody("a private hello", Viewer.Core.Diagnostics.MessageChannel.InstantMessage)));
            model.Floaters.Focus(ChatFeature.NearbyId);
            hub.Pump();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var imFrame = shell.Frames[ChatFeature.ConversationsId];
            if (!imFrame.Visible) problems.Add("an arriving message did not open the conversations");
            if (!window.Encloses(imFrame.GetGlobalRect())) problems.Add($"conversations outside the window ({imFrame.GetGlobalRect()})");
            if (conversations.Tabs.TabCount != 1 || !conversations.Tabs.GetTabTitle(0).Contains("Example Resident")) problems.Add($"tabs: {conversations.Tabs.TabCount}");
            if (!conversations.HistoryView.GetParsedText().Contains("a private hello")) problems.Add("the conversation does not show its line");
            foreach (var c in Descendants(imFrame).OfType<Control>().Where(c => c is Label or Button or LineEdit or RichTextLabel or TabBar))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"conversations {c.Name}: no accessible name");

            results[name] = problems;
            Log($"chat {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            nearby.Bind(null);
            conversations.Bind(null);
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["chat"] = results;
        if (!pass)
            _failures.Add("chat and conversations floaters (see chat)");
    }

    // Friends, groups and group notices, with no grid: both features install into a shell already drawn, the people floater opens
    // inside the window with every control named, the friend list shows an online mark, an offer waits for the user (nothing
    // is answered by itself), Accept answers through the submit function, Message opens the conversation with that friend,
    // the rights boxes send the rights (the edit-objects right only after a confirmation), a group chat joins and shows as a
    // tab, and a group notice's attachment is offered and answered only when the user presses a button. Text that tries to
    // add formatting shows as typed. English and pseudo-localised, on this driver.
    private async Task PeopleCheck()
    {
        var results = new Dictionary<string, object?>();
        bool pass = true;
        foreach (var (name, table) in new[] { ("en", StringTable.English()), ("pseudo", StringTable.Pseudo()) })
        {
            var problems = new List<string>();
            var shell = new ShellRoot(new ShellController(table, () => { }));
            AddChild(shell);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var model = shell.Shell;
            var settings = new SettingsService(null);
            var (nearby, conversations) = ChatFeature.Install(shell, settings);
            var people = PeopleFeature.Install(shell, settings, conversations);
            var self = Guid.NewGuid();
            Guid alpha = Guid.NewGuid(), beta = Guid.NewGuid(), offerer = Guid.NewGuid(), group = Guid.NewGuid(), offerId = Guid.NewGuid();
            var sent = new List<Viewer.Core.Actions.ActionIntent>();
            var names = new Viewer.Core.Social.NameCache();
            names.Learn(alpha, "Alpha Friend");
            names.Learn(beta, "Beta Friend");
            var hub = new Viewer.Shell.Chat.CommsHub(self, names, i => { sent.Add(i); return new Viewer.Core.Actions.ActionResult(Viewer.Core.Actions.ActionStatus.Sent, sent.Count); },
                settings, () => DateTimeOffset.Now, null, n => PeopleFeature.NoticeText(n, table, names));
            nearby.Bind(hub);
            conversations.Bind(hub);
            people.Bind(hub);
            Viewer.Core.Diagnostics.MessageBody Body(string t) => new(t, Viewer.Core.Diagnostics.MessageChannel.InstantMessage);
            hub.Enqueue(new FriendsListed([
                new FriendEntry(alpha, "Alpha Friend", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline),
                new FriendEntry(beta, "Beta Friend", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap)]));
            hub.Enqueue(new FriendPresenceChanged(alpha, true));
            hub.Enqueue(new FriendshipOffered(offerer, "Offering Resident", offerId, true, Body("be my [b]friend[/b]")));
            hub.Enqueue(new GroupsListed([new GroupEntry(group, "Example Group", true, true)]));
            hub.Enqueue(new ActiveGroupChanged(group, "Example Group", "Member"));
            hub.Enqueue(new GroupNoticeReceived(group, offerer, "Offering Resident", Body("A [b]subject[/b]"), Body("The notice [b]text[/b]"), true, "A Gift", 6, Guid.NewGuid(), null, false));
            hub.Pump();
            model.Commands.Execute("floater.people");
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var window = GetViewport().GetVisibleRect();
            var frame = shell.Frames[PeopleFeature.PeopleId];
            if (!frame.Visible) problems.Add("the people floater did not open");
            if (!window.Encloses(frame.GetGlobalRect())) problems.Add($"people outside the window ({frame.GetGlobalRect()})");
            if (shell.Dock.Buttons.Count != shell.DockModel.Items.Count) problems.Add($"the dock holds {shell.Dock.Buttons.Count} buttons, expected {shell.DockModel.Items.Count}");
            foreach (var c in Descendants(frame).OfType<Control>().Where(c => c is Label or Button or CheckBox or ItemList or OptionButton or TabContainer))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"people {c.Name}: no accessible name");

            // the list and the online mark
            if (people.FriendList.ItemCount != 2) problems.Add($"friend list holds {people.FriendList.ItemCount} rows");
            else if (people.FriendList.GetItemText(0) == people.FriendList.GetItemText(1).Replace("Beta", "Alpha")) problems.Add("the online friend is not marked");
            if (!nearby.HistoryView.GetParsedText().Contains("Alpha Friend")) problems.Add("no online line in nearby chat");

            // an offer waits; nothing is answered by itself
            if (people.OffersBox.GetChildCount() != 1) problems.Add($"offers shown: {people.OffersBox.GetChildCount()}");
            if (sent.OfType<Viewer.Core.Actions.FriendshipAnswerIntent>().Any()) problems.Add("an offer was answered without the user");
            if (people.OffersBox.GetChildCount() == 1 && Descendants(people.OffersBox).OfType<Label>().FirstOrDefault() is { } offerText && !offerText.Text.Contains("[b]friend[/b]"))
                problems.Add($"the offer text is not shown as typed: '{offerText.Text}'");
            if (Descendants(people.OffersBox).OfType<Button>().FirstOrDefault(b => b.Name == "accept") is { } accept)
                accept.EmitSignal(BaseButton.SignalName.Pressed);
            else
                problems.Add("no accept button on the offer");
            if (!sent.OfType<Viewer.Core.Actions.FriendshipAnswerIntent>().Any(a => a.From == offerer && a.Accept)) problems.Add("Accept did not answer through the pipeline");

            // a friend selected: message opens the conversation, the rights send
            people.FriendList.Select(0);
            people.FriendList.EmitSignal(ItemList.SignalName.ItemSelected, 0);
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            people.MessageButton.EmitSignal(BaseButton.SignalName.Pressed);
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (conversations.Tabs.TabCount != 1 || !conversations.Tabs.GetTabTitle(0).Contains("Alpha Friend")) problems.Add($"Message opened {conversations.Tabs.TabCount} tabs");
            if (!shell.Frames[ChatFeature.ConversationsId].Visible) problems.Add("Message did not show the conversations");
            if (conversations.OfferButton.Visible) problems.Add("the offer-friendship button shows for a friend");
            people.RightMap.ButtonPressed = true;
            if (!sent.OfType<Viewer.Core.Actions.FriendRightsIntent>().Any(r => r.Friend == alpha && r.Rights == (FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap)))
                problems.Add("the map box did not send the rights");
            int rightsBefore = sent.OfType<Viewer.Core.Actions.FriendRightsIntent>().Count();
            people.RightModify.ButtonPressed = true;
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (sent.OfType<Viewer.Core.Actions.FriendRightsIntent>().Count() != rightsBefore) problems.Add("the edit-objects right was sent before the user confirmed");
            if (!people.ConfirmBar.Visible) problems.Add("no confirmation was asked for the edit-objects right");
            people.ConfirmYes.EmitSignal(BaseButton.SignalName.Pressed);
            if (!sent.OfType<Viewer.Core.Actions.FriendRightsIntent>().Any(r => r.Rights.HasFlag(FriendRightsFlags.ModifyObjects) && r.ModifyConfirmed)) problems.Add("the confirmed right was not sent");

            // groups
            people.Tabs.CurrentTab = 1;
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (people.GroupList.ItemCount != 1) problems.Add($"group list holds {people.GroupList.ItemCount} rows");
            if (!people.ActiveLabel.Text.Contains("Example Group")) problems.Add($"active group shows '{people.ActiveLabel.Text}'");
            people.GroupList.Select(0);
            people.GroupList.EmitSignal(ItemList.SignalName.ItemSelected, 0);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            people.GroupChatButton.EmitSignal(BaseButton.SignalName.Pressed);
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!sent.OfType<Viewer.Core.Actions.GroupChatJoinIntent>().Any(j => j.Group == group)) problems.Add("the group chat button did not join");
            if (conversations.Tabs.TabCount != 2 || !conversations.Tabs.GetTabTitle(1).Contains("Example Group")) problems.Add($"group chat tabs: {conversations.Tabs.TabCount}");

            // notices
            people.Tabs.CurrentTab = 2;
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (people.NoticeList.ItemCount != 1) problems.Add($"notice list holds {people.NoticeList.ItemCount} rows");
            if (sent.OfType<Viewer.Core.Actions.GroupNoticeAnswerIntent>().Any()) problems.Add("an attachment was answered without the user");
            people.NoticeList.Select(0);
            people.NoticeList.EmitSignal(ItemList.SignalName.ItemSelected, 0);
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!people.NoticeBody.Text.Contains("[b]text[/b]")) problems.Add($"the notice text is not shown as typed: '{people.NoticeBody.Text}'");
            if (!people.AttachmentAccept.Visible || people.AttachmentAccept.Disabled) problems.Add("the attachment is not offered");
            people.AttachmentAccept.EmitSignal(BaseButton.SignalName.Pressed);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (sent.OfType<Viewer.Core.Actions.GroupNoticeAnswerIntent>().Count(a => a.Accept) != 1) problems.Add("accepting the attachment did not send exactly one answer");
            if (!people.AttachmentAccept.Disabled) problems.Add("the attachment can be answered twice");

            results[name] = problems;
            Log($"people {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            nearby.Bind(null);
            conversations.Bind(null);
            people.Bind(null);
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["people"] = results;
        if (!pass)
            _failures.Add("friends, groups and notices floater (see people)");
    }

    // Notifications and what scripts ask, with no grid: the feature installs into a shell already drawn; a dialog of twelve buttons draws
    // them in rows of three with the first button bottom-left and Ignore on a row below, a click answers with that button through the
    // pipeline and Ignore sends nothing; a text box sends the typed line; a permission request names each permission and Yes answers
    // with exactly those; a web address opens nothing until Go to page is pressed; a long message scrolls inside a limited area; the
    // list floater opens; every control is named and every panel is inside the window. English and pseudo-localised, on this driver.
    private async Task NotificationsCheck()
    {
        var results = new Dictionary<string, object?>();
        bool pass = true;
        foreach (var (name, table) in new[] { ("en", StringTable.English()), ("pseudo", StringTable.Pseudo()) })
        {
            var problems = new List<string>();
            var shell = new ShellRoot(new ShellController(table, () => { }));
            AddChild(shell);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var model = shell.Shell;
            ChatFeature.Install(shell, new SettingsService(null));
            var panels = NotificationFeature.Install(shell);
            var sent = new List<Viewer.Core.Actions.ActionIntent>();
            double now = 100;
            var hub = new Viewer.Shell.Notifications.NotificationHub(i => { sent.Add(i); return new Viewer.Core.Actions.ActionResult(Viewer.Core.Actions.ActionStatus.Sent, sent.Count); },
                () => now, () => "Test User");
            panels.Toasts.Bind(hub);
            panels.List.Bind(hub);
            Guid obj = Guid.NewGuid(), owner = Guid.NewGuid(), task = Guid.NewGuid(), item = Guid.NewGuid();
            var sim = new Viewer.Core.Session.SimRef(1099511628032256UL, "Example Region");
            Viewer.Core.Diagnostics.MessageBody Said(string t) => new(t, Viewer.Core.Diagnostics.MessageChannel.LocalChat);
            var labels = Enumerable.Range(1, 12).Select(i => "b" + i).ToArray();
            hub.Enqueue(new Viewer.Core.Session.ScriptDialogReceived(sim, obj, owner, "Vendor [b]one[/b]", "Some Owner", false, Said("Pick one"), -77, labels));
            hub.Pump();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var window = GetViewport().GetVisibleRect();
            Button B(Node root, string n) => Descendants(root).OfType<Button>().FirstOrDefault(b => b.Name == n)!;
            var toasts = panels.Toasts.Stack.GetChildren().OfType<PanelContainer>().ToList();
            if (toasts.Count != 1) problems.Add($"{toasts.Count} toasts for one dialog");
            else
            {
                var dialog = toasts[0];
                if (!window.Encloses(dialog.GetGlobalRect())) problems.Add($"the dialog is outside the window ({dialog.GetGlobalRect()})");
                var buttons = Enumerable.Range(0, 12).Select(i => B(dialog, "button_" + i)).ToArray();
                if (buttons.Any(b => b is null)) problems.Add("not all twelve buttons are there");
                else
                {
                    // the first button bottom-left, rows of three added upward, the partial row (none here) on top; Ignore below them all
                    if (!(buttons[0].GlobalPosition.X < buttons[1].GlobalPosition.X && buttons[1].GlobalPosition.X < buttons[2].GlobalPosition.X)) problems.Add("the first row does not run left to right");
                    if (Math.Abs(buttons[0].GlobalPosition.Y - buttons[2].GlobalPosition.Y) > 1) problems.Add("buttons one to three are not on one row");
                    if (!(buttons[9].GlobalPosition.Y < buttons[6].GlobalPosition.Y && buttons[6].GlobalPosition.Y < buttons[3].GlobalPosition.Y && buttons[3].GlobalPosition.Y < buttons[0].GlobalPosition.Y))
                        problems.Add("the rows are not added upward from the first button");
                    if (Math.Abs(buttons[0].GlobalPosition.X - buttons[9].GlobalPosition.X) > 1) problems.Add("the first column does not line up");
                    var ignore = B(dialog, "ignore");
                    if (ignore is null) problems.Add("no Ignore button");
                    else if (ignore.GlobalPosition.Y <= buttons[0].GlobalPosition.Y) problems.Add("Ignore is not below the buttons");
                    if (buttons[0].Text != "b1" || buttons[11].Text != "b12") problems.Add("the buttons do not carry the script's labels in order");
                }
                if (Descendants(dialog).OfType<Label>().Any(l => l.Name == "title" && l.Text.Contains("[b]") == false)) problems.Add("the object's name with brackets is not shown as typed");
                foreach (var c in Descendants(dialog).OfType<Control>().Where(c => c is Label or Button or LineEdit))
                    if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"dialog {c.Name}: no accessible name");
                if (sent.Count != 0) problems.Add("something was sent before the user chose");
                if (buttons.All(b => b is not null))
                {
                    buttons[4].EmitSignal(BaseButton.SignalName.Pressed);
                    if (sent.OfType<Viewer.Core.Actions.ScriptDialogReplyIntent>().SingleOrDefault() is not { } reply
                        || reply.ButtonIndex != 4 || reply.Label != "b5" || reply.Channel != -77 || reply.ObjectId != obj || !ReferenceEquals(reply.Sim, sim))
                        problems.Add("the click did not answer with button five on the script's channel");
                }
            }

            // Ignore answers nothing; a message that is long scrolls inside a limited area
            hub.Enqueue(new Viewer.Core.Session.ScriptDialogReceived(sim, obj, owner, "Vendor", "Some Owner", false, Said(string.Join("\n", Enumerable.Repeat("a long line of text", 40))), 1, ["OK"]));
            hub.Pump();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            int before = sent.Count;
            var second = panels.Toasts.Stack.GetChildren().OfType<PanelContainer>().FirstOrDefault();
            if (second is null) problems.Add("the second dialog is not shown");
            else
            {
                var scroll = Descendants(second).OfType<ScrollContainer>().FirstOrDefault(c => c.Name == "message_scroll");
                if (scroll is null || scroll.CustomMinimumSize.Y > NotificationView.MaxMessageHeight) problems.Add("a long message does not scroll in a limited area");
                B(second, "ignore").EmitSignal(BaseButton.SignalName.Pressed);
                if (sent.Count != before) problems.Add("Ignore sent something");
                if (hub.Model.List.Count != 0) problems.Add("Ignore left the dialog open");
            }

            // a text box
            hub.Enqueue(new Viewer.Core.Session.ScriptDialogReceived(sim, obj, owner, "Box", "Some Owner", false, Said("Type"), 9, [Viewer.Shell.Notifications.ScriptPrompts.TextBoxMarker]));
            hub.Pump();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var tb = panels.Toasts.Stack.GetChildren().OfType<PanelContainer>().FirstOrDefault();
            const string typedLine = "hello there";
            var field = tb is null ? null : Descendants(tb).OfType<LineEdit>().FirstOrDefault(l => l.Name == "reply");
            if (field is null) problems.Add("no text field for a text box");
            else
            {
                field.Text = typedLine;
                B(tb!, "submit").EmitSignal(BaseButton.SignalName.Pressed);
                if (sent.OfType<Viewer.Core.Actions.ScriptDialogReplyIntent>().LastOrDefault() is not { } r || r.Label != typedLine || r.ButtonIndex != 0 || r.Channel != 9)
                    problems.Add("Submit did not send the typed line");
            }

            // a permission request: each permission in words, Yes answers with exactly those, nothing before
            hub.Enqueue(new Viewer.Core.Session.ScriptPermissionRequested(sim, task, item, "Gadget", "Some Owner", (1 << 4) | (1 << 2) | (1 << 25)));
            hub.Pump();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var perm = panels.Toasts.Stack.GetChildren().OfType<PanelContainer>().FirstOrDefault();
            if (perm is null) problems.Add("no permission request shown");
            else
            {
                int lines = Descendants(perm).OfType<Label>().Count(l => l.Name.ToString().StartsWith("detail"));
                if (lines != 2) problems.Add($"the request names {lines} permissions, expected 2");
                if (sent.OfType<Viewer.Core.Actions.ScriptPermissionAnswerIntent>().Any()) problems.Add("a request was answered without the user");
                B(perm, "button_0").EmitSignal(BaseButton.SignalName.Pressed);
                if (sent.OfType<Viewer.Core.Actions.ScriptPermissionAnswerIntent>().SingleOrDefault() is not { } a || a.Granted != ((1 << 4) | (1 << 2)) || a.TaskId != task)
                    problems.Add("Yes did not grant exactly the known permissions");
            }

            // a web address opens nothing until the user chooses
            hub.Enqueue(new Viewer.Core.Session.LoadUrlRequested(sim, obj, owner, false, "Sign", Said("Visit us"), "https://example.org/shop"));
            hub.Pump();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var url = panels.Toasts.Stack.GetChildren().OfType<PanelContainer>().FirstOrDefault();
            if (url is null || !Descendants(url).OfType<Label>().Any(l => l.Text.Contains("https://example.org/shop"))) problems.Add("the address is not shown");
            if (sent.OfType<Viewer.Core.Actions.OpenUrlIntent>().Any()) problems.Add("an address was opened without the user");
            if (url is not null)
            {
                B(url, "button_0").EmitSignal(BaseButton.SignalName.Pressed);
                if (!sent.OfType<Viewer.Core.Actions.OpenUrlIntent>().Any(o => o.Url == "https://example.org/shop")) problems.Add("Go to page did not ask to open the address");
            }

            // the list floater
            hub.Enqueue(new Viewer.Core.Session.ScriptDialogReceived(sim, obj, owner, "Waiting", "Some Owner", false, Said("still here"), 1, ["Yes"]));
            hub.Pump();
            model.Commands.Execute("floater.notifications");
            for (int i = 0; i < 4; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var frame = shell.Frames[NotificationFeature.ListId];
            if (!frame.Visible) problems.Add("the notifications floater did not open");
            if (!window.Encloses(frame.GetGlobalRect())) problems.Add($"the notifications floater is outside the window ({frame.GetGlobalRect()})");
            if (panels.List.Rows.GetChildren().OfType<PanelContainer>().Count() != 1) problems.Add($"the list shows {panels.List.Rows.GetChildren().OfType<PanelContainer>().Count()} waiting notifications, expected 1");
            if (hub.Model.UnreadCount != 0) problems.Add("opening the list did not read it");
            foreach (var c in Descendants(frame).OfType<Control>().Where(c => c is Button or LineEdit))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"list {c.Name}: no accessible name");

            results[name] = problems;
            Log($"notifications {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            panels.Toasts.Bind(null);
            panels.List.Bind(null);
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["notifications"] = results;
        if (!pass)
            _failures.Add("notifications and script prompts (see notifications)");
    }


    // Teleport, the world map and the mini-map, with no grid: the feature installs into a shell already drawn, the World menu and the
    // toolbar gain their entries, the teleport floater and the progress panel work (an offer waits and is answered only by a button,
    // progress and failure show in words, Cancel and Close act), the world map decodes a tile picture and draws, a click chooses a
    // spot and its button teleports, and the mini-map draws the region, a friend's dot and a stranger's, zooms and turns. Every
    // control is named and inside the window. English and pseudo-localised, on this driver.
    private async Task TravelCheck()
    {
        var results = new Dictionary<string, object?>();
        bool pass = true;
        foreach (var (name, table) in new[] { ("en", StringTable.English()), ("pseudo", StringTable.Pseudo()) })
        {
            var problems = new List<string>();
            var shell = new ShellRoot(new ShellController(table, () => { }));
            AddChild(shell);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var model = shell.Shell;
            var settings = new SettingsService(null);
            var panels = TravelFeature.Install(shell, settings);
            var self = Guid.NewGuid();
            Guid friend = Guid.NewGuid(), stranger = Guid.NewGuid(), offerer = Guid.NewGuid(), lure = Guid.NewGuid();
            var sent = new List<Viewer.Core.Actions.ActionIntent>();
            var names = new Viewer.Core.Social.NameCache();
            names.Learn(offerer, "Offering Resident");
            var asks = new List<string>();
            // a small picture stands for a map tile
            var picture = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8);
            picture.Fill(new Color(0.2f, 0.5f, 0.3f));
            byte[] jpeg = picture.SaveJpgToBuffer();
            var tiles = new Viewer.Core.Map.MapTileCache((k, ct) => Task.FromResult(Viewer.Core.Assets.AssetFetchResult.Ok(jpeg)));
            var links = new Viewer.Shell.Travel.MapLinks
            {
                RequestName = n => asks.Add(n),
                RequestTile = tiles.Request,
                Own = () => (Viewer.Core.Map.MapMath.HandleOf(1001, 1001), new System.Numerics.Vector3(128, 128, 30)),
            };
            var hub = new Viewer.Shell.Travel.TravelHub(self, names, i => { sent.Add(i); return new Viewer.Core.Actions.ActionResult(Viewer.Core.Actions.ActionStatus.Sent, sent.Count); },
                settings, () => DateTimeOffset.Now, links);
            var friends = new List<Viewer.Shell.Social.FriendRow> { new(friend, "Alpha Friend", true, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap) };
            panels.Overlay.Bind(hub);
            panels.Teleport.Bind(hub, () => friends);
            panels.Map.Bind(hub, tiles, () => friends);
            var coarse = new List<CoarseAvatar> { new(friend, new System.Numerics.Vector3(140, 150, 30)), new(stranger, new System.Numerics.Vector3(110, 100, 1020)) };
            panels.Mini.Bind(hub, () => new Viewer.Shell.Travel.MiniMapInput(self, new System.Numerics.Vector3(128, 128, 30), 0.5f, 1.2f, 256, 256, coarse, id => id == friend));
            hub.Enqueue(new MapBlockSize(1001, 1001, 256, 256, "Example Region", Guid.NewGuid(), 13));
            hub.Enqueue(new TeleportOffered(offerer, "Offering Resident", lure, new Viewer.Core.Diagnostics.MessageBody("come [b]over[/b]", Viewer.Core.Diagnostics.MessageChannel.InstantMessage)));
            hub.Pump();

            async Task Frames(int n)
            {
                for (int i = 0; i < n; i++)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            // the menu and the toolbar
            var envChoice = new Viewer.Shell.EnvironmentChoice(null);
            Viewer.Shell.EnvironmentMenu.Install(model.Menus, model.Commands, envChoice, () => { });
            await Frames(2);
            var world = model.Menus.Menus.FirstOrDefault(m => m.Id == "world");
            if (world is null || world.Entries.Count != 6) problems.Add($"the World menu holds {world?.Entries.Count ?? 0} entries, expected 6");
            // the Environment submenu: its own popup with the official items, a check on the one in force, the choice made through the command
            int worldIndex = shell.Menu.Menus.ToList().FindIndex(m => m.Id == "world");
            PopupMenu? Env() => worldIndex < 0 ? null : shell.Menu.Filled(worldIndex).GetNodeOrNull<PopupMenu>("world_environment");   // as the menu opens
            var envPopup = Env();
            if (envPopup is null || envPopup.ItemCount != 8) problems.Add($"the Environment submenu holds {envPopup?.ItemCount ?? 0} items, expected 8");
            else
            {
                string[] want = ["menu.env.sunrise", "menu.env.midday", "menu.env.legacymidday", "menu.env.sunset", "menu.env.midnight", "menu.env.region", "menu.env.personal", "menu.env.pauseclouds"];
                for (int i = 0; i < 8; i++) if (envPopup.GetItemText(i) != table.Get(want[i])) problems.Add($"Environment item {i} reads '{envPopup.GetItemText(i)}', expected '{table.Get(want[i])}'");
                if (!envPopup.IsItemChecked(5)) problems.Add("Use Shared Environment is not checked with nothing chosen");
                model.Menus.Activate("env.midnight", model.Commands);
                envPopup = Env()!;
                if (envChoice.Current != Viewer.Core.World.SkyChoice.Midnight || !envPopup.IsItemChecked(4) || envPopup.IsItemChecked(5)) problems.Add("choosing Midnight did not move the check mark");
                model.Menus.Activate("env.legacymidday", model.Commands);
                envPopup = Env()!;
                if (envChoice.Current != Viewer.Core.World.SkyChoice.LegacyMidday || !envPopup.IsItemChecked(2) || envPopup.IsItemChecked(4)) problems.Add("choosing Midday (Legacy) did not move the check mark");
                model.Menus.Activate("env.pauseclouds", model.Commands);
                envPopup = Env()!;
                if (!envChoice.CloudsPaused || !envPopup.IsItemChecked(7)) problems.Add("Pause Clouds did not pause and check");
                model.Menus.Activate("env.pauseclouds", model.Commands);
                model.Menus.Activate("env.region", model.Commands);
            }

            // the Personal Lighting window: its controls with the official ranges and accessible names, a slider that changes the model, Reset
            var personal = new Viewer.Core.World.PersonalLighting();
            var personalPanel = PersonalLightingFeature.Install(shell, personal, () => null);
            model.Floaters.Open(PersonalLightingFeature.FloaterId);
            await Frames(3);
            var personalFrame = shell.Frames[PersonalLightingFeature.FloaterId];
            if (!personalFrame.Visible) problems.Add("the Personal Lighting window did not open");
            if (!GetViewport().GetVisibleRect().Encloses(personalFrame.GetGlobalRect())) problems.Add($"Personal Lighting outside the window ({personalFrame.GetGlobalRect()})");
            if (personalPanel.Pickers.Count != 5) problems.Add($"Personal Lighting has {personalPanel.Pickers.Count} colour swatches, expected 5");
            if (personalPanel.SliderRows.Count != 7) problems.Add($"Personal Lighting has {personalPanel.SliderRows.Count} sliders, expected 7");
            foreach (var (field, row) in personalPanel.SliderRows)
            {
                var range = Viewer.Core.World.PersonalLighting.Range(field);
                if (Math.Abs(row.Slider.MinValue - range.Min) > 1e-9 || Math.Abs(row.Slider.MaxValue - range.Max) > 1e-9) problems.Add($"Personal Lighting {field}: range {row.Slider.MinValue} to {row.Slider.MaxValue}");
                if (string.IsNullOrWhiteSpace(row.Slider.AccessibilityName) || row.Slider.AccessibilityName.Contains("[[")) problems.Add($"Personal Lighting {field}: no accessible name");
            }
            foreach (var picker in personalPanel.Pickers.Values)
                if (string.IsNullOrWhiteSpace(picker.AccessibilityName) || picker.AccessibilityName.Contains("[[")) problems.Add($"Personal Lighting {picker.Name}: no accessible name");
            personalPanel.SliderRows[Viewer.Core.World.PersonalField.HazeDensity].Slider.Value = 3.0;
            if (!personal.IsActive || Math.Abs(personal.Value(Viewer.Core.World.PersonalField.HazeDensity, Viewer.Core.World.RegionEnvironment.Default.SkyAt(0.5)) - 3.0) > 1e-6) problems.Add("a Personal Lighting slider did not reach the model");
            personalPanel.ResetButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (personal.IsActive || Math.Abs(personalPanel.SliderRows[Viewer.Core.World.PersonalField.HazeDensity].Slider.Value - Viewer.Core.World.RegionEnvironment.Default.SkyAt(0.5).HazeDensity) > 1e-6) problems.Add("Reset did not put the region's values back");
            model.Floaters.Close(PersonalLightingFeature.FloaterId);
            if (shell.Dock.Buttons.Count != shell.DockModel.Items.Count) problems.Add($"the dock holds {shell.Dock.Buttons.Count} buttons, expected {shell.DockModel.Items.Count}");

            // the teleport floater
            model.Commands.Execute("floater.teleport");
            await Frames(3);
            var window = GetViewport().GetVisibleRect();
            var tele = shell.Frames[TravelFeature.TeleportId];
            if (!tele.Visible) problems.Add("the teleport floater did not open");
            if (!window.Encloses(tele.GetGlobalRect())) problems.Add($"teleport outside the window ({tele.GetGlobalRect()})");
            foreach (var c in Descendants(tele).OfType<Control>().Where(c => c is Label or Button or LineEdit or SpinBox or OptionButton))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"teleport {c.Name}: no accessible name");
            if (panels.Teleport.OffersBox.GetChildCount() != 1) problems.Add($"offers shown: {panels.Teleport.OffersBox.GetChildCount()}");
            if (sent.OfType<Viewer.Core.Actions.TeleportLureAnswerIntent>().Any()) problems.Add("an offer was answered without the user");
            if (Descendants(panels.Teleport.OffersBox).OfType<Label>().FirstOrDefault() is { } offerText && !offerText.Text.Contains("[b]over[/b]"))
                problems.Add($"the offer text is not shown as typed: '{offerText.Text}'");
            if (panels.Teleport.FriendChoice.ItemCount != 1) problems.Add("the friend list for offers is not filled");

            // a region and a position
            string typedRegion = "Example Region", typedOther = "Elsewhere", typedSearch = "Exam";
            panels.Teleport.RegionEdit.Text = typedRegion;
            panels.Teleport.XSpin.Value = 10;
            panels.Teleport.YSpin.Value = 20;
            panels.Teleport.ZSpin.Value = 30;
            panels.Teleport.GoButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);
            if (!sent.OfType<Viewer.Core.Actions.TeleportRegionIntent>().Any(t => t.RegionName == "Example Region" && t.Position == new System.Numerics.Vector3(10, 20, 30)))
                problems.Add("Go did not send the region and position");
            if (!panels.Overlay.Box.Visible) problems.Add("the progress panel did not show for a teleport");
            if (!panels.Overlay.CancelButton.Visible) problems.Add("no Cancel button on a teleport the grid lets be cancelled");
            hub.Enqueue(new TeleportProgressed(TeleportPhase.Started, "", true));
            hub.Enqueue(new TeleportProgressed(TeleportPhase.Progress, "sending_dest", true));
            hub.Pump();
            await Frames(3);
            if (!panels.Overlay.Message.Text.Contains(table.Get("teleport.progress.sending_dest"))) problems.Add($"progress shows '{panels.Overlay.Message.Text}'");
            if (!window.Encloses(panels.Overlay.Box.GetGlobalRect())) problems.Add("the progress panel is outside the window");
            panels.Overlay.CancelButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);
            if (!sent.OfType<Viewer.Core.Actions.TeleportCancelIntent>().Any()) problems.Add("Cancel did not send a cancel");
            if (panels.Overlay.Box.Visible) problems.Add("the progress panel stayed after Cancel");

            // a failure, in words, closed by the user
            panels.Teleport.RegionEdit.Text = typedOther;
            panels.Teleport.GoButton.EmitSignal(BaseButton.SignalName.Pressed);
            hub.Enqueue(new TeleportProgressed(TeleportPhase.Failed, "no_host", true));
            hub.Pump();
            await Frames(3);
            if (!panels.Overlay.Message.Text.Contains(table.Get("teleport.error.no_host"))) problems.Add($"the failure shows '{panels.Overlay.Message.Text}'");
            if (!panels.Overlay.CloseButton.Visible || panels.Overlay.CancelButton.Visible) problems.Add("a failure should offer Close and not Cancel");
            panels.Overlay.CloseButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);
            if (panels.Overlay.Box.Visible) problems.Add("the failure stayed after Close");

            // an offer answered by its button, and only then
            int before = sent.Count;
            if (Descendants(panels.Teleport.OffersBox).OfType<Button>().FirstOrDefault(b => b.Name == "accept") is { } accept)
                accept.EmitSignal(BaseButton.SignalName.Pressed);
            else
                problems.Add("no Teleport button on the offer");
            await Frames(2);
            if (!sent.Skip(before).OfType<Viewer.Core.Actions.TeleportLureAnswerIntent>().Any(a => a.From == offerer && a.LureId == lure && a.Accept)) problems.Add("Teleport did not answer the offer through the pipeline");
            hub.Enqueue(new TeleportProgressed(TeleportPhase.Cancelled, "", true));
            hub.Pump();

            // set home, a request for a teleport answered only by its button, and the region's notices
            panels.Teleport.SetHomeButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);
            if (!sent.OfType<Viewer.Core.Actions.SetHomeIntent>().Any()) problems.Add("Set home to here did not send a set-home request");
            model.Commands.Execute("travel.set_home");
            if (sent.OfType<Viewer.Core.Actions.SetHomeIntent>().Count() != 2) problems.Add("the World menu's Set home to here did not send a request");
            hub.Enqueue(new TeleportRequested(offerer, "Asking Resident", new Viewer.Core.Diagnostics.MessageBody("may I [b]come[/b]?", Viewer.Core.Diagnostics.MessageChannel.InstantMessage)));
            hub.Pump();
            await Frames(3);
            if (panels.Teleport.RequestsBox.GetChildCount() != 1) problems.Add($"requests shown: {panels.Teleport.RequestsBox.GetChildCount()}");
            if (sent.OfType<Viewer.Core.Actions.TeleportOfferIntent>().Any()) problems.Add("a teleport request was answered without the user");
            if (Descendants(panels.Teleport.RequestsBox).OfType<Label>().FirstOrDefault() is { } requestText && !requestText.Text.Contains("may I [b]come[/b]?"))
                problems.Add($"the request text is not shown as typed: '{requestText.Text}'");
            if (Descendants(panels.Teleport.RequestsBox).OfType<Button>().FirstOrDefault(b => b.Name == "offer") is { } offerBack)
                offerBack.EmitSignal(BaseButton.SignalName.Pressed);
            else
                problems.Add("no Offer button on the request");
            await Frames(2);
            if (!sent.OfType<Viewer.Core.Actions.TeleportOfferIntent>().Any(o => o.Target == offerer)) problems.Add("Offer teleport did not send an offer to the requester");
            if (panels.Teleport.AskButton.Disabled) problems.Add("Ask for a teleport is off although a friend is listed");
            panels.Teleport.AskButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);
            if (!sent.OfType<Viewer.Core.Actions.TeleportRequestIntent>().Any(o => o.Target == friend)) problems.Add("Ask for a teleport did not send a request to the friend");
            hub.Enqueue(new ServerAlert("You do not have access to the parcel", "", false));
            hub.Pump();
            await Frames(3);
            if (!panels.Overlay.NoticeBox.Visible) problems.Add("a parcel denial showed no notice");
            else
            {
                if (!panels.Overlay.NoticeText.Text.Contains(table.Get("notice.parcel.no_access"))) problems.Add($"the notice shows '{panels.Overlay.NoticeText.Text}'");
                if (!window.Encloses(panels.Overlay.NoticeBox.GetGlobalRect())) problems.Add("the notice is outside the window");
                if (string.IsNullOrWhiteSpace(panels.Overlay.NoticeBox.AccessibilityName) || string.IsNullOrWhiteSpace(panels.Overlay.NoticeText.AccessibilityName)) problems.Add("the notice has no accessible name");
            }
            panels.Overlay.NoticeClose.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);
            if (panels.Overlay.NoticeBox.Visible) problems.Add("the notice stayed after Close");

            // the world map
            model.Commands.Execute("floater.map");
            await Frames(8);
            var mapFrame = shell.Frames[TravelFeature.MapId];
            if (!mapFrame.Visible) problems.Add("the world map did not open");
            if (!window.Encloses(mapFrame.GetGlobalRect())) problems.Add($"map outside the window ({mapFrame.GetGlobalRect()})");
            foreach (var c in Descendants(mapFrame).OfType<Control>().Where(c => c is Label or Button or LineEdit or OptionButton or ItemList))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"map {c.Name}: no accessible name");
            if (panels.Map.View is not { } view) problems.Add("the world map has no view");
            else
            {
                if (string.IsNullOrWhiteSpace(view.AccessibilityName)) problems.Add("the map view has no accessible name");
                hub.WorldMap.CentreOnMetres(1001 * 256 + 128, 1001 * 256 + 128);
                await Frames(8);
                if (hub.WorldMap.Tiles.Count == 0) problems.Add("the map asked for no tiles");
                if (tiles.Fetches == 0 || tiles.Count == 0) problems.Add($"no tile arrived (fetches {tiles.Fetches}, kept {tiles.Count})");
                var click = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = view.Size / 2 };
                view._GuiInput(click);
                view._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = view.Size / 2 });
                await Frames(3);
                if (hub.WorldMap.Selected is null) problems.Add("a click on the map chose no place");
                if (sent.OfType<Viewer.Core.Actions.TeleportHandleIntent>().Any()) problems.Add("choosing a place on the map teleported");
                panels.Map.TeleportButton.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames(2);
                if (!sent.OfType<Viewer.Core.Actions.TeleportHandleIntent>().Any(t => t.RegionHandle == Viewer.Core.Map.MapMath.HandleOf(1001, 1001)))
                    problems.Add("the map's Teleport button did not send the chosen place");
                if (!sent.OfType<Viewer.Core.Actions.TeleportHandleIntent>().Any(t => t.Position.Z == 30)) problems.Add("the map's chosen place did not take the agent's height");
                hub.Enqueue(new TeleportProgressed(TeleportPhase.Cancelled, "", true));
                hub.Pump();
                double scale = hub.WorldMap.PixelsPerRegion;
                view._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = view.Size / 2 });
                if (hub.WorldMap.PixelsPerRegion <= scale) problems.Add("the wheel did not zoom the map");
                panels.Map.SearchEdit.Text = typedSearch;
                panels.Map.SearchButton.EmitSignal(BaseButton.SignalName.Pressed);
                if (!asks.Contains("Exam")) problems.Add("Search did not ask the grid");
                if (panels.Map.FriendChoice.ItemCount != 1) problems.Add("the friend who shares their place is not listed");
            }

            // the mini-map
            model.Commands.Execute("floater.minimap");
            await Frames(5);
            var miniFrame = shell.Frames[TravelFeature.MiniMapId];
            if (!miniFrame.Visible) problems.Add("the mini-map did not open");
            if (!window.Encloses(miniFrame.GetGlobalRect())) problems.Add($"mini-map outside the window ({miniFrame.GetGlobalRect()})");
            foreach (var c in Descendants(miniFrame).OfType<Control>().Where(c => c is Label or Button or CheckBox))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"mini-map {c.Name}: no accessible name");
            if (panels.Mini.View?.Frame is not { } frame) problems.Add("the mini-map drew no frame");
            else
            {
                if (frame.Dots.Count != 2) problems.Add($"the mini-map holds {frame.Dots.Count} dots, expected 2");
                if (frame.Dots.Count(d => d.Friend) != 1) problems.Add("the friend's dot is not told apart");
                if (!frame.Rotated) problems.Add("the mini-map does not turn with the camera by default");
            }
            double miniScale = hub.MiniMap.PixelsPerRegion;
            panels.Mini.ZoomInButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (hub.MiniMap.PixelsPerRegion <= miniScale) problems.Add("Zoom in did not zoom the mini-map");
            panels.Mini.RotateBox.ButtonPressed = false;
            await Frames(3);
            if (hub.MiniMap.Rotate) problems.Add("the rotate box did not turn rotation off");
            if (panels.Mini.View?.Frame is { Rotated: true }) problems.Add("the mini-map still turns after the box was cleared");

            results[name] = problems;
            Log($"travel {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            panels.Overlay.Bind(null);
            panels.Teleport.Bind(null, null);
            panels.Map.Bind(null, null, null);
            panels.Mini.Bind(null, null);
            tiles.Dispose();
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["travel"] = results;
        if (!pass)
            _failures.Add("teleport, world map and mini-map floaters (see travel)");
    }

    private static IEnumerable<Node> Descendants(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    private sealed class CaptureSink : ILogSink
    {
        public readonly List<LogEntry> Entries = new();
        public void Write(LogEntry entry) { lock (Entries) Entries.Add(entry); }
    }

    private void WriteResult()
    {
        string path = _resultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(_result, new JsonSerializerOptions { WriteIndented = true }));
        Log($"result written: {path}");
    }

    private static string? UserArg(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private static void Log(string line)
    {
        GD.Print("S1 " + line);
    }
}

/// <summary>Stands for the world's key handling in S-1's key check: records every key that reaches the unhandled stage.</summary>
public partial class S1KeySink : Node
{
    public List<Key> Keys { get; } = new();

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } k)
            Keys.Add(k.Keycode);
    }
}
