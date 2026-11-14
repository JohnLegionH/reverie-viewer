// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Viewer.Addons;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using MotionState = Viewer.Addons.MotionState;
using Viewer.Core.Diagnostics;

namespace Viewer.Shell.Addons;

/// <summary>What the viewer does for add-ons: the user's own avatar, chat and inventory, as the host hands them on.</summary>
public interface IAddonServices
{
    /// <summary>Seconds since the viewer started.</summary>
    double Now { get; }

    /// <summary>Asks the region to start or stop animations on the user's own avatar, in order, as one request.</summary>
    void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests);

    /// <summary>Says a line in local chat as the user, through the action pipeline. False when it was not sent.</summary>
    bool Say(string text);

    /// <summary>The user's inventory while logged in.</summary>
    IAddonInventory? Inventory { get; }

    /// <summary>Changes to the user's inventory while logged in, with no limit of its own: the host keeps each add-on inside
    /// the folder its manifest names.</summary>
    IAddonInventoryWriter? InventoryWriter => null;

    /// <summary>Plays an animation on the user's own avatar in this viewer only (nothing goes to the region); null ends it.</summary>
    void PlayLocally(Guid? animation) { }

    /// <summary>Says a line on a chat channel as the user, through the action pipeline (restraint replies). False when not sent.</summary>
    bool SayOnChannel(int channel, string text) => false;

    /// <summary>Sits the user on an object through the action pipeline. False when refused or not sent.</summary>
    bool ForceSit(Guid objectId) => false;

    /// <summary>Stands the user up through the action pipeline. False when refused or not sent.</summary>
    bool ForceStand() => false;

    /// <summary>Teleports the user to a global position, metres, through the action pipeline. False when refused or not sent.</summary>
    bool ForceTeleport(double x, double y, double z) => false;

    /// <summary>Whether an object is in the viewer's object list now (worn, or in the region).</summary>
    bool ObjectPresent(Guid objectId) => true;

    /// <summary>The viewer's own name.</summary>
    string ViewerName => "Viewer";
}

/// <summary>One add-on the host knows: its manifest, where it came from, and its state.</summary>
public sealed class AddonEntry
{
    internal AddonEntry(AddonManifest manifest, bool builtIn, string? folder, Func<IAddon>? factory)
    {
        Manifest = manifest;
        BuiltIn = builtIn;
        Folder = folder;
        Factory = factory;
    }

    public AddonManifest Manifest { get; }
    public string Id => Manifest.Id;
    /// <summary>Shipped with the viewer (on by default) or installed by the user (off until the user turns it on).</summary>
    public bool BuiltIn { get; }
    /// <summary>The add-on's folder; null for one registered in code.</summary>
    public string? Folder { get; }
    internal Func<IAddon>? Factory { get; }

    /// <summary>Whether the user wants it on (and, for an add-on with a job, it is the one chosen for that job).</summary>
    public bool Enabled { get; internal set; }
    /// <summary>Whether it is running now.</summary>
    public bool Running => Instance is not null;
    /// <summary>The permissions the user granted (empty until granted).</summary>
    public IReadOnlyList<string> Granted { get; internal set; } = [];
    /// <summary>Why it was switched off after an error, or could not be loaded; null when nothing went wrong.</summary>
    public string? Note { get; internal set; }

    /// <summary>The add-on is switched at the next login (its manifest says so): the switch shows the choice, which applies then.</summary>
    public bool WaitsForLogin { get; internal set; }

    internal IAddon? Instance { get; set; }
    internal AddonContext? Context { get; set; }
    internal AddonLoadContext? LoadContext { get; set; }
}

/// <summary>
/// The add-on host: finds add-ons (the viewer's own add-ons folder and the user's Addons folder), loads each in its own load
/// context, starts the ones that are on, hands them events, carries out what they ask within what the user granted, asks their
/// filters before the user's sit, stand and teleport actions (it is a veto point of the action pipeline), and switches off an
/// add-on that throws, with a note. Runs on the viewer's main thread.
/// </summary>
public sealed class AddonHost : IActionVeto, IDisposable
{
    public const string StateFileName = "addons-state.json";

    private readonly IAddonServices _services;
    private readonly CoreLog _log;
    private readonly string? _stateFile;
    private readonly List<AddonEntry> _entries = [];
    private readonly Dictionary<string, bool> _savedEnabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _savedGranted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _memorySettings = new(StringComparer.Ordinal);
    private readonly List<(Guid Animation, bool Start)> _pending = [];
    private bool _loggedIn;
    private MotionState _motion;
    private bool _mouselook;
    private IReadOnlyList<PlayingAnimation> _playing = [];

    public AddonHost(IAddonServices services, CoreLog log, string? stateFile = null)
    {
        _services = services;
        _log = log.For("addons");
        _stateFile = stateFile;
        ReadState();
    }

    public IReadOnlyList<AddonEntry> Addons => _entries;

