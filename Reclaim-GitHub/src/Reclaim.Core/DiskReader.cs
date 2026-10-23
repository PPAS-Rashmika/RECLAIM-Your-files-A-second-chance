using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Reclaim.Core;

// The source is opened with READ access only. No write, lock, dismount or repair control codes exist here.
public sealed class DiskReader : IDisposable
{
    private readonly Stream? file;
    private readonly bool tolerateErrors;
    private readonly CancellationToken token;
    private readonly List<ByteRange> badRanges = [];
    public IReadOnlyList<ByteRange> BadRanges => badRanges;
    public int ReadRetries { get; set; } = 1;
    private readonly SafeFileHandle? device;
    private readonly object gate = new();
    private readonly byte[] cache = new byte[64 * 1024];
    private long cacheOffset = -1;
    private int cacheCount;
    public long Length { get; }
    public DiskReader(ScanSource source, bool tolerateErrors = false, CancellationToken token = default)
    {
        this.tolerateErrors = tolerateErrors; this.token = token;
        if (source.IsImage)
        {
            file = new FileStream(source.DevicePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
            Length = file.Length;
        }
        else
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Drive scanning requires Windows.");
            device = Native.CreateFileW(source.DevicePath, 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (device.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); device.Dispose();
                if (error == 5) throw new UnauthorizedAccessException("Raw drive scanning needs administrator access. Use 'Restart as administrator' or scan a disk image.");
                throw new IOException(new Win32Exception(error).Message);
            }
            var length = new byte[8];
            if (!Native.DeviceIoControl(device, 0x7405c, null, 0, length, length.Length, out _, IntPtr.Zero))
            { device.Dispose(); throw new IOException("Cannot read the drive length: " + new Win32Exception(Marshal.GetLastWin32Error()).Message); }
            Length = BitConverter.ToInt64(length);
            // Extend this read-only handle to the partition boundary, including the final sectors.
            // This changes handle behavior; it does not lock, dismount, repair or write the volume.
            Native.DeviceIoControl(device, 0x90083, null, 0, [], 0, out _, IntPtr.Zero);
        }
    }
    // Also used by fault-injection verification; the reader owns this stream.
    public static DiskReader FromStream(Stream source, bool tolerateErrors = false, CancellationToken token = default) => new(source, tolerateErrors, token, true);
    private DiskReader(Stream source, bool tolerateErrors, CancellationToken token, bool ownsStream)
    {
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("A seekable read stream is required.");
        file = source; Length = source.Length; this.tolerateErrors = tolerateErrors; this.token = token;
    }

