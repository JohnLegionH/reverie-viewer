// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Automation;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Which test accounts the automation may log in with. The allowed aliases come from the test-account pool file, the
/// same file the machine's claim script hands avatar accounts out from (one alias per line, # comments, aliases only, no
/// secrets): the file named by NV_AVATAR_POOL, else avatar-pool.txt beside the claim check script NV_CLAIM_CHECK_SCRIPT.
/// There is no list in the code. One account is refused whatever the file says: the grid owner's own, a person's real
/// account and never a test account. Every refusal says why.
/// </summary>
public class AccountPoolTests
{
    private readonly string _dir = TestTemp.NewFolder("pool");

    private string Accounts(params (string Alias, string First, string Last)[] accounts)
    {
        string f = Path.Combine(_dir, "accounts.json");
        File.WriteAllText(f, "{\"accounts\":[" + string.Join(",", accounts.Select(a =>
            $"{{\"alias\":\"{a.Alias}\",\"first\":\"{a.First}\",\"last\":\"{a.Last}\",\"password\":\"pw-not-real\"}}")) + "]}");
        return f;
    }

    private string Pool(params string[] lines)
    {
        string f = Path.Combine(_dir, AccountsFile.PoolFileName);
        File.WriteAllText(f, string.Join("\r\n", lines) + "\r\n");
        return f;
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    // ---------------- where the pool file is ----------------

    [Fact]
    public void The_pool_file_is_the_one_NV_AVATAR_POOL_names_first()
    {
        string named = Path.Combine(_dir, "named-pool.txt");
        Assert.Equal(named, AccountsFile.PoolPath(Env(("NV_AVATAR_POOL", named), ("NV_CLAIM_CHECK_SCRIPT", Path.Combine(_dir, "claims.sh")))));
    }

    [Fact]
    public void Without_NV_AVATAR_POOL_the_pool_file_is_the_one_beside_the_claim_check_script()
    {
        string script = Path.Combine(_dir, "claims.sh");
        Assert.Equal(Path.Combine(_dir, "avatar-pool.txt"), AccountsFile.PoolPath(Env(("NV_CLAIM_CHECK_SCRIPT", script))));
    }

    [Fact]
    public void With_neither_setting_there_is_no_pool_file()
    {
        Assert.Null(AccountsFile.PoolPath(Env()));
        Assert.Null(AccountsFile.PoolPath(Env(("NV_AVATAR_POOL", ""), ("NV_CLAIM_CHECK_SCRIPT", ""))));
    }

    // ---------------- reading it ----------------

    [Fact]
    public void The_pool_file_is_read_as_the_claim_script_reads_it()
    {
        string f = Pool("# a comment line", "", "  tester1  ", "Tester2 # an account for the second avatar", "jolt1", "tester1", "not an alias", "viewer1\t");
        Assert.Equal(["tester1", "tester2", "jolt1", "viewer1"], AccountsFile.ReadPool(f));
    }

    // ---------------- logins ----------------

    [Fact]
    public void An_alias_in_the_pool_file_logs_in_and_the_password_stays_a_secret()
    {
        string accounts = Accounts(("tester2", "Test", "Two"), ("jolt1", "Jolt", "One"));
        string pool = Pool("tester2", "jolt1");
        var (first, last, pw) = AccountsFile.Load(accounts, "tester2", pool);
        Assert.Equal(("Test", "Two"), (first, last));
        Assert.Equal("[redacted:PasswordDigest]", pw.ToString());
        Assert.Equal(("Jolt", "One"), (AccountsFile.Load(accounts, "jolt1", pool).First, AccountsFile.Load(accounts, "jolt1", pool).Last));
    }

    [Fact]
    public void There_is_no_list_in_the_code_an_alias_the_pool_file_leaves_out_is_refused_and_the_refusal_names_the_file()
    {
        string accounts = Accounts(("tester1", "Test", "One"), ("jolt2", "Jolt", "Two"));
        string pool = Pool("jolt2");
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "tester1", pool));
        Assert.Contains("not approved for automation logins", ex.Message);
        Assert.Contains("tester1 is not in the test-account pool file", ex.Message);
        Assert.Contains(pool, ex.Message);
    }

    [Fact]
    public void With_no_pool_file_set_every_alias_is_refused_and_the_refusal_names_the_settings()
    {
        string accounts = Accounts(("tester1", "Test", "One"));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "tester1", null));
        Assert.Contains("no test-account pool file", ex.Message);
        Assert.Contains("NV_AVATAR_POOL", ex.Message);
        Assert.Contains("NV_CLAIM_CHECK_SCRIPT", ex.Message);
    }

    [Fact]
    public void A_pool_file_that_does_not_exist_refuses_and_says_so()
    {
        string accounts = Accounts(("tester1", "Test", "One"));
        string missing = Path.Combine(_dir, "no-such-pool.txt");
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "tester1", missing));
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void An_alias_in_the_pool_but_not_in_the_accounts_file_is_refused_and_says_so()
    {
        string accounts = Accounts(("tester1", "Test", "One"));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "jolt1", Pool("jolt1", "tester1")));
        Assert.Contains("not in the accounts file", ex.Message);
    }

    [Theory]
    [InlineData("Tester1")]
    [InlineData(" tester1")]
    [InlineData("tester1 ")]
    public void An_alias_is_matched_exactly_so_another_spelling_cannot_reach_an_account_the_pool_leaves_out(string alias)
    {
        // the pool's aliases are lower case; the accounts file may hold another account under another spelling
        string accounts = Accounts(("tester1", "Test", "One"), (alias, "Not", "Pooled"));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, alias, Pool("tester1")));
        Assert.Contains("is not in the test-account pool file", ex.Message);
    }

    // ---------------- the owner's account: never, whatever the file says ----------------

    [Theory]
    [InlineData("legion")]
    [InlineData("Legion")]
    [InlineData("LEGION")]
    public void The_owners_account_is_refused_even_when_the_pool_file_lists_it(string alias)
    {
        string accounts = Accounts((alias, "Some", "Body"));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, alias, Pool(alias, "tester1")));
        Assert.Contains("never allowed for automation logins", ex.Message);
        Assert.DoesNotContain("pool file", ex.Message);
    }

    [Theory]
    [InlineData("Legion", "Resident")]
    [InlineData("Some", "legion")]
    public void The_owners_account_is_refused_under_any_alias_by_its_name(string first, string last)
    {
        string accounts = Accounts(("helper", first, last));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "helper", Pool("helper")));
        Assert.Contains("never allowed for automation logins", ex.Message);
    }

    [Fact]
    public void The_hard_check_on_its_own()
    {
        Assert.True(AccountsFile.NeverAllowed("legion"));
        Assert.True(AccountsFile.NeverAllowed(" Legion "));
        Assert.True(AccountsFile.NeverAllowed("x", "Legion", "Resident"));
        Assert.True(AccountsFile.NeverAllowed("x", "Some", "LEGION"));
        Assert.False(AccountsFile.NeverAllowed("tester1", "Test", "One"));
        Assert.False(AccountsFile.NeverAllowed("legion2"));   // only the whole name
        Assert.False(AccountsFile.NeverAllowed("x", "Legionnaire", "Resident"));
    }
}
