using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Renci.SshNet;

namespace SessionManager;

/// <summary>One entry in the remote folder listing.</summary>
public sealed class RemoteItem
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>A symbolic link (IsDirectory then says what it points to).</summary>
    public bool IsLink { get; init; }
    public long Length { get; init; }
    public DateTime Modified { get; init; }

    public string Glyph => IsDirectory ? "" : "";
    public string SizeText => IsDirectory ? "" : FormatSize(Length);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}

/// <summary>SFTP file browser shown beside an SSH terminal. Uses its own SFTP connection over the same route.</summary>
public sealed class SftpPanel : DockPanel, IDisposable
{
    private readonly ObservableCollection<RemoteItem> _items = new();
    private readonly ListBox _list = new();
    private readonly TextBox _pathBox = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 5), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5 };
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = new();

    private Func<Task<SftpClient>>? _connect;
    private SftpClient? _sftp;
    private Task? _connecting;
    private string _path = "";
    private bool _disposed;

    public SftpPanel()
    {
        SetResourceReference(BackgroundProperty, "PanelBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");

        // Toolbar
        var bar = new WrapPanel { Margin = new Thickness(4, 4, 4, 2) };
        bar.Children.Add(IconButton("", "Up one folder (Backspace)", () => _ = GoUp()));
        bar.Children.Add(IconButton("", "Home folder", () => _ = Load(_sftp?.WorkingDirectory ?? "/")));
        bar.Children.Add(IconButton("", "Refresh (F5)", () => _ = Load(_path)));
        bar.Children.Add(IconButton("", "New folder", () => _ = NewFolder()));
        bar.Children.Add(IconButton("", "Upload files…", () => _ = UploadPicked()));
        bar.Children.Add(IconButton("", "Download selected…", () => _ = DownloadSelected()));
        bar.Children.Add(IconButton("", "Delete selected (Del)", () => _ = DeleteSelected()));
        var header = new Border { Child = bar };
        header.SetResourceReference(Border.BackgroundProperty, "ToolbarBrush");
        SetDock(header, Dock.Top);
        Children.Add(header);

        // Path box
        _pathBox.Margin = new Thickness(6, 4, 6, 4);
        _pathBox.Padding = new Thickness(4, 2, 4, 2);
        _pathBox.ToolTip = "Type a path and press Enter";
        _pathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = Load(_pathBox.Text.Trim()); e.Handled = true; } };
        SetDock(_pathBox, Dock.Top);
        Children.Add(_pathBox);

        // Column headers
        var cols = Row(Label("Name"), Label("Size", HorizontalAlignment.Right), Label("Modified"));
        cols.Margin = new Thickness(10, 2, 14, 2);
        SetDock(cols, Dock.Top);
        Children.Add(cols);

        var statusBorder = new Border { Child = _status, BorderThickness = new Thickness(0, 1, 0, 0) };
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        SetDock(statusBorder, Dock.Bottom);
        Children.Add(statusBorder);

        // File list
        _list.ItemsSource = _items;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.SelectionMode = SelectionMode.Extended;
        _list.AllowDrop = true;
        _list.ItemContainerStyle = (Style)Application.Current.FindResource("ListRow");
        _list.ItemTemplate = BuildItemTemplate();
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.MouseDoubleClick += (_, e) => { if (ItemFrom(e.OriginalSource) is { } item) _ = Open(item); };
        _list.KeyDown += List_KeyDown;
        _list.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _sftp != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _list.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = Upload(files); };
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenuOpening += List_ContextMenuOpening;
        Children.Add(_list);

        SetStatus("Not connected");
        IsVisibleChanged += (_, _) => { if (IsVisible) EnsureConnected(); };
    }

    // ───────────── Connection ─────────────

    /// <summary>Called when the terminal is connected; the panel connects the first time it is shown.</summary>
    public void Attach(Func<Task<SftpClient>> connect)
    {
        _connect = connect;
        if (IsVisible) EnsureConnected();
    }

    /// <summary>Called when the terminal disconnects.</summary>
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

    private void EnsureConnected()
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
            // A newer connection may have started after a disconnect; only clear our own.
            if (_connect == connect) _connecting = null;
        }
    }

    /// <summary>Runs an SFTP operation off the UI thread, one at a time.</summary>
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

    private Task Open(RemoteItem item) => item.IsDirectory ? Load(item.FullName) : Download(new[] { item });

    // ───────────── File operations ─────────────

    private async Task NewFolder()
    {
        if (_sftp == null) return;
        var name = Dialogs.PromptText(Window.GetWindow(this)!, "New folder", "Folder name:");
        if (name == null) return;
        await Do("Creating folder", c => c.CreateDirectory(Join(_path, name)));
        await Load(_path);
    }

    private async Task Rename(RemoteItem item)
    {
        var name = Dialogs.PromptText(Window.GetWindow(this)!, "Rename", "New name:", item.Name);
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
        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete {what} on the server?{folders}", "Delete", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
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
        if (_sftp == null) return;
        var dlg = new OpenFileDialog { Title = $"Upload to {_path}", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) await Upload(dlg.FileNames);
    }

    private async Task Upload(string[] localPaths)
    {
        if (_sftp == null || localPaths.Length == 0) return;
        var existing = _items.Select(i => i.Name).ToHashSet();
        var clashes = localPaths.Select(Path.GetFileName).Where(n => n != null && existing.Contains(n)).ToList();
        if (clashes.Count > 0 && MessageBox.Show(Window.GetWindow(this)!,
                $"{(clashes.Count == 1 ? $"\"{clashes[0]}\" already exists" : $"{clashes.Count} items already exist")} in {_path}. Overwrite?",
                "Upload", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

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
            if (new DirectoryInfo(local).LinkTarget != null) return; // junction or link: skip, it can loop
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
        var owner = Window.GetWindow(this);
        if (items.Count == 1 && !items[0].IsDirectory)
        {
            var dlg = new SaveFileDialog { Title = "Download", FileName = items[0].Name };
            if (dlg.ShowDialog(owner) != true) return;
            var item = items[0];
            await Do("Downloading", c => DownloadFile(c, item.FullName, dlg.FileName, item.Length));
            SetStatus($"Downloaded {item.Name}");
            return;
        }
        var folder = new OpenFolderDialog { Title = "Download to folder" };
        if (folder.ShowDialog(owner) != true) return;
        await Do("Downloading", c =>
        {
            foreach (var i in items) DownloadRecursive(c, i, folder.FolderName);
        });
        SetStatus($"Downloaded {items.Count} item{(items.Count == 1 ? "" : "s")} to {folder.FolderName}");
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
        string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>Downloads a copy, opens it in Notepad and offers to upload it again whenever it is saved.</summary>
    private async Task EditInNotepad(RemoteItem item)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SessionManager", "edit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var local = Path.Combine(dir, SafeFileName(item.Name));
        if (!await Do("Downloading", c => DownloadFile(c, item.FullName, local, item.Length))) return;
        SetStatus($"Editing {item.Name} — save in Notepad to upload it again");

        var watcher = new FileSystemWatcher(dir, Path.GetFileName(local)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        bool asking = false;
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            if (asking || _disposed || _sftp == null) return;
            asking = true;
            var answer = MessageBox.Show(Window.GetWindow(this)!, $"\"{item.Name}\" was changed. Upload it to {item.FullName}?",
                "Upload changes", MessageBoxButton.YesNo, MessageBoxImage.Question);
            asking = false;
            if (answer != MessageBoxResult.Yes) return;
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
        watcher.Changed += (_, _) => Dispatcher.InvokeAsync(() => { debounce.Stop(); debounce.Start(); });
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);

        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = false }); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    /// <summary>Runs an operation and reports failures; returns false if it failed.</summary>
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
            MessageBox.Show(Window.GetWindow(this)!, $"{what} failed.\n\n{ex.Message}", "SFTP", MessageBoxButton.OK, MessageBoxImage.Error);
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
        Dispatcher.InvokeAsync(() => SetStatus($"{verb} {name}{pct} ({RemoteItem.FormatSize(done)})"));
    }

    private void SetStatus(string text) => _status.Text = text;

    // ───────────── Input ─────────────

    private List<RemoteItem> Selected() => _list.SelectedItems.Cast<RemoteItem>().ToList();

    private static RemoteItem? ItemFrom(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem li) return li.DataContext as RemoteItem;
        return null;
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        var sel = _list.SelectedItem as RemoteItem;
        switch (e.Key)
        {
            case Key.Enter when sel != null: _ = Open(sel); break;
            case Key.Back: _ = GoUp(); break;
            case Key.Delete: _ = DeleteSelected(); break;
            case Key.F2 when sel != null: _ = Rename(sel); break;
            case Key.F5: _ = Load(_path); break;
            default: return;
        }
        e.Handled = true;
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = _list.ContextMenu!;
        menu.Items.Clear();
        if (_sftp == null) { e.Handled = true; return; }
        var sel = Selected();
        var one = sel.Count == 1 ? sel[0] : null;
        if (one is { IsDirectory: true }) menu.Items.Add(MenuItem("Open", "", () => _ = Load(one.FullName), "Enter"));
        if (sel.Count > 0) menu.Items.Add(MenuItem("Download…", "", () => _ = Download(sel)));
        if (one is { IsDirectory: false }) menu.Items.Add(MenuItem("Edit in Notepad", "", () => _ = EditInNotepad(one)));
        if (one != null) menu.Items.Add(MenuItem("Rename", "", () => _ = Rename(one), "F2"));
        if (sel.Count > 0) menu.Items.Add(MenuItem("Delete", "", () => _ = DeleteSelected(), "Del"));
        if (one != null) menu.Items.Add(MenuItem("Copy path", "", () => { try { Clipboard.SetText(one.FullName); } catch { } }));
        if (sel.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Upload files…", "", () => _ = UploadPicked()));
        menu.Items.Add(MenuItem("New folder", "", () => _ = NewFolder()));
        menu.Items.Add(MenuItem("Refresh", "", () => _ = Load(_path), "F5"));
    }

    // ───────────── UI helpers ─────────────

    private static MenuItem MenuItem(string header, string glyph, Action action, string? gesture = null)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 14 };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var item = new MenuItem { Header = header, Icon = icon, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    private static Button IconButton(string glyph, string tip, Action action)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ToolButton"),
            Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 13 },
            ToolTip = tip,
            Padding = new Thickness(7, 5, 7, 5),
            Focusable = false,
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static TextBlock Label(string text, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var t = new TextBlock { Text = text, FontSize = 11, HorizontalAlignment = align };
        t.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        return t;
    }

    /// <summary>Name | Size | Modified columns, shared by the header and the rows.</summary>
    private static Grid Row(UIElement name, UIElement size, UIElement modified)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        Grid.SetColumn(size, 1);
        Grid.SetColumn(modified, 2);
        if (modified is FrameworkElement m) m.Margin = new Thickness(12, 0, 0, 0);
        g.Children.Add(name);
        g.Children.Add(size);
        g.Children.Add(modified);
        return g;
    }

    private static DataTemplate BuildItemTemplate() => (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="64" />
                    <ColumnDefinition Width="112" />
                </Grid.ColumnDefinitions>
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="{Binding Glyph}" FontFamily="{DynamicResource Icons}" Foreground="{DynamicResource FolderBrush}"
                               Margin="0,0,7,0" VerticalAlignment="Center" />
                    <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" />
                </StackPanel>
                <TextBlock Grid.Column="1" Text="{Binding SizeText}" HorizontalAlignment="Right" FontSize="11.5"
                           Foreground="{DynamicResource SubTextBrush}" />
                <TextBlock Grid.Column="2" Text="{Binding ModifiedText}" Margin="12,0,0,0" FontSize="11.5"
                           Foreground="{DynamicResource SubTextBrush}" />
            </Grid>
        </DataTemplate>
        """);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        Detach();
    }
}
