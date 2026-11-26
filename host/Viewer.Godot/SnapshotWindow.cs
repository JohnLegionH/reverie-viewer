// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Globalization;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Snapshots;

namespace Viewer.Godot;

/// <summary>Adds the Snapshot window to the shell: a floater and its entry in the World menu, on Ctrl+Shift+S, as the official viewer's.</summary>
public static class SnapshotFeature
{
    public const string FloaterId = "snapshot";

    public static SnapshotWindow Install(ShellRoot shell, SettingsService settings)
    {
        var s = shell.Shell;
        var window = new SnapshotWindow(s.Strings, settings);
        shell.AddFloater(new FloaterSpec(FloaterId, "snapshot.title", new UiSize(380, 480), new UiSize(320, 360)), window, window.BuildFooter());
        s.Commands.Register("floater.snapshot", () => s.Floaters.Toggle(FloaterId));
        s.Menus.AddEntry("world", new MenuEntry("world.snapshot", "menu.world.snapshot", "floater.snapshot", Checked: () => s.Floaters.IsOpen(FloaterId)));
        s.Floaters.Opened += id => { if (id == FloaterId) window.Opened?.Invoke(); };
        s.Floaters.Closed += id => { if (id == FloaterId) window.Closed?.Invoke(); };
        return window;
    }
}

