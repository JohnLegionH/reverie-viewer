// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A scripted login for the dialog flows: each attempt gets the next scripted answer and every request is kept.</summary>
internal sealed class LoginFlow : ILoginPrompts
{
    public readonly List<TransportLoginRequest> Requests = [];
    public readonly List<LoginDialog> Shown = [];
    public readonly LoginModel Model;
    private readonly Func<int, TransportLoginResult>[] _script;
    private readonly Queue<Func<LoginDialog, LoginDialogResult>> _answers = new();

    public LoginFlow(string? settingsDir = null, CertificateTrust? trust = null, params Func<int, TransportLoginResult>[] script)
    {
        _script = script;
        Model = new LoginModel(StringTable.English(), opts =>
        {
            var fake = new FakeTransport
            {
                OnLogin = (r, _) =>
                {
                    int i = Requests.Count;
                    Requests.Add(r);
                    return Task.FromResult(_script[Math.Min(i, _script.Length - 1)](i));
                },
            };
            return new ViewerSession(opts, fake, new CoreLog(new ListSink(), "test"));
        }, null, settingsDir)
        { LoginUri = "https://grid.example.org:8002/", UserName = "Test User", Prompts = this, Trust = trust };
        Model.SetPassword("pw");
    }

    /// <summary>What the person does with the next dialog.</summary>
    public LoginFlow Answer(Func<LoginDialog, LoginDialogResult> answer) { _answers.Enqueue(answer); return this; }
    public LoginFlow Answer(LoginDialogResult result) => Answer(_ => result);

    public Task<LoginDialogResult> AskAsync(LoginDialog dialog, CancellationToken ct)
    {
        Shown.Add(dialog);
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue()(dialog) : LoginDialogResult.Cancelled);
    }

    public static Func<int, TransportLoginResult> Fail(string reason, string message = "", string messageId = "", string errorCode = "", string? mfaHash = null,
        Dictionary<string, string>? args = null, string certificate = "") =>
        _ => TransportLoginResult.Failed(reason, message) with
        {
            Reply = new LoginReply(reason, message, messageId, args ?? new(), errorCode, mfaHash is null ? null : new Secret(mfaHash, SensitiveKind.Token), certificate),
        };

    public static Func<int, TransportLoginResult> Ok(string? mfaHash = null) =>
        _ => FakeTransport.Ok(Guid.Parse("11111111-2222-3333-4444-555555555555")) with
        {
            Reply = mfaHash is null ? null : new LoginReply("", "", "", new Dictionary<string, string>(), "", new Secret(mfaHash, SensitiveKind.Token), ""),
        };
}

/// <summary>
/// The login failures that ask the person something, driven through the dialog models with no window and no network:
/// terms of service, the critical message, a required update, the multifactor token, and the failures that show nothing new.
/// Each test asserts the dialog shown and the fields of the next request.
/// </summary>
public class LoginDialogFlowTests
{
    private static LoginDialogResult Continue(bool agreed = true) => new(true, Agreed: agreed);