    /// <summary>The viewer's restraint enforcement, handed to a built-in add-on granted "restraints" (set by the viewer).</summary>
    public RlvGate? Restraints { get; set; }

    /// <summary>The folder that holds each add-on's own settings folder (the user's add-on settings); null keeps settings in memory.</summary>
    public string? SettingsRoot { get; set; }

    /// <summary>The load context an add-on's library runs in; null when it is not running or was registered in code.</summary>
    public AssemblyLoadContext? LoadContextOf(string id) => Find(id)?.LoadContext;

    /// <summary>Something changed that the Add-ons window shows.</summary>
    public event Action? Changed;

    /// <summary>A window or a menu item was added or removed.</summary>
    public event Action? ScreenChanged;

    /// <summary>A filter refused an action: the add-on's name and its reason.</summary>
    public event Action<string, string>? Refused;

    /// <summary>The add-ons' windows, with the add-on each belongs to.</summary>
    public IReadOnlyList<(string AddonId, AddonWindow Window)> Windows =>
        _entries.Where(e => e.Context is not null).SelectMany(e => e.Context!.Windows.Select(w => (e.Id, w))).ToArray();

    /// <summary>The controls the add-ons put in the quick bar, with the add-on each belongs to.</summary>
    public IReadOnlyList<(string AddonId, AddonControl Control)> QuickControls =>
        _entries.Where(e => e.Context is not null).SelectMany(e => e.Context!.QuickControls.Select(c => (e.Id, c))).ToArray();

    /// <summary>The add-ons' dock items (interface 0.5), in the order the add-ons were turned on, with the add-on each belongs to.</summary>
    public IReadOnlyList<(string AddonId, AddonDockItem Item)> DockItems =>
        _entries.Where(e => e.Context is not null).OrderBy(e => e.Context!.StartOrder)
            .SelectMany(e => e.Context!.DockItems.Where(d => !d.Removed).Select(d => (e.Id, d))).ToArray();

    private long _starts;

    /// <summary>The next start's place in the order add-ons were turned on.</summary>
    internal long NextStartOrder() => ++_starts;

    /// <summary>The add-ons' menu items.</summary>
    public IReadOnlyList<(string AddonId, string Label, Action Chosen)> MenuItems =>
        _entries.Where(e => e.Context is not null).SelectMany(e => e.Context!.MenuItems.Select(m => (e.Id, m.Label, m.Chosen))).ToArray();

    /// <summary>The jobs the known add-ons fill.</summary>
    public IReadOnlyList<string> Jobs => _entries.Select(e => e.Manifest.Job).OfType<string>().Distinct().ToArray();

    /// <summary>The add-on that has a job now, if any.</summary>
    public AddonEntry? ActiveFor(string job) => _entries.FirstOrDefault(e => e.Manifest.Job == job && e.Enabled);

    private AddonEntry? Find(string id) => _entries.FirstOrDefault(e => e.Id == id);

    // ---------------- finding add-ons ----------------

