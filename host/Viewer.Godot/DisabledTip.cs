// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Shell.Ui;

namespace Viewer.Godot;

/// <summary>
/// The shared way to disable a button or tick box and say why (UI-BASICS H2): while the control is disabled its tooltip is the
/// reason, shown by the engine's own hover tooltip; when it is enabled again its normal tooltip, if it had one, comes back. A
/// disabled control keeps catching the pointer (the engine does not let a disabled button ignore it), so the hover still reaches it.
/// The text choice is <see cref="DisabledReasons.TooltipFor"/>, tested without the engine.
/// </summary>
public static class DisabledTip
{
    private const string NormalTipKey = "normal_tooltip";

    /// <summary>Sets <paramref name="control"/> disabled or enabled; while disabled, <paramref name="reason"/> is its tooltip.</summary>
    public static void Set(BaseButton control, bool disabled, string? reason)
    {
        if (!control.HasMeta(NormalTipKey))
            control.SetMeta(NormalTipKey, control.TooltipText ?? "");
        string normal = (string)control.GetMeta(NormalTipKey);
        control.Disabled = disabled;
        control.TooltipText = DisabledReasons.TooltipFor(disabled, reason, normal.Length == 0 ? null : normal) ?? "";
    }
}
