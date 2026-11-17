// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>
/// Notecard windows, after the official viewer's notecard preview (indra/newview/llpreviewnotecard.cpp): the card's name as the
/// title, its description, the text read only, scrollable and selectable (a selection copies as plain text), web addresses that
/// open through the action pipeline, and below the text a row for each embedded item: a landmark shows where it leads and
/// offers a teleport, and "Copy to inventory" is enabled where the rules allow. Several cards can be open at once; opening one
/// already open brings its window to the front. Windows are a small pool of floaters that are reused, oldest first.
/// No layout file or artwork of any viewer is used.
/// </summary>
public sealed class NotecardFeature
{
    /// <summary>How many notecard windows can be open at once; opening one more reuses the window at the back.</summary>
    public const int MaxWindows = 8;

    private readonly ShellRoot _shell;
    private readonly StringTable _strings;
    private readonly NotecardWindowSet _set = new();
    private readonly Dictionary<Guid, int> _slotOf = new();
    private readonly NotecardPanel?[] _panels = new NotecardPanel?[MaxWindows];
    private readonly Guid[] _itemIn = new Guid[MaxWindows];
    private NotecardReader? _reader;
    private Func<ActionIntent, ActionResult>? _submit;
    private CancellationTokenSource _cancel = new();
    private readonly Action<string, string?>? _log;

    public NotecardFeature(ShellRoot shell, Action<string, string?>? log = null)
    {
        _log = log;
        _shell = shell;
        _strings = shell.Shell.Strings;
        shell.Shell.Floaters.Closed += OnClosed;
    }

    public static string FloaterId(int slot) => "notecard." + (slot + 1);

    /// <summary>The cards open, front last.</summary>
    public IReadOnlyList<Guid> Open => _set.Open;

    /// <summary>The window showing an item, for the development automation.</summary>
    public NotecardPanel? PanelOf(Guid itemId) => _slotOf.TryGetValue(itemId, out int slot) ? _panels[slot] : null;

