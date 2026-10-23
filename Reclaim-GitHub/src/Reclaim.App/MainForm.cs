using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Collections.Concurrent;
using Reclaim.Core;

namespace Reclaim.App;

public sealed class MainForm : Form
{
    private readonly TextBox location = new();
    private readonly ComboBox mode = new();
    private readonly ComboBox drives = new();
    private readonly TextBox search = new();
    private readonly ComboBox condition = new();
    private readonly ComboBox fileType = new();
    private readonly ActionButton selectVisible = new("Select visible");
    private readonly ActionButton scan = new("Start scan", true);
    private readonly ActionButton cancel = new("Stop");
    private readonly ActionButton folderBrowse = new("Choose folder");
    private readonly ActionButton imageBrowse = new("Disk image");
    private readonly ActionButton recover = new("Recover selected", true);
    private readonly ActionButton inspect = new("Inspect bytes");
    private readonly ActionButton openFile = new("Open file");
    private readonly ActionButton openOutput = new("Open saved folder");
    private readonly Label modeHint = Theme.Label("");
    private readonly Label sourceType = Theme.Label("SOURCE LOCATION", 8, Theme.Muted, FontStyle.Bold);
    private readonly Label status = Theme.Label("Ready to scan · your source is opened read-only", 9, Theme.Muted);
    private readonly Label foundValue = Theme.Label("0", 23, Theme.Ink, FontStyle.Bold);
    private readonly Label availableValue = Theme.Label("0", 23, Theme.Accent, FontStyle.Bold);
    private readonly Label selectedValue = Theme.Label("0", 23, Theme.Ink, FontStyle.Bold);
    private readonly Label selectedSize = Theme.Label("0 B selected", 8, Theme.Muted);
    private readonly Label resultCount = Theme.Label("Discovered files", 14, Theme.Ink, FontStyle.Bold);
    private readonly Label detailTitle = Theme.Label("Select a file to see its recovery details", 10, Theme.Ink, FontStyle.Bold);
    private readonly Label detailNote = Theme.Label("Check a card to recover it. Double-click a file to open its full viewer.", 9, Theme.Muted);
    private readonly ProgressBar progressBar = new();
    private readonly FileGallery gallery = new();
    private readonly Label empty = Theme.Label("Choose a location, then start a scan.\nDeleted files will appear here, grouped by type.", 12, Theme.Muted);
    private readonly Dictionary<string, ActionButton> categoryButtons = [];
    private readonly HashSet<Guid> selected = [];
    private readonly ToolTip tips = new();
    private List<RecoveryItem> visibleItems = [];
    private ScanResult? result;
    private CancellationTokenSource? cancellation;
    private string category = "All types";
    private string? selectedFormat;
    private bool updatingFileTypes;
    private sealed record FormatChoice(string? Extension, int Count)
    {
        public override string ToString() => (Extension is null ? "All formats" : Categories.FormatOf("file" + Extension)) + $" ({Count:N0})";
    }
    private string? lastOutput;
    private string? advancedExecutable;
    private bool isImage;
    private bool busy;
    private bool scanning;
    private bool recovering;
    private CancellationTokenSource? recoveryCancellation;
    private readonly ConcurrentQueue<RecoveryItem> liveItems = new();
    private ScanResult? liveView;
    private DateTime liveRefresh;
    private bool settingSource;
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 150 };
    private ScanProgress? latestProgress;

    public MainForm(string? source = null, bool image = false)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Reclaim — File Recovery"; Font = Theme.Font(); BackColor = Theme.Background;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(1440, 900); MinimumSize = new Size(1250, 820);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (ArgumentException) { }
        BuildLayout(); WireEvents(); PopulateDrives();
        if (source is not null) SetSource(source, image);
        UpdateModeHint();
        ResumeLayout(true);
        uiTimer.Tick += (_, _) => ShowProgress(); uiTimer.Start();
    }
    private void BuildLayout()
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var root = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, MinimumSize = new Size(1230, 770), Size = ClientSize };
        viewport.Resize += (_, _) => root.Size = new Size(Math.Max(viewport.ClientSize.Width, root.MinimumSize.Width), Math.Max(viewport.ClientSize.Height, root.MinimumSize.Height));
        root.ColumnStyles.Add(new(SizeType.Absolute, 190)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        viewport.Controls.Add(root); Controls.Add(viewport);
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 34, 47), Margin = new Padding(0) };
        root.Controls.Add(sidebar, 0, 0); sidebar.Controls.Add(new BrandPanel());
        var nav = new TableLayoutPanel { Location = new Point(12, 116), Width = 166, Height = 414, RowCount = 9, ColumnCount = 1, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        nav.RowStyles.Add(new(SizeType.Absolute, 36));
        nav.Controls.Add(Theme.Label("FILE TYPES", 8, Color.FromArgb(130, 151, 168), FontStyle.Bold), 0, 0);
        string[] labels = ["All types", "Images", "Videos", "Audio", "Documents", "Archives", "Other"];
        for (int i = 0; i < labels.Length; i++)
        {
            string key = labels[i]; nav.RowStyles.Add(new(SizeType.Absolute, 46));
            var button = new ActionButton(key + "   0")
            {
                Tag = key, Navigation = true, Dark = true, Dock = DockStyle.Fill,
                Margin = new Padding(0, 3, 0, 3), Font = Theme.Font(10)
            };
            button.Click += (_, _) => { category = key; selectedFormat = null; ApplyFilters(); UpdateCategoryButtons(); };
            categoryButtons[key] = button; nav.Controls.Add(button, 0, i + 1);
        }
        sidebar.Controls.Add(nav);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 230, Padding = new Padding(12, 12, 12, 20), RowCount = 4, ColumnCount = 1 };
        bottom.ColumnStyles.Add(new(SizeType.Percent, 100));
        bottom.RowStyles.Add(new(SizeType.Percent, 100)); bottom.RowStyles.Add(new(SizeType.Absolute, 48)); bottom.RowStyles.Add(new(SizeType.Absolute, 48)); bottom.RowStyles.Add(new(SizeType.Absolute, 28));
        var readOnly = Theme.Label("READ-ONLY SCANNING\nYour source stays untouched.", 9, Color.FromArgb(142, 170, 184));
        bottom.Controls.Add(readOnly, 0, 0);
        var help = new ActionButton("Recovery tools") { Dark = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        help.Click += (_, _) => ShowTools(); bottom.Controls.Add(help, 0, 1);
        var admin = new ActionButton(IsAdmin() ? "Administrator mode" : "Run as administrator") { Dark = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        admin.Enabled = !IsAdmin(); admin.Font = Theme.Font(8, FontStyle.Bold); admin.Click += (_, _) => RestartElevated(); bottom.Controls.Add(admin, 0, 2);
        bottom.Controls.Add(Theme.Label("Reclaim 2.3.0 · Local recovery", 8, Color.FromArgb(112, 143, 161)), 0, 3);
        sidebar.Controls.Add(bottom);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 18), ColumnCount = 1, RowCount = 7, Margin = new Padding(0) };
        int[] heights = [78, -1, -1, -1, 0, 72, 68];
        foreach (int h in heights) main.RowStyles.Add(h < 0 ? new(SizeType.AutoSize) : h == 0 ? new(SizeType.Percent, 100) : new(SizeType.Absolute, h));
        root.Controls.Add(main, 1, 0);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 220));
        header.RowStyles.Add(new(SizeType.AutoSize)); header.RowStyles.Add(new(SizeType.Percent, 100));
        var headline = Theme.Label("Your files. A second chance.", 25, Theme.Ink, FontStyle.Bold);
        headline.Name = "Headline"; headline.AutoSize = true;
        header.Controls.Add(headline, 0, 0);
        header.Controls.Add(Theme.Label("Find lost files, preview their contents and recover what matters.", 10, Theme.Muted), 0, 1);
        var badge = Theme.Label("●  OFFLINE · NO UPLOADS", 8, Theme.Accent, FontStyle.Bold); badge.TextAlign = ContentAlignment.MiddleRight;
        header.Controls.Add(badge, 1, 0); main.Controls.Add(header, 0, 0);

        var sourceCard = new Card { Name = "SourceCard", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(18, 10, 18, 10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var sourceLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 2, Margin = Padding.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        sourceLayout.ColumnStyles.Add(new(SizeType.Percent, 100));
        sourceLayout.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < 4; i++) sourceLayout.RowStyles.Add(new(SizeType.AutoSize));
        sourceLayout.Controls.Add(sourceType, 0, 0); sourceLayout.SetColumnSpan(sourceType, 2);
        sourceType.AutoSize = true; sourceType.Margin = new Padding(0, 0, 0, 8);
        location.Font = Theme.Font(10); location.PlaceholderText = @"Choose a drive or folder, e.g. E:\Pictures";
        sourceLayout.Controls.Add(new InputFrame(location) { Name = "LocationField", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 8) }, 0, 1);
        var sourceActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 8) };
        ActionButton[] actions = [folderBrowse, imageBrowse, scan, cancel];
        for (int i = 0; i < actions.Length; i++)
        {
            sourceActions.ColumnStyles.Add(new(SizeType.Percent, 25));
            if (i < 3) sourceActions.ColumnStyles.Add(new(SizeType.Absolute, 8));
            actions[i].Dock = DockStyle.Fill; actions[i].Margin = Padding.Empty;
            sourceActions.Controls.Add(actions[i], i * 2, 0);
        }
        cancel.Enabled = false; sourceLayout.Controls.Add(sourceActions, 1, 1);
        drives.DropDownStyle = ComboBoxStyle.DropDownList;
        sourceLayout.Controls.Add(new InputFrame(drives) { Name = "DriveField", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }, 0, 2);
        mode.DropDownStyle = ComboBoxStyle.DropDownList; mode.Items.AddRange(["Deep scan", "Quick scan", "Advanced · PhotoRec", "Lost partitions · deep", "Camera video · deep"]); mode.SelectedIndex = 0;
        var sourceOptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        sourceOptions.ColumnStyles.Add(new(SizeType.Percent, 100)); sourceOptions.ColumnStyles.Add(new(SizeType.AutoSize));
        sourceOptions.Controls.Add(new InputFrame(mode) { Name = "ModeField", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        var refresh = new ActionButton("Refresh drives") { Name = "RefreshDrives", Dock = DockStyle.Fill, Font = Theme.Font(9), Margin = Padding.Empty };
        refresh.Click += (_, _) => { if (!busy) PopulateDrives(); }; sourceOptions.Controls.Add(refresh, 1, 0);
        sourceLayout.Controls.Add(sourceOptions, 1, 2);
        sourceLayout.Controls.Add(modeHint, 0, 3); sourceLayout.SetColumnSpan(modeHint, 2); modeHint.Font = Theme.Font(8); modeHint.ForeColor = Theme.Muted;
        modeHint.AutoSize = true; modeHint.Margin = new Padding(0, 8, 0, 0);
        sourceCard.Controls.Add(sourceLayout); main.Controls.Add(sourceCard, 0, 1);

        var stats = new TableLayoutPanel { Name = "RecoveryStats", Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        stats.RowStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < 3; i++) { stats.ColumnStyles.Add(new(SizeType.Percent, 33.333f)); if (i < 2) stats.ColumnStyles.Add(new(SizeType.Absolute, 8)); }
        stats.Controls.Add(Stat("FILES FOUND", foundValue, "Across selected scan stages"), 0, 0);
        stats.Controls.Add(Stat("AVAILABLE TO RECOVER", availableValue, "Review condition before saving"), 2, 0);
        stats.Controls.Add(Stat("SELECTED", selectedValue, "0 B selected", selectedSize), 4, 0); main.Controls.Add(stats, 0, 2);

        var toolbar = new TableLayoutPanel { Name = "ResultsToolbar", Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        toolbar.ColumnStyles.Add(new(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new(SizeType.Absolute, 205)); toolbar.ColumnStyles.Add(new(SizeType.Absolute, 170)); toolbar.ColumnStyles.Add(new(SizeType.Absolute, 148)); toolbar.ColumnStyles.Add(new(SizeType.AutoSize));
        toolbar.Controls.Add(resultCount, 0, 0);
        search.PlaceholderText = "Search name or original path"; toolbar.Controls.Add(new InputFrame(search) { Name = "SearchField", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }, 1, 0);
        fileType.DropDownStyle = ComboBoxStyle.DropDownList; fileType.MaxDropDownItems = 16; fileType.DropDownWidth = 340;
        fileType.AccessibleName = "File format"; fileType.Items.Add(new FormatChoice(null, 0)); fileType.SelectedIndex = 0;
        toolbar.Controls.Add(new InputFrame(fileType) { Name = "FileTypeField", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }, 2, 0);
        condition.DropDownStyle = ComboBoxStyle.DropDownList; condition.Items.AddRange(["All conditions", "Named files", "Signatures", "Needs review", "Unavailable"]); condition.SelectedIndex = 0;
        toolbar.Controls.Add(new InputFrame(condition) { Name = "ConditionField", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }, 3, 0);
        selectVisible.Name = "SelectVisible"; selectVisible.Dock = DockStyle.Fill; selectVisible.Enabled = false;
        selectVisible.Click += (_, _) => ToggleVisibleSelection(); toolbar.Controls.Add(selectVisible, 4, 0);
        main.Controls.Add(toolbar, 0, 3);

        var results = new Panel { Name = "ResultsWorkspace", Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        gallery.Dock = DockStyle.Fill; results.Controls.Add(gallery);
        empty.TextAlign = ContentAlignment.MiddleCenter; empty.BackColor = Theme.Background; results.Controls.Add(empty); empty.BringToFront();
        main.Controls.Add(results, 0, 4);
        var details = new Card { Name = "DetailsCard", Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), Padding = new Padding(16, 10, 16, 10) };
        var detailLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0) };
        detailLayout.ColumnStyles.Add(new(SizeType.Percent, 100)); detailLayout.ColumnStyles.Add(new(SizeType.AutoSize)); detailLayout.ColumnStyles.Add(new(SizeType.AutoSize));
        detailLayout.RowStyles.Add(new(SizeType.Absolute, 25)); detailLayout.RowStyles.Add(new(SizeType.Percent, 100));
        detailLayout.Controls.Add(detailTitle, 0, 0); detailLayout.Controls.Add(detailNote, 0, 1);
        inspect.Anchor = AnchorStyles.Right; inspect.Margin = new Padding(10, 0, 0, 0); inspect.Enabled = false;
        openFile.Anchor = AnchorStyles.Right; openFile.Margin = new Padding(10, 0, 0, 0); openFile.Enabled = false;
        detailLayout.Controls.Add(openFile, 1, 0); detailLayout.SetRowSpan(openFile, 2);
        detailLayout.Controls.Add(inspect, 2, 0); detailLayout.SetRowSpan(inspect, 2); details.Controls.Add(detailLayout); main.Controls.Add(details, 0, 5);
        var footer = new TableLayoutPanel { Name = "RecoveryFooter", Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        footer.RowStyles.Add(new(SizeType.Percent, 100)); footer.RowStyles.Add(new(SizeType.Absolute, 8)); footer.RowStyles.Add(new(SizeType.Absolute, 6));
        footer.Controls.Add(status, 0, 0);
        openOutput.Anchor = AnchorStyles.Right; openOutput.Margin = new Padding(0, 0, 8, 0); openOutput.Enabled = false; footer.Controls.Add(openOutput, 1, 0);
        recover.Anchor = AnchorStyles.Right; recover.Margin = Padding.Empty; recover.Enabled = false; footer.Controls.Add(recover, 2, 0);
        progressBar.Dock = DockStyle.Fill; progressBar.Style = ProgressBarStyle.Continuous; progressBar.Margin = Padding.Empty; footer.Controls.Add(progressBar, 0, 2); footer.SetColumnSpan(progressBar, 3);
        main.Controls.Add(footer, 0, 6);
        UpdateCategoryButtons();
        tips.SetToolTip(mode, "Deep: NTFS / FAT / exFAT records plus signatures. Advanced: PhotoRec saves more supported formats while scanning to a separate drive.");
        tips.SetToolTip(location, "You can type the original folder path even if that folder has been deleted.");
        tips.SetToolTip(imageBrowse, "Read-only .img, .dd, .raw or .bin image; all recognized MBR / GPT partitions are scanned.");
        tips.SetToolTip(fileType, "Filter by filename extension, such as JPG, PNG, PDF or MP4. Counts match the category, search and condition filters. No extension includes files without a suffix.");
        tips.SetToolTip(selectVisible, "Select all recoverable files matching the current filters. Selections in other formats stay selected.");
    }
    private Card Stat(string title, Label value, string subtitle, Label? subtitleLabel = null)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(14, 10, 14, 10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) layout.RowStyles.Add(new(SizeType.AutoSize));
        var heading = Theme.Label(title, 8, Theme.Muted, FontStyle.Bold); heading.AutoSize = true;
        value.AutoSize = true; value.AutoEllipsis = false; value.Margin = new Padding(0, 3, 0, 3);
        var description = subtitleLabel ?? Theme.Label(subtitle, 8, Theme.Muted); description.AutoSize = true;
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(value, 0, 1); layout.Controls.Add(description, 0, 2);
        card.Controls.Add(layout); return card;
    }
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var workingArea = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, workingArea.Width), Math.Min(MinimumSize.Height, workingArea.Height));
        // Keep the startup window within the monitor's available area after DPI scaling.
        if (WindowState == FormWindowState.Normal)
            Size = new Size(Math.Min(Width, workingArea.Width), Math.Min(Height, workingArea.Height));
    }
    private void WireEvents()
    {
        folderBrowse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose the original location of your deleted files", UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (dialog.ShowDialog(this) == DialogResult.OK) SetSource(dialog.SelectedPath, false);
        };
        imageBrowse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "Choose a read-only disk image", Filter = "Disk images|*.img;*.dd;*.raw;*.bin|All files|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK) SetSource(dialog.FileName, true);
        };
        drives.SelectedIndexChanged += (_, _) => { if (!settingSource && drives.SelectedItem is DriveChoice choice) SetSource(choice.Root, false); };
        location.TextChanged += (_, _) => { if (!busy && !settingSource) { ClearResults(); UpdateModeHint(); } };
        mode.SelectedIndexChanged += (_, _) => UpdateModeHint();
        scan.Click += async (_, _) => await StartScan(); cancel.Click += (_, _) => { cancellation?.Cancel(); recoveryCancellation?.Cancel(); cancel.Enabled = false; status.Text = "Stopping after the current read…"; };
        recover.Click += async (_, _) => await RecoverSelected();
        search.TextChanged += (_, _) => ApplyFilters(); condition.SelectedIndexChanged += (_, _) => ApplyFilters();
        fileType.SelectedIndexChanged += (_, _) =>
        {
            if (updatingFileTypes) return;
            selectedFormat = (fileType.SelectedItem as FormatChoice)?.Extension; ApplyFilters();
        };
        fileType.DropDownClosed += (_, _) => ApplyFilters(true);
        inspect.Click += (_, _) => InspectFile(); openFile.Click += (_, _) => OpenFile();
        gallery.CurrentItemChanged += (_, _) => UpdateDetails();
        gallery.ItemActivated += (_, _) => OpenFile();
        gallery.CheckRequested += (item, value) =>
        {
            if (recovering || busy && !scanning || !item.CanRecover) return;
            if (value) selected.Add(item.Id); else selected.Remove(item.Id);
            UpdateSelection();
        };
        openOutput.Click += (_, _) =>
        {
            if (lastOutput is null || !Directory.Exists(lastOutput)) return;
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(lastOutput); Process.Start(start);
        };
        FormClosing += (_, e) =>
        {
            if (busy || recovering) { e.Cancel = true; cancellation?.Cancel(); recoveryCancellation?.Cancel(); status.Text = "Stopping safely. Close the app when the operation finishes."; }
        };
    }
    private sealed record DriveChoice(string Root, string Display) { public override string ToString() => Display; }
    private void PopulateDrives()
    {
        settingSource = true; drives.Items.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.Network or DriveType.CDRom) continue;
                drives.Items.Add(new DriveChoice(drive.RootDirectory.FullName, $"{drive.Name}  {drive.VolumeLabel}  ·  {drive.DriveFormat}  ·  {Categories.Size(drive.TotalSize)}"));
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        drives.SelectedIndex = -1; drives.Text = "Choose a drive"; settingSource = false;
        foreach (string device in Devices.PhysicalDisks()) drives.Items.Add(new DriveChoice(device, "Physical disk " + device[17..] + "  ·  all partitions (administrator)"));
    }
    private void SetSource(string path, bool image)
    {
        if (busy) return;
        settingSource = true; isImage = image; location.Text = path;
        sourceType.Text = image ? "DISK IMAGE · READ-ONLY" : path.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase) ? "PHYSICAL DISK · ALL PARTITIONS" : "SOURCE LOCATION";
        drives.SelectedIndex = -1; settingSource = false; ClearResults(); UpdateModeHint();
    }
    private ScanSource GetSource()
    {
        string path = location.Text.Trim();
        if (path.Length == 0) throw new InvalidOperationException("Choose the location of your deleted files first.");
        if (isImage)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("That disk image does not exist.");
            return new(Path.GetFullPath(path), true);
        }
        if (path.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase)) { var device = new ScanSource(path, false, "", true); _ = device.DevicePath; return device; }
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        if (root.StartsWith(@"\\") || root.Length != 3 || root[1] != ':') throw new InvalidOperationException("Choose a local drive, USB drive or disk image.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException("That drive is not ready.");
        return new(full, false, full[root.Length..].Trim('\\'));
    }
    private async Task StartScan()
    {
        if (busy) return;
        ScanSource source;
        try { source = GetSource(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { ShowError(ex.Message); return; }
        // Disk images can be scanned without elevation. A live drive needs a read-only raw handle.
        if (!source.IsImage && !IsAdmin()) { ShowError("For drive scanning, click 'Restart as administrator' in the sidebar. Disk images can be scanned without administrator access."); return; }
        if (mode.SelectedIndex == 2) { await StartAdvanced(source); return; }
        CameraRecovery.Initialization? cameraReference = null;
        if (mode.SelectedIndex == 4)
        {
            using var reference = new OpenFileDialog { Title = "Optional compatible fMP4 camera reference · Cancel to use surviving initialization metadata", Filter = "Camera reference|*.mp4;*.mov;*.m4v|All files|*.*" };
            if (reference.ShowDialog(this) == DialogResult.OK)
            {
                try { cameraReference = CameraRecovery.LoadInitialization(reference.FileName); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { ShowError(ex.Message); return; }
            }
        }
        ClearResults(); scanning = true; SetBusy(true); cancellation = new CancellationTokenSource();
        ScanMode selectedMode = mode.SelectedIndex switch { 0 => ScanMode.Deep, 1 => ScanMode.Quick, 3 => ScanMode.LostPartitions, _ => ScanMode.Camera };
        try
        {
            var completed = await Task.Run(() => RecoveryEngine.Scan(source, selectedMode, p => Interlocked.Exchange(ref latestProgress, p), cancellation.Token,
                view => Interlocked.Exchange(ref liveView, view), item => liveItems.Enqueue(item), cameraReference));
            DrainLiveResults(true); result = completed;
            ApplyFilters(); UpdateCategoryButtons(); UpdateStats();
            status.Text = result.Cancelled ? "Scan stopped · discovered results are retained" : $"Scan complete · {result.FileSystem} · {result.Items.Count:N0} files found";
            progressBar.Value = result.Cancelled ? 0 : 100;
            if (result.Warnings.Count > 0) ShowText("Scan notes", string.Join("\r\n\r\n", result.Warnings));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        { status.Text = "Scan could not finish"; ShowError(ex.Message); }
        finally { DrainLiveResults(true); scanning = false; latestProgress = null; cancellation.Dispose(); cancellation = null; SetBusy(false); }
    }
    private async Task RecoverSelected()
    {
        if (recovering || busy && !scanning || result is null) return;
        var items = result.Items.Where(x => selected.Contains(x.Id) && x.CanRecover).ToList();
        if (items.Count == 0) return;
        using var dialog = new FolderBrowserDialog { Description = "Choose where to save recovered files. Use a different drive or USB device.", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { RecoveryEngine.ValidateDestination(result.Source, dialog.SelectedPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { ShowError(ex.Message); return; }
        int risky = items.Count(x => x.State is RecoveryState.AtRisk or RecoveryState.Signature);
        if (risky > 0 && MessageBox.Show(this, $"{risky:N0} selected files need review or were found by signatures. Their contents may be incomplete or changed.\n\nSave {items.Count:N0} selected files ({Categories.Size(items.Sum(x => x.Size))}) to:\n{dialog.SelectedPath}", "Review selected recovery", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        bool duringScan = scanning; recovering = true; recoveryCancellation = new CancellationTokenSource();
        if (!duringScan) SetBusy(true); else { recover.Enabled = false; gallery.SetSelectionEnabled(false); }
        try
        {
            var scanResult = result;
            var outcome = await Task.Run(() => RecoveryEngine.Recover(scanResult, items, dialog.SelectedPath,
                duringScan ? null : p => Interlocked.Exchange(ref latestProgress, p), recoveryCancellation.Token));
            lastOutput = outcome.Folder; openOutput.Enabled = true;
            status.Text = $"{outcome.Saved:N0} saved · {outcome.Failed:N0} failed" + (outcome.Cancelled ? " · stopped" : "");
            progressBar.Value = outcome.Cancelled ? 0 : 100;
            ShowText("Recovery results", $"Saved: {outcome.Saved:N0}\r\nFailed: {outcome.Failed:N0}\r\nStopped: {outcome.Cancelled}\r\n\r\nFiles are grouped by type in:\r\n{outcome.Folder}\r\n\r\nA recovery-report.json records every completed attempt. Check recovered files before relying on them." +
                (outcome.Errors.Count > 0 ? "\r\n\r\n" + string.Join("\r\n", outcome.Errors.Take(20)) : ""));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { status.Text = "Recovery did not finish"; ShowError(ex.Message); }
        finally { recovering = false; recoveryCancellation.Dispose(); recoveryCancellation = null; if (!scanning) { latestProgress = null; SetBusy(false); } else SetBusy(true); }
    }
    private async Task StartAdvanced(ScanSource source)
    {
        string executable = advancedExecutable ?? Path.Combine(AppContext.BaseDirectory, "Engines", "PhotoRec", "photorec_win.exe");
        if (!File.Exists(executable))
        {
            using var engine = new OpenFileDialog { Title = "Choose PhotoRec 7.2 · download and extract it from cgsecurity.org first", Filter = "PhotoRec engine|photorec_win.exe", CheckFileExists = true };
            if (engine.ShowDialog(this) != DialogResult.OK) return;
            executable = advancedExecutable = engine.FileName;
        }
        using var destination = new FolderBrowserDialog { Description = "Advanced recovery saves files WHILE scanning. Choose a folder on a different drive with enough free space. Original folder filtering is unavailable.", UseDescriptionForTitle = true };
        if (destination.ShowDialog(this) != DialogResult.OK) return;
        try { RecoveryEngine.ValidateDestination(source, destination.SelectedPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { ShowError(ex.Message); return; }
        ClearResults(); SetBusy(true); cancellation = new CancellationTokenSource();
        try
        {
            var outcome = await AdvancedRecovery.Run(source, destination.SelectedPath, executable, p => Interlocked.Exchange(ref latestProgress, p), cancellation.Token);
            result = outcome.Result; lastOutput = outcome.Folder; openOutput.Enabled = true;
            ApplyFilters(); UpdateCategoryButtons(); UpdateStats();
            status.Text = $"Advanced scan {(result.Cancelled ? "stopped" : "finished")} · {result.Items.Count:N0} files already saved";
            progressBar.Value = result.Cancelled ? 0 : 100;
            ShowText("Advanced recovery results", $"{result.Items.Count:N0} files saved and grouped by type in:\r\n{outcome.Folder}\r\n\r\nSelect a file to preview it. These files are already saved; Recover selected creates another copy.\r\n\r\n" + string.Join("\r\n\r\n", result.Warnings));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { status.Text = "Advanced scan could not finish"; ShowError(ex.Message); }
        finally { latestProgress = null; cancellation.Dispose(); cancellation = null; SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        value |= recovering;
        busy = value; scan.Enabled = !value; cancel.Enabled = value;
        location.Enabled = !value; folderBrowse.Enabled = !value; imageBrowse.Enabled = !value; drives.Enabled = !value; mode.Enabled = !value;
        bool interactive = !recovering && (!value || scanning);
        recover.Enabled = interactive && selected.Count > 0; inspect.Enabled = interactive && CurrentItem()?.CanRecover == true;
        openFile.Enabled = interactive && CurrentItem() is not null; gallery.SetSelectionEnabled(interactive);
        UpdateSelection();
        if (value && !scanning) { progressBar.Value = 0; status.Text = "Preparing…"; }
    }
    private void ShowProgress()
    {
        if (scanning && DateTime.UtcNow >= liveRefresh) { DrainLiveResults(false); liveRefresh = DateTime.UtcNow.AddMilliseconds(500); }
        var p = Interlocked.Exchange(ref latestProgress, null);
        if (p is null || !busy) return;
        status.Text = p.Phase + " · " + (p.Detail.Length > 0 ? p.Detail : $"{p.Done:N0} / {p.Total:N0}");
        progressBar.Value = (int)(p.Fraction * 100);
        if (p.Phase != "Recovering selected files") foundValue.Text = (scanning ? result?.Items.Count ?? 0 : p.Found).ToString("N0");
    }
    private void DrainLiveResults(bool all)
    {
        var view = Interlocked.Exchange(ref liveView, null); if (view is not null) result = view;
        if (result is null) return;
        int count = 0;
        while ((all || count < 4000) && liveItems.TryDequeue(out var item)) { result.Items.Add(item); count++; }
        if (count > 0) { ApplyFilters(true); UpdateCategoryButtons(); UpdateStats(); }
    }
    private void ClearResults()
    {
        result = null; liveView = null; liveItems.Clear(); selectedFormat = null; selected.Clear(); visibleItems = []; UpdateFileTypes([]); gallery.SetItems(null, visibleItems, selected.Contains); empty.Visible = true;
        UpdateCategoryButtons(); UpdateStats(); UpdateDetails(); resultCount.Text = "Discovered files";
    }
    private void ApplyFilters(bool preservePosition = false)
    {
        string query = search.Text.Trim();
        var matching = (result?.Items ?? []).Where(x => (category == "All types" || x.Category.ToString() == category)
            && (query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.OriginalPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (condition.SelectedIndex switch { 1 => x.CarvedOffset is null && x.RecoveredPath is null, 2 => x.State == RecoveryState.Signature, 3 => x.State == RecoveryState.AtRisk, 4 => !x.CanRecover, _ => true }))
            .ToList();
        UpdateFileTypes(matching);
        visibleItems = matching.Where(x => selectedFormat is null || Categories.ExtensionOf(x.Name).Equals(selectedFormat, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Category).ThenBy(x => Categories.ExtensionOf(x.Name), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        gallery.SetItems(result, visibleItems, selected.Contains, preservePosition); empty.Visible = visibleItems.Count == 0;
        empty.Tag = visibleItems.Count == 0 ? null : "SkipPreview";
        empty.Text = result is null ? "Choose a location, then start a scan.\nDeleted files will appear here, grouped by type." : "No files match this view.\nTry another category, clear the filters, or scan another location.";
        resultCount.Text = result is null ? "Discovered files" : $"{visibleItems.Count:N0} files in this view";
        UpdateDetails(); UpdateSelection();
    }
    private void UpdateFileTypes(IReadOnlyList<RecoveryItem> matching)
    {
        if (fileType.DroppedDown) return;
        var choices = new List<FormatChoice> { new(null, matching.Count) };
        choices.AddRange(matching.GroupBy(x => Categories.ExtensionOf(x.Name), StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key.Length == 0 ? 1 : 0).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => new FormatChoice(x.Key, x.Count())));
        if (selectedFormat is not null && !choices.Any(x => string.Equals(x.Extension, selectedFormat, StringComparison.OrdinalIgnoreCase))) choices.Add(new(selectedFormat, 0));
        int index = choices.FindIndex(x => string.Equals(x.Extension, selectedFormat, StringComparison.OrdinalIgnoreCase));
        if (fileType.Items.Cast<FormatChoice>().SequenceEqual(choices) && fileType.SelectedIndex == index) return;
        updatingFileTypes = true; fileType.BeginUpdate();
        try { fileType.Items.Clear(); fileType.Items.AddRange(choices.Cast<object>().ToArray()); fileType.SelectedIndex = index; }
        finally { fileType.EndUpdate(); updatingFileTypes = false; }
    }
    private void UpdateCategoryButtons()
    {
        foreach (var (name, button) in categoryButtons)
        {
            int count = result?.Items.Count(x => name == "All types" || x.Category.ToString() == name) ?? 0;
            button.Text = $"{name}   {count:N0}";
            button.Selected = category == name;
        }
    }
    private void UpdateStats()
    {
        foundValue.Text = (result?.Items.Count ?? 0).ToString("N0");
        availableValue.Text = (result?.Items.Count(x => x.CanRecover) ?? 0).ToString("N0"); UpdateSelection();
    }
    private void UpdateSelection()
    {
        selectedValue.Text = selected.Count.ToString("N0");
        selectedSize.Text = Categories.Size(result?.Items.Where(x => selected.Contains(x.Id)).Sum(x => x.Size) ?? 0) + " selected";
        recover.Enabled = !recovering && (!busy || scanning) && selected.Count > 0; gallery.Invalidate();
        var available = visibleItems.Where(x => x.CanRecover).ToList();
        selectVisible.Enabled = !recovering && (!busy || scanning) && available.Count > 0;
        selectVisible.Text = available.Count > 0 && available.All(x => selected.Contains(x.Id)) ? "Clear visible" : "Select visible";
    }
    private void ToggleVisibleSelection()
    {
        if (recovering || busy && !scanning) return;
        var available = visibleItems.Where(x => x.CanRecover).ToList();
        bool remove = available.Count > 0 && available.All(x => selected.Contains(x.Id));
        foreach (var item in available) { if (remove) selected.Remove(item.Id); else selected.Add(item.Id); }
        UpdateSelection();
    }
    private RecoveryItem? CurrentItem() => gallery.CurrentItem;
    private void UpdateDetails()
    {
        var item = CurrentItem(); bool interactive = !recovering && (!busy || scanning); inspect.Enabled = interactive && item?.CanRecover == true; openFile.Enabled = interactive && item is not null;
        detailTitle.Text = item is null ? "Select a file to see its recovery details" : item.Name + "  ·  " + Categories.Size(item.Size) + "  ·  " + item.Method;
        detailNote.Text = item is null ? "Check a card to recover it. Double-click a file to open its full viewer." : item.RecoveryChance + " · " + item.Validation + " · " + item.Note;
        tips.SetToolTip(detailNote, detailNote.Text);

    }
    private void UpdateModeHint()
    {
        modeHint.Text = mode.SelectedIndex == 3 ? "Lost partitions · read-only sector search for surviving NTFS / FAT / exFAT / ext volume starts, then deep recovery."
            : mode.SelectedIndex == 4 ? "Camera video · deep signatures + fMP4 fragment reconstruction. Optional compatible fMP4 reference; arbitrary camera clusters cannot be inferred."
            : mode.SelectedIndex == 2 ? "Advanced · requires an extracted PhotoRec 7.2 engine. Select photorec_win.exe when starting; files save during the scan."
            : mode.SelectedIndex == 0
            ? "Deep scan · NTFS / FAT / exFAT / ext records + signatures. Results appear while scanning. Signature folders are unknown."
            : "Quick scan · NTFS / FAT / exFAT / ext directory records + Recycle Bin. Results appear while scanning.";
    }
    private void OpenFile()
    {
        var item = CurrentItem(); if (recovering || busy && !scanning || item is null || result is null) return;
        using var viewer = new Form { Text = item.Name + " — Reclaim", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(960, 760), MinimumSize = new Size(620, 460), BackColor = Color.White, Font = Theme.Font() };
        var content = new FilePreview { Dock = DockStyle.Fill, Margin = Padding.Empty };
        viewer.Controls.Add(content);
        viewer.Shown += (_, _) => content.ShowItem(result, item);
        viewer.ShowDialog(this);
    }
    private void InspectFile()
    {
        var item = CurrentItem(); if (item is null || result is null) return;
        try
        {
            int length = (int)Math.Min(item.Size, 1024); var bytes = new byte[length];
            using (var content = RecoveryContent.Open(result, item)) content.ReadExactly(bytes);
            var text = new StringBuilder($"{item.Name}\r\n{item.OriginalPath}\r\n{item.Note}\r\n\r\nFIRST {length:N0} BYTES\r\n");
            for (int i = 0; i < length; i += 16)
            {
                var line = bytes.Skip(i).Take(16).ToArray();
                text.Append($"{i:X6}  {Convert.ToHexString(line).Chunk(2).Select(x => new string(x)).Aggregate("", (a, b) => a + b + " "),-48}  ");
                text.AppendLine(new string(line.Select(x => x >= 32 && x < 127 ? (char)x : '.').ToArray()));
            }
            ShowText("File inspection", text.ToString(), true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { ShowError(ex.Message); }
    }
    private void ShowGuide() => ShowText("Recovery guide", RecoveryGuide.Text);
    private void ShowTools()
    {
        ScanSource? source = result?.Source;
        if (source is null) { try { source = GetSource(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { } }
        using var tools = new RecoveryTools(source, result, CurrentItem()); tools.ShowDialog(this); UpdateDetails(); gallery.Invalidate();
    }
    private void RestartElevated()
    {
        if (busy) return;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            if (location.Text.Trim().Length > 0) { start.ArgumentList.Add(isImage ? "--image" : "--source"); start.ArgumentList.Add(location.Text.Trim()); }
            Process.Start(start); Close();
        }
        catch (System.ComponentModel.Win32Exception ex) { ShowError("Administrator restart was not completed. " + ex.Message); }
    }
    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private void ShowError(string message) => MessageBox.Show(this, message, "Reclaim", MessageBoxButtons.OK, MessageBoxIcon.Information);
    private void ShowText(string title, string text, bool monospace = false)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(760, 570), MinimumSize = new Size(540, 350), BackColor = Color.White, Font = Theme.Font() };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = text, BackColor = Color.White, BorderStyle = BorderStyle.None, Font = monospace ? new Font("Consolas", 10) : Theme.Font(11) };
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) }; panel.Controls.Add(box); dialog.Controls.Add(panel);
        var close = new ActionButton("Close", true) { Dock = DockStyle.Bottom, Height = 46, DialogResult = DialogResult.OK }; dialog.Controls.Add(close);
        dialog.AcceptButton = close; dialog.CancelButton = close; dialog.ShowDialog(this);
    }
    public void LoadPreview(ScanResult preview)
    {
        SetSource(preview.Source.Path, preview.Source.IsImage); result = preview; ApplyFilters(); UpdateCategoryButtons(); UpdateStats();

        UpdateDetails();
        status.Text = $"Sample image scanned · {preview.FileSystem} · {preview.Items.Count:N0} real fixture results"; progressBar.Value = 100;
    }
    internal Task PreviewPending => gallery.Pending;
    internal void SelectPreviewFile(string name)
    {
        gallery.FocusItem(name); UpdateDetails();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { uiTimer.Stop(); uiTimer.Dispose(); tips.Dispose(); cancellation?.Dispose(); }
        base.Dispose(disposing);
    }
}
