// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;

namespace Viewer.Shell;

/// <summary>
/// Whether the frame-time log is on: the saved setting (off by default), except where the environment variable NV_FRAME_TIME_LOG is set:
/// 0, off, false or no for off, 1, on, true or yes for hitches and summaries, full for a line per frame. A variable with another value is ignored.
/// </summary>
public static class FrameTimeLogSettings
{
    public static FrameTimeLogMode Resolve(SettingsService settings, Func<string, string?> env) =>
        env("NV_FRAME_TIME_LOG")?.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" or "no" => FrameTimeLogMode.Off,
            "1" or "true" or "on" or "yes" => FrameTimeLogMode.Hitches,
            "full" => FrameTimeLogMode.Full,
            _ => settings.Get(Settings.FrameTimeLog) ? FrameTimeLogMode.Hitches : FrameTimeLogMode.Off,
        };
}
