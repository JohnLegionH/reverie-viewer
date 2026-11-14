// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Mono.Cecil;
using Mono.Cecil.Cil;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Structural guards for the session and its transport, each with its negative control:
///   - ADR-006: no static field in Viewer.Core can hold per-session state (a writable static, or a static mutable
///     collection); the arch fixture's deliberate static counter must be caught.
///   - hazard 3: nothing in Viewer.Core calls a LibreMetaverse appearance-send or bake API;
///     the same scan over LibreMetaverse itself must find such calls.
///   - The adapter's settings are appearance-passive.
/// </summary>
public class SessionInvariantTests(ITestOutputHelper output)
{
    private static readonly string[] MutableCollections =
    [
        "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.List`1", "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.Queue`1", "System.Collections.Generic.Stack`1", "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Concurrent.ConcurrentDictionary`2", "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentBag`1", "System.Runtime.CompilerServices.ConditionalWeakTable`2",
    ];

    /// <summary>
    /// Reviewed process-wide statics that are not session state: PhysicsWorld's Jolt foundation refcount and shared
    /// job pool (the native library is initialised once per process) and its two negative-control switches.
    /// LmvTexture's once-per-process flag for wrapping CoreJ2K's own process-wide message logger (it drops
    /// the expected "Codestream truncated" warning of prefix decodes; no session data).
    /// AllocationMeter's three thread-statics, the counted work running on the
    /// current thread (its meter, kind and nested bytes); per thread and per piece of work, never per session.
    /// </summary>
    private static readonly string[] ProcessWideAllowed =
    [
        "Viewer.Core.Physics.PhysicsWorld::s_refCount",
        "Viewer.Core.Physics.PhysicsWorld::s_pool",
        "Viewer.Core.Physics.PhysicsWorld::<DestroyNativeJobSystemAtTeardown>k__BackingField",
        "Viewer.Core.Physics.PhysicsWorld::<DestroyNativePhysicsSystemAtTeardown>k__BackingField",
        "Viewer.Core.Protocol.LmvTexture/QuietTruncationWarnings::s_installed",
        "Viewer.Core.Diagnostics.AllocationMeter::t_meter",
        "Viewer.Core.Diagnostics.AllocationMeter::t_kind",
        "Viewer.Core.Diagnostics.AllocationMeter::t_nested",
        // the tree meshes: derived from a species and a scale alone and never changed afterwards, so one copy serves every session
        "Viewer.Core.World.TreeMesh::Registry",
    ];

