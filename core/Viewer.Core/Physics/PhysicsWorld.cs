// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using JoltPhysicsSharp;

namespace Viewer.Core.Physics;

/// <summary>
/// A minimal Jolt physics system: create, step, destroy. It manages the Jolt binding the way the grid does
/// (ADR-009): JoltPhysicsSharp 2.19.1 with the patched joltc, a process-global ref-counted Foundation, one shared
/// job pool created by the first world in and destroyed by the last world out, and the grid's teardown fix
/// workaround for the 2.19.1 job-pool thread leak.
/// </summary>
public sealed class PhysicsWorld : IDisposable
{
    private const int JoltMaxJobs = 2048;    // PhysicsSettings.h cMaxPhysicsJobs
    private const int JoltMaxBarriers = 8;   // PhysicsSettings.h cMaxPhysicsBarriers
    private const uint ObjectLayerCount = 2; // 0 = static, 1 = moving
    private const uint BroadPhaseLayerCount = 2;

    // Foundation.Init / Foundation.Shutdown are process-global, and so is the job pool. Ref-counted: Init and the
    // pool on the 0 -> 1 transition, pool teardown and Shutdown on the 1 -> 0 transition.
    private static readonly object s_foundationGate = new();
    private static int s_refCount;
    private static JobSystemThreadPool? s_pool;

    /// <summary>
    /// TEST FLAG ONLY. When false, the last world out disposes the job pool the stock way (the 2.19.1 behaviour that
    /// leaks the pool's worker threads). Exists so the lifecycle smoke can prove its check fails without that fix.
    /// </summary>
    internal static bool DestroyNativeJobSystemAtTeardown { get; set; } = true;

    /// <summary>
    /// TEST FLAG ONLY. When false, the world disposes its PhysicsSystem the stock way (the 2.19.1 behaviour that never
    /// destroys the native system, leaking it and the patched joltc's per-system 8 MB TempAllocator every cycle).
    /// </summary>
    internal static bool DestroyNativePhysicsSystemAtTeardown { get; set; } = true;

    private readonly object _gate = new();
    private ObjectLayerPairFilterTable? _objectLayerPairFilter;
    private BroadPhaseLayerInterfaceTable? _broadPhaseInterface;
    private ObjectVsBroadPhaseLayerFilterTable? _objectVsBroadPhaseFilter;
    private PhysicsSystem? _system;
    private bool _disposed;

    public PhysicsWorld(int jobThreads = -1)
    {
        lock (s_foundationGate)
        {
            if (s_refCount == 0)
            {
                if (!Foundation.Init(false))
                    throw new InvalidOperationException("Jolt Foundation.Init(false) failed (native joltc not loaded).");
                s_pool = new JobSystemThreadPool(new JobSystemThreadPoolConfig
                {
                    maxJobs = JoltMaxJobs,
                    maxBarriers = JoltMaxBarriers,
                    numThreads = jobThreads,
                });
            }
            s_refCount++;
        }

        _objectLayerPairFilter = new ObjectLayerPairFilterTable(ObjectLayerCount);
        _objectLayerPairFilter.EnableCollision(new ObjectLayer(0), new ObjectLayer(1));
        _objectLayerPairFilter.EnableCollision(new ObjectLayer(1), new ObjectLayer(1));

        _broadPhaseInterface = new BroadPhaseLayerInterfaceTable(ObjectLayerCount, BroadPhaseLayerCount);
        _broadPhaseInterface.MapObjectToBroadPhaseLayer(new ObjectLayer(0), new BroadPhaseLayer(0));
        _broadPhaseInterface.MapObjectToBroadPhaseLayer(new ObjectLayer(1), new BroadPhaseLayer(1));

        _objectVsBroadPhaseFilter = new ObjectVsBroadPhaseLayerFilterTable(
            _broadPhaseInterface, BroadPhaseLayerCount, _objectLayerPairFilter, ObjectLayerCount);

        _system = new PhysicsSystem(new PhysicsSystemSettings
        {
            MaxBodies = 1024,
            MaxBodyPairs = 1024,
            MaxContactConstraints = 1024,
            ObjectLayerPairFilter = _objectLayerPairFilter,
            BroadPhaseLayerInterface = _broadPhaseInterface,
            ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhaseFilter,
        });
    }

    /// <summary>Diagnostics: what the binding says about ownership of each native handle (NativeObject.OwnsHandle).</summary>
    internal string DescribeHandleOwnership()
    {
        static string One(string name, NativeObject? o) => $"{name}.OwnsHandle={(o == null ? "n/a" : s_ownsHandle?.GetValue(o))}";
        return string.Join(" ", One("PhysicsSystem", _system), One("ObjectLayerPairFilterTable", _objectLayerPairFilter),
            One("BroadPhaseLayerInterfaceTable", _broadPhaseInterface), One("ObjectVsBroadPhaseLayerFilterTable", _objectVsBroadPhaseFilter),
            One("JobSystemThreadPool", s_pool));
    }

