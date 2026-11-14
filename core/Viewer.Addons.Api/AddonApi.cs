// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Addons;

/// <summary>The add-on interface this assembly describes. A manifest names the version it needs in "interface".</summary>
public static class AddonInterface
{
    public const string Version = "0.5";
}

/// <summary>
/// An add-on: one public class with a public constructor that takes no arguments. The viewer makes one when the add-on is
/// switched on, calls <see cref="Start"/> once, and <see cref="Stop"/> once when it is switched off or the viewer closes.
/// Everything the viewer calls runs on the viewer's main thread, one call at a time.
/// </summary>
public interface IAddon
{
    void Start(IAddonContext context);

    /// <summary>Undo what the add-on is doing (stop its animations, for one). The viewer removes the add-on's windows, menu
    /// items, filters and event handlers itself after this, and stops any animation it started and did not stop.</summary>
    void Stop();
}

/// <summary>Optional: answers development tools with the add-on's state. Only development builds of the viewer call it.</summary>
public interface IAddonDiagnostics
{
    object? Call(string action, string? text);
}

/// <summary>What an add-on may ask for. The user grants them, all or none, when switching an add-on on.</summary>
public static class AddonPermissions
{
    /// <summary>Logged in, logged out, region changed.</summary>
    public const string Session = "session";
    /// <summary>The user's own avatar's motion state (standing, walking, flying, sitting and so on).</summary>
    public const string Motion = "motion";
    /// <summary>The list of animations the region says the user's own avatar plays.</summary>
    public const string AnimationsRead = "animations.read";
    /// <summary>Play and stop animations on the user's own avatar.</summary>
    public const string AnimationsPlay = "animations.play";
    /// <summary>Local chat and the user's own objects' owner-say, as received.</summary>
    public const string ChatRead = "chat.read";
    /// <summary>Say something in local chat as the user.</summary>
    public const string ChatSend = "chat.send";
    /// <summary>Allow or refuse the user's actions of the kinds in <see cref="AddonActionKind"/>.</summary>
    public const string Filter = "filter";
    /// <summary>Add windows and menu items.</summary>
    public const string Window = "window";
    /// <summary>Read the user's inventory: folder and item names, descriptions, links, and the asset id of animations. Never asset contents.</summary>
    public const string InventoryRead = "inventory.read";
    /// <summary>Change the user's inventory inside the one folder the manifest names in "inventory_folder" (and make that folder):
    /// make folders and links, rename folders, and move things to the Trash. Never purges, never anything outside it.</summary>
    public const string InventoryWrite = "inventory.write";
    /// <summary>(Interface 0.4, not yet public.) Hand the viewer restrictions to enforce, reply on chat channels and force a sit,
    /// a stand or a teleport: the restraint engine's side of RLV. Granted only to add-ons that come with the viewer.</summary>
    public const string Restraints = "restraints";

    public static IReadOnlyList<string> All { get; } = [Session, Motion, AnimationsRead, AnimationsPlay, ChatRead, ChatSend, Filter, Window, InventoryRead, InventoryWrite, Restraints];
}

/// <summary>Thrown to an add-on that uses something it was not granted. The viewer catches it and switches the add-on off.</summary>
public sealed class AddonPermissionException(string permission) : Exception($"the add-on was not granted '{permission}'")
{
    public string Permission { get; } = permission;
}

/// <summary>The user's own avatar's motion, as the region's animations show it.</summary>
public enum MotionState
{
    Unknown, Standing, Walking, Running, Turning, Crouching, CrouchWalking, Jumping, Flying, Hovering, Falling, Landing,
    StandingUp, SittingOnObject, SittingOnGround,
}

/// <summary>One animation the region lists for the avatar; the sequence number changes when it is started again.</summary>
public readonly record struct PlayingAnimation(Guid Animation, int Sequence);

public enum ChatKind { Local, OwnerSay }

