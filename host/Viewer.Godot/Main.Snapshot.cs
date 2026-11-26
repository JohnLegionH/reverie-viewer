// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Shell;
using Viewer.Shell.Snapshots;

namespace Viewer.Godot;

/// <summary>
/// Taking and saving snapshots. A picture is the window's own picture: for a larger size the window's content is drawn at that size for a
/// few frames (the window itself keeps its size), so every effect of the view is in it. The interface is hidden for those frames unless it
/// was asked for; the Snapshot window is never in the picture. Nothing here runs from frame to frame, and the picture is let go when the
/// window closes.
/// </summary>
public partial class Main
{
    private const int SnapshotSettleFrames = 6;   // frames drawn at the new size before the picture is read (the glow pass follows the size)

    private SnapshotWindow? _snapshot;
    private Image? _snapshotShot;
    private ImageTexture? _snapshotPreview;
    private WindowPicture? _snapshotWindowPicture;
    private bool _snapshotBusy;
    private bool _snapshotAgain;
    private bool _snapshotAutomation;
    private bool _snapshotSaveAfterFolder;
    private string? _snapshotSaved;
    private long _snapshotPrivateBefore, _snapshotPrivateAfter, _snapshotVideoBefore, _snapshotVideoPeak;

    // the window's own picture: a handle on it (no pixels), read from the engine once and kept
    private WindowPicture SnapshotView => _snapshotWindowPicture ??= new WindowPicture(GetViewport());

    private void InstallSnapshot()
    {
        if (_snapshot is not null || _shell is null)
            return;
        _snapshot = SnapshotFeature.Install(_shell, UserSettings(null));
#if NV_AUTOMATION
        _snapshotAutomation = Viewer.Automation.AutomationArgs.Enabled(OS.GetCmdlineUserArgs());
#endif
        _snapshot.FolderDialog.UseNativeDialog = !_snapshotAutomation;   // a driven run's folder question stays inside the viewer's own window
        _snapshot.Opened = () => _ = CaptureSnapshot();
        _snapshot.CaptureRequested = () => _ = CaptureSnapshot();
        _snapshot.Closed = ReleaseSnapshot;
        _snapshot.SaveRequested = SaveSnapshot;
        _snapshot.InventoryRequested = () => _ = AskSnapshotFee();
        _snapshot.InventoryConfirmed = () => _ = UploadSnapshot();
        _snapshot.InventoryCancelled = DropSnapshotUpload;
        _snapshot.ChangeFolderRequested = () => AskSnapshotFolder(thenSave: false);
        _snapshot.FolderChosen = OnSnapshotFolderChosen;
    }

    private bool SnapshotOpen => _shell is not null && _shell.Shell.Floaters.IsOpen(SnapshotFeature.FloaterId);

    private int? _snapshotMaxSideForTest;   // set only by the development automation, to meet the refusal on a card that can draw every size

    /// <summary>The graphics card's largest 2D texture side.</summary>
    private int SnapshotMaxSide()
    {
        if (_snapshotMaxSideForTest is { } limit)
            return limit;
        var device = RenderingServer.GetRenderingDevice();
        return device is null ? 16384 : (int)device.LimitGet(RenderingDevice.Limit.MaxTextureSize2D);
    }

    private static long SnapshotPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static long SnapshotVideoBytes() => (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);

    private void DropSnapshotShot()
    {
        DropSnapshotUpload();   // a question about a picture that is gone is dropped
        _snapshotShot?.Dispose();
        _snapshotShot = null;
        _snapshot?.ShowPreview(null);
        _snapshotPreview?.Dispose();
        _snapshotPreview = null;
    }

    private void ReleaseSnapshot()
    {
        DropSnapshotShot();
        _snapshotSaveAfterFolder = false;
        _snapshot?.ShowStatus("");
    }

