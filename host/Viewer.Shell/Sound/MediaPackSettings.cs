// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Core.Media.Packs;

namespace Viewer.Shell.Sound;

/// <summary>The packs the person said no to, kept as settings text (one id a line), and how a pack's size is written.</summary>
public sealed class MediaPackSettings(Func<string, string?> read, Action<string, string> write) : IPackAnswers
{
    public const string RefusedKey = "media_pack_refused";

    private List<string> Refused() => (read(RefusedKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public bool IsRefused(string id) => Refused().Contains(id);

    public void SetRefused(string id, bool refused)
    {
        var ids = Refused();
        if (refused == ids.Contains(id))
            return;
        if (refused)
            ids.Add(id);
        else
            ids.Remove(id);
        write(RefusedKey, string.Join("\n", ids));
    }

    /// <summary>A download's size in whole megabytes (a million bytes), rounded up so it is never shown smaller than it is.</summary>
    public static string SizeText(long bytes) => Math.Max(1, (bytes + 999_999) / 1_000_000).ToString(CultureInfo.CurrentCulture) + " MB";
}
