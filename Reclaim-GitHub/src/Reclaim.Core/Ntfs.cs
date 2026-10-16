using System.Buffers.Binary;
using System.Text;

namespace Reclaim.Core;

public sealed record NtfsGeometry(int SectorSize, int ClusterSize, int RecordSize, long MftCluster, long VolumeLength)
{
    public static NtfsGeometry? FromBoot(ReadOnlySpan<byte> boot)
    {
        if (boot.Length < 512 || !boot.Slice(3, 8).SequenceEqual("NTFS    "u8)) return null;
        int sector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        int sectors = boot[13];
        if (sector is < 512 or > 4096 || (sector & (sector - 1)) != 0 || sectors == 0 || (sectors & (sectors - 1)) != 0)
            throw new InvalidDataException("Invalid NTFS sector or cluster size.");
        int cluster = checked(sector * sectors);
        int encoded = (sbyte)boot[64];
        if (encoded == 0 || encoded < -20) throw new InvalidDataException("Invalid MFT record size.");
        int record = encoded < 0 ? 1 << -encoded : checked(encoded * cluster);
        if (record < sector || record > 1024 * 1024 || record % sector != 0) throw new InvalidDataException("Unsupported MFT record size.");
        long total = BinaryPrimitives.ReadInt64LittleEndian(boot[40..]);
        long mft = BinaryPrimitives.ReadInt64LittleEndian(boot[48..]);
        if (total <= 0 || mft < 0 || mft >= total / sectors) throw new InvalidDataException("Invalid NTFS geometry.");
        return new(sector, cluster, record, mft, checked(total * sector));
    }
}

public sealed record NtfsName(string Name, long Parent, ushort ParentSequence, byte Namespace, DateTime? Modified);
public sealed record NtfsData(long LowestVcn, long Size, long InitializedSize, ushort Flags, List<DataRun>? Runs, byte[]? Resident);
public sealed class NtfsRecord
{
    public long Number { get; init; }
    public ushort Sequence { get; init; }
    public bool InUse { get; init; }
    public bool Directory { get; init; }
    public long BaseRecord { get; init; }
    public ushort BaseSequence { get; init; }
    public List<NtfsName> Names { get; } = [];
    public List<NtfsData> Data { get; } = [];
    public byte[]? AttributeList { get; set; }
    public NtfsData? AttributeListData { get; set; }
    public bool HasAttributeList { get; set; }
}

