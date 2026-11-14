// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// ADR-002 at startup, after the backend has initialised: read what actually initialised (Godot 4.7:
/// RenderingServer.GetCurrentRenderingDriverName, GetVideoAdapterApiVersion, GetVideoAdapterName/Vendor, and
/// OS.GetVideoAdapterDriverInfo), log it, and decide with <see cref="RenderFloor"/>.
/// </summary>
public static class StartupCheck
{
    public static StartupDecision Run(CoreLog log)
    {
        string driver = RenderingServer.GetCurrentRenderingDriverName();
        string api = RenderingServer.GetVideoAdapterApiVersion();
        string adapter = RenderingServer.GetVideoAdapterName();
        var decision = RenderFloor.Check(driver, api, adapter);
        log.Info("startup.graphics", CoreLog.F("backend", driver), CoreLog.F("api-version", api), CoreLog.F("gpu", adapter),
            CoreLog.F("vendor", RenderingServer.GetVideoAdapterVendor()), CoreLog.F("driver", string.Join(" ", OS.GetVideoAdapterDriverInfo())),
            CoreLog.F("method", RenderingServer.GetCurrentRenderingMethod()), CoreLog.F("verdict", decision.Verdict));
        return decision;
    }
}
