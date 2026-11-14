// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Building;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The Build menu's windows: Upload Image (one picture, with its name and description) and Bulk Upload (several pictures, each with its name,
/// its arriving size or why it cannot go, and its progress), and the world's drop target for inventory objects. They only draw what
/// <see cref="UploadChecks"/> and <see cref="UploadBatch"/> (tested without the engine) decide. Behaviour after the official viewer's image
/// preview and bulk upload (read as behaviour only); every text comes from the string table and every control has an accessible name.
/// Main thread only; the upload's own work runs on the thread pool and comes back through deferred calls.
/// </summary>
public static class BuildFeature
{
    public const string ImageFloaterId = "upload_image", BulkFloaterId = "upload_bulk";

    public sealed record Panels(TextureUploadPanel Image, TextureUploadPanel Bulk, WorldDropTarget Drop, FileDialog ImageDialog, FileDialog BulkDialog);

    /// <summary>Adds the windows, the file choosers and the drop target to a shell that is already drawn.</summary>
    public static Panels Install(ShellRoot shell, Func<ITextureUploadService?> service, Func<Guid> texturesFolder)
    {
        var s = shell.Shell;
        var image = new TextureUploadPanel(s.Strings, single: true, service, texturesFolder);
        var bulk = new TextureUploadPanel(s.Strings, single: false, service, texturesFolder);
        shell.AddFloater(new FloaterSpec(ImageFloaterId, "upload.title.image", new UiSize(420, 520), new UiSize(320, 360)), image);
        shell.AddFloater(new FloaterSpec(BulkFloaterId, "upload.title.bulk", new UiSize(520, 520), new UiSize(360, 300)), bulk);
        var drop = new WorldDropTarget();
        shell.MainView.AddChild(drop);
        shell.MainView.MoveChild(drop, 0);   // under every window, so a drop on a window stays the window's
        string imageFilter = "*" + string.Join(", *", UploadImageRules.Extensions) + " ; " + s.Strings.Get("upload.title.image");
        var imageDialog = new FileDialog
        {
            Name = "upload_image_dialog", FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
            Title = s.Strings.Get("upload.dialog.image"), Filters = [imageFilter],
        };
        // the official bulk upload offers every file and marks the ones it cannot take
        var bulkDialog = new FileDialog
        {
            Name = "upload_bulk_dialog", FileMode = FileDialog.FileModeEnum.OpenFiles, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
            Title = s.Strings.Get("upload.dialog.bulk"), Filters = [imageFilter, "* ; *"],
        };
        shell.AddChild(imageDialog);
        shell.AddChild(bulkDialog);
        imageDialog.FileSelected += path => { image.Open([path]); s.Floaters.Open(ImageFloaterId); };
        bulkDialog.FilesSelected += paths => { bulk.Open(paths); s.Floaters.Open(BulkFloaterId); };
        return new Panels(image, bulk, drop, imageDialog, bulkDialog);
    }

    /// <summary>Reads an image file into RGBA pixels with the engine's decoders (PNG, JPEG, TGA, BMP); a file of another kind is not read.</summary>
    public static UploadSource Load(string path)
    {
        if (!UploadImageRules.IsSupportedFile(path))
            return new UploadSource(path, 0, 0, null);
        var img = Image.LoadFromFile(path);
        if (img is null || img.IsEmpty())
            return new UploadSource(path, 0, 0, null, "unreadable");
        if (img.GetWidth() > UploadImageRules.MaxSourceDimension || img.GetHeight() > UploadImageRules.MaxSourceDimension)
            return new UploadSource(path, img.GetWidth(), img.GetHeight(), [], null);
        img.Convert(Image.Format.Rgba8);
        return new UploadSource(path, img.GetWidth(), img.GetHeight(), img.GetData());
    }
}

/// <summary>One upload window: Upload Image (<c>single</c>) or Bulk Upload.</summary>
public partial class TextureUploadPanel : VBoxContainer
{
    private sealed record Row(UploadCheck Check, byte[]? Pixels, LineEdit? Name, LineEdit? Description, Label State);

