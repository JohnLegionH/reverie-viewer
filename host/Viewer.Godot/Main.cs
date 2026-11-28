// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The viewer's entry scene. Installs the engine logger first (ADR-019), then:
///   - S-1 mode (any "--s1-" user argument after "--"): hands over to <see cref="S1Smoke"/>, which runs its checks;
///   - --test-render: hands over to <see cref="TestRender"/> (L4 goldens, measurements, L3 lifecycle);
///   - otherwise: the ADR-002 startup check (refusal = the localised message on stderr and in the log, exit 3 for an
///     unapproved backend, 4 below the floor), then the login screen. In world, the region's terrain, objects and avatars
///     are presented, under an overview camera until the user's own avatar is known and then the follow camera behind it
///     (right-drag orbits, the wheel zooms); the login screen returns when the session ends. Avatars' bodies are read from
///     the character folder named by --character=&lt;folder&gt; or NV_CHARACTER_DIR, else the one shipped beside the executable; without one they are
///     shown by their name tags only.
///   - development builds only: <c>--automation</c> starts the automation API (<see cref="AutomationHost"/>; compiled out of
///     shipping exports).
/// </summary>
public partial class Main : Node
{
    public static CoreLog Log { get; private set; } = new(new ConsoleLogSink(), "viewer");
    /// <summary>The engine's errors and messages, routed into <see cref="Log"/> (null before _Ready).</summary>
    internal static EngineLogRoute? EngineLog { get; private set; }

    private EngineLogger? _logger;
    private LoginScreen? _loginScreen;
    private Viewer.Shell.Grids.GridList? _gridList;
    private Viewer.Shell.Grids.SavedNames? _savedNames;
    private ShellRoot? _shell;
    private Node3D? _stage;
    // the root viewport's scenario, read from the engine once and kept (a World3D read back on each use is a fresh managed wrapper that
    // may be finalizing at that moment)
    private Rid _scenario;
    private World3D? _world;
    private World3D World() => _world ??= GetViewport().FindWorld3D();
    private Rid Scenario() => _scenario.IsValid ? _scenario : (_scenario = World().Scenario);
    private Camera3D? _camera;
    private WorldPresenter? _presenter;
    private ViewerSession? _presented;
    private (int, int) _cameraFor;

    public LoginModel? Model { get; private set; }

    // closing the window logs the session out first (bounded wait), then quits; set in the login mode only
    private CloseRequest? _close;
    private bool _quitting;

