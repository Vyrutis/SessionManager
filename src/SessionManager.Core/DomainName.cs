namespace SessionManager;

/// <summary>Helpers for the Windows domain in front of a user name ("CORP\admin").</summary>
public static class DomainName
{
    /// <summary>The domain without spaces or a trailing backslash; "" = none.</summary>
    public static string Clean(string? domain) => (domain ?? "").Trim().TrimEnd('\\').Trim();

    /// <summary>"CORP" + "admin" → "CORP\admin".</summary>
    public static string Combine(string domain, string user)
    {
        domain = Clean(domain);
        return domain.Length == 0 || user.Length == 0 ? user : $"{domain}\\{user}";
    }

    /// <summary>"CORP\admin" → ("CORP", "admin"); a name without a domain is returned unchanged.</summary>
    public static (string Domain, string User) Split(string? user)
    {
        user ??= "";
        int i = user.IndexOf('\\');
        return i > 0 && i < user.Length - 1 ? (user[..i], user[(i + 1)..]) : ("", user);
    }
}
