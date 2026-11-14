// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The engine chooses its renderer before the viewer's code runs, from <c>--rendering-driver</c>. At start the viewer compares the saved
/// backend choice with the renderer that is running and, if they differ, starts itself again once with the saved one.
/// </summary>
public static class BackendRelaunch
{
    private const string AppliedPrefix = "--backend-applied=";

    /// <summary>The arguments for the second start, or null when no second start is wanted. <paramref name="target"/> is the driver the choice
    /// settled on (<see cref="BackendChoice"/>). Each start names the drivers already tried, so none is tried twice and a start that finds its
    /// own driver below the floor can still go to the other one once.</summary>
    public static IReadOnlyList<string>? Plan(string target, string running, IReadOnlyList<string> engineArgs, IReadOnlyList<string> userArgs)
    {
        if (target is not ("vulkan" or "d3d12") || string.Equals(target, running, StringComparison.OrdinalIgnoreCase))
            return null;
        // a driver given on the command line wins
        if (engineArgs.Any(a => a == "--rendering-driver" || a.StartsWith("--rendering-driver=", StringComparison.Ordinal)))
            return null;
        // test, render and check runs choose their own driver
        if (userArgs.Any(a => a == "--test-render" || a.StartsWith("--s1-", StringComparison.Ordinal)))
            return null;
        var tried = userArgs.Where(a => a.StartsWith(AppliedPrefix, StringComparison.Ordinal)).SelectMany(a => a[AppliedPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (tried.Contains(target))
            return null;
        tried.Add(target);
        return [.. engineArgs, "--rendering-driver", target, "--", .. userArgs.Where(a => !a.StartsWith(AppliedPrefix, StringComparison.Ordinal)), AppliedPrefix + string.Join(",", tried)];
    }

    /// <summary>The drivers an earlier start of this run already tried.</summary>
    public static IReadOnlyList<string> Tried(IReadOnlyList<string> userArgs) =>
        userArgs.Where(a => a.StartsWith(AppliedPrefix, StringComparison.Ordinal)).SelectMany(a => a[AppliedPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
}
