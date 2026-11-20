// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Assets;
using Viewer.Core.Avatars;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>
/// Gestures in the running viewer, as the official gesture manager has them (llgesturemgr.cpp): the gestures the grid lists as active are read
/// when the inventory arrives; a line of open chat with a trigger plays its gesture and is said revised; F2 to F12, with Shift or Ctrl, play
/// the gesture bound to them; a gesture opened from the inventory (or made new, which also activates it, as create_gesture_cb does) shows in
/// the gesture editor, whose Preview plays the gesture as it stands and whose Save stores it and, when active, activates the saved version.
/// The Gestures window (Avatar > Gestures..., Ctrl+G) lists them, activates and deactivates them, plays them and opens them in the editor.
/// </summary>
public partial class Main
{
    private ActiveGestures? _gestureSet;
    private ViewerSession? _gesturesFor;
    private GesturePanel? _gesturePanel;
    private Guid _gestureItem;
    private CancellationTokenSource _gestureCancel = new();
    private bool _gestureOpenHooked;
    private readonly Dictionary<Guid, CancellationTokenSource> _gesturePlays = new();

    private void AttachGestures(ViewerSession session)
    {
        if (_gesturesFor == session)
            return;
        DetachGestures();
        _gesturesFor = session;
        _gestureSet = new ActiveGestures(g => PlayGesture(session, g), pick: n => System.Random.Shared.Next(n));
        InstallGestureList();
        session.Inventory.Changed += OnGestureInventoryChanged;
        if (!_gestureOpenHooked && _inventoryPanel is not null)
        {
            _inventoryPanel.OpenRequested += id => { if (_inventoryFor?.Inventory.Item(id) is { Kind: ItemKind.Gesture }) OpenGesture(id); };
            _gestureOpenHooked = true;
        }
        if (session.Gestures is not { } transport)
            return;
        var token = _gestureCancel.Token;
        var set = _gestureSet;
        _ = Task.Run(async () =>
        {
            foreach (var (item, asset) in transport.ActiveGestures())
            {
                var fetched = await transport.FetchGestureAsync(asset, token).ConfigureAwait(false);
                if (fetched.Status == AssetFetchStatus.Ok && GestureFormat.Parse(fetched.Bytes.Span) is { } doc)
                    set.Set(item, doc);
            }
            Log.Info("gestures.active", Viewer.Core.Diagnostics.CoreLog.F("count", transport.ActiveGestures().Count));
            Callable.From(RefreshGestureList).CallDeferred();   // their triggers and keys show in the Gestures window
        }, token);
    }

    private void DetachGestures()
    {
        if (_gesturesFor is { } old)
            old.Inventory.Changed -= OnGestureInventoryChanged;
        _gestureCancel.Cancel();
        _gestureCancel = new CancellationTokenSource();
        _gestureSet?.Clear();
        _gestureSet = null;
        _gesturesFor = null;
        if (_gesturePanel is not null && _shell is not null)
            _shell.Shell.Floaters.Close(GesturePanel.FloaterId, force: true);
        if (_gestureList is not null && _shell is not null)
            _shell.Shell.Floaters.Close(GesturesFeature.FloaterId, force: true);
    }

