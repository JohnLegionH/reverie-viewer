// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addons;

namespace Viewer.Shell.Addons;

/// <summary>Where an arrow key takes a slider.</summary>
public static class AddonSliderSteps
{
    /// <summary>The next value below (<paramref name="direction"/> under zero) or above the slider's own among those it can show:
    /// a step counted from its Min, or its Max, which need not be on a step.</summary>
    public static double Next(AddonSlider slider, int direction)
    {
        double at = (slider.Value - slider.Min) / slider.Step;
        // the small allowance keeps a value already on a step from counting as just past it
        double steps = direction > 0 ? Math.Floor(at + 1e-9) + 1 : Math.Ceiling(at - 1e-9) - 1;
        return Math.Clamp(Math.Round(slider.Min + steps * slider.Step, 10), slider.Min, slider.Max);
    }
}
