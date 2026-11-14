// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Sound;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// The first build tools: texture upload sizing, encoding, checks, cost and the two capability steps; the create tool, rezzing from
/// inventory, take and delete (their messages and enable rules); the Build menu. No engine and no grid; no shared state, so the class
/// runs in parallel.
/// </summary>
public class BuildToolsTests
{
    // ---------------- sizing (llimage.cpp biasedDimToPowerOfTwo, llfloaterimagepreview.cpp) ----------------

    [Theory]
    [InlineData(4, 4)]
    [InlineData(1, 4)]          // never below the smallest side
    [InlineData(100, 64)]       // 100/64 = 1.56, not above 1.75: down
    [InlineData(120, 128)]      // 120/64 = 1.875: up
    [InlineData(112, 64)]       // exactly 1.75: down
    [InlineData(113, 128)]
    [InlineData(512, 512)]
    [InlineData(3000, 2048)]    // never above the maximum
    [InlineData(1800, 2048)]    // 1800/1024 = 1.76: up
    public void A_side_goes_to_a_power_of_two_biased_down(int side, int expected) =>
        Assert.Equal(expected, UploadImageRules.BiasedDim(side, UploadImageRules.MaxDimension));

    [Theory]
    [InlineData(640, 480, 512, 512)]       // 480/256 = 1.875: up
    [InlineData(300, 200, 256, 128)]       // 300/256 = 1.17 and 200/128 = 1.56: both down
    [InlineData(1024, 1024, 1024, 1024)]
    [InlineData(4000, 2000, 1024, 512)]    // shrunk to 1024 by 512 keeping its proportions, then powers of two
    [InlineData(2000, 300, 1024, 128)]     // 2000x300 -> 1024x154 -> 1024 x 128 (154 contracts to 128)
    [InlineData(10, 10, 8, 8)]
    public void An_image_arrives_at_the_official_size(int w, int h, int ew, int eh)
    {
        Assert.Equal((ew, eh), UploadImageRules.TargetSize(w, h));
    }

    [Fact]
    public void The_upload_takes_the_official_image_files_only()
    {
        foreach (var ok in new[] { "a.png", "B.PNG", "c.jpg", "d.jpeg", "e.tga", "f.bmp", @"x\y\z.Png" })
            Assert.True(UploadImageRules.IsSupportedFile(ok), ok);
        foreach (var no in new[] { "a.gif", "b.webp", "c.j2c", "d", "e.png.txt", "f.wav" })
            Assert.False(UploadImageRules.IsSupportedFile(no), no);
        Assert.Equal("my picture", UploadImageRules.DefaultName(@"pics\sub\my picture.png"));
        Assert.Equal("shot.final", UploadImageRules.DefaultName("/home/u/shot.final.jpg"));
    }

