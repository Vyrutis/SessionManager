using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Renci.SshNet;

namespace SessionManager;

public sealed class RemoteItem
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsLink { get; init; }
    public long Length { get; init; }
    public DateTime Modified { get; init; }

    public string Glyph => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? "" : FormatSize(Length);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}

/// <summary>Remote files over SFTP: browse, upload (also by dropping files), download, edit, rename and delete.</summary>
public sealed class SftpPanel : DockPanel, IDisposable
{
    private readonly ObservableCollection<RemoteItem> _items = new();
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _pathBox = new() { FontSize = 12 };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 5), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Func<Window?> _owner;
    private Func<Task<SftpClient>>? _connect;
    private Task? _connecting;
    private SftpClient? _sftp;
    private string _path = "";
    private bool _disposed;

    public SftpPanel(Func<Window?> owner)
    {
        _owner = owner;
        this.Res(BackgroundProperty, "SmSidebar");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 4, 4, 2) };
        bar.Children.Add(Tool("↑", "Parent folder", () => _ = GoUp()));
        bar.Children.Add(Tool("⟳", "Refresh", () => _ = Load(_path)));
        bar.Children.Add(Tool("⤒ Upload", "Upload files", () => _ = UploadPicked()));
        bar.Children.Add(Tool("⤓ Download", "Download the selected items", () => _ = DownloadSelected()));
        bar.Children.Add(Tool("+ Folder", "New folder", () => _ = NewFolder()));
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        _pathBox.Margin = new Thickness(6, 2, 6, 4);
        _pathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = Load(_pathBox.Text?.Trim() ?? ""); e.Handled = true; } };
        SetDock(_pathBox, Dock.Top);
        Children.Add(_pathBox);

        SetDock(_status, Dock.Bottom);
        Children.Add(_status.Res(TextBlock.ForegroundProperty, "SmSubText"));

        _list.ItemsSource = _items;
        _list.Background = Brushes.Transparent;
        _list.ItemTemplate = new FuncDataTemplate<RemoteItem>((item, scope) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Background = Brushes.Transparent };
            g.Children.Add(new TextBlock { Text = item?.Glyph, Margin = new Thickness(0, 0, 6, 0), FontSize = 12 });
            var name = new TextBlock { Text = item?.Name, TextTrimming = TextTrimming.CharacterEllipsis };
            if (item?.IsLink == true) name.FontStyle = FontStyle.Italic;
            Grid.SetColumn(name, 1);
            g.Children.Add(name);
            var size = new TextBlock { Text = item?.SizeText, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                .Res(TextBlock.ForegroundProperty, "SmSubText");
            Grid.SetColumn(size, 2);
            g.Children.Add(size);
            ToolTip.SetTip(g, $"{item?.FullName}\n{item?.SizeText}  {item?.ModifiedText}".Trim());
            g.DoubleTapped += (_, _) => { if (item != null) _ = Open(item); };
            return g;
        });
        _list.KeyDown += List_KeyDown;
        _list.ContextRequested += (_, e) => { ShowMenu(); e.Handled = true; };
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.Data.Contains(DataFormats.Files) && _sftp != null ? DragDropEffects.Copy : DragDropEffects.None);
        _list.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
            if (paths.Length > 0) _ = Upload(paths);
        });
        Children.Add(_list);
        SetStatus("Not connected");
    }

    private static Button Tool(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Classes = { "tool" }, FontSize = 12, Padding = new Thickness(7, 4) };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    // ───────────── Connection ─────────────

    public void Attach(Func<Task<SftpClient>> connect) => _connect = connect;

    public void Detach()
    {
        _connect = null;
        var c = _sftp;
        _sftp = null;
        _connecting = null;
        if (c != null) Task.Run(() => { try { c.Dispose(); } catch { } });
        _items.Clear();
        SetStatus("Not connected");
    }

    public void EnsureConnected()
    {
        if (_sftp != null || _connecting != null || _connect == null || _disposed) return;
        _connecting = ConnectAsync(_connect);
    }

    private async Task ConnectAsync(Func<Task<SftpClient>> connect)
    {
        SetStatus("Connecting SFTP…");
        try
        {
            var client = await connect();
            if (_disposed || _connect != connect) { client.Dispose(); return; }
            _sftp = client;
            await Load(client.WorkingDirectory);
        }
        catch (Exception ex)
        {
            if (_connect == connect) SetStatus("SFTP unavailable: " + ex.Message);
        }
        finally
        {
            if (_connect == connect) _connecting = null;
        }
    }

    private async Task<T> Run<T>(Func<SftpClient, T> op)
    {
        var c = _sftp ?? throw new InvalidOperationException("Not connected.");
        await _busy.WaitAsync();
        try { return await Task.Run(() => op(c)); }
        finally { _busy.Release(); }
    }

    private Task Run(Action<SftpClient> op) => Run<bool>(c => { op(c); return true; });

    // ───────────── Navigation ─────────────

    private async Task Load(string path)
    {
        if (_sftp == null || path.Length == 0) return;
        try
        {
            var items = await Run(c => c.ListDirectory(path)
                .Where(f => f.Name is not "." and not "..")
                .Select(f =>
                {
                    bool dir = f.IsDirectory;
                    if (f.IsSymbolicLink)
                    {
                        try { dir = c.GetAttributes(f.FullName).IsDirectory; } catch { }
                    }
                    return new RemoteItem { Name = f.Name, FullName = f.FullName, IsDirectory = dir, IsLink = f.IsSymbolicLink, Length = f.Length, Modified = f.LastWriteTime };
                })
                .OrderByDescending(i => i.IsDirectory)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
            _path = path;
            _pathBox.Text = path;
            _items.Clear();
            foreach (var i in items) _items.Add(i);
            SetStatus($"{items.Count} item{(items.Count == 1 ? "" : "s")}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            _pathBox.Text = _path;
        }
    }

    private Task GoUp()
    {
        if (_path is "" or "/") return Task.CompletedTask;
        int i = _path.TrimEnd('/').LastIndexOf('/');
        return Load(i <= 0 ? "/" : _path[..i]);
    }

    private static string Join(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    private Task Open(RemoteItem item) => item.IsDirectory ? Load(item.FullName) : Download([item]);

    // ───────────── File operations ─────────────

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async Task NewFolder()
    {
        if (_sftp == null) return;
        var name = await Dialogs.PromptText(_owner(), "New folder", "Folder name:");
        if (name == null) return;
        await Do("Creating folder", c => c.CreateDirectory(Join(_path, name)));
        await Load(_path);
    }

    private async Task Rename(RemoteItem item)
    {
        var name = await Dialogs.PromptText(_owner(), "Rename", "New name:", item.Name);
        if (name == null || name == item.Name) return;
        await Do("Renaming", c => c.RenameFile(item.FullName, Join(_path, name)));
        await Load(_path);
    }

    private async Task DeleteSelected()
    {
        var items = Selected();
        if (items.Count == 0) return;
        var what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"{items.Count} items";
        var folders = items.Any(i => i.IsDirectory && !i.IsLink) ? "\n\nFolders are deleted with everything in them." : "";
        if (!await Dialogs.Confirm(_owner(), $"Delete {what} on the server?{folders}", "Delete", "Delete", "Cancel", defaultNo: true)) return;
        await Do("Deleting", c => { foreach (var i in items) DeleteRecursive(c, i.FullName, i.IsDirectory && !i.IsLink); });
        await Load(_path);
    }

    /// <summary>Deletes a file or a folder with its contents. Links are removed themselves, never followed.</summary>
    private static void DeleteRecursive(SftpClient c, string path, bool isDir)
    {
        if (!isDir) { c.DeleteFile(path); return; }
        foreach (var f in c.ListDirectory(path).Where(f => f.Name is not "." and not ".."))
            DeleteRecursive(c, f.FullName, f.IsDirectory && !f.IsSymbolicLink);
        c.DeleteDirectory(path);
    }

    private async Task UploadPicked()
    {
        if (_sftp == null || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Upload to {_path}", AllowMultiple = true });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) await Upload(paths);
    }

    private async Task Upload(string[] localPaths)
    {
        if (_sftp == null || localPaths.Length == 0) return;
        var existing = _items.Select(i => i.Name).ToHashSet();
        var clashes = localPaths.Select(p => Path.GetFileName(p.TrimEnd('/'))).Where(n => existing.Contains(n)).ToList();
        if (clashes.Count > 0 && !await Dialogs.Confirm(_owner(),
                $"{(clashes.Count == 1 ? $"\"{clashes[0]}\" already exists" : $"{clashes.Count} items already exist")} in {_path}. Overwrite?",
                "Upload", "Overwrite", "Cancel")) return;

        var target = _path;
        await Do("Uploading", c =>
        {
            foreach (var p in localPaths) UploadRecursive(c, p, target);
        });
        await Load(_path);
    }

    private void UploadRecursive(SftpClient c, string local, string remoteDir)
    {
        var name = Path.GetFileName(local.TrimEnd('\\', '/'));
        var remote = Join(remoteDir, name);
        if (Directory.Exists(local))
        {
            if (new DirectoryInfo(local).LinkTarget != null) return; // symlinked folder: skip, it can loop
            if (!c.Exists(remote)) c.CreateDirectory(remote);
            foreach (var entry in Directory.EnumerateFileSystemEntries(local)) UploadRecursive(c, entry, remote);
            return;
        }
        using var fs = File.OpenRead(local);
        long total = fs.Length;
        c.UploadFile(fs, remote, true, done => Progress("Uploading", name, (long)done, total));
    }

    private async Task DownloadSelected()
    {
        var items = Selected();
        if (items.Count > 0) await Download(items);
    }

    private async Task Download(IReadOnlyList<RemoteItem> items)
    {
        if (Storage is not { } storage) return;
        if (items.Count == 1 && !items[0].IsDirectory)
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Download", SuggestedFileName = items[0].Name });
            if (file?.TryGetLocalPath() is not { } local) return;
            var item = items[0];
            if (await Do("Downloading", c => DownloadFile(c, item.FullName, local, item.Length)))
                SetStatus($"Downloaded {item.Name}");
            return;
        }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Download to folder" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } dir) return;
        if (await Do("Downloading", c => { foreach (var i in items) DownloadRecursive(c, i, dir); }))
            SetStatus($"Downloaded {items.Count} item{(items.Count == 1 ? "" : "s")} to {dir}");
    }

    private void DownloadRecursive(SftpClient c, RemoteItem item, string localDir)
    {
        var local = Path.Combine(localDir, SafeFileName(item.Name));
        if (!item.IsDirectory) { DownloadFile(c, item.FullName, local, item.Length); return; }
        Directory.CreateDirectory(local);
        foreach (var f in c.ListDirectory(item.FullName).Where(f => f.Name is not "." and not ".."))
            DownloadRecursive(c, new RemoteItem { Name = f.Name, FullName = f.FullName, IsDirectory = f.IsDirectory && !f.IsSymbolicLink, Length = f.Length }, local);
    }

    private void DownloadFile(SftpClient c, string remote, string local, long total)
    {
        var name = Path.GetFileName(local);
        using var fs = File.Create(local);
        c.DownloadFile(remote, fs, done => Progress("Downloading", name, (long)done, total));
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(ch => ch == '/' || ch == '\0' || Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>Downloads a copy, opens it in the default editor and offers to upload it again whenever it is saved.</summary>
    private async Task Edit(RemoteItem item)
    {
        // In the user's own cache folder (~/.cache/sessionmanager/edit), not in the shared /tmp.
        var cache = OperatingSystem.IsWindows() ? Path.GetTempPath()
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        var dir = Path.Combine(cache, "sessionmanager", "edit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var local = Path.Combine(dir, SafeFileName(item.Name));
        if (!await Do("Downloading", c => DownloadFile(c, item.FullName, local, item.Length))) return;
        SetStatus($"Editing {item.Name} — save it to upload it again");

        var watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        bool asking = false;
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            if (asking || _disposed || _sftp == null || !File.Exists(local)) return;
            asking = true;
            var answer = await Dialogs.Confirm(_owner(), $"\"{item.Name}\" was changed. Upload it to {item.FullName}?", "Upload changes", "Upload", "Not now");
            asking = false;
            if (!answer) return;
            if (await Do("Uploading", c =>
                {
                    using var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    c.UploadFile(fs, item.FullName, true);
                }))
            {
                SetStatus($"Uploaded {item.Name}");
                await Load(_path);
            }
        };
        // Editors often save by writing a new file and renaming it over the old one.
        void Changed(object? s, FileSystemEventArgs e)
        {
            if (Path.GetFileName(e.FullPath) != Path.GetFileName(local)) return;
            Dispatcher.UIThread.Post(() => { debounce.Stop(); debounce.Start(); });
        }
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Renamed += (s, e) => Changed(s, e);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);

        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(local) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open", [local]) { UseShellExecute = false };
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    private async Task<bool> Do(string what, Action<SftpClient> op)
    {
        if (_sftp == null) return false;
        SetStatus(what + "…");
        try
        {
            await Run(op);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"{what} failed: {ex.Message}");
            await Dialogs.Info(_owner(), $"{what} failed.\n\n{ex.Message}", "SFTP");
            return false;
        }
    }

    private long _lastProgress;

    private void Progress(string verb, string name, long done, long total)
    {
        long now = Environment.TickCount64;
        if (now - _lastProgress < 100 && done < total) return;
        _lastProgress = now;
        var pct = total > 0 ? $" — {done * 100 / total}%" : "";
        Dispatcher.UIThread.Post(() => SetStatus($"{verb} {name}{pct} ({RemoteItem.FormatSize(done)})"));
    }

    private void SetStatus(string text) => _status.Text = text;

    // ───────────── Input ─────────────

    private List<RemoteItem> Selected() => _list.SelectedItems?.Cast<RemoteItem>().ToList() ?? [];

    private void List_KeyDown(object? sender, KeyEventArgs e)
    {
        var items = Selected();
        switch (e.Key)
        {
            case Key.Enter when items.Count == 1: _ = Open(items[0]); break;
            case Key.Back: _ = GoUp(); break;
            case Key.Delete: _ = DeleteSelected(); break;
            case Key.F2 when items.Count == 1: _ = Rename(items[0]); break;
            case Key.F5: _ = Load(_path); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ShowMenu()
    {
        if (_sftp == null) return;
        var items = Selected();
        var list = new List<object>();
        MenuItem Item(string header, Action a, bool enabled = true)
        {
            var m = new MenuItem { Header = header, IsEnabled = enabled };
            m.Click += (_, _) => a();
            list.Add(m);
            return m;
        }
        bool one = items.Count == 1;
        Item("Open", () => _ = Open(items[0]), one);
        Item("Edit (upload on save)", () => _ = Edit(items[0]), one && !items[0].IsDirectory);
        Item("Download…", () => _ = Download(items), items.Count > 0);
        Item("Copy path", () => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(items[0].FullName), one);
        list.Add(new Separator());
        Item("Rename…", () => _ = Rename(items[0]), one);
        Item("Delete", () => _ = DeleteSelected(), items.Count > 0);
        list.Add(new Separator());
        Item("Upload files…", () => _ = UploadPicked());
        Item("New folder…", () => _ = NewFolder());
        Item("Refresh", () => _ = Load(_path));
        new ContextMenu { ItemsSource = list }.Open(_list);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        Detach();
    }
}
