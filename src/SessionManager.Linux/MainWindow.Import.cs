using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace SessionManager;

public sealed partial class MainWindow
{
    private bool _scanning;

    private MenuFlyout BuildImportMenu()
    {
        var flyout = new MenuFlyout();
        void Item(string header, Action action, string? gesture = null, Func<bool>? enabled = null)
        {
            var m = new MenuItem { Header = header };
            if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
            m.Click += (_, _) => action();
            if (enabled != null) flyout.Opening += (_, _) => m.IsEnabled = enabled();
            flyout.Items.Add(m);
        }
        void Sep() => flyout.Items.Add(new Separator());

        Item("Export all sessions…", () => _ = ExportList(null));
        Item("Import a session list or session file…", () => _ = ImportPicked(null));
        Sep();
        Item("Import from Remmina", () => _ = Review(() => ConnectionImport.FromRemminaFiles(ConnectionImport.RemminaFiles()), "Remmina"),
            enabled: ConnectionImport.HasRemmina);
        Item("Import from PuTTY", () => _ = Review(ConnectionImport.FromPutty, "PuTTY"), enabled: ConnectionImport.HasPutty);
        Item("Import from OpenSSH config (~/.ssh/config)", () => _ = Review(() => ConnectionImport.FromSshConfig(ConnectionImport.SshConfigPath), "SSH config"),
            enabled: () => File.Exists(ConnectionImport.SshConfigPath));
        Item("Find .rdp files in my folders…", () => _ = FindRdpFiles());
        Item("Import from mRemoteNG, MobaXterm, Remmina, VNC viewer (.vnc) or CSV file…", () => _ = ImportConnectionFile());
        Sep();
        Item("Scan a picture or PDF…", () => _ = ScanFilePicked());
        Item("Scan a screenshot from the clipboard", () => _ = ScanClipboard(), "Ctrl+Shift+V");
        return flyout;
    }

    private IStorageProvider Storage => StorageProvider;

    private static FilePickerFileType Type(string name, params string[] patterns) => new(name) { Patterns = patterns };

    // ───────────── Files (drag and drop, command line, pickers) ─────────────