    private async Task CaptureSnapshot()
    {
        if (_snapshot is not { } window || _shell is null || !SnapshotOpen)
            return;
        if (_snapshotBusy)
        {
            _snapshotAgain = true;   // a choice changed while a picture was being taken: taken once more afterwards
            return;
        }
        var strings = _shell.Shell.Strings;
        var size = window.ChosenSize;
        var drawing = GetWindow().Size;
        var wanted = SnapshotModel.Resolve(size, drawing.X, drawing.Y);   // named if the picture cannot be taken
        int maxSide = SnapshotMaxSide();
        DropSnapshotShot();
        if (!SnapshotModel.Allowed(size, maxSide))
        {
            window.ShowStatus(strings.Get("snapshot.refused", maxSide));
            Log.Info("snapshot.refused", CoreLog.F("width", size.Width), CoreLog.F("height", size.Height), CoreLog.F("max-side", maxSide));
            return;
        }
        _snapshotBusy = true;
        try
        {
            bool withInterface = window.ShowInterface.ButtonPressed;
            _snapshotPrivateBefore = SnapshotPrivateBytes();
            _snapshotVideoBefore = SnapshotVideoBytes();
            if (!withInterface && SnapshotDialogUp())
            {
                window.ShowStatus(strings.Get("snapshot.dialog"));
                return;
            }
            var image = await TakeWindowPicture(size, withInterface);
            _snapshotPrivateAfter = SnapshotPrivateBytes();
            if (!SnapshotOpen)
            {
                image?.Dispose();
                return;
            }
            if (image is null)
            {
                if (!withInterface && SnapshotDialogUp())
                    window.ShowStatus(strings.Get("snapshot.dialog"));
                else
                    window.ShowStatus(strings.Get("snapshot.failed", wanted.Width, wanted.Height));
                return;
            }
            _snapshotShot = image;
            using (var small = (Image)image.Duplicate())
            {
                float scale = Math.Min(1f, 480f / Math.Max(1, small.GetWidth()));
                small.Resize(Math.Max(1, (int)(small.GetWidth() * scale)), Math.Max(1, (int)(small.GetHeight() * scale)), Image.Interpolation.Bilinear);
                _snapshotPreview = ImageTexture.CreateFromImage(small);
                window.ShowPreview(_snapshotPreview);
            }
            window.ShowStatus(strings.Get("snapshot.ready", image.GetWidth(), image.GetHeight()));
            Log.Info("snapshot.captured", CoreLog.F("width", image.GetWidth()), CoreLog.F("height", image.GetHeight()), CoreLog.F("interface", withInterface),
                CoreLog.F("picture-bytes", (long)image.GetDataSize()), CoreLog.F("private-before-mib", _snapshotPrivateBefore >> 20),
                CoreLog.F("private-after-mib", _snapshotPrivateAfter >> 20), CoreLog.F("video-before-mib", _snapshotVideoBefore >> 20),
                CoreLog.F("video-peak-mib", _snapshotVideoPeak >> 20));
        }
        finally
        {
            _snapshotBusy = false;
            if (_snapshotAgain)
            {
                _snapshotAgain = false;
                _ = CaptureSnapshot();
            }
        }
    }

    /// <summary>The viewer's own windows drawn inside its window (a question, a menu, a tooltip): the engine draws them over everything,
    /// so hiding the interface's nodes does not hide them.</summary>
    private IEnumerable<Window> SnapshotSubwindows() => GetViewport().GetEmbeddedSubwindows().Where(w => w.Visible);

    /// <summary>A question is waiting for an answer. It is left alone (hiding it could answer it): no picture without the interface is taken.</summary>
    private bool SnapshotDialogUp() => SnapshotSubwindows().Any(w => w is not Popup);

    /// <summary>The nodes that are interface, not world: everything drawn over the view, and the marks drawn into it (the selection
    /// outline, the chosen faces, the edit handles and floating text).</summary>
    private IEnumerable<Node> SnapshotInterfaceNodes() =>
        GetChildren().Where(n => n is CanvasItem or CanvasLayer)
            .Concat(new Node?[] { _outline, _faceHighlight, _hoverText, _handles, _banWall }.OfType<Node>());