    internal static List<string> StaticStateViolations(string assemblyPath)
    {
        var found = new List<string>();
        using var asm = AssemblyDefinition.ReadAssembly(assemblyPath);
        foreach (var type in asm.MainModule.GetTypes())
        {
            // Compiler-generated closure/cache classes (<>c) and <PrivateImplementationDetails> hold no session data.
            if (type.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute") || type.Name.StartsWith('<'))
                continue;
            foreach (var f in type.Fields.Where(f => f.IsStatic && !f.IsLiteral))
            {
                if (ProcessWideAllowed.Contains($"{type.FullName}::{f.Name}"))
                    continue;
                var ft = f.FieldType is GenericInstanceType g ? g.ElementType.FullName : f.FieldType.FullName;
                if (!f.IsInitOnly)
                    found.Add($"{type.FullName}::{f.Name} is a writable static");
                else if (f.FieldType.IsArray || MutableCollections.Contains(ft))
                    found.Add($"{type.FullName}::{f.Name} is a static mutable collection ({f.FieldType.Name})");
            }
        }
        return found;
    }

    [Fact]
    public void Viewer_Core_has_no_static_that_can_hold_per_session_state()
    {
        var v = StaticStateViolations(TestPaths.CoreDll);
        foreach (var line in v)
            output.WriteLine(line);
        Assert.Empty(v);
    }

    [Fact]
    public void Negative_control_the_fixtures_static_session_counter_is_caught()
    {
        var v = StaticStateViolations(TestPaths.ArchFixtureDll);
        foreach (var line in v)
            output.WriteLine(line);
        Assert.Contains(v, s => s.Contains("SessionCounter") && s.Contains("writable static"));
        Assert.Contains(v, s => s.Contains("SessionsById") && s.Contains("mutable collection"));
    }

    private static readonly string[] AppearanceSends = ["RequestAgentSetAppearance", "SendAgentSetAppearance", "RequestSetAppearance", "set_SendAppearance"];

    internal static List<string> AppearanceCalls(string assemblyPath, bool includeSettingFalse)
    {
        var found = new List<string>();
        using var asm = AssemblyDefinition.ReadAssembly(assemblyPath);
        foreach (var type in asm.MainModule.GetTypes())
            foreach (var m in type.Methods.Where(m => m.HasBody))
            {
                var ins = m.Body.Instructions;
                for (int i = 0; i < ins.Count; i++)
                {
                    // SendAppearance is a field in LibreMetaverse 3.1.6 (stfld); the sends are methods.
                    if (ins[i].Operand is FieldReference fr && fr.Name == "SendAppearance" && ins[i].OpCode == OpCodes.Stfld)
                    {
                        // Setting it to false (ldc.i4.0 right before) is the passive default, not a send.
                        if (!includeSettingFalse && i > 0 && ins[i - 1].OpCode == OpCodes.Ldc_I4_0)
                            continue;
                        found.Add($"{type.FullName}::{m.Name} -> stfld {fr.DeclaringType.Name}.{fr.Name}");
                        continue;
                    }
                    if (ins[i].Operand is not MethodReference mr)
                        continue;
                    // a bake type of the networking library (its baker); the core's own pure lookups of the bake placeholder ids and slots
                    // (Viewer.Core.*: BakeChannels, BakeOnMesh, BakeAlpha) send nothing and are not what this scan is about
                    bool bake = (mr.DeclaringType.Name.Contains("Baker") || mr.DeclaringType.Name.Contains("Bake"))
                        && !(mr.DeclaringType.Namespace ?? "").StartsWith("Viewer.Core", StringComparison.Ordinal);
                    if (!AppearanceSends.Contains(mr.Name) && !bake)
                        continue;
                    // Setting SendAppearance = false (ldc.i4.0 right before the setter) is the passive default, not a send.
                    if (mr.Name == "set_SendAppearance" && !includeSettingFalse && i > 0 && ins[i - 1].OpCode == OpCodes.Ldc_I4_0)
                        continue;
                    found.Add($"{type.FullName}::{m.Name} -> {mr.DeclaringType.Name}.{mr.Name}");
                }
            }
        return found;
    }

    /// <summary>The only code that may bake, upload a bake or send an appearance. A send is reached through the client-bake controller, which
    /// refuses it in a region that bakes on the server (ClientBakeControllerTests); this scan keeps every other call site out.</summary>
    internal static readonly string[] ClientBakePath = ["ClientBakePipeline", "SetAppearanceBuilder", "LibraryCompositor", "SendLook", "RunBakeAsync", "UploadBakeAsync", "LmvBakeEnvironment", "ResendLook", "DryBakeToFolder", "BakeDryRun"];   // the last two are the development bake, which keeps its bakes instead of uploading them (BakeDryRunTests)

    [Fact]
    public void Nothing_in_the_core_sends_appearance_or_bakes_outside_the_client_bake_path()
    {
        var calls = AppearanceCalls(TestPaths.CoreDll, includeSettingFalse: false);
        foreach (var c in calls)
            output.WriteLine(c);
        Assert.Empty(calls.Where(c => !ClientBakePath.Any(c.Contains)));
        // Not vacuous: the scan sees the client-bake path's own calls, and the passive setting itself.
        Assert.Contains(calls, c => c.Contains("ClientBakePipeline"));
        Assert.Contains(AppearanceCalls(TestPaths.CoreDll, includeSettingFalse: true), c => c.Contains("SendAppearance"));
    }

    [Fact]
    public void Negative_control_the_same_scan_finds_appearance_sends_inside_LibreMetaverse()
    {
        string lmv = typeof(LibreMetaverse.GridClient).Assembly.Location;
        var calls = AppearanceCalls(lmv, includeSettingFalse: false);
        output.WriteLine($"{calls.Count} appearance-send call sites in LibreMetaverse, e.g. {calls.FirstOrDefault()}");
        Assert.NotEmpty(calls);
    }

    [Fact]
    public void The_adapter_client_is_appearance_passive_and_var_region_safe()
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        var s = t.Client.Settings;
        Assert.False(s.Agent.SendAppearance);
        Assert.False(s.AssetCache.Enabled);
        Assert.False(s.World.StoreLandPatches);
        Assert.False(s.Agent.MultipleSims);
        Assert.True(s.World.AlwaysDecodeObjects);
        Assert.True(s.World.AlwaysRequestObjects);
        Assert.False(s.Parcel.PoolParcelData);   // the process-global SimulatorDataPool stays off
        Assert.False(s.World.CachePrimitives);
    }
}
