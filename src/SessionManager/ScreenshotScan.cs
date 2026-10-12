using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinImaging = Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace SessionManager;

/// <summary>
/// Finds connections in a picture or PDF (a server list, a wiki page, an e-mail, a spreadsheet...).
/// Windows' built-in OCR reads the text, the words are put back into rows and columns by their position,
/// then hosts, ports, user names, passwords and the protocol are picked out of each row.
/// Everything runs on this PC; nothing is uploaded.
/// </summary>
public static class ScreenshotScan
{
    public sealed record Result(List<ImportedConnection> Items, string Text);

    /// <summary>One piece of text on a row; a big horizontal gap between words starts a new cell.</summary>
    public sealed record Cell(string Text, double Left, double Right)
    {
        public double Center => (Left + Right) / 2;
    }

    public sealed record Row(List<Cell> Cells)
    {
        public string Text => string.Join("\t", Cells.Select(c => c.Text));
    }

    public const int MaxPdfPages = 60;

    // ───────────── Inputs ─────────────

    public static Task<Result> ScanImageAsync(BitmapSource image) => ScanAsync(async () => [await ToSoftwareBitmap(image)]);

    public static Task<Result> ScanFileAsync(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? ScanAsync(() => RenderPdf(path))
            : ScanImageAsync(LoadFile(path));

    private static async Task<Result> ScanAsync(Func<Task<List<WinImaging.SoftwareBitmap>>> pages)
    {
        var engine = CreateEngine();
        var rows = new List<Row>();
        foreach (var page in await pages())
        {
            using (page)
            {
                var ocr = await engine.RecognizeAsync(page);
                rows.AddRange(ToRows(ocr));
            }
            rows.Add(new Row([])); // page break: nothing carries over from one page to the next
        }
        var text = string.Join("\n", rows.Select(r => r.Text)).Trim();
        return new Result(Extract(rows), text);
    }

    public static BitmapSource LoadFile(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(Path.GetFullPath(path));
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static WinOcr.OcrEngine CreateEngine() =>
        WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
        ?? WinOcr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
        ?? throw new InvalidOperationException("Windows text recognition (OCR) is not available. Add a language with OCR support in Settings > Time & language > Language & region.");

    private static async Task<WinImaging.SoftwareBitmap> ToSoftwareBitmap(BitmapSource image)
    {
        // Small text recognizes much better when enlarged; stay under the engine's size limit.
        double max = WinOcr.OcrEngine.MaxImageDimension;
        double longest = Math.Max(image.PixelWidth, image.PixelHeight);
        double scale = longest < 1400 ? 2 : 1;
        if (longest * scale > max) scale = max / longest;
        BitmapSource src = Math.Abs(scale - 1) > 0.01 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return await Decode(ms.AsRandomAccessStream());
    }

    private static async Task<WinImaging.SoftwareBitmap> Decode(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        using (stream)
        {
            var decoder = await WinImaging.BitmapDecoder.CreateAsync(stream);
            return await decoder.GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied);
        }
    }

    /// <summary>Renders each PDF page at about 220 dpi (scanned and text PDFs alike) for the OCR.</summary>
    private static async Task<List<WinImaging.SoftwareBitmap>> RenderPdf(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        Windows.Data.Pdf.PdfDocument doc;
        try { doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B)) // wrong password
        { throw new InvalidOperationException("This PDF is password protected. Open it, print it to a new PDF without a password, and scan that.", ex); }

        var pages = new List<WinImaging.SoftwareBitmap>();
        uint count = Math.Min(doc.PageCount, MaxPdfPages);
        for (uint i = 0; i < count; i++)
        {
            using var page = doc.GetPage(i);
            double w = page.Size.Width, h = page.Size.Height; // in 1/96 inch
            double scale = Math.Min(220.0 / 96, WinOcr.OcrEngine.MaxImageDimension / Math.Max(w, h));
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)(w * scale), DestinationHeight = (uint)(h * scale),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            });
            stream.Seek(0);
            pages.Add(await Decode(stream));
        }
        return pages;
    }

    /// <summary>
    /// Puts OCR words back into visual rows (same height on the page) and cells (split at wide gaps),
    /// so a table "IP | user | password" stays together even when the OCR reports its columns separately.
    /// </summary>
    private static List<Row> ToRows(WinOcr.OcrResult ocr)
    {
        var words = ocr.Lines.SelectMany(l => l.Words).Select(w => (w.Text, R: w.BoundingRect)).ToList();
        if (words.Count == 0) return [];
        double lineHeight = words.Select(w => w.R.Height).OrderBy(x => x).ElementAt(words.Count / 2);

        var rows = new List<List<(string Text, Windows.Foundation.Rect R)>>();
        foreach (var w in words.OrderBy(w => w.R.Y + w.R.Height / 2))
        {
            double cy = w.R.Y + w.R.Height / 2;
            var row = rows.LastOrDefault();
            if (row != null && Math.Abs(cy - row.Average(x => x.R.Y + x.R.Height / 2)) < lineHeight * 0.55) row.Add(w);
            else rows.Add([w]);
        }

        var result = new List<Row>();
        foreach (var row in rows)
        {
            var cells = new List<Cell>();
            string text = "";
            double left = 0, right = 0;
            foreach (var w in row.OrderBy(w => w.R.X))
            {
                if (text.Length > 0 && w.R.X - right > lineHeight * 1.2)
                {
                    cells.Add(new Cell(text, left, right));
                    text = "";
                }
                if (text.Length == 0) { text = w.Text; left = w.R.X; }
                else text += " " + w.Text;
                right = w.R.X + w.R.Width;
            }
            if (text.Length > 0) cells.Add(new Cell(text, left, right));
            result.Add(new Row(cells));
        }
        return result;
    }

    // ───────────── Picking connections out of the text ─────────────

    private static readonly Regex Ipv4 = new(@"(?<![\d.])((?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3})(?::(\d{1,5}))?(?![\d.]*\d)", RegexOptions.Compiled);
    private static readonly Regex UserAtHost = new(@"(?<![\w.-])([A-Za-z0-9._\\-]{1,64})@((?:[A-Za-z0-9-]+\.)*[A-Za-z0-9-]+|\d{1,3}(?:\.\d{1,3}){3})(?::(\d{1,5}))?", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"\b(rdp|ssh|sftp)://([^\s,;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Host names with at least one dot and a letter-only last part (srv01.corp.local, db.example.com).
    private static readonly Regex Fqdn = new(@"(?<![\w@.-])((?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,24})(?::(\d{1,5}))?(?![\w-])", RegexOptions.Compiled);
    private static readonly Regex HostLabel = new(@"\b(?:host(?:\s*name)?|server|address|ip(?:\s*address)?|computer|machine|target)\s*[:=]\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UserLabel = new(@"\b(?:user(?:\s*name)?|login|account|benutzer(?:name)?|utilisateur|usuario)\s*[:=]\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PassLabel = new(@"\b(?:password|passwort|kennwort|pass|pwd|pw|mot\s*de\s*passe|contrase(?:ñ|n)a)\s*[:=]\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PortLabel = new(@"\bport\s*[:=]?\s*(\d{1,5})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DomainLabel = new(@"\bdomain\s*[:=]\s*([A-Za-z0-9._-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyLabel = new(@"^\s*[A-Za-zÀ-ÿ ]{2,20}\s*[:=]", RegexOptions.Compiled);
    private static readonly Regex RdpWords = new(@"\b(rdp|mstsc|remote\s*desktop|3389|windows(\s*server)?|terminal\s*server|win\d{2}|server\s*20\d\d)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SshWords = new(@"\b(ssh|sftp|putty|linux|ubuntu|debian|centos|rhel|rocky|alma|bash|esxi|router|switch|firewall)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Column headers of a server table.
    private enum Role { None, Name, Host, Port, User, Password, Domain, Protocol }
    private static readonly (Regex Re, Role Role)[] HeaderWords =
    [
        (new(@"^(ip(\s*address)?|ipv4|host(\s*name)?|server|address|fqdn|computer|machine)$", RegexOptions.IgnoreCase), Role.Host),
        (new(@"^(user(\s*name)?|login|account|benutzer(name)?|utilisateur|usuario)$", RegexOptions.IgnoreCase), Role.User),
        (new(@"^(password|passwort|kennwort|pass|pwd|pw|mot de passe|contrase(ñ|n)a|secret)$", RegexOptions.IgnoreCase), Role.Password),
        (new(@"^(port)$", RegexOptions.IgnoreCase), Role.Port),
        (new(@"^(domain)$", RegexOptions.IgnoreCase), Role.Domain),
        (new(@"^(protocol|type|access|os|system)$", RegexOptions.IgnoreCase), Role.Protocol),
        (new(@"^(name|description|label|title|role|function|service|device)$", RegexOptions.IgnoreCase), Role.Name),
    ];

    // Words that look like host names in OCR text but aren't servers.
    private static readonly HashSet<string> NotHosts = new(StringComparer.OrdinalIgnoreCase) { "e.g", "i.e", "etc", "vs", "a.m", "p.m" };
    private static readonly string[] FileExts = [".txt", ".exe", ".png", ".jpg", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".zip", ".log", ".ps1", ".sh", ".msi", ".dll", ".csv", ".json", ".xml", ".html", ".rdp", ".ini", ".cfg", ".conf"];

    private sealed class Hit
    {
        public required ImportedConnection C;
        public int Row;
        public int Cell = -1;
        public bool ExplicitProto, HasPassword, HasUser;
    }

    /// <summary>For plain text (tests, pasted text): cells are separated by tabs, '|' or 2+ spaces.</summary>
    public static List<ImportedConnection> Extract(string text) =>
        Extract(text.Replace("\r", "").Split('\n')
            .Select(l => new Row(Regex.Split(l.Trim(), @"\t+|\s*\|\s*|\s{2,}").Where(c => c.Length > 0)
                .Select((c, i) => new Cell(c, i * 100, i * 100 + 80)).ToList()))
            .ToList());

    public static List<ImportedConnection> Extract(List<Row> rawRows)
    {
        var rows = rawRows.Select(r => new Row(r.Cells.Select(c => c with { Text = NormalizeCell(c.Text) }).Where(c => c.Text.Length > 0).ToList())).ToList();
        var lines = rows.Select(r => r.Text).ToList();
        var hits = new List<Hit>();

        void Add(int row, string host, int? port, string user, Protocol? proto)
        {
            host = host.Trim().TrimEnd('.', ',', ';', ')', ']');
            if (host.Length == 0 || NotHosts.Contains(host)) return;
            if (FileExts.Any(e => host.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return;
            if (IPAddress.TryParse(host, out var ip) && (ip.Equals(IPAddress.Any) || host.EndsWith(".255") || host.StartsWith("255."))) return;
            if (port is <= 0 or > 65535) port = null;
            // The same host found twice on one row (e.g. by the IP and the user@host patterns) is one hit.
            var existing = hits.FirstOrDefault(f => f.Row == row && f.C.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (existing.C.Username.Length == 0 && user.Length > 0) { existing.C.Username = user; existing.HasUser = true; }
                if (existing.C.Port == 0 && port != null) existing.C.Port = port.Value;
                return;
            }
            var cell = rows[row].Cells.FindIndex(c => c.Text.Contains(host, StringComparison.OrdinalIgnoreCase));
            hits.Add(new Hit
            {
                C = new ImportedConnection { Host = host, Port = port ?? 0, Username = user, Protocol = proto ?? Protocol.Ssh },
                Row = row, Cell = cell, ExplicitProto = proto != null, HasUser = user.Length > 0,
            });
        }

        // 1. Hosts on every row
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            foreach (Match m in Url.Matches(l))
                if (ConnectionImport.ParseAddress(m.Value) is { } a)
                    Add(i, a.Host, a.Port, a.User, m.Groups[1].Value.Equals("rdp", StringComparison.OrdinalIgnoreCase) ? Protocol.Rdp : Protocol.Ssh);
            foreach (Match m in UserAtHost.Matches(l))
            {
                // "name@company.com" with no SSH hint is an e-mail address, not a login.
                bool looksLikeMail = !Ipv4.IsMatch(m.Groups[2].Value) && Regex.IsMatch(m.Groups[2].Value, @"\.(com|org|net|de|io|co|edu|gov|at|ch|fr|uk)$", RegexOptions.IgnoreCase)
                                     && !SshWords.IsMatch(l);
                if (looksLikeMail) continue;
                Add(i, m.Groups[2].Value, int.TryParse(m.Groups[3].Value, out var p) ? p : null, m.Groups[1].Value, null);
            }
            foreach (Match m in HostLabel.Matches(l))
                if (ConnectionImport.ParseAddress(m.Groups[1].Value) is { } a)
                    Add(i, a.Host, a.Port, a.User, null);
            foreach (Match m in Ipv4.Matches(l))
                Add(i, m.Groups[1].Value, int.TryParse(m.Groups[2].Value, out var p) ? p : null, "", null);
            foreach (Match m in Fqdn.Matches(l))
            {
                var h = m.Groups[1].Value;
                if (Regex.IsMatch(h, @"^(www\.|.*\.(png|jpg|exe|txt|pdf|docx?|xlsx?)$)", RegexOptions.IgnoreCase)) continue;
                if (l.Contains('@' + h) || (l.Contains("://" + h) && !Url.IsMatch(l))) continue; // e-mail / web addresses
                if (PassLabel.Match(l) is { Success: true } pm && pm.Groups[1].Value.Contains(h)) continue; // a password that looks like a name
                Add(i, h, int.TryParse(m.Groups[2].Value, out var p) ? p : null, "", null);
            }
        }

        // 2. Table headers: "IP | User | Password" — later rows take values by column position.
        var headers = new Dictionary<int, List<(Role Role, Cell Cell)>>();
        for (int i = 0; i < rows.Count; i++)
        {
            var roles = rows[i].Cells.Select(c => (Role: HeaderWords.FirstOrDefault(h => h.Re.IsMatch(c.Text.Trim().TrimEnd(':'))).Role, Cell: c)).ToList();
            if (roles.Count(r => r.Role != Role.None) >= 2 && roles.Any(r => r.Role is Role.Host or Role.User or Role.Password)
                && hits.All(h => h.Row != i))
                headers[i] = roles;
        }

        var hostRows = hits.Select(h => h.Row).Distinct().OrderBy(x => x).ToList();
        foreach (var hit in hits)
        {
            var c = hit.C;
            var row = rows[hit.Row];
            var own = lines[hit.Row];
            int next = hostRows.FirstOrDefault(x => x > hit.Row, lines.Count);
            int end = Math.Min(next, hit.Row + 6);
            for (int k = hit.Row + 1; k < end; k++) if (rows[k].Cells.Count == 0 || headers.ContainsKey(k)) { end = k; break; } // page break / new table
            bool alone = hits.Count(h => h.Row == hit.Row) == 1; // more than one host on a row: don't guess from neighbours

            // a) Labels on the host's own row: "User: admin  Password: x"
            if (alone) ApplyLabels(hit, own);

            // b) Column under a table header
            var header = headers.Where(h => h.Key < hit.Row).OrderByDescending(h => h.Key).Select(h => h.Value).FirstOrDefault();
            if (alone && header != null && hit.Cell >= 0)
            {
                // Each header column takes the nearest cell on this row; the host's own cell is never a value,
                // even when the OCR missed a short header like "IP".
                var used = new HashSet<int> { hit.Cell };
                var pairs = header.Where(h => h.Role is not (Role.None or Role.Host))
                    .SelectMany(h => row.Cells.Select((cell, ci) => (h.Role, Ci: ci, Dist: Math.Abs(h.Cell.Center - cell.Center))))
                    .OrderBy(x => x.Dist);
                var done = new HashSet<Role>();
                foreach (var (role, ci, _) in pairs)
                {
                    if (used.Contains(ci) || done.Contains(role)) continue;
                    used.Add(ci);
                    done.Add(role);
                    Assign(hit, role, row.Cells[ci].Text);
                }
            }

            // c) Values written beside the host with no labels: "10.0.0.5   admin   Secret1"
            if (alone && header == null && hit.Cell >= 0)
            {
                var after = row.Cells.Skip(hit.Cell + 1).Select(x => x.Text).ToList();
                var before = row.Cells.Take(hit.Cell).Select(x => x.Text).ToList();
                // "admin:Secret1", "admin / Secret1", "admin Secret1" in a single cell
                if (after.Count == 1 && !hit.HasUser && !hit.HasPassword)
                {
                    var pair = Regex.Match(after[0], @"^([^\s:/]{1,64})\s*(?:[:/]|\s)\s*(\S+)$");
                    if (pair.Success && !IsPortOrProto(pair.Groups[1].Value) && !IsLabelWord(pair.Groups[1].Value))
                    {
                        Assign(hit, Role.User, pair.Groups[1].Value);
                        Assign(hit, Role.Password, pair.Groups[2].Value);
                        after.Clear();
                    }
                }
                after = after.Where(t => !AnyLabel.IsMatch(t)).ToList();
                foreach (var t in after.ToList())
                {
                    if (Regex.IsMatch(t, @"^:?\d{1,5}$") && int.TryParse(t.TrimStart(':'), out var p) && p is > 0 and < 65536) { if (c.Port == 0) c.Port = p; after.Remove(t); }
                    else if (Regex.IsMatch(t, @"^(rdp|ssh|sftp|windows|linux)$", RegexOptions.IgnoreCase)) { SetProto(hit, t); after.Remove(t); }
                }
                if (!hit.HasUser && after.Count >= 1) { Assign(hit, Role.User, after[0]); after.RemoveAt(0); }
                if (!hit.HasPassword && after.Count >= 1 && hit.HasUser) { Assign(hit, Role.Password, after[0]); after.RemoveAt(0); }
                if (before.Count > 0 && c.Name.Length == 0)
                {
                    var label = before[^1];
                    if (!AnyLabel.IsMatch(label) || !HostLabel.IsMatch(label + " x")) c.Name = CleanName(label);
                }
            }

            // d) Labelled lines below the host, up to the next host: "Login: admin" / "Password: x"
            for (int k = hit.Row + 1; k < end; k++) ApplyLabels(hit, lines[k]);

            // Protocol and the rest
            if (!hit.ExplicitProto)
            {
                bool rdp = RdpWords.IsMatch(own) || c.Port == 3389, ssh = SshWords.IsMatch(own) || c.Port == 22;
                if (!rdp && !ssh)
                {
                    var block = string.Join("\n", Enumerable.Range(hit.Row, end - hit.Row).Select(x => lines[x]));
                    rdp = RdpWords.IsMatch(block);
                    ssh = SshWords.IsMatch(block);
                }
                c.Protocol = rdp && !ssh ? Protocol.Rdp : Protocol.Ssh;
            }
            if (c.Protocol == Protocol.Rdp)
            {
                if (c.Domain.Length == 0 && c.Username.Contains('\\')) (c.Domain, c.Username) = DomainPicker.Split(c.Username);
                if (c.Domain.Length == 0)
                    for (int k = hit.Row; k < end; k++)
                        if (DomainLabel.Match(lines[k]) is { Success: true } dm) { c.Domain = dm.Groups[1].Value; break; }
            }
            if (c.Port == 0) c.Port = ConnectionImport.DefaultPort(c.Protocol);
            if (c.Name.Length == 0) c.Name = NameFor(c, own);
        }
        // The same connection mentioned again further down (a summary, a second page) is kept once,
        // preferring the mention that came with a password.
        return hits.Select(h => h.C)
            .GroupBy(c => (c.Host.ToLowerInvariant(), c.Port, c.Protocol, c.Username.ToLowerInvariant()))
            .Select(g => g.FirstOrDefault(c => c.Password != null) ?? g.First())
            .ToList();
    }

    private static void ApplyLabels(Hit hit, string line)
    {
        if (!hit.HasUser && UserLabel.Match(line) is { Success: true } um) Assign(hit, Role.User, um.Groups[1].Value);
        if (!hit.HasPassword && PassLabel.Match(line) is { Success: true } pm) Assign(hit, Role.Password, pm.Groups[1].Value);
        if (hit.C.Port == 0 && PortLabel.Match(line) is { Success: true } po && int.TryParse(po.Groups[1].Value, out var p) && p is > 0 and < 65536) hit.C.Port = p;
    }

    private static void Assign(Hit hit, Role role, string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value is "-" or "—" or "n/a" or "N/A") return;
        var c = hit.C;
        switch (role)
        {
            case Role.User when !hit.HasUser:
                c.Username = Regex.Replace(value.TrimEnd(',', ';'), @"\s+", "");
                hit.HasUser = true;
                break;
            case Role.Password when !hit.HasPassword:
                c.Password = value;
                hit.HasPassword = true;
                break;
            case Role.Port when c.Port == 0 && int.TryParse(value.TrimStart(':'), out var p) && p is > 0 and < 65536:
                c.Port = p;
                break;
            case Role.Domain when c.Domain.Length == 0:
                c.Domain = DomainPicker.Clean(value);
                break;
            case Role.Protocol:
                SetProto(hit, value);
                break;
            case Role.Name when c.Name.Length == 0:
                c.Name = CleanName(value);
                break;
        }
    }

    private static void SetProto(Hit hit, string text)
    {
        if (hit.ExplicitProto) return;
        bool rdp = RdpWords.IsMatch(text), ssh = SshWords.IsMatch(text);
        if (rdp == ssh) return;
        hit.C.Protocol = rdp ? Protocol.Rdp : Protocol.Ssh;
        hit.ExplicitProto = true;
    }

    private static bool IsLabelWord(string s) => HeaderWords.Any(h => h.Re.IsMatch(s.Trim()));

    private static bool IsPortOrProto(string s) => Regex.IsMatch(s, @"^(\d{1,5}|rdp|ssh|sftp)$", RegexOptions.IgnoreCase);

    private static string NormalizeCell(string text)
    {
        text = text.Trim();
        // "10.0.0. 5" -> "10.0.0.5"
        text = Regex.Replace(text, @"(\d)\s*\.\s+(\d)", "$1.$2");
        // Slashed zero read as 'Ø' ("webØ1", "1Ø.0.0.1")
        text = Regex.Replace(text, @"(?<=[A-Za-z0-9.])[ØøΟО]|[ØøΟО](?=[0-9.])", "0");
        // "CORP \ jsmith" -> "CORP\jsmith"
        text = Regex.Replace(text, @"(?<=\w)\s*\\\s*(?=\w)", "\\");
        // OCR often reads ':' as ';' after a label: "User name ; admin" -> "User name: admin"
        text = Regex.Replace(text, @"(?i)\b(user(?:\s*name)?|login|password|pass|pwd|host(?:\s*name)?|server|ip|port|domain)\s*;\s*", "$1: ");
        return text;
    }

    private static string CleanName(string s)
    {
        s = Regex.Replace(s, @"^[\s\-–•*·|>\d.)]+(?=[A-Za-z])", "");
        s = Regex.Replace(s, s.Contains('(') ? @"[\s:=\-–|>\[\]@]+$" : @"[\s:=\-–|>()\[\]@]+$", "").Trim();
        return s.Length > 48 ? s[..48] : s;
    }

    /// <summary>A label written before the address on the same line ("Web server: 10.0.0.5"), otherwise the short host name.</summary>
    private static string NameFor(ImportedConnection c, string line)
    {
        int idx = line.IndexOf(c.Host, StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            var before = line[..idx].Split('\t').Last();
            before = Regex.Replace(before, @"(?i)\b(host(?:\s*name)?|server|address|ip(?:\s*address)?|rdp|ssh|sftp|url)\s*[:=]?\s*$", "");
            before = CleanName(before);
            if (before.Length is > 1 and <= 40 && !before.Contains("://") && !UserLabel.IsMatch(before) && !before.Contains('@') && Regex.IsMatch(before, "[A-Za-z]"))
                return before;
        }
        if (IPAddress.TryParse(c.Host, out _)) return c.Host;
        return c.Host.Split('.')[0];
    }
}
