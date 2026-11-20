// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>The edit tools through the library: each edit intent becomes its message (<see cref="LmvEdit"/>) to the agent's region, and
/// the region's simulator features give the edit limits.</summary>
internal sealed partial class LmvTransport
{
    private CommandDispatch ExecuteEdit(Simulator sim, ActionIntent edit)
    {
        var self = _client.Self;
        if (edit is SetFaceMaterialIntent material)
            return SendMaterial(sim, material);
        if (edit is RequestPhysicsIntent physics)
            return RequestPhysics(sim, physics);
        if (edit is RequestObjectCostIntent cost)
            return RequestCost(sim, cost);
        if (edit is ReturnObjectsIntent ret)
        {
            var transaction = UUID.Random();
            foreach (var part in LmvBuild.ReturnToOwner(self.AgentID, self.SessionID, self.ActiveGroup, ret.Roots, transaction))
                _client.Network.SendPacket(part, sim);
            return CommandDispatch.Handed;
        }
        if (edit is GiveItemToObjectIntent or CopyObjectItemIntent)
            return MoveItem(sim, edit);
        if (LmvFeatures.Packet(self.AgentID, self.SessionID, self.ActiveGroup, edit) is { } feature)
        {
            _client.Network.SendPacket(feature, sim);
            return CommandDispatch.Handed;
        }
        Packet? packet = edit switch
        {
            SetObjectFacesIntent f => LmvEdit.Image(self.AgentID, self.SessionID, f.LocalId,
                sim.ObjectsPrimitives.TryGetValue(f.LocalId, out var prim) ? prim.Textures : null, f.Faces),
            SetObjectNameIntent n => LmvEdit.ObjectName(self.AgentID, self.SessionID, n.LocalId, n.Name),
            SetObjectDescriptionIntent d => LmvEdit.ObjectDescription(self.AgentID, self.SessionID, d.LocalId, d.Description),
            SetObjectPermissionsIntent p => LmvEdit.ObjectPermissions(self.AgentID, self.SessionID, p.LocalIds, p.Field, p.Set, p.Mask),
            SetObjectLockedIntent l => LmvEdit.Lock(self.AgentID, self.SessionID, l.LocalIds, l.Locked),
            UpdateObjectIntent u => LmvEdit.Update(self.AgentID, self.SessionID, u.LocalId, u.Position, u.Rotation, u.Scale, u.Linkset, u.Uniform),
            SetObjectFlagsIntent f => LmvEdit.Flags(self.AgentID, self.SessionID, f.LocalId, f.Physical, f.Temporary, f.Phantom),
            SetObjectShapeIntent s => LmvEdit.Shape(self.AgentID, self.SessionID, s.LocalId, s.Shape),
            LinkObjectsIntent k => LmvEdit.Link(self.AgentID, self.SessionID, k.OrderedRoots),
            DelinkObjectsIntent k => LmvEdit.Delink(self.AgentID, self.SessionID, k.LocalIds),
            DeselectObjectsIntent d => LmvEdit.Deselect(self.AgentID, self.SessionID, d.LocalIds),
            UndoObjectsIntent u => LmvEdit.Undo(self.AgentID, self.SessionID, self.ActiveGroup, u.ObjectIds),
            RedoObjectsIntent r => LmvEdit.Redo(self.AgentID, self.SessionID, self.ActiveGroup, r.ObjectIds),
            DuplicateObjectsIntent c => LmvEdit.Duplicate(self.AgentID, self.SessionID, self.ActiveGroup, c.Roots, c.Offset, c.Flags),
            DuplicateOnRayIntent c => LmvEdit.DuplicateOnRay(self.AgentID, self.SessionID, self.ActiveGroup, c.Roots, c.Placement, c.CopyCenters, c.CopyRotates, c.Flags),
            SetClickActionIntent c => LmvEdit.ClickAction(self.AgentID, self.SessionID, c.LocalIds, c.Action),
            SetObjectSaleIntent c => LmvEdit.SaleInfo(self.AgentID, self.SessionID, c.Roots, c.SaleType, c.Price),
            _ => null,
        };
        if (packet is null)
            return CommandDispatch.NotSent("unknown_edit");
        if (edit is SetObjectFacesIntent faces)
            _log.Info("edit.faces-sent", CoreLog.F("faces", faces.Faces.Count), CoreLog.F("known", sim.ObjectsPrimitives.ContainsKey(faces.LocalId)));
        _client.Network.SendPacket(packet, sim);
        return CommandDispatch.Handed;
    }

