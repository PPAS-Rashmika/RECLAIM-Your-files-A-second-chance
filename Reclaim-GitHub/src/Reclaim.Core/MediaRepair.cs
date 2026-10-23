using System.Diagnostics;

namespace Reclaim.Core;

public static class MediaRepair
{
    public static async Task RemuxVideo(string input, string output, string ffmpeg, Action<string>? progress, CancellationToken token)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase) || File.Exists(output)) throw new IOException("Repair output must be a new file.");
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("Choose an extracted FFmpeg executable first.", ffmpeg);
        string format = Path.GetExtension(input).ToLowerInvariant() switch { ".mp4" or ".mov" or ".m4v" or ".3gp" => "mov", ".avi" => "avi", ".mkv" or ".webm" => "matroska", _ => throw new InvalidDataException("Video remux supports MP4/MOV/3GP/AVI/MKV/WebM containers.") };
        try
        {
            progress?.Invoke("Rebuilding the video container…");
            await Run(ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe", "-err_detect", "ignore_err", "-fflags", "+genpts+discardcorrupt", "-f", format, "-i", input,
                "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", output], token);
            if (!File.Exists(output) || new FileInfo(output).Length < 32) throw new InvalidDataException("The engine did not produce a usable video.");
            progress?.Invoke("Checking every decoded audio/video frame…");
            await Run(ffmpeg, ["-hide_banner", "-v", "error", "-xerror", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", output, "-map", "0:v?", "-map", "0:a?", "-f", "null", "-"], token);
        }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
    }
    private static async Task Run(string executable, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the media engine.");
        var errors = Drain(process.StandardError); var output = Drain(process.StandardOutput);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await Task.WhenAll(errors, output); throw; }
        string diagnostics = await errors; await output;
        if (process.ExitCode != 0) throw new InvalidDataException("Video repair/validation failed. Missing media or codec headers may require a reference-based specialist repair.\n" + diagnostics);
    }
    private static async Task<string> Drain(StreamReader stream)
    {
        char[] buffer = new char[2048]; string tail = "";
        for (int n; (n = await stream.ReadAsync(buffer)) > 0;) { tail += new string(buffer, 0, n); if (tail.Length > 4096) tail = tail[^4096..]; }
        return tail;
    }
}
