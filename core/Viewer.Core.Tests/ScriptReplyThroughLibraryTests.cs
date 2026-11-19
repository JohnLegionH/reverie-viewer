// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using LibreMetaverse;
using LibreMetaverse.Http;
using LibreMetaverse.StructuredData;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A ScriptRunningReply that the protocol library's own event-queue client fetches from a fake region reaches the script editor with its
/// Engine and EngineChoice: the library polls through the viewer's HTTP handler (the tap), the tap keeps what the library drops, and the
/// editor shows it. The fake region is a message handler that answers the first poll and then holds the next one open. No shared state, so the
/// class runs in parallel.
/// </summary>
public class ScriptReplyThroughLibraryTests
{
    private static readonly Guid Agent = new(1, 0, 0, new byte[8]), ItemId = new(3, 0, 0, new byte[8]), Box = new(5, 0, 0, new byte[8]);

    private sealed class FakeRegion(byte[] firstAnswer) : HttpMessageHandler
    {
        private int _polls;
        public readonly List<byte[]> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            byte[] posted = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            lock (Requests)
                Requests.Add(posted);
            if (Interlocked.Increment(ref _polls) == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(firstAnswer) };
                r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/llsd+xml");
                return r;
            }
            await Task.Delay(Timeout.Infinite, ct);   // the region holds the next poll open until something happens
            throw new InvalidOperationException("unreachable");
        }
    }

    private static byte[] Answer() => OSDParser.SerializeLLSDXmlBytes(new OSDMap
    {
        ["id"] = OSD.FromInteger(1),
        ["events"] = new OSDArray
        {
            new OSDMap
            {
                ["message"] = OSD.FromString("ScriptRunningReply"),
                ["body"] = new OSDMap
                {
                    ["Script"] = new OSDArray
                    {
                        new OSDMap
                        {
                            ["ObjectID"] = OSD.FromUUID(new UUID(Box)), ["ItemID"] = OSD.FromUUID(new UUID(ItemId)),
                            ["Running"] = OSD.FromBoolean(true), ["Mono"] = OSD.FromBoolean(true),
                            ["Engine"] = OSD.FromString("Example.Alpha"), ["EngineChoice"] = OSD.FromString("Example.Alpha"),
                        },
                    },
                },
            },
        },
    });

    [Fact]
    public async Task A_script_reply_fetched_by_the_librarys_event_queue_reaches_the_editor_with_its_engine_and_choice()
    {
        var keeper = new LmvScriptReplyKeeper();
        var region = new FakeRegion(Answer());
        var tap = new LmvLoginTap(region, new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test")) { EventQueueAnswer = keeper.Note };
        var client = new GridClient { HttpCapsClient = new HttpCapsClient(tap) };
        var sim = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 9000), 1);
        // the library ignores answers while its simulator is not connected, and a real connection needs a UDP circuit: mark it connected
        typeof(Simulator).GetField("connected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(sim, true);
        var libraryHeard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new EventQueueClient(new Uri("http://example.org/eq"), sim)
        {
            OnEvent = (name, body) => libraryHeard.TrySetResult(name),
        };
        queue.Start();
        try
        {
            try { Assert.Equal("ScriptRunningReply", await libraryHeard.Task.WaitAsync(TimeSpan.FromSeconds(30))); }
            catch (TimeoutException) { Assert.Fail($"no event; polls={region.Requests.Count} running={queue.Running} second={(region.Requests.Count > 1 ? System.Text.Encoding.UTF8.GetString(region.Requests[1]) : "")} kept={keeper.Take(Box, ItemId, () => new ScriptRuntime(false, false)).Engine}"); }
        }
        finally
        {
            queue.Stop(true);
        }
        lock (region.Requests)   // the library's first poll was one the tap recognises as an event-queue poll
            Assert.NotEmpty(region.Requests);

        // what the library's reply event handler does: it takes what the tap kept, falling back to the library's own flags
        var runtime = keeper.Take(Box, ItemId, () => new ScriptRuntime(true, true));
        Assert.Equal("Example.Alpha", runtime.Engine);
        Assert.Equal("Example.Alpha", runtime.EngineChoice);

        var access = new ScriptAccess(ItemId, Guid.NewGuid(), Agent, new NotecardPermissions(Agent, Agent, 0x7fffffff, NotecardPermissions.Copy | NotecardPermissions.Modify, 0, 0, 0), false, Box, 77);
        var editor = new ScriptEditing(access, "x") { Region = new ScriptRegion(["YEngine", "Example.Alpha"], false, true, false) };
        editor.TextFetched(AssetFetchResult.Ok("default{}"u8.ToArray()));
        editor.RunningKnown(runtime);
        Assert.Equal("Example.Alpha", editor.Runtime?.Engine);
        Assert.Equal("Example.Alpha", editor.Runtime?.EngineChoice);
        Assert.Equal(new ScriptEngineWhy("Example.Alpha", "script.engine.why.saved"), editor.Why);
        Assert.Equal("engine:Example.Alpha", editor.Target.Value);
    }
}
