// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Core.Session;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>The Contents window of an object: its items, and the buttons that copy them to inventory (after the official viewer's
/// Open Object floater: Copy To Inventory and Copy and Wear).</summary>
public partial class ContentsPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly Label _status = new() { Name = "status", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    public RowsPanel Rows { get; }
    public Button Copy { get; }
    public Button CopyWear { get; }

    public ContentsPanel(StringTable strings)
    {
        _strings = strings;
        Name = "contents_panel";
        Rows = new RowsPanel(strings, "contents_rows") { SizeFlagsVertical = SizeFlags.ExpandFill };
        Copy = new Button { Name = "copy_to_inventory_button", Text = strings.Get("contents.copy") };
        Copy.AccessibilityName = Copy.Text;
        CopyWear = new Button { Name = "copy_and_wear_button", Text = strings.Get("contents.copy_wear") };
        CopyWear.AccessibilityName = CopyWear.Text;
        var buttons = new HBoxContainer { Name = "buttons" };
        buttons.AddChild(Copy);
        buttons.AddChild(CopyWear);
        AddChild(_status);
        AddChild(Rows);
        AddChild(buttons);
    }

    /// <summary>Shows what is known of the contents: reading, failed, empty, or the items; the copy buttons are on only when the copy can be made.</summary>
    public void Show(Viewer.Shell.ObjectContentsView? view, bool canCopy, string? messageKey = null)
    {
        Copy.Disabled = CopyWear.Disabled = !canCopy;
        string key = messageKey ?? (view is null || (!view.Loaded && !view.Failed) ? "contents.loading" : view.Failed ? "contents.unavailable" : view.Items!.Count == 0 ? "contents.empty" : "");
        _status.Text = key.Length == 0 ? "" : _strings.Get(key);
        _status.Visible = key.Length > 0;
        Rows.Set((view?.Items ?? []).Select(i => (i.Name, TradeText.AssetTypeKey(i.AssetType))));
    }

    public IReadOnlyList<(string Label, string Value)> Texts() => Rows.Texts();
    public string StatusText => _status.Visible ? _status.Text : "";
}

/// <summary>The Buy window: what is offered, its price, and (for contents) what is inside, with Buy and Cancel.</summary>
public partial class BuyPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly Label _status = new() { Name = "status", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    public RowsPanel Rows { get; }
    public RowsPanel Items { get; }
    public Button Buy { get; }
    public Button Cancel { get; }

    public BuyPanel(StringTable strings)
    {
        _strings = strings;
        Name = "buy_panel";
        Rows = new RowsPanel(strings, "buy_rows") { CustomMinimumSize = new Vector2(0, 90) };
        Items = new RowsPanel(strings, "buy_items") { SizeFlagsVertical = SizeFlags.ExpandFill };
        Buy = new Button { Name = "buy_button", Text = strings.Get("buy.button") };
        Buy.AccessibilityName = Buy.Text;
        Cancel = new Button { Name = "buy_cancel_button", Text = strings.Get("buy.cancel") };
        Cancel.AccessibilityName = Cancel.Text;
        var buttons = new HBoxContainer { Name = "buttons" };
        buttons.AddChild(Buy);
        buttons.AddChild(Cancel);
        AddChild(_status);
        AddChild(Rows);
        AddChild(Items);
        AddChild(buttons);
    }

    /// <summary>Shows the offer, or (no offer) the reason there is none.</summary>
    public void Show(BuyOffer? offer, string? messageKey, Viewer.Shell.ObjectContentsView? contents)
    {
        Buy.Disabled = offer is not { CanBuy: true };
        _status.Text = messageKey is null ? "" : _strings.Get(messageKey);
        _status.Visible = messageKey is not null;
        if (offer is null)
        {
            Rows.Set([]);
            Items.Set([]);
            return;
        }
        Rows.Set(
        [
            ("inspect.name", offer.Name),
            ("buy.sale", offer.SaleType switch { 1 => "buy.sale.original", 2 => "buy.sale.copy", _ => "buy.sale.contents" }),
            ("buy.price", offer.Price.ToString(CultureInfo.InvariantCulture)),
        ]);
        Items.Set(offer.SaleType == 3 && contents?.Items is { } items ? items.Select(i => (i.Name, TradeText.AssetTypeKey(i.AssetType))) : []);
    }

    public string StatusText => _status.Visible ? _status.Text : "";
}

/// <summary>The Pay window: the amounts the object asked for as buttons, a field for any amount, and Pay.</summary>
public partial class PayPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly Label _name = new() { Name = "object_name" };
    private readonly HBoxContainer _quick = new() { Name = "quick_amounts" };
    public LineEdit Amount { get; }
    public Button Pay { get; }
    public event Action<int>? PayPressed;

    public PayPanel(StringTable strings)
    {
        _strings = strings;
        Name = "pay_panel";
        Amount = new LineEdit { Name = "pay_amount", PlaceholderText = strings.Get("pay.amount") };
        Amount.AccessibilityName = strings.Get("pay.amount");
        Pay = new Button { Name = "pay_button", Text = strings.Get("pay.button") };
        Pay.AccessibilityName = Pay.Text;
        Pay.Pressed += () => { if (int.TryParse(Amount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0) PayPressed?.Invoke(n); };
        AddChild(_name);
        AddChild(_quick);
        AddChild(Amount);
        AddChild(Pay);
    }

    /// <summary>Fills in what the object asked for: a default amount (protocol value -1 means none, -2 means the payer types it) and quick buttons.</summary>
    public void Show(PayOffer? offer)
    {
        foreach (var c in _quick.GetChildren())
        {
            _quick.RemoveChild(c);
            c.QueueFree();
        }
        _name.Text = offer?.Name ?? "";
        Amount.Text = offer is { DefaultPrice: > 0 } ? offer.DefaultPrice.ToString(CultureInfo.InvariantCulture) : "";
        Pay.Disabled = offer is null;
        foreach (int price in offer?.Buttons ?? [])
        {
            if (price <= 0)
                continue;
            var b = new Button { Name = "quick_" + price, Text = price.ToString(CultureInfo.InvariantCulture) };
            b.AccessibilityName = b.Text;
            int amount = price;
            b.Pressed += () => Amount.Text = amount.ToString(CultureInfo.InvariantCulture);
            _quick.AddChild(b);
        }
    }
}

internal static class TradeText
{
    /// <summary>The string-table key for an item's kind (the protocol's asset type number).</summary>
    public static string AssetTypeKey(int assetType) => assetType switch
    {
        0 => "asset.texture", 1 => "asset.sound", 3 => "asset.landmark", 5 => "asset.clothing", 6 => "asset.object", 7 => "asset.notecard",
        10 => "asset.script", 13 => "asset.bodypart", 20 => "asset.animation", 21 => "asset.gesture", 49 => "asset.mesh", _ => "asset.other",
    };
}
