// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Viewer.Core.Avatars;

namespace Viewer.Godot;

public partial class Main
{
    /// <summary>The automation's expression command (development builds): play or stop asks the region to start or stop an expression animation on the own
    /// avatar (play also starts a new trace of the face's weights); trace reads that trace. Null without a session, a drawn body, or for a name that is not an
    /// expression animation.</summary>
    public object? ExpressionCall(string action, string? name)
    {
        if (_presented is not { } session || OwnAvatars is not { } avatars || avatars.Scene.Self is not { } self || _bodyHost is not { } host) return null;
        if (action == "trace") return host.ExpressionTrace(self.AgentId, reset: false);
        if (Emotes.FromEmoteName(name) is not { } id || !Emotes.IsEmote(id)) return null;
        bool start = action == "play";
        if (start && host.ExpressionTrace(self.AgentId, reset: true) is null) return null;
        bool sent = session.SendAnimations([(id, start)]);
        return new { id, start, sent };
    }
}