    [Fact]
    public async Task Terms_shown_with_the_grids_text_and_continue_logs_in_again_having_agreed()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("tos", "You must agree to the house rules."), LoginFlow.Ok()).Answer(Continue());

        Assert.True(await f.Model.LoginAsync());

        var d = Assert.Single(f.Shown);
        Assert.Equal(LoginDialogKind.Terms, d.Kind);
        Assert.Contains("You must agree to the house rules.", d.ReadOnlyText);
        Assert.NotNull(d.AgreeLabel);   // the agree box has to be ticked before Continue works
        Assert.Equal("Continue", d.ConfirmLabel);
        Assert.Equal("Cancel", d.CancelLabel);
        Assert.Equal(2, f.Requests.Count);
        Assert.False(f.Requests[0].Extras?.AgreeToTos ?? false);
        Assert.True(f.Requests[1].Extras!.AgreeToTos);
        Assert.False(f.Requests[1].Extras!.ReadCritical);
    }

    [Fact]
    public async Task Cancelling_the_terms_ends_the_attempt_and_shows_the_message_only_once()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("tos", "You must agree to the house rules.")).Answer(LoginDialogResult.Cancelled);

        Assert.False(await f.Model.LoginAsync());

        Assert.Single(f.Requests);
        Assert.Single(f.Shown);
        Assert.DoesNotContain("house rules", f.Model.Status);   // the dialog said it; the status line does not repeat it
        Assert.False(f.Model.Busy);
    }

    [Fact]
    public async Task A_terms_message_that_is_a_web_address_is_offered_as_a_link_that_opens_in_the_browser()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("tos", "https://grid.example.org/terms"), LoginFlow.Ok()).Answer(Continue());

        await f.Model.LoginAsync();

        var d = Assert.Single(f.Shown);
        Assert.Equal("https://grid.example.org/terms", d.Link);
        Assert.Equal("", d.ReadOnlyText);
    }

    [Fact]
    public async Task A_terms_failure_with_no_message_says_the_terms_are_accepted_on_the_grids_site()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("tos", ""), LoginFlow.Ok()).Answer(Continue());

        await f.Model.LoginAsync();

        var d = Assert.Single(f.Shown);
        Assert.Equal(LoginDialogKind.Terms, d.Kind);
        Assert.Contains("accept the Terms of Service", d.Body);
    }

    [Fact]
    public async Task The_critical_message_is_read_only_text_with_one_button_and_continue_logs_in_again_having_read_it()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("critical", "The grid moves on Friday."), LoginFlow.Ok()).Answer(Continue(agreed: false));

        Assert.True(await f.Model.LoginAsync());

        var d = Assert.Single(f.Shown);
        Assert.Equal(LoginDialogKind.Critical, d.Kind);
        Assert.Equal("The grid moves on Friday.", d.ReadOnlyText);
        Assert.Null(d.CancelLabel);
        Assert.Null(d.AgreeLabel);
        Assert.True(f.Requests[1].Extras!.ReadCritical);
        Assert.False(f.Requests[1].Extras!.AgreeToTos);
    }

    [Theory]
    [InlineData("E42", true)]
    [InlineData("", false)]
    public async Task The_critical_message_passes_its_error_code_on_when_the_grid_sent_one(string code, bool listed)
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("critical", "Read this.", errorCode: code), LoginFlow.Ok()).Answer(Continue(false));

        await f.Model.LoginAsync();

        var d = Assert.Single(f.Shown);
        Assert.Equal(listed, d.Fields.Any(x => x.Value == code));
    }

    [Fact]
    public async Task A_required_update_says_which_version_with_the_grids_message_and_ends_the_attempt()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("update", "Please get the new build.", args: new() { ["VERSION"] = "9.9.9" }));

        Assert.False(await f.Model.LoginAsync());

        var d = Assert.Single(f.Shown);
        Assert.Equal(LoginDialogKind.Update, d.Kind);
        Assert.Contains("Version 9.9.9 is required for login.", d.Body);
        Assert.Contains("Please get the new build.", d.Body);
        Assert.DoesNotContain("secondlife.com", d.Body);   // this viewer has no download page of its own to name yet
        Assert.Null(d.CancelLabel);
        Assert.Single(f.Requests);
        Assert.DoesNotContain("Please get the new build.", f.Model.Status);
    }

    [Fact]
    public async Task A_failure_with_no_known_reason_and_no_message_shows_the_unrecognised_reason_dialog()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("zzz-unheard-of")).Answer(LoginDialogResult.Cancelled);

        Assert.False(await f.Model.LoginAsync());

        var d = Assert.Single(f.Shown);
        Assert.Equal(LoginDialogKind.Unknown, d.Kind);
        Assert.Contains("login failed for an unrecognized reason", d.Body);
    }

    [Theory]
    [InlineData("key", "")]
    [InlineData("presence", "")]
    [InlineData("connect", "")]
    [InlineData("zzz-unheard-of", "The grid says no.")]
    public async Task The_failures_the_status_line_already_covers_show_no_dialog_and_nothing_twice(string reason, string message)
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail(reason, message));

        Assert.False(await f.Model.LoginAsync());

        Assert.Empty(f.Shown);
        if (message.Length > 0)
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(f.Model.Status, "The grid says no").Count);
    }

    [Fact]
    public async Task A_failure_that_carries_only_a_message_id_shows_no_dialog()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("zzz-unheard-of", messageId: "SomeMessageId"));

        await f.Model.LoginAsync();

        Assert.Empty(f.Shown);
    }

    [Fact]
    public async Task A_multifactor_challenge_asks_for_the_token_with_the_official_text_and_a_retry_carries_it_without_whitespace()
    {
        var f = new LoginFlow(null, null,
            LoginFlow.Fail("mfa_challenge", "Enter your code.", messageId: "LoginFailedAuthenticationMFARequired"), LoginFlow.Ok())
            .Answer(new LoginDialogResult(true, " 12 34\t56\n"));

        Assert.True(await f.Model.LoginAsync());

        var d = Assert.Single(f.Shown);
        Assert.Equal(LoginDialogKind.Token, d.Kind);
        Assert.NotNull(d.InputLabel);
        Assert.Contains("To continue logging in, enter a new token from your multifactor authentication app.", d.Body);
        Assert.Equal("Continue", d.ConfirmLabel);
        Assert.Equal("Cancel", d.CancelLabel);
        Assert.Equal("123456", f.Requests[1].Extras!.Token!.Reveal());
        Assert.False(f.Requests[0].Extras?.HasToken ?? false);
    }

    [Fact]
    public async Task A_multifactor_challenge_with_a_message_id_the_viewer_has_no_text_for_shows_the_grids_message()
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("mfa_challenge", "Type the six digits.", messageId: "SomethingNew")).Answer(LoginDialogResult.Cancelled);

        await f.Model.LoginAsync();

        Assert.Contains("Type the six digits.", Assert.Single(f.Shown).Body);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "  \t ")]
    [InlineData(true, "123456")]   // Cancel with a token typed still cancels
    public async Task An_empty_token_or_cancel_ends_the_attempt(bool cancelButton, string typed)
    {
        var f = new LoginFlow(null, null, LoginFlow.Fail("mfa_challenge", "Enter your code.", messageId: "LoginFailedAuthenticationMFARequired"), LoginFlow.Ok())
            .Answer(cancelButton ? new LoginDialogResult(false, typed) : new LoginDialogResult(true, typed));

        Assert.False(await f.Model.LoginAsync());

        Assert.Single(f.Requests);
        Assert.Single(f.Shown);
    }

    [Fact]
    public async Task The_hash_a_reply_hands_out_is_sent_with_the_next_attempt_and_the_token_is_cleared()
    {
        // a correct token is answered with a hash and then a terms failure; the next attempt carries the hash and an empty token
        var f = new LoginFlow(null, null,
            LoginFlow.Fail("mfa_challenge", "Enter your code.", messageId: "LoginFailedAuthenticationMFARequired"),
            LoginFlow.Fail("tos", "Rules.", mfaHash: "grid-hash-1"),
            LoginFlow.Ok())
            .Answer(new LoginDialogResult(true, "111111")).Answer(Continue());

        Assert.True(await f.Model.LoginAsync());

        Assert.Equal(3, f.Requests.Count);
        Assert.Equal("111111", f.Requests[1].Extras!.Token!.Reveal());
        Assert.Equal("grid-hash-1", f.Requests[2].Extras!.MfaHash!.Reveal());
        Assert.False(f.Requests[2].Extras!.HasToken);
        Assert.True(f.Requests[2].Extras!.AgreeToTos);
    }

    [Fact]
    public async Task Terms_met_in_the_middle_of_a_challenge_ask_for_a_fresh_token_before_the_retry()
    {
        // the token typed before the terms failure is probably stale by the time the terms are agreed
        var f = new LoginFlow(null, null,
            LoginFlow.Fail("mfa_challenge", "Enter your code.", messageId: "LoginFailedAuthenticationMFARequired"),
            LoginFlow.Fail("tos", "Rules."),
            LoginFlow.Ok())
            .Answer(new LoginDialogResult(true, "111111")).Answer(Continue()).Answer(new LoginDialogResult(true, "222222"));

        Assert.True(await f.Model.LoginAsync());

        Assert.Equal([LoginDialogKind.Token, LoginDialogKind.Terms, LoginDialogKind.Token], f.Shown.Select(d => d.Kind).ToArray());
        Assert.Equal(3, f.Requests.Count);
        Assert.Equal("222222", f.Requests[2].Extras!.Token!.Reveal());
        Assert.True(f.Requests[2].Extras!.AgreeToTos);
    }

    [Fact]
    public async Task Declining_the_fresh_token_after_the_terms_ends_the_attempt()
    {
        var f = new LoginFlow(null, null,
            LoginFlow.Fail("mfa_challenge", "Enter your code.", messageId: "LoginFailedAuthenticationMFARequired"), LoginFlow.Fail("tos", "Rules."), LoginFlow.Ok())
            .Answer(new LoginDialogResult(true, "111111")).Answer(Continue()).Answer(LoginDialogResult.Cancelled);

        Assert.False(await f.Model.LoginAsync());

        Assert.Equal(2, f.Requests.Count);
    }

    [Fact]
    public async Task The_multifactor_hash_is_kept_for_this_run_only_and_no_store_is_made()
    {
        string dir = TestTemp.NewFolderPath("login");
        var f = new LoginFlow(dir, null, LoginFlow.Fail("mfa_challenge", "Enter your code.", messageId: "LoginFailedAuthenticationMFARequired"), LoginFlow.Ok("grid-hash-2"))
            .Answer(new LoginDialogResult(true, "111111"));
        Assert.True(await f.Model.LoginAsync());

        // within the run a later login (a reconnect) sends the hash, with an empty token
        f.Model.SetPassword("pw");
        Assert.True(await f.Model.LoginAsync());
        Assert.Equal("grid-hash-2", f.Requests[^1].Extras!.MfaHash!.Reveal());
        Assert.False(f.Requests[^1].Extras!.HasToken);
        // on disk: only the remembered grid address may exist; no file holds the hash
        foreach (var file in Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : [])
            Assert.DoesNotContain("grid-hash-2", File.ReadAllText(file));
    }
}

