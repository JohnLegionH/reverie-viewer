// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The Linux packages start through tools/release/reverie-linux.sh (shipped as "reverie"). Without a desktop session,
/// or without the display libraries the engine loads, the engine prints its own error and then crashes on the way out;
/// the launcher says what is missing and exits 1 before the engine starts. Run here with bash, a stand-in engine that
/// prints its arguments and exits 7, and a stand-in ldconfig that lists the libraries a case gives it.
/// </summary>
public class LinuxLauncherTests : IDisposable
{
    private static readonly string[] X11 = ["libX11.so.6", "libXcursor.so.1", "libXext.so.6", "libXi.so.6", "libXinerama.so.1", "libXrandr.so.2", "libXrender.so.1", "libxkbcommon.so.0"];
    private static readonly string[] Wayland = ["libwayland-client.so.0", "libwayland-cursor.so.0", "libxkbcommon.so.0"];

    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nv-launcher-" + Guid.NewGuid().ToString("N"));

    public LinuxLauncherTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(Path.Combine(_dir, "pkg"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.Copy(Path.Combine(TestPaths.RepoRoot, "tools", "release", "reverie-linux.sh"), Path.Combine(_dir, "pkg", "reverie"));
        File.WriteAllText(Path.Combine(_dir, "pkg", "Reverie.x86_64"), "#!/bin/sh\necho \"engine started: $*\"\nexit 7\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Bash()
    {
        if (!OperatingSystem.IsWindows()) return "/bin/bash";
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!File.Exists(Path.Combine(p, "git.exe"))) continue;
            foreach (var rel in new[] { "..\\bin\\bash.exe", "..\\..\\bin\\bash.exe" })
            {
                string b = Path.GetFullPath(Path.Combine(p, rel));
                if (File.Exists(b)) return b;
            }
        }
        throw new InvalidOperationException("Git's bash was not found beside git.exe on PATH; these tests need it on Windows");
    }

    private static string Unix(string path) => OperatingSystem.IsWindows()
        ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
        : path;

    private (int Exit, string Output) Start(string[] libs, string? display, string? wayland, params string[] args) =>
        StartWith(new Dictionary<string, string?>(), libs, display, wayland, args);

    private (int Exit, string Output) StartWith(Dictionary<string, string?> extraEnv, string[] libs, string? display, string? wayland, params string[] args)
    {
        // a real cache lists the C library; a case with no libraries at all stands for a system whose cache is empty
        string list = string.Join("", (libs.Length == 0 ? libs : libs.Prepend("libc.so.6")).Select(l => $"\\t{l} (libc6,x86-64) => /usr/lib/x86_64-linux-gnu/{l}\\n"));
        File.WriteAllText(Path.Combine(_dir, "bin", "ldconfig"), $"#!/bin/sh\nprintf '{list}'\n");
        string cmd = $"export PATH=\"{Unix(Path.Combine(_dir, "bin"))}:$PATH\"; exec sh \"{Unix(Path.Combine(_dir, "pkg", "reverie"))}\"" +
                     string.Concat(args.Select(a => $" '{a}'"));
        var env = new Dictionary<string, string?>(extraEnv) { ["DISPLAY"] = display, ["WAYLAND_DISPLAY"] = wayland };
        var r = ChildProcess.Run(Bash(), ["-c", cmd], TimeSpan.FromMinutes(1), env);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        return r;
    }

    [Fact]
    public void With_a_display_and_the_X11_libraries_it_starts_the_engine_with_the_arguments()
    {
        var r = Start(X11, ":0", null, "--display-driver", "x11");
        Assert.Equal(7, r.Exit);
        Assert.Contains("engine started: --display-driver x11", r.Output);
    }

    [Fact]
    public void On_Wayland_without_the_X11_libraries_it_starts_the_engine()
    {
        var r = Start(Wayland, null, "wayland-0");
        Assert.Equal(7, r.Exit);
        Assert.Contains("engine started", r.Output);
    }

    [Fact]
    public void A_missing_display_library_is_named_with_its_package_and_the_engine_is_not_started()
    {
        var r = Start(X11.Where(l => l != "libXcursor.so.1").ToArray(), ":0", null);
        Assert.Equal(1, r.Exit);
        Assert.DoesNotContain("engine started", r.Output);
        Assert.Contains("libXcursor.so.1", r.Output);
        Assert.Contains("libxcursor1", r.Output);
        Assert.Contains("sudo apt install", r.Output);
    }

    [Fact]
    public void Without_a_desktop_session_it_says_so_and_does_not_start_the_engine()
    {
        var r = Start(X11.Concat(Wayland).ToArray(), null, null);
        Assert.Equal(1, r.Exit);
        Assert.DoesNotContain("engine started", r.Output);
        Assert.Contains("no desktop session", r.Output);
    }

    [Fact]
    public void A_library_outside_the_ldconfig_cache_but_on_LD_LIBRARY_PATH_counts_as_present()
    {
        string libs = Path.Combine(_dir, "libs");
        Directory.CreateDirectory(libs);
        File.WriteAllText(Path.Combine(libs, "libXcursor.so.1"), "");
        var r = StartWith(new Dictionary<string, string?> { ["LD_LIBRARY_PATH"] = Unix(libs) },
            X11.Where(l => l != "libXcursor.so.1").ToArray(), ":0", null);
        Assert.Equal(7, r.Exit);
        Assert.Contains("engine started", r.Output);
    }

    [Fact]
    public void An_ldconfig_cache_listing_nothing_leaves_the_check_to_the_engine()
    {
        var r = Start([], ":0", null);
        Assert.Equal(7, r.Exit);
        Assert.Contains("engine started", r.Output);
    }

    [Fact]
    public void A_system_with_no_display_library_at_all_is_told_what_to_install()
    {
        var r = Start(["libc.so.6"], ":0", null);
        Assert.Equal(1, r.Exit);
        Assert.DoesNotContain("engine started", r.Output);
        Assert.Contains("libX11.so.6", r.Output);
        Assert.Contains("libxcursor1", r.Output);
    }

    [Fact]
    public void The_check_can_be_turned_off()
    {
        var r = StartWith(new Dictionary<string, string?> { ["REVERIE_SKIP_DISPLAY_CHECK"] = "1" }, [], null, null);
        Assert.Equal(7, r.Exit);
        Assert.Contains("engine started", r.Output);
    }

    [Fact]
    public void Headless_starts_skip_the_display_check()
    {
        var r = Start([], null, null, "--headless");
        Assert.Equal(7, r.Exit);
        Assert.Contains("engine started: --headless", r.Output);
    }
}