    private Viewer.Core.Assets.IMeshTransport? _meshTransport;
    private MeshSource? _meshes;
    private Viewer.Core.Assets.ITextureTransport? _textureTransport;
    private Viewer.Core.Assets.IMaterialTransport? _materialTransport;
    private Viewer.Core.Assets.IAnimationTransport? _animationTransport;
    private Viewer.Core.Assets.SessionAssets? _animationAssets;
    private AnimationSource? _animations;
    private readonly FollowCamera _follow = new();
    private double? _inAirSince;   // when the region started to say the own avatar is in the air (the camera's flight lag starts after half a second)
    private bool _orbiting;   // the right button is down (a right click opens the context menu; the official viewer has no right-drag turning)
    private Viewer.Motion.CameraControls? _camControls;
    private bool _cameraTool;   // an Alt (or Ctrl+Alt) left-drag is moving the camera
    private readonly Viewer.Motion.MouseSteering _steer = new();   // a left-drag on the avatar or the ground turns the avatar
    private Viewer.Core.Interaction.PickResult? _steerPick;   // what the left button went down on
    private System.Numerics.Vector3 _agentCentre;   // the own avatar's centre as last placed, region metres
    private readonly Dictionary<Key, Viewer.Motion.CameraKey> _cameraKeysDown = new();
    // mouse-look: whether the view is first-person, the mouse movement collected since the last frame, and the crosshair
    private readonly MouseLookMode _mouseLook = new() { Allowed = MouseLookSetting.Enabled() };
    private float _lookDx, _lookDy;
    private Crosshair? _crosshair;
    private CanvasLayer? _crosshairLayer;
    private bool _scriptedMouseLook;   // set while the automation API changes the view
    // the user's own avatar moved from the keyboard (set while a session is presented); the keys held, by key name
    private OwnAvatarDriver? _driver;
    private readonly System.Collections.Generic.Dictionary<string, Viewer.Motion.MoveCommand> _keysDown = new();
    private KeyBindings? _bindings;
    private System.Numerics.Vector3 _eye, _look;   // the follow camera's last eye and target, region metres
    // how smoothly the own avatar is drawn while the region has it moving, each frame's step against what the region's
    // speed gives over that frame's time: stalls (under a quarter of it), jumps (over twice it), the largest ratio and
    // the longest frame; reset each presentation
    private System.Numerics.Vector3? _lastSoles;
    private int _movingFrames, _stalledFrames, _jumpFrames;
    private float _largestRatio;
    private double _longestFrame;
    private bool _following;
    private double _lastCameraAt = -1;
    private readonly System.Diagnostics.Stopwatch _cameraClock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>The frame's time for every avatar's motion (one time a frame, as the official viewer's frame time), latched at the start of each frame.</summary>
    private static readonly Viewer.Shell.FrameClock s_avatarFrameClock = new(() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
    private readonly Viewer.Shell.HandoffLedger _handoffLedger = new(() => s_avatarFrameClock.Now);   // what a crossing hands over stays drawn until the region entered draws it
    private readonly MotionTrace _motionTrace = new();   // how avatars are drawn against the region's positions, per frame (automation only)
    private static System.Threading.Tasks.Task<AvatarPresenter.LoadedBody?>? s_body;
    private static string? s_bodyFolder;
    private MaterialSource? _materials;
    private TextureSource? _textures;
    private Viewer.Core.Assets.ContentStore? _contentStore;
    // what the session's texture fetching did, and the budget it ran under
    private Viewer.Core.Assets.PartialTextureFetcher? _fetcher;
    private Viewer.Core.Assets.FetchCounter? _wholeFetches;
    private Viewer.Core.Assets.TextureTimeline? _timeline;   // where each texture's seconds go
    private Viewer.Core.Residency.LiveTextureBudget? _budget;
    private Viewer.Core.Assets.ContentView? _contentView;
    private Transform3D? _overview;
    private System.Diagnostics.Stopwatch? _presentedFor;

    /// <summary>The per-user encrypted content store (created on first use; DPAPI-protected key).</summary>
    private Viewer.Core.Assets.ContentStore ContentStore() => _contentStore ??= MakeContentStore();
    private readonly Viewer.Core.Assets.SharedAssetStore _sharedAssets = new();

    /// <summary>The test window's size, place and focus flag as the run found them (null outside test runs); written into the run's sidecar.</summary>
    public static System.Collections.Generic.Dictionary<string, object?>? TestWindowInfo { get; private set; }

    private SessionLogFile? _sessionLog;

    /// <summary>The log goes to the console and to a file in the user's log folder, the current session's log and the one before it, at the
    /// console's level (NV_LOG_LEVEL). A restart within one run goes on in the same file. A folder that cannot be used leaves the console log alone.</summary>
    private void OpenSessionLog()
    {
        try
        {
            var file = SessionLogFile.Open(Viewer.Core.Platform.DataDirectories.LogsFolder(System.Environment.GetEnvironmentVariable, Viewer.Core.Platform.DataDirectories.ForCurrentUser().Logs), BackendRelaunch.Tried(OS.GetCmdlineUserArgs()).Count > 0);
            if (file.Path is null)
                return;
            _sessionLog = file;
            var minimum = LevelFilterSink.Parse(System.Environment.GetEnvironmentVariable("NV_LOG_LEVEL"));
            Log = new CoreLog(new TeeLogSink(new ConsoleLogSink(), new LevelFilterSink(file, minimum)), "viewer");
            Log.Info("log.file", CoreLog.F("name", SessionLogFile.CurrentName));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // no user folder to write to: the console log is all there is
        }
    }

    /// <summary>The first start under the viewer's name takes the settings kept under its previous data-folder name (once; the old
    /// folder is left as it was).</summary>
    private void CarrySettingsOver()
    {
        try
        {
            var previous = Viewer.Core.Platform.DataDirectories.For(Viewer.Core.Platform.DataDirectories.Current, System.Environment.GetEnvironmentVariable,
                Viewer.Core.Identity.ViewerIdentity.PreviousDataFolderName).Settings;
            var r = Viewer.Core.Platform.SettingsCarryOver.Run(previous, Viewer.Core.Platform.DataDirectories.ForCurrentUser().Settings);
            Log.Info("settings.carry_over", CoreLog.F("outcome", r.Outcome.ToString()), CoreLog.F("files", r.Files), CoreLog.F("error", r.Error ?? ""));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Info("settings.carry_over", CoreLog.F("outcome", "skipped"), CoreLog.F("error", e.Message));
        }
    }

    public override void _Ready()
    {
        LoggedErrorReleases.Prepare();   // before the logger: every error's backtraces are released on this thread
        OpenSessionLog();
        CarrySettingsOver();   // before anything reads the settings
        _logger = new EngineLogger(EngineLog = new EngineLogRoute(Log));
        OS.AddLogger(_logger);
        // the kept collections, the Variant disposal's getter and queue, made before the first frame
        KeptCollections.PrepareAtStartup();
        Log.Info("godot.kept_setup", CoreLog.F("ms", Math.Round(KeptCollections.SetupMs, 3)), CoreLog.F("frame", KeptCollections.PreparedAtFrame),
            CoreLog.F("variant-disposal", VariantDisposal.Unavailable ?? "pinned"));
        // the viewer's shaders, kept for the process, made before the first frame in every mode (a
        // variant made later would be a new process object in the middle of a session)
        KeptShaders.PrepareAtStartup();

        // a test run, or a start the harness placed (it passes --nv-test-window=): small window in a corner, no focus
        if (OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--s1-", StringComparison.Ordinal)) || OS.GetCmdlineUserArgs().Contains("--test-render")
            || TestWindow.FullSize(OS.GetCmdlineUserArgs()) is not null)
            TestWindowInfo = TestWindow.Apply(GetWindow(), OS.GetCmdlineUserArgs());
        else if (TestWindow.ClearStrayNoFocus(GetWindow(), OS.GetCmdlineUserArgs()))
            Log.Warn("startup.no-focus-setting-ignored", CoreLog.F("os-visible", TestWindow.OsVisible()?.ToString() ?? "unknown"));   // a test run's override.cfg was left beside this build
        if (TestWindowInfo is null)
            DisplayScaleProbe.UseScreenOf(GetWindow());   // a normal run follows the display's scale until the person sets one

        if (OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--s1-", StringComparison.Ordinal)))
        {
            AddChild(new S1Smoke());
            return;
        }

        if (OS.GetCmdlineUserArgs().Contains("--test-render"))
        {
            AddChild(new TestRender());
            return;
        }

        PrimLayer.MergeStill = Viewer.Core.Platform.MergeSetting.ForCurrentUser(OS.GetCmdlineUserArgs());   // --merge=off or the settings file turns merging of still objects off
        if (float.TryParse(System.Environment.GetEnvironmentVariable("NV_MERGE_CELL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cellMetres) && cellMetres >= 16f) PrimLayer.MergeCellMetres = cellMetres;   // measuring: the side of a merge cell
        string? settingsDir = null;
        try { settingsDir = Viewer.Core.Platform.DataDirectories.ForCurrentUser().Settings; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Log.Warn("login.settings-folder-unavailable", CoreLog.F("reason", ex.GetType().Name)); }
        var startSettings = UserSettings(settingsDir);   // read once; chat and later features take their settings from it
        var strings = OS.GetCmdlineUserArgs().Contains("--pseudo-loc") ? StringTable.Pseudo() : StringTable.ForSetting(startSettings.GetText(Settings.UiLanguageKey));
        _strings = strings;
        GetWindow().Title = WindowTitleText.For(strings, null, null);   // "Reverie Viewer"; the avatar and the grid join it once logged in
        _titleSettings = startSettings;
        if (RestartForBackend(startSettings))
            return;   // started again with the backend that was chosen; this copy quits
        var decision = StartupCheck.Run(Log);
        if (!decision.Approved)
        {
            if (FallBackAfterRefusal(decision))
                return;   // started again with the other backend
            string message = _backendDecision is { MessageKey: { } neither } nothingPasses ? strings.Get(neither, nothingPasses.MessageArgs) : decision.Message(strings);
            Console.Error.WriteLine(message);
            Log.Warn("startup.refused", CoreLog.F("verdict", decision.Verdict), CoreLog.F("exit", decision.ExitCode));
            GetTree().Quit(decision.ExitCode);
            return;
        }

        StartPreferences(startSettings);
        var startLook = ThemeStore.Load(startSettings, null);   // the look last saved, for the login screen
        UiTheme.Apply(ThemeResolver.Resolve(startLook, null), ThemeStore.EffectiveScale(startSettings, null, startLook, DisplayScaleProbe.Reading()));
        // the certificates the person chose to trust at login: kept in the settings folder (NV_CERT_STORE names another), applied to every HTTPS connection
        var certificateTrust = new Viewer.Core.Net.CertificateTrust(Viewer.Core.Net.CertificateTrust.FolderFor(settingsDir, System.Environment.GetEnvironmentVariable));
        var statsFigures = new Viewer.Core.Session.ViewerStatsFigures(0, RenderingServer.GetVideoAdapterName(), RenderingServer.GetVideoAdapterVendor(),
            RenderingServer.GetVideoAdapterApiVersion(), string.IsNullOrEmpty(startSettings.GetText(Settings.UiLanguageKey)) ? "en" : startSettings.GetText(Settings.UiLanguageKey)!, 0, true, true);
        var model = new LoginModel(strings, opts =>
        {
            var transport = ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(opts.Outbound), Log, certificateTrust, CharacterFolder());
            _meshTransport = transport as Viewer.Core.Assets.IMeshTransport;
            _textureTransport = transport as Viewer.Core.Assets.ITextureTransport;
            _materialTransport = transport as Viewer.Core.Assets.IMaterialTransport;
            _animationTransport = transport as Viewer.Core.Assets.IAnimationTransport;
            _soundTransport = transport as Viewer.Core.Assets.ISoundTransport;
            BindSettingsTransport(transport);
            if (transport is Viewer.Core.Session.INeighbourControl control)
            {
                foreach (var address in HoldsForNextLogin) control.HoldAtLogin(address);   // (development automation only sets any)
                HoldsForNextLogin.Clear();
            }
            transport.SetDrawDistance(Math.Min(Math.Max(opts.DrawDistance / 2f, 50f), opts.DrawDistance));   // arriving: half of the draw distance, before the login
            // the statistics report's figures (sent only where the grid policy asks for it): read on the report's worker thread, so the
            // adapter's names are read here, on the main thread, and the frame rate through the engine's own counter
            return new ViewerSession(opts, transport, Log) { StatsFigures = () => statsFigures with { Fps = Engine.GetFramesPerSecond() } };
        }, GridAddress.HttpFetcher(new OutboundPolicy(new OutboundOptions()), Log, null, certificateTrust), settingsDir);
        model.Trust = certificateTrust;
        // remember password: the login hash only, DPAPI-protected in the settings folder on Windows, the desktop keyring on Linux
        model.Vault = RememberPassword.ForThisComputer(settingsDir, System.Environment.GetEnvironmentVariable);
        model.UserSettings = startSettings;   // a first login switches the shared environment after teleport on
        model.LogLoginHost = LogLoginHost();
        try { model.MuteCacheDirectory = Viewer.Core.Platform.DataDirectories.ForCurrentUser().Cache; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Log.Warn("mute.cache-folder-unavailable", CoreLog.F("reason", ex.GetType().Name)); }
        model.DrawDistance = startSettings.Get(Settings.DrawDistance);   // the preference, read before login: the first agent updates carry it
        model.Neighbours = Viewer.Core.Platform.NeighboursSetting.ForCurrentUser(OS.GetCmdlineUserArgs());   // the regions around the agent's: on unless --neighbours=off or the settings file says so
        if (GraphicsBackendInfo.Status?.NoticeText(strings) is { } backendNotice)
            model.Announce(backendNotice);   // the backend wanted could not run: said on the login screen, and again once in the world
        model.StartLocation = StartLocationChoice(startSettings);
        // the screen starts with --grid=<address> when given, else the last successful login address
        string? gridOption = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--grid=", StringComparison.Ordinal))?["--grid=".Length..];
        // the grid list in the viewer's own settings (empty at first: no grid ships with the viewer); --grid adds its grid when new and chooses it
        _gridList = new Viewer.Shell.Grids.GridList(settingsDir);
        _gridList.ApplyCommandLine(gridOption);
        _savedNames = new Viewer.Shell.Grids.SavedNames(settingsDir);
        model.LoginUri = _gridList.Selected?.Address ?? "";
        Model = model;
        _close = new CloseRequest(() => model.Session, model.Cancel, Log);
        GetTree().AutoAcceptQuit = false;   // the window's close request comes to _Notification, which logs out first
        _loginScreen = new LoginScreen(strings, model, startSettings, _gridList, _savedNames);
        _shell = new ShellRoot(new ShellController(strings, RequestClose, new SettingsFloaterPlaces(startSettings)));   // every window's place, kept between runs
        AddChild(_shell);
        _shell.MainView.AddChild(_loginScreen);   // the shell holds the login screen; the world is drawn behind it
        InstallNames(model, strings);
        _shell.Shell.Focus.DriverFocusedChanged += OnMovementAllowed;   // the avatar's controls: not while a text field has the keyboard, unless arrow keys always move
        _shell.Shell.Focus.MovementAllowedChanged += ReleaseLetterKeys;
        _preferences = PreferencesFeature.Install(_shell, startSettings, ClearContentCache, ClearSavedLoginNames);
        CommandListFeature.Install(_shell, startSettings, _preferences);   // Ctrl+Shift+K and the top bar's search box
        _loginScreen.PreferencesRequested += () => _shell.Shell.Floaters.Open(PreferencesFeature.FloaterId);
        // Help: the chosen grid's own help page when it publishes one, else About
        _loginScreen.HelpRequested += page => { if (page is not null) _loginScreen.OpenPage(page); else _shell.Shell.Floaters.Open(ShellController.AboutFloaterId); };
        InstallSounds(startSettings);   // the interface sounds: clicks, windows, notifications, chat and travel events
        WireChrome(model, strings);
        // the screen is up; says whether the grid field came filled in and from where (never the address itself)
        Log.Info("login.screen", CoreLog.F("grid-filled", model.LoginUri.Length > 0),
            CoreLog.F("grid-source", GridAddress.Tidy(gridOption).Uri is not null ? "option" : model.LoginUri.Length > 0 ? "remembered" : "none"),
            CoreLog.F("grid-chars", model.LoginUri.Length), CoreLog.F("ms-since-engine-start", Time.GetTicksMsec()));   // the start-up time to the login screen
        WarmPipelines();
        bool automation = false;
#if NV_AUTOMATION
        automation = Viewer.Automation.AutomationArgs.Enabled(OS.GetCmdlineUserArgs());
        if (automation)
            AddChild(new AutomationHost(this));
#endif
        // the Third-Party Viewer Policy's disclosures (1.c, 4.b) at the first run; a development run driven by the automation API
        // starts from an empty settings folder every time, so it is not stopped by them (its own command shows them)
        if (!automation && Disclosures.FirstRunDue(startSettings))
            _ = ShowFirstRunDisclosure(strings, startSettings);
    }

    /// <summary>The first-run disclosure over the login screen: accepted, it is kept and not shown again; refused, the viewer quits.</summary>
    private async System.Threading.Tasks.Task ShowFirstRunDisclosure(StringTable strings, SettingsService settings)
    {
        if (_loginScreen is not { } screen)
            return;
        var answer = await screen.Dialogs.AskAsync(Disclosures.FirstRun(strings), System.Threading.CancellationToken.None);
        if (answer.Confirmed)
            Callable.From(() => Disclosures.FirstRunAccepted(settings)).CallDeferred();
        else
            Callable.From(RequestClose).CallDeferred();
    }

    /// <summary>The pipeline warm-up, off screen, in the login screen's first two frames (before anyone
    /// can have logged in), so the world's first frames no longer compile the viewer's shaders and pipelines.</summary>
    private async void WarmPipelines()
    {
        // start-up figures in the log: the first frame the window draws, and the end of the warm-up, each in milliseconds since the process started
        Action? firstFrame = null;
        firstFrame = () =>
        {
            RenderingServer.Singleton.FramePostDraw -= firstFrame;
            Log.Info("godot.first_frame", CoreLog.F("since_process_start_ms", SinceProcessStartMs()));
        };
        RenderingServer.Singleton.FramePostDraw += firstFrame;
        var warm = await PipelineWarmup.RunAsync(this, GetViewport().Msaa3D, async () => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw));
        Log.Info("godot.pipeline_warmup", CoreLog.F("ms", Math.Round(warm.Ms, 1)), CoreLog.F("variants", warm.Variants),
            CoreLog.F("pipelines", warm.Pipelines.Values.Sum()), CoreLog.F("since_process_start_ms", SinceProcessStartMs()));
        try { await WarmArrival(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warn("godot.arrival_warmup", CoreLog.F("state", "failed"), CoreLog.F("error", ex.GetType().Name), CoreLog.F("message", ex.Message)); }
    }

    /// <summary>The avatar arrival's code run once, off screen, after the pipeline warm-up (<see cref="ArrivalWarmup"/>): the character folder is read now
    /// instead of when the first session enters the world, and once it is read an avatar is made, shaped, baked and posed in a hidden viewport that is then
    /// freed. Skipped when no character folder is set, or a session is already in the world.</summary>
    private async System.Threading.Tasks.Task WarmArrival()
    {
        var bodyTask = Body();
        if (bodyTask is null) return;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!bodyTask.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(60))
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!bodyTask.IsCompletedSuccessfully || bodyTask.Result is not { } loaded || _stage is not null) return;
        var done = await ArrivalWarmup.RunAsync(this, GetViewport().Msaa3D, loaded, async () => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw));
        Log.Info("godot.arrival_warmup", CoreLog.F("ms", Math.Round(done.Ms, 1)), CoreLog.F("avatars", done.Avatars), CoreLog.F("frames", done.Frames));
    }

    private static double SinceProcessStartMs() =>
        Math.Round((DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds, 0);

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            UpdateWindowMemory(now: true);
        if (what == NotificationWMCloseRequest && _close is not null)   // the other modes keep the engine's own quit
            RequestClose();
        else if (what is (int)NotificationApplicationFocusOut or (int)NotificationWMWindowFocusOut)
        {
            _shell?.Shell.Focus.SetWindowFocused(false);
            _driver?.Input.SetFocused(false);   // every held key is released at once
            _keysDown.Clear();
        }
        else if (what is (int)NotificationApplicationFocusIn or (int)NotificationWMWindowFocusIn)
        {
            _shell?.Shell.Focus.SetWindowFocused(true);
            _driver?.Input.SetFocused(_shell?.Shell.Focus.DriverFocused ?? true);   // not while a text field has the keyboard, unless arrow keys always move
            _mouseLook.FocusGained();   // the official viewer leaves mouse-look when the window gets focus back
        }
    }

    /// <summary>Typing started or ended: the avatar's keys count only when no text field has the keyboard (every held key is released when typing starts).</summary>
    /// <summary>A text field took the keyboard: every held key except the arrows is let go (a letter held down would never see its release, which goes to the text field).
    /// The arrows stay when they are allowed to move the avatar while typing; <see cref="OnMovementAllowed"/> lets them go when nothing may.</summary>
    private void ReleaseLetterKeys(bool allowed)
    {
        if (allowed)
            return;
        foreach (var name in _keysDown.Keys.ToArray())
            if (name is not ("LEFT" or "RIGHT" or "UP" or "DOWN") && _keysDown.Remove(name, out var held))
                _driver?.Input.Release(held);
    }

    private void OnMovementAllowed(bool allowed)
    {
        _driver?.Input.SetFocused(allowed);
        if (!allowed)
        {
            _keysDown.Clear();
            _cameraKeysDown.Clear();
            _camControls?.ReleaseAll();
        }
    }

    /// <summary>The view became mouse-look or went back to third person: the avatar's controls, the camera, the own avatar's drawing, the
    /// mouse (captured: it hides and gives movement without end) and the crosshair follow.</summary>
    private void OnMouseLookChanged(bool on)
    {
        _lookDx = _lookDy = 0;
        _orbiting = false;
        _addons?.Mouselook(on);
        if (_driver is not null)
        {
            if (on)
            {
                _driver.Input.EnterMouseLook();
                foreach (var name in _keysDown.Where(k => k.Value is Viewer.Motion.MoveCommand.TurnLeft or Viewer.Motion.MoveCommand.TurnRight).Select(k => k.Key).ToList())
                    _keysDown.Remove(name);   // the turn keys were let go by the controls
            }
            else _driver.Input.LeaveMouseLook();
        }
        if (on) _follow.EnterMouselook(); else _follow.LeaveMouselook();
        if (OwnAvatars is { } avatars) avatars.Scene.HideSelf = on;
        // the real mouse is captured only when the real mouse or keyboard entered it: a scripted entry (the automation API) leaves the user's pointer alone
        Input.MouseMode = on && !_scriptedMouseLook ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        if (_crosshair is not null) _crosshair.Visible = on && MouseLookSetting.ShowCrosshairs();
        Log.Info("mouselook", CoreLog.F("state", on ? "on" : "off"));
    }

    /// <summary>The window's close request (and the automation API's quit): logs a session still in world out, waiting
    /// at most <see cref="CloseRequest.LogoutWait"/> for the grid's reply, then quits; a second request quits at once.</summary>
    public void RequestClose()
    {
        _reconnect?.Forget();   // the user is leaving: the held login goes
        if (_close is null)
        {
            GetTree().Quit(0);
            return;
        }
        _itemProperties?.Commit();   // text typed in Item Properties is saved while the session lives
        LetGoOfSelections();   // the region is told before the logout goes
        _close.Request();
        _shell?.Shell.Floaters.SavePlaces();   // the windows still open keep their places for the next run
        QuitWhenReady();
        UpdateWindowMemory();
    }

    private void QuitWhenReady()
    {
        if (_quitting || _close is not { ReadyToQuit: true })
            return;
        _quitting = true;
        GetTree().Quit(0);
    }

    public override void _Process(double delta)
    {
        s_avatarFrameClock.Latch();
        QuitWhenReady();
#if NV_AUTOMATION
        FrameMeter.Frame();
        long scriptAt = FrameMeter.Begin();
#endif
        long costAt = FrameStamp();
        UpdateComms();
        FrameCost("comms", costAt); costAt = FrameStamp();
        UpdateTravel();
        UpdateChrome();
        UpdateInventory();
        UpdateInteract();
        UpdateNotifications();
        UpdateSounds();
        AddonsFrame();
        LoginDumpTick();
        FrameCost("ui", costAt); costAt = FrameStamp();
        var session = Model?.Session;
        if (session is not null && !session.IsEnded && session.Stage == SessionStage.InWorld && _presented != session)
            Present(session);
        else if (_presented is not null && (session != _presented || _presented.IsEnded))
        {
            NoteDrop(_presented);   // a session the grid ended unasked gets its notice
            Unpresent();
        }
        WatchReconnected();
        _presented?.AdvanceFar();
        FrameCost("present", costAt);
        if (_presenter is not null)
        {
            costAt = FrameStamp();
            if (_presenter.Prims is { } prims && _camera is not null)
            {
                prims.SetCamera(_camera);   // mesh LODs follow the camera
                prims.UpdateLods();
                var size = GetViewport().GetVisibleRect().Size;
                prims.ResidencyView = PrimLayer.ViewOf(_camera, (int)size.X, System.Math.Max(1, (int)size.Y));   // the residency's view of the camera
                if (_presenter.Avatars is { } sized) sized.BakeView = prims.ResidencyView;   // an avatar's bakes are sized by how big the avatar is on screen
            }
            FrameCost("lods", costAt); costAt = FrameStamp();
#if NV_AUTOMATION
            long presenterAt = FrameMeter.Begin();
#endif
            _presenter.Frame();
#if NV_AUTOMATION
            FrameMeter.End(presenterAt, FrameMeter.Presenter);
#endif
            FrameCost("presenter", costAt); costAt = FrameStamp();
            UpdateNeighbours();
            FrameCost("neighbours", costAt);
            if (_presented?.World.Region is { } r && _cameraFor != (r.SizeX, r.SizeY))
            {
                Stage.Overview(_camera!, r.SizeX, r.SizeY);
                _cameraFor = (r.SizeX, r.SizeY);
                _overview = _camera!.GlobalTransform;
            }
            costAt = FrameStamp();
            FollowOwnAvatar();
            AddonsWorldFrame();
            FrameCost("follow", costAt); costAt = FrameStamp();
            UpdateNameTags();
            FrameCost("names", costAt); costAt = FrameStamp();
            _presenter.Prims?.UpdateBlendOrder();   // blended faces in order for the camera as it is drawn this frame
            FrameCost("blend", costAt);
            if (_motionTrace.AllRegions && _presented is not null)
            {
                // every region's avatars, each with the world that states it (a development record of how other avatars are drawn across a border)
                if (_presenter.Avatars is { } here) _motionTrace.Sample(here.Scene, _presented.World, _cameraClock.Elapsed.TotalSeconds, _eye);
                foreach (var stack in _stacks.Values) _motionTrace.Sample(stack.Avatars.Scene, stack.World, _cameraClock.Elapsed.TotalSeconds, _eye);
            }
            else if (OwnAvatars is { } traced && _presented is not null) _motionTrace.Sample(traced.Scene, _presented.World, _cameraClock.Elapsed.TotalSeconds, _eye);
            CrossingFrameTick();   // development aid, off by default
        }
        costAt = FrameStamp();
        LoggedErrorReleases.DrainFrame();   // the engine errors' backtraces, released here on the main thread, pinned
        FrameCost("errors", costAt);
        FrameLogEnd(delta);
#if NV_AUTOMATION
        FrameMeter.End(scriptAt, FrameMeter.Process);
#endif
    }

    /// <summary>Once the user's own avatar is placed, the camera follows it (the follow camera's pose, eased by the frame
    /// time); until then, and while it is not placed, the overview stays.</summary>
    private void FollowOwnAvatar()
    {
        double now = _cameraClock.Elapsed.TotalSeconds;
        double dt = _lastCameraAt < 0 ? 0 : now - _lastCameraAt;
        _lastCameraAt = now;
        if (_mouseLook.Active && _driver is not null)
        {
            _driver.Input.LookBy(_lookDx, _lookDy, dt);   // the mouse turns the heading and tilts the view before the camera reads them
            _lookDx = _lookDy = 0;
        }
        // a reset of the view levels the agent's pitch only while the left button is up (llagentcamera.cpp resetView reads the button's state)
        _follow.LeftMouseDown = Input.IsMouseButtonPressed(MouseButton.Left);
        if (_shiftFromKeyboard && !Input.IsKeyPressed(Key.Shift))   // its release went elsewhere (a menu, a dialog): one key read, only while held
        {
            _shiftFromKeyboard = false;
            _driver?.Input.SetFastKey(false);
        }
        _driver?.Frame(now, dt, _eye, _look);   // the keys turn the heading before the camera reads it
        FlushArrowTrace();
        // the own avatar of the region the agent is in, or, until that region has stated it after a crossing, of the region it came from
        var own = AvatarScene.FirstSelf([OwnAvatars?.Scene, _presenter?.Avatars?.Scene, .. _stacks.Values.Select(s => s.Avatars.Scene)]);
        if (_camera is null || own is not { Scene: var ownScene, Self: var self } || !ownScene.Place(self, out var soles, out float yaw))
        {
            _following = false;
            return;
        }
        float height = self.Size.Z > 0.1f ? self.Size.Z : AvatarScene.DefaultHeight;
        CountStep(self, soles, dt);
        if (_presented is { } shown)
        {
            // the limits the official camera keeps: the draw distance, the ground, the region's width (llagentcamera.cpp)
            var world = shown.World;
            _follow.AvatarHeight = height;
            _follow.DrawDistance = shown.DrawDistance;
            _follow.RegionWidth = world.Region?.SizeX ?? 256;
            // the camera is in the anchor region's frame: its ground, and whether the agent manages the estate there, are those of the region that
            // holds the point (llagentcamera.cpp, calcCameraPositionTargetGlobal: LLWorld::resolveLandHeightGlobal, LLViewerRegion::canManageEstate)
            var worlds = new System.Collections.Generic.List<Viewer.Core.World.WorldState>(_stacks.Count + 2);
            if (_anchorWorld is not null) worlds.Add(_anchorWorld);
            foreach (var stack in _stacks.Values) worlds.Add(stack.World);
            if (!worlds.Contains(world)) worlds.Add(world);
            _follow.LandHeight = (x, y) => RegionGround.Height(worlds, x, y);
            _follow.CanManageEstateAt = (x, y) => RegionGround.WorldAt(worlds, x, y)?.Region?.IsEstateManager == true;
            _follow.Godlike = shown.AgentGodlike;
        }
        _follow.SeatRotation = self.Seat != 0 && ownScene.TrySeatRotation(self, out var seatRotation) ? seatRotation : null;
        if (!_mouseLook.Active) Controls.Frame(now, dt);   // the Alt keys turn, move and pan the view
        // the region's wall: the camera stays on the avatar's side of it (the region states it in its own frame, the camera is in the anchor's)
        _follow.CollidePlane = _presented is { } wall ? RegionGround.PlaneInAnchorFrame(wall.CameraCollidePlane, wall.World.OriginOffset) : default;
        if (Viewer.Core.Avatars.AgentAnimations.InAir(self.Playing)) _inAirSince ??= now;
        else _inAirSince = null;
        var velocity = _presented?.World.Avatar(self.Id)?.Velocity ?? default;
        var centre = soles + new System.Numerics.Vector3(0, 0, height / 2);
        _agentCentre = centre;
        _addonPlacedIn = (_presented?.World, _presented?.World.Epoch ?? 0);
        var (eye, target) = _mouseLook.Active && _driver is not null
            ? _follow.MouselookPose(centre, height, yaw, _driver.Input.Pitch, dt)
            : _follow.Pose(centre, yaw, dt, velocity, _inAirSince is { } since ? (float)(now - since) : 0f);
        if (SeatCameraPose(soles, yaw, height) is { } seatPose)
            (eye, target) = seatPose;   // a seat's camera, or the automation's aim
        (_eye, _look) = (eye, target);
        var e = new Vector3(eye.X, eye.Z, -eye.Y);
        var t = new Vector3(target.X, target.Z, -target.Y);
        if (!e.IsEqualApprox(t)) _camera.LookAtFromPosition(e, t, Vector3.Up);
        _following = true;
    }

    /// <summary>Right-drag orbits the follow camera about the user's own avatar; the wheel zooms. The camera only.</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (_presenter is null) return;
        // a click that no panel took is a click in the world: the keyboard goes back to the world (llviewerwindow / lltoolpie.cpp:
        // gFocusMgr.setKeyboardFocus(NULL)), so the arrow keys move the avatar instead of moving the focus between panels
        if (e is InputEventMouseButton { Pressed: true })
            GetViewport().GuiReleaseFocus();
        if (_mouseLook.Active)
        {
            // mouse-look: the mouse steers and aims, the left button goes to a script that wants it, turning the wheel out or Escape leaves;
            // the keys are the first-person ones (below)
            switch (e)
            {
                case InputEventMouseMotion mm:
                    _lookDx += mm.Relative.X;
                    _lookDy += mm.Relative.Y;
                    GetViewport().SetInputAsHandled();
                    return;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } lb:
                    _driver?.Input.LeftButton(lb.Pressed);
                    GetViewport().SetInputAsHandled();
                    return;
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                    _mouseLook.WheelOut();
                    GetViewport().SetInputAsHandled();
                    return;
                case InputEventMouseButton:
                    GetViewport().SetInputAsHandled();
                    return;
                case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                    _mouseLook.Escape();
                    GetViewport().SetInputAsHandled();
                    return;
            }
        }
        if (CameraToolInput(e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (SteerInput(e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (InteractInput(e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } b:
                _orbiting = b.Pressed;   // only for the context menu: the official viewer's right button does not turn the view
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
            {
                // llagentcamera.cpp handleScrollWheel: a click away from the user is -1; a zoom past the closest is mouse-look
                bool past = _follow.ScrollWheel(-1);
                _mouseLook.WheelIn(past);
                break;
            }
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.M, CtrlPressed: false, AltPressed: false } when _driver is not null:
                _mouseLook.Toggle();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _follow.ScrollWheel(1);
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape, CtrlPressed: false, AltPressed: false, ShiftPressed: false }:
                _follow.ResetView(changeCamera: true, turnAvatar: false);   // View > Reset View (Esc): the default view, behind the avatar; the avatar is not turned (llviewermenu.cpp handle_reset_view)
                break;
            case InputEventKey ck when !ck.Echo && CameraKeyInput(ck):
                break;
            case InputEventKey { Pressed: true, Echo: false, CtrlPressed: true, ShiftPressed: true } ek when _presenter is not null && EnvironmentShortcut(ek.Keycode) is { } envCommand:
                _shell?.Shell.Commands.Execute(envCommand);
                break;
            case InputEventKey { Pressed: true, Echo: false } sk when sk.Keycode == SkyKey && _presenter is not null:
                // an active gesture bound to F9 wins; otherwise F9 cycles the environment (llgesturemgr.cpp triggerGesture runs first)
                if (!TryGestureKey(sk))
                    CycleSky();
                break;
            case InputEventKey { Echo: false, Keycode: Key.Enter or Key.KpEnter or Key.KpDivide } ck when ChatKey(ck):
                break;
            case InputEventKey k when !k.Echo && KeyName(k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode) is { } name:
                if (!MoveKey(name, k.Pressed, k.ShiftPressed, synthetic: false, control: k.CtrlPressed, alt: k.AltPressed))
                    return;
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    private System.Numerics.Vector3 RayDirection(float x, float y)
    {
        var d = _camera!.ProjectRayNormal(new Vector2(x, y));
        return new System.Numerics.Vector3(d.X, -d.Z, d.Y);
    }

    private Vector2 _steerDownAt;

    /// <summary>Left-drag steering (lltoolpie.cpp handleHover): the left button held on the world, dragged past three pixels, grabs the picked
    /// point and turns the avatar so the mouse keeps pointing at it; a click on the own avatar grabs a point three metres in front of it.
    /// The press and a plain click go on to the interaction; a drag that steered is not a click (the release is given a pick of nothing).</summary>
    private bool SteerInput(InputEvent e)
    {
        if (_presented is not { } shown || _driver is null || _camera is null || _mouseLook.Active || _cameraTool) return false;
        // in build mode the left button is the tools' and their handles': a drag moves the handle, never the avatar
        if (_edit is { OwnsLeftButton: true } && !_steer.Pressed) return false;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, AltPressed: false } b:
            {
                var pick = PickAt(b.Position, includeSelf: true);
                if (OwnAvatarHit(shown, pick))
                {
                    // a left press on the own avatar hands the drag to the camera tool (lltoolpie.cpp handleLeftClickPick): with mouse
                    // steering on, the horizontal movement turns the avatar and the vertical one pitches it (lltoolfocus.cpp pickCallback,
                    // handleHover); the click itself is not passed on
                    bool steering = Viewer.Motion.CameraControls.SteersOnOwnAvatar(MaskOf(b), _follow.Mode == Viewer.Motion.CameraViewMode.ThirdPerson,
                        freezeTime: false, hitOwnAvatar: true);
                    _follow.LockOnAvatarForSteering();   // the focus back on the avatar with its axes reset, as the press does there
                    Controls.MouseDown(MaskOf(b), validPoint: true, mouseSteering: steering);
                    _cameraTool = true;
                    _cameraToolPressAt = b.Position;
                    if (!_scriptedMouseLook) Input.MouseMode = Input.MouseModeEnum.Captured;   // the pointer is hidden and held while the camera is dragged
                    return true;
                }
                _steer.MouseDown(b.Position.X, b.Position.Y);
                _steerDownAt = b.Position;
                _steerPick = pick;
                return false;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _steer.Pressed:
            {
                bool steered = _steer.Steering;
                _steer.MouseUp();
                _steerPick = null;
                if (!steered) return false;
                _interact?.LeftUp(new Viewer.Core.Interaction.PickResult(Viewer.Core.Interaction.PickKind.None, null, null, default, 0f));
                return true;
            }
            case InputEventMouseMotion m when _steer.Pressed:
            {
                var p = m.Position;
                if (!_steer.Steering && _steer.ExceedsThreshold(p.X, p.Y, Viewer.Motion.KeyBindingTable.IsMouseBindUsed(Viewer.Motion.KeyMode.ThirdPerson, "LMB", Viewer.Motion.CameraMask.None)))
                    BeginSteering(shown);
                if (!_steer.Steering) return false;
                _driver.Input.Yaw(_steer.Steer(p.X, p.Y, RayDirection, _eye, _driver.Input.Heading ?? 0f));
                return true;
            }
        }
        return false;
    }

    private Vector2? _cameraToolPressAt;

    /// <summary>Whether a pick is the own avatar as a left click sees it: the avatar itself, or an attachment it wears when neither the attachment
    /// nor its root has a touch handler (lltoolpie.cpp handleLeftClickPick walks from such an attachment to its wearer).</summary>
    private static bool OwnAvatarHit(Viewer.Core.Session.ViewerSession shown, Viewer.Core.Interaction.PickResult? pick)
    {
        if (pick is null) return false;
        if (pick.Kind == Viewer.Core.Interaction.PickKind.Avatar) return pick.Avatar?.AgentId == shown.AgentId;
        if (pick.Kind != Viewer.Core.Interaction.PickKind.Object || pick.Hit is not { } hit) return false;
        // from the prim hit up to the attachment's root: a prim with a touch handler stops the walk (it is touched), a HUD stops it too;
        // the attachment's root is the prim the wearer is named on
        var o = shown.World.Object(hit.Id);
        for (int depth = 0; o is not null && depth < 256; depth++)
        {
            if (((o.Interaction?.Flags ?? 0) & Viewer.Core.Interaction.ObjectFlagBits.HandleTouch) != 0) return false;
            if (o.WearerAgentId is { } wearer)
                return wearer == shown.AgentId && !(o.AttachmentPoint is { } point && Viewer.Core.World.Interest.IsHudPoint(point));
            o = o.ParentLocalId == 0 ? null : shown.World.Object(new Viewer.Core.World.ObjectId(o.Id.Epoch, o.ParentLocalId));
        }
        return false;
    }

    private void BeginSteering(Viewer.Core.Session.ViewerSession shown)
    {
        var pick = _steerPick;
        bool valid = pick is { Kind: not Viewer.Core.Interaction.PickKind.None };
        bool own = pick is not null && (pick.Kind == Viewer.Core.Interaction.PickKind.Avatar && pick.Avatar?.AgentId == shown.AgentId
            || pick.Kind == Viewer.Core.Interaction.PickKind.Object && pick.Hit is { } hit && shown.World.Object(hit.Id)?.WearerAgentId == shown.AgentId);
        var at = System.Numerics.Vector3.Normalize(_look - _eye);
        var far = _eye + RayDirection(_steerDownAt.X, _steerDownAt.Y) * 100f;
        _steer.Begin(_follow.FocusOnAvatar, valid, own, pick?.Point ?? default, far, _agentCentre, _eye, at);
    }

    private Viewer.Motion.CameraControls Controls
    {
        get
        {
            if (_camControls is not null) return _camControls;
            _follow.AutoEnterMouselook = false;   // this class keeps the mouse-look state
            _follow.FaceHeading += heading => _driver?.Input.AdoptHeading(heading);   // the focus came back: the avatar faces where the camera looked
            _follow.YawAvatar = angle => _driver?.Input.Yaw(angle);   // an orbit with the focus on the avatar turns the avatar (cameraOrbitAround: gAgent.yaw)
            _camControls = new Viewer.Motion.CameraControls(_follow);
            _camControls.MouselookRequested += () => _mouseLook.WheelIn(true);
            return _camControls;
        }
    }

    private static Viewer.Motion.CameraMask MaskOf(InputEventWithModifiers e) =>
        (e.ShiftPressed ? Viewer.Motion.CameraMask.Shift : 0) | (e.CtrlPressed ? Viewer.Motion.CameraMask.Control : 0) | (e.AltPressed ? Viewer.Motion.CameraMask.Alt : 0);

    /// <summary>The camera tool (lltoolfocus.cpp): Alt, Ctrl+Alt or Ctrl+Alt+Shift with the left button. A click on an object or the ground
    /// focuses it (Alt-click), a click on the sky does nothing; the drag then orbits, pans or zooms by the masks (<see cref="Viewer.Motion.CameraControls"/>).</summary>
    private bool CameraToolInput(InputEvent e)
    {
        if (_presented is null || _driver is null) return false;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, AltPressed: true } b:
            {
                var pick = PickAt(b.Position, includeSelf: true);
                bool valid = pick.Kind != Viewer.Core.Interaction.PickKind.None;
                if (valid) FocusOnPick(pick);
                Controls.MouseDown(MaskOf(b), valid);
                _cameraTool = true;
                if (!_scriptedMouseLook) Input.MouseMode = Input.MouseModeEnum.Captured;   // the pointer is hidden while the camera is dragged
                return true;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _cameraTool:
            {
                bool steered = Controls.MouseSteering;
                Controls.MouseUp();
                _cameraTool = false;
                if (!_mouseLook.Active)
                {
                    Input.MouseMode = Input.MouseModeEnum.Visible;
                    // after mouse steering the pointer comes back where the button went down (lltoolfocus.cpp handleMouseUp); only a window
                    // that has the system's focus moves the pointer, so a window working in the background never takes it
                    if (steered && _cameraToolPressAt is { } back && GetWindow().HasFocus()) GetViewport().WarpMouse(back);
                }
                _cameraToolPressAt = null;
                return true;
            }
            case InputEventMouseMotion m when _cameraTool:
                Controls.MouseMoveScreen(m.Relative.X, m.Relative.Y, MaskOf(m), GetViewport().GetVisibleRect().Size.X);
                return true;
        }
        return false;
    }

    /// <summary>Alt-click: the clicked object, avatar or point becomes the focus (llagentcamera.cpp setFocusGlobal); the own avatar keeps its head as the focus.</summary>
    private void FocusOnPick(Viewer.Core.Interaction.PickResult pick)
    {
        if (_presented is not { } s) return;
        var world = s.World;
        _follow.UnlockView();
        switch (pick.Kind)
        {
            case Viewer.Core.Interaction.PickKind.Object when pick.Hit is { } hit && world.Object(hit.Id) is { } o:
            {
                var offset = pick.Point - o.Position;
                _follow.SetFocus(pick.Point, () => Viewer.Core.Interaction.ObjectPicker.WorldPose(world, o, out var p, out _) ? p + offset : pick.Point, o.Scale, isAvatar: false);
                break;
            }
            case Viewer.Core.Interaction.PickKind.Avatar when pick.Avatar is { } a && a.AgentId != s.AgentId:
            {
                var id = a.Id;
                var offset = pick.Point - a.Position;
                _follow.SetFocus(pick.Point, () => world.Avatar(id)?.Position + offset ?? pick.Point, extents: null, isAvatar: false);
                break;
            }
            case Viewer.Core.Interaction.PickKind.Land:
                _follow.SetFocus(pick.Point);
                break;
        }
    }

    /// <summary>A key with Alt held that the camera uses (key_bindings.xml), or the release of one that was held. True when it was a camera key.</summary>
    private bool CameraKeyInput(InputEventKey k)
    {
        var code = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
        double now = _cameraClock.Elapsed.TotalSeconds;
        if (!k.Pressed)
        {
            if (!_cameraKeysDown.Remove(code, out var held)) return false;
            Controls.KeyUp(held);
            return true;
        }
        if (_mouseLook.Active || KeyName(code) is not { } name) return false;
        // key_bindings.xml: the sitting table while seated (its plain keys move the camera), the third-person one otherwise (Alt keys)
        bool sitting = _interact is { Sitting: true };
        if (!sitting && !k.AltPressed) return false;
        var mode = sitting ? Viewer.Motion.KeyMode.Sitting : Viewer.Motion.KeyMode.ThirdPerson;
        if (!Viewer.Motion.KeyBindingTable.TryFind(mode, name, MaskOf(k), out var command)) return false;
        var route = Viewer.Motion.KeyBindingTable.Route(command);
        if (route.Kind != Viewer.Motion.KeyRouteKind.Camera) return false;
        if (sitting && _driver is not null)
        {
            // a script that took the control, or a seat with its own camera, gets the avatar's key instead of the camera
            bool seatCamera = _interact is { Seat: { } seat } && (seat.EyeOffset != default || seat.AtOffset != default);
            var input = _driver.Input;
            if (Viewer.Motion.KeyBindingTable.SendsAvatarKey(route, input.Script, seatCamera, input.Running, input.AlwaysRun))
            {
                if (input.Focused && route.Fallback is { } avatarKey)
                {
                    _keysDown[name] = avatarKey;
                    input.Press(avatarKey, now, false);
                }
                return true;
            }
        }
        _cameraKeysDown[code] = route.Camera;
        Controls.KeyDown(route.Camera, now);
        return true;
    }

    /// <summary>A bound key went down or up: the avatar's controls take it (false: the key moves nothing). A key's release
    /// releases what its press started, whatever Shift is doing by then.</summary>
    private bool MoveKey(string name, bool down, bool shift, bool synthetic, bool control = false, bool alt = false)
    {
        if (_driver is null || _bindings is null) return false;
        double now = _cameraClock.Elapsed.TotalSeconds;
        if (name == ShiftKeyName)   // the fast-flight key: bound to no movement, and the press is left for whatever else reads it
        {
            // a Shift typed into a text field (a capital, Shift+Enter) is the text's, also where the arrow keys still move the avatar
            if (down && !synthetic && _shell?.Shell.Focus.TextEntryActive == true) return false;
            _driver.Input.SetFastKey(down, synthetic);
            _shiftFromKeyboard = down && !synthetic;
            return false;
        }
        if (!down)
        {
            if (!_keysDown.Remove(name, out var held)) return false;
            _driver.Input.Release(held);
            return true;
        }
        if (!_bindings.TryCommand(name, shift, out var cmd, control, _mouseLook.Active, alt)) return false;
        if (!synthetic && !_driver.Input.Focused) return true;
        if (name is "LEFT" or "RIGHT" or "UP" or "DOWN") _arrowTrace.WorldReceived();
        _keysDown[name] = cmd;
        _driver.Input.Press(cmd, now, synthetic);
        if (cmd is Viewer.Motion.MoveCommand.ToggleFly or Viewer.Motion.MoveCommand.ToggleRun) _keysDown.Remove(name);   // a toggle holds nothing
        return true;
    }

    private const string ShiftKeyName = "SHIFT";
    private bool _shiftFromKeyboard;

    /// <summary>The bindings file's name for a key (the official viewer's key names); null for a key no binding can use.</summary>
    internal static string? KeyName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => k.ToString().ToUpperInvariant(),
        Key.Left => "LEFT", Key.Right => "RIGHT", Key.Up => "UP", Key.Down => "DOWN",
        Key.Pageup => "PGUP", Key.Pagedown => "PGDN", Key.Home => "HOME", Key.Space => "SPACE", Key.Shift => ShiftKeyName,
        _ => null,
    };

    /// <summary>The key bindings file: --keys=&lt;file&gt;, else NV_KEYS; none: the official viewer's defaults.</summary>
    private static string? KeysFile()
    {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--keys=", StringComparison.Ordinal));
        string? file = arg is not null ? arg["--keys=".Length..] : System.Environment.GetEnvironmentVariable("NV_KEYS");
        return string.IsNullOrWhiteSpace(file) ? null : file.Trim().Trim('"');
    }

    private void CountStep(AvatarEntry self, System.Numerics.Vector3 soles, double dt)
    {
        var v = _presented?.World.Avatar(self.Id)?.Velocity ?? default;
        float speed = new System.Numerics.Vector2(v.X, v.Y).Length();
        if (_lastSoles is { } last && dt > 0 && speed > 0.5f)
        {
            float ratio = (soles - last).Length() / (speed * (float)dt);
            _movingFrames++;
            _largestRatio = System.Math.Max(_largestRatio, ratio);
            _longestFrame = System.Math.Max(_longestFrame, dt);
            if (ratio < 0.25f) _stalledFrames++;
            if (ratio > 2f) _jumpFrames++;
        }
        _lastSoles = soles;
    }

    /// <summary>The user's own avatar as the region states it and as it is drawn, with its controls (observe).</summary>
    private object? SelfReport()
    {
        if (_presented is null || OwnAvatars is not { } avatars || avatars.Scene.Self is not { } self) return null;
        var stated = _presented.World.Avatar(self.Id);
        bool placed = avatars.Scene.Place(self, out var soles, out float yaw);
        static double Deg(float radians) => System.Math.Round(radians * 180 / System.Math.PI, 2);
        return new
        {
            region_position = stated is null ? null : new[] { stated.Position.X, stated.Position.Y, stated.Position.Z },
            region_velocity = stated is null ? null : new[] { stated.Velocity.X, stated.Velocity.Y, stated.Velocity.Z },
            region_heading_deg = stated is null ? (double?)null : Deg(Viewer.Core.Avatars.AvatarMotion.Yaw(stated.Rotation)),
            drawn_soles = placed ? new[] { soles.X, soles.Y, soles.Z } : null,
            drawn_heading_deg = placed ? Deg(yaw) : (double?)null,
            drawn_head = _bodyHost is not null && _bodyHost.TryDrawnHead(self.AgentId, out var headAt) ? new[] { headAt.X, headAt.Y, headAt.Z } : null,
            drawn_seat = _bodyHost is not null && _bodyHost.TryDrawnSeat(self.AgentId, out var seatAt) ? new[] { seatAt.X, seatAt.Y, seatAt.Z } : null,
            foot_plane = stated?.FootPlane is { } fp ? new[] { fp.X, fp.Y, fp.Z, fp.W } : null,   // the collision plane the region sends with the avatar (normal, offset)
            avatar_body = new { host = _bodyHost is not null, built = _bodyHost?.Layer is not null, character_folder_given = CharacterFolderGiven() },   // false: no body is drawn, so the drawn_* readings below are empty
            drawn_limbs = _bodyHost?.DrawnLimbs(self.AgentId),
            shape_dump = _bodyHost?.ShapeDump(self.AgentId),   // the weights, face joints and morph fingerprints the body is drawn with now
            terrain_z = placed ? avatars.Scene.GroundAt(soles) : null,   // the terrain's height under the drawn soles, and under the drawn seat
            terrain_under_seat_z = _bodyHost is not null && _bodyHost.TryDrawnSeat(self.AgentId, out var seatOn) ? avatars.Scene.GroundAt(seatOn) : null,
            local_heading_deg = _driver?.Input.Heading is float h ? Deg(h) : (double?)null,
            controls = _driver is null ? null : "0x" + _driver.Input.Controls.ToString("x"),
            flying = _driver?.Input.Flying,
            fast_flying = _driver?.Input.FastFlying,
            focused = _driver?.Input.Focused,
            keys_held = _keysDown.Keys.ToArray(),
            updates_sent = _driver?.Sent,
            seat = self.Seat,
            playing = Viewer.Core.Avatars.AgentAnimations.NamesOf(self.Playing).ToArray(),
            appearance = AppearanceReport(self),
            smoothness = new { moving_frames = _movingFrames, stalled_frames = _stalledFrames, jump_frames = _jumpFrames, largest_step_ratio = System.Math.Round(_largestRatio, 2), longest_frame_ms = System.Math.Round(_longestFrame * 1000, 1) },
        };
    }

    /// <summary>Why <see cref="SelfReport"/> has nothing: which presenter holds the agent, how many avatars its scene and the current world hold, and
    /// whether the world holds the agent's own. Null while the own avatar is reported.</summary>
    private object? SelfMissingReport()
    {
        if (_presented is not { } session || OwnAvatars is { } have && have.Scene.Self is not null)
            return null;
        var avatars = OwnAvatars;
        var snap = session.World.Snapshot();
        return new
        {
            presenter = _currentStack is not null ? "neighbour" : _presenter is not null ? "anchor" : "none",
            scene_avatars = avatars?.Scene.Count,
            world_avatars = snap.Avatars.Count,
            world_holds_agent = snap.Avatars.Values.Any(a => a.AgentId == session.AgentId),
            driver_sent = _driver?.Sent,
        };
    }

    /// <summary>What the region sent for the user's own avatar's look, and what it wears: the appearance mode, the bake texture ids
    /// by slot, the visual-parameter count and the versions, and each worn object's attachment point, shape kind and the
    /// number of its faces that name a bake-on-mesh placeholder. Own avatar only (observe).</summary>
    private object AppearanceReport(Viewer.Shell.AvatarEntry self)
    {
        var world = _presented!.World;
        var look = world.Appearance(self.AgentId);
        var worn = world.AttachmentRoots(self.Id.LocalId).Select(o =>
        {
            int bakeFaces = 0, faces = 0;
            if (o.Textures is { } t)
                for (int f = 0; f < System.Math.Max(t.Overrides.Count, 1); f++) { faces++; if (Viewer.Core.Avatars.BakeChannels.IsBakeId(t[f].TextureId)) bakeFaces++; }
            return new { point = o.AttachmentPoint, kind = o.Shape is null ? "unknown" : o.Shape.SculptType == Viewer.Core.Meshing.PrimShape.SculptTypeMesh ? "mesh" : o.Shape.IsSculpt ? "sculpt" : "prim", faces, bake_faces = bakeFaces };
        }).ToArray();
        return new
        {
            mode = world.AppearanceMode.ToString(),
            received = look is not null,
            version = look?.AppearanceVersion,
            cof_version = look?.CofVersion,
            visual_params = look?.VisualParams.Count,
            sex = look?.Sex,
            bakes = look?.Bakes.ToDictionary(kv => kv.Key, kv => kv.Value),
            face_textures_set = look?.FaceTextures.Count(g => g != Guid.Empty),
            worn_count = worn.Length,
            worn,
        };
    }

    /// <summary>Whether the log names the login host and port, for debugging a connection: --log-login-host, or
    /// NV_LOG_LOGIN_HOST=1. Off by default.</summary>
    private static bool LogLoginHost() =>
        OS.GetCmdlineUserArgs().Contains("--log-login-host") || System.Environment.GetEnvironmentVariable("NV_LOG_LOGIN_HOST") == "1";

    /// <summary>The mesh LOD factor: --mesh-lod-factor=&lt;f&gt;, else NV_MESH_LOD_FACTOR, else the default (2.0); clamped
    /// to 0.01..4 as the official viewer clamps its setting. Higher keeps meshes at their detailed levels further out.</summary>
    private static float MeshLodFactor()
    {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--mesh-lod-factor=", StringComparison.Ordinal));
        return Viewer.Core.Meshing.MeshLodRule.Parse(arg?["--mesh-lod-factor=".Length..])
            ?? Viewer.Core.Meshing.MeshLodRule.Parse(System.Environment.GetEnvironmentVariable("NV_MESH_LOD_FACTOR"))
            ?? Viewer.Core.Meshing.MeshLodRule.DefaultLodFactor;
    }

    /// <summary>The character folder (the avatar data the bodies are drawn from): --character=&lt;folder&gt;, else NV_CHARACTER_DIR,
    /// else, in an exported build, the folder shipped beside the executable.</summary>
    private static string? CharacterFolder() => CharacterChoice().Path;

    private static Viewer.Core.Avatars.CharacterFolderChoice CharacterChoice()
    {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--character=", StringComparison.Ordinal));
        string? exeFolder = OS.HasFeature("template") ? System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) : null;
        return Viewer.Core.Avatars.CharacterFolderChoice.Resolve(arg?["--character=".Length..], System.Environment.GetEnvironmentVariable("NV_CHARACTER_DIR"),
            exeFolder, System.IO.Directory.Exists);
    }

