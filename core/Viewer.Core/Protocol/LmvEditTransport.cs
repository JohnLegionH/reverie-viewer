// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>The edit tools through the library: each edit intent becomes its message (<see cref="LmvEdit"/>) to the agent's region, and
/// the region's simulator features give the edit limits.</summary>
internal sealed partial class LmvTransport
{
    private CommandDispatch ExecuteEdit(Simulator sim, ActionIntent edit)
    {
        var self = _client.Self;
        Packet? packet = edit switch
        {
            SetObjectNameIntent n => LmvEdit.ObjectName(self.AgentID, self.SessionID, n.LocalId, n.Name),
            SetObjectDescriptionIntent d => LmvEdit.ObjectDescription(self.AgentID, self.SessionID, d.LocalId, d.Description),
            SetObjectPermissionsIntent p => LmvEdit.ObjectPermissions(self.AgentID, self.SessionID, p.LocalIds, p.Field, p.Set, p.Mask),
            SetObjectLockedIntent l => LmvEdit.Lock(self.AgentID, self.SessionID, l.LocalIds, l.Locked),
            UpdateObjectIntent u => LmvEdit.Update(self.AgentID, self.SessionID, u.LocalId, u.Position, u.Rotation, u.Scale, u.Linkset, u.Uniform),
            SetObjectFlagsIntent f => LmvEdit.Flags(self.AgentID, self.SessionID, f.LocalId, f.Physical, f.Temporary, f.Phantom),
            SetObjectShapeIntent s => LmvEdit.Shape(self.AgentID, self.SessionID, s.LocalId, s.Shape),
            LinkObjectsIntent k => LmvEdit.Link(self.AgentID, self.SessionID, k.OrderedRoots),
            DelinkObjectsIntent k => LmvEdit.Delink(self.AgentID, self.SessionID, k.LocalIds),
            DuplicateObjectsIntent c => LmvEdit.Duplicate(self.AgentID, self.SessionID, self.ActiveGroup, c.Roots, c.Offset, c.Flags),
            _ => null,
        };
        if (packet is null)
            return CommandDispatch.NotSent("unknown_edit");
        _client.Network.SendPacket(packet, sim);
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
