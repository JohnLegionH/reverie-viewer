// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The interface scale the operating system's display scale asks for.</summary>
public static class DisplayScale
{
    /// <summary>The display scale, in percent, for a screen's dots per inch (96 is 100 percent): at least 100 and at most 200, the
    /// range the interface scale takes. A reading of zero or less (the system gave none) is 100.</summary>
    public static int PercentForDpi(float dpi) =>
        dpi > 0 ? Math.Clamp((int)MathF.Round(dpi * 100f / 96f), 100, 200) : 100;
}
