using System.Text;
using static Reclaim.Core.Partitions;

namespace Reclaim.Core;

// FAT and exFAT directory recovery. Missing FAT chains are never presented as proven layouts.
public sealed class FatVolume
{
    private readonly DiskReader reader;
    private readonly long offset, length, fat, heap;
    private readonly int sector, cluster, fatBits;
    private readonly uint clusters, rootCluster;
    private readonly long rootOffset;
    private readonly int rootSize;
    private readonly bool exfat;
    private byte[]? bitmap;
    private readonly HashSet<uint> visitedDirectories = [];
    public int ClusterSize => cluster;
    public string FileSystem => exfat ? "exFAT" : "FAT" + fatBits;

    public FatVolume(DiskReader reader, long offset, long available)
    {
        this.reader = reader; this.offset = offset;
        var boot = reader.Bytes(offset, 512); exfat = boot.AsSpan(3, 8).SequenceEqual("EXFAT   "u8);
        if (exfat)
        {
            if (boot[108] is < 9 or > 12 || boot[109] + boot[108] > 25 || boot[110] is < 1 or > 2) throw new InvalidDataException("Invalid exFAT geometry.");
            sector = 1 << boot[108]; cluster = sector << boot[109];
            ulong sectors = U64(boot, 72); if (sectors > (ulong)(available / sector)) throw new InvalidDataException("Truncated exFAT volume.");
            length = (long)sectors * sector;
            uint fatSectors = U32(boot, 84); int active = boot[110] == 2 ? U16(boot, 106) & 1 : 0;
            fat = offset + (U32(boot, 80) + (long)active * fatSectors) * sector;
            heap = offset + U32(boot, 88) * (long)sector; clusters = U32(boot, 92); rootCluster = U32(boot, 96); fatBits = 32;
            if ((long)(clusters + 2UL) * 4 > fatSectors * (long)sector) throw new InvalidDataException("exFAT table is too short.");
        }
        else
        {
            sector = U16(boot, 11); byte perCluster = boot[13];
            if (sector is not (512 or 1024 or 2048 or 4096) || perCluster == 0 || (perCluster & (perCluster - 1)) != 0 || boot[16] is < 1 or > 4) throw new InvalidDataException("Invalid FAT geometry.");
            cluster = sector * perCluster;
            long total = U16(boot, 19); if (total == 0) total = U32(boot, 32);
            long fatSize = U16(boot, 22); if (fatSize == 0) fatSize = U32(boot, 36);
            int reserved = U16(boot, 14); if (reserved == 0 || fatSize == 0 || total == 0) throw new InvalidDataException("Invalid FAT table.");
            long rootSectors = (U16(boot, 17) * 32L + sector - 1) / sector;
            long dataSectors = total - reserved - boot[16] * fatSize - rootSectors;
            if (dataSectors <= 0) throw new InvalidDataException("No FAT data area.");
            clusters = checked((uint)(dataSectors / perCluster)); fatBits = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;
            int active = fatBits == 32 && (U16(boot, 40) & 0x80) != 0 ? U16(boot, 40) & 0xf : 0;
            if (active >= boot[16]) throw new InvalidDataException("Invalid active FAT.");
            fat = offset + (reserved + active * fatSize) * sector;
            rootOffset = offset + (reserved + boot[16] * fatSize) * sector; rootSize = checked((int)(rootSectors * sector));
            heap = rootOffset + rootSize; rootCluster = fatBits == 32 ? U32(boot, 44) & 0x0fffffff : 0;
            length = checked(total * sector);
            if ((clusters + 2L) * fatBits > fatSize * sector * 8) throw new InvalidDataException("FAT table is too short.");
        }
        if (length <= 0 || length > available || heap < offset || heap + clusters * (long)cluster > offset + length || fat < offset || fat >= heap)
            throw new InvalidDataException("Filesystem geometry lies outside the partition.");
        if ((exfat || fatBits == 32) && !ValidCluster(rootCluster)) throw new InvalidDataException("Invalid root directory cluster.");
    }
    private bool ValidCluster(uint value) => value >= 2 && value - 2 < clusters;
    private long Address(uint value) => ValidCluster(value) ? heap + (value - 2L) * cluster : throw new InvalidDataException("Cluster is outside the volume.");
    private uint Next(uint value)
    {
        if (!ValidCluster(value)) throw new InvalidDataException("Invalid cluster reference.");
        if (fatBits == 12)
        {
            var bytes = reader.Bytes(fat + value + value / 2, 2); uint packed = U16(bytes, 0);
            return (value & 1) == 0 ? packed & 0xfff : packed >> 4;
        }
        var entry = reader.Bytes(fat + value * (fatBits / 8L), fatBits / 8);
        return fatBits == 16 ? U16(entry, 0) : U32(entry, 0) & (exfat ? uint.MaxValue : 0x0fffffff);
    }
    private bool End(uint next) => next >= (fatBits == 12 ? 0xff8u : fatBits == 16 ? 0xfff8u : exfat ? 0xfffffff8u : 0x0ffffff8u);
    private List<uint> Chain(uint first, int limit, CancellationToken token)
    {
        var result = new List<uint>(); var visited = new HashSet<uint>(); uint at = first;
        while (ValidCluster(at) && result.Count < limit && visited.Add(at))
        { token.ThrowIfCancellationRequested(); result.Add(at); uint next = Next(at); if (End(next) || next == 0) break; at = next; }
        return result;
    }
    private List<ByteRange> Extents(IEnumerable<uint> values)
    {
        var ranges = new List<ByteRange>();
        foreach (uint value in values)
        {
            long address = Address(value);
            if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == address) ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + cluster };
            else ranges.Add(new(address, cluster));
        }
        return ranges;
    }
    private bool IsFree(uint value) => exfat ? bitmap is not null && (bitmap[(value - 2) / 8] & (1 << (int)((value - 2) % 8))) == 0 : Next(value) == 0;
    public IEnumerable<ByteRange> FreeRanges(CancellationToken token)
    {
        if (exfat && bitmap is null) { yield return new(offset, length); yield break; }
        long start = -1;
        for (uint i = 2; i < clusters + 2UL; i++)
        {
            token.ThrowIfCancellationRequested(); bool free = IsFree(i);
            if (free && start < 0) start = i;
            if (!free && start >= 0) { yield return new(Address((uint)start), (i - start) * cluster); start = -1; }
        }
        if (start >= 0) yield return new(Address((uint)start), (clusters + 2L - start) * cluster);
    }
    public bool HasAllocationMap => !exfat || bitmap is not null;
    public void Scan(string folder, Action<ScanProgress> progress, CancellationToken token, List<string> warnings, Action<RecoveryItem> found)
    {
        visitedDirectories.Clear(); int entries = 0, discovered = 0;
        var queue = new Queue<(uint First, long Size, bool Contiguous, string Path, bool Deleted)>();
        queue.Enqueue((rootCluster, 0, false, "", false));
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var dir = queue.Dequeue();
            if (dir.First != 0 && !visitedDirectories.Add(dir.First)) continue;
            if (visitedDirectories.Count > 100000 || entries > 2000000) { warnings.Add("Directory safety limit reached. Some records were not examined."); break; }
            List<ByteRange> ranges;
            if (dir.First == 0) ranges = [new(rootOffset, rootSize)];
            else if (dir.Contiguous && dir.Size > 0)
            {
                int count = (int)Math.Min((dir.Size + cluster - 1) / cluster, 16 * 1024 * 1024 / cluster);
                if (dir.First + (long)count - 1 > clusters + 1L) continue;
                ranges = Extents(Enumerable.Range(0, count).Select(i => dir.First + (uint)i));
            }
            else ranges = Extents(Chain(dir.First, Math.Max(1, 16 * 1024 * 1024 / cluster), token));
            // Keep physical addresses for each entry; directory chains may themselves be fragmented.
            var records = new List<(long Address, byte[] Bytes)>(); bool end = false;
            foreach (var range in ranges)
            {
                for (long p = range.Offset; p < range.Offset + range.Length; p += 32)
                {
                    token.ThrowIfCancellationRequested(); var bytes = reader.Bytes(p, 32);
                    if (bytes[0] == 0) { end = true; break; }
                    records.Add((p, bytes)); entries++;
                }
                if (end) break;
            }
            if (exfat && dir.Path.Length == 0)
            {
                int active = reader.Bytes(offset + 106, 2)[0] & 1;
                var allocation = records.FirstOrDefault(x => x.Bytes[0] == 0x81 && (x.Bytes[1] & 1) == active);
                if (allocation.Bytes is not null)
                {
                    long needed = (clusters + 7L) / 8; ulong declared = U64(allocation.Bytes, 24);
                    if (needed <= 64 * 1024 * 1024 && declared >= (ulong)needed)
                    {
                        var chain = Chain(U32(allocation.Bytes, 20), (int)((needed + cluster - 1) / cluster), token);
                        if (chain.Count * (long)cluster >= needed)
                        {
                            bitmap = new byte[(int)needed]; int copied = 0;
                            foreach (var range in Extents(chain)) { int n = (int)Math.Min(range.Length, needed - copied); reader.ReadExactly(range.Offset, bitmap.AsSpan(copied, n)); copied += n; if (copied == needed) break; }
                        }
                    }
                }
            }
            var lfn = new List<byte[]>();
            for (int index = 0; index < records.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var (entryAddress, b) = records[index];
                bool deleted; string name; uint first; long size, initialized; bool contiguous = false, directory; byte[] fingerprint = b;
                if (exfat)
                {
                    if (b[0] is not (0x05 or 0x85)) continue;
                    int secondary = b[1]; if (secondary is < 2 or > 18 || index + secondary >= records.Count) continue;
                    var set = new byte[(secondary + 1) * 32]; for (int j = 0; j <= secondary; j++) records[index + j].Bytes.CopyTo(set, j * 32);
                    index += secondary; deleted = b[0] == 5;
                    var stream = set.AsSpan(32, 32); if ((stream[0] & 0x7f) != 0x40) continue;
                    ushort expected = U16(set, 2); var checksumBytes = set.ToArray();
                    if (deleted) for (int j = 0; j <= secondary; j++) checksumBytes[j * 32] |= 0x80;
                    ushort sum = 0; for (int j = 0; j < checksumBytes.Length; j++) if (j is not (2 or 3)) sum = (ushort)(((sum << 15) | (sum >> 1)) + checksumBytes[j]);
                    if (sum != expected) { warnings.Add("An exFAT entry set with a failed checksum was skipped."); continue; }
                    int nameLength = stream[3]; var characters = new StringBuilder(); bool validName = true;
                    for (int j = 2; j <= secondary; j++)
                    {
                        if ((set[j * 32] & 0x7f) != 0x41) { validName = false; break; }
                        characters.Append(Encoding.Unicode.GetString(set, j * 32 + 2, 30));
                    }
                    if (!validName || nameLength == 0 || nameLength > characters.Length) continue;
                    name = characters.ToString(0, nameLength);
                    first = U32(set, 52); ulong dataSize = U64(set, 56), validSize = U64(set, 40);
                    if (dataSize > long.MaxValue || validSize > dataSize) continue;
                    size = (long)dataSize; initialized = (long)validSize; contiguous = (stream[1] & 2) != 0;
                    directory = (U16(b, 4) & 0x10) != 0;
                }
                else
                {
                    if (b[11] == 0x0f) { lfn.Add(b); continue; }
                    if ((b[11] & 8) != 0) { lfn.Clear(); continue; }
                    deleted = b[0] == 0xe5; directory = (b[11] & 0x10) != 0;
                    name = ShortName(b, deleted);
                    if (lfn.Count > 0 && lfn.Count <= 20 && lfn.All(x => (x[0] == 0xe5) == deleted && x[13] == lfn[0][13]))
                    {
                        string longName = string.Concat(lfn.AsEnumerable().Reverse().Select(LongName)).Split('\0')[0].TrimEnd('\uffff');
                        if (longName.Length > 0 && (deleted || ShortChecksum(b) == lfn[0][13])) name = longName;
                    }
                    lfn.Clear(); first = U16(b, 26); if (fatBits == 32) first |= (uint)U16(b, 20) << 16;
                    size = U32(b, 28); initialized = size;
                }
                if (name is "." or ".." || name.Length == 0 || name.IndexOfAny(['\\', '/', '\0']) >= 0) continue;
                string path = dir.Path + "\\" + name;
                if (directory)
                {
                    if (ValidCluster(first)) queue.Enqueue((first, size, contiguous, path, deleted || dir.Deleted));
                    continue;
                }
                if (!deleted && !dir.Deleted || !Matches(path, folder)) continue;
                int required = (int)Math.Min(size / cluster + (size % cluster == 0 ? 0 : 1), int.MaxValue);
                bool guessed = false, unsupported = size > 0 && (!ValidCluster(first) || required > clusters || size > length || required > 4000000);
                var chainValues = !unsupported && required > 0 ? Chain(first, required, token) : new List<uint>();
                if (!unsupported && required > 0 && (contiguous || chainValues.Count < required))
                {
                    if (!contiguous && exfat) unsupported = true;
                    else if (first + (long)required - 1 > clusters + 1L) unsupported = true;
                    else { chainValues = Enumerable.Range(0, required).Select(i => first + (uint)i).ToList(); guessed = !contiguous; }
                }
                bool free = !unsupported && chainValues.All(IsFree);
                var state = unsupported ? RecoveryState.Unsupported : guessed || !free ? RecoveryState.AtRisk : RecoveryState.Metadata;
                found(new()
                {
                    Name = name, OriginalPath = path, Size = size, InitializedSize = initialized, Method = FileSystem + " directory",
                    Extents = unsupported ? null : Extents(chainValues), EntryOffset = entryAddress, EntryFingerprint = fingerprint,
                    State = state, Note = unsupported ? "The deleted cluster chain is missing or invalid. Fragmented data cannot be safely mapped."
                        : guessed ? "Deletion cleared the FAT chain. A contiguous layout is inferred; fragmented files may be incomplete. Verify the recovered content."
                        : free ? "Directory metadata survived and mapped clusters are free. Original contents still require verification."
                        : "Some mapped clusters are allocated, or the allocation bitmap is unavailable. Bytes may have been reused."
                }); discovered++;
            }
            progress(new("Reading " + FileSystem + " directories", entries, 0, discovered, dir.Path.Length == 0 ? "Root directory" : dir.Path));
        }
        if (exfat && bitmap is null) warnings.Add("exFAT allocation bitmap could not be read. Deep signatures search all bytes and deletion status is unknown.");
    }
    private static bool Matches(string path, string folder)
    {
        if (folder.Length == 0) return true;
        string normalized = folder.Replace('/', '\\').TrimEnd('\\');
        int colon = normalized.IndexOf(':'); if (colon >= 0) normalized = normalized[(colon + 1)..];
        if (!normalized.StartsWith('\\')) normalized = "\\" + normalized;
        return normalized == "\\" || path.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase);
    }
    private static string ShortName(byte[] b, bool deleted)
    {
        string stem = Encoding.Latin1.GetString(b, 0, 8).TrimEnd(); string ext = Encoding.Latin1.GetString(b, 8, 3).TrimEnd();
        if (deleted) stem = "_" + stem[1..]; // The first character was erased; do not invent the original name.
        return stem + (ext.Length == 0 ? "" : "." + ext);
    }
    private static string LongName(byte[] b) => Encoding.Unicode.GetString(b, 1, 10) + Encoding.Unicode.GetString(b, 14, 12) + Encoding.Unicode.GetString(b, 28, 4);
    private static byte ShortChecksum(byte[] b) { byte sum = 0; for (int i = 0; i < 11; i++) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b[i]); return sum; }
}
