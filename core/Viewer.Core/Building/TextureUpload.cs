// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Building;

/// <summary>
/// The size rules for a texture upload, written from the official viewer's behaviour (llimage.cpp biasedDimToPowerOfTwo,
/// llfloaterimagepreview.cpp, llviewertexturelist.cpp createUploadFile; read as behaviour, no code copied): a side goes to a power
/// of two, rounding down unless it is more than 1.75 times the power below; nothing is larger than the upload maximum; an image larger
/// than the preview's limit is first shrunk with its proportions kept.
/// </summary>
public static class UploadImageRules
{
    /// <summary>The smallest side an image is scaled to.</summary>
    public const int MinDimension = 4;

    /// <summary>The largest side an uploaded texture has.</summary>
    public const int MaxDimension = 2048;

    /// <summary>The largest side the upload window keeps before scaling (the official max_texture_dimension settings' default).</summary>
    public const int PreviewMaxDimension = 1024;

    /// <summary>An image wider or taller than this is refused outright (the official preview's load-area limit).</summary>
    public const int MaxSourceDimension = 8096;

    /// <summary>Above this ratio of a side to the power of two below it, the side goes up to the next power of two.</summary>
    public const float RoundUpThreshold = 1.75f;

    /// <summary>The image files the single upload offers (the official picker's image filter).</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".tga", ".bmp"];

    /// <summary>A side's size as a power of two no larger than <paramref name="max"/>, biased towards rounding down.</summary>
    public static int BiasedDim(int current, int max)
    {
        // halve from the maximum down to the power of two at or below the side (never below the smallest side), then go up only
        // when the side is well past it
        int larger = max, smaller = max;
        while (smaller > current && smaller > MinDimension)
        {
            larger = smaller;
            smaller >>= 1;
        }
        return (float)current / smaller > RoundUpThreshold ? larger : smaller;
    }

    /// <summary>The largest power of two not above <paramref name="current"/>, kept within <see cref="MinDimension"/> and <paramref name="max"/>.</summary>
    public static int ContractDim(int current, int max)
    {
        int d = max;
        while (d > current && d > MinDimension)
            d >>= 1;
        return d;
    }

    /// <summary>The size an image of <paramref name="width"/> by <paramref name="height"/> arrives at.</summary>
    public static (int Width, int Height) TargetSize(int width, int height) => TargetSize(width, height, allow2K: false);

    /// <summary>The size an image arrives at when the window allows 2048 on a side.</summary>
    public static (int Width, int Height) TargetSize(int width, int height, bool allow2K)
    {
        int limit = allow2K ? MaxDimension : PreviewMaxDimension;
        if (width > limit || height > limit)
        {
            // shrink to fit the limit with the proportions kept, each side to the power of two at or below it
            double scale = Math.Min((double)limit / width, (double)limit / height);
            width = ContractDim(Math.Clamp((int)Math.Round(width * scale), MinDimension, limit), limit);
            height = ContractDim(Math.Clamp((int)Math.Round(height * scale), MinDimension, limit), limit);
        }
        return (BiasedDim(width, MaxDimension), BiasedDim(height, MaxDimension));
    }

    /// <summary>Whether a file's name ends in an extension the upload takes.</summary>
    public static bool IsSupportedFile(string path)
    {
        string name = FileName(path);
        int dot = name.LastIndexOf('.');
        return dot > 0 && Extensions.Contains(name[dot..].ToLowerInvariant());
    }

