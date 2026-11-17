// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Two release guards, each with its negative controls: tools/assert-no-grid-address.ps1 (a shipping export comes with no
/// grid filled in: no remembered login data and no login-service address in the viewer's own files) and
/// tools/assert-no-logo.ps1 (no Linden Lab eye-in-hand or Second Life logo and no InWorldz logo or artwork, by name and,
/// where the official viewer's source is at hand, by content). tools/package.ps1 runs both on every export it packs.
/// </summary>
public class ReleaseGuardTests(ITestOutputHelper output)
{
    private static string Script(string name) => Path.Combine(TestPaths.RepoRoot, "tools", name);

    private (int ExitCode, string Output) Run(string script, params string[] args)
    {
        var all = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script(script) };
        all.AddRange(args);
        var r = ChildProcess.Run("powershell.exe", all.ToArray(), TimeSpan.FromMinutes(3));
        output.WriteLine(r.Output);
        return r;
    }

    /// <summary>The official viewer's indra\newview folder, found beside the avatar data folder the tests already use.</summary>
    private static string? OfficialNewview()
    {
        var character = Environment.GetEnvironmentVariable("NV_CHARACTER_DIR");
        if (string.IsNullOrWhiteSpace(character) || !Directory.Exists(character)) return null;
        var newview = Path.GetDirectoryName(Path.GetFullPath(character).TrimEnd('\\', '/'));
        return newview is not null && Directory.Exists(Path.Combine(newview, "icons")) ? newview : null;
    }

    private static string CleanExport()
    {
        string dir = TestTemp.NewFolder("grid-guard");
        Directory.CreateDirectory(Path.Combine(dir, "data_x"));
        File.WriteAllText(Path.Combine(dir, "FIRST-RUN.txt"), "Type your grid's address, for example http://grid.example.org:8002/");
        File.WriteAllBytes(Path.Combine(dir, "data_x", "Viewer.Example.dll"), Encoding.Unicode.GetBytes("no address here https://github.com/example"));
        File.WriteAllBytes(Path.Combine(dir, "data_x", "ThirdParty.dll"), Encoding.Unicode.GetBytes("http://login.thirdparty.example.net:8002/"));
        return dir;
    }

    [Fact]
    public void A_clean_export_passes_the_grid_address_guard()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        var (exit, text) = Run("assert-no-grid-address.ps1", "-Path", CleanExport());
        Assert.Equal(0, exit);
        Assert.Contains("GRID ADDRESS GUARD OK", text);
    }

    [Theory]
    [InlineData("remembered", "last-login-address.txt", "http://grid.example.org:8002/", false)]
    [InlineData("settings", "settings.json", "{}", false)]
    [InlineData("own-assembly", "data_x/Viewer.Shell.dll", "http://192.0.2.20:8002/", true)]
    [InlineData("config", "override.cfg", "login=\"198.51.100.7:9000\"", false)]
    [InlineData("pack", "Reverie.pck", "https://203.0.113.5:8002/", false)]
    public void The_grid_address_guard_refuses_each_kind_of_pre_filled_grid(string name, string relative, string content, bool utf16)
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        string dir = CleanExport();
        string file = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(file, (utf16 ? Encoding.Unicode : Encoding.UTF8).GetBytes(content));
        var (exit, text) = Run("assert-no-grid-address.ps1", "-Path", dir);
        Assert.True(exit == 1, $"{name}: the guard passed an export holding {relative}");
        Assert.Contains("GRID ADDRESS GUARD FAIL", text);
    }

    [Fact]
    public void The_grid_address_guard_refuses_a_missing_folder()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        var (exit, _) = Run("assert-no-grid-address.ps1", "-Path", TestTemp.NewFolderPath("grid-guard-missing"));
        Assert.Equal(1, exit);
    }

    [Theory]
    [InlineData("SL_Logo.png")]
    [InlineData("eye_in_hand.svg")]
    [InlineData("inworldz-banner.jpg")]
    [InlineData("login_sl_logo_small.png")]
    public void The_logo_guard_refuses_a_logo_by_name(string fileName)
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        string dir = TestTemp.NewFolder("logo-guard");
        File.WriteAllBytes(Path.Combine(dir, "button.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, fileName), [4, 5, 6]);
        var (exit, text) = Run("assert-no-logo.ps1", "-Path", dir);
        Assert.Equal(1, exit);
        Assert.Contains("LOGO GUARD FAIL", text);
    }

    [Fact]
    public void With_tracked_only_the_logo_guard_judges_the_files_git_tracks_and_skips_untracked_and_ignored_output()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        string repo = TestTemp.NewFolder("logo-guard-git");
        void Git(params string[] a)
        {
            var r = ChildProcess.Run("git", ["-C", repo, .. a], TimeSpan.FromMinutes(1));
            Assert.True(r.ExitCode == 0, "git " + string.Join(" ", a) + ": " + r.Output);
        }
        Git("init", "-q");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "results/\n");
        Directory.CreateDirectory(Path.Combine(repo, "art"));
        Directory.CreateDirectory(Path.Combine(repo, "results"));
        File.WriteAllBytes(Path.Combine(repo, "art", "button.png"), [1, 2, 3]);
        Git("add", ".gitignore", "art/button.png");
        // a render run's proposal picture (ignored) and a stray untracked picture, both named for Second Life
        File.WriteAllBytes(Path.Combine(repo, "results", "proposal-logincard-second-life.png"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(repo, "art", "secondlife-draft.png"), [7, 8, 9]);

        var (exit, text) = Run("assert-no-logo.ps1", "-Path", repo, "-TrackedOnly");
        Assert.Equal(0, exit);
        Assert.Contains("LOGO GUARD OK: 1 images checked", text);
        // without the switch the same folder fails, so the switch is what skips them
        Assert.Equal(1, Run("assert-no-logo.ps1", "-Path", repo).ExitCode);

        // a tracked logo is still refused, even under the switch
        File.WriteAllBytes(Path.Combine(repo, "art", "SL_Logo.png"), [10, 11, 12]);
        Git("add", "art/SL_Logo.png");
        (exit, text) = Run("assert-no-logo.ps1", "-Path", Path.Combine(repo, "art"), "-TrackedOnly");
        Assert.Equal(1, exit);
        Assert.Contains("SL_Logo.png", text);
    }

    [Fact]
    public void The_logo_guard_refuses_the_official_viewers_icon_under_another_name()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        var newview = OfficialNewview();
        if (newview is null)
        {
            output.WriteLine("SKIP: NV_CHARACTER_DIR does not point into the official viewer's source; the content check needs its icons folder");
            return;
        }
        string dir = TestTemp.NewFolder("logo-guard-ref");
        var icon = Directory.GetFiles(Path.Combine(newview, "icons"), "*.png", SearchOption.AllDirectories).First();
        File.Copy(icon, Path.Combine(dir, "button.png"));
        var (exit, text) = Run("assert-no-logo.ps1", "-Path", dir, "-Reference", newview);
        Assert.Equal(1, exit);
        Assert.Contains("is the official viewer's", text);
    }

    [Fact]
    public void No_tracked_image_in_the_repository_is_a_Linden_or_InWorldz_logo()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        // one -Path argument with the folders separated by ";" (powershell -File cannot pass an array)
        var folders = new[] { "host", "core", "tools", "fixtures", "Docs", "ThirdPartyLicenses", "native" }
            .Select(d => Path.Combine(TestPaths.RepoRoot, d)).Where(Directory.Exists);
        // tracked files only: a render run leaves proposal pictures in ignored folders, and they are not in the repository
        var args = new List<string> { "-Path", string.Join(";", folders), "-TrackedOnly" };
        if (OfficialNewview() is { } newview) { args.Add("-Reference"); args.Add(newview); }
        var (exit, text) = Run("assert-no-logo.ps1", args.ToArray());
        Assert.Equal(0, exit);
        Assert.Contains("LOGO GUARD OK", text);
        Assert.DoesNotContain("OK: 0 images checked", text);
    }

    [Fact]
    public void The_package_script_runs_both_guards_on_every_export()
    {
        var script = File.ReadAllText(Script("package.ps1"));
        Assert.Contains("assert-no-grid-address.ps1", script);
        Assert.Contains("assert-no-logo.ps1", script);
    }
}
