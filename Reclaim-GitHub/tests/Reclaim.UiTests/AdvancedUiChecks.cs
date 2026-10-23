using System.Reflection;
using System.Drawing.Imaging;
using System.Collections.Concurrent;
using Reclaim.App;
using Reclaim.Core;

internal static partial class Program
{
    private static void AdvancedUiChecks(ScanResult scan)
    {
        using var form = new MainForm(); _ = form.Handle; form.PerformLayout();
        var mode = Field<ComboBox>(form, "mode"); Check(mode.Items.Count == 5, "Lost-partition and camera scan modes are selectable");
        foreach (int index in new[] { 3, 4 }) { mode.SelectedIndex = index; form.PerformLayout(); AssertAlignment(form); }
        var view = new ScanResult { Source = scan.Source, SourceLength = scan.SourceLength, ImageLastWriteUtc = scan.ImageLastWriteUtc };
        void Set(string name, object? value) => form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);
        Set("liveView", view); Set("scanning", true);
        form.GetType().GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [true]);
        var queue = Field<ConcurrentQueue<RecoveryItem>>(form, "liveItems"); queue.Enqueue(scan.Items.First(x => x.CanRecover));
        form.GetType().GetMethod("DrainLiveResults", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [true]);
        var gallery = Field<Control>(form, "gallery"); Check((int)gallery.GetType().GetProperty("ItemCount")!.GetValue(gallery)! == 1, "Published results appear while the scan is active");
        gallery.GetType().GetMethod("ToggleCurrent")!.Invoke(gallery, null); Check(Field<Control>(form, "recover").Enabled, "Live results can be selected for recovery before scan completion");
        Check(Field<Control>(form, "openFile").Enabled && !Field<Control>(form, "location").Enabled, "Live previews remain enabled while the source is locked in the UI");
        Set("scanning", false); form.GetType().GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [false]);
        using var tools = new RecoveryTools(scan.Source, scan, scan.Items.First(x => x.CanRecover)); _ = tools.Handle; tools.PerformLayout();
        Check(Descendants(tools).OfType<TabPage>().Count() == 4, "Recovery tools exposes health, validation/repair, bootable tools and guide tabs");
        Check(Descendants(tools).Any(x => x.Text == "Create rescue ISO"), "Boot media has an application action for the ADK ISO builder");
        string folder = Path.Combine(Path.GetTempPath(), "reclaim-photo-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            string original = Path.Combine(folder, "photo.jpg"), repaired = Path.Combine(folder, "repaired.png");
            using (var photo = new Bitmap(24, 18)) { using var g = Graphics.FromImage(photo); g.Clear(Color.CornflowerBlue); photo.Save(original, ImageFormat.Jpeg); }
            byte[] bytes = File.ReadAllBytes(original); File.WriteAllBytes(original, bytes[..^2]);
            string note = PhotoRepair.Repair(original, repaired); using var decoded = Image.FromFile(repaired);
            Check(decoded.Width == 24 && decoded.Height == 18 && note.Contains("end marker"), "Photo repair restores a missing JPEG end marker and validates a new PNG");
            Check(File.ReadAllBytes(original).AsSpan().SequenceEqual(bytes.AsSpan(0, bytes.Length - 2)), "Photo repair leaves the original bytes unchanged");
            decoded.Dispose();
            bool failed = false; string bad = Path.Combine(folder, "bad.jpg"); File.WriteAllBytes(bad, [1, 2, 3]);
            try { PhotoRepair.Repair(bad, Path.Combine(folder, "bad.png")); } catch (ArgumentException) { failed = true; }
            Check(failed && !File.Exists(Path.Combine(folder, "bad.png")), "Undecodable photo data is rejected without a fake repaired file");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("9 advanced UI/photo checks passed.");
    }
}
