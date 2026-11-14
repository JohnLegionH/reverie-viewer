// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using System;
using System.Linq;
using Godot;
using Viewer.Automation;
using Viewer.Core.Camera;
using Viewer.Core.Interaction;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The aim command (development builds only): puts the camera at a given distance and angles from an avatar or an object, holds it
/// there until it is released, and reports the camera pose and where the target lands on the screen, with the verdict of the framing
/// check (the target fills enough of the frame and sits in the middle). The arithmetic is <see cref="CameraAim"/>'s; this finds the
/// target and applies the pose through the same camera path the seat camera uses.
/// </summary>
public partial class Main
{
    private AimRequest? _aimRequest;
    private (System.Numerics.Vector3 Centre, System.Numerics.Vector3 Half, float Yaw)? _aimTarget;
    private float _aimSavedFov = -1f;

    /// <summary>Answers an aim request: ids, numbers and flags, never a name. Null when there is no session.</summary>
    public object? AimCall(AimRequest r)
    {
        if (_camera is null || _interactFor is not { } s)
            return null;
        if (r.Op == "release")
        {
            _aim = null;
            _aimRequest = null;
            _aimTarget = null;
            if (_aimSavedFov > 0f) { _camera.Fov = _aimSavedFov; _aimSavedFov = -1f; }
            return new { aimed = false };
        }
        if (r.Op == "state")
            return _aimRequest is null || _aimTarget is null ? new { aimed = false } : AimReport(_aimRequest, _aimTarget.Value);
        if (OwnAvatars is not { } avatars)
            return null;

        System.Numerics.Vector3 centre, half;
        float yaw;
        Guid id;
        if (r.Target == "avatar")
        {
            var entry = avatars.Scene.Entries.FirstOrDefault(e => (r.Id != Guid.Empty && e.AgentId == r.Id)
                || (!string.IsNullOrEmpty(r.Name) && string.Equals(e.Name, r.Name, StringComparison.OrdinalIgnoreCase)));
            if (entry is null || !avatars.Scene.Place(entry, out var soles, out float placedYaw))
                return new { error = "avatar_not_found", avatars = avatars.Scene.Entries.Count };
            float height = entry.Size.Z > 0.1f ? entry.Size.Z : AvatarScene.DefaultHeight;
            (centre, half) = CameraAim.AvatarBox(r.Point, soles, height);
            yaw = placedYaw;
            id = entry.AgentId;
        }
        else
        {
            var o = s.World.Snapshot().Objects.Values.FirstOrDefault(v => (r.Local != 0 && v.Id.LocalId == r.Local) || (r.Id != Guid.Empty && v.FullId == r.Id));
            if (o is null || !ObjectPicker.WorldPose(s.World, o, out var pos, out var rot))
                return new { error = "object_not_found" };
            (centre, half, yaw, id) = (pos, o.Scale / 2, CameraAim.YawOf(rot), o.FullId);
        }
        var held = r with { Id = id };
        var pose = CameraAim.Pose(centre, r.Distance, r.AroundDeg, r.AboveDeg, yaw, r.FovDeg);
        if (_aimSavedFov < 0f) _aimSavedFov = _camera.Fov;
        _camera.Fov = r.FovDeg;
        _aim = (pose.Eye, pose.Target);
        _aimRequest = held;
        _aimTarget = (centre, half, yaw);
        return AimReport(held, _aimTarget.Value);
    }

    private object AimReport(AimRequest r, (System.Numerics.Vector3 Centre, System.Numerics.Vector3 Half, float Yaw) t)
    {
        // the frame is the picture's size in pixels (the window's; a screenshot saves the viewport at that size). The engine's own projection is in the
        // viewport's visible rectangle, which a content scale can make smaller, so its answer is scaled up to the picture
        var window = GetWindow().Size;   // the picture is the window's size in pixels
        var size = new Vector2(window.X, window.Y);
        var visible = GetViewport().GetVisibleRect().Size;
        var toPicture = visible.X > 0 && visible.Y > 0 ? new Vector2(size.X / visible.X, size.Y / visible.Y) : Vector2.One;
        var pose = CameraAim.Pose(t.Centre, r.Distance, r.AroundDeg, r.AboveDeg, t.Yaw, r.FovDeg);
        var box = CameraAim.BoxOf(pose, size.X, size.Y, CameraAim.Corners(t.Centre, t.Half, t.Yaw));
        var check = CameraAim.Check(box, size.X, size.Y, r.MinFill, r.MaxOffset);
        // what the engine's own camera makes of it: whether the pose has been applied (it is, from the next frame on) and where it puts the target
        var engineEye = new Vector3(pose.Eye.X, pose.Eye.Z, -pose.Eye.Y);
        bool applied = _camera!.GlobalPosition.DistanceTo(engineEye) < 0.05f && Math.Abs(_camera.Fov - r.FovDeg) < 0.01f;
        var engineAt = new Vector3(t.Centre.X, t.Centre.Z, -t.Centre.Y);
        Vector2? enginePx = _camera.IsPositionBehind(engineAt) ? null : _camera.UnprojectPosition(engineAt);
        return new
        {
            aimed = true, target = r.Target, id = r.Id, point = r.Point.ToString(),
            eye = new[] { pose.Eye.X, pose.Eye.Y, pose.Eye.Z }, look = new[] { pose.Target.X, pose.Target.Y, pose.Target.Z }, fov_deg = pose.FovDeg,
            frame = new[] { size.X, size.Y },
            box = new { left = box.Left, top = box.Top, right = box.Right, bottom = box.Bottom, in_front = box.InFront },
            check = new { pass = check.Pass, fill = check.Fill, visible = check.VisibleShare, centre_offset = check.CentreOffset, reasons = check.Reasons, min_fill = r.MinFill, max_offset = r.MaxOffset },
            engine = new { applied, centre = enginePx is { } p ? new[] { p.X * toPicture.X, p.Y * toPicture.Y } : null },
            // the part of the picture the interface bars leave (picture pixels): a target under a bar is not on the screen
            main_area = _shell is { MainArea: { Size.X: > 0, Size.Y: > 0 } m } ? new { left = m.Position.X * toPicture.X, top = m.Position.Y * toPicture.Y, right = m.End.X * toPicture.X, bottom = m.End.Y * toPicture.Y } : null,
        };
    }
}
#endif
