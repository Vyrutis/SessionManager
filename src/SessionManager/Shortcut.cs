using System.IO;

namespace SessionManager;

public static class Shortcut
{
    /// <summary>Creates a desktop .lnk that starts the app with --open {id}. Returns the shortcut path.</summary>
    public static string CreateOnDesktop(Node session)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var safeName = string.Concat(session.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safeName.Length == 0) safeName = "Session";
        var path = Path.Combine(desktop, safeName + ".lnk");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(desktop, $"{safeName} ({i}).lnk");

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the program path.");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = exe;
            lnk.Arguments = $"--open {session.Id}";
            lnk.WorkingDirectory = Path.GetDirectoryName(exe);
            lnk.IconLocation = exe + ",0";
            lnk.Description = $"Open {session.Name} in Session Manager";
            lnk.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        return path;
    }
}
