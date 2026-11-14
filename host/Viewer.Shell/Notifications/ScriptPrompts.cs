// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Actions;
using Viewer.Core.Session;

namespace Viewer.Shell.Notifications;

// Behaviour follows the official viewer's script dialogs, text boxes, permission questions, web address requests and map destinations,
// written from a behaviour spec in our own words (no code, text or layout copied).

/// <summary>How many dialogs one object may have open: the newest replaces the older one of the same object (mode 0, the default), or of the same object and channel.</summary>
public enum DialogLimit { PerObject, PerChannel }

/// <summary>
/// Turns what scripts ask (dialogs, text boxes, permission requests, web addresses, map destinations) into notifications, and sends the
/// user's answers through the action pipeline. Main thread only.
/// </summary>
public sealed class ScriptPrompts
{
    /// <summary>At most this many permission questions from one owner in the window (the owner's own objects: per object).</summary>
    public const int PermissionThrottleCount = 5;
    public const double PermissionThrottleSeconds = 10;

    /// <summary>The most bytes a typed reply may carry.</summary>
    public const int MaxLabelBytes = ScriptDialogReplyIntent.MaxLabelBytes;

    /// <summary>The button label the region sends for a text box: when any button has it, the whole dialog is a text entry.</summary>
    public const string TextBoxMarker = "!!llTextBox!!";

    private readonly NotificationModel _model;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<double> _clock;
    private readonly Func<string> _selfName;
    private readonly Func<Guid, string?> _nameOf;
    private readonly Dictionary<long, (Guid Object, int Channel)> _dialogs = new();
    private readonly Dictionary<string, List<double>> _permissionTimes = new();

    /// <param name="nameOf">The name of an agent or group by id when the viewer knows it (for the owner of a web address request); null when it does not.</param>
    public ScriptPrompts(NotificationModel model, Func<ActionIntent, ActionResult> submit, Func<double> clock, Func<string> selfName, Func<Guid, string?>? nameOf = null)
    {
        _model = model;
        _submit = submit;
        _clock = clock;
        _selfName = selfName;
        _nameOf = nameOf ?? (_ => null);
    }

    public DialogLimit Limit { get; set; }

    /// <summary>Whether scripts may open windows (a map destination). On by default.</summary>
    public bool ScriptsCanShowUi { get; set; } = true;

    /// <summary>Asks whether an object or its owner is blocked (object id, owner id). The block list belongs to another part of the viewer; until it is wired nothing is.</summary>
    public Func<Guid, Guid, bool> IsMuted { get; set; } = (_, _) => false;

    /// <summary>Blocks an object (its id and name) when the user chooses Block on one of its prompts.</summary>
    public Action<Guid, string>? BlockObject { get; set; }

    /// <summary>Takes the answer to "is it blocked" from a block list: an object counts when its own id or its owner is on the list.</summary>
    public void UseMuteList(Viewer.Core.Social.MuteList list) =>
        IsMuted = (obj, owner) => list.IsMuted(obj, null) || (owner != Guid.Empty && list.IsMuted(owner, null));

    /// <summary>Permission requests dropped by the throttle, requests from blocked sources dropped, and requests with no known permission.</summary>
    public int Throttled { get; private set; }
    public int Muted { get; private set; }
    public int Unknown { get; private set; }

    public event Action<MapDestinationRequested>? MapDestination;

    public void Receive(TransportEvent e)
    {
        switch (e)
        {
            case ScriptDialogReceived d:
                Dialog(d);
                break;
            case ScriptPermissionRequested q:
                Permission(q);
                break;
            case LoadUrlRequested u:
                LoadUrl(u);
                break;
            case MapDestinationRequested m when ScriptsCanShowUi:
                MapDestination?.Invoke(m);
                break;
        }
    }

    // ---------------- dialogs and text boxes ----------------

    private void Dialog(ScriptDialogReceived d)
    {
        if (IsMuted(d.ObjectId, d.OwnerId))
        {
            Muted++;
            return;
        }
        bool textBox = d.Buttons.Contains(TextBoxMarker);
        var labels = DialogLayout.Clip(d.Buttons);
        ReplaceOlderDialogs(d);
        var sim = d.Sim;
        Guid obj = d.ObjectId;
        string objectName = d.ObjectName;
        int channel = d.Channel;
        long id = _model.Post(new Notification
        {
            Kind = NotificationKind.Offer,
            Title = NoticeText.Raw(d.ObjectName),
            Subtitle = NoticeText.Raw(d.OwnerName),
            Body = NoticeText.Raw(d.Message.Reveal()),
            Buttons = textBox ? [] : labels.Select((l, i) => new NoticeButton(NoticeText.Raw(l), i)).ToArray(),
            TextEntry = textBox,
            ScriptDialog = true,
            CanBlock = true,
            Source = obj,
            Channel = channel,
            Responder = a =>
            {
                if (a.Button == NotificationAnswer.BlockIndex)
                {
                    BlockObject?.Invoke(obj, objectName);   // blocked: no reply goes to the script
                    return;
                }
                if (a.Button < 0)
                    return;   // Ignore: nothing is sent
                if (textBox)
                    _submit(new ScriptDialogReplyIntent(sim, obj, channel, 0, CutToBytes(a.Text ?? "", MaxLabelBytes)));
                else if (a.Button < labels.Count)
                    _submit(new ScriptDialogReplyIntent(sim, obj, channel, a.Button, labels[a.Button]));
            },
        });
        _dialogs[id] = (obj, channel);
    }

