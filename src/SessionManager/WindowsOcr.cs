using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinImaging = Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace SessionManager;

/// <summary>Windows' built-in OCR (images and PDF pages) feeding <see cref="ScreenshotScan"/>.</summary>
public static class WindowsOcr
{
    public const int MaxPdfPages = ScreenshotScan.MaxPdfPages;

    // ───────────── Inputs ─────────────

    public static Task<ScreenshotScan.Result> ScanImageAsync(BitmapSource image) => ScanAsync(async () => [await ToSoftwareBitmap(image)]);

    public static Task<ScreenshotScan.Result> ScanFileAsync(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? ScanAsync(() => RenderPdf(path))
            : ScanImageAsync(LoadFile(path));

    private static async Task<ScreenshotScan.Result> ScanAsync(Func<Task<List<WinImaging.SoftwareBitmap>>> pages)
    {
        var engine = CreateEngine();
        var pages2 = new List<List<ScreenshotScan.Row>>();
        foreach (var page in await pages())
            using (page)
                pages2.Add(ToRows(await engine.RecognizeAsync(page)));
        return ScreenshotScan.FromPages(pages2);
    }

    public static BitmapSource LoadFile(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(Path.GetFullPath(path));
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static WinOcr.OcrEngine CreateEngine() =>
        WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
        ?? WinOcr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
        ?? throw new InvalidOperationException("Windows text recognition (OCR) is not available. Add a language with OCR support in Settings > Time & language > Language & region.");

    private static async Task<WinImaging.SoftwareBitmap> ToSoftwareBitmap(BitmapSource image)
    {
        // Small text recognizes much better when enlarged; stay under the engine's size limit.
        double max = WinOcr.OcrEngine.MaxImageDimension;
        double longest = Math.Max(image.PixelWidth, image.PixelHeight);
        double scale = longest < 1400 ? 2 : 1;
        if (longest * scale > max) scale = max / longest;
        BitmapSource src = Math.Abs(scale - 1) > 0.01 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return await Decode(ms.AsRandomAccessStream());
    }

    private static async Task<WinImaging.SoftwareBitmap> Decode(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        using (stream)
        {
            var decoder = await WinImaging.BitmapDecoder.CreateAsync(stream);
            return await decoder.GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied);
        }
    }

    /// <summary>Renders each PDF page at about 220 dpi (scanned and text PDFs alike) for the OCR.</summary>
    private static async Task<List<WinImaging.SoftwareBitmap>> RenderPdf(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        Windows.Data.Pdf.PdfDocument doc;
        try { doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B)) // wrong password
        { throw new InvalidOperationException("This PDF is password protected. Open it, print it to a new PDF without a password, and scan that.", ex); }

        var pages = new List<WinImaging.SoftwareBitmap>();
        uint count = Math.Min(doc.PageCount, MaxPdfPages);
        for (uint i = 0; i < count; i++)
        {
            using var page = doc.GetPage(i);
            double w = page.Size.Width, h = page.Size.Height; // in 1/96 inch
            double scale = Math.Min(220.0 / 96, WinOcr.OcrEngine.MaxImageDimension / Math.Max(w, h));
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)(w * scale), DestinationHeight = (uint)(h * scale),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            });
            stream.Seek(0);
            pages.Add(await Decode(stream));
        }
        return pages;
    }

    private static List<ScreenshotScan.Row> ToRows(WinOcr.OcrResult ocr) =>
        ScreenshotScan.ToRows(ocr.Lines.SelectMany(l => l.Words)
            .Select(w => new ScreenshotScan.Word(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
            .ToList());
}
