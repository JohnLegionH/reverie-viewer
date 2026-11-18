// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Assets;
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
/// </summary>
public partial class Main
{
    private ActiveGestures? _gestureSet;
    private ViewerSession? _gesturesFor;
    private GesturePanel? _gesturePanel;
    private Guid _gestureItem;
    private CancellationTokenSource _gestureCancel = new();
    private bool _gestureOpenHooked;

    private void AttachGestures(ViewerSession session)
    {
        if (_gesturesFor == session)
            return;
        DetachGestures();
        _gesturesFor = session;
        _gestureSet = new ActiveGestures(g => PlayGesture(session, g), pick: n => System.Random.Shared.Next(n));
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
        }, token);
    }

    private void DetachGestures()
    {
        _gestureCancel.Cancel();
        _gestureCancel = new CancellationTokenSource();
        _gestureSet?.Clear();
        _gestureSet = null;
        _gesturesFor = null;
        if (_gesturePanel is not null && _shell is not null)
            _shell.Shell.Floaters.Close(GesturePanel.FloaterId, force: true);
    }

    /// <summary>Plays a gesture on the user: animations through the region, sounds as a triggered sound, chat said on channel 0.</summary>
    private void PlayGesture(ViewerSession session, GestureDocument gesture)
    {
        var fx = new SessionGestureEffects(session);
        var token = _gestureCancel.Token;
        Log.Info("gesture.play", Viewer.Core.Diagnostics.CoreLog.F("steps", gesture.Steps.Count));
        _ = Task.Run(() => GesturePlayer.PlayAsync(gesture, fx, (span, ct) => Task.Delay(span, ct), token), token);
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
        var anims = all.Where(i => i.Kind == ItemKind.Animation && i.AssetId is { } a && a != Guid.Empty).Select(i => (i.AssetId!.Value, i.Name)).OrderBy(a => a.Name).ToArray();
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
    }

    /// <summary>A new gesture is activated as soon as it is made (llviewerinventory.cpp create_gesture_cb), then opened.</summary>
    private void ActivateNewGesture(Guid itemId)
    {
        if (_gesturesFor is not { IsEnded: false } session || session.Gestures is not { } transport)
            return;
        transport.SetGestureActive(itemId, transport.GestureAssetOf(itemId), true);
        _gestureSet?.Set(itemId, new GestureDocument());
    }
}
