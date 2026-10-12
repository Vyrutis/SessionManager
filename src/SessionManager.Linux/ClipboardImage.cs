using System.Diagnostics;
using Avalonia.Input.Platform;

namespace SessionManager;

/// <summary>Gets a screenshot (PNG) or copied image files from the clipboard, for the screenshot scan.</summary>
public static class ClipboardImage
{
    private static readonly string[] ImageFormats = ["image/png", "PNG", "image/bmp", "image/jpeg"];

    /// <summary>PNG bytes of a copied picture, or the path of a copied image/PDF file; both null when there is neither.</summary>
    public static async Task<(byte[]? Png, string? File)> GetAsync(IClipboard? clipboard)
    {
        if (clipboard != null)
        {
            try
            {
                var formats = await clipboard.GetFormatsAsync();
                foreach (var f in ImageFormats.Where(formats.Contains))
                    if (await clipboard.GetDataAsync(f) is byte[] { Length: > 8 } bytes) return (bytes, null);

                // Files copied in a file manager arrive as a list of URIs.
                if (formats.Contains("text/uri-list") && await clipboard.GetDataAsync("text/uri-list") is { } uris)
                {
                    var text = uris is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : uris.ToString() ?? "";
                    var file = text.Split('\n').Select(l => l.Trim())
                        .Where(l => l.StartsWith("file://", StringComparison.Ordinal))
                        .Select(l => new Uri(l).LocalPath)
                        .FirstOrDefault(IsScannable);
                    if (file != null) return (null, file);
                }
            }
            catch { }
        }

        // Fall back to the desktop's own clipboard tools.
        if (!OperatingSystem.IsWindows())
        {
            if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
                && await Read("wl-paste", ["--no-newline", "--type", "image/png"]) is { } wl) return (wl, null);
            if (Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 }
                && await Read("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]) is { } x) return (x, null);
        }
        return (null, null);
    }

    public static bool IsScannable(string path) =>
        File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff" or ".pdf";

    private static async Task<byte[]?> Read(string tool, string[] args)
    {
        if (TesseractOcr.FindTool(tool) is not { } path) return null;
        try
        {
            var psi = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            _ = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(cts.Token);
            await copy;
            var bytes = ms.ToArray();
            // A PNG starts with 0x89 'P' 'N' 'G'.
            return p.ExitCode == 0 && bytes.Length > 8 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G' ? bytes : null;
        }
        catch
        {
            return null;
        }
    }
}