    /// <summary>A session's inventory and pipeline (null: logged out; every notecard window closes).</summary>
    public void Bind(InventoryModel? model, INotecardTransport? transport, Func<ActionIntent, ActionResult>? submit)
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
        foreach (var item in _set.Open.ToArray())
            Close(item);
        _reader = model is null ? null : new NotecardReader(model, transport);
        _submit = submit;
    }

    /// <summary>Opens an inventory notecard, or brings its window to the front.</summary>
    public void OpenItem(Guid itemId, string name)
    {
        if (_reader is not { } reader)
            return;
        if (!Show(itemId, name, out var panel))
            return;
        panel.ShowLoading();
        var token = _cancel.Token;
        _ = reader.OpenAsync(itemId, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                Callable.From(() => Shown(itemId, t.Result)).CallDeferred();
        }, TaskScheduler.Default);
    }

    /// <summary>Opens a notecard inside an object, read-only (llpanelobjectinventory.cpp LLTaskNotecardBridge::openItem), or brings its
    /// window to the front.</summary>
    public void OpenTask(Viewer.Core.Session.TaskItem item, Guid objectId, Guid agent, bool objectModify, INotecardTransport? transport)
    {
        if (_reader is null)
            return;
        if (!Show(item.ItemId, item.Name, out var panel))
            return;
        panel.ShowLoading();
        var token = _cancel.Token;
        _ = NotecardReader.OpenTaskAsync(transport, item, objectId, agent, objectModify, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                Callable.From(() => Shown(item.ItemId, t.Result)).CallDeferred();
        }, TaskScheduler.Default);
    }

    /// <summary>Closes an item's window when one is open (the item was deleted).</summary>
    public void CloseItem(Guid itemId)
    {
        if (_slotOf.ContainsKey(itemId))
            Close(itemId);
    }

    /// <summary>Shows a card already read (the development automation's fixtures), in its own window.</summary>
    public void ShowOpened(NotecardOpenResult opened)
    {
        if (Show(opened.ItemId, opened.Name, out _) || _slotOf.ContainsKey(opened.ItemId))
            Shown(opened.ItemId, opened);
    }

    private void Shown(Guid itemId, NotecardOpenResult opened)
    {
        if (opened.Status != NotecardOpenStatus.Ok)
            _log?.Invoke(opened.Status.ToString(), opened.Error);
        if (PanelOf(itemId) is { } panel)
            panel.Show(new NotecardWindowModel(opened), Submit);
    }

    private ActionResult? Submit(ActionIntent intent) => _submit?.Invoke(intent);

    /// <summary>Opens a window for the item (true) or brings its window to the front (false).</summary>
    private bool Show(Guid itemId, string name, out NotecardPanel panel)
    {
        bool fresh = _set.Show(itemId);
        if (!fresh && _slotOf.TryGetValue(itemId, out int open))
        {
            panel = _panels[open]!;
            _shell.Shell.Floaters.Focus(FloaterId(open));
            return false;
        }
        int slot = FreeSlot();
        if (_itemIn[slot] != Guid.Empty && _itemIn[slot] != itemId)
        {
            _set.Close(_itemIn[slot]);
            _slotOf.Remove(_itemIn[slot]);
        }
        if (_panels[slot] is null)
        {
            _panels[slot] = new NotecardPanel(_strings, slot);
            _shell.AddFloater(new FloaterSpec(FloaterId(slot), "notecard.title", new UiSize(420, 460), new UiSize(280, 240)), _panels[slot]!);
        }
        _itemIn[slot] = itemId;
        _slotOf[itemId] = slot;
        panel = _panels[slot]!;
        _shell.SetFloaterTitle(FloaterId(slot), string.IsNullOrEmpty(name) ? _strings.Get("notecard.title") : name);
        _shell.Shell.Floaters.Open(FloaterId(slot));
        _shell.Shell.Floaters.Focus(FloaterId(slot));
        return true;
    }

    /// <summary>A slot with no window open, else the one at the back.</summary>
    private int FreeSlot()
    {
        for (int i = 0; i < MaxWindows; i++)
            if (_itemIn[i] == Guid.Empty || !_set.Open.Contains(_itemIn[i]))
                return i;
        Guid back = _set.Open.First(id => _slotOf.ContainsKey(id));
        return _slotOf[back];
    }

    private void Close(Guid itemId)
    {
        if (_slotOf.TryGetValue(itemId, out int slot))
            _shell.Shell.Floaters.Close(FloaterId(slot), force: true);
        _set.Close(itemId);
    }

    private void OnClosed(string floaterId)
    {
        for (int i = 0; i < MaxWindows; i++)
        {
            if (FloaterId(i) != floaterId || _itemIn[i] == Guid.Empty)
                continue;
            _set.Close(_itemIn[i]);
            _slotOf.Remove(_itemIn[i]);
            _itemIn[i] = Guid.Empty;
            _panels[i]?.Clear();
        }
    }
}

