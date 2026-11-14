// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>
/// What the frame does when one avatar's pose cannot be worked out. The error stays with that avatar: it keeps its last pose, the clip that
/// caused it is no longer used, the cause is logged once for that avatar, and every other avatar is posed and drawn as usual.
/// Behaviour taken from the official viewer, in our own words: a motion that fails to load or deserialize is dropped and the avatar carries on
/// (llmotioncontroller.cpp addMotion and the motion's onInitialize failure, llkeyframemotion.cpp deserialize).
/// Main thread only.
/// </summary>
public sealed class PoseFailures
{
    private readonly Action<string> _log;
    private readonly HashSet<AnimationClip> _dropped = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, HashSet<string>> _seen = new(ReferenceEqualityComparer.Instance);

    public PoseFailures(Action<string> log) => _log = log;

    /// <summary>Whether <paramref name="clip"/> failed before and is no longer to be used.</summary>
    public bool IsDropped(AnimationClip? clip) => clip is not null && _dropped.Contains(clip);

    /// <summary>Records <paramref name="error"/> for <paramref name="avatar"/>. True when it is a new cause for that avatar (and so was logged).</summary>
    public bool Report(object avatar, string label, Exception error, AnimationClip? clip)
    {
        if (clip is not null) _dropped.Add(clip);
        if (!_seen.TryGetValue(avatar, out var causes)) _seen[avatar] = causes = [];
        string cause = error.GetType().Name + ": " + error.Message;
        if (!causes.Add(cause)) return false;
        _log($"avatar.pose-failed avatar={label} clip-dropped={(clip is not null).ToString().ToLowerInvariant()} cause={cause}");
        return true;
    }

    /// <summary>Forgets what is known about <paramref name="avatar"/> (it has left).</summary>
    public void Forget(object avatar) => _seen.Remove(avatar);

    /// <summary>The clips dropped so far.</summary>
    public int DroppedClips => _dropped.Count;
}
