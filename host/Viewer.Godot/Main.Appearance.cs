// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Viewer.Core.Appearance;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The Appearance window in the running viewer: installed with the first session, bound to each session's inventory while it lives, and
/// given the avatar definition (read from the character folder) for its sliders. While an edit is open the own avatar is
/// drawn with the edited shape (a local preview, nothing sent); a saved edit stays on screen until the rebuilt look arrives.
/// </summary>
public partial class Main
{
    private AppearancePanel? _appearancePanel;
    private EditParamCatalog? _editCatalog;
    private string? _editCatalogFolder;
    private VisualParamSet? _lastPreview;
    private int _previewHold;   // 1 once a save went through: the preview then ends with the next look from the simulator
    private int _saveCount;     // saves that have finished in this session (read by the development command)

    /// <summary>The editor's parameter catalog, read once from the character folder's avatar definition (null without one).</summary>
    private EditParamCatalog? EditCatalog()
    {
        var folder = CharacterFolder();
        if (folder is null)
            return null;
        if (_editCatalog is not null && _editCatalogFolder == folder)
            return _editCatalog;
        try
        {
            _editCatalog = EditParamCatalog.Parse(File.ReadAllBytes(Path.Combine(folder, "avatar_lad.xml")));
            _editCatalogFolder = folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            Log.Warn("appearance.definition", CoreLog.F("state", "unreadable"), CoreLog.F("error", ex.GetType().Name));
            _editCatalog = null;
        }
        return _editCatalog;
    }

    private void AttachAppearance(ViewerSession session)
    {
        if (_shell is null)
            return;
        _appearancePanel ??= AppearanceFeature.Install(_shell);
        var catalog = EditCatalog();
        var self = session.AgentId;
        _lastPreview = null;
        Interlocked.Exchange(ref _previewHold, 0);
        _appearancePanel.Bind(session.Inventory, session.Actions.Submit, session.EditItemInfo, session.FetchEditAssetAsync, catalog,
            () => session.World.Appearance(self)?.Sex == "male", session.TextureAssetOf, weights => ApplyPreview(session, catalog, weights),
            id => session.EditItemInfo(id)?.FullRights == true);
        session.EditedItemSaveDone += OnEditedItemSaved;
    }

    private void DetachAppearance(ViewerSession? session)
    {
        if (session is not null)
        {
            session.EditedItemSaveDone -= OnEditedItemSaved;
            session.World.SetSelfPreview(session.AgentId, null);
        }
        _appearancePanel?.Bind(null, null);
        _lastPreview = null;
    }

    private void OnEditedItemSaved(WearableSaveFinished done)
    {
        Interlocked.Increment(ref _saveCount);
        if (done.Saved)
            Interlocked.Exchange(ref _previewHold, 1);
        _appearancePanel?.SaveFinished(done);
    }