    private readonly StringTable _strings;
    private readonly bool _single;
    private readonly Func<ITextureUploadService?> _service;
    private readonly Func<Guid> _folder;
    private readonly VBoxContainer _rows = new() { Name = "upload_rows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly Label _cost = new() { Name = "upload_cost", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _summary = new() { Name = "upload_summary" };
    private readonly CheckBox _allow2K;
    private IReadOnlyList<string> _paths = [];
    private readonly List<Row> _list = new();
    private UploadBatch? _batch;
    private int? _shownPrice;
    private string? _priceError;
    private bool _running, _checking;
    private CancellationTokenSource _cts = new();
    private int _generation;

    public Button Upload { get; }
    public Button Close { get; }

    /// <summary>Raised on the main thread when an upload run has ended (every item done or failed).</summary>
    public event Action? Finished;

    /// <summary>What the last upload made: each image's name and new inventory item (failed ones left out).</summary>
    public IReadOnlyList<(string Name, Guid ItemId)> Uploaded => _batch is { } b
        ? b.Jobs.Zip(b.States).Where(p => p.Second.State == UploadItemState.Done && p.Second.ItemId != Guid.Empty).Select(p => (p.First.Name, p.Second.ItemId)).ToArray()
        : [];

    public TextureUploadPanel(StringTable strings, bool single, Func<ITextureUploadService?> service, Func<Guid> folder)
    {
        _strings = strings;
        _single = single;
        _service = service;
        _folder = folder;
        Name = single ? "upload_image_panel" : "upload_bulk_panel";
        var scroll = new ScrollContainer { Name = "upload_scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_rows);
        AddChild(scroll);
        var destination = new Label { Name = "upload_destination", Text = strings.Get("upload.destination") };
        AddChild(destination);
        // the official 2K choice: shown only where the grid allows 2048 (it prices 2K images, or sends no benefits and sets no limit)
        _allow2K = new CheckBox { Name = "allow_2k", Text = strings.Get("upload.allow_2k"), Visible = false };
        _allow2K.AccessibilityName = _allow2K.Text;
        _allow2K.Toggled += _ => { if (!_running) Open(_paths); };
        AddChild(_allow2K);
        AddChild(_cost);
        AddChild(_summary);
        var buttons = new HBoxContainer { Name = "upload_buttons" };
        Upload = new Button { Name = "upload_button", Text = strings.Get("upload.button") };
        Upload.AccessibilityName = Upload.Text;
        Close = new Button { Name = "upload_close_button", Text = strings.Get("upload.close") };
        Close.AccessibilityName = Close.Text;
        buttons.AddChild(Upload);
        buttons.AddChild(Close);
        AddChild(buttons);
        Upload.Pressed += OnUpload;
        _cost.AccessibilityName = strings.Get("upload.destination");
    }

    /// <summary>What the window shows, for checks: each row's name, its size or reason, and its state; then the cost line and the button.</summary>
    public IReadOnlyList<string> Texts() =>
        _list.Select(r => $"{r.Name?.Text ?? ""} | {(r.Check.Ok ? _strings.Get("upload.size", r.Check.TargetWidth, r.Check.TargetHeight) : _strings.Get(r.Check.Refusal!))} | {r.State.Text}")
            .Append(_cost.Text).Append(Upload.Text).ToArray();

    /// <summary>The upload's states as they stand (empty before an upload ran).</summary>
    public IReadOnlyList<UploadItemStatus> States => _batch?.States ?? [];

    /// <summary>Loads the chosen files, checks each, shows them at the size they will arrive at, and asks the grid for the price.</summary>
    public void Open(IReadOnlyList<string> paths)
    {
        _paths = paths.ToArray();
        var benefits = _service()?.Benefits ?? UploadBenefits.None;
        _allow2K.Visible = benefits.Offers2K;
        if (!_allow2K.Visible) _allow2K.SetPressedNoSignal(false);
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _generation++;
        _batch = null;
        _running = false;
        _shownPrice = null;
        _priceError = null;
        foreach (var c in _rows.GetChildren())
        {
            _rows.RemoveChild(c);
            c.QueueFree();
        }
        _list.Clear();
        var checks = UploadChecks.Check(paths.Select(BuildFeature.Load), _allow2K.ButtonPressed);
        foreach (var check in checks)
        {
            byte[]? pixels = check.Ok ? UploadChecks.Scale(check.Source.Rgba!, check.Source.Width, check.Source.Height, check.TargetWidth, check.TargetHeight) : null;
            _list.Add(AddRow(check, pixels));
        }
        Refresh();
        if (_list.Any(r => r.Check.Ok))
            AskPrice();
    }

    private Row AddRow(UploadCheck check, byte[]? pixels)
    {
        int index = _list.Count;
        var box = new VBoxContainer { Name = "upload_row_" + index };
        var line = new HBoxContainer { Name = "row_line" };
        box.AddChild(line);
        int previewSide = _single ? 256 : 96;
        if (pixels is not null)
        {
            var texture = ImageTexture.CreateFromImage(Image.CreateFromData(check.TargetWidth, check.TargetHeight, false, Image.Format.Rgba8, pixels));
            var preview = new TextureRect
            {
                Name = "preview", Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(previewSide, previewSide),
            };
            preview.AccessibilityName = UploadImageRules.DefaultName(check.Source.Path);
            line.AddChild(preview);
        }
        var fields = new VBoxContainer { Name = "fields", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        line.AddChild(fields);
        LineEdit? name = null, description = null;
        if (check.Ok)
        {
            name = new LineEdit { Name = "name", Text = UploadImageRules.DefaultName(check.Source.Path), PlaceholderText = _strings.Get("upload.name"), MaxLength = 63 };
            name.AccessibilityName = _strings.Get("upload.name");
            fields.AddChild(name);
            if (_single)
            {
                description = new LineEdit { Name = "description", PlaceholderText = _strings.Get("upload.description"), MaxLength = 127 };
                description.AccessibilityName = _strings.Get("upload.description");
                fields.AddChild(description);
            }
            fields.AddChild(new Label { Name = "size", Text = _strings.Get("upload.size", check.TargetWidth, check.TargetHeight) });
        }
        else
        {
            fields.AddChild(new Label { Name = "file", Text = System.IO.Path.GetFileName(check.Source.Path) });
            fields.AddChild(new Label { Name = "refusal", Text = _strings.Get(check.Refusal!), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }
        var state = new Label { Name = "state" };
        fields.AddChild(state);
        _rows.AddChild(box);
        return new Row(check, pixels, name, description, state);
    }

    private List<Row> Ready => _list.Where(r => r.Check.Ok).ToList();

    private UploadJob JobOf(Row r)
    {
        var px = r.Pixels!;
        int w = r.Check.TargetWidth, h = r.Check.TargetHeight;
        string name = (r.Name?.Text ?? "").Trim();
        if (name.Length == 0)
            name = UploadImageRules.DefaultName(r.Check.Source.Path);
        return new UploadJob(name, (r.Description?.Text ?? "").Trim(), () => Viewer.Core.Protocol.LmvTexture.EncodeUpload(w, h, px, UploadChecks.HasAlpha(px)));
    }

    /// <summary>The grid states the price in its answer to the first step; that is asked for the first image and shown before anything is sent.</summary>
    private void AskPrice()
    {
        if (_service() is not { } service)
        {
            _priceError = "upload.cost.no_service";
            Refresh();
            return;
        }
        var probe = new UploadBatch(service, _folder(), [JobOf(Ready[0])]);
        int generation = _generation;
        _checking = true;
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            try { await probe.CheckPriceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Callable.From(() =>
            {
                if (generation != _generation) return;
                _checking = false;
                _shownPrice = probe.PricePerItem;
                _priceError = probe.PriceError;
                Refresh();
            }).CallDeferred();
        });
        Refresh();
    }

    private void OnUpload()
    {
        if (_running || _checking || _shownPrice is not { } shown || Ready.Count == 0 || _service() is not { } service)
            return;
        var batch = new UploadBatch(service, _folder(), Ready.Select(JobOf).ToArray());
        _batch = batch;
        _running = true;
        int generation = _generation;
        var ct = _cts.Token;
        batch.Changed += () => Callable.From(() => { if (generation == _generation) Refresh(); }).CallDeferred();
        Task.Run(async () =>
        {
            try
            {
                await batch.CheckPriceAsync(ct).ConfigureAwait(false);
                // the price agreed is the one shown; a different one is shown instead and the button must be pressed again
                if (batch.PricePerItem == shown)
                    await batch.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            Callable.From(() =>
            {
                if (generation != _generation) return;
                _running = false;
                if (batch.PricePerItem != shown)
                {
                    _shownPrice = batch.PricePerItem;
                    _priceError = batch.PriceError;
                    _batch = null;
                }
                Refresh();
                if (_batch is not null) Finished?.Invoke();
            }).CallDeferred();
        });
        Refresh();
    }

    /// <summary>What the images cost: from the account's benefits per image (the official way, the 2K rule included), else the grid's
    /// answer for each; null until one of them is known.</summary>
    private int? ShownTotal()
    {
        var benefits = _service()?.Benefits ?? UploadBenefits.None;
        int total = 0;
        foreach (var r in Ready)
        {
            if (UploadCost.PerItem(benefits.PriceFor(r.Check.TargetWidth, r.Check.TargetHeight), _shownPrice) is not { } each)
                return null;
            total += Math.Max(0, each);
        }
        return total;
    }

    private void Refresh()
    {
        int count = Ready.Count;
        int? shownTotal = _priceError is null ? ShownTotal() : null;
        if (count == 0)
            _cost.Text = _list.Count == 0 ? "" : _strings.Get("upload.none");
        else if (_priceError is { } error)
            _cost.Text = error.StartsWith("upload.", StringComparison.Ordinal) ? _strings.Get(error) : _strings.Get("upload.cost.refused", error);
        else if (_shownPrice is not { } price)
            _cost.Text = _strings.Get("upload.cost.checking");
        else
            _cost.Text = (shownTotal ?? UploadCost.Total(price, count)) is var t && t > 0 ? _strings.Get("upload.cost.amount", t.ToString(CultureInfo.InvariantCulture)) : _strings.Get("upload.cost.none");
        var (key, amount) = UploadCost.Button(_priceError is null && _shownPrice is not null ? shownTotal : null, 1);
        Upload.Text = amount is { } a ? _strings.Get(key, a.ToString(CultureInfo.InvariantCulture)) : _strings.Get(key);
        Upload.AccessibilityName = Upload.Text;
        bool finished = _batch is not null && !_running;
        Upload.Disabled = count == 0 || _running || _checking || _shownPrice is null || _priceError is not null || finished;
        var states = _batch?.States;
        var ready = Ready;
        for (int i = 0; i < ready.Count; i++)
        {
            var st = states is not null && i < states.Count ? states[i] : null;
            ready[i].State.Text = st is null ? "" : st.State switch
            {
                UploadItemState.Waiting => _strings.Get("upload.state.waiting"),
                UploadItemState.Encoding => _strings.Get("upload.state.encoding"),
                UploadItemState.Sending => _strings.Get("upload.state.sending"),
                UploadItemState.Done => _strings.Get("upload.state.done"),
                _ => _strings.Get("upload.state.failed", st.Error ?? ""),
            };
            if (ready[i].Name is { } n) n.Editable = _batch is null;
            if (ready[i].Description is { } d) d.Editable = _batch is null;
        }
        _summary.Text = states is null ? "" : _strings.Get("upload.summary", states.Count(s => s.State == UploadItemState.Done), states.Count);
    }
}

/// <summary>The world under every window, as a place to drop an inventory object on: it takes a drop only while an object item is being dragged.</summary>
public partial class WorldDropTarget : Control
{
    /// <summary>Whether a dragged id is an object item that may be rezzed.</summary>
    public Func<Guid, bool>? Accepts;

    /// <summary>Raised when an object item was dropped on the world: its id and where (in the main view).</summary>
    public event Action<Guid, Vector2>? Dropped;

    public WorldDropTarget()
    {
        Name = "world_drop";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    private static bool IdOf(Variant data, out Guid id)
    {
        id = Guid.Empty;
        return data.VariantType == Variant.Type.String && Guid.TryParse(data.AsString(), out id);
    }

    /// <summary>Every frame: catches the mouse only while something it accepts is being dragged, so clicks in the world are never taken.</summary>
    public void Track(Viewport viewport)
    {
        bool want = viewport.GuiIsDragging() && IdOf(viewport.GuiGetDragData(), out var id) && (Accepts?.Invoke(id) ?? false);
        var filter = want ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        if (MouseFilter != filter)
            MouseFilter = filter;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => IdOf(data, out var id) && (Accepts?.Invoke(id) ?? false);

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (IdOf(data, out var id))
            Dropped?.Invoke(id, atPosition);
    }
}