    /// <summary>Reads every add-on folder under <paramref name="folder"/> (each holding add-on.json). Problems are logged and
    /// the folder is skipped. Answers how many were added.</summary>
    public int Discover(string folder, bool builtIn)
    {
        if (!Directory.Exists(folder))
            return 0;
        int added = 0;
        foreach (string dir in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
        {
            string file = Path.Combine(dir, AddonManifest.FileName);
            if (!File.Exists(file))
                continue;
            string name = Path.GetFileName(dir);
            AddonManifest? manifest;
            IReadOnlyList<string> problems;
            try { manifest = AddonManifest.Parse(File.ReadAllText(file), out problems); }
            catch (IOException ex) { manifest = null; problems = [ex.GetType().Name]; }
            if (manifest is null)
            {
                _log.Warn("addon.manifest_bad", CoreLog.F("folder", name), CoreLog.F("problems", problems.Count), CoreLog.F("first", problems.FirstOrDefault() ?? ""));
                continue;
            }
            if (!File.Exists(Path.Combine(dir, manifest.Library)))
            {
                _log.Warn("addon.library_missing", CoreLog.F("folder", name));
                continue;
            }
            if (Find(manifest.Id) is not null)
            {
                _log.Warn("addon.duplicate", CoreLog.F("folder", name));
                continue;
            }
            _entries.Add(new AddonEntry(manifest, builtIn, dir, null));
            _log.Info("addon.found", CoreLog.F("id", manifest.Id), CoreLog.F("built_in", builtIn));
            added++;
        }
        Changed?.Invoke();
        return added;
    }

    /// <summary>Adds an add-on made in code (the viewer's tests; nothing in a shipping viewer).</summary>
    public AddonEntry Register(AddonManifest manifest, Func<IAddon> factory, bool builtIn)
    {
        if (Find(manifest.Id) is not null)
            throw new ArgumentException($"an add-on '{manifest.Id}' is already known", nameof(manifest));
        var entry = new AddonEntry(manifest, builtIn, null, factory);
        _entries.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    // ---------------- on and off ----------------

    /// <summary>Starts every add-on that is on: built-in ones unless the user turned them off, outside ones only when the user turned them on.</summary>
    public void StartEnabled()
    {
        var jobsTaken = new HashSet<string>(StringComparer.Ordinal);
        // an add-on the user switched on explicitly takes its job first, then the others in the order found
        foreach (var entry in _entries.OrderByDescending(e => _savedEnabled.TryGetValue(e.Id, out bool saved) && saved).ToArray())
        {
            if (entry.Running || entry.WaitsForLogin)
            {
                if (entry.Enabled && entry.Manifest.Job is { } held) jobsTaken.Add(held);
                continue;
            }
            bool on = _savedEnabled.TryGetValue(entry.Id, out bool wanted) ? wanted : entry.BuiltIn && entry.Manifest.DefaultOn != false;
            entry.Granted = entry.BuiltIn ? entry.Manifest.Permissions
                : _savedGranted.TryGetValue(entry.Id, out var granted) ? entry.Manifest.Permissions.Where(granted.Contains).ToArray() : [];
            if (!entry.BuiltIn && entry.Granted.Count < entry.Manifest.Permissions.Count)
                on = false;   // asks for more than the user granted (a newer version): off until the user agrees again
            if (on && entry.Manifest.Job is { } job && !jobsTaken.Add(job))
                on = false;
            entry.Enabled = on;
        }
        foreach (var entry in _entries.Where(e => e.Enabled && !e.Running && !e.WaitsForLogin).ToArray())
            Start(entry);
        Changed?.Invoke();
    }

    /// <summary>Turns an add-on on or off. An outside add-on is turned on only with <paramref name="grantPermissions"/> (the user saw and accepted
    /// its permissions). Turning on an add-on with a job turns off the one that had the job. False when nothing changed.</summary>
    public bool SetEnabled(string id, bool on, bool grantPermissions = false)
    {
        if (Find(id) is not { } entry)
            return false;
        if (entry.Manifest.SwitchAtNextLogin)
            return SetEnabledAtNextLogin(entry, on, grantPermissions);
        if (!on)
        {
            if (!entry.Enabled && !entry.Running)
                return false;
            entry.Enabled = false;
            Stop(entry);
            _log.Info("addon.off", CoreLog.F("id", id));
            SaveState();
            Changed?.Invoke();
            return true;
        }
        if (entry.Enabled && entry.Running)
            return false;
        bool covered = entry.Manifest.Permissions.All(entry.Granted.Contains);
        if (!entry.BuiltIn && !covered && !grantPermissions)
            return false;
        entry.Granted = entry.Manifest.Permissions;
        if (entry.Manifest.Job is { } job)
            foreach (var other in _entries.Where(e => e != entry && e.Manifest.Job == job && (e.Enabled || e.Running)).ToArray())
            {
                other.Enabled = false;
                Stop(other);
            }
        entry.Enabled = true;
        entry.Note = null;
        _log.Info("addon.on", CoreLog.F("id", id));
        Start(entry);
        SaveState();
        Changed?.Invoke();
        return true;
    }

    /// <summary>An add-on whose manifest says "switch_at": "next-login" (RLV, as Firestorm's RestrainedLove setting): the switch
    /// keeps the choice and says it takes effect at the next login; <see cref="LoggedIn"/> applies it.</summary>
    private bool SetEnabledAtNextLogin(AddonEntry entry, bool on, bool grantPermissions)
    {
        if (entry.Enabled == on)
            return false;
        if (on)
        {
            bool covered = entry.Manifest.Permissions.All(entry.Granted.Contains);
            if (!entry.BuiltIn && !covered && !grantPermissions)
                return false;
            entry.Granted = entry.Manifest.Permissions;
            if (entry.Manifest.Job is { } job)
                foreach (var other in _entries.Where(e => e != entry && e.Manifest.Job == job && e.Enabled).ToArray())
                    other.Enabled = false;
        }
        entry.Enabled = on;
        entry.WaitsForLogin = entry.Enabled != entry.Running;
        entry.Note = entry.WaitsForLogin ? NextLoginNote : null;
        _log.Info(on ? "addon.on_next_login" : "addon.off_next_login", CoreLog.F("id", entry.Id));
        SaveState();
        Changed?.Invoke();
        return true;
    }

    /// <summary>The note an add-on that is switched at the next login shows until then.</summary>
    public const string NextLoginNote = "Takes effect at next login.";

    /// <summary>Applies the choices of add-ons switched at the next login: those turned on start, those turned off stop.</summary>
    private void ApplyNextLogin()
    {
        foreach (var entry in _entries.Where(e => e.Manifest.SwitchAtNextLogin).ToArray())
        {
            entry.WaitsForLogin = false;
            if (entry.Note == NextLoginNote)
                entry.Note = null;
            if (entry.Enabled && !entry.Running)
                Start(entry);
            else if (!entry.Enabled && entry.Running)
                Stop(entry);
        }
    }

    private void Start(AddonEntry entry)
    {
        IAddon instance;
        try
        {
            instance = entry.Factory is { } factory ? factory() : Load(entry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.LoadContext?.Unload();
            entry.LoadContext = null;
            entry.Enabled = false;
            entry.Note = $"Could not be loaded: {ex.GetType().Name}: {ex.Message}";
            _log.Warn("addon.load_failed", CoreLog.F("id", entry.Id), CoreLog.F("error", ex.GetType().Name));
            SaveState();
            return;
        }
        var context = new AddonContext(this, entry);
        entry.Instance = instance;
        entry.Context = context;
        _log.Info("addon.start", CoreLog.F("id", entry.Id));
        if (!Guard(entry, "start", () => instance.Start(context)))
            return;
        if (_loggedIn)
            Guard(entry, "logged in", context.RaiseLoggedIn);
        Flush();
        ScreenChanged?.Invoke();
    }

    private IAddon Load(AddonEntry entry)
    {
        string library = Path.GetFullPath(Path.Combine(entry.Folder!, entry.Manifest.Library));
        var context = new AddonLoadContext(entry.Id, library);
        entry.LoadContext = context;
        var assembly = context.LoadFromFile(library);
        var types = assembly.GetExportedTypes().Where(t => typeof(IAddon).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }).ToArray();
        var type = entry.Manifest.Entry is { } named ? types.FirstOrDefault(t => t.FullName == named) : types.Length == 1 ? types[0] : null;
        if (type is null)
            throw new InvalidOperationException(types.Length == 0 ? "the library has no public IAddon class" : "the library has several IAddon classes and the manifest names none ('entry')");
        return (IAddon)Activator.CreateInstance(type)!;
    }

    /// <summary>Stops a running add-on: its own Stop, then the host removes what it added and stops the animations it left playing.</summary>
    private void Stop(AddonEntry entry)
    {
        if (entry.Instance is not { } instance || entry.Context is not { } context)
            return;
        entry.Instance = null;   // first: an add-on failing inside its own Stop is not stopped twice
        try { instance.Stop(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("addon.stop_failed", CoreLog.F("id", entry.Id), CoreLog.F("error", ex.GetType().Name));
        }
        foreach (Guid animation in context.Started.ToArray())
            _pending.Add((animation, false));
        if (context.Previewing)
            Services.PlayLocally(null);   // a preview it left playing ends with it
        context.Close();
        if (entry.Granted.Contains(AddonPermissions.Restraints))
            Restraints?.Clear();   // whatever the add-on handed over goes with it: an add-on that fails lets every action go ahead
        Flush();
        entry.Context = null;
        entry.LoadContext?.Unload();   // collectible: the code is freed once nothing holds it
        entry.LoadContext = null;
        _log.Info("addon.stop", CoreLog.F("id", entry.Id));
        ScreenChanged?.Invoke();
    }

    /// <summary>Runs a call into an add-on; an exception switches the add-on off with a note. False when it failed.</summary>
    private bool Guard(AddonEntry entry, string where, Action call)
    {
        if (entry.Instance is null)
            return false;
        try
        {
            call();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(entry, where, ex);
            return false;
        }
    }

    private void Fail(AddonEntry entry, string where, Exception ex)
    {
        _log.Warn("addon.failed", CoreLog.F("id", entry.Id), CoreLog.F("where", where), CoreLog.F("error", ex.GetType().Name));
        entry.Note = ex is AddonPermissionException denied
            ? $"Switched off: it used '{denied.Permission}' without that permission."
            : $"Switched off after an error ({where}): {ex.GetType().Name}: {ex.Message}";
        entry.Enabled = false;
        Stop(entry);
        SaveState();
        Changed?.Invoke();
    }

    /// <summary>Runs a call into an add-on from the screen (a button, a menu item): an exception switches the add-on off.</summary>
    public void Run(string addonId, Action call)
    {
        if (Find(addonId) is { Running: true } entry)
        {
            Guard(entry, "window", call);
            Flush();
        }
    }

    /// <summary>The add-on's diagnostics (development builds).</summary>
    public object? Diagnose(string addonId, string action, string? text)
    {
        object? answer = null;
        if (Find(addonId) is { Instance: IAddonDiagnostics diagnostics } entry)
            Guard(entry, "diagnostics", () => answer = diagnostics.Call(action, text));
        Flush();
        return answer;
    }

    // ---------------- what the viewer tells add-ons ----------------

    private void Dispatch(string where, Action<AddonContext> raise)
    {
        foreach (var entry in _entries.Where(e => e.Context is not null).ToArray())
            if (entry.Context is { } context)
                Guard(entry, where, () => raise(context));
        Flush();
    }

    internal bool IsLoggedIn => _loggedIn;
    internal MotionState CurrentMotion => _motion;
    internal bool CurrentMouselook => _mouselook;
    internal IReadOnlyList<PlayingAnimation> CurrentPlaying => _playing;
    internal IAddonServices Services => _services;
    internal void RaiseScreenChanged() => ScreenChanged?.Invoke();

    public void LoggedIn()
    {
        if (_loggedIn)
            return;
        _loggedIn = true;
        var starting = _entries.Where(e => e.Manifest.SwitchAtNextLogin && e.Enabled && !e.Running).ToArray();
        ApplyNextLogin();   // an add-on started here hears that it is logged in as it starts
        Dispatch("logged in", c => { if (!starting.Any(s => s.Context == c)) c.RaiseLoggedIn(); });
    }

    public void LoggedOut()
    {
        if (!_loggedIn)
            return;
        _loggedIn = false;
        _motion = MotionState.Unknown;
        _playing = [];
        Dispatch("logged out", c => c.RaiseLoggedOut());
    }

    public void RegionChanged(string region) => Dispatch("region changed", c => c.RaiseRegionChanged(region));

    public void Motion(MotionState state)
    {
        if (state == _motion)
            return;
        _motion = state;
        Dispatch("motion", c => c.RaiseMotion(state));
    }

    public void Mouselook(bool on)
    {
        if (on == _mouselook)
            return;
        _mouselook = on;
        Dispatch("mouselook", c => c.RaiseMouselook(on));
    }

    public void Playing(IReadOnlyList<PlayingAnimation> playing)
    {
        _playing = [.. playing];
        Dispatch("animations", c => c.RaisePlaying(_playing));
    }

    public void Chat(ChatLine line) => Dispatch("chat", c => c.RaiseChat(line));

    /// <summary>Whether an owner-say line is kept out of the chat history: an RLV command line (starting with '@') while the add-on
    /// filling the "rlv" job runs, as RLVa hides them. While it is off, such lines show as ordinary owner-say.</summary>
    public bool Swallows(ChatLine line) =>
        line.Kind == ChatKind.OwnerSay && line.Text is { Length: > 3 } text && text[0] == '@' && ActiveFor("rlv") is { Running: true };

    /// <summary>Once a frame: the add-ons' Tick, then their animation requests go out.</summary>
    public void Frame() => Dispatch("tick", c => c.RaiseTick());

    internal void Queue(Guid animation, bool start) => _pending.Add((animation, start));

    private void Flush()
    {
        if (_pending.Count == 0)
            return;
        var requests = _pending.ToArray();
        _pending.Clear();
        _services.SendAnimations(requests);
    }

    // ---------------- refusals ----------------

    public VetoDecision Check(ActionIntent intent, ActionPhase phase)
    {
        if (phase == ActionPhase.Continue || KindOf(intent.Kind) is not { } kind)
            return VetoDecision.Allow;
        var action = new AddonAction(kind);
        foreach (var entry in _entries.Where(e => e.Context is not null).ToArray())
        {
            foreach (var filter in entry.Context?.FiltersFor(kind) ?? [])
            {
                AddonVerdict verdict = AddonVerdict.Allow;
                if (!Guard(entry, "filter", () => verdict = filter(action)))
                    break;   // the add-on is off now; its other filters went with it
                if (!verdict.Allowed)
                {
                    _log.Info("addon.refused", CoreLog.F("id", entry.Id), CoreLog.F("kind", kind));
                    Refused?.Invoke(entry.Manifest.Name, verdict.Reason ?? "");
                    return VetoDecision.Refuse("addon.refused");
                }
            }
        }
        return VetoDecision.Allow;
    }

    private static AddonActionKind? KindOf(ActionKind kind) => kind switch
    {
        ActionKind.Sit or ActionKind.SitGround => AddonActionKind.Sit,
        ActionKind.Stand => AddonActionKind.Stand,
        ActionKind.TeleportRegion or ActionKind.TeleportLandmark or ActionKind.TeleportHome or ActionKind.TeleportHandle or ActionKind.TeleportLureAnswer => AddonActionKind.Teleport,
        _ => null,
    };

    // ---------------- settings and state ----------------

    internal string SettingsFolderOf(string id)
    {
        if (SettingsRoot is null)
            return "";
        string folder = Path.Combine(SettingsRoot, id);
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal Dictionary<string, string> SettingsOf(string id)
    {
        if (SettingsRoot is null)
        {
            if (!_memorySettings.TryGetValue(id, out var memory))
                _memorySettings[id] = memory = new(StringComparer.Ordinal);
            return memory;
        }
        string file = Path.Combine(SettingsFolderOf(id), "settings.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.Warn("addon.settings_unreadable", CoreLog.F("id", id), CoreLog.F("error", ex.GetType().Name));
            return [];
        }
    }

    internal void SaveSettings(string id, Dictionary<string, string> settings)
    {
        if (SettingsRoot is null)
        {
            _memorySettings[id] = settings;
            return;
        }
        try
        {
            File.WriteAllText(Path.Combine(SettingsFolderOf(id), "settings.json"), JsonSerializer.Serialize(settings, Indented));
        }
        catch (IOException ex)
        {
            _log.Warn("addon.settings_unwritable", CoreLog.F("id", id), CoreLog.F("error", ex.GetType().Name));
        }
    }

    /// <summary>Gives an add-on settings it has never had (the viewer's own settings carried over when a feature became an add-on).
    /// Does nothing once the add-on has settings of its own; true when the settings were written.</summary>
    public bool SeedSettings(string id, IReadOnlyDictionary<string, string> settings)
    {
        if (SettingsOf(id).Count > 0)
            return false;
        SaveSettings(id, new Dictionary<string, string>(settings, StringComparer.Ordinal));
        return true;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private sealed record StateFile(Dictionary<string, bool>? Enabled, Dictionary<string, string[]>? Granted);

    private void ReadState()
    {
        if (_stateFile is null || !File.Exists(_stateFile))
            return;
        try
        {
            var state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_stateFile));
            foreach (var (id, on) in state?.Enabled ?? []) _savedEnabled[id] = on;
            foreach (var (id, granted) in state?.Granted ?? []) _savedGranted[id] = granted;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.Warn("addon.state_unreadable", CoreLog.F("error", ex.GetType().Name));
        }
    }

    private void SaveState()
    {
        foreach (var entry in _entries)
        {
            _savedEnabled[entry.Id] = entry.Enabled;
            _savedGranted[entry.Id] = [.. entry.Granted];
        }
        if (_stateFile is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(new StateFile(_savedEnabled, _savedGranted), Indented));
        }
        catch (IOException ex)
        {
            _log.Warn("addon.state_unwritable", CoreLog.F("error", ex.GetType().Name));
        }
    }

    /// <summary>Stops every running add-on (the viewer is closing); what the user switched on stays on.</summary>
    public void Dispose()
    {
        foreach (var entry in _entries.Where(e => e.Running).ToArray())
            Stop(entry);
    }
}

/// <summary>The user's own avatar's motion as the region's animation list shows it.</summary>
public static class AddonMotion
{
    // the most telling animation first: a stand plays beside a walk, a hover beside a landing
    private static readonly (string Name, MotionState State)[] Order =
    [
        ("sit_ground", MotionState.SittingOnGround), ("standup", MotionState.StandingUp), ("falldown", MotionState.Falling),
        ("pre_jump", MotionState.Jumping), ("jump", MotionState.Jumping), ("fly", MotionState.Flying), ("fly_slow", MotionState.Flying),
        ("hover_up", MotionState.Hovering), ("hover_down", MotionState.Hovering), ("hover", MotionState.Hovering),
        ("land", MotionState.Landing), ("medium_land", MotionState.Landing), ("crouch_walk", MotionState.CrouchWalking),
        ("crouch", MotionState.Crouching), ("run", MotionState.Running), ("walk", MotionState.Walking), ("female_walk", MotionState.Walking),
        ("turn_left", MotionState.Turning), ("turn_right", MotionState.Turning), ("stand", MotionState.Standing),
    ];

    public static MotionState From(IReadOnlyList<Guid> playing, bool seated)
    {
        if (seated)
            return MotionState.SittingOnObject;
        var names = playing.Select(p => AgentAnimations.Names.TryGetValue(p, out var n) ? n : null).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, state) in Order)
            if (names.Contains(name))
                return state;
        return MotionState.Unknown;
    }
}

