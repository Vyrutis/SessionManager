using System.IO;
using SessionManager;

/// <summary>Release tag parsing, version comparison and reading the GitHub "latest release" response.</summary>
static class UpdateCheckerTests
{
    public static void Run(Action<string, bool, string> check)
    {
        foreach (var (latest, current, expected) in new[] { ("1.3.4", "1.3.3", true), ("1.3.3", "1.3.3", false), ("1.3.2", "1.3.3", false), ("1.10.0", "1.9.9", true), ("2.0", "1.9.9", true) })
            check($"update: {latest} vs {current} is {(expected ? "newer" : "not newer")}", UpdateChecker.IsNewer(Version.Parse(latest), Version.Parse(current)) == expected, "");

        foreach (var (tag, expected) in new[] { ("v1.3.3", "1.3.3"), ("1.4", "1.4.0"), ("v2.0.1-beta", "2.0.1") })
            check($"update: tag \"{tag}\" reads as {expected}", UpdateChecker.TryParseVersion(tag, out var v) && v.ToString(3) == expected, "");
        foreach (var bad in new[] { "", "latest" })
            check($"update: tag \"{bad}\" is rejected", !UpdateChecker.TryParseVersion(bad, out _), "");

        const string json = """
            {"tag_name":"v1.4.0","body":"Notes","html_url":"https://github.com/x/y/releases/tag/v1.4.0",
             "assets":[{"name":"src.zip","browser_download_url":"https://github.com/x/y/releases/download/v1.4.0/src.zip"},
                       {"name":"SessionManager-1.4.0.msi","browser_download_url":"https://github.com/x/y/releases/download/v1.4.0/SessionManager-1.4.0.msi"}]}
            """;
        var r = UpdateChecker.Parse(json);
        check("update: release picks the .msi asset", r != null && r.Version.ToString(3) == "1.4.0" && r.MsiName == "SessionManager-1.4.0.msi"
            && r.MsiUrl == "https://github.com/x/y/releases/download/v1.4.0/SessionManager-1.4.0.msi" && r.Notes == "Notes", "");
        check("update: release without assets has no installer", UpdateChecker.Parse("""{"tag_name":"v1.4.0"}""")?.MsiUrl == null, "");
        check("update: a \"Not Found\" reply gives no release", UpdateChecker.Parse("""{"message":"Not Found"}""") == null, "");
        check("update: an installer from another host is ignored", UpdateChecker.Parse("""{"tag_name":"v1.4.0","assets":[{"name":"a.msi","browser_download_url":"https://evil.example/a.msi"}]}""")?.MsiUrl == null, "");
        check("update: a bad release page falls back to the repo", UpdateChecker.Parse("""{"tag_name":"v1.4.0","html_url":"file:///c:/x.exe"}""")?.PageUrl == "https://github.com/Vyrutis/SessionManager/releases/latest", "");
        check("update: the Lithuanian installer is not picked for updates", UpdateChecker.Parse("""{"tag_name":"v1.4.0","assets":[{"name":"SessionManager-1.4.0-lt.msi","browser_download_url":"https://github.com/x/y/releases/download/v1.4.0/SessionManager-1.4.0-lt.msi"},{"name":"SessionManager-1.4.0.msi","browser_download_url":"https://github.com/x/y/releases/download/v1.4.0/SessionManager-1.4.0.msi"}]}""")?.MsiName == "SessionManager-1.4.0.msi", "");
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var withDigest = UpdateChecker.Parse($$"""{"tag_name":"v1.4.0","assets":[{"name":"a.msi","browser_download_url":"https://github.com/x/y/a.msi","digest":"sha256:{{hash.ToUpperInvariant()}}"}]}""");
        check("update: the published SHA-256 is read", withDigest?.MsiSha256 == hash && withDigest.CanInstall, "");
        check("update: no published checksum means no install from the app", UpdateChecker.Parse("""{"tag_name":"v1.4.0","assets":[{"name":"a.msi","browser_download_url":"https://github.com/x/y/a.msi"}]}""") is { CanInstall: false, MsiUrl: not null }, "");
        foreach (var bad in new[] { "sha1:abc", "sha256:xyz", "sha256:" + hash[1..], "", "md5:" + hash })
            check($"update: digest \"{bad}\" is rejected", UpdateChecker.ParseDigest(bad) == null, "");
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "installer");
        var real = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("installer"))).ToLowerInvariant();
        check("update: a file with the right checksum passes", UpdateChecker.Verify(file, real), "");
        check("update: a changed file fails", !UpdateChecker.Verify(file, hash), "");
        File.Delete(file);
        check("update: automatic checks are off by default", !new AppData().CheckForUpdates, "");
        check("update: a non-https asset is ignored", UpdateChecker.Parse("""{"tag_name":"v1.4.0","assets":[{"name":"a.msi","browser_download_url":"http://x/a.msi"}]}""")?.MsiUrl == null, "");
    }
}
