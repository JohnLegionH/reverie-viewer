// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Building;
using Viewer.Core.Protocol;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The model upload windows: Upload Model (one file) and Bulk Models (several), after the official model preview (llfloatermodelpreview.cpp,
/// read as behaviour only) kept simple: each model turning in a small preview, its triangles and levels of detail, its land impact and fee as
/// the grid states them, the shared settings (generated levels of detail, physics, include textures, scale; in Bulk Models each file may
/// override the scale), and an Upload button that names the fee when there is one. Reading, planning, fees and the upload are the core's
/// (<see cref="ModelImport"/>, <see cref="LmvModelUpload"/>, <see cref="ModelUploadBatch"/>, tested without the engine); this only draws them.
/// Main thread only; the planning and the grid's answers run on the thread pool and come back through deferred calls.
/// </summary>
public static class ModelFeature
{
    public const string ModelFloaterId = "upload_model", BulkModelsFloaterId = "upload_bulk_models";

    public sealed record Panels(ModelUploadPanel Model, ModelUploadPanel Bulk, FileDialog ModelDialog, FileDialog BulkDialog);

    public static Panels Install(ShellRoot shell, Func<IModelUploadService?> service, Func<Guid> objectsFolder, Func<Guid> texturesFolder)
    {
        var s = shell.Shell;
        var model = new ModelUploadPanel(s.Strings, single: true, service, objectsFolder, texturesFolder);
        var bulk = new ModelUploadPanel(s.Strings, single: false, service, objectsFolder, texturesFolder);
        shell.AddFloater(new FloaterSpec(ModelFloaterId, "model.title", new UiSize(460, 620), new UiSize(360, 420)), model);
        shell.AddFloater(new FloaterSpec(BulkModelsFloaterId, "model.title.bulk", new UiSize(560, 620), new UiSize(400, 360)), bulk);
        string filter = "*" + string.Join(", *", ModelImport.Extensions) + " ; " + s.Strings.Get("model.title");
        var modelDialog = new FileDialog
        {
            Name = "upload_model_dialog", FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
            Title = s.Strings.Get("model.dialog"), Filters = [filter],
        };
        // as the image bulk upload: every file is offered, and the ones that cannot go are marked with the reason
        var bulkDialog = new FileDialog
        {
            Name = "upload_bulk_models_dialog", FileMode = FileDialog.FileModeEnum.OpenFiles, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
            Title = s.Strings.Get("model.dialog.bulk"), Filters = [filter, "* ; *"],
        };
        shell.AddChild(modelDialog);
        shell.AddChild(bulkDialog);
        modelDialog.FileSelected += path => { model.Open([path]); s.Floaters.Open(ModelFloaterId); };
        bulkDialog.FilesSelected += paths => { bulk.Open(paths); s.Floaters.Open(BulkModelsFloaterId); };
        return new Panels(model, bulk, modelDialog, bulkDialog);
    }

    /// <summary>Reads a model file and the files it names beside it (by name only, in the same folder).</summary>
    public static ImportedModel Load(string path)
    {
        if (!ModelImport.IsSupportedFile(path))
            return ImportedModel.Refused(ModelRefusals.Format);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return ImportedModel.Refused(ModelRefusals.Unreadable);
            if (info.Length > ModelImport.MaxFileBytes) return ImportedModel.Refused(ModelRefusals.TooLarge);
            string folder = info.DirectoryName ?? "";
            byte[]? Sibling(string name)
            {
                string file = Path.Combine(folder, Path.GetFileName(name));
                return File.Exists(file) && new FileInfo(file).Length <= ModelImport.MaxFileBytes ? File.ReadAllBytes(file) : null;
            }
            return ModelImport.Read(path, File.ReadAllBytes(path), Sibling);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ImportedModel.Refused(ModelRefusals.Unreadable, e.GetType().Name);
        }
    }

    /// <summary>A model image the core's PNG reader does not take (JPEG and others), read by the engine; main thread only.</summary>
    public static (int Width, int Height, byte[] Rgba)? DecodeWithEngine(byte[] bytes)
    {
        var img = new Image();
        var err = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? img.LoadJpgFromBuffer(bytes)
            : PngDecoder.IsPng(bytes) ? img.LoadPngFromBuffer(bytes) : img.LoadWebpFromBuffer(bytes);
        if (err != Error.Ok || img.IsEmpty())
            return null;
        img.Convert(Image.Format.Rgba8);
        return (img.GetWidth(), img.GetHeight(), img.GetData());
    }
}

