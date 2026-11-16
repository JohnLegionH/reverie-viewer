// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Session;

namespace Viewer.Shell;

/// <summary>
/// The login screen's logic, wired to the core's <see cref="ViewerSession"/>. The view (Viewer.Godot) binds its
/// controls to the properties and shows <see cref="Status"/>; every status text comes from the string table.
/// The password: the view hands it over once through <see cref="SetPassword"/> (and clears its own field); it is held
/// as a <see cref="Secret"/> and released the moment an attempt starts, whatever the outcome, so nothing here keeps it
/// after the attempt. The session itself never stores it.
/// </summary>
public sealed class LoginModel(StringTable strings, Func<SessionOptions, ViewerSession> sessionFactory, GridFetcher? fetcher = null, string? settingsDir = null)
{
    private readonly object _gate = new();
    private Secret? _password;
    private CancellationTokenSource? _attempt;

    public string LoginUri { get; set; } = "";

    /// <summary>The user settings a first login switches SwitchToSharedEnvAfterTeleport on in (llagent.cpp setFirstLogin); null: none are touched.</summary>
    public SettingsService? UserSettings { get; set; }

    /// <summary>Where the login flow's dialogs (terms, critical message, update, multifactor token, certificate) are shown and answered;
    /// null: none are shown and the failure goes to the status line, as before.</summary>
    public ILoginPrompts? Prompts { get; set; }

    /// <summary>The certificates the person chose to trust. The session factory hands the same object to each transport, so a choice made
    /// here applies to every HTTPS connection of the next attempt; null: none are trusted.</summary>
    public Viewer.Core.Net.CertificateTrust? Trust { get; set; }

    /// <summary>The login address the last attempt resolved to and sent the login to (empty before an attempt).</summary>
    public string ResolvedLoginUri { get; private set; } = "";

    /// <summary>Raised when <see cref="ResolvedLoginUri"/> changes.</summary>
    public event Action<string>? ResolvedChanged;

    /// <summary>How the typed grid presents itself (the placeholder look until it has answered).</summary>
    public GridBranding Branding { get; private set; } = GridBranding.None;

    /// <summary>Raised when <see cref="Branding"/> changes.</summary>
    public event Action<GridBranding>? BrandingChanged;

