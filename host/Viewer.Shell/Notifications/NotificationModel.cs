// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Notifications;

// Behaviour follows the official viewer's notification types, toast times and unique-notice rule, written from a behaviour spec in
// our own words (no code, text or layout copied).

/// <summary>How a notification behaves: a tip is a toast only; a notify keeps until answered after a short toast; an offer keeps
/// until answered and its toast does not time out; an alert is modal.</summary>
public enum NotificationKind { Tip, Notify, Offer, Alert }

/// <summary>Words for the interface: a string-table key with an optional argument, or text exactly as given (a script's or the grid's).</summary>
public sealed record NoticeText(string? Key, string? Arg, string? Literal)
{
    public static NoticeText Of(string key, string? arg = null) => new(key, arg, null);
    public static NoticeText Raw(string literal) => new(null, null, literal);
}

/// <summary>One button of a notification; <paramref name="Index"/> is what the responder receives.</summary>
public sealed record NoticeButton(NoticeText Label, int Index);

/// <summary>What the user did with a notification: the button's index (-1 closes without a reply) and the typed text of a text entry.</summary>
public readonly record struct NotificationAnswer(int Button, string Text)
{
    /// <summary>The button index of the Block button that a blockable notification has besides its own buttons.</summary>
    public const int BlockIndex = -2;
}

/// <summary>One notification. Built by the poster; the model owns its state.</summary>
public sealed class Notification
{
    public long Id { get; internal set; }
    public NotificationKind Kind { get; init; }
    public NoticeText? Title { get; init; }
    public NoticeText Body { get; init; } = NoticeText.Raw("");

    /// <summary>A second line under the title (a script dialog's owner).</summary>
    public NoticeText? Subtitle { get; init; }

    /// <summary>Lines listed under the body (the permissions asked for, a web address).</summary>
    public IReadOnlyList<NoticeText> Details { get; init; } = [];
    public IReadOnlyList<NoticeButton> Buttons { get; init; } = [];

    /// <summary>True for a text entry: a text field and Submit instead of buttons.</summary>
    public bool TextEntry { get; init; }

    /// <summary>A new notification with the same key replaces the older one (a friend's online notice replaces their last).</summary>
    public string? UniqueKey { get; init; }

    /// <summary>Drawn as a warning (a request that could cost the user money).</summary>
    public bool Caution { get; init; }

    /// <summary>Who raised it (an object's id for a script prompt), so a later prompt from the same source can replace this one.</summary>
    public Guid Source { get; init; }

    /// <summary>True for a script dialog or text box: drawn as a grid of buttons (first button bottom-left) with an Ignore button below.</summary>
    public bool ScriptDialog { get; init; }

    /// <summary>True when the notification has a Block button besides its own buttons (answered with <see cref="NotificationAnswer.BlockIndex"/>).</summary>
    public bool CanBlock { get; init; }

    /// <summary>The script channel a script dialog was sent on (for the per-channel replacement rule).</summary>
    public int Channel { get; init; }

    /// <summary>Runs once when answered. Not run when the notification is removed or replaced.</summary>
    public Action<NotificationAnswer>? Responder { get; init; }

    /// <summary>The interface sound the notification plays when it appears, where its kind does not decide it (an offer from a person
    /// plays the new conversation sound, as the official viewer's offer notifications name it). Null: the kind decides.</summary>
    public Viewer.Shell.Sound.UiSoundSlot? Sound { get; init; }

    public double CreatedAt { get; internal set; }
    public bool Unread { get; internal set; }
    public bool ToastUp { get; internal set; }
    public double ToastUntil { get; internal set; }
    public bool Answered { get; internal set; }
}

/// <summary>
/// Toasts, the list of notifications that wait for the user, and the history. Main thread only; the clock is injected so time is
/// a test input. A tip is a toast only; a notify and an offer stay in the list until answered; an alert is modal and is not a toast.
/// </summary>
public sealed class NotificationModel
{
    /// <summary>How long a tip's toast and a notify's toast stay up, in seconds.</summary>
    public const double TipSeconds = 10, NotifySeconds = 5;

    /// <summary>The most toasts on screen at once (the newest), and the most answered or expired items the history keeps.</summary>
    public const int MaxToasts = 5, HistoryLimit = 100;

    private readonly Func<double> _clock;
    private readonly List<Notification> _live = new();
    private readonly List<Notification> _history = new();
    private long _next;

