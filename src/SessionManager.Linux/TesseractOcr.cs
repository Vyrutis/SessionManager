using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;

namespace SessionManager;

/// <summary>
/// Reads text from pictures and PDFs with Tesseract (and poppler's pdftoppm for PDF pages), on this computer only,
/// then hands the words to <see cref="ScreenshotScan"/> to find hosts, users and passwords.
/// </summary>
public static class TesseractOcr
{
    public static bool HasTesseract => FindTool("tesseract") != null;
    public static bool HasPdfToPpm => FindTool("pdftoppm") != null;

    public static string? FindTool(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (var candidate in OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : [name])
            {
                var p = Path.Combine(dir, candidate);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    public static Task<ScreenshotScan.Result> ScanFileAsync(string path) => Task.Run(() =>
    {
        var work = WorkDir();
        try
        {
            var pages = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? RenderPdf(path, work) : [path];
            var langs = Languages();
            return ScreenshotScan.FromPages(pages.Select(p => Recognize(Prepare(p, work), langs)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    });

    /// <summary>Scans PNG bytes (e.g. a screenshot from the clipboard).</summary>
    public static async Task<ScreenshotScan.Result> ScanImageAsync(byte[] png)
    {
        var work = WorkDir();
        var file = Path.Combine(work, "clipboard.png");
        await File.WriteAllBytesAsync(file, png);
        try { return await ScanFileAsync(file); }
        finally { try { Directory.Delete(work, recursive: true); } catch { } }
    }

    private static string WorkDir()
    {
        var root = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r && Directory.Exists(r) ? r : Path.GetTempPath();
        var dir = Path.Combine(root, "sessionmanager-ocr-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    /// <summary>Small screenshots recognize much better when enlarged.</summary>
    private static string Prepare(string image, string work)
    {
        try
        {
            using var bmp = new Bitmap(image);
            var size = bmp.PixelSize;
            if (Math.Max(size.Width, size.Height) >= 1400) return image;
            using var scaled = bmp.CreateScaledBitmap(new PixelSize(size.Width * 2, size.Height * 2), BitmapInterpolationMode.HighQuality);
            var output = Path.Combine(work, Guid.NewGuid().ToString("N")[..8] + ".png");
            scaled.Save(output);
            return output;
        }
        catch
        {
            return image; // Tesseract reads more formats than we decode; give it the original
        }
    }

    private static List<string> RenderPdf(string pdf, string work)
    {
        var tool = FindTool("pdftoppm") ?? throw new InvalidOperationException("Reading PDFs needs poppler: sudo pacman -S poppler");
        var (code, _, err) = Run(tool, ["-r", "220", "-png", "-l", ScreenshotScan.MaxPdfPages.ToString(), pdf, Path.Combine(work, "page")]);
        if (code != 0)
        {
            if (err.Contains("password", StringComparison.OrdinalIgnoreCase) || err.Contains("Incorrect", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This PDF is password protected. Open it, print it to a new PDF without a password, and scan that.");
            throw new InvalidOperationException("The PDF could not be read: " + err.Trim());
        }
        return Directory.GetFiles(work, "page*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static readonly Dictionary<string, string> LangCodes = new()
    {
        ["de"] = "deu", ["fr"] = "fra", ["es"] = "spa", ["it"] = "ita", ["pt"] = "por", ["nl"] = "nld", ["pl"] = "pol",
        ["ru"] = "rus", ["lt"] = "lit", ["lv"] = "lav", ["et"] = "est", ["cs"] = "ces", ["sv"] = "swe", ["fi"] = "fin",
        ["da"] = "dan", ["nb"] = "nor", ["uk"] = "ukr", ["tr"] = "tur", ["hu"] = "hun", ["ro"] = "ron",
    };

    /// <summary>English plus the desktop language when its Tesseract data is installed.</summary>
    private static string Languages()
    {
        var tool = FindTool("tesseract") ?? throw new InvalidOperationException("Text recognition needs Tesseract: sudo pacman -S tesseract tesseract-data-eng");
        var (_, output, err) = Run(tool, ["--list-langs"]);
        var installed = (output + "\n" + err).Split('\n').Select(l => l.Trim()).Where(l => l.Length is >= 3 and <= 12 && !l.Contains(' ')).ToHashSet();
        var langs = new List<string>();
        if (installed.Contains("eng")) langs.Add("eng");
        var lang = (Environment.GetEnvironmentVariable("LANG") ?? CultureInfo.CurrentUICulture.Name).Split('_', '-', '.')[0].ToLowerInvariant();
        if (LangCodes.TryGetValue(lang, out var code) && installed.Contains(code)) langs.Add(code);
        if (langs.Count == 0)
        {
            var any = installed.FirstOrDefault(l => l != "osd");
            if (any == null) throw new InvalidOperationException("Tesseract has no language data. Install it with: sudo pacman -S tesseract-data-eng");
            langs.Add(any);
        }
        return string.Join('+', langs);
    }

    /// <summary>Runs Tesseract with word boxes (TSV) in "sparse text" mode, which suits tables and screenshots.</summary>
    private static List<ScreenshotScan.Row> Recognize(string image, string langs)
    {
        var tool = FindTool("tesseract")!;
        var (code, tsv, err) = Run(tool, [image, "stdout", "-l", langs, "--psm", "11", "tsv"]);
        if (code != 0) throw new InvalidOperationException("Tesseract could not read the picture: " + err.Trim());
        return ScreenshotScan.ToRows(ParseTsv(tsv));
    }

    /// <summary>Word lines (level 5) of Tesseract's TSV output.</summary>
    public static List<ScreenshotScan.Word> ParseTsv(string tsv)
    {
        var words = new List<ScreenshotScan.Word>();
        foreach (var line in tsv.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 12 || f[0] != "5") continue;
            var text = f[11].Trim();
            if (text.Length == 0) continue;
            if (!double.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf) || conf < 0) continue;
            if (int.TryParse(f[6], out var x) && int.TryParse(f[7], out var y) && int.TryParse(f[8], out var w) && int.TryParse(f[9], out var h))
                words.Add(new ScreenshotScan.Word(text, x, y, w, h));
        }
        return words;
    }

    private static (int Code, string Output, string Error) Run(string tool, string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(tool)} did not start.");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"{Path.GetFileName(tool)} took too long.");
        }
        return (p.ExitCode, output.Result, error.Result);
    }
}
