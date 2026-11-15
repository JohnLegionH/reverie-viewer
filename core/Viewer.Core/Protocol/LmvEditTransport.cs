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
            _ => null,
        };
        if (packet is null)
            return CommandDispatch.NotSent("unknown_edit");
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