/// <summary>What one running add-on is given: every member checks the grant first.</summary>
internal sealed class AddonContext(AddonHost host, AddonEntry entry) : IAddonContext
{
    private readonly List<(AddonActionKind Kind, Func<AddonAction, AddonVerdict> Filter)> _filters = [];
    private readonly List<AddonWindow> _windows = [];
    private readonly List<(string Label, Action Chosen)> _menuItems = [];
    private readonly List<AddonControl> _quick = [];
    private bool _closed;
    private Action? _tick, _loggedIn, _loggedOut;
    private Action<string>? _region;
    private Action<MotionState>? _motion;
    private Action<bool>? _mouselook;
    private Action<IReadOnlyList<PlayingAnimation>>? _playing;
    private Action<ChatLine>? _chat;

    internal HashSet<Guid> Started { get; } = [];
    internal IReadOnlyList<AddonWindow> Windows => _windows;
    internal IReadOnlyList<(string Label, Action Chosen)> MenuItems => _menuItems;
    internal IReadOnlyList<AddonControl> QuickControls => _quick;
    internal IReadOnlyList<AddonDockItem> DockItems => _dock;
    internal long StartOrder { get; } = host.NextStartOrder();
    private readonly List<AddonDockItem> _dock = [];
    internal bool Previewing { get; private set; }

