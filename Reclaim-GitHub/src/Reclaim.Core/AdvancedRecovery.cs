using System.Diagnostics;
using System.Text.Json;

namespace Reclaim.Core;

public sealed record AdvancedOutcome(ScanResult Result, string Folder, int ExitCode);

public static class AdvancedRecovery
{
    public static async Task<AdvancedOutcome> Run(ScanSource source, string destination, string executable, Action<ScanProgress>? progress, CancellationToken token)
    {
        RecoveryEngine.ValidateDestination(source, destination);
        if (!File.Exists(executable)) throw new FileNotFoundException("The bundled PhotoRec engine is missing. Extract the complete Reclaim download.");
        token.ThrowIfCancellationRequested();
        var result = new ScanResult { Source = source, FileSystem = "PhotoRec signatures" };
        using var reader = new DiskReader(source); result.SourceLength = reader.Length;
        if (source.IsImage) result.ImageLastWriteUtc = File.GetLastWriteTimeUtc(source.DevicePath);
        else if (!source.IsPhysicalDevice) result.SourceVolumeIdentity = Native.VolumeIdentity(source.VolumeRoot);
        string job = Path.Combine(Path.GetFullPath(destination), "Reclaim_Advanced_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(job);
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { WorkingDirectory = job, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/log", "/logname", Path.Combine(job, "photorec.log"), "/d", Path.Combine(job, "raw"), "/cmd", source.DevicePath, "partition_none,options,keep_corrupted_file_no,fileopt,everything,enable,wholespace,search" }) start.ArgumentList.Add(argument);
        // PhotoRec's executable requests UAC even for images; an image is an ordinary read-only file.
        // Live-device scans retain the normal administrator requirement and are never downgraded.
        if (source.IsImage) start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        using var process = Process.Start(start) ?? throw new IOException("PhotoRec could not start.");
        var output = Drain(process.StandardOutput); var error = Drain(process.StandardError); int tick = 0;
        try
        {
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                if (tick++ % 10 == 0)
                {
                    int found = Directory.EnumerateFiles(job, "*", SearchOption.AllDirectories).Count(x => Path.GetFileName(Path.GetDirectoryName(x)!).StartsWith("raw.", StringComparison.Ordinal));
                    progress?.Invoke(new("Advanced scan · saving files", 0, 0, found, "PhotoRec is scanning all bytes; results are saved on the destination."));
                }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true; if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        string diagnostic = (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false));
        File.WriteAllText(Path.Combine(job, "engine-output.txt"), diagnostic);
        foreach (string path in Directory.EnumerateFiles(job, "*", SearchOption.AllDirectories).ToArray())
        {
            if (!Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("raw.", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("report.", StringComparison.OrdinalIgnoreCase)) continue;
            var file = new FileInfo(path); if (file.Length == 0) continue;
            string category = Path.Combine(job, Categories.Of(file.Name).ToString()); Directory.CreateDirectory(category);
            string target = Path.Combine(category, RecoveryEngine.SafeFileName(file.Name));
            if (File.Exists(target)) target = Path.Combine(category, Guid.NewGuid().ToString("N")[..8] + "_" + RecoveryEngine.SafeFileName(file.Name));
            File.Move(path, target); file = new FileInfo(target);
            result.Items.Add(new()
            {
                Name = file.Name, OriginalPath = "Unknown original location", Size = file.Length, InitializedSize = file.Length,
                Method = "PhotoRec signature", State = result.Cancelled || process.ExitCode != 0 ? RecoveryState.AtRisk : RecoveryState.Signature,
                RecoveredPath = target, RecoveredLastWriteUtc = file.LastWriteTimeUtc,
                Note = result.Cancelled ? "Advanced scan stopped. This saved file may be incomplete; inspect its contents."
                    : "Already saved by PhotoRec. Original filename, folder and deletion status are unknown. Inspect the recovered content."
            });
        }
        result.Warnings.Add("Advanced scan searched all bytes, including allocated data. Existing files and duplicates can appear. Original folder filters do not apply.");
        if (process.ExitCode != 0 && !result.Cancelled) result.Warnings.Add("PhotoRec exited with code " + process.ExitCode + ". Review engine-output.txt and photorec.log in the saved folder.");
        File.WriteAllText(Path.Combine(job, "recovery-report.json"), JsonSerializer.Serialize(new
        { Source = source.Description, Engine = "PhotoRec 7.2", process.ExitCode, result.Cancelled, Warnings = result.Warnings, Files = result.Items.Select(x => new { x.Name, x.Size, x.Method, x.Condition, x.Note, Output = x.RecoveredPath }) }, new JsonSerializerOptions { WriteIndented = true }));
        return new(result, job, process.ExitCode);
    }
    private static async Task<string> Drain(StreamReader reader)
    {
        var text = new System.Text.StringBuilder(); char[] buffer = new char[4096];
        while (true) { int count = await reader.ReadAsync(buffer); if (count == 0) break; if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length)); }
        return text.ToString();
    }
}
