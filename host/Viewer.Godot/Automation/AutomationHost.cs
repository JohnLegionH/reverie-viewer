// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Automation;
using Viewer.Core.Diagnostics;
using Viewer.Core.Platform;
using Viewer.Core.Session;

namespace Viewer.Godot;

/// <summary>
/// The automation API's host side. DEVELOPMENT BUILDS ONLY (this file is not compiled into a shipping export,
/// Viewer.Godot.csproj) and only when started with <c>--automation</c> (Main). Runs <see cref="AutomationServer"/> and
/// answers its calls on the main thread, through the same LoginModel the login screen uses.
/// The token file is written to the per-user local data directory and removed when the host leaves the tree; the token
/// and the login URI are never logged. The session command's login reads the password from the accounts file itself.
/// </summary>
public partial class AutomationHost : Node
{
    private readonly Main? _main;
    private ViewportTexture? _viewportTexture;

    /// <summary>The window's picture as last drawn. The texture is kept: read back on each call it would be a fresh managed wrapper that may be finalizing.</summary>
    private Image ViewImage()
    {
        _viewportTexture ??= GetViewport().GetTexture();
        return _viewportTexture.GetImage();
    }
    private readonly ConcurrentQueue<(AutomationCall Call, TaskCompletionSource<AutomationReply> Reply)> _calls = new();
    private AutomationServer? _server;
    private string? _tokenPath;
    private int _quitIn = -1;

    public AutomationHost() { }

    public AutomationHost(Main main) => _main = main;

    public override void _Ready()
    {
        _server = new AutomationServer(call =>
        {
            var tcs = new TaskCompletionSource<AutomationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Enqueue((call, tcs));
            return tcs.Task;
        }, e => Main.Log.Info(e));
        _tokenPath = _server.WriteTokenFile(TokenFile.DefaultDirectory(DataDirectories.ForCurrentUser()));
        Main.Log.Info("automation.enabled", CoreLog.F("bind", "loopback"));
    }

    public override void _Process(double delta)
    {
        while (_calls.TryDequeue(out var item))
            _ = Answer(item.Call, item.Reply);
        if (_quitIn > 0 && --_quitIn == 0)
            _main!.RequestClose();   // as the window's close: a session in world is logged out first
    }

    private async Task Answer(AutomationCall call, TaskCompletionSource<AutomationReply> reply)
    {
        try { reply.TrySetResult(await Handle(call)); }
        catch (Exception e) { reply.TrySetResult(AutomationReply.Error(500, "failed: " + e.GetType().Name)); }
    }