    internal IEnumerable<Func<AddonAction, AddonVerdict>> FiltersFor(AddonActionKind kind) =>
        _filters.Where(f => f.Kind == kind).Select(f => f.Filter).ToArray();

    internal void Close()
    {
        _closed = true;
        _filters.Clear();
        _windows.Clear();
        _menuItems.Clear();
        _quick.Clear();
        Previewing = false;
        _tick = _loggedIn = _loggedOut = null;
        _region = null;
        _motion = null;
        _mouselook = null;
        _playing = null;
        _chat = null;
    }

    /// <summary>False once the add-on is stopped (a late call does nothing); throws when the permission was not granted.</summary>
    private bool Need(string permission)
    {
        if (_closed)
            return false;
        if (!entry.Granted.Contains(permission))
            throw new AddonPermissionException(permission);
        return true;
    }

    public string Id => entry.Id;
    public IReadOnlyCollection<string> Granted => entry.Granted;
    public string SettingsFolder => host.SettingsFolderOf(entry.Id);

    public string? GetSetting(string key) => host.SettingsOf(entry.Id).TryGetValue(key, out var v) ? v : null;

    public void SetSetting(string key, string? value)
    {
        if (_closed)
            return;
        var settings = host.SettingsOf(entry.Id);
        if (value is null) settings.Remove(key);
        else settings[key] = value;
        host.SaveSettings(entry.Id, settings);
    }

