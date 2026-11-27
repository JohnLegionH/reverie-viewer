// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Viewer.Core.Media.Packs;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests.Media;

/// <summary>
/// Optional packs: the manifest, the question asked once, the download from a local server that counts requests, the size and hash
/// checks, the archive's paths, and removal. Every file is made under a folder of the test's own.
/// </summary>
public sealed class PackTests : IDisposable
{
    private readonly string _base = TestTemp.NewFolder("packs");
    private string Root => Path.Combine(_base, "packs");

    public void Dispose() => TestTemp.Delete(_base);

    /// <summary>Answers each request with the next reply set for its path; counts the requests.</summary>
    private sealed class PackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public PackServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        Interlocked.Increment(ref _requests);
                        _ = Task.Run(() => Serve(client));
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
            });
        }

        public int Requests => Volatile.Read(ref _requests);
        public Uri Url(string path = "/pack.zip") => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");
        public byte[] Body { get; set; } = [];
        /// <summary>The length the reply states; null: the body's own. A larger one with a shorter body is a cut download.</summary>
        public long? StatedLength { get; set; }
        public bool NoLength { get; set; }
        public string? RedirectTo { get; set; }
        /// <summary>Sends half the body and then nothing, with the connection held open.</summary>
        public bool Stall { get; set; }

        private async Task Serve(TcpClient client)
        {
            try
            {
                using var _ = client;
                var stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0)
                        return;
                    head.Append((char)one[0]);
                }
                bool redirect = RedirectTo is not null && head.ToString().StartsWith("GET /pack.zip ", StringComparison.Ordinal);
                string answer = redirect
                    ? $"HTTP/1.0 302 Found\r\nLocation: {RedirectTo}\r\nContent-Length: 0\r\n\r\n"
                    : "HTTP/1.0 200 OK\r\n" + (NoLength ? "" : $"Content-Length: {StatedLength ?? Body.Length}\r\n") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
                if (redirect)
                    return;
                if (Stall)
                {
                    await stream.WriteAsync(Body.AsMemory(0, Body.Length / 2), _stop.Token);
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
                await stream.WriteAsync(Body, _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        return memory.ToArray();
    }

    private static byte[] GoodZip() => Zip(("engine.txt", "dummy engine"), ("licences/NOTICE.txt", "notice"));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static PackOffer OfferFor(PackServer server, byte[] body, string version = "1.0.0") =>
        new("video", version, server.Url(), body.Length, Hash(body));

    private PackStore Store() => new(Root, allowLoopbackHttp: true, idleTimeout: TimeSpan.FromSeconds(2));

    private int FilesUnderRoot() => Directory.Exists(Root) ? Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Length : 0;

    private static string Json(string url, string id = "video", string version = "1.0.0", long bytes = 10, string? sha = null, string platform = "win-x64", string withdrawn = "") =>
        $$"""{"packs":[{"id":"{{id}}","version":"{{version}}","platform":"{{platform}}","url":"{{url}}","bytes":{{bytes}},"sha256":"{{sha ?? new string('a', 64)}}"}],"withdrawn":[{{withdrawn}}]}""";

    // ---- the manifest

    [Fact]
    public void Manifest_offers_the_pack_of_this_platform_only()
    {
        var offer = PackManifest.Parse(Json("https://example.org/v.zip", bytes: 1234), "win-x64").Offer("video");
        Assert.NotNull(offer);
        Assert.Equal(("1.0.0", 1234L, "example.org"), (offer.Version, offer.Bytes, offer.Url.Host));
        Assert.Null(PackManifest.Parse(Json("https://example.org/v.zip", platform: "linux-x64"), "win-x64").Offer("video"));
    }

    [Theory]
    [InlineData("http://example.org/v.zip", "video", "1.0.0", 10L, false)]
    [InlineData("ftp://example.org/v.zip", "video", "1.0.0", 10L, false)]
    [InlineData("file:///v.zip", "video", "1.0.0", 10L, false)]
    [InlineData("http://example.org/v.zip", "video", "1.0.0", 10L, true)]
    [InlineData("https://user:pw@example.org/v.zip", "video", "1.0.0", 10L, false)]
    [InlineData("https://example.org/v.zip", "..", "1.0.0", 10L, false)]
    [InlineData("https://example.org/v.zip", "video", "..", 10L, false)]
    [InlineData("https://example.org/v.zip", "video", "a/b", 10L, false)]
    [InlineData("https://example.org/v.zip", "video", "1.0.0", 0L, false)]
    [InlineData("https://example.org/v.zip", "video", "1.0.0", 5_000_000_000L, false)]
    public void Manifest_leaves_out_an_entry_it_cannot_trust(string url, string id, string version, long bytes, bool loopbackAllowed)
    {
        // the control: the same entry with good values is offered
        Assert.NotNull(PackManifest.Parse(Json("https://example.org/v.zip"), "win-x64").Offer("video"));
        Assert.Null(PackManifest.Parse(Json(url, id, version, bytes), "win-x64", loopbackAllowed).Offer(id));
    }

    [Fact]
    public void Manifest_takes_loopback_http_only_when_told_to()
    {
        string json = Json("http://127.0.0.1:8000/v.zip");
        Assert.NotNull(PackManifest.Parse(json, "win-x64", allowLoopbackHttp: true).Offer("video"));
        Assert.Null(PackManifest.Parse(json, "win-x64").Offer("video"));
    }

    [Fact]
    public void Manifest_does_not_offer_a_withdrawn_version_and_says_it_is_withdrawn()
    {
        var m = PackManifest.Parse(Json("https://example.org/v.zip", withdrawn: """{"id":"video","version":"1.0.0"}"""), "win-x64");
        Assert.True(m.IsWithdrawn("video", "1.0.0"));
        Assert.Null(m.Offer("video"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"packs":5}""")]
    public void A_manifest_that_cannot_be_read_offers_nothing(string? json) => Assert.Null(PackManifest.Parse(json, "win-x64").Offer("video"));

    // ---- download, checks, remove

    [Fact]
    public async Task A_good_pack_is_installed_with_its_files()
    {
        using var server = new PackServer { Body = GoodZip() };
        var store = Store();
        Assert.Equal(PackInstallResult.Installed, await store.InstallAsync(OfferFor(server, server.Body)));
        var found = store.Find("video");
        Assert.NotNull(found);
        Assert.Equal("1.0.0", found.Version);
        Assert.Equal("dummy engine", File.ReadAllText(Path.Combine(found.Folder, "engine.txt")));
        Assert.True(File.Exists(Path.Combine(found.Folder, "licences", "NOTICE.txt")));
        Assert.Equal(3, FilesUnderRoot());   // the two files and the record: no download or staging file stays
        // a new store finds it too
        Assert.NotNull(Store().Find("video"));
    }

    [Fact]
    public async Task A_wrong_hash_is_discarded()
    {
        using var server = new PackServer { Body = GoodZip() };
        var offer = OfferFor(server, server.Body) with { Sha256 = new string('0', 64) };
        Assert.Equal(PackInstallResult.WrongHash, await Store().InstallAsync(offer));
        Assert.Null(Store().Find("video"));
        Assert.Equal(0, FilesUnderRoot());
    }

    [Fact]
    public async Task A_cut_download_with_a_stated_length_leaves_nothing()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip[..(zip.Length / 2)], StatedLength = zip.Length };
        var result = await Store().InstallAsync(OfferFor(server, zip));
        Assert.True(result is PackInstallResult.DownloadFailed or PackInstallResult.WrongSize, result.ToString());
        Assert.Null(Store().Find("video"));
        Assert.Equal(0, FilesUnderRoot());
    }

    [Fact]
    public async Task A_cut_download_without_a_stated_length_leaves_nothing()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip[..(zip.Length / 2)], NoLength = true };
        Assert.Equal(PackInstallResult.WrongSize, await Store().InstallAsync(OfferFor(server, zip)));
        Assert.Equal(0, FilesUnderRoot());
    }

    [Fact]
    public async Task A_download_that_stops_arriving_is_given_up()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip, Stall = true };
        var result = await Store().InstallAsync(OfferFor(server, zip)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PackInstallResult.DownloadFailed, result);
        Assert.Equal(0, FilesUnderRoot());
    }

    [Fact]
    public async Task A_reply_larger_than_the_stated_size_is_stopped()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = [.. zip, .. new byte[100_000]], NoLength = true };
        Assert.Equal(PackInstallResult.WrongSize, await Store().InstallAsync(OfferFor(server, zip)));
        Assert.Equal(0, FilesUnderRoot());
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/escape.txt")]
    [InlineData("c:escape.txt")]
    [InlineData("a/.. /escape.txt")]
    [InlineData("installed.json")]
    public async Task An_archive_with_a_path_that_leaves_the_pack_is_refused_whole(string bad)
    {
        var zip = Zip(("engine.txt", "dummy engine"), (bad, "escaped"));
        using var server = new PackServer { Body = zip };
        Assert.Equal(PackInstallResult.BadArchive, await Store().InstallAsync(OfferFor(server, zip)));
        Assert.Null(Store().Find("video"));
        Assert.Equal(0, FilesUnderRoot());
        Assert.Empty(Directory.GetFiles(_base, "escape.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Bytes_that_are_not_an_archive_are_refused()
    {
        var body = Encoding.ASCII.GetBytes("this is not a zip file");
        using var server = new PackServer { Body = body };
        Assert.Equal(PackInstallResult.BadArchive, await Store().InstallAsync(OfferFor(server, body)));
        Assert.Equal(0, FilesUnderRoot());
    }

    [Fact]
    public async Task A_redirect_to_an_address_that_is_not_allowed_is_not_followed()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip };
        // an address with a sign-in in it, which this server would answer: only the check keeps the download from going there
        server.RedirectTo = server.Url("/moved.zip").ToString().Replace("http://", "http://user:word@", StringComparison.Ordinal);
        var result = await Store().InstallAsync(OfferFor(server, zip)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PackInstallResult.DownloadFailed, result);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_redirect_that_is_allowed_is_followed()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip };
        server.RedirectTo = server.Url("/moved.zip").ToString();
        Assert.Equal(PackInstallResult.Installed, await Store().InstallAsync(OfferFor(server, zip)));
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task A_cancelled_download_leaves_nothing()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip, Stall = true };
        using var cancel = new CancellationTokenSource();
        var store = new PackStore(Root, allowLoopbackHttp: true);
        var running = store.InstallAsync(OfferFor(server, zip), cancel.Token);
        for (var until = DateTime.UtcNow.AddSeconds(30); server.Requests == 0 && DateTime.UtcNow < until;)
            await Task.Delay(20);
        cancel.Cancel();
        Assert.Equal(PackInstallResult.Cancelled, await running.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, server.Requests);
        Assert.Equal(0, FilesUnderRoot());
    }

    [Fact]
    public async Task Remove_takes_the_whole_pack_away()
    {
        using var server = new PackServer { Body = GoodZip() };
        var store = Store();
        await store.InstallAsync(OfferFor(server, server.Body));
        Assert.NotNull(store.Find("video"));
        Assert.True(store.Remove("video"));
        Assert.Null(store.Find("video"));
        Assert.False(Directory.Exists(Path.Combine(Root, "video")));
        Assert.True(Directory.Exists(Root));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("video/..")]
    public void Remove_never_leaves_the_packs_folder(string id)
    {
        Directory.CreateDirectory(Path.Combine(Root, "video"));
        File.WriteAllText(Path.Combine(_base, "keep.txt"), "keep");
        Assert.False(Store().Remove(id));
        Assert.True(File.Exists(Path.Combine(_base, "keep.txt")));
        Assert.True(Directory.Exists(Path.Combine(Root, "video")));
        Assert.True(Store().Remove("video"));   // the control: a pack's own id is removed
    }

    [Fact]
    public async Task A_new_version_takes_the_place_of_the_old_and_a_withdrawn_one_is_not_ready()
    {
        using var server = new PackServer { Body = GoodZip() };
        var store = Store();
        await store.InstallAsync(OfferFor(server, server.Body, "1.0.0"));
        var good = PackManifest.Parse(Json("https://example.org/v.zip", version: "1.0.0"), "win-x64");
        Assert.Equal(PackState.Ready, store.State("video", good));
        var withdrawn = PackManifest.Parse(Json("https://example.org/v.zip", version: "1.0.1", withdrawn: """{"id":"video","version":"1.0.0"}"""), "win-x64");
        Assert.Equal(PackState.Withdrawn, store.State("video", withdrawn));
        await store.InstallAsync(OfferFor(server, server.Body, "1.0.1"));
        Assert.Equal("1.0.1", store.Find("video")?.Version);
        Assert.Single(Directory.GetDirectories(Path.Combine(Root, "video")));
        Assert.Equal(PackState.Ready, store.State("video", withdrawn));
    }

    [Fact]
    public async Task What_a_viewer_left_unfinished_long_ago_is_cleared_at_the_next_download_and_a_recent_one_is_not()
    {
        string oldPart = Path.Combine(Root, ".download", "old.part"), newPart = Path.Combine(Root, ".download", "new.part");
        string oldStage = Path.Combine(Root, ".staging", "old"), newStage = Path.Combine(Root, ".staging", "new");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPart)!);
        File.WriteAllText(oldPart, "half");
        File.WriteAllText(newPart, "half");
        Directory.CreateDirectory(oldStage);
        Directory.CreateDirectory(newStage);
        File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow.AddDays(-3));
        Directory.SetLastWriteTimeUtc(oldStage, DateTime.UtcNow.AddDays(-3));
        using var server = new PackServer { Body = GoodZip() };
        Assert.Equal(PackInstallResult.Installed, await Store().InstallAsync(OfferFor(server, server.Body)));
        Assert.False(File.Exists(oldPart), "the old part file stays");
        Assert.False(Directory.Exists(oldStage), "the old staging folder stays");
        Assert.True(File.Exists(newPart));
        Assert.True(Directory.Exists(newStage));
    }

    // ---- the question

    private sealed class Asker : IPackAsker
    {
        public List<PackOffer> Asked { get; } = [];
        public int Cancelled { get; private set; }
        public Action<bool>? Answer { get; private set; }
        public void Ask(PackOffer offer, Action<bool> answered) { Asked.Add(offer); Answer = answered; }
        public void Cancel() => Cancelled++;
    }

    private static PackManifest ManifestFor(PackServer server, byte[] zip) =>
        PackManifest.Parse(Json(server.Url().ToString(), bytes: zip.Length, sha: Hash(zip)), "win-x64", allowLoopbackHttp: true);

    private static MediaPackSettings Answers(Dictionary<string, string> kept) => new(k => kept.GetValueOrDefault(k), (k, v) => kept[k] = v);

    private static async Task<(PackOffer Offer, PackInstallResult Result)> Finished(PackOffers offers)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (offers.Poll() is { } done)
                return done;
            await Task.Delay(20);
        }
        Assert.Fail("no download finished in 30 s");
        return default;
    }

    [Fact]
    public async Task Nothing_is_requested_before_yes_and_the_pack_arrives_after_it()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip };
        var asker = new Asker();
        var offers = new PackOffers(ManifestFor(server, zip), Store(), Answers([]), asker);
        Assert.Equal(PackWant.Asking, offers.Want("video"));
        Assert.Equal(PackWant.Asking, offers.Want("video"));
        Assert.Single(asker.Asked);
        Assert.Equal(zip.Length, asker.Asked[0].Bytes);
        await Task.Delay(500);   // a fixed window: it proves that nothing arrives
        Assert.Equal(0, server.Requests);
        asker.Answer!(true);
        var done = await Finished(offers);
        Assert.Equal(PackInstallResult.Installed, done.Result);
        Assert.Equal(1, server.Requests);
        Assert.Null(offers.Poll());   // the outcome is handed over once
        Assert.Equal(PackWant.Ready, offers.Want("video"));
        Assert.Single(asker.Asked);
    }

    [Fact]
    public async Task No_is_remembered_nothing_is_requested_and_a_click_may_ask_again()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip };
        var kept = new Dictionary<string, string>();
        var asker = new Asker();
        var offers = new PackOffers(ManifestFor(server, zip), Store(), Answers(kept), asker);
        offers.Want("video");
        Assert.NotNull(asker.Answer);
        asker.Answer(false);
        Assert.Equal(PackWant.Refused, offers.Want("video"));
        var later = new Asker();
        var nextVisit = new PackOffers(ManifestFor(server, zip), Store(), Answers(kept), later);
        Assert.Equal(PackWant.Refused, nextVisit.Want("video"));
        Assert.Empty(later.Asked);
        await Task.Delay(500);
        Assert.Equal(0, server.Requests);
        Assert.Equal(0, FilesUnderRoot());
        Assert.Equal(PackWant.Asking, nextVisit.AskAgain("video"));
        Assert.Single(later.Asked);
    }

    [Fact]
    public async Task A_question_taken_away_is_no_answer_and_a_late_one_downloads_nothing()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip };
        var kept = new Dictionary<string, string>();
        var asker = new Asker();
        var offers = new PackOffers(ManifestFor(server, zip), Store(), Answers(kept), asker);
        offers.Want("video");
        var late = asker.Answer;
        offers.CancelQuestion();
        Assert.Equal(1, asker.Cancelled);
        late!(true);
        await Task.Delay(500);
        Assert.Equal(0, server.Requests);
        Assert.Empty(kept);
        Assert.Equal(PackWant.Asking, offers.Want("video"));
    }

    [Fact]
    public async Task A_download_that_finishes_as_its_pack_is_removed_leaves_no_pack_and_reports_no_success()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip };
        var store = Store();
        var gate = new TaskCompletionSource();
        var asker = new Asker();
        // the download is already past its last look at the cancel: it installs the pack after the removal
        var offers = new PackOffers(ManifestFor(server, zip), store, Answers([]), asker, async (offer, _) =>
        {
            await gate.Task;
            return await store.InstallAsync(offer);
        });
        offers.Want("video");
        asker.Answer!(true);
        offers.Remove("video");
        gate.SetResult();
        Assert.Equal(PackInstallResult.Cancelled, (await Finished(offers)).Result);
        Assert.Null(store.Find("video"));
    }

    [Fact]
    public void Asking_again_while_the_pack_cannot_be_asked_about_keeps_the_no()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip };
        var kept = new Dictionary<string, string>();
        Answers(kept).SetRefused("video", true);
        var offers = new PackOffers(PackManifest.Empty, Store(), Answers(kept), new Asker());
        Assert.Equal(PackWant.Unavailable, offers.AskAgain("video"));
        Assert.True(Answers(kept).IsRefused("video"));
    }

    [Fact]
    public void With_an_empty_manifest_nothing_is_asked_and_no_folder_is_made()
    {
        var asker = new Asker();
        var offers = new PackOffers(PackManifest.Empty, Store(), Answers([]), asker);
        Assert.Equal(PackWant.Unavailable, offers.Want("video"));
        Assert.Empty(asker.Asked);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task One_download_runs_at_a_time_and_remove_stops_it()
    {
        var zip = GoodZip();
        using var server = new PackServer { Body = zip, Stall = true };
        var asker = new Asker();
        var offers = new PackOffers(ManifestFor(server, zip), new PackStore(Root, allowLoopbackHttp: true), Answers([]), asker);
        Assert.Equal(PackWant.Asking, offers.Want("video"));
        asker.Answer!(true);
        Assert.Equal(PackWant.Downloading, offers.Want("video"));
        offers.Remove("video");
        Assert.Equal(PackInstallResult.Cancelled, (await Finished(offers)).Result);
        Assert.Equal(0, FilesUnderRoot());
    }

    [Theory]
    [InlineData(52_000_000L, "52\u00A0MB")]
    [InlineData(172_600_000L, "173\u00A0MB")]
    [InlineData(1_000_001L, "2\u00A0MB")]
    [InlineData(900L, "1\u00A0MB")]
    public void The_size_shown_is_never_smaller_than_the_download(long bytes, string shown) => Assert.Equal(shown, MediaPackSettings.SizeText(bytes));
}
