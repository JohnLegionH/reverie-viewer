// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The capture tool's login accounts follow the same test-account pool and protected-accounts files as the viewer's automation
/// (host/Viewer.Automation AccountsFile): no account name is built into the tool. These tests run the tool's offline
/// <c>account-check</c> command (it reads the accounts file and answers; it logs in to nothing) as a child process, each with
/// its own temp folder and environment, so they run in parallel.
/// </summary>
public sealed class CaptureAccountsTests
{
    private readonly ITestOutputHelper _out;
    public CaptureAccountsTests(ITestOutputHelper output) { _out = output; }

    private const string Secret = "pw-example-not-a-real-password";

    private sealed record Setup(string Accounts, string? Pool, string? Protected);

    private static Setup Make(string[] pool, string[]? protectedNames = null, bool noPool = false)
    {
        string root = TestTemp.NewFolder("capture-accounts");
        string accounts = Path.Combine(root, "accounts.json");
        File.WriteAllText(accounts, JsonSerializer.Serialize(new
        {
            accounts = new[]
            {
                new { alias = "pool-alpha", first = "Alpha", last = "Resident", password = Secret },
                new { alias = "pool-beta", first = "Beta", last = "Resident", password = Secret },
                new { alias = "outside", first = "Outside", last = "Resident", password = Secret },
            },
        }));
        string? poolFile = null, protectedFile = null;
        if (!noPool) { poolFile = Path.Combine(root, "pool.txt"); File.WriteAllLines(poolFile, pool); }
        if (protectedNames is not null) { protectedFile = Path.Combine(root, "protected.txt"); File.WriteAllLines(protectedFile, protectedNames); }
        return new Setup(accounts, poolFile, protectedFile);
    }

    private (int Exit, string Output) Check(Setup s, string alias)
    {
        var env = new Dictionary<string, string?>
        {
            ["NV_AVATAR_POOL"] = s.Pool,
            ["NV_PROTECTED_ACCOUNTS"] = s.Protected,
            ["NV_CLAIM_CHECK_SCRIPT"] = null,
            ["NV_LOGIN_URI"] = null,
        };
        var r = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.CaptureToolDll, "account-check", "--accounts", s.Accounts, "--account", alias], TimeSpan.FromSeconds(60), env);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        return r;
    }

    [Fact]
    public void A_pool_account_is_allowed_whatever_its_name_and_its_password_is_not_printed()
    {
        var s = Make(["pool-alpha", "pool-beta"]);
        var r = Check(s, "pool-alpha");
        Assert.Equal(0, r.Exit);
        Assert.Contains("account allowed: pool-alpha", r.Output);
        Assert.DoesNotContain(Secret, r.Output);
    }

    [Fact]
    public void An_account_in_the_accounts_file_but_not_in_the_pool_is_refused()
    {
        var s = Make(["pool-alpha"]);
        var r = Check(s, "outside");
        Assert.Equal(1, r.Exit);
        Assert.Contains("not in the test-account pool", r.Output);
        Assert.DoesNotContain("account allowed", r.Output);
        Assert.DoesNotContain(Secret, r.Output);
    }

    [Fact]
    public void With_no_pool_file_set_every_account_is_refused()
    {
        var s = Make([], noPool: true);
        var r = Check(s, "pool-alpha");
        Assert.Equal(1, r.Exit);
        Assert.Contains("no test-account pool file is set", r.Output);
    }

    [Fact]
    public void A_pool_account_on_the_protected_list_is_refused_without_naming_it()
    {
        var s = Make(["pool-alpha", "pool-beta"], protectedNames: ["Beta Resident"]);
        var r = Check(s, "pool-beta");
        Assert.Equal(1, r.Exit);
        Assert.Contains("on the protected list", r.Output);
        Assert.DoesNotContain("Beta", r.Output);
        Assert.Equal(0, Check(s, "pool-alpha").Exit);
    }
}