    public double Now => host.Services.Now;

    public event Action? Tick { add { if (!_closed) _tick += value; } remove => _tick -= value; }

    public bool LoggedInNow => Need(AddonPermissions.Session) && host.IsLoggedIn;
    public event Action? LoggedIn { add { if (Need(AddonPermissions.Session)) _loggedIn += value; } remove => _loggedIn -= value; }
    public event Action? LoggedOut { add { if (Need(AddonPermissions.Session)) _loggedOut += value; } remove => _loggedOut -= value; }
    public event Action<string>? RegionChanged { add { if (Need(AddonPermissions.Session)) _region += value; } remove => _region -= value; }

    public MotionState Motion => Need(AddonPermissions.Motion) ? host.CurrentMotion : MotionState.Unknown;
    public event Action<MotionState>? MotionChanged { add { if (Need(AddonPermissions.Motion)) _motion += value; } remove => _motion -= value; }
    public bool Mouselook => Need(AddonPermissions.Motion) && host.CurrentMouselook;
    public event Action<bool>? MouselookChanged { add { if (Need(AddonPermissions.Motion)) _mouselook += value; } remove => _mouselook -= value; }

    public IReadOnlyList<PlayingAnimation> Playing => Need(AddonPermissions.AnimationsRead) ? host.CurrentPlaying : [];
    public event Action<IReadOnlyList<PlayingAnimation>>? PlayingChanged { add { if (Need(AddonPermissions.AnimationsRead)) _playing += value; } remove => _playing -= value; }