    private static bool CharacterFolderGiven() => CharacterFolder() is { } f && System.IO.Directory.Exists(f);

    /// <summary>The body, read once per process (a later session reuses it).</summary>
    private static System.Threading.Tasks.Task<AvatarPresenter.LoadedBody?>? Body()
    {
        var folder = CharacterFolder();
        if (folder is null)
        {
            Log.Info("avatars.body", CoreLog.F("state", "off"), CoreLog.F("why", "no character folder (--character=, NV_CHARACTER_DIR or the shipped character folder): avatars are shown by their name tags only"));
            return null;
        }
        if (s_body is null || s_bodyFolder != folder)
        {
            s_bodyFolder = folder;
            s_body = AvatarPresenter.LoadBody(folder, ex => Log.Warn("avatars.body", CoreLog.F("state", "unreadable"), CoreLog.F("error", ex.GetType().Name), CoreLog.F("message", ex.Message)));
        }
        return s_body;
    }

    private void Present(ViewerSession session)
    {
        Unpresent();
        _stage = new Node3D { Name = "world" };
        AddChild(_stage);
        var envNode = Stage.Environment();
        var sunNode = Stage.Sun();
        _stage.AddChild(envNode);
        _stage.AddChild(sunNode);
        _camera = new Camera3D { Current = true, Fov = CameraDefaults.VerticalFovDegrees };
        _stage.AddChild(_camera);
        // the terrain representation: the RenderingServer one (ADR-003, chosen by the measurement; the node one is the
        // harness's comparison, TestRender --tr-measure)
        ITerrainView view = new ServerTerrainView(Scenario());
        // the encrypted content store (PerUser on the desktop; a disk hit is served only after the session's
        // authorisation, ADR-006).
        var contentView = ContentStore().View(session.Region?.Name ?? "grid", session.LogTag);
        // this session's meshes, fetched through its own transport (the grid's mesh capability) via SessionAssets.
        // Authorisation: OpenSim does not permission-gate mesh GETs, so this host allows them; a per-grid choice (ADR-006).
        // through the content store too, so a mesh the bounded shared asset store let go is read back
        // from disk, not fetched from the grid again (as textures are).
        _meshes = _meshTransport is { } mt
            ? new MeshSource(new Viewer.Core.Assets.SessionMeshes(new Viewer.Core.Assets.SessionAssets(_sharedAssets, _ => true, contentView.Caching("mesh", mt.FetchMeshAsync)),
                (id, b) => { var r = Viewer.Core.Protocol.LmvMesh.Decode(id, b); return (r.Geometry, r.Refusal); }))
            : null;
        // this session's textures through its own transport, the per-session asset access and the encrypted content
        // store (PerUser on the desktop; a disk hit is served only after the session's authorisation, ADR-006).
        // with a range-capable transport, a level fetches only the bytes it needs (partial entries in
        // the same content store); a failed partial fetch falls back to the transport's ordinary whole-file fetch.
        // --texture-fetch=whole turns partial fetch off (diagnostic, for the live comparison).
        bool partial = !OS.GetCmdlineUserArgs().Contains("--texture-fetch=whole");
        _contentView = contentView;
        _wholeFetches = new Viewer.Core.Assets.FetchCounter();   // whole-file fetches (whole mode and fallbacks), counted
        _fetcher = _textureTransport is { } t0 && partial && t0 is Viewer.Core.Assets.ITextureRangeTransport rt
            ? new Viewer.Core.Assets.PartialTextureFetcher(rt.GetTextureRangeAsync, _wholeFetches.Wrap(t0.FetchTextureAsync), contentView)
            : null;
        _textures = _textureTransport is { } tt
            ? new TextureSource(new Viewer.Core.Assets.SessionAssets(_sharedAssets, _ => true, contentView.Caching("texture", _wholeFetches.Wrap(tt.FetchTextureAsync)),
                fetchLevel: _fetcher is { } pf ? pf.FetchLevelAsync : null))
            : null;
        // this session's PBR material assets (small glTF documents) through its own transport, read off the main thread
        _materials = _materialTransport is { } mat ? new MaterialSource(mat.FetchMaterialAsync, Log) : null;
        // every texture's stages and every texture request, timed (measurement only)
        _timeline = new Viewer.Core.Assets.TextureTimeline();
        if (_textures is not null) _textures.Timeline = _timeline;
        // a texture whose partial codestream did not decode is logged once, with how it ended
        if (_textures is not null) _textures.Log = Log;
        if (_textureTransport is Viewer.Core.Assets.ITextureRangeTransport timed) timed.Timeline = _timeline;
        // base-colour textures are fetched before detail maps: the residency manager's classes, read by the transport
        if (_textureTransport is Viewer.Core.Assets.ITextureRangeTransport classed && _textures is not null) classed.FetchClasses = _textures.FetchClasses;
        // texture decodes on their own threads with a deadline; overruns are logged through this log
        var decoder = Viewer.Core.Imaging.DecodeScheduler.Textures;
        decoder.Log = Log;
        Log.Info("texture.decode", CoreLog.F("threads", decoder.Threads), CoreLog.F("deadline_s", (long)decoder.Deadline.TotalSeconds), CoreLog.F("cap", decoder.MaxWrittenOff));
        Log.Info("texture.fetch", CoreLog.F("mode", _textures is null ? "none" : partial && _textureTransport is Viewer.Core.Assets.ITextureRangeTransport ? "partial" : "whole"));
        // the texture budget from half the card's video memory minus the reserves, following the engine's
        // non-texture memory live; or --texture-budget-mib=
        var (budget, vram) = VideoMemory.TextureBudget();
        _budget = budget;
        _presentedFor = System.Diagnostics.Stopwatch.StartNew();
        // every move of the live budget, with the non-texture figure it came from
        budget.Changed += c => Log.Info("residency.budget.change", CoreLog.F("from-mib", c.Previous / Viewer.Core.Residency.TextureBudget.MiB),
            CoreLog.F("to-mib", c.Budget / Viewer.Core.Residency.TextureBudget.MiB), CoreLog.F("non-texture-mib", c.NonTexture / Viewer.Core.Residency.TextureBudget.MiB));
        Log.Info("residency.budget", CoreLog.F("budget-mib", budget.Budget / Viewer.Core.Residency.TextureBudget.MiB), CoreLog.F("video-memory-mib", vram is { } v ? v / Viewer.Core.Residency.TextureBudget.MiB : -1), CoreLog.F("source", budget.Source));
        // this session's animation assets through its own transport (the region's viewer asset capability) and the content
        // store, read off the main thread, at most four fetches at a time
        _animationAssets = _animationTransport is { } at
            ? new Viewer.Core.Assets.SessionAssets(_sharedAssets, _ => true, contentView.Caching("animation", at.FetchAnimationAsync))
            : null;
        _animations = _animationAssets is { } aa ? new AnimationSource(aa.GetAsync) : null;
        var scenario = Scenario();
        JitWarmup.Start();   // the presentation code is compiled on a thread of its own while the region loads, not in the first frame an avatar arrives
        var prims = new PrimLayer(scenario, session.World, _meshes, _textures, budget.Budget) { LiveBudget = budget, MaterialAssets = _materials, LodFactor = MeshLodFactor() };
        prims.Moving.HasRegionAt = RegionAtFor(session, session.World);   // an object predicted off the edge is carried on only where a region lies
        prims.Moving.Selected = local => SelectedHere(session.World, local);   // a selected object is not carried (LLViewerObject::idleUpdate)
        _bodyHost = new AvatarBodyHost(scenario, Body());   // one body and skeleton for the avatars of every region: an avatar that crosses is the same drawn avatar
        _handoffLedger.OwnMotion.Reset();   // a new presentation: the own avatar starts as a new object
        var avatars = new AvatarPresenter(scenario, session.World, session.AgentId, Body(), _animations, _textures, prims.Residency) { Handoffs = _handoffLedger, Host = _bodyHost };
        avatars.Scene.HasRegionAt = RegionAtFor(session, session.World);   // an avatar predicted off the edge is carried on for at most a second only where a region lies
        prims.Handoffs = _handoffLedger;
        avatars.HandedOver += StartHandoffSample;
        avatars.SelfMotionRanOut += id => _driver?.Input.AnimationStopped(id, _cameraClock.Elapsed.TotalSeconds);   // the end of stand up, pre-jump and landing goes to the region
        _handoffLedger.Exists = session.Identity.Holds;
        _handoffLedger.Locate = session.Identity.CurrentAvatar;
        _attachmentFrames = avatars;
        _hoverAvatars = avatars;
        HookRiggedLogs(prims, session);
        _bindings ??= KeyBindings.Load(KeysFile(), Log);
        _driver = new OwnAvatarDriver(avatars.Scene, session.World,
            u => { bool sent = session.SendAgentUpdate(u); if (sent) _motionTrace.Sent(_cameraClock.Elapsed.TotalSeconds, u.Controls); return sent; },
            run => { bool sent = session.SendAlwaysRun(run); if (sent) _motionTrace.RunTold(_cameraClock.Elapsed.TotalSeconds, run); return sent; });
        // the render state byte: typing while the local chat input has text being typed (llagent.cpp getRenderState). Nothing in this viewer sets the
        // editing bit (no build tool), the hide-title flag (no setting) or the autopilot flag (no autopilot), so those bytes stay clear.
        _driver.StateSource = () => (Viewer.Motion.AgentRenderStates.State(_comms?.Nearby.OwnTyping ?? false, false), Viewer.Motion.AgentRenderStates.Flags(false, false));
        _driver.Input.FastFlightAllowed = session.Allows(Viewer.Core.Session.ViewerFeature.FastFlight);
        _driver.Input.AdoptFlying(UserSettings(null).Get(Settings.FlyingAtExit));   // the saved state of the last session's end, not the region's first animations
        _driver.Input.SetFocused(_shell?.Shell.Focus.DriverFocused ?? GetWindow().HasFocus());   // not while a text field has the keyboard, unless arrow keys always move
        _driver.Input.WalkKeyPressed += _follow.ResetOrbit;   // moving puts the camera back behind the avatar, as the official viewer's does
        _driver.Input.Script = session.ScriptControls;        // what scripts in the region have taken (the left button in mouse-look)
        MouseLookSetting.Apply(_driver.Input);
        _mouseLook.Changed -= OnMouseLookChanged;
        _mouseLook.Changed += OnMouseLookChanged;
        _crosshairLayer = new CanvasLayer { Layer = 10 };
        _crosshair = new Crosshair { Visible = false };
        _crosshairLayer.AddChild(_crosshair);
        AddChild(_crosshairLayer);
        _keysDown.Clear();
        _follow.Reset();
        _inAirSince = null;
        _lastCameraAt = -1;
        _presenter = new WorldPresenter(session.World, view, prims: prims, terrainTextures: _textures,
            environment: new EnvironmentLayer(scenario, envNode, sunNode) { CloudsEnabled = UserSettings(null).Get(Settings.CloudsEnabled) }, avatars: avatars)
        { Sky = _skyChoice, FixedSkies = _fixedSkies, CloudsPaused = EnvChoice.CloudsPaused, Personal = _personalLighting, OutsideHeight = session.NeighboursEnabled ? OutsideHeightFor(session, session.World) : null };   // water and the region's sky
        _glow = new GlowPass(prims, World(), envNode);   // the glow of faces that glow; draws nothing while none is loaded
        AddChild(_glow);
        _presented = session;
        if (session.NeighboursEnabled) AttachNeighbours(session);
        _cameraFor = (0, 0);
        if (_loginScreen is not null) _loginScreen.Visible = false;
        GetViewport().GuiReleaseFocus();   // the login panel closed: the keyboard goes back to the world (llstartup.cpp, gFocusMgr.setKeyboardFocus(NULL) on entering the started state)
        Log.Info("presentation.attached", CoreLog.F("view", view.Kind), CoreLog.F("avatars_already_held", session.World.Snapshot().Avatars.Count));   // avatars that arrived before the presentation listened
        ReloadKeptSky();
        ApplyAllPreferences();
    }

