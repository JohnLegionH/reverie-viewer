// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;
using Viewer.Core.Inventory;

namespace Viewer.Core.Protocol;

/// <summary>Reads ScriptRunningReply events out of an event-queue answer, with the fields the protocol library does not keep (Engine and
/// EngineChoice, which a region that saves an engine choice sends beside the official Mono, Luau and LuauLanguage).</summary>
internal static class LmvScriptReply
{
    /// <summary>Every ScriptRunningReply in an event-queue answer: the object, the script item and what the region said of it.</summary>
    internal static IReadOnlyList<(Guid ObjectId, Guid ItemId, ScriptRuntime Runtime)> Read(OSD answer)
    {
        var found = new List<(Guid, Guid, ScriptRuntime)>();
        if (answer is not OSDMap map || !map.TryGetValue("events", out var list) || list is not OSDArray events)
            return found;
        foreach (var ev in events)
        {
            if (ev is not OSDMap e || e["message"].AsString() != "ScriptRunningReply" || e["body"] is not OSDMap body || body["Script"] is not OSDArray scripts)
                continue;
            foreach (var s in scripts)
            {
                if (s is not OSDMap script)
                    continue;
                bool Flag(string key) => script.TryGetValue(key, out var v) && v.AsBoolean();
                string Text(string key) => script.TryGetValue(key, out var v) ? v.AsString() : "";
                found.Add((script["ObjectID"].AsUUID().Guid, script["ItemID"].AsUUID().Guid,
                    new ScriptRuntime(Flag("Running"), Flag("Mono"), Flag("Luau"), Flag("LuauLanguage"), Text("Engine"), Text("EngineChoice"))));
            }
        }
        return found;
    }
}