public static class NtfsParser
{
    public static List<DataRun> DecodeRuns(ReadOnlySpan<byte> bytes, long firstVcn = 0)
    {
        List<DataRun> runs = [];
        long lcn = 0, vcn = firstVcn;
        int p = 0;
        bool terminated = false;
        while (p < bytes.Length)
        {
            byte header = bytes[p++];
            if (header == 0) { terminated = true; break; }
            int lengthBytes = header & 15, offsetBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || offsetBytes > 8 || p + lengthBytes + offsetBytes > bytes.Length)
                throw new InvalidDataException("Invalid NTFS data run.");
            ulong rawLength = 0;
            for (int i = 0; i < lengthBytes; i++) rawLength |= (ulong)bytes[p++] << (i * 8);
            if (rawLength == 0 || rawLength > long.MaxValue) throw new InvalidDataException("Invalid run length.");
            long count = (long)rawLength;
            long? physical = null;
            if (offsetBytes > 0)
            {
                ulong value = 0;
                byte high = bytes[p + offsetBytes - 1];
                for (int i = 0; i < offsetBytes; i++) value |= (ulong)bytes[p++] << (i * 8);
                if (offsetBytes < 8 && (high & 128) != 0) value |= ulong.MaxValue << (offsetBytes * 8);
                lcn = checked(lcn + unchecked((long)value));
                if (lcn < 0) throw new InvalidDataException("Negative physical cluster.");
                physical = lcn;
            }
            runs.Add(new(vcn, count, physical)); vcn = checked(vcn + count);
            if (runs.Count > 100000) throw new InvalidDataException("Too many extents.");
        }
        if (!terminated) throw new InvalidDataException("Unterminated data runs.");
        return runs;
    }

    public static NtfsRecord? Parse(byte[] raw, int sectorSize, long number)
    {
        if (raw.Length < 48 || !raw.AsSpan(0, 4).SequenceEqual("FILE"u8)) return null;
        byte[] data = (byte[])raw.Clone();
        int usa = U16(data, 4), usaCount = U16(data, 6);
        if (usaCount != data.Length / sectorSize + 1 || usa < 8 || usa + usaCount * 2 > data.Length)
            throw new InvalidDataException("Invalid record fixup array.");
        ushort marker = U16(data, usa);
        for (int i = 1; i < usaCount; i++)
        {
            int tail = i * sectorSize - 2;
            if (U16(data, tail) != marker) throw new InvalidDataException("Torn or corrupt MFT record.");
            data[tail] = data[usa + i * 2]; data[tail + 1] = data[usa + i * 2 + 1];
        }
        ushort flags = U16(data, 22);
        ulong baseRef = U64(data, 32);
        var record = new NtfsRecord
        {
            Number = number, Sequence = U16(data, 16), InUse = (flags & 1) != 0, Directory = (flags & 2) != 0,
            BaseRecord = (long)(baseRef & 0x0000ffffffffffff), BaseSequence = (ushort)(baseRef >> 48)
        };
        int end = checked((int)U32(data, 24));
        int p = U16(data, 20);
        if (end > data.Length || p < 48 || p > end) throw new InvalidDataException("Invalid attribute bounds.");
        while (p + 8 <= end)
        {
            uint type = U32(data, p);
            if (type == uint.MaxValue) break;
            int length = checked((int)U32(data, p + 4));
            if (length < 24 || (length & 7) != 0 || length > end - p) throw new InvalidDataException("Invalid attribute length.");
            var attr = data.AsSpan(p, length);
            bool nonresident = attr[8] != 0;
            int nameLength = attr[9];
            ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(attr[12..]);
            byte[]? value = null;
            NtfsData? part = null;
            if (!nonresident)
            {
                int valueLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(attr[16..]));
                int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[20..]);
                if (valueOffset < 24 || valueOffset > length || valueLen > length - valueOffset) throw new InvalidDataException("Invalid resident attribute.");
                value = attr.Slice(valueOffset, valueLen).ToArray();
                if (type is 0x80 or 0x20) part = new(0, valueLen, valueLen, attrFlags, null, value);
            }
            else if (type is 0x80 or 0x20)
            {
                if (length < 64) throw new InvalidDataException("Invalid nonresident attribute.");
                long vcn = BinaryPrimitives.ReadInt64LittleEndian(attr[16..]);
                int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[32..]);
                if (vcn < 0 || runOffset < 64 || runOffset >= length) throw new InvalidDataException("Invalid run offset.");
                long size = BinaryPrimitives.ReadInt64LittleEndian(attr[48..]);
                long init = BinaryPrimitives.ReadInt64LittleEndian(attr[56..]);
                if (size < 0 || init < 0 || (vcn == 0 && init > size)) throw new InvalidDataException("Invalid stream size.");
                part = new(vcn, size, init, attrFlags, DecodeRuns(attr[runOffset..], vcn), null);
            }
            if (type == 0x30 && value is { Length: >= 66 })
            {
                int chars = value[64];
                if (66 + chars * 2 > value.Length) throw new InvalidDataException("Invalid filename.");
                ulong parent = U64(value, 0);
                DateTime? modified = null;
                try { modified = DateTime.FromFileTimeUtc(unchecked((long)U64(value, 16))); } catch (ArgumentOutOfRangeException) { }
                record.Names.Add(new(Encoding.Unicode.GetString(value, 66, chars * 2), (long)(parent & 0x0000ffffffffffff), (ushort)(parent >> 48), value[65], modified));
            }
            if (type == 0x80 && nameLength == 0 && part is not null) record.Data.Add(part);
            if (type == 0x20)
            {
                record.HasAttributeList = true; record.AttributeList = value; record.AttributeListData = part;
            }
            p += length;
        }
        return record;
    }
    public static List<(long Record, ushort Sequence)> DataReferences(byte[] list)
    {
        var refs = new List<(long, ushort)>(); int p = 0;
        while (p + 26 <= list.Length)
        {
            uint type = U32(list, p);
            if (type is 0 or uint.MaxValue) break;
            int size = U16(list, p + 4);
            if (size < 26 || size > list.Length - p) throw new InvalidDataException("Invalid attribute list entry.");
            if (type == 0x80 && list[p + 6] == 0)
            {
                ulong reference = U64(list, p + 16);
                refs.Add(((long)(reference & 0x0000ffffffffffff), (ushort)(reference >> 48)));
            }
            p += size;
        }
        return refs.Distinct().ToList();
    }
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
}

