// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The ADR-002 hardware floor as a plain function over (backend, API version).</summary>
public class RenderFloorTests
{
    [Theory]
    [InlineData("vulkan", "1.2.198", StartupVerdict.BelowFloor, 4)]
    [InlineData("vulkan", "1.3.0", StartupVerdict.Approved, 0)]           // at the floor
    [InlineData("vulkan", "1.4.325", StartupVerdict.Approved, 0)]         // this machine (S-1)
    [InlineData("vulkan", "2.0", StartupVerdict.Approved, 0)]
    [InlineData("vulkan", "0.9.1", StartupVerdict.BelowFloor, 4)]
    [InlineData("d3d12", "11_1", StartupVerdict.BelowFloor, 4)]
    [InlineData("d3d12", "12_0", StartupVerdict.Approved, 0)]             // at the floor; this machine (S-1)
    [InlineData("d3d12", "12_2", StartupVerdict.Approved, 0)]
    [InlineData("d3d12", "13_0", StartupVerdict.Approved, 0)]
    [InlineData("opengl3", "3.3.0 NVIDIA 581.57", StartupVerdict.UnapprovedBackend, 3)]
    [InlineData("opengl3_angle", "3.0", StartupVerdict.UnapprovedBackend, 3)]
    [InlineData("metal", "3.0", StartupVerdict.UnapprovedBackend, 3)]   // recorded for macOS later; not approved on this build
    [InlineData("", "", StartupVerdict.UnapprovedBackend, 3)]
    [InlineData("vulkan", "", StartupVerdict.UnknownVersion, 4)]         // never passes on a guess
    [InlineData("vulkan", "unknown", StartupVerdict.UnknownVersion, 4)]
    [InlineData("d3d12", "12.0", StartupVerdict.UnknownVersion, 4)]
    public void The_floor_decides_by_backend_and_version(string backend, string api, StartupVerdict verdict, int exit)
    {
        var d = RenderFloor.Check(backend, api, "Test GPU");
        Assert.Equal(verdict, d.Verdict);
        Assert.Equal(exit, d.ExitCode);
        Assert.Equal(verdict == StartupVerdict.Approved, d.Approved);
    }

    [Fact]
    public void The_exit_codes_for_an_unapproved_backend_and_for_below_the_floor_are_distinct_and_non_zero()
    {
        Assert.Equal(3, RenderFloor.ExitUnapprovedBackend);   // the R-1 refusal, unchanged 
        Assert.Equal(4, RenderFloor.ExitBelowFloor);
    }

    [Fact]
    public void Refusal_messages_come_from_the_string_table_and_say_what_is_required_and_what_was_found()
    {
        var en = StringTable.English();
        var below = RenderFloor.Check("vulkan", "1.2.198", "Old GPU").Message(en);
        Assert.Contains("Vulkan 1.3", below);
        Assert.Contains("12_0", below);
        Assert.Contains("Old GPU", below);
        Assert.Contains("1.2", below);
        var gl = RenderFloor.Check("opengl3", "3.3.0", "Any GPU").Message(en);
        Assert.Contains("requires Vulkan or Direct3D 12", gl);   // what S-1's opengl3 case checks
        Assert.Contains("opengl3", gl);
        Assert.DoesNotContain("[[", below + gl);                 // no missing key
        // Localisable: the same decision in another table is that table's text.
        Assert.StartsWith("[", RenderFloor.Check("d3d12", "11_1", "GPU").Message(StringTable.Pseudo()));
        Assert.Equal("", RenderFloor.Check("d3d12", "12_0", "GPU").Message(en));
    }
}

/// <summary>ADR-019: engine text reaches the log only through the redacting route.</summary>
public class EngineLogRouteTests
{
    private const string Url = "https://grid.invalid:9000/CAPS/0f1e2d3c-secret-path/";
    private const string Digest = "$1$0123456789abcdef0123456789abcdef";
    private const string Sid = "11111111-2222-3333-4444-555555555555";
    private const string Text = "texture fetch failed " + Url + " digest " + Digest + " session " + Sid;

