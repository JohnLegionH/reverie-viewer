// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\boot-logo-check.ps1 is how tools\login-proof.ps1 tells the engine's start-up splash (a flat dark picture with the
/// engine's logo in the middle of the window) from the viewer's own first screen. The check must not depend on the viewer's
/// accent colour: a login screen is accepted whatever its accent, and the splash is refused. These tests build small pictures,
/// run the script on them as a child process and read its one answer line. Each test uses its own temp folder, so they run in
/// parallel.
/// </summary>
public sealed class BootLogoCheckTests
{
    private readonly ITestOutputHelper _out;
    public BootLogoCheckTests(ITestOutputHelper output) { _out = output; }

    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "boot-logo-check.ps1");

    private const int W = 480, H = 260;
    private static readonly (byte R, byte G, byte B) EngineBackground = (36, 36, 36);
    private static readonly (byte R, byte G, byte B) EngineLogoBlue = (71, 140, 191);
    private static readonly (byte R, byte G, byte B) PanelGrey = (42, 45, 51);

    private sealed class Picture
    {
        public readonly byte[] Rgb = new byte[W * H * 3];
        public Picture((byte R, byte G, byte B) background) => Fill(0, 0, W, H, background);
        public Picture Fill(int x0, int y0, int w, int h, (byte R, byte G, byte B) c)
        {
            for (int y = y0; y < y0 + h && y < H; y++)
                for (int x = x0; x < x0 + w && x < W; x++)
                {
                    int i = (y * W + x) * 3;
                    Rgb[i] = c.R; Rgb[i + 1] = c.G; Rgb[i + 2] = c.B;
                }
            return this;
        }
    }

    /// <summary>The engine's splash as it looks in a capture: the flat engine background and a blue logo block with a white
    /// outline in the middle of the window, where the engine draws it.</summary>
    private static Picture Splash() => new Picture(EngineBackground)
        .Fill(W / 2 - 34, H / 2 - 38, 68, 56, (255, 255, 255))
        .Fill(W / 2 - 30, H / 2 - 34, 60, 48, EngineLogoBlue);

    /// <summary>A first screen of the viewer: panel-coloured, an accent-coloured mark and name at the top left, and an
    /// accent-coloured button in the card in the middle of the window.</summary>
    private static Picture LoginScreen((byte R, byte G, byte B) accent) => new Picture(PanelGrey)
        .Fill(10, 10, 20, 20, accent)
        .Fill(W / 2 - 60, H / 2 - 15, 120, 30, accent)
        .Fill(W / 2 - 60, H / 2 + 40, 120, 14, (230, 230, 230));

    private static uint[]? _crcTable;
    private static uint Crc(byte[] type, byte[] data)
    {
        _crcTable ??= Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
        uint crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data)) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        void Be(uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        Be((uint)data.Length); s.Write(t); s.Write(data); Be(Crc(t, data));
    }

    private static string Save(Picture p, string folder, string name)
    {
        string path = Path.Combine(folder, name + ".png");
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        ihdr[2] = W >> 8; ihdr[3] = W & 0xFF; ihdr[6] = H >> 8; ihdr[7] = H & 0xFF; ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        for (int y = 0; y < H; y++) { raw.WriteByte(0); raw.Write(p.Rgb, y * W * 3, W * 3); }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw.ToArray());
        Chunk(fs, "IDAT", z.ToArray());
        Chunk(fs, "IEND", []);
        return path;
    }

    /// <summary>The script's answer for a picture: whether it says the boot logo shows, and the whole line.</summary>
    private (bool Showing, string Line) Check(Picture p, string name)
    {
        string folder = TestTemp.NewFolder("boot-logo");
        string png = Save(p, folder, name);
        var r = ChildProcess.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Png", png],
            TimeSpan.FromMinutes(2), new Dictionary<string, string?>());
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(0, r.ExitCode);
        string line = r.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("boot-logo:", StringComparison.Ordinal)) ?? "";
        Assert.Matches(@"^boot-logo: (yes|no) flat=\d\.\d{3} logo=\d\.\d{3}$", line);
        return (line.StartsWith("boot-logo: yes", StringComparison.Ordinal), line);
    }

    [Fact]
    public void The_engine_splash_is_recognised_as_the_boot_logo()
    {
        var (showing, line) = Check(Splash(), "splash");
        Assert.True(showing, line);
    }

    [Fact]
    public void A_flat_engine_background_without_the_logo_is_not_the_boot_logo()
    {
        var (showing, line) = Check(new Picture(EngineBackground), "flat");
        Assert.False(showing, line);
    }

    [Fact]
    public void The_logo_colour_away_from_the_middle_of_the_window_is_not_the_boot_logo()
    {
        // the engine draws its logo in the middle; a mark of the same blue at the top left is the viewer's own
        var p = new Picture(EngineBackground).Fill(10, 10, 40, 40, EngineLogoBlue);
        var (showing, line) = Check(p, "corner");
        Assert.False(showing, line);
    }

    [Theory]
    [InlineData("engine blue", 71, 140, 191)]
    [InlineData("classic blue", 76, 154, 255)]
    [InlineData("teal", 45, 212, 191)]
    [InlineData("amber", 245, 158, 11)]
    [InlineData("rose", 244, 114, 182)]
    [InlineData("white", 255, 255, 255)]
    [InlineData("black", 0, 0, 0)]
    [InlineData("the engine's background grey", 36, 36, 36)]
    public void A_login_screen_is_accepted_whatever_its_accent_colour(string name, int r, int g, int b)
    {
        var (showing, line) = Check(LoginScreen(((byte)r, (byte)g, (byte)b)), "login-" + name.Replace(' ', '-').Replace("'", ""));
        Assert.False(showing, name + ": " + line);
    }
}