    private async Task<Image?> TakeWindowPicture(SnapshotSize size, bool withInterface)
    {
        var root = GetWindow();
        var mode = root.ContentScaleMode;
        var aspect = root.ContentScaleAspect;
        var content = root.ContentScaleSize;
        float factor = root.ContentScaleFactor;
        var drawing = root.Size;   // the window's drawing size in pixels, whatever the interface size is
        var (width, height) = SnapshotModel.Resolve(size, drawing.X, drawing.Y);
        bool resize = !size.IsWindow;
        var hidden = new List<Node>();

        // run again just before each frame is drawn: some of these are shown again by their own per-frame work
        void Hide()
        {
            if (!withInterface)
                foreach (var popup in SnapshotSubwindows().OfType<Popup>().ToList())
                    popup.Hide();   // a menu or tooltip, which closes on the next click anyway
            IEnumerable<Node> nodes = !withInterface ? SnapshotInterfaceNodes()
                : _shell!.Frames.TryGetValue(SnapshotFeature.FloaterId, out var own) ? new Node[] { own } : [];
            foreach (var node in nodes)
            {
                if (node is CanvasItem { Visible: true } item) { item.Visible = false; hidden.Add(item); }
                else if (node is CanvasLayer { Visible: true } layer) { layer.Visible = false; hidden.Add(layer); }
                else if (node is Node3D { Visible: true } spatial) { spatial.Visible = false; hidden.Add(spatial); }
            }
        }

        var beforeDraw = Callable.From(Hide);
        RenderingServer.Singleton.Connect(RenderingServer.SignalName.FramePreDraw, beforeDraw);
        try
        {
            if (resize)
            {
                root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
                root.ContentScaleAspect = Window.ContentScaleAspectEnum.Ignore;
                var scale = SnapshotModel.ContentScale(width, height, factor, drawing.Y);
                root.ContentScaleSize = new Vector2I(scale.Width, scale.Height);
                root.ContentScaleFactor = scale.Factor;
            }
            _snapshotVideoPeak = 0;
            for (int i = 0; i < (resize ? SnapshotSettleFrames : 2); i++)
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                _snapshotVideoPeak = Math.Max(_snapshotVideoPeak, SnapshotVideoBytes());
            }
            if (!withInterface && SnapshotDialogUp())
                return null;   // one opened while the picture was being drawn
            var image = SnapshotView.Take();
            if (image is null || !SnapshotModel.Accepts(size, image.GetWidth(), image.GetHeight()))
            {
                Log.Info("snapshot.wrong-size", CoreLog.F("wanted-width", width), CoreLog.F("wanted-height", height),
                    CoreLog.F("width", image?.GetWidth() ?? 0), CoreLog.F("height", image?.GetHeight() ?? 0));
                image?.Dispose();
                return null;
            }
            image.Convert(Image.Format.Rgb8);
            return image;
        }
        finally
        {
            RenderingServer.Singleton.Disconnect(RenderingServer.SignalName.FramePreDraw, beforeDraw);
            foreach (var node in hidden.Where(IsInstanceValid))
            {
                if (node is CanvasItem item) item.Visible = true;
                else if (node is CanvasLayer layer) layer.Visible = true;
                else if (node is Node3D spatial) spatial.Visible = true;
            }
            if (resize)
            {
                root.ContentScaleSize = content;
                root.ContentScaleAspect = aspect;
                root.ContentScaleMode = mode;
                root.ContentScaleFactor = factor;
            }
        }
    }

    private void SaveSnapshot()
    {
        if (_snapshot is not { } window || _shell is null || _snapshotShot is not { } shot || _snapshotBusy)
            return;
        var settings = UserSettings(null);
        if (settings.GetText(Settings.SnapshotFolderKey) is not { Length: > 0 } folder)
        {
            AskSnapshotFolder(thenSave: true);
            return;
        }
        var strings = _shell.Shell.Strings;
        var format = window.Format;
        byte[] bytes = format == SnapshotFormat.Jpeg ? shot.SaveJpgToBuffer((float)window.Quality.Value / 100f) : shot.SavePngToBuffer();
        try
        {
            _snapshotSaved = SnapshotModel.Save(folder, format, bytes);
            window.ShowStatus(strings.Get("snapshot.saved", Path.GetFileName(_snapshotSaved)));
            Log.Info("snapshot.saved", CoreLog.F("format", format.ToString()), CoreLog.F("bytes", (long)bytes.Length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            window.ShowStatus(strings.Get("snapshot.save.failed"));
            Log.Info("snapshot.save-failed", CoreLog.F("kind", e.GetType().Name));
        }
    }

    private void AskSnapshotFolder(bool thenSave)
    {
        if (_snapshot is not { } window)
            return;
        _snapshotSaveAfterFolder = thenSave;
        if (!_snapshotAutomation)   // the offered default has to exist to be shown; a driven run makes nothing under the person's own folders
        {
            string pictures = OS.GetSystemDir(OS.SystemDir.Pictures);
            string start = UserSettings(null).GetText(Settings.SnapshotFolderKey) ?? SnapshotModel.DefaultFolder(pictures);
            try { Directory.CreateDirectory(start); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { start = pictures; }
            window.FolderDialog.CurrentDir = start;
        }
        window.FolderDialog.PopupCentered(new Vector2I(700, 450));
    }

    private void OnSnapshotFolderChosen(string folder)
    {
        UserSettings(null).SetText(Settings.SnapshotFolderKey, folder);
        _snapshot?.ShowFolder(folder);
        if (_snapshotSaveAfterFolder)
        {
            _snapshotSaveAfterFolder = false;
            SaveSnapshot();
        }
    }

    /// <summary>The snapshot command of the development automation: what the window holds and has chosen, the answer to its folder question
    /// (given to the folder dialog's own "folder chosen" signal), and a lower stand-in for the graphics card's limit.</summary>
    public object SnapshotCall(string action, string? folder)
    {
        if (action == "choose_folder" && folder is not null && _snapshot is { } window && window.FolderDialog.Visible)
        {
            window.FolderDialog.Hide();
            window.FolderDialog.EmitSignal(FileDialog.SignalName.DirSelected, folder);
        }
        if (action == "limit_max_side")
            _snapshotMaxSideForTest = int.TryParse(folder, out int limit) && limit > 0 ? limit : null;
        var drawing = GetWindow().Size;
        return new
        {
            open = SnapshotOpen, busy = _snapshotBusy, has_shot = _snapshotShot is not null,
            width = _snapshotShot?.GetWidth() ?? 0, height = _snapshotShot?.GetHeight() ?? 0,
            shot_bytes = (long)(_snapshotShot?.GetDataSize() ?? 0), preview = _snapshot?.HasPreview ?? false,
            asking_folder = _snapshot?.FolderDialog.Visible ?? false, saved = _snapshotSaved, status = _snapshot?.StatusText ?? "",
            size_index = _snapshot?.SizeChoice.Selected ?? -1, format_index = _snapshot?.FormatChoice.Selected ?? -1,
            show_interface = _snapshot?.ShowInterface.ButtonPressed ?? false,
            window_width = drawing.X, window_height = drawing.Y, max_side = SnapshotMaxSide(),
            private_before = _snapshotPrivateBefore, private_after = _snapshotPrivateAfter, video_before = _snapshotVideoBefore,
            video_peak = _snapshotVideoPeak, private_now = SnapshotPrivateBytes(), video_now = SnapshotVideoBytes(),
        };
    }
}
