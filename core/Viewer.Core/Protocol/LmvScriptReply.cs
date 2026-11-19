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

internal static class LmvScriptEngineReply
{
    /// <summary>Reads the answer of the SetScriptEngine capability: {state, result, EngineChoice} on success ("complete") and {state, result,
    /// error} on failure. A missing EngineChoice reads as empty. Not ok when the HTTP status failed or the state is not "complete".</summary>
    internal static ScriptEngineReply Read(OSD? body, bool httpOk)
    {
        if (body is not OSDMap map)
            return ScriptEngineReply.NotSent;
        string Text(string key) => map.TryGetValue(key, out var v) && v.Type != OSDType.Unknown ? v.AsString() : "";
        bool ok = httpOk && Text("state") == "complete";
        return new ScriptEngineReply(ok, Text("result"), Text("EngineChoice"), Text("error"));
    }
}

/// <summary>Keeps the richer replies the event-queue tap has seen, by script (object, item), until the library's own event for the same reply
/// takes them.</summary>
internal sealed class LmvScriptReplyKeeper
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), ScriptRuntime> _replies = new();

    /// <summary>Keeps the Engine and EngineChoice of every ScriptRunningReply in an event-queue answer (called by the tap before the library
    /// parses the answer).</summary>
    internal void Note(OSD answer)
    {
        if (_replies.Count > 256)
            _replies.Clear();   // replies nobody asked for
        foreach (var (obj, item, runtime) in LmvScriptReply.Read(answer))
            _replies[(obj, item)] = runtime;
    }

    /// <summary>The kept reply for a script, taken out; <paramref name="fallback"/> (the library's own flags) when the tap saw none.</summary>
    internal ScriptRuntime Take(Guid objectId, Guid itemId, Func<ScriptRuntime> fallback) =>
        _replies.TryRemove((objectId, itemId), out var full) ? full : fallback();
}
