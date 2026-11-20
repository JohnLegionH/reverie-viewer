// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Viewer.Core.Avatars;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Compiles the presentation code's methods on a thread of its own, before the main thread first needs them. The first time the main thread
/// runs a method the runtime compiles it there, inside the frame: measured, the first frame of a crowd's arrival took 56 to 66 ms for that
/// alone (the first body added, the first bake upload, the first shape, the first rigged mesh built). The thread does the same compiling
/// ahead of time (<see cref="RuntimeHelpers.PrepareMethod(RuntimeMethodHandle)"/>), starts once, ends when done and is a background thread.
/// Only methods that are not generic are prepared (a generic method needs its type arguments).
/// </summary>
public static class JitWarmup
{
    private static int _started;

    /// <summary>Milliseconds the warm-up took, null until it has finished; and how many methods it prepared.</summary>
    public static double? Milliseconds { get; private set; }
    public static int Methods { get; private set; }

    /// <summary>True when the warm-up has finished.</summary>
    public static bool Done => Milliseconds is not null;

    /// <summary>True from the start until the warm-up has finished.</summary>
    public static bool Running => Volatile.Read(ref _started) != 0 && !Done;

    /// <summary>Starts the warm-up once per process (later calls do nothing).</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var thread = new Thread(Run) { IsBackground = true, Name = "jit-warmup", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private static readonly string[] Namespaces =
    [
        "Viewer.Godot.Presentation", "Viewer.Core.Avatars", "Viewer.Core.Meshing", "Viewer.Core.Residency", "Viewer.Core.Imaging", "Viewer.Core.World", "Viewer.Shell",
    ];

    private static void Run()
    {
        long t0 = Stopwatch.GetTimestamp();
        int count = 0;
        foreach (var assembly in new[] { typeof(JitWarmup).Assembly, typeof(AvatarBody).Assembly, typeof(PresentationCoordinator).Assembly })
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t is not null)!; }
            foreach (var type in types)
            {
                if (type.IsGenericTypeDefinition || type.ContainsGenericParameters || !InScope(type)) continue;
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters || (m.GetMethodBody() is null)) continue;
                    try { RuntimeHelpers.PrepareMethod(m.MethodHandle); count++; }
                    catch (Exception) { /* a method that cannot be prepared ahead is compiled when it is first run, as before */ }
                }
            }
        }
        Methods = count;
        Milliseconds = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    private static bool InScope(Type type)
    {
        var ns = (type.IsNested ? type.DeclaringType!.Namespace : type.Namespace) ?? "";
        foreach (var n in Namespaces)
            if (ns == n) return true;
        return false;
    }
}
