// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Motion;

namespace Viewer.Shell;

/// <summary>
/// The user's camera. The camera itself is <see cref="AgentCamera"/>, ported from the official viewer's agent camera in the
/// Viewer.Motion assembly; this name stays for the host and its tests.
/// </summary>
public sealed class FollowCamera : AgentCamera
{
}
