using System.Reflection;
using Reclaim.App;
using Reclaim.Core;

internal static partial class Program
{
    private static void TypeFilterChecks(ScanResult source)
    {
        var scan = new ScanResult { Source = source.Source, SourceLength = source.SourceLength, ImageLastWriteUtc = source.ImageLastWriteUtc,
            VolumeOffset = source.VolumeOffset, VolumeLength = source.VolumeLength, BootHash = source.BootHash, ClusterSize = source.ClusterSize, FileSystem = source.FileSystem };
        RecoveryItem Item(string name, RecoveryState state = RecoveryState.Resident) => new()
        { Name = name, OriginalPath = name, Size = 3, InitializedSize = 3, Method = "Synthetic format fixture", State = state, ResidentData = [1, 2, 3] };
        scan.Items.AddRange([Item("a.JPG"), Item("b.jpg"), Item("photo.png"), Item("report.pdf"), Item("movie.mp4"), Item("bundle.ZIP"), Item("novel.custom"), Item("LICENSE"), Item("bad.jpg", RecoveryState.Unsupported)]);
        using var form = new MainForm { Opacity = 0, ShowInTaskbar = false };
        form.LoadPreview(scan); form.Show(); Application.DoEvents(); form.PerformLayout();
        var formats = Field<ComboBox>(form, "fileType"); var selected = Field<HashSet<Guid>>(form, "selected");
        string? Key(object choice) => (string?)choice.GetType().GetProperty("Extension")!.GetValue(choice);
        void Choose(string? extension) => formats.SelectedIndex = Enumerable.Range(0, formats.Items.Count).Single(i => Key(formats.Items[i]!) == extension);
        int Count() => Field<List<RecoveryItem>>(form, "visibleItems").Count;
        var action = Field<Control>(form, "selectVisible");
        Check(formats.Items.Count == 8 && formats.Items.Cast<object>().Count(x => Key(x) == ".jpg") == 1, "Format choices merge extension case and include custom/extensionless files");
        Choose(".jpg"); Check(Count() == 3 && Field<List<RecoveryItem>>(form, "visibleItems").All(x => x.Format == "JPG"), "JPG filtering includes uppercase names and excludes other formats");
        ((IButtonControl)action).PerformClick(); Check(selected.Count == 2 && !selected.Contains(scan.Items.Single(x => x.Name == "bad.jpg").Id), "Selecting a format checks every matching recoverable file and skips unavailable files");
        Check(action.Text == "Clear visible", "Bulk selection clearly changes into a clear action");
        Choose(".png"); ((IButtonControl)action).PerformClick(); Check(selected.Count == 3 && Field<Label>(form, "selectedValue").Text == "3", "Selections survive switching formats and add to the total");
        Field<TextBox>(form, "search").Text = "no match";
        Check(Count() == 0 && !action.Enabled && Key(formats.SelectedItem!) == ".png", "An empty search preserves the chosen format and disables bulk selection");
        Field<TextBox>(form, "search").Text = ""; ((IButtonControl)action).PerformClick(); Check(selected.Count == 2, "Clearing one format leaves selections from another format intact");
        Choose(".jpg"); var added = Item("new.jpg"); scan.Items.Add(added);
        form.GetType().GetMethod("ApplyFilters", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [true]);
        Check(Count() == 4 && Key(formats.SelectedItem!) == ".jpg" && selected.Count == 2 && !selected.Contains(added.Id), "Live results refresh type counts without changing the filter or silently selecting arrivals");
        var categories = Field<System.Collections.IDictionary>(form, "categoryButtons"); ((IButtonControl)categories["Documents"]!).PerformClick();
        Check(Key(formats.SelectedItem!) is null && Count() == 1 && Field<List<RecoveryItem>>(form, "visibleItems")[0].Name == "report.pdf", "Changing a main category resets an incompatible format filter");
        ((IButtonControl)categories["Other"]!).PerformClick(); Choose(""); Check(Count() == 1 && Field<List<RecoveryItem>>(form, "visibleItems")[0].Name == "LICENSE", "No extension is an independently selectable format");
        Choose(".custom"); Check(Count() == 1 && Field<List<RecoveryItem>>(form, "visibleItems")[0].Name == "novel.custom", "Unknown/custom extensions remain individually selectable");
        Field<TextBox>(form, "location").Text = "changed-source";
        Check(formats.Items.Count == 1 && Key(formats.SelectedItem!) is null && selected.Count == 0 && !action.Enabled, "Changing sources clears stale format choices and checked files");
        WaitPreview(Field<Control>(form, "gallery"));
        Console.WriteLine("12 file-format filtering and selection checks passed.");
    }
}