    /// <summary>The name an upload is given by default: the file's name without its folder and extension.</summary>
    public static string DefaultName(string path)
    {
        string name = FileName(path);
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
}

/// <summary>An image read from disk for upload: its pixels (RGBA, 8 bits each) or why it could not be read.</summary>
public sealed record UploadSource(string Path, int Width, int Height, byte[]? Rgba, string? ReadError = null);

/// <summary>Why an image cannot be uploaded (keys of the string table), or none.</summary>
public static class UploadRefusals
{
    public const string Format = "upload.refused.format", Unreadable = "upload.refused.unreadable", TooLarge = "upload.refused.too_large",
        TooSmall = "upload.refused.too_small";
}

/// <summary>One image checked for upload: the size it arrives at, or why it cannot go.</summary>
public sealed record UploadCheck(UploadSource Source, int TargetWidth, int TargetHeight, string? Refusal)
{
    public bool Ok => Refusal is null;
}

public static class UploadChecks
{
    /// <summary>Checks each image on its own: one that cannot go does not stop the others.</summary>
    public static IReadOnlyList<UploadCheck> Check(IEnumerable<UploadSource> sources, bool allow2K = false) => sources.Select(s => CheckOne(s, allow2K)).ToArray();

    private static UploadCheck CheckOne(UploadSource s, bool allow2K)
    {
        if (!UploadImageRules.IsSupportedFile(s.Path))
            return new UploadCheck(s, 0, 0, UploadRefusals.Format);
        if (s.ReadError is not null || s.Rgba is null || s.Width <= 0 || s.Height <= 0)
            return new UploadCheck(s, 0, 0, UploadRefusals.Unreadable);
        if (s.Width > UploadImageRules.MaxSourceDimension || s.Height > UploadImageRules.MaxSourceDimension)
            return new UploadCheck(s, 0, 0, UploadRefusals.TooLarge);
        if (s.Rgba.Length < s.Width * s.Height * 4)
            return new UploadCheck(s, 0, 0, UploadRefusals.Unreadable);
        var (w, h) = UploadImageRules.TargetSize(s.Width, s.Height, allow2K);
        return new UploadCheck(s, w, h, null);
    }

    /// <summary>The pixels at the size the image arrives at (an area average going down, linear between pixels going up).</summary>
    public static byte[] Scale(byte[] rgba, int width, int height, int toWidth, int toHeight)
    {
        if (width == toWidth && height == toHeight)
            return (byte[])rgba.Clone();
        var output = new byte[toWidth * toHeight * 4];
        double sx = (double)width / toWidth, sy = (double)height / toHeight;
        Span<double> sum = stackalloc double[4];
        for (int y = 0; y < toHeight; y++)
        {
            double y0 = y * sy, y1 = (y + 1) * sy;
            for (int x = 0; x < toWidth; x++)
            {
                double x0 = x * sx, x1 = (x + 1) * sx;
                int o = (y * toWidth + x) * 4;
                if (sx >= 1 && sy >= 1)
                {
                    // the average of the source area the target pixel covers, each source pixel weighted by its overlap
                    sum.Clear();
                    double weight = 0;
                    for (int yy = (int)y0; yy < Math.Min(height, (int)Math.Ceiling(y1)); yy++)
                    {
                        double wy = Math.Min(yy + 1, y1) - Math.Max(yy, y0);
                        for (int xx = (int)x0; xx < Math.Min(width, (int)Math.Ceiling(x1)); xx++)
                        {
                            double wgt = wy * (Math.Min(xx + 1, x1) - Math.Max(xx, x0));
                            int i = (yy * width + xx) * 4;
                            for (int c = 0; c < 4; c++) sum[c] += rgba[i + c] * wgt;
                            weight += wgt;
                        }
                    }
                    for (int c = 0; c < 4; c++) output[o + c] = (byte)Math.Clamp(Math.Round(sum[c] / weight), 0, 255);
                }
                else
                {
                    // linear between the nearest source pixels (one side may still shrink: its centre is used)
                    double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, width - 1), fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, height - 1);
                    int ax = (int)fx, ay = (int)fy, bx = Math.Min(ax + 1, width - 1), by = Math.Min(ay + 1, height - 1);
                    double tx = fx - ax, ty = fy - ay;
                    for (int c = 0; c < 4; c++)
                    {
                        double top = rgba[(ay * width + ax) * 4 + c] * (1 - tx) + rgba[(ay * width + bx) * 4 + c] * tx;
                        double bottom = rgba[(by * width + ax) * 4 + c] * (1 - tx) + rgba[(by * width + bx) * 4 + c] * tx;
                        output[o + c] = (byte)Math.Clamp(Math.Round(top * (1 - ty) + bottom * ty), 0, 255);
                    }
                }
            }
        }
        return output;
    }

    /// <summary>Whether any pixel is not fully opaque (the alpha is then encoded too).</summary>
    public static bool HasAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255)
                return true;
        return false;
    }
}