    public event Action<ChatLine>? ChatReceived { add { if (Need(AddonPermissions.ChatRead)) _chat += value; } remove => _chat -= value; }

    public void PlayAnimation(Guid animation)
    {
        if (!Need(AddonPermissions.AnimationsPlay))
            return;
        Started.Add(animation);
        host.Queue(animation, true);
    }

    public void StopAnimation(Guid animation)
    {
        if (!Need(AddonPermissions.AnimationsPlay))
            return;
        Started.Remove(animation);
        host.Queue(animation, false);
    }

    public bool Say(string text) => Need(AddonPermissions.ChatSend) && host.Services.Say(text);

    public IDisposable AddFilter(AddonActionKind kind, Func<AddonAction, AddonVerdict> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!Need(AddonPermissions.Filter))
            return new Removal(() => { });
        var item = (kind, filter);
        _filters.Add(item);
        return new Removal(() => _filters.Remove(item));
    }

    public AddonWindow AddWindow(string id, string title)
    {
        if (!Need(AddonPermissions.Window))
            return AddonWindow.Create(id, title);
        if (_windows.Any(w => w.Id == id))
            throw new ArgumentException($"a window '{id}' was already added", nameof(id));
        var window = AddonWindow.Create(id, title);
        _windows.Add(window);
        host.RaiseScreenChanged();
        return window;
    }

    public AddonDockItem? AddDockItem(string id, string label, AddonDockKind kind, string? icon = null, string? tooltip = null,
        IReadOnlyList<AddonDockMenuEntry>? menu = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!Need(AddonPermissions.Window))
            return null;
        // at most two items per add-on, each id once (the spec's dock rules); the dock cuts the name to fit
        _dock.RemoveAll(d => d.Removed);
        if (_dock.Count >= 2 || _dock.Any(d => d.Id == id))
            return null;
        var item = AddonDockItem.Create(id, label ?? "", kind, icon, tooltip, menu);
        item.Changed += host.RaiseScreenChanged;
        _dock.Add(item);
        host.RaiseScreenChanged();
        return item;
    }

    public void AddMenuItem(string label, Action chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        if (!Need(AddonPermissions.Window))
            return;
        _menuItems.Add((label, chosen));
        host.RaiseScreenChanged();
    }

    public IAddonInventory? Inventory => Need(AddonPermissions.InventoryRead) ? host.Services.Inventory : null;

    public IAddonRestraints? Restraints
    {
        get
        {
            if (!Need(AddonPermissions.Restraints))
                return null;
            if (!entry.BuiltIn)
                throw new AddonPermissionException(AddonPermissions.Restraints);   // not yet public: built-in add-ons only
            return host.Restraints is { } gate ? new AddonRestraints(host, gate, () => _closed) : null;
        }
    }

    public IAddonInventoryWriter? InventoryWriter =>
        Need(AddonPermissions.InventoryWrite) && AddonManifest.FolderPath(entry.Manifest.InventoryFolder) is { } path
            && host.Services.Inventory is { } inventory && host.Services.InventoryWriter is { } writer
            ? new AddonInventoryScope(inventory, writer, path) : null;

    public void PlayLocally(Guid animation)
    {
        if (!Need(AddonPermissions.AnimationsPlay))
            return;
        Previewing = true;
        host.Services.PlayLocally(animation);
    }

    public void StopLocally()
    {
        if (!Need(AddonPermissions.AnimationsPlay))
            return;
        Previewing = false;
        host.Services.PlayLocally(null);
    }

    public void AddQuickControl(AddonControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!Need(AddonPermissions.Window))
            return;
        // not yet public: only the add-ons that come with the viewer place controls in its quick bar
        if (!entry.BuiltIn)
            throw new AddonPermissionException("quick bar");
        _quick.Add(control);
        host.RaiseScreenChanged();
    }

    internal void RaiseTick() => _tick?.Invoke();
    internal void RaiseLoggedIn() => _loggedIn?.Invoke();
    internal void RaiseLoggedOut() => _loggedOut?.Invoke();
    internal void RaiseRegionChanged(string region) => _region?.Invoke(region);
    internal void RaiseMotion(MotionState state) => _motion?.Invoke(state);
    internal void RaiseMouselook(bool on) => _mouselook?.Invoke(on);
    internal void RaisePlaying(IReadOnlyList<PlayingAnimation> playing) => _playing?.Invoke(playing);
    internal void RaiseChat(ChatLine line) => _chat?.Invoke(line);

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose()
        {
            _remove?.Invoke();
            _remove = null;
        }
    }
}

