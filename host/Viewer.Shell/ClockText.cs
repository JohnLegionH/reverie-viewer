// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Shell;

/// <summary>The text of the menu bar's clock.</summary>
public static class ClockText
{
    public static string For(DateTime now, string? fixedText, CultureInfo culture) =>
        string.IsNullOrEmpty(fixedText) ? now.ToString("t", culture) : fixedText;
}
