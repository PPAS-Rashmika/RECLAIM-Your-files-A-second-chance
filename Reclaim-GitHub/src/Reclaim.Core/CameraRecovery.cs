using System.Buffers.Binary;
using System.Text;

namespace Reclaim.Core;

// Rebuilds independently recoverable fragmented ISO-BMFF clips (moof + mdat)
// using a compatible surviving initialization segment. It does not guess the
// order of arbitrary GoPro/DJI clusters after their allocation table is lost.
public sealed class CameraRecovery(DiskReader reader, CancellationToken token)
{
    public sealed record Initialization(byte[] Bytes, HashSet<uint> Tracks)
    {
        public Dictionary<uint, uint> DefaultSizes { get; init; } = [];
    }
    private static uint U32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32BigEndian(b);
    private static IEnumerable<(string Type, int Start, int Length, int Header)> Boxes(byte[] bytes, int start, int end)
    {
        for (int p = start; p + 8 <= end;)
        {
            long size = U32(bytes.AsSpan(p)); int header = 8;
            if (size == 1) { if (p + 16 > end) yield break; ulong s = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(p + 8)); if (s > int.MaxValue) yield break; size = (long)s; header = 16; }
            if (size < header || size > end - p) yield break;
            yield return (Encoding.ASCII.GetString(bytes, p + 4, 4), p, (int)size, header); p += (int)size;
        }
    }
    public static Initialization LoadInitialization(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 16L * 1024 * 1024 * 1024) throw new InvalidDataException("Choose a reference clip smaller than 16 GB.");
        byte[]? ftyp = null, moov = null; var h = new byte[8];
        while (input.Position + 8 <= input.Length)
        {
            long start = input.Position; input.ReadExactly(h); long size = U32(h); string type = Encoding.ASCII.GetString(h, 4, 4);
            if (size < 8 || size > input.Length - start) break;
            if (type is "ftyp" or "moov")
            {
                if (size > 16 * 1024 * 1024) throw new InvalidDataException("Reference metadata exceeds 16 MB.");
                var box = new byte[(int)size]; h.CopyTo(box, 0); input.ReadExactly(box.AsSpan(8));
                if (type == "ftyp") ftyp = box; else moov = box;
            }
            input.Position = start + size;
            if (ftyp is not null && moov is not null) return ParseInitialization(ftyp, moov);
        }
        throw new InvalidDataException("The reference has no usable ftyp/moov initialization metadata.");
    }
    public static Initialization ParseInitialization(byte[] ftyp, byte[] moov)
    {
        if (ftyp.Length < 16 || ftyp.Length > 4096 || !ftyp.AsSpan(4, 4).SequenceEqual("ftyp"u8) || moov.Length < 16 || !moov.AsSpan(4, 4).SequenceEqual("moov"u8))
            throw new InvalidDataException("Invalid camera initialization metadata.");
        var tracks = new HashSet<uint>(); var sizes = new Dictionary<uint, uint>();
        foreach (var box in Boxes(moov, 8, moov.Length).Where(x => x.Type == "mvex"))
            foreach (var child in Boxes(moov, box.Start + box.Header, box.Start + box.Length).Where(x => x.Type == "trex" && x.Length >= x.Header + 24))
            {
                uint track = U32(moov.AsSpan(child.Start + child.Header + 4)); tracks.Add(track);
                sizes[track] = U32(moov.AsSpan(child.Start + child.Header + 16));
            }
        if (tracks.Count == 0 || tracks.Contains(0)) throw new InvalidDataException("This is a conventional MP4, not an fMP4 initialization clip. Choose a compatible fragmented MP4 reference; arbitrary camera fragmentation is not supported.");
        return new(ftyp.Concat(moov).ToArray(), tracks) { DefaultSizes = sizes };
    }
    public void Scan(IEnumerable<ByteRange> ranges, Action<ScanProgress> progress, Action<RecoveryItem> found, List<string> warnings, Initialization? reference = null)
    {
        var seen = new HashSet<long>(); byte[] buffer = new byte[4 * 1024 * 1024 + 32];
        long done = 0, total = ranges.Sum(x => x.Length); int count = 0; Initialization? initialization = reference;
        foreach (var range in ranges)
        {
            long end = checked(range.Offset + range.Length);
            for (long start = range.Offset; start < end;)
            {
                token.ThrowIfCancellationRequested(); int step = (int)Math.Min(4 * 1024 * 1024, end - start), n = (int)Math.Min(buffer.Length, end - start);
                reader.ReadExactly(start, buffer.AsSpan(0, n));
                var hits = new SortedSet<int>();
                foreach (var magic in new[] { "ftyp"u8.ToArray(), "moof"u8.ToArray() })
                    for (int cursor = 4; cursor + 4 <= n;)
                    {
                        int hit = buffer.AsSpan(cursor, n - cursor).IndexOf(magic); if (hit < 0) break;
                        int position = cursor + hit; if (position < step + 4) hits.Add(position); cursor = position + 1;
                    }
                foreach (int p in hits)
                {
                    bool ftyp = buffer.AsSpan(p, 4).SequenceEqual("ftyp"u8);
                    if (!ftyp && !buffer.AsSpan(p, 4).SequenceEqual("moof"u8)) continue;
                    long at = start + p - 4; if (!seen.Add(at)) continue;
                    try
                    {
                        long size = U32(buffer.AsSpan(p - 4));
                        if (ftyp)
                        {
                            if (reference is not null || size < 16 || size > 4096) continue;
                            // Only an adjacent surviving moov is used automatically.
                            if (at + size + 8 > end) continue;
                            byte[] next = reader.Bytes(at + size, 8); long m = U32(next);
                            if (!next.AsSpan(4, 4).SequenceEqual("moov"u8) || m < 16 || m > 16 * 1024 * 1024 || m > end - at - size) continue;
                            initialization = ParseInitialization(reader.Bytes(at, (int)size), reader.Bytes(at + size, (int)m)); continue;
                        }
                        if (initialization is null || size < 16 || size > 16 * 1024 * 1024 || size + 8 > end - at) continue;
                        byte[] moof = reader.Bytes(at, (int)size); byte[] md = reader.Bytes(at + size, 8);
                        if (!md.AsSpan(4, 4).SequenceEqual("mdat"u8)) continue;
                        long media = U32(md); int header = 8;
                        if (media == 1) { if (end - at - size < 16) continue; ulong large = BinaryPrimitives.ReadUInt64BigEndian(reader.Bytes(at + size + 8, 8)); if (large > long.MaxValue) continue; media = (long)large; header = 16; }
                        if (media < header || media > end - at - size || media > 16L * 1024 * 1024 * 1024) continue;
                        if (!ValidateFragment(moof, media, header, initialization)) continue;
                        long length = checked(initialization.Bytes.Length + size + media);
                        found(new RecoveryItem
                        {
                            Name = $"camera_fragment_{at:X12}.mp4", OriginalPath = "Camera fragments", Size = length, InitializedSize = length,
                            Method = "Camera fMP4 reconstruction", State = RecoveryState.AtRisk,
                            Pieces = [new(initialization.Bytes.Length, Bytes: initialization.Bytes), new(size + media, at)],
                            Note = "Rebuilt one fMP4 fragment with a compatible track initialization. Original camera/name/deletion status are unknown. Preview or validate playback; fragments are not automatically joined across recordings."
                        }); count++;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentException) { }
                }
                start += step; done += step; progress(new("Reconstructing camera fragments", done, total, count, $"{count:N0} fMP4 clips · source unchanged"));
            }
        }
        warnings.Add("Camera mode reconstructs fMP4 moof/mdat clips with surviving or supplied compatible initialization metadata. Conventional fragmented GoPro/DJI cluster reconstruction and missing codec headers are not inferred.");
        if (initialization is null) warnings.Add("No fMP4 initialization metadata was found. A compatible reference clip can be selected when starting Camera mode.");
    }
    private static bool ValidateFragment(byte[] moof, long media, int mediaHeader, Initialization initialization)
    {
        bool hasTrack = false;
        foreach (var traf in Boxes(moof, 8, moof.Length).Where(x => x.Type == "traf"))
        {
            var children = Boxes(moof, traf.Start + traf.Header, traf.Start + traf.Length).ToList();
            var tfhd = children.FirstOrDefault(x => x.Type == "tfhd"); if (tfhd.Length < tfhd.Header + 8) return false;
            int q = tfhd.Start + tfhd.Header; uint flags = U32(moof.AsSpan(q)) & 0xffffff;
            uint track = U32(moof.AsSpan(q + 4));
            if ((flags & 0x020000) == 0 || (flags & 1) != 0 || !initialization.Tracks.Contains(track)) return false;
            int next = q + 8 + ((flags & 2) != 0 ? 4 : 0) + ((flags & 8) != 0 ? 4 : 0);
            uint defaultSize = initialization.DefaultSizes.GetValueOrDefault(track);
            if ((flags & 16) != 0) { if (next + 4 > tfhd.Start + tfhd.Length) return false; defaultSize = U32(moof.AsSpan(next)); }
            bool hasRun = false;
            foreach (var trun in children.Where(x => x.Type == "trun"))
            {
                int p = trun.Start + trun.Header;
                if (trun.Length < trun.Header + 12) return false;
                uint runFlags = U32(moof.AsSpan(p)) & 0xffffff, samples = U32(moof.AsSpan(p + 4));
                if ((runFlags & 1) == 0 || samples == 0 || samples > 1_000_000) return false;
                long address = BinaryPrimitives.ReadInt32BigEndian(moof.AsSpan(p + 8));
                if (address < moof.Length + mediaHeader || address >= moof.Length + media) return false;
                int fields = ((runFlags & 0x100) != 0 ? 4 : 0) + ((runFlags & 0x200) != 0 ? 4 : 0) + ((runFlags & 0x400) != 0 ? 4 : 0) + ((runFlags & 0x800) != 0 ? 4 : 0);
                int sampleStart = p + 12 + ((runFlags & 4) != 0 ? 4 : 0);
                if (sampleStart + (long)samples * fields > trun.Start + trun.Length) return false;
                long bytes = 0;
                for (int sample = 0; sample < samples; sample++)
                {
                    uint sampleSize = (runFlags & 0x200) != 0 ? U32(moof.AsSpan(sampleStart + sample * fields + ((runFlags & 0x100) != 0 ? 4 : 0))) : defaultSize;
                    if (sampleSize == 0) return false; bytes = checked(bytes + sampleSize);
                }
                if (bytes > moof.Length + media - address) return false;
                hasRun = true;
            }
            if (!hasRun) return false; hasTrack = true;
        }
        return hasTrack;
    }
}