    private GlowPass? _glow;
    private AvatarBodyHost? _bodyHost;   // the avatar body and skeleton every region of the session draws its avatars with

    /// <summary>The time-of-day choice: --sky=&lt;sunrise|midday|sunset|midnight|region&gt; at start (default Region, the region's own clock); the
    /// <see cref="SkyKey"/> key cycles it while in world.</summary>
    private Viewer.Core.World.SkyChoice _skyChoice => EnvChoice.Current;
    internal const Key SkyKey = Key.F9;
    private StringTable? _strings;

    internal static Viewer.Core.World.SkyChoice SkyFromArgs(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--sky=", StringComparison.Ordinal));
        return arg is not null && Viewer.Core.World.SkyChoices.TryParse(arg["--sky=".Length..].Trim('"'), out var c) ? c : Viewer.Core.World.SkyChoice.Region;
    }

    /// <summary>The F9 key: the next choice of the Environment menu (Region, Sunrise, Midday, Sunset, Midnight, Region). Runs the menu's own cycle command.</summary>
    private void CycleSky()
    {
        if (_shell is null || !_shell.Shell.Commands.Execute(Viewer.Shell.EnvironmentMenu.CycleCommand)) EnvChoice.Cycle();
    }

    private void Unpresent()
    {
        if (_presenter is null)
            return;
        ReleaseSnapshot();   // a picture held for the Snapshot window does not outlive the session it shows
        _mouseLook.Leave();   // the mouse is let go and the own avatar drawn again before anything is torn down
        // Every step runs even when an earlier one throws: a skipped dispose leaves engine resources live at quit,
        // and the engine corrupts its heap when it quits with them live. The exceptions are reported and raised
        // together at the end.
        TeardownSteps.RunAll(msg => Log.Warn("teardown.step-failed", CoreLog.F("detail", msg)), (name, ms) => Log.Info("teardown.step-slow", CoreLog.F("step", name), CoreLog.F("ms", Math.Round(ms, 1))),
            ("ban wall", FreeBanWall),
            // the tags are drawn over the main view, which stays: the frame loop that plans them stops with the world
            ("name tags", () => { _nameTags?.HideAll(); _lastTags = []; }),
            ("glow", () => { if (_glow is not null) { RemoveChild(_glow); _glow.Free(); _glow = null; } }),
            ("neighbours", DetachNeighbours),
            ("presenter", () => _presenter?.Dispose()),
            ("avatar body", () => { _bodyHost?.Dispose(); _bodyHost = null; }),
            ("meshes", () => { _meshes?.Dispose(); _meshes = null; }),
            ("textures", () => { _textures?.Dispose(); _textures = null; }),
            ("materials", () => { _materials?.Dispose(); _materials = null; }),
            ("animations", () => { _animations?.Dispose(); _animations = null; }),
            ("animation assets", () => { _animationAssets?.Dispose(); _animationAssets = null; }),
            ("input state", () =>
            {
                _mouseLook.Changed -= OnMouseLookChanged;
                if (_crosshairLayer is not null) { RemoveChild(_crosshairLayer); _crosshairLayer.Free(); _crosshairLayer = null; _crosshair = null; }
                _orbiting = false;
                _following = false;
                if (_driver is not null)
                {
                    Log.Info("agent.controls.totals", CoreLog.F("updates-sent", _driver.Sent), CoreLog.F("flying", _driver.Input.Flying));
                    UserSettings(null).Store(Settings.FlyingAtExit, _driver.Input.Flying);   // the next login starts flying only if this one ended flying
                }
                _driver = null;
                _keysDown.Clear();
                (_lastSoles, _movingFrames, _stalledFrames, _jumpFrames, _largestRatio, _longestFrame) = (null, 0, 0, 0, 0, 0);
            }),
            ("live check", LogLiveCheck),
            ("transport", () =>
            {
                if (_textureTransport is Viewer.Core.Assets.ITextureRangeTransport timed) { timed.Timeline = null; timed.FetchClasses = null; }
            }),
            ("references", () =>
            {
                _timeline = null;
                _fetcher = null;
                _wholeFetches = null;
                _budget = null;
                _contentView = null;
                _overview = null;
                if (_presenter is not null) _presenter.Personal = null;
                _presenter = null;
                _presented = null;
            }),
            ("interaction layers", () =>
            {
                // the outline, face marker, floating text and edit handles hang under the stage and go with it below; the fields let go of
                // them, so the next session makes its own instead of calling into freed nodes (which threw every frame before the world drew)
                _outline = null;
                _faceHighlight = null;
                _hoverText = null;
                _handles = null;
                _handleDrag = null;
                _handleHot = Viewer.Shell.Build.Handle.None;
                _handlesDrawnAt = null;
            }),
            ("stage", () => { _stage?.QueueFree(); _stage = null; _camera = null; }),
            ("login screen", () => { if (_loginScreen is not null) _loginScreen.Visible = true; }));
        Log.Info("presentation.detached");
    }

