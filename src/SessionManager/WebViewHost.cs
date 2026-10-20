using System.IO;
using Microsoft.Web.WebView2.Core;

namespace SessionManager;

/// <summary>The one WebView2 browser environment shared by terminals and web sessions (data in %LOCALAPPDATA%\SessionManager\WebView2).</summary>
public static class WebViewHost
{
    private static Task<CoreWebView2Environment>? _environment;

    public static Task<CoreWebView2Environment> Environment =>
        _environment ??= CoreWebView2Environment.CreateAsync(null, DataFolder);

    /// <summary>Cookies and cache; inside the data folder for a separate (test) copy started with SESSIONMANAGER_DATA.</summary>
    public static string DataFolder => System.Environment.GetEnvironmentVariable("SESSIONMANAGER_DATA") is { Length: > 0 }
        ? Path.Combine(SessionStore.DataDir, "WebView2")
        : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SessionManager", "WebView2");
}