public sealed class NtfsStream(DiskReader reader, long volumeOffset, int clusterSize, List<DataRun> runs, long length)
{
    public long Length { get; } = length;
    public void Read(long offset, Span<byte> output)
    {
        if (offset < 0 || offset > Length || output.Length > Length - offset) throw new EndOfStreamException("Stream read outside logical size.");
        int done = 0;
        foreach (var run in runs.OrderBy(x => x.VirtualCluster))
        {
            long start = checked(run.VirtualCluster * clusterSize), bytes = checked(run.ClusterCount * clusterSize);
            long pos = offset + done;
            if (pos < start) throw new InvalidDataException("A stream extent is missing.");
            if (pos >= start + bytes) continue;
            int n = (int)Math.Min(output.Length - done, start + bytes - pos);
            if (run.PhysicalCluster is long lcn)
                reader.ReadExactly(checked(volumeOffset + lcn * clusterSize + pos - start), output.Slice(done, n));
            else output.Slice(done, n).Clear();
            done += n;
            if (done == output.Length) return;
        }
        if (done != output.Length) throw new InvalidDataException("A stream extent is missing.");
    }
}

public sealed class ClusterBitmap(NtfsStream stream, long clusterCount)
{
    private readonly byte[] page = new byte[65536];
    private long pageOffset = -1;
    private int count;
    public bool IsAllocated(long lcn)
    {
        if (lcn < 0 || lcn >= clusterCount) throw new InvalidDataException("Cluster outside the volume.");
        long pos = lcn / 8;
        if (pos >= stream.Length) throw new InvalidDataException("Truncated allocation bitmap.");
        if (pos < pageOffset || pos >= pageOffset + count)
        {
            pageOffset = pos / page.Length * page.Length;
            count = (int)Math.Min(page.Length, stream.Length - pageOffset);
            stream.Read(pageOffset, page.AsSpan(0, count));
        }
        return (page[pos - pageOffset] & (1 << (int)(lcn % 8))) != 0;
    }
    public IEnumerable<ByteRange> FreeRanges(long volumeOffset, int clusterSize, CancellationToken token)
    {
        long start = -1;
        for (long lcn = 0; lcn < clusterCount; lcn++)
        {
            if ((lcn & 65535) == 0) token.ThrowIfCancellationRequested();
            bool allocated = IsAllocated(lcn);
            if (!allocated && start < 0) start = lcn;
            if (allocated && start >= 0) { yield return new(volumeOffset + start * clusterSize, (lcn - start) * clusterSize); start = -1; }
        }
        if (start >= 0) yield return new(volumeOffset + start * clusterSize, (clusterCount - start) * clusterSize);
    }
}

public sealed class NtfsVolume
{
    private readonly DiskReader reader;
    public NtfsGeometry Geometry { get; }
    public long Offset { get; }
    private readonly NtfsStream mft;
    public ClusterBitmap? Bitmap { get; private set; }
    public NtfsVolume(DiskReader reader, long offset, NtfsGeometry geometry)
    {
        this.reader = reader; Offset = offset; Geometry = geometry;
        if (geometry.VolumeLength > reader.Length - offset) throw new InvalidDataException("This image does not contain the entire NTFS volume.");
        var root = NtfsParser.Parse(reader.Bytes(offset + geometry.MftCluster * geometry.ClusterSize, geometry.RecordSize), geometry.SectorSize, 0)
            ?? throw new InvalidDataException("The master file table is damaged.");
        var data = root.Data.FirstOrDefault(x => x.LowestVcn == 0 && x.Runs is not null)
            ?? throw new InvalidDataException("Cannot locate MFT data extents.");
        List<DataRun> runs = [.. data.Runs!];
        var bootstrap = new NtfsStream(reader, offset, geometry.ClusterSize, runs, data.Size);
        byte[]? list = ReadAttributeList(root);
        if (list is not null)
        {
            var pending = NtfsParser.DataReferences(list).Where(x => x.Record != 0).ToList();
            while (pending.Count > 0)
            {
                bool progress = false;
                foreach (var reference in pending.ToArray())
                {
                    try
                    {
                        var bytes = new byte[geometry.RecordSize]; bootstrap.Read(checked(reference.Record * geometry.RecordSize), bytes);
                        var extension = NtfsParser.Parse(bytes, geometry.SectorSize, reference.Record);
                        if (extension is null || extension.Sequence != reference.Sequence || extension.BaseRecord != 0 || extension.BaseSequence != root.Sequence)
                            throw new InvalidDataException("Invalid MFT extent reference.");
                        foreach (var part in extension.Data.Where(x => x.Runs is not null)) runs.AddRange(part.Runs!);
                        pending.Remove(reference); progress = true;
                    }
                    catch (InvalidDataException) { }
                }
                if (!progress) throw new InvalidDataException("Some MFT extents are inaccessible. Use deep signature scan on an image.");
            }
        }
        mft = new(reader, offset, geometry.ClusterSize, runs.DistinctBy(x => x.VirtualCluster).OrderBy(x => x.VirtualCluster).ToList(), data.Size);
        try
        {
            var bitmap = ReadRecord(6); var part = bitmap?.Data.FirstOrDefault(x => x.Runs is not null && x.LowestVcn == 0);
            if (part is not null) Bitmap = new(new NtfsStream(reader, offset, geometry.ClusterSize, part.Runs!, part.Size), geometry.VolumeLength / geometry.ClusterSize);
        }
        catch (Exception ex) when (ex is IOException or OverflowException) { }
    }
    public NtfsRecord? ReadRecord(long number)
    {
        var bytes = new byte[Geometry.RecordSize]; mft.Read(checked(number * Geometry.RecordSize), bytes);
        return NtfsParser.Parse(bytes, Geometry.SectorSize, number);
    }
    private byte[]? ReadAttributeList(NtfsRecord record)
    {
        if (record.AttributeList is not null) return record.AttributeList;
        if (record.AttributeListData is { Runs: not null } part)
        {
            if (part.Size > 4 * 1024 * 1024) throw new InvalidDataException("Attribute list is too large.");
            var bytes = new byte[(int)part.Size]; new NtfsStream(reader, Offset, Geometry.ClusterSize, part.Runs, part.Size).Read(0, bytes); return bytes;
        }
        return null;
    }