/// <summary>A model turning slowly in its own small 3D view. Its faces go to the engine as every surface does (prepared bytes handed over
/// by <see cref="PrimLayer.HandOver"/>); the background is left clear, so the window's own theme shows behind the model.</summary>
public partial class ModelPreview : SubViewportContainer
{
    private readonly SubViewport _viewport;
    private readonly World3D _world = new();   // the preview's own world, made and kept here (never read back from the viewport)
    private readonly List<StandardMaterial3D> _materials = new();
    private Rid _mesh, _instance;
    private float _angle;

    public ModelPreview(ImportedModel model, int side, Func<int, ImageTexture?> texture)
    {
        Name = "preview";
        Stretch = true;
        CustomMinimumSize = new Vector2(side, side);
        _viewport = new SubViewport { Name = "view", Size = new Vector2I(side, side), World3D = _world, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible };
        AddChild(_viewport);
        _viewport.AddChild(new DirectionalLight3D { Name = "key", RotationDegrees = new Vector3(-40, 30, 0) });
        _viewport.AddChild(new DirectionalLight3D { Name = "fill", RotationDegrees = new Vector3(-20, 210, 0), LightEnergy = 0.5f });
        var all = model.Meshes.Select(m => m.Bounds()).ToArray();
        var min = all.Length == 0 ? System.Numerics.Vector3.Zero : all.Select(b => b.Min).Aggregate(System.Numerics.Vector3.Min);
        var max = all.Length == 0 ? System.Numerics.Vector3.One : all.Select(b => b.Max).Aggregate(System.Numerics.Vector3.Max);
        var centre = (min + max) / 2;
        float radius = Math.Max(0.01f, (max - min).Length() / 2);
        _mesh = Rids.MeshCreate("model preview");
        int surface = 0;
        foreach (var f in model.Meshes.SelectMany(m => m.Faces))
        {
            if (f.Triangles == 0) continue;
            // centred on the model, in the viewer's axes; the shared encoder turns them to the engine's, as it does every prim's faces
            var p = new float[f.Positions.Length];
            for (int i = 0; i + 2 < p.Length; i += 3) { p[i] = f.Positions[i] - centre.X; p[i + 1] = f.Positions[i + 1] - centre.Y; p[i + 2] = f.Positions[i + 2] - centre.Z; }
            PrimLayer.HandOver(_mesh, Viewer.Shell.MeshSurfaceData.Encode(p, f.Normals, f.Indices, f.Uvs, colors: null, slAxes: true, reverseWinding: true));
            var mat = f.Material >= 0 && f.Material < model.Materials.Count ? model.Materials[f.Material] : ModelMaterial.Default;
            // the model's own colour is data from the file, not interface styling
            var material = new StandardMaterial3D { AlbedoColor = ShellTheme.DataColour(mat.BaseColor.X, mat.BaseColor.Y, mat.BaseColor.Z) };
            if (mat.BaseColorImage >= 0 && texture(mat.BaseColorImage) is { } tex) material.AlbedoTexture = tex;
            _materials.Add(material);
            RenderingServer.MeshSurfaceSetMaterial(_mesh, surface++, material.GetRid());
        }
        var camera = new Camera3D { Name = "camera", Fov = 40 };
        _viewport.AddChild(camera);
        camera.Position = new Vector3(0, radius * 0.6f, radius * 3.2f);
        camera.LookAtFromPosition(camera.Position, Vector3.Zero, Vector3.Up);
    }

    public override void _Ready()
    {
        if (_mesh.IsValid && !_instance.IsValid)
            _instance = Rids.InstanceCreate2(_mesh, _world.Scenario, "model preview");
    }

    public override void _Process(double delta)
    {
        if (!_instance.IsValid) return;
        _angle += (float)delta * 0.6f;
        RenderingServer.InstanceSetTransform(_instance, new Transform3D(new Basis(Vector3.Up, _angle), Vector3.Zero));
    }

    public override void _ExitTree()
    {
        if (_instance.IsValid) { Rids.Free(_instance); _instance = default; }
        if (_mesh.IsValid) { Rids.Free(_mesh); _mesh = default; }
    }
}

/// <summary>Upload Model (<c>single</c>) or Bulk Models.</summary>
public partial class ModelUploadPanel : VBoxContainer
{
    private sealed class Row
    {
        public required string Path;
        public required ImportedModel Model;
        public LineEdit? Name;
        public SpinBox? Scale;
        public required Label Info, Fee, State;
    }