    /// <summary>Plays a gesture on the user: animations through the region, sounds as a triggered sound, chat said on channel 0. A gesture
    /// that is active is tracked by its item while it plays, so the Gestures window's Stop can end it (llgesturemgr.cpp stopGesture).</summary>
    private void PlayGesture(ViewerSession session, GestureDocument gesture)
    {
        var fx = new SessionGestureEffects(session);
        var own = CancellationTokenSource.CreateLinkedTokenSource(_gestureCancel.Token);
        var token = own.Token;
        Guid item = _gestureSet?.Items().FirstOrDefault(i => ReferenceEquals(_gestureSet.Get(i), gesture)) ?? Guid.Empty;
        if (item != Guid.Empty)
        {
            CancellationTokenSource? before;
            lock (_gesturePlays)
            {
                _gesturePlays.TryGetValue(item, out before);
                _gesturePlays[item] = own;
            }
            try { before?.Cancel(); }   // played again while it plays: it starts over
            catch (ObjectDisposedException) { }
            Callable.From(ShowGesturesPlaying).CallDeferred();
        }
        Log.Info("gesture.play", Viewer.Core.Diagnostics.CoreLog.F("steps", gesture.Steps.Count));
        _ = Task.Run(async () =>
        {
            try { await GesturePlayer.PlayAsync(gesture, fx, (span, ct) => Task.Delay(span, ct), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_gesturePlays)
                {
                    if (item != Guid.Empty && _gesturePlays.TryGetValue(item, out var current) && ReferenceEquals(current, own))
                        _gesturePlays.Remove(item);
                }
                own.Dispose();
                if (item != Guid.Empty)
                    Callable.From(ShowGesturesPlaying).CallDeferred();
            }
        });
    }

    /// <summary>The Gestures window's Stop button follows which gestures play.</summary>
    private void ShowGesturesPlaying()
    {
        Guid[] playing;
        lock (_gesturePlays)
            playing = _gesturePlays.Keys.ToArray();
        _gestureList?.SetPlaying(playing);
    }

    /// <summary>Stop: ends the chosen gestures that play, and the animations they started.</summary>
    private void StopFromList(IReadOnlyList<Guid> items)
    {
        foreach (var item in items)
        {
            CancellationTokenSource? playing;
            lock (_gesturePlays)
                _gesturePlays.TryGetValue(item, out playing);
            try { playing?.Cancel(); }
            catch (ObjectDisposedException) { }   // it had just ended
        }
        Log.Info("gestures.stop", Viewer.Core.Diagnostics.CoreLog.F("count", items.Count));
    }

    /// <summary>Delete, answered Yes: each gesture is stopped and deactivated, then moved to the Trash (never purged), and the editor
    /// closes when it showed one of them.</summary>
    private void DeleteFromList(IReadOnlyList<Guid> items)
    {
        if (_gesturesFor is not { IsEnded: false } session || session.Gestures is not { } transport || _shell is null)
            return;
        var actions = new InventoryActions(session.Inventory, session.Actions.Submit);
        int moved = 0;
        foreach (var item in items)
        {
            StopFromList([item]);
            if (_gestureSet?.IsActive(item) == true || transport.ActiveGestures().Any(g => g.ItemId == item))
            {
                transport.SetGestureActive(item, transport.GestureAssetOf(item), false);
                _gestureSet?.Remove(item);
            }
            if (actions.Trash(item, confirmed: true).Submitted)
                moved++;
            if (_gestureItem == item)
            {
                _gestureItem = Guid.Empty;
                _shell.Shell.Floaters.Close(GesturePanel.FloaterId, force: true);
            }
        }
        Log.Info("gestures.delete", Viewer.Core.Diagnostics.CoreLog.F("count", items.Count), Viewer.Core.Diagnostics.CoreLog.F("moved", moved));
        RefreshGestureList();
    }

    private sealed class SessionGestureEffects(ViewerSession session) : IGestureEffects
    {
        public void Animate(Guid animation, bool start) => session.SendAnimation(animation, start);
        public void Sound(Guid sound) => session.Gestures?.TriggerSound(sound);
        public void Say(string text) => session.Actions.Submit(new SayIntent(text));
        public bool IsPlaying(Guid animation) => session.World.Animations(session.AgentId).Any(a => a.AnimationId == animation);
    }

