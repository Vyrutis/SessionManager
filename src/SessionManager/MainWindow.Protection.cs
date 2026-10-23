using System.Windows;

namespace SessionManager;

/// <summary>The master password for the saved files (sessions, backups, remembered server keys).</summary>
public partial class MainWindow
{
    private void ChangeMasterPassword()
    {
        string? password;
        if (DataProtector.Mode == ProtectionMode.Password)
        {
            var choice = MessageBox.Show(this, "A master password is set.\n\nYes: change it.\nNo: remove it (your sessions stay encrypted for your Windows account).",
                "Master password", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel) return;
            password = choice == MessageBoxResult.Yes ? Dialogs.PromptNewPassphrase(this, "Master password", "Choose the new master password:") : null;
            if (choice == MessageBoxResult.Yes && password == null) return;
        }
        else
        {
            password = Dialogs.PromptNewPassphrase(this, "Master password",
                "Choose a master password. You will be asked for it every time Session Manager starts.\n\nIf you forget it, the sessions cannot be recovered. Keep an exported copy.");
            if (password == null) return;
        }
        try
        {
            _store.ChangeProtection(password);
            MessageBox.Show(this, password == null ? "The master password is removed." : "The master password is set.", "Master password", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not change the protection: " + ex.Message, "Master password", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