    private static byte[] Gradient(int w, int h, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[i + 2] = (byte)((x + y) * 255 / Math.Max(1, w + h - 2));
                px[i + 3] = alpha;
            }
        return px;
    }

    [Fact]
    public void Scaling_keeps_the_picture_and_gives_the_target_size()
    {
        var px = Gradient(100, 50);
        var scaled = UploadChecks.Scale(px, 100, 50, 64, 64);
        Assert.Equal(64 * 64 * 4, scaled.Length);
        // left edge stays dark red, right edge bright red; top green 0, bottom green high
        Assert.True(scaled[0] < 20 && scaled[(63) * 4] > 230, "red runs left to right");
        Assert.True(scaled[1] < 20 && scaled[(63 * 64) * 4 + 1] > 230, "green runs top to bottom");
        Assert.Equal(255, scaled[3]);
        Assert.Equal(px, UploadChecks.Scale(px, 100, 50, 100, 50));
    }

    [Fact]
    public void Alpha_is_kept_only_when_a_pixel_is_not_opaque()
    {
        Assert.False(UploadChecks.HasAlpha(Gradient(8, 8)));
        var px = Gradient(8, 8);
        px[7 * 4 + 3] = 254;
        Assert.True(UploadChecks.HasAlpha(px));
    }

    // ---------------- bulk checks ----------------

    [Fact]
    public void Each_image_is_checked_on_its_own_and_a_bad_one_names_its_reason()
    {
        var checks = UploadChecks.Check(
        [
            new UploadSource("a.png", 640, 480, Gradient(640, 480)),
            new UploadSource("b.gif", 16, 16, Gradient(16, 16)),
            new UploadSource("c.png", 0, 0, null, "decode failed"),
            new UploadSource("d.jpg", 9000, 100, new byte[4]),
            new UploadSource("e.bmp", 32, 16, Gradient(32, 16)),
        ]);
        Assert.Equal(5, checks.Count);
        Assert.True(checks[0].Ok);
        Assert.Equal((512, 512), (checks[0].TargetWidth, checks[0].TargetHeight));
        Assert.Equal(UploadRefusals.Format, checks[1].Refusal);
        Assert.Equal(UploadRefusals.Unreadable, checks[2].Refusal);
        Assert.Equal(UploadRefusals.TooLarge, checks[3].Refusal);
        Assert.True(checks[4].Ok);
        Assert.Equal((32, 16), (checks[4].TargetWidth, checks[4].TargetHeight));
    }

    // ---------------- cost ----------------

    [Fact]
    public void The_cost_is_the_grids_price_for_each_image_and_the_button_names_it_only_when_it_charges()
    {
        Assert.Equal(30, UploadCost.Total(10, 3));
        Assert.Equal(0, UploadCost.Total(0, 3));
        Assert.Equal(("upload.button.cost", (int?)30), UploadCost.Button(10, 3));
        Assert.Equal(("upload.button", (int?)null), UploadCost.Button(0, 3));
        Assert.Equal(("upload.button.checking", (int?)null), UploadCost.Button(null, 3));
    }

    // ---------------- the two capability steps ----------------

    [Fact]
    public void The_first_step_asks_for_a_texture_with_the_official_fields()
    {
        var folder = Guid.NewGuid();
        var body = LmvBuild.UploadRequestBody(new UploadRequest("Brick", "red wall", folder, 10));
        Assert.Equal("texture", body["asset_type"].AsString());
        Assert.Equal("texture", body["inventory_type"].AsString());
        Assert.Equal("Brick", body["name"].AsString());
        Assert.Equal("red wall", body["description"].AsString());
        Assert.Equal(folder, body["folder_id"].AsUUID().Guid);
        Assert.Equal(10, body["expected_upload_cost"].AsInteger());
        Assert.Equal((int)(LmvBuild.PermMove | LmvBuild.PermTransfer), body["next_owner_mask"].AsInteger());
        Assert.Equal(0, body["group_mask"].AsInteger());
        Assert.Equal(0, body["everyone_mask"].AsInteger());
    }

    [Fact]
    public void The_answers_are_read_as_the_grid_sends_them()
    {
        var t = LmvBuild.ParseTicket(new OSDMap { ["state"] = "upload", ["uploader"] = "http://example.org:9000/CAPS/up/", ["upload_price"] = 10 });
        Assert.True(t.Ok);
        Assert.Equal(new Uri("http://example.org:9000/CAPS/up/"), t.Uploader);
        Assert.Equal(10, t.Price);
        var free = LmvBuild.ParseTicket(new OSDMap { ["state"] = "upload", ["uploader"] = "http://example.org/u" });
        Assert.True(free.Ok);
        Assert.Equal(0, free.Price);
        var busy = LmvBuild.ParseTicket(new OSDMap { ["state"] = "error", ["error"] = new OSDMap { ["message"] = "Insuficient funds" } });
        Assert.False(busy.Ok);
        Assert.Equal("Insuficient funds", busy.Error);
        Assert.False(LmvBuild.ParseTicket(null).Ok);
        var asset = Guid.NewGuid();
        var item = Guid.NewGuid();
        var done = LmvBuild.ParseDone(new OSDMap { ["state"] = "complete", ["new_asset"] = asset.ToString(), ["new_inventory_item"] = new UUID(item) });
        Assert.True(done.Ok);
        Assert.Equal((asset, item), (done.AssetId, done.ItemId));
        Assert.False(LmvBuild.ParseDone(new OSDMap { ["state"] = "failed" }).Ok);
    }

    private sealed class FakeService : ITextureUploadService
    {
        public int Price;
        public List<string> Log = new();
        public HashSet<string> FailSend = new();
        public string? RefuseRequest;
        private int _n;

        public Task<UploadTicket> RequestAsync(UploadRequest request, CancellationToken ct)
        {
            Log.Add($"request {request.Name} cost {request.ExpectedCost}");
            if (RefuseRequest is { } r)
                return Task.FromResult(new UploadTicket(null, 0, r));
            return Task.FromResult(new UploadTicket(new Uri($"http://example.org/up/{++_n}/{request.Name}"), Price, null));
        }

        public Task<UploadDone> SendAsync(Uri uploader, byte[] data, CancellationToken ct)
        {
            string name = uploader.Segments[^1];
            Log.Add($"send {name} {data.Length}");
            return Task.FromResult(FailSend.Contains(name) ? new UploadDone(Guid.Empty, Guid.Empty, "failed") : new UploadDone(Guid.NewGuid(), Guid.NewGuid(), null));
        }
    }

    private static UploadJob Job(string name, int bytes = 3) => new(name, "", () => new byte[bytes]);

    [Fact]
    public async Task The_price_is_asked_first_and_nothing_is_sent_until_the_upload_runs()
    {
        var svc = new FakeService { Price = 10 };
        var batch = new UploadBatch(svc, Guid.NewGuid(), [Job("a"), Job("b"), Job("c")]);
        await batch.CheckPriceAsync(CancellationToken.None);
        Assert.Equal(10, batch.PricePerItem);
        Assert.Equal(30, batch.Total);
        Assert.Equal(["request a cost 0"], svc.Log);
        Assert.All(batch.States, s => Assert.Equal(UploadItemState.Waiting, s.State));
        await batch.RunAsync(CancellationToken.None);
        // the first image's answer is used for its own upload; the others are asked for in turn, each with the price shown
        Assert.Equal(["request a cost 0", "send a 3", "request b cost 10", "send b 3", "request c cost 10", "send c 3"], svc.Log);
        Assert.All(batch.States, s => Assert.Equal(UploadItemState.Done, s.State));
        Assert.All(batch.States, s => Assert.NotEqual(Guid.Empty, s.ItemId));
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_rest()
    {
        var svc = new FakeService { FailSend = { "b" } };
        var batch = new UploadBatch(svc, Guid.NewGuid(), [Job("a"), Job("b"), Job("c")]);
        await batch.CheckPriceAsync(CancellationToken.None);
        Assert.Equal(0, batch.PricePerItem);
        Assert.Equal(0, batch.Total);
        await batch.RunAsync(CancellationToken.None);
        Assert.Equal([UploadItemState.Done, UploadItemState.Failed, UploadItemState.Done], batch.States.Select(s => s.State));
        Assert.NotNull(batch.States[1].Error);
        // an encoder that throws fails only its own image
        var bad = new UploadBatch(new FakeService(), Guid.NewGuid(), [new UploadJob("x", "", () => throw new InvalidDataException("bad")), Job("y")]);
        await bad.CheckPriceAsync(CancellationToken.None);
        await bad.RunAsync(CancellationToken.None);
        Assert.Equal([UploadItemState.Failed, UploadItemState.Done], bad.States.Select(s => s.State));
    }

    [Fact]
    public async Task A_refused_price_check_is_shown_and_the_upload_does_not_run()
    {
        var svc = new FakeService { RefuseRequest = "Insuficient funds" };
        var batch = new UploadBatch(svc, Guid.NewGuid(), [Job("a")]);
        await batch.CheckPriceAsync(CancellationToken.None);
        Assert.Null(batch.PricePerItem);
        Assert.Equal("Insuficient funds", batch.PriceError);
        await batch.RunAsync(CancellationToken.None);
        Assert.DoesNotContain(svc.Log, l => l.StartsWith("send", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_old_first_answer_is_asked_for_again()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var svc = new FakeService();
        var batch = new UploadBatch(svc, Guid.NewGuid(), [Job("a")], () => now);
        await batch.CheckPriceAsync(CancellationToken.None);
        now += UploadBatch.TicketLifetime + TimeSpan.FromSeconds(1);
        await batch.RunAsync(CancellationToken.None);
        Assert.Equal(["request a cost 0", "request a cost 0", "send a 3"], svc.Log);
    }

    [Fact]
    public async Task A_price_that_changes_after_it_was_shown_stops_that_image()
    {
        var svc = new FakeService { Price = 0 };
        var batch = new UploadBatch(svc, Guid.NewGuid(), [Job("a"), Job("b")]);
        await batch.CheckPriceAsync(CancellationToken.None);
        svc.Price = 5;
        await batch.RunAsync(CancellationToken.None);
        Assert.Equal(UploadItemState.Done, batch.States[0].State);
        Assert.Equal(UploadItemState.Failed, batch.States[1].State);
        Assert.DoesNotContain("send b 3", svc.Log);
    }

    // ---------------- J2C ----------------

    [Fact]
    public void An_upload_encodes_and_decodes_back_within_the_lossy_tolerance()
    {
        foreach (bool alpha in new[] { false, true })
        {
            var px = Gradient(64, 64, alpha ? (byte)128 : (byte)255);
            var j2c = LmvTexture.EncodeUpload(64, 64, px, alpha);
            Assert.True(j2c.Length > 0 && j2c[0] == 0xFF && j2c[1] == 0x4F, "a raw codestream (SOC marker)");
            // the default rate is one eighth of the raw size
            Assert.True(j2c.Length <= 64 * 64 * (alpha ? 4 : 3) / 8 + 512, $"size {j2c.Length}");
            var back = LmvTexture.Decode(j2c);
            Assert.True(back.Ok, back.Refusal);
            Assert.Equal((64, 64), (back.Texture!.Width, back.Texture.Height));
            double sum = 0;
            int channels = alpha ? 4 : 3;
            for (int i = 0; i < px.Length; i++)
                if (i % 4 < channels)
                    sum += Math.Abs(px[i] - back.Texture.Rgba[i]);
            double mean = sum / (64 * 64 * channels);
            // a smooth gradient at 1 bit per component comes back within a few levels on average
            Assert.True(mean < 6, $"mean error {mean:F2} with alpha {alpha}");
        }
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(32, 16)]
    [InlineData(4, 64)]
    public void The_smallest_sizes_an_upload_arrives_at_encode_and_decode(int w, int h)
    {
        var px = Gradient(w, h);
        var j2c = LmvTexture.EncodeUpload(w, h, px, false);
        var back = LmvTexture.Decode(j2c);
        Assert.True(back.Ok, back.Refusal);
        Assert.Equal((w, h), (back.Texture!.Width, back.Texture.Height));
    }

    // ---------------- messages ----------------

    private static readonly UUID Agent = new(Guid.Parse("11111111-2222-4333-8444-555555555555"));
    private static readonly UUID Session = new(Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa"));

    [Fact]
    public void ObjectAdd_carries_the_default_box_as_the_official_create_tool_sends_it()
    {
        var target = Guid.NewGuid();
        var p = new Placement(new Vector3(1, 2, 3), new Vector3(4, 5, 6), target, false);
        var m = LmvBuild.ObjectAdd(Agent, Session, UUID.Zero, p);
        Assert.Equal(Agent, m.AgentData.AgentID);
        Assert.Equal(Session, m.AgentData.SessionID);
        var o = m.ObjectData;
        Assert.Equal(DefaultBox.PCodeVolume, o.PCode);
        Assert.Equal(DefaultBox.MaterialWood, o.Material);
        Assert.Equal(DefaultBox.AddFlagCreateSelected, o.AddFlags);
        Assert.Equal(DefaultBox.PathLine, o.PathCurve);
        Assert.Equal(DefaultBox.ProfileSquare, o.ProfileCurve);
        Assert.Equal(100, o.PathScaleX);
        Assert.Equal(100, o.PathScaleY);
        Assert.Equal(0, o.PathBegin);
        Assert.Equal(0, o.PathEnd);
        Assert.Equal(0, o.ProfileBegin);
        Assert.Equal(0, o.ProfileEnd);
        Assert.Equal(new LibreMetaverse.Vector3(0.5f, 0.5f, 0.5f), o.Scale);
        Assert.Equal(LibreMetaverse.Quaternion.Identity, o.Rotation);
        Assert.Equal(new LibreMetaverse.Vector3(1, 2, 3), o.RayStart);
        Assert.Equal(new LibreMetaverse.Vector3(4, 5, 6), o.RayEnd);
        Assert.Equal(target, o.RayTargetID.Guid);
        Assert.Equal(0, o.BypassRaycast);
        Assert.Equal(0, o.RayEndIsIntersection);
        Assert.Equal(0, o.State);
        Assert.Equal(1, LmvBuild.ObjectAdd(Agent, Session, UUID.Zero, p with { BypassRaycast = true }).ObjectData.BypassRaycast);
    }

    [Fact]
    public void RezObject_carries_the_ray_and_the_item_as_the_official_drop_sends_it()
    {
        var item = new InventoryItem(new UUID(Guid.NewGuid()))
        {
            Name = "Chair", Description = "wooden", AssetType = AssetType.Object, InventoryType = InventoryType.Object,
            ParentUUID = new UUID(Guid.NewGuid()), CreatorID = Agent, OwnerID = Agent, Flags = 7, SalePrice = 0, SaleType = SaleType.Not,
            CreationDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Permissions = new Permissions(baseMask: 0x7FFFFFFF, everyoneMask: 0, groupMask: 0, nextOwnerMask: 0x82000, ownerMask: 0x7FFFFFFF),
        };
        var p = new Placement(new Vector3(10, 10, 30), new Vector3(10, 12, 20), Guid.Empty, true);
        var m = LmvBuild.RezObject(Agent, Session, UUID.Zero, p, item, removeItem: true, item.ParentUUID);
        var r = m.RezData;
        Assert.Equal(UUID.Zero, r.FromTaskID);
        Assert.Equal(1, r.BypassRaycast);
        Assert.Equal(new LibreMetaverse.Vector3(10, 10, 30), r.RayStart);
        Assert.Equal(new LibreMetaverse.Vector3(10, 12, 20), r.RayEnd);
        Assert.False(r.RayEndIsIntersection);
        Assert.False(r.RezSelected);
        Assert.True(r.RemoveItem);
        Assert.Equal(7u, r.ItemFlags);
        Assert.Equal(0x82000u, r.NextOwnerMask);
        var d = m.InventoryData;
        Assert.Equal(item.UUID, d.ItemID);
        Assert.Equal(item.ParentUUID, d.FolderID);
        Assert.Equal("Chair", Utils.BytesToString(d.Name));
        Assert.Equal("wooden", Utils.BytesToString(d.Description));
        Assert.Equal((sbyte)AssetType.Object, d.Type);
        Assert.Equal((sbyte)InventoryType.Object, d.InvType);
        Assert.Equal(0x82000u, d.NextOwnerMask);
        Assert.Equal(Helpers.InventoryCRC((int)Utils.DateTimeToUnixTime(item.CreationDate), (byte)item.SaleType, (sbyte)item.InventoryType,
            (sbyte)item.AssetType, item.AssetUUID, item.GroupID, item.SalePrice, item.OwnerID, item.CreatorID, item.UUID, item.ParentUUID,
            (uint)item.Permissions.EveryoneMask, item.Flags, (uint)item.Permissions.NextOwnerMask, (uint)item.Permissions.GroupMask,
            (uint)item.Permissions.OwnerMask), d.CRC);
        Assert.False(LmvBuild.RezObject(Agent, Session, UUID.Zero, p, item, false, UUID.Zero).RezData.RemoveItem);
    }

    [Fact]
    public void DeRezObject_takes_to_the_objects_folder_and_deletes_to_the_trash()
    {
        var folder = new UUID(Guid.NewGuid());
        var txn = new UUID(Guid.NewGuid());
        var take = LmvBuild.DeRez(Agent, Session, 42, BuildRules.DeRezTakeIntoInventory, folder, txn);
        Assert.Equal(Agent, take.AgentData.AgentID);
        Assert.Equal(4, take.AgentBlock.Destination);
        Assert.Equal(folder, take.AgentBlock.DestinationID);
        Assert.Equal(txn, take.AgentBlock.TransactionID);
        Assert.Equal(1, take.AgentBlock.PacketCount);
        Assert.Equal(0, take.AgentBlock.PacketNumber);
        Assert.Equal(42u, Assert.Single(take.ObjectData).ObjectLocalID);
        Assert.Equal(6, LmvBuild.DeRez(Agent, Session, 42, BuildRules.DeRezTrash, folder, txn).AgentBlock.Destination);
    }

    // ---------------- enable rules ----------------

    [Fact]
    public void Take_and_delete_follow_the_official_rules()
    {
        uint own = ObjectFlagBits.YouOwner, mod = ObjectFlagBits.Modify, xfer = ObjectFlagBits.Transfer;
        Assert.True(BuildRules.CanTake(own, false, false));
        Assert.True(BuildRules.CanTake(mod | xfer, false, false));
        Assert.False(BuildRules.CanTake(mod, false, false));       // modifiable but not transferable: not someone else's to take
        Assert.False(BuildRules.CanTake(0, false, false));
        Assert.False(BuildRules.CanTake(own, true, false));        // worn
        Assert.False(BuildRules.CanTake(own, false, true));        // sitting on it
        Assert.True(BuildRules.CanDelete(own, false));
        Assert.True(BuildRules.CanDelete(mod, false));
        Assert.False(BuildRules.CanDelete(0, false));
        Assert.False(BuildRules.CanDelete(own, true));
        Assert.Null(BuildRules.DeleteConfirmation(locked: false, copyable: true, owned: true));
        Assert.Equal("ConfirmObjectDeleteLock", BuildRules.DeleteConfirmation(true, true, true));
        Assert.Equal("ConfirmObjectDeleteNoCopy", BuildRules.DeleteConfirmation(false, false, true));
        Assert.Equal("ConfirmObjectDeleteNoOwn", BuildRules.DeleteConfirmation(false, true, false));
        Assert.Equal("ConfirmObjectDeleteLockNoCopy", BuildRules.DeleteConfirmation(true, false, true));
        Assert.Equal("ConfirmObjectDeleteLockNoOwn", BuildRules.DeleteConfirmation(true, true, false));
        Assert.Equal("ConfirmObjectDeleteNoCopyNoOwn", BuildRules.DeleteConfirmation(false, false, false));
        Assert.Equal("ConfirmObjectDeleteLockNoCopyNoOwn", BuildRules.DeleteConfirmation(true, false, false));
        Assert.True(BuildRules.RezRemovesItem(itemCopyable: false, inTrash: false));
        Assert.True(BuildRules.RezRemovesItem(true, true));
        Assert.False(BuildRules.RezRemovesItem(true, false));
    }

    private static PickHit Hit(Vector3 at, uint local = 7) => new(new ObjectId(default, local), local, Guid.Parse("aaaaaaaa-0000-4000-8000-000000000007"), local, 0, default, default, at, Vector3.UnitZ, Vector3.UnitX, 5f);

    [Fact]
    public void Create_places_on_the_ground_at_the_spot_and_on_an_object_by_the_regions_ray()
    {
        var cam = new Vector3(100, 100, 30);
        var at = Vector3.Normalize(new Vector3(1, 0, -1));
        var land = BuildRules.CreatePlacement(new PickResult(PickKind.Land, null, null, new Vector3(110, 100, 20), 14f), cam, at, at, 0.1f);
        Assert.NotNull(land);
        Assert.True(land!.Value.BypassRaycast);
        Assert.Equal(new Vector3(110, 100, 20), land.Value.RayEnd);
        Assert.Equal(Guid.Empty, land.Value.RayTargetId);
        Assert.True(Vector3.Distance(cam + at * 0.11f, land.Value.RayStart) < 1e-4f, "starts just in front of the camera");
        var obj = BuildRules.CreatePlacement(new PickResult(PickKind.Object, Hit(new Vector3(105, 100, 25)), null, new Vector3(105, 100, 25), 7f), cam, at, at, 0.1f);
        Assert.NotNull(obj);
        Assert.False(obj!.Value.BypassRaycast);
        Assert.Equal(Hit(default).FullId, obj.Value.RayTargetId);
        Assert.True(Vector3.Distance(cam + at * (1 + BuildRules.MaxPlaceDistance), obj.Value.RayEnd) < 1e-3f, "the ray runs on to the limit");
        Assert.Null(BuildRules.CreatePlacement(new PickResult(PickKind.None, null, null, default, 0), cam, at, at, 0.1f));
        Assert.Null(BuildRules.CreatePlacement(new PickResult(PickKind.Avatar, null, null, new Vector3(101, 100, 29), 1f), cam, at, at, 0.1f));
        Assert.Null(BuildRules.CreatePlacement(new PickResult(PickKind.Land, null, null, new Vector3(300, 100, 20), 200f), cam, at, at, 0.1f));
    }

    [Fact]
    public void A_drop_rezzes_from_the_camera_to_the_spot_and_past_an_object()
    {
        var cam = new Vector3(100, 100, 30);
        var land = BuildRules.RezPlacement(new PickResult(PickKind.Land, null, null, new Vector3(110, 100, 20), 14f), cam);
        Assert.Equal(new Placement(cam, new Vector3(110, 100, 20), Guid.Empty, true), land);
        var obj = BuildRules.RezPlacement(new PickResult(PickKind.Object, Hit(new Vector3(105, 100, 25)), null, new Vector3(105, 100, 25), 7f), cam);
        Assert.Equal(new Placement(cam, new Vector3(110, 100, 20), Hit(default).FullId, false), obj);
        Assert.Null(BuildRules.RezPlacement(new PickResult(PickKind.None, null, null, default, 0), cam));
    }

    // ---------------- the Build menu ----------------

    [Fact]
    public void The_build_menu_has_the_official_names_order_and_shortcuts_and_only_working_entries()
    {
        var menus = new MenuModel();
        menus.AddMenu("world", "menu.world");
        menus.AddMenu("content", "menu.content");
        bool inWorld = false;
        BuildMenu.Install(menus, () => inWorld);
        Assert.Equal(["world", "build", "content"], menus.Menus.Select(m => m.Id));
        var build = menus.Menus.Single(m => m.Id == "build");
        Assert.Equal("menu.build", build.LabelKey);
        Assert.Equal([BuildMenu.CreateId, BuildMenu.UploadId], build.Entries.Select(e => e.Id));
        Assert.Equal("control|4", build.Entries[0].Shortcut);
        var upload = build.Entries[1];
        // Model... at its official place after Image..., Bulk models... right after it, then the official Bulk...
        Assert.Equal([BuildMenu.ImageId, BuildMenu.ModelId, BuildMenu.BulkModelsId, BuildMenu.BulkId], upload.Children!.Select(e => e.Id));
        Assert.Equal(["menu.build.upload.image", "menu.build.upload.model", "menu.build.upload.bulk_models", "menu.build.upload.bulk"], upload.Children!.Select(e => e.LabelKey));
        Assert.Equal("control|U", upload.Children![0].Shortcut);
        Assert.Equal("control|alt|U", upload.Children![1].Shortcut);   // Model..., the official alt+control+U
        Assert.All(upload.Children!.Skip(2), e => Assert.Null(e.Shortcut));
        Assert.All(upload.Children!, e => Assert.True(Viewer.Shell.StringTable.English().Has(e.LabelKey), e.LabelKey));
        Assert.Same(menus.ForShortcut("control|u"), upload.Children![0]);
        Assert.False(build.Entries[0].IsEnabled);
        Assert.False(upload.Children![0].IsEnabled);
        inWorld = true;
        Assert.True(build.Entries[0].IsEnabled);
        Assert.True(upload.Children![1].IsEnabled);
    }

    // ---------------- the controller ----------------

    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e11");

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public List<UiSoundSlot> Sounds = new();
        public InteractionController C;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), System.Numerics.Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new InteractionController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, Me,
                new SocialHooks(_ => { }, _ => { }, _ => { }, _ => { }, () => { }), _ => false) { PlaySound = Sounds.Add };
        }

        public void Prim(uint local, Vector3 pos, uint flags) =>
            World.ApplyObject(Sim, new PrimData(local, new Guid((int)local, 0, 0, new byte[8]), 0, 0, pos, System.Numerics.Quaternion.Identity, new Vector3(2, 2, 2),
                Interaction: new PrimInteraction(0, flags)));

        public PickResult Down(float x, float y) =>
            ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);
    }

    [Fact]
    public void Create_places_one_box_where_the_click_lands_and_plays_its_sound()
    {
        var r = new Rig();
        Assert.False(r.C.Place(r.Down(50, 50), new Vector3(50, 50, 60), -Vector3.UnitZ, -Vector3.UnitZ, 0.1f));   // not placing yet
        r.C.BeginCreate();
        Assert.True(r.C.Placing);
        Assert.True(r.C.Place(r.Down(50, 50), new Vector3(50, 50, 60), -Vector3.UnitZ, -Vector3.UnitZ, 0.1f));
        var create = Assert.IsType<CreateBoxIntent>(Assert.Single(r.Sent));
        Assert.True(Vector3.Distance(new Vector3(50, 50, 20), create.Placement.RayEnd) < 1e-4f);
        Assert.True(create.Placement.BypassRaycast);
        Assert.Equal([UiSoundSlot.ObjectCreate], r.Sounds);
        Assert.False(r.C.Placing);
    }

    [Fact]
    public void A_dropped_object_is_rezzed_with_its_sound()
    {
        var r = new Rig();
        var item = Guid.NewGuid();
        Assert.True(r.C.RezDrop(item, r.Down(50, 50), new Vector3(50, 50, 60)));
        var rez = Assert.IsType<RezItemIntent>(Assert.Single(r.Sent));
        Assert.Equal(item, rez.ItemId);
        Assert.True(Vector3.Distance(new Vector3(50, 50, 20), rez.Placement.RayEnd) < 1e-4f);
        Assert.Equal([UiSoundSlot.ObjectRezIn], r.Sounds);
    }

    [Fact]
    public void Take_and_delete_from_the_menu_send_take_plays_its_sound_delete_is_silent_and_the_question_comes_first_when_the_official_asks()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Copy | ObjectFlagBits.Move | ObjectFlagBits.Transfer);
        r.C.RightClick(r.Down(10, 10));
        r.Sent.Clear();
        var delete = Assert.Single(r.C.Entries, e => e.Id == "delete");
        Assert.True(delete.Enabled);
        Assert.Null(delete.Children);
        Assert.True(r.C.Choose("take"));
        Assert.Equal(new TakeIntent(1, false), r.Sent[^1]);
        Assert.True(r.C.Choose("delete"));
        Assert.Equal(new DeleteObjectIntent(1), r.Sent[^1]);
        Assert.Equal([UiSoundSlot.ObjectRezOut], r.Sounds);   // the official viewer plays no sound on delete (llvieweraudio.cpp only preloads it)

        // a locked object of the agent's own: Delete opens the official question, and only its confirm entry deletes
        var q = new Rig();
        q.Prim(2, new Vector3(10, 10, 30), ObjectFlagBits.YouOwner | ObjectFlagBits.Copy);
        q.C.RightClick(q.Down(10, 10));
        q.Sent.Clear();
        var asks = Assert.Single(q.C.Entries, e => e.Id == "delete");
        Assert.Equal(["menu.delete_confirm.ConfirmObjectDeleteLock", "menu.delete_ok"], asks.Children!.Select(c => c.LabelKey));
        Assert.False(asks.Children![0].Enabled);   // the question itself is text, not an action
        Assert.False(q.C.Choose("delete"));
        Assert.Empty(q.Sent);
        Assert.True(q.C.Choose("delete/confirm"));
        Assert.Equal(new DeleteObjectIntent(2), Assert.Single(q.Sent));

        // someone else's object that may not be modified: Take and Delete are greyed
        var g = new Rig();
        g.Prim(3, new Vector3(10, 10, 30), 0);
        g.C.RightClick(g.Down(10, 10));
        Assert.False(Assert.Single(g.C.Entries, e => e.Id == "take").Enabled);
        Assert.False(Assert.Single(g.C.Entries, e => e.Id == "delete").Enabled);
        g.Sent.Clear();
        Assert.False(g.C.Choose("delete"));
        Assert.Empty(g.Sent);
    }

    [Fact]
    public void The_new_intents_check_themselves()
    {
        var ok = new Placement(Vector3.One, Vector3.Zero, Guid.Empty, true);
        Assert.Empty(new CreateBoxIntent(ok).Validate());
        Assert.NotEmpty(new CreateBoxIntent(ok with { RayEnd = new Vector3(float.NaN, 0, 0) }).Validate());
        Assert.NotEmpty(new RezItemIntent(Guid.Empty, ok).Validate());
        Assert.Empty(new RezItemIntent(Guid.NewGuid(), ok).Validate());
        Assert.NotEmpty(new DeleteObjectIntent(0).Validate());
        Assert.Equal(new DeleteObjectCommand(5), new DeleteObjectIntent(5).ToCommand());
        Assert.True(LmvTransport.IsKnownCommand(new CreateObjectCommand(ok)));
        Assert.True(LmvTransport.IsKnownCommand(new RezItemCommand(Guid.NewGuid(), ok)));
        Assert.True(LmvTransport.IsKnownCommand(new DeleteObjectCommand(5)));
    }
}
