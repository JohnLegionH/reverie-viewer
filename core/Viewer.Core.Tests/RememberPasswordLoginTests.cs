// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The login model's remember-password rules, with a vault in memory and a scripted login (no window, no network): a ticked login
/// keeps only the hash, an unticked one keeps nothing and forgets what was kept, the saved hash is what the next attempt sends,
/// unticking forgets it, and a refusal by the grid of the saved hash forgets it while a connection failure does not.
/// </summary>
public class RememberPasswordLoginTests
{
    private sealed class MemoryVault : ILoginVault
    {
        public readonly Dictionary<string, string> Kept = new();
        public bool Available => true;
        public string? UnavailableKey => null;
        public void Save(string grid, string name, Secret hash) => Kept[RememberPassword.EntryKey(grid, name)] = hash.Reveal();
        public Secret? Load(string grid, string name) =>
            Kept.TryGetValue(RememberPassword.EntryKey(grid, name), out var h) ? new Secret(h, SensitiveKind.PasswordDigest) : null;
        public bool Forget(string grid, string name) => Kept.Remove(RememberPassword.EntryKey(grid, name));
    }

    private static (LoginFlow Flow, MemoryVault Vault) Setup(params Func<int, Viewer.Core.Session.TransportLoginResult>[] script)
    {
        var f = new LoginFlow(null, null, script);
        var v = new MemoryVault();
        f.Model.Vault = v;
        return (f, v);
    }

    [Fact]
    public async Task A_ticked_login_keeps_the_hash_of_the_password_and_never_the_password()
    {
        var (f, v) = Setup(LoginFlow.Ok());
        f.Model.SetRememberPassword(true);
        Assert.True(f.Model.RememberPassword);
        Assert.True(await f.Model.LoginAsync());
        Assert.Equal(LoginHash.Of("pw"), Assert.Single(v.Kept).Value);
        Assert.DoesNotContain("pw", v.Kept.Values);
    }

    [Fact]
    public async Task An_unticked_login_keeps_nothing_and_forgets_a_hash_kept_before()
    {
        var (f, v) = Setup(LoginFlow.Ok());
        v.Save(f.Model.LoginUri, f.Model.UserName, new Secret(LoginHash.Of("old"), SensitiveKind.PasswordDigest));
        f.Model.SetRememberPassword(false);
        Assert.True(await f.Model.LoginAsync());
        Assert.Empty(v.Kept);
    }

    [Fact]
    public async Task The_saved_hash_is_what_the_next_attempt_sends()
    {
        var (f, v) = Setup(LoginFlow.Ok());
        string hash = LoginHash.Of("remembered");
        v.Save(f.Model.LoginUri, f.Model.UserName, new Secret(hash, SensitiveKind.PasswordDigest));
        f.Model.SetPassword("");
        Assert.True(f.Model.HasSavedPassword);
        Assert.True(f.Model.UseSavedPassword());
        f.Model.SetRememberPassword(true);
        Assert.True(await f.Model.LoginAsync());
        Assert.Equal(hash, Assert.Single(f.Requests).Password.Reveal());
        Assert.Equal(hash, Assert.Single(v.Kept).Value);   // kept as it was, not hashed again
    }

    [Fact]
    public void Nothing_saved_means_nothing_to_use()
    {
        var (f, _) = Setup(LoginFlow.Ok());
        Assert.False(f.Model.HasSavedPassword);
        Assert.False(f.Model.UseSavedPassword());
    }

    [Fact]
    public void Unticking_forgets_the_hash_saved_for_the_typed_grid_and_name()
    {
        var (f, v) = Setup(LoginFlow.Ok());
        v.Save(f.Model.LoginUri, f.Model.UserName, new Secret(LoginHash.Of("x"), SensitiveKind.PasswordDigest));
        v.Save(f.Model.LoginUri, "Other User", new Secret(LoginHash.Of("y"), SensitiveKind.PasswordDigest));
        f.Model.SetRememberPassword(true);
        f.Model.SetRememberPassword(false);
        Assert.False(f.Model.RememberPassword);
        Assert.Equal(RememberPassword.EntryKey(f.Model.LoginUri, "Other User"), Assert.Single(v.Kept).Key);
    }

    [Fact]
    public async Task A_refusal_of_the_saved_hash_forgets_it()
    {
        var (f, v) = Setup(LoginFlow.Fail("key", "Could not authenticate your avatar."));
        v.Save(f.Model.LoginUri, f.Model.UserName, new Secret(LoginHash.Of("stale"), SensitiveKind.PasswordDigest));
        f.Model.SetPassword("");
        f.Model.SetRememberPassword(true);
        Assert.True(f.Model.UseSavedPassword());
        Assert.False(await f.Model.LoginAsync());
        Assert.Single(f.Requests);
        Assert.Empty(v.Kept);
    }

    [Fact]
    public async Task A_connection_failure_with_the_saved_hash_keeps_it()
    {
        var (f, v) = Setup(LoginFlow.Fail("connect", ""));
        v.Save(f.Model.LoginUri, f.Model.UserName, new Secret(LoginHash.Of("good"), SensitiveKind.PasswordDigest));
        f.Model.SetPassword("");
        f.Model.SetRememberPassword(true);
        Assert.True(f.Model.UseSavedPassword());
        Assert.False(await f.Model.LoginAsync());
        Assert.Single(f.Requests);
        Assert.Single(v.Kept);
    }

    [Fact]
    public async Task A_refusal_of_a_typed_password_leaves_a_saved_hash_alone()
    {
        var (f, v) = Setup(LoginFlow.Fail("key", "Could not authenticate your avatar."));
        v.Save(f.Model.LoginUri, f.Model.UserName, new Secret(LoginHash.Of("good"), SensitiveKind.PasswordDigest));
        f.Model.SetRememberPassword(true);
        Assert.False(await f.Model.LoginAsync());
        Assert.Single(f.Requests);
        Assert.Single(v.Kept);
    }
}
