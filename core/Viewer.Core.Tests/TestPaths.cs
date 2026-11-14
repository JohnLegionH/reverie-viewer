// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;

namespace Viewer.Core.Tests;

/// <summary>Paths baked in at build time (see the AssemblyMetadata items in the csproj).</summary>
internal static class TestPaths
{
    private static string Meta(string key) =>
        typeof(TestPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"assembly metadata {key} is empty");

    public static string RepoRoot => Meta("RepoRoot");
    public static string CoreOutputDir => Meta("CoreOutputDir");
    public static string ArchFixtureDir => Meta("ArchFixtureDir");
    /// <summary>The export folder the scans read: the repo's own, or the one named by NV_EXPORT_BUILD (the shipping folder, checked as it is).</summary>
    public static string GodotBuildDir => Environment.GetEnvironmentVariable("NV_EXPORT_BUILD") is { Length: > 0 } v ? v : Meta("GodotBuildDir");
    public static string CaptureToolDll => Path.Combine(Meta("CaptureToolDir"), "Viewer.Capture.dll");
    public static string LifecycleSmokeDir => Meta("LifecycleSmokeDir");
    public static string ArchFixtureDll => Path.Combine(Meta("ArchFixtureDir"), "Viewer.Core.ArchFixture.dll");
    public static string LifecycleSmokeDll => Path.Combine(Meta("LifecycleSmokeDir"), "Viewer.Core.LifecycleSmoke.dll");
    public static string DecodeProbeDll => Path.Combine(Meta("DecodeProbeDir"), "Viewer.Core.DecodeProbe.dll");
    public static string CoreDll => typeof(Viewer.Core.Physics.PhysicsWorld).Assembly.Location;
    public static string TestOutputDir => AppContext.BaseDirectory;

    /// <summary>The environment variable that points the local-only fixtures at another folder (read-only to the run),
    /// e.g. a fresh clone reading the working repo's fixtures-local folder. Unset or empty: the repo's own fixtures-local\.</summary>
    public const string FixturesLocalVariable = "NV_FIXTURES_LOCAL";

    /// <summary>The local-only fixtures folder for this run (<see cref="FixturesLocalVariable"/>, else the repo's own).</summary>
    public static string FixturesLocal => ResolveFixturesLocal(Environment.GetEnvironmentVariable(FixturesLocalVariable), RepoRoot);

    /// <summary>An override must be an absolute path to an existing folder: a wrong value fails the run rather than
    /// turning every local-only test into a skip (no silent passes).</summary>
    public static string ResolveFixturesLocal(string? overridePath, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(overridePath)) return Path.Combine(repoRoot, "fixtures-local");
        if (!Path.IsPathFullyQualified(overridePath))
            throw new InvalidOperationException($"{FixturesLocalVariable} must be an absolute path: '{overridePath}'");
        if (!Directory.Exists(overridePath))
            throw new InvalidOperationException($"{FixturesLocalVariable} points at a folder that does not exist: '{overridePath}'");
        return Path.GetFullPath(overridePath);
    }
}
