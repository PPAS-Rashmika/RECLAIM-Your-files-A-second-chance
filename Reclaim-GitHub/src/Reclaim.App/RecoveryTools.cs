using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using Reclaim.Core;

namespace Reclaim.App;

public sealed class RecoveryTools : Form
{
    private readonly ScanSource? source;
    private readonly ScanResult? scan;
    private readonly RecoveryItem? item;
    private CancellationTokenSource? operation;
    private string? ffmpeg;
    public RecoveryTools(ScanSource? source, ScanResult? scan, RecoveryItem? item)
    {
        this.source = source; this.scan = scan; this.item = item;
        Text = "Recovery tools — Reclaim"; StartPosition = FormStartPosition.CenterParent; Size = new(940, 680); MinimumSize = new(720, 500);
        Font = Theme.Font(10); BackColor = Theme.Background; Padding = new Padding(22);
        var title = Theme.Label("Recovery tools", 20, Theme.Ink, FontStyle.Bold); title.Dock = DockStyle.Top; title.Height = 55;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var health = Tab(tabs, "Drive health", "Read health / SMART", Health);
        AddAction(health, "All drives", () => Health(true));
        var files = Tab(tabs, "Validate & repair", "Validate selected file", ValidateFile);
        AddAction(files, "Repair selected file", () => Repair(true));
        AddAction(files, "Repair saved file", () => Repair(false));
        AddAction(files, "Choose FFmpeg", ChooseEngine);
        var boot = Tab(tabs, "Bootable recovery", "Create rescue ISO", CreateBootIso);
        AddAction(boot, "Open builder folder", OpenBootBuilder);
        AddAction(boot, "Check requirements", BootRequirements);
        var guide = new TabPage("Guide") { Padding = new Padding(16), BackColor = Color.White };
        guide.Controls.Add(new TextBox { Text = RecoveryGuide.Text, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None }); tabs.TabPages.Add(guide);
        var stop = new ActionButton("Stop operation") { Name = "StopOperation", Enabled = false, Dock = DockStyle.Bottom, Height = 44, Margin = new Padding(0, 12, 0, 0) };
        stop.Click += (_, _) => operation?.Cancel();
        Controls.Add(tabs); Controls.Add(stop); Controls.Add(title);
        // Each tab has its own output so a result remains visible when changing tabs.
        FormClosing += (_, e) => { if (operation is not null) { e.Cancel = true; operation.Cancel(); } };
        ActiveLog().Text = "Select a physical drive or volume in the main window, then read its health. All drives also works without a selection.\r\n\r\nTemperature, wear, power-on hours and uncorrected read/write errors are shown only when Windows and the device expose them. Unsupported counters remain unavailable. No health data is invented.";
        ((TextBox)files.Controls.Find("Output", true)[0]).Text = (item is null ? "No file is selected." : $"Selected: {item.Name}\r\nChance: {item.RecoveryChance}\r\nValidation: {item.Validation}") +
            "\r\n\r\nPhoto repair re-encodes decodable pixels into a new PNG and can append a missing JPEG end marker. It cannot recreate lost pixels.\r\n\r\nVideo repair uses an independently supplied FFmpeg executable to remux surviving video/audio and then decode-check the output. Missing moov/codec headers or arbitrary missing video fragments may require specialist reference-based repair. FFmpeg is not bundled.\r\n\r\nAll repair operations create a new copy on the chosen output drive.";
        ((TextBox)boot.Controls.Find("Output", true)[0]).Text = BootText;
    }
    private const string BootText = "Create rescue ISO builds bootable Windows PE media with an independent rescue console. Choose an output parent folder; a new media folder is created inside it. No USB is formatted.\r\n\r\nRequirements: Windows ADK Deployment Tools plus the matching Windows PE add-on, x64, and administrator access.\r\n\r\nRescue mode can scan read-only raw disks/images for contiguous JPEG/PNG/PDF signatures and save selected files to another physical disk. It is separate from the full desktop application.\r\n\r\nThe full .NET 10 desktop app is not assumed to run inside WinPE. After creating the ISO, test it in a VM before writing it to a USB using a media-writing tool.";
    private TabPage Tab(TabControl tabs, string title, string caption, Func<Task> action)
    {
        var tab = new TabPage(title) { Padding = new Padding(16), BackColor = Color.White };
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 12), Name = "Actions" };
        tab.Controls.Add(new TextBox { Name = "Output", TabStop = false, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = Theme.Font(10) });
        tab.Controls.Add(row); AddAction(tab, caption, action); tabs.TabPages.Add(tab); return tab;
    }
    private void AddAction(TabPage tab, string caption, Func<Task> action)
    {
        var button = new ActionButton(caption) { AutoSize = true, Margin = new Padding(0, 0, 8, 8) };
        button.Click += async (_, _) =>
        {
            if (operation is not null) return; operation = new CancellationTokenSource();
            var tabs = Controls.OfType<TabControl>().Single(); tabs.Enabled = false;
            var stop = Controls.OfType<ActionButton>().Single(x => x.Name == "StopOperation"); stop.Enabled = true;
            try { await action(); }
            catch (OperationCanceledException) { ActiveLog().AppendText("\r\nStopped."); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or JsonException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
            { ActiveLog().AppendText("\r\n" + ex.Message); }
            finally { operation.Dispose(); operation = null; tabs.Enabled = true; stop.Enabled = false; }
        };
        ((FlowLayoutPanel)tab.Controls.Find("Actions", true)[0]).Controls.Add(button);
    }
    private TextBox ActiveLog()
    {
        var tabs = Controls.OfType<TabControl>().Single();
        return (TextBox)(tabs.SelectedTab ?? tabs.TabPages[0]).Controls.Find("Output", true)[0];
    }
    private Task Health() => Health(false);
    private async Task Health(bool all)
    {
        var target = ActiveLog(); target.Text = "Reading Windows drive health and reliability counters…";
        var reports = await DriveHealth.Query(all ? null : source, operation!.Token); var text = new StringBuilder();
        foreach (var r in reports)
        {
            string Value(long? v, string unit = "") => v.HasValue ? v.Value.ToString("N0") + unit : "Unavailable";
            text.AppendLine($"PHYSICAL DISK {r.Number} · {r.Model}\r\nSerial: {r.Serial}\r\nHealth: {r.Health}\r\nStatus: {r.Status}\r\nTemperature: {Value(r.Temperature, " °C")}\r\nWear: {Value(r.Wear, " %")}\r\nPower-on: {Value(r.PowerOnHours, " hours")}\r\nUncorrected read errors: {Value(r.ReadErrors)}\r\nUncorrected write errors: {Value(r.WriteErrors)}\r\n{r.Note}\r\n");
        }
        target.Text = reports.Count > 0 ? text.ToString() : "No matching physical disk was reported. Try All drives or run as administrator.";
    }
    private async Task ValidateFile()
    {
        if (scan is null || item is null) throw new InvalidOperationException("Select a recovered file card first.");
        var target = ActiveLog(); target.Text = "Validating surviving bytes…";
        string message = await Task.Run(() => FileAssessment.Check(scan, item, operation!.Token));
        target.Text = $"{item.Name}\r\n\r\n{message}\r\n\r\nRecovery chance: {item.RecoveryChance}\r\n\r\nChance labels describe evidence, not a statistical probability. A valid container does not prove the original contents survived.";
    }
    private Task ChooseEngine()
    {
        using var choose = new OpenFileDialog { Title = "Choose an independently downloaded FFmpeg executable", Filter = "FFmpeg|ffmpeg.exe", CheckFileExists = true };
        if (choose.ShowDialog(this) == DialogResult.OK) { ffmpeg = choose.FileName; ActiveLog().AppendText("\r\nFFmpeg selected: " + ffmpeg); }
        return Task.CompletedTask;
    }
    private async Task Repair(bool selected)
    {
        if (selected && (scan is null || item is null || !item.CanRecover)) throw new InvalidOperationException("Select an available image/video file card first.");
        string? inputPath = null; string name;
        if (!selected)
        {
            using var input = new OpenFileDialog { Title = "Choose a saved damaged photo or video", Filter = "Photos / videos|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.tif;*.tiff;*.mp4;*.mov;*.m4v;*.3gp;*.avi;*.mkv;*.webm" };
            if (input.ShowDialog(this) != DialogResult.OK) return; inputPath = input.FileName; name = Path.GetFileName(inputPath);
        }
        else name = item!.Name;
        string extension = Path.GetExtension(name).ToLowerInvariant(); bool video = Categories.Of(name) == FileCategory.Videos;
        if (!video && !new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff" }.Contains(extension)) throw new InvalidDataException("Select a supported raster photo or video.");
        if (video)
        {
            ffmpeg ??= FindMediaEngine();
            if (!File.Exists(ffmpeg)) { await ChooseEngine(); if (!File.Exists(ffmpeg)) return; }
        }
        using var destination = new FolderBrowserDialog { Description = "Save repaired copies on a different drive from the recovery source", UseDescriptionForTitle = true };
        if (destination.ShowDialog(this) != DialogResult.OK) return;
        if (source is not null) RecoveryEngine.ValidateDestination(source, destination.SelectedPath);
        string folder = Path.Combine(destination.SelectedPath, "Reclaim_Repair_" + Guid.NewGuid().ToString("N")[..10]); Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "input" + extension), output = Path.Combine(folder, "repaired" + (video ? ".mp4" : ".png"));
        var target = ActiveLog(); target.Text = "Copying surviving bytes to the repair workspace…";
        await Task.Run(() =>
        {
            using var input = selected ? RecoveryContent.Open(scan!, item!) : new FileStream(inputPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var saved = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[1024 * 1024];
            for (int n; (n = input.Read(buffer)) > 0;) { operation!.Token.ThrowIfCancellationRequested(); saved.Write(buffer, 0, n); }
        });
        string note;
        if (video) { await MediaRepair.RemuxVideo(copy, output, ffmpeg!, message => { if (!IsDisposed) BeginInvoke(() => target.Text = message); }, operation!.Token); note = "Surviving video/audio remuxed; full output decode check passed. Dropped corrupt packets or missing original content cannot be reconstructed."; }
        else note = await Task.Run(() => PhotoRepair.Repair(copy, output));
        File.WriteAllText(Path.Combine(folder, "repair-report.json"), JsonSerializer.Serialize(new { Input = name, Output = output, Method = video ? "FFmpeg remux + decode check" : "Raster decode/re-encode", Note = note }, new JsonSerializerOptions { WriteIndented = true }));
        target.Text = "Saved repaired copy:\r\n" + output + "\r\n\r\n" + note + "\r\n\r\nThe copied input remains in the same folder.";
    }
    private static string FindMediaEngine()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "Engines", "FFmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        // An independently downloaded local engine stays outside the distributable ZIP.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Reclaim-Engines", "FFmpeg", "ffmpeg.exe"));
    }
    private Task OpenBootBuilder()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Bootable");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Bootable tools are missing. Extract the full release ZIP.");
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add(folder); Process.Start(info);
        ActiveLog().Text = BootText + "\r\n\r\nOpened: " + folder; return Task.CompletedTask;
    }
    private async Task CreateBootIso()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Use Run as administrator in the main window before creating rescue media.");
        string adk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Assessment and Deployment Kit");
        if (!File.Exists(Path.Combine(adk, "Deployment Tools", "DandISetEnv.bat")) || !Directory.Exists(Path.Combine(adk, "Windows Preinstallation Environment", "amd64", "WinPE_OCs")))
            throw new InvalidOperationException("Install Microsoft Windows ADK Deployment Tools and the matching Windows PE add-on first. See Check requirements and Bootable/README.md.");
        string builder = Path.Combine(AppContext.BaseDirectory, "Bootable", "Build-RescueISO.ps1");
        if (!File.Exists(builder)) throw new FileNotFoundException("Extract the full release ZIP; the boot-media builder is missing.");
        using var destination = new FolderBrowserDialog { Description = "Choose where to create rescue media on a different drive from the recovery source", UseDescriptionForTitle = true };
        if (destination.ShowDialog(this) != DialogResult.OK) return;
        if (source is not null) RecoveryEngine.ValidateDestination(source, destination.SelectedPath);
        string folder = Path.Combine(destination.SelectedPath, "Reclaim_Rescue_Media_" + Guid.NewGuid().ToString("N")[..10]);
        var target = ActiveLog(); target.Text = "Creating rescue ISO… Windows image servicing must finish before this window closes.\r\nNo USB will be formatted.\r\n\r\nOutput: " + folder;
        // Do not kill DISM while an image is mounted. The builder releases its mount in finally.
        Controls.OfType<ActionButton>().Single(x => x.Name == "StopOperation").Enabled = false;
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", builder, "-OutputDirectory", folder }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the rescue media builder.");
        var output = ReadBuilderLog(process.StandardOutput); var error = ReadBuilderLog(process.StandardError);
        await process.WaitForExitAsync(); string diagnostics = await output + "\r\n" + await error;
        string iso = Path.Combine(folder, "Reclaim-Rescue-x64.iso");
        if (process.ExitCode != 0 || !File.Exists(iso)) throw new IOException("Rescue ISO creation failed.\r\n" + diagnostics);
        target.Text = "Created rescue ISO:\r\n" + iso + "\r\n\r\nNo USB was formatted. Test the ISO in a VM, then write it to your chosen USB using a media-writing tool.\r\n\r\n" + diagnostics;
    }
    private static async Task<string> ReadBuilderLog(StreamReader reader)
    {
        char[] buffer = new char[2048]; string tail = "";
        for (int count; (count = await reader.ReadAsync(buffer)) > 0;) { tail += new string(buffer, 0, count); if (tail.Length > 8192) tail = tail[^8192..]; }
        return tail;
    }
    private Task BootRequirements()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Assessment and Deployment Kit");
        ActiveLog().Text = BootText + "\r\n\r\nADK deployment tools: " + (Directory.Exists(Path.Combine(root, "Deployment Tools")) ? "Found" : "Not installed") + "\r\nWindows PE add-on: " + (Directory.Exists(Path.Combine(root, "Windows Preinstallation Environment")) ? "Found" : "Not installed");
        return Task.CompletedTask;
    }
}
