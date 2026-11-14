// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;

namespace Viewer.Godot;

public partial class Main
{
    /// <summary>The file the own avatar's dump is written to, next to the session log.</summary>
    public const string LoginDumpName = "viewer.avatar.json";

    /// <summary>The dump keeps being refreshed (when something in it changed) for this long after the body is first drawn, so a mesh that arrives late is in it.</summary>
    private const double LoginDumpWindowSeconds = 120;

    private object? _dumpSession;
    private double _dumpNextAt, _dumpStarted = -1;
    private string? _dumpLast;
    private static Task<AvatarLoginDump.FolderFingerprint?>? s_characterFingerprint;
    private static readonly JsonSerializerOptions DumpJson = new() { WriteIndented = true };

    /// <summary>
    /// At each login, once the user's own avatar is drawn, writes what it is drawn with, next to the session log (<see cref="LoginDumpName"/>): the visual-parameter
    /// weights, the face joints and the morph fingerprints, the appearance the region sent, the rigged meshes worn and the joints they move, the character folder
    /// in use with a hash of its files, the window, the 3D view and the camera's projection. No picture. The readings are taken on the main thread (a few
    /// microseconds of reading the avatar), the folder is hashed and the file written on another thread, and the file is rewritten (only when its content changed)
    /// every two seconds for <see cref="LoginDumpWindowSeconds"/> seconds after the body first shows, so late worn meshes and the folder's hash are in it.
    /// </summary>
    private void LoginDumpTick()
    {
        try { WriteLoginDump(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dumpNextAt = double.MaxValue;   // a development aid never costs a frame twice
            Log.Warn("avatars.login_dump_failed", CoreLog.F("error", ex.GetType().Name));
        }
    }

    private void WriteLoginDump()
    {
        if (_sessionLog?.Path is not { } logPath || _presented is null || OwnAvatars is not { } avatars || avatars.Scene.Self is not { } self)
            return;
        if (!ReferenceEquals(_dumpSession, _presented))
        {
            _dumpSession = _presented;
            _dumpStarted = -1;
            _dumpLast = null;
            _dumpNextAt = 0;
        }
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _dumpNextAt)
            return;
        var shape = _bodyHost?.ShapeDump(self.AgentId);
        if (shape is null)
        {
            _dumpNextAt = now + 1;   // no body drawn yet
            return;
        }
        if (_dumpStarted < 0)
            _dumpStarted = now;
        _dumpNextAt = now - _dumpStarted > LoginDumpWindowSeconds ? double.MaxValue : now + 2;

        var choice = CharacterChoice();
        string? folder = choice.Path;
        var fingerprintTask = s_characterFingerprint ??= Task.Run(() => AvatarLoginDump.Fingerprint(folder));
        var fingerprint = fingerprintTask.IsCompletedSuccessfully ? fingerprintTask.Result : null;
        var window = GetWindow();
        var viewport = GetViewport();
        var picture = new Vector2(window.Size.X, window.Size.Y) * viewport.Scaling3DScale;   // the 3D buffer: the window's pixels times the 3D scale (the root viewport has no content-scale mode)
        var projection = _camera?.GetCameraProjection();
        var dump = new
        {
            note = "The user's own avatar as drawn: the AV-FACE shape dump, what is worn, the character folder, the window, the 3D view and the camera.",
            shape_dump = shape,
            appearance = AppearanceReport(self),
            worn = _bodyHost?.WornDump(self.AgentId),
            wearables = AvatarLoginDump.Wearables(Viewer.Core.Inventory.OutfitPlanner.Current(_presented.Inventory))
                .Select(w => new { type = w.Type, layer = w.Layer, item = w.Item, name = w.Name, alpha = w.Alpha, body_part = w.BodyPart }).ToList(),   // the current outfit's wearables as this viewer holds it (empty until the folder has loaded)
            character_folder = new
            {
                given = folder is not null && Directory.Exists(folder),
                source = choice.Source.ToString().ToLowerInvariant(),   // argument, environment, shipped or none
                path = folder,
                files = fingerprint?.Files,
                bytes = fingerprint?.Bytes,
                hash_sha256 = fingerprint?.Hash,
                file_hashes = fingerprint?.FileHashes,
            },
            window = new
            {
                size = new[] { window.Size.X, window.Size.Y },
                mode = window.Mode.ToString(),
                content_scale_factor = window.ContentScaleFactor,
                content_scale_mode = window.ContentScaleMode.ToString(),
                visible_rect_2d = new[] { viewport.GetVisibleRect().Size.X, viewport.GetVisibleRect().Size.Y },
                screen_dpi = DisplayServer.ScreenGetDpi(window.CurrentScreen),
                screen_size = new[] { DisplayServer.ScreenGetSize(window.CurrentScreen).X, DisplayServer.ScreenGetSize(window.CurrentScreen).Y },
            },
            view_3d = new { size = new[] { (int)Math.Round(picture.X), (int)Math.Round(picture.Y) }, scaling_3d = viewport.Scaling3DScale, aspect = picture.Y > 0 ? picture.X / picture.Y : 0f },
            camera = _camera is null || projection is not { } p ? null : new
            {
                vertical_fov_degrees = 2 * Math.Atan(1.0 / p.Y.Y) * 180 / Math.PI,
                projection_aspect = p.Y.Y / p.X.X,
                keep_aspect = _camera.KeepAspect.ToString(),
                configured_fov_degrees = _camera.Fov,
            },
        };
        string json = JsonSerializer.Serialize(dump, DumpJson);
        if (json == _dumpLast)
            return;
        string first = _dumpLast is null ? "first" : "refreshed";
        _dumpLast = json;
        string path = Path.Combine(Path.GetDirectoryName(logPath) ?? "", LoginDumpName);
        Log.Info("avatars.login_dump", CoreLog.F("file", LoginDumpName), CoreLog.F("write", first), CoreLog.F("character_hash_ready", fingerprint is not null));
        _ = Task.Run(() =>
        {
            try { File.WriteAllText(path, json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the dump is a development aid: a folder that cannot be written costs nothing else */ }
        });
    }
}