    private readonly StringTable _strings;
    private readonly bool _single;
    private readonly Func<IModelUploadService?> _service;
    private readonly Func<Guid> _objects, _textures;
    private readonly VBoxContainer _rows = new() { Name = "model_rows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly CheckBox _autoLods, _includeTextures;
    private readonly OptionButton _physics;
    private readonly SpinBox _scale;
    private readonly Label _total = new() { Name = "model_total", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _summary = new() { Name = "model_summary" };
    private readonly List<Row> _list = new();
    private readonly ConditionalWeakTable<byte[], object> _decoded = new();
    private ModelUploadBatch? _batch;
    private bool _running, _ran;
    private CancellationTokenSource _cts = new();
    private int _generation;

    public Button Upload { get; }
    public Button Close { get; }

    public event Action? Finished;

    /// <summary>What the last upload made: each model's name and new inventory item (failed ones left out).</summary>
    public IReadOnlyList<(string Name, Guid ItemId)> Uploaded => _batch is { } b
        ? b.Jobs.Zip(b.States).Where(p => p.Second.State == UploadItemState.Done && p.Second.ItemId != Guid.Empty).Select(p => (p.First.Name, p.Second.ItemId)).ToArray()
        : [];

    public ModelUploadPanel(StringTable strings, bool single, Func<IModelUploadService?> service, Func<Guid> objects, Func<Guid> textures)
    {
        _strings = strings;
        _single = single;
        _service = service;
        _objects = objects;
        _textures = textures;
        Name = single ? "upload_model_panel" : "upload_bulk_models_panel";
        var scroll = new ScrollContainer { Name = "model_scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_rows);
        AddChild(scroll);
        var settings = new GridContainer { Name = "model_settings", Columns = 2 };
        _autoLods = new CheckBox { Name = "auto_lods", Text = strings.Get("model.auto_lods"), ButtonPressed = true };
        _includeTextures = new CheckBox { Name = "include_textures", Text = strings.Get("model.include_textures"), ButtonPressed = false };
        _physics = new OptionButton { Name = "physics" };
        _physics.AddItem(strings.Get("model.physics.none"), (int)PhysicsSource.None);
        _physics.AddItem(strings.Get("model.physics.lowest"), (int)PhysicsSource.LowestLod);
        _physics.AccessibilityName = strings.Get("model.physics");
        _scale = new SpinBox { Name = "scale", MinValue = ModelSettings.MinScale, MaxValue = ModelSettings.MaxScale, Step = 0.01, Value = 1 };
        _scale.AccessibilityName = strings.Get("model.scale");
        _autoLods.AccessibilityName = _autoLods.Text;
        _includeTextures.AccessibilityName = _includeTextures.Text;
        settings.AddChild(_autoLods); settings.AddChild(_includeTextures);
        settings.AddChild(new Label { Text = strings.Get("model.physics") }); settings.AddChild(_physics);
        settings.AddChild(new Label { Text = strings.Get("model.scale") }); settings.AddChild(_scale);
        AddChild(settings);
        AddChild(new Label { Name = "model_destination", Text = strings.Get("model.destination") });
        AddChild(_total);
        AddChild(_summary);
        var buttons = new HBoxContainer { Name = "model_buttons" };
        Upload = new Button { Name = "model_upload_button", Text = strings.Get("upload.button") };
        Close = new Button { Name = "model_close_button", Text = strings.Get("upload.close") };
        Upload.AccessibilityName = Upload.Text;
        Close.AccessibilityName = Close.Text;
        buttons.AddChild(Upload); buttons.AddChild(Close);
        AddChild(buttons);
        Upload.Pressed += OnUpload;
        _autoLods.Toggled += _ => AskFees();
        _includeTextures.Toggled += _ => AskFees();
        _physics.ItemSelected += _ => AskFees();
        _scale.ValueChanged += v =>
        {
            foreach (var r in _list) if (r.Scale is { } s) s.SetValueNoSignal(v);
            AskFees();
        };
    }

    private ModelSettings Settings(Row? row = null) => new(_autoLods.ButtonPressed, (PhysicsSource)_physics.GetSelectedId(), _includeTextures.ButtonPressed,
        (float)(row?.Scale?.Value ?? _scale.Value));

    /// <summary>Sets the shared settings (for checks): include textures, physics from the lowest LOD, generated LODs, scale.</summary>
    public void Choose(bool includeTextures, bool lowestPhysics, bool autoLods, float scale)
    {
        _includeTextures.SetPressedNoSignal(includeTextures);
        _physics.Select(lowestPhysics ? 1 : 0);
        _autoLods.SetPressedNoSignal(autoLods);
        _scale.SetValueNoSignal(scale);
        foreach (var r in _list) r.Scale?.SetValueNoSignal(scale);
        AskFees();
    }

    public IReadOnlyList<string> Texts() =>
        _list.Select(r => $"{r.Name?.Text ?? System.IO.Path.GetFileName(r.Path)} | {r.Info.Text} | {r.Fee.Text} | {r.State.Text}").Append(_total.Text).Append(Upload.Text).ToArray();

    public IReadOnlyList<ModelItemStatus> States => _batch?.States ?? [];

    public void Open(IReadOnlyList<string> paths)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _generation++;
        _batch = null;
        _running = false;
        _ran = false;
        foreach (var c in _rows.GetChildren()) { _rows.RemoveChild(c); c.QueueFree(); }
        _list.Clear();
        foreach (var path in paths)
        {
            var model = ModelFeature.Load(path);
            // images the core's PNG reader does not take are read by the engine here, on the main thread, for the planning to use
            var textures = new Dictionary<int, ImageTexture?>();
            if (model.Ok)
                for (int i = 0; i < model.Images.Count; i++)
                {
                    var bytes = model.Images[i].Encoded;
                    if (bytes.Length == 0) continue;
                    var px = PngDecoder.Decode(bytes) ?? ModelFeature.DecodeWithEngine(bytes);
                    if (px is { } p)
                    {
                        if (!PngDecoder.IsPng(bytes)) _decoded.AddOrUpdate(bytes, p);
                        textures[i] = ImageTexture.CreateFromImage(Image.CreateFromData(p.Width, p.Height, false, Image.Format.Rgba8, p.Rgba));
                    }
                }
            _list.Add(AddRow(path, model, textures));
        }
        AskFees();
    }

    private Row AddRow(string path, ImportedModel model, Dictionary<int, ImageTexture?> textures)
    {
        var box = new HBoxContainer { Name = "model_row_" + _list.Count };
        if (model.Ok)
            box.AddChild(new ModelPreview(model, _single ? 220 : 96, i => textures.GetValueOrDefault(i)));
        var fields = new VBoxContainer { Name = "fields", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(fields);
        var row = new Row
        {
            Path = path, Model = model,
            Info = new Label { Name = "info", AutowrapMode = TextServer.AutowrapMode.WordSmart },
            Fee = new Label { Name = "fee", AutowrapMode = TextServer.AutowrapMode.WordSmart },
            State = new Label { Name = "state", AutowrapMode = TextServer.AutowrapMode.WordSmart },
        };
        if (model.Ok)
        {
            row.Name = new LineEdit { Name = "name", Text = UploadImageRules.DefaultName(path), PlaceholderText = _strings.Get("upload.name"), MaxLength = 63 };
            row.Name.AccessibilityName = _strings.Get("upload.name");
            fields.AddChild(row.Name);
            if (!_single)
            {
                row.Scale = new SpinBox { Name = "row_scale", MinValue = ModelSettings.MinScale, MaxValue = ModelSettings.MaxScale, Step = 0.01, Value = _scale.Value, Prefix = _strings.Get("model.scale") };
                row.Scale.AccessibilityName = _strings.Get("model.scale");
                row.Scale.ValueChanged += _ => AskFees();
                fields.AddChild(row.Scale);
            }
        }
        else
        {
            fields.AddChild(new Label { Name = "file", Text = System.IO.Path.GetFileName(path) });
            row.State.Text = _strings.Get(model.Refusal!);
        }
        fields.AddChild(row.Info); fields.AddChild(row.Fee); fields.AddChild(row.State);
        _rows.AddChild(box);
        return row;
    }

    private (int, int, byte[])? Decode(byte[] bytes) => _decoded.TryGetValue(bytes, out var o) ? ((int, int, byte[]))o : null;

    private ModelJob JobOf(Row r)
    {
        if (!r.Model.Ok)
            return new ModelJob(System.IO.Path.GetFileName(r.Path), () => null, r.Model.Refusal);
        string name = (r.Name?.Text ?? "").Trim();
        if (name.Length == 0) name = UploadImageRules.DefaultName(r.Path);
        var settings = Settings(r);
        Guid objects = _objects(), textures = _textures();
        var model = r.Model;
        return new ModelJob(name, () => LmvModelUpload.Plan(model, settings, name, "", objects, textures, img => LmvModelUpload.EncodeTexture(img, Decode)));
    }

    /// <summary>Plans every model with the settings shown and asks the grid each one's fee and weights; nothing is sent.</summary>
    private void AskFees()
    {
        if (_running || _list.Count == 0)
            return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        int generation = ++_generation;
        _ran = false;
        if (_service() is not { } service)
        {
            _batch = null;
            Refresh(noService: true);
            return;
        }
        var batch = new ModelUploadBatch(service, _list.Select(JobOf).ToArray());
        _batch = batch;
        var ct = _cts.Token;
        batch.Changed += () => Callable.From(() => { if (generation == _generation) Refresh(); }).CallDeferred();
        Task.Run(async () =>
        {
            try { await batch.CheckFeesAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Callable.From(() => { if (generation == _generation) Refresh(); }).CallDeferred();
        });
        Refresh();
    }

    private void OnUpload()
    {
        if (_running || _ran || _batch is not { } batch || batch.TotalFee is null)
            return;
        _running = true;
        int generation = _generation;
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            try { await batch.RunAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Callable.From(() =>
            {
                if (generation != _generation) return;
                _running = false;
                _ran = true;
                Refresh();
                Finished?.Invoke();
            }).CallDeferred();
        });
        Refresh();
    }

    private void Refresh(bool noService = false)
    {
        var states = _batch?.States;
        int going = 0;
        for (int i = 0; i < _list.Count; i++)
        {
            var r = _list[i];
            if (r.Model.Ok)
            {
                int tris = r.Model.Triangles;
                var t = ModelLods.Targets(tris);
                r.Info.Text = _strings.Get("model.triangles", tris.ToString(CultureInfo.InvariantCulture)) + "\n" + (_autoLods.ButtonPressed
                    ? _strings.Get("model.lods", t[3], t[2], t[1], t[0]) : _strings.Get("model.lods", tris, tris, tris, tris));
            }
            var st = states is not null && i < states.Count ? states[i] : null;
            if (!r.Model.Ok) continue;
            if (st?.Fee is { } fee)
            {
                r.Fee.Text = _strings.Get("model.land_impact", fee.LandImpact.ToString("0.##", CultureInfo.InvariantCulture)) + " · "
                    + (fee.Price > 0 ? _strings.Get("model.fee.amount", fee.Price) : _strings.Get("model.fee.none")) + "\n"
                    + _strings.Get("model.weights", fee.Streaming.ToString("0.##", CultureInfo.InvariantCulture), fee.Physics.ToString("0.##", CultureInfo.InvariantCulture), fee.Simulation.ToString("0.##", CultureInfo.InvariantCulture));
                going++;
            }
            else
                r.Fee.Text = noService ? _strings.Get("upload.cost.no_service") : st?.State == UploadItemState.Failed ? "" : _strings.Get("model.fee.checking");
            r.State.Text = st is null ? "" : st.State switch
            {
                UploadItemState.Waiting => "",
                UploadItemState.Sending or UploadItemState.Encoding => _strings.Get("upload.state.sending"),
                UploadItemState.Done => _strings.Get("model.state.done"),
                _ => st.Error is { } e && e.StartsWith("model.", StringComparison.Ordinal) ? _strings.Get(e)
                    : st.Fee is null ? _strings.Get("model.fee.refused", st.Error ?? "") : _strings.Get("upload.state.failed", st.Error ?? ""),
            };
            if (r.Name is { } n) n.Editable = !_running && !_ran;
        }
        int? total = _batch?.TotalFee;
        _total.Text = _list.All(r => !r.Model.Ok) ? (_list.Count == 0 ? "" : _strings.Get("model.none"))
            : noService ? _strings.Get("upload.cost.no_service")
            : total is not { } tf ? _strings.Get("model.fee.checking")
            : tf > 0 ? _strings.Get("model.fee.amount", tf) : _strings.Get("model.fee.none");
        var (key, amount) = UploadCost.Button(total, 1);
        Upload.Text = amount is { } a ? _strings.Get(key, a.ToString(CultureInfo.InvariantCulture)) : _strings.Get(key);
        Upload.AccessibilityName = Upload.Text;
        Upload.Disabled = _running || _ran || total is null || going == 0;
        _summary.Text = _ran && states is not null ? _strings.Get("upload.summary", states.Count(s => s.State == UploadItemState.Done), states.Count) : "";
        _autoLods.Disabled = _includeTextures.Disabled = _running || _ran;
        _physics.Disabled = _running || _ran;
        _scale.Editable = !_running && !_ran;
    }
}
