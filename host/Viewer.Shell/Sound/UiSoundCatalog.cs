// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>
/// The interface sounds, one per event the official viewer names in its settings (the UISnd* keys of its settings.xml), in that
/// file's order.
/// </summary>
public enum UiSoundSlot
{
    Alert, BadKeystroke, ChatMention, ChatPing, Click, ClickRelease, HealthReductionF, HealthReductionM, InvalidOp,
    MoneyChangeDown, MoneyChangeUp, NewIncomingIMSession, ObjectCreate, ObjectDelete, ObjectRezIn, ObjectRezOut, Restart,
    Snapshot, StartIM, TeleportOut, Typing, WindowClose, WindowOpen,
}

/// <summary>Where a slot's sound comes from: the grid's sound asset with the official id, or nothing.</summary>
public enum UiSoundSource { Grid, Off }

/// <summary>One slot: the part of its settings keys, its grid sound id (empty where the official viewer plays nothing) and its default switch.</summary>
public sealed record UiSoundInfo(UiSoundSlot Slot, Guid GridId, bool DefaultEnabled, string KeyName);

/// <summary>
/// The slots in the official order with the sound ids the official viewer's settings give them. The viewer ships no interface sound:
/// every slot plays the connected grid's sound for its id (a grid that lacks it plays nothing). The mention sound starts
/// switched off, as the official viewer's mention sound does (its PlaySoundChatMention is off by default).
/// </summary>
public static class UiSoundCatalog
{
    private static UiSoundInfo S(UiSoundSlot slot, string id, string key, bool enabled = true) => new(slot, Guid.Parse(id), enabled, key);

    public static IReadOnlyList<UiSoundInfo> All { get; } =
    [
        S(UiSoundSlot.Alert, "ed124764-705d-d497-167a-182cd9fa2e6c", "alert"),
        S(UiSoundSlot.BadKeystroke, "2ca849ba-2885-4bc3-90ef-d4987a5b983a", "bad_keystroke"),
        S(UiSoundSlot.ChatMention, "03e77cb5-592c-5b33-d271-2e46497c3fb3", "chat_mention", enabled: false),
        S(UiSoundSlot.ChatPing, "7dd36df6-2624-5438-f988-fdf8588a0ad9", "chat_ping"),
        S(UiSoundSlot.Click, "4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6", "click"),
        S(UiSoundSlot.ClickRelease, "4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6", "click_release"),
        S(UiSoundSlot.HealthReductionF, "219c5d93-6c09-31c5-fb3f-c5fe7495c115", "health_reduction_f"),
        S(UiSoundSlot.HealthReductionM, "e057c244-5768-1056-c37e-1537454eeb62", "health_reduction_m"),
        S(UiSoundSlot.InvalidOp, "4174f859-0d3d-c517-c424-72923dc21f65", "invalid_op"),
        S(UiSoundSlot.MoneyChangeDown, "104974e3-dfda-428b-99ee-b0d4e748d3a3", "money_change_down"),
        S(UiSoundSlot.MoneyChangeUp, "77a018af-098e-c037-51a6-178f05877c6f", "money_change_up"),
        S(UiSoundSlot.NewIncomingIMSession, "67cc2844-00f3-2b3c-b991-6418d01e1bb7", "new_incoming_im_session"),
        S(UiSoundSlot.ObjectCreate, "f4a0660f-5446-dea2-80b7-6482a082803c", "object_create"),
        S(UiSoundSlot.ObjectDelete, "0cb7b00a-4c10-6948-84de-a93c09af2ba9", "object_delete"),
        S(UiSoundSlot.ObjectRezIn, "3c8fc726-1fd6-862d-fa01-16c5b2568db6", "object_rez_in"),
        S(UiSoundSlot.ObjectRezOut, "00000000-0000-0000-0000-000000000000", "object_rez_out"),
        S(UiSoundSlot.Restart, "b92a0f64-7709-8811-40c5-16afd624a45f", "restart"),
        S(UiSoundSlot.Snapshot, "3d09f582-3851-c0e0-f5ba-277ac5c73fb4", "snapshot"),
        S(UiSoundSlot.StartIM, "c825dfbc-9827-7e02-6507-3713d18916c1", "start_im"),
        S(UiSoundSlot.TeleportOut, "d7a9a565-a013-2a69-797d-5332baa1a947", "teleport_out"),
        S(UiSoundSlot.Typing, "5e191c7b-8996-9ced-a177-b2ac32bfea06", "typing"),
        S(UiSoundSlot.WindowClose, "2c346eda-b60c-ab33-1119-b8941916a499", "window_close"),
        S(UiSoundSlot.WindowOpen, "c80260ba-41fd-8a46-768a-6bf236360e3a", "window_open"),
    ];

    private static readonly Dictionary<UiSoundSlot, UiSoundInfo> BySlot = All.ToDictionary(i => i.Slot);

    public static UiSoundInfo Of(UiSoundSlot slot) => BySlot[slot];

    /// <summary>The official viewer's settings name of the slot ("UISndClick").</summary>
    public static string OfficialName(UiSoundSlot slot) => "UISnd" + slot;
}
