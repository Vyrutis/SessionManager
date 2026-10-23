using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace SessionManager;

/// <summary>Update check: compares the latest GitHub release with the running version and installs the attached .msi.</summary>
public partial class MainWindow
{
    private bool _updating;

    private static Version CurrentVersion =>
        UpdateChecker.TryParseVersion(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var v)
            ? v : Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    private void InitUpdates()
    {
        if (!_store.Data.CheckForUpdates) return;
        Loaded += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            if (DateTime.UtcNow - _store.Data.LastUpdateCheck < TimeSpan.FromDays(1)) return;
            await CheckForUpdatesAsync(manual: false);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var release = await UpdateChecker.GetLatestAsync();
            if (!manual && release != null)
            {
                _store.Data.LastUpdateCheck = DateTime.UtcNow;
                Save();
            }
            var current = CurrentVersion;
            if (release == null)
            {
                if (manual) MessageBox.Show(this, "Could not reach GitHub to look for updates.", "Session Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!UpdateChecker.IsNewer(release.Version, current))
            {
                if (manual) MessageBox.Show(this, $"You have the latest version ({current.ToString(3)}).", "Session Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!Dialogs.PromptUpdate(this, release, current)) return;
            if (!release.CanInstall)
            {
                Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
                return;
            }
            await InstallUpdateAsync(release);
        }
        catch (Exception ex)
        {
            if (manual) MessageBox.Show(this, "The update check failed: " + ex.Message, "Session Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _updating = false; }
    }

    private async Task InstallUpdateAsync(ReleaseInfo release)
    {
        var oldTitle = Title;
        string msi;
        try
        {
            var progress = new Progress<double>(p => Title = $"{oldTitle} – downloading update {p:P0}");
            msi = await UpdateChecker.DownloadAsync(release, progress);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The update could not be downloaded: " + ex.Message, "Session Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { Title = oldTitle; }

        // Close the window first (it asks about open connections and can be cancelled); the installer starts only once the app is gone.
        void Install(object? s, EventArgs e)
        {
            // Checked again right before it runs, in case the file was replaced while the app was closing.
            try
            {
                if (release.MsiSha256 == null || !UpdateChecker.Verify(msi, release.MsiSha256))
                    throw new InvalidDataException("The installer no longer matches its checksum.");
                Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /passive /norestart") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("The update was not installed: " + ex.Message + "\n\nDownload it from " + release.PageUrl, "Session Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        Closed += Install;
        Close();
        if (!_closeAnimationDone) Closed -= Install; // cancelled
    }
}
