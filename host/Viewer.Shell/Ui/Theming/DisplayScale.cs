// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The interface scale the operating system's display scale asks for.</summary>
public static class DisplayScale
{
    /// <summary>The display scale, in percent, for a screen's dots per inch (96 is 100 percent), within the interface size's range
    /// (<see cref="InterfaceSize"/>, 75 to 200). A reading of zero or less (the system gave none) is 100.</summary>
    public static int PercentForDpi(float dpi) => InterfaceSize.DefaultFor(dpi);
}