/// <summary>The example dialogs the development automation shows: every listed kind makes a dialog with its buttons, and a name that is not a kind makes none.</summary>
public class LoginDialogSamplesTests
{
    [Fact]
    public void Every_sample_kind_makes_a_dialog_with_a_main_button_and_other_names_make_none()
    {
        var strings = StringTable.English();
        Assert.Equal(9, LoginDialogSamples.Kinds.Count);
        foreach (var kind in LoginDialogSamples.Kinds)
        {
            var d = LoginDialogSamples.For(strings, kind);
            Assert.NotNull(d);
            Assert.False(string.IsNullOrWhiteSpace(d.ConfirmLabel), kind);
            Assert.DoesNotContain("[[", d.Title + d.Body + d.ConfirmLabel);   // no string-table key is missing
        }
        Assert.Null(LoginDialogSamples.For(strings, "no-such-dialog"));
    }

    [Fact]
    public void The_trust_sample_offers_trust_and_the_error_samples_do_not()
    {
        var strings = StringTable.English();
        Assert.Equal("Trust", LoginDialogSamples.For(strings, "trust")!.ConfirmLabel);
        foreach (var kind in new[] { "certificate_error", "certificate_unreadable" })
            Assert.NotEqual("Trust", LoginDialogSamples.For(strings, kind)!.ConfirmLabel);
    }
}