    public List<RecoveryItem> Scan(string folder, Action<ScanProgress> progress, CancellationToken token, List<string> warnings, Action<RecoveryItem>? onFound = null)
    {
        long count = mft.Length / Geometry.RecordSize;
        Dictionary<long, NtfsRecord> directories = [];
        Dictionary<long, NtfsRecord> extensions = [];
        List<NtfsRecord> deleted = [];
        int corrupt = 0;
        // Read MFT in batches; all file types are recognized through metadata, even without a signature.
        int recordsPerBatch = Math.Max(1, 4 * 1024 * 1024 / Geometry.RecordSize);
        for (long first = 0; first < count; first += recordsPerBatch)
        {
            token.ThrowIfCancellationRequested();
            int n = (int)Math.Min(recordsPerBatch, count - first);
            var bytes = new byte[n * Geometry.RecordSize]; mft.Read(first * Geometry.RecordSize, bytes);
            for (int i = 0; i < n; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                try
                {
                    var record = NtfsParser.Parse(bytes.AsSpan(i * Geometry.RecordSize, Geometry.RecordSize).ToArray(), Geometry.SectorSize, first + i);
                    if (record is null) continue;
                    if (record.BaseRecord != 0) { extensions[record.Number] = record; continue; }
                    if (record.Directory && record.Names.Count > 0) directories[record.Number] = record;
                    else if (!record.InUse && record.Number >= 16 && record.Names.Count > 0) deleted.Add(record);
                }
                catch (Exception ex) when (ex is IOException or OverflowException or ArgumentException) { corrupt++; }
            }
            progress(new("Reading deleted file records", first + n, count, deleted.Count, $"{first + n:N0} / {count:N0} records"));
        }
        if (corrupt > 0) warnings.Add($"Skipped {corrupt:N0} damaged MFT records.");
        if (Bitmap is null) warnings.Add("Allocation bitmap unavailable. File extents cannot be checked for reuse; results need review.");
        List<RecoveryItem> found = [];
        string filter = folder.Replace('/', '\\').Trim('\\');
        for (int index = 0; index < deleted.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var record = deleted[index];
            var names = record.Names.Where(x => x.Namespace != 2).ToList();
            if (names.Count == 0) names = record.Names;
            NtfsName? selected = null; string? path = null;
            foreach (var name in names)
            {
                string? parent = ResolveDirectory(name.Parent, name.ParentSequence, directories, []);
                string candidate = parent is null ? "Lost paths\\" + name.Name : (parent.Length == 0 ? name.Name : parent + "\\" + name.Name);
                if (filter.Length > 0 && (parent is null || !candidate.StartsWith(filter + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                selected = name; path = candidate; break;
            }
            if (selected is null) continue;
            string reason = "";
            List<NtfsData> parts = [.. record.Data];
            try
            {
                if (record.HasAttributeList)
                {
                    var list = ReadAttributeList(record) ?? throw new InvalidDataException("Missing attribute list.");
                    foreach (var reference in NtfsParser.DataReferences(list).Where(x => x.Record != record.Number))
                    {
                        if (!extensions.TryGetValue(reference.Record, out var ext) || ext.Sequence != reference.Sequence ||
                            ext.BaseRecord != record.Number || ext.BaseSequence != record.Sequence)
                            throw new InvalidDataException("Some file extents have been lost or reused.");
                        parts.AddRange(ext.Data);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OverflowException) { reason = ex.Message; }
            var main = parts.FirstOrDefault(x => x.LowestVcn == 0);
            if (main is null) reason = "The file's data attribute is missing.";
            if (parts.Any(x => (x.Flags & 0x4001) != 0)) reason = "NTFS compressed or EFS encrypted data needs a specialized recovery tool.";
            List<DataRun>? runs = main?.Runs is null ? null : parts.Where(x => x.Runs is not null).SelectMany(x => x.Runs!).DistinctBy(x => x.VirtualCluster).OrderBy(x => x.VirtualCluster).ToList();
            long size = main?.Size ?? 0;
            var state = main?.Resident is not null ? RecoveryState.Resident : RecoveryState.Metadata;
            string note = main?.Resident is not null ? "Contents preserved inside the deleted record." : "Clusters currently marked free. Contents may still have been overwritten or trimmed.";
            if (runs is not null && reason.Length == 0)
            {
                try
                {
                    ValidateRuns(runs, size, Geometry.ClusterSize, Geometry.VolumeLength);
                    if (Bitmap is null) { state = RecoveryState.AtRisk; note = "Cluster allocation could not be checked."; }
                    else
                    {
                        long allocated = 0, total = 0;
                        foreach (var run in runs.Where(x => x.PhysicalCluster is not null))
                            for (long i = 0; i < run.ClusterCount && (run.VirtualCluster + i) * Geometry.ClusterSize < main!.InitializedSize; i++)
                            {
                                if ((total++ & 65535) == 0) token.ThrowIfCancellationRequested();
                                if (Bitmap.IsAllocated(run.PhysicalCluster!.Value + i)) allocated++;
                            }
                        if (allocated > 0) { state = RecoveryState.AtRisk; note = $"{allocated:N0} data clusters are now allocated. Recovered contents may belong to another file."; }
                    }
                }
                catch (Exception ex) when (ex is IOException or OverflowException) { reason = ex.Message; }
            }
            if (reason.Length > 0) { state = RecoveryState.Unsupported; note = reason; }
            var item = new RecoveryItem
            {
                Name = selected.Name, OriginalPath = path!, Size = size, InitializedSize = main?.InitializedSize ?? size,
                Method = "NTFS record", State = state, Note = note, RecordNumber = record.Number, RecordSequence = record.Sequence,
                ResidentData = main?.Resident, Runs = runs, Modified = selected.Modified
            };
            found.Add(item); onFound?.Invoke(item);
            if ((index & 127) == 0) progress(new("Checking recoverable data", index + 1, deleted.Count, found.Count));
        }
        return found;
    }
    private static string? ResolveDirectory(long number, ushort sequence, Dictionary<long, NtfsRecord> dirs, HashSet<long> seen)
    {
        if (number == 5) return dirs.TryGetValue(5, out var root) && root.Sequence == sequence ? "" : null;
        if (seen.Count >= 128 || !seen.Add(number) || !dirs.TryGetValue(number, out var dir) || dir.Sequence != sequence) return null;
        var name = dir.Names.FirstOrDefault(x => x.Namespace != 2) ?? dir.Names.FirstOrDefault();
        if (name is null) return null;
        string? parent = ResolveDirectory(name.Parent, name.ParentSequence, dirs, seen);
        return parent is null ? null : (parent.Length == 0 ? name.Name : parent + "\\" + name.Name);
    }
    public static void ValidateRuns(List<DataRun> runs, long size, int cluster, long volumeLength)
    {
        long next = 0;
        foreach (var run in runs)
        {
            if (run.VirtualCluster != next || run.ClusterCount <= 0) throw new InvalidDataException("Missing or overlapping file extents.");
            next = checked(next + run.ClusterCount);
            if (run.PhysicalCluster is long lcn && (lcn < 0 || lcn > volumeLength / cluster - run.ClusterCount))
                throw new InvalidDataException("File extent outside the volume.");
        }
        if (checked(next * cluster) < size) throw new InvalidDataException("Not all file extents survived deletion.");
    }
}
