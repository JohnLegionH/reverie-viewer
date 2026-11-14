// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Runtime.InteropServices;

namespace Viewer.Godot;

/// <summary>
/// Test only (<c>--tr-crash-test=&lt;kind&gt;</c> on a <c>--test-render</c> run): ends the process in a native crash on
/// purpose, so the tools' handling of a process that dies without a word (the crash dump the launcher asks the .NET runtime
/// for, and the exit code it reports) can be checked. Nothing in a normal run reaches this.
///   access-violation   writes to an address that is not mapped (0xC0000005)
///   heap-corruption    frees a block of the process heap twice (the heap manager ends the process with 0xC0000374)
///   managed            an unhandled exception on a thread-pool thread (the runtime's own crash path)
/// </summary>
internal static class CrashTest
{
    public const string Argument = "--tr-crash-test=";

    public static void Run(string kind)
    {
        Console.Error.WriteLine($"TEST-RENDER crash test: {kind}");
        Console.Error.Flush();
        switch (kind)
        {
            case "access-violation":
                unsafe { *(int*)0x7FF000000000L = 1; }
                break;
            case "heap-corruption":
            {
                IntPtr heap = GetProcessHeap();
                IntPtr block = HeapAlloc(heap, 0, 64);
                HeapFree(heap, 0, block);
                HeapFree(heap, 0, block);
                break;
            }
            case "managed":
                System.Threading.ThreadPool.QueueUserWorkItem(_ => throw new InvalidOperationException("crash test"));
                System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                break;
            default:
                Console.Error.WriteLine($"TEST-RENDER crash test: unknown kind '{kind}' (access-violation, heap-corruption, managed)");
                break;
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetProcessHeap();
    [DllImport("kernel32.dll")] private static extern IntPtr HeapAlloc(IntPtr heap, uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr block);
}
