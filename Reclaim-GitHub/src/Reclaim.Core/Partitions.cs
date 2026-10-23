using System.Buffers.Binary;

namespace Reclaim.Core;

public sealed record Partition(long Offset, long Length, string FileSystem);

public static class Partitions
{
    public static string Identify(ReadOnlySpan<byte> b)
    {
        if (b.Length < 512) return "Raw";
        if (b.Slice(3, 8).SequenceEqual("NTFS    "u8)) return "NTFS";
        if (b.Slice(3, 8).SequenceEqual("EXFAT   "u8)) return "exFAT";
        if (b.Slice(82, 5).SequenceEqual("FAT32"u8)) return "FAT";
        if (b.Slice(54, 3).SequenceEqual("FAT"u8)) return "FAT";
        return "Raw";
    }
    public static List<Partition> Discover(DiskReader reader, List<string> warnings)
    {
        if (reader.Length < 512) return [new(0, reader.Length, "Raw")];
        var boot = reader.Bytes(0, 512);
        string initial = IdentifyAt(reader, 0, reader.Length);
        if (initial != "Raw") return [new(0, reader.Length, initial)];
        var result = new List<Partition>();
        foreach (int sector in new[] { 512, 4096 })
        {
            if (reader.Length < sector * 2) continue;
            var gpt = reader.Bytes(sector, sector);
            if (!gpt.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) continue;
            int headerSize = (int)U32(gpt, 12); uint checksum = U32(gpt, 16);
            if (headerSize is < 92 || headerSize > sector) { warnings.Add("Invalid GPT header; falling back to raw scanning."); continue; }
            var header = gpt[..headerSize]; header.AsSpan(16, 4).Clear();
            if (Crc(header) != checksum) { warnings.Add("GPT header checksum failed; partition addresses were not trusted."); continue; }
            ulong tableLba = U64(gpt, 72); uint count = U32(gpt, 80), entrySize = U32(gpt, 84);
            long tableSize = (long)count * entrySize;
            if (count > 16384 || entrySize is < 128 or > 4096 || tableSize > 16 * 1024 * 1024 || tableLba > (ulong)(reader.Length / sector)) continue;
            long tableOffset = checked((long)tableLba * sector);
            if (tableOffset > reader.Length - tableSize) continue;
            var table = reader.Bytes(tableOffset, (int)tableSize);
            if (Crc(table) != U32(gpt, 88)) { warnings.Add("GPT partition-table checksum failed; using raw scanning."); continue; }
            for (int i = 0; i < count; i++)
            {
                int at = checked(i * (int)entrySize);
                if (table.AsSpan(at, 16).IndexOfAnyExcept((byte)0) < 0) continue;
                ulong first = U64(table, at + 32), last = U64(table, at + 40);
                if (last < first || last >= (ulong)(reader.Length / sector)) continue;
                Add(checked((long)first * sector), checked((long)(last - first + 1) * sector));
            }
            if (result.Count > 0) return Validate(result, warnings);
        }
        if (boot[510] == 0x55 && boot[511] == 0xaa)
        {
            for (int i = 0; i < 4; i++)
            {
                int at = 446 + 16 * i; byte type = boot[at + 4];
                long start = U32(boot, at + 8) * 512L, size = U32(boot, at + 12) * 512L;
                if (type is 0 or 0xee || size == 0) continue;
                if (type is 5 or 0xf or 0x85)
                {
                    long next = start; var visited = new HashSet<long>();
                    while (visited.Count < 128 && visited.Add(next) && next >= start && next <= Math.Min(reader.Length, start + size) - 512)
                    {
                        var ebr = reader.Bytes(next, 512);
                        if (ebr[510] != 0x55 || ebr[511] != 0xaa) break;
                        Add(next + U32(ebr, 454) * 512L, U32(ebr, 458) * 512L);
                        if (ebr[466] is not (5 or 0xf or 0x85)) break;
                        next = start + U32(ebr, 470) * 512L;
                    }
                }
                else Add(start, size);
            }
        }
        return result.Count > 0 ? Validate(result, warnings) : [new(0, reader.Length, "Raw")];
        void Add(long offset, long length)
        {
            if (offset < 512 || length < 512 || offset > reader.Length - length) return;
            result.Add(new(offset, length, IdentifyAt(reader, offset, length)));
        }
    }
    public static string IdentifyAt(DiskReader reader, long offset, long length)
    {
        if (length < 512) return "Raw";
        string type = Identify(reader.Bytes(offset, 512));
        if (type != "Raw") return type;
        if (length >= 2048)
        {
            byte[] s = reader.Bytes(offset + 1024, 1024);
            if (ExtVolume.IsExt(s)) return (U32(s, 96) & 0x40) != 0 ? "ext4" : (U32(s, 92) & 4) != 0 ? "ext3" : "ext2";
        }
        return "Raw";
    }
    public static List<Partition> SearchLost(DiskReader reader, Action<ScanProgress> progress, CancellationToken token, List<string> warnings)
    {
        var candidates = new List<Partition>(); var seen = new HashSet<long>();
        byte[] buffer = new byte[4 * 1024 * 1024 + 2048];
        for (long start = 0; start < reader.Length; start += 4 * 1024 * 1024)
        {
            token.ThrowIfCancellationRequested();
            int step = (int)Math.Min(4 * 1024 * 1024, reader.Length - start);
            int n = (int)Math.Min(buffer.Length, reader.Length - start); reader.ReadExactly(start, buffer.AsSpan(0, n));
            for (int p = 0; p < step && p + 512 <= n; p += 512)
            {
                token.ThrowIfCancellationRequested(); long at = start + p;
                try
                {
                    string fs = Identify(buffer.AsSpan(p, 512));
                    bool extCandidate = p + 2048 <= n && ExtVolume.IsExt(buffer.AsSpan(p + 1024, 1024));
                    if (fs == "Raw" && !extCandidate) continue;
                    var boot = buffer.AsSpan(p, 512).ToArray(); long length = 0;
                    if (fs == "NTFS" && boot[510] == 0x55 && boot[511] == 0xaa)
                    {
                        var g = NtfsGeometry.FromBoot(boot)!; length = g.VolumeLength;
                        long origin = at;
                        bool HasMft(long address) => address >= 0 && address <= reader.Length - 4 && reader.Bytes(address, 4).AsSpan().SequenceEqual("FILE"u8);
                        if (length > reader.Length - origin || !HasMft(origin + g.MftCluster * g.ClusterSize))
                        {
                            origin = at - length + g.SectorSize;
                            if (origin < 0 || !HasMft(origin + g.MftCluster * g.ClusterSize)) continue;
                            // A backup boot can identify the start, but without a primary boot
                            // metadata revalidation is not possible. Report it, don't repair it.
                            if (warnings.Count < 200) warnings.Add($"NTFS backup boot at {at:N0} suggests a volume at {origin:N0}; primary boot repair is required before metadata recovery."); continue;
                        }
                    }
                    else if (fs is "FAT" or "exFAT" && boot[510] == 0x55 && boot[511] == 0xaa)
                    {
                        _ = new FatVolume(reader, at, reader.Length - at);
                        long sectors = fs == "exFAT" ? checked((long)U64(boot, 72)) : U16(boot, 19) == 0 ? U32(boot, 32) : U16(boot, 19);
                        int sector = fs == "exFAT" ? 1 << boot[108] : U16(boot, 11); length = checked(sectors * sector);
                    }
                    else if (extCandidate && U16(buffer, p + 1024 + 90) == 0)
                    {
                        var ext = new ExtVolume(reader, at, reader.Length - at); fs = ext.FileSystem;
                        length = ExtVolume.DeclaredLength(buffer.AsSpan(p + 1024, 1024).ToArray());
                    }
                    if (length < 2048 || length > reader.Length - at || !seen.Add(at)) continue;
                    candidates.Add(new(at, length, fs));
                    if (candidates.Count >= 1024) { warnings.Add("Lost partition search reached its 1,024-candidate safety limit."); return Validate(candidates, warnings); }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentException) { }
            }
            progress(new("Searching for lost partitions", Math.Min(reader.Length, start + step), reader.Length, candidates.Count, $"{candidates.Count:N0} validated volume starts · no partition table changes"));
        }
        warnings.Add("Lost partition search is read-only. Only validated surviving primary NTFS/FAT/exFAT/ext volume structures are scanned; partition tables are not rewritten.");
        return Validate(candidates, warnings);
    }
    private static List<Partition> Validate(List<Partition> values, List<string> warnings)
    {
        var result = new List<Partition>();
        foreach (var part in values.OrderBy(x => x.Offset))
        {
            if (result.Count > 0 && part.Offset < result[^1].Offset + result[^1].Length)
            { warnings.Add("An overlapping partition entry was ignored."); continue; }
            result.Add(part);
        }
        return result;
    }
    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320); }
        return ~crc;
    }
    internal static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    internal static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    internal static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
}
