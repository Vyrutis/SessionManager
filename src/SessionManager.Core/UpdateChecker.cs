using System.Text.Json;

namespace SessionManager;

/// <summary>A newer release on GitHub, with its notes and the installer (.msi) to download.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, string? MsiName, string? MsiUrl);

/// <summary>Looks up the latest GitHub release and compares it with the running version.</summary>
public static class UpdateChecker
{
    private static readonly System.Text.RegularExpressions.Regex LanguageInstaller = new(@"-[a-z]{2}(-[A-Za-z]{2})?\.msi$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public const string Repo = "Vyrutis/SessionManager";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SessionManager-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>The latest release, or null when it can't be read (offline, rate limited, no release yet).</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancel = default)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cancel);
            if (!response.IsSuccessStatusCode) return null;
            return Parse(await response.Content.ReadAsStringAsync(cancel));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads a GitHub "latest release" response; null if it has no usable version tag.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var tag = Str(root, "tag_name");
        if (!TryParseVersion(tag, out var version)) return null;

        string? msiName = null, msiUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                // SessionManager-1.4.0-lt.msi is the Lithuanian installer; updates use the plain one (the app's language is a setting).
                if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && !LanguageInstaller.IsMatch(name) && IsTrustedDownload(url))
                {
                    msiName = name;
                    msiUrl = url;
                    break;
                }
            }
        }
        var page = Str(root, "html_url");
        if (!IsHttps(page, out var pageUri) || !pageUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) page = $"https://github.com/{Repo}/releases/latest";
        return new ReleaseInfo(version, tag, Str(root, "body").Trim(), page, msiName, msiUrl);
    }

    /// <summary>Parses "v1.3.3" or "1.3.3" (an optional suffix like "-beta" is ignored).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return false;
        version = Normalize(v);
        return true;
    }

    /// <summary>True when <paramref name="latest"/> is newer than <paramref name="current"/> (missing parts count as 0).</summary>
    public static bool IsNewer(Version latest, Version current) => Normalize(latest) > Normalize(current);

    private static bool IsHttps(string url, out Uri uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Installers only come from github.com (release assets redirect to githubusercontent.com).</summary>
    private static bool IsTrustedDownload(string url) =>
        IsHttps(url, out var u) && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    /// <summary>Downloads the installer into a temp folder and returns its path.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (release.MsiUrl == null || release.MsiName == null) throw new InvalidOperationException("This release has no installer attached.");
        var dir = Path.Combine(Path.GetTempPath(), "SessionManagerUpdate");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"SessionManager-{release.Version.ToString(3)}.msi");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        cancel = timeout.Token;

        using var response = await Http.GetAsync(release.MsiUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using (var input = await response.Content.ReadAsStreamAsync(cancel))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), cancel);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        return path;
    }
}
