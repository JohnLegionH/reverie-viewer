// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The GPU's dedicated video memory, where the platform lets us read it. Godot 4.7 does not report the
/// adapter's memory size, so on Windows it is read from the display adapter's driver entry (the value the adapter's
/// driver publishes, matched by the adapter name Godot reports). Anywhere else, or when no entry matches: null, and the
/// budget uses its fallback (Mac and Linux come later). Read-only.
/// </summary>
public static class VideoMemory
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static long? Detect(string adapterName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(adapterName)) return null;
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is null) return null;
            foreach (var name in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(name);
                if (k?.GetValue("DriverDesc") is not string desc || !string.Equals(desc.Trim(), adapterName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                switch (k.GetValue("HardwareInformation.qwMemorySize"))
                {
                    case long q when q > 0: return q;
                    case byte[] b when b.Length == 8: return BitConverter.ToInt64(b, 0);
                }
                switch (k.GetValue("HardwareInformation.MemorySize"))
                {
                    case int d when d != 0: return (uint)d;
                    case byte[] b when b.Length >= 4: return BitConverter.ToUInt32(b, 0);
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return null;
    }

    /// <summary>The live viewer's texture budget: <c>--texture-budget-mib=N</c> (configuration) wins; otherwise the
    /// detected video memory (half of it, minus the reserves), following the engine's non-texture memory live (see <see cref="NonTexture"/>).</summary>
    public static (Viewer.Core.Residency.LiveTextureBudget Budget, long? VideoMemory) TextureBudget()
    {
        long? configured = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--texture-budget-mib=", StringComparison.Ordinal)) is { } arg
            && long.TryParse(arg["--texture-budget-mib=".Length..], out var mib) && mib > 0 ? mib * Viewer.Core.Residency.TextureBudget.MiB : null;
        long? vram = Detect(RenderingServer.GetVideoAdapterName());
        return (new Viewer.Core.Residency.LiveTextureBudget(vram, configured), vram);
    }

    /// <summary>Live accounting: everything the engine holds in video memory that is not the residency's textures
    /// (RenderingInfo.VideoMemUsed, which on Forward+ includes textures, buffers and the engine's other allocations, minus
    /// the residency's resident bytes). measured the split on both backends.</summary>
    public static long NonTexture(long residentTextureBytes) =>
        (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed) - residentTextureBytes;
}