    /// <summary>F2 to F12, alone or with Shift or Ctrl, play the active gesture bound to them (llgesturemgr.cpp triggerGesture).</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || _gestureSet is null || k.Keycode < Key.F2 || k.Keycode > Key.F12)
            return;
        byte key = (byte)(0xA2 + (k.Keycode - Key.F2));
        uint mask = (k.CtrlPressed ? GestureKeys.MaskControl : 0) | (k.ShiftPressed ? GestureKeys.MaskShift : 0) | (k.AltPressed ? 0x0002u : 0);
        if (_gestureSet.Key(key, mask))
            GetViewport().SetInputAsHandled();
    }

    /// <summary>The gesture window, made once.</summary>
    private GesturePanel Gestures()
    {
        if (_gesturePanel is not null)
            return _gesturePanel;
        _gesturePanel = new GesturePanel(_shell!.Shell.Strings);
        // the official window is 460 by 550 (floater_preview_gesture.xml)
        _shell.AddFloater(new FloaterSpec(GesturePanel.FloaterId, "gesture.title", new UiSize(480, 560), new UiSize(400, 440)), _gesturePanel);
        _gesturePanel.SaveRequested = SaveGesture;
        _gesturePanel.PreviewRequested = doc => { if (_gesturesFor is { IsEnded: false } s) { PlayGesture(s, Copy(doc)); _gesturePanel.ShowStatus("gesture.playing"); } };
        _gesturePanel.ActiveChanged = SetGestureActive;
        return _gesturePanel;
    }

    private static GestureDocument Copy(GestureDocument g) => GestureFormat.Parse(GestureFormat.Write(g)) ?? new GestureDocument();

    /// <summary>Opens an inventory gesture in the editor: its file is read (a new gesture has none yet and starts empty).</summary>
    private void OpenGesture(Guid itemId)
    {
        if (_inventoryFor is not { IsEnded: false } session || _shell is null || session.Gestures is not { } transport)
            return;
        var item = session.Inventory.Item(itemId);
        if (item?.LinkedId is { } linked && session.Inventory.Item(linked) is { Kind: ItemKind.Gesture })
            itemId = linked;
        string name = session.Inventory.Item(itemId)?.Name ?? "";
        var panel = Gestures();
        _gestureItem = itemId;
        _shell.SetFloaterTitle(GesturePanel.FloaterId, name.Length > 0 ? name : _shell.Shell.Strings.Get("gesture.title"));
        _shell.Shell.Floaters.Open(GesturePanel.FloaterId);
        _shell.Shell.Floaters.Focus(GesturePanel.FloaterId);
        var all = session.Inventory.AllItems();
        var anims = BuiltInAnimations.Choices(all.Where(i => i.Kind == ItemKind.Animation && i.AssetId is { } a && a != Guid.Empty).Select(i => (i.AssetId!.Value, i.Name)).ToArray()).ToArray();   // the built-in animations first, then the agent's own (LLPreviewGesture::addAnimations)
        var sounds = all.Where(i => i.Kind == ItemKind.Sound && i.AssetId is { } a && a != Guid.Empty).Select(i => (i.AssetId!.Value, i.Name)).OrderBy(a => a.Name).ToArray();
        bool active = _gestureSet?.IsActive(itemId) == true || transport.ActiveGestures().Any(g => g.ItemId == itemId);
        Guid asset = transport.GestureAssetOf(itemId);
        panel.Show(new GestureDocument(), active, canEdit: true, anims, sounds);
        if (asset == Guid.Empty)
            return;
        panel.ShowStatus("gesture.loading");
        var token = _gestureCancel.Token;
        _ = transport.FetchGestureAsync(asset, token).ContinueWith(t =>
        {
            var doc = t.IsCompletedSuccessfully && t.Result.Status == AssetFetchStatus.Ok ? GestureFormat.Parse(t.Result.Bytes.Span) : null;
            Callable.From(() =>
            {
                if (_gestureItem != itemId)
                    return;
                if (doc is null)
                    panel.ShowStatus("gesture.failed");
                else
                    panel.Show(doc, active, canEdit: true, anims, sounds);
            }).CallDeferred();
        }, TaskScheduler.Default);
    }

    private void SaveGesture(GestureDocument doc)
    {
        if (_gesturesFor is not { IsEnded: false } session || session.Gestures is not { } transport || _gesturePanel is not { } panel || _gestureItem == Guid.Empty)
            return;
        Guid item = _gestureItem;
        var copy = Copy(doc);
        if (!GestureFormat.FitsSave(GestureFormat.Write(copy)))
        {
            panel.ShowStatus("gesture.save.too_big");   // GestureSaveFailedTooManySteps
            return;
        }
        panel.ShowStatus("gesture.saving");
        _ = transport.SaveGestureAsync(item, GestureFormat.Write(copy), _gestureCancel.Token).ContinueWith(t =>
        {
            var result = t.IsCompletedSuccessfully ? t.Result : NotecardSaveResult.Of(NotecardSaveStatus.Failed);
            Callable.From(() =>
            {
                if (result.Status != NotecardSaveStatus.Saved)
                {
                    panel.ShowStatus("gesture.save.failed");
                    return;
                }
                panel.MarkSaved();
                panel.ShowStatus("gesture.saved");
                // an active gesture plays its saved version from now on (llpreviewgesture.cpp onSaveComplete replaces the active one)
                if (_gestureSet?.IsActive(item) == true || panel.ActiveCheck.ButtonPressed)
                {
                    transport.SetGestureActive(item, result.NewAssetId, true);
                    _gestureSet?.Set(item, copy);
                }
                Log.Info("gesture.saved");
                RefreshGestureList();   // a changed trigger or key shows in the Gestures window
            }).CallDeferred();
        }, TaskScheduler.Default);
    }

    private void SetGestureActive(bool on)
    {
        if (_gesturesFor is not { IsEnded: false } session || session.Gestures is not { } transport || _gestureItem == Guid.Empty || _gesturePanel is not { } panel)
            return;
        Guid asset = transport.GestureAssetOf(_gestureItem);
        transport.SetGestureActive(_gestureItem, asset, on);
        if (on)
            _gestureSet?.Set(_gestureItem, Copy(panel.Document));
        else
            _gestureSet?.Remove(_gestureItem);
        Log.Info("gesture.active", Viewer.Core.Diagnostics.CoreLog.F("on", on));
        RefreshGestureList();
    }

    /// <summary>A new gesture is activated as soon as it is made (llviewerinventory.cpp create_gesture_cb), then opened.</summary>
    private void ActivateNewGesture(Guid itemId)
    {
        if (_gesturesFor is not { IsEnded: false } session || session.Gestures is not { } transport)
            return;
        transport.SetGestureActive(itemId, transport.GestureAssetOf(itemId), true);
        _gestureSet?.Set(itemId, new GestureDocument());
        RefreshGestureList();
    }

    // ---------------- the Gestures window (Avatar > Gestures..., Ctrl+G) ----------------

    private GesturesWindow? _gestureList;
    private int _gestureListDirty;

    /// <summary>The Gestures window and its menu entry, made once, with the first session.</summary>
    private void InstallGestureList()
    {
        if (_gestureList is not null || _shell is null)
            return;
        _gestureList = GesturesFeature.Install(_shell);
        _gestureList.Opened = RefreshGestureList;
        _gestureList.EditRequested = OpenGesture;
        _gestureList.PlayRequested = PlayFromList;
        _gestureList.ToggleRequested = ToggleFromList;
        _gestureList.StopRequested = StopFromList;
        _gestureList.DeleteRequested = DeleteFromList;
        // Make new gesture: made as Inventory > New Gesture makes it, in the Gestures folder, then activated and opened (onClickNew,
        // GestureShowCallback)
        _gestureList.NewRequested = () => OnNewRequested("gesture", Guid.Empty);
    }

    /// <summary>The inventory changed (on the network thread): the open window reads its rows again once, on the main thread.</summary>
    private void OnGestureInventoryChanged()
    {
        if (Interlocked.Exchange(ref _gestureListDirty, 1) == 0)
            Callable.From(() => { Interlocked.Exchange(ref _gestureListDirty, 0); RefreshGestureList(); }).CallDeferred();
    }

    /// <summary>The window's rows from the inventory and the active gestures; nothing is done while it is closed. The Gestures folder
    /// and its subfolders are read when not yet loaded, as the official window fetches them on opening (postBuild startFetch).</summary>
    private void RefreshGestureList()
    {
        if (_gestureList is null || _shell is null || !_shell.Shell.Floaters.IsOpen(GesturesFeature.FloaterId) || _gesturesFor is not { IsEnded: false } session)
            return;
        var inv = session.Inventory;
        Guid folder = inv.FolderOfKind(FolderKind.Gesture);
        if (folder != Guid.Empty)
        {
            inv.EnsureLoaded(folder);
            foreach (var sub in inv.AllFolders().Where(f => inv.IsDescendantOf(f.Id, folder) && inv.LoadState(f.Id) == FolderLoad.NotLoaded))
                inv.EnsureLoaded(sub.Id);
        }
        var active = (session.Gestures?.ActiveGestures().Select(g => g.ItemId) ?? []).Concat(_gestureSet?.Items() ?? []).ToHashSet();
        _gestureList.Show(GestureListRows.Build(inv, active, id => _gestureSet?.Get(id), _shell.Shell.Strings.Get("gestures.loading")));
    }

    /// <summary>The activate button: each gesture activated (its file read so its trigger and key work) or deactivated, as the official
    /// gesture manager does (llgesturemgr.cpp activateGesture, deactivateGesture); the editor's Active box follows.</summary>
    private void ToggleFromList(IReadOnlyList<(Guid ItemId, bool Activate)> changes)
    {
        if (_gesturesFor is not { IsEnded: false } session || session.Gestures is not { } transport)
            return;
        foreach (var (item, on) in changes)
        {
            Guid asset = transport.GestureAssetOf(item);
            transport.SetGestureActive(item, asset, on);
            if (on)
                LoadActiveGesture(session, item, asset, play: false);
            else
                _gestureSet?.Remove(item);
            if (_gestureItem == item && _gesturePanel is { } panel)
                panel.ActiveCheck.SetPressedNoSignal(on);
        }
        Log.Info("gestures.toggle", Viewer.Core.Diagnostics.CoreLog.F("count", changes.Count));
        RefreshGestureList();
    }

    /// <summary>Play or a double-click: an active gesture plays; an inactive one is activated first (the grid told) and plays once its
    /// file is read (llfloatergesture.cpp onClickPlay: activateGestureWithAsset, then playGesture from the loaded callback).</summary>
    private void PlayFromList(Guid item)
    {
        if (_gesturesFor is not { IsEnded: false } session || session.Gestures is not { } transport)
            return;
        if (_gestureSet?.Get(item) is { } doc)
        {
            PlayGesture(session, doc);
            return;
        }
        Guid asset = transport.GestureAssetOf(item);
        transport.SetGestureActive(item, asset, true);
        LoadActiveGesture(session, item, asset, play: true);
        RefreshGestureList();
    }

    /// <summary>Reads a gesture just made active and adds it to the active set (and plays it, when asked), then shows it in the list.</summary>
    private void LoadActiveGesture(ViewerSession session, Guid item, Guid asset, bool play)
    {
        if (session.Gestures is not { } transport || asset == Guid.Empty)
            return;
        if (_gestureSet is not { } set)
            return;
        _ = transport.FetchGestureAsync(asset, _gestureCancel.Token).ContinueWith(t =>
        {
            var doc = t.IsCompletedSuccessfully && t.Result.Status == AssetFetchStatus.Ok ? GestureFormat.Parse(t.Result.Bytes.Span) : null;
            Callable.From(() =>
            {
                if (doc is null || session.IsEnded || !ReferenceEquals(set, _gestureSet))
                    return;
                set.Set(item, doc);
                if (play)
                    PlayGesture(session, doc);
                RefreshGestureList();
            }).CallDeferred();
        }, TaskScheduler.Default);
    }
}
