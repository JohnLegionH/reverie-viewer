// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Saving a notecard or gesture as the official viewer posts it (llviewerassetupload.cpp LLBufferedAssetUploadInfo::generatePostBody):
/// item_id for the inventory, task_id as well for a card inside an object; the uploader's answer counts as saved only when its state is
/// "complete". Pure, runs in parallel.
/// </summary>
public class EditorUploadTests
{
    private static readonly Guid Item = Guid.Parse("c0c0c0c0-0000-4000-8000-000000000001");
    private static readonly Guid Prim = Guid.Parse("b0b0b0b0-0000-4000-8000-000000000002");

    [Fact]
    public void A_card_in_the_inventory_names_its_item_only()
    {
        var body = LmvTransport.UploadBody(Item, Guid.Empty);
        Assert.Equal(Item, body["item_id"].AsUUID().Guid);
        Assert.False(body.ContainsKey("task_id"));
    }

    [Fact]
    public void A_card_inside_an_object_names_the_object_as_the_task()
    {
        var body = LmvTransport.UploadBody(Item, Prim);
        Assert.Equal(Item, body["item_id"].AsUUID().Guid);
        Assert.Equal(Prim, body["task_id"].AsUUID().Guid);
    }

    [Fact]
    public void Only_a_complete_answer_is_a_save_and_it_names_the_new_asset()
    {
        var asset = Guid.NewGuid();
        var ok = LmvTransport.ParseUploadAnswer(new OSDMap { ["state"] = "complete", ["new_asset"] = OSD.FromUUID(new LibreMetaverse.UUID(asset)) });
        Assert.Equal(NotecardSaveStatus.Saved, ok.Status);
        Assert.Equal(asset, ok.NewAssetId);
        var refused = LmvTransport.ParseUploadAnswer(new OSDMap { ["state"] = "failure", ["message"] = "no permission" });
        Assert.Equal(NotecardSaveStatus.Failed, refused.Status);
        Assert.Equal("no permission", refused.Reason);
        Assert.Equal(NotecardSaveStatus.Failed, LmvTransport.ParseUploadAnswer(new OSDArray()).Status);
    }
}
