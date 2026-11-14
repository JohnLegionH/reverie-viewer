// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Actions;
using Viewer.Core.Session;
using Viewer.Shell.Social;
using Viewer.Shell.Travel;

namespace Viewer.Shell.Notifications;

/// <summary>
/// A session's notifications: the script events the session raises on network threads are queued here and the engine's frame drains
/// them into the prompts (main thread only), where the notification model is also kept current. One hub per session.
/// </summary>
public sealed class NotificationHub
{
    private readonly ConcurrentQueue<TransportEvent> _queue = new();
    private NoticeFeeder? _feeder;

    public NotificationHub(Func<ActionIntent, ActionResult> submit, Func<double> clock, Func<string> selfName, Func<Guid, string?>? nameOf = null)
    {
        Model = new NotificationModel(clock);
        Prompts = new ScriptPrompts(Model, submit, clock, selfName, nameOf);
        Offers = new InventoryOffers(Model, submit);
    }

    public NotificationModel Model { get; }
    public ScriptPrompts Prompts { get; }
    public InventoryOffers Offers { get; }

    /// <summary>Shows what the friend, teleport and notice models decide as notifications (see <see cref="NoticeFeeder"/>).</summary>
    public void Attach(FriendsModel friends, TeleportModel teleport, NoticeModel notices, Func<Guid, string> nameOf) =>
        _feeder ??= new NoticeFeeder(Model, friends, teleport, notices, nameOf);

    /// <summary>Any thread: queues a session event (the session's <c>Communication</c> handler).</summary>
    public void Enqueue(TransportEvent e)
    {
        if (e is ScriptDialogReceived or ScriptPermissionRequested or LoadUrlRequested or MapDestinationRequested or InventoryOffered)
            _queue.Enqueue(e);
    }

    /// <summary>Main thread, every frame: hands queued events to the prompts and takes down toasts whose time is up. Returns how many events were handled.</summary>
    public int Pump()
    {
        int handled = 0;
        while (_queue.TryDequeue(out var e))
        {
            handled++;
            if (e is InventoryOffered)
                Offers.Receive(e);
            else
                Prompts.Receive(e);
        }
        Model.Tick();
        return handled;
    }
}
