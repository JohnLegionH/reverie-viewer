// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Core.Diagnostics;
using Viewer.Core.Physics;

// Physics lifecycle smoke (ADR-009).
//   Viewer.Core.LifecycleSmoke [--cycles N] [--sample-every K] [--no-workaround] [--no-system-destroy]
//     --no-workaround      disables the job pools' native destroy at teardown: the thread leak returns
//     --no-system-destroy  disables the PhysicsSystem native destroy: the per-system memory leak returns
// Prints one SAMPLE line per measurement and a final VERDICT line; exit code 0 = PASS, 1 = FAIL, 2 = error.
// The measurement and the PASS bounds live in Viewer.Core.Diagnostics.PhysicsLifecycleProbe (shared with S-1).

int cycles = 1000;
int sampleEvery = 100;
bool workaround = true;
bool systemDestroy = true;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cycles": cycles = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--sample-every": sampleEvery = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--no-workaround": workaround = false; break;
        case "--no-system-destroy": systemDestroy = false; break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}

PhysicsWorld.DestroyNativeJobSystemAtTeardown = workaround;
PhysicsWorld.DestroyNativePhysicsSystemAtTeardown = systemDestroy;
Console.WriteLine($"CONFIG cycles={cycles} sample_every={sampleEvery} jolt7e={(workaround ? "on" : "off")} system_destroy={(systemDestroy ? "on" : "off")} " +
                  $"cpus={Environment.ProcessorCount} os=\"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\" " +
                  $"runtime={Environment.Version}");

try
{
    var result = PhysicsLifecycleProbe.Run(cycles, sampleEvery, Console.WriteLine);
    return result.Pass ? 0 : 1;
}
catch (Exception e)
{
    Console.WriteLine($"ERROR {e.GetType().Name}: {e.Message}");
    return 2;
}
