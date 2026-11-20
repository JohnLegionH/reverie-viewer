// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Interaction;

/// <summary>The "when left-clicked" choices of the Build window's General tab, in the order the official list shows them
/// (floater_tools.xml, the "clickaction" combo box: Touch, Sit on object, Buy object, Pay object, Open, Zoom, Ignore object, None).</summary>
public enum ClickChoice { Touch, Sit, Buy, Pay, Open, Zoom, Ignore, None }

/// <summary>What the choice does with the object's click action, and what the official panel checks before it sends one (llpanelpermissions.cpp
/// onCommitClickAction, string_value_to_click_action, click_action_to_string_value; llfloatertools.cpp; lltoolpie.cpp, read as behaviour).</summary>
public static class ClickChoices
{
    public static IReadOnlyList<ClickChoice> All { get; } = Enum.GetValues<ClickChoice>();

    /// <summary>The protocol's click action of a choice. "None" is the official viewer's CLICK_ACTION_DISABLED (the click does nothing, not even
    /// touch) and "Ignore object" its CLICK_ACTION_IGNORE.</summary>
    public static byte ActionOf(ClickChoice c) => c switch
    {
        ClickChoice.Touch => ClickAction.Touch,
        ClickChoice.Sit => ClickAction.Sit,
        ClickChoice.Buy => ClickAction.Buy,
        ClickChoice.Pay => ClickAction.Pay,
        ClickChoice.Open => ClickAction.Open,
        ClickChoice.Zoom => ClickAction.Zoom,
        ClickChoice.Ignore => ClickAction.Ignore,
        _ => ClickAction.Disabled,
    };

    /// <summary>The choice an object's click action shows as. An action the list does not offer (play and open media, which the media
    /// controls set) shows as Touch, as the official list does.</summary>
    public static ClickChoice ChoiceOf(byte action) => action switch
    {
        ClickAction.Sit => ClickChoice.Sit,
        ClickAction.Buy => ClickChoice.Buy,
        ClickAction.Pay => ClickChoice.Pay,
        ClickAction.Open => ClickChoice.Open,
        ClickAction.Zoom => ClickChoice.Zoom,
        ClickAction.Ignore => ClickChoice.Ignore,
        ClickAction.Disabled => ClickChoice.None,
        _ => ClickChoice.Touch,
    };

    /// <summary>The list works for objects the agent may modify, and only for prims (not trees or grass)
    /// (llpanelpermissions.cpp: is_perm_modify, is_nonpermanent_enforced, all_volume).</summary>
    public static bool Enabled(bool canModify, bool allVolumes) => canModify && allVolumes;

    /// <summary>What a pick of a choice needs: "Buy object" is refused for an object that is not for sale (the notification CantSetBuyObject,
    /// and the list goes back to what it was); "Pay object" is set anyway for an object without a money() script, with the warning
    /// ClickActionNotPayable. The notice key is null when there is none.</summary>
    public static (bool Set, string? Notice) Check(ClickChoice c, bool forSale, bool payable) => c switch
    {
        ClickChoice.Buy when !forSale => (false, "edit.click.cant_buy"),
        ClickChoice.Pay when !payable => (true, "edit.click.not_payable"),
        _ => (true, null),
    };

    /// <summary>The click action for an object whose sale just changed, when every selected object is still on Touch or Buy: Buy for an object
    /// now for sale, Touch for one no longer for sale (llpanelpermissions.cpp setAllSaleInfo). Null: leave the actions as they are.</summary>
    public static byte? AfterSaleChange(IEnumerable<byte> actions, bool wasForSale, bool isForSale)
    {
        if (wasForSale == isForSale || actions.Any(a => a is not (ClickAction.Touch or ClickAction.Buy)))
            return null;
        return isForSale ? ClickAction.Buy : ClickAction.Touch;
    }

    /// <summary>How far the camera stands from an object a Zoom click frames: the object's box (its size's length) twice over, divided by the
    /// tangent of the angle of view, which is the field of view widened by the aspect when the window is wider than tall and never below
    /// 0.1 (lltoolpie.cpp, CLICK_ACTION_ZOOM). The field of view is the vertical one, in radians.</summary>
    public static float ZoomDistance(Vector3 size, float verticalFov, float aspect)
    {
        float angle = MathF.Max(0.1f, aspect > 1f ? verticalFov * aspect : verticalFov);
        return size.Length() * 2f / MathF.Atan(angle);
    }
}