/// <summary>What the upload costs, from the price the grid states for one texture.</summary>
public static class UploadCost
{
    public static int Total(int perItem, int count) => Math.Max(0, perItem) * Math.Max(0, count);

    /// <summary>The price of one image: the account benefits' price, else the grid's answer.</summary>
    public static int? PerItem(int? benefitPrice, int? answerPrice) => benefitPrice ?? answerPrice;

    /// <summary>The Upload button's label key and the amount it names (none when the grid does not charge).</summary>
    public static (string Key, int? Amount) Button(int? perItem, int count) => perItem switch
    {
        null => ("upload.button.checking", null),
        > 0 => ("upload.button.cost", Total(perItem.Value, count)),
        _ => ("upload.button", null),
    };
}

/// <summary>What the first step of an upload asks for: the item's name and description, the folder it goes to, the cost the viewer showed.</summary>
public sealed record UploadRequest(string Name, string Description, Guid FolderId, int ExpectedCost);

/// <summary>The grid's answer to the first step: where to send the bytes and what it will charge, or what went wrong.</summary>
public sealed record UploadTicket(Uri? Uploader, int Price, string? Error)
{
    public bool Ok => Uploader is not null && Error is null;
}

/// <summary>The grid's answer to the second step: the new asset and inventory item, or what went wrong.</summary>
public sealed record UploadDone(Guid AssetId, Guid ItemId, string? Error)
{
    public bool Ok => Error is null && ItemId != Guid.Empty;
}

/// <summary>The upload prices an account's benefits state (the login reply's account_level_benefits): the texture price and the large
/// texture prices; <see cref="TextureCost"/> is null when the grid sent no benefits.</summary>
public sealed record UploadBenefits(int? TextureCost, IReadOnlyList<int>? LargeTextureCosts)
{
    public static UploadBenefits None { get; } = new(null, null);

    /// <summary>The price of one image at its arriving size, or null to use the grid's answer.</summary>
    public int? PriceFor(int width, int height) => TexturePrice.For(width, height, TextureCost, LargeTextureCosts);

    public bool Offers2K => TexturePrice.Offers2K(TextureCost, LargeTextureCosts);
}

/// <summary>The grid's two-step texture upload (the NewFileAgentInventory capability, then its uploader).</summary>
public interface ITextureUploadService
{
    /// <summary>The account's upload prices from the login reply's benefits; none when the grid sent none (OpenSim).</summary>
    UploadBenefits Benefits => UploadBenefits.None;

    Task<UploadTicket> RequestAsync(UploadRequest request, CancellationToken ct);
    Task<UploadDone> SendAsync(Uri uploader, byte[] data, CancellationToken ct);
}

/// <summary>One image of a batch: its name and description, and how its bytes are made (encoded when its turn comes).</summary>
public sealed record UploadJob(string Name, string Description, Func<byte[]> Encode);

public enum UploadItemState { Waiting, Encoding, Sending, Done, Failed }

public sealed record UploadItemStatus(UploadItemState State, string? Error = null, Guid ItemId = default);

