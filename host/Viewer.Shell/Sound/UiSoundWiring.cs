// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Chat;
using Viewer.Shell.Notifications;
using Viewer.Shell.Travel;

namespace Viewer.Shell.Sound;

/// <summary>
/// Connects the viewer's events to the slots they play, as the official viewer does: a window opening or closing, a notification
/// appearing (an alert plays Alert; an offer from a person plays the new conversation sound; tips play nothing), the region-restart
/// warning, a new incoming conversation, the user starting a conversation, the user starting to type in the chat bar, a teleport
/// beginning, and a map teleport with no place chosen. Each call returns a handle that disconnects what it connected. Main thread only.
/// </summary>
public static class UiSoundWiring
{
    private sealed class Handle(Action undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose()
        {
            _undo?.Invoke();
            _undo = null;
        }
    }

    public static IDisposable Floaters(FloaterManager floaters, UiSounds sounds)
    {
        void Open(string _) => sounds.Play(UiSoundSlot.WindowOpen);
        void Close(string _) => sounds.Play(UiSoundSlot.WindowClose);
        floaters.Opened += Open;
        floaters.Closed += Close;
        return new Handle(() =>
        {
            floaters.Opened -= Open;
            floaters.Closed -= Close;
        });
    }

    /// <summary>The slot a notification plays when it appears, or null.</summary>
    public static UiSoundSlot? SoundOf(Notification n) => n.Sound ?? (n.Kind == NotificationKind.Alert ? UiSoundSlot.Alert : null);

    public static IDisposable Notifications(NotificationModel model, NoticeModel? notices, UiSounds sounds)
    {
        void Posted(Notification n)
        {
            if (SoundOf(n) is { } slot)
                sounds.Play(slot);
        }
        void Restart() => sounds.Play(UiSoundSlot.Restart);
        model.Posted += Posted;
        if (notices is not null)
            notices.RestartWarned += Restart;
        return new Handle(() =>
        {
            model.Posted -= Posted;
            if (notices is not null)
                notices.RestartWarned -= Restart;
        });
    }

    public static IDisposable Chat(NearbyChat? nearby, Conversations? conversations, UiSounds sounds)
    {
        void Typing() => sounds.Play(UiSoundSlot.Typing);
        void Arrived(Conversation _) => sounds.Play(UiSoundSlot.NewIncomingIMSession);
        void Started(Conversation _) => sounds.Play(UiSoundSlot.StartIM);
        if (nearby is not null)
            nearby.OwnTypingStarted += Typing;
        if (conversations is not null)
        {
            conversations.Arrived += Arrived;
            conversations.Started += Started;
        }
        return new Handle(() =>
        {
            if (nearby is not null)
                nearby.OwnTypingStarted -= Typing;
            if (conversations is not null)
            {
                conversations.Arrived -= Arrived;
                conversations.Started -= Started;
            }
        });
    }

    public static IDisposable Travel(TeleportModel? teleport, WorldMapModel? map, UiSounds sounds)
    {
        void Departing() => sounds.Play(UiSoundSlot.TeleportOut);
        void Refused() => sounds.Play(UiSoundSlot.InvalidOp);
        if (teleport is not null)
            teleport.Departing += Departing;
        if (map is not null)
            map.Refused += Refused;
        return new Handle(() =>
        {
            if (teleport is not null)
                teleport.Departing -= Departing;
            if (map is not null)
                map.Refused -= Refused;
        });
    }
}
