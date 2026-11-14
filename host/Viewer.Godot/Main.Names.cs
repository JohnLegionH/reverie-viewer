// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.NameTags;
using Viewer.Shell.Reconnect;

namespace Viewer.Godot;

/// <summary>
/// Name tags over avatars, and the notice (with Reconnect) when the grid ends a session unasked. The tags are planned by
/// <see cref="NameTagModel"/> every frame from the world state and drawn by <see cref="NameTagOverlay"/>; the notice is made by
/// <see cref="DropNotices"/> and a click on Reconnect runs <see cref="ReconnectModel"/> (the user's click only, never by itself).
/// </summary>
public partial class Main
{
    private NameTagOverlay? _nameTags;
    private NameTagModel? _tagModel;
    private DropPanel? _dropPanel;
    private ReconnectModel? _reconnect;
    private ViewerSession? _droppedSession;
    private IReadOnlyList<NameTagPlan> _lastTags = [];

    /// <summary>Whether the camera is in mouselook (the own avatar's tag is not drawn then); set from the mouse-look mode each frame before the tags are planned.</summary>
    private bool _mouselook;

    private void InstallNames(LoginModel login, StringTable strings)
    {
        if (_shell is null)
            return;
        _nameTags = new NameTagOverlay();
        _shell.MainView.AddChild(_nameTags);
        _reconnect = new ReconnectModel(login, strings, (t, ct) => Task.Delay(t, ct));
        _dropPanel = new DropPanel(strings, _reconnect);
        _dropPanel.ReconnectRequested += () =>
        {
            var drop = _droppedSession?.Drop;
            _ = Task.Run(() => _reconnect.RunAsync(drop, CancellationToken.None));   // the login runs off the main thread; the panel reads the model's status each frame
        };
        _dropPanel.Closed += () => _reconnect.Forget();   // the user is done with this login: its credentials go
        _shell.MainView.AddChild(_dropPanel);
    }

    /// <summary>The session that just ended is shown to the user when the grid ended it unasked (called once as the presentation goes).</summary>
    private void NoteDrop(ViewerSession ended)
    {
        if (_dropPanel is null || _reconnect is null || _shell is null)
            return;
        var notice = DropNotices.For(ended, _shell.Shell.Strings, _reconnect.HasLogin);
        if (notice is null)
            return;
        _droppedSession = ended;
        _dropPanel.ShowNotice(notice);
        Log.Info("session.drop-notice", CoreLog.F("kind", ended.Drop?.Kind.ToString() ?? ""), CoreLog.F("can-reconnect", notice.CanReconnect));
    }

    /// <summary>A session in world again hides the notice.</summary>
    private void WatchReconnected()
    {
        if (_dropPanel is { Visible: true } && Model?.Session is { IsEnded: false, Stage: SessionStage.InWorld } s && s != _droppedSession)
        {
            _dropPanel.Dismiss();
            _droppedSession = null;
        }
    }

    private static System.Numerics.Vector3 ToRegionAxes(Vector3 v) => new(v.X, -v.Z, v.Y);