    /// <summary>Asks the typed grid for its grid-info document and takes its branding from it.</summary>
    public async Task RefreshBrandingAsync(string typed)
    {
        int mine = Interlocked.Increment(ref _brandingAsked);
        GridBranding found = GridBranding.None;
        if (fetcher is not null && GridAddress.Tidy(typed).Uri is { } uri)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var got = await fetcher(GridAddress.GridInfoOf(uri), cts.Token).ConfigureAwait(false);
                if (got.Status is >= 200 and < 300)
                    found = GridBranding.FromGridInfo(got.Body);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a grid that cannot be asked has the placeholder look */ }
        }
        // only the latest question counts: a slow earlier answer must not replace a later one
        if (mine != Volatile.Read(ref _brandingAsked) || found == Branding)
            return;
        Branding = found;
        BrandingChanged?.Invoke(found);
    }

    private int _brandingAsked;

    /// <summary>Add a grid's Look up, through the same outbound rules and certificate choices as the login (ten seconds at most).</summary>
    public async Task<Viewer.Shell.Grids.GridLookupResult> LookUpGridAsync(string typed)
    {
        if (fetcher is null)
            return new(null, "grid.add.no_answer", false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { return await Viewer.Shell.Grids.GridLookup.LookUpAsync(typed, fetcher, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(null, "grid.add.no_answer", false); }
    }

    /// <summary>Manage grids' Refresh info for one grid, as <see cref="LookUpGridAsync"/>.</summary>
    public async Task<Viewer.Shell.Grids.GridLookupResult> RefreshGridAsync(Viewer.Shell.Grids.GridEntry entry)
    {
        if (fetcher is null)
            return new(null, "grid.add.no_answer", false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { return await Viewer.Shell.Grids.GridLookup.RefreshAsync(entry, fetcher, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(null, "grid.add.no_answer", false); }
    }

    /// <summary>The address the screen starts with: the start-up option when given, else the last successful login address kept in
    /// <paramref name="settingsDir"/>, else empty. A start-up option that is not an address is ignored.</summary>
    public static string StartAddress(string? gridOption, string? settingsDir)
    {
        if (!string.IsNullOrWhiteSpace(gridOption) && GridAddress.Tidy(gridOption).Uri is { } given) return given.ToString();
        return settingsDir is null ? "" : LoginAddressStore.Load(settingsDir) ?? "";
    }

    /// <summary>Whether the session's log names the login host and port (<see cref="SessionOptions.LogLoginHost"/>).</summary>
    public bool LogLoginHost { get; set; }

    /// <summary>The folder that holds the block list's file for each account (<see cref="SessionOptions.MuteCacheDirectory"/>); null keeps the list in memory only.</summary>
    public string? MuteCacheDirectory { get; set; }
    /// <summary>Whether the session connects to and draws the regions around the agent's (<see cref="SessionOptions.Neighbours"/>). Off by default.</summary>
    public bool Neighbours { get; set; }

    /// <summary>The draw distance (metres) the session starts with: the preference, read before the login so the first agent updates carry it.</summary>
    public float DrawDistance { get; set; } = Viewer.Core.World.Interest.DefaultDrawDistance;
    /// <summary>The name as typed in the one username field. Setting it fills <see cref="FirstName"/> and <see cref="LastName"/>.</summary>
    public string UserName
    {
        get => _userName ?? LoginName.Display(FirstName, LastName);
        set
        {
            _userName = value ?? "";
            var parsed = LoginName.Parse(_userName);
            FirstName = parsed?.First ?? "";
            LastName = parsed?.Last ?? "";
        }
    }

    private string? _userName;
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string StartLocation { get; set; } = "last";

    public string Status { get; private set; } = strings.Get("login.status.ready");
    public bool Busy { get { lock (_gate) return _attempt is not null; } }
    public bool HasPassword { get { lock (_gate) return _password is not null; } }

    /// <summary>The session of the last successful login (the caller owns it from then on).</summary>
    public ViewerSession? Session { get; private set; }

    /// <summary>The code of the last refused attempt (the login service's own key, "cancelled", "exception", "invalid" for missing fields), or null after a success.
    /// What a retry may do depends on it (a credential refusal is not retried).</summary>
    public string? LastFailureCode { get; private set; }

    /// <summary>Raised when an attempt succeeded, with what was used. The model keeps none of it: whoever listens decides what to hold (the reconnect
    /// model keeps it in memory only).</summary>
    public event Action<LoginRecord>? Succeeded;

    /// <summary>Raised (on the thread that changed it) whenever <see cref="Status"/> changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Where a remembered login hash is kept for this user (null: remember password is not offered).</summary>
    public ILoginVault? Vault { get; set; }

    /// <summary>Whether the next successful login keeps its login hash in <see cref="Vault"/>.</summary>
    public bool RememberPassword { get; private set; }

    /// <summary>Ticks or unticks remember password; unticking forgets the hash saved for the grid and name now typed.</summary>
    public void SetRememberPassword(bool on)
    {
        RememberPassword = on;
        if (!on)
            Vault?.Forget(LoginUri, UserName);
    }

    /// <summary>Whether a hash is saved for the grid and name now typed.</summary>
    public bool HasSavedPassword => Vault is { Available: true } v && v.Load(LoginUri, UserName) is not null;

    /// <summary>Uses the saved hash for the next attempt in place of a typed password. False when none is saved.</summary>
    public bool UseSavedPassword()
    {
        if (Vault is not { Available: true } v || v.Load(LoginUri, UserName) is not { } hash)
            return false;
        lock (_gate)
        {
            _password = hash;
            _passwordIsSaved = true;
        }
        return true;
    }

    private bool _passwordIsSaved;

    public void SetPassword(string password)
    {
        lock (_gate)
        {
            _password = string.IsNullOrEmpty(password) ? null : new Secret(password, SensitiveKind.PasswordDigest);
            _passwordIsSaved = false;
        }
    }

    /// <summary>The same, for a password that is already held as a <see cref="Secret"/> (the automation login reads it
    /// from the accounts file straight into one, so it is never a plain string on this path).</summary>
    public void SetPassword(Secret password)
    {
        lock (_gate)
        {
            _password = password;
            _passwordIsSaved = false;
        }
    }

    /// <summary>Runs one login attempt. True when the session logged in; the status then follows it into the world. With
    /// <see cref="RememberPassword"/> on, a success keeps the login hash (never the password) in <see cref="Vault"/>; with it off, a success
    /// forgets any hash kept for this grid and name; a refusal by the grid of a saved hash forgets it.</summary>
    public async Task<bool> LoginAsync()
    {
        _attemptToSave = null;
        _attemptUsedSaved = false;
        bool ok = await LoginOnceAsync().ConfigureAwait(false);
        var vault = Vault;
        if (vault is { Available: true })
        {
            if (ok && _attemptToSave is { } keep)
                vault.Save(LoginUri, UserName, keep);
            else if (ok && !RememberPassword)
                vault.Forget(LoginUri, UserName);
            else if (!ok && _attemptUsedSaved && Viewer.Shell.RememberPassword.ForgetAfterFailure(LastFailureCode))
                vault.Forget(LoginUri, UserName);
        }
        _attemptToSave = null;
        return ok;
    }

    private Secret? _attemptToSave;
    private bool _attemptUsedSaved;

    private async Task<bool> LoginOnceAsync()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(LoginUri)) missing.Add(strings.Get("login.grid.label"));
        // a name typed in the one field that is not blank but cannot be split is a different problem from a missing one
        bool unsplittable = _userName is { } typed && typed.Trim().Length > 0 && LoginName.Parse(typed) is null;
        if (!unsplittable && (string.IsNullOrWhiteSpace(FirstName) || string.IsNullOrWhiteSpace(LastName))) missing.Add(strings.Get("login.username.label"));
        if (!HasPassword) missing.Add(strings.Get("login.password.label"));
        if (missing.Count > 0)
        {
            LastFailureCode = "invalid";
            SetStatus(strings.Get("login.status.missing", string.Join(", ", missing)));
            return false;
        }
        if (unsplittable)
        {
            LastFailureCode = "invalid";
            SetStatus(strings.Get("login.status.bad_name"));
            return false;
        }
        var tidy = GridAddress.Tidy(LoginUri);
        if (tidy.Uri is null)
        {
            LastFailureCode = "invalid";
            SetStatus(strings.Get(tidy.ProblemKey!));
            return false;
        }
        var uri = tidy.Uri;

        Secret password;
        CancellationTokenSource attempt;
        bool busy;
        lock (_gate)
        {
            busy = _attempt is not null;
            password = busy ? null! : _password!;
            if (!busy)
            {
                // the hash to keep if this attempt succeeds and the tick is on: the saved hash as it is, else the hash of the typed password
                _attemptUsedSaved = _passwordIsSaved;
                if (RememberPassword && Vault is { Available: true })
                    _attemptToSave = LoginHash.Is(password.Reveal()) ? password : new Secret(LoginHash.Of(password.Reveal()), SensitiveKind.PasswordDigest);
                _password = null;   // released now: this model does not keep it past the start of the attempt
                _passwordIsSaved = false;
            }
            attempt = busy ? null! : (_attempt = new CancellationTokenSource());
        }
        if (busy)
        {
            SetStatus(strings.Get("login.status.busy"));
            return false;
        }

        try
        {
            // the grid's own login address, looked up from the address that was typed (and only from it)
            var login = uri;
            if (fetcher is not null)
            {
                SetStatus(strings.Get("login.status.checking", uri));
                var resolved = await GridAddress.ResolveAsync(uri, fetcher, attempt.Token).ConfigureAwait(false);
                if (resolved.Login is null)
                {
                    LastFailureCode = "unreachable";
                    SetStatus(strings.Get(resolved.ProblemKey ?? "login.status.not_login_service", StatusText(resolved.Status)));
                    return false;
                }
                login = resolved.Login;
            }
            SetResolved(login.ToString());
            // the multifactor hash the grid handed out earlier in this run for this account (kept in memory only, never on disk)
            string hashKey = (login + "|" + FirstName.Trim() + " " + LastName.Trim()).ToLowerInvariant();
            var extras = new LoginExtras(MfaHash: RunMfaHash(hashKey));
            var pinnedNow = new HashSet<string>();
            while (true)
            {
                SetStatus(strings.Get("login.status.connecting"));
                var session = sessionFactory(new SessionOptions { LoginUri = login.ToString(), LogLoginHost = LogLoginHost, MuteCacheDirectory = MuteCacheDirectory, Neighbours = Neighbours, DrawDistance = DrawDistance, StartLocation = string.IsNullOrWhiteSpace(StartLocation) ? "last" : StartLocation.Trim() });
                session.StageChanged += OnStage;
                var outcome = await session.LoginAsync(new LoginCredentials(FirstName.Trim(), LastName.Trim(), password, extras), attempt.Token).ConfigureAwait(false);
                if (outcome.Reply?.MfaHash is { } handedOut)
                    lock (_gate) _mfaHashes[hashKey] = handedOut;
                LastFailureCode = outcome.Ok ? null : outcome.Code ?? "failed";
                if (outcome.Ok)
                {
                    Session = session;
                    // a first login switches the shared environment after teleport on, once and persistently (llagent.cpp setFirstLogin)
                    if (outcome.Reply?.FirstLogin == true)
                        UserSettings?.Set(Settings.SwitchToSharedEnvAfterTeleport, true);
                    Succeeded?.Invoke(new LoginRecord(FirstName.Trim(), LastName.Trim(), password, LoginUri));
                    if (settingsDir is not null) LoginAddressStore.Save(settingsDir, uri.ToString());
                    if (session.Stage == SessionStage.InWorld)
                        SetStatus(strings.Get("login.status.in_world"));
                    return true;
                }
                session.StageChanged -= OnStage;
                session.Dispose();
                var next = await AfterFailureAsync(outcome, extras, pinnedNow, login, attempt.Token).ConfigureAwait(false);
                if (next is null)
                    return false;
                extras = next;
            }
        }
        catch (OperationCanceledException)
        {
            LastFailureCode = "cancelled";
            SetStatus(strings.Get("login.status.cancelled"));
            return false;
        }
        finally
        {
            lock (_gate) _attempt = null;
            attempt.Dispose();
        }
    }

    private readonly Dictionary<string, Secret> _mfaHashes = new(StringComparer.Ordinal);

    private Secret? RunMfaHash(string key)
    {
        lock (_gate) return _mfaHashes.GetValueOrDefault(key);
    }

    /// <summary>
    /// What follows a refused attempt (llstartup.cpp and lllogininstance.cpp, handleLoginFailure): the terms of service, the grid's
    /// critical message, a required update, the multifactor challenge, a refused certificate, or a failure the status line already
    /// covers. Returns the fields of the next attempt when the person's answer calls for one, or null when the attempt is over
    /// (the status line says how). Without <see cref="Prompts"/> every failure goes to the status line, as it did before.
    /// </summary>
    private async Task<LoginExtras?> AfterFailureAsync(LoginOutcome outcome, LoginExtras extras, HashSet<string> pinnedNow, Uri login, CancellationToken ct)
    {
        var reply = outcome.Reply;
        // a hash in the grid's answer is sent from now on, with an empty token (handleLoginFailure)
        if (reply?.MfaHash is { } hash)
            extras = extras with { MfaHash = hash, Token = null };
        if (Prompts is not { } prompts || outcome.Code == "cancelled")
        {
            await ShowFailureAsync(outcome, login).ConfigureAwait(false);
            return null;
        }

        if (outcome.Code == "certificate" && outcome.Certificate is { } cert)
        {
            bool pin = cert.Kind == CertificateProblem.UnknownAuthority && cert.Sha256.Length > 0 && Trust is not null && pinnedNow.Add(cert.Host + "|" + cert.Sha256);
            if (pin)
            {
                var answer = await prompts.AskAsync(LoginDialogs.TrustCertificate(strings, cert), ct).ConfigureAwait(false);
                if (!answer.Confirmed)
                    return Ended(strings.Get("login.status.cancelled"));
                Trust!.Trust(cert);
                return extras;   // the next attempt's connections accept this certificate for this server
            }
            await prompts.AskAsync(LoginDialogs.CertificateError(strings, cert), ct).ConfigureAwait(false);
            return Ended(strings.Get("login.status.failed_header"));
        }

        string reason = reply is { Reason.Length: > 0 } ? reply.Reason : outcome.Code ?? "";
        switch (reason)
        {
            case "tos":
            {
                var dialog = LoginDialogs.Terms(strings, reply?.Message ?? outcome.Message ?? "");
                var answer = await prompts.AskAsync(dialog, ct).ConfigureAwait(false);
                if (!answer.Confirmed || (dialog.AgreeLabel is not null && !answer.Agreed))
                    return Ended(strings.Get("login.status.cancelled"));
                return await AskTokenAgainAsync(prompts, extras with { AgreeToTos = true }, ct).ConfigureAwait(false);
            }
            case "critical":
            {
                var answer = await prompts.AskAsync(LoginDialogs.Critical(strings, reply ?? LoginReply.Empty with { Message = outcome.Message ?? "" }), ct).ConfigureAwait(false);
                if (!answer.Confirmed)
                    return Ended(strings.Get("login.status.cancelled"));
                return await AskTokenAgainAsync(prompts, extras with { ReadCritical = true }, ct).ConfigureAwait(false);
            }
            case "update":
                await prompts.AskAsync(LoginDialogs.Update(strings, reply ?? LoginReply.Empty with { Message = outcome.Message ?? "" }), ct).ConfigureAwait(false);
                return Ended(strings.Get("login.status.failed_header"));
            case "mfa_challenge":
            {
                var answer = await prompts.AskAsync(LoginDialogs.Token(strings, LoginDialogs.TokenBody(strings, reply)), ct).ConfigureAwait(false);
                string token = LoginDialogs.StripWhitespace(answer.Text);
                if (!answer.Confirmed || token.Length == 0)
                    return Ended(strings.Get("login.status.cancelled"));
                return extras with { Token = new Secret(token, SensitiveKind.Token) };
            }
        }

        // the reasons the status line already covers (key, presence, connect) and any failure that carries words of its own show no dialog
        bool covered = reason is "key" or "presence" or "connect" || !string.IsNullOrWhiteSpace(outcome.Message) || !string.IsNullOrEmpty(reply?.MessageId);
        if (reply is not null && !covered)
        {
            await prompts.AskAsync(LoginDialogs.Unknown(strings), ct).ConfigureAwait(false);
            return Ended(strings.Get("login.status.failed_header"));
        }
        await ShowFailureAsync(outcome, login).ConfigureAwait(false);
        return null;
    }

    /// <summary>A terms or critical-message answer that comes in the middle of a multifactor challenge: the token typed before is very
    /// likely out of date by now, so a new one is asked for before the retry (SL-18511 in the official viewer).</summary>
    private async Task<LoginExtras?> AskTokenAgainAsync(ILoginPrompts prompts, LoginExtras extras, CancellationToken ct)
    {
        if (!extras.HasToken)
            return extras;
        var answer = await prompts.AskAsync(LoginDialogs.Token(strings, strings.Get("login.token.required")), ct).ConfigureAwait(false);
        string token = LoginDialogs.StripWhitespace(answer.Text);
        if (!answer.Confirmed || token.Length == 0)
            return Ended(strings.Get("login.status.cancelled"));
        return extras with { Token = new Secret(token, SensitiveKind.Token) };
    }

    /// <summary>Ends the attempt with this status text (the dialog has already said the rest) and returns null.</summary>
    private LoginExtras? Ended(string status)
    {
        SetStatus(status);
        return null;
    }

    /// <summary>The status line for a failure no dialog covers.</summary>
    private async Task ShowFailureAsync(LoginOutcome outcome, Uri login)
    {
        if (outcome.Code == "cancelled")
            SetStatus(strings.Get("login.status.cancelled"));
        else if (LooksLikeNotALoginService(outcome))
            SetStatus(strings.Get("login.status.not_login_service", StatusText(await ProbeStatusAsync(login).ConfigureAwait(false))));
        else
            // the header, then the grid's own message as it sent it; with no message the header alone, never the reason code
            // (llstartup.cpp 1153-1211: "LoginFailedHeader\n" and the message)
            SetStatus(string.IsNullOrWhiteSpace(outcome.Message) ? strings.Get("login.status.failed_header") : strings.Get("login.status.failed", outcome.Message));
    }

    // The library reports a reply that is not XML-RPC (an empty 404, an HTML page) as a parse failure.
    private static bool LooksLikeNotALoginService(LoginOutcome o) =>
        o.Message is { } m && (m.Contains("deserialize", StringComparison.OrdinalIgnoreCase) || m.Contains("unrecognized format", StringComparison.OrdinalIgnoreCase));

    private async Task<int?> ProbeStatusAsync(Uri login)
    {
        if (fetcher is null) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return (await fetcher(login, cts.Token).ConfigureAwait(false)).Status;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private string StatusText(int? status) => status is int s ? strings.Get("login.status.http", s) : strings.Get("login.status.no_answer");

    private void SetResolved(string text)
    {
        ResolvedLoginUri = text;
        ResolvedChanged?.Invoke(text);
    }

    /// <summary>Cancels the attempt in progress, if any.</summary>
    public void Cancel()
    {
        lock (_gate) _attempt?.Cancel();
    }

    private void OnStage(SessionStage stage)
    {
        string key = stage == SessionStage.InWorld ? "login.status.in_world" : "login.status.stage." + stage;
        if (strings.Has(key))
            SetStatus(strings.Get(key));
    }

    /// <summary>Says something on the login screen's status line (a notice about the start, not a login step).</summary>
    public void Announce(string text) => SetStatus(text);

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }
}

/// <summary>What a successful login used: the names, the password (a secret) and the address typed. For the listener's memory only.</summary>
public sealed record LoginRecord(string FirstName, string LastName, Secret Password, string LoginUri);