    /// <summary>The session command's state (automation; also what a status panel would show).</summary>
    public object SessionState()
    {
        var s = Model?.Session;
        var r = s?.Region;
        return new
        {
            busy = Model?.Busy ?? false,
            status = Model?.Status,
            has_session = s is not null,
            stage = s?.Stage.ToString() ?? "Idle",
            ended = s?.IsEnded ?? false,
            end_reason = s?.EndReason.ToString(),
            region = r is null ? null : new { name = r.Name, size_x = r.SizeX, size_y = r.SizeY },
        };
    }

    /// <summary>The observe command: world counts, terrain completeness, presentation backlog, camera.</summary>
    public object Observe()
    {
        var s = Model?.Session;
        var snap = s?.World.Snapshot();
        return new
        {
            stage = s?.Stage.ToString() ?? "Idle",
            world = snap is null ? null : new
            {
                epoch = snap.Epoch,
                objects = snap.Objects.Count,
                attachments = snap.Objects.Values.Count(o => o.AttachmentPoint is not null),
                avatars = snap.Avatars.Count,
                other_avatars = snap.Avatars.Values.Count(a => a.AgentId != s!.AgentId),
            },
            terrain = s is null ? null : new { patches = s.World.TerrainPatchCount, expected = s.World.TerrainExpected, complete = s.World.TerrainComplete },
            presentation = _presenter?.Describe(),
            camera = _camera is null ? null : new
            {
                position = new[] { _camera.GlobalPosition.X, _camera.GlobalPosition.Y, _camera.GlobalPosition.Z }, fov = _camera.Fov,
                mode = _following ? "follow" : "overview",
                follow = new { orbit_yaw_deg = System.Math.Round(_follow.OrbitYaw * 180 / System.Math.PI, 2), orbit_pitch_deg = System.Math.Round(_follow.OrbitPitch * 180 / System.Math.PI, 2), zoom = _follow.Zoom, distance = System.Math.Round(_follow.Distance, 3) },
            },
            fps = Engine.GetFramesPerSecond(),
            self = SelfReport(),
            self_missing = SelfMissingReport(),
            live_check = LiveCheck(),
        };
    }

