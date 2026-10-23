using System.Buffers.Binary;
using System.IO.Compression;

namespace Reclaim.Core;

public static class FileAssessment
{
    public static string Check(ScanResult scan, RecoveryItem item, CancellationToken token)
    {
        try
        {
            if (RecoveryEngine.TouchesBadRanges(item, scan.BadRanges)) throw new InvalidDataException("Mapped bytes overlap unreadable source areas.");
            using var stream = RecoveryContent.Open(scan, item);
            byte[] h = new byte[(int)Math.Min(32, stream.Length)]; stream.ReadExactly(h); stream.Position = 0;
            string message;
            if (h.AsSpan().StartsWith(new byte[] {137, 80, 78, 71, 13, 10, 26, 10})) message = Png(stream, token);
            else if (h.AsSpan().StartsWith(new byte[] {0x50, 0x4b, 3, 4})) message = Zip(stream, token);
            else if (h.Length >= 2 && h[0] == 0xff && h[1] == 0xd8)
            {
                if (stream.Length < 4) throw new InvalidDataException("Truncated JPEG.");
                stream.Position = stream.Length - 2; var tail = new byte[2]; stream.ReadExactly(tail);
                if (tail[0] != 0xff || tail[1] != 0xd9) throw new InvalidDataException("JPEG end marker is missing. Try repairing a saved copy.");
                message = "JPEG boundary markers present · pixel decoding not checked";
            }
            else
            {
                if (stream.Length > 0) { stream.Position = stream.Length - 1; if (stream.ReadByte() < 0) throw new EndOfStreamException(); }
                message = "Mapped start/end readable · content integrity not established";
            }
            item.ValidationFailed = false; item.Validation = message; return message;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { item.ValidationFailed = true; item.Validation = ex.Message; return "Validation failed: " + ex.Message; }
    }
    private static string Png(Stream stream, CancellationToken token)
    {
        if (stream.Length > 512L * 1024 * 1024) return "PNG validation deferred · exceeds 512 MB check limit";
        stream.Position = 8; bool data = false, first = true; byte[] buffer = new byte[65536], h = new byte[8], tail = new byte[4];
        for (int count = 0; count < 1_000_000 && stream.Position + 12 <= stream.Length; count++)
        {
            token.ThrowIfCancellationRequested(); stream.ReadExactly(h); uint n = BinaryPrimitives.ReadUInt32BigEndian(h);
            if (n > stream.Length - stream.Position - 4 || first && (!h.AsSpan(4, 4).SequenceEqual("IHDR"u8) || n != 13)) throw new InvalidDataException("Invalid PNG chunk bounds.");
            uint crc = Crc32.Update(uint.MaxValue, h.AsSpan(4));
            for (long left = n; left > 0;) { token.ThrowIfCancellationRequested(); int take = (int)Math.Min(buffer.Length, left); stream.ReadExactly(buffer.AsSpan(0, take)); crc = Crc32.Update(crc, buffer.AsSpan(0, take)); left -= take; }
            stream.ReadExactly(tail); if ((crc ^ uint.MaxValue) != BinaryPrimitives.ReadUInt32BigEndian(tail)) throw new InvalidDataException("PNG chunk checksum failed.");
            first = false; if (h.AsSpan(4, 4).SequenceEqual("IDAT"u8)) data = true;
            if (h.AsSpan(4, 4).SequenceEqual("IEND"u8)) return n == 0 && data ? "PNG chunk checksums verified · original bytes cannot be proven" : throw new InvalidDataException("PNG image data missing.");
        }
        throw new InvalidDataException("PNG end chunk is missing.");
    }
    private static string Zip(Stream stream, CancellationToken token)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true); long checkedBytes = 0; int count = 0; var buffer = new byte[65536];
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (++count > 10000 || entry.Length > 512L * 1024 * 1024 - checkedBytes) return "ZIP directory readable · full content check exceeds safety limit";
            using var content = entry.Open(); uint crc = uint.MaxValue; long length = 0;
            for (int n; (n = content.Read(buffer)) > 0;) { token.ThrowIfCancellationRequested(); crc = Crc32.Update(crc, buffer.AsSpan(0, n)); length += n; if (length > entry.Length) throw new InvalidDataException("ZIP member exceeds declared size."); }
            if (length != entry.Length || (crc ^ uint.MaxValue) != entry.Crc32) throw new InvalidDataException("ZIP member checksum failed: " + entry.FullName);
            checkedBytes += length;
        }
        return $"ZIP content checksums verified · {count:N0} entries";
    }
}
