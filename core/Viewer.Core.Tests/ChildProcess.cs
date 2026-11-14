// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;

namespace Viewer.Core.Tests;

/// <summary>Runs a child process to completion, capturing output. A child that overruns is killed, never left running.</summary>
internal static class ChildProcess
{
    public static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> args, TimeSpan timeout) => Run(fileName, args, timeout, null);

    /// <summary><paramref name="environment"/> sets (a value) or removes (null) variables for the child only.</summary>
    public static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in environment ?? new Dictionary<string, string?>())
            if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {fileName}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
            throw new TimeoutException($"{fileName} exceeded {timeout} and was killed");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    public static string DotnetHost => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
}
