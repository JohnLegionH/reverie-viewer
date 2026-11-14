// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The login screen's default background picture travels inside the shell assembly: a wide landscape in JPEG form that carries
/// no embedded metadata (no camera or tool data, prompts or content credentials), and the credits say where it came from.
/// Plain .NET; every member is independent, so the class runs in parallel.
/// </summary>
public class SplashAssetTests
{
    private static (int Width, int Height, List<string> Markers) ReadJpeg(byte[] data)
    {
        Assert.True(data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8, "not a JPEG");
        int i = 2, w = 0, h = 0;
        var markers = new List<string>();
        while (i + 4 <= data.Length && data[i] == 0xFF)
        {
            byte m = data[i + 1];
            int len = (data[i + 2] << 8) | data[i + 3];
            markers.Add(m.ToString("X2"));
            if (m is 0xC0 or 0xC1 or 0xC2) { h = (data[i + 5] << 8) | data[i + 6]; w = (data[i + 7] << 8) | data[i + 8]; }
            if (m == 0xDA) break;   // the picture data follows
            i += 2 + len;
        }
        return (w, h, markers);
    }

    [Fact]
    public void The_default_splash_is_a_wide_jpeg_landscape()
    {
        var data = UiAssets.DefaultSplash();
        Assert.True(data.Length > 50_000, "the picture is missing or too small: " + data.Length + " bytes");
        var (w, h, _) = ReadJpeg(data);
        Assert.True(w >= 1600 && h >= 900, $"{w}x{h}");
        Assert.InRange((double)w / h, 1.70, 1.85);
    }

    [Fact]
    public void The_default_splash_carries_no_embedded_metadata()
    {
        var data = UiAssets.DefaultSplash();
        Assert.NotEmpty(data);
        var (_, _, markers) = ReadJpeg(data);
        // APP1 to APP15 (Exif, XMP, content credentials), and comments
        Assert.DoesNotContain(markers, m => string.CompareOrdinal(m, "E1") >= 0 && string.CompareOrdinal(m, "EF") <= 0);
        Assert.DoesNotContain("FE", markers);
        var text = System.Text.Encoding.Latin1.GetString(data);
        foreach (var word in new[] { "Exif", "xpacket", "xmpmeta", "c2pa", "jumb", "OpenAI" })
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_credits_name_the_picture_and_how_it_was_made()
    {
        var notices = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "THIRD-PARTY-NOTICES.md"));
        int at = notices.IndexOf("login-splash.jpg", StringComparison.Ordinal);
        Assert.True(at >= 0, "THIRD-PARTY-NOTICES.md does not name the picture");
        var section = notices[at..Math.Min(notices.Length, at + 1200)];
        Assert.Contains("ChatGPT", section);
        Assert.Contains("its author", section);
    }
}