/// <summary>
/// One upload of one or several images. The price is asked first (the first step for the first image, whose answer states it) and shown;
/// nothing is sent until <see cref="RunAsync"/>. The images then go one after the other, because the grid serves one upload of an agent
/// at a time; one that fails is marked and the rest go on.
/// </summary>
public sealed class UploadBatch
{
    /// <summary>How long the first image's answer is used for its own upload; older, it is asked for again (the grid's uploader lasts two minutes).</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(90);

    private readonly ITextureUploadService _service;
    private readonly Guid _folder;
    private readonly Func<DateTimeOffset> _clock;
    private readonly UploadItemStatus[] _states;
    private readonly object _gate = new();
    private (UploadTicket Ticket, DateTimeOffset At)? _first;

    public UploadBatch(ITextureUploadService service, Guid folderId, IReadOnlyList<UploadJob> jobs, Func<DateTimeOffset>? clock = null)
    {
        _service = service;
        _folder = folderId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Jobs = jobs;
        _states = jobs.Select(_ => new UploadItemStatus(UploadItemState.Waiting)).ToArray();
    }

    public IReadOnlyList<UploadJob> Jobs { get; }

    public IReadOnlyList<UploadItemStatus> States { get { lock (_gate) return _states.ToArray(); } }

    /// <summary>The price of one texture once the grid has stated it; null before.</summary>
    public int? PricePerItem { get; private set; }

    /// <summary>Why the price could not be read (the first step was refused), or null.</summary>
    public string? PriceError { get; private set; }

    public int? Total => PricePerItem is { } p ? UploadCost.Total(p, Jobs.Count) : null;

    /// <summary>Raised whenever an item's state or the price changes (from the thread the work runs on).</summary>
    public event Action? Changed;

    private void Set(int i, UploadItemStatus s)
    {
        lock (_gate) _states[i] = s;
        Changed?.Invoke();
    }

    private UploadRequest RequestFor(UploadJob job, int cost) => new(job.Name, job.Description, _folder, cost);

    public async Task CheckPriceAsync(CancellationToken ct)
    {
        if (Jobs.Count == 0)
            return;
        UploadTicket ticket;
        try { ticket = await _service.RequestAsync(RequestFor(Jobs[0], 0), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { ticket = new UploadTicket(null, 0, ex.GetType().Name); }
        if (ticket.Ok)
        {
            _first = (ticket, _clock());
            PricePerItem = Math.Max(0, ticket.Price);
            PriceError = null;
        }
        else
            PriceError = ticket.Error ?? "refused";
        Changed?.Invoke();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (PricePerItem is not { } price)
            return;   // nothing goes before the price is known and shown
        for (int i = 0; i < Jobs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var job = Jobs[i];
            try
            {
                Set(i, new UploadItemStatus(UploadItemState.Encoding));
                byte[] data = await Task.Run(job.Encode, ct).ConfigureAwait(false);
                Set(i, new UploadItemStatus(UploadItemState.Sending));
                UploadTicket ticket;
                if (i == 0 && _first is { } first && _clock() - first.At <= TicketLifetime)
                    ticket = first.Ticket;
                else
                    ticket = await _service.RequestAsync(RequestFor(job, price), ct).ConfigureAwait(false);
                _first = null;
                if (!ticket.Ok)
                {
                    Set(i, new UploadItemStatus(UploadItemState.Failed, ticket.Error ?? "refused"));
                    continue;
                }
                if (ticket.Price != price)
                {
                    // the user agreed to the price shown; a different one is not paid without asking again
                    Set(i, new UploadItemStatus(UploadItemState.Failed, "price_changed"));
                    continue;
                }
                var done = await _service.SendAsync(ticket.Uploader!, data, ct).ConfigureAwait(false);
                Set(i, done.Ok ? new UploadItemStatus(UploadItemState.Done, null, done.ItemId) : new UploadItemStatus(UploadItemState.Failed, done.Error ?? "failed"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Set(i, new UploadItemStatus(UploadItemState.Failed, ex.GetType().Name));
            }
        }
    }
}