/// <summary>One notecard window's content.</summary>
public partial class NotecardPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private NotecardWindowModel? _model;
    private Func<ActionIntent, ActionResult?>? _submit;

    public Label DescriptionLabel { get; }
    public RichTextLabel TextView { get; }
    public VBoxContainer ItemRows { get; }
    public Label StatusLabel { get; }

    public NotecardPanel(StringTable strings, int slot)
    {
        _strings = strings;
        Name = "notecard_panel_" + (slot + 1);
        DescriptionLabel = new Label { Name = "description", AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = UiTheme.MutedLabel, CustomMinimumSize = new Vector2(200, 0) };
        DescriptionLabel.AccessibilityName = strings.Get("notecard.description");
        AddChild(DescriptionLabel);
        TextView = new RichTextLabel
        {
            Name = "text", BbcodeEnabled = true, SelectionEnabled = true, ContextMenuEnabled = true, ScrollActive = true,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.Click,
        };
        TextView.AccessibilityName = strings.Get("notecard.text");
        TextView.MetaClicked += OnMeta;
        AddChild(TextView);
        ItemRows = new VBoxContainer { Name = "items" };
        ItemRows.AccessibilityName = strings.Get("notecard.items");
        AddChild(ItemRows);
        StatusLabel = new Label { Name = "status", AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = UiTheme.MutedLabel, CustomMinimumSize = new Vector2(200, 0) };
        AddChild(StatusLabel);
    }

    /// <summary>The window's model, for the development automation.</summary>
    internal NotecardWindowModel? Model => _model;

    public void ShowLoading()
    {
        Clear();
        StatusLabel.Text = _strings.Get("notecard.loading");
    }

    public void Clear()
    {
        _model = null;
        DescriptionLabel.Text = string.Empty;
        TextView.Clear();
        foreach (var child in ItemRows.GetChildren())
            child.QueueFree();
        StatusLabel.Text = string.Empty;
    }

    public void Show(NotecardWindowModel model, Func<ActionIntent, ActionResult?> submit)
    {
        Clear();
        _model = model;
        _submit = submit;
        DescriptionLabel.Text = model.Opened.Description;
        DescriptionLabel.Visible = model.Opened.Description.Length > 0;
        if (model.Opened.MessageKey is { } key)
        {
            StatusLabel.Text = _strings.Get(key);
            return;
        }
        // the one link renderer every text place uses: web addresses, location and app links
        TextView.Text = model.RenderText(Escape, HostLinks.Router);
        foreach (var item in model.Opened.Document?.Items ?? [])
        {
            if (model.Offer(item.Index) is { } offer)
                ItemRows.AddChild(ItemRow(model, offer));
        }
    }

    private Control ItemRow(NotecardWindowModel model, NotecardItemOffer offer)
    {
        var row = new HBoxContainer { Name = "item_" + offer.Item.Index };
        string label = offer.Teleport && offer.Place.Length > 0 ? _strings.Get("notecard.place", offer.Item.Name, offer.Place) : offer.Item.Name;
        var name = new Label { Name = "name", Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        name.AccessibilityName = label;
        row.AddChild(name);
        if (offer.Teleport)
        {
            var teleport = new Button { Name = "teleport", Text = _strings.Get("notecard.teleport") };
            teleport.AccessibilityName = _strings.Get("notecard.teleport");
            teleport.Pressed += () => Send(model.TeleportTo(offer.Item.Index), _strings.Get("notecard.teleport.sent", offer.Item.Name));
            row.AddChild(teleport);
        }
        var copy = new Button { Name = "copy", Text = _strings.Get("notecard.copy"), Disabled = !offer.Copy };
        copy.AccessibilityName = _strings.Get("notecard.copy");
        copy.Pressed += () => Send(model.CopyToInventory(offer.Item.Index), _strings.Get("notecard.copy.sent", offer.Item.Name));
        row.AddChild(copy);
        return row;
    }

    /// <summary>Copies an embedded item out (the automation's way in; the button does the same).</summary>
    internal bool CopyItem(int index) => _model is { } m && Send(m.CopyToInventory(index), _strings.Get("notecard.copy.sent", m.Offer(index)?.Item.Name ?? ""));

    private bool Send(ActionIntent? intent, string sent)
    {
        if (intent is null || _submit is null)
            return false;
        var result = _submit(intent);
        bool ok = result is { Sent: true };
        StatusLabel.Text = ok ? sent : _strings.Get("notecard.copy.refused");
        return ok;
    }

    /// <summary>Clicks a link target as the mouse does (the development automation's way in).</summary>
    internal void ClickTarget(string meta) => OnMeta(meta);

    private void OnMeta(Variant meta)
    {
        if (_model is null || Viewer.Shell.Links.LinkText.LinkOf(meta.AsString()) is not { } link)
            return;
        StatusLabel.Text = HostLinks.Click(link) ?? "";
    }

    /// <summary>The card's text is shown as it is: a "[" would start a tag.</summary>
    private static string Escape(string text) => text.Replace("[", "[lb]");
}