    public NotificationModel(Func<double>? clock = null)
    {
        _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>The toasts on screen, oldest first (so the newest is last).</summary>
    public IReadOnlyList<Notification> Toasts => _live.Where(n => n.ToastUp).ToArray();

    /// <summary>The notifications that wait for the user, oldest first: notifies and offers not yet answered.</summary>
    public IReadOnlyList<Notification> List => _live.Where(InList).ToArray();

    /// <summary>Answered, expired and pushed-off items, oldest first.</summary>
    public IReadOnlyList<Notification> History => _history.ToArray();

    /// <summary>The alert the user must answer first; null when there is none.</summary>
    public Notification? Modal => _live.FirstOrDefault(n => n.Kind == NotificationKind.Alert);

    /// <summary>How many of the list's notifications were never read.</summary>
    public int UnreadCount => _live.Count(n => InList(n) && n.Unread);

    /// <summary>Raised when anything shown changed.</summary>
    public event Action? Changed;

    /// <summary>Raised when a notification is posted (after it is in the lists).</summary>
    public event Action<Notification>? Posted;

    private static bool InList(Notification n) => n.Kind is NotificationKind.Notify or NotificationKind.Offer;

    public long Post(Notification n)
    {
        ArgumentNullException.ThrowIfNull(n);
        double now = _clock();
        if (n.UniqueKey is { } key)
            _live.RemoveAll(o => o.UniqueKey == key);   // replaced: gone without a reply and without a history entry
        n.Id = ++_next;
        n.CreatedAt = now;
        n.Answered = false;
        n.Unread = InList(n);
        n.ToastUp = n.Kind != NotificationKind.Alert;
        n.ToastUntil = n.Kind switch
        {
            NotificationKind.Tip => now + TipSeconds,
            NotificationKind.Notify => now + NotifySeconds,
            _ => double.PositiveInfinity,
        };
        _live.Add(n);
        EnforceToastLimit();
        Changed?.Invoke();
        Posted?.Invoke(n);
        return n.Id;
    }

    /// <summary>Answers a notification: runs its responder once with the button (-1 closes without a reply) and the typed text, and moves it
    /// to the history. False when there is no such waiting notification (a second answer does nothing).</summary>
    public bool Answer(long id, int button, string text = "")
    {
        var n = _live.Find(x => x.Id == id);
        if (n is null)
            return false;
        _live.Remove(n);
        n.ToastUp = false;
        n.Answered = true;
        Archive(n);
        Changed?.Invoke();
        n.Responder?.Invoke(new NotificationAnswer(button, text ?? ""));
        return true;
    }

    /// <summary>Takes the toast off the screen. A tip is let go (it is only a toast); anything else stays in the list.</summary>
    public bool CloseToast(long id)
    {
        var n = _live.Find(x => x.Id == id);
        if (n is null || !n.ToastUp)
            return false;
        n.ToastUp = false;
        if (n.Kind == NotificationKind.Tip)
        {
            _live.Remove(n);
            Archive(n);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drops a notification with no reply and no history entry (its source withdrew it).</summary>
    public bool Remove(long id)
    {
        if (_live.RemoveAll(x => x.Id == id) == 0)
            return false;
        Changed?.Invoke();
        return true;
    }

    public void MarkRead(long id)
    {
        if (_live.Find(x => x.Id == id) is { Unread: true } n)
        {
            n.Unread = false;
            Changed?.Invoke();
        }
    }

    public void MarkAllRead()
    {
        bool any = false;
        foreach (var n in _live.Where(x => x.Unread))
        {
            n.Unread = false;
            any = true;
        }
        if (any)
            Changed?.Invoke();
    }

    /// <summary>Every frame: takes down the toasts whose time is up.</summary>
    public void Tick()
    {
        double now = _clock();
        bool changed = false;
        foreach (var n in _live.Where(x => x.ToastUp && x.ToastUntil <= now).ToArray())
        {
            n.ToastUp = false;
            if (n.Kind == NotificationKind.Tip)
            {
                _live.Remove(n);
                Archive(n);
            }
            changed = true;
        }
        if (changed)
            Changed?.Invoke();
    }

    private void EnforceToastLimit()
    {
        var toasts = _live.Where(n => n.ToastUp).ToList();
        for (int i = 0; i < toasts.Count - MaxToasts; i++)
        {
            toasts[i].ToastUp = false;
            if (toasts[i].Kind == NotificationKind.Tip)
            {
                _live.Remove(toasts[i]);
                Archive(toasts[i]);
            }
        }
    }

    private void Archive(Notification n)
    {
        _history.Add(n);
        if (_history.Count > HistoryLimit)
            _history.RemoveRange(0, _history.Count - HistoryLimit);
    }
}
