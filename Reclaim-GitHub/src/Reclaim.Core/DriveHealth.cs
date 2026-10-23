using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Reclaim.Core;

public sealed record DriveHealthReport(int Number, string Model, string Serial, string Health, string Status,
    long? Temperature, long? Wear, long? PowerOnHours, long? ReadErrors, long? WriteErrors, string Note);

public static class DriveHealth
{
    public static async Task<IReadOnlyList<DriveHealthReport>> Query(ScanSource? source, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Drive health requires Windows Storage Management.");
        if (source?.IsImage == true) throw new InvalidOperationException("An image has no live SMART sensors. Select its physical drive or choose all drives.");
        HashSet<int>? disks = source is null ? null : source.IsPhysicalDevice
            ? [int.Parse(source.DevicePath[17..])] : Native.VolumeDisks(Native.VolumeIdentity(source.VolumeRoot));
        const string script = """
            $ErrorActionPreference='Stop'
            $ProgressPreference='SilentlyContinue'
            try {
            $reports=@(Get-Disk | ForEach-Object {
                $d=$_; $c=$null; $note=''
                try { $c=$d | Get-StorageReliabilityCounter -ErrorAction Stop }
                catch { $note='Reliability/SMART counters are not exposed by this drive, USB bridge or provider, or elevation is required.' }
                [pscustomobject]@{Number=[int]$d.Number;Model=[string]$d.FriendlyName;Serial=[string]$d.SerialNumber;Health=[string]$d.HealthStatus;Status=($d.OperationalStatus -join ', ');Temperature=$c.Temperature;Wear=$c.Wear;PowerOnHours=$c.PowerOnHours;ReadErrors=$c.ReadErrorsUncorrected;WriteErrors=$c.WriteErrorsUncorrected;Note=$note}
            })
            ConvertTo-Json -InputObject $reports -Depth 3 -Compress
            } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
            """;
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start Windows Storage Management.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0) throw new IOException("Drive health is unavailable. Run as administrator; this Windows/USB provider may restrict health access. " + error[..Math.Min(error.Length, 1000)]);
        var reports = Parse(output);
        return disks is null ? reports : reports.Where(x => disks.Contains(x.Number)).ToArray();
    }
    public static IReadOnlyList<DriveHealthReport> Parse(string json)
    {
        if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("Unexpected drive health response size.");
        return JsonSerializer.Deserialize<List<DriveHealthReport>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
}
