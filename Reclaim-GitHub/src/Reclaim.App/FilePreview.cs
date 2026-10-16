using System.Net;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Reclaim.Core;

namespace Reclaim.App;

internal sealed class FilePreview : Panel
{
    private readonly Label title = Theme.Label("File preview", 13, Theme.Ink, FontStyle.Bold);
    private readonly Label caption = Theme.Label("Select a file to inspect its contents", 9, Theme.Muted);
    private readonly Label note = Theme.Label("Preview reads the source without saving recovered data.", 8, Theme.Muted);
    private readonly TextBox text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = Color.White, Font = Theme.Font(10) };
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(244, 247, 249) };
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White };
    private readonly ActionButton enableMedia = new("Enable PDF / media preview");
    private readonly Dictionary<string, ActionButton> tabs = [];
    private readonly ToolTip accessibleTitle = new();
    private WebView2? browser;
    private readonly List<Stream> browserStreams = [];
    private ScanResult? scan;
    private RecoveryItem? item;
    private string view = "Preview", resourceUrl = "", pageUrl = "", mediaMarkup = "";
    private int revision;
    private string? workspace;
    private bool initializing;
    private ulong navigationId;
    public Task Pending { get; private set; } = Task.CompletedTask;
    public string PreviewBody => text.Text;
    public bool HasImage => picture.Image is not null;
    public FilePreview()
    {
        SetStyle(ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill; Margin = new Padding(12, 0, 0, 0); BackColor = Color.White; Padding = new Padding(1);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = new Padding(15, 12, 15, 10) };
        foreach (int height in new[] { 30, 28, 48, 0, 40 }) layout.RowStyles.Add(height == 0 ? new(SizeType.Percent, 100) : new(SizeType.Absolute, height));
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(caption, 0, 1); layout.Controls.Add(content, 0, 3); layout.Controls.Add(note, 0, 4);
        title.AutoEllipsis = true; caption.AutoEllipsis = true;
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
        for (int i = 0; i < 3; i++) { navigation.ColumnStyles.Add(new(SizeType.Percent, 33.333f)); if (i < 2) navigation.ColumnStyles.Add(new(SizeType.Absolute, 6)); }
        int tabIndex = 0;
        foreach (string tab in new[] { "Preview", "Details", "Hex" })
        {
            var button = new ActionButton(tab) { Dock = DockStyle.Fill, Selected = tab == view, Margin = Padding.Empty };
            tabs[tab] = button; button.Click += (_, _) => { view = tab; Pending = LoadContent(); }; navigation.Controls.Add(button, tabIndex++ * 2, 0);
        }
        content.Controls.Add(text); content.Controls.Add(picture); picture.Visible = false;
        enableMedia.Dock = DockStyle.Bottom; enableMedia.Height = 40; enableMedia.Visible = false; enableMedia.Tag = "SkipPreview"; enableMedia.Click += async (_, _) =>
        {
            if (scan is null) return;
            using var folder = new FolderBrowserDialog { Description = "Choose a preview workspace on another drive. PDF/media viewing creates a browser cache here.", UseDescriptionForTitle = true };
            if (folder.ShowDialog(FindForm()) != DialogResult.OK) return;
            try { RecoveryEngine.ValidateDestination(scan.Source, folder.SelectedPath); StopBrowser(); browser?.Dispose(); browser = null; workspace = folder.SelectedPath; await LoadContent(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { text.Text = ex.Message; }
        };
        content.Controls.Add(enableMedia); layout.Controls.Add(navigation, 0, 2); Controls.Add(layout);
        Paint += (_, e) => { using var pen = new Pen(Theme.Line); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); };
        text.Text = "Select a recovered file to see its preview.\r\n\r\nImages • text • Office document text • ZIP contents • PDF • audio / video\r\n\r\nOther formats offer file details and a byte preview.";
    }
    public void ShowItem(ScanResult? value, RecoveryItem? file)
    {
        if (scan == value && item == file) return;
        if (scan != value && browser is not null) { StopBrowser(); browser.Dispose(); browser = null; workspace = null; }
        scan = value; item = file; Pending = LoadContent();
    }
    private async Task LoadContent()
    {
        int current = ++revision; var selected = item; var source = scan;
        StopBrowser(); picture.Visible = false; picture.Image?.Dispose(); picture.Image = null;
        picture.Tag = "SkipPreview"; text.Tag = null;
        text.Visible = true; enableMedia.Visible = false; text.Font = Theme.Font(10); text.WordWrap = true;
        foreach (var tab in tabs) { tab.Value.Selected = tab.Key == view; tab.Value.Invalidate(); }
        title.Text = selected?.Name ?? "File preview"; caption.Text = selected is null ? "Select a file to inspect its contents" : Categories.Size(selected.Size) + " · " + selected.Category + " · " + selected.Condition;
        accessibleTitle.SetToolTip(title, selected?.Name ?? "Select a file to preview");
        note.Text = selected?.State is RecoveryState.AtRisk or RecoveryState.Signature ? "Review this preview. Missing or reused data can produce incomplete content." : "Preview reads file bytes without exporting the file.";
        if (selected is null || source is null) { text.Text = "Select a file in the results to see its preview.\r\n\r\nImages, text, documents, archives and supported media appear here."; return; }
        if (view == "Details")
        {
            text.Text = $"FILE NAME\r\n{selected.Name}\r\n\r\nORIGINAL LOCATION\r\n{selected.OriginalPath}\r\n\r\nRECOVERY METHOD\r\n{selected.Method}\r\n\r\nSIZE\r\n{selected.Size:N0} bytes\r\n\r\nCONDITION\r\n{selected.Condition}\r\n\r\nEVIDENCE\r\n{selected.Note}\r\n\r\nPARTITION\r\n{selected.Volume?.FileSystem ?? source.FileSystem} · offset {selected.Volume?.Offset ?? source.VolumeOffset:N0}"; return;
        }
        if (!selected.CanRecover) { text.Text = selected.Note; return; }
        text.Text = "Reading preview…";
        try
        {
            string extension = Path.GetExtension(selected.Name).ToLowerInvariant();
            if (view == "Hex")
            {
                string hex = await Task.Run(() => PreviewText.Hex(PreviewText.Head(source, selected)));
                if (current != revision || IsDisposed) return;
                text.Font = new Font("Consolas", 9); text.WordWrap = false; text.Text = hex; note.Text = "First 4 KB. All formats can be inspected as bytes."; return;
            }
            if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico" }.Contains(extension) && selected.Size <= 32 * 1024 * 1024)
            {
                var image = await Task.Run(() =>
                {
                    using var stream = RecoveryContent.Open(source, selected); using var decoded = Image.FromStream(stream, true, true);
                    if ((long)decoded.Width * decoded.Height > 25000000) throw new InvalidDataException("Image is too large for a safe preview.");
                    return new Bitmap(decoded);
                });
                if (current != revision || IsDisposed) { image.Dispose(); return; }
                picture.Image = image; picture.Visible = true; text.Visible = false; picture.BringToFront();
                picture.Tag = null; text.Tag = "SkipPreview";
                note.Text = $"{image.Width:N0} × {image.Height:N0} px · fit to window · first frame / page"; return;
            }
            if (extension == ".pdf" || selected.Category is FileCategory.Audio or FileCategory.Videos)
            {
                if (extension == ".pdf" && selected.Size > 64 * 1024 * 1024) { text.Text = "PDF exceeds the 64 MB preview limit. Use Details or Hex, or recover it to view the full document."; return; }
                if (FindForm()?.Visible != true) { text.Text = "PDF / media preview is available when the app is opened. Codec support depends on the installed browser runtime."; return; }
                await LoadMedia(source, selected, current); return;
            }
            string preview = await Task.Run(() => PreviewText.Read(source, selected));
            if (current == revision && !IsDisposed) text.Text = preview;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or System.Xml.XmlException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        { if (current == revision && !IsDisposed) text.Text = "Preview could not decode this file.\r\n\r\n" + ex.Message + "\r\n\r\nUse Details or Hex to inspect the surviving data."; }
    }
    private async Task LoadMedia(ScanResult source, RecoveryItem file, int current)
    {
        if (initializing) { text.Text = "Browser preview is starting. Select Preview again in a moment."; return; }
        try
        {
            string cacheRoot = workspace ?? AppContext.BaseDirectory;
            RecoveryEngine.ValidateDestination(source.Source, cacheRoot);
            string profile = Path.Combine(cacheRoot, ".reclaim-preview-cache");
            if (browser is null)
            {
                initializing = true;
                var creating = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = Color.White };
                browser = creating; content.Controls.Add(creating);
                var environment = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --disable-sync --no-first-run"));
                if (creating.IsDisposed || current != revision) return;
                await creating.EnsureCoreWebView2Async(environment);
                if (creating.IsDisposed || current != revision) return;
                var core = creating.CoreWebView2;
                core.Settings.IsScriptEnabled = false; core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false; core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += ResourceRequested;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.NavigationStarting += (_, e) => { if (e.Uri != resourceUrl && e.Uri != pageUrl && e.Uri != "about:blank") e.Cancel = true; else navigationId = e.NavigationId; };
                core.NavigationCompleted += (_, e) => { if (e.NavigationId == navigationId && !e.IsSuccess) { creating.Visible = false; text.Visible = true; text.Text = "The browser could not display this format (" + e.WebErrorStatus + "). Use Details or Hex; media codecs and PDF support depend on the installed runtime."; } };
            }
            if (current != revision || IsDisposed || browser?.CoreWebView2 is null) return;
            resourceUrl = "https://reclaim.preview/" + Guid.NewGuid().ToString("N") + Path.GetExtension(file.Name).ToLowerInvariant();
            text.Visible = false; browser.Visible = true; browser.BringToFront();
            if (Path.GetExtension(file.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) browser.CoreWebView2.Navigate(resourceUrl);
            else
            {
                string tag = file.Category == FileCategory.Audio ? "audio" : "video";
                mediaMarkup = $"<!doctype html><html><head><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; media-src https://reclaim.preview; style-src 'unsafe-inline'\"></head><body style='margin:0;background:#f4f7f9;display:flex;align-items:center;justify-content:center;height:100vh'><{tag} controls preload='metadata' style='width:100%;max-height:100%' src='{resourceUrl}'></{tag}></body></html>";
                pageUrl = resourceUrl + "/player.html";
                browser.CoreWebView2.Navigate(pageUrl);
            }
            text.Visible = false; browser.Visible = true; browser.BringToFront(); note.Text = "Local streaming preview · no file export · browser cache on " + Path.GetPathRoot(cacheRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException)
        {
            text.Text = "PDF / media preview needs Microsoft Edge WebView2 Runtime and a cache folder on another drive.\r\n\r\n" + ex.Message;
            enableMedia.Visible = true; enableMedia.BringToFront();
            if (browser?.CoreWebView2 is null) { browser?.Dispose(); browser = null; }
        }
        finally { initializing = false; }
    }
    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = browser!.CoreWebView2;
        // Completed response streams are no longer needed when a later request arrives.
        for (int i = browserStreams.Count - 1; i >= 0; i--)
        {
            try { if (browserStreams[i].Position < browserStreams[i].Length) continue; }
            catch (ObjectDisposedException) { }
            browserStreams[i].Dispose(); browserStreams.RemoveAt(i);
        }
        if (e.Request.Uri == pageUrl && pageUrl.Length > 0)
        {
            var html = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(mediaMarkup)); browserStreams.Add(html);
            e.Response = core.Environment.CreateWebResourceResponse(html, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nContent-Length: " + html.Length + "\r\nCache-Control: no-store\r\n"); return;
        }
        if (e.Request.Uri != resourceUrl || scan is null || item is null)
        {
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Length: 0\r\n"); return;
        }
        Stream? opened = null;
        try
        {
            var stream = opened = RecoveryContent.Open(scan, item); long start = 0, end = item.Size - 1; bool partial = false;
            if (e.Request.Headers.Contains("Range"))
            {
                string range = e.Request.Headers.GetHeader("Range");
                var parts = range.StartsWith("bytes=") ? range[6..].Split('-') : [];
                if (parts.Length != 2 || range.Contains(',') || item.Size == 0) throw new IOException("Unsupported preview range.");
                if (parts[0].Length == 0) { if (!long.TryParse(parts[1], out long suffix) || suffix <= 0) throw new IOException(); start = Math.Max(0, item.Size - suffix); }
                else { if (!long.TryParse(parts[0], out start)) throw new IOException(); if (parts[1].Length > 0 && !long.TryParse(parts[1], out end)) throw new IOException(); }
                if (start < 0 || start >= item.Size || end < start) throw new IOException("Preview range is outside the file.");
                end = Math.Min(end, item.Size - 1); partial = true;
            }
            if (item.Category is FileCategory.Audio or FileCategory.Videos && end - start + 1 > 4 * 1024 * 1024)
            { end = start + 4 * 1024 * 1024 - 1; partial = true; }
            var bounded = new PreviewRange(stream, start, Math.Max(0, end - start + 1)); browserStreams.Add(bounded); opened = null;
            Stream response = bounded;
            if (item.Size <= 4 * 1024 * 1024)
            {
                var memory = new MemoryStream(); bounded.CopyTo(memory); memory.Position = 0; response = memory;
                browserStreams.Add(memory); bounded.Dispose();
            }
            string type = Mime(item.Name); string headers = $"Content-Type: {type}\r\nContent-Length: {bounded.Length}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n";
            if (type == "application/pdf") headers += "Content-Disposition: inline; filename=preview.pdf\r\n";
            if (partial) headers += $"Content-Range: bytes {start}-{end}/{item.Size}\r\n";
            e.Response = core.Environment.CreateWebResourceResponse(response, partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { opened?.Dispose(); e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 416, "Unavailable", "Content-Length: 0\r\n"); }
    }
    private static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    { ".pdf" => "application/pdf", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".m4a" => "audio/mp4", ".flac" => "audio/flac", ".ogg" => "audio/ogg", ".opus" => "audio/ogg", ".mp4" or ".m4v" => "video/mp4", ".mov" => "video/quicktime", ".webm" => "video/webm", ".avi" => "video/x-msvideo", _ => "application/octet-stream" };
    private void StopBrowser()
    {
        navigationId = 0;
        if (browser?.CoreWebView2 is not null) { browser.CoreWebView2.Stop(); browser.Visible = false; }
        foreach (var stream in browserStreams) stream.Dispose(); browserStreams.Clear(); resourceUrl = ""; pageUrl = "";
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { revision++; StopBrowser(); picture.Image?.Dispose(); picture.Image = null; browser?.Dispose(); accessibleTitle.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class PreviewRange(Stream source, long start, long length) : Stream
{
    private long position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
    public override int Read(byte[] buffer, int offset, int count)
    { if (position >= length) return 0; source.Position = start + position; int n = source.Read(buffer, offset, (int)Math.Min(count, length - position)); position += n; return n; }
    public override long Seek(long offset, SeekOrigin origin) => Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length, _ => throw new ArgumentException() }) + offset);
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
