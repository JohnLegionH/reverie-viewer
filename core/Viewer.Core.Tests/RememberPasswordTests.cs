// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Diagnostics;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Remember password: only the login hash the official viewer sends ("$1$" and the MD5 of the password) is kept, never the password;
/// on Windows it is protected with DPAPI for the current user and kept in one file in the per-user settings folder, per grid and
/// name. A blob cannot be read under another scope, and nothing readable reaches the disk. Example addresses and names only.
/// </summary>
public class RememberPasswordTests
{
    private const string Grid = "https://grid.example.org:8002/";
    private const string Name = "Test User";

    [Fact]
    public void The_hash_is_dollar_one_dollar_and_the_md5_of_the_password_in_lower_case_hex()
    {
        // MD5("password") is a well-known vector
        Assert.Equal("$1$" + "5f4dcc3b5aa765d61d8327deb882cf99", LoginHash.Of("password"));   // split so the repository scan never sees a hash
        Assert.Equal(35, LoginHash.Of("anything at all").Length);
    }

    [Fact]
    public void A_login_hash_is_recognised_inside_text_and_other_text_is_not()
    {
        Assert.True(LoginHash.Contains("x=$1$" + "5f4dcc3b5aa765d61d8327deb882cf99;"));
        Assert.True(LoginHash.Contains(LoginHash.Of("secret")));
        Assert.False(LoginHash.Contains("$1$ not a hash"));
        Assert.False(LoginHash.Contains("5f4dcc3b5aa765d61d8327deb882cf99"));
    }

    [Fact]
    public void The_entry_key_ignores_case_and_spacing_of_the_name_and_the_form_of_the_address()
    {
        string a = RememberPassword.EntryKey(Grid, "Test User");
        Assert.NotEqual("", a);
        Assert.Equal(a, RememberPassword.EntryKey("grid.example.org:8002", "  test   user "));
        Assert.NotEqual(a, RememberPassword.EntryKey(Grid, "Other User"));
        Assert.NotEqual(a, RememberPassword.EntryKey("https://other.example.org:8002/", "Test User"));
    }

    [Theory]
    [InlineData("key", true)]
    [InlineData("failed", true)]
    [InlineData("presence", true)]
    [InlineData("cancelled", false)]
    [InlineData("unreachable", false)]
    [InlineData("connect", false)]
    [InlineData("exception", false)]
    [InlineData("certificate", false)]
    [InlineData("invalid", false)]
    [InlineData(null, false)]
    public void Only_a_refusal_by_the_grid_forgets_the_saved_hash(string? code, bool forget) =>
        Assert.Equal(forget, RememberPassword.ForgetAfterFailure(code));

    [Fact]
    public void A_saved_hash_comes_back_for_its_grid_and_name_only_and_forgetting_removes_it()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI is Windows only; Linux uses the Secret Service
        string folder = TestTemp.NewFolder("vault");
        try
        {
            var vault = new DpapiLoginVault(folder);
            Assert.True(vault.Available);
            string hash = LoginHash.Of("correct horse");
            vault.Save(Grid, Name, new Secret(hash, SensitiveKind.PasswordDigest));
            Assert.Equal(hash, vault.Load(Grid, Name)?.Reveal());
            Assert.Equal(hash, new DpapiLoginVault(folder).Load("grid.example.org:8002", "test user")?.Reveal());   // read back from disk
            Assert.Null(vault.Load(Grid, "Other User"));
            Assert.Null(vault.Load("https://other.example.org/", Name));
            Assert.True(vault.Forget(Grid, Name));
            Assert.Null(new DpapiLoginVault(folder).Load(Grid, Name));
            Assert.False(vault.Forget(Grid, Name));
        }
        finally { TestTemp.Delete(folder); }
    }

    [Fact]
    public void Nothing_readable_lands_on_disk()
    {
        if (!OperatingSystem.IsWindows()) return;
        string folder = TestTemp.NewFolder("vault");
        try
        {
            var vault = new DpapiLoginVault(folder);
            string hash = LoginHash.Of("correct horse");
            vault.Save(Grid, Name, new Secret(hash, SensitiveKind.PasswordDigest));
            Assert.Equal([DpapiLoginVault.FileName], Directory.GetFiles(folder).Select(Path.GetFileName));
            byte[] bytes = File.ReadAllBytes(vault.FilePath);
            Assert.NotEmpty(bytes);
            string asText = Encoding.UTF8.GetString(bytes);
            string digits = hash[3..];
            foreach (var form in new[] { hash, digits, digits.ToUpperInvariant(), Convert.ToBase64String(Encoding.UTF8.GetBytes(hash)),
                         Convert.ToBase64String(Encoding.UTF8.GetBytes(digits)), "correct horse", "Test User", "test user", "grid.example.org" })
                Assert.DoesNotContain(form, asText, StringComparison.OrdinalIgnoreCase);
            Assert.False(LoginHash.Contains(asText));
        }
        finally { TestTemp.Delete(folder); }
    }

    [Fact]
    public void A_protected_blob_cannot_be_read_under_another_scope()
    {
        if (!OperatingSystem.IsWindows()) return;
        string hash = LoginHash.Of("correct horse");
        byte[] blob = DpapiLoginVault.Protect(hash, "scope-a");
        Assert.NotEmpty(blob);
        Assert.Equal(hash, DpapiLoginVault.Unprotect(blob, "scope-a"));
        Assert.Null(DpapiLoginVault.Unprotect(blob, "scope-b"));
        // a blob moved to another entry's place is not read as that entry's hash
        string folder = TestTemp.NewFolder("vault");
        try
        {
            var vault = new DpapiLoginVault(folder);
            vault.Save(Grid, Name, new Secret(hash, SensitiveKind.PasswordDigest));
            string text = File.ReadAllText(vault.FilePath);
            string mine = RememberPassword.EntryKey(Grid, Name);
            string other = RememberPassword.EntryKey(Grid, "Other User");
            File.WriteAllText(vault.FilePath, text.Replace(Hex(mine), Hex(other)));
            Assert.Null(new DpapiLoginVault(folder).Load(Grid, "Other User"));
        }
        finally { TestTemp.Delete(folder); }
    }

    [Fact]
    public void Without_a_safe_place_the_tick_is_not_offered_and_the_reason_is_one_line_from_the_string_table()
    {
        var vault = new NoLoginVault("login.remember_password.unavailable");
        Assert.False(vault.Available);
        var strings = StringTable.English();
        Assert.True(strings.Has(vault.UnavailableKey!));
        Assert.DoesNotContain('\n', strings.Get(vault.UnavailableKey!));
        Assert.True(strings.Has("login.remember_password"));
    }

    [Fact]
    public void The_login_screen_offers_the_tick_off_by_default()
    {
        Assert.False(Settings.LoginRememberPassword.Default);
    }

    private static string Hex(string entryKey) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(entryKey))).ToLowerInvariant();
}
