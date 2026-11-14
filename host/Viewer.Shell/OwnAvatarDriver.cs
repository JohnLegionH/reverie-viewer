// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// Moves the user's own avatar from the keyboard, once a frame: the controls (<see cref="Input"/>) turn the heading and
/// make the control bits; the heading is drawn at once (<see cref="AvatarScene.SelfHeading"/>), the position stays the
/// region's (smoothed by the scene); the bits, the body rotation and the camera go to the region when
/// <see cref="Rule"/> says so. The heading starts as the region's, and is taken from the region again after a region
/// change or a sit or a stand; whether the avatar flies is taken from the region's first animation list (a login in
/// the air). When the movement keys are released their bits are simply no longer sent and the drawn avatar goes on being
/// drawn from the region's updates, so it comes to rest as the region stops it (as the official viewer does). Nothing is
/// sent before the own avatar is placed. No engine type; main thread only.
/// </summary>
public sealed class OwnAvatarDriver
{
    private AvatarScene _scene;
    private WorldState _world;
    private readonly Func<AgentUpdate, bool> _send;
    private ObjectId _for;
    private uint _seat;
    private uint _lastSeat;

    /// <param name="send">Sends one update to the region; false when it could not.</param>
    /// <param name="sendRun">Tells the region whether the avatar runs (the reliable SetAlwaysRun message); false when it could not.</param>
    public OwnAvatarDriver(AvatarScene scene, WorldState world, Func<AgentUpdate, bool> send, Func<bool, bool>? sendRun = null)
    {
        _scene = scene;
        _world = world;
        _send = send;
        if (sendRun is not null) Input.WalkRunChanged += run => sendRun(run);
    }

    public AgentControlInput Input { get; } = new();
    public AgentUpdateRule Rule { get; } = new();

    /// <summary>How far the frame the camera is given in lies from the region's own (metres; zero when they are the same). The camera
    /// the region is told about is its own-frame position: the given eye and target less this.</summary>
    public Vector3 CameraShift { get; set; }

    /// <summary>Moves the driver to another region's scene and world (the agent has crossed into it). The keys held, the run state, the
    /// update rule and the count of updates sent carry on. With <paramref name="keepHeading"/> (a crossing) the heading carries on too: it is
    /// the agent's own, as in the official viewer, which keeps the agent's rotation across a crossing (<c>llagent.cpp</c>,
    /// <c>LLAgent::setRegion</c>); the new region's copy of the avatar is not asked, because it can be a stale one from an earlier visit, and
    /// adopting it turned the avatar round at the border. Without it (a teleport) the heading is taken from the region on the next frame.</summary>
    public void Rebind(AvatarScene scene, WorldState world, bool keepHeading = false)
    {
        // a crossing: until the region entered states the avatar, the region left's copy of it stands in (the official agent is one object that is
        // updated every frame, llagent.cpp sendAgentUpdate); a teleport waits for the new region's avatar
        _came = keepHeading && _scene.Self is not null ? (_scene, _world) : null;
        _scene = scene;
        _world = world;
        _carryHeading = keepHeading;   // the new region's avatar is followed again from its first frame, with or without taking its heading
        // The own avatar's body faces the agent's direction, never a region's copy of it (llvoavatar.cpp, LLVOAvatar::updateOrientation, own-avatar
        // branch: the base direction is the agent's at-axis). A region entered again holds an old copy facing the other way, and a frame drawn
        // before this driver's next frame would show it; so the agent's heading is on the new scene from the moment of the crossing.
        if (keepHeading && Input.Heading is float carried) scene.SelfHeading = carried;
        if (!keepHeading)
        {
            _for = default;   // the heading is adopted again from the avatar the new region states
            _seat = 0;
        }
    }

    private bool _carryHeading;
    private (AvatarScene Scene, WorldState World)? _came;

    /// <summary>The render state and flag bytes to send with each update (<see cref="AgentRenderStates"/>), read each frame; null: both zero.</summary>
    public Func<(byte State, byte Flags)>? StateSource { get; set; }

    /// <summary>Updates the region accepted for sending.</summary>
    public int Sent { get; private set; }

    /// <summary>The last update sent (null before the first).</summary>
    public AgentUpdate? Last { get; private set; }

    /// <summary>One frame at <paramref name="now"/>, <paramref name="dt"/> seconds after the last; the camera's eye and
    /// the point it looks at are the follow camera's (region metres).</summary>
    public void Frame(double now, double dt, Vector3 eye, Vector3 target)
    {
        var (scene, world) = (_scene, _world);
        if (scene.Self is null && _came is { } came && came.Scene.Self is not null) (scene, world) = came;
        else if (scene.Self is not null) _came = null;
        // standing in with the region left's copy after it has let the avatar go (the region entered has stated it, and its scene takes that later in
        // the frame): the statement is the region entered's, so no frame of the crossing goes without an update
        if (scene.Self is not { } self
            || (world.Avatar(self.Id) ?? (ReferenceEquals(scene, _scene) ? null : _world.AvatarOf(self.AgentId))) is not { } stated)
        {
            // in a crossing the agent's heading stays on the scene (see Rebind); otherwise the region's own rotation is drawn
            _scene.SelfHeading = _carryHeading ? Input.Heading : null;
            return;
        }
        if (Input.Heading is not null && _carryHeading)
        {
            _for = self.Id;   // the same agent in the region it has crossed into: its heading stays
            _seat = self.Seat;
        }
        else if (Input.Heading is null || self.Id != _for || self.Seat != _seat)
        {
            Input.AdoptHeading(AvatarMotion.Yaw(stated.Rotation));
            _for = self.Id;
            _seat = self.Seat;
        }
        _carryHeading = false;
        // Whether the agent flies is the viewer's own state, never read from what the region plays: it starts from the saved state of the last
        // session's end (the host sets it; llagent.cpp, LLAgent::init, FlyingAtExit), and sitting down turns it off (llviewermessage.cpp,
        // process_avatar_sit_response). A region that plays a hover for a moment while it settles an avatar placed on a prim at login must not
        // leave the fly bit set.
        if (self.Seat != 0 && _lastSeat == 0 && Input.Flying)
            Input.AdoptFlying(false);
        _lastSeat = self.Seat;
        float groundSpeed = new Vector2(stated.Velocity.X, stated.Velocity.Y).Length();
        uint controls = Input.Frame(now, dt, AgentAnimations.InAir(self.Playing), groundSpeed);
        float heading = Input.Heading!.Value;
        _scene.SelfHeading = heading;
        scene.SelfHeading = heading;   // the region left's copy, while it stands in
        var body = Input.MouseLook ? AgentControlInput.BodyRotation(heading, Input.Pitch) : AgentControlInput.BodyRotation(heading);
        var (state, flags) = StateSource is { } source ? source() : default;
        var update = new AgentUpdate(controls, body, eye - CameraShift, target - CameraShift, AgentHead.Rotation(Input.MouseLook, body, target - eye), state, flags);
        if (!Rule.ShouldSend(update, now))
            return;
        if (_send(update))
        {
            Sent++;
            Last = update;
        }
        else
            Rule.Unsent();   // the session would not take it (not in the world, or a teleport): the rule is as if it had not been asked
    }
}