    /// <summary>Imports any supported file: session lists, sessions, .rdp, other tools' exports, pictures and PDFs.</summary>
    private async Task ImportPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == SessionList.Extension) await ImportList(path);
        else if (ext is ".smsession" or ".rdp") await ImportSessionFiles([path], null);
        else if (ClipboardImage.IsScannable(path)) await ScanAsync(() => TesseractOcr.ScanFileAsync(path), Path.GetFileNameWithoutExtension(path));
        else await ReviewFile(path);
    }

    private async Task ImportPicked(Node? folder)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import sessions",
            AllowMultiple = true,
            FileTypeFilter = [Type("Session files", "*.smsession", "*.smlist", "*.rdp"), Type("All files", "*")],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        foreach (var list in paths.Where(p => p.EndsWith(SessionList.Extension, StringComparison.OrdinalIgnoreCase))) await ImportList(list);
        var single = paths.Where(p => !p.EndsWith(SessionList.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (single.Count > 0) await ImportSessionFiles(single, folder);
    }

    private async Task ImportSessionFiles(List<string> files, Node? folder)
    {
        Node? last = null;
        foreach (var file in files)
        {
            try
            {
                Node node;
                string? password = null;
                if (Path.GetExtension(file).Equals(".rdp", StringComparison.OrdinalIgnoreCase))
                {
                    var (name, settings) = SessionFile.ImportRdp(file, _store.PresetFor(Protocol.Rdp));
                    node = new Node { Kind = NodeKind.Session, Name = name, Protocol = Protocol.Rdp, Settings = settings };
                }
                else
                {
                    var model = SessionFile.Read(file);
                    node = new Node { Kind = NodeKind.Session, Name = model.Name, Protocol = model.Protocol, Settings = model.Settings };
                    if (model.Password != null) password = await AskImportPassphrase(model);
                    // A file from someone else could forward ports into this computer; only keep tunnels the user agrees to.
                    if (node.Settings.Tunnels.Count > 0 && !await Dialogs.Confirm(this,
                            $"\"{model.Name}\" opens these port forwards when it connects:\n\n" +
                            string.Join("\n", node.Settings.Tunnels.Select(t => "  " + t)) +
                            "\n\nKeep them? Choose No to import the session without them.", "Import session", defaultNo: true))
                        node.Settings.Tunnels.Clear();
                }

                if (node.Settings!.CredentialId is Guid cid && _store.Data.Credentials.All(c => c.Id != cid)) node.Settings.CredentialId = null;
                if (node.Settings.JumpHostId is Guid jid && _store.FindById(jid) == null) node.Settings.JumpHostId = null;
                var target = folder != null && folder.Protocol == node.Protocol ? folder : _store.RootFor(node.Protocol);
                _store.AddChild(target, node);
                if (password != null) CredentialStore.Set(node.Id, password, node.Settings!.Username);
                last = node;
            }
            catch (Exception ex)
            {
                await Error($"Could not import {Path.GetFileName(file)}", ex);
            }
        }
        if (last != null)
        {
            Select(last);
            Save();
        }
    }

    private async Task<string?> AskImportPassphrase(SessionFile.FileModel model)
    {
        var label = $"\"{model.Name}\" includes a protected password.\nEnter the password that was chosen when it was exported:";
        while (true)
        {
            var r = await Dialogs.PromptPassword(this, "Import session", label, offerSave: false);
            if (r == null)
            {
                await Info($"\"{model.Name}\" will be imported without its password.");
                return null;
            }
            var pw = SessionFile.Decrypt(model.Password!, r.Value.Password);
            if (pw != null) return pw;
            label = "Wrong password, try again:";
        }
    }

    private async Task<string?> PromptNewPassphrase(string title, string label)
    {
        var a = new TextBox { PasswordChar = '•', Watermark = "Password" };
        var b = new TextBox { PasswordChar = '•', Watermark = "Repeat the password" };
        var error = new TextBlock { Classes = { "sub" }, IsVisible = false };
        var body = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, a, b, error } };
        var d = new Dialogs.DialogWindow(title, body);
        d.AddButton("OK", isDefault: true, accent: true);
        d.AddButton("Cancel", isCancel: true);
        d.Validate = () =>
        {
            if (string.IsNullOrEmpty(a.Text) || a.Text.Length < 6) { error.Text = "Use at least 6 characters."; error.IsVisible = true; return false; }
            if (a.Text != b.Text) { error.Text = "The two passwords are different."; error.IsVisible = true; return false; }
            return true;
        };
        d.Opened += (_, _) => a.Focus();
        return await d.ShowFor(this) == 0 ? a.Text : null;
    }

    // ───────────── Export ─────────────

    private static string SafeName(string s) => string.Concat(s.Select(c => c == '/' || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private async Task Export(Node node)
    {
        var types = new List<FilePickerFileType> { Type("Session Manager session", "*.smsession") };
        if (node.Protocol == Protocol.Rdp) types.Add(Type("Remote Desktop file", "*.rdp"));
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save session to file", SuggestedFileName = SafeName(node.Name) + SessionFile.Extension,
            DefaultExtension = "smsession", FileTypeChoices = types,
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        if (Path.GetExtension(path).Equals(".rdp", StringComparison.OrdinalIgnoreCase))
        {
            SessionFile.ExportRdp(path, node);
            await Info("Saved. Passwords are not stored in .rdp files.");
            return;
        }

        var password = await Task.Run(() => CredentialStore.Get(PasswordId(node)));
        string? passphrase = null;
        if (password != null)
        {
            var answer = await Dialogs.Ask3(this, "Include the saved password in the file?\n\n" +
                "If you choose Yes, you'll pick a password to protect the file. It will be needed when the session is imported.",
                "Yes, protected", "No");
            if (answer < 0) return;
            if (answer == 0)
            {
                passphrase = await PromptNewPassphrase("Protect the password", "Choose a password to protect the exported file:");
                if (passphrase == null) return;
            }
            else password = null;
        }
        SessionFile.Export(path, node, password, passphrase);
        await Info(password != null ? "Session saved, including the encrypted password." : "Session saved (without password).");
    }

    private async Task ExportList(Node? folder)
    {
        List<Node> items = folder == null ? _store.Data.Roots.SelectMany(r => r.Children).ToList()
            : folder.IsRoot ? folder.Children.ToList()
            : [folder];
        await ExportNodes(items, full: folder == null,
            folder == null ? "Export all sessions" : $"Export \"{folder.Name}\"",
            folder == null ? "Sessions" : folder.Name);
    }

    /// <summary>Saves these items to a .smlist file; a full export also takes every profile and the presets.</summary>
    private async Task ExportNodes(List<Node> items, bool full, string title, string baseName)
    {
        var sessions = SessionList.AllNodes(items).Where(n => n.IsSession).ToList();
        if (sessions.Count == 0)
        {
            await Info("There are no sessions to export here.");
            return;
        }
        var used = sessions.Select(s => s.Settings?.CredentialId).OfType<Guid>().ToHashSet();
        var profiles = _store.Data.Credentials.Where(c => full || used.Contains(c.Id)).ToList();

        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = SafeName($"{baseName} {DateTime.Now:yyyy-MM-dd}") + SessionList.Extension,
            DefaultExtension = "smlist",
            FileTypeChoices = [Type("Session Manager session list", "*.smlist")],
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        var passwords = await Task.Run(() =>
        {
            var d = new Dictionary<Guid, string>();
            foreach (var id in sessions.Select(s => s.Id).Concat(profiles.Select(p => p.Id)))
                if (CredentialStore.Get(id) is { } pw) d[id] = pw;
            return d;
        });

        string? passphrase = null;
        if (passwords.Count > 0)
        {
            var answer = await Dialogs.Ask3(this,
                $"{passwords.Count} saved password{(passwords.Count == 1 ? "" : "s")} can be included in the file.\n\n" +
                "Include them? If you choose Yes, you'll pick a password that protects the file. It will be needed to import them.",
                "Yes, protected", "No");
            if (answer < 0) return;
            if (answer == 0)
            {
                passphrase = await PromptNewPassphrase("Protect the passwords", "Choose a password to protect the exported file:");
                if (passphrase == null) return;
            }
        }

        var model = SessionList.Build(items, profiles, full ? _store.Data.Presets : null, passphrase != null ? passwords : null, passphrase);
        passwords.Clear();
        SessionList.Write(path, model);
        int folders = SessionList.AllNodes(items).Count(n => n.IsFolder);
        await Info($"Exported {sessions.Count} session{(sessions.Count == 1 ? "" : "s")}" +
                   (folders > 0 ? $" in {folders} folder{(folders == 1 ? "" : "s")}" : "") +
                   (passphrase != null ? ", including the encrypted passwords." : " (without passwords).") + $"\n\n{path}");
    }

    private async Task ImportList(string file)
    {
        SessionList.FileModel model;
        try { model = SessionList.Read(file); }
        catch (Exception ex)
        {
            await Error($"Could not read {Path.GetFileName(file)}", ex);
            return;
        }

        var all = SessionList.AllNodes(model.Items).ToList();
        int sessionCount = all.Count(n => n.IsSession), folderCount = all.Count(n => n.IsFolder);
        if (sessionCount == 0)
        {
            await Info("This list doesn't contain any sessions.");
            return;
        }
        var description = $"\"{Path.GetFileName(file)}\" contains {sessionCount} session{(sessionCount == 1 ? "" : "s")}" +
                          (folderCount > 0 ? $" in {folderCount} folder{(folderCount == 1 ? "" : "s")}" : "") +
                          (model.Credentials.Count > 0 ? $" and {model.Credentials.Count} credential profile{(model.Credentials.Count == 1 ? "" : "s")}" : "") +
                          (model.Passwords != null ? ", with protected passwords" : "") + ".";
        var mode = await Dialogs.Ask3(this, description + "\n\nAdd them to your sessions, or replace all your sessions with this list?",
            "Add to my sessions", "Replace all", title: "Import session list");
        if (mode < 0) return;
        if (mode == 1 && !await Dialogs.Confirm(this, "Replace ALL your sessions and folders with this list? This can't be undone.", "Replace", "Replace", "Cancel", defaultNo: true))
            return;

        Dictionary<Guid, string>? passwords = null;
        if (model.Passwords != null)
        {
            var label = "Enter the password that was chosen when the list was exported:";
            while (true)
            {
                var r = await Dialogs.PromptPassword(this, "Import session list", label, offerSave: false);
                if (r == null)
                {
                    await Info("The sessions will be imported without their passwords.");
                    break;
                }
                passwords = SessionList.DecryptPasswords(model, r.Value.Password);
                if (passwords != null) break;
                label = "Wrong password, try again:";
            }
        }

        var withTunnels = all.Where(n => n.IsSession && n.Settings!.Tunnels.Count > 0).ToList();
        if (withTunnels.Count > 0)
        {
            var names = string.Join("\n", withTunnels.Take(8).Select(n => $"  {n.Name}: {string.Join(", ", n.Settings!.Tunnels)}"));
            if (withTunnels.Count > 8) names += $"\n  … and {withTunnels.Count - 8} more";
            if (!await Dialogs.Confirm(this, $"These sessions open port forwards when they connect:\n\n{names}\n\n" +
                    "Keep the port forwards? Choose No to import the sessions without them.", "Import session list", defaultNo: true))
                foreach (var n in withTunnels) n.Settings!.Tunnels.Clear();
        }

        try
        {
            if (mode == 1)
            {
                var keep = all.Select(n => n.Id).ToHashSet();
                foreach (var old in SessionList.AllNodes(_store.Data.Roots).Where(n => n.IsSession && !keep.Contains(n.Id)))
                {
                    try { CredentialStore.Delete(old.Id); } catch { }
                }
                SessionList.Clear(_store);
                if (model.Presets != null) _store.Data.Presets = model.Presets;
            }

            var result = SessionList.Merge(_store, model);
            int savedPasswords = 0, failed = 0;
            if (passwords != null)
            {
                foreach (var n in result.AddedSessions)
                    if (passwords.TryGetValue(n.Id, out var pw))
                    {
                        try { CredentialStore.Set(n.Id, pw, n.Settings!.Username); savedPasswords++; } catch { failed++; }
                    }
                foreach (var p in model.Credentials)
                    if (passwords.TryGetValue(p.Id, out var pw) && !CredentialStore.Has(p.Id))
                    {
                        try { CredentialStore.Set(p.Id, pw, p.Username); savedPasswords++; } catch { failed++; }
                    }
                passwords.Clear();
            }

            ApplyFilter();
            if (result.AddedSessions.FirstOrDefault() is { } first) Select(first);
            Save();
            await Info($"Imported {result.Added} session{(result.Added == 1 ? "" : "s")}" +
                       (result.Folders > 0 ? $" and {result.Folders} new folder{(result.Folders == 1 ? "" : "s")}" : "") + "." +
                       (result.Skipped > 0 ? $"\n\n{result.Skipped} session{(result.Skipped == 1 ? " was" : "s were")} already in your list and skipped." : "") +
                       (savedPasswords > 0 ? $"\n\n{savedPasswords} password{(savedPasswords == 1 ? "" : "s")} saved in the keyring." : "") +
                       (failed > 0 ? $"\n\n{failed} password{(failed == 1 ? "" : "s")} could not be saved (is a keyring running?)." : ""));
        }
        catch (Exception ex)
        {
            Save();
            await Error("The import did not finish", ex);
        }
    }

    // ───────────── Other tools ─────────────

    private async Task ImportConnectionFile()
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import connections",
            FileTypeFilter =
            [
                Type("Connection lists", "*.xml", "*.mxtsessions", "*.ini", "*.csv", "*.tsv", "*.remmina", "*.vnc", "config"),
                Type("All files", "*"),
            ],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await ReviewFile(path);
    }

    private async Task ReviewFile(string path)
    {
        string source = "file";
        await Review(() =>
        {
            var (items, src) = ConnectionImport.FromFile(path);
            source = src;
            return items;
        }, () => source);
    }

    private Task Review(Func<List<ImportedConnection>> read, string source) => Review(read, () => source);

    private async Task Review(Func<List<ImportedConnection>> read, Func<string> source)
    {
        List<ImportedConnection> items;
        try { items = await Task.Run(read); }
        catch (Exception ex)
        {
            await Error("Could not read the connections", ex);
            return;
        }
        if (items.Count == 0)
        {
            await Info($"No SSH or Remote Desktop connections were found in {source()}.");
            return;
        }
        await ReviewAndAdd(items, source(), null);
    }

    private async Task FindRdpFiles()
    {
        if (!await Dialogs.Confirm(this,
                "Look for .rdp files in your Desktop, Documents and Downloads folders (up to 5 folders deep, hidden folders skipped)?\n\n" +
                "Only the file names and connection settings are read; nothing is changed.", "Find .rdp files", "Search", "Cancel"))
            return;
        var preset = _store.PresetFor(Protocol.Rdp);
        await Review(() => ConnectionImport.FindRdpFiles(preset), ".rdp files");
    }

    // ───────────── Screenshot / PDF scan ─────────────

    private async Task ScanFilePicked()
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Scan a picture or PDF for connections",
            FileTypeFilter = [Type("Pictures and PDFs", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff", "*.pdf"), Type("All files", "*")],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await ScanAsync(() => TesseractOcr.ScanFileAsync(path), Path.GetFileNameWithoutExtension(path));
    }

    private async Task ScanClipboard()
    {
        var (png, file) = await ClipboardImage.GetAsync(Clipboard);
        if (png != null) await ScanAsync(() => TesseractOcr.ScanImageAsync(png), "Screenshot");
        else if (file != null) await ScanAsync(() => TesseractOcr.ScanFileAsync(file), Path.GetFileNameWithoutExtension(file));
        else await Info("There is no picture on the clipboard. Take a screenshot (for example with the Print Screen key and \"Copy to clipboard\"), then try again.\n\n" +
                        "On Wayland this needs wl-clipboard, on X11 xclip, if the picture isn't found.");
    }

    private async Task ScanAsync(Func<Task<ScreenshotScan.Result>> scan, string source)
    {
        if (_scanning) return;
        if (!TesseractOcr.HasTesseract)
        {
            await Info("Scanning pictures needs Tesseract (text recognition). Install it with:\n\n    sudo pacman -S tesseract tesseract-data-eng\n\nFor PDFs also: sudo pacman -S poppler");
            return;
        }
        _scanning = true;
        var cursor = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            var result = await scan();
            Cursor = cursor;
            if (result.Items.Count == 0 && result.Text.Length == 0)
            {
                await Info("No text was recognized. Try a sharper or larger picture.");
                return;
            }
            await ReviewAndAdd(result.Items, source, result.Text);
        }
        catch (Exception ex)
        {
            Cursor = cursor;
            await Error("The scan did not work", ex);
        }
        finally
        {
            Cursor = cursor;
            _scanning = false;
        }
    }

    private async Task ReviewAndAdd(List<ImportedConnection> items, string source, string? recognizedText)
    {
        var review = new ImportReviewWindow(source, items, recognizedText);
        if (!await review.ShowFor(this) || review.Selected.Count == 0) return;
        await AddImported(review.Selected, review.FolderName, review.SkipExisting, review.ShareLogins, source);
    }

    private async Task AddImported(List<ImportedConnection> items, string? folderName, bool skipExisting, bool shareLogins, string source)
    {
        static string Key(Protocol p, string host, int port, string user) => $"{p}|{host.ToLowerInvariant()}|{port}|{user.ToLowerInvariant()}";
        var existing = _store.Data.Roots.SelectMany(r => r.Descendants()).Where(n => n.IsSession && n.Settings != null)
            .Select(n => Key(n.Protocol, n.Settings!.Host, n.Settings.Port, ProfileFor(n.Settings)?.Username ?? n.Settings.Username)).ToHashSet();

        var profiles = new Dictionary<(string User, string Domain, string Password), CredentialProfile>();
        var errors = new List<string>();
        if (shareLogins)
        {
            foreach (var g in items.Where(i => i.Password != null && i.Username.Length > 0)
                         .GroupBy(i => (i.Username, i.Domain, Password: i.Password!)).Where(g => g.Count() > 1))
            {
                var key = g.Key;
                var profile = await Task.Run(() => _store.Data.Credentials.FirstOrDefault(c =>
                    c.Username.Equals(key.Username, StringComparison.OrdinalIgnoreCase) && c.Domain.Equals(key.Domain, StringComparison.OrdinalIgnoreCase)
                    && CredentialStore.Get(c.Id) == key.Password));
                if (profile == null)
                {
                    var baseName = (key.Domain.Length > 0 ? key.Domain + "\\" : "") + key.Username;
                    var name = baseName;
                    for (int n = 2; _store.Data.Credentials.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{baseName} ({n})";
                    profile = new CredentialProfile { Name = name, Username = key.Username, Domain = key.Domain };
                    try { CredentialStore.Set(profile.Id, key.Password, profile.Username); }
                    catch (Exception ex) { errors.Add(ex.Message); continue; }
                    _store.Data.Credentials.Add(profile);
                }
                profiles[key] = profile;
            }
        }

        int added = 0, skipped = 0, failedPasswords = 0;
        Node? last = null;
        foreach (var c in items)
        {
            var user = c.Protocol == Protocol.Ssh ? DomainName.Combine(c.Domain, c.Username) : c.Username;
            var key = Key(c.Protocol, c.Host, c.Port, user);
            if (skipExisting && existing.Contains(key)) { skipped++; continue; }
            existing.Add(key);

            var folder = _store.RootFor(c.Protocol);
            if (folderName != null) folder = FolderNamed(folder, folderName);
            foreach (var part in c.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                folder = FolderNamed(folder, part);

            var settings = c.Template?.Clone() ?? _store.PresetFor(c.Protocol);
            settings.Host = c.Host;
            settings.Port = c.Port > 0 ? c.Port : ConnectionImport.DefaultPort(c.Protocol);
            settings.Username = user;
            settings.Domain = c.Protocol == Protocol.Rdp ? c.Domain : "";
            settings.CredentialId = null;
            settings.JumpHostId = null;
            var keyPath = c.PrivateKeyPath.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), c.PrivateKeyPath[2..]) : c.PrivateKeyPath;
            if (c.Protocol == Protocol.Ssh && keyPath.Length > 0 && File.Exists(keyPath)) settings.PrivateKeyPath = keyPath;
            if (c.Protocol == Protocol.Rdp) RememberDomain(settings);
            if (c.Password != null && profiles.TryGetValue((c.Username, c.Domain, c.Password), out var shared))
                settings.CredentialId = shared.Id;

            var node = new Node { Kind = NodeKind.Session, Name = c.Name.Length > 0 ? c.Name : c.Host, Protocol = c.Protocol, Settings = settings };
            _store.AddChild(folder, node);
            if (c.Password != null && settings.CredentialId == null)
            {
                try { CredentialStore.Set(node.Id, c.Password, settings.Username); }
                catch { failedPasswords++; }
            }
            added++;
            last = node;
        }

        if (last != null) Select(last);
        ApplyFilter();
        Save();
        var msg = $"Added {added} connection{(added == 1 ? "" : "s")} from {source}.";
        if (profiles.Count > 0) msg += $"\n{profiles.Count} shared login{(profiles.Count == 1 ? "" : "s")} saved as credential profile{(profiles.Count == 1 ? "" : "s")}.";
        if (skipped > 0) msg += $"\n{skipped} skipped because you already have them.";
        if (failedPasswords > 0 || errors.Count > 0)
            msg += $"\n{failedPasswords + errors.Count} password{(failedPasswords + errors.Count == 1 ? "" : "s")} could not be saved in the keyring; you'll be asked when connecting." +
                   (errors.FirstOrDefault() is { } first ? $"\n\n{first}" : "");
        await Info(msg);
    }

    private Node FolderNamed(Node parent, string name)
    {
        var f = parent.Children.FirstOrDefault(n => n.IsFolder && n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (f != null) return f;
        f = new Node { Kind = NodeKind.Folder, Name = name, IsExpanded = true };
        _store.AddChild(parent, f);
        return f;
    }
}
