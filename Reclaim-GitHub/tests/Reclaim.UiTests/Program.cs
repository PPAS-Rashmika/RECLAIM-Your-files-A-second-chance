using System.Reflection;
using Reclaim.App;
using Reclaim.Core;
using Microsoft.Web.WebView2.WinForms;

internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--buttons")) { ButtonChecks(); return; }
        string image = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/tests/sample-ntfs.img");
        var scan = RecoveryEngine.Scan(new(image, true), ScanMode.Deep, null, CancellationToken.None);
        if (args.Contains("--layout")) { LayoutChecks(scan); return; }
        using var form = new MainForm();
        _ = form.Handle; form.LoadPreview(scan); form.PerformLayout();
        var gallery = Field<Control>(form, "gallery");
        WaitPreview(gallery);
        int Count() => (int)gallery.GetType().GetProperty("ItemCount")!.GetValue(gallery)!;
        bool HasImage() => (bool)gallery.GetType().GetProperty("HasImage")!.GetValue(gallery)!;
        var search = Field<TextBox>(form, "search"); var filter = Field<ComboBox>(form, "condition");
        Check(HasImage(), "The file gallery automatically decodes an inline image thumbnail");
        Check(Count() == scan.Items.Count, "Every scan result appears in the virtual gallery");
        Check(!Descendants(form).Any(x => x.GetType().Name == "FilePreview"), "The results workspace has no separate preview pane");
        search.Text = "notes"; Check(Count() == 1, "Filename search filters the preview cards"); WaitPreview(gallery);
        Check(gallery.GetType().GetProperty("PreviewBody")!.GetValue(gallery)!.ToString()!.Contains("සිංහල"), "Text cards display recovered Unicode contents inline");
        Check(!HasImage(), "Text cards do not inherit the previous image");
        Invoke(form, "ToggleVisibleSelection");
        Check(Field<Label>(form, "selectedValue").Text == "1", "Visible selection updates the recovery count");
        Check(Field<Control>(form, "recover").Enabled, "Checked cards enable recovery");
        Invoke(form, "ToggleVisibleSelection"); Check(!Field<Control>(form, "recover").Enabled, "Clearing checked cards disables recovery");
        search.Text = ""; filter.SelectedIndex = 4; Check(Count() == 1, "Unavailable-condition filtering works");
        gallery.GetType().GetMethod("ToggleCurrent")!.Invoke(gallery, null);
        Check(Field<Label>(form, "selectedValue").Text == "0", "An unavailable card cannot be checked");
        filter.SelectedIndex = 0; gallery.GetType().GetMethod("FocusItem")!.Invoke(gallery, ["family.png"]);
        Check(Field<Label>(form, "detailTitle").Text.Contains("family.png"), "Focused card updates the recovery details");
        Field<TextBox>(form, "location").Text = image + ".changed";
        Check(Count() == 0 && !Field<Control>(form, "recover").Enabled, "Changing the source clears stale cards and selections");
        WaitPreview(gallery); Check((int)gallery.GetType().GetProperty("CachedCount")!.GetValue(gallery)! == 0, "Changing the source releases cached thumbnail images");
        form.Size = form.MinimumSize; form.PerformLayout();
        Check(gallery.Width > 900 && gallery.Height > 200, "Large inline previews use the full workspace at minimum window size");
        GalleryChecks(form, gallery, scan);
        Console.WriteLine("15 gallery workflow checks passed. No live drive was scanned and no window was opened.");
        if (args.Contains("--browser")) BrowserChecks(image, scan);
    }
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    private static void Invoke(object o, string name) => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, null);
    private static void WaitPreview(Control preview)
    {
        var pending = (Task)preview.GetType().GetProperty("Pending")!.GetValue(preview)!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!pending.IsCompleted && watch.Elapsed.TotalSeconds < 10) { Application.DoEvents(); Thread.Sleep(10); }
        if (!pending.IsCompleted) throw new Exception("Preview did not finish."); pending.GetAwaiter().GetResult();
    }
    private static Control NewFileViewer() => (Control)Activator.CreateInstance(typeof(MainForm).Assembly.GetType("Reclaim.App.FilePreview")!)!;
    private static void BrowserChecks(string image, ScanResult scan)
    {
        using var form = new Form { Opacity = 0, ShowInTaskbar = false, Size = new Size(960, 760) };
        var preview = NewFileViewer(); form.Controls.Add(preview); form.Show(); form.PerformLayout();
        var pdf = scan.Items.Single(x => x.Name.EndsWith(".pdf"));
        preview.GetType().GetMethod("ShowItem")!.Invoke(preview, [scan, pdf]); WaitPreview(preview);
        WaitPreview(preview);
        var browser = Field<WebView2?>(preview, "browser") ?? throw new Exception("WebView2 did not initialize: " + preview.GetType().GetProperty("PreviewBody")!.GetValue(preview));
        Check(browser.CoreWebView2 is not null, "Microsoft WebView2 runtime initializes for embedded preview");
        bool? success = null; ulong navigationId = 0;
        browser.CoreWebView2!.NavigationStarting += (_, e) => navigationId = e.NavigationId;
        browser.CoreWebView2.NavigationCompleted += (_, e) => { if (e.NavigationId == navigationId) success = e.IsSuccess; };
        string resource = Field<string>(preview, "resourceUrl"); browser.CoreWebView2.Navigate(resource);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (success != true && watch.Elapsed.TotalSeconds < 20) { Application.DoEvents(); Thread.Sleep(10); }
        Check(success == true, "PDF preview navigation succeeds using the intercepted local file stream");
        var type = browser.CoreWebView2.ExecuteScriptAsync("document.contentType"); Wait(type, 10);
        Check(type.Result.Contains("application/pdf"), "Browser receives the recovered PDF as a PDF document");
        string mediaPath = Path.Combine(Path.GetDirectoryName(image)!, "signatures.raw");
        var media = RecoveryEngine.Scan(new(mediaPath, true), ScanMode.Deep, null, CancellationToken.None);
        var wav = media.Items.Single(x => x.Name.EndsWith(".wav"));
        preview.GetType().GetMethod("ShowItem")!.Invoke(preview, [media, wav]); WaitPreview(preview);
        browser = Field<WebView2>(preview, "browser"); watch.Restart(); int ready = 0;
        while (watch.Elapsed.TotalSeconds < 15)
        {
            var state = browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('audio')?.readyState ?? 0"); Wait(state, 5);
            if (int.TryParse(state.Result, out ready) && ready >= 1) break;
            Application.DoEvents(); Thread.Sleep(50);
        }
        if (ready < 1)
        {
            var detail = browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({url:location.href, html:document.body.innerHTML, src:document.querySelector('audio')?.currentSrc,error:document.querySelector('audio')?.error?.code})"); Wait(detail, 5); Console.WriteLine("Media diagnostic: " + detail.Result);
        }
        Check(ready >= 1, "Audio preview decodes the recovered WAV metadata from the local stream");
        Console.WriteLine("4 embedded browser checks passed. Only synthetic images were read; the temporary invisible test window was closed.");
    }
    private static void Wait(Task task, int seconds)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed.TotalSeconds < seconds) { Application.DoEvents(); Thread.Sleep(10); }
        if (!task.IsCompleted) throw new Exception("Browser operation timed out."); task.GetAwaiter().GetResult();
    }
    private static void Check(bool value, string description)
    { if (!value) throw new Exception(description); Console.WriteLine("PASS " + description); }
    private static void LayoutChecks(ScanResult scan)
    {
        foreach (int targetDpi in new[] { 96, 120, 144, 192 })
        {
            using var form = new MainForm(); _ = form.Handle; form.LoadPreview(scan); form.PerformLayout();
            float factor = (float)targetDpi / form.DeviceDpi;
            // Simulate the geometry and text sizes for other display densities; do not change the user's display settings.
            if (Math.Abs(factor - 1) > .01f)
            {
                var fonts = Descendants(form).Select(x => (Control: x, Font: x.Font)).ToList();
                form.AutoScaleMode = AutoScaleMode.None; form.SuspendLayout(); form.Scale(new SizeF(factor, factor));
                foreach (var entry in fonts) entry.Control.Font = new Font(entry.Font.FontFamily, entry.Font.SizeInPoints * factor, entry.Font.Style);
                form.ResumeLayout(true);
            }
            foreach (string field in new[] { "foundValue", "availableValue", "selectedValue" })
            {
                var label = Field<Label>(form, field);
                foreach (string count in new[] { "3,783", "123,456,789", "0" })
                {
                    label.Text = count; form.PerformLayout(); var measured = label.GetPreferredSize(Size.Empty);
                    if (label.ClientSize.Height < measured.Height || label.ClientSize.Width < measured.Width)
                        throw new Exception($"{targetDpi} DPI {field}: text {measured}, available {label.ClientSize}");
                }
            }
            Check(true, $"{targetDpi / 96f:P0} layout simulation shows every digit, including large counts");
            foreach (Control button in Descendants(form).Where(x => x.GetType().Name == "ActionButton" && x.Tag as string != "SkipPreview"))
            {
                var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                if (button.ClientSize.Height < text.Height + 4 || button.ClientSize.Width < text.Width + 8)
                    throw new Exception($"{targetDpi} DPI button '{button.Text}' text {text}, available {button.ClientSize}");
            }
            var headline = Descendants(form).OfType<Label>().Single(x => x.Name == "Headline");
            if (headline.Height < headline.GetPreferredSize(Size.Empty).Height)
                throw new Exception($"{targetDpi} DPI headline was clipped");
            Check(true, $"{targetDpi / 96f:P0} layout simulation gives every button enough space for its caption");
            AssertAlignment(form);
            Check(true, $"{targetDpi / 96f:P0} layout simulation aligns field/button rows, card edges and footer centers without clipped instructions");
        }
        Console.WriteLine("12 clipping/alignment regression checks passed. Other DPIs were simulated without changing display settings.");
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