    /// <summary>The edited weights, or null when the edit ends: the own avatar's shape is drawn from them (nothing is sent).</summary>
    private void ApplyPreview(ViewerSession session, EditParamCatalog? catalog, IReadOnlyDictionary<int, float>? weights)
    {
        var self = session.AgentId;
        if (weights is null || catalog is null || session.World.Appearance(self)?.VisualParams is not { } received)
        {
            bool keep = Interlocked.Exchange(ref _previewHold, 0) == 1 && _lastPreview is not null;
            session.World.SetSelfPreview(self, keep ? _lastPreview : null, releaseOnNextAppearance: true);
            if (!keep)
                _lastPreview = null;
            return;
        }
        Interlocked.Exchange(ref _previewHold, 0);
        _lastPreview = PreviewParams.Apply(received, catalog, weights);
        session.World.SetSelfPreview(self, _lastPreview);
    }

#if NV_AUTOMATION
    /// <summary>The appearance command (development builds only). Reading answers counts, ids and kinds, never a name. Editing and saving a
    /// wearable is refused unless the run says a scratch test account is in use (<c>NV_OUTFIT_SCRATCH_OK=1</c>).</summary>
    public object? AppearanceCall(string action, string? text, Guid id, double a, double b, double c, int param)
    {
        if (_appearancePanel is not { Editing: { } ed } panel || _inventoryFor is not { } session)
            return null;
        object State()
        {
            var look = session.World.Appearance(session.AgentId);
            var last = session.LastEditedItemSave;
            string BakeHash(AvatarAppearance? x) => x is null ? "" : string.Join(",", x.Bakes.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.ToString("N")[..8]));
            // one avatar's look as ids and figures: versions, the bake ids, a hash of the visual parameters, the Height byte and the drawn body height
            object Look(AvatarAppearance x)
            {
                byte? height = x.VisualParams.TryGet(33, out var hb) ? hb : null;
                float? bodyHeight = null;
                if (Body() is { IsCompletedSuccessfully: true, Result: { } loaded } && loaded.Body.Shape is { } def)
                    bodyHeight = Viewer.Core.Avatars.AvatarShape.From(new Viewer.Core.Avatars.AvatarRig(loaded.Body.Skeleton), def, x.VisualParams).Measure(null)?.Height;
                return new
                {
                    version = x.AppearanceVersion, cof_version = x.CofVersion, bakes = BakeHash(x),
                    params_hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(x.VisualParams.Raw.ToArray()))[..12],
                    height_byte = height, body_height = bodyHeight is { } bh ? Math.Round(bh, 3) : (double?)null,
                };
            }
            return new
            {
                worn = ed.Worn().Select(w => new { id = w.TargetId.ToString(), type = (int)w.Type }).ToArray(),
                state = ed.State.ToString(), reason = ed.Reason, item = ed.ItemId.ToString(), save_count = Volatile.Read(ref _saveCount),
                type = ed.Editor is { } e ? (int)e.Type : -1, dirty = ed.HasUnsavedChanges, can_modify = ed.Editor?.CanModify ?? false,
                can_save = ed.CanSave, can_save_as = ed.CanSaveAs, save_state = ed.SaveState.ToString(), save_reason = ed.SaveReason,
                last_save = last is null ? null : new { as_copy = last.AsCopy, saved = last.Saved, rebaked = last.Rebaked, reason = last.Reason, item = last.ItemId.ToString() },
                preview = session.World.DrawnVisualParams(session.AgentId) is { } drawn && look is not null && !drawn.SameAs(look.VisualParams),
                values = ed.Editor is { } ed2 ? ed2.Values.Where(kv => kv.Key is 33 or 34 or 803 or 804 or 805).Select(kv => new { id = kv.Key, value = Math.Round(kv.Value, 3) }).ToArray() : [],
                look = look is null ? null : Look(look),
                item_asset = ed.ItemId == Guid.Empty ? "" : session.EditItemInfo(ed.ItemId)?.AssetId.ToString("N")[..8] ?? "",
                others = session.World.Appearances().Where(x => x.AgentId != session.AgentId).Select(x => (object)new { agent = x.AgentId.ToString(), look = Look(x) }).ToArray(),
            };
        }
        if (action == "appearance")
            return State();
        if (action == "ap_info")
        {
            if (text == "*texture" && Viewer.Shell.Appearance.TexturePicker.Rows(session.Inventory, "").FirstOrDefault() is { } firstTexture)
                id = firstTexture.ItemId;
            var info = session.EditItemInfo(id);
            if (info is null)
                return new { known = false, item = id.ToString() };
            var pending = session.FetchEditAssetAsync(info.AssetId, info.AssetType, CancellationToken.None);
            var fetched = pending.Wait(TimeSpan.FromSeconds(30)) ? pending.Result : null;
            Viewer.Core.Appearance.WearableAsset.TryParse(fetched is { Status: Viewer.Core.Assets.AssetFetchStatus.Ok } f ? f.Bytes.Span : [], out var parsed, out _);
            return new
            {
                known = true, item = id.ToString(), asset = info.AssetId.ToString(), asset_type = info.AssetType, can_modify = info.CanModify, can_copy = info.CanCopy,
                fetch = fetched?.Status.ToString(), bytes = fetched?.Bytes.Length ?? 0, wearable_type = (int)parsed.Type, params_in_file = parsed.Params.Count, textures_in_file = parsed.Textures.Count,
                values = parsed.Params.Where(kv => kv.Key is 33 or 803 or 804 or 805 or 840 or 841).Select(kv => new { id = kv.Key, value = Math.Round(kv.Value, 3) }).ToArray(),
            };
        }
        if (Environment.GetEnvironmentVariable("NV_OUTFIT_SCRATCH_OK") != "1")
            return new { refused = "scratch_only" };
        switch (action)
        {
            case "ap_open":
            {
                var wanted = id != Guid.Empty ? ed.Worn().Where(w => w.TargetId == id).ToList()
                    : ed.Worn().Where(w => string.Equals(Viewer.Core.Appearance.WearableKinds.Name(w.Type), text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (wanted.Count != 1)
                    return new { matched = wanted.Count, opened = false };
                bool done = ed.OpenAsync(wanted[0].TargetId).Wait(TimeSpan.FromSeconds(30));
                return new { matched = 1, opened = done && ed.State == Viewer.Shell.Appearance.EditState.Ready, state = ed.State.ToString(), reason = ed.Reason };
            }
            case "ap_set":
                if (ed.Editor is not { } es) return new { ok = false, why = "no_editor" };
                es.SetValue(param, (float)a);
                return new { ok = true, value = es.Value(param), dirty = es.IsDirty };
            case "ap_colour":
            {
                if (ed.Editor is not { } ec || Viewer.Core.Appearance.WearableKinds.Swatches(ec.Type).FirstOrDefault() is not { } swatch) return new { ok = false, why = "no_swatch" };
                ec.SetColour(swatch, (float)a, (float)b, (float)c);
                var (r, g, bl) = ec.Colour(swatch);
                return new { ok = true, r, g, b = bl, dirty = ec.IsDirty };
            }
            case "ap_texture":
            {
                if (ed.Editor is not { } et) return new { ok = false, why = "no_editor" };
                if (text is { } literal && literal.StartsWith("asset:", StringComparison.Ordinal) && Guid.TryParse(literal["asset:".Length..], out var literalAsset))
                {
                    // a texture by its asset id (a texture every grid accepts in a wearable, such as the viewer's default ones)
                    et.SetTexture(param, literalAsset);
                    return new { ok = et.Texture(param) == literalAsset, dirty = et.IsDirty, asset = literalAsset.ToString("N")[..8] };
                }
                bool first = text is "*first" or "*valid";   // the first row of the picker's list (no name needed); "*valid": the first the grid's baker can decode
                var rows = id != Guid.Empty ? Viewer.Shell.Appearance.TexturePicker.Rows(session.Inventory, "").Where(r => r.ItemId == id).ToList()
                    : first ? Viewer.Shell.Appearance.TexturePicker.Rows(session.Inventory, "", i => session.EditItemInfo(i)?.FullRights == true).Where(r => !r.InLibrary && session.TextureAssetOf(r.ItemId) != Guid.Empty).Take(1).ToList()
                    : Viewer.Shell.Appearance.TexturePicker.Rows(session.Inventory, text ?? "").Where(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rows.Count != 1)
                    return new { ok = false, matched = rows.Count };
                if (text == "*valid")
                {
                    // the first of the account's own textures with every right whose file is a usable JPEG 2000 picture (a seeded file that is not one makes the grid's bake fail)
                    var candidates = Viewer.Shell.Appearance.TexturePicker.Rows(session.Inventory, "", i => session.EditItemInfo(i)?.FullRights == true).Where(r => !r.InLibrary).ToList();
                    rows = [];
                    int tried = 0;
                    foreach (var r in candidates)
                    {
                        var a2 = session.TextureAssetOf(r.ItemId);
                        if (a2 == Guid.Empty) continue;
                        tried++;
                        var fetch = session.FetchEditAssetAsync(a2, 0, CancellationToken.None);
                        if (fetch.Wait(TimeSpan.FromSeconds(30)) && fetch.Result is { Status: Viewer.Core.Assets.AssetFetchStatus.Ok } got && Viewer.Core.Imaging.J2kGuard.Validate(got.Bytes.Span, null, out _) is null)
                        {
                            rows = [r];
                            break;
                        }
                    }
                    if (rows.Count != 1)
                        return new { ok = false, matched = 0, tried };
                }
                var asset = session.TextureAssetOf(rows[0].ItemId);
                if (asset == Guid.Empty)
                    return new { ok = false, why = "no_asset" };
                et.SetTexture(param, asset);
                return new { ok = et.Texture(param) == asset, dirty = et.IsDirty, asset = asset.ToString("N")[..8] };
            }
            case "ap_prepare":
            {
                bool ready = Viewer.Shell.Appearance.TexturePicker.Prepare(session.Inventory);
                var rows = Viewer.Shell.Appearance.TexturePicker.Rows(session.Inventory, "");
                return new { ready, textures = rows.Count, in_library = rows.Count(r => r.InLibrary), with_asset = rows.Count(r => session.TextureAssetOf(r.ItemId) != Guid.Empty),
                    full_rights = rows.Count(r => session.EditItemInfo(r.ItemId)?.FullRights == true), full_rights_own = rows.Count(r => !r.InLibrary && session.EditItemInfo(r.ItemId)?.FullRights == true) };
            }
            case "ap_save":
                return new { submitted = ed.Save(), save_state = ed.SaveState.ToString(), reason = ed.SaveReason };
            case "ap_saveas":
                return new { submitted = ed.SaveAs(text ?? ""), save_state = ed.SaveState.ToString(), reason = ed.SaveReason };
            case "ap_revert":
                ed.Editor?.Revert();
                return new { dirty = ed.HasUnsavedChanges };
            case "ap_close":
                return new { verdict = ed.RequestClose().ToString(), state = ed.State.ToString() };
            case "ap_discard":
                ed.DiscardAndClose();
                return new { state = ed.State.ToString(), dirty = ed.HasUnsavedChanges };
            default:
                return null;
        }
    }
#endif
}
