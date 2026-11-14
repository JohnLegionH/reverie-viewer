// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Runtime.InteropServices;

namespace Viewer.Godot;

/// <summary>
/// What the operating system lets a process present at, read for a measuring run: whether Windows is throttling the process
/// (power throttling, which the system applies to a process it does not see in the foreground), and whether the
/// display stack can present without waiting for the next refresh (the DXGI "allow tearing" feature, which a windowed
/// swap chain needs to leave the compositor's pacing). Windows only; elsewhere every reading is "unknown".
/// </summary>
public static class PresentDiagnostics
{
    private const int ProcessPowerThrottling = 4;          // PROCESS_INFORMATION_CLASS
    private const uint ThrottlingExecutionSpeed = 0x1;     // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    private const uint ThrottlingIgnoreTimerResolution = 0x4;   // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState info, int size);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    /// <summary>The throttling controls this process has set and which of them are on. Both masks are 0 when the process
    /// leaves everything to the system (the default), in which case the system may throttle it.</summary>
    public static (string Mode, uint ControlMask, uint StateMask) ThrottlingState()
    {
        if (!OperatingSystem.IsWindows()) return ("unknown", 0, 0);
        try
        {
            var s = new PowerThrottlingState { Version = 1 };
            if (!GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref s, Marshal.SizeOf<PowerThrottlingState>()))
                return ("unknown", 0, 0);
            // a control bit with its state bit clear is an explicit opt-out; no control bit leaves the choice to the system
            string mode = (s.ControlMask & ThrottlingExecutionSpeed) == 0 ? "system-decides"
                : (s.StateMask & ThrottlingExecutionSpeed) == 0 ? "opted-out" : "forced-on";
            return (mode, s.ControlMask, s.StateMask);
        }
        catch (Exception) { return ("unknown", 0, 0); }
    }

    /// <summary>Tells Windows not to throttle this process's execution speed or its timer resolution. Returns whether the call was accepted.</summary>
    public static bool OptOutOfThrottling()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var s = new PowerThrottlingState { Version = 1, ControlMask = ThrottlingExecutionSpeed | ThrottlingIgnoreTimerResolution, StateMask = 0 };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref s, Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception) { return false; }
    }

    // IDXGIFactory5::CheckFeatureSupport is slot 28 of the interface (IUnknown 3, IDXGIObject 4, IDXGIFactory 5,
    // IDXGIFactory1 2, IDXGIFactory2 11, IDXGIFactory3 1, IDXGIFactory4 2, then IDXGIFactory5's own first method).
    private const int CheckFeatureSupportSlot = 28;
    private const int FeaturePresentAllowTearing = 0;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CheckFeatureSupportFn(IntPtr self, int feature, ref int data, int size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr obj);

    /// <summary>True or false when the system answers; null when it cannot be asked.</summary>
    public static bool? TearingSupported()
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr f1 = IntPtr.Zero, f5 = IntPtr.Zero;
        try
        {
            var iid1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid1, out f1) < 0 || f1 == IntPtr.Zero) return null;
            var iid5 = new Guid("7632e1f5-ee65-4dca-87fd-84cd75f8838d");   // IDXGIFactory5
            var qi = Slot<QueryInterfaceFn>(f1, 0);
            if (qi(f1, ref iid5, out f5) < 0 || f5 == IntPtr.Zero) return null;
            int allowed = 0;
            if (Slot<CheckFeatureSupportFn>(f5, CheckFeatureSupportSlot)(f5, FeaturePresentAllowTearing, ref allowed, sizeof(int)) < 0) return null;
            return allowed != 0;
        }
        catch (Exception) { return null; }
        finally
        {
            if (f5 != IntPtr.Zero) Slot<ReleaseFn>(f5, 2)(f5);
            if (f1 != IntPtr.Zero) Slot<ReleaseFn>(f1, 2)(f1);
        }
    }

    private static T Slot<T>(IntPtr com, int index) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(com);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, index * IntPtr.Size));
    }
}