    /// <summary>A PBR material put on or taken off faces (with an override when given) through the region's ModifyMaterialParams
    /// capability, as the official viewer's LLGLTFMaterialList sends it; a region without the capability cannot take one.</summary>
    private CommandDispatch SendMaterial(Simulator sim, SetFaceMaterialIntent m)
    {
        Uri? cap = null;
        try { cap = sim.Caps?.CapabilityURI("ModifyMaterialParams"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        if (cap is null)
            return CommandDispatch.NotSent("no_material_service");
        var body = LmvEdit.MaterialParams(new UUID(m.ObjectId), m.Faces, new UUID(m.MaterialId), m.OverrideJson);
        _ = Task.Run(async () =>
        {
            try
            {
                var (_, data) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, body, CancellationToken.None).ConfigureAwait(false);
                bool ok = data is not null && OSDParser.Deserialize(data) is OSDMap reply && reply.TryGetValue("success", out var s) && s.AsBoolean();
                if (!ok)
                    _log.Warn("edit.material-refused", CoreLog.F("faces", m.Faces.Count));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("edit.material-failed", CoreLog.F("error", ex.GetType().Name));
            }
        });
        return CommandDispatch.Handed;
    }

    /// <summary>An inventory item put into a prim (a script through RezScript, anything else through UpdateTaskInventory), or a prim's item
    /// copied out into the inventory folder for its type (MoveTaskInventory).</summary>
    private CommandDispatch MoveItem(Simulator sim, ActionIntent edit)
    {
        var inv = _client.Inventory;
        switch (edit)
        {
            case GiveItemToObjectIntent give:
            {
                if (inv.Store is not { } store || !store.TryGetValue(new UUID(give.ItemId), out var found) || found is not InventoryItem item)
                    return CommandDispatch.NotSent("no_item");
                if (give.Script)
                    inv.CopyScriptToTask(give.LocalId, item, true, sim);
                else
                    inv.UpdateTaskInventory(give.LocalId, item, sim, false);
                return CommandDispatch.Handed;
            }
            case CopyObjectItemIntent copy:
            {
                // the folder for the item's type (llpanelobjectinventory.cpp's move into the folder for the asset type), else the inventory's top
                var type = (FolderType)copy.AssetType;
                UUID folder = copy.FolderId != Guid.Empty ? new UUID(copy.FolderId)
                    : Enum.IsDefined(type) && type != FolderType.None ? inv.FindFolderForType(type) : UUID.Zero;
                if (folder == UUID.Zero)
                    folder = inv.Store?.RootFolder?.UUID ?? UUID.Zero;
                if (folder == UUID.Zero)
                    return CommandDispatch.NotSent("no_inventory");
                inv.MoveTaskInventory(copy.LocalId, new UUID(copy.ItemId), folder, sim);
                return CommandDispatch.Handed;
            }
        }
        return CommandDispatch.NotSent("unknown_edit");
    }

    /// <summary>The objects' physics settings through the region's GetObjectPhysicsData capability; each answer becomes an event.</summary>
    private CommandDispatch RequestPhysics(Simulator sim, RequestPhysicsIntent r)
    {
        Uri? cap = null;
        try { cap = sim.Caps?.CapabilityURI("GetObjectPhysicsData"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        if (cap is null)
            return CommandDispatch.NotSent("no_physics_service");
        var body = LmvFeatures.PhysicsRequest(r.ObjectIds);
        _ = Task.Run(async () =>
        {
            try
            {
                var (_, data) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, body, CancellationToken.None).ConfigureAwait(false);
                if (data is null || data.Length > UploadAnswerBytes)
                    return;
                foreach (var (id, physics) in LmvFeatures.ParsePhysics(OSDParser.Deserialize(data)))
                    Raise(new ObjectPhysicsReceived(id, 0, physics));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("edit.physics-failed", CoreLog.F("error", ex.GetType().Name));
            }
        });
        return CommandDispatch.Handed;
    }

    /// <summary>The objects' land impact through the region's GetObjectCost capability (llviewerobjectlist.cpp fetchObjectCostsCoro): each
    /// answer becomes an event. A region without the capability gives none, and the window shows nothing for it.</summary>
    private CommandDispatch RequestCost(Simulator sim, RequestObjectCostIntent r)
    {
        Uri? cap = null;
        try { cap = sim.Caps?.CapabilityURI("GetObjectCost"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        if (cap is null)
            return CommandDispatch.NotSent("no_cost_service");
        var body = LmvFeatures.PhysicsRequest(r.ObjectIds);
        _ = Task.Run(async () =>
        {
            try
            {
                var (_, data) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, body, CancellationToken.None).ConfigureAwait(false);
                if (data is null || data.Length > UploadAnswerBytes)
                    return;
                foreach (var (id, objectCost, linksetCost) in LmvFeatures.ParseCosts(OSDParser.Deserialize(data)))
                    Raise(new ObjectCostReceived(id, objectCost, linksetCost));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("edit.cost-failed", CoreLog.F("error", ex.GetType().Name));
            }
        });
        return CommandDispatch.Handed;
    }

    /// <summary>The region's physics settings for an object it sends on its own (after a change).</summary>
    private void OnPhysicsProperties(object? sender, PhysicsPropertiesEventArgs e)
    {
        var p = e.PhysicsProperties;
        var id = e.Simulator?.ObjectsPrimitives.TryGetValue(p.LocalID, out var prim) == true ? prim.ID.Guid : Guid.Empty;
        if (id == Guid.Empty)
            return;
        Raise(new ObjectPhysicsReceived(id, p.LocalID, FeatureRules.Clamp(new PhysicsParams((byte)p.PhysicsShapeType, p.GravityMultiplier, p.Friction, p.Density, p.Restitution))));
    }

    /// <summary>The edit limits of the agent's region: the official ones with the grid's own from its simulator features' OpenSim extras.</summary>
    public EditLimits EditLimits()
    {
        var extras = new Dictionary<string, double>();
        try
        {
            if (_client.Network.CurrentSim?.Features?.Get("OpenSimExtras") is OSDMap map)
                foreach (var key in new[] { "MinPrimScale", "MaxPrimScale", "MinPhysPrimScale", "MaxPhysPrimScale", "MinSimHeight", "MaxSimHeight" })
                    if (map.TryGetValue(key, out var v) && v.Type is OSDType.Real or OSDType.Integer)
                        extras[key] = v.AsReal();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return Building.EditLimits.FromExtras(extras);
    }
}