    /// <summary>The newest dialog replaces the older one of the same object (and, in the per-channel mode, channel); the older closes with no reply.</summary>
    private void ReplaceOlderDialogs(ScriptDialogReceived d)
    {
        var waiting = _model.List.Select(n => n.Id).ToHashSet();
        foreach (var id in _dialogs.Keys.Where(k => !waiting.Contains(k)).ToArray())
            _dialogs.Remove(id);
        foreach (var (id, key) in _dialogs.ToArray())
            if (key.Object == d.ObjectId && (Limit == DialogLimit.PerObject || key.Channel == d.Channel))
            {
                _model.Remove(id);
                _dialogs.Remove(id);
            }
    }

    /// <summary>The longest prefix of the text that fits in the byte limit without splitting a character.</summary>
    internal static string CutToBytes(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;
        var sb = new StringBuilder();
        int bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > maxBytes)
                break;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    // ---------------- permission requests ----------------

    private void Permission(ScriptPermissionRequested q)
    {
        int known = ScriptPermissions.Known(q.Questions);
        if (known == 0)
        {
            Unknown++;
            return;
        }
        if (IsMuted(q.TaskId, Guid.Empty))
        {
            Muted++;
            return;
        }
        if (!NoteQuestion(q))
        {
            Throttled++;
            return;
        }
        var sim = q.Sim;
        Guid task = q.TaskId, item = q.ItemId;
        string objectName = q.ObjectName;
        bool caution = ScriptPermissions.HasCaution(known);
        _model.Post(new Notification
        {
            Kind = NotificationKind.Offer,
            Title = NoticeText.Raw(q.ObjectName),
            Subtitle = NoticeText.Raw(q.OwnerName),
            Body = NoticeText.Of(caution ? "script.permission.caution" : "script.permission.request"),
            Details = ScriptPermissions.Asked(known).Select(e => NoticeText.Of(e.Key)).ToArray(),
            Buttons = [new NoticeButton(NoticeText.Of("button.yes"), 0), new NoticeButton(NoticeText.Of("button.no"), 1)],
            Caution = caution,
            CanBlock = true,
            Source = task,
            Responder = a =>
            {
                if (a.Button == NotificationAnswer.BlockIndex)
                {
                    BlockObject?.Invoke(task, objectName);
                    _submit(new ScriptPermissionAnswerIntent(sim, task, item, 0));   // the script is told no, so it does not wait for ever
                    return;
                }
                if (a.Button < 0)
                    return;   // closed without an answer: the script keeps waiting
                _submit(new ScriptPermissionAnswerIntent(sim, task, item, a.Button == 0 ? known : 0));
            },
        });
    }

    /// <summary>Counts a permission question against its owner's window. The user's own objects are counted one object at a time, since
    /// resetting many of them at once is common; everyone else's per owner. False when the window is full and the question is dropped.</summary>
    private bool NoteQuestion(ScriptPermissionRequested q)
    {
        bool own = Clean(q.OwnerName) == Clean(_selfName());
        string key = own ? "task:" + q.TaskId : "owner:" + Clean(q.OwnerName);
        double now = _clock();
        if (!_permissionTimes.TryGetValue(key, out var times))
            _permissionTimes[key] = times = new List<double>();
        times.RemoveAll(t => now - t >= PermissionThrottleSeconds);
        if (times.Count >= PermissionThrottleCount)
            return false;
        times.Add(now);
        if (_permissionTimes.Count > 500)
            foreach (var stale in _permissionTimes.Where(p => p.Value.Count == 0 || now - p.Value[^1] >= PermissionThrottleSeconds).Select(p => p.Key).ToArray())
                _permissionTimes.Remove(stale);
        return true;
    }

    private static string Clean(string name)
    {
        string n = (name ?? "").Trim();
        return n.EndsWith(" Resident", StringComparison.Ordinal) ? n[..^" Resident".Length] : n;
    }

    // ---------------- web addresses ----------------

    private void LoadUrl(LoadUrlRequested u)
    {
        if (IsMuted(u.ObjectId, u.OwnerId))
        {
            Muted++;
            return;
        }
        string url = u.Url ?? "";
        bool openable = OpenUrlIntent.IsOpenable(url);
        string? owner = _nameOf(u.OwnerId);
        var buttons = new List<NoticeButton>();
        if (openable)
            buttons.Add(new NoticeButton(NoticeText.Of("button.go_to_page"), 0));
        buttons.Add(new NoticeButton(NoticeText.Of("button.cancel"), 1));
        _model.Post(new Notification
        {
            Kind = NotificationKind.Offer,
            Title = NoticeText.Raw(u.ObjectName),
            Subtitle = owner is null ? null : NoticeText.Raw(owner),
            Body = NoticeText.Raw(u.Message.Reveal()),
            Details = [NoticeText.Raw(url)],
            Buttons = buttons,
            Source = u.ObjectId,
            Responder = a =>
            {
                if (a.Button == 0 && openable)
                    _submit(new OpenUrlIntent(url));   // only the user's own click opens anything
            },
        });
    }
}