    /// <summary>Texture fetching, decoding and the budget of this session.</summary>
    private object? LiveCheck()
    {
        if (_presenter is null) return null;
        var first = _textures?.FirstImageMs().OrderBy(x => x).ToArray() ?? [];
        double Pct(double p) => first.Length == 0 ? 0 : System.Math.Round(first[System.Math.Min(first.Length - 1, (int)System.Math.Ceiling(p * first.Length) - 1)], 1);
        var decoder = Viewer.Core.Imaging.DecodeScheduler.Textures;
        const long MiB = Viewer.Core.Residency.TextureBudget.MiB;
        return new
        {
            seconds = System.Math.Round(_presentedFor?.Elapsed.TotalSeconds ?? 0, 1),
            fetch = _fetcher?.Stats,
            answers = _fetcher?.Answers,
            fallback_reasons = _fetcher?.FallbackReasons,
            whole = _wholeFetches is { } w ? new { requests = w.Requests, ok = w.Ok, bytes = w.Bytes } : null,
            textures_requested = _textures?.Requested().Count ?? 0,
            prefix_decodes = _textures?.PrefixDecodes ?? 0,
            more_fetches = _textures?.MoreFetches ?? 0,
            prefix_failures = _textures?.PrefixFailures(),
            refusals = _textures?.Refusals(),
            timeline = _timeline?.Summary(),
            first_image_ms = new { count = first.Length, p50 = Pct(0.5), p95 = Pct(0.95), max = first.Length == 0 ? 0 : System.Math.Round(first[^1], 1) },
            decoder = new { threads = decoder.Threads, written_off = decoder.WrittenOff, created = decoder.Created, stopped = decoder.Stopped },
            budget = _budget is { } b ? new
            {
                mib = b.Budget / MiB, initial_mib = b.Initial / MiB, min_mib = b.Min / MiB, max_mib = b.Max / MiB, changes = b.Changes, source = b.Source,
                history = b.History.Select(c => new { at_s = System.Math.Round(c.At.TotalSeconds, 1), non_texture_mib = c.NonTexture / MiB, from_mib = c.Previous / MiB, to_mib = c.Budget / MiB }).ToArray(),
            } : null,
        };
    }