/// <summary>A chat line received. <paramref name="FromId"/> is the avatar or object that spoke.</summary>
public sealed record ChatLine(ChatKind Kind, Guid FromId, string FromName, string Text);

/// <summary>The kinds of the user's actions an add-on may filter (interface 0.1).</summary>
public enum AddonActionKind { Sit, Stand, Teleport }

/// <summary>An action the user is about to take.</summary>
public sealed record AddonAction(AddonActionKind Kind);

/// <summary>A filter's answer. A refusal's reason is shown to the user.</summary>
public readonly record struct AddonVerdict(bool Allowed, string? Reason)
{
    public static AddonVerdict Allow => new(true, null);
    public static AddonVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>An inventory folder as an add-on sees it.</summary>
public readonly record struct AddonFolder(Guid Id, string Name);

/// <summary>An inventory item as an add-on sees it. <paramref name="AssetId"/> is given for animations only, and is empty
/// while the item a link points to is not known.</summary>
/// <paramref name="IsNotecard"/> (interface 0.3) marks a notecard, whose text <see cref="IAddonInventory.ReadNotecardTextAsync"/> can give.
public readonly record struct AddonItem(Guid Id, string Name, string Description, bool IsLink, bool IsAnimation, Guid LinkedId, Guid AssetId, bool IsNotecard = false);

/// <summary>
/// Changes to the user's inventory, inside the folder the add-on's manifest names in "inventory_folder" (a path of folder
/// names from the inventory's top, such as "#Firestorm/#AO"). The folders of that path are made when missing. A link may
/// point at any item of the user's (making it changes nothing of the item). Anything that would change something outside
/// the folder answers false (or an empty id) and changes nothing. Moving to the Trash never purges.
/// </summary>
public interface IAddonInventoryWriter
{
    /// <summary>A new folder inside the add-on's folder; empty when it was not made.</summary>
    Task<Guid> CreateFolderAsync(Guid parent, string name);
    /// <summary>A link in <paramref name="folder"/> to the item <paramref name="target"/> with the description given.</summary>
    Task<bool> LinkAsync(Guid folder, Guid target, string description);
    Task<bool> RenameFolderAsync(Guid folder, string name);
    /// <summary>Moves an item or a folder inside the add-on's folder to the Trash.</summary>
    Task<bool> MoveToTrashAsync(Guid id, bool isFolder);
}

/// <summary>One thing the user dropped on a drop zone: an inventory item or folder.</summary>
public readonly record struct AddonDropped(Guid Id, bool IsFolder, string Name);

/// <summary>The user's inventory, read only.</summary>
public interface IAddonInventory
{
    Guid RootId { get; }
    bool IsComplete(Guid folder);
    /// <summary>Asks for the folder's contents; they arrive later.</summary>
    void Fetch(Guid folder);
    IReadOnlyList<AddonFolder> Folders(Guid folder);
    IReadOnlyList<AddonItem> Items(Guid folder);
    /// <summary>The asset of the animation a link points to, as known now; empty while it is not known.</summary>
    Guid AssetOf(Guid linkItemId);
    /// <summary>Asks the viewer to read folders again (contents that may have changed since they were read).</summary>
    void Refresh(Guid folder);

    /// <summary>(Interface 0.3.) The text of a notecard the user handed the add-on: one the user dropped on an add-on's drop zone,
    /// or one inside a folder the user dropped there, that the user may read. Embedded items stand as their names. Null for any
    /// other notecard, or when it could not be read.</summary>
    Task<string?> ReadNotecardTextAsync(Guid itemId) => Task.FromResult<string?>(null);
}

/// <summary>
/// What the viewer gives one add-on. Each member checks the add-on's grant: using something not granted throws
/// <see cref="AddonPermissionException"/>, and the viewer then switches the add-on off with a note to the user.
/// </summary>
public interface IAddonContext
{
    /// <summary>The add-on's id, from its manifest.</summary>
    string Id { get; }

    /// <summary>The permissions the user granted.</summary>
    IReadOnlyCollection<string> Granted { get; }

    /// <summary>A folder that is the add-on's own, for its settings and files. Needs no permission.</summary>
    string SettingsFolder { get; }

    /// <summary>Reads a setting from the add-on's own settings file (settings.json in its folder); null when not set.</summary>
    string? GetSetting(string key);

    /// <summary>Writes a setting to the add-on's own settings file; null removes it.</summary>
    void SetSetting(string key, string? value);

    /// <summary>Seconds since the viewer started, the clock every timer should use.</summary>
    double Now { get; }

    /// <summary>Once a frame. Needs no permission.</summary>
    event Action? Tick;

    // ---- events ----

    /// <summary>Whether the user is logged in now. Needs <see cref="AddonPermissions.Session"/>.</summary>
    bool LoggedInNow { get; }
    event Action? LoggedIn;
    event Action? LoggedOut;
    /// <summary>The user's avatar arrived in another region (the region's name).</summary>
    event Action<string>? RegionChanged;

    /// <summary>Needs <see cref="AddonPermissions.Motion"/>.</summary>
    MotionState Motion { get; }
    event Action<MotionState>? MotionChanged;

    /// <summary>Whether the camera is in mouse-look. Needs <see cref="AddonPermissions.Motion"/>.</summary>
    bool Mouselook { get; }
    event Action<bool>? MouselookChanged;

    /// <summary>Needs <see cref="AddonPermissions.AnimationsRead"/>.</summary>
    IReadOnlyList<PlayingAnimation> Playing { get; }
    event Action<IReadOnlyList<PlayingAnimation>>? PlayingChanged;

    /// <summary>Needs <see cref="AddonPermissions.ChatRead"/>.</summary>
    event Action<ChatLine>? ChatReceived;

    // ---- actions ----

    /// <summary>Starts an animation on the user's own avatar. Needs <see cref="AddonPermissions.AnimationsPlay"/>.
    /// Requests made in one frame go to the region together, in order, at the end of the frame.</summary>
    void PlayAnimation(Guid animation);

    /// <summary>Stops an animation on the user's own avatar. Needs <see cref="AddonPermissions.AnimationsPlay"/>.</summary>
    void StopAnimation(Guid animation);

    /// <summary>Plays an animation on the user's own avatar in this viewer only, as a preview: nothing is sent to the region,
    /// so nobody else sees it. Playing another, or <see cref="StopLocally"/>, ends it. Needs <see cref="AddonPermissions.AnimationsPlay"/>.</summary>
    void PlayLocally(Guid animation);

    /// <summary>Ends the local preview. Needs <see cref="AddonPermissions.AnimationsPlay"/>.</summary>
    void StopLocally();

    /// <summary>Says a line in local chat as the user (through the same checks as the user's own chat). False when it was not
    /// sent. Needs <see cref="AddonPermissions.ChatSend"/>.</summary>
    bool Say(string text);

    // ---- refusals ----

    /// <summary>Asks to be consulted before every action of this kind; the first filter that refuses wins. Dispose to remove.
    /// Needs <see cref="AddonPermissions.Filter"/>.</summary>
    IDisposable AddFilter(AddonActionKind kind, Func<AddonAction, AddonVerdict> filter);

    // ---- the screen ----

    /// <summary>Adds a window (closed at first). Needs <see cref="AddonPermissions.Window"/>.</summary>
    AddonWindow AddWindow(string id, string title);

    /// <summary>Adds an item to the Add-ons menu that runs <paramref name="chosen"/>. Needs <see cref="AddonPermissions.Window"/>.</summary>
    void AddMenuItem(string label, Action chosen);

    /// <summary>Puts a small control (a switch or a choice list) in the viewer's quick bar beside its own quick buttons.
    /// Needs <see cref="AddonPermissions.Window"/>. Not yet public: only add-ons that come with the viewer may use it; for
    /// any other it throws <see cref="AddonPermissionException"/>.</summary>
    void AddQuickControl(AddonControl control);

    // ---- the dock (interface 0.5) ----

    /// <summary>(Interface 0.5.) Puts a button on the viewer's dock: in its Make group after Build, in the order add-ons ask. An add-on
    /// cannot draw its own controls on the main screen; this is how it gets a place there. <paramref name="label"/> is the name under
    /// the button (ten characters at most; longer text is cut with an ellipsis); <paramref name="icon"/> is the name of an icon from the
    /// viewer's set or a single-colour svg the add-on ships (drawn in the current text colour), "plug" when none; <paramref name="menu"/>
    /// is what the chevron and a right-click show. At most two items per add-on; null when refused (a third item, or an id already in
    /// use). The items go the moment the add-on is turned off. Needs <see cref="AddonPermissions.Window"/>.</summary>
    AddonDockItem? AddDockItem(string id, string label, AddonDockKind kind, string? icon = null, string? tooltip = null,
        IReadOnlyList<AddonDockMenuEntry>? menu = null) => null;

    // ---- inventory ----

    /// <summary>The user's inventory, read only; null while logged out. Needs <see cref="AddonPermissions.InventoryRead"/>.</summary>
    IAddonInventory? Inventory { get; }

    /// <summary>Changes inside the add-on's own inventory folder; null while logged out. Needs <see cref="AddonPermissions.InventoryWrite"/>.</summary>
    IAddonInventoryWriter? InventoryWriter { get; }

    // ---- restraints (interface 0.4, not yet public) ----

    /// <summary>The viewer's restraint enforcement. Needs <see cref="AddonPermissions.Restraints"/>, which only add-ons that come
    /// with the viewer are granted; any other gets <see cref="AddonPermissionException"/>.</summary>
    IAddonRestraints? Restraints => null;
}

/// <summary>(Interface 0.5.) What a dock item does when clicked: runs something, or turns on and off (with a menu on its chevron).</summary>
public enum AddonDockKind { Action, Toggle, ToggleWithMenu }

/// <summary>(Interface 0.5.) One entry of a dock item's menu: a label, an optional icon name, an optional check mark, or a separator.</summary>
public sealed record AddonDockMenuEntry(string Label, string? Icon = null, bool? Checked = null, bool Separator = false);

/// <summary>
/// (Interface 0.5.) A button an add-on has on the dock. The add-on sets its state (on or off, attention with an optional count, its
/// menu) or removes it; the viewer says when it was clicked and which menu entry was chosen. Made by the viewer only
/// (<see cref="IAddonContext.AddDockItem"/>).
/// </summary>
public sealed class AddonDockItem
{
    private IReadOnlyList<AddonDockMenuEntry> _menu;

    private AddonDockItem(string id, string label, AddonDockKind kind, string? icon, string? tooltip, IReadOnlyList<AddonDockMenuEntry>? menu)
    {
        Id = id; Label = label; Kind = kind; Icon = icon; Tooltip = tooltip; _menu = menu?.ToArray() ?? [];
    }

    /// <summary>Made by the viewer only.</summary>
    public static AddonDockItem Create(string id, string label, AddonDockKind kind, string? icon, string? tooltip, IReadOnlyList<AddonDockMenuEntry>? menu) =>
        new(id, label, kind, icon, tooltip, menu);

    public string Id { get; }
    public string Label { get; }
    public AddonDockKind Kind { get; }
    public string? Icon { get; }
    public string? Tooltip { get; }
    public bool On { get; private set; }
    public bool Attention { get; private set; }
    public int? Count { get; private set; }
    public IReadOnlyList<AddonDockMenuEntry> Menu => _menu;

    /// <summary>True once the add-on removed it.</summary>
    public bool Removed { get; private set; }

    /// <summary>Raised to the add-on when the button (its main part) was clicked.</summary>
    public event Action? Clicked;

    /// <summary>Raised to the add-on when an entry of its menu was chosen: the entry's index.</summary>
    public event Action<int>? MenuChosen;

    /// <summary>Raised to the viewer when the add-on changed the item's state or removed it.</summary>
    public event Action? Changed;

    public void SetOn(bool on) { if (!STUB) { On = on; Changed?.Invoke(); } }

    public void SetAttention(bool on, int? count = null) { if (!STUB) { Attention = on; Count = on ? count : null; Changed?.Invoke(); } }

    public void SetMenu(IReadOnlyList<AddonDockMenuEntry> menu) { if (!STUB) { _menu = menu.ToArray(); Changed?.Invoke(); } }

    public void Remove() { if (!STUB) { Removed = true; Changed?.Invoke(); } }

    /// <summary>The viewer: the button was clicked.</summary>
    public void Click() { if (!STUB) Clicked?.Invoke(); }

    /// <summary>The viewer: a menu entry was chosen.</summary>
    public void ChooseMenu(int index) { if (!STUB && index >= 0 && index < _menu.Count) MenuChosen?.Invoke(index); }

    private const bool STUB = true;
}

/// <summary>One restriction an object holds: the object, the behaviour and its option, whether the viewer enforces it, and
/// when it was set (seconds on the add-on's clock).</summary>
public sealed record AddonRestriction(Guid ObjectId, string ObjectName, string Behaviour, string Option, bool Enforced, double SetAt);

/// <summary>(Interface 0.4, not yet public.) The viewer's side of a restraint engine: the add-on keeps the protocol, the viewer
/// enforces what it is handed. A restriction the viewer does not know is ignored. Everything handed over is dropped when the
/// add-on stops, for any reason (so an add-on that fails lets every action go ahead).</summary>
public interface IAddonRestraints
{
    /// <summary>The viewer's own name, for version replies.</summary>
    string ViewerName { get; }

    /// <summary>Replaces the set of restrictions the viewer enforces.</summary>
    void Publish(IReadOnlyList<AddonRestriction> restrictions);

    /// <summary>Says a reply on a chat channel as the user, past every restriction (RLV replies). False when it was not sent.</summary>
    bool Reply(int channel, string text);

    /// <summary>Sits the user on an object, past the restrictions of <paramref name="issuer"/>. False when refused or not sent.</summary>
    bool ForceSit(Guid issuer, Guid objectId);

    /// <summary>Stands the user up, past the restrictions of <paramref name="issuer"/>. False when refused or not sent.</summary>
    bool ForceStand(Guid issuer);

    /// <summary>Teleports the user to a global position in metres, past the restrictions of <paramref name="issuer"/>.</summary>
    bool ForceTeleport(Guid issuer, double x, double y, double z);

    /// <summary>Whether an object is in the viewer's object list now (worn, or in the region).</summary>
    bool ObjectPresent(Guid objectId);
}

/// <summary>A window an add-on adds: a title and a column of controls. The viewer draws it in its own style.</summary>
public sealed class AddonWindow
{
    private readonly List<AddonControl> _controls = [];

    internal AddonWindow(string id, string title)
    {
        Id = id;
        Title = title;
    }

    /// <summary>Made by the viewer only (<see cref="IAddonContext.AddWindow"/>).</summary>
    public static AddonWindow Create(string id, string title) => new(id, title);

    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<AddonControl> Controls => _controls;

    /// <summary>Raised when the add-on asks for the window to open or close (true opens).</summary>
    public event Action<bool>? OpenRequested;

    /// <summary>Raised when a control is added.</summary>
    public event Action? Changed;

    public T Add<T>(T control) where T : AddonControl
    {
        _controls.Add(control);
        Changed?.Invoke();
        return control;
    }

    /// <summary>Puts a control at a place in the column (0 is the top).</summary>
    public T Insert<T>(int index, T control) where T : AddonControl
    {
        _controls.Insert(Math.Clamp(index, 0, _controls.Count), control);
        Changed?.Invoke();
        return control;
    }

    /// <summary>Replaces every control at once (one redraw).</summary>
    public void SetControls(IEnumerable<AddonControl> controls)
    {
        _controls.Clear();
        _controls.AddRange(controls);
        Changed?.Invoke();
    }

    /// <summary>Takes a control out of the window.</summary>
    public bool Remove(AddonControl control)
    {
        bool removed = _controls.Remove(control);
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    public void Open() => OpenRequested?.Invoke(true);
    public void Close() => OpenRequested?.Invoke(false);
}

/// <summary>A control in an add-on's window. <see cref="Changed"/> tells the viewer to draw it again.</summary>
public abstract class AddonControl(string id)
{
    private string _text = "";
    private bool _enabled = true;

    public string Id { get; } = id;
    public string Text { get => _text; set { _text = value; Changed?.Invoke(); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Changed?.Invoke(); } }
    public event Action? Changed;
    protected void Raise() => Changed?.Invoke();
}

public sealed class AddonLabel(string id) : AddonControl(id);

public sealed class AddonButton(string id) : AddonControl(id)
{
    public event Action? Pressed;
    /// <summary>The viewer calls this when the user presses the button.</summary>
    public void Press() => Pressed?.Invoke();
}

public sealed class AddonToggle(string id) : AddonControl(id)
{
    private bool _on;
    public bool On { get => _on; set { _on = value; Raise(); } }
    public event Action<bool>? Toggled;
    /// <summary>The viewer calls this when the user turns the switch.</summary>
    public void Turn(bool on) { _on = on; Toggled?.Invoke(on); }
}

public sealed class AddonChoice(string id) : AddonControl(id)
{
    private IReadOnlyList<string> _items = [];
    private int _selected = -1;
    public IReadOnlyList<string> Items { get => _items; set { _items = [.. value]; Raise(); } }
    public int Selected { get => _selected; set { _selected = value; Raise(); } }
    public event Action<int>? Chosen;
    /// <summary>The viewer calls this when the user picks an item.</summary>
    public void Choose(int index) { _selected = index; Chosen?.Invoke(index); }
}

/// <summary>A one-line text box; <see cref="AddonControl.Text"/> is its label and <see cref="Value"/> what the user typed.</summary>
public sealed class AddonTextField(string id) : AddonControl(id)
{
    private string _value = "";
    public string Value { get => _value; set { _value = value; Raise(); } }
    /// <summary>The viewer calls this as the user types.</summary>
    public void Type(string value) => _value = value;
    /// <summary>Raised when the user presses Enter in the field.</summary>
    public event Action? Submitted;
    /// <summary>Raised when the user presses Esc in the field.</summary>
    public event Action? Cancelled;
    /// <summary>Raised when the add-on asks for the keyboard to go to this field.</summary>
    public event Action? FocusRequested;
    /// <summary>How many times the add-on asked for the keyboard (for tests and tools).</summary>
    public int FocusRequests { get; private set; }
    /// <summary>The viewer calls this when the user presses Enter in the field.</summary>
    public void Submit() => Submitted?.Invoke();
    /// <summary>The viewer calls this when the user presses Esc in the field.</summary>
    public void Cancel() => Cancelled?.Invoke();
    /// <summary>Asks the viewer to put the keyboard in this field.</summary>
    public void Focus() { FocusRequests++; FocusRequested?.Invoke(); }
}

/// <summary>A place the user can drop inventory items and folders on (dragged from the inventory window); its text says what to drop.</summary>
public sealed class AddonDropZone(string id) : AddonControl(id)
{
    public event Action<IReadOnlyList<AddonDropped>>? Dropped;
    /// <summary>The viewer calls this when the user drops things on the zone.</summary>
    public void Drop(IReadOnlyList<AddonDropped> dropped) => Dropped?.Invoke(dropped);
}

/// <summary>Two or more buttons side by side.</summary>
public sealed class AddonButtonRow(string id, params AddonButton[] buttons) : AddonControl(id)
{
    public IReadOnlyList<AddonButton> Buttons { get; } = buttons;
}