    /// <summary>Plans and draws this frame's tags.</summary>
    private void UpdateNameTags()
    {
        if (_nameTags is null)
            return;
        if (_presented is not { } session || _camera is null || _shell is null)
        {
            _lastTags = [];
            _nameTags.HideAll();
            return;
        }
        var settings = UserSettings(null);
        var strings = _shell.Shell.Strings;
        _tagModel ??= new NameTagModel(() => NameTagSettings.From(settings), () => _cameraClock.Elapsed.TotalSeconds, k => strings.Get(k));
        var friends = _comms?.Friends.Friends.Select(f => f.Id).ToHashSet() ?? [];
        var inputs = new List<NameTagInput>();
        foreach (var a in session.World.AvatarList())
        {
            bool self = a.AgentId == session.AgentId;
            var parts = (a.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string full = parts.Length == 2 ? NameTagModel.FullName(parts[0], parts[1]) : (a.Name ?? "");
            var anims = session.World.Animations(a.AgentId).Select(p => p.AnimationId).ToArray();
            // where the body is drawn (by whichever region draws it), not where the region last stated it
            var (position, rotation) = _bodyHost is not null && _bodyHost.TryDrawnPose(a.AgentId, out var drawnAt, out var drawnHeading) ? (drawnAt, drawnHeading) : NameTagPose.Of(OwnAvatars?.Scene, a);
            System.Numerics.Vector3? head = _bodyHost is not null && _bodyHost.TryDrawnHead(a.AgentId, out var drawnHead) ? drawnHead : null;
            inputs.Add(new NameTagInput(a.AgentId, new AvatarNames(full), a.Title, position, rotation, a.Scale, self, friends.Contains(a.AgentId),
                !self && session.Mutes.IsMuted(a.AgentId, null, 0, nameCounts: false), anims, head));
        }
        var basis = _camera.GlobalTransform.Basis;
        var cam = new NameTagCamera(ToRegionAxes(_camera.GlobalPosition), ToRegionAxes(-basis.Z), ToRegionAxes(basis.Y), _camera.Near);
        _mouselook = _mouseLook.Active;   // the own tag is hidden while the view is in mouse-look
        _lastTags = _tagModel.Update(inputs, cam, _mouselook);
        _nameTags.Show(_lastTags, _camera, GetViewport().GetVisibleRect().Size);
    }

    /// <summary>Development builds: how many tags are drawn and for whom (ids and counts, never a name).</summary>
    public object NameTagsState() => new
    {
        shown = _nameTags?.Shown ?? 0,
        drawn = _nameTags?.DrawnAgents.ToArray() ?? [],
        tags = _lastTags.Select(t => new { agent = t.AgentId, alpha = Math.Round(t.Alpha, 3), lines = t.Lines.Count, distance = Math.Round(t.Distance, 2), anchor = new[] { Math.Round(t.Anchor.X, 2), Math.Round(t.Anchor.Y, 2), Math.Round(t.Anchor.Z, 2) } }).ToArray(),
        mode = UserSettings(null).Get(Settings.NameTagMode),
        show_self = UserSettings(null).Get(Settings.NameTagShowSelf),
    };

#if NV_AUTOMATION
    /// <summary>The fault command (development builds only): makes the circuit take in no more packets so it times out.</summary>
    public object FaultCall(string action)
    {
        if (action != "drop_inbound")
            return new { ok = false, why = "unknown action" };
        bool done = Model?.Session?.CloseInboundCircuit() ?? false;
        return new { ok = done };
    }

    /// <summary>The mute command (development builds only): where the block list stands, how many entries it holds and which ids; with
    /// <c>block</c> or <c>unblock</c> and a resident's id, the same add or remove the interface makes (the name comes from the name cache, so
    /// no name passes through the automation connection).</summary>
    public object? MuteCall(string action = "state", Guid agent = default)
    {
        if (Model?.Session is not { } s)
            return null;
        bool? done = null;
        if (action is "block" or "unblock" && agent != Guid.Empty)
        {
            string name = s.Names.Get(agent) ?? "";
            done = action == "block" ? s.Block(Viewer.Core.Session.MuteKind.Resident, agent, name).Done : s.Unblock(agent, name);
        }
        return new
        {
            state = s.Mutes.State.ToString(), count = s.Mutes.Count, dropped = s.MutedDrops, done,
            ids = s.Mutes.Entries.Select(e => e.Id).ToArray(),
        };
    }

    /// <summary>The reconnect command (development builds only): the notice's state, and a click on its button.</summary>
    public object ReconnectCall(string action)
    {
        if (action == "cancel")
            _reconnect?.Cancel();
        if (action == "click" && _dropPanel is { Visible: true, Notice.CanReconnect: true })
        {
            var drop = _droppedSession?.Drop;
            if (_reconnect is not null)
                _ = Task.Run(() => _reconnect.RunAsync(drop, CancellationToken.None));
        }
        return new
        {
            notice = _dropPanel?.Visible ?? false,
            kind = _droppedSession?.Drop?.Kind.ToString(),
            can_reconnect = _dropPanel?.Notice?.CanReconnect ?? false,
            state = _reconnect?.State.ToString(),
            attempts = _reconnect?.Attempts ?? 0,
            countdown = _reconnect?.Countdown ?? 0,
        };
    }
#endif
}
