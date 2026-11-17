// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Threading.Tasks;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The texture preview, as the official viewer's texture preview window (llpreviewtexture.cpp, behaviour reference only): opening a
/// texture from the inventory (double-click, Enter, Open) shows the image fitted to the window with its size, name and description.
/// The texture is fetched and decoded off the main thread; the image is made on it.
/// </summary>
public partial class Main
{
    private const string TexturePreviewId = "texture_preview";
    private TexturePreviewPanel? _texturePreview;
    private Guid _previewing;

    /// <summary>Opens the preview for an inventory texture (or a link to one). False when the item is not a texture.</summary>
    private bool OpenTexturePreview(Guid itemId)
    {
        if (_inventoryFor is not { IsEnded: false } session || _shell is null || _strings is null)
            return false;
        var item = session.Inventory.Item(itemId);
        if (item?.LinkedId is { } linked && session.Inventory.Item(linked) is { Kind: ItemKind.Texture } target)
            item = target;
        if (item is not { Kind: ItemKind.Texture } texture)
            return false;
        ShowTexturePreview(texture.Id, texture.Name, texture.Description, texture.AssetId);
        return true;
    }

    /// <summary>Opens the preview for a texture inside an object (llpanelobjectinventory.cpp LLTaskTextureBridge::openItem): its image is the
    /// item's asset, which the region lists only to those who may see it; otherwise the preview says it could not be loaded.</summary>
    private void OpenTaskTexturePreview(Viewer.Core.Session.TaskItem item) =>
        ShowTexturePreview(item.ItemId, item.Name, item.Description, item.AssetId == Guid.Empty ? null : item.AssetId);

    /// <summary>Shows a texture in the preview, fetched and decoded off the main thread.</summary>
    private void ShowTexturePreview(Guid shownItem, string name, string description, Guid? assetId)
    {
        if (_shell is null || _strings is null)
            return;
        var texture = (Id: shownItem, Name: name, Description: description, AssetId: assetId);
        if (_texturePreview is null)
        {
            _texturePreview = new TexturePreviewPanel(_strings);
            _shell.AddFloater(new FloaterSpec(TexturePreviewId, "texture.preview.title", new UiSize(420, 480), new UiSize(240, 260)), _texturePreview);
        }
        _previewing = texture.Id;
        _texturePreview.Show(texture.Name, texture.Description);
        _shell.SetFloaterTitle(TexturePreviewId, _strings.Get("texture.preview.title_of", texture.Name));
        _shell.Shell.Floaters.Open(TexturePreviewId);
        Log.Info("texture.preview.open", CoreLog.F("has-asset", texture.AssetId is { } a && a != Guid.Empty));
        if (texture.AssetId is not { } asset || asset == Guid.Empty || _textureTransport is not { } transport)
        {
            _texturePreview.Failed();
            return;
        }
        Guid shown = texture.Id;
        _ = Task.Run(async () =>
        {
            Viewer.Core.Imaging.DecodedTexture? decoded = null;
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(30));
                var fetched = await transport.FetchTextureAsync(asset, cts.Token).ConfigureAwait(false);
                if (fetched.Status == Viewer.Core.Assets.AssetFetchStatus.Ok && fetched.Bytes.Length > 0)
                    decoded = Viewer.Core.Protocol.LmvTexture.Decode(fetched.Bytes.ToArray()).Texture;
            }
            catch (System.Exception ex) when (ex is not System.OutOfMemoryException) { decoded = null; }
            Callable.From(() =>
            {
                if (_previewing != shown || _texturePreview is null)
                    return;   // another texture was opened meanwhile
                if (decoded is null)
                    _texturePreview.Failed();
                else
                    _texturePreview.ShowImage(Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, decoded.Rgba), decoded.FullWidth, decoded.FullHeight);
            }).CallDeferred();
        });
    }

    /// <summary>Opens the inventory's selected row as Enter or a double-click does.</summary>
    internal bool OpenSelectedInventoryRow() => _inventoryPanel?.OpenSelected() ?? false;

    /// <summary>The preview's state for the automation: open, the item shown, the size read, whether the image is drawn (never the name).</summary>
    internal object TexturePreviewState() => new
    {
        open = _shell?.Shell.Floaters.IsOpen(TexturePreviewId) ?? false,
        item = _previewing == Guid.Empty ? null : _previewing.ToString(),
        width = _texturePreview?.ImageWidth ?? 0,
        height = _texturePreview?.ImageHeight ?? 0,
        drawn = _texturePreview?.HasImage ?? false,
        failed = _texturePreview?.HasFailed ?? false,
    };
}

/// <summary>The preview window's content: the image fitted with its proportions kept, then its size, name and description.</summary>
public sealed partial class TexturePreviewPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly TextureRect _image = new() { Name = "image", ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 200) };
    private readonly Label _size = new() { Name = "size" };
    private readonly Label _name = new() { Name = "name", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _description = new() { Name = "description", AutowrapMode = TextServer.AutowrapMode.WordSmart };

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }
    public bool HasImage => _image.Texture is not null;
    public bool HasFailed { get; private set; }

    public TexturePreviewPanel(StringTable strings)
    {
        _strings = strings;
        Name = "texture_preview";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        _image.AccessibilityName = strings.Get("texture.preview.image");
        AddChild(_image);
        _size.ThemeTypeVariation = UiTheme.MutedLabel;
        AddChild(_size);
        AddChild(_name);
        _description.ThemeTypeVariation = UiTheme.MutedLabel;
        AddChild(_description);
    }

    /// <summary>A texture is being fetched: its name and description now, the image when it has arrived.</summary>
    public void Show(string name, string description)
    {
        _image.Texture = null;
        ImageWidth = ImageHeight = 0;
        HasFailed = false;
        _size.Text = _strings.Get("texture.preview.loading");
        _name.Text = _strings.Get("texture.preview.name", name);
        _name.AccessibilityName = _name.Text;
        _description.Text = description.Length > 0 ? _strings.Get("texture.preview.description", description) : _strings.Get("texture.preview.no_description");
        _description.AccessibilityName = _description.Text;
    }

    public void ShowImage(Image image, int fullWidth, int fullHeight)
    {
        _image.Texture = ImageTexture.CreateFromImage(image);
        ImageWidth = fullWidth;
        ImageHeight = fullHeight;
        _size.Text = _strings.Get("texture.preview.size", fullWidth + " × " + fullHeight);
        _size.AccessibilityName = _size.Text;
    }

    public void Failed()
    {
        HasFailed = true;
        _size.Text = _strings.Get("texture.preview.failed");
        _size.AccessibilityName = _size.Text;
    }
}
