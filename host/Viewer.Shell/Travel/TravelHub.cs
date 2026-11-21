// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Actions;
using Viewer.Core.Session;
using Viewer.Core.Social;

namespace Viewer.Shell.Travel;

/// <summary>
/// The session's travel events (teleport messages and offers, map replies, the region's arrival) as the interface sees them: the
/// session raises them on network threads, the hub queues them, and the engine's frame drains the queue into the models, so the
/// models are only ever touched on the main thread. The map and mini-map zoom are read from the settings and kept when changed.
/// One hub per session.
/// </summary>
public sealed class TravelHub
{
    private readonly ConcurrentQueue<TransportEvent> _queue = new();

    /// <param name="here">The region the agent is in (handle and name), when known; a teleport to it is a teleport inside the region.</param>
    /// <param name="attachments">How many objects the agent wears (lengthens the teleport give-up time).</param>
    /// <param name="links">The map's links to the outside. The hub points the teleport and home links at its own teleport model.</param>
    public TravelHub(Guid self, NameCache names, Func<ActionIntent, ActionResult> submit, SettingsService settings, Func<DateTimeOffset> clock, MapLinks links, Func<(ulong Handle, string Name)?>? here = null, Func<int>? attachments = null)
    {
        Teleport = new TeleportModel(self, names, submit, clock, here, attachments);
        links.Teleport = Teleport.ToHandle;
        links.TeleportAddress = a => Teleport.ToAddress(a, WorldMap?.Grid);
        links.Home = Teleport.Home;
        WorldMap = new WorldMapModel(links, settings.Get(Settings.WorldMapScale), clock);
        MiniMap = new MiniMapModel { Rotate = settings.Get(Settings.MiniMapRotate) };
        MiniMap.SetScale(settings.Get(Settings.MiniMapScale));
        // the zoom and the turn are kept when changed (the service writes only a real change)
        WorldMap.Changed += () => settings.Set(Settings.WorldMapScale, WorldMap.PixelsPerRegion);
        MiniMap.Changed += () =>
        {
            settings.Set(Settings.MiniMapScale, MiniMap.PixelsPerRegion);
            settings.Set(Settings.MiniMapRotate, MiniMap.Rotate);
        };
    }

    public TeleportModel Teleport { get; }
    public WorldMapModel WorldMap { get; }
    public MiniMapModel MiniMap { get; }
    public NoticeModel Notices { get; } = new();

    /// <summary>Any thread: queues a session event (the session's <c>Communication</c> handler).</summary>
    public void Enqueue(TransportEvent e) => _queue.Enqueue(e);

    /// <summary>Main thread, every frame: hands queued events to the models and runs the time-outs. Returns how many events were handled.</summary>
    public int Pump()
    {
        int handled = 0;
        while (_queue.TryDequeue(out var e))
        {
            handled++;
            switch (e)
            {
                case ServerAlert or ParcelCollision:
                    Notices.Receive(e);
                    break;
                case TeleportProgressed or TeleportOffered or TeleportRequested or SimConnected or MovementComplete or Disconnected:
                    Teleport.Receive(e);
                    break;
                case MapBlockSize or FriendLocated:
                    WorldMap.Receive(e);
                    break;
            }
        }
        Teleport.Tick();
        return handled;
    }
}