    private static string Route(Redactor? redactor)
    {
        var sink = new ListSink();
        var route = new EngineLogRoute(new CoreLog(sink, "test", redactor));
        route.Error(0, "fetch", "res://x.cs", 12, Text, Text);
        route.Error(1, "fetch", "res://x.cs", 13, Text, "");
        route.Message(Text, isError: true);
        route.Message(Text, isError: false);
        Assert.Equal(["engine.error", "engine.warning", "engine.stderr", "engine.message"], sink.Entries.Select(e => e.Event));
        return sink.Text;
    }

    [Fact]
    public void Engine_errors_are_counted_and_warnings_and_messages_are_not()
    {
        var route = new EngineLogRoute(new CoreLog(new ListSink(), "test"));
        Assert.Equal(0, route.Errors);
        route.Error(0, "free", "x.cpp", 1, "Parameter \"material\" is null.", "");
        route.Error(1, "free", "x.cpp", 2, "a warning", "");
        route.Error(2, "free", "x.cpp", 3, "a script error", "");
        route.Error(3, "free", "x.cpp", 4, "a shader error", "");
        route.Message("printed", isError: true);
        route.Message("printed", isError: false);
        Assert.Equal(3, route.Errors);
    }

    [Fact]
    public void Engine_warnings_are_counted_apart_from_errors()
    {
        var route = new EngineLogRoute(new CoreLog(new ListSink(), "test"));
        Assert.Equal(0, route.Warnings);
        route.Error(1, "free", "x.cpp", 2, "a warning", "");
        route.Error(1, "free", "x.cpp", 3, "another warning", "");
        route.Error(0, "free", "x.cpp", 4, "an error", "");
        route.Message("printed", isError: true);
        Assert.Equal(2, route.Warnings);
        Assert.Equal(1, route.Errors);
    }

    [Fact]
    public void An_engine_error_with_a_url_path_a_digest_and_a_session_id_is_logged_redacted()
    {
        var text = Route(null);
        Assert.DoesNotContain("/CAPS/", text);
        Assert.DoesNotContain("secret-path", text);
        Assert.DoesNotContain(Digest, text);
        Assert.DoesNotContain(Sid, text);
        Assert.Contains("https://grid.invalid:9000/[redacted:path]", text);
        Assert.Contains("[redacted:PasswordDigest]", text);
        Assert.Contains("[redacted:SessionId]", text);
        Assert.Contains("texture fetch failed", text);   // the useful part survives
    }

    [Theory]
    [InlineData(SensitiveKind.CapabilityUrl, "secret-path")]
    [InlineData(SensitiveKind.PasswordDigest, Digest)]
    [InlineData(SensitiveKind.SessionId, Sid)]
    public void Negative_control_switching_off_one_redaction_leaks_exactly_that_value(SensitiveKind off, string leaked)
    {
        // The same engine text through the same route, with one redaction off: that value now reaches the sink, so it
        // is the route's redaction (EngineText -> Redactor) that removes it, not anything upstream.
        Assert.Contains(leaked, Route(Redactor.WithoutForTests(off)));
    }

    [Fact]
    public void Negative_control_engine_text_logged_as_a_plain_string_would_keep_its_session_id()
    {
        // The id-shaped rule applies to EngineText only (the core's own fields legitimately carry ids); the route wraps every
        // engine string in EngineText, which is what gets it the stricter scrub.
        var sink = new ListSink();
        new CoreLog(sink, "test").Error("engine.error", null, CoreLog.F("code", Text));
        Assert.Contains(Sid, sink.Text);
        Assert.Equal("[engine-text]", new EngineText(Text).ToString());   // and EngineText never renders raw on its own
    }
}

/// <summary>ADR-020: the string table, pseudo-localisation, the no-literal-UI-text scan and accessible names.</summary>
public class StringTableTests
{
    private static readonly StringTable En = StringTable.English();

