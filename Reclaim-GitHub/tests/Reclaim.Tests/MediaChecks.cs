using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Reclaim.Core;

namespace Reclaim.Tests;

internal static class MediaChecks
{
    private static void Assert(bool value) { if (!value) throw new Exception("Media assertion failed."); }
    private static void Generate(string engine, params string[] args)
    {
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-nostdin", "-y" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; string error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception(error);
    }
    public static void Run(string folder, string engine, Action<string, Action> test)
    {
        folder = Path.Combine(folder, "media-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(folder);
        test("Real FFmpeg video remux recovers an AVI with damaged index without changing input", () =>
        {
            string path = Path.Combine(folder, "damaged.avi"), output = Path.Combine(folder, "repaired.mp4");
            Generate(engine, "-f", "lavfi", "-i", "color=c=navy:s=96x64:r=10:d=1", "-c:v", "mpeg4", "-an", path);
            byte[] bytes = File.ReadAllBytes(path); int at = bytes.AsSpan().IndexOf("idx1"u8); Assert(at > 0);
            Array.Clear(bytes, at + 8, bytes.Length - at - 8); File.WriteAllBytes(path, bytes); byte[] before = SHA256.HashData(bytes);
            MediaRepair.RemuxVideo(path, output, engine, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(File.Exists(output) && new FileInfo(output).Length > 100); Assert(SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(before));
        });
        test("Real camera fMP4 fragments remain decodable after scatter recovery", () =>
        {
            string reference = Path.Combine(folder, "reference.mp4");
            Generate(engine, "-f", "lavfi", "-i", "testsrc2=s=96x64:r=10:d=2", "-c:v", "libx264", "-g", "5", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-an", reference);
            var init = CameraRecovery.LoadInitialization(reference); byte[] input = File.ReadAllBytes(reference), raw = new byte[4 * 1024 * 1024]; int expected = 0;
            for (int p = 0; p + 8 <= input.Length;)
            {
                int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(p))); Assert(size >= 8 && size <= input.Length - p);
                if (input.AsSpan(p + 4, 4).SequenceEqual("moof"u8))
                {
                    int media = checked((int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(p + size)));
                    input.AsSpan(p, size + media).CopyTo(raw.AsSpan(4096 + expected * 512 * 1024)); expected++;
                }
                p += size;
            }
            string image = Path.Combine(folder, "scattered.raw"); File.WriteAllBytes(image, raw);
            var scan = RecoveryEngine.Scan(new(image, true), ScanMode.Camera, null, CancellationToken.None, cameraReference: init);
            var clips = scan.Items.Where(x => x.Method == "Camera fMP4 reconstruction").ToList(); Assert(expected >= 2 && clips.Count == expected);
            for (int i = 0; i < clips.Count; i++)
            {
                string clip = Path.Combine(folder, "clip" + i + ".mp4"); using (var target = File.Create(clip)) using (var source = RecoveryContent.Open(scan, clips[i])) source.CopyTo(target);
                MediaRepair.RemuxVideo(clip, Path.Combine(folder, "verified" + i + ".mp4"), engine, null, CancellationToken.None).GetAwaiter().GetResult();
            }
        });
        test("Video repair rejects missing codec metadata without reporting success", () =>
        {
            string path = Path.Combine(folder, "unrecoverable.mp4"), output = Path.Combine(folder, "invalid-output.mp4"); File.WriteAllBytes(path, new byte[128]); bool failed = false;
            try { MediaRepair.RemuxVideo(path, output, engine, null, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidDataException) { failed = true; }
            Assert(failed && !File.Exists(output));
        });
    }
}