/// <summary>
/// The load context of one add-on: its library and the libraries beside it load here, apart from the viewer's own and from
/// every other add-on's. The interface assembly is the viewer's own copy, so the add-on's types and the viewer's agree.
/// Collectible: once the add-on is stopped and nothing holds its code, the runtime frees it, so no restart is needed.
/// </summary>
internal sealed class AddonLoadContext : AssemblyLoadContext
{
    private static readonly Assembly Api = typeof(IAddon).Assembly;
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _folder;

    public AddonLoadContext(string id, string libraryPath) : base("addon:" + id, isCollectible: true)
    {
        _folder = Path.GetDirectoryName(libraryPath)!;
        _resolver = new AssemblyDependencyResolver(libraryPath);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == Api.GetName().Name)
            return Api;
        string? path = _resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name is { } simple && File.Exists(Path.Combine(_folder, simple + ".dll")))
            path = Path.Combine(_folder, simple + ".dll");
        return path is null ? null : LoadFromFile(path);
    }

    /// <summary>Loads a library from a copy in memory, so its file is not held open: an add-on's folder can be replaced or moved
    /// while the viewer runs, and the new version is loaded the next time the add-on is switched on.</summary>
    public Assembly LoadFromFile(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return LoadFromStream(stream);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
}

/// <summary>The restraints handle a built-in add-on is given: what it publishes goes to the gate, replies and forced actions go
/// through the action pipeline under the gate's forcing scope, so only the issuing object's own restrictions are passed over.</summary>
internal sealed class AddonRestraints(AddonHost host, RlvGate gate, Func<bool> closed) : IAddonRestraints
{
    public string ViewerName => host.Services.ViewerName;

    public void Publish(IReadOnlyList<AddonRestriction> restrictions)
    {
        if (!closed())
            gate.Set(restrictions);
    }

    public bool Reply(int channel, string text)
    {
        if (closed())
            return false;
        using (gate.Forcing(Guid.Empty, reply: true))
            return host.Services.SayOnChannel(channel, text);
    }

    public bool ForceSit(Guid issuer, Guid objectId)
    {
        if (closed())
            return false;
        using (gate.Forcing(issuer))
            return host.Services.ForceSit(objectId);
    }

    public bool ForceStand(Guid issuer)
    {
        if (closed())
            return false;
        using (gate.Forcing(issuer))
            return host.Services.ForceStand();
    }

    public bool ForceTeleport(Guid issuer, double x, double y, double z)
    {
        if (closed())
            return false;
        using (gate.Forcing(issuer))
            return host.Services.ForceTeleport(x, y, z);
    }

    public bool ObjectPresent(Guid objectId) => host.Services.ObjectPresent(objectId);
}