    /// <summary>Every key the host logic uses: the login screen's controls, the floor messages and the model's status keys.</summary>
    private static IEnumerable<string> UsedKeys()
    {
        foreach (var c in LoginScreenSpec.Controls)
        {
            yield return c.LabelKey;
            yield return c.AccessibleNameKey;
            if (c.HintKey is not null) yield return c.HintKey;
        }
        yield return LoginScreenSpec.TitleKey;
        var shell = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell");
        foreach (var f in Directory.GetFiles(shell, "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"""((?:login|startup)\.[a-z_.]+)"""))
                yield return m.Groups[1].Value;
        foreach (var stage in new[] { "LoggedIn", "SimConnected", "EventQueueRunning", "MovementComplete" })
            yield return "login.status.stage." + stage;
    }

    [Fact]
    public void Every_key_the_host_uses_is_in_the_table()
    {
        var missing = UsedKeys().Distinct().Where(k => !En.Has(k) && !k.EndsWith('.')).ToList();
        Assert.Empty(missing);
        Assert.Equal("[[no.such.key]]", En.Get("no.such.key"));   // a missing key is visible, never blank
    }

    [Fact]
    public void Pseudo_localisation_lengthens_accents_and_brackets_every_string_and_keeps_placeholders()
    {
        var pseudo = StringTable.Pseudo();
        Assert.Equal(En.Keys.OrderBy(k => k), pseudo.Keys.OrderBy(k => k));
        foreach (var key in En.Keys)
        {
            string a = En.Get(key), b = pseudo.Get(key);
            Assert.StartsWith("[", b);
            Assert.EndsWith("]", b);
            Assert.True(b.Length >= a.Length * 1.3, $"{key}: {b.Length} < 1.3 x {a.Length}");
            foreach (Match m in Regex.Matches(a, @"\{\d+\}"))
                Assert.Contains(m.Value, b);
        }
        Assert.Contains("á", pseudo.Get("login.button.login") + pseudo.Get("login.title"));
    }

    private static readonly Regex UiLiteral = new(
        @"\.(Text|PlaceholderText|TooltipText|AccessibilityName|AccessibilityDescription|Title|DialogText)\s*=\s*\$?@?""" +
        @"|\b(Text|PlaceholderText|TooltipText|AccessibilityName)\s*=\s*\$?@?""" +
        @"|\.(SetText|AddItem|SetTooltipText)\(\s*\$?@?""" +
        @"|OS\.Alert\(\s*\$?@?""", RegexOptions.Compiled);

    internal static List<string> UiLiterals(string source) =>
        source.Split('\n').Select((l, i) => (Line: l.Trim(), No: i + 1)).Where(x => !x.Line.StartsWith("//") && UiLiteral.IsMatch(x.Line))
            .Select(x => $"{x.No}: {x.Line}").ToList();

    [Fact]
    public void The_host_sets_no_user_facing_text_from_a_literal()
    {
        var host = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        var found = Directory.GetFiles(host, "*.cs").SelectMany(f => UiLiterals(File.ReadAllText(f)).Select(v => Path.GetFileName(f) + ":" + v)).ToList();
        Assert.Empty(found);
    }

    [Fact]
    public void Negative_control_the_scan_finds_literal_ui_text()
    {
        var src = "var b = new Button { Text = \"Log in\" };\nlabel.Text = $\"Hello {name}\";\nedit.PlaceholderText = \"First\";\nx.AccessibilityName = \"Pw\";\nok.Text = strings.Get(\"login.title\");";
        Assert.Equal(4, UiLiterals(src).Count);
    }

    [Fact]
    public void Every_login_control_has_a_label_and_an_accessible_name_from_the_table()
    {
        Assert.Equal(["grid", "resolved", "username", "password", "start", "login", "cancel", "status"], LoginScreenSpec.Controls.Select(c => c.Id));
        foreach (var c in LoginScreenSpec.Controls)
        {
            Assert.False(string.IsNullOrWhiteSpace(En.Get(c.AccessibleNameKey)), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(En.Get(c.LabelKey)), c.Id);
            Assert.DoesNotContain("[[", En.Get(c.AccessibleNameKey) + En.Get(c.LabelKey));
        }
        Assert.Single(LoginScreenSpec.Controls, c => c.Kind == LoginControlKind.Secret);
    }
}

/// <summary>The login screen's model against the fake transport.</summary>
public class LoginModelTests
{
    private const string Password = "correct horse battery staple";
    private static readonly StringTable En = StringTable.English();

    private static (LoginModel Model, FakeTransport Fake, List<string> Statuses) Model(Action<FakeTransport>? setup = null)
    {
        var fake = new FakeTransport();
        setup?.Invoke(fake);
        var log = new CoreLog(new ListSink(), "test");
        var m = new LoginModel(En, opts => new ViewerSession(opts, fake, log))
        {
            LoginUri = "https://grid.invalid:8002/", FirstName = "Test", LastName = "User", StartLocation = "uri:Alpha&128&128&25",
        };
        var statuses = new List<string>();
        m.StatusChanged += statuses.Add;
        m.SetPassword(Password);
        return (m, fake, statuses);
    }

    /// <summary>No field of the model holds the password (as a Secret or as text) after the attempt.</summary>
    private static void NotRetained(LoginModel m)
    {
        Assert.False(m.HasPassword);
        foreach (var f in typeof(LoginModel).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
        {
            var v = f.GetValue(m);
            Assert.False(v is Secret, $"{f.Name} holds a Secret");
            Assert.False(v is string s && s.Contains(Password), $"{f.Name} holds the password");
        }
    }

    [Fact]
    public async Task A_successful_login_reaches_the_world_with_status_from_the_table()
    {
        var (m, fake, statuses) = Model();
        Assert.True(await m.LoginAsync());
        Assert.NotNull(m.Session);
        var login = Assert.Single(fake.Logins);
        Assert.Equal("Test", login.FirstName);
        Assert.Equal(Password, login.Password.Reveal());           // the password did reach the login call
        Assert.Equal("uri:Alpha&128&128&25", login.StartLocation);
        NotRetained(m);

        fake.DriveToInWorld(new SimRef(1099511628032000UL, "Alpha"));
        Assert.Equal(En.Get("login.status.in_world"), m.Status);
        Assert.Equal(En.Get("login.status.connecting"), statuses[0]);
        Assert.Contains(En.Get("login.status.stage.LoggedIn"), statuses);
        Assert.All(statuses, s => Assert.DoesNotContain("[[", s));
        m.Session!.Dispose();
    }

    [Fact]
    public async Task A_refused_login_shows_the_failure_text_and_keeps_nothing()
    {
        var (m, _, _) = Model(f => f.OnLogin = (_, _) => Task.FromResult(TransportLoginResult.Failed("presence", "You appear to be already logged in.")));
        Assert.False(await m.LoginAsync());
        Assert.Equal(En.Get("login.status.failed", "You appear to be already logged in."), m.Status);
        Assert.Null(m.Session);
        NotRetained(m);
    }

    [Fact]
    public async Task Cancel_stops_the_attempt_and_says_so()
    {
        var started = new TaskCompletionSource();
        var (m, fake, _) = Model(f => f.OnLogin = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return TransportLoginResult.Failed("x", null); });
        var attempt = m.LoginAsync();
        await started.Task;
        Assert.True(m.Busy);
        m.Cancel();
        Assert.False(await attempt);
        Assert.True(fake.LastLoginToken.IsCancellationRequested);
        Assert.Equal(En.Get("login.status.cancelled"), m.Status);
        Assert.False(m.Busy);
        NotRetained(m);
    }

    [Fact]
    public async Task Missing_fields_and_a_bad_address_are_reported_without_contacting_the_grid()
    {
        var (m, fake, _) = Model();
        m.FirstName = "";
        Assert.False(await m.LoginAsync());
        Assert.Equal(En.Get("login.status.missing", En.Get("login.username.label")), m.Status);
        m.FirstName = "Test";
        m.LoginUri = "grid.invalid:8002";
        Assert.False(await m.LoginAsync());
        Assert.Equal(En.Get("login.status.no_scheme"), m.Status);   // no http:// or https://: said plainly, not guessed
        Assert.Empty(fake.Logins);
        Assert.True(m.HasPassword);   // not consumed by a rejected form: the user can correct the field and retry
    }
}

/// <summary>Closing the viewer (the window's close request, or the automation API's quit) logs the session out first.</summary>
public class CloseRequestTests
{
    private static readonly StringTable En = StringTable.English();

    private sealed class Rig
    {
        public readonly FakeTransport Fake = new();
        public readonly ListSink Sink = new();
        public readonly LoginModel Model;
        public readonly List<TimeSpan> Waits = new();
        public readonly TaskCompletionSource WaitOver = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CloseRequest Close;

        public Rig()
        {
            var log = new CoreLog(Sink, "test");
            Model = new LoginModel(En, opts => new ViewerSession(opts, Fake, log))
            {
                LoginUri = "https://grid.example.org:8002/", FirstName = "Test", LastName = "User",
            };
            Model.SetPassword("example password");
            // the bounded wait, held open until the test ends it (no real clock)
            Close = new CloseRequest(() => Model.Session, Model.Cancel, log)
            {
                Delay = (t, ct) => { lock (Waits) Waits.Add(t); return WaitOver.Task.WaitAsync(ct); },
            };
        }

        public async Task<ViewerSession> InWorld()
        {
            Assert.True(await Model.LoginAsync());
            Fake.DriveToInWorld(new SimRef(1099511628032000UL, "Alpha"));
            return Model.Session!;
        }
    }

    private static async Task Until(Func<bool> done)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "condition not reached within 20 s");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Closing_in_world_logs_out_and_quits_when_the_grid_answers()
    {
        var rig = new Rig();
        var session = await rig.InWorld();
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Fake.OnLogout = _ => reply.Task;

        rig.Close.Request();
        Assert.Equal(1, rig.Fake.Logouts);
        Assert.True(rig.Close.LoggingOut);
        Assert.False(rig.Close.ReadyToQuit);   // waiting for the grid's reply

        reply.SetResult();
        await Until(() => rig.Close.ReadyToQuit);
        Assert.True(session.IsEnded);
        Assert.Equal(SessionEndReason.LoggedOut, session.EndReason);
        Assert.Contains("logout.complete", rig.Sink.Text);
        session.Dispose();
    }

    [Fact]
    public async Task The_wait_for_the_grids_reply_is_bounded()
    {
        var rig = new Rig();
        var session = await rig.InWorld();
        rig.Fake.OnLogout = _ => new TaskCompletionSource().Task;   // the grid never answers (and ignores cancellation)

        rig.Close.Request();
        await Until(() => rig.Waits.Count == 1);
        Assert.Equal(CloseRequest.LogoutWait, rig.Waits[0]);
        Assert.Equal(TimeSpan.FromSeconds(6), CloseRequest.LogoutWait);
        Assert.False(rig.Close.ReadyToQuit);

        rig.WaitOver.SetResult();   // the bound runs out
        await Until(() => rig.Close.ReadyToQuit);
        Assert.True(rig.Fake.LastLogoutToken.IsCancellationRequested);   // the logout call is told to stop
        Assert.Contains("close.logout_wait_over", rig.Sink.Text);
        session.Dispose();
    }

    [Fact]
    public async Task A_second_close_while_waiting_quits_at_once()
    {
        var rig = new Rig();
        var session = await rig.InWorld();
        rig.Fake.OnLogout = _ => new TaskCompletionSource().Task;

        rig.Close.Request();
        Assert.False(rig.Close.ReadyToQuit);
        rig.Close.Request();
        Assert.True(rig.Close.ReadyToQuit);
        Assert.Equal(1, rig.Fake.Logouts);   // the logout was sent once
        Assert.Contains("close.forced", rig.Sink.Text);
        session.Dispose();
    }

    [Fact]
    public async Task Without_a_session_close_quits_at_once_and_a_login_in_progress_is_cancelled()
    {
        var idle = new Rig();
        idle.Close.Request();
        Assert.True(idle.Close.ReadyToQuit);
        Assert.Equal(0, idle.Fake.Logouts);

        var rig = new Rig();
        var started = new TaskCompletionSource();
        rig.Fake.OnLogin = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return TransportLoginResult.Failed("x", null); };
        var attempt = rig.Model.LoginAsync();
        await started.Task;
        rig.Close.Request();
        Assert.True(rig.Close.ReadyToQuit);
        Assert.True(rig.Fake.LastLoginToken.IsCancellationRequested);
        Assert.False(await attempt);
        Assert.Equal(0, rig.Fake.Logouts);
    }

    [Fact]
    public async Task A_session_already_logged_out_is_not_logged_out_again()
    {
        var rig = new Rig();
        var session = await rig.InWorld();
        await session.LogoutAsync(CancellationToken.None);
        rig.Close.Request();
        Assert.True(rig.Close.ReadyToQuit);
        Assert.Equal(1, rig.Fake.Logouts);
        session.Dispose();
    }
}