    private async Task<AutomationReply> Handle(AutomationCall call)
    {
        var m = _main!;
        switch (call.Command)
        {
            case "session":
                return await Session(m, Str(call.Body, "action") ?? "state", call.Body);
            case "observe":
                return AutomationReply.Ok(m.Observe());
            case "prefs":   // {action: state|set|preset|save|cancel|reset, key?, value?, page?}: what Preferences holds and what is in force (development builds only)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("state" or "set" or "preset" or "save" or "cancel" or "reset"))
                    return AutomationReply.Error(400, "action must be state, set, preset, save, cancel or reset");
                System.Text.Json.JsonElement value = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("value", out var pv) ? pv : default;
                return m.PrefsCall(action, Str(call.Body, "key"), value, Str(call.Body, "page")) is { } prefs ? AutomationReply.Ok(prefs) : AutomationReply.Error(409, "no preferences window");
            }
            case "nametags":   // the tags drawn now, as agent ids, opacities and line counts
                return AutomationReply.Ok(m.NameTagsState());
            case "fault":   // {action: drop_inbound}: the circuit takes in no more packets, so it times out (development builds only)
                return AutomationReply.Ok(m.FaultCall(Str(call.Body, "action") ?? ""));
            case "mute":   // {action: state|block|unblock, agent?}: the block list's state, count and ids; block and unblock a resident by id
                return m.MuteCall(Str(call.Body, "action") ?? "state", Guid.TryParse(Str(call.Body, "agent"), out var muted) ? muted : Guid.Empty) is { } mute
                    ? AutomationReply.Ok(mute) : AutomationReply.Error(409, "no session");
            case "reconnect":   // {action: state|click|cancel}: the drop notice, its Reconnect button and the wait for a held session
                return AutomationReply.Ok(m.ReconnectCall(Str(call.Body, "action") ?? "state"));
            case "login_screen":   // {action: state|submit, accounts_file?, account?, login_uri?, remember?, use_saved?}: the login screen filled and submitted with Enter, as a person does; remember password's state (never a hash) (development builds only)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("state" or "submit"))
                    return AutomationReply.Error(400, "action must be state or submit");
                bool Flag(string key) => call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty(key, out var fv) && fv.ValueKind == JsonValueKind.True;
                string? name = null;
                Secret? password = null;
                if (action == "submit")
                {
                    string? accounts = Str(call.Body, "accounts_file"), alias = Str(call.Body, "account");
                    if (accounts is null && alias is null && Str(call.Body, "name") is { } typedName)
                        name = typedName;   // a name with no password: the attempt stops at the missing password (proves Enter submits, offline)
                    else if (accounts is null || alias is null || !Path.IsPathFullyQualified(accounts))
                        return AutomationReply.Error(400, "accounts_file (absolute) and account are required, or a name alone");
                    else
                    {
                        try { var acct = AccountsFile.Load(accounts, alias); name = acct.First + " " + acct.Last; password = acct.Password; }
                        catch (Exception) { return AutomationReply.Error(400, "account not available"); }
                    }
                }
                return m.LoginScreenCall(action, Str(call.Body, "login_uri"), name, password, Flag("remember"), Flag("use_saved"), Str(call.Body, "start")) is { } screen
                    ? AutomationReply.Ok(screen) : AutomationReply.Error(409, "no login screen");
            }
            case "topbar":   // {action: state|hover_all|click|type, index?, text?}: the top bar and the dock as a person meets them (development builds only)
                return await m.TopBarCall(Str(call.Body, "action") ?? "state", Str(call.Body, "text"), (int)Num(call.Body, "index", 0)) is { } tb ? AutomationReply.Ok(tb) : AutomationReply.Error(409, "no shell");
            case "dock":   // {id, owner?, menu?}: a dock button clicked, or an entry of its menu chosen (development builds only)
                return m.DockCall(Str(call.Body, "id") ?? "", Str(call.Body, "owner"), (int)Num(call.Body, "menu", -1)) is { } dk ? AutomationReply.Ok(dk) : AutomationReply.Error(409, "no shell");
            case "menus":   // the top menu open now, the menus on screen, the inventory's item menu and selection
                return AutomationReply.Ok(m.MenuState());
            case "prefs_layout":   // {page}: Preferences opened on a page; each control's top and bottom against the page's visible height (development builds only)
                return m.PrefsLayoutCall(Str(call.Body, "page") ?? "sound") is { } layout ? AutomationReply.Ok(layout) : AutomationReply.Error(409, "no preferences window or no such page");
            case "upload_notice":   // {action: state|show}: the last upload notice (its text key and buttons) and the inventory's selection; show presses Show in inventory
                return AutomationReply.Ok(m.UploadNoticeCall(Str(call.Body, "action") ?? "state"));
            case "texture_preview":   // {action: state|open_selected}: the texture preview (open, the item, the size read, whether the image is drawn); open_selected opens the inventory's selected row as Enter does
                if (Str(call.Body, "action") == "open_selected")
                    m.OpenSelectedInventoryRow();
                return AutomationReply.Ok(m.TexturePreviewState());
            case "login_dialog":   // {action: show|state|close, kind?}: an example of a login-flow dialog (terms, critical, update, mfa, trust, certificate_error, ...) on the login screen (development builds only)
                return m.LoginDialogCall(Str(call.Body, "action") ?? "state", Str(call.Body, "kind")) is { } shown ? AutomationReply.Ok(shown) : AutomationReply.Error(409, "no login screen");
            case "screenshot":
            {
                string? dir = Str(call.Body, "dir");
                if (dir is null || !Path.IsPathFullyQualified(dir) || !Directory.Exists(dir))
                    return AutomationReply.Error(400, "dir must be an existing absolute directory");
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var img = ViewImage();
                string name = Str(call.Body, "name") is { } n && n.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? n : $"screenshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                string path = Path.Combine(dir, name + ".png");
                img.SavePng(path);
                return AutomationReply.Ok(new { file = path, width = img.GetWidth(), height = img.GetHeight() });
            }
            case "body_mask":   // {dir, name?, hidden_too?}: the own avatar's body alone (hidden_too: with the parts its worn objects hide), flat white on black, from the camera as it is; answers the file, the camera and the drawn head joint (SL axes)
            {
                string? dir = Str(call.Body, "dir");
                if (dir is null || !Path.IsPathFullyQualified(dir) || !Directory.Exists(dir))
                    return AutomationReply.Error(400, "dir must be an existing absolute directory");
                if (m.ViewCamera is not { } cam)
                    return AutomationReply.Error(409, "no camera");
                System.Numerics.Matrix4x4 head = default;
                bool hiddenToo = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("hidden_too", out var ht) && ht.ValueKind == JsonValueKind.True;
                var img = await Presentation.BodyMask.Capture(this, cam, (Material? mat, out System.Numerics.Matrix4x4 h) => m.SetOwnMask(mat, hiddenToo, out h), h => head = h, ViewImage);
                if (img is null)
                    return AutomationReply.Error(409, "no own body drawn");
                string name = Str(call.Body, "name") is { } n && n.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? n : $"mask-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                string path = Path.Combine(dir, name + ".png");
                img.SavePng(path);
                var t = cam.GlobalTransform;
                var eye = Presentation.BodyMask.FromEngine(t.Origin);
                var forward = Presentation.BodyMask.FromEngine(-t.Basis.Z);
                var up = Presentation.BodyMask.FromEngine(t.Basis.Y);
                return AutomationReply.Ok(new
                {
                    file = path, hidden_too = hiddenToo, width = img.GetWidth(), height = img.GetHeight(), fov_deg = cam.Fov, keep_aspect = cam.KeepAspect.ToString(),
                    eye = new[] { eye.X, eye.Y, eye.Z }, forward = new[] { forward.X, forward.Y, forward.Z }, up = new[] { up.X, up.Y, up.Z },
                    head_world = new[] { head.M11, head.M12, head.M13, head.M14, head.M21, head.M22, head.M23, head.M24, head.M31, head.M32, head.M33, head.M34, head.M41, head.M42, head.M43, head.M44 },
                });
            }
            case "frame_burst":   // {}: the next 1.5 s of frames are saved as pictures under NV_LOG_DIR (development builds started with NV_FRAME_BURST=1)
                return m.FrameBurstStart("command") is { } burstDir ? AutomationReply.Ok(new { dir = burstDir }) : AutomationReply.Error(409, "frame bursts are off, or one is running");
            case "camera":   // {yaw_deg}: the camera only, about the vertical axis from its overview pose
            {
                if (call.Body.ValueKind != JsonValueKind.Object || !call.Body.TryGetProperty("yaw_deg", out var y) || y.ValueKind != JsonValueKind.Number)
                    return AutomationReply.Error(400, "yaw_deg (a number) is required");
                return m.SetCameraYaw(y.GetDouble()) is { } done ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no camera");
            }
            case "orbit":   // {yaw_deg?, pitch_deg?, zoom?}: the follow camera's orbit and zoom (the camera only)
            {
                double? Num(string key) => call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                // {op, value?}: the camera operations the mouse and keys use (unlock, orbit_around_deg, orbit_over_deg, pan_left_m, pan_up_m, wheel, reset, state)
                if (Str(call.Body, "op") is { } op)
                    return m.CameraView(op, Num("value") ?? 0) is { } view ? AutomationReply.Ok(view) : AutomationReply.Error(400, "unknown op");
                return AutomationReply.Ok(m.SetCameraOrbit(Num("yaw_deg"), Num("pitch_deg"), Num("zoom")));
            }
            case "look_probe":   // where the water meets the land, where trees and larger textured objects are, and each texture's loaded level against the level asked for
                return m.LookProbe() is { } looks ? AutomationReply.Ok(looks) : AutomationReply.Error(409, "not in world or no water height");
            case "plain_ground":   // {x?, y?, min_above_water?, clear_radius?}: the bare, level land nearest a point that lies well above the water with no object near it (development builds)
            {
                double Num(string key, double d) => call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
                return m.PlainGroundNear((float)Num("x", 128), (float)Num("y", 128), (float)Num("min_above_water", 1), (float)Num("clear_radius", 3),
                    call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("prefer", out var pref) && pref.ValueKind == JsonValueKind.Array ? pref.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray() : null) is { } plain ? AutomationReply.Ok(plain) : AutomationReply.Error(409, "not in world or no water height");
            }
            case "mesh_probe":   // {x, y, z, radius?, max?}: the mesh objects nearest a point, what each is drawn at and why, and its face textures (development builds)
            {
                double Num(string key, double d) => call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
                return m.MeshProbe((float)Num("x", 0), (float)Num("y", 0), (float)Num("z", 0), (float)Num("radius", 8), (int)Num("max", 12), Num("names", 0) > 0) is { } found ? AutomationReply.Ok(found) : AutomationReply.Error(409, "not in world");
            }
            case "moving_probe":   // {max?}: the objects carried between the region's updates, each as last stated and as drawn now (development builds)
            {
                int most = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("max", out var mv) && mv.ValueKind == JsonValueKind.Number ? mv.GetInt32() : 40;
                return m.MovingProbe(most) is { } carried ? AutomationReply.Ok(carried) : AutomationReply.Error(409, "not in world");
            }
            case "avatar_probe":   // {name?, save?}: each other avatar's bakes, hidden body slots and worn pieces with their face textures, as sent and as drawn (development builds)
            {
                string? name = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() : null;
                string? save = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("save", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() : null;
                return m.AvatarProbe(name, save) is { } probed ? AutomationReply.Ok(probed) : AutomationReply.Error(409, "not in world");
            }
            case "presented_audit":   // {sample?}: the objects the world holds against the objects presented, the reason for each that is not (development builds)
            {
                int sample = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("sample", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : 6;
                return m.PresentedAudit(sample) is { } audit ? AutomationReply.Ok(audit) : AutomationReply.Error(409, "not in world");
            }
            case "neighbours":   // the session's regions and what is drawn for each (answers grid cells, sizes, counts and ids, never names)
                return AutomationReply.Ok(m.NeighboursReport());
            case "terrain":   // each region's terrain texturing: where each detail layer came from, its size, the repeat and the pattern offset
                return AutomationReply.Ok(m.TerrainReport());
            case "neighbour_debug":   // {action: hold_next_login|pretend_not_held|trace_on|trace_off|release, addresses?}: the crossing record, and holding neighbours back from the advance connect of the next login
            {
                string action = Str(call.Body, "action") ?? "";
                if (action == "hold_next_login")
                {
                    // before the login (no session yet): the addresses ("ip:port") the next session does not connect in advance
                    var addresses = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("addresses", out var list) && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Select(a => a.GetString() ?? "").ToArray() : [];
                    if (addresses.Length == 0 || !addresses.All(a => System.Net.IPEndPoint.TryParse(a, out _)))
                        return AutomationReply.Error(400, "addresses must be a list of ip:port");
                    m.HoldsForNextLogin.AddRange(addresses);
                    return AutomationReply.Ok(new { held = addresses.Length });
                }
                if (m.Model?.Session?.NeighbourControl is not { } control)
                    return AutomationReply.Error(409, "not in world");
                switch (action)
                {
                    case "trace_on": control.SetCrossingTrace(true); return AutomationReply.Ok(new { trace = true });
                    case "trace_off": control.SetCrossingTrace(false); return AutomationReply.Ok(new { trace = false });
                    case "agent_trace_on": control.SetAgentUpdateTrace(true); return AutomationReply.Ok(new { agent_trace = true });
                    case "agent_trace_off": control.SetAgentUpdateTrace(false); return AutomationReply.Ok(new { agent_trace = false });
                    case "pretend_not_held":
                    {
                        // {addresses}: the next crossing into each is handled as one into a region the viewer has no circuit to
                        var list = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("addresses", out var many) && many.ValueKind == JsonValueKind.Array
                            ? many.EnumerateArray().Select(a => a.GetString() ?? "").ToArray() : [];
                        return list.Length > 0 && list.All(control.PretendNotHeld) ? AutomationReply.Ok(new { masked = list.Length }) : AutomationReply.Error(400, "addresses must be a list of ip:port");
                    }
                    case "release": control.ReleaseHolds(); return AutomationReply.Ok(new { released = true });
                    default: return AutomationReply.Error(400, "action must be hold_next_login, pretend_not_held, trace_on, trace_off or release");
                }
            }
            case "layers":   // quality layers of the session's textures, own bakes and the map image
                return await m.LayerReport() is { } report ? AutomationReply.Ok(report) : AutomationReply.Error(409, "not in world");
            case "texture_probe":   // {concurrency, to?}: the session's textures straight from the texture capability
            {
                int n = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("concurrency", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1;
                long? to = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null;
                return await m.TextureProbe(n, to) is { } probe ? AutomationReply.Ok(probe) : AutomationReply.Error(409, "not in world or no texture capability");
            }
            case "keys":   // {key, down, shift?, control?}: presses or releases one of the own avatar's movement keys, as a focused keyboard
            {
                string? key = Str(call.Body, "key");
                if (key is null || call.Body.ValueKind != JsonValueKind.Object || !call.Body.TryGetProperty("down", out var dn) || dn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return AutomationReply.Error(400, "key (a string) and down (true or false) are required");
                bool shift = call.Body.TryGetProperty("shift", out var sh) && sh.ValueKind == JsonValueKind.True;
                bool control = call.Body.TryGetProperty("control", out var ct) && ct.ValueKind == JsonValueKind.True;
                return m.AutomationKey(key, dn.GetBoolean(), shift, control) is { } done ? AutomationReply.Ok(done) : AutomationReply.Error(409, "not in world");
            }
            case "mouselook":   // {action: enter|leave|toggle|look|wheel_in|wheel_out|escape|button|state, dx?, dy?, down?}: mouse-look as the mouse and keys do it
            {
                string action = Str(call.Body, "action") ?? "state";
                double? Num(string key) => call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                bool down = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("down", out var dn) && dn.ValueKind == JsonValueKind.True;
                return m.AutomationMouseLook(action, (float)(Num("dx") ?? 0), (float)(Num("dy") ?? 0), down) is { } done ? AutomationReply.Ok(done) : AutomationReply.Error(409, "not in world");
            }
            case "motion":   // {action: start|start_all|take|stop}: the per-frame record of avatars drawn against the region's positions (start_all: every region's)
            {
                string action = Str(call.Body, "action") ?? "take";
                if (action is not ("start" or "start_all" or "take" or "stop")) return AutomationReply.Error(400, "action must be start, start_all, take or stop");
                return AutomationReply.Ok(m.MotionTraceCall(action));
            }
            case "chat":   // {action: say|im|find, text?, kind?, agent?, token?}: nearby chat and conversations (answers counts, never text)
            {
                string action = Str(call.Body, "action") ?? "find";
                if (action is not ("say" or "im" or "find")) return AutomationReply.Error(400, "action must be say, im or find");
                Guid agent = Guid.TryParse(Str(call.Body, "agent"), out var a) ? a : Guid.Empty;
                return m.ChatCall(action, Str(call.Body, "text"), Str(call.Body, "kind"), agent, Str(call.Body, "token")) is { } done
                    ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no session");
            }
            case "dialog":   // {action: find|answer|open, nth?, button?, text?, token?, labels?}: what scripts asked and the notifications waiting (answers ids, kinds, counts and layout; text only compared with a token)
            {
                string action = Str(call.Body, "action") ?? "find";
                if (action is not ("find" or "answer" or "open"))
                    return AutomationReply.Error(400, "action must be find, answer or open");
                string[]? labels = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array
                    ? ls.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : null;
                return m.DialogCall(action, (int)Num(call.Body, "nth", 0), (int)Num(call.Body, "button", 0), Str(call.Body, "text"), Str(call.Body, "token"), labels) is { } done
                    ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no session");
            }
            case "friends":   // {action: list|offer|accept|decline|im|notices|grant_map, agent?}: friends and presence (answers ids, flags and counts, never names or text)
            {
                string action = Str(call.Body, "action") ?? "list";
                if (action is not ("list" or "offer" or "accept" or "decline" or "im" or "notices" or "grant_map"))
                    return AutomationReply.Error(400, "action must be list, offer, accept, decline, im, notices or grant_map");
                Guid agent = Guid.TryParse(Str(call.Body, "agent"), out var a) ? a : Guid.Empty;
                return m.FriendsCall(action, agent) is { } done ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no session");
            }
            case "groups":   // {action: list|join|say|find, group?, text?, token?}: groups, group chat and notices (answers ids and counts, never names or text)
            {
                string action = Str(call.Body, "action") ?? "list";
                if (action is not ("list" or "join" or "say" or "find"))
                    return AutomationReply.Error(400, "action must be list, join, say or find");
                Guid group = Guid.TryParse(Str(call.Body, "group"), out var g) ? g : Guid.Empty;
                return m.GroupsCall(action, group, Str(call.Body, "text"), Str(call.Body, "token")) is { } done ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no session");
            }
            case "teleport":   // {action: status|region|home|set_home|cancel|offer|ask|accept|decline|offer_back|refuse, region?, x?, y?, z?, agent?}: teleport (answers keys, ids and flags, never names or text)
            {
                string action = Str(call.Body, "action") ?? "status";
                if (action is not ("status" or "region" or "home" or "set_home" or "cancel" or "offer" or "ask" or "accept" or "decline" or "offer_back" or "refuse"))
                    return AutomationReply.Error(400, "action must be status, region, home, set_home, cancel, offer, ask, accept, decline, offer_back or refuse");
                Guid agent = Guid.TryParse(Str(call.Body, "agent"), out var a) ? a : Guid.Empty;
                return m.TeleportCall(action, Str(call.Body, "region"), Num(call.Body, "x", 128), Num(call.Body, "y", 128), Num(call.Body, "z", 30), agent, Num(call.Body, "grid_x", -1), Num(call.Body, "grid_y", -1)) is { } done
                    ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no session");
            }
            case "interact":   // {action: state|aim|hover|click|rightclick|choose|stand|press_stop_flying|sit_ground|scan|press_copy|press_buy, id?, x?, y?, z?, entry?, expect?, distance?}: clicking things in the world (answers ids, flags, keys and counts; a name is only compared with expect)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("state" or "aim" or "hover" or "click" or "rightclick" or "choose" or "stand" or "press_stop_flying" or "sit_ground" or "scan" or "press_copy" or "press_buy"))
                    return AutomationReply.Error(400, "action must be state, aim, hover, click, rightclick, choose, stand, press_stop_flying, sit_ground, scan, press_copy or press_buy");
                Guid target = Guid.TryParse(Str(call.Body, "id"), out var tg) ? tg : Guid.Empty;
                return m.InteractCall(action, target, Num(call.Body, "x", 128), Num(call.Body, "y", 128), Num(call.Body, "z", 30), Str(call.Body, "entry"), Str(call.Body, "expect"), Num(call.Body, "distance", 0)) is { } done
                    ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no session");
            }
            case "edit":   // {action: state|toggle|tool|click|select|linked|position|size|rotation|shape|name|flags|locked|link|unlink|drag, x?, y?, z?, text?, expect?}: build mode's edit tools (answers ids, numbers and states)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("state" or "toggle" or "tool" or "click" or "select" or "linked" or "position" or "size" or "rotation" or "shape" or "name" or "flags" or "locked" or "link" or "unlink" or "handle" or "manip" or "snap" or "snap_xy" or "use_selection_grid" or "grid" or "grid_options" or "handle_points" or "undo" or "control" or "screen" or "picker" or "picker_row" or "material"))
                    return AutomationReply.Error(400, "action must be state, toggle, tool, click, select, linked, position, size, rotation, shape, name, flags, locked, link, unlink, handle, manip, snap, snap_xy, use_selection_grid, grid, grid_options, handle_points, undo, control, screen, picker, picker_row or material");
                return m.EditCall(action, Num(call.Body, "x", 128), Num(call.Body, "y", 128), Num(call.Body, "z", 30), Str(call.Body, "text"), Str(call.Body, "expect")) is { } edited
                    ? AutomationReply.Ok(edited) : AutomationReply.Error(409, "not in the world");
            }
            case "build":   // {action: state|create|rez|upload_image|upload_bulk|press_upload|upload_model|upload_bulk_models|press_model_upload|take_back, x?, y?, z?, id?, text?, expect?}: the Build menu's tools (answers ids, counts, sizes and states; a name only through expect; for the model windows expect carries their settings)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("state" or "create" or "rez" or "upload_image" or "upload_bulk" or "press_upload" or "upload_model" or "upload_bulk_models" or "press_model_upload" or "take_back"))
                    return AutomationReply.Error(400, "action must be state, create, rez, upload_image, upload_bulk, press_upload, upload_model, upload_bulk_models, press_model_upload or take_back");
                return m.BuildCall(action, Guid.TryParse(Str(call.Body, "id"), out var bid) ? bid : Guid.Empty, Num(call.Body, "x", 128), Num(call.Body, "y", 128), Num(call.Body, "z", 30),
                    Str(call.Body, "text"), Str(call.Body, "expect")) is { } built ? AutomationReply.Ok(built) : AutomationReply.Error(409, "no session");
            }
            case "aim":   // {op: aim|state|release, target: avatar|object, id?, name?, local?, point: head|chest|body|centre, distance, around_deg, above_deg, fov_deg, min_fill?, max_offset?}: puts the camera at a distance and angles from a person or an object and holds it; answers the pose, the target's box on the screen and the framing check
            {
                if (!AimRequest.TryParse(call.Body, out var aim, out var aimError))
                    return AutomationReply.Error(400, aimError);
                return m.AimCall(aim) is { } aimed ? AutomationReply.Ok(aimed) : AutomationReply.Error(409, "no session");
            }
            case "map":   // {action: state|open|search|choose|centre|click|teleport|friend, name?, agent?}: the world map and mini-map (answers counts and ids, never names)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("state" or "open" or "search" or "choose" or "centre" or "click" or "teleport" or "friend" or "neighbour"))
                    return AutomationReply.Error(400, "action must be state, open, search, choose, centre, click, teleport, friend or neighbour");
                Guid agent = Guid.TryParse(Str(call.Body, "agent"), out var a) ? a : Guid.Empty;
                return m.MapCall(action, Str(call.Body, "name"), agent) is { } done ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no session");
            }
            case "inventory":   // {action: status|open|search|sort, kind?, text?, sort?}: the inventory, read only (answers counts and states, never a name)
            {
                string action = Str(call.Body, "action") ?? "status";
                string[] outfitActions = ["outfit", "refresh", "wear", "add", "takeoff", "attach", "detach", "wearoutfit", "saveoutfit", "dropbase"];
                if (outfitActions.Contains(action))   // reading is always allowed; changing what is worn needs a scratch account (NV_OUTFIT_SCRATCH_OK)
                    return m.OutfitCall(action, Str(call.Body, "text"), int.TryParse(Str(call.Body, "point"), out var point) ? point : 0, Guid.TryParse(Str(call.Body, "id"), out var pick) ? pick : Guid.Empty) is { } outfit
                        ? AutomationReply.Ok(outfit) : AutomationReply.Error(409, "no session");
                string[] appearanceActions = ["appearance", "ap_info", "ap_open", "ap_set", "ap_colour", "ap_texture", "ap_prepare", "ap_save", "ap_saveas", "ap_revert", "ap_close", "ap_discard"];
                if (appearanceActions.Contains(action))   // reading is always allowed; editing and saving a wearable needs a scratch account (NV_OUTFIT_SCRATCH_OK)
                    return m.AppearanceCall(action, Str(call.Body, "text"), Guid.TryParse(Str(call.Body, "id"), out var wearable) ? wearable : Guid.Empty,
                        Num(call.Body, "a", 0), Num(call.Body, "b", 0), Num(call.Body, "c", 0), (int)Num(call.Body, "param", 0)) is { } appearance
                        ? AutomationReply.Ok(appearance) : AutomationReply.Error(409, "no session");
                if (action is not ("status" or "open" or "search" or "sort")) return AutomationReply.Error(400, "action must be status, open, search, sort, outfit, wear, add, takeoff, attach, wearoutfit or saveoutfit");
                return m.InventoryCall(action, Str(call.Body, "kind"), Str(call.Body, "text"), Str(call.Body, "sort"), Str(call.Body, "tab")) is { } done
                    ? AutomationReply.Ok(done) : AutomationReply.Error(409, "no session");
            }
            case "notecard":   // {action: state|list|open|fixture|copy|links|link|chat_link|render, id?, text?, index?}; links/link: the front window's links, click one; chat_link: click a link in nearby chat; render: a text as every chat place and a card show it: notecard windows (live cards by counts and kinds, never their names or text; a fixture file's text is given)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("state" or "list" or "open" or "fixture" or "copy" or "links" or "link" or "chat_link" or "render"))
                    return AutomationReply.Error(400, "action must be state, list, open, fixture, copy, links, link, chat_link or render");
                return m.NotecardCall(action, Str(call.Body, "text"), Guid.TryParse(Str(call.Body, "id"), out var card) ? card : Guid.Empty, (int)Num(call.Body, "index", 0)) is { } notecard
                    ? AutomationReply.Ok(notecard) : AutomationReply.Error(409, "no session");
            }
            case "addons":   // {action: list|on|off|open|rlv, id?}: the add-ons, their state and their windows; on grants an add-on's permissions as the user's confirmation does; rlv answers RLV's state (held restrictions, locked items, counts)
            {
                string action = Str(call.Body, "action") ?? "list";
                if (action is not ("list" or "on" or "off" or "open" or "rlv")) return AutomationReply.Error(400, "action must be list, on, off, open or rlv");
                return m.AddonsCall(action, Str(call.Body, "id")) is { } addons ? AutomationReply.Ok(addons) : AutomationReply.Error(409, "no add-on host");
            }
            case "overrider":   // {action: state|on|off|next|previous|reload|set|make_set|animations|drop|review|review_add|review_cancel|newset, text?, seconds?}: the animation overrider (answers ids, counts and state names, never a set's name; make_set lays out a small set in the agent's own inventory; animations lists animation ids of the own inventory; drop drops ids on the set editor, "<zone>|<ids>" on one of its zones; newset makes a set named text; newset_ask presses New set with no name typed, newset_make types the asked-for name and presses Make set)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("state" or "on" or "off" or "next" or "previous" or "reload" or "set" or "make_set" or "animations" or "drop" or "review" or "review_add" or "review_cancel" or "newset" or "newset_ask" or "newset_make" or "prompt_cancel"))
                    return AutomationReply.Error(400, "action must be state, on, off, next, previous, reload, set, make_set, animations, drop, review, review_add, review_cancel, newset, newset_ask, newset_make or prompt_cancel");
                if (action == "make_set")
                    return Str(call.Body, "text") is { Length: > 0 } setName && await m.OverriderMakeSet(setName, Num(call.Body, "seconds", 10)) is { } made
                        ? AutomationReply.Ok(made) : AutomationReply.Error(409, "no session, or no set name");
                return m.OverriderCall(action, Str(call.Body, "text")) is { } overrider ? AutomationReply.Ok(overrider) : AutomationReply.Error(409, "no session");
            }
            case "expression":   // {action: play|stop|trace, name?}: asks the region to start or stop an expression animation (express_smile and the like) on the own avatar, or reads the face's weight trace (play starts a new one)
            {
                string action = Str(call.Body, "action") ?? "trace";
                if (action is not ("play" or "stop" or "trace")) return AutomationReply.Error(400, "action must be play, stop or trace");
                return m.ExpressionCall(action, Str(call.Body, "name")) is { } expr ? AutomationReply.Ok(expr) : AutomationReply.Error(409, "no session, no drawn body, or not an expression animation");
            }
            case "ui_sounds":   // {action: log|clear|play|state|find|close_window, slot?, name?}: the interface sounds' decisions, a slot played as its Play button does, where a control is, a window closed (development builds only)
                return m.UiSoundsCall(Str(call.Body, "action") ?? "log", Str(call.Body, "slot"), Str(call.Body, "name")) is { } snd ? AutomationReply.Ok(snd) : AutomationReply.Error(409, "no sounds");
            case "world_sounds":   // {action: state|clear|feed, events?, objects?, listener?}: sounds in the world: each source, where it plays and at what gain, the decisions; feed (without a session) puts sound events into a service made for the check (development builds only)
                return m.WorldSoundsCall(Str(call.Body, "action") ?? "state", call.Body) is { } ws ? AutomationReply.Ok(ws) : AutomationReply.Error(409, "no sounds");
            case "sound_tuner":   // {action: state|open|choose|set|save|reset, slot?, key?, value?}: the Sound Tuner: the sound shown, its numbers, which sounds are tuned; a slider moved as a drag moves it (development builds only)
                return m.SoundTunerCall(Str(call.Body, "action") ?? "state", Str(call.Body, "slot"), Str(call.Body, "key"), Num(call.Body, "value", float.NaN)) is { } st ? AutomationReply.Ok(st) : AutomationReply.Error(409, "no tuner");
            case "windows":   // {action: state|open|move|resize|close, name?, x?, y?, w?, h?}: the open windows' ids and rectangles; a window opened, moved, resized or closed as the user does it (development builds only)
                return m.WindowsCall(Str(call.Body, "action") ?? "state", Str(call.Body, "name"), Num(call.Body, "x", 0), Num(call.Body, "y", 0), Num(call.Body, "w", 0), Num(call.Body, "h", 0)) is { } windows
                    ? AutomationReply.Ok(windows) : AutomationReply.Error(409, "no shell");
            case "inventory_view":   // {action: state|sort|suitcase|pointer|pointer_live|move_pick|move_drop|move_where, sort?, item?, folder?}: the inventory window's sort as shown and kept, a sort chosen as a click does; suitcase: a made-up suitcase folder opened and closed while it is read again and again (before a login only) (development builds only)
                return await m.InventoryViewCall(Str(call.Body, "action") ?? "state", Str(call.Body, "sort"), Str(call.Body, "item"), Str(call.Body, "folder")) is { } invView ? AutomationReply.Ok(invView) : AutomationReply.Error(409, "no shell");
            case "open_window":   // {name?}: opens a window of the shell by its id or title (Preferences included); without a name, lists the ids
                return m.OpenWindowCall(Str(call.Body, "name")) is { } opened ? AutomationReply.Ok(opened) : AutomationReply.Error(409, "no shell");
            case "sky":   // {choice: sunrise|midday|sunset|midnight|region}: the time-of-day choice, as the sky key sets it
            {
                string? choice = Str(call.Body, "choice");
                return choice is not null && await m.AutomationSky(choice) is { } set ? AutomationReply.Ok(set) : AutomationReply.Error(400, "choice must be sunrise, midday, legacy noon, sunset, midnight or region");
            }
            case "personal":   // {field: HazeDensity|CloudCoverage|SceneGamma|..., value?: number, colour?: [r, g, b], reset?: true}: the Personal Lighting window's settings, as the window sets them
                return m.AutomationPersonal(call.Body) is { } personal ? AutomationReply.Ok(personal) : AutomationReply.Error(400, "field must be a Personal Lighting control with a value or a colour of three numbers, or reset must be true");
            case "sample":   // the process, memory-collector, engine, native-heap and pipeline figures of a long run (forces two full collections)
            {
                if (Str(call.Body, "reset_slices") == "1") FrameMeter.ResetLongest();   // the longest slice starts again; a light reading follows
                return AutomationReply.Ok(Str(call.Body, "light") == "1" ? LiveSample.Light(m) : LiveSample.Take());
            }
            case "input":   // {kind: key|mouse|state, key?, button?: left|right|middle (none: a move), down?, shift?, ctrl?, alt?, x?, y?}: a key or mouse event through the engine's input path (development builds only)
            {
                bool Flag(string key) => call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty(key, out var fv) && fv.ValueKind == JsonValueKind.True;
                float At(string key) => call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty(key, out var nv) && nv.ValueKind == JsonValueKind.Number ? (float)nv.GetDouble() : 0f;
                return m.InputCall(Str(call.Body, "kind") ?? "state", Str(call.Body, "key"), Str(call.Body, "button"), Flag("down"), Flag("shift"), Flag("ctrl"), Flag("alt"), At("x"), At("y")) is { } sent
                    ? AutomationReply.Ok(sent) : AutomationReply.Error(400, "kind must be key (with a key name), mouse or state");
            }
            case "typing":   // {action: focus|release|text|key|state, text?, caret?, key?, down?}: the chat bar's text field and an arrow key sent as a pressed key (development builds only)
            {
                string action = Str(call.Body, "action") ?? "state";
                if (action is not ("focus" or "release" or "text" or "key" or "state"))
                    return AutomationReply.Error(400, "action must be focus, release, text, key or state");
                int caret = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("caret", out var cv) && cv.ValueKind == JsonValueKind.Number && cv.TryGetInt32(out var ci) ? ci : -1;
                bool down = call.Body.ValueKind == JsonValueKind.Object && call.Body.TryGetProperty("down", out var dv) && dv.ValueKind == JsonValueKind.True;
                return m.TypingCall(action, Str(call.Body, "text"), caret, Str(call.Body, "key"), down) is { } typing ? AutomationReply.Ok(typing) : AutomationReply.Error(409, "no shell");
            }
            case "engine":   // the renderer that is running, the drivers an earlier start of this run tried, the frames drawn so far and the window (development builds only)
                return AutomationReply.Ok(new
                {
                    rendering_driver = global::Godot.RenderingServer.GetCurrentRenderingDriverName(),
                    api_version = global::Godot.RenderingServer.GetVideoAdapterApiVersion(),
                    backend_tried = Viewer.Shell.BackendRelaunch.Tried(global::Godot.OS.GetCmdlineUserArgs()),
                    frames = (long)global::Godot.Engine.GetFramesDrawn(),
                    process = System.Environment.ProcessId,
                    debug_build = global::Godot.OS.IsDebugBuild(),
                });
            case "quit":
                _quitIn = 10;   // a few frames, so this reply is written before the process exits
                return AutomationReply.Ok(new { quitting = true });
            default:
                return AutomationReply.Error(404, "command");
        }
    }

    private static async Task<AutomationReply> Session(Main m, string action, JsonElement body)
    {
        var model = m.Model;
        if (model is null)
            return AutomationReply.Error(409, "no login model");
        switch (action)
        {
            case "state":
                return AutomationReply.Ok(m.SessionState());
            case "login":
            {
                if (model.Busy || model.Session is { IsEnded: false })
                    return AutomationReply.Error(409, "a session is active");
                string? accounts = Str(body, "accounts_file"), alias = Str(body, "account"), uri = Str(body, "login_uri");
                if (accounts is null || alias is null || uri is null || !Path.IsPathFullyQualified(accounts))
                    return AutomationReply.Error(400, "accounts_file (absolute), account and login_uri are required");
                (string first, string last, Secret password) acct;
                try { acct = AccountsFile.Load(accounts, alias); }
                catch (Exception) { return AutomationReply.Error(400, "account not available"); }
                model.LoginUri = uri;
                model.UserName = acct.first + " " + acct.last;   // the whole name in the one username field, split as typed text is
                model.StartLocation = Str(body, "start") ?? "last";
                model.SetPassword(acct.password);
                _ = model.LoginAsync();
                return AutomationReply.Ok(new { started = true });
            }
            case "logout":
            {
                var s = model.Session;
                if (s is null)
                    return AutomationReply.Error(409, "no session");
                m.LetGoOfSelections();   // on the main thread (calls are answered from _Process)
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await s.LogoutAsync(cts.Token);
                return AutomationReply.Ok(m.SessionState());
            }
            default:
                return AutomationReply.Error(400, "action must be state, login or logout");
        }
    }

    private static string? Str(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static float Num(JsonElement body, string name, float fallback) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    public override void _ExitTree()
    {
        _server?.Dispose();
        if (_tokenPath is not null)
            TokenFile.Remove(_tokenPath);
    }
}
#endif
