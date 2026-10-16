using System.Reflection;
using Reclaim.App;
using Reclaim.Core;

internal static partial class Program
{
    private static void GalleryChecks(MainForm form, Control gallery, ScanResult scan)
    {
        form.LoadPreview(scan); WaitPreview(gallery);
        int start = (int)gallery.GetType().GetProperty("CachedCount")!.GetValue(gallery)!;
        var large = new ScanResult { Source = scan.Source, SourceLength = scan.SourceLength, ImageLastWriteUtc = scan.ImageLastWriteUtc,
            BootHash = scan.BootHash, VolumeOffset = scan.VolumeOffset, VolumeLength = scan.VolumeLength, ClusterSize = scan.ClusterSize, FileSystem = scan.FileSystem };
        var original = scan.Items.Single(x => x.Name == "notes.txt");
        byte[] bytes = PreviewText.Head(scan, original);
        for (int i = 0; i < 500; i++) large.Items.Add(new RecoveryItem { Name = $"notes-{i:D3}.txt", OriginalPath = "Documents", Method = "Test resident data", Size = bytes.Length, InitializedSize = bytes.Length, ResidentData = bytes, State = RecoveryState.Resident });
        form.LoadPreview(large); WaitPreview(gallery);
        Check((int)gallery.GetType().GetProperty("CachedCount")!.GetValue(gallery)! < 30, "A 500-file gallery decodes only the visible preview cards");
        gallery.GetType().GetMethod("FocusItem")!.Invoke(gallery, ["notes-499.txt"]); WaitPreview(gallery);
        string body = gallery.GetType().GetProperty("PreviewBody")!.GetValue(gallery)!.ToString()!;
        Check(body.Contains("සිංහල"), "Scrolling to the last file loads its real content preview");
        gallery.GetType().GetMethod("ToggleCurrent")!.Invoke(gallery, null);
        Check(Field<Label>(form, "selectedValue").Text == "1", "A preview-card checkbox selects the correct offscreen file");
        var selected = Field<HashSet<Guid>>(form, "selected");
        Check(selected.Contains(large.Items.Last().Id), "Recovery selection uses file identity after virtual scrolling");
        form.LoadPreview(scan); WaitPreview(gallery);
        Check((int)gallery.GetType().GetProperty("CachedCount")!.GetValue(gallery)! <= Math.Max(start, 20), "Returning to another scan discards the old gallery cache");
        Console.WriteLine("5 virtual gallery checks passed.");
    }
}