    /// <summary>Steps the world once; returns false if Jolt reported an update error.</summary>
    public bool Step(float deltaTime)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _system!.Update(deltaTime, 1, s_pool!) == PhysicsUpdateError.None;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            // Native teardown order, as the grid: system -> filters; then jobs -> Foundation below.
            if (_system != null)
            {
                if (DestroyNativePhysicsSystemAtTeardown)
                    DestroyPhysicsSystem(_system);
                else
                    _system.Dispose();
            }
            _system = null;
            _objectVsBroadPhaseFilter?.Dispose();
            _objectVsBroadPhaseFilter = null;
            _broadPhaseInterface?.Dispose();
            _broadPhaseInterface = null;
            _objectLayerPairFilter?.Dispose();
            _objectLayerPairFilter = null;
        }

        lock (s_foundationGate)
        {
            if (s_refCount > 0 && --s_refCount == 0)
            {
                // Last world out: the job pool goes BEFORE Foundation.
                if (s_pool != null)
                {
                    if (DestroyNativeJobSystemAtTeardown)
                        DestroyJobSystem(s_pool);
                    else
                        s_pool.Dispose();
                }
                s_pool = null;
                Foundation.Shutdown();
            }
        }
    }

    // =====================================================================
    // The grid's fix, mirrored (its physics module's commit a8e918b3cf: destroy the job pools'
    // native job systems at teardown, the JoltPhysicsSharp 2.19.1 leak; its JoltPhysicsBackend.cs).
    //
    // JoltPhysicsSharp 2.19.1's JobSystem sets its handle through the parameterless NativeObject constructor,
    // so NativeObject.OwnsHandle stays false and JobSystemThreadPool.Dispose skips DisposeNative - i.e.
    // JPH_JobSystem_Destroy is never called and the pool's workers are never stopped or joined (the finalizer
    // path checks the same flag). Jolt's own destructor (~JobSystemThreadPool -> StopThreads) joins them,
    // so we call joltc's existing JPH_JobSystem_Destroy export ourselves, then dispose the wrapper (which,
    // OwnsHandle being false, destroys nothing a second time). If a future binding sets OwnsHandle, we skip
    // our destroy and its Dispose does it. No native change: the export ships in joltc.dll.
    // =====================================================================
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void JPH_JobSystem_Destroy(IntPtr jobSystem);

    // NativeObject.OwnsHandle is `protected internal` in the binding - read it by reflection.
    private static readonly PropertyInfo? s_ownsHandle = typeof(NativeObject).GetProperty(
        "OwnsHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static void DestroyJobSystem(JobSystemThreadPool pool)
    {
        // Unknown binding shape (no such property): assume it does NOT own the handle, as 2.19.1 does not.
        bool bindingDestroys = s_ownsHandle?.GetValue(pool) is true;
        if (!bindingDestroys && !pool.IsDisposed && pool.Handle != IntPtr.Zero)
            JPH_JobSystem_Destroy(pool.Handle);   // ~JobSystemThreadPool joins the workers
        pool.Dispose();
    }

    // =====================================================================
    // viewer extension of that fix: NOT in the grid (its commit c0193d1bbf destroys only the job pools).
    //
    // The same 2.19.1 defect hits PhysicsSystem: measured, PhysicsSystem.OwnsHandle=False, so
    // PhysicsSystem.Dispose never calls JPH_PhysicsSystem_Destroy. Each create/destroy cycle then leaked the
    // native system plus the patched joltc's per-system TempAllocator (~8.6 MiB private bytes per cycle). Same
    // mechanism as the grid's fix: call joltc's existing export ourselves when the binding does not own the handle, then
    // dispose the wrapper, which destroys nothing a second time. (BroadPhaseLayerInterfaceTable also reports
    // OwnsHandle=False, but joltc 1.0.4 exports no destroy for it; it is a few bytes and left alone.)
    // =====================================================================
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void JPH_PhysicsSystem_Destroy(IntPtr system);

    private static void DestroyPhysicsSystem(PhysicsSystem system)
    {
        bool bindingDestroys = s_ownsHandle?.GetValue(system) is true;
        if (!bindingDestroys && !system.IsDisposed && system.Handle != IntPtr.Zero)
            JPH_PhysicsSystem_Destroy(system.Handle);
        system.Dispose();
    }
}
