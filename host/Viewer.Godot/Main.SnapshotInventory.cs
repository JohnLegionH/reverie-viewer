// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;

namespace Viewer.Godot;

/// <summary>
/// Saving the snapshot on show to the inventory: the grid's fee is shown first, and the picture is uploaded as a texture, at a size the
/// grid takes, only when it is agreed to. It goes to the Photo Album folder (the Textures folder where there is none), as the official
/// viewer's does. Nothing here runs from frame to frame.
/// </summary>
public partial class Main
{
    private SnapshotSave? _snapshotSave;

    private void DropSnapshotUpload()
    {
        _snapshotSave?.Cancel();
        _snapshot?.ShowFee(null);
    }

    private async Task AskSnapshotFee()
    {
        if (_snapshot is not { } window || _shell is null || _snapshotShot is not { } shot)
            return;
        var strings = _shell.Shell.Strings;
        if (_interactFor is not { IsEnded: false } session || session.BuildService is not { } service)
        {
            window.ShowFee(strings.Get("snapshot.inventory.no_service"));
            return;
        }
        if (_snapshotSave is { State: SnapshotSaveState.AskingPrice or SnapshotSaveState.Uploading })
            return;
        var folder = session.Inventory.FolderOfKind(FolderKind.Snapshot);
        if (folder == Guid.Empty)
            folder = session.Inventory.FolderOfKind(FolderKind.Texture);
        // the pixels are copied here, on the engine's thread; scaling and encoding happen off it
        byte[] pixels;
        int width = shot.GetWidth(), height = shot.GetHeight();
        using (var copy = (Image)shot.Duplicate())
        {
            copy.Convert(Image.Format.Rgba8);
            pixels = copy.GetData();
        }
        var (toWidth, toHeight) = UploadImageRules.TargetSize(width, height);
        var job = new UploadJob(SnapshotSave.NameFor(DateTime.Now), string.Empty, () =>
        {
            var scaled = UploadChecks.Scale(pixels, width, height, toWidth, toHeight);
            for (int i = 3; i < scaled.Length; i += 4) scaled[i] = 255;   // a picture of the window has nothing see-through in it
            return Viewer.Core.Protocol.LmvTexture.EncodeUpload(toWidth, toHeight, scaled, false);
        });
        var save = _snapshotSave = new SnapshotSave(service, folder);
        window.ShowFee(strings.Get("snapshot.inventory.checking"));
        await save.AskAsync(job, default);
        if (_snapshotSave != save || save.State == SnapshotSaveState.Idle)
            return;
        Log.Info("snapshot.inventory-price", CoreLog.F("state", save.State.ToString()), CoreLog.F("price", save.Price ?? -1),
            CoreLog.F("width", toWidth), CoreLog.F("height", toHeight));
        if (save.State == SnapshotSaveState.Priced)
            window.ShowFee(strings.Get("snapshot.inventory.fee", save.Price ?? 0, toWidth, toHeight), canConfirm: true);
        else
            window.ShowFee(SnapshotRefusal(save));
    }

    private async Task UploadSnapshot()
    {
        if (_snapshot is not { } window || _shell is null || _snapshotSave is not { State: SnapshotSaveState.Priced } save)
            return;
        var strings = _shell.Shell.Strings;
        window.ShowFee(strings.Get("snapshot.inventory.uploading"));
        await save.ConfirmAsync(default);
        Log.Info("snapshot.inventory-upload", CoreLog.F("state", save.State.ToString()), CoreLog.F("item", save.ItemId.ToString()));
        if (_snapshotSave == save)
            window.ShowFee(save.State == SnapshotSaveState.Saved ? strings.Get("snapshot.inventory.saved") : SnapshotRefusal(save));
    }

    private string SnapshotRefusal(SnapshotSave save)
    {
        var strings = _shell!.Shell.Strings;
        return save.Reason switch
        {
            "no_folder" or "no_service" => strings.Get("snapshot.inventory.no_service"),
            "price_changed" => strings.Get("snapshot.inventory.price_changed"),
            var reason => strings.Get("snapshot.inventory.refused", reason ?? string.Empty),
        };
    }
}
