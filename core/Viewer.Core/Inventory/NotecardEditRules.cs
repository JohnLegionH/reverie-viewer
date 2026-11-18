// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Inventory;

public enum EmbeddedClick { None, Open, OfferCopy }

public static class NotecardEditRules
{
    public const int MaxTextBytes = 0, MaxDescriptionBytes = 0;
    public static bool CanModify(NotecardAccess access, bool objectModify) => false;
    public static bool CanEmbed(ItemKind kind, uint nextOwnerMask) => false;
    public static EmbeddedClick ClickOf(ItemKind kind) => EmbeddedClick.None;
    public static int TextBytes(string text) => 0;
}

public enum NotecardSaveStatus { Saved, NoService, Failed }

public sealed record NotecardSaveResult(NotecardSaveStatus Status, Guid NewAssetId, string? Reason = null)
{
    public static NotecardSaveResult Saved(Guid asset) => new(NotecardSaveStatus.Saved, asset);
    public static NotecardSaveResult Of(NotecardSaveStatus status, string? reason = null) => new(status, Guid.Empty, reason);
}

public interface INotecardSaveTransport
{
    Task<NotecardSaveResult> SaveNotecardAsync(NotecardAccess access, byte[] file, CancellationToken ct);
}
