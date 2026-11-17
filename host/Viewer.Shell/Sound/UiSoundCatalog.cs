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

/// <summary>Where a slot's sound comes from: the file shipped with this viewer, the grid's sound asset with the official id, or nothing.</summary>
public enum UiSoundSource { Ours, Grid, Off }

/// <summary>One slot: its grid sound id (empty where the official viewer plays nothing), its default source and switch, and the name of
/// our file (a recording, <see cref="UiSoundRecordings"/>, or made by tools/ui-sounds; shipped inside the shell assembly).</summary>
public sealed record UiSoundInfo(UiSoundSlot Slot, Guid GridId, UiSoundSource DefaultSource, bool DefaultEnabled, string File)
{
    /// <summary>The part of the settings keys that names this slot ("new_incoming_im_session").</summary>
    public string KeyName => File["ui-".Length..^".wav".Length].Replace('-', '_');
}

/// <summary>
/// The slots in the official order with the sound ids the official viewer's settings give them. Every slot plays our file by default
/// (on Second Life the grid's instead, by the per-grid switch: <see cref="UiSoundSettings.SourceOn"/>). The mention sound starts
/// switched off, as the official viewer's mention sound does (its PlaySoundChatMention is off by default).
/// </summary>
public static class UiSoundCatalog
{
    private static UiSoundInfo S(UiSoundSlot slot, string id, string file, UiSoundSource source = UiSoundSource.Ours, bool enabled = true) =>
        new(slot, Guid.Parse(id), source, enabled, file);

    public static IReadOnlyList<UiSoundInfo> All { get; } =
    [
        S(UiSoundSlot.Alert, "ed124764-705d-d497-167a-182cd9fa2e6c", "ui-alert.wav"),
        S(UiSoundSlot.BadKeystroke, "2ca849ba-2885-4bc3-90ef-d4987a5b983a", "ui-bad-keystroke.wav"),
        S(UiSoundSlot.ChatMention, "03e77cb5-592c-5b33-d271-2e46497c3fb3", "ui-chat-mention.wav", enabled: false),
        S(UiSoundSlot.ChatPing, "7dd36df6-2624-5438-f988-fdf8588a0ad9", "ui-chat-ping.wav"),
        S(UiSoundSlot.Click, "4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6", "ui-click.wav"),
        S(UiSoundSlot.ClickRelease, "4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6", "ui-click-release.wav"),
        S(UiSoundSlot.HealthReductionF, "219c5d93-6c09-31c5-fb3f-c5fe7495c115", "ui-health-reduction-f.wav"),
        S(UiSoundSlot.HealthReductionM, "e057c244-5768-1056-c37e-1537454eeb62", "ui-health-reduction-m.wav"),
        S(UiSoundSlot.InvalidOp, "4174f859-0d3d-c517-c424-72923dc21f65", "ui-invalid-op.wav"),
        S(UiSoundSlot.MoneyChangeDown, "104974e3-dfda-428b-99ee-b0d4e748d3a3", "ui-money-change-down.wav"),
        S(UiSoundSlot.MoneyChangeUp, "77a018af-098e-c037-51a6-178f05877c6f", "ui-money-change-up.wav"),
        S(UiSoundSlot.NewIncomingIMSession, "67cc2844-00f3-2b3c-b991-6418d01e1bb7", "ui-new-incoming-im-session.wav"),
        S(UiSoundSlot.ObjectCreate, "f4a0660f-5446-dea2-80b7-6482a082803c", "ui-object-create.wav"),
        S(UiSoundSlot.ObjectDelete, "0cb7b00a-4c10-6948-84de-a93c09af2ba9", "ui-object-delete.wav"),
        S(UiSoundSlot.ObjectRezIn, "3c8fc726-1fd6-862d-fa01-16c5b2568db6", "ui-object-rez-in.wav"),
        S(UiSoundSlot.ObjectRezOut, "00000000-0000-0000-0000-000000000000", "ui-object-rez-out.wav"),
        S(UiSoundSlot.Restart, "b92a0f64-7709-8811-40c5-16afd624a45f", "ui-restart.wav"),
        S(UiSoundSlot.Snapshot, "3d09f582-3851-c0e0-f5ba-277ac5c73fb4", "ui-snapshot.wav"),
        S(UiSoundSlot.StartIM, "c825dfbc-9827-7e02-6507-3713d18916c1", "ui-start-im.wav"),
        S(UiSoundSlot.TeleportOut, "d7a9a565-a013-2a69-797d-5332baa1a947", "ui-teleport-out.wav"),
        S(UiSoundSlot.Typing, "5e191c7b-8996-9ced-a177-b2ac32bfea06", "ui-typing.wav"),
        S(UiSoundSlot.WindowClose, "2c346eda-b60c-ab33-1119-b8941916a499", "ui-window-close.wav"),
        S(UiSoundSlot.WindowOpen, "c80260ba-41fd-8a46-768a-6bf236360e3a", "ui-window-open.wav"),
    ];

    private static readonly Dictionary<UiSoundSlot, UiSoundInfo> BySlot = All.ToDictionary(i => i.Slot);

    public static UiSoundInfo Of(UiSoundSlot slot) => BySlot[slot];

    /// <summary>The official viewer's settings name of the slot ("UISndClick").</summary>
    public static string OfficialName(UiSoundSlot slot) => "UISnd" + slot;
}
