// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Platform;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>ADR-019 per-user data directories, per platform; nothing beside the executable. Pure: nothing is created.</summary>
public class DataDirectoriesTests
{
    private static Func<string, string?> Env(params (string K, string V)[] vars) =>
        k => vars.FirstOrDefault(v => v.K == k).V;

    [Fact]
    public void Windows_settings_roam_and_cache_and_logs_stay_local()
    {
        var d = DataDirectories.For(DataPlatform.Windows, Env(("APPDATA", @"C:\Users\u\AppData\Roaming"), ("LOCALAPPDATA", @"C:\Users\u\AppData\Local")), "ReverieViewer");
        Assert.Equal(@"C:\Users\u\AppData\Roaming\ReverieViewer", d.Settings);
        Assert.Equal(@"C:\Users\u\AppData\Local\ReverieViewer\Cache", d.Cache);
        Assert.Equal(@"C:\Users\u\AppData\Local\ReverieViewer\Logs", d.Logs);
    }

    [Fact]
    public void Linux_follows_XDG_and_ignores_relative_values()
    {
        var d = DataDirectories.For(DataPlatform.Linux, Env(("HOME", "/home/u"), ("XDG_CONFIG_HOME", "/cfg"), ("XDG_CACHE_HOME", "relative/cache")), "ReverieViewer");
        Assert.Equal("/cfg/ReverieViewer", d.Settings);
        Assert.Equal("/home/u/.cache/ReverieViewer", d.Cache);
        Assert.Equal("/home/u/.local/state/ReverieViewer/logs", d.Logs);
    }

    [Fact]
    public void MacOS_uses_the_Library_folders()
    {
        var d = DataDirectories.For(DataPlatform.MacOS, Env(("HOME", "/Users/u")), "ReverieViewer");
        Assert.Equal("/Users/u/Library/Application Support/ReverieViewer", d.Settings);
        Assert.Equal("/Users/u/Library/Caches/ReverieViewer", d.Cache);
        Assert.Equal("/Users/u/Library/Logs/ReverieViewer", d.Logs);
    }

    [Theory]
    [InlineData(DataPlatform.Windows)]
    [InlineData(DataPlatform.Linux)]
    [InlineData(DataPlatform.MacOS)]
    public void Missing_variables_are_an_error_never_a_fallback_to_the_executable_folder(DataPlatform platform)
    {
        Assert.Throws<InvalidOperationException>(() => DataDirectories.For(platform, Env(), "ReverieViewer"));
    }

    [Fact]
    public void A_relative_APPDATA_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => DataDirectories.For(DataPlatform.Windows, Env(("APPDATA", "AppData"), ("LOCALAPPDATA", @"C:\L")), "ReverieViewer"));
    }

    [Fact]
    public void A_directory_inside_the_application_folder_is_refused()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var d = DataDirectories.For(DataPlatform.Windows, Env(("APPDATA", exeDir), ("LOCALAPPDATA", @"C:\Elsewhere")), "ReverieViewer");
        Assert.Throws<InvalidOperationException>(() => d.EnsureNotUnder(exeDir));
    }

    [Fact]
    public void This_machines_directories_are_not_beside_the_executable()
    {
        var d = DataDirectories.ForCurrentUser();
        foreach (var p in new[] { d.Settings, d.Cache, d.Logs })
        {
            Assert.True(Path.IsPathRooted(p), p);
            Assert.DoesNotContain(AppContext.BaseDirectory.TrimEnd('\\', '/'), p, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ReverieViewer", p);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    public void The_product_must_be_a_plain_folder_name(string product)
    {
        Assert.Throws<ArgumentException>(() => DataDirectories.For(DataPlatform.Linux, Env(("HOME", "/h")), product));
    }

    [Fact]
    public void The_packs_folder_is_local_to_the_person_and_not_in_the_roaming_settings_or_the_cache()
    {
        var env = Env(("APPDATA", @"C:\Users\u\AppData\Roaming"), ("LOCALAPPDATA", @"C:\Users\u\AppData\Local"), ("HOME", "/srv/example"));
        Assert.Equal(@"C:\Users\u\AppData\Local\ReverieViewer\Packs", DataDirectories.PacksFolder(DataPlatform.Windows, env, "ReverieViewer"));
        Assert.Equal("/srv/example/.local/share/ReverieViewer/packs", DataDirectories.PacksFolder(DataPlatform.Linux, env, "ReverieViewer"));
    }

    [Fact]
    public void A_log_folder_named_in_the_environment_replaces_the_users_log_folder()
    {
        var logs = DataDirectories.LogsFolder(Env(("NV_LOG_DIR", @"X:\runs\logs")), @"C:\Users\u\AppData\Local\ReverieViewer\Logs");
        Assert.Equal(@"X:\runs\logs", logs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\logs")]
    public void Without_an_absolute_log_folder_in_the_environment_the_users_log_folder_stands(string configured)
    {
        string usual = @"C:\Users\u\AppData\Local\ReverieViewer\Logs";
        Assert.Equal(usual, DataDirectories.LogsFolder(Env(("NV_LOG_DIR", configured)), usual));
        Assert.Equal(usual, DataDirectories.LogsFolder(Env(), usual));
    }

    [Fact]
    public void The_launcher_that_starts_every_test_and_automation_viewer_gives_it_a_log_folder_of_its_own()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Viewer.sln"))) dir = Path.GetDirectoryName(dir)!;
        string script = File.ReadAllText(Path.Combine(dir ?? throw new InvalidOperationException("repository root not found"), "tools", "godot.ps1"));
        Assert.Contains("NV_LOG_DIR", script);
    }
}