    /// <summary>The session's texture fetch totals in the log when it ends.</summary>
    private void LogLiveCheck()
    {
        if (_wholeFetches is null) return;
        var s = _fetcher?.Stats;
        Log.Info("texture.fetch.totals", CoreLog.F("range-requests", s?.RangeRequests ?? 0), CoreLog.F("range-bytes", s?.BytesFetched ?? 0),
            CoreLog.F("whole-fallbacks", s?.WholeFallbacks ?? 0), CoreLog.F("whole-requests", _wholeFetches.Requests), CoreLog.F("whole-bytes", _wholeFetches.Bytes));
        if (_timeline is { } tl) Log.Info("texture.timeline", [.. tl.Summary().LogFields()]);   // where the session's texture seconds went
    }

#if NV_AUTOMATION
    /// <summary>The motion trace (the automation API): start clears and records, take returns the samples since the last
    /// take, stop ends recording. Times are seconds on the camera clock; positions are soles in region metres.</summary>
    public object MotionTraceCall(string action)
    {
        switch (action)
        {
            case "start": _motionTrace.Start(); break;
            case "start_all": _motionTrace.Start(allRegions: true); break;
            case "stop": _motionTrace.Stop(); break;
        }
        var rows = action == "take" || action == "stop" ? _motionTrace.Take() : [];
        var sent = action == "take" || action == "stop" ? _motionTrace.TakeSent() : [];
        static float R(float v) => (float)System.Math.Round(v, 3);
        return new
        {
            recording = _motionTrace.Recording, dropped = _motionTrace.Dropped,
            sent = sent.Select(e => new object[] { System.Math.Round(e.At, 4), e.Run is { } r ? "run" : "controls", e.Run is { } run ? run : (object)("0x" + e.Controls.ToString("x")) }).ToArray(),
            samples = rows.Select(r => new object[] { System.Math.Round(r.At, 4), r.LocalId, r.Self, R(r.Drawn.X), R(r.Drawn.Y), R(r.Drawn.Z), R(r.Stated.X), R(r.Stated.Y), R(r.Stated.Z), R(r.Velocity.X), R(r.Velocity.Y), R(r.Velocity.Z), r.Updates, R(r.Camera.X), R(r.Camera.Y), R(r.Camera.Z), r.Agent.ToString(), System.Math.Round(r.Placed, 4) }).ToArray(),
        };
    }

    /// <summary>Presses or releases a bound key as a focused window's keyboard would (the automation API); null when
    /// nothing is presented.</summary>
    public object? AutomationKey(string key, bool down, bool shift, bool control = false)
    {
        if (_driver is null) return null;
        bool taken = MoveKey(key.ToUpperInvariant(), down, shift, synthetic: true, control: control);
        return new { key = key.ToUpperInvariant(), down, shift, control, taken, keys_held = _keysDown.Keys.ToArray() };
    }

    /// <summary>Mouse-look through the automation API, on the same paths as the real mouse and keys: <c>enter</c>, <c>leave</c>, <c>toggle</c>,
    /// <c>look</c> (the mouse moves <paramref name="dx"/> to the right and <paramref name="dy"/> down, in pixels, taken at the next frame),
    /// <c>wheel_in</c> and <c>wheel_out</c> (a notch of the wheel), <c>escape</c>, and <c>button</c> (the left button down or up). Null when nothing is presented.</summary>
    public object? AutomationMouseLook(string action, float dx, float dy, bool down)
    {
        if (_driver is null) return null;
        _scriptedMouseLook = true;   // the pointer is not captured for a scripted change
        bool known = true;
        switch (action)
        {
            case "enter": if (!_mouseLook.Active) _mouseLook.Toggle(); break;
            case "leave": _mouseLook.Leave(); break;
            case "toggle": _mouseLook.Toggle(); break;
            case "look": if (_mouseLook.Active) { _lookDx += dx; _lookDy += dy; } break;
            case "wheel_in":
            {
                bool past = _follow.ScrollWheel(-1);
                _mouseLook.WheelIn(past);
                break;
            }
            case "wheel_out": _mouseLook.WheelOut(); break;
            case "escape": _mouseLook.Escape(); break;
            case "button": _driver.Input.LeftButton(down); break;
            case "state": break;
            default: known = false; break;
        }
        _scriptedMouseLook = false;
        var input = _driver.Input;
        return new
        {
            known, active = _mouseLook.Active, forced = _mouseLook.Forced, allowed = _mouseLook.Allowed,
            pitch_deg = System.Math.Round(input.Pitch * 180 / System.Math.PI, 3),
            heading_deg = input.Heading is float h ? System.Math.Round(h * 180 / System.Math.PI, 3) : (double?)null,
            controls = "0x" + input.Controls.ToString("x"), sensitivity = input.MouseSensitivity, invert = input.InvertMouse, smooth = input.SmoothMouse,
            own_avatar_drawn = OwnAvatars is { } a ? a.Scene.Self is { } self ? a.Scene.Draws(self) : (bool?)null : null,
            left_button_wanted = _presented?.ScriptControls.LeftButtonGrabbed(_mouseLook.Active),
            zoom = _follow.Zoom,
        };
    }

    /// <summary>Turns the camera about the vertical axis by <paramref name="yawDegrees"/>
    /// from its overview pose (0 = back to the start). The camera only; nothing is sent to the grid.</summary>
    public object? SetCameraYaw(double yawDegrees)
    {
        if (_camera is null || _overview is not { } start) return null;
        _camera.GlobalTransform = new Transform3D(new Basis(Vector3.Up, (float)(yawDegrees * System.Math.PI / 180.0)) * start.Basis, start.Origin);
        return new { yaw = yawDegrees };
    }

    /// <summary>Where the horizon is on the screen (the viewport y of a point far away straight ahead at the camera's own height), for scripts that click the far ground.</summary>
    private double? HorizonY()
    {
        if (_camera is null) return null;
        var forward = -_camera.GlobalTransform.Basis.Z;
        forward.Y = 0;
        if (forward.LengthSquared() < 1e-8f) return null;
        var far = _camera.GlobalPosition + forward.Normalized() * 100000f;
        return System.Math.Round(_camera.UnprojectPosition(far).Y, 1);
    }