/// <summary>
/// The Snapshot window: a preview of the picture, Refresh, the size, format, quality and interface choices, and Save to disk. The window
/// only shows and asks; the picture is taken and saved by its owner. It does no work of its own from frame to frame.
/// </summary>
public sealed partial class SnapshotWindow : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly TextureRect _preview;
    private readonly Label _folder;
    private readonly Label _status;
    private readonly HBoxContainer _qualityRow;
    private readonly Label _qualityValue;

    public OptionButton SizeChoice { get; }
    public OptionButton FormatChoice { get; }
    public HSlider Quality { get; }
    public CheckBox ShowInterface { get; }
    public Button RefreshButton { get; private set; } = null!;
    public Button SaveButton { get; private set; } = null!;
    public Button FolderButton { get; }
    public FileDialog FolderDialog { get; }

    public Action? Opened { get; set; }
    public Action? Closed { get; set; }

    /// <summary>The picture has to be taken again (Refresh, or a change of size or of the interface choice).</summary>
    public Action? CaptureRequested { get; set; }
    public Action? SaveRequested { get; set; }
    public Action? ChangeFolderRequested { get; set; }
    public Action<string>? FolderChosen { get; set; }

    public SnapshotSize ChosenSize => SnapshotModel.Sizes[Math.Max(0, SizeChoice.Selected)];
    public SnapshotFormat Format => (SnapshotFormat)Math.Max(0, FormatChoice.Selected);
    public string StatusText => _status.Text;
    public bool HasPreview => _preview.Texture is not null;

    public SnapshotWindow(StringTable strings, SettingsService settings)
    {
        _strings = strings;
        Name = "snapshot";
        AccessibilityName = strings.Get("snapshot.title");
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _preview = new TextureRect
        {
            Name = "snapshot_preview", CustomMinimumSize = new Vector2(0, 150), SizeFlagsVertical = SizeFlags.ExpandFill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        _preview.AccessibilityName = strings.Get("snapshot.preview");
        AddChild(_preview);

        SizeChoice = new OptionButton { Name = "snapshot_size", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var size in SnapshotModel.Sizes)
            SizeChoice.AddItem(size.IsWindow ? strings.Get("snapshot.size.window")
                : string.Create(CultureInfo.InvariantCulture, $"{size.Width} × {size.Height}"));
        SizeChoice.Select(settings.Get(Settings.SnapshotSizeChoice));
        SizeChoice.ItemSelected += i => { settings.Set(Settings.SnapshotSizeChoice, (int)i); CaptureRequested?.Invoke(); };
        AddRow("snapshot.size", SizeChoice);

        FormatChoice = new OptionButton { Name = "snapshot_format", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        FormatChoice.AddItem(strings.Get("snapshot.format.png"));
        FormatChoice.AddItem(strings.Get("snapshot.format.jpeg"));
        FormatChoice.Select(settings.Get(Settings.SnapshotFileFormat));
        FormatChoice.ItemSelected += i => { settings.Set(Settings.SnapshotFileFormat, (int)i); ShowQuality(); };
        AddRow("snapshot.format", FormatChoice);

        Quality = new HSlider { Name = "snapshot_quality", MinValue = 1, MaxValue = 100, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        Quality.Value = settings.Get(Settings.SnapshotJpegQuality);
        _qualityValue = new Label { Name = "snapshot_quality_value", CustomMinimumSize = new Vector2(36, 0), HorizontalAlignment = HorizontalAlignment.Right };
        Quality.ValueChanged += v => { settings.Set(Settings.SnapshotJpegQuality, (int)v); ShowQuality(); };
        _qualityRow = AddRow("snapshot.quality", Quality);
        _qualityRow.AddChild(_qualityValue);

        ShowInterface = new CheckBox { Name = "snapshot_interface", Text = strings.Get("snapshot.interface"), ButtonPressed = settings.Get(Settings.SnapshotInterface) };
        ShowInterface.AccessibilityName = ShowInterface.Text;
        ShowInterface.Toggled += on => { settings.Set(Settings.SnapshotInterface, on); CaptureRequested?.Invoke(); };
        AddChild(ShowInterface);

        var folderRow = new HBoxContainer { Name = "snapshot_folder_row" };
        _folder = new Label
        {
            Name = "snapshot_folder", ThemeTypeVariation = ReverieStyle.Muted, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = MouseFilterEnum.Pass,
        };
        folderRow.AddChild(_folder);
        FolderButton = new RvButton(strings.Get("snapshot.folder.change")) { Name = "snapshot_folder_btn" };
        FolderButton.Pressed += () => ChangeFolderRequested?.Invoke();
        folderRow.AddChild(FolderButton);
        AddChild(folderRow);

        _status = new Label { Name = "snapshot_status", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0) };
        AddChild(_status);

        FolderDialog = new FileDialog
        {
            Name = "snapshot_folder_dialog", FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true, Title = strings.Get("snapshot.folder.title"),
        };
        FolderDialog.DirSelected += dir => FolderChosen?.Invoke(dir);
        AddChild(FolderDialog);
        ShowQuality();
        ShowFolder(settings.GetText(Settings.SnapshotFolderKey));
    }

    /// <summary>Refresh and Save to disk, kept in view below the window's content.</summary>
    public Control BuildFooter()
    {
        var bar = new HBoxContainer { Name = "snapshot_footer" };
        RefreshButton = new RvButton(_strings.Get("snapshot.refresh")) { Name = "snapshot_refresh" };
        RefreshButton.Pressed += () => CaptureRequested?.Invoke();
        bar.AddChild(RefreshButton);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        SaveButton = new RvButton(_strings.Get("snapshot.save"), primary: true) { Name = "snapshot_save", Disabled = true };
        SaveButton.Pressed += () => SaveRequested?.Invoke();
        bar.AddChild(SaveButton);
        return bar;
    }

    private HBoxContainer AddRow(string key, Control field)
    {
        var row = new HBoxContainer { Name = field.Name + "_row" };
        var label = new Label { Text = _strings.Get(key), CustomMinimumSize = new Vector2(96, 0) };
        field.AccessibilityName = label.Text;
        row.AddChild(label);
        row.AddChild(field);
        AddChild(row);
        return row;
    }

    private void ShowQuality()
    {
        _qualityRow.Visible = Format == SnapshotFormat.Jpeg;
        _qualityValue.Text = ((int)Quality.Value).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The preview picture (null: none), and whether there is a picture that can be saved.</summary>
    public void ShowPreview(Texture2D? preview)
    {
        _preview.Texture = preview;
        SaveButton.Disabled = preview is null;
    }

    public void ShowStatus(string text) => _status.Text = text;

    public void ShowFolder(string? folder)
    {
        _folder.Text = folder ?? _strings.Get("snapshot.folder.none");
        _folder.TooltipText = folder ?? "";
    }
}
