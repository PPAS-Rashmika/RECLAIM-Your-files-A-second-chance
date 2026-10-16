using System.Buffers.Binary;
using System.Text;

namespace Reclaim.Core;

// Carving reconstructs contiguous files with surviving headers and boundaries.
// It cannot infer the original folder, filename, deletion status, or fragmented extents.
public sealed class SignatureScanner(DiskReader reader, CancellationToken token)
{
    public const long MaximumCarvedSize = 2L * 1024 * 1024 * 1024;
    private sealed record Magic(byte[] Bytes, int Adjustment = 0);
    private static readonly Magic[] Headers =
    [
        new([0xff, 0xd8, 0xff]), new([137, 80, 78, 71, 13, 10, 26, 10]),
        new("GIF87a"u8.ToArray()), new("GIF89a"u8.ToArray()), new("BM"u8.ToArray()),
        new("%PDF-"u8.ToArray()), new([0x50, 0x4b, 3, 4]), new("RIFF"u8.ToArray()),
        new("ftyp"u8.ToArray(), -4), new([0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c])
    ];
    public List<RecoveryItem> Scan(IEnumerable<ByteRange> ranges, long total, Action<ScanProgress> progress, int initialFound, Action<RecoveryItem>? onFound = null)
    {
        List<RecoveryItem> items = [];
        HashSet<long> seen = [];
        long done = 0, coveredUntil = -1;
        byte[] buffer = new byte[4 * 1024 * 1024 + 32];
        foreach (var range in ranges)
        {
            token.ThrowIfCancellationRequested();
            long end = checked(range.Offset + range.Length);
            for (long offset = range.Offset; offset < end;)
            {
                token.ThrowIfCancellationRequested();
                int step = (int)Math.Min(4 * 1024 * 1024, end - offset);
                int read = (int)Math.Min(step + 32L, end - offset);
                reader.ReadExactly(offset, buffer.AsSpan(0, read));
                SortedSet<long> candidates = [];
                foreach (var magic in Headers)
                {
                    int p = 0;
                    while (p < read)
                    {
                        int hit = buffer.AsSpan(p, read - p).IndexOf(magic.Bytes);
                        if (hit < 0) break;
                        int position = p + hit;
                        long candidate = offset + position + magic.Adjustment;
                        if (position < step && candidate >= range.Offset) candidates.Add(candidate);
                        p = position + 1;
                    }
                }
                foreach (long start in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (start < coveredUntil || !seen.Add(start)) continue;
                    try
                    {
                        var match = Inspect(start, Math.Min(end, start + MaximumCarvedSize));
                        if (match is null || match.Value.Length <= 0 || match.Value.Length > end - start) continue;
                        var (ext, length, note) = match.Value;
                        var item = new RecoveryItem
                        {
                            Name = $"found_{start:X12}.{ext}", OriginalPath = "Unsorted signatures", Size = length, InitializedSize = length,
                            Method = "Deep signature", State = RecoveryState.Signature, CarvedOffset = start,
                            Note = "Contiguous file structure found. Original name, folder and deletion status are unknown. " + note
                        };
                        items.Add(item); onFound?.Invoke(item);
                        coveredUntil = start + length;
                    }
                    catch (Exception ex) when (ex is IOException or OverflowException or ArgumentException) { }
                }
                offset += step; done += step;
                progress(new("Deep signature search", done, total, initialFound + items.Count, $"{Categories.Size(done)} read · {items.Count:N0} signatures"));
            }
        }
        return items;
    }
    private (string Extension, long Length, string Note)? Inspect(long start, long end)
    {
        if (end - start < 16) return null;
        byte[] head = reader.Bytes(start, (int)Math.Min(64, end - start));
        if (head.AsSpan().StartsWith(new byte[] {137, 80, 78, 71, 13, 10, 26, 10})) return Png(start, end);
        if (head[0] == 0xff && head[1] == 0xd8 && head[2] == 0xff) return Jpeg(start, end);
        if (head.AsSpan().StartsWith("GIF87a"u8) || head.AsSpan().StartsWith("GIF89a"u8)) return Gif(start, end);
        if (head.AsSpan().StartsWith("%PDF-"u8) && head.Length >= 8 && head[5] is (byte)'1' or (byte)'2' && head[6] == '.')
        {
            long eof = FindMarker(start + 8, end, "%%EOF"u8.ToArray());
            if (eof < 0) return null;
            long length = eof + 5 - start;
            for (int i = 0; i < 2 && start + length < end; i++)
            { byte b = reader.Bytes(start + length, 1)[0]; if (b is 10 or 13) length++; else break; }
            return ("pdf", length, "Ends at the first PDF EOF marker; later incremental revisions may be missing.");
        }
        if (head.AsSpan().StartsWith("BM"u8))
        {
            long length = U32(head, 2); uint pixelOffset = U32(head, 10), dib = U32(head, 14);
            if (length >= 26 && length <= end - start && pixelOffset >= 14 + dib && pixelOffset < length && dib is 12 or 40 or 52 or 56 or 108 or 124)
                return ("bmp", length, "Declared bitmap size checked.");
        }
        if (head.AsSpan().StartsWith("RIFF"u8))
        {
            long length = U32(head, 4) + 8L;
            string form = Encoding.ASCII.GetString(head, 8, 4);
            string? ext = form switch { "WAVE" => "wav", "AVI " => "avi", "WEBP" => "webp", _ => null };
            if (ext is not null && length >= 20 && length <= end - start)
            {
                long p = start + 12;
                while (p + 8 <= start + length)
                {
                    token.ThrowIfCancellationRequested();
                    long size = U32(reader.Bytes(p + 4, 4), 0);
                    p += 8 + size + (size & 1);
                }
                if (p == start + length) return (ext, length, "RIFF chunk boundaries checked.");
            }
        }
        if (head.AsSpan().StartsWith(new byte[] {0x50, 0x4b, 3, 4})) return Zip(start, end);
        if (head.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return IsoMedia(start, end);
        if (head.AsSpan().StartsWith(new byte[] {0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c}) && head.Length >= 32 && head[6] == 0)
        {
            uint headerCrc = Crc32.Update(0xffffffff, head.AsSpan(12, 20)) ^ 0xffffffff;
            ulong nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(12)), nextSize = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(20));
            if (headerCrc != U32(head, 8) || nextOffset > (ulong)(end - start - 32) || nextSize > (ulong)(end - start - 32) - nextOffset || nextSize == 0) return null;
            long length = checked(32 + (long)nextOffset + (long)nextSize); uint crc = 0xffffffff;
            var block = new byte[65536];
            for (long p = 0; p < (long)nextSize;)
            {
                token.ThrowIfCancellationRequested(); int n = (int)Math.Min(block.Length, (long)nextSize - p);
                reader.ReadExactly(start + 32 + (long)nextOffset + p, block.AsSpan(0, n)); crc = Crc32.Update(crc, block.AsSpan(0, n)); p += n;
            }
            return (crc ^ 0xffffffff) == U32(head, 28) ? ("7z", length, "7-Zip start and next-header checksums verified.") : null;
        }
        return null;
    }
    private (string, long, string)? Png(long start, long end)
    {
        long p = start + 8; bool first = true, hasData = false;
        while (p + 12 <= end)
        {
            token.ThrowIfCancellationRequested();
            var h = reader.Bytes(p, 8); long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            string type = Encoding.ASCII.GetString(h, 4, 4);
            if (size > end - p - 12 || (first && (type != "IHDR" || size != 13))) return null;
            if (!h.AsSpan(4, 4).ToArray().All(x => x is >= 65 and <= 90 or >= 97 and <= 122)) return null;
            uint crc = Crc32.Update(0xffffffff, h.AsSpan(4, 4));
            byte[] block = new byte[65536];
            for (long part = 0; part < size;)
            {
                token.ThrowIfCancellationRequested(); int n = (int)Math.Min(block.Length, size - part);
                reader.ReadExactly(p + 8 + part, block.AsSpan(0, n)); crc = Crc32.Update(crc, block.AsSpan(0, n)); part += n;
            }
            if ((crc ^ 0xffffffff) != BinaryPrimitives.ReadUInt32BigEndian(reader.Bytes(p + 8 + size, 4))) return null;
            p += size + 12; first = false;
            if (type == "IDAT") hasData = true;
            if (type == "IEND") return size == 0 && hasData ? ("png", p - start, "PNG chunk checksums verified.") : null;
        }
        return null;
    }
    private (string, long, string)? Jpeg(long start, long end)
    {
        long p = start + 2; bool frame = false;
        while (p + 4 <= end)
        {
            token.ThrowIfCancellationRequested(); var marker = reader.Bytes(p, 4);
            if (marker[0] != 0xff) return null;
            if (marker[1] == 0xff) { p++; continue; }
            int id = marker[1];
            if (id is 0 or 0xd8 or 0xd9) return null;
            if (id is 0x01 or >= 0xd0 and <= 0xd7) { p += 2; continue; }
            int size = BinaryPrimitives.ReadUInt16BigEndian(marker.AsSpan(2));
            if (size < 2 || size > end - p - 2) return null;
            if (id is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf) frame = true;
            p += 2 + size;
            if (id == 0xda)
            {
                if (!frame) return null;
                long eof = FindMarker(p, end, new byte[] {0xff, 0xd9});
                return eof < 0 ? null : ("jpg", eof + 2 - start, "JPEG header, frame and end marker checked; image decoding is not guaranteed.");
            }
        }
        return null;
    }
    private (string, long, string)? Gif(long start, long end)
    {
        var head = reader.Bytes(start, 13); long p = start + 13;
        if ((head[10] & 128) != 0) p += 3 * (1 << ((head[10] & 7) + 1));
        bool image = false;
        while (p < end)
        {
            token.ThrowIfCancellationRequested(); byte kind = reader.Bytes(p++, 1)[0];
            if (kind == 0x3b) return image ? ("gif", p - start, "GIF block boundaries checked.") : null;
            if (kind == 0x21) p++;
            else if (kind == 0x2c)
            {
                if (p + 9 > end) return null;
                byte packed = reader.Bytes(p + 8, 1)[0]; p += 9;
                if ((packed & 128) != 0) p += 3 * (1 << ((packed & 7) + 1));
                p++; image = true;
            }
            else return null;
            while (p < end)
            {
                token.ThrowIfCancellationRequested(); int n = reader.Bytes(p++, 1)[0];
                if (n == 0) break; p += n;
            }
        }
        return null;
    }
    private (string, long, string)? Zip(long start, long end)
    {
        long search = start + 30;
        while (search + 22 <= end)
        {
            token.ThrowIfCancellationRequested();
            long eof = FindMarker(search, end, new byte[] {0x50, 0x4b, 5, 6});
            if (eof < 0 || eof + 22 > end) return null;
            var e = reader.Bytes(eof, 22);
            int entries = U16(e, 10), comment = U16(e, 20);
            long directorySize = U32(e, 12), directoryOffset = U32(e, 16);
            if (U16(e, 4) == 0 && U16(e, 6) == 0 && U16(e, 8) == entries && entries > 0 && entries != 65535 &&
                directorySize > 0 && directoryOffset > 0 && directoryOffset + directorySize == eof - start && eof + 22 + comment <= end)
            {
                long p = start + directoryOffset; string ext = "zip"; bool valid = true;
                for (int i = 0; i < entries; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (p + 46 > eof) { valid = false; break; }
                    var c = reader.Bytes(p, 46);
                    if (!c.AsSpan(0, 4).SequenceEqual(new byte[] {0x50, 0x4b, 1, 2})) { valid = false; break; }
                    int name = U16(c, 28), extra = U16(c, 30), note = U16(c, 32);
                    if (p + 46L + name + extra + note > eof) { valid = false; break; }
                    string filename = Encoding.UTF8.GetString(reader.Bytes(p + 46, name));
                    if (filename.StartsWith("word/", StringComparison.Ordinal)) ext = "docx";
                    else if (filename.StartsWith("xl/", StringComparison.Ordinal)) ext = "xlsx";
                    else if (filename.StartsWith("ppt/", StringComparison.Ordinal)) ext = "pptx";
                    if (filename == "META-INF/container.xml" && ext == "zip") ext = "epub";
                    uint local = U32(c, 42), compressed = U32(c, 20);
                    if (local + 30L > directoryOffset) { valid = false; break; }
                    var l = reader.Bytes(start + local, 30);
                    if (!l.AsSpan(0, 4).SequenceEqual(new byte[] {0x50, 0x4b, 3, 4}) || local + 30L + U16(l, 26) + U16(l, 28) + compressed > directoryOffset)
                    { valid = false; break; }
                    p += 46 + name + extra + note;
                }
                if (valid && p == eof) return (ext, eof + 22 + comment - start, "ZIP directory and member bounds checked. ZIP64 / split archives are unsupported; content CRCs are not verified.");
            }
            search = eof + 4;
        }
        return null;
    }
    private (string, long, string)? IsoMedia(long start, long end)
    {
        long p = start; bool data = false, metadata = false;
        string ext = "mp4";
        HashSet<string> boxes = ["ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "moof", "mfra", "styp", "sidx", "pdin"];
        while (p + 8 <= end)
        {
            token.ThrowIfCancellationRequested(); var h = reader.Bytes(p, 8);
            string type = Encoding.ASCII.GetString(h, 4, 4);
            if (!boxes.Contains(type)) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h); int header = 8;
            if (size == 1)
            {
                if (p + 16 > end) break;
                ulong large = BinaryPrimitives.ReadUInt64BigEndian(reader.Bytes(p + 8, 8));
                if (large > long.MaxValue) break; size = (long)large; header = 16;
            }
            // A box extending to EOF has no trustworthy endpoint in unallocated space.
            if (size < header || size > end - p) break;
            if (type == "ftyp")
            {
                if (p != start || size < 16 || size > 4096) return null;
                string brand = Encoding.ASCII.GetString(reader.Bytes(p + header, 4));
                ext = brand switch { "qt  " => "mov", "M4A " or "M4B " => "m4a", "avif" or "avis" => "avif", "heic" or "heix" or "mif1" => "heic", "3gp4" or "3gp5" or "3gp6" => "3gp", _ => "mp4" };
            }
            if (type == "mdat") data = true;
            if (type is "moov" or "meta") metadata = true;
            p += size;
        }
        return data && metadata ? (ext, p - start, "Container box boundaries checked. Some fragmented or zero-sized media containers are unsupported.") : null;
    }
    private long FindMarker(long start, long end, byte[] marker)
    {
        byte[] bytes = new byte[128 * 1024 + 32];
        for (long p = start; p < end;)
        {
            token.ThrowIfCancellationRequested();
            int step = (int)Math.Min(128 * 1024, end - p);
            int n = (int)Math.Min(step + marker.Length - 1L, end - p);
            reader.ReadExactly(p, bytes.AsSpan(0, n));
            int hit = bytes.AsSpan(0, n).IndexOf(marker);
            if (hit >= 0) return p + hit;
            p += step;
        }
        return -1;
    }
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
}

public static class Crc32
{
    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }
    public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    { foreach (byte b in bytes) crc = Table[(crc ^ b) & 255] ^ (crc >> 8); return crc; }
}
