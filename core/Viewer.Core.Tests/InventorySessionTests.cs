// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Net;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Xunit;
using static Viewer.Core.Tests.InventoryModelTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The inventory as the session carries it: the transport's inventory records reach the session's model, opening a folder
/// asks the transport for it, and folder or item names reach neither a capture nor a log. A fake transport, no grid.
/// </summary>
public class InventorySessionTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private const string PrivateName = "Zebra Plans 4471";

    private static (ViewerSession Session, FakeTransport Fake, ListSink Log) InWorld()
    {
        var fake = new FakeTransport();
        var log = new ListSink();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(log, "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake, log);
    }

    [Fact]
    public void The_skeleton_and_the_grids_answers_reach_the_sessions_model()
    {
        var (s, fake, _) = InWorld();
        Assert.False(s.Inventory.HasSkeleton);
        fake.Raise(Skeleton());
        Assert.True(s.Inventory.HasSkeleton);
        Assert.Equal(Root, s.Inventory.RootId);
        fake.Raise(new InventoryFolderFetched(Mine, true, 9, 1, new List<InventoryFolderData>(), new List<InventoryItemData> { I(1, Mine, "Note") }));
        Assert.Equal(FolderLoad.Loaded, s.Inventory.LoadState(Mine));
        fake.Raise(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(2, Mine, "Other") }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.Equal(2, s.Inventory.ChildItems(Mine).Count);
    }

    [Fact]
    public void Opening_a_folder_asks_the_transport_for_it_with_the_right_owner()
    {
        var (s, fake, _) = InWorld();
        fake.Raise(Skeleton());
        Assert.True(s.Inventory.EnsureLoaded(Textures));
        Assert.True(s.Inventory.EnsureLoaded(LibObjects));
        Assert.Equal(new[] { (Textures, Owner), (LibObjects, LibOwner) }, fake.FolderRequests.ToArray());
    }

    [Fact]
    public void A_transport_that_cannot_fetch_leaves_the_folder_failed_and_does_not_throw()
    {
        var bare = new BareTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, bare, new CoreLog(new ListSink(), "test"));
        bare.Raise(Skeleton());
        Assert.False(session.Inventory.EnsureLoaded(Textures) && session.Inventory.LoadState(Textures) != FolderLoad.Failed);
        Assert.Equal(FolderLoad.Failed, session.Inventory.LoadState(Textures));
    }

    [Fact]
    public void Inventory_events_are_never_recorded_in_a_capture()
    {
        Assert.False(CaptureFile.IsRecorded(Skeleton()));
        Assert.False(CaptureFile.IsRecorded(new InventoryFolderFetched(Mine, true, 1, 0, new List<InventoryFolderData>(), new List<InventoryItemData>())));
        Assert.False(CaptureFile.IsRecorded(new InventoryChanged(Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>())));
        Assert.True(CaptureFile.IsRecorded(new ObjectsKilled(Here, [1])));
    }

    [Fact]
    public void A_recording_session_writes_no_inventory_into_its_capture_file()
    {
        string path = TestTemp.NewFilePath("inventory-capture", ".jsonl.gz");
        var fake = new FakeTransport();
        using (var writer = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "inventory", Guid.Empty, 0, 0, "test")))
        {
            var rec = new RecordingTransport(fake, writer, new CaptureFile.Aliases(Guid.Empty));
            fake.Raise(new ObjectsKilled(Here, [1]));
            fake.Raise(Skeleton());
            fake.Raise(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(1, Mine, PrivateName) }, Array.Empty<Guid>(), Array.Empty<Guid>()));
            Assert.Equal(1, rec.Recorded);
        }
        var c = CaptureFile.Read(path);
        Assert.IsType<ObjectsKilled>(Assert.Single(c.Records).Event);
        TestTemp.Delete(path);
    }

    [Fact]
    public void Folder_and_item_names_never_reach_the_log()
    {
        var (s, fake, log) = InWorld();
        fake.Raise(Skeleton());
        s.Inventory.EnsureLoaded(Mine);
        fake.Raise(new InventoryFolderFetched(Mine, true, 9, 2, new[] { F(MineSub, Mine, PrivateName) }, new List<InventoryItemData> { I(1, Mine, PrivateName) }));
        fake.Raise(new InventoryChanged(new[] { F(Textures, Root, PrivateName + " renamed") }, new[] { I(2, Mine, PrivateName) }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        s.Inventory.EnsureLoaded(Trash);
        Assert.DoesNotContain(PrivateName, log.Text);
        Assert.DoesNotContain("Zebra", log.Text);
    }

    /// <summary>A transport with no inventory side at all.</summary>
    private sealed class BareTransport : ISessionTransport
    {
        public event Action<TransportEvent>? Event;
        public void Raise(TransportEvent e) => Event?.Invoke(e);
        public Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct) => Task.FromResult(FakeTransport.Ok(Owner));
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public SimRef? CurrentSim => null;
        public int ConnectedSimulatorCount => 0;
        public void SetDrawDistance(float metres) { }
        public void Dispose() { }
    }
}
