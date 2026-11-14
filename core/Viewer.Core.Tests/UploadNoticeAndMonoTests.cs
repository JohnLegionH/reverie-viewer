// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Notifications;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The notice after an upload says what was uploaded and where, with a Show in inventory button that selects the item; and the
/// Sound page's Mono setting: off by default, near the top of the page, and folding the master mix to one channel. Pure.
/// </summary>
public class UploadNoticeAndMonoTests
{
    private static readonly StringTable Strings = StringTable.English();

    private static string Text(NoticeText t) => t.Literal ?? (t.Arg is null ? Strings.Get(t.Key!) : Strings.Get(t.Key!, t.Arg));

    [Fact]
    public void One_upload_names_the_item_and_its_folder_and_Show_selects_it()
    {
        var item = Guid.NewGuid();
        Guid shown = Guid.Empty;
        var n = UploadNotice.For([("Brick wall", item)], UploadFolder.Textures, id => shown = id);
        Assert.NotNull(n);
        Assert.Equal("Uploaded to Textures: Brick wall", Text(n!.Body));
        var button = Assert.Single(n.Buttons);
        Assert.Equal(UploadNotice.ShowButton, button.Index);
        Assert.Equal("Show in inventory", Text(button.Label));
        n.Responder!(new NotificationAnswer(UploadNotice.ShowButton, ""));
        Assert.Equal(item, shown);
    }

    [Fact]
    public void A_bulk_upload_gives_a_count_and_Show_selects_the_first_item()
    {
        var first = Guid.NewGuid();
        Guid shown = Guid.Empty;
        var n = UploadNotice.For([("a", first), ("b", Guid.NewGuid()), ("c", Guid.NewGuid())], UploadFolder.Textures, id => shown = id);
        Assert.NotNull(n);
        Assert.Equal("Uploaded 3 items to Textures", Text(n!.Body));
        n.Responder!(new NotificationAnswer(UploadNotice.ShowButton, ""));
        Assert.Equal(first, shown);
    }

    [Fact]
    public void A_model_goes_to_Objects()
    {
        var n = UploadNotice.For([("Chair", Guid.NewGuid())], UploadFolder.Objects, _ => { });
        Assert.NotNull(n);
        Assert.Equal("Uploaded to Objects: Chair", Text(n!.Body));
    }

    [Fact]
    public void Nothing_uploaded_means_no_notice_and_closing_it_selects_nothing()
    {
        Assert.Null(UploadNotice.For([], UploadFolder.Textures, _ => { }));
        Guid shown = Guid.Empty;
        var n = UploadNotice.For([("x", Guid.NewGuid())], UploadFolder.Textures, id => shown = id);
        Assert.NotNull(n);
        n!.Responder!(new NotificationAnswer(-1, ""));
        Assert.Equal(Guid.Empty, shown);
    }

    [Fact]
    public void Mono_is_off_by_default_and_sits_near_the_top_of_the_Sound_page()
    {
        Assert.False(AudioLevels.Mono.Default);
        var keys = PreferenceCatalog.OfPage(PrefPage.Sound).Select(d => d.Key).ToList();
        int at = keys.IndexOf(AudioLevels.Mono.Key);
        Assert.InRange(at, 0, 3);
        Assert.Equal(AudioLevels.MuteMaster.Key, keys[at - 1]);
        Assert.True(Strings.Has("prefs.s." + AudioLevels.Mono.Key));
    }

    [Fact]
    public void Mono_folds_the_master_mix_to_one_channel()
    {
        var s = new SettingsService(null);
        Assert.Equal(1f, AudioLevels.StereoSpread(s));
        s.Set(AudioLevels.Mono, true);
        Assert.Equal(0f, AudioLevels.StereoSpread(s));
    }
}
