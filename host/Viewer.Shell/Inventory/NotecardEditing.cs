// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

public sealed class NotecardEditBuffer
{
    public const char Mark = '⁣';
    public NotecardEditBuffer(NotecardDocument? document) { }
    public string ToDisplay(string fileText) => "";
    public (string Text, IReadOnlyList<NotecardEmbeddedItem> Items) FromDisplay(string display) => ("", []);
    public string Insert(string display, int at, NotecardEmbeddedItem item) => "";
    public byte[] ToFile(string display) => [];
    public NotecardEmbeddedItem? ItemAt(string display, int index) => null;
    public static (int Start, int Length)? TokenSpanAt(string display, int caret) => null;
}

public enum NotecardCloseVerdict { Close, Ask }

public sealed class NotecardEditSession
{
    public NotecardEditSession(NotecardAccess access, bool objectModify, NotecardDocument document, INotecardSaveTransport? transport) { }
    public bool CanEdit => true;
    public bool CanSave => false;
    public bool CanCopyText => false;
    public bool IsDirty => false;
    public bool OverLimit => false;
    public void SetText(string display, IReadOnlyList<NotecardEmbeddedItem> known) { }
    public Task<NotecardSaveResult> SaveAsync(CancellationToken ct) => Task.FromResult(NotecardSaveResult.Of(NotecardSaveStatus.NoService));
    public NotecardCloseVerdict RequestClose() => NotecardCloseVerdict.Ask;
}