    /// <summary>The camera operations the mouse and the keys use, by name (the automation API's orbit command with an op): unlock (Alt),
    /// orbit_around_deg and orbit_over_deg (an unlocked drag), pan_left_m and pan_up_m, wheel (clicks, negative zooms in), reset (Esc) and
    /// state. Null for an unknown op. The camera only; nothing is sent to the grid.</summary>
    public object? CameraView(string op, double value)
    {
        const double ToRad = System.Math.PI / 180;
        switch (op)
        {
            case "unlock": _follow.UnlockView(); break;
            case "orbit_around_deg": _follow.UnlockView(); _follow.OrbitAround((float)(value * ToRad)); break;
            case "orbit_over_deg": _follow.UnlockView(); _follow.OrbitOver((float)(value * ToRad)); break;
            case "pan_left_m": _follow.UnlockView(); _follow.PanLeft((float)value); break;
            case "pan_up_m": _follow.UnlockView(); _follow.PanUp((float)value); break;
            case "wheel": { bool past = _follow.ScrollWheel((float)value); _scriptedMouseLook = true; _mouseLook.WheelIn(past); _scriptedMouseLook = false; break; }
            case "reset": _follow.ResetView(changeCamera: true, turnAvatar: false); break;
            case "state": break;
            default: return null;
        }
        return new
        {
            focus_on_avatar = _follow.FocusOnAvatar,
            mode = _follow.Mode.ToString(),
            zoom = _follow.Zoom,
            distance = System.Math.Round(_follow.Distance, 3),
            focus_distance = System.Math.Round(_follow.FocusDistance, 3),
            land_z = _follow.LandHeight is { } land ? System.Math.Round(land(_eye.X, _eye.Y), 3) : (double?)null,
            horizon_y = HorizonY(),
            eye = new[] { _eye.X, _eye.Y, _eye.Z },
            look = new[] { _look.X, _look.Y, _look.Z },
        };
    }

    /// <summary>Sets the follow camera's orbit (degrees about up, and up or down) and zoom (a fraction of the default
    /// distance), within their limits; a null leaves that value. The camera only; nothing is sent to the grid.</summary>
    public object SetCameraOrbit(double? yawDegrees, double? pitchDegrees, double? zoom)
    {
        _follow.SetOrbit(yawDegrees is { } y ? (float)(y * System.Math.PI / 180) : _follow.OrbitYaw, pitchDegrees is { } p ? (float)(p * System.Math.PI / 180) : _follow.OrbitPitch);
        if (zoom is { } z) _follow.SetZoom((float)z);
        return new { following = _following, orbit_yaw_deg = _follow.OrbitYaw * 180 / System.Math.PI, orbit_pitch_deg = _follow.OrbitPitch * 180 / System.Math.PI, zoom = _follow.Zoom };
    }

    /// <summary>Sets the time-of-day choice by name (the sky key's choices); null when the name is not one. The presenter and the window title follow, as the key does it.</summary>
    public async System.Threading.Tasks.Task<object?> AutomationSky(string name)
    {
        if (!Viewer.Core.World.SkyChoices.TryParse(name, out var choice)) return null;
        // the menu's own item runs, as a click would; the reply waits until the sky is in force or could not be loaded
        bool failed = false;
        void OnFailed(Viewer.Core.World.SkyChoice c) { if (c == choice) failed = true; }
        EnvChoice.Failed += OnFailed;
        try
        {
            string item = "env." + Viewer.Core.World.SkyChoices.Label(choice).ToLowerInvariant();
            if (_shell is null || !_shell.Shell.Menus.Activate(item, _shell.Shell.Commands)) _ = EnvChoice.Request(choice);
            var until = global::Godot.Time.GetTicksMsec() + 20000;
            while (EnvChoice.Current != choice && !failed && global::Godot.Time.GetTicksMsec() < until)
                await System.Threading.Tasks.Task.Delay(50);
        }
        finally { EnvChoice.Failed -= OnFailed; }
        return new { choice = Viewer.Core.World.SkyChoices.Label(_skyChoice), requested = Viewer.Core.World.SkyChoices.Label(choice), failed, why = failed ? _fixedSkies?.LastFailure : null };
    }

    /// <summary>The textures this session requested, fetched again straight from the
    /// region's texture capability with a plain bounded client of our own (not the protocol library's), at a fixed
    /// concurrency (at most the official viewer's 8); <paramref name="to"/> is each request's last byte (null: whole files).
    /// Read-only GETs. The answer carries timings and counts only, never a texture id or the capability's address.</summary>
    public async System.Threading.Tasks.Task<object?> TextureProbe(int concurrency, long? to)
    {
        if (_presented is null || _textures is null || _textureTransport is not Viewer.Core.Assets.ITextureCapabilityProbe probe) return null;
        var ids = _textures.Requested().ToList();
        var r = await probe.ProbeTexturesAsync(ids, concurrency, to, System.Threading.CancellationToken.None);
        return r is null ? null : new { textures = ids.Count, concurrency = r.Concurrency, wall_ms = r.WallMs, timing = r.Timing };
    }

    /// <summary>The quality layers declared by every texture this session requested (from
    /// the content store), by this avatar's own bakes and by the region's map image; other avatars' bakes as counts only,
    /// never their ids. A texture not in the store is fetched read-only through the session's texture capability (its first
    /// chunk, which holds the main header; the whole file when the transport cannot fetch a range).</summary>
    public async System.Threading.Tasks.Task<object?> LayerReport()
    {
        var s = _presented;
        if (s is null || _textures is null || _contentView is not { } store || _textureTransport is not { } tt) return null;
        int fetched = 0;
        async System.Threading.Tasks.Task<(int? Layers, int W, int H, long Bytes, string? Why, int Components, string Container, string Magic)> Read(Guid id)
        {
            byte[]? bytes = null;
            long total = 0;
            if (store.TryGet("texture", id, out var whole)) { bytes = whole; total = whole.Length; }
            else if (store.TryGetPartial("texture", id, out var part, out var t)) { bytes = part; total = t ?? part.Length; }
            if (bytes is null)
            {
                fetched++;
                using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(30));
                if (tt is Viewer.Core.Assets.ITextureRangeTransport rt)
                {
                    var a = await rt.GetTextureRangeAsync(id, 0, Viewer.Core.Imaging.TextureByteRange.FirstChunk - 1, Viewer.Core.Imaging.J2kLimits.Default.MaxBytes, cts.Token);
                    if (a.Body is null) return (null, 0, 0, 0, a.Why ?? a.Kind.ToString(), 0, "", "");
                    bytes = a.Body;
                    total = a.Total ?? a.Body.Length;
                }
                else
                {
                    var r = await tt.FetchTextureAsync(id, cts.Token);
                    if (r.Status != Viewer.Core.Assets.AssetFetchStatus.Ok) return (null, 0, 0, 0, r.Status.ToString(), 0, "", "");
                    bytes = r.Bytes.ToArray();
                    total = bytes.Length;
                }
            }
            // measurement reading: the guard's reading with the component count opened, and JP2 files looked into
            var (h, why, container, magic) = Viewer.Core.Imaging.TextureLayers.Measure(bytes);
            return (h?.Layers, h?.Width ?? 0, h?.Height ?? 0, total, why, h?.Components ?? 0, container, magic);
        }
        var requested = _textures.Requested().ToList();
        var objectLayers = new System.Collections.Generic.List<int?>();
        var unreadable = new System.Collections.Generic.Dictionary<string, int>();
        var containers = new System.Collections.Generic.Dictionary<string, int>();
        var over = new System.Collections.Generic.List<object>();
        foreach (var id in requested)
        {
            var r = await Read(id);
            objectLayers.Add(r.Layers);
            if (r.Why is not null) unreadable[r.Why] = unreadable.GetValueOrDefault(r.Why) + 1;
            if (r.Container is not ("j2c" or "")) containers[r.Container] = containers.GetValueOrDefault(r.Container) + 1;
            if (r.Layers > Viewer.Core.Imaging.TextureLayers.Threshold) over.Add(new { kind = "requested", id, width = r.W, height = r.H, bytes = r.Bytes, layers = r.Layers });
        }
        var self = new System.Collections.Generic.List<object>();
        var otherLayers = new System.Collections.Generic.List<int?>();
        var otherComponents = new System.Collections.Generic.List<int?>();
        int otherAvatars = 0;
        foreach (var look in s.World.Appearances())
        {
            if (!look.IsSelf) otherAvatars++;
            foreach (var (slot, id) in look.Bakes)
            {
                if (id == Guid.Empty) continue;
                var r = await Read(id);
                if (look.IsSelf)
                {
                    self.Add(new { slot, id, width = r.W, height = r.H, components = r.Components, container = r.Container, bytes = r.Bytes, layers = r.Layers, why = r.Why, magic = r.Layers is null ? r.Magic : null });
                    if (r.Layers > Viewer.Core.Imaging.TextureLayers.Threshold) over.Add(new { kind = "own bake", id, width = r.W, height = r.H, bytes = r.Bytes, layers = r.Layers });
                }
                else
                {
                    otherLayers.Add(r.Layers);
                    otherComponents.Add(r.Components);
                    if (r.Layers > Viewer.Core.Imaging.TextureLayers.Threshold) over.Add(new { kind = "other avatar's bake", width = r.W, height = r.H, bytes = r.Bytes, layers = r.Layers });
                }
            }
        }
        object? map = null;
        if (s.Region?.MapImageId is { } mapId)
        {
            var r = await Read(mapId);
            map = new { id = mapId, width = r.W, height = r.H, components = r.Components, container = r.Container, bytes = r.Bytes, layers = r.Layers, why = r.Why, magic = r.Magic };
            if (r.Layers > Viewer.Core.Imaging.TextureLayers.Threshold) over.Add(new { kind = "map image", id = mapId, width = r.W, height = r.H, bytes = r.Bytes, layers = r.Layers });
        }
        return new
        {
            requested = new { count = requested.Count, histogram = Viewer.Core.Imaging.TextureLayers.Histogram(objectLayers).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), unreadable, not_raw_j2c = containers },
            own_bakes = self,
            appearances = s.World.Appearances().Count,
            other_avatars = otherAvatars,
            other_bakes = new { count = otherLayers.Count, histogram = Viewer.Core.Imaging.TextureLayers.Histogram(otherLayers).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                components = Viewer.Core.Imaging.TextureLayers.Histogram(otherComponents).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) },
            map_image = map,
            over_threshold = over,
            fetched_for_report = fetched,
        };
    }
#endif

    /// <summary>The time limit on quitting (<see cref="QuitWatchdog"/>): started when the tree is left, so a stall in the engine's own
    /// shutdown after the viewer's cleanup ends the process with a log line instead of keeping it alive.</summary>
    private QuitWatchdog? _quitWatchdog;

    public override void _ExitTree()
    {
        _quitWatchdog = new QuitWatchdog(Log, QuitWatchdog.TerminateThisProcess, QuitWatchdog.Limit,
            () => JitWarmup.Running ? ["jit-warmup"] : []);
        _quitWatchdog.Arm();
        _quitWatchdog.Enter("settings");
        try { RecordInventoryLogout(); } catch (Exception) { /* the settings file is a convenience; shutdown goes on */ }
        try { _shell?.Shell.Floaters.SavePlaces(); } catch (Exception) { /* the windows' places are a convenience; shutdown goes on */ }
        _quitWatchdog.Enter("add-ons");
        try { _addons?.Dispose(); } catch (Exception) { /* each add-on is stopped on its own; shutdown goes on */ }
        _quitWatchdog.Enter("presentation");
        try { CrossingFramesFlush(); } catch (Exception) { /* a development trace; shutdown goes on */ }
        try { Unpresent(); }
        finally
        {
            // whatever happens above, the rest of the shutdown runs
            // The engine frees the scene while its audio thread still mixes: each generator is stopped and kept until the audio
            // thread has mixed past the stop (Main.AudioGenerators.cs).
            _quitWatchdog.Enter("audio");
            try { ReleaseAudioGenerators(); } catch (Exception e) { Log.Warn("quit.audio-failed", CoreLog.F("detail", e.Message)); }
            _quitWatchdog.Enter("variant-disposal");
            VariantDisposal.DrainAll("shutdown");   // nothing held is left to a finalizer
            // The engine corrupts its heap when it quits with resources we made still live: free any a failed teardown left.
            string live = Presentation.Rids.CheckAtQuit(out int leaked);
            if (leaked > 0) Log.Warn("quit.live-resources-freed", CoreLog.F("count", leaked), CoreLog.F("detail", live));
            if (_logger is not null)
                OS.RemoveLogger(_logger);
            LoggedErrorReleases.DrainAll();   // the backtraces of errors logged up to the logger's removal
            // The viewer's own cleanup is done; what runs next is the engine's shutdown. The watchdog stays armed through it.
            _quitWatchdog.Enter("engine-shutdown");
        }
    }
}