    public byte[] Bytes(long offset, int count)
    {
        if (count < 0 || count > 64 * 1024 * 1024) throw new InvalidDataException("Read is outside the allowed range.");
        var bytes = new byte[count]; ReadExactly(offset, bytes); return bytes;
    }
    public void ReadExactly(long offset, Span<byte> output)
    {
        if (offset < 0 || offset > Length || output.Length > Length - offset) throw new EndOfStreamException("The source is truncated or the data address is invalid.");
        lock (gate)
        {
            if (output.Length <= 32 * 1024)
            {
                int done = 0;
                while (done < output.Length)
                {
                    long pos = offset + done;
                    if (pos < cacheOffset || pos >= cacheOffset + cacheCount)
                    {
                        long next = pos / cache.Length * cache.Length;
                        int count = (int)Math.Min(cache.Length, Length - next);
                        try { ReadDirect(next, cache.AsSpan(0, count)); }
                        catch (IOException)
                        {
                            cacheOffset = -1; cacheCount = 0;
                            ReadDirect(pos, output[done..]); return;
                        }
                        cacheOffset = next; cacheCount = count;
                    }
                    int start = (int)(pos - cacheOffset);
                    int n = Math.Min(output.Length - done, cacheCount - start);
                    cache.AsSpan(start, n).CopyTo(output[done..]); done += n;
                }
            }
            else ReadDirect(offset, output);
        }
    }
    public void InvalidateCache() { lock (gate) { cacheOffset = -1; cacheCount = 0; } }
    private void ReadDirect(long offset, Span<byte> output)
    {
        token.ThrowIfCancellationRequested();
        long readEnd = offset + output.Length;
        if (!tolerateErrors || !badRanges.Any(x => offset < x.Offset + x.Length && x.Offset < readEnd))
        {
            try { ReadWithRetry(offset, output); return; }
            catch (IOException) when (tolerateErrors) { }
        }
        // A failed large read is split into aligned 4 KiB units. Unreadable bytes
        // are placeholders for scanning only; export always uses strict reads.
        int done = 0;
        while (done < output.Length)
        {
            token.ThrowIfCancellationRequested(); long pos = offset + done;
            int count = (int)Math.Min(output.Length - done, 4096 - pos % 4096);
            var part = output.Slice(done, count);
            if (badRanges.Any(x => pos >= x.Offset && pos + count <= x.Offset + x.Length)) { part.Clear(); done += count; continue; }
            try { ReadWithRetry(pos, part); }
            catch (IOException)
            {
                part.Clear(); AddBad(pos / 4096 * 4096, Math.Min(4096, Length - pos / 4096 * 4096));
            }
            done += count;
        }
    }
    private void AddBad(long offset, long length)
    {
        if (badRanges.Any(x => offset >= x.Offset && offset + length <= x.Offset + x.Length)) return;
        badRanges.Add(new(offset, length));
        badRanges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        for (int i = badRanges.Count - 1; i > 0; i--)
            if (badRanges[i].Offset <= badRanges[i - 1].Offset + badRanges[i - 1].Length)
            {
                var a = badRanges[i - 1]; var b = badRanges[i];
                badRanges[i - 1] = new(a.Offset, Math.Max(a.Offset + a.Length, b.Offset + b.Length) - a.Offset); badRanges.RemoveAt(i);
            }
        // Bound memory even on extensively damaged media; larger ranges remain conservative.
        if (badRanges.Count > 16384)
        {
            var a = badRanges[0]; var b = badRanges[1]; badRanges[0] = new(a.Offset, b.Offset + b.Length - a.Offset); badRanges.RemoveAt(1);
        }
    }
    private void ReadWithRetry(long offset, Span<byte> output)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { ReadOnce(offset, output); return; }
            catch (IOException) when (attempt < Math.Clamp(ReadRetries, 0, 3)) { }
        }
    }
    private void ReadOnce(long offset, Span<byte> output)
    {
        if (file is not null)
        {
            file.Position = offset; file.ReadExactly(output); return;
        }
        long aligned = offset / 4096 * 4096;
        int skip = (int)(offset - aligned);
        int length = (int)Math.Min((skip + output.Length + 4095L) / 4096 * 4096, Length - aligned);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (!Native.SetFilePointerEx(device!, aligned, out _, 0) ||
                !Native.ReadFile(device!, buffer, length, out int read, IntPtr.Zero))
                throw new IOException("Source read failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (read < skip + output.Length) throw new EndOfStreamException("Short disk read.");
            buffer.AsSpan(skip, output.Length).CopyTo(output);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    public void Dispose() { file?.Dispose(); device?.Dispose(); }
}

internal static class Native
{
    internal static HashSet<int> VolumeDisks(string identity)
    {
        using var handle = CreateFileW(identity.TrimEnd('\\'), 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        byte[] extents = new byte[65536];
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x560000, null, 0, extents, extents.Length, out int returned, IntPtr.Zero) || returned < 8)
            throw new IOException("Cannot verify the destination's physical disk. Choose another local drive.");
        uint count = BitConverter.ToUInt32(extents, 0); if (count == 0 || count > (returned - 8) / 24) throw new IOException("Invalid destination disk information.");
        return Enumerable.Range(0, (int)count).Select(i => BitConverter.ToInt32(extents, 8 + i * 24)).ToHashSet();
    }
    internal static string[] PhysicalDevices()
    {
        var names = new char[65536]; uint size = QueryDosDeviceW(null, names, names.Length);
        if (size == 0) return [];
        return new string(names, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, "^PhysicalDrive[0-9]+$"))
            .OrderBy(x => int.Parse(x[13..])).Select(x => @"\\.\" + x).ToArray();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string? name, char[] target, int size);
    internal static string VolumeIdentity(string directory)
    {
        using var handle = CreateFileW(directory, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot verify the output volume: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var text = new System.Text.StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 1);
        if (count == 0 || count >= text.Capacity) throw new IOException("Cannot safely resolve the output volume.");
        string path = text.ToString(); int close = path.IndexOf('}');
        if (!path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || close < 0)
            throw new IOException("Choose a local destination drive whose volume can be verified.");
        return path[..(close + 1)];
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, System.Text.StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(SafeFileHandle handle, byte[] data, int length, out int read, IntPtr overlapped);
}
