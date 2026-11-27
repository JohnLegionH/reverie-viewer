// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using LibreMetaverse;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using TexGen = Viewer.Core.World.TexGen;
using Viewer.Core.Media;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests.Media;

/// <summary>What a region sends about media, as the viewer reads and keeps it.</summary>
public sealed class MediaMessageTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly SimRef There = new(300000UL * 256 << 32 | 300000UL * 256, "Far Region");
    private static readonly Guid Texture = new("5a9f0c1e-3b7d-4e22-9c4a-1f2b3c4d5e6f");

    private static readonly ParcelMediaInfo Video = new("http://media.example.org/clip.mp4", Texture, true, "video/mp4", "A clip", 320, 180, true);

    private static (ViewerSession Session, FakeTransport Fake, ListSink Log) InWorld()
    {
        var fake = new FakeTransport();
        var sink = new ListSink();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(sink, "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake, sink);
    }

    private static string Lines(ListSink sink) => sink.Text;

    [Fact]
    public void A_parcels_media_is_read_whole()
    {
        var read = LmvTransport.ToParcelMediaInfo(new ParcelMedia
        {
            MediaURL = Video.Url, MediaID = new UUID(Texture), MediaAutoScale = true, MediaType = "video/mp4", MediaDesc = "A clip",
            MediaWidth = 320, MediaHeight = 180, MediaLoop = true,
        });
        Assert.Equal(Video, read);
        Assert.Equal(ParcelMediaInfo.None, LmvTransport.ToParcelMediaInfo(default));
    }

    [Fact]
    public void A_script_media_command_keeps_its_command_and_time()
    {
        var read = LmvTransport.ToParcelMediaCommand(Here, new ParcelMediaCommandEventArgs(null!, 1, (ParcelFlags)3, ParcelMediaCommand.Play, 12.5f));
        Assert.Equal(new ParcelMediaCommanded(Here, ParcelMediaVerb.Play, 12.5f, 3), read);
    }

    [Fact]
    public void An_object_media_answer_names_each_face_that_has_media()
    {
        var faces = new MediaEntry?[]
        {
            null,
            new() { CurrentURL = "http://media.example.org/now", HomeURL = "http://media.example.org/home", AutoPlay = true, Width = 512, Height = 256, EnableWhiteList = true, WhiteList = ["media.example.org"] },
        };
        var read = Assert.Single(LmvTransport.ToFaceMedia(faces));
        Assert.Equal((1, "http://media.example.org/now", "http://media.example.org/home", true, 512, 256, true),
            (read.Face, read.CurrentUrl, read.HomeUrl, read.AutoPlay, read.Width, read.Height, read.WhitelistEnabled));
        Assert.Equal(["media.example.org"], read.Whitelist!);
        Assert.Empty(LmvTransport.ToFaceMedia(null));
    }

    [Fact]
    public void A_prim_update_carries_the_media_version_and_the_faces_with_the_media_bit()
    {
        var textures = new Primitive.TextureEntry(UUID.Random());
        textures.CreateFace(2).MediaFlags = true;
        var prim = new Primitive { LocalID = 5, ID = UUID.Random(), Textures = textures, MediaURL = "x-mv:0000000003/00000000-0000-0000-0000-000000000000" };
        prim.PrimData.PCode = PCode.Prim;
        var data = LmvTransport.ToPrimData(prim);
        Assert.Equal(prim.MediaURL, data.MediaVersion);
        Assert.Equal(1UL << 2, data.MediaFaces);
    }

    [Fact]
    public void The_media_of_the_agents_parcel_is_kept_and_logged_and_a_later_message_replaces_it()
    {
        var (session, fake, log) = InWorld();
        Assert.Equal(ParcelMediaInfo.None, session.ParcelMedia);
        fake.Raise(new AgentParcelChanged(Here, "Example Parcel", 7, Media: Video));
        Assert.Equal(Video, session.ParcelMedia);
        Assert.Contains("media.parcel", Lines(log));
        Assert.Contains("video/mp4", Lines(log));
        Assert.Contains(Texture.ToString(), Lines(log));
        Assert.Contains("media.example.org", Lines(log));
        var other = Video with { Url = "http://media.example.org/other.mp4" };
        fake.Raise(new ParcelMusicReceived(Here, 7, "", other));
        Assert.Equal(other, session.ParcelMedia);
        fake.Raise(new ParcelMusicReceived(Here, 8, "", Video));   // another parcel
        fake.Raise(new ParcelMusicReceived(There, 7, "", Video));  // another region
        Assert.Equal(other, session.ParcelMedia);
    }

    [Fact]
    public void A_script_update_and_command_are_kept_and_logged_for_this_region_only()
    {
        var (session, fake, log) = InWorld();
        fake.Raise(new ParcelMediaUpdated(There, Video));
        fake.Raise(new ParcelMediaCommanded(There, ParcelMediaVerb.Play, 0, 0));
        Assert.Equal(ParcelMediaInfo.None, session.ParcelMedia);
        Assert.Null(session.LastParcelMediaCommand);
        fake.Raise(new ParcelMediaUpdated(Here, Video));
        fake.Raise(new ParcelMediaCommanded(Here, ParcelMediaVerb.Pause, 4, 0));
        Assert.Equal(Video, session.ParcelMedia);
        Assert.Equal(ParcelMediaVerb.Pause, session.LastParcelMediaCommand?.Command);
        Assert.Contains("media.parcel-command", Lines(log));
    }

    [Fact]
    public void Object_media_is_kept_and_logged_by_face_and_dropped_on_another_region()
    {
        var (session, fake, log) = InWorld();
        var id = Guid.NewGuid();
        fake.Raise(new ObjectMediaReceived(Here, id, "x-mv:0000000001/0", [new FaceMediaEntry(3, "http://media.example.org/now", "http://media.example.org/home", AutoPlay: true)]));
        Assert.Equal(3, Assert.Single(session.ObjectMedia(id)).Face);
        Assert.Contains("media.face", Lines(log));
        fake.Raise(new AgentParcelChanged(Here, "Example Parcel", 7, Media: Video));
        fake.Raise(new SimConnected(There, 256, 256));
        fake.CurrentSim = There;
        Assert.Empty(session.ObjectMedia(id));
        Assert.Equal(ParcelMediaInfo.None, session.ParcelMedia);
    }

    [Fact]
    public void Media_messages_are_never_written_to_a_capture()
    {
        Assert.False(CaptureFile.IsRecorded(new ParcelMediaUpdated(Here, Video)));
        Assert.False(CaptureFile.IsRecorded(new ObjectMediaReceived(Here, Guid.NewGuid(), "v", [])));
    }

    [Fact]
    public async Task Object_media_is_asked_for_once_per_version_and_again_after_a_failure()
    {
        var asked = new ConcurrentQueue<Guid>();
        var got = new ConcurrentQueue<string>();
        int fail = 1;
        using var fetcher = new PrimMediaFetcher<object>(
            (_, id, _) => { asked.Enqueue(id); return Task.FromResult<IReadOnlyList<FaceMediaEntry>?>(Interlocked.Decrement(ref fail) >= 0 ? null : [new FaceMediaEntry(0, "a", "b")]); },
            (_, _, version, _) => got.Enqueue(version), (_, _) => Task.CompletedTask);
        var sim = new object();
        var one = Guid.NewGuid();
        fetcher.Want(sim, one, "v1");
        await Until(() => got.Count == 1);
        Assert.Equal(["v1"], got);
        Assert.Equal(2, asked.Count);   // the first answer failed
        fetcher.Want(sim, one, "v1");
        fetcher.Want(sim, one, "v2");
        await Until(() => got.Count == 2);
        Assert.Equal(["v1", "v2"], got);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task An_object_whose_media_was_removed_gets_no_late_answer_and_is_asked_about_again_later()
    {
        var answer = new TaskCompletionSource<IReadOnlyList<FaceMediaEntry>?>();
        var got = new ConcurrentQueue<string>();
        int asked = 0;
        using var fetcher = new PrimMediaFetcher<object>((_, _, _) => Interlocked.Increment(ref asked) == 1 ? answer.Task : Task.FromResult<IReadOnlyList<FaceMediaEntry>?>([]),
            (_, _, version, _) => got.Enqueue(version), (_, _) => Task.CompletedTask);
        var one = Guid.NewGuid();
        fetcher.Want(new object(), one, "v1");
        await Until(() => asked == 1);
        Assert.True(fetcher.Forget(one), "the object had media that was asked for");
        Assert.False(fetcher.Forget(one));
        Assert.False(fetcher.Forget(Guid.NewGuid()));
        answer.SetResult([new FaceMediaEntry(0, "a", "b")]);
        fetcher.Want(new object(), one, "v1");   // media put back: asked for anew
        await Until(() => !got.IsEmpty);
        Assert.Equal(["v1"], got);
        Assert.Equal(2, asked);
    }

    private static async Task Until(Func<bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < end)
            await Task.Delay(20);
    }

    [Fact]
    public void A_texture_edit_keeps_the_media_bit_of_every_face()
    {
        var textures = new Primitive.TextureEntry(UUID.Random());
        textures.CreateFace(1).MediaFlags = true;
        textures.CreateFace(4).MediaFlags = true;
        var edit = new FaceEdit(1, new FaceSettings(Guid.NewGuid(), new Viewer.Core.World.Rgba(1, 0, 0, 1), 2, 2, 0, 0, 0, TexGen.Planar, true, 0));
        var sent = LmvEdit.Image(UUID.Random(), UUID.Random(), 9, textures, [edit]).ObjectData[0].TextureEntry;
        var read = new Primitive.TextureEntry(sent, 0, sent.Length);
        Assert.True(read.GetFace(1).MediaFlags);
        Assert.True(read.GetFace(4).MediaFlags);
        Assert.False(read.GetFace(0).MediaFlags);
    }
}
