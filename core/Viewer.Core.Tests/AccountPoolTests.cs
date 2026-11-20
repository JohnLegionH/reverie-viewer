// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Automation;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Which test accounts the automation may log in with. The allowed aliases come from the test-account pool file, the
/// same file the machine's claim script hands avatar accounts out from (one alias per line, # comments, aliases only, no
/// secrets): the file named by NV_AVATAR_POOL, else avatar-pool.txt beside the claim check script NV_CLAIM_CHECK_SCRIPT.
/// There is no list in the code. Accounts in the protected-accounts file (NV_PROTECTED_ACCOUNTS, else protected-accounts.txt
/// beside the claim check script) are refused whatever the pool says. Every refusal says why.
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

    // ---------------- the protected list: never, whatever the pool says ----------------
    // A machine keeps the accounts that must never be used for automation (a person's real account, never a test
    // account) in a protected-accounts file outside the repository, named the way the pool file is named. The code
    // holds no account names. These tests use the stand-in "owner".

    private string Protected(params string[] lines)
    {
        string f = Path.Combine(_dir, AccountsFile.ProtectedFileName);
        File.WriteAllText(f, string.Join("\r\n", lines) + "\r\n");
        return f;
    }

    [Fact]
    public void The_protected_file_is_the_one_NV_PROTECTED_ACCOUNTS_names_first()
    {
        string named = Path.Combine(_dir, "named-protected.txt");
        Assert.Equal(named, AccountsFile.ProtectedPath(Env(("NV_PROTECTED_ACCOUNTS", named), ("NV_CLAIM_CHECK_SCRIPT", Path.Combine(_dir, "claims.sh")))));
    }

    [Fact]
    public void Without_NV_PROTECTED_ACCOUNTS_the_protected_file_is_the_one_beside_the_claim_check_script()
    {
        Assert.Equal(Path.Combine(_dir, "protected-accounts.txt"), AccountsFile.ProtectedPath(Env(("NV_CLAIM_CHECK_SCRIPT", Path.Combine(_dir, "claims.sh")))));
        Assert.Null(AccountsFile.ProtectedPath(Env()));
    }

    [Fact]
    public void The_protected_file_is_read_as_names_one_per_line_with_comments()
    {
        string f = Protected("# accounts never used by the automation", "", "  Owner  ", "Some Body # a full name", "owner");
        Assert.Equal(["owner", "some body"], AccountsFile.ReadProtected(f));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("Owner")]
    public void A_pool_alias_that_is_also_protected_is_refused_with_a_message_that_names_no_account(string alias)
    {
        string accounts = Accounts((alias, "Some", "Body"));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, alias, Pool(alias, "tester2"), Protected("owner")));
        Assert.Contains("account is on the protected list", ex.Message);
        Assert.DoesNotContain("owner", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Some", ex.Message);
    }

    [Theory]
    [InlineData("Owner", "Resident")]
    [InlineData("Some", "owner")]
    [InlineData("Some", "Body")]   // listed by full name
    public void A_pool_alias_whose_account_name_is_protected_is_refused(string first, string last)
    {
        string accounts = Accounts(("helper", first, last));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "helper", Pool("helper"), Protected("owner", "some body")));
        Assert.Contains("account is on the protected list", ex.Message);
        Assert.DoesNotContain("helper", ex.Message);
    }

    [Fact]
    public void An_account_protected_by_its_name_is_refused_without_its_alias_even_when_the_pool_leaves_it_out()
    {
        string accounts = Accounts(("helper", "Some", "Body"));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "helper", Pool("tester2"), Protected("some body")));
        Assert.Contains("account is on the protected list", ex.Message);
        Assert.DoesNotContain("helper", ex.Message);
    }

    [Fact]
    public void Spacing_inside_a_protected_full_name_does_not_matter()
    {
        string accounts = Accounts(("helper", "Some ", "Body"));
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "helper", Pool("helper"), Protected("Some   Body")));
        Assert.Contains("account is on the protected list", ex.Message);
    }

    [Fact]
    public void A_pool_alias_that_is_not_protected_is_allowed()
    {
        string accounts = Accounts(("tester2", "Test", "Two"), ("owner", "Some", "Body"));
        var (first, last, _) = AccountsFile.Load(accounts, "tester2", Pool("tester2", "owner"), Protected("owner"));
        Assert.Equal(("Test", "Two"), (first, last));
        // only whole names: a name that merely starts with a protected one is not protected
        string more = Accounts(("owner2", "Owners", "Resident"));
        Assert.Equal("Owners", AccountsFile.Load(more, "owner2", Pool("owner2"), Protected("owner")).First);
    }

    [Fact]
    public void A_protected_file_that_is_set_but_missing_refuses_every_account_and_says_why()
    {
        string accounts = Accounts(("tester2", "Test", "Two"));
        string missing = Path.Combine(_dir, "no-such-protected.txt");
        var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "tester2", Pool("tester2"), missing));
        Assert.Contains("protected-accounts file", ex.Message);
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void A_protected_file_that_cannot_be_read_refuses_every_account_and_says_why()
    {
        string accounts = Accounts(("tester2", "Test", "Two"));
        string f = Protected("owner");
        using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // held open with no sharing: unreadable
        {
            var ex = Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(accounts, "tester2", Pool("tester2"), f));
            Assert.Contains("protected-accounts file", ex.Message);
            Assert.Contains("could not be read", ex.Message);
            Assert.Contains(f, ex.Message);
        }
    }

    [Fact]
    public void With_no_protected_file_set_the_pool_alone_decides()
    {
        string accounts = Accounts(("tester2", "Test", "Two"));
        Assert.Equal("Test", AccountsFile.Load(accounts, "tester2", Pool("tester2"), null).First);
    }

    [Fact]
    public void The_account_check_holds_no_digest_hash_or_account_specific_code()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Automation", "TokenFile.cs"));
        int start = source.IndexOf("public static class AccountsFile", StringComparison.Ordinal);
        Assert.True(start >= 0, "AccountsFile not found in TokenFile.cs");
        string check = source[start..];
        Assert.DoesNotMatch("[0-9a-fA-F]{32,}", check);   // no hash constant
        // whole words: the password's secret kind, SensitiveKind.PasswordDigest, is not a name check
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\bDigests?\b|\bSHA\d*|\bHash|Cryptography|owner",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase), check);
    }
}
